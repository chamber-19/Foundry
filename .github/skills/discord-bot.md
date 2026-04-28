# Discord bot patterns

## Architecture

The bot is a standalone Python process using discord.py. It calls the Foundry.Broker REST API over localhost. It does NOT embed LLM logic — it is a thin command → API → response relay.

## Command structure

All commands use Discord slash commands (`discord.app_commands`). Commands are organized into cogs:

- `jobs.py` — `/jobs`, `/job`, `/metrics`
- `schedules.py` — `/schedules`, `/workflows`, `/run-workflow`
- `knowledge.py` — `/reindex`, `/search`
- `utility.py` — `/brief`, `/health`, `/status`

## API calls

Use `aiohttp` (not `requests`) for all broker calls. 30-second timeout on API calls.

## Auto-posting

Background tasks poll for job completions (every 30 s) and health transitions (every 60 s). Track posted job IDs in memory. Daily brief posts at a configurable time.

## Error handling

Every command is wrapped in `try/except`. Never expose stack traces in Discord. If the broker is unreachable, reply "Broker offline" — do not crash.

## Stateless

The bot has no database. It delegates all state to the broker. In-memory tracking (posted job IDs, last health state) is disposable — a restart clears it.
