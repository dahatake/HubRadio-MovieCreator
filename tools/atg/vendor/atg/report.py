from __future__ import annotations

import sqlite3


TERMINAL_COVERAGE_STATUSES = {"succeeded", "failed", "blocked"}
NON_TERMINAL_STATUSES = {"pending", "running"}


def _connect(db_path: str) -> sqlite3.Connection:
    connection = sqlite3.connect(db_path)
    connection.row_factory = sqlite3.Row
    return connection


def _load_execution(connection: sqlite3.Connection, execution_id: str) -> sqlite3.Row:
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
    return row


def _load_nodes(connection: sqlite3.Connection, execution_id: str) -> list[sqlite3.Row]:
    return list(
        connection.execute(
            """
            WITH attempt_stats AS (
                SELECT execution_id, node_id, MAX(attempt_number) AS final_attempt
                FROM attempts
                WHERE execution_id = ?
                GROUP BY execution_id, node_id
            ),
            artifact_stats AS (
                SELECT execution_id, node_id, COUNT(*) AS artifact_count
                FROM artifacts
                WHERE execution_id = ?
                GROUP BY execution_id, node_id
            )
            SELECT
                n.node_id,
                n.status,
                COALESCE(a.final_attempt, n.attempt_count) AS final_attempt,
                COALESCE(r.artifact_count, 0) AS artifact_count
            FROM nodes AS n
            LEFT JOIN attempt_stats AS a
                ON a.execution_id = n.execution_id AND a.node_id = n.node_id
            LEFT JOIN artifact_stats AS r
                ON r.execution_id = n.execution_id AND r.node_id = n.node_id
            WHERE n.execution_id = ?
            ORDER BY n.node_id
            """,
            (execution_id, execution_id, execution_id),
        ).fetchall()
    )


def _determine_overall_status(node_rows: list[sqlite3.Row], accepted: int | None) -> str:
    terminated_count = sum(1 for row in node_rows if row["status"] in TERMINAL_COVERAGE_STATUSES)
    total_nodes = len(node_rows)
    has_non_terminal = any(row["status"] in NON_TERMINAL_STATUSES for row in node_rows)

    if total_nodes > 0 and terminated_count == total_nodes and accepted == 1:
        return "成功"
    if has_non_terminal:
        return "未完了"
    if accepted == 0:
        return "不合格"
    if accepted is None:
        return "未実施"
    return "要確認"


def _acceptance_display(accepted: int | None) -> tuple[str, str]:
    if accepted == 1:
        return "accepted=1", "合格"
    if accepted == 0:
        return "accepted=0", "不合格"
    return "未実施", "executions.accepted が NULL"


def _coverage_note(
    node_rows: list[sqlite3.Row], terminal_count: int, total_nodes: int
) -> str:
    """終端被覆の注記を返す。

    終端被覆は `failed` / `blocked` も「終端した」として数える指標であり、
    成功率ではない。100% でも失敗・ブロックを含みうるため、達成語を使わず
    内訳を必ず併記する。
    """
    if total_nodes == 0:
        return "ノードが登録されていない"
    succeeded = sum(1 for row in node_rows if row["status"] == "succeeded")
    failed = sum(1 for row in node_rows if row["status"] == "failed")
    blocked = sum(1 for row in node_rows if row["status"] == "blocked")
    breakdown = f"succeeded {succeeded} / failed {failed} / blocked {blocked}"
    if terminal_count < total_nodes:
        return f"未終端ノードあり（{breakdown}）"
    if failed or blocked:
        return f"全ノードが終端。ただし {breakdown} であり成功ではない"
    return f"全ノードが succeeded（{breakdown}）"


def generate_report(db_path: str, execution_id: str) -> str:
    connection = _connect(db_path)
    try:
        execution = _load_execution(connection, execution_id)
        node_rows = _load_nodes(connection, execution_id)
    finally:
        connection.close()

    accepted = execution["accepted"]
    overall_status = _determine_overall_status(node_rows, accepted)
    terminal_count = sum(1 for row in node_rows if row["status"] in TERMINAL_COVERAGE_STATUSES)
    total_nodes = len(node_rows)
    coverage = (terminal_count / total_nodes * 100.0) if total_nodes else 0.0
    acceptance_value, acceptance_note = _acceptance_display(accepted)
    coverage_note = _coverage_note(node_rows, terminal_count, total_nodes)

    lines = [
        "# ATG 終端レポート",
        "",
        f"総合結果: {overall_status}",
        "",
        "## 実行情報",
        f"- 実行ID: {execution['execution_id']}",
        f"- 契約ハッシュ: {execution['contract_sha256']}",
        f"- HEAD commit: {execution['head_commit']}",
        "",
        "## ノード別の最終ステータス",
        "| Node | Status | Attempts | Artifacts |",
        "|---|---|---:|---:|",
    ]

    if node_rows:
        for row in node_rows:
            lines.append(
                f"| {row['node_id']} | {row['status']} | {row['final_attempt']} | {row['artifact_count']} |"
            )
    else:
        lines.append("| (none) | pending | 0 | 0 |")

    lines.extend(
        [
            "",
            "## 機能受入条件",
            "| 指標 | 値 | 判定/注記 |",
            "|---|---|---|",
            "| 実行中の人手介入回数 | 未計測 | ATG §13.1 は実行ログ中のプロンプト数を数えると規定する。ATG CLI は実行ログを保持しないため本レポートでは計測しない |",
            f"| 必須ノードの終端被覆 | {coverage:.1f}% ({terminal_count}/{total_nodes}) | {coverage_note} |",
            f"| 終端受入 | {acceptance_value} | {acceptance_note} |",
            "| 契約外書込み | 未計測 | 本レポートの範囲外。`atg node check-writes` をノードごとに実行して確認する（未実行なら未検証のまま） |",
            "| 出典なし決定 | 未計測 | 本レポートの範囲外。`atg node finish` が handoff の `decisions[].source_refs` を検査済みだが、本表では件数を集計しない |",
        ]
    )

    return "\n".join(lines) + "\n"
