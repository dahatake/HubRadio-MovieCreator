# ADR-0010: OS-onlyの1操作起動と単一EXE配布

- 状態: Accepted（方式の採用。成立性・配布受入の完了ではない）
- 決定日: 2026-09-06
- タスク: OSL-002
- 根拠: 利用者が[実装計画](../../../work/20260906-OneActionLaunch-ImplementationPlan.md)のD01〜D18をデフォルトで採用し、全タスク実行を指示した。
- 要求: [NFR-009 / AC-007](../../requirement-definition.md)、既存NFR-004/007/008、BR-004

## 置き換える範囲

[ADR-0006](ADR-0006-distribution-and-azure.md)のWindows配布に関する初期MSIX/Evergreen既定を本ADRで置き換える。旧MSIXのproject、builder、測定結果は履歴・将来資産として保持し、未実施install/upgrade/uninstallを完了にしない。

Azureの判断、[ADR-0008](ADR-0008-bounded-azure-evaluation.md)の限定評価、[ADR-0009](ADR-0009-source-only-defaults-and-automated-evaluation.md)の業務範囲・既定値・単一利用者方針は変更しない。新しいクラウド、認証情報、`.env`は本件に不要である。

## 決定

1. 対象はサポート中のproduction-channel Windows 11 24H2以降 x64の標準ユーザー。未検証edition、Windows 10、ARM64へ対応を拡張しない。
2. 保存済み `HubRadio.App.exe` への1回の起動指示を入口とする。ブラウザー等による取得操作は含めない。取得後の手動展開・依存導入・セットアップは不要にする。
3. 既存WinUIアプリをSDK標準のunpackaged/self-contained単一EXEとしてpublishする。内部ファイル展開と既存child processを許容し、展開不要・単一プロセスとは表示しない。
4. .NET/Windows App SDK Runtime、Fixed Version WebView2、既存Python runtime、固定FFmpeg/ffprobe、採用CPU ASRモデル、Web静的assetとprofileを同梱する。取得後の起動にnetwork downloadを必要としない。
5. SDK標準の自己展開先を利用し、業務データは既存の `%LOCALAPPDATA%\HubRadio.MovieCreator` 配下へ保存する。実際の展開rootと `AppContext.BaseDirectory` はOSL-003で確認する。
6. Pythonは既存のembeddable/固定wheel/`-I -B`/`._pth`契約を再利用する。pip、SDK、Node.js、PowerShell 7、GPU用追加Runtimeを利用者PCの前提にしない。
7. 保留・不同意・非採用モデルを新規取得・追加同梱・有効化しない。既存固定wheelの台帳済み付随資産は、そのhash・許諾・noticeを維持する。同梱を採用・実行許可・一般配布承認へ読み替えない。
8. WebView2の配布版は同梱Fixed Runtimeを一意に指定し、version検出と初期化へ同じfolderを渡す。欠落をsystem版への黙ったfallbackで隠さない。開発ビルドのEvergreen経路は維持し、その区別をbuild時だけに限定する。
9. 欠落するnative DLLは実測で特定し、許諾されたapp-local配布を最小限に検討する。開発PCのSystem32を丸ごとコピーせず、OS側の保護を無効化しない。
10. 更新は終了後の手動EXE差替えとする。自動更新、installer、rollback、uninstaller、サービス、汎用provider、独自extractor、別の業務pipelineを先行実装しない。

## 成立性gateとpublish設定

採用SDKは `Microsoft.WindowsAppSDK 2.4.0` を維持する。同packageが参照するBase 2.0.4の `Microsoft.WindowsAppSDK.SingleFile.targets` と公式の専用配布資料を根拠に、まずOSL-003で実アプリを検証する。

- `WindowsPackageType=None`、`RuntimeIdentifier=win-x64`
- `SelfContained=true`、`WindowsAppSDKSelfContained=true`
- `PublishSingleFile=true`、`IncludeAllContentForSelfExtract=true`
- `EnableMsixTooling=true`（必要なPRI生成のため。MSIX出力・インストールを行う指定ではない）
- 必要なUndockedRegFreeWinRT自動初期化とSDKの設定検査を維持する。
- trimming、Native AOT、先行した圧縮最適化は行わない。

既存[MSIX記録](../../../packaging/HubRadio.Package/README.md)には通常publishでXAML/PRIが欠けた記録がある。専用単一EXE資料と一般self-contained資料にも記述の不一致がある。このため、上記設定の存在だけで成功としない。

OSL-003ではXAML/PRI、Web asset、実展開先、BaseDirectory、初期化を隔離したworktree・データ領域で測る。小PoCでsystem WebView2を使用した場合は明示し、完全payload・offline・clean OS受入とは区別する。成立しなければそこで後続製品実装を停止し、失敗の根拠と最小修正/代替案を提示する。独自ランチャーやscript方式へ無断で切り替えない。

## 配置契約

展開されたasset rootに対して、次を配置する。ModelManagerへ渡すbundle rootは `ModelCatalog/` とし、catalog内のrelativePathを変更しない。

| 相対配置 | 内容 |
|---|---|
| root | 正規のアプリ/.NET/Windows App SDK出力と必要なXAML/PRI/native資産 |
| `WebAssets/` | 既存production静的assetとasset manifest |
| `Profiles/`、`DefaultSettings/Profiles/` | 既存profile原本 |
| `Worker/` | 検証済みCPython runtime、固定wheel、worker、runtime manifest |
| `Tools/` | 固定FFmpeg/ffprobe。既存EXE基準参照と一致させる |
| `ModelCatalog/catalog.json` | 既存pinned catalogをbyte不変で保持 |
| `ModelCatalog/<bundle.relativePath>/` | 採用ASRの4ファイル |
| `WebView2Runtime/` | 固定・検証済みx64 Runtimeの全必要内容 |
| `Licenses/`、`Evidence/` | 実際の同梱物に対応するnotice/component manifest |

SDKの実展開に合わせてこの契約を確認する。元媒体、`Music/`、案件DB、秘密鍵、test-only wheel、開発venv/SDK/Node.js、不要なsymbol/source mapは配布しない。`Music/`不在は既存要求に従いBGMなしとし、新しい音源を取得しない。

既存[Python組立](../../../packaging/python/build-runtime.ps1)と[検証](../../../packaging/python/verify-runtime.ps1)、FFmpeg/model/Web manifestを再利用する。catalogのhashや`releaseApproved`を起動対応のために一括変更しない。

## SBOM・許諾・更新責任

候補EXEを作るOSL-012が、既存buildで生成できるSPDX/CycloneDX等の標準形式で候補SBOMを生成し、tool/schema版と未解析範囲を記録する。独自の一覧形式を完全なSBOMと呼ばない。OSL-013の内容照合後、OSL-017でQ-010のnotice/SBOM/CVE/再配布条件と対応付ける。

Fixed WebView2は自動更新されないため、配布担当者が更新版を固定・再検証して配布する。標準再配布パッケージを使わないnative依存にも更新責任がある。新たな自動更新機構は作らない。

初回/再起動時間、可視UIまでの時間、配布・展開・model cacheのピーク容量、CPU/RAM最小値は実測後に確定する。既存MSIXの約1.22 GBという過去値を、新EXEのサイズや必要空き容量とみなさない。

## 技術評価と一般提供

- OSL-019で評価許諾が成立するローカル技術候補を固定し、OSL-020で実際の署名状態・OS・policyを記録して起動を検証する。
- 署名主体・実配布先は未選定。これだけを理由に許諾済みローカル検証・文書化を一律に停止しない。
- 保護に拒否された試験、clean OS不在、許諾未確認はBLOCKEDであり、保護解除や推測でPASSにしない。
- 一般提供には別分岐OSL-021が必要。信頼される署名手段と承認された配布先を選定し、署名後の同一EXEで起動と実取得経路の受入を再実行する。外側EXEの署名だけで全child binaryの許可を仮定しない。
- 証明書の購入・作成、公開release、クラウド配布先作成、受信者への証明書導入を無断で行わない。未選定時はローカル技術評価用に留める。
- 起動受入を既存 `V2-013/V2-014` の業務E2E、認識精度、字幕品質、全体提供の完了に読み替えない。

## 根拠資料

確認日: 2026-09-06。公式説明と製品上の選択を区別し、実測証拠は後続タスクが保存する。

- [WinUI unpackaged / single-file](https://learn.microsoft.com/windows/apps/package-and-deploy/unpackage-winui-app)
- [Windows App SDK project properties](https://learn.microsoft.com/windows/apps/package-and-deploy/project-properties)
- [.NET single-file deployment](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)
- [WebView2 Runtime distribution](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution)
- [WinRT CoreWebView2Environment](https://learn.microsoft.com/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2environment?view=webview2-winrt-1.0.4129.50)
- [SmartScreen reputation](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation)
- [Smart App Control](https://learn.microsoft.com/windows/apps/develop/smart-app-control/overview)
- [第三者受入方針](../licenses/third-party-intake.md)、[実現方式調査](../research/implementation-research.md)
