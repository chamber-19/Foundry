"""
PR Preprocessor v2 — Deterministic + ML-enhanced scoring before LLM.

GATES (pass/fail — fail = skip LLM entirely):
  - Builds?       → CI status via GitHub API
  - Duplicate?    → file overlap + title similarity vs decision memory
  - Conflict?     → file overlap with other open PRs (NEW)

SIGNALS (0-10 points):
  - Has tests?         → 0, 1, or 2
  - PR size            → 0 or 1
  - Commit format      → 0 or 1
  - Churn risk         → 0 or 1
  - File coherence     → 0 or 1  (NEW: are changed files in related directories?)
  - Security patterns  → 0 or 1  (NEW: no secrets/credentials in diff)
  - Doc coverage       → 0 or 1  (NEW: docs updated when public API changes)
  - Historical trend   → 0-2     (NEW: ML-based author/area success rate)

Output: JSON with gate results, signal breakdown, pre-score, confidence
Pre-score formula: 4 (gate bonus) + signals (0-10) = 4-14 range, normalized to 1-10
LLM adjusts final ±1 = possible 0-10 range
"""

import json
import math
import sys
import os
import re
import subprocess
from datetime import datetime, timedelta, timezone
from difflib import SequenceMatcher
from typing import Any

MEMORY_FILE = os.path.join(os.path.expanduser("~"), ".office-rag-db", "decision-memory.json")


def _try_import_sklearn() -> bool:
    try:
        import sklearn  # noqa: F401
        return True
    except ImportError:
        return False


def load_memory(hours=4):
    if not os.path.exists(MEMORY_FILE):
        return []
    with open(MEMORY_FILE, "r") as f:
        memory = json.load(f)
    cutoff = datetime.now(timezone.utc) - timedelta(hours=hours)
    recent = []
    for entry in memory:
        if entry.get("decision") == "auto-merged":
            try:
                ts = entry["timestamp"].replace("Z", "+00:00")
                if "+" not in ts and "-" not in ts[10:]:
                    ts += "+00:00"
                parsed = datetime.fromisoformat(ts)
                if parsed > cutoff:
                    recent.append(entry)
            except Exception:
                pass
    return recent


def load_full_memory() -> list[dict[str, Any]]:
    """Load all decision memory for historical trend analysis."""
    if not os.path.exists(MEMORY_FILE):
        return []
    try:
        with open(MEMORY_FILE, "r") as f:
            return json.load(f)
    except Exception:
        return []


# ========== GATES ==========

def gate_builds(ci_status):
    if ci_status == "failure":
        return False, "CI checks failed"
    return True, f"CI status: {ci_status}"


def gate_duplicate(pr_title, pr_files, recent_merges):
    for merged in recent_merges:
        # File overlap
        merged_files = merged.get("files") or []
        if pr_files and merged_files:
            overlap = set(pr_files) & set(merged_files)
            if len(pr_files) > 0:
                overlap_pct = len(overlap) / len(pr_files)
                if overlap_pct >= 0.5:
                    return False, f"50%+ file overlap with #{merged.get('pr_number')} ({merged.get('title')})"

        # Title similarity
        merged_title = merged.get("title", "")
        sim = SequenceMatcher(None, pr_title.lower(), merged_title.lower()).ratio()
        if sim > 0.7:
            return False, f"Title {sim:.0%} similar to #{merged.get('pr_number')} ({merged.get('title')})"

    return True, "No duplicates found"


def gate_conflict(pr_number, pr_files, open_pr_files):
    """Check if this PR has high file overlap with other open PRs."""
    conflicts = []
    for other_number, other_files in (open_pr_files or {}).items():
        if other_number == pr_number:
            continue
        if not pr_files or not other_files:
            continue
        overlap = set(pr_files) & set(other_files)
        if len(overlap) >= 3 or (len(pr_files) > 0 and len(overlap) / len(pr_files) >= 0.5):
            conflicts.append(f"#{other_number} ({len(overlap)} shared files)")
    if conflicts:
        return False, f"Conflict risk with: {', '.join(conflicts[:3])}"
    return True, "No conflict risk"


# ========== SIGNALS ==========

def signal_has_tests(files):
    test_patterns = [r'test', r'spec', r'\.test\.', r'\.spec\.', r'Tests\.cs$', r'_test\.']
    test_files = [f for f in files if any(re.search(p, f, re.IGNORECASE) for p in test_patterns)]
    prod_files = [f for f in files if f not in test_files]

    if test_files and prod_files:
        return 2, f"{len(test_files)} test + {len(prod_files)} prod files"
    elif test_files:
        return 1, f"{len(test_files)} test files only"
    else:
        return 0, "No test files in diff"


def signal_pr_size(additions, deletions):
    total = additions + deletions
    if total > 500:
        return 0, f"{total} lines — large PR"
    return 1, f"{total} lines — reasonable"


def signal_commit_format(commit_messages):
    if not commit_messages:
        return 0, "No commits"
    pattern = r'^(feat|fix|test|chore|docs|refactor|style|ci|perf|build)[\(:]'
    good = sum(1 for m in commit_messages if re.match(pattern, m, re.IGNORECASE))
    ratio = good / len(commit_messages)
    if ratio >= 0.5:
        return 1, f"{good}/{len(commit_messages)} conventional"
    return 0, f"{good}/{len(commit_messages)} conventional"


def signal_churn_risk(files, repo_path=None):
    if not repo_path or not os.path.exists(repo_path):
        return 1, "Skipped — no local repo"
    try:
        high_churn = []
        for f in files[:10]:
            result = subprocess.run(
                ["git", "log", "--since=30 days ago", "--format=%H", "--", f],
                capture_output=True, text=True, cwd=repo_path
            )
            count = len(result.stdout.strip().split("\n")) if result.stdout.strip() else 0
            if count >= 5:
                high_churn.append(f"{f} ({count}x)")
        if high_churn:
            return 0, f"High churn: {', '.join(high_churn[:3])}"
        return 1, "Low churn"
    except Exception:
        return 1, "Churn check failed"


def signal_file_coherence(files):
    """Check if changed files are in related directories (coherent change)."""
    if not files or len(files) <= 1:
        return 1, "Single file or empty — coherent"

    directories = set()
    for f in files:
        parts = f.replace("\\", "/").split("/")
        if len(parts) >= 2:
            directories.add(parts[0])
        else:
            directories.add("root")

    # If files span more than 4 top-level directories, it's scattered
    if len(directories) > 4:
        return 0, f"Scattered across {len(directories)} directories: {', '.join(sorted(directories)[:5])}"
    return 1, f"Coherent ({len(directories)} {'directory' if len(directories) == 1 else 'directories'})"


def signal_security_patterns(files, diff_text=None):
    """Check for common security anti-patterns in file names or diff text."""
    risky_file_patterns = [
        r'\.env$', r'\.pem$', r'\.key$', r'secrets?\.', r'credentials?\.',
        r'password', r'token.*\.json$'
    ]

    for f in files:
        for pattern in risky_file_patterns:
            if re.search(pattern, f, re.IGNORECASE):
                return 0, f"Sensitive file pattern: {f}"

    if diff_text:
        secret_patterns = [
            r'(?:password|secret|token|api[_-]?key)\s*[=:]\s*["\'][^"\']{8,}',
            r'ghp_[A-Za-z0-9_]{36}',
            r'sk-[A-Za-z0-9]{48}',
        ]
        for pattern in secret_patterns:
            if re.search(pattern, diff_text, re.IGNORECASE):
                return 0, "Possible credential in diff"

    return 1, "No security concerns"


def signal_doc_coverage(files):
    """Check if documentation is updated when public API files change."""
    api_patterns = [r'Program\.cs$', r'Controllers/', r'Endpoints/', r'\.csproj$']
    doc_patterns = [r'\.md$', r'Docs/', r'README', r'CHANGELOG']

    has_api_change = any(
        any(re.search(p, f, re.IGNORECASE) for p in api_patterns)
        for f in files
    )
    has_doc_change = any(
        any(re.search(p, f, re.IGNORECASE) for p in doc_patterns)
        for f in files
    )

    if has_api_change and has_doc_change:
        return 1, "API change with doc update"
    elif has_api_change and not has_doc_change:
        return 0, "API change without doc update"
    return 1, "No API change (docs optional)"


def signal_historical_trend(pr_data, full_memory):
    """ML-enhanced: score based on historical success rate of similar PRs.

    Uses scikit-learn when available to build a lightweight classifier on
    past merge/reject decisions. Falls back to simple ratio otherwise.
    """
    if not full_memory:
        return 1, "No history available"

    # Categorize the current PR
    files = pr_data.get("files", [])
    title = pr_data.get("title", "").lower()

    # Determine primary area from file paths
    area = _classify_area(files)

    # Count historical outcomes for this area
    area_merges = 0
    area_rejects = 0
    area_total = 0
    for entry in full_memory:
        entry_files = entry.get("files") or []
        entry_area = _classify_area(entry_files)
        if entry_area == area:
            area_total += 1
            if entry.get("decision") == "auto-merged":
                area_merges += 1
            elif entry.get("decision") in ("rejected", "closed"):
                area_rejects += 1

    if area_total < 3:
        return 1, f"Area '{area}' — insufficient history ({area_total} records)"

    # Try ML-based scoring with scikit-learn
    if _try_import_sklearn() and len(full_memory) >= 10:
        try:
            ml_score, ml_reason = _ml_historical_score(pr_data, full_memory, area)
            return ml_score, ml_reason
        except Exception:
            pass

    # Fallback: simple success ratio
    success_rate = area_merges / area_total if area_total > 0 else 0.5
    if success_rate >= 0.7:
        return 2, f"Area '{area}' — {success_rate:.0%} merge rate ({area_total} records)"
    elif success_rate >= 0.4:
        return 1, f"Area '{area}' — {success_rate:.0%} merge rate ({area_total} records)"
    return 0, f"Area '{area}' — {success_rate:.0%} merge rate ({area_total} records)"


def _classify_area(files) -> str:
    """Classify a set of files into a broad area label."""
    if not files:
        return "unknown"
    area_keywords = {
        "tests": [r'test', r'spec', r'Tests\.cs'],
        "docs": [r'\.md$', r'Docs/', r'README'],
        "ml": [r'ML', r'ml_', r'scoring', r'analytics'],
        "broker": [r'Broker/', r'Program\.cs'],
        "scripts": [r'scripts/', r'\.ps1$', r'\.py$'],
        "models": [r'Models/', r'\.cs$'],
        "ui": [r'Views/', r'ViewModels/', r'\.xaml'],
        "ci": [r'\.yml$', r'\.yaml$', r'github/workflows'],
    }
    area_counts: dict[str, int] = {}
    for f in files:
        for area, patterns in area_keywords.items():
            if any(re.search(p, f, re.IGNORECASE) for p in patterns):
                area_counts[area] = area_counts.get(area, 0) + 1
    if not area_counts:
        return "other"
    return max(area_counts, key=area_counts.get)


def _resolve_state_root() -> str:
    """Resolve the State root path: FOUNDRY_STATE_ROOT env var first, then platform default."""
    env_val = os.environ.get("FOUNDRY_STATE_ROOT", "")
    if env_val:
        return env_val
    if sys.platform == "win32":
        return r"C:\FoundryState"
    return os.path.join(os.path.expanduser("~"), "foundry-state")


def _ml_historical_score(pr_data, full_memory, area) -> tuple[int, str]:
    """Use scikit-learn to predict merge likelihood from historical features.

    Attempts to load a persisted model from State/ml-artifacts/pr-scorer-model.joblib
    first. Falls back to training an ephemeral model if loading fails or the file
    does not exist.
    """
    import numpy as np
    from sklearn.ensemble import GradientBoostingClassifier

    clf = None
    model_source = "ephemeral"

    # Try to load persisted model
    try:
        import joblib
        model_path = os.path.join(_resolve_state_root(), "ml-artifacts", "pr-scorer-model.joblib")
        if os.path.exists(model_path):
            clf = joblib.load(model_path)
            model_source = "persisted"
    except (ImportError, OSError, ValueError, TypeError):
        clf = None
        model_source = "ephemeral"

    if clf is None:
        # Train an ephemeral model on the spot
        X = []
        y = []
        for entry in full_memory:
            decision = entry.get("decision", "")
            if decision not in ("auto-merged", "rejected", "closed"):
                continue
            entry_files = entry.get("files") or []
            features = _extract_features(entry, entry_files)
            X.append(features)
            y.append(1 if decision == "auto-merged" else 0)

        if len(X) < 10 or len(set(y)) < 2:
            raise ValueError("Insufficient training data")

        X_arr = np.array(X, dtype=np.float64)
        y_arr = np.array(y, dtype=np.int64)

        clf = GradientBoostingClassifier(
            n_estimators=50, max_depth=3, random_state=42, min_samples_split=3
        )
        clf.fit(X_arr, y_arr)
        n_samples = len(X)
    else:
        # Count samples for reporting (from memory, same logic as training)
        n_samples = sum(
            1 for e in full_memory
            if e.get("decision", "") in ("auto-merged", "rejected", "closed")
        )

    # Predict for current PR
    pr_features = _extract_features(pr_data, pr_data.get("files", []))
    prob = clf.predict_proba(np.array([pr_features], dtype=np.float64))[0]
    merge_prob = prob[1] if len(prob) > 1 else prob[0]

    reason_suffix = f"area: {area}, n={n_samples}, model_source: {model_source}"
    if merge_prob >= 0.7:
        return 2, f"ML: {merge_prob:.0%} merge confidence ({reason_suffix})"
    elif merge_prob >= 0.4:
        return 1, f"ML: {merge_prob:.0%} merge confidence ({reason_suffix})"
    return 0, f"ML: {merge_prob:.0%} merge confidence ({reason_suffix})"


def _extract_features(entry, files) -> list[float]:
    """Extract numeric features from a PR entry for ML classification."""
    additions = entry.get("additions", 0)
    deletions = entry.get("deletions", 0)
    total_size = additions + deletions
    num_files = len(files) if files else 0
    has_tests = 1.0 if any(re.search(r'test|spec|Tests\.cs', f, re.IGNORECASE) for f in files) else 0.0
    has_docs = 1.0 if any(re.search(r'\.md$|Docs/|README', f, re.IGNORECASE) for f in files) else 0.0
    test_ratio = sum(1 for f in files if re.search(r'test|spec', f, re.IGNORECASE)) / max(num_files, 1)

    # Directory spread
    dirs = set()
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


# ========== CONFIDENCE CALCULATION ==========

def _compute_confidence(result: dict[str, Any]) -> float:
    """Compute a 0-1 confidence score for the pre-score.

    Higher confidence when:
    - More signals are at their maximum value
    - History is available
    - CI status is known (not 'none')
    """
    signals = result.get("signals", {})
    if not signals:
        return 0.3

    max_possible = sum(s.get("max", 1) for s in signals.values())
    actual = sum(s.get("score", 0) for s in signals.values())

    # Base confidence from signal coverage (signals at max = high confidence)
    base = actual / max_possible if max_possible > 0 else 0.5

    # Boost if CI status is known
    ci_gate = result.get("gates", {}).get("builds", {})
    if ci_gate.get("reason", "").startswith("CI status: success"):
        base = min(1.0, base + 0.1)
    elif ci_gate.get("reason", "").startswith("CI status: none"):
        base = max(0.0, base - 0.1)

    return round(base, 3)


# ========== MAIN ==========

def preprocess(pr_data):
    title = pr_data.get("title", "")
    files = pr_data.get("files", [])
    additions = pr_data.get("additions", 0)
    deletions = pr_data.get("deletions", 0)
    ci_status = pr_data.get("ci_status", "none")
    commit_messages = pr_data.get("commit_messages", [])
    repo_path = pr_data.get("repo_path", None)
    diff_text = pr_data.get("diff_text", None)
    pr_number = pr_data.get("pr_number", 0)
    open_pr_files = pr_data.get("open_pr_files", None)

    recent_merges = load_memory()
    full_memory = load_full_memory()

    result = {
        "version": 2,
        "gates": {},
        "signals": {},
        "pre_score": 0,
        "normalized_score": 0,
        "confidence": 0.0,
        "gate_passed": True,
        "gate_failure_reason": None,
        "signal_summary": "",
        "ml_engine": "sklearn" if _try_import_sklearn() else "heuristic",
    }

    # Gates
    build_ok, build_reason = gate_builds(ci_status)
    result["gates"]["builds"] = {"passed": build_ok, "reason": build_reason}

    dup_ok, dup_reason = gate_duplicate(title, files, recent_merges)
    result["gates"]["not_duplicate"] = {"passed": dup_ok, "reason": dup_reason}

    conflict_ok, conflict_reason = gate_conflict(pr_number, files, open_pr_files)
    result["gates"]["no_conflict"] = {"passed": conflict_ok, "reason": conflict_reason}

    if not build_ok:
        result["gate_passed"] = False
        result["gate_failure_reason"] = build_reason
        return result

    if not dup_ok:
        result["gate_passed"] = False
        result["gate_failure_reason"] = dup_reason
        return result

    # Conflict is a soft gate — warn but don't block
    if not conflict_ok:
        result["gates"]["no_conflict"]["warning"] = True

    # Signals
    test_score, test_reason = signal_has_tests(files)
    result["signals"]["tests"] = {"score": test_score, "max": 2, "reason": test_reason}

    size_score, size_reason = signal_pr_size(additions, deletions)
    result["signals"]["size"] = {"score": size_score, "max": 1, "reason": size_reason}

    commit_score, commit_reason = signal_commit_format(commit_messages)
    result["signals"]["commits"] = {"score": commit_score, "max": 1, "reason": commit_reason}

    churn_score, churn_reason = signal_churn_risk(files, repo_path)
    result["signals"]["churn"] = {"score": churn_score, "max": 1, "reason": churn_reason}

    coherence_score, coherence_reason = signal_file_coherence(files)
    result["signals"]["coherence"] = {"score": coherence_score, "max": 1, "reason": coherence_reason}

    security_score, security_reason = signal_security_patterns(files, diff_text)
    result["signals"]["security"] = {"score": security_score, "max": 1, "reason": security_reason}

    doc_score, doc_reason = signal_doc_coverage(files)
    result["signals"]["doc_coverage"] = {"score": doc_score, "max": 1, "reason": doc_reason}

    history_score, history_reason = signal_historical_trend(pr_data, full_memory)
    result["signals"]["historical_trend"] = {"score": history_score, "max": 2, "reason": history_reason}

    # Pre-score: 4 base (gates passed) + signals (0-10)
    signal_total = (
        test_score + size_score + commit_score + churn_score
        + coherence_score + security_score + doc_score + history_score
    )
    result["pre_score"] = 4 + signal_total

    # Normalize to 1-10 scale: raw range is 4-14, map to 1-10
    raw = result["pre_score"]
    result["normalized_score"] = max(1, min(10, round((raw - 4) / 10 * 9 + 1)))

    # Confidence
    result["confidence"] = _compute_confidence(result)

    # Build human-readable summary for LLM
    lines = []
    for name, sig in result["signals"].items():
        lines.append(f"  {name}: {sig['score']}/{sig['max']} — {sig['reason']}")
    result["signal_summary"] = "\n".join(lines)

    return result


if __name__ == "__main__":
    if len(sys.argv) > 1:
        with open(sys.argv[1], "r") as f:
            pr_data = json.load(f)
    else:
        pr_data = json.load(sys.stdin)

    result = preprocess(pr_data)
    print(json.dumps(result, indent=2))
