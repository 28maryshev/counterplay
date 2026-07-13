// Meta Radar: ежедневный автопост аномалии меты (10:00 UTC).
// Ротация типов sleeper → rising → trap → counter_surprise; в день смены патча
// (и следующий) — режим Seismograph. Все утверждения — на «эффективном» патче
// (первые дни нового патча данных мало — используется предыдущий).
const dl = require('../db/dataLayer');
const champs = require('../lib/champions');
const scoring = require('../lib/scoring');
const { COLORS, embed } = require('../lib/embeds');
const { db, kvGet, kvSet } = require('../db/botDb');
const logger = require('../lib/logger');

const TYPES = ['sleeper', 'rising', 'trap', 'counter_surprise'];

const adj = (r) => scoring.adjPct(r.g, r.w);
const roleLabel = { top: 'Top', jungle: 'Jungle', mid: 'Mid', adc: 'ADC', support: 'Support' };
const fmtGames = (g) => (g >= 1000 ? `${(g / 1000).toFixed(1)}k` : String(g));

// ── Кандидаты по типам ───────────────────────────────────────────────────

function candidatesSleeper(patch, excluded) {
  return dl
    .getChampionStats(patch, { minGames: 400 })
    .filter((r) => !excluded.has(r.championId) && adj(r) >= 52 && r.pickrate < 3)
    .sort((a, b) => adj(b) - adj(a));
}

function candidatesRising(patch, prev, excluded) {
  if (!prev) return [];
  const prevMap = new Map(
    dl.getChampionStats(prev, { minGames: 400 }).map((r) => [`${r.championId}:${r.role}`, r])
  );
  return dl
    .getChampionStats(patch, { minGames: 400 })
    .map((r) => {
      const p = prevMap.get(`${r.championId}:${r.role}`);
      return p ? { ...r, prevAdj: adj(p), deltaPp: adj(r) - adj(p) } : null;
    })
    .filter((r) => r && !excluded.has(r.championId) && r.deltaPp >= 1.5)
    .sort((a, b) => b.deltaPp - a.deltaPp);
}

function candidatesTrap(patch, excluded) {
  return dl
    .getChampionStats(patch, { minGames: 1000 })
    .filter((r) => !excluded.has(r.championId) && r.pickrate >= 7 && adj(r) < 48.5)
    .sort((a, b) => adj(a) - adj(b)); // самый глубокий трап первым
}

function candidatesCounterSurprise(patch, excluded) {
  // Пикрейты всех чемпион-ролей патча
  const stats = new Map(
    dl.getChampionStats(patch).map((r) => [`${r.championId}:${r.role}`, r])
  );
  return dl
    .getMatchupPairs(patch, 100)
    .map((m) => {
      const me = stats.get(`${m.championId}:${m.role}`);
      const vs = stats.get(`${m.vsId}:${m.role}`);
      if (!me || !vs) return null;
      return { ...m, adjM: adj(m), myPick: me.pickrate, vsPick: vs.pickrate };
    })
    .filter(
      (m) =>
        m &&
        !excluded.has(m.championId) &&
        m.adjM >= 56 &&
        m.vsPick >= 5 &&
        m.myPick < 2
    )
    .sort((a, b) => b.adjM - a.adjM);
}

// ── Embed'ы ──────────────────────────────────────────────────────────────

function buildEmbed(type, c, patch) {
  const name = champs.name(c.championId);
  const role = roleLabel[c.role] ?? c.role;
  const e = embed(type === 'trap' ? COLORS.red : type === 'counter_surprise' ? COLORS.blue : COLORS.green)
    .setTitle(`📡 META RADAR — Patch ${patch}`);
  const icon = champs.iconUrl(c.championId);
  if (icon) e.setThumbnail(icon);

  if (type === 'sleeper') {
    e.setDescription(
      `🔥 **SLEEPER OP: ${name} ${role}**\n` +
        `Only **${c.pickrate.toFixed(1)}%** pick rate — but **${adj(c).toFixed(1)}% WR** over ${fmtGames(c.g)} games.\n` +
        `Nobody plays it. It just wins.`
    );
  } else if (type === 'rising') {
    e.setDescription(
      `📈 **RISING: ${name} ${role}**\n` +
        `${c.prevAdj.toFixed(1)}% → **${adj(c).toFixed(1)}% WR** this patch (**+${c.deltaPp.toFixed(1)} pp**).\n` +
        `${fmtGames(c.g)} games and climbing — get on it before it's banned.`
    );
  } else if (type === 'trap') {
    e.setDescription(
      `⚠️ **TRAP PICK: ${name} ${role}**\n` +
        `Picked in **${c.pickrate.toFixed(1)}%** of games — winning only **${adj(c).toFixed(1)}%** (${fmtGames(c.g)} games).\n` +
        `Popular ≠ good. Think twice before locking it.`
    );
  } else {
    const vsName = champs.name(c.vsId);
    e.setDescription(
      `🧊 **HIDDEN COUNTER: ${name} into ${vsName} (${role})**\n` +
        `**${c.adjM.toFixed(1)}% WR** in the matchup (${fmtGames(c.g)} games) vs a ${c.vsPick.toFixed(1)}%-pickrate ${vsName}.\n` +
        `At ${c.myPick.toFixed(1)}% pick rate, almost nobody knows this answer exists.`
    );
  }
  return e;
}

function seismographEmbed(cur, old) {
  const curMap = new Map(
    dl.getChampionStats(cur, { minGames: 200 }).map((r) => [`${r.championId}:${r.role}`, r])
  );
  const movers = dl
    .getChampionStats(old, { minGames: 200 })
    .map((p) => {
      const n = curMap.get(`${p.championId}:${p.role}`);
      return n ? { ...n, from: adj(p), to: adj(n), d: adj(n) - adj(p) } : null;
    })
    .filter(Boolean)
    .sort((a, b) => Math.abs(b.d) - Math.abs(a.d))
    .slice(0, 5);
  if (!movers.length) return null;
  const lines = movers.map(
    (m) =>
      `**${champs.name(m.championId)}** (${roleLabel[m.role] ?? m.role}): ` +
      `${m.from.toFixed(1)}% → **${m.to.toFixed(1)}%** (${m.d >= 0 ? '+' : ''}${m.d.toFixed(1)})`
  );
  return embed(COLORS.gold)
    .setTitle(`🌋 SEISMOGRAPH — Patch ${cur} vs ${old}`)
    .setDescription(`Biggest win-rate moves of the new patch:\n\n${lines.join('\n')}`);
}

// ── Основной прогон ──────────────────────────────────────────────────────

async function run(ctx) {
  const channel = await ctx.client.channels.fetch(ctx.config.channels.metaRadar);
  const today = new Date().toISOString().slice(0, 10);

  // Seismograph: смена патча → 2 дня спецрежима.
  const cur = dl.getCurrentPatch();
  const lastSeen = kvGet('last_seen_patch');
  if (lastSeen && cur && lastSeen !== cur) {
    kvSet('seismo_old', lastSeen);
    kvSet('seismo_days', '2');
  }
  if (cur) kvSet('last_seen_patch', cur);

  const seismoDays = parseInt(kvGet('seismo_days') ?? '0', 10);
  if (seismoDays > 0) {
    const old = kvGet('seismo_old');
    const e = old ? seismographEmbed(cur, old) : null;
    kvSet('seismo_days', String(seismoDays - 1));
    if (e) {
      await channel.send({ embeds: [e] });
      logger.info(`metaRadar: seismograph posted (${cur} vs ${old})`);
      return;
    }
    logger.warn('metaRadar: seismograph had no movers, falling back to rotation');
  }

  const patch = dl.getEffectivePatch();
  const prev = dl.getPreviousPatch();
  if (!patch) {
    logger.warn('metaRadar: no patch data — skipping post');
    return;
  }

  // Исключения: чемпионы из радара за последние 14 дней.
  const since = new Date(Date.now() - 14 * 86400e3).toISOString().slice(0, 10);
  const excluded = new Set(
    db.prepare('SELECT champion_id c FROM radar_log WHERE date >= ?').all(since).map((r) => r.c)
  );

  const lastType = db
    .prepare('SELECT type FROM radar_log ORDER BY rowid DESC LIMIT 1')
    .get()?.type;
  const startIdx = (TYPES.indexOf(lastType) + 1) % TYPES.length;

  for (let i = 0; i < TYPES.length; i++) {
    const type = TYPES[(startIdx + i) % TYPES.length];
    let cands = [];
    if (type === 'sleeper') cands = candidatesSleeper(patch, excluded);
    else if (type === 'rising') cands = candidatesRising(patch, prev, excluded);
    else if (type === 'trap') cands = candidatesTrap(patch, excluded);
    else cands = candidatesCounterSurprise(patch, excluded);
    if (!cands.length) continue;

    const top = cands[0];
    await channel.send({ embeds: [buildEmbed(type, top, patch)] });
    db.prepare('INSERT INTO radar_log (date, type, champion_id, role) VALUES (?, ?, ?, ?)').run(
      today,
      type,
      top.championId,
      top.role
    );
    logger.info(`metaRadar: posted ${type} — ${champs.name(top.championId)} ${top.role}`);
    return;
  }
  logger.warn('metaRadar: no candidates for any anomaly type — skipping post');
}

module.exports = { run };
