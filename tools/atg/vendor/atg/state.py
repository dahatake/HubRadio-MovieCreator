from __future__ import annotations

import sqlite3
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable
from uuid import uuid4


SCHEMA = """
CREATE TABLE IF NOT EXISTS executions (
    execution_id TEXT PRIMARY KEY,
    contract_sha256 TEXT NOT NULL,
    head_commit TEXT NOT NULL,
    started_at TEXT NOT NULL,
    finished_at TEXT,
    accepted INTEGER
);

CREATE TABLE IF NOT EXISTS nodes (
    execution_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    status TEXT NOT NULL CHECK(status IN ('pending','running','succeeded','failed','blocked')),
    attempt_count INTEGER NOT NULL DEFAULT 0,
    state_version INTEGER NOT NULL DEFAULT 0,
    lease_token TEXT,
    PRIMARY KEY (execution_id, node_id)
);

CREATE TABLE IF NOT EXISTS attempts (
    execution_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    attempt_number INTEGER NOT NULL,
    outcome TEXT CHECK(outcome IN ('running','succeeded','failed')),
    failure_class TEXT CHECK(failure_class IN ('transient','implementation','contract') OR failure_class IS NULL),
    started_at TEXT NOT NULL,
    finished_at TEXT,
    PRIMARY KEY (execution_id, node_id, attempt_number)
);

CREATE TABLE IF NOT EXISTS artifacts (
    execution_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    path TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    PRIMARY KEY (execution_id, node_id, path)
);
"""


@dataclass(frozen=True)
class NodeState:
    status: str
    attempt_count: int
    state_version: int
    lease_token: str | None


def _connect(db_path: str) -> sqlite3.Connection:
    return sqlite3.connect(db_path, isolation_level=None)


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _lease_token() -> str:
    return uuid4().hex


def _rollback(connection: sqlite3.Connection) -> None:
    try:
        connection.rollback()
    except sqlite3.Error:
        pass


def initialize(db_path: str) -> None:
    Path(db_path).parent.mkdir(parents=True, exist_ok=True)
    connection = _connect(db_path)
    try:
        connection.executescript(SCHEMA)
        connection.commit()
    finally:
        connection.close()


def begin_execution(
    db_path: str, execution_id: str, contract_sha256: str, head_commit: str
) -> None:
    connection = _connect(db_path)
    try:
        connection.execute(
            """
            INSERT INTO executions (
                execution_id, contract_sha256, head_commit, started_at, finished_at, accepted
            ) VALUES (?, ?, ?, ?, NULL, NULL)
            """,
            (execution_id, contract_sha256, head_commit, _utc_now()),
        )
        connection.commit()
    finally:
        connection.close()


def read_execution(db_path: str, execution_id: str) -> dict[str, object]:
    connection = _connect(db_path)
    try:
        row = connection.execute(
            """
            SELECT execution_id, contract_sha256, head_commit, accepted
            FROM executions
            WHERE execution_id = ?
            """,
            (execution_id,),
        ).fetchone()
        if row is None:
            raise ValueError(f"unknown execution: {execution_id}")
        return {
            "execution_id": str(row[0]),
            "contract_sha256": str(row[1]),
            "head_commit": str(row[2]),
            "accepted": None if row[3] is None else int(row[3]),
        }
    finally:
        connection.close()


def register_nodes(db_path: str, execution_id: str, node_ids: Iterable[str]) -> None:
    connection = _connect(db_path)
    try:
        connection.execute("BEGIN IMMEDIATE")
        execution = connection.execute(
            "SELECT 1 FROM executions WHERE execution_id = ?",
            (execution_id,),
        ).fetchone()
        if execution is None:
            raise ValueError(f"unknown execution: {execution_id}")
        for node_id in node_ids:
            connection.execute(
                """
                INSERT INTO nodes (
                    execution_id, node_id, status, attempt_count, state_version, lease_token
                ) VALUES (?, ?, 'pending', 0, 0, NULL)
                ON CONFLICT(execution_id, node_id) DO NOTHING
                """,
                (execution_id, node_id),
            )
        connection.commit()
    except Exception:
        _rollback(connection)
        raise
    finally:
        connection.close()


def finish_execution(db_path: str, execution_id: str, accepted: bool) -> None:
    connection = _connect(db_path)
    try:
        connection.execute(
            """
            UPDATE executions
            SET finished_at = ?, accepted = ?
            WHERE execution_id = ?
            """,
            (_utc_now(), 1 if accepted else 0, execution_id),
        )
        connection.commit()
    finally:
        connection.close()


def start_node(db_path: str, execution_id: str, node_id: str) -> tuple[bool, int]:
    connection = _connect(db_path)
    try:
        connection.execute("BEGIN IMMEDIATE")
        execution = connection.execute(
            "SELECT 1 FROM executions WHERE execution_id = ?",
            (execution_id,),
        ).fetchone()
        if execution is None:
            raise ValueError(f"unknown execution: {execution_id}")
        row = connection.execute(
            """
            SELECT status, attempt_count
            FROM nodes
            WHERE execution_id = ? AND node_id = ?
            """,
            (execution_id, node_id),
        ).fetchone()

        if row is None:
            attempt_number = 1
            connection.execute(
                """
                INSERT INTO nodes (
                    execution_id, node_id, status, attempt_count, state_version, lease_token
                ) VALUES (?, ?, 'running', ?, 1, ?)
                """,
                (execution_id, node_id, attempt_number, _lease_token()),
            )
        else:
            status, current_attempt_count = row
            if status != "pending":
                connection.commit()
                return False, int(current_attempt_count)

            attempt_number = int(current_attempt_count) + 1
            connection.execute(
                """
                UPDATE nodes
                SET status = 'running',
                    attempt_count = ?,
                    state_version = state_version + 1,
                    lease_token = ?
                WHERE execution_id = ? AND node_id = ?
                """,
                (attempt_number, _lease_token(), execution_id, node_id),
            )

        connection.execute(
            """
            INSERT INTO attempts (
                execution_id, node_id, attempt_number, outcome, failure_class, started_at, finished_at
            ) VALUES (?, ?, ?, 'running', NULL, ?, NULL)
            """,
            (execution_id, node_id, attempt_number, _utc_now()),
        )
        connection.commit()
        return True, attempt_number
    except Exception:
        _rollback(connection)
        raise
    finally:
        connection.close()


def finish_node(
    db_path: str,
    execution_id: str,
    node_id: str,
    *,
    output_artifacts: Iterable[tuple[str, str]] = (),
) -> None:
    connection = _connect(db_path)
    try:
        connection.execute("BEGIN IMMEDIATE")
        row = connection.execute(
            """
            SELECT attempt_count
            FROM nodes
            WHERE execution_id = ? AND node_id = ? AND status = 'running'
            """,
            (execution_id, node_id),
        ).fetchone()
        if row is None:
            raise ValueError(f"node is not running: {execution_id}/{node_id}")

        attempt_number = int(row[0])
        finished_at = _utc_now()
        for artifact_path, artifact_sha256 in output_artifacts:
            connection.execute(
                """
                INSERT INTO artifacts (execution_id, node_id, path, sha256)
                VALUES (?, ?, ?, ?)
                """,
                (execution_id, node_id, artifact_path, artifact_sha256),
            )
        connection.execute(
            """
            UPDATE nodes
            SET status = 'succeeded',
                state_version = state_version + 1,
                lease_token = NULL
            WHERE execution_id = ? AND node_id = ?
            """,
            (execution_id, node_id),
        )
        connection.execute(
            """
            UPDATE attempts
            SET outcome = 'succeeded',
                finished_at = ?
            WHERE execution_id = ? AND node_id = ? AND attempt_number = ?
            """,
            (finished_at, execution_id, node_id, attempt_number),
        )
        connection.commit()
    except Exception:
        _rollback(connection)
        raise
    finally:
        connection.close()


def record_failure(
    db_path: str,
    execution_id: str,
    node_id: str,
    failure_class: str,
    max_attempts: int,
    *,
    blocked_node_ids: Iterable[str] = (),
) -> tuple[bool, int]:
    if failure_class not in {"transient", "implementation", "contract"}:
        raise ValueError(f"unknown failure class: {failure_class}")
    if isinstance(max_attempts, bool) or not isinstance(max_attempts, int) or max_attempts < 1:
        raise ValueError("max_attempts must be a positive integer")

    blocked_ids = tuple(dict.fromkeys(blocked_node_ids))
    if node_id in blocked_ids:
        raise ValueError("a failed node cannot block itself")

    connection = _connect(db_path)
    try:
        connection.execute("BEGIN IMMEDIATE")
        row = connection.execute(
            """
            SELECT attempt_count
            FROM nodes
            WHERE execution_id = ? AND node_id = ? AND status = 'running'
            """,
            (execution_id, node_id),
        ).fetchone()
        if row is None:
            raise ValueError(f"node is not running: {execution_id}/{node_id}")

        attempt_count = int(row[0])
        retry_allowed = failure_class != "contract" and attempt_count < max_attempts
        next_status = "pending" if retry_allowed else "failed"
        finished_at = _utc_now()

        connection.execute(
            """
            UPDATE nodes
            SET status = ?,
                state_version = state_version + 1,
                lease_token = NULL
            WHERE execution_id = ? AND node_id = ?
            """,
            (next_status, execution_id, node_id),
        )
        connection.execute(
            """
            UPDATE attempts
            SET outcome = 'failed',
                failure_class = ?,
                finished_at = ?
            WHERE execution_id = ? AND node_id = ? AND attempt_number = ?
            """,
            (failure_class, finished_at, execution_id, node_id, attempt_count),
        )
        if not retry_allowed:
            for blocked_node_id in blocked_ids:
                connection.execute(
                    """
                    INSERT INTO nodes (
                        execution_id, node_id, status, attempt_count, state_version, lease_token
                    ) VALUES (?, ?, 'blocked', 0, 1, NULL)
                    ON CONFLICT(execution_id, node_id) DO UPDATE SET
                        status = 'blocked',
                        state_version = nodes.state_version + 1,
                        lease_token = NULL
                    WHERE nodes.status = 'pending'
                    """,
                    (execution_id, blocked_node_id),
                )
        connection.commit()
        return retry_allowed, attempt_count
    except Exception:
        _rollback(connection)
        raise
    finally:
        connection.close()


def is_terminal(db_path: str, execution_id: str, node_id: str) -> bool:
    connection = _connect(db_path)
    try:
        row = connection.execute(
            """
            SELECT status
            FROM nodes
            WHERE execution_id = ? AND node_id = ?
            """,
            (execution_id, node_id),
        ).fetchone()
        if row is None:
            return False
        return row[0] in {"succeeded", "failed", "blocked"}
    finally:
        connection.close()


def read_nodes(db_path: str, execution_id: str) -> dict[str, NodeState]:
    connection = _connect(db_path)
    try:
        rows = connection.execute(
            """
            SELECT node_id, status, attempt_count, state_version, lease_token
            FROM nodes
            WHERE execution_id = ?
            ORDER BY node_id
            """,
            (execution_id,),
        ).fetchall()
        return {
            str(node_id): NodeState(
                status=str(status),
                attempt_count=int(attempt_count),
                state_version=int(state_version),
                lease_token=None if lease_token is None else str(lease_token),
            )
            for node_id, status, attempt_count, state_version, lease_token in rows
        }
    finally:
        connection.close()
