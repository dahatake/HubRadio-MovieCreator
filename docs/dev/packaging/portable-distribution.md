# 単一EXE候補の技術評価と配布境界（OSL-022）

確認日: 2026-09-06。これは、`artifacts/portable/candidate-review-06/Evidence/portable-release.json` と同候補の公開検証要約に基づく**ローカル技術評価のrunbook**です。一般提供の案内、署名手順、クリーンOSの受入記録ではありません。署名・実配布の将来手順は[単一EXEの署名・実配布手順](portable-signing.md)を参照してください。

## 現在の候補と技術評価

| 項目 | 記録値・結果 |
| --- | --- |
| 候補 | `candidate-review-06`、`0.1.0-osl.1`、x64 |
| 配布候補 | `App/HubRadio.App.exe` 1ファイル |
| EXE size | 2,058,629,690 bytes |
| EXE SHA-256 | `159161e3426c566cac6ea43bf6faa6587a2c1e192af81339341f83aa340a092e` |
| build scope | `bounded-local-candidate`。source tree は dirty、再現buildの主張は `false` |
| release approval | `releaseApproved: false` |
| 監査用Contents | 3,794 files、2,057,722,173 bytes、artifact-set SHA-256 `505aa9a4bfd64da2a3925401a0b36e37b5c63fbace6c9b66664aff903cdd091b` |
| SBOM | SPDX 2.2。公開要約の検証結果は `Success`、error count は 0 |

候補の公開境界は `App/`、`Contents/`、`Evidence/`、`Sbom/` です。`Contents/` は実際のbundle入力を検査する監査用コピーであり、フォルダー配布物ではありません。利用者に渡すことを想定する起動ファイルは `App/HubRadio.App.exe` だけです。

OSL-013の公開検証では、候補EXE、3,794-fileのContents、payload manifest、SPDXファイル一覧とSHA-256の対応を確認しました。OSL-014の現行ホスト参照試験（`current-host-reference-not-clean-OS`）では、この同一hashの候補について次を記録しています。

- cold 起動で可視ウィンドウまで 37,520 ms、native `Ready` まで 44,023 ms。
- warm 起動で可視ウィンドウまで 2,535 ms、native `Ready` まで 5,525 ms。
- Fixed WebView2の子プロセスが自己展開領域内にあり、記録済みhashと一致したこと。
- cold/warm とも、実展開tree 3,793 files が監査用Contents 3,794 filesから非bundle host入力1件を除いた集合と一致し、起動前後で不変だったこと。ユーザーデータは展開領域の外側で作成または利用されたこと。
- 両試行は通常終了し、候補EXEは不変だったこと。

これは、保存済みの単一EXEを1回起動してSDKの内部展開後にUIへ到達する、**one-action launch の現行ホスト上の技術評価**です。内部展開、WebView2子プロセス、利用者ごとのデータ保存は発生します。候補が単一の配布ファイルであることは、実行プロセスが1つだけであること、または一切展開しないことを意味しません。

## build担当者と利用者の前提を分ける

| 区分 | 前提・作業 |
| --- | --- |
| build担当者 | Windows x64 上の PowerShell 7+、固定.NET SDK、SBOM tool、Git、事前に実行した `scripts/build.ps1` と `prepare-payload.ps1`、固定済みのPython/FFmpeg/model/WebView2入力を使用する。これらは候補を組み立て・検査するための開発前提である。 |
| 利用者 | 承認済みの取得経路から得た候補EXEをローカルディスクへ保存し、1回起動する想定である。SDK、Git、Node.js、Python、pip、PowerShell 7、FFmpeg、追加Runtime、管理者昇格、環境変数設定を起動前提にしない。 |

候補は自己完結型の実行依存を内包する設計であり、Fixed WebView2、Python runtime、FFmpeg/ffprobe、採用CPU/int8 ASR model、Web asset、profileとその証拠を監査対象に含めます。利用者データは実行依存の展開場所と分離します。起動後にSDKが展開するため、必要な空き容量・初回待ち時間の正式な許容値はまだ確定していません。

## 公開候補とprivate evidenceの分離

候補の公開可能な要約は `candidate-review-06/` に置き、公開メタデータにローカル絶対path、raw tool parameter、例外本文を含めません。build logs、component inputのコピー、SBOM validationの詳細診断は兄弟の `candidate-review-06.private/` に隔離します。private evidenceは配布payloadでも一般向け文書でもなく、公開候補の子ディレクトリへ移動・混在させません。

候補の追跡には公開の `Evidence/portable-release.json`、`Evidence/sbom-validation.json`、SPDX SBOMを使用します。詳細なbuild診断、個人path、環境固有情報その他の非公開データはprivate evidence境界内だけで扱います。

## 未完了事項と禁止する読み替え

次の事項は、この候補の技術評価では完了・承認されていません。

- `releaseApproved` は **false** のままです。ライセンス／再配布条件、notice、CVE／security review、一般提供の承認を通過したとは扱いません。
- JavaScript acknowledgement は `NOT EVALUATED`、network isolation claim は `false` です。native `Ready` を業務E2E、ASR品質、ネットワーク完全隔離の成功へ読み替えません。
- **OSL-020 は未完了**です。現行ホスト参照試験はクリーンOS・標準ユーザーの受入ではなく、クリーンOS検証を主張しません。
- **OSL-021 は未完了**です。署名主体、証明書、実配布URL、実取得時のMOTW／policy受入が未選定・未検証であり、署名済み候補や一般提供を主張しません。
- SmartScreen、Smart App Control、組織policy、すべての端末での無警告起動、更新・rollback・uninstall、最小ハードウェア／空き容量の受入は未評価です。
- 未完了の業務機能や動画のrender/deliveryのE2Eを、起動技術の評価結果で完了扱いにしません。

一般提供を検討する際は、OSL-020のクリーンWindows受入と、OSL-021で同じ署名後bytesを実配布経路から取得して検証するgateを完了しなければなりません。未署名候補での現行ホスト試験は、その後の署名済み候補の試験を代替しません。

## 既存MSIXとの関係

この候補はMSIXを置き換えた一般配布物ではありません。既存の[MSIX packaging README](../../../packaging/HubRadio.Package/README.md)に記録されたPK-006の同一ホスト証拠、未署名状態、未完了gateは保全します。MSIXのinstall、upgrade、rollback、uninstallは将来区分のままです。
