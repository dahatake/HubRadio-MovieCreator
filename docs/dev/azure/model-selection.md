# Z-002 Microsoft Foundry Model Selection

- 調査日: 2026-08-30
- Subscription: `0e930937-43f5-484d-ba9e-1dd1151cf7ba`
- 推奨resource region: `japaneast`
- 暫定deployment target: `gpt-5.6-sol` version `2026-07-09`
- 暫定SKU: `DataZoneStandard`
- 暫定capacity: `10`（catalog default）
- 状態: **catalog、platform capacity、subscription quotaを確認済み。品質合格は未判定。**

## 選定方法

Azure CLI `2.89.1`で指定Subscriptionを明示し、次を2026-08-30に照会した。

1. `Microsoft.CognitiveServices` provider registration。
2. Japan Eastのmodel catalogとSKU。
3. `gpt-5.6-sol` version `2026-07-09`の全Region model capacity。
4. capacityがある各RegionのSubscription usage/quota。
5. East USの`claude-opus-5` catalogとquota（Japan Eastにはcatalog entryなし）。
6. Japan Eastの`gpt-5.6-sol` SKU catalogが返す`capacity.default=10`。

providerは`Registered`、Subscriptionは`Enabled`だった。照会時点でAIProject kindの既存projectは0件だった。

## GPT-5.6候補

Japan Eastのcatalogには次のGA modelが存在した。

| Model | Version | Lifecycle | Japan East SKU | Japan East quota used/limit |
|---|---|---|---|---:|
| `gpt-5.6-luna` | `2026-07-09` | GenerallyAvailable | GlobalStandard、GlobalProvisionedManaged | GlobalStandard `0/1000` |
| `gpt-5.6-sol` | `2026-07-09` | GenerallyAvailable | GlobalStandard、DataZoneStandard | GlobalStandard `0/1000`、DataZoneStandard `0/333` |
| `gpt-5.6-terra` | `2026-07-09` | GenerallyAvailable | GlobalStandard、GlobalProvisionedManaged | GlobalStandard `0/1000` |

公式catalog上、`gpt-5.6-sol`はChat Completions、Responses API、Structured Outputsをサポートする。今回のgrounded JSON候補検証に必要なStructured Outputsと、Asia Pacific Data Zoneを同時に選べるため、**評価用の暫定target**にする。これは他variantより日本語品質・根拠性・費用が優れるという判定ではない。

## 全Region評価

`gpt-5.6-sol` / `2026-07-09` / `GlobalStandard`について、platform capacityと指定Subscription quotaがともに正だった28 Regionを評価対象として保持する。各行の照会時点available/quotaは`1000`だった。

| Region | Platform available | Subscription available | Deployment disposition |
|---|---:|---:|---|
| `australiaeast` | 1000 | 1000 | 代替候補 |
| `brazilsouth` | 1000 | 1000 | 代替候補 |
| `canadacentral` | 1000 | 1000 | 代替候補 |
| `canadaeast` | 1000 | 1000 | 代替候補 |
| `centralus` | 1000 | 1000 | 代替候補 |
| `eastus` | 1000 | 1000 | 代替候補 |
| `eastus2` | 1000 | 1000 | 代替候補 |
| `francecentral` | 1000 | 1000 | 代替候補 |
| `germanywestcentral` | 1000 | 1000 | 代替候補 |
| `italynorth` | 1000 | 1000 | 代替候補 |
| `japaneast` | 1000 | 1000 | **第一候補** |
| `koreacentral` | 1000 | 1000 | 代替候補 |
| `northcentralus` | 1000 | 1000 | 代替候補 |
| `norwayeast` | 1000 | 1000 | 代替候補 |
| `polandcentral` | 1000 | 1000 | 代替候補 |
| `southafricanorth` | 1000 | 1000 | 代替候補 |
| `southcentralus` | 1000 | 1000 | 代替候補 |
| `southeastasia` | 1000 | 1000 | 代替候補 |
| `southindia` | 1000 | 1000 | 代替候補 |
| `spaincentral` | 1000 | 1000 | 代替候補 |
| `swedencentral` | 1000 | 1000 | 代替候補 |
| `switzerlandnorth` | 1000 | 1000 | 代替候補 |
| `switzerlandwest` | 1000 | 1000 | 代替候補 |
| `uaenorth` | 1000 | 1000 | 代替候補 |
| `uksouth` | 1000 | 1000 | 代替候補 |
| `westeurope` | 1000 | 1000 | 代替候補 |
| `westus` | 1000 | 1000 | 代替候補 |
| `westus3` | 1000 | 1000 | 代替候補 |

`availableCapacity`とquota値はAzure control planeが返したcapacity unitであり、将来の保証ではない。deployment直前に再照会する。

## Claude Opus 5比較

公式資料とEast US catalogでは次を確認した。

- `claude-opus-5` version `2`はHosted on Azure、GA。
- GlobalStandardとUS DataZoneStandardを持つ。
- Hosted on Azure版のregion availabilityは公式region表でAmericas、Asia Pacificは利用不可。East USのversion 2 catalog entryとquotaもAzure CLIで照合した。
- East US quotaはGlobalStandard `0/40`、DataZoneStandard `0/13`。
- AnthropicはClaude on Azureでも独立data processorであり、Anthropic termsとsubscription eligibility確認が必要。

Japan East優先、Asia Pacific Data Zone、Azure上の最小構成という今回の条件には一致しないため、`claude-opus-5`を選択しない。Anthropic infrastructure hosted版もADR-0004により禁止する。

## Deployment判断

全28 Regionへ同一resource/modelを重複作成すると、quota、費用管理、削除、監査対象を不必要に増やす。利用者が応答できない状態では、明示された推奨とYAGNIを優先し、次の単一構成をZ-004/Z-005の対象とする。

| 項目 | 選択 |
|---|---|
| Region | `japaneast` |
| Model | `gpt-5.6-sol` |
| Version | `2026-07-09` |
| SKU | `DataZoneStandard` |
| Capacity | `10`（catalog default） |
| Processing scope | Asia Pacific Data Zone。公式Data Zone定義上、Japan East単一region処理の保証ではない。詳細な処理・保存・abuse monitoring境界はZ-003で確定する |
| Alternative regions | 上記28 Regionを再照会対象として保持。自動作成・自動failoverは実装せず、利用者がregion変更を明示した場合だけ同じ照会を再実行する |

## 未確認・未承認

- 日本語品質、groundedness、意味保全、固有名詞、quiz answer support。
- GlobalStandardとDataZoneStandardの実測latency・費用差。
- production trafficと必要TPM。
- Z-003で確認するmodified abuse monitoringの適用有無。
- release承認。

## 公式資料

- [Foundry Models sold by Azure](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure)
- [Region availability for Foundry Models sold by Azure](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure-region-availability)
- [Claude models in Microsoft Foundry](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/claude-models)
- [Foundry Models from partners: region availability](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/models-from-partners#region-availability-by-deployment-type)