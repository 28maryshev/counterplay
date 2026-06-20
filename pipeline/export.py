"""
Экспортирует данные из data.db в ../pipeline/data.js (window.CP_DATA).
Запуск: py export.py
"""
import sqlite3
import json
import urllib.request
from pathlib import Path

BASE = Path(__file__).parent
K = 50  # Лаплас-сглаживание (совпадает с C#-движком)


def get_champion_map():
    """Возвращает {numeric_id_str: DataDragon_id} из кэша или Data Dragon."""
    cache = BASE / 'champion_map.json'
    if cache.exists():
        with open(cache, encoding='utf-8') as f:
            return json.load(f)

    print('Загружаю карту чемпионов из Data Dragon...')
    with urllib.request.urlopen('https://ddragon.leagueoflegends.com/api/versions.json') as r:
        version = json.loads(r.read())[0]
    url = f'https://ddragon.leagueoflegends.com/cdn/{version}/data/en_US/champion.json'
    with urllib.request.urlopen(url) as r:
        data = json.loads(r.read())

    id_map = {c['key']: c['id'] for c in data['data'].values()}
    with open(cache, 'w', encoding='utf-8') as f:
        json.dump(id_map, f)
    print(f'  Сохранено {len(id_map)} чемпионов -> {cache.name}')
    return id_map


def main():
    id_map = get_champion_map()
    con = sqlite3.connect(BASE / 'data.db')

    # Последние 3 патча (та же логика что в C#)
    patches = [r[0] for r in con.execute("""
        SELECT DISTINCT patch FROM base_wr
        ORDER BY CAST(SUBSTR(patch,1,INSTR(patch,'.')-1) AS INTEGER) DESC,
                 CAST(SUBSTR(patch,INSTR(patch,'.')+1)   AS INTEGER) DESC
        LIMIT 3
    """)]
    if not patches:
        print('Нет данных в data.db!')
        return
    ph = patches + [patches[-1]] * (3 - len(patches))

    tiers = [r[0] for r in con.execute("SELECT DISTINCT tier_bucket FROM base_wr")]
    tier = 'emerald' if 'emerald' in tiers else tiers[0]
    print(f'Патчи: {patches}  Тир: {tier}')

    def sid(num_id):
        return id_map.get(str(num_id))

    # ── Base WR ──────────────────────────────────────────────────────────────
    base_wr = {}
    for champ_id, role, games, wins in con.execute("""
        SELECT champion_id, role, SUM(games), SUM(wins) FROM base_wr
        WHERE tier_bucket=? AND patch IN (?,?,?)
        GROUP BY champion_id, role
    """, [tier] + ph):
        s = sid(champ_id)
        if not s: continue
        base_wr.setdefault(s, {})[role] = {'g': games, 'w': wins}

    # ── Matchup ───────────────────────────────────────────────────────────────
    matchup = {}
    for champ_id, vs_id, role, games, wins in con.execute("""
        SELECT champion_id, vs_champion_id, role, SUM(games), SUM(wins) FROM matchup
        WHERE tier_bucket=? AND patch IN (?,?,?)
        GROUP BY champion_id, vs_champion_id, role
    """, [tier] + ph):
        s, v = sid(champ_id), sid(vs_id)
        if not s or not v: continue
        matchup.setdefault(s + '/' + v, {})[role] = {'g': games, 'w': wins}

    # ── Synergy ───────────────────────────────────────────────────────────────
    synergy = {}
    for champ_id, ally_id, role, ally_role, games, wins in con.execute("""
        SELECT champion_id, ally_id, role, ally_role, SUM(games), SUM(wins) FROM synergy
        WHERE tier_bucket=? AND patch IN (?,?,?)
        GROUP BY champion_id, ally_id, role, ally_role
    """, [tier] + ph):
        s, a = sid(champ_id), sid(ally_id)
        if not s or not a: continue
        synergy.setdefault(s + '/' + a, {})[role + '/' + ally_role] = {'g': games, 'w': wins}

    con.close()

    out = {
        'meta': {'patches': patches, 'tier': tier},
        'base_wr': base_wr,
        'matchup': matchup,
        'synergy': synergy,
    }

    out_path = BASE / 'data.js'
    with open(out_path, 'w', encoding='utf-8') as f:
        f.write('// Авто-сгенерировано из data.db\nwindow.CP_DATA = ')
        json.dump(out, f, ensure_ascii=False, separators=(',', ':'))
        f.write(';\n')

    print(f'Экспортировано: {len(base_wr)} чемпионов, '
          f'{len(matchup)} матчап-пар, {len(synergy)} синергий')
    print(f'Файл: {out_path}')


if __name__ == '__main__':
    main()
