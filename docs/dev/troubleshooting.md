# Stage 0 トラブルシューティング

この文書は、現在の benchmark と固定 fixture 動画生成ツールを対象にします。製品アプリケーションは未実装のため、GUI の障害対応手順はまだありません。

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

**原因:** `benchmarks/setup-python-env.ps1` の既定 path に Python 3.12 がありません。

**対処:** Python 3.12 を用意するか、実在する Python 3.12 の絶対 path を `-PythonExecutable` に指定します。別 major／minor version を無理に使用しません。

```powershell
pwsh.exe -NoLogo -NoProfile -File benchmarks\setup-python-env.ps1 -PythonExecutable C:\path\to\python.exe
```

## pinned package の設定に失敗する

**原因:** network、package source、wheel 対応、既存環境の不整合などが考えられます。表示された事実を確認する前に原因を断定しません。

**対処:** Python version、失敗した package 名、exit code を確認します。必要な場合は `-Recreate` で `benchmarks/.venv` を作り直します。`-Recreate` は指定した環境 directory を削除して再作成するため、別用途の環境を指定しないでください。

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
