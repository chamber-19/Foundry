# Foundry — Agent Broker

> **Status:** Foundry is mid-pivot from an ML scoring pipeline to an internal
> agent broker for the chamber-19 family. The README reflects the *target*
> shape; some scaffolding for the old design may remain in-tree pending
> follow-up cleanup PRs.

Foundry is a focused agent broker for the chamber-19 tool family. It provides a knowledge/RAG stack for semantic search and document indexing, a Discord bot as the sole operator interface, and an ASP.NET Core broker API with an async job queue.

## What Foundry Does

### Knowledge / RAG Stack
- Qdrant-backed document indexing via Ollama embeddings
- Semantic search over knowledge documents
- RAG context building for operator queries

### Broker API
- ASP.NET Core minimal API on `localhost:57420`
- Async job queue with LiteDB persistence
- Job scheduling and workflow templates
- Health checks

### Discord Bot
- Sole operator interface
- Routes all LLM inference through the broker/Ollama

## Project Layout

```
Foundry/
├── src/
│   ├── Foundry.Core/           # Shared models + services (class library)
│   │   ├── Models/             # Data models (jobs, settings, knowledge)
│   │   └── Services/           # Services, coordinators, persistence
│   └── Foundry.Broker/         # ASP.NET Core API host
│       └── Endpoints/          # HTTP endpoint definitions
├── tests/
│   └── Foundry.Core.Tests/     # xUnit tests
├── scripts/
│   ├── rag/                    # RAG pipeline (indexer, search, query)
│   ├── automation/             # PR review, issue scanning, Discord briefs, scheduled tasks
│   └── commands/               # Operator commands (review, approve, reject, scan, status)
├── bot/                        # Discord bot — sole operator interface
├── evals/                      # Agent evaluation harness (being repurposed)
├── schemas/                    # Feature schema (frozen)
├── docs/                       # Architecture, conventions, roadmap
└── Foundry.sln                 # Solution file
```

## Quick Start

### Prerequisites
- .NET 10 SDK
- Python 3.10+ (for RAG scripts)
- Ollama (for LLM and embedding inference)
- Qdrant (optional, for vector search)

### Build & Run
```bash
dotnet restore Foundry.sln
dotnet build Foundry.sln
dotnet run --project src/Foundry.Broker
```

The broker starts on `http://127.0.0.1:57420`.

### Run Tests
```bash
dotnet test Foundry.sln
```

### Discord Bot
```bash
cd bot
pip install -r requirements.txt
cp bot_config.example.json bot_config.json
# Edit bot_config.json with your Discord token and channel IDs
python foundry_bot.py
```

## Key Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Basic health check |
| `GET` | `/api/health` | Detailed subsystem health |
| `GET` | `/api/state` | Current broker state |
| `POST` | `/api/ml/index-knowledge` | Index knowledge documents |
| `GET` | `/api/knowledge/index-status` | Knowledge index status |
| `POST` | `/api/knowledge/search` | Semantic search |
| `GET` | `/api/jobs` | List recent jobs |
| `GET` | `/api/jobs/{jobId}` | Get job by ID |
| `GET` | `/api/schedules` | List job schedules |
| `POST` | `/api/workflows` | Create workflow template |

## Configuration

Settings are loaded from `foundry.settings.json` (or `foundry.settings.local.json` for overrides):

```json
{
  "ollamaEndpoint": "http://127.0.0.1:11434",
  "mlModel": "qwen3:8b",
  "jobRetentionDays": 30,
  "knowledgeLibraryPath": "",
  "stateRootPath": "",
  "discordBotToken": ""
}
```
