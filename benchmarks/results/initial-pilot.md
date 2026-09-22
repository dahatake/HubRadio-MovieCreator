# 初期検証シナリオ（要求定義書 §18）実測記録

**対象:** source-only vertical slice の最終受入素材
**正本要求:** [docs/requirement-definition.md](../../docs/requirement-definition.md) §18

> [!IMPORTANT]
> 本書には**実際に実行して得た測定値だけ**を記録する。未実施・未測定の項目は「未実施」「未測定」と明記し、`PASS` と書かない。

---

## 1. 受入素材の測定（T-02 / 2026-09-21）

### 1.1 使用した測定ツール

| 項目 | 値 |
|---|---|
| 実行ファイル | `artifacts/portable/candidate-review-06/Contents/Tools/ffprobe.exe` |
| 版 | `n8.1.2-34-g9b6c8969e0-20260731`（`ffprobe -version` の出力） |
| SHA-256 | `4c2f730969c9551aec21c5ca07eb73f63bb0920204c9cd6c9a6e7be6be0458d2` |
| 承認記録との一致 | **一致**（[benchmarks/results/ffmpeg-capabilities.json](./ffmpeg-capabilities.json) `executables.ffprobe.sha256` および `byteSize=113330688`） |
| package | `BtbN.FFmpeg.LGPL.8.1` / `8.1.2-20260731` / `win64-lgpl-static` |

**WinGet導入版について（重要）:** `%LOCALAPPDATA%\Microsoft\WinGet\Packages\BtbN.FFmpeg.LGPL.8.1_*\ffmpeg-n8.1.2-50-g1a748fe2cd-win64-lgpl-8.1\bin\ffprobe.exe` は SHA-256 `67176fa62f89f94c3bcd379fd05677a25651569a2eb8880ec2194e62c82be412` であり、**承認記録と不一致**（別ビルド `n8.1.2-50`）。よって承認済み同梱payload側の実行ファイルを使用した。

### 1.2 素材の同定

| 項目 | 値 |
|---|---|
| パス | `sample/Copilot  あなたの毎日の AI コンパニオン.mp4`（`Copilot` の後は半角スペース2個） |
| SHA-256 | `07904c1dd4c482fa495ea8cd3572c98fff4eaf66b41cf89e396e661f3b8c3e9c` |
| byte size | 9,971,886 |
| 要求定義書 §18 の記載との一致 | **一致** |

### 1.3 承認済みffprobeによる再測定値

| 項目 | 実測値 |
|---|---|
| container | `mov,mp4,m4a,3gp,3g2,mj2` |
| duration | `60.093243` 秒 |
| size | `9971886` bytes |
| 映像 codec / profile | `h264` / `High` |
| 解像度 | 1280 × 720 |
| frame rate (`r_frame_rate`) | `24000/1001`（= 23.976 fps） |
| 音声 codec / profile | `aac` / `LC` |
| sample rate / channels | 44,100 Hz / 2ch |

### 1.4 参考実測値（システム版ffprobe）との照合

調査段階で取得した参考値（1280×720 / 23.976fps / AAC-LC 44.1kHz / 60.093243秒 / 9,971,886 bytes）と、上記1.3の承認済みffprobe再測定値は**全項目一致**した。

**ただし、参考実測値（システム版ffprobe）は受入証拠として流用していない。** 受入証拠として採用するのは §1.3 の承認済みffprobeによる再測定値のみである。

### 1.5 未実施の範囲（T-02 時点）

- pipeline の E2E 実行は**未実施**（`T-31` で実施する）
- loudness / True Peak / A/V同期 / 字幕品質 / 知覚品質の測定は**未実施**
- 人手評価は**未実施**

---

## 2. 実素材によるsource-only受入の実行（T-31 / 2026-09-22）

> [!CAUTION]
> **結果: 不合格（FAIL）。** 承認済み受入素材でpipelineを実行したが、**標準成果物は1件も生成されなかった**。
> 本節は実行した事実と観測値だけを記録する。要求§18の記録6項目のうち、**生成後にしか観測できない項目は取得できていない**。
> `V2-014` は**未完了**であり、`releaseApproved=false` を維持する。

### 2.1 実行方法

| 項目 | 値 |
|---|---|
| シナリオ | `tests/HubRadio.App.Tests/SourceOnlyApprovedMediaPipelineTests.cs`（`T-30` で新規追加。productionコード変更なし） |
| 実行メソッド | `ApprovedMediaSourceOnlyPipeline_GeneratesTheStandardSet_AndRecordsTheAcceptanceMeasurements` |
| opt-in環境変数 | `HUBRADIO_RUN_LIVE_PIPELINE_TESTS=1` / `HUBRADIO_LIVE_PIPELINE_SINGLE_TEST=<上記メソッド名>` / `HUBRADIO_APPROVED_MEDIA_PATH=<受入素材の絶対パス>` |
| 隔離 | `artifacts/validation/approved-media-<guid>` をACL付きで新規作成。専用SQLite DB、専用model catalog、`TEMP`/`HOME`/`HF_*` を実行rootへ固定。実利用者profile・既定アプリケーションディレクトリ・本番DBは不使用 |
| 素材の扱い | リポジトリの `sample/` は**読取のみ**。実行rootへ複製し、複製のSHA-256が §1.2 の値と一致することを検査 |
| 実行回数 | 4回（うち1回は準備予算超過、3回は同一箇所で同一failure code。**再現性あり**） |

### 2.2 時間の実測（最終試行 `approved-media-f8b4a686`）

| 区間 | 実測 |
|---|---|
| 依存binary検証完了 | 4.084 秒 |
| worker runtime検証完了（2,697ファイル / 268,991,381 bytes） | 17.515 秒 |
| ASR model展開・導入完了 | 38.433 秒 |
| 素材複製・digest照合完了 | 39.486 秒 |
| **準備（prep）合計** | **39.680 秒** |
| ingest完了（callback.ingest） | 92.896 秒 |
| 文字起こし完了（callback.transcript、実ASR） | 122.887 秒 |
| 構成完了（callback.composition） | 142.626 秒 |
| 生成runtime確定（callback.generated） | 143.956 秒 |
| **失敗（ReconciliationRequired）** | **145.302 秒** |

**予算に関する記録:** 初回試行は準備予算300秒を超過して失敗した。原因は上記 worker runtime の全件検証で、**duration非依存**の固定費である。予算を1800秒へ引き上げた後の実測は17.5〜47秒であり、初回の超過は**ファイルキャッシュがcoldな初回実行に起因する**と考えられる（複数回の実測による推定であり、断定しない）。実行・シナリオ予算（1800/5400/9000秒）は**4秒合成媒体の既存実績からの推定**であり、本素材での上限実測ではない。今回は予算超過ではなく機能failureで停止したため、これらの上限は**検証されていない**。

### 2.3 失敗の同定

| 項目 | 観測 |
|---|---|
| 実行phase | `ReconciliationRequired` |
| failure code | `source_only.reconciliation_required` |
| 根本の例外 | `StandardGenerationException` / `Reason = PreparationNotCheckpointed` |
| 発生箇所 | [StandardArtifactGenerator.ValidateCheckpoint](../../src/HubRadio.App/Rendering/StandardArtifactGenerator.cs)（`GenerateAsync` の try ブロック**外**で実行される事前検査） |
| 直接の意味 | 保存済みcomposition checkpointから復元した `StandardCompositionPreparation` が、生成器へ渡された実行中の `Preparation` と**一致しない** |
| ジョブ記録 | root job `failed` / `job.execution_failed`、ingest job `succeeded`。durable progressは `source_only.analysis` → `composition` → `rendering` の3件で停止 |

### 2.4 切り分けのための対照実験と観測（いずれも本機で実測）

| # | 観測 | 結論 |
|---|---|---|
| O-1 | 既存の合成4秒媒体E2E `RealSourceOnlyPipeline_DeliversTheEntireGeneratedSet_AndReopensWithoutRerunning` を**同一コード・同一機で実行し成功**（4分26秒、納品・再読込まで完了） | 全般的なregressionでも環境不備でもない。**実素材固有**である |
| O-2 | 実行中に `FormatException` は1件も発生していない（first-chance観測） | `VersionService.GetStandardComposition` の読込・意味的replayは**成功している**。復元失敗ではない |
| O-3 | 生成時に `InvalidOperationException` は発生していない | evidence envelopeのkind不一致でもない |
| O-4 | 保存済みcompositionを2回復元し、`Plan`・`ParentTimelineMap`・`ChildTimelineMap` の全体serialization digestが**完全に一致** | replayは**決定的**であり、非決定性が原因ではない |
| O-5 | 実行中に記録された `generated-runtime.json` の `planDigest` = `0c6723c8…` と、復元した `Plan.PlanDigest` = `0c6723c8…` が**一致** | `PlanDigest` が覆う範囲の計画内容は一致している |

**帰結（事実から導ける範囲）:** `ValidateCheckpoint` は `StandardRenderGraphFactory.Digest<T>`（対象オブジェクトの**全public propertyを直列化**する汎用digest）で比較する。O-5 より `PlanDigest` の対象範囲は一致しているため、不一致は **`PlanDigest` が覆わない範囲**にある。

### 2.4.1 不一致fieldの同定（2026-09-22 追試）★

`SourceOnlyRuntimeCallbacks.CompositionReady`（[SourceOnlyRuntimeOptions.cs:150](../../src/HubRadio.App/Projects/SourceOnlyRuntimeOptions.cs)）は、pipelineが生成器へ渡すのと**同一の** `StandardCompositionPreparation` をテストへ渡す（[SourceOnlyPipeline.cs:2204-2213](../../src/HubRadio.App/Projects/SourceOnlyPipeline.cs) と [:1349](../../src/HubRadio.App/Projects/SourceOnlyPipeline.cs) が同じ `preparation` 変数を使う）。これを利用し、**productionコードを1行も変更せずに** `ValidateCheckpoint` の7条件を再現して判定した。

| 条件 | 判定 |
|---|---|
| cond1 `Child` のcanonical JSON | 一致 |
| **cond2 `Plan`** | **不一致** |
| cond3 `ApplicationDigest` | 一致 |
| cond4 `ExpectedWorkingCopyRevision` | 一致（ともに `1`） |
| cond5 `PreparedWorkingCopy` | 一致 |
| cond6 `ParentTimelineMap` | 一致 |
| cond7 `ChildTimelineMap` | 一致 |

**不一致は `cond2` のみ。** 差分23件はすべて `plan.LongForm.Provenance.SourceSpans[*]` に集中し、値の並びは次のとおりであった。

| index | 実行中（expected） | 復元後（stored） |
|---|---|---|
| [0] | `Start=111/50` | `Start=0/1`（全体包絡span） |
| [1] | `Start=1519/50` | `Start=111/50` |
| [2] | `Start=0/1`（全体包絡span） | `Start=1519/50` |

**順序だけが異なり、集合は同一であることを機械的に確認した。** 各要素を直列化して整列した列が両者で完全一致する（診断出力 `plan.longFormSourceSpans.sameSetIgnoringOrder=true`）。

**根本原因（確定）:** `ValidateCheckpoint` が使う `Digest<T>` は**配列の順序に依存する**ため、`LongForm.Provenance.SourceSpans` が同一集合の**別順序**になるだけでdigestが変わり、生成が拒否される。

**実素材でのみ再現する理由（確定）:** 合成4秒媒体の machine transcript は **utterance 0件**（当該runの `*.machine-transcript.json` は163バイト、`utterances` 配列が空。retained run root `live-e2e-f7c1b51c…` で実測）であり、文字起こし由来のspanが生成されない。したがって `SourceSpans` は全体包絡spanの**1件だけ**となり、要素が1件なら順序の違いが生じ得ない。これがO-1（合成媒体は成功）と矛盾しない理由である。**「無音だから文字起こしが空」と仮定したのではなく、保全された実行rootの成果物で実測した。**

**再現性:** 異なる案件ID・異なる実行rootで2回、同一の判定（cond2のみ不一致、`sameSetIgnoringOrder=true`）を得た。

**未確定として残す点:** 実行中と復元後のどちらの順序が**正本**かは決めていない。修正は `LongForm.Provenance.SourceSpans` の並びを生成側・復元側のどちらで正規化するかの判断を伴い、FR-032（追跡性）における並びの意味づけと、保存済みcanonical JSON・`PlanDigest`・graph digestへの影響評価が必要である。**本タスクの範囲外であり、修正は行っていない。**

### 2.5 要求§18の記録6項目の取得状況

| # | §18の記録項目 | 状態 |
|---|---|---|
| ① | 入力digest、設定profile版、tool/model版 | **取得済み。** 入力SHA-256 `07904c1d…`（§1.2と一致）／9,971,886 bytes、設定snapshot digest `5e306d24…`、ASR model `asr.faster-whisper-small.cpu-int8`（manifest `4e924b13…`、device `cpu`、compute `int8`）、ffmpeg/ffprobeは承認済み固定digest、font `dff723ba…` |
| ② | 自動候補、自動適用区間、未採用候補、warning | **部分取得。** 実ASRによる文字起こしと自動decision、silence detection、repetition、render preflight（`reason = source_only.candidate_timing_requires_review`、除外候補0件）の各evidenceが実行rootに生成された。ただし**結果DTOとしての候補・不成立理由は未生成** |
| ③ | 字幕の読速、行長、行数、同期、safe zone判定 | **未取得。** 出力が存在せず測定できない |
| ④ | codec、decode、duration、A/V同期、loudness、True Peak判定 | **未取得。** 出力が存在せず測定できない |
| ⑤ | 各出力要素の`OriginRef`と使用素材digest | **未取得。** 出力が存在しない |
| ⑥ | 自動代理評価のPASS/WARNING/FAIL/NOT EVALUATED | **未取得。** 評価段階へ到達していない |
| — | 人手確認（任意記録） | **未実施** |

### 2.6 標準成果物（§18の1〜6）の生成有無

| 成果物 | 件数 | 備考 |
|---|---:|---|
| 社内向け16:9長尺版 | 0 | 生成段階に到達せず |
| 9:16短尺候補（最大3） | 0 | 同上。**短尺が3本未満である理由は「候補不成立」ではなく「生成未到達」であり、不成立理由としては記録できない** |
| 字幕焼込版 / 字幕なし版 | 0 | 同上 |
| 字幕file（SRT/VTT） | 0 | 同上 |
| 文字起こし（`transcript.json`） | 0 | 付帯成果物の段階へ未到達 |
| サムネイル候補3枚 | 0 | 同上 |
| 使用区間一覧（`used-source-ranges.json`） | 0 | 同上 |
| 自動代理評価記録 | 0 | 同上 |

**空・重複した動画は1件も作られていない**（生成段階へ到達していないため）。

### 2.7 本節で主張しないこと

- アップスケール（1280×720→1920×1080）の実施結果は**未測定**である。
- ASR精度、知覚品質、人手評価は**実施しておらず、`PASS` としない**。
- 実行・シナリオの時間予算の妥当性は**未検証**である。
- 実利用者profile・本番DB・`Music/` は変更していない（実行rootのみを使用）。
- native GUI受入（`UX-041`/`UX-043`）、clean OS受入、署名、外部公開は**対象外・未実施**である。
