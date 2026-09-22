// Counterplay Discord bot — точка входа: клиент, команды, события, cron (UTC).
const { Client, Collection, GatewayIntentBits } = require('discord.js');
const cron = require('node-cron');
const config = require('./config');
const logger = require('./lib/logger');
const dataSync = require('./lib/dataSync');
const champs = require('./lib/champions');
const dl = require('./db/dataLayer');
const { kvGet, kvSet } = require('./db/botDb'); // схема bot.db применяется при загрузке модуля

if (config.missing.length) {
  logger.error(`Missing required .env vars: ${config.missing.join(', ')}`);
  process.exit(1);
}

process.on('unhandledRejection', (e) => logger.error('unhandledRejection:', e));
process.on('uncaughtException', (e) => logger.error('uncaughtException:', e));

const client = new Client({
  intents: [GatewayIntentBits.Guilds, GatewayIntentBits.GuildMessages, GatewayIntentBits.MessageContent]
});

const commands = new Collection();
for (const name of ['pool', 'counter', 'matchup', 'admin', 'collect', 'patch', 'changes']) {
  const mod = require(`./commands/${name}`);
  commands.set(mod.data.name, mod);
}

const ctx = { client, config, logger, commands };

for (const name of ['interactionCreate', 'threadCreate']) {
  const ev = require(`./events/${name}`);
  client.on(ev.name, (...args) => ev.execute(ctx, ...args));
}

// Реже, чем раз в сутки. Крон вида '0 12 */3 * *' считает дни МЕСЯЦА: 1, 4, 7…
// 31 — и на стыке месяцев даёт разрыв в сутки вместо трёх. Плюс перезапуск
// контейнера в неудачную минуту просто съедал бы выпуск. Поэтому будим задачу
// ежедневно, а частоту держим по отметке о прошлом запуске.
function everyDays(days, key, fn) {
  return async (ctx) => {
    const kvKey = `cadence_${key}`;
    const last = Number(kvGet(kvKey) || 0);
    // Допуск в два часа. Отметка ставится по ОКОНЧАНИИ работы, а крон будит
    // задачу ровно через N суток после прошлого ПРОБУЖДЕНИЯ — то есть всегда чуть
    // раньше отметки. Без запаса выпуск промахивался бы мимо окна и уезжал на
    // сутки, а перезапуски копили бы этот сдвиг. Разбудить второй раз за день
    // допуск не может: крон срабатывает раз в сутки.
    if (Date.now() < last + days * 864e5 - 2 * 3600e3) return;
    await fn(ctx);
    kvSet(kvKey, String(Date.now()));
  };
}

// Cron-джоб с защитой: ошибка одной функции не роняет процесс.
function job(label, channelKey, fn) {
  return async () => {
    if (channelKey && !config.channels[channelKey]) return; // канал не настроен — функция выключена
    try {
      await fn(ctx);
    } catch (e) {
      logger.error(`cron ${label}:`, e);
    }
  };
}

async function main() {
  await dataSync.ensure();
  try {
    logger.info(`data: current patch ${dl.getCurrentPatch()}, ${dl.countMatches()} matches`);
  } catch {
    logger.warn('data: snapshot not loaded yet — data features answer "unavailable"');
  }
  await champs.load();

  client.once('clientReady', () => {
    logger.info(`Logged in as ${client.user.tag}`);
    for (const [key, id] of Object.entries(config.channels))
      if (!id) logger.warn(`channel ${key} not configured — the feature is disabled`);

    const tz = { timezone: 'Etc/UTC' };
    // Радар — раз в двое суток, дуэли — раз в трое: ежедневная лента была
    // слишком плотной для канала. Разгадка ходит следом за задачей: она сама
    // ищет неразгаданную дуэль, поэтому в дни без задачи просто молчит.
    cron.schedule('0 10 * * *',
      job('metaRadar', 'metaRadar', everyDays(2, 'metaRadar', require('./cron/metaRadar').run)), tz);
    cron.schedule('0 12 * * *',
      job('duelPost', 'draftDuels', everyDays(3, 'duelPost', require('./cron/duelPost').run)), tz);
    cron.schedule('0 22 * * *', job('duelReveal', 'draftDuels', require('./cron/duelReveal').run), tz);
    cron.schedule('0 20 * * 0', job('weeklyBoard', 'draftDuels', require('./cron/weeklyBoard').run), tz);
    cron.schedule('15 * * * *', job('dataSync', null, () => dataSync.sync()), tz);
    cron.schedule('30 * * * *', job('releaseWatch', 'announcements', require('./cron/releaseWatch').run), tz);
    cron.schedule('45 * * * *', job('patchWatch', 'announcements', require('./cron/patchWatch').run), tz);
    // Итоги дня по установкам — в полночь по Киеву (не UTC: «конец дня» должен
    // совпадать с календарным днём владельца). Сами установки сюда больше не
    // опрашиваются: о каждой сообщает сам сайт вебхуком в момент первого
    // выхода программы на связь (см. lib/install-notify.ts на сайте), поэтому
    // минутная лента отключена — она давала второе сообщение о том же событии.
    cron.schedule('0 0 * * *', job('installsDaily', 'installs', require('./cron/installsDaily').run), {
      timezone: 'Europe/Kyiv'
    });
    logger.info('cron scheduled (UTC): radar 10:00 every 2 days, duel 12:00 every 3 days, reveal 22:00, board Sun 20:00, sync+releases+patch hourly; installs summary at 00:00 Europe/Kyiv (live feed is pushed by the site)');
  });

  await client.login(config.token);
}

main().catch((e) => {
  logger.error('fatal:', e);
  process.exit(1);
});
