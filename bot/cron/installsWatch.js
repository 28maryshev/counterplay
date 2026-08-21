// Живая лента установок: раз в минуту спрашиваем сайт, не появилось ли новых,
// и постим каждую отдельным сообщением. Метку последней увиденной храним в
// bot.db — после перезапуска бот не задублирует уже показанное.
//
// Когда установок станет много и лента начнёт мешать, эту джобу можно просто
// выключить (убрать CH_INSTALLS или строку cron) — ежедневная сводка
// (installsDaily) продолжит работать сама по себе.
const { kvGet, kvSet } = require('../db/botDb');
const logger = require('../lib/logger');
const { flag } = require('./installsDaily');

const KV_KEY = 'last_install_seen';
const MAX_POST = 10; // за один проход: всплеск не превратится в простыню сообщений

async function fetchNew(config, since) {
  const url = `${config.siteUrl}/api/telemetry/installs?since=${encodeURIComponent(since)}`;
  const headers = { 'User-Agent': 'counterplay-bot' };
  if (config.telemetrySecret) headers['x-telemetry-secret'] = config.telemetrySecret;

  const res = await fetch(url, { headers });
  if (!res.ok) throw new Error(`telemetry installs -> ${res.status}`);
  return res.json();
}

async function run(ctx) {
  const { client, config } = ctx;

  // Первый запуск: старое не показываем, начинаем отсчёт с этого момента.
  const since = kvGet(KV_KEY);
  if (!since) {
    kvSet(KV_KEY, new Date().toISOString());
    logger.info('installsWatch: initialised, watching from now on');
    return;
  }

  const data = await fetchNew(config, since);
  const list = data.installs ?? [];
  if (list.length === 0) return;

  const channel = await client.channels.fetch(config.channels.installs);
  const shown = list.slice(-MAX_POST);
  const skipped = list.length - shown.length;
  // Номер установки: у последней он равен общему счётчику, у предыдущих — меньше.
  const lastNo = Number(data.total || 0);

  for (const [i, inst] of shown.entries()) {
    const no = lastNo ? lastNo - (shown.length - 1 - i) : null;
    const where = inst.country ? `${flag(inst.country)} ${inst.country}` : '🌐 неизвестно';
    const version = inst.version ? ` · \`${inst.version}\`` : '';
    await channel.send(`🎉 **Новая установка**${no ? ` №${no}` : ''} — ${where}${version}`);
  }
  if (skipped > 0) await channel.send(`…и ещё **${skipped}** установок за эту минуту.`);

  kvSet(KV_KEY, list[list.length - 1].firstSeen);
  logger.info(`installsWatch: posted ${shown.length} new install(s)`);
}

module.exports = { run };
