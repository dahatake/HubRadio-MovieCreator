# 承認素材 `hubradio-sample.mp4` の実素材受入 実測記録

要求定義書 §18 の承認素材（2026-09-23 承認、`sample/hubradio-sample.mp4`、SHA-256 `fb8d304f5fec7011fc3dd898cc3a8fcbcbe56ee64d6e97e65a864251b89f5a7b`、117,030,068 bytes）による source-only 受入（`V2-014`）の実行記録である。素材の測定値と 2026-09-23 のベースライン実行は [initial-pilot.md](initial-pilot.md) §4・§5 にある。

> [!IMPORTANT]
> **pipeline は解析段で停止し、標準成果物は生成されていない。** `V2-014` は引き続き不合格であり、`releaseApproved=false` を維持する。productionコード（`src/`）は変更していない。

## 1. ベースライン再実行（P3-00R / 2026-09-24）

### 1.1 実行と結果

- run_root: `artifacts/validation/approved-media-906a82e9813a4f9db418763faa037084`（保全。`artifacts/` はリポジトリに含まれない）
- 実行: `dotnet build tests/HubRadio.App.Tests -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false` の後、`HUBRADIO_RUN_LIVE_PIPELINE_TESTS=1`・`HUBRADIO_LIVE_PIPELINE_SINGLE_TEST=ApprovedMediaSourceOnlyPipeline_GeneratesTheStandardSet_AndRecordsTheAcceptanceMeasurements`・`HUBRADIO_APPROVED_MEDIA_PATH=<リポジトリ>\sample\hubradio-sample.mp4` で App runner を `--filter FullyQualifiedName~ApprovedMediaSourceOnlyPipeline --minimum-expected-tests 1 --progress off` で1回実行
- 結果: **失敗**（終了コード 2、`source_only.execution_failed`）。所要 107.3秒（準備 70.0秒、実行 37.0秒）
- stop_point: 解析段の開始直後（進捗 `source_only.analysis` 1/6）。取込み（9段階）は成功した。停止理由は `AudioExtraction` の `TimelineProbeFailed`。有力な原因は解析音声の開始時刻の取得の失敗だが、実行時の throw 箇所は一意には捕捉していない（§1.2）

| 段階 | 経過（ms） |
|---|---:|
| `dependencies.binaries_verified` | 1,518 |
| `dependencies.verified` | 59,457 |
| `prep.completed` | 69,974 |
| `callback.ingest` | 105,503 |
| `execution.Failed` | 106,964 |

2026-09-23 の実行（[initial-pilot.md](initial-pilot.md) §5）が9.2秒で停止した worker runtime の検証（`dependencies.verified` の手前）は、本実行では通過した。§5.2 の「実行環境に依存した停止」という仮説と矛盾しないが、原因の証明ではない。

### 1.2 停止理由と有力な原因

| # | 事実 | 出典 |
|---|---|---|
| A-01 | 解析段は、解析音声の開始を承認済みffprobeの `-select_streams <音声> -read_intervals %+#2 -show_frames -show_entries frame=stream_index,best_effort_timestamp,nb_samples` で取得し、出力が空なら `TimelineProbeFailed` にする | `src/HubRadio.Infrastructure/Analysis/AudioExtraction.cs`（`DecodedFrameReadInterval`、`CreateDecodedFrameProbeRequest`、`ParseDecodedAudioStart`） |
| A-02 | 新素材の音声 stream は先頭に priming packet を2個持つ（`pts=-2048`・`-1024`、1個目に `skip_samples=2048`）。変更前素材には無い | 承認済みffprobe `-read_intervals %+#6 -show_packets`（本機実測） |
| A-03 | 同じ引数で新素材を測ると、`%+#2` では frame が0行、`%+#4` では2行（`best_effort_timestamp=0`・`1024`）。変更前素材は `%+#2` で2行 | 承認済みffprobe（本機実測） |
| A-04 | 一時的に試験の例外記録へ `AudioExtractionException` の理由を加えた診断実行（試験ファイルは実行後に元へ戻した）で、停止の直前に `AudioExtractionException.TimelineProbeFailed` が記録された | 診断実行root `artifacts/validation/approved-media-85804df1adaf447184e01e4825a6a286` |

A-02〜A-04 は本機での実測で、生出力と診断実行root（`artifacts/`）はリポジトリに含まれない。外部から検証するには保全した実行root と変更前素材が必要である。

**判定:** 停止理由は `TimelineProbeFailed` である（A-04）。有力な原因は、**`-read_intervals %+#2` が priming packet だけを読み、decode 後の frame が得られないこと**である（A-01〜A-03 で独立に再現）。診断実行は例外の型と理由だけを記録したため、実行時の throw 箇所は一意には特定していない。停止は表示寸法（SAR、[initial-pilot.md](initial-pilot.md) §5.3.1）より前であり、事前の予測（構成段での停止）とは一致しなかった。

### 1.3 §18 記録6項目の取得状況

- not_acquired: ASR の発話数・検出言語、無音候補、強調・編集候補、字幕 cue 数、pipeline 内の loudness／True Peak、縦型→16:9 の pipeline 出力。いずれも解析段で停止したため取得できていない
- v2_014_status: **不合格（継続）**。標準成果物は0本。`releaseApproved=false`

### 1.4 本節で主張しないこと

- 停止原因を直せば標準成果物が生成されるとは主張しない。後段には SAR 未指定の扱い（[initial-pilot.md](initial-pilot.md) §5.3.1）と True Peak の余裕（同 §5.3.3）が残る
- ASR 精度・知覚品質・人手評価は未実施であり `PASS` としない
