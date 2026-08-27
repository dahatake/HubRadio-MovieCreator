# 開発を始める

この手順は、現在の Stage 0 技術検証を再現する開発者向けです。一般利用者向けの完成アプリケーションをインストールする手順ではありません。

> [!IMPORTANT]
> Foundation scaffoldは実装済みですが、編集機能は未実装です。現在利用できるのは、最小WinUI 3／React／Python workerのbuild・smoke testと、固定fixture向けStage 0検証ツールです。任意動画向けの一般的な編集CLIではありません。

Foundation全体の依存復元、静的解析、build、testはrepository rootで次を実行します。

```powershell
pwsh.exe -NoLogo -NoProfile -File .\test.ps1 -Configuration Release
```

## 前提条件

| 用途 | 条件 |
|---|---|
| OS | Windows 11 x64 |
| shell | PowerShell 7 以降（PSEdition Core） |
| source 管理 | Git |
| fixture | 利用許可を確認した固定動画 |
| media 検証 | WinGet の `BtbN.FFmpeg.LGPL.8.1` version `8.1.2-20260731` |
| ML 検証 | Python 3.12 と `benchmarks/setup-python-env.ps1` が作る隔離環境 |
| fixture 動画生成 | `h264_nvenc` を利用できる NVIDIA GPU と十分なローカル空き容量 |

ハードウェア最小要件は未確定です。記録済みの検証機は単一の実測環境であり、動作保証の最小構成ではありません。

## 1. PowerShell を確認する

```powershell
pwsh.exe -NoLogo -NoProfile -Command '$PSVersionTable | Select-Object PSEdition, PSVersion'
```

`PSEdition` が `Core`、major version が 7 以上であることを確認します。Windows PowerShell 5.1 の `powershell.exe` は使用しません。

## 2. リポジトリを取得する

```powershell
git clone https://github.com/dahatake/HubRadio-MovieCreator.git
Set-Location HubRadio-MovieCreator
```

以降のコマンドはリポジトリのルートで実行します。

## 3. 認可済み fixture を配置する

権利と利用目的を確認した次のファイルを配置します。

```text
sample/20260615_024326000_iOS.MOV
```

期待値は `benchmarks/fixtures/fixture-manifest.json` が正本です。

```powershell
pwsh.exe -NoLogo -NoProfile -Command '(Get-Item "sample\20260615_024326000_iOS.MOV").Length'
pwsh.exe -NoLogo -NoProfile -Command '(Get-FileHash "sample\20260615_024326000_iOS.MOV" -Algorithm SHA256).Hash.ToLowerInvariant()'
```

期待値:

- byte size: `6494572403`
- SHA-256: `fe9c763fdf6fe7be4f368bfba926a1f3f2407ceaf044ec7d055e0869e65c1a9f`

> [!CAUTION]
> fixture の権利状態は `unconfirmed` で、許可用途はローカル技術評価だけです。Git や外部サービスへ送信しないでください。別動画を同じ名前へ置いても、ハーネスの size／hash 検査が拒否します。

## 4. 固定 FFmpeg build を用意する

```powershell
winget install --id BtbN.FFmpeg.LGPL.8.1 --exact --version 8.1.2-20260731 --accept-package-agreements --accept-source-agreements
```

ハーネスは package 名だけでなく実行ファイルの SHA-256 も検査します。現在の canonical capability record は `benchmarks/results/ffmpeg-capabilities.json` です。

- `ffmpeg.exe`: `6099366f31293cdc6c283ea44ffb32f07e3139cd0caf6d0db652a7d064d089cb`
- `ffprobe.exe`: `4c2f730969c9551aec21c5ca07eb73f63bb0920204c9cd6c9a6e7be6be0458d2`

別 version や GPL build を使う場合は hash を直接書き換えず、ライセンス、build configuration、機能、実行結果を再評価します。

## 5. Python 検証環境を作る

Python を使う Stage 0 タスクを実行する場合だけ、固定環境を作ります。

```powershell
pwsh.exe -NoLogo -NoProfile -File benchmarks\setup-python-env.ps1
```

既定では `%LOCALAPPDATA%\Programs\Python\Python312\python.exe` を検査し、`benchmarks/.venv` を作成します。スクリプトは Python 3.12 と、直接利用する package の固定 version を検査します。完全な transitive hash lock は Stage 0 の採用技術決定後に行う計画であり、現在の環境を release lock とみなしません。

## 6. 小さい確認から実行する

ハードウェア情報を取得します。

```powershell
pwsh.exe -NoLogo -NoProfile -File benchmarks\run-benchmarks.ps1 -Stage hardware -ChildTimeoutSeconds 300
```

固定 FFmpeg の capability を取得します。

```powershell
pwsh.exe -NoLogo -NoProfile -File benchmarks\run-benchmarks.ps1 -Stage ffmpeg-capabilities -ChildTimeoutSeconds 300
```

現行ハーネスはすべての stage で fixture の不変性を検査するため、この2つにも fixture が必要です。

成功・失敗の machine-readable run は `benchmarks/results/runs/` に保存されます。このディレクトリはローカル証拠であり Git 対象外です。レビュー済みで非機密の要約だけを `benchmarks/results/*.json` として管理します。

## 7. 重い stage を実行する前に確認する

`media-ingest`、ASR、レンダーなどは全編 decode、GPU、数 GB の一時領域を使う場合があります。実行前に次を確認します。

- 対象タスクの依存と完了条件
- 十分な空き容量
- 固定した tool／model の hash
- fixture の権利とローカル限定条件
- 既存 raw run を上書きしないこと

例として、媒体取込検証は次のコマンドです。

```powershell
pwsh.exe -NoLogo -NoProfile -File benchmarks\run-benchmarks.ps1 -Stage media-ingest -ChildTimeoutSeconds 10800
```

対応 stage と現在の採否は、[詳細実装計画](planning/implementation-plan.md)、[ADR-0007](decisions/ADR-0007-stage-0-technology-selection.md)、`benchmarks/results/` の canonical 結果を確認してください。Stage 0の分類とfallbackは承認済みですが、production閾値、権利、再配布、release readinessは未承認です。

## 8. ローカル検証動画を生成する

必要な場合だけ、固定 fixture から H.264／AAC のローカル検証動画を作ります。

```powershell
pwsh.exe -NoLogo -NoProfile -File tools\create-test-video.ps1 -SourcePath sample\20260615_024326000_iOS.MOV -OutputPath test\20260615_024326000_iOS.MOV -TimeoutSeconds 10800
```

- 生成動画は Git 対象外です。
- manifest と秘匿済み問題レポートだけをレビュー対象にします。
- source global metadata はコピーしませんが、muxer／codec の技術タグは残る場合があります。
- 出力成功は fixture の外部公開許可を意味しません。

## 現在作らない設定

Azure は通常経路に含まれず、現在は resource、region、model deployment が未選択です。API key、`.env`、Azure 設定ファイルは作成しません。
