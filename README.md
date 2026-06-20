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
├── *.cs                  # C# app — LCU connector + recommendation engine
├── Counterplay.csproj
├── test-draft/           # Browser-based draft sandbox (no build needed)
│   ├── index.html
│   ├── engine.js
│   ├── app.js
│   └── styles.css
└── pipeline/
    ├── collect.py        # Fetches match history via MATCH-V5, builds SQLite DB
    └── data.db           # Aggregated stats (not included in repo, generate locally)
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

## Running the web prototype

Open `test-draft/index.html` in a browser. No build step, no dependencies. Lets you manually set up any draft scenario and see how the recommendation engine responds. Uses synthetic win rate data (deterministic from champion IDs) — real numbers come from the data pipeline.

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
