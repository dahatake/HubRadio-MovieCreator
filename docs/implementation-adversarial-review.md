# 実装進行成果物 敵対的レビュー

**対象:** `docs/`、`benchmarks/`、ルート設定の2026年8月27日時点スナップショット  
**目的:** Windows 11ローカル優先アプリの全実装計画を、各タスクの検証・敵対的レビューを伴って実行すること  
**レビュー方針:** 5軸を独立に監査し、実ファイル・実行結果で裏付けられない候補、未着手と明記された将来タスク、単なる好みを除外した。  

未実行と明記されたStage 0残件やアプリ本体は、それ自体を完成済み成果物の欠陥として水増ししていない。進捗表示や完了主張が実態と矛盾する箇所だけを問題にした。

| No. | 軸 | 重大度 | 指摘箇所 | 問題の説明 | 修正案 |
|-----|-----|--------|---------|-----------|--------|
| 1 | 品質・運用性 | Critical | リポジトリ直下 | `.gitignore`がなく、権利未確認の6,494,572,403 byte動画とraw benchmark runが`git status`に出ていた。誤commitによる情報漏えいとリポジトリ肥大化が現実に起こり得た。 | **修正済み:** `sample/*`、raw run、work/cache/log/model、各言語build成果物と秘密設定を除外し、`sample/.gitkeep`だけ追跡可能にした。`git check-ignore`で動画とrunの除外を確認した。 |
| 2 | 品質・運用性 | Critical | `benchmarks/run-benchmarks.ps1`、`benchmarks/stages/inspect-media.ps1` | 子プロセスが無期限`WaitForExit()`で、取消・timeout・子孫killがなかった。端末停止後も全編decodeの`ffmpeg.exe` PID 48516が孤児として残ったことを実測した。 | **修正済み:** 親、metadata、packet、decodeへ安全上限を設け、timeout/例外/finallyで`Kill(true)`と`Dispose()`を行う。60秒timeout試験でexit 1、孤児0、tmp 0を確認した。 |
| 3 | 品質・運用性 | Critical | `inspect-media.ps1`の`firstErrorLines`、親ハーネスの例外 | FFmpeg/ffprobe stderrはsource絶対パスやユーザープロファイルを含み得るのに、そのまま永続化・例外化しつつ`absolutePathPersisted=false`としていた。 | **修正済み:** `<SOURCE>`、`<WORKSPACE>`、`<USERPROFILE>`へ置換してから保存・例外化する。timeout失敗runに絶対パスがないことを検査した。 |
| 4 | 品質・運用性 | Major | `Invoke-JsonChildProcess`、`Invoke-CapturedProcess`、`Get-PacketStatistics` | `System.Diagnostics.Process`を破棄しておらず、反復実行でOS handleを蓄積し得た。 | **修正済み:** Start状態を追跡し、全経路の`finally`で子孫停止と`Dispose()`を実施する。 |
| 5 | 品質・運用性 | Major | `benchmarks/results/schema.json`と`run-benchmarks.ps1` | 厳密なJSON Schemaを用意していたが、生成時に一度も実行していなかった。schemaと結果の乖離を防げなかった。 | **修正済み:** 原子的昇格前に`Test-Json -SchemaFile`を必須化。全成功/失敗runが有効であることを確認した。 |
| 6 | 整合性 | Major | `Assert-HardwarePayload`、`Assert-FfmpegPayload`、`Assert-MediaPayload` | 必須property名だけを見て`schemaVersion`値を検査しておらず、互換性のない子payloadも受理できた。 | **修正済み:** 各payloadで`1.0.0`を明示検査し、不一致を拒否する。 |
| 7 | 品質・運用性 | Major | `run-benchmarks.ps1` source不変検査 | 実行前はSHA-256を検査したが、実行後はサイズとmtimeだけだった。同サイズ・mtime復元を伴う内容変更を検出できなかった。 | **修正済み:** stage終了後にもSHA-256を再計算し、実行前hashとの一致を要求する。 |
| 8 | 整合性 | Major | `inspect-media.ps1`のFFmpeg探索 | ST-003がWinGet directoryの最新候補を選ぶだけで、ST-002で承認した`ffmpeg.exe`/`ffprobe.exe`と同一か確認しなかった。 | **修正済み:** canonical ST-002成果物を読み、両exeの実測SHA-256が一致しない限り媒体検査を開始しない。 |
| 9 | 内容の妥当性 | Major | `inspect-ffmpeg.ps1` | package versionを固定文字列で出しながら、探索は更新日時が最新のdirectoryだった。別buildへ更新されても古い版として記録し得た。 | **修正済み:** 実際の`ffmpeg -version`先頭行にexact build ID `n8.1.2-34-g9b6c8969e0-20260731`があることを強制する。 |
| 10 | 根拠性・不確実性管理 | Major | ST-002 `package.archiveSha256` | archiveはstage内で再取得・再hashしていないのに、manifest由来の期待hashを実測hashのように提示していた。 | **修正済み:** `archiveSha256Provenance`と`archiveBytesRehashedByStage=false`を追加。展開後exe hashとは明確に分離した。 |
| 11 | 根拠性・不確実性管理 | Major | `hardware-inventory.json` | canonical結果に元`benchmarkRunId`/run hashがなく、recordedAt、NVIDIA構造、空き容量、tool表記がraw runと異なっていた。どの測定から作ったか追跡不能だった。 | **修正済み:** 元run ID/SHA-256を追加し、実測payloadへ値と構造を同期。人の解釈は`assessment`/`stage0Implications`として分離した。 |
| 12 | 目的適合性 | Major | `implementation-plan.md`先頭・末尾、ADR-0001 | ADRは実行許可済みでP/STを実行中なのに、計画は「レビュー待ち」「次はP-001」のままだった。実態と次作業を誤表示した。 | **修正済み:** 実行中、P全件とST-001/002完了、次はST-003、後続gateを明記した。 |
| 13 | 整合性 | Major | ADR-0001優先順位、`implementation-research.md`、ADR-0002 | 正本優先順位に後から作ったAccepted ADRがなく、調査書のPostgreSQL候補がWindows計画のSQLite決定より上位に読めた。 | **修正済み:** Accepted ADRを調査書より上位へ追加。ADR-0002と調査書にSQLiteがWindows後続決定であることを明記した。 |
| 14 | 整合性 | Major | ADR-0004、計画S-004 | 「自動的に公開範囲を拡大しない」だけで、人が社内限定から外部候補へ拡大した場合の版・承認失効・再レビューが未定義だった。 | **修正済み:** 権限/理由を監査し、子CompositionVersionを作成、旧承認を失効し、拡大後に必要な全レビューを再実行する規則を追加した。 |
| 15 | 品質・運用性 | Major | ADR-0005の解析成果物/backup | 調査書は未採用Suggestionも監査用に隔離するとしたが、backupは「採用済みとModelRun」だけで、却下・修正判断を復元できなかった。 | **修正済み:** Suggestionと採用・却下・修正イベントを案件と共に保持・backupし、再生成可能な大容量raw特徴だけ除外可能とした。 |
| 16 | 内容の妥当性 | Major | 計画D-005 | 要求は単語時刻を求める一方、PoC不達時は発話時刻へfallbackする方針だったが、Tokenの時刻欠落表現が未定義で、架空単語時刻を作る余地があった。 | **修正済み:** 取得不能Tokenは時刻を捏造せず、親Utterance範囲と手動調整要否を参照する。 |
| 17 | 内容の妥当性 | Major | 計画D-007、ADR-0005 | `WorkingCopy`と不変`CompositionVersion`の関係、保存時点、crash復旧が不明だった。 | **修正済み:** WorkingCopyはSQLite上の復旧可能な可変draft、checkpointで新しい不変CompositionVersionへ昇格すると定義した。 |
| 18 | 整合性 | Major | 計画D-006、A-016、R-014 | 採用SuggestionをEditorialRecordへ変換するか、SuggestionのままCompositionへ入れるか曖昧で、来歴の二重化が起こり得た。 | **修正済み:** Suggestionは監査記録として残し、Composition要素がSuggestion IDとOriginRefを参照。人が独自作成した文言だけEditorialRecordとした。 |
| 19 | 整合性 | Major | 計画D-008/D-010/R-020 | RenderProfileとRenderManifestの版・役割境界が不明瞭で、最終render入力を取り違え得た。 | **修正済み:** RenderProfileは版ID/content digestを持つ出力仕様、RenderManifestは単一render実行記録と定義した。 |
| 20 | 内容の妥当性 | Major | `inspect-media.ps1` format正規化 | `size`や`bit_rate`が欠落した場合に`[int64]$null`で0へ変換し、「不明」を実測0として記録し得た。 | **修正済み:** `Convert-OptionalInt64`で欠落/N/Aを`null`のまま保持する。 |
| 21 | 品質・運用性 | Major | `run-benchmarks.ps1`失敗経路 | schemaは`failed`を許したが、実装は失敗時に何も保存せず、原因が端末だけに消えた。 | **修正済み:** 秘匿済みcode/messageを持つfailed JSONをschema検証後に原子的保存し、その後non-zero終了。timeout試験で生成・schema・孤児0を確認した。 |
| 22 | 品質・運用性 | Minor | benchmark schema運用 | `1.0.0` schemaを将来どう変更するか規則がなく、過去runを同じ版の変更で壊す恐れがあった。 | **修正済み:** `benchmarks/results/README.md`で1.0内は任意追加のみ、breaking changeは新しいimmutable schema、過去run非書換えと定義した。 |
| 23 | 品質・運用性 | Minor | `schema.json`の`runId` | 任意文字列を許しており、時刻/stage/衝突回避suffixの形式を機械検査できなかった。 | **修正済み:** 現行生成規則に対応する正規表現patternを追加した。 |
| 24 | 根拠性・不確実性管理 | Minor | `third-party-intake.md` | Stage 0候補のPySceneDetectとMediaPipeについて、方針表にはlicenseを記載したが一次資料URLが一覧になかった。 | **修正済み:** 公式repository/documentationとlicense識別子を追加し、exact package/model版は採用時に固定すると留保した。 |
| 25 | 根拠性・不確実性管理 | Minor | ADR-0006 Azure subscription集計 | 86件/既定0の集計に取得方法がなく、保存しなかった個別IDとの境界も不明だった。 | **修正済み:** Azure MCP `subscription_list`のcredential/read-only実行による集計で、個別ID・tenant・名称は機密性のため保存しないと明記した。 |

## サマリー（検出時）

- Critical: 3件
- Major: 18件
- Minor: 4件
- 合格判定: **FAIL**（Critical > 0）

## 修正後再判定

- 未修正 Critical: 0件
- 未修正 Major: 0件
- 未修正 Minor: 0件
- 合格判定: **PASS**（Critical = 0）

## 修正内容の概要

- 権利未確認の大容量素材とraw runをGit対象外にし、追跡用placeholderだけ残した。
- 実際に孤児化したFFmpegを停止し、親/子/packet全経路へtimeout、子孫kill、Disposeを実装した。
- source/user/workspace pathをエラーから除去し、成功・失敗runをJSON Schemaで実検証して原子的保存するようにした。
- 実行後source SHA-256、ST-002実行ファイルhash、exact FFmpeg buildを強制した。
- archive manifest hash、展開後exe実測hash、runtime encode未測定を分離した。
- canonical結果をraw run ID/SHA-256へ結び付け、schema互換規則とrun ID形式を固定した。
- 計画進捗、ADR優先順位、SQLite判断、公開区分拡大、Suggestion保持を文書間で同期した。
- Token時刻fallback、WorkingCopy昇格、Suggestion採用来歴、RenderProfile/Manifest境界を明確化した。
- 第三者候補とAzure集計の出典・取得方法・未確認事項を明記した。

## 除外した主な誤検出

- `d3d12va`は当該FFmpegが実際に`-hwaccels`へ出した値であり、存在しない機能として削除しなかった。ただしruntime成功は未測定と明記した。
- 2026年7月/4月のFFmpeg/C2PA日付はレビュー日2026年8月より過去であり、未来日付ではない。
- Python 3.14.7は対象PCの実測値であり、公開時期の推測を理由に削除しなかった。ML wheel互換は別途Python 3.12隔離PoCで確認する。
- fixture manifestとbenchmark resultは別schemaの成果物であり、`pending-ST-003`がresult stage enumに入らないことは矛盾ではない。
- 修正前のraw FFmpeg runに、後から追加した任意のarchive来歴fieldがないことは履歴改変の理由にしない。`benchmarks/results/README.md`どおり過去runを非書換えで保持し、canonical summaryは修正後run ID/SHA-256を参照する。
- ST-003以降、正解注釈、Stage 0統合報告、アプリ本体の未完成は現在の計画で明示されており、完成済みと偽っていないため問題件数へ水増ししなかった。

## 実動画生成タスクの追加レビュー（2026年8月27日）

**対象:** `tools/create-test-video.ps1`、`test/20260615_024326000_iOS.MOV`、同manifest、問題レポート  
**現成果物:** H.264 Main 1280×720 yuv420p 30000/1001 fps、AAC-LC 48 kHz stereo、1,081.428秒、374,146,177 byte  
**完全性:** source/output SHA-256はmanifestと一致し、sourceは不変、outputはsourceと異なる。承認済みFFmpegによる全編decodeはexit 0・message 0、代表3フレームにも明白な破損なし。

初回レビューでは現動画の欠陥を検出しなかった。将来の再実行経路まで対象にした二次レビューで、現動画には影響しない次の5件を検出し、全て修正した。

| No. | 軸 | 重大度 | 指摘 | 修正と確認 |
|-----|-----|--------|------|------------|
| V-1 | 内容の妥当性 | Minor | manifestは`yuv420p`を主張する一方、内部ffprobe取得・合否判定に`pix_fmt`がなかった。 | **修正済み:** `pix_fmt`を取得し、`yuv420p`を必須化。現manifestにも実測値を記録した。 |
| V-2 | 整合性 | Major | sourceに音声があってもoutput音声0本を合格扱いできた。 | **修正済み:** source音声有無を取得し、有ならoutputをAAC 1本、無なら0本に限定した。現outputはAAC 1本で合格。 |
| V-3 | 品質・運用性 | Minor | 後続失敗時に問題レポートを上書きし、既存の問題1・2を失う実装だった。 | **修正済み:** 既存最大番号の次へ原子的に追記する。小型一時ファイルで問題1〜4の順序と既存内容保持を確認した。 |
| V-4 | 整合性 | Major | output昇格後にmanifest昇格が失敗すると、新outputと旧manifestが混在し得た。 | **修正済み:** 既存pairを同一directoryの一時backupへ退避し、どちらかの昇格失敗時は両方を復元する。正常commitと意図的manifest失敗のrollbackを小型一時ファイルで確認した。 |
| V-5 | 品質・運用性 | Minor | `status=succeeded`前の検査がffprobeだけで、全編decodeを実行していなかった。 | **修正済み:** staging outputを`-xerror`付きで映像・任意音声とも全編decodeし、成功証跡をmanifestへ記録する。現outputも独立実行で合格済み。 |

metadataについては、source由来のglobal metadataが出力に残っていない一方、FFmpegが生成するcontainer/codec技術タグは存在することを実測した。manifestと生成コードの表現を「source global metadataをcopyしない。技術タグは残り得る」へ修正し、全metadata消去という過剰な主張を除いた。

### 追加レビュー修正後判定

- 未修正 Major: 0件
- 未修正 Minor: 0件
- 現動画の再render要否: 不要
- 合格判定: **PASS**

## ST-009 ショット検出の敵対的レビュー（2026年8月27日）

**対象:** `benchmarks/stages/detect-shots.ps1`、`shot_detection.py`、ベンチハーネス、schema、raw run、`benchmarks/results/shot-detection.json`
**採用raw証跡:** `20260826T2259012687261Z-shot-detection-5f160fd6`、SHA-256 `2be6879b2d3c964bca80ff86e3841a0d1b4abb7b37af3beadf11afa0a9b67bb3`
**実測:** Content 27.0、Content 40.0、Adaptive 3.0はいずれもfixture上の境界0件。4、8、12秒に既知カットを持つ対照動画では全構成が3件すべてを検出した。

| No. | 軸 | 重大度 | 指摘 | 修正と確認 |
|-----|-----|--------|------|------------|
| S9-1 | 内容の妥当性 | Major | PySceneDetectの`get_scene_list()`はカット0件時に既定で空配列を返すため、実際は全編1シーンなのに`sceneCount=0`と記録していた。 | **修正済み:** `start_in_scene=True`を指定した。カットなし3秒区間で既定0件、修正版1件を再現し、再測定で全構成が`sceneCount=1`、`boundaryCount=0`になった。 |
| S9-2 | 根拠性・不確実性管理 | Major | 初回測定の境界0件だけでは、素材に検出対象がない場合と検出器が動作していない場合を区別できなかった。 | **修正済み:** 既知カット対照を同じ3構成で必須実行し、全構成が3/3件を検出したことをrawへ保存した。ハーネスも対照不合格を成功扱いしない。 |
| S9-3 | 品質・運用性 | Major | PTS CSVが空でもfixtureの境界が0件なら写像処理へ入らず、成功結果を返せた。 | **修正済み:** sourceまたはproxyの観測PTSが0件ならPython側で即時失敗する。 |

### 修正後の証拠照合

- raw runのSHA-256、run ID、source hash、proxy hash、3構成の測定値、対照結果をcanonicalへ機械照合し、PASSを確認した。
- PythonはPylance構文検査と`py_compile`、PowerShellはparser検査に合格した。VS Code選択環境には`scenedetect`がないためimport診断が残るが、固定された`benchmarks/.venv`ではPySceneDetect 0.7.1のimportと実行に成功している。
- 正解注釈がないため、誤検出率・見逃し率と採用閾値は測定不能である。`selectedDetector`と`selectedThreshold`は`null`のまま、`openQuestions`へ残した。
- fixture上の検出境界が0件のため、実境界を代理PTSから元PTSへ変換するend-to-end経路は未測定である。静的実装の存在を実測済みと表現していない。

### 採用しなかった指摘

- 子stage単体にfixture hash検査がないという指摘は、親ハーネスが子stageの実行前後にsource SHA-256を検査する設計境界を見落としていたため採用しなかった。
- 対照生成のNVENC fallback不在は、ST-004の固定NVENC代理とP-004の記録済み検証機を前提にするStage 0測定の欠陥ではない。製品のfallbackはST-005のcanonicalに別途記録済みである。
- JSON Schemaよりruntime validatorの必須項目が多い点は、過去raw互換を保つschemaと新規実行を厳格化するvalidatorの役割差であり、既存runを壊すrequired追加は行わなかった。
- canonicalがrawの空`firstBoundaries`等を省略する点は、rawを保持したままレビュー済み非機密要約だけを追跡する既定方針に合致するため、データ消失とは扱わなかった。

### ST-009 修正後判定

- 未修正 Critical: 0件
- 未修正 Major: 0件
- 未修正 Minor: 0件
- 閾値選定: **未測定（正例注釈待ち）**
- 合格判定: **PASS（測定タスク完了、未測定値の捏造なし）**

## ST-010 顔検出の敵対的レビュー（2026年8月27日）

**対象:** `benchmarks/stages/detect-faces.ps1`、`face_detection.py`、ベンチハーネス、schema、raw run、`benchmarks/results/face-detection.json`
**採用raw証跡:** `20260827T0008446257559Z-face-detection-df3aece7`、SHA-256 `cae1e0f0e4f25f02cf8959802a2302245951a543e48f16a582cd0c4fe120728e`
**実測:** MediaPipe 1.0.1、short-range model、640×360代理、VIDEO mode、confidence 0.5。300/300標本をdecodeし、fixtureの検出は0件。hash固定したMediaPipe portrait正例では1件、score 0.921886を検出した。

| No. | 軸 | 重大度 | 指摘 | 修正と確認 |
|-----|-----|--------|------|------------|
| S10-1 | 品質・運用性 | Critical | 初期実装は取得したmodelのhashを記録するだけで、破損・差替えを使用前に拒否せず、直接最終pathへ保存していた。 | **修正済み:** byte sizeとSHA-256を定数固定し、GUID付き一時fileへ取得、検証後に原子的昇格する。既存cacheも毎回再検証し、不一致をfail closedにした。 |
| S10-2 | 整合性 | Major | 観測PTS不足時に`frame index × 33ms`を代用でき、VFR素材の時刻を捏造する経路があった。 | **修正済み:** source/proxy PTSを必須化し、厳密単調性とproxy frame count一致を検査する。任意fps換算fallbackを削除した。 |
| S10-3 | 根拠性・不確実性管理 | Major | 初回runはfixture 0件だけを記録し、顔が難しい内容なのかmodel/pipelineが動作していないのか区別できなかった。 | **修正済み:** byte size/SHA-256固定のMediaPipe portraitを同じmodel・VIDEO modeへ通す正例対照を必須化。対照0件ならstageを失敗させ、再測定で1件を検出した。 |
| S10-4 | 根拠性・不確実性管理 | Major | 小顔・非正面・複数人の定性分類が12フレーム目視レビューを参照していたが、そのraw証跡path/hashがcanonicalになかった。 | **修正済み:** 権利未確認画像をcommitせず、`benchmarks/work/face-review.json`とmontageをlocal raw証跡として保存し、canonicalへJSONのbyte size/SHA-256を記録した。資料共有は未観測のまま`openQuestions`へ残した。 |
| S10-5 | 品質・運用性 | Major | payload assertion失敗時、childが返した安全なmodel/library/sampling情報もfailed rawから全て失われた。 | **修正済み:** `Get-SafeFailurePayload`で安全なtop-level allowlistだけを将来のfailed payloadへ保存する。`observations`、bbox、source time、任意pathは除外した。修正前run `20260827T0007291076285Z-face-detection-f58ec3d1`の空payloadは履歴証跡として非書換えで保持し、修正後関数をASTから抽出した合成partial payloadとfailed envelopeでallowlist・schema回帰を確認した。 |
| S10-6 | 根拠性・不確実性管理 | Major | `mediapipe==1.0.1`の版だけがあり、実際のWindows wheelと同梱license textのcontent hashが受入記録になかった。 | **修正済み:** `mediapipe-1.0.1-py3-none-win_amd64.whl`、同梱LICENSE、model、正例assetの配布元・byte size・SHA-256を`third-party-intake.md`へ実測記録した。 |
| S10-7 | 整合性 | Major | 対照追加時に`schemaVersion 1.0.0`の`control`をrequiredへ加え、対照追加前の成功rawが同じschemaで再検証できなくなった。 | **修正済み:** schemaの`control`定義はoptionalな互換追加とし、新規runだけを`Assert-FaceDetectionPayload`で必須化した。対照追加前後の成功rawを同じschemaで再検証した。 |

### 修正後の証拠照合

- canonicalのrun ID/run hash、source/proxy/model/control hash、300標本、検出集計、処理時間をrawへ機械照合し、PASSを確認した。
- 正例対照はshort-range modelでIMAGE/VIDEOとも1件を検出した。fixtureの0件を「顔が存在しない」と解釈していない。
- 12枚の層化目視subsetでは小顔、非正面、複数人を定性的に確認したが、bbox正解注釈は作成していない。条件別precision/recallを算出せず、資料共有は未測定とした。
- Pythonは固定`benchmarks/.venv`でcompile/import/実行、PowerShellはparser、raw/failed envelopeはJSON Schema、sourceは実行前後SHA-256、孤児processは0を確認した。
- 修正前failed rawは書き換えていない。修正後の失敗経路は、`library`等の許可fieldを保持しながら`observations`と任意pathを除外する合成payloadを作り、failed envelopeがschemaを通ることを独立検証した。

### 採用しなかった指摘

- modelと正例assetをGitへ同梱すべきという指摘は、Stage 0ではhash固定cacheと取得証跡が完了条件であり、offline bundleはF-008/PK-004に明示された後続タスクであるため採用しなかった。clean環境で固定配布元への到達が必要な制約は受入記録へ明記した。
- 資料共有が12枚のsubsetにないためST-010全体が未完了という指摘は、添付実行プラン§5.1が正解データ不在時に分布・処理時間・対照実験を記録し、必要指標を`openQuestions`へ残す完了方法を明記しているため採用しなかった。
- rawの全300 observationsをcanonicalへ複製すべきという指摘は、source timeとbboxを含むlocal rawをGit対象外にし、レビュー済み非機密要約だけを追跡する方針に反するため採用しなかった。

### ST-010 修正後判定

- 未修正 Critical: 0件
- 未修正 Major: 0件
- 未修正 Minor: 0件
- precision/recall・条件別miss率: **未測定（`visual-reference.jsonl`待ち）**
- 構成推奨: **現行full-frame 640×360 short-range構成は自動リフレームへ非推奨。手動layout/keyframeを維持**
- 合格判定: **PASS（測定タスク完了、最終採否はST-015承認待ち）**

## ST-016 音響イベント・録画品質の敵対的レビュー（2026年8月27日）

**対象:** `benchmarks/stages/detect-audio-events.ps1`、`audio_event_detection.py`、ベンチハーネス、schema、raw run、`benchmarks/results/audio-event-and-recording-quality.json`
**採用raw証跡:** `20260827T0046465037091Z-audio-event-and-recording-quality-54f1f69b`、SHA-256 `c2a23164b490c197e2f225213bc9d8cd4ffef9bfcd1b990b7c6784663d3a6d73`
**実測:** FFmpeg `silencedetect`、100 ms波形規則、Silero VAD、YAMNetを16 kHz解析音声上で比較。全方式の正例対照は合格した。fixtureではstrict silence 0件、低エネルギー1件、clipping 0件、channel差2件、Silero待ち1件、YAMNet咳1件・noise 0件・wait 1件だった。

| No. | 軸 | 重大度 | 指摘 | 修正と確認 |
|-----|-----|--------|------|------------|
| S16-1 | 整合性 | Major | 初回runはST-003の最小audio packet PTS `-0.044`秒をdecoded PCMの開始時刻として加算したが、PyAV decoderはcodec delayを除去し最初のdecoded frameをPTS 0で返していたため、全候補時刻が44 ms早かった。 | **修正済み:** Pythonでstream time base、最初のdecoded frame PTS/time/sample数を実測し、期待値と不一致なら失敗する。修正版で候補時刻が+0.044秒補正された。初回rawは非書換えで保持した。 |
| S16-2 | 根拠性・不確実性管理 | Major | 時刻補正後runは成功したが、FFmpeg `silencedetect`設定とdecoded-frame timing evidenceをpayloadへ投影しておらず、canonical主張をrawだけで再検証できなかった。 | **修正済み:** final rawへ両証拠を追加し、新規runのruntime validatorで`timelineAuthority`、`decodedTimelineEvidence`、FFmpeg設定を必須化した。schema 1.0は過去raw互換のためrequired追加せず、旧不完全payloadがruntimeで拒否されることを確認した。 |
| S16-3 | 根拠性・不確実性管理 | Major | Python環境setupは3つのtop-level packageしかpinしていないのに「exact dependency set」へ追跡できると過大主張し、ST-016が直接使う`onnxruntime`、`av`、`numpy`はtransitive driftし得た。 | **修正済み:** 説明をdirect runtime pinへ限定し、3 packageをexact版へ昇格した。complete hash-locked transitive setはPK-001へ明示的に留保し、更新後setup実行で6 pinすべての一致を確認した。 |
| S16-4 | 根拠性・不確実性管理 | Major | YAMNet、speech control、Silero VAD、親`faster-whisper` wheelとdirect runtime wheelのartifact hash／license／notice条件が受入台帳になかった。 | **修正済み:** model/control/wheelのbyte size・SHA-256、exact tag license本文のURL・SHA-256、notice条件を`third-party-intake.md`へ記録し、canonicalのmodel/control/toolingから同節を参照した。 |

### 修正後の証拠照合

- raw runのID/SHA-256、source/model/control hash、decoded sample数、全候補件数・総時間、方式間temporal Jaccard、処理時間をcanonicalへ機械照合し、PASSを確認した。
- FFmpeg既知3秒silence、波形のlow-energy/clipping/channel差、Silero speech/silence、YAMNet speech/silence/white-noise対照はすべて合格した。rights-cleared cough正例は存在せず、`openQuestions`に残した。
- ST-003は全編decode exit 0、decoder error 0件である。null muxer由来61行を録画破損と誤分類していない。
- 終端待ちは波形・Silero・YAMNetが重なったがstrict silenceではない。channel差2件はSpeech区間、咳1件はYAMNet単独・低score・context依存であり、真偽ラベルではなく誤検出リスクとして分類した。
- candidate時刻、audio sample、clip、transcriptはcanonicalから除外し、local raw/reviewだけへ保存した。source hash不変、schema、process cleanup、direct package版、license参照を確認した。

### 採用しなかった指摘

- schema 1.0でもnested timing/configをrequired化すべきという提案は、同版の過去rawを壊すため採用しなかった。新規runはruntime validatorで厳格化し、schemaは後方互換を維持した。
- 直ちに全transitive dependencyをhash lockすべきという提案は、採用技術確定後のPK-001に明記された作業である。ST-016が直接importするruntime版とwheel hashは今回固定し、full lockだけを後続へ残した。
- 正解注釈がないためST-016未完了という指摘は、実行プラン§5.1が分布・処理時間・対照実験を測り、精度を`openQuestions`へ残す完了方法を定義しているため採用しなかった。
- candidate 0件をイベント不在の証拠とする解釈、YAMNet咳候補を真の咳とする解釈、自動削除への採用はいずれも証拠を超えるため採用しなかった。

### ST-016 修正後判定

- 未修正 Critical: 0件
- 未修正 Major: 0件
- 未修正 Minor: 0件
- Precision/Recall・production閾値: **未測定（`removal-reference.jsonl`待ち）**
- 推奨: **decoder/clipping/channel差はtechnical warning候補、waitはreview候補、YAMNet cough/noiseは手動試聴必須**
- 自動削除: **禁止**
- 合格判定: **PASS（測定タスク完了、最終採否はST-015承認待ち）**

## ST-014 Process・packageの敵対的レビュー（2026年8月27日）

**対象:** `benchmarks/probes/`、`benchmarks/stages/measure-process-and-package.ps1`、ベンチハーネス、schema、raw run、`benchmarks/results/process-and-package.json`
**採用raw証跡:** `20260827T0302246003561Z-process-and-package-2ba57f42`、SHA-256 `691536494a83777cdaf3d930368bff7fa583e754305251d31a77f1bb8259521b`
**実測:** kill-on-close、host crash、2秒timeoutの子・孫終了は合格。128 MiB process-memory候補制限は割当後のallocationで合格した。active-process limitは2つ目の割当をWin32 error 1816で拒否したが自動終了せず、caller cleanup後もJob PID listと`ActiveProcesses`が10秒以内に1へ収束しなかったため不採用とした。unsigned MSIXは2つのfirst-party exe、FFmpeg/ffprobe、model、source由来codec clipを含み、pack/unpack、HEVC/AAC decode、model hash照合に合格した。

| No. | 軸 | 重大度 | 指摘 | 修正と確認 |
|-----|-----|--------|------|------------|
| S14-1 | 品質・運用性 | Critical | active-process limitに拒否された2つ目のprocessが実機で自動終了せず、初期probeが孤児を残した。 | **修正済み:** assignment失敗と自動終了を別記録し、callerが必ず`Kill(entireProcessTree:true)`、bounded wait、process不在確認を行う。例外経路も`finally`で同じcleanupを実行する。final runと5回stress replayで孤児0を確認し、active-process limit自体は不採用とした。 |
| S14-2 | 整合性 | Major | memory-hogがJob割当前にallocationを開始でき、128 MiB制限が失敗原因だと断定できない競合があった。 | **修正済み:** child ready → Job割当 → go marker → allocationのhandshakeへ変更し、`allocatedBytesBeforeFailure`、working set、Job peakをrawへ保存した。production値としては未承認のまま保持した。 |
| S14-3 | 整合性 | Major | `JOBOBJECT_BASIC_PROCESS_ID_LIST`の件数をactive-process countとして扱い、拒否processのhandleが残る状態で1件への減少を期待していた。 | **修正済み:** PID listと`JOBOBJECT_BASIC_ACCOUNTING_INFORMATION.ActiveProcesses`を別々に取得し、cleanup前後を記録した。final runではprocess不在を確認した一方、両countは2のまま非収束であることを隠さず記録した。 |
| S14-4 | 品質・運用性 | Major | kill-on-closeの`processCountBeforeClose`に3〜4 PIDがあっても、named child/grandchildの2件だけでleak 0を判定していた。 | **修正済み:** Jobが返した全PIDの終了を検査し、`allObservedProcessIdsExitedAfterClose=true`、leak 0を成功条件へ追加した。schema/runtime validatorも個別証拠との矛盾を拒否する。 |
| S14-5 | 品質・運用性 | Major | PID markerを最終pathへ直接書き、readerは`File.Exists`直後に開くため、writer close前の共有違反を再現した。 | **修正済み:** PID markerをGUID付き一時fileへ書き、close後にatomic moveする。修正版self-testを5回連続実行し全回合格した。 |
| S14-6 | 品質・運用性 | Major | timeout/例外時に親だけの終了を待ち、子孫がredirected pipeを保持すると`ReadToEndAsync`が無期限化し得た。 | **修正済み:** process tree PIDを観測してkill後に全件をbounded waitし、内部probe子の標準handleを外側pipeから分離した。stdout/stderr drainにも10秒上限とcleanup後の再上限を設け、2秒timeout対照で子・孫終了を必須化した。 |
| S14-7 | 根拠性・不確実性管理 | Major | sourceを`-c copy`したcodec clipをMSIXへ含めながら`sourceMediaIncludedInPackage=false`と記録していた。 | **修正済み:** original全体は非同梱、source-derived excerptは同梱と分解し、aggregateをtrueへ訂正した。fixture rightsは`unconfirmed`、local technical evaluation限定、external publication不可をraw/canonicalとdistribution blockerへ保存した。 |
| S14-8 | 根拠性・不確実性管理 | Major | host固有のactive-limit異常とmemory値を記録したが、hardware/OS証跡へのimmutable参照がなかった。 | **修正済み:** hardware benchmark run ID/SHA-256、記録OS build、current OS build、architecture、Insider状態をfinal rawへ追加し、一致しないhostをruntime validatorで拒否する。 |
| S14-9 | 品質・運用性 | Major | ST-014 schemaはnested field名だけを要求し、hash文字列、負の時間、booleanの文字列化、host/rights/accounting欠落、PID証拠との矛盾を受理できた。 | **修正済み:** 型、範囲、SHA-256、URI、条件付きrequired、rights blocker、PID/active flag整合を追加した。6種類のnegative payloadと従来5種類の型負例を拒否し、過去raw 24件の後方互換を確認した。 |
| S14-10 | 根拠性・不確実性管理 | Major | WinApp CLIの暗黙BuildTools取得がTLSで失敗し、.NET SDK/publish依存も事後記録だけだった。WorkerProbeの`passed`は非空fileだけを意味した。 | **修正済み:** SDK 10.0.400を`global.json`で固定、locked restoreを使用し、BuildTools 10.0.26100.1のNuGet content hashとx64 MakeAppx hashを固定した。WinApp CLIは任意観測だけにし、WorkerProbeはexpected SHA-256一致時だけ成功する。 |

### 修正後の証拠照合

- final rawのrun ID/SHA-256、host/tool/model/package/executable hash、Job Object全項目、codec/model検証、rightsと未測定flagをcanonicalへ照合し、PASSを確認した。
- 修正版ProcessProbeを5回連続実行し、kill-on-close、caller cleanup、memory制限は5/5合格、自動終了は0/5、孤児processは0件だった。
- source fixtureは実行前後SHA-256一致。packageは192,170,312 bytes、SHA-256 `6c1b16505f18d2329ebe7f613eebf9939e94fd51421ad20e9550df8de9823666`で、raw/canonical/local fileが一致した。
- schemaは全historical raw 24件を受理しつつ、host欠落、rights欠落、空blocker、矛盾PID、active accounting欠落、矛盾`passed`の6負例と、型/hash/負値5負例を拒否した。
- 署名、install、package identity launch、AppContainer、upgrade、uninstall、production-channel clean Windowsは全て`false`／未測定で、PK-006/PK-007の完了とは表現していない。

### 採用しなかった指摘

- unsigned packageをinstallしてAppContainerまで検証しなければST-014未完了という指摘は、署名主体・証明書・clean Windowsが外部入力であり、正本がPK-006/PK-007へ分離しているため採用しなかった。未測定境界はcanonicalに列挙した。
- active-process limitの公式記述に合わせて結果を成功へ変更すべきという提案は、実機で自動終了もaccounting収束も観測できなかったため採用しなかった。結果を不採用として保持した。
- source-derived clipにGPS等が残ったと断定する指摘は、metadata key検査で該当keyを観測していないため採用しなかった。ただしrights未確認である事実だけで外部配布をblockした。
- MSIX、raw run、modelをGitへcommitすべきという指摘は、大容量・権利未確認・local rawを除外する既定方針に反するため採用しなかった。probe source、lock、schema、stage、canonicalだけを追跡する。

### ST-014 修正後判定

- 未修正 Critical: 0件
- 未修正 Major: 0件
- active-process limit: **不採用**
- process-memory 128 MiB: **候補値のみ（ST-015承認待ち）**
- package: **unsigned pack/unpack feasibilityのみ合格**
- 署名・install・AppContainer・clean Windows: **未測定**
- 合格判定: **PASS（測定タスク完了、release package完了の主張なし）**

## ST-006 日本語ASRの敵対的レビュー（2026年8月27日）

**対象:** `benchmarks/stages/measure-asr.ps1`、`asr_benchmark.py`、ベンチハーネス、schema、synthetic control、raw run、`benchmarks/results/asr-benchmark.json`

**採用raw証跡:** `20260827T0718012939475Z-asr-benchmark-1e66cc2c`、SHA-256 `a92491fd5c956151b335171413289b59c0d9d4098cf765501c463728786faf3c`

**実測:** 同一の16 kHz mono PCM 1,081.428秒を比較。faster-whisper small CPU int8は384.895秒、CUDA float16は46.719秒、Foundry Local 30秒window adapterは41.579秒（adapter 2.725秒、engine呼出合計38.854秒）。全3候補が日本語synthetic controlと固有名詞4/4を通過した。fixture正解文字起こしは存在しない。

| No. | 軸 | 重大度 | 指摘 | 修正と確認 |
|-----|-----|--------|------|------------|
| S6-1 | 根拠性・不確実性管理 | Critical | Foundry Localのone-shot長尺結果が3文字・duration `null`でも、非空textだけで全編処理と誤認できた。 | **修正済み:** one-shotは診断としてcoverage不成立に分類。30秒×37 windowの順序、総duration、元PCMとのbyte一致を検証した上で、`inputWindowSubmissionCoverageVerified=true`と`engineWholeInputCoverageVerified=false`を別記録した。submission coverageをengine処理coverageと表現しない。 |
| S6-2 | 品質・運用性 | Major | faster-whisper CUDAは対象PCに必要なcuBLAS/cuDNNがPATH上になく、初回試行が失敗した。単に「GPU非対応」とすると実機能力を誤分類する。 | **修正済み:** Foundry Local CUDA EP cacheの14 file、2,423,764,787 bytes、manifest SHA-256を検証し、当該子processのPATHだけへ注入。CUDA control・全fixture処理に成功した。一方、同cacheのrelease再配布承認は`false`とし、Stage 0 local実行と出荷可否を分離した。 |
| S6-3 | 整合性 | Major | 初期process計測はwrapper PID中心で、実際に推論する子Python/daemonのworking setを過小計測し得た。 | **修正済み:** rootと明示PIDから子孫を動的再探索し、aggregate working set、観測PID数、system-wide GPU memory scopeを記録。final rawはCPU 3 PID、CUDA 3 PID、Foundry最大6 PIDを観測した。GPU値はWDDMのsystem-wide観測でありper-process値と主張しない。 |
| S6-4 | セキュリティ | Critical | private transcript/audio work directoryが親ACLを継承すると、Authenticated Users等の広い主体が読める実在状態を許し得た。既存保護ACLへの再書込みは`SeSecurityPrivilege`を要求して失敗した。 | **修正済み:** 現在SID、SYSTEM、Administratorsの3主体だけへ継承FullControlを設定し、owner、非継承、規則数、権利を検証。設定関数を冪等化し、終了時はread-only検証だけ行う。子process作成fileにも広い主体の規則0件を確認し、final evidence directoryは3規則で保護された。 |
| S6-5 | 根拠性・不確実性管理 | Major | fixture正解文字起こしなしにCER/WERや固有名詞精度を比較できず、synthetic TTS controlをfixture精度の代替にする危険があった。 | **修正済み:** synthetic controlは機構確認と4固有名詞checkだけに限定し、`fixtureCerMeasured=false`、`fixtureWerMeasured=false`、`fixtureProperNounErrorRateMeasured=false`、production threshold未承認をraw/canonicalで強制。候補のfixture transcript hashが異なることも隠さず、精度順位を作らなかった。 |
| S6-6 | 品質・運用性 | Major | formal rerunで、trapが元例外を`ScriptHalted`へ置換、parser-validなinline `if`がruntimeでcommand扱い、ACL再設定失敗、正常な空stderrのbinding失敗が順に露見した。 | **修正済み:** 元`ErrorRecord`保持、CUDA後処理の事前代入、ACL設定/検証分離と冪等化、`AllowEmptyString`を実装。success/failure分岐、同一directoryへのACL二重適用、子process file継承、空stderr JSON childを小型対照で実行確認した。失敗rawは非書換えで保持し、全件source事後SHA-256一致・孤児0を確認した。 |
| S6-7 | セキュリティ | Major | ASR schemaが未知のnested propertyや失敗payloadを受理でき、`rawUtterance`等の別名で文字列を混入できる負例が成立した。 | **修正済み:** transcript近傍DTOを`additionalProperties=false`へ限定し、ASR失敗payloadは空objectだけを許可。runtimeでも敏感なproperty名をallowlist検査した。未知field付き成功payloadと内容付き失敗payloadをschemaが拒否し、最終時点の既存raw 32件の後方互換も維持した。 |
| S6-8 | 整合性 | Major | 子stageが失敗した場合、外側harnessはsourceの事後hashを取らず、失敗時の元動画不変性が未確認になり得た。 | **修正済み:** 成功・失敗の両経路でsize、mtime、SHA-256を再取得し、machine-readable envelopeへ`sourcePostRunIntegrityChecked`と`sourceUnchanged`を保存。final成功rawと修正中の全失敗rawで一致した。 |
| S6-9 | 根拠性・不確実性管理 | Major | 実行した未commit実装とST-003音声streamのimmutable参照がpayloadだけでは再構成できなかった。 | **修正済み:** Git HEADに加え、harness、wrapper、Python driver、schema、environment setup、control configのbyte size/SHA-256を記録。ST-003 raw run SHA-256と実audio stream値を参照し、final rawと現在fileを機械照合した。 |
| S6-10 | 内容の妥当性 | Minor | engineごとに公開能力が異なる成功条件と、Foundryだけに必要なadapter込み時間が、単純なengine同士の比較に読めた。Foundry model loadも独立測定していない。 | **修正済み:** canonicalへ`successCriteriaEquivalentAcrossEngines=false`、`crossCandidateEngineTimingComparable=false`、operational path限定のscope、Foundry model-load未測定を明記。adapter準備とengine呼出は別値のまま保持した。rawはimmutable evidenceとして書き換えていない。 |
| S6-11 | 根拠性・不確実性管理 | Major | ST-006が直接使うCTranslate2、huggingface_hub、tokenizers wheelとexact license本文、Foundry binary/model/EPの受入記録が不足していた。 | **修正済み:** 4 wheelのbyte size/SHA-256、exact tag license hash、faster-whisper model exact revision card、Foundry CLI/daemon/model/EP manifestを`third-party-intake.md`へ追加。Foundry catalogのlicense宣言と独立配布承認を区別し、CUDA EP再配布は未承認のままblockした。 |

### 修正後の証拠照合

- canonicalのrun ID/SHA-256、source/normalized audio/model/tool/implementation/artifact hash、候補件数・時間・memory・timestamp flagsをrawへ27項目で機械照合し、失敗0件だった。
- private artifact 3件のsize/SHA-256はrawと一致。private transcript文字列927件をraw envelopeへ完全一致検索し漏えい0件、raw/canonicalの絶対path pattern 0件を確認した。
- final evidence directoryには`stage-payload.json`とcompletion markerがあり、local payloadはraw payloadと意味的に一致した。work/raw/modelのtracked fileは0件だった。
- sourceは実行前後およびレビュー時のSHA-256がmanifestと一致。ASR/FFmpeg残存processは0、Foundry Whisper modelは事後unloaded、事前に起動済みだったserverは事後もreadyだった。
- PowerShell parser、Python `py_compile`、VS Code diagnostics、schema、全historical raw回帰、privacy負例、ACL lifecycle、空stderr childを検証した。

### 採用しなかった指摘

- 全engineへ同一のsegment/word timestamp成功条件を要求する提案は、時刻を返さないFoundryの能力不足を隠すか、評価対象から消すため採用しなかった。共通の日本語・固有名詞controlと、engine固有能力gateを分離し、FoundryはFR-002時刻providerとして保留した。
- Foundryの`fixtureCoverageMilliseconds`からadapter時間を除くべきという提案は、30秒window化が当該candidateを長尺へ適用するための必須operational pathであるため採用しなかった。代わりにadapter 2.725秒とengine 38.854秒を別記録し、cross-engine timingを同等比較不可と明記した。
- 子stage内でもJSON Schemaを再実行しなければ未検証という指摘は、外側harnessが成功rawの原子的保存前に`Test-Json -SchemaFile`を必須実行し、さらに`Assert-AsrPayload`を通す境界を見落としていたため採用しなかった。
- nested artifactの`text`をregexが検出しないという指摘は、patternが階層に関係なく`"text":`を検出し、runtime property guardとstrict nested schemaも存在するため採用しなかった。local artifactにtextがあること自体は意図したprivate evidenceである。
- complete transitive hash lock、clean Windows、署名、production CUDA bundleをST-006で完了すべきという提案は、PK-001/PK-004/PK-007/Q-010の後続範囲である。ST-006では直接runtime、model、実行binaryのhashと未承認境界までを固定した。

### ST-006 修正後判定

- 未修正 Critical: 0件
- 未修正 Major: 0件
- 未修正 Minor: 0件
- fixture CER/WER/固有名詞誤り率: **未測定（`transcript-reference.jsonl`待ち）**
- ST-007評価対象: **faster-whisper small CUDA float16（暫定）**
- fallback候補: **faster-whisper small CPU int8**
- Foundry timed ASR: **保留（engine時刻・処理durationなし）**
- CUDA release配布: **未承認**
- 合格判定: **PASS（測定タスク完了、最終採否・閾値はST-015承認待ち）**
