// app.js — интерфейс тестового драфта. Вся логика — в engine.js (глобал CP).

const DDRAGON_FALLBACK_VERSION = '14.10.1'; // иконки старых чемпионов есть в любой версии
let ddVersion = DDRAGON_FALLBACK_VERSION;

const emptyTeam = () => ({ top: null, jungle: null, mid: null, adc: null, support: null });

const state = {
  myRole: 'support',
  ally: emptyTeam(),
  enemy: emptyTeam(),
  bans: [],          // массив id
  active: null,      // { side:'ally'|'enemy', role } — выбранный слот
  banMode: false,
  search: '',
};

const $ = (id) => document.getElementById(id);
const roleName = (key) => CP.ROLES.find((r) => r.key === key).name;
const iconUrl = (id) => `https://ddragon.leagueoflegends.com/cdn/${ddVersion}/img/champion/${id}.png`;

// Иконка чемпиона с запасным вариантом (инициалы), если картинка не загрузилась.
function champImg(id, extraClass = '') {
  const c = CP.byId(id);
  const initials = c ? c.name.slice(0, 2) : '??';
  return `<span class="champimg ${extraClass}">
      <img src="${iconUrl(id)}" alt="${c ? c.name : id}"
           onerror="this.style.display='none';this.nextElementSibling.style.display='flex';">
      <span class="fallback" style="display:none">${initials}</span>
    </span>`;
}

function isTaken(id) {
  for (const r of CP.ROLE_KEYS) {
    if (state.ally[r] === id || state.enemy[r] === id) return true;
  }
  return state.bans.includes(id);
}

// ---------- слоты команд ----------
function renderSlots(side) {
  const wrap = side === 'ally' ? $('allySlots') : $('enemySlots');
  wrap.innerHTML = '';
  for (const role of CP.ROLES) {
    const id = state[side][role.key];
    const isMine = side === 'ally' && role.key === state.myRole;
    const isActive = state.active && state.active.side === side && state.active.role === role.key;
    const c = id ? CP.byId(id) : null;

    // Иконки-подсказки: контры для врагов, синергии для союзников (не мой слот)
    let hintsHtml = '';
    if (id) {
      if (side === 'enemy') {
        const counters = CP.topCounters(id, role.key);
        if (counters.length > 0)
          hintsHtml = '<div class="slot-hints"><span class="hint-label">Контры:</span><span class="hint-icons">'
            + counters.map((x) => champImg(x.id, 'tiny')).join('') + '</span></div>';
      } else if (!isMine) {
        const syns = CP.topSynergies(id, state.myRole);
        if (syns.length > 0)
          hintsHtml = '<div class="slot-hints"><span class="hint-label">Синергия:</span><span class="hint-icons">'
            + syns.map((x) => champImg(x.id, 'tiny')).join('') + '</span></div>';
      }
    }

    const slot = document.createElement('div');
    slot.className = `slot ${side}` + (isMine ? ' mine' : '') + (isActive ? ' active' : '') + (id ? ' filled' : '');
    slot.innerHTML = `
      <span class="rolelbl">${role.name}${isMine ? ' • ВЫ' : ''}</span>
      <span class="slotmain">
        ${id ? champImg(id) : '<span class="champimg empty">+</span>'}
        <span class="cname">${c ? c.name : (isMine ? 'ваш пик' : '—')}</span>
      </span>
      ${hintsHtml}
      ${id ? '<button class="clear" title="Очистить">×</button>' : ''}
    `;
    slot.addEventListener('click', (e) => {
      if (e.target.classList.contains('clear')) {
        state[side][role.key] = null;
        render();
        return;
      }
      state.active = { side, role: role.key };
      render();
    });
    wrap.appendChild(slot);
  }
}

// ---------- центр: рекомендации ----------
function renderRecs() {
  const box = $('recs');
  box.innerHTML = '';

  const recs = CP.recommend(state);
  const opp = state.enemy[state.myRole] ? CP.byId(state.enemy[state.myRole]) : null;

  const head = document.createElement('div');
  head.className = 'recnote';
  head.innerHTML = `Роль: <b>${roleName(state.myRole)}</b> · прямой оппонент: <b>${opp ? opp.name : 'неизвестен'}</b>`;
  box.appendChild(head);

  if (recs.length === 0) {
    const e = document.createElement('div');
    e.className = 'recnote';
    e.textContent = 'Нет доступных кандидатов — все забанены или взяты.';
    box.appendChild(e);
    return;
  }

  recs.slice(0, 4).forEach((rec, i) => {
    const card = document.createElement('div');
    card.className = 'rec' + (i === 0 ? ' top' : '');
    card.innerHTML = `
      <div class="recrank">${i + 1}</div>
      ${champImg(rec.id)}
      <div class="recbody">
        <div class="recname">${rec.name}<span class="recscore">score ${rec.score.toFixed(1)}</span></div>
        <div class="recwhy">${CP.explain(rec).map((l) => '<span>' + l + '</span>').join('')}</div>
        <div class="recbars">
          ${bar('база', rec.parts.base)}
          ${bar('vs опп.', rec.parts.direct)}
          ${bar('vs врагов', rec.parts.other)}
          ${bar('синергия', rec.parts.syn)}
        </div>
      </div>
    `;
    card.title = 'Кликни, чтобы зафиксировать как свой пик';
    card.addEventListener('click', () => { state.ally[state.myRole] = rec.id; render(); });
    box.appendChild(card);
  });
}

// мини-бар вклада: центр = 0, вправо положительно, влево отрицательно
function bar(label, val) {
  const pct = Math.max(-6, Math.min(6, val)) / 6; // -1..1
  const w = Math.abs(pct) * 50;
  const side = pct >= 0 ? 'pos' : 'neg';
  return `<div class="bar"><span class="barlbl">${label}</span>
     <span class="bartrack"><span class="barfill ${side}" style="width:${w}%"></span></span>
     <span class="barval">${val >= 0 ? '+' : ''}${val.toFixed(1)}</span></div>`;
}

// ---------- сетка чемпионов ----------
function renderGrid() {
  const grid = $('grid');
  grid.innerHTML = '';
  const q = state.search.trim().toLowerCase();
  const list = CP.CHAMPIONS
    .filter((c) => !q || c.name.toLowerCase().includes(q) || c.id.toLowerCase().includes(q))
    .sort((a, b) => CP.ROLE_KEYS.indexOf(a.role) - CP.ROLE_KEYS.indexOf(b.role) || a.name.localeCompare(b.name));

  for (const c of list) {
    const cell = document.createElement('button');
    cell.className = 'gcell' + (isTaken(c.id) && !state.bans.includes(c.id) ? ' taken' : '') + (state.bans.includes(c.id) ? ' banned' : '');
    cell.title = `${c.name} (${roleName(c.role)})`;
    cell.innerHTML = champImg(c.id) + `<span class="gname">${c.name}</span>`;
    cell.addEventListener('click', () => onPick(c.id));
    grid.appendChild(cell);
  }
}

function onPick(id) {
  if (state.banMode) {
    const i = state.bans.indexOf(id);
    if (i >= 0) state.bans.splice(i, 1);
    else state.bans.push(id);
    render();
    return;
  }
  if (!state.active) { flashHint('Сначала выбери слот: слева — союзник, справа — враг.'); return; }
  if (isTaken(id)) { flashHint('Этот чемпион уже занят или забанен.'); return; }
  state[state.active.side][state.active.role] = id;
  advanceActive();
  render();
}

// перейти к следующему пустому слоту той же стороны
function advanceActive() {
  const side = state.active.side;
  const idx = CP.ROLE_KEYS.indexOf(state.active.role);
  for (let k = 1; k <= CP.ROLE_KEYS.length; k++) {
    const r = CP.ROLE_KEYS[(idx + k) % CP.ROLE_KEYS.length];
    if (!state[side][r]) { state.active = { side, role: r }; return; }
  }
  state.active = null;
}

// ---------- баны ----------
function renderBans() {
  const wrap = $('bans');
  wrap.innerHTML = '';
  if (state.bans.length === 0) {
    wrap.innerHTML = '<span class="nobans">нет</span>';
  } else {
    for (const id of state.bans) {
      const chip = document.createElement('button');
      chip.className = 'banchip';
      chip.innerHTML = champImg(id, 'small') + '<span class="x">×</span>';
      chip.title = 'Снять бан';
      chip.addEventListener('click', () => { state.bans = state.bans.filter((b) => b !== id); render(); });
      wrap.appendChild(chip);
    }
  }
}

// ---------- подсказка над сеткой ----------
let hintTimer = null;
function flashHint(msg) {
  const el = $('activeInfo');
  el.textContent = msg;
  el.classList.add('warn');
  clearTimeout(hintTimer);
  hintTimer = setTimeout(() => { el.classList.remove('warn'); updateActiveInfo(); }, 1800);
}
function updateActiveInfo() {
  const el = $('activeInfo');
  if (el.classList.contains('warn')) return;
  if (state.banMode) { el.textContent = '— режим банов'; return; }
  el.textContent = state.active
    ? `— слот: ${state.active.side === 'ally' ? 'союзник' : 'враг'} ${roleName(state.active.role)}`
    : '';
}

// ---------- общий рендер ----------
function render() {
  renderSlots('ally');
  renderSlots('enemy');
  renderRecs();
  renderBans();
  renderGrid();
  updateActiveInfo();
  const bt = $('banToggle');
  bt.textContent = 'Бан: ' + (state.banMode ? 'вкл' : 'выкл');
  bt.classList.toggle('on', state.banMode);
}

// ---------- актуальная версия Data Dragon (иконки и так грузятся по fallback) ----------
async function fetchVersion() {
  try {
    const r = await fetch('https://ddragon.leagueoflegends.com/api/versions.json');
    const v = await r.json();
    if (Array.isArray(v) && v[0]) { ddVersion = v[0]; render(); }
  } catch (_) {
    /* оффлайн / CORS — остаёмся на fallback; иконки <img> кросс-доменно работают */
  }
}

// ---------- инициализация ----------
function init() {
  const sel = $('myRole');
  CP.ROLES.forEach((r) => {
    const o = document.createElement('option');
    o.value = r.key;
    o.textContent = r.name;
    if (r.key === state.myRole) o.selected = true;
    sel.appendChild(o);
  });
  sel.addEventListener('change', () => { state.myRole = sel.value; render(); });

  $('banToggle').addEventListener('click', () => { state.banMode = !state.banMode; render(); });
  $('reset').addEventListener('click', () => {
    state.ally = emptyTeam();
    state.enemy = emptyTeam();
    state.bans = [];
    state.active = null;
    state.banMode = false;
    render();
  });
  $('search').addEventListener('input', (e) => { state.search = e.target.value; renderGrid(); });

  render();
  fetchVersion();
}

document.addEventListener('DOMContentLoaded', init);
