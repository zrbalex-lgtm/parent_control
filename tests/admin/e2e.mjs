// Browser walkthrough of the admin app against the in-memory Supabase mock.
// Usage: node tests/admin/e2e.mjs [screenshotDir]
import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

// CommonJS require resolves globally installed Playwright too (NODE_PATH).
const { chromium } = createRequire(import.meta.url)('playwright');

const here = dirname(fileURLToPath(import.meta.url));
const adminDir = resolve(here, '../../admin');
const shots = process.argv[2] || here;
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml',
                '.png': 'image/png', '.webmanifest': 'application/manifest+json' };

const server = createServer(async (req, res) => {
  const path = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  const file = join(adminDir, path.endsWith('/') ? `${path}index.html` : path);
  try {
    const body = await readFile(file);
    res.writeHead(200, { 'content-type': TYPES[extname(file)] || 'application/octet-stream' });
    res.end(body);
  } catch {
    res.writeHead(404); res.end();
  }
}).listen(0);
const base = `http://127.0.0.1:${server.address().port}/`;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2 });
const errors = [];
page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
page.on('console', (m) => { if (m.type() === 'error' && !/Invalid login/.test(m.text())) errors.push(`console: ${m.text()}`); });
await page.route('https://cdn.jsdelivr.net/**', async (route) => route.fulfill({
  contentType: 'text/javascript', body: await readFile(join(here, 'mock-supabase.js')),
}));

const step = (name) => console.log(`ok   ${name}`);
const fail = (msg) => { throw new Error(`FAIL: ${msg}`); };
const store = () => page.evaluate(() => window.__store);

// ---------- login ----------
await page.goto(base);
await page.getByLabel('Email').fill('admin@pc.home');
await page.getByLabel('Пароль').fill('wrong');
await page.getByRole('button', { name: 'Войти' }).click();
await page.getByText('Неверный email или пароль').waitFor();
step('wrong password shows error');
await page.screenshot({ path: `${shots}/01-login.png` });

await page.getByLabel('Пароль').fill('secret');
await page.getByRole('button', { name: 'Войти' }).click();
await page.getByRole('heading', { name: 'Главная' }).waitFor();
await page.getByText('Добавь детей на вкладке «Дети».').waitFor();
step('login lands on home with no requests');
await page.getByRole('link', { name: /Устройства/ }).click();
await page.getByRole('heading', { name: 'Устройства' }).waitFor();

// ---------- devices before children ----------
await page.getByText('Онлайн').waitFor();
await page.getByText('Часы ПК сбиты на 7 мин').waitFor();
await page.getByText(/Ждёт установки · код до/).waitFor();
step('device status and flags render');
await page.screenshot({ path: `${shots}/02-devices.png`, fullPage: true });

// ---------- children ----------
await page.getByRole('link', { name: /Дети/ }).click();
await page.getByText('Пока никого нет.').waitFor();
for (const [name, emoji] of [['Миша', '🦊'], ['Аня', '']]) {
  await page.getByRole('button', { name: '+ Добавить ребёнка' }).click();
  await page.getByLabel('Имя').fill(name);
  if (emoji) await page.getByLabel('Эмодзи (необязательно)').fill(emoji);
  await page.getByRole('radio').nth(name === 'Аня' ? 3 : 0).click();
  await page.getByRole('button', { name: 'Сохранить' }).click();
  await page.getByRole('button', { name: `Изменить: ${name}` }).waitFor();
}
if ((await store()).children.length !== 2) fail('two children stored');
step('children added');
await page.screenshot({ path: `${shots}/03-children.png` });

// rename + delete roundtrip
await page.getByRole('button', { name: 'Изменить: Аня' }).click();
await page.getByLabel('Имя').fill('Анна');
await page.getByRole('button', { name: 'Сохранить' }).click();
await page.getByRole('button', { name: 'Изменить: Анна' }).waitFor();
step('child renamed');

// ---------- bind account ----------
await page.getByRole('link', { name: /Устройства/ }).click();
await page.getByLabel(/misha/).selectOption({ label: 'Миша' });
await page.getByText('Учётка привязана').waitFor();
{
  const s = await store();
  const misha = s.children.find((c) => c.name === 'Миша');
  if (s.device_accounts.find((a) => a.windows_user === 'misha').child_id !== misha.id) fail('account bound');
}
step('account bound to child');

// ---------- add device ----------
await page.getByRole('button', { name: '+ Добавить компьютер' }).click();
await page.getByLabel('Название').fill('Комп Ани');
await page.getByRole('button', { name: 'Сохранить' }).click();
const code = await page.locator('.code').textContent();
if (!/^[A-Z2-9]{4}-[A-Z2-9]{4}$/.test(code)) fail(`code format: ${code}`);
step(`device added, code ${code}`);
await page.screenshot({ path: `${shots}/04-device-code.png` });
await page.getByRole('button', { name: 'Готово' }).click();

// menu: disable with confirm
await page.getByRole('group').filter({ hasText: '⋯' }).first();
await page.getByLabel('Действия: Комп в детской').click();
await page.getByRole('button', { name: 'Отключить' }).click();
await page.getByRole('dialog').getByRole('button', { name: 'Отключить' }).click();
await page.getByText('Отключён', { exact: true }).first().waitFor();
if ((await store()).devices[0].is_enabled !== false) fail('device disabled');
step('device disabled via menu + confirm');
await page.screenshot({ path: `${shots}/05-devices-after.png`, fullPage: true });

// ---------- time requests ----------
await page.evaluate(() => {
  const s = window.__store;
  const misha = s.children.find((c) => c.name === 'Миша');
  const anna = s.children.find((c) => c.name === 'Анна');
  const ago = (m) => new Date(Date.now() - m * 60e3).toISOString();
  s.time_requests.push(
    { id: 1, child_id: misha.id, device_id: s.devices[0].id, minutes: 30, reason: 'доиграть катку', status: 'pending', created_at: ago(2) },
    { id: 2, child_id: anna.id, device_id: s.devices[0].id, minutes: 20, reason: null, status: 'pending', created_at: ago(1) });
});
await page.getByRole('link', { name: /Главная/ }).click();
await page.getByText('Миша просит 30 мин').waitFor();
await page.getByText('«доиграть катку»').waitFor();
await page.screenshot({ path: `${shots}/08-requests.png`, fullPage: true });
{
  const card = page.locator('li.request', { hasText: 'Миша' });
  await card.getByRole('button', { name: 'Подтвердить +30 мин' }).waitFor(); // defaults to the asked amount
  await card.getByRole('button', { name: '60 минут' }).click();
  await card.getByRole('button', { name: '15 минут' }).click();
  await card.getByRole('button', { name: 'Подтвердить +15 мин' }).click();
}
await page.getByText('Миша: добавлено 15 мин').waitFor();
await page.locator('li.request', { hasText: 'Анна' }).getByRole('button', { name: 'Отклонить' }).click();
await page.locator('li.request').first().waitFor({ state: 'detached' });
await page.getByText('добавлено 15').waitFor();
{
  const s = await store();
  if (s.bonuses.length !== 1 || s.bonuses[0].minutes !== 15) fail('approve creates bonus with chosen amount');
  if (s.time_requests.find((r) => r.id === 2).status !== 'denied') fail('deny');
}
if (await page.locator('main').evaluate((m) => /\bnull\b|undefined/.test(m.innerText))) fail('stray null/undefined text');
step('requests approved and denied');
await page.screenshot({ path: `${shots}/09-requests-after.png`, fullPage: true });

// ---------- locked by default ----------
await page.getByRole('button', { name: 'Настройки' }).click();
await page.getByRole('switch').check();
await page.getByText('Закрыт по умолчанию: включено').waitFor();
await page.locator('.child-card[data-child="Миша"]').getByText('Доступ закрыт').waitFor();
const mishaCard = page.locator('.child-card[data-child="Миша"]');
const annaCard = page.locator('.child-card[data-child="Анна"]');
await mishaCard.getByText('Доступ закрыт').waitFor();
await page.evaluate(() => {
  const s = window.__store;
  const anna = s.children.find((c) => c.name === 'Анна');
  s.time_requests.push({ id: 3, child_id: anna.id, device_id: s.devices[0].id, kind: 'unlock', minutes: 5,
    reason: 'уроки сделала', status: 'pending', created_at: new Date().toISOString() });
});
await page.getByRole('button', { name: 'Обновить' }).click();
await page.getByText('Анна просит открыть доступ').waitFor();
await page.screenshot({ path: `${shots}/10-access.png`, fullPage: true });
await mishaCard.getByRole('button', { name: 'Открыть доступ' }).click();
await page.getByText('Миша: доступ открыт').waitFor();
await page.locator('li.request', { hasText: 'Анна' }).getByRole('button', { name: 'Открыть доступ' }).click();
await page.getByText('Анна: доступ открыт').waitFor();
await annaCard.getByText('Доступ открыт').waitFor();
if ((await store()).day_unlocks.length !== 2) fail('two day unlocks');
await mishaCard.getByRole('button', { name: 'Закрыть доступ' }).click();
await mishaCard.getByText('Доступ закрыт').waitFor();
await page.getByRole('button', { name: 'Настройки' }).click();
await page.getByRole('switch').uncheck();
await page.getByText('Закрыт по умолчанию: выключено').waitFor();
await mishaCard.getByRole('button', { name: /доступ/ }).waitFor({ state: 'detached' });
step('locked by default: toggle, open/close, unlock request');

// ---------- dashboard quick actions ----------
await mishaCard.getByText('Minecraft').waitFor();
await mishaCard.getByText('За «Комп в детской»').waitFor();
await mishaCard.getByRole('button', { name: 'Добавить 30 минут: Миша' }).click();
await page.getByText('Миша: +30 мин').waitFor();
await mishaCard.getByText('(бонус +45)').waitFor();
await mishaCard.getByRole('button', { name: /с причиной/ }).click();
await page.getByLabel('Минуты (минус — забрать время)').fill('-10');
await page.getByLabel('Причина (необязательно)').fill('не убрал комнату');
await page.getByRole('dialog').getByRole('button', { name: 'Сохранить' }).click();
await page.getByText('Миша: -10 мин').waitFor();
await mishaCard.getByText('(бонус +35)').waitFor();

await mishaCard.getByRole('button', { name: 'Заблокировать' }).click();
await page.getByRole('dialog').getByRole('button', { name: 'Заблокировать' }).click();
await page.getByText('Миша: заблокирован до конца дня').waitFor();
await mishaCard.getByText('Заблокирован до 00:00').waitFor();
await page.screenshot({ path: `${shots}/12-dashboard-locked.png`, fullPage: true });
await mishaCard.getByRole('button', { name: 'Снять блокировку' }).click();
await page.getByText('Миша: блокировка снята').waitFor();
await annaCard.getByRole('button', { name: 'Через 5 мин' }).click();
await annaCard.getByText(/Блокировка в \d\d:\d\d/).waitFor();

await annaCard.getByRole('button', { name: 'Сообщение' }).click();
await page.getByLabel('Появится на экране в течение минуты').fill('Ужинать!');
await page.getByRole('dialog').getByRole('button', { name: 'Отправить' }).click();
await page.getByText('Сообщение отправлено').waitFor();
{
  const s = await store();
  const anna = s.children.find((c) => c.name === 'Анна');
  const msg = s.commands.find((k) => k.kind === 'message');
  if (!msg || msg.child_id !== anna.id || msg.message !== 'Ужинать!') fail('message command');
  const lock = s.commands.find((k) => k.kind === 'lock' && k.child_id !== anna.id);
  if (!lock || !lock.cancelled_at) fail('lock cancelled');
  const reasonBonus = s.bonuses.find((b) => b.minutes === -10);
  if (!reasonBonus || reasonBonus.reason !== 'не убрал комнату') fail('negative bonus with reason');
}
await page.screenshot({ path: `${shots}/13-dashboard.png`, fullPage: true });
step('dashboard: bonus, custom bonus, lock/unlock, lock in 5 min, message');

// ---------- schedule ----------
await page.getByRole('link', { name: /Расписание/ }).click();
await page.getByRole('heading', { name: 'Каждую неделю' }).waitFor();
await page.getByRole('tab', { name: /Миша/ }).click();
await page.waitForTimeout(300);
// Monday: ∞ -> 2:00, then +15 twice -> 2:30
await page.getByRole('button', { name: 'Понедельник: без лимита' }).click();
await page.getByRole('button', { name: 'Понедельник: больше на 15 минут' }).click();
await page.getByRole('button', { name: 'Понедельник: больше на 15 минут' }).click();
await page.getByLabel('Понедельник: с').selectOption('15:00');
await page.getByLabel('Понедельник: до').selectOption('20:00');
await page.getByRole('button', { name: 'Пн → на все будни' }).click();
await page.getByRole('button', { name: 'Сохранить' }).click();
await page.getByText('Расписание сохранено').waitFor();
{
  const s = await store();
  const misha = s.children.find((c) => c.name === 'Миша');
  const rows = s.schedule_weekly.filter((r) => r.child_id === misha.id).sort((a, b) => a.weekday - b.weekday);
  if (rows.length !== 7) fail(`7 weekly rows, got ${rows.length}`);
  for (const r of rows.slice(0, 5)) {
    if (r.limit_minutes !== 150 || r.window_from !== '15:00:00' || r.window_to !== '20:00:00') fail(`weekday ${r.weekday}: ${JSON.stringify(r)}`);
  }
  if (rows[5].limit_minutes !== null || rows[6].window_to !== '24:00:00') fail('weekend untouched');
}
step('weekly schedule saved (Mon copied to weekdays)');

// reload view: values come back from "server"
await page.getByRole('link', { name: /Дети/ }).click();
await page.getByRole('link', { name: /Расписание/ }).click();
await page.getByLabel('Пятница: с').waitFor();
if (await page.getByLabel('Пятница: с').inputValue() !== '15:00') fail('friday window reloaded');
step('schedule reload keeps values');

// override
await page.getByRole('button', { name: '+ Добавить' }).click();
await page.getByLabel('Лимит', { exact: true }).selectOption('none');
await page.getByLabel('Окно', { exact: true }).selectOption('set');
await page.getByLabel('Заметка').fill('каникулы');
await page.getByRole('dialog').getByRole('button', { name: 'Сохранить' }).click();
await page.getByText('Исключение сохранено').waitFor();
await page.getByText(/без лимита · 10:00–20:00 · каникулы/).waitFor();
step('override added');
await page.screenshot({ path: `${shots}/06-schedule.png`, fullPage: true });

// second child chip switches data
await page.getByRole('tab', { name: /Анна/ }).click();
await page.getByText(/Праздники, каникулы/).waitFor();
step('child switch loads empty schedule');

// desktop width sanity
await page.setViewportSize({ width: 1100, height: 900 });
await page.getByRole('tab', { name: /Миша/ }).click();
await page.getByText(/каникулы/).first().waitFor();
await page.screenshot({ path: `${shots}/07-schedule-desktop.png`, fullPage: true });

// logout
await page.getByRole('button', { name: 'Выйти' }).click();
await page.getByRole('button', { name: 'Войти' }).waitFor();
step('logout returns to login');

// no horizontal scroll at phone width on any tab
await page.setViewportSize({ width: 360, height: 780 });
await page.getByLabel('Пароль').fill('secret');
await page.getByLabel('Email').fill('admin@pc.home');
await page.getByRole('button', { name: 'Войти' }).click();
for (const tab of ['Главная', 'Дети', 'Расписание', 'Устройства']) {
  await page.getByRole('link', { name: new RegExp(tab) }).click();
  await page.waitForTimeout(400);
  const over = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  if (over > 0) fail(`${tab}: horizontal overflow ${over}px`);
}
step('no horizontal overflow at 360px');

if (errors.length) fail(`browser errors:\n${errors.join('\n')}`);
console.log('ALL ADMIN CHECKS PASSED');
await browser.close();
server.close();
