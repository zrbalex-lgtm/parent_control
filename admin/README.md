# admin — parent PWA

Static app (no build step), published to GitHub Pages by `.github/workflows/pages.yml`.

- `js/config.js` — Supabase URL + **public** anon key + schema. Safe to commit: data is protected by RLS and `parent_control.app_admins`.
- `js/api.js` — the only module that talks to Supabase (supabase-js from jsDelivr, pinned).
- `js/views/` — `children`, `schedule`, `devices`.

Install on the phone: open the Pages URL in Safari → Share → «На экран Домой».

## Tests

```bash
node tests/admin/e2e.mjs [screenshotDir]
```
Runs the app in headless Chromium at phone width against an in-memory Supabase mock (`tests/admin/mock-supabase.js`).
