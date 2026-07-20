// /patch — свежесть данных: официальный патч LoL против того, что у нас в базе
// и что опубликовано на сайте / в программе / в рунах. Один взгляд — и видно,
// где данные отстали и пора обновлять.
const { SlashCommandBuilder } = require('discord.js');
const { COLORS, embed } = require('../lib/embeds');
const freshness = require('../lib/freshness');

// ✅ совпадает с основным готовым патчем, ⚠️ отстал, ❔ неизвестно.
function mark(pub, primary) {
  if (!pub) return '❔';
  return pub === primary ? '✅' : '⚠️';
}

module.exports = {
  data: new SlashCommandBuilder()
    .setName('patch')
    .setDescription('Data freshness vs the live LoL patch'),

  async execute(interaction) {
    await interaction.deferReply();
    const s = await freshness.status();
    const db = s.db;
    const primary = (db && db.primary) || s.official || '?';

    const dbLine = !db || !db.patches || !db.patches.length
      ? 'no data'
      : db.ready
        ? `${db.newest} — ready ✅`
        : `${db.newest} — filling ⏳ ${(db.fraction * 100).toFixed(0)}% (holding on ${db.primary})`;

    const e = embed(COLORS.blue)
      .setTitle('🩺 Data freshness')
      .addFields(
        { name: 'Live patch (Data Dragon)', value: s.official ? `**${s.official}**` : 'n/a', inline: true },
        { name: 'Our database', value: dbLine, inline: true },
        { name: '​', value: '​', inline: true },
        { name: 'Site · tier list / guides', value: `${s.published.site || '—'} ${mark(s.published.site, primary)}`, inline: true },
        { name: 'Program · app database', value: `${s.published.program || '—'} ${mark(s.published.program, primary)}`, inline: true },
        { name: 'Runes / builds', value: `${s.published.runes || '—'} ${mark(s.published.runes, primary)}`, inline: true }
      );

    const stale = ['site', 'program', 'runes'].filter(
      (k) => s.published[k] && s.published[k] !== primary
    );
    e.setDescription(
      stale.length
        ? `Primary ready patch: **${primary}**. Behind: **${stale.join(', ')}** — needs an update.`
        : `Everything is on **${primary}**. ✅`
    );
    await interaction.editReply({ embeds: [e] });
  }
};
