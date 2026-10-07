# ショート動画の作り方の指針（FR-054の組み込みの規則の根拠）

確認日: 2026-10-06。規則`buzz-editing-preset.v1`（`src/HubRadio.App/Projects/BuzzEditingPreset.cs`）の値の根拠を記録する。アプリは実行時にネットワークへ接続せず、この表だけを使う（FR-054.5）。規則を変えるときは、このメモ、`BuzzEditingPreset.cs`、要求定義書の表FR-054-T1を同時に更新する。

出典の区分:

- **公式**: プラットフォームの公式のヘルプ。
- **実務**: 実務者の解説記事。効果は保証されない（A-054-03）。

| 指針 | 区分 | 出典 | 規則での使い方 |
| --- | --- | --- | --- |
| 正方形・縦長の動画は、3分までYouTube Shortsとして扱われる（2024-10-15以降） | 公式 | YouTube Help「Understand three-minute YouTube Shorts」 https://support.google.com/youtube/answer/15424877 | 縦長の元動画はショート向きと見なし、縦型ショートの本数を最大（3本）にする |
| 終了画面は5〜20秒（25秒以上の通常の動画） | 公式 | YouTube Help「Add end screens to videos」 https://support.google.com/youtube/answer/6388789 （検索結果の要約で確認） | 8分以上の動画は終了画面を最大の20秒にする |
| YouTubeは音量を約-14 LUFSに正規化する | 公式 | YouTube Help（音量の正規化） https://support.google.com/youtube/answer/7016692 （検索結果の要約で確認） | 選べる最大の-16 LUFSがすでに既定値のため、規則では変えない |
| 最初の1〜3秒で、見続けるかスワイプするかが決まる。冒頭の挨拶や前置きを避け、結論や見せ場から始める | 実務 | shortgenius.com「10 YouTube Shorts Best Practices for 2026」、miraflow.ai「YouTube Shorts Best Practices in 2026」 | つかみ（`editing.shortHookMaximumStart`）を2秒以内にする |
| 音を出さずに見る人が多いため、字幕（焼き込み）を常に入れ、短く読みやすくする。画面下部はUIに隠れやすい | 実務 | 同上、jetfuel.agency「How to Optimize Short-Form Video in 2026」 | 縦型の字幕1行を9文字にする（字幕の位置は既存の安全領域、FR-014） |
| ショートの視聴維持は15〜35秒程度で高い。内容が重ならない短い切り抜きが好まれる | 実務 | 同上、reelsmakerai.com「Short-Form Video Strategy 2026」 | 45秒未満の元動画は縦型ショートを1本にする。3分未満は2本 |

追補（2026-10-07、規則`buzz-editing-preset.v2`。質問票Q-001・Q-003の回答Aによる）:

- 短い間を詰めるジャンプカット（上の「最初の1〜3秒」「テンポ」の指針）: 縦長の元動画では、間を詰める強さ（`editing.pauseTightening`、FR-057）を「強め」（0.4秒以上）にする。
- 強調テロップ（上の「音を出さずに見る人が多い」の指針）: 縦長かつ8分未満の動画では強調テロップ（`subtitles.emphasisTelopAmount`、FR-059）を「多め」に、8分以上の長い動画では「少なめ」にする。
- 効果音（FR-060）: 既定で有効とし、規則では変えない（音量の好みは利用者が設定で決める）。
