# ADR-0001: 要求・設計文書の正本

- 状態: Accepted
- 決定日: 2026-08-27
- 対象: HubRadio-MovieCreator
- タスク: P-001

## 決定

実装判断の正本3文書と、そこから派生する実行計画を次の優先順位で固定する。

1. `docs/requirement-definition.md` — 何を実現するか、優先順位、受入基準
2. `docs/adversarial-review.md` — 調査書に対する確認済み問題と修正条件
3. `docs/decisions/ADR-*.md` — 要求を変えない範囲で採用した具体的判断。後のAccepted ADRが同じ論点の古いADRを置き換える
4. `docs/implementation-research.md` — 実現方式、候補技術、PoC条件。Accepted ADRと競合する候補記述はADRを優先する
5. `docs/implementation-plan.md` — 上記文書から派生する実行順序、成果物、依存関係

上位文書と下位文書が矛盾する場合は上位を採用する。実測前の数値、未確認のモデル能力、未承認の外部サービス条件は正本とみなさない。

## 実在確認

| 文書 | 版・状態 | SHA-256（2026-08-27時点） |
|---|---|---|
| `docs/requirement-definition.md` | 0.1 Draft | `f28c24f9c5f72859ebb71503a6242703ec5225b329e3aa3a7face56fd73e60a9` |
| `docs/implementation-research.md` | 調査日 2026-08-26、Windows後続決定注記を反映 | `35cca8d661f8837b176e75a18a67a16113b34d9531b03307dc226f243f1b5c16` |
| `docs/adversarial-review.md` | 修正後 PASS | `b0af2715b70dd3c322c6f4d293b8e6ed4c06f0bbcc7e02d1541fd9804b25a8a4` |
| `docs/implementation-plan.md` | 計画日 2026-08-26、実行中 | `4dcdf1cd64a8fae83bfefcc91e83029c11bf87711a94fa54a1754d8484cce572` |

指定されていた `/hve-dev/requirement-definition.md` はリポジトリに存在しない。したがって、存在しないパスを推測して作成したり、別内容の要求定義として扱ったりしない。

## 変更規則

- 文書のSHA-256は変更検知用であり、内容変更を禁止する署名ではない。
- 上記文書が更新された場合、実装を続行する前に要求トレーサビリティと影響タスクを再評価する。
- 要求定義がDraftであるため、未決事項はデフォルト判断として別ADRへ記録し、事実として断定しない。
- 実装結果と実測値は正本を書き換えず、`benchmarks/results/`、受入証拠、後続ADRへ保存する。

## 結果

P-001の後続タスクは、この優先順位と実在する3文書を入力として実行する。
