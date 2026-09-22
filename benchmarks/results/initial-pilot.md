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

---

## 3. 実素材によるsource-only受入の再実行（P2-12 / 2026-09-23）

> [!CAUTION]
> **結果: 不合格（FAIL）。** ただし停止箇所は §2 とは**異なる**。
> §2 で停止していた生成前検査（`PreparationNotCheckpointed`）は**通過**し、**標準成果物の生成・付帯成果物の昇格まで到達した**。
> 今回停止したのは**自動代理評価（QC）**であり、`loudness` と `truePeak` の2規則が `FAIL` になって納品に至らなかった。
> **`V2-014` は引き続き未完了**であり、`releaseApproved=false` を維持する。人手評価・ASR精度・知覚品質は**実施しておらず `PASS` としない**。

### 3.1 実行方法と識別子

| 項目 | 値 |
|---|---|
| シナリオ | `tests/HubRadio.App.Tests/SourceOnlyApprovedMediaPipelineTests.cs`（`P2-10` の診断を含む。productionコード変更なし） |
| 実行メソッド | `ApprovedMediaSourceOnlyPipeline_GeneratesTheStandardSet_AndRecordsTheAcceptanceMeasurements` |
| 実行root（保全） | `artifacts/validation/approved-media-3d95736adfa048f5a45a6de1fe4209b5` |
| 実行ID | `9ccb99ac-6ad3-4557-8093-2e5feb0a00cc` |
| 案件ID / 子版ID | `64c69755-40f2-47ff-858f-cdd69d21d667` / `d7dbcbaf-dfaf-516c-aae0-8b149f74970b` |
| 素材 | §1.2 と同一（SHA-256 `07904c1d…` / 9,971,886 bytes）。実行rootへ複製し digest照合済み |
| 直前の変更 | `LongFormProvenanceSnapshot` の `SourceSpans` を保存時と同じ正規順で保持する修正（`P2-11`）のみ |

### 3.2 §2 の停止箇所の解消（`P2-11` の効果の実測）

保全された `checkpoint-diagnosis.txt` の内容は次のとおりで、**`ValidateCheckpoint` の7条件すべてが一致**した（§2.4.1 では `cond2.plan` のみ不一致だった）。

| 条件 | §2.4.1（修正前） | 本節（修正後） |
|---|---|---|
| cond1 `Child` canonical JSON | 一致 | 一致 |
| **cond2 `Plan`** | **不一致** | **一致** |
| cond3〜cond7 | 一致 | 一致 |

`source_only.reconciliation_required` は発生していない。**同一素材・同一手順で不一致が解消したことを実測した。**

### 3.3 時間の実測

| 区間 | 実測（scenario開始からの経過） |
|---|---|
| scenario実行開始（準備完了） | 70.216 秒 |
| 案件受理 | 71.460 秒 |
| ingest完了（callback.ingest） | 118.841 秒 |
| 文字起こし完了（callback.transcript、実ASR） | 148.423 秒 |
| 解析完了（callback.analysis） | 148.645 秒 |
| 構成完了（callback.composition） | 151.660 秒 |
| 生成runtime確定（callback.generated） | 152.390 秒 |
| レンダー待機開始 | 161.676 秒 |
| 評価待機開始 | 176.634 秒 |
| 失敗確定（execution.Failed） | 219.721 秒 |
| scenario終了 | 221.486 秒 |
| testhost全体 | 3分42秒 |

**時間予算の判定:** 準備1800秒に対し実測70.2秒、実行5400秒に対し実測150.2秒、シナリオ9000秒に対し実測221.5秒。**いずれも予算を超えていない。** ただし本実行は納品段階へ到達していないため、**納品まで含めた所要の上限は依然として未測定**である。§2 の初回試行で観測された準備予算超過は再現しなかった。

### 3.4 標準成果物（§18の1〜6）の生成有無

| 成果物 | 件数 | 根拠・理由 |
|---|---:|---|
| 社内向け16:9長尺版 | **1** | `98b8793e-9ca0-41d3-97cf-53a7e2f8d558.mp4` / 51,785,587 bytes。role `LandscapeClean` |
| 9:16短尺候補（最大3） | **0** | omission `ShortForm` / `NoGroundedCandidate`。**空・重複した動画は1件も作られていない** |
| 字幕焼込版 | **0** | omission `landscape-burn-in/srt/vtt` / `NoDisplayCues`（3.6参照） |
| 字幕なし版 | **1** | 上記長尺版が字幕なし版に相当する |
| 字幕file（SRT/VTT） | **0** | 同上（表示cueが0件のため） |
| 文字起こし（`transcript.json`） | **1** | `deliveries/ancillary/d7dbcbaf…/c8d588fc…/transcript.json` |
| サムネイル候補3枚 | **6** | 16:9×3・9:16×3（role `LandscapeThumbnail` / `VerticalThumbnail` 各3件） |
| 使用区間一覧（`used-source-ranges.json`） | **1** | `deliveries/ancillary/d7dbcbaf…/c8d588fc…/used-source-ranges.json` |
| 自動代理評価記録 | **7** | 成果物7件それぞれに独立した評価run |

`GenerationDisposition = Completed`。**アップスケールは実施された。** 承認済みffprobe（§1.1、SHA-256 `4c2f7309…`）で長尺版を再測定した結果は `width=1920` / `height=1080` / `codec_name=h264` / `r_frame_rate=24000/1001` / `duration=60.060000`。素材の 1280×720（§1.3）から **1920×1080 へ拡大され、frame rateは 23.976 fps のまま保持**された。画質の良否は測定しておらず `PASS` としない。

### 3.5 §18 の記録6項目

| # | §18の記録項目 | 状態と実測値 |
|---|---|---|
| ① | 入力digest、設定profile版、tool/model版 | **取得済み。** 入力SHA-256 `07904c1d…`、設定snapshot digest `5e306d24…`、文字起こしdigest `69cb3fd5…`、source PTS index digest `011a198b…`。model run 3件＝`explicit-editorial-phrase-dictionary`(`fr005-2026-08-31.v1`/`HubRadio.Deterministic`)、`silencedetect`(`stage0-2026-08-27.ffmpeg-silencedetect.v1`/`FFmpeg`)、`hubradio_worker.repetition`(`fr005-2026-08-31.repetition.v1`/`CPython`)。出典 `automatic-decisions.json` |
| ② | 自動候補、自動適用区間、未採用候補、warning | **取得済み。** `selectedCandidateIds` **0件**、`skipped` **0件**（自動適用した区間は無い）。warningは `PlatformUiUnmeasured` / `ThumbnailQualityNotEvaluated` / `LibraryAbsent`（music） / `NoEligibleNonDialogueCues`（music）。出典 `automatic-decisions.json`、`generation-evidence.json` |
| ③ | 字幕の読速、行長、行数、同期、safe zone判定 | **部分取得。表示cueが0件のため合否判定は成立しない。** 未解決warningとして `subtitles.readingSpeedMaximum`（`source-reading-speed-exceeded`）2件、`subtitles.cueGapMinimum`（`no-feasible-partition-with-source-gaps`）2件、`subtitles.cueMinimumDuration`（`source-duration-below-minimum`）1件を実測。safe zoneは `subtitles.platformUiMaskStatus` = `platform-ui-mask-not-measured` で**未測定**。出典 `subtitle-warnings.json` |
| ④ | codec、decode、duration、A/V同期、loudness、True Peak判定 | **取得済み。** `decodedSuccessfully=true`、`videoTimingContinuous=true`、`videoFrameCadenceExact=true`、video frame数 1,440、出力duration 3003/50（=60.06秒）、A/V開始offset 0・終了offset 0（**A/V同期は実測で0ずれ**）。**loudness `-14.27` LUFS（判定 `FAIL`）**、**True Peak `-0.97` dBTP（判定 `FAIL`）**。出典 `evaluation-receipt.json` |
| ⑤ | 各出力要素の`OriginRef`と使用素材digest | **取得済み。** `used-source-ranges.json` の `origins` は `sourceSpan` 3件、entryの `sourceSpan.sourceDigest` は `07904c1d…`（§1.2と一致）、`assetId` `db038af2-…`、`streamId` `audio:1` |
| ⑥ | 自動代理評価の`PASS`/`WARNING`/`FAIL`/`NOT EVALUATED` | **取得済み。** `LandscapeClean`: pass 6 / **fail 2** / warning 0 / notEvaluated 19。thumbnail 6件: 各 pass 3 / fail 0 / warning 0 / notEvaluated 24 |
| — | 人手確認（任意記録） | **未実施** |

### 3.6 停止の同定 — loudness / True Peak

| 項目 | 観測 |
|---|---|
| 実行phase | `Failed` |
| failure code | `source_only.output_evaluation_failed` |
| 直接の原因 | `LandscapeClean` の自動代理評価に `FAIL` が2件あり `HasBlockingFailures` が成立した |
| 規則1 | `loudness` → `source_only.loudness_outside_frozen_tolerance` |
| 規則2 | `truePeak` → `source_only.true_peak_above_frozen_limit` |
| 納品 | 成果物7件すべて `delivered=false`。納品受領証は作成されていない |

**フィルター側の実測値（`generation-evidence.json` の `Loudness`、AAC符号化前の `render-filter` 段階）**

| 項目 | 実測 |
|---|---|
| 目標 | `I=-16` LUFS / `TP=-1` dBTP / `LRA=11` |
| 入力（素材そのもの） | `I=-16.71` LUFS / `TP=-1.88` dBTP / `LRA=9.2` |
| 出力（正規化後） | `I=-14.21` LUFS / `LRA=6.6` |
| 正規化種別 | `dynamic`（単一passの `loudnorm`） |
| QCの最終ファイル再測定 | `I=-14.27` LUFS / `TP=-0.97` dBTP |

**事実として確定できること:** 入力素材は要求の許容範囲（[要求定義書 FR-020](../../docs/requirement-definition.md)：-16 LKFS ±1 dB、True Peak -1 dBFS以下）に**既に収まっていた**（-16.71 は ±1 の範囲内、-1.88 は -1 以下）。正規化後の出力は **-14.21/-14.27 LUFS**、**-0.97 dBTP** となり、**いずれも許容範囲外**になった。適用されたのは単一passの `loudnorm`（`linear=false`、[FfmpegCompiler.cs:1120-1123](../../src/HubRadio.Infrastructure/Rendering/FfmpegCompiler.cs)）であり、目標値は [StandardRenderConfiguration.cs:108](../../src/HubRadio.App/Rendering/StandardRenderConfiguration.cs) が `audio.internalOnlineTargetLoudness` / `audio.truePeakMaximum` から束縛している。

**本節で断定しないこと:** 単一passの `loudnorm` が常にこの乖離を生むとは断定しない。本件は**この素材・この設定での1回の実測**である。修正方針（2-pass化、既に範囲内の素材への非適用など）は**未決定**であり、本タスクでは実装していない。**QCの許容値を緩めて通すことは症状隠しであり行わない。**

### 3.7 付帯成果物の昇格位置（`P2-21` の実測根拠）

納品段階が `source_only.output_evaluation_failed` で失敗したにもかかわらず、`transcript.json` と `used-source-ranges.json` は**両方とも** `projects/<projectId>/deliveries/ancillary/<versionId>/<exportId>/` 配下に存在した。すなわち**付帯成果物は納品トランザクションの成立前に `ArtifactKind.Delivery` として昇格されている**。詳細な判定は [v2-013 レビュー追補](../../docs/dev/reviews/v2-013-ancillary-artifacts-adversarial-review.md)に記録する。

### 3.8 本節で主張しないこと

- **`V2-014` は合格していない。** 納品・再読込まで到達していない。
- ASR精度、知覚品質、人手評価は**実施しておらず `PASS` としない**。実ASRの発話数は**2件**であったが、これは精度の測定ではない。
- アップスケール結果は§3.4で測定した。納品まで含めた所要上限は**未測定**である。
- 実利用者profile・本番DB・`Music/` は変更していない（実行rootのみを使用）。
- native GUI受入（`UX-041`/`UX-043`）、clean OS受入、署名、外部公開は**対象外・未実施**である。
- §2 の不合格記録は履歴として保全しており、1文字も改変していない。
