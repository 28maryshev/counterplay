// Логгер: консоль (docker logs) + файл logs/bot.log с простой ротацией на 5 MB.
const fs = require('fs');
const path = require('path');

const LOG_DIR = path.join(__dirname, '..', 'logs');
const LOG_FILE = path.join(LOG_DIR, 'bot.log');
const MAX_BYTES = 5 * 1024 * 1024;

function writeLine(line) {
  try {
    fs.mkdirSync(LOG_DIR, { recursive: true });
    try {
      const st = fs.statSync(LOG_FILE);
      if (st.size > MAX_BYTES) fs.renameSync(LOG_FILE, LOG_FILE + '.1');
    } catch {
      /* файла ещё нет */
    }
    fs.appendFileSync(LOG_FILE, line + '\n');
  } catch {
    /* файловый лог не критичен */
  }
}

function log(level, ...args) {
  const msg = args
    .map((a) => (a instanceof Error ? a.stack || a.message : typeof a === 'object' ? JSON.stringify(a) : String(a)))
    .join(' ');
  const line = `${new Date().toISOString()} [${level}] ${msg}`;
  // eslint-disable-next-line no-console
  console[level === 'ERROR' ? 'error' : 'log'](line);
  writeLine(line);
}

module.exports = {
  info: (...a) => log('INFO', ...a),
  warn: (...a) => log('WARN', ...a),
  error: (...a) => log('ERROR', ...a)
};
