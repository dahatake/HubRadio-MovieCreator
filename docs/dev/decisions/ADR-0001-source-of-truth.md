# ADR-0001: 要求・設計文書の正本

- 状態: Accepted
- 決定日: 2026-08-27
- 対象: HubRadio-MovieCreator
- タスク: P-001

## 決定

実装判断の正本と、そこから派生する実行計画を次の優先順位で固定する。

1. `docs/requirement-definition.md` — 何を実現するか、優先順位、受入基準
2. `docs/dev/reviews/adversarial-review.md` — 調査書に対する確認済み問題と修正条件
3. `docs/dev/decisions/ADR-*.md` — 要求を変えない範囲で採用した具体的判断。後のAccepted ADRが同じ論点の古いADRを置き換える
4. `docs/dev/research/default-settings-research.md` — 数値、既定値、外部資料のscopeと採否。要求・Accepted ADRと競合する場合は上位を優先する
5. `docs/dev/research/implementation-research.md` — 実現方式、候補技術、PoC条件。Accepted ADRと競合する候補記述はADRを優先する
6. `docs/dev/planning/implementation-plan.md` — 上記文書から派生する実行順序、成果物、依存関係

上位文書と下位文書が矛盾する場合は上位を採用する。実測前の数値、未確認のモデル能力、未承認の外部サービス条件は正本とみなさない。

## 初期決定時の実在確認（履歴）

次のhashは2026-08-27から2026-08-31までの各記録時点に対する履歴であり、現在fileのhashではない。

| 文書 | 版・状態 | SHA-256（各記録時点） |
|---|---|---|
| `docs/requirement-definition.md` | 0.1 Draft | `f28c24f9c5f72859ebb71503a6242703ec5225b329e3aa3a7face56fd73e60a9` |
| `docs/dev/research/implementation-research.md` | 調査日 2026-08-26、Windows後続決定注記を反映 | `8ef0efdf1b4fccd9f144ec52dcc0b1bef2f8fd3335ea6c4bad1343ae2fb89c64` |
| `docs/dev/reviews/adversarial-review.md` | 修正後 PASS | `09a85e34ea015fef2719075223aa89d45d3723d61ce38399dc182eea94166c71` |
| `docs/dev/planning/implementation-plan.md` | 計画日 2026-08-26、2026-08-31時点124/203完了・A-001〜A-009/A-011/A-014/A-018〜A-021/S-007/S-014/Z-001〜Z-007/PK-001〜PK-005完了・PK-006同一host実装済み/clean Windows受入待ち・M-011 durable restart/M-012媒体敵対試験完了 | `90b398c6bb80f529664905b88a74e310523b0865f2c0b5e2fdcef62982d5d726` |

## 2026-09-02の再baseline

- 要求正本は版0.2 Draftへ更新した。
- [ADR-0009](ADR-0009-source-only-defaults-and-automated-evaluation.md)が、単一利用者、source-only quick start、版付き既定値、自動代理評価と、旧ADRの部分置換範囲を定める。
- [`default-settings-research.md`](../research/default-settings-research.md)が、数値と外部資料scopeの台帳である。
- `implementation-plan.md`の§0が現在のtask分類とDAGであり、§1以降は旧要求版0.1の履歴snapshotである。
- 旧`124/203`、`139/203`、`160/203`を現在の完了数として使わない。

文書横断・出典・敵対的レビュー後のSHA-256は次のとおり。これらは`V2-000`完了時点の内容検知値であり、署名または将来変更の禁止ではない。

| 文書 | 版・状態 | SHA-256（V2-000完了時点） |
|---|---|---|
| `docs/requirement-definition.md` | 0.2 Draft、production実装停止中 | `c6f5afecff5b7bd6fecc95f4ed462e1e47747e03f419475968f1d764330bb3c5` |
| `docs/dev/research/default-settings-research.md` | 調査日2026-09-02、R01〜R26監査済み | `4c07f8531fc40534bd5e0a4abd6a43cb450780cf16515afa34c17da95b798d92` |
| `docs/dev/decisions/ADR-0009-source-only-defaults-and-automated-evaluation.md` | Accepted | `8447aa37bb829c6ca2811dfdf4dea8324700197e93b1b402b0bf98dfdb710b2e` |
| `docs/dev/planning/implementation-plan.md` | V2-000完了、V2-001以降停止 | `def0085015c7f37e797a1b3cfcd9a44a4f04dfd3bf11c5d4c368c9a655db7069` |
| `docs/dev/reviews/v2-defaults-adversarial-review.md` | PASS（文書baselineのみ） | `201ae085cb585a4dfe39551c06f82bbd88e6655c5a48ebe07482b09d71199f2c` |

上表は`V2-000`完了時点の履歴である。その後、2026-09-02の利用者指示によりproduction実装を再開し、現在の実行状態は[`implementation-plan.md` §0](../planning/implementation-plan.md#0-要求版02の現在baseline)とtaskごとの敵対的レビューで管理する。

指定されていた `/hve-dev/requirement-definition.md` はリポジトリに存在しない。したがって、存在しないパスを推測して作成したり、別内容の要求定義として扱ったりしない。

## 変更規則

- 文書のSHA-256は変更検知用であり、内容変更を禁止する署名ではない。
- 上記文書が更新された場合、実装を続行する前に要求トレーサビリティと影響タスクを再評価する。
- 要求定義がDraftであるため、未決事項はデフォルト判断として別ADRへ記録し、事実として断定しない。
- 実装結果と実測値は正本を書き換えず、`benchmarks/results/`、受入証拠、後続ADRへ保存する。
- 外部資料の仕様、推奨、査読知見、広告文脈、実務知見、製品判断を区別し、取得日とscopeを記録する。
- 自動代理評価をhuman evaluation、外部公開承認、法的適合と表記しない。

## 結果

後続taskは、この優先順位、要求版0.2、ADR-0009、現在計画§0を入力として実行する。production実装は利用者指示により再開済みであり、各taskの依存、試験、独立した敵対的レビューを満たして順に進める。
