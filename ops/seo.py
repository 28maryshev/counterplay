# -*- coding: utf-8 -*-
"""Позиции в поиске — Google Search Console и Bing Webmaster Tools.

Чего нет в своей аналитике сайта: та видит только тех, кто ДОШЁЛ. Здесь видно
то, что было до клика — по каким запросам нас показывают, на каком месте, и
берут ли поисковики наши страницы вообще. Источника два, потому что картины у
них разные: Bing пока даёт нам заметно больше показов и кликов, чем Google.

Запуск:
    python ops/seo.py            обе картины: позиции, динамика недели, запросы, страницы
    python ops/seo.py --google   только Google
    python ops/seo.py --bing     только Bing
    python ops/seo.py --index    выборочная проверка индексации (Google, 15 адресов)

Ключи НЕ лежат в репозитории — он публичный. Где они на самом деле, записано в
памяти проекта:
    Google — сервисный аккаунт counterplay-reader, переменная GSC_KEY или файл
             ~/.ssh/counterplay-*.json;
    Bing   — строка из панели (Settings → API Access), переменная BING_KEY или
             файл ~/.ssh/bing-webmaster.key.
"""
import argparse
import datetime as dt
import glob
import json
import os
import random
import re
import subprocess
import sys

SITE = "sc-domain:counterplays.com"
SCOPE = ["https://www.googleapis.com/auth/webmasters.readonly"]

BING_SITE = "https://counterplays.com/"
BING_API = "https://ssl.bing.com/webmaster/api.svc/json"


def short_url(u: str) -> str:
    """Убираем имя хоста. www-версия отвечает своим содержимым, но canonical на
    ней указывает на основной адрес, и в карте сайта только основной — в отчёте
    это один и тот же адрес, незачем их разводить."""
    return re.sub(r"^https?://(www\.)?counterplays\.com", "", u) or "/"


def key_path() -> str:
    p = os.environ.get("GSC_KEY")
    if p and os.path.exists(p):
        return p
    found = glob.glob(os.path.expanduser("~/.ssh/counterplay-*.json"))
    if not found:
        sys.exit("ключ Search Console не найден: задай GSC_KEY или положи его в ~/.ssh/")
    return found[0]


def service():
    from google.oauth2 import service_account
    from googleapiclient.discovery import build
    creds = service_account.Credentials.from_service_account_file(key_path(), scopes=SCOPE)
    return build("searchconsole", "v1", credentials=creds, cache_discovery=False)


def rows(svc, start, end, dims, limit=25):
    body = {"startDate": str(start), "endDate": str(end), "dimensions": dims, "rowLimit": limit}
    return svc.searchanalytics().query(siteUrl=SITE, body=body).execute().get("rows", [])


def top_by_shows(svc, start, end, dim, n):
    """Search Console отдаёт строки по кликам. Нам интереснее показы: запрос, по
    которому нас видят сотню раз и ни разу не нажимают, — это и есть работа."""
    all_rows = rows(svc, start, end, [dim], 500)
    all_rows.sort(key=lambda r: -r["impressions"])
    return all_rows[:n]


def report(svc):
    # Google отдаёт данные с задержкой в пару дней — вчерашний день уже полный.
    end = dt.date.today() - dt.timedelta(days=1)
    start = end - dt.timedelta(days=27)

    print("=== GOOGLE: ПО ДНЯМ (последние 14) ===")
    for r in rows(svc, start, end, ["date"], 40)[-14:]:
        print(f"  {r['keys'][0]}  кликов {r['clicks']:>4.0f}  показов {r['impressions']:>6.0f}"
              f"  CTR {r['ctr'] * 100:>5.1f}%  позиция {r['position']:>5.1f}")

    def total(s, e):
        rs = rows(svc, s, e, [], 1)
        return rs[0] if rs else None

    now = total(end - dt.timedelta(days=6), end)
    was = total(end - dt.timedelta(days=13), end - dt.timedelta(days=7))
    print("\n=== GOOGLE: НЕДЕЛЯ К НЕДЕЛЕ ===")
    if now and was:
        def line(name, a, b, fmt="{:.0f}", less_is_better=False):
            d = a - b
            better = (d <= 0) if less_is_better else (d >= 0)
            print(f"  {name:<18} {fmt.format(a):>8}  было {fmt.format(b):>8}   "
                  f"{'+' if d >= 0 else ''}{fmt.format(d)}  {'лучше' if better else 'хуже'}")
        line("клики", now["clicks"], was["clicks"])
        line("показы", now["impressions"], was["impressions"])
        line("CTR, %", now["ctr"] * 100, was["ctr"] * 100, "{:.2f}")
        line("средняя позиция", now["position"], was["position"], "{:.1f}", less_is_better=True)
    else:
        print("  сравнивать не с чем")

    print("\n=== GOOGLE: ЗАПРОСЫ (28 дней, по показам) ===")
    for r in top_by_shows(svc, start, end, "query", 15):
        print(f"  поз {r['position']:>5.1f}  показов {r['impressions']:>5.0f}"
              f"  кликов {r['clicks']:>3.0f}   {r['keys'][0][:60]}")

    print("\n=== GOOGLE: СТРАНИЦЫ (28 дней, по показам) ===")
    for r in top_by_shows(svc, start, end, "page", 12):
        p = short_url(r["keys"][0])
        print(f"  поз {r['position']:>5.1f}  показов {r['impressions']:>5.0f}"
              f"  кликов {r['clicks']:>3.0f}   {p[:60]}")

    n = len(rows(svc, start, end, ["page"], 25000))
    print(f"\nстраниц с показами за 28 дней: {n}")


# ------------------------------------------------------------------ Bing ---

def bing_key():
    k = os.environ.get("BING_KEY")
    if k and k.strip():
        return k.strip()
    p = os.path.expanduser("~/.ssh/bing-webmaster.key")
    if os.path.exists(p):
        with open(p, encoding="utf-8") as f:
            return f.read().strip()
    return None


def bing_call(method, key, **params):
    """Запрос к Bing. Через curl, как и карта сайта: на голый urllib отвечают не всегда."""
    q = "&".join(f"{k}={v}" for k, v in
                 {"siteUrl": BING_SITE, **params, "apikey": key}.items())
    out = subprocess.run(["curl", "-s", f"{BING_API}/{method}?{q}"],
                         capture_output=True, text=True, encoding="utf-8").stdout
    try:
        data = json.loads(out)
    except Exception:
        print(f"  {method}: ответ не разобрался ({out[:80]})")
        return None
    if isinstance(data, dict) and "ErrorCode" in data:
        print(f"  {method}: отказ — {data.get('Message', data['ErrorCode'])}")
        return None
    return data.get("d")


def bing_date(v):
    """Bing отдаёт время как /Date(1789689600000)/ — миллисекунды UTC."""
    m = re.search(r"\((-?\d+)", str(v))
    if not m:
        return None
    return dt.datetime.fromtimestamp(int(m.group(1)) / 1000, dt.timezone.utc).date()


def bing_report():
    key = bing_key()
    if not key:
        print("=== BING === ключа нет (BING_KEY или ~/.ssh/bing-webmaster.key) — пропускаю")
        return

    days = bing_call("GetRankAndTrafficStats", key) or []
    days = sorted([r for r in days if bing_date(r.get("Date"))], key=lambda r: bing_date(r["Date"]))

    print("=== BING: ПО ДНЯМ (последние 14) ===")
    if not days:
        print("  данных нет")
    for r in days[-14:]:
        print(f"  {bing_date(r['Date'])}  кликов {r.get('Clicks', 0):>4}"
              f"  показов {r.get('Impressions', 0):>6}")

    print("\n=== BING: НЕДЕЛЯ К НЕДЕЛЕ ===")
    now, was = days[-7:], days[-14:-7]
    if now and was:
        def line(name, field):
            a, b = sum(r.get(field, 0) for r in now), sum(r.get(field, 0) for r in was)
            # Периоды бывают разной длины (данных всего несколько дней) — судим по среднему за день.
            pa, pb = a / len(now), b / len(was)
            print(f"  {name:<18} {a:>6} за {len(now)} дн.  было {b:>6} за {len(was)} дн."
                  f"   в день {pa:.1f} против {pb:.1f}  {'лучше' if pa >= pb else 'хуже'}")
        line("клики", "Clicks")
        line("показы", "Impressions")
    else:
        print("  сравнивать не с чем")

    # Запросы и страницы Bing отдаёт одной сводкой, без разбивки по дням.
    for title, method in (("ЗАПРОСЫ", "GetQueryStats"), ("СТРАНИЦЫ", "GetPageStats")):
        data = bing_call(method, key) or []
        data.sort(key=lambda r: -r.get("Impressions", 0))
        when = bing_date(data[0]["Date"]) if data else "?"
        print(f"\n=== BING: {title} (сводка на {when}, по показам) ===")
        for r in data[:15 if method == "GetQueryStats" else 12]:
            name = str(r.get("Query", ""))
            if method == "GetPageStats":
                name = short_url(name)
            print(f"  поз {r.get('AvgImpressionPosition', 0):>5.1f}"
                  f"  показов {r.get('Impressions', 0):>5}"
                  f"  кликов {r.get('Clicks', 0):>3}   {name[:60]}")

    crawl = bing_call("GetCrawlStats", key) or []
    crawl = sorted([r for r in crawl if bing_date(r.get("Date"))], key=lambda r: bing_date(r["Date"]))
    if crawl:
        c = crawl[-1]
        print(f"\n=== BING: ОБХОД И ИНДЕКС ({bing_date(c['Date'])}) ===")
        print(f"  в индексе {c.get('InIndex', 0)} страниц, обойдено за день {c.get('CrawledPages', 0)}")
        print(f"  ошибок обхода {c.get('CrawlErrors', 0)}, 5xx {c.get('Code5xx', 0)},"
              f" 4xx {c.get('Code4xx', 0)}, закрыто robots.txt {c.get('BlockedByRobotsTxt', 0)}")

    links = bing_call("GetLinkCounts", key, page=0)
    if isinstance(links, dict):
        found = links.get("Links") or []
        total = sum(l.get("Count", 0) for l in found)
        print(f"\nвходящих ссылок Bing видит: {total}"
              + ("   ← это и есть главная жалоба Bing на сайт" if total == 0 else ""))


def index_sample(svc, n=15):
    # Карту качаем curl-ом: на голый urllib Cloudflare отвечает 403.
    xml = subprocess.run(["curl", "-s", "-A", "Mozilla/5.0", "https://counterplays.com/sitemap.xml"],
                         capture_output=True, text=True, encoding="utf-8").stdout
    urls = re.findall(r"<loc>([^<]+)</loc>", xml)
    if not urls:
        sys.exit("карта сайта не прочиталась")
    random.seed(7)  # выборка та же при повторах — видно движение, а не разброс
    sample = ["https://counterplays.com", "https://counterplays.com/tier-list",
              "https://counterplays.com/counters"] + random.sample(urls, max(0, n - 3))

    print(f"в карте сайта {len(urls)} адресов; проверяю {len(sample)}\n")
    tally = {}
    for u in sample:
        try:
            res = svc.urlInspection().index().inspect(
                body={"inspectionUrl": u, "siteUrl": SITE}).execute()
            s = res["inspectionResult"]["indexStatusResult"]
            verdict = s.get("coverageState", "?")
            crawled = s.get("lastCrawlTime", "не обходился")[:10]
            tally[verdict] = tally.get(verdict, 0) + 1
            short = u.replace("https://counterplays.com", "") or "/"
            print(f"  {short[:44]:<44} {verdict[:38]:<38} обход {crawled}")
        except Exception as e:
            print(f"  {u[:44]:<44} ОШИБКА {str(e)[:60]}")
    print("\nитого:")
    for k, v in sorted(tally.items(), key=lambda x: -x[1]):
        print(f"  {v:>2} — {k}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="Позиции сайта в поиске: Google + Bing")
    ap.add_argument("--index", action="store_true", help="проверить индексацию выборки адресов (Google)")
    ap.add_argument("--google", action="store_true", help="только Google")
    ap.add_argument("--bing", action="store_true", help="только Bing")
    args = ap.parse_args()

    if args.index:
        index_sample(service())
    else:
        if not args.bing:
            report(service())
            print()
        if not args.google:
            bing_report()
