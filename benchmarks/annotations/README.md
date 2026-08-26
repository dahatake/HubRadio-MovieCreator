# Stage 0 注釈規約

- タスク: P-009
- 対象素材: `benchmarks/fixtures/fixture-manifest.json`
- 目的: 候補技術の採否と手動フォールバック条件を、同じ実素材と同じ注釈で比較する

## 原則

- 閾値や合否値は注釈前に作らない。
- 公開ベンチマークの値をHubらじお素材の期待値として流用しない。
- 自動出力を見てから正解を変更しない。
- 判断不能は無理に正解化せず`uncertain`と理由を記録する。
- 元動画は変更せず、注釈は元素材PTSの有理時刻または整数microsecondで保存する。
- 氏名、肩書、機密、権利の最終判断は権限を持つ人が行う。

## 共通レコード

各JSONLレコードは最低限次を持つ。

```json
{
  "schemaVersion": "1.0.0",
  "assetId": "source-ios-20260615-024326000",
  "annotatorId": "opaque-local-id",
  "annotationType": "transcript",
  "sourceRange": {
    "startMicroseconds": 0,
    "endMicroseconds": 1000000
  },
  "label": {},
  "confidence": "certain",
  "notes": null
}
```

`annotatorId`へ表示名やメールアドレスを書かない。対応表が必要な場合はGit対象外の保護領域へ置く。

## 注釈セット

### 1. 文字起こし

ファイル: `transcript-reference.jsonl`

- 発話単位の開始/終了
- 正解文字列
- 話者の匿名ラベル
- 固有名詞spanと正規表記
- 重なり発話、雑音、聞き取り不能

日本語の基本指標はCERとし、分かち書き規則を固定できた場合だけWERも併記する。

### 2. 話者

ファイル: `speaker-reference.jsonl`

- 匿名話者区間
- overlap区間
- 画面外話者
- 手動でも判定不能な区間

本人名との対応は別の保護データとし、モデル評価は匿名ラベルで行う。

### 3. shot / transition

ファイル: `shot-reference.jsonl`

- hard cut
- fade/dissolve
- camera switch
- slide/screen-share transition
- 判定許容窓は評価時に分布を見て決め、ここで固定しない

### 4. 顔・主要領域

ファイル: `visual-reference.jsonl`

samplingした時刻ごとに、顔bbox、資料bbox、重要文字領域、遮蔽、横顔、小顔、画面外を記録する。人物同定は行わない。

### 5. topic

ファイル: `topic-reference.jsonl`

- topic開始/終了
- 人が付けた仮title
- titleを支持する元素材の`titleEvidenceRanges`
- summary正解を作る場合は、それを支持する`summaryEvidenceRanges`
- 主話者
- keyword
- 重要発言
- 想定視聴者
- 公開注意候補

境界とtitleの評価を分離する。AI生成title、AIが返した`evidenceSpanIds`、採否状態は正解注釈へ書かず、`benchmarks/results/`側で人の証拠範囲と比較する。

### 6. 削除候補

ファイル: `removal-reference.jsonl`

要求FR-005の種別ごとに`candidate`、`keep`、`uncertain`を付ける。

- silence
- restart/correction
- repetition
- preparation
- recording-problem
- off-topic
- cough/noise/wait
- explicit-edit-instruction

自動削除の正解ではなく、人へ提示すべき候補かを評価する。

### 7. 見どころ・hook・quiz

ファイル: `editorial-reference.jsonl`

- 単独で意味が通るか
- 不足文脈
- 学び
- 公開policy
- 音声/映像利用可否
- 元発言の意味保全
- quiz回答の根拠と曖昧性

魅力度と安全性を別項目にし、安全性を加点で相殺しない。

### 8. 字幕・画角

ファイル: `layout-reference.jsonl`

- Cueの意味単位
- 発話同期
- manual改行
- safe zone違反
- 顔/資料/UIとの衝突
- 9:16 layout mode
- manual keyframe
- 自動解なし区間

## 評価者と一致

- 技術注釈: 開発担当
- 字幕・編集・見どころ: 編集担当
- 発言意味・quiz根拠: コンテンツ責任者
- 公開注意: 情報管理担当
- ブランド: ブランド担当
- accessibility自動検査: 開発担当
- keyboard、字幕/SDH、mute理解の人評価: accessibility担当。専任者がいない検証では編集担当とコンテンツ責任者が代行し、専任評価でないことを結果へ明記

複数評価者を確保できる項目は独立注釈し、一致/不一致の件数を保存する。1名しかいない項目は単一評価であることを明記し、評価者間一致を捏造しない。

## 版管理

注釈ファイルごとに素材SHA-256、annotation schema版、作成日時、更新理由をsidecar manifestへ記録する。自動結果は`benchmarks/results/`へ分離し、正解注釈へ混ぜない。
