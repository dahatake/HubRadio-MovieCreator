# 実装進行成果物 敵対的レビュー

**対象:** `docs/`、`benchmarks/`、ルート設定の2026年8月27日時点スナップショット  
**目的:** Windows 11ローカル優先アプリの全実装計画を、各タスクの検証・敵対的レビューを伴って実行すること  
**レビュー方針:** 5軸を独立に監査し、実ファイル・実行結果で裏付けられない候補、未着手と明記された将来タスク、単なる好みを除外した。  

未実行のST-003以降やアプリ本体は、現在の計画が未完了と明記しているため、それ自体を完成済み成果物の欠陥として水増ししていない。進捗表示や完了主張が実態と矛盾する箇所だけを問題にした。

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
