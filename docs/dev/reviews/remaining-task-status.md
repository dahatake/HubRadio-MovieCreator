# 残タスク実行状況（2026-09-06）

## 完了（成果物・本PC正常系）

- **OSL-013**: `candidate-review-06` を独立検証。公開rootは `App/Contents/Evidence/Sbom` のみ、Contents 3,794件、SBOM SPDX 2.2、payload relation、単一EXE bundle header、hashを確認。PowerShell終了コードの残留を`$?`で分離してPASSを確定。
- **OSL-014 本PC正常系**: 利用者の明示指示で現在のPC・非昇格利用者環境にて再実行。cold Ready 44.023秒、warm 5.525秒、両方exit 0・強制終了なし。同梱WebView2、実展開3,793件のhash一致・不変、対象プロセス残留0、backup保持、最終試行前後のDB hash一致を確認。詳細は[実行記録](osl-014-local-host-verification.md)。clean OS、JS受領ACKの独立確認、異常系再実行はこのPASSに含めない。
- **OSL-015**: candidate-review-06で実行。native imports、Worker health、Worker protocol、FFmpeg、ffprobe、CPU/int8 model load、候補hash不変がPASS。ASR精度とclean OSはNOT EVALUATED。
## 実装・文書完了（実行／承認待ち）

- **OSL-016**: `tests/Reliability/portable-launch-recovery.md` を作成。6ケース（再展開、欠落/破損、容量不足、アクセス拒否、startup failure、正常再起動）。手順自体は未実行。
- **OSL-017**: `docs/dev/licenses/release-review.md` と通知インベントリを作成・更新。技術評価候補として分類し、一般再配布・法務・CVE承認は未成立。
- **OSL-019**: candidate-review-06を技術評価候補として固定。EXE hash `159161e3426c566cac6ea43bf6faa6587a2c1e192af81339341f83aa340a092e`、Contents digest `505aa9a4bfd64da2a3925401a0b36e37b5c63fbace6c9b66664aff903cdd091b`、release manifest hash `1d44ef54dc805227f0438a76816a7de044dcd8ab3da29353ee4e95be369e52be`を記録。候補の正式なlicense/CVE/clean OS承認は未成立。

OSL-018は署名・配布運用文書を完了しているが、署名・実配布自体は未実施である。

## BLOCKED / 未完了

- **OSL-014 残る受入範囲**: 本PC正常系は上記PASS。Sandboxの過去2試行はprobe証跡未生成のままであり、その履歴を成功へ変更しない。旧smokeの終了例外は親アプリの終了待ちで、先行例外も隠し得る。「無関係なWebView2が多数あることが原因」という説明は未実証のため撤回する。異常系・別環境・汎用smoke統合は別の未評価範囲。
- **OSL-016**: 利用者の明示指示によりスキップ。復旧手順は作成済みだが、実障害注入は実行していない。
- **OSL-020**: 利用者の明示指示によりスキップ。Windows Sandboxの実行ファイル、Containers-DisposableClientVMパッケージ、vmcomputeとSandbox起動セッションは確認したが、LogonCommandの結果は未生成であった。さらにSandboxはWindows 11 Enterprise Insider Preview build 29648であり、production-channel clean Windowsの代替ではない。clean OS受入は未実施のままである。
- **OSL-021**: 利用者の明示指示により今回スキップ。署名・外部配布を実行していない。
- **OSL-022〜027**: 文書・計画同期を実施済み。各文書の敵対的レビューで確認した事実不一致を反映済み。OSL-028の最終独立レビュー待ち。
- **OSL-028**: 未着手。全差分・証拠・文書同期の最終独立レビュー。OSL-020のclean OS未成立、OSL-021の明示スキップ、業務E2E未完了を最終結果から分離する。

## 検証記録

- 今回の検証は[OSL-014本PC正常系](osl-014-local-host-verification.md)のみ。以下の全回帰件数は従来の記録であり、今回全回帰を再実行したという主張ではない。製品コード・候補EXEは変更していない。
- focused OSL-012/013/015関連テストは、修正後に再実行が必要なテストfixtureを除き、候補生成前の126件回帰は成功。
- 標準Release回帰: Architecture 175/175、Core 665成功・1環境gate、Infrastructure 726成功・21環境gate、App 1,182成功・3環境gate、Web 474/474・accessibility 4/4・assets 6/6、Python 104/104、失敗0。
- 最終候補06はprivate証跡を `candidate-review-06.private` へ分離。公開candidateにはprivate BuildLogs/ComponentInputsを含めない。
- `releaseApproved=false`を維持。未実施のclean OS、署名、一般提供、業務E2EをPASSへ変更していない。
