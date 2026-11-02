// App groups: rules that name apps in reports ("javaw.exe" -> "Minecraft"), plus ungrouped apps.
import { h, modal, confirmDialog, busy, toast, fmtDuration, humanError } from '../ui.js';

export const FIELDS = { exe: 'Программа (exe)', product: 'Название продукта', title: 'Заголовок окна' };
const TYPES = { contains: 'содержит', regex: 'рег. выражение' };

export function describeRule(r) {
  return `${FIELDS[r.match_field]} ${TYPES[r.match_type]} «${r.pattern}»`;
}

/** Suggested group name for an app: product name, else exe without extension. */
export function suggestName(app) {
  if (app.product) return app.product.replace(/\(TM\)|\(R\)|™|®/g, '').trim();
  return (app.exe || '').replace(/\.exe$/i, '');
}

/**
 * Rule editor in a bottom sheet with a live preview of what it catches over 7 days.
 * Resolves true when saved.
 */
export function editRule(api, rule, existingNames = []) {
  const draft = { group_name: '', match_field: 'exe', match_type: 'contains', pattern: '', ...rule };
  return modal(draft.id ? 'Правило группы' : 'Новое правило', (close) => {
    const name = h('input', { id: 'rule-group', value: draft.group_name, maxlength: '60', required: true,
                              list: 'group-names', autocomplete: 'off', placeholder: 'напр. Minecraft' });
    const names = h('datalist', { id: 'group-names' }, existingNames.map((n) => h('option', { value: n })));
    const field = h('select', { id: 'rule-field' },
      Object.entries(FIELDS).map(([v, t]) => h('option', { value: v, selected: v === draft.match_field }, t)));
    const type = h('select', { id: 'rule-type', 'aria-label': 'Как сравнивать' },
      Object.entries(TYPES).map(([v, t]) => h('option', { value: v, selected: v === draft.match_type }, t)));
    const pattern = h('input', { id: 'rule-pattern', value: draft.pattern, maxlength: '200', required: true, autocomplete: 'off',
                                 spellcheck: 'false', placeholder: 'напр. javaw.exe или minecraft' });
    const preview = h('div', { class: 'rule-preview muted small', 'aria-live': 'polite' }, 'Введи текст — покажу, что попадёт в группу.');
    const err = h('p', { class: 'form-error', role: 'alert' });

    let timer;
    let seq = 0;
    const runPreview = () => {
      clearTimeout(timer);
      const p = pattern.value.trim();
      if (!p) {
        preview.replaceChildren('Введи текст — покажу, что попадёт в группу.');
        return;
      }
      timer = setTimeout(async () => {
        const my = ++seq;
        preview.replaceChildren('Считаю…');
        try {
          const res = await api.previewAppRule(field.value, type.value, p);
          if (my !== seq) return;
          preview.replaceChildren(
            h('div', { class: res.minutes ? 'ok-text' : '' },
              res.minutes ? `За 7 дней попадает ${fmtDuration(res.minutes)}:` : 'За 7 дней ничего не попадает.'),
            res.samples.length
              ? h('ul', { class: 'samples' }, res.samples.map((s) => h('li', {},
                  h('span', { class: 'strong' }, s.product || s.exe), ` · ${s.exe}`,
                  s.title ? h('span', { class: 'block' }, `«${s.title}»`) : null,
                  h('span', { class: 'block' }, fmtDuration(s.minutes)))))
              : null);
        } catch (e) {
          if (my === seq) preview.replaceChildren(h('span', { class: 'danger-text' }, humanError(e)));
        }
      }, 350);
    };
    for (const el of [field, type]) el.addEventListener('change', runPreview);
    pattern.addEventListener('input', runPreview);
    if (draft.pattern) runPreview();

    const save = h('button', { class: 'btn primary', type: 'submit' }, 'Сохранить');
    return h('form', {
      onsubmit: async (e) => {
        e.preventDefault();
        err.textContent = '';
        if (!name.value.trim() || !pattern.value.trim()) return;
        save.disabled = true;
        try {
          await api.saveAppGroup({ ...draft, group_name: name.value, match_field: field.value,
                                   match_type: type.value, pattern: pattern.value });
          close(true);
        } catch (ex) {
          err.textContent = humanError(ex);
          save.disabled = false;
        }
      },
    },
      h('label', { for: 'rule-group' }, 'Группа'), name, names,
      h('label', { for: 'rule-field' }, 'Где искать'), field,
      h('label', { for: 'rule-pattern' }, 'Текст'),
      h('div', { class: 'rule-pattern-row' }, type, pattern),
      preview, err,
      h('div', { class: 'actions' }, h('span', { class: 'grow' }), save));
  });
}

export async function render({ api }) {
  const container = h('div', { class: 'stack' });

  async function refresh() {
    const [rules, ungrouped] = await Promise.all([api.listAppGroups(), api.ungroupedApps(7)]);
    const names = [...new Set(rules.map((r) => r.group_name))].sort((a, b) => a.localeCompare(b, 'ru'));

    const ruleRow = (r) => h('li', { class: 'row' },
      h('span', { class: 'grow small' }, describeRule(r)),
      h('button', { class: 'icon-btn', 'aria-label': `Изменить: ${describeRule(r)}`, onclick: () => edit(r) }, '✎'),
      h('button', { class: 'icon-btn danger-text', 'aria-label': `Удалить: ${describeRule(r)}`, onclick: () => remove(r) }, '×'));

    const groupCards = names.map((n) => h('li', { class: 'card', 'data-group': n },
      h('div', { class: 'section-head' },
        h('span', { class: 'strong grow' }, n),
        h('button', { class: 'btn ghost small', onclick: () => edit({ group_name: n }) }, '+ правило')),
      h('ul', { class: 'list' }, rules.filter((r) => r.group_name === n).map(ruleRow))));

    const ungroupedRow = (a) => h('li', { class: 'row' },
      h('div', { class: 'grow' },
        h('div', { class: 'strong' }, a.product || a.exe),
        h('div', { class: 'muted small' }, [a.product ? a.exe : null, a.title ? `«${a.title}»` : null].filter(Boolean).join(' · ')),
        h('div', { class: 'muted small' }, fmtDuration(a.minutes))),
      h('button', {
        class: 'btn ghost small', 'aria-label': `В группу: ${a.product || a.exe}`,
        onclick: () => edit({ group_name: suggestName(a), match_field: 'exe', match_type: 'contains', pattern: a.exe }),
      }, 'В группу'));

    container.replaceChildren(
      h('p', { class: 'muted small' },
        'Группы объединяют программы и сайты в отчётах: например, javaw.exe или окно «Minecraft» → «Minecraft». Первое подходящее правило побеждает.'),
      names.length
        ? h('ul', { class: 'list' }, groupCards)
        : h('div', { class: 'card empty' }, h('p', { class: 'muted' }, 'Групп пока нет.')),
      h('button', { class: 'btn primary block', onclick: () => edit({}) }, '+ Новая группа'),
      h('section', { class: 'stack', 'aria-labelledby': 'ungrouped-title' },
        h('h2', { id: 'ungrouped-title' }, 'Без группы за 7 дней'),
        ungrouped.length
          ? h('ul', { class: 'card list' }, ungrouped.map(ungroupedRow))
          : h('div', { class: 'card empty' }, h('p', { class: 'muted' }, 'Всё разложено по группам.'))));

    async function edit(rule) {
      if (await editRule(api, rule, names)) {
        toast('Сохранено', 'ok');
        await refresh();
      }
    }

    async function remove(r) {
      const ok = await confirmDialog('Удалить правило?', describeRule(r));
      if (!ok) return;
      await busy(null, async () => { await api.deleteAppGroup(r.id); await refresh(); });
    }
  }

  await refresh();
  return container;
}
