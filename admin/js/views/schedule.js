// Schedule: weekly template per child + date overrides.
import {
  h, modal, confirmDialog, busy, toast, avatar, timeSelect,
  WEEKDAYS, WEEKDAYS_LONG, fmtMinutes, fmtDate, localDate,
} from '../ui.js';

const STEP = 15;
const MAX_LIMIT = 24 * 60;
const UNLIMITED_OVERRIDE = MAX_LIMIT; // overrides use null for "inherit", so "no limit" = whole day

let selectedChildId = null; // survives tab switches within the session

// Limit editor: [−] 2:00 [+] [∞]. value null = no limit.
function limitControl(value, onChange, label) {
  let current = value;
  const display = h('output', { class: 'limit-value', 'aria-live': 'polite' });
  const minus = h('button', { type: 'button', class: 'step', 'aria-label': `${label}: меньше на 15 минут` }, '−');
  const plus = h('button', { type: 'button', class: 'step', 'aria-label': `${label}: больше на 15 минут` }, '+');
  const inf = h('button', { type: 'button', class: 'chip-toggle', 'aria-label': `${label}: без лимита` }, '∞');

  const sync = () => {
    display.textContent = current === null ? 'без лимита' : fmtMinutes(current);
    display.classList.toggle('muted', current === null);
    inf.setAttribute('aria-pressed', String(current === null));
    minus.disabled = current === null || current <= 0;
    plus.disabled = current !== null && current >= MAX_LIMIT;
  };
  const set = (v) => { current = v; sync(); onChange(current); };

  minus.addEventListener('click', () => set(Math.max(0, current - STEP)));
  plus.addEventListener('click', () => set(current === null ? 60 : Math.min(MAX_LIMIT, current + STEP)));
  inf.addEventListener('click', () => set(current === null ? 120 : null));
  sync();
  return h('div', { class: 'limit' }, minus, display, plus, inf);
}

export async function render({ api, navigate }) {
  const children = await api.listChildren();
  if (!children.length) {
    return h('div', { class: 'card empty' },
      h('p', {}, 'Сначала добавь детей.'),
      h('button', { class: 'btn primary', onclick: () => navigate('children') }, 'К списку детей'));
  }
  if (!children.some((c) => c.id === selectedChildId)) selectedChildId = children[0].id;

  const container = h('div', { class: 'stack' });
  const chips = h('div', { class: 'chips', role: 'tablist', 'aria-label': 'Ребёнок' });
  const weeklyBox = h('section', { class: 'card', 'aria-labelledby': 'weekly-title' });
  const overridesBox = h('section', { class: 'card', 'aria-labelledby': 'ovr-title' });
  container.append(chips, weeklyBox, overridesBox);

  function renderChips() {
    chips.replaceChildren(...children.map((c) =>
      h('button', {
        class: 'chip', role: 'tab', 'aria-selected': String(c.id === selectedChildId),
        onclick: async () => {
          if (c.id === selectedChildId) return;
          if (weeklyDirty() && !(await confirmDialog('Есть несохранённые изменения',
            'Переключиться и потерять их?', { okText: 'Переключиться' }))) return;
          selectedChildId = c.id;
          renderChips();
          await loadChild();
        },
      }, avatar(c, 'sm'), c.name)));
  }

  // ---------------- weekly ----------------
  let saved = [];
  let draft = [];
  const weeklyDirty = () => JSON.stringify(saved) !== JSON.stringify(draft);

  function renderWeekly() {
    const saveBtn = h('button', { class: 'btn primary', disabled: !weeklyDirty() }, 'Сохранить');
    const markDirty = () => { saveBtn.disabled = !weeklyDirty(); };

    const rows = draft.map((d, i) => {
      const from = timeSelect(d.window_from, { allowEnd: false, label: `${WEEKDAYS_LONG[i]}: с` });
      const to = timeSelect(d.window_to, { allowStart: false, label: `${WEEKDAYS_LONG[i]}: до` });
      const warn = h('span', { class: 'field-warn', role: 'alert' });
      const onWindow = () => {
        d.window_from = from.value;
        d.window_to = to.value;
        warn.textContent = d.window_from >= d.window_to ? 'Начало позже конца' : '';
        markDirty();
      };
      from.addEventListener('change', onWindow);
      to.addEventListener('change', onWindow);
      return h('div', { class: `day-row${i >= 5 ? ' weekend' : ''}` },
        h('span', { class: 'day-label', title: WEEKDAYS_LONG[i] }, WEEKDAYS[i]),
        limitControl(d.limit_minutes, (v) => { d.limit_minutes = v; markDirty(); }, WEEKDAYS_LONG[i]),
        h('div', { class: 'window' }, from, h('span', { class: 'muted' }, '–'), to, warn));
    });

    const copyWeekdays = h('button', {
      class: 'btn ghost small',
      onclick: () => {
        for (let i = 1; i < 5; i++) draft[i] = { ...draft[0], weekday: i + 1 };
        renderWeekly();
      },
    }, 'Пн → на все будни');

    saveBtn.addEventListener('click', () => busy(saveBtn, async () => {
      const bad = draft.findIndex((d) => d.window_from >= d.window_to);
      if (bad >= 0) {
        toast(`${WEEKDAYS_LONG[bad]}: начало окна позже конца`, 'error');
        return;
      }
      await api.saveWeekly(selectedChildId, draft);
      saved = structuredClone(draft);
      toast('Расписание сохранено', 'ok');
      renderWeekly();
    }));

    weeklyBox.replaceChildren(
      h('div', { class: 'section-head' }, h('h2', { id: 'weekly-title' }, 'Каждую неделю'), copyWeekdays),
      h('p', { class: 'muted small' }, 'Лимит в день и окно, когда можно сидеть за компьютером (время ПК).'),
      h('div', { class: 'week' }, rows),
      h('div', { class: 'actions' }, h('span', { class: 'grow' }), saveBtn));
  }

  // ---------------- overrides ----------------
  async function renderOverrides() {
    const items = await api.listOverrides(selectedChildId, localDate());
    const describe = (o) => {
      const parts = [];
      if (o.limit_minutes !== null) {
        parts.push(o.limit_minutes >= UNLIMITED_OVERRIDE ? 'без лимита' : `лимит ${fmtMinutes(o.limit_minutes)}`);
      }
      if (o.window_from || o.window_to) {
        parts.push(`${o.window_from || '…'}–${o.window_to || '…'}`);
      }
      return parts.join(' · ') || 'как обычно';
    };

    overridesBox.replaceChildren(
      h('div', { class: 'section-head' },
        h('h2', { id: 'ovr-title' }, 'Исключения'),
        h('button', { class: 'btn ghost small', onclick: () => editOverride(null) }, '+ Добавить')),
      items.length
        ? h('ul', { class: 'list' }, items.map((o) =>
            h('li', { class: 'row' },
              h('div', { class: 'grow' },
                h('div', { class: 'strong' }, fmtDate(o.local_date)),
                h('div', { class: 'muted small' }, describe(o), o.note ? ` · ${o.note}` : '')),
              h('button', { class: 'btn ghost small', onclick: () => editOverride(o) }, 'Изменить'))))
        : h('p', { class: 'muted small' }, 'Праздники, каникулы, особые дни — перекрывают недельный шаблон.'));
  }

  async function editOverride(existing) {
    const o = existing
      ? { ...existing }
      : { local_date: localDate(), limit_minutes: null, window_from: null, window_to: null, note: '' };
    const result = await modal(existing ? 'Исключение' : 'Новое исключение', (close) => {
      const date = h('input', { type: 'date', id: 'ovr-date', value: o.local_date, min: localDate(), required: true, disabled: !!existing });

      // Limit: inherit / unlimited / custom
      let limitMode = o.limit_minutes === null ? 'inherit' : o.limit_minutes >= UNLIMITED_OVERRIDE ? 'none' : 'set';
      let limitValue = limitMode === 'set' ? o.limit_minutes : 180;
      const limitBox = h('div', {});
      const limitSel = h('select', { id: 'ovr-limit-mode' },
        h('option', { value: 'inherit', selected: limitMode === 'inherit' }, 'Как обычно'),
        h('option', { value: 'none', selected: limitMode === 'none' }, 'Без лимита'),
        h('option', { value: 'set', selected: limitMode === 'set' }, 'Задать'));
      const drawLimit = () => limitBox.replaceChildren(limitMode === 'set'
        ? limitControl(limitValue, (v) => { limitValue = v ?? 180; if (v === null) { limitMode = 'none'; limitSel.value = 'none'; drawLimit(); } }, 'Лимит')
        : '');
      limitSel.addEventListener('change', () => { limitMode = limitSel.value; drawLimit(); });
      drawLimit();

      // Window: inherit / custom
      const hasWindow = !!(o.window_from || o.window_to);
      const winSel = h('select', { id: 'ovr-win-mode' },
        h('option', { value: 'inherit', selected: !hasWindow }, 'Как обычно'),
        h('option', { value: 'set', selected: hasWindow }, 'Задать'));
      const from = timeSelect(o.window_from || '10:00', { allowEnd: false, label: 'Окно: с' });
      const to = timeSelect(o.window_to || '20:00', { allowStart: false, label: 'Окно: до' });
      const winBox = h('div', { class: 'window' }, from, h('span', { class: 'muted' }, '–'), to);
      const drawWin = () => { winBox.hidden = winSel.value !== 'set'; };
      winSel.addEventListener('change', drawWin);
      drawWin();

      const note = h('input', { id: 'ovr-note', value: o.note || '', maxlength: '80', placeholder: 'напр. каникулы' });
      const err = h('p', { class: 'form-error', role: 'alert' });
      const save = h('button', { class: 'btn primary', type: 'submit' }, 'Сохранить');

      return h('form', {
        onsubmit: async (e) => {
          e.preventDefault();
          err.textContent = '';
          if (!date.value) { err.textContent = 'Выбери дату'; return; }
          const setWin = winSel.value === 'set';
          if (setWin && from.value >= to.value) { err.textContent = 'Начало окна позже конца'; return; }
          const value = {
            local_date: date.value,
            limit_minutes: limitMode === 'inherit' ? null : limitMode === 'none' ? UNLIMITED_OVERRIDE : limitValue,
            window_from: setWin ? from.value : null,
            window_to: setWin ? to.value : null,
            note: note.value.trim(),
          };
          await busy(save, async () => {
            await api.saveOverride(selectedChildId, value);
            close('saved');
          });
        },
      },
        h('label', { for: 'ovr-date' }, 'Дата'), date,
        h('label', { for: 'ovr-limit-mode' }, 'Лимит'), limitSel, limitBox,
        h('label', { for: 'ovr-win-mode' }, 'Окно'), winSel, winBox,
        h('label', { for: 'ovr-note' }, 'Заметка'), note,
        err,
        h('div', { class: 'actions' },
          existing ? h('button', { type: 'button', class: 'btn danger ghost', onclick: () => close('delete') }, 'Удалить') : null,
          h('span', { class: 'grow' }),
          save));
    });

    if (result === 'saved') {
      toast('Исключение сохранено', 'ok');
      await renderOverrides();
    } else if (result === 'delete') {
      await busy(null, async () => {
        await api.deleteOverride(selectedChildId, existing.local_date);
        toast('Удалено', 'ok');
        await renderOverrides();
      });
    }
  }

  async function loadChild() {
    weeklyBox.replaceChildren(h('div', { class: 'loading' }, 'Загрузка…'));
    saved = await api.getWeekly(selectedChildId);
    draft = structuredClone(saved);
    renderWeekly();
    await renderOverrides();
  }

  renderChips();
  await loadChild();
  return container;
}
