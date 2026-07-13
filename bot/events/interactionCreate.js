// Роутер взаимодействий: слэш-команды, автокомплит чемпионов, кнопки дуэлей.
const dl = require('../db/dataLayer');
const champs = require('../lib/champions');
const { db } = require('../db/botDb');
const logger = require('../lib/logger');

async function handleButton(ctx, interaction) {
  const [tag, duelIdStr, letter] = interaction.customId.split(':');
  if (tag !== 'duel') return;
  const duel = db.prepare('SELECT * FROM duels WHERE id = ?').get(Number(duelIdStr));
  if (!duel || duel.revealed) {
    await interaction.reply({ content: 'Voting is closed for this duel.', ephemeral: true });
    return;
  }
  db.prepare(
    `INSERT INTO duel_votes (duel_id, user_id, choice, voted_at) VALUES (?, ?, ?, ?)
     ON CONFLICT(duel_id, user_id) DO UPDATE SET choice = excluded.choice, voted_at = excluded.voted_at`
  ).run(duel.id, interaction.user.id, letter, new Date().toISOString());
  const options = JSON.parse(duel.options_json);
  const idx = ['A', 'B', 'C', 'D'].indexOf(letter);
  const champName = champs.name(options[idx]);
  await interaction.reply({
    content: `Vote recorded: **${letter} — ${champName}**. You can change it until the reveal.`,
    ephemeral: true
  });
}

async function handleAutocomplete(interaction) {
  const focused = interaction.options.getFocused().toLowerCase();
  const names = champs
    .allNames()
    .filter((n) => n.toLowerCase().includes(focused))
    .slice(0, 25);
  await interaction.respond(names.map((n) => ({ name: n, value: n })));
}

async function execute(ctx, interaction) {
  try {
    if (interaction.isAutocomplete()) {
      await handleAutocomplete(interaction);
      return;
    }
    if (interaction.isButton()) {
      await handleButton(ctx, interaction);
      return;
    }
    if (!interaction.isChatInputCommand()) return;

    const command = ctx.commands.get(interaction.commandName);
    if (!command) return;
    try {
      await command.execute(interaction, ctx);
    } catch (e) {
      if (e instanceof dl.DataUnavailableError) {
        const msg = { content: 'Data temporarily unavailable — try again in a few minutes.', ephemeral: true };
        if (interaction.deferred || interaction.replied) await interaction.editReply(msg);
        else await interaction.reply(msg);
        return;
      }
      throw e;
    }
  } catch (e) {
    logger.error(`interaction ${interaction.commandName ?? interaction.customId ?? '?'}:`, e);
    try {
      const msg = { content: 'Something went wrong — the incident is logged.', ephemeral: true };
      if (interaction.deferred || interaction.replied) await interaction.editReply(msg);
      else if (interaction.isRepliable()) await interaction.reply(msg);
    } catch {
      /* interaction истёк */
    }
  }
}

module.exports = { name: 'interactionCreate', execute };
