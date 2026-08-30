# ADR-0008: 限定Azure評価経路の有効化

- 状態: Accepted
- 決定日: 2026-08-30
- タスク: Z-001〜Z-007
- cloud necessity証拠: [`benchmarks/results/cloud-necessity.md`](../../../benchmarks/results/cloud-necessity.md)
- model/region証拠: [`docs/dev/azure/model-selection.md`](../azure/model-selection.md)
- data processing証拠: [`docs/dev/azure/data-processing-review.md`](../azure/data-processing-review.md)
- deployment証拠: [`docs/dev/azure/deployment-record.md`](../azure/deployment-record.md)
- identity証拠: [`docs/dev/azure/identity-record.md`](../azure/identity-record.md)

## 背景

[ADR-0006](ADR-0006-distribution-and-azure.md)と[ADR-0007](ADR-0007-stage-0-technology-selection.md)は、各決定時点でlocal不達の証拠、送信境界、model/region/quota、利用者のresource作成指示が揃っていなかったため、Azure gateを閉じた。この判断は当時の事実として維持する。

その後、Z-001で評価したlocal text modelのaccepted JSON 0件を確認した一方、manual authoringによる代替可能性とcloud necessity自体は未証明として分離した。利用者はこの未測定を成功へ読み替えず、Subscription `0e930937-43f5-484d-ba9e-1dd1151cf7ba`を指定して利用可能Regionの評価と限定Azure resource作成を明示した。Z-002〜Z-004でmodel、全28利用可能Region、quota、data processing、最小resource構成を確認し、Z-005でJapan Eastの限定評価resourceを作成した。

## 決定

文章候補の限定評価に必要なAzure分岐だけを有効化する。

| Boundary | Decision |
|---|---|
| Subscription | `0e930937-43f5-484d-ba9e-1dd1151cf7ba` |
| Tenant | `16b3c013-d300-468d-ac64-7eda0820b6d3` |
| Resource Group | `HubRadio-MovieCreator` |
| Region | `japaneast`（表示名Japan East）の単一Regionだけに実resourceを作成 |
| Foundry account | `hubradio-moviecreator-jpe-d605e526`、`AIServices` S0 |
| Model deployment | `gpt-5-6-sol`、model `gpt-5.6-sol` version `2026-07-09`、DataZoneStandard capacity 10 |
| Authentication | single-tenant desktop public client、system browser、Microsoft Entra user token |
| Authorization | account resource scopeの`Cognitive Services OpenAI User` |
| Shared secret | API key、client secret、certificate、connection stringを作成・取得・保存しない |
| Token storage | process内cacheだけ。disk、設定、DB、log、backupへ永続化しない |

「利用可能な全Region」はZ-002で同一条件により評価した。resourceの全Region複製を意味しない。複数Region作成は必要性、費用、failover要件がなく、最小構成と矛盾するため行わない。利用者推奨、data residency、model availability、quotaを満たすJapan Eastを実作成先とした。

## Runtime gate

Azure resourceとdesktop identityが存在しても、通常の編集内容を自動送信しない。

1. **完了。** Z-007で選択span、直接隣接する明示的な最小文脈、sensitivity mask、local provenance付き送信前diff、人の操作を受けるdigest-bound one-shot確認境界を実装した。
2. 動画、音声、画像、path、Windows SID、内部project ID、全文文字起こしは送信対象にしない。
3. Z-008は実在する2つ目の文章候補実装が揃うまでinterfaceを先行作成しない。
4. Z-009でclosed schemaに加えて根拠ID、数値、固有名詞を検査する。schema適合を真実性とみなさない。
5. Z-010でoffline/auth/quota失敗時のmanual/local fallback、ModelRun監査、利用停止を実装する。

Z-007はcanonical payload bytesをnetworkへ送らないlocal builderである。credential、sender、永続化、ModelRun、response処理、Z-008 interfaceを持たず、確認済みpayloadを生成できることを実コンテンツ送信の実施証拠へ読み替えない。Z-008〜Z-010が完了するまでend-user cloud featureは未提供である。Z-005のsynthetic requestはdata-plane接続証拠であり、実コンテンツ送信、model品質、groundedness、production readinessの証拠ではない。

## 置換範囲

- ADR-0006の「この案件用subscription/region/modelは未選択」と「Z-001まで選ばない」は、本ADRの確定値に限って置き換える。
- ADR-0007の「Azureは非採用でgate closed」は、resource作成とZ-006までに限って置き換える。
- ADR-0006/0007のlocal-first、最小payload、人の確認、API key禁止、production/release未承認、手動fallbackは置き換えない。
- Azure resource作成の許可を、model品質、費用、public network、release、外部公開の承認へ拡張しない。
- 今回の限定評価許可をcloud necessityの証明へ読み替えない。

## 敵対的レビュー

- 全Region評価と全Regionresource作成を分離し、作成済みの単一Region `japaneast`を過大表現しないよう修正した。
- `ContentLogging` field不在をmodified abuse monitoring無効の証拠へ読み替えず、既定監視として記録した。
- service-generated ARM fieldのwhat-if差分をIaCへ逆輸入せず、再適用前後の保持を実測した。
- public client IDとtenant IDは非秘密識別子であり、配布assemblyから読めることを明記した。
- Z-006ではsync/async、cancel、scope、revocation failure、atomic sign-out、16件のin-flight requestを試験し、実在指摘を反映した。
- `S-007`と`Z-006`は`Z-007`の独立した兄弟前提である。S-007はZ-006依存を追加せずローカル処理として完了し、本文や一致値をfindingへ保持しないこと、走査不能時に外部出力警告を維持することを検証した。
- `Z-007`は主選択と直接隣接contextを明示rangeとして受け、同じrevision/dictionary snapshotを内部で再走査する。異なるpreviewのdigest、二重確認、16並行確認、split surrogate、重複range、NFKC、public URL、quoted/relative path、file name、SID、GUID形式内部IDを敵対試験し、21件を通した。previewから未確認wire bytesを取得する経路はprivate one-shot stateへ閉じた。
- API keyの存在確認としてkey-list APIを呼ぶ提案は、secret値を取得するため不採用とした。management planeの`disableLocalAuth=true`、credential不在、source/config scanを境界とし、Q-010で再監査する。
- .NET testはrepositoryの`test.ps1`と同じbuild済みMTP executableで実行した。`dotnet test`で0件だった誤ったfilter試行を合格証拠に使っていない。
- `Azure.Identity`はconfigured NuGet source上の最新stable `1.21.0`と当日のdirect/transitive advisory不在を確認した。将来不在を保証せずQ-010で再照会する。

## 結果

- Z-001〜Z-007は完了した。
- `A-004`/`A-005`/`S-007`を入力としてZ-007のlocal payload確認境界を完了した。これを実コンテンツ送信許可へ読み替えず、Z-008〜Z-010のgateを維持する。
- Azure accountとEntra objectsは後続評価のため維持し、暗黙に削除・拡張しない。
- `G-RELEASE`、production費用、品質、rights、retention、署名、clean Windows受入は未承認のまま維持する。