"""
ML Retrain Script — PR Scoring Model

Loads historical decision memory, trains a GradientBoostingClassifier,
validates with 5-fold cross-validation, and persists the model only if
quality thresholds are met.

Usage:
    python scripts/scoring/retrain.py

Output:
    JSON summary on stdout with metrics and whether the model was saved.
    Model saved to:   State/ml-artifacts/pr-scorer-model.joblib
    Metrics saved to: State/ml-artifacts/model-metrics.json
"""

import json
import os
import re
import sys
from datetime import datetime, timezone
from typing import Any


MEMORY_FILE = os.path.join(os.path.expanduser("~"), ".office-rag-db", "decision-memory.json")

FEATURE_NAMES = [
    "total_size",
    "num_files",
    "has_tests",
    "has_docs",
    "test_ratio",
    "dir_spread",
    "additions",
    "deletions",
]

MIN_SAMPLES = 10
MIN_CV_F1 = 0.5


def _try_import_sklearn() -> bool:
    try:
        import sklearn  # noqa: F401
        return True
    except ImportError:
        return False


def _resolve_state_root() -> str:
    """Resolve the State root path: FOUNDRY_STATE_ROOT env var first, then platform default."""
    env_val = os.environ.get("FOUNDRY_STATE_ROOT", "")
    if env_val:
        return env_val
    if sys.platform == "win32":
        return r"C:\FoundryState"
    return os.path.join(os.path.expanduser("~"), "foundry-state")


def load_full_memory() -> list[dict[str, Any]]:
    """Load all decision memory for historical trend analysis."""
    if not os.path.exists(MEMORY_FILE):
        return []
    try:
        with open(MEMORY_FILE, "r") as f:
            return json.load(f)
    except Exception:
        return []


def _extract_features(entry: dict[str, Any], files: list[str]) -> list[float]:
    """Extract numeric features from a PR entry for ML classification."""
    additions = entry.get("additions", 0)
    deletions = entry.get("deletions", 0)
    total_size = additions + deletions
    num_files = len(files) if files else 0
    has_tests = 1.0 if any(re.search(r'test|spec|Tests\.cs', f, re.IGNORECASE) for f in files) else 0.0
    has_docs = 1.0 if any(re.search(r'\.md$|Docs/|README', f, re.IGNORECASE) for f in files) else 0.0
    test_ratio = sum(1 for f in files if re.search(r'test|spec', f, re.IGNORECASE)) / max(num_files, 1)

    dirs: set[str] = set()
    for f in files:
        parts = f.replace("\\", "/").split("/")
        if len(parts) >= 2:
            dirs.add(parts[0])
    dir_spread = len(dirs)

    return [
        float(total_size),
        float(num_files),
        has_tests,
        has_docs,
        test_ratio,
        float(dir_spread),
        float(additions),
        float(deletions),
    ]


def _build_feature_matrix(memory: list[dict[str, Any]]):
    """Build X (features) and y (labels) from decision memory."""
    X = []
    y = []
    for entry in memory:
        decision = entry.get("decision", "")
        if decision not in ("auto-merged", "rejected", "closed"):
            continue
        files = entry.get("files") or []
        features = _extract_features(entry, files)
        X.append(features)
        y.append(1 if decision == "auto-merged" else 0)
    return X, y


def main() -> None:
    if not _try_import_sklearn():
        print(json.dumps({
            "ok": False,
            "error": "scikit-learn is not installed. Install it with: pip install scikit-learn",
            "model_saved": False,
        }))
        return

    import numpy as np
    from sklearn.ensemble import GradientBoostingClassifier
    from sklearn.model_selection import cross_val_score
    try:
        import joblib
    except ImportError:
        print(json.dumps({
            "ok": False,
            "error": "joblib is not installed. Install it with: pip install joblib",
            "model_saved": False,
        }))
        return

    # Load decision memory
    if not os.path.exists(MEMORY_FILE):
        print(json.dumps({
            "ok": False,
            "error": "No decision memory file found. Run the scoring pipeline first to accumulate data.",
            "model_saved": False,
        }))
        return

    memory = load_full_memory()
    if not memory:
        print(json.dumps({
            "ok": False,
            "error": "Decision memory file is empty.",
            "model_saved": False,
        }))
        return

    # Build feature matrix
    X_list, y_list = _build_feature_matrix(memory)

    if len(X_list) < MIN_SAMPLES:
        print(json.dumps({
            "ok": False,
            "error": f"Insufficient training data: {len(X_list)} samples (minimum {MIN_SAMPLES} required).",
            "model_saved": False,
            "training_samples": len(X_list),
        }))
        return

    if len(set(y_list)) < 2:
        print(json.dumps({
            "ok": False,
            "error": "Training data contains only one class label. Need both merged and rejected/closed PRs.",
            "model_saved": False,
            "training_samples": len(X_list),
        }))
        return

    X_arr = np.array(X_list, dtype=np.float64)
    y_arr = np.array(y_list, dtype=np.int64)

    positive_samples = int(np.sum(y_arr == 1))
    negative_samples = int(np.sum(y_arr == 0))
    training_samples = len(y_arr)

    # Cross-validate
    clf = GradientBoostingClassifier(n_estimators=100, max_depth=4, random_state=42)
    cv_f1_scores = cross_val_score(clf, X_arr, y_arr, cv=5, scoring="f1")
    cv_acc_scores = cross_val_score(clf, X_arr, y_arr, cv=5, scoring="accuracy")

    cv_f1 = float(cv_f1_scores.mean())
    cv_f1_std = float(cv_f1_scores.std())
    cv_accuracy = float(cv_acc_scores.mean())

    model_saved = False
    save_error = None

    if cv_f1 >= MIN_CV_F1:
        # Train on full dataset and persist
        clf.fit(X_arr, y_arr)

        state_root = _resolve_state_root()
        artifacts_dir = os.path.join(state_root, "ml-artifacts")

        try:
            os.makedirs(artifacts_dir, exist_ok=True)

            model_path = os.path.join(artifacts_dir, "pr-scorer-model.joblib")
            joblib.dump(clf, model_path)

            # Build feature importances dict
            feature_importances = {
                name: round(float(imp), 6)
                for name, imp in zip(FEATURE_NAMES, clf.feature_importances_)
            }

            retrained_at = datetime.now(timezone.utc).isoformat()
            metrics = {
                "retrained_at": retrained_at,
                "cv_f1": round(cv_f1, 6),
                "cv_f1_std": round(cv_f1_std, 6),
                "cv_accuracy": round(cv_accuracy, 6),
                "training_samples": training_samples,
                "positive_samples": positive_samples,
                "negative_samples": negative_samples,
                "feature_importances": feature_importances,
                "feature_names": FEATURE_NAMES,
            }

            metrics_path = os.path.join(artifacts_dir, "model-metrics.json")
            with open(metrics_path, "w") as f:
                json.dump(metrics, f, indent=2)

            model_saved = True

        except Exception:
            save_error = "Failed to save model artifacts. Check that the State directory is accessible."
    else:
        save_error = (
            f"CV F1 score {cv_f1:.3f} is below threshold {MIN_CV_F1}. Model not persisted."
        )

    summary = {
        "ok": True,
        "model_saved": model_saved,
        "training_samples": training_samples,
        "positive_samples": positive_samples,
        "negative_samples": negative_samples,
        "cv_f1": round(cv_f1, 6),
        "cv_f1_std": round(cv_f1_std, 6),
        "cv_accuracy": round(cv_accuracy, 6),
    }
    if save_error:
        summary["warning"] = save_error

    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
