"""Upper-bound calculations for validated ATG graphs."""

from __future__ import annotations

from collections import defaultdict
from typing import Any, TypedDict

from .graph import topological_order, validate_graph


class Bounds(TypedDict):
    t1: float
    t_inf: float
    l_max: float
    speedup_upper_bound: float


def calculate_bounds(data: dict[str, Any], *, parallelism: int) -> Bounds:
    """Return all graph bounds required by the CLI in one deterministic result."""
    return {
        "t1": total_work(data),
        "t_inf": critical_path_length(data),
        "l_max": max_resource_load(data),
        "speedup_upper_bound": speedup_bound(data, parallelism),
    }


def total_work(data: dict[str, Any]) -> float:
    """Return T1, the total amount of work in the graph."""
    nodes, _node_map, _order = _validated_context(data)
    return sum(float(node["duration"]) for node in nodes)


def critical_path_length(data: dict[str, Any]) -> float:
    """Return T∞, the longest duration-weighted path in the DAG."""
    _nodes, node_map, order = _validated_context(data)
    longest_path_to: dict[str, float] = {}
    for node_id in order:
        node = node_map[node_id]
        longest_path_to[node_id] = float(node["duration"]) + max(
            (longest_path_to[dependency] for dependency in node["deps"]),
            default=0.0,
        )
    return max(longest_path_to.values(), default=0.0)


def resource_load(data: dict[str, Any]) -> dict[str, float]:
    """Return each resource's accumulated write load L(r)."""
    nodes, _node_map, _order = _validated_context(data)
    loads: defaultdict[str, float] = defaultdict(float)
    for node in nodes:
        duration = float(node["duration"])
        for resource in node["write_set"]:
            loads[resource] += duration
    return dict(sorted(loads.items()))


def max_resource_load(data: dict[str, Any]) -> float:
    """Return Lmax, the heaviest resource load in the graph."""
    loads = resource_load(data)
    return max(loads.values(), default=0.0)


def speedup_bound(data: dict[str, Any], p: int) -> float:
    """Return the upper bound min(P, T1/T∞, T1/Lmax)."""
    if isinstance(p, bool) or not isinstance(p, int) or p < 1:
        raise ValueError("p must be a positive integer")

    t1 = total_work(data)
    t_inf = critical_path_length(data)
    l_max = max_resource_load(data)
    if t_inf <= 0 or l_max <= 0:
        return float(p)
    return min(float(p), t1 / t_inf, t1 / l_max)


def bottom_level(data: dict[str, Any]) -> dict[str, float]:
    """Return b(v), the remaining critical-path length from each node."""
    nodes, node_map, order = _validated_context(data)
    successors: dict[str, list[str]] = {node_id: [] for node_id in node_map}
    for node in nodes:
        for dependency in node["deps"]:
            successors[dependency].append(node["id"])

    levels: dict[str, float] = {}
    for node_id in reversed(order):
        levels[node_id] = float(node_map[node_id]["duration"]) + max(
            (levels[successor] for successor in successors[node_id]),
            default=0.0,
        )
    return levels


def bottom_levels(data: dict[str, Any]) -> dict[str, float]:
    """Return the R4 bottom-level priorities for every graph node."""
    return bottom_level(data)


def _validated_context(
    data: dict[str, Any],
) -> tuple[list[dict[str, Any]], dict[str, dict[str, Any]], list[str]]:
    errors = validate_graph(data)
    if errors:
        raise ValueError("; ".join(errors))

    order = topological_order(data)
    if order is None:
        raise ValueError("graph contains cycle")

    nodes = data["nodes"]
    node_map = {node["id"]: node for node in nodes}
    return nodes, node_map, order
