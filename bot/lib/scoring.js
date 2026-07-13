// Скоринг кандидата для Draft Duels — упрощённая копия движка приложения:
// чистые контр-дельты относительно собственной базы + темперирование по объёму.
// Данные — взвешенное окно 3 патчей (dataLayer.w*).
const dl = require('../db/dataLayer');

const K = 50; // сглаживание Лапласа (как в приложении)
const CONF_BASE = 250; // темпер базового WR
const CONF_MATCH = 50; // темпер парных матчапов
const CONF_SYN = 60; // темпер синергии

const W_BASE = 1.0;
const W_OTHER = 0.8;
const W_SYNERGY = 1.2;
const W_DIRECT = { top: 2.5, jungle: 2.2, mid: 2.0, support: 2.0, adc: 1.8 };

/** Сглаженная дельта от 50% в процентных пунктах. */
const delta = (g, w, k = K) => (g > 0 ? ((w + k / 2) / (g + k) - 0.5) * 100 : 0);

/** Сглаженный WR в процентах (для показа в текстах). */
const adjPct = (g, w, k = K) => ((w + k / 2) / (g + k)) * 100;

/**
 * Оценка кандидата.
 * @param candId champion_id кандидата
 * @param role роль кандидата ('top'|...)
 * @param enemies {role: championId} — известные враги по ролям
 * @param allies {role: championId} — известные союзники по ролям (без слота игрока)
 * @returns {score, parts, detail} или null, если у кандидата нет базовой строки
 */
function score(candId, role, enemies, allies) {
  const baseRow = dl.wChampionStat(candId, role);
  if (!baseRow) return null;

  const rawBase = delta(baseRow.g, baseRow.w);
  const base = rawBase * (baseRow.g / (baseRow.g + CONF_BASE));

  // Чистая контра: матчап минус собственная база, с темпером по объёму пары.
  const counterDelta = (m) =>
    m ? (delta(m.g, m.w) - rawBase) * (m.g / (m.g + CONF_MATCH)) : 0;

  let direct = 0;
  const directEnemy = enemies[role];
  let directDetail = null;
  if (directEnemy) {
    const m = dl.wMatchup(candId, directEnemy, role);
    direct = counterDelta(m);
    if (m) directDetail = { vsId: directEnemy, adjWr: adjPct(m.g, m.w), games: Math.round(m.rawG) };
  }

  const otherDeltas = [];
  for (const [eRole, eId] of Object.entries(enemies)) {
    if (eRole === role) continue;
    otherDeltas.push(counterDelta(dl.wMatchup(candId, eId, role)));
  }
  const others = otherDeltas.length
    ? otherDeltas.reduce((a, b) => a + b, 0) / otherDeltas.length
    : 0;

  const synDeltas = [];
  let bestSyn = null;
  for (const [aRole, aId] of Object.entries(allies)) {
    if (aRole === role) continue;
    const s = dl.wSynergy(candId, role, aId, aRole);
    const d = s ? (delta(s.g, s.w) - rawBase) * (s.g / (s.g + CONF_SYN)) : 0;
    synDeltas.push(d);
    if (s && (!bestSyn || d > bestSyn.d)) {
      bestSyn = { d, allyId: aId, adjWr: adjPct(s.g, s.w), games: Math.round(s.rawG) };
    }
  }
  const syn = synDeltas.length ? synDeltas.reduce((a, b) => a + b, 0) / synDeltas.length : 0;

  const total =
    W_BASE * base + (W_DIRECT[role] ?? 2.2) * direct + W_OTHER * others + W_SYNERGY * syn;

  return {
    score: total,
    parts: { base, direct, others, syn },
    detail: {
      baseAdjWr: adjPct(baseRow.g, baseRow.w),
      baseGames: Math.round(baseRow.rawG),
      direct: directDetail,
      bestSyn
    }
  };
}

module.exports = { score, delta, adjPct, K, W_DIRECT };
