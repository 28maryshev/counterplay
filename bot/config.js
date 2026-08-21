// Конфигурация из .env + валидация. Каналы опциональны: без id канала
// соответствующая функция просто отключается (warn в лог при старте).
require('dotenv').config();

const required = ['DISCORD_TOKEN', 'CLIENT_ID', 'GUILD_ID'];

function build() {
  const missing = required.filter((k) => !process.env[k]);
  return {
    missing, // index.js решает, падать ли (offline-тесты не требуют токена)
    token: process.env.DISCORD_TOKEN || '',
    clientId: process.env.CLIENT_ID || '',
    guildId: process.env.GUILD_ID || '',
    channels: {
      metaRadar: process.env.CH_META_RADAR || '',
      draftDuels: process.env.CH_DRAFT_DUELS || '',
      submitFinds: process.env.CH_SUBMIT_FINDS || '',
      hallOfFame: process.env.CH_HALL_OF_FAME || '',
      announcements: process.env.CH_ANNOUNCEMENTS || '',
      installs: process.env.CH_INSTALLS || ''
    },
    dataDbUrl:
      process.env.DATA_DB_URL ||
      'https://github.com/28maryshev/counterplay/releases/download/data/data.db',
    dataVersionUrl:
      process.env.DATA_VERSION_URL ||
      'https://github.com/28maryshev/counterplay/releases/download/data/data-version.json',
    dataDir: process.env.DATA_DIR || './data',
    botDbPath: process.env.BOT_DB_PATH || './data/bot.db',
    adminIds: (process.env.ADMIN_USER_IDS || '')
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean),
    siteUrl: process.env.SITE_URL || 'https://counterplays.com',
    // Общий секрет с сайтом: им закрыт /api/telemetry/summary (итоги установок).
    telemetrySecret: process.env.TELEMETRY_SHARED_SECRET || '',
    // Google Analytics: ключ сервисного аккаунта и id ресурса. Без них блок
    // «Источники визитов» в сводке просто не показывается.
    gaKeyPath: process.env.GA_KEY_FILE || '',
    gaProperty: process.env.GA_PROPERTY_ID || ''
  };
}

module.exports = build();
