"""ATG §6.4 adaptation advice.

This module only evaluates observations and returns a recommendation.
It never starts, stops, or reconfigures any parallel work.
"""

from __future__ import annotations


Action = tuple[str, str]


def advise(
    ready_count: int,
    rate_limited: bool,
    memory_pressure: bool,
    test_queue_len: int,
    merge_wave_active: bool = False,
) -> Action:
    """Return an ATG §6.4 action and reason from the observed signals."""
    if merge_wave_active:
        return (
            "hold",
            "merge wave active: serialize writes to the integration branch",
        )

    if rate_limited:
        return (
            "halve",
            "rate limited: halve P or serialize the affected write key",
        )

    if memory_pressure:
        return (
            "halve",
            "memory pressure: halve P or serialize the affected write key",
        )

    if test_queue_len > 0:
        return (
            "halve",
            "test queue growing: halve P or serialize the affected write key",
        )

    if ready_count > 0:
        return (
            "increase",
            "ready nodes available without pressure: increase P toward the upper bound",
        )

    return (
        "hold",
        "no ready nodes and no pressure signals: hold the current recommendation",
    )


__all__ = ["advise"]
