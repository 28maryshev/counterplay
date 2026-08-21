// Google Analytics 4 (Data API) — источники визитов для ежедневной сводки.
//
// Почему только в сводке, а не в ленте установок: GA отдаёт ТОЛЬКО агрегаты.
// Данных по конкретному человеку там нет (для этого нужен экспорт в BigQuery),
// поэтому подписать отдельную установку «пришёл из TikTok» через GA нельзя —
// это делает сопоставление по нашей собственной базе загрузок.
//
// Авторизация — сервисный аккаунт: подписываем JWT ключом из файла и меняем
// его на токен. Отдельная библиотека ради этого не нужна, хватает crypto.
const crypto = require('crypto');
const fs = require('fs');
const logger = require('./logger');

const TOKEN_URL = 'https://oauth2.googleapis.com/token';
const SCOPE = 'https://www.googleapis.com/auth/analytics.readonly';

let cached = { token: null, expires: 0 };

const b64 = (buf) => Buffer.from(buf).toString('base64url');

async function accessToken(keyPath) {
  if (cached.token && Date.now() < cached.expires) return cached.token;

  const key = JSON.parse(fs.readFileSync(keyPath, 'utf8'));
  const now = Math.floor(Date.now() / 1000);
  const header = b64(JSON.stringify({ alg: 'RS256', typ: 'JWT' }));
  const claims = b64(
    JSON.stringify({
      iss: key.client_email,
      scope: SCOPE,
      aud: TOKEN_URL,
      exp: now + 3600,
      iat: now
    })
  );
  const signature = crypto
    .createSign('RSA-SHA256')
    .update(`${header}.${claims}`)
    .sign(key.private_key, 'base64url');

  const res = await fetch(TOKEN_URL, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer',
      assertion: `${header}.${claims}.${signature}`
    })
  });
  if (!res.ok) throw new Error(`GA token -> ${res.status}`);
  const json = await res.json();

  // Токен живёт час; обновляем за минуту до конца, чтобы не ловить 401 на границе.
  cached = { token: json.access_token, expires: Date.now() + (json.expires_in - 60) * 1000 };
  return cached.token;
}

async function runReport(keyPath, property, body) {
  const token = await accessToken(keyPath);
  const res = await fetch(`https://analyticsdata.googleapis.com/v1beta/properties/${property}:runReport`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });
  if (!res.ok) throw new Error(`GA runReport -> ${res.status}`);
  return res.json();
}

/** Топ источников визитов за сутки: [{ source, sessions, downloads }]. */
async function sourcesLastDay(config, limit = 5) {
  if (!config.gaKeyPath || !config.gaProperty) return null;
  if (!fs.existsSync(config.gaKeyPath)) {
    logger.warn(`ga: key file not found (${config.gaKeyPath}) — GA block disabled`);
    return null;
  }

  const range = [{ startDate: '1daysAgo', endDate: 'today' }];

  const [visits, downloads] = await Promise.all([
    runReport(config.gaKeyPath, config.gaProperty, {
      dateRanges: range,
      dimensions: [{ name: 'sessionSource' }],
      metrics: [{ name: 'sessions' }],
      orderBys: [{ metric: { metricName: 'sessions' }, desc: true }],
      limit
    }),
    // Клики по «Скачать»: то же событие, что шлёт сайт в GA.
    runReport(config.gaKeyPath, config.gaProperty, {
      dateRanges: range,
      dimensions: [{ name: 'sessionSource' }],
      metrics: [{ name: 'eventCount' }],
      dimensionFilter: {
        filter: { fieldName: 'eventName', stringFilter: { value: 'download' } }
      },
      limit: 25
    })
  ]);

  const bySource = new Map();
  for (const row of downloads.rows ?? [])
    bySource.set(row.dimensionValues[0].value, Number(row.metricValues[0].value));

  return (visits.rows ?? []).map((row) => {
    const source = row.dimensionValues[0].value;
    return {
      source: source === '(direct)' ? 'прямой заход' : source,
      sessions: Number(row.metricValues[0].value),
      downloads: bySource.get(source) ?? 0
    };
  });
}

module.exports = { sourcesLastDay };
