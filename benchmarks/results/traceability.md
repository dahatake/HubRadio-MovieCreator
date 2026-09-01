# Q-005 Traceability Acceptance Results

- タスク: Q-005
- 状態: **合格（決定論的synthetic contract受入）**
- 実測日時: 2026-09-01T15:24:05.7335252+00:00
- 要求: FR-032、NFR-006、BR-004、BR-005、AC-005
- 実行基点commit: `ba96aeb54da8796598047520ae96e104cc65e974`
- acceptance result SHA-256: `5dc59f2c18c18f2f96cc0d5b71604c49020e33a3a6a233fc2ad74f8c5efe20bf`
- acceptance result UTF-8 byte length: 5,425 byte

## 1. 判定

Q-005の決定論的synthetic fixtureは、2個の正の長さを持つSourceSpan-origin区間で出力範囲`[0/1, 5/1)`を隙間なく分割した。各区間は`TimelineMap -> OriginRef(SourceSpan) -> SourceSpan -> Asset -> RightsRecord`へ解決でき、使用SourceSpan 2/2と使用Asset 1/1をdelivery evidenceへ結合した。

- 逆引き不能な出力時刻: 0件
- 解決不能なOriginRef: 0件
- 解決不能な使用素材の権利状態: 0件
- 使用区間inventory: 2件
- 機械起点字幕として識別したscene: 1件
- 人修正字幕として識別したscene: 1件
- SourceSpan digest破損negative control: 検出・拒否
- negative controlでvalidation後に呼ばれたapproval/artifact dependency: 0回
- canonical audit record: 3/3件
- audit record内のfixture本文・file/storage reference漏えい: 0件

`TimelineMapBuilder`が作成した区間はzero-based、正の長さ、相互に隣接し、合計範囲を完全分割する。各区間の開始・中点・3/4点・999/1000点の計8点でoutput -> source -> outputの往復一致を確認し、区間境界は後続区間へ、出力終端は範囲外へ写像された。この区間分割と区間内の線形offset写像により、有限の8点だけを「任意時刻」の代用にせず、全ての`0/1 <= t < 5/1`を同じ契約で解決する。

これは自動化されたsynthetic contract受入である。実媒体の処理、実ASR/LLM推論、実render、字幕品質、権利証憑の真正性、人による受入またはrelease承認を合格と主張しない。

## 2. 実行環境

| 項目 | 実測値 |
|---|---|
| OS | Windows |
| Architecture | X64 |
| PowerShell | Core 7.6.5 |
| .NET SDK | 10.0.400 |
| Test host | Microsoft Testing Platform / MSTest |
| Focused acceptance | minimum 1件を要求し、focused gate成功 |
| Build gate | Web dependency/build、.NET restore、Release x64 warnings-as-errors build成功 |
| Source freeze | 実行前後のHEADと選択17 source fingerprintが一致 |

## 3. 使用区間inventory

ID、actor SID、字幕本文、file名、storage reference、絶対pathはcanonical evidenceへ出力していない。

| Output half-open range | Source half-open range | Origin kind | 字幕由来 | 判断証拠 | Rights |
|---|---|---|---|---|---|
| `[0/1, 2/1)` | `[10/1, 12/1)` | SourceSpan | machine-origin、未変更 | synthetic no-inference contractへ結合した候補の明示的human adoption | Approved、external publication allowed |
| `[2/1, 5/1)` | `[20/1, 23/1)` | SourceSpan | Human child revisionで修正 | canonical transcript correction record | Approved、external publication allowed |

1件目のModelRunはSuggestion/decision/deliveryの型付き結合を試験する`no-inference` synthetic contract markerであり、実モデル実行の証拠ではない。acceptance JSONは`actualModelInferenceUsed=false`を保持し、reportも実AI性能または実AI生成を主張しない。

## 4. 逆引きchain

### Scene 1: machine-origin subtitle + human adoption

1. `TimelineMap`のoutput区間から`OriginRef(SourceSpan)`とsource区間を解決した。
2. `SourceSpan`から使用Asset、Asset content digest、Human child TranscriptRevision、未変更token evidenceを解決し、childのparentとedit recordからmachine source revisionへ辿った。
3. SourceSpan digestとAsset digestが一致し、AssetのRightsRecordが`Approved`かつexternal publication allowedであることを確認した。
4. explicit-evidence highlight candidate、synthetic no-inference ModelRun contract、human `Adopted` decision、adopted suggestion referenceが同じoriginへ結合されることを確認した。
5. adopted referenceを持つCompositionElement、TimelineMap、RenderGraph、RenderManifest、DeliveryManifestまでversion/digestを保持した。

### Scene 2: human-modified subtitle

1. `TimelineMap`のoutput区間から`OriginRef(SourceSpan)`とsource区間を解決した。
2. 元のmachine TranscriptRevisionを不変に保ち、Human child revisionのparent、actor-controlled edit record、変更token 1件を確認した。
3. source revision、child revision、edit recordをrepositoryのcanonical serializerで再生成し、3個のdigestが全て一致した。
4. revised SourceSpanからAsset digestとRightsRecordを解決し、Composition/Timeline/Deliveryの使用区間へ結合した。
5. transcript correctionのminimized canonical audit recordを保存し、本文をaudit reasonへ含めないことを確認した。

## 5. OriginRef種別の境界

正例で主張するtimeline originは`SourceSpan`だけである。AssetはSourceSpanを介して完全解決した。direct `Asset` originは今回のcompositionで使用しておらず、合格主張に含めない。

現行domainには`OriginRef.EditorialRecordId`タグはあるが、解決対象となる具体的`EditorialRecord` aggregate/storeが存在しない。trusted ID membershipを実体解決へ読み替えず、EditorialRecordの正例は作成も主張もしていない。acceptance JSONは次を明示した。

- `editorialRecordAggregateAvailable=false`
- `editorialRecordEvidenceClaimed=false`
- `directAssetTimelineOriginClaimed=false`
- `assetResolvedThroughSourceSpan=true`

したがって、存在しないrecordやAI証拠を捏造して0 unresolvedを作ってはいない。

## 6. Revision・digest・delivery整合性

| 検査 | 実測結果 |
|---|---|
| Composition elementsから再計算したcontent digest | 一致 |
| TimelineMapから再計算したdigest | 一致 |
| DeliveryManifestのCompositionVersion ID/digest | 一致 |
| machine source revision canonical digest | 一致 |
| Human child revision canonical digest | 一致 |
| transcript edit record canonical digest | 一致 |
| used SourceSpan count | 2/2 |
| artifact入力順を反転したused-source-span digest | 同一 |
| artifact入力順を反転したapproval-evidence digest | 同一 |
| artifact入力順を反転したdelivery-evidence digest | 同一 |
| Delivery artifact count | 2 |

## 7. Negative control

最初のSourceSpanについてAsset content digestと一致しないdigestを持つbroken validation contextを作成した。

1. `RenderGraphValidator`は`OriginDigestMismatch`を返した。
2. `DeliveryService.CreateAsync`は`DeliveryFailureReason.ProvenanceInvalid`で拒否した。
3. approval評価、artifact metadata read、artifact validationは全て0回だった。
4. broken evidenceをclamp、補完、trusted-ID化して成功へ読み替えていない。

negative controlはpositive deliveryとは別contextで実行し、positive evidenceを変更していない。

## 8. Auditとprivacy

`ProvenanceAuditService`を通して次の3種類を保存し、全てcanonical JSONであることを再確認した。

- `transcriptCorrection`
- `suggestionDecision`
- `delivery`

acceptance testはcore JSONを書き出す直前にfixture SID、字幕本文、file名、storage reference、Windows drive path（backslash/forward slash）、UNC path、WSL Windows mount pathを検査した。runnerは最終JSONについて同じpath種別、SID、user name、字幕本文、storage reference、credential assignmentを再検査した。全検査は一致0件だった。source fingerprintはrepository-relative pathだけを含む。

Acceptance JSONは標準出力から一時的にmemory/captureへ取り込み、SHA-256と実測値を抽出した後はrepositoryへ永続保存していない。canonical reportにはentity ID、actor SID、credential、字幕・transcript内容、raw path、storage referenceを含めていない。

## 9. Evidence source fingerprints

成功runの前後でHEADと次の選択17 sourceのSHA-256を比較し、全て同一であることを確認した。これはQ-005が直接使う主要実装の選択集合であり、transitive dependency全体のSBOMを主張しない。

| Relative source | SHA-256 |
|---|---|
| `tests/Acceptance/AC005_Traceability.cs` | `2f177c5f47a8e889cd882762b0871574f9412c274c654560ae3da1ad074e80c0` |
| `tests/Acceptance/Invoke-Q005TraceabilityAcceptance.ps1` | `f0cca23e2d1c41aef8405a8fe15dcbe86dcda5f82c030c51ed8fcbbbfc1c18f4` |
| `tests/HubRadio.App.Tests/HubRadio.App.Tests.csproj` | `1072a917353b00c19285cd9e43474c103d2857450b3d2fb10a32991b96ca0cd3` |
| `src/HubRadio.App/Analysis/SuggestionService.cs` | `83ee76625e12bf2c829e7821774a158e589246655ac8d55e35df0f3793b5db91` |
| `src/HubRadio.App/Delivery/DeliveryService.cs` | `50d12904ac41f67a62e80414139e591909aac6e31981f7193bc9ef2fce650485` |
| `src/HubRadio.App/Audit/ProvenanceAuditService.cs` | `90c152c971d612800cf4d07ac31f19f5530bf6c53f2c13b394934b2c0f44cf4f` |
| `src/HubRadio.App/Transcripts/TranscriptEditor.cs` | `932b347a9684006fd04dcf2579a64abda6fa736eca678f4234d52aacc133eaf9` |
| `src/HubRadio.Core/Analysis/ModelRun.cs` | `0d04d2fc2684d3ff2a1b50f24f926c699774457f995b923e0f4e1d4ef8024d84` |
| `src/HubRadio.Core/Analysis/Suggestion.cs` | `09cca14af4b2f7347a8272b7d86316fca8f7bf5c93e4a0751f33cd2f996323d6` |
| `src/HubRadio.Core/Projects/Asset.cs` | `7a709f95f85ed2da735d42454665c68d2b6fd647bd06a11475234490fe276bb0` |
| `src/HubRadio.Core/Projects/RightsRecord.cs` | `309eb86b25565e9aebae305d0b41af93d30b4c934574fc222cc0f2f97cf52f4a` |
| `src/HubRadio.Core/Provenance/OriginRef.cs` | `2d155ea8bc28de82202c00c2a89696f849a108cdb4c102275950af6cb900da6d` |
| `src/HubRadio.Core/Provenance/SourceSpan.cs` | `3fb50d1a204a66caf864a7a0cbb43baed7375e5664256df778de51c085f1f620` |
| `src/HubRadio.Core/Provenance/TimelineMap.cs` | `2620d03635aeda6299be7415659e770894ac7178b0141512e6cd937672a857ce` |
| `src/HubRadio.Core/Provenance/TimelineMapBuilder.cs` | `2983695d96ff870b294ee3e9c43e1c0d5e9a0cee5f3fac73eb17d5ba42390927` |
| `src/HubRadio.Core/Rendering/RenderGraphCanonicalEvidence.cs` | `af572a128d1924de9fc763a32eac8d64ad1308ecb55deabc8127eb76a65cee86` |
| `src/HubRadio.Core/Rendering/RenderGraphValidator.cs` | `db9c0928cbb6854bc1ba848b3c70be6b74c366e435b982c9526b4d2e2d478e4a` |

## 10. 再実行

PowerShell 7で`tests/Acceptance/Invoke-Q005TraceabilityAcceptance.ps1`を実行する。runnerは次をfail-closedで実行し、全gate成功時だけ単一のminimized JSONを標準出力へ返す。

1. locked Web dependency restore、TypeScript verification、production Web build
2. test project restore
3. Release x64 warnings-as-errors build
4. `AC005_Traceability`だけのfocused MTP test（minimum 1 test）
5. unresolved count、digest、decision、rights、negative control、scope claimの再検証
6. 実行前後のrepository HEADと選択17 source fingerprint一致検査
7. source fingerprint採取
8. 最終JSON privacy scan
9. 一時work directory cleanup

## 11. 残余範囲

- 5秒・2 sceneの決定論的synthetic fixtureであり、実動画、VFR、実字幕ファイルの視聴品質、実render出力は未測定である。
- machine-origin/human-modifiedの型付き識別を検証したが、実ASR/LLM推論、accuracy、groundedness、意味保全、モデル再現性は未測定である。
- synthetic RightsRecordの解決を検証したが、実素材の契約・権利証憑の真正性や組織上の公開承認は確認していない。
- human decision/edit recordはdeterministic fixtureであり、実在人物による受入または公開承認ではない。
- positive timeline originはSourceSpanだけである。direct Asset originと、aggregateが存在しないEditorialRecord originの正例は未実施である。
- UIからsceneへ移動する操作、WinUI/WebView2表示、利用者による視認確認は未実施である。
- Q-005自動試験の合格だけで`G1-MUST`、release readiness、実媒体pilotを通過扱いにしない。
