# 安定エラーコードとローカル診断ログ

本書は J-009 で実装済みの安定エラーコードだけを列挙する。将来機能のコードを先取りして予約しない。

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

Windows上のlog pathはローカル固定diskの通常pathだけを受理する。UNC、device namespace、named pipe、alternate data stream、既存のreparse pointを含むpathはI/O前と書込み直前に拒否する。workspace root内への配置とACLは後続S-003の境界だが、J-009だけでも端末外shareや別streamをlocal logとして受理しない。

各書込みでは、既存の完全かつ schema-valid な record を新しい順に byte 上限へ収め、一時 file を flush してから同一 directory 内で置換する。record の途中を byte truncate しない。同じ log path への process 内書込みは共有 gate、別 process との書込みはゼロ長 lock file で直列化され、待機と file I/O は caller cancellation を受け取る。既定上限は 1 MiB、設定可能範囲は 1 KiB から 16 MiB であり、log file は上限を超えて増加しない。

診断ログは ADR-0005 に従って workspace の `logs/` に置く循環対象で、backup 対象外である。ADR-0004 に従い、送信本文や API key を複製しない。本契約は S-012 の監査ログではなく、cloud telemetry も実装しない。