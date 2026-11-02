// Home dashboard: pending requests, then one live card per child with quick actions.
import {
  h, busy, toast, avatar, modal, confirmDialog, fmtAgo, fmtTime, fmtMinutes, localDate,
} from '../ui.js';

const GRANT_OPTIONS = [15, 30, 60];
const QUICK_BONUS = [15, 30, 60];
const REFRESH_MS = 30_000;

// Next local midnight: "lock for today" ends there.
function nextMidnight() {
  const d = new Date();
  d.setHours(24, 0, 0, 0);
  return d;
}

function minutesOfDay(hhmm) {
  const [h, m] = (hhmm || '00:00').split(':').map(Number);
  return h * 60 + m;
}

export async function render({ api }) {
  const container = h('div', { class: 'stack' });
  let loading = false;

  async function refresh() {
    if (loading) return;
    loading = true;
    try {
      await draw();
    } finally {
      loading = false;
    }
  }

  async function draw() {
    const today = localDate();
    const [{ pending, recent }, devices, dash] = await Promise.all([
      api.listRequests(), api.listDevices(), api.dashboard(today),
    ]);
    const children = dash?.children || [];
    const childById = new Map(children.map((c) => [c.id, c]));
    const deviceName = (id) => devices.find((d) => d.id === id)?.name;
    const childOf = (id) => childById.get(id) || { name: '?', color: '#9aa3b2' };

    // ---------- requests ----------
    const denyButton = (r) => h('button', {
      class: 'btn danger ghost',
      onclick: (e) => busy(e.currentTarget, async () => {
        await api.decideRequest(r.id, false);
        toast('Запрос отклонён', 'ok');
        await refresh();
      }),
    }, 'Отклонить');

    const requestHead = (r, child, title) => h('div', { class: 'row' },
      avatar(child),
      h('div', { class: 'grow' },
        h('div', { class: 'strong' }, title),
        h('div', { class: 'muted small' },
          [fmtAgo(r.created_at), deviceName(r.device_id)].filter(Boolean).join(' · '))));

    const unlockCard = (r, child) => h('li', { class: 'card request' },
      requestHead(r, child, `${child.name} просит открыть доступ`),
      r.reason ? h('p', { class: 'reason' }, `«${r.reason}»`) : null,
      h('div', { class: 'grant' },
        h('button', {
          class: 'btn primary',
          onclick: (e) => busy(e.currentTarget, async () => {
            await api.decideRequest(r.id, true, null, today);
            toast(`${child.name}: доступ открыт`, 'ok');
            await refresh();
          }),
        }, 'Открыть доступ'),
        denyButton(r)));

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
        await api.decideRequest(r.id, true, chosen, today);
        toast(`${child.name}: открыто на ${chosen} мин`, 'ok');
        await refresh();
      }));
      return h('li', { class: 'card request' },
        requestHead(r, child, `${child.name} просит ${r.minutes} мин`),
        r.reason ? h('p', { class: 'reason' }, `«${r.reason}»`) : null,
        h('div', { class: 'amounts', role: 'group', 'aria-label': 'Сколько добавить' }, chips),
        h('div', { class: 'grant' }, confirm, denyButton(r)),
        h('p', { class: 'muted small' }, 'Доступ откроется сразу на это время — даже вне расписания и сверх лимита.'));
    };

    const pendingCard = (r) => (r.kind === 'unlock' ? unlockCard : timeCard)(r, childOf(r.child_id));

    // ---------- child cards ----------
    const childCard = (c) => {
      const now = new Date();
      const nowMin = now.getHours() * 60 + now.getMinutes();
      const allowed = c.limit_minutes == null ? null : c.limit_minutes + c.bonus_minutes;
      const left = allowed == null ? null : allowed - c.spent_minutes;
      const from = c.window_from || '00:00';
      const to = c.window_to || '24:00';
      const fullDay = from === '00:00' && to === '24:00';
      const inWindow = nowMin >= minutesOfDay(from) && nowMin < minutesOfDay(to);
      const activeLocks = (c.locks || []).filter((l) => new Date(l.lock_from) <= now);
      const scheduled = (c.locks || []).filter((l) => new Date(l.lock_from) > now);
      const accessClosed = c.requires_unlock && !c.unlocked;

      // what is on the screen right now
      const app = c.now ? (c.now.app_group || c.now.title || c.now.exe) : null;
      const where = c.now
        ? h('div', { class: 'presence on' },
            h('span', { class: 'dot', 'aria-hidden': 'true' }),
            h('span', {}, `За «${c.now.device_name}»`),
            c.now.state === 'idle' ? h('span', { class: 'muted' }, ' · без активности') : null,
            c.now.state === 'locked' ? h('span', { class: 'muted' }, ' · экран заблокирован') : null)
        : h('div', { class: 'presence' }, h('span', { class: 'dot', 'aria-hidden': 'true' }), 'Не за компьютером');

      // usage + progress
      const pct = allowed ? Math.min(100, Math.round((c.spent_minutes / allowed) * 100)) : 0;
      const tone = left == null ? 'ok' : left <= 5 ? 'danger' : left <= 15 ? 'warn' : 'ok';
      const usage = h('div', { class: 'usage' },
        h('div', { class: 'usage-numbers' },
          h('span', { class: 'usage-big' }, fmtMinutes(c.spent_minutes)),
          h('span', { class: 'muted' }, allowed == null ? ' · без лимита' : ` из ${fmtMinutes(allowed)}`),
          c.bonus_minutes ? h('span', { class: 'muted small' }, ` (бонус ${c.bonus_minutes > 0 ? '+' : ''}${c.bonus_minutes})`) : null),
        allowed != null
          ? h('div', { class: `bar ${tone}`, role: 'progressbar', 'aria-valuemin': '0', 'aria-valuemax': String(allowed),
                       'aria-valuenow': String(c.spent_minutes), 'aria-label': 'Использовано' },
              h('span', { style: { width: `${pct}%` } }))
          : null,
        h('div', { class: 'muted small' },
          [
            left != null ? (left > 0 ? `осталось ${fmtMinutes(left)}` : 'лимит исчерпан') : null,
            fullDay ? 'весь день' : `окно ${from}–${to}${inWindow ? '' : ' (сейчас вне окна)'}`,
          ].filter(Boolean).join(' · ')));

      // badges
      const badges = [];
      if (activeLocks.length) {
        const until = activeLocks.map((l) => l.lock_until).filter(Boolean).sort().pop();
        badges.push(h('span', { class: 'flag danger' }, until ? `Заблокирован до ${fmtTime(until)}` : 'Заблокирован'));
      }
      for (const l of scheduled) badges.push(h('span', { class: 'flag' }, `Блокировка в ${fmtTime(l.lock_from)}`));
      if (c.requires_unlock) {
        badges.push(h('span', { class: `flag ${c.unlocked ? 'ok' : ''}` }, c.unlocked ? 'Доступ открыт' : 'Доступ закрыт'));
      }
      if (c.tamper_locked) {
        badges.push(h('a', { class: 'flag danger', href: '#/security' }, 'Заблокирован после вмешательства'));
      } else if (c.tamper_unhandled > 0) {
        badges.push(h('a', { class: 'flag', href: '#/security' }, `Вмешательство: ${c.tamper_unhandled}`));
      }

      // actions
      const bonusButtons = QUICK_BONUS.map((m) => h('button', {
        class: 'btn ghost small', 'aria-label': `Добавить ${m} минут: ${c.name}`,
        onclick: (e) => busy(e.currentTarget, async () => {
          await api.addBonus(c.id, today, m, null);
          toast(`${c.name}: +${m} мин`, 'ok');
          await refresh();
        }),
      }, `+${m}`));
      bonusButtons.push(h('button', {
        class: 'btn ghost small', 'aria-label': `Добавить или убрать время с причиной: ${c.name}`,
        onclick: () => customBonus(c, today),
      }, '±…'));

      const lockIds = [...activeLocks, ...scheduled].map((l) => l.id);
      const actions = lockIds.length
        ? [h('button', {
            class: 'btn ghost',
            onclick: (e) => busy(e.currentTarget, async () => {
              await api.cancelLocks(lockIds);
              toast(`${c.name}: блокировка снята`, 'ok');
              await refresh();
            }),
          }, 'Снять блокировку')]
        : [
            h('button', {
              class: 'btn danger ghost',
              onclick: async () => {
                const ok = await confirmDialog(`Заблокировать «${c.name}»?`,
                  'Компьютер закроется до конца дня (через 20 секунд, с предупреждением на экране).', { okText: 'Заблокировать' });
                if (!ok) return;
                await busy(null, async () => {
                  await api.lockChild(c.id, new Date(), nextMidnight());
                  toast(`${c.name}: заблокирован до конца дня`, 'ok');
                  await refresh();
                });
              },
            }, 'Заблокировать'),
            h('button', {
              class: 'btn ghost',
              onclick: (e) => busy(e.currentTarget, async () => {
                await api.lockChild(c.id, new Date(Date.now() + 5 * 60e3), nextMidnight());
                toast(`${c.name}: блокировка через 5 минут`, 'ok');
                await refresh();
              }),
            }, 'Через 5 мин'),
          ];
      if (c.requires_unlock) {
        actions.unshift(h('button', {
          class: `btn ${accessClosed ? 'primary' : 'ghost'}`,
          onclick: (e) => busy(e.currentTarget, async () => {
            if (accessClosed) await api.openAccess(c.id, today);
            else await api.closeAccess(c.id, today);
            toast(accessClosed ? `${c.name}: доступ открыт` : `${c.name}: доступ закрыт`, 'ok');
            await refresh();
          }),
        }, accessClosed ? 'Открыть доступ' : 'Закрыть доступ'));
      }
      actions.push(h('button', { class: 'btn ghost', onclick: () => sendMessage(c) }, 'Сообщение'));

      return h('li', { class: 'card child-card', 'data-child': c.name },
        h('div', { class: 'row' },
          avatar(c),
          h('div', { class: 'grow' }, h('div', { class: 'strong' }, c.name), where)),
        app ? h('div', { class: 'now-app', title: c.now.title || '' }, app) : null,
        usage,
        badges.length ? h('div', { class: 'flags' }, badges) : null,
        h('div', { class: 'bonus-row', role: 'group', 'aria-label': 'Добавить время' }, bonusButtons),
        h('div', { class: 'action-row' }, actions));
    };

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
            h('span', { class: 'muted' }, ` · ${fmtAgo(r.decided_at)}${r.decided_by_name?.startsWith('Telegram') ? ` · ${r.decided_by_name}` : ''}`))));
    };

    container.replaceChildren(...[
      pending.length
        ? h('section', { 'aria-labelledby': 'req-title', class: 'stack' },
            h('h2', { id: 'req-title' }, `Запросы (${pending.length})`),
            h('ul', { class: 'list' }, pending.map(pendingCard)))
        : null,
      children.length
        ? h('ul', { class: 'list' }, children.map(childCard))
        : h('div', { class: 'card empty' }, h('p', { class: 'muted' }, 'Добавь детей на вкладке «Дети».')),
      h('div', { class: 'refresh-row muted small' },
        h('span', {}, `Обновлено в ${fmtTime(new Date())}`),
        h('button', { class: 'btn ghost small', onclick: (e) => busy(e.currentTarget, refresh) }, 'Обновить')),
      recent.length
        ? h('section', { class: 'card' },
            h('h2', {}, 'Запросы за сутки'),
            h('ul', { class: 'list' }, recent.map(recentRow)))
        : null,
    ].filter(Boolean));
  }

  async function customBonus(c, today) {
    const done = await modal(`Время для «${c.name}»`, (close) => {
      const minutes = h('input', { id: 'bonus-min', type: 'number', inputmode: 'numeric', step: '5', value: '30', min: '-240', max: '240' });
      const reason = h('input', { id: 'bonus-reason', maxlength: '80', placeholder: 'напр. помог по дому' });
      const err = h('p', { class: 'form-error', role: 'alert' });
      const save = h('button', { class: 'btn primary', type: 'submit' }, 'Сохранить');
      return h('form', {
        onsubmit: async (e) => {
          e.preventDefault();
          const m = Number(minutes.value);
          if (!Number.isInteger(m) || m === 0 || Math.abs(m) > 240) { err.textContent = 'От −240 до 240, не ноль'; return; }
          await busy(save, async () => {
            await api.addBonus(c.id, today, m, reason.value.trim());
            close(m);
          });
        },
      },
        h('label', { for: 'bonus-min' }, 'Минуты (минус — забрать время)'), minutes,
        h('label', { for: 'bonus-reason' }, 'Причина (необязательно)'), reason,
        err,
        h('div', { class: 'actions' }, h('span', { class: 'grow' }), save));
    });
    if (done) {
      toast(`${c.name}: ${done > 0 ? '+' : ''}${done} мин`, 'ok');
      await refresh();
    }
  }

  async function sendMessage(c) {
    const sent = await modal(`Сообщение для «${c.name}»`, (close) => {
      const text = h('textarea', { id: 'msg-text', rows: '3', maxlength: '300', placeholder: 'напр. Ужинать через 10 минут!' });
      const err = h('p', { class: 'form-error', role: 'alert' });
      const send = h('button', { class: 'btn primary', type: 'submit' }, 'Отправить');
      return h('form', {
        onsubmit: async (e) => {
          e.preventDefault();
          if (!text.value.trim()) { err.textContent = 'Напиши текст'; return; }
          await busy(send, async () => {
            await api.sendMessage(c.id, text.value.trim());
            close(true);
          });
        },
      },
        h('label', { for: 'msg-text' }, 'Появится на экране в течение минуты'), text,
        err,
        h('div', { class: 'actions' }, h('span', { class: 'grow' }), send));
    });
    if (sent) toast('Сообщение отправлено', 'ok');
  }

  await refresh();

  // Live refresh while this view is on screen and the app is visible.
  const timer = setInterval(() => {
    if (!container.isConnected) { clearInterval(timer); return; }
    if (!document.hidden && !document.querySelector('.backdrop')) refresh().catch(() => {});
  }, REFRESH_MS);

  return container;
}
