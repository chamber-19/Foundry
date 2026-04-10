"""
Unit tests for signal calculations and gate logic in scripts/scoring/preprocessor.py.

Covers:
  - signal_has_tests: all score branches, multiple test-file patterns
  - signal_pr_size: boundary conditions (<=500, >500)
  - signal_commit_format: empty list, all conventional, >=50%, <50%, all type prefixes
  - signal_churn_risk: no repo_path, nonexistent path, git subprocess mock
  - signal_file_coherence: empty/single, few directories, scattered (>4)
  - signal_security_patterns: clean, risky file names, credential patterns in diff
  - signal_doc_coverage: no API change, API+doc update, API without doc update
  - gate_builds: success, failure, none, other status
  - gate_duplicate: no recent merges, file overlap >=50%, title similarity >70%
  - gate_conflict: no open PRs, same-PR skip, file overlap >=3, overlap >=50%
  - _classify_area: all known area labels, other, unknown (empty)
  - _extract_features: return shape and representative values
  - signal_historical_trend: no history, insufficient area records, heuristic branches
  - preprocess (integration): CI gate failure, duplicate gate failure, full pass with scoring
"""

import sys
import unittest
from pathlib import Path
from unittest.mock import patch, MagicMock

# ---------------------------------------------------------------------------
# Module import
# ---------------------------------------------------------------------------
_SCRIPTS_DIR = Path(__file__).parent
sys.path.insert(0, str(_SCRIPTS_DIR))

import preprocessor as _module  # noqa: E402


# ---------------------------------------------------------------------------
# Helper – build a minimal valid pr_data dict
# ---------------------------------------------------------------------------
def _pr(**overrides) -> dict:
    base = {
        "pr_number": 1,
        "title": "feat: add feature",
        "files": ["src/foo.py", "tests/test_foo.py"],
        "additions": 50,
        "deletions": 10,
        "ci_status": "success",
        "commit_messages": ["feat: add feature"],
        "repo_path": None,
        "diff_text": None,
        "open_pr_files": None,
    }
    base.update(overrides)
    return base


# ===========================================================================
# Group 1: signal_has_tests
# ===========================================================================
class TestSignalHasTests(unittest.TestCase):
    """signal_has_tests returns (score, reason) with score in {0, 1, 2}."""

    def test_no_files_returns_zero(self):
        score, reason = _module.signal_has_tests([])
        self.assertEqual(score, 0)
        self.assertIn("No test files", reason)

    def test_only_prod_files_returns_zero(self):
        score, reason = _module.signal_has_tests(["src/main.py", "src/utils.py"])
        self.assertEqual(score, 0)
        self.assertIn("No test files", reason)

    def test_only_test_files_returns_one(self):
        score, reason = _module.signal_has_tests(["tests/test_foo.py"])
        self.assertEqual(score, 1)
        self.assertIn("test files only", reason)

    def test_test_and_prod_files_returns_two(self):
        score, reason = _module.signal_has_tests(["src/foo.py", "tests/test_foo.py"])
        self.assertEqual(score, 2)
        self.assertIn("test", reason)
        self.assertIn("prod", reason)

    def test_pattern_spec_filename(self):
        score, _ = _module.signal_has_tests(["src/app.py", "spec/app_spec.rb"])
        self.assertEqual(score, 2)

    def test_pattern_dottest_extension(self):
        score, _ = _module.signal_has_tests(["src/app.py", "src/app.test.ts"])
        self.assertEqual(score, 2)

    def test_pattern_dotspec_extension(self):
        score, _ = _module.signal_has_tests(["src/app.py", "src/app.spec.ts"])
        self.assertEqual(score, 2)

    def test_pattern_tests_cs(self):
        score, _ = _module.signal_has_tests(["src/Service.cs", "tests/ServiceTests.cs"])
        self.assertEqual(score, 2)

    def test_pattern_underscore_test(self):
        score, _ = _module.signal_has_tests(["src/lib.go", "pkg/lib_test.go"])
        self.assertEqual(score, 2)

    def test_case_insensitive_matching(self):
        # "TEST" in uppercase should still match
        score, _ = _module.signal_has_tests(["src/Foo.cs", "tests/TEST_Foo.cs"])
        self.assertEqual(score, 2)

    def test_multiple_test_files_counted(self):
        files = ["src/a.py", "tests/test_a.py", "tests/test_b.py"]
        score, reason = _module.signal_has_tests(files)
        self.assertEqual(score, 2)
        self.assertIn("2 test", reason)
        self.assertIn("1 prod", reason)

    def test_return_is_two_tuple(self):
        result = _module.signal_has_tests([])
        self.assertIsInstance(result, tuple)
        self.assertEqual(len(result), 2)


# ===========================================================================
# Group 2: signal_pr_size
# ===========================================================================
class TestSignalPrSize(unittest.TestCase):
    """signal_pr_size returns (1, reason) for <=500 lines and (0, reason) for >500."""

    def test_zero_lines_is_reasonable(self):
        score, reason = _module.signal_pr_size(0, 0)
        self.assertEqual(score, 1)
        self.assertIn("reasonable", reason)

    def test_small_pr_is_reasonable(self):
        score, _ = _module.signal_pr_size(10, 5)
        self.assertEqual(score, 1)

    def test_exactly_500_is_reasonable(self):
        score, reason = _module.signal_pr_size(300, 200)
        self.assertEqual(score, 1)
        self.assertIn("500", reason)

    def test_501_lines_is_large(self):
        score, reason = _module.signal_pr_size(300, 201)
        self.assertEqual(score, 0)
        self.assertIn("large PR", reason)

    def test_large_pr_is_penalised(self):
        score, reason = _module.signal_pr_size(1000, 500)
        self.assertEqual(score, 0)
        self.assertIn("1500", reason)

    def test_reason_contains_total(self):
        score, reason = _module.signal_pr_size(100, 50)
        self.assertIn("150", reason)

    def test_return_is_two_tuple(self):
        result = _module.signal_pr_size(10, 10)
        self.assertIsInstance(result, tuple)
        self.assertEqual(len(result), 2)


# ===========================================================================
# Group 3: signal_commit_format
# ===========================================================================
class TestSignalCommitFormat(unittest.TestCase):
    """signal_commit_format returns (0/1, reason) based on conventional-commit ratio."""

    def test_empty_commits_returns_zero(self):
        score, reason = _module.signal_commit_format([])
        self.assertEqual(score, 0)
        self.assertEqual(reason, "No commits")

    def test_all_conventional_returns_one(self):
        messages = ["feat: add feature", "fix: resolve bug"]
        score, reason = _module.signal_commit_format(messages)
        self.assertEqual(score, 1)
        self.assertIn("conventional", reason)

    def test_exactly_half_conventional_returns_one(self):
        messages = ["feat: add feature", "some random message"]
        score, _ = _module.signal_commit_format(messages)
        self.assertEqual(score, 1)

    def test_below_half_returns_zero(self):
        messages = ["random commit", "another random", "feat: one good"]
        score, _ = _module.signal_commit_format(messages)
        self.assertEqual(score, 0)

    def test_none_conventional_returns_zero(self):
        messages = ["fix stuff", "update things"]
        score, _ = _module.signal_commit_format(messages)
        self.assertEqual(score, 0)

    def test_prefix_fix(self):
        score, _ = _module.signal_commit_format(["fix: something"])
        self.assertEqual(score, 1)

    def test_prefix_test(self):
        score, _ = _module.signal_commit_format(["test: add tests"])
        self.assertEqual(score, 1)

    def test_prefix_chore(self):
        score, _ = _module.signal_commit_format(["chore: cleanup"])
        self.assertEqual(score, 1)

    def test_prefix_docs(self):
        score, _ = _module.signal_commit_format(["docs: update readme"])
        self.assertEqual(score, 1)

    def test_prefix_refactor(self):
        score, _ = _module.signal_commit_format(["refactor: extract method"])
        self.assertEqual(score, 1)

    def test_prefix_style(self):
        score, _ = _module.signal_commit_format(["style: fix indentation"])
        self.assertEqual(score, 1)

    def test_prefix_ci(self):
        score, _ = _module.signal_commit_format(["ci: update workflow"])
        self.assertEqual(score, 1)

    def test_prefix_perf(self):
        score, _ = _module.signal_commit_format(["perf: speed up query"])
        self.assertEqual(score, 1)

    def test_prefix_build(self):
        score, _ = _module.signal_commit_format(["build: bump version"])
        self.assertEqual(score, 1)

    def test_prefix_with_scope(self):
        score, _ = _module.signal_commit_format(["feat(auth): add login"])
        self.assertEqual(score, 1)

    def test_case_insensitive_prefix(self):
        score, _ = _module.signal_commit_format(["FEAT: upper case"])
        self.assertEqual(score, 1)

    def test_reason_shows_ratio(self):
        messages = ["feat: good", "bad commit"]
        _, reason = _module.signal_commit_format(messages)
        self.assertIn("1/2", reason)


# ===========================================================================
# Group 4: signal_churn_risk
# ===========================================================================
class TestSignalChurnRisk(unittest.TestCase):
    """signal_churn_risk skips gracefully when no local repo is available."""

    def test_no_repo_path_skipped(self):
        score, reason = _module.signal_churn_risk(["src/foo.py"], repo_path=None)
        self.assertEqual(score, 1)
        self.assertIn("Skipped", reason)

    def test_nonexistent_repo_path_skipped(self):
        score, reason = _module.signal_churn_risk(["src/foo.py"], repo_path="/nonexistent/path")
        self.assertEqual(score, 1)
        self.assertIn("Skipped", reason)

    def test_empty_files_no_repo_skipped(self):
        score, reason = _module.signal_churn_risk([], repo_path=None)
        self.assertEqual(score, 1)

    @patch("preprocessor.subprocess.run")
    @patch("preprocessor.os.path.exists", return_value=True)
    def test_low_churn_returns_one(self, mock_exists, mock_run):
        mock_result = MagicMock()
        mock_result.stdout = "abc123\ndef456"  # 2 commits — below threshold
        mock_run.return_value = mock_result
        score, reason = _module.signal_churn_risk(["src/foo.py"], repo_path="/fake/repo")
        self.assertEqual(score, 1)
        self.assertIn("Low churn", reason)

    @patch("preprocessor.subprocess.run")
    @patch("preprocessor.os.path.exists", return_value=True)
    def test_high_churn_returns_zero(self, mock_exists, mock_run):
        # 6 commits in last 30 days → high churn (threshold is 5)
        mock_result = MagicMock()
        mock_result.stdout = "\n".join([f"commit{i}" for i in range(6)])
        mock_run.return_value = mock_result
        score, reason = _module.signal_churn_risk(["src/foo.py"], repo_path="/fake/repo")
        self.assertEqual(score, 0)
        self.assertIn("High churn", reason)

    @patch("preprocessor.subprocess.run", side_effect=Exception("git not found"))
    @patch("preprocessor.os.path.exists", return_value=True)
    def test_subprocess_exception_returns_one(self, mock_exists, mock_run):
        score, reason = _module.signal_churn_risk(["src/foo.py"], repo_path="/fake/repo")
        self.assertEqual(score, 1)
        self.assertIn("failed", reason)


# ===========================================================================
# Group 5: signal_file_coherence
# ===========================================================================
class TestSignalFileCoherence(unittest.TestCase):
    """signal_file_coherence penalises PRs scattered across >4 top-level directories."""

    def test_empty_files_is_coherent(self):
        score, reason = _module.signal_file_coherence([])
        self.assertEqual(score, 1)
        self.assertIn("coherent", reason.lower())

    def test_single_file_is_coherent(self):
        score, reason = _module.signal_file_coherence(["src/main.py"])
        self.assertEqual(score, 1)

    def test_files_in_same_directory_is_coherent(self):
        score, reason = _module.signal_file_coherence(["src/a.py", "src/b.py", "src/c.py"])
        self.assertEqual(score, 1)
        self.assertIn("1", reason)

    def test_files_in_four_directories_is_coherent(self):
        files = ["src/a.py", "tests/b.py", "docs/c.md", "scripts/d.sh"]
        score, reason = _module.signal_file_coherence(files)
        self.assertEqual(score, 1)
        self.assertIn("4", reason)

    def test_files_in_five_directories_is_scattered(self):
        files = [
            "src/a.py", "tests/b.py", "docs/c.md",
            "scripts/d.sh", "configs/e.json",
        ]
        score, reason = _module.signal_file_coherence(files)
        self.assertEqual(score, 0)
        self.assertIn("Scattered", reason)
        self.assertIn("5", reason)

    def test_root_files_counted_as_root_dir(self):
        # Files with no directory component are counted as "root"
        score, reason = _module.signal_file_coherence(["README.md", "setup.py"])
        self.assertEqual(score, 1)

    def test_return_is_two_tuple(self):
        result = _module.signal_file_coherence([])
        self.assertIsInstance(result, tuple)
        self.assertEqual(len(result), 2)


# ===========================================================================
# Group 6: signal_security_patterns
# ===========================================================================
class TestSignalSecurityPatterns(unittest.TestCase):
    """signal_security_patterns flags risky file names and credential patterns in diff."""

    def test_clean_files_no_diff_is_safe(self):
        score, reason = _module.signal_security_patterns(["src/main.py"], diff_text=None)
        self.assertEqual(score, 1)
        self.assertIn("No security concerns", reason)

    def test_env_file_is_flagged(self):
        score, reason = _module.signal_security_patterns([".env"])
        self.assertEqual(score, 0)
        self.assertIn(".env", reason)

    def test_pem_file_is_flagged(self):
        score, _ = _module.signal_security_patterns(["certs/server.pem"])
        self.assertEqual(score, 0)

    def test_key_file_is_flagged(self):
        score, _ = _module.signal_security_patterns(["keys/private.key"])
        self.assertEqual(score, 0)

    def test_secrets_file_is_flagged(self):
        score, _ = _module.signal_security_patterns(["config/secrets.json"])
        self.assertEqual(score, 0)

    def test_credentials_file_is_flagged(self):
        score, _ = _module.signal_security_patterns(["config/credentials.yml"])
        self.assertEqual(score, 0)

    def test_password_filename_is_flagged(self):
        score, _ = _module.signal_security_patterns(["config/password_store.txt"])
        self.assertEqual(score, 0)

    def test_token_json_file_is_flagged(self):
        score, _ = _module.signal_security_patterns(["auth/token.json"])
        self.assertEqual(score, 0)

    def test_password_in_diff_is_flagged(self):
        diff = 'password = "supersecret123"'
        score, reason = _module.signal_security_patterns([], diff_text=diff)
        self.assertEqual(score, 0)
        self.assertIn("credential", reason.lower())

    def test_secret_in_diff_is_flagged(self):
        diff = 'secret: "my_long_secret_value"'
        score, reason = _module.signal_security_patterns([], diff_text=diff)
        self.assertEqual(score, 0)

    def test_token_in_diff_is_flagged(self):
        diff = 'token = "abcdefghijklmnop"'
        score, reason = _module.signal_security_patterns([], diff_text=diff)
        self.assertEqual(score, 0)

    def test_github_token_in_diff_is_flagged(self):
        diff = "ghp_" + "A" * 36
        score, _ = _module.signal_security_patterns([], diff_text=diff)
        self.assertEqual(score, 0)

    def test_clean_diff_is_safe(self):
        diff = "+ def greet(name):\n+     return f'Hello {name}'"
        score, _ = _module.signal_security_patterns(["src/greet.py"], diff_text=diff)
        self.assertEqual(score, 1)

    def test_empty_files_and_no_diff_is_safe(self):
        score, _ = _module.signal_security_patterns([])
        self.assertEqual(score, 1)


# ===========================================================================
# Group 7: signal_doc_coverage
# ===========================================================================
class TestSignalDocCoverage(unittest.TestCase):
    """signal_doc_coverage checks docs are updated when public API files change."""

    def test_no_api_no_doc_change_passes(self):
        score, reason = _module.signal_doc_coverage(["src/helper.py"])
        self.assertEqual(score, 1)
        self.assertIn("docs optional", reason)

    def test_api_and_doc_change_passes(self):
        score, reason = _module.signal_doc_coverage(["src/Program.cs", "docs/README.md"])
        self.assertEqual(score, 1)
        self.assertIn("doc update", reason)

    def test_api_without_doc_change_fails(self):
        score, reason = _module.signal_doc_coverage(["src/Program.cs"])
        self.assertEqual(score, 0)
        self.assertIn("without doc update", reason)

    def test_controllers_path_triggers_api(self):
        score, _ = _module.signal_doc_coverage(["src/Controllers/UserController.cs"])
        self.assertEqual(score, 0)

    def test_endpoints_path_triggers_api(self):
        score, _ = _module.signal_doc_coverage(["src/Endpoints/UserEndpoint.cs"])
        self.assertEqual(score, 0)

    def test_csproj_triggers_api(self):
        score, _ = _module.signal_doc_coverage(["src/Foundry.Core.csproj"])
        self.assertEqual(score, 0)

    def test_md_file_counts_as_doc(self):
        score, _ = _module.signal_doc_coverage(["src/Program.cs", "CHANGELOG.md"])
        self.assertEqual(score, 1)

    def test_docs_directory_counts_as_doc(self):
        score, _ = _module.signal_doc_coverage(["src/Program.cs", "Docs/api-guide.md"])
        self.assertEqual(score, 1)

    def test_readme_counts_as_doc(self):
        score, _ = _module.signal_doc_coverage(["src/Program.cs", "README.md"])
        self.assertEqual(score, 1)

    def test_return_is_two_tuple(self):
        result = _module.signal_doc_coverage([])
        self.assertIsInstance(result, tuple)
        self.assertEqual(len(result), 2)


# ===========================================================================
# Group 8: gate_builds
# ===========================================================================
class TestGateBuilds(unittest.TestCase):
    """gate_builds passes on any status except 'failure'."""

    def test_failure_status_blocks(self):
        passed, reason = _module.gate_builds("failure")
        self.assertFalse(passed)
        self.assertIn("failed", reason)

    def test_success_status_passes(self):
        passed, reason = _module.gate_builds("success")
        self.assertTrue(passed)
        self.assertIn("success", reason)

    def test_none_status_passes(self):
        passed, _ = _module.gate_builds("none")
        self.assertTrue(passed)

    def test_pending_status_passes(self):
        passed, _ = _module.gate_builds("pending")
        self.assertTrue(passed)

    def test_unknown_status_passes(self):
        passed, _ = _module.gate_builds("unknown")
        self.assertTrue(passed)

    def test_return_is_two_tuple(self):
        result = _module.gate_builds("success")
        self.assertIsInstance(result, tuple)
        self.assertEqual(len(result), 2)


# ===========================================================================
# Group 9: gate_duplicate
# ===========================================================================
class TestGateDuplicate(unittest.TestCase):
    """gate_duplicate blocks on >=50% file overlap or >70% title similarity."""

    def test_no_recent_merges_passes(self):
        passed, reason = _module.gate_duplicate("feat: new thing", ["src/foo.py"], [])
        self.assertTrue(passed)
        self.assertIn("No duplicates", reason)

    def test_below_50_percent_file_overlap_passes(self):
        recent = [{"pr_number": 10, "title": "old PR", "files": ["src/other.py"]}]
        passed, _ = _module.gate_duplicate("feat: new", ["src/foo.py", "src/bar.py"], recent)
        self.assertTrue(passed)

    def test_exactly_50_percent_file_overlap_blocks(self):
        recent = [{"pr_number": 10, "title": "old PR", "files": ["src/foo.py"]}]
        passed, reason = _module.gate_duplicate("feat: new", ["src/foo.py", "src/bar.py"], recent)
        self.assertFalse(passed)
        self.assertIn("#10", reason)

    def test_100_percent_file_overlap_blocks(self):
        files = ["src/foo.py", "src/bar.py"]
        recent = [{"pr_number": 10, "title": "old PR", "files": files}]
        passed, reason = _module.gate_duplicate("feat: new", files, recent)
        self.assertFalse(passed)

    def test_title_similarity_above_threshold_blocks(self):
        recent = [{"pr_number": 5, "title": "feat: add feature", "files": []}]
        # Identical title → similarity = 1.0 > 0.7
        passed, reason = _module.gate_duplicate("feat: add feature", ["src/new.py"], recent)
        self.assertFalse(passed)
        self.assertIn("#5", reason)

    def test_different_title_and_files_passes(self):
        recent = [{"pr_number": 5, "title": "fix: old bug fix", "files": ["src/old.py"]}]
        passed, _ = _module.gate_duplicate("feat: completely new feature", ["src/new.py"], recent)
        self.assertTrue(passed)

    def test_empty_pr_files_skips_file_overlap_check(self):
        recent = [{"pr_number": 5, "title": "old", "files": ["src/foo.py"]}]
        # Empty pr_files — file overlap check is skipped, different title passes
        passed, _ = _module.gate_duplicate("completely different title xyz", [], recent)
        self.assertTrue(passed)


# ===========================================================================
# Group 10: gate_conflict
# ===========================================================================
class TestGateConflict(unittest.TestCase):
    """gate_conflict flags high file overlap with other open PRs."""

    def test_no_open_pr_files_passes(self):
        passed, reason = _module.gate_conflict(1, ["src/foo.py"], None)
        self.assertTrue(passed)
        self.assertIn("No conflict risk", reason)

    def test_empty_open_pr_files_passes(self):
        passed, _ = _module.gate_conflict(1, ["src/foo.py"], {})
        self.assertTrue(passed)

    def test_same_pr_number_is_skipped(self):
        open_prs = {"1": ["src/foo.py", "src/bar.py", "src/baz.py"]}
        passed, _ = _module.gate_conflict(1, ["src/foo.py", "src/bar.py", "src/baz.py"], open_prs)
        self.assertTrue(passed)

    def test_overlap_of_three_files_flags_conflict(self):
        open_prs = {"2": ["src/a.py", "src/b.py", "src/c.py"]}
        passed, reason = _module.gate_conflict(1, ["src/a.py", "src/b.py", "src/c.py"], open_prs)
        self.assertFalse(passed)
        self.assertIn("#2", reason)

    def test_overlap_below_threshold_passes(self):
        open_prs = {"2": ["src/other.py"]}
        passed, _ = _module.gate_conflict(1, ["src/foo.py", "src/bar.py", "src/baz.py"], open_prs)
        self.assertTrue(passed)

    def test_fifty_percent_file_overlap_flags_conflict(self):
        # 2 shared out of 2 pr_files = 100% ≥ 50%
        open_prs = {"2": ["src/a.py", "src/b.py"]}
        passed, reason = _module.gate_conflict(1, ["src/a.py", "src/b.py"], open_prs)
        self.assertFalse(passed)

    def test_return_is_two_tuple(self):
        result = _module.gate_conflict(1, [], None)
        self.assertIsInstance(result, tuple)
        self.assertEqual(len(result), 2)


# ===========================================================================
# Group 11: _classify_area
# ===========================================================================
class TestClassifyArea(unittest.TestCase):
    """_classify_area maps file paths to a broad area label."""

    def test_empty_files_returns_unknown(self):
        self.assertEqual(_module._classify_area([]), "unknown")

    def test_test_files_classified_as_tests(self):
        self.assertEqual(_module._classify_area(["tests/test_foo.py"]), "tests")

    def test_spec_files_classified_as_tests(self):
        self.assertEqual(_module._classify_area(["spec/foo_spec.rb"]), "tests")

    def test_markdown_files_classified_as_docs(self):
        self.assertEqual(_module._classify_area(["docs/readme.md"]), "docs")

    def test_readme_classified_as_docs(self):
        self.assertEqual(_module._classify_area(["README.md"]), "docs")

    def test_ml_files_classified_as_ml(self):
        self.assertEqual(_module._classify_area(["scripts/ml_pipeline.py"]), "ml")

    def test_broker_files_classified_as_broker(self):
        self.assertEqual(_module._classify_area(["src/Foundry.Broker/Program.cs"]), "broker")

    def test_yaml_ci_files_classified_as_ci(self):
        # Use a path matched only by r'github/workflows' (no .yml/.yaml so the
        # case-insensitive r'ML' pattern does not also match the extension)
        self.assertEqual(_module._classify_area([".github/workflows/deploy"]), "ci")

    def test_unrecognised_files_classified_as_other(self):
        self.assertEqual(_module._classify_area(["random/unknown_file.xyz"]), "other")

    def test_mixed_files_returns_dominant_area(self):
        # Majority of files are in the scripts directory, so "scripts" should dominate.
        files = ["scripts/a.py", "scripts/b.py", "tests/test_c.py"]
        self.assertEqual(_module._classify_area(files), "scripts")


# ===========================================================================
# Group 12: _extract_features
# ===========================================================================
class TestExtractFeatures(unittest.TestCase):
    """_extract_features returns a list of 8 numeric features."""

    def test_returns_list_of_eight_floats(self):
        entry = {"additions": 10, "deletions": 5}
        features = _module._extract_features(entry, ["src/foo.py"])
        self.assertIsInstance(features, list)
        self.assertEqual(len(features), 8)
        for val in features:
            self.assertIsInstance(val, float)

    def test_total_size_is_first_feature(self):
        entry = {"additions": 30, "deletions": 20}
        features = _module._extract_features(entry, [])
        self.assertEqual(features[0], 50.0)

    def test_num_files_is_second_feature(self):
        files = ["a.py", "b.py", "c.py"]
        features = _module._extract_features({}, files)
        self.assertEqual(features[1], 3.0)

    def test_has_tests_feature_set_when_test_file_present(self):
        features = _module._extract_features({}, ["tests/test_foo.py"])
        self.assertEqual(features[2], 1.0)

    def test_has_tests_feature_zero_when_no_test_file(self):
        features = _module._extract_features({}, ["src/foo.py"])
        self.assertEqual(features[2], 0.0)

    def test_has_docs_feature_set_when_md_file_present(self):
        features = _module._extract_features({}, ["README.md"])
        self.assertEqual(features[3], 1.0)

    def test_test_ratio_correct(self):
        files = ["tests/test_a.py", "tests/test_b.py", "src/c.py"]
        features = _module._extract_features({}, files)
        # 2 test files out of 3 total
        self.assertAlmostEqual(features[4], 2 / 3, places=5)

    def test_dir_spread_correct(self):
        files = ["src/a.py", "tests/b.py", "docs/c.md"]
        features = _module._extract_features({}, files)
        self.assertEqual(features[5], 3.0)

    def test_additions_and_deletions_at_end(self):
        entry = {"additions": 7, "deletions": 3}
        features = _module._extract_features(entry, [])
        self.assertEqual(features[6], 7.0)
        self.assertEqual(features[7], 3.0)

    def test_empty_entry_and_files_gives_zeros(self):
        features = _module._extract_features({}, [])
        self.assertEqual(features, [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0])


# ===========================================================================
# Group 13: signal_historical_trend
# ===========================================================================
class TestSignalHistoricalTrend(unittest.TestCase):
    """signal_historical_trend scores based on historical merge rates for the PR area."""

    def test_empty_memory_returns_one(self):
        score, reason = _module.signal_historical_trend(_pr(), [])
        self.assertEqual(score, 1)
        self.assertIn("No history", reason)

    def test_none_memory_returns_one(self):
        # None is falsy — same early-exit branch as an empty list
        score, reason = _module.signal_historical_trend(_pr(), None)
        self.assertEqual(score, 1)

    def _make_memory(self, n_merged, n_rejected, area_files):
        entries = []
        for _ in range(n_merged):
            entries.append({"decision": "auto-merged", "files": area_files})
        for _ in range(n_rejected):
            entries.append({"decision": "rejected", "files": area_files})
        return entries

    def test_insufficient_area_history_returns_one(self):
        # Only 2 records for the area — below the 3-record threshold
        memory = self._make_memory(1, 1, ["tests/test_foo.py"])
        pr = _pr(files=["tests/test_new.py"])
        score, reason = _module.signal_historical_trend(pr, memory)
        self.assertEqual(score, 1)
        self.assertIn("insufficient history", reason)

    def test_high_merge_rate_returns_two(self):
        # 7 merges + 1 reject = 87.5% ≥ 70%
        memory = self._make_memory(7, 1, ["tests/test_foo.py"])
        pr = _pr(files=["tests/test_new.py"])
        with patch.object(_module, "_try_import_sklearn", return_value=False):
            score, reason = _module.signal_historical_trend(pr, memory)
        self.assertEqual(score, 2)
        self.assertIn("merge rate", reason)

    def test_medium_merge_rate_returns_one(self):
        # 5 merges + 5 rejects = 50% — in [40%, 70%) range
        memory = self._make_memory(5, 5, ["tests/test_foo.py"])
        pr = _pr(files=["tests/test_new.py"])
        with patch.object(_module, "_try_import_sklearn", return_value=False):
            score, reason = _module.signal_historical_trend(pr, memory)
        self.assertEqual(score, 1)

    def test_low_merge_rate_returns_zero(self):
        # 1 merge + 9 rejects = 10% < 40%
        memory = self._make_memory(1, 9, ["tests/test_foo.py"])
        pr = _pr(files=["tests/test_new.py"])
        with patch.object(_module, "_try_import_sklearn", return_value=False):
            score, reason = _module.signal_historical_trend(pr, memory)
        self.assertEqual(score, 0)

    def test_return_is_two_tuple(self):
        result = _module.signal_historical_trend(_pr(), [])
        self.assertIsInstance(result, tuple)
        self.assertEqual(len(result), 2)


# ===========================================================================
# Group 14: preprocess (integration)
# ===========================================================================
class TestPreprocess(unittest.TestCase):
    """End-to-end integration tests for the preprocess() function."""

    def setUp(self):
        # Patch load_memory and load_full_memory so tests don't touch the filesystem
        patcher_mem = patch.object(_module, "load_memory", return_value=[])
        patcher_full = patch.object(_module, "load_full_memory", return_value=[])
        self.mock_mem = patcher_mem.start()
        self.mock_full = patcher_full.start()
        self.addCleanup(patcher_mem.stop)
        self.addCleanup(patcher_full.stop)

    def test_result_has_required_keys(self):
        result = _module.preprocess(_pr())
        for key in ("version", "gates", "signals", "pre_score",
                    "normalized_score", "confidence", "gate_passed",
                    "gate_failure_reason", "signal_summary", "ml_engine"):
            self.assertIn(key, result, f"Missing key: {key}")

    def test_version_is_2(self):
        result = _module.preprocess(_pr())
        self.assertEqual(result["version"], 2)

    def test_ci_failure_blocks_gate(self):
        result = _module.preprocess(_pr(ci_status="failure"))
        self.assertFalse(result["gate_passed"])
        self.assertIn("CI", result["gate_failure_reason"])
        # Signals should not be populated when gate fails
        self.assertEqual(result["signals"], {})

    def test_duplicate_gate_blocks_when_matched(self):
        recent = [{"pr_number": 99, "title": "feat: add feature", "files": []}]
        with patch.object(_module, "load_memory", return_value=recent):
            result = _module.preprocess(_pr(title="feat: add feature"))
        self.assertFalse(result["gate_passed"])
        self.assertIn("#99", result["gate_failure_reason"])

    def test_full_pass_gate_passed_is_true(self):
        result = _module.preprocess(_pr())
        self.assertTrue(result["gate_passed"])
        self.assertIsNone(result["gate_failure_reason"])

    def test_all_signal_keys_present_on_full_pass(self):
        result = _module.preprocess(_pr())
        for key in ("tests", "size", "commits", "churn", "coherence",
                    "security", "doc_coverage", "historical_trend"):
            self.assertIn(key, result["signals"], f"Missing signal: {key}")

    def test_pre_score_is_at_least_four_on_gate_pass(self):
        # 4 is the base gate bonus; signals add 0-10
        result = _module.preprocess(_pr())
        self.assertGreaterEqual(result["pre_score"], 4)

    def test_pre_score_not_exceeds_fourteen(self):
        result = _module.preprocess(_pr())
        self.assertLessEqual(result["pre_score"], 14)

    def test_normalized_score_in_range_one_to_ten(self):
        result = _module.preprocess(_pr())
        self.assertGreaterEqual(result["normalized_score"], 1)
        self.assertLessEqual(result["normalized_score"], 10)

    def test_confidence_is_between_zero_and_one(self):
        result = _module.preprocess(_pr())
        self.assertGreaterEqual(result["confidence"], 0.0)
        self.assertLessEqual(result["confidence"], 1.0)

    def test_signal_summary_is_non_empty_string_on_pass(self):
        result = _module.preprocess(_pr())
        self.assertIsInstance(result["signal_summary"], str)
        self.assertGreater(len(result["signal_summary"]), 0)

    def test_conflict_warning_set_when_open_pr_overlaps(self):
        open_prs = {"2": ["src/a.py", "src/b.py", "src/c.py"]}
        result = _module.preprocess(_pr(
            files=["src/a.py", "src/b.py", "src/c.py"],
            open_pr_files=open_prs,
        ))
        # Conflict is a soft gate — gate_passed should still be True
        self.assertTrue(result["gate_passed"])
        self.assertTrue(result["gates"]["no_conflict"].get("warning"))

    def test_pre_score_addition_correctness(self):
        # Compute expected pre_score manually from the signal scores
        result = _module.preprocess(_pr())
        signal_total = sum(s["score"] for s in result["signals"].values())
        self.assertEqual(result["pre_score"], 4 + signal_total)

    def test_gates_dict_contains_expected_gates(self):
        result = _module.preprocess(_pr())
        for gate in ("builds", "not_duplicate", "no_conflict"):
            self.assertIn(gate, result["gates"], f"Missing gate: {gate}")


if __name__ == "__main__":
    unittest.main()
