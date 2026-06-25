"""
collect.py — сбор матч-статистики EUW и агрегация в data.db.

Запуск:
    python collect.py --key RGAPI-xxxx
    python collect.py --key RGAPI-xxxx --tier emerald --games 1000 --db data.db

Dev-ключ: 100 req / 2 мин — скрипт сам следит за лимитом.
"""

import argparse
import sqlite3
import time
import sys
from pathlib import Path

try:
    from riotwatcher import LolWatcher, ApiError
except ImportError:
    sys.exit('Установи зависимость: pip install riotwatcher')

# ---------- Регион ----------

REGION_LCU   = 'euw1'    # League-V4, Summoner-V4
REGION_MATCH = 'europe'  # MATCH-V5

# ---------- Бакеты эло ----------
# Для каждого бакета указываем пары (tier, division), из которых берём саммонеров.

TIER_BUCKETS: dict[str, list[tuple[str, str]]] = {
    'silver':  [(t, d) for t in ('SILVER', 'BRONZE', 'IRON')
                        for d in ('I', 'II', 'III', 'IV')],
    'gold':    [(t, d) for t in ('GOLD', 'PLATINUM')
                        for d in ('I', 'II', 'III', 'IV')],
    'emerald': [(t, d) for t in ('EMERALD', 'DIAMOND')
                        for d in ('I', 'II', 'III', 'IV')],
    'master':  [('MASTER', 'I')],
}

# teamPosition из MATCH-V5 → наш ключ роли
POSITION_MAP = {
    'TOP':     'top',
    'JUNGLE':  'jungle',
    'MIDDLE':  'mid',
    'BOTTOM':  'adc',
    'UTILITY': 'support',
}

QUEUE_RANKED = 420   # RANKED_SOLO_5x5

# Пауза между запросами: 1.3 с → ~46 req/min, безопасно при лимите 100 req/2 мин.
RATE_DELAY = 1.3


# ---------- База данных ----------

def init_db(path: str) -> sqlite3.Connection:
    con = sqlite3.connect(path)
    con.executescript("""
        PRAGMA journal_mode = WAL;

        CREATE TABLE IF NOT EXISTS processed_matches (
            match_id TEXT PRIMARY KEY
        );

        -- Базовый винрейт чемпиона на роли
        CREATE TABLE IF NOT EXISTS base_wr (
            champion_id  INTEGER,
            role         TEXT,
            tier_bucket  TEXT,
            patch        TEXT,
            games        INTEGER DEFAULT 0,
            wins         INTEGER DEFAULT 0,
            PRIMARY KEY (champion_id, role, tier_bucket, patch)
        );

        -- Противостояние на линии (один и тот же role у обоих)
        CREATE TABLE IF NOT EXISTS matchup (
            champion_id    INTEGER,
            role           TEXT,
            vs_champion_id INTEGER,
            tier_bucket    TEXT,
            patch          TEXT,
            games          INTEGER DEFAULT 0,
            wins           INTEGER DEFAULT 0,
            PRIMARY KEY (champion_id, role, vs_champion_id, tier_bucket, patch)
        );

        -- Синергия с союзником другой роли
        CREATE TABLE IF NOT EXISTS synergy (
            champion_id INTEGER,
            role        TEXT,
            ally_id     INTEGER,
            ally_role   TEXT,
            tier_bucket TEXT,
            patch       TEXT,
            games       INTEGER DEFAULT 0,
            wins        INTEGER DEFAULT 0,
            PRIMARY KEY (champion_id, role, ally_id, ally_role, tier_bucket, patch)
        );
    """)
    con.commit()
    return con


def is_processed(con: sqlite3.Connection, match_id: str) -> bool:
    return con.execute(
        'SELECT 1 FROM processed_matches WHERE match_id = ?', (match_id,)
    ).fetchone() is not None


def mark_processed(con: sqlite3.Connection, match_id: str):
    con.execute('INSERT OR IGNORE INTO processed_matches VALUES (?)', (match_id,))


def upsert(con: sqlite3.Connection, table: str, key_cols: list, key_vals: list, win: bool):
    """Универсальный upsert: games+1, wins+int(win)."""
    placeholders = ', '.join('?' for _ in key_cols)
    cols = ', '.join(key_cols)
    conflict = ', '.join(key_cols)
    con.execute(
        f"""INSERT INTO {table} ({cols}, games, wins)
            VALUES ({placeholders}, 1, ?)
            ON CONFLICT ({conflict})
            DO UPDATE SET games = games + 1, wins = wins + ?""",
        key_vals + [int(win), int(win)]
    )


# ---------- Обработка одного матча ----------

def patch_of(game_version: str) -> str:
    """'14.10.428.5571' → '14.10'"""
    parts = game_version.split('.')
    return f'{parts[0]}.{parts[1]}' if len(parts) >= 2 else game_version


def process_match(con: sqlite3.Connection, match: dict, tier_bucket: str):
    info  = match.get('info', {})
    patch = patch_of(info.get('gameVersion', '0.0'))

    if info.get('queueId') != QUEUE_RANKED:
        return  # пропускаем не-ранкед

    participants = info.get('participants', [])

    # Строим карту: teamId → {role → participant}
    teams: dict[int, dict[str, dict]] = {}
    for p in participants:
        pos = POSITION_MAP.get(p.get('teamPosition', ''))
        if not pos:
            continue
        teams.setdefault(p['teamId'], {})[pos] = p

    team_ids = list(teams.keys())
    if len(team_ids) != 2:
        return

    t1, t2 = team_ids[0], team_ids[1]

    for side_a, side_b in ((t1, t2), (t2, t1)):
        for role, p in teams[side_a].items():
            champ = p['championId']
            win   = p['win']

            # base WR
            upsert(con, 'base_wr',
                   ['champion_id', 'role', 'tier_bucket', 'patch'],
                   [champ, role, tier_bucket, patch], win)

            # matchup — против оппонента той же роли
            opp = teams[side_b].get(role)
            if opp:
                upsert(con, 'matchup',
                       ['champion_id', 'role', 'vs_champion_id', 'tier_bucket', 'patch'],
                       [champ, role, opp['championId'], tier_bucket, patch], win)

            # synergy — с каждым союзником другой роли
            for ally_role, ally in teams[side_a].items():
                if ally_role != role:
                    upsert(con, 'synergy',
                           ['champion_id', 'role', 'ally_id', 'ally_role', 'tier_bucket', 'patch'],
                           [champ, role, ally['championId'], ally_role, tier_bucket, patch], win)

    mark_processed(con, match['metadata']['matchId'])


# ---------- API-вызов с retry ----------

def api_call(fn, *args, **kwargs):
    while True:
        try:
            time.sleep(RATE_DELAY)
            return fn(*args, **kwargs)
        except ApiError as e:
            code = e.response.status_code
            if code == 429:
                wait = int(e.response.headers.get('Retry-After', 10))
                print(f'  [429] Rate limit — жду {wait}s…', flush=True)
                time.sleep(wait)
            elif code in (401, 403):
                sys.exit(f'\n[{code}] API-ключ недействителен или истёк. Сгенерируй новый dev-ключ на '
                         'developer.riotgames.com (он живёт 24 ч и аннулируется при регенерации) '
                         'и запусти снова — собранное уже сохранено.')
            elif code == 404:
                return None  # ресурс не найден — пропускаем
            else:
                print(f'  [ApiError {code}] {e}', flush=True)
                return None


# ---------- Основной сбор ----------

def collect(api_key: str, tier_bucket: str, target_games: int, db_path: str, days: int = 30, pages: int = 1):
    watcher = LolWatcher(api_key)
    con     = init_db(db_path)

    import datetime
    start_time = int((datetime.datetime.now(datetime.timezone.utc)
                      - datetime.timedelta(days=days)).timestamp())

    already = con.execute('SELECT COUNT(*) FROM processed_matches').fetchone()[0]
    print(f'Уже в базе: {already} матчей.')
    print(f'Цель: +{target_games} новых | бакет={tier_bucket} | регион={REGION_LCU}')
    print(f'Фильтр: последние {days} дней (с {datetime.datetime.fromtimestamp(start_time).strftime("%Y-%m-%d")})')
    print(f'Пауза между запросами: {RATE_DELAY}s (~{int(60/RATE_DELAY)} req/min)\n')

    # Набираем puuid из League-V4 (несколько страниц для большего охвата)
    puuids: list[str] = []
    seen:   set[str]  = set()
    puuid_cap = pages * len(TIER_BUCKETS[tier_bucket]) * 20

    for tier, division in TIER_BUCKETS[tier_bucket]:
        for page in range(1, pages + 1):
            if len(puuids) >= puuid_cap:
                break
            entries = api_call(
                watcher.league.entries,
                REGION_LCU, 'RANKED_SOLO_5x5', tier, division, page=page
            )
            if not entries:
                break  # страниц больше нет

            for entry in entries[:20]:
                puuid = entry.get('puuid')
                if not puuid:
                    summ = api_call(watcher.summoner.by_id, REGION_LCU, entry['summonerId'])
                    puuid = summ['puuid'] if summ else None
                if puuid and puuid not in seen:
                    puuids.append(puuid)
                    seen.add(puuid)

        print(f'  {tier}/{division}: {len(puuids)} puuid набрано', flush=True)

    print(f'\nВсего puuid: {len(puuids)}. Начинаю сбор матчей…\n')

    total = 0
    try:
        for i, puuid in enumerate(puuids, 1):
            if total >= target_games:
                break

            # Регулярная строка прогресса по игрокам — видно, что сбор идёт.
            print(f'  игрок {i}/{len(puuids)} · собрано {total}/{target_games}', flush=True)

            match_ids = api_call(
                watcher.match.matchlist_by_puuid,
                REGION_MATCH, puuid,
                queue=QUEUE_RANKED, count=30, start_time=start_time
            )
            if not match_ids:
                continue

            for mid in match_ids:
                if total >= target_games:
                    break
                if is_processed(con, mid):
                    continue

                match = api_call(watcher.match.by_id, REGION_MATCH, mid)
                if not match:
                    continue

                process_match(con, match, tier_bucket)
                con.commit()
                total += 1

                if total % 10 == 0:
                    print(f'    +{total}/{target_games} матчей…', flush=True)
    except KeyboardInterrupt:
        print('\nОстановка по Ctrl+C — сохраняю собранное…', flush=True)

    # Сбрасываем WAL в основной файл — иначе C# в ReadOnly-режиме не увидит данные.
    con.execute("PRAGMA wal_checkpoint(FULL)")
    con.close()
    print(f'\nГотово. Новых матчей за прогон: {total}. База: {db_path}')


# ---------- CLI ----------

if __name__ == '__main__':
    p = argparse.ArgumentParser(description='Counterplay — сбор матч-статистики')
    p.add_argument('--key',   required=True,
                   help='Riot API key (RGAPI-…)')
    p.add_argument('--tier',  default='emerald',
                   choices=list(TIER_BUCKETS),
                   help='Бакет эло (по умолчанию: emerald)')
    p.add_argument('--games', type=int, default=500,
                   help='Сколько новых матчей собрать (по умолчанию: 500)')
    p.add_argument('--db',    default='data.db',
                   help='Путь к SQLite-базе (по умолчанию: data.db)')
    p.add_argument('--days',  type=int, default=30,
                   help='Брать матчи только за последние N дней (по умолчанию: 30)')
    p.add_argument('--pages', type=int, default=3,
                   help='Сколько страниц игроков брать из каждого дивизиона (по умолчанию: 3 → больше игроков → больше матчей)')
    args = p.parse_args()

    collect(args.key, args.tier, args.games, args.db, args.days, args.pages)
