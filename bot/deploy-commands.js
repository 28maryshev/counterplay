// Регистрация guild-команд (мгновенно применяются). Запуск: node deploy-commands.js
const { REST, Routes } = require('discord.js');
const config = require('./config');

if (!config.token || !config.clientId || !config.guildId) {
  console.error('DISCORD_TOKEN, CLIENT_ID and GUILD_ID must be set in .env');
  process.exit(1);
}

const commands = ['pool', 'counter', 'matchup', 'admin'].map(
  (name) => require(`./commands/${name}`).data.toJSON()
);

(async () => {
  const rest = new REST().setToken(config.token);
  const data = await rest.put(Routes.applicationGuildCommands(config.clientId, config.guildId), {
    body: commands
  });
  console.log(`Registered ${data.length} guild commands: ${data.map((c) => '/' + c.name).join(', ')}`);
})().catch((e) => {
  console.error('deploy-commands failed:', e);
  process.exit(1);
});
