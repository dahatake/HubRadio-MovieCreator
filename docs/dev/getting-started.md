# 開発を始める

この手順は、ソースからの開発・技術検証と既存Stage 0検証の再現を行う開発者向けです。一般利用者向けの完成アプリケーションをインストールする手順ではありません。

> [!IMPORTANT]
> **2026-09-10 JST現在:** 4タブ・設定UIに加え、必要なbackendと実offline ASR・生成・QC・自動代理評価／出力policy・set納品／再読込・再生成・再試行を接続済みです。UX-041／UX-043を除く**対象52単位は達成済み**で、全回帰・静的検査・資源結合・最終独立レビューを完了しました。任意動画向けの一般的な編集CLIや一般提供済み製品を意味しません。

画面を操作する方は[画面の使い方](../ui-workflow-guide.md)を参照してください。4タブ・設定操作と道具ごとの利用可能範囲を案内するもので、以下の開発環境構築を利用者へ求めるものではありません。

## 現在の実接続と検証範囲（2026-09-10追補）

- 現在の必須範囲は当初54単位からUX-041／UX-043を除く**52単位**で、必要なbackend実装・結合も承認済みです。旧A17のbackend待機条件は失効しています。native受入のUX-041／UX-043は明示除外であり、現在52単位の環境ブロッカーにしません。
- `V2-010/011`は実QC producer、評価record・state／出力policy、実納品証跡をApp/UIへ接続済みです。SQLite schema 12の字幕編集子版・journalはcommit後にACKし、再生成受付時に保存済み設定snapshotと字幕head／prefixを固定して投影します。保存だけでは動画を変更せず、旧版・旧成果物を保持します。
- 2026-09-09の初回全7生成成果物のset納品／再読込、再生成、model不在の実失敗からの再試行は成功しました。入力は**4秒の合成動画だけ**で、後述の原本`sample/20260615_024326000_iOS.MOV`は今回のE2Eに使用していません。全7件は当該runの生成集合であって全要求の出力種類ではなく、ASR精度・実素材品質の合格も意味しません。
- 別の手動字幕描画試験は、既知の合成`Machine`字幕を保存・fresh serviceでreplay・変更設定で投影し、実FFmpeg／機械QC、字幕ファイルと焼込画素差分を確認して成功しました。**実認識や納品E2Eではありません**。各再読込はstore／pipeline／service再作成で、native GUI再起動受入ではありません。検証は隔離workspace／DBで行い、実ユーザーprofile・既存DBを変更するnative起動はしていません。
- UX-035のmanual reframe／layout・keyframe等はlegacyのセッション内機能／理由付き利用不可の当初範囲に留め、source-onlyは字幕専用です。UX-050の旧承認テンプレートによる手動thumbnailの`Unavailable`も許容範囲です。標準自動thumbnailは実元フレームを使う社内限定・ニュートラル経路へ接続済みで、架空の`BrandApprover`は作りません。両タスクは52単位に残り、手動機能の汎用永続化を追加完了条件にはしません。

既存ログと現在判断は[詳細実装計画](planning/implementation-plan.md)と[品質記録](quality.md)に集約しています。全回帰は**6,926成功・35 skip・失敗0**、限定実統合4件は別実行で成功し、最終独立レビューは重大指摘なしで閉鎖済みです。runtime結合出力`artifacts/validation/app-runtime-20260909c`は警告0・エラー0でビルド完了し、全6 rootのコピーを照合済み。PRI修正はWinUIのXBF／PRIを無効化していません。最終C# formatも成功し、残る静的ブロッカーはありません。

App packageは**`0.1.1.0`**へ更新され、[CHANGELOGのUnreleased](../../CHANGELOG.md#unreleased)へ反映済みです。実行用Appの.NET assembly版`1.0.0.0`とは別管理です。版更新後の検査・最終機械監査は主作業の確認予定で、本同期では未実行です。`V2-014`全体、原本sample・人手・clean OS・署名・legal／release承認の完了は宣言せず、過去の54単位台帳・OSL候補の履歴と`releaseApproved=false`を維持します。

## 開発者ビルドと候補の技術評価を分ける

このページの SDK、Git、PowerShell 7、Node.js、Python、FFmpeg、および fixture の前提条件は、ソースから build・test・技術検証を行う**開発者**向けです。候補EXEの利用者に、これらの導入、環境変数設定、管理者昇格、または追加Runtimeの手動導入を求めるものではありません。

OS-onlyの1操作起動候補は、[portable distribution runbook](packaging/portable-distribution.md) と [ADR-0010](decisions/ADR-0010-one-action-portable-launch.md) を正本の導線とします。`candidate-review-06` はローカル技術評価だけの候補であり、一般配布用ではありません。実施済みの本PC正常系記録は [OSL-014 local-host verification](reviews/osl-014-local-host-verification.md)、候補の第三者依存・再配布条件の状態は [release-candidate review](licenses/release-review.md) を確認してください。clean OS受入はOSL-020の成果物が作成されるまで未成立です。後者が `BLOCKED / REVIEW_PENDING` である間は、一般配布または一般利用者向けの提供を行いません。

Foundation全体の依存復元、静的解析、build、testはrepository rootで次を実行します。

```powershell
pwsh.exe -NoLogo -NoProfile -File .\scripts\test.ps1 -Configuration Release
```

最新の作業treeをRelease buildしてAppを起動する場合は次を実行します。既に同じRelease出力から起動中の`HubRadio.App`は、build前に終了します。`-SkipBuild`で既存のRelease出力をそのまま起動し、`-Wait`でApp終了まで待ってその終了コードを返します。

```powershell
pwsh.exe -NoLogo -NoProfile -File .\scripts\run.ps1
```

build・test・起動用のscriptは`scripts/`にまとめています。

| script | 用途 |
|---|---|
| `scripts/build.ps1` | 依存復元、Web build、.NET build、静的解析 |
| `scripts/test.ps1` | `build.ps1`の後に.NET・Web・Python workerの全testを実行 |
| `scripts/verify.ps1` | 要求IDの追跡性検査の後に`test.ps1`を実行（CI） |
| `scripts/run.ps1` | Release buildの後に`HubRadio.App.exe`を起動 |

## 前提条件

| 用途 | 条件 |
|---|---|
| OS | Windows 11 x64 |
| shell | PowerShell 7 以降（PSEdition Core） |
| source 管理 | Git |
| fixture | 利用許可を確認した固定動画 |
| media 検証 | WinGet の `BtbN.FFmpeg.LGPL.8.1` version `8.1.2-20260731` |
| 製品worker | Windows x64版CPython 3.12。`scripts/build.ps1`がPK-001 hash lockから隔離環境を作る |
| Stage 0 ML検証 | Python 3.12 と `benchmarks/setup-python-env.ps1` が作る別の隔離環境 |
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

## 5. Python環境を作る

製品workerは`scripts/build.ps1`または`scripts/test.ps1`から構築する。`src/HubRadio.Worker/requirements.lock`と`requirements-test.lock`がWindows x64 / CPython 3.12用wheelをSHA-256で固定し、buildはbinary-only・hash必須で復元する。手動の`pip freeze`や`pip install -e .[test]`でlockを上書きしない。詳細は[Python依存lock](packaging/python-dependency-lock.md)を参照する。

Stage 0 benchmarkを再実行する場合だけ、製品workerとは別の固定環境を作る。

Python を使う Stage 0 タスクを実行する場合だけ、固定環境を作ります。

```powershell
pwsh.exe -NoLogo -NoProfile -File benchmarks\setup-python-env.ps1
```

既定では `%LOCALAPPDATA%\Programs\Python\Python312\python.exe` を検査し、`benchmarks/.venv` を作成します。これは過去のStage 0候補を再測定する環境であり、PK-001の製品worker lockやrelease packageではありません。heldまたはcondition-falseのbenchmark packageが存在しても、製品lockへ追加しません。

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

## 9. .NET／Web の全回帰を実行するときの運用（2026-09-23 追補）

- **全回帰は runner ごとに逐次実行します。** App・Infrastructure・Web を同時に実行すると、締切（deadline）に依存する試験が広範に時間切れします（2026-09-22〜23 に観測。Web は負荷時に `5000ms` の時間切れがファイル間を移動、Infrastructure の `MediaImportSecurityTests.NonterminatingTool_IsKilledAtDeadlineWithoutPathDisclosure` は全件実行時に失敗し、単独実行では成功）。詳細と実測は[品質管理](quality.md)の 2026-09-22〜23 の節を参照してください。
- 同じ期間に逐次実行した結果は、Infrastructure 3回すべて失敗0（`DOTNET_PROCESSOR_COUNT=1`）、App 3回すべて失敗0でした。
- **締切を一括で引き上げる対処は取りません。** 実際に遅くなった退行を見逃しやすくなるためです。
- App runner は `--progress off` を付け、出力をパイプで受けて実行します（例: `...\HubRadio.App.Tests.exe --progress off 2>&1 | ForEach-Object { $_ }`）。出力をファイルへリダイレクトした実行で終了コード `-1` の異常終了を3回観測した記録がありますが、2026-09-23 23:44以降の再試行（リダイレクト・`Tee-Object` のそれぞれで `--progress off` の有無を変えた4通り）では再現せず、**原因は未特定**です。
- サンドボックス内でビルドする場合は `dotnet build <project> -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false` を使います（プロセス間の名前付きパイプが拒否されると、複数ノードのビルドがエラー0件のまま失敗するため）。

## 現在作らない設定

Azure は通常の編集経路では既定無効です。[ADR-0008](decisions/ADR-0008-bounded-azure-evaluation.md)に記録された限定評価用resource・region・model deploymentは選定・作成済みですが、利用者向けcloud機能や実コンテンツ送信の完成を意味しません。今回のUI操作にAPI key、`.env`、新しいAzure設定ファイルは不要です。
