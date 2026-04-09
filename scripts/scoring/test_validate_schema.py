"""
Tests for validate_schema.py — feature-v1.json schema validation.

Covers the three new code quality metric fields:
  - code_coverage        (number 0.0–100.0, nullable)
  - technical_debt       (number >= 0.0, nullable)
  - cyclomatic_complexity (number >= 0.0, nullable)

Also verifies that existing required fields are still enforced and that
valid samples continue to pass.
"""

import sys
import unittest
from pathlib import Path

_SCORING_DIR = Path(__file__).parent
sys.path.insert(0, str(_SCORING_DIR))

from validate_schema import validate_sample  # noqa: E402


# ---------------------------------------------------------------------------
# Shared helpers
# ---------------------------------------------------------------------------
_MINIMAL_VALID_SAMPLE = {
    "pr_number": 1,
    "repo": "Foundry",
    "title": "Add feature",
    "author": "dev",
    "timestamp": "2024-01-01T00:00:00Z",
    "additions": 10,
    "deletions": 2,
    "file_count": 3,
    "files": ["src/Foo.cs"],
    "score": 7,
    "verdict": "APPROVE",
    "decision": "auto-merge",
    "model": "deepseek-r1:14b",
    "json_parsed": True,
}


def _sample(**overrides) -> dict:
    s = dict(_MINIMAL_VALID_SAMPLE)
    s.update(overrides)
    return s


# ---------------------------------------------------------------------------
# Group 1: Baseline — existing required fields still enforced
# ---------------------------------------------------------------------------
class TestRequiredFields(unittest.TestCase):
    def test_minimal_valid_sample_passes(self):
        ok, errors = validate_sample(_MINIMAL_VALID_SAMPLE)
        self.assertTrue(ok, errors)

    def test_missing_required_field_fails(self):
        s = dict(_MINIMAL_VALID_SAMPLE)
        del s["score"]
        ok, errors = validate_sample(s)
        self.assertFalse(ok)
        self.assertTrue(any("score" in e for e in errors))


# ---------------------------------------------------------------------------
# Group 2: code_coverage field
# ---------------------------------------------------------------------------
class TestCodeCoverageField(unittest.TestCase):
    def test_null_code_coverage_passes(self):
        ok, errors = validate_sample(_sample(code_coverage=None))
        self.assertTrue(ok, errors)

    def test_zero_code_coverage_passes(self):
        ok, errors = validate_sample(_sample(code_coverage=0.0))
        self.assertTrue(ok, errors)

    def test_hundred_code_coverage_passes(self):
        ok, errors = validate_sample(_sample(code_coverage=100.0))
        self.assertTrue(ok, errors)

    def test_mid_range_code_coverage_passes(self):
        ok, errors = validate_sample(_sample(code_coverage=87.5))
        self.assertTrue(ok, errors)

    def test_negative_code_coverage_fails(self):
        ok, errors = validate_sample(_sample(code_coverage=-1.0))
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_above_100_code_coverage_fails(self):
        ok, errors = validate_sample(_sample(code_coverage=100.1))
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_absent_code_coverage_passes(self):
        """Field is optional — omitting it entirely must not fail."""
        ok, errors = validate_sample(_MINIMAL_VALID_SAMPLE)
        self.assertTrue(ok, errors)


# ---------------------------------------------------------------------------
# Group 3: technical_debt field
# ---------------------------------------------------------------------------
class TestTechnicalDebtField(unittest.TestCase):
    def test_null_technical_debt_passes(self):
        ok, errors = validate_sample(_sample(technical_debt=None))
        self.assertTrue(ok, errors)

    def test_zero_technical_debt_passes(self):
        ok, errors = validate_sample(_sample(technical_debt=0.0))
        self.assertTrue(ok, errors)

    def test_positive_technical_debt_passes(self):
        ok, errors = validate_sample(_sample(technical_debt=42.5))
        self.assertTrue(ok, errors)

    def test_negative_technical_debt_fails(self):
        ok, errors = validate_sample(_sample(technical_debt=-5.0))
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_absent_technical_debt_passes(self):
        ok, errors = validate_sample(_MINIMAL_VALID_SAMPLE)
        self.assertTrue(ok, errors)


# ---------------------------------------------------------------------------
# Group 4: cyclomatic_complexity field
# ---------------------------------------------------------------------------
class TestCyclomaticComplexityField(unittest.TestCase):
    def test_null_cyclomatic_complexity_passes(self):
        ok, errors = validate_sample(_sample(cyclomatic_complexity=None))
        self.assertTrue(ok, errors)

    def test_zero_cyclomatic_complexity_passes(self):
        ok, errors = validate_sample(_sample(cyclomatic_complexity=0.0))
        self.assertTrue(ok, errors)

    def test_positive_cyclomatic_complexity_passes(self):
        ok, errors = validate_sample(_sample(cyclomatic_complexity=12.3))
        self.assertTrue(ok, errors)

    def test_negative_cyclomatic_complexity_fails(self):
        ok, errors = validate_sample(_sample(cyclomatic_complexity=-1.0))
        self.assertFalse(ok)
        self.assertTrue(len(errors) > 0)

    def test_absent_cyclomatic_complexity_passes(self):
        ok, errors = validate_sample(_MINIMAL_VALID_SAMPLE)
        self.assertTrue(ok, errors)


# ---------------------------------------------------------------------------
# Group 5: All three fields present together
# ---------------------------------------------------------------------------
class TestAllCodeQualityFieldsTogether(unittest.TestCase):
    def test_all_populated_passes(self):
        ok, errors = validate_sample(
            _sample(code_coverage=91.2, technical_debt=8.0, cyclomatic_complexity=5.5)
        )
        self.assertTrue(ok, errors)

    def test_all_null_passes(self):
        ok, errors = validate_sample(
            _sample(code_coverage=None, technical_debt=None, cyclomatic_complexity=None)
        )
        self.assertTrue(ok, errors)

    def test_mixed_null_and_value_passes(self):
        ok, errors = validate_sample(
            _sample(code_coverage=75.0, technical_debt=None, cyclomatic_complexity=3.0)
        )
        self.assertTrue(ok, errors)


if __name__ == "__main__":
    unittest.main()
