"""
validate_schema.py — Feature vector schema validator for the ML scoring pipeline.

Loads schemas/feature-v1.json and validates a dict against it.

Usage (Python):
    from scripts.scoring.validate_schema import validate_sample
    ok, errors = validate_sample(sample_dict)

Usage (CLI — pipe a JSON object via stdin):
    python scripts/scoring/validate_schema.py < sample.json

Returns exit code 0 on success, 1 on validation failure.
"""

import json
import os
import sys
from typing import Any

# Resolve the schema path relative to this file's location:
#   scripts/scoring/validate_schema.py  →  ../../schemas/feature-v1.json
_SCHEMA_PATH = os.path.join(
    os.path.dirname(__file__), "..", "..", "schemas", "feature-v1.json"
)

_SCHEMA_CACHE: dict[str, Any] | None = None


def _load_schema() -> dict[str, Any]:
    global _SCHEMA_CACHE
    if _SCHEMA_CACHE is None:
        abs_path = os.path.abspath(_SCHEMA_PATH)
        with open(abs_path, "r", encoding="utf-8") as f:
            _SCHEMA_CACHE = json.load(f)
    return _SCHEMA_CACHE


# ─────────────────────────────────────────────
# Public API
# ─────────────────────────────────────────────

def validate_sample(sample: dict) -> tuple[bool, list[str]]:
    """Validate *sample* against the feature-v1 schema.

    Returns:
        (True, [])            — sample is valid
        (False, [<errors>])   — sample is invalid; errors lists what's wrong
    """
    schema = _load_schema()

    # Prefer jsonschema when available (pip install jsonschema)
    try:
        import jsonschema  # type: ignore

        validator = jsonschema.Draft7Validator(schema)
        errors = [e.message for e in sorted(validator.iter_errors(sample), key=str)]
        if errors:
            return False, errors
        return True, []
    except ImportError:
        pass

    # ── Manual fallback (no external dependencies) ──────────────────────────
    errors: list[str] = []

    required_fields: list[str] = schema.get("required", [])
    properties: dict[str, Any] = schema.get("properties", {})

    # 1. Required fields present
    for field in required_fields:
        if field not in sample:
            errors.append(f"'{field}' is a required property")

    # 2. Type + constraint checks for known properties
    for field, spec in properties.items():
        if field not in sample:
            continue  # already caught above if required
        value = sample[field]

        # Resolve oneOf (nullable types like cluster_id)
        if "oneOf" in spec:
            valid_types = spec["oneOf"]
        else:
            valid_types = [spec]

        type_ok = False
        for t_spec in valid_types:
            t = t_spec.get("type")
            if t == "null" and value is None:
                type_ok = True
                break
            if t == "integer" and isinstance(value, int) and not isinstance(value, bool):
                if "minimum" in t_spec and value < t_spec["minimum"]:
                    errors.append(
                        f"'{field}' must be >= {t_spec['minimum']} (got {value})"
                    )
                if "maximum" in t_spec and value > t_spec["maximum"]:
                    errors.append(
                        f"'{field}' must be <= {t_spec['maximum']} (got {value})"
                    )
                type_ok = True
                break
            if t == "number" and isinstance(value, (int, float)) and not isinstance(value, bool):
                if "minimum" in t_spec and value < t_spec["minimum"]:
                    errors.append(
                        f"'{field}' must be >= {t_spec['minimum']} (got {value})"
                    )
                if "maximum" in t_spec and value > t_spec["maximum"]:
                    errors.append(
                        f"'{field}' must be <= {t_spec['maximum']} (got {value})"
                    )
                type_ok = True
                break
            if t == "string" and isinstance(value, str):
                if "enum" in t_spec and value not in t_spec["enum"]:
                    errors.append(
                        f"'{field}' must be one of {t_spec['enum']} (got '{value}')"
                    )
                type_ok = True
                break
            if t == "boolean" and isinstance(value, bool):
                type_ok = True
                break
            if t == "array" and isinstance(value, list):
                item_spec = t_spec.get("items", {})
                item_type = item_spec.get("type")
                if item_type:
                    for i, item in enumerate(value):
                        if item_type == "string" and not isinstance(item, str):
                            errors.append(
                                f"'{field}[{i}]' must be a string (got {type(item).__name__})"
                            )
                        elif item_type == "number" and not isinstance(item, (int, float)):
                            errors.append(
                                f"'{field}[{i}]' must be a number (got {type(item).__name__})"
                            )
                type_ok = True
                break

        if not type_ok and value is not None:
            expected = " or ".join(
                t.get("type", "?") for t in valid_types if "type" in t
            )
            errors.append(
                f"'{field}' has wrong type (expected {expected}, got {type(value).__name__})"
            )

    return (len(errors) == 0), errors


# ─────────────────────────────────────────────
# CLI entry point
# ─────────────────────────────────────────────

if __name__ == "__main__":
    try:
        sample = json.load(sys.stdin)
    except json.JSONDecodeError:
        print("ERROR: invalid JSON input", file=sys.stderr)
        sys.exit(1)
    except Exception:
        print("ERROR: failed to read from stdin", file=sys.stderr)
        sys.exit(1)

    try:
        ok, errs = validate_sample(sample)
    except Exception:
        print("ERROR: internal validation error", file=sys.stderr)
        sys.exit(1)

    if ok:
        print("OK: sample is valid")
        sys.exit(0)
    else:
        print(f"INVALID: {len(errs)} error(s):")
        for err in errs:
            print(f"  - {err}")
        sys.exit(1)
