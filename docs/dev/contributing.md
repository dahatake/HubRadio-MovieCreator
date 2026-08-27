# 開発への参加

正式な `CONTRIBUTING.md`、Code of Conduct、CI policy はまだありません。この文書は、現在の Stage 0 と後続実装で変更を安全にレビューするための最小手順です。存在しない運用制度や自動化を前提にしません。

Issue: <https://github.com/dahatake/HubRadio-MovieCreator/issues>

## 変更前

1. 対象の要求 ID、ADR、実装計画の task ID を確認します。
2. 現在の gate と依存 task が完了しているか確認します。
3. 実装済み、計画中、条件付きのどれを変更するか明記します。
4. 最小の成果物と検証へ分割します。
5. 二つ目の実装がない Strategy／Factory／Provider、未使用設定、将来用 hook を追加しません。

正本の優先順位は [ADR-0001](decisions/ADR-0001-source-of-truth.md) に従います。

1. [`docs/requirement-definition.md`](../requirement-definition.md)
2. [確認済みの実現方式レビュー](reviews/adversarial-review.md)
3. [Accepted ADR](decisions/)
4. [技術調査](research/implementation-research.md)
5. [詳細実装計画](planning/implementation-plan.md)

矛盾する場合は上位文書を優先します。要求定義は Draft であるため、未決事項を推測で埋めません。

## 実装変更

- 正常系だけでなく、その変更に実在する失敗、取消、timeout、disk full、rollback などを検証します。
- 元動画と承認済み版を上書きしません。
- UI 文字列を SQL、shell、FFmpeg filterscript、Python code へ連結しません。
- AI 候補の生成と、人による採用を同じ操作にしません。
- 数値閾値には測定、適用素材、責任者、承認状態を記録します。
- Azure はローカル不達と利用者の明示指示が揃うまで通常経路へ追加しません。

変更後の検証は [品質管理](quality.md) と、対象 task の完了条件に従います。

## Stage 0 benchmark の変更

新しい stage や指標を追加する場合は [Stage 0 ベンチマーク](benchmarks/README.md) の手順に従います。

- fixture と source hash guard を維持する
- 成功と失敗を schema-valid な JSON で記録する
- timeout 時に子孫 process を残さない
- raw run と canonical summary を分離する
- 0件や失敗を解釈する control を用意する
- 測定していない精度や production 閾値を記載しない

## 文書変更

- ルート `README.md` は非エンジニアの一般利用者向けに保ちます。
- 開発、保守、技術検証、設計判断は `docs/dev/` に置きます。
- 製品要求の正本 `docs/requirement-definition.md` は `docs/` 直下に維持します。
- リポジトリ内リンクは相対 path を使用します。
- ファイル移動時は全参照と ADR-0001 の path／SHA-256 を同期します。
- 現在と将来を区別し、未実装 UI の操作方法を作りません。
- 図が必要な場合は既存 SVG を優先し、`title`、`desc`、代替 text を確認します。

## セキュリティと権利

次を commit しません。

- `sample/` の private fixture と生成動画
- raw benchmark run、private transcript、音声、frame、bbox
- model weight と取得 cache
- API key、token、秘密設定
- raw media metadata、絶対 path、PII

新しい依存は version、配布元、byte size／hash、license、利用条件を [第三者受入台帳](licenses/third-party-intake.md) へ記録します。fixture や素材の利用権は software の MIT License と別に確認します。

詳細は [セキュリティとプライバシー](security-privacy.md) を参照してください。

## 敵対的レビュー

各 task の完了後、次の5軸を確認します。

1. 目的適合性
2. 内容の妥当性
3. 整合性
4. 根拠性・不確実性管理
5. 品質・運用性

指摘は実ファイル、実行結果、一次資料で検証します。存在しない問題は追加しません。実在する指摘を反映し、同じ検証を再実行してから次の依存 task へ進みます。

## 変更提出前の確認

- 対象要求 ID と task ID が分かる
- 変更範囲が必要最小限である
- 正常系と実在する失敗経路を検証した
- source、秘密、PII、raw metadata を漏らしていない
- 文書リンク、schema、hash、進捗表示が同期している
- 未測定・未承認・条件付き項目を明記した
- 敵対的レビューの実在指摘を反映した
