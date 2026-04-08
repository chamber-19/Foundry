# Refactor Pressure Notes

> **Purpose:** Track areas of the codebase under known refactor pressure, document the technical debt they represent, and provide prioritized guidance for future cleanup.

---

## How to Use This Document

Each entry describes a **pressure area**: a part of the codebase that is functional today but is accumulating structural debt. Entries are grouped by priority — High, Medium, and Low — based on how much they affect future development velocity. When a pressure area is resolved, move its entry to the **Resolved Pressure** archive table at the bottom of this document. If the resolution corresponds to a new phase of work, also mark that phase complete in `PHASES-ROADMAP.md`.

---

## High Pressure

These areas actively slow down new feature development and increase the risk of regression.

### 1. OfficeBrokerOrchestrator — Monolithic Coordinator

| | |
|---|---|
| **Files** | `DailyDesk.Core/Services/OfficeBrokerOrchestrator.cs`, `DailyDesk.Core/Services/MLPipelineCoordinator.cs` |
| **Size** | ~3,850 lines |
| **Phase introduced** | Phase 1 (grew through Phase 9) |

**What it does now:**
The orchestrator is the single entry point for every operation — ML pipelines, research jobs, knowledge indexing, agent dispatch, operator memory, and workspace snapshots. It holds references to 15+ injected services and coordinates state transitions under a shared `_gate` semaphore.

**Why it is under pressure:**
- Adding a new desk workflow requires modifying this file regardless of whether the change is related to other areas.
- All state reads go through the same `SemaphoreSlim`, making selective locking impossible without touching the orchestrator.
- Test coverage requires constructing the entire orchestrator graph, even for tests that only exercise one domain (e.g., ML pipeline scoring).

**Refactor direction:**
Split into domain coordinators:
- `ResearchCoordinator` — research jobs, watchlist, enrichment. ✅ Extracted to `DailyDesk.Core/Services/ResearchCoordinator.cs` but not yet delegated from `OfficeBrokerOrchestrator`.
- `MLPipelineCoordinator` — ML job dispatch, result retrieval, export artifacts. ✅ Extracted and wired: `OfficeBrokerOrchestrator` now holds `_mlPipelineCoordinator` and delegates all ML pipeline methods to it.
- `KnowledgeCoordinator` — import, indexing, context building. ✅ Extracted to `DailyDesk.Core/Services/KnowledgeCoordinator.cs` but not yet delegated from `OfficeBrokerOrchestrator`.

Keep `OfficeBrokerOrchestrator` as a thin facade that delegates to these coordinators. The facade boundary means `Program.cs` endpoints do not change callers.

**Prerequisite:** No blocking prerequisite. Wire `ResearchCoordinator` and `KnowledgeCoordinator` into `OfficeBrokerOrchestrator` the same way `MLPipelineCoordinator` was — constructor injection, then replace each direct implementation with a delegate call. Update existing tests that construct the orchestrator directly to pass the new coordinator dependencies.

---

## Medium Pressure

These areas add maintenance overhead but do not block current development.

### 2. MLAnalyticsService — Redundant In-Memory TTL Cache

| | |
|---|---|
| **File** | `DailyDesk/Services/MLAnalyticsService.cs` |
| **Phase introduced** | Phase 1 |
| **Made redundant by** | Phase 3 (`MLResultStore`, LiteDB) |

**What it does now:**
`MLAnalyticsService` maintains an in-memory 5-minute TTL cache for analytics, forecast, and embeddings results (`_cachedAnalytics`, `_cachedEmbeddings`, `_cachedForecast`). This was added before the async job model existed.

**Why it is under pressure:**
- `MLResultStore` (Phase 3) now persists the latest result for each ML type in LiteDB. Callers can retrieve a persistent, restart-safe result from `MLResultStore` at any time.
- The in-memory cache and `MLResultStore` can diverge: a fresh run persisted to LiteDB will not be reflected in the in-memory cache until the TTL expires.
- `InvalidateCache()` must be called explicitly after a job completes, creating a coordination requirement between `OfficeJobWorker` and `MLAnalyticsService`.

**Refactor direction:**
Remove the in-memory TTL cache from `MLAnalyticsService`. Replace the three `_cached*` field groups with a single call to `MLResultStore.GetLatest*(type)` where callers currently access cached results. Keep the `InvalidateCache()` method as a no-op stub temporarily so call sites compile without change, then remove it once all callers are updated.

**Prerequisite:** Confirm `MLResultStore` returns a non-null result for all three ML types after a job completes. Covered by existing Phase 3 integration tests.

---

### 3. ML Endpoints — `?sync=true` Backward Compatibility Path

| | |
|---|---|
| **Files** | `DailyDesk.Broker/Endpoints/MLEndpoints.cs` |
| **Phase introduced** | Phase 3 (async job model) |
| **Removal condition** | WPF client fully migrated to job polling (Phase 9 complete) |

**What it does now:**
Six ML endpoints (`/api/ml/analytics`, `/forecast`, `/embeddings`, `/pipeline`, `/export-artifacts`, `/ml/index-knowledge`) accept a `?sync=true` query parameter that switches them back to synchronous blocking behavior. This was added to allow gradual migration of callers during the Phase 3 async transition.

**Why it is under pressure:**
- The synchronous code path is a duplicate of the pre-Phase-3 blocking logic. Both paths must be kept in sync when the ML execution logic changes.
- `sync=true` callers bypass job lifecycle tracking (no job ID, no status polling, no retention).
- Phase 9 (WPF async integration) adds `JobPollingService` to the WPF client. Once the client migrates to polling, the sync path has no remaining legitimate callers.

**Refactor direction:**
After confirming no active callers use `?sync=true`:
1. Remove the `sync` query parameter check from each endpoint.
2. Remove the inline blocking code path in each handler.
3. Update `CURRENT-STATE.md` to reflect that ML endpoints are async-only.

**Prerequisite:** Phase 9 WPF client migration complete and validated on the target workstation. Run a one-time audit to confirm no callers pass `?sync=true`.

---

### 4. MLAnalyticsService — Dual ONNX/Python Execution with Stale Availability Check

| | |
|---|---|
| **File** | `DailyDesk/Services/MLAnalyticsService.cs` |
| **Phase introduced** | Phase 7 (ONNX engine added alongside existing Python path) |

**What it does now:**
`MLAnalyticsService` has a three-tier execution order for each ML operation: ONNX model (if file present) → Python subprocess → computed fallback. Python availability is checked once at first use via `IsPythonAvailable()`, result is cached in `_pythonAvailable` (a `bool?` field), and never re-evaluated for the lifetime of the process.

**Why it is under pressure:**
- A Python environment installed or removed after the first check will never be detected. The process must be restarted to reflect a change in Python availability.
- All three ML types (analytics, embeddings, forecast) each have their own ONNX check, Python fallback, and hardcoded fallback calculation — roughly 60 lines of near-identical control flow repeated three times.
- Artifacts generation (`GenerateMLArtifactsAsync`) is Python-only with no ONNX path, meaning it silently returns a fallback bundle on workstations without Python even when the ONNX engine is otherwise healthy.

**Refactor direction:**
1. Extract the three-tier execution pattern (ONNX → Python → fallback) into a private generic helper `TryExecuteAsync<T>(Func<T?> onnxFn, Func<Task<T?>> pythonFn, Func<T> fallbackFn)`.
2. Replace the cached `bool? _pythonAvailable` field with a short-lived check (e.g., re-evaluate at most once per 5 minutes) so Python environment changes are picked up without restarting.
3. Add an ONNX-based artifacts generation path or document the Python-only limitation explicitly in a code comment and in `CURRENT-STATE.md`.

**Prerequisite:** No blocking prerequisite. Refactor can be done independently. Verify all ML endpoint integration tests still pass after the helper extraction.

---

### 5. KnowledgeImportService — Python Subprocess Dependency for Rich Extraction

| | |
|---|---|
| **File** | `DailyDesk/Services/KnowledgeImportService.cs` |
| **Phase introduced** | Phase 7 (Docling pipeline) |

**What it does now:**
`KnowledgeImportService` invokes an external Python script (`_pythonScriptPath`) via `ProcessRunner` for all rich document extraction (PDF, DOCX, PPTX, OneNote packages). If the script is absent or Python is unavailable, extraction throws `FileNotFoundException` or `InvalidOperationException`. The service also hard-caps document loading at 64 files per call (`.Take(64)`) with no configuration surface.

**Why it is under pressure:**
- The Python script path is passed as a constructor argument with no validation until the first extraction call, making configuration errors silent until runtime.
- The 64-document cap is an unexplained magic number. Large knowledge libraries silently drop newer documents beyond the cap.
- There is no fallback for unsupported file types — if the Python script fails mid-import, the entire `LoadAsync` call throws and the partial document list is discarded.
- Deployment on a machine without Python (e.g., a locked-down workstation) breaks all rich-format knowledge import with no degraded-mode behavior.

**Refactor direction:**
1. Validate `_pythonScriptPath` at construction time (or at `LoadAsync` startup) and log a clear warning if the script is absent, then continue with only built-in text/markdown extraction rather than throwing.
2. Replace the hard-coded `64` with a configurable `MaxDocuments` property (default 64, settable via `DailyDeskSettings`).
3. Catch per-document extraction exceptions inside the loop, log the failure, and continue with a `LearningDocument` that carries only the filename and an error flag — so a single corrupt PDF does not abort the entire import.

**Prerequisite:** No blocking prerequisite. Validate against the existing `KnowledgeImportServiceTests` suite after each sub-step.

---

## Low Pressure

These areas are well-understood technical debt that does not need immediate action but should be tracked.

### 6. Store JSON Export — Dropbox Compatibility Holdover

| | |
|---|---|
| **Files** | `DailyDesk/Services/OperatorMemoryStore.cs`, `DailyDesk.Core/Services/OfficeSessionStateStore.cs` |
| **Phase introduced** | Phase 2 (LiteDB migration) |

**What it does now:**
All three stores maintain a JSON export path alongside LiteDB storage. JSON export was kept after the Phase 2 migration to preserve Dropbox sync compatibility (`training-history.json`, `operator-memory.json`, `broker-live-session.json`).

**Why it is under pressure:**
- JSON export is a secondary write on every save, increasing write amplification.
- The export files can be modified out-of-band (e.g., manual edits in Dropbox), and the conflict resolution behavior between the JSON file and LiteDB is not explicit.
- The fallback logic (load JSON if LiteDB fails) remains but has not been exercised since the LiteDB migration stabilized.

**Refactor direction:**
Once LiteDB has been proven stable across multiple workstations:
1. Remove the on-save JSON export from each store.
2. Keep a one-time `ExportToJson()` utility method for manual snapshot/debugging.
3. Remove the `LoadFromJson` fallback path or demote it to a one-time migration-only path.

**Prerequisite:** Confirm LiteDB stability on all active workstations. Keep the export path until then.

---

### 7. OnnxMLEngine — Coarse Single Lock Across Independent Model Sessions

| | |
|---|---|
| **File** | `DailyDesk/Services/OnnxMLEngine.cs` |
| **Phase introduced** | Phase 7 (ONNX in-process engine) |

**What it does now:**
`OnnxMLEngine` manages three independent `InferenceSession` instances (analytics, embeddings, forecast), each loaded lazily from disk. All lazy-load paths share a single `_sessionLock` object, meaning a slow cold-start of one model type blocks the other two from initializing concurrently.

**Why it is under pressure:**
- Once all three sessions are warm the shared lock is uncontested, but the first cold-start (e.g., when the WPF client sends three ML requests close together after a restart) serializes what could otherwise be parallel model loads.
- The `Dispose()` method sets a `_disposed` flag but does not null the session references, so a use-after-dispose will still dereference the `InferenceSession` objects and throw an `ObjectDisposedException` from within the ONNX Runtime rather than a clean `ObjectDisposedException` from the engine itself.

**Refactor direction:**
1. Replace the single `_sessionLock` with three separate per-model locks (`_analyticsLock`, `_embeddingsLock`, `_forecastLock`) so concurrent cold-starts for different model types do not block each other.
2. In `Dispose()`, null the session fields after disposing them to prevent post-dispose use from reaching the ONNX Runtime.

**Prerequisite:** No blocking prerequisite. Changes are confined to `OnnxMLEngine.cs`. Verify with the existing ONNX engine unit tests.

---

### 8. WPF MainViewModel — Partial Class Growth

| | |
|---|---|
| **Files** | `DailyDesk/ViewModels/MainViewModel.cs`, `DailyDesk/ViewModels/MainViewModel.Operator.cs`, `DailyDesk/ViewModels/MainViewModel.Workflow.cs`, `DailyDesk/ViewModels/MainViewModel.OfficeChat.cs`, `DailyDesk/ViewModels/MainViewModel.OfficeDesks.cs`, `DailyDesk/ViewModels/MainViewModel.Guide.cs` |
| **Combined size** | ~4,200+ lines |
| **Phase introduced** | Phase 1 (grew through Phase 9) |

**What it does now:**
The WPF ViewModel is split across 6 partial class files. Each file handles a domain (operator memory, workflow automation, chat routing, desk selection, training guide).

**Why it is under pressure:**
- The partial class approach manages file size but does not enforce boundaries. One partial class can freely call or modify state from another, and frequently does.
- The desk-specific chat logic in `MainViewModel.OfficeDesks.cs` and `MainViewModel.OfficeChat.cs` will grow as Phase 9 async polling matures (job polling, streaming responses, status display).

**Refactor direction:**
Convert to ViewModel-per-desk as the SK agent desks mature (post Phase 9):
- `OperatorViewModel` — operator memory, inbox, suggestions.
- `ResearchViewModel` — research jobs, watchlist.
- `WorkflowViewModel` — schedules, daily-run, workflow templates.

Keep `MainViewModel` as a shell that navigates between desk ViewModels. This mirrors the SK agent desk model established in Phase 6.

**Prerequisite:** Phase 9 async integration stabilized. Defer until WPF job polling is confirmed working.

---

## Resolved Pressure (Archive)

Keep a record of pressure areas that have been resolved so contributors understand why certain patterns were adopted.

| Area | Resolved In | Resolution |
|------|------------|-----------|
| Manual JSON parsing for Ollama API | Phase 1 (PR 1.4) | Replaced with OllamaSharp typed client |
| Regex-based HTML parsing in LiveResearchService | Phase 1 (PR 1.2) | Replaced with AngleSharp DOM queries |
| No retry logic on external calls | Phase 2 (PR 2.2) | Added named Polly resilience pipelines |
| JSON file persistence with no migration | Phase 2 (PR 2.1) | Replaced with LiteDB + JSON import path |
| Blocking ML endpoints with no status visibility | Phase 3 | Added async job model with LiteDB backing |
| No job cleanup (accumulating jobs) | Phase 3 (PR 3.4, PR 6) | Added `DeleteOlderThan` + `JobRetentionWorker` |
| TF-IDF keyword search only for knowledge retrieval | Phase 5 | Added Ollama embedding + Qdrant semantic search |
| Direct LLM calls without agent structure | Phase 6 | Added SK agent desks with tool-call support |
| Text-only document extraction | Phase 7 | Added Docling pipeline with table and figure extraction |
| No scheduled automation | Phase 8 | Added cron-style `JobSchedulerStore` + `JobSchedulerWorker` |
| WPF client blocking on ML calls | Phase 9 | Added `JobPollingService` with async poll loop |
| Validators.cs flat file vs. convention | Tech Debt (chunk7) | Completed domain split: added `Validators/MLValidators.cs` and `Validators/ScheduleValidators.cs` alongside pre-existing `ChatValidators.cs`; deleted root-level `Validators.cs` |
| PHASES-ROADMAP.md stale phase status | Tech Debt (chunk issue) | Updated status table: Phase 4 → ✅ Complete (health monitoring, JobRetentionWorker, PingAsync); Phase 5 → ✅ Complete (EmbeddingService, VectorStoreService, KnowledgeIndexStore) |
| Broker Program.cs — All Endpoints in One File | Tech Debt (chunk issue) | Extracted 30+ endpoints into 8 dedicated `IEndpointRouteBuilder` extension files under `DailyDesk.Broker/Endpoints/`; request records co-located with their handlers; `Program.cs` reduced to ~70 lines of infrastructure setup |
