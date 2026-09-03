# V2-004 source-only Project契約 敵対的レビュー

- 対象task: `V2-004`
- 対象要求: `docs/requirement-definition.md` 版0.2 Draft §6、FR-001、FR-025、AC-001
- task base: `f5d276fab9538e7d5f4057128fe8e31f1d062683`
- レビュー日: 2026-09-03
- 状態: **REVIEW CLOSED — 個別task commit可能**
- 未解決Critical / Major / Minor: **0 / 0 / 0**

> [!IMPORTANT]
> 本レビューのPASS範囲は、元動画candidate 1件から安全なsource-only draftを作り、版付き設定snapshot、社内限定区分、opaque source handoff、legacy draft互換、bridge/schema/Web契約を維持する範囲である。管理workspaceへの実byte取込、Composition計画、render、知覚・human・法的・platform評価、package、release readinessはPASSにしない。

## 1. 対象変更

最終差分は追跡済み19 path、未追跡2 path、合計21 pathである。stagedはレビュー時点で0件だった。

- `schemas/project-creation-draft.schema.json`
- `schemas/web-bridge.schema.json`
- `src/HubRadio.App/Projects/ProjectApplicationRuntime.cs`
- `src/HubRadio.App/Projects/ProjectCreationService.cs`
- `src/HubRadio.App/Projects/SourceOnlyProjectCreationContract.cs`
- `src/HubRadio.App/Web/ProjectBridgeHandler.cs`
- `src/HubRadio.Infrastructure/Database/DefaultSettingsStore.cs`
- `src/HubRadio.Infrastructure/Database/ProjectCreationDraftStore.cs`
- `src/HubRadio.Infrastructure/Diagnostics/StableErrorCodes.cs`
- `src/HubRadio.Infrastructure/Media/AssetCandidateRegistry.cs`
- `src/HubRadio.Web/src/bridge/projectProtocol.test.ts`
- `src/HubRadio.Web/src/bridge/projectProtocol.ts`
- `src/HubRadio.Web/src/features/projects/ProjectWorkspace.test.tsx`
- `src/HubRadio.Web/src/features/projects/ProjectWorkspace.tsx`
- `src/HubRadio.Web/src/features/projects/projectState.test.ts`
- `tests/HubRadio.App.Tests/ProjectCreationServiceTests.cs`
- `tests/HubRadio.App.Tests/SourceOnlyProjectCreationContractTests.cs`
- `tests/HubRadio.App.Tests/WebBridgeSchemaTests.cs`
- `tests/HubRadio.Infrastructure.Tests/AssetCandidateRegistryTests.cs`
- `tests/HubRadio.Infrastructure.Tests/DiagnosticErrorCodeTests.cs`
- `tests/HubRadio.Infrastructure.Tests/ProjectCreationDraftStoreTests.cs`

レビュー文書追加前の内容集約SHA-256は`2c36d18c7bf745f068272798f8c48c40fcdeb77fbc1b625920a2b70496b74eb5`である。これは各relative path、byte size、file SHA-256をordinal順、実TAB区切り、LF結合したUTF-8 bytesから計算した。

## 2. 実装結果

- native pickerが保持するraw pathをWebへ返さず、opaque candidate IDだけをbridgeへ渡す。
- filename stemからcontent nameを導出し、空、前後空白、末尾dot、制御文字、不正UTF-16、Windows予約名、既存payload上限超過をtyped errorで拒否する。別名へ黙って置換しない。
- 要求で定義された安全なpurpose/audienceと、版付きsnapshotで確認したinternal-only、language fallback、匿名話者policy、期限なし、neutral/no-logo、成果物setをsource-only draftへ投影する。
- profile ID/version/digestと設定snapshot/digestをcanonical payloadへ固定し、global設定変更を既存draftへ遡及させない。
- source-only既定値をlegacy save/finalize入力で変更できず、旧10-field draftは従来codecで読書きできる。
- candidateはproject finalizeまで排他予約し、成功後は別projectから再予約できない一回限りのingest handoffへ遷移する。後続`MediaImporter`は同じtokenを一度だけclaimできる。
- source-only作成失敗または呼出元へ返らない取消tokenは破棄する。finalize失敗時は予約を解放し、再試行可能にする。
- Project、settings bind、draft closeはSQLiteの同一transactionで更新する。OCC、二重finalize、stale candidateを型付きで拒否する。
- schema、C# response、TypeScript protocol/state/UIをclosed unionとして同期し、errorへpath、SID、exception detailを含めない。

## 3. 採用したfindingと反映

| ID | 重要度 | 再現した問題 | 反映と確認 |
|---|---|---|---|
| `V2-004-R01` | Major | source-only作成でpicker登録後にfile消失、DB insert失敗、またはpost-selection cancellationが起きると、draftも利用者が知るtokenもないcandidateがregistryへ残り得た | `AssetCandidateReservation.Discard`を追加し、`CreateSourceOnlyDraftAsync`が失敗・取消時に破棄するよう修正。file消失、DB failure、取消の回帰testを追加した |
| `V2-004-R02` | Major | legacy draft 2件へ同じcandidateを入れると、最初のfinalize後もcandidateがavailableで2件目もfinalizeできた | legacy finalizeにもexclusive reserveを適用し、DB成功後にproject再予約不可のhandoffへcommitするよう修正。同じtokenで二つ目のprojectが作れない負例を追加した |
| `V2-004-R03` | Major | source-only finalize時にcandidate entryを削除すると、後続`MediaImporter.TryClaim`が必ず失敗し、元動画取込へ渡せなかった | commit済みcandidateを「project再予約不可・ingest claim可能」状態へ変更し、claim後に削除するone-time handoffへ修正した |
| `V2-004-R04` | Major | ingestがhandoffを正常claimした後、finalized draftを再読込すると`reselectionRequired`と誤表示し、利用者が実行不能な再選択を促した | finalized draftは成立済みsourceとして`available`を維持し、finalize→claim→load/listの回帰testを追加した |
| `V2-004-R05` | Major | production picker delegateがcandidate登録後、serviceへIDを返す前に再度cancellationを検査し、例外時にserviceがcandidateを破棄できなかった | 登録後の所有権をserviceへ返してから取消判定するよう統一し、単独picker APIでもreserve→discard後に取消を再送出する回帰testを追加した |
| `V2-004-T01` | Major | 並行reservation testが全taskを`Wait()`させたまま開始eventを`Set()`せず、focused runが永久待機した | task配列を作成後に`Set()`してから`WhenAll`するよう修正し、`AssetCandidateRegistryTests`全16件の完走を確認した |

別のclosure reviewerが最新の追跡済み・未追跡差分を読み直し、上記findingをclosed、新規qualifying finding 0、未解決Critical/Major/Minor 0と判定した。

## 4. 不採用・再分類した指摘

| 指摘 | 判定 | 根拠 |
|---|---|---|
| Project finalize自身が管理workspaceへsource bytesをcopy/hashすべき | **後続scope** | 要求の基本フローでは開始確認後の非破壊取込であり、V2-004はProject draft/finalizeとhandoffまでを所有する。実byte copy、decode、PTS、hash検査は既存ingest契約と後続統合で行う。V2-004は取込済みとは主張しない |
| `File.Exists`とfinalize間のfile変更をV2-004だけで完全排除すべき | **後続ingestでfail closed** | V2-004はpathを公開せずone-time handoffを維持する。immutable originalの確立、open handle、copy、前後hashはingest責務であり、本taskで架空の取込完了を作らない |
| purpose、audience、language、匿名話者等の全値が別hard-code正本である | **current blockerとして不採用** | purpose/audienceは要求§6.1と根拠台帳が直接定義する決定的導出値である。scope/language/anonymous/deadline/template/artifactはpinned 123-key snapshotのpolicyを検証し、snapshot全体を固定する。現profileにはpurpose、audience、匿名label、output profile IDのkeyがなく、同一`1.0.0`へ追加するとV2-003のidentity/inventory/digest契約を破る |
| V2-004で既存long render profileを1280×720から1920×1080へ置換すべき | **V2-008/V2-013へ継続** | V2-004は既存development catalog referenceを固定するだけでrender適合を主張しない。標準Compositionの1920×1080 profile選択・render検証はV2-008/V2-013で閉じる。要求との寸法差は未評価のまま残し、PASSへ丸めない |
| source stemへ255 UTF-8 byte上限を置く | **指摘採用後に除去** | 正本に255 byteの根拠がなかったため、独自閾値を削除し、既存のProject draft 64 KiB resource envelopeだけを適用した |
| source-only画面が旧wizard責務を残す | **V2-005へ継続** | V2-004はbridge/stateとsource-only要約を提供する。通常画面を元動画、要約、開始/取消だけに置換する責務はV2-005である |

## 5. 機械検証

実行環境はWindows、PowerShell Core 7.6.5、.NET SDK 10.0.400、Node.js 24.19.0、npm 11.17.0、CPython 3.12.10である。MTPは各classを`--filter FullyQualifiedName~... --minimum-expected-tests 1`付きで実行し、0件実行を成功にしていない。

### 5.1 focused verification

| 対象 | 成功 | 失敗 | environment-gated |
|---|---:|---:|---:|
| `SourceOnlyProjectCreationContractTests` | 3 | 0 | 0 |
| `ProjectCreationServiceTests` | 20 | 0 | 0 |
| `BridgeProtocolTests` | 34 | 0 | 0 |
| `WebBridgeSchemaTests` | 7 | 0 | 0 |
| `AssetCandidateRegistryTests` | 16 | 0 | 0 |
| `DiagnosticErrorCodeTests` | 15 | 0 | 1 |
| `ProjectCreationDraftStoreTests` | 11 | 0 | 0 |
| Web protocol/state/workspace 3 files | 39 | 0 | 0 |
| **合計** | **145** | **0** | **1** |

`DiagnosticErrorCodeTests`の1件は、この環境でdirectory symbolic linkを作成できないためinconclusiveであり、PASSへ数えていない。

### 5.2 canonical Release全回帰

| Runner | Success | Failure | environment-gated |
|---|---:|---:|---:|
| Architecture .NET | 46 | 0 | 0 |
| Core .NET | 654 | 0 | 1 |
| Infrastructure .NET | 705 | 0 | 28 |
| App .NET | 992 | 0 | 3 |
| Web Vitest | 444 | 0 | 0 |
| Accessibility Node | 4 | 0 | 0 |
| Web asset/dependency | 6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

全runnerはexit 0、failure 0で完走した。.NETは合計2,429件中2,397成功、32件environment-gatedである。Web/Nodeは454成功、Pythonは104成功である。restore、hash-locked dependency check、format/analyzer、warnings-as-errors build、TypeScript typecheck、production bundle検証、Python syntax verificationも成功した。Viteの500 kB超chunk warningは残り、失敗または性能PASSへ読み替えていない。

## 6. 静的・asset監査

- VS Code diagnostics: 0件。
- `git diff --check`: PASS。
- 未追跡fileの行末whitespace: 0件。
- 変更fileの0 byte: 0件。
- 変更media path: 0件。
- `project-creation-draft.schema.json`、`web-bridge.schema.json`: parse成功。
- source fixture: 6,494,572,403 bytes、SHA-256 `fe9c763fdf6fe7be4f368bfba926a1f3f2407ceaf044ec7d055e0869e65c1a9f`で前後一致。
- `Music/`: 現在36 MP3、149,081,539 bytes、reparse file 0、名称非開示の内容集約SHA-256 `7225a4c7d92d882f1abab87d2d55a14dee00a8f9b6ca6471e17741a6e09b7d3e`で前後一致。

`Music/`は計画書の2026-09-03T06:12観測「不在」と現在状態が異なる。この差を権利、完全性、外部利用許可の証拠にせず、V2-009開始時に現在receiptから再baselineする。

## 7. 残余scope

- V2-005: 旧wizardを元動画、設定要約、社内限定、開始/取消だけの通常画面へ置換し、double submit、keyboard/focus、restart/retryを閉じる。
- ingest統合: one-time handoffから管理workspaceへの非破壊copy、形式、stream、PTS、全編decode、容量、前後hashを検証する。
- V2-008/V2-013: 1920×1080標準Composition profile、最大3短尺、subtitle/thumbnail、実render/QC/deliveryを閉じる。
- human accessibility、意味完全性、知覚品質、platform UI mask、外部公開、権利・法的適合、clean-machine install、署名、SBOM/license/CVE完全性、release readiness: **NOT EVALUATED / PENDING**。
- V2-004 task commitは本review文書と同じlogical commitへ含める。commit SHAは自己参照を避け、本taskのGit履歴とWave A integration receiptへ記録する。

## 8. closure

- **Closure status: REVIEW CLOSED — V2-004個別task差分はcommit可能。**
- **未解決Critical: 0件。**
- **未解決Major: 0件。**
- **未解決Minor: 0件。**
- Wave A shared bridge union、統合focused test、統合全回帰、main到達はまだ完了しておらず、本個別reviewのPASSへ含めない。
