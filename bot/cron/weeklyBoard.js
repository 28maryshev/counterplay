// Недельный лидерборд дуэлей (воскресенье 20:00 UTC).
const { COLORS, embed } = require('../lib/embeds');
const { db } = require('../db/botDb');
const { isoWeek } = require('./duelReveal');
const logger = require('../lib/logger');

async function run(ctx) {
  const week = isoWeek(new Date().toISOString().slice(0, 10));
  const rows = db
    .prepare(
      `SELECT user_id, points, correct, total FROM duel_scores
       WHERE week = ? ORDER BY points DESC, correct DESC LIMIT 10`
    )
    .all(week);
  if (!rows.length) {
    logger.warn(`weeklyBoard: no scores for ${week} — skipping`);
    return;
  }
  const lines = rows.map(
    (r, i) =>
      `**#${i + 1}** <@${r.user_id}> — **${r.points} pts** (${r.correct}/${r.total} correct)`
  );
  const e = embed(COLORS.gold)
    .setTitle(`🏆 DRAFT DUELS — Week ${week} Leaderboard`)
    .setDescription(`${lines.join('\n')}\n\nNew week starts now. Play daily to climb!`);

  const channel = await ctx.client.channels.fetch(ctx.config.channels.draftDuels);
  await channel.send({ embeds: [e], allowedMentions: { parse: [] } });
  logger.info(`weeklyBoard: posted for ${week} (${rows.length} players)`);
}

module.exports = { run };
