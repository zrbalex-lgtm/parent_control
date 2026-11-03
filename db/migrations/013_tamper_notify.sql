-- =====================================================================
-- Telegram: notify linked chats about selected tamper events
-- (for now: sign-in blocked after repeated re-logins).
-- =====================================================================

set search_path = parent_control, extensions;

create or replace function trg_tamper_events_tg()
returns trigger
language plpgsql security definer
set search_path = parent_control, extensions, pg_temp
as $$
begin
  if new.type in ('login_blocked') then
    perform fn_tg_notify('tamper', new.id);
  end if;
  return null;
end $$;

create trigger tamper_events_tg
  after insert on tamper_events
  for each row execute function trg_tamper_events_tg();
