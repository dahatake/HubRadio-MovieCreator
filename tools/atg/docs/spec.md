# ATG Kit v0.1 仕様書（実装の一次根拠）

**版**: 0.1.0
**目的**: 本ファイルは、以降のすべての実装タスクが `work/Autonomus Task Graph (ATG)-technical-description.md`（全1457行）を読まずに実装できるよう、実装対象を1ファイルへ固定する。実装タスクは本ファイルの該当章のみを読めばよい。
**由来**: ATG技術文書 §6・§7.1-7.5・§8.1-8.5・§9.1-9.5・§13.1、および `work/Autonomus Task Graph(ATG)-ImplementationPlan.md` §4 T0-1確定値（Q1〜Q10）。

---

## 1. 適用範囲と非適用範囲

### 1.1 適用範囲（実装するもの）

> 計画段階の分割判定・粒度・見積・完了条件は Skill 層の規約（`.github/skills/atg/references/planning.md`）として本キットに含むが、CLI は計画判定を強制しない（CLI 実行形式は 14 のまま）。

- ルート契約の検証・SHA-256固定（ATG §8.1）
- タスクグラフの検証（非巡回・書込み集合宣言）（ATG §6.1）
- 上界の算出（命題1〜4: T1・T∞・Lmax・加速上界）（ATG §7.1-7.5）
- 実行規則R1〜R4（起動可能ノードの決定。**判定のみ、起動はしない**）（ATG §6.2）
- 有界リワーク（R5。失敗クラスは呼び出し側が明示）（ATG §6.2）
- 耐久制御状態（sqlite3。リース・重複起動拒否・再開時の重複実行防止）（ATG §9.5）
- 成果物ハッシュとhandoff検証（ATG §8.4）
- 宣言外書込みの検出（ATG §8.2）
- 適応規則の判定（P の増減提案のみ。**実行はしない**）（ATG §6.4、Q10=(b)確定）
- 統合順序決定・競合3分類の記録（**実行はしない**）（ATG §8.3、Q10=(b)確定）
- 終端レポート（ATG §13.1の機能受入条件をそのまま出力）

### 1.2 非適用範囲（作らないもの。ImplementationPlan.md §3 N1〜N10 と同一）

| # | 作らないもの | 理由 |
|---|---|---|
| N1 | LLM呼び出し・エージェント起動を行うランナー | 各面に既存の実行基盤がある。ATGの差分は「同時実行してよいかの決定」 |
| N2 | 統合ウェーブの**実行**（実際の`git merge`実行・実際の影響範囲テスト実行） | 決定・分類の判定のみ実装し、実行はエージェント側に委ねる（Q4確定方針と整合） |
| N3 | 書込み集合W(v)の自動推定 | ATG §15-1が「今後の課題」とする。宣言方式で足りる |
| N4 | JSON Schemaファイル・jsonschema依存 | 依存ゼロを維持。検証は各モジュールの明示的なfail-closed検査で行う |
| N5 | GUI/Web UI/サーバ/外部データベース | 指示にない。sqlite1ファイルで足りる |
| N6 | 秘密値スキャナ・独自の資格情報管理 | 契約に秘密を書かせない運用規則（Skill側）で足りる |
| N7 | 失敗クラスの自動判定 | 呼び出し側が明示的に渡す。推定はATG §9.2に反する |
| N8 | 設定オプション・環境変数・プラグイン機構・Strategy/Factory等の抽象化 | 単発用途。必要になるまで作らない |
| N9 | PyPI/外部レジストリへの公開 | 要求定義FR-KIT-06が対象外と規定済み |
| N10 | 既存共有実装（`tools/skills/_kit/`・`tools/for-other-repo/_bootstrap/`）への変更 | ATGは依存ゼロ・設定なし・索引なしのため、それらの提供価値が無い（T0-3確定） |

---

## 2. CLIコマンド仕様（14 実行形式）

**実装言語**: Python 3.11+、標準ライブラリのみ（Q6確定）。
**エントリポイント**: `python -m atg <subcommand> [args]`。

| # | コマンド | 引数 | 標準出力（JSON、1行） | 終了コード |
|---|---|---|---|---|
| 1 | `atg init` | `[--dir <path>] [--force]`（既定は状態ディレクトリの解決順に従う。下記参照） | `{"created": ["<path>", ...]}` | 0=成功／1=既に存在（`--force`なし） |
| 2 | `atg contract check <path>` | 契約JSONのパス | `{"valid": true, "sha256": "<64hex>"}` または `{"valid": false, "errors": [...]}` | 0=valid／1=invalid |
| 3 | `atg graph check <path>` | グラフJSONのパス | `{"valid": true, "T1": <float>, "Tinf": <float>, "Lmax": <float>, "speedup_bound": <float>}` または `{"valid": false, "errors": [...]}` | 0=valid／1=invalid |
| 4 | `atg run begin --contract <path> --graph <path> [--state <path>]` | 契約・グラフ・状態DBパス（既定は状態ディレクトリの解決順 + `state.sqlite3`。下記参照） | `{"execution_id": "<uuid>", "head": "<sha or 'unknown'>"}` | 0=成功／1=契約またはグラフが無効／2=HEAD取得不能 |
| 5 | `atg node next --graph <path> [--execution-id <id>] [--limit N] [--state <path>]` | グラフJSONのパス・（任意）実行ID・上限数（既定 無制限） | `{"runnable": ["<node-id>", ...]}` | 0=常に成功（空配列も正常） |
| 6 | `atg node start <id> --execution-id <id> [--state <path>]` | ノードID・実行ID | `{"started": true, "attempt": <int>}` | 0=成功／1=起動不可（既に実行中の重複起動、または依存未充足・書込み競合で runnable でない） |
| 7 | `atg node finish <id> --handoff <path> --execution-id <id> [--state <path>]` | ノードID・handoff.jsonパス・実行ID | `{"finished": true}` または `{"finished": false, "errors": [...]}` | 0=成功／1=handoff検証失敗 |
| 8 | `atg node fail <id> --class <transient\|implementation\|contract> --execution-id <id> [--max-attempts N] [--state <path>]` | ノードID・失敗クラス・実行ID | `{"retry": true, "attempt": <int>}` または `{"retry": false, "reason": "max_attempts_exceeded"\|"contract_violation"}` | 0=判定成功（retry可否は出力内） |
| 9 | `atg node check-writes <id> --base <rev> --graph <path> [--repo-dir <path>]` | ノードID・比較対象git rev・グラフパス・検査対象の作業ツリー（既定 `git rev-parse --show-toplevel`。git管理外なら `.`。**作業ツリーのルートであること**） | `{"ok": true}` または `{"ok": false, "violations": ["<path>", ...]}` | 0=ok／1=違反あり、または `--repo-dir` が作業ツリーのルートでない |
| 10 | `atg parallel advise --ready-count N --rate-limited <bool> --memory-pressure <bool> --test-queue-len N [--merge-wave-active <bool>]` | ATG§6.4の4観測値と、統合ウェーブ実行中かどうか（既定 `False`） | `{"action": "increase\|halve\|hold", "reason": "<text>"}` | 0=常に成功 |
| 11 | `atg merge plan --node-ids <id> [<id> ...]` | 当該ウェーブの完了ノードID列（**互いに依存しないノード集合に限る**） | `{"order": ["<node-id>", ...]}`（ID昇順） | 0=成功 |
| 12 | `atg merge classify --merge-exit N --verify-exit N [--writeset-violation <bool>]` | 各種exit code | `{"class": "mechanical\|semantic\|contract_violation\|none"}` | 0=常に成功 |
| 13 | `atg run finish --acceptance-exit <N> --evidence <path> [--execution-id <id>] [--state <path>]` | 受入コマンドのexit code・証跡パス・（任意）実行ID | `{"accepted": <bool>}` | 0=グラフの全ノードが終端かつacceptance exitが0／1=未終端・証跡不在・acceptance exit非0 |
| 14 | `atg report --execution-id <id> [--state <path>]` | 実行ID・状態DBパス | Markdown文字列（stdout） | 0=成功／1=状態DBまたは実行IDが不正 |

**状態ディレクトリ（`.atg`）の既定解決順**（`--dir` / `--state` を明示しない場合。cli.py `_resolve_state_dir`）:

1. 環境変数 `ATG_STATE_DIR`
2. カレントディレクトリに既存の `.atg` があればそれ（既存導入先との後方互換。データの自動移行はしない）
3. `git rev-parse --show-toplevel` のルート直下 `.atg`（キット配下やリポジトリのサブディレクトリで実行しても、状態がリポジトリルートへ集約される）
4. カレントディレクトリ直下 `.atg`（git 管理外のフォールバック）

`--repo-dir`（`node check-writes` のみ）は明示指定が無ければ同じく `git rev-parse --show-toplevel` に解決し、無ければ `.` にフォールバックする。

**注記**: 表には 14 の実行形式がある。これは `run begin` / `run finish` と `merge plan` / `merge classify` を別の実行形式として数えるためである。以降の実装・Skill・受入は本表の 14 形式を正本とし、いずれも省略しない。

**`atg node next` の除外規則**: `--execution-id` を与えた場合、`runnable` からは耐久状態が終端のノード（`succeeded` / `failed` / `blocked`）と実行中のノード（`running`）を除外する。**終端失敗ノードを返してはならない**。返すと、呼び出し側の `node next` → `node start`（exit 1）→ `node next` ループが耐久状態を変えないまま同じ集合を受け取り続け、停止条件に到達しない。`--execution-id` を与えない場合は耐久状態を参照しないため、依存充足と書込み競合だけで判定する。

### 2.1 共通のfail-closed規則

- 不正なJSON、必須キー欠落、型不一致は例外を発生させず、`{"valid": false, "errors": [...]}` 形式で終了コード非0を返す（コマンドがvalidityを主題とする場合）。
- 上記は `atg.cli.main` の一括捕捉（`InputError` / `ValueError` / `sqlite3.Error`）で保証する。生のトレースバックを利用者へ出さない。
- 未知のサブコマンド・引数は`argparse`の標準動作（終了コード2、stderrにusage）に従う。
- すべてのコマンドはUTF-8・LFで出力する。

### 2.2 本版で意図的に採用しない仕様（YAGNI 境界）

| 項目 | 現仕様 | 理由 |
|---|---|---|
| `atg init` の契約テンプレート出力 | ディレクトリ作成のみ | テンプレートの内容は対象リポジトリごとに異なり、汎用テンプレートは誤用を招くため本版では提供しない |
| `contract.parallelism_hint` の自動連動 | 検証のみ。`graph check` の P はノード総数を用いる | 契約と上界計算の結合は本版の適用範囲外。上界は「その形状で達しうる最大」を示す指標として用いる |
| `merge classify` の結果永続化 | 分類結果を返すのみ | ATG §8.3 の判定だけを提供し、記録・実行は呼び出し側に委ねる（N2） |

---

## 3. データ形式（3種、すべてJSON）

### 3.1 ルート契約 `atg-contract.json`

```json
{
  "objective": "<1文>",
  "non_goals": ["<文字列>", ...],
  "deliverables": ["<相対パス>", ...],
  "acceptance_command": "<正確なコマンド文字列>",
  "baseline_policy": "<文字列>",
  "time_budget": "<文字列>",
  "credit_budget": "<文字列（任意）>",
  "execution_envelope": {
    "code_changes": "allowed_within_write_set",
    "dependency_additions": "declared_only",
    "external_network_writes": "deny",
    "production_deploy": "deny",
    "secret_read": "declared_refs_only"
  },
  "secret_policy": "<文字列>",
  "parallelism_hint": <int, 任意>
}
```

**必須キー**: `objective`, `non_goals`, `deliverables`, `acceptance_command`, `baseline_policy`, `time_budget`, `execution_envelope`, `secret_policy`。
**任意キー**: `parallelism_hint`, `credit_budget`。

> **ATG 技術文書からの逸脱**: ATG §8.1 の表は `credit_budget` を必須としているが、本キットは CLI 自身が課金を伴う処理を一切行わないため任意キーへ緩和している。費用上限を運用する場合は契約に記載し、実行主体側で強制すること（ATG Kit は強制しない）。

**fail-closed規則**: 必須キー欠落・未知キー・`acceptance_command`が空文字列・重複キー（JSON解析前の生テキストで検出）は`errors`に列挙して拒否する。重複キー検査は `atg.cli` の契約読み込み経路（`_load_contract`）で強制する。

**SHA-256の算出**: キーをソートし、`separators=(",", ":")` でcompact化し、UTF-8でエンコードした正準形（末尾に LF を1つ付与する）に対してSHA-256を取る。キー順・空白の違いによらず同一内容は同一ハッシュになる。

### 3.2 タスクグラフ `atg-graph.json`

```json
{
  "nodes": [
    {
      "id": "<一意な文字列>",
      "deps": ["<node-id>", ...],
      "duration": <float>,
      "write_set": ["<相対パス or リソース識別子>", ...],
      "contract": {
        "inputs": ["<相対パス>", ...],
        "outputs": ["<相対パス>", ...],
        "verify_command": "<文字列>",
        "timeout_seconds": <int>,
        "max_attempts": <int>,
        "allowed_tools": ["<文字列>", ...]
      }
    }
  ]
}
```

**fail-closed規則**:
- `id`重複、`deps`の参照先が存在しない、循環依存、`write_set`未宣言、`contract.outputs`に列挙されたパスが自身の`write_set`に含まれない場合は拒否する。
- `contract.allowed_tools` は**非空**の文字列配列でなければならない。空配列 `[]` は拒否する（使用可能なツールを宣言しないノードは実行封筒を定義できないため）。
- `contract` 直下に未知のキーがある場合は拒否する（`contract has unknown key(s): ...`）。`inputs` / `outputs` / `verify_command` / `timeout_seconds` / `max_attempts` / `allowed_tools` 以外は受け付けない。

### 3.3 引継ぎ `handoff.json`（ATG §8.4のJSONスキーマをそのまま採用）

```json
{
  "schema_version": 1,
  "execution_id": "<id>",
  "node_id": "<id>",
  "attempt": 1,
  "root_contract_sha256": "<sha256>",
  "input_artifacts": [{"path": "<相対パス>", "sha256": "<sha256>"}],
  "decisions": [{"id": "D-001", "statement": "<決定内容>", "source_refs": ["<path or url>"]}],
  "outputs": [{"path": "<相対パス>", "sha256": "<sha256>"}],
  "verification": {"command": "<正確なコマンド文字列>", "exit_code": 0, "evidence_path": "<相対パス>"},
  "risks": [],
  "blocked_reasons": [],
  "next_nodes": ["<node-id>"]
}
```

**fail-closed規則**:
- `decisions[].source_refs`が空配列の要素が1件でもあれば拒否する。
- `outputs[].sha256`が実ファイルのSHA-256と一致しない、または実ファイルが存在しない場合は拒否する。
- `outputs[].path` と `verification.evidence_path` は handoff ファイルが置かれたディレクトリを基準に解決する。ノード専用 worktree で実行する場合、handoff は当該 worktree 内（通常は worktree ルート）へ置く。
- `verification.exit_code`が整数でない（bool含む）場合は拒否する。
- `verification.command` が当該ノードの `contract.verify_command` と**完全一致**しない場合は拒否する（別のコマンドの成功をノードの検証結果として持ち込ませないため）。
- `execution_id` / `node_id` / `attempt` / `root_contract_sha256` が実行中のノードの値と一致しない場合は拒否する（handoff を実行・ノード・試行・契約へ束縛する）。
- 出力ファイルが存在するだけでは成功にしない（ATG §9.5「成功の形をした降格を禁止する」）: `verification.exit_code == 0` かつ全`outputs`のハッシュ一致の両方を要求する。

---

## 4. 耐久状態のスキーマ（sqlite3）

ファイル: `<状態ディレクトリ>/state.sqlite3`（`--state`で変更可。状態ディレクトリの解決順は§2の表直後を参照）

```sql
CREATE TABLE executions (
    execution_id TEXT PRIMARY KEY,
    contract_sha256 TEXT NOT NULL,
    head_commit TEXT NOT NULL,
    started_at TEXT NOT NULL,
    finished_at TEXT,
    accepted INTEGER
);

CREATE TABLE nodes (
    execution_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    status TEXT NOT NULL CHECK(status IN ('pending','running','succeeded','failed','blocked')),
    attempt_count INTEGER NOT NULL DEFAULT 0,
    state_version INTEGER NOT NULL DEFAULT 0,
    lease_token TEXT,
    PRIMARY KEY (execution_id, node_id)
);

CREATE TABLE attempts (
    execution_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    attempt_number INTEGER NOT NULL,
    outcome TEXT CHECK(outcome IN ('running','succeeded','failed')),
    failure_class TEXT CHECK(failure_class IN ('transient','implementation','contract') OR failure_class IS NULL),
    started_at TEXT NOT NULL,
    finished_at TEXT,
    PRIMARY KEY (execution_id, node_id, attempt_number)
);

CREATE TABLE artifacts (
    execution_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    path TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    PRIMARY KEY (execution_id, node_id, path)
);
```

**原子性の規則**: すべての状態遷移（`node start`/`node finish`/`node fail`）は単一トランザクション内で行う。`node start`は`status`が`pending`の場合のみ`running`へ遷移でき、既に`running`/`succeeded`/`failed`/`blocked`なら拒否する（重複起動拒否）。`succeeded`/`failed`/`blocked`は終端状態であり、再度`start`できない（再開時の重複実行防止）。失敗確定したノードの推移的後続は同じトランザクションで`blocked`へ遷移する。独立ノードは変更しない。

**`lease_token`の範囲（YAGNI境界の明記）**: `lease_token`は`node start`時に一意値（例: uuid4）を発行し重複起動検出にのみ使う。**時間経過によるリース失効・タイムアウト監視は v0.1 では実装しない**（N8）。プロセスが異常終了した場合の扱いは、再開時に耐久状態を読み直して`running`のまま停止しているノードを検出し、利用者/Skillが`node fail --class transient`で明示的に失敗確定させる運用とする（自動失効タイマーは追加しない）。なお `lease` は分散システムの文献では期限付き排他を意味するが、本実装の `lease_token` は期限・更新・リソース側検査のいずれも持たない**実行識別子**である。

---

## 5. 実行規則R1〜R5の実装対応表

| ATGの規則 | 内容 | モジュール | 関数 |
|---|---|---|---|
| R1 準備完了 | 依存ノードがすべて`succeeded`、かつ自身が `running` / `succeeded` / `failed` / `blocked` のいずれでもない | `schedule.py` | `is_ready(node, succeeded_ids, active_ids, terminal_ids)` |
| R2 相互排他 | 実行中ノードと`write_set`が交わらない | `schedule.py` | `is_conflict_free(node, running_nodes)` |
| R3 並列上限 | `--limit`件数を超えない | `schedule.py` | `next_runnable(..., limit)` |
| R4 優先度 | 残余臨界路長降順、同値はID昇順 | `schedule.py`, `bounds.py`（`bottom_level`計算） | `next_runnable(...)` |
| R5 有界リワーク | `attempt_count < max_attempts`なら再試行 | `state.py` | `record_failure(node_id, failure_class)` |

---

## 6. 失敗クラスと有界リワーク

| 失敗クラス | 意味 | v0.1の扱い |
|---|---|---|
| `transient` | 一時的障害（レート制限・タイムアウト等） | 再試行対象。`attempt_count < max_attempts`なら`retry: true` |
| `implementation` | 実装の誤り（ノード検証失敗） | 再試行対象。同上 |
| `contract` | 契約違反（宣言外書込み等） | **再試行しない**。即座に失敗確定（ATG §8.3の「契約違反」分類と整合） |

呼び出し側（Skill/エージェント）が`--class`で明示する。CLIは分類を推測しない（ATG §9.2）。

**`reason` の使い分け**: `retry: false` のとき、`contract` クラスは `"contract_violation"`、`transient` / `implementation` で試行回数が上限に達した場合は `"max_attempts_exceeded"` を返す。`contract` は試行回数に関係なく再試行しないため、上限超過と同じ理由を返してはならない（呼び出し側が「あと数回試せば通る」と誤解する）。

**backoff の責務**: `transient` の再試行前に待つかどうか、どれだけ待つかは**呼び出し側の責務**である。ATG CLI は待機処理を持たず、`retry` の真偽値と `attempt` を返すだけである。

---

## 7. ATG技術文書との対応（実装/非実装）

| ATGの節 | 内容 | v0.1の扱い | 理由 |
|---|---|---|---|
| §6.1 | タスクグラフ | 実装 | JSON宣言 |
| §6.2 R1-R4 | 準備完了・相互排他・上限・優先度 | 実装（判定のみ） | 起動は呼び出し側 |
| §6.2 R5 | 有界リワーク | 実装 | クラスは呼び出し側が明示 |
| §6.3 | 実効並列度 | 部分実装（`--limit`と競合なし集合のみ） | provider/machineの観測は非実装（N8: 過剰な観測機構を追加しない） |
| §6.4 | 適応規則 | 部分実装（決定・提示のみ） | 実行はしない（Q10=(b)確定、N2） |
| §7.1-7.5 | 命題1〜4 | 実装 | 実行前に算出・報告 |
| §8.1 | ルート契約 | 実装 | SHA-256・fail-closed |
| §8.2 | 隔離 | 手順として実装（検査はCLI） | git worktreeは素のコマンドを使う（Q7確定） |
| §8.3 | 統合ウェーブ | 部分実装（決定・分類のみ） | 実行はしない（Q10=(b)確定、N2） |
| §8.4 | 三層メモリー・handoff | 実装 | — |
| §8.5 | 階層検証 | ノード層・終端層を実装 | 統合層・CI層は利用者側 |
| §9.1 | 実行アルゴリズム | Skill手順として実装 | — |
| §9.2 | 質問しないための決定表 | Skill手順として実装 | — |
| §9.3 | 実行封筒 | Skill手順+契約項目 | — |
| §9.4 | 冪等性 | 契約項目としてのみ | 機構は非実装（exactly-oneを主張しない） |
| §9.5 | 障害耐性と再開 | 実装 | — |
| §13.1 | 機能受入条件 | レポートの受入条件表として実装 | — |
| §15 | 今後の課題 | 非実装 | N3 |
