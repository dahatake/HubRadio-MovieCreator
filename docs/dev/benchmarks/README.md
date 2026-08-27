# Stage 0 ベンチマーク

この文書は、Stage 0 の技術検証を実行・レビューする開発者向けの入口です。ベンチマークのコード、fixture manifest、JSON Schema、canonical 結果は `benchmarks/` に置き、読者向けの方針と指標は `docs/dev/benchmarks/` に置きます。

> [!IMPORTANT]
> ベンチマークの成功は製品採用や production 品質の承認を意味しません。正解注釈がない指標や人が承認していない閾値は、未測定・未承認のまま記録します。

## 入口

| 目的 | 場所 |
|---|---|
| 実行ハーネス | [`benchmarks/run-benchmarks.ps1`](../../../benchmarks/run-benchmarks.ps1) |
| 各 stage の実装 | [`benchmarks/stages/`](../../../benchmarks/stages/) |
| 固定 fixture と control | [`benchmarks/fixtures/`](../../../benchmarks/fixtures/) |
| process／package probe | [`benchmarks/probes/`](../../../benchmarks/probes/) |
| JSON Schema と canonical 結果 | [`benchmarks/results/`](../../../benchmarks/results/) |
| 注釈規約 | [annotation-policy.md](annotation-policy.md) |
| 指標定義 | [metric-definitions.md](metric-definitions.md) |
| result schema 方針 | [result-schema-policy.md](result-schema-policy.md) |
| 環境構築 | [開発を始める](../getting-started.md) |
| 採否と依存 | [詳細実装計画](../planning/implementation-plan.md) |

## 証拠の区分

### Raw run

`benchmarks/results/runs/` に保存する、単一実行の machine-readable 証拠です。

- 成功と失敗を同じ envelope で記録します。
- 実行時の schema、tool、入力、実装ファイルの hash を可能な範囲で保持します。
- ローカル証拠であり Git 対象外です。
- transcript、画像、音声、絶対 path などを含み得る private artifact は、ACL を限定した Git 対象外領域へ分離します。
- 過去 run は後から書き換えません。

### Canonical summary

`benchmarks/results/*.json` に置く、レビュー済みの要約です。

- 元 raw run の ID と SHA-256 を記録します。
- 非機密で、判断に必要な値だけを含めます。
- 人による解釈、未測定事項、採用・保留・手動化の候補を raw 値と分離します。
- 0件、失敗、単回観測を、精度や不存在の証明へ読み替えません。

詳細は [result schema 方針](result-schema-policy.md) を参照してください。

## 現在の stage

| Stage | canonical 結果 | 現在の扱い |
|---|---|---|
| `hardware` | `hardware-inventory.json` | 単一検証機の inventory |
| `ffmpeg-capabilities` | `ffmpeg-capabilities.json` | 固定 LGPL build の capability と hash |
| `media-ingest` | `media-ingest.json` | VFR、stream、全編 decode の実測 |
| `proxy-frame-map` | `proxy-frame-map.json` | 代理 frame と元素材 PTS の写像 |
| `encoder-matrix` | `encoder-matrix.json` | H.264 候補の単一環境比較 |
| `shot-detection` | `shot-detection.json` | fixture 境界0件、既知 cut control 合格。閾値未承認 |
| `face-detection` | `face-detection.json` | fixture 検出0件、正例 control 合格。自動 reframe 非推奨 |
| `audio-event-and-recording-quality` | `audio-event-and-recording-quality.json` | 候補は手動試聴前提。自動削除禁止 |
| `process-and-package` | `process-and-package.json` | Job Object と unsigned MSIX の Stage 0 probe |
| `asr-benchmark` | `asr-benchmark.json` | CPU／CUDA／Foundry Local 比較。fixture 精度未測定 |
| `alignment-benchmark` | `alignment-benchmark.json` | 時刻構造と control。exact 境界精度未測定 |
| `local-text-model` | `local-text-model.json` | 2候補とも採用条件未達。manual-only |
| `manual-render` | `manual-render.json` | 16:9／9:16 の機構と sampled PTS 逆引き |
| `preview-fidelity` | `preview-fidelity.json` | actual WebView2 Canvas/SVGとFFmpeg静止frame区間の差分を記録。知覚品質・moving-video・cross-DPI/runtimeは未測定 |

ファイル名だけで採用可否を判断せず、各 canonical の `assessment`、`openQuestions`、`configurationDisposition` と [実装計画](../planning/implementation-plan.md)を確認してください。

## 実行の流れ

1. fixture manifest、入力 size、入力 SHA-256 を検査します。
2. PowerShell 7 の子プロセスで選択 stage を実行します。
3. timeout や例外時は子孫プロセスを終了します。
4. 実行後に source の size、更新日時、SHA-256 を再検査します。
5. 成功・失敗 envelope を JSON Schema で検証します。
6. staging ファイルから raw run へ原子的に昇格します。
7. raw 証拠をレビューし、非機密の canonical summary を別途更新します。
8. 実在する問題だけを敵対的レビューへ記録し、修正後に再検証します。

基本コマンド:

```powershell
pwsh.exe -NoLogo -NoProfile -File benchmarks\run-benchmarks.ps1 -Stage <stage-name> -ChildTimeoutSeconds <seconds>
```

前提条件と小さい確認手順は [開発を始める](../getting-started.md) を参照してください。

## 注釈と指標

- 正解注釈は detector や model の出力を見る前に作成します。
- 自動出力を正解データへ混ぜません。
- 判断不能は `uncertain` と理由を記録します。
- 日本語 ASR は CER を基本とし、tokenizer を固定できる場合だけ WER を併記します。
- 平均だけでなく件数、中央値、上位分位、失敗カテゴリを保存します。
- 合否値は同じ素材・注釈・計算式で比較した後、関係者が承認します。

詳細は [注釈規約](annotation-policy.md) と [指標定義](metric-definitions.md) を参照してください。

## 新しい stage を追加する場合

1. 対象要求、実装計画の task ID、依存 stage を確認します。
2. `benchmarks/stages/` に、固定 fixture を読み JSON payload を返す最小実装を追加します。
3. `run-benchmarks.ps1` の stage 選択、起動、runtime validator を追加します。
4. `benchmarks/results/schema.json` を後方互換に拡張します。breaking change は新しい schema version にします。
5. 成功、失敗、timeout、source 不変、path 秘匿、孤児プロセスなしを検証します。
6. 0件や失敗を解釈できる control を用意します。control がない場合は未確認と記録します。
7. raw run を canonical summary として直接 commit しません。
8. canonical 作成後に敵対的レビューを行い、測定事実を超える主張を除きます。

## 機密性と権利

- fixture、生成動画、raw run、model cache は原則として Git 対象外です。
- media metadata の raw tag 値を terminal、issue、canonical summary へ出しません。
- source 時刻、bbox、transcript、候補 clip など、内容へ到達できる証拠はローカルに限定します。
- fixture の技術的な QC 合格は、外部公開や再配布の許可を与えません。
- model、runtime、control asset は version、配布元、byte size、SHA-256、license 条件を記録します。
