// Home: pending time requests (dashboard with live status comes in a later step).
import { h, busy, toast, avatar, fmtAgo, localDate } from '../ui.js';

const GRANT_OPTIONS = [15, 30, 60];

export async function render({ api }) {
  const container = h('div', { class: 'stack' });

  async function refresh() {
    const [{ pending, recent }, children, devices] = await Promise.all([
      api.listRequests(), api.listChildren(), api.listDevices(),
    ]);
    const childById = new Map(children.map((c) => [c.id, c]));
    const deviceName = (id) => devices.find((d) => d.id === id)?.name;

    const pendingCard = (r) => {
      const child = childById.get(r.child_id) || { name: '?', color: '#9aa3b2' };
      const options = [...new Set([r.minutes, ...GRANT_OPTIONS])].sort((a, b) => a - b);
      const approve = (minutes) => (e) => busy(e.currentTarget, async () => {
        await api.decideRequest(r.id, true, minutes, localDate());
        toast(`${child.name}: добавлено ${minutes} мин`, 'ok');
        await refresh();
      });
      return h('li', { class: 'card request' },
        h('div', { class: 'row' },
          avatar(child),
          h('div', { class: 'grow' },
            h('div', { class: 'strong' }, `${child.name} просит ${r.minutes} мин`),
            h('div', { class: 'muted small' },
              [fmtAgo(r.created_at), deviceName(r.device_id)].filter(Boolean).join(' · ')))),
        r.reason ? h('p', { class: 'reason' }, `«${r.reason}»`) : null,
        h('div', { class: 'grant' },
          options.map((m) => h('button', {
            class: `btn ${m === r.minutes ? 'primary' : 'ghost'} small`,
            onclick: approve(m),
          }, `+${m}`)),
          h('span', { class: 'grow' }),
          h('button', {
            class: 'btn danger ghost small',
            onclick: (e) => busy(e.currentTarget, async () => {
              await api.decideRequest(r.id, false);
              toast('Запрос отклонён', 'ok');
              await refresh();
            }),
          }, 'Отклонить')));
    };

    const recentRow = (r) => {
      const child = childById.get(r.child_id) || { name: '?', color: '#9aa3b2' };
      return h('li', { class: 'row' },
        avatar(child, 'sm'),
        h('span', { class: 'grow' }, `${child.name}: ${r.minutes} мин`),
        h('span', { class: `small ${r.status === 'approved' ? 'ok-text' : 'muted'}` },
          r.status === 'approved' ? `добавлено ${r.granted_minutes}` : 'отклонён'),
        h('span', { class: 'muted small' }, fmtAgo(r.decided_at)));
    };

    container.replaceChildren(...[
      h('div', { class: 'section-head' },
        h('h2', {}, 'Запросы времени'),
        h('button', { class: 'btn ghost small', onclick: (e) => busy(e.currentTarget, refresh) }, 'Обновить')),
      pending.length
        ? h('ul', { class: 'list' }, pending.map(pendingCard))
        : h('div', { class: 'card empty' }, h('p', { class: 'muted' }, 'Новых запросов нет.')),
      recent.length
        ? h('section', { class: 'card' },
            h('h2', {}, 'За сутки'),
            h('ul', { class: 'list' }, recent.map(recentRow)))
        : null,
    ].filter(Boolean));
  }

  await refresh();
  return container;
}
