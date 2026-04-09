"""
Integration tests for validate_schema.py — feature-v1.json schema validation.

Exercises the validate_sample() public API against a wide range of inputs to
ensure that the schema is correctly enforced in real-world scenarios:

  - Happy path (fully valid samples)
  - Missing required fields
  - Type violations for every field category
  - Out-of-range numeric constraints
  - Invalid enum value for 'verdict'
  - Nullable (oneOf) fields accept both null and a proper typed value
  - Array item-type validation for 'files', 'concerns', and
    'embedding_similarity_scores'
  - CLI entry point reads stdin and writes to stdout correctly
"""

import json
import os
import subprocess
import sys
import unittest
from pathlib import Path

# ---------------------------------------------------------------------------
# Ensure the module under test is importable regardless of working directory
# ---------------------------------------------------------------------------
_SCORING_DIR = Path(__file__).parent
sys.path.insert(0, str(_SCORING_DIR))

from validate_schema import validate_sample  # noqa: E402


# ---------------------------------------------------------------------------
# Shared helpers
# ---------------------------------------------------------------------------

def _minimal_valid_sample() -> dict:
    """Return a dict that satisfies every *required* field with valid values."""
    return {
        "pr_number": 42,
        "repo": "Foundry",
        "title": "Add feature X",
        "author": "dev",
        "timestamp": "2024-01-15T10:30:00Z",
        "additions": 10,
        "deletions": 5,
        "file_count": 3,
        "files": ["src/a.cs", "src/b.cs", "tests/c.cs"],
        "score": 7,
        "verdict": "APPROVE",
        "decision": "auto-merge",
        "model": "deepseek-r1:14b",
        "json_parsed": True,
    }


def _full_valid_sample() -> dict:
    """Return a dict with all optional fields populated with valid values."""
    sample = _minimal_valid_sample()
    sample.update(
        {
            "branch": "feature/x",
            "created_at": "2024-01-14T08:00:00Z",
            "total_size": 15,
            "has_tests": 1.0,
            "has_docs": 0.0,
            "test_ratio": 0.33,
            "dir_spread": 2,
            "cluster_id": 3,
            "cluster_label": "large-refactor",
            "cluster_confidence": 0.85,
            "embedding_similarity_scores": [0.9, 0.75, 0.6],
            "plateau_detected": False,
            "forecast_confidence": 0.92,
            "concerns": ["Missing tests"],
            "summary": "Adds feature X with minor edge cases.",
        }
    )
    return sample


# ---------------------------------------------------------------------------
# Group 1: Happy path
# ---------------------------------------------------------------------------

class TestHappyPath(unittest.TestCase):
    """Valid samples must pass validation with no errors."""

    def test_minimal_valid_sample_is_valid(self):
        ok, errors = validate_sample(_minimal_valid_sample())
        self.assertTrue(ok)
        self.assertEqual(errors, [])

    def test_full_valid_sample_is_valid(self):
        ok, errors = validate_sample(_full_valid_sample())
        self.assertTrue(ok)
        self.assertEqual(errors, [])

    def test_all_verdict_enum_values_are_valid(self):
        for verdict in ("APPROVE", "REQUEST_CHANGES", "NEEDS_DISCUSSION", "UNKNOWN"):
            sample = _minimal_valid_sample()
            sample["verdict"] = verdict
            ok, errors = validate_sample(sample)
            self.assertTrue(ok, f"verdict='{verdict}' should be valid; errors: {errors}")

    def test_score_boundary_minimum_is_valid(self):
        sample = _minimal_valid_sample()
        sample["score"] = 1
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_score_boundary_maximum_is_valid(self):
        sample = _minimal_valid_sample()
        sample["score"] = 10
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_empty_files_list_is_valid(self):
        sample = _minimal_valid_sample()
        sample["files"] = []
        sample["file_count"] = 0
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_additional_unknown_fields_are_allowed(self):
        """additionalProperties: true — unknown fields must not cause errors."""
        sample = _minimal_valid_sample()
        sample["custom_metric"] = 99
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_nullable_fields_accepting_null(self):
        sample = _full_valid_sample()
        for field in (
            "cluster_id",
            "cluster_label",
            "cluster_confidence",
            "embedding_similarity_scores",
            "plateau_detected",
            "forecast_confidence",
        ):
            sample[field] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)


# ---------------------------------------------------------------------------
# Group 2: Missing required fields
# ---------------------------------------------------------------------------

_REQUIRED_FIELDS = [
    "pr_number",
    "repo",
    "title",
    "author",
    "timestamp",
    "additions",
    "deletions",
    "file_count",
    "files",
    "score",
    "verdict",
    "decision",
    "model",
    "json_parsed",
]


class TestMissingRequiredFields(unittest.TestCase):
    """Each required field must trigger an error when absent."""

    def _assert_required_error(self, field: str) -> None:
        sample = _minimal_valid_sample()
        del sample[field]
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, f"Expected failure when '{field}' is missing")
        joined = " ".join(errors)
        self.assertIn(field, joined, f"Expected error mentioning '{field}'; got: {errors}")

    def test_missing_pr_number(self):
        self._assert_required_error("pr_number")

    def test_missing_repo(self):
        self._assert_required_error("repo")

    def test_missing_title(self):
        self._assert_required_error("title")

    def test_missing_author(self):
        self._assert_required_error("author")

    def test_missing_timestamp(self):
        self._assert_required_error("timestamp")

    def test_missing_additions(self):
        self._assert_required_error("additions")

    def test_missing_deletions(self):
        self._assert_required_error("deletions")

    def test_missing_file_count(self):
        self._assert_required_error("file_count")

    def test_missing_files(self):
        self._assert_required_error("files")

    def test_missing_score(self):
        self._assert_required_error("score")

    def test_missing_verdict(self):
        self._assert_required_error("verdict")

    def test_missing_decision(self):
        self._assert_required_error("decision")

    def test_missing_model(self):
        self._assert_required_error("model")

    def test_missing_json_parsed(self):
        self._assert_required_error("json_parsed")

    def test_all_required_fields_missing_returns_multiple_errors(self):
        ok, errors = validate_sample({})
        self.assertFalse(ok)
        self.assertGreaterEqual(len(errors), len(_REQUIRED_FIELDS))


# ---------------------------------------------------------------------------
# Group 3: Type violations
# ---------------------------------------------------------------------------

class TestTypeViolations(unittest.TestCase):
    """Fields with the wrong Python type must trigger validation errors."""

    def _assert_type_error(self, field: str, bad_value) -> None:
        sample = _minimal_valid_sample()
        sample[field] = bad_value
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, f"Expected failure for '{field}'={bad_value!r}; no errors raised")

    def test_pr_number_as_string_fails(self):
        self._assert_type_error("pr_number", "42")

    def test_pr_number_as_float_fails(self):
        self._assert_type_error("pr_number", 42.5)

    def test_pr_number_as_bool_fails(self):
        self._assert_type_error("pr_number", True)

    def test_repo_as_integer_fails(self):
        self._assert_type_error("repo", 123)

    def test_title_as_list_fails(self):
        self._assert_type_error("title", ["a", "b"])

    def test_author_as_none_fails(self):
        """'author' is required and not declared nullable."""
        self._assert_type_error("author", None)

    def test_additions_as_string_fails(self):
        self._assert_type_error("additions", "ten")

    def test_deletions_as_bool_fails(self):
        self._assert_type_error("deletions", True)

    def test_file_count_as_float_fails(self):
        self._assert_type_error("file_count", 3.5)

    def test_files_as_string_fails(self):
        self._assert_type_error("files", "src/a.cs")

    def test_score_as_string_fails(self):
        self._assert_type_error("score", "7")

    def test_score_as_float_fails(self):
        self._assert_type_error("score", 7.5)

    def test_verdict_as_integer_fails(self):
        self._assert_type_error("verdict", 1)

    def test_json_parsed_as_string_fails(self):
        self._assert_type_error("json_parsed", "true")

    def test_json_parsed_as_integer_fails(self):
        self._assert_type_error("json_parsed", 1)

    def test_has_tests_as_string_fails(self):
        self._assert_type_error("has_tests", "yes")

    def test_has_tests_as_bool_fails(self):
        self._assert_type_error("has_tests", True)

    def test_cluster_id_as_string_fails(self):
        """cluster_id is oneOf(integer, null) — a string must fail."""
        self._assert_type_error("cluster_id", "three")

    def test_cluster_confidence_as_string_fails(self):
        self._assert_type_error("cluster_confidence", "high")


# ---------------------------------------------------------------------------
# Group 4: Constraint violations (min/max)
# ---------------------------------------------------------------------------

class TestConstraintViolations(unittest.TestCase):
    """Values outside declared min/max must produce errors."""

    def _assert_constraint_error(self, field: str, bad_value) -> None:
        sample = _minimal_valid_sample()
        sample[field] = bad_value
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, f"Expected failure for '{field}'={bad_value!r}; errors: {errors}")
        self.assertTrue(len(errors) > 0, f"Expected at least one error for '{field}'={bad_value!r}")

    def test_score_below_minimum(self):
        self._assert_constraint_error("score", 0)

    def test_score_above_maximum(self):
        self._assert_constraint_error("score", 11)

    def test_additions_negative(self):
        self._assert_constraint_error("additions", -1)

    def test_deletions_negative(self):
        self._assert_constraint_error("deletions", -1)

    def test_file_count_negative(self):
        self._assert_constraint_error("file_count", -1)

    def test_has_tests_above_maximum(self):
        sample = _minimal_valid_sample()
        sample["has_tests"] = 1.5
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)

    def test_has_tests_below_minimum(self):
        sample = _minimal_valid_sample()
        sample["has_tests"] = -0.1
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)

    def test_test_ratio_above_maximum(self):
        sample = _minimal_valid_sample()
        sample["test_ratio"] = 1.1
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)

    def test_cluster_confidence_above_maximum(self):
        sample = _minimal_valid_sample()
        sample["cluster_confidence"] = 1.1
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)

    def test_cluster_confidence_below_minimum(self):
        sample = _minimal_valid_sample()
        sample["cluster_confidence"] = -0.1
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)

    def test_forecast_confidence_above_maximum(self):
        sample = _minimal_valid_sample()
        sample["forecast_confidence"] = 2.0
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)

    def test_total_size_negative(self):
        sample = _minimal_valid_sample()
        sample["total_size"] = -5
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)

    def test_dir_spread_negative(self):
        sample = _minimal_valid_sample()
        sample["dir_spread"] = -2
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)


# ---------------------------------------------------------------------------
# Group 5: Enum violations
# ---------------------------------------------------------------------------

class TestEnumViolations(unittest.TestCase):
    """Invalid enum values for 'verdict' must produce errors."""

    def _assert_enum_error(self, bad_verdict: str) -> None:
        sample = _minimal_valid_sample()
        sample["verdict"] = bad_verdict
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, f"Expected failure for verdict='{bad_verdict}'")
        self.assertTrue(len(errors) > 0, f"Expected at least one error for verdict='{bad_verdict}'")

    def test_verdict_lowercase_approve_fails(self):
        self._assert_enum_error("approve")

    def test_verdict_mixed_case_fails(self):
        self._assert_enum_error("Approve")

    def test_verdict_unknown_value_fails(self):
        self._assert_enum_error("REJECT")

    def test_verdict_empty_string_fails(self):
        self._assert_enum_error("")

    def test_verdict_numeric_string_fails(self):
        self._assert_enum_error("1")


# ---------------------------------------------------------------------------
# Group 6: Nullable (oneOf) fields
# ---------------------------------------------------------------------------

class TestNullableFields(unittest.TestCase):
    """Fields declared as oneOf([type, null]) must accept both null and typed values."""

    def test_cluster_id_accepts_integer(self):
        sample = _minimal_valid_sample()
        sample["cluster_id"] = 5
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_cluster_id_accepts_null(self):
        sample = _minimal_valid_sample()
        sample["cluster_id"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_cluster_label_accepts_string(self):
        sample = _minimal_valid_sample()
        sample["cluster_label"] = "large-refactor"
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_cluster_label_accepts_null(self):
        sample = _minimal_valid_sample()
        sample["cluster_label"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_cluster_confidence_accepts_float_in_range(self):
        sample = _minimal_valid_sample()
        sample["cluster_confidence"] = 0.5
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_cluster_confidence_accepts_null(self):
        sample = _minimal_valid_sample()
        sample["cluster_confidence"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_embedding_similarity_scores_accepts_list_of_numbers(self):
        sample = _minimal_valid_sample()
        sample["embedding_similarity_scores"] = [0.1, 0.9, 0.5]
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_embedding_similarity_scores_accepts_null(self):
        sample = _minimal_valid_sample()
        sample["embedding_similarity_scores"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_plateau_detected_accepts_true(self):
        sample = _minimal_valid_sample()
        sample["plateau_detected"] = True
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_plateau_detected_accepts_false(self):
        sample = _minimal_valid_sample()
        sample["plateau_detected"] = False
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_plateau_detected_accepts_null(self):
        sample = _minimal_valid_sample()
        sample["plateau_detected"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_forecast_confidence_accepts_float_in_range(self):
        sample = _minimal_valid_sample()
        sample["forecast_confidence"] = 0.75
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_forecast_confidence_accepts_null(self):
        sample = _minimal_valid_sample()
        sample["forecast_confidence"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)


# ---------------------------------------------------------------------------
# Group 7: Array item-type validation
# ---------------------------------------------------------------------------

class TestArrayItemTypes(unittest.TestCase):
    """Arrays with wrong-typed items must produce item-level errors."""

    def test_files_with_non_string_items_fails(self):
        sample = _minimal_valid_sample()
        sample["files"] = [1, 2, 3]
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)

    def test_files_with_mixed_items_fails(self):
        sample = _minimal_valid_sample()
        sample["files"] = ["src/a.cs", 42]
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)

    def test_concerns_with_non_string_items_fails(self):
        sample = _minimal_valid_sample()
        sample["concerns"] = [True, False]
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)

    def test_concerns_with_all_strings_is_valid(self):
        sample = _minimal_valid_sample()
        sample["concerns"] = ["Too large", "Missing docs"]
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_embedding_similarity_scores_with_non_numeric_items_fails(self):
        sample = _minimal_valid_sample()
        sample["embedding_similarity_scores"] = ["high", "low"]
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, errors)

    def test_embedding_similarity_scores_with_all_numbers_is_valid(self):
        sample = _minimal_valid_sample()
        sample["embedding_similarity_scores"] = [0.1, 0.8]
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)


# ---------------------------------------------------------------------------
# Group 8: CLI entry point
# ---------------------------------------------------------------------------

class TestCliEntryPoint(unittest.TestCase):
    """The CLI (stdin → stdout) interface must behave correctly end-to-end."""

    _SCRIPT = str(_SCORING_DIR / "validate_schema.py")

    def _run_cli(self, stdin_text: str) -> tuple[int, str, str]:
        result = subprocess.run(
            [sys.executable, self._SCRIPT],
            input=stdin_text,
            capture_output=True,
            text=True,
        )
        return result.returncode, result.stdout, result.stderr

    def test_valid_sample_exits_zero(self):
        returncode, stdout, _ = self._run_cli(json.dumps(_minimal_valid_sample()))
        self.assertEqual(returncode, 0, stdout)

    def test_valid_sample_prints_ok(self):
        _, stdout, _ = self._run_cli(json.dumps(_minimal_valid_sample()))
        self.assertIn("OK", stdout)

    def test_invalid_sample_exits_nonzero(self):
        bad = _minimal_valid_sample()
        del bad["score"]
        returncode, _, _ = self._run_cli(json.dumps(bad))
        self.assertNotEqual(returncode, 0)

    def test_invalid_sample_prints_invalid(self):
        bad = _minimal_valid_sample()
        del bad["score"]
        _, stdout, _ = self._run_cli(json.dumps(bad))
        self.assertIn("INVALID", stdout)

    def test_malformed_json_exits_nonzero(self):
        returncode, _, stderr = self._run_cli("{not valid json")
        self.assertNotEqual(returncode, 0)

    def test_malformed_json_prints_error_to_stderr(self):
        _, _, stderr = self._run_cli("{not valid json")
        self.assertIn("ERROR", stderr)

    def test_out_of_range_score_exits_nonzero(self):
        bad = _minimal_valid_sample()
        bad["score"] = 0
        returncode, _, _ = self._run_cli(json.dumps(bad))
        self.assertNotEqual(returncode, 0)

    def test_wrong_verdict_enum_exits_nonzero(self):
        bad = _minimal_valid_sample()
        bad["verdict"] = "approve"
        returncode, _, _ = self._run_cli(json.dumps(bad))
        self.assertNotEqual(returncode, 0)


if __name__ == "__main__":
    unittest.main()
