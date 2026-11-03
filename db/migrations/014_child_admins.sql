-- =====================================================================
-- Several adults, each with their own children.
-- child_admins says which adult manages which child (many-to-many).
-- Every child-scoped table, view and admin RPC now shows only "my" children;
-- a PC is visible to the adults of the children bound to it (and to whoever added it).
-- App groups, settings and the bot itself stay shared.
-- =====================================================================

set search_path = parent_control, extensions;

create table child_admins (
  child_id uuid not null references children on delete cascade,
  user_id  uuid not null references auth.users on delete cascade,
  added_at timestamptz not null default now(),
  primary key (child_id, user_id)
);
alter table child_admins enable row level security;
revoke all on child_admins from anon;
grant select on child_admins to authenticated;
grant all on child_admins to service_role;

-- Everyone who is an admin today keeps seeing every child.
insert into child_admins (child_id, user_id)
select c.id, a.user_id from children c cross join app_admins a
on conflict do nothing;

alter table devices add column if not exists created_by uuid references auth.users on delete set null;
-- Existing PCs belong to the only admin there is (if there is exactly one).
update devices set created_by = (select a.user_id from app_admins a limit 1)
 where created_by is null and (select count(*) from app_admins) = 1;

alter table tg_link_codes add column if not exists created_by uuid;
alter table tg_chats add column if not exists owner uuid references auth.users on delete set null;
-- Children this chat hears about; null = all children of the owner.
alter table tg_chats add column if not exists children uuid[];
update tg_chats set owner = (select a.user_id from app_admins a limit 1)
 where owner is null and (select count(*) from app_admins) = 1;

-- ---------------------------------------------------------------------
-- Scope helpers
-- ---------------------------------------------------------------------
create or replace function fn_my_child(p_child uuid)
returns boolean
language sql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
  select exists (select 1 from child_admins ca where ca.child_id = p_child and ca.user_id = auth.uid())
$$;

create or replace function fn_my_device(p_device uuid)
returns boolean
language sql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
  select exists (select 1 from devices d where d.id = p_device and d.created_by = auth.uid())
      or exists (select 1 from device_accounts a
                   join child_admins ca on ca.child_id = a.child_id and ca.user_id = auth.uid()
                  where a.device_id = p_device)
$$;

-- The adult who creates a child manages it.
create or replace function trg_children_owner()
returns trigger
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if auth.uid() is not null then
    insert into child_admins (child_id, user_id) values (new.id, auth.uid()) on conflict do nothing;
  end if;
  return null;
end $$;

create trigger children_owner after insert on children
  for each row execute function trg_children_owner();

-- ---------------------------------------------------------------------
-- Row level security: narrow the existing admin policies
-- ---------------------------------------------------------------------
create policy mine on child_admins for select to authenticated
  using (is_admin() and fn_my_child(child_id));

alter policy admin_all on children
  using (is_admin() and fn_my_child(id)) with check (is_admin());
alter policy admin_all on schedule_weekly
  using (is_admin() and fn_my_child(child_id)) with check (is_admin() and fn_my_child(child_id));
alter policy admin_all on schedule_overrides
  using (is_admin() and fn_my_child(child_id)) with check (is_admin() and fn_my_child(child_id));
alter policy admin_all on bonuses
  using (is_admin() and fn_my_child(child_id)) with check (is_admin() and fn_my_child(child_id));
alter policy admin_all on day_unlocks
  using (is_admin() and fn_my_child(child_id)) with check (is_admin() and fn_my_child(child_id));
alter policy admin_all on time_requests
  using (is_admin() and fn_my_child(child_id)) with check (is_admin() and fn_my_child(child_id));
alter policy admin_all on commands
  using (is_admin() and fn_my_child(child_id)) with check (is_admin() and fn_my_child(child_id));
alter policy admin_all on command_deliveries
  using (is_admin() and exists (select 1 from commands k where k.id = command_id and fn_my_child(k.child_id)))
  with check (is_admin() and exists (select 1 from commands k where k.id = command_id and fn_my_child(k.child_id)));
alter policy admin_all on devices
  using (is_admin() and fn_my_device(id)) with check (is_admin() and fn_my_device(id));
alter policy admin_all on device_accounts
  using (is_admin() and fn_my_device(device_id) and (child_id is null or fn_my_child(child_id)))
  with check (is_admin() and fn_my_device(device_id) and (child_id is null or fn_my_child(child_id)));
alter policy admin_all on heartbeats
  using (is_admin() and case when child_id is null then fn_my_device(device_id) else fn_my_child(child_id) end)
  with check (is_admin());
alter policy admin_all on tamper_events
  using (is_admin() and (device_id is null or fn_my_device(device_id)))
  with check (is_admin() and (device_id is null or fn_my_device(device_id)));
alter policy admin_all on tg_chats
  using (is_admin() and (owner is null or owner = auth.uid()))
  with check (is_admin() and (owner is null or owner = auth.uid()));
grant update (children) on tg_chats to authenticated;

-- ---------------------------------------------------------------------
-- Admin RPCs (security definer) filtered the same way
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
        cross join lateral fn_day_access(c.id, p_local_date) x
       where fn_my_child(c.id)), '[]'::jsonb),
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
        join v_device_flags f on f.device_id = d.id
       where fn_my_device(d.id)), '[]'::jsonb)
  );
end $$;

create or replace function admin_history(p_child uuid, p_from date, p_to date)
returns jsonb
language plpgsql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_from timestamptz;
  v_to   timestamptz;
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  if not fn_my_child(p_child) then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  if p_from is null or p_to is null or p_to < p_from or p_to - p_from > 62 then
    raise exception 'invalid range' using errcode = '22023';
  end if;
  -- Generous UTC bounds for minute_ts; local_date does the exact filtering.
  v_from := p_from::timestamptz - interval '1 day';
  v_to   := (p_to + 1)::timestamptz + interval '1 day';

  return jsonb_build_object(
    'days', coalesce((
      select jsonb_agg(jsonb_build_object(
               'date',          d.d,
               'minutes',       coalesce(u.screen, 0),
               'idle_minutes',  coalesce(u.idle, 0),
               'allowed',       case when e.limit_minutes is null then null
                                     else greatest(0, e.limit_minutes + e.bonus_minutes) end,
               'bonus_minutes', e.bonus_minutes)
               order by d.d)
        from generate_series(p_from, p_to, interval '1 day') g(day)
        cross join lateral (select g.day::date as d) d
        cross join lateral fn_effective_day(p_child, d.d) e
        left join lateral (
          select count(distinct h.minute_ts) filter (where h.state in ('active', 'idle')) as screen,
                 count(distinct h.minute_ts) filter (where h.state = 'idle')            as idle
            from heartbeats h
           where h.child_id = p_child and h.local_date = d.d
             and h.minute_ts >= v_from and h.minute_ts < v_to) u on true), '[]'::jsonb),

    'apps', coalesce((
      select jsonb_agg(jsonb_build_object('app', a.app, 'grouped', a.grouped, 'minutes', a.minutes)
                       order by a.minutes desc)
        from (
          select l.app, bool_or(l.grouped) as grouped, count(*)::int as minutes
            from heartbeats h
            cross join lateral fn_app_label(h.exe_name, h.product_name, h.window_title) l
           where h.child_id = p_child and h.local_date between p_from and p_to
             and h.minute_ts >= v_from and h.minute_ts < v_to
             and h.state in ('active', 'idle')
           group by l.app
           order by 3 desc
           limit 15) a), '[]'::jsonb),

    'sessions', coalesce((
      select jsonb_agg(jsonb_build_object(
               'device_id', s.device_id, 'device_name', dv.name,
               'started_at', s.started_at, 'ended_at', s.ended_at, 'minutes', s.minutes,
               'top_app', (
                 select l.app
                   from heartbeats h2
                   cross join lateral fn_app_label(h2.exe_name, h2.product_name, h2.window_title) l
                  where h2.device_id = s.device_id and h2.child_id = p_child
                    and h2.minute_ts >= s.started_at and h2.minute_ts < s.ended_at
                    and h2.state in ('active', 'idle')
                  group by l.app order by count(*) desc limit 1))
               order by s.started_at desc)
        from (
          -- runs of consecutive counted minutes (a gap of 2+ minutes starts a new session)
          select device_id, min(minute_ts) as started_at,
                 max(minute_ts) + interval '1 minute' as ended_at, count(*)::int as minutes
            from (
              select device_id, minute_ts,
                     minute_ts - (row_number() over (partition by device_id order by minute_ts))
                                 * interval '1 minute' as grp
                from (select distinct device_id, minute_ts from heartbeats
                       where child_id = p_child and local_date between p_from and p_to
                         and minute_ts >= v_from and minute_ts < v_to
                         and state in ('active', 'idle')) m) x
           group by device_id, grp
           order by min(minute_ts) desc
           limit 60) s
        left join devices dv on dv.id = s.device_id), '[]'::jsonb)
  );
end $$;

create or replace function admin_ungrouped(p_days int default 7)
returns jsonb
language plpgsql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  return coalesce((
    select jsonb_agg(jsonb_build_object(
             'exe', u.exe, 'product', u.product, 'title', u.title, 'minutes', u.minutes)
             order by u.minutes desc)
      from (
        select h.exe_name as exe, nullif(h.product_name, '') as product,
               mode() within group (order by h.window_title) as title,
               count(*)::int as minutes
          from heartbeats h
         where h.child_id is not null and fn_my_child(h.child_id)
           and h.minute_ts > now() - make_interval(days => least(greatest(coalesce(p_days, 7), 1), 31))
           and h.state in ('active', 'idle')
           and h.exe_name is not null
           and fn_app_group(h.exe_name, h.product_name, h.window_title) is null
         group by 1, 2
         order by 4 desc
         limit 30) u), '[]'::jsonb);
end $$;

create or replace function admin_preview_app_rule(p_field text, p_type text, p_pattern text)
returns jsonb
language plpgsql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_result jsonb;
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  if p_field not in ('exe', 'product', 'title') or p_type not in ('contains', 'regex')
     or coalesce(btrim(p_pattern), '') = '' then
    raise exception 'invalid rule' using errcode = '22023';
  end if;
  if p_type = 'regex' then
    begin
      perform '' ~* p_pattern;
    exception when invalid_regular_expression then
      raise exception 'invalid regex' using errcode = '22023';
    end;
  end if;

  select jsonb_build_object(
           'minutes', coalesce(sum(x.minutes), 0),
           'samples', coalesce(jsonb_agg(jsonb_build_object('exe', x.exe, 'product', x.product,
                                                            'title', x.title, 'minutes', x.minutes)
                                         order by x.minutes desc)
                               filter (where x.rn <= 5), '[]'::jsonb))
    into v_result
    from (
      select h.exe_name as exe, nullif(h.product_name, '') as product,
             mode() within group (order by h.window_title) as title,
             count(*)::int as minutes,
             row_number() over (order by count(*) desc) as rn
        from heartbeats h
       where h.child_id is not null and fn_my_child(h.child_id)
         and h.minute_ts > now() - interval '7 days'
         and h.state in ('active', 'idle')
         and fn_rule_matches(p_field, p_type, p_pattern, h.exe_name, h.product_name, h.window_title)
       group by 1, 2) x;
  return v_result;
end $$;

create or replace function admin_decide_request(
  p_id bigint, p_approve boolean, p_minutes int default null, p_local_date date default null)
returns void
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not is_admin() or not exists (select 1 from time_requests r where r.id = p_id and fn_my_child(r.child_id)) then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  perform fn_decide_request(p_id, p_approve, p_minutes, p_local_date, auth.uid(), 'админка');
end $$;

create or replace function admin_clear_tamper_lock(p_device_id uuid)
returns void
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not is_admin() or not fn_my_device(p_device_id) then
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
   where id = any (p_ids) and handled_at is null
     and (device_id is null or fn_my_device(device_id));
end $$;

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
  insert into devices (name, reg_code_hash, reg_code_expires_at, created_by)
  values (trim(p_name), digest(fn_norm_code(v_code), 'sha256'), now() + interval '24 hours', auth.uid())
  returning id into v_id;

  return query select v_id, v_code;
end $$;

create or replace function admin_reset_device_code(p_device_id uuid)
returns text
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_code text;
begin
  if not is_admin() or not fn_my_device(p_device_id) then
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

create or replace function admin_tg_link_code()
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
  delete from tg_link_codes where expires_at < now() - interval '1 day';
  loop
    v_code := upper(substr(encode(gen_random_bytes(8), 'hex'), 1, 6));
    exit when not exists (select 1 from tg_link_codes where code = v_code);
  end loop;
  insert into tg_link_codes (code, created_by) values (v_code, auth.uid());
  return v_code;
end $$;

-- ---------------------------------------------------------------------
-- [admin] Adults
-- ---------------------------------------------------------------------
create or replace function admin_list_admins()
returns jsonb
language plpgsql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  return coalesce((
    select jsonb_agg(jsonb_build_object('user_id', a.user_id, 'email', u.email, 'is_me', a.user_id = auth.uid())
                     order by u.email)
      from app_admins a join auth.users u on u.id = a.user_id), '[]'::jsonb);
end $$;

-- Gives an existing Supabase user (created in the dashboard) access to this app.
create or replace function admin_add_admin(p_email text)
returns jsonb
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_user uuid;
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  select u.id into v_user from auth.users u where lower(u.email) = lower(btrim(p_email));
  if v_user is null then
    raise exception 'user not found' using errcode = 'P0002';
  end if;
  insert into app_admins (user_id) values (v_user) on conflict do nothing;
  return jsonb_build_object('user_id', v_user, 'email', lower(btrim(p_email)));
end $$;

create or replace function admin_child_admins(p_child uuid)
returns jsonb
language plpgsql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not is_admin() or not fn_my_child(p_child) then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  return coalesce((
    select jsonb_agg(jsonb_build_object('user_id', ca.user_id, 'email', u.email, 'is_me', ca.user_id = auth.uid())
                     order by u.email)
      from child_admins ca join auth.users u on u.id = ca.user_id
     where ca.child_id = p_child), '[]'::jsonb);
end $$;

-- Add or remove an adult for a child I manage; the last adult cannot be removed.
create or replace function admin_set_child_admin(p_child uuid, p_user uuid, p_on boolean)
returns void
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not is_admin() or not fn_my_child(p_child) then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  if p_on then
    if not exists (select 1 from app_admins a where a.user_id = p_user) then
      raise exception 'not an adult of this app' using errcode = '22023';
    end if;
    insert into child_admins (child_id, user_id) values (p_child, p_user) on conflict do nothing;
  else
    if (select count(*) from child_admins where child_id = p_child) <= 1 then
      raise exception 'last adult' using errcode = '22023';
    end if;
    delete from child_admins where child_id = p_child and user_id = p_user;
  end if;
end $$;

-- ---------------------------------------------------------------------
-- [bot] chats hear only about their children
-- ---------------------------------------------------------------------
create or replace function fn_chat_children(p_chat_id bigint)
returns setof uuid
language sql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
  select c.id
    from tg_chats t
    cross join children c
   where t.chat_id = p_chat_id
     and (t.owner is null or exists (select 1 from child_admins ca where ca.child_id = c.id and ca.user_id = t.owner))
     and (t.children is null or c.id = any (t.children))
$$;

create or replace function tg_link_chat(p_code text, p_chat_id bigint, p_title text, p_by text)
returns boolean
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_owner uuid;
begin
  update tg_link_codes set used_at = now()
   where code = upper(btrim(p_code)) and used_at is null and expires_at > now()
  returning created_by into v_owner;
  if not found then
    return false;
  end if;
  insert into tg_chats (chat_id, title, linked_by, owner) values (p_chat_id, left(p_title, 200), left(p_by, 200), v_owner)
  on conflict (chat_id) do update set title = excluded.title, linked_by = excluded.linked_by,
                                      owner = excluded.owner, children = null, linked_at = now();
  return true;
end $$;

create or replace function tg_chats_for_child(p_child uuid)
returns setof bigint
language sql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
  select t.chat_id from tg_chats t where p_child in (select fn_chat_children(t.chat_id))
$$;

create or replace function tg_chats_for_device(p_device uuid)
returns setof bigint
language sql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
  select distinct t.chat_id
    from tg_chats t
    join device_accounts a on a.device_id = p_device and a.child_id is not null
   where a.child_id in (select fn_chat_children(t.chat_id))
$$;

create or replace function tg_decide(p_chat_id bigint, p_id bigint, p_approve boolean, p_minutes int, p_by text)
returns jsonb
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not exists (select 1 from time_requests r
                  where r.id = p_id and r.child_id in (select fn_chat_children(p_chat_id))) then
    raise exception 'chat not linked' using errcode = '42501';
  end if;
  perform fn_decide_request(p_id, p_approve, p_minutes, null, null, 'Telegram: ' || coalesce(p_by, '?'));
  return tg_request_info(p_id);
end $$;

create or replace function tg_status()
returns jsonb
language sql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
  select coalesce(jsonb_agg(jsonb_build_object(
           'id', c.id,
           'name', c.name,
           'spent', fn_spent_minutes(c.id, t.d),
           'allowed', case when e.limit_minutes is null then null else e.limit_minutes + e.bonus_minutes end,
           'window_from', to_char(e.window_from, 'HH24:MI'),
           'window_to', to_char(e.window_to, 'HH24:MI'),
           'requires_unlock', x.requires_unlock,
           'unlocked', x.unlocked,
           'locked', exists (select 1 from commands k
                              where k.child_id = c.id and k.kind = 'lock' and k.cancelled_at is null
                                and k.lock_from <= now() and (k.lock_until is null or k.lock_until > now())),
           'tamper_locked', exists (select 1 from devices dv
                                     where dv.tamper_locked_since is not null
                                       and dv.id in (select a.device_id from device_accounts a where a.child_id = c.id)),
           'pending', (select count(*) from time_requests r where r.child_id = c.id and r.status = 'pending'),
           'now', (select jsonb_build_object('device', dv.name, 'state', n.state,
                                             'app', coalesce(n.app_group, nullif(n.product_name, ''), n.exe_name),
                                             'title', n.window_title)
                     from v_device_now n join devices dv on dv.id = n.device_id
                    where n.child_id = c.id and n.minute_ts > now() - interval '3 minutes'
                      and n.state in ('active', 'idle')
                    order by n.minute_ts desc limit 1))
           order by c.sort_order, c.name), '[]'::jsonb)
    from children c
    cross join (select fn_local_today() as d) t
    cross join lateral fn_effective_day(c.id, t.d) e
    cross join lateral fn_day_access(c.id, t.d) x
$$;

create or replace function tg_status(p_chat_id bigint)
returns jsonb
language sql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
  select coalesce(jsonb_agg(s.e order by s.ord), '[]'::jsonb)
    from (select e, ord from jsonb_array_elements(tg_status()) with ordinality x(e, ord)) s
   where (s.e->>'id')::uuid in (select fn_chat_children(p_chat_id))
$$;

create or replace function tg_pending_requests(p_chat_id bigint)
returns jsonb
language sql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
  select coalesce(jsonb_agg(tg_request_info(r.id) order by r.created_at), '[]'::jsonb)
    from time_requests r
   where r.status = 'pending' and r.child_id in (select fn_chat_children(p_chat_id))
$$;

revoke execute on function
  fn_my_child(uuid), fn_my_device(uuid), fn_chat_children(bigint),
  admin_list_admins(), admin_add_admin(text), admin_child_admins(uuid), admin_set_child_admin(uuid, uuid, boolean),
  tg_chats_for_child(uuid), tg_chats_for_device(uuid), tg_status(bigint), tg_pending_requests(bigint)
from public, anon;
grant execute on function fn_my_child(uuid), fn_my_device(uuid) to authenticated;
grant execute on function admin_list_admins(), admin_add_admin(text), admin_child_admins(uuid),
  admin_set_child_admin(uuid, uuid, boolean) to authenticated;
revoke execute on function tg_chats_for_child(uuid), tg_chats_for_device(uuid), tg_status(bigint),
  tg_pending_requests(bigint), fn_chat_children(bigint) from authenticated;
grant execute on function tg_chats_for_child(uuid), tg_chats_for_device(uuid), tg_status(bigint),
  tg_pending_requests(bigint) to service_role;
