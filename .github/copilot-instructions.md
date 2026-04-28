<!-- markdownlint-disable MD013 -->
# Copilot Instructions — Foundry

> **Repo:** `chamber-19/Foundry`
> **Role:** Local agent broker for Chamber 19 dependency monitoring, GitHub/Discord operations, RAG, and Ollama-backed summaries.

Use Chamber 19 shared conventions as reference guidance, but this file is the
repo-specific source of truth.

## Mission — read this first

Foundry is the **agent broker** for the Chamber 19 tool family. It receives
GitHub webhooks and Discord commands, routes work to local-LLM agents
(Ollama), and posts structured output back. It is **not** an ML training
pipeline, a PR scoring system, or a Suite artifact exporter — those were
prior incarnations and have been retired.

**Keep and build on:**

- The Discord bot in `bot/` (operator UI)
- The ASP.NET Core broker in `src/Foundry.Broker/` (HTTP + job queue)
- The job persistence layer (`FoundryJobStore`, `FoundryDatabase`,
  `JobSchedulerStore`, `WorkflowStore`)
- The Ollama service (`OllamaService.cs`)
- The knowledge / RAG stack (`KnowledgeCoordinator`,
  `KnowledgeImportService`, `KnowledgeIndexStore`, `KnowledgeSearchService`,
  `EmbeddingService`, `VectorStoreService`)
- `ProcessRunner`, `FoundryResiliencePipelines`, `IModelProvider` plumbing

**Strip and do not reintroduce:**

- `MLAnalyticsService`, `MLPipelineCoordinator`, `MLResultStore`,
  `OnnxMLEngine` and their models
- `scripts/scoring/`, `scripts/ml/` (training-side)
- Suite artifact export endpoints (Suite is no longer a consumer)
- The training-data / mastery / forecasting models in
  `Foundry.Core/Models/` (`TrainingAttempt*`, `MLForecastResult`,
  `MLAnalyticsResult`, `TopicMasterySummary`, `LearningProfile`,
  `SuiteMLArtifact`, etc.)

When in doubt about whether something stays: if it's used by the agent
layer, the broker, the bot, or the knowledge index, **keep**. If it
exists to score PRs, train models, persist ML results, or export to Suite,
**strip**.

## Current Shape

- Broker: ASP.NET Core minimal API in `src/Foundry.Broker/`.
- Core services/models: `src/Foundry.Core/`.
- Operator UI: Discord bot in `bot/`.
- Local model runtime: Ollama at `http://127.0.0.1:11434` by default.

## Build And Test

```text
dotnet restore Foundry.sln
dotnet build Foundry.sln
dotnet test Foundry.sln

cd bot
pip install -r requirements.txt
python -m py_compile foundry_bot.py
```

## Non-Goals

- Do not restore ML training, PR scoring, Suite artifact export, TensorFlow,
  TorchSharp, ML.NET, ONNX Runtime, scikit-learn, or `scripts/ml` /
  `scripts/scoring` flows.
- If a feature sounds like scoring, implement it as deterministic checks plus
  optional LLM structured extraction plus rule-based output.

## Agent design contract

Agents are server-side workers that:

1. **Receive a structured request** (webhook payload, Discord command,
   scheduled trigger).
2. **Run deterministic pre-checks first** (semver parsing, ecosystem
   detection, special-case lookups) — no LLM.
3. **Use the LLM for structured extraction, never for open-ended judgment.**
   Output must match a JSON schema validated by the caller.
4. **Apply a rule engine to the structured output** to produce a verdict.
5. **Post the verdict back** via the GitHub API or Discord.

This layered design is what makes a 7B–14B local model usable. If a
proposed agent design relies on the LLM "deciding" something with no
schema and no pre/post layers, push back — that pattern produces the
same flaky results that the old ML pipeline did.

## Local-LLM constraints

- **Pin Ollama model tags explicitly.** Use
  `qwen2.5-coder:14b-instruct-q5_K_M`, not `qwen2.5-coder:14b`. Models
  update under you otherwise.
- **Use Ollama's `format: "json"` mode plus a Pydantic / System.Text.Json
  validator with a one-shot retry on schema failure.** Do NOT introduce
  grammar-constrained decoding (outlines, llama.cpp grammar mode) — it
  adds complexity for marginal gain at this scale.
- **Every agent must have an eval set under `evals/` with at least 20
  hand-labeled examples before it ships.** Shadow-mode (post comments, do
  not auto-merge) for at least two weeks before enabling write actions.
- **If Ollama is unreachable, agents must fail open to "needs human
  review"** — never silently auto-approve.

## Review-Critical Rules

- Agents fail open to `needs human review` if Ollama or GitHub is unavailable.
- GitHub webhook receivers must validate signatures, but current dependency
  monitoring uses scheduled polling instead of webhooks.
- Secrets belong in `foundry.settings.local.json`, environment variables, or a
  secret manager. Never commit tokens.
- Endpoint or settings changes require README/config docs updates.

Path-specific rules live under `.github/instructions/`.
