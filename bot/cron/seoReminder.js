// Еженедельное напоминание посмотреть позиции в поиске.
//
// Приходит в личные сообщения владельцу, а не в канал: канал установок на
// сервере не настроен (CH_INSTALLS пуст), да и напоминание это личное — в общий
// канал ему незачем. Адресаты — ADMIN_USER_IDS.
//
// Сам отчёт бот собрать не может и не должен: ключи Search Console и Bing лежат
// ТОЛЬКО на машине владельца, и переносить их на сервер ради напоминалки — плохой
// размен. Поэтому здесь напоминание с готовым списком, на что смотреть, а цифры
// даёт `python ops/seo.py` на месте.
//
// Порядок пунктов не случайный: сверху то, что решает, снизу то, что следствие.
// Позиции сами по себе ничего не говорят, пока на сайт никто не ссылается.
const { COLORS, embed } = require('../lib/embeds');
const logger = require('../lib/logger');

const WATCH = [
  '**Входящие ссылки** — стало не ноль? Это главный признак, что дело сдвинулось.',
  '**Показы в Google** — растут ли (было 75 за неделю).',
  '**CTR в Bing** — растёт ли (было 1.3% при 1 689 показах).',
  '**Запрос `counterplay`** — ползёт ли вверх с 33 места.',
  '**`Discovered, not indexed`** — уменьшается ли доля.'
];

async function run(ctx) {
  const { client, config } = ctx;
  if (!config.adminIds.length) {
    logger.warn('seoReminder: некому писать — ADMIN_USER_IDS пуст');
    return;
  }

  const e = embed(COLORS.blue)
    .setTitle('Пора посмотреть позиции в поиске')
    .setDescription('```\npython ops/seo.py\n```Обе выдачи разом — Google и Bing.')
    .addFields({ name: 'На что смотреть, в этом порядке', value: WATCH.join('\n') })
    .addFields({
      name: 'Чего не делать',
      value: 'Не смотреть дневные скачки — там один шум. Движение видно за две-три недели.'
    });

  let sent = 0;
  for (const id of config.adminIds) {
    try {
      const user = await client.users.fetch(id);
      await user.send({ embeds: [e] });
      sent++;
    } catch (err) {
      // Закрытые личные сообщения — не повод ронять задачу остальным адресатам.
      logger.warn(`seoReminder: не доставлено ${id}: ${err.message}`);
    }
  }
  logger.info(`seoReminder: отправлено ${sent} из ${config.adminIds.length}`);
}

module.exports = { run, WATCH };
