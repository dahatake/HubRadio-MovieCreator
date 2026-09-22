# `V2-012` 過去版復元UI/RPC 敵対的レビュー

**対象タスク:** `T-20`〜`T-22b`（Wave 2 V群。計画は一時作業ファイル `work/20260921-2000-TaskExecutionPlan.md` にあり、**リポジトリには含めていない**）
**対象要求:** FR-030 版管理・過去版に戻せること、FR-032 追跡性
**レビュー実施:** 2026-09-22（実装者と独立した観点で実施）
**base commit:** `f1abbcb`

> [!IMPORTANT]
> 本書は実施済みの自動試験の結果のみを記録する。native GUI受入・人手評価・実素材受入は**未実施**であり、`PASS` とは記録しない。
> 本レビューをもって「`V2` 完了」「製品完成」「一般提供可」とは判断しない。`releaseApproved=false` は維持する。

---

## 1. 「復元」の一義的定義（`T-20` の成果）

[ui-workflow-contract.md「実装済みpipeline API」](../architecture/ui-workflow-contract.md)に次を定義した。

- `pipeline.versions`（**読取のみ**）: 当該案件の過去版を列挙する。版ID・digest・親版ID・実行ID・phase・結果の有無・現在版か否かのみを返す。
- `pipeline.restore`（**変更**）: 選んだ過去版の構成を**新しい子版として作り直し**（`VersionService.RollbackAsChild`）、その子版から**成果物を再生成**する（`SourceOnlyPipeline.Regenerate`）。現在版・過去版・旧成果物を**削除も上書きもしない**。

**「タブや警告の『戻る』」および `pipeline.read` の前回親版表示は復元ではない**旨を契約に明記し、過去版で現在版を置き換える操作は提供しないことを確定した。

---

## 2. レビュー7観点の判定

| # | 観点 | 判定 | 根拠 |
|---|---|---|---|
| 1 | correctness | **重大指摘あり → 修正済み** | 指摘1・2・6・10 |
| 2 | failure / 復旧 | **重大指摘あり → 修正済み** | 指摘1・2・8 |
| 3 | security / privacy | **PASS** | §4 |
| 4 | citation / scope | **PASS** | 変更ファイルはマッピング内 |
| 5 | 過大主張 | **指摘あり → 修正済み** | 指摘4・5・12 |
| 6 | 不要な抽象 | **指摘あり → 修正済み** | 指摘4・9。新規framework・イベントバス・履歴抽象は導入していない |
| 7 | 要求trace | **指摘あり → 修正済み** | 指摘4（到達不能だった） |
| — | 新規status／failure code／設定キー／依存 | **PASS（追加なし）** | status集合は既存6値のまま |

---

## 3. 指摘と反映結果

### 指摘1（High）: 2段階変更の部分失敗が「孤立head」を作り、以後の再生成を恒久的に阻害していた

- **内容:** `RollbackAsChild` の commit 後に `Regenerate` が例外（同時実行・設定revision移動・shutdown等）を投げると、working copy の head だけが新しい子版へ移り、それを参照する実行行が存在しない状態になる。UIが送る `expectedVersionId` は旧実行の版のままなので `pipeline.regenerate` が以後常に `conflict` になり、`pipeline.retry` も条件を満たさない。
- **反映:** `AdoptRestoredHead` を追加し、「実行行から参照されていない head が、対象版と同一要素を持つ子版である」場合は**新たに積み増さずその子版を採用**して `Regenerate` を再実行する。これにより**同じ復元操作を再試行するだけで回復**できる。
- **副次修正:** 対象版が現在の head と同一の場合は変更前に `conflict` を返すようにし、`RollbackAsChild` の `ArgumentException` が `invalid` として返る経路を塞いだ。

### 指摘2（High）: 未解決副作用ゲートを `pipeline.regenerate` だけが持っていた

- **内容:** 再生成は「保存済み結果の `StagingCleanupRequired` / `ReconciliationRequired`」を検査して `conflict` にするが、同じ `Regenerate` に至る復元はこの検査を通らなかった。
- **反映:** 当該検査を両メソッド共通の前段へ**引き上げた**。復元も再生成と同一の受付条件になる。

### 指摘3（Medium）: 読取専用の `pipeline.versions` がEditor権限を要求していた

- **反映:** `Demand` の免除対象を `PipelineRead or PipelineVersions` に拡張した。閲覧者は一覧を読めるが、復元は引き続き拒否される（`RestoreIsDeniedForAViewerAndResolvesNoPipeline` で検証）。

### 指摘4（Medium）: 生成物に到達経路がなく、文書が「実装済み」と過大主張していた

- **内容:** `pipeline.versions` は `PIPELINE_METHODS` に含まれず transport を通過できなかった。`createPipelineVersionsRequest` / `parsePipelineVersionsResponse` / `VersionHistoryPanel` / `versionHistory` prop はテスト以外から参照されていなかった。
- **反映:** **未使用のまま残さず、到達可能にした。**
  - bridge client に `sendPipelineVersions` / `onPipelineVersionsResponse` の専用レーンを追加（`pipeline.versions` は結果shapeが異なるため、既存のpipeline応答レーンとは分離）。
  - `PipelineSession` に `readVersions()` / `restore()` を追加し、`PipelineProvider` 経由で公開。
  - `WorkflowWorkspace` が仕上がり画面の表示時に一覧を1回読み、`ResultGallery` の `versionHistory` へ渡し、復元は既存の再生成前提条件（未保存編集なし・保存済み設定確認済み）を共有する。
- **`pipeline.restore` の応答形は既存の snapshot と同一**であり、新しい status も新しい応答種別も増やしていない。

### 指摘5（Medium）: 一覧の試験が空経路しか通っていなかった

- **内容:** `VersionListIsReadOnlyAndCarriesNoPathOrArtifactContent` は `notFound` のみを検査し、path非含有のassertは `versions` 配列が存在しない応答に対する空虚な検査だった。`WriteVersionsResponse` はどのテストからも実行されていなかった。
- **反映:** 実際の版を持つ fixture で `VersionListReturnsTheRealPastVersionsWithoutAnyPathOrArtifactContent` を追加。版ID・digest・実行ID・phase・`hasResult`・`isCurrent`・`workingCopyRevision` を実値で検証し、workspace root・`storageReference`・`stagingPath`・成果物IDが応答に現れないことと、**読取前後で実行行の `StateDigest`・`Revision` が不変**であることを確認する。

### 指摘6（Medium）: `isCurrent` が working copy head と食い違い得た

- **反映:** `ListVersionHistory` の `isCurrent` を、最新実行の版ではなく `WorkingCopy.BaseVersionId` から導出するよう変更した。

### 指摘7（Medium）: 一覧読取が系譜全体の再検証を伴い高コストだった

- **反映:** `VersionService.GetVersion`（evidence系譜を遡って再検証する）をやめ、`ProjectStore.GetCompositionVersion` で版行を直接読むようにした。一覧に必要なのはID・親版・digest・案件のみである。`VersionService` の生成もループ外へ出した。

### 指摘8（Medium-Low）: digest不一致を黙って一覧から落としていた

- **内容:** 実行行と版行が食い違う＝どちらかが改変されている状態を隠し、全件落ちると `notFound`（＝「過去版はない」という積極的な虚偽）になり得た。
- **反映:** `SourceOnlyResultException(StoredEvidenceInvalid)` を送出し、`invalid` として返すようにした。**短い一覧で正常を装わない。**

### 指摘9（Low・YAGNI）: 未使用フィールド

- **反映:** `SourceOnlyBridgeHandler.projects` を削除した。

### 指摘10（Low）: `sourceOnlyVersionEntry.phase` だけが開いた文字列だった

- **反映:** snapshot と同じ12値の閉じた列挙に変更した。

### 指摘11（Low・過大主張）: 復元の応答一致判定が恒真だった

- **内容:** `s.parentVersionId !== targetVersionId` は、子版IDが常に新規GUIDであるため必ず真になる。
- **反映:** `parentVersionId !== null` と `versionId !== targetVersionId` と設定digest一致を検査する形に改め、`pipelineSession.snapshotMatchesAttempt` 側でも「新しい実行であること」「親が直前のheadでも対象版でもないこと」「ingest jobが同一であること」を検査するようにした。

### 指摘12（Low）: 未読と空読を同じ文言で表示していた

- **反映:** `versions` を `readonly PipelineVersionEntry[] | null` とし、`null`＝未取得（「過去版はまだ取得していません。」）、`[]`＝取得して0件（「過去版の記録はありません。」）、`unavailableReason`＝取得失敗、の3状態を区別して表示・検証するようにした。

---

## 4. security / privacy の確認（観点3の根拠）

- `SourceOnlyVersionHistoryEntry` と `WriteVersionsResponse` が出力するのは ID・digest・phase名・boolean のみで、path・storage reference・成果物本文・例外本文を含まない。
- 応答statusは既存6値のまま。`pipeline.versions` の非 `available` 応答も**同じ閉じた結果shape**（`status` / `workingCopyRevision` / `versions`）で返すよう `Fail` ヘルパーで統一した。
- 権限は現在のnative actorで判定する。一覧読取後に actor が変わった場合は `forbidden` を返す。復元は変更直前に `DemandCurrent` を行う。
- 応答サイズは100件上限（schema `maxItems` とTSの `array(…, 100)` が一致）。

## 5. 修正反映後の再試験（実測）

| Runner | 合計 | 成功 | skip | 失敗 |
|---|---|---|---|---|
| Architecture | 209 | 209 | 0 | 0 |
| Core | 732 | 731 | 1 | 0 |
| Infrastructure | 1,362 | 1,327 | 34 | 1（環境依存） |
| App | 2,424 | 2,416 | 8 | 0 |
| Web | 2,073 | 2,073 | 0 | 0（※） |
| Python worker | 170 | 169 | 1 | 0 |

- ビルド: 警告0・エラー0。`git diff --check`: 0件。
- **Infrastructure の1件**は `ProcessRunnerTests.RunAsync_HostCrashClosesJobAndKillsRootAndInheritedDescendant` で、本変更前のベースラインでも同一に失敗する Windows Error Reporting 依存の事象である（[quality.md 2026-09-22節](../quality.md)に実測値を記録）。**本変更に起因しない。**
- **※ Web:** 既定タイムアウト（5000ms）での実行では、本機の負荷により `ResultGallery` / `SettingsPanel` / `WorkflowWorkspace` の計4件がタイムアウトした。`--testTimeout=30000` での再実行では当該3ファイル143件が**全件成功**する。**製品欠陥ではなく実行環境の速度依存**と実測で確定した。上表の「失敗0」は延長タイムアウトでの確認結果であり、**既定タイムアウトで常に0であると主張するものではない。**

### 本変更が実際に壊した既存テスト（1件・修正済み）

`WorkflowWorkspace.test.tsx` の「画面往復で要求列が変わらない」assertは、新しい**読取専用**の `pipeline.versions` を検出して失敗した。テストの本来の意図（往復で作業を再受付しない）を保ったまま、`pipeline.versions` を除外した比較へ改め、`cancel/retry/regenerate/restore` と `jobs.cancel/retry` が0件であることを明示的にassertするよう強化した。

## 6. 本レビューで確認していない範囲

- 実際に `RollbackAsChild` → `Regenerate` が成功して子版と成果物が生成される**pipeline E2E** — **未実施**。現時点の検証は契約・権限・競合・応答形・UI操作に限られる。
- native GUI 上での復元操作（`UX-041`/`UX-043` は利用者指定で対象外）— **未実施**。
- 実素材による受入 — **未実施**（`T-31`）。
- 人手評価17項目・`UX-044` — **`PENDING`／未測定のまま**。
