// History: screen time per day, top apps and sessions for one child.
// Tapping a day in the chart narrows apps and sessions to that day; tapping an app shows its window titles.
import { h, toast, avatar, localDate, fmtDuration, fmtTime, fmtDate, WEEKDAYS } from '../ui.js';
import { editRule, suggestName } from './groups.js';

const PERIODS = [{ days: 7, label: '7 дней' }, { days: 30, label: '30 дней' }];
const BROWSER_EXES = /^(chrome|msedge|firefox|opera|brave|vivaldi|browser)\.exe$/i;
// Labels the server makes up itself; there is nothing to group for them.
const SYNTHETIC = /^(Нет данных об окне|Экранное время \(блокировка, запросы\))$/;

// Kept across visits to the tab.
let selectedChildId = null;
let periodDays = 7;

const addDays = (d, n) => { const x = new Date(d); x.setDate(x.getDate() + n); return x; };

function dayChart(days, selected, onPick) {
  const max = Math.max(60, ...days.map((d) => Math.max(d.minutes, d.allowed ?? 0)));
  const many = days.length > 10;
  const cols = days.map((d, i) => {
    const date = new Date(`${d.date}T00:00:00`);
    const over = d.allowed != null && d.minutes > d.allowed;
    const label = many
      ? (i % 5 === 0 || i === days.length - 1 || d.date === selected ? String(date.getDate()) : '')
      : WEEKDAYS[(date.getDay() + 6) % 7];
    const tip = `${fmtDate(d.date)}: ${fmtDuration(d.minutes)}${d.allowed != null ? ` из ${fmtDuration(d.allowed)}` : ''}`;
    const isSel = d.date === selected;
    return h('button', {
      type: 'button',
      class: `col${isSel ? ' selected' : ''}${selected && !isSel ? ' dimmed' : ''}`,
      title: tip, 'aria-label': tip, 'aria-pressed': String(isSel),
      onclick: () => onPick(isSel ? null : d.date),
    },
      h('div', { class: 'col-plot' },
        d.allowed != null ? h('div', { class: 'col-limit', style: { bottom: `${(d.allowed / max) * 100}%` } }) : null,
        h('div', { class: `col-bar${over ? ' over' : ''}`, style: { height: `${(d.minutes / max) * 100}%` } })),
      h('div', { class: 'col-label', 'aria-hidden': 'true' }, label));
  });
  return h('div', { class: `chart${many ? ' many' : ''}` }, cols);
}

/** A rule that would catch this report line: browser sites by window title, other apps by exe. */
function ruleFor(a) {
  const site = BROWSER_EXES.test(a.exe || '') && a.app.includes(': ') ? a.app.slice(a.app.indexOf(': ') + 2) : null;
  if (site) return { group_name: site, match_field: 'title', match_type: 'contains', pattern: site };
  if (a.exe) return { group_name: suggestName({ product: a.app.replace(/^Windows: /, ''), exe: a.exe }), match_field: 'exe', match_type: 'contains', pattern: a.exe };
  return { group_name: a.app, match_field: 'product', match_type: 'contains', pattern: a.app };
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

  let selectedDay = null; // YYYY-MM-DD or null = whole period
  let period = null;      // last period answer (chart + summary)

  function renderControls() {
    chips.replaceChildren(...children.map((c) => h('button', {
      class: 'chip', role: 'tab', 'aria-selected': String(c.id === selectedChildId),
      onclick: () => { if (c.id !== selectedChildId) { selectedChildId = c.id; selectedDay = null; renderControls(); load(); } },
    }, avatar(c, 'sm'), c.name)));
    periods.replaceChildren(...PERIODS.map((p) => h('button', {
      class: p.days === periodDays ? 'active' : '', 'aria-pressed': String(p.days === periodDays),
      onclick: () => { if (p.days !== periodDays) { periodDays = p.days; selectedDay = null; renderControls(); load(); } },
    }, p.label)));
  }

  const range = () => {
    const to = new Date();
    return { from: localDate(addDays(to, -(periodDays - 1))), to: localDate(to) };
  };

  let seq = 0;
  /** Loads the period (chart) and, when a day is picked, that day's apps and sessions. */
  async function load({ keepPeriod = false } = {}) {
    const my = ++seq;
    if (!keepPeriod) body.replaceChildren(h('div', { class: 'loading' }, 'Загрузка…'));
    const { from, to } = range();
    let details;
    try {
      if (!keepPeriod || !period) period = await api.history(selectedChildId, from, to);
      details = selectedDay ? await api.history(selectedChildId, selectedDay, selectedDay) : period;
    } catch (e) {
      if (my === seq) body.replaceChildren(h('div', { class: 'card' }, h('p', {}, e.message || String(e))));
      return;
    }
    if (my !== seq) return;
    renderBody(period, details, selectedDay ? { from: selectedDay, to: selectedDay } : { from, to });
  }

  function pickDay(day) {
    selectedDay = day;
    load({ keepPeriod: true });
  }

  function renderBody(periodData, details, detailRange) {
    const { days } = periodData;
    const { apps, sessions } = details;
    const total = days.reduce((a, d) => a + d.minutes, 0);
    const over = days.filter((d) => d.allowed != null && d.minutes > d.allowed).length;
    const dayInfo = selectedDay ? days.find((d) => d.date === selectedDay) : null;

    // ---------- summary + chart ----------
    const summary = h('section', { class: 'card stack', 'aria-labelledby': 'hist-title' },
      h('h2', { id: 'hist-title' }, 'Экранное время'),
      h('div', { class: 'stats' },
        h('div', {}, h('div', { class: 'stat' }, fmtDuration(total)), h('div', { class: 'muted small' }, 'всего')),
        h('div', {}, h('div', { class: 'stat' }, fmtDuration(Math.round(total / days.length))), h('div', { class: 'muted small' }, 'в среднем за день')),
        h('div', {}, h('div', { class: `stat${over ? ' warn-text' : ''}` }, String(over)), h('div', { class: 'muted small' }, 'дней сверх лимита'))),
      total ? dayChart(days, selectedDay, pickDay) : h('p', { class: 'muted' }, 'За этот период за компьютером не сидел.'),
      total ? h('div', { class: 'legend muted small' },
        h('span', {}, h('i', { class: 'swatch-bar' }), 'время'),
        h('span', {}, h('i', { class: 'swatch-limit' }), 'лимит с бонусами'),
        h('span', {}, h('i', { class: 'swatch-bar over' }), 'сверх лимита')) : null,
      total && !selectedDay ? h('p', { class: 'muted small' }, 'Нажми на день, чтобы посмотреть только его.') : null);

    // Which slice the cards below show.
    const scope = selectedDay
      ? h('div', { class: 'day-scope' },
          h('span', { class: 'grow' },
            h('span', { class: 'strong' }, fmtDate(selectedDay)),
            dayInfo ? h('span', { class: 'muted' }, ` · ${fmtDuration(dayInfo.minutes)}${dayInfo.allowed != null ? ` из ${fmtDuration(dayInfo.allowed)}` : ''}`) : null),
          h('button', { class: 'btn ghost small', onclick: () => pickDay(null) }, 'Весь период'))
      : null;

    // ---------- top apps ----------
    const maxApp = Math.max(1, ...apps.map((a) => a.minutes));
    const appRow = (a) => {
      const titles = h('ul', { class: 'titles', hidden: true });
      let loaded = false;
      const toggle = h('button', {
        type: 'button', class: 'app-name', 'aria-expanded': 'false', title: 'Показать окна',
        onclick: async () => {
          const open = toggle.getAttribute('aria-expanded') !== 'true';
          toggle.setAttribute('aria-expanded', String(open));
          titles.hidden = !open;
          if (!open || loaded) return;
          loaded = true;
          titles.replaceChildren(h('li', { class: 'muted small' }, 'Загрузка…'));
          try {
            const list = await api.appTitles(selectedChildId, detailRange.from, detailRange.to, a.app);
            titles.replaceChildren(...(list.length
              ? list.map((t) => h('li', {}, h('span', { class: 'grow' }, t.title), h('span', { class: 'muted nowrap' }, fmtDuration(t.minutes))))
              : [h('li', { class: 'muted small' }, 'Заголовков нет.')]));
          } catch (e) {
            loaded = false;
            titles.replaceChildren(h('li', { class: 'form-error' }, e.message || String(e)));
          }
        },
      }, h('span', { class: 'chev', 'aria-hidden': 'true' }, '›'), h('span', { class: 'strong' }, a.app));
      return h('li', { class: 'app-row' },
        h('div', { class: 'app-head' },
          toggle,
          a.grouped || SYNTHETIC.test(a.app) ? null : h('button', {
            class: 'btn ghost small', 'aria-label': `В группу: ${a.app}`,
            onclick: async () => {
              const names = [...new Set((await api.listAppGroups()).map((r) => r.group_name))];
              if (await editRule(api, ruleFor(a), names)) {
                toast('Группа сохранена', 'ok');
                period = null;
                load();
              }
            },
          }, 'В группу'),
          h('span', { class: 'muted small nowrap' }, fmtDuration(a.minutes))),
        h('div', { class: 'meter' }, h('span', { style: { width: `${(a.minutes / maxApp) * 100}%` } })),
        titles);
    };
    const appsCard = h('section', { class: 'card stack', 'aria-labelledby': 'apps-title' },
      h('h2', { id: 'apps-title' }, 'Приложения'),
      scope,
      apps.length ? h('ul', { class: 'list apps' }, apps.map(appRow)) : h('p', { class: 'muted' }, 'Нет данных.'),
      apps.length ? h('p', { class: 'muted small' }, 'Нажми на название — покажу, какие окна были открыты. Браузеры разбиты по сайтам из заголовка окна.') : null);

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
