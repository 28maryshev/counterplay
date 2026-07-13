// ЕДИНСТВЕННОЕ место с SQL к основной базе Counterplay.
// Схема (сверена с реальной базой): base_wr / matchup / synergy — ключи
// (champion_id INTEGER, role, [vs_champion_id|ally_id+ally_role], tier_bucket,
// patch TEXT, games, wins). Бакеты всегда агрегируем SUM.
//
// ВАЖНО: patch — TEXT («16.9» > «16.13» лексикографически), поэтому патчи
// сортируем численно и отбрасываем мусорные (< 5% объёма от максимального).
const mainDb = require('./mainDb');

const PATCH_WEIGHTS = [1.0, 0.7, 0.45]; // текущий, предыдущий, пред-предыдущий

class DataUnavailableError extends Error {
  constructor() {
    super('Main database snapshot is not loaded');
    this.name = 'DataUnavailableError';
  }
}

// Обёртка запросов: при SQLITE-ошибке один раз переоткрыть соединение и повторить
// (снапшот мог быть подменён из-под нас в редком гоночном окне).
function q(fn) {
  let db = mainDb.get();
  if (!db) throw new DataUnavailableError();
  try {
    return fn(db);
  } catch (e) {
    if (/SQLITE/i.test(String(e.code || e.message))) {
      mainDb.reopen();
      db = mainDb.get();
      if (!db) throw new DataUnavailableError();
      return fn(db);
    }
    throw e;
  }
}

// ── Патчи ────────────────────────────────────────────────────────────────

const cmpPatch = (a, b) => {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  return pa[0] - pb[0] || (pa[1] || 0) - (pb[1] || 0);
};

let _patchCache = { gen: -1, list: [] };

/** Содержательные патчи базы по возрастанию (мусор < 5% объёма отброшен). */
function patches() {
  if (_patchCache.gen === mainDb.generation()) return _patchCache.list;
  const rows = q((db) =>
    db.prepare('SELECT patch, SUM(games) g FROM base_wr GROUP BY patch').all()
  );
  const max = rows.reduce((m, r) => Math.max(m, r.g), 0);
  const list = rows
    .filter((r) => r.g >= max * 0.05 && /^\d+\.\d+$/.test(r.patch))
    .map((r) => r.patch)
    .sort(cmpPatch);
  _patchCache = { gen: mainDb.generation(), list };
  return list;
}

const getCurrentPatch = () => patches().at(-1) ?? null;
const getPreviousPatch = () => patches().at(-2) ?? null;

/** Последние ≤3 патча (от свежего к старому) + веса. */
function getPatchWindow() {
  const list = patches().slice(-PATCH_WEIGHTS.length).reverse();
  return { patches: list, weights: PATCH_WEIGHTS.slice(0, list.length) };
}

/** Всего матчей на патче (в каждом матче 10 участников base_wr). */
function getTotalMatches(patch) {
  const r = q((db) =>
    db.prepare('SELECT SUM(games) g FROM base_wr WHERE patch = ?').get(patch)
  );
  return Math.round((r?.g ?? 0) / 10);
}

/** Патч для «утверждений о текущем патче» (radar/verifier): первые дни патча
 *  данных мало — откатываемся на предыдущий. */
function getEffectivePatch(minMatches = 20000) {
  const cur = getCurrentPatch();
  if (!cur) return null;
  if (getTotalMatches(cur) >= minMatches) return cur;
  return getPreviousPatch() ?? cur;
}

// ── Запросы по одному патчу (Meta Radar, Lab Verifier) ──────────────────

/** Все чемпион-роли патча: games, wins, winrate, pickrate. */
function getChampionStats(patch, { role = null, minGames = 0 } = {}) {
  const total = getTotalMatches(patch) || 1;
  const rows = q((db) =>
    db
      .prepare(
        `SELECT champion_id championId, role, SUM(games) g, SUM(wins) w
         FROM base_wr WHERE patch = ? ${role ? 'AND role = ?' : ''}
         GROUP BY champion_id, role HAVING SUM(games) >= ?`
      )
      .all(...(role ? [patch, role, minGames] : [patch, minGames]))
  );
  return rows.map((r) => ({
    ...r,
    winrate: (100 * r.w) / r.g,
    pickrate: (100 * r.g) / total
  }));
}

function getChampionStat(patch, championId, role) {
  const r = q((db) =>
    db
      .prepare(
        `SELECT SUM(games) g, SUM(wins) w FROM base_wr
         WHERE patch = ? AND champion_id = ? AND role = ?`
      )
      .get(patch, championId, role)
  );
  if (!r || !r.g) return null;
  const total = getTotalMatches(patch) || 1;
  return { championId, role, g: r.g, w: r.w, winrate: (100 * r.w) / r.g, pickrate: (100 * r.g) / total };
}

/** Роль с максимумом игр у чемпиона на патче (для Verifier без указанной роли). */
function getMainRole(patch, championId) {
  const r = q((db) =>
    db
      .prepare(
        `SELECT role, SUM(games) g FROM base_wr
         WHERE patch = ? AND champion_id = ? GROUP BY role ORDER BY g DESC LIMIT 1`
      )
      .get(patch, championId)
  );
  return r?.role ?? null;
}

function getMatchup(patch, aId, bId, role) {
  const r = q((db) =>
    db
      .prepare(
        `SELECT SUM(games) g, SUM(wins) w FROM matchup
         WHERE patch = ? AND champion_id = ? AND vs_champion_id = ? AND role = ?`
      )
      .get(patch, aId, bId, role)
  );
  if (!r || !r.g) return null;
  return { g: r.g, w: r.w, winrate: (100 * r.w) / r.g };
}

/** Все пары матчапов патча с минимумом игр (для counter_surprise). */
function getMatchupPairs(patch, minGames) {
  return q((db) =>
    db
      .prepare(
        `SELECT champion_id championId, role, vs_champion_id vsId, SUM(games) g, SUM(wins) w
         FROM matchup WHERE patch = ?
         GROUP BY champion_id, role, vs_champion_id HAVING SUM(games) >= ?`
      )
      .all(patch, minGames)
  );
}

// ── Взвешенное окно 3 патчей (скоринг дуэлей, Pick Coach) ────────────────

// Хелпер: собрать CASE-выражение и параметры окна.
function winSql(col = 'patch') {
  const { patches: ps, weights } = getPatchWindow();
  const casePairs = ps.map(() => 'WHEN ? THEN ?').join(' ');
  const caseSql = `CASE ${col} ${casePairs} ELSE 0 END`;
  const caseParams = ps.flatMap((p, i) => [p, weights[i]]);
  const inSql = `${col} IN (${ps.map(() => '?').join(',')})`;
  return { caseSql, caseParams, inSql, inParams: ps };
}

// Общий агрегат: WHERE + GROUP BY по окну; возвращает g/w (взвешенные) и rawG.
function wAgg(table, { select = '', where, whereParams, groupBy = '', having = 0 }) {
  const { caseSql, caseParams, inSql, inParams } = winSql();
  const sql = `
    SELECT ${select ? select + ',' : ''}
      SUM(games) rawG,
      SUM(games * ${caseSql}) g,
      SUM(wins  * ${caseSql}) w
    FROM ${table}
    WHERE ${where} AND ${inSql}
    ${groupBy ? `GROUP BY ${groupBy} HAVING SUM(games) >= ${Number(having)}` : ''}`;
  const params = [...caseParams, ...caseParams, ...whereParams, ...inParams];
  return q((db) => (groupBy ? db.prepare(sql).all(...params) : db.prepare(sql).get(...params)));
}

/** Взвешенная база чемпиона на роли (или null). */
function wChampionStat(championId, role) {
  const r = wAgg('base_wr', {
    where: 'champion_id = ? AND role = ?',
    whereParams: [championId, role]
  });
  return r && r.g ? r : null;
}

/** Все чемпионы роли по окну: rawG (популярность), g/w (взвешенные). */
function wChampionStats(role, minRawGames = 0) {
  return wAgg('base_wr', {
    select: 'champion_id championId',
    where: 'role = ?',
    whereParams: [role],
    groupBy: 'champion_id',
    having: minRawGames
  });
}

/** Топ-n популярных на роли (по сырым games за окно). */
function wTopPopular(role, n) {
  return wChampionStats(role)
    .sort((a, b) => b.rawG - a.rawG)
    .slice(0, n);
}

/** Взвешенный матчап a vs b на роли (или null). */
function wMatchup(aId, bId, role) {
  const r = wAgg('matchup', {
    where: 'champion_id = ? AND vs_champion_id = ? AND role = ?',
    whereParams: [aId, bId, role]
  });
  return r && r.g ? r : null;
}

/** Все матчапы чемпиона на роли (для /pool). */
function wMatchupsFor(championId, role, minRawGames = 0) {
  return wAgg('matchup', {
    select: 'vs_champion_id vsId',
    where: 'champion_id = ? AND role = ?',
    whereParams: [championId, role],
    groupBy: 'vs_champion_id',
    having: minRawGames
  });
}

/** Кто играет против enemy на роли (для /counter): взвешенные g/w по парам. */
function wCountersAgainst(enemyId, role, minRawGames = 0) {
  return wAgg('matchup', {
    select: 'champion_id championId',
    where: 'vs_champion_id = ? AND role = ?',
    whereParams: [enemyId, role],
    groupBy: 'champion_id',
    having: minRawGames
  });
}

/** Взвешенная синергия пары (роли обеих сторон известны из драфта). */
function wSynergy(championId, role, allyId, allyRole) {
  const r = wAgg('synergy', {
    where: 'champion_id = ? AND role = ? AND ally_id = ? AND ally_role = ?',
    whereParams: [championId, role, allyId, allyRole]
  });
  return r && r.g ? r : null;
}

/** Сколько матчей обработано пайплайном (для /admin status). */
function countMatches() {
  return q((db) => db.prepare('SELECT COUNT(*) c FROM processed_matches').get().c);
}

module.exports = {
  DataUnavailableError,
  patches,
  getCurrentPatch,
  getPreviousPatch,
  getPatchWindow,
  getEffectivePatch,
  getTotalMatches,
  getChampionStats,
  getChampionStat,
  getMainRole,
  getMatchup,
  getMatchupPairs,
  wChampionStat,
  wChampionStats,
  wTopPopular,
  wMatchup,
  wMatchupsFor,
  wCountersAgainst,
  wSynergy,
  countMatches
};
