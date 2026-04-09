"""
Integration tests for scripts/scoring/validate_schema.py.

These tests exercise validate_sample() directly (Python API) and the CLI
entry point (subprocess) to cover valid inputs, missing/wrong-typed fields,
boundary violations, enum constraints, nullable oneOf fields, and array
item-type checks.
"""

import importlib.util
import json
import subprocess
import sys
import pathlib
import pytest

# ---------------------------------------------------------------------------
# Import validate_sample without installing the package
# ---------------------------------------------------------------------------

_REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
_SCRIPT = _REPO_ROOT / "scripts" / "scoring" / "validate_schema.py"

spec = importlib.util.spec_from_file_location("validate_schema", _SCRIPT)
_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(_module)

validate_sample = _module.validate_sample


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

@pytest.fixture()
def minimal_valid():
    """A sample that satisfies every required field with correct types."""
    return {
        "pr_number": 42,
        "repo": "Foundry",
        "title": "Add feature X",
        "author": "octocat",
        "timestamp": "2024-01-15T10:00:00Z",
        "additions": 10,
        "deletions": 5,
        "file_count": 3,
        "files": ["src/foo.cs", "src/bar.cs", "tests/foo_test.cs"],
        "score": 7,
        "verdict": "APPROVE",
        "decision": "auto-merge",
        "model": "deepseek-r1:14b",
        "json_parsed": True,
    }


@pytest.fixture()
def complete_valid(minimal_valid):
    """A sample that includes every optional field with valid values."""
    return {
        **minimal_valid,
        "branch": "feature/add-x",
        "created_at": "2024-01-14T08:00:00Z",
        "total_size": 15,
        "has_tests": 1.0,
        "has_docs": 0.0,
        "test_ratio": 0.33,
        "dir_spread": 2,
        "cluster_id": 3,
        "cluster_label": "large-refactor",
        "cluster_confidence": 0.91,
        "embedding_similarity_scores": [0.85, 0.72, 0.60],
        "plateau_detected": False,
        "forecast_confidence": 0.78,
        "concerns": ["Missing error handling in bar.cs"],
        "summary": "Adds feature X with minor concerns.",
    }


# ---------------------------------------------------------------------------
# Happy-path tests
# ---------------------------------------------------------------------------

class TestValidSamples:
    def test_minimal_required_fields(self, minimal_valid):
        ok, errors = validate_sample(minimal_valid)
        assert ok is True
        assert errors == []

    def test_complete_with_all_optional_fields(self, complete_valid):
        ok, errors = validate_sample(complete_valid)
        assert ok is True
        assert errors == []

    def test_nullable_fields_set_to_none(self, minimal_valid):
        """Nullable oneOf fields are allowed to be null."""
        sample = {
            **minimal_valid,
            "cluster_id": None,
            "cluster_label": None,
            "cluster_confidence": None,
            "embedding_similarity_scores": None,
            "plateau_detected": None,
            "forecast_confidence": None,
        }
        ok, errors = validate_sample(sample)
        assert ok is True
        assert errors == []

    def test_empty_files_array(self, minimal_valid):
        ok, errors = validate_sample({**minimal_valid, "files": []})
        assert ok is True
        assert errors == []

    def test_empty_concerns_array(self, minimal_valid):
        ok, errors = validate_sample({**minimal_valid, "concerns": []})
        assert ok is True
        assert errors == []

    def test_score_boundary_min(self, minimal_valid):
        ok, errors = validate_sample({**minimal_valid, "score": 1})
        assert ok is True

    def test_score_boundary_max(self, minimal_valid):
        ok, errors = validate_sample({**minimal_valid, "score": 10})
        assert ok is True

    def test_all_valid_verdict_values(self, minimal_valid):
        for verdict in ("APPROVE", "REQUEST_CHANGES", "NEEDS_DISCUSSION", "UNKNOWN"):
            ok, errors = validate_sample({**minimal_valid, "verdict": verdict})
            assert ok is True, f"Expected valid verdict '{verdict}', got errors: {errors}"

    def test_additional_properties_are_allowed(self, minimal_valid):
        """Schema uses additionalProperties: true, so unknown keys are fine."""
        ok, errors = validate_sample({**minimal_valid, "custom_field": "anything"})
        assert ok is True


# ---------------------------------------------------------------------------
# Missing required fields
# ---------------------------------------------------------------------------

REQUIRED_FIELDS = [
    "pr_number", "repo", "title", "author", "timestamp",
    "additions", "deletions", "file_count", "files",
    "score", "verdict", "decision", "model", "json_parsed",
]


class TestMissingRequiredFields:
    @pytest.mark.parametrize("field", REQUIRED_FIELDS)
    def test_missing_single_required_field(self, minimal_valid, field):
        sample = {k: v for k, v in minimal_valid.items() if k != field}
        ok, errors = validate_sample(sample)
        assert ok is False
        assert any(field in e for e in errors), (
            f"Expected error mentioning '{field}', got: {errors}"
        )

    def test_missing_all_required_fields(self):
        ok, errors = validate_sample({})
        assert ok is False
        assert len(errors) >= len(REQUIRED_FIELDS)


# ---------------------------------------------------------------------------
# Type violations
# ---------------------------------------------------------------------------

class TestTypeViolations:
    def test_pr_number_wrong_type(self, minimal_valid):
        minimal_valid["pr_number"] = "not-an-int"
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_additions_wrong_type(self, minimal_valid):
        minimal_valid["additions"] = "ten"
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_deletions_wrong_type(self, minimal_valid):
        minimal_valid["deletions"] = 3.5
        ok, errors = validate_sample(minimal_valid)
        # 3.5 is a float, not an integer — expect a type error
        assert ok is False
        assert len(errors) >= 1

    def test_file_count_wrong_type(self, minimal_valid):
        minimal_valid["file_count"] = True  # bool is not treated as int
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_score_wrong_type(self, minimal_valid):
        minimal_valid["score"] = "seven"
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_json_parsed_wrong_type(self, minimal_valid):
        minimal_valid["json_parsed"] = 1  # int is not bool
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_files_not_array(self, minimal_valid):
        minimal_valid["files"] = "src/foo.cs"
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_files_array_with_non_string_items(self, minimal_valid):
        minimal_valid["files"] = [1, 2, 3]
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_repo_wrong_type(self, minimal_valid):
        minimal_valid["repo"] = 99
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_has_tests_wrong_type(self, minimal_valid):
        minimal_valid["has_tests"] = "yes"
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1


# ---------------------------------------------------------------------------
# Boundary violations
# ---------------------------------------------------------------------------

class TestBoundaryViolations:
    def test_score_below_minimum(self, minimal_valid):
        minimal_valid["score"] = 0
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_score_above_maximum(self, minimal_valid):
        minimal_valid["score"] = 11
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_additions_negative(self, minimal_valid):
        minimal_valid["additions"] = -1
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_deletions_negative(self, minimal_valid):
        minimal_valid["deletions"] = -5
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_file_count_negative(self, minimal_valid):
        minimal_valid["file_count"] = -1
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_has_tests_above_maximum(self, minimal_valid):
        minimal_valid["has_tests"] = 1.5
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_has_tests_below_minimum(self, minimal_valid):
        minimal_valid["has_tests"] = -0.1
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_cluster_confidence_above_maximum(self, minimal_valid):
        minimal_valid["cluster_confidence"] = 1.1
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_forecast_confidence_below_minimum(self, minimal_valid):
        minimal_valid["forecast_confidence"] = -0.5
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1


# ---------------------------------------------------------------------------
# Enum violations
# ---------------------------------------------------------------------------

class TestEnumViolations:
    def test_verdict_invalid_value(self, minimal_valid):
        minimal_valid["verdict"] = "LGTM"
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_verdict_case_sensitive(self, minimal_valid):
        minimal_valid["verdict"] = "approve"
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1


# ---------------------------------------------------------------------------
# Nullable oneOf field variations
# ---------------------------------------------------------------------------

class TestNullableOneOfFields:
    def test_cluster_id_valid_integer(self, minimal_valid):
        minimal_valid["cluster_id"] = 0
        ok, errors = validate_sample(minimal_valid)
        assert ok is True

    def test_cluster_id_null(self, minimal_valid):
        minimal_valid["cluster_id"] = None
        ok, errors = validate_sample(minimal_valid)
        assert ok is True

    def test_cluster_id_wrong_type(self, minimal_valid):
        minimal_valid["cluster_id"] = "group-1"
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_embedding_similarity_scores_valid_array(self, minimal_valid):
        minimal_valid["embedding_similarity_scores"] = [0.1, 0.9, 0.5]
        ok, errors = validate_sample(minimal_valid)
        assert ok is True

    def test_embedding_similarity_scores_null(self, minimal_valid):
        minimal_valid["embedding_similarity_scores"] = None
        ok, errors = validate_sample(minimal_valid)
        assert ok is True

    def test_embedding_similarity_scores_array_with_non_number(self, minimal_valid):
        minimal_valid["embedding_similarity_scores"] = [0.5, "high", 0.3]
        ok, errors = validate_sample(minimal_valid)
        assert ok is False
        assert len(errors) >= 1

    def test_plateau_detected_true(self, minimal_valid):
        minimal_valid["plateau_detected"] = True
        ok, errors = validate_sample(minimal_valid)
        assert ok is True

    def test_plateau_detected_false(self, minimal_valid):
        minimal_valid["plateau_detected"] = False
        ok, errors = validate_sample(minimal_valid)
        assert ok is True

    def test_plateau_detected_null(self, minimal_valid):
        minimal_valid["plateau_detected"] = None
        ok, errors = validate_sample(minimal_valid)
        assert ok is True


# ---------------------------------------------------------------------------
# CLI integration tests (subprocess)
# ---------------------------------------------------------------------------

class TestCLI:
    def _run_cli(self, payload: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            [sys.executable, str(_SCRIPT)],
            input=payload,
            capture_output=True,
            text=True,
        )

    def test_cli_valid_sample_exits_zero(self, minimal_valid):
        result = self._run_cli(json.dumps(minimal_valid))
        assert result.returncode == 0
        assert "OK" in result.stdout

    def test_cli_invalid_sample_exits_one(self, minimal_valid):
        minimal_valid["score"] = 99
        result = self._run_cli(json.dumps(minimal_valid))
        assert result.returncode == 1
        assert "INVALID" in result.stdout

    def test_cli_missing_required_fields_exits_one(self):
        result = self._run_cli(json.dumps({}))
        assert result.returncode == 1
        assert "INVALID" in result.stdout

    def test_cli_malformed_json_exits_one(self):
        result = self._run_cli("{not valid json}")
        assert result.returncode == 1
        assert "ERROR" in result.stderr

    def test_cli_reports_error_count(self, minimal_valid):
        # Remove two required fields so we expect at least 2 errors
        del minimal_valid["pr_number"]
        del minimal_valid["repo"]
        result = self._run_cli(json.dumps(minimal_valid))
        assert result.returncode == 1
        # Output should mention the error count (number >= 2)
        assert "error" in result.stdout.lower()

    def test_cli_valid_complete_sample_exits_zero(self, complete_valid):
        result = self._run_cli(json.dumps(complete_valid))
        assert result.returncode == 0
        assert "OK" in result.stdout
