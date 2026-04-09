# Phases 3–9 Roadmap

> **Purpose:** A single document showing every phase of work for Foundry repo from Phase 3 onward, broken into PRs that can be executed one at a time. Phases 1–2 are complete. This document covers Phase 3 (already implemented) through Phase 9 (future).

---

## Status Summary

| Phase | Title | Status |
|-------|-------|--------|
| 1 | Foundation (Serilog, AngleSharp, FluentValidation, OllamaSharp) | ✅ Complete |
| 2 | Persistence & Resilience (LiteDB, Polly) | ✅ Complete |
| 3 | Async Job Model (Job Store, Worker, Endpoints, Retention) | ✅ Complete |
| 4 | Observability & Health Monitoring | ✅ Complete |
| 5 | Semantic Search (Ollama Embeddings + Qdrant) | ✅ Complete |
| 6 | Agent Orchestration (Semantic Kernel) | ✅ Complete |
| 7 | Document Extraction (Docling) | ✅ Complete |
| 8 | Scheduled Automation & Operator Workflows | ✅ Complete |
| 9 | WPF Client Async Integration | ✅ Complete |

---

## Phase 3 — Async Job Model for ML Work ✅ COMPLETE

**Goal:** Move ML pipeline execution from synchronous broker endpoints to a background job system with persistent state.

### PR 3.1: Job Record Model + FoundryJobStore

**What was done:**
- Created `FoundryJob` model with fields: `Id` (GUID), `Type` (ml-analytics/ml-forecast/ml-embeddings/ml-pipeline/ml-export-artifacts), `Status` (queued/running/succeeded/failed), `CreatedAt`, `StartedAt`, `CompletedAt`, `Error`, `ResultKey`, `RequestedBy`, `RequestPayload`.
- Created `FoundryJobStore` backed by LiteDB `jobs` collection with methods:
  - `Enqueue(type, requestedBy, requestPayload)` — create a queued job.
  - `GetById(jobId)` — retrieve a job by ID.
  - `ListRecent(count)` — list most recent jobs.
  - `DequeueNext()` — atomically claim the oldest queued job.
  - `MarkSucceeded(jobId, resultKey)` — mark job as succeeded with result pointer.
  - `MarkFailed(jobId, error)` — mark job as failed with error message.

**Files touched:**
- `Foundry/Models/FoundryJob.cs` — new model.
- `Foundry/Services/FoundryJobStore.cs` — new LiteDB-backed store.

---

### PR 3.2: Background Job Worker

**What was done:**
- Created `FoundryJobWorker : BackgroundService` in `Foundry.Broker`.
- Worker polls LiteDB every 2 seconds for queued jobs via `DequeueNext()`.
- Executes one job at a time, dispatching to the orchestrator based on job type.
- Updates job lifecycle: `queued` → `running` → `succeeded`/`failed`.
- Stores ML results in LiteDB via `MLResultStore` (keyed by `ResultKey`).
- On failure: captures exception message in `Error` field.
- Registered as `IHostedService` in `Program.cs`.

**Files touched:**
- `Foundry.Broker/FoundryJobWorker.cs` — new background service.
- `Foundry.Broker/Program.cs` — register hosted service.

---

### PR 3.3: ML Endpoints → Async + Job Status Endpoints

**What was done:**
- Modified ML endpoints (`POST /api/ml/analytics`, `/forecast`, `/embeddings`, `/pipeline`, `/export-artifacts`) to return `{ jobId, status: "queued" }` by default.
- Added `?sync=true` query parameter for backward-compatible blocking behavior.
- Added new endpoints:
  - `GET /api/jobs` — list recent jobs (last 50).
  - `GET /api/jobs/{jobId}` — get job status and metadata.
  - `GET /api/jobs/{jobId}/result` — get job result JSON (succeeded only).

**Files touched:**
- `Foundry.Broker/Program.cs` — modified ML endpoints, added job endpoints.

---

### PR 3.4: ML Result Persistence (Restart-Safe)

**What was done:**
- Created `MLResultStore` that persists latest ML analytics/forecast/embeddings results to LiteDB collections (`ml_analytics`, `ml_forecast`, `ml_embeddings`).
- Uses `PersistedMLResult` wrapper with serialized JSON payload.
- `FoundryOrchestrator` restores from LiteDB on init, persists after each ML run.
- Export-artifacts endpoint is now restart-safe (survives broker restart).

**Files touched:**
- `Foundry/Services/MLResultStore.cs` — new persistence service.
- `Foundry/Models/PersistedMLResult.cs` — new wrapper model.
- `Foundry.Core/Services/FoundryOrchestrator.cs` — restore/persist on init and after ML runs.

---

### PR 3.5: Job Worker Hardening (Timeout + Stale Recovery)

**What was done:**
- Added per-job timeout to `FoundryJobWorker` (prevents a hanging job from blocking the worker indefinitely).
- Added `RecoverStaleJobs()` to `FoundryJobStore` — on startup, marks any jobs stuck in `running` status (from a previous crash) as `failed` with a recovery message.
- Worker calls `RecoverStaleJobs()` on initialization before processing new jobs.

**Files touched:**
- `Foundry.Broker/FoundryJobWorker.cs` — timeout + recovery on startup.
- `Foundry/Services/FoundryJobStore.cs` — `RecoverStaleJobs()` method.

---

### PR 3.6: Job Management & Retention

**What was done:**
- Added `FoundryJobStore.DeleteById(jobId)` — delete a completed (succeeded/failed) job. Queued/running jobs protected.
- Added `FoundryJobStore.DeleteOlderThan(cutoff)` — bulk-delete completed jobs older than threshold.
- Added `FoundryJobStore.ListByStatus(status, limit)` — filter jobs for monitoring.
- Added `FoundryJobStore.GetTotalCount()` — total job count for observability.
- Added `DELETE /api/jobs/{jobId}` endpoint (204/404/400 with status guard).
- Added `GET /api/jobs?status=...&type=...` — filtered listing with query params.

**Files touched:**
- `Foundry/Services/FoundryJobStore.cs` — 4 new methods.
- `Foundry.Broker/Program.cs` — new DELETE endpoint, updated GET with filters.

---

### Phase 3 Test Coverage (22 tests)

| Area | Tests | What's Covered |
|------|-------|----------------|
| Job model unit tests | 8 | Enqueue, retrieve, dequeue, mark succeeded/failed, list recent |
| Stale job recovery | 4 | Old running → failed, recent running preserved, queued/completed ignored, count |
| Job integration (PR 5) | 13 | FIFO ordering, full lifecycle, edge cases, payload round-trip, ListRecent limits, idempotent recovery, multi-iteration, dequeue skips |
| Job management (PR 6) | 9 | DeleteById across statuses, DeleteOlderThan with mixed active/expired, ListByStatus filter/limit, GetTotalCount |

---

## Phase 4 — Observability & Health Monitoring ✅ COMPLETE

**Goal:** Add structured health checks, metrics, and operational endpoints so you can tell at a glance whether Ollama, Python, LiteDB, and the job worker are healthy.

### PR 4.1: Health Check Endpoints

**What was done:**
- Added `GET /api/health` endpoint returning structured status for each subsystem (Ollama, Python, LiteDB, job worker).
- Added `PingAsync()` method to `IModelProvider` and implemented in `OllamaService`.
- Added `ProcessRunner.CheckPythonAsync()` convenience method.
- Each subsystem reports `ok`/`degraded`/`unavailable` with an overall status.

**Files touched:**
- `Foundry.Broker/Program.cs` — new endpoint.
- `Foundry/Services/IModelProvider.cs` — `PingAsync()`.
- `Foundry/Services/OllamaService.cs` — implement `PingAsync()`.
- `Foundry/Services/ProcessRunner.cs` — `CheckPythonAsync()`.
- `Foundry/Models/FoundryHealthReport.cs` — new model.

---

### PR 4.2: Job Metrics & Dashboard Endpoint

**What was done:**
- Added `GET /api/jobs/metrics` endpoint returning total jobs by status, average duration, and queue depth.
- Added `FoundryJobStore.GetMetrics()`, `GetAverageDuration()`, `GetCountByStatus()`, and `GetCompletedSince()` methods.

**Files touched:**
- `Foundry/Services/FoundryJobStore.cs` — 4 new methods.
- `Foundry/Models/OfficeJobMetrics.cs` — new model.
- `Foundry.Broker/Program.cs` — new endpoint.

---

### PR 4.3: Automated Job Retention Cleanup

**What was done:**
- Added `JobRetentionWorker : BackgroundService` that runs once per day and calls `FoundryJobStore.DeleteOlderThan()`.
- Retention period is configurable via `FoundrySettings.JobRetentionDays` (default: 30).
- Logs deleted job count at `Information` level.

**Files touched:**
- `Foundry.Broker/JobRetentionWorker.cs` — new file.
- `Foundry.Broker/Program.cs` — register hosted service.
- `Foundry/Models/FoundrySettings.cs` — `JobRetentionDays` property.

---

## Phase 5 — Semantic Search (Ollama Embeddings + Qdrant) ✅ COMPLETE

**Goal:** Replace the TF-IDF/keyword fallback in `KnowledgePromptContextBuilder` with real vector embeddings for semantic search across the knowledge library.

**Prerequisite:** Phase 3 complete (async jobs exist to generate embeddings).

### PR 5.1: Ollama Embeddings via OllamaSharp

**What was done:**
- Added `EmbeddingService` in `Foundry/Services/` that calls Ollama's `/api/embed` endpoint via OllamaSharp.
- Accepts a text string, returns a `float[]` vector.
- Wrapped in the `ollama` Polly resilience pipeline.
- Returns null if Ollama is unavailable (callers handle gracefully).

**Files touched:**
- `Foundry/Services/EmbeddingService.cs` — new file.

---

### PR 5.2: Qdrant Local Vector Store

**What was done:**
- Added `Qdrant.Client` NuGet to `Foundry.Core.csproj`.
- Created `VectorStoreService` in `Foundry/Services/` wrapping Qdrant client.
- Methods: `UpsertAsync`, `SearchAsync`, `DeleteAsync`, `GetCollectionInfoAsync`.
- Creates collection `office-knowledge` on first use.
- Returns empty results if Qdrant is unreachable (existing TF-IDF fallback continues to work).

**Files touched:**
- `Foundry/Services/VectorStoreService.cs` — new file.
- `Foundry.Core.csproj` — `Qdrant.Client` NuGet.
- `README.md` — Qdrant Docker setup instructions.

---

### PR 5.3: Knowledge Indexing Job

**What was done:**
- Added `knowledge-index` job type to `OfficeJobType`.
- `KnowledgeIndexStore` tracks indexed document hashes in LiteDB to avoid re-indexing unchanged documents.
- Endpoints added: `POST /api/ml/index-knowledge`, `GET /api/knowledge/index-status`.

**Files touched:**
- `Foundry/Models/FoundryJob.cs` — new job type.
- `Foundry/Services/KnowledgeIndexStore.cs` — new file.
- `Foundry.Broker/Program.cs` — new endpoints.
- `Foundry.Broker/FoundryJobWorker.cs` — handler for knowledge-index jobs.

---

### PR 5.4: Semantic Knowledge Search

**What was done:**
- Modified `KnowledgePromptContextBuilder` to generate embedding for the user's query and search Qdrant.
- Falls back to existing keyword/TF-IDF search if Qdrant is unavailable.
- Merges results: Qdrant results first, then keyword results to fill remaining slots.
- Added `POST /api/knowledge/search` endpoint.

**Files touched:**
- `Foundry/Services/KnowledgePromptContextBuilder.cs` — semantic search path added.
- `Foundry/Models/KnowledgeSearchResult.cs` — new model.
- `Foundry.Broker/Program.cs` — new endpoint.

---

## Phase 6 — Agent Orchestration (Semantic Kernel) ✅ COMPLETE

**Goal:** Replace hand-rolled prompt composition with Semantic Kernel agents that have tool-calling capabilities.

### PR 6.1: Semantic Kernel Core Integration

**What was done:**
- Added `Microsoft.SemanticKernel` 1.71.0 to `Foundry.Core.csproj`.
- Created `OfficeKernelFactory` in `Foundry/Services/` that builds an SK `Kernel` configured for the local Ollama endpoint.
- Created base `DeskAgent` class that wraps an SK `ChatCompletionAgent` with system prompt and tool registration.

**Files touched:**
- `Foundry/Services/OfficeKernelFactory.cs` — new file.
- `Foundry/Services/DeskAgent.cs` — new file.
- `Foundry.Core.csproj` — `Microsoft.SemanticKernel` NuGet.

---

### PR 6.2: Desk-Specific Agents

**What was done:**
- Created five agent classes in `Foundry/Services/Agents/`:
  - `ChiefOfStaffAgent` — routes the day, accesses state and job list.
  - `EngineeringDeskAgent` — technical analysis, code review, architecture tradeoffs.
  - `SuiteContextAgent` — read-only Suite repo/runtime awareness.
  - `GrowthOpsAgent` — monetization, operator memory, suggestions.
  - `MLEngineerAgent` — ML pipeline, analytics, forecasts.
- Agent dispatch in `SendChatAsync` replaces `PromptComposer.ComposeChat()`, with fallback to direct `IModelProvider`.
- Agents registered in DI in `Program.cs`.

**Files touched:**
- `Foundry/Services/Agents/ChiefOfStaffAgent.cs` — new file.
- `Foundry/Services/Agents/EngineeringDeskAgent.cs` — new file.
- `Foundry/Services/Agents/SuiteContextAgent.cs` — new file.
- `Foundry/Services/Agents/GrowthOpsAgent.cs` — new file.
- `Foundry/Services/Agents/MLEngineerAgent.cs` — new file.
- `Foundry.Core/Services/FoundryOrchestrator.cs` — agent dispatch.
- `Foundry.Broker/Program.cs` — register agents in DI.

---

### PR 6.3: Multi-Turn Agent Memory

**What was done:**
- `DeskAgent` loads the desk thread state into SK `ChatHistory` for each request.
- Conversation summary stored in `DeskThreadState.Summary` for multi-turn memory.
- `OperatorMemoryStore.CloneDeskMessage` copies `ToolCalls` for persistence.

**Files touched:**
- `Foundry/Services/DeskAgent.cs` — memory management.
- `Foundry/Models/DeskThreadState.cs` — `Summary` field.
- `Foundry/Models/DeskMessageRecord.cs` — `ToolCalls` field.
- `Foundry/Services/OperatorMemoryStore.cs` — `CloneDeskMessage` copies `ToolCalls`.

---

## Phase 7 — Document Extraction (Docling) ✅ COMPLETE

**Goal:** Replace the basic `extract_document_text.py` with Docling for richer document extraction (tables, images, OCR).

### PR 7.1: Docling Python Integration

**What was done:**
- Replaced `extract_document_text.py` internals with a Docling-based script (same CLI interface).
- Docling handles: PDF (with tables and figures), DOCX, PPTX, HTML, images (OCR).
- Output format extended to `{ ok, text, metadata, tables, figures }`.
- Heuristic fallback: if Docling is not installed, falls back to existing basic extraction.
- Updated `README.md` with Docling setup instructions.

**Files touched:**
- `Foundry/Scripts/extract_document_text.py` — rewritten internals.
- `README.md` — Docling setup instructions.

---

### PR 7.2: Table and Figure Extraction

**What was done:**
- Extended extraction output to include structured tables (JSON arrays) and figure descriptions.
- `KnowledgeImportService.ExtractViaPythonRichAsync` returns full response with tables and figures.
- `LearningDocument` model updated with optional `Tables` and `ExtractedFigures` fields.
- `ExtractedTable` and `ExtractedFigure` models added to `LearningDocument.cs`.

**Files touched:**
- `Foundry/Scripts/extract_document_text.py` — table/figure output.
- `Foundry/Services/KnowledgeImportService.cs` — `ExtractViaPythonRichAsync`.
- `Foundry/Models/LearningDocument.cs` — `ExtractedTable`, `ExtractedFigure`, optional fields.

---

## Phase 8 — Scheduled Automation & Operator Workflows ✅ COMPLETE

**Goal:** Add scheduled job execution and operator-defined workflows so the system can run ML pipelines, knowledge indexing, and maintenance tasks automatically.

### PR 8.1: Cron-Style Job Scheduler

**What was done:**
- Created `JobSchedule` model and `JobSchedulerStore` backed by LiteDB `job_schedules` collection.
- `JobSchedulerWorker : BackgroundService` checks schedules every minute and enqueues jobs via `FoundryJobStore.Enqueue()`.
- Endpoints added: `GET /api/schedules`, `POST /api/schedules`, `PUT /api/schedules/{id}`, `DELETE /api/schedules/{id}`.
- Schedule validators added to `Foundry.Broker/Validators.cs`.

**Files touched:**
- `Foundry/Models/JobSchedule.cs` — new model.
- `Foundry/Services/JobSchedulerStore.cs` — new LiteDB-backed store.
- `Foundry.Broker/JobSchedulerWorker.cs` — new background service.
- `Foundry.Broker/Program.cs` — register service, endpoints.
- `Foundry.Broker/Validators.cs` — schedule validators.

---

### PR 8.2: Daily Run Automation

**What was done:**
- Added `daily-run` job type to `OfficeJobType`.
- `RunDailyWorkflowAsync` in the orchestrator orchestrates: state refresh, ML pipeline, artifact export, suggestion generation.
- `GET /api/daily-run/latest` endpoint returns the most recent daily run summary.

**Files touched:**
- `Foundry/Models/FoundryJob.cs` — `DailyRun` job type.
- `Foundry/Models/DailyRunTemplate.cs` — run summary model.
- `Foundry.Broker/FoundryJobWorker.cs` — daily run handler.
- `Foundry.Core/Services/FoundryOrchestrator.cs` — `RunDailyWorkflowAsync()`.
- `Foundry.Broker/Program.cs` — new endpoint.

---

### PR 8.3: Operator Workflow Templates

**What was done:**
- Created `WorkflowTemplate` model and `WorkflowStore` backed by LiteDB `workflow_templates` collection.
- Two built-in templates: "Daily Run", "Knowledge Refresh".
- Endpoints: `GET /api/workflows`, `POST /api/workflows`, `POST /api/workflows/{id}/run`, `DELETE /api/workflows/{id}`.
- Workflow validators added to `Foundry.Broker/Validators.cs`.

**Files touched:**
- `Foundry/Models/WorkflowTemplate.cs` — new model.
- `Foundry/Services/WorkflowStore.cs` — new LiteDB-backed store.
- `Foundry.Broker/Program.cs` — new endpoints.
- `Foundry.Broker/Validators.cs` — workflow validators.

---

## Phase 9 — WPF Client Async Integration ✅ COMPLETE

**Goal:** Update the WPF desktop client to use the async job model and semantic search instead of blocking API calls.

### PR 9.1: Job Polling in ViewModels

**What was done:**
- Added `JobPollingService` to WPF (submits ML requests, polls `GET /api/jobs/{jobId}` every 2 seconds, updates ViewModel on completion).
- ML-related ViewModels updated to use polling instead of blocking calls.

**Files touched:**
- `Foundry/Services/JobPollingService.cs` — new file.

---

### PR 9.2: Semantic Search in Knowledge View

**What was done:**
- Added `KnowledgeSearchService` that calls `POST /api/knowledge/search` on the broker.
- Results ranked by semantic relevance with similarity scores.
- Falls back to text search if the broker reports Qdrant is unavailable.
- `KnowledgeSearchResult` model added.

**Files touched:**
- `Foundry/Services/KnowledgeSearchService.cs` — new file.
- `Foundry/Models/KnowledgeSearchResult.cs` — new model.

---

### PR 9.3: Agent Chat with Tool Feedback

**What was done:**
- Chat response format extended to include tool invocation metadata.
- `DeskMessageRecord.ToolCalls` field added for tool call records.
- `ToolCallRecord` model added.
- `OperatorMemoryStore.CloneDeskMessage` copies `ToolCalls` for persistence.

**Files touched:**
- `Foundry/Models/DeskMessageRecord.cs` — `ToolCalls` field.
- `Foundry/Services/OperatorMemoryStore.cs` — `CloneDeskMessage` updated.

---

## Quick Reference: PR Execution Order

| PR | Phase | Title | Dependencies |
|----|-------|-------|-------------|
| 3.1 | 3 | Job Record Model + FoundryJobStore | Phase 2 (LiteDB) |
| 3.2 | 3 | Background Job Worker | 3.1 |
| 3.3 | 3 | ML Endpoints → Async + Job Status Endpoints | 3.1, 3.2 |
| 3.4 | 3 | ML Result Persistence (Restart-Safe) | 3.1 |
| 3.5 | 3 | Job Worker Hardening (Timeout + Stale Recovery) | 3.2 |
| 3.6 | 3 | Job Management & Retention | 3.1 |
| 4.1 | 4 | Health Check Endpoints | None |
| 4.2 | 4 | Job Metrics & Dashboard Endpoint | None |
| 4.3 | 4 | Automated Job Retention Cleanup | None |
| 5.1 | 5 | Ollama Embeddings via OllamaSharp | None |
| 5.2 | 5 | Qdrant Local Vector Store | 5.1 |
| 5.3 | 5 | Knowledge Indexing Job | 5.1, 5.2 |
| 5.4 | 5 | Semantic Knowledge Search | 5.1, 5.2, 5.3 |
| 6.1 | 6 | Semantic Kernel Core Integration | None |
| 6.2 | 6 | Desk-Specific Agents | 6.1 |
| 6.3 | 6 | Multi-Turn Agent Memory | 6.1, 6.2 |
| 7.1 | 7 | Docling Python Integration | None |
| 7.2 | 7 | Table and Figure Extraction | 7.1 |
| 8.1 | 8 | Cron-Style Job Scheduler | None |
| 8.2 | 8 | Daily Run Automation | 8.1 |
| 8.3 | 8 | Operator Workflow Templates | 8.1, 8.2 |
| 9.1 | 9 | Job Polling in ViewModels | None (uses existing job API) |
| 9.2 | 9 | Semantic Search in Knowledge View | 5.4 |
| 9.3 | 9 | Agent Chat with Tool Feedback | 6.2 |

**Total: 24 PRs across 7 phases — all complete ✅.**

Phase 3 PRs are sequential (each builds on the previous).
Phase 4 PRs are independent of each other and can be done in any order.
Phases 5 and 6 are independent of each other (can be parallelized).
Phase 7 is independent of all others.
Phase 8 depends on Phase 3 (already complete).
Phase 9 depends on Phases 5 and 6 for full functionality but PR 9.1 can be done immediately.
