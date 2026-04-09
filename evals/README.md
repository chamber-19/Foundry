# Evaluation datasets

Tracked query/answer pairs for measuring pipeline quality.

## Files (to be created as data is collected)

- `rag-goldens.jsonl` — Query + expected document IDs + answer rubric for RAG retrieval eval
- `pr-scoring.jsonl` — PR snapshot + human decision + post-merge outcome for scorer eval
- `baselines/` — Current main-branch benchmark outputs for regression comparison

## Format

Each line in a .jsonl file is a self-contained JSON object:

```json
{"query": "How does the hydraulic valve work?", "expected_doc_ids": ["doc-123", "doc-456"], "answer_rubric": "Must reference pressure regulation", "groundedness": true}
```

These datasets will be versioned once DVC is added (not yet — collect 50+ entries first).
