// History: screen time per day, top apps and sessions for one child.
import { h, toast, avatar, localDate, fmtDuration, fmtTime, fmtDate, WEEKDAYS } from '../ui.js';
import { editRule, suggestName } from './groups.js';

const PERIODS = [{ days: 7, label: '7 дней' }, { days: 30, label: '30 дней' }];

// Kept across visits to the tab.
let selectedChildId = null;
let periodDays = 7;

const addDays = (d, n) => { const x = new Date(d); x.setDate(x.getDate() + n); return x; };

function dayChart(days) {
  const max = Math.max(60, ...days.map((d) => Math.max(d.minutes, d.allowed ?? 0)));
  const many = days.length > 10;
  const cols = days.map((d, i) => {
    const date = new Date(`${d.date}T00:00:00`);
    const over = d.allowed != null && d.minutes > d.allowed;
    const label = many
      ? (i % 5 === 0 || i === days.length - 1 ? String(date.getDate()) : '')
      : WEEKDAYS[(date.getDay() + 6) % 7];
    const tip = `${fmtDate(d.date)}: ${fmtDuration(d.minutes)}${d.allowed != null ? ` из ${fmtDuration(d.allowed)}` : ''}`;
    return h('div', { class: 'col', title: tip, role: 'img', 'aria-label': tip },
      h('div', { class: 'col-plot' },
        d.allowed != null ? h('div', { class: 'col-limit', style: { bottom: `${(d.allowed / max) * 100}%` } }) : null,
        h('div', { class: `col-bar${over ? ' over' : ''}`, style: { height: `${(d.minutes / max) * 100}%` } })),
      h('div', { class: 'col-label', 'aria-hidden': 'true' }, label));
  });
  return h('div', { class: `chart${many ? ' many' : ''}` }, cols);
}

export async function render({ api, navigate }) {
  const children = await api.listChildren();
  if (!children.length) {
    return h('div', { class: 'card empty' },
      h('p', {}, 'Сначала добавь детей.'),
      h('button', { class: 'btn primary', onclick: () => navigate('children') }, 'К списку детей'));
  }
  if (!children.some((c) => c.id === selectedChildId)) selectedChildId = children[0].id;

  const container = h('div', { class: 'stack' });
  const chips = h('div', { class: 'chips', role: 'tablist', 'aria-label': 'Ребёнок' });
  const periods = h('div', { class: 'segmented', role: 'group', 'aria-label': 'Период' });
  const body = h('div', { class: 'stack' });
  container.append(chips, h('div', { class: 'filter-row' }, periods,
    h('span', { class: 'grow' }),
    h('a', { class: 'btn ghost small', href: '#/groups' }, 'Группы приложений')), body);

  function renderControls() {
    chips.replaceChildren(...children.map((c) => h('button', {
      class: 'chip', role: 'tab', 'aria-selected': String(c.id === selectedChildId),
      onclick: () => { if (c.id !== selectedChildId) { selectedChildId = c.id; renderControls(); load(); } },
    }, avatar(c, 'sm'), c.name)));
    periods.replaceChildren(...PERIODS.map((p) => h('button', {
      class: p.days === periodDays ? 'active' : '', 'aria-pressed': String(p.days === periodDays),
      onclick: () => { if (p.days !== periodDays) { periodDays = p.days; renderControls(); load(); } },
    }, p.label)));
  }

  let seq = 0;
  async function load() {
    const my = ++seq;
    body.replaceChildren(h('div', { class: 'loading' }, 'Загрузка…'));
    const to = new Date();
    const from = addDays(to, -(periodDays - 1));
    let data;
    try {
      data = await api.history(selectedChildId, localDate(from), localDate(to));
    } catch (e) {
      if (my === seq) body.replaceChildren(h('div', { class: 'card' }, h('p', {}, e.message || String(e))));
      return;
    }
    if (my !== seq) return;

    const { days, apps, sessions } = data;
    const total = days.reduce((a, d) => a + d.minutes, 0);
    const used = days.filter((d) => d.minutes > 0).length;
    const over = days.filter((d) => d.allowed != null && d.minutes > d.allowed).length;

    // ---------- summary + chart ----------
    const summary = h('section', { class: 'card stack', 'aria-labelledby': 'hist-title' },
      h('h2', { id: 'hist-title' }, 'Экранное время'),
      h('div', { class: 'stats' },
        h('div', {}, h('div', { class: 'stat' }, fmtDuration(total)), h('div', { class: 'muted small' }, 'всего')),
        h('div', {}, h('div', { class: 'stat' }, fmtDuration(Math.round(total / days.length))), h('div', { class: 'muted small' }, 'в среднем за день')),
        h('div', {}, h('div', { class: `stat${over ? ' warn-text' : ''}` }, String(over)), h('div', { class: 'muted small' }, 'дней сверх лимита'))),
      total ? dayChart(days) : h('p', { class: 'muted' }, 'За этот период за компьютером не сидел.'),
      total ? h('div', { class: 'legend muted small' },
        h('span', {}, h('i', { class: 'swatch-bar' }), 'время'),
        h('span', {}, h('i', { class: 'swatch-limit' }), 'лимит с бонусами'),
        h('span', {}, h('i', { class: 'swatch-bar over' }), 'сверх лимита')) : null);

    // ---------- top apps ----------
    const maxApp = Math.max(1, ...apps.map((a) => a.minutes));
    const appRow = (a) => h('li', { class: 'app-row' },
      h('div', { class: 'app-head' },
        h('span', { class: 'strong grow' }, a.app),
        a.grouped || a.app === 'Без окна' ? null : h('button', {
          class: 'btn ghost small', 'aria-label': `В группу: ${a.app}`,
          onclick: async () => {
            const names = [...new Set((await api.listAppGroups()).map((r) => r.group_name))];
            const field = /\.exe$/i.test(a.app) ? 'exe' : 'product';
            if (await editRule(api, { group_name: suggestName({ product: a.app }), match_field: field, match_type: 'contains', pattern: a.app }, names)) {
              toast('Группа сохранена', 'ok');
              load();
            }
          },
        }, 'В группу'),
        h('span', { class: 'muted small nowrap' }, fmtDuration(a.minutes))),
      h('div', { class: 'meter' }, h('span', { style: { width: `${(a.minutes / maxApp) * 100}%` } })));
    const appsCard = h('section', { class: 'card stack', 'aria-labelledby': 'apps-title' },
      h('h2', { id: 'apps-title' }, 'Приложения'),
      apps.length ? h('ul', { class: 'list apps' }, apps.map(appRow)) : h('p', { class: 'muted' }, 'Нет данных.'));

    // ---------- sessions ----------
    const byDay = new Map();
    for (const s of sessions) {
      const key = localDate(new Date(s.started_at));
      if (!byDay.has(key)) byDay.set(key, []);
      byDay.get(key).push(s);
    }
    const sessionsCard = h('section', { class: 'card stack', 'aria-labelledby': 'sess-title' },
      h('h2', { id: 'sess-title' }, 'Сессии'),
      sessions.length
        ? [...byDay].map(([day, list]) => h('div', { class: 'session-day' },
            h('div', { class: 'muted small strong' }, fmtDate(day)),
            h('ul', { class: 'list' }, list.map((s) => h('li', { class: 'row session' },
              h('span', { class: 'nowrap' }, `${fmtTime(s.started_at)}–${fmtTime(s.ended_at)}`),
              h('span', { class: 'grow muted small' }, [s.top_app, s.device_name].filter(Boolean).join(' · ')),
              h('span', { class: 'nowrap strong' }, fmtDuration(s.minutes)))))))
        : h('p', { class: 'muted' }, 'Сессий нет.'),
      sessions.length >= 60 ? h('p', { class: 'muted small' }, 'Показаны последние 60 сессий.') : null);

    body.replaceChildren(summary, appsCard, sessionsCard);
  }

  renderControls();
  await load();
  return container;
}
