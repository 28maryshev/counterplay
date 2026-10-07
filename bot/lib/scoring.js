// Оценка кандидата для драфт-дуэлей — ядро оценки движка программы
// (RecommendationEngine.Recommend в src/Engine): те же слагаемые, веса и
// сглаживание. Чего здесь нет: личных факторов игрока (наигранность, личный
// винрейт, пулы, напарник — у бота их нет), драфт-фич на ручных тегах и
// предметах, приора по всем дивизионам. И одно отличие по данным: бот
// складывает все эло вместе, программа считает по эло игрока.
//
// Поменяли формулу в программе — поменяйте здесь и в lib/draft-engine.ts сайта.
const dl = require('../db/dataLayer');

const K = 50; // сглаживание базового винрейта
const K_PAIR = 20; // сглаживание парных таблиц (матчапы, синергия)
const BASE_CONF = 250; // доверие к базе по объёму
const MATCHUP_CONF = 50; // доверие к паре по объёму
const CONF_GAMES = 60; // доверие к синергии по объёму

const W_BASE = 1.0;
const W_OTHER = 0.6; // прочие враги по таблице своей роли (запасной путь)
const W_CROSS = 1.2; // прочие враги по кросс-ролевым матчапам (основной путь)
const W_SYNERGY = 1.2; // вес синергии, когда все пятеро врагов известны…
const W_SYN_MAX = 1.5; // …и когда не известен ни один
const W_BOTLANE = 1.5; // бот 2 на 2: против вражеского дуо-партнёра
const W_BOTLANE_BOTH = 1.8; // …когда виден весь вражеский бот
const BOTLANE_MIN_GAMES = 20000; // меньше кросс-данных — слагаемое бота молчит
// Вес прямой контры по роли: топ изолирован — контра решает больше всего; бот — 2 на 2.
const W_DIRECT = { top: 2.5, jungle: 2.2, mid: 2.0, support: 2.0, adc: 1.8 };

/** Сглаженная дельта от 50% в процентных пунктах. */
const delta = (g, w, k = K) => (g > 0 ? ((w + k / 2) / (g + k) - 0.5) * 100 : 0);

/** Сглаженный винрейт в процентах (для показа в текстах). */
const adjPct = (g, w, k = K) => ((w + k / 2) / (g + k)) * 100;

/**
 * Оценка кандидата.
 * @param candId champion_id кандидата
 * @param role роль кандидата ('top'|...)
 * @param enemies {role: championId} — известные враги по ролям (вместе с оппонентом по линии)
 * @param allies {role: championId} — известные союзники по ролям (без слота игрока)
 * @returns {score, parts, detail} или null, если у кандидата нет базовой строки
 */
function score(candId, role, enemies, allies) {
  const baseRow = dl.wChampionStat(candId, role);
  if (!baseRow) return null;

  const rawBase = delta(baseRow.g, baseRow.w); // сила без поправки на объём
  const base = rawBase * (baseRow.g / (baseRow.g + BASE_CONF));

  // ЧИСТАЯ контра: винрейт пары минус собственная сила кандидата, с поправкой на
  // объём пары — иначе сильные чемпионы патча выглядели бы «контрпиками всех».
  const pureVs = (g, w) => (g > 0 ? (delta(g, w, K_PAIR) - rawBase) * (g / (g + MATCHUP_CONF)) : 0);

  // Оппонент по линии.
  const directId = enemies[role] || 0;
  let direct = 0;
  let directDetail = null;
  if (directId) {
    const m = dl.wMatchup(candId, directId, role);
    if (m) {
      direct = pureVs(m.g, m.w);
      directDetail = { vsId: directId, adjWr: adjPct(m.g, m.w), games: Math.round(m.rawG) };
    }
  }

  // Бот — это 2 на 2: адк и саппорт контрят ещё и вражеского дуо-партнёра.
  const duoRole = role === 'adc' ? 'support' : role === 'support' ? 'adc' : null;
  const duoId = duoRole ? enemies[duoRole] || 0 : 0;
  const wBotlane = duoRole && duoId && directId ? W_BOTLANE_BOTH : W_BOTLANE;

  // Прочие враги (кроме оппонента и дуо-партнёра): сперва кросс-ролевой матчап
  // «моя роль против его роли», нет такой пары — таблица своей роли. Каждый путь
  // копит игры и победы отдельно и сглаживается один раз.
  let xg = 0, xw = 0, og = 0, ow = 0;
  for (const [eRole, eId] of Object.entries(enemies)) {
    if (!eId || eId === directId || (duoRole && eId === duoId)) continue;
    const x = dl.wCross(candId, role, eId, eRole);
    if (x) {
      xg += x.g;
      xw += x.w;
    } else {
      const m = dl.wMatchup(candId, eId, role);
      if (m) {
        og += m.g;
        ow += m.w;
      }
    }
  }
  const cross = pureVs(xg, xw);
  const others = pureVs(og, ow);

  // Синергия: пары со всеми союзниками складываются и сглаживаются вместе.
  let sg = 0, sw = 0;
  let bestSyn = null;
  for (const [aRole, aId] of Object.entries(allies)) {
    if (!aId) continue;
    const s = dl.wSynergy(candId, role, aId, aRole);
    if (!s) continue;
    sg += s.g;
    sw += s.w;
    const d = pureVs(s.g, s.w); // только для объяснения: кто из союзников лучшая пара
    if (!bestSyn || d > bestSyn.d) {
      bestSyn = { d, allyId: aId, adjWr: adjPct(s.g, s.w), games: Math.round(s.rawG) };
    }
  }
  const syn = sg > 0 ? (delta(sg, sw, K_PAIR) - rawBase) * (sg / (sg + CONF_GAMES)) : 0;

  // Синергия весит больше, пока врагов мало: в начале драфта сочетание со своими —
  // единственное, что известно.
  const known = Object.values(enemies).filter(Boolean).length;
  const wSynergy = W_SYNERGY + (W_SYN_MAX - W_SYNERGY) * Math.max(0, 1 - known / 5);

  let botlane = 0;
  if (duoRole && duoId && dl.crossGames() >= BOTLANE_MIN_GAMES) {
    const b = dl.wCross(candId, role, duoId, duoRole);
    if (b) botlane = pureVs(b.g, b.w);
  }

  const total =
    W_BASE * base +
    (W_DIRECT[role] ?? 2.2) * direct +
    W_OTHER * others +
    wSynergy * syn +
    wBotlane * botlane +
    W_CROSS * cross;

  return {
    score: total,
    parts: { base, direct, others, cross, syn, botlane },
    detail: {
      baseAdjWr: adjPct(baseRow.g, baseRow.w),
      baseGames: Math.round(baseRow.rawG),
      direct: directDetail,
      bestSyn
    }
  };
}

module.exports = { score, delta, adjPct, K, W_DIRECT };
