// Devices: PCs, registration codes, Windows account -> child binding.
import { h, modal, confirmDialog, busy, toast, fmtAgo, fmtTime } from '../ui.js';

const ONLINE_MS = 2 * 60 * 1000;

// Close "⋯" menus on outside click or after picking an item.
document.addEventListener('click', (e) => {
  for (const m of document.querySelectorAll('details.menu[open]')) {
    if (!m.contains(e.target) || e.target.closest('.menu-list button')) m.open = false;
  }
});

function status(d) {
  if (!d.is_enabled) return { kind: 'off', text: 'Отключён' };
  if (!d.registered_at) {
    const exp = d.reg_code_expires_at && new Date(d.reg_code_expires_at);
    return exp && exp > new Date()
      ? { kind: 'wait', text: `Ждёт установки · код до ${fmtTime(exp)}` }
      : { kind: 'warn', text: 'Не установлен · код истёк' };
  }
  if (d.last_seen_at && Date.now() - new Date(d.last_seen_at).getTime() < ONLINE_MS) {
    return { kind: 'ok', text: 'Онлайн' };
  }
  return { kind: 'idle', text: `Офлайн · ${fmtAgo(d.last_seen_at)}` };
}

function flagChips(d) {
  const f = d.flags || {};
  const out = [];
  if (f.clock_skew) {
    const min = Math.round(Math.abs(d.clock_skew_sec || 0) / 60);
    out.push(`Часы ПК сбиты на ${min} мин`);
  }
  if (f.unbound_account) out.push('Есть непривязанная учётка');
  if (f.long_silence) out.push('Молчит больше суток');
  if (f.offline_minutes_7d > 0) out.push(`Без сети за неделю: ${f.offline_minutes_7d} мин`);
  if (d.registered_at && d.auto_update === false) out.push('Автообновление выключено');
  const chips = out.map((t) => h('span', { class: 'flag' }, t));
  // Tamper state links to the security tab.
  if (d.tamper_locked_since) chips.unshift(h('a', { class: 'flag danger', href: '#/security' }, 'Заблокирован после вмешательства'));
  if (d.config_state === 'lost') chips.unshift(h('a', { class: 'flag danger', href: '#/security' }, 'Конфиг утерян'));
  if (d.config_state === 'restored') chips.push(h('a', { class: 'flag', href: '#/security' }, 'Конфиг восстановлен'));
  return chips;
}

function showCode(name, code) {
  return modal('Код установки', (close) => [
    h('p', {}, `Компьютер «${name}»`),
    h('div', { class: 'code', 'aria-label': `Код: ${code.split('').join(' ')}` }, code),
    h('p', { class: 'muted small' }, 'Код одноразовый и действует 24 часа. Посмотреть его ещё раз нельзя — только выпустить новый.'),
    h('ol', { class: 'steps small' },
      h('li', {}, 'На этом ПК войди под учёткой администратора.'),
      h('li', {}, 'Запусти установщик из PowerShell от имени администратора.'),
      h('li', {}, 'Введи код, когда установщик попросит.')),
    h('div', { class: 'actions' },
      navigator.clipboard ? h('button', {
        class: 'btn ghost',
        onclick: () => navigator.clipboard.writeText(code).then(() => toast('Скопировано', 'ok'), () => {}),
      }, 'Копировать') : null,
      h('span', { class: 'grow' }),
      h('button', { class: 'btn primary', onclick: () => close() }, 'Готово')),
  ]);
}

function askName(title, initial = '') {
  return modal(title, (close) => {
    const input = h('input', { id: 'device-name', value: initial, maxlength: '40', required: true, placeholder: 'напр. Комп Миши', autocomplete: 'off' });
    return h('form', {
      onsubmit: (e) => { e.preventDefault(); if (input.value.trim()) close(input.value.trim()); },
    },
      h('label', { for: 'device-name' }, 'Название'), input,
      h('div', { class: 'actions' }, h('span', { class: 'grow' }),
        h('button', { class: 'btn primary', type: 'submit' }, 'Сохранить')));
  });
}

export async function render({ api }) {
  const container = h('div', { class: 'stack' });

  async function refresh() {
    const [devices, children] = await Promise.all([api.listDevices(), api.listChildren()]);

    const accountRow = (a) => {
      const id = `acc-${a.id}`;
      const sel = h('select', {
        id,
        onchange: (e) => busy(e.currentTarget, async () => {
          await api.setAccountChild(a.id, sel.value || null);
          toast(sel.value ? 'Учётка привязана' : 'Учётка без ограничений', 'ok');
          await refresh();
        }),
      },
        // A new account nobody has decided on yet: force an explicit choice, even "no limits".
        !a.child_id && !a.reviewed_at ? h('option', { value: '__new', selected: true, disabled: true }, 'Новая учётка — выбери') : null,
        h('option', { value: '', selected: !a.child_id && !!a.reviewed_at }, 'Не ограничивать'),
        children.map((c) => h('option', { value: c.id, selected: a.child_id === c.id }, c.name)));
      return h('li', { class: `account${!a.child_id && !a.reviewed_at ? ' unbound' : ''}` },
        h('label', { for: id, class: 'account-name' },
          h('span', { class: 'strong' }, a.windows_user),
          h('span', { class: 'muted small' }, `вход: ${fmtAgo(a.last_seen_at)}`)),
        sel);
    };

    const card = (d) => {
      const st = status(d);
      const menu = h('details', { class: 'menu' },
        h('summary', { class: 'icon-btn', 'aria-label': `Действия: ${d.name}` }, '⋯'),
        h('div', { class: 'menu-list' },
          h('button', { onclick: () => rename(d) }, 'Переименовать'),
          d.tamper_locked_since ? h('button', { onclick: () => unlockTamper(d) }, 'Разблокировать после вмешательства') : null,
          h('button', { onclick: () => toggle(d) }, d.is_enabled ? 'Отключить' : 'Включить'),
          d.registered_at ? h('button', { onclick: () => toggleUpdates(d) }, d.auto_update === false ? 'Включить автообновление' : 'Выключить автообновление') : null,
          h('button', { onclick: () => newCode(d) }, d.registered_at ? 'Переустановка: новый код' : 'Новый код'),
          h('button', { class: 'danger-text', onclick: () => remove(d) }, 'Удалить')));
      const flags = flagChips(d);
      return h('li', { class: 'card device' },
        h('div', { class: 'device-head' },
          h('div', { class: 'grow' },
            h('div', { class: 'strong' }, d.name),
            h('div', { class: 'muted small' },
              [d.machine_name, d.client_version && `v${d.client_version}`].filter(Boolean).join(' · ') || 'ещё не подключался')),
          menu),
        h('div', { class: `status ${st.kind}` }, h('span', { class: 'dot', 'aria-hidden': 'true' }), st.text),
        flags.length ? h('div', { class: 'flags' }, flags) : null,
        d.accounts.length
          ? h('ul', { class: 'accounts' }, d.accounts.map(accountRow))
          : d.registered_at
            ? h('p', { class: 'muted small' }, 'Учётки появятся после первого входа в Windows.')
            : null);
    };

    container.replaceChildren(
      devices.length
        ? h('ul', { class: 'list' }, devices.map(card))
        : h('div', { class: 'card empty' },
            h('p', {}, 'Компьютеров пока нет.'),
            h('p', { class: 'muted' }, 'Добавь ПК — получишь одноразовый код для установщика.')),
      h('button', { class: 'btn primary block', onclick: (e) => add(e.currentTarget) }, '+ Добавить компьютер'));
  }

  async function add(btn) {
    const name = await askName('Новый компьютер');
    if (!name) return;
    const res = await busy(btn, () => api.createDevice(name));
    if (!res) return;
    await refresh();
    await showCode(name, res.code);
  }

  async function rename(d) {
    const name = await askName('Переименовать', d.name);
    if (!name || name === d.name) return;
    await busy(null, async () => { await api.updateDevice(d.id, { name }); await refresh(); });
  }

  async function unlockTamper(d) {
    const ok = await confirmDialog(`Разблокировать «${d.name}»?`,
      'Детские учётки снова будут работать по расписанию. События можно посмотреть на вкладке «Безопасность».',
      { okText: 'Разблокировать', danger: false });
    if (!ok) return;
    await busy(null, async () => {
      await api.clearTamperLock(d.id);
      toast('Разблокировано', 'ok');
      await refresh();
    });
  }

  async function toggleUpdates(d) {
    const on = d.auto_update === false;
    await busy(null, async () => {
      await api.updateDevice(d.id, { auto_update: on });
      toast(on ? 'Автообновление включено' : 'Автообновление выключено: версия останется как есть', 'ok');
      await refresh();
    });
  }

  async function toggle(d) {
    if (d.is_enabled) {
      const ok = await confirmDialog(`Отключить «${d.name}»?`,
        'Компьютер перестанет ограничивать время, пока ты его снова не включишь.', { okText: 'Отключить' });
      if (!ok) return;
    }
    await busy(null, async () => {
      await api.updateDevice(d.id, { is_enabled: !d.is_enabled });
      toast(d.is_enabled ? 'Отключён' : 'Включён', 'ok');
      await refresh();
    });
  }

  async function newCode(d) {
    if (d.registered_at) {
      const ok = await confirmDialog('Выпустить новый код?',
        'Текущая установка на этом ПК сразу перестанет работать — понадобится переустановить с новым кодом.',
        { okText: 'Выпустить' });
      if (!ok) return;
    }
    const code = await busy(null, () => api.resetDeviceCode(d.id));
    if (!code) return;
    await refresh();
    await showCode(d.name, code);
  }

  async function remove(d) {
    const ok = await confirmDialog(`Удалить «${d.name}»?`,
      'Удалится вся история этого компьютера. Служба на ПК перестанет синхронизироваться.');
    if (!ok) return;
    await busy(null, async () => {
      await api.deleteDevice(d.id);
      toast('Удалено', 'ok');
      await refresh();
    });
  }

  await refresh();
  return container;
}
