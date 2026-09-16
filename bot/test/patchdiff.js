// Проверка сравнения патчей на маленьких выдуманных данных: ни сети, ни базы.
// Запуск: node test/patchdiff.js
//
// Здесь проверяется не «работает ли скачивание», а ровно те правила, из-за
// которых сводка когда-то врала: варианты предметов и чемпионов из других
// режимов, смена единиц измерения, переезд значений внутри данных, косметика в
// подсказках. Каждый случай ниже — реальный, все они были в патчах 16.15–16.18.
process.env.DISCORD_TOKEN = process.env.DISCORD_TOKEN || 'offline';
process.env.CLIENT_ID = process.env.CLIENT_ID || 'offline';
process.env.GUILD_ID = process.env.GUILD_ID || 'offline';
// Своя база на время прогона: проверки не должны трогать рабочую bot.db.
process.env.BOT_DB_PATH =
  process.env.BOT_DB_PATH || require('path').join(require('os').tmpdir(), 'counterplay-test-bot.db');

const assert = require('assert');
const { render, paginate, _internal: pd } = require('../lib/patchDiff');
const { displayPatch, dataPatch } = require('../lib/patch');

let passed = 0;
function check(name, fn) {
  try {
    fn();
    passed++;
    console.log(`  ok  ${name}`);
  } catch (e) {
    console.error(`  FAIL ${name}: ${e.message}`);
    process.exitCode = 1;
  }
}

function section(t) {
  console.log('\n' + t);
}

// ─── разметка ───────────────────────────────────────────────────────────────

section('Разметка Riot → текст');

check('теги снимаются, <br> становится переносом', () => {
  const out = pd.plain('<mainText><stats><attention>45</attention> Ability Power</stats><br>Heals an ally</mainText>');
  assert.strictEqual(out, '45 Ability Power\nHeals an ally');
});

check('сказка про предмет выбрасывается', () => {
  assert.ok(!pd.plain('<stats>40 AD</stats><flavorText>Forged in fire</flavorText>').includes('Forged'));
});

check('подстановка остаётся видимой, даже с умножением', () => {
  assert.strictEqual(pd.plain('deals {{ magicpen*100 }}% damage'), 'deals {magicpen*100}% damage');
});

check('пробел перед точкой от снятого тега не считается правкой', () => {
  assert.deepStrictEqual(pd.textHunks('Magic Penetration<br>.', 'Magic Penetration.'), []);
});

// ─── сравнение текстов ──────────────────────────────────────────────────────

section('Сравнение текстов');

check('одинаковый текст изменений не даёт', () => {
  assert.deepStrictEqual(pd.textHunks('Attacks grant 8% Attack Speed', 'Attacks grant 8% Attack Speed'), []);
});

check('правка числа показывается с окружением', () => {
  const [h] = pd.textHunks('grant Attack Speed for 3 seconds', 'grant Attack Speed for 4 seconds');
  assert.deepStrictEqual(h.removed, ['3']);
  assert.deepStrictEqual(h.added, ['4']);
  assert.ok(pd.renderHunk(h).includes('**3 → 4**'), pd.renderHunk(h));
  assert.ok(pd.renderHunk(h).includes('seconds'), pd.renderHunk(h));
});

check('добавленный кусок помечается плюсом', () => {
  const [h] = pd.textHunks('Active: pulls enemies', 'Passive: grants Magic Penetration. Active: pulls enemies');
  assert.ok(pd.renderHunk(h).startsWith('**+ '), pd.renderHunk(h));
});

// ─── что считать косметикой ─────────────────────────────────────────────────

section('Косметика против баланса');

check('процент, уехавший внутрь подстановки, — косметика', () => {
  // Poppy, 16.18: «{movespeedmod}%» стало «{movespeedmod}».
  assert.strictEqual(pd.cosmetic(pd.textHunks('Slows by {{ movespeedmod }}%', 'Slows by {{ movespeedmod }}')), true);
});

check('замена одной подстановки другой — косметика', () => {
  // Lulu, 16.16: «{{ e7 }}%» стало «{{ asbonus*100 }}%».
  assert.strictEqual(
    pd.cosmetic(pd.textHunks('grants {{ e7 }}% Attack Speed', 'grants {{ asbonus*100 }}% Attack Speed')),
    true
  );
});

check('правка обычного слова — не косметика, даже без цифр', () => {
  // Locke, 16.16: «his current Cooldown» стало «his total Cooldown».
  assert.strictEqual(pd.cosmetic(pd.textHunks('refund his current Cooldown', 'refund his total Cooldown')), false);
});

// ─── числа умений ───────────────────────────────────────────────────────────

section('Числа умений');

check('перевод процентов в доли не выдаётся за нерф', () => {
  assert.strictEqual(pd.unitsOnly('20/22.5/25/27.5/30', '0.2/0.225/0.25/0.275/0.3'), true);
});

check('настоящий нерф единицами не объясняется', () => {
  assert.strictEqual(pd.unitsOnly('75/110/145/180/215', '65/100/135/170/205'), false);
});

check('обнуление поля — это переезд значения, а не правка', () => {
  assert.strictEqual(pd.relocated('30/55/80/105/130', '0'), true);
  assert.strictEqual(pd.relocated('30/55', '25/50'), false);
});

// ─── предметы ───────────────────────────────────────────────────────────────

section('Предметы');

const item = (over = {}) => ({
  name: 'Test',
  description: '<stats><attention>40</attention> Attack Damage</stats>',
  gold: { total: 3000, purchasable: true },
  from: [],
  stats: { FlatPhysicalDamageMod: 40 },
  maps: { 11: true, 12: true, 30: false },
  ...over
});

check('правка цены видна', () => {
  const r = pd.diffItems({ 1: item() }, { 1: item({ gold: { total: 3100, purchasable: true } }) });
  assert.strictEqual(r.changed.length, 1);
  assert.deepStrictEqual(r.changed[0].gold, { from: 3000, to: 3100 });
});

check('правка вне Ущелья не попадает в сводку', () => {
  // 16.18: Riot трогал вариант Banshee's Veil, которого на Ущелье нет.
  const arena = { maps: { 11: false, 30: true } };
  const r = pd.diffItems(
    { 1: item(arena) },
    { 1: item({ ...arena, description: '<stats><attention>45</attention> Attack Damage</stats>' }) }
  );
  assert.strictEqual(r.changed.length, 0);
});

check('изменение пути сборки показывается именами', () => {
  const parts = { 2: item({ name: 'Ruby Crystal' }), 3: item({ name: 'Tunneler' }) };
  const r = pd.diffItems(
    { ...parts, 1: item({ from: ['2', '3'] }) },
    { ...parts, 1: item({ from: ['3'] }) }
  );
  assert.deepStrictEqual(r.changed[0].path, { from: ['Ruby Crystal', 'Tunneler'], to: ['Tunneler'] });
});

check('характеристики не дублируются с текстом описания', () => {
  // Числа характеристик Riot печатает в самом описании: если изменился текст,
  // отдельная строка про stats была бы тем же изменением во второй раз.
  const r = pd.diffItems(
    { 1: item() },
    {
      1: item({
        description: '<stats><attention>45</attention> Attack Damage</stats>',
        stats: { FlatPhysicalDamageMod: 45 }
      })
    }
  );
  assert.ok(r.changed[0].text.length, 'текст должен быть');
  assert.ok(!r.changed[0].stats, 'stats дублируют текст');
});

check('новый и убранный предмет попадают в свои списки', () => {
  const r = pd.diffItems({ 1: item({ name: 'Gone' }) }, { 2: item({ name: 'Fresh' }) });
  assert.deepStrictEqual(r.added.map((x) => x.name), ['Fresh']);
  assert.deepStrictEqual(r.removed.map((x) => x.name), ['Gone']);
});

// ─── чемпионы ───────────────────────────────────────────────────────────────

section('Чемпионы');

const spell = (over = {}) => ({
  name: 'Q Spell',
  tooltip: 'Deals {{ damage }} damage',
  cooldownBurn: '12/11/10/9/8',
  costBurn: '50',
  rangeBurn: '600',
  maxrank: 5,
  effectBurn: [null, '30/55/80'],
  datavalues: {},
  ...over
});

const champ = (over = {}) => ({
  name: 'Tester',
  stats: { hp: 650, armor: 34, attackdamage: 60 },
  spells: [spell()],
  passive: { name: 'Passive', description: 'Does a thing' },
  ...over
});

check('базовая характеристика с читаемой подписью', () => {
  const r = pd.diffChampions({ A: champ() }, { A: champ({ stats: { hp: 650, armor: 32, attackdamage: 60 } }) });
  assert.deepStrictEqual(r.changed[0].stats, [{ key: 'armor', label: 'armor', from: 34, to: 32 }]);
});

check('перезарядка умения', () => {
  const r = pd.diffChampions({ A: champ() }, { A: champ({ spells: [spell({ cooldownBurn: '13/12/11/10/9' })] }) });
  assert.deepStrictEqual(r.changed[0].spells[0].changes, [
    { label: 'cooldown', from: '12/11/10/9/8', to: '13/12/11/10/9' }
  ]);
});

check('вариант чемпиона из другого режима игнорируется', () => {
  // Когда режим закрывают, такие записи исчезают разом — без фильтра сводка
  // объявила бы, что Riot удалил из игры шесть десятков чемпионов.
  const r = pd.diffChampions({ A: champ(), Jade_A: champ() }, { A: champ() });
  assert.strictEqual(r.removed.length, 0);
});

check('переезд значения в другое поле не показывается как обнуление', () => {
  const r = pd.diffChampions({ A: champ() }, { A: champ({ spells: [spell({ effectBurn: [null, '0'] })] }) });
  assert.strictEqual(r.changed.length, 0);
});

check('новый чемпион виден', () => {
  const r = pd.diffChampions({}, { A: champ({ title: 'the Tested' }) });
  assert.strictEqual(r.added[0].name, 'Tester');
});

// ─── руны ───────────────────────────────────────────────────────────────────

section('Руны');

const tree = (desc) => [
  { name: 'Domination', slots: [{ runes: [{ id: 9923, name: 'Hail of Blades', shortDesc: 'x', longDesc: desc }] }] }
];

check('правка числа в описании руны', () => {
  const r = pd.diffRunes(tree('Gain 120% Attack Speed'), tree('Gain 90% Attack Speed'));
  assert.strictEqual(r.changed.length, 1);
  assert.ok(pd.renderHunk(r.changed[0].text[0]).includes('**120% → 90%**'));
});

// ─── сводка ─────────────────────────────────────────────────────────────────

section('Сводка');

check('одинаковые правки у многих чемпионов сводятся в одну строку', () => {
  // 16.16: двадцати семи стрелкам подняли сопротивление магии одной строкой.
  const before = {};
  const after = {};
  for (const n of ['Ashe', 'Jinx', 'Vayne']) {
    before[n] = champ({ name: n, stats: { hp: 650, spellblock: 30 } });
    after[n] = champ({ name: n, stats: { hp: 650, spellblock: 33 } });
  }
  const report = {
    from: '16.15',
    to: '16.16',
    items: { changed: [], added: [], removed: [] },
    champions: pd.diffChampions(before, after),
    runes: { changed: [], added: [], removed: [] },
    summoners: { changed: [], added: [], removed: [] }
  };
  const text = render(report).join('\n');
  assert.ok(text.includes('**Ashe, Jinx, Vayne** (3)'), text);
  assert.strictEqual((text.match(/magic resist: 30 → 33/g) || []).length, 1, 'правка должна встретиться один раз');
});

check('пустая сводка так и говорит', () => {
  const empty = { changed: [], added: [], removed: [] };
  const text = render({ items: empty, champions: empty, runes: empty, summoners: empty }).join('\n');
  assert.ok(text.includes('No Summoner'), text);
});

check('двух пустых строк подряд в сводке не бывает', () => {
  const empty = { changed: [], added: [], removed: [] };
  const lines = render({
    items: { changed: [{ id: '1', name: 'A', gold: { from: 1, to: 2 } }], added: [], removed: [] },
    champions: { changed: [{ id: 'B', name: 'B', stats: [], spells: [], text: [] }], added: [], removed: [] },
    runes: empty,
    summoners: empty
  });
  assert.ok(!lines.some((l, i) => !l.trim() && !(lines[i - 1] || 'x').trim()), lines.join('|'));
});

// ─── постинг в Discord ──────────────────────────────────────────────────────

section('Постинг');

check('сводка режется по строкам, а не посреди числа', () => {
  const lines = Array.from({ length: 40 }, (_, i) => `• cooldown: 12/11/10/9/8 → 13/12/11/10/${i}`);
  const pages = paginate(lines, 300);
  assert.ok(pages.length > 1, 'должно получиться несколько кусков');
  for (const p of pages) {
    assert.ok(p.length <= 300, `кусок ${p.length} символов`);
    for (const l of p.split('\n')) assert.ok(lines.includes(l), `строка разорвана: ${l}`);
  }
  assert.strictEqual(pages.join('\n').split('\n').length, lines.length, 'строки не должны теряться');
});

check('слишком длинная строка обрезается, а не роняет пост', () => {
  const [page] = paginate(['x'.repeat(50) + ' tail'], 20);
  assert.ok(page.length <= 20, page);
  assert.ok(page.endsWith('…'), page);
});

check('номер патча ходит в обе стороны', () => {
  // Игрок видит «26.18», данные Riot называют тот же патч «16.18».
  assert.strictEqual(displayPatch('16.18'), '26.18');
  assert.strictEqual(dataPatch('26.18'), '16.18');
  assert.strictEqual(dataPatch('16.18'), '16.18', 'номер в виде данных принимаем как есть');
  assert.strictEqual(dataPatch('чепуха'), null);
});

// ─── пояснения из патчноута ─────────────────────────────────────────────────

section('Пересказ патчноута');

const { _internal: pc } = require('../lib/patchContext');

check('строка с числом не проходит', () => {
  // Цифры у нас измеренные. Число из пересказа может быть верным, но отличить
  // верное от выдуманного игрок не сможет, а мы обещаем ему обратное.
  const kept = pc.keepSafeLines('- Aurora got a mid-scope update\n- Grievous Wounds now cuts healing by 40%', '26.18');
  assert.deepStrictEqual(kept, ['Aurora got a mid-scope update']);
});

check('номер патча числом не считается', () => {
  assert.deepStrictEqual(pc.keepSafeLines('- Patch 26.18 reworks the jungle', '26.18'), [
    'Patch 26.18 reworks the jungle'
  ]);
});

check('NONE означает «пояснений нет»', () => {
  assert.deepStrictEqual(pc.keepSafeLines('NONE', '26.18'), []);
});

check('больше трёх строк не берём', () => {
  const many = Array.from({ length: 8 }, (_, i) => `- change number ${'x'.repeat(i + 1)}`).join('\n');
  assert.strictEqual(pc.keepSafeLines(many, '26.18').length, 3);
});

check('маркеры списка снимаются', () => {
  assert.deepStrictEqual(pc.keepSafeLines('• the jungle works differently now', '26.18'), [
    'the jungle works differently now'
  ]);
});

check('ход мысли без маркера за ответ не принимается', () => {
  // Модель иногда не выдерживает форму: «All value tweaks… NONE». Без этого
  // правила такая строка ушла бы игроку как пояснение к патчу.
  const messy = 'All value tweaks, no functional change. Now check the item.NONE';
  assert.deepStrictEqual(pc.keepSafeLines(messy, '26.18'), []);
});

check('отдельные режимы в пояснения не попадают', () => {
  // Настоящие ответы по 26.16 и 26.18: в патчноуте режимам отводят много места,
  // и пересказ охотно брал оттуда — а сводка выше строго про Ущелье.
  const real = [
    '- League Classic mode debuts on the map, reviving old-version kits for champions.',
    "- Arena's Bravery mode returns as a standing weekly event.",
    '- The Support Role Quest no longer grants bonus progress for champion combat.'
  ].join('\n');
  assert.deepStrictEqual(pc.keepSafeLines(real, '26.16'), [
    'The Support Role Quest no longer grants bonus progress for champion combat.'
  ]);
});

check('слово внутри другого слова за режим не принимается', () => {
  assert.deepStrictEqual(pc.keepSafeLines('- Turf control around the drake pit works differently now.', '26.18'), [
    'Turf control around the drake pit works differently now.'
  ]);
});

check('косметика и разговор о самом патчноуте отсекаются', () => {
  // Обе строки — настоящие ответы модели по патчу 26.18.
  const real = [
    '- This update reworks how Classic Skin Chromas are sold by folding them into base skins.',
    '- No Mordekaiser or Lulu changes appear anywhere in these patch notes.',
    '- The support quest no longer grants bonus progress for champion combat.'
  ].join('\n');
  assert.deepStrictEqual(pc.keepSafeLines(real, '26.18'), [
    'The support quest no longer grants bonus progress for champion combat.'
  ]);
});

check('запрос читает патчноут Riot по прямому адресу', () => {
  const p = pc.buildPrompt('26.18', ['Ekko']);
  assert.ok(p.includes('leagueoflegends.com/en-us/news/game-updates/'), p.slice(0, 200));
  assert.ok(p.includes('patch-26-18-notes'), 'адрес должен собираться из номера патча');
  assert.ok(/NO NUMBERS/.test(p), 'запрет чисел должен быть в запросе');
});

// ─── деньги на API ──────────────────────────────────────────────────────────

section('Счёт API');

const anthropic = require('../lib/anthropic');

check('пустой счёт опознаётся по ответу API', () => {
  const real =
    '{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Claude API. Please go to Plans & Billing to upgrade or purchase credits."}}';
  assert.strictEqual(anthropic.isBillingProblem(400, real), true);
  assert.strictEqual(anthropic.isBillingProblem(402, 'payment required'), true);
});

check('обычная ошибка за отказ по деньгам не выдаётся', () => {
  assert.strictEqual(anthropic.isBillingProblem(400, '{"error":{"message":"max_tokens is too large"}}'), false);
  assert.strictEqual(anthropic.isBillingProblem(429, 'rate limit exceeded'), false);
  assert.strictEqual(anthropic.isBillingProblem(500, 'internal'), false);
});

console.log(`\n${passed} проверок пройдено${process.exitCode ? ', есть падения' : ''}`);
