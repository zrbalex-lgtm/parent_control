-- =====================================================================
-- Time requests: a child asks for more minutes from the PC, the admin
-- approves (adds a bonus) or denies. device_sync gets p_time_requests.
-- =====================================================================

set search_path = parent_control, extensions;

create table time_requests (
  id              bigint generated always as identity primary key,
  client_id       uuid not null unique,              -- generated on the PC; makes resends idempotent
  child_id        uuid not null references children on delete cascade,
  device_id       uuid references devices on delete set null,
  minutes         int  not null check (minutes between 5 and 240),
  reason          text,
  status          text not null default 'pending' check (status in ('pending','approved','denied')),
  granted_minutes int,
  created_at      timestamptz not null default now(),
  decided_at      timestamptz,
  decided_by      uuid
);
create unique index time_requests_one_pending on time_requests (child_id) where status = 'pending';
create index on time_requests (created_at desc);

alter table time_requests enable row level security;
create policy admin_all on time_requests for all to authenticated
  using (is_admin()) with check (is_admin());
revoke all on time_requests from anon;
grant select, insert, update, delete on time_requests to authenticated;
grant all on time_requests to service_role;

-- device_sync gains a parameter: drop the old signature so PostgREST
-- never sees two overloads.
drop function device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[]);

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
  insert into time_requests (client_id, child_id, device_id, minutes, reason)
  select distinct on (a.child_id)
         (r->>'client_id')::uuid, a.child_id, p_device_id,
         least(greatest((r->>'minutes')::int, 5), 240),
         left(nullif(r->>'reason', ''), 200)
    from jsonb_array_elements(case when jsonb_typeof(p_time_requests) = 'array'
                                   then p_time_requests else '[]'::jsonb end) r
    join device_accounts a
      on a.device_id = p_device_id and a.windows_sid = r->>'sid' and a.child_id is not null
   where r->>'client_id' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
     and r->>'minutes' ~ '^[0-9]{1,4}$'
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
          from commands c where c.id = any (v_msg_ids)), '[]'::jsonb),
    'requests', coalesce((
        select jsonb_agg(jsonb_build_object(
                 'id', t.id, 'client_id', t.client_id, 'child_id', t.child_id,
                 'minutes', t.minutes, 'granted_minutes', t.granted_minutes,
                 'status', t.status, 'created_at', t.created_at, 'decided_at', t.decided_at)
                 order by t.id)
          from time_requests t
         where (t.status = 'pending' or t.created_at > now() - interval '24 hours')
           and t.child_id in (select a.child_id from device_accounts a
                               where a.device_id = p_device_id and a.child_id is not null)), '[]'::jsonb)
  );
end $$;


-- ---------------------------------------------------------------------
-- [admin] Approve (adds a bonus for p_local_date) or deny a request.
-- ---------------------------------------------------------------------
create or replace function admin_decide_request(
  p_id         bigint,
  p_approve    boolean,
  p_minutes    int  default null,
  p_local_date date default null
)
returns void
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  r time_requests%rowtype;
  v_minutes int;
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;

  select * into r from time_requests where id = p_id for update;
  if not found then
    raise exception 'request not found' using errcode = 'P0002';
  end if;
  if r.status <> 'pending' then
    raise exception 'request already decided' using errcode = '22023';
  end if;

  if p_approve then
    v_minutes := coalesce(p_minutes, r.minutes);
    if v_minutes is null or v_minutes <= 0 or v_minutes > 600 then
      raise exception 'bad minutes' using errcode = '22023';
    end if;
    insert into bonuses (child_id, local_date, minutes, reason)
    values (r.child_id, coalesce(p_local_date, current_date), v_minutes,
            'Запрос' || coalesce(': ' || r.reason, ''));
    update time_requests
       set status = 'approved', granted_minutes = v_minutes, decided_at = now(), decided_by = auth.uid()
     where id = p_id;
  else
    update time_requests
       set status = 'denied', decided_at = now(), decided_by = auth.uid()
     where id = p_id;
  end if;
end $$;

revoke execute on function
  device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb),
  admin_decide_request(bigint, boolean, int, date)
from public, anon, authenticated;

grant execute on function device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb) to anon;
grant execute on function admin_decide_request(bigint, boolean, int, date) to authenticated;
