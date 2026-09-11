# V2-002 / V2-003 shared settings bootstrap 敵対的統合レビュー

- 対象: `V2-002 / V2-003 shared integration`
- 対象worktree: `C:\GitHub\HubRadio-MovieCreator-V2-Settings-Integration`
- レビュー日: 2026-09-03
- 状態: **PASS — REVIEW CLOSED**
- 新規受入finding: **0件**
- 未解決Critical: **0件**
- 未解決Major: **0件**
- closureでのproduction code/test変更: **なし**

> [!IMPORTANT]
> このPASSは、V2-002のversioned default-settings storeとV2-003のclosed bundled profileをproduction startupへ接続し、schema migration後の初期登録、既存global stateの保持、再起動no-op、fail-closed validation、legacy project非遡及、build/publish byte identityを確認したshared integrationだけを対象とする。設定UI、案件作成時bind、Composition/renderへの適用、clean-machine install、reparse-pointを含むpackage/install threat model、署名、外部公開、human evaluation、release readinessをPASSにしない。

## 1. Git境界と対象inventory

### 1.1 commit境界

| 区分 | commit | 備考 |
|---|---|---|
| 共通base | `e8221367ddd631aea585d91c7dd216ab5d1f851b` | V2-002/V2-003の比較基点 |
| V2-002 original task head | `8b96e2aeafcdfc8c41c0409b55548bf582082917` | 元task worktreeのhead |
| V2-003 original task head | `8cfa2c243c2b81503227fecfd0dd10956b382790` | 元task worktreeのhead |
| V2-002 integration commit | `2b9ec6fcf5f43a3945041f80e4e24f850fa8021a` | parentは共通base |
| 統合済みV2-003 / current committed HEAD | `6a13a0e3eb37e3da6d748ff3016f56d716a94448` | parentはV2-002 integration commit |

対象worktreeはdetached HEADである。V2-002 original task headとV2-002 integration commitの対象9 path、V2-003 original task headとcurrent committed HEADの対象6 pathをそれぞれ比較し、内容差がないことを確認した。commit SHAの違いを内容差として扱っていない。

### 1.2 V2-002由来のcommitted path

- `docs/dev/reviews/v2-002-default-settings-store-adversarial-review.md`
- `src/HubRadio.Infrastructure/Database/DatabaseMigrator.cs`
- `src/HubRadio.Infrastructure/Database/DefaultSettingsStore.cs`
- `src/HubRadio.Infrastructure/Database/Migrations/008_default_settings.sql`
- `src/HubRadio.Infrastructure/Database/SqliteStoreSupport.cs`
- `src/HubRadio.Infrastructure/HubRadio.Infrastructure.csproj`
- `tests/HubRadio.Infrastructure.Tests/DatabaseMigratorTests.cs`
- `tests/HubRadio.Infrastructure.Tests/DefaultSettingsStoreTests.cs`
- `tests/HubRadio.Infrastructure.Tests/ProjectStoreTests.cs`

### 1.3 V2-003由来のcommitted path

- `docs/dev/reviews/v2-003-default-settings-profile-adversarial-review.md`
- `src/HubRadio.App/DefaultSettings/BundledDefaultSettingsProfileCatalog.cs`
- `src/HubRadio.App/DefaultSettings/BundledDefaultSettingsProfileLoader.cs`
- `src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json`
- `src/HubRadio.App/HubRadio.App.csproj`
- `tests/HubRadio.App.Tests/BundledDefaultSettingsProfileTests.cs`

### 1.4 shared integrationのworking path

- `src/HubRadio.App/MainWindow.xaml.cs`
- `src/HubRadio.App/Projects/ProjectApplicationRuntime.cs`
- `src/HubRadio.App/DefaultSettings/BundledDefaultSettingsBootstrap.cs`
- `tests/HubRadio.App.Tests/BundledDefaultSettingsBootstrapTests.cs`
- `docs/dev/reviews/v2-settings-bootstrap-integration-adversarial-review.md`（本closure）

既存task review、計画/status、当時の一時作業記録、asset、他worktreeは変更していない。

## 2. 独立レビュー軸

次の3軸で、既存reviewの結論をそのまま採用せず、現在の統合working treeから再確認した。

1. correctness / ordering / idempotence / immutability / migration / retry
2. security / privacy / reliability / canonicality / corruption / fail-closed behavior
3. requirement / ADR / traceability / task ownership / scope overclaim

問題を独立再現できた場合だけcode/testを変更する方針とした。今回、新しいcontract violationまたは既存testと矛盾するproduction defectは再現しなかったため、code/testは変更していない。

## 3. 受入した指摘

**新規受入findingは0件。**

V2-002とV2-003の単独closureで既に受け入れ、修正・test化された事項は、それぞれのtask reviewに記録済みである。本closureはそれらを新規findingとして二重計上せず、統合による回帰がないことを検証した。

## 4. 不採用とした統合review主張

### A. bundled settings bootstrapがproductionから到達不能である

**不採用。** `MainWindow.InitializeWebViewAsync`は`ProjectApplicationRuntime.Create`へ、`AppContext.BaseDirectory/DefaultSettings/Profiles`を渡す。改行をまたぐ完全なproduction call patternは対象source内で一意に1件だった。

`ProjectApplicationRuntime.Create`の実行順も次のとおりである。

1. per-user workspaceのACLを検証または作成する。
2. `DatabaseMigrator.Migrate`でschema 8までmigrationする。
3. `BundledDefaultSettingsBootstrap.Initialize`でprofile/global defaultsをbootstrapする。
4. `AuthorizationService.BootstrapInitialRoles`で初期roleを作る。

したがってstore table作成前のbootstrapでも、未接続dead codeでもない。

### B. directory symlink/junctionまたはTOCTOUにより、任意・破損・機微bytesが承認済みprofileとして受理される

**現行contractのacceptance bypassとして不採用。** reparse pointを物理的に辿り得ることと、読み取った内容を承認済みprofileとして受理することは同じではない。

受理にはclosed catalog inventoryに加え、loaderによる上限、strict UTF-8、canonical JSON、exact ID/name/version、123-key inventory、evidence registry、critical semantics、approved domain content digestの一致が必要である。異なるcanonical profile、追加entry、破損bytesはdatabase mutation前に拒否される。production startup boundaryも内部例外detailを利用者向け成功として露出せず、generic failureへ正規化する既存testが成功した。

一方、physical locality、regular-file限定、reparse拒否、race-resistant file openを明示契約にするかは、package/install権限を含むthreat modelで決めるべき残余scopeとして保持する。本closureは「reparse pointが存在し得ない」または「任意の場所を一切readしない」とは主張しない。

### C. bootstrapがloader後にhard-coded digestを再検査しないため、profile driftをsilent acceptanceする

**不採用。** `BundledDefaultSettingsProfileLoader.Load`がapproved identity/contentを検証した後に返す`DefaultSettingsProfile`はimmutableなdomain valueである。bootstrapはそのcanonical JSONから新しいbyte配列を生成する。

さらに`DefaultSettingsStore.RegisterProfile`は入力byteを所有コピーし、strict canonical payloadを再parseし、domain digestを再計算し、canonical再生成byteとの完全一致を要求する。同一ID/versionで異なる内容が既にstoreにある場合も拒否する。したがって、reviewで仮定された「loader通過後にobjectが別threadから書き換えられ、storeが無検証で登録する」経路は現在の型とstore境界には存在しない。

### D. profile登録後にglobal初期化が失敗すると、partial stateのため再試行不能になる

**不採用。** profile登録はcontent-addressedかつidempotentであり、global insertとは別の明示的段階である。global insertをtriggerで失敗させる既存integration testは、exact profile 1件が残りglobal rowは0件であること、その後の再試行でrevision 0のglobal rowが1件だけ作られることを確認する。これは回復可能な段階的状態であり、silent partial successではない。

### E. revision 0のinitial/current snapshot一致をSQL CHECKで保証しないため要件違反である

**production defectとして不採用。** 現在の設計はlayered validationである。

- SQL層はSTRICT type、identity形、size、revision下限、profile/project FKを保証する。
- C#層はstrict UTF-8、canonical JSON、SHA-256 digest、profile binding、setting値、revision 0のinitial/current意味的一致を保証する。

各々はvalidだが異なる2 snapshotをrevision 0 rowへ手動注入する既存負例を単独実行し、`GetProjectSettings`がfail-closedにすることを1/1で確認した。canonical JSON parseとdigest再計算をSQLite CHECKへ重複実装する要求は確認できず、runtime層での検出を欠陥とはしない。

### F. `FR-033`と`AC-006`が正本要求に存在せず、traceabilityが成立しない

**不採用。** `docs/requirement-definition.md`には`FR\-033 設定管理`と`AC\-006 設定と自動代理評価`が存在する。Markdown source上のhyphenがescapeされているため、unescaped literalだけの検索は偽陰性になる。同じく`NFR\-003`、`NFR\-006`、`NFR\-008`も存在する。

## 5. 統合contractの最終確認

- catalog validationはdatabase mutationより前に完了する。
- global settingsが存在しない場合、exact bundled profileを登録して、そのtyped initial snapshotをrevision 0として初期化する。
- matching global settingsが既にある場合、catalog identityを解決したうえで`Existing`を返し、利用者override、revision、timestampを変更しない。
- global profileがcatalog外ならbundled profileを追加せず、既存global stateも変更しない。
- 同一ID/versionのstore内容がbundleと異なる場合、globalを初期化せずfail-closedにする。
- stored global digest corruptionはbootstrap mutation前のread validationで拒否する。
- schema 7からschema 8へ上げた既存projectは暗黙bindされず、typed `Unbound`のまま残る。
- profile登録後のglobal insert failureはretry-safeで、重複profile/global rowを作らない。
- source、Release build output、publish outputのbundle bytesは完全一致する。
- 新しいnetwork、process、credential、secret、PII、external publication経路を追加していない。

## 6. 機械検証

実行環境はWindows、PowerShell 7.6.5 Core、.NET SDK 10.0.400である。Release/x64とwarnings-as-errorsを使用し、MTP実行にはminimum discovery countを指定した。

| 検証 | 結果 | 独立に確認した内容 |
|---|---:|---|
| `HubRadio.App.Tests` Release build | PASS | warning 0、error 0 |
| `BundledDefaultSettingsBootstrapTests` class | 8/8成功 | exact bytes、typed global、restart override保持、profile mismatch、store drift、catalog failure、corruption、v7非遡及、retry |
| `BundledDefaultSettingsProfileTests` class | 12/12成功 | strict loader、closed catalog、pinned identity/inventory/evidence/semantics、packaging bytes |
| `StartAsync_InitializationThrows_ReportsFailureWithoutExceptionDetail` | 1/1成功 | startup exception detailをgeneric failure境界へ正規化 |
| `ProjectRevisionZeroWithDifferentValidSnapshotsFailsClosed` | 1/1成功 | SQLを通る異なるvalid snapshotをruntime readで拒否 |
| App publish | PASS | source/build/publishの`1.0.0.json`が各54,001 bytesで完全一致 |
| raw bundle SHA-256 | 一致 | `3f5fd0e63c575d954178e91ba32647e579f3c940ddb9b16cc8a6e2e17d968953` |
| production call pattern | 1件 | `MainWindow`からshared bootstrapを含むruntimeへの一意な接続 |
| original task head対integration commit | 一致 | V2-002対象9 path、V2-003対象6 pathに内容差なし |
| tracked `git diff --check` | PASS | whitespace error 0 |
| untracked whitespace check | 3/3成功 | bootstrap、integration test、本closureを個別検査 |
| tracked + untracked source zero-byte scan | 0/757 | Git inventoryを使用し、`bin`/`obj`を除外。missing sourceも0 |
| final working status | 5/5 expected paths | shared integration 4 pathと本closureだけ。unexpected/missing path 0 |

closure commit後にcanonical `test.ps1 -Configuration Release`を実行し、Architecture 46/46、Core 654成功・1環境依存、Infrastructure 709成功・19環境依存、App 979成功・3環境依存、Web 442、Accessibility 4、Web asset 6、Python 104、failure 0を確認した。環境依存23件はPASSへ数えていない。Web buildのchunk-size warningと、`dotnet format`のworkspace-load warningは隠さず記録し、build/analyzer/test failureは0だった。

bundleのraw-file SHA-256とdomain content digestは別契約である。domain content digestはV2-003でpinされた`5ffedbd56f43f9142b92c16e36abfd289da45cd34feea897da82101164a7414c`であり、両者を混同していない。

`HubRadio.Infrastructure.Tests`の最初の`--no-restore`実行は`project.assets.json`不在の`NETSDK1004`でtest discovery前に停止した。対象projectだけをrestore後、MTPへ誤転送された非対応`-warnaserror`を除き、`TreatWarningsAsErrors=true`を維持した最終実行が1/1成功した。前2回はtest failureとして数えていない。

利用者指示に従い、full `test.ps1`は実行していない。このclosureは上表のfocused validationをcanonical全回帰またはrelease gateとして過大表示しない。

## 7. 残余scope

- bundled install directoryのphysical locality、file/directory reparse point、ownership/ACL、race-resistant readを必須invariantにする場合は、package/install threat model、production contract、実装、testを同時に追加する。
- clean-machine install、MSIX内容・署名・upgrade、install directory権限、post-install mutationは未評価である。
- V2-004/V2-006以降のsource-only案件作成、案件settings bind、専用設定UI、bridge接続は未評価である。
- V2-008/V2-009以降のComposition、render、subtitle、layout、transition、audio、asset consumerへのsettings適用は未評価である。
- main統合後の後続taskを含む最終canonical回帰、実素材render、知覚品質、human/legal evaluation、platform適合、外部投稿、release readinessは未評価である。

## 8. closure判定

- **Closure status: PASS — REVIEW CLOSED**
- **新規受入finding: 0件**
- **未解決Critical: 0件**
- **未解決Major: 0件**
- **code/test変更: なし**

現在のshared integrationは、V2-002のstore contractとV2-003のapproved bundle contractをproduction startupで順序どおり接続し、focused validationで期待状態とfail-closed境界を満たした。残余項目は上記later/package scopeへ限定し、未評価事項をPASSへ含めない。
