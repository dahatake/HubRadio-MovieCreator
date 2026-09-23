# ATG ノード実行ループ

この資料は、1ノードを実行する際の具体的な手順を、ATG CLI §2 に存在するコマンドと素の `git` コマンドだけで固定する。`git` コマンドはリポジトリルートで実行する。`python -m atg` は ATG パッケージを解決できる位置（上流リポジトリでは `tools/skills/atg`、配布先ではキットディレクトリ）で実行するか、当該ディレクトリを `PYTHONPATH` へ設定したうえでリポジトリルートから実行する。

配布キットでは `tools/atg/atg.ps1` / `./tools/atg/atg.sh` が `python -m atg` と同じ CLI の
別名として使え、以下の各コマンド例はそのまま `tools\atg\atg.ps1 <サブコマンド以降>` に
読み替えられる（同梱の `vendor/atg` を自動で `PYTHONPATH` へ通すため）。

## プレースホルダー

- `<N>`: 同時に取得する起動可能ノード数
- `<id>`: 実行対象ノード ID
- `<graph-path>`: `atg-graph.json` のパス
- `<root-contract-path>`: ルート契約 JSON のパス
- `<execution-id>`: `python -m atg run begin` が返した実行 ID
- `<worktree-path>`: 対象ノード専用の worktree パス
- `<branch>`: 対象ノード専用のブランチ名
- `<base-rev>`: worktree 作成直後の基準 revision
- `<handoff-path>`: 作成した `handoff.json` のパス

## 1. 開始前: 起動可能ノードを取得する

まず、現在起動してよいノードだけを取得する。

```bash
python -m atg node next --graph <graph-path> --execution-id <execution-id> --limit <N>
```

- 出力の `runnable` に `<id>` が含まれるノードだけを対象にする。
- `runnable` が空配列なら、その時点では起動できるノードがないので新しい worktree は作らない。
- `runnable` には終端状態（`succeeded` / `failed` / `blocked`）のノードは含まれない。

## 2. 隔離: 1ノード = 1 worktree = 1 branch を作る

対象ノードごとに専用 worktree と専用 branch を作る。`--force` は使わない。

```bash
git worktree add <worktree-path> -b <branch>
git -C <worktree-path> rev-parse HEAD
```

- 2つ目のコマンドの出力を `<base-rev>` として控え、後段の `python -m atg node check-writes` に使う。
- 以後の実装・検証は `<worktree-path>` 内で行う。

## 3. 開始記録: `python -m atg node start` でリースを取る

worktree を作ったら、実作業の前に durable state 上でノード開始を記録する。

```bash
python -m atg node start <id> --execution-id <execution-id>
```

- exit 0 のときだけ、このワーカーが当該ノードを続行してよい。
- exit 1 は**起動不可**である。出力で原因を区別できる（2026-09-22 実測）。
  - `{"error": "node is not runnable: <id>"}` … 依存未充足または書込み競合で runnable でない。
  - `{"started": false, "attempt": <N>}` … 既に開始済み（重複起動拒否）。
  いずれの場合も当該ノードをスキップし、この worktree を使わずに次の起動可能ノード探索へ戻る。

```bash
git worktree remove <worktree-path>
python -m atg node next --graph <graph-path> --execution-id <execution-id> --limit <N>
```

## 4. コンテキストパック構築: ルート契約の必要部分 + 直接依存 handoff のみを渡す

現在ノードに必要な契約断片と依存情報だけを集める。

```bash
git -C <worktree-path> show HEAD:<root-contract-path>
git -C <worktree-path> show HEAD:<graph-path>
```

- ルート契約は全文を無差別に渡さず、当該ノードに関係する部分だけを抜き出して渡す。少なくとも、当該ノードの `outputs` に関係する `deliverables`、必要な `objective` / `non_goals`、`acceptance_command`、`execution_envelope`、`secret_policy` を対象にする。
- グラフからは、当該ノード自身の `contract` と `deps` だけを取り出す。
- 依存ノードについては **直接依存ノードの `handoff.json` のみ** を渡す。推移的先祖の全履歴は含めず、先祖情報が必要なら direct dependency の handoff に要約された内容だけを使う。

## 5. 実装: エージェントが対象ファイルを変更する

実装前後で変更対象を確認しながら、エージェントは `write_set` と `contract.outputs` に含まれるファイルだけを更新する。

```bash
git -C <worktree-path> status --short
git -C <worktree-path> diff --name-only
```

- ここで実際の編集を行う。
- 編集後に `git -C <worktree-path> diff --name-only` を見直し、意図したファイルだけが変更されていることを確認する。

## 6. ノード検証: `contract.verify_command` を実行する

ノード contract に書かれた検証コマンドを、その文字列のまま実行する。

```bash
<exact value of node.contract.verify_command>
```

- exit 0 なら次へ進む。
- exit 非0 なら implementation failure として記録する。

```bash
python -m atg node fail <id> --class implementation --execution-id <execution-id>
```

- `{"retry": true, ...}` なら Step 4 のコンテキストパック構築へ戻って再試行する。
- `{"retry": false, ...}` なら当該ノードは失敗確定となり、後続ノードは durable state 側でブロック対象になる。

## 7. 書込み検査: 宣言外書込みがないか確認する

検証が通ったら、基準 revision との差分が `write_set` に収まっているかを ATG に判定させる。

```bash
python -m atg node check-writes <id> --base <base-rev> --graph <graph-path> --repo-dir <worktree-path>
```

- **`--repo-dir` を省略するとカレントディレクトリだけが検査される。** Step 2 で worktree を作った場合、`--repo-dir <worktree-path>` を必ず渡す。渡し忘れると worktree 内の違反は検出されない。
- **`--repo-dir` には作業ツリーのルートを渡す。** サブディレクトリを渡すと、検査せずに拒否される（exit 1）。`git diff` はリポジトリルート基準、`git ls-files --others` はカレントディレクトリ基準でパスを返すため、基準が食い違って違反を見逃すからである。

- `{"ok": true}` なら次へ進む。
- `{"ok": false, "violations": [...]}` なら宣言外書込みがあるので、違反箇所を修正してから Step 6 へ戻る。

```bash
git -C <worktree-path> diff --name-only <base-rev>
```

- 修正後は、もう一度 `contract.verify_command` を実行し直してから `python -m atg node check-writes` を再実行する。

## 8. handoff 作成: `outputs` の SHA-256 を埋めた `handoff.json` を用意する

ATG CLI §2 には handoff 生成専用コマンドがないため、このステップはワーカー自身が `handoff.json` を作る。ただし、入力と出力の確定には `git` で差分を固定してから進める。

```bash
git -C <worktree-path> diff --name-only <base-rev>
git -C <worktree-path> status --short
```

- `outputs[]` には、`contract.outputs` に対応する成果物だけを列挙し、各ファイルの **SHA-256 実値** を書き込む。
- `verification.command` には Step 6 で実行した `contract.verify_command` の正確な文字列を、`verification.exit_code` には実際の終了コードを入れる。
- `decisions[]` の各要素では `source_refs` を **必ず** 埋める。空配列の decision が 1 件でもあると、後続の `python -m atg node finish` で reject される。
- 直接依存 handoff から受け取った根拠を流用した場合も、その参照元を `decisions[].source_refs` に残す。

## 9. 完了記録: `python -m atg node finish` で成功を記録し、失敗時は `python -m atg node fail` へ分岐する

handoff ができたら、その内容を ATG に検証させながら完了記録する。

```bash
python -m atg node finish <id> --handoff <handoff-path> --execution-id <execution-id>
```

- exit 0 なら当該ノードは成功で終端する。
- handoff 検証失敗、出力ハッシュ不一致、`source_refs` 欠落などの契約違反で完了できない場合は contract failure として扱う。

```bash
python -m atg node fail <id> --class contract --execution-id <execution-id>
```

- タイムアウト、レート制限、外部サービス瞬断などの一時障害で完了できない場合は transient failure として扱う。

```bash
python -m atg node fail <id> --class transient --execution-id <execution-id>
```

- `python -m atg node fail` の結果が `{"retry": true, ...}` なら Step 4 へ戻る。
- `{"retry": false, ...}` なら当該ノードは失敗確定し、その後続ノードはブロックされる。
- Step 6 の implementation failure でも、再試行可否の見方は同じである。

## 10. 再開手順: 中断後は `python -m atg node next` を再実行するだけでよい

プロセス中断後の再起動では、前回の worktree やローカル推測に頼らず、durable state からもう一度起動可能ノードを取得する。

```bash
python -m atg node next --graph <graph-path> --execution-id <execution-id> --limit <N>
python -m atg node start <id> --execution-id <execution-id>
```

- 再起動時は、`python -m atg node next --graph <graph-path> --execution-id <execution-id> --limit <N>` が返したノードだけに対して `python -m atg node start <id> --execution-id <execution-id>` を試みる。
- `python -m atg node next` は durable state を読み、**終端状態（`succeeded` / `failed` / `blocked`）のノードを `runnable` から除外する**。さらに durable state の重複起動拒否により、終端状態のノードは `python -m atg node start` でも起動されない（exit 1）。
- したがって、再開時に必要なのは `python -m atg node next` の再実行だけであり、終端済みノードを明示的に再実行する手順は不要である。
- **停止条件**: `runnable` が空配列になったら、起動できるノードはもう無い。未終端ノードが残っていないかを `python -m atg report` で確認し、残っていなければ `python -m atg run finish` へ進む。`runnable` が空でないのに `python -m atg node start` が exit 1 を返し続ける場合は、判定と durable state が矛盾している異常であり、ループを続けずに停止して報告する。
