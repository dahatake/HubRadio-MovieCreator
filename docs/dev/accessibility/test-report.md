# U-015 / Q-009 アクセシビリティ評価記録（旧受入と版0.2への対応）

- 評価日: 2026-09-02
- 対象: `HubRadio.MovieCreator` の主要 Web UI flow
- 基準 commit: `89e67fc14bbf058a6597e32783461f701dd98d30`
- 要求: `NFR-005`、実装計画 `U-015` / `Q-009`

現在のUI/UX枝は、利用者指定で`UX-041/043`を除いた**対象52単位すべて完了**。追加証拠は本書末尾の2026-09-09節を参照する。除外項目をPASSにせず、旧受入のPENDINGと未実施の人手評価は変更しない。9月7日・8日の記録は各日時点の履歴であり、当時の未接続・native smoke未実施を現在の機能未完了へ読み替えない。

> **HUMAN EVALUATION: PENDING**
>
> **U-015 acceptance: PENDING / NOT ACCEPTED**
>
> 本記録の自動試験とブラウザー自動操作だけでは U-015 / Q-009 を受入済みにしない。代表素材、実 WebView2 host、物理キーボード、実表示環境を使った人手確認が完了するまで release gate を開けない。

> [!IMPORTANT]
> 上記3行と人手17項目の`PENDING`は、要求版0.1に対する2026-09-02時点の歴史的事実であり、`PASS`へ変更しない。一方、要求版0.2と[ADR-0009](../decisions/ADR-0009-source-only-defaults-and-automated-evaluation.md)は、人手確認を社内限定quick startの必須release gateにせず、自動判定可能項目を`Automated proxy`として受け入れる。これは「人手評価が完了した」という意味ではない。版0.2のquick startと設定UIは本記録後に実装されたため、本書の既存自動PASSだけでそれらや`V2-010/V2-014`を完了扱いにしない。
>
> 本書の9月2日の自動結果は旧要求版0.1に対する既存Web UIの証拠である。後から実装された版0.2のquick start（`V2-005`）、設定画面（`V2-006`）と、当時未実装の成果物・任意確認画面（`V2-012`）を対象にしていない。後段の追加証拠と分離し、既存442 test等を新UIの合格証拠へ転用しない。

## 判定

| 評価面 | 状態 | 根拠 |
|---|---|---|
| 自動 keyboard interaction | PASS | 主要 flow の focused control に対する Enter、Space、矢印、Home、End、Delete と form 入力を component test で実行 |
| focus の authored CSS contract | PASS | 共通 `:focus-visible` が 3 px outline / 3 px offset を宣言することを専用試験で確認 |
| light / dark token contrast | PASS | 通常文字と filled control の対象 token pair が 4.5:1 以上であることを専用試験で確認 |
| theme-aware description text | PASS | `.description` が固定色でなく `--cp-text-muted` を使うことを専用試験で確認 |
| 非色依存の機械確認 | PASS | visible text、`aria-current`、`aria-selected`、`aria-pressed`、role/status と形状・境界を併用する状態を試験 |
| subtitle control の自動操作 | PASS | 表示切替、表示 profile / variant、Cue 編集と revision 作成を keyboard interaction で確認 |
| mute control の自動操作 | PASS | visible label、accessible name、`aria-pressed`、`video.muted` の同期を確認 |
| 人手による keyboard / focus / color / subtitle / SDH / mute 理解 | **PENDING** | 未実施 |
| U-015 総合受入 | **PENDING / NOT ACCEPTED** | 人手評価が必須 |

### 要求版0.2での読み方

| 証拠 | 版0.2での状態 | 制限 |
|---|---|---|
| 既存keyboard component test | `Automated proxy evidence: reusable` | 新quick start・settings・results UIは未対象 |
| authored `:focus-visible` contract | `Automated proxy evidence: reusable` | 実WebView2/DPIでの知覚的視認性は未評価 |
| contrast token計算 | `Automated proxy evidence: reusable` | 新UIの全stateは未対象 |
| 非色依存semantic contract | `Automated proxy evidence: reusable` | 人の状態理解は未評価 |
| 字幕・mute controlの自動操作 | `Automated proxy evidence: reusable` | 新しい字幕profile、実動画の理解・可読性は未評価 |
| 人手17項目 | `HUMAN EVALUATION: PENDING` | 任意評価として残し、自動PASSへ変換しない |

版0.2の正式な自動代理評価は`V2-010`で規則版、実行環境、入力・出力digest、`PASS/WARNING/FAIL/NOT EVALUATED`を新しいevidenceへ記録する。本書の旧総合受入欄は変更しない。

## 自動試験結果

2026-09-02 に Windows 11 Enterprise Insider Preview 10.0.29648（64-bit）、PowerShell 7.6.5、Node.js 24.19.0、npm 11.17.0、.NET SDK 10.0.400、Python 3.12.10 で実行した。

- Web typecheck: PASS
- Web production build: PASS（production runtime closure は React、React DOM、Scheduler の3 package）
- Vitest: **56 files / 442 tests PASS**
- 専用 accessibility contract: **4 / 4 PASS**
  - light / dark の通常文字 token pair: 4.5:1 以上
  - light / dark の filled control text: 4.5:1 以上
  - 共通 keyboard focus declaration: 3 px outline / 3 px offset
  - `.description`: theme-aware muted text token
- production asset / dependency checks: **6 / 6 PASS**
- `npm audit`: info / low / moderate / high / critical のすべて **0**
- .NET automated tests:
  - Architecture: 46 PASS
  - Core: 637 PASS / 1 environment-gated inconclusive
  - Infrastructure: 688 PASS / 19 environment-gated inconclusive
  - App: 957 PASS / 3 environment-gated inconclusive
- Python worker: **104 PASS**

環境依存で inconclusive となった .NET 試験は symbolic link、別 account ACL、非 Windows path などであり、本記録では PASS に読み替えない。

### Keyboard interaction の対象

- 案件作成 wizard: Home / End による step 移動、Enter による確定
- ジョブ進捗: ID 入力、Enter による照会
- 解析結果: 理由入力、Enter による候補採用
- preview mode: Space による字幕表示、縦横、音声のみ等の切替
- player: Enter / Space による mute 切替
- timeline: accessible element 選択と Delete
- layout / subtitle: roving focus、Tab 順、本文・理由入力、Enter による Cue revision 作成
- review / delivery: Home / End による tab 移動と selected panel の同期
- thumbnail: Enter による evidence 読込と生成開始

## ローカルブラウザー自動確認

ビルド済み `dist` を `127.0.0.1` のみで配信し、VS Code integrated browser の Chromium 148 / Electron 42.8.1（viewport 1440 × 1000、automation DPR 1.0）で確認した。偽 WebView bridge は ping と閉じた `player.read` fixture だけに応答し、`player-media.appassets.example` への媒体要求は遮断した。

観測結果:

- production CSS が外部 stylesheet として読み込まれた。
- skip link は focus 時に画面内へ現れ、solid outline を表示した。
- `#project-content` は `tabIndex=-1` を持ち、fragment activation 時の focus target になった。
- focused subtitle checkbox の状態が `true` から `false` へ変わり、`data-subtitles-visible` と一致した。
- 音声のみ切替後、映像は `aria-hidden=true` となり、「音声のみプレビュー」「映像を隠しています」という非色依存の説明が表示された。
- mute は `ミュート` / `ミュート解除`、accessible name、`aria-pressed=false/true`、`video.muted=false/true` が双方向に同期した。
- 外部媒体を遮断した状態でも player evidence と操作状態が残り、媒体未読込を visible status で示した。

### 自動ブラウザー確認の制限

- integrated browser page は自動化中に `document.visibilityState=hidden` だった。物理的な Tab / Shift+Tab とブラウザー既定の Enter / Space activation の体感・順序は、この結果から受入済みとしない。
- focus rule の成果物宣言は 3 px だが、embedded browser の計算値は host scaling の影響を受けた。実 WebView2 と対象 DPI での見た目は人手確認へ残す。
- 偽 bridge と固定 fixture を使用したため、native host、実媒体、字幕同期、実音声、SDH 内容は評価していない。
- この確認は screen reader、Windows High Contrast / forced colors、色覚多様性、人の理解度を評価していない。

## 人手評価チェックリスト

以下はすべて未実施である。担当者、OS build、WebView2 runtime、display、解像度、DPI scaling、theme、代表素材、実施日時、観測結果、修正後再確認を追記すること。

| 人手確認項目 | 状態 | 完了条件 |
|---|---|---|
| 全主要 flow の Tab / Shift+Tab 順序 | **PENDING** | 表示順と操作順が一致し、focus loss / trap / 意図しない飛越がない |
| skip link の物理 keyboard 操作 | **PENDING** | 起動直後の Tab で表示され、Enter 後に主要 content へ focus が移る |
| Enter / Space / 矢印 / Home / End / Delete | **PENDING** | native WebView2 上で各 control の説明どおり動き、誤操作がない |
| focus indicator の視認性 | **PENDING** | light / dark、100% / 125% / 150% 以上の対象 DPI、scroll / overlay 状態で見失わない |
| focus indicator の非遮蔽 | **PENDING** | viewport 端、card、dialog、sticky / clipped 領域で indicator が隠れない |
| light / dark の文字・control contrast | **PENDING** | 実描画、hover、disabled、error、selected、focus を人が確認する |
| High Contrast / forced colors | **PENDING** | Windows の対象 mode で文字、境界、focus、状態が識別できる |
| 色だけに依存しない状態理解 | **PENDING** | warning / error / pending / selected / approved / muted を文言・形状・semantic から区別できる |
| 字幕表示切替の理解 | **PENDING** | 字幕あり / なし、burned-in / subtitle-free、全文 / 選択、句読点表示を誤認しない |
| 字幕の可読性と同期 | **PENDING** | 代表動画で文字量、背景、配置、時刻同期、縦横表示を確認する |
| 字幕 Cue 編集 flow | **PENDING** | keyboard のみで Cue 選択、本文・時刻・style・配置、理由、確定、元時刻移動ができる |
| SDH / 非発話音 cue の内容理解 | **PENDING** | 代表素材で話者識別、音楽・効果音・重要な非発話音の必要情報が理解できるか判断する |
| mute 状態の理解 | **PENDING** | 見た目と読み上げの双方で現在状態と押下後の結果を誤認しない |
| mute / 無音での主要内容理解 | **PENDING** | 代表素材を mute しても字幕・SDH・画面説明から主要内容を理解できる |
| 音声のみ mode の理解 | **PENDING** | 映像が隠れていること、再生位置・波形・mute 操作が継続することを理解できる |
| zoom / reflow / 320 px 幅 | **PENDING** | 主要操作と情報が欠落・重複・重なりなく利用できる |
| screen reader の名称・状態・順序 | **PENDING** | 対象 reader で control 名、role、状態、status 更新、エラーが理解できる |

## 旧要求版0.1の残存gate

1. 上記の全 `PENDING` 項目を、対象となる実 Windows / WebView2 / DPI と代表動画で人が実施する。
2. failure があれば修正し、自動試験と該当人手 flow を再実行する。
3. 評価者と証跡を記録し、`HUMAN EVALUATION` と `U-015 acceptance` を明示的な承認判断で更新する。
4. Q-009 は本記録の人手確認が完了するまで未完了とする。

要求版0.1に対しては **U-015 は受入未完了であり、Q-009 の人手gateも未完了**である。この未実施事実は維持する。要求版0.2では`U-015/Q-009`を変更taskとして扱い、人手gateを閉じたと偽らず、自動代理評価と任意human observationへ分離して再受入する。

## 2026-09-07 UI/UX改善の追加証拠（UX-041/047）

上記2026-09-02の結果と人手PENDINGは履歴として保全する。以下の表は追加の失敗経路レビュー前の第1回記録である。新しい4タブ、独立設定、未保存保持を含む未commit差分に対する**機械・fixture表示証拠**であり、実動画の自動代理評価器や実WebView2の総合受入ではない。最新の再試験は本節末尾に分離する。

| 検証 | 結果 | 範囲・制限 |
|---|---|---|
| Web全回帰 | 75 files／1660 tests成功、失敗0 | jsdom等のcomponent・protocol・state試験。実geometryの代替ではない |
| accessibility contract | 5/5成功 | light/darkの対象contrast pair、focus、非色依存等の機械契約 |
| production assets | 6/6成功、build/verifier成功 | 依存3package、CSP、生成CSS/JS、manifest。新規実行EXEの起動受入ではない |
| 明暗×5サイズ×5画面 | 50ケースでdocument横はみ出し0 | production asset＋テスト専用bridge。1280以上の3サイズはdocument縦はみ出し0 |
| 標準1280×720 | 通常4画面の内容scrollなし | 元動画選択済みfixture。長い詳細や展開後の証跡まで無scrollを保証しない |
| 全123設定 | 123キー重複なし、保存ボタン全件pane内 | 同梱default値の表示試験。実DBへ123回保存した証拠ではない |
| 200%文字拡大 | 5対象control全体に到達 | CSS root font 32px、DOM focus/scrollによる確認。native zoom／物理keyboardとは別 |
| 任意の利用者観察 | NOT EVALUATED | 今回の人手理解・知覚・音声・screen reader・High Contrast試験は未実施 |

環境はCode 1.136.1／Chrome 148.0.7778.280／Electron 42.10.0、DPR 1、`visibilityState=hidden`。OS倍率・native client寸法は未測定。画面操作には合成click eventを使用したため、物理入力の既定動作まで確認済みとしない。

測定対象Web manifest SHA-256は`95a8fc59d632f72478b3ca3fcf4a1c53c8320c1ae979d606332cb37809fd6806`で、最終全回帰後のApp `WebAssets`内のmanifestと一致する。private測定JSONのSHA-256は`1fb54092c57f1a72ccc324c3181ab9530357643ec11daf75f0e319feb6761c6a`。実寸値・原本名・画像・手順は[比較・受入手順](../../../tests/Acceptance/UIUX_CompactWorkflow.md#変更後の機械確認2026-09-07)に記録した。

検出した表示問題（設定一覧の共通padding、長い値による保存操作の押出し、拡大時の内容領域縮小、開始画面の縦積み）は局所CSSで修正して再測定した。値の切捨て・極小文字・bodyのoverflow隠蔽でPASSにしていない。実警告・job・成果物のE契約は未接続であり、その操作の受入は残件である。

### 追加レビュー後の再試験

file応答によるDOM再生成と競合/timeout後のfocus消失を修正し、他の編集先へ移動した利用者からfocusを奪わず、有効な再読込または元の操作へ復帰する試験を追加した。timeline競合、Transcript/layoutの失敗後再取得、部分ACKの未送信入力保持も追加回帰した。

最新の標準runはWeb **75 files／1766 tests**、accessibility **5/5**、assets **6/6**成功、失敗0。Web/Appのmanifest SHA-256は`f58db99cfb263f0c5bbbc204bebc2c4fc983c86e6e3d8c0692bb88f9b5ead13e`。このassetで明暗50画面、全123キー、CSS文字200%の5対象到達を再測定し、前表と同じ限定範囲で問題なしを確認した。

この時点の原本名、hash、画像、未評価の境界は[追加レビュー修正後の再確認](../../../tests/Acceptance/UIUX_CompactWorkflow.md#追加レビュー修正後の再確認2026-09-07)を正本とする。旧1660件の結果を新しい1766件へ上書きしたり、人の観察を実施済みにしたりしていない。実WebView2、OS倍率、screen reader、High Contrast、人の理解はNOT EVALUATEDのままである。

## 2026-09-08 再開後の追加証拠（UX-032/041/047）

- 標準Release回帰はWeb **75 files／1811 tests**、accessibility **6/6**、assets **6/6**成功、失敗0。新しいaccessibility contractはrootが縦scrollbar差引後の幅へ縮めることを検査する。CSSの改行だけを正規化し、overflow隠蔽で通過させない。
- 最新Web/App manifestはSHA-256 `d60f1ff6c1fe06477bbfaca88e82f01e01e2b7811a8461c0f5af78331edd834c`。このassetで明暗50画面、全123設定、CSS文字32pxの検索・入力・保存・詳細・閉じるを再測定した。
- document横あふれ0/50、幅1280以上のdocument縦あふれ0/30、1280×720の外側pane10/10でscroll寸法とclient寸法が一致。123個の異なるキーで保存buttonが詳細pane内。5control全体が可視範囲へ到達し、focusがhidden/inert内へ入らない。
- 解析の理由だけの未送信入力で案件切替を禁止し、画面往復の入力保持・復帰focus・明示破棄・切替再許可の5項目を確認。実保存や解析結果の永続化を示すものではない。
- Code1.136.1／Chrome148.0.7778.280／Electron42.10.0、**DPR1.25**、visibility hidden。前のDPR約1の横あふれ失敗記録とは環境が同一ではなく、CSSだけの因果効果を断定しない。

全回帰原本SHA-256 `e514dcb9b857a99b967195948ef472c4fc0fd1dcdaf2418e552e94a836ad5998`、表示原本SHA-256 `21f7e3b7bac0983243b25079a2dd2ba86b2f51e8b8374e82358095301710d899`。原本名・画像・失敗記録・制限は[9月8日受入追補](../../../tests/Acceptance/UIUX_CompactWorkflow.md#再開後の表示再確認2026-09-08)へ分離する。

測定はテストbridge、合成click、DOM focus/scrollによる機械確認。物理checkbox clickが反映されない統合ブラウザーの制限ではDOM clickを使用した。実WebView、native zoom、OS倍率、実動画再生、screen reader、High Contrast、人の理解は未評価のまま。現EXEは実利用者rootを固定使用し隔離CLIがないため、今回のnative smokeは実施せず使い捨てWindows環境等を待つ。任意観察を新たな必須生成gateにはしない。

## 2026-09-09 機能・全回帰の追加証拠（現在）

> [!NOTE]
> **実素材に関する現在の事実（2026-09-23追補）。** 本節の「`V2-014`でもsample／提供実MOVは使用していない」という記述は、**2026-09-09時点の観測として保全**する。現在の事実は異なる。承認済み受入素材によるopt-in受入シナリオは2026-09-22に実行済みで、2026-09-23の再実行では標準成果物の生成まで到達した。ただし**`V2-014` は合格しておらず**、人手17項目の `PENDING` と `UX-044` 未測定も変わらない。なお2026-09-23に受入素材は `sample/hubradio-sample.mp4` へ変更されており、上記の実行結果は変更前の素材に対するものである。新素材でのpipeline実行は未実施であり、素材の測定値は[受入記録 §4](../../../benchmarks/results/initial-pilot.md)を参照すること。本節の日付付き観測値・件数・IDは改変していない。

**今回対象の52単位はすべて完了**。利用者指定で`UX-041/043`を除き、最終静的検査・runtime照合・記録確定とUX-048最終独立レビューまで完了、未解決の重大指摘なし。実native機能は接続・検証済みであり、旧A17／noJob／E契約未接続という状態ではない。除外項目をPASSや新たなblockerにせず、限定legacyの理由付き`Unavailable`と人手未評価を維持する。

- Web全回帰は**84 files／2066成功・失敗0**（`web-all-20260909T134816937Z-b185d9e1`）、accessibility **6/6**、assets **6/6**成功。
- Appは全UID summary `app-shards-20260909T150051Z-0e9f12c6`で**ALL 2410＝2402成功＋8 skip、失敗0**。skipは環境条件4件とopt-in実統合4件であり、後者は別実行PASS。focusedや別実行を全回帰件数へ足さない。
- 全回帰総計は**6926成功・35 skip・失敗0**。PRI hook修正後のArchitectureは209成功（`observed-test-20260909T163146676Z-0543b2b4`）。現行結果はshard・runner別実行であり、標準`test.ps1`の一括実行とはしない。
- 実ASRを含む全7成果物の保存・再openは39ファイルの空白修正・token同値確認後の`observed-e2e-20260909T163411105Z-97f44d5a`で1件PASS。前回initial `observed-e2e-20260909T144834883Z-41b8cdcc`は履歴として保全。再生成`observed-e2e-20260909T145632533Z-ba46a2bd`、retry`observed-e2e-20260909T145852256Z-bac8df13`もPASS。
- 既知の合成Machine cueを使った手動字幕編集・保存・再読込・実FFmpeg／QCは`observed-test-20260909T145059046Z-3a086056`でPASS。これは実ASR字幕の精度試験ではない。

実統合は4秒の合成媒体であり、`V2-014`でもsample／提供実MOVは使用していない。性能・ASR精度・実GUIの物理入力・人の理解／可読性を受入済みにしない。9月7日・8日のfixture geometryを最新assetの再測定結果へ転用せず、旧人手17項目は`PENDING`、`UX-044`は未測定のまま。今回の追補はmain側の確認済み結果の記録であり、再実行や新しい人手承認ではない。

最終.NET format（`20260909T163633`）、PowerShell parser 35ファイル、`pip check`、`diff check`、Web production buildはPASS。39ファイルのC# token同値は`token-verification.json`で確認済み、sourceの0-byte該当なし。Vite約848KB警告は残る。runtime App build（`20260909T162001902Z`）は終了コード0・エラー0・警告0、`artifacts/validation/app-runtime-20260909c`のPRI 3760 bytesとコピー済みrootを照合済み。MRT／MSIXのPRI入力除外の最小試験2/2/4もPASS。

全runnerの原本は[品質記録](../quality.md#2026-09-09-機能全回帰の確認現在)、対象52 ID完全一致・欠落0・重複0・18 raw原本hashの機械照合は[詳細実装計画](../planning/implementation-plan.md#082-対象52単位の完了と実接続の限定検証2026-09-10-jst追補)と`artifacts/validation/final-evidence-hashes-20260909c.json`を参照。最終hash記録（20260909c）も存在する。対象52単位の完了条件成立を記録した後、package `0.1.1.0`とCHANGELOG Unreleasedを反映済み。旧`candidate-review-06`は変更せず、一般提供の`releaseApproved=false`と人手未評価を維持する。
