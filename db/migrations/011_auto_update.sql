-- =====================================================================
-- Auto-update: per-device switch the guard asks before installing a new
-- version from GitHub Releases.
-- =====================================================================

set search_path = parent_control, extensions;

alter table devices add column if not exists auto_update boolean not null default true;

-- [anon] Is this PC allowed to install updates right now?
create or replace function device_update_policy(p_device_id uuid, p_secret text)
returns jsonb
language plpgsql stable security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_dev devices%rowtype;
begin
  select * into v_dev from devices where id = p_device_id;
  if not found
     or v_dev.secret_hash is null
     or v_dev.secret_hash <> digest(coalesce(p_secret, ''), 'sha256') then
    raise exception 'unauthorized' using errcode = '28000';
  end if;
  return jsonb_build_object('auto_update', v_dev.auto_update and v_dev.is_enabled);
end $$;

revoke execute on function device_update_policy(uuid, text) from public, anon, authenticated;
grant execute on function device_update_policy(uuid, text) to anon;
