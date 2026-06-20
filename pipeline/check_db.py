import sqlite3, os

con = sqlite3.connect('data.db')

size = os.path.getsize('data.db') / 1024
print(f'Размер: {size:.1f} KB\n')

print('Строк в таблицах:')
for t in ('processed_matches', 'base_wr', 'matchup', 'synergy'):
    n = con.execute(f'SELECT COUNT(*) FROM {t}').fetchone()[0]
    print(f'  {t}: {n}')

# Последний патч (правильная сортировка по версии)
latest = con.execute("""
    SELECT patch FROM base_wr
    ORDER BY CAST(SUBSTR(patch, 1, INSTR(patch, '.') - 1) AS INTEGER) DESC,
             CAST(SUBSTR(patch, INSTR(patch, '.') + 1)     AS INTEGER) DESC
    LIMIT 1
""").fetchone()[0]
print(f'\nПоследний патч: {latest}')

print('\nМатчи по патчам (топ-5 свежих):')
rows = con.execute("""
    SELECT patch, SUM(games)/10 as matches
    FROM base_wr
    GROUP BY patch
    ORDER BY CAST(SUBSTR(patch, 1, INSTR(patch, '.') - 1) AS INTEGER) DESC,
             CAST(SUBSTR(patch, INSTR(patch, '.') + 1)     AS INTEGER) DESC
    LIMIT 5
""").fetchall()
for r in rows:
    print(f'  {r[0]}: ~{r[1]} матчей')

print(f'\nТоп-10 чемпионов support на патче {latest}:')
rows = con.execute("""
    SELECT champion_id, games, ROUND(100.0*wins/games,1) wr
    FROM base_wr WHERE role = "support" AND patch = ?
    ORDER BY games DESC LIMIT 10
""", (latest,)).fetchall()
for r in rows:
    print(f'  championId={r[0]:4d}  games={r[1]:4d}  WR={r[2]}%')

print(f'\nМатчапы support на патче {latest} (топ-10 по играм):')
rows = con.execute("""
    SELECT champion_id, vs_champion_id, games, ROUND(100.0*wins/games,1) wr
    FROM matchup WHERE role = "support" AND patch = ?
    ORDER BY games DESC LIMIT 10
""", (latest,)).fetchall()
for r in rows:
    print(f'  {r[0]:4d} vs {r[1]:4d}  games={r[2]}  WR={r[3]}%')

con.close()
