-- =====================================================================
-- Home screen-time control: tables, views, RLS (Supabase / PostgreSQL)
-- RPC functions live in 002_rpc.sql.
-- =====================================================================

create extension if not exists pgcrypto;

-- ---------------------------------------------------------------------
-- Admin identity
-- Sign-ups must be DISABLED in Supabase Auth. In addition, only users
-- listed here get access, so an accidental sign-up gains nothing.
-- Insert the parent's auth.users id once via the SQL editor.
-- ---------------------------------------------------------------------
create table app_admins (
  user_id uuid primary key references auth.users on delete cascade
);

create or replace function is_admin()
returns boolean
language sql stable security definer set search_path = ''
as $$
  select exists (select 1 from public.app_admins where user_id = auth.uid())
$$;

-- ---------------------------------------------------------------------
-- Children
-- ---------------------------------------------------------------------
create table children (
  id          uuid primary key default gen_random_uuid(),
  name        text not null,
  color       text not null default '#4f8cff',
  avatar      text,                          -- emoji or short image URL
  sort_order  int  not null default 0,
  created_at  timestamptz not null default now()
);

-- ---------------------------------------------------------------------
-- Devices (PCs)
-- ---------------------------------------------------------------------
create table devices (
  id                  uuid primary key default gen_random_uuid(),
  name                text not null,             -- name given in admin UI
  machine_name        text,                      -- Windows computer name, reported at registration
  secret_hash         bytea,                     -- sha256(device secret); null until registered
  reg_code_hash       bytea,                     -- sha256(one-time code); cleared after use
  reg_code_expires_at timestamptz,
  registered_at       timestamptz,
  is_enabled          boolean not null default true,
  client_version      text,
  last_seen_at        timestamptz,
  clock_skew_sec      int,                       -- client clock minus server clock at last sync
  created_at          timestamptz not null default now()
);

-- Windows account on a given PC -> child. child_id null = not restricted
-- (e.g. parent accounts); rows are auto-created on first sight so the
-- admin UI can show unbound accounts.
create table device_accounts (
  id            uuid primary key default gen_random_uuid(),
  device_id     uuid not null references devices on delete cascade,
  windows_sid   text not null,
  windows_user  text not null,
  child_id      uuid references children on delete set null,
  first_seen_at timestamptz not null default now(),
  last_seen_at  timestamptz not null default now(),
  unique (device_id, windows_sid)
);

-- ---------------------------------------------------------------------
-- Schedule
-- limit_minutes null = no daily limit. Window is in PC local time,
-- [window_from, window_to), same day only; '24:00' is a valid end.
-- Missing weekly row = no limit, whole day allowed.
-- ---------------------------------------------------------------------
create table schedule_weekly (
  child_id      uuid not null references children on delete cascade,
  weekday       smallint not null check (weekday between 1 and 7),   -- ISO: 1 = Monday
  limit_minutes int check (limit_minutes >= 0),
  window_from   time not null default '00:00',
  window_to     time not null default '24:00',
  primary key (child_id, weekday),
  check (window_from < window_to)
);

-- Per-date exceptions. Null fields inherit from the weekly template.
create table schedule_overrides (
  child_id      uuid not null references children on delete cascade,
  local_date    date not null,
  limit_minutes int check (limit_minutes >= 0),
  window_from   time,
  window_to     time,
  note          text,
  primary key (child_id, local_date),
  check (window_from is null or window_to is null or window_from < window_to)
);

create table bonuses (
  id          bigint generated always as identity primary key,
  child_id    uuid not null references children on delete cascade,
  local_date  date not null,
  minutes     int  not null check (minutes <> 0),    -- negative = take time away
  reason      text,
  created_by  uuid default auth.uid(),
  created_at  timestamptz not null default now()
);
create index on bonuses (child_id, local_date);

-- ---------------------------------------------------------------------
-- Heartbeats: one row per device per minute (console session only).
-- child_id is resolved SERVER-side from device_accounts at insert time.
-- Idempotency: (device_id, minute_ts) is unique; resends are ignored.
-- ---------------------------------------------------------------------
create table heartbeats (
  id            bigint generated always as identity primary key,
  device_id     uuid not null references devices on delete cascade,
  minute_ts     timestamptz not null,               -- client time truncated to the minute (UTC)
  local_date    date not null,                      -- PC-local date the minute belongs to
  received_at   timestamptz not null default now(),
  state         text not null check (state in ('active','idle','locked','no_user')),
  windows_sid   text,
  windows_user  text,
  child_id      uuid references children on delete set null,
  exe_name      text,
  product_name  text,
  window_title  text,
  unique (device_id, minute_ts)
);
create index on heartbeats (child_id, local_date);
create index on heartbeats (device_id, minute_ts desc);

-- ---------------------------------------------------------------------
-- App groups for reports
-- ---------------------------------------------------------------------
create table app_groups (
  id          bigint generated always as identity primary key,
  group_name  text not null,
  match_field text not null check (match_field in ('exe','product','title')),
  match_type  text not null check (match_type in ('contains','regex')),
  pattern     text not null,
  priority    int  not null default 100,           -- lower wins
  is_enabled  boolean not null default true,
  created_at  timestamptz not null default now()
);

-- Reject invalid regexes up front so report views never fail at runtime.
create or replace function trg_app_groups_validate()
returns trigger language plpgsql
set search_path = public, extensions, pg_temp
as $$
begin
  if new.match_type = 'regex' then
    perform '' ~* new.pattern;
  end if;
  return new;
end $$;

create trigger app_groups_validate
  before insert or update on app_groups
  for each row execute function trg_app_groups_validate();

-- First matching group for a window, or null.
create or replace function fn_app_group(p_exe text, p_product text, p_title text)
returns text
language sql stable
set search_path = public, extensions, pg_temp
as $$
  select ag.group_name
  from app_groups ag
  cross join lateral (
    select case ag.match_field
             when 'exe'     then p_exe
             when 'product' then p_product
             else p_title
           end as v
  ) f
  where ag.is_enabled
    and f.v is not null
    and case ag.match_type
          when 'contains' then strpos(lower(f.v), lower(ag.pattern)) > 0
          else f.v ~* ag.pattern
        end
  order by ag.priority, ag.id
  limit 1
$$;

-- ---------------------------------------------------------------------
-- Commands from admin to PCs
-- Locks are stateful: a lock is active while now() in [lock_from, lock_until)
-- and not cancelled. lock_until null = until cancelled; the admin UI sets
-- lock_until to the next local midnight for "lock for today".
-- "Unlock" = set cancelled_at. Messages are one-shot (expire after 24h).
-- device_id null = every PC the child is bound to.
-- Status per device (pending / delivered / done) comes from command_deliveries.
-- ---------------------------------------------------------------------
create table commands (
  id           bigint generated always as identity primary key,
  child_id     uuid not null references children on delete cascade,
  device_id    uuid references devices on delete cascade,
  kind         text not null check (kind in ('lock','message')),
  lock_from    timestamptz,
  lock_until   timestamptz,
  message      text,
  created_at   timestamptz not null default now(),
  cancelled_at timestamptz,
  check (kind <> 'lock'    or lock_from is not null),
  check (kind <> 'message' or message   is not null),
  check (lock_until is null or lock_until > lock_from)
);
create index on commands (child_id) where cancelled_at is null;

create table command_deliveries (
  command_id   bigint not null references commands on delete cascade,
  device_id    uuid   not null references devices  on delete cascade,
  delivered_at timestamptz not null default now(),
  done_at      timestamptz,
  primary key (command_id, device_id)
);

-- ---------------------------------------------------------------------
-- Effective settings for a child on a PC-local date
-- ---------------------------------------------------------------------
create or replace function fn_effective_day(p_child uuid, p_date date)
returns table (limit_minutes int, window_from time, window_to time, bonus_minutes int)
language sql stable
set search_path = public, extensions, pg_temp
as $$
  select
    case when o.child_id is not null and o.limit_minutes is not null
         then o.limit_minutes else w.limit_minutes end,
    coalesce(o.window_from, w.window_from, time '00:00'),
    coalesce(o.window_to,   w.window_to,   time '24:00'),
    coalesce((select sum(b.minutes)::int from bonuses b
              where b.child_id = p_child and b.local_date = p_date), 0)
  from (select 1) x
  left join schedule_weekly w
         on w.child_id = p_child and w.weekday = extract(isodow from p_date)
  left join schedule_overrides o
         on o.child_id = p_child and o.local_date = p_date
$$;

-- ---------------------------------------------------------------------
-- Report views (security_invoker so RLS of the caller applies)
-- ---------------------------------------------------------------------

-- Screen time per child per day. Distinct minutes: simultaneous use of
-- two PCs counts once (see open question #2).
create view v_child_day_usage with (security_invoker = true) as
select
  child_id,
  local_date,
  count(distinct minute_ts) filter (where state in ('active','idle')) as screen_minutes,
  count(distinct minute_ts) filter (where state = 'idle')            as idle_minutes,
  min(minute_ts) as first_minute,
  max(minute_ts) as last_minute
from heartbeats
where child_id is not null
group by child_id, local_date;

-- Latest heartbeat per device: what is on screen right now.
create view v_device_now with (security_invoker = true) as
select distinct on (h.device_id)
  h.device_id, h.minute_ts, h.received_at, h.state,
  h.windows_user, h.child_id,
  h.exe_name, h.product_name, h.window_title,
  fn_app_group(h.exe_name, h.product_name, h.window_title) as app_group
from heartbeats h
order by h.device_id, h.minute_ts desc;

-- Sessions: runs of consecutive counted minutes per child and device.
create view v_sessions with (security_invoker = true) as
with m as (
  select distinct child_id, device_id, minute_ts
  from heartbeats
  where child_id is not null and state in ('active','idle')
), g as (
  select *,
         minute_ts - row_number() over (partition by child_id, device_id order by minute_ts)
                     * interval '1 minute' as grp
  from m
)
select child_id, device_id,
       min(minute_ts)                       as started_at,
       max(minute_ts) + interval '1 minute' as ended_at,
       count(*)::int                        as minutes
from g
group by child_id, device_id, grp;

-- App / group usage per child per day.
create view v_app_usage_day with (security_invoker = true) as
select
  h.child_id,
  h.local_date,
  coalesce(x.grp, nullif(h.product_name, ''), h.exe_name, '(no window)') as app,
  x.grp is not null as is_grouped,
  count(*)::int as minutes
from heartbeats h
cross join lateral (
  select fn_app_group(h.exe_name, h.product_name, h.window_title) as grp
) x
where h.child_id is not null and h.state in ('active','idle')
group by 1, 2, 3, 4;

-- Suspicious-activity flags per device.
create view v_device_flags with (security_invoker = true) as
select
  d.id as device_id,
  abs(coalesce(d.clock_skew_sec, 0)) > 300 as clock_skew,
  exists (select 1 from device_accounts a
          where a.device_id = d.id and a.child_id is null
            and a.last_seen_at > now() - interval '7 days')       as unbound_account,
  d.is_enabled and d.last_seen_at < now() - interval '24 hours'    as long_silence,
  -- minutes recorded while offline (uploaded late) in the last 7 days
  (select count(*) from heartbeats h
   where h.device_id = d.id
     and h.minute_ts > now() - interval '7 days'
     and h.received_at - h.minute_ts > interval '5 minutes')::int  as offline_minutes_7d
from devices d;

-- ---------------------------------------------------------------------
-- RLS: admin gets everything; devices have no table access at all.
-- ---------------------------------------------------------------------
do $$
declare t text;
begin
  foreach t in array array[
    'app_admins','children','devices','device_accounts','schedule_weekly',
    'schedule_overrides','bonuses','heartbeats','app_groups','commands',
    'command_deliveries']
  loop
    execute format('alter table %I enable row level security', t);
    if t <> 'app_admins' then
      execute format(
        'create policy admin_all on %I for all to authenticated
           using (is_admin()) with check (is_admin())', t);
    end if;
  end loop;
end $$;

-- Defense in depth: PCs talk only through RPC, so anon needs no table access.
revoke all on all tables in schema public from anon;
