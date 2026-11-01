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
await page.getByRole('heading', { name: 'Устройства' }).waitFor();
step('login lands on devices');

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
for (const tab of ['Дети', 'Расписание', 'Устройства']) {
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
