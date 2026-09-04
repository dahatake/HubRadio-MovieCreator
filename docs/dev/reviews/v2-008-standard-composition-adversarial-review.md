# V2-008 標準Composition計画 敵対的レビュー

- 対象task: `V2-008`
- 対象要求: `docs/requirement-definition.md` 版0.2 Draft FR-007〜FR-015、FR-021、FR-024、FR-030〜FR-032、NFR-002、NFR-003、NFR-006〜NFR-008、AC-001、AC-002、AC-005
- task base: `9f80cd52f1c699b120d101da8ca8f9dc4ecbeabb`
- base tree: `164d7e5d8798bf37e4a61a352ee635154463fd37`
- task branch: `task/v2-008-20260904`
- task commit: `84c330a9664eca1b71df75165752546219433572`
- integrated local main: `84c330a9664eca1b71df75165752546219433572`
- レビュー日: 2026-09-05
- 状態: **REVIEW CLOSED — V2-008 local main統合済み**
- 未解決Critical / Major / Minor: **0 / 0 / 0**

> [!IMPORTANT]
> 本レビューのPASS範囲は、source-only案件から、社内限定16:9長尺1件、成立した0〜3件の9:16短尺、字幕variant参照、固定またはぼかし背景layout、BGM割当receipt、実frame根拠がある場合の0〜3件のthumbnail候補、不成立理由、warning、入力digest、child `CompositionVersion`とcanonical semantic-replay evidenceを決定的に計画・checkpointするcontractである。実render、自動代理評価、delivery、人による知覚品質、法的適合、platform UI適合、外部公開、signed package、clean-machine install、release readinessはPASSにしない。

## 1. 対象変更とreceipt

`9f80cd52…`からのreview対象実装差分は27 path（modified 17 / untracked 10）である。本review文書を加えたcommit対象は28 pathとなる。

### 1.1 production / schema

- `schemas/composition-version.schema.json`
- `schemas/standard-composition-evidence.schema.json`
- `src/HubRadio.App/Compositions/AutoDecisionOrchestrator.cs`
- `src/HubRadio.App/Compositions/LongFormComposer.cs`
- `src/HubRadio.App/Compositions/StandardCompositionEvidenceCodec.cs`
- `src/HubRadio.App/Compositions/StandardCompositionOrchestrator.cs`
- `src/HubRadio.App/Compositions/StandardCompositionPlan.cs`
- `src/HubRadio.App/Compositions/StandardCompositionPlanner.cs`
- `src/HubRadio.App/Compositions/VersionService.cs`
- `src/HubRadio.App/Policy/ExportPolicy.cs`
- `src/HubRadio.App/Profiles/BundledRenderProfileLoader.cs`
- `src/HubRadio.App/Profiles/StandardRenderProfileFactory.cs`
- `src/HubRadio.App/Projects/ProjectCreationService.cs`
- `src/HubRadio.Core/Compositions/CompositionEvidenceEnvelope.cs`
- `src/HubRadio.Core/Projects/ProjectSnapshotJson.cs`
- `src/HubRadio.Core/Properties/AssemblyInfo.cs`
- `src/HubRadio.Infrastructure/Database/ProjectCreationDraftStore.cs`
- `src/HubRadio.Infrastructure/Database/ProjectStore.cs`

### 1.2 tests

- `tests/HubRadio.App.Tests/BundledRenderProfileTests.cs`
- `tests/HubRadio.App.Tests/IngestWorkflowTests.cs`
- `tests/HubRadio.App.Tests/ProjectCreationServiceTests.cs`
- `tests/HubRadio.App.Tests/StandardCompositionOrchestratorTests.cs`
- `tests/HubRadio.App.Tests/StandardCompositionPlanTests.cs`
- `tests/HubRadio.App.Tests/StandardCompositionPlannerTests.cs`
- `tests/HubRadio.App.Tests/VersionServiceTests.cs`
- `tests/HubRadio.Core.Tests/CompositionSchemaTests.cs`
- `tests/HubRadio.Infrastructure.Tests/ProjectCreationDraftStoreTests.cs`

各relative path、byte size、file SHA-256をordinal順、実TAB区切り、LF結合、UTF-8 BOMなし、末尾LFなしで計算した最終内容集約SHA-256は`ebafb201b83adac4e5e1378378fab1f349455ac05cadc0f66613c79f8f69c719`である。review前の完全patchは15,944行、621,774 bytes、SHA-256 `372bf80889575f2a01930cbba2d1001a4508b64cce1e575da95358d8800a6d97`だった。その後のproduction/schema変更はなく、既存公開surface testへ新しいimmutable型を追加し1/1成功した。

## 2. 実装結果

### 2.1 standard planとsafe defaults

- `StandardCompositionPlan`は社内限定long formを必須1件とし、1920×1080、H.264/AAC、source frame rate、neutral/no-logo、opening動画OFFを強制する。
- episode titleはProjectの自由入力をそのまま信頼せず、source assetのleaf filename stemから再導出し、Project content nameとexact一致させる。来歴は発話を偽らずsource assetへ結ぶ。
- program name、episode number、summary、CTA等の自由記述は、closed trusted editorial-record catalogの根拠がある場合だけ保持する。SourceSpan IDを付けただけの任意文は省略する。
- long form字幕variantはburn-in、subtitle-free、SubRip、WebVTTを各1件、shortはburn-inを1件だけ参照する。
- short CTAは同じplan内long formへの`ViewLongForm`だけで、URL、channel、form、community等を生成しない。

### 2.2 short候補、hook、layout

- shortは明示採用済みEmphasis/Highlightだけを対象にし、current transcript/topic、source digest、contiguous token、TimelineMap包含、15〜60秒を検証する。
- 同一Suggestion ID、同一SourceSpan IDの異なる内容、同一source range、overlap、stale、unmapped、上限超過をtyped exclusionまたはtyped failureへ分離する。
- approved scoreがないため架空scoreを追加せず、asset/stream/range/IDのcanonical順で最大3件へ切る。不足をduplicateや空成果物で埋めない。
- hookはoriginal transcript由来かつ`AnswerAfterExplicitQuestion`の明示根拠がある採用候補だけを使う。成立しなければhookなし。
- subject evidenceがないためface/material認識を主張せず、元映像全体を維持するfixedまたはblurred-source-background layoutへfallbackし、`SubjectRegionUnavailable`、fallback種別、`PlatformUiUnmeasured`を記録する。

### 2.3 thumbnail実frame経路

- thumbnail候補がない場合は0件＋`NoMeasuredSourceFrame`、1〜2件は実候補だけ＋`NoAdditionalMeasuredSourceFrame`、3件時は不足reasonなしとする。
- `StandardThumbnailEvidence.FromCompletedIngestAsync`だけを公開生成経路とし、完了済み`IngestWorkflowRunResult`、verified original、generated proxy、source `PtsIndex`、`ProxyFrameMap`、`ThumbnailSpriteIndex`を一体で検証する。
- source、proxy、sprite image、sprite indexの登録artifactを`ArtifactStore.OpenValidatedReadAsync`で実fileまで再hashし、ID、project、kind、content digest、byte size、開いたstream lengthをexact照合する。
- sprite ordinal順の先頭から、proxy→sourceへexactにmapできる一意な実source frameだけを最大3件採用し、source asset、frame sequence、PTSを保持する。
- blur、露出、余白、文字衝突のapproved評価規則がないためquality scoreを作らず、候補がある場合も`ThumbnailQualityNotEvaluated`を残す。
- canonical evidenceにはsource/proxy/sprite artifact receipts、PTS/frame-map/sprite-index digest、selection rule version、frame sequence/PTS/duration/key-frame evidenceを保存する。load時はimmutable receiptとしてsemantic replayし、current artifact health checkは新しい実行時入力生成に限定する。

### 2.4 profile、tool/model、music、Project正本

- measured profile自体はapproved fixtureへexact bindingしつつ、出力frame rateは固定29.97ではなく明示されたsource frame rateをlong/short両profile、profile ID/digest、plan inputへ固定する。
- tool digestとmodel digestを個別のtyped inputとしてplan identityとevidenceへ保存する。
- V2-009の`MusicPlanningResult`を再利用し、catalog digestだけでなくcue、track digest、assigned/unassigned reason、duck gain、loop/stretch禁止、warning、7 domainの`NotEvaluated`をplan/evidenceへ保存する。
- Project canonical snapshot serializerをCoreへ一本化し、Project creation draft payloadとprojects rowのProject payloadを分離する。旧public finalize overloadは維持する。
- standard checkpointはrequest Project canonical snapshot、stored Project payload、revision、scope、deadline、updated timestamp、payload digestを同じSQLite immediate transaction内でexact照合する。

### 2.5 child、persistence、replay

- parent CompositionとWorkingCopyのproject/base/elements/digestをexact検証し、parentを変更せず同内容のchild `CompositionVersion`へstandard plan evidenceを付ける。
- `CompositionEvidenceEnvelope`はclosed kind `autoDecision` / `standardCompositionPlan`だけを受け、canonical UTF-8 JSONとSHA-256を4 MiB envelope内で検証する。
- evidence writerは4 MiB bounded `IBufferWriter<byte>`を使い、profile bytes、単一text、direct evidence collectionsもcopy/encode前に上限を検査する。
- evidenceはProject、parent、working copy、settings profile/snapshot、measured profiles、source geometry/frame rate、tool/model、music、topic/candidate/adoption、thumbnail receipt、expected child/timeline digestをclosed DTO graphへ保存する。
- restart loadはknown kindごとにcodec dispatchし、rootから反復的にlineageを再生する。cycle、missing parent、unknown kind、noncanonical/duplicate/open JSON、digest mismatch、semantic tamperを拒否する。
- standard parentからの再計画は元のtyped AutoDecision input digestを引き継ぎ、standard evidence digestをAutoDecisionとして誤記しない。
- `CheckpointWorkingCopyExact`はProject、parent、working-copy canonical payloadと列をatomicに照合する。同一要求の競合時だけexact checkpointへ収束し、Project/parent/異なるrequestの競合を成功へ丸めない。
- legacy `VersionService.Checkpoint`は既存の編集済みWorkingCopy contractを維持し、standard/auto-decisionのexact snapshot経路だけを強化した。

## 3. 独立3軸レビュー

最新production/schemaを含む完全patchを固定し、次の3軸を独立read-only reviewした。

1. correctness: deterministic ID/digest、source/candidate binding、long/short/thumbnail/music、semantic replay、exact/legacy checkpoint、restart、concurrency。
2. security/privacy/reliability: cross-project/source/artifact substitution、same-ID different-content、canonical/duplicate/oversize JSON、actual artifact bytes、OCC、exception/path/secret leakage、internal-only scope。
3. requirements/schema/traceability/YAGNI: FR-007〜015、FR-021/024/030〜032、NFR-002/003/006/007/008、omission/warning、未測定claim、既存table/evidence再利用。

findingはexact file/symbol、違反contract、構築可能な再現入力または手順、expected/actualが揃うものだけを採用した。誤ったworktree、存在しないpath、build成功と矛盾するcompile指摘、理論上の懸念だけを示す候補は採用していない。

最終patch-only reviewではsecurityとrequirementsのqualifying findingは0件だった。correctness候補として出たcanonical非決定性、上限内`int` overflow、固定正値に対するdivision-by-zero、digest表記、UUID bit表現等は実装・上限・既存testに反し、具体的失敗を再現できなかったため不採用とした。closure時点の未解決Critical / Major / Minorは0 / 0 / 0である。

## 4. 採用したfindingと反映

| ID | 重要度 | 再現した問題 | 反映・確認 |
|---|---|---|---|
| `V2-008-R01` | Major | source span originを付けただけの任意ProgramName/Summary/CTAを保持でき、発話の意味根拠を偽れた | trusted editorial originだけを保持し、source-only自由記述を省略。正負testを追加 |
| `V2-008-R02` | Major | evaluation input digestがTopicMetadataのorigin等を落とし、意味の異なる入力が同digestになった | evidence codecのclosed DTO全体をdomain-separated digest化。衝突再現testをgreen化 |
| `V2-008-R03` | Major | candidate未参照のforeign/stale TopicMetadataをevidenceへ混入できた | Project、transcript ID/digest、topic range、important SourceSpanを全revisionで検証 |
| `V2-008-R04` | Major | `sourcePreserve`設定でも出力profileが30000/1001固定だった | source frame rateをlong/short profile、ID/digest、evidenceへ固定。25 fps round-trip test追加 |
| `V2-008-R05` | Major | tool/model versionのcontent digestがplan identityへなかった | Tool/Model typed input digestを追加し、変更時のplan identity差をtest |
| `V2-008-R06` | Major | Musicはcatalog digestだけでcue/track assignmentがplanから失われた | V2-009 `MusicPlanningResult`をplan/evidenceへ保存し、assigned music round-tripをtest |
| `V2-008-R07` | Major | filename-derived titleを最初の発話SourceSpanへ結び、来歴を偽っていた | titleをsource asset originへ変更し、filename stemとProject名をexact照合 |
| `V2-008-R08` | Major | standard parentから再計画できず、またstandard evidence digestをAutoDecision欄へ入れる余地があった | standard replanを許可し、元AutoDecision digestをtyped継承。2段checkpoint/restart test追加 |
| `V2-008-R09` | Major | exact retryがstored column/payload mismatchを成功扱いできた | version/working-copy列、canonical payload、updated timestampをexact比較 |
| `V2-008-R10` | Major | request parent/Projectとstored正本のexact比較がなく、再読不能childまたは未保存Project factsを保存できた | parent semantic load＋同一transactionのparent/Project exact snapshot照合を追加 |
| `V2-008-R11` | Major | parent source rangeとprovenance SourceSpan rangeを別々に自己整合させられた | planner/orchestrator双方で同一ID SourceSpan内の包含を検証 |
| `V2-008-R12` | Major | profile digestでCapabilitiesとUnmeasuredのcollection境界が衝突できた | collection tag/count/item tagをdigestへ追加し衝突test追加 |
| `V2-008-R13` | Minor | schemaはzero durationを受理しruntimeだけ拒否した | `positiveTime`/`positiveRational`を追加しschema/runtime parityをtest |
| `V2-008-R14` | Major | evidence生成後にしか4 MiBを検査せず、大きな入力collection/profile/textを先にcopyできた | input preflightとbounded writerを追加。oversize/Count TOCTOU負例をtest |
| `V2-008-R15` | Major | callerが有効なkindだが意味的に壊れたstored evidence parentを標準系譜へ接続できた | Apply前にstored evidence lineageをsemantic loadし、正規2段replanとの対向testを追加 |
| `V2-008-R16` | Major | 同一SourceSpan IDをprovenance/candidate/candidate間で異なるrange/tokenへ再定義できた | authoritative IDはexact content必須、candidate間も一意。採否前に拒否する負例を追加 |
| `V2-008-R17` | Major | production thumbnail経路が常時0件で、実frame evidenceを入力できなかった | completed-ingest PTS/frame-map/spriteとregistered bytesから0〜3候補を作る経路を追加 |
| `V2-008-R18` | Major | compact thumbnail metadataだけでactual artifactを主張できた | 公開factoryをcompleted ingest＋4 registered artifact実hash検証へ限定し、receipt tamper test追加 |
| `V2-008-R19` | Regression | exact working-copy timestamp検査をlegacy checkpointへ広げ、編集済みdraftの既存test 2件を壊した | exact pathだけに条件を限定。失敗2件と`VersionServiceTests` 18/18で再確認 |
| `V2-008-R20` | Regression | Project creation draft payloadをProject正本としても保存する旧設計とexact Project照合が競合した | Core canonical Project serializerへ一本化し、draft/project payloadを分離。public overload互換を維持 |

## 5. 不採用・再分類した候補

| 候補 | 判断 | 根拠 |
|---|---|---|
| standard plan専用table/migrationが必要 | **不採用** | planは既存`composition_versions`のversioned evidence envelopeへatomic保存される。別tableは二重正本になり、要求もしていない |
| `standard-composition-plan.schema.json`が別途必要 | **不採用** |公開persisted contractは`composition-version.schema.json`＋closed `standard-composition-evidence.schema.json`。plan domainはsemantic replayで再構築する |
| `CanonicalJson`が非決定的かもしれない | **不採用** | implementationはordinal canonicalizationを行い、反復、permutation、restart、実payloadの逐語round-tripが成功。具体的不一致なし |
| digest比較は秘密値なのでconstant-time必須 | **不採用** | 対象は公開監査用content digestでsecret authentication値ではない。timingによる機密漏洩scenarioを再現できない |
| deterministic UUIDのversion bitがSHA-256と矛盾 | **不採用** | IDはinternal deterministic opaque IDで、外部RFC v5準拠claimをしていない。seed/label/domain分離と再現性をtest済み |
| evidence collection itemは256 bytes未満かもしれない | **不採用** | count capはcopy前DoSを抑える保守的上限で、最終byte上限はbounded writerが独立強制する。小さいitemは安全性を弱めない |
| lineage全体に任意の深度上限を追加すべき | **将来hardening** | 現要求にclosed値がなく、反復walk、cycle検出、各evidence上限がある。任意thresholdを製品contractへ追加しない |
| pre-release旧Project payloadを自動backfillすべき | **不採用 / fail-closed** | 旧payloadからingest後asset、rights、実frame factsを安全に推測できない。旧rowは読取可能だが新standard planにはcanonical Project更新または再取込を要求する |
| thumbnail候補が常に0でよい | **当初fallbackからMajorへ再分類** | 実preview evidenceがある場合まで到達不能なのはFR-021を閉じないため、実artifact検証付きpositive経路を追加した |
| actual artifactはloadごとに再hashすべき | **責務分離** | evidence replayはimmutable creation-time receiptを検証する。current artifact healthは新規plan入力生成時に`OpenValidatedReadAsync`で検査し、render直前再検証はV2-013 scope |

## 6. 機械検証

実行環境はWindows、PowerShell Core 7+、.NET SDK 10.0.400、Node.js/npm/Vitest、CPython 3.12.10である。MTP focused testは`--minimum-expected-tests`を付け、0件実行を成功にしていない。

### 6.1 focused / component

| 対象 | 成功 | 失敗 | environment-gated |
|---|---:|---:|---:|
| StandardComposition plan/planner/orchestrator | 77 | 0 | 0 |
| IngestWorkflow（actual artifact連携を含む） | 57 | 0 | 0 |
| VersionService | 18 | 0 | 0 |
| AutoDecision | 19 | 0 | 0 |
| ExportPolicy | 27 | 0 | 0 |
| LongForm関連 | 22 | 0 | 0 |
| Bundled/standard profile | 20 | 0 | 0 |
| ProjectCreationService | 20 | 0 | 0 |
| ProjectStore / ProjectCreationDraftStore | 27 | 0 | 0 |
| Core composition schema/evidence | 12 | 0 | 0 |

新規testは先に完全修飾名で明示実行した。thumbnail public factoryはcompleted ingestのregistered artifactから生成するpositive caseと、sprite bytes改変のnegative caseを2/2成功させた。thumbnail artifact receipt rehash/tamperは2/2成功した。

### 6.2 実payload Draft 2020-12 validation

latest x64 App test assemblyのfixtureからdefault、adopted highlight、assigned music、thumbnail evidenceの4 payloadを実codecで生成し、PowerShell 7 `Test-Json`で`schemas/standard-composition-evidence.schema.json`へ照合した。

| Payload | UTF-8 bytes | SHA-256 | 結果 |
|---:|---:|---|---|
| 0 default | 119,988 | `0b0b0c880639dc46cb66b99cb518b8ab341155f1b3117c879b75ad9374d176de` | PASS |
| 1 adopted highlight | 123,243 | `b9245e9c371ffcc6821e521e7850dad1b99aa143046a918157ab1c33640ee65e` | PASS |
| 2 assigned music | 120,211 | `45d10905b627dfaf0ba77317c35da059ab80aab92c4fbf2bab37b2a0f21ad42b` | PASS |
| 3 thumbnail evidence | 121,898 | `95b3a35756f2c7ec0b063ec58d4378e93e52ce30eac7b00cbc48b7ebfe7a8b6f` | PASS |

### 6.3 canonical Release全回帰

`test.ps1 -Configuration Release`をtask worktreeで実行した。

| Runner | Success | Failure | environment-gated |
|---|---:|---:|---:|
| Architecture .NET | 46 | 0 | 0 |
| Core .NET | 665 | 0 | 1 |
| Infrastructure .NET | 726 | 0 | 21 |
| App .NET | 1,119 | 0 | 3 |
| Web Vitest | 469 | 0 | 0 |
| Accessibility Node | 4 | 0 | 0 |
| Web asset/dependency | 6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

.NETは2,581件中2,556成功、25件environment-gated、failure 0。Web/Nodeは479成功、Pythonは104成功、failure 0だった。restore、npm audit（脆弱性0）、hash-locked Python restore、`pip check`、TypeScript typecheck、production Vite build、runtime dependency closure、production asset検証、format/analyzer、warnings-as-errors solution build、Python syntax verificationも成功した。Viteの637.41 kB chunk warningは残り、性能PASSへ読み替えていない。

## 7. 静的監査

- VS Code diagnostics: 変更対象で0件。
- `git diff --check`: PASS。
- changed path: 27、0-byte changed file: 0。
- changed media file: 0。
- 追加行のabsolute user path hit: 0。
- secret/private-key/connection-string assignment pattern hit: 0。
- `HubRadio.slnx`のprobe副作用: 0。
- review前内容aggregate: `ebafb201b83adac4e5e1378378fab1f349455ac05cadc0f66613c79f8f69c719`。

source MOVと`Music/`は本taskで変更・追跡していない。今回のV2-008 closureでは大容量Git外assetを再hashしておらず、2026-09-04計画receiptを現在再検証済みとは主張しない。V2-014でbefore/after receiptを再取得する。

## 8. 残余scope

- 自動代理評価4状態、closed rule、human/NOT EVALUATED分離: `V2-010`。
- quick-start進捗・成果物・評価・任意self-review UI: `V2-012`。
- actual render、QC、delivery、render直前source/tool/music/artifact再検証: `V2-013`。
- 実素材end-to-end、package、signed install、clean machine、最終受入: `V2-014`。
- platform UI mask、human accessibility/可読性/理解、音楽・brand・speech・mix品質、rights/legal、external publication、SBOM/CVE、release approval: **NOT EVALUATED / PENDING**。
- symlink/reparse、non-Windows、cross-account、Stage 0 ignored work filesに依存する25 .NET test: **environment-gated**。
- pre-release旧Project rowから不足するasset/rights/frame factsを推測するmigration: 実装せずfail-closed。明示的なcanonical Project更新または再取込が必要。

## 9. closure

- **Closure status: REVIEW CLOSED — V2-008 local main統合済み。**
- **未解決Critical: 0件。**
- **未解決Major: 0件。**
- **未解決Minor: 0件。**
- task commit `84c330a…`はlocal mainへfast-forward済み。pushは実施していない。
- Wave Bの共有treeでV2-005 Web focused 54/54、source-only contract 3/3、V2-008 StandardComposition 77/77を確認した。quick-start finalizeからplan jobを自動起動するUI接続はV2-012/V2-013の責務であり、本taskでend-to-end完了とは主張しない。
- external、人手、法的、platform、package、release gateはPENDING / NOT EVALUATEDのまま維持する。
