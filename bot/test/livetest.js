// Живой прогон: логин + немедленный пост Meta Radar и Draft Duel (как /admin *-now).
// Запуск: node test/livetest.js [radar|duel|reveal|board]... (без аргументов: radar duel)
const { Client, GatewayIntentBits } = require('discord.js');
const config = require('../config');
const logger = require('../lib/logger');
const dataSync = require('../lib/dataSync');
const champs = require('../lib/champions');
require('../db/botDb');

const jobs = {
  radar: () => require('../cron/metaRadar').run,
  duel: () => require('../cron/duelPost').run,
  reveal: () => require('../cron/duelReveal').run,
  board: () => require('../cron/weeklyBoard').run
};

(async () => {
  const wanted = process.argv.slice(2).length ? process.argv.slice(2) : ['radar', 'duel'];
  await dataSync.ensure();
  await champs.load();
  const client = new Client({ intents: [GatewayIntentBits.Guilds] });
  await client.login(config.token);
  await new Promise((res) => client.once('clientReady', res));
  logger.info(`livetest: logged in as ${client.user.tag}`);
  const ctx = { client, config, logger };
  for (const name of wanted) {
    if (!jobs[name]) continue;
    logger.info(`livetest: running ${name}…`);
    await jobs[name]()(ctx);
  }
  await client.destroy();
  logger.info('livetest: done');
  process.exit(0);
})().catch((e) => {
  logger.error('livetest failed:', e);
  process.exit(1);
});
