# UX-004 UI変更前の基準測定

- 実施日: 2026-09-07。
- 対象: `2e85452b4a8747421831e3f0276199c07666e08f`のWeb production build。
- asset manifest: `src/HubRadio.Web/dist/asset-manifest.json`、SHA-256 `175ae30b9c7592edb2ed3cd3cf1d2dbdf6b2a1aa6fc5f6f9c6a6f24e7e9b87e4`（元bytes）。後続buildで更新されるため比較にはこのhashを使う。
- 条件: Windows上のVS Code integrated browser、loopback限定Vite preview。bridge未接続。実利用者DB・元動画・設定fileは使用していない。
- 表示: 1280×720 CSS px、light、devicePixelRatio `1.0000000149011612`。OS表示倍率はこの値から推測しない。document.visibilityStateは`hidden`。
- Web zoom: 独立の取得は未実施。1280×720は自動化で指定したCSS viewportであり、devicePixelRatioからzoomを推定していない。
- user agent: Code/1.136.1、Chrome/148.0.7778.280、Electron/42.10.0。

## 観測

scroll位置を(0,0)へ戻して測定した。documentのscrollWidthは1265px、scrollHeightは6867px。画面収まりは不成立だが、これは改修前の観測でありテスト実行失敗ではない。

| 見出し | 画面上端からの位置（CSS px） |
|---|---:|
| 案件一覧 | 302 |
| ジョブの進行状況 | 661 |
| プレビューモード | 968 |
| 代理動画プレイヤー | 1461 |
| 手動サムネイル生成 | 2134 |
| 解析結果 | 2511 |
| 文字起こし編集 | 2826 |
| 編集タイムライン | 3134 |
| 縦型レイアウト・字幕・テロップ | 3745 |
| 構成版の比較 | 4416 |
| レビュー・コメント・承認 | 5297 |
| 配信成果物と証跡 | 5969 |
| 案件設定 | 6584 |

ジョブ・プレイヤー・timeline・layout・review・delivery・設定にはID入力が表示されている。比較には版IDとdigest入力がある。新規source-onlyの確認画面はnative pickerが必要なため、この未接続表示から動作を判断しない。

private screenshot: `artifacts/validation/uiux-baseline-20260907-1280x720.png`（Git管理外）。これは媒体を含まない変更前画面である。

## 実行した自動検証

- `npm run build`: 成功。production runtime 3 packages、生成asset 3件を検証。
- Vitest: 60 files / 474 tests PASS。
- Node accessibility: 4/4 PASS。
- Node asset: 6/6 PASS。
- 既存の500kB超chunk warningが表示された。UI改善に便乗したbundler基盤の追加はしない。

## 未評価

実WebView2 client寸法、OS scale、物理keyboard、native picker、案件data付き画面、実処理、成果物、知覚的な使いやすさは未評価。embedded browserの`hidden`状態を、人によるkeyboard受入の証拠にしない。

UX-004はこの基準記録と[比較手順](../../../tests/Acceptance/UIUX_CompactWorkflow.md)を出力する。以後の測定は新runとして記録し、この変更前の値を上書きしない。