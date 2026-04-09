# Foundry — Copilot Instructions

## What this project is

Foundry is a standalone ML scoring and reasoning pipeline. It has three engines (scikit-learn for classification, PyTorch for embeddings, TensorFlow for forecasting) that run in sequence, cross-feeding through an EngineHandoff contract. It produces versioned artifacts consumed by a separate project called Suite. All operator interaction happens through a Discord bot — there is no GUI.

The runtime backbone is: ASP.NET Core broker (Foundry.Broker) → Ollama for LLM inference → Qdrant for vector storage → LiteDB for persistence → PowerShell/Python scripts for automation.

## Think before coding

Do not assume. Do not hide confusion. Surface tradeoffs.

- If a change touches the ML pipeline (MLPipelineCoordinator, KnowledgeCoordinator, scoring scripts), state what you think the change will affect before implementing
- If a change could break the EngineHandoff contract between engines, stop and explain
- If you're unsure whether something is ML-related or leftover from the old Office/DailyDesk codebase, ask — don't guess
- Present multiple approaches when the tradeoff isn't obvious (e.g., "we could add this to the coordinator or the endpoint — here's why each matters")

## Simplicity first

Minimum code that solves the problem. Nothing speculative.

- No abstractions for single-use code
- No "flexibility" or "configurability" that wasn't requested
- If 200 lines could be 50, rewrite it
- The three-engine architecture is already the abstraction layer — don't add more layers on top of it
- Python scripts should stay as standalone scripts invoked by ProcessRunner, not get wrapped in elaborate frameworks

## Surgical changes

Touch only what you must. Clean up only your own mess.

- Don't "improve" adjacent code, comments, or formatting in files you're editing
- Don't refactor the orchestrator, coordinators, or endpoint wiring unless specifically asked
- Match existing patterns: services use constructor injection, endpoints use minimal API style, Python scripts read from stdin/file and write JSON to stdout
- If you notice something wrong in unrelated code, mention it in a comment — don't fix it silently

## Goal-driven execution

Define success criteria before implementing. Every change should be verifiable.

- For ML changes: "these tests pass" or "this endpoint returns this shape"
- For script changes: "running this script produces this output"
- For model changes: "dotnet build succeeds with 0 errors, 0 warnings"
- State the verification step explicitly so it can be run

## Architecture rules

### Project structure
- `src/Foundry.Core/` — Models and Services. All ML logic lives here.
- `src/Foundry.Broker/` — ASP.NET Core minimal API. Thin endpoints that delegate to coordinators.
- `tests/Foundry.Core.Tests/` — xUnit tests.
- `scripts/` — Python ML scripts, PowerShell automation, RAG pipeline, scoring engine.
- `bot/` — Discord bot (Python, discord.py). Sole operator interface.
- `schemas/` — Frozen feature schemas. Do not modify.

### Namespaces
- `Foundry.Models` for all data models
- `Foundry.Services` for all services
- `Foundry.Broker` for broker-specific code

### Key patterns
- All ML operations route through `MLPipelineCoordinator` or `KnowledgeCoordinator`
- `FoundryOrchestrator` is a thin delegator — never add business logic to it
- Jobs are async-first using `FoundryJobStore` queue pattern
- Python scripts are invoked via `ProcessRunner` — they read JSON from file/stdin and write JSON to stdout
- Every Python script has heuristic fallbacks when ML libraries aren't installed
- The `EngineHandoff` contract means scikit-learn's cluster labels flow into PyTorch's metadata, and PyTorch's similarity scores flow into TensorFlow's input features

### Environment
- .NET 10, target `net10.0` (never `net10.0-windows`)
- Python 3.13 via miniconda3
- PowerShell 7 (`pwsh`, not `powershell`)
- Shared state path: `FOUNDRY_STATE_ROOT` env var → `C:\FoundryState` fallback
- Repo path: `FOUNDRY_REPO_ROOT` env var → `%USERPROFILE%\Documents\GitHub\Foundry` fallback

### What does NOT belong in this repo
- No WPF, MAUI, Blazor, or any desktop/web UI code
- No chat agents, operator memory, or research services (these were in the old Office repo)
- No Semantic Kernel agent orchestration
- No direct LLM chat routing — all LLM calls go through the broker or scripts
- No references to "DailyDesk", "Office", or the old repo structure
- No Dropbox paths — shared state uses Tailscale SMB or FOUNDRY_STATE_ROOT

### Multi-machine context
This pipeline runs across 3 machines connected via Tailscale:
- DUSTIN (i7-14700K, 32GB, 7700 XT) — primary, runs broker + GPU inference
- DUSTINWARD (Ultra 7 155H, 64GB) — batch processing, CPU inference
- PRETTYANDPINK (Ryzen 9 5900X, 64GB) — overnight embeddings, shared machine

The broker binds to a configurable host (default 127.0.0.1, overridable to Tailscale IP). Scripts use `FOUNDRY_STATE_ROOT` which points to a shared SMB mount on other machines.