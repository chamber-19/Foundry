# Library Decisions

> **Historical archive:** this document covers library choices made during the ML-pipeline build-out (phases 1–9) that predates the chamber-19 transfer. Entries for TensorFlow.NET, TorchSharp, ONNX Runtime, ML.NET, and scikit-learn are no longer applicable — those dependencies were removed in cleanup pass 1. Entries for LiteDB, Polly, Serilog, OllamaSharp, and AngleSharp remain relevant. New library decisions should be added here by the contributor who adds the dependency.

---

## Phase 1 Libraries

### Serilog (`Serilog.AspNetCore`, `Serilog.Sinks.File`)

| | |
|---|---|
| **GitHub** | [serilog/serilog](https://github.com/serilog/serilog) |
| **NuGet** | `Serilog.AspNetCore`, `Serilog.Sinks.File`, `Serilog.Sinks.Console` |
| **License** | Apache-2.0 |
| **Added to** | `Foundry.Broker.csproj` |

**Problem it solves:**
The broker currently logs only via the built-in `ILogger` to console output. When Ollama times out, Python subprocesses fail, or ML pipelines crash, there is no persistent diagnostic trail. Services like `OllamaService`, `ProcessRunner`, and `MLAnalyticsService` have zero logging — they rely on exception propagation and silent fallbacks.

**What it replaces:**
- Built-in `WebApplication.Logger` (console-only) → Serilog with console + rolling file sinks.
- No service-level logging → Structured `ILogger<T>` injection into services.

**Canonical usage patterns:**

```csharp
// In Program.cs — configure Serilog before building the app
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(stateRoot, "logs", "office-broker-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

// In services — use standard ILogger<T> (Serilog provides the implementation)
public sealed class OllamaService : IModelProvider
{
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(ILogger<OllamaService> logger, ...) { _logger = logger; }

    public async Task<string> GenerateAsync(...)
    {
        _logger.LogInformation("Generating response for route {Route} with model {Model}", route, model);
        // ... work ...
        _logger.LogWarning("Ollama returned empty response for route {Route}", route);
    }
}
```

**Rules for AI agents:**
- Always use structured logging: `_logger.LogInformation("Did {Action} for {Entity}", action, entity)` — never string interpolation.
- Log at `Information` for successful operations with timing. Log at `Warning` for fallbacks. Log at `Error` for exceptions.
- Do not log request/response bodies (may contain user data). Log operation names, durations, and outcome codes.
- Never add `Console.WriteLine` for diagnostics — always use `ILogger<T>`.

---

### AngleSharp (`AngleSharp`)

| | |
|---|---|
| **GitHub** | [AngleSharp/AngleSharp](https://github.com/AngleSharp/AngleSharp) |
| **NuGet** | `AngleSharp` |
| **License** | MIT |
| **Added to** | `Foundry.Core.csproj` |

**Problem it solves:**
`LiveResearchService.cs` uses four compiled `Regex` patterns to extract search results, snippets, and meta descriptions from HTML. The `ExtractPreview` method strips HTML with multiple regex passes (remove scripts, replace block elements, strip all tags, normalize whitespace). This approach is brittle — it breaks when HTML structure changes and cannot handle malformed markup, nested tags, or encoded entities correctly.

**What it replaces:**
- `ResultLinkRegex` → `document.QuerySelectorAll("a.result__a")` with `.GetAttribute("href")` and `.TextContent`.
- `ResultSnippetRegex` → `document.QuerySelectorAll("a.result__snippet")` with `.TextContent`.
- `DescriptionMetaRegex` / `OgDescriptionMetaRegex` → `document.QuerySelector("meta[name='description']")?.GetAttribute("content")`.
- `ExtractPreview` regex pipeline → `document.QuerySelector("body")?.TextContent` (built-in text extraction with whitespace normalization).
- `CleanHtml` → No longer needed (AngleSharp handles entity decoding and text extraction natively).

**Canonical usage patterns:**

```csharp
using AngleSharp;
using AngleSharp.Html.Parser;

// Parse HTML into a DOM
var parser = new HtmlParser();
using var document = await parser.ParseDocumentAsync(html);

// Extract search results (replaces ResultLinkRegex)
var links = document.QuerySelectorAll("a.result__a");
foreach (var link in links)
{
    var href = link.GetAttribute("href") ?? string.Empty;
    var title = link.TextContent.Trim();
}

// Extract meta description (replaces DescriptionMetaRegex + OgDescriptionMetaRegex)
var description = document.QuerySelector("meta[name='description']")?.GetAttribute("content")
    ?? document.QuerySelector("meta[property='og:description']")?.GetAttribute("content")
    ?? string.Empty;

// Extract page text (replaces ExtractPreview regex pipeline)
var bodyText = document.QuerySelector("body")?.TextContent ?? string.Empty;
var cleaned = Regex.Replace(bodyText, @"\s+", " ").Trim();
if (cleaned.Length > 900) cleaned = cleaned[..897] + "...";
```

**Rules for AI agents:**
- Always use AngleSharp for HTML parsing. Never write new regex patterns for HTML extraction.
- Use `QuerySelector` / `QuerySelectorAll` with CSS selectors (same syntax as browser DevTools).
- Use `.TextContent` for text extraction (strips all tags, decodes entities).
- Use `.InnerHtml` only when you need the raw HTML of a subtree.
- Dispose `IHtmlDocument` when done (it implements `IDisposable`).
- Do not install AngleSharp.Js or AngleSharp.Css unless JavaScript execution or CSS parsing is specifically needed.

---

### FluentValidation (`FluentValidation`)

| | |
|---|---|
| **GitHub** | [FluentValidation/FluentValidation](https://github.com/FluentValidation/FluentValidation) |
| **NuGet** | `FluentValidation` |
| **License** | Apache-2.0 |
| **Added to** | `Foundry.Broker.csproj` |

**Problem it solves:**
Broker request validation is scattered across two layers:
1. **Orchestrator parameter checks** — `ArgumentException` throws for empty strings and invalid enum values (e.g., `Status must be accepted, deferred, or rejected`).
2. **Broker catch blocks** — `ArgumentException` → `400 BadRequest` with `{ error: message }`.

This means the orchestrator is doing both business logic and input validation. Validation errors are indistinguishable from business rule violations in the response format. There is no structured error response (just a string message).

**What it replaces:**
- Orchestrator `ArgumentException` throws for parameter validation → FluentValidation validators at the endpoint level.
- Scattered `string.IsNullOrWhiteSpace` checks → Declarative `RuleFor(x => x.Property).NotEmpty()`.

**What it does NOT replace:**
- Orchestrator `InvalidOperationException` throws for state validation (e.g., "No active job") — these stay in the orchestrator.
- `Math.Clamp` for numeric clamping — this stays in the orchestrator.

**Canonical usage patterns:**

```csharp
using FluentValidation;

// Define a validator for a request record
public sealed class ChatSendRequestValidator : AbstractValidator<ChatSendRequest>
{
    public ChatSendRequestValidator()
    {
        RuleFor(x => x.Prompt)
            .NotEmpty()
            .WithMessage("Prompt is required.");
    }
}

public sealed class InboxResolveRequestValidator : AbstractValidator<InboxResolveRequest>
{
    public InboxResolveRequestValidator()
    {
        RuleFor(x => x.SuggestionId)
            .NotEmpty()
            .WithMessage("SuggestionId is required.");

        RuleFor(x => x.Status)
            .NotEmpty()
            .Must(s => new[] { "accepted", "deferred", "rejected" }
                .Contains(s, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Status must be accepted, deferred, or rejected.");
    }
}

// Use in endpoint (manual validation without middleware)
app.MapPost("/api/chat/send", async (ChatSendRequest request, FoundryOrchestrator orchestrator, CancellationToken ct) =>
{
    var validator = new ChatSendRequestValidator();
    var validation = validator.Validate(request);
    if (!validation.IsValid)
    {
        return Results.BadRequest(new { errors = validation.Errors.Select(e => e.ErrorMessage) });
    }
    // ... proceed with orchestrator call
});
```

**Rules for AI agents:**
- Every broker request record that accepts user input should have a corresponding `AbstractValidator<T>`.
- Validate at the endpoint level, before calling the orchestrator.
- Return structured error responses: `{ errors: ["message1", "message2"] }`.
- Do NOT add FluentValidation to model classes or service layers — it is an API boundary concern.
- Keep orchestrator state validation (`InvalidOperationException`) separate from input validation.

---

### OllamaSharp (`OllamaSharp`)

| | |
|---|---|
| **GitHub** | [awaescher/OllamaSharp](https://github.com/awaescher/OllamaSharp) |
| **NuGet** | `OllamaSharp` |
| **License** | MIT |
| **Added to** | `Foundry.Core.csproj` |

**Problem it solves:**
`OllamaService.cs` hand-rolls HTTP requests to the Ollama API:
- Manual `HttpClient` configuration with hardcoded 90-second timeout.
- Manual JSON serialization of `OllamaChatRequest` records.
- Manual deserialization of `OllamaChatResponse` records.
- Dual-path model discovery (HTTP `api/tags` with CLI `ollama list` fallback).
- No streaming support (`Stream: false` always).
- No model management (pull, delete, show).
- No embeddings API support (embeddings go through Python subprocess instead).

OllamaSharp is the Microsoft-recommended .NET client for Ollama. It provides typed APIs for all endpoints, streaming support, model management, and function calling — all tested against Ollama's API contract.

**What it replaces:**
- `OllamaChatRequest/Response/Message` internal records → OllamaSharp's typed request/response classes.
- `HttpClient.PostAsync("api/chat", ...)` → `OllamaApiClient.ChatAsync(...)`.
- `HttpClient.GetAsync("api/tags")` → `OllamaApiClient.ListLocalModelsAsync()`.
- Manual JSON serialization → OllamaSharp handles serialization internally.

**What it does NOT replace:**
- `IModelProvider` interface — stays as the service boundary.
- `ProcessRunner` CLI fallback — stays as a defensive backup.
- `PromptComposer` — stays as the prompt construction layer.

**Canonical usage patterns:**

```csharp
using OllamaSharp;
using OllamaSharp.Models.Chat;

// Initialize client
var client = new OllamaApiClient(new Uri("http://127.0.0.1:11434"));

// List models (replaces HTTP api/tags + CLI fallback)
var models = await client.ListLocalModelsAsync(cancellationToken);
var modelNames = models.Select(m => m.Name).ToList();

// Non-streaming chat (replaces GenerateAsync)
client.SelectedModel = "qwen3:8b";
var request = new ChatRequest
{
    Messages = new[]
    {
        new Message(ChatRole.System, systemPrompt),
        new Message(ChatRole.User, userPrompt),
    },
    Stream = false,
};
var response = await client.ChatAsync(request, cancellationToken);
var content = response?.Message?.Content ?? string.Empty;

// Structured JSON output (replaces GenerateJsonAsync<T>)
var jsonRequest = new ChatRequest
{
    Messages = new[] { new Message(ChatRole.System, systemPrompt), new Message(ChatRole.User, userPrompt) },
    Stream = false,
    Format = "json",
};
var jsonResponse = await client.ChatAsync(jsonRequest, cancellationToken);
var parsed = JsonSerializer.Deserialize<T>(jsonResponse?.Message?.Content ?? "{}");

// Embeddings (future — replaces Python ml_document_embeddings.py)
var embeddings = await client.EmbedAsync(new EmbedRequest
{
    Model = "qwen3:8b",
    Input = new[] { "document text here" },
}, cancellationToken);
```

**Rules for AI agents:**
- Use `OllamaApiClient` for all Ollama HTTP communication. Never create a raw `HttpClient` for Ollama endpoints.
- Keep the `IModelProvider` interface as the service boundary. `OllamaService` wraps `OllamaApiClient` and implements `IModelProvider`.
- Set `Stream = false` for non-streaming calls (matches current behavior). Streaming can be added later as a separate feature.
- Handle `HttpRequestException` and `TaskCanceledException` (timeout) — wrap with Polly when Phase 2 is complete.
- Do not use OllamaSharp's built-in chat history management — we manage conversation state in `DeskThreadState`.

---

## Phase 2 Libraries

### LiteDB (`LiteDB`)

| | |
|---|---|
| **GitHub** | [mbdavid/LiteDB](https://github.com/mbdavid/LiteDB) |
| **NuGet** | `LiteDB` |
| **License** | MIT |
| **Added to** | `Foundry.Core.csproj` |

**Problem it solves:**
All persistent state is stored as JSON files with `File.ReadAllTextAsync` / `File.WriteAllTextAsync`:
- `training-history.json` — Training attempts, defense attempts, reflections (max 120 each).
- `operator-memory.json` — Policies, watchlists, suggestions, activities, desk threads.
- `broker-live-session.json` — Current session state.

This approach has several problems:
1. **No concurrent access safety** — Two writes can corrupt the file.
2. **Full file read/write on every operation** — Loading 120 training attempts to add one.
3. **No indexing** — Finding a specific suggestion requires deserializing the entire file.
4. **No transactions** — A crash mid-write leaves a corrupted file.
5. **No query capability** — All filtering is done in memory after full deserialization.

**What it replaces:**
- `File.ReadAllTextAsync` / `File.WriteAllTextAsync` → LiteDB collections with LINQ queries.
- In-memory filtering → LiteDB indexed queries.
- Manual JSON serialization → LiteDB's built-in BSON serialization.
- No transactions → LiteDB ACID transactions.

**What it does NOT replace:**
- Store normalization logic — migrates into LiteDB's `OnModelCreating` / post-load hooks.
- Dropbox sync — LiteDB's `.db` file can be synced via Dropbox (single file).
- Max-item limits — enforced in store logic, not by LiteDB.

**Canonical usage patterns:**

```csharp
using LiteDB;

// Open database (single file, embedded, no server)
using var db = new LiteDatabase(Path.Combine(stateRoot, "foundry.db"));

// Get collection (auto-created on first use)
var attempts = db.GetCollection<TrainingAttemptRecord>("training_attempts");

// Insert
attempts.Insert(new TrainingAttemptRecord { ... });

// Query with index
attempts.EnsureIndex(x => x.CompletedAt);
var recent = attempts.Query()
    .OrderByDescending(x => x.CompletedAt)
    .Limit(120)
    .ToList();

// Upsert (update or insert by Id)
var suggestions = db.GetCollection<SuggestedAction>("suggestions");
suggestions.Upsert(suggestion);

// Single document store (session state)
var sessions = db.GetCollection<OfficeLiveSessionState>("session_state");
var state = sessions.FindById("current") ?? new OfficeLiveSessionState();
state.Id = "current";
sessions.Upsert(state);

// Transaction
db.BeginTrans();
try
{
    attempts.Insert(record);
    sessions.Upsert(updatedState);
    db.Commit();
}
catch
{
    db.Rollback();
    throw;
}
```

**Rules for AI agents:**
- Use a single `LiteDatabase` instance per application lifetime (it is thread-safe for reads, serializes writes).
- Add `EnsureIndex` for any field used in `Query().Where(...)` or `OrderBy(...)`.
- Use `Upsert` for entities with stable IDs (suggestions, policies). Use `Insert` for append-only entities (attempts, activities).
- Keep max-item limits in store logic: query, sort, skip/take, then delete excess.
- Do not store large binary data in LiteDB — keep file paths as references.
- Use `BsonMapper.Global` for custom serialization only when needed (default mapping handles most C# types).

---

### Polly (`Polly`)

| | |
|---|---|
| **GitHub** | [App-vNext/Polly](https://github.com/App-vNext/Polly) |
| **NuGet** | `Polly` |
| **License** | BSD-3-Clause |
| **Added to** | `Foundry.Core.csproj` |

**Problem it solves:**
Every external call in Office has no retry logic:
- **Ollama:** Single HTTP call with 90-second timeout. If Ollama is starting up, loading a model, or temporarily overloaded, the call fails and falls back to a deterministic response. No retry.
- **Web research:** Single HTTP call per source with 25-second timeout. Transient DNS or connection failures kill the entire enrichment.
- **Python subprocesses:** Single execution. If the process fails to spawn (file handle contention, antivirus scan), no retry.

Polly provides retry, circuit breaker, timeout, and fallback patterns as composable resilience pipelines.

**What it replaces:**
- Hardcoded `Timeout = TimeSpan.FromSeconds(90)` → Polly `TimeoutAsync` policy.
- No retry anywhere → Polly `RetryAsync` with exponential backoff.
- No circuit breaker → Polly `CircuitBreakerAsync` to stop hammering a dead service.

**What it does NOT replace:**
- Service-level fallback logic (deterministic responses, heuristic ML) — Polly adds retry before the fallback.
- `_mlGate` semaphore — Polly handles transient faults, not concurrency control.

**Canonical usage patterns:**

```csharp
using Polly;
using Polly.Retry;

// Define a resilience pipeline for Ollama
var ollamaPipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(2),
        BackoffType = DelayBackoffType.Exponential,
        ShouldHandle = new PredicateBuilder()
            .Handle<HttpRequestException>()
            .Handle<TaskCanceledException>(),
    })
    .AddTimeout(TimeSpan.FromSeconds(90))
    .Build();

// Use in OllamaService
public async Task<string> GenerateAsync(...)
{
    return await _ollamaPipeline.ExecuteAsync(async ct =>
    {
        var response = await _client.ChatAsync(request, ct);
        return response?.Message?.Content ?? string.Empty;
    }, cancellationToken);
}

// Define a resilience pipeline for web research
var webPipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 2,
        Delay = TimeSpan.FromSeconds(1),
        BackoffType = DelayBackoffType.Constant,
        ShouldHandle = new PredicateBuilder()
            .Handle<HttpRequestException>(),
    })
    .AddTimeout(TimeSpan.FromSeconds(25))
    .Build();

// Define a resilience pipeline for Python subprocesses
var pythonPipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 1,
        Delay = TimeSpan.FromSeconds(2),
        ShouldHandle = new PredicateBuilder()
            .Handle<InvalidOperationException>(),
    })
    .AddTimeout(TimeSpan.FromSeconds(90))
    .Build();
```

**Rules for AI agents:**
- Wrap every external call (HTTP, subprocess, file I/O to remote paths) in a Polly resilience pipeline.
- Use exponential backoff for services that may be overloaded (Ollama). Use constant delay for services where retry timing doesn't matter (web pages).
- Add circuit breaker for services that can be truly down (Ollama). Skip circuit breaker for one-shot operations (subprocess).
- Log retry attempts at `Warning` level: `_logger.LogWarning("Retrying {Operation} (attempt {Attempt})", ...)`.
- Do not retry on `ArgumentException` or `InvalidOperationException` from our own code — only retry transient external failures.
- Polly v8+ uses `ResiliencePipeline` (not the older `Policy` API). Always use the new API.

---

## Phase 3 Concepts (No Library — Application Architecture)

### Async Job Model

**Problem it solves:**
ML pipeline endpoints (`/api/ml/analytics`, `/api/ml/pipeline`, etc.) run synchronously — the HTTP request blocks until the Python subprocess completes. A full pipeline run can take 30-60+ seconds, during which the broker is unresponsive to that client. There is no way to:
- Check if ML work is already running.
- See the result of a previous run without re-running.
- Cancel a running ML operation.
- Know when an ML operation started or how long it took.

**What it introduces:**
- `FoundryJob` record persisted in LiteDB `jobs` collection.
- `FoundryJobWorker : BackgroundService` that processes queued jobs.
- Status endpoint (`GET /api/jobs/{jobId}`) for polling.
- ML endpoints return `{ jobId, status: "queued" }` instead of blocking.

**Rules for AI agents:**
- All long-running work (>5 seconds) should go through the job model.
- Jobs are persisted in LiteDB — they survive broker restarts.
- Only one ML job runs at a time (enforced by the existing `_mlGate`).
- Job results are stored in LiteDB with a `ResultKey` pointer — not in the job record itself.
- Use `?sync=true` query parameter for backward compatibility during migration.

---

## Phase 4 Libraries (Future — Not Yet Added)

### Qdrant

| | |
|---|---|
| **GitHub** | [qdrant/qdrant](https://github.com/qdrant/qdrant) + [qdrant/qdrant-dotnet](https://github.com/qdrant/qdrant-dotnet) |
| **Prerequisite** | Phase 3 (async jobs) complete |
| **Purpose** | Persistent vector database for semantic search over knowledge library documents |

**Do not add until:**
1. The async job model is running and LiteDB is storing results.
2. Document embeddings are being generated regularly.
3. Docker is available on the target workstation.
4. The TF-IDF fallback is demonstrably insufficient.

### Semantic Kernel

| | |
|---|---|
| **GitHub** | [microsoft/semantic-kernel](https://github.com/microsoft/semantic-kernel) |
| **Prerequisite** | Phase 3 (async jobs) + stable tool/plugin boundary |
| **Purpose** | LLM orchestration framework with agent, memory, and plugin support |

**Status:** ✅ Added in Phase 6. See [Phase 6 Libraries](#phase-6-libraries) for full documentation.

### Docling

| | |
|---|---|
| **GitHub** | [DS4SD/docling](https://github.com/DS4SD/docling) |
| **Prerequisite** | None (optional at any phase) |
| **Purpose** | Layout-aware document extraction (PDF tables, PPTX content, OCR) |

**Do not add until:**
1. PDF table extraction is needed (current `pypdf` is text-only).
2. PowerPoint content extraction is needed.
3. The added ~1GB install size is acceptable for the workstation.

---

## Phase 5 Libraries

### Qdrant.Client

| | |
|---|---|
| **GitHub** | [qdrant/qdrant-dotnet](https://github.com/qdrant/qdrant-dotnet) |
| **NuGet** | `Qdrant.Client` |
| **Version** | 1.17.0 |
| **License** | Apache-2.0 |
| **Added to** | `Foundry.Core.csproj` |

**Problem it solves:**
Document embeddings generated by `EmbeddingService` (via Ollama) need persistent vector storage for semantic search. Without a vector database, semantic similarity searches would require comparing the query embedding against all document embeddings in memory every time — O(n) per query.

**What it replaces:**
- `ml_document_embeddings.py` flat JSON file storage → Qdrant persistent vector index with cosine similarity search.
- In-memory keyword/TF-IDF matching in `KnowledgePromptContextBuilder` → Vector similarity search as the primary retrieval mechanism (keyword search remains as fallback).

**Canonical usage patterns:**

```csharp
// VectorStoreService wraps QdrantClient with graceful fallback
var vectorStore = new VectorStoreService(
    host: "localhost",
    port: 6334,
    collectionName: "office-knowledge",
    vectorSize: 768); // nomic-embed-text default dimension

// Upsert a document embedding with metadata
await vectorStore.UpsertAsync(
    docId: "uuid-string",
    vector: embeddingVector,
    metadata: new Dictionary<string, string>
    {
        ["path"] = "docs/guide.md",
        ["kind"] = "md",
        ["source"] = "Knowledge"
    });

// Search for similar documents
var results = await vectorStore.SearchAsync(queryVector, topK: 5);

// All operations return empty/false instead of throwing when Qdrant is unreachable
```

**Infrastructure requirement:**
Qdrant runs as a local Docker container:
```bash
docker run -d --name qdrant -p 6333:6333 -p 6334:6334 \
  -v qdrant_storage:/qdrant/storage \
  qdrant/qdrant
```

**Fallback behavior:**
All `VectorStoreService` methods catch exceptions and return graceful defaults (empty results, false) when Qdrant is unreachable. The existing keyword/TF-IDF search in `KnowledgePromptContextBuilder` continues to work as the fallback path.

---

## Phase 6 Libraries

### Microsoft.SemanticKernel

| | |
|---|---|
| **GitHub** | [microsoft/semantic-kernel](https://github.com/microsoft/semantic-kernel) |
| **NuGet** | `Microsoft.SemanticKernel` |
| **Version** | 1.74.0 |
| **License** | MIT |
| **Added to** | `Foundry.Core.csproj` |

**Problem it solves:**
`PromptComposer` manually concatenates system prompts, context, and user input into a single text block sent to Ollama. There is no support for tool calling, function chaining, or structured multi-turn memory beyond raw thread state. Each desk route shares the same code path (prompt composition → Ollama → response) with no desk-specific tooling or behavior.

**What it replaces:**
- Direct `IModelProvider.GenerateAsync()` calls in chat → SK `IChatCompletionService` with structured `ChatHistory`.
- Monolithic `BuildDeskConversationPromptLocked()` context blob → Per-agent tool functions that the LLM can invoke selectively.
- Flat thread message replay → `ChatHistory` with system/user/assistant roles and condensed conversation summaries.

**Canonical usage patterns:**

```csharp
// OfficeKernelFactory creates kernels wired to the local Ollama endpoint
var factory = new OfficeKernelFactory("http://localhost:11434");
var kernel = factory.CreateKernel("llama3.2");

// Each desk route has a dedicated DeskAgent subclass
var agent = new ChiefOfStaffAgent(kernel);
var response = await agent.ChatAsync(
    userMessage: "What should I work on?",
    threadMessages: thread.Messages,
    threadSummary: thread.Summary,
    contextBlock: contextInfo,
    cancellationToken: ct);

// Agent tools are SK kernel functions with [KernelFunction] attribute
[KernelFunction("get_office_state")]
[Description("Get a summary of the current office state.")]
public static string GetOfficeState(
    [Description("Current provider label")] string providerLabel,
    [Description("Current session focus")] string sessionFocus,
    [Description("Daily objective")] string dailyObjective)
{
    return $"Provider: {providerLabel} | Focus: {sessionFocus} | Objective: {dailyObjective}";
}
```

**Agent architecture:**
- `OfficeKernelFactory` — builds SK `Kernel` with Ollama's OpenAI-compatible `/v1/chat/completions` endpoint.
- `DeskAgent` (abstract base) — wraps `IChatCompletionService`, manages `ChatHistory`, and provides multi-turn memory with automatic summarisation of older messages.
- Five desk-specific agents: `ChiefOfStaffAgent`, `EngineeringDeskAgent`, `SuiteContextAgent`, `GrowthOpsAgent`, `MLEngineerAgent`.

**Fallback behavior:**
If an SK agent returns an empty response or throws, `SendChatAsync` falls back to the original direct `IModelProvider.GenerateAsync()` call with `PromptComposer` templates. The existing prompt-composition path is preserved as a reliable fallback.

**Multi-turn memory:**
When a desk thread exceeds the summary threshold (16 messages), the agent uses the LLM to generate a condensed summary of older messages. The summary is stored in `DeskThreadState.Summary` and injected as context on subsequent turns, keeping the effective context window manageable.

**Rules for AI agents:**
1. Always create agents through `OfficeKernelFactory.CreateKernel()` — never construct `Kernel` directly.
2. Agent tool functions must be `static` methods with `[KernelFunction]` and `[Description]` attributes.
3. When adding a new desk route, create a new `DeskAgent` subclass and register it in `FoundryOrchestrator.InitializeAgents()`.
4. The SK agent path is opt-in per chat call; the `PromptComposer` path remains as fallback.
5. Use version ≥ 1.71.0 to avoid CVE-2026-25592 (arbitrary file write via function calling).
