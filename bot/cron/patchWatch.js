// patchWatch — раз в час сверяет официальный патч (Data Dragon) и готовность
// нашей базы, и постит в #announcements ключевые переходы:
//   • вышел новый патч LoL → «собираем данные, тир-лист держим на старом»;
//   • по новому патчу набралось достаточно данных → «готово, пора обновлять сайт».
// Программа (data.db) обновляется сама после кругов сбора; сайт — вручную.
// Первый прогон инициализируется молча (не спамим стартовым состоянием).
const { COLORS, embed } = require('../lib/embeds');
const { kvGet, kvSet } = require('../db/botDb');
const freshness = require('../lib/freshness');
const logger = require('../lib/logger');

const KV_OFFICIAL = 'patchwatch_last_official';
const KV_READY = 'patchwatch_last_ready';

async function run(ctx, { force = false } = {}) {
  const s = await freshness.status();
  if (!s.official) {
    logger.warn('patchWatch: no official patch (Data Dragon unreachable)');
    return;
  }
  const primary = s.db && s.db.primary;
  const lastOfficial = kvGet(KV_OFFICIAL);
  const lastReady = kvGet(KV_READY);

  // Первый прогон: запомнить состояние без поста.
  if (!lastOfficial && !force) {
    kvSet(KV_OFFICIAL, s.official);
    if (primary) kvSet(KV_READY, primary);
    logger.info(`patchWatch: initialized (live ${s.official}, ready ${primary || '—'})`);
    return;
  }

  const msgs = [];
  if (s.official !== lastOfficial) {
    msgs.push(
      `🌐 **New LoL patch ${s.official}** detected. Collecting matches — the tier list and guides stay on **${primary || lastReady || '—'}** until the new patch has enough data.`
    );
    kvSet(KV_OFFICIAL, s.official);
  }
  if (primary && primary !== lastReady && (!lastReady || freshness.cmpPatch(primary, lastReady) > 0)) {
    msgs.push(
      `📊 **Patch ${primary} data is ready.** The app database updates itself; run the site deploy to move guides & tier list to **${primary}**.`
    );
    kvSet(KV_READY, primary);
  }

  if (!msgs.length) {
    if (force) {
      msgs.push(
        `Live **${s.official}** · our DB **${s.db && s.db.newest}** (${s.db && s.db.ready ? 'ready' : 'filling'}) · site ${s.published.site} · program ${s.published.program} · runes ${s.published.runes}.`
      );
    } else {
      return;
    }
  }

  const chId = ctx.config.channels.announcements;
  if (!chId) {
    logger.info(`patchWatch: ${msgs.join(' | ')}`);
    return;
  }
  const channel = await ctx.client.channels.fetch(chId);
  await channel.send({
    embeds: [embed(COLORS.blue).setTitle('🩺 Patch watch').setDescription(msgs.join('\n\n'))]
  });
  logger.info(`patchWatch: posted (${msgs.length} note(s))`);
}

module.exports = { run };
