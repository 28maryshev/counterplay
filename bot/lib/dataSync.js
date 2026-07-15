// Синхронизация снапшота базы с GitHub Releases (тег data).
// Владелец публикует data.db + data-version.json скриптом build/publish-data.ps1;
// бот раз в час сверяет version (hash содержимого) и атомарно подменяет файл.
const fs = require('fs');
const path = require('path');
const config = require('../config');
const logger = require('./logger');
const mainDb = require('../db/mainDb');

const MANIFEST_FILE = path.join(config.dataDir, 'data-version.json');

function localManifest() {
  try {
    return JSON.parse(fs.readFileSync(MANIFEST_FILE, 'utf8'));
  } catch {
    return null;
  }
}

async function fetchJson(url) {
  const res = await fetch(url, { redirect: 'follow' });
  if (!res.ok) throw new Error(`GET ${url} -> ${res.status}`);
  return res.json();
}

async function downloadTo(url, dest) {
  const res = await fetch(url, { redirect: 'follow' });
  if (!res.ok) throw new Error(`GET ${url} -> ${res.status}`);
  // Стримим на диск, а не держим весь снапшот (~143 МБ) в памяти: буфер разом
  // выбивал бот за лимит контейнера и ронял его в цикл перезапуска.
  const { Readable } = require('stream');
  const { pipeline } = require('stream/promises');
  await pipeline(Readable.fromWeb(res.body), fs.createWriteStream(dest));
  return fs.statSync(dest).size;
}

/** Проверить удалённую версию и при отличии скачать и подменить снапшот.
 *  Возвращает 'updated' | 'current' | 'failed'. Никогда не бросает. */
async function sync() {
  try {
    fs.mkdirSync(config.dataDir, { recursive: true });
    const remote = await fetchJson(config.dataVersionUrl);
    const local = localManifest();
    if (local && local.version === remote.version && fs.existsSync(mainDb.file)) {
      return 'current';
    }
    logger.info(
      `dataSync: downloading snapshot version=${remote.version} patch=${remote.patch}`
    );
    const tmp = mainDb.file + '.tmp';
    const bytes = await downloadTo(config.dataDbUrl, tmp);
    // Подмена: закрыть соединение, заменить файл, открыть заново.
    mainDb.close();
    fs.renameSync(tmp, mainDb.file);
    fs.writeFileSync(MANIFEST_FILE, JSON.stringify(remote));
    mainDb.open();
    logger.info(`dataSync: snapshot updated (${(bytes / 1e6).toFixed(1)} MB), db reopened`);
    return 'updated';
  } catch (e) {
    logger.warn('dataSync failed (continuing on cached snapshot):', e.message);
    // Гарантировать, что соединение живо, если файл на месте.
    if (!mainDb.get() && fs.existsSync(mainDb.file)) mainDb.open();
    return 'failed';
  }
}

/** При старте: открыть кэш, если есть; иначе попытаться скачать. */
async function ensure() {
  if (fs.existsSync(mainDb.file)) {
    mainDb.open();
    // Фоновая проверка свежести — не блокируем старт.
    sync();
    return true;
  }
  const r = await sync();
  return r === 'updated';
}

module.exports = { sync, ensure, localManifest };
