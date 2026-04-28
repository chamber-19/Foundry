# Foundry — Agent Broker

Foundry is the internal **agent broker** for the [chamber-19](https://github.com/chamber-19) tool family. It receives GitHub webhook events and Discord slash commands, routes them to local-LLM agents via [Ollama](https://ollama.com), and posts structured output back to GitHub PRs and Discord channels.

It is **not** a public-facing service, not a generic AI platform, and not a replacement for the Tauri apps in the chamber-19 family. It is a focused orchestration layer that lives alongside those apps.

## Where Foundry fits in the Chamber 19 family

```text
Operator (Discord)
       │  slash commands
       ▼
  bot/foundry_bot.py  ──HTTP──▶  Foundry.Broker  (localhost:57420)
                                       │
                     ┌─────────────────┼──────────────────┐
                     ▼                 ▼                  ▼
               Ollama (LLM)     LiteDB (jobs)      Qdrant (vectors)
               Foundry.Core     knowledge index    semantic search
```

The broker receives events, enqueues async jobs, dispatches work to agents, and posts results. Agents are deterministic pre-check → LLM structured extraction → rule-engine verdict pipelines.

For org-wide conventions see [chamber-19/.github](https://github.com/chamber-19/.github).

## What Foundry does

### Broker API

- ASP.NET Core minimal API on `localhost:57420`
- Async job queue with LiteDB persistence
- Job scheduling and workflow templates
- Health checks across all subsystems

### Knowledge / RAG stack

- Qdrant-backed document indexing via Ollama embeddings
- Semantic search over indexed knowledge documents
- RAG context building for agent and operator queries

### Discord bot

- Sole human-operator interface (`bot/foundry_bot.py`)
- Routes all LLM inference through the broker and Ollama
- Background tasks post job completions and health transitions automatically

### Agent layer (in progress)

- `IAgent` contract for deterministic pre-check → LLM → rule-engine pipelines
- First agent: `dep-reviewer` — triages Dependabot PRs across chamber-19 repos
- Shadow-mode evaluation before any write actions are enabled

## Project layout

```text
Foundry/
├── src/
│   ├── Foundry.Core/           # Shared models + services (class library)
│   │   ├── Models/             # Data models (jobs, settings, knowledge)
│   │   └── Services/           # Services, coordinators, persistence
│   └── Foundry.Broker/         # ASP.NET Core API host
│       └── Endpoints/          # HTTP endpoint definitions
├── tests/
│   └── Foundry.Core.Tests/     # xUnit tests
├── bot/                        # Discord bot — sole operator interface
├── evals/                      # Agent evaluation harness + golden datasets
├── docs/                       # Architecture, conventions, library decisions
└── Foundry.sln
```

## Quick start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Ollama](https://ollama.com) running on `http://127.0.0.1:11434`
- Python 3.10+ (for the Discord bot)
- Qdrant (optional — semantic search degrades to keyword search without it)

### Build and run the broker

```text
dotnet restore Foundry.sln
dotnet build Foundry.sln
dotnet run --project src/Foundry.Broker
```

The broker starts on `http://127.0.0.1:57420`.

### Run tests

```text
dotnet test Foundry.sln
```

### Run the Discord bot

```text
cd bot
pip install -r requirements.txt
cp bot_config.example.json bot_config.json
# Edit bot_config.json — add your Discord token and channel IDs (never commit this file)
python foundry_bot.py
```

## Key endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Basic liveness check |
| `GET` | `/api/health` | Detailed subsystem health (Ollama, LiteDB, job worker) |
| `GET` | `/api/state` | Current broker state |
| `POST` | `/api/knowledge/index` | Index knowledge documents |
| `GET` | `/api/knowledge/index-status` | Knowledge index status |
| `POST` | `/api/knowledge/search` | Semantic search |
| `GET` | `/api/jobs` | List recent jobs |
| `GET` | `/api/jobs/{jobId}` | Get job by ID |
| `GET` | `/api/schedules` | List job schedules |
| `POST` | `/api/workflows` | Create workflow template |

## Configuration

Settings are loaded from `foundry.settings.json`. Local overrides go in `foundry.settings.local.json` (gitignored):

```json
{
  "ollamaEndpoint": "http://127.0.0.1:11434",
  "ollamaModel": "qwen2.5-coder:14b-instruct-q5_K_M",
  "jobRetentionDays": 30,
  "knowledgeLibraryPath": "",
  "stateRootPath": ""
}
```

**Secrets** (`discordBotToken`, `githubAppPrivateKey`) must go in `foundry.settings.local.json` or environment variables — never in the committed settings file.

## Contributing and agent development

See [CONTRIBUTING.md](./CONTRIBUTING.md) for build commands and contributor guidance.

Agent contributors must read [`.github/copilot-instructions.md`](./.github/copilot-instructions.md) — it describes the deterministic pre-check → LLM → rule-engine pattern every agent must follow, eval requirements, and local-LLM constraints.
