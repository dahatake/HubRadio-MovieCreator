# ATG Kit — Autonomous Task Graph 判定エンジン

タスクをグラフとして扱い、依存関係と書込み集合からノードの並列実行可否・有界リトライ・
終端受入を**判定**する。LLM 呼び出しやエージェント実行そのものは行わない。
依存ゼロ（Python 標準ライブラリのみ）で、追加のインストール作業なしに動作する。

計画段階の分割判定・粒度・見積・完了条件は Skill 層の規約（`.github/skills/atg/references/planning.md`）として本キットに含む。
CLI は計画判定を強制せず、CLI 実行形式は 14 のまま変わらない。

- 設計の一次根拠: [`docs/spec.md`](docs/spec.md)
- 限界・非保証事項: [`docs/LIMITS.md`](docs/LIMITS.md)
- 動く最小例: [`examples/hello-atg/`](examples/hello-atg/)

---

## 対応面と実測確認状況

ATG Kit は Agent Skill（`.github/skills/atg/`）として配置し、以下のローカル 3 面で使うことを想定する。

| 面 | 確認状況 | 根拠 |
|---|---|---|
| GitHub Copilot CLI | **実機確認済み**: `copilot skill list` が `.github/skills/` 配下の Skill を実際に検出する | 2026-09-21 実行、`copilot` CLI 1.0.87-0 |
| VS Code の GitHub Copilot | **実機確認済み（Skill 発見・読み込み + CLI の E2E）**: VS Code 上の Copilot セッションが `.github/skills/atg/` を project Skill として列挙し、`SKILL.md` と `references/` を読み込めた。統合ターミナルから配布キットの CLI を別リポジトリで通し実行できた（`init` → `contract check` → `graph check` → `run begin` → `node next` → `node start` → `report`） | 2026-09-22 実測。**Copilot Chat 拡張の UI 操作（スラッシュコマンド等）は未確認** |
| GitHub Copilot app（デスクトップ） | 未検証 | 本キットの開発環境では当該アプリの操作による確認を行っていない |

**並列実行について**: ATG 自体はノードの並列起動を行わない（`atg node next` は「同時実行してよいノード集合」を返すだけ）。Copilot CLI には複数タスクを並列実行する `/fleet` 機能が存在することを実機確認済みだが、ATG の判定結果と `/fleet` を組み合わせた E2E 検証は本 v0.1 の範囲外である。

---

## セットアップ

このフォルダ（`tools/skills/atg/`）は上流の正本であり、依存ゼロでそのまま
`python -m atg --help` が動く（Python 3.11 以上）。

他リポジトリへ配布した場合は、コピー先リポジトリのルートで
`tools/atg/install.py --repo-root . --no-venv` を実行するだけでセットアップが
完了する（Skill 配置を含む）。以後は `tools/atg/atg.ps1` / `./tools/atg/atg.sh` が
`python -m atg` の別名として使える。手順の正本は `GETTING-STARTED.md`
（配布後はキット直下、上流リポジトリでは
`tools/for-other-repo/atg/GETTING-STARTED.md`）。

Skill 定義（`.github/skills/atg/`）は、配布先では `install.py` が導入先リポジトリの
`.github/skills/atg/` へ自動配置する。

---

## コマンド一覧（14 実行形式）

詳細な引数・出力形式・終了コードは [`docs/spec.md`](docs/spec.md) §2 を参照。

| コマンド | 役割 |
|---|---|
| `atg init` | 状態ディレクトリを初期化する |
| `atg contract check <path>` | ルート契約 JSON を検証し、正準 SHA-256 を返す |
| `atg graph check <path>` | タスクグラフ JSON を検証し、T1/T∞/Lmax/加速上界を返す |
| `atg run begin` | 実行を耐久登録する |
| `atg run finish` | 終端受入を判定し、状態へ記録する |
| `atg node next` | 起動可能ノード（依存充足かつ書込み競合なし。終端状態 `succeeded` / `failed` / `blocked` は除く）を列挙する |
| `atg node start <id>` | ノード開始を記録し、重複起動と runnable でないノードの起動を拒否する |
| `atg node finish <id>` | handoff を検証してノード完了を確定する |
| `atg node fail <id>` | 失敗クラスに基づいて再試行可否を判定する（`contract` は `contract_violation`、上限超過は `max_attempts_exceeded`。待機は行わない） |
| `atg node check-writes <id>` | 宣言外書込みがないかを検査する |
| `atg parallel advise` | 観測値に基づき並列度の増減を提案する（実行はしない） |
| `atg merge plan` | 統合順序を決定する（実行はしない） |
| `atg merge classify` | 統合結果を機械的／意味的／契約違反に分類する（実行はしない） |
| `atg report` | 実行状態から終端レポートを生成する（終端被覆は `failed` / `blocked` も数えるため成功率ではない） |

---

## 動作確認済みの流れ

`examples/hello-atg/` を使い、他リポジトリへ実際に配布した状態での通し実行を実測済みである
（3 ノード: 2 つが並列実行可能、1 つが両方に依存）。

> **注**: この例は演習用の骨組みであり、ノードの成果物（`parser.py` / `formatter.py` / `main.py`）は
> 同梱していない。契約の `acceptance_command` はそれらの存在を確認するため、ノードを実行して
> 成果物を作る前に実行すると exit 1 になる。これは意図した挙動である。

```bash
python -m atg contract check examples/hello-atg/atg-contract.json
python -m atg graph check examples/hello-atg/atg-graph.json
python -m atg run begin --contract examples/hello-atg/atg-contract.json --graph examples/hello-atg/atg-graph.json
python -m atg node next --graph examples/hello-atg/atg-graph.json --execution-id <id>
```

ノード実行の完全な手順（worktree 作成・handoff 作成・再開手順）は
`.github/skills/atg/references/execution-loop.md` を参照する（配布後はキット直下の
`.github/skills/atg/` に配置される）。

---

## 既知の制約

- LLM 呼び出し・エージェント実行そのものは行わない（判定支援のみ）。
- 統合ウェーブの実行（`git merge` 実行・影響範囲テスト実行）は自動化しない。
- 並列実行の実際のプロセス起動は行わない。
- 詳細は [`docs/LIMITS.md`](docs/LIMITS.md) を参照。

性能・品質改善・コスト削減の程度についてはいかなる主張も行わない。
