import hashlib
import json
from typing import Any


_REQUIRED_KEYS = (
    "objective",
    "non_goals",
    "deliverables",
    "acceptance_command",
    "baseline_policy",
    "time_budget",
    "execution_envelope",
    "secret_policy",
)
_OPTIONAL_KEYS = ("parallelism_hint", "credit_budget")
_ALLOWED_KEYS = set(_REQUIRED_KEYS) | set(_OPTIONAL_KEYS)
_ENVELOPE_KEYS = {
    "code_changes",
    "dependency_additions",
    "external_network_writes",
    "production_deploy",
    "secret_read",
}
_STRING_KEYS = (
    "objective",
    "acceptance_command",
    "baseline_policy",
    "time_budget",
    "secret_policy",
)


def validate_contract(data: Any) -> list[str]:
    errors: list[str] = []

    if not isinstance(data, dict):
        return ["contract must be a dict"]

    for key in _REQUIRED_KEYS:
        if key not in data:
            errors.append(f"missing required key: {key}")

    for key in sorted(data):
        if key not in _ALLOWED_KEYS:
            errors.append(f"unknown top-level key: {key}")

    for key in _STRING_KEYS:
        value = data.get(key)
        if key in data and (not isinstance(value, str) or not value.strip()):
            errors.append(f"{key} must be a non-empty string")

    for key in ("non_goals", "deliverables"):
        value = data.get(key)
        if key in data and not _is_string_list(value):
            errors.append(f"{key} must be an array of non-empty strings")

    envelope = data.get("execution_envelope")
    if "execution_envelope" in data:
        if not isinstance(envelope, dict):
            errors.append("execution_envelope must be an object")
        else:
            missing = sorted(_ENVELOPE_KEYS - set(envelope))
            unknown = sorted(set(envelope) - _ENVELOPE_KEYS)
            if missing:
                errors.append(
                    "execution_envelope missing permission(s): " + ", ".join(missing)
                )
            if unknown:
                errors.append(
                    "execution_envelope unknown permission(s): " + ", ".join(unknown)
                )
            for key, value in envelope.items():
                if key in _ENVELOPE_KEYS and (
                    not isinstance(value, str) or not value.strip()
                ):
                    errors.append(
                        f"execution_envelope.{key} must be a non-empty string"
                    )

    if "credit_budget" in data and (
        not isinstance(data["credit_budget"], str) or not data["credit_budget"].strip()
    ):
        errors.append("credit_budget must be a non-empty string")

    if "parallelism_hint" in data and (
        isinstance(data["parallelism_hint"], bool)
        or not isinstance(data["parallelism_hint"], int)
        or data["parallelism_hint"] < 1
    ):
        errors.append("parallelism_hint must be a positive integer")

    return errors


def canonical_json(data: dict[str, Any]) -> bytes:
    """Return the contract's deterministic UTF-8 JSON representation."""
    return (
        json.dumps(
        data,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
        )
        + "\n"
    ).encode("utf-8")


def canonical_sha256(data: dict[str, Any]) -> str:
    return hashlib.sha256(canonical_json(data)).hexdigest()


def parse_contract_json(text: str) -> dict[str, Any]:
    """Parse contract JSON while rejecting duplicate object keys."""
    if check_duplicate_keys(text):
        raise ValueError("contract JSON contains duplicate keys")
    data = json.loads(text)
    if not isinstance(data, dict):
        raise ValueError("contract JSON must be an object")
    errors = validate_contract(data)
    if errors:
        raise ValueError("; ".join(errors))
    return data


def check_duplicate_keys(text: str) -> bool:
    has_duplicates = False

    def _hook(pairs):
        nonlocal has_duplicates
        seen = set()
        result = {}
        for key, value in pairs:
            if key in seen:
                has_duplicates = True
            seen.add(key)
            result[key] = value
        return result

    json.loads(text, object_pairs_hook=_hook)
    return has_duplicates


def _is_string_list(value: Any) -> bool:
    return isinstance(value, list) and all(
        isinstance(item, str) and item.strip() for item in value
    )
