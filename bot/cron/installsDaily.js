// Итоги дня по установкам приложения: раз в сутки берём агрегаты с сайта
// (/api/telemetry/summary) и постим сводку в приватный канал владельца.
//
// Почему сводка, а не сообщение на каждую установку: когда установок станет
// много, канал превратится в ленту спама, а тренд по ней всё равно не увидеть.
// Здесь же сразу видно движение — сколько новых, как это к вчерашнему дню,
// сколько людей реально запускали программу.
//
// Наружу приложение шлёт только обезличенный id устройства; сюда приходят
// исключительно агрегаты — ни одного идентификатора в канал не попадает.
const { COLORS, embed } = require('../lib/embeds');
const ga = require('../lib/ga');
const logger = require('../lib/logger');

// Флаги стран эмодзи: 'TR' → 🇹🇷. Неизвестная страна (нет CF-заголовка) — глобус.
function flag(code) {
  if (!code || code.length !== 2 || !/^[A-Z]{2}$/.test(code)) return '🌐';
  return String.fromCodePoint(...[...code].map((c) => 0x1f1e6 + c.charCodeAt(0) - 65));
}

// Разница с прошлыми сутками: «+3» / «−1» / «ровно столько же».
function delta(now, prev) {
  const d = now - prev;
  if (d === 0) return 'столько же, сколько вчера';
  return `${d > 0 ? '+' : '−'}${Math.abs(d)} к прошлым суткам`;
}

async function fetchSummary(config) {
  const url = `${config.siteUrl}/api/telemetry/summary`;
  const headers = { 'User-Agent': 'counterplay-bot' };
  if (config.telemetrySecret) headers['x-telemetry-secret'] = config.telemetrySecret;

  const res = await fetch(url, { headers });
  if (!res.ok) throw new Error(`telemetry summary -> ${res.status}`);
  return res.json();
}

async function run(ctx) {
  const { client, config } = ctx;
  const s = await fetchSummary(config);

  const countries =
    s.countries?.length > 0
      ? s.countries.map((c) => `${flag(c.country)} ${c.country} — ${c.n}`).join('\n')
      : '—';
  const versions =
    s.versions?.length > 0 ? s.versions.map((v) => `\`${v.version}\` — ${v.n}`).join('\n') : '—';

  // Источники визитов на сайт за сутки. GA знает только агрегаты, поэтому здесь
  // они и уместны: видно, какой канал вообще приводит людей и сколько из них
  // дожали до кнопки «Скачать».
  let sources = null;
  try {
    sources = await ga.sourcesLastDay(config);
  } catch (err) {
    logger.warn(`installsDaily: GA unavailable (${err.message})`);
  }

  const e = embed(COLORS.green)
    .setTitle('📥 Установки за сутки')
    .setDescription(
      `**${s.newInstalls}** новых установок · ${delta(s.newInstalls, s.newInstallsPrev)}`
    )
    .addFields(
      { name: 'Всего установок', value: `${s.totalInstalls}`, inline: true },
      { name: 'Активны за сутки (DAU)', value: `${s.activeToday}`, inline: true },
      { name: 'Активны за неделю (WAU)', value: `${s.active7d}`, inline: true },
      { name: 'Откуда новые', value: countries, inline: true },
      { name: 'Версии в строю', value: versions, inline: true }
    );

  if (sources?.length) {
    e.addFields({
      name: 'Источники визитов (GA)',
      value: sources
        .map((s2) => `${s2.source} — ${s2.sessions}${s2.downloads ? ` (скачали ${s2.downloads})` : ''}`)
        .join('\n'),
      inline: false
    });
  }

  const channel = await client.channels.fetch(config.channels.installs);
  await channel.send({ embeds: [e] });
  logger.info(`installsDaily: posted (${s.newInstalls} new, ${s.totalInstalls} total)`);
}

module.exports = { run, flag, delta };
