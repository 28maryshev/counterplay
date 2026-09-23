# -*- coding: utf-8 -*-
"""Позиции в поиске Google — из Search Console.

Чего нет в своей аналитике сайта: та видит только тех, кто ДОШЁЛ. Здесь видно
то, что было до клика — по каким запросам нас показывают, на каком месте, и
берёт ли Google наши страницы вообще.

Запуск:
    python ops/seo.py            позиции, динамика недели, запросы, страницы
    python ops/seo.py --index    выборочная проверка индексации (15 адресов)

Ключ сервисного аккаунта (counterplay-reader) НЕ лежит в репозитории — он
публичный. Путь берётся из переменной окружения GSC_KEY, иначе ищется файл
~/.ssh/counterplay-*.json. Где он на самом деле — записано в памяти проекта.
"""
import argparse
import datetime as dt
import glob
import os
import random
import re
import subprocess
import sys

SITE = "sc-domain:counterplays.com"
SCOPE = ["https://www.googleapis.com/auth/webmasters.readonly"]


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


def report(svc):
    # Google отдаёт данные с задержкой в пару дней — вчерашний день уже полный.
    end = dt.date.today() - dt.timedelta(days=1)
    start = end - dt.timedelta(days=27)

    print("=== ПО ДНЯМ (последние 14) ===")
    for r in rows(svc, start, end, ["date"], 40)[-14:]:
        print(f"  {r['keys'][0]}  кликов {r['clicks']:>4.0f}  показов {r['impressions']:>6.0f}"
              f"  CTR {r['ctr'] * 100:>5.1f}%  позиция {r['position']:>5.1f}")

    def total(s, e):
        rs = rows(svc, s, e, [], 1)
        return rs[0] if rs else None

    now = total(end - dt.timedelta(days=6), end)
    was = total(end - dt.timedelta(days=13), end - dt.timedelta(days=7))
    print("\n=== НЕДЕЛЯ К НЕДЕЛЕ ===")
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

    print("\n=== ЗАПРОСЫ (28 дней, по показам) ===")
    for r in rows(svc, start, end, ["query"], 15):
        print(f"  поз {r['position']:>5.1f}  показов {r['impressions']:>5.0f}"
              f"  кликов {r['clicks']:>3.0f}   {r['keys'][0][:60]}")

    print("\n=== СТРАНИЦЫ (28 дней, по показам) ===")
    for r in rows(svc, start, end, ["page"], 12):
        p = r["keys"][0].replace("https://counterplays.com", "") or "/"
        print(f"  поз {r['position']:>5.1f}  показов {r['impressions']:>5.0f}"
              f"  кликов {r['clicks']:>3.0f}   {p[:60]}")

    n = len(rows(svc, start, end, ["page"], 25000))
    print(f"\nстраниц с показами за 28 дней: {n}")


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
    ap = argparse.ArgumentParser(description="Позиции сайта в Google (Search Console)")
    ap.add_argument("--index", action="store_true", help="проверить индексацию выборки адресов")
    args = ap.parse_args()
    svc = service()
    index_sample(svc) if args.index else report(svc)
