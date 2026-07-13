// Справочник чемпионов: id ↔ имя ↔ идентификатор Data Dragon (для иконок).
// Основной источник — Data Dragon (en_US); фолбэк без сети — champion_map.json
// (копия pipeline/champion_map.json: id → DDragon-идентификатор).
const logger = require('./logger');

let ddVersion = null; // null = иконки недоступны (офлайн-фолбэк)
const byId = new Map(); // id:number -> {id, dd, name}
const index = new Map(); // norm(name|dd|alias) -> entry

// Алиасы сообщества → DDragon-идентификатор (норм-форма).
const ALIASES = {
  mf: 'MissFortune',
  asol: 'AurelionSol',
  j4: 'JarvanIV',
  tf: 'TwistedFate',
  yi: 'MasterYi',
  kass: 'Kassadin',
  cait: 'Caitlyn',
  naut: 'Nautilus',
  sera: 'Seraphine',
  mundo: 'DrMundo',
  ori: 'Orianna',
  ww: 'Warwick',
  wukong: 'MonkeyKing',
  kaisa: 'Kaisa',
  ksante: 'KSante',
  gp: 'Gangplank',
  vlad: 'Vladimir',
  malz: 'Malzahar',
  morde: 'Mordekaiser',
  kata: 'Katarina',
  lb: 'Leblanc',
  tk: 'TahmKench',
  nunu: 'Nunu',
  renata: 'Renata',
  blitz: 'Blitzcrank',
  heca: 'Hecarim',
  velkoz: 'Velkoz',
  reksai: 'RekSai',
  kogmaw: 'KogMaw'
};

const norm = (s) => String(s).toLowerCase().replace(/[^a-z0-9]/g, '');

function register(entry, ...keys) {
  for (const k of keys) {
    const n = norm(k);
    if (n && !index.has(n)) index.set(n, entry);
  }
}

function buildIndex(entries) {
  byId.clear();
  index.clear();
  for (const e of entries) {
    byId.set(e.id, e);
    register(e, e.name, e.dd);
  }
  for (const [alias, dd] of Object.entries(ALIASES)) {
    const e = index.get(norm(dd));
    if (e) register(e, alias);
  }
}

async function load() {
  try {
    const versions = await (await fetch('https://ddragon.leagueoflegends.com/api/versions.json')).json();
    ddVersion = versions[0];
    const data = await (
      await fetch(`https://ddragon.leagueoflegends.com/cdn/${ddVersion}/data/en_US/champion.json`)
    ).json();
    const entries = Object.values(data.data).map((c) => ({
      id: parseInt(c.key, 10),
      dd: c.id,
      name: c.name
    }));
    buildIndex(entries);
    logger.info(`champions: loaded ${entries.length} from Data Dragon ${ddVersion}`);
  } catch (e) {
    // Офлайн-фолбэк: имена = DDragon-идентификаторы, иконки недоступны.
    const map = require('./champion_map.json');
    const entries = Object.entries(map).map(([id, dd]) => ({
      id: parseInt(id, 10),
      dd,
      // 'MissFortune' -> 'Miss Fortune' для читаемости
      name: dd.replace(/([a-z])([A-Z])/g, '$1 $2')
    }));
    buildIndex(entries);
    ddVersion = null;
    logger.warn(`champions: Data Dragon unavailable (${e.message}) — using bundled map, no icons`);
  }
}

function name(id) {
  return byId.get(id)?.name ?? `#${id}`;
}

function iconUrl(id) {
  const e = byId.get(id);
  if (!e || !ddVersion) return null;
  return `https://ddragon.leagueoflegends.com/cdn/${ddVersion}/img/champion/${e.dd}.png`;
}

/** Разрешить пользовательский ввод в чемпиона (или null). */
function resolve(input) {
  return index.get(norm(input)) ?? null;
}

/** Найти чемпионов в свободном тексте (Lab Verifier): токены + би/триграммы,
 *  чтобы «Vi» матчился только отдельным словом, а «Miss Fortune» — парой. */
function findInText(text) {
  const tokens = String(text)
    .split(/[^a-zA-Z0-9']+/)
    .filter(Boolean);
  const found = [];
  const seen = new Set();
  const tryAdd = (s) => {
    const e = index.get(norm(s));
    if (e && !seen.has(e.id)) {
      seen.add(e.id);
      found.push(e);
      return true;
    }
    return false;
  };
  for (let i = 0; i < tokens.length; i++) {
    // Сначала длинные n-граммы, чтобы «Aurelion Sol» не съелся по кускам.
    if (i + 2 < tokens.length && tryAdd(tokens[i] + tokens[i + 1] + tokens[i + 2])) {
      i += 2;
      continue;
    }
    if (i + 1 < tokens.length && tryAdd(tokens[i] + tokens[i + 1])) {
      i += 1;
      continue;
    }
    tryAdd(tokens[i]);
  }
  return found;
}

/** Список имён для автокомплита. */
function allNames() {
  return [...byId.values()].map((e) => e.name).sort();
}

module.exports = { load, name, iconUrl, resolve, findInText, allNames, norm };
