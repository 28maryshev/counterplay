// /admin — ручные триггеры и статус (доступ только ADMIN_USER_IDS).
const { SlashCommandBuilder } = require('discord.js');
const dl = require('../db/dataLayer');
const dataSync = require('../lib/dataSync');
const { db, kvGet } = require('../db/botDb');
const { COLORS, embed } = require('../lib/embeds');

const startedAt = Date.now();

module.exports = {
  data: new SlashCommandBuilder()
    .setName('admin')
    .setDescription('Counterplay bot admin controls')
    .addSubcommand((s) => s.setName('status').setDescription('Bot and data status'))
    .addSubcommand((s) => s.setName('radar-now').setDescription('Post Meta Radar immediately'))
    .addSubcommand((s) => s.setName('duel-now').setDescription('Post a Draft Duel immediately'))
    .addSubcommand((s) => s.setName('reveal-now').setDescription('Reveal the current duel immediately'))
    .addSubcommand((s) => s.setName('board-now').setDescription('Post the weekly leaderboard'))
    .addSubcommand((s) => s.setName('data-sync').setDescription('Check/download the latest database snapshot')),

  async execute(interaction, ctx) {
    if (!ctx.config.adminIds.includes(interaction.user.id)) {
      await interaction.reply({ content: 'No access.', ephemeral: true });
      return;
    }
    const sub = interaction.options.getSubcommand();

    if (sub === 'status') {
      await interaction.deferReply({ ephemeral: true });
      let dataInfo = 'snapshot NOT loaded';
      try {
        const manifest = dataSync.localManifest();
        const patches = dl.patches();
        dataInfo =
          `patch **${dl.getCurrentPatch()}** (window: ${dl.getPatchWindow().patches.join(', ')})\n` +
          `matches in db: **${dl.countMatches().toLocaleString('en-US')}**\n` +
          `snapshot: \`${manifest?.version ?? '?'}\` (published ${manifest?.updated?.slice(0, 16) ?? '?'})\n` +
          `patches seen: ${patches.join(', ')}`;
      } catch {
        /* нет данных */
      }
      const lastRadar = db.prepare('SELECT date, type FROM radar_log ORDER BY rowid DESC LIMIT 1').get();
      const activeDuel = db.prepare('SELECT id, date, revealed FROM duels ORDER BY id DESC LIMIT 1').get();
      const uptimeMin = Math.round((Date.now() - startedAt) / 60000);
      const e = embed(COLORS.blue).setTitle('🤖 Bot status').setDescription(
        `${dataInfo}\n\n` +
          `last radar: ${lastRadar ? `${lastRadar.date} (${lastRadar.type})` : '—'}\n` +
          `last duel: ${activeDuel ? `#${activeDuel.id} on ${activeDuel.date} (${activeDuel.revealed ? 'revealed' : 'open'})` : '—'}\n` +
          `last seen patch: ${kvGet('last_seen_patch') ?? '—'}\n` +
          `uptime: ${Math.floor(uptimeMin / 60)}h ${uptimeMin % 60}m · rss ${(process.memoryUsage().rss / 1e6).toFixed(0)} MB`
      );
      await interaction.editReply({ embeds: [e] });
      return;
    }

    if (sub === 'data-sync') {
      await interaction.deferReply({ ephemeral: true });
      const r = await dataSync.sync();
      await interaction.editReply(
        r === 'updated'
          ? `Snapshot updated — current patch: **${dl.getCurrentPatch()}**.`
          : r === 'current'
            ? 'Snapshot is already up to date.'
            : 'Sync failed (see logs) — still running on the cached snapshot.'
      );
      return;
    }

    // Триггеры постов: канал должен быть настроен.
    const jobs = {
      'radar-now': { mod: require('../cron/metaRadar'), needs: 'metaRadar' },
      'duel-now': { mod: require('../cron/duelPost'), needs: 'draftDuels' },
      'reveal-now': { mod: require('../cron/duelReveal'), needs: 'draftDuels' },
      'board-now': { mod: require('../cron/weeklyBoard'), needs: 'draftDuels' }
    };
    const job = jobs[sub];
    if (!job) return;
    if (!ctx.config.channels[job.needs]) {
      await interaction.reply({
        content: `Channel for this feature is not configured (.env).`,
        ephemeral: true
      });
      return;
    }
    await interaction.deferReply({ ephemeral: true });
    try {
      await job.mod.run(ctx);
      await interaction.editReply('Done — check the channel (a warning in logs if there was nothing to post).');
    } catch (e) {
      ctx.logger.error(`/admin ${sub}:`, e);
      await interaction.editReply(`Failed: ${e.message}`);
    }
  }
};
