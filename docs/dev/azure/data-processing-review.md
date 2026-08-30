# Z-003 Azure Data Processing Review

- 確認日: 2026-08-30
- 対象model: `gpt-5.6-sol` version `2026-07-09`
- 対象deployment type: `DataZoneStandard`
- 対象resource region: `japaneast`
- 状態: **処理境界とresource作成後の設定を確認済み。Z-007のlocal payload確認境界まで実装し、network送信は未提供。**

## 適用範囲

このレビューは、利用者が選択・マスク・確認した最小文字起こしspanから、未承認の文章Suggestionを1回生成するstateless inferenceだけを対象にする。

次は対象外であり、有効化しない。

- Responses APIの保存message history。
- Assistants/Threads、Stored completions、Files、vector stores。
- Batch、fine-tuning、On Your Data、URL retrieval。
- 音声、動画、画像、資料、全文transcriptの送信。

## Models sold by Azureのデータ処理

Microsoft公式資料で次を確認した。

- promptとcompletionは他customerやOpenAIへ提供されない。
- promptとcompletionは、明示的な許可・指示なしにfoundation modelの学習へ使われない。
- base modelはstatelessで、prompt/completionをmodel内へ保存しない。
- real-time Guardrailsがpromptとcompletionを処理する。
- abuse monitoringにより、違反の兆候があるprompt/completionのsampleがhuman review用data storeへ保存され得る。
- modified abuse monitoringの承認がある場合、human review用data storeへの保存は行われない。automated reviewは残るが、その処理のためにprompt/completionを保存せず、model trainingにも使用しない。

したがって、「API呼出しは一切保存されない」とは表示しない。通常のstateless inferenceと、abuse monitoringの例外的保存を区別する。

## Data Zoneとresidency

`DataZoneStandard`ではprompt/responseがMicrosoft定義のdata zone内で処理される。Japan Eastに作るAsia Pacific Data Zone deploymentでは、prompt/responseはJapan Eastを含むAsia Pacific data zone内のいずれかのregionで処理される。**Japan East単一regionでの処理は保証されない。**

Global deployment typeはmodelが利用可能な任意geographyで処理され得るため、今回の初期構成には選ばない。

Global/DataZone deploymentで保存されるdata at rest（abuse monitoring storeを含む）は、resourceのcustomer-designated Azure geographyへ保存される。resource作成後にlocation、SKU、capabilitiesをmanagement planeから再取得して証拠へ残す。

## App側の送信allowlist

送信候補は次の全条件を満たす必要がある。

1. 利用者が目的とsource spanを明示選択している。
2. payload previewを利用者が確認している。
3. PII、秘密、内部URL、credential、不要な固有名詞の候補がpayload上でmask済みで、走査が完全である。
4. UIがregion、model ID/version、deployment typeを表示する。
5. payloadがclosed schemaに一致する。
6. responseもclosed schemaに一致し、known evidence token IDだけを参照する。

Z-007は1〜5のうちlocalで検証できる境界を実装した。exact TranscriptRevisionと辞書snapshotを再走査し、主選択と直接隣接する明示contextだけをrequest内evidence ID、role、masked textへ変換する。previewはlocal provenance、mask差分、region、model、deployment type、processing scope、retention disclosureを表示し、payloadと表示条件を結合したdigestへのone-shot確認後だけdefensive payload bytesを発行する。network senderとresponse処理は持たないため、6および実送信はZ-008〜Z-010まで未完了である。

送信payloadに含めてよい値:

- 利用者が確認した文字列。
- request内だけで有効なopaque evidence token ID。
- 出力種別とlanguage等、closed enumで表す最小設定。

送信payloadに含めてはいけない値:

- 音声、動画、画像、binary、base64 media。
- file path、file name、URL。
- Windows SID、user principal name、email address。
- local project ID、asset ID、job ID、artifact ID。
- API key、access token、connection string。
- 全文transcriptまたは選択外の文脈。

## 応答と監査

- model出力は`Suggestion`に限定し、自動採用しない。
- schema適合をtruth、groundedness、意味保全の証明にしない。
- 通常logへprompt、completion、token textを保存しない。
- `ModelRun`へprovider、resource region、deployment、model/version、input digest、response digest、送信区分、採否を記録する。
- 人が採用・修正・却下した判断を別audit eventとして残す。
- Azure失敗、offline、auth失敗、quota不足時にlocal/manual編集を失わない。

## 認証

- desktop向けMicrosoft Entra ID authenticationを使う。
- API keyは取得・保存・設定しない。
- tokenをsource、`.env`、DB、log、backupへ保存しない。
- data-plane権限は利用者へresource scopeの最小roleで付与する。

## Claude Opus 5を選択しない理由

`claude-opus-5` version 2はAzure-hostedであってもAnthropicがseller/operatorおよび独立data processorで、Anthropic termsとdata processing addendumが適用される。Hosted on Azure版のData ZoneはUSで、Asia Pacific availabilityはない。Japan East優先と今回の処理境界に一致しないため採用しない。

Anthropic infrastructure hosted版は、選択Azure region外・Azure外で処理され得るためADR-0004により禁止する。

## Resource作成後の確認項目

1. accountの`location`が`japaneast`である。
2. deployment SKUが`DataZoneStandard`である。
3. model/versionが`gpt-5.6-sol` / `2026-07-09`である。
4. **確認済み。** account capabilityの`ContentLogging` fieldは2026-08-30のmanagement-plane再取得で不在だった。`false`とはみなさず、既定abuse monitoringが適用されるものとして扱う。
5. **確認済み。** `disableLocalAuth=true`としてdata planeをEntra IDに限定した。
6. **確認済み。** `publicNetworkAccess=Enabled`を記録し、秘密を含まないsynthetic smoke requestだけで動作確認した。実コンテンツは送信していない。

## 未決事項

- 組織がmodified abuse monitoringの承認を保有するか。
- Japan geographyに対する組織固有のresidency要件。
- public networkを禁止する組織policyの有無。
- prompt/completionの保護証拠領域での保持要否。
- production traffic、費用上限、必要capacity。

未決事項は成功扱いにしない。今回のresourceは限定評価用であり、release approvalではない。

## 公式資料

- [Data, privacy, and security for Models sold by Azure](https://learn.microsoft.com/azure/foundry/responsible-ai/openai/data-privacy)
- [Deployment types for Foundry Models](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/deployment-types)
- [Compare hosting options for Claude models](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/claude-models-hosting-comparison)
- [Claude models in Microsoft Foundry](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/claude-models)