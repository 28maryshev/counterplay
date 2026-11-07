# -*- coding: utf-8 -*-
"""Заливка файлов в Cloudflare R2 из коллектора.

Зачем вообще: GitHub отдаёт базу медленно и неровно. Замеры с обычного
домашнего канала — GitHub 0.11 МБ/с, сеть Cloudflare 5–6 МБ/с, Data Dragon
9 МБ/с. Разница в полсотни раз, и канал тут ни при чём.

Почему подписываем сами, а не boto3 или rclone: в контейнере коллектора нет ни
того, ни другого, а памяти на машине 256 МБ — ставить ради пяти файлов целый
SDK незачем. S3-подпись версии 4 укладывается в полсотни строк на стандартной
библиотеке.

Настройка — переменными окружения; без них модуль молча выключен, и публикация
идёт как прежде, только в GitHub:
    R2_DATA_ENDPOINT   https://<account>.r2.cloudflarestorage.com
    R2_DATA_BUCKET     имя бакета (ОТДЕЛЬНОГО от бэкапов: этот публичный)
    R2_DATA_KEY_ID     access key id
    R2_DATA_SECRET     secret access key
"""
import datetime as dt
import hashlib
import hmac
import os
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.parse import quote

import requests

REGION = 'auto'
SERVICE = 's3'
TIMEOUT = 300


def configured() -> bool:
    """Настроен ли R2. Нет — вызывающий просто пропускает заливку."""
    return all(os.environ.get(k) for k in
               ('R2_DATA_ENDPOINT', 'R2_DATA_BUCKET', 'R2_DATA_KEY_ID', 'R2_DATA_SECRET'))


def _sign(key: bytes, msg: str) -> bytes:
    return hmac.new(key, msg.encode('utf-8'), hashlib.sha256).digest()


def _signing_key(secret: str, stamp: str) -> bytes:
    k = _sign(('AWS4' + secret).encode('utf-8'), stamp)
    k = _sign(k, REGION)
    k = _sign(k, SERVICE)
    return _sign(k, 'aws4_request')


EMPTY_HASH = hashlib.sha256(b'').hexdigest()


def _signed(method: str, name: str, payload_hash: str, extra: dict | None = None,
            query: dict | None = None) -> tuple[str, dict]:
    """Адрес и заголовки запроса, подписанного S3 v4. name пустой — сам бакет.

    Заголовки из extra входят в подпись (в нижнем регистре, как и прочие).
    """
    endpoint = os.environ['R2_DATA_ENDPOINT'].rstrip('/')
    bucket   = os.environ['R2_DATA_BUCKET']
    key_id   = os.environ['R2_DATA_KEY_ID']
    secret   = os.environ['R2_DATA_SECRET']

    host = endpoint.split('://', 1)[1]
    uri = f'/{bucket}/{name}' if name else f'/{bucket}'
    # Строка запроса для подписи: ключи по порядку, значения в кодировке RFC 3986
    # (косая черта тоже кодируется — иначе подпись не сойдётся).
    qs = '&'.join(f'{quote(k, safe="-_.~")}={quote(str(v), safe="-_.~")}'
                  for k, v in sorted((query or {}).items()))
    url = endpoint + uri + (f'?{qs}' if qs else '')

    now = dt.datetime.now(dt.timezone.utc)
    amz_date = now.strftime('%Y%m%dT%H%M%SZ')
    stamp = now.strftime('%Y%m%d')

    headers = {
        'host': host,
        'x-amz-content-sha256': payload_hash,
        'x-amz-date': amz_date,
        **{k.lower(): v for k, v in (extra or {}).items()},
    }
    signed_headers = ';'.join(sorted(headers))
    canonical_headers = ''.join(f'{k}:{headers[k]}\n' for k in sorted(headers))

    canonical = '\n'.join([
        method, uri, qs, canonical_headers, signed_headers, payload_hash])
    scope = f'{stamp}/{REGION}/{SERVICE}/aws4_request'
    to_sign = '\n'.join([
        'AWS4-HMAC-SHA256', amz_date, scope,
        hashlib.sha256(canonical.encode('utf-8')).hexdigest()])
    signature = hmac.new(_signing_key(secret, stamp),
                         to_sign.encode('utf-8'), hashlib.sha256).hexdigest()

    headers['Authorization'] = (
        f'AWS4-HMAC-SHA256 Credential={key_id}/{scope}, '
        f'SignedHeaders={signed_headers}, Signature={signature}')
    return url, headers


def put(path: Path, name: str, content_type: str = 'application/octet-stream',
        cache_seconds: int = 300) -> None:
    """Залить файл под именем name. Бросает исключение, если не вышло.

    cache_seconds — сколько край Cloudflare держит файл. База меняется раз в
    сутки, но номер версии игроки спрашивают часто: пусть свежая база доезжает
    в пределах минут, а не висит на краю сутками.
    """
    body = path.read_bytes()
    url, headers = _signed('PUT', name, hashlib.sha256(body).hexdigest(), {
        'content-type': content_type,
        'cache-control': f'public, max-age={cache_seconds}',
    })
    r = requests.put(url, data=body, headers=headers, timeout=TIMEOUT)
    if r.status_code not in (200, 201):
        raise RuntimeError(f'R2 {name}: HTTP {r.status_code} {r.text[:200]}')


def put_file(path: Path, name: str, content_type: str = 'application/octet-stream',
             cache_seconds: int = 300, disposition: str | None = None) -> None:
    """Как put, но файл идёт потоком, а не целиком в память.

    Нужен большим файлам: полный пакет программы весит ~75 МБ, а памяти на
    машине 256 МБ — put прочитал бы его в память целиком. Хэш для подписи
    считается вторым проходом по диску. disposition — заголовок
    Content-Disposition, который R2 потом отдаёт с файлом (установщику —
    attachment, чтобы браузер его скачивал).
    """
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    extra = {
        'content-type': content_type,
        'cache-control': f'public, max-age={cache_seconds}',
        'content-length': str(path.stat().st_size),
    }
    if disposition:
        extra['content-disposition'] = disposition
    url, headers = _signed('PUT', name, h.hexdigest(), extra)
    with open(path, 'rb') as f:
        r = requests.put(url, data=f, headers=headers, timeout=TIMEOUT)
    if r.status_code not in (200, 201):
        raise RuntimeError(f'R2 {name}: HTTP {r.status_code} {r.text[:200]}')


def head(name: str) -> int | None:
    """Размер файла в бакете или None, если его нет. Прямо в хранилище, мимо
    кэша Cloudflare: публичный адрес мог бы ответить тем, что лежало раньше."""
    url, headers = _signed('HEAD', name, EMPTY_HASH)
    r = requests.head(url, headers=headers, timeout=60)
    if r.status_code == 404:
        return None
    if r.status_code != 200:
        raise RuntimeError(f'R2 HEAD {name}: HTTP {r.status_code}')
    return int(r.headers.get('content-length', '0'))


def get(name: str) -> bytes | None:
    """Небольшой файл из бакета целиком (фид обновлений) или None, если его нет."""
    url, headers = _signed('GET', name, EMPTY_HASH)
    r = requests.get(url, headers=headers, timeout=60)
    if r.status_code == 404:
        return None
    if r.status_code != 200:
        raise RuntimeError(f'R2 GET {name}: HTTP {r.status_code} {r.text[:200]}')
    return r.content


def list_keys(prefix: str) -> list[str]:
    """Имена файлов бакета с этим началом (ListObjectsV2, по тысяче за запрос)."""
    keys: list[str] = []
    token = None
    while True:
        query = {'list-type': '2', 'prefix': prefix}
        if token:
            query['continuation-token'] = token
        url, headers = _signed('GET', '', EMPTY_HASH, query=query)
        r = requests.get(url, headers=headers, timeout=60)
        if r.status_code != 200:
            raise RuntimeError(f'R2 LIST {prefix}: HTTP {r.status_code} {r.text[:200]}')
        root = ET.fromstring(r.content)
        ns = root.tag[:root.tag.index('}') + 1] if root.tag.startswith('{') else ''
        keys += [k.text or '' for k in root.iter(f'{ns}Key')]
        if (root.findtext(f'{ns}IsTruncated') or '').lower() != 'true':
            return keys
        token = root.findtext(f'{ns}NextContinuationToken')
        if not token:
            return keys


def delete(name: str) -> None:
    """Удалить файл. Нет такого — не ошибка (S3 отвечает 204 и так)."""
    url, headers = _signed('DELETE', name, EMPTY_HASH)
    r = requests.delete(url, headers=headers, timeout=60)
    if r.status_code not in (200, 204, 404):
        raise RuntimeError(f'R2 DELETE {name}: HTTP {r.status_code} {r.text[:200]}')
