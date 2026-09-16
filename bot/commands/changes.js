// /changes — что Riot изменил в патче: цены и характеристики предметов, пути
// сборки, кулдауны и числа умений, базовые статы, тексты описаний предметов и
// рун.
//
// Всё считается сравнением официальных данных игры, а не пересказом патчноута,
// поэтому числа здесь ровно те, что опубликовал Riot. Чего тут нет — влияния на
// винрейты: оно считается по матчам, а сразу после выхода патча их ещё нет.
//
// Патч называется так, как его видит игрок в клиенте («26.18»); номер в виде
// данных Riot тоже принимается.
const { SlashCommandBuilder } = require('discord.js');
const { COLORS, embed } = require('../lib/embeds');
const patchDiff = require('../lib/patchDiff');
const patchContext = require('../lib/patchContext');
const logger = require('../lib/logger');
const { displayPatch: dp, dataPatch } = require('../lib/patch');

// Сколько сообщений готовы занять под один ответ.
const PAGES = 4;

module.exports = {
  data: new SlashCommandBuilder()
    .setName('changes')
    .setDescription('What Riot changed in a patch, measured from the game data')
    .addStringOption((o) =>
      o.setName('patch').setDescription('Patch to show, e.g. 26.18 (default: the live one)')
    )
    .addStringOption((o) =>
      o.setName('from').setDescription('Compare against this patch instead of the previous one')
    )
    // По умолчанию выключено: за пояснениями идёт запрос с поиском, он и
    // медленный, и платный, а список изменений полон и без него.
    .addBooleanOption((o) =>
      o.setName('notes').setDescription('Also add context from the official patch notes (slower)')
    ),

  async execute(interaction, ctx) {
    await interaction.deferReply();

    const asked = { to: interaction.options.getString('patch'), from: interaction.options.getString('from') };
    for (const [key, value] of Object.entries(asked)) {
      if (!value) continue;
      const parsed = dataPatch(value);
      if (!parsed) {
        await interaction.editReply(`\`${value}\` is not a patch number — try \`26.18\`.`);
        return;
      }
      asked[key] = parsed;
    }

    let report;
    try {
      report = await patchDiff.diff({ from: asked.from || undefined, to: asked.to || undefined });
    } catch (e) {
      logger.warn(`/changes ${asked.from || '—'} → ${asked.to || 'live'}: ${e.message}`);
      await interaction.editReply(
        'Could not read the patch data right now. If you named a patch, check the number — very old ones are no longer published.'
      );
      return;
    }

    const lines = [
      `Comparing **${dp(report.from)} → ${dp(report.to)}**, straight from the game data — ` +
        'prices, stats and ability numbers. Win rates come later, once enough matches are in.',
      '',
      ...patchDiff.render(report)
    ];
    const pages = patchDiff.paginate(lines);
    const shown = pages.slice(0, PAGES);
    if (pages.length > shown.length)
      shown[shown.length - 1] += `\n\n…and ${pages.length - shown.length} more part(s) of the list.`;

    // Первый кусок — ответ на команду, остальные идут следом: в одно сообщение
    // Discord столько не пускает.
    for (let i = 0; i < shown.length; i++) {
      const e = embed(COLORS.blue).setDescription(shown[i]);
      if (i === 0) e.setTitle(`🧾 What changed in patch ${dp(report.to)}`);
      if (i === 0) await interaction.editReply({ embeds: [e] });
      else await interaction.followUp({ embeds: [e] });
    }

    if (!interaction.options.getBoolean('notes')) return;

    // Пересказ патчноута — отдельным сообщением, со своей подписью и ссылкой на
    // источник. В одном списке с измеренными числами ему не место.
    //
    // Новый запрос стоит денег, поэтому по команде его делает только владелец;
    // остальным отдаётся уже сохранённое. Для текущего патча оно там и есть —
    // его кладёт пост при выходе патча.
    const mayFetch = (ctx && ctx.config ? ctx.config.adminIds : []).includes(interaction.user.id);
    const notes = await patchContext.forPatch(ctx, {
      patch: dp(report.to),
      allowFetch: mayFetch,
      focus: [
        ...report.items.changed.map((e) => e.name),
        ...report.champions.changed.filter((e) => e.spells.length || e.text.length).map((e) => e.name)
      ]
    });
    if (!notes) {
      await interaction.followUp({
        content: mayFetch
          ? 'No patch-note context this time — the numbers above are unaffected.'
          : 'Patch-note context is only kept for patches the bot has announced — the numbers above are the full picture either way.',
        ephemeral: true
      });
      return;
    }
    const e = embed(COLORS.gold)
      .setTitle(`📰 Patch ${dp(report.to)} in Riot’s own words`)
      .setDescription(notes.lines.map((l) => `• ${l}`).join('\n'))
      .setFooter({ text: 'Retold from the official patch notes — not measured like the list above.' });
    if (notes.sources.length) e.setURL(notes.sources[0].url);
    await interaction.followUp({ embeds: [e] });
  }
};
