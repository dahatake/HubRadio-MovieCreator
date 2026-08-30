# 第三者ソフトウェア・モデル受入方針

- 状態: Accepted for Stage 0
- 決定日: 2026-08-27
- タスク: P-008

## 原則

第三者依存は、版、配布元、取得日、成果物SHA-256、ライセンス本文、モデルカード、利用条件を記録できたものだけを採用する。`latest`参照や無検証のモデル自動更新を本番で使わない。

## 既定判断

| 候補 | 用途 | Stage 0判断 | 出荷条件 |
|---|---|---|---|
| FFmpeg/ffprobe | probe、proxy、render、QC | winget `BtbN.FFmpeg.LGPL.8.1` version `8.1.2-20260731`を開発評価候補とする | winget manifestの期待archive hash `089e4169e93b2b3f3acbfced3c0704d24276a225641bdda04d796d28b07a2a38`とinstaller検証結果を記録し、展開後exeは別途実測hashを取る。`-version`、`-buildconf`、対応source、LGPL義務、codec特許を確認 |
| GPL FFmpeg build | 同上 | 不採用 | 法務と配布方針が明示変更されるまで禁止 |
| faster-whisper/CTranslate2 | OpenAI Whisper互換weightをCTranslate2で実行するローカルASR候補 | faster-whisper `v1.2.1`をPython 3.12隔離環境でPoC | faster-whisperとCTranslate2はいずれもMIT。package/modelのexact版、CUDA 12/cuDNN 9互換、weight hashを確認 |
| Whisper weights | ASR | PoC候補 | 配布元、モデル版、MIT条件、weight hashを確認 |
| Foundry Local Whisper/SLM | ローカルASR/文章候補 | 利用可能性と品質を比較 | model aliasだけでなく実variant/version、取得元、license、cacheを記録 |
| pyannote Community-1 | 話者分離 | **不同意のため未取得・未実行**。手動話者割当を既定とする | 利用条件同意、連絡先共有条件、CC-BY-4.0 attribution、weight hashが揃った場合のみ再検討 |
| PySceneDetect | shot境界 | 0.7系のexact版をPoC候補 | exact版、ライセンス、PyAV/OpenCV依存を記録 |
| MediaPipe Face Detector | 顔bbox | Windows amd64 CPU経路をPoC候補 | package/modelのexact版、Apache-2.0表示、model hashを確認 |
| TalkNet/SAM 2 | active speaker/追跡 | Stage 2まで導入しない | 実素材PoC、依存、weight license/hashが揃った場合のみ |
| DeepFilterNet | 音声補正 | Stage 2まで導入しない | A/B品質評価とlicense/hash確認 |
| フォント | 字幕・ブランド | 開発用にWindows標準フォントだけを使用 | 商用配布、埋込、動画焼込の許諾をブランド単位で確認 |
| BGM/ジングル/SFX | 音声素材 | 承認素材が未提供のため使用しない | RightsRecordと証憑がある素材だけ参照 |
| ロゴ/画像/資料 | 視覚素材 | 開発用neutral placeholderだけを使用 | 承認版、出典、用途、地域、期間を記録 |

## Stage 0導入候補

- Python 3.12
- BtbN FFmpeg `n8.1.2-34-g9b6c8969e0` win64 LGPL static build（開発PoC用）
- faster-whisper `v1.2.1`（CTranslate2 exact版はlock解決後に記録）
- PySceneDetect
- MediaPipe

上記は採用確定ではない。Windows x64 wheel、GPU/CPU実行、モデル取得、実素材精度、配布サイズを測定してからST-015で採否を決める。

## ST-006 ASR runtime・model受入記録

取得・確認日: 2026年8月27日。以下はWindows x64 / Python 3.12上のStage 0評価に実際に使用または同一versionとして再取得した成果物であり、最終配布物への採用記録ではない。wheelは構成済みMicrosoft package feed proxy経由でPyPI package identityを指定して取得した。

| 種別 | 固定対象 | 配布元 | byte size | SHA-256 | license・条件 |
|---|---|---|---:|---|---|
| Python wheel | `faster_whisper-1.2.1-py3-none-any.whl` | PyPI / https://github.com/SYSTRAN/faster-whisper/tree/v1.2.1 | 1,118,909 | `79a66ad50688c0b794dd501dc340a736992a6342f7f95e5811be60b5224a26a7` | METADATA classifierはMIT。wheelにlicense本文は同梱されないためexact tagのlicenseを別途固定 |
| Python wheel | `ctranslate2-4.8.1-cp312-cp312-win_amd64.whl` | PyPI / https://github.com/OpenNMT/CTranslate2/tree/v4.8.1 | 19,220,789 | `49f96e861b57301f0b76a082109bde2cac8204a6b4fedc870883008271e82251` | wheel METADATAとwheel内にlicense表記・本文を確認できなかったため、exact tagのMIT licenseを別途固定 |
| Python wheel | `huggingface_hub-1.28.0-py3-none-any.whl` | PyPI / https://github.com/huggingface/huggingface_hub/tree/v1.28.0 | 793,202 | `58a8bacb03072edfc38067065e9dc24bbb34805410fcd36a1632de0b329660bb` | METADATA classifierはApache Software License。同梱`licenses/LICENSE`は11,357 bytes、SHA-256 `c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4` |
| Python wheel | `tokenizers-0.23.1-cp310-abi3-win_amd64.whl` | PyPI / https://github.com/huggingface/tokenizers/tree/v0.23.1 | 2,798,223 | `e7bfaf995c1bdbbd21d13539decb6650967013759318627d85daeb7881af16b7` | METADATA classifierはApache Software License。wheelにlicense本文は同梱されないためexact tagのlicenseを別途固定 |
| ASR model | `Systran/faster-whisper-small` revision `536b0662742c02347bc0e980a01041f333bce120` | https://huggingface.co/Systran/faster-whisper-small/tree/536b0662742c02347bc0e980a01041f333bce120 | 486,212,372 | manifest `4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f` | exact revision README front matterは`license: mit`。4 model fileの個別hashは`benchmarks/results/asr-benchmark.json`の参照rawに記録 |
| Foundry Local Appx binary | CLI `0.10.3` / `foundry.exe` | installed `Microsoft.FoundryLocal` Appx | 10,967,040 | `53a805895debd297bc5fe424bdbf64f551efbe0c53ddae397ecae9f5bcc7db98` | preview。実機Appxの`SignatureKind`は`Developer`。製品配布の信頼性を示さない |
| Foundry Local Appx binary | CLI `0.10.3` / `foundrylocald.exe` | installed `Microsoft.FoundryLocal` Appx | 12,513,792 | `a49c23d4af9f470da7128c29eef3fb1c0a64cf8555c2a3500e8683855886f3d1` | local daemon。model/serverの実行前後状態一致をST-006 rawで確認 |
| Foundry Local model | `openai-whisper-small-cuda-gpu:3` | Foundry Local catalog/cache | 316,267,930 | manifest `003489877f5a82eccbaeb435e520fb1656780331f26ae7cb4f03bbb15f8f64f8` | catalogが`apache-2.0`と宣言したことだけを記録。独立した配布license審査は未完了 |
| Foundry CUDA execution provider | 14-file CUDA EP cache | Foundry Local dynamic EP cache | 2,423,764,787 | manifest `8248ba49910e8fba56ddc8b4810a55cc9b3a069ca0dc2f7a23cd5f1f9a807021` | CUDA 12.8.3、cuDNN 9、ONNX Runtime CUDAを含む。Foundry公式資料はNVIDIA SDK EULA適用を明記。CTranslate2子processでのlocal評価は成功したが、再配布承認は**false** |

license evidence:

- faster-whisper v1.2.1 `LICENSE`（MIT）: https://raw.githubusercontent.com/SYSTRAN/faster-whisper/v1.2.1/LICENSE — 1,064 bytes、SHA-256 `af6798135e729f8aa6c853936d037dfdea449734d26b8ea6a89805fca758c0d5`
- CTranslate2 v4.8.1 `LICENSE`（MIT）: https://raw.githubusercontent.com/OpenNMT/CTranslate2/v4.8.1/LICENSE — 1,115 bytes、SHA-256 `54aa79d9fe3c09e67a16dcd95b9e88676405a6ec174efda31036983cf7672ecb`
- huggingface_hub v1.28.0 `LICENSE`（Apache-2.0）: https://raw.githubusercontent.com/huggingface/huggingface_hub/v1.28.0/LICENSE — 11,357 bytes、SHA-256 `c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4`
- tokenizers v0.23.1 `LICENSE`（Apache-2.0）: https://raw.githubusercontent.com/huggingface/tokenizers/v0.23.1/LICENSE — 11,357 bytes、SHA-256 `c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4`
- faster-whisper-small exact revision README/model card: https://huggingface.co/Systran/faster-whisper-small/resolve/536b0662742c02347bc0e980a01041f333bce120/README.md — 1,998 bytes、SHA-256 `329373481008c7c38654aff8ecdcf0163c211557cc7ba8e2ef6f2f84b4f75ec8`、front matter `license: mit`
- Foundry Local CLI / execution provider reference: https://learn.microsoft.com/azure/foundry-local/reference/reference-cli — CLIはpreview、CUDA EPはNVIDIA SDK EULA対象と記載

Windows Japanese TTS controlはインストール済み`Microsoft Haruka Desktop`でローカル生成した機構確認用であり、音声またはvoice componentを製品へ再配布する判断ではない。PK-001は採用CPU workerのtransitive wheelをhash固定したが、Foundry CUDA cacheのcopy、Appx/modelの同梱、Python runtime、notice生成は未承認であり、PK-002/PK-004/Q-010まで出荷対象にしない。

## ST-011 Foundry Local文章model受入記録

取得・確認日: 2026年8月27日。以下は対象PCのFoundry Local cacheに既に存在し、ST-011のローカル技術評価へ実際に使用した成果物である。新規downloadは行っていない。catalogのlicense表示は記録したが、model配布物の独立したlicense本文・notice・再配布審査ではない。

| 種別 | 固定対象 | 配布元 | byte size | SHA-256 | license・条件 |
|---|---|---|---:|---|---|
| Foundry Local chat model | `qwen3-0.6b-cuda-gpu:2` / cache `v2` | Foundry Local catalog/cache、publisher表示 `Microsoft` | 516,135,598 | 8-file manifest `38dc1ac88c6af4a684f96e8a9b4c30a6bca2671115a84e15189d93d57a10ebbe` | catalogは`apache-2.0`、`reasoning,tool-calling`を宣言。独立配布license reviewは未完了。ST-011ではcontrol/fixture全4入力でaccepted JSON 0件 |
| Foundry Local chat model | `qwen3-4b-cuda-gpu:2` / cache `v2` | Foundry Local catalog/cache、publisher表示 `Microsoft` | 2,823,706,996 | 9-file manifest `89d232e91d2c130e5ffd38f03776ad47c3b9779c8689e565fd4b2ea483f6e657` | catalogは`apache-2.0`、`reasoning,tool-calling`を宣言。独立配布license reviewは未完了。synthetic schema/IDは通過したがcontrol不合格、fixture accepted JSON 0件 |

両modelは`CUDAExecutionProvider`で実行し、ST-006で固定したCUDA EP 14-file manifest `8248ba49910e8fba56ddc8b4810a55cc9b3a069ca0dc2f7a23cd5f1f9a807021`を使用した。Foundry Local CLI/REST API 0.10.3はpreview、AppxはDeveloper署名であり、model/EPのcache copy、offline bundle、製品同梱、再配布は未承認である。ST-011の不合格をlicense不備と混同せず、機能面ではmanual-only、供給網面ではPK-004/Q-010までblockを維持する。

## ST-012 render解析runtime受入記録

取得・確認日: 2026年8月27日。ST-012のframe比較は既存の隔離Python環境に入っていたOpenCV/NumPyを使用した。以下のmanifest hashは、各installed distributionが列挙するfileのうち通常file（`__pycache__`と`.pyc`を除外）を、`relativePath NUL byteSize NUL fileSha256 LF`順で集約したStage 0証跡であり、wheel hashや完全なSBOMの代替ではない。

| 種別 | 固定対象 | 配布元 | installed file count / bytes | installed manifest SHA-256 | license・条件 |
|---|---|---|---:|---|---|
| Python distribution | `opencv-contrib-python` `5.0.0.93` | package metadata: https://github.com/opencv/opencv-python | 111 / 144,937,256 | `17cb96114ad1770c1cc5f6e4a11270bd63c78c13a56b377414f839e30ce97030` | metadataはApache 2.0。ST-012ではgrayscale frame MADだけに使用 |
| Python distribution | `opencv-python` `5.0.0.93` | package metadata: https://github.com/opencv/opencv-python | 47 / 144,683,896 | `88e81024645b0f27577ca20c93c340dbcd8f5c174bdf9bfd00fbfe816fbd57b0` | metadataはApache 2.0。同一`cv2` namespaceを共有するため、最終lockでは重複distributionを解消する |
| Python distribution | `numpy` `2.5.2` | PyPI / https://numpy.org/ | ST-016記録を再利用 | ST-016 wheel SHA-256 `28ac63476ec7651484215ee7fa15a1f78b57c14621f01e392afe17b9a1390ce4` | SPDX expressionは`BSD-3-Clause AND 0BSD AND MIT AND Zlib AND CC0-1.0` |

OpenCV 2 distributionの同居はStage 0環境の観測事実であり、両方を製品へ含める判断ではない。A-012がheldであるため、PK-001は両providerを製品lockから明示除外した。将来のannotationと別の採用決定が成立するまでwheelを選ばない。ST-012のsource-derived renderは権利未確認のためlocal technical evaluationに限定し、FFmpeg/OpenCV/NumPyの利用がfixtureの外部公開許可を与えるものではない。

## ST-010 MediaPipe受入記録

取得・確認日: 2026年8月27日。以下はWindows x64 / Python 3.12上のStage 0評価に使用した実在成果物であり、最終配布物への採用記録ではない。

| 種別 | 固定対象 | 配布元 | byte size | SHA-256 | ライセンス・用途 |
|---|---|---|---:|---|---|
| Python wheel | `mediapipe-1.0.1-py3-none-win_amd64.whl` | https://pypi.org/project/mediapipe/1.0.1/ | 20,103,114 | `96dc9de6bd04a6315ef424fda5c48e0929f2d78317295e75bc32c0bceeab517b` | package metadataは`Apache 2.0`。同梱`LICENSE`は12,331 bytes、SHA-256 `8707eef0533987efc5b155d64761eeb6e20793f50b9bd1a68dad1cf4719d0ed8` |
| Face Detector model | `blaze_face_short_range.tflite` (`float16/1`) | https://storage.googleapis.com/mediapipe-models/face_detector/blaze_face_short_range/float16/1/blaze_face_short_range.tflite | 229,746 | `b4578f35940bf5a1a655214a1cce5cab13eba73c1297cd78e1a04c2380b0152f` | MediaPipe Tasks Face DetectorのStage 0入力。モデルカード相当の機能・制約資料は公式Face Detector documentationを参照 |
| Positive-control image | `portrait.jpg` | https://storage.googleapis.com/mediapipe-assets/portrait.jpg | 176,561 | `a6f11efaa834706db23f275b6115058fa87fc7f14362681e6abe14e82749de3e` | benchmark正例だけに使用。MediaPipe vision testdataの`BUILD` packageは`licenses = ["notice"]`（Apache-2.0）を宣言。製品素材へ同梱しない |

モデルと正例assetは取得後にbyte sizeとSHA-256を検査し、一致した場合だけ原子的にcacheへ昇格する。cacheがないclean環境では固定HTTPS配布元への到達が必要であり、offline bundle、silent update禁止、最終Model BOMはF-008/PK-004で実装する。ST-010の0件結果は自動顔リフレームの採用根拠にせず、ST-015の人判断まで手動layout/keyframeを推奨する。

## ST-016 音響model・runtime受入記録

取得・確認日: 2026年8月27日。以下はWindows x64 / Python 3.12上のStage 0評価に使用した実在成果物であり、最終配布物への採用記録ではない。

| 種別 | 固定対象 | 配布元 | byte size | SHA-256 | license・条件 |
|---|---|---|---:|---|---|
| Audio classifier model | `yamnet_audio_classifier_with_metadata.tflite` (`float32/1`) | https://storage.googleapis.com/mediapipe-models/audio_classifier/yamnet/float32/1/yamnet.tflite | 4,126,810 | `4d8b4a53282dc83ef04e3e7dbc4fbc98082e34e44ed798e16c3a0cdd4c584faf` | Apache-2.0。Google YAMNet model cardとTensorFlow Model Gardenをlicense根拠とする。scoreはclass間でcalibrateされず、対象domainのannotationでthreshold調整が必要 |
| Speech control | `speech_16000_hz_mono.wav` | https://storage.googleapis.com/mediapipe-assets/speech_16000_hz_mono.wav | 136,798 | `71caf50b8757d6ab9cad5eae4d36669d3c20c225a51660afd7fe0dc44cdb74f6` | MediaPipe audio testdata packageの`licenses = ["notice"]`（Apache-2.0）。benchmark正例だけに使用し、製品素材へ同梱しない |
| VAD model | `silero_vad_v6.onnx` | `faster-whisper` 1.2.1 wheel内 `faster_whisper/assets/` | 1,245,151 | `4cbf549b8326f60f80f2536d9eefeb450a9abe83365a098031c89719f1be17d2` | upstream Silero VAD v6.0はMIT。copyright/permission noticeを配布物へ保持する。親wheelとasset hashを対で記録 |
| Python wheel | `faster_whisper-1.2.1-py3-none-any.whl` | PyPI / https://github.com/SYSTRAN/faster-whisper/tree/v1.2.1 | 1,118,909 | `79a66ad50688c0b794dd501dc340a736992a6342f7f95e5811be60b5224a26a7` | MIT。wheelにSilero assetを含む。最終配布時はSYSTRANとSilero双方のnoticeを含める |
| Python wheel | `onnxruntime-1.29.0-cp312-cp312-win_amd64.whl` | PyPI / https://onnxruntime.ai/ | 14,001,407 | `4acf2b4948b7ede87221ca6332344b8facdc8059d6ac751a7d367d04532b02dd` | MIT。Silero ONNX CPU推論runtime |
| Python wheel | `av-18.1.0-cp311-abi3-win_amd64.whl` | PyPI / https://pyav.org/ | 27,595,679 | `ea1480b7a8d5405cb5f382b344731bf125fd2c1c6fae3964f6c48595628387ff` | package metadataはBSD-3-Clause。音声decode/resampleに使用。bundled FFmpeg library構成は最終SBOMで別途確認 |
| Python wheel | `numpy-2.5.2-cp312-cp312-win_amd64.whl` | PyPI / https://numpy.org/ | 12,464,674 | `28ac63476ec7651484215ee7fa15a1f78b57c14621f01e392afe17b9a1390ce4` | package metadataのSPDX expressionは`BSD-3-Clause AND 0BSD AND MIT AND Zlib AND CC0-1.0`。最終noticeはwheel同梱licenseを基準に生成 |

license evidence:

- TensorFlow Models `LICENSE`（Apache-2.0）: https://raw.githubusercontent.com/tensorflow/models/master/LICENSE — 11,512 bytes、SHA-256 `5b17814bf0de8cf65069bc6d7cc38cff19fcaa864d243423ad3ef3db01b52385`
- Google YAMNet model card: https://www.kaggle.com/models/google/yamnet/tensorFlow2/yamnet/1 — Apache-2.0、521 audio event classes、score未calibrate・domain mismatchの制約を明記
- MediaPipe audio testdata `BUILD`: https://raw.githubusercontent.com/google-ai-edge/mediapipe/master/mediapipe/tasks/testdata/audio/BUILD — 1,543 bytes、SHA-256 `97969e828f6fb4aadbb9810a36dc99e6f0b806f32bebaa8a846140fa0b85e1ab`
- Silero VAD v6.0 `LICENSE`（MIT）: https://raw.githubusercontent.com/snakers4/silero-vad/v6.0/LICENSE — 1,075 bytes、SHA-256 `2e63e9a38b6e8fc0c7bc37ce174caca1862870856c6daf5697cfb785e925520b`
- faster-whisper v1.2.1 `LICENSE`（MIT）: https://raw.githubusercontent.com/SYSTRAN/faster-whisper/v1.2.1/LICENSE — 1,064 bytes、SHA-256 `af6798135e729f8aa6c853936d037dfdea449734d26b8ea6a89805fca758c0d5`
- ONNX Runtime v1.29.0 `LICENSE`（MIT）: https://raw.githubusercontent.com/microsoft/onnxruntime/v1.29.0/LICENSE — 1,073 bytes、SHA-256 `2f07c72751aed99790b8a4869cf2311df85a860b22ded05fa22803587a48922c`
- PyAV v18.1.0 `LICENSE.txt`（BSD-3-Clause）: https://raw.githubusercontent.com/PyAV-Org/PyAV/v18.1.0/LICENSE.txt — 1,505 bytes、SHA-256 `76af0461ffb92e19f1c14449e95557d83a2dfaa1baf202d49e5f1d8746c0da19`
- NumPy v2.5.2 `LICENSE.txt`（BSD-3-Clause。wheel内の追加第三者licenseは最終notice生成時に別途収集）: https://raw.githubusercontent.com/numpy/numpy/v2.5.2/LICENSE.txt — 1,543 bytes、SHA-256 `1be1df33863f97a7bc1c4d67980bd6c69c9a6fef0a5ee76e6ad6cb91e56e8491`

YAMNetとSileroのscore/thresholdはStage 0比較設定であり、productionの合否値ではない。YAMNetのcough候補は正例対照と人手annotationがないため、手動試聴なしに削除候補を確定しない。採用CPU workerのtransitive wheel hash lockはPK-001で完了した。offline model bundleと全license noticeの生成はPK-004/Q-010で完了させる。

## ST-014 Windows package toolchain受入記録

取得・確認日: 2026年8月27日。以下はWindows x64上のStage 0 package構築に使用した実在成果物であり、製品MSIXの署名・配布承認ではない。

| 種別 | 固定対象 | 配布元 | byte size | SHA-256 | license・条件 |
|---|---|---|---:|---|---|
| .NET SDK selector | `benchmarks/probes/global.json` / SDK `10.0.400` | installed .NET SDK | — | global.json `deb68fdcdf73ca5a9b4862010d170ebc117339887aa0aa72d22a04ade7437942` | probe buildだけをexact SDKへ固定。self-contained出力に含まれるruntime/native componentsのnoticeはPK-002/Q-010で収集する |
| NuGet package | `Microsoft.Windows.SDK.BuildTools` `10.0.26100.1` | Microsoft package feed proxy | 22,476,117 | `7e37f35384a7b146730fa67dde243d46b97ce5ca73aaeb1c3141cd812b286395` | `.nuspec`は`requireLicenseAcceptance=true`、license URL `https://aka.ms/WinSDKLicenseURL`を宣言。local package buildにのみ使用し、redistribution承認は未実施 |
| Package tool | x64 `makeappx.exe` | 上記BuildTools package | 567,328 | `9981418e9a18a9aa19bb69f835137a3bf8fe2e6fe7e18d5ca9b76191cc331f3f` | MSIX pack/unpackに使用。tool自体はStage 0 MSIXへ同梱していない |
| License evidence | Windows SDK license RTF | `https://download.microsoft.com/download/0/F/F/0FF2B061-47DD-4F55-89B6-FD1D8C44F14D/sdk_license.rtf` | 246,945 | `dd07eb178e00c6bba4148457fc00ff77cd4887eb521d504186fe59c9ec8bbe62` | binary RTFのため本文条件を本記録で要約・断定しない。署名済み配布前に組織のlicense reviewを必須とする |

BuildToolsはexact NuGet lock（content hash `ju02pHSp1LDEIvFgLIDbCbXGPpTTrXJrH90HxC6neclpbRpdS8RxeRbYOzPQFByUPCgT/Egbz4s/cpNMamW1Sg==`）からrestoreした。WinApp CLI 0.6.1は実機に存在したが、暗黙のBuildTools取得が再現不能だったためfinal runのpackage構築には使用していない。完全な.NET runtime notice、Windows SDK license承認、FFmpeg/model notice、SBOMはPK-002/PK-003/PK-004/Q-010で完了させる。

## ST-013 WebView2 preview runtime受入記録

取得・確認日: 2026年8月27日。以下はactual WebView2 Canvas/SVG captureのStage 0評価に使用したSDK、host runtime、Windows標準fontであり、製品bundleや配布承認ではない。

| 種別 | 固定対象 | 配布元 | byte size | SHA-256 | license・条件 |
|---|---|---|---:|---|---|
| NuGet package | `Microsoft.Web.WebView2` `1.0.4129.50` | Microsoft NuGet package | 9,245,553 | `d3934f482d484b89fb4825df720c710664e1143a1e90f7b3a60794ef33f473d2` | lock content hash `BuJC70c7SCl8wlvojLZbc/E5ONmGyG3fhAMmpftCpKerxae//TxgKOTELRK2QRD7m7jigNFQRf6cfVT3ZiIQoQ==`。同梱`LICENSE.txt`はBSD-style条件、1,487 bytes、SHA-256 `0af8f1b807512aae39c2ac1aa4d0cae65cabecb6fd554b8439a5162a0d6eca55`。binary再配布時はcopyright、条件、免責の保持が必要 |
| Host runtime | Evergreen WebView2 `151.0.4129.107` / `msedgewebview2.exe` | 対象Windowsへinstalled | 4,477,256 | `fe7922326f2a1d3188454ba5502861253223d5da10d9da0b858f9b10f3e01468` | ST-013はhost runtimeを使用しbundleしていない。Evergreen更新後の同一出力は保証せず、versionをrunごとに記録 |
| Windows font | Segoe UI Bold / `segoeuib.ttf` | 対象Windowsへinstalled | 947,092 | `aeb9e4a6ec5cc59f4d72df8189032d7dbb28f45161cf1552174818b5465dac4e` | browser/FFmpeg比較にhost fontを使用し、benchmarkへ同梱していない。製品動画への焼込・配布条件はブランドfont採用時に再審査 |

ST-013のNuGet restoreはSDK `10.0.400`を`global.json`で固定し、`packages.lock.json --locked-mode`で実行した。WebView2 Runtimeとfontはhost dependencyであり、NuGet package hashだけではそれらを固定しない。製品のWebView2 bootstrap/install、offline環境、runtime更新時の回帰、fontライセンス、SBOMはU-001/PK-005〜PK-007/Q-010で完了する。

## 追加確認済み一次資料

- faster-whisper公式リポジトリ/README/License: https://github.com/SYSTRAN/faster-whisper （MIT、`v1.2.1`、Python 3.9+、現行GPU要件はCUDA 12 + cuDNN 9）
- CTranslate2公式リポジトリ/License: https://github.com/OpenNMT/CTranslate2 （MIT）
- BtbN FFmpeg-Builds公式リポジトリ: https://github.com/BtbN/FFmpeg-Builds （build scriptsはMIT。生成FFmpegと同梱依存は各ライセンスに従う）
- BtbN Auto-Build tag: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-07-31-14-10
- FFmpeg License and Legal Considerations: https://ffmpeg.org/legal.html （FFmpegはLGPL 2.1-or-laterが基本で、GPL部分を有効化するとGPLが適用）
- PySceneDetect公式リポジトリ/ドキュメント: https://github.com/Breakthrough/PySceneDetect / https://www.scenedetect.com/docs/latest/ （BSD-3-Clause。採用時のexact版とPyAV/OpenCV依存を別途固定）
- MediaPipe公式リポジトリ: https://github.com/google-ai-edge/mediapipe （Apache-2.0。Python packageとFace Detector model assetの版/hashを採用時に固定）
- MediaPipe Face Detector documentation: https://ai.google.dev/edge/mediapipe/solutions/vision/face_detector （IMAGE/VIDEO/LIVE_STREAM、model、検出結果、構成option、制約の一次資料）

`BtbN`はFFmpeg本体の開発者名ではなく、再現可能なbuild scriptsとWindows向け成果物を公開する配布プロジェクト名である。開発PoCで取得するzipと、最終配布で採用するFFmpeg構成を同一視しない。最終配布時は公式checklistに照らしてsource/configure/dependencyライセンスを別途固定する。

## SBOM / Model BOM

依存情報は後続F-008で機械可読CSVと通知文書へ移す。配布候補に未確認のlicense、取得元、hash、利用条件が1件でもあればreleaseを保留する。
