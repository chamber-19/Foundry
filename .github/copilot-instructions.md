<!-- markdownlint-disable MD013 MD033 -->
# Copilot Instructions

> **Family-wide rules:** See [chamber-19/.github](https://github.com/chamber-19/.github/blob/main/.github/copilot-instructions.md) for Chamber 19 org-wide Copilot guidance. This file contains **repo-specific** rules that layer on top of the org rules.
>
> **Repo:** `chamber-19/Foundry`
> **Role:** Internal agent broker for the Chamber 19 tool family — receives GitHub webhooks and Discord commands, routes to local-LLM agents (Ollama), posts structured output back

---

## Mission — read this first

Foundry has been transferred to `chamber-19/Foundry` and cleanup pass 1 has removed the old ML scaffolding. The codebase is now a focused **agent broker**. Do not reintroduce ML training code, scoring pipelines, or Suite-style infrastructure.

**Keep and build on:**

- The Discord bot in `bot/` (operator UI)
- The ASP.NET Core broker in `src/Foundry.Broker/` (HTTP + job queue)
- The job persistence layer (`FoundryJobStore`, `FoundryDatabase`, `JobSchedulerStore`, `WorkflowStore`)
- The Ollama service (`OllamaService.cs`)
- The knowledge / RAG stack (`KnowledgeCoordinator`, `KnowledgeImportService`, `KnowledgeIndexStore`, `KnowledgeSearchService`, `EmbeddingService`, `VectorStoreService`) — this powers RAG over the chamber-19 repos
- `ProcessRunner`, `FoundryResiliencePipelines`, `IModelProvider` plumbing

**Already stripped (cleanup pass 1 — do not restore):**

- `MLAnalyticsService`, `MLPipelineCoordinator`, `MLResultStore`, `OnnxMLEngine` and their models
- `scripts/scoring/`, `scripts/ml/` (training-side)
- Suite artifact export endpoints (Suite is no longer a consumer)
- The training-data / mastery / forecasting models (`TrainingAttempt*`, `MLForecastResult`, `MLAnalyticsResult`, `TopicMasterySummary`, `LearningProfile`, `SuiteMLArtifact`, etc.)

When in doubt about whether something stays: if it is used by the agent layer, the broker, the bot, or the knowledge index, **keep**. If it exists to score PRs, train models, persist ML results, or export to Suite, **do not restore**.

---

## Architecture context

### Stack

- **.NET 10 SDK** — `Foundry.Core` (class library) + `Foundry.Broker` (ASP.NET Core minimal API host)
- **Python 3.10+** — Discord bot in `bot/` only. Python is not used for ML anymore.
- **Ollama** — local LLM runtime (`http://127.0.0.1:11434` by default). Pin model tags explicitly in `foundry.settings.json`.
- **Qdrant or Chroma** — optional vector store for the knowledge index
- **LiteDB** — embedded persistence for jobs, schedules, knowledge index metadata

### Process model

- The broker runs as a single ASP.NET Core process on `localhost:57420`
- Background workers (`FoundryJobWorker`, `JobSchedulerWorker`, `JobRetentionWorker`) drain the queue
- The Discord bot is a separate Python process that talks HTTP to the broker
- Agents are invoked by the broker, not by the bot directly — the bot is a thin UI

### Where Foundry sits in the family

| Caller | How it talks to Foundry |
| --- | --- |
| Operator (a human) | Discord slash commands → bot → broker HTTP API |
| `chamber-19/transmittal-builder` etc. | GitHub webhooks → broker `/webhooks/github` → agent layer |
| Future scheduled tasks | `JobSchedulerWorker` cron-like jobs |

Foundry is **not** consumed by other chamber-19 repos as a library. It is a **service** that observes them and acts on their behalf.

---

## Repo-specific rules — Foundry

## 1. Don't reintroduce the ML pipeline

The org-wide rules already say to push back on Suite-style infrastructure. For Foundry specifically that means:

- No new ML training code, model retraining loops, or feature engineering pipelines
- No new dependencies on TensorFlow.NET, TorchSharp, ML.NET, ONNX Runtime, or scikit-learn
- No restoration of `scripts/scoring/` or `scripts/ml/` even if asked — the user has decided these go
- The `EngineHandoff` contract is being renamed to `AgentHandoff`. If you see `EngineHandoff` referenced in new code, it's stale

If the user asks for a feature that sounds like the old ML pipeline, ask whether they want it as an **agent** (LLM call wrapped in deterministic pre/post-processing) instead. The answer is almost always yes.

## 2. Agent design contract

Agents are server-side workers that:

1. Receive a structured request (webhook payload, Discord command, scheduled trigger)
2. Run **deterministic pre-checks** first (semver parsing, ecosystem detection, special-case lookups) — no LLM
3. Use the LLM for **structured extraction**, never for open-ended judgment. Output must match a JSON schema validated by the caller.
4. Apply a **rule engine** to the structured output to produce a verdict
5. Post the verdict back via the GitHub API or Discord

This layered design is what makes a 7B–14B local model usable. If a proposed agent design relies on the LLM "deciding" something with no schema and no pre/post layers, push back — that pattern fails on local models.

## 3. Local-LLM constraints

- Pin Ollama model tags explicitly (e.g. `qwen2.5-coder:14b-instruct-q5_K_M`, not `qwen2.5-coder:14b`). Models update under you otherwise.
- Use Ollama's `format: "json"` mode plus a Pydantic / System.Text.Json validator with a one-shot retry on schema failure. Do NOT introduce grammar-constrained decoding (outlines, llama.cpp grammars) without a measured failure rate that justifies it.
- Every agent must have an eval set under `evals/` with at least 20 hand-labeled examples before it ships. Shadow-mode (post comments, do not auto-merge) for at least two weeks before enabling write actions.
- If Ollama is unreachable, agents must **fail open to "needs human review"** — never silently auto-approve.

## 4. Documentation currency

Every PR you produce **must** keep these docs in lockstep with the code:

| When you change … | You must also update … |
| --- | --- |
| `foundry.settings.json` shape or any field in `FoundrySettings.cs` | `README.md` § Configuration and `docs/` if a corresponding architecture doc exists |
| Any HTTP endpoint in `src/Foundry.Broker/Endpoints/` | `README.md` § Key Endpoints route table |
| `bot/foundry_bot.py` slash command surface | `bot/` README (create if absent) and `README.md` § Discord Bot |
| Agent contract (system prompt, JSON schema, model selection) | A note in `evals/README.md` and the agent's eval golden set |
| `bot/requirements.txt` | `README.md` § Prerequisites if Python version changes |
| Any user-facing behaviour | `CHANGELOG.md` `## [Unreleased]` (create the file if it doesn't exist) |

If a PR changes code but leaves a doc inconsistent, the PR is incomplete.

## 5. Markdown formatting

All `*.md` files must pass `markdownlint-cli2 "**/*.md"` against the rules in `.markdownlint.jsonc` (create one matching the other chamber-19 repos if absent). In short:

- Fenced code blocks: always declare a language. Use `text` for prose, ASCII art, or shell session output — never a bare block
- Use `_emphasis_` and `**strong**` consistently
- Surround headings, lists, and fenced blocks with blank lines
- First line of every file is a `#` H1; archival callouts go below it

## 6. Secrets and tokens

- `discordBotToken`, `githubAppPrivateKey`, and any Ollama auth credentials live in `foundry.settings.local.json` (gitignored), environment variables, or a secret manager — never in `foundry.settings.json`
- The example config (`bot_config.example.json`) carries placeholder values, never real ones
- When implementing a webhook receiver, validate the GitHub `X-Hub-Signature-256` header before processing — never trust the payload

## 7. Reference docs

- [`README.md`](../README.md) — top-level architecture, endpoint table, settings reference
- [`evals/README.md`](../evals/README.md) — eval dataset conventions (build evals before agents)
- [`bot/foundry_bot.py`](../bot/foundry_bot.py) — Discord bot entry point
- [`src/Foundry.Broker/Program.cs`](../src/Foundry.Broker/Program.cs) — broker startup and DI wiring
- `chamber-19/.github` org rules — family-wide conventions, MCP server usage, design system

If you find a discrepancy between code and these docs, fixing the doc is part of your job, not someone else's.
