// patchWatch — раз в час сверяет официальный патч (Data Dragon) и готовность
// нашей базы, и постит в #announcements ключевые переходы:
//   • вышел новый патч LoL → «собираем данные, тир-лист держим на старом»;
//   • следом — что именно Riot в этом патче изменил;
//   • по новому патчу набралось достаточно данных → «готово, пора обновлять сайт».
// Программа (data.db) обновляется сама после кругов сбора; сайт — вручную.
// Первый прогон инициализируется молча (не спамим стартовым состоянием).
const { COLORS, embed } = require('../lib/embeds');
const { kvGet, kvSet } = require('../db/botDb');
const freshness = require('../lib/freshness');
const patchDiff = require('../lib/patchDiff');
const logger = require('../lib/logger');
const {displayPatch: dp} = require('../lib/patch');

const KV_OFFICIAL = 'patchwatch_last_official';
const KV_READY = 'patchwatch_last_ready';
// Отметка о сводке изменений ведётся отдельно от отметки о самом патче: номер
// версии появляется на CDN раньше её файлов, и тогда сводку надо переспросить
// через час, а сообщение о выходе патча повторять не надо.
const KV_DIFF = 'patchwatch_last_diff';

// Разом сводку не шлём: у Discord есть предел на длину сообщения, а на крупном
// патче правок бывает под сотню.
const DIFF_PAGES = 4;

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
    kvSet(KV_DIFF, s.official);
    if (primary) kvSet(KV_READY, primary);
    logger.info(`patchWatch: initialized (live ${s.official}, ready ${primary || '—'})`);
    return;
  }

  const msgs = [];
  if (s.official !== lastOfficial) {
    msgs.push(
      `🌐 **New LoL patch ${dp(s.official)}** detected. Collecting matches — the tier list and guides stay on **${dp(primary || lastReady || '—')}** until the new patch has enough data.`
    );
    kvSet(KV_OFFICIAL, s.official);
  }
  if (primary && primary !== lastReady && (!lastReady || freshness.cmpPatch(primary, lastReady) > 0)) {
    msgs.push(
      `📊 **Patch ${dp(primary)} data is ready.** The app database updates itself; run the site deploy to move guides & tier list to **${dp(primary)}**.`
    );
    kvSet(KV_READY, primary);
  }

  if (!msgs.length && force)
    msgs.push(
      `Live **${s.official}** · our DB **${s.db && s.db.newest}** (${s.db && s.db.ready ? 'ready' : 'filling'}) · site ${s.published.site} · program ${s.published.program} · runes ${s.published.runes}.`
    );

  const chId = ctx.config.channels.announcements;
  if (!chId) {
    if (msgs.length) logger.info(`patchWatch: ${msgs.join(' | ')}`);
    return;
  }

  if (msgs.length) {
    const channel = await ctx.client.channels.fetch(chId);
    await channel.send({
      embeds: [embed(COLORS.blue).setTitle('🩺 Patch watch').setDescription(msgs.join('\n\n'))]
    });
    logger.info(`patchWatch: posted (${msgs.length} note(s))`);
  }

  // Сводка идёт следом за сообщением о патче и со своей отметкой, поэтому
  // попытку видно даже в час, когда никаких переходов не случилось.
  await announceChanges(ctx, chId, s.official, lastOfficial, force);
}

/**
 * Что Riot изменил в этом патче — отдельным постом.
 *
 * Сводка считается по данным самой игры, а не по тексту патчноута: пересказ
 * ошибся бы в числе молча, и отличить его ошибку от наших измерений было бы
 * уже нельзя. Поэтому здесь только то, что Riot опубликовал сам.
 */
async function announceChanges(ctx, chId, official, lastOfficial, force) {
  const done = kvGet(KV_DIFF);
  if (done === official && !force) return;

  const from = done || lastOfficial;
  // Сравнивать не с чем — первый прогон с этой функцией. Запоминаем патч молча,
  // сводка уйдёт со следующим.
  if (!from || freshness.cmpPatch(from, official) >= 0) {
    kvSet(KV_DIFF, official);
    return;
  }

  let report;
  try {
    report = await patchDiff.diff({ from, to: official });
  } catch (e) {
    // Файлы нового патча ещё не выложены — отметку не ставим, попробуем на
    // следующем часу.
    logger.warn(`patchWatch: patch diff ${from} → ${official} failed — ${e.message}`);
    return;
  }

  // Сравниваемую пару называем всегда: бот мог простоять и пропустить патч, и
  // тогда сводка охватывает два шага сразу — это должно быть видно.
  const lines = [
    `Comparing **${dp(report.from)} → ${dp(report.to)}**, straight from the game data — ` +
      'prices, stats and ability numbers. Win rates come later, once enough matches are in.',
    '',
    ...patchDiff.render(report)
  ];
  const pages = patchDiff.paginate(lines);
  const shown = pages.slice(0, DIFF_PAGES);
  if (pages.length > shown.length)
    shown[shown.length - 1] += `\n\n…and ${pages.length - shown.length} more part(s) of the list.`;

  const channel = await ctx.client.channels.fetch(chId);
  for (let i = 0; i < shown.length; i++) {
    const e = embed(COLORS.blue).setDescription(shown[i]);
    if (i === 0) e.setTitle(`🧾 What changed in patch ${dp(report.to)}`);
    await channel.send({ embeds: [e] });
  }

  kvSet(KV_DIFF, official);
  logger.info(
    `patchWatch: changes ${report.from} → ${report.to} posted (${report.total} change(s), ${shown.length} message(s))`
  );
}

module.exports = { run };
