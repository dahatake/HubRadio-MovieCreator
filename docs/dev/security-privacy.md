# セキュリティとプライバシー

HubRadio MovieCreator は、企業向け動画、文字起こし、出演者情報、内部資料を扱うため、ローカル優先、単一利用者、社内限定、非破壊処理を基本境界にします。

> [!IMPORTANT]
> **2026-09-10 JST現在:** Stage 0と既存基盤、要求版0.2の設定・自動decision・Composition・社内限定BGM、4タブと`setting.txt`に加え、必要なbackendと実開始・offline ASR・生成・QC・自動代理評価／出力policy・set納品・再生成・再試行を接続済みです。SQLite schema 12の字幕編集永続化と受付時snapshot captureも実装済みです。UX-041／UX-043を除く**対象52単位は達成済み**で、全回帰・静的検査・資源結合・最終独立レビューを完了しました。[詳細実装計画](planning/implementation-plan.md)、[品質記録](quality.md)、[ADR-0011](decisions/ADR-0011-compact-workflow-and-settings-file.md)を参照し、原本sample・人手・clean OS・署名・legal／release承認や製品全体の受入とは区別してください。

## 既定のデータ境界

- 元動画、代理動画、音声、frame、画像を既定で端末外へ送りません。
- 元動画を上書きしません。
- quick startは社内限定から開始し、外部destinationと直接投稿を既定で無効にします。
- closedな規則だけを可逆な自動decisionへ適用し、AIまたは自動代理評価で外部公開区分、権利、機密性、法的適合を確定しません。
- 旧区分はデータ互換のため保持できますが、外部公開候補・承認済みは初期releaseの自動処理対象ではありません。
- 下位の素材や成果物は、親より広い公開区分へ自動昇格できません。

公開区分と送信条件は[ADR-0004](decisions/ADR-0004-data-classification.md)、社内限定quick startに対する置換範囲は[ADR-0009](decisions/ADR-0009-source-only-defaults-and-automated-evaluation.md)を正本とします。

## ローカル保存

既存製品基盤はローカル固定ディスク上の管理ワークスペースを前提に実装しています。現行の`ProjectApplicationRuntime`は現在の標準ユーザー用`%LOCALAPPDATA%\HubRadio.MovieCreator\Workspace`とその`hubradio.db`を使用し、WorkspaceのACLを作成・検証します。設定UIは存在しますが、設定からの実保存先切替は未完了です。ネットワーク共有、同期folder、リムーバブル媒体を初期既定にしません。

今回の限定技術検証は隔離workspace／DBを使い、nativeアプリの起動や実ユーザーprofile・既存DBの変更はしていません。現在の必須範囲は当初54単位からUX-041／UX-043を除く52単位で、必要なbackendも承認済みです。旧A17のbackend待機条件は失効し、明示除外されたnative受入を52単位の環境ブロッカーとして再追加しません。これはnative隔離・clean OS受入のPASSを意味しません。

- 元動画、文字起こし改訂、編集版、設定snapshot、自動decision、自動代理評価、監査、納品物を正本として扱います。既存のreview/approval recordも履歴として保持します。
- 代理、波形、sprite、preview などの再生成可能物を正本と分けます。
- 容量不足時に正本を自動削除しません。
- 保持期間が未決のため、時間だけを理由とする自動削除を行いません。
- backup は manifest、size、SHA-256、SQLite `integrity_check` を確認してから完成扱いにします。

保存、削除、backup、ACLの決定は[ADR-0005](decisions/ADR-0005-storage-retention.md)を参照してください。ArtifactStore、WorkspaceRecovery、ACL、検証付きbackup/restoreは実装済みです。workspaceの実保存先切替、保持・削除操作UI、外部固定ディスクのBitLocker状態確認と保留UIは未完了です。別account ACL受入は未実施の履歴事実として残りますが、単一利用者の初期release gateではありません。

`S-003`のactive受入削除は、既存のworkspace ACL作成、path traversal防止、同一利用者のaccess checkを削除する指示ではありません。防御コードは保持し、cross-account隔離を保証済みとも表示しません。複数利用者scopeを追加するときに別account脅威分析と受入を再開します。

## 設定UIと設定fileの境界（UX-045）

### scope・表示と実処理

SQLiteを設定の正本とし、globalと案件initial/currentを分離します。`SettingsWorkspaceStateProvider`はタブの外でsessionと項目別draftを保持し、通常は1項目ずつ保存します。別項目のdraftを保存成功で消さず、破棄を伴うscope切替・reset・file適用はUIで確認します。globalは架空projectIdで表現せず、案件設定は実projectIdに結び付けます。UIの`hidden`／`inert`や確認表示はnativeの認可の代替ではありません。

`CreateSourceOnlyDraftAsync`は元動画選択後の下書き作成時にglobal snapshotを固定し、finalizeはそのsnapshotを案件へ結び付けます。以後のglobal更新・file適用は既存下書き、案件initial、Compositionへ遡及しません。「新しい動画の既定値」「案件開始時の初期値」「次回生成向け」は適用対象・予定の説明であり、開始ボタンで最新globalを再取得したり、保存だけで実動画へ適用したりする意味ではありません。source-onlyの閉じたconsumer制約も維持します。

元動画／自動編集／仕上がり／保存の4タブは表示先であり、追加の承認gateではありません。`E-START`／`E-EVALUATION`／`E-RESULT`／`E-REGENERATE`は接続済みで、finalize受付の実job、`pipeline.read`の実版・結果、実QC・納品receiptを照合して表示します。旧`noJob`／`noVersion`を通常経路の説明に使わず、未取得・不正なbindingでは理由付きで操作を止めます。予定set、DB設定保存、実生成、delivery昇格は別であり、未測定項目は`NOT EVALUATED`を保持します。接続仕様は[UI実装契約](architecture/ui-workflow-contract.md)、現在の観測結果は[詳細実装計画](planning/implementation-plan.md)と[品質記録](quality.md)を参照してください。修正前のQC失敗は履歴として残し、旧QC・review、fixture、設定試験を実render・人の確認のPASSへ流用しません。

### 固定pathとfile ACL

- nativeがper-user app root直下の`%LOCALAPPDATA%\HubRadio.MovieCreator\setting.txt`だけを指定する。Workspace、EXE隣、WebAssets、自己展開cacheへ保存せず、Webからraw pathや任意file名を受け取らない。
- [`SettingsTextFile`](../../src/HubRadio.Infrastructure/Settings/SettingsTextFile.cs)は既存path検査を再利用し、不正root、reparse point、非通常fileを拒否する。file固有のroot/SID検証は明示操作まで遅延し、起動時のfile読込・自動適用・変更監視はしない。file不在・不正・アクセス失敗をDB設定の利用停止や初期化の理由にしない。
- 一時fileは作成時から継承を遮断したprotected ACLと現在actorのownerを持ち、現在actor／SYSTEM／Administratorsだけに明示の`FullControl`を設定する。完成fileもowner・ACL・通常file属性を検証し、許可外のACLを黙って修復しない。
- app root全体や既存directoryのACLは変更しない。Workspaceのdirectory ACLが兄弟のexport fileへ継承されるとも主張しない。file操作前後の検証と同一利用者試験は、別accountでのアクセス拒否の実証とは区別する。

### strictな形式と整合性

- 出力は既存`DefaultSettingsSnapshot`形状の整形JSON、UTF-8・BOMなし・LF。raw **4 MiB以下**とcanonical **1 MiB以下**を別々に検査する。読込前のfile長と読取量・読込後の長さを確認し、canonical化後だけの制限にしない。
- 読込はstrict UTF-8を要求する。先頭BOM1個、JSON上の空白・CRLF/LF・object property順等の表現差だけをfile境界でcanonical化し、文字列値やdomain配列順を変更しない。Int64・有理数はdoubleやJavaScript number経由で丸めない。
- 既存`CanonicalJson`と`DefaultSettingsSnapshot.ParseCanonical`を使い、未知・欠落・重複property/key、深層JSON、型・範囲・順序違反を拒否する。対象DBに結び付いたprofileのID・version・digestとの一致を要求し、最新profileへの自動migrationや欠落値の補完をしない。
- profile不一致とsnapshotの`contentDigest`不一致を区別して拒否する。digestを補正して不一致入力を受理せず、Core/DB parserへ`ignoreDigest`を追加しない。`fileSha256`はBOM・改行を含む元bytesそのもののhashであり、profile/snapshot digestと別物。整形だけの変更でも確認済みfile hashは失効する。hashは出所の認証や共有の安全性を保証しない。

### 明示操作・競合・失敗

[`SettingsFileBridgeRequestHandler`](../../src/HubRadio.App/Web/SettingsFileBridge.cs)はfileや対象DBの参照より先に認可を行います。export／previewにはread、applyにはno-opでもedit権限を要求します。既存案件RPC、`settings.global.*`、fileの有限methodをclosed schemaで扱い、settings要求64 KiB／応答4 MiBを維持します。file全文やpath・SQL・任意commandをWebへ渡さず、検証済み値・identity・固定statusだけを返します。

| 操作 | 更新と確認の境界 |
|---|---|
| `settings.file.export` | nativeが対象scopeのDB保存済みsnapshotを読む。未保存draftは出力せず、UIで「未保存項目へ戻る／保存済み値だけを出力／取消」を確認する。`expectedFileSha256 = null`は新規作成だけ。既存fileは`overwriteRequired`と元bytesのhashを返し、確認後はそのhashを要求する |
| `settings.file.preview` | 検証済みfileを候補として返すだけでDB・file・案件を更新しない。応答の候補値はUIの保存済みsnapshotや未保存draftを置換しない |
| `settings.file.apply` | 元fileを再読込して期待hashを照合し、対象scope・`expectedRevision`・`expectedProfileId`・`expectedProfileVersion`・`expectedProfileDigest`・`expectedSnapshotDigest`を再検査する。差分がある場合だけ既存storeの1回のOCC更新。同値でもidentityと認可を検査し、案件initial・別scope・既存Compositionは不変 |

同一scopeのDB変更、file変更、古い対象identityは競合として拒否し、部分適用やsilent mergeをしません。preview候補のdigestを保存済み対象の`expectedSnapshotDigest`として流用せず、対象scope切替や競合後はpreviewをやり直します。全体・案件・fileの非同期応答はrequestId／method／scope／identityで照合します。

exportは同directoryの一意なprivate一時fileをflush・hash検証してから、新規なら非上書きmove、確認済み既存fileなら置換します。commit前の失敗・取消では旧fileとDBを保全し、cleanupは自身の一時fileだけです。置換後の不確実な失敗で復旧を確認できなければrollback copyを残してreconciliationを要求します。置換・DB commit後の応答断も「未更新」と推測せず、保存済み状態とfileの再読込で確認し、盲目的に再実行しません。内容、個人path、SID、秘密、例外本文を通常logや失敗応答へ転載しません。

最終照合から置換まで対象の読み取りハンドルを保持し、通常のin-place書込みを共有違反で抑止します。Windowsの置換に必要なdelete共有は許可するため、**これは原子的なcompare-and-swapではありません**。協調mutexを使わない外部のatomic renameが競合した場合は、置換成功後の実退避bytesを期待値と区別し、検証できる単発競合では実退避fileを固定名へ戻してConflictにします。原本が空でも、初回置換失敗後の未使用の空予約を復旧元と推定しません。

復旧中に別の変更が割り込む、backupのACL/サイズを確認できない、復旧が失敗する場合は、未知の退避bytesを削除せず`ReconciliationRequired=true`で停止します。残る`.setting-*.tmp`は無条件に削除・自動採用しません。通常の競合保全試験を、継続的な悪意あるrename/ACL変更・crashを含む完全なfilesystem transaction保証へ拡張しないでください。必要な再照合が終わるまで、設定fileを別ツールから同時更新しないでください。

### backup/restoreと検証範囲

既存[`BackupService`](../../src/HubRadio.Infrastructure/Backup/BackupService.cs)はDB snapshotと対象の登録artifactをbackupするため、DB内のglobal・案件initial/currentも含まれます。Workspace外のexport fileはbackup対象外で、必要な保全は利用者の明示コピーです。restoreは旧workspaceを`.hubradio.previous-*`に保持して切り替え、`setting.txt`は独立して残ります。「restoreでexportを破壊する」「fileからDBを自動復元する」とは扱いません。復元後はDB側のprofile・revisionを基準に、明示preview/applyで再検証します。

対応する検証は[`SettingsTextFileTests`](../../tests/HubRadio.Infrastructure.Tests/SettingsTextFileTests.cs)、[`SettingsFileBridgeTests`](../../tests/HubRadio.App.Tests/SettingsFileBridgeTests.cs)、[`SettingsFileIntegrationTests`](../../tests/HubRadio.App.Tests/SettingsFileIntegrationTests.cs)にあります。統合試験は一時app root・実SQLite/file/ACL・productionのsettings配線を用い、handler/store再作成、別scope不変、restore後のDB復元・export不変・明示適用を扱います。handler再作成を実アプリのprocess再起動や実WebView操作と呼ばず、この証拠をG-LIVE、別account、実動画、clean OS、署名・一般提供の受入へ拡張しません。

## 字幕編集と実納品の来歴境界（2026-09-10追補）

- `SubtitleEditStore`／`DurableSubtitleEditService`はSQLite schema 12へ字幕編集の子版・journalを保存し、commit後にACKする。root・digest・順序・来歴を再照合してreplayし、最新headと過去版のexact読込を混同しない。字幕保存済みでも動画は変更後未生成であり、元動画・旧成果物を書き換えない。
- 明示再生成の受付で保存済み設定snapshotと字幕head／journal prefixを固定し、子版へ投影する。後続編集を進行中の生成へ混入させず、設定保存だけを実出力成功としない。再試行は実失敗とその履歴を保持し、架空の成功・版・納品receiptを作らない。
- UX-035のmanual reframe／layout・keyframe等はlegacyのセッション内機能／理由付き利用不可に留める。source-onlyの字幕専用workspaceや字幕journalを汎用手動編集の永続化と呼ばない。UX-050の旧承認テンプレートによる手動thumbnailの`Unavailable`も当初の許容範囲で、両タスクは52必須単位に残る。標準自動thumbnailは実元フレームを使う社内限定・ニュートラル経路で接続し、架空の`BrandApprover`や人の承認を生成しない。

初回の全7成果物set納品／再読込、再生成、model不在の実失敗からの再試行は、2026-09-09の実offline ASR・生成・QCを使った**4秒合成動画**の限定検証で成功しました。原本`sample/20260615_024326000_iOS.MOV`は使用していません。保存字幕の実描画成功は別の4秒合成動画に既知の合成`Machine`字幕を与えた試験で、**実認識・納品E2E・人手確認の証拠ではありません**。再読込もstore／pipeline／service再作成の検証であり、native GUI再起動ではありません。ログと範囲は[詳細実装計画 §0.8.2](planning/implementation-plan.md)に記載します。

対象52単位の全回帰・静的検査・資源結合は完了し、最終独立レビューは重大指摘なしで閉鎖済みです。[詳細実装計画](planning/implementation-plan.md)と[品質記録](quality.md)に6,926成功・35 skip・失敗0と別実行の限定実統合4件成功を記録しています。App packageは`0.1.1.0`へ更新され、[CHANGELOGのUnreleased](../../CHANGELOG.md#unreleased)へ反映済みです。版更新後の検査・最終機械監査は主作業の確認予定で、本同期では未実行です。`V2-014`全体、実素材品質、clean OS、署名・legal／release承認や旧OSL状態は変更せず、`releaseApproved=false`を維持します。

## Windows identity と権限

製品基盤ではWindowsユーザーのSIDを監査主体の識別子として実装済みです。表示名だけを識別子にせず、独自passwordや認証serverを追加しません。

- 要求版0.2の利用者は現在の標準ユーザー1人です。
- AI、Python worker、FFmpeg、自動代理評価器はsystem actorであり、人のSIDと区別します。
- 旧role assignmentとapproval recordは既存データ互換のため保持できますが、社内限定quick startの必須gateにしません。
- 任意self-reviewは`CompositionVersion`の内容digestへ固定します。

旧役割とreview経路は[ADR-0003](decisions/ADR-0003-local-identity-and-review.md)、単一利用者stateの設計は[ADR-0009](decisions/ADR-0009-source-only-defaults-and-automated-evaluation.md)を参照してください。UIのnavigation stateや旧review表示の存在を、V2の評価・出力policyの結合完了とは扱いません。

## Stage 0 のローカル証拠

現在の benchmark は次を実装しています。

- fixture の実行前後 SHA-256 検査
- path、ユーザープロファイル、workspace のエラー表示からの秘匿
- 成功／失敗 JSON の schema 検証
- timeout 時の子孫 process 終了
- transcript、生成文章、音声、frame を canonical summary から除外
- 一部の private evidence directory に対する限定 ACL の設定・検証
- raw run、model、cache、動画の Git 除外

Stage 0 の仕組みは、完成した製品 workspace の権限モデルや脅威対策の代替ではありません。

## 1操作起動候補の境界

`candidate-review-06`は、bounded local technical evaluationだけの候補です。公開候補には実行候補、内容・構成の検証証跡、SBOMを置き、BuildLogsとComponentInputsを含むprivate証跡は公開candidateの兄弟ディレクトリへ分離します。これは同一利用者のローカルfilesystem上の分離であり、ACLや一般配布向けのアクセス制御を意味しません。公開候補への混入はbuilderとOSL-013 verifierで拒否します。公開証跡は候補の構成・hash・判定を照合するための最小限の情報とし、private原本を共有用の証拠へ昇格しません。

- SDKの自己展開cache、WebView2 user data、workspace／案件DB／設定はそれぞれの用途を分離する。候補payloadと実行依存のcacheを、案件データの保存先や証拠入力として使わない。
- 配布候補のWebView2はFixed Version `152.0.4191.62`を一意に指定し、検出と初期化の両方で同じ同梱Runtimeを使用する。system runtimeへの黙ったfallbackは許可しない。Fixed Runtimeは自動更新されないため、更新時は版・内容・条件を再確認した別候補を作成する。自動更新機構は追加しない。
- 実行観測は対象アプリとその子孫processに限定する。入力・展開・WebView2 user data・private証跡は既存workspaceを削除せず、runごとのbackup・hash照合・専用展開先で追跡する。portableのWebView2 user dataは実装上 `%LOCALAPPDATA%\HubRadio.MovieCreator\WebView2` 固定であり、run単位のuser-data分離を主張しない。無関係なprocess、利用者の既存workspace、既存証跡を停止・削除・上書きしない。
- 本PCで確認した候補の正常系は、production-channel clean OS、別標準ユーザー、offline、欠落・破損・容量不足・アクセス拒否、署名・MOTW・実配布の受入を証明しない。これらは未評価またはBLOCKEDのまま扱う。

候補の`releaseApproved=false`は維持する。license、notice、SBOM／Model BOM、CVE、再配布条件、法的判断、clean OS、署名、実配布、業務E2Eの各gateが未解決の間、候補を一般提供可能と表示しない。詳細なcomponent判断は[release-candidate review](licenses/release-review.md)、本PC正常系の観測範囲は[OSL-014記録](reviews/osl-014-local-host-verification.md)を正本とします。

## 製品境界の合成敵対試験

現在実装済みのclosed surfaceに対して、次の自動試験を実装しています。

- React text interpolationは合成XSS文字列をescapeし、製品Web sourceにはraw-HTMLまたはdynamic-code sinkを置かないarchitecture guardを適用する。
- WebView bridgeとPython workerはXSS、shell、filter、Unicode confusable風のunknown methodをexact allowlistで拒否し、入力文字列をresponseまたはstderrへ反映しない。
- worker CLIはoption省略を許さず、公開済みの`--health`と`--protocol`だけをexactに受理する。
- process引数はshellを使わず個別tokenで渡し、OS/runtimeが黙って変換するNULと不正UTF-16を起動前に拒否する。metacharacterと改行は単一tokenとして往復する。
- ArtifactStoreのtraversal、UNC、device namespace、ADS、reserved device、reparse pointと、LocalDiagnosticLogの本文・path・秘密非記録は既存の専用試験を正本証拠とする。
- 現在のtransition境界は任意filter文字列を公開せず、proxy/preview用FFmpeg filterは固定値または検証済み数値から決定論的に生成する。

これらは合成入力と当時の実装surfaceに対する証拠です。その後に追加した版0.2のquick start、設定、自動decision、Composition計画は個別taskで敵対試験済みです。自動代理評価と最終render／export policyも現在は実装・接続済みですが、この旧試験だけで新接続の安全性を証明したとはしません。今回52単位の最終独立レビューは完了しましたが、環境で作成不能だったreparse point、production resource閾値、製品全体の統合security受入を代替しません。別account ACLを未実施からPASSへ読み替えませんが、現在scopeの必須受入にはしません。

## Media metadata

media metadata には、位置情報、端末名、撮影時刻、comment などが含まれる可能性があります。

- raw tag 値を terminal、issue、通常 log、canonical summary へ出しません。
- 比較結果は key 名、存在有無、除去成否など必要最小限にします。
- path の秘匿だけで metadata が安全になったとは判断しません。
- 外部共有前に、成果物の metadata と権利状態を別々に確認します。

## Azure の条件付き経路

Azureは通常の処理経路ではなく既定OFFです。既存の限定評価resourceがあることをproduction必要性とみなしません。end-user機能として検討するには次の条件がすべて必要です。

1. ローカルまたは手動経路で要求を満たせないことを実測した。
2. 利用者が目的と対象の文字起こし span を選択した。
3. PII、秘密、内部 URL、認証情報、不要な固有名詞を解消した。
4. 送信 payload、送信先、region、model、保持条件を利用者が確認した。
5. 利用者がcloud機能の有効化を明示した。新規resourceが必要な場合は作成も別に明示した。

送信候補にできるのは、選択・マスク・確認済みの根拠付き文字起こし span だけです。動画、音声、画像、file path、Windows SID、内部案件 ID、全文文字起こしを送りません。

S-007では、email、内部URL/host/IP、credential形式、適用済みProject/Series辞書snapshotをローカルで候補化するscannerを実装しました。findingはrevision/utterance/source-span ID、安定した理由code、原文UTF-16 offset/lengthだけを保持し、文字起こし本文や一致した秘密を複製しません。無効Unicodeまたは走査不能時も外部出力警告を維持します。このscannerの完了は候補の解消、外部出力許可、cloud送信確認の代替ではありません。

Z-001〜Z-007で全28利用可能Regionを評価し、単一Region `japaneast`（表示名Japan East）に最小評価resourceとmodel deploymentを作成し、single-tenant desktop Entra authenticationとlocal-only payload確認境界を実装しました。API key、client secret、certificate、connection stringは作成・取得・保存していません。

Z-007の`CloudPayloadBuilder`は、利用者が指定した主範囲と直接隣接する明示contextだけをexact TranscriptRevision・辞書snapshotに対して再走査します。S-007の候補に加え、URL、path、file name、Windows SID、GUID形式内部IDをNFKCから原文範囲へ逆写像して`[MASKED]`へ置き換えます。previewは元本文やmatched値を複製せず、local provenance range、mask理由、実際に送信候補となる文字列、送信先、region、deployment、model、処理範囲、保持条件を表示します。送信候補のclosed canonical JSONはrequest内だけのevidence ID、role、masked textだけを含み、local IDや送信先metadataを含みません。payloadと表示条件を結合したdigestに一致する明示確認後、一度だけdefensive bytesを発行します。

Z-007にはnetwork sender、credential取得、payload永続化、通常log、ModelRun、response採用を実装していません。Z-008〜Z-010の実在文章候補実装、grounded validation、manual/local fallback、監査が未完了のため、実コンテンツはAzureへ送信されず、end-user cloud featureも未提供です。詳細は [ADR-0008](decisions/ADR-0008-bounded-azure-evaluation.md) を参照してください。

## 秘密情報

- API key を source、設定、SQLite、log、backup へ保存しません。
- `setting.txt`はsecret・認証情報用の設定ではなく、専用の認証設定欄も追加しません。自由入力を含むprivateな利用者データとして扱い、export成功を「共有して安全」の判定にしません。
- Azure 経路は desktop 向けEntra user認証とresource-scope RBACを使用し、process終了を越えるtoken cacheを有効化しません。
- token、認証情報、文字起こし本文を通常 log へ出しません。
- 問題調査では、必要な値をそのまま転載せず、存在、分類、hash、秘匿済み error code を優先します。

## 権利と software license

software license と、動画・画像・音楽・logo・font・model の利用権は別です。

- fixture と生成動画は、リポジトリの MIT License によって外部公開可能にはなりません。
- 権利未確認素材を外部公開用成果物へ自動採用しません。
- `Music/`は利用者が社内利用を許可したread-only libraryとしてだけ使い、使用track digestと社内限定条件を記録します。外部公開権または第三者への再配布権を推測しません。
- model／runtime は version、配布元、hash、license、利用条件を確認します。

社内法務部門という特定担当の承認はquick startのrelease gateではありません。ただし、第三者license、notice、利用規約、再配布条件、SBOM、CVE、codec/patent条件を記録・検査する責務は削除しません。

第三者依存の現在の受入記録は [第三者ソフトウェア・モデル受入方針](licenses/third-party-intake.md) を参照してください。

## 変更前の確認

- 新しい入力、log、canonical result に内容、PII、秘密、絶対 path が入らないか
- 新しい child process が shell、無制限出力、無期限待機を使わないか
- 新しい file path が担当保存境界を逸脱しないか（媒体はworkspace、設定exportはnative固定app root）。traversalや任意pathを許さないか
- 新しい自動decisionがclosed規則を越えて意味判断、外部公開区分、権利、機密性を確定しないか
- 自動代理評価をhuman evaluationまたは法的適合と誤表示していないか
- 新しい依存に version、hash、license、取得元があるか
- `Music/`を変更・外部用profileへ利用していないか
- 新しいcloud経路がローカル不達と明示有効化に結び付いているか
