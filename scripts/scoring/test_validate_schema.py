"""
Tests for validate_schema.py — Feature vector schema validator.

Covers:
  - Valid minimal and full samples pass
  - Missing required fields are reported
  - Type mismatches are reported
  - Integer range violations (score, additions, deletions, …)
  - Test-coverage fields: has_tests and test_ratio (0.0–1.0 bounds)
  - Enum validation for verdict
  - Nullable oneOf fields (cluster_id, cluster_label, cluster_confidence,
    embedding_similarity_scores, plateau_detected, forecast_confidence)

Run with:
    python -m unittest discover scripts/scoring
"""

import sys
import unittest
from pathlib import Path

# ---------------------------------------------------------------------------
# Import the module under test
# ---------------------------------------------------------------------------
sys.path.insert(0, str(Path(__file__).parent))

from validate_schema import validate_sample  # noqa: E402


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def _minimal_valid() -> dict:
    """Return the smallest sample that satisfies every required field."""
    return {
        "pr_number": 1,
        "repo": "Foundry",
        "title": "My PR",
        "author": "dev",
        "timestamp": "2024-01-01T00:00:00Z",
        "additions": 10,
        "deletions": 5,
        "file_count": 3,
        "files": ["src/foo.py"],
        "score": 7,
        "verdict": "APPROVE",
        "decision": "auto-merge",
        "model": "deepseek-r1:14b",
        "json_parsed": True,
    }


# ---------------------------------------------------------------------------
# Group 1: Valid samples
# ---------------------------------------------------------------------------
class TestValidSamples(unittest.TestCase):
    """Correctly shaped samples must return (True, [])."""

    def test_minimal_valid_sample_passes(self):
        ok, errors = validate_sample(_minimal_valid())
        self.assertTrue(ok, errors)
        self.assertEqual(errors, [])

    def test_full_sample_with_all_optional_fields_passes(self):
        sample = _minimal_valid()
        sample.update(
            {
                "branch": "feature/x",
                "created_at": "2024-01-01T00:00:00Z",
                "total_size": 15,
                "has_tests": 1.0,
                "has_docs": 0.0,
                "test_ratio": 0.33,
                "dir_spread": 2,
                "cluster_id": 3,
                "cluster_label": "docs-only",
                "cluster_confidence": 0.85,
                "embedding_similarity_scores": [0.9, 0.8],
                "plateau_detected": False,
                "forecast_confidence": 0.75,
                "concerns": ["Needs review"],
                "summary": "Short summary.",
            }
        )
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_nullable_fields_as_null_pass(self):
        sample = _minimal_valid()
        sample.update(
            {
                "cluster_id": None,
                "cluster_label": None,
                "cluster_confidence": None,
                "embedding_similarity_scores": None,
                "plateau_detected": None,
                "forecast_confidence": None,
            }
        )
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_score_boundary_minimum_passes(self):
        sample = _minimal_valid()
        sample["score"] = 1
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_score_boundary_maximum_passes(self):
        sample = _minimal_valid()
        sample["score"] = 10
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_all_verdict_enum_values_pass(self):
        for v in ("APPROVE", "REQUEST_CHANGES", "NEEDS_DISCUSSION", "UNKNOWN"):
            sample = _minimal_valid()
            sample["verdict"] = v
            ok, errors = validate_sample(sample)
            self.assertTrue(ok, f"verdict '{v}' should be valid; errors: {errors}")

    def test_has_tests_boundary_zero_passes(self):
        sample = _minimal_valid()
        sample["has_tests"] = 0.0
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_has_tests_boundary_one_passes(self):
        sample = _minimal_valid()
        sample["has_tests"] = 1.0
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_test_ratio_midpoint_passes(self):
        sample = _minimal_valid()
        sample["test_ratio"] = 0.5
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_additions_zero_passes(self):
        sample = _minimal_valid()
        sample["additions"] = 0
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_files_empty_list_passes(self):
        sample = _minimal_valid()
        sample["files"] = []
        sample["file_count"] = 0
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_additional_property_is_allowed(self):
        """Schema sets additionalProperties:true — unknown keys must not fail."""
        sample = _minimal_valid()
        sample["custom_field"] = "ignored"
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)


# ---------------------------------------------------------------------------
# Group 2: Missing required fields
# ---------------------------------------------------------------------------
class TestMissingRequiredFields(unittest.TestCase):
    """Each required field, when absent, must produce a validation error."""

    REQUIRED = [
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

    def _check_missing(self, field: str):
        sample = _minimal_valid()
        del sample[field]
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, f"Removing '{field}' should fail validation")
        self.assertTrue(len(errors) > 0, f"Expected at least one error for missing '{field}'")

    def test_missing_pr_number_fails(self):
        self._check_missing("pr_number")

    def test_missing_repo_fails(self):
        self._check_missing("repo")

    def test_missing_title_fails(self):
        self._check_missing("title")

    def test_missing_author_fails(self):
        self._check_missing("author")

    def test_missing_timestamp_fails(self):
        self._check_missing("timestamp")

    def test_missing_additions_fails(self):
        self._check_missing("additions")

    def test_missing_deletions_fails(self):
        self._check_missing("deletions")

    def test_missing_file_count_fails(self):
        self._check_missing("file_count")

    def test_missing_files_fails(self):
        self._check_missing("files")

    def test_missing_score_fails(self):
        self._check_missing("score")

    def test_missing_verdict_fails(self):
        self._check_missing("verdict")

    def test_missing_decision_fails(self):
        self._check_missing("decision")

    def test_missing_model_fails(self):
        self._check_missing("model")

    def test_missing_json_parsed_fails(self):
        self._check_missing("json_parsed")


# ---------------------------------------------------------------------------
# Group 3: Type violations
# ---------------------------------------------------------------------------
class TestTypeViolations(unittest.TestCase):
    """Wrong-typed values for known properties must fail validation."""

    def test_pr_number_as_string_fails(self):
        sample = _minimal_valid()
        sample["pr_number"] = "not-an-int"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_score_as_string_fails(self):
        sample = _minimal_valid()
        sample["score"] = "seven"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_json_parsed_as_string_fails(self):
        sample = _minimal_valid()
        sample["json_parsed"] = "true"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_additions_as_float_fails(self):
        sample = _minimal_valid()
        sample["additions"] = 3.5
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_files_as_string_fails(self):
        sample = _minimal_valid()
        sample["files"] = "not-an-array"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)


# ---------------------------------------------------------------------------
# Group 4: Integer range violations
# ---------------------------------------------------------------------------
class TestIntegerRangeViolations(unittest.TestCase):
    """Integer fields with minimum/maximum constraints must reject out-of-range values."""

    def test_score_below_minimum_fails(self):
        sample = _minimal_valid()
        sample["score"] = 0
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_score_above_maximum_fails(self):
        sample = _minimal_valid()
        sample["score"] = 11
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_additions_negative_fails(self):
        sample = _minimal_valid()
        sample["additions"] = -1
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_deletions_negative_fails(self):
        sample = _minimal_valid()
        sample["deletions"] = -5
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_file_count_negative_fails(self):
        sample = _minimal_valid()
        sample["file_count"] = -1
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)


# ---------------------------------------------------------------------------
# Group 5: Test-coverage fields (has_tests, test_ratio)
# ---------------------------------------------------------------------------
class TestCoverageFieldValidation(unittest.TestCase):
    """
    Test-coverage fields defined in feature-v1.json must enforce their
    0.0–1.0 bounds and number type.
    """

    # has_tests ──────────────────────────────────────────────────────────────

    def test_has_tests_above_maximum_fails(self):
        sample = _minimal_valid()
        sample["has_tests"] = 1.5
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, "has_tests > 1.0 should fail")
        self.assertTrue(len(errors) > 0)

    def test_has_tests_below_minimum_fails(self):
        sample = _minimal_valid()
        sample["has_tests"] = -0.1
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, "has_tests < 0.0 should fail")
        self.assertTrue(len(errors) > 0)

    def test_has_tests_as_string_fails(self):
        sample = _minimal_valid()
        sample["has_tests"] = "yes"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    # test_ratio ─────────────────────────────────────────────────────────────

    def test_test_ratio_above_maximum_fails(self):
        sample = _minimal_valid()
        sample["test_ratio"] = 2.0
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, "test_ratio > 1.0 should fail")
        self.assertTrue(len(errors) > 0)

    def test_test_ratio_below_minimum_fails(self):
        sample = _minimal_valid()
        sample["test_ratio"] = -0.5
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, "test_ratio < 0.0 should fail")
        self.assertTrue(len(errors) > 0)

    def test_test_ratio_as_string_fails(self):
        sample = _minimal_valid()
        sample["test_ratio"] = "high"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    # has_docs follows the same pattern ──────────────────────────────────────

    def test_has_docs_above_maximum_fails(self):
        sample = _minimal_valid()
        sample["has_docs"] = 1.1
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, "has_docs > 1.0 should fail")
        self.assertTrue(len(errors) > 0)

    def test_has_docs_below_minimum_fails(self):
        sample = _minimal_valid()
        sample["has_docs"] = -1.0
        ok, errors = validate_sample(sample)
        self.assertFalse(ok, "has_docs < 0.0 should fail")
        self.assertTrue(len(errors) > 0)


# ---------------------------------------------------------------------------
# Group 6: Verdict enum validation
# ---------------------------------------------------------------------------
class TestVerdictEnumValidation(unittest.TestCase):
    """verdict must be one of the four allowed values."""

    def test_unknown_verdict_fails(self):
        sample = _minimal_valid()
        sample["verdict"] = "LGTM"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_lowercase_verdict_fails(self):
        sample = _minimal_valid()
        sample["verdict"] = "approve"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_empty_verdict_fails(self):
        sample = _minimal_valid()
        sample["verdict"] = ""
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)


# ---------------------------------------------------------------------------
# Group 7: Nullable (oneOf) fields
# ---------------------------------------------------------------------------
class TestNullableFields(unittest.TestCase):
    """Fields typed as oneOf[T, null] must accept their typed value or null."""

    def test_cluster_id_as_integer_passes(self):
        sample = _minimal_valid()
        sample["cluster_id"] = 5
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_cluster_id_as_null_passes(self):
        sample = _minimal_valid()
        sample["cluster_id"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_cluster_id_as_string_fails(self):
        sample = _minimal_valid()
        sample["cluster_id"] = "cluster-3"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_cluster_confidence_valid_range_passes(self):
        sample = _minimal_valid()
        sample["cluster_confidence"] = 0.95
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_cluster_confidence_above_one_fails(self):
        sample = _minimal_valid()
        sample["cluster_confidence"] = 1.2
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_forecast_confidence_above_one_fails(self):
        sample = _minimal_valid()
        sample["forecast_confidence"] = 1.01
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_plateau_detected_as_boolean_passes(self):
        sample = _minimal_valid()
        sample["plateau_detected"] = True
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_plateau_detected_as_string_fails(self):
        sample = _minimal_valid()
        sample["plateau_detected"] = "yes"
        ok, errors = validate_sample(sample)
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_embedding_similarity_scores_as_list_of_numbers_passes(self):
        sample = _minimal_valid()
        sample["embedding_similarity_scores"] = [0.1, 0.9, 0.5]
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)

    def test_embedding_similarity_scores_as_null_passes(self):
        sample = _minimal_valid()
        sample["embedding_similarity_scores"] = None
        ok, errors = validate_sample(sample)
        self.assertTrue(ok, errors)


if __name__ == "__main__":
    unittest.main()
