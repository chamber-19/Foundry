"""
Unit tests for validate_schema.py — Feature vector schema validator.

Tests cover:
  - Valid complete samples
  - Missing required fields
  - Type errors for all property types (integer, string, boolean, array)
  - Integer range constraints (score 1-10, additions/deletions/file_count >= 0)
  - Number range constraints (has_tests, has_docs, test_ratio, confidence fields 0.0-1.0)
  - Enum validation for verdict
  - Nullable (oneOf) fields: accept null and typed values, reject wrong types
  - Array item type validation (files, concerns, embedding_similarity_scores)
  - Schema caching (_SCHEMA_CACHE populated after first load)
  - Manual fallback path (jsonschema unavailable) produces equivalent results
  - CLI entry point (stdin/stdout/exit code behaviour)
"""

import json
import sys
import unittest
from io import StringIO
from pathlib import Path
from subprocess import PIPE, run
from unittest.mock import patch

# ---------------------------------------------------------------------------
# Import the module under test
# ---------------------------------------------------------------------------
_SCORING_DIR = Path(__file__).parent
sys.path.insert(0, str(_SCORING_DIR))

import validate_schema as _module  # noqa: E402
from validate_schema import validate_sample  # noqa: E402

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _minimal_valid_sample() -> dict:
    """Return a minimal sample that satisfies every required field."""
    return {
        "pr_number": 42,
        "repo": "Foundry",
        "title": "Add unit tests",
        "author": "dev",
        "timestamp": "2024-01-01T00:00:00Z",
        "additions": 10,
        "deletions": 2,
        "file_count": 3,
        "files": ["src/foo.py", "src/bar.py"],
        "score": 7,
        "verdict": "APPROVE",
        "decision": "auto-merge",
        "model": "deepseek-r1:14b",
        "json_parsed": True,
    }


def _validate_fallback(sample: dict):
    """Run validate_sample with jsonschema unavailable (manual fallback path)."""
    with patch.dict(sys.modules, {"jsonschema": None}):
        # Clear cached schema so _load_schema runs fresh under the patch
        original_cache = _module._SCHEMA_CACHE
        _module._SCHEMA_CACHE = None
        try:
            return validate_sample(sample)
        finally:
            _module._SCHEMA_CACHE = original_cache


# ─────────────────────────────────────────────────────────────────────────────
# Group 1: Valid samples
# ─────────────────────────────────────────────────────────────────────────────

class TestValidSample(unittest.TestCase):
    """A fully compliant sample must pass validation."""

    def test_minimal_valid_sample_is_valid(self):
        ok, errors = validate_sample(_minimal_valid_sample())
        self.assertTrue(ok)
        self.assertEqual(errors, [])

    def test_valid_sample_with_optional_fields_is_valid(self):
        sample = _minimal_valid_sample()
        sample.update({
            "branch": "feature/tests",
            "created_at": "2024-01-01T00:00:00Z",
            "total_size": 12,
            "has_tests": 1.0,
            "has_docs": 0.0,
            "test_ratio": 0.5,
            "dir_spread": 2,
            "cluster_id": 3,
            "cluster_label": "medium-churn",
            "cluster_confidence": 0.85,
            "embedding_similarity_scores": [0.9, 0.8, 0.7],
            "plateau_detected": False,
            "forecast_confidence": 0.75,
            "concerns": ["Could use more tests"],
            "summary": "Good small PR.",
        })
        ok, errors = validate_sample(sample)
        self.assertTrue(ok)
        self.assertEqual(errors, [])

    def test_valid_sample_with_nullable_fields_as_null_is_valid(self):
        sample = _minimal_valid_sample()
        sample.update({
            "cluster_id": None,
            "cluster_label": None,
            "cluster_confidence": None,
            "embedding_similarity_scores": None,
            "plateau_detected": None,
            "forecast_confidence": None,
        })
        ok, errors = validate_sample(sample)
        self.assertTrue(ok)
        self.assertEqual(errors, [])

    def test_additional_properties_are_allowed(self):
        sample = _minimal_valid_sample()
        sample["custom_field"] = "anything"
        ok, errors = validate_sample(sample)
        self.assertTrue(ok)

    def test_all_valid_verdict_values_pass(self):
        for verdict in ("APPROVE", "REQUEST_CHANGES", "NEEDS_DISCUSSION", "UNKNOWN"):
            sample = _minimal_valid_sample()
            sample["verdict"] = verdict
            ok, errors = validate_sample(sample)
            self.assertTrue(ok, f"Expected valid for verdict={verdict!r}, got errors: {errors}")


# ─────────────────────────────────────────────────────────────────────────────
# Group 2: Missing required fields
# ─────────────────────────────────────────────────────────────────────────────

class TestRequiredFields(unittest.TestCase):
    """Omitting any required field must make validation fail."""

    REQUIRED = [
        "pr_number", "repo", "title", "author", "timestamp",
        "additions", "deletions", "file_count", "files",
        "score", "verdict", "decision", "model", "json_parsed",
    ]

    def _assert_required_error(self, field: str):
        sample = _minimal_valid_sample()
        del sample[field]
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, f"Expected failure when '{field}' is missing")
        self.assertTrue(
            any(field in e for e in errors),
            f"Expected error mentioning '{field}', got: {errors}",
        )

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


# ─────────────────────────────────────────────────────────────────────────────
# Group 3: Type errors
# ─────────────────────────────────────────────────────────────────────────────

class TestTypeErrors(unittest.TestCase):
    """Wrong types for known properties must produce validation errors."""

    def _assert_type_error(self, field: str, bad_value):
        sample = _minimal_valid_sample()
        sample[field] = bad_value
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, f"Expected failure for {field}={bad_value!r}, got no errors")
        self.assertTrue(len(errors) > 0, f"Expected at least one error for {field}={bad_value!r}")

    # Integer fields must not accept strings
    def test_pr_number_as_string_fails(self):
        self._assert_type_error("pr_number", "42")

    def test_additions_as_string_fails(self):
        self._assert_type_error("additions", "10")

    def test_deletions_as_string_fails(self):
        self._assert_type_error("deletions", "2")

    def test_file_count_as_string_fails(self):
        self._assert_type_error("file_count", "3")

    def test_score_as_string_fails(self):
        self._assert_type_error("score", "7")

    # Boolean must not accept integers (0/1)
    def test_json_parsed_as_integer_fails(self):
        self._assert_type_error("json_parsed", 1)

    # String fields must not accept integers
    def test_repo_as_integer_fails(self):
        self._assert_type_error("repo", 123)

    def test_title_as_integer_fails(self):
        self._assert_type_error("title", 123)

    def test_verdict_as_integer_fails(self):
        self._assert_type_error("verdict", 1)

    # Array field must not accept a string
    def test_files_as_string_fails(self):
        self._assert_type_error("files", "file.py")

    # Number field must not accept a string
    def test_has_tests_as_string_fails(self):
        sample = _minimal_valid_sample()
        sample["has_tests"] = "yes"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)


# ─────────────────────────────────────────────────────────────────────────────
# Group 4: Integer range constraints
# ─────────────────────────────────────────────────────────────────────────────

class TestIntegerConstraints(unittest.TestCase):
    """Integer fields with minimum/maximum constraints must enforce those bounds."""

    def test_score_below_minimum_fails(self):
        sample = _minimal_valid_sample()
        sample["score"] = 0
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_score_above_maximum_fails(self):
        sample = _minimal_valid_sample()
        sample["score"] = 11
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_score_at_minimum_passes(self):
        sample = _minimal_valid_sample()
        sample["score"] = 1
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_score_at_maximum_passes(self):
        sample = _minimal_valid_sample()
        sample["score"] = 10
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_additions_negative_fails(self):
        sample = _minimal_valid_sample()
        sample["additions"] = -1
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_deletions_negative_fails(self):
        sample = _minimal_valid_sample()
        sample["deletions"] = -1
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_file_count_negative_fails(self):
        sample = _minimal_valid_sample()
        sample["file_count"] = -1
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_additions_zero_passes(self):
        sample = _minimal_valid_sample()
        sample["additions"] = 0
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")


# ─────────────────────────────────────────────────────────────────────────────
# Group 5: Number range constraints
# ─────────────────────────────────────────────────────────────────────────────

class TestNumberConstraints(unittest.TestCase):
    """Number fields (0.0-1.0) must enforce their minimum/maximum."""

    def _assert_number_out_of_range(self, field: str, bad_value: float):
        sample = _minimal_valid_sample()
        sample[field] = bad_value
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, f"Expected failure for {field}={bad_value}")
        self.assertTrue(len(errors) > 0, f"Expected errors for {field}={bad_value}")

    def test_has_tests_above_1_fails(self):
        self._assert_number_out_of_range("has_tests", 1.5)

    def test_has_tests_below_0_fails(self):
        self._assert_number_out_of_range("has_tests", -0.1)

    def test_has_docs_above_1_fails(self):
        self._assert_number_out_of_range("has_docs", 2.0)

    def test_test_ratio_above_1_fails(self):
        self._assert_number_out_of_range("test_ratio", 1.1)

    def test_cluster_confidence_above_1_fails(self):
        sample = _minimal_valid_sample()
        sample["cluster_confidence"] = 1.5
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_forecast_confidence_above_1_fails(self):
        sample = _minimal_valid_sample()
        sample["forecast_confidence"] = 2.0
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_has_tests_at_boundary_0_passes(self):
        sample = _minimal_valid_sample()
        sample["has_tests"] = 0.0
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_has_tests_at_boundary_1_passes(self):
        sample = _minimal_valid_sample()
        sample["has_tests"] = 1.0
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")


# ─────────────────────────────────────────────────────────────────────────────
# Group 6: Enum field (verdict)
# ─────────────────────────────────────────────────────────────────────────────

class TestEnumValidation(unittest.TestCase):
    """The verdict field must be restricted to its enum values."""

    def test_invalid_verdict_fails(self):
        sample = _minimal_valid_sample()
        sample["verdict"] = "ACCEPT"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_lowercase_verdict_fails(self):
        sample = _minimal_valid_sample()
        sample["verdict"] = "approve"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_empty_verdict_fails(self):
        sample = _minimal_valid_sample()
        sample["verdict"] = ""
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)


# ─────────────────────────────────────────────────────────────────────────────
# Group 7: Nullable (oneOf) fields
# ─────────────────────────────────────────────────────────────────────────────

class TestNullableFields(unittest.TestCase):
    """Fields with oneOf[typed, null] must accept both null and the typed value."""

    def test_cluster_id_as_integer_passes(self):
        sample = _minimal_valid_sample()
        sample["cluster_id"] = 5
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_cluster_id_as_null_passes(self):
        sample = _minimal_valid_sample()
        sample["cluster_id"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_cluster_id_as_string_fails(self):
        sample = _minimal_valid_sample()
        sample["cluster_id"] = "five"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_cluster_label_as_string_passes(self):
        sample = _minimal_valid_sample()
        sample["cluster_label"] = "large-refactor"
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_cluster_label_as_null_passes(self):
        sample = _minimal_valid_sample()
        sample["cluster_label"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_plateau_detected_as_bool_passes(self):
        sample = _minimal_valid_sample()
        sample["plateau_detected"] = True
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_plateau_detected_as_null_passes(self):
        sample = _minimal_valid_sample()
        sample["plateau_detected"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_embedding_similarity_scores_as_list_of_numbers_passes(self):
        sample = _minimal_valid_sample()
        sample["embedding_similarity_scores"] = [0.9, 0.7, 0.5]
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_embedding_similarity_scores_as_null_passes(self):
        sample = _minimal_valid_sample()
        sample["embedding_similarity_scores"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")


# ─────────────────────────────────────────────────────────────────────────────
# Group 8: Array item type validation
# ─────────────────────────────────────────────────────────────────────────────

class TestArrayItems(unittest.TestCase):
    """Items inside array fields must match the declared item type."""

    def test_files_with_non_string_item_fails(self):
        sample = _minimal_valid_sample()
        sample["files"] = ["valid.py", 42]
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_files_with_all_strings_passes(self):
        sample = _minimal_valid_sample()
        sample["files"] = ["a.py", "b.py", "c.py"]
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_empty_files_array_passes(self):
        sample = _minimal_valid_sample()
        sample["files"] = []
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_concerns_with_non_string_item_fails(self):
        sample = _minimal_valid_sample()
        sample["concerns"] = ["Needs tests", 99]
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_concerns_with_all_strings_passes(self):
        sample = _minimal_valid_sample()
        sample["concerns"] = ["Too large", "Missing docs"]
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_embedding_similarity_scores_with_non_number_item_fails(self):
        sample = _minimal_valid_sample()
        sample["embedding_similarity_scores"] = [0.9, "high"]
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)


# ─────────────────────────────────────────────────────────────────────────────
# Group 9: Schema caching
# ─────────────────────────────────────────────────────────────────────────────

class TestSchemaCache(unittest.TestCase):
    """_load_schema should cache its result after the first call."""

    def setUp(self):
        # Reset cache before each test
        self._original_cache = _module._SCHEMA_CACHE
        _module._SCHEMA_CACHE = None

    def tearDown(self):
        _module._SCHEMA_CACHE = self._original_cache

    def test_cache_is_none_before_first_load(self):
        self.assertIsNone(_module._SCHEMA_CACHE)

    def test_cache_is_populated_after_load(self):
        _module._load_schema()
        self.assertIsNotNone(_module._SCHEMA_CACHE)

    def test_cache_is_dict_after_load(self):
        _module._load_schema()
        self.assertIsInstance(_module._SCHEMA_CACHE, dict)

    def test_second_load_returns_same_object(self):
        first = _module._load_schema()
        second = _module._load_schema()
        self.assertIs(first, second)

    def test_loaded_schema_has_required_key(self):
        schema = _module._load_schema()
        self.assertIn("required", schema)

    def test_loaded_schema_has_properties_key(self):
        schema = _module._load_schema()
        self.assertIn("properties", schema)


# ─────────────────────────────────────────────────────────────────────────────
# Group 10: Manual fallback path (no jsonschema)
# ─────────────────────────────────────────────────────────────────────────────

class TestManualFallback(unittest.TestCase):
    """The manual fallback (no jsonschema) must produce equivalent results."""

    def test_valid_sample_passes_in_fallback(self):
        ok, errors = _validate_fallback(_minimal_valid_sample())
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_missing_required_field_fails_in_fallback(self):
        sample = _minimal_valid_sample()
        del sample["score"]
        ok, errors = _validate_fallback(sample)
        self.assertFalse(ok)
        self.assertTrue(any("score" in e for e in errors))

    def test_wrong_type_fails_in_fallback(self):
        sample = _minimal_valid_sample()
        sample["pr_number"] = "not-an-int"
        ok, errors = _validate_fallback(sample)
        self.assertFalse(ok)
        self.assertTrue(any("pr_number" in e for e in errors))

    def test_score_out_of_range_fails_in_fallback(self):
        sample = _minimal_valid_sample()
        sample["score"] = 0
        ok, errors = _validate_fallback(sample)
        self.assertFalse(ok)
        self.assertTrue(any("score" in e for e in errors))

    def test_invalid_verdict_fails_in_fallback(self):
        sample = _minimal_valid_sample()
        sample["verdict"] = "INVALID"
        ok, errors = _validate_fallback(sample)
        self.assertFalse(ok)
        self.assertTrue(any("verdict" in e for e in errors))

    def test_nullable_field_as_null_passes_in_fallback(self):
        sample = _minimal_valid_sample()
        sample["cluster_id"] = None
        ok, errors = _validate_fallback(sample)
        self.assertTrue(ok, f"Unexpected errors: {errors}")

    def test_nullable_field_wrong_type_fails_in_fallback(self):
        sample = _minimal_valid_sample()
        sample["cluster_id"] = "not-an-int"
        ok, errors = _validate_fallback(sample)
        self.assertFalse(ok)
        self.assertTrue(any("cluster_id" in e for e in errors))

    def test_array_item_wrong_type_fails_in_fallback(self):
        sample = _minimal_valid_sample()
        sample["files"] = ["ok.py", 99]
        ok, errors = _validate_fallback(sample)
        self.assertFalse(ok)
        self.assertTrue(any("files" in e for e in errors))

    def test_negative_additions_fails_in_fallback(self):
        sample = _minimal_valid_sample()
        sample["additions"] = -5
        ok, errors = _validate_fallback(sample)
        self.assertFalse(ok)
        self.assertTrue(any("additions" in e for e in errors))

    def test_has_tests_out_of_range_fails_in_fallback(self):
        sample = _minimal_valid_sample()
        sample["has_tests"] = 2.0
        ok, errors = _validate_fallback(sample)
        self.assertFalse(ok)
        self.assertTrue(any("has_tests" in e for e in errors))

    def test_boolean_int_fails_in_fallback(self):
        sample = _minimal_valid_sample()
        sample["json_parsed"] = 1
        ok, errors = _validate_fallback(sample)
        self.assertFalse(ok)
        self.assertTrue(any("json_parsed" in e for e in errors))


# ─────────────────────────────────────────────────────────────────────────────
# Group 11: CLI entry point
# ─────────────────────────────────────────────────────────────────────────────

class TestCLI(unittest.TestCase):
    """The __main__ block must read JSON from stdin and exit with correct codes."""

    _SCRIPT = str(_SCORING_DIR / "validate_schema.py")

    def _run(self, stdin_text: str):
        return run(
            [sys.executable, self._SCRIPT],
            input=stdin_text,
            stdout=PIPE,
            stderr=PIPE,
            text=True,
        )

    def test_valid_sample_exits_zero(self):
        result = self._run(json.dumps(_minimal_valid_sample()))
        self.assertEqual(result.returncode, 0)

    def test_valid_sample_prints_ok(self):
        result = self._run(json.dumps(_minimal_valid_sample()))
        self.assertIn("OK", result.stdout)

    def test_invalid_json_exits_one(self):
        result = self._run("{not valid json}")
        self.assertEqual(result.returncode, 1)

    def test_invalid_json_writes_to_stderr(self):
        result = self._run("{not valid json}")
        self.assertIn("ERROR", result.stderr)

    def test_invalid_sample_exits_one(self):
        sample = _minimal_valid_sample()
        del sample["score"]
        result = self._run(json.dumps(sample))
        self.assertEqual(result.returncode, 1)

    def test_invalid_sample_prints_invalid(self):
        sample = _minimal_valid_sample()
        del sample["score"]
        result = self._run(json.dumps(sample))
        self.assertIn("INVALID", result.stdout)

    def test_invalid_sample_lists_errors(self):
        sample = _minimal_valid_sample()
        del sample["score"]
        result = self._run(json.dumps(sample))
        self.assertIn("score", result.stdout)


if __name__ == "__main__":
    unittest.main()
