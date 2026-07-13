// Офлайн-прогон на реальном снапшоте базы: dataLayer, радар, дуэль, вердикты.
// Запуск: node test/offline.js (нужен data/data.db; токен Discord не нужен).
process.env.DISCORD_TOKEN = process.env.DISCORD_TOKEN || 'offline';
process.env.CLIENT_ID = process.env.CLIENT_ID || 'offline';
process.env.GUILD_ID = process.env.GUILD_ID || 'offline';

const mainDb = require('../db/mainDb');
const dl = require('../db/dataLayer');
const champs = require('../lib/champions');
const scoring = require('../lib/scoring');

function section(t) {
  console.log('\n' + '─'.repeat(60) + '\n' + t + '\n' + '─'.repeat(60));
}

(async () => {
  if (!mainDb.open()) {
    console.error('data/data.db not found — seed it first');
    process.exit(1);
  }
  await champs.load();

  section('PATCHES');
  console.log('all:', dl.patches().join(', '));
  console.log('current:', dl.getCurrentPatch(), '| previous:', dl.getPreviousPatch());
  console.log('window:', JSON.stringify(dl.getPatchWindow()));
  console.log('effective:', dl.getEffectivePatch());
  console.log('total matches (current):', dl.getTotalMatches(dl.getCurrentPatch()));
  console.log('processed_matches:', dl.countMatches());

  section('META RADAR candidates (top-2 per type)');
  const radar = require('../cron/metaRadar');
  // Внутренние функции не экспортированы — проверяем через данные напрямую.
  const patch = dl.getEffectivePatch();
  const prev = dl.getPreviousPatch();
  const adj = (r) => scoring.adjPct(r.g, r.w);
  const sleeper = dl
    .getChampionStats(patch, { minGames: 400 })
    .filter((r) => adj(r) >= 52 && r.pickrate < 3)
    .sort((a, b) => adj(b) - adj(a))
    .slice(0, 2);
  console.log('sleeper:', sleeper.map((r) => `${champs.name(r.championId)} ${r.role} ${adj(r).toFixed(1)}% pr=${r.pickrate.toFixed(1)}% g=${r.g}`));
  const prevMap = new Map(dl.getChampionStats(prev, { minGames: 400 }).map((r) => [`${r.championId}:${r.role}`, r]));
  const rising = dl
    .getChampionStats(patch, { minGames: 400 })
    .map((r) => {
      const p = prevMap.get(`${r.championId}:${r.role}`);
      return p ? { ...r, d: adj(r) - adj(p) } : null;
    })
    .filter((r) => r && r.d >= 1.5)
    .sort((a, b) => b.d - a.d)
    .slice(0, 2);
  console.log('rising:', rising.map((r) => `${champs.name(r.championId)} ${r.role} +${r.d.toFixed(1)}pp g=${r.g}`));
  const trap = dl
    .getChampionStats(patch, { minGames: 1000 })
    .filter((r) => r.pickrate >= 7 && adj(r) < 48.5)
    .sort((a, b) => adj(a) - adj(b))
    .slice(0, 2);
  console.log('trap:', trap.map((r) => `${champs.name(r.championId)} ${r.role} ${adj(r).toFixed(1)}% pr=${r.pickrate.toFixed(1)}%`));
  const stats = new Map(dl.getChampionStats(patch).map((r) => [`${r.championId}:${r.role}`, r]));
  const cs = dl
    .getMatchupPairs(patch, 100)
    .map((m) => {
      const me = stats.get(`${m.championId}:${m.role}`);
      const vs = stats.get(`${m.vsId}:${m.role}`);
      if (!me || !vs) return null;
      return { ...m, adjM: adj(m), myPick: me.pickrate, vsPick: vs.pickrate };
    })
    .filter((m) => m && m.adjM >= 56 && m.vsPick >= 5 && m.myPick < 2)
    .sort((a, b) => b.adjM - a.adjM)
    .slice(0, 2);
  console.log('counter_surprise:', cs.map((m) => `${champs.name(m.championId)} into ${champs.name(m.vsId)} (${m.role}) ${m.adjM.toFixed(1)}% g=${m.g}`));

  section('DRAFT DUEL generation');
  const { generateDuel } = require('../cron/duelPost');
  const duel = generateDuel(new Date().toISOString().slice(0, 10));
  if (!duel) console.log('FAILED to generate');
  else {
    console.log('role:', duel.role);
    console.log('allies:', Object.entries(duel.allies).map(([r, id]) => `${r}:${champs.name(id)}`).join(' '));
    console.log('enemies:', Object.entries(duel.enemies).map(([r, id]) => `${r}:${champs.name(id)}`).join(' '));
    console.log('options:', duel.explanation.options.map((o) => `${o.letter}:${champs.name(o.id)} (${o.score.toFixed(1)})`).join('  '));
    console.log('correct:', champs.name(duel.correct));
    console.log('top detail:', JSON.stringify(duel.explanation.top.detail));
  }

  section('PICK COACH');
  const { topCounters } = require('../commands/counter');
  const yasuo = champs.resolve('Yasuo');
  console.log('/counter Yasuo mid:', topCounters(yasuo.id, 'mid', 80, 5).map((c) => `${champs.name(c.championId)} ${c.adjWr.toFixed(1)}% (g=${Math.round(c.rawG)})`));
  const morgana = champs.resolve('Morgana');
  const leona = champs.resolve('Leona');
  const m = dl.wMatchup(morgana.id, leona.id, 'support');
  console.log('/matchup Morgana vs Leona support:', m ? `${scoring.adjPct(m.g, m.w).toFixed(1)}% (rawG=${Math.round(m.rawG)})` : 'no data');

  section('LAB VERIFIER parsing');
  for (const text of [
    'Seraphine ADC is broken',
    'Darius top feels unkillable this patch',
    'morgana vs leona support',
    'ASol mid is secretly OP',
    'random text without champions'
  ]) {
    const found = champs.findInText(text);
    console.log(`"${text}" ->`, found.map((f) => f.name).join(', ') || '(none)');
  }

  section('SCORING sample');
  const jinx = champs.resolve('Jinx');
  const cait = champs.resolve('Caitlyn');
  const lulu = champs.resolve('Lulu');
  const s = scoring.score(jinx.id, 'adc', { adc: cait.id }, { support: lulu.id });
  console.log('Jinx adc vs Caitlyn, with Lulu:', s && { score: s.score.toFixed(2), parts: Object.fromEntries(Object.entries(s.parts).map(([k, v]) => [k, v.toFixed(2)])) });

  console.log('\nOK');
})();
