"""ATG CLI: spec.md §2 の14実行形式を各モジュールへ委譲する薄い統合層。

このモジュールは判定ロジックを持たない。各サブコマンドは対応する atg.* モジュールの
関数を呼び出し、その戻り値を JSON へ整形して標準出力へ書き出すだけである。
"""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import subprocess
import sys
import uuid
from pathlib import Path
from typing import Any, Sequence

from atg import artifacts, bounds, contract, graph, merge as merge_mod, report, schedule, state, writeset
from atg import adapt as adapt_mod


class InputError(Exception):
    """入力ファイルが読めない、または JSON として不正であることを表す。"""


def _emit(payload: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")


def _read_text(path: str) -> str:
    try:
        with open(path, "r", encoding="utf-8") as file_obj:
            return file_obj.read()
    except OSError as exc:
        raise InputError(f"cannot read {path}: {exc}") from exc


def _load_json(path: str) -> dict[str, Any]:
    """JSON を読み込む。spec.md §2.1 に従い例外は InputError へ正規化する。"""
    try:
        data = json.loads(_read_text(path))
    except json.JSONDecodeError as exc:
        raise InputError(f"{path} is not valid JSON: {exc}") from exc
    if not isinstance(data, dict):
        raise InputError(f"{path} must contain a JSON object")
    return data


def _load_contract(path: str) -> dict[str, Any]:
    """契約 JSON を読み込む。spec.md §3.1 に従い重複キーを拒否する。"""
    text = _read_text(path)
    try:
        if contract.check_duplicate_keys(text):
            raise InputError(f"{path} contains duplicate JSON keys")
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        raise InputError(f"{path} is not valid JSON: {exc}") from exc
    if not isinstance(data, dict):
        raise InputError(f"{path} must contain a JSON object")
    return data


def _git_toplevel() -> str | None:
    """カレントディレクトリを含む git 作業ツリーのルートを返す。git 管理外なら None。"""
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            capture_output=True,
            text=True,
            check=True,
        )
    except (subprocess.CalledProcessError, FileNotFoundError):
        return None
    top = result.stdout.strip()
    return top or None


def _resolve_state_dir(explicit: str | None) -> Path:
    """状態ディレクトリ（`.atg`）の既定解決順（spec.md）。

    キット配下やリポジトリのサブディレクトリで実行しても、状態が呼び出し元の
    カレントディレクトリ配下へ散らばらないようにする（D4 是正）。解決順:
    1. 明示指定（`init --dir`。`--state` の明示指定は `_default_state_path` が先に採る）
    2. 環境変数 `ATG_STATE_DIR`
    3. カレントディレクトリに既に `.atg` があればそれ（既存利用者の後方互換）
    4. `git rev-parse --show-toplevel` のルート直下 `.atg`
    5. カレントディレクトリ直下 `.atg`（git 管理外）
    """
    if explicit:
        return Path(explicit)
    env_dir = os.environ.get("ATG_STATE_DIR")
    if env_dir:
        return Path(env_dir)
    cwd_state = Path(".atg")
    if cwd_state.is_dir():
        return cwd_state
    toplevel = _git_toplevel()
    if toplevel:
        return Path(toplevel) / ".atg"
    return cwd_state


def _resolve_repo_dir(explicit: str | None) -> str:
    """`node check-writes` の検査対象ルートの既定解決順。

    明示指定が無ければ git 作業ツリーのルートを使うことで、リポジトリの
    サブディレクトリから実行しても `writeset._require_work_tree_root` の
    検査に通る（従来はカレントディレクトリ固定で、サブディレクトリからの
    実行が常に失敗していた）。git 管理外では従来どおり `.` にフォールバックする。
    """
    if explicit:
        return explicit
    toplevel = _git_toplevel()
    return toplevel if toplevel else "."


def _default_state_path(explicit: str | None) -> str:
    if explicit:
        return explicit
    return str(_resolve_state_dir(None) / "state.sqlite3")


def _context_path(state_path: str) -> Path:
    return Path(f"{state_path}.context.json")


def _write_context(state_path: str, context: dict[str, Any]) -> None:
    path = _context_path(state_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(context, ensure_ascii=False), encoding="utf-8")


def _read_context(state_path: str) -> dict[str, Any]:
    path = _context_path(state_path)
    if not path.is_file():
        raise ValueError("state context is missing; run atg run begin first")
    data = _load_json(str(path))
    if not isinstance(data, dict) or "graph" not in data or "contract_sha256" not in data:
        raise ValueError("state context is invalid")
    return data


def _descendants(graph_data: dict[str, Any], node_id: str) -> list[str]:
    followers = {
        str(node["id"]): [
            str(candidate["id"])
            for candidate in graph_data.get("nodes", [])
            if str(node["id"]) in candidate.get("deps", [])
        ]
        for node in graph_data.get("nodes", [])
    }
    pending = list(followers.get(node_id, []))
    descendants: list[str] = []
    while pending:
        current = pending.pop()
        if current in descendants:
            continue
        descendants.append(current)
        pending.extend(followers.get(current, []))
    return descendants


def _node_by_id(graph_data: dict[str, Any], node_id: str) -> dict[str, Any]:
    for node in graph_data.get("nodes", []):
        if str(node.get("id")) == node_id:
            return node
    raise ValueError(f"unknown node: {node_id}")


def _head_commit(repo_dir: str = ".") -> str:
    try:
        result = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=repo_dir,
            capture_output=True,
            text=True,
            check=True,
        )
        return result.stdout.strip()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return "unknown"


# ---------------------------------------------------------------------------
# サブコマンド実装（各関数は対応モジュールへの委譲のみ）
# ---------------------------------------------------------------------------


def _self_ignore(state_dir: Path) -> None:
    """ATG 自身の状態ディレクトリを git から隠す。

    これを怠ると `atg node check-writes` が ATG 自身の状態ファイルを
    契約外書込みとして報告し、実際の違反がノイズに埋もれる。
    """
    try:
        state_dir.mkdir(parents=True, exist_ok=True)
        ignore_file = state_dir / ".gitignore"
        if not ignore_file.exists():
            ignore_file.write_text("*\n", encoding="utf-8")
    except OSError:
        # 状態ディレクトリを作れない場合は後続処理が同じ原因で失敗するため、
        # ここでは握りつぶさず呼び出し側の失敗に委ねる。
        pass


def cmd_init(args: argparse.Namespace) -> int:
    target_dir = _resolve_state_dir(args.dir)
    if target_dir.exists() and not args.force:
        _emit({"created": []})
        return 1
    _self_ignore(target_dir)
    _emit({"created": [str(target_dir)]})
    return 0


def cmd_contract_check(args: argparse.Namespace) -> int:
    data = _load_contract(args.path)
    errors = contract.validate_contract(data)
    if errors:
        _emit({"valid": False, "errors": errors})
        return 1
    _emit({"valid": True, "sha256": contract.canonical_sha256(data)})
    return 0


def cmd_graph_check(args: argparse.Namespace) -> int:
    data = _load_json(args.path)
    errors = graph.validate_graph(data)
    if errors:
        _emit({"valid": False, "errors": errors})
        return 1
    _emit(
        {
            "valid": True,
            "T1": bounds.total_work(data),
            "Tinf": bounds.critical_path_length(data),
            "Lmax": bounds.max_resource_load(data),
            "speedup_bound": bounds.speedup_bound(data, p=len(data.get("nodes", [])) or 1),
        }
    )
    return 0


def cmd_run_begin(args: argparse.Namespace) -> int:
    contract_data = _load_contract(args.contract)
    if contract.validate_contract(contract_data):
        _emit({"execution_id": "", "head": "unknown"})
        return 1
    graph_data = _load_json(args.graph)
    if graph.validate_graph(graph_data):
        _emit({"execution_id": "", "head": "unknown"})
        return 1

    head = _head_commit()
    if head == "unknown":
        _emit({"execution_id": "", "head": "unknown"})
        return 2

    execution_id = str(uuid.uuid4())
    state_path = _default_state_path(args.state)
    _self_ignore(Path(state_path).parent)
    state.initialize(state_path)
    contract_sha256 = contract.canonical_sha256(contract_data)
    state.begin_execution(
        state_path, execution_id, contract_sha256, head
    )
    state.register_nodes(
        state_path,
        execution_id,
        [str(node["id"]) for node in graph_data.get("nodes", [])],
    )
    _write_context(
        state_path,
        {
            "execution_id": execution_id,
            "contract_sha256": contract_sha256,
            "graph": graph_data,
        },
    )
    _emit({"execution_id": execution_id, "head": head})
    return 0


def cmd_node_next(args: argparse.Namespace) -> int:
    graph_data = _load_json(args.graph)
    succeeded_ids: set[str] = set()
    running_ids_and_writesets: dict[str, list[str]] = {}
    terminal_ids: set[str] = set()

    if args.execution_id:
        state_path = _default_state_path(args.state)
        write_sets_by_id = {
            str(node["id"]): list(node.get("write_set", []))
            for node in graph_data.get("nodes", [])
        }
        for node_id, node_state in state.read_nodes(state_path, args.execution_id).items():
            if node_state.status == "succeeded":
                succeeded_ids.add(node_id)
            elif node_state.status == "running":
                running_ids_and_writesets[node_id] = write_sets_by_id.get(node_id, [])
            elif node_state.status in ("failed", "blocked"):
                terminal_ids.add(node_id)

    runnable = schedule.next_runnable(
        graph_data,
        succeeded_ids,
        running_ids_and_writesets,
        limit=args.limit,
        terminal_ids=terminal_ids,
    )
    _emit({"runnable": runnable})
    return 0


def cmd_node_start(args: argparse.Namespace) -> int:
    state_path = _default_state_path(args.state)
    context = _read_context(state_path)
    node_states = state.read_nodes(state_path, args.execution_id)
    current = node_states.get(args.node_id)
    if current is not None and current.status != "pending":
        started, attempt = state.start_node(state_path, args.execution_id, args.node_id)
        _emit({"started": started, "attempt": attempt})
        return 0 if started else 1
    graph_data = context["graph"]
    write_sets_by_id = {
        str(node["id"]): list(node.get("write_set", []))
        for node in graph_data.get("nodes", [])
    }
    succeeded_ids = {
        node_id for node_id, node_state in node_states.items()
        if node_state.status == "succeeded"
    }
    running_ids_and_writesets = {
        node_id: write_sets_by_id.get(node_id, [])
        for node_id, node_state in node_states.items()
        if node_state.status == "running"
    }
    runnable = schedule.next_runnable(
        graph_data,
        succeeded_ids,
        running_ids_and_writesets,
        limit=len(graph_data.get("nodes", [])) or None,
    )
    if args.node_id not in runnable:
        _emit({"error": f"node is not runnable: {args.node_id}"})
        return 1
    started, attempt = state.start_node(state_path, args.execution_id, args.node_id)
    _emit({"started": started, "attempt": attempt})
    return 0 if started else 1


def cmd_node_finish(args: argparse.Namespace) -> int:
    state_path = _default_state_path(args.state)
    context = _read_context(state_path)
    execution = state.read_execution(state_path, args.execution_id)
    node = _node_by_id(context["graph"], args.node_id)
    node_state = state.read_nodes(state_path, args.execution_id).get(args.node_id)
    if node_state is None or node_state.status != "running":
        _emit({"finished": False, "errors": [f"node is not running: {args.node_id}"]})
        return 1
    handoff_data = _load_json(args.handoff)
    base_dir = str(Path(args.handoff).parent)
    errors = artifacts.validate_handoff_for_node(
        handoff_data,
        base_dir,
        execution_id=args.execution_id,
        node_id=args.node_id,
        attempt=node_state.attempt_count,
        root_contract_sha256=str(execution["contract_sha256"]),
        expected_outputs=list(node["contract"]["outputs"]),
        verify_command=str(node["contract"]["verify_command"]),
    )
    verification = handoff_data.get("verification")
    exit_code_ok = isinstance(verification, dict) and verification.get("exit_code") == 0
    if errors or not exit_code_ok:
        if not exit_code_ok:
            errors = list(errors) + ["verification.exit_code must be 0 to finish successfully"]
        _emit({"finished": False, "errors": errors})
        return 1

    state.finish_node(
        state_path,
        args.execution_id,
        args.node_id,
        output_artifacts=tuple(
            (output["path"], output["sha256"]) for output in handoff_data["outputs"]
        ),
    )
    _emit({"finished": True})
    return 0


def cmd_node_fail(args: argparse.Namespace) -> int:
    state_path = _default_state_path(args.state)
    context = _read_context(state_path)
    retry, attempt = state.record_failure(
        state_path,
        args.execution_id,
        args.node_id,
        args.failure_class,
        args.max_attempts,
        blocked_node_ids=_descendants(context["graph"], args.node_id),
    )
    if retry:
        _emit({"retry": True, "attempt": attempt})
    elif args.failure_class == "contract":
        # 契約違反は試行回数に関係なく再試行しない（spec.md §6）。
        # 上限超過と同じ理由を返すと、呼び出し側が原因を取り違える。
        _emit({"retry": False, "reason": "contract_violation"})
    else:
        _emit({"retry": False, "reason": "max_attempts_exceeded"})
    return 0


def cmd_node_check_writes(args: argparse.Namespace) -> int:
    graph_data = _load_json(args.graph)
    node = next(
        (n for n in graph_data.get("nodes", []) if n.get("id") == args.node_id), None
    )
    if node is None:
        _emit({"ok": False, "violations": [f"unknown node: {args.node_id}"]})
        return 1

    changed = writeset.changed_paths(args.base, repo_dir=_resolve_repo_dir(args.repo_dir))
    violations = writeset.find_violations(changed, node.get("write_set", []))
    if violations:
        _emit({"ok": False, "violations": violations})
        return 1
    _emit({"ok": True})
    return 0


def cmd_parallel_advise(args: argparse.Namespace) -> int:
    action, reason = adapt_mod.advise(
        args.ready_count,
        args.rate_limited,
        args.memory_pressure,
        args.test_queue_len,
        merge_wave_active=args.merge_wave_active,
    )
    _emit({"action": action, "reason": reason})
    return 0


def cmd_merge_plan(args: argparse.Namespace) -> int:
    order = merge_mod.plan_merge_order(args.node_ids)
    _emit({"order": order})
    return 0


def cmd_merge_classify(args: argparse.Namespace) -> int:
    result_class = merge_mod.classify_conflict(
        args.merge_exit, args.verify_exit, writeset_violation=args.writeset_violation
    )
    _emit({"class": result_class})
    return 0

def cmd_run_finish(args: argparse.Namespace) -> int:
    state_path = _default_state_path(args.state)
    evidence_exists = Path(args.evidence).is_file()
    terminal = True
    if args.execution_id:
        context = _read_context(state_path)
        terminal = all(
            state.is_terminal(state_path, args.execution_id, str(node["id"]))
            for node in context["graph"].get("nodes", [])
        )
    accepted = args.acceptance_exit == 0 and evidence_exists and terminal
    if args.execution_id:
        state.finish_execution(state_path, args.execution_id, accepted)
    _emit({"accepted": accepted})
    return 0 if accepted else 1


def cmd_report(args: argparse.Namespace) -> int:
    state_path = _default_state_path(args.state)
    sys.stdout.write(report.generate_report(state_path, args.execution_id))
    return 0


# ---------------------------------------------------------------------------
# argparse 構成
# ---------------------------------------------------------------------------


def _bool_arg(value: str) -> bool:
    return value.lower() in ("1", "true", "yes", "on")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="atg", description="Autonomous Task Graph Kit")
    subparsers = parser.add_subparsers(dest="command", required=True)

    p_init = subparsers.add_parser("init")
    p_init.add_argument(
        "--dir", default=None,
        help="状態ディレクトリ（既定: ATG_STATE_DIR → 既存の ./.atg → git repo ルート直下 .atg → ./.atg）。",
    )
    p_init.add_argument("--force", action="store_true")
    p_init.set_defaults(func=cmd_init)

    p_contract = subparsers.add_parser("contract")
    contract_sub = p_contract.add_subparsers(dest="contract_command", required=True)
    p_contract_check = contract_sub.add_parser("check")
    p_contract_check.add_argument("path")
    p_contract_check.set_defaults(func=cmd_contract_check)

    p_graph = subparsers.add_parser("graph")
    graph_sub = p_graph.add_subparsers(dest="graph_command", required=True)
    p_graph_check = graph_sub.add_parser("check")
    p_graph_check.add_argument("path")
    p_graph_check.set_defaults(func=cmd_graph_check)

    p_run = subparsers.add_parser("run")
    run_sub = p_run.add_subparsers(dest="run_command", required=True)
    p_run_begin = run_sub.add_parser("begin")
    p_run_begin.add_argument("--contract", required=True)
    p_run_begin.add_argument("--graph", required=True)
    p_run_begin.add_argument("--state", default=None)
    p_run_begin.set_defaults(func=cmd_run_begin)
    p_run_finish = run_sub.add_parser("finish")
    p_run_finish.add_argument("--acceptance-exit", type=int, required=True)
    p_run_finish.add_argument("--evidence", required=True)
    p_run_finish.add_argument("--execution-id", default=None, dest="execution_id")
    p_run_finish.add_argument("--state", default=None)
    p_run_finish.set_defaults(func=cmd_run_finish)

    p_node = subparsers.add_parser("node")
    node_sub = p_node.add_subparsers(dest="node_command", required=True)

    p_node_next = node_sub.add_parser("next")
    p_node_next.add_argument("--limit", type=int, default=None)
    p_node_next.add_argument("--graph", required=True)
    p_node_next.add_argument("--execution-id", default=None, dest="execution_id")
    p_node_next.add_argument("--state", default=None)
    p_node_next.set_defaults(func=cmd_node_next)

    p_node_start = node_sub.add_parser("start")
    p_node_start.add_argument("node_id")
    p_node_start.add_argument("--execution-id", required=True, dest="execution_id")
    p_node_start.add_argument("--state", default=None)
    p_node_start.set_defaults(func=cmd_node_start)

    p_node_finish = node_sub.add_parser("finish")
    p_node_finish.add_argument("node_id")
    p_node_finish.add_argument("--handoff", required=True)
    p_node_finish.add_argument("--execution-id", required=True, dest="execution_id")
    p_node_finish.add_argument("--state", default=None)
    p_node_finish.set_defaults(func=cmd_node_finish)

    p_node_fail = node_sub.add_parser("fail")
    p_node_fail.add_argument("node_id")
    p_node_fail.add_argument(
        "--class", required=True, dest="failure_class",
        choices=["transient", "implementation", "contract"],
    )
    p_node_fail.add_argument("--execution-id", required=True, dest="execution_id")
    p_node_fail.add_argument("--max-attempts", type=int, default=3, dest="max_attempts")
    p_node_fail.add_argument("--state", default=None)
    p_node_fail.set_defaults(func=cmd_node_fail)

    p_node_check_writes = node_sub.add_parser("check-writes")
    p_node_check_writes.add_argument("node_id")
    p_node_check_writes.add_argument("--base", required=True)
    p_node_check_writes.add_argument("--graph", required=True)
    p_node_check_writes.add_argument(
        "--repo-dir", default=None, dest="repo_dir",
        help="検査対象の作業ツリー（既定: git repo ルート。git 管理外なら カレント）。"
        "ノード専用 worktree を検査する場合に指定する。",
    )
    p_node_check_writes.set_defaults(func=cmd_node_check_writes)

    p_parallel = subparsers.add_parser("parallel")
    parallel_sub = p_parallel.add_subparsers(dest="parallel_command", required=True)
    p_parallel_advise = parallel_sub.add_parser("advise")
    p_parallel_advise.add_argument("--ready-count", type=int, required=True, dest="ready_count")
    p_parallel_advise.add_argument(
        "--rate-limited", type=_bool_arg, required=True, dest="rate_limited"
    )
    p_parallel_advise.add_argument(
        "--memory-pressure", type=_bool_arg, required=True, dest="memory_pressure"
    )
    p_parallel_advise.add_argument(
        "--test-queue-len", type=int, required=True, dest="test_queue_len"
    )
    p_parallel_advise.add_argument(
        "--merge-wave-active", type=_bool_arg, default=False, dest="merge_wave_active"
    )
    p_parallel_advise.set_defaults(func=cmd_parallel_advise)

    p_merge = subparsers.add_parser("merge")
    merge_sub = p_merge.add_subparsers(dest="merge_command", required=True)
    p_merge_plan = merge_sub.add_parser("plan")
    p_merge_plan.add_argument("--node-ids", nargs="+", required=True, dest="node_ids")
    p_merge_plan.set_defaults(func=cmd_merge_plan)
    p_merge_classify = merge_sub.add_parser("classify")
    p_merge_classify.add_argument("--merge-exit", type=int, required=True, dest="merge_exit")
    p_merge_classify.add_argument("--verify-exit", type=int, required=True, dest="verify_exit")
    p_merge_classify.add_argument(
        "--writeset-violation", type=_bool_arg, default=False, dest="writeset_violation"
    )
    p_merge_classify.set_defaults(func=cmd_merge_classify)

    p_report = subparsers.add_parser("report")
    p_report.add_argument("--execution-id", required=True, dest="execution_id")
    p_report.add_argument("--state", default=None)
    p_report.set_defaults(func=cmd_report)

    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return args.func(args)
    except InputError as exc:
        # spec.md §2.1: 入力不正は例外を送出せず fail-closed な JSON で返す。
        _emit({"valid": False, "errors": [str(exc)]})
        return 1
    except ValueError as exc:
        _emit({"valid": False, "errors": [str(exc)]})
        return 1
    except sqlite3.Error as exc:
        _emit({"valid": False, "errors": [f"state store error: {exc}"]})
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
