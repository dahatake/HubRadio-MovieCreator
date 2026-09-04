# V2 Wave A shared integration 敵対的レビュー

- 対象: `V2-004`、`V2-006`、`V2-007`、`V2-009`のclosed union
- integration base: `f5d276fab9538e7d5f4057128fe8e31f1d062683`
- integrated HEAD: `21b30b8ed21e76ed3a3330bb4ab9d9f22006c70b`
- integrated tree: `60cfb00d244ab9f88520e1a92d2615b95a84f735`
- レビュー日: 2026-09-04
- 状態: **REVIEW CLOSED — Wave B開始可能**
- 未解決Critical / Major / Minor: **0 / 0 / 0**

> [!IMPORTANT]
> 本レビューのPASS範囲は、source-only Project、全体設定、3種類限定の可逆auto-decision、read-only Music catalog/policyが同一main treeで共存し、共有bridge/schema/persistence境界と各taskのnegative contractが回帰しないことである。V2-005以降のUI・composition・render wiring、人による評価、権利・法的適合、外部公開、signed package、clean-machine installはPASSにしない。

## 1. 統合receipt

`git log --reverse f5d276f..21b30b8`と各commitのancestor検査を実施し、次の直列履歴を確認した。全commitに対する`git merge-base --is-ancestor <commit> HEAD`はexit 0だった。

1. `dc2b287edc8cc9cc1ff12f10017a380306040736` — V2-004 source-only Project contract
2. `8d84333769426a3b26474c7f24b83f55f024342f` — V2-006 settings interface
3. `d9b96527995d4e858f1a864a81bc7b1cad4405a2` — integrated-tree code structure refactor
4. `c63917ab93e1453f4ee6fd3945d3a6413942dce9` — V2-009 Music implementation
5. `34e08aca946101b7bd2149eca45c0ada623f4b91` — V2-009 closure fixes
6. `21b30b8ed21e76ed3a3330bb4ab9d9f22006c70b` — V2-007 reversible auto-decisions

開始時と試験後のmain working treeはcleanで、`main...origin/main [ahead 2]`だった。4 task review文書はすべてHEADに存在し、各taskの未解決Critical/Major/Minorは0である。

## 2. shared boundary結果

- `schemas/web-bridge.schema.json`、`ProjectApplicationRuntime`、`ProjectBridgeHandler`、Web protocol/stateでは、source-only Projectとsettings methodをclosed unionとして保持した。unknown method、oversize、stale/late response、authorization-before-existence、opaque source candidate、one-time handoff、path/SID非開示の既存negative contractをfocused suiteで再実行した。
- source-only作成時のprofile/settings snapshotは案件へ固定され、current global settingsの後続変更を既存案件へ遡及させない。legacy draft/read compatibilityを維持する。
- `CompositionVersion`、`AutoDecisionEvidenceCodec`、`VersionService`、`ProjectStore`では、canonical evidence、semantic reload、4 MiB UTF-8 byte envelope、revision OCC、atomic checkpoint、strict ancestor rollback-as-childを維持する。
- `MusicCatalogScanner`、`MusicPolicy`、`MusicPlanningService`はProject/settings/decisionへ暗黙結合せず、host指定rootのread-only technical catalogとinternal-only policyだけを提供する。render wiringは後続taskへ残す。
- project/settings/decision/musicのbridge、schema、snapshot、evidenceにraw filesystem path、SID、Music file名、credentialを追加していない。

## 3. 独立integration review

独立reviewerがcurrent HEADとunion pathを対象に、(a) correctnessと共有境界、(b) security/privacy/reliability、(c) requirements/schema/traceability/YAGNIをread-onlyで確認した。確認対象には`ProjectCreationService`、`ProjectApplicationRuntime`、`ProjectBridgeHandler`、`web-bridge.schema.json`、`VersionService`、`AutoDecisionEvidenceCodec`、`composition-version.schema.json`、`MusicCatalogScanner`、`MusicPolicy`、`MusicPlanningService`と対応testを含む。

findingはexact file/symbol、違反contract、再現入力/手順、expected/actualの4点が揃うものだけを採用した。**qualifying findingは0件**だった。

最初のreview attemptはcurrent Git履歴を確認できず旧計画のpre-integration SHAを現在値と誤認したため、findingにもclosure根拠にも採用しなかった。current HEADと全task commitの祖先関係を一次取得した後、別のread-only reviewを実施して上記結果を得た。

## 4. 機械検証

### 4.1 integration focused verification

| Runner | Success | Failure | environment-gated |
|---|---:|---:|---:|
| .NET shared/focused | 111 | 0 | 2 |
| Web shared/focused | 63 | 0 | 0 |

0件discoveryを成功扱いせず、.NET focused executableはminimum expected testを指定した。

### 4.2 canonical Release全回帰

`test.ps1 -Configuration Release`をmain HEADで実行した。terminal終了通知の文言とは独立に、command自身が末尾へ`WAVE_A_FULL_TEST_EXIT=0`と`WAVE_A_DIFF_CHECK_EXIT=0`を出力しており、全runnerと後続`git diff --check`が完走した。

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

.NETは2,493件中2,459成功、34件environment-gated、failure 0。Web/Nodeは475成功、Pythonは104成功、failure 0だった。restore、dependency consistency、TypeScript typecheck、production build/assets、format/analyzer、warnings-as-errors .NET build、Python syntax、`git diff --check`も成功した。Viteの500 kB chunk warningは既知warningとして残し、性能PASSへ読み替えない。

## 5. asset receipt

試験後に匿名recipe（`Music/<leafName><TAB>byteSize<TAB>lowercaseSha256`をpath ordinal順、LF結合、UTF-8 BOMなし、末尾LFなし）で再取得した。

- top-level regular MP3: 36
- total bytes: 149,081,539
- reparse: 0
- regular non-MP3: 0
- aggregate SHA-256: `f0763e5f17b508b2b717a219b2728b6e342379c4f8cae136c5700b7e5e6fa814`

V2-009/V2-007の開始・終了receiptと一致し、Music bytesは変更されていない。これは権利、音楽的適合、ブランド適合、外部利用、再配布許可を証明しない。

## 6. 残余scope

- symlink/reparse、approved packaged FFmpeg/FFprobe、non-Windows、Stage 0 work files等の34 .NET testは環境依存のため**PENDING / environment-gated**であり、PASSへ数えない。
- 実WebView2、物理keyboard、screen reader、High Contrast、DPI、字幕・音声・映像・musicの人による知覚品質は**NOT EVALUATED / PENDING**。
- rights/legal、external publication、production package、signing、clean-machine install、upgrade/rollback/uninstall、release approvalは**NOT EVALUATED / PENDING**。
- quick-start UIはV2-005、standard Composition planningはV2-008、evaluation/state/results/render/delivery/packageはV2-010〜V2-014が所有する。

## 7. closure

- **Closure status: REVIEW CLOSED — Wave A closed unionはmainで回帰し、Wave Bを開始可能。**
- **未解決Critical: 0件。**
- **未解決Major: 0件。**
- **未解決Minor: 0件。**
- 外部・人手・法的・release gateはPENDING/NOT EVALUATEDのまま維持する。
