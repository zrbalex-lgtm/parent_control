# db — Supabase schema

All objects live in schema **`parent_control`** of the shared "Home projects"
Supabase project (one schema per home project; Auth is shared).

| File | What |
|---|---|
| `migrations/001_schema.sql` | Tables, views, RLS |
| `migrations/003_tighten_grants.sql` | Function grants after advisor run |
| `migrations/002_rpc.sql` | RPC: `admin_create_device`, `admin_reset_device_code`, `admin_dashboard`, `device_register`, `device_sync` |
| `migrations/004_time_requests.sql` | `time_requests`, `device_sync` v2 (`p_time_requests`), `admin_decide_request` |
| `tests/` | Local test run against plain PostgreSQL (with a Supabase stub) |

## Applying to Supabase

1. Run the migrations in order: `001` … `004`.
2. Settings → API (Data API) → **Exposed schemas**: add `parent_control`.
3. Auth → Providers → Email: **disable sign-ups** (shared by all home projects).
4. Create the parent user (Auth → Users → Add user), then:
   ```sql
   insert into parent_control.app_admins (user_id)
   select id from auth.users where email = '<parent email>';
   ```
   Being signed in is not enough: only users in `parent_control.app_admins`
   see this project's data, so other home-project users get nothing here.

## Clients

Every client must target the schema:
- supabase-js: `createClient(url, key, { db: { schema: 'parent_control' } })`
- plain HTTP (Windows service): headers `Content-Profile: parent_control`
  (POST /rest/v1/rpc/...) and `Accept-Profile: parent_control` (GET).

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

### Time requests (since 004)

`device_sync(..., p_time_requests jsonb)` — items `{"client_id": "<uuid>", "sid": "S-1-…", "minutes": 30, "reason": "…"}`.
Idempotent by `client_id`; at most one pending request per child; unknown/unbound SIDs ignored.
Response gets `"requests": [{"id", "client_id", "child_id", "minutes", "granted_minutes", "status", "created_at", "decided_at"}]`
(pending + last 24h). Admin decides via `admin_decide_request(p_id, p_approve, p_minutes, p_local_date)`; approval inserts a bonus.
