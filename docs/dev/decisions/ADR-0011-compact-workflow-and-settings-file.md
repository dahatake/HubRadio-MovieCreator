# ADR-0011: コンパクトな状態タブと明示的な設定file

- 状態: Accepted（2026-09-07の利用者指示による第2版A01〜A20の採用）。実装の完成を示さない。
- 正本: [要求定義](../../requirement-definition.md)、2026-09-07承認済みのUI/UX第2版方針（A01〜A20、本文へ恒久化済み）。
- 根拠: [UI研究](../research/ui-ux-research.md)、[変更前測定](../reviews/ui-ux-baseline.md)。

## 決定

1. 通常画面は元動画／自動編集／仕上がり／保存の4状態タブ、設定は独立画面とする。必須入力と開始確認は増やさない。
2. 必要なeditorだけを表示し、tab移動は取消やresetと分離する。未保存編集、時刻、undo、job監視を保持する。
3. 1280×720 CSS px・Web zoom 100%を通常画面の無scroll基準とする。長文、timeline、拡大時は例外。基準測定のOS倍率・Web zoomは未測定であり、測定済み最小環境とはしない。
4. 既存React、provider、CSS tokenを継承する。4タブ用の有限な状態だけを追加し、Router／状態管理library／汎用workflow frameworkを追加しない。
5. SQLiteを正本とし、全体既定値と案件initial/currentを分離する。通常保存は1項目。全体変更は固定済み下書き・既存Compositionへ遡及しない。
6. `setting.txt`はper-user app root直下の明示export/import。Workspace外、backup対象外、起動時自動読込なし。raw4MiB/canonical1MiB、strict UTF-8/profile/digest、file単位ACLと安全な置換を採る。
7. file内の値の手編集を通すためにdigestを緩めない。表現上のBOM・改行・空白差だけをfile境界で許容する。
8. 予定・実生成・実保存・評価・人の確認を別表示にする。実V2契約の未受領をfixtureで埋めない。

## 既存V2との責務

V2-010/011/013のUI非依存な実処理・policy・出力契約は別枝で作成・検証される。UX側は[UI実装契約](../architecture/ui-workflow-contract.md)に従う表示・設定保存を先行する。両枝が成立してからUX-040A〜Dで結合する。

V2-012の画面整理責務は今回のUX taskへ引き継ぐが、過去版を復元する新UI/RPCはV2-012のR-003再受入責務に残す。既存`RollbackAsChild`の型・実装があることを新UIが完成した証拠としない。

G-UIはUI・設定の部分到達、G-LIVEは実データ接続の到達であり、製品全体・一般提供とは別。任意の人手観察は未実施ならNOT EVALUATEDとし、生成・releaseの必須gateへ戻さない。

## 却下

fileを唯一の正本へ置換、DBとの常時同期、手編集用の新設定言語、全UI kit置換、全CSS再整理、外部公開や新rendererの抱き合わせをしない。レビューが推測した現障害を事実として実装理由にしない。

## 検証・記録

各taskはfocused検証、独立した敵対的レビュー、実在指摘の反映確認後に後続を開始する。共有schema等の不可分変更は直列taskの統合単位で検証する。全回帰はG-UI前後と実接続後に実施し、恒久記録は[詳細実装計画](../planning/implementation-plan.md)、[品質記録](../quality.md)、関連レビューへ集約する。