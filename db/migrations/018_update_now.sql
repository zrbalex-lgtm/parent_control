-- =====================================================================
-- "Update now": an adult asks a PC to update right away, even while a
-- child is at it (normally updates wait until nobody is at the PC).
-- The request is valid for an hour; device_sync passes it on.
-- =====================================================================

set search_path = parent_control, extensions;

alter table devices add column if not exists update_requested_at timestamptz;

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
  v_dev    devices%rowtype;
begin
  -- The core authenticates the device and raises on a bad secret.
  v_result := device_sync_core(p_device_id, p_secret, p_client_version, p_client_now, p_local_date,
                               p_heartbeats, p_acked_commands, p_time_requests);
  select * into v_dev from devices where id = p_device_id;
  return v_result || jsonb_build_object(
    'send_logs', v_dev.logs_requested_at is not null,
    'app_blocks', fn_device_app_blocks(p_device_id),
    'update_requested_at', case when v_dev.update_requested_at > now() - interval '1 hour'
                                then v_dev.update_requested_at end);
end $$;

revoke execute on function device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb)
  from public, anon, authenticated;
grant execute on function device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb) to anon;
