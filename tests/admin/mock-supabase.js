// In-memory stand-in for supabase-js, served instead of the CDN bundle in
// browser tests. Covers only what admin/js/api.js uses.
const now = Date.now();
const iso = (msAgo) => new Date(now - msAgo).toISOString();
let seq = 1;
const uid = () => `00000000-0000-4000-8000-${String(seq++).padStart(12, '0')}`;

const store = (window.__store = {
  children: [],
  schedule_weekly: [],
  schedule_overrides: [],
  devices: [
    { id: uid(), name: 'Комп в детской', machine_name: 'KIDS-PC', is_enabled: true,
      registered_at: iso(86400e3), reg_code_expires_at: null, client_version: '0.1.0',
      last_seen_at: iso(30e3), clock_skew_sec: 420, created_at: iso(86400e3),
      tamper_locked_since: iso(600e3), config_state: 'restored', auto_update: true },
    { id: uid(), name: 'Ноутбук', machine_name: null, is_enabled: true,
      registered_at: null, reg_code_expires_at: new Date(now + 20 * 3600e3).toISOString(),
      client_version: null, last_seen_at: null, clock_skew_sec: null, created_at: iso(3600e3),
      tamper_locked_since: null, config_state: 'ok' },
  ],
  tamper_events: [],
  app_groups: [],
  tg_chats: [{ chat_id: -100123, title: 'Семья', linked_by: 'Alexander', linked_at: iso(86400e3) }],
  device_accounts: [],
  time_requests: [],
  bonuses: [],
  settings: [{ id: 1, locked_by_default: false, tamper_action: 'lock' }],
  day_unlocks: [],
  commands: [],
  schedule_weekly_seed: null,
});
store.tamper_events.push(
  { id: 1, device_id: store.devices[0].id, machine_name: 'KIDS-PC', occurred_at: iso(600e3), received_at: iso(590e3),
    type: 'config_restored', details: { files: 'device.json,state.json' }, windows_user: 'misha', verified: true, handled_at: null },
  { id: 2, device_id: store.devices[0].id, machine_name: 'KIDS-PC', occurred_at: iso(500e3), received_at: iso(490e3),
    type: 'clock_changed', details: { kind: 'timezone', from: 'W. Europe Standard Time', to: 'Hawaiian Standard Time' },
    windows_user: 'misha', verified: true, handled_at: null },
  { id: 3, device_id: store.devices[0].id, machine_name: 'KIDS-PC', occurred_at: iso(90000e3), received_at: iso(90000e3),
    type: 'tray_unresponsive', details: {}, windows_user: 'misha', verified: true, handled_at: iso(80000e3) });
store.device_accounts.push(
  { id: uid(), device_id: store.devices[0].id, windows_sid: 'S-1-5-21-1', windows_user: 'misha', child_id: null, last_seen_at: iso(60e3) },
  { id: uid(), device_id: store.devices[0].id, windows_sid: 'S-1-5-21-2', windows_user: 'papa', child_id: null, last_seen_at: iso(7200e3) });

// Apps seen on the PCs (for history, ungrouped list and rule preview).
const SEEN_APPS = [
  { exe: 'javaw.exe', product: 'Java(TM) Platform SE', title: 'Minecraft 1.21', minutes: 340 },
  { exe: 'chrome.exe', product: 'Google Chrome', title: 'YouTube — Google Chrome', minutes: 210 },
  { exe: 'Roblox.exe', product: null, title: 'Roblox', minutes: 95 },
];
const ruleMatches = (r, a) => {
  const v = r.match_field === 'exe' ? a.exe : r.match_field === 'product' ? a.product : a.title;
  if (!v) return false;
  return r.match_type === 'contains' ? v.toLowerCase().includes(r.pattern.toLowerCase()) : new RegExp(r.pattern, 'i').test(v);
};
const groupOf = (a) => store.app_groups.filter((r) => r.is_enabled !== false)
  .sort((x, y) => (x.priority ?? 100) - (y.priority ?? 100)).find((r) => ruleMatches(r, a))?.group_name ?? null;

const PK = { tg_chats: ['chat_id'], app_groups: ['id'], commands: ['id'], bonuses: ['id'], settings: ['id'], day_unlocks: ['child_id', 'local_date'], children: ['id'], devices: ['id'], device_accounts: ['id'],
             schedule_weekly: ['child_id', 'weekday'], schedule_overrides: ['child_id', 'local_date'] };
const TIME_COLS = ['window_from', 'window_to'];
const toPgTime = (r) => {
  for (const c of TIME_COLS) if (typeof r[c] === 'string' && r[c].length === 5) r[c] = `${r[c]}:00`;
  return r;
};

function flagsView() {
  return store.devices.map((d) => ({
    device_id: d.id,
    clock_skew: Math.abs(d.clock_skew_sec || 0) > 300,
    unbound_account: store.device_accounts.some((a) => a.device_id === d.id && !a.child_id),
    long_silence: d.is_enabled && !!d.last_seen_at && now - Date.parse(d.last_seen_at) > 86400e3,
    offline_minutes_7d: d.registered_at ? 12 : 0,
  }));
}

class Query {
  constructor(table) { this.table = table; this.op = 'select'; this.filters = []; this.orders = []; }
  select() { return this; }
  eq(c, v) { this.filters.push((r) => r[c] === v); return this; }
  gte(c, v) { this.filters.push((r) => r[c] != null && r[c] >= v); return this; }
  neq(c, v) { this.filters.push((r) => r[c] !== v); return this; }
  in(c, vs) { this.filters.push((r) => vs.includes(r[c])); return this; }
  is(c, v) { this.filters.push((r) => (r[c] ?? null) === v); return this; }
  limit(n) { this.max = n; return this; }
  order(c, opts = {}) { this.orders.push({ c, desc: opts.ascending === false }); return this; }
  insert(rows) { this.op = 'insert'; this.payload = [].concat(rows); return this; }
  update(patch) { this.op = 'update'; this.payload = patch; return this; }
  delete() { this.op = 'delete'; return this; }
  upsert(rows, opts = {}) {
    this.op = 'upsert'; this.payload = [].concat(rows);
    this.keys = opts.onConflict ? opts.onConflict.split(',') : PK[this.table];
    return this;
  }
  then(resolve, reject) {
    return new Promise((r) => setTimeout(r, 40)).then(() => this.exec()).then(resolve, reject);
  }
  exec() {
    window.__calls = (window.__calls || []).concat([{ table: this.table, op: this.op, payload: this.payload }]);
    if (this.table === 'v_device_flags') return { data: flagsView(), error: null };
    const rows = store[this.table];
    if (!rows) return { data: null, error: { message: `relation "${this.table}" does not exist` } };
    const match = (r) => this.filters.every((f) => f(r));
    switch (this.op) {
      case 'select': {
        let out = rows.filter(match).map((r) => structuredClone(r));
        for (const { c, desc } of [...this.orders].reverse()) {
          out.sort((a, b) => (desc ? -1 : 1) * String(a[c] ?? '').localeCompare(String(b[c] ?? ''), 'ru', { numeric: true }));
        }
        return { data: this.max ? out.slice(0, this.max) : out, error: null };
      }
      case 'insert':
        for (const p of this.payload) rows.push(toPgTime({ id: uid(), created_at: new Date().toISOString(), ...p }));
        return { data: null, error: null };
      case 'update':
        for (const r of rows.filter(match)) Object.assign(r, toPgTime({ ...this.payload }));
        return { data: null, error: null };
      case 'delete':
        store[this.table] = rows.filter((r) => !match(r));
        if (this.table === 'children') {
          const ids = new Set(store.children.map((c) => c.id));
          for (const a of store.device_accounts) if (a.child_id && !ids.has(a.child_id)) a.child_id = null;
        }
        return { data: null, error: null };
      case 'upsert':
        for (const p of this.payload) {
          const ex = rows.find((r) => this.keys.every((k) => r[k] === p[k]));
          if (ex) Object.assign(ex, toPgTime({ ...p }));
          else rows.push(toPgTime({ ...p }));
        }
        return { data: null, error: null };
      default:
        return { data: null, error: { message: 'unsupported' } };
    }
  }
}

function newCode() {
  const a = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  let s = '';
  for (let i = 0; i < 8; i++) s += a[Math.floor(Math.random() * a.length)] + (i === 3 ? '-' : '');
  return s;
}

export function createClient() {
  const listeners = [];
  let session = null;
  try { session = JSON.parse(localStorage.getItem('mock-session')); } catch { /* ignore */ }
  const emit = () => listeners.forEach((cb) => cb(session ? 'SIGNED_IN' : 'SIGNED_OUT', session));
  return {
    auth: {
      async getSession() { return { data: { session } }; },
      onAuthStateChange(cb) {
        listeners.push(cb);
        setTimeout(() => cb('INITIAL_SESSION', session), 0);
        return { data: { subscription: { unsubscribe() {} } } };
      },
      async signInWithPassword({ email, password }) {
        if (password !== 'secret') return { data: null, error: { message: 'Invalid login credentials' } };
        session = { user: { id: 'u1', email } };
        localStorage.setItem('mock-session', JSON.stringify(session));
        setTimeout(emit, 0);
        return { data: { session }, error: null };
      },
      async signOut() {
        session = null;
        localStorage.removeItem('mock-session');
        setTimeout(emit, 0);
        return { error: null };
      },
    },
    from: (t) => new Query(t),
    async rpc(name, args) {
      await new Promise((r) => setTimeout(r, 40));
      window.__calls = (window.__calls || []).concat([{ rpc: name, args }]);
      if (name === 'is_admin') return { data: true, error: null };
      if (name === 'admin_dashboard') {
        const now = Date.now();
        const isoDow = ((new Date().getDay() + 6) % 7) + 1;
        return { data: { children: store.children.map((c, i) => {
          const w = store.schedule_weekly.find((r) => r.child_id === c.id && r.weekday === isoDow);
          return {
            ...c,
            limit_minutes: w ? w.limit_minutes : 120,
            bonus_minutes: store.bonuses.filter((b) => b.child_id === c.id && b.local_date === args.p_local_date)
              .reduce((a, b) => a + b.minutes, 0),
            window_from: w ? w.window_from.slice(0, 5) : '00:00',
            window_to: w ? w.window_to.slice(0, 5) : '24:00',
            spent_minutes: i === 0 ? 100 : 20,
            requires_unlock: !!c.locked_by_default,
            unlocked: store.day_unlocks.some((u) => u.child_id === c.id && u.local_date === args.p_local_date),
            now: i === 0 ? { device_id: store.devices[0].id, device_name: store.devices[0].name, state: 'active',
                             app_group: 'Minecraft', exe: 'javaw.exe', title: 'Minecraft 1.21', minute_ts: new Date().toISOString() } : null,
            tamper_unhandled: 0,
            tamper_locked: false,
            locks: store.commands.filter((k) => k.child_id === c.id && k.kind === 'lock' && !k.cancelled_at
                                          && (!k.lock_until || Date.parse(k.lock_until) > now)),
          };
        }) }, error: null };
      }
      if (name === 'admin_decide_request') {
        const r = store.time_requests.find((x) => x.id === args.p_id);
        if (!r || r.status !== 'pending') return { data: null, error: { message: 'request already decided' } };
        r.status = args.p_approve ? 'approved' : 'denied';
        r.decided_at = new Date().toISOString();
        if (args.p_approve && r.kind === 'unlock') {
          store.day_unlocks.push({ child_id: r.child_id, local_date: args.p_local_date });
        } else if (args.p_approve) {
          r.granted_minutes = args.p_minutes ?? r.minutes;
          store.bonuses.push({ child_id: r.child_id, local_date: args.p_local_date, minutes: r.granted_minutes });
        }
        return { data: null, error: null };
      }
      if (name === 'admin_tg_link_code') return { data: 'A1B2C3', error: null };
      if (name === 'admin_history') {
        const days = [];
        for (let d = new Date(`${args.p_from}T00:00:00`); d <= new Date(`${args.p_to}T00:00:00`); d.setDate(d.getDate() + 1)) {
          const i = days.length;
          const p = (n) => String(n).padStart(2, '0');
          days.push({ date: `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`,
                      minutes: [95, 130, 0, 60, 145, 30, 110][i % 7], idle_minutes: 5, allowed: 120, bonus_minutes: 0 });
        }
        const byApp = new Map();
        for (const a of SEEN_APPS) {
          const g = groupOf(a);
          const key = g || a.product || a.exe;
          const cur = byApp.get(key) || { app: key, grouped: !!g, minutes: 0 };
          cur.minutes += a.minutes;
          byApp.set(key, cur);
        }
        const t0 = Date.now() - 3600e3;
        return { data: {
          days,
          apps: [...byApp.values()].sort((a, b) => b.minutes - a.minutes),
          sessions: [
            { device_id: store.devices[0].id, device_name: store.devices[0].name, started_at: new Date(t0).toISOString(),
              ended_at: new Date(t0 + 48 * 60e3).toISOString(), minutes: 48, top_app: groupOf(SEEN_APPS[0]) || 'Java(TM) Platform SE' },
            { device_id: store.devices[0].id, device_name: store.devices[0].name, started_at: new Date(t0 - 86400e3).toISOString(),
              ended_at: new Date(t0 - 86400e3 + 75 * 60e3).toISOString(), minutes: 75, top_app: 'Google Chrome' },
          ],
        }, error: null };
      }
      if (name === 'admin_ungrouped') {
        return { data: SEEN_APPS.filter((a) => !groupOf(a)), error: null };
      }
      if (name === 'admin_preview_app_rule') {
        let hits;
        try {
          hits = SEEN_APPS.filter((a) => ruleMatches({ match_field: args.p_field, match_type: args.p_type, pattern: args.p_pattern }, a));
        } catch {
          return { data: null, error: { message: 'invalid regex', code: '22023' } };
        }
        return { data: { minutes: hits.reduce((x, a) => x + a.minutes, 0), samples: hits }, error: null };
      }
      if (name === 'admin_clear_tamper_lock') {
        const d = store.devices.find((x) => x.id === args.p_device_id);
        Object.assign(d, { tamper_locked_since: null, config_state: 'ok' });
        return { data: null, error: null };
      }
      if (name === 'admin_mark_tamper_handled') {
        for (const e of store.tamper_events) if (args.p_ids.includes(e.id) && !e.handled_at) e.handled_at = new Date().toISOString();
        return { data: null, error: null };
      }
      if (name === 'admin_create_device') {
        const id = uid();
        store.devices.push({ id, name: args.p_name, machine_name: null, is_enabled: true, registered_at: null,
          reg_code_expires_at: new Date(Date.now() + 86400e3).toISOString(), client_version: null,
          last_seen_at: null, clock_skew_sec: null, created_at: new Date().toISOString() });
        return { data: [{ device_id: id, code: newCode() }], error: null };
      }
      if (name === 'admin_reset_device_code') {
        const d = store.devices.find((x) => x.id === args.p_device_id);
        Object.assign(d, { registered_at: null, reg_code_expires_at: new Date(Date.now() + 86400e3).toISOString() });
        return { data: newCode(), error: null };
      }
      return { data: null, error: { message: `unknown rpc ${name}` } };
    },
  };
}
