// Один вход к API модели для всего бота.
//
// Зачем отдельный файл: важен не столько удобный вызов, сколько честный разбор
// отказа. Модель у нас делает две вещи — сводит заметки нескольких релизов и
// добавляет контекст к патчу, — и обе не критичны: не ответила, значит пост
// выйдет без них. Опасно другое: перестать работать МОЛЧА. Кончились деньги на
// счёте — это видно только в логах, а бот продолжает бодро постить всё то же,
// только без сводок. Поэтому про деньги здесь говорится вслух и лично
// владельцу, один раз в сутки, и отдельно сообщается, когда всё снова заработало.
const config = require('../config');
const logger = require('./logger');
const { kvGet, kvSet } = require('../db/botDb');

const API = 'https://api.anthropic.com/v1/messages';
const DEFAULT_MODEL = 'claude-sonnet-5';

// Отметка о том, что про пустой счёт уже сказано. Хранит дату, чтобы напоминать
// раз в сутки: каждый час — это не забота, а навязчивость.
const KV_BILLING = 'anthropic_billing_alert';
const REMIND_AFTER_MS = 24 * 60 * 60 * 1000;

/** Кончились деньги? Ответ на это у API один — 400 с текстом про баланс. */
function isBillingProblem(status, detail) {
  if (status === 402) return true;
  if (status !== 400 && status !== 403) return false;
  return /credit balance|billing|insufficient (funds|credit)|purchase credits/i.test(detail || '');
}

/**
 * Запрос к модели. Возвращает разобранный результат, никогда не бросает.
 *
 * Успех: { ok: true, text, sources }. Отказ: { ok: false, kind, detail }, где
 * kind — 'no_key' | 'no_credit' | 'rate_limit' | 'overloaded' | 'http' |
 * 'network' | 'empty'. Вызывающему достаточно проверить ok: обо всём, что
 * требует внимания человека, здесь уже сказано.
 */
async function ask(ctx, { prompt, model = DEFAULT_MODEL, maxTokens = 1200, tools, timeoutMs = 60000, label = 'api' }) {
  const key = process.env.ANTHROPIC_API_KEY;
  if (!key) return { ok: false, kind: 'no_key', detail: 'ANTHROPIC_API_KEY не задан' };

  const body = { model, max_tokens: maxTokens, messages: [{ role: 'user', content: prompt }] };
  if (tools) body.tools = tools;

  let res;
  let raw;
  try {
    res = await fetch(API, {
      method: 'POST',
      headers: { 'x-api-key': key, 'anthropic-version': '2023-06-01', 'content-type': 'application/json' },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(timeoutMs)
    });
    raw = await res.text();
  } catch (e) {
    logger.warn(`${label}: запрос не дошёл — ${e.message}`);
    return { ok: false, kind: 'network', detail: e.message };
  }

  if (!res.ok) {
    if (isBillingProblem(res.status, raw)) {
      await reportBilling(ctx, label, raw);
      return { ok: false, kind: 'no_credit', detail: raw.slice(0, 300) };
    }
    const kind = res.status === 429 ? 'rate_limit' : res.status === 529 ? 'overloaded' : 'http';
    logger.warn(`${label}: модель ответила ${res.status} — ${raw.slice(0, 200)}`);
    return { ok: false, kind, detail: `${res.status}` };
  }

  let data;
  try {
    data = JSON.parse(raw);
  } catch {
    return { ok: false, kind: 'empty', detail: 'ответ не разобрался' };
  }

  const blocks = data.content || [];
  const text = blocks
    .filter((b) => b.type === 'text')
    .map((b) => b.text || '')
    .join('')
    .trim();

  // Ссылки берём из результатов инструментов: в тексте их может не быть, а
  // показывать пересказ без источника нельзя. Поиск отдаёт список результатов,
  // чтение страницы — одну, поэтому разбираем оба вида.
  const sources = [];
  const addSource = (url, title) => {
    if (url && !sources.some((s) => s.url === url)) sources.push({ url, title: title || url });
  };
  for (const b of blocks) {
    if (b.type === 'web_search_tool_result' && Array.isArray(b.content))
      for (const r of b.content) addSource(r && r.url, r && r.title);
    if (b.type === 'web_fetch_tool_result' && b.content)
      addSource(b.content.url, (b.content.content && b.content.content.title) || b.content.url);
  }

  // Запрос прошёл — значит деньги есть. Если мы про них жаловались, скажем, что
  // всё снова работает: иначе владелец будет ждать письма, которого не будет.
  await clearBilling(ctx);

  if (!text) return { ok: false, kind: 'empty', detail: `stop_reason=${data.stop_reason}` };
  return { ok: true, text, sources, usage: data.usage };
}

/** Сказать владельцу, что на счёте API пусто. Не чаще раза в сутки. */
async function reportBilling(ctx, label, detail) {
  logger.error(`${label}: на счёте API закончились деньги — функции модели отключены`);

  const last = Number(kvGet(KV_BILLING) || 0);
  if (last && Date.now() - last < REMIND_AFTER_MS) return;
  kvSet(KV_BILLING, String(Date.now()));

  const ids = (ctx && ctx.config ? ctx.config.adminIds : config.adminIds) || [];
  if (!ids.length || !ctx || !ctx.client) return;

  const text =
    '⚠️ **Счёт API пуст.** Пока денег нет, бот работает без модели: анонс релиза ' +
    'выйдет склейкой заметок, а к сводке патча не будет пояснений из патчноута. ' +
    'Сами цифры патча считаются без модели и не пострадали.\n' +
    'Напомню об этом не раньше чем через сутки, а когда счёт пополнится — скажу.';
  for (const id of ids) {
    try {
      const user = await ctx.client.users.fetch(id);
      await user.send(text);
    } catch (e) {
      logger.warn(`billing alert: не доставлено ${id} — ${e.message}`);
    }
  }
}

/** Счёт пополнили — снять отметку и сказать об этом. */
async function clearBilling(ctx) {
  if (!kvGet(KV_BILLING)) return;
  kvSet(KV_BILLING, '');
  logger.info('API: счёт снова в порядке, функции модели вернулись');

  const ids = (ctx && ctx.config ? ctx.config.adminIds : config.adminIds) || [];
  if (!ids.length || !ctx || !ctx.client) return;
  for (const id of ids) {
    try {
      const user = await ctx.client.users.fetch(id);
      await user.send('✅ Счёт API снова в порядке — сводки и пояснения работают.');
    } catch {
      /* не доставили — не беда, отметка уже снята */
    }
  }
}

/** Идут ли сейчас функции модели. Для /admin status. */
function billingState() {
  const last = Number(kvGet(KV_BILLING) || 0);
  if (!process.env.ANTHROPIC_API_KEY) return { ok: false, reason: 'no key' };
  if (!last) return { ok: true };
  return { ok: false, reason: 'out of credit', since: new Date(last).toISOString().slice(0, 16).replace('T', ' ') };
}

module.exports = { ask, billingState, isBillingProblem, KV_BILLING };
