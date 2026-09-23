# 計画 → タスクグラフ変換規則（`atg-graph.json` / `atg-contract.json`）

> 本ファイルは、フェーズ 1（[planning.md](planning.md)）で確定した計画を、ATG CLI が検証できるタスクグラフとルート契約へ落とすための規則を定義する。
> 変換後の実行手順は [execution-loop.md](execution-loop.md)、契約各項目の記述規約は [contract-authoring.md](contract-authoring.md) を参照する。

## 0. この変換を行う条件 / 行わない条件

**行う条件（すべて満たすとき）**

1. 計画文書の `## 分割判定` が確定している。
2. 実行を ATG の判定（起動可否・書込み競合・有界リトライ・終端受入）で制御したい。
3. 各ノードの**変更候補パス**と **exit code で判定できる検証コマンド**が確定している。

**行わない条件（いずれかに当てはまるとき）**

- 分割判定そのものをやり直したい場合。分割判定は [planning.md](planning.md) §3 が正本であり、タスクグラフ側では判定しない。
- 変更候補パスが未確定な場合。ATG は `write_set` を**自動推定しない**。推測で埋めない。
- 完了条件が自然言語のままで、exit code による判定へ落ちていない場合。

> 変換後も計画文書は計画の正本として残す。タスクグラフは計画の**写像**であり、置き換えではない。

---

## 1. フィールド対応表（`atg-graph.json`）

| ATG 側 | 計画側の出どころ | 変換規則と制約 |
|---|---|---|
| `nodes[].id` | 計画の DAG ステップ ID（`S1` 等） | 非空文字列。グラフ内で一意。**連番の数値をそのまま `id` にしない**（`deps` の解決で取り違えやすい）。`node-1` のように接頭辞を付けて文字列化する |
| `nodes[].deps` | 計画の DAG 依存 | **番号ではなく、対応ノードの `id` 文字列へ変換する**。番号のまま入れると `unknown dependency` で拒否される（§3 E1）。依存なしは `[]` |
| `nodes[].duration` | 計画の見積（R+P+I+V+F の分） | **正の数値のみ**。`0`・未記入・文字列は拒否される。上界計算にのみ使われ、合否判定には使われない |
| `nodes[].write_set` | 各ノードの「変更候補パス」 | **非空配列**。重複不可。絶対パス・`..` を含むパス・バックスラッシュは不可（Windows でも `src/a.py` 形式で書く）。ディレクトリ単位で宣言してよい（`docs/report/` は配下を覆う） |
| `nodes[].contract.inputs` | 各ノードの「参照元」 | 相対パスの配列。空配列可 |
| `nodes[].contract.outputs` | 各ノードの「成果物」 | 相対パスの配列。**すべて自ノードの `write_set` に覆われていること**（§3 E2） |
| `nodes[].contract.verify_command` | 各ノードの `## 完了条件` | **exit 0 で判定できるコマンド文字列 1 本**へ変換する。チェックボックスの自然言語をそのまま入れない。検証が複数ある場合は `&&` で 1 本にまとめるか、ノードを分ける |
| `nodes[].contract.timeout_seconds` | 見積から決める | 1 以上の整数 |
| `nodes[].contract.max_attempts` | 再試行方針 | 0 以上の整数。`python -m atg node fail` の有界リトライ判定に使われる |
| `nodes[].contract.allowed_tools` | 実行封筒（ルート契約 `execution_envelope`）と整合させる | **非空**の文字列配列 |

### 1.1 ルート契約（`atg-contract.json`）の対応

| ルート契約の項目 | 計画側の出どころ |
|---|---|
| `objective` | 計画の「概要（目的）」 |
| `non_goals` | 計画の「非対象」 |
| `deliverables` | 全ノードの `contract.outputs` の和集合（レビュー対象になるもの） |
| `acceptance_command` | `## 完了条件` のうち、**全体の合否**を 1 コマンドで判定するもの |
| `baseline_policy` | 計画の「既存の失敗の扱い」。未記載なら「既存失敗は許容し、新しい失敗を増やさない」を明示的に決めてから書く |
| `time_budget` | 見積合計に基づく hard limit |
| `execution_envelope` / `secret_policy` | 計画側に対応項目はない。[contract-authoring.md](contract-authoring.md) §1.7 / §1.8 に従って新規に定める |

> 各項目の記述規約は [contract-authoring.md](contract-authoring.md) が正本。本ファイルは**どこから値を持ってくるか**だけを定義する。

---

## 2. 変換手順

1. 計画の DAG から、ノード集合と依存関係を書き出し、`node-N` 形式の `id` を割り当てる。
2. 各ノードの「変更候補パス」を `write_set` にする。**同じロックファイル・同じ移行系列のような共有資源は、人工的に分割せず同一キーで宣言する**（分割すると並列可能と誤判定される）。詳細は [contract-authoring.md](contract-authoring.md) §2。
3. 各ノードの `## 完了条件` から、exit code で判定できるコマンドを 1 本選び `verify_command` にする。該当コマンドが存在しない場合は、**変換を止めて完了条件を先に確定する**。
4. `contract.outputs` が自ノードの `write_set` に覆われているか確認する。
5. `python -m atg graph check <path>` と `python -m atg contract check <path>` を実行し、**両方 exit 0** を確認する。非 0 のまま先へ進まない。
6. 実行は [execution-loop.md](execution-loop.md) の手順に従う。

### 2.1 変換例（3 ノード）

- `node-1`: パーサ実装（依存なし）
- `node-2`: フォーマッタ実装（依存なし）
- `node-3`: 結合（`node-1` / `node-2` へ依存）

```json
{
  "nodes": [
    {
      "id": "node-1",
      "deps": [],
      "duration": 45,
      "write_set": ["src/parser.py", "tests/test_parser.py"],
      "contract": {
        "inputs": ["docs/report-export.md"],
        "outputs": ["src/parser.py", "tests/test_parser.py"],
        "verify_command": "pytest tests/test_parser.py -q",
        "timeout_seconds": 1800,
        "max_attempts": 2,
        "allowed_tools": ["read", "write", "run_tests"]
      }
    },
    {
      "id": "node-2",
      "deps": [],
      "duration": 40,
      "write_set": ["src/formatter.py", "tests/test_formatter.py"],
      "contract": {
        "inputs": ["docs/report-export.md"],
        "outputs": ["src/formatter.py", "tests/test_formatter.py"],
        "verify_command": "pytest tests/test_formatter.py -q",
        "timeout_seconds": 1800,
        "max_attempts": 2,
        "allowed_tools": ["read", "write", "run_tests"]
      }
    },
    {
      "id": "node-3",
      "deps": ["node-1", "node-2"],
      "duration": 30,
      "write_set": ["src/main.py", "docs/report-export.md"],
      "contract": {
        "inputs": ["src/parser.py", "src/formatter.py"],
        "outputs": ["src/main.py", "docs/report-export.md"],
        "verify_command": "pytest tests/ -q",
        "timeout_seconds": 1800,
        "max_attempts": 2,
        "allowed_tools": ["read", "write", "run_tests"]
      }
    }
  ]
}
```

この例を `python -m atg graph check` に通すと `{"valid": true, "T1": 115.0, "Tinf": 75.0, "Lmax": 45.0, "speedup_bound": 1.5333333333333334}` が返る（実測）。

### 2.2 共有資源がある場合（並列にならない例）

見かけ上は別作業でも、同じロックファイルを更新する 2 ノードは `write_set` を同一キーで宣言する。ATG はこれを競合として扱い、同時には起動可能集合へ入れない。

```json
{
  "nodes": [
    {
      "id": "add-http-client",
      "deps": [],
      "duration": 20,
      "write_set": ["dependencies.lock"],
      "contract": {
        "inputs": [],
        "outputs": ["dependencies.lock"],
        "verify_command": "pytest tests/test_dependencies.py -q",
        "timeout_seconds": 600,
        "max_attempts": 1,
        "allowed_tools": ["read", "write"]
      }
    },
    {
      "id": "add-csv-library",
      "deps": [],
      "duration": 20,
      "write_set": ["dependencies.lock"],
      "contract": {
        "inputs": [],
        "outputs": ["dependencies.lock"],
        "verify_command": "pytest tests/test_dependencies.py -q",
        "timeout_seconds": 600,
        "max_attempts": 1,
        "allowed_tools": ["read", "write"]
      }
    }
  ]
}
```

`dependencies.lock#part-a` のような人工的な分割をしてはいけない。分割すると ATG は競合なしと判定し、実際には壊れる並列実行を許してしまう。この例は `{"valid": true, "T1": 40.0, "Tinf": 20.0, "Lmax": 40.0, "speedup_bound": 1.0}` を返す（実測）。共有資源へ全作業が集中しているため、**並列度をいくら上げても加速しない**ことがグラフ検証の時点で数値として出る。

---

## 3. 拒否される典型的な誤変換（実測）

いずれも `python -m atg graph check` が **exit 1** で拒否する。メッセージは実行結果の原文。

| ID | 誤り | 実際のエラーメッセージ |
|---|---|---|
| E1 | 依存を番号のまま `deps` に入れた（`"deps": [1, 2]`） | `node node-3: unknown dependency: 1` / `node node-3: unknown dependency: 2`（依存ごとに 1 行） |
| E2 | `contract.outputs` が `write_set` に含まれていない | `node node-1: output 'tests/test_parser.py' is not covered by write_set` |
| E3 | `contract` に独自キーを足した（例: `notes`） | `node node-1: contract has unknown key(s): notes` |
| E4 | `duration` を `0` にした / 省略した | `node node-1: duration must be positive` |
| E5 | グラフ直下に `nodes` 以外のキーを足した（例: `task_scope`） | `graph must contain exactly the nodes key` |
| E6 | 完了条件の自然言語をそのまま入れ、`verify_command` を空にした | `node node-1: contract.verify_command must be non-empty` |

> E5 が示すとおり、**計画側のメタデータ（`task_scope` / `context_size` / `split_decision`）をタスクグラフへ持ち込むことはできない**。両者は別ファイルとして併存させる。

---

## 4. この変換で引き継がれないもの

| 計画側の要素 | タスクグラフ側 | 扱い |
|---|---|---|
| `task_scope` / `context_size` / `split_decision` | 対応なし | 計画文書に残す。ATG は分割判定に関与しない |
| リポジトリ固有の運用メタデータ（担当者・ラベル・親タスク番号等） | 対応なし | 計画文書側に残す。ATG は運用系の連携を行わない |
| `## 完了条件` のチェックボックス本文 | `verify_command` / `acceptance_command` へ**要約されない** | 人間可読の完了条件は計画文書に残す。コマンドは別途決める |
| 実行面・実行基盤の選択 | 対応なし | ATG は実行面を判定しない |
