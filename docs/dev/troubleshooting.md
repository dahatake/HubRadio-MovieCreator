# 開発・Stage 0 トラブルシューティング

この文書は、現在のFoundation、Stage 1基盤、要求版0.2の一部UI、benchmark、固定fixture動画生成ツールを対象にします。source-only quick startと設定画面は実装済みですが、一般配布とend-to-end render／deliveryは未完了のため、主にtyped service境界を記載します。

最初に [開発を始める](getting-started.md) の前提条件を確認してください。

## 限定ローカル技術評価候補を起動できない

この節は一般提供用の手順ではありません。承認されたローカル技術評価では、`artifacts/portable/candidate-review-06/App/HubRadio.App.exe` を直接起動します。`Contents/` は監査専用の展開内容であり、起動用フォルダーではありません。公開の配布URLやダウンロード手順はありません。

正常系は、Windows 11 Enterprise Insider Preview build 29648 x64 の本PC、非昇格利用者でのみ確認済みです。既存profileを保全し、専用の新しいSDK展開先を使ったcold相当起動でnative Readyまで44.023秒、同じ専用展開領域を再使用したwarm起動で5.525秒でした。coldはOS再起動後・空profile・offline・クリーンOSを意味せず、warm値も専用展開領域の再使用条件に限定されます。production-channelのクリーンWindows、別標準ユーザー、offline、異常系は未評価です。

起動時にSDKが実行依存を再生成可能な展開cacheへ置きますが、案件データはそこに保存しません。WebView2 profileはportable実装上の固定per-user pathであり、run単位の分離を主張しません。利用者データの実際の保存先は次のとおりです。

- `%LOCALAPPDATA%\HubRadio.MovieCreator\Workspace\hubradio.db` — 案件のSQLite DB
- `%LOCALAPPDATA%\HubRadio.MovieCreator\Workspace\backups` — DB backup
- `%LOCALAPPDATA%\HubRadio.MovieCreator\WebView2` — WebView2 profile

起動失敗への対処として、この領域を削除・改名したり、別のWebView2 Runtimeを環境変数で指定したりしません。候補EXEやSDK展開物の改変も行いません。

### `webview.runtime_unavailable`

**意味:** 同梱Fixed WebView2 Runtimeを検出できなかったため、Web UIの初期化を開始していません。portable候補はsystem Evergreen Runtimeへ黙って切り替えません。

**対処:** アプリの表示に従って候補を再取得し、承認済みの候補EXEと照合してから再起動します。評価担当者から候補を再取得できない場合は、診断コードだけを添えて報告します。path、例外詳細、案件内容、認証情報を共有しません。

### `webview.initialization_failed`

**意味:** Runtime検出後にWebView2の初期化に失敗しました。原因はこのコードだけでは特定しません。

**対処:** 一度アプリを終了してから再起動します。継続する場合は、利用者データを保持したまま、診断コードと評価環境（OS版、x64、非昇格）を管理者へ伝えます。Runtimeの追加導入、system Runtimeへの切替、profileやworkspaceの削除を回避策にしません。

## `PowerShell 7+ (Core) is required.`

**原因:** Windows PowerShell 5.1 で実行しています。

**対処:** `powershell.exe` ではなく、PowerShell 7 の `pwsh.exe -NoLogo -NoProfile` を使用します。

```powershell
pwsh.exe -NoLogo -NoProfile -Command '$PSVersionTable | Select-Object PSEdition, PSVersion'
```

`PSEdition` が `Core`、major version が 7 以上であることを確認します。

## `Fixture source does not exist`

**原因:** private fixture が `sample/` にありません。

**対処:** 利用許可を確認した原本を、次の場所へ配置します。

```text
sample/20260615_024326000_iOS.MOV
```

fixture を入手できない場合、実素材を必要とする stage を成功扱いにしません。

## `Fixture SHA-256 mismatch` または byte-size mismatch

**原因:** 別ファイル、変更済みファイル、または不完全な copy です。

**対処:** `benchmarks/fixtures/fixture-manifest.json` の size と SHA-256 を確認します。検査を無効化したり、期待 hash を実行結果に合わせて変更したりしません。

## `Approved FFmpeg package is not installed`

**原因:** 固定 WinGet package が見つかりません。

**対処:** [開発を始める](getting-started.md#4-固定-ffmpeg-build-を用意する) の package ID と version を確認します。

## FFmpeg／ffprobe の hash mismatch

**原因:** 別 variant、更新版、変更済み binary が選択されています。

**対処:** `benchmarks/results/ffmpeg-capabilities.json` の package、version、実行ファイル SHA-256 と照合します。別 build を使う場合は hash だけを書き換えず、license、build configuration、capability、runtime 検証をやり直します。

## `h264_nvenc` を利用できない

**原因:** 現行の `tools/create-test-video.ps1` は NVENC 固定で、対応 NVIDIA GPU、driver、encoder capability を必要とします。

**対処:** `ffmpeg-capabilities` と GPU driver を確認します。現行 tool に encoder fallback はありません。別 encoder へ黙って切り替えず、新しい測定と profile 検証を行います。

## Python 3.12 が見つからない

**原因:** 製品workerの`build.ps1`が`py.exe -3.12`でWindows x64版CPython 3.12を選択できないか、`benchmarks/setup-python-env.ps1`の既定pathにStage 0用Python 3.12がありません。

**対処:** Windows x64版CPython 3.12を用意します。Stage 0 benchmarkだけを再実行する場合は、実在するPython 3.12の絶対pathを`-PythonExecutable`に指定できます。別implementation、32-bit、別major/minor versionを無理に使用しません。

```powershell
pwsh.exe -NoLogo -NoProfile -File benchmarks\setup-python-env.ps1 -PythonExecutable C:\path\to\python.exe
```

## pinned package の設定に失敗する

**原因:** 製品workerではlockと取得artifactのSHA-256不一致、sdistしかないplatform、Python/architecture不一致、network/package source、既存環境の不整合が考えられます。Stage 0 benchmark環境では別の候補package設定が原因になり得ます。表示された事実を確認する前に原因を断定しません。

**対処:** 製品workerでは`requirements.lock`、`requirements-test.lock`、`wheel-matrix.json`を個別に書き換えず、Python implementation/version/architecture、失敗したwheel名、期待hash、exit codeを確認します。新versionへ更新する場合は29-wheel closureをfresh venvで再解決・再検証します。Stage 0 benchmarkだけは、必要な場合に`-Recreate`で`benchmarks/.venv`を作り直します。`-Recreate`は指定した環境directoryを削除するため、製品workerや別用途の環境を指定しないでください。

## benchmark が timeout する

**原因:** 全編 decode、ASR、レンダーなどが指定した安全上限を超えました。

**対処:** 次を確認します。

- 残存する FFmpeg／ffprobe／Python process がないこと
- CPU、GPU、memory、空き容量
- 対象 stage の想定処理時間
- `benchmarks/results/runs/` に保存された失敗 JSON

根拠がある場合だけ `-ChildTimeoutSeconds` を増やします。許容範囲は 60〜86400 秒です。

## benchmark が失敗したが terminal 情報が足りない

ハーネスは、秘匿済み error code／message を持つ failed JSON を `benchmarks/results/runs/` へ保存してから non-zero で終了します。raw run と、同じ stage の canonical summary を確認します。

絶対 path、transcript、raw metadata、認証情報を issue や文書へ転載しません。

## 「編集を開始」で「操作を完了できませんでした」と出て先へ進めない

元動画画面で「編集を開始」を押したときのエラーです。`project.runtime_unavailable` の場合、表示文に原因と対処が書かれています（FR-036）。

アプリは、押された時点で実行基盤の初期化と復旧を自動で再確認・再実行し、回復すれば続けて案件を作成します。回復できないときだけ、次のいずれかの原因が表示されます。

| 表示される原因 | 確認と対処 |
| --- | --- |
| 動画処理ツール／動画解析ツール／文字起こしワーカー／文字起こしモデル／字幕用フォント／ビルド証跡が見つからない、または検証に失敗した | アプリの実行フォルダーに `Tools\ffmpeg.exe`、`Tools\ffprobe.exe`、`Worker\python.exe`、`ModelCatalog\catalog.json`、`Fonts`、`Evidence` があるか確認します。**Debug 構成の `bin\Debug` には含まれません。** 配布パッケージ（ポータブル版）を使うか、`-p:HubRadioRuntimePayloadPath=<準備済み payload>` を指定して Release 構成でビルドし直します（payload の準備は `packaging/portable/prepare-payload.ps1`）。ファイルを補ったら、アプリを閉じずに「もう一度試す（自動で再確認）」を押します。 |
| 編集の準備（初期化または前回の処理の復旧）が完了していない | しばらく待って「もう一度試す」を押します。直らなければアプリを再起動します。 |

ファイルは自動では取得・複製されません（検証済みの配布物だけを使うため）。

## 動画生成が失敗する

「自動編集」画面の「エラーの内容」に、失敗codeごとの原因と対応策が出ます（FR-047、固定文は`src/HubRadio.Web/src/features/workflow/pipelineFailureGuide.ts`）。

- `source_only.unsupported_source_timing`: 元動画が、映像1本・音声1本、開始0秒、固定フレームレート、フレームの連続、の条件を満たしません。音声が映像より0.25秒（`SourceAudioTailPolicy.MaximumShortfall`）以内で早く終わる場合は失敗にせず、末尾を無音で埋めます。ffprobe（`-show_entries stream=codec_type,r_frame_rate,avg_frame_rate,start_time,duration`）で各streamの開始と長さを確認し、条件を満たすMP4に書き出し直します。

`test/<元ファイル名から拡張子を除いた名前>-problems.md` を確認します。

- staging は終了時に清掃されます。
- output と manifest は pair で昇格します。
- 昇格失敗時は既存 pair を復元します。
- 問題レポートは既存の履歴を消さず追記します。

問題レポートの候補原因を、確認済みの根本原因と読み替えないでください。

## backup restore が `ReconciliationRequired=true` で失敗する

`.hubradio.restore-*` や `.hubradio.previous-*` をExplorerから直接削除・改名しません。

- `RestoreValidationFailed` で現行workspaceが残っている場合は、`ListIncompleteRestoresAsync`で隔離領域を列挙し、利用者が破棄を選んだ対象だけを`DiscardIncompleteRestoreAsync`へ渡します。
- `RollbackFailed` の場合は、`ListIncompleteCutoversAsync`で同一operation IDに結ばれた候補を列挙します。利用者が旧workspaceへの復帰を選んだ対象だけを`RollbackIncompleteCutoverAsync`へ渡します。
- `RestoredWorkspaceIsActive=true` は、active workspaceの検証に失敗し、旧workspaceが保持されている状態です。rollbackはactive側をrestore隔離名へ移してから旧workspaceを戻します。
- 候補が列挙できない、またはrollbackが再度失敗する場合は、自動削除やDB修復をせず、workspaceとbackup rootを保持して調査します。

成功したrestoreが保持する旧workspaceは、active workspaceが正常なら不完全cutoverとして列挙されません。暗号化、保持期間、削除UIは未完了の別gateです。

## 生成動画が `git status` に出ない

**原因:** 大容量・権利未確認 media の誤 commit を防ぐための正常動作です。

**対処:** 動画そのものは commit しません。manifest と、内容を秘匿した問題レポートだけをレビューします。

## `preview-fidelity` の新しい raw runがcanonicalと一致しない

**原因:** raw run の成功だけでは、そのrunをreviewed canonicalとして採用したことになりません。

**対処:** raw run を書き換えず、実測値、未測定範囲、privacy 境界を敵対的レビューします。採用する場合だけ `benchmarks/results/preview-fidelity.json` のrun ID/SHA-256と要約を更新し、古いrawは履歴証拠として残します。

## 調査時の注意

- media metadata の raw tag 値を terminal や文書へ出さない
- fixture、生成動画、private artifact を Git へ追加しない
- 失敗 run を削除・書換えしない
- 未確認の原因を根本原因として記載しない
- 警告や0件を品質の合格へ読み替えない

stage の証拠と判断方法は [Stage 0 ベンチマーク](benchmarks/README.md) を参照してください。
