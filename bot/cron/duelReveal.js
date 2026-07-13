// Draft Duels: вскрытие ответа (22:00 UTC) — распределение голосов, очки недели.
const dl = require('../db/dataLayer');
const champs = require('../lib/champions');
const { COLORS, embed } = require('../lib/embeds');
const { db } = require('../db/botDb');
const logger = require('../lib/logger');

/** ISO-неделя даты: '2026-W29'. */
function isoWeek(dateStr) {
  const d = new Date(dateStr + 'T00:00:00Z');
  const day = (d.getUTCDay() + 6) % 7; // пн=0
  d.setUTCDate(d.getUTCDate() - day + 3); // четверг этой недели
  const jan4 = new Date(Date.UTC(d.getUTCFullYear(), 0, 4));
  const week = 1 + Math.round((d - jan4) / (7 * 86400e3));
  return `${d.getUTCFullYear()}-W${String(week).padStart(2, '0')}`;
}

async function run(ctx) {
  const duel = db
    .prepare('SELECT * FROM duels WHERE revealed = 0 ORDER BY id DESC LIMIT 1')
    .get();
  if (!duel) {
    logger.warn('duelReveal: no unrevealed duel — skipping');
    return;
  }
  db.prepare('UPDATE duels SET revealed = 1 WHERE id = ?').run(duel.id); // голоса закрыты

  const explanation = JSON.parse(duel.explanation_json);
  const options = explanation.options; // [{letter, id, score}]
  const correctOpt = options.find((o) => o.id === duel.correct);
  const runnerOpt = options.find((o) => o.id === explanation.runner.id) ?? null;

  const votes = db.prepare('SELECT user_id, choice FROM duel_votes WHERE duel_id = ?').all(duel.id);
  const counts = { A: 0, B: 0, C: 0, D: 0 };
  for (const v of votes) counts[v.choice] = (counts[v.choice] ?? 0) + 1;
  const total = votes.length;

  // Очки недели: correct +3, runner-up +1, участие total+1.
  const week = isoWeek(duel.date);
  const upsert = db.prepare(
    `INSERT INTO duel_scores (user_id, week, points, correct, total) VALUES (?, ?, ?, ?, 1)
     ON CONFLICT(user_id, week) DO UPDATE SET
       points = points + excluded.points,
       correct = correct + excluded.correct,
       total = total + 1`
  );
  const applyAll = db.transaction(() => {
    for (const v of votes) {
      const pts = v.choice === correctOpt?.letter ? 3 : v.choice === runnerOpt?.letter ? 1 : 0;
      const cor = v.choice === correctOpt?.letter ? 1 : 0;
      upsert.run(v.user_id, week, pts, cor);
    }
  });
  applyAll();

  // Текст «почему» из сохранённой разбивки.
  const det = explanation.top.detail ?? {};
  const why = [];
  if (det.direct)
    why.push(
      `• Direct counter: **${det.direct.adjWr.toFixed(1)}% WR** into ${champs.name(det.direct.vsId)} (${det.direct.games} games)`
    );
  if (det.baseAdjWr)
    why.push(`• Solid base: ${det.baseAdjWr.toFixed(1)}% WR on the role (${det.baseGames} games)`);
  if (det.bestSyn)
    why.push(
      `• Synergy with ${champs.name(det.bestSyn.allyId)}: ${det.bestSyn.adjWr.toFixed(1)}% WR together`
    );

  const split = ['A', 'B', 'C', 'D']
    .map((l) => `${l} ${total ? Math.round((100 * (counts[l] ?? 0)) / total) : 0}%`)
    .join(' · ');

  const e = embed(COLORS.gold)
    .setTitle('🧩 DUEL — ANSWER')
    .setDescription(
      `The engine says: **${correctOpt?.letter ?? '?'} — ${champs.name(duel.correct)}** ` +
        `(score +${explanation.top.score.toFixed(1)})\n\n` +
        `${why.join('\n') || '—'}\n\n` +
        `Runner-up: **${champs.name(explanation.runner.id)}** (score +${explanation.runner.score.toFixed(1)})\n\n` +
        `Vote split: ${split} — **${total}** vote${total === 1 ? '' : 's'}`
    );
  const icon = champs.iconUrl(duel.correct);
  if (icon) e.setThumbnail(icon);

  const channel = await ctx.client.channels.fetch(ctx.config.channels.draftDuels);
  const payload = { embeds: [e] };
  if (duel.message_id) payload.reply = { messageReference: duel.message_id, failIfNotExists: false };
  await channel.send(payload);
  logger.info(`duelReveal: duel #${duel.id} revealed (${total} votes)`);
}

module.exports = { run, isoWeek };
