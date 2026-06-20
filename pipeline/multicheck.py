import sqlite3
con = sqlite3.connect('data.db')

patches = [r[0] for r in con.execute("""
    SELECT DISTINCT patch FROM base_wr
    ORDER BY CAST(SUBSTR(patch,1,INSTR(patch,'.')-1) AS INTEGER) DESC,
             CAST(SUBSTR(patch,INSTR(patch,'.')+1)   AS INTEGER) DESC
    LIMIT 3
""")]
print('Патчи для агрегации:', patches)
ph = patches + [patches[-1]] * (3 - len(patches))  # pad до 3

print()
for role in ('top', 'jungle', 'mid', 'adc', 'support'):
    n = con.execute("""
        SELECT COUNT(*) FROM (
            SELECT champion_id FROM base_wr
            WHERE role=? AND tier_bucket='emerald' AND patch IN (?,?,?)
            GROUP BY champion_id HAVING SUM(games) >= 30
        )
    """, [role] + ph).fetchone()[0]
    print(f'  {role:<8} {n:>3} кандидатов (>= 30 игр суммарно)')

avg = con.execute("""
    SELECT AVG(total) FROM (
        SELECT champion_id, vs_champion_id, SUM(games) total FROM matchup
        WHERE role='support' AND tier_bucket='emerald' AND patch IN (?,?,?)
        GROUP BY champion_id, vs_champion_id
    )
""", ph).fetchone()[0]
pairs = con.execute("""
    SELECT COUNT(DISTINCT champion_id || '-' || vs_champion_id) FROM matchup
    WHERE role='support' AND tier_bucket='emerald' AND patch IN (?,?,?)
""", ph).fetchone()[0]
print(f'\nSupport матчап-пар (3 патча): {pairs}, среднее игр: {avg:.1f}')
con.close()
