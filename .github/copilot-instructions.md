# Foundry — Copilot Instructions

## Project Overview
Foundry is a standalone ML scoring and reasoning pipeline that produces versioned artifacts for Suite. It is **not** a desktop app, chat agent, or GUI application. All operator interaction happens through a Discord bot and the ASP.NET Core broker API.

## Architecture
- **Foundry.Core** — Shared class library with ML models, services, and domain coordinators
- **Foundry.Broker** — ASP.NET Core minimal API that hosts ML endpoints, job scheduling, and health checks
- **Foundry.Core.Tests** — xUnit test project

## Key Conventions
- Use `Foundry.Models` for all data models
- Use `Foundry.Services` for all service classes
- Use `Foundry.Broker` for broker-specific code (endpoints, background workers)
- All ML operations go through `MLPipelineCoordinator` or `KnowledgeCoordinator`
- The `FoundryOrchestrator` is a thin coordinator — don't add business logic to it
- Use Polly resilience pipelines from `FoundryResiliencePipelines` for retries/circuit breakers
- Use LiteDB via `FoundryDatabase` for persistence
- All jobs are async-first with the `FoundryJobStore` queue pattern
- Python scripts live in `scripts/` and are invoked via `ProcessRunner`

## What NOT to Add
- No WPF, MAUI, or desktop UI code
- No chat agents, operator memory, or research services
- No Semantic Kernel agent orchestration
- No direct LLM chat routing
