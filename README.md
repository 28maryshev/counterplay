# Counterplay

Counterplay is a Windows application for League of Legends that helps you choose
a champion during champion select. It reads the draft from the game client as it
happens and shows which picks work best against the enemy team and alongside your
own, together with the build, runes, summoner spells and skill order for the pick
you settle on.

Website: [counterplays.com](https://counterplays.com)

> Counterplay isn't endorsed by Riot Games and doesn't reflect the views or
> opinions of Riot Games or anyone officially involved in producing or managing
> Riot Games properties. League of Legends and Riot Games are trademarks or
> registered trademarks of Riot Games, Inc. League of Legends © Riot Games, Inc.

## What it does

- Recognises champion select automatically while the client is running
- Reads your assigned role, your team's picks and the enemy picks as they happen
- Ranks the champions available to you for the role you are playing, weighing the
  matchup against your lane opponent most heavily, then the rest of the enemy
  team, then how well the pick fits your own team
- Gives a short reason for every recommendation, so the advice can be judged
  rather than taken on faith
- Shows the build, runes, summoner spells and skill order for the champion you
  are about to lock in, and can send the rune page to the client in one click
- Tracks your rank over a session and shows what the next game is worth
- Adapts to your champion pool, so the advice stays inside what you actually play
- Speaks English and Russian

## What it deliberately does not do

These limits come from Riot's rules for third-party applications, and the
application is built around them rather than against them.

- Advice appears **only during champion select**. Once the game starts, the
  overlay stops advising — there is no in-game assistance of any kind.
- Nothing is read from the game's memory or process. The application talks only
  to the official client API on your own machine.
- In Ranked Solo/Duo, enemy summoner names are never shown — opponents appear as
  Enemy 1, Enemy 2 and so on.
- No account credentials are asked for, stored or transmitted. The application
  cannot log in, queue, play or act on your behalf.

## Requirements

- Windows 10 or 11
- League of Legends installed, with the client running

## Install

Download the latest installer from the
[Releases](https://github.com/28maryshev/counterplay/releases) page and run it.
Updates are delivered automatically; statistics refresh on their own as each
patch settles.

## Statistics

Recommendations are computed from ranked matches collected through Riot's public
match API and aggregated by champion, role, rank and patch. Individual matches
are not retained — only the aggregate counts the recommendations are built from.
Numbers are recalculated as a patch matures, and the application holds back a new
patch until there is enough of it to say anything meaningful.

## Support

Questions, bug reports and suggestions: [counterplays.com](https://counterplays.com).

## License

Proprietary. Copyright © 2026 Counterplay. All rights reserved. See
[LICENSE](LICENSE) — the source is published for transparency, not for reuse:
copying, modification, redistribution and derivative works are not permitted.
