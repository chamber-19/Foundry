# Office — ML-Powered PR Scoring Pipeline

Office is an ML-powered PR scoring and training pipeline with a two-machine distributed setup. It continuously scores pull requests using a local Ollama LLM, feeds the scores into a training feature schema, retrains a gradient-boosted scoring model, and keeps a RAG index updated for context retrieval.

## Layout

- `DailyDesk/`: WPF desktop application (operator UI and agent desks)
- `DailyDesk.Broker/`: ASP.NET Core web service broker (localhost:57420)
- `DailyDesk.Core/`: Shared business logic and ML pipeline models
- `DailyDesk.Core.Tests/`: Unit tests (xUnit)
- `scripts/`: PR scoring pipeline, RAG system, and ML training scripts
- `schemas/`: Training feature schema (`feature-v1.json`)
- `Knowledge/`: Repo-owned seed knowledge for the RAG index
- `Docs/`: Architecture, conventions, and library decisions

## ML Scoring Pipeline

The pipeline scores every open PR using a local Ollama model, records the scores as training features, and retrains the scoring model on a schedule.

```
auto-pr-review.ps1  →  feature-v1.json  →  retrain.py  →  scoring model
        ↑                                                         ↓
    RAG context (rag/)                                  replay-historical.ps1
```

### Key Scripts

| Script | Purpose |
|--------|---------|
| `scripts/auto-pr-review.ps1` | Live scoring engine — reviews all open PRs with Ollama |
| `scripts/scoring/replay-historical.ps1` | Replay historical PR scores for model training |
| `scripts/scoring/pull-training-data.ps1` | Pull training data from GitHub |
| `scripts/scoring/retrain.py` | Retrain the gradient-boosted scoring model |
| `scripts/scoring/validate_schema.py` | Validate feature schema against `feature-v1.json` |
| `scripts/rag/index.py` | Index repository content into the RAG vector store |
| `scripts/rag/query.py` | Query the RAG index for context retrieval |

### Training Feature Schema

`schemas/feature-v1.json` defines the features used for model training:

- PR metadata (size, file count, author history)
- LLM score from Ollama review
- Merge outcome (merged / closed / still open)

### Two-Machine Setup

- **DUSTIN** (primary): Runs `auto-pr-review.ps1` + Ollama (qwen3:14b), handles scoring
- **Machine 2** (secondary): Runs the lighter `qwen3:8b` model, handles training replay

## Agent Desks

Office includes five Ollama-powered agent desks, each with its own focus:

| Route | Title | Purpose |
|-------|-------|---------|
| `chief` | Chief of Staff | Routes the day across Suite, engineering, and growth |
| `engineering` | Engineering Desk | Technical analysis and code review support |
| `suite` | Suite Context | Read-only awareness of Suite repo and runtime signals |
| `business` | Growth Ops | Monetization and offer framing |
| `ml` | ML Engineer | ML pipeline status, forecasts, and Suite-ready artifacts |

## Embedded ML Pipeline

Office includes a local machine learning pipeline that analyzes operator data and produces actionable insights. The pipeline runs Python scripts as subprocesses and falls back to heuristic analysis when ML libraries are not installed.

### ML Engines

| Engine | Library | Purpose |
|--------|---------|---------|
| Analytics | Scikit-learn | Pattern clustering, operator readiness prediction, pattern classification |
| Document Embeddings | PyTorch | Semantic embeddings for knowledge library, document similarity, relevance-ranked search |
| Forecast | TensorFlow | Time-series forecasting, anomaly alerts, trend estimation |

### Suite Integration Artifacts

The ML pipeline produces versioned artifacts that Suite can consume through its deterministic workflows:

- **operator-readiness**: Readiness signals for project task assignment
- **knowledge-index**: Semantic document index for Suite's standards checker
- **watchdog-baseline**: Anomaly detection baselines for Suite's watchdog telemetry

Artifacts are exported to `State/ml-artifacts/` and follow Suite's review-first design philosophy.

### ML Setup (Optional)

The ML pipeline works without any Python ML libraries installed (uses heuristic fallbacks). For full ML capability:

```powershell
pip install scikit-learn torch tensorflow
```

### Document Extraction Setup (Optional)

Document extraction works with basic `pypdf` and `python-docx` out of the box. For richer extraction (tables, figures, OCR, PPTX, HTML, images), install Docling:

```powershell
pip install docling
```

Enable the embedded ML pipeline in `dailydesk.settings.json`:

```json
{
  "enableMLPipeline": true
}
```

### ML Broker Endpoints

| Method | Endpoint | Purpose |
|--------|----------|---------|
| POST | `/api/ml/analytics` | Run Scikit-learn analytics (async; `?sync=true` for blocking) |
| POST | `/api/ml/forecast` | Run TensorFlow forecast (async; `?sync=true` for blocking) |
| POST | `/api/ml/embeddings` | Run PyTorch document embeddings (async; `?sync=true` for blocking) |
| POST | `/api/ml/pipeline` | Run full ML pipeline (all three engines + artifact export) |
| POST | `/api/ml/export-artifacts` | Export Suite integration artifacts |
| POST | `/api/ml/index-knowledge` | Index knowledge documents into vector store (async) |
| GET | `/api/knowledge/index-status` | Get knowledge index status |
| POST | `/api/knowledge/search` | Semantic search across the knowledge library |

### Job Endpoints

| Method | Endpoint | Purpose |
|--------|----------|---------|
| GET | `/api/jobs` | List recent jobs (supports `?status=...&type=...` filters) |
| GET | `/api/jobs/{jobId}` | Get job status and metadata |
| GET | `/api/jobs/{jobId}/result` | Get job result (succeeded jobs only) |
| GET | `/api/jobs/metrics` | Job throughput, failure rates, and queue depth |
| DELETE | `/api/jobs/{jobId}` | Delete a completed job |

### Health Endpoint

| Method | Endpoint | Purpose |
|--------|----------|---------|
| GET | `/api/health` | Subsystem health: Ollama, Python, LiteDB, job worker |

## Qdrant Setup (Semantic Search)

Semantic search requires a local Qdrant vector database. Run Qdrant as a Docker container:

```bash
docker run -d --name qdrant -p 6333:6333 -p 6334:6334 \
  -v qdrant_storage:/qdrant/storage \
  qdrant/qdrant
```

Qdrant is **optional** — all semantic search features fall back gracefully to keyword search when Qdrant is unavailable.

## Workstation Setup

Recommended path on both machines:

```text
C:\Users\<you>\Documents\GitHub\Office
```

Office state and knowledge files live under Dropbox:

- `%USERPROFILE%\Dropbox\SuiteWorkspace\Office\Knowledge`
- `%USERPROFILE%\Dropbox\SuiteWorkspace\Office\State`

Clone on the second machine:

```powershell
git clone https://github.com/Koraji95-coder/Office.git C:\Users\<you>\Documents\GitHub\Office
```

## Build

```powershell
cd DailyDesk
dotnet build
```

## Run

```powershell
cd DailyDesk
dotnet run
```

## Test

```powershell
dotnet test DailyDesk.Core.Tests
```

## Relationship To Suite

- `Suite` stays in its own repo.
- `Daily Office` stays in this repo.
- `Suite Runtime Control` lives in `Suite` and launches the built Office executable from the workstation-local path.
- Office's ML pipeline produces artifacts that Suite can consume through its deterministic, review-first workflows.
- Suite does **not** host an agent product surface. Office owns local chat, orchestration, and operator-assistant work.
