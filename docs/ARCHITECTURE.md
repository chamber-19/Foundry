# Foundry — Architecture Plan

> **Historical archive:** this document describes the Foundry ML-pipeline build-out (Phases 1–9) that predates the chamber-19 transfer and cleanup pass 1. The ML scoring pipeline, training stores, and agent desk scaffolding described here have been removed. Use [README.md](../README.md) for current architecture guidance and [`.github/copilot-instructions.md`](../.github/copilot-instructions.md) for contributor rules.

> **Goal:** Provide reliable ML pipeline execution, knowledge indexing, job scheduling, and health monitoring via an ASP.NET Core broker API. All operator interaction happens through a Discord bot.

---

## Where the Repo Already Aligns

Before proposing changes, it is important to recognize what the codebase already does well:

| Pattern | Where | Why It Matters |
|---------|-------|----------------|
| **Dual-semaphore concurrency** | `FoundryOrchestrator._gate` / `_mlGate` | ML work already runs outside the main state lock. This is the seed for a proper job model. |
| **Parallel ML execution** | `RunFullMLPipelineAsync` uses `Task.WhenAll` | Analytics, forecast, and embeddings are independent. The orchestrator already treats them as separate units of work. |
| **Graceful degradation** | `OllamaService`, `MLAnalyticsService`, `LiveResearchService` | Every external call has a fallback path: CLI fallback for model listing, heuristic fallback for ML, deterministic synthesis for research. |
| **ProcessRunner subprocess model** | `ProcessRunner.cs` | Python ML work is already isolated in subprocesses with stdout/stderr capture and exit code checking. |
| **TTL-based caching** | `MLAnalyticsService` (5-min default) | ML results are cached to avoid redundant work. This is a precursor to job result caching. |
| **Request models as records** | `Program.cs` (lines 632–651) | All broker request types are immutable records. FluentValidation can target these directly. |
| **IModelProvider abstraction** | `OllamaService : IModelProvider` | The LLM provider is already behind an interface. OllamaSharp can be swapped in without touching callers. |
| **State normalization on load** | `OfficeSessionStateStore`, `OperatorMemoryStore` | Stores already normalize/migrate state on load. LiteDB migration can follow this same pattern. |

---

## Phase 1 — Foundation (Logging, Parsing, Validation, Ollama Client) ✅ COMPLETE

**Goal:** Improve observability, correctness, and client reliability without changing architecture.

### 1.1 Serilog Structured Logging

**Scope:** `Foundry.Broker` (primary), `Foundry.Core` (secondary).

**What changes:**
- Add `Serilog.AspNetCore` and `Serilog.Sinks.File` to `Foundry.Broker.csproj`.
- Configure in `Program.cs`: console sink + rolling file sink (`State/logs/office-broker-.log`).
- Replace `builder.WebHost` logger with `UseSerilog()`.
- No changes to existing `logger.LogError()` call sites — Serilog plugs into `ILogger`.
- Add `ILogger` injection to `OllamaService`, `ProcessRunner`, and `MLAnalyticsService` for structured diagnostics (elapsed time, model name, exit codes).

**What stays the same:**
- All 20+ endpoint catch blocks remain unchanged.
- No new log levels forced into services that currently use fallback patterns.

**Smallest safe PR:** Serilog setup in Broker `Program.cs` + rolling file config only. Service-level logging can follow.

### 1.2 AngleSharp HTML Extraction

**Scope:** `Foundry/Services/LiveResearchService.cs`.

**What changes:**
- Add `AngleSharp` NuGet to `Foundry.Core.csproj` (since `LiveResearchService` is linked into Core).
- Replace the four compiled `Regex` patterns (`ResultLinkRegex`, `ResultSnippetRegex`, `DescriptionMetaRegex`, `OgDescriptionMetaRegex`) with AngleSharp DOM queries.
- Replace `ExtractPreview` regex pipeline with AngleSharp document parsing.
- `CleanHtml` becomes `document.Body.TextContent` (built-in text extraction).

**What stays the same:**
- `SearchAsync` public API unchanged.
- `EnrichSourcesAsync` parallel pattern unchanged.
- HTTP client configuration unchanged.
- Fallback patterns unchanged.

**Why not HtmlAgilityPack:** AngleSharp provides a full DOM with `querySelector` / `querySelectorAll` that matches browser behavior. HAP is lighter but more fragile for dynamic content patterns.

### 1.3 FluentValidation for Broker Requests

**Scope:** `Foundry.Broker/Program.cs` request records.

**What changes:**
- Add `FluentValidation` NuGet to `Foundry.Broker.csproj`.
- Create validators for request records that currently rely on orchestrator `ArgumentException` throws.
- Call `validator.Validate()` at the top of each endpoint, returning `400 BadRequest` with structured error details.
- Remove duplicated validation from orchestrator methods where the broker now handles it.

**What stays the same:**
- Orchestrator state validation (`InvalidOperationException`) stays in the orchestrator.
- Numeric clamping (`Math.Clamp`) stays in the orchestrator.
- Request record definitions stay in `Program.cs` (can move later).

**Target validators:**
- `ChatRouteRequestValidator` — Route not empty, Route in known catalog.
- `ChatSendRequestValidator` — Prompt not empty.
- `ResearchRunRequestValidator` — Query not empty.
- `WatchlistRunRequestValidator` — WatchlistId not empty.
- `InboxResolveRequestValidator` — SuggestionId not empty, Status in `[accepted, deferred, rejected]`.

### 1.4 OllamaSharp Client Replacement

**Scope:** `Foundry/Services/OllamaService.cs`.

**What changes:**
- Add `OllamaSharp` NuGet to `Foundry.Core.csproj`.
- Replace internal `HttpClient` + manual JSON serialization with `OllamaApiClient`.
- Replace `OllamaChatRequest/Response` records with OllamaSharp's typed API.
- Replace `GetInstalledModelsAsync` HTTP+CLI dual path with OllamaSharp's `ListLocalModelsAsync`.
- Replace `GenerateAsync` / `GenerateJsonAsync` with OllamaSharp chat API.

**What stays the same:**
- `IModelProvider` interface unchanged.
- `OllamaService` class name and constructor signature unchanged.
- `ProcessRunner` fallback for model listing stays as defensive backup.
- 90-second timeout behavior preserved.
- No streaming added (can be added later as a separate PR).

**Why OllamaSharp and not Semantic Kernel:** OllamaSharp is a thin, typed HTTP client. It replaces the exact code we wrote by hand. Semantic Kernel is an orchestration framework that was added in Phase 6 after the async job model existed.

---

## Phase 2 — Persistence & Resilience (LiteDB, Polly) ✅ COMPLETE

**Goal:** Replace fragile JSON file I/O with a proper embedded database. Add retry/circuit-breaker patterns to all external calls.

**Packages added:** `LiteDB` 5.0.21, `Polly.Core` 8.6.6 to `Foundry.Core.csproj`.

### 2.1 LiteDB Local Persistence

**Scope:** `TrainingStore.cs`, `OperatorMemoryStore.cs`, `OfficeSessionStateStore.cs`.

**What changes:**
- Add `LiteDB` NuGet to `Foundry.Core.csproj`.
- Create `FoundryDatabase` wrapper class that manages a single `LiteDatabase` instance (`foundry.db`).
- Migrate each store to use LiteDB collections instead of JSON files:
  - `TrainingStore` → `training_attempts`, `defense_attempts`, `reflections` collections.
  - `OperatorMemoryStore` → `policies`, `watchlists`, `suggestions`, `activities` collections.
  - `OfficeSessionStateStore` → `session_state` collection (single document).
- Preserve existing load/save semantics and normalization logic.
- Add a `jobs` collection (empty schema) for Phase 3.
- Keep JSON export capability for Dropbox sync compatibility.

**What stays the same:**
- All store public APIs remain identical.
- Normalization/migration logic stays.
- Dropbox path configuration stays.
- Max-item limits (120 attempts, 240 suggestions) stay as collection-level logic.

**Migration path:** On first run with LiteDB, check for existing JSON files and import them. Mark JSON files as migrated (rename with `.migrated` suffix). Fall back to JSON if LiteDB fails to open.

### 2.2 Polly Resilience Pipelines

**Scope:** `OllamaService`, `LiveResearchService`, `ProcessRunner`, `MLAnalyticsService`.

**What changes:**
- Add `Polly` NuGet to `Foundry.Core.csproj`.
- Define named resilience pipelines:
  - **`ollama`**: Retry 3× with exponential backoff (2s, 4s, 8s) + circuit breaker (5 failures → 30s open).
  - **`web-research`**: Retry 2× with 1s delay + timeout at 25s (current timeout preserved).
  - **`python-subprocess`**: Retry 1× (for transient process spawn failures) + timeout at 90s.
- Wrap `OllamaService` HTTP calls in the `ollama` pipeline.
- Wrap `LiveResearchService.SearchAsync` and `EnrichSourcesAsync` calls in the `web-research` pipeline.
- Wrap `ProcessRunner.RunAsync` calls in the `python-subprocess` pipeline.

**What stays the same:**
- Fallback patterns in services remain (Polly adds retry before the existing fallback).
- `_mlGate` semaphore stays (Polly handles transient faults, not concurrency control).
- Existing timeout values preserved as Polly timeout policies.
- `ProcessRunner` exit code checking unchanged.

**Why Polly and not `Microsoft.Extensions.Http.Resilience`:** Polly works for both HTTP and non-HTTP calls (Python subprocesses). The extensions package is HTTP-only. We need both.

---

## Phase 3 — Async Job Model for ML Work ✅ COMPLETE

**Goal:** Move ML pipeline execution from synchronous broker endpoints to a background job system with persistent state.

**Implementation:** `FoundryJob` model + `FoundryJobStore` (LiteDB) + `FoundryJobWorker` (`BackgroundService`) + 3 new endpoints + `?sync=true` backward compatibility on ML endpoints.

### 3.1 Job Record Model

```
FoundryJob
├── Id: string (GUID)
├── Type: string ("ml-analytics" | "ml-forecast" | "ml-embeddings" | "ml-pipeline" | "ml-export-artifacts")
├── Status: string ("queued" | "running" | "succeeded" | "failed")
├── CreatedAt: DateTimeOffset
├── StartedAt: DateTimeOffset?
├── CompletedAt: DateTimeOffset?
├── Error: string?
├── ResultKey: string? (pointer to result in LiteDB)
└── RequestedBy: string? ("broker" | "operator" | "schedule")
```

**Persisted in:** LiteDB `jobs` collection (created in Phase 2).

### 3.2 Background Worker

**Scope:** `Foundry.Broker` — new `IHostedService`.

**What changes:**
- Add `FoundryJobWorker : BackgroundService` to `Foundry.Broker`.
- Worker polls LiteDB `jobs` collection for `queued` jobs.
- Executes one job at a time (reuses `_mlGate` concurrency model).
- Updates job status through lifecycle: `queued` → `running` → `succeeded`/`failed`.
- Stores results in LiteDB (keyed by `ResultKey`).
- On failure: captures exception message in `Error` field, sets status to `failed`.

**What stays the same:**
- Existing ML endpoint handlers still work (they now enqueue a job and return the job ID).
- `RunFullMLPipelineAsync` logic moves to the worker but uses the same orchestrator methods.
- `_mlGate` semaphore continues to prevent concurrent ML execution.
- Python subprocess execution unchanged.

### 3.3 Status & Result Endpoints

**New endpoints:**
- `GET /api/jobs/{jobId}` — Return job record (status, timestamps, error).
- `GET /api/jobs/{jobId}/result` — Return job result (if succeeded).
- `GET /api/jobs` — List recent jobs (last 50).

**Modified endpoints:**
- `POST /api/ml/analytics` → Returns `{ jobId, status: "queued" }` instead of blocking.
- `POST /api/ml/forecast` → Same.
- `POST /api/ml/embeddings` → Same.
- `POST /api/ml/pipeline` → Same.

**Backward compatibility:** Add `?sync=true` query parameter to ML endpoints for callers that need the old blocking behavior during migration.

### 3.4 Job Management & Retention (PR 6)

**Problem:** Jobs accumulate indefinitely in LiteDB without cleanup, unlike other stores that enforce item limits.

**Solution:**
- `FoundryJobStore.DeleteById(jobId)` — Delete a completed (succeeded/failed) job by ID. Queued/running jobs cannot be deleted.
- `FoundryJobStore.DeleteOlderThan(cutoff)` — Bulk-delete completed jobs older than a date threshold.
- `FoundryJobStore.ListByStatus(status, limit)` — Filter jobs by status for monitoring.
- `FoundryJobStore.GetTotalCount()` — Total job count for observability.
- `DELETE /api/jobs/{jobId}` — HTTP endpoint for single-job deletion (204 No Content / 404 / 400).
- `GET /api/jobs?status=...&type=...` — Filtered listing with optional status and type query params.

**Retention policy:** Completed jobs are eligible for deletion after 30 days. `JobRetentionWorker` (Phase 4) handles automated cleanup daily.

### 3.5 No UI Changes Required

The WPF client currently calls ML endpoints and waits for the response. With the async model:
- Client gets back a job ID immediately.
- Client polls `GET /api/jobs/{jobId}` until status is `succeeded` or `failed`.
- This can be implemented in the WPF ViewModel later without broker changes.

---

## Phase 4 — Observability & Health Monitoring ✅ COMPLETE

**Goal:** Add structured health checks, metrics, and operational endpoints so you can tell at a glance whether Ollama, Python, LiteDB, and the job worker are healthy.

### 4.1 Health Check Endpoint

- `GET /api/health` returns structured status for each subsystem: Ollama, Python, LiteDB, job worker.
- `IModelProvider.PingAsync()` checks Ollama reachability.
- `ProcessRunner.CheckPythonAsync()` checks Python availability.
- `FoundryHealthReport` model carries per-subsystem `ok`/`degraded`/`unavailable` status.

### 4.2 Job Metrics Endpoint

- `GET /api/jobs/metrics` returns total jobs by status, average duration, queue depth, and completed-since counts.
- `FoundryJobStore.GetMetrics()`, `GetAverageDuration()`, `GetCountByStatus()`, `GetCompletedSince()` methods.
- `OfficeJobMetrics` model.

### 4.3 Automated Job Retention

- `JobRetentionWorker : BackgroundService` runs once per day and calls `FoundryJobStore.DeleteOlderThan()`.
- Retention period configurable via `FoundrySettings.JobRetentionDays` (default: 30).

---

## Phase 5 — Semantic Search (Ollama Embeddings + Qdrant) ✅ COMPLETE

**Goal:** Replace the TF-IDF/keyword fallback in `KnowledgePromptContextBuilder` with real vector embeddings for semantic search across the knowledge library.

### 5.1 Ollama Embeddings

- `EmbeddingService` calls Ollama `/api/embed` via OllamaSharp. Returns `float[]` vector or null on failure.

### 5.2 Qdrant Local Vector Store

- `VectorStoreService` wraps `Qdrant.Client` with `UpsertAsync`, `SearchAsync`, `DeleteAsync`, `GetCollectionInfoAsync`. Graceful empty fallback when Qdrant is unreachable.
- Collection: `office-knowledge`. Qdrant runs as local Docker container.

### 5.3 Knowledge Indexing Job

- `knowledge-index` job type. `KnowledgeIndexStore` (LiteDB `knowledge_index`) tracks indexed document hashes to avoid re-indexing unchanged documents.

### 5.4 Semantic Knowledge Search

- `KnowledgePromptContextBuilder` generates embedding for the user's query, searches Qdrant, then fills remaining slots with keyword results.
- `POST /api/knowledge/search` endpoint. Falls back to keyword search when Qdrant is unavailable.

---

## Phase 6 — Agent Orchestration (Semantic Kernel) ✅ COMPLETE

**Goal:** Replace hand-rolled prompt composition with Semantic Kernel agents that have tool-calling capabilities.

### 6.1–6.3 SK Core + Desk Agents + Multi-Turn Memory

- `OfficeKernelFactory` builds an SK `Kernel` for the local Ollama endpoint (`Microsoft.SemanticKernel` 1.74.0).
- `DeskAgent` base class wraps SK `ChatCompletionAgent` with system prompt and tool registration.
- Five desk agents in `Foundry/Services/Agents/`: `ChiefOfStaffAgent`, `EngineeringDeskAgent`, `SuiteContextAgent`, `GrowthOpsAgent`, `MLEngineerAgent`.
- Agent dispatch in `SendChatAsync` replaces `PromptComposer.ComposeChat()`, with fallback to direct `IModelProvider`.
- `DeskThreadState.Summary` for multi-turn memory. `DeskMessageRecord.ToolCalls` for tool invocation records.

---

## Phase 7 — Document Extraction (Docling) ✅ COMPLETE

**Goal:** Replace basic `pypdf` document extraction with Docling for tables, figures, and OCR.

- `extract_document_text.py` uses Docling when installed; falls back to `pypdf`/`python-docx`.
- Output: `{ ok, text, metadata, tables, figures }`.
- `KnowledgeImportService.ExtractViaPythonRichAsync` returns the full response.
- `LearningDocument` gains `ExtractedTable` and `ExtractedFigure` optional fields.

---

## Phase 8 — Scheduled Automation & Operator Workflows ✅ COMPLETE

**Goal:** Add scheduled job execution and operator-defined workflows.

- `JobSchedule` model + `JobSchedulerStore` (LiteDB `job_schedules`). `JobSchedulerWorker` enqueues jobs on schedule.
- `daily-run` job type: state refresh → ML pipeline → artifact export → suggestions. `RunDailyWorkflowAsync` in orchestrator.
- `WorkflowTemplate` + `WorkflowStore` (LiteDB `workflow_templates`). Two built-in templates: "Daily Run", "Knowledge Refresh".
- New endpoints: `/api/schedules` CRUD, `/api/daily-run/latest`, `/api/workflows` CRUD + run.

---

## Phase 9 — WPF Client Async Integration ✅ COMPLETE

**Goal:** Update the WPF desktop client to use the async job model and semantic search.

- `JobPollingService` submits ML requests and polls `GET /api/jobs/{jobId}` every 2 seconds.
- `KnowledgeSearchService` calls `POST /api/knowledge/search` for semantic search with similarity scores.
- `KnowledgeSearchResult` model. `ToolCallRecord` in `DeskMessageRecord` surfaces tool use to the WPF UI.

---

## Implementation Status

All 9 phases are complete. The codebase has 243 passing tests and a fully async, agent-orchestrated, semantically searchable architecture running entirely on the local workstation.
