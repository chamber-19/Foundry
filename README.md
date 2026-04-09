# Foundry — ML Scoring & Reasoning Pipeline

Foundry is a standalone three-engine ML pipeline that produces versioned artifacts for [Suite](https://github.com/Koraji95-coder/Suite). It scores pull requests, generates semantic embeddings for knowledge indexing, and runs time-series accuracy forecasting — all orchestrated through an ASP.NET Core broker API with a Discord bot as the sole operator interface.

## What Foundry Does

### Three-Engine ML Pipeline
The engines run in sequence with explicit cross-feed via an **EngineHandoff** contract:

1. **Scikit-learn** — Clustering, classification, PR scoring, topic grouping, operator readiness prediction
2. **PyTorch** — Semantic embeddings for knowledge indexing via Qdrant, document similarity
3. **TensorFlow** — Time-series accuracy forecasting, plateau detection, anomaly alerts, mastery estimation

Each engine has heuristic fallbacks when its ML library isn't installed.

### PR Scoring Engine
- Deterministic gates: CI pass, duplicate check
- Weighted signals: tests, PR size, commit format, churn risk
- LLM adjustment via local Ollama

### RAG Pipeline
- ChromaDB/Qdrant document indexing + vector search
- Ollama-powered query answering

### Broker API
- ASP.NET Core minimal API on `localhost:57420`
- ML endpoints, job scheduling, health checks
- Async job queue with LiteDB persistence

## Project Layout

```
Foundry/
├── src/
│   ├── Foundry.Core/           # Shared models + ML services (class library)
│   │   ├── Models/             # Data models (ML results, jobs, settings)
│   │   └── Services/           # ML services, coordinators, persistence
│   └── Foundry.Broker/         # ASP.NET Core API host
│       └── Endpoints/          # HTTP endpoint definitions
├── tests/
│   └── Foundry.Core.Tests/     # xUnit tests
├── scripts/
│   ├── scoring/                # PR scoring (preprocessor, schema validation, retraining)
│   ├── rag/                    # RAG pipeline (indexer, search, query)
│   ├── ml/                     # ML artifacts, embeddings, document preprocessing
│   ├── automation/             # PR review, issue scanning, Discord briefs, scheduled tasks
│   └── commands/               # Operator commands (review, approve, reject, scan, status)
├── bot/                        # Discord bot — sole operator interface
├── schemas/                    # Feature schema (frozen training data validation)
├── docs/                       # Architecture, conventions, roadmap
└── Foundry.sln                 # Solution file
```

## Quick Start

### Prerequisites
- .NET 10 SDK
- Python 3.10+ (for ML scripts)
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
| `GET` | `/api/state` | Current pipeline state |
| `POST` | `/api/ml/pipeline` | Run full ML pipeline |
| `POST` | `/api/ml/embeddings` | Run document embeddings |
| `POST` | `/api/ml/export-artifacts` | Export Suite artifacts |
| `POST` | `/api/ml/index-knowledge` | Index knowledge documents |
| `GET` | `/api/knowledge/index-status` | Knowledge index status |
| `POST` | `/api/knowledge/search` | Semantic search |
| `GET` | `/api/jobs` | List recent jobs |
| `GET` | `/api/schedules` | List job schedules |
| `POST` | `/api/workflows` | Create workflow template |

All ML endpoints support `?sync=true` for synchronous execution or default to async job queuing.

## Artifacts

Artifacts are exported to `State/ml-artifacts/` and consumed by Suite through its review-first workflows.

## Configuration

Settings are loaded from `foundry.settings.json` (or `foundry.settings.local.json` for overrides):

```json
{
  "enableMLPipeline": true,
  "ollamaEndpoint": "http://127.0.0.1:11434",
  "mlModel": "qwen3:8b",
  "mlArtifactExportPath": "",
  "jobRetentionDays": 30,
  "knowledgeLibraryPath": "",
  "stateRootPath": "",
  "discordBotToken": ""
}
```
