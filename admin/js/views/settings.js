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
    const chats = await api.listTgChats();
    const row = (c) => h('li', { class: 'row' },
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
      }, '×'));

    container.replaceChildren(
      h('section', { class: 'card stack', 'aria-labelledby': 'tg-title' },
        h('h2', { id: 'tg-title' }, 'Telegram'),
        h('p', { class: 'muted small' },
          `Бот @${BOT} присылает запросы времени с кнопками «Подтвердить» и «Отклонить», а по команде /status показывает, кто сколько сидит. `
          + 'Он отвечает только в подключённых чатах.'),
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
