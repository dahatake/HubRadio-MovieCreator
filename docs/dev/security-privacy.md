# セキュリティとプライバシー

HubRadio MovieCreator は、企業向け動画、文字起こし、出演者情報、内部資料を扱うため、ローカル優先と人による承認を基本境界にします。

> [!IMPORTANT]
> Stage 0は完了し、製品基盤を段階実装中です。以下には実装済み境界と未完了の製品flowが混在するため、各節の状態と詳細実装計画を併せて確認してください。

## 既定のデータ境界

- 元動画、代理動画、音声、frame、画像を既定で端末外へ送りません。
- 元動画を上書きしません。
- AI や規則ベース処理は候補を返し、公開区分、権利、機密性、承認を自動確定しません。
- 社内限定、特定グループ限定、外部公開候補、外部公開承認済み、公開不可、判定待ちを区別します。
- 下位の素材や成果物は、親より広い公開区分へ自動昇格できません。

公開区分と送信条件の正本は [ADR-0004](decisions/ADR-0004-data-classification.md) です。

## ローカル保存

製品基盤は、利用者が選択するローカル固定ディスク上の管理ワークスペースを前提に実装しています。初回選択UIは未完了で、ネットワーク共有、同期 folder、リムーバブル媒体を初期既定にしません。

- 元動画、文字起こし改訂、編集版、承認、監査、納品物を正本として扱います。
- 代理、波形、sprite、preview などの再生成可能物を正本と分けます。
- 容量不足時に正本を自動削除しません。
- 保持期間が未決のため、時間だけを理由とする自動削除を行いません。
- backup は manifest、size、SHA-256、SQLite `integrity_check` を確認してから完成扱いにします。

保存、削除、backup、ACL の決定は [ADR-0005](decisions/ADR-0005-storage-retention.md) を参照してください。ArtifactStore、WorkspaceRecovery、ACL、検証付きbackup/restoreは実装済みです。初回workspace選択UI、保持・削除UI、別account ACL受入、外部固定ディスクのBitLocker状態確認と保留UIは未完了です。

## Windows identity と権限

製品基盤では Windows ユーザーの SID を監査主体の識別子として実装済みです。表示名だけを識別子にせず、独自 password や認証 server を追加しません。

- 初回起動時の SID には Administrator と Editor を割り当てます。
- 承認役割は明示割当まで付与しません。
- AI、Python worker、FFmpeg に承認者権限を与えません。
- 承認は `CompositionVersion` の内容 digest に固定します。

役割とレビュー経路は [ADR-0003](decisions/ADR-0003-local-identity-and-review.md) を参照してください。

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

## Media metadata

media metadata には、位置情報、端末名、撮影時刻、comment などが含まれる可能性があります。

- raw tag 値を terminal、issue、通常 log、canonical summary へ出しません。
- 比較結果は key 名、存在有無、除去成否など必要最小限にします。
- path の秘匿だけで metadata が安全になったとは判断しません。
- 外部共有前に、成果物の metadata と権利状態を別々に確認します。

## Azure の条件付き経路

Azure は通常の処理経路ではありません。次の条件がすべて揃った場合だけ検討します。

1. ローカルまたは手動経路で要求を満たせないことを実測した。
2. 利用者が目的と対象の文字起こし span を選択した。
3. PII、秘密、内部 URL、認証情報、不要な固有名詞を解消した。
4. 送信 payload、送信先、region、model、保持条件を利用者が確認した。
5. 利用者が Azure resource の作成を明示的に指示した。

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
- model／runtime は version、配布元、hash、license、利用条件を確認します。

第三者依存の現在の受入記録は [第三者ソフトウェア・モデル受入方針](licenses/third-party-intake.md) を参照してください。

## 変更前の確認

- 新しい入力、log、canonical result に内容、PII、秘密、絶対 path が入らないか
- 新しい child process が shell、無制限出力、無期限待機を使わないか
- 新しい file path が workspace 外や traversal を許さないか
- 新しい AI 機能が人の採否と公開 gate を迂回しないか
- 新しい依存に version、hash、license、取得元があるか
- 新しい cloud 経路がローカル不達と明示承認に結び付いているか
