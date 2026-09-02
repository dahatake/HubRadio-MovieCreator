# ADR-0003: ローカルWindows IDとレビュー経路

- 状態: Accepted
- 決定日: 2026-08-27
- タスク: P-005

> [!IMPORTANT]
> 2026-09-02以降、社内限定quick startの既定review経路、承認自動省略禁止、承認roleの初期割当は[ADR-0009](ADR-0009-source-only-defaults-and-automated-evaluation.md)が置き換える。actor SID、監査event、digest-boundな任意review、変更時の子版、公開範囲を自動拡大しない原則は維持する。以下は決定時点の履歴である。

## 決定

単一PC版では、現在のWindowsユーザーのSIDを監査主体の不変識別子として使う。表示名だけを識別子にしない。

- 認証サーバー、独自パスワード、Entra必須化は行わない。
- ローカルの役割割当はSQLiteへ保存し、操作時に権限を検査する。
- 同じSIDが複数の役割を兼任できるが、各レビュー判断は対象版、実行時役割、日時、理由を別イベントとして記録する。
- AI、Pythonワーカー、FFmpeg、システム管理処理へ承認者権限を与えない。

## 役割

| 役割 | 主な操作 |
|---|---|
| Viewer | 閲覧、許可されたプレビュー |
| Editor | 案件作成、解析候補採否、編集版作成、レビュー提出 |
| PerformerReviewer | 出演者名・肩書・発言・公開可否の確認 |
| ContentApprover | 内容、機密候補、公開区分の確認と承認 |
| BrandApprover | ロゴ、色、表現、対外素材の確認と承認 |
| RightsApprover | 権利確認または根拠付き例外判断 |
| Administrator | 役割、保存先、テンプレート公開、監査閲覧 |

## 既定レビュー経路

### 社内限定

`Editing → EditorReview → ContentReview → Approved → FinalRendering → QualityControl → Exported`

- PerformerReviewは出演者確認が案件ポリシーで必要な場合に挿入する。
- BrandReviewは承認済み固定テンプレートから逸脱した場合に挿入する。

### 外部公開候補

`Editing → EditorReview → PerformerReview → ContentReview → BrandReview → Approved → FinalRendering → QualityControl → Exported`

- 使用素材に未確認権利がある場合はRightsApproverの確認または根拠付き例外記録を必須にする。
- `Published`は外部公開を自動実行せず、別権限の利用者が公開事実を記録した状態とする。

RightsApproverの判断は独立した`RightsReview`状態を追加せず、`Approved`および外部用`Exported`へ進む直前のポリシーゲートとして扱う。判断イベントは対象素材、権利状態、例外理由、判断者を保持する。

## 状態規則

- `ChangesRequested`と`PublicationBlocked`は一時フラグでなく永続状態とする。
- 承認は`CompositionVersion`の内容digestへ固定する。
- `CompositionVersion`はテンプレートIDだけでなく不変のテンプレート版IDと内容digestを参照し、その参照を版digestへ含める。新しいテンプレート版の公開は既存版を書き換えない。
- 字幕、カット、音声、レイアウト、テンプレート等の内容変更は子版を作り、承認を引き継がない。
- 同じ内容digestの技術的再レンダーは、承認を維持できる。
- 役割変更後も過去イベントには判断時の役割を保持する。
- ローカル単一利用者が全役割を兼任する開発時でも、承認操作を自動省略しない。
- `Published`は`Exported`から遷移する永続状態であり、媒体への公開処理を実行する命令ではない。公開先、公開日時、記録者を保存する事実記録である。公開取消や内容変更は`Published`を`Exported`へ戻さず、別の取消イベントまたは新しい子版として記録する。

## 既定割当

初回起動時の現在SIDへ`Administrator`と`Editor`を割り当てる。承認役割は利用者が明示的に割り当てるまで付与しない。これにより、単一PCでも編集者自身が暗黙に最終承認することを防ぐ。

## 完了判定

権限マトリクス、状態遷移、役割兼任、承認digest、公開事実の分離を実装・受入試験へ引き継ぐ。
