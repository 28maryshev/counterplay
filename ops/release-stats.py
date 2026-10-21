# -*- coding: utf-8 -*-
"""Игроки и выпуски: сколько людей играло вокруг каждого выпуска.

Номер версии в телеметрии не помогает — у всех один и тот же (release.ps1 не
передаёт его в сборку), поэтому выпуск с игроками связывается по ВРЕМЕНИ:
когда вышел, кто запускал программу за сутки до и после, сколько скачали
установщик, пока он был свежим, и сколько пакетов обновления ушло.

Источника три:
    GitHub    — даты выпусков; загрузки установщика (сайт ведёт на файл
                последнего выпуска, так что это загрузки, пока выпуск был
                свежим); загрузки пакетов обновления из катящегося тега latest;
                загрузки фида releases.win.json — его спрашивает каждая
                программа при запуске и раз в час, а release.ps1 заменяет его
                при каждом выпуске, и счётчик начинается с нуля;
    сайт      — таблицы heartbeats (пинг на каждый запуск, в том числе на
                перезапуск после обновления), installs (новые — fresh) и
                downloads. Только чтение: запрос идёт внутри BEGIN READ ONLY;
    журнал    — journal/feed-checks.md: сколько раз спросили фид за жизнь
                каждого выпуска (строку дописывает release.ps1 перед заменой).

Запуск:
    python ops/release-stats.py                обновить сводку в journal/RELEASES.md
    python ops/release-stats.py --print        только напечатать, файл не трогать
    python ops/release-stats.py --github-only  без телеметрии сайта (ssh не нужен)
    python ops/release-stats.py --days 60      по выпускам — за 60 дней (по умолчанию 45)

В файле меняется только кусок между метками auto:begin и auto:end — описание и
выводы вокруг него пишутся руками и не затираются.
"""
import argparse
import datetime as dt
import json
import os
import re
import subprocess
import sys
from collections import defaultdict
from pathlib import Path

REPO = os.environ.get("GITHUB_REPO", "28maryshev/counterplay")
SITE = os.environ.get("SITE_SSH", "cp-site")
COLLECTOR = os.environ.get("COLLECTOR_SSH", "collector")
DB_CONTAINER = "counterplay-site-db-1"

ROOT = Path(__file__).resolve().parent.parent
DOC = ROOT / "journal" / "RELEASES.md"
FEED_LOG = ROOT / "journal" / "feed-checks.md"
BEGIN, END = "<!-- auto:begin -->", "<!-- auto:end -->"
# Сколько часов должен прожить выпуск, чтобы считаться устоявшимся. Программа
# спрашивает фид раз в час; за шесть до выпуска доходит заметная часть игроков
# (по Δ), за час-два — единицы.
SETTLE = 6

UTC = dt.timezone.utc


def ts(s: str) -> dt.datetime:
    return dt.datetime.fromisoformat(s.replace("Z", "+00:00")).astimezone(UTC)


def gh_lines(*args) -> list[dict]:
    """Каждый объект — отдельной строкой (--jq), так --paginate не склеивает
    страницы в невалидный JSON."""
    out = subprocess.run(["gh", "api", *args], capture_output=True, text=True,
                         encoding="utf-8")
    if out.returncode != 0:
        sys.exit(f"gh api {args[0]}: {out.stderr.strip()}")
    return [json.loads(l) for l in out.stdout.splitlines() if l.strip()]


# ── GitHub ────────────────────────────────────────────────────────────────

def github():
    rels = gh_lines(
        f"repos/{REPO}/releases", "--paginate", "--jq",
        '.[] | select(.tag_name|test("^v[0-9]")) | {tag: .tag_name, at: .published_at,'
        ' setup: ([.assets[]|select(.name|test("Setup"))|.download_count]|add // 0)}')
    for r in rels:
        r["ver"] = r["tag"][1:]
        r["at"] = ts(r["at"])
    rels.sort(key=lambda r: r["at"])
    # Время жизни — до следующего выпуска; у свежего — до сейчас.
    now = dt.datetime.now(UTC)
    for a, b in zip(rels, rels[1:] + [None]):
        a["until"] = b["at"] if b else None
        a["life"] = ((b["at"] if b else now) - a["at"]).total_seconds() / 3600

    feed = gh_lines(f"repos/{REPO}/releases/tags/latest", "--jq",
                    ".assets[] | {name, n: .download_count, at: .created_at}")
    pk = defaultdict(lambda: {"delta": 0, "full": 0, "laid": None})
    current = None
    for a in feed:
        m = re.match(r"Counterplay-(\d+\.\d+\.\d+)-(delta|full)\.nupkg$", a["name"])
        if m:
            pk[m[1]][m[2]] = a["n"]
            pk[m[1]]["laid"] = ts(a["at"])
        elif a["name"] == "releases.win.json":
            current = {"at": ts(a["at"]), "n": a["n"]}
    for r in rels:
        r.update(pk.get(r["ver"], {"delta": None, "full": None, "laid": None}))
        # Пакеты выложены сильно позже выпуска — значит, их перевыкладывали
        # (откат ленты через -FeedOnly), и GitHub начал счёт загрузок заново.
        r["relaid"] = r["laid"] if r["laid"] and r["laid"] - r["at"] > dt.timedelta(minutes=30) else None
    return rels, current


def feed_log(rels):
    """Снимки счётчика фида, которые release.ps1 сделал перед заменой.
    Строка: | фид выложен | заменён | загрузок |. Выпуск — последний,
    вышедший не позже выкладки фида (фид кладут сразу за выпуском)."""
    if not FEED_LOG.exists():
        return {}
    got = {}
    for line in FEED_LOG.read_text(encoding="utf-8").splitlines():
        m = re.match(r"\|\s*(\d{4}-\S+)\s*\|\s*(\d{4}-\S+)\s*\|\s*(\d+)\s*\|", line)
        if not m:
            continue
        laid, gone, n = ts(m[1]), ts(m[2]), int(m[3])
        owner = [r for r in rels if r["at"] <= laid + dt.timedelta(minutes=5)]
        if owner:
            got[owner[-1]["ver"]] = n / max((gone - laid).total_seconds() / 3600, 0.01)
    return got


# ── Телеметрия сайта ──────────────────────────────────────────────────────

def psql(sql: str) -> list[list[str]]:
    """Прямой ssh на сайт пускают только с домашнего адреса, а он меняется —
    тогда тем же путём, что healthcheck: через сервер сбора."""
    cmd = (f"docker exec -i {DB_CONTAINER} psql -U counterplay -d counterplay"
           " -X -q -t -A -F '|' -v ON_ERROR_STOP=1")
    for jump in ([], ["-J", COLLECTOR]):
        out = subprocess.run(
            ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", *jump, SITE, cmd],
            input=sql, capture_output=True, text=True, encoding="utf-8")
        if out.returncode == 0:
            return [l.split("|") for l in out.stdout.splitlines() if l.strip()]
        err = out.stderr.strip()
    sys.exit(f"телеметрия сайта не прочиталась: {err}")


def telemetry(rels):
    windows = ",\n".join(f"('{r['ver']}', '{r['at'].isoformat()}'::timestamptz)" for r in rels)
    sql = f"""
BEGIN READ ONLY;
SELECT 'first', to_char(min(created_at) AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')
  FROM heartbeats;
SELECT 'day', to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD'),
       count(DISTINCT install_id), count(*)
  FROM heartbeats GROUP BY 2 ORDER BY 2;
SELECT 'week', to_char(date_trunc('week', created_at AT TIME ZONE 'UTC'), 'YYYY-MM-DD'),
       count(DISTINCT install_id)
  FROM heartbeats GROUP BY 2 ORDER BY 2;
SELECT 'month', to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM'), count(DISTINCT install_id)
  FROM heartbeats GROUP BY 2 ORDER BY 2;
SELECT 'new', to_char(first_seen AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')
  FROM installs WHERE fresh;
SELECT 'dl', to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD')
  FROM downloads;
WITH r(ver, t) AS (VALUES {windows})
SELECT 'rel', r.ver,
       count(DISTINCT h.install_id) FILTER (WHERE h.created_at <  r.t),
       count(*)                     FILTER (WHERE h.created_at <  r.t),
       count(DISTINCT h.install_id) FILTER (WHERE h.created_at >= r.t),
       count(*)                     FILTER (WHERE h.created_at >= r.t)
  FROM r LEFT JOIN heartbeats h
    ON h.created_at >= r.t - interval '24 hours' AND h.created_at < r.t + interval '24 hours'
 GROUP BY r.ver, r.t;
COMMIT;
"""
    t = {"day": {}, "week": {}, "month": {}, "new": [], "dl": [], "rel": {}, "first": None}
    for row in psql(sql):
        k = row[0]
        if k == "first":
            t["first"] = ts(row[1]) if row[1] else None
        elif k == "day":
            t["day"][row[1]] = (int(row[2]), int(row[3]))
        elif k in ("week", "month"):
            t[k][row[1]] = int(row[2])
        elif k == "new":
            t["new"].append(ts(row[1]))
        elif k == "dl":
            t["dl"].append(row[1])
        elif k == "rel":
            t["rel"][row[1]] = tuple(int(x) for x in row[2:6])
    return t


# ── Сводка ────────────────────────────────────────────────────────────────

def life(h: float) -> str:
    if h < 1:
        return f"{round(h * 60)} мин"
    if h < 48:
        return f"{h:.0f} ч"
    return f"{h / 24:.1f} д".replace(".", ",")


def cell(v) -> str:
    return "—" if v is None else str(v)


def dec(x: float) -> str:
    return f"{x:.1f}".replace(".", ",")


def render(rels, current, feed_rate, t, days: int) -> str:
    now = dt.datetime.now(UTC)
    out = [f"Снято {now:%Y-%m-%d %H:%M} UTC командой `python ops/release-stats.py`"
           + ("" if t else " **без телеметрии сайта** (`--github-only`)") + ".", ""]

    # Сейчас: свежий выпуск и сколько программ на связи.
    last = rels[-1]
    out.append("### Сейчас")
    out.append("")
    out.append(f"- свежий выпуск **{last['ver']}**, вышел {last['at']:%d.%m %H:%M} UTC, "
               f"{life(last['life'])} назад;")
    if current:
        hours = max((now - current["at"]).total_seconds() / 3600, 0.01)
        out.append(f"- фид обновлений спросили **{current['n']}** раз за {life(hours)} — "
                   f"**{dec(current['n'] / hours)} в час**: примерно столько программ "
                   "держится на связи (каждая спрашивает раз в час и при запуске);")
    if t:
        last_day = max(t["day"]) if t["day"] else None
        if last_day:
            out.append(f"- за {last_day} (UTC, день ещё может идти): "
                       f"игроков {t['day'][last_day][0]}, запусков {t['day'][last_day][1]}.")
    out.append("")

    # По неделям — только где есть телеметрия.
    if t and t["day"]:
        out.append("### По неделям")
        out.append("")
        out.append("Неделя с понедельника, UTC. Игроки за неделю — разные установки, "
                   "запускавшие программу; в день — среднее по дням недели.")
        out.append("")
        out.append("| неделя | выпусков | игроков за неделю | в день | запусков | "
                   "загрузок с сайта | новых установок |")
        out.append("|---|---|---|---|---|---|---|")
        weeks = defaultdict(lambda: {"rel": [], "dau": [], "pings": 0, "new": 0, "dl": 0})
        monday = lambda d: (d - dt.timedelta(days=d.weekday())).strftime("%Y-%m-%d")
        for day, (dau, pings) in t["day"].items():
            w = weeks[monday(dt.date.fromisoformat(day))]
            w["dau"].append(dau)
            w["pings"] += pings
        for r in rels:
            weeks[monday(r["at"].date())]["rel"].append(r["ver"])
        for n in t["new"]:
            weeks[monday(n.date())]["new"] += 1
        for d in t["dl"]:
            weeks[monday(dt.date.fromisoformat(d))]["dl"] += 1
        for wk in sorted(weeks):
            w = weeks[wk]
            if not w["dau"]:
                continue
            vs = w["rel"]
            rel = ", ".join(vs) if len(vs) <= 3 else f"{vs[0]}…{vs[-1]}"
            avg = sum(w["dau"]) / len(w["dau"])
            out.append(f"| {dt.date.fromisoformat(wk):%d.%m} | "
                       f"{len(vs)}{' (' + rel + ')' if rel else ''} | "
                       f"{t['week'].get(wk, '—')} | {avg:.0f} | {w['pings']} | "
                       f"{w['dl']} | {w['new']} |")
        out.append("")

    # По выпускам за последние days дней. Выпуск, проживший меньше SETTLE часов,
    # — исправление на ходу: его сменили раньше, чем до него дошло большинство.
    # Такие сворачиваются в выпуск, который их сменил и устоял; загрузки
    # установщика и новые установки — за всю серию, остальное — устоявшегося.
    edge = now - dt.timedelta(days=days)
    recent = [r for r in rels if r["at"] >= edge]
    series, run = [], []
    for r in recent:
        run.append(r)
        if r["life"] >= SETTLE or r is rels[-1]:
            series.append(run)
            run = []
    if run:
        series.append(run)
    out.append(f"### По выпускам за {days} дней")
    out.append("")
    out.append(f"Время — UTC. Выпуски, прожившие меньше {SETTLE} ч, свёрнуты в тот, что их "
               "сменил (в скобках). «Жил» — до следующего выпуска. «Установщик» — "
               "загрузки Setup.exe, пока серия была свежей; «новых» — новые установки за "
               "то же время. «Δ» — сколько раз обновление прошло через эту версию дельтой "
               "(в том числе позже, по цепочке на более новую); «полных» — сколько "
               "обновились на неё целым пакетом, пока она была свежей. «На связи» — "
               "запросов фида в час за жизнь выпуска. «Сутки до / после» — разные "
               "установки, запускавшие программу за 24 ч до выхода и после; в скобках — "
               "запусков.")
    out.append("")
    hdr = "| выпуск | вышел | жил | установщик | новых | Δ | полных | на связи |"
    sep = "|---|---|---|---|---|---|---|---|"
    if t:
        hdr += " сутки до | сутки после |"
        sep += "---|---|"
    out += [hdr, sep]
    relaid = []
    for run in reversed(series):
        r = run[-1]
        name = r["ver"] + (f" (+{len(run) - 1}: {run[0]['ver']}…)" if len(run) > 2 else
                           f" (+{run[0]['ver']})" if len(run) == 2 else "")
        new = None
        if t and t["first"] and run[0]["at"] >= t["first"]:
            end = r["until"] or now
            new = sum(1 for n in t["new"] if run[0]["at"] <= n < end)
        rate = feed_rate.get(r["ver"])
        mark = "*" if r["relaid"] else ""
        if mark:
            relaid.append(f"\\* {r['ver']}: пакеты перевыложены {r['relaid']:%d.%m %H:%M} — "
                          "счёт загрузок с этого момента.")
        lived = life(r["life"]) + (", свежий" if r["until"] is None else "")
        row = (f"| {name} | {r['at']:%d.%m %H:%M} | {lived} | "
               f"{sum(x['setup'] for x in run)} | "
               f"{cell(new)} | {cell(r['delta'])}{mark} | {cell(r['full'])}{mark} | "
               f"{'—' if rate is None else dec(rate)} |")
        if t:
            w = t["rel"].get(r["ver"])
            if w and t["first"] and r["at"] - dt.timedelta(hours=24) >= t["first"]:
                row += f" {w[0]} ({w[1]}) | {w[2]} ({w[3]}) |"
            else:
                row += " — | — |"
        out.append(row)
    out.append("")
    if relaid:
        out += [*relaid, ""]

    # По месяцам — вся история. Δ и полных сюда не идут: дельту одной программы,
    # прошедшей цепочкой через пять версий, GitHub считает пять раз, и сумма за
    # месяц говорит о трафике, а не о людях.
    out.append("### По месяцам")
    out.append("")
    out.append(f"«Устоявшихся» — проживших от {SETTLE} ч. «Установщик» — загрузки Setup.exe "
               "выпусков этого месяца. Игроков — разные установки, запускавшие программу "
               "за месяц.")
    out.append("")
    out.append("| месяц | выпусков | устоявшихся | установщик | игроков | новых установок |")
    out.append("|---|---|---|---|---|---|")
    months = defaultdict(list)
    for r in rels:
        months[r["at"].strftime("%Y-%m")].append(r)
    for m in sorted(months, reverse=True):
        rs = months[m]
        new = sum(1 for n in t["new"] if n.strftime("%Y-%m") == m) if t else None
        out.append(f"| {m} | {len(rs)} ({rs[0]['ver']}…{rs[-1]['ver']}) | "
                   f"{sum(1 for r in rs if r['life'] >= SETTLE)} | {sum(r['setup'] for r in rs)} | "
                   f"{t['month'].get(m, '—') if t else '—'} | {cell(new) if t and t['month'].get(m) else '—'} |")
    out.append("")

    if t and t["first"]:
        out.append(f"Телеметрия сайта — с {t['first']:%d.%m.%Y} (раньше база потеряна "
                   "вместе с сервером); до этой даты колонки сайта пустые.")
        out.append("")
    return "\n".join(out)


def write(body: str):
    text = DOC.read_text(encoding="utf-8") if DOC.exists() else ""
    if BEGIN not in text or END not in text:
        sys.exit(f"в {DOC} нет меток {BEGIN} … {END} — некуда писать")
    head, rest = text.split(BEGIN, 1)
    _, tail = rest.split(END, 1)
    DOC.write_text(f"{head}{BEGIN}\n{body}\n{END}{tail}", encoding="utf-8")
    print(f"сводка записана в {DOC.relative_to(ROOT)}")


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description="Игроки вокруг выпусков: GitHub + телеметрия сайта")
    ap.add_argument("--print", action="store_true", help="только напечатать")
    ap.add_argument("--github-only", action="store_true", help="без телеметрии сайта")
    ap.add_argument("--days", type=int, default=45, help="по выпускам — за сколько дней")
    a = ap.parse_args()

    rels, current = github()
    tele = None if a.github_only else telemetry(rels)
    body = render(rels, current, feed_log(rels), tele, a.days)
    if a.print:
        print(body)
    else:
        write(body)
