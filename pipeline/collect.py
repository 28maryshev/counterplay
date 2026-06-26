"""
collect.py — автономный сбор матч-статистики и агрегация в data.db.

Собирает без перерыва: добирает игроков, пока есть; когда бакет вычерпан
(игроки кончились или просела доля новых матчей) — переключается на следующий
бакет, а когда все бакеты пройдены — на следующий регион. Перед каждым
переключением печатает общее число матчей в базе. Ctrl+C — мягкая остановка
с сохранением и подсказкой команды публикации базы на сервер.

Запуск:
    python collect.py --key RGAPI-xxxx
    python collect.py --key RGAPI-xxxx --tier emerald,gold,master --region euw1,na1,kr
    python collect.py --key RGAPI-xxxx --tier all --region all --db pipeline\\data.db

Матчи и игроки дедуплицируются глобально — повторный запуск только дополняет базу.
Dev-ключ: 100 req / 2 мин — скрипт сам следит за лимитом.
"""

import argparse
import sqlite3
import time
import sys
from pathlib import Path

import requests  # зависимость riotwatcher — для перехвата сетевых обрывов

try:
    from riotwatcher import LolWatcher, ApiError
except ImportError:
    sys.exit('Установи зависимость: pip install riotwatcher')

# ---------- Регион ----------

REGION_LCU   = 'euw1'    # League-V4, Summoner-V4 (платформенный роутинг) — дефолт
REGION_MATCH = 'europe'  # MATCH-V5 (региональный роутинг) — дефолт

# Платформа (League-V4/Summoner-V4) → регион (MATCH-V5).
# Имя слева — то, что подаётся в --region.
PLATFORM_TO_REGIONAL = {
    'euw1': 'europe', 'eun1': 'europe', 'tr1': 'europe', 'ru': 'europe',
    'na1': 'americas', 'br1': 'americas', 'la1': 'americas', 'la2': 'americas',
    'kr': 'asia', 'jp1': 'asia',
    'oc1': 'sea', 'ph2': 'sea', 'sg2': 'sea', 'th2': 'sea', 'tw2': 'sea', 'vn2': 'sea',
}

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

# Пауза между запросами. Лимит dev-ключа 100 req/2 мин = 50 req/min → теоретич.
# минимум 1.2 с. 1.25 с (~48 req/min) — у потолка, но с запасом на джиттер/429.
# Меняется флагом --delay (опускать ниже 1.2 рискованно: пойдут 429).
RATE_DELAY = 1.25


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
    net_retries = 0
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
        except (requests.exceptions.RequestException, ConnectionError, OSError) as e:
            # Обрыв соединения / таймаут / DNS — НЕ роняем автономный прогон,
            # а ждём и повторяем с нарастающей паузой; после серии неудач — пропуск.
            net_retries += 1
            if net_retries > 6:
                print(f'  [сеть] не восстановилось ({type(e).__name__}) — пропускаю запрос', flush=True)
                return None
            wait = min(60, 5 * net_retries)
            print(f'  [сеть] {type(e).__name__}: повтор через {wait}s (попытка {net_retries})', flush=True)
            time.sleep(wait)


# ---------- Перебор игроков бакета ----------

def iter_players(watcher, platform: str, bucket: str, seen: set):
    """Бесконечно (пока есть страницы) выдаёт НОВЫЕ puuid игроков бакета.
    Для апекса (master/GM/challenger) — отдельные эндпоинты, для остальных —
    постраничный entries по дивизионам. Когда страницы кончились — добираем
    следующий дивизион; когда кончились все — генератор завершается."""

    # Апекс-лиги: master-бакет тянем из challenger → GM → master.
    if bucket == 'master':
        for name in ('challenger_by_queue', 'grandmaster_by_queue', 'masters_by_queue'):
            fn = getattr(watcher.league, name, None)
            if fn is None:
                continue
            league = api_call(fn, platform, 'RANKED_SOLO_5x5')
            for entry in (league or {}).get('entries', []):
                puuid = entry.get('puuid')
                if not puuid and entry.get('summonerId'):
                    summ = api_call(watcher.summoner.by_id, platform, entry['summonerId'])
                    puuid = summ['puuid'] if summ else None
                if puuid and puuid not in seen:
                    seen.add(puuid)
                    yield puuid
        return

    # Обычные тиры: листаем страницы дивизиона, пока не опустеют.
    for tier, division in TIER_BUCKETS[bucket]:
        page = 1
        while True:
            entries = api_call(
                watcher.league.entries,
                platform, 'RANKED_SOLO_5x5', tier, division, page=page
            )
            if not entries:
                break  # дивизион исчерпан — к следующему
            page += 1
            for entry in entries:
                puuid = entry.get('puuid')
                if not puuid and entry.get('summonerId'):
                    summ = api_call(watcher.summoner.by_id, platform, entry['summonerId'])
                    puuid = summ['puuid'] if summ else None
                if puuid and puuid not in seen:
                    seen.add(puuid)
                    yield puuid


# ---------- Сбор одного бакета ----------

# Окно для оценки «свежести» сбора: если на последних WINDOW проверенных матчах
# доля новых упала ниже SATURATION (бакет вычерпан) — переключаемся дальше.
import collections as _collections

SAT_WINDOW = 250
SAT_MIN_NEW = 12  # < ~5% новых на окне → считаем бакет исчерпанным


def db_total(con) -> int:
    return con.execute('SELECT COUNT(*) FROM processed_matches').fetchone()[0]


def collect_bucket(watcher, con, platform: str, regional: str, bucket: str,
                   start_time: int, cap: int, session_total: int) -> int:
    """Собирает матчи бакета, пока: не кончатся игроки / не просядет частота /
    не достигнут общий лимит. Возвращает число НОВЫХ матчей за бакет.
    KeyboardInterrupt пробрасывается наверх (обрабатывается в run_continuous)."""

    seen: set[str] = set()
    recent = _collections.deque(maxlen=SAT_WINDOW)  # 1=новый матч, 0=дубликат
    collected = 0
    players = 0

    for puuid in iter_players(watcher, platform, bucket, seen):
        players += 1
        print(f'  [{platform}/{bucket}] игрок {players} · новых матчей за бакет {collected}', flush=True)

        match_ids = api_call(
            watcher.match.matchlist_by_puuid,
            regional, puuid, queue=QUEUE_RANKED, count=30, start_time=start_time
        )
        for mid in (match_ids or []):
            if is_processed(con, mid):
                recent.append(0)
                continue
            match = api_call(watcher.match.by_id, regional, mid)
            if not match:
                continue
            process_match(con, match, bucket)
            con.commit()
            collected += 1
            recent.append(1)
            if collected % 10 == 0:
                print(f'    +{collected} (всего в базе: {db_total(con)})', flush=True)
            if cap and session_total + collected >= cap:
                print(f'  Достигнут общий лимит {cap}.', flush=True)
                return collected

        # Просадка частоты: окно заполнено, а новых почти нет → бакет вычерпан.
        if len(recent) == recent.maxlen and sum(recent) < SAT_MIN_NEW:
            print(f'  Частота сбора низкая ({sum(recent)}/{recent.maxlen} новых) — '
                  f'бакет {bucket} вычерпан, переключаюсь.', flush=True)
            return collected

    # Генератор иссяк — игроки бакета кончились.
    print(f'  Игроки бакета {bucket} закончились.', flush=True)
    return collected


# ---------- Автономный сбор: бакеты × регионы ----------

def run_continuous(api_key: str, db_path: str, regions: list, buckets: list,
                   days: int, cap: int):
    import datetime
    watcher = LolWatcher(api_key)
    con     = init_db(db_path)
    start_time = int((datetime.datetime.now(datetime.timezone.utc)
                      - datetime.timedelta(days=days)).timestamp())

    print(f'Старт. В базе сейчас: {db_total(con)} матчей.')
    print(f'Регионы: {", ".join(regions)} | Бакеты: {", ".join(buckets)}')
    print(f'Фильтр: последние {days} дней | пауза {RATE_DELAY}s (~{int(60/RATE_DELAY)} req/min)')
    print(f'Лимит за сессию: {"без лимита" if not cap else cap}. Ctrl+C — остановка с сохранением.\n')

    session_total = 0
    interrupted = False
    try:
        for region in regions:
            regional = PLATFORM_TO_REGIONAL[region]
            for bucket in buckets:
                print(f'\n==== Регион {region} -> {regional} | бакет {bucket} | '
                      f'в базе: {db_total(con)} ====', flush=True)
                got = collect_bucket(watcher, con, region, regional, bucket,
                                     start_time, cap, session_total)
                session_total += got
                print(f'---- Бакет {bucket}/{region}: +{got} | '
                      f'в базе всего: {db_total(con)} | за сессию: {session_total} ----',
                      flush=True)
                if cap and session_total >= cap:
                    raise StopIteration
            print(f'\n#### Все бакеты региона {region} пройдены. '
                  f'В базе: {db_total(con)} ####', flush=True)
        print('\nВсе регионы и бакеты пройдены за этот проход.')
    except KeyboardInterrupt:
        interrupted = True
        print('\nОстановка по Ctrl+C — сохраняю собранное…', flush=True)
    except StopIteration:
        print(f'\nДостигнут общий лимит {cap}.', flush=True)

    # Сбрасываем WAL в основной файл — иначе C# в ReadOnly не увидит данные.
    con.execute('PRAGMA wal_checkpoint(FULL)')
    final = db_total(con)
    con.close()

    print(f'\nГотово. За сессию собрано: {session_total}. Всего в базе: {final}. База: {db_path}')
    if interrupted or session_total:
        print('Чтобы выложить базу на сервер, выполни:')
        print('  powershell -ExecutionPolicy Bypass -File .\\build\\publish-data.ps1')


def _parse_list(value: str, valid, name: str) -> list:
    """'all' → все ключи; иначе список через запятую с проверкой."""
    if value.strip().lower() == 'all':
        return list(valid)
    items = [v.strip() for v in value.split(',') if v.strip()]
    bad = [v for v in items if v not in valid]
    if bad:
        sys.exit(f'Неизвестные значения {name}: {", ".join(bad)}. Доступно: {", ".join(valid)}')
    return items


# ---------- CLI ----------

if __name__ == '__main__':
    p = argparse.ArgumentParser(description='Counterplay — автономный сбор матч-статистики')
    p.add_argument('--key', required=True,
                   help='Riot API key (RGAPI-…)')
    p.add_argument('--tier', default='emerald,gold,master',
                   help='Бакеты через запятую или all (по умолчанию: emerald,gold,master). '
                        f'Доступно: {", ".join(TIER_BUCKETS)}')
    p.add_argument('--region', default='euw1',
                   help='Платформы через запятую или all (по умолчанию: euw1). '
                        f'Доступно: {", ".join(PLATFORM_TO_REGIONAL)}')
    p.add_argument('--games', type=int, default=0,
                   help='Лимит новых матчей за сессию (0 = без лимита, по умолчанию)')
    p.add_argument('--db', default='data.db',
                   help='Путь к SQLite-базе (по умолчанию: data.db)')
    p.add_argument('--days', type=int, default=30,
                   help='Брать матчи только за последние N дней (по умолчанию: 30)')
    p.add_argument('--delay', type=float, default=RATE_DELAY,
                   help=f'Пауза между запросами, с (по умолчанию: {RATE_DELAY}). '
                        'Минимум ~1.2 при dev-ключе, ниже — пойдут 429.')
    args = p.parse_args()

    if args.delay < 1.2:
        print(f'ВНИМАНИЕ: пауза {args.delay}s ниже лимита dev-ключа (1.2s) — '
              'ожидаются 429 и простои на Retry-After.')
    RATE_DELAY = args.delay  # глобал, который читает api_call

    buckets = _parse_list(args.tier,   list(TIER_BUCKETS),        '--tier')
    regions = _parse_list(args.region, list(PLATFORM_TO_REGIONAL), '--region')

    run_continuous(args.key, args.db, regions, buckets, args.days, args.games)
