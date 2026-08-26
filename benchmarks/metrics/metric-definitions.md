# Stage 0 指標定義

- タスク: P-009
- 決定日: 2026-08-27
- 状態: 指標式を固定。合否値は未設定

## 合否値の決め方

1. 現行手作業を同じ素材で測る。
2. 候補技術を同じ入力、同じ注釈、同じ計算式で測る。
3. 平均だけでなく件数、中央値、上位分位、失敗カテゴリを保存する。
4. 編集、コンテンツ、情報管理、開発の担当者が、業務効果と失敗時の手動経路を確認する。
5. Stage 1開始前に、指標ごとの合否値、適用素材、除外条件、責任者を`stage-0-report.md`へ記録する。

数値データがない状態で合否値を設定しない。

## 適用時期

本書はStage 0だけでなく、同じ計算規則をStage 1/2の受入へ引き継ぐ指標辞書である。媒体・ASR・解析・manual renderに適用できる指標はStage 0で測る。完成UIが必要なkeyboard/focus/contrast等はUI実装後のQ-009で初めて測り、Stage 0で測定済みと扱わない。

## 文字起こし

### CER

$$
CER = \frac{S + D + I}{N}
$$

正解文字列をUnicode NFCへ正規化し、比較用の空白・句読点規則を評価manifestへ保存する。`S`は置換、`D`は削除、`I`は挿入、`N`は正解文字数。

### WER

$$
WER = \frac{S_w + D_w + I_w}{N_w}
$$

日本語tokenizerと版を固定できた試験だけで使用する。CERと置き換えない。

### 固有名詞誤り率

注釈済み固有名詞spanのうち正規表記と一致しない件数を、評価対象固有名詞総数で割る。辞書適用前後を分ける。

### 時刻誤差

注釈開始/終了と推定開始/終了の絶対誤差をmicrosecondで計算し、中央値と上位分位を保存する。単語時刻と発話時刻を分ける。

## 話者

DERはmiss、false alarm、speaker confusionを含め、採用ライブラリの計算規則とcollar/overlap設定を結果manifestへ記録する。設定不明のDERを比較しない。

話者数誤差、手動再割当区間数、重なり発話の誤りを別集計する。

## shot / transition

- Precision = 正しく対応した予測境界 / 全予測境界
- Recall = 正しく対応した予測境界 / 全正解境界
- F1 = PrecisionとRecallの調和平均

境界許容窓を複数記録し、hard cutとgradual transitionを分ける。

## topic

- 境界Precision/Recall/F1
- 境界絶対誤差
- 人が移動した境界数と移動量
- title/summary出力に有効な根拠spanが付く率と、人が注釈した証拠範囲へ正しく到達する率
- 主話者、keyword、公開注意の手動修正件数

## 削除候補

種別ごとにPrecision/Recallと候補件数を記録する。特に`keep`を削除候補にした件数を表示し、候補を自動削除しない。

## 見どころ

- Precision@K
- 採用率
- 境界修正率
- 近似重複率
- 意味変更として却下された率
- policy gateで除外/社内限定となった件数

`K`は評価時に提示可能件数として関係者が決める。学習前のheuristic版と将来版を動画単位で分離して比較する。

## リフレーム

sampling時刻ごとに次を測る。

- 重要領域カバー率
- crop外の重み付き面積
- viewport中心の速度・加速度
- layout切替数
- manual keyframe数
- 自動解なし区間数

人による「不自然な画角」判定を別集計する。

## 字幕・テロップ

- 画面外、safe zone、顔/資料/UI重なり件数
- Cue逆転、重複、空文字件数
- 発話先行/遅延の分布
- profileで定義した読速違反件数
- manual改行、位置、時刻、文言の修正件数
- 強調候補の採用/却下件数

## 音声

- program loudness
- true peak
- clipping件数
- 左右差
- 無音/急変検出件数
- 補正前後の人によるA/B判定

媒体ごとの目標値はAudioDeliveryProfile確定後に記録する。

## レンダー

- wall-clock時間
- peak working set / GPU memory（取得可能な場合）
- 成功、失敗、取消、再試行件数
- 出力durationと期待durationの差
- A/V同期差
- 同じRender Manifestから生成したgraph/引数列の一致
- decoder後の視覚/音声差分（採用時）

異なるhardware encoder間のbyte一致を合格条件にしない。

## セキュリティ・コンプライアンス

既知PII、秘密、公開不可、権利未確認のfixtureについてPrecision/Recall、候補件数、見逃し、過検出、判断時間を記録する。AIだけで公開可を確定しない。

## 追跡性

- OriginRefなしの確定要素数
- 逆引き不能な出力range数
- 誤ったSourceSpanへ移動した件数
- AI/人区分欠落数
- 使用素材とRightsRecordの未対応数

構造ゲートとして、確定要素のOriginRef欠落と逆引き不能は最終出力を保留する。

## アクセシビリティ

- contrast違反
- keyboardだけで到達不能な操作
- focus消失
- 色だけで区別する状態
- 字幕欠落区間
- mute状態で理解不能と判断された主要内容

## 業務効果

- 案件総リードタイム
- 人の編集時間
- 人のレビュー時間
- 手戻り回数
- 生成した成果物数
- 別担当者への引継ぎ時間

現行手作業と同じ成果物範囲で比較する。
