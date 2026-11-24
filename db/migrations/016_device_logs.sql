-- =====================================================================
-- Remote logs: an adult asks a PC for its log ("Прислать журнал"),
-- the guard uploads the last days of its log file on the next sync.
-- device_sync gets one extra answer field through a thin wrapper, so the
-- big function from 007 stays untouched.
-- =====================================================================

set search_path = parent_control, extensions;

alter table devices add column if not exists logs_requested_at timestamptz;

create table if not exists device_logs (
  id           bigserial primary key,
  device_id    uuid not null references devices(id) on delete cascade,
  requested_at timestamptz,
  uploaded_at  timestamptz not null default now(),
  size_bytes   int not null,
  content      text not null
);
create index if not exists device_logs_device_idx on device_logs (device_id, uploaded_at desc);

alter table device_logs enable row level security;
drop policy if exists admin_read on device_logs;
create policy admin_read on device_logs for select to authenticated
  using (is_admin() and fn_my_device(device_id));
drop policy if exists admin_delete on device_logs;
create policy admin_delete on device_logs for delete to authenticated
  using (is_admin() and fn_my_device(device_id));

revoke all on device_logs from anon, authenticated;
grant select, delete on device_logs to authenticated;
grant all on device_logs to service_role;

-- ---------------------------------------------------------------------
-- device_sync: same signature and answer as before, plus "send_logs".
-- ---------------------------------------------------------------------
do $$
begin
  if not exists (select 1 from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                  where n.nspname = 'parent_control' and p.proname = 'device_sync_core') then
    alter function device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb)
      rename to device_sync_core;
  end if;
end $$;

revoke execute on function device_sync_core(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb)
  from public, anon, authenticated;

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
  return v_result || jsonb_build_object('send_logs',
    coalesce((select d.logs_requested_at is not null from devices d where d.id = p_device_id), false));
end $$;

revoke execute on function device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb)
  from public, anon, authenticated;
grant execute on function device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[], jsonb) to anon;

-- ---------------------------------------------------------------------
-- [anon] The PC uploads its log text. Keeps the last 3 uploads per PC.
-- ---------------------------------------------------------------------
create or replace function device_upload_logs(p_device_id uuid, p_secret text, p_content text)
returns jsonb
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_dev  devices%rowtype;
  v_text text;
  v_id   bigint;
begin
  select * into v_dev from devices where id = p_device_id;
  if not found
     or v_dev.secret_hash is null
     or v_dev.secret_hash <> digest(coalesce(p_secret, ''), 'sha256') then
    raise exception 'unauthorized' using errcode = '28000';
  end if;

  -- At most ~2 MB: keep the newest part (the end of the text).
  v_text := coalesce(p_content, '');
  if length(v_text) > 2000000 then
    v_text := right(v_text, 2000000);
  end if;

  insert into device_logs (device_id, requested_at, size_bytes, content)
  values (p_device_id, v_dev.logs_requested_at, octet_length(v_text), v_text)
  returning id into v_id;

  update devices set logs_requested_at = null where id = p_device_id;

  delete from device_logs
   where device_id = p_device_id
     and id not in (select l.id from device_logs l where l.device_id = p_device_id
                     order by l.uploaded_at desc, l.id desc limit 3);

  return jsonb_build_object('id', v_id, 'size_bytes', octet_length(v_text));
end $$;

revoke execute on function device_upload_logs(uuid, text, text) from public, anon, authenticated;
grant execute on function device_upload_logs(uuid, text, text) to anon;
