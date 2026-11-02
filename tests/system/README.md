# System Test の実行境界

期待値の正本は `rdcheck.py show <AC-ID>` の承認済み AC。
実装・既存テストは入口と既存 runner の再利用を調べるために参照する。
アプリのソース、要求文書、カタログはここでは変更しない。

## 非 UI の事前確認

リポジトリのルートから実行する。デスクトップ UIA は起動しない。

```powershell
python -m pytest tests/system/test_media_tools.py tests/system/test_output_artifacts.py -q
pwsh -NoProfile -File tests/system/tooling/media-tools-regression.ps1 -Distribution <検証対象アプリのフォルダー>
dotnet test --project tests/system/contract/HubRadio.System.Contracts.csproj -c Release --filter FullyQualifiedName~PermissionContracts
pwsh -NoProfile -File tests/system/acceptance/run-integration.ps1 -Ac All -Preflight
pwsh -NoProfile -File tests/system/acceptance/run-integration.ps1 -Ac All -SupportingChecksOnly
python scripts/ledger.py preflight
python scripts/verify.py --docs-only --strict-ledger
```

`SupportingChecksOnly` は React/jsdom と native .NET の既存の結合試験を
要求ごとに集約・重複排除して実行する。**AC 全体の PASS でも台帳の PASS でもない。**
デスクトップ描画、実際のスクリーンリーダー、完成動画の画素・音声、
テンプレート導入前の独立した基準出力、自然言語の意味のレビューを代替しない。
`Preflight` は native runner の実際のテスト一覧と各 AC の全 `Name=`
フィルターを照合し、0 件や存在しないメソッド名を拒否する。

## 18 件の精査と担当入口

| 既存ケース | 要求 / AC | 自動検証する境界 | 残る条件 |
|---|---|---|---|
| IT-004（E2E-004 を統合） | FR-064 / AC-041 | 現在の Windows SID、実権限 DB、実 AuthorizationService、request parser、native browse。権限なしで forbidden、閉じた応答、picker 呼出し 0、設定バイト不変。付与後の陽性対照も実行 | conductor による記録実行（not_run）。E2E-004 は削除せず統合理由付きで停止 |
| E2E-008 | FR-132 / AC-139 | 同じ実権限基盤、native export、実 writer。拒否時 lookup/open/write 0、既存ファイルの内容・日時不変、SID/例外/パス非漏洩。付与後の実コピーも実行 | conductor による記録実行（not_run） |
| E2E-005 | FR-131 / AC-132 | JobThumbnail / ProjectJobList / AllProjectJobsPanel / JobProgressPanel / JobResultsBrowser の既存 React 結合試験と元の desktop 試験 | integration のため CHK-10。全画面の実機描画は別途実行 |
| IT-001 | FR-131 / AC-133 | 生成中・失敗・画像読込失敗の代替表示、他 Job 画像の継続、元の desktop 試験 | integration のため CHK-10。実レイアウト・取消を含む実機確認は未代替 |
| IT-002 | FR-131 / AC-134 | 完了・再作成後の画像更新、ID/digest 別の画像 binding、元の desktop 試験 | integration のため CHK-10 |
| IT-003 | FR-131 / AC-135 | 名前・日時・状態を含む accessible name、tab stop、Job 選択の既存 React 試験と元の desktop 試験 | integration のため CHK-10。実支援技術の読み上げは未代替 |
| E2E-025 | FR-136 / AC-150 | 一覧の項目、選択から Job request、演出適用の native 結合、元の desktop 試験 | integration のため CHK-10。完成映像の観察は未代替 |
| E2E-026 | FR-136 / AC-151 | 既定 planner / burned-in 出力の比較、元の desktop 試験 | integration のため CHK-10。導入前独立 golden の完全性はこの runner で主張しない |
| E2E-027 | FR-136 / AC-152 | ID/版の永続化・再読込・再現性、調整項目・変更あり表示、元の desktop 試験 | integration のため CHK-10 |
| E2E-028 | FR-137 / AC-153 | 7 定義、色/書体数、字幕文字保存、cue の上下限、テロップの許可出典、元の desktop 試験 | integration のため CHK-10。記録と完成映像の一致は別途観察 |
| E2E-029 | FR-137 / AC-154 | 語の分割/強調、倍率・動き、効果音設定、native render graph と元の desktop 試験 | integration のため CHK-10。完成映像/音声は別途観察 |
| E2E-035 | FR-137 / AC-155 | 登録前後の選択可否、権利状態、登録拒否、画像/色、native 出力と元の desktop 試験 | integration のため CHK-10 |
| E2E-030 | FR-138 / AC-156 | safe margin/字幕領域の肯定・否定配置、native render graph と元の desktop 試験 | integration のため CHK-10。画素の外接矩形は未代替 |
| E2E-031 | FR-138 / AC-157 | 話題切替、名前・肩書きの入力/確認/匿名/欠落、native record と元の desktop 試験 | integration のため CHK-10 |
| E2E-032 | FR-138 / AC-158 | 1080×1920、3段、15–20字、選択フック/フックなし警告、native 出力と元の desktop 試験 | integration のため CHK-10 |
| E2E-033 | FR-139 / AC-159 | 4.5:1、色以外の強調、否定/条件の保存、元の desktop 試験 | integration のため CHK-10。全7種の実映像と編集機能の完全観察は未代替 |
| E2E-036 | FR-139 / AC-160 | 同梱書体・版・OFL/改変拒否、font record、ASS、合成効果音、元の desktop 試験 | integration のため CHK-10 |
| E2E-034 | FR-139 / AC-161 | catalogue と guide の表現の自動チェック | manual のため CHK-10。語句走査だけで全説明の意味を保証しない。通常実行は自動チェック後も exit 非0で未実施の手動レビューを明示 |

上記 16 件の非 system AC を勝手に system に再分類しない。
要求の検証レベルと System Test 台帳の対象範囲の判断は conductor / rd-author へ返す。
古い「配布 exe がない」「権限再現方法が TBD」という停止理由は再利用しない。
既存ケースは削除せず、同じ AC のケースも追加しない。

## desktop 試験と権限の安全性

`run-integration.ps1 -Ac AC-150` のように `SupportingChecksOnly` を付けない
実行は、補助結合試験に加えて元の desktop 試験も実行する。
**UIA は常に 1 プロセスずつ**。conductor が別の UIA を実行している間は起動しない。
副作用を避けるため、権限契約は一時 DB のアプリ内役割だけを変更する。
現在の利用者に OS 権限を与えたり、別アカウントを作ったり、
実 Workspace の ACL や `workspace-location.json` を変更したりしない。

## テスト基盤の回帰防止

- `MediaTools.ps1`: 対象 sandbox の `Tools/ffmpeg.exe` / `ffprobe.exe` が最優先。
  PATH は開発ビルドの fixture 生成・検査だけの fallback。
  別 checkout の Tools を暗黙探索せず、アプリの不足依存も補充しない。
  回帰試験は PATH 空・同名 PATH ツール・空白を含むパス・欠落を確認する。
  `-Distribution` 付きでは実同梱バイナリで MP4 生成→probe→PNG 抽出を行う。
- `OutputArtifacts.ps1`: manifest のファイル名を固定せず、
  公開 evidence の `Manifest/<artifact-id>.json` も探索する。
  AC-079 は存在だけでなく output evidence のない空 manifest を拒否する。
- AC-038: HTML select は候補移動だけでなく ENTER で確定後に観察。
  2・3・4 列と 4 件上限の期待値は変更しない。UIA での再実行は別途必要。
- テストの pass と台帳の pass は別。ここで追加/再設計した有効ケースは
  not_run で渡し、conductor が最新の実行結果だけを `ledger.py run` で記録する。
