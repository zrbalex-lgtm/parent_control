# parent_control

Home screen-time control for kids' Windows PCs.

| Part | What |
|---|---|
| `db/` | Supabase schema (`parent_control`), RPC, local tests |
| `admin/` | Parent PWA on GitHub Pages: dashboard, requests, history, app groups, children, schedule, devices, security |
| `windows/` | Guard service + tray app (.NET 10, one exe), PowerShell installer |
| `tests/admin/` | Browser tests of the admin app against a Supabase mock |

- Install on a PC: [windows/installer/INSTALL.md](windows/installer/INSTALL.md)
- DB contract: [db/README.md](db/README.md)
- Admin app: [admin/README.md](admin/README.md)

Builds: GitHub Actions → **Windows guard** (tests, self-contained build, installer test) → artifact `parent-control-guard-win-x64`.
