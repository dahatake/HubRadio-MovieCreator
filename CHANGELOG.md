# Changelog

重要な変更をこのファイルに記録します。Keep a Changelogの区分を使用します。

## [Unreleased]

### Added

- 元動画から実ジョブ、offline ASR、標準生成、技術QC、自動代理評価、社内限定ローカル納品までの実接続。
- 設定を固定した子版再生成、再読込、失敗後の明示再試行と旧成果物の保全。
- SQLite schema 12による字幕編集の永続保存・検証付き再生・再生成時の編集履歴固定。
- 4タブと独立設定、版に結合した警告移動、未保存入力の保持、明示的な設定ファイル保存・復元。
- 合成媒体を使う実パイプラインと字幕編集の実エンコード／QC試験。

### Changed

- 必須UI/UX対象52単位の検証・独立レビュー後、開発用App packageを`0.1.0.0`から`0.1.1.0`へPATCH更新。
- 現行Worker・フォント・Web資源の固定入力とApp build identityの検証を統合。

### Fixed

- WindowsでWorkerの一時出力パスが長くなり保存に失敗する問題。
- 実生成警告のcode／roleと保存・Web codecの不一致。
- ffprobeのSEI付帯情報によるPTS／動画QCの誤拒否。
- コピー先の保持ハンドルによる納品directory昇格失敗。
- BGM改変検知の誤った失敗分類、兄弟字幕の編集・未適用入力の保持。
- 通常MRT CoreビルドがWorkerデータをPRI修飾子として解釈する問題。

### Notes

- UX-041／UX-043は利用者指定の対象外。未実施の実GUI・人手・実素材品質・clean OS・署名・一般配布は承認済みとはしません。
- Worker `0.1.0`、Web `0.0.0`、vendor pin、`releaseApproved=false`を維持。PATCH更新はMSIX作成・インストール・一般提供の実施を意味しません。
- 検証範囲と原本証跡は[詳細実装計画](docs/dev/planning/implementation-plan.md)と[品質記録](docs/dev/quality.md)を参照してください。