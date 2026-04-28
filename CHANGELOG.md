# Changelog

All notable changes to Foundry are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

---

## [Unreleased]

### Changed

- Repository transferred from `Koraji95-coder/Foundry` to `chamber-19/Foundry`
- Documentation rewritten for chamber-19 family alignment:
  - `README.md` rewritten to reflect broker + Discord bot + future agent host scope
  - Historical docs (`docs/ARCHITECTURE.md`, `docs/PHASES-ROADMAP.md`, `docs/TECHNICAL-DEBT.md`,
    `docs/LIBRARY-DECISIONS.md`) marked with archival callouts
  - `docs/CONVENTIONS.md` updated to remove ML scaffold references
  - `.github/skills/discord-bot.md` updated to remove ML command references
  - `.github/skills/ml-pipeline.md` and `.github/skills/scoring-engine.md` marked as historical archives
  - `evals/README.md` rewritten for the agent evaluation harness
  - `.github/copilot-instructions.md` updated: repo location corrected, Mission section updated to reflect cleanup pass 1 completion
- Added `CONTRIBUTING.md`
- Added `CHANGELOG.md`

### Removed (cleanup pass 1 — prior PR)

- ML scoring pipeline: `MLAnalyticsService`, `MLPipelineCoordinator`, `MLResultStore`, `OnnxMLEngine`
- Training-data models: `TrainingAttempt*`, `MLForecastResult`, `MLAnalyticsResult`, `TopicMasterySummary`,
  `LearningProfile`, `SuiteMLArtifact`
- `scripts/scoring/` and `scripts/ml/` (training-side Python)
- Suite artifact export endpoints
