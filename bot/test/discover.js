// Проверка токена + список серверов/каналов бота. Токен не печатаем.
require('dotenv').config();
const t = (process.env.DISCORD_TOKEN || '').trim();
const h = { Authorization: 'Bot ' + t };
const api = (p) => fetch('https://discord.com/api/v10' + p, { headers: h }).then(async (r) => {
  if (!r.ok) throw new Error(`${p} -> ${r.status} ${await r.text()}`);
  return r.json();
});

(async () => {
  if (!t) { console.log('TOKEN: empty'); return; }
  console.log('token format:', /^[\w-]+\.[\w-]+\.[\w-]+$/.test(t) ? 'looks like a bot token' : 'UNEXPECTED (not xxx.yyy.zzz)');
  const me = await api('/users/@me');
  console.log('bot user:', me.username + '#' + me.discriminator, '| id:', me.id);
  const guilds = await api('/users/@me/guilds');
  if (!guilds.length) { console.log('guilds: NONE — бот ещё не приглашён на сервер'); return; }
  for (const g of guilds) {
    console.log(`guild: "${g.name}" id=${g.id}`);
    const chans = await api(`/guilds/${g.id}/channels`);
    for (const c of chans.sort((a, b) => (a.position ?? 0) - (b.position ?? 0))) {
      const type = { 0: 'text', 2: 'voice', 4: 'category', 5: 'announcement', 15: 'forum' }[c.type] ?? c.type;
      console.log(`  [${type}] #${c.name} id=${c.id}`);
    }
  }
})().catch((e) => console.error('FAIL:', e.message));
