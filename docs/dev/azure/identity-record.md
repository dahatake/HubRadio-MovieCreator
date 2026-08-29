# Z-006 Desktop Entra Authentication Record

- 検証日: 2026-08-30
- Subscription: `0e930937-43f5-484d-ba9e-1dd1151cf7ba`
- Tenant: `16b3c013-d300-468d-ac64-7eda0820b6d3`
- 状態: **single-tenant public-client登録と実装を検証済み。production承認ではない。**

## Entra object

| Field | Verified value |
|---|---|
| Display name | `HubRadio Movie Creator` |
| Application (client) ID | `f1088a3a-7414-4afc-bd9f-a845bde4d1f4` |
| Application object ID | `32271bae-f66c-425b-86ef-31681ea9f670` |
| Service principal object ID | `637a155d-2b53-4406-8e99-a0c2afde18c6` |
| Sign-in audience | `AzureADMyOrg` |
| Public-client redirect URI | `http://localhost` |
| Public-client flow | enabled |
| Application owner | 1件、作成時のsigned-in principalと一致 |
| Service principal | enabled、type `Application` |

Application/client ID、tenant ID、endpoint、deployment名はpublic clientを構成する非秘密識別子であり、配布assemblyから読み取れる。認証能力を与えるsecretとして扱わない。利用者のMicrosoft Entra tokenとresource RBACが認可境界である。

## Credentialとpermissionの不在

2026-08-30のMicrosoft Graph-backed Azure CLI再取得で次を確認した。

| Check | Count/value |
|---|---|
| Password credentials | 0 |
| Certificate/key credentials | 0 |
| Federated identity credentials | 0 |
| Required resource access entries | 0 |
| Optional claims | 0 |
| App roles | 0 |
| Exposed OAuth2 permission scopes | 0 |
| Pre-authorized applications | 0 |
| Web redirect URIs | 0 |
| SPA redirect URIs | 0 |
| Implicit access-token issuance | disabled |
| Implicit ID-token issuance | disabled |

初回集計ではnullの3種類のoptional-claim collectionをPowerShellの`@($null)`で各1件と誤集計した。raw objectを再取得し、nullを0件として数える関数で上表の0件を確認した。誤集計値は採用していない。

対象Azure AI account scopeで、現在のsigned-in **User**に`Cognitive Services OpenAI User`が1件ある。service principalへdata-plane roleを割り当てていない。desktop public clientは利用者として認証し、利用者自身のRBACで認可されるためである。

## Runtime design

`AzureCredentialProvider`は次を固定する。

- `InteractiveBrowserCredential`とsystem default browser。
- Azure public cloud authority、上記single tenant、上記public client。
- system-browser desktop app向けに登録したexact redirect URI `http://localhost`。
- Azure AI以外を拒否する単一exact scope `https://ai.azure.com/.default`。
- `TokenCachePersistenceOptions`未設定。Azure.Identityのinstance内cacheは利用するがdiskへ永続化しない。
- authentication record、access token、refresh tokenをapp設定、DB、log、backupへ書かない。
- caller cancellationをそのまま返し、AADSTS等の認証失敗detailをpublic exceptionへ連結しない。

2026-08-30のconfigured NuGet source検索では`Azure.Identity`の最新stableは`1.21.0`で、実装も同versionを直接pinした。同projectに対する`--vulnerable --include-transitive`監査はvulnerable packageを返さなかった。これは照会時点でconfigured sourceが公開する既知advisoryの確認結果であり、将来の脆弱性不在を保証しない。

`InteractiveBrowserCredential`はOAuth 2.0 authorization-code flowを実装し、必要時にsystem default browserを起動する。public client用secret/certificateを作成せず、implicit flowも有効化しない。PKCE等のprotocol処理はMicrosoftのidentity libraryへ委譲し、HubRadio独自のcode verifierやtoken parserを実装しない。

`AzureCredentialSession.SignOut()`はcredential referenceをatomicに破棄し、sign-outと競合して完了したtokenを呼び元へ返さない。これは**アプリ内sessionのsign-out**であり、既にEntraが発行したtokenのserver-side revocation、OS/browserのSSO cookie削除、tenant-wide logoutを主張しない。RBAC剥奪、account無効化、conditional access等でcredentialが拒否された場合は、detailを保持しないstable authentication failureへ変換する。

## Automated verification

レビュー反映後、`AzureCredentialProviderTests` 8件と`AzureCloudConfigurationTests` 1件を実行した。

- tenant/client UUID、authority、redirect URI、disk cache不使用。
- exact Azure AI scopeだけを許可し、default/empty/foreign/multiple scopeを拒否。
- sync/async authentication、sanitized failure、pre-cancellation/in-flight cancellation。
- sign-out中に完了する1件および16件同時のtokenを全件拒否。
- sign-out idempotency、null factory/credential、最小public surface。
- appのtenant/client/endpoint/deployment座標へのbinding。

fake credentialのsynthetic token文字列だけを使用し、実access tokenをtest output、file、DB、logへ出力していない。live interactive sign-in、RBAC実剥奪、browser SSO logoutは自動testでは実行していない。

repository標準`test.ps1 -Configuration Release`による全回帰はbuild/static analysisを通過し、.NET 1,365件中1,346件成功・19件environment-gated inconclusive、Web 22件成功、Python 42件成功だった。失敗は0件である。19件はsymbolic-link権限、別非管理者account、non-Windows runner等の既知環境条件であり、成功へ読み替えない。

## Adversarial review disposition

| Finding | Disposition |
|---|---|
| sync path、cancellation、empty/default scopeの試験不足 | 採用。専用testを追加した |
| 複数in-flight requestとsign-out race | 採用。16要求をbarrier後に完了させ、全件拒否を検証した |
| generationとcredentialの2-field state | 採用。restore不能な単一credential referenceのatomic exchangeへ簡素化した |
| `IsSignedOut`をguardとして誤用できる | 採用。point-in-time snapshotであることをAPI commentに明記した |
| client/tenant IDがassemblyから読める | 事実。public-client識別子でありsecretではないため、隠蔽せず本記録で境界を明記した |
| 全`Exception`をcatchして認証失敗へ変換する | 不採用。OOMやprogramming defectを認証失敗として握り潰さず、Azure.Identityの認証failureだけをsanitizationする |
| scopeをURI正規化して別表記も許可する | 不採用。最小権限allowlistはexact valueだけを許可する |
| wrapperでtoken expirationを再検査する | 不採用。Azure.Identityがtoken lifetime/refreshを管理する契約で、二重clock policyを導入しない |
| redirectを`127.0.0.1`へ変更する | 不採用。Microsoftのsystem-browser desktop手順とlive登録がexact `http://localhost`で一致する |

## Boundaries

- Interactive browser authenticationは現時点の限定evaluation用で、release/production承認ではない。
- Cloud payload minimization、人による送信確認、response validation、manual/local fallbackはZ-007～Z-010で別途実装する。
- Z-006はmodel品質、費用、network policy、release readinessの証拠にならない。
- App registrationまたはresource RBACを削除・無効化した場合、hard-coded coordinateが存在しても認証・認可は成功しない。

## Official basis

- [Desktop app code configuration](https://learn.microsoft.com/entra/identity-platform/scenario-desktop-app-configuration)
- [InteractiveBrowserCredential](https://learn.microsoft.com/dotnet/api/azure.identity.interactivebrowsercredential)
- [InteractiveBrowserCredential token-cache persistence](https://learn.microsoft.com/dotnet/api/azure.identity.interactivebrowsercredentialoptions.tokencachepersistenceoptions)
- [OAuth 2.0 authorization-code flow](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-auth-code-flow)