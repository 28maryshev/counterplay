// Пояснения к патчу из патчноута Riot — то, чего в цифрах не видно.
//
// Сравнение данных (lib/patchDiff.js) показывает ЧТО изменилось, до единицы, и
// ошибиться там нельзя. Но по числам не понять, что чемпиона переработали
// целиком, что поменяли систему или карту, что вышел новый чемпион. Это есть
// только в тексте патчноута, а текст приходится пересказывать — значит,
// возможна ошибка.
//
// Отсюда три правила, на которых всё здесь держится:
//
// 1. Никаких чисел. Цифры у нас уже есть — измеренные. Строка с числом из
//    пересказа отбрасывается целиком, даже если число верное: отличить верное
//    от выдуманного игрок не сможет, а мы обещаем ему обратное.
// 2. Только сайт Riot. Поиск ограничен leagueoflegends.com — источник
//    официальный, и ссылку на него видно под пояснениями.
// 3. Отдельным блоком и с пометкой. Пересказ не должен стоять в одном списке с
//    измерениями.
//
// Не получилось — блока просто нет. Пояснения приятны, но сводка без них
// остаётся полной.
const anthropic = require('./anthropic');
const { kvGet, kvSet } = require('../db/botDb');
const logger = require('./logger');

// Sonnet хватает: задача — прочитать страницу и пересказать три предложения.
// Раз в патч это копейки, но дороже, чем нужно, платить незачем.
const MODEL = 'claude-sonnet-5';

// Модель ходит только на сайт Riot: патчноут там, а чужие пересказы нам не нужны
// ни по качеству, ни по принципу.
//
// Читать страницу напрямую оказалось и точнее, и дешевле, чем искать. Поиск
// отдаёт куски страниц, и по ним модель решала, что ничего не нашла; чтобы
// всё-таки прочитать, она пускалась перебирать выдачу и упиралась в потолок
// ответа, не написав ни строчки. Адрес патчноута предсказуем, поэтому мы просто
// говорим его — а поиск остаётся рядом на случай, если Riot поменяет адреса.
const TOOLS = [
  { type: 'web_fetch_20260209', name: 'web_fetch', max_uses: 3, allowed_domains: ['leagueoflegends.com'] },
  { type: 'web_search_20260209', name: 'web_search', max_uses: 1, allowed_domains: ['leagueoflegends.com'] }
];

/** Адреса патчноута. Riot менял схему, поэтому их два — какой живой, увидит модель. */
function noteUrls(patch) {
  const slug = String(patch).replace('.', '-');
  return [
    `https://www.leagueoflegends.com/en-us/news/game-updates/league-of-legends-patch-${slug}-notes/`,
    `https://www.leagueoflegends.com/en-us/news/game-updates/patch-${slug}-notes/`
  ];
}

// Поиск с чтением страниц идёт долго — это не интерактивный запрос.
const TIMEOUT_MS = 240000;
const MAX_LINES = 3;

// Запас на ответ нужен щедрый. С маленьким лимитом модель тратила его на сам
// поиск и упиралась в потолок, не успев написать ни строчки: ответ приходил
// пустым, хотя патчноут был найден и прочитан.
const MAX_TOKENS = 6000;

// Патчноут задним числом не меняется, поэтому удачный ответ храним: повторный
// вопрос про тот же патч ничего не стоит. Это же держит расходы в рамках, когда
// пояснения просят командой.
const cacheKey = (patch) => `patchnotes_${patch}`;

function cached(patch) {
  try {
    const raw = kvGet(cacheKey(patch));
    if (!raw) return null;
    const v = JSON.parse(raw);
    return v && Array.isArray(v.lines) && v.lines.length ? v : null;
  } catch {
    return null;
  }
}

function buildPrompt(patch, focus) {
  const touched = focus && focus.length ? focus.slice(0, 20).join(', ') : '';
  const [first, second] = noteUrls(patch);
  return [
    `Read the official League of Legends patch ${patch} notes. Fetch this address first:`,
    first,
    `If that page does not exist, fetch ${second}, and only if neither works, search leagueoflegends.com for the patch ${patch} notes.`,
    '',
    'Write what a player needs to know about this patch that plain numbers cannot show:',
    'champion reworks or mid-scope updates, reworked items, system or map changes,',
    'a new champion, a change in how something works rather than by how much.',
    touched ? `Our own data says these were touched: ${touched}.` : '',
    '',
    'Scope: a ranked game on Summoner’s Rift, and nothing else. Ignore limited-time',
    'and alternate modes entirely — Arena, ARAM, Classic mode, events, rotating queues —',
    'however large their section of the notes is.',
    '',
    'Rules:',
    '- at most 3 bullets, one sentence each, each starting with "- ";',
    '- NO NUMBERS anywhere: no values, no percentages, no cooldowns, no prices.',
    '  The exact numbers are already shown to the player from the game data;',
    '  your job is only what they do not convey;',
    '- skip skins, esports, bundles, bugfixes and cosmetics;',
    '- skip anything that is only a value being raised or lowered;',
    '- plain factual voice, no marketing, no exclamation marks, English;',
    '- if the notes for this patch cannot be found, or nothing qualifies,',
    '  answer with exactly NONE and nothing else.',
    '',
    'Answer with the bullet list (or NONE) and nothing else: no preamble, no reasoning,',
    'no closing remark. Every line of the answer must start with "- ".'
  ]
    .filter(Boolean)
    .join('\n');
}

/**
 * Пояснения к патчу. patch — игровой номер («26.18»), focus — что наше
 * сравнение нашло изменённым (помогает модели смотреть в нужную сторону).
 * allowFetch=false отдаёт только уже сохранённое: за деньги ходит тот, кто
 * публикует патч, а не каждый, кто спросил.
 *
 * Возвращает { lines, sources } или null, если пояснений нет.
 */
async function forPatch(ctx, { patch, focus = [], allowFetch = true } = {}) {
  const saved = cached(patch);
  if (saved) return saved;
  if (!allowFetch) return null;

  const r = await anthropic.ask(ctx, {
    prompt: buildPrompt(patch, focus),
    model: MODEL,
    maxTokens: MAX_TOKENS,
    tools: TOOLS,
    timeoutMs: TIMEOUT_MS,
    label: `patch notes ${patch}`
  });
  if (!r.ok) {
    logger.info(`patchContext ${patch}: пояснений не будет (${r.kind}: ${r.detail})`);
    return null;
  }

  const lines = keepSafeLines(r.text, patch);
  if (!lines.length) {
    logger.info(`patchContext ${patch}: пересказ не прошёл отбор`);
    return null;
  }

  // Ссылка нужна на сам патчноут, а не на любую страницу, попавшуюся поиску.
  const sources = r.sources
    .filter((s) => /leagueoflegends\.com/i.test(s.url) && /patch/i.test(s.url))
    .slice(0, 1);

  const result = { lines, sources };
  try {
    kvSet(cacheKey(patch), JSON.stringify(result));
  } catch (e) {
    logger.warn(`patchContext ${patch}: не сохранилось — ${e.message}`);
  }
  return result;
}

/**
 * Отбор строк — тот самый рубеж, из-за которого пересказ не может выдать себя
 * за измерение.
 *
 * Берём ТОЛЬКО пункты списка. Модель иногда не выдерживает форму и вываливает
 * ход мысли перед ответом («All value tweaks, no functional change. Now check…»);
 * без маркера такая строка отправилась бы игроку как пояснение. Что маркера не
 * имеет — не ответ.
 *
 * Числа выбрасываются вместе со строкой, даже если число верное: отличить
 * верное от выдуманного игрок не сможет. Номер самого патча за число не
 * считаем — «in patch 26.18» безобидно.
 */
function keepSafeLines(text, patch) {
  const out = [];
  for (const raw of String(text || '').split('\n')) {
    const trimmed = raw.trim();
    if (!/^[-*•]\s+/.test(trimmed)) continue;
    const line = trimmed.replace(/^[-*•]\s+/, '').trim();
    if (!line || /^NONE$/i.test(line)) continue;
    const withoutPatch = line.split(String(patch)).join(' ');
    if (/\d/.test(withoutPatch)) continue; // число в пересказе — строку прочь
    if (OFF_TOPIC.test(line) || ABOUT_NOTES.test(line)) continue;
    out.push(line);
    if (out.length === MAX_LINES) break;
  }
  return out;
}

// Просьба в запросе — не гарантия. Пересказ сбивался и на то, что мы велели
// пропустить (в 26.18 выдал абзац про продажу хроматиков), и на разговор о
// самом патчноуте вместо его содержания («никаких изменений тут не нашлось»).
// Игроку не нужно ни то, ни другое, поэтому отсекаем здесь, а не надеемся.
//
// Отдельные режимы — та же беда, только заметнее: в патчноуте им отводят много
// места, и пересказ охотно берёт оттуда. Но сводка выше строго про Ущелье, и
// пояснения к ней должны быть о том же, иначе игрок читает про Арену там, где
// ждал объяснения к своим цифрам.
const OFF_TOPIC = new RegExp(
  '\\b(' +
    [
      'skins?|chromas?|bundles?|battle pass|event pass|esports?|emotes?|ward skin|cosmetics?|merch|icons?',
      'arena|aram|league classic|classic mode|bravery|nexus blitz|urf|one for all|swiftplay|brawl',
      'teamfight tactics|rotating (game )?mode'
    ].join('|') +
    ')\\b',
  'i'
);
const ABOUT_NOTES =
  /\b(patch notes|these notes|the notes|nothing (else )?qualifies|cannot be reported|can be reported|not mentioned|no .{0,40}(changes|updates) (appear|are listed))\b/i;

module.exports = { forPatch, _internal: { keepSafeLines, buildPrompt } };
