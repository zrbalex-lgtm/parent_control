-- One-off cleanup: the first deploy put everything into "public".
-- Parent control now lives in schema "parent_control"; these were empty.
-- Run once in the Supabase SQL editor.
drop view if exists public.v_child_day_usage, public.v_device_now, public.v_sessions,
                    public.v_app_usage_day, public.v_device_flags;
drop table if exists public.command_deliveries, public.commands, public.heartbeats,
                     public.bonuses, public.schedule_overrides, public.schedule_weekly,
                     public.device_accounts, public.devices, public.app_groups,
                     public.children, public.app_admins cascade;
drop function if exists
  public.admin_dashboard(date),
  public.device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[]),
  public.device_register(text, text, text),
  public.admin_reset_device_code(uuid),
  public.admin_create_device(text),
  public.fn_spent_minutes(uuid, date),
  public.fn_parse_heartbeats(jsonb),
  public.fn_new_secret(),
  public.fn_new_reg_code(),
  public.fn_norm_code(text),
  public.fn_effective_day(uuid, date),
  public.fn_app_group(text, text, text),
  public.trg_app_groups_validate(),
  public.is_admin();
