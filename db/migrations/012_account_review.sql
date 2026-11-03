-- =====================================================================
-- "Unbound account" flag only for accounts nobody has decided on yet.
-- An account the admin set to "no limits" on purpose is reviewed, not unbound.
-- =====================================================================

set search_path = parent_control, extensions;

alter table device_accounts add column if not exists reviewed_at timestamptz;

-- Accounts that exist now have been seen and handled already.
update device_accounts set reviewed_at = now() where reviewed_at is null;

-- Any admin change to the binding (including "no limits") marks the account as reviewed.
create or replace function trg_device_accounts_reviewed()
returns trigger
language plpgsql
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if auth.uid() is not null then
    new.reviewed_at := now();
  end if;
  return new;
end $$;

create trigger device_accounts_reviewed
  before update of child_id on device_accounts
  for each row execute function trg_device_accounts_reviewed();

create or replace view v_device_flags with (security_invoker = true) as
select
  d.id as device_id,
  abs(coalesce(d.clock_skew_sec, 0)) > 300 as clock_skew,
  exists (select 1 from device_accounts a
          where a.device_id = d.id and a.child_id is null and a.reviewed_at is null
            and a.last_seen_at > now() - interval '7 days')       as unbound_account,
  d.is_enabled and d.last_seen_at < now() - interval '24 hours'    as long_silence,
  (select count(*) from heartbeats h
   where h.device_id = d.id
     and h.minute_ts > now() - interval '7 days'
     and h.received_at - h.minute_ts > interval '5 minutes')::int  as offline_minutes_7d
from devices d;
