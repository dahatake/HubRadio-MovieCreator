# UI/UX改善の比較・受入手順

要求正本は`docs/requirement-definition.md`、承認済みの設計は`docs/dev/decisions/ADR-0011-compact-workflow-and-settings-file.md`と`docs/dev/architecture/ui-workflow-contract.md`。以下は手順であり、未実施項目をPASSにしない。

現在の実行範囲は利用者指定で`UX-041/043`を除く52単位。判定は**対象52単位すべて完了**であり、最終静的検査・runtime照合・UX-048最終独立レビュー・記録確定・package `0.1.1.0`／CHANGELOG Unreleased反映済み。詳細は末尾の2026-09-09節に記録する。除外項目はPASSではなく、人手評価・一般提供承認とは別境界。9月7日・8日の未接続／実job未実施という記述は履歴として保全し、現在のblockerへ戻さない。

## 測定条件

1. 対象commitとWeb asset manifestを記録する。既存のproduction build/verifierを通す。
2. loopback previewではbridge未接続かテスト専用fixtureかを明示する。本番へfixtureを同梱しない。実WebViewとの検証は別記録にする。
3. CSS viewport、client領域、zoom、devicePixelRatio、OS scaleの測定有無、theme、browser版、visibilityStateを記録する。
4. 1280×720、1600×900、1920×1080、1024×640、320 CSS px幅相当を確認する。200%文字拡大は幅相当試験と区別する。
5. screenshot/raw測定はrun別の`artifacts/validation/`へ。共有文書に元動画、個人path、秘密情報を転載しない。

## 機械検証

- 通常4画面と設定の各pageでdocument scrollWidth/HeightとclientWidth/Height、主要操作のboundsを取得。overflowによる切捨てを見逃さない。
- 4tabのrole/selected/controls、左右/Home/End、Enter/Space、Tab、skip link、設定からの復帰、非表示panelのfocus除外を確認。
- 案件未選択、選択済み、未保存、処理待ち、取消、競合、未接続、失敗、0候補を区別。予定件数から実成果物を捏造しない。
- 全123設定の分類・検索・page・advancedへ到達でき、保存/reset後に未保存draftが消えないことを確認。
- scope変更、遅延response、tab往復で案件/版/時刻/undo/未保存本文を保持。非表示の動画をpauseし、戻るだけで再生しない。
- fileは一時app rootの`setting.txt`だけで確認。UTF-8/BOM/LF、raw4MiB/canonical1MiB、strict digest、上書き確認、preview非更新、OCC、ACL、restore後の独立性を試験する。
- light/dark contrast、visible icon label、reduced-motion、拡大時reflowを確認する。
- 実データ接続後はjob/version/artifact/evaluation/deliveryのbinding、warningへの往復、明示再生成を別runで検査する。

## 任意の利用者観察（UX-044）

| 操作 | 観測した行動・つまずき | 本人の判断 | 状態 |
|---|---|---|---|
| 元動画選択と開始 | 未実施 | 未取得 | NOT EVALUATED |
| 成果物の種類・状態の理解 | 未実施 | 未取得 | NOT EVALUATED |
| 設定変更と結果の違い | 未実施 | 未取得 | NOT EVALUATED |
| タブ往復と編集状態の保持 | 未実施 | 未取得 | NOT EVALUATED |
| setting.txtの保存・復元 | 未実施 | 未取得 | NOT EVALUATED |
| warningから修正先への移動 | 未実施 | 未取得 | NOT EVALUATED |

物理keyboardや人の理解を自動操作の成功から推測しない。任意観察をquick startの必須gateにしない。自動検証できなかった項目も未評価として明記する。

## 変更前の実施記録

2026-09-07: `docs/dev/reviews/ui-ux-baseline.md`。1280×720のbridge未接続Web表示でpage高さ6867pxを観測。このbaseline取得時点では変更後の受入は未実施だった。

## 変更後の機械確認（2026-09-07）

以下は追加の失敗経路レビュー前の第1回測定記録。対象は`2e85452b4a8747421831e3f0276199c07666e08f`上の未commit UI/UX差分。この時点のWeb asset manifestとApp出力内の同manifestのSHA-256は、ともに`95a8fc59d632f72478b3ca3fcf4a1c53c8320c1ae979d606332cb37809fd6806`。最新のbytesと再試験は後段に分離する。

### 実ブラウザーの表示測定

- VS Code integrated browser: Code 1.136.1、Chrome 148.0.7778.280、Electron 42.10.0、DPR 1、`visibilityState=hidden`。OS表示倍率、native client寸法、実WebView2は未測定。
- loopbackのproduction assetへ、同梱profileの全123定義・初期値から作った**テスト専用bridge応答**を使用。架空の試験用案件名・IDと明示したfixtureであり、実ユーザーDB、実媒体、実処理ジョブは使用していない。
- 1280×720、1600×900、1920×1080、1024×640、320×720 CSS pxについて、light/darkそれぞれの通常4画面＋設定を測定した。**50ケースすべてdocumentの横はみ出し0**。1280以上の3サイズではdocumentの縦はみ出しも0。
- 1280×720の元動画選択済みfixtureでは、通常4画面の内容領域も`clientHeight = scrollHeight`。開始・取消は画面内に収まる。設定は123個の異なるキーを順に選択し、**全123個の保存ボタンのboundsが詳細pane内、documentは1280×720内**であることを確認した。これは保存ボタンの表示位置の検査であり、全123項目を実DBへ保存した試験ではない。
- 長い設定値・リストは内容scrollを許容し、保存・初期化をその外に配置。320px幅では縦方向のscrollで全文へ到達する設計を維持する。1024×640に通常無scrollの追加保証はしない。
- 200%文字拡大はroot `font-size: 200%`（32px）によるCSS試験。native browser zoom／OS倍率の試験ではない。ページ切替、入力、初期化、詳細、閉じるの5対象を`scrollIntoView`等で移動し、各control全体が可視範囲内へ到達することを確認した。
- 操作は`dispatchEvent`、DOM focus、`scrollIntoView`を含む自動化。物理keyboard、screen reader、High Contrast、実動画再生、人の理解はこの結果からPASSにしない。keyboard動作の機械契約は別のVitest試験で確認している。

private原本: `artifacts/validation/uiux-browser-measurements-78a6fcae-fa5d-4a07-b144-bf0c9ddb6e4f.json`（SHA-256 `1fb54092c57f1a72ccc324c3181ab9530357643ec11daf75f0e319feb6761c6a`）。原本の記録時刻は`2026-09-07T06:35:54.616Z`で、各ケースの実行時間ではない。

最終画像は同directoryの`uiux-source-final-20260907.png`（SHA-256 `be777dc0d6cb208cbe1bd17360eebd91d90017b70b5b1e97e506b88b33600269`）と`uiux-settings-final-20260907.png`（`9be1156c85748a736693d99e4cc904d0235891a2f06124d7f4cd1301f9d68777`）。いずれもfixture画面。画像だけで全ケースの合格を判断しない。

### 保存・回帰の証拠

- `SettingsFileIntegrationTests`の10ケースは、一時app root／DBで実native handlerとstoreを使い、export確認・preview非更新・apply、scope/OCC、再起動、backup/restore後のexport不変を検証して10成功・失敗0・skip0。
- 標準`test.ps1 -Configuration Release`の最終runは全runner成功。Architecture 192、Core 665、Infrastructure 881、App 1464、Web 1660、accessibility 5、assets 6、worker 104が成功。Core 1／Infrastructure 25／App 3の環境skipは成功に含めない。
- privateログは`artifacts/validation/uiux-full-verification-final-assets-20260907.log`、SHA-256 `0100fd8d956a31377354ab034ef78b91d011635f0660b70765f91c1f27f9e5c0`。型・format・警告をエラーとする.NET build・Web dependency/dist verifierも同runで通過した。

**未成立:** 実WebView2を使う今回の画面操作、実警告から修正先への一巡、実動画の生成・自動評価・納品・再生成、clean OS／署名／一般配布。任意観察表の`NOT EVALUATED`はそのまま維持する。

## 追加レビュー修正後の再確認（2026-09-07）

fileの最終照合と置換の間の競合、空backupの区別、競合後のUndo、文字起こし/layoutの失敗時入力保持と部分Cue ACK、file操作のfocus復帰を修正し、各focused試験と独立再レビュー後に標準全回帰を実行した。

- **全回帰:** Architecture192、Core665（skip1）、Infrastructure897（skip25）、App1464（skip3）、Web75 files/1766 tests、accessibility5、assets6、worker104成功。失敗0。個別runの件数を重複加算しない。
- **全回帰原本:** `artifacts/validation/uiux-full-verification-recovery-reviewed-20260907.log`、SHA-256 `f9e24d404885af796b5f6f54d344fbcc2198100e7b01247b8fff35d2ed72af65`。
- **測定対象:** WebとApp内のasset manifestがSHA-256 `f58db99cfb263f0c5bbbc204bebc2c4fc983c86e6e3d8c0692bb88f9b5ead13e`で一致。commitは同じ未commit差分で、fixture/browser条件は第1回と同じ。
- **表示再試験:** 明暗×5サイズ×5画面の50ケースでdocument横はみ出し0。1280以上3サイズでdocument縦はみ出し0。1280×720の通常4画面では内容scrollも0。123キーすべての保存ボタンがpane内。
- **文字拡大:** root font200%=32pxで、ページ切替・入力・初期化・詳細・戻るの5対象をDOM scroll/focusで移動し全体可視を確認。native zoom、物理keyboard、実WebViewの試験ではない。
- **表示原本:** `artifacts/validation/uiux-browser-measurements-339a592a-02dc-44c1-a9e5-bcca93252c6f.json`、SHA-256 `87e7f8c461acb90d3a87a2780c27631831be4b09435e73f67b8e48561ea60394`。記録時刻`2026-09-07T10:28:56.072Z`は原本保存時刻。
- **画像:** 同directoryの`uiux-source-recovery-reviewed-20260907.png`（SHA-256 `7a6a372f6a58377f64c4e0e857cd0c8782bf05949201b37c760acfd4620ae404`）と`uiux-settings-recovery-reviewed-20260907.png`（`a60baac3f702776162b28672cb378dbf0bdf554e6b1314a4aca4cc1b178b6a0d`）。

第1回の原本は改変していない。新しいassetへ古い画像・test結果を流用せず再試験した。上記の未成立項目、任意の人手観察、実警告のproducer接続は引き続き未完了である。

## 再開後の表示再確認（2026-09-08）

最新の解析編集統合を含むproduction assetを測定し、320px幅のsource/results/settings×2themeで15pxのdocument横あふれを確認した（client305、scroll320）。rootの`min-width:320px`を`0`へ変更し、縦scrollbar差引後の幅へ縮小できるようにした。overflow隠蔽は追加せず、CSS契約testを追加。独立レビューで見つかったtestのCRLF依存も改行正規化で修正した。

修正前原本: `artifacts/validation/uiux-resume-browser-4fe371a4-6362-4216-95be-38b7b88c213b.json`、SHA-256 `ddf3fc0934d2ba4090efd1582a0ffbe8494d25174d0e81e07177c88d89ad8002`。50ケース中6ケースの失敗を保全する。

修正後原本: `artifacts/validation/uiux-resume-browser-806d44cc-78c1-472b-95d1-337c662bdd4b.json`、SHA-256 `21f7e3b7bac0983243b25079a2dd2ba86b2f51e8b8374e82358095301710d899`。保存時刻`2026-09-08T02:45:55.451Z`。Web/App manifest共通SHA-256 `d60f1ff6c1fe06477bbfaca88e82f01e01e2b7811a8461c0f5af78331edd834c`を最終全回帰後にも照合した。

| 測定 | 修正後の記録 | 制限 |
|---|---|---|
| 明暗×5サイズ×5画面 | 50組、重複/欠落なし、document横あふれ0 | 1280×720、1600×900、1920×1080、1024×640、320×720 CSS px |
| 幅1280以上 | document縦あふれ0/30 | 小幅の縦scrollは許容 |
| 1280×720の外側pane | 通常4画面＋設定、明暗10組で縦横scroll寸法＝client寸法 | 全details展開・解析長文の内部scrollなしを保証しない |
| 設定 | 異なる123キー、保存button全件pane内 | 123回の実保存ではない |
| CSS文字32px | 検索・入力・保存・詳細・閉じるの5control全体へ到達、focus非表示なし | native zoom／物理keyboardと別 |
| 解析の未送信理由 | 切替禁止・入力保持・復帰focus・明示破棄・切替再許可がtrue | 受付済みホスト変更の永続化・undoとは別 |
| bridge/error記録 | 想定のread/pingのみ、errors空 | 任意エラーの網羅的検出保証ではない |

環境はCode1.136.1／Chrome148.0.7778.280／Electron42.10.0、**DPR1.25**、visibility hidden。修正前のDPR約1とは異なるため、同条件でCSS変更だけの因果効果を測定したとはしない。修正後の当該環境での再測定として扱う。

fixtureは既存`analysisTestFixture.ts`と同梱profile由来。実DB・実media・実jobは使用せず、native bridgeをブラウザー初期化時だけ置き換えた。production HTML/CSS/JSとCSPは無加工でloopback配信し、記録転送は別のテスト専用ページで行った。checkboxの物理click相当操作が反映されないためDOM clickを使用。無効な設定キーと上限外値の試行は成功扱いにせず、実在キー・有効値で測定して未保存入力を破棄した。保存要求は実行していない。

画像は同directoryの`uiux-source-resumed-20260908.png`（SHA-256 `a50b124a009dbe04866de6da851755fb1895a7dd38a3376aee1141c5a56c49ca`）、`uiux-analysis-resumed-20260908.png`（`71f62ffa3d4964e7ecdb987305af57d1f31a8264eabcd8f864a29cdd2ae378ea`）、`uiux-settings-resumed-20260908.png`（`4b2a2a04be552ad4e44fff3a9076ca0de4a5ad95a7b9bc37d9bbd24ee5305a70`）。fixture画像であり実成果物ではない。測定server3本は終了、4183/4184/4185 listener0を確認済み。

標準`test.ps1 -Configuration Release`はArchitecture192、Core726（skip1）、Infrastructure1006（skip25）、App1464（skip3）、Web75files/1811tests、accessibility6、assets6、worker104成功。失敗0。原本`artifacts/validation/uiux-v2-full-20260908-b.log`、SHA-256 `e514dcb9b857a99b967195948ef472c4fc0fd1dcdaf2418e552e94a836ad5998`。型/依存/format/警告エラー化build/asset検証を含む。focused再実行は重複加算しない。

実warning往復・実job・実成果物/評価/保存・明示再生成はE契約待ち。現EXEは利用者LocalApplicationDataを固定使用し、app root/DBを隔離するCLIがないため、実利用者DBでnative smokeは実行していない。利用者profileを接続しない使い捨てWindows環境等で実WebView・OS倍率・起動受入を別途実施する。任意人手表とclean OS／署名／一般提供の未評価を変更しない。[中間レビュー](../../docs/dev/reviews/ui-ux-adversarial-review.md)は最終UX-048の代替ではない。

## 実native機能・全回帰の確認（2026-09-09・現在）

実warningから対象版・修正先への往復、実job開始、成果物／評価／保存、明示再生成・retry、字幕変更の永続化と再生成への反映は接続・検証済み。旧A17の依存待ちや`noJob`を現在状態としない。手動layout等の未永続化と旧承認済みthumbnail経路の理由付き`Unavailable`は限定的な能力境界として維持し、全経路が未接続であるかのように扱わない。

### 確認済み実統合（opt-in別実行4件）

| 経路 | 結果 | 原本run ID |
|---|---|---|
| 元媒体→実ローカルASR→生成・QC→全7成果物保存→再open（39ファイルの空白修正・token同値確認後） | PASS（1件） | `observed-e2e-20260909T163411105Z-97f44d5a` |
| 明示再生成、新しい処理・journalと旧成果物の保持 | PASS | `observed-e2e-20260909T145632533Z-ba46a2bd` |
| retry、新しい処理・journalと旧成果物の保持 | PASS | `observed-e2e-20260909T145852256Z-bac8df13` |
| 既知の合成Machine cueの手動字幕編集→保存・再読込→実FFmpeg／QC | PASS | `observed-test-20260909T145059046Z-3a086056` |

最後の試験は非空の既知cueを使い、変更後の字幕投影・実encodeと旧成果物の不変性を確認したもので、実ASR由来の字幕と称しない。E2Eは**4秒の合成媒体**で、`V2-014`でもsample／提供実MOVは使用していない。性能、ASR精度、人手・知覚品質の評価ではない。旧QC失敗runと前回initial `observed-e2e-20260909T144834883Z-41b8cdcc`は履歴として保全し、最新runに重複加算しない。実native機能の成功を実GUI／物理keyboard／screen readerの受入へ拡張しない。

### 最新全回帰と確定済みの完了記録

- App全UID: `app-shards-20260909T150051Z-0e9f12c6` summary、**ALL 2410＝2402成功＋8 skip、失敗0**。8 skipは環境条件4件＋opt-in実統合4件。後者の別実行PASSを2402へ加算しない。
- Infrastructure **1337成功／25 skip／失敗0**、Core **731成功／1 skip／失敗0**、PRI hook修正後のArchitecture **209成功／失敗0**（`observed-test-20260909T163146676Z-0543b2b4`）。
- Web **84 files／2066成功**、accessibility **6/6**、assets **6/6**、Worker **169成功／1 skip**、いずれも失敗0。runner別原本は[品質記録](../../docs/dev/quality.md#2026-09-09-機能全回帰の確認現在)を参照し、focused件数を重複加算しない。
- 全回帰総計は**6926成功・35 skip・失敗0**。現行結果はmain側のshard・runner別実行の確認記録で、標準`test.ps1`の一括実行や本追補時の再実行とはしない。別実行の実統合4件は総計に足さない。
- 最終.NET format（`20260909T163633`）、PowerShell parser **35ファイル**、`pip check`、`diff check`、Web production buildはPASS。39ファイルのC# token同値は`token-verification.json`で確認済み、sourceの0-byte該当なし。Viteの約**848KB**警告は残り、性能PASSではない。
- runtime App build（`20260909T162001902Z`）は終了コード0・エラー0・警告0。`app-runtimec`のPRI **3760 bytes**とコピー済みrootを照合済み。通常MRTの`PackagingOutputs`とMSIXのPRI入力について、対象source完全一致の除外を最小試験**2/2/4 PASS**で確認した。
- UX-048最終独立レビューは閉鎖済み、未解決の重大指摘なし。対象52単位の完了条件成立を記録した後、package **`0.1.1.0`**とCHANGELOG Unreleasedを反映済み。

対象52 ID完全一致・欠落0・重複0・18 raw原本hashは[詳細実装計画§0.8.2](../../docs/dev/planning/implementation-plan.md#082-対象52単位の完了と実接続の限定検証2026-09-10-jst追補)と`artifacts/validation/final-evidence-hashes-20260909c.json`へ機械照合済みとして記録する。`UX-041/043`は除外でありPASSではない。`UX-044`は未測定、人手17項目は`PENDING`を維持する。旧`candidate-review-06`は変更せず、一般提供は`releaseApproved=false`のままとする。