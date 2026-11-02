-- =====================================================================
-- Phase 2 reports: history per child, ungrouped apps, app rule preview.
-- All admin-only (security definer + is_admin()).
-- =====================================================================

set search_path = parent_control, extensions;

create index if not exists heartbeats_child_minute on heartbeats (child_id, minute_ts);

-- Does one window match a rule? Same semantics as fn_app_group.
create or replace function fn_rule_matches(p_field text, p_type text, p_pattern text,
                                           p_exe text, p_product text, p_title text)
returns boolean
language sql immutable
set search_path = parent_control, extensions, pg_temp
as $$
  select v is not null and case p_type
           when 'contains' then strpos(lower(v), lower(p_pattern)) > 0
           else v ~* p_pattern
         end
  from (select case p_field when 'exe' then p_exe when 'product' then p_product else p_title end as v) x
$$;

-- What an app is called in reports: its group, else product name, else exe.
create or replace function fn_app_label(p_exe text, p_product text, p_title text)
returns table (app text, grouped boolean)
language sql stable
set search_path = parent_control, extensions, pg_temp
as $$
  select coalesce(g, nullif(p_product, ''), nullif(p_exe, ''), 'Без окна'), g is not null
  from (select fn_app_group(p_exe, p_product, p_title) as g) x
$$;

-- ---------------------------------------------------------------------
-- [admin] History of one child for [p_from, p_to] (PC-local dates, at most 62 days):
-- per-day screen time vs allowed, top apps, recent sessions.
-- ---------------------------------------------------------------------
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

-- ---------------------------------------------------------------------
-- [admin] Apps without a group over the last p_days, for quick grouping.
-- ---------------------------------------------------------------------
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
         where h.child_id is not null
           and h.minute_ts > now() - make_interval(days => least(greatest(coalesce(p_days, 7), 1), 31))
           and h.state in ('active', 'idle')
           and h.exe_name is not null
           and fn_app_group(h.exe_name, h.product_name, h.window_title) is null
         group by 1, 2
         order by 4 desc
         limit 30) u), '[]'::jsonb);
end $$;

-- ---------------------------------------------------------------------
-- [admin] What a rule would catch over the last 7 days (before saving it).
-- ---------------------------------------------------------------------
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
       where h.child_id is not null
         and h.minute_ts > now() - interval '7 days'
         and h.state in ('active', 'idle')
         and fn_rule_matches(p_field, p_type, p_pattern, h.exe_name, h.product_name, h.window_title)
       group by 1, 2) x;
  return v_result;
end $$;

revoke execute on function
  fn_rule_matches(text, text, text, text, text, text),
  fn_app_label(text, text, text),
  admin_history(uuid, date, date),
  admin_ungrouped(int),
  admin_preview_app_rule(text, text, text)
from public, anon, authenticated;
grant execute on function admin_history(uuid, date, date), admin_ungrouped(int),
  admin_preview_app_rule(text, text, text) to authenticated;
