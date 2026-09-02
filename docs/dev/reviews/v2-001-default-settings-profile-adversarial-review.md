# V2-001 DefaultSettingsProfile 敵対的レビュー

- 対象task: `V2-001`
- 対象base commit: `c95df62f33a20fd78cfcfc3889625ab30b63ce0c`
- レビュー日: 2026-09-02
- 状態: **PASS**
- 未解決Critical/Major: **0件**

> [!IMPORTANT]
> このPASSは、既定設定のdomain、canonical JSON、schema、snapshot、digest境界だけを対象とする。具体的なproduction既定値、設定永続化、設定画面、案件作成、render、自動代理評価、実素材成果物、human evaluation、external publication、clean-machine package、release readinessをPASSにしない。

## 1. 対象

- `src/HubRadio.Core/Settings/SettingsProfileVersion.cs`
- `src/HubRadio.Core/Settings/DefaultSettingDefinition.cs`
- `src/HubRadio.Core/Settings/DefaultSettingsProfile.cs`
- `src/HubRadio.Core/Settings/DefaultSettingsJsonCodec.cs`
- `schemas/default-settings-profile.schema.json`
- `schemas/default-settings-snapshot.schema.json`
- `tests/HubRadio.Core.Tests/DefaultSettingsProfileTests.cs`
- `tests/HubRadio.Core.Tests/DefaultSettingsSchemaTests.cs`

既存依存の`ContentDigest`、`EntityId<TTag>`、`CanonicalJson`、`ContractText`、`RationalNumber`も実ファイルと利用箇所を照合した。独立レビューはread-onlyで行い、reviewer自身がtestを実行したとは記録しない。実行結果は§5の実装者検証として分離する。

## 2. 受入した指摘と反映

| ID | 重要度 | 実在した問題 | 反映と確認 |
|---|---|---|---|
| `V2-001-R01` | Major | `CanonicalJson`が整数`250`を`25e1`へ正規化し得る一方、parserが`JsonElement.TryGetInt64`だけに依存したため、serializer自身のcanonical出力を読み戻せなかった | canonicalな指数表記を`BigInteger`で厳密にInt64へ復元する`ReadCanonicalInt64`を追加した。非整数、指数過大、Int64範囲外は拒否する。修正前focused runは16件中14成功・2失敗、修正後は16/16だった |
| `V2-001-R02` | Major | JSON property順がcanonicalでも、`settings`、`allowedTextValues`、`evidence`、snapshot `values`の意味上の順序が逆の入力をconstructorが黙ってsortし、parserが受理した | parse後にdomain objectを再serializeし、入力との完全一致を要求した。各arrayを反転したcanonical JSONが`JsonException`になる負例を追加した |
| `V2-001-R03` | Minor | 非約分の`10/12`は同じ値の`5/6`へ黙って正規化され、意味上非canonicalな入力を受理し得た | 同じ再serialize境界で拒否し、`10/12`を負例へ追加した。最終focused runは17/17だった |

## 3. 不採用または後続責務とした指摘

| 指摘 | 判断 | 根拠 |
|---|---|---|
| `RationalNumber`が存在せずbuild不能 | **不採用** | `src/HubRadio.Core/Layout/RationalNumber.cs`が実在し、GCD約分を実装している。Release buildはwarning/error 0、rational testも成功した。実在fileと実行結果に反する指摘は採用しない |
| `R99`、`U99`、`C99`等、形式は正しいが現台帳にないIDをV2-001で拒否すべき | **V2-003へ限定して継続確認** | V2-001は根拠区分と`Udd`/`Cdd`/`Rdd`/`productDecision`形式をclosedにする責務であり、具体的なproduction profileと台帳値の一致はV2-003の完了条件である。V2-001へ将来の台帳registry/providerを先行実装しない。V2-003では使用する全IDが実在台帳項目へ解決することをtestする |
| JSON Schemaだけでsort、重複key、範囲とdigestを完全検証すべき | **不採用** | Draft 2020-12 schemaでprofile依存のkey集合、numeric kind間比較、SHA-256再計算は表現しない。schemaの`$comment`どおりruntime parserが重複、順序、range、allowlist、profile binding、digestをfail-closedで検証する |

## 4. 最終レビュー結果

独立reviewは次を確認した。

- canonical SemVerは非負3要素、先行zeroなし、Int32範囲内である。
- profile digestはschema version、name、version、sort済みdefinition全体を含み、profile IDとdigest自身を除外する境界が明示されている。
- snapshotはprofile ID、version、digestと全settingのexact value集合へ結合される。
- unknown enum、unknown property、missing property、invalid UUID/digest、wrong kind、range外、allowlist外、重複・欠落keyを拒否する。
- boolean/text/text-listはunit `none`、numeric boundaryは同kind、rational denominatorは正を要求する。
- list、definition、evidence、selectionはdefensive copyされ、runtime状態やpath、actor、結果をdomainへ混入しない。
- closed schemaとruntime parserの責務分離が明示されている。
- I/O、network、process、secret、PIIを追加していない。

Critical/Majorの未解決指摘は0件であり、判定は**PASS**である。

## 5. 機械検証

実行環境はWindows、PowerShell 7.6.5 Core、.NET SDK 10.0.400、Node.js 24.19.0、npm 11.17.0、CPython 3.12.10である。

### 5.1 Focused / component

| Runner | 成功 | failure | environment-gated inconclusive / skip |
|---|---:|---:|---:|
| `DefaultSettingsProfileTests` + `DefaultSettingsSchemaTests` | 17 | 0 | 0 |
| Core全体 | 654 | 0 | 1 |

Coreの1件はGit管理外のStage 0 observed-PTS work file不在による`Assert.Inconclusive`であり、PASSへ数えていない。

### 5.2 Canonical Release全回帰

`pwsh.exe -NoLogo -NoProfile -File .\test.ps1 -Configuration Release`を最終codeに対して実行し、全runnerが完了した。

| Runner | 成功 | failure | environment-gated inconclusive / skip |
|---|---:|---:|---:|
| Architecture .NET | 46 | 0 | 0 |
| Core .NET | 654 | 0 | 1 |
| Infrastructure .NET | 679 | 0 | 28 |
| App .NET | 959 | 0 | 3 |
| Web Vitest | 442 | 0 | 0 |
| Accessibility Node contract | 4 | 0 | 0 |
| Web asset negative/control | 6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

.NET restore/build/format/analyzer、Web dependency restore/typecheck/production build/dependency・asset検証、Python hash-locked dependency整合とsyntax検証も成功した。Viteは612.03 kBのchunk-size warningを出したがbuild/test failureではなく、V2-001はWeb bundleを変更していない。環境依存32件はsymbolic link、承認済みFFmpeg不在、非Windows経路、cross-account ACL等であり、PASSへ数えていない。

`git diff --check`も成功した。

## 6. 残余scope

- V2-002: profile registry/current pointer、利用者設定、案件snapshot、個別/全reset、migration、OCC。
- V2-003: 調査台帳どおりの具体的render/subtitle/audio/transition/neutral profileと、使用する根拠IDの台帳照合。
- V2-005/V2-006: source-only quick startと設定画面。
- V2-010: 自動代理評価。
- 実素材render、知覚品質、human evaluation、platform UI mask、clean Windows、signed package、完全SBOM/license/CVE、release readinessは未評価である。
