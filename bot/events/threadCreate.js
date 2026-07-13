// Lab Verifier: новый пост в форуме #submit-finds → распознать чемпиона/матчап,
// вынести вердикт по базе, при CONFIRMED — кросспост в #hall-of-fame.
const dl = require('../db/dataLayer');
const champs = require('../lib/champions');
const scoring = require('../lib/scoring');
const { COLORS, embed } = require('../lib/embeds');
const { db } = require('../db/botDb');
const logger = require('../lib/logger');

const roleLabel = { top: 'Top', jungle: 'Jungle', mid: 'Mid', adc: 'ADC', support: 'Support' };
const ROLE_WORDS = {
  top: 'top', toplane: 'top', toplaner: 'top',
  jungle: 'jungle', jungler: 'jungle', jgl: 'jungle', jg: 'jungle', jung: 'jungle',
  mid: 'mid', middle: 'mid', midlane: 'mid', midlaner: 'mid',
  adc: 'adc', bot: 'adc', bottom: 'adc', botlane: 'adc', marksman: 'adc',
  support: 'support', supp: 'support', sup: 'support', utility: 'support'
};

const fmtGames = (g) => (g >= 1000 ? `${(g / 1000).toFixed(1)}k` : String(g));

function detectRole(text) {
  for (const tok of String(text).toLowerCase().split(/[^a-z]+/)) {
    if (ROLE_WORDS[tok]) return ROLE_WORDS[tok];
  }
  return null;
}

/** Вердикт для одиночного чемпиона (champion+role, эффективный патч). */
function verdictSingle(patch, champ, role) {
  const s = dl.getChampionStat(patch, champ.id, role);
  if (!s || s.g < 50) return { icon: '❓', label: 'NOT ENOUGH DATA', color: COLORS.blue, stat: s };
  const adj = scoring.adjPct(s.g, s.w);
  if (adj >= 51 && s.g >= 300) return { icon: '✅', label: 'CONFIRMED', color: COLORS.green, adj, stat: s };
  if (adj >= 51) return { icon: '⚠️', label: 'SMALL SAMPLE', color: COLORS.gold, adj, stat: s };
  return { icon: '❌', label: 'NOT CONFIRMED', color: COLORS.red, adj, stat: s };
}

/** Вердикт для матчапа a vs b. */
function verdictMatchup(patch, a, b, role) {
  const m = dl.getMatchup(patch, a.id, b.id, role);
  if (!m || m.g < 50) return { icon: '❓', label: 'NOT ENOUGH DATA', color: COLORS.blue, m };
  const adj = scoring.adjPct(m.g, m.w);
  if (adj >= 51 && m.g >= 100) return { icon: '✅', label: 'CONFIRMED', color: COLORS.green, adj, m };
  if (adj >= 51) return { icon: '⚠️', label: 'SMALL SAMPLE', color: COLORS.gold, adj, m };
  return { icon: '❌', label: 'NOT CONFIRMED', color: COLORS.red, adj, m };
}

async function crosspostHof(ctx, thread, author, patch, champ, role, line) {
  const already = db
    .prepare('SELECT 1 FROM hof_log WHERE patch = ? AND champion_id = ? AND role = ?')
    .get(patch, champ.id, role);
  if (already || !ctx.config.channels.hallOfFame) return false;

  const e = embed(COLORS.gold)
    .setTitle('🏆 VERIFIED FIND')
    .setDescription(
      `**${champ.name}** (${roleLabel[role] ?? role}) — discovered by ${author}\n${line}\n\n` +
        `[Original post](${thread.url})`
    );
  const icon = champs.iconUrl(champ.id);
  if (icon) e.setThumbnail(icon);

  const hof = await ctx.client.channels.fetch(ctx.config.channels.hallOfFame);
  await hof.send({ embeds: [e], allowedMentions: { parse: [] } });
  db.prepare('INSERT OR IGNORE INTO hof_log (patch, champion_id, role, thread_id) VALUES (?, ?, ?, ?)').run(
    patch,
    champ.id,
    role,
    thread.id
  );
  return true;
}

async function execute(ctx, thread) {
  try {
    if (!ctx.config.channels.submitFinds || thread.parentId !== ctx.config.channels.submitFinds)
      return;

    let starter = null;
    try {
      starter = await thread.fetchStarterMessage();
    } catch {
      /* стартовое сообщение могло ещё не доехать */
    }
    const text = `${thread.name} ${starter?.content ?? ''}`;
    const author = starter?.author ? `<@${starter.author.id}>` : 'the author';

    let patch;
    try {
      patch = dl.getEffectivePatch();
    } catch {
      await thread.send('Data temporarily unavailable — try again later.');
      return;
    }
    if (!patch) return;

    const found = champs.findInText(text);
    if (!found.length) {
      await thread.send(
        `Couldn't identify the champion — try "Champion + role", e.g. "Seraphine ADC".`
      );
      return;
    }

    const isMatchup = found.length >= 2 && /\b(vs\.?|versus|into|against)\b/i.test(text);
    const role =
      detectRole(text) ?? dl.getMainRole(patch, found[0].id) ?? 'mid';

    if (isMatchup) {
      const [a, b] = found;
      const v = verdictMatchup(patch, a, b, role);
      if (!v.m) {
        await thread.send(
          `${v.icon} **${v.label}** — no recorded games of ${a.name} vs ${b.name} (${roleLabel[role]}) on patch ${patch}.`
        );
        return;
      }
      const line = `${a.name} vs ${b.name} (${roleLabel[role]}) — **${v.adj?.toFixed(1) ?? '?'}% WR** over ${fmtGames(v.m.g)} games, patch ${patch}`;
      await thread.send(
        `${v.icon} **${v.label}**\n${line}\n${
          v.label === 'CONFIRMED'
            ? 'The database agrees — this matchup is real.'
            : v.label === 'NOT CONFIRMED'
              ? 'The numbers do not back this one up.'
              : 'Not enough games to be sure yet.'
        }`
      );
      return;
    }

    const champ = found[0];
    const v = verdictSingle(patch, champ, role);
    if (!v.stat) {
      await thread.send(
        `${v.icon} **${v.label}** — ${champ.name} ${roleLabel[role]} has almost no games on patch ${patch}.`
      );
      return;
    }
    const line = `${champ.name} ${roleLabel[role]} — **${(v.adj ?? scoring.adjPct(v.stat.g, v.stat.w)).toFixed(1)}% WR** over ${fmtGames(v.stat.g)} games, patch ${patch}`;
    await thread.send(
      `${v.icon} **${v.label}**\n${line}\n${
        v.label === 'CONFIRMED'
          ? 'The database confirms it — nice find!'
          : v.label === 'SMALL SAMPLE'
            ? 'Looks promising, but the sample is still small.'
            : v.label === 'NOT CONFIRMED'
              ? 'The numbers do not back this one up.'
              : 'Not enough games to judge yet.'
      }`
    );

    if (v.label === 'CONFIRMED') {
      const posted = await crosspostHof(ctx, thread, author, patch, champ, role, line);
      if (posted) await thread.send('Verified! Posted to #hall-of-fame 🏆');
    }
  } catch (e) {
    logger.error('threadCreate (Lab Verifier):', e);
  }
}

module.exports = { name: 'threadCreate', execute };
