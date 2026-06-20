import sqlite3
con = sqlite3.connect('data.db')
rows = con.execute("""
    SELECT champion_id, games, wins, ROUND(100.0*wins/games,1) as wr
    FROM base_wr
    WHERE role='support' AND tier_bucket='emerald' AND patch='16.12'
    ORDER BY games DESC
    LIMIT 30
""").fetchall()
print(f"{'champ_id':<12} {'games':<7} {'wins':<6} WR%")
for r in rows:
    print(f"  {r[0]:<12} {r[1]:<7} {r[2]:<6} {r[3]}%")
con.close()
