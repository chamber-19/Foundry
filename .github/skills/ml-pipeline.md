# ML pipeline patterns

## Three-engine cascade

1. Scikit-learn runs first — produces cluster labels and classification scores
2. PyTorch runs second — uses cluster labels as Qdrant metadata filters, produces embeddings and similarity scores
3. TensorFlow runs third — consumes cluster assignments and similarity scores as input features, produces forecasts

## EngineHandoff contract

Each engine's output feeds the next. The handoff is explicit — if engine 1 fails, engine 2 knows to skip the cluster-filtered path and search the full corpus instead. Never assume a previous engine succeeded.

## Heuristic fallbacks

Every Python script must work without its ML library installed. If scikit-learn/PyTorch/TensorFlow is missing, return a degraded but structurally valid response with `"engine": "fallback"` or `"engine": "heuristic"`. Never crash on import failure.

## Artifact export

All artifacts go to `State/ml-artifacts/` with `"source": "foundry-ml-pipeline"`, a semver version, a timestamp, and `"reviewRequired": true/false`. Suite consumes these through its review-first workflow.

## Job pattern

All ML operations are async by default. POST endpoints accept `?sync=true` for blocking mode, otherwise they enqueue a job via FoundryJobStore and return a job ID. The FoundryJobWorker processes the queue.
