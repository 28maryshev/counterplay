// Свежесть данных: официальный патч LoL (Data Dragon) + готовность НАШЕЙ базы к
// нему + что уже опубликовано (сайт / программа / руны). Используется командой
// /patch и cron'ом patchWatch. Пороги готовности синхронны с политикой удержания
// патча в пайплайне (pipeline/freshness.py): новый патч не «промоутим», пока по
// нему не набралось достаточно данных — иначе тир-лист и рекомендации шумят.
const config = require('../config');
const mainDb = require('../db/mainDb');
const logger = require('./logger');

const DDRAGON = 'https://ddragon.leagueoflegends.com/api/versions.json';
const MAIN_BUCKET = 'emerald';
const READY_MIN_GAMES = 150; // взвеш. игр на «чемпион+роль», чтобы считать пару набранной
const READY_FRACTION = 0.7; // доля пар прошлого патча, покрытых новым → патч «готов»

// «16.14.1» → «16.14»; сравнение патчей по числам.
const toMajor = (v) => (v ? String(v).split('.').slice(0, 2).join('.') : null);
const cmpPatch = (a, b) => {
  const [a1, a2] = a.split('.').map(Number);
  const [b1, b2] = b.split('.').map(Number);
  return a1 - b1 || (a2 || 0) - (b2 || 0);
};

async function fetchJson(url, timeoutMs = 10000) {
  const signal = AbortSignal.timeout ? AbortSignal.timeout(timeoutMs) : undefined;
  const r = await fetch(url, {
    headers: { 'User-Agent': 'counterplay-bot', Accept: 'application/json' },
    redirect: 'follow',
    signal
  });
  if (!r.ok) throw new Error(`GET ${url} -> ${r.status}`);
  return r.json();
}

/** Актуальный патч из официального источника (Data Dragon). */
async function officialPatch() {
  const arr = await fetchJson(DDRAGON);
  return toMajor(arr[0]);
}

// Сколько «чемпион+роль» в основном бакете набрали >= READY_MIN_GAMES на патче.
function coverage(db, patch) {
  const row = db
    .prepare(
      `SELECT COUNT(*) n FROM (
         SELECT champion_id, role, SUM(games) g FROM base_wr
         WHERE patch=? AND tier_bucket=? GROUP BY champion_id, role HAVING g >= ?)`
    )
    .get(patch, MAIN_BUCKET, READY_MIN_GAMES);
  return row ? row.n : 0;
}

/** Состояние нашей базы: патчи, готовность новейшего, «основной готовый» патч. */
function dbState() {
  const db = mainDb.get();
  if (!db) return null;
  try {
    const patches = db
      .prepare('SELECT DISTINCT patch FROM base_wr')
      .all()
      .map((r) => r.patch)
      .filter((p) => /^\d+\.\d+$/.test(p))
      .sort(cmpPatch)
      .reverse();
    if (!patches.length) return { patches: [] };
    const newest = patches[0];
    const prev = patches[1] || null;
    const covNew = coverage(db, newest);
    const covPrev = prev ? coverage(db, prev) : covNew;
    const fraction = covPrev > 0 ? covNew / covPrev : 1;
    const ready = fraction >= READY_FRACTION;
    // Основной готовый патч = новейший, если он «готов», иначе держим предыдущий.
    const primary = ready ? newest : prev || newest;
    return { patches, newest, prev, covNew, covPrev, fraction, ready, primary };
  } catch (e) {
    logger.warn(`freshness dbState: ${e.message}`);
    return null;
  }
}

/** Опубликованные патчи: программа (релиз), руны (манифест), сайт (/api/version). */
async function published() {
  const out = { program: null, runes: null, site: null, siteRaw: null };
  await Promise.all([
    fetchJson(config.dataVersionUrl)
      .then((v) => (out.program = toMajor(v.patch)))
      .catch((e) => logger.warn(`freshness program: ${e.message}`)),
    fetchJson(`${config.siteUrl}/api/stats/v1/manifest.json`)
      .then((v) => (out.runes = toMajor(v.patch)))
      .catch((e) => logger.warn(`freshness runes: ${e.message}`)),
    fetchJson(`${config.siteUrl}/api/version`)
      .then((v) => {
        out.siteRaw = v;
        out.site = toMajor(v.tiersPatch || v.draftPatch);
      })
      .catch((e) => logger.warn(`freshness site: ${e.message}`))
  ]);
  return out;
}

/** Полный снимок свежести для /patch и patchWatch. */
async function status() {
  const [official, pub] = await Promise.all([officialPatch().catch(() => null), published()]);
  return { official, db: dbState(), published: pub, ts: new Date().toISOString() };
}

module.exports = {
  status,
  officialPatch,
  dbState,
  published,
  toMajor,
  cmpPatch,
  READY_MIN_GAMES,
  READY_FRACTION,
  MAIN_BUCKET
};
