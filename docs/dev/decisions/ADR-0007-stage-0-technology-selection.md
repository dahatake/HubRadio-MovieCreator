# ADR-0007: Stage 0技術選択と手動fallback

- 状態: Accepted
- 決定日: 2026-08-27
- タスク: ST-015、ST-015-CLOSE
- 承認証拠: [`benchmarks/results/stage-0-report.md`](../../../benchmarks/results/stage-0-report.md)
- 承認証拠SHA-256: `c450dd8413977f6790aed3e015edad8b27dbf215b14278e99989eb704ab5c093`

> [!IMPORTANT]
> 2026-09-02以降、保留taskの再開に記載した「別の人手判断」は、[ADR-0009](ADR-0009-source-only-defaults-and-automated-evaluation.md)により単一product ownerの明示判断へ置き換える。採用技術、condition-false/heldの事実、未測定値、manual fallback、release未承認境界は維持する。以下は決定時点の履歴である。

## 背景

Stage 0では、Windows 11 x64単一PC上で媒体取込、VFR時刻、代理写像、encoder、ASR、alignment、shot、face、音声event、子process、package、local text model、manual render、WebView2 previewを実測した。承認証拠は14件のcanonical resultと、それぞれが参照するGit対象外のraw runに基づく。

本ADRは、承認済み報告書の分類をStage 1の実装依存へ反映する。測定完了をaccuracy、production品質、外部公開、再配布、release readinessの承認とは扱わない。

## 決定

### 採用するbaseline

| 対象 | Stage 1の決定 |
|---|---|
| VFR・時刻 | 観測PTSと有理数を正本にし、公称fpsから元時刻を復元しない。負のstream開始offsetを保持する |
| 代理動画 | 代理上の選択を観測済み`ProxyFrameMap`でsource PTSへ解決する。Stage 0の最大誤差をproduction閾値へ転用しない |
| FFmpeg | 固定LGPL buildを開発baselineとする。NVENCを利用可能時の候補、Media Foundationを必須fallbackとし、実encoder/profileを記録する |
| ASR | faster-whisper smallを条件付き採用する。CPUを必須fallbackとし、CUDAは測定機に既存のruntimeをcopyしないローカル開発・再測定だけに限定する |
| 単語時刻 | 編集可能な候補として扱い、自動確定・clampをしない。発話時刻と手動字幕調整を常設する |
| Manual render | 16:9/9:16、trim、concat、crop、subtitle、audio gainの実証をtyped Render IRの入力にする |
| Preview | Canvas/SVGの直接previewは編集用、同一LayoutSpec/Cueから作るFFmpeg忠実区間を承認対象にする |
| 子process | Job Objectのkill-on-closeとcaller側の明示kill・bounded waitを併用し、active-process limitへ依存しない |
| Package | 複数実行ファイルのunsigned MSIXはfeasibility証拠としてのみ採用し、署名・install・upgrade・uninstallは未完了とする |
| 媒体警告 | decode、clipping、channel等を人のreview signalとして提示し、自動削除へ変換しない |

### 条件不成立として実装しないtask

| Task | 決定 | 代替経路 |
|---|---|---|
| `A-010` | pyannote利用条件へ同意していないためcondition-false。worker、model、配布物を作らない | `A-011`の手動speaker割当 |
| `A-015` | Stage 0で根拠ID付きaccepted JSONが0件だったためcondition-false。local text suggestion clientを作らない | `A-014`と`A-020`の決定論的値・手入力 |

### 再評価まで保留するtask

| Task | 保留根拠 | 再開条件 | 代替経路 |
|---|---|---|---|
| `A-012` | fixtureの3構成でshot境界0件。synthetic hard-cut controlは動作したが、正例annotationがない | 代表fixtureの正例annotation、detector/threshold再評価、別の人手判断 | transcript/audio規則と手動topic境界 |
| `A-013` | positive controlは1件検出したが、fixture 300/300 frameでface検出0件。精度annotationがない | 代表fixtureのface annotation、bbox/threshold再評価、別の人手判断 | manual layout、manual subject region、manual keyframe、版付きsafe zone |

保留は失敗を成功と読み替えるものではない。再開条件が満たされるまでproduction workerや自動判定を実装せず、未測定のprecision/recallや閾値を設定しない。

## 依存関係の再baseline

heldまたはcondition-false taskを通常critical pathへ残さない。

| Task | 再baseline |
|---|---|
| `A-014` | transcript発話境界と決定論的規則を必須入力にし、`A-012`は将来の任意入力とする |
| `A-016` | 実装対象のSuggestion producerだけを統合し、`A-010`、`A-012`、`A-013`、`A-015`を必須依存にしない |
| `R-008` | manual layoutを`A-013`から独立させ、face候補は将来の任意入力とする |
| `R-011` | 字幕制約を版付きLayoutSpecのsafe zoneから導出し、`A-013`を必須依存にしない |
| `E-005` | blur、露出、余白等の決定論的評価をbaselineにし、face scoreを任意入力とする |
| `E-009` | transcriptのspeaker turn、手動speaker割当、manual subject keyframeをbaselineにし、diarization/face候補を任意入力とする |
| `E-010` | Compositionのedit境界と承認素材をbaselineにし、shot/face候補を任意入力とする |
| `PK-001` / `PK-004` | 不採用modelを配布依存へ含めず、採用・保留・不採用状態をBOMへ記録する |

この再baselineにより、`Q-000`、`Q-001`、`Q-004`、`Q-005`の`G1-MUST`経路はheld taskへ依存しない。将来A-012/A-013を採用しても、手動経路を削除しない。

## 必須fallback契約

承認証拠§5の8項目をStage 1の設計・受入契約とする。各fallbackは対応機能と同時に実装し、対応機能の初回有効化前に試験する。未試験または不合格なら対応機能を無効のままfail closedとし、関連する`G1-MUST`受入へ進めない。

## 未承認のまま維持する事項

- ASR、alignment、shot、face、audio eventのaccuracyとproduction閾値
- 知覚品質、moving-video/audio preview、cross-DPI/runtime/GPU再現性
- CUDA runtimeの再配布、FFmpeg/codecの組織承認、完全SBOM
- fixtureおよび追加されたlocal assetの外部公開権利
- signed install、upgrade、rollback、uninstall、release readiness
- retention期間と組織上のrelease承認者

これらはStage 1開始を妨げないが、承認証拠§7のrelease blockerとして残す。

## Azure

本ADRの決定時点ではAzureを非採用とし、gate closedを維持した。当時の「全task実行」指示は、`Z-001`のcloud necessity証拠や`Z-005`のresource作成指示を代替しなかった。その後に証拠と別の明示指示が成立したため、[ADR-0008](ADR-0008-bounded-azure-evaluation.md)がZ-001〜Z-007に限ってこの状態を置き換える。Z-007はnetwork senderを持たないlocal payload確認境界であり、実コンテンツ送信とend-user cloud featureはZ-008〜Z-010完了まで閉じたままである。

## 敵対的レビュー

- task表を機械解析し、203 ID、一意性、条件不成立・保留14件、後続baseline-active 163件を確認した。算術は「完了25 + 先行条件不成立`ST-008` 1 + 後続177 = 全203」と明示した。
- 初期案に残っていた`A-016`の範囲依存、`R-011`、`E-005`、`E-009`、`E-010`、`PK-001`、`PK-004`の直接・範囲依存を検出し、manual-firstまたは任意入力へ修正した。
- `Q-000`、`Q-001`、`Q-004`、`Q-005`のtransitive dependencyを再確認し、`A-010`、`A-012`、`A-013`、`A-015`への必須依存が0件であることを確認した。
- 承認scope、production閾値、rights、release、CUDA再配布、Azure gate、navigation、正本hashを独立にレビューし、範囲の拡大や捏造値がないことを確認した。
- 修正後の未解決指摘は0件だった。

## 結果

- ローカルStage 1の`F-001`以降を開始できる。
- 後続taskは自動化が不在でも手動経路で完結できるよう実装する。
- 数値閾値、権利、配布、releaseの未決事項は解消済みと扱わない。
- 本決定を変更する場合は、新しいevidenceと別のAccepted ADRを必要とする。
