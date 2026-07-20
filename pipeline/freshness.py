# -*- coding: utf-8 -*-
"""
freshness.py — готовность патча и политика УДЕРЖАНИЯ.

Когда выходит новый патч, по нему сперва мало матчей — тир-лист и рекомендации
на таких данных шумят. Поэтому не «промоутим» новый патч, пока он не наберёт
достаточно данных: держим предыдущий (полный) как основной. Как только новый
патч покрывает большинство «чемпион+роль» — переключаемся сами.

Пороги держим синхронно с ботом (bot/lib/freshness.js): READY_MIN_GAMES /
READY_FRACTION. Используется экспортами (export_draft/export_tiers/export_runes),
чтобы сайт, программа и руны держали ОДИН и тот же основной патч.
"""

MAIN_BUCKET = 'emerald'
READY_MIN_GAMES = 150   # взвеш. игр на «чемпион+роль», чтобы считать пару набранной
READY_FRACTION = 0.7    # доля пар прошлого патча, покрытых новым → патч «готов»


def patch_key(p):
    return tuple(int(x) for x in p.split('.'))


def all_patches(con):
    ps = [r[0] for r in con.execute('SELECT DISTINCT patch FROM base_wr')
          if r[0] and r[0][0].isdigit()]
    return sorted(ps, key=patch_key, reverse=True)


def coverage(con, patch, bucket=MAIN_BUCKET):
    """Сколько «чемпион+роль» в основном бакете набрали >= READY_MIN_GAMES игр."""
    return con.execute(
        """SELECT COUNT(*) FROM (
             SELECT champion_id, role, SUM(games) g FROM base_wr
             WHERE patch=? AND tier_bucket=? GROUP BY champion_id, role
             HAVING g >= ?)""", (patch, bucket, READY_MIN_GAMES)).fetchone()[0]


def is_ready(con, patch, prev):
    """Готов ли патч: покрывает ли >= READY_FRACTION пар предыдущего патча."""
    if prev is None:
        return True
    cov_new = coverage(con, patch)
    cov_prev = coverage(con, prev)
    if cov_prev <= 0:
        return True
    return (cov_new / cov_prev) >= READY_FRACTION


def pick_patches(con, n=3):
    """Патчи для выгрузки/движка с удержанием. Если новейший патч ещё сырой —
    исключаем его, основным становится предыдущий (полный). Возвращает до n
    патчей, свежий → старый."""
    ps = all_patches(con)
    if not ps:
        return []
    if len(ps) >= 2 and not is_ready(con, ps[0], ps[1]):
        ps = ps[1:]
    return ps[:n]
