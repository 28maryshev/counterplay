// /matchup a:<name> b:<name> [role] — WR пары лицом к лицу.
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

module.exports = {
  data: new SlashCommandBuilder()
    .setName('matchup')
    .setDescription('Head-to-head win rate of two champions on a lane')
    .addStringOption((o) =>
      o.setName('a').setDescription('First champion').setRequired(true).setAutocomplete(true)
    )
    .addStringOption((o) =>
      o.setName('b').setDescription('Second champion').setRequired(true).setAutocomplete(true)
    )
    .addStringOption((o) =>
      o.setName('role').setDescription('Lane (default: their common role)').addChoices(...ROLE_CHOICES)
    ),

  async execute(interaction) {
    const a = champs.resolve(interaction.options.getString('a'));
    const b = champs.resolve(interaction.options.getString('b'));
    if (!a || !b) {
      await interaction.reply({
        content: `Couldn't recognize ${!a ? `**${interaction.options.getString('a')}**` : `**${interaction.options.getString('b')}**`} — try the autocomplete suggestions.`,
        ephemeral: true
      });
      return;
    }
    const patch = dl.getEffectivePatch();
    const role =
      interaction.options.getString('role') ?? dl.getMainRole(patch, a.id) ?? 'mid';

    const m = dl.wMatchup(a.id, b.id, role);
    if (!m) {
      await interaction.reply({
        content: `No recorded games of **${a.name} vs ${b.name}** on ${roleLabel[role]}. Try another role?`,
        ephemeral: true
      });
      return;
    }
    const adjA = scoring.adjPct(m.g, m.w);
    const adjB = 100 - adjA;
    const fav = adjA >= 50 ? a : b;
    const und = fav === a ? b : a;
    const favWr = Math.max(adjA, adjB);

    const verdict =
      m.rawG < 50
        ? `Too few games to call it (only ${Math.round(m.rawG)}).`
        : favWr >= 53
          ? `**${fav.name} is favored** into ${und.name} (${favWr.toFixed(1)}% over ${fmtGames(m.rawG)} games).`
          : `Close to even — skill matters more than the pick here (${favWr.toFixed(1)}% over ${fmtGames(m.rawG)} games).`;

    const { patches } = dl.getPatchWindow();
    const e = embed(COLORS.blue)
      .setTitle(`⚔️ ${a.name} vs ${b.name} (${roleLabel[role]})`)
      .setDescription(
        `**${a.name}**: ${adjA.toFixed(1)}% WR\n**${b.name}**: ${adjB.toFixed(1)}% WR\n\n${verdict}`
      )
      .addFields({ name: 'Patches', value: patches.join(', '), inline: true });
    const icon = champs.iconUrl(fav.id);
    if (icon) e.setThumbnail(icon);
    await interaction.reply({ embeds: [e], ephemeral: true });
  }
};
