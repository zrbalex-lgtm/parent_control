// Telegram bot for the parent control app (Supabase Edge Function "telegram-bot").
// - Telegram webhook: /start CODE, /link CODE, /status, /requests, /help, approve/deny buttons.
// - Internal calls from the database (pg_net trigger): new request -> post to all linked chats,
//   decided -> update every message about it; "setup" registers the webhook and commands.
// Both callers must present the shared secret from parent_control.tg_config.
import { createClient } from "npm:@supabase/supabase-js@2.49.4";

const TOKEN = Deno.env.get("TELEGRAM_BOT_TOKEN") ?? "";
const TZ = "Europe/Zurich";
const sb = createClient(Deno.env.get("SUPABASE_URL")!, Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!, {
  db: { schema: "parent_control" },
  auth: { persistSession: false },
});

type Info = {
  id: number; kind: "time" | "unlock"; status: string; minutes: number; granted_minutes: number | null;
  reason: string | null; child: string; device: string | null; created_at: string;
  decided_by_name: string | null; spent: number; allowed: number | null;
};

// ---------------- helpers ----------------

let cachedConfig: { secret: string; function_url: string } | null = null;
async function config() {
  if (!cachedConfig) {
    const { data, error } = await sb.from("tg_config").select("secret,function_url").eq("id", 1).single();
    if (error) throw error;
    cachedConfig = data;
  }
  return cachedConfig!;
}

async function tg(method: string, body: Record<string, unknown>) {
  const res = await fetch(`https://api.telegram.org/bot${TOKEN}/${method}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  const json = await res.json().catch(() => ({ ok: false, description: `HTTP ${res.status}` }));
  if (!json.ok && !/not modified/i.test(json.description ?? "")) console.error(method, json.description);
  return json;
}

async function rpc<T>(fn: string, args: Record<string, unknown> = {}): Promise<T> {
  const { data, error } = await sb.rpc(fn, args);
  if (error) throw error;
  return data as T;
}

const esc = (s: unknown) => String(s ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

function dur(m: number | null | undefined) {
  if (m == null) return "без лимита";
  if (m < 60) return `${m} мин`;
  const h = Math.floor(m / 60), mm = m % 60;
  return mm ? `${h} ч ${mm} мин` : `${h} ч`;
}

const time = (iso: string) =>
  new Intl.DateTimeFormat("ru-RU", { timeZone: TZ, hour: "2-digit", minute: "2-digit" }).format(new Date(iso));

const sendText = (chat_id: number, text: string, extra: Record<string, unknown> = {}) =>
  tg("sendMessage", { chat_id, text, parse_mode: "HTML", disable_web_page_preview: true, ...extra });

// ---------------- requests ----------------

function requestText(r: Info) {
  const usage = `Сегодня: ${dur(r.spent)}${r.allowed != null ? ` из ${dur(r.allowed)}` : ""}`;
  const where = [r.device, `запрос в ${time(r.created_at)}`].filter(Boolean).join(" · ");
  const ask = r.kind === "unlock"
    ? `🔓 <b>${esc(r.child)}</b> просит открыть доступ на сегодня`
    : `⏳ <b>${esc(r.child)}</b> просит ещё <b>${dur(r.minutes)}</b>`;
  const lines = [ask];
  if (r.reason) lines.push(`«${esc(r.reason)}»`);
  lines.push(`<i>${esc(where)}</i>`, usage);
  if (r.status === "approved") {
    lines.push("", r.kind === "unlock"
      ? `✅ Доступ открыт (${esc(r.decided_by_name)})`
      : `✅ Одобрено ${dur(r.granted_minutes ?? r.minutes)} (${esc(r.decided_by_name)})`);
  } else if (r.status === "denied") {
    lines.push("", `❌ Отклонено (${esc(r.decided_by_name)})`);
  }
  return lines.join("\n");
}

function requestKeyboard(r: Info) {
  if (r.status !== "pending") return { inline_keyboard: [] };
  if (r.kind === "unlock") {
    return { inline_keyboard: [[
      { text: "✅ Открыть доступ", callback_data: `a:${r.id}:0` },
      { text: "❌ Отклонить", callback_data: `d:${r.id}` },
    ]] };
  }
  const amounts = [...new Set([15, 30, 60, r.minutes])].sort((a, b) => a - b);
  return { inline_keyboard: [
    amounts.map((m) => ({ text: `+${m} мин${m === r.minutes ? " ✓" : ""}`, callback_data: `a:${r.id}:${m}` })),
    [{ text: "❌ Отклонить", callback_data: `d:${r.id}` }],
  ] };
}

async function postRequest(r: Info, chatIds: number[]) {
  for (const chat_id of chatIds) {
    const res = await sendText(chat_id, requestText(r), { reply_markup: requestKeyboard(r) });
    if (res.ok) {
      await sb.from("tg_request_messages").insert({ request_id: r.id, chat_id, message_id: res.result.message_id });
    }
  }
}

async function refreshRequestMessages(id: number) {
  const r = await rpc<Info>("tg_request_info", { p_id: id });
  if (!r) return;
  const { data: msgs } = await sb.from("tg_request_messages").select("chat_id,message_id").eq("request_id", id);
  for (const m of msgs ?? []) {
    await tg("editMessageText", {
      chat_id: m.chat_id, message_id: m.message_id, text: requestText(r), parse_mode: "HTML",
      reply_markup: requestKeyboard(r),
    });
  }
}

// ---------------- status ----------------

type Child = {
  name: string; spent: number; allowed: number | null; window_from: string; window_to: string;
  requires_unlock: boolean; unlocked: boolean; locked: boolean; tamper_locked: boolean; pending: number;
  now: { device: string; state: string; app: string | null; title: string | null } | null;
};

async function statusText(chatId: number) {
  const children = await rpc<Child[]>("tg_status", { p_chat_id: chatId });
  if (!children.length) return "В этом чате нет детей. Отметь их в админке: ⚙ Настройки → Telegram.";
  const now = new Intl.DateTimeFormat("ru-RU", { timeZone: TZ, hour: "2-digit", minute: "2-digit" }).format(new Date());
  const blocks = children.map((c) => {
    const head = c.now
      ? `🟢 <b>${esc(c.name)}</b> — за ПК «${esc(c.now.device)}»${c.now.app ? `: ${esc(c.now.app)}` : ""}${c.now.state === "idle" ? " (не трогает)" : ""}`
      : `⚪️ <b>${esc(c.name)}</b> — не за компьютером`;
    const full = c.window_from === "00:00" && c.window_to === "24:00";
    const lines = [head,
      `   ${dur(c.spent)}${c.allowed != null ? ` из ${dur(c.allowed)}` : ""} · ${full ? "весь день" : `${c.window_from}–${c.window_to}`}`];
    if (c.locked) lines.push("   🔒 заблокирован из админки");
    if (c.tamper_locked) lines.push("   ⚠️ заблокирован после вмешательства");
    if (c.requires_unlock && !c.unlocked) lines.push("   🚫 доступ на сегодня закрыт");
    if (c.pending) lines.push(`   ⏳ ждёт ответа на запрос (/requests)`);
    return lines.join("\n");
  });
  return `<b>Сейчас ${now}</b>\n\n${blocks.join("\n\n")}`;
}

// ---------------- Telegram updates ----------------

const HELP = [
  "Команды:",
  "/status — кто сколько сидит сейчас",
  "/requests — запросы, ждущие ответа",
  "/help — эта подсказка",
  "",
  "Новые запросы времени приходят сюда сами, ответить можно кнопками.",
].join("\n");

const NOT_LINKED =
  "Этот чат не подключён. В админке: ⚙ Настройки → Telegram → «Подключить чат», потом отправь сюда /link КОД.";

const nameOf = (u: { first_name?: string; last_name?: string; username?: string } | undefined) =>
  [u?.first_name, u?.last_name].filter(Boolean).join(" ") || u?.username || "?";

async function isLinked(chatId: number) {
  const { data } = await sb.from("tg_chats").select("chat_id").eq("chat_id", chatId).maybeSingle();
  return !!data;
}

// deno-lint-ignore no-explicit-any
async function onMessage(msg: any) {
  const text: string = msg.text ?? "";
  if (!text.startsWith("/")) return; // groups: ignore chatter
  const [rawCmd, ...args] = text.trim().split(/\s+/);
  const cmd = rawCmd.split("@")[0].toLowerCase();
  const chatId: number = msg.chat.id;
  const title = msg.chat.title ?? nameOf(msg.from);

  if ((cmd === "/start" || cmd === "/link") && args[0]) {
    const ok = await rpc<boolean>("tg_link_chat", { p_code: args[0], p_chat_id: chatId, p_title: title, p_by: nameOf(msg.from) });
    await sendText(chatId, ok ? `✅ Чат подключён.\n\n${HELP}` : "Код не подошёл или устарел. Получи новый в админке.");
    return;
  }
  if (!(await isLinked(chatId))) {
    await sendText(chatId, NOT_LINKED);
    return;
  }
  switch (cmd) {
    case "/status":
      await sendText(chatId, await statusText(chatId));
      break;
    case "/requests": {
      const pending = await rpc<Info[]>("tg_pending_requests", { p_chat_id: chatId });
      if (!pending.length) await sendText(chatId, "Запросов нет.");
      else for (const r of pending) await postRequest(r, [chatId]);
      break;
    }
    default:
      await sendText(chatId, HELP);
  }
}

// deno-lint-ignore no-explicit-any
async function onCallback(q: any) {
  const [action, idStr, minStr] = String(q.data ?? "").split(":");
  const id = Number(idStr);
  const chatId: number | undefined = q.message?.chat?.id;
  if (!id || !chatId || (action !== "a" && action !== "d")) {
    await tg("answerCallbackQuery", { callback_query_id: q.id });
    return;
  }
  try {
    const minutes = Number(minStr) || null;
    const r = await rpc<Info>("tg_decide", {
      p_chat_id: chatId, p_id: id, p_approve: action === "a", p_minutes: minutes, p_by: nameOf(q.from),
    });
    await tg("answerCallbackQuery", {
      callback_query_id: q.id,
      text: action === "d" ? "Отклонено" : r.kind === "unlock" ? "Доступ открыт" : `Одобрено: ${dur(r.granted_minutes)}`,
    });
    // Instant feedback here; the database trigger updates the other chats.
    await tg("editMessageText", {
      chat_id: chatId, message_id: q.message.message_id, text: requestText(r), parse_mode: "HTML",
      reply_markup: requestKeyboard(r),
    });
  } catch (e) {
    const msg = (e as { message?: string }).message ?? String(e);
    const text = /already decided/.test(msg) ? "Уже решено" : /not linked/.test(msg) ? "Чат не подключён" : "Ошибка, попробуй ещё раз";
    await tg("answerCallbackQuery", { callback_query_id: q.id, text, show_alert: !/already decided/.test(msg) });
    if (/already decided/.test(msg)) await refreshRequestMessages(id);
    else console.error("decide failed", msg);
  }
}

// ---------------- tamper notifications ----------------

async function tamperText(id: number): Promise<{ text: string; deviceId: string | null } | null> {
  const { data: e } = await sb.from("tamper_events")
    .select("type,details,windows_user,machine_name,device_id").eq("id", id).maybeSingle();
  if (!e) return null;
  let device = e.machine_name ?? "";
  if (e.device_id) {
    const { data: d } = await sb.from("devices").select("name").eq("id", e.device_id).maybeSingle();
    if (d?.name) device = d.name;
  }
  if (e.type === "login_blocked") {
    const until = e.details?.until ? time(e.details.until) : "?";
    const text = [
      `🚪 <b>${esc(e.details?.user ?? e.windows_user)}</b>: вход в Windows закрыт до <b>${until}</b>`,
      `${esc(e.details?.attempts ?? "10")} раз подряд входил в заблокированное время.`,
      `<i>${esc(device)}</i>`,
      "Запрет снимется сам. Раньше: добавь время в админке (если кончился лимит) или на ПК от администратора запусти reset-logon.cmd.",
    ].join("\n");
    return { text, deviceId: e.device_id };
  }
  return null;
}

// ---------------- internal events from the database ----------------

// deno-lint-ignore no-explicit-any
async function onInternal(body: any) {
  switch (body.event) {
    case "request": {
      // Only chats that hear about this child.
      const r = await rpc<Info>("tg_request_info", { p_id: body.request_id });
      const { data: req } = await sb.from("time_requests").select("child_id").eq("id", body.request_id).maybeSingle();
      if (!r || !req) return { ok: true };
      const chats = await rpc<number[]>("tg_chats_for_child", { p_child: req.child_id });
      await postRequest(r, chats ?? []);
      return { ok: true };
    }
    case "decided":
      await refreshRequestMessages(body.request_id);
      return { ok: true };
    case "tamper": {
      // request_id carries the tamper event id here
      const notice = await tamperText(body.request_id);
      if (!notice) return { ok: true };
      const chats = notice.deviceId ? await rpc<number[]>("tg_chats_for_device", { p_device: notice.deviceId }) : [];
      for (const chatId of chats ?? []) await sendText(chatId, notice.text);
      return { ok: true };
    }
    case "setup": {
      const cfg = await config();
      const webhook = await tg("setWebhook", {
        url: cfg.function_url, secret_token: cfg.secret,
        allowed_updates: ["message", "callback_query"], drop_pending_updates: true,
      });
      const commands = await tg("setMyCommands", { commands: [
        { command: "status", description: "Кто сколько сидит сейчас" },
        { command: "requests", description: "Запросы, ждущие ответа" },
        { command: "help", description: "Подсказка" },
      ] });
      const me = await tg("getMe", {});
      return { webhook, commands, me: me.result?.username ?? null };
    }
    default:
      return { ok: false, error: "unknown event" };
  }
}

// ---------------- entry ----------------

Deno.serve(async (req) => {
  if (req.method !== "POST") return new Response("not found", { status: 404 });
  if (!TOKEN) return new Response("TELEGRAM_BOT_TOKEN is not set", { status: 500 });
  const { secret } = await config();
  const fromTelegram = req.headers.get("x-telegram-bot-api-secret-token") === secret;
  const internal = req.headers.get("x-internal-secret") === secret;
  if (!fromTelegram && !internal) return new Response("forbidden", { status: 403 });

  const body = await req.json().catch(() => ({}));
  try {
    if (internal) return Response.json(await onInternal(body));
    if (body.message) await onMessage(body.message);
    else if (body.callback_query) await onCallback(body.callback_query);
  } catch (e) {
    // Telegram retries on errors; log and acknowledge so one bad update does not loop.
    console.error("update failed", e);
  }
  return Response.json({ ok: true });
});
