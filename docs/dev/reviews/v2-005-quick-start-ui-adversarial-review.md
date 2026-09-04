# V2-005 かんたん編集UI 敵対的レビュー

- 対象task: `V2-005`
- 対象要求: `docs/requirement-definition.md` 版0.2 Draft §6、§7、§14.1、NFR-001、NFR-004、NFR-005、NFR-008、AC-001
- task base: `af2383a767b83fafc0887f62e9d4055fc71d805a`
- task commit: `48eab52f5fb5f42545ec1a4b145c4f937e5fb32c`
- integrated main: `0f3460ae1e359e4ecc62320f6eaa5407c7ec1c7a`
- レビュー日: 2026-09-04
- 状態: **REVIEW CLOSED — V2-005 main統合済み**
- 未解決Critical / Major / Minor: **0 / 0 / 0**

> [!IMPORTANT]
> 本レビューのPASS範囲は、source-only draftを元動画、固定済み設定の要約、社内限定、標準成果物、開始前warning、`編集を開始`、`取消`だけで確認するWeb UIと、その既存Project bridge/state/OCC契約との接続である。実WebView2、物理keyboard、screen reader、DPI、High Contrast、人による理解・知覚品質、外部公開、権利・法的適合、signed package、clean-machine install、release readinessはPASSにしない。

## 1. 対象変更とreceipt

`af2383a…`からのtask差分は次の4 pathだけである。

- `src/HubRadio.Web/src/features/projects/ProjectWorkspace.test.tsx`
- `src/HubRadio.Web/src/features/projects/ProjectWorkspace.tsx`
- `src/HubRadio.Web/src/features/projects/QuickStartPanel.tsx`
- `src/HubRadio.Web/src/styles.css`

各relative path、byte size、file SHA-256をordinal順、実TAB区切り、LF結合したUTF-8 BOMなしbytesから計算した内容集約SHA-256は`16cb8a2fffde9a1b437a71242b8da283e2d2177c03405d550e510f599b6332e8`である。変更media pathは0件だった。

## 2. 実装結果

- `draft.sourceOnly !== null`のときだけ`QuickStartPanel`を表示し、legacy `sourceOnly === null` draftには既存5-step／10-field wizardを維持した。
- source-only画面は元動画の利用可否、固定済みprofile version、profile digest、settings snapshot digest、社内限定、標準成果物、開始前warning件数を通常DOMへ表示する。
- draft ID、source candidate ID、profile ID、filesystem path、Windows SIDを画面へ表示しない。artifact labelはReact text interpolationで描画し、HTMLとして挿入しない。
- 元動画が現在sessionで利用できない場合だけ既存pickerによる再選択を要求し、warning件数を現在stateから導出する。
- `編集を開始`は既存`finalizeDraft`を再利用する。pending中は開始・取消・再選択をdisabledにし、form submit側でもpendingとfinalizable stateを再確認する。
- Project reducerのrequest ID＋method相関、duplicate/late response無視、revision OCC、private-safe error、picker cancel時の旧token保持を変更せず再利用した。
- `取消`は`closeDraft`だけを呼び、finalizeまたはingestを開始しない。
- source-only headingへprogrammatic focusを移し、開始は標準form submitのEnter、取消は標準button操作を使う。状態は色だけでなくtext、border、`role=status`、`aria-live=polite`で表す。
- 新しいbridge method、backend service、provider、schema、thresholdを追加していない。

## 3. 独立3軸レビュー

current mainとtask差分を対象に、次の3軸を別々にread-only reviewした。

1. correctness: source-only分岐、start/cancel、source再選択、legacy互換、pending/finalized state。
2. security/privacy/reliability/accessibility proxy: XSS、path/SID/token非開示、duplicate submit、late response、OCC、focus、keyboard、非色依存status。
3. requirements/schema/traceability/YAGNI: §6/§7/§14.1、NFR-001、AC-001、ADR-0009、既存bridge/state再利用、不要schema/provider非追加。

findingはexact file/symbol、違反contract、再現入力または手順、expected/actualの4点が揃う場合だけ採用した。3軸のqualifying code findingは**0件**だった。

別のread-only closure reviewerがcurrent main、task commit、4変更path、試験件数、棄却理由、PENDING境界を再照合し、修正要求0件、未解決Critical／Major／Minor 0件と判定した。

## 4. 不採用・再分類した候補

| 候補 | 判断 | 根拠 |
|---|---|---|
| `dl`直下の`div`が各1組の`dt`/`dd`を含む構造はHTMLとして不正 | **不採用** | 現行HTMLの`dl` content modelは、1組以上の`dt`の後に1組以上の`dd`を持つ`div`によるgroupingを許可する。対象markupは各`div`が`dt`→`dd`の組を持ち、違反を再現できない |
| artifact setは常に固定6件でなければならない | **不採用** | UIはnative source-only summaryのclosed `artifactSet`を表示する責務であり、V2-008以降で成立候補数と不足理由を確定する。V2-005で空・重複成果物を生成または水増ししない |
| 実WebView2／screen reader評価がないためcode defect | **NOT EVALUATEDへ分離** | DOM event、focus、contrast token、text statusは機械試験したが、人の操作性・支援技術実機評価は実施していない。未実施をPASSにもcode failureにも読み替えない |
| digestへtooltipまたは追加ARIA labelが必須 | **不採用** | digestは通常textとして完全表示され、要求にtooltip契約はない。追加実装の必要性とactual failureを再現できない |

## 5. 機械検証

実行環境はWindows、PowerShell Core 7.6.5、.NET SDK 10.0.400、Node.js 24.19.0、npm 11.17.0、Vitest 4.1.11、CPython 3.12.10である。`npm ci`はpackage-lock固定の113 packageを復元し、npm auditは脆弱性0件だった。最初の`npm exec vitest`はlocal dependency未復元のため別版install promptを出したので拒否し、試験結果へ数えていない。

### 5.1 V2-005 focused／Web component verification

| Runner | Success | Failure | environment-gated |
|---|---:|---:|---:|
| Project workspace/state/session/protocol/App Vitest 5 files | 54 | 0 | 0 |
| Web full Vitest 60 files | 469 | 0 | 0 |
| Accessibility Node | 4 | 0 | 0 |
| Web asset/dependency | 6 | 0 | 0 |

TypeScript typecheck、production Vite build、runtime dependency closure、production asset検証も成功した。Viteの637.41 kB chunk warningは残り、性能PASSへ読み替えていない。

### 5.2 canonical Release全回帰

`test.ps1 -Configuration Release`をintegrated mainで実行した。

| Runner | Success | Failure | environment-gated |
|---|---:|---:|---:|
| Architecture .NET | 46 | 0 | 0 |
| Core .NET | 663 | 0 | 1 |
| Infrastructure .NET | 725 | 0 | 21 |
| App .NET | 1,034 | 0 | 3 |
| Web Vitest | 469 | 0 | 0 |
| Accessibility Node | 4 | 0 | 0 |
| Web asset/dependency | 6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

.NETは2,493件中2,468成功、25件environment-gated、failure 0。Web/Nodeは479成功、Pythonは104成功、failure 0だった。restore、hash-locked Python dependency check、`pip check`、format/analyzer、warnings-as-errors solution build、TypeScript typecheck、production bundle検証、Python syntax verificationも成功した。

## 6. 静的監査

- VS Code diagnostics: 4変更pathで0件。
- `git diff --check`: PASS。
- tracked／untracked source inventoryの0 byte: 0件。
- task変更path: 4件、変更media path: 0件。
- Git working tree: canonical実行後clean。
- main、origin/main: ともに`0f3460ae1e359e4ecc62320f6eaa5407c7ec1c7a`。

## 7. 残余scope

- standard Composition plan、short／thumbnail候補、不成立reasonは`V2-008`が所有する。
- quick startからprogress／artifact／evaluation／optional self-reviewへの単一selection接続は`V2-012`が所有する。
- symlink/reparse、non-Windows、Stage 0 work files等の25 .NET testは**PENDING / environment-gated**であり、成功数へ含めない。
- 実WebView2、物理keyboard、screen reader、DPI、High Contrast、人による可読性・理解は**NOT EVALUATED / PENDING**。
- visual／audio／subtitle／music／brandの知覚品質、platform固有適合、rights/legal、external publication、production package、signing、clean-machine install、release approvalは**NOT EVALUATED / PENDING**。
- source MOVと`Music/`のbytesは本taskで変更していない。今回のV2-005 closureでは大容量Git外assetを再hashしておらず、計画書の2026-09-04 receiptを現在再検証済みとは主張しない。V2-014でbefore/after receiptを再取得する。

## 8. closure

- **Closure status: REVIEW CLOSED — V2-005実装はmainへ統合済み。**
- **未解決Critical: 0件。**
- **未解決Major: 0件。**
- **未解決Minor: 0件。**
- Wave BはV2-008の個別closure後に実施する。
- 外部・人手・法的・release gateはPENDING / NOT EVALUATEDのまま維持する。
