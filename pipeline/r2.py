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
from pathlib import Path

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


def put(path: Path, name: str, content_type: str = 'application/octet-stream',
        cache_seconds: int = 300) -> None:
    """Залить файл под именем name. Бросает исключение, если не вышло.

    cache_seconds — сколько край Cloudflare держит файл. База меняется раз в
    сутки, но номер версии игроки спрашивают часто: пусть свежая база доезжает
    в пределах минут, а не висит на краю сутками.
    """
    endpoint = os.environ['R2_DATA_ENDPOINT'].rstrip('/')
    bucket   = os.environ['R2_DATA_BUCKET']
    key_id   = os.environ['R2_DATA_KEY_ID']
    secret   = os.environ['R2_DATA_SECRET']

    host = endpoint.split('://', 1)[1]
    uri = f'/{bucket}/{name}'
    url = endpoint + uri

    body = path.read_bytes()
    payload_hash = hashlib.sha256(body).hexdigest()

    now = dt.datetime.now(dt.timezone.utc)
    amz_date = now.strftime('%Y%m%dT%H%M%SZ')
    stamp = now.strftime('%Y%m%d')

    headers = {
        'host': host,
        'x-amz-content-sha256': payload_hash,
        'x-amz-date': amz_date,
        'content-type': content_type,
        'cache-control': f'public, max-age={cache_seconds}',
    }
    signed_headers = ';'.join(sorted(headers))
    canonical_headers = ''.join(f'{k}:{headers[k]}\n' for k in sorted(headers))

    canonical = '\n'.join([
        'PUT', uri, '', canonical_headers, signed_headers, payload_hash])
    scope = f'{stamp}/{REGION}/{SERVICE}/aws4_request'
    to_sign = '\n'.join([
        'AWS4-HMAC-SHA256', amz_date, scope,
        hashlib.sha256(canonical.encode('utf-8')).hexdigest()])
    signature = hmac.new(_signing_key(secret, stamp),
                         to_sign.encode('utf-8'), hashlib.sha256).hexdigest()

    headers['Authorization'] = (
        f'AWS4-HMAC-SHA256 Credential={key_id}/{scope}, '
        f'SignedHeaders={signed_headers}, Signature={signature}')

    r = requests.put(url, data=body, headers=headers, timeout=TIMEOUT)
    if r.status_code not in (200, 201):
        raise RuntimeError(f'R2 {name}: HTTP {r.status_code} {r.text[:200]}')
