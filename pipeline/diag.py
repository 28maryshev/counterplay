import sqlite3
con = sqlite3.connect('data.db')

patch = con.execute("""
    SELECT patch FROM base_wr
    ORDER BY CAST(SUBSTR(patch, 1, INSTR(patch, '.') - 1) AS INTEGER) DESC,
             CAST(SUBSTR(patch, INSTR(patch, '.') + 1)    AS INTEGER) DESC
    LIMIT 1
""").fetchone()[0]
print('Последний патч (корректная сортировка):', patch)

print()
print('Кандидаты support по бакетам:')
for bucket in ('silver', 'gold', 'emerald', 'master'):
    n = con.execute(
        "SELECT COUNT(*) FROM base_wr WHERE role='support' AND tier_bucket=? AND patch=?",
        (bucket, patch)
    ).fetchone()[0]
    print(f'  {bucket:8s} / {patch}: {n} строк')

print()
print('Все уникальные (tier_bucket, patch) для support:')
rows = con.execute("""
    SELECT tier_bucket, patch, COUNT(*) as n FROM base_wr
    WHERE role='support'
    GROUP BY tier_bucket, patch
    ORDER BY tier_bucket,
             CAST(SUBSTR(patch, 1, INSTR(patch, '.') - 1) AS INTEGER) DESC,
             CAST(SUBSTR(patch, INSTR(patch, '.') + 1) AS INTEGER) DESC
""").fetchall()
for r in rows:
    print(f'  {r[0]:8s} / {r[1]}: {r[2]} чемпионов')

con.close()
