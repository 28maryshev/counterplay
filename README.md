# Counterplay

Windows desktop app that gives champion pick recommendations during the draft phase of League of Legends. Reads the current draft state from the League Client (LCU API) and suggests picks based on win rates, counter-matchup data, and team synergy.

> Not affiliated with or endorsed by Riot Games.

## What it does

- Connects to the League client automatically when it's running
- Detects your role, your teammates' picks, and enemy picks in real time
- Suggests the best picks for your role based on:
  - Base win rate for the patch
  - Counter-matchup score against your lane opponent (highest weight)
  - Average matchup score against the rest of the enemy team
  - Synergy with your teammates
- Shows a brief reason for each recommendation
- Works during champion select only — no in-game overlay or assistance
- Enemy summoner names are hidden in Ranked Solo/Duo (shown as Enemy 1, Enemy 2, etc.)

## Structure

```
Counterplay/
├── src/                  # C# application (.NET 8, WPF)
│   ├── App/              # entry point, autostart, settings
│   ├── Client/           # League Client (LCU) — connection, draft parsing, rune import
│   ├── Engine/           # pick scoring: matchups, synergy, champion traits, pools
│   ├── Data/             # stats database, Data Dragon, session tracking, telemetry
│   ├── Ui/               # overlay window, pool settings, icons, localization
│   └── Dev/              # sandboxes for testing without a live client
├── assets/               # fonts, icons, i18n strings (embedded into the build)
├── pipeline/             # Python: match collection and stat aggregation
│   ├── collect.py        # fetches matches via MATCH-V5 into SQLite
│   ├── publish_data.py   # publishes the slim database as a GitHub release
│   └── export_*.py       # exports for the website (draft, tier list, runes)
├── bot/                  # Discord bot: meta radar, data freshness, release notes
├── build/                # release scripts (Velopack installer, data publishing)
└── docs/                 # design notes and specifications
```

## Running the C# app

Requires .NET 8 and a running League of Legends client.

```
cd C:\Counterplay
dotnet run
```

The app reads the lockfile from the default League install path. To use a custom path:

```
dotnet run -- "D:\path\to\lockfile"
```

It will print draft state and recommendations to the console as you go through champion select.

## Testing without a live client

```
dotnet run test
```

Opens a sandbox where you place picks and bans by hand and watch the recommendations react — the same engine and the same database as in a real draft, no League client needed.

## Building the stats database

Requires Python 3 and a Riot API key.

```
cd pipeline
pip install riotwatcher
python collect.py --key YOUR_API_KEY --region euw1 --tier emerald --games 5000
```

This pulls ranked matches from MATCH-V5, aggregates win rates, counter-matchups, and synergy stats into `data.db`. Raw match data is not stored — only aggregated counts per champion/role/patch.

## Tech

- C# / .NET 8 — LCU integration, recommendation engine, future WPF overlay
- Python — offline data pipeline (riotwatcher, SQLite)
- Riot APIs used: match-v5, league-v4, summoner-v4, Data Dragon
