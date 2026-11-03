// Settings: Telegram chats that get request notifications and can answer them.
import { h, modal, confirmDialog, busy, toast, fmtAgo } from '../ui.js';

const BOT = 'Parent_control_zrb_bot';

function showCode(code) {
  return modal('Подключить чат', (close) => [
    h('div', { class: 'code', 'aria-label': `Код: ${code.split('').join(' ')}` }, code),
    h('p', { class: 'muted small' }, 'Код одноразовый, действует 30 минут.'),
    h('h3', {}, 'Личный чат'),
    h('p', {}, 'Открой бота по кнопке и нажми «Start» — код подставится сам.'),
    h('a', { class: 'btn primary block', href: `https://t.me/${BOT}?start=${code}`, target: '_blank', rel: 'noopener' },
      `Открыть @${BOT}`),
    h('h3', {}, 'Групповой чат'),
    h('ol', { class: 'steps small' },
      h('li', {}, `Добавь @${BOT} в группу.`),
      h('li', {}, h('span', {}, 'Отправь в группе: '), h('code', {}, `/link ${code}`))),
    h('div', { class: 'actions' }, h('span', { class: 'grow' }),
      h('button', { class: 'btn ghost', onclick: () => close() }, 'Готово')),
  ]);
}

export async function render({ api }) {
  const container = h('div', { class: 'stack' });

  async function refresh() {
    const [chats, admins, children] = await Promise.all([api.listTgChats(), api.listAdmins(), api.listChildren()]);

    // Which of my children a chat hears about (null = all of them).
    const chatChildren = (c) => {
      if (!children.length) return null;
      const on = (id) => c.children == null || c.children.includes(id);
      return h('div', { class: 'chips wrap', role: 'group', 'aria-label': `Дети в чате ${c.title || c.chat_id}` },
        children.map((k) => h('button', {
          type: 'button', class: 'chip small-chip', 'aria-pressed': String(on(k.id)), 'aria-selected': String(on(k.id)),
          onclick: (e) => busy(e.currentTarget, async () => {
            const current = children.map((x) => x.id).filter(on);
            const next = on(k.id) ? current.filter((x) => x !== k.id) : [...current, k.id];
            if (!next.length) { toast('Хотя бы один ребёнок', 'error'); return; }
            await api.setChatChildren(c.chat_id, next.length === children.length ? null : next);
            await refresh();
          }),
        }, k.name)));
    };

    const adultsCard = h('section', { class: 'card stack', 'aria-labelledby': 'adults-title' },
      h('h2', { id: 'adults-title' }, 'Взрослые'),
      h('p', { class: 'muted small' },
        'Кто может входить в эту админку. Каждый видит только детей, к которым его добавили (в карточке ребёнка → «Кто управляет»).'),
      h('ul', { class: 'list' }, admins.map((a) => h('li', { class: 'row' },
        h('span', { class: 'grow' }, a.email, a.is_me ? h('span', { class: 'muted small' }, ' · это ты') : null)))),
      h('form', {
        class: 'add-adult',
        onsubmit: async (e) => {
          e.preventDefault();
          const input = e.currentTarget.querySelector('input');
          const btn = e.currentTarget.querySelector('button');
          if (!input.value.trim()) return;
          await busy(btn, async () => {
            await api.addAdmin(input.value.trim());
            toast('Добавлен. Теперь дай ему детей в их карточках.', 'ok');
            await refresh();
          });
        },
      },
        h('input', { type: 'email', 'aria-label': 'Email взрослого', placeholder: 'email взрослого', required: true, autocomplete: 'off' }),
        h('button', { class: 'btn ghost', type: 'submit' }, 'Добавить')),
      h('p', { class: 'muted small' },
        'Сначала создай ему пользователя в Supabase: Authentication → Users → Add user (email и пароль), потом добавь здесь.'));
    const row = (c) => h('li', { class: 'chat-row' }, h('div', { class: 'row' },
      h('div', { class: 'grow' },
        h('div', { class: 'strong' }, c.title || `Чат ${c.chat_id}`),
        h('div', { class: 'muted small' }, [c.chat_id < 0 ? 'группа' : 'личный', c.linked_by && `подключил ${c.linked_by}`, fmtAgo(c.linked_at)]
          .filter(Boolean).join(' · '))),
      h('button', {
        class: 'icon-btn danger-text', 'aria-label': `Отключить: ${c.title || c.chat_id}`,
        onclick: async () => {
          if (!(await confirmDialog('Отключить чат?', 'Бот перестанет писать в этот чат и отвечать там на команды.', { okText: 'Отключить' }))) return;
          await busy(null, async () => { await api.removeTgChat(c.chat_id); toast('Отключено', 'ok'); await refresh(); });
        },
      }, '×')), chatChildren(c));

    container.replaceChildren(adultsCard,
      h('section', { class: 'card stack', 'aria-labelledby': 'tg-title' },
        h('h2', { id: 'tg-title' }, 'Telegram'),
        h('p', { class: 'muted small' },
          `Бот @${BOT} присылает запросы времени с кнопками «Подтвердить» и «Отклонить», а по команде /status показывает, кто сколько сидит. `
          + 'Он отвечает только в подключённых чатах; в каждый чат приходят только отмеченные дети. Ты видишь чаты, которые подключил сам.'),
        chats.length
          ? h('ul', { class: 'list' }, chats.map(row))
          : h('p', { class: 'muted' }, 'Подключённых чатов нет.'),
        h('button', {
          class: 'btn primary block',
          onclick: (e) => busy(e.currentTarget, async () => {
            const code = await api.tgLinkCode();
            await showCode(code);
            await refresh();
          }),
        }, '+ Подключить чат')));
  }

  await refresh();
  return container;
}
