# 安定エラーコードとローカル診断ログ

本書は実装済みの安定エラーコードだけを列挙する。将来機能のコードを先取りして予約しない。

## エラーコード規約

- コードは小文字 ASCII の dot 区切りで、各 segment は英小文字から始まり、英小文字・数字・単一 underscore だけを含む。
- 公開済みコードの意味を変更・再利用しない。追加時は実装、試験、本書を同じ変更で更新する。
- `StableErrorCodes.Validate` は正規形であっても本書に未定義のコードを拒否する。
- 例外の `Message` は安定した一般文だけとし、path、入力、秘密、transcript、media metadata を含めない。

## 現在のカタログ

| コード | 公開元 | 意味 | 許可された補助値 |
|---|---|---|---|
| `process.start_failed` | `ProcessStartException` | shell を介さない子 process を開始できなかった | nullable な OS native error code |
| `diagnostic.log_write_failed` | `DiagnosticLogException` | bounded local diagnostic log の作成、読取り、rotation、置換のいずれかに失敗した | なし。失敗 path や inner exception は公開しない |
| `webview.runtime_unavailable` | `WebViewStartupResult`（起動画面表示） | 構成された WebView2 Runtime 対象（開発時の Evergreen または portable 配布の Fixed）の版を検出できず、Web UI 初期化を開始しなかった | なし。検出例外、path、環境変数は公開しない |
| `webview.initialization_failed` | `WebViewStartupResult`（起動画面表示） | Runtime 検出後に WebView2 control を初期化できなかった | なし。初期化例外、user data path、内容は公開しない |
| `worker.invalid_message` | `WorkerProtocolException` | worker の NDJSON、順序、相関、終了状態のいずれかが閉じた v1 契約に適合しなかった | なし。標準入出力、request、path、例外内容は公開しない |
| `worker.message_too_large` | `WorkerProtocolException` | worker protocol の単一 JSON record が 512 UTF-8 bytes を超えた | なし。超過 record は公開しない |
| `worker.unknown_method` | `WorkerProtocolException` | v1 で許可した `worker.ping` 以外の method を拒否した | なし。入力 method は公開しない |
| `worker.processing_failed` | `WorkerProtocolException` | worker が要求を処理できず、内容を伴わない固定 error envelope で終了した | なし。Python 例外や入力内容は公開しない |

U-001 の WebView2 の 2 コードは起動・復旧画面へ表示するだけで、例外としては投げず、現時点では診断ログへ書かない。下記の enum 対応は catalog との一致を保つために定義してある。

`webview.runtime_unavailable` は構成対象の Runtime 検出段階だけを表し、Fixed フォルダの不存在も含む。検出後の初期化失敗は `webview.initialization_failed` とする。明示 Fixed path の null・空白・相対 path は引数契約違反として拒否し、これらや他の構成・検証失敗に検出失敗コードを再利用しない。両コードの結果には例外やフォルダ path の詳細を含めない。

## 結合CLIの失敗コード（FR-035）

`stitch.*` コード（`stitch.usage`、`stitch.input_*`、`stitch.output_*`、`stitch.tool_unverified`、`stitch.encode_failed`、`stitch.timeout`、`stitch.output_verification_failed`、`stitch.cancelled`、`stitch.internal_error`）は、結合CLI（`src/HubRadio.Stitch`）が標準エラーへ `error <code>: <説明>` の1行で出す終了時コードである。公開元は `BookendStitchException` / `BookendStitchCommand`。意味と exit code の対応の正本は[要求定義書 FR-035](../requirements/editing.md#fr-035-オープニングクロージング動画の結合)の失敗コード表とし、本書では重複して列挙しない。仕上がり画面のbridge `stitch.run`は、同じコード（`stitch.usage`を除く）を`status=failed`の`code`として返し、error envelopeは使わない（GUI応答statusの正本もFR-035）。診断ログには書かないため `StableErrorCodes` の対象外である。説明文には path・ファイル名・ffmpeg の出力を含めず、入力は役割（opening／main／closing）だけで示す。

## 診断ログ契約

`LocalDiagnosticLog` は UTF-8 BOM なし、LF 終端の JSON Lines を書く。property 順序と severity 表現は固定し、1 record は次の allowlist だけを持つ。

- `schemaVersion`
- `timestampUtc`
- `severity`
- `code`
- `component`
- `operation`
- `durationMs`
- `nativeErrorCode`
- `exceptionType`
- `exceptionTypeTruncated`
- `exceptionTypeRedacted`
- `exceptionContentOmitted`

`component` と `operation` は現在の catalog に対応する enum allowlist であり、任意文字列ではない。コードとの不一致も constructor で拒否する。J-009 の persisted API は自由記述 message を受け付けない。`DiagnosticLogEntry.FromException` は例外型名だけを ASCII 英数字と記号 `.`、`_`、`+`、`` ` `` の文字種 allowlist、秘密・Windows path の redaction、UTF-8 byte 上限へ通す。Unicode 同形文字を含むその他の文字は置換し、次を常に破棄する。

- `Exception.Message`
- stack trace と source file path
- inner exception
- `Exception.Data`
- transcript、字幕、prompt、media filename、media metadata、command output

Windows上のlog pathはローカル固定diskの通常pathだけを受理する。UNC、device namespace、named pipe、alternate data stream、既存のreparse pointを含むpathはI/O前と書込み直前に拒否する。workspace rootとACLの保護はS-003コードで実装済みだが別account受入待ちであり、J-009単体でも端末外shareや別streamをlocal logとして受理しない。

各書込みでは、既存の完全かつ schema-valid な record を新しい順に byte 上限へ収め、一時 file を flush してから同一 directory 内で置換する。record の途中を byte truncate しない。同じ log path への process 内書込みは共有 gate、別 process との書込みはゼロ長 lock file で直列化され、待機と file I/O は caller cancellation を受け取る。既定上限は 1 MiB、設定可能範囲は 1 KiB から 16 MiB であり、log file は上限を超えて増加しない。

診断ログは ADR-0005 に従って workspace の `logs/` に置く循環対象で、backup 対象外である。ADR-0004 に従い、送信本文や API key を複製しない。本契約は S-012 の監査ログではなく、cloud telemetry も実装しない。