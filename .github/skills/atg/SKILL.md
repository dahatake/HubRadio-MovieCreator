---
name: atg
description: >
  USE FOR: deterministic task-graph planning and execution-decision support,
  including task decomposition into a dependency graph, split decisions
  (PROCEED / SPLIT_REQUIRED) from task scope and context size, effort
  estimation, definition-of-done rules, runnable-node selection, write-set
  conflict checks, blocking rules, bounded retry decisions, execution-loop
  guidance, and deny-by-default execution envelopes. DO NOT USE FOR: launching
  LLM calls or agent executions, running git merges or tests, inferring write
  sets, or any repository-specific workflow orchestration. WHEN: a user needs
  ATG Kit guidance to plan and split work, or to decide what may run in
  parallel, what must block, and how to proceed without guessing.
metadata:
  origin: user
  version: 0.2.2
category: planning
---

# atg

ATG Kit は、タスクをグラフとして扱い、どの作業を並列化できるか、どこで停止すべきか、どの再試行が許されるかを**判定支援**するための Skill である。目的は、人手による逐次判断を減らしつつ、契約・依存関係・書込み競合・検証失敗を fail-closed で扱えるようにすることにある。

ATG Kit 自体は **LLM 呼び出しやエージェント実行そのものを行わない**。行うのは、計画の分割判定、ルート契約の固定、タスクグラフの検証、起動可能ノードの判定、失敗クラスに応じた有界リワーク判定、終端条件の確認であり、実際の生成・編集・実行は外部の実行主体が担う。

## 2 つのフェーズ

| フェーズ | 内容 | 参照 |
|---|---|---|
| フェーズ 1: 計画 | `task_scope` / `context_size` の確定、`PROCEED` / `SPLIT_REQUIRED` の機械的判定、粒度・見積・完了条件の規約 | [references/planning.md](references/planning.md) |
| フェーズ 2: 実行判定 | 契約の固定、グラフ検証、起動可能ノードの判定、有界リトライ、終端受入 | [references/execution-loop.md](references/execution-loop.md) |

フェーズ 1 で確定した計画をフェーズ 2 のタスクグラフ・ルート契約へ落とす変換規則は [references/plan-to-graph.md](references/plan-to-graph.md) に定義する。
分割判定は**エージェントの裁量を認めない**。`task_scope=multi` または `context_size=large` は例外なく `SPLIT_REQUIRED` である。
完了条件が exit code で判定できない場合は、フェーズ 2 へ進まずに完了条件の確定を先に行う。

## 対応面

ATG Kit は次の **ローカル 3 面**で使うことを想定する。

- **VS Code の GitHub Copilot**
- **GitHub Copilot CLI**
- **GitHub Copilot app**

いずれの面でも、ATG Kit の役割は共通であり、実行可否・並列性・停止条件を判断するための薄い境界として振る舞う。

他リポジトリへ配布したキットでは、`tools/atg/atg.ps1` / `./tools/atg/atg.sh` が
`python -m atg` と同じ CLI の別名として使える（同梱の `vendor/atg` を自動で
`PYTHONPATH` へ通す）。`python -m atg` を直接呼ぶ場合は、キットの `vendor/` フォルダを
`PYTHONPATH` へ設定する（手順はキット同梱の `GETTING-STARTED.md` を参照）。

## 実行ループの概要

ATG Kit の実行ループは、まずルート契約を固定して前提条件を一括検査し、次にタスクグラフを検証して上界を計算し、耐久状態へ実行を登録する。その後、依存が満たされたノードから書込み競合のないものだけを起動可能集合として選び、完了結果・成果物ハッシュ・検証証跡を反映しながら進める。統合や差戻しは検証結果に基づいて有界に扱い、最後は受入コマンドの結果を含む単一の終端レポートで閉じる。

詳細は [references/execution-loop.md](references/execution-loop.md) を参照。

## 質問しないための決定表

「強制主体」列は、その動作を **ATG CLI が機械的に強制するか**、それとも **実行主体（エージェント/人）が守るべき規約か**を示す。ATG Kit は LLM 呼び出しもエージェント実行も行わないため、実行主体の責務に分類された行は ATG が保証しない。

| 状況 | 自動動作 | 強制主体 | 根拠 |
|---|---|---|---|
| 任意項目が未指定 | 契約スキーマの既定値を適用 | 実行主体 | 決定的に定まる |
| 必須項目が未指定だが一意に解決可能 | 出典付きで解決し契約へ固定 | 実行主体 | 推測ではない |
| 必須項目の候補が複数 | **当該ノードをブロック**。推測しない | 実行主体 | 推測は捏造と同義 |
| ノード検証が失敗 | implementation クラスのリトライ方針 | CLI が判定（クラスは呼び出し側が明示） | 構造化された失敗として扱う |
| 提供側のタイムアウト・レート制限 | backoff 後に有限回リトライ | リトライ可否は CLI。**backoff の待機は実行主体** | transient |
| 書込み競合 | 該当キーを直列化し統合層で解消 | **CLI が強制** | R2 |
| 資格情報・権限が不足 | **ブロック**。秘密値を生成・保存しない | 実行主体 | 安全境界 |
| 実行封筒外の課金・デプロイ・破壊操作 | **実行しない**。終端レポートへ記載 | 実行主体 | 実行封筒 |
| 終端の受入コマンドが失敗 | 成功にしない。証跡付きで失敗 | **CLI が強制** | fail-closed |

**候補が複数のときはブロックして推測しない。**  
**資格情報不足はブロック**し、秘密値を生成・保存しない。

## ATG が強制するもの / 強制しないもの

この区別を誤ると、「ATG を使っているから安全」という誤った安心につながる。

| ATG CLI が機械的に強制する | 根拠コマンド |
|---|---|
| 依存未充足・書込み競合・終端状態のノードを起動させない | `python -m atg node next` / `python -m atg node start` |
| 同一ノードの重複起動を拒否する | `python -m atg node start` |
| 出典（`decisions[].source_refs`）が空の引継ぎを拒否する | `python -m atg node finish` |
| 成果物ハッシュ不一致・検証 exit 非 0 の引継ぎを拒否する | `python -m atg node finish` |
| 引継ぎを実行 ID・ノード ID・試行回数・契約 SHA-256 へ束縛する | `python -m atg node finish` |
| 契約クラスの失敗を再試行させない／上限超過で再試行を止める | `python -m atg node fail` |
| 宣言外書込みを検出する（検出範囲の限界は `docs/LIMITS.md` §2） | `python -m atg node check-writes` |
| 未終端ノードが残る、または受入コマンドが失敗した実行を受理しない | `python -m atg run finish` |

| ATG CLI が**強制しない**（実行主体の責務） | 理由 |
|---|---|
| 資格情報・権限の不足の検出とブロック | 秘密値スキャナ・資格情報管理は作らない（`docs/LIMITS.md` §4 N6） |
| 実行封筒（本番デプロイ・課金・削除・権限変更）の拒否 | 封筒は契約 JSON の**宣言値**であり、CLI は課金・デプロイを一切実行しないため強制機構を持たない |
| リトライ前の backoff・待機 | CLI は `retry` の真偽値を返すだけで待機処理を持たない |
| 失敗クラスの判定 | 呼び出し側が `--class` で明示する。CLI は推測しない（`docs/spec.md` §6） |
| write set の推定 | 宣言方式のみ（`docs/LIMITS.md` §4 N3） |
| ノードの実際の実行・並列プロセス起動・`git merge` の実行 | 実行基盤は外部（`docs/LIMITS.md` §4 N1 / N2） |

## 実行封筒（deny-by-default）

実行封筒は、開始時に明示された操作だけを許可する deny-by-default の境界である。**これは契約へ宣言する運用規約であり、ATG CLI が実行時に強制するものではない**（上表を参照）。既定では次を守る。

| 区分 | 既定 | 備考 |
|---|---|---|
| コード変更 | 許可 | 宣言された write set の範囲内に限る |
| 依存追加 | 条件付き | 契約で明示された場合のみ |
| 外部ネットワーク書込み | 既定拒否 | 明示契約がない限り実行しない |
| 本番デプロイ・課金・削除・権限変更 | **既定拒否** | 契約で明示されない限り実行しない |
| 秘密値の読み取り | 条件付き | 明示された参照のみを扱う |

とくに **本番デプロイ・課金・削除・権限変更は既定拒否**であり、契約に明示されない限り実行しない。

## コマンド一覧

詳細な入出力・終了コードは `docs/spec.md` を参照。

| コマンド | 1行説明 |
|---|---|
| `python -m atg init` | 状態ディレクトリを初期化する。 |
| `python -m atg contract check` | ルート契約 JSON を検証し、正準 SHA-256 を返す。 |
| `python -m atg graph check` | タスクグラフ JSON を検証し、上界指標を返す。 |
| `python -m atg run begin / python -m atg run finish` | 実行の開始登録と最終受入判定を行う。 |
| `python -m atg node next` | 現在の状態から起動可能ノードを列挙する（終端状態のノードは含めない）。 |
| `python -m atg node start` | ノード開始を耐久状態へ記録する。重複起動と、runnable でないノードの起動を拒否する。 |
| `python -m atg node finish` | handoff を検証してノード完了を確定する。 |
| `python -m atg node fail` | 失敗クラスに基づいて再試行可否を判定する。`contract` は `contract_violation`、上限超過は `max_attempts_exceeded` を返す（待機は行わない）。 |
| `python -m atg node check-writes` | 宣言外書込みがないかを検査する（`--repo-dir` で worktree を指定する。検出範囲の限界は `docs/LIMITS.md` §2 を参照）。 |
| `python -m atg parallel advise` | 観測値に基づき並列度の増減提案を返す。 |
| `python -m atg merge plan` | 統合順序を決定する。`--node-ids` には**互いに依存しないノード集合**（`python -m atg node next` が返した集合）だけを渡す。返る順序は ID 昇順であり依存順ではない。 |
| `python -m atg merge classify` | 統合失敗を機械的・意味的・契約違反に分類して返す（永続化はしない）。 |
| `python -m atg report` | 実行状態から終端レポートを生成する。終端被覆は `failed` / `blocked` も数えるため成功率ではない。 |

## 関連資料

- [references/planning.md](references/planning.md)
- [references/plan-to-graph.md](references/plan-to-graph.md)
- [references/execution-loop.md](references/execution-loop.md)
- [references/contract-authoring.md](references/contract-authoring.md)
