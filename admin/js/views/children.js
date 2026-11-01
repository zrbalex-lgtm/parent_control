// Children: list, add, edit, delete.
import { h, modal, confirmDialog, busy, toast, avatar, CHILD_COLORS } from '../ui.js';

export async function render({ api }) {
  const container = h('div', { class: 'stack' });

  async function refresh() {
    const children = await api.listChildren();
    const list = children.length
      ? h('ul', { class: 'list' }, children.map((c) =>
          h('li', { class: 'row card tappable', tabindex: '0', role: 'button',
                    'aria-label': `Изменить: ${c.name}`,
                    onclick: () => edit(c),
                    onkeydown: (e) => { if (e.key === 'Enter') edit(c); } },
            avatar(c),
            h('span', { class: 'grow' },
              h('span', { class: 'strong' }, c.name),
              c.locked_by_default ? h('span', { class: 'muted small' }, ' · закрыт по умолчанию') : null),
            h('span', { class: 'chevron', 'aria-hidden': 'true' }, '›'))))
      : h('div', { class: 'card empty' },
          h('p', {}, 'Пока никого нет.'),
          h('p', { class: 'muted' }, 'Добавь детей, потом привяжи их учётки Windows на вкладке «Устройства».'));

    container.replaceChildren(
      list,
      h('button', { class: 'btn primary block', onclick: () => edit(null) }, '+ Добавить ребёнка'));
  }

  async function edit(child) {
    const isNew = !child;
    const draft = child ? { ...child } : { name: '', color: CHILD_COLORS[0], avatar: '', sort_order: 0, locked_by_default: false };

    const result = await modal(isNew ? 'Новый ребёнок' : 'Изменить', (close) => {
      const name = h('input', { id: 'child-name', value: draft.name, maxlength: '40', required: true, autocomplete: 'off' });
      const emoji = h('input', { id: 'child-emoji', value: draft.avatar || '', maxlength: '4', placeholder: 'напр. 🦊', autocomplete: 'off' });
      const preview = h('div', { class: 'preview' });
      const updatePreview = () => preview.replaceChildren(
        avatar({ name: name.value || '?', avatar: emoji.value.trim(), color: draft.color }, 'lg'));
      name.addEventListener('input', updatePreview);
      emoji.addEventListener('input', updatePreview);

      const swatches = h('div', { class: 'swatches', role: 'radiogroup', 'aria-label': 'Цвет' },
        CHILD_COLORS.map((col) => {
          const b = h('button', {
            type: 'button', class: 'swatch', role: 'radio', style: { background: col },
            'aria-label': col, 'aria-checked': String(col === draft.color),
            onclick: () => {
              draft.color = col;
              for (const s of swatches.children) s.setAttribute('aria-checked', String(s === b));
              updatePreview();
            },
          });
          return b;
        }));
      updatePreview();

      const locked = h('input', {
        type: 'checkbox', id: 'child-locked', class: 'switch', role: 'switch', checked: !!draft.locked_by_default,
      });

      const err = h('p', { class: 'form-error', role: 'alert' });
      const save = h('button', { class: 'btn primary', type: 'submit' }, 'Сохранить');
      const form = h('form', {
        onsubmit: async (e) => {
          e.preventDefault();
          if (!name.value.trim()) { err.textContent = 'Нужно имя'; return; }
          await busy(save, async () => {
            await api.saveChild({ ...draft, name: name.value.trim(), avatar: emoji.value.trim(), locked_by_default: locked.checked });
            close('saved');
          });
        },
      },
        preview,
        h('label', { for: 'child-name' }, 'Имя'), name,
        h('label', { for: 'child-emoji' }, 'Эмодзи (необязательно)'), emoji,
        h('label', {}, 'Цвет'), swatches,
        h('div', { class: 'setting' },
          h('label', { for: 'child-locked', class: 'setting-label' },
            h('span', { class: 'strong' }, 'Закрыт по умолчанию'),
            h('span', { class: 'muted small' },
              'Каждый день компьютер закрыт, пока не откроешь доступ на «Главной» (или не одобришь запрос). Дальше — по расписанию.')),
          locked),
        err,
        h('div', { class: 'actions' },
          isNew ? null : h('button', { type: 'button', class: 'btn danger ghost', onclick: () => close('delete') }, 'Удалить'),
          h('span', { class: 'grow' }),
          save));
      return form;
    });

    if (result === 'saved') {
      toast(isNew ? 'Добавлено' : 'Сохранено', 'ok');
      await refresh();
    } else if (result === 'delete') {
      const ok = await confirmDialog(`Удалить «${child.name}»?`,
        'Удалятся расписание, бонусы и привязки учёток. История экранного времени останется без имени.');
      if (ok) {
        await busy(null, async () => {
          await api.deleteChild(child.id);
          toast('Удалено', 'ok');
          await refresh();
        });
      }
    }
  }

  await refresh();
  return container;
}
