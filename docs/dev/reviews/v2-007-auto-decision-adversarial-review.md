# V2-007 可逆自動decision 敵対的レビュー

- 対象task: `V2-007`
- 対象要求: `docs/requirement-definition.md` 版0.2 Draft FR-005、FR-030、NFR-008
- task base: `34e08aca946101b7bd2149eca45c0ada623f4b91`
- レビュー日: 2026-09-04
- 状態: **REVIEW CLOSED — 個別task commit可能**
- 未解決Critical / Major / Minor: **0 / 0 / 0**

> [!IMPORTANT]
> 本レビューのPASS範囲は、明示cut、構造的隣接かつNFKC完全一致の反復後側、`-50 dB / 2.0 s`のsource先頭/末尾無音から0.30秒を保全する処理、の3規則だけをimmutable child Compositionへ適用し、canonical evidence、TimelineMap、atomic checkpoint、rollback-as-childで再現・取消できるcontractである。fuzzy意味判定、文脈逸脱推定、production品質、人による編集判断、未知の第4規則をPASSにしない。

## 1. 対象変更

現在の正本差分は16 pathである。復旧付録の11 pathに、既存public/schema test同期、silence timeline provenance修正、friend assembly設定を加えた実在差分を記録する。過去の11 path receiptを現在差分件数へ流用しない。

- `schemas/composition-version.schema.json`
- `src/HubRadio.App/Compositions/AutoDecisionEvidenceCodec.cs`
- `src/HubRadio.App/Compositions/AutoDecisionOrchestrator.cs`
- `src/HubRadio.App/Compositions/VersionService.cs`
- `src/HubRadio.Core/Compositions/CompositionEvidenceEnvelope.cs`
- `src/HubRadio.Core/Compositions/CompositionVersion.cs`
- `src/HubRadio.Infrastructure/Analysis/AudioExtraction.cs`
- `src/HubRadio.Infrastructure/Analysis/SilenceDetector.cs`
- `src/HubRadio.Infrastructure/Database/ProjectStore.cs`
- `src/HubRadio.Infrastructure/Database/SqliteStoreSupport.cs`
- `src/HubRadio.Infrastructure/Properties/AssemblyInfo.cs`
- `tests/HubRadio.App.Tests/AutoDecisionOrchestratorTests.cs`
- `tests/HubRadio.App.Tests/VersionServiceTests.cs`
- `tests/HubRadio.Core.Tests/CompositionSchemaTests.cs`
- `tests/HubRadio.Core.Tests/CompositionTests.cs`
- `tests/HubRadio.Infrastructure.Tests/ProjectStoreTests.cs`

## 2. 実装結果

- `AutoDecisionOrchestrator`は3つのclosed rule以外のcandidateを`UnknownRule`で拒否する。候補、rule/version/digest、ModelRun、SourceSpan、parent Composition、settings profile/snapshot、transcript、prior/new decision history、actor/time/reason、child、TimelineMapをcanonical evidenceへ固定する。
- 同一canonical inputからinput digest、child ID、element split ID、decision ID、TimelineMap IDを決定的に生成する。candidate順を変えても同じchild/digest/operationsになる。
- explicit cutはtimed tokenの完全な辞書一致だけ、repetitionは構造的隣接・NFKC完全一致の後側だけを対象にする。fuzzy、非隣接、untimed、重複、既決定、unknown ruleをmutation前に拒否する。
- silenceはversioned `-50 dB / 2 s` producer evidenceだけを受理し、parent source extentの先頭または末尾だけを対象に0.30秒を残す。middle silence、range外、overlapを拒否する。
- `AnalysisAudioTimeline`のcanonical parserを追加し、schema version、project/job/asset/artifact/digest/stream/time base/sample count、全派生値、mapping ruleを再生成byte一致で検証する。
- `SilenceCandidate` constructorはtimelineとsample boundariesからsource timeを再計算し、asset、source digest、audio stream、SourceSpan rangeの完全一致を要求する。canonical timeline自体をevidence payloadへ含め、load時にも同じconstructorを通す。
- `CompositionEvidenceEnvelope`はcanonical UTF-8 JSONとSHA-256を不可分にし、4 MiB UTF-8 byte envelopeをruntimeで適用する。既存versionはevidenceなしでround-trip可能に保つ。
- checkpointはoriginal draftをexpected state、child elementsをnew stateとして分離し、expected revision、project/base lineage、child content digest、validated evidenceを1 transactionで検証・保存する。競合時はexact same childだけをidempotent resultとして解決する。
- rollbackは保存済みcurrent childからstrict ancestorの内容を新しいchildへ複製し、過去version/evidence/review dataを変更・継承しない。

## 3. 採用したfindingと反映

| ID | 重要度 | exact symbol・再現・expected/actual | 反映と確認 |
|---|---|---|---|
| `V2-007-R01` | Major | `AutoDecisionEvidenceCodec.ReadSilencePayload`がwriterに存在しないnested `suggestion`を必須化。生成した正規evidenceをreloadするとexpected=同一child再構築、actual=`FormatException` | readerをwriterのclosed payloadへ一致させ、producer suggestionを外側bindingから渡す。apply/restart testで確認 |
| `V2-007-R02` | Major | `AutoDecisionOrchestrator.Apply`が編集後`PreparedWorkingCopy`をDBのexpected draftとして渡した。正規same-revision editでexpected=atomic checkpoint、actual=stored originalとのpayload不一致 | original `request.WorkingCopy`とnew child elements/digestを分離してcheckpointへ渡す。apply、duplicate、concurrent winner/loserをtest |
| `V2-007-R03` | Major | `ProjectStore.CheckpointWorkingCopy`のold payload/timestamp完全一致が、revisionはcurrentだがin-memory contentを編集した正規draftを拒否。expected=revision/base/project OCC、actual=編集自体を拒否 | old payload equalityをOCC条件から除外し、project/base/revision/time non-regression、transaction rollbackを維持。duplicate version/update failure/concurrency testで確認 |
| `V2-007-R04` | Major | `SilenceCandidate`をevidence codecから復元するためconstructorをpublic化すると、`SilenceDetectorTests.PublicSurface_IsImmutableAndDoesNotExposeDeletionOrThresholdMutation`がexpected public ctor 0 / actual 1で失敗 | constructorをinternalへ戻し、既存Core→Appと同じfriend assembly patternで`HubRadio.App`だけにtrusted reconstructionを許可。public surface test成功 |
| `V2-007-R05` | Major | silence payloadがsample indexとSourceSpanだけを保存し、両者の意味的一致を検証しなかった。sample値だけ改変してevidence digestを再計算するとexpected=semantic rejection、actual=別sample evidenceを受理可能 | canonical `AnalysisAudioTimeline`をpayloadへ固定し、constructorでsample→source range/asset/digest/streamを照合。`SilenceSamplesMustMatchCanonicalTimelineAndSourceRange`と再hash済みtamper testを追加 |
| `V2-007-R06` | Major | child checkpoint前にexpected content digestとの一致確認がなく、不整合callerがpartial stateへ進む余地 | `CheckpointAutoDecision`へexpected child digestを渡し、validated evidenceが再構築するchildと保存childを一致検証 |
| `V2-007-T01` | Test | `CompositionVersion.Evidence`追加後、immutable public property testとschema testが旧8 propertyのまま | optional get-only Evidenceとclosed envelope schemaを既存testへ同期。legacy constructor互換も維持 |
| `V2-007-T02` | Test | semantically equivalent future profile testがnew snapshotとold profileを混在させた不正fixtureを渡し、実装不具合に見えた | snapshotと対応するfuture profileを同時に渡し、canonical evidenceの`input.settingsSnapshot.profileDigest`をassert |
| `V2-007-T03` | Test/resource | 4 MiB runtime UTF-8 byte envelopeの直接負例がなかった | ASCIIで上限超過canonical JSONを生成し、`CompositionEvidenceEnvelope.Create`が`FormatException`を返すtestを追加 |

## 4. 不採用・再分類した懸念

| 指摘 | 判断 | 根拠 |
|---|---|---|
| schemaへ`maxLength: 4194304`を追加すべき | **不採用** | JSON Schema `maxLength`はUnicode文字数であり、契約はUTF-8 byte数。非ASCIIでは同値でなく正確な4 MiB byte envelopeを表現できない。schema commentとruntime byte testを正本とする |
| `InternalsVisibleTo("HubRadio.App")`はpublic API拡大 | **不採用** | external public surfaceはconstructor 0のまま。trusted application assemblyだけが同じvalidating constructorを利用し、既存Core→App patternとも一致する |
| evidence digestを再hashすればsemantic tamperを受理する | **修正済み** | digestだけでなく`ValidateLoaded`がrequestをdomain objectへ復元し`Prepare`を再実行、stored child snapshotとbyte比較する。sample tamperはtimeline→range不一致で拒否 |
| `canonicalJson` outer parse前に4 MiBを判定できないため無制限 | **不採用** | evidence object単体のruntime envelopeは4 MiBで閉じている。Composition snapshot全体のtransport上限を新規に発明する契約は本taskにない |
| 最初のWeb全回帰worker timeoutはV2-007 code defect | **environment transient** | V2-007はWebを変更していない。同一bytesを孤立再実行するとVitest 465、Node 10が成功し、その後canonical全回帰も完走した。失敗runはPASS件数へ含めない |
| rollbackで過去approvalも継承すべき | **不採用** | rollbackは新childであり、旧approvalを自動継承するとdigest-bound review契約を破る |

## 5. 機械検証

実行環境はWindows、PowerShell Core 7.6.5、.NET SDK 10.0.400、Node.js/Vite、CPython 3.12.10である。focused MTPは全て`--minimum-expected-tests 1`付きで0件実行を成功にしていない。

### 5.1 focused verification

| 対象 | 成功 | 失敗 | environment-gated |
|---|---:|---:|---:|
| `AutoDecisionOrchestratorTests` | 19 | 0 | 0 |
| `VersionServiceTests` | 16 | 0 | 0 |
| `CompositionTests` | 9 | 0 | 0 |
| `CompositionSchemaTests` | 10 | 0 | 0 |
| `ProjectStoreTests` | 15 | 0 | 0 |
| `SilenceDetectorTests` | 9 | 0 | 1 |
| **合計** | **78** | **0** | **1** |

### 5.2 canonical Release全回帰

| Runner | Success | Failure | environment-gated |
|---|---:|---:|---:|
| Architecture .NET | 46 | 0 | 0 |
| Core .NET | 663 | 0 | 1 |
| Infrastructure .NET | 716 | 0 | 30 |
| App .NET | 1,034 | 0 | 3 |
| Web Vitest | 465 | 0 | 0 |
| Accessibility Node | 4 | 0 | 0 |
| Web asset/dependency | 6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

全runnerはexit 0、failure 0で完走した。.NETは2,493件中2,459成功、34件environment-gated。Web/Nodeは475成功、Pythonは104成功である。restore、dependency consistency、format/analyzer、warnings-as-errors build、TypeScript typecheck、production bundle、Python syntax、`git diff --check`も成功した。Vite chunk size warningは残り、V2-007 defectまたは性能PASSへ読み替えない。

## 6. 静的・asset監査

- actual changed/untracked path: 16件。allowlist外0件、欠落0件。
- 0-byte source: 0件。
- changed media path: 0件。
- credential/token/private-key pattern: 0件。
- VS Code diagnostics: 0件。
- `composition-version.schema.json`: parse済み、schema test成功。
- `git diff --check`: PASS。
- `Music/`は本task差分に含まれず、直前の正本receipt 36 MP3、149,081,539 bytes、reparse 0、regular non-MP3 0、aggregate `f0763e5f17b508b2b717a219b2728b6e342379c4f8cae136c5700b7e5e6fa814`から変更していない。

## 7. 残余scope

- pinned packaged FFmpegが利用できる環境での実silence control: **environment-gated**。parser/domain contractはsynthetic testでPASS。
- symlink/reparse、cross-account ACL、non-Windows経路、Stage 0 work files: 既存environment gateとして分離。
- semantic/human edit quality、production media適合、external publication、legal/rights、package/release: **NOT EVALUATED / PENDING**。
- standard long/short/subtitle/thumbnail composition planningはV2-008、render/QC/deliveryはV2-013が所有する。

## 8. closure

独立correctness、security/privacy/reliability、requirements/schema/traceability/YAGNI reviewを実施した。candidateはexact symbol、明示契約、再現、expected/actualの4点が揃う場合だけ採用した。上記finding反映後、new qualifying findingは0、未解決Critical/Major/Minorは0である。

- **Closure status: REVIEW CLOSED — V2-007個別task差分はcommit可能。**
- **未解決Critical: 0件。**
- **未解決Major: 0件。**
- **未解決Minor: 0件。**
- main統合、V2-008以降、human/external/release gateは本個別reviewのPASSへ含めない。
