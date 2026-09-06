# 品質管理

この文書は、Stage 0、既存製品基盤、要求版0.2の後続実装で守る品質管理の入口です。測定していない品質、未実装のテスト、実施していない人手評価を完了済みとは扱いません。

## 現在の品質境界

`.NET`、React、Python worker、PowerShellの自動testと一括buildは実装済みです。2026-09-02の既存実行記録は[アクセシビリティ評価記録](accessibility/test-report.md)等に残します。要求版0.2は`V2-000`〜`V2-009`を実装・レビュー済みで、source-only quick start、設定store/UI、可逆な自動decision、標準Composition計画、社内限定BGM policyまで到達しています。自動代理評価、単一利用者state、実render／delivery、最終受入は未実装です。

既存実装と検証証拠には次の仕組みがあります。

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
- .NET/Web/Pythonのfocused test、component回帰、全回帰
- keyboard interaction、focus CSS、contrast token、非色依存semanticの自動contract
- media、bridge、worker、process、path、render境界の合成敵対試験

詳細は[Stage 0ベンチマーク](benchmarks/README.md)、[実装進行成果物の敵対的レビュー](reviews/implementation-adversarial-review.md)、[アクセシビリティ評価記録](accessibility/test-report.md)を参照してください。過去のPASSは、そのrunが対象にしたsurfaceだけの証拠である。

## 1操作起動候補の品質gate

`candidate-review-06`はbounded local technical evaluationの候補です。公開候補の構成・SBOM・検証サマリーと、BuildLogs／ComponentInputsを含むprivate原本は公開candidateの兄弟ディレクトリへ分離して保全します。これはfilesystem ACLによる保護境界ではありません。公開サマリーはprivate原本の代替でも、候補全体のrelease authorizationでもありません。

- 本PCの正常系では、同梱Fixed WebView2 `152.0.4191.62`、cold/warmのnative Ready、通常終了、対象子孫processの終了、実行中の展開物と候補manifestの照合・不変性を確認した。この結果は当該PC・当該候補・正常系の観測に限定する。
- 実行入力とSDK展開cacheはrunごとの専用領域で追跡し、WebView2 user dataはportable実装の固定per-user pathを使用するためrun単位の分離を主張しない。workspace／案件DBとprivate証跡は削除せず、backup・hash照合・対象process限定で既存データへ影響しないことをrunごとに確認する。入力やprivate原本を通常の共有品質証拠へ混在させない。
- production-channel clean OSと標準ユーザーでの通常・異常系、offline、欠落・破損・容量不足・アクセス拒否、JavaScript受領ACKの独立確認、署名・MOTW・実配布はPASSではない。clean OS受入は現在BLOCKEDであり、保護を解除して結果を置き換えない。
- Fixed Runtimeやapp-local native依存の更新は、更新対象の版・内容・供給網条件を再確認し、候補、SBOM、証拠、起動受入を再作成・再照合する。既存候補のPASSを更新後のbytesへ遡及しない。

`releaseApproved=false`を維持する。license、notice、完全なSBOM／Model BOM、CVE、再配布条件、法的判断、clean OS、署名、実配布、業務E2Eがopen gateである間、一般提供の品質gateは成立しない。候補の供給網判断は[release-candidate review](licenses/release-review.md)、本PCでの観測値と未評価範囲は[OSL-014記録](reviews/osl-014-local-host-verification.md)を参照してください。

## 品質の判断原則

### 測定事実と判断を分ける

- 実行時間、件数、hash、status は raw 証拠から機械照合します。
- 採用、保留、手動化は canonical summary の判断として分離します。
- 0件は対象が存在しない証拠にも、検出器が正しい証拠にもなりません。
- JSON Schema 適合は、文章の正しさや意味の根拠を保証しません。
- 同一環境の byte 一致を、異なる PC／GPU／driver の再現性へ一般化しません。

### 任意確認と手動フォールバックを維持する

精度や利用条件が未確認の機能は、値を推測して有効化しません。手動話者割当、手動topic metadata、手動layout、字幕調整、手動試聴など、利用者が任意に修正できる経路を残します。これらを社内限定quick startの必須承認gateにせず、安全なfallbackで自動処理を完了できる設計にします。

### 自動代理評価の境界を明示する

- codec、全編decode、A/V同期、字幕、safe zone、loudness、True Peak、来歴、keyboard/focus/contrast等、closedな機械判定だけを自動代理評価へ含めます。
- 結果は`PASS`、`WARNING`、`FAIL`、`NOT EVALUATED`、規則版、実行環境、digestを記録します。
- `Automated proxy: PASS`を`HUMAN EVALUATION: PASS`、知覚品質保証、理解度保証、法的適合と呼びません。
- 既存の人手17項目`PENDING`は履歴として維持し、自動PASSへ書き換えません。

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
- [要求版0.2・既定設定・再baseline敵対的レビュー](reviews/v2-defaults-adversarial-review.md)
- [V2-001版付き既定設定domain/schema敵対的レビュー](reviews/v2-001-default-settings-profile-adversarial-review.md)
- [V2-008標準Composition計画敵対的再レビュー](reviews/v2-008-standard-composition-adversarial-rereview.md)

## 変更ごとの最小確認

### 文書

- 現在、計画、条件付きの表現を分ける
- 正本の優先順位と相対リンクを確認する
- 外部数値を仕様、公式推奨、査読知見、広告資料、実務知見、製品判断に分類する
- URL、取得日、表示版・更新日、適用scope、直接支えるclaimを記録する
- 広告仕様をorganic/general仕様へ読み替えない
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
- 自動代理評価と人手評価を明確に区別した
- 敵対的レビューの実在指摘を修正し、再検証した

全体の gate と依存関係は [詳細実装計画](planning/implementation-plan.md) を参照してください。
