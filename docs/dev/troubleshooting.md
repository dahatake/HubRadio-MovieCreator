# 開発・Stage 0 トラブルシューティング

この文書は、現在のFoundation、Stage 1基盤、要求版0.2の一部UI、benchmark、固定fixture動画生成ツールを対象にします。source-only quick startと設定画面は実装済みですが、一般配布とend-to-end render／deliveryは未完了のため、主にtyped service境界を記載します。

最初に [開発を始める](getting-started.md) の前提条件を確認してください。

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

## 動画生成が失敗する

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
