// Home: daily access (when "locked by default" is on) and pending requests.
import { h, busy, toast, avatar, fmtAgo, localDate } from '../ui.js';

const GRANT_OPTIONS = [15, 30, 60];

export async function render({ api }) {
  const container = h('div', { class: 'stack' });

  async function refresh() {
    const today = localDate();
    const [{ pending, recent }, children, devices, settings, dash] = await Promise.all([
      api.listRequests(), api.listChildren(), api.listDevices(), api.getSettings(), api.dashboard(today),
    ]);
    const childById = new Map(children.map((c) => [c.id, c]));
    const deviceName = (id) => devices.find((d) => d.id === id)?.name;
    const childOf = (id) => childById.get(id) || { name: '?', color: '#9aa3b2' };

    // ---------- daily access ----------
    const accessCard = () => {
      const rows = (dash?.children || []).map((c) => {
        const open = !!c.unlocked;
        return h('li', { class: 'row access-row' },
          avatar(c),
          h('div', { class: 'grow' },
            h('div', { class: 'strong' }, c.name),
            h('div', { class: `small ${open ? 'ok-text' : 'warn-text'}` }, open ? 'Доступ открыт' : 'Доступ закрыт')),
          h('button', {
            class: `btn ${open ? 'ghost' : 'primary'} small`,
            onclick: (e) => busy(e.currentTarget, async () => {
              if (open) await api.closeAccess(c.id, today);
              else await api.openAccess(c.id, today);
              toast(open ? `${c.name}: доступ закрыт` : `${c.name}: доступ открыт`, 'ok');
              await refresh();
            }),
          }, open ? 'Закрыть' : 'Открыть'));
      });
      return h('section', { class: 'card', 'aria-labelledby': 'access-title' },
        h('div', { class: 'section-head' }, h('h2', { id: 'access-title' }, 'Доступ сегодня')),
        h('p', { class: 'muted small' }, 'Включено «Закрыт по умолчанию»: компьютер откроется после «Открыть», дальше — по расписанию.'),
        rows.length ? h('ul', { class: 'list' }, rows) : h('p', { class: 'muted' }, 'Детей пока нет.'));
    };

    // ---------- requests ----------
    const unlockCard = (r, child) => h('li', { class: 'card request' },
      h('div', { class: 'row' },
        avatar(child),
        h('div', { class: 'grow' },
          h('div', { class: 'strong' }, `${child.name} просит открыть доступ`),
          h('div', { class: 'muted small' },
            [fmtAgo(r.created_at), deviceName(r.device_id)].filter(Boolean).join(' · ')))),
      r.reason ? h('p', { class: 'reason' }, `«${r.reason}»`) : null,
      h('div', { class: 'grant' },
        h('button', {
          class: 'btn primary',
          onclick: (e) => busy(e.currentTarget, async () => {
            await api.decideRequest(r.id, true, null, localDate());
            toast(`${child.name}: доступ открыт`, 'ok');
            await refresh();
          }),
        }, 'Открыть доступ'),
        denyButton(r)));

    const denyButton = (r) => h('button', {
      class: 'btn danger ghost',
      onclick: (e) => busy(e.currentTarget, async () => {
        await api.decideRequest(r.id, false);
        toast('Запрос отклонён', 'ok');
        await refresh();
      }),
    }, 'Отклонить');

    const timeCard = (r, child) => {
      const options = [...new Set([r.minutes, ...GRANT_OPTIONS])].sort((a, b) => a - b);
      let chosen = r.minutes;
      const confirm = h('button', { class: 'btn primary' }, '');
      const syncLabel = () => { confirm.textContent = `Подтвердить +${chosen} мин`; };
      const chips = options.map((m) => h('button', {
        class: 'chip-toggle amount', 'aria-pressed': String(m === chosen), 'aria-label': `${m} минут`,
        onclick: () => {
          chosen = m;
          chips.forEach((c, i) => c.setAttribute('aria-pressed', String(options[i] === m)));
          syncLabel();
        },
      }, `${m} мин`));
      syncLabel();
      confirm.addEventListener('click', (e) => busy(e.currentTarget, async () => {
        await api.decideRequest(r.id, true, chosen, localDate());
        toast(`${child.name}: добавлено ${chosen} мин`, 'ok');
        await refresh();
      }));

      return h('li', { class: 'card request' },
        h('div', { class: 'row' },
          avatar(child),
          h('div', { class: 'grow' },
            h('div', { class: 'strong' }, `${child.name} просит ${r.minutes} мин`),
            h('div', { class: 'muted small' },
              [fmtAgo(r.created_at), deviceName(r.device_id)].filter(Boolean).join(' · ')))),
        r.reason ? h('p', { class: 'reason' }, `«${r.reason}»`) : null,
        h('div', { class: 'amounts', role: 'group', 'aria-label': 'Сколько добавить' }, chips),
        h('div', { class: 'grant' }, confirm, denyButton(r)));
    };

    const pendingCard = (r) => (r.kind === 'unlock' ? unlockCard : timeCard)(r, childOf(r.child_id));

    const recentRow = (r) => {
      const child = childOf(r.child_id);
      const what = r.kind === 'unlock' ? 'открыть доступ' : `${r.minutes} мин`;
      const result = r.status !== 'approved' ? 'отклонён'
        : r.kind === 'unlock' ? 'открыт' : `добавлено ${r.granted_minutes}`;
      return h('li', { class: 'row' },
        avatar(child, 'sm'),
        h('div', { class: 'grow' },
          h('div', {}, `${child.name}: ${what}`),
          h('div', { class: 'small' },
            h('span', { class: r.status === 'approved' ? 'ok-text' : 'muted' }, result),
            h('span', { class: 'muted' }, ` · ${fmtAgo(r.decided_at)}`))));
    };

    container.replaceChildren(...[
      settings.locked_by_default ? accessCard() : null,
      h('div', { class: 'section-head' },
        h('h2', {}, 'Запросы'),
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
