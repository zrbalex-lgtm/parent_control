-- =====================================================================
-- App blocks: an adult blocks an app group for a child until a time
-- (e.g. "YouTube for 2 hours"). The PC gets the active blocks with the
-- group's rules in the device_sync answer and closes matching windows:
-- the browser tab for sites, the app itself otherwise.
-- =====================================================================

set search_path = parent_control, extensions;

create table if not exists app_blocks (
  id           bigint generated always as identity primary key,
  child_id     uuid not null references children(id) on delete cascade,
  group_name   text not null check (length(trim(group_name)) > 0),
  until        timestamptz not null,
  created_by   uuid default auth.uid(),
  created_at   timestamptz not null default now(),
  cancelled_at timestamptz,
  check (until > created_at)
);
create index if not exists app_blocks_active_idx on app_blocks (child_id, until) where cancelled_at is null;

alter table app_blocks enable row level security;
drop policy if exists admin_all on app_blocks;
create policy admin_all on app_blocks for all to authenticated
  using (is_admin() and fn_my_child(child_id))
  with check (is_admin() and fn_my_child(child_id));

revoke all on app_blocks from anon, authenticated;
grant select, insert, update on app_blocks to authenticated;
grant all on app_blocks to service_role;

-- ---------------------------------------------------------------------
-- Active blocks for the children bound to accounts on this PC, with the
-- enabled rules of each blocked group.
-- ---------------------------------------------------------------------
create or replace function fn_device_app_blocks(p_device_id uuid)
returns jsonb
language sql stable
set search_path = parent_control, extensions, pg_temp
as $$
  select coalesce(jsonb_agg(jsonb_build_object(
           'child_id', b.child_id,
           'group_name', b.group_name,
           'until', b.until,
           'rules', (select coalesce(jsonb_agg(jsonb_build_object(
                              'match_field', g.match_field, 'match_type', g.match_type, 'pattern', g.pattern)
                            order by g.id), '[]'::jsonb)
                       from app_groups g
                      where g.group_name = b.group_name and g.is_enabled))
         order by b.until), '[]'::jsonb)
  from app_blocks b
  where b.cancelled_at is null
    and b.until > now()
    and b.child_id in (select a.child_id from device_accounts a
                        where a.device_id = p_device_id and a.child_id is not null);
$$;

revoke execute on function fn_device_app_blocks(uuid) from public, anon, authenticated;

-- device_sync (wrapper from 016): adds "app_blocks".
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
  v_result jsonb;
begin
  -- The core authenticates the device and raises on a bad secret.
  v_result := device_sync_core(p_device_id, p_secret, p_client_version, p_client_now, p_local_date,
                               p_heartbeats, p_acked_commands, p_time_requests);
  return v_result || jsonb_build_object(
    'send_logs', coalesce((select d.logs_requested_at is not null from devices d where d.id = p_device_id), false),
    'app_blocks', fn_device_app_blocks(p_device_id));
end $$;

revoke execute on function device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb)
  from public, anon, authenticated;
grant execute on function device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb) to anon;
