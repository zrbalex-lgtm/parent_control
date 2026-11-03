-- =====================================================================
-- More useful app names in reports:
--  * browsers are split by site taken from the window title ("Chrome: YouTube");
--  * Windows components, the guard's own windows and minutes without a window get clear names;
--  * drill-down: which window titles made up an app's time.
-- =====================================================================

set search_path = parent_control, extensions;

create or replace function fn_browser_name(p_exe text)
returns text
language sql immutable
set search_path = parent_control, extensions, pg_temp
as $$
  select case lower(coalesce(p_exe, ''))
           when 'chrome.exe'  then 'Chrome'
           when 'msedge.exe'  then 'Edge'
           when 'firefox.exe' then 'Firefox'
           when 'opera.exe'   then 'Opera'
           when 'brave.exe'   then 'Brave'
           when 'vivaldi.exe' then 'Vivaldi'
           when 'browser.exe' then 'Яндекс Браузер'
         end
$$;

-- Page title without the browser's own suffix ("… - Google Chrome", "… and 2 more pages - Microsoft Edge").
create or replace function fn_browser_page(p_title text)
returns text
language sql immutable
set search_path = parent_control, extensions, pg_temp
as $$
  select case when p ~* '^(Google Chrome|Mozilla Firefox|Opera|Brave|Vivaldi|Яндекс[ .]?Браузер|Yandex Browser|Microsoft\S*\s*Edge)$'
              then null else p end
    from (select nullif(btrim(regexp_replace(regexp_replace(regexp_replace(
                   coalesce(p_title, ''),
                   '\s+[-—–|]\s+(Google Chrome|Mozilla Firefox|Opera|Brave|Vivaldi|Яндекс[ .]?Браузер|Yandex Browser|Microsoft\S*\s*Edge)\s*$', '', 'i'),
                   '\s+(and|и) \d+ (more|других|ещё)\y.*$', '', 'i'),
                   -- unread counters like "(3) YouTube"
                   '^\(\d+\+?\)\s*', '')), '') as p) x
$$;

-- Site of a browser window: the last " - " / " — " / " | " part of the page title ("… - YouTube" -> "YouTube").
create or replace function fn_browser_site(p_exe text, p_title text)
returns text
language sql immutable
set search_path = parent_control, extensions, pg_temp
as $$
  select case when fn_browser_name(p_exe) is null or fn_browser_page(p_title) is null then null
              else left(btrim((regexp_split_to_array(fn_browser_page(p_title), '\s+[-—–|]\s+'))
                              [array_length(regexp_split_to_array(fn_browser_page(p_title), '\s+[-—–|]\s+'), 1)]), 40)
         end
$$;

create or replace function fn_app_label(p_exe text, p_product text, p_title text)
returns table (app text, grouped boolean)
language sql stable
set search_path = parent_control, extensions, pg_temp
as $$
  select coalesce(g,
           case
             -- no window info: the tray was not running or not reporting that minute
             when nullif(p_exe, '') is null then 'Нет данных об окне'
             when lower(p_exe) = 'parentcontrol.guard.exe' then 'Экранное время (блокировка, запросы)'
             when fn_browser_site(p_exe, p_title) is not null
               then fn_browser_name(p_exe) || ': ' || fn_browser_site(p_exe, p_title)
             when fn_browser_name(p_exe) is not null then fn_browser_name(p_exe)
             when coalesce(p_product, '') ilike 'Microsoft%Windows%Operating System' then
               case lower(p_exe)
                 when 'explorer.exe'             then 'Windows: проводник и рабочий стол'
                 when 'systemsettings.exe'       then 'Windows: параметры'
                 when 'applicationframehost.exe' then 'Windows: приложение'
                 when 'lockapp.exe'              then 'Windows: экран блокировки'
                 when 'searchhost.exe'           then 'Windows: поиск'
                 when 'shellexperiencehost.exe'  then 'Windows: меню и панель задач'
                 when 'startmenuexperiencehost.exe' then 'Windows: меню «Пуск»'
                 else 'Windows: ' || regexp_replace(p_exe, '\.exe$', '', 'i')
               end
             else coalesce(nullif(p_product, ''), p_exe)
           end), g is not null
  from (select fn_app_group(p_exe, p_product, p_title) as g) x
$$;

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
      select jsonb_agg(jsonb_build_object('app', a.app, 'grouped', a.grouped, 'minutes', a.minutes, 'exe', a.exe)
                       order by a.minutes desc)
        from (
          select l.app, bool_or(l.grouped) as grouped, count(*)::int as minutes,
                 mode() within group (order by h.exe_name) as exe
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

-- ---------------------------------------------------------------------
-- [admin] Window titles behind one app line of the history (top 20).
-- ---------------------------------------------------------------------
create or replace function admin_app_titles(p_child uuid, p_from date, p_to date, p_app text)
returns jsonb
language plpgsql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if not is_admin() or not fn_my_child(p_child) then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  if p_from is null or p_to is null or p_to < p_from or p_to - p_from > 62 then
    raise exception 'invalid range' using errcode = '22023';
  end if;
  return coalesce((
    select jsonb_agg(jsonb_build_object('title', t.title, 'exe', t.exe, 'minutes', t.minutes) order by t.minutes desc)
      from (
        select coalesce(fn_browser_page(h.window_title), nullif(h.window_title, ''), '(без заголовка)') as title,
               mode() within group (order by h.exe_name) as exe,
               count(*)::int as minutes
          from heartbeats h
          cross join lateral fn_app_label(h.exe_name, h.product_name, h.window_title) l
         where h.child_id = p_child and h.local_date between p_from and p_to
           and h.minute_ts >= p_from::timestamptz - interval '1 day'
           and h.minute_ts <  (p_to + 1)::timestamptz + interval '1 day'
           and h.state in ('active', 'idle')
           and l.app = p_app
         group by 1
         order by 3 desc
         limit 20) t), '[]'::jsonb);
end $$;

revoke execute on function fn_browser_name(text), fn_browser_page(text), fn_browser_site(text, text),
  admin_app_titles(uuid, date, date, text) from public, anon;
grant execute on function admin_app_titles(uuid, date, date, text) to authenticated;
