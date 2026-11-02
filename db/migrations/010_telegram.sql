-- =====================================================================
-- Telegram bot: linked chats, request notifications with approve buttons,
-- /status. The bot runs as the Edge Function "telegram-bot" with the
-- service role; everything it may do goes through tg_* functions below.
-- =====================================================================

set search_path = parent_control, extensions;

-- pg_net lets triggers call the Edge Function (not available in local tests).
do $$
begin
  create extension if not exists pg_net;
exception when others then
  raise notice 'pg_net not available: %', sqlerrm;
end $$;

-- Time zone of the PCs: "today" for approvals coming from Telegram.
alter table settings add column if not exists timezone text not null default 'Europe/Zurich';

alter table time_requests add column if not exists decided_by_name text;

-- Single row: shared secret between the database, Telegram and the Edge Function.
create table tg_config (
  id           int primary key default 1 check (id = 1),
  secret       text not null default encode(gen_random_bytes(24), 'hex'),
  function_url text not null
);
insert into tg_config (function_url)
values ('https://opufwbpkuofxhfmlqhqq.supabase.co/functions/v1/telegram-bot');

-- Chats (private or group) allowed to use the bot.
create table tg_chats (
  chat_id   bigint primary key,
  title     text,
  linked_by text,
  linked_at timestamptz not null default now()
);

-- One-time codes to link a chat (valid 30 minutes).
create table tg_link_codes (
  code       text primary key,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null default now() + interval '30 minutes',
  used_at    timestamptz
);

-- Telegram messages showing a request, so all of them can be updated once it is decided.
create table tg_request_messages (
  request_id bigint not null references time_requests on delete cascade,
  chat_id    bigint not null,
  message_id bigint not null,
  primary key (request_id, chat_id, message_id)
);

alter table tg_config enable row level security;
alter table tg_chats enable row level security;
alter table tg_link_codes enable row level security;
alter table tg_request_messages enable row level security;
-- Admin manages chats; config, codes and message ids are internal.
create policy admin_all on tg_chats for all to authenticated using (is_admin()) with check (is_admin());
revoke all on tg_config, tg_link_codes, tg_request_messages from anon, authenticated;
revoke all on tg_chats from anon;
grant select, delete on tg_chats to authenticated;
grant all on tg_config, tg_chats, tg_link_codes, tg_request_messages to service_role;

create or replace function fn_local_today()
returns date
language sql stable
set search_path = parent_control, extensions, pg_temp
as $$
  select (now() at time zone coalesce((select timezone from settings where id = 1), 'Europe/Zurich'))::date
$$;

-- ---------------------------------------------------------------------
-- Decide a request (shared by the admin app and the bot).
-- ---------------------------------------------------------------------
create or replace function fn_decide_request(
  p_id bigint, p_approve boolean, p_minutes int, p_local_date date, p_by uuid, p_by_name text)
returns void
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  r time_requests%rowtype;
  v_minutes int;
  v_date date := coalesce(p_local_date, fn_local_today());
begin
  select * into r from time_requests where id = p_id for update;
  if not found then
    raise exception 'request not found' using errcode = 'P0002';
  end if;
  if r.status <> 'pending' then
    raise exception 'request already decided' using errcode = '22023';
  end if;

  if p_approve and r.kind = 'unlock' then
    insert into day_unlocks (child_id, local_date) values (r.child_id, v_date) on conflict do nothing;
    update time_requests
       set status = 'approved', decided_at = now(), decided_by = p_by, decided_by_name = p_by_name
     where id = p_id;
  elsif p_approve then
    v_minutes := coalesce(p_minutes, r.minutes);
    if v_minutes is null or v_minutes <= 0 or v_minutes > 600 then
      raise exception 'bad minutes' using errcode = '22023';
    end if;
    insert into bonuses (child_id, local_date, minutes, reason)
    values (r.child_id, v_date, v_minutes, 'Запрос' || coalesce(': ' || r.reason, ''));
    update time_requests
       set status = 'approved', granted_minutes = v_minutes, decided_at = now(),
           decided_by = p_by, decided_by_name = p_by_name
     where id = p_id;
  else
    update time_requests
       set status = 'denied', decided_at = now(), decided_by = p_by, decided_by_name = p_by_name
     where id = p_id;
  end if;
end $$;

create or replace function admin_decide_request(
  p_id bigint, p_approve boolean, p_minutes int default null, p_local_date date default null)
returns void
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not is_admin() then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  perform fn_decide_request(p_id, p_approve, p_minutes, p_local_date, auth.uid(), 'админка');
end $$;

-- ---------------------------------------------------------------------
-- Notify the bot (async HTTP via pg_net). Never fails the caller.
-- ---------------------------------------------------------------------
create or replace function fn_tg_notify(p_event text, p_request_id bigint)
returns void
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  c tg_config%rowtype;
begin
  if to_regproc('net.http_post') is null or not exists (select 1 from tg_chats) then
    return;
  end if;
  select * into c from tg_config where id = 1;
  perform net.http_post(
    url     := c.function_url,
    body    := jsonb_build_object('event', p_event, 'request_id', p_request_id),
    headers := jsonb_build_object('Content-Type', 'application/json', 'x-internal-secret', c.secret));
exception when others then
  raise warning 'telegram notify failed: %', sqlerrm;
end $$;

create or replace function trg_time_requests_tg()
returns trigger
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if tg_op = 'INSERT' and new.status = 'pending' then
    perform fn_tg_notify('request', new.id);
  elsif tg_op = 'UPDATE' and old.status = 'pending' and new.status <> 'pending' then
    perform fn_tg_notify('decided', new.id);
  end if;
  return null;
end $$;

create trigger time_requests_tg
  after insert or update of status on time_requests
  for each row execute function trg_time_requests_tg();

-- ---------------------------------------------------------------------
-- [admin] One-time code to link a Telegram chat.
-- ---------------------------------------------------------------------
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
  insert into tg_link_codes (code) values (v_code);
  return v_code;
end $$;

-- ---------------------------------------------------------------------
-- [bot] functions (service_role only)
-- ---------------------------------------------------------------------
create or replace function tg_link_chat(p_code text, p_chat_id bigint, p_title text, p_by text)
returns boolean
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  update tg_link_codes set used_at = now()
   where code = upper(btrim(p_code)) and used_at is null and expires_at > now();
  if not found then
    return false;
  end if;
  insert into tg_chats (chat_id, title, linked_by) values (p_chat_id, left(p_title, 200), left(p_by, 200))
  on conflict (chat_id) do update set title = excluded.title, linked_by = excluded.linked_by, linked_at = now();
  return true;
end $$;

-- Request as shown in Telegram.
create or replace function tg_request_info(p_id bigint)
returns jsonb
language sql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
  select jsonb_build_object(
           'id', r.id, 'kind', r.kind, 'status', r.status, 'minutes', r.minutes,
           'granted_minutes', r.granted_minutes, 'reason', r.reason,
           'child', c.name, 'device', d.name, 'created_at', r.created_at,
           'decided_by_name', r.decided_by_name,
           'spent', fn_spent_minutes(r.child_id, fn_local_today()),
           'allowed', (select case when e.limit_minutes is null then null
                                   else e.limit_minutes + e.bonus_minutes end
                         from fn_effective_day(r.child_id, fn_local_today()) e))
    from time_requests r
    join children c on c.id = r.child_id
    left join devices d on d.id = r.device_id
   where r.id = p_id
$$;

create or replace function tg_decide(p_chat_id bigint, p_id bigint, p_approve boolean, p_minutes int, p_by text)
returns jsonb
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not exists (select 1 from tg_chats where chat_id = p_chat_id) then
    raise exception 'chat not linked' using errcode = '42501';
  end if;
  perform fn_decide_request(p_id, p_approve, p_minutes, null, null, 'Telegram: ' || coalesce(p_by, '?'));
  return tg_request_info(p_id);
end $$;

-- Children right now, for /status.
create or replace function tg_status()
returns jsonb
language sql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
  select coalesce(jsonb_agg(jsonb_build_object(
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

create or replace function tg_pending_requests()
returns jsonb
language sql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
  select coalesce(jsonb_agg(tg_request_info(r.id) order by r.created_at), '[]'::jsonb)
    from time_requests r where r.status = 'pending'
$$;

revoke execute on function
  fn_local_today(), fn_decide_request(bigint, boolean, int, date, uuid, text),
  fn_tg_notify(text, bigint), admin_tg_link_code(),
  tg_link_chat(text, bigint, text, text), tg_request_info(bigint),
  tg_decide(bigint, bigint, boolean, int, text), tg_status(), tg_pending_requests()
from public, anon, authenticated;
grant execute on function admin_tg_link_code() to authenticated;
grant execute on function tg_link_chat(text, bigint, text, text), tg_request_info(bigint),
  tg_decide(bigint, bigint, boolean, int, text), tg_status(), tg_pending_requests() to service_role;
