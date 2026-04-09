# PR scoring engine

## Architecture

preprocessor.py → gates (pass/fail) → signals (0-5 points) → pre-score (4-9) → LLM adjustment (±1) → final score (3-10)

## Gates (fail = skip LLM entirely)

- CI status: failure = instant reject
- Duplicate check: 50%+ file overlap or 70%+ title similarity with recent merges = reject

## Signals

- Has tests: 0 (none), 1 (test files only), 2 (test + prod files)
- PR size: 0 (>500 lines), 1 (≤500 lines)
- Commit format: 0 (<50% conventional), 1 (≥50% conventional)
- Churn risk: 0 (≥5 commits in 30 days on touched files), 1 (low churn)

## Score clamping

Always clamp: `final_score = max(3, min(10, pre_score + llm_adjustment))`
Always validate LLM adjustment: `llm_adjustment = max(-1, min(1, llm_adjustment))`

## Feature schema

`schemas/feature-v1.json` is FROZEN. Every training sample must validate against it. Never modify the schema — if new features are needed, create `feature-v2.json`.

## Data files

- `raw.jsonl` — append-only, raw PR data before validation
- `features.jsonl` — validated feature vectors ready for training
- `decision-memory.json` — historical scoring decisions
