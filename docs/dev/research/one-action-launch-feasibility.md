# OSL-003: 単一EXEの小規模成立性検証

- 実施日: 2026-09-06
- 判定: **技術PoC成立。独立した敵対的レビューPASS（必須修正なし）**
- 対象: [ADR-0010](../decisions/ADR-0010-one-action-portable-launch.md)のSDK標準single-file方式。全payload・クリーンOS・一般提供の合格ではない。
- source基準: `49c5a49ea4b9c0d38841b212f8dea52529bdf945`、detachedな別worktree。実ユーザーの案件領域は使用しない。

## 環境と一時変更

| 項目 | 実際の条件 |
|---|---|
| OS | Windows 11 Enterprise Insider Preview、10.0.29648、x64 |
| build shell / SDK | PowerShell Core 7.6.5 / .NET SDK 10.0.400 |
| Windows App SDK | 2.4.0、Base 2.0.4のSingleFile targets |
| WebView2 | hostのEvergreen 152.0.4191.66。Fixed Runtimeはまだ使用していない |
| 署名 | `NotSigned`（実出力EXEで確認） |
| 起動データ | 検証用directoryをMainWindowへ明示指定する一時probe変更。実ユーザーのLOCALAPPDATAを参照しない |
| 展開先 | test childだけに `DOTNET_BUNDLE_EXTRACT_BASE_DIR` を指定した隔離directory。OS標準のTEMP既定配置の受入は未実施 |
| cwd | EXE配置先と異なる検証データdirectory |
| 未実施 | clean production OS、標準ユーザーtokenの確認、offline隔離、実取得/MOTW、CPU基盤、picker/設定/bridgeの機能受入、知覚評価 |

一時変更は、隔離worktreeのMainWindowでデータrootを検証用環境変数から必須入力とし、BaseDirectoryと資産存在、StartupCoordinatorの結果をローカルJSONへ記録するものだけ。本文や秘密は記録しない。この環境変数とprobe記録を製品へ導入する計画ではなく、利用者へ環境設定を要求する方式でもない。

## 再現した操作

1. source基準を別worktreeへ取得し、既存lockからWeb依存を復元。typecheck、production dependency検査、Vite build、dist検査が成功した。Viteの既存chunk-size警告は発生したが、今回UI分割は行わない。
2. 同じSDKでAppをrestoreし、Release / win-x64 / unpackaged / self-containedをpublishした。
3. 使用した追加propertyは `SelfContained=true`、`WindowsAppSDKSelfContained=true`、`WindowsPackageType=None`、`PublishSingleFile=true`、`EnableMsixTooling=true`、`IncludeAllContentForSelfExtract=true`、`WindowsAppSdkUndockedRegFreeWinRTInitialize=true`、`PublishTrimmed=false`、`DebugType=None`。既存SDK版・製品コードの業務処理は変更しない。
4. 新しい空の展開領域とデータ領域で、EXEを明示pathからshellなしで起動した。probe-result通知またはprocess終了をboundedなevent waitで待ち、実windowのUI Automationを確認した。
5. 同一EXE・同一展開cacheで再起動した。2回目のWebView/案件データ領域は新規であり、既存profileを保持した完全なwarm条件ではない。展開tree前後の全file hashと通常終了を確認した。

## 実測結果

| 観測 | 初回 run-01 | cache再利用 run-02 |
|---|---|---|
| publish | exit 0、出力rootのfileは `HubRadio.App.exe` 1個 | 同じEXEを使用 |
| EXE byte数 | 238,282,100 | 同一 |
| EXE SHA-256 | `96235f9288a42ee8c1ce2991f819dc5756357ba6e7a44ebbb9ea6b3f2351f5a4` | 同一 |
| main window | 検出 | 検出 |
| UI Automation | `BundledWebApplication` / `Bundled Web UI ready` / 可視 | 同一 |
| StartupCoordinator | Ready / Evergreen 152.0.4191.66 | Ready / 同一版 |
| process起動直前〜probe-result通知の経過 | 25,421 ms | 4,936 ms |
| 通常終了code | 初回ハーネスに記録項目なし。未測定 | 0、CloseMainWindow成功、10秒以内の終了確認成功 |
| 配布EXEの前後hash | 不変 | 不変 |
| 展開file数・合計byte数 | 536 / 227,563,429 | 同一 |
| 展開済み全fileの前後hash比較 | 比較対象となる事前treeなし | 536件、不変 |
| AppContext.BaseDirectory | 実展開先の `HubRadio.App/<bundle-id>/` | 同じ実展開先 |
| `HubRadio.App.pri` | 存在、2,187,768 bytes | 存在 |
| 単独 `App.xbf` / `MainWindow.xbf` | なし | なし |
| `WebAssets/asset-manifest.json` | 存在 | 存在 |

上記経過時間はprobe通知までの計測であり、最初の可視UIまで、OS file cache cold、全payloadの展開性能、一般利用者の操作時間を測った値ではない。性能合格閾値も未合意である。

## 判定と本実装への反映

- 固定SDKの標準single-file設定で、実MainWindow、WebViewHostのstartup、既存Web UI Readyが成立した。独自ランチャーやSDK更新は不要という範囲の根拠になる。
- **旧通常publishのXBF欠落記録を、このsingle-file出力の不具合へ読み替えない。** PRIは存在し、単独XBFがなくても実際のXAML画面が動作した。後続検証は正規のsingle-file資源出力と実起動を確認し、従来の単独XBF名を無条件に必須にしない。PRI内部の完全なresource解析は今回行っていない。
- BaseDirectory基準で既存Web asset/profileを使う構成は、少なくともこの小PoCで動作した。後続OSL-009〜013でTools、Worker、ModelCatalog、Fixed WebView2を同じ展開rootへ正しく含める必要がある。
- 新しいpublish profile/targetsの本実装はまだ未着手。probe用のデータroot差替え・計測コードは製品へ取り込まない。
- 小PoCにPython、FFmpeg、ASR weight、Fixed WebView2のpayloadは含めていない。238 MBを最終配布容量として案内しない。
- OSL-004/020/021の受入やAC-007全体は未実施。製品の実render/delivery統合や既存V2の完了とも無関係である。

## ローカル証拠

証拠はGit管理外の `artifacts/portable-spike/osl003/` に保存した。別worktreeのpublish EXE・展開treeもローカルに保全し、一般配布していない。

| file | bytes | SHA-256 |
|---|---:|---|
| `publish-01.log` | 455 | `ed82122cab63601f56fccc1f5979d21ec17e08f72b291643047fc32ae2ec32f8` |
| `run-01-result.json` | 875 | `38a9ba1d5593ca04738d1ebaed1c7a86ee6f1b6c1e4047d61f80706e189dd3f5` |
| `run-02-result.json` | 1004 | `d619f249666d485bcc5bf477fa9e7b31bd18265c6693ee41e323b4b050559052` |
| `probe-source.diff` | 2609 | `ef6893e732d6e04b847ded5fdbdea840f26eb24e141427c71d712a173d92ef52` |
| `Invoke-StartupProbe.ps1`（再起動時の版） | 6317 | `f14703ed9a46938f9db61e58e5d21979ca45eb08ba365dca8eaaa17b2dda6939` |

初回ハーネスは終了code・事前tree比較の記録を持たず、その不足を2回目の測定で補った。初回の未測定項目を遡及的に成功へ書き換えていない。完全な供給網・release再現性の証拠ではなく、この限定PoCの追跡用記録である。

レビューでは2回の結果・ログ・一時差分・再起動ハーネスを照合した。保存ハーネスはrun-02版だけであり、publishの引数・終了値とEXE/PRIの個別サイズは当日のterminal観測と本文に記録したが、上の5ファイルだけからは独立再現できない。この証拠の留保も維持する。

## 出典

- [現行App project](../../../src/HubRadio.App/HubRadio.App.csproj)、[既存MainWindow](../../../src/HubRadio.App/MainWindow.xaml.cs)、[旧MSIXの測定記録](../../../packaging/HubRadio.Package/README.md)
- [Microsoftのsingle-file WinUI設定](https://learn.microsoft.com/windows/apps/package-and-deploy/unpackage-winui-app#single-file-exe)
- [Windows App SDK properties](https://learn.microsoft.com/windows/apps/package-and-deploy/project-properties)
- [.NET single-fileの展開・API・bundle契約](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)
