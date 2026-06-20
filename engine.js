// engine.js — данные чемпионов и движок рекомендаций (ЗАГЛУШКА для теста).
// Винрейты / контр-матчапы / синергия СИНТЕТИЧЕСКИЕ — детерминированно из id.
// Реальные данные придут из пайплайна MATCH-V5. Работает в браузере и Node.

(function () {
  const ROLES = [
    { key: 'top',     name: 'Top'     },
    { key: 'jungle',  name: 'Jungle'  },
    { key: 'mid',     name: 'Mid'     },
    { key: 'adc',     name: 'ADC'     },
    { key: 'support', name: 'Support' },
  ];
  const ROLE_KEYS = ROLES.map((r) => r.key);

  const K = 50; // Лаплас-сглаживание (совпадает с RecommendationEngine.cs)
  let _data = null; // реальные данные из window.CP_DATA (pipeline/data.js)

  function loadRealData(d) { _data = d; }
  function smoothed(s) {
    if (!s || !s.g) return null; // нет данных → вернём null → упадём на синтетику
    return (s.w + K / 2) / (s.g + K);
  }

  const CHAMPIONS = [
    // ── Top ──────────────────────────────────────────────────────────────────
    { id: 'Aatrox',      name: 'Аатрокс',       role: 'top',     baseWR: 49.3 },
    { id: 'Ambessa',     name: 'Амбесса',        role: 'top',     baseWR: 50.0 },
    { id: 'Camille',     name: 'Камилл',         role: 'top',     baseWR: 49.5 },
    { id: 'Chogath',     name: 'Чо\'Гат',        role: 'top',     baseWR: 50.5 },
    { id: 'Darius',      name: 'Дариус',         role: 'top',     baseWR: 50.4 },
    { id: 'DrMundo',     name: 'Доктор Мундо',   role: 'top',     baseWR: 50.8 },
    { id: 'Fiora',       name: 'Фиора',          role: 'top',     baseWR: 49.8 },
    { id: 'Gangplank',   name: 'Гангпланк',      role: 'top',     baseWR: 49.6 },
    { id: 'Garen',       name: 'Гарен',          role: 'top',     baseWR: 51.2 },
    { id: 'Gnar',        name: 'Гнар',           role: 'top',     baseWR: 49.9 },
    { id: 'Gragas',      name: 'Грагас',         role: 'top',     baseWR: 50.2 },
    { id: 'Gwen',        name: 'Гвен',           role: 'top',     baseWR: 50.3 },
    { id: 'Illaoi',      name: 'Иллаой',         role: 'top',     baseWR: 51.1 },
    { id: 'Irelia',      name: 'Ирелия',         role: 'top',     baseWR: 49.6 },
    { id: 'Jax',         name: 'Джакс',          role: 'top',     baseWR: 50.7 },
    { id: 'Jayce',       name: 'Джейс',          role: 'top',     baseWR: 49.4 },
    { id: 'Kayle',       name: 'Кейль',          role: 'top',     baseWR: 50.1 },
    { id: 'Kennen',      name: 'Кеннен',         role: 'top',     baseWR: 50.0 },
    { id: 'Kled',        name: 'Клед',           role: 'top',     baseWR: 50.2 },
    { id: 'KSante',      name: 'К\'Санте',       role: 'top',     baseWR: 49.8 },
    { id: 'Malphite',    name: 'Мальфит',        role: 'top',     baseWR: 51.6 },
    { id: 'Mordekaiser', name: 'Мордекайзер',    role: 'top',     baseWR: 51.4 },
    { id: 'Nasus',       name: 'Насус',          role: 'top',     baseWR: 50.9 },
    { id: 'Olaf',        name: 'Олаф',           role: 'top',     baseWR: 50.1 },
    { id: 'Ornn',        name: 'Орнн',           role: 'top',     baseWR: 50.9 },
    { id: 'Quinn',       name: 'Квин',           role: 'top',     baseWR: 49.7 },
    { id: 'Renekton',    name: 'Ренектон',       role: 'top',     baseWR: 50.1 },
    { id: 'Riven',       name: 'Ривен',          role: 'top',     baseWR: 49.2 },
    { id: 'Rumble',      name: 'Рамбл',          role: 'top',     baseWR: 50.4 },
    { id: 'Sett',        name: 'Сетт',           role: 'top',     baseWR: 50.8 },
    { id: 'Shen',        name: 'Шен',            role: 'top',     baseWR: 50.6 },
    { id: 'Singed',      name: 'Синджед',        role: 'top',     baseWR: 50.3 },
    { id: 'Sion',        name: 'Сион',           role: 'top',     baseWR: 50.5 },
    { id: 'Teemo',       name: 'Тимо',           role: 'top',     baseWR: 50.3 },
    { id: 'Trundle',     name: 'Трандл',         role: 'top',     baseWR: 50.6 },
    { id: 'Tryndamere',  name: 'Трандамер',      role: 'top',     baseWR: 49.9 },
    { id: 'Urgot',       name: 'Ургот',          role: 'top',     baseWR: 50.6 },
    { id: 'Yorick',      name: 'Йорик',          role: 'top',     baseWR: 50.7 },
    // ── Jungle ───────────────────────────────────────────────────────────────
    { id: 'Amumu',       name: 'Аму-Му',         role: 'jungle',  baseWR: 51.7 },
    { id: 'BelVeth',     name: 'Бэл\'Вет',       role: 'jungle',  baseWR: 50.0 },
    { id: 'Briar',       name: 'Бриар',          role: 'jungle',  baseWR: 50.0 },
    { id: 'Diana',       name: 'Диана',          role: 'jungle',  baseWR: 50.4 },
    { id: 'Elise',       name: 'Элиза',          role: 'jungle',  baseWR: 49.4 },
    { id: 'Evelynn',     name: 'Эвелинн',        role: 'jungle',  baseWR: 49.8 },
    { id: 'Fiddlesticks',name: 'Фиддлстикс',     role: 'jungle',  baseWR: 51.0 },
    { id: 'Graves',      name: 'Грейвс',         role: 'jungle',  baseWR: 50.5 },
    { id: 'Hecarim',     name: 'Хекарим',        role: 'jungle',  baseWR: 51.3 },
    { id: 'Ivern',       name: 'Айверн',         role: 'jungle',  baseWR: 50.2 },
    { id: 'JarvanIV',    name: 'Джарван IV',     role: 'jungle',  baseWR: 50.3 },
    { id: 'Karthus',     name: 'Картус',         role: 'jungle',  baseWR: 50.6 },
    { id: 'Kayn',        name: 'Кайн',           role: 'jungle',  baseWR: 50.2 },
    { id: 'KhaZix',      name: 'Ка\'Зикс',       role: 'jungle',  baseWR: 50.1 },
    { id: 'Kindred',     name: 'Сородичи',       role: 'jungle',  baseWR: 49.9 },
    { id: 'LeeSin',      name: 'Ли Син',         role: 'jungle',  baseWR: 48.6 },
    { id: 'Lillia',      name: 'Лиллия',         role: 'jungle',  baseWR: 50.3 },
    { id: 'MasterYi',    name: 'Мастер И',       role: 'jungle',  baseWR: 50.8 },
    { id: 'Nidalee',     name: 'Нидали',         role: 'jungle',  baseWR: 48.9 },
    { id: 'Nocturne',    name: 'Ноктюрн',        role: 'jungle',  baseWR: 50.4 },
    { id: 'Nunu',        name: 'Нуну',           role: 'jungle',  baseWR: 51.1 },
    { id: 'Rammus',      name: 'Рамус',          role: 'jungle',  baseWR: 51.0 },
    { id: 'RekSai',      name: 'Рек\'Сай',       role: 'jungle',  baseWR: 49.8 },
    { id: 'Rengar',      name: 'Ренгар',         role: 'jungle',  baseWR: 49.5 },
    { id: 'Sejuani',     name: 'Сеюани',         role: 'jungle',  baseWR: 51.4 },
    { id: 'Shaco',       name: 'Шако',           role: 'jungle',  baseWR: 49.3 },
    { id: 'Shyvana',     name: 'Шайвана',        role: 'jungle',  baseWR: 50.6 },
    { id: 'Skarner',     name: 'Скарнер',        role: 'jungle',  baseWR: 50.2 },
    { id: 'Udyr',        name: 'Удыр',           role: 'jungle',  baseWR: 50.7 },
    { id: 'Vi',          name: 'Вай',            role: 'jungle',  baseWR: 50.0 },
    { id: 'Viego',       name: 'Виего',          role: 'jungle',  baseWR: 49.7 },
    { id: 'Volibear',    name: 'Волибир',        role: 'jungle',  baseWR: 50.5 },
    { id: 'Warwick',     name: 'Варвик',         role: 'jungle',  baseWR: 51.0 },
    { id: 'Wukong',      name: 'Укун',           role: 'jungle',  baseWR: 50.6 },
    { id: 'XinZhao',     name: 'Синь Чжао',      role: 'jungle',  baseWR: 50.4 },
    { id: 'Zac',         name: 'Зак',            role: 'jungle',  baseWR: 51.2 },
    // ── Mid ──────────────────────────────────────────────────────────────────
    { id: 'Ahri',        name: 'Ари',            role: 'mid',     baseWR: 51.1 },
    { id: 'Akali',       name: 'Акали',          role: 'mid',     baseWR: 49.2 },
    { id: 'Akshan',      name: 'Аксан',          role: 'mid',     baseWR: 50.0 },
    { id: 'Anivia',      name: 'Анивия',         role: 'mid',     baseWR: 50.5 },
    { id: 'Annie',       name: 'Энни',           role: 'mid',     baseWR: 50.8 },
    { id: 'AurelionSol', name: 'Аурелион Сол',   role: 'mid',     baseWR: 51.2 },
    { id: 'Aurora',      name: 'Аврора',         role: 'mid',     baseWR: 50.0 },
    { id: 'Azir',        name: 'Азир',           role: 'mid',     baseWR: 49.5 },
    { id: 'Cassiopeia',  name: 'Кассиопея',      role: 'mid',     baseWR: 50.4 },
    { id: 'Corki',       name: 'Корки',          role: 'mid',     baseWR: 50.4 },
    { id: 'Ekko',        name: 'Экко',           role: 'mid',     baseWR: 50.3 },
    { id: 'Fizz',        name: 'Физз',           role: 'mid',     baseWR: 49.7 },
    { id: 'Galio',       name: 'Галио',          role: 'mid',     baseWR: 50.6 },
    { id: 'Heimerdinger',name: 'Хеймердингер',   role: 'mid',     baseWR: 50.1 },
    { id: 'Hwei',        name: 'Хвей',           role: 'mid',     baseWR: 50.3 },
    { id: 'Kassadin',    name: 'Кассадин',       role: 'mid',     baseWR: 50.2 },
    { id: 'Katarina',    name: 'Катарина',       role: 'mid',     baseWR: 49.8 },
    { id: 'Leblanc',     name: 'Лебланк',        role: 'mid',     baseWR: 49.5 },
    { id: 'Lissandra',   name: 'Лиссандра',      role: 'mid',     baseWR: 50.7 },
    { id: 'Lux',         name: 'Люкс',           role: 'mid',     baseWR: 50.9 },
    { id: 'Malzahar',    name: 'Малзахар',       role: 'mid',     baseWR: 50.7 },
    { id: 'Naafiri',     name: 'Нафири',         role: 'mid',     baseWR: 50.0 },
    { id: 'Neeko',       name: 'Нико',           role: 'mid',     baseWR: 50.2 },
    { id: 'Orianna',     name: 'Орианна',        role: 'mid',     baseWR: 50.5 },
    { id: 'Qiyana',      name: 'Кияна',          role: 'mid',     baseWR: 49.6 },
    { id: 'Ryze',        name: 'Рэйз',           role: 'mid',     baseWR: 49.8 },
    { id: 'Swain',       name: 'Суэйн',          role: 'mid',     baseWR: 50.5 },
    { id: 'Sylas',       name: 'Сайлас',         role: 'mid',     baseWR: 49.9 },
    { id: 'Syndra',      name: 'Синдра',         role: 'mid',     baseWR: 49.9 },
    { id: 'Taliyah',     name: 'Талия',          role: 'mid',     baseWR: 50.4 },
    { id: 'Talon',       name: 'Талон',          role: 'mid',     baseWR: 50.0 },
    { id: 'TwistedFate', name: 'Судьба',         role: 'mid',     baseWR: 50.1 },
    { id: 'Veigar',      name: 'Вейгар',         role: 'mid',     baseWR: 51.0 },
    { id: 'Velkoz',      name: 'Вел\'Коз',       role: 'mid',     baseWR: 50.8 },
    { id: 'Vex',         name: 'Векс',           role: 'mid',     baseWR: 51.0 },
    { id: 'Viktor',      name: 'Виктор',         role: 'mid',     baseWR: 50.6 },
    { id: 'Vladimir',    name: 'Владимир',       role: 'mid',     baseWR: 50.3 },
    { id: 'Xerath',      name: 'Ксерат',         role: 'mid',     baseWR: 50.6 },
    { id: 'Yasuo',       name: 'Ясуо',           role: 'mid',     baseWR: 49.0 },
    { id: 'Yone',        name: 'Йоне',           role: 'mid',     baseWR: 49.3 },
    { id: 'Zed',         name: 'Зед',            role: 'mid',     baseWR: 49.4 },
    { id: 'Ziggs',       name: 'Зиггс',          role: 'mid',     baseWR: 50.3 },
    { id: 'Zoe',         name: 'Зо',             role: 'mid',     baseWR: 50.1 },
    // ── ADC ──────────────────────────────────────────────────────────────────
    { id: 'Aphelios',    name: 'Афелиос',        role: 'adc',     baseWR: 50.2 },
    { id: 'Ashe',        name: 'Эш',             role: 'adc',     baseWR: 50.1 },
    { id: 'Caitlyn',     name: 'Кейтлин',        role: 'adc',     baseWR: 49.6 },
    { id: 'Draven',      name: 'Дрейвен',        role: 'adc',     baseWR: 49.8 },
    { id: 'Ezreal',      name: 'Эзреаль',        role: 'adc',     baseWR: 49.1 },
    { id: 'Jhin',        name: 'Джин',           role: 'adc',     baseWR: 50.7 },
    { id: 'Jinx',        name: 'Джинкс',         role: 'adc',     baseWR: 51.3 },
    { id: 'Kaisa',       name: 'Каиса',          role: 'adc',     baseWR: 50.5 },
    { id: 'Kalista',     name: 'Калиста',        role: 'adc',     baseWR: 49.7 },
    { id: 'KogMaw',      name: 'Kog\'Maw',       role: 'adc',     baseWR: 51.5 },
    { id: 'Lucian',      name: 'Люциан',         role: 'adc',     baseWR: 49.9 },
    { id: 'MissFortune', name: 'Мисс Фортюн',   role: 'adc',     baseWR: 51.1 },
    { id: 'Nilah',       name: 'Нила',           role: 'adc',     baseWR: 50.0 },
    { id: 'Samira',      name: 'Самира',         role: 'adc',     baseWR: 49.6 },
    { id: 'Sivir',       name: 'Сивир',          role: 'adc',     baseWR: 50.9 },
    { id: 'Smolder',     name: 'Смолдер',        role: 'adc',     baseWR: 50.0 },
    { id: 'Tristana',    name: 'Тристана',       role: 'adc',     baseWR: 50.3 },
    { id: 'Twitch',      name: 'Твич',           role: 'adc',     baseWR: 50.4 },
    { id: 'Varus',       name: 'Варус',          role: 'adc',     baseWR: 50.2 },
    { id: 'Vayne',       name: 'Вейн',           role: 'adc',     baseWR: 49.4 },
    { id: 'Xayah',       name: 'Зайя',           role: 'adc',     baseWR: 50.0 },
    { id: 'Zeri',        name: 'Зери',           role: 'adc',     baseWR: 50.1 },
    // ── Support ──────────────────────────────────────────────────────────────
    { id: 'Alistar',     name: 'Алистар',        role: 'support', baseWR: 50.1 },
    { id: 'Bard',        name: 'Бард',           role: 'support', baseWR: 49.4 },
    { id: 'Blitzcrank',  name: 'Блицкранк',      role: 'support', baseWR: 50.4 },
    { id: 'Brand',       name: 'Брэнд',          role: 'support', baseWR: 50.2 },
    { id: 'Braum',       name: 'Браум',          role: 'support', baseWR: 50.7 },
    { id: 'Janna',       name: 'Джанна',         role: 'support', baseWR: 51.3 },
    { id: 'Karma',       name: 'Карма',          role: 'support', baseWR: 50.7 },
    { id: 'Leona',       name: 'Леона',          role: 'support', baseWR: 50.9 },
    { id: 'Lulu',        name: 'Лулу',           role: 'support', baseWR: 51.0 },
    { id: 'Maokai',      name: 'Маокай',         role: 'support', baseWR: 50.8 },
    { id: 'Milio',       name: 'Милио',          role: 'support', baseWR: 51.1 },
    { id: 'Morgana',     name: 'Моргана',        role: 'support', baseWR: 50.8 },
    { id: 'Nami',        name: 'Нами',           role: 'support', baseWR: 50.5 },
    { id: 'Nautilus',    name: 'Наутилус',       role: 'support', baseWR: 50.6 },
    { id: 'Pantheon',    name: 'Пантеон',        role: 'support', baseWR: 50.0 },
    { id: 'Poppy',       name: 'Поппи',          role: 'support', baseWR: 50.5 },
    { id: 'Pyke',        name: 'Пайк',           role: 'support', baseWR: 49.0 },
    { id: 'Rakan',       name: 'Рейкан',         role: 'support', baseWR: 50.3 },
    { id: 'Rell',        name: 'Релль',          role: 'support', baseWR: 50.5 },
    { id: 'Renata',      name: 'Рената',         role: 'support', baseWR: 50.1 },
    { id: 'Senna',       name: 'Сенна',          role: 'support', baseWR: 51.4 },
    { id: 'Seraphine',   name: 'Серафина',       role: 'support', baseWR: 50.6 },
    { id: 'Sona',        name: 'Сона',           role: 'support', baseWR: 51.5 },
    { id: 'Soraka',      name: 'Сорака',         role: 'support', baseWR: 51.2 },
    { id: 'TahmKench',   name: 'Тахм Кенч',      role: 'support', baseWR: 50.9 },
    { id: 'Taric',       name: 'Тарик',          role: 'support', baseWR: 50.8 },
    { id: 'Thresh',      name: 'Треш',           role: 'support', baseWR: 49.8 },
    { id: 'Yuumi',       name: 'Юуми',           role: 'support', baseWR: 50.4 },
    { id: 'Zilean',      name: 'Зилеан',         role: 'support', baseWR: 51.0 },
    { id: 'Zyra',        name: 'Зайра',          role: 'support', baseWR: 50.3 },
  ];

  const byId   = (id)   => CHAMPIONS.find((c) => c.id === id) || null;
  const byRole = (role) => CHAMPIONS.filter((c) => c.role === role);

  // ─── Теги чемпионов ─────────────────────────────────────────────────────────
  const TAGS = {
    // Top
    Aatrox:       ['sustain', 'dive', 'hard_cc'],
    Ambessa:      ['dive', 'burst', 'mobility', 'aggressive'],
    Camille:      ['dive', 'hard_cc', 'mobility'],
    Chogath:      ['tank', 'hard_cc', 'dive', 'scale'],
    Darius:       ['juggernaut', 'dive', 'hard_cc'],
    DrMundo:      ['tank', 'sustain', 'scale', 'juggernaut'],
    Fiora:        ['split', 'dive', 'mobility'],
    Gangplank:    ['poke', 'scale', 'zone_control'],
    Garen:        ['juggernaut', 'tank'],
    Gnar:         ['engage', 'hard_cc', 'poke', 'scale'],
    Gragas:       ['engage', 'hard_cc', 'cc', 'zone_control', 'dive'],
    Gwen:         ['dive', 'sustain', 'scale', 'mobility'],
    Illaoi:       ['zone_control', 'sustain', 'hard_cc'],
    Irelia:       ['dive', 'mobility', 'hard_cc', 'scale'],
    Jax:          ['scale', 'dive', 'hard_cc'],
    Jayce:        ['poke', 'cc', 'burst', 'mobility'],
    Kayle:        ['scale', 'hypercarry', 'ult_invuln', 'needs_peel'],
    Kennen:       ['engage', 'hard_cc', 'burst', 'zone_control'],
    Kled:         ['aggressive', 'dive', 'hard_cc', 'engage'],
    KSante:       ['tank', 'engage', 'hard_cc', 'mobility', 'dive'],
    Malphite:     ['engage', 'hard_cc', 'ult_malphite', 'tank'],
    Mordekaiser:  ['dive', 'cc', 'zone_control', 'scale'],
    Nasus:        ['juggernaut', 'scale', 'cc'],
    Olaf:         ['dive', 'aggressive', 'sustain', 'juggernaut'],
    Ornn:         ['engage', 'hard_cc', 'tank', 'peel'],
    Quinn:        ['poke', 'aggressive', 'mobility'],
    Renekton:     ['dive', 'hard_cc', 'aggressive'],
    Riven:        ['dive', 'burst', 'mobility', 'hard_cc'],
    Rumble:       ['zone_control', 'burst', 'cc'],
    Sett:         ['juggernaut', 'engage', 'hard_cc'],
    Shen:         ['engage', 'hard_cc', 'peel', 'tank', 'utility'],
    Singed:       ['cc', 'tank', 'sustain', 'zone_control'],
    Sion:         ['engage', 'hard_cc', 'tank', 'juggernaut'],
    Teemo:        ['poke', 'zone_control', 'stealth'],
    Trundle:      ['dive', 'cc', 'sustain', 'anti_tank'],
    Tryndamere:   ['dive', 'hypercarry', 'scale', 'mobility'],
    Urgot:        ['burst', 'hard_cc', 'zone_control'],
    Yorick:       ['zone_control', 'scale', 'split'],
    // Jungle
    Amumu:        ['engage', 'hard_cc', 'channels_ult', 'tank'],
    BelVeth:      ['dive', 'burst', 'mobility', 'scale', 'hypercarry'],
    Briar:        ['dive', 'burst', 'aggressive', 'hard_cc', 'channels_ult'],
    Diana:        ['dive', 'burst', 'cc', 'hard_cc', 'engage'],
    Elise:        ['dive', 'cc', 'hard_cc', 'burst'],
    Evelynn:      ['dive', 'burst', 'stealth', 'mobility'],
    Fiddlesticks: ['channels_ult', 'cc', 'hard_cc', 'poke'],
    Graves:       ['dive', 'burst', 'mobility'],
    Hecarim:      ['engage', 'dive', 'mobility', 'hard_cc'],
    Ivern:        ['peel', 'shield', 'cc', 'utility'],
    JarvanIV:     ['engage', 'hard_cc', 'dive', 'ult_trap'],
    Karthus:      ['poke', 'burst', 'channels_ult', 'scale'],
    Kayn:         ['dive', 'burst', 'mobility'],
    KhaZix:       ['dive', 'burst', 'stealth', 'mobility'],
    Kindred:      ['cc', 'utility', 'scale', 'ult_invuln'],
    LeeSin:       ['dive', 'cc', 'hard_cc', 'mobility'],
    Lillia:       ['cc', 'hard_cc', 'mobility', 'poke'],
    MasterYi:     ['dive', 'scale', 'hypercarry', 'mobility'],
    Nidalee:      ['poke', 'mobility', 'burst'],
    Nocturne:     ['dive', 'burst', 'stealth', 'hard_cc'],
    Nunu:         ['engage', 'peel', 'channels_ult', 'cc', 'hard_cc'],
    Rammus:       ['engage', 'hard_cc', 'tank', 'cc'],
    RekSai:       ['dive', 'cc', 'mobility'],
    Rengar:       ['dive', 'burst', 'stealth'],
    Sejuani:      ['engage', 'hard_cc', 'channels_ult', 'tank'],
    Shaco:        ['burst', 'stealth', 'cc', 'mobility'],
    Shyvana:      ['scale', 'dive', 'zone_control'],
    Skarner:      ['engage', 'hard_cc', 'dive', 'tank'],
    Udyr:         ['engage', 'hard_cc', 'tank', 'juggernaut', 'dive'],
    Vi:           ['dive', 'hard_cc', 'engage'],
    Viego:        ['dive', 'burst', 'stealth'],
    Volibear:     ['dive', 'engage', 'hard_cc', 'tank'],
    Warwick:      ['dive', 'hard_cc', 'sustain'],
    Wukong:       ['engage', 'hard_cc', 'dive', 'burst'],
    XinZhao:      ['dive', 'cc', 'hard_cc', 'engage', 'aggressive'],
    Zac:          ['engage', 'hard_cc', 'cc', 'tank', 'dive'],
    // Mid
    Ahri:         ['burst', 'mobility', 'cc'],
    Akali:        ['dive', 'burst', 'stealth', 'mobility'],
    Akshan:       ['poke', 'mobility', 'cc', 'utility'],
    Anivia:       ['cc', 'hard_cc', 'zone_control', 'poke'],
    Annie:        ['burst', 'hard_cc', 'engage', 'cc'],
    AurelionSol:  ['burst', 'cc', 'hard_cc', 'scale', 'poke'],
    Aurora:       ['burst', 'mobility', 'cc', 'dive'],
    Azir:         ['poke', 'zone_control', 'hard_cc', 'scale'],
    Cassiopeia:   ['poke', 'cc', 'hard_cc', 'scale'],
    Corki:        ['poke', 'mobility', 'scale'],
    Ekko:         ['dive', 'burst', 'mobility'],
    Fizz:         ['dive', 'burst', 'mobility', 'cc'],
    Galio:        ['engage', 'hard_cc', 'tank', 'cc', 'peel'],
    Heimerdinger: ['poke', 'zone_control', 'cc'],
    Hwei:         ['poke', 'burst', 'cc', 'zone_control'],
    Kassadin:     ['dive', 'burst', 'mobility', 'cc'],
    Katarina:     ['dive', 'burst', 'mobility', 'channels_ult'],
    Leblanc:      ['burst', 'dive', 'mobility', 'cc'],
    Lissandra:    ['burst', 'cc', 'hard_cc', 'engage', 'zone_control'],
    Lux:          ['burst', 'cc', 'hard_cc', 'poke', 'shield'],
    Malzahar:     ['burst', 'hard_cc', 'zone_control'],
    Naafiri:      ['dive', 'burst', 'mobility', 'aggressive'],
    Neeko:        ['burst', 'cc', 'hard_cc', 'engage', 'zone_control'],
    Orianna:      ['ult_orianna', 'poke', 'cc', 'hard_cc'],
    Qiyana:       ['burst', 'dive', 'mobility', 'cc', 'hard_cc'],
    Ryze:         ['poke', 'burst', 'scale', 'cc'],
    Swain:        ['poke', 'cc', 'sustain', 'scale', 'zone_control'],
    Sylas:        ['dive', 'burst', 'mobility', 'cc', 'hard_cc'],
    Syndra:       ['burst', 'cc', 'hard_cc'],
    Taliyah:      ['poke', 'zone_control', 'cc', 'hard_cc'],
    Talon:        ['dive', 'burst', 'stealth', 'mobility', 'roam'],
    TwistedFate:  ['cc', 'hard_cc', 'burst', 'roam'],
    Veigar:       ['burst', 'cc', 'hard_cc', 'scale'],
    Velkoz:       ['poke', 'burst', 'channels_ult'],
    Vex:          ['burst', 'cc', 'hard_cc', 'poke'],
    Viktor:       ['poke', 'burst', 'cc', 'scale'],
    Vladimir:     ['poke', 'sustain', 'scale', 'dive'],
    Xerath:       ['poke', 'burst', 'cc', 'hard_cc'],
    Yasuo:        ['dive', 'burst', 'mobility', 'ult_airborne'],
    Yone:         ['dive', 'burst', 'mobility', 'ult_airborne'],
    Zed:          ['burst', 'dive', 'stealth', 'mobility'],
    Ziggs:        ['poke', 'burst', 'zone_control', 'cc'],
    Zoe:          ['burst', 'poke', 'cc', 'hard_cc', 'mobility'],
    // ADC
    Aphelios:     ['scale', 'hypercarry', 'needs_peel', 'poke'],
    Ashe:         ['poke', 'cc', 'hard_cc'],
    Caitlyn:      ['trap', 'poke', 'scale'],
    Draven:       ['aggressive', 'kill_lane', 'hypercarry'],
    Ezreal:       ['poke', 'mobility'],
    Jhin:         ['poke', 'cc'],
    Jinx:         ['scale', 'hypercarry', 'poke', 'needs_peel'],
    Kaisa:        ['scale', 'hypercarry', 'mobility', 'dive'],
    Kalista:      ['scale', 'aggressive', 'hypercarry', 'utility'],
    KogMaw:       ['scale', 'hypercarry', 'needs_peel', 'poke'],
    Lucian:       ['aggressive', 'poke', 'mobility', 'lucian'],
    MissFortune:  ['poke', 'burst', 'channels_ult', 'aggressive'],
    Nilah:        ['aggressive', 'dive', 'scale', 'mobility'],
    Samira:       ['aggressive', 'dive', 'mobility'],
    Sivir:        ['scale', 'utility'],
    Smolder:      ['scale', 'hypercarry', 'poke', 'needs_peel'],
    Tristana:     ['scale', 'aggressive', 'mobility', 'dive'],
    Twitch:       ['scale', 'hypercarry', 'stealth', 'needs_peel'],
    Varus:        ['poke', 'cc', 'hard_cc', 'scale'],
    Vayne:        ['scale', 'hypercarry', 'needs_peel', 'dive'],
    Xayah:        ['scale', 'xayah_pair', 'cc'],
    Zeri:         ['scale', 'hypercarry', 'mobility', 'aggressive'],
    // Support
    Alistar:      ['engage', 'hard_cc', 'peel', 'heal', 'tank'],
    Bard:         ['roam', 'cc', 'hard_cc', 'utility'],
    Blitzcrank:   ['hook', 'engage', 'cc', 'hard_cc'],
    Brand:        ['poke', 'burst'],
    Braum:        ['peel', 'engage', 'hard_cc', 'shield'],
    Janna:        ['peel', 'disengage', 'shield', 'hard_cc'],
    Karma:        ['peel', 'poke', 'shield', 'cc', 'mobility'],
    Leona:        ['engage', 'hard_cc', 'tank'],
    Lulu:         ['peel', 'shield', 'cc', 'hard_cc'],
    Maokai:       ['engage', 'hard_cc', 'tank', 'peel', 'cc'],
    Milio:        ['peel', 'heal', 'shield', 'cc'],
    Morgana:      ['shield', 'cc', 'hard_cc', 'poke'],
    Nami:         ['peel', 'cc', 'hard_cc', 'heal', 'nami_e'],
    Nautilus:     ['hook', 'engage', 'hard_cc', 'tank'],
    Pantheon:     ['engage', 'hard_cc', 'dive', 'burst', 'cc'],
    Poppy:        ['engage', 'hard_cc', 'peel', 'tank', 'disengage'],
    Pyke:         ['hook', 'cc', 'roam', 'burst'],
    Rakan:        ['engage', 'cc', 'hard_cc', 'xayah_pair', 'peel', 'mobility'],
    Rell:         ['engage', 'hard_cc', 'tank'],
    Renata:       ['utility', 'cc', 'shield', 'peel'],
    Senna:        ['poke', 'peel', 'scale', 'cc', 'heal'],
    Seraphine:    ['peel', 'cc', 'hard_cc', 'shield', 'heal', 'poke'],
    Sona:         ['peel', 'heal', 'cc', 'hard_cc', 'shield'],
    Soraka:       ['peel', 'heal', 'cc'],
    TahmKench:    ['peel', 'tank', 'hard_cc', 'sustain', 'shield'],
    Taric:        ['peel', 'heal', 'cc', 'hard_cc', 'ult_invuln'],
    Thresh:       ['hook', 'engage', 'hard_cc', 'peel', 'utility'],
    Yuumi:        ['peel', 'heal', 'shield', 'cc'],
    Zilean:       ['utility', 'cc', 'hard_cc', 'peel', 'ult_invuln'],
    Zyra:         ['poke', 'cc', 'zone_control'],
  };

  function getTags(id) { return TAGS[id] || []; }
  function hasTag(id, tag) { return getTags(id).includes(tag); }
  function hasAnyTag(id, tags) { return tags.some((t) => getTags(id).includes(t)); }

  // ─── Семантическая синергия ──────────────────────────────────────────────────
  function detectSynergies(champId, allies) {
    const labels = [];
    const myTags = getTags(champId);
    const myHas  = (tags) => tags.some((t) => myTags.includes(t));

    for (const ally of allies) {
      const aId   = ally.id;
      const aName = ally.name;
      const aRole = ally.role;

      // ── Эксклюзивные дуэты ─────────────────────────────
      if ((champId === 'Rakan' && aId === 'Xayah') || (champId === 'Xayah' && aId === 'Rakan')) {
        labels.push({ icon: '💎', text: 'Ксая–Рейкан: эксклюзивный дуэт бот-лайна — усиленные способности' });
        continue;
      }
      if (champId === 'Nami' && aId === 'Lucian') {
        labels.push({ icon: '⚡', text: 'Нами Е усиляет авто-атаки Люциана — один из лучших бот-дуэтов' });
        continue;
      }

      // ── Ловушка + хук (Кейтлин) ────────────────────────
      if (aId === 'Caitlyn' && myHas(['hook', 'engage'])) {
        labels.push({ icon: '🎯', text: 'Ловушка Кейтлин + хук — гарантированный хедшот и килл' });
        continue;
      }

      // ── Ганк-синергия ───────────────────────────────────
      if (aRole === 'jungle' && hasAnyTag(aId, ['dive', 'engage']) && myHas(['engage', 'hook', 'hard_cc'])) {
        labels.push({ icon: '🗡️', text: 'Ганк-синергия с ' + aName + ': СС держит, ' + aName + ' прыгает — лёгкое убийство' });
        continue;
      }

      // ── Ульт-синергия: Ясуо/Йоне ───────────────────────
      if (hasAnyTag(aId, ['ult_airborne']) && myHas(['engage', 'hard_cc'])) {
        labels.push({ icon: '🌪️', text: 'Ульт-синергия с ' + aName + ': любой кукан активирует его ульт' });
        continue;
      }

      // ── Ульт-синергия: каналирующий ульт ───────────────
      if (hasAnyTag(aId, ['channels_ult']) && myHas(['hard_cc', 'engage'])) {
        labels.push({ icon: '🎶', text: 'Ульт-синергия с ' + aName + ': СС фиксирует врагов под ультом' });
        continue;
      }

      // ── Ульт-синергия: Орианна ─────────────────────────
      if (hasAnyTag(aId, ['ult_orianna']) && myHas(['engage', 'hard_cc'])) {
        labels.push({ icon: '⚙️', text: 'Ульт-синергия с Орианной: инициация собирает врагов под мяч-ульт' });
        continue;
      }

      // ── Ульт-синергия: Мальфит ─────────────────────────
      if (hasAnyTag(aId, ['ult_malphite']) && myHas(['burst', 'dive', 'ult_airborne'])) {
        labels.push({ icon: '🌊', text: 'Комбо с Мальфитом: ульт собирает врагов, добиваешь пока они в воздухе' });
        continue;
      }

      // ── Тарик/Зилеан/Кайнд (неуязвимость) ─────────────
      if (hasAnyTag(aId, ['ult_invuln']) && myHas(['dive', 'burst', 'hypercarry'])) {
        labels.push({ icon: '🛡️', text: aName + ' даёт неуязвимость — прыгаешь/атакуешь без риска умереть' });
        continue;
      }

      // ── Ловушка Джарвана ────────────────────────────────
      if (hasAnyTag(aId, ['ult_trap']) && myHas(['burst', 'dive', 'zone_control'])) {
        labels.push({ icon: '🏟️', text: 'Арена ' + aName + ': враги заперты, добиваешь в ограниченном пространстве' });
        continue;
      }

      // ── Защита гиперкэрри ───────────────────────────────
      if (hasAnyTag(aId, ['hypercarry', 'needs_peel']) && myHas(['peel', 'shield', 'heal'])) {
        labels.push({ icon: '🛡️', text: 'Защита гиперкэрри ' + aName + ': щит и пилинг в приоритете' });
        continue;
      }

      // ── Агрессивный бот-лайн ────────────────────────────
      if (aRole === 'adc' && hasAnyTag(aId, ['aggressive', 'kill_lane']) && myHas(['engage', 'hook', 'hard_cc'])) {
        labels.push({ icon: '⚔️', text: 'Килл-лайн с ' + aName + ': ранний нажим + агрессивный кэрри' });
        continue;
      }

      // ── Пилинг для мобильного адк ───────────────────────
      if (aRole === 'adc' && hasAnyTag(aId, ['dive', 'mobility']) && myHas(['peel', 'shield'])) {
        labels.push({ icon: '🔰', text: 'Прикрытие для ' + aName + ': пока он прыгает, ты защищаешь от фокуса' });
        continue;
      }
    }

    return labels;
  }

  // ─── FNV-1a хэш для синтетических данных ────────────────────────────────────
  function hash(s) {
    let h = 2166136261 >>> 0;
    for (let i = 0; i < s.length; i++) {
      h ^= s.charCodeAt(i);
      h = Math.imul(h, 16777619) >>> 0;
    }
    return h >>> 0;
  }

  function counter(aId, bId) {
    if (!aId || !bId || aId === bId) return 0;
    const lo  = aId < bId ? aId : bId;
    const hi  = aId < bId ? bId : aId;
    const raw = (hash(lo + '#' + hi) % 11) - 5;
    return aId === lo ? raw : -raw;
  }

  function synergy(aId, bId) {
    if (!aId || !bId || aId === bId) return 0;
    const lo = aId < bId ? aId : bId;
    const hi = aId < bId ? bId : aId;
    return (hash(lo + '&' + hi) % 8) - 2;
  }

  const W = { base: 1.0, direct: 2.5, other: 0.8, synergy: 1.2 };
  const avg = (arr) => (arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : 0);

  function recommend(state) {
    const { myRole, ally, enemy } = state;
    const banned = new Set(state.bans || []);
    const taken  = new Set();
    for (const r of ROLE_KEYS) {
      if (ally[r])  taken.add(ally[r]);
      if (enemy[r]) taken.add(enemy[r]);
    }

    const directOppId   = enemy[myRole] || null;
    const otherEnemyIds = ROLE_KEYS.filter((r) => r !== myRole).map((r) => enemy[r]).filter(Boolean);

    const allyData = ROLE_KEYS.filter((r) => r !== myRole).map((r) => {
      const id = ally[r];
      if (!id) return null;
      const ac = byId(id);
      return { id, role: r, name: ac ? ac.name : id };
    }).filter(Boolean);

    const candidates = byRole(myRole).filter((c) => !banned.has(c.id) && !taken.has(c.id));

    const scored = candidates.map((c) => {
      // Base WR — реальные данные если есть, иначе синтетика
      const bwStat  = _data?.base_wr[c.id]?.[myRole];
      const bwReal  = smoothed(bwStat);
      const base    = bwReal !== null ? (bwReal - 0.5) * 100 : c.baseWR - 50;
      const displayWR = bwReal !== null ? bwReal * 100 : c.baseWR;

      // Матчап с прямым оппонентом
      let direct = 0;
      if (directOppId) {
        const mStat = _data?.matchup[c.id + '/' + directOppId]?.[myRole];
        const mReal = smoothed(mStat);
        direct = mReal !== null ? (mReal - 0.5) * 100 : counter(c.id, directOppId);
      }

      // Матчапы против прочих врагов
      const otherCounters = otherEnemyIds.map((e) => {
        const ec    = byId(e);
        const mStat = _data?.matchup[c.id + '/' + e]?.[myRole];
        const mReal = smoothed(mStat);
        const val   = mReal !== null ? (mReal - 0.5) * 100 : counter(c.id, e);
        return { id: e, name: ec ? ec.name : e, val };
      });
      const other = avg(otherCounters.map((x) => x.val));

      // Синергия с союзниками
      const allySyns = allyData.map((a) => {
        const sStat = _data?.synergy[c.id + '/' + a.id]?.[myRole + '/' + a.role];
        const sReal = smoothed(sStat);
        const val   = sReal !== null ? (sReal - 0.5) * 100 : synergy(c.id, a.id);
        return { id: a.id, name: a.name, role: a.role, val };
      });
      const syn = avg(allySyns.map((x) => x.val));

      const synLabels = detectSynergies(c.id, allyData);
      const synBonus  = synLabels.length * 0.5;

      const score = W.base * base + W.direct * direct + W.other * other + W.synergy * (syn + synBonus);
      return { id: c.id, name: c.name, baseWR: displayWR,
               parts: { base, direct, other, syn }, directOppId,
               otherCounters, allySyns, allyData, synLabels, score };
    });

    scored.sort((a, b) => b.score - a.score);
    return scored;
  }

  function explain(rec) {
    const lines = [];

    for (const lbl of rec.synLabels || []) {
      lines.push(lbl.icon + ' ' + lbl.text);
    }

    if (rec.directOppId) {
      const opp     = byId(rec.directOppId);
      const oppName = opp ? opp.name : rec.directOppId;
      const d       = rec.parts.direct;
      if (d >= 2)        lines.push('Уверенно выигрывает линию против ' + oppName + ' (+' + d.toFixed(1) + '%).');
      else if (d >= 0.5) lines.push('Небольшое преимущество в матчапе против ' + oppName + ' (+' + d.toFixed(1) + '%).');
      else if (d <= -2)  lines.push('Сложный матчап против ' + oppName + ' (' + d.toFixed(1) + '%) — состав компенсирует.');
      else if (d < -0.5) lines.push('Небольшое давление со стороны ' + oppName + ' (' + d.toFixed(1) + '%) — не критично.');
      else               lines.push('Нейтральный матчап против ' + oppName + '.');
    }

    if ((rec.synLabels || []).length === 0) {
      const goodSyn = [...(rec.allySyns || [])].sort((a, b) => b.val - a.val).filter((x) => x.val >= 1.5);
      if (goodSyn.length > 0) {
        const names = goodSyn.slice(0, 2).map((x) => x.name).join(' и ');
        lines.push('Сильная синергия с ' + names + ': вместе создают опасные комбинации.');
      } else if (rec.parts.syn >= 0.5 && (rec.allySyns || []).length > 0) {
        const best = [...(rec.allySyns || [])].sort((a, b) => b.val - a.val)[0];
        if (best && best.val > 0) lines.push('Хорошо работает с ' + best.name + '.');
      }
    }

    const goodEnemy = [...(rec.otherCounters || [])].sort((a, b) => b.val - a.val).filter((x) => x.val >= 1.5);
    if (goodEnemy.length > 0) {
      const names = goodEnemy.slice(0, 2).map((x) => x.name).join(' и ');
      lines.push('Выгодные матчапы против ' + names + '.');
    } else if (rec.parts.other >= 0.5 && (rec.otherCounters || []).length > 0) {
      lines.push('Комфортно играет против текущего состава врагов.');
    }

    if (rec.baseWR >= 51.5)      lines.push('Один из сильнейших в патче — WR ' + rec.baseWR.toFixed(1) + '%.');
    else if (rec.baseWR >= 51.0) lines.push('Хороший базовый WR ' + rec.baseWR.toFixed(1) + '%.');
    else if (rec.baseWR < 49.5)  lines.push('Низкий WR ' + rec.baseWR.toFixed(1) + '% — требует уверенного знания.');

    if (lines.length === 0) lines.push('Нейтральный пик, база ' + rec.baseWR.toFixed(1) + '%.');
    return lines;
  }

  function topCounters(enemyId, role, exclude) {
    const excl = new Set(exclude || []);
    return byRole(role)
      .filter((c) => c.id !== enemyId && !excl.has(c.id))
      .map((c) => ({ id: c.id, name: c.name, val: counter(c.id, enemyId) }))
      .sort((a, b) => b.val - a.val)
      .slice(0, 3);
  }

  function topSynergies(allyId, myRole, exclude) {
    const excl = new Set(exclude || []);
    return byRole(myRole)
      .filter((c) => c.id !== allyId && !excl.has(c.id))
      .map((c) => ({ id: c.id, name: c.name, val: synergy(allyId, c.id) }))
      .sort((a, b) => b.val - a.val)
      .slice(0, 3);
  }

  const CP = {
    ROLES, ROLE_KEYS, CHAMPIONS, TAGS,
    byId, byRole, getTags, hasTag, hasAnyTag,
    counter, synergy, recommend, explain,
    topCounters, topSynergies, detectSynergies, W,
    loadRealData,
  };
  if (typeof window !== 'undefined') window.CP = CP;
  if (typeof module !== 'undefined' && module.exports) module.exports = CP;
})();
