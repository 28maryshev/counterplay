// Draft Duels: генерация и публикация ежедневной задачи (12:00 UTC).
const {
  ActionRowBuilder,
  ButtonBuilder,
  ButtonStyle
} = require('discord.js');
const dl = require('../db/dataLayer');
const champs = require('../lib/champions');
const scoring = require('../lib/scoring');
const { COLORS, embed } = require('../lib/embeds');
const { db } = require('../db/botDb');
const logger = require('../lib/logger');

const ROLES = ['top', 'jungle', 'mid', 'adc', 'support'];
const roleLabel = { top: 'Top', jungle: 'Jungle', mid: 'Mid', adc: 'ADC', support: 'Support' };
const LETTERS = ['A', 'B', 'C', 'D'];
const LETTER_EMOJI = { A: '🅰', B: '🅱', C: '🇨', D: '🇩' };

const pick = (arr) => arr[Math.floor(Math.random() * arr.length)];

/** Сгенерировать драфт: союзники/враги из топ-15 популярных каждой роли. */
function generateDraft(playerRole) {
  const used = new Set();
  const take = (role) => {
    const pool = dl.wTopPopular(role, 15).filter((r) => !used.has(r.championId));
    if (!pool.length) return null;
    const c = pick(pool).championId;
    used.add(c);
    return c;
  };
  const allies = {};
  for (const r of ROLES) if (r !== playerRole) allies[r] = take(r);
  const enemies = {};
  for (const r of ROLES) enemies[r] = take(r);
  if (Object.values(allies).some((v) => v == null) || Object.values(enemies).some((v) => v == null))
    return null;
  return { allies, enemies, used };
}

/** Полная генерация задачи с контролем качества (top1 − top2 ≥ 3). */
function generateDuel(dateStr) {
  const playerRole = ROLES[Math.floor(Date.parse(dateStr) / 86400e3) % ROLES.length];

  for (let attempt = 0; attempt < 10; attempt++) {
    const draft = generateDraft(playerRole);
    if (!draft) continue;

    const candidates = dl
      .wTopPopular(playerRole, 25)
      .filter((r) => !draft.used.has(r.championId))
      .map((r) => {
        const s = scoring.score(r.championId, playerRole, draft.enemies, draft.allies);
        return s ? { id: r.championId, ...s } : null;
      })
      .filter(Boolean)
      .sort((a, b) => b.score - a.score);
    if (candidates.length < 8) continue;

    if (candidates[0].score - candidates[1].score < 3 && attempt < 9) continue;

    // Варианты: top1 + два из мест 2–6 + один из хвоста (места 15–25).
    const mid = candidates.slice(1, 6);
    const tailStart = Math.min(14, candidates.length - 2);
    const tail = candidates.slice(tailStart);
    const optSet = [candidates[0]];
    while (optSet.length < 3 && mid.length) {
      const c = pick(mid);
      mid.splice(mid.indexOf(c), 1);
      optSet.push(c);
    }
    const trap = pick(tail.filter((c) => !optSet.includes(c)) || mid);
    if (trap) optSet.push(trap);
    if (optSet.length < 4) continue;

    // Перемешать → буквы A–D.
    for (let i = optSet.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [optSet[i], optSet[j]] = [optSet[j], optSet[i]];
    }

    return {
      date: dateStr,
      role: playerRole,
      allies: draft.allies,
      enemies: draft.enemies,
      options: optSet.map((o) => o.id),
      correct: candidates[0].id,
      explanation: {
        options: optSet.map((o, i) => ({ letter: LETTERS[i], id: o.id, score: o.score })),
        top: { id: candidates[0].id, score: candidates[0].score, parts: candidates[0].parts, detail: candidates[0].detail },
        runner: { id: candidates[1].id, score: candidates[1].score, detail: candidates[1].detail }
      }
    };
  }
  return null;
}

function duelEmbed(duel) {
  const teamLines = (obj) =>
    ROLES.filter((r) => obj[r] != null)
      .map((r) => `${roleLabel[r]}: **${champs.name(obj[r])}**`)
      .join('\n');
  const optLine = duel.options
    .map((id, i) => `${LETTER_EMOJI[LETTERS[i]]} ${champs.name(id)}`)
    .join('  ');
  const opp = duel.enemies[duel.role];

  return embed(COLORS.gold)
    .setTitle(`🧩 DRAFT DUEL — ${duel.date}`)
    .setDescription(
      `Which pick does the Counterplay engine rate highest here?\n\n${optLine}\n\n` +
        `Vote below! Answer drops at **22:00 UTC**.`
    )
    .addFields(
      { name: 'Your role', value: roleLabel[duel.role], inline: true },
      { name: 'Lane opponent', value: opp ? `**${champs.name(opp)}**` : '—', inline: true },
      { name: 'Your team', value: teamLines(duel.allies) || '—', inline: true },
      { name: 'Enemy team', value: teamLines(duel.enemies), inline: true }
    );
}

function buttons(duelId, disabled = false) {
  const row = new ActionRowBuilder();
  for (const letter of LETTERS) {
    row.addComponents(
      new ButtonBuilder()
        .setCustomId(`duel:${duelId}:${letter}`)
        .setLabel(letter)
        .setStyle(ButtonStyle.Secondary)
        .setDisabled(disabled)
    );
  }
  return row;
}

async function run(ctx) {
  const date = new Date().toISOString().slice(0, 10);
  if (db.prepare('SELECT id FROM duels WHERE date = ?').get(date)) {
    logger.warn(`duelPost: duel for ${date} already exists — skipping`);
    return;
  }
  const duel = generateDuel(date);
  if (!duel) {
    logger.warn('duelPost: failed to generate a duel — skipping');
    return;
  }

  const info = db
    .prepare(
      `INSERT INTO duels (date, role, ally_json, enemy_json, options_json, correct, explanation_json)
       VALUES (?, ?, ?, ?, ?, ?, ?)`
    )
    .run(
      duel.date,
      duel.role,
      JSON.stringify(duel.allies),
      JSON.stringify(duel.enemies),
      JSON.stringify(duel.options),
      duel.correct,
      JSON.stringify(duel.explanation)
    );
  const duelId = info.lastInsertRowid;

  const channel = await ctx.client.channels.fetch(ctx.config.channels.draftDuels);
  const msg = await channel.send({ embeds: [duelEmbed(duel)], components: [buttons(duelId)] });
  db.prepare('UPDATE duels SET message_id = ? WHERE id = ?').run(msg.id, duelId);
  logger.info(
    `duelPost: duel #${duelId} posted (${duel.role}, correct: ${champs.name(duel.correct)})`
  );
}

module.exports = { run, generateDuel, duelEmbed, buttons, LETTERS };
