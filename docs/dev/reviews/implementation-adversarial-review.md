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
| 22 | 品質・運用性 | Minor | benchmark schema運用 | `1.0.0` schemaを将来どう変更するか規則がなく、過去runを同じ版の変更で壊す恐れがあった。 | **修正済み:** `docs/dev/benchmarks/result-schema-policy.md`で1.0内は任意追加のみ、breaking changeは新しいimmutable schema、過去run非書換えと定義した。 |
| 23 | 品質・運用性 | Minor | `schema.json`の`runId` | 任意文字列を許しており、時刻/stage/衝突回避suffixの形式を機械検査できなかった。 | **修正済み:** 現行生成規則に対応する正規表現patternを追加した。 |
| 24 | 根拠性・不確実性管理 | Minor | `docs/dev/licenses/third-party-intake.md` | Stage 0候補のPySceneDetectとMediaPipeについて、方針表にはlicenseを記載したが一次資料URLが一覧になかった。 | **修正済み:** 公式repository/documentationとlicense識別子を追加し、exact package/model版は採用時に固定すると留保した。 |
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
- 修正前のraw FFmpeg runに、後から追加した任意のarchive来歴fieldがないことは履歴改変の理由にしない。`docs/dev/benchmarks/result-schema-policy.md`どおり過去runを非書換えで保持し、canonical summaryは修正後run ID/SHA-256を参照する。
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
| S10-6 | 根拠性・不確実性管理 | Major | `mediapipe==1.0.1`の版だけがあり、実際のWindows wheelと同梱license textのcontent hashが受入記録になかった。 | **修正済み:** `mediapipe-1.0.1-py3-none-win_amd64.whl`、同梱LICENSE、model、正例assetの配布元・byte size・SHA-256を`docs/dev/licenses/third-party-intake.md`へ実測記録した。 |
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
| S16-4 | 根拠性・不確実性管理 | Major | YAMNet、speech control、Silero VAD、親`faster-whisper` wheelとdirect runtime wheelのartifact hash／license／notice条件が受入台帳になかった。 | **修正済み:** model/control/wheelのbyte size・SHA-256、exact tag license本文のURL・SHA-256、notice条件を`docs/dev/licenses/third-party-intake.md`へ記録し、canonicalのmodel/control/toolingから同節を参照した。 |

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
| S6-11 | 根拠性・不確実性管理 | Major | ST-006が直接使うCTranslate2、huggingface_hub、tokenizers wheelとexact license本文、Foundry binary/model/EPの受入記録が不足していた。 | **修正済み:** 4 wheelのbyte size/SHA-256、exact tag license hash、faster-whisper model exact revision card、Foundry CLI/daemon/model/EP manifestを`docs/dev/licenses/third-party-intake.md`へ追加。Foundry catalogのlicense宣言と独立配布承認を区別し、CUDA EP再配布は未承認のままblockした。 |

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

## ST-007 日本語ASR時刻の敵対的レビュー（2026年8月27日）

**対象:** `benchmarks/stages/measure-alignment.ps1`、`alignment_benchmark.py`、`alignment-control.json`、ベンチハーネス、schema、ST-006 private artifact、raw run、`benchmarks/results/alignment-benchmark.json`

**採用raw証跡:** `20260827T0947132960205Z-alignment-benchmark-cf2b2ab6`、SHA-256 `521c1de8a8a371bc17cec2ca2a84df5a1d1e6368dcb3eef955f753c97290ffa0`

**実測:** ST-006のCPU/CUDA artifactを再文字起こしせずhash固定で再解析。両方とも全emitted wordに有限時刻があり、segment/word構造違反0件、parent segment外へ0.14秒出たwordが各1件だった。独立4-slot TTS controlはCPU/CUDAとも4/4 slotを通過。CPU/CUDAのexact-token 4,667 pairではstart差中央値0秒、P90 0.04秒、P99 0.54秒、最大1.52秒だったが、正解注釈との誤差ではない。

| No. | 軸 | 重大度 | 指摘 | 修正と確認 |
|-----|-----|--------|------|------------|
| S7-1 | 内容の妥当性 | Major | 初期controlは短い固有名詞を孤立発話させ、2語を認識できなかった。次に同一ブランド文を4回連結するとWhisperの文脈抑制で語が脱落し、時刻機構と語彙精度を混同した。 | **修正済み:** 一般的な日本語4語を別々のTTS clipとして独立transcribeし、local timestampをmanifest offsetでglobal slotへ写像。controlは4/4成功した。語彙品質とexact boundary精度はcontrol対象外と明記した。 |
| S7-2 | 整合性 | Major | control manifestの`audio`は連結音声を示す一方、実際の成功controlは独立clipをtranscribeしており、入力を黙って連結音声と誤読できた。 | **修正済み:** `transcriptionInputMode=independent-slot-clips-mapped-through-manifest-offsets`と`constructedControlAudioWasTranscribed=false`をraw/schema/runtime/canonicalで必須化。各clipのhash、size、duration、full-input coverageを検証した。 |
| S7-3 | 整合性 | Major | `speechClipStartSeconds + local word time`のoffset算術は、clip durationとmanifest rangeが一致しなければ小さい誤差をslot余白が隠し得た。 | **修正済み:** manifestのspeech clip durationと実WAV durationを1 µs以内、artifact size/hash、transcribe入力durationとWAV duration、whole-input coverageを検査。mapped termがslotだけでなくspeech clip range内にあることを4/4×2経路で確認した。 |
| S7-4 | 根拠性・不確実性管理 | Major | CPU/CUDAはcontrol output hash・文字数、fixture output hash、segment/word countが異なるのに、CUDA選定が精度優位に読めた。 | **修正済み:** control 42/43文字、fixture 861/694 segment、5,254/5,266 word、output hash不一致を`crossDeviceOutputDivergence`へ明示。`selectionUsesOutputAccuracy=false`、時刻精度への含意は`unknown-without-independent-reference`とし、CUDAはST-006の能力・単回速度だけで暫定対象にした。 |
| S7-5 | 根拠性・不確実性管理 | Major | 日本語の反復tokenを`SequenceMatcher`で対応付けるため、離れた同一tokenを誤pairする可能性があり、最大差をaccuracy誤差として読めた。 | **修正済み:** `duplicateTokenPairingAmbiguityPossible=true`と`agreementIsAccuracyEvidence=false`を必須化。start差が0.1秒超304件、0.5秒超52件、1秒超11件をdescriptive diagnosticsとして追加し、閾値ではないと明記した。 |
| S7-6 | 内容の妥当性 | Minor | parent segment外wordの観測閾値1 msがcode上のmagic numberで、0.14秒の観測とsuccess/fallback判断の関係が不明だった。 | **修正済み:** `parentContainmentObservationThresholdSeconds=0.001`をraw/schema/canonicalへ記録。overflowをclampせず、word timingをeditable candidate、発話時刻＋手動調整を必須fallbackとした。 |
| S7-7 | セキュリティ | Major | 初回formal runはsafeな`transcriptionMilliseconds`を敏感fieldと誤判定し、alignment stageなのに「ASR payload」エラーを出した。 | **修正済み:** 共通guardを`Assert-NoUnexpectedSensitiveProperties`へ改名し、安全なhash/timing/capability/fallback fieldだけを明示allowlist化。失敗raw `20260827T0917364241819Z-alignment-benchmark-629a6602`（SHA-256 `2f78d15a1398d57905c6cf5fbd0c4bdaacdc3df4aeb78297716f840db99211ea`）は非書換えで保持した。 |
| S7-8 | セキュリティ | Minor | random work directoryは作成直後に親ACLを継承し、限定ACL設定まで短いwindowがある。 | **対応済み境界:** 現行.NET CoreにはWindows `DirectorySecurity`付き原子的directory作成overloadがないことを実機reflectionで確認。限定ACL設定完了直後にdirectoryが空であることを必須検査し、それ以前にfile/contentを作らない。最終directoryは3主体・非継承・広い主体0件。製品workspaceの原子的作成/別account試験はS-003で完了する。 |
| S7-9 | 根拠性・不確実性管理 | Minor | schemaがST-006時点から増え、未commit実装でrunしたためGit commitだけでは再現できないという指摘があった。 | **修正済み既存契約:** schema 1.0はoptionalなstage追加を許す方針で、全historical rawを同時検証する。各runは実行時schemaを含む実装6 fileのsize/SHA-256を記録し、現在fileへ照合した。Git cleanとは偽らず`implementationFilesCleanAtRun=false`を保持した。 |

### 修正後の証拠照合

- final raw SHA-256、source前後/current hash、ST-006 raw/canonical、CPU/CUDA artifact、実装6 file、local artifact 4件を独立照合し、全件一致した。
- final local evidence directoryはcompletion markerあり、ACL protected、3規則、広い主体0件、non-empty diagnostic 0件。alignment残存processとtracked private evidenceは0件だった。
- private control文字列17件をraw envelopeへ完全一致検索し漏えい0件、raw/canonicalの絶対path pattern 0件を確認した。local/raw payloadは`recordedAtUtc`表記正規化以外で一致し、同じ時刻instantだった。
- 4-slot controlはCPU/CUDAとも日本語、構造違反0、各assigned occurrence 1件、slot内・speech clip内を通過。連結音声をtranscribeしていないこともmachine-readableに固定した。
- canonicalのrun/upstream/implementation/control/fixture/agreement/disposition/privacyを最終rawへ11集約項目で機械照合し、失敗0件だった。
- PowerShell parser、PSScriptAnalyzer 1.25.0（warning/error 0件）、Python `py_compile`、parent runtime validator、JSON Schema、historical raw回帰を確認した。

### 採用しなかった指摘

- CPU/CUDAのoutput差の原因を特定しない限り未完了という提案は、正解文字起こしがなく原因を断定できないため採用しなかった。差自体と選定にaccuracyを使わないことを記録し、原因はopen questionへ残した。
- `SequenceMatcher`差を時刻精度として合格判定すべき提案は、同一tokenの対応が正解spanを保証しないため採用しなかった。descriptive observationだけに限定した。
- 0.14秒overflowをparentへ自動clampすべき提案は、元のmodel出力を隠し、意味のあるword/utterance境界を捏造し得るため採用しなかった。候補を保持して手動調整へ送る。
- directory作成前に空DACLを設定する提案は、提示手順も作成後`Set-Acl`でraceを解消せず、空DACLは実行主体自身をlock outし得るため採用しなかった。content作成前の限定ACL検証を現Stage 0の境界とした。
- schema hashがST-006と異なることをbreaking driftとする指摘は、stage enum拡張とoptional `$defs`追加が既存rawを狭めず、全historical rawが通ること、各runが実行schema hashを持つことを見落としていたため採用しなかった。

### ST-007 修正後判定

- 未修正 Critical: 0件
- 未修正 Major: 0件
- 未修正 Minor: 0件
- exact word/utterance boundary error: **未測定（独立timing annotation待ち）**
- word timing: **編集可能な候補として暫定利用可、production承認なし**
- fallback: **発話時刻＋手動字幕調整を必須維持**
- parent overflow: **0.14秒を各経路1件、非clamp**
- CUDA release配布: **未承認**
- 合格判定: **PASS（測定タスク完了、最終採否・閾値はST-015承認待ち）**

## ST-011 Foundry Local文章モデルの敵対的レビュー（2026年8月27日）

**対象:** `benchmarks/stages/measure-text-model.ps1`、`text_model_benchmark.py`、`local-text-model-control.json`、生成output schema、ベンチハーネス、schema、ST-006 private artifact、raw run、`benchmarks/results/local-text-model.json`

**採用raw証跡:** `20260827T1055523174852Z-local-text-model-b53e420a`、SHA-256 `6d8bc3275448e8a4528c62ab3d045c7756f63787b2d3415d10356701a76f1a36`

**実測:** 実在catalog/cacheから`qwen3-0.6b-cuda-gpu:2`と`qwen3-4b-cuda-gpu:2`を選び、19-token synthetic controlと、ST-006 CUDA artifactから決定論的に切り出したstart/middle/end 3 window（計501/5,266 word）を逐次評価した。0.6Bはaccepted JSON 0/4、4Bはsynthetic 1/1だけschema・ID構造を通したが期待3 topicに対し1 topicでcontrol不合格、fixtureは0/3だった。semantic ground truthはなく、採用候補0件、manual-onlyとした。

| No. | 軸 | 重大度 | 指摘 | 修正と確認 |
|-----|-----|--------|------|------------|
| S11-1 | 内容の妥当性 | Major | 当初はmodel IDを推測する余地があり、存在しないvariantで評価を捏造し得た。 | **修正済み:** installed CLI 0.10.3のJSON catalogを照会し、cache済みCUDA 2候補を実在確認。model info、個別file size/hash、manifest、publisher/license表示、execution providerを固定した。 |
| S11-2 | 整合性 | Major | PowerShell functionの1要素出力がscalarへunrollされ、loaded model IDの`[0]`が先頭文字を返し、正常loadを失敗判定した。 | **修正済み:** loaded-model集合の全call siteを`@(...)`で配列化。候補間でloaded model 0件、終了後0件、前後集合hash一致を最終rawで確認した。 |
| S11-3 | 根拠性・不確実性管理 | Major | 公式資料に記載されたtoken-count endpointが実機CLI 0.10.3でHTTP 404となり、補助endpoint失敗をmodel failureと誤分類した。 | **修正済み:** 404を`tokenCountEndpointAvailable=false`として記録しcompletionを継続。completion responseのusageを必須検証し、実機API差を能力結果から分離した。 |
| S11-4 | セキュリティ | Major | fixture本文をCLI argumentへ渡すとprocess command lineへ露出し得た。proxy経由や非loopback endpointも避ける必要があった。 | **修正済み:** dynamic server statusからliteral loopback HTTP endpointだけを許可し、proxyを無効化したREST bodyで送信。process argumentはACL保護済みinput file pathだけとし、endpointもdiagnosticで秘匿した。 |
| S11-5 | セキュリティ | Major | source/generated wordingがraw/canonicalやdaemon logへ残る可能性があった。 | **修正済み:** 3主体・非継承ACLのignored work directoryへinput/response/outputを隔離。payloadはhash/count/shapeだけにし、daemon log追記580 bytesをcanaryと146 exact probeで走査、match 0件を確認した。失敗payloadも空objectへ限定した。 |
| S11-6 | 内容の妥当性 | Major | free textから最初の`{...}`を拾う緩いparserは、trailing proseや複数objectを黙って採用し得た。Qwen reasoning prefix/code fenceとの境界も必要だった。 | **修正済み:** direct JSON、または閉じた`<think>` prefix後の単一JSON／完全なcode fenceだけをbounded adapterで許可。trailing prose、未閉鎖fence、長さ打切りはfail closed。failed responseには本文を出さずfinish reason、usage、hash、shapeを記録した。 |
| S11-7 | 根拠性・不確実性管理 | Major | JSON Schema適合と既知ID/range/subset検査をsemantic groundednessやFR完全受入と誤認できた。 | **修正済み:** schema、ID存在、重複、順序、non-overlap、topic内evidenceを構造検査として分離。semantic groundedness、hook意味保全、quiz回答support、FR-025全成果物は未測定、full requirement acceptanceではないとscope/raw/canonicalへ固定した。 |
| S11-8 | 内容の妥当性 | Major | 4Bはsyntheticでschema-validだったが、期待3 topicに対し1件だけだった。marker不在だけをprompt-injection robustnessと過大評価できた。 | **修正済み:** expected/observed topic rangeをopaque IDだけでmachine-readable化し、range/count不一致でcontrol失敗。`controlInjectionMarkerAbsenceIsRobustnessEvidence=false`を必須化した。 |
| S11-9 | 品質・運用性 | Major | memory観測不能時に`$null`を`[int64]`へcastすると0となり、未測定を0 MiBと誤記録し得た。GPU peakのpolling粒度も不明だった。 | **修正済み:** 空集合は`null`を保持。GPUはWDDM system-wide、1,000 ms sampling、短いpeakを過小観測し得ることをraw/canonicalへ記録した。 |
| S11-10 | 整合性 | Major | 「manual entry path available」は、計画に存在するだけなのに実装済み・実行済みに読めた。 | **修正済み:** `specifiedInImplementationPlan=true`、`implementedByBenchmark=false`、`executedByBenchmark=false`へ分離。ST-011ではmanual-onlyを決めるだけで、A-014/A-020/UI実装済みとは主張しない。 |
| S11-11 | 整合性 | Major | review追加fieldをschema 1.0のrequiredへ入れると、直前のimmutable rawを1件破壊した。 | **修正済み:** schema 1.0の追加fieldはoptionalへ戻し、新規runはruntime validatorで必須化。最終時点のhistorical raw 43件が全てschema-validであることを確認した。 |
| S11-12 | 根拠性・不確実性管理 | Major | catalogの`supportsToolCalling=true`を強制function call成功保証と誤認できた。実機0.6Bはlegacy function指定を無視した。 | **修正済み:** tool callingはcatalog宣言だけとしschema保証に使わない。実際のcompletionをstrict output schemaとID validatorへ通し、失敗をそのまま記録した。 |

### 修正後の証拠照合

- final raw、source前後hash、ST-006/ST-007 upstream、実装6 file、model 17 file、CUDA EP manifestを照合し、全件一致した。
- 0.6B load 2,548.547 ms、4B load 4,246.772 ms、4 dataset推論合計は76,380.817 ms / 191,193.778 msの単回観測。最大aggregate working setは3,138,379,776 / 5,864,386,560 bytes、system-wide GPU peakは7,159 / 7,801 MiBだった。
- 0.6Bは4 responseすべてcompletion上限1,536 token到達。4Bもfixture 3件は上限到達し、syntheticだけ283 tokenでstopした。失敗response本文は公開せずsafe shape/usage/hashで分類した。
- 3 fixture windowは178/162/161 word、pairwise overlap 0。全fixture semantic coverageは主張せず、human semantic referenceも存在しない。
- Foundry serverは事前/事後ready、loaded modelは事前/事後0、候補間unload成功。model cache manifestの事後変化0件。
- PowerShell parser、PSScriptAnalyzer 1.25.0（warning/error 0件）、Python `py_compile`、parent runtime validator、JSON Schema、全historical raw回帰、privacy負例、source不変、private Git境界を確認した。
- 3軸レビュー反映後の最終再レビューで未解決指摘0件だった。

### 採用しなかった指摘

- code fence内の最初のJSONを見つけ、後続proseを無視して採用する提案は、複数objectや注釈を黙って受理してfail-closed境界を弱めるため採用しなかった。完全なsingle outputだけを許可する。
- JSONを生成できなかったresponseをbest-effortで意味評価する提案は、schema契約を満たさない候補を部分採用可能に見せるため採用しなかった。safe response shapeは記録するがvalidationは`null`のまま失敗とする。
- synthetic controlを通すため期待topic数やrangeを実測後に緩和する提案は、testを結果へ合わせることになるため採用しなかった。
- fixture windowを19 tokenまで縮めて成功させる提案は、実用入力との差を隠すため採用しなかった。現在の失敗を保持し、別prompt/modelは新しい評価候補として扱う。
- ST-011でFR-006/011/022/025の全fieldとsemantic accuracyを実装・承認すべきという指摘は、当taskが代表的な根拠ID付きJSON候補の技術spikeであり、完全実装はA/Eタスク、人手閾値承認はST-015であるため採用しなかった。代わりに未評価scopeを明示した。

### ST-011 修正後判定

- 未修正 Critical: 0件
- 未修正 Major: 0件
- 未修正 Minor: 0件
- structurally eligible candidate: **0 / 2**
- fixture accepted JSON: **0 / 6 model-window combinations**
- semantic groundedness / wording accuracy: **未測定**
- local text model: **不採用（manual-only）**
- model/CUDA release配布: **未承認**
- fallback: **手動topic metadata・hook・quiz・publishing wording作成を必須維持（実装は後続）**
- 合格判定: **PASS（測定タスク完了、モデル採用のPASSではない。最終採否・閾値はST-015承認待ち）**

## ST-012 手書きmanifest renderの敵対的レビュー（2026年8月27日）

**対象:** `benchmarks/stages/measure-manual-render.ps1`、`manual_render_benchmark.py`、`manual-render-manifest.json`、neutral ASS、ベンチハーネス、schema、raw run、`benchmarks/results/manual-render.json`

**採用raw証跡:** `20260827T1136430750369Z-manual-render-a94e7fa7`、SHA-256 `76a5041b9ba49cfea56cd59c0eb1f09c77037eb1cc682e5d3b6678bf3753f438`

**実測:** 16:9は5秒×2 source区間のtrim/concat、1280×720 scale、neutral ASS、0.5 audio gain、9:16はsource中央1080×1920 exact cropを実行。各compositionを2回renderし、4 output全てH.264 Main/AAC 48 kHz stereo、全編decode成功。同一session内でencoded bytesとdecoded video/audio hashが一致した。observed output/source PTSで4点を逆引きし、source-frame選択誤差は最大0.014166667秒、全点で5秒offset controlよりmapped frameのgrayscale MADが小さかった。

| No. | 軸 | 重大度 | 指摘 | 修正と確認 |
|-----|-----|--------|------|------------|
| S12-1 | 内容の妥当性 | Critical | 初期設計案のcrop `1280x2160`は9:16ではなく、1080×1920へscaleすると歪みを生む。 | **修正済み:** source 3840×2160から中央`x=1380,y=120,w=1080,h=1920`を切り出すexact 9:16 cropへ変更。manifest validatorで値と比率を固定した。 |
| S12-2 | 内容の妥当性 | Critical | 初期filter graphはconcat入力を全video後に全audioと並べたが、FFmpeg concatはsegmentごとの`[v][a]`交互入力を要求する。 | **修正済み:** `[v0][a0][v1][a1]concat=n=2:v=1:a=1`へ修正。output 7.5秒のsampleが第2 source区間へ逆引きされ、frame対照も通過した。 |
| S12-3 | 整合性 | Major | 全10秒を丸めた300 frameを期待すると、各5秒segmentへ独立に`fps`を適用した実graphの298 frameを誤って失敗扱いした。 | **修正済み:** 各segmentの`floor(duration×rate)`を合計し、landscape 298、vertical 299をprofileの有理frame rateから導出。失敗rawは非書換えで保持した。 |
| S12-4 | セキュリティ | Major | subtitle pathをfilter graphへ任意挿入するとpath traversal/filter injectionが可能になる。 | **修正済み:** exact basename `manual-render-subtitles.ass`だけを許可し、basename一致、file hash、source transcript非由来を検査。FFmpegはfixture directoryをworking directoryにして単一quoted filenameを使う。 |
| S12-5 | 根拠性・不確実性管理 | Major | nominal fpsやmanifest算術だけで元時刻逆引きを「確認済み」とするとVFR sourceで循環論証になる。 | **修正済み:** 固定ffprobeでsource/output packet PTSを再採取し、observed output PTS→piecewise manifest→observed source PTSの4点を検査。独立にexpected transformed source frameと5秒offset controlを比較した。 |
| S12-6 | 根拠性・不確実性管理 | Major | frame MADを知覚品質や全scene mapping精度として過大評価できる。 | **修正済み:** 320×180 grayscale MADは4点のcontent correspondence controlだけと明記。perceptual quality、全frame accuracy、production閾値は未測定。 |
| S12-7 | 内容の妥当性 | Major | subtitle filterが実行成功しても、実際に表示された証拠にならない。 | **修正済み:** active 1.001秒とinactive 2.5025秒を、それぞれ同時刻のsubtitleなしsource transformと比較。active差2.214983、inactive差0.855069でmechanismを確認した。subtitle文はneutralで、source speech traceabilityは未測定。 |
| S12-8 | 内容の妥当性 | Major | `volume=0.5`をargumentに含めるだけではaudio filter適用の証拠にならない。 | **修正済み:** 同じsource区間のno-gain referenceとoutputを`volumedetect`で比較し、期待-6.0206 dBに対し-6.1 dB、絶対差0.0794 dBを観測。1 dB toleranceはStage 0 control値でproduction未承認とした。 |
| S12-9 | 根拠性・不確実性管理 | Major | 同一sessionでbyte一致してもcross-machine/GPU/driver再現性を保証しない。 | **修正済み:** `sameMachineGpuDriverObservation=true`、`crossMachineOrDriverReproducibilityMeasured=false`、byte identityはtask completion必須でないことをraw/canonicalへ明記した。 |
| S12-10 | セキュリティ | Major | source-derived video/frame/audio、PTS、stderr、command lineがpublic payload/Gitへ漏れる可能性がある。 | **修正済み:** 3主体ACLのignored work directoryへ隔離し、payloadはhash/aggregateのみ。`-map_metadata -1`、absolute path/command/stderr拒否、source前後hash、private Git tracking 0を検査した。 |
| S12-11 | 品質・運用性 | Minor | GPU memoryや画質閾値を本taskへ持ち込むと、render mechanismの合否とdelivery品質を混同する。 | **対応済み:** ST-012はrender時間、size、decode、hash、PTS、mechanism controlsを記録し、perceptual qualityとproduction thresholdsをST-015以降へ残した。 |
| S12-12 | 根拠性・不確実性管理 | Minor | neutral subtitle controlをFR-032の字幕・要約content-level traceability完成と誤読できる。 | **修正済み:** `subtitleSourceSpeechTraceabilityMeasured=false`、`contentLevelOriginRefTraceabilityMeasured=false`を追加し、機械的なoutput→source時刻写像だけを実証範囲とした。 |

### 修正後の証拠照合

- landscape 2回は3,812,277 bytes、SHA-256 `b2410563edbc750bff60ef74c36b329dfdfbfbbcc9842a89dfe026af6d983b4c`で一致。vertical 2回は9,824,562 bytes、SHA-256 `2d4aa9f41f6650b92ca87be04f113bd7e6f853e4cafa64f1874014e2c47134e4`で一致。decoded video/audio hashも各composition内で一致した。
- landscapeは298 frame/10.0秒、A/V end差0.023367秒。verticalは299 frame/9.976633秒、A/V end差0.014633秒。4 outputとも全編decode成功。
- reverse-map error最大はlandscape 0.014166667秒、vertical 0.005833333秒。4/4 sampleで5秒offset controlがmapped MADを上回った。
- subtitle active/inactive control、audio gain control、manifest全5 operation、exact 9:16 cropが通過した。
- source-derived artifactはignored work内、ACL 3規則・非継承・unexpected 0。source前後hash一致、absolute path/command argument/source pixel/audio sample/transcriptのpayload混入0件。
- PowerShell parser、PSScriptAnalyzer 1.25.0（warning/error 0件）、Python `py_compile`、parent runtime validator、JSON Schema、historical raw回帰を確認した。
- 3軸レビューと反映後再レビューで未解決指摘0件だった。

### 採用しなかった指摘

- planning memoryにあったsynthetic color canaryを「実装必須」とする指摘は、正本taskの完了条件ではなく、tracked manifestにも含めていないため採用しなかった。実sourceのpiecewise sampleと5秒offset controlで今回のmappingを検査した。
- byte identityをtask必須gateにする提案は、NVENC/driver差を超えて一般化できないため採用しなかった。同一sessionの観測としてのみ保存した。
- MADへ普遍的数値thresholdを設定する提案は、知覚品質基準が未承認のため採用しなかった。各sampleでmapped/controlを相対比較し、値をそのまま保存した。
- neutral subtitleを元発言へ結び付ける提案は、source transcriptをtracked fixtureへ入れ、ST-012のprivacy/scopeを越えるため採用しなかった。content-level OriginRefは後続D/R/Qタスクで検証する。

### ST-012 修正後判定

- 未修正 Critical: 0件
- 未修正 Major: 0件
- 未修正 Minor: 0件
- required operations: **trim / concat / crop / subtitle / audio 全て実行**
- 16:9 / 9:16 outputs: **各2回、全編decode成功**
- output→source reverse map: **4/4 sample、source-frame選択誤差最大0.014166667秒**
- same-session reproducibility: **encoded/decoded hash一致**
- perceptual quality / content-level traceability / production thresholds: **未測定・未承認**
- 合格判定: **PASS（測定タスク完了、最終profile採否はST-015承認待ち）**

## ST-013 WebView2 preview fidelityの敵対的レビュー（2026年8月27日）

**対象:** `benchmarks/web-preview/index.html`、`benchmarks/probes/WebPreviewProbe/`、`measure-preview-fidelity.ps1`、`preview_fidelity_benchmark.py`、共有LayoutSpec/Cue、ベンチハーネス、schema、raw run、`benchmarks/results/preview-fidelity.json`

**採用raw証跡:** `20260827T1233421361375Z-preview-fidelity-0ce91abd`、SHA-256 `51dac9064e0a9ab3d4e58ac035836dfd2ab927c904874e2d0afefa90d9c475a0`

**実測:** actual Evergreen WebView2 `151.0.4129.107`でCanvas/SVGを各3時点captureし、同じ正規化LayoutSpec/Cueから作った2.002秒・60 frameのFFmpeg H.264 Media Foundation区間と6組を比較した。WebView2対FFmpegの平均8-bit channel差は7.861054〜9.204768、Canvas対SVGは2.331489〜2.979348。いずれもpixel同一ではない。3 rendererすべてでactive/inactiveのCue領域差が領域外差を上回った。

| No. | 軸 | 重大度 | 指摘 | 修正と確認 |
|---|---|---|---|---|
| S13-1 | セキュリティ・運用性 | Major | WebView2 capture完了直後に専用user-data directoryを削除すると、browser processが一時fileを保持して削除が失敗した。 | **修正済み:** probeが`BrowserProcessExited`をbounded waitし、resource解放後に専用profileを削除してから結果を返す。修正後formal rawで`browserProcessExitObservedBeforeHostResult=true`、`userDataDirectoryRemoved=true`、private evidence上のdirectory不在を確認した。 |
| S13-2 | セキュリティ・根拠性 | Major | preview表示にはsource-derived pixelsをWebView2へ渡す必要がある一方、単に`sourcePixelsIncludedInPayload=false`と書くとlocal rendererへの転送まで否定したように読めた。 | **修正済み:** `sourcePixelsTransferredToLocalWebView2=true`を明示し、raw/canonicalへの非包含と区別した。documentへ`default-src 'none'`、`connect-src 'none'`等のCSPを追加し、単一file navigationとpopup遮断も維持した。 |
| S13-3 | 整合性 | Minor | probe buildは`benchmarks/probes/global.json`でSDK 10.0.400を固定していたが、実装hash集合に同fileがなかった。 | **修正済み:** `global.json`を10件目のimplementation fileとしてbyte size/SHA-256照合し、rawへ保存した。 |
| S13-4 | 根拠性・不確実性管理 | Minor | 125% DPIでCSS viewport 1024×576、physical capture 1280×720だったため、単一DPI観測を他のscaleへ一般化し得た。 | **修正済み:** host DPI/devicePixelRatio/CSS viewport/capture寸法を分離して記録し、`crossDpiScaleReproducibilityMeasured=false`をruntime/canonicalで必須化した。96 DPI限定という未承認条件は追加していない。 |
| S13-5 | 整合性 | Minor | review追加fieldをschema 1.0のrequiredへすると、直前のimmutable ST-013 rawを破壊する。 | **修正済み:** schema上はoptional追加とし、current runtime validatorで10-file provenance、local pixel transfer、CSP境界、cross-DPI未測定を必須化した。全historical rawを再検証した。 |

### 修正後の証拠照合

- final raw、source前後hash、ST-004/ST-012/FFmpeg upstream、実装10 file、WebView2 SDK/runtime/font、private artifact 13 descriptorを照合し、失敗0件だった。
- captureはCanvas/SVG各3件、全て1280×720。host DPIとdevicePixelRatioは1.25、CSS viewportは1024×576で、全reported rectangleとCue stateが共有contractへ一致した。
- faithful intervalはH.264 Constrained Baseline、yuv420p、30000/1001、2.002秒、60 frameで全編decodeした。sample選択差は0.001〜0.018433秒だが、合否閾値には使用していない。
- Cue機構対照はCanvas、SVG、FFmpegの全てで2つのinactive controlに対して分離した。これはCue表示機構の相対対照であり、知覚品質やproduction acceptanceではない。
- source-derived frame、6 capture、3 faithful sample frame、interval video、diagnosticはignored work内の3主体・非継承ACLで保持し、WebView2 profileは削除した。raw/canonicalにpixel、audio、transcript、Cue text、絶対path、command argumentは含まない。
- PowerShell parser、PSScriptAnalyzer 1.25.0、Python `py_compile`、.NET warning-as-error build、runtime validator、JSON Schema、historical raw回帰を確認した。3軸の最終再レビューで実在する未解決指摘は0件だった。

### 採用しなかった指摘

- FFmpeg Cueが実画像で未検証という指摘は、FFmpeg active frameと前後2つのinactive frameを比較したCue領域/領域外の相対対照がrawにあり、runtime validatorも3 renderer全件を要求するため採用しなかった。
- 96 DPI以外を失敗にすべきという指摘は、正本にその閾値がなく、今回の目的は差分を記録可能にすることだから採用しなかった。観測scaleを固定せず記録し、cross-DPIを未測定とした。
- source pixelsをWebView2へ渡さない提案は、preview自体がsource画像を表示する要件と矛盾する。外部通信を遮断したlocal rendererへの必要な転送と、raw/canonical・Git・外部への開示を分離した。
- `sourcePixelsTransferredToLocalWebView2`と`sourcePixelsTransferredOnlyToLocalWebView2`は、capture事実とprivacy境界という異なるscopeを表すため統合しなかった。final rawには両方がありschema/runtime検証済みである。
- 任意のpixel差、時間差、geometry差をproduction合否閾値にする提案は、人手承認済み基準がないため採用しなかった。値をdescriptive evidenceとして保持する。

### ST-013 修正後判定

- 未修正 Critical / Major / Minor: **0件**
- actual WebView2 Canvas/SVG capture: **各3件**
- FFmpeg faithful static-frame interval: **2.002秒、60 frame、全編decode成功**
- WebView2対FFmpeg / Canvas対SVG差分: **記録済み、pixel同一性なし**
- Cue active/inactive mechanism control: **3 renderer全て合格**
- moving-video / audio / perceptual quality / cross-DPI/runtime/GPU / production threshold: **未測定・未承認**
- 合格判定: **PASS（測定タスク完了、preview方式と閾値の採否はST-015人手承認待ち）**
