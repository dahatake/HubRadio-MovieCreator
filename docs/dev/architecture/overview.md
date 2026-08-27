# アーキテクチャ概要

HubRadio MovieCreator は、Windows 11 x64 の単一 PC で動作する、ローカル優先の動画編集支援アプリケーションとして設計しています。

> [!IMPORTANT]
> Stage 0 は2026年8月27日に人手承認済みです。[ADR-0007](../decisions/ADR-0007-stage-0-technology-selection.md)に従い、condition-falseの話者分離・local text modelと、再評価保留のshot・face detectorを製品critical pathから外しました。ベンチマーク、FFmpeg・Python の検証スクリプト、レンダーとパッケージの probe は存在しますが、WinUI 3／WebView2／React の製品アプリケーション、SQLite の業務データ、レビュー UI は未実装です。

![HubRadio MovieCreatorのローカル優先アーキテクチャ。緑はStage 0で実装済み、青はStage 1以降の計画、紫の破線は条件付きAzure経路](system-overview.svg)

## 図の読み方

- **緑:** Stage 0 で実装・実測済みの検証経路
- **青:** Stage 1 以降に実装する製品アプリケーション
- **紫の破線:** ローカルで不達となり、利用者が送信内容と Azure 利用を明示承認した場合だけ追加する経路

Stage 0で評価しても採用しなかったmodelや再評価保留のdetectorは、製品構成要素として図示していません。対応する要求はtranscript規則、手動speaker割当、manual layout/keyframe、版付きsafe zoneで実装します。

現在の完了タスクと次の作業は、[詳細実装計画](../planning/implementation-plan.md)を正本とします。

## 設計原則

1. **ローカル優先:** 元動画、音声、画像は既定で端末外へ送信しません。
2. **元動画不変:** 取込後の元動画を編集処理で上書きしません。
3. **非破壊編集:** カット、字幕、画角、音声、効果は元素材への参照として保持します。
4. **根拠追跡:** 出力要素を元発言、承認済み素材、または編集者作成要素へ逆引きできるようにします。
5. **人が最終判断:** 削除、公開区分、権利、機密性、承認を AI だけで確定しません。
6. **最小構成:** 正式な要件がないサーバー、分散基盤、汎用拡張点は追加しません。

製品範囲の決定は [ADR-0002](../decisions/ADR-0002-product-scope.md) を参照してください。

## 目標プロセス構成

### 単一 .NET ホスト

計画中の `HubRadio.App.exe` が次を所有します。

- WinUI 3 ウィンドウと WebView2
- ユースケースとジョブ調整
- SQLite 接続
- 管理ワークスペース
- FFmpeg と Python 子プロセスの起動・監視

ASP.NET Core、localhost HTTP サーバー、Windows Service、別の業務 API プロセスは置きません。

### WinUI 3 と React

- **WinUI 3:** ウィンドウ、ネイティブファイル選択、起動、障害表示
- **WebView2 内の React:** 案件一覧、解析、タイムライン、字幕、レイアウト、レビュー
- **通信:** 許可リスト化した型付き WebMessage

React からファイルパス、SQL、FFmpeg 引数、任意コマンドは送信しません。

### 子プロセス

- **FFmpeg／ffprobe:** probe、代理生成、レンダー、全編 decode QC
- **Python ML worker:** ASRと、ADR-0007で採用したローカル解析

製品版では両者を .NET ホストの子プロセスとして実行します。shell は使わず、timeout、取消、子孫終了、実行ファイル hash を検査します。Python は SQLite に直接接続せず、版付き NDJSON 契約とジョブ専用 staging を使います。

Stage 0のshot、face、local text model用probeは不採用・保留判断の証拠として残しますが、現在の製品workerや配布依存には含めません。

Stage 0 では、製品ホストへ統合する前の PowerShell／Python 検証実装だけが存在します。

## 永続化

### SQLite

次の索引と業務状態を保存する計画です。

- 案件、権限、ジョブ
- 復旧可能な作業コピー
- 不変の編集版索引
- レビュー、承認、権利、監査
- 納品物の索引

単一 PC・単一書込ホストが対象であるため、共有 PostgreSQL やクラウド DB は導入しません。

### 管理ワークスペース

初回起動時に利用者が選んだローカル固定ディスクへ、元動画、代理、解析、編集版、プレビュー、レンダー、納品、証拠を保存する計画です。保存・保持・バックアップの決定は [ADR-0005](../decisions/ADR-0005-storage-retention.md) を参照してください。

ファイルは staging へ書き、hash 検証後に同一ボリューム内で昇格します。SQLite には昇格済み成果物だけを登録します。

## 非破壊編集と追跡性

計画中の主要概念は次のとおりです。

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
| `DeliveryManifest` | 動画、字幕、画像、文書を同じ承認版へ束ねる |

これらは実装計画上の型であり、現時点で製品コードとして完成していません。

## 処理フロー

1. 利用者が元動画と案件情報を登録します。
2. ホストが staging へコピーし、形式、stream、PTS、全編 decode、容量を検査します。
3. FFmpeg と Python が解析成果物を返します。自動出力は候補として隔離します。
4. 利用者が候補を採否し、字幕、区間、レイアウト、音声を修正します。
5. checkpoint で不変の編集版と出力から元素材への写像を作ります。
6. 公開区分に必要なレビューを、人が対象版の digest に対して行います。
7. 承認済み版から型付き Render IR を作り、FFmpeg 引数へ変換します。
8. codec、duration、A/V 同期、字幕、画面、音量、全編 decode を検査します。
9. QC 済み成果物だけを納品領域へ昇格し、hash と来歴を記録します。

この流れは [要求定義 §7](../../requirement-definition.md#7-基本処理フロー) を実装へ具体化した計画であり、完成済みの操作フローではありません。

## レビュー境界

社内限定の既定経路:

`Editing → EditorReview → ContentReview → Approved → FinalRendering → QualityControl → Exported`

外部公開候補の既定経路:

`Editing → EditorReview → PerformerReview → ContentReview → BrandReview → Approved → FinalRendering → QualityControl → Exported`

承認後に字幕、カット、音声、レイアウト、テンプレートを変更した場合は子版を作り、旧承認を引き継ぎません。詳細は [ADR-0003](../decisions/ADR-0003-local-identity-and-review.md) を参照してください。

## 信頼境界

| 境界 | 計画する強制策 |
|---|---|
| React → .NET | origin、size、schema、method の許可リスト検査 |
| .NET → FFmpeg | shell 無効、引数配列、型付き Render IR、timeout、binary hash |
| .NET → Python | NDJSON schema、message 上限、timeout、SQLite 直接接続禁止 |
| ホスト → workspace | path 正規化、ID lookup、staging、hash、atomic move、Windows ACL |
| ホスト → SQLite | parameterized query、transaction、foreign key、migration 前 backup |
| ホスト → Azure | 既定無効。選択・マスク・確認済み文字起こし span だけ |

データ分類と Azure 送信条件は [ADR-0004](../decisions/ADR-0004-data-classification.md)、配布と Azure の現状は [ADR-0006](../decisions/ADR-0006-distribution-and-azure.md) を参照してください。

## 初期対象外

- 同時共同編集、複数 PC 同期、マルチテナント
- PostgreSQL、Cosmos DB、Service Bus、AKS、マイクロサービス
- localhost Web API
- 無人での外部公開や媒体への直接投稿
- ディープフェイク、発言内容の改変
- 汎用プラグイン基盤や、二つ目の実装がない Provider／Factory
- 正式な必要性が未確定の OTIO、C2PA、WORM

## 判断の優先順位

実装判断が競合する場合は、[ADR-0001](../decisions/ADR-0001-source-of-truth.md) に従い、要求定義、確認済みレビュー、Accepted ADR、技術調査、実装計画の順に優先します。
