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
