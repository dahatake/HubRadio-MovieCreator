# Z-004 Minimal Azure Resource Plan

- 計画日: 2026-08-30
- Subscription: `0e930937-43f5-484d-ba9e-1dd1151cf7ba`
- Resource Group: `HubRadio-MovieCreator`
- Region: `japaneast`
- 状態: **作成前検証済み。利用者の明示指示あり。**

## 作成対象

| 種別 | 名前 | 設定 |
|---|---|---|
| Resource Group | `HubRadio-MovieCreator` | location `japaneast` |
| Foundry resource | `hubradio-moviecreator-jpe-d605e526` | `Microsoft.CognitiveServices/accounts`、kind `AIServices`、SKU `S0`、custom subdomain同名 |
| Model deployment | `gpt-5-6-sol` | model `gpt-5.6-sol`、version `2026-07-09`、SKU `DataZoneStandard`、capacity `10` |
| Data-plane RBAC | 現在のsigned-in principal | resource scopeの`Cognitive Services OpenAI User` |

account名のsuffixは`subscription ID | resource-group | japaneast`のSHA-256先頭8文字から決定的に生成した。利用者名やmedia情報を含まない。

## 作成しないもの

- Foundry project、hub、agent、capability host。
- Storage、Cosmos DB、Azure AI Search、Key Vault。
- VNet、private endpoint、VPN、public IP。
- App Service、Functions、Container Apps、AKS、VM。
- Application Insights、Log Analytics、cloud telemetry。
- service principal secret、API key、connection string。
- 複数Regionへのresource/modelの重複作成。

HubRadioはローカルdesktop appからstateless inferenceだけを行い、RAG、agent、server-side application hostingを必要としないため、bare `AIServices` resourceを使う。

## 作成前に確認した事実

- Subscriptionは`Enabled`。
- `Microsoft.CognitiveServices` providerは`Registered`。
- Resource Group `HubRadio-MovieCreator`は未作成。
- 同Subscriptionには既存OpenAI accountが1件あるが、別resource group・別用途であり再利用しない。
- 提案account名と同名のaccountはSubscription内に存在しない。
- Japan Eastの`gpt-5.6-sol` DataZoneStandard quotaは`0/333`。
- catalogのdefault deployment capacityは`10`。
- 現在principalはSubscription scopeのOwner、Contributor、User Access Administrator、Cognitive Services Contributor、Foundry Userを継承しており、作成とrole assignmentを実行できる。
- Subscriptionへ適用されたpolicy assignmentは3件で、SQL/OSS database protectionに限定されていた。Cognitive Servicesまたはpublic networkを対象とするassignmentは検出しなかった。これはresource作成APIの最終policy評価を代替しない。

## Networkとauthentication

desktop clientが任意の社内Windows PCから利用する初期評価であり、接続元VNet/VPN要件は提供されていない。このためpublic network endpointを有効にする。ただし次を必須とする。

1. custom subdomainを作成する。
2. data-planeのlocal/key authenticationを無効化する。
3. `Cognitive Services OpenAI User`をresource scopeで明示割当する。
4. appはMicrosoft Entra tokenだけを送る。
5. endpointとdeployment名は非秘密設定として扱い、token/keyは保存しない。

private networkは、接続元networkとDNS要件が提供されていない状態で先行作成しない。組織policyがpublic networkを禁止する場合は、このresourceをproduction利用せず、VNet設計を別taskとして承認する。

## 作成順序

1. deployment直前にJapan East model catalogとDataZoneStandard quotaを再照会する。
2. model lifecycleがGA、versionが`2026-07-09`、catalog default capacityが`10`であることを再確認する。
3. 現在principalのobject IDと、resource作成・role assignmentに必要な継承roleを再確認する。
4. Subscriptionに適用されたAzure Policyを照会し、public networkまたはCognitive Services作成をdenyするpolicyがないか確認する。denyされる場合は回避せず停止する。
5. Resource Groupが未作成であることを再確認する。既に存在する場合はlocationと全resourceを列挙し、空でないRGへ追記しない。
6. account名suffixを同じSHA-256入力から再計算し、計画名と一致させる。
7. Resource Groupを作成し、provisioning stateを検証する。
8. `AIServices` S0 accountをJapan Eastへcustom domain付きで作成する。
9. management APIで`disableLocalAuth=true`と`publicNetworkAccess=Enabled`を設定する。
10. accountのkind、SKU、location、custom domain、local auth、network設定、`ContentLogging` capabilityの有無と値を再取得する。
11. 現在principalへresource scopeの`Cognitive Services OpenAI User`を割り当てる。
12. deployment名`gpt-5-6-sol`にmodel ID `gpt-5.6-sol`をDataZoneStandard capacity 10で作成する。
13. deploymentのmodel、version、SKU、capacity、provisioning stateを再取得する。
14. API keyを取得せず、Microsoft Entra tokenで秘密を含まないsynthetic structured-output smoke testを行う。
15. resource inventory、role assignment、quota使用量をpath/tokenなしで記録する。

## 作成後の合格条件

- accountは`Succeeded`、kind `AIServices`、SKU `S0`、location `japaneast`。
- `disableLocalAuth=true`。
- `ContentLogging=false`が存在する場合だけmodified abuse monitoring適用済みと記録する。field不在は作成失敗ではなく、既定abuse monitoringが適用される状態として記録する。
- deploymentは`Succeeded`、`gpt-5.6-sol` / `2026-07-09` / `DataZoneStandard` / capacity `10`。
- resource scopeに現在principalの`Cognitive Services OpenAI User`が1件ある。
- key list APIを呼んでいない。
- synthetic requestがEntra IDで成功し、closed JSON schemaへ適合する。
- prompt/response本文、access token、absolute pathをGit・DB・通常logへ保存していない。

## Failureとrollback

- Resource Group作成だけで後続が失敗した場合、他resourceがないことを確認してResource Groupを削除する。
- account作成後にsecurity設定、RBAC、deploymentのいずれかが失敗した場合、accountを評価用として残さず削除する。
- smoke test不合格時はdeploymentを削除する。model品質不合格をresource作成成功へ読み替えない。
- 削除前に対象IDが本計画のSubscription/RG/account/deploymentと完全一致することを再検証する。
- 既存`dahatake-UC-006/openai-chatbot-99bd55`は変更・削除しない。

## Cost境界

- `AIServices` S0 account自体を複数Regionへ作らない。
- pay-as-you-go DataZoneStandard deploymentをcapacity 10で開始する。
- PTU、ProvisionedManaged、reservationは作成しない。
- traffic、token数、予算が未測定のため費用保証をしない。
- model評価後、不要ならdeploymentとresourceを明示削除する。

## 公式根拠

- [Create an Azure AI Services resource](https://learn.microsoft.com/azure/ai-services/multi-service-resource?pivots=azcli)
- [Configure Microsoft Entra ID for Azure OpenAI](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/managed-identity)
- [Azure OpenAI role-based access control](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/role-based-access-control)
- [Disable local authentication](https://learn.microsoft.com/azure/ai-services/disable-local-auth)