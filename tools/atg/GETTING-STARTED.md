# ATG Kit — 他リポジトリでの導入手順

タスクグラフの依存関係・書込み集合を検証し、ノードの並列実行可否・有界リトライ・終端受入を判定する
Autonomous Task Graph (ATG) Kit。**依存ゼロ（Python 標準ライブラリのみ）**で、LLM 呼び出しや
エージェント実行そのものは行わない判定支援ツールである。

このフォルダは配布元リポジトリの宣言（`tools/for-other-repo/atg/package.toml`）に基づき
`tools/for-other-repo/copy_to_repo.py` が組み立てたもの。
同梱の版情報は [`KIT-VERSION.json`](./KIT-VERSION.json) にある。

---

## 1. セットアップ（3 手順）

```pwsh
# 1. 配布元リポジトリのルートで（--no-docs は不要。ATG にドキュメント宣言は無い）
python tools\for-other-repo\copy_to_repo.py <導入先>\tools -p atg

# 2. 導入先リポジトリのルートで（これだけ）
python tools\atg\install.py --repo-root . --no-venv

# 3. 動作確認
tools\atg\atg.ps1 --help
tools\atg\atg.ps1 contract check tools\atg\examples\hello-atg\atg-contract.json
```

bash では `python tools/atg/install.py --repo-root . --no-venv` の後、
`./tools/atg/atg.sh --help` で確認する。

`--no-venv` は依存ゼロの ATG では venv を作る必要が無いために付ける。省略すると
空の `.venv-atg` が作られるだけで、機能には影響しない。

`install.py` は Skill 定義の配置（`.github/skills/atg/`）を含めて 1 回で完了する。
実行後、以下がそのまま使える。

```bash
tools/atg/atg.sh init
tools/atg/atg.sh contract check tools/atg/examples/hello-atg/atg-contract.json
tools/atg/atg.sh graph check tools/atg/examples/hello-atg/atg-graph.json
```

Python 3.11 以上であれば動作する。

### Skill 定義の配置

`install.py` がコピー先リポジトリの**ワークスペースルート**の `.github/skills/atg/` へ
自動配置する（キット同梱の `skill/SKILL.md` が正本のコピー元）。Copilot が探索するのは
ワークスペースルートの `.github/skills/` であり、キット配下の `<コピー先フォルダ>/skill/` では
検出されない。

| 面 | 確認状況 |
|---|---|
| GitHub Copilot CLI（`copilot skill list`） | **実機確認済み**: プロジェクト直下の `.github/skills/` を検出する |
| VS Code の GitHub Copilot | 未検証（本キットの開発時点では GUI 操作による確認を行っていない） |
| GitHub Copilot app（デスクトップ） | 未検証（同上） |

### `0.2.1` 以前から更新する場合

`0.2.2` 以降はキット直下に `.github/` を持たない。旧版で
`Copy-Item <kit>/.github/skills/atg .github/skills/atg` を手動実行していた場合、
キット直下の `<コピー先フォルダ>/.github/` は不要になったので削除してよい
（リポジトリルートの `.github/skills/atg/` は `install.py --force` で更新される）。

---

## 2. 使い方

**手順 0（計画）**: タスクグラフを書く前に、`task_scope` / `context_size` を確定して分割判定（`PROCEED` / `SPLIT_REQUIRED`）と見積・完了条件を決める。規約は
[`skill/references/planning.md`](skill/references/planning.md)、計画からグラフ JSON への変換規則は
[`skill/references/plan-to-graph.md`](skill/references/plan-to-graph.md) を参照する
（配布後は `.github/skills/atg/references/` に配置される）。

```bash
# 1. 状態ディレクトリを初期化
tools/atg/atg.sh init

# 2. 契約とグラフを検証する（examples/hello-atg/ に動く最小例がある）
tools/atg/atg.sh contract check tools/atg/examples/hello-atg/atg-contract.json
tools/atg/atg.sh graph check tools/atg/examples/hello-atg/atg-graph.json

# 3. 実行を耐久登録する
tools/atg/atg.sh run begin --contract tools/atg/examples/hello-atg/atg-contract.json --graph tools/atg/examples/hello-atg/atg-graph.json

# 4. 起動可能ノードを取得する
tools/atg/atg.sh node next --graph tools/atg/examples/hello-atg/atg-graph.json --limit 2
```

`atg.ps1` / `atg.sh` / `atg.cmd` はいずれも同じ CLI（`python -m atg`）の別名であり、
同梱の `vendor/atg` を自動で `PYTHONPATH` へ通した上で全引数を転送する。
`python -m atg` を直接呼びたい場合は、このキットの `vendor/` フォルダを
`PYTHONPATH` へ設定する。

ノード実行の完全な手順（worktree 作成・handoff 作成・再開手順を含む）は
[`skill/references/execution-loop.md`](skill/references/execution-loop.md) を参照する。
契約の書き方は [`skill/references/contract-authoring.md`](skill/references/contract-authoring.md) を参照する。

---

## 3. ドキュメント

| ファイル | 内容 |
|---|---|
| [`docs/spec.md`](./docs/spec.md) | 実装仕様（14 実行形式・データ形式・耐久状態スキーマ） |
| [`docs/LIMITS.md`](./docs/LIMITS.md) | 限界・非保証事項・v0.1 非対応範囲 |
| [`README.md`](./README.md) | キット全体の使い方（3 面ごとのセットアップ） |
| [`skill/SKILL.md`](skill/SKILL.md) | Skill 仕様本体（配布後は `.github/skills/atg/SKILL.md` として配置される） |

---

## 4. 更新（版の同期）

現状確認はこのフォルダだけでできる。

```bash
python install.py --kit-dir . --version    # 導入済みの版
python install.py --kit-dir . --verify     # 同梱ファイルの改変・欠落
```

更新は上流リポジトリ側で実行する。

```bash
# 上流との差分確認（コピーせず、版と改変状況だけ表示）
python tools/for-other-repo/copy_to_repo.py <コピー先> -p atg --check

# 更新
python tools/for-other-repo/copy_to_repo.py <コピー先> -p atg
```

`KIT-VERSION.json` に記録された版より上流が新しいときだけコピーされる。
同版・古い版を上書きするには `--force` を付ける。

---

## 5. 既知の制約

- ATG は LLM 呼び出し・エージェント実行そのものは行わない。ノードの実装・検証コマンドの実行はエージェント側が担う。
- 統合ウェーブの実際の `git merge` 実行・影響範囲テストの実行は自動化しない（決定・分類の判定のみ提供する）。
- 並列実行の実際のプロセス起動もエージェント側が担う（`atg node next` は起動可能なノード集合を返すのみ）。
- 詳細は [`docs/LIMITS.md`](./docs/LIMITS.md) を参照。
