# アーキテクチャ概要

HubRadio MovieCreator は、Windows 11 x64 の単一 PC で動作する、ローカル優先の動画編集支援アプリケーションとして設計しています。

> [!IMPORTANT]
> Stage 0 と Foundation、および既存のStage 1基盤に加え、要求版0.2の`V2-000`〜`V2-009`を実装・レビュー済みです。source-only quick start、版付き既定値と設定画面、可逆な自動decision、標準Composition計画、社内限定BGM policyまで実装済みです。
>
> **2026-09-10 JST現在:** 承認済みのUI/UX第2版方針を恒久化した[ADR-0011](../decisions/ADR-0011-compact-workflow-and-settings-file.md)に沿う4タブ、全体／案件設定、`setting.txt`に加え、必要なbackendと実開始・評価・成果物・納品・再生成・再試行を接続済みです。SQLite schema 12で字幕編集を永続保存し、再生成受付時の設定・字幕snapshotを固定します。UX-041／UX-043を除く**対象52単位は達成済み**で、全回帰・静的検査・資源結合・最終独立レビューを完了しました。製品全体の受入とは別であり、旧review/approval型は履歴・互換資産で、人手承認の代用ではありません。
>
> [ADR-0007](../decisions/ADR-0007-stage-0-technology-selection.md)に従い、condition-falseの話者分離・local text modelと、再評価保留のshot・face detectorを製品critical pathから外す判断は維持します。SQLite業務store、永続job/worker protocol、編集操作、durable媒体取込、proxy・preview素材、境界試験、ACL、audit、backup/restore、Python wheel hash lock等の既存証拠も、要求版0.2と矛盾しない範囲で再利用します。

![HubRadio MovieCreatorの単一利用者向けローカル優先アーキテクチャ。緑は既存実装、青は一部実装または版0.2で計画中、紫の破線は既定無効の条件付きAzure経路](system-overview.svg)

## 図の読み方

- **緑:** 現在実装・実測済みの経路
- **青:** 一部実装または Stage 1 以降に実装する製品経路
- **紫の破線:** ローカルで不達となり、利用者が送信内容と Azure 利用を明示承認した場合だけ追加する経路

図と色分けは接続更新前のsnapshotです。現在の実接続・検証範囲は以下の本文と[詳細実装計画 §0.8.2](../planning/implementation-plan.md)を優先し、図の青色だけで未実装とは判断しません。

Stage 0で評価しても採用しなかったmodelや再評価保留のdetectorは、製品構成要素として図示していません。対応する要求はtranscript規則、手動speaker割当、manual layout/keyframe、版付きsafe zoneで実装します。

旧taskの実装履歴と、新要求に対する分類・次の作業は、[詳細実装計画](../planning/implementation-plan.md)を正本とします。旧`124/203`、`139/203`、`160/203`等を現在の進捗値として使いません。

## 設計原則

1. **ローカル優先:** 元動画、音声、画像は既定で端末外へ送信しません。
2. **元動画不変:** 取込後の元動画を編集処理で上書きしません。
3. **非破壊編集:** カット、字幕、画角、音声、効果は元素材への参照として保持します。
4. **根拠追跡:** 出力要素を元発言、承認済み素材、または編集者作成要素へ逆引きできるようにします。
5. **版付きsmart defaults:** 元動画以外を安全な既定値から補完し、全設定を確認・変更・resetできるようにします。
6. **自動代理評価の正直な境界:** 機械判定できるclosed contractを自動評価し、人の知覚・理解・法的判断を実施したとは主張しません。
7. **社内限定と可逆性:** 自動decisionは社内限定の子版へ記録し、任意に確認、取消、修正できます。
8. **最小構成:** 正式な要件がないサーバー、分散基盤、汎用拡張点は追加しません。

製品範囲の決定は [ADR-0002](../decisions/ADR-0002-product-scope.md) を参照してください。

## プロセス構成

### 単一 .NET ホスト

`HubRadio.App.exe` が次を所有し、source-only pipelineまで接続しています。

- WinUI 3 ウィンドウと WebView2
- ユースケースとジョブ調整
- SQLite 接続
- 管理ワークスペース
- FFmpeg と Python 子プロセスの起動・監視

ASP.NET Core、localhost HTTP サーバー、Windows Service、別の業務 API プロセスは置きません。

OSL-019では、この構成を SDK 標準の unpackaged/self-contained・自己展開型単一ファイル候補 `HubRadio.App.exe` として固定しました。これは利用者が保持する配布ファイルが1個であることを指し、展開不要・単一プロセス・一般提供済みを意味しません。候補には .NET／Windows App SDK、Web 静的asset、profile、既存のPython runtime、FFmpeg／ffprobe、採用CPU/int8 ASR model、および Fixed Version WebView2 Runtime を同梱します。独自extractor、installer、更新serviceは追加していません。

自己展開された実行依存は SDK が管理する再生成可能な展開cacheに置かれ、候補payloadと同じく案件データではありません。元媒体、案件workspace／SQLite DB、利用者設定、WebView2 profileは展開cacheへ保存せず、既存の `%LOCALAPPDATA%\HubRadio.MovieCreator` 配下へ保持します。展開cacheの再生成や配布EXEの差替えを、これらの利用者データの削除・再作成と扱いません。

### WinUI 3 と React

- **WinUI 3:** ウィンドウ、起動、障害表示、token化するネイティブファイル選択は実装済み
- **WebView2 内の React:** secureなbundled shell、元動画／自動編集／仕上がり／保存の4状態タブ、独立した全体／案件設定画面、既存editorの選択表示を実装。画面の存在と実処理・評価・成果物への接続は下記のとおり分離する
- **通信:** origin、UTF-8 byte size、closed schema、methodを検査する型付き WebMessage。project、settings（案件・global・file）、jobs、player、analysis等のclosed allowlistだけを受理する。nativeの Ready は navigation成功だけではなく、React mount後の自動 `bridge.ping` に対するnative応答のpost成功までを待つ。JavaScript側の応答受領ACKはこの契約に含めない

React からファイルパス、SQL、FFmpeg 引数、任意コマンドは送信しません。

WebView2のprofileは`%LOCALAPPDATA%\HubRadio.MovieCreator\WebView2`へ置き、bundled entry pointは`https://appassets.example/index.html`だけを許可します。same-origin以外のresource、任意top-level navigation、popup、downloadを遮断し、raw host objectは公開しません。自己展開型候補では Fixed Version `152.0.4191.62` Runtime を同梱し、検出と環境初期化へ同じ `WebView2Runtime/` folderを渡します。portable経路でRuntimeが欠落・不正ならsystem Evergreenへの黙ったfallbackはせず、native recoveryを表示します。開発buildのEvergreen経路は維持します。

同梱Fixed Runtimeを使う自己展開型候補は、このPCでcold/warmのnative Ready、実Runtime、展開tree不変、通常終了を確認済みです。これは開発者PC上の正常系だけであり、production-channelのclean OS・別の標準ユーザー・offline、署名、MOTW／実配布経路、Runtime欠落等の異常系は未受入です。署名済み一般提供、第三者再配布条件、SmartScreen／組織policy、実WebViewでのJavaScript受領ACKも未検証です。

製品projectはWindows App SDK `2.4.0`を維持します。隔離した最小`ProgressRing` probeでは`2.4.0`がmain windowを作れず`Compatible=false`、`2.4.1-experimental`が`Compatible=true`でした。単一controlの差だけでexperimental SDKへ更新せず、現行startup画面はtext表示を使います。

### 現在の画面と実接続境界（2026-09-10追補）

[`App.tsx`](../../../src/HubRadio.Web/src/App.tsx)は[`WorkflowWorkspace`](../../../src/HubRadio.Web/src/features/workflow/WorkflowWorkspace.tsx)を表示します。4タブは表示先であり、追加の必須入力や承認gateではありません。`main.tsx`の既存providerをタブの外に維持し、stateful panelをmountedのまま`hidden`／`inert`にします。タブ移動だけでは下書きclose、設定reset、job取消・再実行を呼びません。非表示mediaはpauseし、時刻を保持します。設定を閉じると元タブと呼出し元focusへ戻ります。

設定は[`SettingsWorkspaceStateProvider`](../../../src/HubRadio.Web/src/features/settings/SettingsStateProvider.tsx)の単一session/stateで扱い、globalと実projectIdに結び付いた案件scopeを区別します。5分類・検索・ページ付き一覧と1項目detailを使い、通常保存は1項目単位です。他項目の未保存draftを保持し、scope切替や破棄を伴う操作は確認します。旧settings providerや既存review/editor経路の残存を、V2の実処理接続とは扱いません。

| 契約 | 現在の実装境界 |
|---|---|
| `E-START` | nativeのfinalize受付で案件・実行・実jobを同一transactionで確定し、`SourceOnlyPipeline`を開始。UIは`pipeline.read`の実snapshotで確認し、finalize応答を処理完了とはしない |
| `E-EVALUATION` | 実`OutputQc`→`SourceOnlyOutputEvaluation`→V2-010/011の評価record・state/policy・store→UIを接続。集約engine自体は媒体測定器ではなく、観測不足・人の理解等は`NOT EVALUATED`を保持 |
| `E-RESULT` | `StandardArtifactGenerator`→`SourceOnlyResultService`→`SourceOnlyDeliveryService`を接続。実版・artifact・QC・set納品receiptを再検証して表示し、再読込にも対応 |
| `E-REGENERATE` | 保存設定snapshotと字幕head／journal prefixを受付時に固定し、親版→子版・実job→生成・QC・納品へ接続。`pipeline.retry`も接続済み。設定・字幕の保存だけでは既存動画を更新しない |

現行`WorkflowWorkspace`は`PipelineProvider`の実snapshotからjob・版・親版比較contextを作り、検証済みの結果・警告・納品状態を表示します。旧`noJob`／`noVersion`／`noComparison`や未接続providerの記述は現在の通常経路には適用しません。未取得・不正なbindingでは理由付きで操作を止め、予定set、生成済み、保存済みを区別します。接続仕様は[UI実装契約](ui-workflow-contract.md)、現在の検証結果は[詳細実装計画](../planning/implementation-plan.md)と[品質記録](../quality.md)を参照してください。修正前のQC失敗は履歴であり、現在の未解決事項ではありません。

現在の承認範囲は当初54単位からUX-041／UX-043を除く**52必須単位**で、必要なbackendも含みます。旧A17のbackend待機条件は失効しています。2026-09-09の実offline ASR・生成・QCを通る初回全7成果物のset納品／再読込、再生成、実失敗後の再試行は成功しました。入力は**4秒の合成動画だけ**で、原本`sample/20260615_024326000_iOS.MOV`は未使用です。既知の合成`Machine`字幕による保存・投影・実描画の成功は別証拠で、実認識の成功とは扱いません。再読込はstore／pipeline／service再作成であり、native GUI受入ではありません。実ユーザーprofile・既存DBは変更せず、UX-041／UX-043を52単位のブロッカーにしません。

UX-035のmanual reframe／layout・keyframe等はlegacyのセッション内機能／理由付き利用不可の当初範囲に留め、source-only workspaceは字幕専用です。UX-050の旧承認テンプレートによる手動thumbnailの`Unavailable`も許容された境界です。標準自動thumbnailは実元フレーム・社内限定・ニュートラル経路で接続済みであり、架空の`BrandApprover`は作りません。両タスクを52単位から除外したり、汎用手動編集の永続化を完了としたりしません。

G-LIVE／最終UX-048を含む対象52単位は達成済みで、最終独立レビューは重大指摘なしで閉鎖しました。[詳細実装計画](../planning/implementation-plan.md)と[品質記録](../quality.md)に全回帰6,926成功・35 skip・失敗0と、別実行の限定実統合4件成功を記録しています。`V2-014`全体、原本sample・人手・clean OS・署名・legal／release受入やOSLの既存状態は変更せず、`releaseApproved=false`を維持します。

runtime結合出力`artifacts/validation/app-runtime-20260909c`は警告0・エラー0でビルド完了し、全6 rootのコピーと現行Web manifest（SHA-256 `8572ef7b0d75db6b83b4f2164f315ca4cb80371d6f8eee37e0322b15497d5134`）の一致を確認済みです。PRI修正はruntimeデータだけを通常MRT走査から除外し、WinUIのXBF／PRI・copy／publishを維持しています。実行用Appの.NET assembly版は`1.0.0.0`のままで、更新済みApp package版`0.1.1.0`とは別管理です。[CHANGELOGのUnreleased](../../../CHANGELOG.md#unreleased)も更新済みですが、版更新後の検査・最終機械監査は主作業の確認予定で、本同期では未実行です。

### 子プロセス

- **FFmpeg／ffprobe:** probe、代理生成、レンダー、全編 decode QC
- **Python ML worker:** ASRと、ADR-0007で採用したローカル解析

製品版では両者を .NET ホストの子プロセスとして実行します。shell は使わず、timeout、取消、子孫終了、実行ファイル hash を検査します。Python は SQLite に直接接続せず、版付き NDJSON 契約とジョブ専用 staging を使います。

自己展開型候補でも FFmpeg／ffprobe とPython workerは .NET ホストの子プロセスであり続けます。候補でWorker health/protocol、native import、FFmpeg／ffprobe、採用CPU/int8 modelのoffline loadを独立に確認していますが、これらはASR精度、動画1本から納品までの業務E2E、render／deliveryの完成を示しません。

Stage 0のshot、face、local text model用probeは不採用・保留判断の証拠として残しますが、現在の製品workerや配布依存には含めません。

PK-001ではruntime 25件とtest固有4件をexact version・SHA-256・binary-onlyで固定し、同一Windows x64 hostのfresh CPython 3.12.10 AMD64 venvへdownload済みwheelhouseだけから再現しました。これはPython runtime、ASR model、CUDA、MSIX、完全license/CVE/SBOM、release readinessの証拠ではありません。

Stage 0のPowerShell／Python検証実装に加え、Foundationでは製品ホスト、Web UI、workerの最小buildable scaffoldを作成しました。ここでのscaffold成功は業務機能の完成を意味しません。

S-014では当時のclosed surfaceだけを対象に、React text interpolationのescapeとraw-HTML/dynamic-code sink不在、bridge/workerのunknown RPC、workerのexact CLI、process argvのstrict UTF-16/NUL境界を合成入力で検証しました。その後に追加したUI、typed Render IR／FFmpeg compiler、Composition計画は各taskの専用試験を持ちますが、環境依存reparse point、別account ACL、統合security受入`Q-008`の代替ではありません。

## 永続化

### SQLite

SQLiteを次の索引と業務状態の保存先とし、実pipelineの評価・納品・字幕編集の永続化を接続しています。これを任意self-review等を含む全V2責務の受入完了とはしません。

- 案件、権限、ジョブ
- 復旧可能な作業コピー
- 不変の編集版索引
- 全体既定値と案件initial/current snapshot、自動decision、自動代理評価、任意確認
- 既存のレビュー、承認、権利、監査record
- 納品物の索引
- 取込jobのversioned canonical request/checkpoint

schema 12の`SubtitleEditStore`／`DurableSubtitleEditService`は字幕編集の子版とjournalを原子的に保存し、commit後にACKします。再読込時はroot・digest・順序・来歴を検証してreplayし、通常の最新head読込と警告のexact版読込を分けます。再生成は保存設定snapshotと字幕head／prefixを受付時にcaptureして投影し、後続編集を混入させません。「字幕変更は保存済み／動画は変更後未生成」と表示し、解析のstaged changesや手動layout／keyframe等のセッション内状態へ永続化保証を拡張しません。

単一 PC・単一書込ホストが対象であるため、共有 PostgreSQL やクラウド DB は導入しません。

### 管理ワークスペース

ローカル固定ディスク上の管理workspaceへ、元動画、代理、解析、編集版、プレビュー、レンダー、納品、証拠を保存する基盤を実装済みです。現行nativeは`%LOCALAPPDATA%\HubRadio.MovieCreator\Workspace`を使用します。初回起動時の保存先選択と設定からの実保存先切替は未完了です。保存・保持・バックアップの決定は [ADR-0005](../decisions/ADR-0005-storage-retention.md) を参照してください。

ファイルは staging へ書き、hash 検証後に同一ボリューム内で昇格します。SQLite には昇格済み成果物だけを登録します。取込workflowは完了stageごとにcheckpointを先にcommitし、再起動後は登録済みartifactのpath、size、digest、metadataとstaging境界を再検証してから未完了suffixだけを再開します。artifact昇格とcheckpointの間に不一致が残った場合は自動推測修復せず、手動reconciliationを要求します。

### 設定の正本と明示的なsetting.txt

実行時の正本は既存`DefaultSettingsStore`のSQLiteです。globalと案件は別revisionを持ち、案件initialは不変、currentだけを更新します。globalのreset先は結び付いたprofileの初期値、案件のreset先は案件initialです。fileを第二の自動同期storeにはしません。

**下書き固定と表示の制限:** [`CreateSourceOnlyDraftAsync`](../../../src/HubRadio.App/Projects/ProjectCreationService.cs)は元動画選択後、下書き作成時にglobal snapshotを固定します。finalize時にglobalを再取得せず、その固定snapshotを案件へ結び付けます。したがって「新しい動画の既定値」の変更は作成済み下書きへ遡及せず、「案件開始時の初期値」は実job開始時の再取得を意味しません。「次回生成向け」は保存だけで動画が変わる意味ではなく、明示的な再生成受付で保存snapshotを固定します。`SourceOnlyProjectCreationContract`の公開区分・匿名話者・言語・期限・templateの閉じた制約は維持され、全設定を保存できることと全consumerへの接続を区別します。固定summaryにない詳細を現在のglobal値や`long-form-1280x720`等のcatalog名から推測しません。

nativeが所有する固定保存先は`%LOCALAPPDATA%\HubRadio.MovieCreator\setting.txt`です。Workspace、EXE隣、WebAssets、自己展開cacheの外に置き、起動時自動読込・file watcher・秘密情報用設定を追加しません。[`SettingsFileBridgeRequestHandler`](../../../src/HubRadio.App/Web/SettingsFileBridge.cs)は次を分離します。

- `settings.file.export`: 対象scopeのDB保存済みsnapshotだけを出力。未保存draftは出力しないことをUIで確認する。`expectedFileSha256 = null`は未存在時の新規作成だけを許可し、既存fileはhash付き上書き確認を要求する。確認後のfile変更は競合とする。
- `settings.file.preview`: strictに検証した候補を保存済み値との差分表示へ返すだけで、DB、file、案件、未保存draftを更新しない。
- `settings.file.apply`: 元bytesの`fileSha256`と対象scope・expected revision・profile ID/version/digest・保存済みsnapshot digestを再検査する。差分があれば既存storeの1回のOCC更新で適用し、同値なら更新しない。案件initial・別scope・既存Compositionは変更しない。

[`SettingsTextFile`](../../../src/HubRadio.Infrastructure/Settings/SettingsTextFile.cs)は整形UTF-8・BOMなし・LFで出力し、raw **4 MiB以下**とcanonical **1 MiB以下**を両方検査します。読込時の表現差だけを正規化して既存strict parserへ渡し、profile／contentDigest不一致を補正して受理しません。一時fileと完成fileには現在actor／SYSTEM／Administratorsのprotected file ACLを設定・検証し、app root全体のACLは変更しません。失敗・応答断時の扱いを含む詳細は[セキュリティとプライバシー](../security-privacy.md)を参照してください。

既存backupにはDB内のglobal・案件initial/currentが含まれますが、Workspace外の`setting.txt`は含まれません。`BackupService`のrestoreは旧workspaceを`.hubradio.previous-*`へ保持して切り替え、独立したexport fileは変更しません。復元DBとfileを自動同期せず、明示preview/applyで復元後のprofile・revisionを再照合します。この境界は[`SettingsFileIntegrationTests`](../../../tests/HubRadio.App.Tests/SettingsFileIntegrationTests.cs)の一時app root・実DB/file・native handler再作成による統合試験で扱います。実アプリのprocess再起動や動画生成のE2Eとは区別します。

## 非破壊編集と追跡性

主要概念は次のとおりです。

| 概念 | 役割 |
|---|---|
| `RationalTime` / `TimeRange` | VFR を含む時刻を浮動小数点の公称 fps に依存せず保持する |
| `SourceSpan` | 元素材の時間範囲と必要な空間領域を指す |
| `OriginRef` | 元発言、承認済み素材、編集者作成要素を区別する |
| `WorkingCopy` | SQLite に復旧可能な可変 draft として保存する |
| `CompositionVersion` | checkpoint 時に作る不変の編集版 |
| `TimelineMap` | 出力時刻から元素材または編集素材へ逆引きする |
| `RenderProfile` | 縦横比、字幕、codec などの版付き出力仕様 |
| `RenderManifest` | 1回のレンダー入力、実行条件、結果を記録する |
| `DeliveryManifest` | 動画、字幕、画像、文書を編集版へ束ねる。旧承認版との結合は履歴・互換経路として保持 |

表の全概念は型として実装済みです。`WorkingCopy`のSQLite永続化、EditOperation、TimelineMap再構築、review/approval基盤、typed Render IRとFFmpeg compileに加え、source-onlyの実render job・set納品・再生成まで接続しました。根拠は上述の限定技術検証であり、個別型や接続の存在を全操作フロー・全V2受入の完了とは扱いません。

## 処理フロー

1. 利用者が元動画1本を選び、社内限定の既定設定要約に対して`編集を開始`または取消を選びます。
2. ホストがstagingへコピーし、形式、stream、PTS、全編decode、容量を検査します。
3. FFmpegとPythonが解析成果物を返し、根拠付きcandidateとwarningを保存します。
4. closedな版付き規則だけを自動decisionとして子Compositionへ適用します。曖昧な意味判断は候補のまま残します。
5. 同じCompositionから長尺、短尺、字幕、音声、BGM、サムネイル等の型付きRender IRを作ります。
6. codec、duration、A/V同期、字幕、画面領域、音量、全編decode、来歴を自動代理評価します。
7. 技術的破損または来歴欠落はblockし、知覚項目の判断不能はwarningと未評価範囲を残します。
8. QC済みの社内限定成果物だけを納品領域へ昇格し、hash、設定版、元区間、評価結果を記録します。
9. 利用者は任意にpreview、差分、根拠を確認し、修正した子版を再評価・再出力できます。

この流れは [要求定義 §7](../../requirements/common.md#7-基本処理フロー) の処理契約です。実接続のうち上述の4秒合成入力・生成集合を検証済みですが、任意動画・全成果物種類・全手動操作の受入完了を意味しません。

## 評価と任意確認の境界

社内限定quick startのstate model（V2-010/011の実評価・出力policyは接続済み。UIの表示先stateおよび納品receiptとは別）:

`Editing → AutomatedEvaluation → Ready | ReadyWithWarnings | Failed → Exported`

今回の実E2Eは`ReadyWithWarnings`と実納品receiptを別々に検証しています。納品成功から`Exported`へのstate遷移や人手承認を補完しません。

`Failed`は技術破損、来歴欠落、素材利用範囲違反等のclosedな条件に限定します。自動代理評価を`human PASS`または人手承認と表記しません。利用者は任意に`SelfReviewed`または`ChangesRequested`を記録できます。字幕、カット、音声、レイアウト、テンプレートを変更した場合は子版を作り、自動代理評価を再実行します。

旧review/approval経路は既存データの読取と履歴監査のため残せますが、quick startの必須gateにはしません。詳細は[ADR-0009](../decisions/ADR-0009-source-only-defaults-and-automated-evaluation.md)を参照してください。

## 信頼境界

| 境界 | 強制策と現在状態 |
|---|---|
| React → .NET | **実装済み:** origin、strict UTF-8 byte size、closed schema、project／settings（案件・global・file）等のmethod許可リスト。設定操作はnativeで認可・対象identityを検査 |
| .NET → FFmpeg | **実装・接続済み:** shell無効、引数配列、timeout、子孫終了、binary hash、proxyのNVENC→Media Foundation fallback、typed Render IR／allowlist compile、source-onlyの実render・QC・set納品。限定合成E2Eを全媒体・全環境の受入へ拡張しない |
| .NET → Python | **実装済み:** closed NDJSON schema、message上限、timeout、SQLite直接接続禁止 |
| ホスト → workspace | path 正規化、ID lookup、staging、hash、atomic move、Windows ACL |
| ホスト → setting.txt | **実装済み:** Workspace外のnative固定path、raw/canonical上限、strict profile/contentDigest、元bytesのhash、file ACL、確認付き置換。preview非更新・apply OCC |
| ホスト → SQLite | parameterized query、transaction、foreign key、migration 前 backup |
| ホスト → Azure | 既定無効。利用者が明示有効化した選択・マスク・確認済み文字起こしspanだけ |

データ分類と Azure 送信条件は [ADR-0004](../decisions/ADR-0004-data-classification.md)、配布と Azure の現状は [ADR-0006](../decisions/ADR-0006-distribution-and-azure.md) を参照してください。

## 初期対象外

- 同時共同編集、複数 PC 同期、マルチテナント
- PostgreSQL、Cosmos DB、Service Bus、AKS、マイクロサービス
- localhost Web API
- 無人での外部公開や媒体への直接投稿
- 多役割、人手承認、cross-account試験、社内法務承認を社内限定quick startの必須gateにすること
- ディープフェイク、発言内容の改変
- 汎用プラグイン基盤や、二つ目の実装がない Provider／Factory
- 正式な必要性が未確定の OTIO、C2PA、WORM

## 判断の優先順位

実装判断が競合する場合は、[ADR-0001](../decisions/ADR-0001-source-of-truth.md) に従い、要求定義、確認済みレビュー、Accepted ADR、技術調査、実装計画の順に優先します。
