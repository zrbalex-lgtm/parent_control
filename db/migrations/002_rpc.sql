-- =====================================================================
-- RPC functions
--   admin_*  : called by the parent from the admin app (authenticated)
--   device_* : called by the Windows service / installer (anon key)
-- pgcrypto lives in schema "extensions" on Supabase, hence the search_path.
-- =====================================================================

set search_path = parent_control, extensions;

-- ---------------------------------------------------------------------
-- Internal helpers
-- ---------------------------------------------------------------------

-- Normalize a registration code typed by a human: "abcd-efgh" -> "ABCDEFGH".
create or replace function fn_norm_code(p_code text)
returns text
language sql immutable
set search_path = parent_control, extensions, pg_temp
as $$
  select upper(regexp_replace(coalesce(p_code, ''), '[^A-Za-z0-9]', '', 'g'))
$$;

-- 8-char one-time code formatted as XXXX-XXXX (no 0/O/1/I to avoid typos).
create or replace function fn_new_reg_code()
returns text
language plpgsql volatile
set search_path = parent_control, extensions, pg_temp
as $$
declare
  alphabet constant text := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';  -- 32 chars, so byte % 32 is uniform
  b bytea := gen_random_bytes(8);
  r text := '';
begin
  for i in 0..7 loop
    r := r || substr(alphabet, (get_byte(b, i) % 32) + 1, 1);
    if i = 3 then
      r := r || '-';
    end if;
  end loop;
  return r;
end $$;

-- Permanent device secret: 32 random bytes, base64url without padding.
create or replace function fn_new_secret()
returns text
language sql volatile
set search_path = parent_control, extensions, pg_temp
as $$
  select rtrim(translate(encode(gen_random_bytes(32), 'base64'), '+/', '-_'), '=')
$$;

-- Parse and sanitize a heartbeat batch. Invalid items are dropped instead
-- of failing the whole batch, so one bad item can't block the client queue.
create or replace function fn_parse_heartbeats(p jsonb)
returns table (
  minute_ts  timestamptz,
  local_date date,
  state      text,
  sid        text,
  usr        text,
  exe        text,
  product    text,
  title      text
)
language plpgsql stable
set search_path = parent_control, extensions, pg_temp
as $$
declare
  e jsonb;
begin
  if p is null or jsonb_typeof(p) <> 'array' then
    return;
  end if;
  for e in select * from jsonb_array_elements(p) loop
    begin
      minute_ts  := date_trunc('minute', (e->>'ts')::timestamptz);
      local_date := (e->>'local_date')::date;
      state      := e->>'state';
      sid        := left(nullif(e->>'sid', ''), 200);
      usr        := left(nullif(e->>'user', ''), 256);
      exe        := left(nullif(e->>'exe', ''), 260);
      product    := left(nullif(e->>'product', ''), 256);
      title      := left(nullif(e->>'title', ''), 512);
    exception when others then
      continue;  -- malformed timestamp or date
    end;
    if minute_ts is null or local_date is null
       or state not in ('active', 'idle', 'locked', 'no_user')
       or minute_ts < now() - interval '60 days'
       or minute_ts > now() + interval '1 day' then
      continue;
    end if;
    return next;
  end loop;
end $$;

-- Counted screen minutes of a child on a PC-local date, across all PCs.
create or replace function fn_spent_minutes(p_child uuid, p_date date)
returns int
language sql stable
set search_path = parent_control, extensions, pg_temp
as $$
  select count(distinct h.minute_ts)::int
  from heartbeats h
  where h.child_id = p_child
    and h.local_date = p_date
    and h.state in ('active', 'idle')
$$;

-- ---------------------------------------------------------------------
-- [admin] Create a device slot + one-time registration code (24h).
-- ---------------------------------------------------------------------
create or replace function admin_create_device(p_name text)
returns table (device_id uuid, code text)
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
#variable_conflict use_column
declare
  v_code text;
  v_id   uuid;
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  if coalesce(trim(p_name), '') = '' then
    raise exception 'device name is required' using errcode = '22023';
  end if;

  v_code := fn_new_reg_code();
  insert into devices (name, reg_code_hash, reg_code_expires_at)
  values (trim(p_name), digest(fn_norm_code(v_code), 'sha256'), now() + interval '24 hours')
  returning id into v_id;

  return query select v_id, v_code;
end $$;

-- ---------------------------------------------------------------------
-- [admin] New one-time code for an existing device (reinstall).
-- The old secret stops working immediately.
-- ---------------------------------------------------------------------
create or replace function admin_reset_device_code(p_device_id uuid)
returns text
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_code text;
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;

  v_code := fn_new_reg_code();
  update devices
     set reg_code_hash       = digest(fn_norm_code(v_code), 'sha256'),
         reg_code_expires_at = now() + interval '24 hours',
         secret_hash         = null,
         registered_at       = null
   where id = p_device_id;

  if not found then
    raise exception 'device not found' using errcode = 'P0002';
  end if;
  return v_code;
end $$;

-- ---------------------------------------------------------------------
-- [anon] Installer exchanges the one-time code for a permanent secret.
-- ---------------------------------------------------------------------
create or replace function device_register(
  p_code           text,
  p_machine_name   text,
  p_client_version text
)
returns table (device_id uuid, device_secret text)
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
#variable_conflict use_column
declare
  v_id     uuid;
  v_secret text;
begin
  select d.id into v_id
    from devices d
   where d.reg_code_hash = digest(fn_norm_code(p_code), 'sha256')
     and d.reg_code_expires_at > now()
   for update;

  if v_id is null then
    raise exception 'invalid or expired registration code' using errcode = '28000';
  end if;

  v_secret := fn_new_secret();
  update devices
     set secret_hash         = digest(v_secret, 'sha256'),
         reg_code_hash       = null,
         reg_code_expires_at = null,
         registered_at       = now(),
         machine_name        = left(p_machine_name, 100),
         client_version      = left(p_client_version, 50)
   where id = v_id;

  return query select v_id, v_secret;
end $$;

-- ---------------------------------------------------------------------
-- [anon] Main sync, called by the service once a minute.
-- Contract: see docs in 001/README. Returns jsonb.
-- A disabled device gets {"device_enabled": false} and must stop enforcing.
-- ---------------------------------------------------------------------
create or replace function device_sync(
  p_device_id      uuid,
  p_secret         text,
  p_client_version text,
  p_client_now     timestamptz,
  p_local_date     date,
  p_heartbeats     jsonb    default '[]'::jsonb,
  p_acked_commands bigint[] default '{}'
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
  return jsonb_build_object(
    'server_now',     now(),
    'device_enabled', true,
    'accepted',       v_accepted,
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
                            'spent_minutes', fn_spent_minutes(c.id, d.d))
                            order by d.d)
                     from (values (p_local_date), (p_local_date + 1)) d(d)
                     cross join lateral fn_effective_day(c.id, d.d) e))
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
          from commands c where c.id = any (v_msg_ids)), '[]'::jsonb)
  );
end $$;

-- ---------------------------------------------------------------------
-- [admin] Dashboard payload in one call.
-- p_local_date = parent's local "today" (same timezone as the PCs).
-- ---------------------------------------------------------------------
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
        cross join lateral fn_effective_day(c.id, p_local_date) e), '[]'::jsonb),
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
-- Grants. Supabase grants EXECUTE to public by default, so lock down
-- everything first, then open what each role needs.
-- ---------------------------------------------------------------------
revoke execute on function
  fn_norm_code(text),
  fn_new_reg_code(),
  fn_new_secret(),
  fn_parse_heartbeats(jsonb),
  fn_spent_minutes(uuid, date),
  admin_create_device(text),
  admin_reset_device_code(uuid),
  device_register(text, text, text),
  device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[]),
  admin_dashboard(date)
from public, anon, authenticated;

grant execute on function
  device_register(text, text, text),
  device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[])
to anon, authenticated;

grant execute on function
  admin_create_device(text),
  admin_reset_device_code(uuid),
  admin_dashboard(date),
  fn_spent_minutes(uuid, date)
to authenticated;
