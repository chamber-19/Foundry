"""
Unit tests for the preprocessor module (scripts/scoring/preprocessor.py).

Covers:
  - Gates: builds (mergeable_state mapping), duplicate detection, conflict detection
  - Signals: tests, size, commit format, churn, coherence, security, doc coverage, historical trend
  - Normalization formula and score ranges
  - Confidence calculation
  - Full preprocess() integration with various PR shapes
  - Edge cases: empty files, missing keys, None values
"""

import sys
import unittest
from pathlib import Path
from unittest.mock import patch

# ---------------------------------------------------------------------------
# Module import — same pattern as test_validate_schema.py
# ---------------------------------------------------------------------------
_SCRIPTS_DIR = Path(__file__).parent
sys.path.insert(0, str(_SCRIPTS_DIR))

import preprocessor as _mod  # noqa: E402


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def _pr(**overrides) -> dict:
    """Return a minimal PR data dict for preprocessor input."""
    data = {
        "title": "Add unit tests",
        "files": ["src/main.py", "tests/test_main.py"],
        "additions": 50,
        "deletions": 10,
        "ci_status": "clean",
        "commit_messages": ["test: add tests"],
        "pr_number": 42,
    }
    data.update(overrides)
    return data


# ===================================================================
# GATE: builds
# ===================================================================
class TestGateBuilds(unittest.TestCase):
    """gate_builds must correctly map GitHub mergeable_state values."""

    def test_clean_passes(self):
        ok, reason = _mod.gate_builds("clean")
        self.assertTrue(ok)
        self.assertIn("clean", reason)

    def test_success_passes(self):
        ok, _ = _mod.gate_builds("success")
        self.assertTrue(ok)

    def test_none_passes(self):
        ok, reason = _mod.gate_builds("none")
        self.assertTrue(ok)
        self.assertIn("none", reason)

    def test_unknown_passes(self):
        ok, _ = _mod.gate_builds("unknown")
        self.assertTrue(ok)

    def test_behind_passes(self):
        ok, _ = _mod.gate_builds("behind")
        self.assertTrue(ok)

    def test_failure_fails(self):
        ok, reason = _mod.gate_builds("failure")
        self.assertFalse(ok)
        self.assertIn("failed", reason.lower())

    def test_unstable_fails(self):
        ok, reason = _mod.gate_builds("unstable")
        self.assertFalse(ok)
        self.assertIn("unstable", reason)

    def test_dirty_fails(self):
        ok, reason = _mod.gate_builds("dirty")
        self.assertFalse(ok)
        self.assertIn("dirty", reason)

    def test_blocked_fails(self):
        ok, reason = _mod.gate_builds("blocked")
        self.assertFalse(ok)
        self.assertIn("blocked", reason)

    def test_case_insensitive(self):
        ok, _ = _mod.gate_builds("CLEAN")
        self.assertTrue(ok)
        ok2, _ = _mod.gate_builds("UNSTABLE")
        self.assertFalse(ok2)

    def test_none_value_passes(self):
        ok, _ = _mod.gate_builds(None)
        self.assertTrue(ok)

    def test_empty_string_passes(self):
        ok, _ = _mod.gate_builds("")
        self.assertTrue(ok)


# ===================================================================
# GATE: duplicate
# ===================================================================
class TestGateDuplicate(unittest.TestCase):
    """gate_duplicate detects file overlap and title similarity."""

    def test_no_recent_merges_passes(self):
        ok, _ = _mod.gate_duplicate("title", ["a.py"], [])
        self.assertTrue(ok)

    def test_high_file_overlap_fails(self):
        recent = [{"pr_number": 10, "title": "old", "files": ["a.py", "b.py"]}]
        ok, reason = _mod.gate_duplicate("new title", ["a.py", "b.py"], recent)
        self.assertFalse(ok)
        self.assertIn("overlap", reason.lower())

    def test_low_file_overlap_passes(self):
        recent = [{"pr_number": 10, "title": "old", "files": ["a.py", "b.py", "c.py", "d.py"]}]
        ok, _ = _mod.gate_duplicate("new title", ["a.py", "e.py", "f.py", "g.py"], recent)
        self.assertTrue(ok)

    def test_high_title_similarity_fails(self):
        recent = [{"pr_number": 10, "title": "Add unit tests for scoring", "files": []}]
        ok, reason = _mod.gate_duplicate("Add unit tests for scoring module", [], recent)
        self.assertFalse(ok)
        self.assertIn("similar", reason.lower())

    def test_different_title_passes(self):
        recent = [{"pr_number": 10, "title": "Refactor database", "files": []}]
        ok, _ = _mod.gate_duplicate("Add authentication", [], recent)
        self.assertTrue(ok)

    def test_empty_files_no_crash(self):
        recent = [{"pr_number": 10, "title": "old", "files": None}]
        ok, _ = _mod.gate_duplicate("new", [], recent)
        self.assertTrue(ok)


# ===================================================================
# GATE: conflict
# ===================================================================
class TestGateConflict(unittest.TestCase):
    """gate_conflict detects overlapping files with other open PRs."""

    def test_no_open_prs_passes(self):
        ok, _ = _mod.gate_conflict(1, ["a.py"], None)
        self.assertTrue(ok)

    def test_no_overlap_passes(self):
        ok, _ = _mod.gate_conflict(1, ["a.py"], {2: ["b.py"]})
        self.assertTrue(ok)

    def test_high_overlap_fails(self):
        files = ["a.py", "b.py", "c.py"]
        open_prs = {2: ["a.py", "b.py", "c.py", "d.py"]}
        ok, reason = _mod.gate_conflict(1, files, open_prs)
        self.assertFalse(ok)
        self.assertIn("#2", reason)

    def test_skips_self(self):
        files = ["a.py", "b.py", "c.py"]
        open_prs = {1: ["a.py", "b.py", "c.py"]}
        ok, _ = _mod.gate_conflict(1, files, open_prs)
        self.assertTrue(ok)

    def test_empty_files_passes(self):
        ok, _ = _mod.gate_conflict(1, [], {2: ["a.py"]})
        self.assertTrue(ok)


# ===================================================================
# SIGNAL: tests
# ===================================================================
class TestSignalHasTests(unittest.TestCase):

    def test_both_test_and_prod_scores_2(self):
        score, _ = _mod.signal_has_tests(["src/main.py", "tests/test_main.py"])
        self.assertEqual(score, 2)

    def test_test_only_scores_1(self):
        score, _ = _mod.signal_has_tests(["tests/test_main.py"])
        self.assertEqual(score, 1)

    def test_no_tests_scores_0(self):
        score, _ = _mod.signal_has_tests(["src/main.py", "src/utils.py"])
        self.assertEqual(score, 0)

    def test_empty_files_scores_0(self):
        score, _ = _mod.signal_has_tests([])
        self.assertEqual(score, 0)

    def test_csharp_test_files_detected(self):
        score, _ = _mod.signal_has_tests(["src/Service.cs", "tests/ServiceTests.cs"])
        self.assertEqual(score, 2)

    def test_spec_files_detected(self):
        score, _ = _mod.signal_has_tests(["src/app.js", "src/app.spec.js"])
        self.assertEqual(score, 2)


# ===================================================================
# SIGNAL: size
# ===================================================================
class TestSignalPrSize(unittest.TestCase):

    def test_small_pr_scores_1(self):
        score, _ = _mod.signal_pr_size(50, 10)
        self.assertEqual(score, 1)

    def test_large_pr_scores_0(self):
        score, _ = _mod.signal_pr_size(400, 200)
        self.assertEqual(score, 0)

    def test_boundary_500_scores_1(self):
        score, _ = _mod.signal_pr_size(300, 200)
        self.assertEqual(score, 1)

    def test_boundary_501_scores_0(self):
        score, _ = _mod.signal_pr_size(301, 200)
        self.assertEqual(score, 0)

    def test_zero_lines_scores_1(self):
        score, _ = _mod.signal_pr_size(0, 0)
        self.assertEqual(score, 1)


# ===================================================================
# SIGNAL: commit format
# ===================================================================
class TestSignalCommitFormat(unittest.TestCase):

    def test_all_conventional_scores_1(self):
        score, _ = _mod.signal_commit_format(["feat: add login", "fix: typo"])
        self.assertEqual(score, 1)

    def test_none_conventional_scores_0(self):
        score, _ = _mod.signal_commit_format(["update stuff", "more changes"])
        self.assertEqual(score, 0)

    def test_mixed_above_50pct_scores_1(self):
        score, _ = _mod.signal_commit_format(["feat: add login", "update stuff", "fix: typo"])
        self.assertEqual(score, 1)

    def test_empty_commits_scores_0(self):
        score, _ = _mod.signal_commit_format([])
        self.assertEqual(score, 0)

    def test_case_insensitive(self):
        score, _ = _mod.signal_commit_format(["FEAT: add login"])
        self.assertEqual(score, 1)


# ===================================================================
# SIGNAL: file coherence
# ===================================================================
class TestSignalFileCoherence(unittest.TestCase):

    def test_single_file_coherent(self):
        score, _ = _mod.signal_file_coherence(["src/main.py"])
        self.assertEqual(score, 1)

    def test_empty_files_coherent(self):
        score, _ = _mod.signal_file_coherence([])
        self.assertEqual(score, 1)

    def test_same_directory_coherent(self):
        score, _ = _mod.signal_file_coherence(["src/a.py", "src/b.py"])
        self.assertEqual(score, 1)

    def test_scattered_files_incoherent(self):
        files = ["src/a.py", "tests/b.py", "docs/c.md", "scripts/d.ps1", "bot/e.py"]
        score, reason = _mod.signal_file_coherence(files)
        self.assertEqual(score, 0)
        self.assertIn("Scattered", reason)

    def test_four_dirs_still_coherent(self):
        files = ["src/a.py", "tests/b.py", "docs/c.md", "scripts/d.ps1"]
        score, _ = _mod.signal_file_coherence(files)
        self.assertEqual(score, 1)


# ===================================================================
# SIGNAL: security patterns
# ===================================================================
class TestSignalSecurityPatterns(unittest.TestCase):

    def test_no_issues_scores_1(self):
        score, _ = _mod.signal_security_patterns(["src/main.py"])
        self.assertEqual(score, 1)

    def test_env_file_scores_0(self):
        score, reason = _mod.signal_security_patterns([".env"])
        self.assertEqual(score, 0)
        self.assertIn(".env", reason)

    def test_pem_file_scores_0(self):
        score, _ = _mod.signal_security_patterns(["certs/server.pem"])
        self.assertEqual(score, 0)

    def test_key_file_scores_0(self):
        score, _ = _mod.signal_security_patterns(["keys/private.key"])
        self.assertEqual(score, 0)

    def test_credential_in_diff_scores_0(self):
        diff = 'api_key = "sk-abcdefghijklmnopqrstuvwxyz012345678901234567"'
        score, reason = _mod.signal_security_patterns(["src/config.py"], diff_text=diff)
        self.assertEqual(score, 0)
        self.assertIn("credential", reason.lower())

    def test_github_token_in_diff_scores_0(self):
        diff = 'token = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij"'
        score, _ = _mod.signal_security_patterns(["src/config.py"], diff_text=diff)
        self.assertEqual(score, 0)

    def test_short_password_not_flagged(self):
        diff = 'password = "short"'
        score, _ = _mod.signal_security_patterns(["src/config.py"], diff_text=diff)
        self.assertEqual(score, 1)


# ===================================================================
# SIGNAL: doc coverage
# ===================================================================
class TestSignalDocCoverage(unittest.TestCase):

    def test_no_api_change_scores_1(self):
        score, _ = _mod.signal_doc_coverage(["src/utils.py"])
        self.assertEqual(score, 1)

    def test_api_change_with_docs_scores_1(self):
        score, _ = _mod.signal_doc_coverage(["src/Foundry.Broker/Program.cs", "README.md"])
        self.assertEqual(score, 1)

    def test_api_change_without_docs_scores_0(self):
        score, reason = _mod.signal_doc_coverage(["src/Foundry.Broker/Program.cs"])
        self.assertEqual(score, 0)
        self.assertIn("without doc", reason.lower())

    def test_csproj_change_without_docs_scores_0(self):
        score, _ = _mod.signal_doc_coverage(["src/Foundry.Core/Foundry.Core.csproj"])
        self.assertEqual(score, 0)


# ===================================================================
# SIGNAL: historical trend
# ===================================================================
class TestSignalHistoricalTrend(unittest.TestCase):

    def test_no_history_scores_1(self):
        score, _ = _mod.signal_historical_trend(_pr(), [])
        self.assertEqual(score, 1)

    def test_insufficient_area_history_scores_1(self):
        memory = [
            {"files": ["src/a.py"], "decision": "auto-merged"},
            {"files": ["src/b.py"], "decision": "auto-merged"},
        ]
        score, reason = _mod.signal_historical_trend(_pr(files=["src/c.py"]), memory)
        self.assertEqual(score, 1)
        self.assertIn("insufficient", reason.lower())

    def test_high_success_rate_scores_2(self):
        memory = [
            {"files": ["tests/a.py"], "decision": "auto-merged"},
            {"files": ["tests/b.py"], "decision": "auto-merged"},
            {"files": ["tests/c.py"], "decision": "auto-merged"},
            {"files": ["tests/d.py"], "decision": "rejected"},
        ]
        pr = _pr(files=["tests/new_test.py"])
        score, _ = _mod.signal_historical_trend(pr, memory)
        self.assertEqual(score, 2)

    def test_low_success_rate_scores_0(self):
        memory = [
            {"files": ["scripts/a.py"], "decision": "rejected"},
            {"files": ["scripts/b.py"], "decision": "closed"},
            {"files": ["scripts/c.py"], "decision": "closed"},
            {"files": ["scripts/d.py"], "decision": "auto-merged"},
        ]
        pr = _pr(files=["scripts/new.py"])
        score, _ = _mod.signal_historical_trend(pr, memory)
        self.assertEqual(score, 0)


# ===================================================================
# Area classification
# ===================================================================
class TestClassifyArea(unittest.TestCase):

    def test_empty_files_unknown(self):
        self.assertEqual(_mod._classify_area([]), "unknown")

    def test_test_files(self):
        self.assertEqual(_mod._classify_area(["tests/test_main.py"]), "tests")

    def test_doc_files(self):
        self.assertEqual(_mod._classify_area(["README.md"]), "docs")

    def test_script_files(self):
        self.assertEqual(_mod._classify_area(["scripts/deploy.ps1"]), "scripts")

    def test_mixed_returns_majority(self):
        area = _mod._classify_area(["tests/a.test.js", "tests/b.test.js", "src/c.cs"])
        self.assertEqual(area, "tests")


# ===================================================================
# Feature extraction
# ===================================================================
class TestExtractFeatures(unittest.TestCase):

    def test_returns_8_features(self):
        # 8 features: total_size, num_files, has_tests, has_docs, test_ratio, dir_spread, additions, deletions
        features = _mod._extract_features({"additions": 10, "deletions": 5}, ["src/a.py"])
        self.assertEqual(len(features), 8)

    def test_all_floats(self):
        features = _mod._extract_features({"additions": 10, "deletions": 5}, ["src/a.py"])
        for f in features:
            self.assertIsInstance(f, float)

    def test_empty_files(self):
        features = _mod._extract_features({"additions": 0, "deletions": 0}, [])
        self.assertEqual(len(features), 8)
        self.assertEqual(features[0], 0.0)  # total_size
        self.assertEqual(features[1], 0.0)  # num_files

    def test_test_ratio_correct(self):
        features = _mod._extract_features(
            {"additions": 10, "deletions": 5},
            ["src/a.py", "tests/test_a.py"]
        )
        # features[4] is test_ratio: 1 test out of 2 files = 0.5
        self.assertAlmostEqual(features[4], 0.5)


# ===================================================================
# Normalization formula
# ===================================================================
class TestNormalization(unittest.TestCase):
    """Verify the normalization from raw 4-14 range to 1-10."""

    def _normalize(self, raw):
        return max(1, min(10, round((raw - 4) / 10 * 9 + 1)))

    def test_min_raw_gives_1(self):
        self.assertEqual(self._normalize(4), 1)

    def test_max_raw_gives_10(self):
        self.assertEqual(self._normalize(14), 10)

    def test_midpoint(self):
        # raw=9 → (9-4)/10*9+1 = 5.5 → round to 6
        self.assertEqual(self._normalize(9), 6)

    def test_raw_below_4_clamped_to_1(self):
        self.assertEqual(self._normalize(0), 1)

    def test_raw_above_14_clamped_to_10(self):
        self.assertEqual(self._normalize(20), 10)


# ===================================================================
# Confidence calculation
# ===================================================================
class TestConfidence(unittest.TestCase):

    def test_clean_ci_boosts_confidence(self):
        result = {
            "gates": {"builds": {"reason": "CI status: clean"}},
            "signals": {
                "tests": {"score": 2, "max": 2},
                "size": {"score": 1, "max": 1},
            },
        }
        conf = _mod._compute_confidence(result)
        self.assertGreater(conf, 0.9)

    def test_unknown_ci_penalizes_confidence(self):
        result = {
            "gates": {"builds": {"reason": "CI status: unknown"}},
            "signals": {
                "tests": {"score": 2, "max": 2},
                "size": {"score": 1, "max": 1},
            },
        }
        conf = _mod._compute_confidence(result)
        self.assertLess(conf, 1.0)

    def test_no_signals_gives_low_confidence(self):
        result = {"gates": {}, "signals": {}}
        conf = _mod._compute_confidence(result)
        self.assertEqual(conf, 0.3)

    def test_all_zero_signals(self):
        result = {
            "gates": {"builds": {"reason": "CI status: none"}},
            "signals": {
                "tests": {"score": 0, "max": 2},
                "size": {"score": 0, "max": 1},
                "commits": {"score": 0, "max": 1},
            },
        }
        conf = _mod._compute_confidence(result)
        # base = 0/4 = 0.0, penalty = -0.1 → clamped to 0.0
        self.assertEqual(conf, 0.0)


# ===================================================================
# Full preprocess() integration
# ===================================================================
class TestPreprocessIntegration(unittest.TestCase):
    """End-to-end tests of the preprocess() function."""

    def _preprocess(self, **overrides):
        with patch.object(_mod, "load_memory", return_value=[]), \
             patch.object(_mod, "load_full_memory", return_value=[]):
            return _mod.preprocess(_pr(**overrides))

    def test_returns_dict(self):
        result = self._preprocess()
        self.assertIsInstance(result, dict)

    def test_has_required_keys(self):
        result = self._preprocess()
        for key in ["version", "gates", "signals", "pre_score", "normalized_score",
                     "confidence", "gate_passed", "gate_failure_reason",
                     "signal_summary", "ml_engine"]:
            self.assertIn(key, result, f"Missing key: {key}")

    def test_version_is_2(self):
        result = self._preprocess()
        self.assertEqual(result["version"], 2)

    def test_clean_ci_passes_gates(self):
        result = self._preprocess(ci_status="clean")
        self.assertTrue(result["gate_passed"])
        self.assertIsNone(result["gate_failure_reason"])

    def test_unstable_ci_fails_gates(self):
        result = self._preprocess(ci_status="unstable")
        self.assertFalse(result["gate_passed"])
        self.assertIn("unstable", result["gate_failure_reason"])

    def test_dirty_ci_fails_gates(self):
        result = self._preprocess(ci_status="dirty")
        self.assertFalse(result["gate_passed"])

    def test_gate_failure_skips_signals(self):
        result = self._preprocess(ci_status="unstable")
        self.assertEqual(result["signals"], {})
        self.assertEqual(result["pre_score"], 0)

    def test_ideal_pr_scores_high(self):
        result = self._preprocess(
            ci_status="clean",
            files=["src/service.py", "tests/test_service.py"],
            additions=50,
            deletions=10,
            commit_messages=["feat: add service"],
        )
        self.assertTrue(result["gate_passed"])
        self.assertGreaterEqual(result["normalized_score"], 7)

    def test_poor_pr_scores_low(self):
        result = self._preprocess(
            ci_status="clean",
            files=["src/a.py", "src/b.py", "docs/c.md", "scripts/d.py",
                   "bot/e.py", "schemas/f.json"],
            additions=1000,
            deletions=500,
            commit_messages=["stuff"],
        )
        self.assertTrue(result["gate_passed"])
        self.assertLessEqual(result["normalized_score"], 6)

    def test_normalized_score_in_valid_range(self):
        result = self._preprocess()
        self.assertGreaterEqual(result["normalized_score"], 1)
        self.assertLessEqual(result["normalized_score"], 10)

    def test_confidence_in_valid_range(self):
        result = self._preprocess()
        self.assertGreaterEqual(result["confidence"], 0.0)
        self.assertLessEqual(result["confidence"], 1.0)

    def test_signal_summary_is_string(self):
        result = self._preprocess()
        self.assertIsInstance(result["signal_summary"], str)

    def test_all_signals_present(self):
        result = self._preprocess()
        expected_signals = ["tests", "size", "commits", "churn", "coherence",
                           "security", "doc_coverage", "historical_trend"]
        for sig in expected_signals:
            self.assertIn(sig, result["signals"], f"Missing signal: {sig}")

    def test_each_signal_has_score_max_reason(self):
        result = self._preprocess()
        for name, sig in result["signals"].items():
            self.assertIn("score", sig, f"Signal '{name}' missing 'score'")
            self.assertIn("max", sig, f"Signal '{name}' missing 'max'")
            self.assertIn("reason", sig, f"Signal '{name}' missing 'reason'")

    def test_ml_engine_without_sklearn(self):
        result = self._preprocess()
        self.assertIn(result["ml_engine"], ("sklearn", "heuristic"))

    def test_security_risk_detected(self):
        result = self._preprocess(files=[".env", "src/main.py"])
        self.assertEqual(result["signals"]["security"]["score"], 0)

    def test_conflict_gate_is_soft(self):
        """Conflict gate should warn but not block."""
        files = ["a.py", "b.py", "c.py"]
        open_prs = {99: ["a.py", "b.py", "c.py", "d.py"]}
        result = self._preprocess(files=files, open_pr_files=open_prs)
        self.assertTrue(result["gate_passed"])
        self.assertTrue(result["gates"]["no_conflict"].get("warning", False))

    def test_empty_files_no_crash(self):
        result = self._preprocess(files=[])
        self.assertTrue(result["gate_passed"])

    def test_missing_optional_keys(self):
        """preprocess handles missing optional keys gracefully."""
        with patch.object(_mod, "load_memory", return_value=[]), \
             patch.object(_mod, "load_full_memory", return_value=[]):
            result = _mod.preprocess({
                "title": "test",
                "files": [],
                "additions": 0,
                "deletions": 0,
            })
        self.assertIsInstance(result, dict)
        self.assertTrue(result["gate_passed"])


if __name__ == "__main__":
    unittest.main()
