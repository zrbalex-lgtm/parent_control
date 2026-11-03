# db — Supabase schema

All objects live in schema **`parent_control`** of the shared "Home projects"
Supabase project (one schema per home project; Auth is shared).

| File | What |
|---|---|
| `migrations/001_schema.sql` | Tables, views, RLS |
| `migrations/003_tighten_grants.sql` | Function grants after advisor run |
| `migrations/002_rpc.sql` | RPC: `admin_create_device`, `admin_reset_device_code`, `admin_dashboard`, `device_register`, `device_sync` |
| `migrations/004_time_requests.sql` | `time_requests`, `device_sync` v2 (`p_time_requests`), `admin_decide_request` |
| `migrations/005_locked_by_default.sql` | `day_unlocks`, unlock requests, `fn_day_access`, `settings` |
| `migrations/006_child_locked_by_default.sql` | `children.locked_by_default` (per child) |
| `migrations/007_hardening.sql` | `tamper_events`, `device_report`, `device_beacon`, tamper lock, admin RPCs |
| `migrations/008_local_tamper_clear.sql` | `device_report` applies events in order; `tamper_cleared_locally` lifts the lock |
| `migrations/009_reports.sql` | `admin_history`, `admin_ungrouped`, `admin_preview_app_rule` (History tab, app groups) |
| `migrations/010_telegram.sql` | Telegram bot: linked chats, request notifications (pg_net → Edge Function `telegram-bot`), `tg_*` functions |
| `migrations/011_auto_update.sql` | `devices.auto_update`, `device_update_policy` |
| `tests/` | Local test run against plain PostgreSQL (with a Supabase stub) |

## Applying to Supabase

1. Run the migrations in order: `001` … `011`.
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

### Tamper events (since 007)

- `device_report(p_device_id, p_secret, p_events)` — items `{"client_id", "occurred_at", "type", "details": {}, "user"}`;
  returns `{"accepted", "tamper_locked"}`. Lock types (see `fn_is_lock_event`) set `devices.tamper_locked_since`
  when `settings.tamper_action = 'lock'`.
- `device_beacon(p_machine_name, p_reason, p_device_id)` — unauthenticated "config lost" signal, rate limited.
- `device_sync` response adds `tamper_locked`, `tamper_action`.
- Admin: `admin_clear_tamper_lock(device_id)`, `admin_mark_tamper_handled(ids[])`.
- Since 008: event `tamper_cleared_locally` (sent after `ParentControl.Guard.exe clear-tamper` on the PC) clears the lock;
  events are applied in order, so a lock event after it locks again.
