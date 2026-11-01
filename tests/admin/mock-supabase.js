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
      last_seen_at: iso(30e3), clock_skew_sec: 420, created_at: iso(86400e3) },
    { id: uid(), name: 'Ноутбук', machine_name: null, is_enabled: true,
      registered_at: null, reg_code_expires_at: new Date(now + 20 * 3600e3).toISOString(),
      client_version: null, last_seen_at: null, clock_skew_sec: null, created_at: iso(3600e3) },
  ],
  device_accounts: [],
  time_requests: [],
  bonuses: [],
});
store.device_accounts.push(
  { id: uid(), device_id: store.devices[0].id, windows_sid: 'S-1-5-21-1', windows_user: 'misha', child_id: null, last_seen_at: iso(60e3) },
  { id: uid(), device_id: store.devices[0].id, windows_sid: 'S-1-5-21-2', windows_user: 'papa', child_id: null, last_seen_at: iso(7200e3) });

const PK = { children: ['id'], devices: ['id'], device_accounts: ['id'],
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
        return { data: out, error: null };
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
      if (name === 'admin_decide_request') {
        const r = store.time_requests.find((x) => x.id === args.p_id);
        if (!r || r.status !== 'pending') return { data: null, error: { message: 'request already decided' } };
        r.status = args.p_approve ? 'approved' : 'denied';
        r.decided_at = new Date().toISOString();
        if (args.p_approve) {
          r.granted_minutes = args.p_minutes ?? r.minutes;
          store.bonuses.push({ child_id: r.child_id, local_date: args.p_local_date, minutes: r.granted_minutes });
        }
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
