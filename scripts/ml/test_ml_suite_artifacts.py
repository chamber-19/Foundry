"""
Tests for ml_suite_artifacts.py.

Integration tests cover exception paths through main():
  - JSONDecodeError → {"ok": false, "error": "Invalid JSON input."}
  - Unexpected exceptions → {"ok": false, "error": "An unexpected error occurred. See server logs for details."}
  - Happy path → {"ok": true, "artifacts": [...]}

Unit tests cover individual builder functions and helpers for schema validation
and error handling behavior without going through main().
"""

import json
import os
import sys
import tempfile
import unittest
from io import StringIO
from pathlib import Path
from unittest.mock import patch

# ---------------------------------------------------------------------------
# Import the module under test
# ---------------------------------------------------------------------------
_SCRIPTS_DIR = Path(__file__).parent
sys.path.insert(0, str(_SCRIPTS_DIR))

import ml_suite_artifacts as _module  # noqa: E402


# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
_STATIC_UNEXPECTED_ERROR = (
    "An unexpected error occurred. See server logs for details."
)
_STATIC_JSON_ERROR = "Invalid JSON input."

_MINIMAL_VALID_INPUT = json.dumps(
    {
        "analytics": {
            "overallReadiness": 0.8,
            "readinessBreakdown": [],
            "adaptiveSchedule": [],
            "topicClusters": [],
            "operatorPattern": {},
            "engine": "test-engine",
        },
        "embeddings": {
            "embeddings": [],
            "similarities": [],
            "engine": "test-engine",
        },
        "forecast": {
            "masteryEstimates": [],
            "anomalies": [],
            "plateaus": [],
            "forecasts": [],
            "engine": "test-engine",
        },
    }
)


# ---------------------------------------------------------------------------
# Helper
# ---------------------------------------------------------------------------
def _run_main_with_input(stdin_text: str) -> dict:
    """Run main() with the given stdin text and return parsed stdout JSON."""
    with patch("sys.stdin", StringIO(stdin_text)):
        captured = StringIO()
        with patch("sys.stdout", captured):
            _module.main()
    output = captured.getvalue().strip()
    return json.loads(output)


# ---------------------------------------------------------------------------
# Group 1: JSONDecodeError path
# ---------------------------------------------------------------------------
class TestJsonDecodeErrorPath(unittest.TestCase):
    """main() must return static 'Invalid JSON input.' on malformed JSON."""

    def test_invalid_json_returns_ok_false(self):
        result = _run_main_with_input("{not valid json}")
        self.assertFalse(result["ok"])

    def test_invalid_json_returns_static_error_string(self):
        result = _run_main_with_input("{not valid json}")
        self.assertEqual(result["error"], _STATIC_JSON_ERROR)

    def test_truncated_json_returns_static_error_string(self):
        result = _run_main_with_input('{"analytics": {')
        self.assertEqual(result["error"], _STATIC_JSON_ERROR)

    def test_bare_text_returns_static_error_string(self):
        result = _run_main_with_input("this is not json at all")
        self.assertEqual(result["error"], _STATIC_JSON_ERROR)

    def test_invalid_json_error_has_no_exception_details(self):
        """The error value must be a static string, not a dynamic exception message."""
        result = _run_main_with_input("{bad}")
        self.assertNotIn("JSONDecodeError", result["error"])
        self.assertNotIn("Expecting", result["error"])
        self.assertNotIn("line", result["error"])


# ---------------------------------------------------------------------------
# Group 2: Unexpected exception paths
# ---------------------------------------------------------------------------
class TestUnexpectedExceptionPath(unittest.TestCase):
    """All non-JSONDecodeError exceptions must return a static error string."""

    def _assert_unexpected_error_response(self, result: dict) -> None:
        self.assertFalse(result["ok"])
        self.assertEqual(result["error"], _STATIC_UNEXPECTED_ERROR)

    def test_key_error_in_artifact_builder_returns_static_detail(self):
        """A KeyError raised inside an artifact builder must produce static detail."""
        with patch.object(_module, "_build_operator_readiness", side_effect=KeyError("topic")):
            result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        self._assert_unexpected_error_response(result)

    def test_type_error_in_artifact_builder_returns_static_detail(self):
        with patch.object(_module, "_build_knowledge_index", side_effect=TypeError("bad type")):
            result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        self._assert_unexpected_error_response(result)

    def test_value_error_in_artifact_builder_returns_static_detail(self):
        with patch.object(_module, "_build_study_schedule", side_effect=ValueError("bad value")):
            result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        self._assert_unexpected_error_response(result)

    def test_runtime_error_in_artifact_builder_returns_static_detail(self):
        with patch.object(_module, "_build_watchdog_baseline", side_effect=RuntimeError("runtime fail")):
            result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        self._assert_unexpected_error_response(result)

    def test_attribute_error_returns_static_detail(self):
        with patch.object(_module, "_build_operator_readiness", side_effect=AttributeError("no attr")):
            result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        self._assert_unexpected_error_response(result)

    def test_exception_message_not_leaked_in_response(self):
        """Raw exception message must NOT appear in the error detail (no information disclosure)."""
        sentinel = "SENSITIVE_INTERNAL_DETAIL_12345"
        with patch.object(_module, "_build_operator_readiness", side_effect=RuntimeError(sentinel)):
            result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        self.assertNotIn(sentinel, result.get("error", ""))
        self.assertNotIn(sentinel, json.dumps(result))

    def test_unexpected_error_detail_is_static_string(self):
        """Error detail must be exactly the required static string, not a dynamic value."""
        with patch.object(_module, "_build_operator_readiness", side_effect=Exception("dynamic msg")):
            result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        self.assertEqual(result["error"], _STATIC_UNEXPECTED_ERROR)


# ---------------------------------------------------------------------------
# Group 3: Happy path / valid input
# ---------------------------------------------------------------------------
class TestHappyPath(unittest.TestCase):
    """Valid inputs must produce ok=True with all four artifact types."""

    def test_empty_input_returns_ok_true(self):
        result = _run_main_with_input("")
        self.assertTrue(result["ok"])

    def test_empty_input_produces_four_artifacts(self):
        result = _run_main_with_input("")
        self.assertEqual(len(result["artifacts"]), 4)

    def test_valid_input_returns_ok_true(self):
        result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        self.assertTrue(result["ok"])

    def test_valid_input_produces_four_artifacts(self):
        result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        self.assertEqual(len(result["artifacts"]), 4)

    def test_artifact_types_are_present(self):
        result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        types = {a["artifactType"] for a in result["artifacts"]}
        self.assertEqual(
            types,
            {
                "operator-readiness",
                "knowledge-index",
                "study-schedule",
                "watchdog-baseline",
            },
        )

    def test_each_artifact_has_required_fields(self):
        result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        required = {"artifactType", "version", "generatedAt", "source", "reviewRequired", "data"}
        for artifact in result["artifacts"]:
            for field in required:
                self.assertIn(field, artifact, f"Missing field '{field}' in {artifact['artifactType']}")

    def test_source_is_foundry_ml_pipeline(self):
        result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        for artifact in result["artifacts"]:
            self.assertEqual(artifact["source"], "foundry-ml-pipeline")

    def test_version_is_semver(self):
        result = _run_main_with_input(_MINIMAL_VALID_INPUT)
        for artifact in result["artifacts"]:
            parts = artifact["version"].split(".")
            self.assertEqual(len(parts), 3, f"Bad version in {artifact['artifactType']}")

    def test_whitespace_only_input_returns_ok_true(self):
        result = _run_main_with_input("   \n  ")
        self.assertTrue(result["ok"])

    def test_empty_json_object_returns_ok_true(self):
        result = _run_main_with_input("{}")
        self.assertTrue(result["ok"])


# ---------------------------------------------------------------------------
# Shared data for unit tests
# ---------------------------------------------------------------------------
_ANALYTICS_FULL = {
    "overallReadiness": 0.8,
    "readinessBreakdown": [
        {"topic": "alpha", "readiness": 0.9, "confidence": 0.8, "trend": 0.85, "improving": True},
        {"topic": "beta", "readiness": 0.4, "confidence": 0.6},
    ],
    "adaptiveSchedule": [
        {"topic": "alpha", "priority": "high"},
        {"topic": "gamma", "priority": "low"},
    ],
    "topicClusters": [{"cluster": 0, "topics": ["alpha"]}],
    "operatorPattern": {"style": "visual"},
    "engine": "sklearn-v1",
}

_FORECAST_FULL = {
    "masteryEstimates": [
        {"topic": "alpha", "mastered": True, "estimatedDays": 0},
        {"topic": "beta", "mastered": False, "estimatedDays": 14},
    ],
    "anomalies": [{"topic": "beta", "type": "plateau"}],
    "plateaus": [{"topic": "gamma", "recommendation": "review-basics"}],
    "forecasts": [
        {"topic": "alpha", "currentAccuracy": 0.9, "trend": "up", "dataPoints": 10},
        {"topic": "beta", "currentAccuracy": 0.2, "trend": "down", "dataPoints": 5},
    ],
    "engine": "tf-v1",
}

_EMBEDDINGS_FULL = {
    "embeddings": [
        {"documentId": "doc1", "title": "Doc One", "dimensions": 384},
        {"documentId": "doc2", "title": "Doc Two", "dimensions": 384},
    ],
    "similarities": [
        {"documentA": "doc1", "documentB": "doc2", "similarity": 0.9},
        {"documentA": "doc3", "documentB": "doc4", "similarity": 0.3},
    ],
    "engine": "pytorch-v1",
}

_ARTIFACT_SCHEMA = {"artifactType", "version", "generatedAt", "source", "reviewRequired", "data"}


# ---------------------------------------------------------------------------
# Group 4: Unit tests — _build_operator_readiness
# ---------------------------------------------------------------------------
class TestBuildOperatorReadiness(unittest.TestCase):
    """Unit tests for _build_operator_readiness schema and logic."""

    def test_returns_correct_artifact_type(self):
        result = _module._build_operator_readiness({}, {})
        self.assertEqual(result["artifactType"], "operator-readiness")

    def test_schema_has_all_required_fields(self):
        result = _module._build_operator_readiness({}, {})
        for field in _ARTIFACT_SCHEMA:
            self.assertIn(field, result, f"Missing top-level field: {field}")

    def test_data_has_required_keys(self):
        result = _module._build_operator_readiness({}, {})
        required = {"overallReadiness", "skillSignals", "activeAnomalies", "operatorPattern", "recommendation"}
        for key in required:
            self.assertIn(key, result["data"], f"Missing data key: {key}")

    def test_source_is_foundry_ml_pipeline(self):
        result = _module._build_operator_readiness({}, {})
        self.assertEqual(result["source"], "foundry-ml-pipeline")

    def test_review_required_is_true(self):
        result = _module._build_operator_readiness({}, {})
        self.assertTrue(result["reviewRequired"])

    def test_version_is_semver(self):
        result = _module._build_operator_readiness({}, {})
        parts = result["version"].split(".")
        self.assertEqual(len(parts), 3)

    def test_empty_inputs_produce_default_values(self):
        result = _module._build_operator_readiness({}, {})
        self.assertEqual(result["data"]["overallReadiness"], 0.0)
        self.assertEqual(result["data"]["skillSignals"], [])
        self.assertEqual(result["data"]["activeAnomalies"], 0)

    def test_recommendation_ready_when_high_readiness_no_anomalies(self):
        result = _module._build_operator_readiness(
            {"overallReadiness": 0.9}, {"anomalies": []}
        )
        self.assertEqual(result["data"]["recommendation"], "ready")

    def test_recommendation_developing_when_medium_readiness(self):
        result = _module._build_operator_readiness(
            {"overallReadiness": 0.6}, {"anomalies": []}
        )
        self.assertEqual(result["data"]["recommendation"], "developing")

    def test_recommendation_needs_study_when_low_readiness(self):
        result = _module._build_operator_readiness(
            {"overallReadiness": 0.3}, {"anomalies": []}
        )
        self.assertEqual(result["data"]["recommendation"], "needs-study")

    def test_recommendation_not_ready_when_anomalies_present(self):
        """High readiness is overridden to non-ready when anomalies exist."""
        result = _module._build_operator_readiness(
            {"overallReadiness": 0.9}, {"anomalies": [{"topic": "alpha"}]}
        )
        self.assertNotEqual(result["data"]["recommendation"], "ready")

    def test_skill_signals_built_from_readiness_breakdown(self):
        result = _module._build_operator_readiness(_ANALYTICS_FULL, _FORECAST_FULL)
        self.assertEqual(len(result["data"]["skillSignals"]), 2)

    def test_skill_signal_has_required_keys(self):
        result = _module._build_operator_readiness(_ANALYTICS_FULL, _FORECAST_FULL)
        required = {"topic", "readiness", "confidence", "trend", "improving", "mastered", "estimatedDaysToMastery"}
        for signal in result["data"]["skillSignals"]:
            for key in required:
                self.assertIn(key, signal, f"Missing signal key: {key}")

    def test_mastery_info_merged_into_skill_signal(self):
        result = _module._build_operator_readiness(_ANALYTICS_FULL, _FORECAST_FULL)
        alpha_signal = next(s for s in result["data"]["skillSignals"] if s["topic"] == "alpha")
        self.assertTrue(alpha_signal["mastered"])
        self.assertEqual(alpha_signal["estimatedDaysToMastery"], 0)

    def test_active_anomalies_count_matches_forecast(self):
        result = _module._build_operator_readiness(_ANALYTICS_FULL, _FORECAST_FULL)
        self.assertEqual(result["data"]["activeAnomalies"], len(_FORECAST_FULL["anomalies"]))

    def test_operator_pattern_forwarded(self):
        result = _module._build_operator_readiness(_ANALYTICS_FULL, _FORECAST_FULL)
        self.assertEqual(result["data"]["operatorPattern"], {"style": "visual"})


# ---------------------------------------------------------------------------
# Group 5: Unit tests — _build_knowledge_index
# ---------------------------------------------------------------------------
class TestBuildKnowledgeIndex(unittest.TestCase):
    """Unit tests for _build_knowledge_index schema and logic."""

    def test_returns_correct_artifact_type(self):
        result = _module._build_knowledge_index({})
        self.assertEqual(result["artifactType"], "knowledge-index")

    def test_schema_has_all_required_fields(self):
        result = _module._build_knowledge_index({})
        for field in _ARTIFACT_SCHEMA:
            self.assertIn(field, result, f"Missing top-level field: {field}")

    def test_data_has_required_keys(self):
        result = _module._build_knowledge_index({})
        required = {"totalDocuments", "indexedDocuments", "documentClusters", "embeddingEngine"}
        for key in required:
            self.assertIn(key, result["data"], f"Missing data key: {key}")

    def test_review_required_is_false(self):
        result = _module._build_knowledge_index({})
        self.assertFalse(result["reviewRequired"])

    def test_source_is_foundry_ml_pipeline(self):
        result = _module._build_knowledge_index({})
        self.assertEqual(result["source"], "foundry-ml-pipeline")

    def test_empty_embeddings_produces_zero_documents(self):
        result = _module._build_knowledge_index({})
        self.assertEqual(result["data"]["totalDocuments"], 0)
        self.assertEqual(result["data"]["indexedDocuments"], [])

    def test_total_documents_matches_embeddings_list(self):
        result = _module._build_knowledge_index(_EMBEDDINGS_FULL)
        self.assertEqual(result["data"]["totalDocuments"], 2)

    def test_index_entry_has_required_fields(self):
        result = _module._build_knowledge_index(_EMBEDDINGS_FULL)
        required = {"documentId", "title", "dimensions", "indexed"}
        for entry in result["data"]["indexedDocuments"]:
            for key in required:
                self.assertIn(key, entry, f"Missing index entry key: {key}")

    def test_all_index_entries_are_marked_indexed(self):
        result = _module._build_knowledge_index(_EMBEDDINGS_FULL)
        for entry in result["data"]["indexedDocuments"]:
            self.assertTrue(entry["indexed"])

    def test_similarity_above_threshold_creates_cluster(self):
        result = _module._build_knowledge_index(_EMBEDDINGS_FULL)
        # doc1/doc2 pair has similarity 0.9 (> 0.5), should form a cluster
        self.assertEqual(len(result["data"]["documentClusters"]), 1)

    def test_similarity_below_threshold_excluded_from_clusters(self):
        embeddings = {
            "embeddings": [],
            "similarities": [{"documentA": "doc1", "documentB": "doc2", "similarity": 0.3}],
        }
        result = _module._build_knowledge_index(embeddings)
        self.assertEqual(result["data"]["documentClusters"], [])

    def test_embedding_engine_forwarded(self):
        result = _module._build_knowledge_index(_EMBEDDINGS_FULL)
        self.assertEqual(result["data"]["embeddingEngine"], "pytorch-v1")

    def test_unknown_engine_fallback(self):
        result = _module._build_knowledge_index({})
        self.assertEqual(result["data"]["embeddingEngine"], "unknown")

    def test_document_clusters_capped_at_ten(self):
        similarities = [
            {"documentA": f"a{i}", "documentB": f"b{i}", "similarity": 0.9}
            for i in range(20)
        ]
        result = _module._build_knowledge_index({"similarities": similarities})
        self.assertLessEqual(len(result["data"]["documentClusters"]), 10)


# ---------------------------------------------------------------------------
# Group 6: Unit tests — _build_study_schedule
# ---------------------------------------------------------------------------
class TestBuildStudySchedule(unittest.TestCase):
    """Unit tests for _build_study_schedule schema and logic."""

    def test_returns_correct_artifact_type(self):
        result = _module._build_study_schedule({}, {})
        self.assertEqual(result["artifactType"], "study-schedule")

    def test_schema_has_all_required_fields(self):
        result = _module._build_study_schedule({}, {})
        for field in _ARTIFACT_SCHEMA:
            self.assertIn(field, result, f"Missing top-level field: {field}")

    def test_data_has_required_keys(self):
        result = _module._build_study_schedule({}, {})
        required = {"schedule", "totalTopics", "plateauCount", "analyticsEngine", "forecastEngine"}
        for key in required:
            self.assertIn(key, result["data"], f"Missing data key: {key}")

    def test_review_required_is_true(self):
        result = _module._build_study_schedule({}, {})
        self.assertTrue(result["reviewRequired"])

    def test_source_is_foundry_ml_pipeline(self):
        result = _module._build_study_schedule({}, {})
        self.assertEqual(result["source"], "foundry-ml-pipeline")

    def test_empty_inputs_produce_empty_schedule(self):
        result = _module._build_study_schedule({}, {})
        self.assertEqual(result["data"]["schedule"], [])
        self.assertEqual(result["data"]["totalTopics"], 0)
        self.assertEqual(result["data"]["plateauCount"], 0)

    def test_schedule_length_matches_adaptive_schedule(self):
        result = _module._build_study_schedule(_ANALYTICS_FULL, _FORECAST_FULL)
        self.assertEqual(result["data"]["totalTopics"], 2)
        self.assertEqual(len(result["data"]["schedule"]), 2)

    def test_plateau_detection_flag_set_for_matching_topic(self):
        result = _module._build_study_schedule(_ANALYTICS_FULL, _FORECAST_FULL)
        gamma_entry = next(e for e in result["data"]["schedule"] if e["topic"] == "gamma")
        self.assertTrue(gamma_entry["plateauDetected"])

    def test_plateau_detection_flag_false_for_non_plateau_topic(self):
        result = _module._build_study_schedule(_ANALYTICS_FULL, _FORECAST_FULL)
        alpha_entry = next(e for e in result["data"]["schedule"] if e["topic"] == "alpha")
        self.assertFalse(alpha_entry["plateauDetected"])

    def test_plateau_recommendation_added_for_plateau_topic(self):
        result = _module._build_study_schedule(_ANALYTICS_FULL, _FORECAST_FULL)
        gamma_entry = next(e for e in result["data"]["schedule"] if e["topic"] == "gamma")
        self.assertEqual(gamma_entry.get("plateauRecommendation"), "review-basics")

    def test_plateau_recommendation_absent_for_non_plateau_topic(self):
        result = _module._build_study_schedule(_ANALYTICS_FULL, _FORECAST_FULL)
        alpha_entry = next(e for e in result["data"]["schedule"] if e["topic"] == "alpha")
        self.assertNotIn("plateauRecommendation", alpha_entry)

    def test_mastery_estimate_merged_into_schedule_entry(self):
        result = _module._build_study_schedule(_ANALYTICS_FULL, _FORECAST_FULL)
        alpha_entry = next(e for e in result["data"]["schedule"] if e["topic"] == "alpha")
        self.assertEqual(alpha_entry["estimatedDaysToMastery"], 0)

    def test_plateau_count_matches_forecast(self):
        result = _module._build_study_schedule(_ANALYTICS_FULL, _FORECAST_FULL)
        self.assertEqual(result["data"]["plateauCount"], len(_FORECAST_FULL["plateaus"]))

    def test_analytics_engine_forwarded(self):
        result = _module._build_study_schedule(_ANALYTICS_FULL, _FORECAST_FULL)
        self.assertEqual(result["data"]["analyticsEngine"], "sklearn-v1")

    def test_forecast_engine_forwarded(self):
        result = _module._build_study_schedule(_ANALYTICS_FULL, _FORECAST_FULL)
        self.assertEqual(result["data"]["forecastEngine"], "tf-v1")

    def test_unknown_engines_as_fallback(self):
        result = _module._build_study_schedule({}, {})
        self.assertEqual(result["data"]["analyticsEngine"], "unknown")
        self.assertEqual(result["data"]["forecastEngine"], "unknown")


# ---------------------------------------------------------------------------
# Group 7: Unit tests — _build_watchdog_baseline
# ---------------------------------------------------------------------------
class TestBuildWatchdogBaseline(unittest.TestCase):
    """Unit tests for _build_watchdog_baseline schema and logic."""

    def test_returns_correct_artifact_type(self):
        result = _module._build_watchdog_baseline({}, {})
        self.assertEqual(result["artifactType"], "watchdog-baseline")

    def test_schema_has_all_required_fields(self):
        result = _module._build_watchdog_baseline({}, {})
        for field in _ARTIFACT_SCHEMA:
            self.assertIn(field, result, f"Missing top-level field: {field}")

    def test_data_has_required_keys(self):
        result = _module._build_watchdog_baseline({}, {})
        required = {"baselineMetrics", "activeAnomalies", "topicClusters", "monitoringEnabled"}
        for key in required:
            self.assertIn(key, result["data"], f"Missing data key: {key}")

    def test_review_required_is_false(self):
        result = _module._build_watchdog_baseline({}, {})
        self.assertFalse(result["reviewRequired"])

    def test_source_is_foundry_ml_pipeline(self):
        result = _module._build_watchdog_baseline({}, {})
        self.assertEqual(result["source"], "foundry-ml-pipeline")

    def test_empty_inputs_produce_empty_metrics(self):
        result = _module._build_watchdog_baseline({}, {})
        self.assertEqual(result["data"]["baselineMetrics"], [])
        self.assertFalse(result["data"]["monitoringEnabled"])

    def test_monitoring_enabled_when_forecasts_present(self):
        result = _module._build_watchdog_baseline(_ANALYTICS_FULL, _FORECAST_FULL)
        self.assertTrue(result["data"]["monitoringEnabled"])

    def test_baseline_metric_count_matches_forecasts(self):
        result = _module._build_watchdog_baseline(_ANALYTICS_FULL, _FORECAST_FULL)
        self.assertEqual(len(result["data"]["baselineMetrics"]), 2)

    def test_baseline_metric_has_required_fields(self):
        result = _module._build_watchdog_baseline(_ANALYTICS_FULL, _FORECAST_FULL)
        required = {"topic", "baselineAccuracy", "expectedRange", "trend", "dataPoints"}
        for metric in result["data"]["baselineMetrics"]:
            for key in required:
                self.assertIn(key, metric, f"Missing metric key: {key}")

    def test_expected_range_has_low_and_high(self):
        result = _module._build_watchdog_baseline(_ANALYTICS_FULL, _FORECAST_FULL)
        for metric in result["data"]["baselineMetrics"]:
            self.assertIn("low", metric["expectedRange"])
            self.assertIn("high", metric["expectedRange"])

    def test_expected_range_low_not_below_zero(self):
        forecast = {"forecasts": [{"topic": "x", "currentAccuracy": 0.05, "trend": "flat", "dataPoints": 1}]}
        result = _module._build_watchdog_baseline({}, forecast)
        self.assertGreaterEqual(result["data"]["baselineMetrics"][0]["expectedRange"]["low"], 0.0)

    def test_expected_range_high_not_above_one(self):
        forecast = {"forecasts": [{"topic": "x", "currentAccuracy": 0.95, "trend": "flat", "dataPoints": 1}]}
        result = _module._build_watchdog_baseline({}, forecast)
        self.assertLessEqual(result["data"]["baselineMetrics"][0]["expectedRange"]["high"], 1.0)

    def test_active_anomalies_forwarded_from_forecast(self):
        result = _module._build_watchdog_baseline(_ANALYTICS_FULL, _FORECAST_FULL)
        self.assertEqual(result["data"]["activeAnomalies"], _FORECAST_FULL["anomalies"])

    def test_topic_clusters_forwarded_from_analytics(self):
        result = _module._build_watchdog_baseline(_ANALYTICS_FULL, _FORECAST_FULL)
        self.assertEqual(result["data"]["topicClusters"], _ANALYTICS_FULL["topicClusters"])


# ---------------------------------------------------------------------------
# Group 8: Unit tests — _resolve_state_root
# ---------------------------------------------------------------------------
class TestResolveStateRoot(unittest.TestCase):
    """Unit tests for _resolve_state_root path resolution."""

    def test_env_var_overrides_default(self):
        with patch.dict(os.environ, {"FOUNDRY_STATE_ROOT": "/custom/state"}):
            self.assertEqual(_module._resolve_state_root(), "/custom/state")

    def test_empty_env_var_falls_back_to_default(self):
        with patch.dict(os.environ, {"FOUNDRY_STATE_ROOT": ""}, clear=False):
            result = _module._resolve_state_root()
            self.assertIsInstance(result, str)
            self.assertGreater(len(result), 0)

    def test_no_env_var_returns_platform_default_on_linux(self):
        env = {k: v for k, v in os.environ.items() if k != "FOUNDRY_STATE_ROOT"}
        with patch.dict(os.environ, env, clear=True):
            with patch.object(_module.sys, "platform", "linux"):
                result = _module._resolve_state_root()
        expected = os.path.join(os.path.expanduser("~"), "foundry-state")
        self.assertEqual(result, expected)

    def test_no_env_var_returns_windows_default_on_win32(self):
        env = {k: v for k, v in os.environ.items() if k != "FOUNDRY_STATE_ROOT"}
        with patch.dict(os.environ, env, clear=True):
            with patch.object(_module.sys, "platform", "win32"):
                result = _module._resolve_state_root()
        self.assertEqual(result, r"C:\FoundryState")


# ---------------------------------------------------------------------------
# Group 9: Unit tests — _load_scoring_model_metrics
# ---------------------------------------------------------------------------
class TestLoadScoringModelMetrics(unittest.TestCase):
    """Unit tests for _load_scoring_model_metrics file loading and error handling."""

    def test_returns_none_when_file_does_not_exist(self):
        with patch.object(_module, "_resolve_state_root", return_value="/nonexistent/path"):
            result = _module._load_scoring_model_metrics()
        self.assertIsNone(result)

    def test_returns_dict_when_file_is_valid_json(self):
        payload = {"accuracy": 0.95, "model": "deepseek-r1"}
        with tempfile.TemporaryDirectory() as tmpdir:
            ml_dir = os.path.join(tmpdir, "ml-artifacts")
            os.makedirs(ml_dir)
            metrics_path = os.path.join(ml_dir, "model-metrics.json")
            with open(metrics_path, "w", encoding="utf-8") as f:
                json.dump(payload, f)
            with patch.object(_module, "_resolve_state_root", return_value=tmpdir):
                result = _module._load_scoring_model_metrics()
        self.assertEqual(result, payload)

    def test_returns_none_when_file_contains_invalid_json(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            ml_dir = os.path.join(tmpdir, "ml-artifacts")
            os.makedirs(ml_dir)
            metrics_path = os.path.join(ml_dir, "model-metrics.json")
            with open(metrics_path, "w", encoding="utf-8") as f:
                f.write("{not valid json}")
            with patch.object(_module, "_resolve_state_root", return_value=tmpdir):
                result = _module._load_scoring_model_metrics()
        self.assertIsNone(result)

    def test_returns_none_on_permission_error(self):
        with patch.object(_module, "_resolve_state_root", side_effect=PermissionError("denied")):
            result = _module._load_scoring_model_metrics()
        self.assertIsNone(result)


# ---------------------------------------------------------------------------
# Group 10: Unit tests — _load_analytics_model_metrics
# ---------------------------------------------------------------------------
class TestLoadAnalyticsModelMetrics(unittest.TestCase):
    """Unit tests for _load_analytics_model_metrics file loading and error handling."""

    def test_returns_none_when_file_does_not_exist(self):
        with patch.object(_module, "_resolve_state_root", return_value="/nonexistent/path"):
            result = _module._load_analytics_model_metrics()
        self.assertIsNone(result)

    def test_returns_dict_when_file_is_valid_json(self):
        payload = {"f1": 0.88, "precision": 0.91}
        with tempfile.TemporaryDirectory() as tmpdir:
            ml_dir = os.path.join(tmpdir, "ml-artifacts")
            os.makedirs(ml_dir)
            metrics_path = os.path.join(ml_dir, "analytics-model-metrics.json")
            with open(metrics_path, "w", encoding="utf-8") as f:
                json.dump(payload, f)
            with patch.object(_module, "_resolve_state_root", return_value=tmpdir):
                result = _module._load_analytics_model_metrics()
        self.assertEqual(result, payload)

    def test_returns_none_when_file_contains_invalid_json(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            ml_dir = os.path.join(tmpdir, "ml-artifacts")
            os.makedirs(ml_dir)
            metrics_path = os.path.join(ml_dir, "analytics-model-metrics.json")
            with open(metrics_path, "w", encoding="utf-8") as f:
                f.write("<<not json>>")
            with patch.object(_module, "_resolve_state_root", return_value=tmpdir):
                result = _module._load_analytics_model_metrics()
        self.assertIsNone(result)

    def test_returns_none_on_os_error(self):
        with patch.object(_module, "_resolve_state_root", side_effect=OSError("disk error")):
            result = _module._load_analytics_model_metrics()
        self.assertIsNone(result)


# ---------------------------------------------------------------------------
# Group 11: Unit tests — _read_input
# ---------------------------------------------------------------------------
class TestReadInput(unittest.TestCase):
    """Unit tests for _read_input stdin and --input file argument handling."""

    def test_reads_from_stdin_by_default(self):
        with patch("sys.stdin", StringIO("hello from stdin")):
            with patch("sys.argv", ["ml_suite_artifacts.py"]):
                result = _module._read_input()
        self.assertEqual(result, "hello from stdin")

    def test_reads_from_file_when_input_arg_provided(self):
        content = json.dumps({"analytics": {}})
        with tempfile.NamedTemporaryFile(mode="w", suffix=".json", delete=False) as f:
            f.write(content)
            tmp_path = f.name
        try:
            with patch("sys.argv", ["ml_suite_artifacts.py", "--input", tmp_path]):
                result = _module._read_input()
            self.assertEqual(result, content)
        finally:
            os.unlink(tmp_path)

    def test_empty_stdin_returns_empty_string(self):
        with patch("sys.stdin", StringIO("")):
            with patch("sys.argv", ["ml_suite_artifacts.py"]):
                result = _module._read_input()
        self.assertEqual(result, "")

    def test_ignores_unknown_args_and_reads_stdin(self):
        with patch("sys.stdin", StringIO("data")):
            with patch("sys.argv", ["ml_suite_artifacts.py", "--unknown", "value"]):
                result = _module._read_input()
        self.assertEqual(result, "data")


if __name__ == "__main__":
    unittest.main()
