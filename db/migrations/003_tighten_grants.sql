-- Tighten function grants after the Supabase security advisor run.

-- is_admin() is only needed by signed-in users (RLS policies, admin RPC).
revoke execute on function is_admin() from public, anon;
grant execute on function is_admin() to authenticated;

-- Device RPC are for PCs (anon key) only.
revoke execute on function
  device_register(text, text, text),
  device_sync(uuid, text, text, timestamptz, date, jsonb, bigint[])
from authenticated;
