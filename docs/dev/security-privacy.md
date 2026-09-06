# セキュリティとプライバシー

HubRadio MovieCreator は、企業向け動画、文字起こし、出演者情報、内部資料を扱うため、ローカル優先、単一利用者、社内限定、非破壊処理を基本境界にします。

> [!IMPORTANT]
> Stage 0と既存製品基盤に加え、要求版0.2のsource-only quick start、版付き設定store/UI、可逆な自動decision、標準Composition計画、社内限定BGM policyを実装・レビュー済みです。単一利用者state、自動代理評価、版0.2の最終export／delivery flowは未実装です。以下には既存境界と計画が混在するため、[詳細実装計画 §0](planning/implementation-plan.md#0-要求版02の現在baseline)も確認してください。

## 既定のデータ境界

- 元動画、代理動画、音声、frame、画像を既定で端末外へ送りません。
- 元動画を上書きしません。
- quick startは社内限定から開始し、外部destinationと直接投稿を既定で無効にします。
- closedな規則だけを可逆な自動decisionへ適用し、AIまたは自動代理評価で外部公開区分、権利、機密性、法的適合を確定しません。
- 旧区分はデータ互換のため保持できますが、外部公開候補・承認済みは初期releaseの自動処理対象ではありません。
- 下位の素材や成果物は、親より広い公開区分へ自動昇格できません。

公開区分と送信条件は[ADR-0004](decisions/ADR-0004-data-classification.md)、社内限定quick startに対する置換範囲は[ADR-0009](decisions/ADR-0009-source-only-defaults-and-automated-evaluation.md)を正本とします。

## ローカル保存

既存製品基盤は、利用者が選択するローカル固定ディスク上の管理ワークスペースを前提に実装しています。要求版0.2の初期値は現在の標準ユーザー用`%LOCALAPPDATA%\HubRadio.MovieCreator\Workspace`とし、設定画面から変更可能にする計画です。新しい既定解決とUIは未実装で、ネットワーク共有、同期folder、リムーバブル媒体を初期既定にしません。

- 元動画、文字起こし改訂、編集版、設定snapshot、自動decision、自動代理評価、監査、納品物を正本として扱います。既存のreview/approval recordも履歴として保持します。
- 代理、波形、sprite、preview などの再生成可能物を正本と分けます。
- 容量不足時に正本を自動削除しません。
- 保持期間が未決のため、時間だけを理由とする自動削除を行いません。
- backup は manifest、size、SHA-256、SQLite `integrity_check` を確認してから完成扱いにします。

保存、削除、backup、ACLの決定は[ADR-0005](decisions/ADR-0005-storage-retention.md)を参照してください。ArtifactStore、WorkspaceRecovery、ACL、検証付きbackup/restoreは実装済みです。workspace設定、保持・削除UI、外部固定ディスクのBitLocker状態確認と保留UIは未完了です。別account ACL受入は未実施の履歴事実として残りますが、単一利用者の初期release gateではありません。

`S-003`のactive受入削除は、既存のworkspace ACL作成、path traversal防止、同一利用者のaccess checkを削除する指示ではありません。防御コードは保持し、cross-account隔離を保証済みとも表示しません。複数利用者scopeを追加するときに別account脅威分析と受入を再開します。

## Windows identity と権限

製品基盤ではWindowsユーザーのSIDを監査主体の識別子として実装済みです。表示名だけを識別子にせず、独自passwordや認証serverを追加しません。

- 要求版0.2の利用者は現在の標準ユーザー1人です。
- AI、Python worker、FFmpeg、自動代理評価器はsystem actorであり、人のSIDと区別します。
- 旧role assignmentとapproval recordは既存データ互換のため保持できますが、社内限定quick startの必須gateにしません。
- 任意self-reviewは`CompositionVersion`の内容digestへ固定します。

旧役割とreview経路は[ADR-0003](decisions/ADR-0003-local-identity-and-review.md)、現在の単一利用者stateは[ADR-0009](decisions/ADR-0009-source-only-defaults-and-automated-evaluation.md)を参照してください。

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

これらは合成入力と当時の実装surfaceに対する証拠です。その後に追加した版0.2のquick start、設定、自動decision、Composition計画は個別taskで敵対試験済みですが、自動代理評価、最終Render/export policy、環境で作成不能だったreparse point、production resource閾値、統合security受入は別の実装・受入を必要とします。別account ACLを未実施からPASSへ読み替えませんが、現在scopeの必須受入にはしません。

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
- 新しい file path が workspace 外や traversal を許さないか
- 新しい自動decisionがclosed規則を越えて意味判断、外部公開区分、権利、機密性を確定しないか
- 自動代理評価をhuman evaluationまたは法的適合と誤表示していないか
- 新しい依存に version、hash、license、取得元があるか
- `Music/`を変更・外部用profileへ利用していないか
- 新しいcloud経路がローカル不達と明示有効化に結び付いているか
