"""Validation and deterministic traversal for ATG task graphs."""

from __future__ import annotations

from collections import Counter
import heapq
from numbers import Real
from typing import Any


def topological_order(data: dict[str, Any]) -> list[str] | None:
    """Return a deterministic topological order, or ``None`` for an invalid DAG."""
    if validate_graph_structure(data):
        return None
    return _topological_order(data)


def validate_graph(data: Any) -> list[str]:
    """Return all deterministic, fail-closed graph validation errors."""
    errors = validate_graph_structure(data)
    if errors:
        return errors

    if topological_order_without_validation(data) is None:
        errors.append("graph contains cycle")
    return errors


def validate_graph_structure(data: Any) -> list[str]:
    """Validate graph shape without performing cycle detection."""
    if not isinstance(data, dict):
        return ["graph must be an object"]
    if set(data) != {"nodes"}:
        return ["graph must contain exactly the nodes key"]

    nodes = data.get("nodes")
    if not isinstance(nodes, list) or not nodes:
        return ["nodes must be a non-empty array"]
    if any(not isinstance(node, dict) for node in nodes):
        return ["nodes must contain only objects"]

    errors: list[str] = []
    node_ids = [node.get("id") for node in nodes]
    known_ids = {node_id for node_id in node_ids if _is_nonempty_string(node_id)}
    counts = Counter(node_id for node_id in node_ids if _is_nonempty_string(node_id))
    for node_id, count in sorted(counts.items()):
        if count > 1:
            errors.append(f"duplicate node id: {node_id}")

    for node in nodes:
        node_id = node.get("id")
        display_id = node_id if _is_nonempty_string(node_id) else "<missing-id>"
        if not _is_nonempty_string(node_id):
            errors.append("node id must be a non-empty string")

        dependencies = node.get("deps")
        if not isinstance(dependencies, list):
            errors.append(f"node {display_id}: deps must be an array")
        else:
            for dependency in dependencies:
                if not _is_nonempty_string(dependency) or dependency not in known_ids:
                    errors.append(f"node {display_id}: unknown dependency: {dependency}")

        duration = node.get("duration")
        if (
            isinstance(duration, bool)
            or not isinstance(duration, Real)
            or duration <= 0
        ):
            errors.append(f"node {display_id}: duration must be positive")

        write_set = node.get("write_set")
        if not isinstance(write_set, list) or not write_set:
            errors.append(f"node {display_id}: write_set must be a non-empty array")
            write_set = []
        elif any(not _is_resource_id(item) for item in write_set):
            errors.append(f"node {display_id}: write_set contains an invalid resource")
        elif len(set(write_set)) != len(write_set):
            errors.append(f"node {display_id}: write_set contains duplicate resources")

        contract = node.get("contract")
        if not isinstance(contract, dict):
            errors.append(f"node {display_id}: contract must be an object")
            continue
        _validate_node_contract(contract, display_id, errors)
        outputs = contract.get("outputs")
        if not isinstance(outputs, list):
            errors.append(f"node {display_id}: contract.outputs must be an array")
            continue
        for output in outputs:
            if not _is_relative_path(output):
                errors.append(f"node {display_id}: output path is invalid: {output}")
                continue
            if not any(_write_set_covers(output, resource) for resource in write_set):
                errors.append(
                    f"node {display_id}: output '{output}' is not covered by write_set"
                )

    return errors


def _validate_node_contract(
    contract: dict[str, Any],
    node_id: str,
    errors: list[str],
) -> None:
    required = {
        "inputs",
        "outputs",
        "verify_command",
        "timeout_seconds",
        "max_attempts",
        "allowed_tools",
    }
    missing = sorted(required - set(contract))
    if missing:
        errors.append(
            f"node {node_id}: contract missing required key(s): {', '.join(missing)}"
        )
    unknown = sorted(set(contract) - required)
    if unknown:
        errors.append(
            f"node {node_id}: contract has unknown key(s): {', '.join(unknown)}"
        )
    for key in ("inputs", "outputs", "allowed_tools"):
        value = contract.get(key)
        if not isinstance(value, list) or (
            key == "allowed_tools" and not value
        ) or any(
            not _is_nonempty_string(item) for item in value
        ):
            errors.append(f"node {node_id}: contract.{key} must be an array of strings")
    if not _is_nonempty_string(contract.get("verify_command")):
        errors.append(f"node {node_id}: contract.verify_command must be non-empty")
    for key, minimum in (("timeout_seconds", 1), ("max_attempts", 0)):
        value = contract.get(key)
        if (
            isinstance(value, bool)
            or not isinstance(value, int)
            or value < minimum
        ):
            errors.append(f"node {node_id}: contract.{key} is invalid")


def topological_order_without_validation(data: dict[str, Any]) -> list[str] | None:
    return _topological_order(data)


def _topological_order(data: dict[str, Any]) -> list[str] | None:
    nodes = data["nodes"]
    adjacency: dict[str, list[str]] = {node["id"]: [] for node in nodes}
    indegree: dict[str, int] = {node["id"]: 0 for node in nodes}
    for node in nodes:
        for dependency in node["deps"]:
            adjacency[dependency].append(node["id"])
            indegree[node["id"]] += 1

    ready = [node_id for node_id, degree in indegree.items() if degree == 0]
    heapq.heapify(ready)
    ordered: list[str] = []
    while ready:
        current = heapq.heappop(ready)
        ordered.append(current)
        for follower in adjacency[current]:
            indegree[follower] -= 1
            if indegree[follower] == 0:
                heapq.heappush(ready, follower)
    return ordered if len(ordered) == len(nodes) else None


def _is_nonempty_string(value: Any) -> bool:
    return isinstance(value, str) and bool(value.strip())


def _is_resource_id(value: Any) -> bool:
    return (
        _is_nonempty_string(value)
        and "\\" not in value
        and not value.startswith("/")
        and not value.startswith("../")
        and "/../" not in value
    )


def _is_relative_path(value: Any) -> bool:
    return _is_resource_id(value)


def _write_set_covers(output: str, resource: str) -> bool:
    """出力パスが書込み集合の要素に覆われるかを判定する。

    判定規則は `atg.writeset.find_violations` と同一でなければならない。
    片方だけが末尾 `/` を要求すると、`graph check` は拒否するが
    `node check-writes` は許可する、という矛盾した挙動になる。
    """
    normalized = resource.rstrip("/")
    return output == normalized or output.startswith(f"{normalized}/")
