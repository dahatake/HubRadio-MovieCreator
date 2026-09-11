# UI/UX実装契約（UX-003）

2026-09-10 JST更新。要求追補と[ADR-0011](../decisions/ADR-0011-compact-workflow-and-settings-file.md)の実装用境界。**UX-041／UX-043を除く対象52単位は達成済み**。限定合成媒体での実ASR・生成・QC・納品／再読込・再生成・復旧、手修正字幕の実レンダー、全回帰、最終静的検査、実行用Appへのpayloadコピー照合を完了し、最終独立レビューは重大指摘なしで閉鎖した（§6）。未測定品質・GUI受入や製品全体の完了とは別判定とする。

現在の記録は本書§6、[詳細実装計画](../planning/implementation-plan.md)、[品質記録](../quality.md)に恒久化する。2026-09-08の実行経緯と開始条件は当時の履歴として本文に要約し、ルートの一時作業領域へは参照しない。App packageは`0.1.1.0`へ更新され、[CHANGELOGのUnreleased](../../../CHANGELOG.md#unreleased)へ反映済み。版更新後の検査・最終機械監査は主作業の確認予定で、本同期では実行・再測定していない。`releaseApproved=false`を維持する。

## 1. 状態と画面

- `source`／`processing`／`results`／`delivery`が固定タブ。`settings`は独立した表示先。設定を閉じると元タブとfocusへ復帰。
- `draftId`、実`projectId`、存在する`jobId`、実`versionId`を区別し、未取得はnull。placeholder IDや初期値から処理成功を推測しない。
- rootの既存providerを維持。stateful editorは必要に応じmountedのままhiddenにし、visible時だけfocusする。非表示videoはpause・時刻保持。
- 通常のID入力は選択contextへ置換。未取得のtargetでは読み込みを実行せず、短い未選択／未接続表示にする。単独componentの既存テスト経路は維持する。
- source-onlyは下書きへ固定した設定を使う。`project.finalizeDraft`のnative経路はcoordinatorへ受付を委譲し、案件・下書き・実行・実jobを同一transactionで確定して処理を開始する。フロントから別のjob開始要求は送らず、`pipeline.read`の実snapshotで受付・進行を確認する。finalizeの応答だけを処理完了とはしない。
- warning移動先はpreview時刻、setting key、evidence、editorの有限集合。targetの案件・版・digestがなければ移動不可の理由を表示する。

## 2. 設定scopeと保存

- 全体scopeは`projectId = null`、案件scopeは実際のcanonical UUID。UIは文字ラベルで区別する。nullはglobalを表し架空案件ではない。
- 既存`settings.read/update/reset/resetAll`の案件要求・応答は後方互換で維持する。globalは別methodへ追加。
- snapshotの既存field（profileId/name/version/digest、initialSnapshotDigest、snapshotDigest、revision、updatedAt、definitions、initialValues、values、definitionCount）を再利用。globalだけprojectIdはnull、initialValuesはprofile初期値とする。
- 通常保存は1項目。draftをkey別にstateへ保持し、1保存の応答で他draftを消さない。scope変更・全resetで破棄する場合は確認する。
- mutation identityはexpectedRevision、expectedProfileId、expectedProfileVersion、expectedProfileDigest、expectedSnapshotDigestを全て要求する。案件にはprojectIdも要求。
- 値は既存wire形式のboolean／integer／rational／text／textList。Int64は文字列。設定の型・範囲・allowlistは既存profileで検証。
- global revisionと案件revisionを相互に流用しない。保存済みとconsumerへ適用済みを区別し、未接続の消費機能やcloudを有効化したように見せない。

## 3. 実装済みの有限settings message

| method | params | result |
|---|---|---|
| `settings.global.read` | 空object | 既存同様のstatusとglobal snapshot |
| `settings.global.update` | mutation identity＋key/value（projectIdなし） | statusとglobal snapshot |
| `settings.global.reset` | mutation identity＋key（projectIdなし） | statusとglobal snapshot |
| `settings.global.resetAll` | mutation identity（projectIdなし） | statusとglobal snapshot |
| `settings.file.export` | projectId（nullable）＋mutation identity＋expectedFileSha256（nullable） | status、snapshot、fileSha256 |
| `settings.file.preview` | projectId（nullable）＋mutation identity | status、snapshot、fileSha256 |
| `settings.file.apply` | projectId（nullable）＋mutation identity＋expectedFileSha256（必須hash） | status、snapshot、fileSha256 |

file応答のsnapshotは、export/apply成功時は保存済み状態、preview成功時だけ読込候補値を持つpreviewであり、stateの保存済みsnapshotを置換しない。previewのrevision/initialValuesは対象の現在状態に結合し、values/snapshotDigestだけが候補を表す。file以外の応答へfileSha256は加えない。

file statusは既存のavailable/forbidden/notFound/unbound/conflict/invalid/unavailableに、fileNotFound/profileMismatch/snapshotMismatch/overwriteRequiredを追加する。失敗はsnapshot=null。overwriteRequiredだけは現在fileのSHA-256を返して確認対象にできる。他の失敗はfileSha256=nullを基本にする。raw path、file本文、例外本文、SQL、任意commandは送らない。

最初のexportのexpectedFileSha256=nullは「未存在なら作成」を意味し、既存fileはoverwriteRequiredで止める。確認後は返されたhashを指定する。確認後に変わったfileはconflict。applyもpreviewの元bytesのhashと対象mutation identityの両方を再検証する。

settings系の要求64KiB／応答4MiBの現行上限を維持する。全体・案件・fileの応答を共通clientでrequestId/method/scope/identityと照合し、古い応答を破棄する。

## 4. file境界

- nativeがapp rootと固定名`setting.txt`を所有する。Workspace外、EXE/cache外。boot時には読まない。
- 出力は保存済みsnapshotの整形UTF-8/BOMなし/LF。raw4MiB以下、canonical1MiB以下を両方検査する。
- readerはraw bytesを有限に読み、厳密UTF-8、先頭BOM1個、文字列外空白を正規化して既存CanonicalJson・DefaultSettingsSnapshot.ParseCanonicalへ渡す。
- profile mismatchとsnapshot contentDigest mismatchは別に拒否。手編集のためのignoreDigestは設けない。
- `fileSha256`は元bytesそのもの。read中の変化、reparse、非通常file、不正rootを拒否。fileACLは現在actor/SYSTEM/Administratorsだけを想定し、既存app rootのACLを変更しない。
- 書込は同directoryの一意な一時fileと安全な置換。失敗・取消は旧file/DBを保持。cleanupは自身の一時fileだけ。応答断では再読込による結果確認を優先する。
- previewはDB非更新。applyは既存storeのOCCで1回更新し、元bytesとtargetが変われば拒否。restoreではexportを変更せず、復元DBのprofileを再照合する。

## 5. 表示とCSS

標準1280×720 CSS pxの通常画面・設定1pageはpage scrollなし。長文/timeline/拡大は例外。初期の設定一覧は1page6件、詳細は1項目を基本とし、実画面で収まりを検証する。page件数を利用者optionへ増やさない。

色は現行`--cp-*`を使い、contrastのため調整済みのtokenを戻さない。style追加はfeature-local CSSをTSXからimport。生成CSSに@importやremote参照を残さない。iconは必要なFluent JSX subset＋可視ラベル。

## 6. 実接続と最終検証の現在状態

| 契約 | 現行sourceの実装・配線 | 観測済みの検証（下記実測①〜④） |
|---|---|---|
| E-START | `ProjectCreationService` → `SourceOnlyApplicationRuntime` → `SourceOnlyPipeline`。`FinalizeSourceOnlyWithExecution`で案件・下書き・実行・jobを原子的に確定し、実Workerを含むpipelineを開始 | ①で実Worker・実ASRから後続処理までPASS。②③で再生成・復旧後の実処理もPASS |
| E-EVALUATION | `SourceOnlyOutputEvaluation`へ実`OutputQc`を注入。実観測、評価record、出力state/policy、SQLite保存を接続し、結果DTOをUIへ供給 | ①〜③で実出力のQC・評価保存までPASS。④で手修正字幕の実レンダー・QCもPASS。評価engine単体を媒体測定器とは扱わない |
| E-RESULT | `StandardArtifactGenerator`、`SourceOnlyResultService`、`SourceOnlyDeliveryService.DeliverAsync`を接続。実artifact・評価・社内限定ローカル納品証跡を検証して表示 | ①で7成果物のQC・社内限定ローカル納品・保存／再読込までPASS。②③で再生成・復旧後の結果も検証済み。レンダー到達だけによる成功判定ではない |
| E-REGENERATE | 保存済み設定snapshotと字幕編集headを受付時に固定し、親版から子版・実job・結果へ接続。旧版・旧ファイルを保持 | ②の再生成、③の復旧・再試行、④の永続保存した手修正字幕の投影・実レンダーがPASS。旧成果物の保持を検証済み |

### 実測結果と完了範囲

以下は2026-09-09に観測・提示された結果であり、この文書更新で再実行したものではない。

| 実測 | 検証範囲 | 観測ID・結果 |
|---|---|---|
| ① 初回実処理 | 実ASR→7成果物レンダー→QC→社内限定ローカル納品・保存／再読込 | `observed-e2e-20260909T144834883Z-41b8cdcc`：PASS |
| ② 再生成 | 保存済み条件からの子版生成・QC・保存／再読込、旧成果物保持 | `observed-e2e-20260909T145632533Z-ba46a2bd`：PASS |
| ③ 復旧・再試行 | 復旧後の再試行から実処理・QC・保存／再読込 | `observed-e2e-20260909T145852256Z-bac8df13`：PASS |
| ④ 手修正字幕 | 字幕編集の永続保存・再読込・投影、実レンダー・QC、旧成果物保持 | `observed-test-20260909T145059046Z-3a086056`：PASS |

①〜③は限定的な合成媒体で実Worker・実ASRを動かした結合検証であり、**実MOVのASR精度や一般的な映像・音声の品質を測定したものではない**。④は既知の合成字幕Cueを手修正した実レンダー検証で、実ASRの認識精度を示さない。知覚品質・理解・法的判断・GUI／native OS受入の未測定事項は別に保持する。

**履歴:** これらより前の「7成果物レンダー後にQC失敗」は修正前の記録であり、現在の未解決E2E状態ではない。

| 回帰検証 | 提示済み結果 |
|---|---|
| App全体 | 2,410ケース：2,402 PASS／8 skip／0 fail |
| Infrastructure | 1,337 PASS／25 skip |
| Core | 731 PASS／1 skip |
| Architecture | 209 PASS（PRI回帰追加後） |
| Web | 2,066 PASS |
| Worker | 169 PASS／1 skip |
| accessibility／assets | 各6 PASS |
| **合計** | **6,926 PASS／35 skip／0 fail** |

Appの2,410ケースには環境依存4件とopt-in実統合4件の計8 skipを含む。**別実行でPASSを確認したのはopt-inの4件**で、環境依存4件をPASSへ変更しない。全体の35 skipは環境条件31件＋opt-in 4件であり、別実行を成功数へ重複加算しない。①の書式修正後の再確認も`observed-e2e-20260909T163411105Z-97f44d5a`でPASSとなった。

最終C# formatは`final-static-format-20260909T163633167Z`で成功し、39ファイルの構文トークン不変（説明コメント1行以外は空白修正）を確認済み。残る静的ブロッカーはない。runtime結合出力`artifacts/validation/app-runtime-20260909c`は警告0・エラー0でビルド完了し、全6 rootのコピーを照合済み。Web manifest SHA-256は`8572ef7b0d75db6b83b4f2164f315ca4cb80371d6f8eee37e0322b15497d5134`。PRI修正はruntimeデータだけを通常MRT走査から除外し、WinUIのXBF／PRIとcopy／publishを維持する。実行用Appの.NET assembly版`1.0.0.0`はpackage版`0.1.1.0`とは別管理である。

確認sourceは`src/HubRadio.App/Projects/SourceOnlyApplicationRuntime.cs`、`SourceOnlyPipeline.cs`、`SourceOnlyPipeline.Subtitles.cs`、`SourceOnlyOutputEvaluation.cs`、`SourceOnlyResultService.cs`、`SourceOnlyDeliveryService.cs`、`src/HubRadio.App/Web/SourceOnlyBridge.cs`、`SourceOnlyMedia.cs`、`src/HubRadio.Infrastructure/Database/SourceOnlyExecutionStore.cs`。実行依存の検証・復旧が未完なら開始を拒否し、legacyのfinalize-onlyへfallbackしない。readは保存済み実行・結果を再検証し、制御操作は準備済みpipelineを要求する。

**現在の必須範囲は全54実行単位からUX-041／UX-043を除く52単位で、すべて達成済み**。必要なbackendを含む実接続、全回帰・静的検査・資源結合と最終独立レビューを根拠とし、配線や個別試験だけによる判定ではない。旧A17の待機条件は失効している。UX-041／UX-043は明示的対象外で、環境ブロッカーではない。人の知覚・理解、法的判断、native OS受入の未測定事項は別管理し、V2全体・一般提供の合格へ拡張しない。

### 実装済みpipeline API

| method | params | 操作境界 |
|---|---|---|
| `pipeline.read` | `projectId` | 最新実行のphase、実`rootJobId`／`ingestJobId`、設定digest、実版と親版、結果・前回親版の結果、失敗codeを返す |
| `pipeline.regenerate` | `projectId`、`expectedVersionId`、`expectedVersionDigest`、`expectedSettingsRevision`、`expectedSettingsSnapshotDigest` | 現行親版・保存設定・整合性を再照合して子版生成を受け付ける。字幕の子版を主レンダー親版へ代用しない |
| `pipeline.retry` | `projectId`、`executionId`、`expectedRevision` | 最新の失敗・取消済み実行で、結果も版もまだない場合だけ再試行 |
| `pipeline.cancel` | `projectId`、`executionId`、`expectedRevision` | 対象・revisionを照合し、pipeline所有処理の終了・後始末を待つ。合成した取消成功を保存しない |

要求はclosed schemaで検査し、actor・権限・path・設定値・承認をWebから受け取らない。応答statusは`available/notFound/conflict/forbidden/unavailable/invalid`。結果の`none/valid/invalid`を実行phaseや評価PASSと混同しない。`invalid`の診断用参照は媒体利用を許可せず、未取得IDを架空値で埋めない。

### 現在のUI合成・警告移動

`WorkflowWorkspace`は`PipelineProvider`の実snapshotからjob・版・親版比較contextを作り、`ResultGallery`へ検証済み結果と実warning・callbackを渡す。作成予定だけの`noJob/noVersion/noComparison`前提は現行経路に適用しない。今回結果・前回親版の保持表示・未生成を区別し、保存タブは実納品証跡に基づく。再確認が必要な結果や処理中の旧結果から変更操作を許可しない。

警告は案件・実行・版／digest・artifact／digest・警告identityを照合し、対象を読み込んでからseek/focusし、戻る際も結果・設定・版を再照合する。元動画はproxy根拠とseek応答、出力動画は対象mediaとseek完了を確認する。字幕警告は対象版を厳密に読み、実cueの結び付きを確認する。通常の最新保存版読込とは分け、別版・空cue・別の道具へ置換しない。dirty／pendingの入力・DOM・選択・undoと元のbindingを保持し、遅延応答・不一致・対象なし・timeoutを成功扱いしない。子版へ進んだ後は元警告と同じ版として選択復元しない。UX-039Bの照合処理は接続済みだが、native実画面の最終受入完了とはしない。

### 字幕の永続保存と再生成

`DurableSubtitleEditService`と`SubtitleEditStore`はSQLite **schema 12（`012_subtitle_edits`）**へ手動字幕編集の子版・journalを原子的に保存し、commit後にACKする。再読込・再起動時はrootとjournalのdigest・順序・来歴を検証してreplayする。通常の`layout.read`は版／digestのpinと`latestInBranch: true`で同じブランチの最新保存headを取得し、警告のexact読込ではこのflagを付けない。

応答の`durableSubtitles`（`rootVersionId/headVersionId/headVersionDigest/sequence/journalDigest`）とsnapshotを照合して、字幕編集の`persisted`を表示する。過去版のexact読込や古い要求の冪等replayを最新headとは扱わない。**レンダー後の字幕編集は「字幕変更は保存済み／動画は変更後未生成」**であり、元の動画・字幕ファイルは不変。明示的な再生成で保存設定snapshotと字幕journalのhead／prefixを固定し、子版へ投影して新しい成果物を生成・再評価する。後続編集を進行中の生成へ混入させない。

### 当初契約内の利用不可機能

source-onlyの生成済み字幕workspaceは字幕専用として公開し、**手動layout・画角・keyframe・terop編集／永続化は提供しない**。legacy runtimeのメモリ内機能を字幕journalの永続保存と同一視しない。`ThumbnailApplicationRuntime`の旧承認テンプレートproviderも既定では利用不可のまま。一方、`StandardArtifactGenerator`の**標準自動thumbnailは社内限定で接続済み**であり、旧承認経路とは別。設定の勝手な変更や架空の`BrandApprover`承認で利用可能に見せない。

これは当初UX-035／UX-050で認められた理由付き利用不可表示の境界である。両タスクは52必須タスク内に残り、新しいブロッカー・対象外の追加でも「手動機能実装済み」の主張でもない。過去版復元UI/RPCは引き続きV2-012（R-003再受入）の責務であり、タブや警告の「戻る」をundo・過去版復元とはしない。

### 解析編集の既存境界

解析（UX-032）は`WorkflowWorkspace`から`AnalysisPanel`へ`readOnly`を渡さず、`onDirtyChange`による`analysisDirty`を案件切替のguardへ含める。既存の有効な解析snapshotと権限がある場合は、候補の採否・トピック編集を利用できる。タブ・道具の往復では項目別の未送信入力、ACK待ち、エラーを保持し、成功応答は対応する送信入力だけを解消する。競合や読込失敗で保持した入力を黙って破棄・再送しない。

ホスト受付済みの解析の採用・編集はメモリ内のstaged changes（`persisted = false`）であり、自動保存でも永続化完了でもない。入力guardと明示的な破棄は未送信入力を対象とし、受付済みstaged changesを取り消すものではない。確認sourceは`AnalysisPanel.tsx`、`analysisState.ts`、`src/HubRadio.App/Web/AnalysisBridge.cs`。実pipelineからの解析データ供給と、その後の手動解析変更の永続化は別である。字幕の永続保存を解析変更へ拡張せず、fixtureによる成功表示や任意の既存job/版を不足したbindingの代わりに使わない。