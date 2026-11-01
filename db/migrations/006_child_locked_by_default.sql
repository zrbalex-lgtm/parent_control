-- =====================================================================
-- "Locked by default" becomes a per-child setting (children.locked_by_default).
-- The former global value is copied to every child; settings.locked_by_default
-- is no longer read.
-- =====================================================================

set search_path = parent_control, extensions;

alter table children add column locked_by_default boolean not null default false;
update children
   set locked_by_default = coalesce((select s.locked_by_default from settings s where s.id = 1), false);

create or replace function fn_day_access(p_child uuid, p_date date)
returns table (requires_unlock boolean, unlocked boolean)
language sql stable
set search_path = parent_control, extensions, pg_temp
as $$
  select coalesce((select c.locked_by_default from children c where c.id = p_child), false),
         exists (select 1 from day_unlocks u where u.child_id = p_child and u.local_date = p_date)
$$;
