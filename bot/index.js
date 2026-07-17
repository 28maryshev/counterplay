// Counterplay Discord bot — точка входа: клиент, команды, события, cron (UTC).
const { Client, Collection, GatewayIntentBits } = require('discord.js');
const cron = require('node-cron');
const config = require('./config');
const logger = require('./lib/logger');
const dataSync = require('./lib/dataSync');
const champs = require('./lib/champions');
const dl = require('./db/dataLayer');
require('./db/botDb'); // применить схему bot.db при старте

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
for (const name of ['pool', 'counter', 'matchup', 'admin', 'collect']) {
  const mod = require(`./commands/${name}`);
  commands.set(mod.data.name, mod);
}

const ctx = { client, config, logger, commands };

for (const name of ['interactionCreate', 'threadCreate']) {
  const ev = require(`./events/${name}`);
  client.on(ev.name, (...args) => ev.execute(ctx, ...args));
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
    cron.schedule('0 10 * * *', job('metaRadar', 'metaRadar', require('./cron/metaRadar').run), tz);
    cron.schedule('0 12 * * *', job('duelPost', 'draftDuels', require('./cron/duelPost').run), tz);
    cron.schedule('0 22 * * *', job('duelReveal', 'draftDuels', require('./cron/duelReveal').run), tz);
    cron.schedule('0 20 * * 0', job('weeklyBoard', 'draftDuels', require('./cron/weeklyBoard').run), tz);
    cron.schedule('15 * * * *', job('dataSync', null, () => dataSync.sync()), tz);
    cron.schedule('30 * * * *', job('releaseWatch', 'announcements', require('./cron/releaseWatch').run), tz);
    logger.info('cron scheduled (UTC): radar 10:00, duel 12:00, reveal 22:00, board Sun 20:00, sync+releases hourly');
  });

  await client.login(config.token);
}

main().catch((e) => {
  logger.error('fatal:', e);
  process.exit(1);
});
