// Blocking an app group for a child for a while (YouTube for 2 hours, Minecraft until tonight).
import { h, modal, busy, toast, fmtTime } from '../ui.js';

const OPTIONS = [
  { label: '30 мин', minutes: 30 },
  { label: '1 час', minutes: 60 },
  { label: '2 часа', minutes: 120 },
  { label: 'До конца дня', minutes: null },
];

function untilFor(minutes) {
  if (minutes != null) return new Date(Date.now() + minutes * 60e3);
  const d = new Date();
  d.setHours(24, 0, 0, 0);
  return d;
}

/** Asks for how long and blocks the group; resolves true when a block was created. */
export function blockAppDialog(api, child, groupName) {
  return modal(`Заблокировать «${groupName}»`, (close) => [
    h('p', { class: 'muted' },
      `${child.name} не сможет открыть «${groupName}» на компьютере: сайт закроется во вкладке (браузер останется), `
      + 'программа закроется. Остальное время работает как обычно.'),
    h('div', { class: 'choice-grid', role: 'group', 'aria-label': 'На сколько' },
      OPTIONS.map((o) => h('button', {
        class: 'btn ghost',
        onclick: (e) => busy(e.currentTarget, async () => {
          const until = untilFor(o.minutes);
          await api.blockApp(child.id, groupName, until);
          toast(`«${groupName}» заблокировано до ${fmtTime(until)}`, 'ok');
          close(true);
        }),
      }, o.label))),
    h('p', { class: 'muted small' }, 'Что входит в «' + groupName + '», задают правила группы (История → Группы приложений).'),
  ]);
}

/** Red chips "«YouTube» до 19:05 ✕" for a child's active blocks; tap removes the block. */
export function blockChips(api, blocks, onChange) {
  return blocks.map((b) => h('button', {
    type: 'button', class: 'flag danger flag-btn', 'aria-label': `Снять блокировку «${b.group_name}»`, title: 'Снять блокировку',
    onclick: (e) => busy(e.currentTarget, async () => {
      await api.cancelAppBlocks([b.id]);
      toast(`«${b.group_name}» снова доступно`, 'ok');
      await onChange();
    }),
  }, `«${b.group_name}» до ${fmtTime(b.until)} ✕`));
}
