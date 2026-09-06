# OS-onlyの1操作起動: 受入手順

- タスク: OSL-004
- 作成日: 2026-09-06
- **状態: procedure implemented / execution NOT RUN（手順実装済み・実行未実施）**
- 本書は承認済みD01〜D18に基づく手順であり、候補EXE、VM、起動、署名の検証結果ではない。各試験の初期判定は `NOT EVALUATED`。独立レビューは別工程とする。

正本: [要求定義書 NFR-009 / AC-007](../../docs/requirement-definition.md)、[ADR-0010](../../docs/dev/decisions/ADR-0010-one-action-portable-launch.md)、[承認済み計画 OSL-004・020・021 / §7.7](../../work/20260906-OneActionLaunch-ImplementationPlan.md)。既存の[起動smoke](../smoke/Test-WebViewStartup.ps1)と[bridge schema](../../schemas/web-bridge.schema.json)を再利用する。

## 1. 範囲と判定

入口はローカルディスクに保存済みの `HubRadio.App.exe` 1ファイル。取得操作は数えず、ダブルクリックまたはEnterによる**起動指示1回**から既存UIが操作可能になるまでを試す。内部の自己展開・子プロセスは許容する。後述の機能確認操作や動画選択後の「編集を開始」は、アプリ起動の1操作とは別である。

OSL-020は評価利用が許されるOSL-019の固定候補でAC-007.1〜8を実行する。署名・実配布のAC-007.9はOSL-021、動画納品・認識精度・字幕品質・AC-001〜006は既存V2業務受入の所有とし、本書の成功から推定しない。

| 判定 | 記録条件 |
|---|---|
| `PASS` | 指定条件で実行し、当該assertionを実証する証跡が揃う。負例のPASSは安全な拒否・復旧の成立であって、通常起動成功ではない。 |
| `FAIL` | 試験できたが期待と異なる。必要依存の不足、実Runtimeの不一致、データ消失、payload変更等の実結果を記録する。 |
| `BLOCKED` | 必要なVM・候補・評価許諾・観測手段がない、またはOS/組織policyが試験を拒否した。原因と再開条件を記録する。 |
| `NOT EVALUATED` | 未実施、今回の対象外、または合格値未合意。未測定値は「未測定」とし、0や推定値で埋めない。 |

**使い捨てのクリーンproduction-channel VMを用意できなければ、OS-only受入はBLOCKEDでありPASSではない。** 同一開発ホストの成功、空PATH試験、Insiderの成功では解除しない。判定は行・環境・試行ごとに残し、skip、0件実行、欠けた証跡を成功件数へ含めない。必須項目にFAIL/BLOCKED/NOT EVALUATEDが残る範囲を一括PASSにしない。

## 2. 環境を固定し、初回を汚染しない

使い捨てVMの専用標準アカウントを使う。実際の開発者profile、案件DB、モデルcache、証明書、認証情報はコピー・共有・マウントしない。開発ホストで環境変数だけを差し替えた起動を隔離の代用にしない。VM準備者の権限とアプリ実行tokenを区別する。VM基盤の構築や新しい検証frameworkは本手順の対象外。

| 前提・初回の記録 | 必須内容・条件（実行token以外は起動前に固定） |
|---|---|
| OSと出自 | サポート中のWindows 11 24H2以降、x64。edition、version、完全なbuild番号、更新channel、導入media/imageの識別子・digest、初期snapshot ID、導入履歴。production/InsiderとN/LTSC/Sモード等を明記し、未試験editionへ一般化しない。 |
| ハードウェア | CPU/割当vCPU、RAM、disk種別・filesystem・空き容量、GPUと追加GPU Runtimeの有無。実測条件であり最小要件ではない。 |
| 権限 | 起動前にWindows標準のアカウント情報で標準アカウント・管理者group非所属を確認し、実起動時に対象アプリの非昇格token・integrity levelを確認する。確認手段と結果だけを残す。「UACが出なかった」だけでは不足。token情報を取得できなければ該当assertionはBLOCKED。 |
| 導入済み依存 | Git、Visual Studio/SDK、.NET/Windows App SDK Runtime、WebView2/Edge、Node.js/npm、Python/pip、PowerShell 7、FFmpeg/ffprobe、VC++再頒布Runtime、GPU Runtime、test runnerについて存在/不存在/不明・版・導入由来を記録。OS標準搭載物と追加導入物、Python等の実体と起動aliasを区別する。OS標準搭載物を削除して「OS-only」を演出しない。 |
| offline | EXE搬入後、初回起動前にguestの外部通信経路を切る。切断方法、適用時点、試験終了までの状態を記録する。offline関連の環境変数だけを通信遮断の証拠にしない。 |
| policy・信頼 | UAC、Defender、SmartScreen、Smart App Control、組織Application Controlの実状態と不明項目、取得経路、MOTWの有無、候補の実際の署名状態。試験のための保護無効化、MOTW除去、証明書導入、policy/registry回避をしない。 |
| 候補 | OSL-019のsource commit、候補ID、EXEのSHA-256・byte数、component manifest/SBOMのdigest、評価利用許諾の証拠ID。ゲストへ渡したEXEのhashを照合。manifest等は監査側資料であり、起動の追加入力にしない。 |
| 初期データ | アプリ実行歴・依存展開・アプリ固有モデルcache・ユーザーデータがない初期snapshot。payload rootは実展開後に観測し、SDKの未確認の絶対pathを決め打ちしない。 |

次のフェーズを証拠上も分離する。

1. **A: clean初回。** OSと保存済みEXEだけで起動する。PowerShell 7、SDK、test runner、追加Runtimeを事前導入せず、script・CLI引数・手動ZIP展開・PATH等の手動設定を使わない。OS組込み表示またはguest外の観測で操作・画面・時間を記録する。検証動画は起動成立の観測後に、許諾済み非機微fixtureだけを搬入する。
2. **B: 補助検証。** Aの証跡とsnapshotを保全してから、別snapshotでOSL-014/015/016の確定手順を使用する。追加toolの版・hash・導入差分を記録し、対象EXEの同一hashと標準tokenを維持する。補助toolが不足Runtimeを補った結果はclean証拠にしない。必要なら汚染前snapshotへ戻し、保護を維持して再検証する。
3. **参考環境。** 開発ホスト直接実行、空PATHだけの隔離、Insider、出自不明imageは参考結果と明記する。同じ物理host上のVMでも、guestのクリーン性・production channelを独立に立証できた場合だけclean証拠に分類する。

## 3. 初回起動・UIのマトリクス

初回起動そのものはAで確認する。UI Automationやbridge envelopeの詳細観測に補助toolが必要な場合はBとして別記し、Aで観測した事実へ遡及混入しない。

| ID / 要求 | 操作と期待結果 | 最小証拠 |
|---|---|---|
| L01 / AC-007.1・5 | 保存済みEXEを1回起動し、他の開始操作なしで操作可能な既存画面へ到達する。installer、手動展開、依存導入/download、管理者昇格、OS再起動、Azureログイン/API key、環境設定を要求しない。OSの確認・拒否が出たら内容と追加操作数を残し、無警告1操作成功にしない。保護による停止はBLOCKED。 | Aの連続した操作・画面記録、起動回数、token、offline記録、EXEの前後hash。 |
| L02 / AC-007.2 | `BundledWebApplication` のNameが `Bundled Web UI ready`、`IsOffscreen=false`。白いwindowやhandleだけでは不可。「ホスト接続を確認」を押し「ホスト接続を確認しました」へ遷移する。詳細観測では `bridge.ping` の送受信で `version=1`、requestId/method一致、`result.protocolVersion=1` を照合する。 | Aの実画面・応答、Bの既存native UI Automation契約と秘匿済み応答要約。起動後の機能確認操作数はL01と別計数。 |
| L03 / AC-007.3 | 当該アプリに属するWebView子プロセスの実行中EXEの実path、版、SHA-256と親子関係を取得する。実展開root内の `WebView2Runtime/` と候補manifestへ照合し、必要なmoduleも同梱版であることを確認する。設定文字列・manifestの存在・OS側Edge/WebView2の版だけでは不可。system版へのfallbackはFAIL。 | 実行中childと実ファイルの対応、Runtime版・digest、rootからの相対配置。観測不能はBLOCKED。 |
| L04 / AC-007.2 | 「新しい案件」からWindowsの元動画選択へ進み、取消後に再試行する。必要時は「元動画を選び直す」を使う。許諾済み非機微fixtureを選び、「選択済み」「適用する既定設定」「社内限定」を確認する。実際に通った `project.createDraft` / `project.pickSource` の応答を対応付ける。取消を選択成功にしない。 | fixture ID/digest、実pickerの表示・取消・再選択、要求と応答の一致、profile版・digest。元動画の絶対pathやmetadata本文は残さない。 |
| L05 / AC-007.2・6 | 検証用案件のみ「編集を開始」で確定する。実 `project.finalizeDraft` 応答から得たprojectIdを「案件設定」の `settings-project-id` へ入力し「設定を読み込む」。`settings.read` が `available` で、5設定群・現在値・案件開始時の初期値・根拠・profile版を表示する。「編集」群の非通信設定1件をUIの許容値へ変更し「保存」→「再読み込み」で一致を確認、保持試験用に残す。空画面、`unbound` / `notFound` / `unavailable`だけでは成立しない。 | 実案件IDの検証用識別子、設定key・変更前後の値/内容digest・revision、`settings.update` / `settings.read` の成功応答。Bで取得した結果はBと明記。 |
| L06 / AC-007.5 | アプリが自動作成した案件/DB/設定、WebViewユーザーデータがテストguestの既存 `%LOCALAPPDATA%\HubRadio.MovieCreator` 配下にあり、SDKの実行依存treeと分離される。WebViewユーザーデータは同配下の `WebView2`。EXE隣接の `*.WebView2` やpayload内へ書かない。 | 実観測rootの用途別対応、作成先の相対path、書込先検査。開発者profileを使っていないこと。 |

UI名称は既存[MainWindow](../../src/HubRadio.App/MainWindow.xaml)、[App](../../src/HubRadio.Web/src/App.tsx)、[QuickStartPanel](../../src/HubRadio.Web/src/features/projects/QuickStartPanel.tsx)、[SettingsPanel](../../src/HubRadio.Web/src/features/settings/SettingsPanel.tsx)の契約を使う。架空のAutomationId、開発server、mock応答、DevToolsの有効化で代用しない。安全に観測できないassertionはBLOCKEDとして残す。

**開発者専用smokeの契約（OSL-014全体は未完了）:** `Test-WebViewStartup.ps1` はPowerShell 7必須。`Distribution` は `Development` / `Portable` のみを受け付ける、テストの期待値選択用引数である。候補のbuild種別に合わせて指定し、アプリの配布方式・設定・保存先を切り替える利用者向けflagとして扱わない。既存の位置引数／名前付き `AppPath` とpath解決は維持する。

| `Distribution` | `AppPath` 契約 | `RuntimeUnavailable` 時の `WebViewStartupStatus` の期待Name |
|---|---|---|
| `Portable` | Bの候補EXEを明示する。省略・空・空白はアプリ起動前に拒否し、開発buildへfallbackしない。 | `アプリの配布ファイルが不完全です` |
| `Development`（既定・開発build専用） | 省略時は従来どおり `src/HubRadio.App/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/HubRadio.App.exe`。明示指定も可。 | `WebView2 Runtime を利用できません` |

**警告:** 両モードとも実アプリを起動し、実行アカウントの通常の `%LOCALAPPDATA%\HubRadio.MovieCreator` を使用する。**Bの使い捨てVM・専用テストアカウントだけで使い、利用者／開発者の実profileでは実行しない。** 本修正はVM・ユーザー・設定を自動作成せず、保存先環境変数の差替えも隔離の代用にしない。

Ready側の `BundledWebApplication` / `Bundled Web UI ready` / `IsOffscreen=false` は変更しない。実装済みのReady guardは、navigation成功と、mount済みReactからの既存の自動 `bridge.ping` に対するnative応答のPOST成功を待つ。これはJavaScript側への応答到達・処理完了ACKではない。L02の「ホスト接続を確認」の手動クリックは起動後の再確認であり、起動1操作の成立に必要な操作ではない。Ready検査だけではL02の手動再確認・L03の実Runtime・L04/L05の操作を証明しない。cold/warm、実Runtime配置、自己展開tree不変、待ち時間の実測への対応はOSL-014の未完了範囲である。EXE隣接treeのhash比較だけでは自己展開先を検査できず、15秒のwindow/Ready待ちは製品性能要件ではない。

既存負例は `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER` を存在しないfolderへ向ける**SDK Runtime override失敗**である。Portableでも同梱Fixed Runtimeを残したままSDKの検出が失敗すると `RuntimeUnavailable` / `診断コード: webview.runtime_unavailable` になり、上表のPortable見出しを比較する。同梱fileを物理的に除いたF01の代替ではない。出力の既存 `RuntimeUnavailableRecovery` はこの診断表示の確認であり、同梱欠落からの復旧やOSL-014完了を意味しない。また、このsmokeは同じprofileでReady試行を先に行うため、DB・profile未生成の証明には使わず、F05〜F08は独立した新しいprofileで確認する。

## 4. 同梱CPU実行基盤を独立に試す（AC-007.4）

L01成功とは別に、同一候補から実展開されたファイルを使う。Bで既存[Python verifier](../../packaging/python/verify-runtime.ps1)・[runtime manifest](../../packaging/python/runtime-manifest.json)とOSL-015の確定検証を再利用する。明示した同梱EXE、payloadと異なる作業directory、**検査子プロセスだけ**の空PATH、offlineで実行し、通常利用者へ環境設定を要求しない。検証前後で実体pathとhashを照合する。

| ID | 独立assertion | 最小証拠 |
|---|---|---|
| P01: worker health | `Worker/`の同梱Pythonでmanifestの `-I -B -m hubradio_worker --health` を使用。exit 0、stderr空、応答 `{"status":"ok"}`。 | Python実体の版/path/digest、引数、終了code、health結果。 |
| P02: protocol | 同梱workerの `--protocol` へ既存v1 `worker.ping` を送り入力を閉じる。progressのsequence 1・2と最後のresponseの計3行、requestId/method一致、正常終了を確認。 | protocol版、受信件数・順序・対応の検査結果。 |
| P03: native imports | `av`、`ctranslate2`、`faster_whisper`、`numpy`、`onnxruntime`、`tokenizers` の6件を実importし、版を固定manifestと照合。隔離flag有効、pipなし、Python探索pathはruntime内。ロードされた非OS native依存の出所を確認し、開発Runtimeや追加GPU依存で補わない。 | 6件別の結果、隔離状態、module/DLLの実配置・digest、初期依存inventoryとの照合。 |
| P04: FFmpeg/ffprobe | `Tools/`の双方の実体で版を照合。非機微の短いfixtureをffprobeでstream認識し、FFmpegで全編decodeして正常終了することを確認。PATH上の別実体は使わない。 | 両EXEの版/digest、fixture ID/digest、streamの必要最小情報、終了code。 |
| P05: CPU model | [catalog](../../packaging/models/catalog.json)の `asr.faster-whisper-small.cpu-int8` の4ファイルを `ModelCatalog/` とcatalogの `bundle.relativePath` から照合。空のアプリ用model cacheから、明示的ローカルdirectory、CPU/int8、`local_files_only=True` で実load完了を確認する。ファイル存在やimport成功だけでは不可。 | model ID/revision、4ファイルのhash照合、実loadのdevice/compute type・結果、cache生成先、offline状態。 |

全項目でruntime/payloadを変更しない。保留・不同意・非採用モデルを新規取得・追加同梱・有効化せず、既存wheel内の台帳済み付随資産は固定hash・notice契約を維持する。`releaseApproved`を書き換えない。これらの成功はCPU基盤の証拠だけであり、ASR精度、実動画の納品、業務E2Eは `NOT EVALUATED` のままとする。

## 5. 終了・再展開・負例（AC-007.6・7）

保存・復旧試験はL05で作った使い捨て案件と設定を基準に、各試験を独立snapshotで行う。WebView起動負例（F01のWebView欠落、F05〜F08）は§5.1の未使用profileで行い、L05やReady試行のprofileを流用しない。元の候補は保全し、故障注入は検証用複製だけに行う。対象root・fileを実観測とmanifestで特定できなければ停止する。詳細な注入手段はOSL-016の確定手順を使用し、本書で独自extractorやpreloaderを追加しない。

| ID | 操作と期待結果 | 最小証拠 |
|---|---|---|
| R01: 終了・warm | windowを通常終了し、アプリと関連childの終了・exit結果を確認。同じEXEを再起動してL02/L03を再確認し、L05の同じ作成済み案件を読み戻す。新規作成や設定再保存で補わず、案件ID、変更済み設定値、profile/snapshot digestの保持を確認する。強制終了は正常終了成功にしない。 | 起動前後の案件・設定の意味的比較、終了結果、warm条件。DBは監査更新があるためbyte不変だけで判定しない。 |
| R02: cache再生成 | 全関連process終了後、その候補の実展開cacheだけを除去し、同じEXEから再展開する。案件/DB/設定/WebView profileは残す。アプリ管理の再生成可能なmodel cacheの除去・offline再loadも別試行にし、保存データを消さない。 | 除去対象rootの同定、再展開後のmanifest/hash一致、L02/L03、案件・設定保持、P05再load結果。TEMPやLOCALAPPDATA全体の削除は禁止。 |
| R03: read-only | 別snapshotでEXEと展開済み実行依存treeを読取/実行可・書込不可にし、ユーザーデータ領域は書込可のままL02〜L05とP01〜P05を行う。アプリがEXE・依存を変更せず動作することを確認する。ACL準備は負荷条件の設定であり、通常利用者の必須setupにしない。 | 実効書込権限、EXEの前後hash、EXE隣接treeと実展開treeの相対path/byte数/SHA-256一覧の前後比較、書込先。directoryのread-only属性だけでは書込拒否の証明にしない。 |
| F01: 依存欠落・破損 | `WebView2Runtime/`、Web asset、worker、FFmpeg、採用modelの必要fileを1対象ずつ欠落/改変させる。該当操作が失敗を返し、system Runtimeやdownloadで隠さない。UI開始前に全worker/modelを強制loadする要求ではなく、P試験で検出してよい。 | 各対象・注入種別ごとの期待/実結果、元/改変後digest、実childの出所、診断code。SDKが自己修復した場合は復元hashを実証し、拒否を観測したとは記録しない。 |
| F02: EXE破損 | 複製EXEを破損させ、起動拒否または実際の失敗を確認。正常起動の証拠とは分離する。 | 元/改変後hash、OS/Runtimeの実結果、到達段階、終了codeまたは取得不能理由。 |
| F03: 容量不足 | 実展開先、およびユーザーデータ保存先の空き容量不足を別々に再現する。成功表示や既存案件の消失がなく、原因解消後に同じ正常候補で復旧できることを確認。 | 対象領域、開始/最小空き容量、実結果、データ保持と復旧結果。開発hostのdiskは使わない。 |
| F04: アクセス拒否 | EXE実行、展開先書込、依存読取、ユーザーデータ書込の拒否を別々に再現。標準tokenのまま実結果を記録し、拒否条件を戻した後に正常候補で復旧する。 | 対象と実効権限、失敗段階・code、既存データの保持、復旧結果。OS保護の無効化とは区別する。 |

managed起動まで到達するPortableのWebView Runtime欠落は、既存 `WebViewStartupStatus` / `アプリの配布ファイルが不完全です` / `診断コード: webview.runtime_unavailable` と再取得案内を照合する。開発buildの見出しは§3のDevelopment行だけに適用する。managed entry前の容量不足・拒否・破損ではnative recovery panelを必須にせず、OS/SDKの実際の結果と対処を記録する。正常候補での保護拒否はBLOCKED。注入した負例の期待どおりの拒否とは別判定とする。

初回のSDK展開による新規file生成と、その後のアプリによるpayload変更を区別する。初回展開の実内容を候補manifestと照合し、以後の操作前後を比較する。復旧のために案件・設定を初期化して成功にしない。

### 5.1 B限定: WebView起動guardの負例

以下は**受入の期待値であり、guard・解放処理の実装完了や実行済みを主張しない**。候補で期待と異なればFAIL、必要な観測手段がなければBLOCKEDとする。Aのclean初回証拠とは分離し、既存のfocusedな補助検証だけを使う。製品へ試験flag、ユーザー設定provisioner、汎用の一括検証frameworkを追加しない。

- **負例・試行ごとに、アプリ未起動の新しい使い捨てVMテストprofileへ戻す。** 実profileを掃除して流用しない。注入前に `%LOCALAPPDATA%\HubRadio.MovieCreator` 配下の `Workspace/hubradio.db`（付随fileを含む）・設定・`WebView2`、override先、対象browserの事前状態を記録する。既存DB/profileがあれば「作成されなかった」と判定せず、未使用の試験snapshot/profileでやり直す。
- F05〜F08は、**正常な同梱Fixed Runtime全体が実展開rootに存在することを先にmanifest/hashで照合する**。その確認のために同じprofileでアプリのReady試行を先行させない。故障注入は対象の子プロセスの環境だけに限定し、機械／ユーザーの恒久環境変数、registry、OS保護を変更しない。注入しないoverrideの有無も確認し、複数の故障を混ぜない。
- Runtimeの版検出、環境／control初期化、browser生成、`ProjectApplicationRuntime.Create` による案件Runtime/DB作成、bridge開始、Ready到達を別々に観測する。事前・事後のfile状態だけでなく到達段階を対応付け、既存targetや起動後の不在だけで未初期化を断定しない。F06〜F08ではWebView profileが生成され得るため、DB未生成とprofile未生成を同一視しない。

| ID | 操作と期待結果 | 最小証拠 |
|---|---|---|
| F05: 無効Runtime override | 同梱Runtimeを残し、`WEBVIEW2_BROWSER_EXECUTABLE_FOLDER` だけを存在しないfolderへ向ける。SDK検出失敗から `RuntimeUnavailable`、§3のPortable見出しと `診断コード: webview.runtime_unavailable`。環境／control初期化、案件Runtime/DB、bridge、Readyへ進まない。F01の物理欠落とは別判定。 | 同梱Runtimeの事前照合、override先の不存在、検出失敗・初期化未到達、未使用profileの前後比較、native診断。既存smokeの診断表示部分は再利用できるが、Ready先行の一括実行では未生成を証明しない。 |
| F06: 有効な別Runtime override | 同梱Runtimeを残し、同梱root外の完全で有効なFixed Runtimeへ `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER` を向ける。許諾・hash確認済みの同じRuntimeの検証用複製でもよい。版検出・実初期化が成功した後、`CoreWebView2.BrowserProcessId` から `Process.GetProcessById(...).MainModule.FileName` で得た実EXEが同梱 `WebView2Runtime/msedgewebview2.exe` と一致せず、`InitializationFailed` になる。**案件Runtime/DB・bridge作成・Readyより前に拒否**し、`WebViewStartupStatus` のNameは `Web UI を開始できませんでした`、診断は `診断コード: webview.initialization_failed`。 | 両Runtimeの完全性・実版、実PIDとEXEの対応／path不一致、拒否段階、DB未生成、bridge/Ready未到達、generic診断。検出段階の `RuntimeUnavailable` だけではこの試験のPASSにしない。 |
| F07: user-data override | Runtimeは正常な同梱版のまま、`WEBVIEW2_USER_DATA_FOLDER` だけを、通常の `HubRadio.MovieCreator/WebView2` とは異なる専用の空で書込可能なdirectoryへ向ける。SDKの実効 `CoreWebView2Environment.UserDataFolder` と要求先の不一致を `EnsureCoreWebView2Async` 前に検出し、controller/browser生成・案件Runtime/DB作成・bridge開始・Readyへ進まず `InitializationFailed` で拒否する。期待Nameは `Web UI を開始できませんでした`、診断は `診断コード: webview.initialization_failed`。Runtime欠落の見出し／codeに置き換えず、絶対pathや例外詳細を表示しない。 | 要求先（desired）と実効user-data先（actual）の秘匿済み投影・照合結果、通常先／override先の事前・事後状態、controller/browser生成未到達、DB未生成、bridge/Ready未到達、generic診断。書込拒否だけで失敗するF04とは分ける。 |
| F08: initialize delegate失敗probe | Bの使い捨て環境で、実際に有効な同梱Runtimeを使う `WebViewStartupCoordinator.StartAsync(browserExecutableFolder, initializeWebView)` のfocused probeを行う。実 `GetAvailableBrowserVersionString(folder)` による検出と、同じfolder・新しいテストprofileでの `CreateWithOptionsAsync` / `EnsureCoreWebView2Async` を通した後、DB/bridge作成前の初期化delegateに制御した例外を注入する。期待は `InitializationFailed`、`DiagnosticCode=webview.initialization_failed`、`RuntimeVersion=null`、Ready未到達。 | 実Runtimeの版/path/hash、検出後のdelegate到達・例外注入箇所、結果と秘匿境界、DB未生成、生成したWebViewの解放。空folderと偽の版文字列だけの単体試験を実初期化証拠にしない。probe結果はCoordinatorの証拠であり、native表示はF06/F07で別途確認する。 |

**Bの失敗後・終了時の解放:** F06〜F08でWebView/browserを生成した場合は、失敗を返す経路でもWinUIの `WebView2.Close()`（本体のcontrolは `WebApplicationView`）による解放を期待する。F06/F07はnative失敗画面を維持したままbrowserが残留しないこと、F08はprobeのfinallyで解放することを確認する。事前に記録した `BrowserProcessId` と `CoreWebView2Environment.BrowserProcessExited` または対象process終了の観測を対応付け、通常のwindow終了後も当該試行のchildが残らないことを確認する。観測期限・実経過・未解放を記録し、`WebViewHost.Dispose()` のbridge/event解除や親processのexitだけをbrowser解放の証明にしない。強制終了・無関係なEdge/WebView2の一括killを成功扱いせず、解放確認前にprofileやRuntimeを削除しない。

実装の照合先: [MainWindow起動処理](../../src/HubRadio.App/MainWindow.xaml.cs)、[Coordinatorと結果契約](../../src/HubRadio.App/Web/WebViewStartupCoordinator.cs)、[既存Coordinator単体試験](../HubRadio.App.Tests/WebViewStartupCoordinatorTests.cs)、[案件Runtime/DB初期化](../../src/HubRadio.App/Projects/ProjectApplicationRuntime.cs)、[WebViewHostの終了処理](../../src/HubRadio.App/Web/WebViewHost.cs)。API参照: [WinRT CoreWebView2Environment（検出・生成・UserDataFolder・BrowserProcessExited）](https://learn.microsoft.com/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2environment?view=webview2-winrt-1.0.4129.50)、[WinUI WebView2.Close](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.webview2.close?view=windows-app-sdk-2.0)。

## 6. 時間・容量を実測する（AC-007.8）

初回coldは§2の初期snapshot、warmは同一EXE・展開cache保持・OS再起動なし、再展開はR02として分ける。各条件を最低1回記録し、反復した場合も全試行の件数・値・失敗を残す。OS再起動やOS file cache状態、計測方法・分解能・補助toolの影響を記録し、未確認のdisk cold状態を保証しない。

| 計測 | 定義 |
|---|---|
| 起動・最初の可視UI | 起動指示をt0とし、最初に実際に描画された可視UIまでと、既存native Ready契約到達までを別々に測る。startup panel・失敗画面・操作可能なWeb UIを区別する。後続ping/機能確認の成否も紐付け、window handleの出現だけでUI成立にしない。人の操作待ちは起動latencyへ混ぜない。 |
| 展開・再起動時間 | cold/warm/再展開ごとの経過時間、観測可能なら展開区間を記録する。区間を観測できなければ分解値は未測定とする。試験timeout・timeout発生を別記し、既存smokeの15秒を合格値にしない。 |
| 容量 | EXE、実展開tree、model cache、ユーザーデータ、一時展開残存物について実byte数とdisk占有量、開始空き容量・最小空き容量を記録する。EXE＋展開＋model cache＋一時領域が共存するピークを時系列で測り、別時点の各最大値を合算して実ピークと呼ばない。 |

**許容起動時間、容量上限・必要空き容量、CPU/RAM最小要件はTBD（実測後の合意対象）。** 測定完了と性能受入を分け、合格値未合意の性能判定は `NOT EVALUATED`。過去MSIXの容量から新EXEの値を作らない。未計測のピークや終了時点だけの容量を測定済みとしない。

## 7. 証跡と引渡し

OSL-020の実行担当は結果を `docs/dev/reviews/one-action-launch-acceptance.md`、証跡を `artifacts/portable-validation/<run-id>/` に保存する。これは承認済み計画上の出力先であり、**本書作成時点では生成・実在を主張しない**。本手順・実行台帳へ架空の実行結果を記入しない。

- 証拠IDは `run-id / case-id / attempt / evidence-kind` の組で一意にする。各行に環境分類、A/Bフェーズ、snapshot ID、実施日時、期待/実結果、4値の判定、証拠ID、失敗・保留・未評価の理由と再開条件を記録する。
- 全行を候補EXEのSHA-256に結び付ける。source commit、component manifest/SBOMのdigest、OS/依存inventory、権限・network・policy、fixture digest、試験手順/toolの版・hashを共通run情報で参照する。改変負例は元と改変後のhashを分ける。
- 各証拠fileには実際の保存先への参照、byte数、SHA-256を付ける。表のcase IDは試験の識別子であって、実行済み証跡IDやhashの代用ではない。
- pathは実観測値を用途別rootと相対pathへ正規化する。個人の絶対path、SID/アカウント名、媒体のGPS・撮影時刻・端末名・字幕/音声本文、秘密鍵・token、全環境変数や全command lineを共有reportへ出さない。親子processはrun内識別子で対応付ける。
- screenshot/logも非機微の必要部分だけを保存し、その保存後のbytesをhash化する。秘匿前の原本が不可欠な場合はアクセス制限した試験領域に留め、公開する投影とそのdigestを区別する。単なるpathの推測や伏字だけでRuntime一致を断定しない。
- 初回clean、補助検証、同一host/Insiderの結果を別表で集約し、各必須項目の未完了を列挙する。P試験のPASSを業務E2EのPASSに、機械的UI検査を人の可読性評価に変換しない。

## 8. 署名・MOTW・一般提供は別の未完了gate

署名主体・実配布先が未選定でも、評価利用許諾とOS policyが許すローカル技術試験は進められる。一方、未選定の実ダウンロード信頼試験はOSL-021側のBLOCKEDであり、一般提供を承認したことにはならない。

OSL-021では承認された実配布経路と対象policyを使い、取得時のMOTWを保持し、署名・信頼動作を確認する。ローカルコピーでMOTWがない試験をSmartScreenの実ダウンロード受入と表示しない。OS保護の無効化、警告回避、受信者への証明書導入を成功条件にしない。

署名はEXEのbytes/hashを変えるため、署名後の最終hashを固定し、**実際に配布する同じ署名済みEXEで、本書の起動・同梱基盤・保存/復旧・測定を再受入する**。署名前の結果を転記せず、component manifest/SBOMとの対応も再確認する。外側EXEの署名だけでWebView/Python/FFmpeg等のchild binaryの許可を仮定しない。署名・実配布・供給網gateが未完了なら一般提供は保留し、AC-001〜006や製品全体の完了とも分けて報告する。