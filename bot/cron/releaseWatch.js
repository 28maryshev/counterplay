// Анонс новых версий приложения: раз в час проверяем GitHub-релизы
// (теги vX.Y.Z от Velopack) и постим новинку в #announcements.
// Первый запуск инициализируется молча (старьё не анонсируем).
const { COLORS, embed } = require('../lib/embeds');
const { kvGet, kvSet } = require('../db/botDb');
const logger = require('../lib/logger');

const KV_KEY = 'last_announced_app_release';
const RELEASES_URL = 'https://api.github.com/repos/28maryshev/counterplay/releases?per_page=15';

const cmpVer = (a, b) => {
  const pa = a.replace(/^v/, '').split('.').map(Number);
  const pb = b.replace(/^v/, '').split('.').map(Number);
  return pa[0] - pb[0] || pa[1] - pb[1] || pa[2] - pb[2];
};

/** Последний релиз приложения (тег vX.Y.Z; релиз данных `data` не считается). */
async function fetchLatest() {
  const res = await fetch(RELEASES_URL, {
    headers: { 'User-Agent': 'counterplay-bot', Accept: 'application/vnd.github+json' }
  });
  if (!res.ok) throw new Error(`GitHub releases -> ${res.status}`);
  const releases = await res.json();
  return (
    releases
      .filter((r) => !r.draft && !r.prerelease && /^v\d+\.\d+\.\d+$/.test(r.tag_name))
      .sort((a, b) => cmpVer(b.tag_name, a.tag_name))[0] ?? null
  );
}

async function run(ctx, { force = false } = {}) {
  const latest = await fetchLatest();
  if (!latest) {
    logger.warn('releaseWatch: no app releases found');
    return;
  }
  const last = kvGet(KV_KEY);

  // Первый прогон: запоминаем текущий релиз без поста — анонсируем только новые.
  if (!last && !force) {
    kvSet(KV_KEY, latest.tag_name);
    logger.info(`releaseWatch: initialized at ${latest.tag_name} (no announcement)`);
    return;
  }
  if (last === latest.tag_name && !force) return;

  const version = latest.tag_name;
  const notes = (latest.body || '')
    .replace(/\r/g, '')
    .trim()
    .slice(0, 1500);
  const e = embed(COLORS.gold)
    .setTitle(`📦 Counterplay ${version} is out!`)
    .setDescription(
      (notes ? `**What's new**\n${notes}\n\n` : '') +
        `The desktop app updates itself automatically on launch.\n` +
        `New here? [Download Counterplay](${ctx.config.siteUrl}/download)`
    )
    .setTimestamp(new Date(latest.published_at || Date.now()));

  const channel = await ctx.client.channels.fetch(ctx.config.channels.announcements);
  await channel.send({ embeds: [e] });
  kvSet(KV_KEY, version);
  logger.info(`releaseWatch: announced ${version}`);
}

module.exports = { run };
