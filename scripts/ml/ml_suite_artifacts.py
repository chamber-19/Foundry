"""
Suite integration artifact generator for Foundry ML pipeline.

Produces deterministic, versioned artifacts that Suite can consume through its
existing local-model and runtime infrastructure. These artifacts bridge Foundry's
ML-powered learning analytics with Suite's production workflows.

Artifact types:
- operator-readiness: Signals to Suite about operator skill readiness for project tasks
- knowledge-index: Semantic index of imported knowledge for Suite's standards checker
- study-schedule: Adaptive study plan that can surface in Suite's project timeline
- watchdog-baseline: Anomaly detection baselines for Suite's watchdog telemetry

Input: JSON on stdin with analytics, embeddings, and forecast results
Output: JSON on stdout with versioned artifacts ready for Suite consumption

All artifacts are deterministic and review-first (matching Suite's design philosophy).
"""

import json
import os
import sys
import traceback
from datetime import datetime, timezone
from typing import Any


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def _build_operator_readiness(
    analytics: dict[str, Any],
    forecast: dict[str, Any],
) -> dict[str, Any]:
    """Build operator readiness artifact for Suite project assignment."""
    readiness_breakdown = analytics.get("readinessBreakdown", [])
    mastery_estimates = forecast.get("masteryEstimates", [])
    anomalies = forecast.get("anomalies", [])

    mastery_map = {m["topic"]: m for m in mastery_estimates}

    skill_signals = []
    for entry in readiness_breakdown:
        topic = entry["topic"]
        mastery = mastery_map.get(topic, {})
        skill_signals.append(
            {
                "topic": topic,
                "readiness": entry["readiness"],
                "confidence": entry.get("confidence", 0.5),
                "trend": entry.get("trend", entry["readiness"]),
                "improving": entry.get("improving", False),
                "mastered": mastery.get("mastered", False),
                "estimatedDaysToMastery": mastery.get("estimatedDays"),
            }
        )

    overall_readiness = analytics.get("overallReadiness", 0.0)
    active_anomalies = len(anomalies)

    return {
        "artifactType": "operator-readiness",
        "version": "1.0.0",
        "generatedAt": _now_iso(),
        "source": "foundry-ml-pipeline",
        "reviewRequired": True,
        "data": {
            "overallReadiness": overall_readiness,
            "skillSignals": skill_signals,
            "activeAnomalies": active_anomalies,
            "operatorPattern": analytics.get("operatorPattern", {}),
            "recommendation": "ready"
            if overall_readiness >= 0.75 and active_anomalies == 0
            else "developing"
            if overall_readiness >= 0.5
            else "needs-study",
        },
    }


def _build_knowledge_index(
    embeddings: dict[str, Any],
) -> dict[str, Any]:
    """Build knowledge index artifact for Suite's standards checker."""
    doc_embeddings = embeddings.get("embeddings", [])
    similarities = embeddings.get("similarities", [])

    index_entries = []
    for embedding in doc_embeddings:
        index_entries.append(
            {
                "documentId": embedding["documentId"],
                "title": embedding["title"],
                "dimensions": embedding["dimensions"],
                "indexed": True,
            }
        )

    clusters = []
    if similarities:
        high_sim_pairs = [s for s in similarities if s.get("similarity", 0) > 0.5]
        seen: set[str] = set()
        for pair in high_sim_pairs:
            doc_a = pair["documentA"]
            doc_b = pair["documentB"]
            if doc_a not in seen and doc_b not in seen:
                clusters.append(
                    {
                        "documents": [doc_a, doc_b],
                        "similarity": pair["similarity"],
                    }
                )
                seen.add(doc_a)
                seen.add(doc_b)

    return {
        "artifactType": "knowledge-index",
        "version": "1.0.0",
        "generatedAt": _now_iso(),
        "source": "foundry-ml-pipeline",
        "reviewRequired": False,
        "data": {
            "totalDocuments": len(index_entries),
            "indexedDocuments": index_entries,
            "documentClusters": clusters[:10],
            "embeddingEngine": embeddings.get("engine", "unknown"),
        },
    }


def _build_study_schedule(
    analytics: dict[str, Any],
    forecast: dict[str, Any],
) -> dict[str, Any]:
    """Build adaptive study schedule artifact for Suite's project timeline."""
    schedule = analytics.get("adaptiveSchedule", [])
    plateaus = forecast.get("plateaus", [])
    mastery_estimates = forecast.get("masteryEstimates", [])

    plateau_topics = {p["topic"] for p in plateaus}
    mastery_map = {m["topic"]: m for m in mastery_estimates}

    enriched_schedule = []
    for item in schedule:
        topic = item["topic"]
        entry = {
            **item,
            "plateauDetected": topic in plateau_topics,
            "estimatedDaysToMastery": mastery_map.get(topic, {}).get(
                "estimatedDays"
            ),
        }
        if topic in plateau_topics:
            plateau = next(p for p in plateaus if p["topic"] == topic)
            entry["plateauRecommendation"] = plateau.get("recommendation", "")
        enriched_schedule.append(entry)

    return {
        "artifactType": "study-schedule",
        "version": "1.0.0",
        "generatedAt": _now_iso(),
        "source": "foundry-ml-pipeline",
        "reviewRequired": True,
        "data": {
            "schedule": enriched_schedule,
            "totalTopics": len(enriched_schedule),
            "plateauCount": len(plateaus),
            "analyticsEngine": analytics.get("engine", "unknown"),
            "forecastEngine": forecast.get("engine", "unknown"),
        },
    }


def _build_watchdog_baseline(
    analytics: dict[str, Any],
    forecast: dict[str, Any],
) -> dict[str, Any]:
    """Build anomaly detection baseline for Suite's watchdog telemetry."""
    topic_clusters = analytics.get("topicClusters", [])
    anomalies = forecast.get("anomalies", [])
    forecasts = forecast.get("forecasts", [])

    baseline_metrics = []
    for fc in forecasts:
        baseline_metrics.append(
            {
                "topic": fc["topic"],
                "baselineAccuracy": fc["currentAccuracy"],
                "expectedRange": {
                    "low": round(max(0, fc["currentAccuracy"] - 0.15), 3),
                    "high": round(min(1, fc["currentAccuracy"] + 0.15), 3),
                },
                "trend": fc["trend"],
                "dataPoints": fc["dataPoints"],
            }
        )

    return {
        "artifactType": "watchdog-baseline",
        "version": "1.0.0",
        "generatedAt": _now_iso(),
        "source": "foundry-ml-pipeline",
        "reviewRequired": False,
        "data": {
            "baselineMetrics": baseline_metrics,
            "activeAnomalies": anomalies,
            "topicClusters": topic_clusters,
            "monitoringEnabled": len(baseline_metrics) > 0,
        },
    }


def _resolve_state_root() -> str:
    """Resolve the State root path: FOUNDRY_STATE_ROOT env var first, then platform default."""
    env_val = os.environ.get("FOUNDRY_STATE_ROOT", "")
    if env_val:
        return env_val
    if sys.platform == "win32":
        return r"C:\FoundryState"
    return os.path.join(os.path.expanduser("~"), "foundry-state")


def _load_scoring_model_metrics() -> dict[str, Any] | None:
    """Load model-metrics.json from the State artifacts directory if it exists."""
    try:
        metrics_path = os.path.join(_resolve_state_root(), "ml-artifacts", "model-metrics.json")
        if not os.path.exists(metrics_path):
            return None
        with open(metrics_path, "r") as f:
            return json.load(f)
    except Exception:
        return None


def _load_analytics_model_metrics() -> dict[str, Any] | None:
    """Load analytics-model-metrics.json if it exists; returns None otherwise."""
    try:
        metrics_path = os.path.join(_resolve_state_root(), "ml-artifacts", "analytics-model-metrics.json")
        if os.path.exists(metrics_path):
            with open(metrics_path, "r", encoding="utf-8") as f:
                return json.load(f)
    except Exception:
        pass
    return None


def main() -> None:
    try:
        raw = _read_input()
        payload = json.loads(raw) if raw.strip() else {}
    except json.JSONDecodeError:
        print(json.dumps({"ok": False, "error": "Invalid JSON input."}))
        return

    try:
        analytics = payload.get("analytics", {})
        embeddings = payload.get("embeddings", {})
        forecast = payload.get("forecast", {})

        artifacts = {
            "ok": True,
            "generatedAt": _now_iso(),
            "artifacts": [
                _build_operator_readiness(analytics, forecast),
                _build_knowledge_index(embeddings),
                _build_study_schedule(analytics, forecast),
                _build_watchdog_baseline(analytics, forecast),
            ],
        }

        scoring_model_metrics = _load_scoring_model_metrics()
        if scoring_model_metrics is not None:
            artifacts["scoringModelMetrics"] = scoring_model_metrics

        analytics_model_metrics = _load_analytics_model_metrics()
        if analytics_model_metrics is not None:
            artifacts["analyticsModelMetrics"] = analytics_model_metrics

        print(json.dumps(artifacts, ensure_ascii=False))
    except Exception:
        traceback.print_exc(file=sys.stderr)
        print(
            json.dumps(
                {
                    "ok": False,
                    "error": "An unexpected error occurred. See server logs for details.",
                }
            )
        )


def _read_input() -> str:
    """Read input from --input file argument or stdin."""
    for i, arg in enumerate(sys.argv[1:], 1):
        if arg == "--input" and i < len(sys.argv) - 1:
            from pathlib import Path

            return Path(sys.argv[i + 1]).read_text(encoding="utf-8")
    return sys.stdin.read()


if __name__ == "__main__":
    main()
