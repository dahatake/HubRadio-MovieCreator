# UI/UX改善・V2中核の敵対的レビュー（2026-09-09追補）

現在の判定は**対象52単位すべて完了、UX-048最終独立レビュー閉鎖済み、未解決の重大指摘なし**。利用者指定で`UX-041/043`を除いた範囲であり、除外項目のPASSや一般提供承認ではない。最新の根本原因・修正後証拠は末尾§6に記録する。以下のメタデータと§1〜5は**2026-09-08時点の中間審査の履歴**であり、旧A17・E契約未接続・実native待ちを現在のblockerへ戻さない。

- 確認日: 2026-09-08。
- 対象: `2e85452b4a8747421831e3f0276199c07666e08f`上の未commit差分。UI/UX、設定ファイル、解析入力、V2-010/011のdomain・永続化・出力認可境界。
- 正本: [UI実装契約](../architecture/ui-workflow-contract.md)、[ADR-0011](../decisions/ADR-0011-compact-workflow-and-settings-file.md)、[詳細実装計画§0.8](../planning/implementation-plan.md#08-uiux改善2026-09-07承認)。9月7日の各task・レビュー履歴は本レビューと[品質記録](../quality.md)の履歴節に保全する。
- **判定: ローカルで検証した範囲の中間審査。全54単位・G-LIVE・最終UX-048・一般提供の完了ではない。**

## 1. レビュー方法と境界

実装担当と別のエージェントがsource・test・契約を読み、実在する問題だけを採用した。修正後に独立再レビュー、focused試験、標準全回帰、表示再測定を行った。レビューで見つからなかったことを、任意入力・全環境への安全性保証とはしない。

A17の既定方針を維持する。着手済みだったV2-010/011の中核を修正・検証したが、未完成V2-013や新しい本番DTOをUI taskへ追加せず、未受領E契約は依存待ちとする。fixtureを製品のjob・成果物・評価の代用として配線していない。

## 2. 指摘と反映

| 対象 | 確認した問題 | 反映・確認 |
|---|---|---|
| UX-032 | shellの参照専用表示では解析の未送信編集を扱えなかった | `analysisDirty`を案件切替guardへ接続。理由だけの入力・項目別draft・ACK・競合・明示破棄を検証。受付済み変更は`persisted=false`のまま |
| V2-010 | 評価の対象artifact種別と永続化相関の不足 | Render結合・canonical/正規化列照合・migration 009、負例を追加。集約器と実観測producerを区別 |
| V2-011出力 | 事後receiptだけでは出力前の認可・再照合・one-shot境界にならない | `AuthorizeLocalInternalDeliveryAsync`と`ConsumeLocalInternalDeliveryAsync`へ変更。実source/Renderを`ArtifactStore`で検証 |
| V2-011出力 | 同期generic callbackが非同期処理完了前にreceiptを保存できる | `Func<Context,CancellationToken,Task>`をawait。handle寿命とreceipt保存をcallback完了へ結合 |
| V2-011競合 | 別authorizationが同じready revisionの副作用を二重実行できる | immediate SQLite transaction下で最終根拠照合・callback・receipt。2store競合でcallback1回を実試験 |
| V2-011評価 | project revision/snapshotが評価入力に結び付かず、古い試行の遅延結果を付け替えられる | 開始eventのcanonical digestを評価`InputDigest`へ固定。factory・保存・履歴再構築で開始時刻と照合。正規JSONによる付け替え負例も追加 |
| V2-011復旧 | 評価中にprojectが更新されると同じworkflowを進められない | `TechnicalReevaluationStarted`で進行中試行を明示的に置き換え可能。新snapshot・新digestを使用し旧結果は拒否 |
| V2-011履歴 | 参照済み評価の規則行・一部列が更新可能 | 親評価の全列UPDATE、規則行INSERT/UPDATE/DELETEを禁止。全列列挙のno-op UPDATEも固有triggerで拒否する回帰を追加 |
| V2-011試験 | 旧API呼出し、重複Project INSERT、開始時刻の引数取り違え | 現行APIと実ファイルfixtureへ更新。named timestamp引数・開始との事前照合。期待拒否を緩めない |
| V2-011 SQL | 初回の明示再開始revision 3を旧`revision > 3`が拒否 | `> 2`へ変更し、Core/reducer/SQL/保存の状態遷移を一致させた |
| UX-041狭幅 | 縦scrollbarがある320px画面でrootの`min-width:320px`により15pxの横はみ出し | rootを`min-width:0`へ。overflow隠蔽なし。回帰testと50画面の再測定 |
| UX-041試験 | 新CSS契約testのselectorがCRLFで一致しない | source読込時に改行だけをLFへ正規化 |
| 全体 | .NET formatterの空白違反81件 | 空白だけを修正し、標準format・warn-as-error buildを通過 |

V2-011の最終独立再レビューでは、確認したdomain／永続化境界に未解決の重大指摘なし。狭幅修正・契約／guide／計画の独立レビューでも、上記反映後に重大な不整合は確認されなかった。過去の実行失敗や測定失敗を消さず、修正後runを別記録にした。

最終文書レビューでは、成功表示だった編集が受入記録とaccessibility記録へ反映されていない2件を確認した。両文書を個別に追補し、実在確認と独立再レビューで解消を確認した。最終の読取専用監査`artifacts/validation/audit-uiux-resume-20260908.py`は53項目成功・失敗0。原本bytesのhash、runner別件数、測定組合せ、全54task行の重複/欠落、相対リンク先の実在、版未変更を照合した。これは未受領Eや実native受入を通過させる検査ではない。

### 保証しない境界

- ファイル配送とDB receiptは1つの原子的transactionではない。callback成功後の取消・receipt失敗は`ReconciliationRequired=true`として明示し、再送を安全と推測しない。
- callbackは信頼されたアプリ内部処理であり、同じDBへ書くcallbackは契約外。本番deliveryの接続・失敗回復はV2-013で別途受入が必要。
- `AutomatedEvaluationEngine`は供給された観測の集約器。未取得のcodec/decode/音量等を実測済みにしない。人の理解・知覚・法的適合はNOT EVALUATED。
- 未送信解析入力の破棄は、ホスト受付済みの未永続化変更を取り消さない。実際の解析producerと永続化は別境界。

## 3. 最終ローカル実行結果

`test.ps1 -Configuration Release`を修正後に実行し、全runner成功。依存復元、format、warn-as-error build、Web型・依存・asset検証を含む。

| runner | 成功 | 環境skip | 失敗 |
|---|---:|---:|---:|
| Architecture | 192 | 0 | 0 |
| Core | 726 | 1 | 0 |
| Infrastructure | 1006 | 25 | 0 |
| App | 1464 | 3 | 0 |
| Web Vitest（75 files） | 1811 | 0 | 0 |
| accessibility | 6 | 0 | 0 |
| assets | 6 | 0 | 0 |
| worker | 104 | 0 | 0 |

原本: `artifacts/validation/uiux-v2-full-20260908-b.log`、SHA-256 `e514dcb9b857a99b967195948ef472c4fc0fd1dcdaf2418e552e94a836ad5998`。成功5315、環境skip29を混同しない。focused Core50／Store74／Migration63／Backup51／Bootstrap8は上表の内数であり加算しない。

Web/Appの`asset-manifest.json`はともにSHA-256 `d60f1ff6c1fe06477bbfaca88e82f01e01e2b7811a8461c0f5af78331edd834c`で測定対象と一致。App出力と既存のUIUX依存payloadに利用者`setting.txt`は0件。新しい配布EXE・署名・一般提供の成功証拠ではない。Viteの500kB超bundle警告は残り、性能受入にはしていない。

## 4. 表示証拠

原本: `artifacts/validation/uiux-resume-browser-806d44cc-78c1-472b-95d1-337c662bdd4b.json`、SHA-256 `21f7e3b7bac0983243b25079a2dd2ba86b2f51e8b8374e82358095301710d899`。保存時刻`2026-09-08T02:45:55.451Z`。Code 1.136.1／Chrome 148.0.7778.280／Electron 42.10.0、**DPR1.25**、visibility hidden。

- 明暗×5サイズ×5画面の50組。document横あふれ0/50、幅1280以上のdocument縦あふれ0/30。
- 1280×720の通常4画面と設定の外側paneは縦横scroll寸法がclient寸法に一致（両theme計10組）。
- 123個の異なる設定キーで保存buttonが詳細pane内。保存処理を123回実行した証拠ではない。
- CSS root font32pxで検索・入力・保存・詳細・閉じるの5controlが可視範囲へ到達しfocusが非表示に入らない。
- 解析の入力保持・切替禁止・復帰focus・明示破棄・切替再許可の5項目がtrue。

修正前原本`uiux-resume-browser-4fe371a4-6362-4216-95be-38b7b88c213b.json`（SHA-256 `ddf3fc0934d2ba4090efd1582a0ffbe8494d25174d0e81e07177c88d89ad8002`）では6/50組の横あふれを記録。**前後でDPRが約1→1.25と異なり、同一環境でCSSだけの因果効果を測定した比較とはしない。** 修正後の上記環境に対する再測定として扱う。

信頼されたテストbridge・合成click・DOM focus/scrollによる記録。測定判定コードはこの会話で実行し、保存した真偽値と寸法を独立reviewで照合した。実native、物理入力、実媒体、screen reader、High Contrast、人の理解、OS倍率の受入ではない。測定用3serverは終了し、4183/4184/4185のlistener不在を確認した。

## 5. 残件と後続条件

| 残件 | 未完了の理由／再開条件 |
|---|---|
| UX-039B | 実warning ID・版/digest・targetを供給するproducer未接続。純粋navigation試験を実warning往復の代替にしない |
| UX-040A〜D | E-START/E-EVALUATION/E-RESULT/E-REGENERATEの実契約未受領。実job→版→artifact→評価→delivery、設定snapshot→子版→再生成の製品経路が必要 |
| UX-041/043の実native部分 | 現EXEは`MainWindow`で実利用者のLocalApplicationDataを固定使用し、app root/DBを隔離するCLIがない。実利用者DBを使うsmokeは今回実行しない。利用者profileを接続しない使い捨てWindows VM等で受入する |
| UX-048最終 | E結合・実native再受入後に全差分と文書を再審査。今回の中間審査を最終合格へ置換しない |
| CHANGELOG／PATCH | 全54実行単位完了後という利用者条件が未成立。`CHANGELOG.md`未作成、package `0.1.0.0`／worker `0.1.0`／Web `0.0.0`を維持 |

clean OS・署名・配布・license/CVE/legal・全業務E2Eは[OSL等の既存残件](remaining-task-status.md)と分離し、`releaseApproved=false`を維持する。追加の承認・秘密情報・本番resource操作を推測して進めない。

## 6. 2026-09-09 実統合の修正後レビュー（現在）

実native機能・実producer・App/UI・render/QC/delivery・字幕保存／再生成は接続済み。以下は実際の失敗から確認した根本原因と、修正・再レビューでclosedとなった6件の指摘である。対象52単位のUX-048最終独立レビューも完了し、未解決の重大指摘なし。人手評価・配布承認や旧OSL-028の閉鎖へ拡張しない。

| 根本原因 | 修正・閉鎖した境界 | 修正後の証拠 | 個別指摘 |
|---|---|---|---|
| Workerの一時出力pathが284文字になり、最終pathが使えてもpublishに失敗 | 実probeで旧一時名284文字の失敗と短縮名238文字の成功を確認。一時名を短縮し、排他的作成・上書き禁止・自分の一時fileだけのcleanupを維持 | publish回帰3件、Worker全回帰169成功／1 skip（`worker-all-fixed-20260909T111238857Z.log`）、実ASR E2E `observed-e2e-20260909T144834883Z-41b8cdcc` PASS | CLOSED |
| 実producerのwarning code／roleをnative・Web codecが拒否 | 55個の明示codeと`landscape-subtitles`、`human-evaluation`、canonical UUID付き`vertical-subtitles`等を両側で整合。warningを改変せず、不明code・不正role・artifact権限への混用は拒否 | focused 45成功（`observed-test-20260909T135243305Z-cdde5abb`）、最新Web全回帰・実E2E PASS | CLOSED |
| OutputQcのffprobe frame行にSEI由来の空fieldが残り、正しい媒体を拒否 | `:frame_side_data=`で抑制し、検証済み3fieldの後の空の末尾区切り1個だけを許容。欠落・重複・未知field・複数区切りは拒否し、raw証拠hashは保持 | QC focused 24成功（`observed-test-20260909T144811733Z-1c77ae19`）、実E2Eと実字幕FFmpeg／QC PASS | CLOSED |
| コピー先の子fileをopenしたままではWindowsの`Directory.Move`が失敗 | copied child handleをrename前にdispose。入力handle・DB transactionを保持し、昇格後の全出力を再hashしてcommitまで検証 | delivery focusedと改ざん回帰、全7成果物保存・再open `observed-e2e-20260909T144834883Z-41b8cdcc` PASS | CLOSED |
| BGMのbyteSize／digest不整合が一般process失敗へ誤分類 | `BinaryVerificationException`だけを`UnsupportedMusic`へ分類し、不整合媒体はencoderへ渡さない。他の失敗を一律に隠さない | 関連focused 13成功（`observed-test-20260909T144747136Z-52190981`）、最新App全UID回帰PASS | CLOSED |
| 通常MRTのPRI入力除外hookの対象が不適切 | 通常MRTでは`GetPriOutputs`ではなく`PackagingOutputs`を対象にする。対象source完全一致の除外を通常MRT／MSIXの両経路で確認 | 最小試験2/2/4 PASS、修正後Architecture209成功（`observed-test-20260909T163146676Z-0543b2b4`）、`artifacts/validation/app-runtime-20260909c`のPRI 3760 bytesとコピー済みroot照合PASS | CLOSED |

focused件数は重複のある修正確認であり、最新全回帰へ加算しない。旧`task.output_failed`／QC失敗runと上表の前回initial E2Eは履歴として保持し、最新実統合は下記の空白修正後runで区別する。個別指摘の閉鎖と対象52単位の最終レビュー完了を区別して記録し、架空の評価者・人手承認を補わない。

### 全回帰と別実行の照合

- 最新Appは`app-shards-20260909T150051Z-0e9f12c6` summaryで**ALL 2410＝2402成功＋8 skip、失敗0**。8 skipは環境条件4件＋opt-in実統合4件。別実行4件のPASSをApp成功数へ重複加算しない。
- Infrastructure1337成功／25 skip、Core731成功／1 skip、PRI hook修正後Architecture209成功、Web84 files／2066成功、accessibility6／assets6成功、Worker169成功／1 skip、すべて失敗0。総計は**6926成功・35 skip・失敗0**。原本IDは[品質記録](../quality.md#2026-09-09-機能全回帰の確認現在)に集約する。現行結果はshard・runner別実行であり、標準`test.ps1`の一括実行とはしない。
- 実ASR・全7成果物保存／再openは39ファイルの空白修正・token同値確認後の`observed-e2e-20260909T163411105Z-97f44d5a`で1件PASS。前回initial `observed-e2e-20260909T144834883Z-41b8cdcc`は履歴として保全。再生成は`observed-e2e-20260909T145632533Z-ba46a2bd`、retryは`observed-e2e-20260909T145852256Z-bac8df13`で各PASS。
- 既知の合成Machine cueの手動字幕編集・永続化・再読込・実FFmpeg／QCは`observed-test-20260909T145059046Z-3a086056`でPASS。実ASR字幕と称しない。

E2Eは4秒の合成媒体を使用し、`V2-014`でもsample／提供実MOVは使用していない。性能、ASR精度、人手／知覚品質の受入ではない。実native機能の検証と実GUIの人手受入を分離する。契約で許容された限定legacyの理由付き`Unavailable`は維持し、架空の承認や新たな必須gateで埋めない。

### 最終確認と完了条件の成立

最終.NET format（`20260909T163633`）、PowerShell parser 35ファイル、`pip check`、`diff check`、Web production buildはPASS、Vite約848KB警告は残存。39ファイルのC# token同値は`token-verification.json`で確認済み、sourceの0-byte該当なし。runtime App build（`20260909T162001902Z`）は終了コード0・エラー0・警告0で、`artifacts/validation/app-runtime-20260909c`のPRI 3760 bytesとコピー済みrootを照合済み。

対象52 ID完全一致・欠落0・重複0・18 raw原本hashは[詳細実装計画§0.8.2](../planning/implementation-plan.md#082-対象52単位の完了と実接続の限定検証2026-09-10-jst追補)と`artifacts/validation/final-evidence-hashes-20260909c.json`へ機械照合済みとして記録する。最終hash記録（20260909c）も存在する。UX-048最終独立レビューを閉鎖し、対象52単位の完了条件成立を記録してからpackage `0.1.1.0`とCHANGELOG Unreleasedを反映済み。本追補はmain側の確認済み結果の同期で、再実行や新たな承認ではない。

`UX-041/043`は除外でありPASSではない。`UX-044`の未測定・人手17項目の`PENDING`、旧OSL-028等の歴史的リリース残件を維持する。旧`candidate-review-06`は変更せず、一般提供は`releaseApproved=false`のままである。

### PATCH後のFINAL完了記録

対象52単位の完了条件成立後、package版定義のみを`0.1.1.0`へ更新し、builder／verifier／manifest／`MsixPackagingProjectTests`の値を完全同期した。以下はmain側の確認済み結果の同期であり、本DOC更新での再実行ではない。

- **Architecture focused subset:** package版定義のみの更新後に**11成功・失敗0・skip0**。原本`artifacts/validation/observed-test-20260909T165857057Z-ab0c40ef.stdout.log`。全回帰の追加ではなく、総計**6926成功・35 skip・失敗0**へ加算しない。
- **最終build:** 成功、**警告0・エラー0**。原本`artifacts/validation/observed-build-20260909T165512021Z-97a04735.stdout.log`。
- **最終format:** **PASSED**。`artifacts/validation/final-static-format-20260909T165629710Z.report.json`は`[]`。
- **最終静的source検査（2026-09-09 16:59:06 UTC、main観測）:** PowerShell parser **35ファイルPASS**、`pip check`／`diff check` **PASS**。

**対象52単位（利用者指定の`UX-041/043`を除外）はすべて完了、UX-048最終独立レビュー閉鎖済み**。除外項目、人手17項目の`PENDING`、`UX-044`未測定、`releaseApproved=false`と9月8日以前の履歴は変更しない。
