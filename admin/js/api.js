// Data access layer: the only module that talks to Supabase.
import { SUPABASE_URL, SUPABASE_KEY, DB_SCHEMA, SUPABASE_JS_URL } from './config.js';

const { createClient } = await import(SUPABASE_JS_URL);

const sb = createClient(SUPABASE_URL, SUPABASE_KEY, {
  db: { schema: DB_SCHEMA },
  auth: { persistSession: true, autoRefreshToken: true, storageKey: 'pc-admin-auth' },
});

function unwrap({ data, error }) {
  if (error) throw error;
  return data;
}

// "15:00:00" -> "15:00"
const hhmm = (t) => (t ? String(t).slice(0, 5) : null);

const DEVICE_COLUMNS =
  'id,name,machine_name,is_enabled,registered_at,reg_code_expires_at,' +
  'client_version,last_seen_at,clock_skew_sec,created_at';

export const api = {
  // ---------------- auth ----------------
  async getSession() {
    const { data } = await sb.auth.getSession();
    return data.session;
  },
  onAuthChange(cb) {
    sb.auth.onAuthStateChange((_event, session) => cb(session));
  },
  async signIn(email, password) {
    unwrap(await sb.auth.signInWithPassword({ email, password }));
  },
  async signOut() {
    await sb.auth.signOut();
  },
  async isAdmin() {
    return unwrap(await sb.rpc('is_admin')) === true;
  },

  // ---------------- children ----------------
  async listChildren() {
    return unwrap(
      await sb.from('children').select('id,name,color,avatar,sort_order')
        .order('sort_order').order('name'));
  },
  async saveChild(child) {
    const row = {
      name: child.name, color: child.color, avatar: child.avatar || null,
      sort_order: child.sort_order ?? 0,
    };
    if (child.id) {
      unwrap(await sb.from('children').update(row).eq('id', child.id));
    } else {
      unwrap(await sb.from('children').insert(row));
    }
  },
  async deleteChild(id) {
    unwrap(await sb.from('children').delete().eq('id', id));
  },

  // ---------------- schedule ----------------
  // Returns 7 entries (ISO weekday 1..7); missing rows mean "no limit, whole day".
  async getWeekly(childId) {
    const rows = unwrap(
      await sb.from('schedule_weekly')
        .select('weekday,limit_minutes,window_from,window_to')
        .eq('child_id', childId));
    const byDay = new Map(rows.map((r) => [r.weekday, r]));
    return [1, 2, 3, 4, 5, 6, 7].map((d) => {
      const r = byDay.get(d);
      return {
        weekday: d,
        limit_minutes: r ? r.limit_minutes : null,
        window_from: r ? hhmm(r.window_from) : '00:00',
        window_to: r ? hhmm(r.window_to) : '24:00',
      };
    });
  },
  async saveWeekly(childId, days) {
    const rows = days.map((d) => ({
      child_id: childId, weekday: d.weekday, limit_minutes: d.limit_minutes,
      window_from: d.window_from, window_to: d.window_to,
    }));
    unwrap(await sb.from('schedule_weekly').upsert(rows, { onConflict: 'child_id,weekday' }));
  },
  async listOverrides(childId, fromDate) {
    const rows = unwrap(
      await sb.from('schedule_overrides')
        .select('local_date,limit_minutes,window_from,window_to,note')
        .eq('child_id', childId).gte('local_date', fromDate).order('local_date'));
    return rows.map((r) => ({ ...r, window_from: hhmm(r.window_from), window_to: hhmm(r.window_to) }));
  },
  async saveOverride(childId, o) {
    unwrap(await sb.from('schedule_overrides').upsert({
      child_id: childId, local_date: o.local_date, limit_minutes: o.limit_minutes,
      window_from: o.window_from, window_to: o.window_to, note: o.note || null,
    }, { onConflict: 'child_id,local_date' }));
  },
  async deleteOverride(childId, localDate) {
    unwrap(await sb.from('schedule_overrides').delete()
      .eq('child_id', childId).eq('local_date', localDate));
  },

  // ---------------- devices ----------------
  async listDevices() {
    const [devices, flags, accounts] = await Promise.all([
      sb.from('devices').select(DEVICE_COLUMNS).order('name').then(unwrap),
      sb.from('v_device_flags').select('*').then(unwrap),
      sb.from('device_accounts')
        .select('id,device_id,windows_sid,windows_user,child_id,last_seen_at')
        .order('windows_user').then(unwrap),
    ]);
    const flagsById = new Map(flags.map((f) => [f.device_id, f]));
    return devices.map((d) => ({
      ...d,
      flags: flagsById.get(d.id) || {},
      accounts: accounts.filter((a) => a.device_id === d.id),
    }));
  },
  async createDevice(name) {
    const rows = unwrap(await sb.rpc('admin_create_device', { p_name: name }));
    return rows[0]; // { device_id, code }
  },
  async resetDeviceCode(deviceId) {
    return unwrap(await sb.rpc('admin_reset_device_code', { p_device_id: deviceId }));
  },
  async updateDevice(id, patch) {
    unwrap(await sb.from('devices').update(patch).eq('id', id));
  },
  async deleteDevice(id) {
    unwrap(await sb.from('devices').delete().eq('id', id));
  },
  // ---------------- time requests ----------------
  // Pending ones plus everything decided in the last 24 hours.
  async listRequests() {
    const since = new Date(Date.now() - 24 * 3600e3).toISOString();
    const [pending, recent] = await Promise.all([
      sb.from('time_requests').select('id,child_id,device_id,minutes,reason,status,granted_minutes,created_at,decided_at')
        .eq('status', 'pending').order('created_at').then(unwrap),
      sb.from('time_requests').select('id,child_id,device_id,minutes,reason,status,granted_minutes,created_at,decided_at')
        .neq('status', 'pending').gte('decided_at', since).order('decided_at', { ascending: false }).then(unwrap),
    ]);
    return { pending, recent };
  },
  async decideRequest(id, approve, minutes, localDateStr) {
    unwrap(await sb.rpc('admin_decide_request', {
      p_id: id, p_approve: approve, p_minutes: approve ? minutes : null, p_local_date: localDateStr,
    }));
  },

  async setAccountChild(accountId, childId) {
    unwrap(await sb.from('device_accounts').update({ child_id: childId }).eq('id', accountId));
  },
};
