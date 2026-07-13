// /pool role:<role> champions:<a, b, c> — анализ пула: хардкаунтеры,
// лучшие матчапы, предложения банов и pocket pick, закрывающий дыры пула.
const { SlashCommandBuilder } = require('discord.js');
const dl = require('../db/dataLayer');
const champs = require('../lib/champions');
const scoring = require('../lib/scoring');
const { COLORS, embed } = require('../lib/embeds');

const ROLE_CHOICES = [
  { name: 'Top', value: 'top' },
  { name: 'Jungle', value: 'jungle' },
  { name: 'Mid', value: 'mid' },
  { name: 'ADC', value: 'adc' },
  { name: 'Support', value: 'support' }
];
const roleLabel = { top: 'Top', jungle: 'Jungle', mid: 'Mid', adc: 'ADC', support: 'Support' };

/** Матчапы чемпиона с adjWR, отсортированные по adjWR. minGames с фолбэком. */
function matchupList(champId, role, minGames) {
  return dl
    .wMatchupsFor(champId, role, minGames)
    .map((m) => ({ vsId: m.vsId, adjWr: scoring.adjPct(m.g, m.w), rawG: m.rawG }))
    .sort((a, b) => a.adjWr - b.adjWr); // худшие (хардкаунтеры) первыми
}

module.exports = {
  data: new SlashCommandBuilder()
    .setName('pool')
    .setDescription('Analyze your champion pool: counters, ban suggestions, pocket pick')
    .addStringOption((o) =>
      o
        .setName('role')
        .setDescription('Your role')
        .setRequired(true)
        .addChoices(...ROLE_CHOICES)
    )
    .addStringOption((o) =>
      o
        .setName('champions')
        .setDescription('Up to 5 champions, comma-separated (e.g. Morgana, Lulu)')
        .setRequired(true)
    ),

  async execute(interaction) {
    const role = interaction.options.getString('role');
    const raw = interaction.options.getString('champions');
    const inputs = raw.split(',').map((s) => s.trim()).filter(Boolean).slice(0, 5);
    const pool = [];
    const unknown = [];
    for (const s of inputs) {
      const c = champs.resolve(s);
      if (c && !pool.some((p) => p.id === c.id)) pool.push(c);
      else if (!c) unknown.push(s);
    }
    if (!pool.length) {
      await interaction.reply({
        content: `Couldn't recognize any champion in "${raw}". Format: \`Morgana, Lulu, Nami\`.`,
        ephemeral: true
      });
      return;
    }

    let minGames = 80;
    let perChamp = pool.map((c) => ({ champ: c, ms: matchupList(c.id, role, minGames) }));
    if (perChamp.every((p) => !p.ms.length)) {
      minGames = 50;
      perChamp = pool.map((c) => ({ champ: c, ms: matchupList(c.id, role, minGames) }));
    }

    const fields = [];
    const allHardCounters = new Map(); // vsId -> [adjWr против каждого из пула]
    for (const { champ, ms } of perChamp) {
      const hard = ms.filter((m) => m.adjWr <= 46).slice(0, 3);
      const best = ms.filter((m) => m.adjWr >= 54).slice(-3).reverse();
      for (const m of ms) {
        if (!allHardCounters.has(m.vsId)) allHardCounters.set(m.vsId, []);
        allHardCounters.get(m.vsId).push(m.adjWr);
      }
      const lines = [];
      if (hard.length)
        lines.push(
          '⚔️ Struggles vs: ' +
            hard.map((m) => `**${champs.name(m.vsId)}** (${m.adjWr.toFixed(1)}%)`).join(', ')
        );
      if (best.length)
        lines.push(
          '💪 Stomps: ' +
            best.map((m) => `**${champs.name(m.vsId)}** (${m.adjWr.toFixed(1)}%)`).join(', ')
        );
      fields.push({
        name: champ.name,
        value: lines.join('\n') || '*Not enough matchup data yet.*'
      });
    }

    // Ban suggestions: оппоненты с avg adjWR < 47% против ≥ 2 чемпионов пула.
    const bans = [...allHardCounters.entries()]
      .map(([vsId, wrs]) => ({ vsId, n: wrs.length, avg: wrs.reduce((a, b) => a + b, 0) / wrs.length }))
      .filter((b) => b.n >= 2 && b.avg < 47)
      .sort((a, b) => a.avg - b.avg)
      .slice(0, 3);
    if (bans.length)
      fields.push({
        name: '🚫 Ban suggestions',
        value: bans
          .map((b) => `**${champs.name(b.vsId)}** — beats ${b.n} of your picks (avg ${b.avg.toFixed(1)}% for you)`)
          .join('\n')
      });

    // Pocket pick: из топ-20 роли — кто лучше всех бьёт найденные хардкаунтеры пула.
    const threats = bans.length
      ? bans.map((b) => b.vsId)
      : [...allHardCounters.entries()]
          .filter(([, wrs]) => Math.min(...wrs) <= 46)
          .sort((a, b) => Math.min(...a[1]) - Math.min(...b[1]))
          .slice(0, 3)
          .map(([vsId]) => vsId);
    if (threats.length) {
      const poolIds = new Set(pool.map((p) => p.id));
      let bestPocket = null;
      for (const cand of dl.wTopPopular(role, 20)) {
        if (poolIds.has(cand.championId)) continue;
        const wrs = threats
          .map((t) => dl.wMatchup(cand.championId, t, role))
          .filter((m) => m && m.rawG >= 30)
          .map((m) => scoring.adjPct(m.g, m.w));
        if (!wrs.length) continue;
        const avg = wrs.reduce((a, b) => a + b, 0) / wrs.length;
        if (!bestPocket || avg > bestPocket.avg) bestPocket = { id: cand.championId, avg, n: wrs.length };
      }
      if (bestPocket && bestPocket.avg >= 50)
        fields.push({
          name: '🃏 Pocket pick',
          value:
            `**${champs.name(bestPocket.id)}** — ${bestPocket.avg.toFixed(1)}% avg WR into the picks that counter your pool. ` +
            'Add it to close the gap.'
        });
    }

    const { patches } = dl.getPatchWindow();
    const e = embed(COLORS.blue)
      .setTitle(`🧰 Pool analysis — ${roleLabel[role]}`)
      .setDescription(
        pool.map((p) => p.name).join(', ') +
          (unknown.length ? `\n*(not recognized: ${unknown.join(', ')})*` : '') +
          (minGames === 50 ? '\n*(small samples — take with a grain of salt)*' : '')
      )
      .addFields(...fields.slice(0, 25))
      .setFooter({ text: `Patches ${patches.join(', ')} • counterplays.com` });
    await interaction.reply({ embeds: [e], ephemeral: true });
  }
};
