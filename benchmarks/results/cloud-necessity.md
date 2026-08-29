# Z-001 Cloud Necessity Evidence

- 記録日: 2026-08-30
- 対象: 文章提案（topic metadata、hook、quiz、publishing wording）
- Subscription: `0e930937-43f5-484d-ba9e-1dd1151cf7ba`
- 判定: **ローカルmodel不達は確認済み。cloud necessity自体は未証明。利用者の明示指示によりAzure評価・最小構成作成を開始する。**

## 確認済み事実

`local-text-model.json`のcanonical evidenceは次を記録している。

- Foundry Local Qwen3 0.6B/4Bのstructurally eligible candidateは`0/2`。
- fixture windowのaccepted JSONは`0/6`。
- semantic groundedness、generated wording accuracy、production thresholdは未測定・未承認。
- source transcriptとgenerated wordingはcanonical evidenceへ保存していない。
- manual authoringは必須fallbackとして計画されたが、benchmarkでは実装・実行していない。

参照:

- canonical evidence file: `benchmarks/results/local-text-model.json`
- canonical evidence file SHA-256（ファイル全体をSHA-256で計算）: `ea6bc9855b6226b47ac735671273b19ec8d44da4199204f9e1ee045e4129d692`
- source benchmark run: `20260827T1055523174852Z-local-text-model-b53e420a`
- source run SHA-256: `6d8bc3275448e8a4528c62ab3d045c7756f63787b2d3415d10356701a76f1a36`

## この証拠から言えること

評価した2つのlocal modelは、評価したgrounded JSON契約を満たさない。したがって、この2候補を製品文章提案経路へ採用しない判断には根拠がある。

## この証拠から言えないこと

- すべてのlocal modelが不達であるとは言えない。
- manual authoringでは要件を現実的に満たせないとは言えない。
- Azure modelの日本語品質、意味保全、根拠性が合格するとは言えない。
- Azure利用がproductionで必須であるとは言えない。

## 今回のAzure開始根拠（Z-005の入力記録）

利用者は2026-08-30に、指定SubscriptionでのAzure作成と全タスク実行を明示指示した。この指示はresource作成の承認であり、上記の未測定事項を成功・必要性の証拠へ読み替えるものではない。

Azure経路は次の境界で評価する。これらはZ-001の合格主張ではなく、後続Z-007/Z-009が実装・検証する制約である。

1. 人が選択・マスク・確認した最小文字起こしspanだけを候補payloadにする。
2. 音声、動画、画像、ファイルpath、Windows SID、内部project IDを送らない。
3. model応答は未承認Suggestionとして保存し、自動採用しない。
4. local/manual fallbackを削除しない。
5. Structured Output適合を真実性・意味保全の証拠にしない。

## 結論

`Z-001`は「local model不達」と「cloud necessity未証明」を分離して記録した。後続Azure作成はcloud necessityが証明されたためではなく、利用者の明示指示による限定評価・最小構成として実施する。