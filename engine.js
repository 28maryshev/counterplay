// engine.js — данные чемпионов и движок рекомендаций (ЗАГЛУШКА для теста).
// ВАЖНО: винрейты / контр-матчапы / синергия здесь СИНТЕТИЧЕСКИЕ —
// детерминированно сгенерированы, чтобы обкатать механику. Реальные данные
// придут из пайплайна MATCH-V5 (этап 3). Файл работает и в браузере, и в Node.

(function () {
  const ROLES = [
    { key: 'top',     name: 'Top'     },
    { key: 'jungle',  name: 'Jungle'  },
    { key: 'mid',     name: 'Mid'     },
    { key: 'adc',     name: 'ADC'     },
    { key: 'support', name: 'Support' },
  ];
  const ROLE_KEYS = ROLES.map((r) => r.key);

  // id — идентификатор из Data Dragon (для иконок). baseWR — базовый винрейт (заглушка).
  const CHAMPIONS = [
    // Top
    { id: 'Garen',       name: 'Гарен',       role: 'top',     baseWR: 51.2 },
    { id: 'Darius',      name: 'Дариус',      role: 'top',     baseWR: 50.4 },
    { id: 'Sett',        name: 'Сетт',        role: 'top',     baseWR: 50.8 },
    { id: 'Malphite',    name: 'Мальфит',     role: 'top',     baseWR: 51.6 },
    { id: 'Aatrox',      name: 'Аатрокс',     role: 'top',     baseWR: 49.3 },
    { id: 'Fiora',       name: 'Фиора',       role: 'top',     baseWR: 49.8 },
    { id: 'Camille',     name: 'Камилл',      role: 'top',     baseWR: 49.5 },
    { id: 'Nasus',       name: 'Насус',       role: 'top',     baseWR: 50.9 },
    { id: 'Renekton',    name: 'Ренектон',    role: 'top',     baseWR: 50.1 },
    { id: 'Irelia',      name: 'Ирелия',      role: 'top',     baseWR: 49.6 },
    { id: 'Jax',         name: 'Джакс',       role: 'top',     baseWR: 50.7 },
    { id: 'Teemo',       name: 'Тимо',        role: 'top',     baseWR: 50.3 },
    { id: 'Kennen',      name: 'Кеннен',      role: 'top',     baseWR: 50.0 },
    { id: 'Mordekaiser', name: 'Мордекайзер', role: 'top',     baseWR: 51.4 },
    { id: 'Illaoi',      name: 'Иллаой',      role: 'top',     baseWR: 51.1 },
    { id: 'Riven',       name: 'Ривен',       role: 'top',     baseWR: 49.2 },
    { id: 'Urgot',       name: 'Ургот',       role: 'top',     baseWR: 50.6 },
    { id: 'Ornn',        name: 'Орнн',        role: 'top',     baseWR: 50.9 },
    { id: 'Gragas',      name: 'Грагас',      role: 'top',     baseWR: 50.2 },
    // Jungle
    { id: 'LeeSin',      name: 'Ли Син',      role: 'jungle',  baseWR: 48.6 },
    { id: 'Sejuani',     name: 'Сеюани',      role: 'jungle',  baseWR: 51.4 },
    { id: 'Warwick',     name: 'Варвик',      role: 'jungle',  baseWR: 51.0 },
    { id: 'Kayn',        name: 'Кайн',        role: 'jungle',  baseWR: 50.2 },
    { id: 'Vi',          name: 'Вай',         role: 'jungle',  baseWR: 50.0 },
    { id: 'Viego',       name: 'Виего',       role: 'jungle',  baseWR: 49.7 },
    { id: 'Hecarim',     name: 'Хекарим',     role: 'jungle',  baseWR: 51.3 },
    { id: 'Graves',      name: 'Грейвс',      role: 'jungle',  baseWR: 50.5 },
    { id: 'Nidalee',     name: 'Нидали',      role: 'jungle',  baseWR: 48.9 },
    { id: 'Elise',       name: 'Элиза',       role: 'jungle',  baseWR: 49.4 },
    { id: 'Amumu',       name: 'Аму-Му',      role: 'jungle',  baseWR: 51.7 },
    { id: 'Shyvana',     name: 'Шайвана',     role: 'jungle',  baseWR: 50.6 },
    { id: 'MasterYi',    name: 'Мастер И',    role: 'jungle',  baseWR: 50.8 },
    { id: 'Nunu',        name: 'Нуну',        role: 'jungle',  baseWR: 51.1 },
    { id: 'RekSai',      name: 'Рек\'Сай',    role: 'jungle',  baseWR: 49.8 },
    { id: 'JarvanIV',    name: 'Джарван IV',  role: 'jungle',  baseWR: 50.3 },
    { id: 'Zac',         name: 'Зак',         role: 'jungle',  baseWR: 51.2 },
    { id: 'Nocturne',    name: 'Нoctюрн',     role: 'jungle',  baseWR: 50.4 },
    // Mid
    { id: 'Ahri',        name: 'Ари',         role: 'mid',     baseWR: 51.1 },
    { id: 'Yasuo',       name: 'Ясуо',        role: 'mid',     baseWR: 49.0 },
    { id: 'Yone',        name: 'Йоне',        role: 'mid',     baseWR: 49.3 },
    { id: 'Zed',         name: 'Зед',         role: 'mid',     baseWR: 49.4 },
    { id: 'Lux',         name: 'Люкс',        role: 'mid',     baseWR: 50.9 },
    { id: 'Syndra',      name: 'Синдра',      role: 'mid',     baseWR: 49.9 },
    { id: 'Viktor',      name: 'Виктор',      role: 'mid',     baseWR: 50.6 },
    { id: 'Ekko',        name: 'Экко',        role: 'mid',     baseWR: 50.3 },
    { id: 'Fizz',        name: 'Физз',        role: 'mid',     baseWR: 49.7 },
    { id: 'Orianna',     name: 'Орианна',     role: 'mid',     baseWR: 50.5 },
    { id: 'Akali',       name: 'Акали',       role: 'mid',     baseWR: 49.2 },
    { id: 'Katarina',    name: 'Катарина',    role: 'mid',     baseWR: 49.8 },
    { id: 'TwistedFate', name: 'Судьба',      role: 'mid',     baseWR: 50.1 },
    { id: 'Corki',       name: 'Корки',       role: 'mid',     baseWR: 50.4 },
    { id: 'Leblanc',     name: 'Лебланк',     role: 'mid',     baseWR: 49.5 },
    { id: 'Vex',         name: 'Векс',        role: 'mid',     baseWR: 51.0 },
    { id: 'Velkoz',      name: 'Вел\'Коз',    role: 'mid',     baseWR: 50.8 },
    { id: 'Malzahar',    name: 'Малзахар',    role: 'mid',     baseWR: 50.7 },
    // ADC
    { id: 'Jinx',        name: 'Джинкс',      role: 'adc',     baseWR: 51.3 },
    { id: 'Caitlyn',     name: 'Кейтлин',     role: 'adc',     baseWR: 49.6 },
    { id: 'Ezreal',      name: 'Эзреаль',     role: 'adc',     baseWR: 49.1 },
    { id: 'Kaisa',       name: 'Каиса',       role: 'adc',     baseWR: 50.5 },
    { id: 'Jhin',        name: 'Джин',        role: 'adc',     baseWR: 50.7 },
    { id: 'Ashe',        name: 'Эш',          role: 'adc',     baseWR: 50.1 },
    { id: 'Tristana',    name: 'Тристана',    role: 'adc',     baseWR: 50.3 },
    { id: 'Sivir',       name: 'Сивир',       role: 'adc',     baseWR: 50.9 },
    { id: 'Vayne',       name: 'Вейн',        role: 'adc',     baseWR: 49.4 },
    { id: 'MissFortune', name: 'Мисс Фортюн', role: 'adc',    baseWR: 51.1 },
    { id: 'Draven',      name: 'Дрейвен',     role: 'adc',     baseWR: 49.8 },
    { id: 'Samira',      name: 'Самира',      role: 'adc',     baseWR: 49.6 },
    { id: 'Xayah',       name: 'Зайя',        role: 'adc',     baseWR: 50.0 },
    { id: 'Lucian',      name: 'Люциан',      role: 'adc',     baseWR: 49.9 },
    { id: 'KogMaw',      name: 'Kog\'Maw',    role: 'adc',     baseWR: 51.5 },
    { id: 'Twitch',      name: 'Твич',        role: 'adc',     baseWR: 50.4 },
    // Support
    { id: 'Thresh',      name: 'Треш',        role: 'support', baseWR: 49.8 },
    { id: 'Lulu',        name: 'Лулу',        role: 'support', baseWR: 51.0 },
    { id: 'Leona',       name: 'Леона',       role: 'support', baseWR: 50.9 },
    { id: 'Nautilus',    name: 'Наутилус',    role: 'support', baseWR: 50.6 },
    { id: 'Bard',        name: 'Бард',        role: 'support', baseWR: 49.4 },
    { id: 'Soraka',      name: 'Сорака',      role: 'support', baseWR: 51.2 },
    { id: 'Pyke',        name: 'Пайк',        role: 'support', baseWR: 49.0 },
    { id: 'Blitzcrank',  name: 'Блицкранк',   role: 'support', baseWR: 50.4 },
    { id: 'Karma',       name: 'Карма',       role: 'support', baseWR: 50.7 },
    { id: 'Nami',        name: 'Нами',        role: 'support', baseWR: 50.5 },
    { id: 'Janna',       name: 'Джанна',      role: 'support', baseWR: 51.3 },
    { id: 'Morgana',     name: 'Моргана',     role: 'support', baseWR: 50.8 },
    { id: 'Alistar',     name: 'Алистар',     role: 'support', baseWR: 50.1 },
    { id: 'Zyra',        name: 'Зайра',       role: 'support', baseWR: 50.3 },
    { id: 'Sona',        name: 'Сона',        role: 'support', baseWR: 51.5 },
    { id: 'Seraphine',   name: 'Серафина',    role: 'support', baseWR: 50.6 },
    { id: 'Brand',       name: 'Брэнд',       role: 'support', baseWR: 50.2 },
    { id: 'Rakan',       name: 'Рейкан',      role: 'support', baseWR: 50.3 },
    { id: 'Rell',        name: 'Релль',       role: 'support', baseWR: 50.5 },
    { id: 'Renata',      name: 'Рената',      role: 'support', baseWR: 50.1 },
    { id: 'Taric',       name: 'Тарик',       role: 'support', baseWR: 50.8 },
    { id: 'Milio',       name: 'Милио',       role: 'support', baseWR: 51.1 },
    { id: 'Senna',       name: 'Сенна',       role: 'support', baseWR: 51.4 },
  ];

  const byId   = (id)   => CHAMPIONS.find((c) => c.id === id) || null;
  const byRole = (role) => CHAMPIONS.filter((c) => c.role === role);

  // ─── Теги чемпионов ─────────────────────────────────────────────────────────
  // Используются для семантического определения типа синергии.
  const TAGS = {
    // Top
    Garen:       ['juggernaut', 'tank'],
    Darius:      ['juggernaut', 'dive', 'hard_cc'],
    Sett:        ['juggernaut', 'engage', 'hard_cc'],
    Malphite:    ['engage', 'hard_cc', 'ult_malphite', 'tank'],
    Aatrox:      ['sustain', 'dive', 'hard_cc'],
    Fiora:       ['split', 'dive', 'mobility'],
    Camille:     ['dive', 'hard_cc', 'mobility'],
    Nasus:       ['juggernaut', 'scale', 'cc'],
    Renekton:    ['dive', 'hard_cc', 'aggressive'],
    Irelia:      ['dive', 'mobility', 'hard_cc', 'scale'],
    Jax:         ['scale', 'dive', 'hard_cc'],
    Teemo:       ['poke', 'zone_control', 'stealth'],
    Kennen:      ['engage', 'hard_cc', 'burst', 'zone_control'],
    Mordekaiser: ['dive', 'cc', 'zone_control', 'scale'],
    Illaoi:      ['zone_control', 'sustain', 'hard_cc'],
    Riven:       ['dive', 'burst', 'mobility', 'hard_cc'],
    Urgot:       ['burst', 'hard_cc', 'zone_control'],
    Ornn:        ['engage', 'hard_cc', 'tank', 'peel'],
    Gragas:      ['engage', 'hard_cc', 'cc', 'zone_control', 'dive'],
    // Jungle
    LeeSin:      ['dive', 'cc', 'hard_cc', 'mobility'],
    Sejuani:     ['engage', 'hard_cc', 'channels_ult', 'tank'],
    Warwick:     ['dive', 'hard_cc', 'sustain'],
    Kayn:        ['dive', 'burst', 'mobility', 'stealth'],
    Vi:          ['dive', 'hard_cc', 'engage'],
    Viego:       ['dive', 'burst', 'stealth'],
    Hecarim:     ['engage', 'dive', 'mobility', 'hard_cc'],
    Graves:      ['dive', 'burst', 'mobility'],
    Nidalee:     ['poke', 'mobility', 'burst'],
    Elise:       ['dive', 'cc', 'hard_cc', 'burst'],
    Amumu:       ['engage', 'hard_cc', 'channels_ult', 'tank'],
    Shyvana:     ['scale', 'dive', 'zone_control'],
    MasterYi:    ['dive', 'scale', 'hypercarry', 'mobility'],
    Nunu:        ['engage', 'peel', 'channels_ult', 'cc', 'hard_cc'],
    RekSai:      ['dive', 'cc', 'mobility'],
    JarvanIV:    ['engage', 'hard_cc', 'dive', 'ult_trap'],
    Zac:         ['engage', 'hard_cc', 'cc', 'tank', 'dive'],
    Nocturne:    ['dive', 'burst', 'stealth', 'hard_cc'],
    // Mid
    Ahri:        ['burst', 'mobility', 'cc'],
    Yasuo:       ['dive', 'burst', 'mobility', 'ult_airborne'],
    Yone:        ['dive', 'burst', 'mobility', 'ult_airborne'],
    Zed:         ['burst', 'dive', 'stealth', 'mobility'],
    Lux:         ['burst', 'cc', 'hard_cc', 'poke', 'shield'],
    Syndra:      ['burst', 'cc', 'hard_cc'],
    Viktor:      ['poke', 'burst', 'cc', 'scale'],
    Ekko:        ['dive', 'burst', 'mobility'],
    Fizz:        ['dive', 'burst', 'mobility', 'cc'],
    Orianna:     ['ult_orianna', 'poke', 'cc', 'hard_cc'],
    Akali:       ['dive', 'burst', 'stealth', 'mobility'],
    Katarina:    ['dive', 'burst', 'mobility', 'channels_ult'],
    TwistedFate: ['cc', 'hard_cc', 'burst', 'roam'],
    Corki:       ['poke', 'mobility', 'scale'],
    Leblanc:     ['burst', 'dive', 'mobility', 'cc'],
    Vex:         ['burst', 'cc', 'hard_cc', 'poke'],
    Velkoz:      ['poke', 'burst', 'channels_ult'],
    Malzahar:    ['burst', 'hard_cc', 'zone_control'],
    // ADC
    Jinx:        ['scale', 'hypercarry', 'poke', 'needs_peel'],
    Caitlyn:     ['trap', 'poke', 'scale'],
    Ezreal:      ['poke', 'mobility'],
    Kaisa:       ['scale', 'hypercarry', 'mobility', 'dive'],
    Jhin:        ['poke', 'cc'],
    Ashe:        ['poke', 'cc', 'hard_cc'],
    Tristana:    ['scale', 'aggressive', 'mobility', 'dive'],
    Sivir:       ['scale', 'utility'],
    Vayne:       ['scale', 'hypercarry', 'needs_peel', 'dive'],
    MissFortune: ['poke', 'burst', 'channels_ult', 'aggressive'],
    Draven:      ['aggressive', 'kill_lane', 'hypercarry'],
    Samira:      ['aggressive', 'dive', 'mobility'],
    Xayah:       ['scale', 'xayah_pair', 'cc'],
    Lucian:      ['aggressive', 'poke', 'mobility', 'lucian'],
    KogMaw:      ['scale', 'hypercarry', 'needs_peel', 'poke'],
    Twitch:      ['scale', 'hypercarry', 'stealth', 'needs_peel'],
    // Support
    Thresh:      ['hook', 'engage', 'hard_cc', 'peel', 'utility'],
    Lulu:        ['peel', 'shield', 'cc', 'hard_cc'],
    Leona:       ['engage', 'hard_cc', 'tank'],
    Nautilus:    ['hook', 'engage', 'hard_cc', 'tank'],
    Bard:        ['roam', 'cc', 'hard_cc', 'utility'],
    Soraka:      ['peel', 'heal', 'cc'],
    Pyke:        ['hook', 'cc', 'roam', 'burst'],
    Blitzcrank:  ['hook', 'engage', 'cc', 'hard_cc'],
    Karma:       ['peel', 'poke', 'shield', 'cc', 'mobility'],
    Nami:        ['peel', 'cc', 'hard_cc', 'heal', 'nami_e'],
    Janna:       ['peel', 'disengage', 'shield', 'hard_cc'],
    Morgana:     ['shield', 'cc', 'hard_cc', 'poke'],
    Alistar:     ['engage', 'hard_cc', 'peel', 'heal', 'tank'],
    Zyra:        ['poke', 'cc', 'zone_control'],
    Sona:        ['peel', 'heal', 'cc', 'hard_cc', 'shield'],
    Seraphine:   ['peel', 'cc', 'hard_cc', 'shield', 'heal', 'poke'],
    Brand:       ['poke', 'burst'],
    Rakan:       ['engage', 'cc', 'hard_cc', 'xayah_pair', 'peel', 'mobility'],
    Rell:        ['engage', 'hard_cc', 'tank'],
    Renata:      ['utility', 'cc', 'shield', 'peel'],
    Taric:       ['peel', 'heal', 'cc', 'hard_cc', 'ult_invuln'],
    Milio:       ['peel', 'heal', 'shield', 'cc'],
    Senna:       ['poke', 'peel', 'scale', 'cc', 'heal'],
  };

  function getTags(id) { return TAGS[id] || []; }
  function hasTag(id, tag) { return getTags(id).includes(tag); }
  function hasAnyTag(id, tags) { return tags.some((t) => getTags(id).includes(t)); }

  // ─── Семантическая синергия ──────────────────────────────────────────────────
  // allies: [{id, name, role}]
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

      // ── Ганк-синергия (джангл с дайвом) ────────────────
      if (aRole === 'jungle' && hasAnyTag(aId, ['dive', 'engage']) && myHas(['engage', 'hook', 'hard_cc'])) {
        labels.push({ icon: '🗡️', text: 'Ганк-синергия с ' + aName + ': СС держит, ' + aName + ' прыгает — лёгкое убийство' });
        continue;
      }

      // ── Ульт-синергия: кукан → Ясуо/Йоне ──────────────
      if (hasAnyTag(aId, ['ult_airborne']) && myHas(['engage', 'hard_cc'])) {
        labels.push({ icon: '🌪️', text: 'Ульт-синергия с ' + aName + ': любой кукан активирует его ульт' });
        continue;
      }

      // ── Ульт-синергия: CC → каналирующий ульт (МФ, Вел'Коз, Катарина) ──
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

      // ── Ульт-синергия: Тарик (неуязвимость) ───────────
      if (hasAnyTag(aId, ['ult_invuln']) && myHas(['dive', 'burst'])) {
        labels.push({ icon: '🛡️', text: 'Тарик даёт неуязвимость во время дайва — прыгаешь без риска умереть' });
        continue;
      }

      // ── Ловушка Джарвана ────────────────────────────────
      if (hasAnyTag(aId, ['ult_trap']) && myHas(['burst', 'dive', 'zone_control'])) {
        labels.push({ icon: '🏟️', text: 'Арена ' + aName + ': враги заперты, добиваешь в ограниченном пространстве' });
        continue;
      }

      // ── Защита гиперкэрри ───────────────────────────────
      if (hasAnyTag(aId, ['hypercarry', 'needs_peel']) && myHas(['peel', 'shield', 'heal'])) {
        labels.push({ icon: '🛡️', text: 'Защита гиперкэрри ' + aName + ': твоя задача — пилить и лечить' });
        continue;
      }

      // ── Агрессивный бот-лайн ────────────────────────────
      if (aRole === 'adc' && hasAnyTag(aId, ['aggressive', 'kill_lane']) && myHas(['engage', 'hook', 'hard_cc'])) {
        labels.push({ icon: '⚔️', text: 'Килл-лайн с ' + aName + ': ранний нажим + агрессивный кэрри' });
        continue;
      }

      // ── Пилинг для адк, который сам прыгает ─────────────
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

  // state: { myRole, ally:{role:id|null}, enemy:{role:id|null}, bans:[id] }
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

    // Союзники с ролями — нужны для семантической синергии
    const allyData = ROLE_KEYS.filter((r) => r !== myRole).map((r) => {
      const id = ally[r];
      if (!id) return null;
      const ac = byId(id);
      return { id, role: r, name: ac ? ac.name : id };
    }).filter(Boolean);
    const allyIds = allyData.map((a) => a.id);

    const candidates = byRole(myRole).filter((c) => !banned.has(c.id) && !taken.has(c.id));

    const scored = candidates.map((c) => {
      const base = c.baseWR - 50;
      const direct = directOppId ? counter(c.id, directOppId) : 0;

      const otherCounters = otherEnemyIds.map((e) => {
        const ec = byId(e);
        return { id: e, name: ec ? ec.name : e, val: counter(c.id, e) };
      });
      const other = avg(otherCounters.map((x) => x.val));

      const allySyns = allyData.map((a) => ({
        id: a.id, name: a.name, role: a.role, val: synergy(c.id, a.id),
      }));
      const syn = avg(allySyns.map((x) => x.val));

      // Семантические метки синергии влияют на скоринг: +0.5 per label
      const synLabels = detectSynergies(c.id, allyData);
      const synBonus  = synLabels.length * 0.5;

      const score = W.base * base + W.direct * direct + W.other * other + W.synergy * (syn + synBonus);
      return { id: c.id, name: c.name, baseWR: c.baseWR,
               parts: { base, direct, other, syn }, directOppId,
               otherCounters, allySyns, allyData, synLabels, score };
    });

    scored.sort((a, b) => b.score - a.score);
    return scored;
  }

  // ─── Объяснение ─────────────────────────────────────────────────────────────
  function explain(rec) {
    const lines = [];

    // Семантические метки синергии — на первом месте
    for (const lbl of rec.synLabels || []) {
      lines.push(lbl.icon + ' ' + lbl.text);
    }

    // Прямой оппонент
    if (rec.directOppId) {
      const opp     = byId(rec.directOppId);
      const oppName = opp ? opp.name : rec.directOppId;
      const d       = rec.parts.direct;
      if (d >= 2)        lines.push('Уверенно выигрывает линию против ' + oppName + ' (+' + d.toFixed(1) + '%) — можно давить с первых уровней.');
      else if (d >= 0.5) lines.push('Небольшое преимущество в матчапе против ' + oppName + ' (+' + d.toFixed(1) + '%).');
      else if (d <= -2)  lines.push('Сложный матчап против ' + oppName + ' (' + d.toFixed(1) + '%) — нужна осторожная игра, но состав это компенсирует.');
      else if (d < -0.5) lines.push('Небольшое давление со стороны ' + oppName + ' (' + d.toFixed(1) + '%) — не критично.');
      else               lines.push('Нейтральный матчап против ' + oppName + ' — исход линии зависит от игры.');
    }

    // Статистическая синергия (если нет семантических меток)
    if ((rec.synLabels || []).length === 0) {
      const goodSyn = [...(rec.allySyns || [])].sort((a, b) => b.val - a.val).filter((x) => x.val >= 1.5);
      if (goodSyn.length > 0) {
        const names = goodSyn.slice(0, 2).map((x) => x.name).join(' и ');
        lines.push('Сильная синергия с ' + names + ': вместе создают опасные комбинации.');
      } else if (rec.parts.syn >= 0.5 && (rec.allySyns || []).length > 0) {
        const best = [...(rec.allySyns || [])].sort((a, b) => b.val - a.val)[0];
        if (best && best.val > 0) lines.push('Хорошо работает с ' + best.name + ' — совместные действия приносят результат.');
      }
    }

    // Матчапы против остальных врагов
    const goodEnemy = [...(rec.otherCounters || [])].sort((a, b) => b.val - a.val).filter((x) => x.val >= 1.5);
    if (goodEnemy.length > 0) {
      const names = goodEnemy.slice(0, 2).map((x) => x.name).join(' и ');
      lines.push('Выгодные матчапы против ' + names + ' — хорошо вписывается в состав врагов.');
    } else if (rec.parts.other >= 0.5 && (rec.otherCounters || []).length > 0) {
      lines.push('Комфортно играет против текущего состава врагов.');
    }

    // Базовый WR
    if (rec.baseWR >= 51.5)      lines.push('Один из сильнейших в патче — базовый WR ' + rec.baseWR.toFixed(1) + '%.');
    else if (rec.baseWR >= 51.0) lines.push('Хороший базовый WR ' + rec.baseWR.toFixed(1) + '%.');
    else if (rec.baseWR < 49.5)  lines.push('Низкий базовый WR ' + rec.baseWR.toFixed(1) + '% — требует уверенного знания чемпиона.');

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
  };
  if (typeof window !== 'undefined') window.CP = CP;
  if (typeof module !== 'undefined' && module.exports) module.exports = CP;
})();
