"""Deterministic ATG scheduling rules R1-R4."""

from __future__ import annotations

from typing import Any, Mapping

try:
    from atg.bounds import bottom_level as _imported_bottom_level
    from atg.graph import validate_graph

    USING_IMPORTED_BOTTOM_LEVEL = True
except ImportError:
    _imported_bottom_level = None
    USING_IMPORTED_BOTTOM_LEVEL = False


def is_ready(
    node: dict[str, Any],
    succeeded_ids: set[str],
    active_ids: set[str],
    terminal_ids: set[str] | None = None,
) -> bool:
    """Return whether the node satisfies the R1 readiness rule.

    ``terminal_ids`` holds nodes that already reached a terminal state other than
    ``succeeded`` (``failed`` / ``blocked``). They can never be started again, so
    reporting them as runnable would make the caller's start/next loop spin
    forever without changing durable state.
    """
    node_id = str(node["id"])
    if node_id in succeeded_ids or node_id in active_ids:
        return False
    if terminal_ids is not None and node_id in terminal_ids:
        return False
    return all(str(dependency) in succeeded_ids for dependency in node.get("deps", []))


def is_conflict_free(
    node: dict[str, Any],
    occupied_resources: set[str],
) -> bool:
    """Return whether the node satisfies the R2 mutual exclusion rule."""
    return set(node.get("write_set", [])).isdisjoint(occupied_resources)



def next_runnable(
    graph_data: dict,
    succeeded_ids: set[str],
    running_ids_and_writesets: dict[str, list[str]],
    limit: int | None = None,
    terminal_ids: set[str] | None = None,
) -> list[str]:
    """Return the next runnable node ids under rules R1-R4.

    ``terminal_ids`` must contain every node whose durable status is ``failed``
    or ``blocked``; omitting them makes this function report nodes that
    ``atg node start`` will always refuse.
    """
    if limit is not None and limit <= 0:
        return []

    nodes = graph_data.get("nodes", [])
    active_ids = set(running_ids_and_writesets)
    terminal = set(terminal_ids or ())
    running_resources = {
        resource
        for write_set in running_ids_and_writesets.values()
        for resource in write_set
    }
    priorities = _bottom_levels(graph_data)

    candidates = [
        node
        for node in nodes
        if is_ready(node, succeeded_ids, active_ids, terminal)
        and is_conflict_free(node, running_resources)
    ]
    ordered_candidates = sorted(
        candidates,
        key=lambda node: (-priorities[str(node["id"])], str(node["id"])),
    )

    selected: list[str] = []
    selected_resources: set[str] = set()
    for node in ordered_candidates:
        if limit is not None and len(selected) >= limit:
            break
        if not is_conflict_free(node, selected_resources):
            continue
        node_id = str(node["id"])
        selected.append(node_id)
        selected_resources.update(str(resource) for resource in node.get("write_set", []))

    return selected


def select_runnable_nodes(
    graph: dict[str, Any],
    states: Mapping[str, str],
    *,
    limit: int,
) -> list[str]:
    """Select R1-ready, R2-conflict-free nodes in R4 deterministic priority.

    This is the status-aware entry point. It validates its inputs and then
    delegates the actual selection to :func:`next_runnable` so that both entry
    points cannot drift apart again.
    """
    if isinstance(limit, bool) or not isinstance(limit, int) or limit < 1:
        raise ValueError("limit must be a positive integer")
    errors = validate_graph(graph)
    if errors:
        raise ValueError("; ".join(errors))

    node_by_id = {node["id"]: node for node in graph["nodes"]}
    unknown_states = set(states) - set(node_by_id)
    if unknown_states:
        raise ValueError(f"state contains unknown node IDs: {sorted(unknown_states)}")

    known_statuses = {"pending", "running", "succeeded", "failed", "blocked"}
    invalid_statuses = set(states.values()) - known_statuses
    if invalid_statuses:
        raise ValueError(
            f"state contains unknown statuses: {sorted(invalid_statuses)}"
        )

    succeeded_ids = {
        node_id for node_id, status in states.items() if status == "succeeded"
    }
    running_ids_and_writesets = {
        node_id: list(node_by_id[node_id]["write_set"])
        for node_id, status in states.items()
        if status == "running"
    }
    terminal_ids = {
        node_id
        for node_id, status in states.items()
        if status in ("failed", "blocked")
    }
    return next_runnable(
        graph,
        succeeded_ids,
        running_ids_and_writesets,
        limit=limit,
        terminal_ids=terminal_ids,
    )



def _bottom_levels(graph_data: dict[str, Any]) -> dict[str, float]:
    if _imported_bottom_level is not None:
        try:
            computed = _imported_bottom_level(graph_data)
            return {str(node_id): float(value) for node_id, value in computed.items()}
        except Exception:
            pass
    return _inline_bottom_levels(graph_data)



def _inline_bottom_levels(graph_data: dict[str, Any]) -> dict[str, float]:
    nodes = graph_data.get("nodes", [])
    node_by_id = {str(node["id"]): node for node in nodes}
    successors: dict[str, list[str]] = {node_id: [] for node_id in node_by_id}
    for node in nodes:
        node_id = str(node["id"])
        for dependency in node.get("deps", []):
            dependency_id = str(dependency)
            if dependency_id in successors:
                successors[dependency_id].append(node_id)

    memo: dict[str, float] = {}

    def visit(node_id: str) -> float:
        if node_id in memo:
            return memo[node_id]
        node = node_by_id[node_id]
        duration = float(node.get("duration", 0))
        follower_levels = [visit(successor) for successor in successors[node_id]]
        memo[node_id] = duration + (max(follower_levels) if follower_levels else 0.0)
        return memo[node_id]

    return {node_id: visit(node_id) for node_id in node_by_id}
