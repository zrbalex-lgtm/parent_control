// Small DOM and formatting helpers shared by all views.

// h('div', { class: 'x', onclick: fn }, child1, 'text', [more])
export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k.startsWith('on') && typeof v === 'function') {
      el.addEventListener(k.slice(2), v);
    } else if (k === 'class') {
      el.className = v;
    } else if (k === 'style' && typeof v === 'object') {
      Object.assign(el.style, v);
    } else if (k === 'value') {
      el.value = v;
    } else if (v === true) {
      el.setAttribute(k, '');
    } else {
      el.setAttribute(k, v);
    }
  }
  append(el, children);
  return el;
}

function append(el, children) {
  for (const c of children) {
    if (c === null || c === undefined || c === false) continue;
    if (Array.isArray(c)) append(el, c);
    else el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
}

// ---------------- toast ----------------
let toastTimer;
export function toast(message, kind = 'info') {
  let el = document.getElementById('toast');
  if (!el) {
    el = h('div', { id: 'toast', role: 'status', 'aria-live': 'polite' });
    document.body.append(el);
  }
  el.textContent = message;
  el.className = `toast show ${kind}`;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), kind === 'error' ? 5000 : 2500);
}

export function showError(err) {
  console.error(err);
  toast(humanError(err), 'error');
}

export function humanError(err) {
  const msg = err?.message || String(err);
  if (/Invalid login credentials/i.test(msg)) return 'Неверный email или пароль';
  if (/Failed to fetch|NetworkError|Load failed/i.test(msg)) return 'Нет связи с сервером';
  if (/JWT expired/i.test(msg)) return 'Сессия истекла, войди заново';
  if (/forbidden|permission denied|42501/i.test(msg)) return 'Нет доступа';
  return msg;
}

// ---------------- modal ----------------
// Opens a bottom sheet. build(close) returns content nodes; resolves with close(value).
export function modal(title, build) {
  return new Promise((resolve) => {
    const prevFocus = document.activeElement;
    const close = (value) => {
      document.removeEventListener('keydown', onKey);
      backdrop.classList.remove('open');
      setTimeout(() => backdrop.remove(), 180);
      prevFocus?.focus?.();
      resolve(value);
    };
    const onKey = (e) => { if (e.key === 'Escape') close(undefined); };
    const sheet = h('div', { class: 'sheet', role: 'dialog', 'aria-modal': 'true', 'aria-label': title },
      h('div', { class: 'sheet-head' },
        h('h2', {}, title),
        h('button', { class: 'icon-btn', 'aria-label': 'Закрыть', onclick: () => close(undefined) }, '✕')),
      h('div', { class: 'sheet-body' }, build(close)));
    const backdrop = h('div', {
      class: 'backdrop',
      onclick: (e) => { if (e.target === backdrop) close(undefined); },
    }, sheet);
    document.body.append(backdrop);
    document.addEventListener('keydown', onKey);
    requestAnimationFrame(() => {
      backdrop.classList.add('open');
      sheet.querySelector('input, select, textarea, .primary')?.focus();
    });
  });
}

export function confirmDialog(title, text, { okText = 'Удалить', danger = true } = {}) {
  return modal(title, (close) => [
    h('p', { class: 'muted' }, text),
    h('div', { class: 'actions' },
      h('button', { class: 'btn ghost', onclick: () => close(false) }, 'Отмена'),
      h('button', { class: `btn ${danger ? 'danger' : 'primary'}`, onclick: () => close(true) }, okText)),
  ]).then((v) => v === true);
}

// Runs an async action with the button disabled; reports errors as toasts.
export async function busy(button, fn) {
  if (button) button.disabled = true;
  try {
    return await fn();
  } catch (err) {
    showError(err);
    return undefined;
  } finally {
    if (button) button.disabled = false;
  }
}

// ---------------- formatting ----------------
export const WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
export const WEEKDAYS_LONG = ['Понедельник', 'Вторник', 'Среда', 'Четверг', 'Пятница', 'Суббота', 'Воскресенье'];

export function fmtMinutes(m) {
  if (m === null || m === undefined) return '∞';
  const hh = Math.floor(m / 60);
  const mm = String(m % 60).padStart(2, '0');
  return `${hh}:${mm}`;
}

// Local calendar date as YYYY-MM-DD (not UTC).
export function localDate(d = new Date()) {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

export function fmtDate(iso) {
  const d = new Date(`${iso}T00:00:00`);
  const wd = WEEKDAYS[(d.getDay() + 6) % 7];
  return `${wd}, ${d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' })}`;
}

export function fmtTime(ts) {
  return new Date(ts).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}

export function fmtAgo(ts) {
  if (!ts) return 'никогда';
  const sec = Math.max(0, (Date.now() - new Date(ts).getTime()) / 1000);
  if (sec < 90) return 'только что';
  const min = Math.round(sec / 60);
  if (min < 60) return `${min} мин назад`;
  const hrs = Math.round(min / 60);
  if (hrs < 24) return `${hrs} ч назад`;
  return new Date(ts).toLocaleString('ru-RU', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
}

// Half-hour slots for time pickers: "00:00" .. "24:00".
export function timeSlots() {
  const out = [];
  for (let m = 0; m <= 24 * 60; m += 30) {
    out.push(`${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`);
  }
  return out;
}

// Time picker: half-hour slots, keeps any non-slot value already stored.
export function timeSelect(value, { allowStart = true, allowEnd = true, label } = {}) {
  const slots = timeSlots().filter((t) => (allowStart || t !== '00:00') && (allowEnd || t !== '24:00'));
  if (value && !slots.includes(value)) {
    slots.push(value);
    slots.sort();
  }
  return h('select', { class: 'time', 'aria-label': label },
    slots.map((t) => h('option', { value: t, selected: t === value }, t)));
}

export const CHILD_COLORS = ['#4f8cff', '#3ecf8e', '#f5a524', '#f25f8c', '#a77bf3', '#2dc7d6', '#ff7a45', '#9aa3b2'];

export function avatar(child, size = 'md') {
  return h('span', { class: `avatar ${size}`, style: { background: child.color }, 'aria-hidden': 'true' },
    child.avatar || (child.name || '?').slice(0, 1).toUpperCase());
}
