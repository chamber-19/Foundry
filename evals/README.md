# Evaluation datasets

Tracked input/output pairs for measuring agent quality. Every agent that ships must have at least 20 hand-labeled examples in its golden set before it leaves shadow mode.

## Directory layout

```text
evals/
├── README.md              # This file
├── dep-reviewer/          # Dep-reviewer agent (Dependabot PR triage)
│   ├── goldens.jsonl      # Hand-labeled examples (to be created)
│   └── baselines/         # Benchmark outputs from main branch
└── <agent-name>/          # One subdirectory per agent
```

## Golden set format

Each line in a `.jsonl` file is a self-contained JSON object:

```json
{
  "id": "dr-001",
  "input": { "ecosystem": "npm", "package": "express", "from": "4.18.2", "to": "4.19.2" },
  "expected_verdict": "auto-mergeable",
  "expected_reason": "Patch bump, no breaking changes in changelog",
  "label_source": "human",
  "labeled_at": "2026-04-28"
}
```

## Conventions

- Minimum 20 examples per agent before the agent exits shadow mode.
- Label source must be `"human"` — never use LLM-generated labels as ground truth.
- `baselines/` holds benchmark output snapshots from `main`. A passing CI eval means the current output matches or improves on the baseline.
- Increment the baseline after a deliberate quality improvement — never to silence a regression.

## Running evals (placeholder)

Eval tooling is not yet implemented. Track progress in the [Foundry issue tracker](https://github.com/chamber-19/Foundry/issues).
