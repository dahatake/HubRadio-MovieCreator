from __future__ import annotations


def plan_merge_order(node_ids: list[str]) -> list[str]:
    if any(not isinstance(node_id, str) or not node_id.strip() for node_id in node_ids):
        raise ValueError("node ids must be non-empty strings")
    if len(set(node_ids)) != len(node_ids):
        raise ValueError("node ids must be unique")
    return sorted(node_ids)


def classify_conflict(
    merge_exit_code: int,
    verify_exit_code: int | None,
    writeset_violation: bool = False,
) -> str:
    if type(merge_exit_code) is not int:
        raise ValueError("merge_exit_code must be an integer")
    if verify_exit_code is not None and type(verify_exit_code) is not int:
        raise ValueError("verify_exit_code must be an integer")
    if not isinstance(writeset_violation, bool):
        raise ValueError("writeset_violation must be a boolean")
    if writeset_violation:
        return "contract_violation"
    if merge_exit_code != 0:
        return "mechanical"
    if verify_exit_code is not None and verify_exit_code != 0:
        return "semantic"
    return "none"
