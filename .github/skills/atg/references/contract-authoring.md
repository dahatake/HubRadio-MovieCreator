# ルート契約と `write_set` の記述指針

`atg-contract.json` は、ATG 実行 1 回分の目的、境界、受入条件、予算を固定するルート契約である。`atg-graph.json` を作る前に定義し、機械的に検証できる形で書く。

## 1. ルート契約 `atg-contract.json` の必須項目

以下の 8 項目はすべて必須である。どれか 1 つでも欠けると契約は成立しない。

### 1.1 `objective`

- 書き方: 達成したい結果を 1 文で書く。作業手順ではなく、完了時の状態を表す。
- 記入例:

```json
"objective": "月次レポートを CSV で出力できる CLI コマンドを追加する"
```

### 1.2 `non_goals`

- 書き方: 今回やらないことを配列で列挙する。スコープ外を先に固定し、作業の膨張を防ぐ。
- 記入例:

```json
"non_goals": ["UI の全面刷新は行わない", "本番データの移行は行わない"]
```

### 1.3 `deliverables`

- 書き方: 期待成果物の相対パスを列挙する。レビューや引継ぎの対象になるファイルを具体的に書く。
- 記入例:

```json
"deliverables": ["src/report_export.py", "tests/test_report_export.py", "docs/report-export.md"]
```

### 1.4 `acceptance_command`

- 書き方: **exit 0 で判定できる、正確なコマンド文字列を 1 つだけ書く。** 自然言語の説明ではなく、機械がそのまま実行できるコマンドを記述する。
- 良い例: `pytest tests/ -q`
- 悪い例: `テストが通ること`
- 記入例:

```json
"acceptance_command": "pytest tests/ -q"
```

`acceptance_command` は「合格条件の説明」ではなく「合格を判定する実行コマンド」である。`動作確認する` や `問題なければ完了` のような自然言語は不可である。

### 1.5 `baseline_policy`

- 書き方: 着手時点で既に存在する失敗や既知課題をどう扱うかを明示する。典型は「既存失敗は許容するが、新しい失敗は増やさない」である。
- 記入例:

```json
"baseline_policy": "既存の失敗は許容するが、この変更で新しい失敗を増やさない"
```

### 1.6 `time_budget`

- 書き方: 時間上限を単位付きで書く。曖昧な目安ではなく、停止判定に使える表現にする。
- 記入例:

```json
"time_budget": "90m hard limit"
```

### 1.7 `execution_envelope`

- 書き方: 許可する変更と禁止する外部副作用をオブジェクトで固定する。少なくとも、コード変更、依存追加、外部ネットワーク書込み、本番デプロイ、秘密値読取りを明示する。
- 記入例:

```json
"execution_envelope": {
  "code_changes": "allowed_within_write_set",
  "dependency_additions": "declared_only",
  "external_network_writes": "deny",
  "production_deploy": "deny",
  "secret_read": "declared_refs_only"
}
```

### 1.8 `secret_policy`

- 書き方: 秘密値をどこへ保存してはいけないか、必要時に何を記録してよいかを明示する。秘密そのものではなく、参照名だけを扱う方針にする。
- 記入例:

```json
"secret_policy": "秘密値は契約・引継ぎ・ログ・成果物へ保存せず、必要時は参照名のみを記録する"
```

## 2. `write_set` の分割指針

`write_set` は各ノードが書き込む相対パスまたは資源識別子の集合である。`contract.outputs` に列挙した出力は、同じノードの `write_set` に必ず含める。

ATG の実務上の帰結は単純である。**単一の共有資源は並列化を殺す。** 全ノードが同じ資源を書くなら、その共有資源が直列化の原因になり、並列度を上げても速度向上は得られない。したがって、`write_set` は「同時に安全に書ける単位」で分け、逆に同じ共有資源を触る作業は同じ書込みキーとして扱う。

### 2.1 分割できる例

2 つの独立したモジュールファイルを別々に変更するなら、`write_set` も別々にできる。

```json
[
  {
    "id": "update-parser",
    "write_set": ["src/parser.py"]
  },
  {
    "id": "update-formatter",
    "write_set": ["src/formatter.py"]
  }
]
```

この 2 ノードは `src/parser.py` と `src/formatter.py` という別資源に書くため、依存関係がなければ並列実行候補にできる。

### 2.2 分割できない例

見かけ上は別作業でも、同じ共有資源を書くなら分割してはいけない。そういう場合は、同じ書込みキーとして宣言し、直列化される前提で扱う。

#### ロックファイルの例

```json
[
  {
    "id": "add-http-client",
    "write_set": ["dependencies.lock"]
  },
  {
    "id": "add-csv-library",
    "write_set": ["dependencies.lock"]
  }
]
```

2 つのノードが別の依存を追加していても、最終的に同じ `dependencies.lock` を更新するなら共有資源は 1 つである。`dependencies.lock#part-a` のように人工的に分けてはいけない。

#### マイグレーション対象の例

```json
[
  {
    "id": "add-customer-columns",
    "write_set": ["database/migration-stream"]
  },
  {
    "id": "add-invoice-index",
    "write_set": ["database/migration-stream"]
  }
]
```

それぞれ別ファイルのマイグレーションを生成する実装であっても、適用順序が 1 本の系列に束縛されるなら共有資源は同じである。こういうときは個別ファイル名より、`database/migration-stream` のような論理資源識別子で 1 つの書込みキーとして扱う。

## 3. まとめ

- ルート契約では、必須 8 項目をすべて機械可読に書く。
- `acceptance_command` は自然言語ではなく、**exit 0 で合否判定できる正確なコマンド文字列**にする。
- `write_set` は「同時に安全に書ける資源単位」で分ける。
- 同じロックファイルや同じマイグレーション系列のような共有資源は、1 つの書込みキーとして宣言し、並列化できない前提で設計する。
