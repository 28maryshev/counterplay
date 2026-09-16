// patchDiff — что именно Riot изменил между двумя патчами.
//
// Источник один и он официальный: Data Dragon (item.json, championFull.json,
// runesReforged.json, summoner.json). Мы эти файлы не пересказываем, а
// СРАВНИВАЕМ: цену предмета, его характеристики, путь сборки, кулдауны и
// стоимость умений, базовые статы чемпионов, тексты описаний. Отсюда главное
// свойство сводки — в ней физически не может появиться числа, которого нет у
// Riot. Поэтому она делается кодом, а не пересказом патчноута: пересказ ошибётся
// в цифре молча, и отличить его ошибку от наших измерений будет уже нельзя.
//
// Чего здесь нет. «Почему» изменения — это текст патчноута, его тут не будет.
// Влияния на винрейты тоже: оно считается по матчам, а они на момент выхода
// патча ещё не собраны.
//
// Всё, что не Ущелье Призывателей, отбрасывается: правки ARAM и Арены игрока в
// ранкеде не касаются, а шуму от них больше, чем пользы, — в тот же патч 16.18
// Riot трогал вариант Banshee's Veil, которого на Ущелье просто нет.
const fs = require('fs');
const path = require('path');
const config = require('../config');
const logger = require('./logger');

const VERSIONS_URL = 'https://ddragon.leagueoflegends.com/api/versions.json';
const CDN = 'https://ddragon.leagueoflegends.com/cdn';
const LOCALE = 'en_US';
const SR_MAP = '11'; // Ущелье Призывателей
const CLASSIC_MODE = 'CLASSIC'; // им помечены спеллы обычной игры

// Сколько патчей держим в кэше. Файлы патча неизменны, так что кэш вечный: раз
// скачали — больше никогда. Три-четыре версии хватает на любое сравнение
// «текущий с предыдущим» и весят они копейки.
const KEEP_VERSIONS = 4;

// Путь к кэшу считаем от корня бота, а не от текущей папки: модуль запускают и
// из контейнера (cwd=/app), и руками из репозитория.
const BOT_ROOT = path.resolve(__dirname, '..');
const dataRoot = () =>
  path.isAbsolute(config.dataDir) ? config.dataDir : path.join(BOT_ROOT, config.dataDir);
const cacheDir = (version) => path.join(dataRoot(), 'ddragon', version);

// ─── загрузка данных ────────────────────────────────────────────────────────

async function getJson(url, timeoutMs = 30000) {
  const r = await fetch(url, {
    headers: { 'User-Agent': 'counterplay-bot', Accept: 'application/json' },
    redirect: 'follow',
    signal: AbortSignal.timeout(timeoutMs)
  });
  if (!r.ok) throw new Error(`GET ${url} -> ${r.status}`);
  return r.json();
}

/** Файл данных конкретной версии. Скачивается один раз, дальше с диска. */
async function dataFile(version, name) {
  const file = path.join(cacheDir(version), name);
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    /* нет в кэше — качаем ниже */
  }
  const json = await getJson(`${CDN}/${version}/data/${LOCALE}/${name}`);
  try {
    fs.mkdirSync(cacheDir(version), { recursive: true });
    fs.writeFileSync(file, JSON.stringify(json));
  } catch (e) {
    logger.warn(`patchDiff: кэш не записался (${e.message}) — работаем без него`);
  }
  return json;
}

/** Убрать кэш старых патчей: сравниваем всегда свежие, прошлые не понадобятся. */
function sweepCache(keep = KEEP_VERSIONS) {
  try {
    const root = path.join(dataRoot(), 'ddragon');
    const dirs = fs
      .readdirSync(root)
      .filter((d) => /^\d+\.\d+\.\d+$/.test(d))
      .sort(cmpVersion)
      .reverse();
    for (const old of dirs.slice(keep)) fs.rmSync(path.join(root, old), { recursive: true, force: true });
  } catch {
    /* нечего чистить */
  }
}

// ─── версии ─────────────────────────────────────────────────────────────────

/** «16.18.1» → [16, 18, 1]; сравнение по числам, а не по строке. */
function cmpVersion(a, b) {
  const x = String(a).split('.').map(Number);
  const y = String(b).split('.').map(Number);
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    const d = (x[i] || 0) - (y[i] || 0);
    if (d) return d;
  }
  return 0;
}

const major = (v) => String(v).split('.').slice(0, 2).join('.');

/**
 * Пара версий для сравнения. Патч задаётся как «16.18» — берём его самую
 * свежую сборку: внутри патча Riot иногда выпускает 16.18.2, и сравнивать надо
 * именно её, иначе поздние правки потеряются.
 */
async function resolvePair({ from, to } = {}) {
  const all = await getJson(VERSIONS_URL);
  const builds = all.filter((v) => /^\d+\.\d+\.\d+$/.test(v)).sort(cmpVersion).reverse();
  const newestOf = (patch) => builds.find((v) => major(v) === major(patch));

  const toVersion = to ? newestOf(to) : builds[0];
  if (!toVersion) throw new Error(`патч ${to} не найден в Data Dragon`);
  const fromVersion = from
    ? newestOf(from)
    : builds.find((v) => cmpVersion(major(v) + '.0', major(toVersion) + '.0') < 0);
  if (!fromVersion) throw new Error(`патч ${from || 'предыдущий'} не найден в Data Dragon`);

  return { fromVersion, toVersion, from: major(fromVersion), to: major(toVersion) };
}

// ─── разметка Riot → обычный текст ──────────────────────────────────────────

const ENTITIES = { nbsp: ' ', amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", '#39': "'" };

function decode(s) {
  return s.replace(/&(#\d+|[a-z]+);/gi, (m, e) => {
    if (e[0] === '#') return String.fromCharCode(Number(e.slice(1)));
    return ENTITIES[e.toLowerCase()] ?? m;
  });
}

/**
 * Описания Riot отдаёт разметкой: <stats>, <passive>, <magicDamage>, <br>.
 * Сравнивать её как есть нельзя — перестановка тега выглядела бы изменением
 * баланса. Приводим к обычному тексту, а подстановки вида {{ qdamagecalc }}
 * оставляем видимыми: в подсказках умений числа живут именно в них.
 */
function plain(s) {
  if (!s) return '';
  let t = String(s);
  t = t.replace(/<flavorText>[\s\S]*?<\/flavorText>/gi, ' '); // сказка про предмет
  t = t.replace(/<br\s*\/?>/gi, '\n');
  t = t.replace(/<[^>]+>/g, ' ');
  t = t.replace(/\{\{\s*([^{}]+?)\s*\}\}/g, '{$1}');
  t = decode(t);
  t = t.replace(/[ \t]+/g, ' ');
  t = t.replace(/ ?\n ?/g, '\n');
  t = t.replace(/\n{2,}/g, '\n');
  // Снятый тег оставляет пробел перед точкой, и «Penetration.» против
  // «Penetration .» читалось бы как правка текста, хотя правки нет.
  t = t.replace(/\s+([.,;:!?%)])/g, '$1');
  return t.trim();
}

// ─── сравнение текстов ──────────────────────────────────────────────────────

const tokenize = (s) => plain(s).split(/\s+/).filter(Boolean);

// Выше этого произведения длин точное сравнение считать не стоит: такие тексты
// Riot не правит по слову, он их переписывает целиком.
const LCS_LIMIT = 250000;

/** Наибольшая общая подпоследовательность → список блоков eq/del/ins. */
function lcsBlocks(a, b) {
  const n = a.length;
  const m = b.length;
  const dp = Array.from({ length: n + 1 }, () => new Int32Array(m + 1));
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);

  const blocks = [];
  const push = (type, token) => {
    const last = blocks[blocks.length - 1];
    if (last && last.type === type) last.tokens.push(token);
    else blocks.push({ type, tokens: [token] });
  };
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) push('eq', a[i++]), j++;
    else if (dp[i + 1][j] >= dp[i][j + 1]) push('del', a[i++]);
    else push('ins', b[j++]);
  }
  while (i < n) push('del', a[i++]);
  while (j < m) push('ins', b[j++]);
  return blocks;
}

/**
 * Изменённые куски текста с окружением — чтобы «3 → 4» читалось не голым
 * числом, а как «…for 3 → 4 seconds…».
 */
function textHunks(oldText, newText, ctx = 5) {
  const a = tokenize(oldText);
  const b = tokenize(newText);
  if (a.join(' ') === b.join(' ')) return [];

  // Общее начало и общий хвост срезаем до дорогого сравнения: обычно правится
  // одно число в середине, и без среза LCS считал бы весь текст.
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head++;
  let tail = 0;
  while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail])
    tail++;

  const midA = a.slice(head, a.length - tail);
  const midB = b.slice(head, b.length - tail);
  const before = a.slice(0, head);
  const after = a.slice(a.length - tail);

  if (midA.length * midB.length > LCS_LIMIT)
    return [{ before: before.slice(-ctx), removed: midA, added: midB, after: after.slice(0, ctx), rewritten: true }];

  const blocks = [
    ...(before.length ? [{ type: 'eq', tokens: before }] : []),
    ...lcsBlocks(midA, midB),
    ...(after.length ? [{ type: 'eq', tokens: after }] : [])
  ];

  const hunks = [];
  for (let k = 0; k < blocks.length; k++) {
    if (blocks[k].type === 'eq') continue;
    const removed = blocks[k].type === 'del' ? blocks[k].tokens : [];
    const added = blocks[k].type === 'ins' ? blocks[k].tokens : [];
    // del и ins подряд — это одна замена, а не два разных изменения.
    if (blocks[k].type === 'del' && blocks[k + 1] && blocks[k + 1].type === 'ins') {
      added.push(...blocks[k + 1].tokens);
      k++;
    }
    const prev = blocks.slice(0, k + 1).reverse().find((x) => x.type === 'eq');
    const next = blocks.slice(k + 1).find((x) => x.type === 'eq');
    hunks.push({
      before: prev ? prev.tokens.slice(-ctx) : [],
      removed,
      added,
      after: next ? next.tokens.slice(0, ctx) : []
    });
  }
  return hunks;
}

/**
 * Изменение только в подстановках — косметика, не баланс. Так, в 16.18 у Poppy
 * в подсказке «{movespeedmod}%» стало «{movespeedmod}»: процент переехал внутрь
 * переменной, число осталось прежним. А вот правка обычного слова значима —
 * «refund % of his current cooldown» и «…of his total cooldown» это разные
 * вещи, хотя цифр в них нет вовсе.
 */
function cosmetic(hunks) {
  const meaningful = (t) => !t.includes('{') && /[0-9A-Za-z]/.test(t);
  return hunks.every((h) => ![...h.removed, ...h.added].some(meaningful));
}

/**
 * Одно и то же значение, записанное в других единицах. Riot иногда переводит
 * величину из процентов в долю — «20/22.5/25» превращается в «0.2/0.225/0.25».
 * В данных это выглядит как обвальный нерф, хотя в игре не поменялось ничего,
 * поэтому такие пары отбрасываем: ровно стократный множитель сразу на всех
 * рангах балансом не бывает.
 */
function unitsOnly(from, to) {
  const a = String(from ?? '').split('/').map(Number);
  const b = String(to ?? '').split('/').map(Number);
  if (a.length !== b.length || a.some(Number.isNaN) || b.some(Number.isNaN)) return false;
  if (a.every((x) => x === 0)) return false;
  const ratios = a.map((x, i) => (x === 0 ? (b[i] === 0 ? null : NaN) : b[i] / x));
  const real = ratios.filter((r) => r !== null);
  if (!real.length || real.some(Number.isNaN)) return false;
  const k = real[0];
  if (!real.every((r) => Math.abs(r - k) <= 1e-6 * Math.abs(k))) return false;
  return Math.abs(k - 100) < 1e-6 || Math.abs(k - 0.01) < 1e-9;
}

/**
 * Значения умения уехали из effectBurn в другое место данных. Riot постепенно
 * переводит умения на новый формат, и старое поле при переезде обнуляется
 * целиком: «30/55/80/105/130 → 0» — это не обнуление урона, а смена места
 * хранения. Ни один патч не обнуляет умение, так что такую пару отбрасываем.
 */
const allZero = (v) => String(v ?? '').split('/').every((x) => Number(x) === 0);
const relocated = (from, to) => allZero(from) !== allZero(to);

/** «…for 3 → 4 seconds…» — в разметке Discord. */
function renderHunk(h) {
  const before = h.before.join(' ');
  const after = h.after.join(' ');
  let mid;
  if (h.removed.length && h.added.length)
    mid = h.rewritten ? '**rewritten**' : `**${h.removed.join(' ')} → ${h.added.join(' ')}**`;
  else if (h.added.length) mid = `**+ ${h.added.join(' ')}**`;
  else mid = `**− ${h.removed.join(' ')}**`;
  return [before && `…${before}`, mid, after && `${after}…`].filter(Boolean).join(' ').replace(/\n/g, ' ');
}

// ─── предметы ───────────────────────────────────────────────────────────────

const onRift = (it) => Boolean(it && it.maps && it.maps[SR_MAP] === true);
const num = (v) => (typeof v === 'number' ? v : Number(v));

function diffItems(a, b) {
  const changed = [];
  const added = [];
  const removed = [];
  const nameOf = (data, id) => (data[id] ? data[id].name : `#${id}`);

  for (const id of Object.keys(b)) {
    if (!onRift(b[id])) continue;
    const before = a[id];
    if (!before) {
      added.push({ id, name: b[id].name, cost: b[id].gold ? b[id].gold.total : null });
      continue;
    }
    const e = { id, name: b[id].name };
    let any = false;

    const goldBefore = before.gold ? before.gold.total : null;
    const goldAfter = b[id].gold ? b[id].gold.total : null;
    if (goldBefore !== goldAfter) {
      e.gold = { from: goldBefore, to: goldAfter };
      any = true;
    }

    const pathBefore = (before.from || []).join(',');
    const pathAfter = (b[id].from || []).join(',');
    if (pathBefore !== pathAfter) {
      e.path = {
        from: (before.from || []).map((c) => nameOf(a, c)),
        to: (b[id].from || []).map((c) => nameOf(b, c))
      };
      any = true;
    }

    const hunks = textHunks(before.description, b[id].description);
    if (hunks.length) {
      e.text = hunks;
      any = true;
    } else {
      // Характеристики Riot перечисляет в самом описании, поэтому сравнивать
      // их отдельно надо только тогда, когда текст НЕ менялся: иначе одно и то
      // же изменение попало бы в сводку дважды.
      const stats = [];
      for (const k of new Set([...Object.keys(before.stats || {}), ...Object.keys(b[id].stats || {})])) {
        const x = (before.stats || {})[k];
        const y = (b[id].stats || {})[k];
        if (num(x) !== num(y)) stats.push({ key: k, from: x ?? 0, to: y ?? 0 });
      }
      if (stats.length) {
        e.stats = stats;
        any = true;
      }
    }

    if (any) changed.push(e);
  }

  for (const id of Object.keys(a))
    if (onRift(a[id]) && !b[id]) removed.push({ id, name: a[id].name });

  return { changed, added, removed };
}

// ─── чемпионы ───────────────────────────────────────────────────────────────

// Подписи идут в пост бота, а он говорит с сообществом по-английски — как все
// остальные его сообщения.
const STAT_LABELS = {
  hp: 'health',
  hpperlevel: 'health/lvl',
  mp: 'mana',
  mpperlevel: 'mana/lvl',
  movespeed: 'move speed',
  armor: 'armor',
  armorperlevel: 'armor/lvl',
  spellblock: 'magic resist',
  spellblockperlevel: 'magic resist/lvl',
  attackrange: 'attack range',
  hpregen: 'health regen',
  hpregenperlevel: 'health regen/lvl',
  mpregen: 'mana regen',
  mpregenperlevel: 'mana regen/lvl',
  crit: 'crit',
  critperlevel: 'crit/lvl',
  attackdamage: 'attack damage',
  attackdamageperlevel: 'attack damage/lvl',
  attackspeed: 'attack speed',
  attackspeedperlevel: 'attack speed/lvl'
};

const SLOTS = ['Q', 'W', 'E', 'R'];
const SPELL_FIELDS = [
  ['cooldownBurn', 'cooldown'],
  ['costBurn', 'cost'],
  ['rangeBurn', 'range'],
  ['maxrank', 'ranks']
];

// Кроме обычных чемпионов в данных живут их варианты для отдельных режимов
// («Jade_Ahri» и подобные). На Ущелье их нет, а когда режим закрывают, они
// пропадают из файла разом — без этого фильтра сводка объявила бы, что Riot
// удалил из игры шесть десятков чемпионов.
const isChampion = (id) => !String(id).includes('_');

function diffChampions(a, b) {
  const changed = [];
  const added = [];
  const removed = [];

  for (const key of Object.keys(b)) {
    if (!isChampion(key)) continue;
    const before = a[key];
    if (!before) {
      added.push({ id: key, name: b[key].name, title: b[key].title });
      continue;
    }
    const e = { id: key, name: b[key].name, stats: [], spells: [], text: [] };

    for (const k of Object.keys(b[key].stats || {})) {
      const x = num((before.stats || {})[k]);
      const y = num(b[key].stats[k]);
      if (x !== y) e.stats.push({ key: k, label: STAT_LABELS[k] || k, from: x, to: y });
    }

    (b[key].spells || []).forEach((spell, i) => {
      const old = (before.spells || [])[i];
      if (!old) return;
      const s = { slot: SLOTS[i] || `S${i + 1}`, name: spell.name, changes: [], text: [] };

      for (const [field, label] of SPELL_FIELDS)
        if (String(old[field]) !== String(spell[field]))
          s.changes.push({ label, from: old[field], to: spell[field] });

      // Числа умения лежат в effectBurn: в самой подсказке на их месте стоит
      // подстановка, так что текст изменения урона не покажет — только это поле.
      const oldEff = old.effectBurn || [];
      const newEff = spell.effectBurn || [];
      for (let n = 0; n < Math.max(oldEff.length, newEff.length); n++) {
        if (oldEff[n] == null && newEff[n] == null) continue;
        if (String(oldEff[n]) === String(newEff[n])) continue;
        if (unitsOnly(oldEff[n], newEff[n]) || relocated(oldEff[n], newEff[n])) continue;
        s.changes.push({ label: `effect ${n}`, from: oldEff[n], to: newEff[n] });
      }

      if (JSON.stringify(old.datavalues || {}) !== JSON.stringify(spell.datavalues || {}))
        s.changes.push({ label: 'values', from: '—', to: 'changed' });

      const hunks = textHunks(old.tooltip, spell.tooltip);
      if (hunks.length && !cosmetic(hunks)) s.text = hunks;

      if (s.changes.length || s.text.length) e.spells.push(s);
    });

    const passiveHunks = textHunks(
      (before.passive || {}).description,
      (b[key].passive || {}).description
    );
    if (passiveHunks.length && !cosmetic(passiveHunks))
      e.text.push({ label: `Passive ${(b[key].passive || {}).name || ''}`.trim(), hunks: passiveHunks });

    if (e.stats.length || e.spells.length || e.text.length) changed.push(e);
  }

  for (const key of Object.keys(a))
    if (isChampion(key) && !b[key]) removed.push({ id: key, name: a[key].name });

  return { changed, added, removed };
}

// ─── руны ───────────────────────────────────────────────────────────────────

function flattenRunes(trees) {
  const out = new Map();
  for (const tree of trees || [])
    for (const slot of tree.slots || [])
      for (const rune of slot.runes || []) out.set(String(rune.id), { ...rune, tree: tree.name });
  return out;
}

function diffRunes(a, b) {
  const before = flattenRunes(a);
  const after = flattenRunes(b);
  const changed = [];
  const added = [];
  const removed = [];

  for (const [id, rune] of after) {
    const old = before.get(id);
    if (!old) {
      added.push({ id, name: rune.name, tree: rune.tree });
      continue;
    }
    // longDesc подробнее: в нём числа, которых в короткой строке нет.
    const hunks = textHunks(old.longDesc, rune.longDesc);
    const short = textHunks(old.shortDesc, rune.shortDesc);
    if (hunks.length || short.length)
      changed.push({ id, name: rune.name, tree: rune.tree, text: hunks.length ? hunks : short });
  }
  for (const [id, rune] of before) if (!after.has(id)) removed.push({ id, name: rune.name, tree: rune.tree });

  return { changed, added, removed };
}

// ─── заклинания призывателя ─────────────────────────────────────────────────

function diffSummoners(a, b) {
  const changed = [];
  for (const id of Object.keys(b)) {
    const spell = b[id];
    if (!(spell.modes || []).includes(CLASSIC_MODE)) continue;
    const old = a[id];
    if (!old) continue;
    const e = { id, name: spell.name, changes: [], text: [] };
    for (const [field, label] of [
      ['cooldownBurn', 'cooldown'],
      ['rangeBurn', 'range']
    ])
      if (String(old[field]) !== String(spell[field]))
        e.changes.push({ label, from: old[field], to: spell[field] });
    const hunks = textHunks(old.description, spell.description);
    if (hunks.length && !cosmetic(hunks)) e.text = hunks;
    if (e.changes.length || e.text.length) changed.push(e);
  }
  return { changed, added: [], removed: [] };
}

// ─── сводка целиком ─────────────────────────────────────────────────────────

/**
 * Полное сравнение двух патчей. from/to — в виде «16.17» / «16.18»; без
 * аргументов берётся последний патч и предыдущий.
 */
async function diff({ from, to } = {}) {
  const v = await resolvePair({ from, to });
  const [itemsA, itemsB, champA, champB, runesA, runesB, sumA, sumB] = await Promise.all([
    dataFile(v.fromVersion, 'item.json'),
    dataFile(v.toVersion, 'item.json'),
    dataFile(v.fromVersion, 'championFull.json'),
    dataFile(v.toVersion, 'championFull.json'),
    dataFile(v.fromVersion, 'runesReforged.json'),
    dataFile(v.toVersion, 'runesReforged.json'),
    dataFile(v.fromVersion, 'summoner.json'),
    dataFile(v.toVersion, 'summoner.json')
  ]);
  sweepCache();

  const report = {
    ...v,
    items: diffItems(itemsA.data, itemsB.data),
    champions: diffChampions(champA.data, champB.data),
    runes: diffRunes(runesA, runesB),
    summoners: diffSummoners(sumA.data, sumB.data)
  };
  report.total = ['items', 'champions', 'runes', 'summoners'].reduce(
    (n, k) => n + report[k].changed.length + report[k].added.length + report[k].removed.length,
    0
  );
  return report;
}

// ─── текст сводки ───────────────────────────────────────────────────────────

const arrow = (from, to) => `${from} → ${to}`;

function itemLines(e) {
  const out = [`**${e.name}**`];
  if (e.gold) out.push(`• cost: ${arrow(e.gold.from, e.gold.to)}`);
  if (e.path)
    out.push(`• builds from: ${e.path.from.join(' + ') || '—'} → ${e.path.to.join(' + ') || '—'}`);
  for (const s of e.stats || []) out.push(`• ${s.key}: ${arrow(s.from, s.to)}`);
  for (const h of e.text || []) out.push(`• ${renderHunk(h)}`);
  return out;
}

function championLines(e) {
  const out = [`**${e.name}**`];
  for (const s of e.stats) out.push(`• ${s.label}: ${arrow(s.from, s.to)}`);
  for (const sp of e.spells) {
    for (const c of sp.changes) out.push(`• ${sp.slot} ${sp.name} — ${c.label}: ${arrow(c.from, c.to)}`);
    for (const h of sp.text) out.push(`• ${sp.slot} ${sp.name} — ${renderHunk(h)}`);
  }
  for (const t of e.text) for (const h of t.hunks) out.push(`• ${t.label} — ${renderHunk(h)}`);
  return out;
}

/**
 * Сводка строками. Ничего не сокращает и не «резюмирует» — печатает ровно то,
 * что нашло сравнение.
 */
function render(report) {
  const lines = [];
  const section = (title, body) => {
    if (!body.length) return;
    lines.push(`__${title}__`, ...body, '');
  };

  const items = [];
  for (const e of report.items.changed) items.push(...itemLines(e), '');
  for (const e of report.items.added) items.push(`**${e.name}** — new item${e.cost ? `, ${e.cost} gold` : ''}`);
  for (const e of report.items.removed) items.push(`**${e.name}** — removed`);
  section('Items', items);

  // Системные правки Riot трогают целый класс разом: в 16.16 пятнадцати стрелкам
  // подняли сопротивление магии одной строкой. Пятнадцать одинаковых абзацев
  // подряд не читаются, поэтому совпадающие наборы правок сводим в один.
  const champs = [];
  const statOnly = new Map();
  for (const e of report.champions.changed) {
    if (e.spells.length || e.text.length) {
      champs.push(...championLines(e), '');
      continue;
    }
    const key = e.stats.map((s) => `${s.label}: ${arrow(s.from, s.to)}`).join(', ');
    if (!statOnly.has(key)) statOnly.set(key, []);
    statOnly.get(key).push(e.name);
  }
  for (const [changes, names] of statOnly)
    champs.push(`**${names.join(', ')}**${names.length > 1 ? ` (${names.length})` : ''}`, `• ${changes}`, '');
  for (const e of report.champions.added) champs.push(`**${e.name}**, ${e.title} — new champion`);
  for (const e of report.champions.removed) champs.push(`**${e.name}** — removed`);
  section('Champions', champs);

  const runes = [];
  for (const e of report.runes.changed) runes.push(`**${e.name}** (${e.tree})`, ...e.text.map((h) => `• ${renderHunk(h)}`), '');
  for (const e of report.runes.added) runes.push(`**${e.name}** (${e.tree}) — new rune`);
  for (const e of report.runes.removed) runes.push(`**${e.name}** (${e.tree}) — removed`);
  section('Runes', runes);

  const sums = [];
  for (const e of report.summoners.changed) {
    sums.push(`**${e.name}**`);
    for (const c of e.changes) sums.push(`• ${c.label}: ${arrow(c.from, c.to)}`);
    for (const h of e.text) sums.push(`• ${renderHunk(h)}`);
    sums.push('');
  }
  section('Summoner spells', sums);

  if (!lines.length) return ['No Summoner’s Rift changes between these patches.'];
  return lines;
}

module.exports = {
  diff,
  render,
  resolvePair,
  sweepCache,
  // наружу — для тестов и для будущих потребителей сводки
  _internal: {
    plain,
    textHunks,
    renderHunk,
    cosmetic,
    unitsOnly,
    relocated,
    diffItems,
    diffChampions,
    diffRunes,
    diffSummoners,
    cmpVersion
  }
};

// Запуск руками: node lib/patchDiff.js [откуда] [куда] [--json]
if (require.main === module) {
  const args = process.argv.slice(2);
  const json = args.includes('--json');
  const [from, to] = args.filter((a) => !a.startsWith('--'));
  diff({ from, to })
    .then((r) => {
      if (json) {
        console.log(JSON.stringify(r, null, 2));
        return;
      }
      console.log(`Patch ${r.from} → ${r.to} (${r.fromVersion} → ${r.toVersion}), changes: ${r.total}\n`);
      console.log(render(r).join('\n'));
    })
    .catch((e) => {
      console.error('patchDiff:', e.message);
      process.exitCode = 1;
    });
}
