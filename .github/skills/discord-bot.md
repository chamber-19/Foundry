# Discord bot patterns

## Architecture

The bot is a standalone Python process using discord.py. It calls the Foundry.Broker REST API over localhost. It does NOT embed ML logic — it's a thin command→API→response relay.

## Command structure

All commands use Discord slash commands (discord.app_commands). Commands are organized into cogs:

- pipeline.py — /status, /pipeline, /embeddings, /forecast, /reindex, /search
- pr.py — /review, /approve, /reject, /scan
- jobs.py — /jobs, /job, /metrics
- schedules.py — /schedules, /workflows, /run-workflow
- utility.py — /brief, /machines

## API calls

Use aiohttp (not requests) for all broker calls. 30-second timeout on API calls, 300-second timeout on subprocess calls.

## PowerShell execution

Use asyncio.create_subprocess_exec with pwsh (PowerShell 7). The repo root comes from FOUNDRY_REPO_ROOT env var.

## Auto-posting

Background tasks poll for job completions (every 30s) and health transitions (every 60s). Track posted job IDs in memory. Daily brief posts at a configurable time.

## Error handling

Every command is wrapped in try/except. Never expose stack traces in Discord. If the broker is unreachable, reply "Broker offline" — don't crash.

## Stateless

The bot has no database. It delegates all state to the broker. In-memory tracking (posted job IDs, last health state) is disposable — restart clears it.
