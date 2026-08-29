# Z-005 Azure Deployment Record

- 実行日: 2026-08-30
- Subscription: `0e930937-43f5-484d-ba9e-1dd1151cf7ba`
- Deployment: `hubradio-z005-20260830`
- Deployment state: `Succeeded`
- Deployment timestamp: `2026-08-30T07:13:01.354391+09:00`
- Correlation ID: `cd2986e8-863a-4e2d-b4a3-e0ce1302d306`

## IaC

| File | SHA-256 |
|---|---|
| `infra/main.bicep` | `5a2a17ab20b30d7817bb4a324add6a7e75d861c14d132703e2380d86f67842a3` |
| `infra/foundry.bicep` | `385eb117e64251ff1e70f3d7680643fbd572ced0b9455b95f19f043d476cb144` |

`az deployment sub what-if`は次の4件だけを`Create`として返し、status `Succeeded`、diagnosticsなしだった。

1. Resource Group。
2. `AIServices` account。
3. model deployment。
4. resource-scope role assignment。

既存resourceへの変更・削除は表示されなかった。what-if確認後に同じBicep parameterでdeploymentを実行した。

## 作成結果

| Resource | Value |
|---|---|
| Resource Group | `HubRadio-MovieCreator` |
| RG location/state | `japaneast` / `Succeeded` |
| Foundry account | `hubradio-moviecreator-jpe-d605e526` |
| Account resource ID | `/subscriptions/0e930937-43f5-484d-ba9e-1dd1151cf7ba/resourceGroups/HubRadio-MovieCreator/providers/Microsoft.CognitiveServices/accounts/hubradio-moviecreator-jpe-d605e526` |
| Account kind/SKU | `AIServices` / `S0` |
| Account location/state | `japaneast` / `Succeeded` |
| Endpoint | `https://hubradio-moviecreator-jpe-d605e526.cognitiveservices.azure.com/` |
| Deployment | `gpt-5-6-sol` |
| Model/version | `gpt-5.6-sol` / `2026-07-09` |
| Deployment SKU/capacity | `DataZoneStandard` / `10` |
| Deployment state | `Succeeded` |
| Version upgrade | `NoAutoUpgrade` |
| RAI policy | `Microsoft.DefaultV2` |

Resource Group inventoryは`Microsoft.CognitiveServices/accounts` 1件だけだった。Foundry project、Storage、Search、Cosmos DB、Key Vault、network、hosting resourceは作成していない。

## Security検証

| Check | Result |
|---|---|
| Custom subdomain | account名と一致 |
| `disableLocalAuth` | `true` |
| `publicNetworkAccess` | `Enabled` |
| Data-plane role | `Cognitive Services OpenAI User` |
| Role scope | 作成したaccount resourceのみ |
| Principal | 実行時に確認したsigned-in principal object ID |
| `ContentLogging` capability | 2026-08-30のmanagement-plane再取得でfield absent |
| API key list operation | 未実行 |

`ContentLogging` field不在はmodified abuse monitoring適用済みを意味しない。公式資料の判定規則に従い、既定abuse monitoringが適用されるものとして扱う。

## Quota検証

Japan East `OpenAI.DataZoneStandard.gpt-5.6-sol`:

- deployment前: used `0` / limit `333`
- deployment後: used `10` / limit `333`
- deployment後available: `323`

単位はAzure usage APIが返した`Count`である。必要TPM・費用・production trafficの保証へ読み替えない。

## Data-plane smoke test

Microsoft Entra tokenを`https://ai.azure.com` resource向けに取得し、API keyなしでOpenAI v1 Chat Completions endpointへsynthetic requestを送った。

| Check | Result |
|---|---|
| Endpoint host | `hubradio-moviecreator-jpe-d605e526.openai.azure.com` |
| Requested deployment | `gpt-5-6-sol` |
| Reported model | `gpt-5.6-sol-2026-07-09` |
| Finish reason | `stop` |
| Closed structured output | valid |
| Prompt/response本文の記録 | なし |
| Access tokenの記録 | なし |

smoke schemaは`status=ok`とrequest内だけで有効なopaque `evidenceId=e1`だけを許可し、additional propertyがないことをローカルで検査した。`e1`とprompt/response本文は永続化せず、このdeployment recordにはshape検証結果だけを残した。このsynthetic成功は日本語品質、groundedness、意味保全、業務要件の合格証拠ではない。

## 再適用検証

敵対レビュー後、同一parameterで2回目のwhat-ifとdeploymentを実行した。

- 初回templateではservice既定の`Microsoft.DefaultV2`が未宣言で、what-ifがRAI policy削除を示したため、IaCへ明示して修正した。
- RAI policy明示直後のwhat-ifでは、service read-only field `properties.currentCapacity`だけがDelete deltaとして残った。
- service状態反映後の最終what-ifでは、accountのservice生成`properties.armFeatures`とdeploymentの`properties.currentCapacity`がtemplate未指定のDelete deltaとして表示された。これらを入力設定としてIaCへ逆輸入していない。
- 同じBicepの再deploymentは`Succeeded`。
- 再適用前後でaccount showの`armFeatures`はfield不在、deployment `currentCapacity=10`とRAI policy `Microsoft.DefaultV2`は保持され、resource inventoryは1件のままだった。

capacity `10`はJapan East DataZoneStandard catalogのdefaultで、単一利用者の限定評価開始値である。production throughput requirementを表さず、実測なしに増やさない。

## Regionの扱い

Z-002で利用可能な28 Regionを評価したが、実resourceは利用者推奨と最小構成に従いJapan Eastの1件だけを作成した。自動failover、複数Region複製、PTUは作成していない。

## 残余条件

- desktop appのInteractive Browser authenticationとpayload gateはZ-006/Z-007で実装する。
- model responseのgrounded-ID validationとlocal/manual fallbackはZ-009/Z-010で検証する。
- public networkを禁止する組織policyが後から提示された場合、このresourceをproduction利用しない。
- modified abuse monitoring、production費用、release readinessは未承認。