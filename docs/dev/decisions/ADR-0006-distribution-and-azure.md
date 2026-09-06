# ADR-0006: Windows配布と条件付きAzure

- 状態: Accepted
- 決定日: 2026-08-27
- タスク: P-010

> [!NOTE]
> 本ADRは決定時点の記録である。2026-08-30に評価したlocal modelの不達証拠と利用者の限定評価指示が得られた後のSubscription、Region、resource、identityは[ADR-0008](ADR-0008-bounded-azure-evaluation.md)が限定的に置き換える。cloud necessity自体は未証明であり、local-first、最小payload、API key禁止、release未承認は継続する。

> [!NOTE]
> 2026-09-06の利用者指示により、初期Windows配布のMSIX/Evergreen既定は[ADR-0010](ADR-0010-one-action-portable-launch.md)のOS-only単一EXE/Fixed Runtime方式と成立性gateへ置き換える。以下のWindows配布記述とMSIX測定は履歴として保持し、インストーラー関連は将来事項とする。Azure部分はこの変更の対象外である。

## Windows配布

初期配布はWindows 11 x64向けの署名済み社内MSIXを既定とする。

- WinUI 3ホスト、React静的asset、self-contained Python worker、FFmpeg/ffprobeを1つのPackaging Projectへ収めるPoCを行う。
- Single-project MSIXは複数実行ファイル制約があるため既定にしない。
- WebView2はEvergreen Runtimeを使い、起動時に存在と利用可能版を確認する。
- Microsoft Store公開は初期対象外。
- 署名主体、証明書、Intune/Configuration Manager等の社内配布経路は未提供であり、`PK-007`まで未決として保持する。
- 開発PCはWindows Insider buildであるため、release前にproduction-channel Windows 11 24H2 x64 clean環境でinstall/upgrade/uninstallを試験する。

## Azureの現状

2026-08-27にAzure MCP `subscription_list`をcredential認証・read-onlyで実行した結果、現在のcredentialから有効なAzure subscriptionを複数参照できた。機密性のため個別結果はリポジトリへ保存せず、件数と既定有無だけを記録した。

| 項目 | 記録 |
|---|---|
| 参照可能subscription | 86件 |
| Enabled | 86件 |
| 既定subscription | 0件 |
| この案件用subscription | 未選択 |
| データ所在地要件 | 未提供 |
| Azure region | 未選択 |
| Foundry resource/project | 未作成・未選択 |
| model deployment | 未作成・未選択 |

subscription ID、tenant ID、display nameはこのADRへ保存しない。

## 選択しない理由

Azureは通常critical pathに含めない。ローカル文章モデルまたは決定論的な手動経路で要件を満たせるかを先に測る。`Z-001`でローカル不達が確認されるまで、subscription、region、modelを選ばない。

Azureが必要になった場合は次の順で決める。

1. 送信する文字起こしspanと分類を確定する。
2. データ処理者、保持、residency、利用条件を確認する。
3. 当日時点のFoundry catalog、model availability、quotaを確認する。
4. 複数subscriptionから用途と権限が適切な1件を選ぶ。
5. Resource Group名を必ず`HubRadio-MovieCreator`とする。
6. ユーザーの明示的な作成指示後にだけresourceを作る。

## モデル

GPT-5.6系列やAzure-hosted Claude Opus 5を名前だけで先行選択しない。モデルID、GA/preview、Structured Output、日本語品質、Azure-hostedか、quota、regionを選択時に再確認する。Anthropicインフラhost版はAzure限定条件を満たさないため選ばない。

## 秘密と認証

Azure経路を実装する場合はデスクトップ向けEntra認証とRBACを使い、APIキーをソース、設定、DB、ログ、バックアップへ保存しない。現時点では`.env`もAzure設定ファイルも不要なので作成しない。
