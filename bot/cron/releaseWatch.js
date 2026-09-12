// Анонс новых версий приложения: раз в час проверяем GitHub-релизы
// (теги vX.Y.Z от Velopack) и постим новинку в #announcements.
// Первый запуск инициализируется молча (старьё не анонсируем).
//
// Версии часто выходят пачкой: правка, за ней ещё одна, и к моменту проверки их
// три. Раньше пост доставался только последней, а остальные пропадали — хотя
// игрок их всё равно установил. Теперь заметки всех пропущенных складываются в
// один список: человек видит, что изменилось за всё время с прошлого анонса.
const { COLORS, embed } = require('../lib/embeds');
const { kvGet, kvSet } = require('../db/botDb');
const logger = require('../lib/logger');

const KV_KEY = 'last_announced_app_release';
const RELEASES_URL = 'https://api.github.com/repos/28maryshev/counterplay/releases?per_page=15';
const RELEASES_PAGE = 'https://github.com/28maryshev/counterplay/releases';

// Больше в описание эмбеда всё равно не влезет с запасом на хвост про загрузку.
const LIMIT = 3500;

const cmpVer = (a, b) => {
  const pa = a.replace(/^v/, '').split('.').map(Number);
  const pb = b.replace(/^v/, '').split('.').map(Number);
  return pa[0] - pb[0] || pa[1] - pb[1] || pa[2] - pb[2];
};

/** Релизы приложения от старых к новым (тег vX.Y.Z; релиз данных `data` не в счёт). */
async function fetchReleases() {
  const res = await fetch(RELEASES_URL, {
    headers: { 'User-Agent': 'counterplay-bot', Accept: 'application/vnd.github+json' }
  });
  if (!res.ok) throw new Error(`GitHub releases -> ${res.status}`);
  const releases = await res.json();
  return releases
    .filter((r) => !r.draft && !r.prerelease && /^v\d+\.\d+\.\d+$/.test(r.tag_name))
    .sort((a, b) => cmpVer(a.tag_name, b.tag_name));
}

/** Непустые строки заметок одного релиза. */
function lines(release) {
  return (release.body || '')
    .replace(/\r/g, '')
    .split('\n')
    .map((s) => s.trimEnd())
    .filter((s) => s.trim().length > 0);
}

async function run(ctx, { force = false } = {}) {
  const releases = await fetchReleases();
  if (releases.length === 0) {
    logger.warn('releaseWatch: no app releases found');
    return;
  }
  const latest = releases[releases.length - 1];
  const last = kvGet(KV_KEY);

  // Первый прогон: запоминаем текущий релиз без поста — анонсируем только новые.
  if (!last && !force) {
    kvSet(KV_KEY, latest.tag_name);
    logger.info(`releaseWatch: initialized at ${latest.tag_name} (no announcement)`);
    return;
  }
  if (last === latest.tag_name && !force) return;

  // Всё, что вышло с прошлого анонса. Если метки нет (принудительный запуск) —
  // говорим только о последней версии.
  const missed = last ? releases.filter((r) => cmpVer(r.tag_name, last) > 0) : [];
  const posted = missed.length > 0 ? missed : [latest];

  const seen = new Set();
  const body = [];
  for (const r of posted) {
    for (const line of lines(r)) {
      // Одна и та же строка могла попасть в соседние релизы — повторять её в
      // общем списке незачем.
      const key = line.trim().toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      body.push(line);
    }
  }

  let notes = body.join('\n').trim();
  if (notes.length > LIMIT) {
    // Режем по границе слова, а не посреди него, и честно говорим, что это не всё.
    notes =
      notes.slice(0, LIMIT).replace(/\s+\S*$/, '') +
      `\n…and more in the [release notes](${RELEASES_PAGE}).`;
  }

  // Когда версий несколько, в заголовке списка честно говорим, за какие именно.
  const span = posted.length > 1 ? ` *(${posted[0].tag_name} → ${latest.tag_name})*` : '';

  const version = latest.tag_name;
  const e = embed(COLORS.gold)
    .setTitle(`📦 Counterplay ${version} is out!`)
    .setDescription(
      (notes ? `**What's new**${span}\n${notes}\n\n` : '') +
        `The desktop app updates itself automatically on launch.\n` +
        `New here? [Download Counterplay](${ctx.config.siteUrl}/download)`
    )
    .setTimestamp(new Date(latest.published_at || Date.now()));

  const channel = await ctx.client.channels.fetch(ctx.config.channels.announcements);
  await channel.send({ embeds: [e] });
  kvSet(KV_KEY, version);
  logger.info(
    `releaseWatch: announced ${version}` +
      (posted.length > 1
        ? ` (+${posted.length - 1} missed: ${posted.map((r) => r.tag_name).join(', ')})`
        : '')
  );
}

module.exports = { run };
