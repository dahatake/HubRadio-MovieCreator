# OSL-014 このPCでの慎重な再実行

- 実施日: 2026-09-06
- 利用者指示: Windows Sandboxではなく、このPCの環境で再度慎重に実行する。
- 判定: **本PCでの正常系実行はPASS**（native Ready、cold/warm、同梱Runtime、展開物照合・不変性、通常終了）。
- 対象: `artifacts/portable/candidate-review-06/App/HubRadio.App.exe`。製品コード、候補EXE、同梱内容は変更・再ビルドしていない。
- 対象EXE: 2,058,629,690 bytes / SHA-256 `159161e3426c566cac6ea43bf6faa6587a2c1e192af81339341f83aa340a092e`。
- 環境: Windows 11 Enterprise Insider Preview build 29648、x64、このPCの現在の利用者、非昇格実行、PowerShell 7.6.5。
- OSL-020のclean production Windows受入、OSL-021の署名・実配布は実施していない。OSL-021は利用者指示どおり今回スキップ。

## 保全と観測方法

1. 起動前に候補hash、既存HubRadioプロセス0件、対象profile使用中のWebView2プロセス0件、ambientのWebView2／bundle overrideなしを確認した。
2. 最初の事前確認では案件Workspace／DBは存在せず、既存profileは199ファイル・15,214,642 bytesだった。private証跡領域へコピーして全hash一致を確認してから起動した。
3. 最初の試行でアプリが通常の起動処理によりWorkspace／DBを作成した。後続試行ではこれを消さず、明示した先行runの続行として扱った。最終runのバックアップはDBを含む325ファイルで、再度hashを検証した。
4. SDKの`DOTNET_BUNDLE_EXTRACT_BASE_DIR`を対象子プロセスだけに設定し、短い専用TEMP領域へ新規展開した。warmは同じ領域を再使用。TEMPの既定ACL・Windows保護を変更していない。
5. 対象PIDが所有する可視`HubRadio MovieCreator`ウィンドウだけを列挙し、`BundledWebApplication`のName=`Bundled Web UI ready`かつ`IsOffscreen=false`を確認した。
6. 終了前にPID・開始時刻・実EXEを対応付けた子孫を記録し、同じPIDが所有するウィンドウへ`WM_CLOSE`を送った。記録した全子孫の終了後だけwarmへ進めた。無関係なEdge／WebView2を列挙名で停止していない。
7. privateのhost trace・標準出力／エラー・backupと、pathを含まない結果JSONを分離した。失敗runを上書きせず保持している。

今回のcoldは「新しいSDK展開先」の意味であり、OS file cacheを消した状態、OS再起動後、初期化前の利用者profileではない。ネットワークは遮断しておらず、offline・OS-onlyの成功とは呼ばない。

## 最終runの実測結果

Run ID: `host-20260906T131215-e5dee148`

| 観測 | cold | warm |
|---|---:|---:|
| 可視ウィンドウまで | 37,520 ms | 2,535 ms |
| native Readyまで | 44,023 ms | 5,525 ms |
| Ready要素の画面外判定 | false | false |
| 対象ウィンドウへの終了要求受付 | true | true |
| 通常終了／終了コード | true / 0 | true / 0 |
| 終了要求から終了確認まで | 449 ms | 480 ms |
| 強制終了 | なし | なし |
| 記録したWebView2子孫の終了 | 6/6 | 7/7 |
| 実Runtimeの同梱path・hash一致 | 全件一致 | 全件一致 |
| 展開物の候補manifest一致・終了前後不変 | 一致 | 一致 |

時間はプロセス生成直後に開始した観測ストップウォッチの値（UIAサンプリング間隔500 ms）。観測上限600秒・終了上限30秒は安全な試験期限であり、製品の性能合格閾値ではない。

- 実WebView2の版: `152.0.4191.62`。
- 実Runtime EXE SHA-256: `0f75ba7da899408d4c4474d58358bdbee732705fc090161cbf08e8783520572d`。
- 実展開物: **3,793 files / 2,047,587,645 bytes**。Contents 3,794件から、外側EXEのprefixとなる非bundle host入力1件を除いた集合と一致。
- 実展開集合SHA-256: `2d00adb209ab172031c890288ed08ec4ffe72ae4549c691acbaa32e934f668b3`。cold/warmの終了前後4記録が一致し、最終の独立再ハッシュでも不一致0件。
- 最終監査時点で対象アプリ0件、今回の専用展開先から動くWebView2も0件。子孫の終了記録だけでなく、専用pathによる再走査で残留なしを確認した。
- 候補EXEは前後hash一致。最終cold/warm前のbackupと現在の案件DBもhash一致。profileと今回作成されたDBは保持し、backupの自動復元・データ削除は行っていない。

## Readyの意味と未評価範囲

既存native Readyは、navigation成功と、mount後の自動`bridge.ping`へのnative応答post成功を待つ契約である。今回この契約を検査した。UIAの`NameProperty`で確認文言が見つからないことを、通信失敗とは断定しない。

以下は**NOT EVALUATED**のまま:

- JavaScript側での応答受領ACKの独立確認、L02の手動再確認、picker／案件作成／設定編集などの全業務操作。
- runtime欠落・有効な別Runtimeへのoverride・user-data overrideの負例再実行、disk full／アクセス拒否／cache除去などの破壊的負例。
- クリーンproduction OS、別の標準アカウント、offline、MOTW／署名／一般配布、ASR精度・動画編集E2E。

この正常系PASSは、上記を含むAC-007全項目・製品出荷承認や、全smokeスクリプトの汎用対応完了を意味しない。`releaseApproved=false`を維持する。

## 途中試行と原因の訂正

| Run | 観測結果 | 扱い |
|---|---|---|
| `host-20260906T121314-e9ce2b8f` | 可視ウィンドウ確認後、追加したJS文言待ちで600秒経過。集計で`PSArgumentException`発生 | ハーネス失敗。OrderedDictionaryのキーを`Measure-Object`のプロパティとして扱った誤りをPSCustomObject化で修正。起動・終了証拠の欠落を推測補完しない |
| `host-20260906T124350-e8c7d27e` | native Ready 52,037 ms、展開物一致、観測中339,663 msでexit 0。強制終了なし | 追加JS文言を成功条件にしたため待機し続けた。`ExitedBeforeReady`という保存ラベルはReady=trueと矛盾するため、そのラベルを製品障害の根拠にしない。終了契機は未特定 |
| `host-20260906T131215-e5dee148` | 既存native Ready契約でcold/warm、通常終了、Runtime、展開物がPASS | 本PC正常系の最終結果 |

旧`Test-WebViewStartup.ps1`は起動上限15秒で、今回のcold実測37.5秒（可視化）／44.0秒（Ready）より短い。さらにfinally内の終了例外が先行例外を隠すため、旧ログだけから「WebView child待ち」や「無関係なWebView2が多いため起動不能」とは結論できない。後者を原因としていたstatusの記述は訂正した。

## 証拠の所在

root: `artifacts/validation/osl014-host-careful-a1/`

| 証拠 | SHA-256 |
|---|---|
| 最終run `result.json` | `8034468fe685c813263476cc59c8b1f0b9cc89c82b12d1152f138dc240a79392` |
| 最終run `cold/result.json` | `553e760f434b7ab39f8c2808cdeb2caf07c9da30ebd7ce069fab6842a6356b23` |
| 最終run `warm/result.json` | `3fde59f7561ff2547809e23908ea93b6ba4d2683d8ddb9aa4686bcd4d222adaf` |
| `Run-HostCheck.ps1`（最終実行版） | `2b047403f8ed3fdd3ba13bf2ed35a683c602c29faf17f678d1695f2c5e943bde` |
| `WindowProbe.cs`（最終実行版） | `aae7eb7e968fe141903205977499db54b8823dd91277f12b8d01fc6bc9e96498` |

元の結果JSON・backup・traceはGit管理外のまま保全する。private原本にはユーザーpath/profileが含まれるため外部共有しない。独立レビューは上記の正常系範囲でPASS、記録後の子孫生成を見落とす余地は最終専用path再走査で補完した。