# Stage 0 採否・フォールバック・残余リスク報告

- タスク: ST-015
- 状態: **人手承認待ち**
- 証拠基準日: 2026年8月27日
- 対象: Windows 11 x64単一PC上のStage 0技術検証
- 製品アプリケーション: 未実装
- Azure resource: 未作成

## 1. 判定の意味

本報告は、Stage 0で実測した機構をStage 1の実装入力として採用、保留、手動化、禁止へ分類するための承認資料である。§3〜§6の分類と規則はすべて**未承認の提案**であり、§9の全項目が人に承認されるまで決定として扱わない。benchmarkの`status=succeeded`は測定処理が完了したことを示し、モデル精度、知覚品質、production閾値、外部公開、再配布、release readinessの承認を意味しない。

現時点では、独立した文字起こし・時刻・shot・face・audio event正解注釈と、人による視聴品質基準がない。したがって、**数値のproduction合否閾値は設定しない**。Stage 1は、以下の手動fallbackとfail-closed条件を保持する場合に限って開始候補とする。production/release閾値は注釈・実装・受入試験が揃うまで未承認のまま維持する。

## 2. Evidence register

| 区分 | Canonical | 採用raw run | Raw SHA-256 |
|---|---|---|---|
| Hardware | `hardware-inventory.json` | `20260826T1548073271369Z-hardware-d1b1bd37` | `3e5b1e6ab9ce19795b8244bd433ab8882b534e4b06b9314d7d86ac67536775c7` |
| FFmpeg capability | `ffmpeg-capabilities.json` | `20260826T1637448588203Z-ffmpeg-capabilities-7ddd373d` | `11b9b63c656ac12eb4c4f945b64b851c9ac99a03959fc7308c3103ecb88b56df` |
| Media ingest | `media-ingest.json` | `20260826T1945575983887Z-media-ingest-64a77bc8` | `f47f5f89dacc86fc6c73acd83f6009347235af697b5d00837935693d705c0c09` |
| Proxy frame map | `proxy-frame-map.json` | `20260826T2102079908030Z-proxy-frame-map-5a725e5c` | `a581baa89950dfb231283ddf8fa958dc513e6dafc758625c7c05996d203cee69` |
| Encoder matrix | `encoder-matrix.json` | `20260826T2146550053095Z-encoder-matrix-bfe154ce` | `12f4bedf9d2aa1116b96dca736de0c8984222a9c0d81514b7a7f61f14295e445` |
| ASR | `asr-benchmark.json` | `20260827T0718012939475Z-asr-benchmark-1e66cc2c` | `a92491fd5c956151b335171413289b59c0d9d4098cf765501c463728786faf3c` |
| Alignment | `alignment-benchmark.json` | `20260827T0947132960205Z-alignment-benchmark-cf2b2ab6` | `521c1de8a8a371bc17cec2ca2a84df5a1d1e6368dcb3eef955f753c97290ffa0` |
| Shot detection | `shot-detection.json` | `20260826T2259012687261Z-shot-detection-5f160fd6` | `2be6879b2d3c964bca80ff86e3841a0d1b4abb7b37af3beadf11afa0a9b67bb3` |
| Face detection | `face-detection.json` | `20260827T0008446257559Z-face-detection-df3aece7` | `cae1e0f0e4f25f02cf8959802a2302245951a543e48f16a582cd0c4fe120728e` |
| Audio event / recording quality | `audio-event-and-recording-quality.json` | `20260827T0046465037091Z-audio-event-and-recording-quality-54f1f69b` | `c2a23164b490c197e2f225213bc9d8cd4ffef9bfcd1b990b7c6784663d3a6d73` |
| Process / package | `process-and-package.json` | `20260827T0302246003561Z-process-and-package-2ba57f42` | `691536494a83777cdaf3d930368bff7fa583e754305251d31a77f1bb8259521b` |
| Local text model | `local-text-model.json` | `20260827T1055523174852Z-local-text-model-b53e420a` | `6d8bc3275448e8a4528c62ab3d045c7756f63787b2d3415d10356701a76f1a36` |
| Manual render | `manual-render.json` | `20260827T1136430750369Z-manual-render-a94e7fa7` | `76a5041b9ba49cfea56cd59c0eb1f09c77037eb1cc682e5d3b6678bf3753f438` |
| Preview fidelity | `preview-fidelity.json` | `20260827T1233421361375Z-preview-fidelity-0ce91abd` | `51dac9064e0a9ab3d4e58ac035836dfd2ab927c904874e2d0afefa90d9c475a0` |

Raw runとsource-derived artifactはGit対象外であり、canonicalだけを追跡する。過去rawは失敗を含めて書き換えない。

## 3. 提案するStage 1分類

以下は**未承認の承認案**であり、この文書だけでは確定しない。表中の「採用」「不採用」は承認後に適用する提案分類であり、現在の確定状態ではない。

| 対象 | 提案分類 | 実測根拠 | Stage 1の境界 |
|---|---|---|---|
| VFR・時刻 | **採用** | sourceは公称59.94 fpsに対して平均57.62 fps、packet duration 44種、映像開始`-0.083333`秒、音声開始`-0.044`秒 | 正本時刻は観測PTSと有理数。frame番号÷公称fpsを禁止し、負の開始offsetを保持する |
| 代理動画とframe map | **採用** | 640×360、30000/1001、80,042,595 bytes。62,311点の往復誤差は中央値`0.033333333`秒、最大`0.635`秒 | `0.635`秒をproduction合否値にしない。proxyで選択し、source側の観測PTSへ必ず解決する |
| FFmpeg build | **条件付き採用** | 固定LGPL buildに必要filter、AAC、NVENC、Media Foundationが存在し、GPL encoderなしで出力可能 | Stage 1開発baseline。PK-003/Q-010でsource/config/notice/SBOM証拠を作り、指定された組織のrelease承認者が再配布義務・codec特許を承認するまで出荷不可 |
| H.264 encoder | **NVENC優先候補 + Media Foundation必須fallback** | 30秒区間で両方decode成功。NVENCは10,113,517 bytes・VMAF平均96.2248、MFは15,497,724 bytes・平均95.4591。設定はCQ対固定bitrateで同条件ではない | hardware/runtime失敗時はMFへ戻し、実際のencoder・profileをRenderManifestへ記録。品質順位を他環境へ一般化しない |
| Local ASR | **faster-whisper smallを条件付き採用** | CUDA 46.719秒、CPU 384.895秒の単回観測。両方が全入力とsegment/word timestampを返した | CUDAは測定機の暫定primary、CPUは必須fallback。CER/WER未測定。ST-015はCUDA配布を承認しない。PK-001/PK-004で権限あるlicense/package承認者と判断期限を記録し、正規の再配布可能runtimeを固定できなければrelease候補をCPU-onlyにする。現在のFoundry cacheはどちらの場合も無断copyしない |
| Word timing | **編集可能candidateとして採用** | emitted wordの有限timestamp coverage 100%、構造違反0。CPU/CUDA各1語でparent外`0.14`秒、output自体も不一致 | 自動確定・clampを禁止し、utterance timingと手動字幕調整を常設する |
| Manual render mechanisms | **採用** | 16:9/9:16、trim/concat/crop/subtitle/audio gain、全4 output decode、4点の元PTS逆引き | typed Render IRの根拠にする。知覚品質、content-level OriginRef、cross-machine再現性は未承認 |
| Preview architecture | **機構を条件付き採用** | actual WebView2 Canvas/SVG各3 captureとFFmpeg 2.002秒/60 frame区間が同一LayoutSpec/Cueを消費し、6組の差分を記録 | 直接previewは編集用、差がある場合の承認対象はfaithful interval。moving-video、audio、cross-DPI/runtime/GPU、知覚品質は後続試験まで未承認 |
| Job Object timeout / kill-on-close / host-crash cleanup | **採用** | timeout時の明示process-tree終了、最後のJob handle close、host `FailFast`の3経路でchild/grandchild終了、観測孤児0 | J-004はassigned descendantへkill-on-closeを適用する。J-003/J-004のcallerはtimeout・割当拒否・例外時に明示killとbounded waitを行う。active-process limit、自動終了、accounting収束には依存しない |
| Multi-executable MSIX layout | **feasibilityのみ採用** | unsigned packageのpack/unpack、2 first-party executable、codec/model検証が成功 | Packaging Project方針を継続するが、署名・install・upgrade・uninstall・clean-machineを完了済みとしない |
| Technical ingest warnings | **review signalとして採用** | full decode error 0、deterministic clipping/channel checksとcontrolが動作 | warningを削除確定へ変換しない。元媒体の技術診断として人へ提示する |

## 4. 保留・手動化・禁止

| 対象 | 分類 | 理由と必須fallback |
|---|---|---|
| pyannote diarization（ST-008） | **未実行・手動化** | `docs/dev/licenses/third-party-intake.md`に利用条件不同意・未取得を記録済み。匿名speakerを人が出演者へ割り当てる。将来、権限ある人が条件へ明示同意し、license/hashを固定した場合だけ別taskとして再検討する |
| Foundry Local Whisper timing | **保留** | 37 windowのPCM submissionは確認したがengine duration/segment/word timestampなし。FR-002時刻providerにしない |
| PySceneDetect threshold | **未選択・保留** | fixtureは3構成とも境界0件、synthetic hard-cut controlは各3/3。正例annotationなしにdetector/thresholdを選ばない。transcript/audio/manual boundaryを主経路にする |
| MediaPipe full-frame short-range face detection | **自動reframeへ不採用・手動化** | positive control 1件に対しfixture 300/300 frameで検出0。manual layout/keyframeを必須にする |
| Qwen3 0.6B/4B local text suggestions | **不採用・manual-only** | structurally eligible 0/2、fixture accepted JSON 0/6。topic metadata、hook、quiz、publishing文言は人が作成・確認する |
| Audio event candidates | **一部保留・人手確認のみ** | wait候補は複数方式で重なったが正解ではない。YAMNet coughは1件のmodel-only候補で正例・聴取なし | 自動削除を禁止。候補時刻へ移動して人が試聴し、採否を確定する |
| Active-process Job limit | **不採用** | 2つ目の割当はerror 1816で拒否されたが自動終了せず、caller cleanup後もJob accountingが収束しなかった | 同limitへ依存せず、runner側の同時実行制御と明示cleanupを使う |
| 128 MiB process-memory limit | **値を保留** | controlled probeでは作用したがproduction workload値ではない | workload別計測まで数値を固定しない |
| AppContainer | **保留・未測定** | unsigned packageをinstallせず、workspace/tool accessを試験していない | 初期はfull-trust desktop + Job Object。AppContainerをsecurity境界と主張しない |
| 数値のproduction品質・精度閾値 | **全て未承認** | 正解注釈、人の品質評価、複数fixture、複数環境が不足 | 合否を捏造せず、該当機能をmanual reviewまたはrelease blockへ送る |
| Azure | **非採用・gate closed** | local text modelの機能評価は不達だが、要求を満たすmanual pathを実装する提案がある。手動作業量は未測定であり、Azure necessity・送信範囲・利用者の作成指示はいずれも成立していない | model不達だけではZ-001/Z-005を自動開始しない。manual pathでは要件を現実的に満たせないという追加証拠と明示指示が揃うまでsubscription、region、modelを選択せずresourceを作成しない |

## 5. 手動fallback契約

§9-Bで承認された場合、次をStage 1の**設計・受入契約**とする。これは現時点で実装済みという意味でも、F-001 scaffold前に8経路を実装するという意味でもない。各fallbackはStage 1で対応機能と同時に実装し、その機能を有効化する前か、遅くとも関連する`G1-MUST`受入前に試験する。

1. ASR word timestampは候補として表示し、utterance単位時刻と手動字幕調整を常に利用可能にする。
2. speakerは手動割当を主経路にする。
3. topic境界はtranscript/audio規則と手動操作を主経路にし、visual cutだけへ依存しない。
4. 縦型reframeはmanual layoutとmanual keyframeを主経路にする。
5. topic名、要約、hook、quiz、投稿文はmanual authoringを主経路にし、ST-011のmodelを有効化しない。
6. 咳・noise・wait・channel差はreview候補またはtechnical warningに限定し、自動削除しない。
7. Canvas/SVG previewと忠実区間に差がある場合、忠実区間を承認対象にする。
8. NVENCを利用できない場合はMedia Foundationへfallbackし、encoder/profile差を記録する。

## 6. 閾値の承認案

### 今回承認できるもの

数値ではなく、次の**判定規則**だけをStage 1の安全条件として承認候補にする。

- source不変、schema、hash、full decode、PTS単調性、既知control、ACL、path/content非漏えいを機械gateにする。
- 正解注釈のないaccuracy値、raw pixel差、CPU/CUDA agreement、model score、VMAF単回値をproduction合否値にしない。
- 自動化が未採用・失敗・不明の場合は、上記の手動fallbackへ戻す。
- 技術再renderと内容変更を区別し、内容変更時は承認を失効する。

### 未測定のため承認対象にできない数値

- ASR CER/WER、固有名詞誤り率、word/utterance境界誤差
- shot precision/recallとdetector threshold
- face precision/recall、bbox error、採用confidence threshold
- cough/noise/wait precision/recall、model/rule threshold
- 知覚品質、moving-video seek/playback latency、audio preview品質
- loudness/true peak、concurrency、UI response、RPO/RTO、cross-machine/DPI/runtime分布

### 測定済みだがproduction合否値として未承認の数値

- proxy往復誤差分布と最大source frame duration
- CPU/CUDA output差・一致分布・単回処理時間
- encoder VMAF、size、時間（CQと固定bitrateの異なる設定による単一区間比較）
- manual renderのA/V end差、audio gain誤差、同一環境hash一致
- previewのraw pixel差、sample frame選択差、単一DPI/runtimeでのcapture値
- 128 MiB process-memory probeと各detector/modelのStage 0候補score/threshold

いずれも未承認のままにし、対応するannotation、複数fixture、人の評価、Stage 1受入試験で決める。現在の測定値をそのままproduction閾値へ転用しない。

## 7. Stage 1開始条件とrelease blocker

### Stage 1開始前

- [ ] 本報告の分類、必須fallback、閾値未承認の扱い、残余リスクを人が承認する。
- [ ] F-001以降も外部公開・release完了ではなく、ローカル実装と受入準備であることを確認する。
- [ ] ST-008を記録済みの利用条件不同意のままskipし、手動speaker割当で進むことを確認する。将来の同意は新しい記録と再評価を必要とする。
- [ ] manual pathの作業量が未測定である残余リスクを受容し、追加のcloud necessity証拠と明示指示が出るまでAzure gateを閉じたまま進むことを確認する。

fixture権利、署名証明書、clean production Windows、完全SBOMはStage 1のローカルscaffold開始条件ではない。ただし次のrelease blockerとして残す。

### Release前に必須

- 既存fixtureのRightsRecordと証憑参照を`approved`へ確定するか、権利確認済みの代替素材で影響を受けるbenchmark/受入試験を再実行し、その判断者・日時・根拠を保存する
- production-channel Windows 11 24H2 x64 clean環境でのsigned install、package-identity launch、upgrade、rollback、uninstall。release候補で実際に選んだNVENC/Media Foundation経路も再検証する
- signing主体、証明書、配布経路の確定
- FFmpeg、Pythonの完全なtransitive依存、model、WebView2、fontを含むhash lock、SBOM、license/notice、脆弱性・再配布審査。FFmpegのLGPL source/config/noticeとcodec特許は組織の指定承認者が判断する
- CUDAを使う場合は権限あるlicense/package承認者と判断期限を記録し、正規の再配布可能runtimeとnoticeを固定する。承認できなければCPU-only製品としてCUDA pathを無効化し、現在のFoundry cacheは同梱しない
- WebView2の起動時runtime確認、欠落時診断、Evergreen更新方針、release候補runtimeでの回帰を検証する。benchmarkのCSPはpageのsubresource/connectを遮断しただけで、Evergreen更新・runtime telemetryのnetwork動作を監査していないため、組織policyとの整合も確認する。最小versionは実測・support方針なしに捏造しない
- 独立annotationと人手評価に基づく必要なproduction閾値
- retention期間、backup対象、restore、削除、uninstall後データ方針の組織承認
- §5のfallbackを含むMust受入、security、recovery、accessibility、traceabilityの全gate

## 8. 残余リスク

1. P-003は`fixture-manifest.json`へfixtureのhash、`rightsStatus=unconfirmed`、local technical evaluationの利用可否を記録して完了した。これは権利承認ではない。測定fixtureはその1本だけで、内容特性・収録条件・話者・画面共有を代表しない。
2. 正解注釈がないため、ASR、時刻、shot、face、audio eventのaccuracyは不明である。
3. CUDA成功はFoundry Local cache由来runtimeへ依存し、release再配布経路は未承認である。
4. automatic face reframe、local text generation、diarizationは採用できず、手動作業量は未測定である。
5. renderとpreviewは短いsample・単一PC・単一runtime/DPIであり、長尺・moving-video・cross-machineの品質と性能は不明である。製品scopeは単一PCでありremote renderingは対象外だが、Evergreen runtime自身の更新・telemetry通信は本benchmarkで監査していない。
6. unsigned MSIXのpack/unpack成功は、署名済み製品のinstall・upgrade・recoveryを保証しない。
7. Stage 0のACL、CSP、process cleanupは製品workspace、Web bridge、authorization、auditの完成を意味しない。
8. Azureを追加しても上記の権利・正解注釈・人手承認問題は自動的に解消しない。

## 9. 承認記録

次の全項目への人手判断が必要である。

| ID | 承認対象 | 提案 | 状態 |
|---|---|---|---|
| A | Stage 1技術baseline | §3の条件付き採用案を使用する | 未承認 |
| B | 手動fallback | §5を必須契約にする | 未承認 |
| C | 閾値 | §6の機械gateだけを採用し、production数値は後続へ保留する | 未承認 |
| D | Residual risk | §8を受容してローカルStage 1を開始する | 未承認 |
| E | Conditional paths | ST-008のskipを明示的な将来再評価可能状態として維持し、manual workload未測定を受容した上で、追加のcloud necessity証拠と利用者の明示指示までAzure gateを閉じる | 未承認 |
| F | Release blocker | §7のrelease blockerはStage 1開始で免除されず、全件をrelease前に解消する | 未承認 |

承認者、役割、日時、変更条件は、人の回答後に記録する。承認前はST-015未完了であり、F-001以降を開始しない。

## 10. 敵対的レビュー

本報告を、(1)数値・evidence整合、(2)要求・依存・gate、(3)security・privacy・rights・licenseの3軸でレビューした。

- 全14 canonicalのrun ID、raw SHA-256、報告中の実測値を照合し、数値不一致・単位誤り・捏造閾値は0件だった。
- 実在する曖昧さとして、提案と確定の語調、fallbackの実装時期、CUDA配布のclosure、Azureを閉じる根拠、rights証拠の形式、Job Object採用範囲、未測定と測定済み未承認の混同を修正した。
- Azure subscriptionのread-only inventoryはP-010/ADR-0006の判断材料であり、subscription選択、resource作成、model deploymentではないため「gate違反」という指摘は採用しなかった。
- P-003の完了条件は権利状態と利用可否の記録であり、`unconfirmed`を隠さずlocal technical evaluationへ限定している。権利承認済みでなければStage 0/ローカルscaffoldを開始できない、という正本にない条件は追加しなかった。
- ST-011のmodel不達だけでAzure分岐を自動開始する指摘は、manual path、Z-001のcloud necessity証拠、利用者の明示指示を無視するため採用しなかった。
- 最終再レビューで、P-003が`unconfirmed`状態を記録して完了した境界、CUDAのCPU-only closure、page-level CSPとWebView2 runtime通信監査の差、Job Objectとcaller cleanupの責任分担を追記した。
- 修正後の未解決指摘は0件。ただし§9の人手承認自体は未実施であり、ST-015は未完了である。