-- =====================================================================
-- An administrator on the PC lifted the tamper lock locally
-- (ParentControl.Guard.exe clear-tamper): the PC reports
-- 'tamper_cleared_locally' and the server lock follows it.
-- Events are applied in order, so a later lock event wins again.
-- =====================================================================

set search_path = parent_control, extensions;

create or replace function device_report(p_device_id uuid, p_secret text, p_events jsonb)
returns jsonb
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
declare
  v_dev      devices%rowtype;
  e          jsonb;
  v_client   uuid;
  v_at       timestamptz;
  v_type     text;
  v_accepted int := 0;
  v_change   text;            -- null = no change, 'lock' or 'clear'
  v_restored boolean := false;
  v_action   text;
begin
  select * into v_dev from devices where id = p_device_id;
  if not found
     or v_dev.secret_hash is null
     or v_dev.secret_hash <> digest(coalesce(p_secret, ''), 'sha256') then
    raise exception 'unauthorized' using errcode = '28000';
  end if;

  v_action := coalesce((select s.tamper_action from settings s where s.id = 1), 'lock');

  if jsonb_typeof(p_events) = 'array' then
    for e in select * from jsonb_array_elements(p_events) loop
      begin
        v_client := (e->>'client_id')::uuid;
        v_at     := coalesce((e->>'occurred_at')::timestamptz, now());
      exception when others then
        continue;
      end;
      v_type := e->>'type';
      if v_client is null or v_type is null or v_type !~ '^[a-z_]{2,40}$' then
        continue;
      end if;
      insert into tamper_events (client_id, device_id, machine_name, occurred_at, type, details, windows_user)
      values (v_client, p_device_id, v_dev.machine_name, least(v_at, now() + interval '1 day'), v_type,
              case when jsonb_typeof(e->'details') = 'object' then e->'details' end,
              left(nullif(e->>'user', ''), 256))
      on conflict (client_id) do nothing;
      if found then
        v_accepted := v_accepted + 1;
        if fn_is_lock_event(v_type) and v_action = 'lock' then
          v_change := 'lock';
        elsif v_type = 'tamper_cleared_locally' then
          v_change := 'clear';
        end if;
        v_restored := v_restored or v_type = 'config_restored';
      end if;
    end loop;
  end if;

  update devices
     set tamper_locked_since = case v_change
                                 when 'lock'  then coalesce(tamper_locked_since, now())
                                 when 'clear' then null
                                 else tamper_locked_since end,
         config_state = case when v_restored then 'restored' else config_state end
   where id = p_device_id
  returning * into v_dev;

  return jsonb_build_object('accepted', v_accepted, 'tamper_locked', v_dev.tamper_locked_since is not null);
end $$;

revoke execute on function device_report(uuid, text, jsonb) from public, anon, authenticated;
grant execute on function device_report(uuid, text, jsonb) to anon;
