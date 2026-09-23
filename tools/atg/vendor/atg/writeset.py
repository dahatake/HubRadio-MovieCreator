from __future__ import annotations

import subprocess
from pathlib import Path


def changed_paths(base_rev: str, repo_dir: str) -> list[str]:
    """base_rev 以降に変更されたパスを返す。

    `git diff --name-only` は未追跡（新規作成）ファイルを列挙しないため、
    `git ls-files --others --exclude-standard` を併用する。これを怠ると、
    ノードが宣言外の新規ファイル（秘密ファイルを含む）を作成しても
    契約外書込みとして検出できない。
    """
    _require_work_tree_root(repo_dir)

    tracked = _git(["diff", "--name-only", base_rev], repo_dir)
    untracked = _git(["ls-files", "--others", "--exclude-standard"], repo_dir)

    seen: dict[str, None] = {}
    for line in tracked + untracked:
        if line.strip():
            seen.setdefault(_normalize_path(line), None)
    return list(seen)


def _require_work_tree_root(repo_dir: str) -> None:
    """`repo_dir` が作業ツリーのルートでなければ拒否する。

    `git diff --name-only` はリポジトリルート基準、
    `git ls-files --others` はカレントディレクトリ基準でパスを返す。
    サブディレクトリを渡すと両者の基準が食い違い、宣言外書込みを見逃す。
    推測で補正せず fail-closed で拒否する。
    """
    toplevel = _git(["rev-parse", "--show-toplevel"], repo_dir)[0]
    if Path(toplevel).resolve() != Path(repo_dir).resolve():
        raise ValueError(
            f"repo_dir must be the work tree root: {toplevel} (got {repo_dir})"
        )


def _git(args: list[str], repo_dir: str) -> list[str]:
    result = subprocess.run(
        ["git", *args],
        cwd=repo_dir,
        capture_output=True,
        text=True,
        check=True,
    )
    return result.stdout.splitlines()


def find_violations(changed: list[str], write_set: list[str]) -> list[str]:
    normalized_write_set = [_normalize_path(path).rstrip("/") for path in write_set if path.strip()]
    violations: list[str] = []

    for path in changed:
        normalized_path = _normalize_path(path)
        if any(
            normalized_path == allowed or normalized_path.startswith(f"{allowed}/")
            for allowed in normalized_write_set
        ):
            continue
        violations.append(normalized_path)

    return violations


def _normalize_path(path: str) -> str:
    return path.replace("\\", "/")
