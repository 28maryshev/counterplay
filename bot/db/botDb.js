// Собственная база бота (bot.db): дуэли, голоса, очки, лог радара, hall of fame,
// key-value для служебных отметок. Схема применяется идемпотентно при старте.
const fs = require('fs');
const path = require('path');
const Database = require('better-sqlite3');
const config = require('../config');

fs.mkdirSync(path.dirname(path.resolve(config.botDbPath)), { recursive: true });
const db = new Database(config.botDbPath);
db.pragma('journal_mode = WAL');

db.exec(`
CREATE TABLE IF NOT EXISTS kv (
  key TEXT PRIMARY KEY,
  value TEXT
);

CREATE TABLE IF NOT EXISTS radar_log (
  date TEXT,
  type TEXT,
  champion_id INTEGER,
  role TEXT
);

CREATE TABLE IF NOT EXISTS duels (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  date TEXT UNIQUE,
  role TEXT,
  ally_json TEXT,
  enemy_json TEXT,
  options_json TEXT,
  correct INTEGER,
  explanation_json TEXT,
  message_id TEXT,
  revealed INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS duel_votes (
  duel_id INTEGER,
  user_id TEXT,
  choice TEXT,
  voted_at TEXT,
  PRIMARY KEY (duel_id, user_id)
);

CREATE TABLE IF NOT EXISTS duel_scores (
  user_id TEXT,
  week TEXT,
  points INTEGER DEFAULT 0,
  correct INTEGER DEFAULT 0,
  total INTEGER DEFAULT 0,
  PRIMARY KEY (user_id, week)
);

CREATE TABLE IF NOT EXISTS hof_log (
  patch TEXT,
  champion_id INTEGER,
  role TEXT,
  thread_id TEXT,
  PRIMARY KEY (patch, champion_id, role)
);
`);

const kvGetStmt = db.prepare('SELECT value FROM kv WHERE key = ?');
const kvSetStmt = db.prepare(
  'INSERT INTO kv (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value'
);

module.exports = {
  db,
  kvGet: (key) => kvGetStmt.get(key)?.value ?? null,
  kvSet: (key, value) => kvSetStmt.run(key, String(value))
};
