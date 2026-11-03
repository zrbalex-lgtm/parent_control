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
  'client_version,last_seen_at,clock_skew_sec,created_at,tamper_locked_since,config_state,auto_update';

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
      await sb.from('children').select('id,name,color,avatar,sort_order,locked_by_default')
        .order('sort_order').order('name'));
  },
  async saveChild(child) {
    const row = {
      name: child.name, color: child.color, avatar: child.avatar || null,
      sort_order: child.sort_order ?? 0, locked_by_default: !!child.locked_by_default,
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
        .select('id,device_id,windows_sid,windows_user,child_id,last_seen_at,reviewed_at')
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
  // ---------------- settings & daily access ----------------
  async getSettings() {
    const rows = unwrap(await sb.from('settings').select('locked_by_default,tamper_action').eq('id', 1));
    return rows[0] || { locked_by_default: false, tamper_action: 'lock' };
  },
  async setLockedByDefault(on) {
    unwrap(await sb.from('settings').update({ locked_by_default: on, updated_at: new Date().toISOString() }).eq('id', 1));
  },
  async setTamperAction(action) {
    unwrap(await sb.from('settings').update({ tamper_action: action, updated_at: new Date().toISOString() }).eq('id', 1));
  },
  async dashboard(localDateStr) {
    return unwrap(await sb.rpc('admin_dashboard', { p_local_date: localDateStr }));
  },
  async openAccess(childId, localDateStr) {
    unwrap(await sb.from('day_unlocks').upsert({ child_id: childId, local_date: localDateStr }, { onConflict: 'child_id,local_date' }));
  },
  async closeAccess(childId, localDateStr) {
    unwrap(await sb.from('day_unlocks').delete().eq('child_id', childId).eq('local_date', localDateStr));
  },

  // ---------------- quick actions ----------------
  async addBonus(childId, localDateStr, minutes, reason) {
    unwrap(await sb.from('bonuses').insert({ child_id: childId, local_date: localDateStr, minutes, reason: reason || null }));
  },
  // Lock on all PCs of the child from `from` until `until` (Date objects).
  async lockChild(childId, from, until) {
    unwrap(await sb.from('commands').insert({
      child_id: childId, kind: 'lock', lock_from: from.toISOString(), lock_until: until.toISOString(),
    }));
  },
  async cancelLocks(lockIds) {
    if (!lockIds.length) return;
    unwrap(await sb.from('commands').update({ cancelled_at: new Date().toISOString() }).in('id', lockIds));
  },
  async sendMessage(childId, text) {
    unwrap(await sb.from('commands').insert({ child_id: childId, kind: 'message', message: text }));
  },

  // ---------------- time requests ----------------
  // Pending ones plus everything decided in the last 24 hours.
  async listRequests() {
    const since = new Date(Date.now() - 24 * 3600e3).toISOString();
    const [pending, recent] = await Promise.all([
      sb.from('time_requests').select('id,child_id,device_id,kind,minutes,reason,status,granted_minutes,created_at,decided_at')
        .eq('status', 'pending').order('created_at').then(unwrap),
      sb.from('time_requests').select('id,child_id,device_id,kind,minutes,reason,status,granted_minutes,created_at,decided_at,decided_by_name')
        .neq('status', 'pending').gte('decided_at', since).order('decided_at', { ascending: false }).then(unwrap),
    ]);
    return { pending, recent };
  },
  async decideRequest(id, approve, minutes, localDateStr) {
    unwrap(await sb.rpc('admin_decide_request', {
      p_id: id, p_approve: approve, p_minutes: approve ? minutes : null, p_local_date: localDateStr,
    }));
  },

  // ---------------- history & app groups ----------------
  async history(childId, fromStr, toStr) {
    return unwrap(await sb.rpc('admin_history', { p_child: childId, p_from: fromStr, p_to: toStr }));
  },
  async ungroupedApps(days = 7) {
    return unwrap(await sb.rpc('admin_ungrouped', { p_days: days }));
  },
  async previewAppRule(field, type, pattern) {
    return unwrap(await sb.rpc('admin_preview_app_rule', { p_field: field, p_type: type, p_pattern: pattern }));
  },
  async listAppGroups() {
    return unwrap(await sb.from('app_groups')
      .select('id,group_name,match_field,match_type,pattern,priority,is_enabled')
      .order('group_name').order('priority'));
  },
  async saveAppGroup(rule) {
    const row = {
      group_name: rule.group_name.trim(), match_field: rule.match_field, match_type: rule.match_type,
      pattern: rule.pattern.trim(), priority: rule.priority ?? 100, is_enabled: rule.is_enabled ?? true,
    };
    if (rule.id) unwrap(await sb.from('app_groups').update(row).eq('id', rule.id));
    else unwrap(await sb.from('app_groups').insert(row));
  },
  async deleteAppGroup(id) {
    unwrap(await sb.from('app_groups').delete().eq('id', id));
  },

  // ---------------- telegram ----------------
  async listTgChats() {
    return unwrap(await sb.from('tg_chats').select('chat_id,title,linked_by,linked_at').order('linked_at'));
  },
  async removeTgChat(chatId) {
    unwrap(await sb.from('tg_chats').delete().eq('chat_id', chatId));
  },
  async tgLinkCode() {
    return unwrap(await sb.rpc('admin_tg_link_code'));
  },

  // ---------------- security (tamper events) ----------------
  async listTamperEvents({ onlyUnhandled = false, deviceId = null, limit = 100 } = {}) {
    let q = sb.from('tamper_events')
      .select('id,device_id,machine_name,occurred_at,received_at,type,details,windows_user,verified,handled_at');
    if (onlyUnhandled) q = q.is('handled_at', null);
    if (deviceId) q = q.eq('device_id', deviceId);
    return unwrap(await q.order('occurred_at', { ascending: false }).limit(limit));
  },
  async countUnhandledTamper() {
    const rows = unwrap(await sb.from('tamper_events').select('id').is('handled_at', null).limit(100));
    return rows.length;
  },
  async markTamperHandled(ids) {
    if (!ids.length) return;
    unwrap(await sb.rpc('admin_mark_tamper_handled', { p_ids: ids }));
  },
  async clearTamperLock(deviceId) {
    unwrap(await sb.rpc('admin_clear_tamper_lock', { p_device_id: deviceId }));
  },

  async setAccountChild(accountId, childId) {
    unwrap(await sb.from('device_accounts').update({ child_id: childId }).eq('id', accountId));
  },
};
