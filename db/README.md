# db — Supabase schema

| File | What |
|---|---|
| `migrations/001_schema.sql` | Tables, views, RLS |
| `migrations/002_rpc.sql` | RPC: `admin_create_device`, `admin_reset_device_code`, `admin_dashboard`, `device_register`, `device_sync` |
| `tests/` | Local test run against plain PostgreSQL (with a Supabase stub) |

## Applying to Supabase

1. Run the migrations in order: `001_schema.sql`, `002_rpc.sql`, `003_tighten_grants.sql`.
2. Auth → Providers → Email: **disable sign-ups**.
3. Create the parent user (Auth → Users → Add user), then:
   ```sql
   insert into app_admins (user_id) select id from auth.users where email = '<parent email>';
   ```

## Local tests

```bash
PGUSER=postgres ./db/tests/run_local.sh
```
Creates a throwaway `pc_test` database and prints `ok …` per check.

## device_sync contract

Request (once a minute):
`device_sync(p_device_id, p_secret, p_client_version, p_client_now, p_local_date, p_heartbeats, p_acked_commands)`

Heartbeat item:
```json
{"ts":"2026-11-01T14:05:00Z","local_date":"2026-11-01","state":"active",
 "sid":"S-1-5-21-...","user":"misha","exe":"javaw.exe",
 "product":"Java(TM) Platform SE","title":"Minecraft 1.21"}
```
`state`: `active` | `idle` | `locked` | `no_user`. Invalid items are dropped silently.

Response:
```json
{
  "server_now": "...", "device_enabled": true, "accepted": 3,
  "accounts": [{"sid":"S-1-5-...","user":"misha","child_id":"..."}],
  "children": [{"child_id":"...","name":"Миша","days":[
     {"local_date":"2026-11-01","limit_minutes":120,"bonus_minutes":30,
      "window_from":"15:00","window_to":"20:00","spent_minutes":47}, {"...": "tomorrow"}]}],
  "locks":    [{"id":12,"child_id":"...","lock_from":"...","lock_until":"..."}],
  "messages": [{"id":13,"child_id":"...","text":"Ужинать!","created_at":"..."}]
}
```
- `limit_minutes: null` = no limit. Window is `[from, to)` in PC local time.
- `spent_minutes` = distinct counted minutes for that child across all PCs.
- Lock active while `lock_from <= now < lock_until` (`lock_until: null` = until cancelled).
- Messages repeat until their id is sent back in `p_acked_commands`.
- `device_enabled: false` → the service stops enforcing.
- Errors: SQLSTATE `28000` = bad secret / code.
