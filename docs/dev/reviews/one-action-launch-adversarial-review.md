# 1操作起動・配布成果物の敵対的レビュー

- 日付: 2026-09-06
- 対象: OSL-001〜012の現在の実装・文書・試験証拠と、それらが呼ぶ既存起動／Python検証境界
- 目的: 保存済み単一EXEへの1回の起動指示で、対象Windowsの標準ユーザーが追加セットアップなしに既存UIを操作できること。内部展開を許容し、インストーラー・自動更新・業務E2E完成は別扱いとする。
- 正本: [NFR-009/AC-007](../../requirement-definition.md)、[ADR-0010](../decisions/ADR-0010-one-action-portable-launch.md)、[OSL計画](../../../work/20260906-OneActionLaunch-ImplementationPlan.md)
- 方法: builder、payload、起動、文書、試験証拠を独立担当で並列に検査。作成時の意図や過去のPASSだけを根拠にせず、実コード・実bundle一覧・実ログ・公式APIと照合した。同一問題を複数担当が指摘した場合は統合した。
- 現在の状態: **16件すべてに最小修正を反映し、完成候補・SBOM・全自動回帰・独立再レビューを確認済み。実WebView、clean production Windows、署名・実配布は別の未評価受入。**

## 確認した問題と処置

件数はレビュー開始時の問題15件と、実候補の再検証で独立して実証した問題1件の計16件。同じ原因から生じた追加不整合は対応する行の修正へ含め、水増ししない。未着手のOSLタスクを1件ずつ欠陥として数えない。

| No. | 軸 | 重大度 | 指摘箇所 | 問題の説明 | 修正案 |
|-----|-----|--------|---------|-----------|--------|
| 1 | 目的適合性 | Critical | `packaging/portable/build-portable.ps1` 必須bundle入力 | 統合CLRを持つ実SDKの`singlefilehost.exe`構成に独立`coreclr.dll`を要求し、publish成功後に候補生成が必ず停止した。candidate02の実失敗で確認。 | 固定SDKの統合host/CoreLib契約を検証する。WindowsのDLL名大小文字差は照合だけで吸収し、記録名・Ordinal hashは保持。修正・回帰・完成候補で確認済み。 |
| 2 | 内容の妥当性 | Critical | `PortablePayload.targets`／`build-portable.ps1` publish対象 | 実bundleに開発用`.pubxml`2件が混入し、必要資産の包含確認だけでは拒否できなかった。秘密が漏れたとまでは主張しないが、不要な開発資産を含めない配布契約に反する。 | portableだけでprofileとrawコンパイル入力のpublish経路を除外し、builderもコピー前に独立拒否。XAMLコンパイル・PRI/XBF・vendor資産は保持。完成候補の禁止物0件を確認済み。 |
| 3 | 目的適合性 | Critical | `src/HubRadio.App/Web/WebViewHost.cs`／`Bridge.cs` 初回navigation | WebMessage無効のまま初回navigationし、完了後に有効化していた。公式仕様では変更は次のnavigationから適用され、初回UIがbridgeを使えない順序だった。 | origin/schema制限を維持してBridge登録・有効化をNavigate前へ移す。順序・初回pingの回帰試験を追加。コード・自動回帰・独立再レビュー済み。実WebView受入は未評価として分離。 |
| 4 | 品質・運用性 | Major | `build-portable.ps1:Invoke-BuildTool` | 子process終了と出力回収が無期限待機。テストrunner外側のtimeoutは実builderの停止保証にならない。実ハングが発生したという指摘ではない。 | publish/SBOM10分、git/version30秒の有限安全期限とbounded drain/cleanupを設ける。実際に停止しない検証子process・子孫を停止する試験を追加。修正・試験済み。 |
| 5 | 内容の妥当性 | Major | `packaging/python/build-runtime.ps1`／`verify-runtime.ps1` legacy集合hash | `Sort-Object -CaseSensitive`がCurrentCulture依存。同一2,695件でもtr-TRでは既存hashと不一致になり、verifierの集約検査が失敗することを再現した。 | 既存hashと互換な`-Culture en-US`を明示。Ordinal移行による旧証拠の書換えは行わない。4カルチャ試験とtr-TRでの実runtime検証が成功。 |
| 6 | 整合性 | Minor | `packaging/portable/prepare-payload.ps1` OutputPath | `;`等の後段builderが拒否するpathを受理して大容量payloadを作れてしまい、工程間の入力制約が一致しなかった。 | OutputPathだけへ後段MSBuildの文字制約を大容量I/O前に適用。vendorファイル名の共通検証は変更しない。修正・回帰済み。 |
| 7 | 根拠性・不確実性管理 | Major | `WebViewHost.cs`／`MainWindow.xaml.cs` Ready条件 | navigation成功とURL一致だけでReadyとなり、React未起動やJS欠落を成功に見せ得た。既存smokeも同じAutomation名を確認するだけだった。 | navigation成功と、React mount後の既存pingに対するnative応答post成功の両方を有限時間内に確認。JS受領ACKまでは保証しない。欠けたsignal・失敗・取消・StrictModeの試験を追加。修正・回帰済み。 |
| 8 | 品質・運用性 | Major | `MainWindow.xaml.cs` WebView2環境作成 | SDKは`WEBVIEW2_USER_DATA_FOLDER`で明示値を上書き可能だが、実user-data folderを検査していなかった。実行依存tree等への保存先変更を検出できない。 | `environment.UserDataFolder`と期待per-user pathをcontroller生成前に照合。不一致はpathを含まない初期化失敗。共通の直接path比較と負例試験を追加。修正・回帰済み。 |
| 9 | 品質・運用性 | Minor | `MainWindow.xaml.cs` 起動失敗／Closed | 初期化後の失敗でhostイベントを解除してもWebView本体を閉じず、ブラウザーとuser-data使用が残る経路があった。 | 所有者がhost破棄、非表示化、`Close()`を一度だけ実施。既存案件・profileを削除しない。コード修正・再レビュー済み。実process終了確認は未実施。 |
| 10 | 根拠性・不確実性管理 | Major | `work/20260906-OneActionLaunch-Execution.md` OSL-012 | 最新candidate02がexit1で終了済みなのに「生成中」と記載され、publish成功と候補／SBOM成功を区別できない状態だった。 | attempt名、exit1、`BUNDLE_REQUIRED`、完成候補なし、SBOM未生成を追記。過去の限定成功は保全。修正済み。 |
| 11 | 整合性 | Major | `docs/dev/planning/implementation-plan.md` §0 | 現在の実行順は§0だけと案内する一方、承認・実施中のOSL系への参照がなく、V2-010だけを次手としていた。 | 業務系V2と起動系OSLを分離し、§0.7からADR・計画・最新台帳へ誘導。旧203件snapshotは変更しない。修正済み。 |
| 12 | 根拠性・不確実性管理 | Major | `artifacts/validation/osl009/Test-PayloadEvidence.ps1` manifest照合 | 件数と各行の存在だけを確認し、宣言を`[A,B]→[A,A]`へ変えても完全一致と判定できた。実treeのdigestだけでは宣言の欠落を検出できない。 | 宣言pathの一意性・厳密なOrdinal順・完全キー集合・宣言側digestを検査し、同数の重複／欠落等の6合成試験を追加。修正済み、6合成試験＋18実証項目成功。 |
| 13 | 整合性 | Major | `tests/smoke/Test-WebViewStartup.ps1`／`tests/Acceptance/OneActionLaunch.md` | Portableの正しい失敗タイトルは「アプリの配布ファイルが不完全です」だが、検査は開発版タイトルだけを要求し誤FAILする。 | developer用Distribution指定とPortable時の明示AppPathを追加し、表示契約を分離。SDK override負例と物理欠落負例を混同しない。手順・コード修正済み、実GUIは未実施。 |
| 14 | 品質・運用性 | Major | `WebViewStartupCoordinatorTests.cs`／受入F06・F07 | 有効な別Runtimeへのoverrideやuser-data overrideを指定したとき、Readyや新規DB作成より前に拒否するガードの負例がなかった。単なる「実GUI未実施」とは別の試験設計漏れ。 | path拒否の決定的試験と、使い捨てBフェーズの実Runtime／保存先override手順を追加。試験設計と単体は修正済み、実Runtime受入は未実施として残す。 |
| 15 | 根拠性・不確実性管理 | Minor | `artifacts/validation/osl009/Test-PayloadEvidence.ps1` 証拠run | 固定`corrupt-worker`が残るため再実行が失敗し、以前の成功`result.json`がrun識別なしで残る。新旧結果の対応が曖昧だった。 | 未存在のrun専用directoryを必須とし、run ID・開始/終了時刻・script/helper/input hashと失敗状態を記録。過去証拠を上書きしない。改訂ハーネスを新規runで実行し成功。 |
| 16 | 内容の妥当性 | Critical | `build-portable.ps1` SBOM検証結果の公開境界 | ツールの検証生JSONは実行パラメーターにローカル絶対パス3件を含む。これを公開証拠として検査したため、SBOM生成と検証が成功しても候補完成前に`METADATA_PATH`で停止した。さらに`ValidationErrors`の`Count=0`／`Errors=[]`形状を空辞書と誤認した検査でも正常結果を拒否した。SBOM本体のパス漏えいではない。 | 生JSONを私用BuildLogsへ保全し、厳密なSuccess・整数`Count=0`・空`Errors`配列・原本hashから5項目の公開サマリーを生成する。公開パス検査は維持。実JSON投影、27件のbuilder回帰、完成候補、独立レビューで確認済み。 |

## サマリー

### 検出した問題（各修正前、追加実証を含む）

- Critical: 4件
- Major: 9件
- Minor: 3件
- 合格判定: **FAIL（Critical > 0）**

### 修正後

- Critical: 0件
- Major: 0件（未修正の指摘なし）
- Minor: 0件（未修正の指摘なし）
- 合格判定: **PASS（Critical = 0）**

このPASSは、上表16件の修正が現在のコード、完成したローカル候補、SBOM、証拠、自動回帰で成立したという判定である。実WebViewでの初回bridge／override拒否／process終了、production-channel clean OS、標準ユーザー、offline、署名・MOTW・実配布、第三者条件、動画編集E2Eは別の受入であり、未実施のままPASSへ含めない。

## 実施済みの検証

- 配布・Python関連のfocused試験: 126/126成功、失敗0、skip0（No.16の追加3件を含む）。
- React Appの起動試験: 13/13成功。production build、依存3件とdist検査成功。
- native起動／bridge／path試験: 102/102成功、失敗0、skip0。
- Pythonのtr-TR実検証: 2,695 files / 268,442,311 bytes、既存集合hash `1ef1e9fca2b89c006f6030de0b315a9255c9b969fdf3ac7cc36a30aeb412ab7f`一致。native import6件、health、protocol、入力tree不変を確認。
- 改訂証拠ハーネス: 6合成試験と18実証項目すべて成功。run `review-73ff1eb5255e479581ec08308c5338cb` の結果SHA-256は `aa42672fb4382a45e3cd38a37abef07c45621166decc917292221c6d690af9e8`。過去の結果ファイルは変更していない。
- No.16の実JSON再検証: 生JSONを変更せず公開5項目への投影に成功。対応原本SHA-256は `6b6059da8796f71be0552e6a076e9609fa2d76b48916b7605a686856fea16ffe`。その後のbuilder27件の回帰も成功。
- 初回完成候補: `candidate-review-03` は履歴として保持する。現行の固定候補は `candidate-review-06` であり、同じ版・同じsource commitでもprivate証跡分離と候補manifestの再生成後の別bytesである。現行値は [OSL-017 release review](../licenses/release-review.md) と [OSL-019 status](remaining-task-status.md) を参照する。
- 初回候補の当時の確認（3,794 Contents / SBOM / release manifest hash）はこのレビューの履歴証拠であり、candidate-review-06の一般release承認を意味しない。現行candidate-06は`releaseApproved=false`を維持する。
- 標準Release全回帰: Architecture 175/175、Core 665成功・1環境gate、Infrastructure 726成功・21環境gate、App 1,182成功・3環境gate、Web Vitest 474/474＋accessibility 4/4＋asset 6/6、Python 104/104。失敗0。環境gateをPASSへ変換していない。
- 起動・配布修正の独立した再レビュー: 指摘反映をソースとSDK契約で確認。実GUI／完成SBOM成功を代替するものではない。

## 修正方針・残余範囲

- No.1〜16はすべて最小のコード、試験または文書変更を実施した。一般化したprovider、installer、別のruntime取得、自動更新、署名基盤は追加していない。
- No.3、9、13、14の実WebView・実process・overrideの最終受入は、使い捨て環境でのOSL-014/020へ残す。通常の開発者profileや実案件DBを使う試験で代用しない。これは上表のコード／試験設計修正を未修正とするものではなく、受入範囲の留保である。
- 署名主体・実配布先・production-channelのクリーンOS証拠・最終第三者再配布条件は引き続き未確定。未実施をPASSにしない。
- 元のOSL全28タスクをこのレビューで完了扱いにしない。全タスク完了後という条件付きのCHANGELOG記載は、条件が成立するまで行わない。
- 指定された`hve`、Markdown query Skill、Code Query skillはこのリポジトリに存在せず、今回変更対象でもない。無関係なHubRadioパッケージへのPATCH繰上げは行わない。

## 直接根拠

- 失敗したcandidate02: `artifacts/portable/candidate-osl012-02.771b318b033742f3a5316e4c387782b6.partial/BuildLogs/`。publish stdoutとFilesToBundleの3798行を照合し、host形態・開発profile・DLL名表記を確認。
- No.16の実証: `artifacts/portable/candidate-review-01.7557340c8a40426589d5da14ceefbaf6.partial/`。SPDXの3794files/176packages、検証Result=Success・ValidationErrorsのCount=0/Errors=[]、SBOM本体の絶対パス0件と検証生JSONのパラメーター3件を照合した。値は共有せず項目名だけを確認した。`.Count`が同名のJSON項目を返すことを「空辞書」と誤読した説明は訂正済み。
- 固定SDK `10.0.400` とruntime pack `10.0.11` の `RuntimeList.xml` による `DropFromSingleFile` の扱い。
- [WebView2 IsWebMessageEnabledの適用タイミング](https://learn.microsoft.com/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2settings?view=webview2-winrt-1.0.4129.50): navigation開始後の設定変更は次のtop-level navigationから適用。
- [WebView2 Environment](https://learn.microsoft.com/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2environment?view=webview2-winrt-1.0.4129.50) と環境変数による実効folderの扱い。
- [WinUI WebView2.Close](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.webview2.close): 所有controllerの明示終了。

本文のコード位置はシンボル／工程名を基準に記載した。修正後の行番号の移動を、修正前の障害位置として偽らない。
