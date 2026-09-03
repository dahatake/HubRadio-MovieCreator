# V2-006 全体設定UI 敵対的レビュー

- 対象task: `V2-006`
- 対象要求: `docs/requirement-definition.md` 版0.2 Draft §6.4、§14.4、FR-033、AC-006
- task base: `f5d276fab9538e7d5f4057128fe8e31f1d062683`
- レビュー日: 2026-09-03
- 状態: **REVIEW CLOSED — 個別task commit可能**
- 未解決Critical / Major / Minor: **0 / 0 / 0**

> [!IMPORTANT]
> 本レビューのPASS範囲は、案件に固定された123設定を5群で表示・更新・案件開始時へresetするnative bridge、closed schema、TypeScript protocol/state/session、React UIと機械検証である。実WebView2上の人による操作性、screen reader、DPI、forced-colors、知覚品質、外部platform、release readinessはPASSにしない。

## 1. 対象変更

本laneは、過去の未コミットworktreeを復元できなかったため、cleanな`f5d276f`から再実装した。計画付録の履歴receiptは28 pathだったが、現在の実装は追跡済み12 path、未追跡12 path、合計24 pathである。履歴にあった未変更fileや実在しないfixture名を、現在の変更として捏造しない。

- `schemas/web-bridge.schema.json`
- `src/HubRadio.App/MainWindow.xaml.cs`
- `src/HubRadio.App/Projects/ProjectApplicationRuntime.cs`
- `src/HubRadio.App/Web/Bridge.cs`
- `src/HubRadio.App/Web/SettingsBridge.cs`
- `src/HubRadio.App/Web/WebViewHost.cs`
- `src/HubRadio.Web/src/App.test.tsx`
- `src/HubRadio.Web/src/App.tsx`
- `src/HubRadio.Web/src/bridge/index.ts`
- `src/HubRadio.Web/src/features/settings/SettingsPanel.test.tsx`
- `src/HubRadio.Web/src/features/settings/SettingsPanel.tsx`
- `src/HubRadio.Web/src/features/settings/SettingsStateProvider.tsx`
- `src/HubRadio.Web/src/features/settings/index.ts`
- `src/HubRadio.Web/src/features/settings/settingsBridgeSession.test.ts`
- `src/HubRadio.Web/src/features/settings/settingsBridgeSession.ts`
- `src/HubRadio.Web/src/features/settings/settingsProtocol.test.ts`
- `src/HubRadio.Web/src/features/settings/settingsProtocol.ts`
- `src/HubRadio.Web/src/features/settings/settingsState.test.ts`
- `src/HubRadio.Web/src/features/settings/settingsState.ts`
- `src/HubRadio.Web/src/main.tsx`
- `src/HubRadio.Web/src/styles.css`
- `tests/HubRadio.App.Tests/BridgeProtocolSecurityTests.cs`
- `tests/HubRadio.App.Tests/SettingsBridgeTests.cs`
- `tests/HubRadio.App.Tests/WebBridgeSchemaTests.cs`

レビュー文書追加前の内容集約SHA-256は`8e327e5bbc2a70f4c282c5f2c140d813d462178ec5acb341c135f202428dee2f`である。各relative path、byte size、file SHA-256をordinal順、実TAB区切り、LF結合したUTF-8 bytesから計算した。

## 2. 実装結果

- `settings.read`、`settings.update`、`settings.reset`、`settings.resetAll`をnative allowlist、routing、closed JSON schema、TypeScript unionへ追加した。
- 現在のWindows actorをnative側で再認可し、actor SIDをWeb入力として受け付けない。認可はproject存在確認より先に行う。
- 123件のproduction profile inventoryを、案件と出力、編集、字幕とグラフィック、音声と素材、プライバシーと保存の5群・固定順で表示する。
- current、案件開始時のinitial、profile defaultを分けて表示する。個別resetと全resetはimmutableな案件initial snapshotへ戻し、最新globalへ暗黙移行しない。
- update/resetはrevision、profile ID/version/digest、current snapshot digestを完全なOCC identityとして送る。conflict後は明示refreshまで変更を拒否する。
- boolean、integer、rational、text、textListの5 value kind、14 unit、7 evidence kindとsource ID組をclosedに検証する。unknown key/kind/unit/evidence、Int64範囲外、schema外responseをfail closedにする。
- requestはnativeと同じ64 KiB envelopeを送信前に検査する。WebView2の字句serializationがcompact JSONとbyte同一であるとは主張せず、全無応答原因に対して5秒の相関timeoutでpendingを破棄し再操作可能にする。
- 案件ID変更時はsessionとunderlying bridge clientのpending correlationを破棄し、旧案件のlate responseを採用しない。
- tabはArrow上下左右、Home、Endを扱い、Tab/Shift+Tab、buttonのEnter/Space、advanced toggle、根拠detailsのEscape閉鎖とsummaryへのfocus復帰をcomponent testで検証した。
- operation完了、conflict、refresh要求をstatus/alertとして表示し、evidenceをtextとして描画する。任意URL取得やHTML挿入を行わない。

## 3. 採用したfindingと反映

| ID | 重要度 | 再現した問題 | 反映と確認 |
|---|---|---|---|
| `V2-006-R01` | Major | 案件Aのread待機中に入力を案件Bへ変更すると、Aのlate responseがsnapshotを採用し入力をAへ巻き戻した | 案件変更時にsessionのpendingとbridge client correlationをabandonし、reducerもpending/snapshotをclearするよう修正。session/state/UIのlate response負例を追加した |
| `V2-006-R02` | Major | nativeが応答を返さない場合、session pendingが解除されず、同じ案件でUIが永久busyになった | 送信前64 KiB検査に加え、既存job patternと同じ5秒の相関timeoutを追加。timeout時にclient correlationとsession pendingを破棄し、late responseを無視して`unavailable`へ戻すtestを追加した |
| `V2-006-R03` | Minor | TypeScript parserがInt32上限超過revision/definitionCountと、暦として存在しない`updatedAt`を受理した | nonnegative Int32上限と、native round-trip形式のUTC timestampを`Date` round-tripで検証。calendar-invalid、形式違い、上限超過の負例を追加した |
| `V2-006-R04` | Minor | 展開した根拠detailsでEscapeを押しても閉じず、計画のkeyboard契約を満たさなかった | Escapeでdetailsを閉じsummaryへfocusを戻す実装とDOM testを追加した |
| `V2-006-R05` | Major | TypeScript parserが`definitionCount=0`かつdefinitions/initial/currentが空のschema違反snapshotをavailableとして受理した | 3配列のnonemptyを要求し、空snapshot負例を追加した |
| `V2-006-T01` | Test | settings追加によりlayout methodがglobal enum末尾ではなくなり、layout testの末尾2件assertionだけが失敗した | global exact method順testは維持し、layout固有testは`layout.read/apply`の包含を検証するよう修正した |
| `V2-006-T02` | Test gate | native resetAll/notFound/unbound/invalid、UI保存/reset/resetAll、全group key、Tab/Shift+Tab、Enter/Space、status/alertの明示証拠が不足していた | nativeとcomponent testを追加し、production profile 123件と全5 value kindも明示assertした |

独立したcorrectness、security/privacy/reliability/accessibility-contract、requirements/schema/YAGNIレビューを行い、各候補はexact file/symbol、違反契約、再現入力、expected/actualの4点が揃った場合だけ採用した。修正後の別closure reviewerは新規qualifying finding 0、未解決Critical/Major/Minor 0と判定した。

## 4. 不採用・再分類した懸念

| 指摘 | 判断 | 根拠 |
|---|---|---|
| compact `JSON.stringify` bytesとWebView2受信JSONが必ず異なり境界超過する | **findingとして不採用** | WebView2はJSON変換を規定するが、任意requestの字句byte差による上限超過は再現・保証されていない。byte同一性は主張せず、実在する全無応答caseをtimeoutで閉じた |
| 計画の履歴28 pathにないため現在24 pathは不完全 | **不採用** | 過去worktreeは復元不能であり、現在laneはclean baseから再実装した。現在のGit inventory、実ファイル、build/testを正本とし、存在しない`settingsTestFixture.ts`を成果物にしない |
| 人によるkeyboard/screen reader/contrast評価が未実施なのでcode defect | **NOT EVALUATEDへ分離** | machine-testable DOM/event/contrast token契約は実行したが、人の理解・操作性・支援技術実機評価は実施していない。未実施をPASSにもcode failureにも捏造しない |
| evidence URLを自動取得すべき | **不採用** | 要求はevidence ID/区分の表示であり、任意URL取得はprivacy/security境界を拡大する。React textだけを表示する |

## 5. 機械検証

実行環境はWindows、PowerShell Core 7.6.5、.NET SDK 10.0.400、Node.js 24.19.0、npm 11.17.0、CPython 3.12.10である。MTPは`--filter FullyQualifiedName~... --minimum-expected-tests 1`を付け、0件実行を成功にしていない。

### 5.1 focused verification

| 対象 | 成功 | 失敗 | environment-gated |
|---|---:|---:|---:|
| `SettingsBridgeTests` | 6 | 0 | 0 |
| `WebBridgeSchemaTests` | 8 | 0 | 0 |
| `BridgeProtocolSecurityTests` | 32 | 0 | 0 |
| `BridgeProtocolTests` | 34 | 0 | 0 |
| Web App/bridge/security/settings 7 files | 57 | 0 | 0 |
| **合計** | **137** | **0** | **0** |

TypeScript typecheckとwarnings-as-errors solution buildも成功した。

### 5.2 canonical Release全回帰

| Runner | Success | Failure | environment-gated |
|---|---:|---:|---:|
| Architecture .NET | 46 | 0 | 0 |
| Core .NET | 654 | 0 | 1 |
| Infrastructure .NET | 700 | 0 | 28 |
| App .NET | 992 | 0 | 3 |
| Web Vitest | 463 | 0 | 0 |
| Accessibility Node | 4 | 0 | 0 |
| Web asset/dependency | 6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

全runnerはexit 0、failure 0で完走した。.NETは合計2,424件中2,392成功、32件environment-gatedである。Web/Nodeは473成功、Pythonは104成功である。restore、hash-locked dependency check、format/analyzer、warnings-as-errors build、TypeScript typecheck、production bundle検証、Python syntax verificationも成功した。Viteの633.03 kB chunk warningは残り、性能PASSへ読み替えていない。

## 6. 静的・asset監査

- VS Code diagnostics: 0件。
- `git diff --check`: PASS。
- 未追跡fileの行末whitespace: 0件。
- 変更fileの0 byte: 0件。
- 変更media path: 0件。
- `web-bridge.schema.json`: parse成功。
- source fixture: 6,494,572,403 bytes、SHA-256 `fe9c763fdf6fe7be4f368bfba926a1f3f2407ceaf044ec7d055e0869e65c1a9f`で前後一致。
- `Music/`: 36 MP3、149,081,539 bytes、reparse file 0、名称非開示の内容集約SHA-256 `7225a4c7d92d882f1abab87d2d55a14dee00a8f9b6ca6471e17741a6e09b7d3e`で前後一致。

## 7. 残余scope

- V2-004とのshared `web-bridge.schema.json`、`ProjectApplicationRuntime.cs`、bridge routingはWave A integrationでclosed unionにする。blind `ours/theirs`解決は禁止する。
- V2-005/V2-012はquick startからsettingsへの遷移、進捗・成果物・評価UIとの統合を所有する。
- 実WebView2、screen reader、DPI、forced-colors/high-contrast、人によるkeyboard操作性・可読性: **NOT EVALUATED**。
- visual/music/brand/speech知覚品質、platform固有適合、外部公開、権利・法的適合、package、release readiness: **NOT EVALUATED / PENDING**。
- task commit SHAは自己参照を避け、Git履歴とWave A integration receiptへ記録する。

## 8. closure

- **Closure status: REVIEW CLOSED — V2-006個別task差分はcommit可能。**
- **未解決Critical: 0件。**
- **未解決Major: 0件。**
- **未解決Minor: 0件。**
- Wave A統合、main到達、human/release gateは本個別reviewのPASSへ含めない。
