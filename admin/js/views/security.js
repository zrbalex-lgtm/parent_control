// Security: tamper events reported by the PCs, tamper lock, what tampering does.
import { h, busy, toast, confirmDialog, fmtAgo, fmtTime } from '../ui.js';

// Must match fn_is_lock_event on the server.
export const LOCK_EVENTS = new Set([
  'config_missing', 'config_restored', 'data_tampered', 'acl_fixed',
  'service_dacl_fixed', 'safeboot_fixed', 'watchdog_fixed', 'clock_changed',
]);

const FILES = { 'device.json': 'регистрация', 'state.json': 'настройки и учёт', 'data folder': 'папка данных' };

/** Human text for one event. */
export function describeEvent(e) {
  const d = e.details || {};
  switch (e.type) {
    case 'config_missing':
      return 'Регистрация программы пропала';
    case 'config_restored': {
      const files = String(d.files || '').split(',').filter(Boolean).map((f) => FILES[f] || f);
      return `Удалены файлы программы (${files.join(', ') || 'данные'}) — восстановлены`;
    }
    case 'data_tampered':
      return 'Изменены данные программы';
    case 'acl_fixed':
      return d.what === 'registry' ? 'Изменены права на настройки в реестре — исправлено'
        : d.what === 'install' ? 'Изменены права на папку программы — исправлено'
        : 'Изменены права на папку данных — исправлено';
    case 'service_dacl_fixed':
      return d.what === 'config' ? 'Изменён запуск службы — исправлено' : 'Изменены права на службу — исправлено';
    case 'safeboot_fixed':
      return 'Отключена работа в безопасном режиме — исправлено';
    case 'watchdog_fixed':
      return d.what === 'task' ? 'Задача-сторож удалена или изменена — восстановлена' : 'Служба была остановлена — запущена снова';
    case 'clock_changed':
      if (d.kind === 'timezone') return `Изменён часовой пояс: ${d.from || '?'} → ${d.to || '?'}`;
      if (d.jump_seconds) {
        const min = Math.round(Number(d.jump_seconds) / 60);
        return `Переведены часы на ${min > 0 ? '+' : ''}${min} мин`;
      }
      return 'Переведены часы';
    case 'tray_unresponsive':
      return 'Значок в трее закрыт больше 5 минут';
    case 'tamper_cleared_locally':
      return 'Блокировку снял администратор прямо на ПК';
    default:
      return e.type;
  }
}

function deviceState(d) {
  if (d.tamper_locked_since) return { kind: 'danger', text: `Заблокирован после вмешательства с ${fmtTime(d.tamper_locked_since)}` };
  if (d.config_state === 'lost') return { kind: 'danger', text: 'Конфиг утерян — нужна переустановка' };
  if (d.config_state === 'restored') return { kind: 'warn', text: 'Конфиг был удалён и восстановлен' };
  return null;
}

export async function render({ api }) {
  const container = h('div', { class: 'stack' });
  const filter = { onlyUnhandled: true, deviceId: '' };

  async function refresh() {
    const [settings, devices, events] = await Promise.all([
      api.getSettings(),
      api.listDevices(),
      api.listTamperEvents({ onlyUnhandled: filter.onlyUnhandled, deviceId: filter.deviceId || null }),
    ]);
    const deviceName = new Map(devices.map((d) => [d.id, d.name]));

    // ---------- what tampering does ----------
    const action = settings.tamper_action || 'lock';
    const option = (value, title, hint) => h('label', { class: `choice${action === value ? ' selected' : ''}` },
      h('input', {
        type: 'radio', name: 'tamper-action', value, checked: action === value,
        onchange: (e) => busy(e.currentTarget, async () => {
          await api.setTamperAction(value);
          toast(value === 'lock' ? 'Будет блокировать' : 'Только оповещения', 'ok');
          await refresh();
        }),
      }),
      h('span', {}, h('span', { class: 'strong' }, title), h('span', { class: 'muted small block' }, hint)));
    const settingsCard = h('section', { class: 'card stack', 'aria-labelledby': 'tamper-action-title' },
      h('h2', { id: 'tamper-action-title' }, 'При вмешательстве'),
      h('div', { class: 'choices', role: 'radiogroup', 'aria-labelledby': 'tamper-action-title' },
        option('lock', 'Блокировать', 'Детские учётки на этом ПК закрыты, пока ты не разблокируешь.'),
        option('notify', 'Только оповещать', 'Событие появится здесь, ограничения работают как обычно.')),
      h('p', { class: 'muted small' }, 'Администраторов Windows блокировка не касается никогда.'));

    // ---------- devices needing attention ----------
    const attention = devices.map((d) => ({ d, st: deviceState(d) })).filter((x) => x.st);
    const deviceCards = attention.length
      ? h('ul', { class: 'list' }, attention.map(({ d, st }) => h('li', { class: 'card device', 'data-device': d.id },
          h('div', { class: 'strong' }, d.name),
          h('div', { class: `flag ${st.kind}` }, st.text),
          d.tamper_locked_since
            ? h('div', { class: 'actions' },
                h('button', { class: 'btn primary', onclick: (e) => unlock(e.currentTarget, d) }, 'Разблокировать'))
            : d.config_state === 'lost'
              ? h('p', { class: 'muted small' }, 'Запусти install.cmd на этом ПК с новым кодом (Устройства → ⋯ → Переустановка).')
              : null)))
      : h('div', { class: 'card ok-banner' }, h('span', { 'aria-hidden': 'true' }, '✓ '), 'Все компьютеры в порядке');

    // ---------- events ----------
    const filterRow = h('div', { class: 'filter-row' },
      h('div', { class: 'segmented', role: 'group', 'aria-label': 'Какие события' },
        h('button', { class: filter.onlyUnhandled ? 'active' : '', 'aria-pressed': String(filter.onlyUnhandled),
          onclick: () => { filter.onlyUnhandled = true; refresh(); } }, 'Новые'),
        h('button', { class: filter.onlyUnhandled ? '' : 'active', 'aria-pressed': String(!filter.onlyUnhandled),
          onclick: () => { filter.onlyUnhandled = false; refresh(); } }, 'Все')),
      devices.length > 1
        ? h('select', {
            'aria-label': 'Компьютер',
            onchange: (e) => { filter.deviceId = e.currentTarget.value; refresh(); },
          },
            h('option', { value: '', selected: !filter.deviceId }, 'Все компьютеры'),
            devices.map((d) => h('option', { value: d.id, selected: filter.deviceId === d.id }, d.name)))
        : null);

    const unhandled = events.filter((e) => !e.handled_at);
    const eventRow = (e) => h('li', { class: `event${e.handled_at ? ' handled' : ''}`, 'data-event': e.id },
      h('span', { class: `event-dot ${LOCK_EVENTS.has(e.type) ? 'danger' : 'warn'}`, 'aria-hidden': 'true' }),
      h('div', { class: 'grow' },
        h('div', {}, describeEvent(e)),
        h('div', { class: 'muted small' }, [
          deviceName.get(e.device_id) || e.machine_name || 'неизвестный ПК',
          e.windows_user,
          e.verified ? null : 'без подписи ПК',
          fmtAgo(e.occurred_at),
        ].filter(Boolean).join(' · '))),
      e.handled_at
        ? null
        : h('button', {
            class: 'icon-btn', 'aria-label': `Прочитано: ${describeEvent(e)}`, title: 'Прочитано',
            onclick: (ev) => busy(ev.currentTarget, async () => { await api.markTamperHandled([e.id]); await refresh(); }),
          }, '✓'));

    const eventsSection = h('section', { class: 'stack', 'aria-labelledby': 'events-title' },
      h('div', { class: 'section-head' },
        h('h2', { id: 'events-title', class: 'grow' }, 'События'),
        unhandled.length
          ? h('button', {
              class: 'btn ghost small',
              onclick: (ev) => busy(ev.currentTarget, async () => {
                await api.markTamperHandled(unhandled.map((e) => e.id));
                toast('Отмечено', 'ok');
                await refresh();
              }),
            }, 'Прочитать все')
          : null),
      filterRow,
      events.length
        ? h('ul', { class: 'card list events' }, events.map(eventRow))
        : h('div', { class: 'card empty' },
            h('p', { class: 'muted' }, filter.onlyUnhandled ? 'Новых событий нет.' : 'Событий нет.')));

    container.replaceChildren(deviceCards, eventsSection, settingsCard);
    // The tab badge counts unread events; let the shell recount.
    window.dispatchEvent(new Event('security-changed'));
  }

  async function unlock(btn, d) {
    const ok = await confirmDialog(`Разблокировать «${d.name}»?`,
      'Детские учётки снова будут работать по расписанию. Сначала посмотри события ниже.',
      { okText: 'Разблокировать', danger: false });
    if (!ok) return;
    await busy(btn, async () => {
      await api.clearTamperLock(d.id);
      toast('Разблокировано. ПК узнает об этом в течение минуты.', 'ok');
      await refresh();
    });
  }

  await refresh();
  return container;
}
