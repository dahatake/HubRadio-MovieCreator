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

## ST-010 MediaPipe受入記録

取得・確認日: 2026年8月27日。以下はWindows x64 / Python 3.12上のStage 0評価に使用した実在成果物であり、最終配布物への採用記録ではない。

| 種別 | 固定対象 | 配布元 | byte size | SHA-256 | ライセンス・用途 |
|---|---|---|---:|---|---|
| Python wheel | `mediapipe-1.0.1-py3-none-win_amd64.whl` | https://pypi.org/project/mediapipe/1.0.1/ | 20,103,114 | `96dc9de6bd04a6315ef424fda5c48e0929f2d78317295e75bc32c0bceeab517b` | package metadataは`Apache 2.0`。同梱`LICENSE`は12,331 bytes、SHA-256 `8707eef0533987efc5b155d64761eeb6e20793f50b9bd1a68dad1cf4719d0ed8` |
| Face Detector model | `blaze_face_short_range.tflite` (`float16/1`) | https://storage.googleapis.com/mediapipe-models/face_detector/blaze_face_short_range/float16/1/blaze_face_short_range.tflite | 229,746 | `b4578f35940bf5a1a655214a1cce5cab13eba73c1297cd78e1a04c2380b0152f` | MediaPipe Tasks Face DetectorのStage 0入力。モデルカード相当の機能・制約資料は公式Face Detector documentationを参照 |
| Positive-control image | `portrait.jpg` | https://storage.googleapis.com/mediapipe-assets/portrait.jpg | 176,561 | `a6f11efaa834706db23f275b6115058fa87fc7f14362681e6abe14e82749de3e` | benchmark正例だけに使用。MediaPipe vision testdataの`BUILD` packageは`licenses = ["notice"]`（Apache-2.0）を宣言。製品素材へ同梱しない |

モデルと正例assetは取得後にbyte sizeとSHA-256を検査し、一致した場合だけ原子的にcacheへ昇格する。cacheがないclean環境では固定HTTPS配布元への到達が必要であり、offline bundle、silent update禁止、最終Model BOMはF-008/PK-004で実装する。ST-010の0件結果は自動顔リフレームの採用根拠にせず、ST-015の人判断まで手動layout/keyframeを推奨する。

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
