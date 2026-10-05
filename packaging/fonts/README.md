# フォントとアプリ／Worker のビルド識別契約

この変更は2026-09-09の保存済み検証証跡を反映する文書更新であり、バージョン更新・法務承認・配布承認ではない。修正済み Worker の実ビルド／検証と新しい準備 payload の作成は完了しているが、この payload を使用した App publish は未実行、App E2E は QC が未完了。`releaseApproved` は `false` のまま。この文書更新ではコマンド／ビルド／テスト・実機受入・全ファイル再ハッシュを実行していない。UX041／UX043 は対象外。

## 検証済みの固定フォント入力

公式 `notofonts/noto-cjk` の tag `Sans2.004`、固定ソース commit `523d033d6cb47f4a80c58a35753646f5c3608a78` の原本を使用する。保存先は `artifacts/inputs/fonts/verified-2.004-7388bda58dd8/`、検証証跡は同ディレクトリの `verification.json`。以下は保存済み証跡に記録された raw-file の実測値であり、main 側の `font-manifest.json` に反映済み。この文書更新では再取得・ハッシュ再計算・sfnt 再解析は行っていない。

- フォント: `Sans/SubsetOTF/JP/NotoSansJP-Regular.otf`。保存ファイル名は `NotoSansJP-Regular.otf`。`byteSize: 4533028`、SHA-256 `dff723ba59d57d136764a04b9b2d03205544f7cd785a711442d6d2d085ac5073`、Git blob SHA-1 `4af556abb9cc42e585aab777b01857246a110746`。
- ライセンス: 同じ commit の **リポジトリルート `LICENSE`**（`Sans/LICENSE` ではない）。保存ファイル名は `LICENSE`。`byteSize: 4301`、SHA-256 `6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2`、Git blob SHA-1 `d952d62c065f3f35fb83a173496e90b21525aef3`。原本本文は SIL Open Font License 1.1（`OFL-1.1`）を含み、フォントとともに保持・同梱する。
- sfnt name table の記録: family `Noto Sans JP`、style `Regular`、version `Version 2.004;hotconv 1.0.118;makeotfexe 2.5.65603`、PostScript name `NotoSansJP-Regular`。入力契約の `version` は `2.004`。起動時の family/style/PostScript name 照合とは区別する。
- 未設定・欠損・不一致は fail-closed。Windows フォントや PATH の探索、ネットワーク取得、自己計算 SHA による暗黙許可は行わない。

入力マニフェストの全フィールドは `schemaVersion` (整数1)、`family`、`style`、`version`、`sourceCommit`、`sourcePath`、`relativePath`、`byteSize`、`sha256`、`license`、`releaseApproved`。
`license` は `relativePath`、`sourcePath`、`spdxId`、`byteSize`、`sha256`。追加フィールドはランタイムが拒否する。

## 現在の準備済み入力と証跡（2026-09-09）

- 現行 Worker は `artifacts/python/WorkerRuntime-Live-20260909c`。first-party 7ファイル／65,381 bytes、runtime 2,696ファイル／268,462,606 bytes。集合 hash と実 build／verify ログは [Python runtime README](../python/README.md) に記載する。vendor ピンは変更せず、first-party の一時出力名短縮による修正を含む。旧2回ビルド一致の証跡は現行 runtime の再現性証明へ流用しない。
- 新しい準備 payload は `artifacts/portable/live-payload-20260909c`。[完了 receipt](../../artifacts/validation/observed-payload-20260909T141021801Z-cf864da8.json) は `2026-09-09T14:25:15.4099645Z` に終了コード **0**、`watchdogStopped: false` を記録する。[出力ログ](../../artifacts/validation/observed-payload-20260909T141021801Z-cf864da8.stdout.log) と同 payload の `Evidence/portable-payload.json` は **3,266ファイル／1,823,960,675 bytes**、集合 SHA-256 `e047e26b90149da332c07387e36ee86a847c2c62eb5939b96ec41810e9004580` を記録する。
- この payload の `Fonts/NotoSansJP-Regular.otf`、`Fonts/LICENSE`、`Licenses/NotoSansJP-OFL.txt` の記録値は上記の固定原本／現行入力契約と一致する。ライセンスのソースは引き続きリポジトリルート `LICENSE`。現在の準備 package に対する整合性・read-only・ライセンス同梱の証跡であり、法務承認を意味しない。
- `appPublishRequired: true`、`releaseApproved: false`。準備 payload は App publish 済み候補ではなく、App E2E／QC・最終配布物の受入も証明しない。保存済み証跡は ignored `artifacts/` 内にあり、fresh checkout には含まれない。

## ビルド順序と配布レイアウト

1. 現在のソースと固定 CPython／hash-locked wheels からの runtime 再作成・検証は上記 `WorkerRuntime-Live-20260909c` で完了。旧 `uiux-payload-current` や `WorkerRuntime-Live-20260909b` の Worker は現行入力として流用しない。以後ソースを変更した場合は別の新規出力へ再作成・検証する。
2. `packaging/python/runtime-manifest.json` の `workerSource.{files,fileCount,totalBytes,artifactSetSha256}` と `output.{expectedFileCount,expectedTotalBytes,expectedArtifactSetSha256}` は今回の実測値へ更新済み。以後の変更も実測に基づき更新し、`runtime` と `wheelInput` の vendor ピンを自動更新しない。
3. `Worker/runtime-files.json` は生成物。`schemaVersion: "1.0.0"`、`runtimeIdentity`、`runtimeVersion`、`architecture`、`sourceManifestSha256`、`fileCount`、`totalBytes`、`artifactSetSha256`、`files`（各 `path,byteSize,sha256`）を持つ。`sourceManifestSha256` は上記入力マニフェストの **raw bytes** の SHA。Worker 集合順序は既存契約どおり en-US／case-sensitive。
4. フォント／ライセンス入力契約と上記保存原本を使用した新しい payload の作成は `live-payload-20260909c` で完了。再作成時の `prepare-payload.ps1` 必須引数は `-FontPath` と `-FontLicensePath`。入力検証と payload 作成の完了証跡は区別し、既存出力を上書きしない。
5. 未完了の後続作業として、この payload を入力に App を再コンパイル・publish する。Python とフォントの入力マニフェストを DLL に埋め込み、起動時はその契約と出荷マニフェストを照合する。

準備済み payload は `Worker`, `Tools`, `ModelCatalog`, `Fonts`, `WebView2Runtime`, `Licenses`, `Evidence` の7ルート。
Fonts は `NotoSansJP-Regular.otf`, `LICENSE`, `font-manifest.json` の3ファイルだけ。ライセンスは `Licenses/NotoSansJP-OFL.txt` にも同梱し、入力マニフェストは `Evidence/font-manifest.json` にも収録する。

`Evidence/application-build.json` は **準備済み payload に含めない**。MSBuild がコンパイル済み `HubRadio.App.dll` から生成する。フィールドは `schemaVersion` (整数1)、`relativePath: "HubRadio.App.dll"`、`version` (assembly version、4要素)、`size` (整数 bytes)、`sha256`。最終 bundle EXE をハッシュしないため循環しない。single-file では実際の `FilesToBundle` の DLL を測定し、portable TSV 記録前に JSON を追加する。

この digest は **ビルド識別**であり、真正性や独立した配布承認ではない。Worker の生成集合も同じ区別を保つ。CPython `python.exe` は引き続き `104952` bytes / `4d6f5f81a4bca11191c4c7c6b43632694d0a4ce74e068619d8fdc161d469859a` に固定する。

準備 payload の集合との比較から外すのは `Evidence/portable-payload.json`（自己参照回避）と、ビルド生成の `Evidence/application-build.json` だけ。**両方とも最終 Contents・SBOM からは除外しない**。

## main 側の実行コマンド（この作業では未実行）

最新インストール済み Windows x64 PowerShell 7+ から、リポジトリルートを作業ディレクトリとして実行する。以下の `<...>` は実際の固定入力の絶対パスへ置換する。

現在の入力を参照する `<new-runtime>` は `artifacts/python/WorkerRuntime-Live-20260909c`、`<new-payload>` は `artifacts/portable/live-payload-20260909c` の絶対パスを指す。再作成用の `<new-runtime-under-artifacts/python>` と `<new-payload-under-artifacts/portable>`、publish 用の `<new-candidate>` は、それぞれ既存出力と異なる新規保存先とする。候補検証の Worker 期待値（`expectedWorker`）も現行入力マニフェストとこの修正済み runtime を基準にし、旧候補に含まれる Worker を基準にしない。以下は操作の参照例であり、この文書更新で実行したコマンドではない。

- Worker: `pwsh.exe -NoLogo -NoProfile -File packaging/python/build-runtime.ps1 -RuntimeArchivePath "<official-zip>" -WheelhousePath "<locked-wheels>" -BuildPythonPath "<build-python.exe>" -OutputPath "<new-runtime-under-artifacts/python>"`
- 準備: `pwsh.exe -NoLogo -NoProfile -File packaging/portable/prepare-payload.ps1 -PythonRuntimePath "<new-runtime>" -FfmpegPath "<ffmpeg.exe>" -FfprobePath "<ffprobe.exe>" -FfmpegLicensePath "<license>" -ModelBundlePath "<model-bundle>" -WebView2RuntimePath "<fixed-runtime>" -WebView2LicensePath "<license-snapshot>" -FontPath "artifacts/inputs/fonts/verified-2.004-7388bda58dd8/NotoSansJP-Regular.otf" -FontLicensePath "artifacts/inputs/fonts/verified-2.004-7388bda58dd8/LICENSE" -OutputPath "<new-payload-under-artifacts/portable>"`
- データのみ検証: `pwsh.exe -NoLogo -NoProfile -File packaging/portable/verify-runtime-payload.ps1 -PayloadPath "<new-payload>"`
- 通常ビルド opt-in: `pwsh.exe -NoLogo -NoProfile -File scripts\build.ps1 -Configuration Release -RuntimePayloadPath "<new-payload>"`
- App のみ（Web dist/restore 済み）: `dotnet build src/HubRadio.App/HubRadio.App.csproj -c Release --no-restore -p:Platform=x64 -p:HubRadioRuntimePayloadPath="<new-payload>"`
- Portable: `pwsh.exe -NoLogo -NoProfile -File packaging/portable/build-portable.ps1 -DotnetPath "<dotnet.exe>" -SbomToolPath "<pinned-sbom-tool.exe>" -PayloadPath "<new-payload>" -Version "<existing-version>" -OutputPath "<new-candidate-under-artifacts/portable>"`
- 最終候補: `pwsh.exe -NoLogo -NoProfile -File packaging/portable/verify-portable.ps1 -CandidatePath "<new-candidate>"`

通常ビルドは `HubRadioPortablePayloadPath` を指定しても alias として利用可能。指定なしでは大容量 payload をコピーしない。指定時は `Worker/Tools/ModelCatalog/Fonts/Licenses/Evidence` を検証・コピーし、コピー後も欠損・余剰・変更ファイルを拒否する。起動時に利用者が環境変数を設定する必要はない。stale 出力は自動削除せず明示的にビルドを失敗させるため、clean な App 出力を使用する。

追加テストは `tests/HubRadio.App.Tests/SourceOnlyResourceManifestTests.cs` と `tests/Architecture.Tests/RuntimeResourceBuildContractTests.cs`。前者は合成マニフェストと一時ファイルの検証、後者は実行しないソース契約検査であり、実配布物の受入結果ではない。