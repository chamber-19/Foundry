<!-- markdownlint-disable MD013 -->
# Copilot Instructions — Foundry

> **Repo:** `chamber-19/Foundry`
> **Role:** Local agent broker for Chamber 19 dependency monitoring, GitHub/Discord operations, RAG, and Ollama-backed summaries.

Use the org `.github/copilot-instructions.md` as shared reference guidance,
but this file is the repo-specific source of truth. The org file is not
auto-loaded; conventions that apply here are restated below where they
matter.

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

The knowledge stack is preserved for future cross-repo Q&A and code-aware
agent context. It is not currently wired into any shipping agent. Don't
remove it, but don't expand it until an agent actually consumes it.

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

Before deleting any stripped type, grep the keep-list for references and
remove them. The strip is not complete until `dotnet build Foundry.sln`
passes with the deleted files. Do not leave dangling parameter types,
DI registrations, or migration entries pointing at removed classes.

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

Once an eval harness exists, agent eval runs go under `evals/<agent-name>/`
and are invoked via `dotnet test Foundry.sln --filter Category=Eval`. Until
that wiring exists, leave the slot empty rather than faking it.

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
schema and no pre/post layers, **do not implement it.** Surface the
concern in the PR description and request a redesign that fits the
layered contract above. That pattern produces the same flaky results the
old ML pipeline did.

## Local-LLM constraints

- **Pin Ollama model tags explicitly.** Use
  `qwen2.5-coder:14b-instruct-q5_K_M`, not `qwen2.5-coder:14b`. Models
  update under you otherwise. Pinned tags live in `foundry.settings.json`
  under `ollama.models.*` and are referenced by agents through
  `IModelProvider`. Never hardcode model tags in agent code.
- **Use Ollama's `format: "json"` mode plus a `System.Text.Json` /
  Pydantic validator with a one-shot retry on schema failure.** Do NOT
  introduce grammar-constrained decoding (outlines, llama.cpp grammar
  mode) — it adds complexity for marginal gain at this scale.
- **Eval set first, agent second, write actions last.** Every agent must
  have an eval set under `evals/<agent-name>/` with at least 20
  hand-labeled examples *before write actions are enabled*. Order is:
  build the labeled dataset → write the agent against it → run shadow
  mode (post comments/notifications, do not auto-merge or auto-approve)
  for at least two weeks → enable write actions only after agent
  agreement with labels is at or above the threshold defined per agent.
- Shadow mode is the default state for any new agent. Promotion out of
  shadow mode is a deliberate PR with eval results attached.

## Review-Critical Rules

- **Fail-open contract.** Agents must fail open to `needs human review` if
  Ollama, GitHub, or any other required service is unavailable. Never
  silently auto-approve, auto-merge, or skip notification on degraded
  state.
- **Polling, not webhooks.** Dependency monitoring uses scheduled polling
  against the GitHub API. If a future agent needs webhook ingress, HMAC
  signature validation is mandatory and the public ingress path must be
  documented and reviewed before merge.
- **Secrets handling.** Tokens and credentials belong in
  `foundry.settings.local.json`, environment variables, or a secret
  manager. Never commit them. The local-settings file is gitignored —
  keep it that way.
- **Docs follow code.** Endpoint, settings, or agent-contract changes
  require corresponding README and config-doc updates in the same PR.

Path-specific rules live under `.github/instructions/*.instructions.md`
with `applyTo:` frontmatter. Subsystem detail (per-agent prompts, RAG
indexing internals, eval harness conventions) goes there, not in this
file.

## Reference

Behavior conventions (push back, simplicity first, surgical changes,
goal-driven execution) follow the org `.github/copilot-instructions.md`
and the Karpathy-inspired guidelines at
<https://github.com/forrestchang/andrej-karpathy-skills>. When this file
and the org file disagree, this file wins for Foundry-specific work.