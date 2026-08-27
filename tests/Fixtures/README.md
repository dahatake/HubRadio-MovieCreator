# Golden fixture 管理

このディレクトリは、F-007 の golden fixture 管理形式だけを保持します。元動画、生成動画、音声、frame、transcript、raw benchmark run は置きません。大型媒体と権利未確認媒体は Git に追加せず、`fixture-index.json` から repository-relative path で参照します。

## 管理ファイル

- `fixture-index.json`: private media、追跡済み control、期待値参照、欠落時の consumer contract を機械可読に記録します。
- `README.md`: 取得、生成、検証、欠落時動作、更新規則を説明します。

すべての path は repository root 基準で `/` 区切りです。絶対 path、資格情報、download URL は記録しません。

## Private media inventory

| ID | 種別 | local path | byte size | SHA-256 | 状態 |
|---|---|---|---:|---|---|
| `source-ios-20260615-024326000` | 権利未確認の原本 | `sample/20260615_024326000_iOS.MOV` | `6494572403` | `fe9c763fdf6fe7be4f368bfba926a1f3f2407ceaf044ec7d055e0869e65c1a9f` | expected-local / Git ignored |
| `proxy-ios-20260615-024326000-720p` | 原本から生成した開発用 proxy | `test/20260615_024326000_iOS.MOV` | `374146177` | `01f87fdc00dd4c4bcb73735b744dad52a3396a8242d1d40cd609de3a6dd31b95` | expected-local / Git ignored |

原本値の正本は `benchmarks/fixtures/fixture-manifest.json#/sourceAssets/0`、proxy 値の正本は `test/20260615_024326000_iOS.MOV.manifest.json#/output` です。上表と index の hash/size はこの既存値の転記であり、新しい測定値や推測値ではありません。

両媒体とも rights は `unconfirmed`、用途は `local-technical-evaluation-only`、外部公開は不可です。技術検証成功、ファイル所持、hash 一致のいずれも公開・再配布許可にはなりません。

## 原本の取得

1. 権限のある社内経路または rights holder から、索引と同一内容の private copy を取得します。この repository は取得先を保存しません。
2. `sample/20260615_024326000_iOS.MOV` へ配置します。
3. Git に stage せず、size と SHA-256 を `fixture-index.json` および全 `integrity.consistencyReferences` と照合します。
4. rights evidence が承認されるまでは local technical evaluation 以外へ使用しません。

別素材、再エンコード品、同名ファイルを代用品として扱ってはいけません。内容が変わる場合は既存 ID を上書きせず、新しい manifest と fixture ID をレビューして追加します。

## 開発用 proxy の生成

原本の integrity 検証後、repository root で次を実行します。

```powershell
pwsh.exe -NoLogo -NoProfile -File tools/create-test-video.ps1 `
  -SourcePath sample/20260615_024326000_iOS.MOV `
  -OutputPath test/20260615_024326000_iOS.MOV
```

このスクリプトは `benchmarks/results/ffmpeg-capabilities.json` の固定 FFmpeg 情報、`benchmarks/fixtures/fixture-manifest.json` の原本 hash/size、利用可能な NVENC 経路を検査し、出力と manifest を対で配置します。生成物は Git ignored です。byte-identical regeneration は保証されていないため、生成後の proxy が追跡済み manifest と異なる場合は自動で index を更新せず、差異を調査して新しい基準をレビューします。

テストや CI が原本または proxy を自動取得・自動生成してはいけません。生成は明示的なローカル setup 操作です。

## Integrity verification

媒体が存在する場合は、使用前に次を確認します。

1. `Get-Item -LiteralPath <relative-path>` の `Length` が index の `byteSize` と一致する。
2. `Get-FileHash -LiteralPath <relative-path> -Algorithm SHA256` を小文字化した値が index の `sha256` と一致する。
3. index の値が `integrity.authority` の JSON Pointer と一致する。
4. 原本は `integrity.consistencyReferences` の全 hash/size と一致する。
5. `git check-ignore -v --no-index <relative-path>` が意図した `.gitignore` 規則を返す。
6. proxy は `test/20260615_024326000_iOS.MOV.manifest.json#/validation/fullDecode/succeeded` が `true` の記録を持つ。ただしこの記録だけで現在のローカルファイルを検査済みとはみなさず、現在の hash/size を必ず照合する。

存在する媒体の hash または size が一致しない場合、その fixture に依存するテストは **fail** とし、`private-fixture-integrity-mismatch` を報告します。manifest や index を自動更新したり、不一致を skip に変えたりしてはいけません。

## Private fixture がない場合

通常の checkout と CI に private media がないことは許容されます。

- 欠落 fixture に依存する test case だけを **skip** し、理由 `private-fixture-unavailable` と不足 ID/path を表示します。
- skip を pass として黙って扱いません。
- repository、ネットワーク、別 fixture から代替媒体を自動取得しません。
- `trackedControlFixtures` だけで完結する test は継続します。
- fixture が存在するのに integrity が違う場合は「欠落」ではなく fail です。

この repository には現時点で共通 test runner 用の fixture loader はありません。今後の consumer は上記 contract を実装し、private fixture の有無を test 本体より前に判定してください。

## 追跡する小型 control

`fixture-index.json` の `trackedControlFixtures` には、作成時点で実在し Git 追跡されている次の小型ファイルだけを登録しています。

| Control | 役割 | 期待値の参照先 |
|---|---|---|
| `benchmarks/fixtures/asr-control.json` | 日本語 ASR mechanism control | 同ファイルの `referenceText`、`properNouns`、`normalization`、`accuracyThresholdApproved` |
| `benchmarks/fixtures/alignment-control.json` | 分離 slot alignment control | 同ファイルの `construction`、`phrases`、`accuracyThresholdApproved` |
| `benchmarks/fixtures/local-text-model-control.json` | grounded ID / prompt-injection control | 同ファイルの `expected`、`generation`、`assessment` |
| `benchmarks/fixtures/local-text-model-output.schema.json` | 出力 JSON contract | 同ファイルの `required`、`properties`、`$defs` |
| `benchmarks/fixtures/manual-render-manifest.json` | trim/concat/crop/subtitle/audio/render control | 同ファイルの `renderProfiles`、`compositions`、mechanism、assessment |
| `benchmarks/fixtures/manual-render-subtitles.ass` | neutral subtitle burn-in control | `manual-render-manifest.json#/subtitle` |
| `benchmarks/fixtures/preview-fidelity-layout.json` | 共通 LayoutSpec/Cue control | 同ファイルの `profile`、`layoutSpec`、`cues`、`samples`、`comparison` |

control の生成音声、render、capture、transcript は追跡対象ではありません。control に `accuracyThresholdApproved: false` または production 未承認の記録がある場合、それを合格閾値へ読み替えてはいけません。

## 期待値と baseline の参照

期待値は index へ複製せず、`expectedValueReferences` と `expectationSets` の path + RFC 6901 JSON Pointer で参照します。

- control の明示的な期待値は、参照された control section を test input/contract として使用します。
- Stage 0 canonical は `configurationDisposition`、`stage0Implications`、`assessment` などの curated section だけを bounded observation として参照します。
- `recordedAtUtc`、host 固有値、run ID、全 JSON envelope の byte equality は golden expectation にしません。
- Stage 0 の単回観測、0件、model score、画像差、速度、精度未測定値を production threshold にしません。
- 参照先が削除・移動された場合は index validation を fail し、曖昧な fallback path を探しません。

## 更新規則

1. private media は追加・copy・rename せず、先に権利と用途を確認します。
2. hash/size は既存のレビュー済み manifest に記録された値だけを index へ転記します。
3. 期待値は control/canonical の path と JSON Pointer で追加し、同じ値を index に再定義しません。
4. 新しい tracked control は、ファイルが実在し Git 追跡されてから登録します。
5. index 更新時は JSON parse、全 tracked path、全 JSON Pointer、manifest hash/size 整合、private path の ignore、絶対 path/secret 不在を検証します。
6. rights が未確認のままなら status を改善して見せず、local-only と外部公開不可を維持します。
