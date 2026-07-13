// /counter champion:<name> [role] — топ-5 контрпиков против чемпиона.
// Ранжирование по чистой контр-дельте (adjWR матчапа − adjWR базы контрпика),
// как в движке приложения, а не по сырому WR матчапа.
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
const fmtGames = (g) => (g >= 1000 ? `${(g / 1000).toFixed(1)}k` : String(Math.round(g)));

/** Топ контрпиков против enemy на роли: [{championId, adjWr, pureDelta, rawG}]. */
function topCounters(enemyId, role, minGames, limit) {
  const baseMap = new Map(dl.wChampionStats(role).map((r) => [r.championId, r]));
  return dl
    .wCountersAgainst(enemyId, role, minGames)
    .map((m) => {
      const b = baseMap.get(m.championId);
      if (!b) return null;
      const adjM = scoring.delta(m.g, m.w);
      const adjB = scoring.delta(b.g, b.w);
      return {
        championId: m.championId,
        adjWr: scoring.adjPct(m.g, m.w),
        pureDelta: (adjM - adjB) * (m.g / (m.g + 50)),
        rawG: m.rawG
      };
    })
    .filter(Boolean)
    .sort((a, b) => b.pureDelta - a.pureDelta)
    .slice(0, limit);
}

module.exports = {
  data: new SlashCommandBuilder()
    .setName('counter')
    .setDescription('Top counter picks against a champion (from the Counterplay database)')
    .addStringOption((o) =>
      o.setName('champion').setDescription('Enemy champion').setRequired(true).setAutocomplete(true)
    )
    .addStringOption((o) =>
      o.setName('role').setDescription('Lane (default: their main role)').addChoices(...ROLE_CHOICES)
    ),

  async execute(interaction) {
    const input = interaction.options.getString('champion');
    const champ = champs.resolve(input);
    if (!champ) {
      await interaction.reply({
        content: `Couldn't find champion **${input}** — try the autocomplete suggestions.`,
        ephemeral: true
      });
      return;
    }
    const patch = dl.getEffectivePatch();
    const role =
      interaction.options.getString('role') ?? dl.getMainRole(patch, champ.id) ?? 'mid';

    let counters = topCounters(champ.id, role, 80, 5);
    let small = false;
    if (!counters.length) {
      counters = topCounters(champ.id, role, 50, 5);
      small = true;
    }
    if (!counters.length) {
      await interaction.reply({
        content: `Not enough matchup data for **${champ.name}** (${roleLabel[role]}) yet.`,
        ephemeral: true
      });
      return;
    }

    const { patches } = dl.getPatchWindow();
    const lines = counters.map(
      (c, i) =>
        `**#${i + 1} ${champs.name(c.championId)}** — ${c.adjWr.toFixed(1)}% WR in the matchup ` +
        `(${fmtGames(c.rawG)} games)`
    );
    const e = embed(COLORS.blue)
      .setTitle(`🛡️ Counters vs ${champ.name} (${roleLabel[role]})`)
      .setDescription(lines.join('\n') + (small ? '\n\n*(small samples — take with a grain of salt)*' : ''))
      .addFields({ name: 'Patches', value: patches.join(', '), inline: true });
    const icon = champs.iconUrl(champ.id);
    if (icon) e.setThumbnail(icon);
    await interaction.reply({ embeds: [e], ephemeral: true });
  },

  topCounters
};
