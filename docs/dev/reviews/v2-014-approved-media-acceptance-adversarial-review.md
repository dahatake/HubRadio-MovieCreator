# `V2-014` 承認済み実素材によるsource-only受入 敵対的レビュー

**対象タスク:** `T-30`〜`T-32d`（Wave 3 Q群）
**対象要求:** 要求定義書 §18 初期検証シナリオ、AC-001〜AC-006
**レビュー実施:** 2026-09-22（実装者と独立した観点で実施）
**base commit:** `fa20a0c`

> [!CAUTION]
> **受入は不合格である。** 承認済み素材でpipelineを実行したが標準成果物は1件も生成されなかった。
> 本書は実施した試験の結果のみを記録する。人手評価・native GUI受入・clean OS受入・署名・外部公開は**未実施**であり `PASS` とは記録しない。
> 本レビューをもって「`V2` 完了」「製品完成」「一般提供可」とは判断しない。**`releaseApproved=false` は維持する。**

---

## 1. レビュー7観点の判定

| # | 観点 | 判定 | 根拠 |
|---|---|---|---|
| 1 | correctness | **受入対象は不合格。レビュー対象成果物（試験と記録）はPASS** | opt-in E2Eは意図どおり実行され、失敗を失敗として報告した。§3の指摘1・2を反映済み |
| 2 | failure / 復旧 | **PASS** | 失敗時も実行rootを保全し、環境変数を復元し、pipelineとworker leaseを解放する。実利用者profile・本番DB・`Music/`・`sample/` に書き込まない |
| 3 | security / privacy | **PASS** | ログ・診断は安定codeと列挙値のみ。例外messageやpath・文字起こし本文・SIDを出力しない。§3の指摘2で設計どおりであることを再確認 |
| 4 | citation / scope | **PASS** | 変更は新規テスト1件と記録文書4件のみ。`src/` の差分は0件（`git diff --stat -- src/` で確認） |
| 5 | 過大主張 | **PASS（指摘3・4を反映後）** | 「実施済み」と「合格」を明確に分離した。取得できていない§18記録項目を取得済みと書いていない |
| 6 | 不要な抽象 | **PASS（指摘5を反映後）** | 新規interface・抽象クラス・Strategy/Factory・設定キー・NuGet/npm依存なし。既存 `SourceOnlyRealPipelineTests` は1行も変更していない |
| 7 | 要求trace | **指摘あり → 記録で反映** | §18の記録6項目を1:1で対応付け、③〜⑥が未取得であることを明記した（§3の指摘4） |

---

## 2. 実施した検証（すべて本機での実測）

| # | 検証 | 結果 |
|---|---|---|
| V-1 | 環境変数未設定で新規テストを実行 | `Inconclusive`（`approved_media.opt_in_required.HUBRADIO_RUN_LIVE_PIPELINE_TESTS`）。全回帰を壊さない |
| V-2 | `dotnet format --verify-no-changes` | 終了コード0 |
| V-3 | `build.ps1 -Configuration Release` | 0警告・0エラー |
| V-4 | 承認済み素材でのopt-in E2E | **失敗**。`source_only.reconciliation_required` / `PreparationNotCheckpointed`。3回再現 |
| V-5 | 対照: 既存の合成4秒媒体E2E | **成功**（4分26秒、納品・再読込まで完了） |
| V-6 | 保存済みcompositionの復元を2回実行しdigest比較 | 全digest一致（replayは決定的） |
| V-7 | 実行中の `planDigest` と保存済み `Plan.PlanDigest` の照合 | 一致（`0c6723c8…`） |
| V-8 | `src/` の差分 | 0件 |

---

## 3. 指摘と反映結果

### 指摘1（High）: 準備の時間予算が実測に基づいていなかった

- **内容:** `T-30` の初版は既存E2Eの150秒級の準備予算を素材長比で外挿し300秒としたが、準備の主コストは**素材長に依存しない** worker runtime 全2,697ファイル（268,991,381 bytes）の検証であり、初回実行で300秒を超過して失敗した。
- **反映:** 予算を1800秒へ引き上げ、**その根拠（何が固定費か、なぜ外挿が誤りか）をコード内コメントに明記**した。あわせて `dependencies.binaries_verified` / `dependencies.verified` / `model.copied` の中間milestoneを追加し、次回以降は推定ではなく実測で区間を判定できるようにした。
- **未解決として残した点:** 実行・シナリオ予算（1800/5400/9000秒）は依然**推定**である。今回は予算ではなく機能failureで停止したため**上限は検証されていない**。この旨を記録文書に明記した。

### 指摘2（High）: 受入harnessが不透明なgate codeしか報告せず、原因を特定できなかった

- **内容:** pipelineは生成段階の未分類例外を `source_only.reconciliation_required` という単一のgateへ畳み込む（[SourceOnlyPipeline.cs:1196-1206](../../../src/HubRadio.App/Projects/SourceOnlyPipeline.cs)）。初版のharnessはこのcodeしか記録せず、**運用者が「何が契約に反したか」を一切判断できなかった。** 受入記録としての価値が無い。
- **反映:** 生成系例外の**分類のみ**（型名＋定義済み列挙値）を記録する限定的な観測を追加した。これにより `PreparationNotCheckpointed` を特定できた。
- **越権していないことの確認:** 例外messageもpathも文字起こし本文も出力しない。対象は `StandardGenerationException` / `FfmpegCompilerException` / `SubtitleSegmentationException` / `FormatException`（内側の型名のみ） / `ArgumentException` / `InvalidOperationException` に限定し、それ以外は記録しない。productionコードは変更していない。
- **オーバーエンジニアリングではない理由:** 「受入で観測した失敗を同定できること」は `V2-014` の完了条件（標準成果物と**不成立理由**を実測する）に直接由来する。汎用のログ基盤・設定項目・抽象層は導入していない。

### 指摘3（Critical）: 「実施した」ことを「合格した」と読める記述の危険

- **内容:** 初稿の `V2-014` 行は「実施済み」とだけ書きかけており、読者が受入成立と誤読しうる。
- **反映:** 詳細実装計画・品質記録・受入記録のすべてで **「実施済み・不合格」** と明記し、標準成果物が**0件**であること、`V2-014` が**未完了**であることを併記した。

### 指摘4（Critical）: §18の記録6項目を「取得した」と書けない

- **内容:** 生成に到達していないため、③字幕判定・④codec/decode/duration/A-V同期/loudness/True Peak・⑤`OriginRef`と使用素材digest・⑥自動代理評価は**原理的に取得できていない**。取得済みとする記述は捏造になる。
- **反映:** [initial-pilot.md §2.5](../../../benchmarks/results/initial-pilot.md) に6項目を1:1の表にし、①=取得済み、②=部分取得、③〜⑥=**未取得**と明記した。§2.6で標準成果物8種すべて**0件**であることを列挙した。
- **あわせて修正した誤解:** 短尺候補が3本未満である事実を「候補不成立」として記録しない。**理由は「生成未到達」であり、§18が求める不成立理由とは別物**である旨を明記した。

### 指摘5（Medium）: 受入チェックリストを前進させたい誘惑

- **内容:** `T-32a`/`T-32b` は `tests/Acceptance` の更新を予定していたが、今回の実測では**成立した項目が1つも無い**。`ActualMediaUsed` 等のscope flagは「**当該テスト自身**が実素材を使ったか」を表す宣言であり、別harnessでの実行をもってこれを `true` にすることは虚偽記載である。
- **反映:** `AC001_LongForm.cs` と `Must_VerticalManual.cs` を**1文字も変更しなかった**。`Q000Complete`/`Q001Complete` は `false`、人手15項目は `PENDING` のまま。判断理由を計画・品質記録の双方に残した。
- **確認:** `git diff --stat -- tests/Acceptance/` が0件であること。

### 指摘6（Medium）: 根本原因の主張範囲

- **内容:** 「不一致fieldはこれだ」と断定したくなるが、当初の実測で確定できたのはここまでであった — 復元は成功かつ決定的（V-6）、`FormatException`/`InvalidOperationException` は未発生、`PlanDigest` の範囲は一致（V-7）。
- **反映（当初）:** 記録は確定事実までに留め、**「不一致fieldは未特定である」と明記**した。推測を原因として書かなかった。
- **追試による確定（2026-09-22）:** `CompositionReady` が生成器へ渡すのと同一の preparation をテストへ渡すこと（[SourceOnlyRuntimeOptions.cs:150](../../../src/HubRadio.App/Projects/SourceOnlyRuntimeOptions.cs)、[SourceOnlyPipeline.cs:2204-2213](../../../src/HubRadio.App/Projects/SourceOnlyPipeline.cs) と [:1349](../../../src/HubRadio.App/Projects/SourceOnlyPipeline.cs)）を利用し、**productionコードを1行も変更せずに** `ValidateCheckpoint` の7条件を再現した。不一致は **`Plan` のみ**で、差分は `LongForm.Provenance.SourceSpans` の**順序のみ**（集合は同一、`sameSetIgnoringOrder=true`）。`Digest<T>` が配列順序に依存するため生成が拒否される。合成媒体は machine transcript の utterance が**0件**（保全された実行rootで実測。「無音だから空」という仮定ではない）でspanが1件のみのため順序差が生じ得ず成功する。2回再現。
- **なお未確定:** どちらの順序が正本かは決めていない。修正はFR-032の並びの意味づけと保存済みdigestへの影響評価を伴い、`V2-014` の範囲外である。

### 指摘7（Low・未修正）: 生成系例外の分類が同一実行で6回記録される

- **内容:** `PreparationNotCheckpointed` が1回の実行で6件記録された。呼び出し経路の重複か再試行が示唆されるが、**本受入の結論には影響しない**。
- **未修正の理由:** 原因調査はproductionコードの挙動解析を要し、受入の範囲外である。事実のみ記録する。

---

## 4. 本レビューが確認していない範囲

- ASR精度、知覚品質、アップスケール結果、人手評価（実施していない）
- 実行・シナリオ時間予算の妥当性（機能failureで停止したため未検証）
- native GUI操作（`UX-041`/`UX-043` は利用者指定で対象外）、clean OS受入、署名、外部公開
- `Q-002`/`Q-004`/`Q-006`/`Q-008`/`Q-010`/`Q-011`/`Q-012`（未着手）
- OSL残件（`OSL-016`/`017`/`020`/`021`/`028`）

## 5. 結論

`T-30`〜`T-32d` の成果物（opt-in受入harnessと4件の記録）は7観点でPASSとする。
**ただし `V2-014` そのものは不合格・未完了である。** 実素材で標準成果物が生成されない既知の欠陥が残っており、
`G-Q` ゲートは通過していない。`releaseApproved=false` を維持する。

---

## 6. 追補（`P2-15` / 2026-09-23）— 群A（`P2-11`〜`P2-14`）の敵対的レビュー

> 本節は追補である。§1〜§5 の**不合格記録・指摘・判定を1文字も変更していない**。

### 6.1 レビュー対象

| 対象 | 内容 |
|---|---|
| production変更 | `LongFormProvenanceSnapshot` のコンストラクタで `SourceSpans` を保存時と同じ正規順に整列（[LongFormComposer.cs](../../../src/HubRadio.App/Compositions/LongFormComposer.cs)）。`src/` の差分は**この1ファイル・16行追加/1行削除のみ** |
| 試験追加 | `StandardCompositionOrchestratorTests.RestoredPlanMatchesTheCheckpointedPlanWhenProvenanceArrivesUnordered`（既存ファイルへの1ケース追加） |
| 記録 | [initial-pilot.md §3](../../../benchmarks/results/initial-pilot.md)、[implementation-plan.md `V2-014` 行](../planning/implementation-plan.md)、README、本書 |

### 6.2 7観点の判定

| # | 観点 | 判定 | 根拠 |
|---|---|---|---|
| ① | correctness | **合格** | §6.3 |
| ② | failure / 復旧 | **条件付き合格**（指摘10・Low を残す） | §6.4 |
| ③ | security / privacy | **合格** | §6.5 |
| ④ | citation / scope | **合格** | §6.6 |
| ⑤ | 過大主張 | **合格** | §6.7 |
| ⑥ | 不要な抽象 | **合格** | §6.8 |
| ⑦ | 要求trace | **条件付き合格**（`V2-014` は未達のまま） | §6.9 |

### 6.3 ① correctness — 修正が「症状隠し」でないことの独立確認

**指摘8（Critical・反映済み）: 比較条件の緩和で通してはならない。**

独立に確認した事実は次の3点である。

1. **比較側を一切変更していない。** `git diff --stat -- src/` の結果は `LongFormComposer.cs` の1ファイルのみであり、比較を行う [StandardArtifactGenerator.cs](../../../src/HubRadio.App/Rendering/StandardArtifactGenerator.cs) の `ValidateCheckpoint`（7条件）と [StandardRenderGraphFactory.cs](../../../src/HubRadio.App/Rendering/StandardRenderGraphFactory.cs) の `Digest<T>` は**1行も変わっていない**。順序依存の比較はそのまま残っている。
2. **原因側を保存形式に合わせた。** 不一致は「同じ集合が2つの異なる順序で存在すること」であった。保存側 [StandardCompositionEvidenceCodec.cs の `ProvenanceDto.From`](../../../src/HubRadio.App/Compositions/StandardCompositionEvidenceCodec.cs) は以前から `(AssetId, StreamId, Range.Start, Range.End, Id)` で正規化して保存していた。**保存される形は今回の変更でも一切変わらない**（実行中の並びが保存時の並びへ一致しただけ）。よって既存の保存済みcanonical JSONとの後方互換は保たれる。
3. **回帰試験が修正前に失敗することを実測した。** 修正を一時的に戻して再ビルドし、新規試験は `expected: e92107d5…` / `actual: 7d24af9f…` の**plan digest不一致で失敗**した。修正を戻すと成功する。**この試験は症状ではなく原因を捕捉している。**

**指摘9（Medium・反映済み）: 正規順の定義が2箇所に存在する。** コンストラクタと codec の双方が同じ並び替えを持つため、片方だけが変更されると不一致が再発し得る。**反映:** 新規回帰試験は「実行中のplan」と「復元後のplan」の全体digestを比較するため、**両者の定義が乖離した瞬間に失敗する**。定義の一本化（codec 側の `OrderBy` 削除）は行っていない。理由は、production差分を最小に保つためであり、乖離は上記試験で検出できるからである。

**なぜその順序が正本か（要求・不変条件による説明）。** 保存された証跡（`CompositionEvidenceEnvelope` のcanonical JSON）が唯一の監査対象であり、その並びは以前から codec の正規順である。実行中の並び（文字起こし由来span→全体包絡spanを末尾に追加、[SourceOnlyPipeline.cs:2473](../../../src/HubRadio.App/Projects/SourceOnlyPipeline.cs)）は収集順にすぎず、**要求にも文書にも意味づけがない**。FR-032（追跡性）は各要素の `OriginRef` と区間値で担保されており、**配列順序に依存しない**。よって保存側の順序を正本とするのが整合的である。

**順序非依存性の確認。** `LongFormProvenanceSnapshot.SourceSpans` の全利用箇所を読み、辞書化・`Any`・集合化・再整列のいずれかであって列の並びに依存しないことを確認した（`LongFormComposer.ValidateProvenance`、`StandardCompositionPlan.ValidateLongFormContext` / `ValidateOutputBindings`、`StandardCompositionPlanner.ValidateSourceEvidence`（独自に再整列する）、`SourceOnlyApplicationRuntime`、`SourceOnlyPipeline`）。`PlanDigest` は long-form 側が `span.Id` で整列してから算出するため（[LongFormComposer.cs `ProvenanceProjection`](../../../src/HubRadio.App/Compositions/LongFormComposer.cs)）**順序非依存であり、既存の保存済みdigestは引き続き一致する**。これは §2.4 O-5（`planDigest` が一致していた事実）とも整合する。

### 6.4 ② failure / 復旧

**指摘10（Low・未修正）: `sourceSpans` に `null` 要素があると、コンストラクタで `NullReferenceException` になる。** 従来は後段の `LongFormComposer.ValidateProvenance` が `ContextMismatch` を返していた。

- **未修正とした理由:** 同じ形の `null` は以前から codec の `ProvenanceDto.From` で同様に `NullReferenceException` になる。すなわち**新しい失敗の種類を増やしていない**。`null` を弾く検査を追加すると、既存の `ContextMismatch` 経路との重複や例外種別の変更を招き、要求にない予防的エラーハンドリングになる。
- **未修正であることを隠していない。** 該当経路を対象にした試験は存在しない（**未確認**）。

**復旧経路は変更していない。** `PreparationNotCheckpointed` / `source_only.reconciliation_required` のゲート自体は残っており、順序以外の不一致は従来どおり検出される。

### 6.5 ③ security / privacy

- 追加した試験・記録に素材本文・文字起こし本文・利用者SID・利用者profile pathは含まれない。記録したのは digest、GUID、件数、時間、リポジトリ相対pathのみである。
- 実行は隔離rootのみを使用し、実利用者profile・既定アプリケーションディレクトリ・本番DBを変更していない（[initial-pilot.md §3.8](../../../benchmarks/results/initial-pilot.md)）。
- 権限・承認・公開範囲に関する判定は変更していない。

### 6.6 ④ citation / scope

- §3 の各数値は保全された実行root `approved-media-3d95736adfa048f5a45a6de1fe4209b5` 内の成果物（`checkpoint-diagnosis.txt`、`evaluation-receipt.json`、`generation-evidence.json`、`automatic-decisions.json`、`subtitle-warnings.json`、`used-source-ranges.json`）と、承認済みffprobeによる再測定に基づく。
- 出力解像度は**推測せず**、承認済みffprobe（SHA-256 `4c2f7309…`）で実測した。
- 変更ファイルは §6.1 の範囲に限られる。新規 NuGet / npm / pip 依存、新規設定キー、`DefaultSettingsProfile` 版の変更はいずれも**無い**。

### 6.7 ⑤ 過大主張

- **`V2-014` を合格と書いていない。** §3 の見出しは「不合格（FAIL）」であり、停止箇所が変わったことだけを記録している。
- ASR精度・知覚品質・人手評価・clean OS・署名を `PASS` と書いていない。実ASRの発話数2件は**精度の測定ではない**と明記した。
- 「V2完了」「製品完成」「一般提供可」とは書いていない。`releaseApproved=false` を維持している。
- **新たに判明した不具合を隠していない。** `loudness` / `truePeak` の `FAIL` は §3.6 に実測値付きで記録し、**QCの許容値を緩める対処は行わない**と明記した。

### 6.8 ⑥ 不要な抽象

新規のクラス・interface・Strategy/Factory・設定キー・failure code・依存関係はいずれも**追加していない**。production変更は既存コンストラクタ内の1式である。試験は既存ファイルへの1ケース追加である。

### 6.9 ⑦ 要求trace

| 要求 | 状態 |
|---|---|
| §18（初期検証シナリオ） | 記録6項目のうち①②④⑤⑥を実測取得。③は表示cueが0件のため**判定不成立**。**シナリオ全体は未達**（納品未到達） |
| FR-031（出力） | quick start標準成果物のうち長尺・字幕なし版・文字起こし・サムネイル・使用区間一覧が生成された。短尺と字幕は**不成立理由付きで0件**。空の偽物は作られていない |
| FR-032（追跡性） | `used-source-ranges.json` の `origins`／`sourceDigest` が素材digestと一致。順序変更は追跡性を損なわない（§6.3） |
| FR-020（音声補正） | **未達。** 出力の loudness / True Peak が -16 LKFS ±1・-1 dBFS以下を満たさない |
| AC-001〜AC-006 | **いずれも未達のまま。** 納品・再読込へ到達していないため、受入チェックリストは1項目も更新していない |

### 6.10 未解決事項（本追補では解決していない）

1. **`loudness` / `truePeak` の `FAIL`。** 入力素材は許容範囲内であったのに、単一passの `loudnorm` 適用後に範囲外となった（§3.6）。修正方針は**利用者判断**であり未実施。
2. **指摘10（Low）** の `null` 要素経路。
3. **納品まで通す統合試験（`P2-20`）** — 本追補の時点で**未実装**。
4. **Infrastructure の失敗1件**（`MediaImportSecurityTests.NonterminatingTool_IsKilledAtDeadlineWithoutPathDisclosure`）。本変更に起因しないことは、変更を戻した状態で同一に失敗することにより確認済みだが、**原因は未特定**である。
5. **Web の既定タイムアウトの負荷依存**。負荷のない状態では全件成功するが、負荷下では時間切れがファイル間を移動する。**コードは変更していない**（[品質記録](../quality.md)）。
6. 人手17項目・`UX-044`・clean OS・署名 — **未実施のまま**。

### 6.11 結論

群Aの production 修正は**原因側の最小修正**であり、比較条件の緩和ではないことを独立に確認した。**ただし `V2-014` は合格していない。** 停止箇所が生成前検査からQCへ移動し、§18の記録項目の多くが初めて実測で取得できた、というのが本追補の到達点である。

## 7. 追補（`P3-06` / 2026-09-24）— 第3版計画 `P3-*` の敵対的レビュー

### 7.1 レビュー対象と方法

対象は、第3版計画（`P3-00`〜`P3-41`）の実行で追加・変更した次の成果物である。**productionコード（`src/`）の差分は0件**である。

- [受入記録 §5](../../../benchmarks/results/initial-pilot.md)（`P3-00` ベースライン、`P3-13` 暫定判定、`P3-02` の判断材料）
- [UI workflow契約 §6](../architecture/ui-workflow-contract.md) の NOTE（`P3-40` 案(a)）
- 新規試験 `tests/HubRadio.App.Tests/SourceOnlyDeliveryIntegrationTests.cs`（`P3-20`）
- [品質記録](../quality.md) の 2026-09-23（23:44以降）の節、[開発開始手順 §9](../getting-started.md)（`P3-30`〜`P3-32`）、`README.md`、[詳細実装計画](../planning/implementation-plan.md) の `V2-014` 行、`CHANGELOG.md`（`P3-05`）

各タスクの完了後に、作成者とは別の独立レビューを行い、指摘をすべて反映してから再レビューで「残指摘なし」を確認した。

### 7.2 7観点の判定

| 観点 | 判定 | 根拠 |
|---|---|---|
| ① correctness | **OK（反映後）** | 2-pass の測定条件の誤り（解析passのTP不一致）を再測定で訂正。`ProbeAsync` が source-only の構成経路上にあること、長尺版が非手動layoutの fit＋pad 分岐を通ることをコードで確認 |
| ② failure / 復旧 | **OK** | `P3-30` は(c)現状維持で未解決failureとして記録を継続し、`P3-31` は原因未特定と明記。skip／`Inconclusive` を成功に数えていない |
| ③ security / privacy | **OK** | 追記した記録に絶対path・SID・素材本文は含まない。実行rootはrepository相対の識別子のみ |
| ④ citation / scope | **OK（反映後）** | 実行rootを省略形から完全なIDへ修正。Win32 error の根拠となる診断scriptと生出力を保存し、「同条件の再現による推定」と限定 |
| ⑤ 過大主張 | **OK（反映後）** | 「新素材でE2Eを実行」を「新素材を指定して起動したが素材照合前に停止し、新素材は未処理」へ限定。`P3-13` を「判定済み」から「暫定」へ。対処案(a)の「実現できない」を「現行APIをproduction変更なしで使う範囲では」へ限定 |
| ⑥ 不要な抽象 | **OK** | 新規の抽象・設定キー・依存を導入していない。新規試験は既存 internal fixture を再利用し、harnessを複製していない（91行） |
| ⑦ 要求trace | **OK（反映後）** | `V2-014` 追補へ完全な実行root IDと `watchdog.log` を記載 |

### 7.3 主な指摘と反映

| # | 重要度 | 指摘 | 反映 |
|---|---|---|---|
| 1 | High | 最初に追記した受入記録 §5 が文字化けしていた（PowerShell 5.1 から python の標準入力へ渡した際に日本語がASCII化） | §5を削除し、UTF-8 file経由で書き直した。日本語として読めることを別途確認 |
| 2 | Medium | 「2-pass」の TP -1.1／-1.2 行が、解析passを TP -1 で行っていた | 解析passと適用passのTPを揃えて再測定し、表を差し替えた |
| 3 | Medium | `P3-13` を判定済みとするのは過大（pipeline出力の1frameが未取得） | 「暫定（未完了）」へ変更 |
| 4 | Medium | Win32 error の根拠が保存されていない | 診断scriptと生出力を保存 |
| 5 | Low | `P3-20` の試験が、再読込時の付帯成果物の再検証を失敗側で検査していない | 1byte改変→再読込で `StoredEvidenceInvalid` を検査する形へ置換（冗長な直接検査を削除） |
| 6 | Low | 受領証跡から付帯成果物を「逆引き」できると書くのは過大 | 「`BaseResultDigest` によりdigestで束縛している」と記載 |
| 7 | Blocking | `V2-014` の証拠IDが一意でない | 完全な実行root IDを記載 |
| 8 | Non-Blocking | 新素材を処理したように読める記述、対処案(a)の過剰断定、CHANGELOG内の不整合 | いずれも限定・統一した |

### 7.4 独立確認

- **症状隠しでないこと:** QCの上限値・判定規則・既存試験の検査内容は変更していない。新素材のSAR未指定は、既存試験が意図的に拒否している挙動であり、**実装を変えずに利用者判断事項とした**。
- **`P3-20` の検出力（変異試験の実測）:** `SourceOnlyDeliveryReceipt.CopyResult` から付帯成果物の引継ぎを外すと失敗し、`SourceOnlyResultService` の付帯成果物の再検証を無効化すると失敗する。いずれも復元後に `src/` の差分が0行であることを確認した。
- **決定性・replay:** productionコードを変更していないため、`FilterScriptDigest`・graph digest の算出は変わらない。

### 7.5 未解決事項

1. **新素材のSAR未指定の扱い**（利用者判断）。現行実装では生成段階で停止する見込み。
2. **サンドボックス内での実素材E2E**。`GetFinalPathNameByHandleW` の既定形式が拒否されるため、ポリシー変更またはサンドボックス外での実行が必要。
3. **loudness／True Peak の修正方針（`P3-02`）**。新素材でQCまで到達していないため保留。判断材料は[受入記録 §5.3.3](../../../benchmarks/results/initial-pilot.md)。
4. `P3-11`・`P3-12`（字幕規則・`Q-011`）は、新素材のASR・候補・cueが取得できていないため保留のまま。
5. Infrastructure の締切競合（`P3-30`）と App runner の異常終了（`P3-31`）は、今回の試行で再現しなかったが**原因は未特定**。
6. 人手17項目・`UX-044`・clean OS・署名 — **未実施のまま**。

### 7.6 結論

第3版計画で実行できるタスクは計画のデフォルトどおりに実施し、実行できないものは停止理由を実測で記録した。**`V2-014` は合格していない。** 新素材は、実行環境の制約（依存検証）と素材の性質（SAR未指定）の2点で生成段階へ到達していない。前者は利用者の環境設定、後者は仕様判断を要する。
