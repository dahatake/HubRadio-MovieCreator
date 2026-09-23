# 残タスク実行状況（2026-09-09追補）

> [!NOTE]
> **実素材に関する現在の事実（2026-09-23追補）。** 本書の2026-09-09節にある「`V2-014`でもsample／提供実MOVは使用していない」という記述は、**2026-09-09時点の観測として保全**する。現在の事実は異なる。2026-09-22に承認済み受入素材 `sample/Copilot  あなたの毎日の AI コンパニオン.mp4` を使うopt-in受入シナリオを**実行しており**、2026-09-23の再実行では長尺1本・サムネイル6枚・付帯成果物2種が実際に生成された。ただし**`V2-014` は合格していない**（自動代理評価の `loudness` / `True Peak` が `FAIL` で納品に到達していない）。なお2026-09-23に受入素材は `sample/hubradio-sample.mp4` へ変更されており、上記の実行結果は変更前の素材に対するものである。新素材でのpipeline実行は未実施であり、素材の測定値は[受入記録 §4](../../../benchmarks/results/initial-pilot.md)を参照すること。以下の日付付き記録は改変していない。

## UI/UX枝の現在状態（2026-09-09）

**今回対象の52単位はすべて完了。UX-048最終独立レビュー閉鎖済み、未解決の重大指摘なし**。

- **範囲:** 利用者指定で`UX-041/043`を除く52単位。除外した実WebView／native GUI受入をPASSへ変更せず、今回のblockerにも戻さない。一般提供承認は別境界。
- **機能完了:** 実nativeのwarning往復、job開始、版・成果物・評価・全7成果物保存／再open、明示再生成・retry、手動字幕の永続化・再生成への投影まで接続・検証済み。旧A17／E未接続／noJob／QC失敗は修正前の履歴であり、現在状態ではない。
- **限定能力:** 手動layout等の未永続化と旧承認済みthumbnail経路の理由付き`Unavailable`は、契約上許容された限定legacy境界として維持する。自動thumbnail生成まで未接続とせず、架空の承認や追加taskで埋めない。
- **最新App全UID:** `app-shards-20260909T150051Z-0e9f12c6` summary、**ALL 2410＝2402成功＋8 skip、失敗0**。8 skipは**環境条件4件＋opt-in実統合4件**であり、後者は別実行PASS。focused件数も別実行も2402へ加算しない。
- **他の全回帰:** Infrastructure1337成功／25 skip、Core731成功／1 skip、PRI hook修正後Architecture209成功（`observed-test-20260909T163146676Z-0543b2b4`）、Web84 files／2066成功、accessibility6／assets6成功、Worker169成功／1 skip、すべて失敗0。全runner総計は**6926成功・35 skip・失敗0**。原本は[品質記録](../quality.md#2026-09-09-機能全回帰の確認現在)を参照。現行結果はshard・runner別実行であり、標準`test.ps1`の一括実行とはしない。
- **実統合4件:** 実ASR・全7成果物保存／再openは39ファイルの空白修正・token同値確認後の`observed-e2e-20260909T163411105Z-97f44d5a`で1件PASS。再生成 `observed-e2e-20260909T145632533Z-ba46a2bd`、retry `observed-e2e-20260909T145852256Z-bac8df13`、既知の合成Machine cueの手動字幕編集・実FFmpeg／QC `observed-test-20260909T145059046Z-3a086056`も各PASS。前回initial `observed-e2e-20260909T144834883Z-41b8cdcc`は履歴として保全し、4件の別実行も全回帰へ加算しない。
- **評価の制限:** E2Eは4秒の合成媒体で、`V2-014`でもsample／提供実MOVは使用していない。性能、ASR精度、人手・知覚品質の受入ではない。字幕試験の既知Machine cueを実ASR由来と称しない。`UX-044`は未測定、人手17項目は`PENDING`を維持する。

| 最終処理 | 現在状態 |
|---|---|
| PowerShell parser 35ファイル／pip check／diff check | PASS |
| Web production build | PASS、Vite約848KB bundle警告は残存 |
| C# format／source健全性 | 最終.NET format（`20260909T163633`）PASS。39ファイルのC# token同値は`token-verification.json`で確認済み、sourceの0-byte該当なし |
| runtime App build／資源照合 | `20260909T162001902Z`は終了コード0・エラー0・警告0。`artifacts/validation/app-runtime-20260909c`のPRI 3760 bytesとコピー済みroot照合PASS |
| MRT／MSIX PRI | 通常MRTは`GetPriOutputs`ではなく`PackagingOutputs`を対象に修正。対象source完全一致の除外を最小試験2/2/4で確認、すべてPASS |
| UX-048／記録確定 | 最終独立レビュー閉鎖済み、未解決の重大指摘なし。対象52 ID完全一致・欠落0・重複0・18 raw原本hashを機械照合済み |
| CHANGELOG／package PATCH | 対象52単位の完了条件成立を記録した後、Unreleased追記とpackage `0.1.1.0`を反映済み |

根本原因6件と修正後証拠は[9月9日レビュー追補](ui-ux-adversarial-review.md#6-2026-09-09-実統合の修正後レビュー現在)、確定済みの52単位は[詳細実装計画§0.8.2](../planning/implementation-plan.md#082-対象52単位の完了と実接続の限定検証2026-09-10-jst追補)と[品質記録](../quality.md#2026-09-09-機能全回帰の確認現在)を参照。最終hash記録（20260909c）も存在する。本追補はmain側の確認済み結果の同期で、再実行ではない。旧`candidate-review-06`は変更せず、`releaseApproved=false`と旧OSL-028等の歴史的リリース残件を維持する。下記9月8日および9月6日の結果は日付付き履歴として保全し、現在のUI/UX対象範囲と混同しない。

### PATCH後のFINAL完了記録

対象52単位の完了条件成立後、package版定義のみを`0.1.1.0`へ更新し、builder／verifier／manifest／`MsixPackagingProjectTests`の値を完全同期した。以下はmain側の確認済み結果の同期であり、本DOC更新での再実行ではない。

- **Architecture focused subset:** package版定義のみの更新後に**11成功・失敗0・skip0**。原本`artifacts/validation/observed-test-20260909T165857057Z-ab0c40ef.stdout.log`。全回帰の追加ではなく、総計**6926成功・35 skip・失敗0**へ加算しない。
- **最終build:** 成功、**警告0・エラー0**。原本`artifacts/validation/observed-build-20260909T165512021Z-97a04735.stdout.log`。
- **最終format:** **PASSED**。`artifacts/validation/final-static-format-20260909T165629710Z.report.json`は`[]`。
- **最終静的source検査（2026-09-09 16:59:06 UTC、main観測）:** PowerShell parser **35ファイルPASS**、`pip check`／`diff check` **PASS**。

**対象52単位（利用者指定の`UX-041/043`を除外）はすべて完了、UX-048最終独立レビュー閉鎖済み**。除外項目、人手17項目の`PENDING`、`UX-044`未測定、`releaseApproved=false`と9月8日以前の履歴は変更しない。

## UI/UX枝の状態（2026-09-08・履歴）

- **今回実施:** 解析の未送信入力を保護する編集統合、V2-010/011のdomain・保存・出力認可境界の修正、敵対的レビュー反映、320px横あふれ修正、最終全回帰とfixture表示再測定、品質・利用者文書・実行台帳の同期。
- **検証:** 全runner成功、失敗0。Web1811、.NET3388、accessibility6、assets6、worker104成功、環境skip29は別扱い。明暗50画面・123設定・文字200%の5control・解析guardを再確認。実native/物理入力/実media成功を含めない。
- **依存待ち:** UX-039Bの実warning往復、UX-040A〜DのE-START/E-EVALUATION/E-RESULT/E-REGENERATE、実社内thumbnail policy。集約器・storeが存在しても本番producer・delivery統合完了とはしない。既定A17を維持する。
- **環境待ち:** UX-041/043の実WebView・新EXEのnative Ready。現CLIは一時app root/DBを指定できず、実利用者profileを使うsmokeは実行していない。利用者profileを接続しない使い捨てWindows環境等が必要。
- **最終処理待ち:** UX-048は中間審査まで。全54単位完了後のCHANGELOG Unreleased/PATCH条件は未成立で、版は未変更。

詳細と原本hashは[UI/UX中間レビュー](ui-ux-adversarial-review.md)、[品質記録](../quality.md)を参照する。以下は2026-09-06のOSL記録を保全したもの。旧候補06のPASSを今回の新UIへ遡及しない。

## 完了（成果物・本PC正常系、2026-09-06 OSL履歴）

- **OSL-013**: `candidate-review-06` を独立検証。公開rootは `App/Contents/Evidence/Sbom` のみ、Contents 3,794件、SBOM SPDX 2.2、payload relation、単一EXE bundle header、hashを確認。PowerShell終了コードの残留を`$?`で分離してPASSを確定。
- **OSL-014 本PC正常系**: 利用者の明示指示で現在のPC・非昇格利用者環境にて再実行。cold Ready 44.023秒、warm 5.525秒、両方exit 0・強制終了なし。同梱WebView2、実展開3,793件のhash一致・不変、対象プロセス残留0、backup保持、最終試行前後のDB hash一致を確認。詳細は[実行記録](osl-014-local-host-verification.md)。clean OS、JS受領ACKの独立確認、異常系再実行はこのPASSに含めない。
- **OSL-015**: candidate-review-06で実行。native imports、Worker health、Worker protocol、FFmpeg、ffprobe、CPU/int8 model load、候補hash不変がPASS。ASR精度とclean OSはNOT EVALUATED。
## 実装・文書完了（実行／承認待ち、2026-09-06 OSL履歴）

- **OSL-016**: `tests/Reliability/portable-launch-recovery.md` を作成。6ケース（再展開、欠落/破損、容量不足、アクセス拒否、startup failure、正常再起動）。手順自体は未実行。
- **OSL-017**: `docs/dev/licenses/release-review.md` と通知インベントリを作成・更新。技術評価候補として分類し、一般再配布・法務・CVE承認は未成立。
- **OSL-019**: candidate-review-06を技術評価候補として固定。EXE hash `159161e3426c566cac6ea43bf6faa6587a2c1e192af81339341f83aa340a092e`、Contents digest `505aa9a4bfd64da2a3925401a0b36e37b5c63fbace6c9b66664aff903cdd091b`、release manifest hash `1d44ef54dc805227f0438a76816a7de044dcd8ab3da29353ee4e95be369e52be`を記録。候補の正式なlicense/CVE/clean OS承認は未成立。

OSL-018は署名・配布運用文書を完了しているが、署名・実配布自体は未実施である。

## BLOCKED / 未完了（2026-09-06 OSL履歴・リリース残件）

- **OSL-014 残る受入範囲**: 本PC正常系は上記PASS。Sandboxの過去2試行はprobe証跡未生成のままであり、その履歴を成功へ変更しない。旧smokeの終了例外は親アプリの終了待ちで、先行例外も隠し得る。「無関係なWebView2が多数あることが原因」という説明は未実証のため撤回する。異常系・別環境・汎用smoke統合は別の未評価範囲。
- **OSL-016**: 利用者の明示指示によりスキップ。復旧手順は作成済みだが、実障害注入は実行していない。
- **OSL-020**: 利用者の明示指示によりスキップ。Windows Sandboxの実行ファイル、Containers-DisposableClientVMパッケージ、vmcomputeとSandbox起動セッションは確認したが、LogonCommandの結果は未生成であった。さらにSandboxはWindows 11 Enterprise Insider Preview build 29648であり、production-channel clean Windowsの代替ではない。clean OS受入は未実施のままである。
- **OSL-021**: 利用者の明示指示により今回スキップ。署名・外部配布を実行していない。
- **OSL-022〜027**: 文書・計画同期を実施済み。各文書の敵対的レビューで確認した事実不一致を反映済み。OSL-028の最終独立レビュー待ち。
- **OSL-028**: 未着手。全差分・証拠・文書同期の最終独立レビュー。OSL-020のclean OS未成立、OSL-021の明示スキップ、業務E2E未完了を最終結果から分離する。

## 検証記録（2026-09-06 OSL履歴）

- 今回の検証は[OSL-014本PC正常系](osl-014-local-host-verification.md)のみ。以下の全回帰件数は従来の記録であり、今回全回帰を再実行したという主張ではない。製品コード・候補EXEは変更していない。
- focused OSL-012/013/015関連テストは、修正後に再実行が必要なテストfixtureを除き、候補生成前の126件回帰は成功。
- 標準Release回帰: Architecture 175/175、Core 665成功・1環境gate、Infrastructure 726成功・21環境gate、App 1,182成功・3環境gate、Web 474/474・accessibility 4/4・assets 6/6、Python 104/104、失敗0。
- 最終候補06はprivate証跡を `candidate-review-06.private` へ分離。公開candidateにはprivate BuildLogs/ComponentInputsを含めない。
- `releaseApproved=false`を維持。未実施のclean OS、署名、一般提供、業務E2EをPASSへ変更していない。
