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

// Статус коллектор переписывает раз в 30 с; старше этого — значит, он молчит.
const STATUS_STALE_MS = 5 * 60 * 1000;

// Что ответить на присланный ключ. Коллектор берёт ключ из файла только между
// кругами: пока идёт публикация или сбор старым ключом, новый ждёт. 5 октября бот
// пообещал «старт через ~15 с», а ключ два часа пролежал под публикацией.
function keyReply(st, now = Date.now()) {
  const age = st ? now - Date.parse(st.at) : NaN;
  if (!st || !(age < STATUS_STALE_MS)) {
    const mins = Number.isFinite(age) ? `${Math.round(age / 60000)} min` : 'a while';
    return `Key saved, but the collector has not reported for ${mins} — check \`/collect status\`.`;
  }
  switch (st.state) {
    case 'key_expired':
    case 'publishing':
    case 'published':
    case 'pruning':
      return 'Key saved. The collector is publishing the database right now; collecting with this key ' +
        'starts once that is done (from a few minutes to about two hours). The channel gets ▶️ when it starts.';
    case 'collecting':
      return 'Key saved. The collector is still running on the current key and switches to this one ' +
        'when that key expires or the round ends.';
    case 'disk_full':
      return 'Key saved, but collecting is stopped: the server is low on disk. It resumes on its own once space is freed.';
    default:
      return 'Key accepted — the collector will pick it up within a minute and start collecting.';
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
      await interaction.reply({ content: keyReply(readStatus()), ephemeral: true });
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
          st.db_mb != null ? `database size: **${st.db_mb} MB**` : null,
          // Диск — узкое место сервера (база растёт), поэтому помечаем, когда мало.
          st.disk_free_gb != null
            ? `disk free: **${st.disk_free_gb} GB** of ${st.disk_total_gb} GB` +
              (st.disk_free_gb < 1 ? '  ⚠️ low' : '')
            : null,
          st.version ? `published version: \`${st.version}\` (patch ${st.patch})` : null,
          st.error ? `last error: \`${st.error}\`` : null,
          `updated: ${String(st.at).slice(0, 19)}`
        ].filter(Boolean)
      : ['collector has not reported yet'];
    lines.push(`key present: **${hasKey ? 'yes' : 'no'}**`);

    await interaction.reply({ content: lines.join('\n'), ephemeral: true });
  }
};
