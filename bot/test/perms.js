// Проверка эффективных прав бота в настроенных каналах (без постинга).
require('dotenv').config();
const t = (process.env.DISCORD_TOKEN || '').trim();
const h = { Authorization: 'Bot ' + t };
const api = (p) => fetch('https://discord.com/api/v10' + p, { headers: h }).then(async (r) => {
  if (!r.ok) throw new Error(`${p} -> ${r.status}`);
  return r.json();
});

const P = {
  VIEW_CHANNEL: 1n << 10n,
  SEND_MESSAGES: 1n << 11n,
  EMBED_LINKS: 1n << 14n,
  READ_MESSAGE_HISTORY: 1n << 16n,
  SEND_MESSAGES_IN_THREADS: 1n << 38n
};

(async () => {
  const guildId = process.env.GUILD_ID.trim();
  const botId = process.env.CLIENT_ID.trim();
  const [guild, roles, member, channels] = await Promise.all([
    api(`/guilds/${guildId}`),
    api(`/guilds/${guildId}/roles`),
    api(`/guilds/${guildId}/members/${botId}`),
    api(`/guilds/${guildId}/channels`)
  ]);

  const roleMap = new Map(roles.map((r) => [r.id, BigInt(r.permissions)]));
  let base = roleMap.get(guildId) ?? 0n; // @everyone
  for (const rid of member.roles) base |= roleMap.get(rid) ?? 0n;

  const effective = (ch) => {
    let perms = base;
    const ov = ch.permission_overwrites ?? [];
    const ev = ov.find((o) => o.id === guildId);
    if (ev) perms = (perms & ~BigInt(ev.deny)) | BigInt(ev.allow);
    let allow = 0n, deny = 0n;
    for (const o of ov) if (o.type === 0 && member.roles.includes(o.id)) { allow |= BigInt(o.allow); deny |= BigInt(o.deny); }
    perms = (perms & ~deny) | allow;
    const mo = ov.find((o) => o.type === 1 && o.id === botId);
    if (mo) perms = (perms & ~BigInt(mo.deny)) | BigInt(mo.allow);
    return perms;
  };

  const targets = [
    ['CH_META_RADAR (#meta-radar)', process.env.CH_META_RADAR, ['VIEW_CHANNEL', 'SEND_MESSAGES', 'EMBED_LINKS']],
    ['CH_DRAFT_DUELS (#draft-duels)', process.env.CH_DRAFT_DUELS, ['VIEW_CHANNEL', 'SEND_MESSAGES', 'EMBED_LINKS', 'READ_MESSAGE_HISTORY']],
    ['CH_SUBMIT_FINDS (#submit-finds)', process.env.CH_SUBMIT_FINDS, ['VIEW_CHANNEL', 'SEND_MESSAGES_IN_THREADS', 'READ_MESSAGE_HISTORY']],
    ['CH_HALL_OF_FAME (#hall-of-fame)', process.env.CH_HALL_OF_FAME, ['VIEW_CHANNEL', 'SEND_MESSAGES', 'EMBED_LINKS']]
  ];
  for (const [label, id, needs] of targets) {
    const ch = channels.find((c) => c.id === (id || '').trim());
    if (!ch) { console.log(`${label}: канал не найден`); continue; }
    const perms = effective(ch);
    const missing = needs.filter((n) => (perms & P[n]) === 0n);
    console.log(`${label}: ${missing.length ? 'НЕ ХВАТАЕТ -> ' + missing.join(', ') : 'OK ✅'}`);
  }
})().catch((e) => console.error('FAIL:', e.message));
