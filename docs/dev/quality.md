# 品質管理

この文書は、Stage 0、既存製品基盤、要求版0.2の後続実装で守る品質管理の入口です。測定していない品質、未実装のテスト、実施していない人手評価を完了済みとは扱いません。

## 現在の品質境界

**今回対象の52単位はすべて完了**。UI/UXの54単位から利用者指定の`UX-041/043`を除いた範囲です。最終静的検査、runtime出力コピー照合、UX-048最終独立レビュー、記録確定まで完了し、未解決の重大指摘はありません。対象52単位の完了条件成立を記録した後、package `0.1.1.0`と`CHANGELOG.md`のUnreleased追記を反映済みです。除外項目をPASSへ変更せず、一般提供の承認とも区別します。

実warning、job、版、成果物、評価、保存・再open、明示再生成・retryの製品経路は接続済みです。9月8日のA17／E契約未受領／noJobという状態は履歴であり、現在状態には適用しません。限定的なlegacy経路の理由付き`Unavailable`は許容された能力境界として残し、未実装の承認や永続化を捏造しません。2026-09-02・07・08の結果と未実施の人手評価は以下と[アクセシビリティ評価記録](accessibility/test-report.md)に保全します。

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

## 2026-09-09 機能・全回帰の確認（現在）

以下はmain側で確認済みの最新runの記録であり、この文書更新で再実行した結果ではない。現行の全回帰はshard・runner別の実行であり、標準`test.ps1`を一括実行した記録ではない。総計は**6926成功・35 skip・失敗0**。focused再実行や別実行の実統合PASSを加算しない。

| runner | 成功 | skip | 失敗 | 確認済み原本／run ID |
|---|---:|---:|---:|---|
| App（全UID、ALL 2410） | 2402 | 8 | 0 | `app-shards-20260909T150051Z-0e9f12c6` summary |
| Infrastructure | 1337 | 25 | 0 | `observed-test-20260909T150315642Z-9705b12c` |
| Core | 731 | 1 | 0 | `observed-test-20260909T152906585Z-b2c5f617` |
| Architecture（PRI hook修正後） | 209 | 0 | 0 | `observed-test-20260909T163146676Z-0543b2b4` |
| Web Vitest（84 files） | 2066 | 0 | 0 | `web-all-20260909T134816937Z-b185d9e1` |
| accessibility／assets | 6／6 | 0 | 0 | main側の専用検証確認記録 |
| Python worker | 169 | 1 | 0 | `worker-all-fixed-20260909T111238857Z.log` |

Appの8 skipは**環境条件4件＋opt-in実統合4件**。後者4件は下記の別runでPASSしており、8件すべてを環境skipと記載せず、Appの2402成功へ足さない。Core最新runとArchitectureはログ読取まで確認済み。

| 別実行の実統合 | 結果 | 確認済みrun ID |
|---|---|---|
| 実ローカルASR→生成・QC→全7成果物保存→再open（39ファイルの空白修正・token同値確認後） | PASS（1件） | `observed-e2e-20260909T163411105Z-97f44d5a` |
| 明示再生成 | PASS | `observed-e2e-20260909T145632533Z-ba46a2bd` |
| retry | PASS | `observed-e2e-20260909T145852256Z-bac8df13` |
| 既知の合成Machine cueを手動字幕編集→保存・再読込→実FFmpeg／QC | PASS | `observed-test-20260909T145059046Z-3a086056` |

E2E入力は4秒の合成媒体であり、`V2-014`でもsample／提供実MOVは使用していない。実ASRの実行成功はASR精度の受入ではなく、既知Machine cueの字幕試験も実ASR由来の字幕とはしない。性能、知覚品質、人手評価のPASSを含めない。旧QC失敗と前回initial E2E `observed-e2e-20260909T144834883Z-41b8cdcc`は履歴として残し、最新runへ重複加算しない。

最終.NET format（`20260909T163633`）、PowerShell parser 35ファイル、`pip check`、`diff check`、Web production buildはPASS。39ファイルのC# token同値は`token-verification.json`で確認済みで、sourceの0-byte該当はなし。Viteの約848KB bundle警告は残り、性能受入のPASSとはしない。

runtime App build（`20260909T162001902Z`）は終了コード0・エラー0・警告0。`artifacts/validation/app-runtime-20260909c`のPRIは3760 bytesで、コピー済みrootを照合済み。通常MRTの`PackagingOutputs`とMSIXのPRI入力について、対象sourceの完全一致による除外を最小試験2/2/4で確認し、すべてPASSした。新出力の確認であり、旧`candidate-review-06`は変更していない。

根本原因6件・修正後証拠は[9月9日レビュー追補](reviews/ui-ux-adversarial-review.md#6-2026-09-09-実統合の修正後レビュー現在)を参照。対象52 ID完全一致・欠落0・重複0と18 raw原本hashは[詳細実装計画§0.8.2](planning/implementation-plan.md#082-対象52単位の完了と実接続の限定検証2026-09-10-jst追補)と`artifacts/validation/final-evidence-hashes-20260909c.json`へ記録済み。UX-048最終独立レビューは閉鎖済み。`UX-044`は未測定、人手17項目は`PENDING`を維持し、一般提供は`releaseApproved=false`のままである。

## 2026-09-08 再開後の最終ローカル検証（履歴）

解析の未送信編集を案件切替guardへ統合し、V2-010/011の評価入力相関、非同期出力認可、履歴不変性、再評価・backup/restoreを修正した。敵対的レビュー指摘の反映後に、最新表示で見つかった320px幅のroot横あふれも修正し、標準Release検証を再実行した。

| runner | 成功 | 環境skip | 失敗 |
|---|---:|---:|---:|
| Architecture | 192 | 0 | 0 |
| Core | 726 | 1 | 0 |
| Infrastructure | 1006 | 25 | 0 |
| App | 1464 | 3 | 0 |
| Web Vitest（75 files） | 1811 | 0 | 0 |
| accessibility／assets | 6／6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

原本`artifacts/validation/uiux-v2-full-20260908-b.log`、SHA-256 `e514dcb9b857a99b967195948ef472c4fc0fd1dcdaf2418e552e94a836ad5998`。format、warn-as-error build（警告0・エラー0）、依存・型・asset検証を含む全runner成功。環境skip29件やfocused再実行を成功数へ重複加算しない。Web/App manifestは共通SHA-256 `d60f1ff6c1fe06477bbfaca88e82f01e01e2b7811a8461c0f5af78331edd834c`で表示再測定対象と一致する。

明暗50画面・123設定・CSS文字32pxの5操作・解析guardを再確認。DPR1.25・visibility hiddenのfixture証拠であり、実native・物理入力・実媒体・人の理解は未評価。前のDPR約1の失敗測定と同条件の因果比較にはしない。[受入追補](../../tests/Acceptance/UIUX_CompactWorkflow.md#再開後の表示再確認2026-09-08)へ原本と制限を記録した。

`AutomatedEvaluationEngine`は供給された観測の集約器で、上のtest件数を実動画の自動評価結果へ転用しない。FS配送とDB receiptは完全に原子的ではなく、callback成功後のreceipt失敗ではreconciliationが必要。本番E契約・実WebView・最終UX-048は未完了で、全54単位完了後のCHANGELOG/PATCH条件も未成立である。

同日時点の範囲は本節の履歴表、指摘・反映・再確認は[UI/UX中間レビュー](reviews/ui-ux-adversarial-review.md)を参照する。以下9月7日の結果もその時点の履歴で、現在bytesの証拠へ読み替えない。

## 2026-09-07 UI/UX枝の検証結果（履歴）

4タブと独立設定、全体／案件scope、未保存保持、strictな`setting.txt`の明示保存・復元を実装し、最終assetで標準Release検証を実行した。実警告・実job開始・実成果物・再生成のE契約は未受領であり、通しの動画編集・納品や全54タスクの完了を意味しない。

| runner | 成功 | 環境skip | 失敗 |
|---|---:|---:|---:|
| Architecture | 192 | 0 | 0 |
| Core | 665 | 1 | 0 |
| Infrastructure | 897 | 25 | 0 |
| App | 1464 | 3 | 0 |
| Web Vitest | 1766 | 0 | 0 |
| accessibility／assets | 5／6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

skip29件はreparse point、所有者変更、非Windows経路、別account、Stage 0実PTS等の環境条件による。成功へ換算しない。`SettingsFileIntegrationTests`10件は上記Appの内数で、一時root/DBの実handler・backup/restoreを検証した。個別実行341件などの件数を全回帰に加算しない。

標準`test.ps1`は依存復元、format、warn-as-errorの.NET build、Web型・dependency/dist検証を含めて終了コード0。最終privateログ`artifacts/validation/uiux-full-verification-recovery-reviewed-20260907.log`のSHA-256は`f9e24d404885af796b5f6f54d344fbcc2198100e7b01247b8fff35d2ed72af65`。これは追加レビューで確認したfile置換競合、部分ACK/失敗後の入力保持、file操作のfocus復帰の修正を含む。前段の1660 Web tests時点のrun、EPERM、書式違反、旧payload fixture、ACL比較で失敗したログは削除せず、修正後runと分ける。Viteの500kB超bundle警告は残り、性能受入のPASSとはしない。

最終Web manifestとApp出力内のmanifestはSHA-256 `f58db99cfb263f0c5bbbc204bebc2c4fc983c86e6e3d8c0692bb88f9b5ead13e`で一致。追加修正後にfixtureの明暗50画面と123設定の表示測定、CSS文字拡大の5操作到達を再実行した。原本`uiux-browser-measurements-339a592a-02dc-44c1-a9e5-bcca93252c6f.json`のSHA-256は`87e7f8c461acb90d3a87a2780c27631831be4b09435e73f67b8e48561ea60394`。詳細は[比較・受入手順](../../tests/Acceptance/UIUX_CompactWorkflow.md#追加レビュー修正後の再確認2026-09-07)を参照する。実WebView2、物理keyboard、人の理解、実媒体は未評価。

file試験175件中171成功・4環境skip、Web recovery276成功のfocused試験は上記全回帰の内数である。file runnerに最低成功172を指定した試行のexit9は171成功との差によるもので、skipを成功へ足さず最低171で再実行してexit0を確認した。これらの試行も別記録として残す。

### 同梱noticeの実payload照合

既存候補を上書きせず`artifacts/portable/uiux-payload-current`へ新規の依存payloadを生成した。manifest自身を除く3260 file／1,819,389,846 bytes、集合SHA-256 `d3bbb21bec58cde1bf0a9691d397fbd42908624dc70ae85fd658e4a2d8117044`。`Licenses/THIRD-PARTY-NOTICES-NONFINAL.md`は現在のnoticeとSHA-256 `de211e7dfb2cc37b232bea502feff008b6d040594331bc00e97e18c5c3082946`で一致し、payload manifestにも結合される。

payload manifest SHA-256は`cd3073de0db4ad5f46b8bb146a28819077b68c968d9d1dea7dfd604fcb5e458a`。`appPublishRequired=true`、`releaseApproved=false`を維持する。**これは依存payloadであり、新しい単一EXE・署名・完全SBOM・一般提供候補を作成した証拠ではない。** `candidate-review-06`は旧証拠のまま保全している。

全54単位の完了には至っていないため、利用者が全完了後に指定したCHANGELOG Unreleased追記とpackage PATCH増分はまだ実施しない。この時点の実施範囲と保留は本節および[UI/UX中間レビュー](reviews/ui-ux-adversarial-review.md)の履歴として保全する。

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
- [UI/UX改善・V2中核の中間レビュー](reviews/ui-ux-adversarial-review.md)

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
