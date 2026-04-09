"""
Unit tests for validate_sample in scripts/scoring/validate_schema.py.

Covers:
  - Valid samples (minimal, with optional fields, null optionals, verdict enum, score boundaries)
  - Missing required fields
  - Type violations
  - Range constraint violations
  - Enum constraint violation for 'verdict'
  - Array item type validation
  - Return contract (always (bool, list[str]))
  - Manual fallback path (jsonschema patched out via sys.modules)
"""

import sys
import unittest
from pathlib import Path
from unittest.mock import patch

# ---------------------------------------------------------------------------
# Module import
# ---------------------------------------------------------------------------
_SCRIPTS_DIR = Path(__file__).parent
sys.path.insert(0, str(_SCRIPTS_DIR))

import validate_schema as _module  # noqa: E402


# ---------------------------------------------------------------------------
# Helper
# ---------------------------------------------------------------------------
def _valid_sample(**overrides) -> dict:
    """Return a minimal fully-valid feature vector, with optional overrides."""
    sample = {
        "pr_number": 42,
        "repo": "Foundry",
        "title": "Add unit tests",
        "author": "dev",
        "timestamp": "2026-01-01T00:00:00Z",
        "additions": 10,
        "deletions": 5,
        "file_count": 3,
        "files": ["src/foo.py", "tests/test_foo.py"],
        "score": 7,
        "verdict": "APPROVE",
        "decision": "auto-merge",
        "model": "deepseek-r1:14b",
        "json_parsed": True,
    }
    sample.update(overrides)
    return sample


# ---------------------------------------------------------------------------
# Group 1: Valid samples
# ---------------------------------------------------------------------------
class TestValidSample(unittest.TestCase):
    """Fully valid samples must return (True, [])."""

    def test_minimal_valid_sample_passes(self):
        ok, errors = _module.validate_sample(_valid_sample())
        self.assertTrue(ok)
        self.assertEqual(errors, [])

    def test_valid_sample_returns_two_tuple(self):
        result = _module.validate_sample(_valid_sample())
        self.assertIsInstance(result, tuple)
        self.assertEqual(len(result), 2)

    def test_valid_with_all_optional_fields(self):
        sample = _valid_sample(
            branch="feature/tests",
            created_at="2026-01-01T00:00:00Z",
            total_size=15,
            has_tests=1.0,
            has_docs=0.0,
            test_ratio=0.5,
            dir_spread=2,
            cluster_id=3,
            cluster_label="refactor",
            cluster_confidence=0.9,
            embedding_similarity_scores=[0.8, 0.7],
            plateau_detected=False,
            forecast_confidence=0.75,
            concerns=["minor style issue"],
            summary="Adds unit tests.",
        )
        ok, errors = _module.validate_sample(sample)
        self.assertTrue(ok)
        self.assertEqual(errors, [])

    def test_valid_with_null_optional_fields(self):
        sample = _valid_sample(
            cluster_id=None,
            cluster_label=None,
            cluster_confidence=None,
            embedding_similarity_scores=None,
            plateau_detected=None,
            forecast_confidence=None,
        )
        ok, errors = _module.validate_sample(sample)
        self.assertTrue(ok)
        self.assertEqual(errors, [])

    def test_all_valid_verdict_values(self):
        for verdict in ("APPROVE", "REQUEST_CHANGES", "NEEDS_DISCUSSION", "UNKNOWN"):
            with self.subTest(verdict=verdict):
                ok, _ = _module.validate_sample(_valid_sample(verdict=verdict))
                self.assertTrue(ok)

    def test_score_boundary_minimum(self):
        ok, _ = _module.validate_sample(_valid_sample(score=1))
        self.assertTrue(ok)

    def test_score_boundary_maximum(self):
        ok, _ = _module.validate_sample(_valid_sample(score=10))
        self.assertTrue(ok)

    def test_empty_files_list_is_valid(self):
        ok, _ = _module.validate_sample(_valid_sample(files=[]))
        self.assertTrue(ok)

    def test_zero_additions_and_deletions_is_valid(self):
        ok, _ = _module.validate_sample(_valid_sample(additions=0, deletions=0))
        self.assertTrue(ok)

    def test_embedding_similarity_scores_list_of_numbers_is_valid(self):
        ok, _ = _module.validate_sample(
            _valid_sample(embedding_similarity_scores=[0.1, 0.5, 0.95])
        )
        self.assertTrue(ok)

    def test_additional_unknown_properties_are_allowed(self):
        ok, _ = _module.validate_sample(_valid_sample(extra_field="allowed"))
        self.assertTrue(ok)


# ---------------------------------------------------------------------------
# Group 2: Missing required fields
# ---------------------------------------------------------------------------
class TestMissingRequiredFields(unittest.TestCase):
    """Omitting any required field must return (False, [non-empty])."""

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

    def _assert_invalid_without(self, field: str) -> None:
        sample = _valid_sample()
        del sample[field]
        ok, errors = _module.validate_sample(sample)
        self.assertFalse(ok, f"Missing '{field}' should be invalid")
        self.assertGreater(len(errors), 0, f"Missing '{field}' should produce errors")

    def test_missing_pr_number(self):
        self._assert_invalid_without("pr_number")

    def test_missing_repo(self):
        self._assert_invalid_without("repo")

    def test_missing_title(self):
        self._assert_invalid_without("title")

    def test_missing_author(self):
        self._assert_invalid_without("author")

    def test_missing_timestamp(self):
        self._assert_invalid_without("timestamp")

    def test_missing_additions(self):
        self._assert_invalid_without("additions")

    def test_missing_deletions(self):
        self._assert_invalid_without("deletions")

    def test_missing_file_count(self):
        self._assert_invalid_without("file_count")

    def test_missing_files(self):
        self._assert_invalid_without("files")

    def test_missing_score(self):
        self._assert_invalid_without("score")

    def test_missing_verdict(self):
        self._assert_invalid_without("verdict")

    def test_missing_decision(self):
        self._assert_invalid_without("decision")

    def test_missing_model(self):
        self._assert_invalid_without("model")

    def test_missing_json_parsed(self):
        self._assert_invalid_without("json_parsed")


# ---------------------------------------------------------------------------
# Group 3: Type violations
# ---------------------------------------------------------------------------
class TestTypeViolations(unittest.TestCase):
    """Supplying the wrong Python type for a field must return (False, [non-empty])."""

    def _assert_invalid(self, **override):
        ok, errors = _module.validate_sample(_valid_sample(**override))
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_pr_number_as_string(self):
        self._assert_invalid(pr_number="not-an-int")

    def test_score_as_string(self):
        self._assert_invalid(score="7")

    def test_json_parsed_as_string(self):
        self._assert_invalid(json_parsed="true")

    def test_files_as_string_not_list(self):
        self._assert_invalid(files="not-a-list")

    def test_additions_as_float(self):
        self._assert_invalid(additions=1.5)

    def test_deletions_as_float(self):
        self._assert_invalid(deletions=2.5)

    def test_verdict_as_integer(self):
        self._assert_invalid(verdict=1)

    def test_has_tests_as_string(self):
        self._assert_invalid(has_tests="yes")

    def test_cluster_id_as_string(self):
        self._assert_invalid(cluster_id="three")


# ---------------------------------------------------------------------------
# Group 4: Range constraint violations
# ---------------------------------------------------------------------------
class TestRangeConstraints(unittest.TestCase):
    """Values outside declared min/max must return (False, [non-empty])."""

    def _assert_invalid(self, **override):
        ok, errors = _module.validate_sample(_valid_sample(**override))
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_score_below_minimum(self):
        self._assert_invalid(score=0)

    def test_score_above_maximum(self):
        self._assert_invalid(score=11)

    def test_additions_negative(self):
        self._assert_invalid(additions=-1)

    def test_deletions_negative(self):
        self._assert_invalid(deletions=-1)

    def test_file_count_negative(self):
        self._assert_invalid(file_count=-1)

    def test_has_tests_above_maximum(self):
        self._assert_invalid(has_tests=1.5)

    def test_has_docs_below_minimum(self):
        self._assert_invalid(has_docs=-0.1)

    def test_cluster_confidence_above_maximum(self):
        self._assert_invalid(cluster_confidence=1.5)

    def test_forecast_confidence_below_minimum(self):
        self._assert_invalid(forecast_confidence=-0.1)


# ---------------------------------------------------------------------------
# Group 5: Enum constraint — 'verdict'
# ---------------------------------------------------------------------------
class TestEnumConstraints(unittest.TestCase):
    """Verdict values not in the allowed enum must return (False, [non-empty])."""

    def _assert_invalid(self, verdict):
        ok, errors = _module.validate_sample(_valid_sample(verdict=verdict))
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_invalid_verdict_string(self):
        self._assert_invalid("MERGE")

    def test_lowercase_verdict(self):
        self._assert_invalid("approve")

    def test_empty_string_verdict(self):
        self._assert_invalid("")

    def test_mixed_case_verdict(self):
        self._assert_invalid("Approve")


# ---------------------------------------------------------------------------
# Group 6: Array item type validation
# ---------------------------------------------------------------------------
class TestArrayItemValidation(unittest.TestCase):
    """Non-conforming items inside array fields must return (False, [non-empty])."""

    def test_files_with_non_string_item(self):
        ok, errors = _module.validate_sample(_valid_sample(files=["ok.py", 123]))
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_concerns_with_non_string_item(self):
        ok, errors = _module.validate_sample(_valid_sample(concerns=["ok", 42]))
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_embedding_similarity_scores_with_non_number_item(self):
        ok, errors = _module.validate_sample(
            _valid_sample(embedding_similarity_scores=["not-a-number"])
        )
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_concerns_as_list_of_strings_is_valid(self):
        ok, _ = _module.validate_sample(_valid_sample(concerns=["concern A", "concern B"]))
        self.assertTrue(ok)

    def test_empty_concerns_list_is_valid(self):
        ok, _ = _module.validate_sample(_valid_sample(concerns=[]))
        self.assertTrue(ok)


# ---------------------------------------------------------------------------
# Group 7: Return contract
# ---------------------------------------------------------------------------
class TestReturnContract(unittest.TestCase):
    """validate_sample must always return (bool, list[str])."""

    def test_valid_returns_true_and_empty_list(self):
        ok, errors = _module.validate_sample(_valid_sample())
        self.assertIs(ok, True)
        self.assertIsInstance(errors, list)
        self.assertEqual(errors, [])

    def test_invalid_returns_false_and_non_empty_list(self):
        sample = _valid_sample()
        del sample["pr_number"]
        ok, errors = _module.validate_sample(sample)
        self.assertIs(ok, False)
        self.assertIsInstance(errors, list)
        self.assertGreater(len(errors), 0)

    def test_errors_are_strings(self):
        sample = _valid_sample()
        del sample["pr_number"]
        _, errors = _module.validate_sample(sample)
        for err in errors:
            self.assertIsInstance(err, str)

    def test_multiple_missing_fields_all_reported(self):
        sample = _valid_sample()
        del sample["pr_number"]
        del sample["repo"]
        ok, errors = _module.validate_sample(sample)
        self.assertFalse(ok)
        self.assertGreaterEqual(len(errors), 2)


# ---------------------------------------------------------------------------
# Group 8: Manual fallback (jsonschema patched out)
# ---------------------------------------------------------------------------
class TestManualFallback(unittest.TestCase):
    """Patch jsonschema out of sys.modules to exercise the pure-Python fallback."""

    def _validate(self, sample: dict) -> tuple:
        with patch.dict("sys.modules", {"jsonschema": None}):
            return _module.validate_sample(sample)

    def test_valid_sample_passes(self):
        ok, errors = self._validate(_valid_sample())
        self.assertTrue(ok)
        self.assertEqual(errors, [])

    def test_missing_required_field_fails(self):
        sample = _valid_sample()
        del sample["pr_number"]
        ok, errors = self._validate(sample)
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_wrong_type_fails(self):
        ok, errors = self._validate(_valid_sample(pr_number="not-an-int"))
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_invalid_verdict_fails(self):
        ok, errors = self._validate(_valid_sample(verdict="INVALID"))
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_score_below_minimum_fails(self):
        ok, errors = self._validate(_valid_sample(score=0))
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_score_above_maximum_fails(self):
        ok, errors = self._validate(_valid_sample(score=11))
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_null_nullable_fields_pass(self):
        sample = _valid_sample(
            cluster_id=None,
            cluster_confidence=None,
            forecast_confidence=None,
            plateau_detected=None,
        )
        ok, errors = self._validate(sample)
        self.assertTrue(ok)
        self.assertEqual(errors, [])

    def test_out_of_range_float_fails(self):
        ok, errors = self._validate(_valid_sample(cluster_confidence=2.0))
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_missing_field_error_references_field_name(self):
        sample = _valid_sample()
        del sample["pr_number"]
        _, errors = self._validate(sample)
        self.assertTrue(
            any("pr_number" in e for e in errors),
            f"Expected 'pr_number' in errors; got: {errors}",
        )

    def test_range_error_message_contains_offending_value(self):
        _, errors = self._validate(_valid_sample(score=0))
        self.assertTrue(
            any("0" in e for e in errors),
            f"Expected offending value '0' in errors; got: {errors}",
        )

    def test_array_item_error_is_reported(self):
        ok, errors = self._validate(_valid_sample(files=["ok.py", 999]))
        self.assertFalse(ok)
        self.assertGreater(len(errors), 0)

    def test_enum_error_references_field(self):
        _, errors = self._validate(_valid_sample(verdict="BAD"))
        self.assertTrue(
            any("verdict" in e for e in errors),
            f"Expected 'verdict' in errors; got: {errors}",
        )


if __name__ == "__main__":
    unittest.main()
