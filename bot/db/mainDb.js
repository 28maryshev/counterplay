// Снапшот основной базы Counterplay (read-only) + горячая подмена файла.
// Файл лежит в DATA_DIR/data.db и обновляется lib/dataSync.js.
const path = require('path');
const fs = require('fs');
const Database = require('better-sqlite3');
const config = require('../config');
const logger = require('../lib/logger');

const DB_FILE = path.join(config.dataDir, 'data.db');

let db = null;
let gen = 0; // растёт при каждом переоткрытии — для инвалидации кэшей dataLayer

function open() {
  close();
  if (!fs.existsSync(DB_FILE)) {
    logger.warn(`mainDb: snapshot not found at ${DB_FILE} — data features disabled until sync`);
    return false;
  }
  db = new Database(DB_FILE, { readonly: true, fileMustExist: true });
  gen++;
  return true;
}

function close() {
  if (db) {
    try {
      db.close();
    } catch {
      /* уже закрыта */
    }
    db = null;
  }
}

module.exports = {
  open,
  close,
  reopen: () => open(),
  get: () => db,
  generation: () => gen,
  file: DB_FILE
};
