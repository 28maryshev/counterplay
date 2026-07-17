// /collect — управление сбором статистики (доступ только ADMIN_USER_IDS).
//
// Ключ Riot живёт 24 ч, поэтому его приходится обновлять руками. Команда
// эфемерная: ключ виден только тебе и НЕ попадает в историю канала.
// Бот не собирает сам — он кладёт ключ в общий том, а сбор ведёт отдельный
// сервис (pipeline/collector_service.py), который следит за файлом.
const fs = require('node:fs');
const path = require('node:path');
const { SlashCommandBuilder } = require('discord.js');

// Общий том с коллектором (см. docker-compose коллектора).
const CONTROL_DIR = process.env.CONTROL_DIR || '/control';
const KEY_FILE = path.join(CONTROL_DIR, 'key');
const STATUS_FILE = path.join(CONTROL_DIR, 'status');

const KEY_RE = /^RGAPI-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function readStatus() {
  try {
    return JSON.parse(fs.readFileSync(STATUS_FILE, 'utf8'));
  } catch {
    return null;
  }
}

module.exports = {
  data: new SlashCommandBuilder()
    .setName('collect')
    .setDescription('Stats collection controls')
    .addSubcommand((s) =>
      s
        .setName('key')
        .setDescription('Send a fresh Riot API key to start/continue collection')
        .addStringOption((o) =>
          o.setName('value').setDescription('RGAPI-… (dev key, valid 24h)').setRequired(true)
        )
    )
    .addSubcommand((s) => s.setName('status').setDescription('Collector status'))
    .addSubcommand((s) => s.setName('stop').setDescription('Clear the key — collector stops after the current match')),

  async execute(interaction, ctx) {
    if (!ctx.config.adminIds.includes(interaction.user.id)) {
      await interaction.reply({ content: 'No access.', ephemeral: true });
      return;
    }
    const sub = interaction.options.getSubcommand();

    if (sub === 'key') {
      const key = interaction.options.getString('value').trim();
      // Проверяем формат до записи: опечатка иначе стоила бы цикла «сбор → 403 →
      // уведомление» на пустом месте.
      if (!KEY_RE.test(key)) {
        await interaction.reply({
          content: 'Not a Riot dev key. Expected `RGAPI-xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`.',
          ephemeral: true
        });
        return;
      }
      try {
        fs.mkdirSync(CONTROL_DIR, { recursive: true });
        // 0600: ключ читает только владелец тома (бот и коллектор — один uid).
        fs.writeFileSync(KEY_FILE, key, { encoding: 'utf8', mode: 0o600 });
      } catch (e) {
        await interaction.reply({ content: `Could not hand the key over: \`${e.message}\``, ephemeral: true });
        return;
      }
      await interaction.reply({
        content: 'Key accepted — the collector will pick it up within ~15s and start collecting.',
        ephemeral: true
      });
      return;
    }

    if (sub === 'stop') {
      try {
        fs.unlinkSync(KEY_FILE);
      } catch {
        /* уже нет — не страшно */
      }
      await interaction.reply({
        content: 'Key cleared. The collector stops after the current match and waits for a new key.',
        ephemeral: true
      });
      return;
    }

    // status
    const st = readStatus();
    const hasKey = fs.existsSync(KEY_FILE);
    const lines = st
      ? [
          `state: **${st.state}**`,
          st.matches != null ? `matches in db: **${st.matches.toLocaleString('en-US')}**` : null,
          st.this_key != null ? `collected on this key: **+${st.this_key.toLocaleString('en-US')}**` : null,
          st.collected != null ? `collected last round: **${st.collected}**` : null,
          st.version ? `published version: \`${st.version}\` (patch ${st.patch})` : null,
          st.error ? `last error: \`${st.error}\`` : null,
          `updated: ${String(st.at).slice(0, 19)}`
        ].filter(Boolean)
      : ['collector has not reported yet'];
    lines.push(`key present: **${hasKey ? 'yes' : 'no'}**`);

    await interaction.reply({ content: lines.join('\n'), ephemeral: true });
  }
};
