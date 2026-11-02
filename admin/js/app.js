// App entry: auth gate, hash router, bottom navigation.
import { h, toast, busy, humanError } from './ui.js';

const ROUTES = {
  home: { title: 'Главная', icon: '⌂', load: () => import('./views/home.js') },
  history: { title: 'История', icon: '▤', load: () => import('./views/history.js') },
  children: { title: 'Дети', icon: '☺', load: () => import('./views/children.js') },
  schedule: { title: 'Расписание', icon: '◷', load: () => import('./views/schedule.js') },
  devices: { title: 'Устройства', tab: 'ПК', icon: '▭', load: () => import('./views/devices.js') },
  security: { title: 'Безопасность', tab: 'Защита', icon: '◈', load: () => import('./views/security.js') },
  // Not in the tab bar: opened from History.
  groups: { title: 'Группы приложений', icon: '', hidden: true, parent: 'history', load: () => import('./views/groups.js') },
};
const DEFAULT_ROUTE = 'home';

const root = document.getElementById('app');
let api;
let renderSeq = 0;

function fatal(message) {
  root.replaceChildren(h('div', { class: 'center-screen' },
    h('div', { class: 'card narrow' },
      h('h1', {}, 'Ошибка запуска'),
      h('p', { class: 'muted' }, message),
      h('button', { class: 'btn primary', onclick: () => location.reload() }, 'Обновить'))));
}

function currentRoute() {
  const name = location.hash.replace(/^#\/?/, '').split('?')[0];
  return ROUTES[name] ? name : DEFAULT_ROUTE;
}

// ---------------- login ----------------
function renderLogin() {
  const email = h('input', { type: 'email', id: 'email', autocomplete: 'username', required: true, inputmode: 'email' });
  const password = h('input', { type: 'password', id: 'password', autocomplete: 'current-password', required: true });
  const error = h('p', { class: 'form-error', role: 'alert' });
  const submit = h('button', { class: 'btn primary block', type: 'submit' }, 'Войти');
  const form = h('form', {
    class: 'card narrow',
    onsubmit: async (e) => {
      e.preventDefault();
      error.textContent = '';
      submit.disabled = true;
      try {
        await api.signIn(email.value.trim(), password.value);
        // onAuthChange re-renders
      } catch (err) {
        error.textContent = humanError(err);
        submit.disabled = false;
      }
    },
  },
    h('div', { class: 'brand' }, h('span', { class: 'brand-mark', 'aria-hidden': 'true' }, '◷'), 'Экранное время'),
    h('label', { for: 'email' }, 'Email'), email,
    h('label', { for: 'password' }, 'Пароль'), password,
    error, submit);
  root.replaceChildren(h('div', { class: 'center-screen' }, form));
  email.focus();
}

function renderNoAccess(session) {
  root.replaceChildren(h('div', { class: 'center-screen' },
    h('div', { class: 'card narrow' },
      h('h1', {}, 'Нет доступа'),
      h('p', { class: 'muted' }, `Учётка ${session.user.email} не входит в администраторы родительского контроля.`),
      h('button', { class: 'btn ghost block', onclick: () => api.signOut() }, 'Выйти'))));
}

// ---------------- shell ----------------
function renderShell() {
  const nav = h('nav', { class: 'tabbar', 'aria-label': 'Разделы' },
    Object.entries(ROUTES).filter(([, r]) => !r.hidden).map(([name, r]) =>
      h('a', { href: `#/${name}`, 'data-route': name, class: 'tab' },
        h('span', { class: 'tab-icon', 'aria-hidden': 'true' }, r.icon,
          name === 'security' ? h('span', { class: 'tab-badge', id: 'security-badge', hidden: true }) : null),
        h('span', {}, r.tab || r.title))));
  const header = h('header', { class: 'topbar' },
    h('h1', { id: 'page-title' }, ''),
    h('button', {
      class: 'btn ghost small',
      onclick: (e) => busy(e.currentTarget, () => api.signOut()),
    }, 'Выйти'));
  const main = h('main', { id: 'view', tabindex: '-1' });
  root.replaceChildren(h('div', { class: 'shell' }, header, main, nav));
}

async function renderRoute() {
  const name = currentRoute();
  const route = ROUTES[name];
  const seq = ++renderSeq;
  document.getElementById('page-title').textContent = route.title;
  document.title = `${route.title} · Экранное время`;
  const tabName = route.parent || name;
  for (const a of document.querySelectorAll('.tab')) {
    a.classList.toggle('active', a.dataset.route === tabName);
    if (a.dataset.route === tabName) a.setAttribute('aria-current', 'page');
    else a.removeAttribute('aria-current');
  }
  const view = document.getElementById('view');
  view.replaceChildren(h('div', { class: 'loading' }, 'Загрузка…'));
  try {
    const mod = await route.load();
    if (seq !== renderSeq) return;
    const content = await mod.render({ api, navigate: (r) => { location.hash = `#/${r}`; } });
    if (seq !== renderSeq) return;
    view.replaceChildren(content);
    updateSecurityBadge();
  } catch (err) {
    if (seq !== renderSeq) return;
    view.replaceChildren(h('div', { class: 'card' },
      h('p', {}, humanError(err)),
      h('button', { class: 'btn ghost', onclick: renderRoute }, 'Повторить')));
  }
}

// Number of unread security events on the tab (best effort).
async function updateSecurityBadge() {
  const badge = document.getElementById('security-badge');
  if (!badge) return;
  try {
    const n = await api.countUnhandledTamper();
    badge.hidden = n === 0;
    badge.textContent = n > 99 ? '99+' : String(n);
    badge.parentElement.parentElement.setAttribute('aria-label', n ? `Защита, новых событий: ${n}` : 'Защита');
  } catch {
    badge.hidden = true;
  }
}

let shownFor = null;   // user id the shell was built for
let loginShown = false;

async function onSession(session) {
  if (!session) {
    shownFor = null;
    if (!loginShown) renderLogin();
    loginShown = true;
    return;
  }
  loginShown = false;
  if (shownFor === session.user.id) return; // token refresh, nothing to redraw
  let admin = false;
  try {
    admin = await api.isAdmin();
  } catch (err) {
    toast(humanError(err), 'error');
  }
  if (!admin) {
    shownFor = null;
    renderNoAccess(session);
    return;
  }
  shownFor = session.user.id;
  renderShell();
  renderRoute();
}

async function main() {
  try {
    ({ api } = await import('./api.js'));
  } catch (err) {
    console.error(err);
    fatal('Не удалось загрузить библиотеку Supabase. Проверь интернет и обнови страницу.');
    return;
  }
  window.addEventListener('hashchange', () => { if (shownFor) renderRoute(); });
  window.addEventListener('security-changed', () => { if (shownFor) updateSecurityBadge(); });

  // Auth events can arrive back to back (initial session + getSession);
  // handle them one at a time so the shell is built once.
  let chain = Promise.resolve();
  const handle = (session) => { chain = chain.then(() => onSession(session)); };
  api.onAuthChange(handle);
  handle(await api.getSession());

  if ('serviceWorker' in navigator && location.protocol === 'https:') {
    navigator.serviceWorker.register('./sw.js').catch(() => {});
  }
}

main();
