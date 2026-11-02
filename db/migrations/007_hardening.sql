-- =====================================================================
-- Hardening: tamper events reported by PCs, optional lock after tampering,
-- "config lost" beacons, admin handling.
-- =====================================================================

set search_path = parent_control, extensions;

-- What to do when a PC reports tampering: lock its child accounts until the
-- admin clears it, or only notify.
alter table settings add column tamper_action text not null default 'lock'
  check (tamper_action in ('lock', 'notify'));

alter table devices add column tamper_locked_since timestamptz;
alter table devices add column config_state text not null default 'ok'
  check (config_state in ('ok', 'restored', 'lost'));

create table tamper_events (
  id           bigint generated always as identity primary key,
  client_id    uuid unique,                    -- generated on the PC; resends are idempotent
  device_id    uuid references devices on delete cascade,
  machine_name text,
  occurred_at  timestamptz not null,
  received_at  timestamptz not null default now(),
  type         text not null,
  details      jsonb,
  windows_user text,
  verified     boolean not null default true,  -- false for unauthenticated beacons
  handled_at   timestamptz,
  handled_by   uuid
);
create index on tamper_events (device_id, occurred_at desc);
create index tamper_events_unhandled on tamper_events (occurred_at desc) where handled_at is null;

alter table tamper_events enable row level security;
create policy admin_all on tamper_events for all to authenticated
  using (is_admin()) with check (is_admin());
revoke all on tamper_events from anon;
grant select, insert, update, delete on tamper_events to authenticated;
grant all on tamper_events to service_role;

-- Event types that put the PC into tamper lock (when tamper_action = 'lock').
create or replace function fn_is_lock_event(p_type text)
returns boolean
language sql immutable
set search_path = parent_control, extensions, pg_temp
as $$
  select p_type in ('config_missing', 'config_restored', 'data_tampered', 'acl_fixed',
                    'service_dacl_fixed', 'safeboot_fixed', 'watchdog_fixed', 'clock_changed')
$$;

-- ---------------------------------------------------------------------
-- [anon] PC reports tamper events. Items:
-- {"client_id": uuid, "occurred_at": ts, "type": "acl_fixed", "details": {...}, "user": "misha"}
-- ---------------------------------------------------------------------
create or replace function device_report(p_device_id uuid, p_secret text, p_events jsonb)
returns jsonb
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_dev      devices%rowtype;
  e          jsonb;
  v_client   uuid;
  v_at       timestamptz;
  v_type     text;
  v_accepted int := 0;
  v_lock     boolean := false;
  v_restored boolean := false;
  v_action   text;
begin
  select * into v_dev from devices where id = p_device_id;
  if not found
     or v_dev.secret_hash is null
     or v_dev.secret_hash <> digest(coalesce(p_secret, ''), 'sha256') then
    raise exception 'unauthorized' using errcode = '28000';
  end if;

  if jsonb_typeof(p_events) = 'array' then
    for e in select * from jsonb_array_elements(p_events) loop
      begin
        v_client := (e->>'client_id')::uuid;
        v_at     := coalesce((e->>'occurred_at')::timestamptz, now());
      exception when others then
        continue;
      end;
      v_type := e->>'type';
      if v_client is null or v_type is null or v_type !~ '^[a-z_]{2,40}$' then
        continue;
      end if;
      insert into tamper_events (client_id, device_id, machine_name, occurred_at, type, details, windows_user)
      values (v_client, p_device_id, v_dev.machine_name, least(v_at, now() + interval '1 day'), v_type,
              case when jsonb_typeof(e->'details') = 'object' then e->'details' end,
              left(nullif(e->>'user', ''), 256))
      on conflict (client_id) do nothing;
      if found then
        v_accepted := v_accepted + 1;
        v_lock := v_lock or fn_is_lock_event(v_type);
        v_restored := v_restored or v_type = 'config_restored';
      end if;
    end loop;
  end if;

  v_action := coalesce((select s.tamper_action from settings s where s.id = 1), 'lock');
  update devices
     set tamper_locked_since = case when v_lock and v_action = 'lock'
                                    then coalesce(tamper_locked_since, now()) else tamper_locked_since end,
         config_state = case when v_restored then 'restored' else config_state end
   where id = p_device_id
  returning * into v_dev;

  return jsonb_build_object('accepted', v_accepted, 'tamper_locked', v_dev.tamper_locked_since is not null);
end $$;

-- ---------------------------------------------------------------------
-- [anon] "My configuration is gone": sent by a PC that can no longer prove
-- who it is. Unverified; at most one per machine name per 10 minutes.
-- ---------------------------------------------------------------------
create or replace function device_beacon(p_machine_name text, p_reason text, p_device_id uuid default null)
returns void
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_name   text := left(coalesce(p_machine_name, ''), 100);
  v_device uuid;
begin
  if v_name = '' then return; end if;
  if exists (select 1 from tamper_events t
              where t.machine_name = v_name and not t.verified
                and t.received_at > now() - interval '10 minutes') then
    return;
  end if;

  -- Trust the id only if it matches the machine name we know for it.
  select d.id into v_device from devices d
   where (p_device_id is not null and d.id = p_device_id and d.machine_name = v_name)
      or (p_device_id is null and d.machine_name = v_name)
   limit 1;

  insert into tamper_events (device_id, machine_name, occurred_at, type, details, verified)
  values (v_device, v_name, now(), 'config_missing',
          jsonb_build_object('reason', left(coalesce(p_reason, ''), 200)), false);

  if v_device is not null then
    update devices set config_state = 'lost' where id = v_device;
  end if;
end $$;

create or replace function device_sync(
  p_device_id      uuid,
  p_secret         text,
  p_client_version text,
  p_client_now     timestamptz,
  p_local_date     date,
  p_heartbeats     jsonb    default '[]'::jsonb,
  p_acked_commands bigint[] default '{}',
  p_time_requests  jsonb    default '[]'::jsonb
)
returns jsonb
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_dev      devices%rowtype;
  v_accepted int := 0;
  v_lock_ids bigint[];
  v_msg_ids  bigint[];
begin
  -- 1. Authenticate the device
  select * into v_dev from devices where id = p_device_id;
  if not found
     or v_dev.secret_hash is null
     or v_dev.secret_hash <> digest(coalesce(p_secret, ''), 'sha256') then
    raise exception 'unauthorized' using errcode = '28000';
  end if;

  if p_local_date is null then
    raise exception 'p_local_date is required' using errcode = '22023';
  end if;

  update devices
     set last_seen_at   = now(),
         client_version = coalesce(left(p_client_version, 50), client_version),
         clock_skew_sec = case when p_client_now is null then clock_skew_sec
                               else extract(epoch from p_client_now - now())::int end
   where id = p_device_id;

  if not v_dev.is_enabled then
    return jsonb_build_object('server_now', now(), 'device_enabled', false);
  end if;

  -- 2. Register Windows accounts seen in this batch
  insert into device_accounts (device_id, windows_sid, windows_user, first_seen_at, last_seen_at)
  select p_device_id,
         h.sid,
         coalesce((array_agg(h.usr order by h.minute_ts desc) filter (where h.usr is not null))[1], h.sid),
         now(),
         max(h.minute_ts)
    from fn_parse_heartbeats(p_heartbeats) h
   where h.sid is not null
   group by h.sid
  on conflict (device_id, windows_sid) do update
     set windows_user = excluded.windows_user,
         last_seen_at = greatest(device_accounts.last_seen_at, excluded.last_seen_at);

  -- 3. Store heartbeats; child resolved from the current account binding
  insert into heartbeats (device_id, minute_ts, local_date, state, windows_sid, windows_user,
                          child_id, exe_name, product_name, window_title)
  select p_device_id, h.minute_ts, h.local_date, h.state, h.sid, h.usr,
         a.child_id, h.exe, h.product, h.title
    from fn_parse_heartbeats(p_heartbeats) h
    left join device_accounts a
           on a.device_id = p_device_id and a.windows_sid = h.sid
  on conflict (device_id, minute_ts) do nothing;
  get diagnostics v_accepted = row_count;

  -- 3b. Time requests from children on this PC (idempotent by client_id,
  --     at most one pending request per child)
  insert into time_requests (client_id, child_id, device_id, kind, minutes, reason)
  select distinct on (a.child_id)
         (r->>'client_id')::uuid, a.child_id, p_device_id,
         case when r->>'kind' = 'unlock' then 'unlock' else 'time' end,
         case when r->>'kind' = 'unlock' then 5 else least(greatest((r->>'minutes')::int, 5), 240) end,
         left(nullif(r->>'reason', ''), 200)
    from jsonb_array_elements(case when jsonb_typeof(p_time_requests) = 'array'
                                   then p_time_requests else '[]'::jsonb end) r
    join device_accounts a
      on a.device_id = p_device_id and a.windows_sid = r->>'sid' and a.child_id is not null
   where r->>'client_id' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
     and (r->>'kind' = 'unlock' or r->>'minutes' ~ '^[0-9]{1,4}$')
     and not exists (select 1 from time_requests t where t.child_id = a.child_id and t.status = 'pending')
   order by a.child_id
  on conflict (client_id) do nothing;

  -- 4. Acknowledge executed commands
  update command_deliveries
     set done_at = now()
   where device_id = p_device_id
     and command_id = any (coalesce(p_acked_commands, '{}'))
     and done_at is null;

  -- 5. Commands for children bound to this PC
  select coalesce(array_agg(c.id), '{}') into v_lock_ids
    from commands c
   where c.kind = 'lock'
     and c.cancelled_at is null
     and (c.device_id is null or c.device_id = p_device_id)
     and (c.lock_until is null or c.lock_until > now())
     and c.child_id in (select a.child_id from device_accounts a
                         where a.device_id = p_device_id and a.child_id is not null);

  select coalesce(array_agg(c.id), '{}') into v_msg_ids
    from commands c
   where c.kind = 'message'
     and c.cancelled_at is null
     and c.created_at > now() - interval '24 hours'
     and (c.device_id is null or c.device_id = p_device_id)
     and c.child_id in (select a.child_id from device_accounts a
                         where a.device_id = p_device_id and a.child_id is not null)
     and not exists (select 1 from command_deliveries d
                      where d.command_id = c.id and d.device_id = p_device_id
                        and d.done_at is not null);

  insert into command_deliveries (command_id, device_id)
  select unnest(v_lock_ids || v_msg_ids), p_device_id
  on conflict do nothing;

  -- 6. Response
  -- An authenticated sync proves the PC has its credentials again.
  update devices set config_state = 'ok' where id = p_device_id and config_state = 'lost';

  return jsonb_build_object(
    'server_now',     now(),
    'device_enabled', true,
    'accepted',       v_accepted,
    'tamper_locked',  v_dev.tamper_locked_since is not null,
    'tamper_action',  coalesce((select s.tamper_action from settings s where s.id = 1), 'lock'),
    'accounts', coalesce((
        select jsonb_agg(jsonb_build_object(
                 'sid', a.windows_sid, 'user', a.windows_user, 'child_id', a.child_id)
                 order by a.windows_user)
          from device_accounts a
         where a.device_id = p_device_id), '[]'::jsonb),
    'children', coalesce((
        select jsonb_agg(jsonb_build_object(
                 'child_id', c.id,
                 'name',     c.name,
                 'days', (
                   select jsonb_agg(jsonb_build_object(
                            'local_date',    d.d,
                            'limit_minutes', e.limit_minutes,
                            'bonus_minutes', e.bonus_minutes,
                            'window_from',   to_char(e.window_from, 'HH24:MI'),
                            'window_to',     to_char(e.window_to,   'HH24:MI'),
                            'spent_minutes', fn_spent_minutes(c.id, d.d),
                            'requires_unlock', x.requires_unlock,
                            'unlocked',        x.unlocked)
                            order by d.d)
                     from (values (p_local_date), (p_local_date + 1)) d(d)
                     cross join lateral fn_effective_day(c.id, d.d) e
                     cross join lateral fn_day_access(c.id, d.d) x))
                 order by c.sort_order, c.name)
          from children c
         where c.id in (select a.child_id from device_accounts a
                         where a.device_id = p_device_id and a.child_id is not null)),
        '[]'::jsonb),
    'locks', coalesce((
        select jsonb_agg(jsonb_build_object(
                 'id', c.id, 'child_id', c.child_id,
                 'lock_from', c.lock_from, 'lock_until', c.lock_until)
                 order by c.id)
          from commands c where c.id = any (v_lock_ids)), '[]'::jsonb),
    'messages', coalesce((
        select jsonb_agg(jsonb_build_object(
                 'id', c.id, 'child_id', c.child_id, 'text', c.message,
                 'created_at', c.created_at)
                 order by c.id)
          from commands c where c.id = any (v_msg_ids)), '[]'::jsonb),
    'requests', coalesce((
        select jsonb_agg(jsonb_build_object(
                 'id', t.id, 'client_id', t.client_id, 'child_id', t.child_id, 'kind', t.kind,
                 'minutes', t.minutes, 'granted_minutes', t.granted_minutes,
                 'status', t.status, 'created_at', t.created_at, 'decided_at', t.decided_at)
                 order by t.id)
          from time_requests t
         where (t.status = 'pending' or t.created_at > now() - interval '24 hours')
           and t.child_id in (select a.child_id from device_accounts a
                               where a.device_id = p_device_id and a.child_id is not null)), '[]'::jsonb)
  );
end $$;

create or replace function admin_dashboard(p_local_date date)
returns jsonb
language plpgsql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;

  return jsonb_build_object(
    'server_now', now(),
    'children', coalesce((
      select jsonb_agg(jsonb_build_object(
               'id',     c.id,
               'name',   c.name,
               'color',  c.color,
               'avatar', c.avatar,
               'limit_minutes', e.limit_minutes,
               'bonus_minutes', e.bonus_minutes,
               'window_from',   to_char(e.window_from, 'HH24:MI'),
               'window_to',     to_char(e.window_to,   'HH24:MI'),
               'spent_minutes', fn_spent_minutes(c.id, p_local_date),
               'requires_unlock', x.requires_unlock,
               'unlocked',        x.unlocked,
               'tamper_unhandled', (select count(*) from tamper_events t
                                     where t.handled_at is null
                                       and t.device_id in (select a.device_id from device_accounts a where a.child_id = c.id)),
               'tamper_locked', exists (select 1 from devices dv2
                                         where dv2.tamper_locked_since is not null
                                           and dv2.id in (select a.device_id from device_accounts a where a.child_id = c.id)),
               'now', (
                 -- most recent activity within the last 3 minutes
                 select jsonb_build_object(
                          'device_id',    n.device_id,
                          'device_name',  dv.name,
                          'state',        n.state,
                          'app_group',    n.app_group,
                          'exe',          n.exe_name,
                          'title',        n.window_title,
                          'minute_ts',    n.minute_ts)
                   from v_device_now n
                   join devices dv on dv.id = n.device_id
                  where n.child_id = c.id
                    and n.minute_ts > now() - interval '3 minutes'
                  order by n.minute_ts desc
                  limit 1),
               'locks', coalesce((
                 select jsonb_agg(jsonb_build_object(
                          'id', k.id, 'device_id', k.device_id,
                          'lock_from', k.lock_from, 'lock_until', k.lock_until)
                          order by k.lock_from)
                   from commands k
                  where k.child_id = c.id and k.kind = 'lock'
                    and k.cancelled_at is null
                    and (k.lock_until is null or k.lock_until > now())), '[]'::jsonb))
               order by c.sort_order, c.name)
        from children c
        cross join lateral fn_effective_day(c.id, p_local_date) e
        cross join lateral fn_day_access(c.id, p_local_date) x), '[]'::jsonb),
    'devices', coalesce((
      select jsonb_agg(jsonb_build_object(
               'id',             d.id,
               'name',           d.name,
               'machine_name',   d.machine_name,
               'is_enabled',     d.is_enabled,
               'registered',     d.secret_hash is not null,
               'client_version', d.client_version,
               'last_seen_at',   d.last_seen_at,
               'online',         coalesce(d.last_seen_at > now() - interval '2 minutes', false),
               'clock_skew_sec', d.clock_skew_sec,
               'config_state',   d.config_state,
               'tamper_locked_since', d.tamper_locked_since,
               'tamper_unhandled', (select count(*) from tamper_events t where t.device_id = d.id and t.handled_at is null),
               'flags', jsonb_build_object(
                          'clock_skew',         f.clock_skew,
                          'unbound_account',    f.unbound_account,
                          'long_silence',       f.long_silence,
                          'offline_minutes_7d', f.offline_minutes_7d))
               order by d.name)
        from devices d
        join v_device_flags f on f.device_id = d.id), '[]'::jsonb)
  );
end $$;

-- ---------------------------------------------------------------------
-- [admin] Clear the tamper lock (and the config state flag) of a PC.
-- ---------------------------------------------------------------------
create or replace function admin_clear_tamper_lock(p_device_id uuid)
returns void
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  update devices set tamper_locked_since = null, config_state = 'ok' where id = p_device_id;
end $$;

create or replace function admin_mark_tamper_handled(p_ids bigint[])
returns void
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  update tamper_events set handled_at = now(), handled_by = auth.uid()
   where id = any (p_ids) and handled_at is null;
end $$;

revoke execute on function
  fn_is_lock_event(text),
  device_report(uuid, text, jsonb),
  device_beacon(text, text, uuid),
  device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb),
  admin_dashboard(date),
  admin_clear_tamper_lock(uuid),
  admin_mark_tamper_handled(bigint[])
from public, anon, authenticated;
grant execute on function device_report(uuid, text, jsonb), device_beacon(text, text, uuid),
  device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb) to anon;
grant execute on function admin_dashboard(date), admin_clear_tamper_lock(uuid),
  admin_mark_tamper_handled(bigint[]) to authenticated;
