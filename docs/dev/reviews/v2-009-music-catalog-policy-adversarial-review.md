# V2-009 Music catalog / policy 敵対的レビュー

- 対象task: `V2-009`
- 対象要求: `docs/requirement-definition.md` 版0.2 Draft FR-019、FR-020、BR-004
- 実装commit: `c63917ab93e1453f4ee6fd3945d3a6413942dce9`
- closure差分base: `c63917ab93e1453f4ee6fd3945d3a6413942dce9`
- レビュー日: 2026-09-04
- 状態: **REVIEW CLOSED — closure修正commit可能**
- 未解決Critical / Major / Minor: **0 / 0 / 0**

> [!IMPORTANT]
> 本レビューのPASS範囲は、明示されたlocal rootのtop-level regular MP3を読み取り専用で列挙し、technical metadataとcontent digestだけを用いて社内向けcue assignmentを決定するcontractである。音楽的適合、ブランド適合、人による聴取、speech理解、mix品質、権利・法的適合、外部利用、再配布、production FFprobeによる実36曲の適格性はPASSにしない。

## 1. 対象変更

実装本体4ファイルとfocused test 3ファイルを対象にした。closureでは同じ7ファイルだけを変更した。

- `src/HubRadio.App/Media/MusicPlanningService.cs`
- `src/HubRadio.Core/Media/MusicCatalog.cs`
- `src/HubRadio.Core/Media/MusicPolicy.cs`
- `src/HubRadio.Infrastructure/Media/MusicCatalogScanner.cs`
- `tests/HubRadio.App.Tests/MusicPlanningServiceTests.cs`
- `tests/HubRadio.Core.Tests/MusicPolicyTests.cs`
- `tests/HubRadio.Infrastructure.Tests/MusicCatalogScannerTests.cs`

変更media path、credential、token、秘密鍵、追加network/download処理は0件である。

## 2. 実装結果

- scannerはhostが明示したlocal absolute rootのtop-level regular `.mp3` だけを対象にする。UNC、device、ADS、reparse、非regular fileを拒否し、sourceをrename/delete/writeしない。
- scan前後にcontent digestとbyte sizeを再取得し、途中変更をcatalogへ公開しない。同一contentは1件へ畳みtyped warningを残す。
- ffprobe requestはshellを使わず、固定timeoutと256 KiB capture上限を持つ。closed JSON shape、audio stream 1本、canonical `mp3` codec、positive duration/sample rate/channelsだけを受理する。
- absent、empty、inaccessible、unsafe、no eligible tracksをtyped state/warningとして扱い、BGMなしで正常完了可能にする。
- trackとcueはcontent digestのordinal順で決定的に処理する。trackがcue以上の長さの場合だけ割り当て、loop/stretchを禁止する。
- speech-free title/topic/end cardだけを既定対象とし、dialogue BGMは既定OFF、明示有効時だけforeground比`-20 dB`を記録する。
- musical/brand/human listening/legal/copyright/speech understanding/mix qualityは常に`NotEvaluated`であり、自動PASSへ昇格しない。

## 3. 採用したfindingと反映

| ID | 重要度 | exact symbol・再現・expected/actual | 反映と確認 |
|---|---|---|---|
| `V2-009-R01` | Major | `MusicCatalogWarning.UnsafeLibraryPath`。Core.Media公開面を列挙する`IngestValidatorTests.PublicSurfaceIsImmutablePrivateDataFreeAndPersistenceFree`を実行すると、private native locationを示す禁止語`Path`を含みexpected=false / actual=trueで失敗した | 値を`UnsafeLibraryLocation`へ変更し、enum名にもPath/FileNameがないことをscanner testへ追加。該当App test 1/1成功 |
| `V2-009-R02` | Major | `MusicCueEligibility`と`MusicPolicyEvaluation`がpositional recordでpublic init setterを持ち、`with`式でeligible＋block reason等の矛盾状態を生成できた。public Core.Mediaはget-onlyであるべきところactual setterあり | validating constructorを持つsealed get-only classへ変更。undefined enumとeligibility/reason矛盾を拒否し、public property setter 0をtest |
| `V2-009-R03` | Major | `MusicCatalog.CreateAvailable/CreateUnavailable`がstateとwarningの対応を検証せず、Available＋LibraryAbsent、Absent＋LibraryEmpty、Empty＋warningなしを生成できた。expectedはtyped stateとwarning一致、actualは矛盾catalog受理 | factoryでclosed state-warning invariantを検証。4種類の矛盾入力をnegative test化 |
| `V2-009-R04` | Major | `MusicCatalog.ComputeDigest`がdecimalのscaleを含む既定文字列表現を使用し、数値的に等しい`1m`と`1.0m`で異なるcatalog digestになり得た。expectedは同一technical durationなら同一identity、actualは表記scale依存 | durationをinvariant `G29`で正規化し、`1m`/`1.0m`のdigest同一testを追加 |
| `V2-009-R05` | Major | positional `MusicCueAssignment`をpublicに直接生成でき、Assigned＋track null、loop=true、未割当＋gain等を作れた。expectedはno loop/stretchかつreason/track/gain整合、actualは不可能状態受理 | constructorをinternal化し、reason/track/gain/loop/stretch invariantを検証。public constructor 0と不正内部入力拒否をtest |
| `V2-009-T01` | Test gate | root/track reparseとinaccessible rootの明示検証がなく、environment可否とcontract合否を分離できなかった | directory/file symlink testを追加し、作成不可環境は`Assert.Inconclusive`。Windows protected rootは実際に読取拒否できる場合のみtyped warningを確認 |

## 4. 不採用・再分類した懸念

| 指摘 | 判断 | 根拠 |
|---|---|---|
| dedicated `MusicCatalogTests`が必要 | **不採用** | class名の契約はない。catalog factory/invariantは`MusicPolicyTests`、scanner生成は`MusicCatalogScannerTests`、consumerは`MusicPlanningServiceTests`で実行する |
| typeがproductionで未使用 | **不採用** | `MusicPlanningService`が`MusicCatalog`と`MusicPolicy`を直接利用する。render wiringはV2-013 scope |
| 任意のtrack byte上限を追加すべき | **不採用** | 要求/ADRにclosed値がなく、`SHA256.HashDataAsync(Stream)`はstreamingである。任意threshold追加は禁止され、cancel、probe timeout、bounded outputは既にある |
| scanner終了後からrender直前の変更もscannerが保持検知すべき | **後続scope** | scannerはscan中変更を拒否する。render直前のdigest再検証とTOCTOU対策はV2-013が所有する |
| `Music/`が存在するので権利・fitもPASS | **NOT EVALUATED** | 存在、byte/hash不変、technical scan contractは権利・音楽的適合・外部利用を証明しない |

## 5. 機械検証

実行環境はWindows、PowerShell Core 7.6.5、.NET SDK 10.0.400、Node.js/Vite、CPython 3.12.10である。MTP focused testは`--minimum-expected-tests 1`付きで0件実行を成功にしていない。

### 5.1 focused verification

| 対象 | 成功 | 失敗 | environment-gated |
|---|---:|---:|---:|
| `MusicPolicyTests` | 9 | 0 | 0 |
| `MusicCatalogScannerTests` | 11 | 0 | 2 |
| `MusicPlanningServiceTests` | 7 | 0 | 0 |
| Core.Media public-surface regression | 1 | 0 | 0 |
| **合計** | **28** | **0** | **2** |

### 5.2 canonical Release全回帰

| Runner | Success | Failure | environment-gated |
|---|---:|---:|---:|
| Architecture .NET | 46 | 0 | 0 |
| Core .NET | 663 | 0 | 1 |
| Infrastructure .NET | 716 | 0 | 30 |
| App .NET | 1,012 | 0 | 3 |
| Web Vitest | 465 | 0 | 0 |
| Accessibility Node | 4 | 0 | 0 |
| Web asset/dependency | 6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

全runnerはexit 0、failure 0で完走した。.NETは2,471件中2,437成功、34件environment-gated。Web/Nodeは475成功、Pythonは104成功である。restore、dependency consistency、format/analyzer、warnings-as-errors build、TypeScript typecheck、production bundle、Python syntax、`git diff --check`も成功した。Vite chunk size warningは残り、V2-009 defectまたは性能PASSへ読み替えない。

## 6. Music read-only receipt

正本`Music/`について、top-level regular MP3ごとのrelative leaf identity、byte size、SHA-256をordinal順・実TAB区切り・LF結合・UTF-8 BOMなし・末尾LFなしで匿名集約した。ファイル名は本証跡へ記録しない。

- before/after MP3 count: 36 / 36
- before/after total bytes: 149,081,539 / 149,081,539
- before/after top-level reparse count: 0 / 0
- before/after regular non-MP3 count: 0 / 0
- before/after aggregate SHA-256: `f0763e5f17b508b2b717a219b2728b6e342379c4f8cae136c5700b7e5e6fa814`

**Music bytesは完全一致し、変更されていない。**

## 7. 残余scope

- approved packaged FFprobeが利用できる環境での実36曲production probe: **NOT EVALUATED**。synthetic closed contractはPASS。
- symlink/reparse test 2件: 現環境で作成不可のため**environment-gated**。
- music/brand/speech/mixの人による知覚評価、rights/legal、外部利用・再配布: **NOT EVALUATED / PENDING**。
- compositionへのmusic assignment固定、render直前digest再検証、実mix/QC、delivery: V2-008/V2-010/V2-013 scope。

## 8. closure

- **Closure status: REVIEW CLOSED — V2-009 closure差分はcommit可能。**
- **未解決Critical: 0件。**
- **未解決Major: 0件。**
- **未解決Minor: 0件。**
- main統合、後続render、human/legal/release gateは本個別reviewのPASSへ含めない。
