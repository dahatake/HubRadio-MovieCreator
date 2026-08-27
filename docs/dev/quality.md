# 品質管理

この文書は、現在の Stage 0 検証と、後続の製品実装で守る品質管理の入口です。測定していない品質や、未実装のテストを完了済みとは扱いません。

## 現在の品質境界

製品アプリケーションの自動テスト suite と CI workflow はまだありません。`.NET`、React、Python worker の製品テストと一括 build は、Stage 0 承認後の F-005／F-006 で追加する計画です。

現在実装済みなのは、Stage 0 の検証ツールと証拠に対する次の仕組みです。

- PowerShell 7 parser と、対象に応じた Python compile／import 検査
- benchmark envelope の JSON Schema 検証
- fixture の実行前後 size、更新日時、SHA-256 検査
- FFmpeg／ffprobe、model、主要 runtime artifact の version／hash 記録
- timeout、取消時の子孫 process 終了、process dispose
- staging から最終成果物への原子的昇格
- output／manifest pair の失敗時 rollback
- ffprobe profile 検査と `-xerror` 全編 decode
- 想定外の0件や失敗を解釈するための positive control
- raw run とレビュー済み canonical summary の分離
- 未測定値、単回観測、未承認閾値の明示

詳細は [Stage 0 ベンチマーク](benchmarks/README.md)を参照してください。

## 品質の判断原則

### 測定事実と判断を分ける

- 実行時間、件数、hash、status は raw 証拠から機械照合します。
- 採用、保留、手動化は canonical summary の判断として分離します。
- 0件は対象が存在しない証拠にも、検出器が正しい証拠にもなりません。
- JSON Schema 適合は、文章の正しさや意味の根拠を保証しません。
- 同一環境の byte 一致を、異なる PC／GPU／driver の再現性へ一般化しません。

### 人のフォールバックを維持する

精度や利用条件が未確認の機能は、値を推測して有効化しません。手動話者割当、手動 topic metadata、手動レイアウト、字幕調整、手動試聴など、案件を完了できる人の経路を残します。

### 失敗証拠を消さない

- 過去の raw run は書き換えません。
- 問題レポートは既存内容を消さず追記します。
- 修正前後の run ID と SHA-256 を canonical やレビューへ残します。
- 未完成の staging は成功成果物として表示しません。

## 敵対的レビュー

各タスクの成果物を、次の5軸で独立に確認します。

1. **目的適合性:** 要求、task、利用者の目的を満たすか
2. **内容の妥当性:** 値、時刻、型、例、処理結果が内部整合しているか
3. **整合性:** 要求、ADR、実装、schema、証拠の間に矛盾がないか
4. **根拠性・不確実性管理:** 実測、推測、未測定、外部資料の範囲を区別しているか
5. **品質・運用性:** 失敗、取消、復旧、秘匿、再実行、配布を安全に扱えるか

レビューでは、実ファイル・実行結果・一次資料で確認できる問題だけを採用します。推測による指摘は反映しません。修正後に同じ観点を再確認し、未解決の重大問題がないことを確認してから次の依存タスクへ進みます。

既存の記録:

- [実現方式調査の敵対的レビュー](reviews/adversarial-review.md)
- [実装進行成果物の敵対的レビュー](reviews/implementation-adversarial-review.md)

## 変更ごとの最小確認

### 文書

- 現在、計画、条件付きの表現を分ける
- 正本の優先順位と相対リンクを確認する
- パス変更時は、参照元と ADR-0001 の hash を同期する
- 未実装の UI、運用、性能値を作らない
- SVG は XML、アクセシビリティ、表示範囲を確認する

### PowerShell／Python の Stage 0 ツール

- parser／compile と対象環境での import
- 正常、失敗、timeout の machine-readable 結果
- source 不変、path／内容秘匿、孤児 process なし
- schema の過去 run 互換
- 0件／失敗を判定する control

### 後続の製品コード

製品コードの確認項目は、対応する実装計画タスクと受入基準に従います。テスト framework、architecture test、accessibility、backup／restore、MSIX は、実装前に空の仕組みだけを作りません。

## 完了条件

タスクを完了とする前に、次を満たします。

- 対象要求と依存 task が特定されている
- 変更したファイルと検証結果が一致している
- 正常系だけでなく、その task に実在する失敗経路を確認した
- 内容、秘密、PII、raw metadata を不要に保存していない
- 測定不能・未承認の項目を明記した
- 敵対的レビューの実在指摘を修正し、再検証した

全体の gate と依存関係は [詳細実装計画](planning/implementation-plan.md) を参照してください。
