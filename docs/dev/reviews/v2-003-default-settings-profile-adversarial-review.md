# V2-003 具体的な版付き既定profile 敵対的レビュー

- 対象task: `V2-003`
- 対象base HEAD: `e8221367ddd631aea585d91c7dd216ab5d1f851b`
- task commit: **なし**（利用者指示 `Do NOT commit` に従った未コミット差分）
- レビュー日時: 2026-09-02T22:55:13+09:00
- 状態: **PASS（V2-003差分と限定回帰）**
- 未解決Critical/Major: **0件**

> [!IMPORTANT]
> このPASSは、調査済み既定値の版付きbundle、closed catalog、strict loader、既存development render profile非破壊、App component回帰とcanonical全回帰までを対象とする。startup store bootstrap、Composition/runtime適用、human evaluation、外部platform適合・公開、clean-machine/package、署名、release readinessはPASSにしない。

## 1. 対象

- `src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json`
- `src/HubRadio.App/DefaultSettings/BundledDefaultSettingsProfileLoader.cs`
- `src/HubRadio.App/DefaultSettings/BundledDefaultSettingsProfileCatalog.cs`
- `src/HubRadio.App/HubRadio.App.csproj`
- `tests/HubRadio.App.Tests/BundledDefaultSettingsProfileTests.cs`

根拠として、`docs/dev/research/default-settings-research.md`、`docs/requirement-definition.md`、V2-001のdomain/schema、既存`src/HubRadio.App/Profiles/**`も照合した。

## 2. 受入した指摘と反映

| ID | 重要度 | 実在した問題 | 反映と確認 |
|---|---|---|---|
| `V2-003-R01` | Major | 初期122-setting案にFR-024のCTAがなく、対応長尺があるshortだけへ`本編を見る`を設定する製品判断を表現できなかった | `editing.callToActionTexts`を追加し、条件・CTAなしfallback・URL等を推測しない説明を固定した。最終inventoryは123件、domain digestは`5ffedbd56f43f9142b92c16e36abfd289da45cd34feea897da82101164a7414c`である |
| `V2-003-R02` | Minor | mutation testが単に任意の`JsonException`を期待すると、evidence registryまたはcritical semantic validatorではなく最終pinned digestだけでgreenになり得た | mutation helperから例外を返し、`R99`は`outside the approved registry`、inventory driftは`key inventory is not approved`、same-version driftはapproved content、identity substitutionはidentity、critical driftはsemantic rejection messageまで確認した |
| `V2-003-R03` | Minor | 旧development render profileの非変更は一時的な`git diff`だけで、将来回帰を検出する恒久testがなかった | 4 profileのcanonical whole-document SHA-256をpinし、`development-neutral-placeholder`かつ外部brand asset不要のままであることをtestした。改行差だけでは偽陽性にしない |
| `V2-003-R04` | Minor | evidenceを空配列にしたcanonical inputはfail-closedではあったが、domain constructorの`ArgumentException`がstrict loader境界から漏れ、他の不正JSONと異なる例外契約になった | loaderがparse中のdomain `ArgumentException`をinner exceptionとして保持した`JsonException`へ正規化した。欠落evidence負例を追加し、修正前12件中11成功・1失敗、修正後12/12を確認した |

## 3. 不採用または後続責務とした指摘

| 指摘 | 判断 | 根拠 |
|---|---|---|
| bundled catalogを今taskでdatabase current pointerへ接続すべき | **shared integrationへ継続** | task planの`V2-002 / V2-003 shared integration`が、bundle初期登録、current pointer、再起動no-op、store round-tripを明示的に所有する。V2-003単独へmigration/store責務を重複実装しない |
| 123設定を今taskで既存render/composition処理へ直接投影すべき | **V2-008/V2-009へ継続** | V2-008がComposition/profile/decision contract、V2-009がaudio/asset contractを所有する。V2-003は版付き定義と解決可能なcatalogを提供し、まだconsumerがない値を既存runtimeへ推測接続しない |
| 既存720p/fixed-30000-over-1001 profileをproduction値で上書きすべき | **不採用** | task planは既存ID/contentとComposition参照を壊さず、placeholderをproduction承認済みと誤表示しないことを要求する。production defaultsは別bundleとして追加した |
| TikTok、LinkedIn、Netflix、BBC、Apple等の値を外部適合保証として表示できる | **不採用** | profileは広告・partner・字幕制作・podcast等の資料scopeを説明に残し、`notClaimed`、`unmeasured`、warning、`NOT EVALUATED`を明示する。claim語彙15件を個別確認した |

## 4. 最終レビュー結果

- profileはstrict UTF-8、BOMなし、canonical JSON、54,001 bytesであり、public Core parserとのround-tripが完全一致する。
- identityは`8d75a1fe-d10e-4aa7-87c5-3ac2d06d1f9b`、versionは`1.0.0`、domain content digestは`5ffedbd56f43f9142b92c16e36abfd289da45cd34feea897da82101164a7414c`で固定した。
- 123 keyはordinal順で一意であり、群別件数は音声と素材20、編集22、privacyと保存13、案件と出力30、字幕とグラフィック38である。
- 全settingは1件以上のevidenceを持つ。`U01`、`C02〜C05`、`R01〜R26`のうち使用対象だけをkindとの組でclosed registryへ固定し、`R99`等を再digest後も拒否する。
- 16:9 1920×1080、9:16 1080×1920、source frame-rate維持、明示HDR tone map+warning、MP4/H.264/AAC-LC/48 kHz/BT.709を固定し、外部platform適合はclaimしない。
- transitionはhard cut 0 ms、crossfade既定OFF・有効時250 msであり、0 msを外部研究の推奨値とは表記しない。
- 日本語SDH指向候補は7 cps、横16全角、2行、最短5/6秒、最長7秒、gap最小2 frame、白/不透明黒、BBC汎用領域を採用し、platform mask未測定warningと意味的完全性`NOT EVALUATED`を保持する。
- 音声はinternal/online -16 LKFS ±1、True Peak -1 dBFS、別broadcast -23 LUFSを分離し、noise/hum既定適用OFF、音楽的適合と知覚的理解は`NOT EVALUATED`である。
- neutral/no-logo、source frame only thumbnail、生成顔・生成場面OFF、社内限定、external publication OFF、cloud text suggestion OFF、自動削除OFFを確認した。
- loaderはempty、64 KiB超過、invalid UTF-8、BOM/noncanonical bytes、unknown/missing property、重複・欠落・順序違反、identity/digest drift、unregistered evidence、critical semantic driftをfail-closedで拒否する。
- catalogはexact `1.0.0.json` 1件だけを許可し、ID/version/digestの完全一致で解決する。返却collectionはread-onlyである。
- 既存4 render profileはcanonical content、development placeholder表示、外部brand asset不要の契約を維持し、`Music/`に変更を加えていない。
- 新規network/process、secret、credential、PII、外部asset取得、Music読書きは追加していない。

Critical/Majorの未解決指摘は0件であり、V2-003の単独差分は**PASS**である。

## 5. 機械検証

実行環境はWindows、PowerShell 7.6.5 Core、.NET SDK 10.0.400である。

| 検証 | 結果 | 備考 |
|---|---:|---|
| Release / warnings-as-errors App test build | PASS | warning 0、error 0、zero-byte source 0 |
| 新規旧profile不変test単独 | 1/1 | discoveryを名前指定で確認 |
| `BundledDefaultSettingsProfileTests` | 12/12 | negative、catalog、packagingを含む |
| HubRadio.App.Tests component | 971成功 / 0失敗 / 3 inconclusive | 合計974。3件はこの環境でsymbolic linkを作れないためで、PASSへ数えていない |
| Core settings/schema/canonical focused | 30/30 | `DefaultSettingsProfileTests`、`DefaultSettingsSchemaTests`、`CanonicalJsonTests` |
| Core semantic profile probe | PASS | 123 definitions、digest一致、public parse/canonical再生成完全一致 |
| isolated App publish | PASS | publish先profile 54,001 bytes、sourceとbyte-for-byte一致、raw file SHA-256 `3f5fd0e63c575d954178e91ba32647e579f3c940ddb9b16cc8a6e2e17d968953` |
| Web production prerequisite build | PASS | typecheck、3 runtime dependencies、3 dist assets、npm audit 0 vulnerabilities。612.03 kB chunk warningは記録し、成功へ隠していない |
| canonical Release全回帰 | PASS | Architecture 46/46、Core 654成功・1環境依存、Infrastructure 688成功・19環境依存、App 971成功・3環境依存、Web 442、Accessibility 4、Web asset 6、Python 104、failure 0 |
| VS Code diagnostics | 0 | 変更6成果物すべて |
| `git diff --check` | PASS | whitespace errorなし |
| 旧render profiles / `Music/` diff | 変更なし | canonical hash testも追加 |

旧render profileのcanonical whole-document SHA-256は次のとおり。

- `16x9/long-form.json`: `a5ffe4cb92cd39e0ecf9f2dacf4dba18b5fc9b27a9808e3b42131b0a15c478e4`
- `16x9/youtube-landscape.json`: `7c3b50a4f1223610180d2777c5755bbe61e7a36c87f41eeee0db17ace26415cb`
- `16x9/landscape-short.json`: `bb1940c4e531f1cd1a7886f561c032f192210cedcd189a982b06bb12b913d536`
- `9x16/vertical-short.json`: `998eb247113a144a486362f7ada653cb07e6e3ab69a86a1fd19addec04460f58`

## 6. 未実施・残余scope

- full `test.ps1`: closure後に**PASS**。environment-gated 23件はPASSへ数えず、上表へ別記した。
- task commit SHA: **なし**。利用者の`Do NOT commit`指示によりbase HEADだけを記録した。
- V2-002/V2-003 shared integration: bundleのstore初期登録、current pointer、再起動no-op、store canonical byte round-trip、既存project非遡及。
- V2-008/V2-009: Compositionとrender/subtitle/layout/transition/audio/asset consumerへの版付き設定適用。
- 実素材render、字幕意味完全性、speech/BGM知覚品質、human evaluation、platform UI mask実測、外部投稿・適合、clean-machine install、MSIX署名・upgrade、SBOM/license/CVE完全性、release readiness: **NOT EVALUATED**。

## 7. 独立closure（2026-09-02）

> [!IMPORTANT]
> 本節は、closure開始時のworking treeを改めて読み、追跡済み差分だけでなく未追跡成果物も含めて判定した独立結論である。現在のファイルと件数が異なる場合、本節の機械検証結果を§5の履歴値より優先する。closure後のcanonical全回帰結果は§5へ追記した。

### 7.1 closure対象と確定事実

次の全review報告を対象にした。

1. 本文§2〜§4の実装・敵対的review。
2. 「具体profileが存在しない」としたreview報告。
3. 問題なしとした独立correctness review。
4. TOCTOU、symlink/junction、JSON深度、runtime callsite、`PreserveNewest`、caller path loggingを挙げたsecurity/privacy/reliability review。

closure開始時のHEADは`e8221367ddd631aea585d91c7dd216ab5d1f851b`であり、`git status --short --untracked-files=all`は次を示した。

- 追跡済み変更: `src/HubRadio.App/HubRadio.App.csproj`。
- 未追跡: 本review文書、loader、catalog、profile JSON、対象test。
- `src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json`は実在し、54,001 bytes（上限65,536 bytes以内）、123 settings、ID `8d75a1fe-d10e-4aa7-87c5-3ac2d06d1f9b`、version `1.0.0`、domain content digest `5ffedbd56f43f9142b92c16e36abfd289da45cd34feea897da82101164a7414c`、raw SHA-256 `3f5fd0e63c575d954178e91ba32647e579f3c940ddb9b16cc8a6e2e17d968953`である。

したがって「profileなし」は、未追跡ファイルをinventoryへ含めなかったことによる偽陰性であり、review findingとして成立しない。

### 7.2 finding判定表

| 報告 / finding | closure判定 | Critical/Major残件 | 独立根拠 |
|---|---|---:|---|
| 本文`V2-003-R01`〜`R04` | **Accepted → 修正済み・closed** | 0 | CTA、mutation testの識別力、旧development profile不変、domain例外の`JsonException`正規化は現在の実装と対象testで維持されている |
| 具体profileが存在しない | **Rejected** | 0 | 未追跡`1.0.0.json`を見落とした報告である。54,001 bytes、123 settingsを実測し、loader testが同じsource bytesを読み込んだ |
| 独立correctness review: 問題なし | **Accepted** | 0 | pinned identity/inventory/digest、strict canonical round-trip、build output byte identityの各testを個別に1/1成功で再確認した |
| Security #1: TOCTOUで任意または機微fileを受理する | **Rejected** | 0 | file置換の競合窓自体は存在するが、受理には64 KiB以内、strict UTF-8/canonical shape、exact ID/name/version、123-key inventory、evidence、critical semantics、approved content digestの全一致が必要である。任意・破損・機微bytesはfail-closedであり、報告はproduction acceptance bypassまたは内容漏えいを実証していない |
| Security #2: symlink/junctionを辿るためV2-003違反 | **Rejected（観測事実は記録）** | 0 | 現在の追加testではfile symlink caseは環境制約でinconclusive、directory junction caseはexact approved 54,001-byte profileを読み込めたためtest assertionが失敗した。しかしV2-003契約には「bundled regular file」またはphysical locality/reparse拒否のinvariantがない。task planがregular file/reparse拒否を明記するのは後続V2-009の`Music/` catalogである。承認済みbytesの別location受理はidentity/content acceptanceの迂回ではないため、後付けinvariantを実装欠陥にしない |
| Security #3: JSON深度がunbounded | **Rejected** | 0 | `JsonDocument.Parse`の既定`JsonDocumentOptions.MaxDepth=0`は最大深度64を意味し、超過時は`JsonException`となる。入力もloaderで64 KiBに制限されるためunboundedではない。公式仕様: [JsonDocumentOptions.MaxDepth](https://learn.microsoft.com/dotnet/api/system.text.json.jsondocumentoptions.maxdepth) |
| Security #4: production runtime callsiteがない | **Rejected as current-task defect / Later scope** | 0 | `src/**/*.cs`の`BundledDefaultSettingsProfileCatalog.Load(` callsiteは0件だが、V2-002/V2-003 shared integrationがbundle初期登録、current pointer、再起動no-op、store round-tripを明示的に所有する。V2-003単独へbootstrapを先取りしない |
| Security #5: `PreserveNewest`でstale/corrupt outputがproduction acceptanceを通る | **Rejected** | 0 | target-only変更を`PreserveNewest`が修復しない可能性は一般論としてあるが、必須test `ProfileJsonIsCopiedBesideTheBuiltAppAssemblyWithIdenticalBytes`はsource/outputをbyte-for-byte比較し、今回1/1成功した。新しい破損destinationでbuildだけが成功してもこのtestは失敗するため、「build成功かつ既存identity testも見逃す」経路は実証されていない。公式仕様も`PreserveNewest`をsource変更時のcopy、`IfDifferent`をtarget側変更のreset用途として区別する: [.NET SDK MSBuild item metadata](https://learn.microsoft.com/dotnet/core/project-sdk/msbuild-props#copytopublishdirectory) |
| Security #6: 将来callerがpathをlogする | **Rejected** | 0 | 現在production caller/loggerは存在せず、報告の漏えいにはrepositoryにない仮想callerが必要である。loader/catalogが生成するvalidation messageは入力bytesまたは`profilesDirectory`を補間しない。将来bootstrapのlog redactionはshared integrationのreview対象とする |

### 7.3 現在の機械検証

PowerShell 7.6.5 Core、.NET SDK 10.0.400、MTP `--minimum-expected-tests 1`で実行した。

| 検証 | 現在結果 | 判定 |
|---|---:|---|
| `BundledDefaultSettingsProfileTests` class filter | 12/12成功 | 契約外のreparse拒否DataRowを除外後、全contract testが成功した |
| `ProfileLoadsWithPinnedIdentityInventoryEvidenceAndCriticalDefaults` | 1/1成功 | 123 settings、identity、domain digest、critical defaultsを確認 |
| `ProfileBytesAreCanonicalStrictUtf8WithoutBomAndRoundTripExactly` | 1/1成功 | 64 KiB未満、BOMなし、canonical bytes、strict UTF-8、public parser round-tripを確認 |
| `ProfileJsonIsCopiedBesideTheBuiltAppAssemblyWithIdenticalBytes` | 1/1成功 | source/build outputのbyte identityとoutput loader受理を確認 |
| class内の上記以外のcontract tests | 全件成功 | oversized/invalid UTF-8/noncanonical、shape/inventory/order、identity/digest/evidence/critical mutation、catalog/API/immutabilityの負例を含む |
| `git diff --check` | PASS | tracked差分にwhitespace errorなし。未追跡review文書も`git diff --no-index --check`の診断出力なし |
| zero-byte source（`bin/obj`除外） | 0/747 | tracked + untrackedの実ソース拡張子をinventory化。空の`.gitkeep` 3件はsource破損へ誤分類していない |

directory junctionの再現は、catalogがphysical path boundaryを検査しないことまでは示した。しかし読み込ませたtargetは承認済みsourceと完全同一であり、arbitrary content、digest drift、追加profile、機微内容の受理を示していない。regular-file invariantはV2-003の受入条件でないため、後付けしたreparse拒否DataRowはreview findingとともに不採用とし、contract test集合から除外した。除外後のclass filterは12/12成功した。

### 7.4 closure status

- **Closure status: REVIEW CLOSED — V2-003のproduction contractを迂回するcode defectなし。code変更不要。**
- **未解決Critical: 0件。**
- **未解決Major: 0件。**
- test-contract discrepancy: 0件。契約化されていないreparse拒否assertionは不採用理由を記録したうえで除外し、production codeは変更していない。

### 7.5 residual later scope

- V2-002/V2-003 shared integrationで、trusted bundled pathの決定、store初期登録、current pointer、再起動no-op、canonical byte round-tripを実装・検証する。
- shared bootstrapのthreat modelでphysical localityまたはregular-file invariantが必要と決まった場合に限り、reparse/junction拒否とrace-resistant readを契約・実装・testへ同時追加する。現時点では要件を遡及して発明しない。
- build/publish先のpost-copy mutationを自動修復する要件が追加された場合は`IfDifferent`等を評価する。現契約ではsource/output byte identity testを必須gateとして維持する。
- 将来callerのtelemetry/logではraw path、profile bytes、内容を出さないことをshared integrationのsecurity reviewで確認する。
- V2-008/V2-009のComposition/render/subtitle/audio/asset consumer接続、clean-machine/package/signing/release readiness、human/platform evaluationは引き続き本closureの外である。
