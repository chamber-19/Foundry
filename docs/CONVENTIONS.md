# Foundry — Coding Conventions

> **Purpose:** Establish patterns that AI agents and contributors must follow when writing code in this repository. Foundry is a standalone ML pipeline — there is no desktop UI, chat agent system, or research service.

---

## General Principles

1. **Incremental, not rewrite.** Never propose replacing an entire service or subsystem in one PR. Make the smallest change that improves the specific problem.

2. **Keep the service boundary stable.** Public interfaces (`IModelProvider`, store public methods, orchestrator public methods) do not change unless there is a clear versioning plan.

3. **Fallback before failure.** Every external call (Ollama, web, subprocess) must have a fallback path. If the call fails after retry, produce a degraded but usable result — not an exception.

4. **Local-first, no cloud dependencies.** Foundry runs entirely on the local workstation (or across machines via Tailscale). Do not add cloud APIs, SaaS dependencies, or remote database connections. All data stays local.

5. **Single NuGet per problem.** Do not add multiple libraries for the same concern. Consult `LIBRARY-DECISIONS.md` before adding any dependency.

---

## C# Code Style

### Naming
- **Classes:** PascalCase, descriptive nouns (`OllamaService`, `TrainingStore`, `FoundryJobWorker`).
- **Interfaces:** `I` prefix (`IModelProvider`).
- **Methods:** PascalCase, verb-first (`GenerateAsync`, `LoadSummaryAsync`, `RunMLAnalyticsAsync`).
- **Private fields:** `_camelCase` with underscore prefix (`_httpClient`, `_gate`, `_logger`).
- **Constants:** PascalCase (`OllamaProviderId`, `DefaultPort`).
- **Records:** PascalCase with positional parameters (`record ChatSendRequest(string Prompt, string? RouteOverride)`).

### Async
- All I/O methods must be `async Task<T>` or `async Task`.
- Always accept `CancellationToken cancellationToken = default` as the last parameter.
- Always pass `cancellationToken` to downstream async calls.
- Use `ConfigureAwait(false)` in library code (`Foundry.Core`). Omit in broker/worker code (`Foundry.Broker`).

### Error Handling
```csharp
// Pattern 1: External call with retry + fallback (preferred)
try
{
    return await _pipeline.ExecuteAsync(async ct =>
    {
        return await _client.ChatAsync(request, ct);
    }, cancellationToken);
}
catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
{
    _logger.LogWarning(ex, "Ollama call failed for {Route}, using fallback", route);
    return BuildFallbackResponse(route);
}

// Pattern 2: Orchestrator validation
if (string.IsNullOrWhiteSpace(prompt))
    throw new ArgumentException("Prompt is required.", nameof(prompt));

// Pattern 3: Orchestrator state validation
if (activeJob is null)
    throw new InvalidOperationException("No active job.");

// Pattern 4: Broker endpoint catch
catch (ArgumentException ex)
{
    return Results.BadRequest(new { error = ex.Message });
}
catch (Exception ex)
{
    logger.LogError(ex, "Endpoint {Endpoint} failed", endpointName);
    // ⚠ Never return ex.Message or ex.ToString() — use a static generic string.
    // The full exception (message + stack trace) is preserved in the server log.
    // See Docs/stack-trace-exposure-remediation.md for the full policy.
    return Results.Problem(
        detail: "An unexpected error occurred. See server logs for details.",
        statusCode: 500
    );
}
```

### Logging
- Use `ILogger<T>` injected via constructor. Never use `Console.WriteLine`.
- Use structured logging: `_logger.LogInformation("Generated {Action} for {Route}", action, route)`.
- Log levels:
  - `Debug` — Internal state details useful during development only.
  - `Information` — Successful operations with key metrics (duration, count).
  - `Warning` — Fallback triggered, retry attempted, degraded result returned.
  - `Error` — Unhandled exception caught at endpoint level.
- Do not log sensitive data (user prompts, training answers, knowledge content).

### Dependency Injection
- Services are registered as singletons in `Program.cs`.
- Use constructor injection for all dependencies.
- `ILogger<T>` is always available via the DI container.
- When adding a new service, register it in `Program.cs` alongside existing registrations.

### JSON Serialization
- Use `System.Text.Json` (not Newtonsoft.Json).
- Default options: `new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true }`.
- Use records with `init` properties for deserialization targets.

---

## Project Structure

```
Foundry/
├── src/
│   ├── Foundry.Core/         # Shared class library (net10.0, no Windows dependency)
│   │   ├── Models/             # Shared data models (Foundry.Models namespace)
│   │   └── Services/           # All services: coordinators, stores, ML engines, external clients
│   └── Foundry.Broker/       # ASP.NET Core minimal API (localhost broker)
│       ├── Endpoints/          # IEndpointRouteBuilder extension classes + validators (one file per domain group)
│       └── Program.cs          # Infrastructure setup only (DI, middleware, hosted services)
├── tests/
│   └── Foundry.Core.Tests/   # xUnit tests
├── scripts/                  # Python ML scripts, PowerShell automation, RAG pipeline, scoring engine
│   ├── ml/                   # ML suite artifacts, batch scripts
│   ├── rag/                  # RAG pipeline scripts
│   ├── scoring/              # Scoring engine and preprocessor
│   ├── automation/           # Auto-review and CI scripts
│   └── commands/             # Discord command helpers
├── bot/                      # Discord bot (Python, discord.py) — sole operator interface
├── docs/                     # Architecture docs, library decisions, conventions
├── schemas/                  # Frozen feature schemas (do not modify)
└── evals/                    # Evaluation datasets
```

### Where to Put New Code

| Type of code | Location | Why |
|-------------|----------|-----|
| New shared service | `src/Foundry.Core/Services/` | All services live here; registered in Broker via DI |
| New shared model | `src/Foundry.Core/Models/` | Single model location for the whole pipeline |
| Broker-only endpoint logic | `src/Foundry.Broker/Endpoints/` | One static class per domain group; call `app.Map*Endpoints(logger)` in `Program.cs` |
| Broker-only background service | `src/Foundry.Broker/` (new file) | Register in Program.cs |
| FluentValidation validators | `src/Foundry.Broker/Endpoints/` | Co-located with request records in the same endpoint file |
| New NuGet for shared services | `src/Foundry.Core/Foundry.Core.csproj` | Core is the shared dependency |
| New NuGet for Broker-only features | `src/Foundry.Broker/Foundry.Broker.csproj` | Keep Core dependency-lean |
| Tests | `tests/Foundry.Core.Tests/` | All tests go here |

---

## API Conventions

### Endpoint Pattern
```csharp
app.MapPost("/api/{resource}/{action}", async (RequestType request, FoundryOrchestrator orchestrator, CancellationToken ct) =>
{
    // 1. Validate (FluentValidation)
    var validation = new RequestValidator().Validate(request);
    if (!validation.IsValid)
        return Results.BadRequest(new { errors = validation.Errors.Select(e => e.ErrorMessage) });

    // 2. Execute
    try
    {
        var result = await orchestrator.MethodAsync(request.Param, ct);
        var state = await orchestrator.GetStateAsync(ct);
        return Results.Ok(new { result, state });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Endpoint {Endpoint} failed", "/api/{resource}/{action}");
        // ⚠ Never return ex.Message — use a static generic string.
        // See Docs/stack-trace-exposure-remediation.md for the full policy.
        return Results.Problem(
            detail: "An unexpected error occurred. See server logs for details.",
            statusCode: 500
        );
    }
});
```

### Response Shape
- Success: `{ result, state }` or `{ result1, result2, state }`.
- Validation error: `{ errors: ["message1", "message2"] }`.
- Business rule error: `{ error: "message" }`.
- Server error: RFC 7807 Problem Details.

### Job Endpoints (Phase 3)
- Submit: `POST /api/ml/{type}` → `{ jobId, status: "queued" }`.
- Poll: `GET /api/jobs/{jobId}` → `{ id, type, status, createdAt, completedAt, error }`.
- Result: `GET /api/jobs/{jobId}/result` → `{ ...result }`.
- List: `GET /api/jobs` → `{ jobs: [...] }`.

---

## Testing Conventions

- Framework: xUnit.
- Test file naming: `{Feature}Tests.cs` (e.g., `OfficeBrokerLogicTests.cs`).
- Test method naming: `MethodName_Condition_ExpectedResult` (e.g., `BuildSummary_WithAttempts_ReturnsWeakTopics`).
- Arrange-Act-Assert pattern.
- No mocking framework yet — tests operate on real instances with in-memory data.
- Tests must pass on Linux (`dotnet test Foundry.Core.Tests`).
- Exception: `ResolveOfficeRootPath` test is known to fail on Linux (Windows path conventions).

---

## Python Script Conventions

- Scripts live in `scripts/` subdirectories (`scripts/ml/`, `scripts/rag/`, `scripts/scoring/`, etc.).
- Called via `ProcessRunner.RunAsync("python", scriptPath + " --input " + inputFile)`.
- Input: JSON file path passed as `--input` argument.
- Output: JSON written to stdout.
- Error: Written to stderr, captured by ProcessRunner.
- Exit code: 0 = success, non-zero = failure.
- All scripts must have heuristic fallback when ML libraries are not installed.
- Do not add new Python dependencies without updating the ML setup instructions in README.md.

---

## Resilience Conventions (Phase 2+)

- Every external HTTP call wraps in a named Polly `ResiliencePipeline`.
- Every subprocess call wraps in a named Polly `ResiliencePipeline`.
- Pipeline names: `ollama`, `web-research`, `python-subprocess`.
- Retry counts: 3 for Ollama, 2 for web, 1 for subprocess.
- Always log retry attempts at `Warning` level.
- Circuit breaker for long-lived services (Ollama). No circuit breaker for one-shot operations.

---

## Persistence Conventions (Phase 2+)

- Single database file: `foundry.db` in the state root directory.
- One `LiteDatabase` instance per application lifetime (thread-safe).
- Collection naming: `snake_case` (e.g., `training_attempts`, `session_state`, `jobs`).
- Index fields used in queries.
- Upsert for entities with stable IDs. Insert for append-only entities.
- Enforce max-item limits in store logic, not in the database.
- Keep JSON export for Dropbox compatibility (periodic export, not primary storage).

---

## Git & PR Conventions

- Branch naming: `feature/{description}`, `fix/{description}`, `docs/{description}`.
- Commit messages: imperative mood, concise (`Add Serilog logging to Broker`, `Replace regex HTML parsing with AngleSharp`).
- One concern per PR. Do not mix unrelated changes.
- PRs should pass `dotnet build` and `dotnet test Foundry.Core.Tests` before merge.
- Docs-only PRs do not require test changes.
