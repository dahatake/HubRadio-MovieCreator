# V2-002 DefaultSettingsStore 敵対的レビュー

- 対象task: `V2-002 — 設定store、reset、snapshot migration`
- 対象base commit: `e8221367ddd631aea585d91c7dd216ab5d1f851b`
- レビュー日: 2026-09-02
- 状態: **PASS**
- 未解決Critical/Major: **0件**

> [!IMPORTANT]
> このPASSは、SQLite schema 8、default-settings profile/global/project snapshotの永続化、OCC、案件初期snapshotへの個別/全reset、legacy projectのunbound状態、再起動復元を対象とする。具体的なproduction profile、runtime bootstrap、設定UI、source-only案件作成、render、自動代理評価、実素材成果物、human evaluation、external publication、package、release readinessをPASSにしない。

## 1. 対象と独立レビュー軸

主な対象は次のとおり。

- `src/HubRadio.Infrastructure/Database/Migrations/008_default_settings.sql`
- `src/HubRadio.Infrastructure/Database/DefaultSettingsStore.cs`
- `src/HubRadio.Infrastructure/Database/DatabaseMigrator.cs`
- `src/HubRadio.Infrastructure/Database/SqliteStoreSupport.cs`
- `src/HubRadio.Infrastructure/HubRadio.Infrastructure.csproj`
- `tests/HubRadio.Infrastructure.Tests/DefaultSettingsStoreTests.cs`
- `tests/HubRadio.Infrastructure.Tests/DatabaseMigratorTests.cs`
- `tests/HubRadio.Infrastructure.Tests/ProjectStoreTests.cs`

独立レビューは次の3軸で行われた。

1. correctness / invariant / transaction / OCC / canonicality
2. security / privacy / reliability / corruption / FK / rollback
3. requirement / schema / ADR / traceability / scope claim

要求・計画との照合では、additive schema 8、profile identity/version/digestとcanonical payload、global settings、案件作成時の不変initial snapshot、stale-write拒否、案件initial値へのreset、legacy unbound、product defaultをmigration SQLへ埋め込まない境界を確認した。要求適合レビューからは具体的な違反が報告されなかった。

## 2. 受入した指摘と反映

独立レビューのproduction defect主張は再現しなかった。一方、境界挙動を明示するtest evidenceの追加は有用だったため、次を受け入れた。closureではproduction codeとmigration SQLを変更していない。

| ID | 重要度 | 受入内容 | 再現・反映 |
|---|---|---|---|
| `V2-002-R01` | Minor / test coverage | 2つのstoreから同じ案件を同時bindする契約を明示するtestがなかった | `ConcurrentIdenticalProjectBindingsAcrossStoresAreIdempotent`を追加。同一snapshotでは2呼び出しとも成功し、同じrevision 0を返し、DB rowは1件だけであることを確認した |
| `V2-002-R02` | Minor / test coverage | 異なるinitial snapshotによる同時bindの分類を明示するtestがなかった | `ConcurrentDifferentProjectBindingsAcrossStoresRejectExactlyOne`を追加。1件だけ成功し、他方はexact `InvalidOperationException`と既存のsemantic messageで拒否され、DB rowは1件だけであることを確認した |
| `V2-002-R03` | Minor / layered validation | revision 0で、各々はvalidだがinitial/currentが異なるsnapshotを読む負例が明示されていなかった | `ProjectRevisionZeroWithDifferentValidSnapshotsFailsClosed`を追加。SQL層を通る手動改変をruntime read validationがgenericなinvalid-stored-data errorでfail-closedにすることを確認した |
| `V2-002-R04` | Minor / boundary evidence | `int.MaxValue`での増分と無変更保証がtest化されていなかった | `RevisionOverflowFailsClosedWithoutMutatingStoredSettings`を追加。`OverflowException`、revision/timestamp/snapshot不変を確認した |
| `V2-002-R05` | Minor / FK evidence | 既存testは参照中profile/projectのDELETE RESTRICTを検査していたが、接続上の有効状態とUPDATE RESTRICTを同じtestで明示していなかった | `Schema_DefaultSettingsEnforcesIdentityBoundsBindingsAndRestrictedDeletes`へ`PRAGMA foreign_keys = 1`、profile ID更新拒否、project ID更新拒否を追加した |

## 3. 不採用とした主張

### A. `BindProjectSettings`にread-before-insert raceがあり、同一bindの片方がUNIQUE違反になる

**不採用。** `BindProjectSettings`はreadより前に`BeginTransaction(deferred: false)`を開始する。`Microsoft.Data.Sqlite` 10.xでは非deferred transactionは`BEGIN IMMEDIATE`として開始され、SQLiteではpending changeを持てるtransactionは同時に1つだけである。競合writerはbindのread/insert区間へ同時進入せず、先行transaction完了後の状態を読む。

公式根拠:

- [Microsoft.Data.Sqlite transactions](https://learn.microsoft.com/dotnet/standard/data/sqlite/transactions)
- [SqliteConnection.BeginTransaction API](https://learn.microsoft.com/dotnet/api/microsoft.data.sqlite.sqliteconnection.begintransaction?view=msdata-sqlite-10.0.0)

2-store実測でも、同一snapshotは2件とも成功し、異なるsnapshotは1件成功・1件semantic rejectionだった。さらに、仮にINSERT自体がPRIMARY KEY違反になった場合、`ExecuteNonQuery()`が`SqliteException`を送出するため、戻り値0を検査する`EnsureSingleMutation`がUNIQUE違反をgeneric messageへ変換する、というレビュー説明も成立しない。

### B. SQLだけではsnapshot payload/profile意味整合とrevision 0のsnapshot一致を保証できない

**production defectとして不採用。** schemaとruntime parserの責務分離はV2-001からの明示契約である。

- SQL: strict type、identity形、size、非負revision、profile/project FKを保証する。
- C#: canonical JSON、SHA-256 digest再計算、profile identity/version/digest、setting集合・値制約、revision 0のinitial/current一致を保証する。

`snapshot_digest`はsnapshot全値のdigestであり、profile定義の`profile_digest`と同値であるべき列ではない。値をoverrideした正当なsnapshotでは両digestは異なる。SQLiteのFK/CHECKへcanonical JSON parseやSHA-256再計算を要求する案は、この責務分離を壊す。

手動SQL改変はrevision 0で異なるvalid snapshotを保存できるが、追加testどおり読み出し時にfail-closedとなる。generic errorは破損payloadや内部構造を外部へ露出しない既存方針であり、より詳細なmessageを要求する契約はない。

### C. SQLite INTEGERとC# `int`の上限差がrevision defectである

**不採用。** 公開domain contractのrevision型は`int`で、更新は`checked(current.Revision + 1)`、読取は`ReadInt32`である。SQLite INTEGERの物理容量がより大きいことは、domain上限を64-bitへ拡張する要求にならない。`int.MaxValue`からの更新はwrapせず、SQL mutation前に`OverflowException`で停止し、追加testどおり保存状態は不変だった。別のdomain例外、`long`化、またはSQL上限追加を要求する正本契約は確認できなかった。

### D. `ListProfiles`のSQL lexicographic orderをC# sortが隠している

**不採用。** API契約は返却結果のsemantic orderであり、DB内部の中間orderではない。`ListProfiles`は最終的に`ProfileComparer`でprofile ID、major、minor、patchを比較する。既存の`ProfilesRoundTripExactCanonicalBytesAcrossRestartInSemanticOrder`は、まさに`1.2.0`が`1.10.0`より前に来ることを検査していた。`DefaultSettingsStore`はsealedであり、レビューで挙げられた「subclassがsortを迂回する」経路も存在しない。

### E. FK RESTRICTが未検査で、通常のDELETEでorphanが作られる

**不採用。** 既存の`Schema_DefaultSettingsEnforcesIdentityBoundsBindingsAndRestrictedDeletes`は、global/project settingsから参照中のprofile削除と、settingsを持つproject削除を既にconstraint violationとして検査していた。store test fileだけを検索してmigration testを除外したレビュー結果は不完全だった。

closureでは証拠をさらに明示し、同じ接続で`PRAGMA foreign_keys`が1であることと参照先IDのUPDATE RESTRICTも検査した。FKを意図的に無効化するかDB fileを外部破損させなければorphan再現はできず、その場合もruntime readはmissing/mismatched profileをinvalid stored dataとしてfail-closedにする。

### F. 要求・schema・traceability違反

独立した要求適合レビューでは、具体的な違反は確認されなかった。V2-003の具体的profile bundle、V2-002/V2-003 shared bootstrap、V2-004以降のUI/application接続をV2-002へ先行実装しない。

## 4. 最終レビュー結果

次を確認した。

- schema 8はadditiveで、profile/global/project settings tableをSTRICTに作成する。
- profile canonical bytes、identity/version/digest、snapshot payload/digestは保存後にも再検証される。
- global/project更新はexpected revisionとimmediate transactionを使い、stale writerを拒否する。
- project initial snapshotはbind後に不変で、global変更は既存projectへ遡及しない。
- 個別/全resetは最新globalではなくproject initial snapshotへ戻る。
- legacy projectは暗黙bindされず、型付きunbound状態で読める。
- invalid UTF-8、noncanonical JSON、digest drift、missing profile、payload超過、逆行timestamp、SQL injection文字列、partial update failureをfail-closedにする。
- SQL/runtimeのlayered validation、FK RESTRICT、rollback、再起動復元が互いを補完する。
- path、secret、transcript、PII、network、process起動を追加していない。

Critical/Majorの未解決指摘は0件であり、判定は**PASS**である。

## 5. 機械検証

実行環境はWindows、PowerShell 7.6.5 Core、.NET SDK 10.0.400、`Microsoft.Data.Sqlite` 10.0.11である。MTP実行はすべて`--minimum-expected-tests`付きで、対象worktreeのRelease executableを絶対パスから起動した。

| 検証 | total | success | failure | skip | 結果 |
|---|---:|---:|---:|---:|---|
| closureで追加した4件 | 4 | 4 | 0 | 0 | PASS |
| 補強したdefault-settings FK schema test | 1 | 1 | 0 | 0 | PASS |
| `DefaultSettingsStoreTests`全体 | 16 | 16 | 0 | 0 | PASS |
| canonical Release全回帰 | 2,391 .NET / 452 Web / 104 Python | 2,368 .NET / 452 Web / 104 Python | 0 | 23 .NET | PASS（環境依存23件は別計上） |

上のfocused実行集合は重複するため、合計21件の一意testを実行したという意味ではない。closure後にcanonical `test.ps1 -Configuration Release`を実行し、Architecture 46/46、Core 654成功・1環境依存、Infrastructure 709成功・19環境依存、App 959成功・3環境依存、Web 442、Accessibility 4、Web asset 6、Python 104、failure 0を確認した。Release buildはwarning/error 0、exit 0で成功し、編集後IDE diagnosticsも0件だった。

## 6. 残余scope

- V2-003: 調査台帳に結合した具体的な版付きproduction profileとstrict bundle/catalog。
- V2-002/V2-003 shared integration: bundle 1.0.0の初期登録/current設定、再起動no-op、byte identity、v7 upgrade後の非遡及を統合確認する。
- V2-004/V2-006: source-only案件作成と全設定UIからstoreを利用するapplication/bridge接続。
- V2-014: 統合後の最終canonical全回帰、実素材成果物、package再検証、外部gate一覧。
- SQLite file自体の外部改変、storage corruptionからの修復・auditは、検出してfail-closedにする境界を越える運用/recovery scopeである。
- 実素材render、知覚品質、human/legal evaluation、external platform適合、signed package、release readinessは未評価である。
