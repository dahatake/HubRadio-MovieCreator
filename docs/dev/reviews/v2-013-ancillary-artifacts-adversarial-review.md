# `V2-013` 付帯成果物（文字起こし・使用区間一覧）敵対的レビュー

**対象タスク:** `T-10`〜`T-17b`（Wave 1 R群。計画は一時作業ファイル `work/20260921-2000-TaskExecutionPlan.md` にあり、**リポジトリには含めていない**）
**対象要求:** FR-031 標準成果物set、FR-032 元動画との追跡性、NFR-008
**レビュー実施:** 2026-09-22（実装者と独立した観点で実施）
**base commit:** `f1abbcb`

> [!IMPORTANT]
> 本書は実施済みの自動試験の結果のみを記録する。人手評価・実素材受入・clean OS受入・署名は**未実施**であり、`PASS` とは記録しない。
> 本レビューをもって「`V2` 完了」「製品完成」「一般提供可」とは判断しない。`releaseApproved=false` は維持する。

---

## 1. レビュー7観点の判定

| # | 観点 | 判定 | 根拠 |
|---|---|---|---|
| 1 | correctness | **重大指摘あり → 修正済み** | 指摘1（後述）。修正後は新規試験で実証 |
| 2 | failure / 復旧 | **重大指摘あり → 修正済み** | 指摘2。原子性・ロールバック経路自体は無改変 |
| 3 | security / privacy | **PASS** | path・ファイル本文・例外本文が境界を越えないことを確認 |
| 4 | citation / scope | **PASS（軽微指摘あり）** | 変更ファイルはマッピング内。指摘9のみ |
| 5 | 過大主張 | **指摘あり → 修正済み** | 指摘5・6・7 |
| 6 | 不要な抽象 | **PASS（軽微指摘あり）** | 新規interface/抽象クラス/Strategy/Factory/設定キーなし。指摘8のみ |
| 7 | 要求trace | **指摘あり → 修正済み** | 指摘4（FR-031 の「不成立理由」） |

---

## 2. 指摘と反映結果

### 指摘1（Critical）: 付帯エクスポートが全ての実行で失敗する経路になっていた

- **内容:** `ExportAncillaryArtifactsAsync` が `import.SourceSpans` のみを渡していたが、source-only の composition 要素は `CreateFullSourceSpan` が作る**全体包絡span**（[SourceOnlyPipeline.cs:2915-2931](../../../src/HubRadio.App/Projects/SourceOnlyPipeline.cs)）を origin とする。この span は `import.SourceSpans` に含まれない（pipeline 自身が [:2447](../../../src/HubRadio.App/Projects/SourceOnlyPipeline.cs) で `Append(fullSource)` している）。よって `ValidateProvenance` が `ProvenanceInvalid` を投げる。
- **さらに深い問題:** 包絡spanを渡すと、今度は `ValidateTranscriptEvidence` の「当該revisionに束縛されたspanはすべてutterance spanでなければならない」という検査に落ちる（包絡spanは `TranscriptRevisionId` を持つがutterance spanではない）。**2つの検査が相互に矛盾していた。**
- **反映:**
  - `CompositionState` に composition が実際に使った span 集合（`decisionSpans`）を保持させ、それを exporter に渡すようにした。
  - `ValidateTranscriptEvidence` の最終検査を、「utterance span **または** token根拠を持つ span」に精緻化した（`IsTokenGroundedSpan`、[AncillaryArtifactExporter.cs](../../../src/HubRadio.App/Delivery/AncillaryArtifactExporter.cs)）。主張するtokenがすべて当該revisionの実在tokenであり、かつそのutterance範囲がspan範囲に収まることを要求する。**偽造されたtranscript由来主張は依然として拒否される。**
- **実証:** `ExportAsync_AcceptsAFullSourceEnvelopeSpanThatTheCompositionOriginatesFrom`（成功）と `ExportAsync_StillRejectsASpanThatInventsTranscriptProvenance`（`TranscriptBindingMismatch` を送出、成果物0件）を追加。

### 指摘2（High）: 包括的 `catch` が `ReconciliationRequired` を握り潰していた

- **内容:** `AncillaryArtifactExportException.ReconciliationRequired`（`ArtifactConflict` / `CleanupFailed` / `RegisteredArtifactInvalid` / 一部の `PromotionFailed`）まで警告級のomissionへ格下げしていた。片方だけ昇格に成功した場合の孤立成果物や、削除できなかったstagingディレクトリが黙って残る。
- **反映:** `AncillaryArtifactExportException` を明示的に捕捉し、`ReconciliationRequired` のときは既存の手動解決gate（`SourceOnlyExecutionPhase.ReconciliationRequired`）へ送る。それ以外は列挙した例外型のみを捕捉する（包括 `catch` を廃止）。動画7成果物はこの時点で既に永続化済みであり、escalation によって失われない。

### 指摘3（High）: 統合点に試験がなかった

- **内容:** `ExportAncillaryArtifactsAsync` を駆動する試験が皆無で、指摘1・2があっても全試験が緑のままだった。
- **反映:** exporter レベルで source-only の**実span形状**（utterance spans + 全体包絡span）を再現する試験を追加し、2成果物が実際に書かれることを assert した。pipeline全体のE2Eは `T-30`/`T-31`（opt-in・実素材）の責務として残す。**本レビュー時点でpipeline実行によるE2E検証は未実施**である。

### 指摘4（Medium）: `Other`/`UnrecognizedOmission` が FR-031 の「不成立理由」を失わせていた

- **内容:** どちらの成果物が欠けたのかすら記録されず、要求([requirement-definition.md:808](../../requirement-definition.md))の「不成立理由をmanifestへ記録する」を満たしていなかった。
- **反映:** `SourceOnlyResultOmissionRole` に `Transcript` / `UsedSourceRanges` を、`SourceOnlyResultOmissionReason` に `AncillaryExportFailed` を**末尾追加**（既存値の順序・数値は不変）。未生成の種別ごとに1件のomissionを記録する。
- **enum値追加の理由記録:** `T-13` のDoDは「増やす場合は理由を記録」を許容している。追加の根拠はFR-031の「不成立理由」要求そのものであり、投機的拡張ではない。Web契約（`pipelineProtocol.ts`）と `schemas/web-bridge.schema.json` の列挙も同時に更新した。

### 指摘5（Medium）: 実装されていない不変条件をdocとschemaが主張していた

- **内容:** 「欠けた項目は必ずomissionとして記録される」と書いていたが、`ValidateShape` はそれを検査していない。評価器の出力は付帯ステップ実行**前**に構築されるため、型不変条件として強制できない。
- **反映:** 主張を実態に合わせて修正した（型ではなくpipelineが対を保証する旨を明記）。schemaの `$comment` も同様に修正。**強制できない保証を書かない。**

### 指摘6（Low・過大主張）: 反射による空虚な契約テスト

- **内容:** 「grounded subset を要求できる」とdocに書きつつ、実体は `kinds` 引数の `IsOptional` のみを確認しており、`Prepare` が `kinds` を無視しても通る。
- **反映:** 当該テストを**削除**した。実挙動は `ExportAsync_WritesOnlyTheRequestedGroundedSubsetOfKinds` が検証している。

### 指摘7（Low・過大主張）: 「7成果物」と書いて2件しか検査していないコメント

- **反映:** コメントを実際の検査内容（このfixtureの2成果物）に修正し、あわせて種別ごとのomission表示を assert するよう強化した。

### 指摘8（Low・YAGNI）: 未使用exportと文言の二重定義

- **内容:** `ancillaryArtifactLabels` / `ancillaryArtifactFileNames` を export しながら消費者は同一ファイル内のみ。一方 `WorkflowWorkspace.tsx` は同じ文言をinlineで重複定義していた。
- **反映:** ラベルを1つのmapへ統合し、`WorkflowWorkspace.tsx` から import して使う形にした。**真実の源泉を1つにした。**

### 指摘9（Low・scope）: `work/` が未追跡

- **内容:** `.gitignore` は `/benchmarks/work/` のみを無視する。
- **判断:** 一時作業ファイルの置き場所は利用者指定であり、`.gitignore` の変更は本タスクの範囲外とした。本タスクが生成した一時ログ（`*.log`）は作業完了時に削除する。

---

## 3. security / privacy の確認（観点3の根拠）

- `SourceOnlyResultEvidenceReference` が運ぶのは `ArtifactId` / `ContentDigest` / `ByteSize` / `ArtifactKind` のみで、path・storage reference・ファイル本文を含まない。
- `AncillaryArtifactExportException.Message` はUIへ到達しない。
- 応答status集合は既存6値のまま増えていない。
- `SourceOnlyResultTests` の既存assertが、projectionに workspace root・`file:`・`stagingPath` が現れないことを継続検証している。

## 4. 後方互換性の確認

- `SourceOnlyResultDto.AncillaryArtifacts` は既定 `null` ＋ `JsonIgnore(WhenWritingNull)` のため、付帯成果物を持たない結果の canonical JSON は**キー自体が出現せず**、既存の保存済みJSONと byte 一致・digest 一致を保つ。
- `ParseCanonical` の `CheckArray` は `TryGetProperty` でガードしている。
- Web境界は `ToWebDto` が常に配列を実体化するため、閉じたschemaのキー数一致検査と両立する。
- 実証: `AncillaryArtifactsRoundTripAndAbsentOnesKeepTheExactLegacyBytes`。

## 5. 修正反映後の再試験（実測）

| Runner | 合計 | 成功 | skip | 失敗 |
|---|---|---|---|---|
| Architecture | 209 | 209 | 0 | 0 |
| Core | 732 | 731 | 1 | 0 |
| Infrastructure | 1,362 | 1,327 | 34 | 1（環境依存・後述） |
| App | 2,420 | 2,412 | 8 | 0 |
| Web | 2,067 | 2,067 | 0 | 0 |
| Python worker | 170 | 169 | 1 | 0 |

- ビルド: 警告0・エラー0。`git diff --check`: 0件。
- **Infrastructure の1件** `ProcessRunnerTests.RunAsync_HostCrashClosesJobAndKillsRootAndInheritedDescendant` は**本変更前のベースラインでも同一に失敗**する。原因は本機の Windows Error Reporting 処理により `Environment.FailFast` したプロセスの終了に6〜29秒かかること（3回測定: 28.89秒 / 9.04秒 / 6.17秒）であり、テストの15秒固定予算を超える。**環境依存と実測で特定済み**であり、本変更に起因しない。

## 6. 本レビューで確認していない範囲

- 実素材（`sample/Copilot  あなたの毎日の AI コンパニオン.mp4`）によるE2E実行 — `T-31` で実施予定。**未実施。**
- pipeline を納品まで通した統合検証 — **未実施。** 本レビュー後の再レビューで、この不足が原因で検出できていなかった**要件未達のCritical 2件**（納品時の付帯成果物消失、派生候補spanの誤拒否）が見つかった。詳細は[品質記録 2026-09-22 自己成果物の敵対的レビュー節](../quality.md)。
- pipeline を実際に走らせた場合の付帯成果物生成 — exporter レベルでは実証したが、pipeline E2Eは**未実施**。
- ASR精度・知覚品質・人手評価（17項目 `PENDING`）・`UX-044` — **未測定。**
- clean OS受入・署名・一般配布 — **対象外・未実施。**
