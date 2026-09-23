from __future__ import annotations

import hashlib
from pathlib import Path


REQUIRED_KEYS = (
    "schema_version",
    "execution_id",
    "node_id",
    "attempt",
    "root_contract_sha256",
    "input_artifacts",
    "decisions",
    "outputs",
    "verification",
)


def file_sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as file_obj:
        for chunk in iter(lambda: file_obj.read(8192), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_handoff(data: dict, base_dir: str) -> list[str]:
    errors: list[str] = []
    if not isinstance(data, dict):
        return ["handoff must be a dict"]

    for key in REQUIRED_KEYS:
        if key not in data:
            errors.append(f"missing required key: {key}")

    if data.get("schema_version") != 1:
        errors.append("schema_version must be 1")
    for key in ("execution_id", "node_id"):
        if not _is_nonempty_string(data.get(key)):
            errors.append(f"{key} must be a non-empty string")
    attempt = data.get("attempt")
    if type(attempt) is not int or attempt < 1:
        errors.append("attempt must be a positive integer")
    if not _is_sha256(data.get("root_contract_sha256")):
        errors.append("root_contract_sha256 must be a SHA-256 hex string")

    input_artifacts = data.get("input_artifacts")
    if not isinstance(input_artifacts, list):
        errors.append("input_artifacts must be a list")
    else:
        for index, input_artifact in enumerate(input_artifacts):
            if not isinstance(input_artifact, dict):
                errors.append(f"input_artifacts[{index}] must be a dict")
                continue
            if not _is_relative_path(input_artifact.get("path")):
                errors.append(
                    f"input_artifacts[{index}].path must be a normalized relative path"
                )
            if not _is_sha256(input_artifact.get("sha256")):
                errors.append(
                    f"input_artifacts[{index}].sha256 must be a SHA-256 hex string"
                )

    decisions = data.get("decisions")
    if isinstance(decisions, list):
        for index, decision in enumerate(decisions):
            if not isinstance(decision, dict):
                errors.append(f"decisions[{index}] must be a dict")
                continue
            if not _is_nonempty_string(decision.get("id")):
                errors.append(f"decisions[{index}].id must be a non-empty string")
            if not _is_nonempty_string(decision.get("statement")):
                errors.append(
                    f"decisions[{index}].statement must be a non-empty string"
                )
            source_refs = decision.get("source_refs")
            if not isinstance(source_refs, list):
                errors.append(f"decisions[{index}].source_refs must be a list")
            elif not source_refs:
                errors.append(f"decisions[{index}].source_refs must not be empty")
            elif any(not _is_nonempty_string(source_ref) for source_ref in source_refs):
                errors.append(
                    f"decisions[{index}].source_refs must contain non-empty strings"
                )
    elif "decisions" in data:
        errors.append("decisions must be a list")

    outputs = data.get("outputs")
    if isinstance(outputs, list):
        root_dir = Path(base_dir)
        for index, output in enumerate(outputs):
            if not isinstance(output, dict):
                errors.append(f"outputs[{index}] must be a dict")
                continue

            relative_path = output.get("path")
            if not _is_relative_path(relative_path):
                errors.append(
                    f"outputs[{index}].path must be a normalized relative path"
                )
                continue

            declared_sha256 = output.get("sha256")
            if not _is_sha256(declared_sha256):
                errors.append(
                    f"outputs[{index}].sha256 must be a SHA-256 hex string"
                )
                continue

            output_path = (root_dir / relative_path).resolve()
            if root_dir.resolve() not in output_path.parents:
                errors.append(
                    f"outputs[{index}].path must remain within the relative workspace"
                )
                continue
            if not output_path.is_file():
                errors.append(f"outputs[{index}].path does not exist: {relative_path}")
                continue

            actual_sha256 = file_sha256(str(output_path))
            if actual_sha256 != declared_sha256:
                errors.append(
                    f"outputs[{index}].sha256 mismatch for {relative_path}: "
                    f"expected {declared_sha256}, got {actual_sha256}"
                )
    elif "outputs" in data:
        errors.append("outputs must be a list")

    verification = data.get("verification")
    if isinstance(verification, dict):
        if not _is_nonempty_string(verification.get("command")):
            errors.append("verification.command must be a non-empty string")
        if not _is_relative_path(verification.get("evidence_path")):
            errors.append(
                "verification.evidence_path must be a normalized relative path"
            )
        if "exit_code" not in verification:
            errors.append("missing required key: verification.exit_code")
        elif type(verification["exit_code"]) is not int:
            errors.append("verification.exit_code must be an int (bool is not allowed)")
        elif verification["exit_code"] != 0:
            errors.append("verification.exit_code must be 0")
    elif "verification" in data:
        errors.append("verification must be a dict")

    return errors


def validate_handoff_for_node(
    data: dict,
    base_dir: str,
    *,
    execution_id: str,
    node_id: str,
    attempt: int,
    root_contract_sha256: str,
    expected_outputs: list[str],
    verify_command: str,
) -> list[str]:
    """Validate a handoff and bind it to the active execution and node."""
    errors = validate_handoff(data, base_dir)
    if not isinstance(data, dict):
        return errors

    expected_values = {
        "execution_id": execution_id,
        "node_id": node_id,
        "attempt": attempt,
        "root_contract_sha256": root_contract_sha256,
    }
    for key, expected in expected_values.items():
        if data.get(key) != expected:
            errors.append(f"handoff {key} does not match the active execution")

    verification = data.get("verification")
    if isinstance(verification, dict) and verification.get("command") != verify_command:
        errors.append("handoff verification.command does not match node contract")

    outputs = data.get("outputs")
    output_paths = {
        output.get("path")
        for output in (outputs if isinstance(outputs, list) else [])
        if isinstance(output, dict)
    }
    for output_path in expected_outputs:
        if output_path not in output_paths:
            errors.append(
                f"handoff outputs does not include declared node output: {output_path}"
            )
    return errors


def _is_relative_path(value: object) -> bool:
    return (
        isinstance(value, str)
        and bool(value)
        and "\\" not in value
        and not value.startswith("/")
        and not value.startswith("../")
        and "/../" not in value
    )


def _is_nonempty_string(value: object) -> bool:
    return isinstance(value, str) and bool(value.strip())


def _is_sha256(value: object) -> bool:
    return (
        isinstance(value, str)
        and len(value) == 64
        and all(character in "0123456789abcdefABCDEF" for character in value)
    )
