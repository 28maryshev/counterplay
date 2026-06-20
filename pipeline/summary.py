import sqlite3
con = sqlite3.connect('data.db')

total = con.execute('SELECT COUNT(*) FROM processed_matches').fetchone()[0]
print(f'Всего матчей: {total}')

patch = con.execute("""
    SELECT patch FROM base_wr
    ORDER BY CAST(SUBSTR(patch,1,INSTR(patch,'.')-1) AS INTEGER) DESC,
             CAST(SUBSTR(patch,INSTR(patch,'.')+1)   AS INTEGER) DESC
    LIMIT 1
""").fetchone()[0]
print(f'Актуальный патч: {patch}\n')

print('── Кандидаты по ролям (emerald, текущий патч, >= 30 игр) ──')
for role in ('top','jungle','mid','adc','support'):
    n = con.execute("""
        SELECT COUNT(*) FROM base_wr
        WHERE role=? AND tier_bucket='emerald' AND patch=? AND games>=30
    """, (role, patch)).fetchone()[0]
    print(f'  {role:<8} {n:>3} чемпионов')

print('\n── Покрытие матчап-таблицы (support, текущий патч) ──')
total_pairs = con.execute("""
    SELECT COUNT(*) FROM matchup
    WHERE role='support' AND tier_bucket='emerald' AND patch=?
""", (patch,)).fetchone()[0]
avg_games = con.execute("""
    SELECT AVG(games) FROM matchup
    WHERE role='support' AND tier_bucket='emerald' AND patch=?
""", (patch,)).fetchone()[0]
print(f'  пар матчапов: {total_pairs}')
print(f'  среднее игр на пару: {avg_games:.1f}')

print('\n── Топ-5 саппортов по играм ──')
rows = con.execute("""
    SELECT champion_id, games, ROUND(100.0*wins/games,1) wr
    FROM base_wr WHERE role='support' AND tier_bucket='emerald' AND patch=?
    ORDER BY games DESC LIMIT 5
""", (patch,)).fetchall()
for r in rows:
    print(f'  id={r[0]}  {r[1]} игр  WR={r[2]}%')

con.close()
