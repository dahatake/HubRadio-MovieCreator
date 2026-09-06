# 1操作起動 実行・敵対的レビュー台帳

- 開始日: 2026-09-06
- 承認: 利用者が[実装計画](20260906-OneActionLaunch-ImplementationPlan.md)のデフォルトD01〜D18と全タスク実行を指示した。
- 基準commit: `49c5a49ea4b9c0d38841b212f8dea52529bdf945` (`main`)
- 開始時の未追跡ファイル: 上記実装計画書1件。既存tracked変更なし。
- 実施規則: 各タスクの検証→独立した敵対的レビュー→実在指摘の反映→反映確認を完了してから依存先へ進む。同じファイルや実行データを並列共有しない。
- 外部条件: 署名主体・実配布先は未選定。必要条件が未確認の試験は未実施/BLOCKEDとし、成功に置換しない。

## タスク状態

| ID | 状態 | 検証・レビュー |
|---|---|---|
| OSL-001 | 完了 | 要求ID・AC-007の9項目・相対リンク・差分検証成功。P2指摘1件を反映し再レビューPASS |
| OSL-002 | 完了 | 相対リンク・限定差分・診断確認成功。敵対的レビューPASS（修正指摘0件） |
| OSL-003 | 完了 | SDK標準publishと2回の実起動Ready成功。別worktree/検証データ。限定PoCの敵対的レビューPASS |
| OSL-004 | 完了 | 受入手順の文書・相対リンク・診断確認。敵対的レビューPASS（実行受入は未実施） |
| OSL-005 | 完了 | Fixed 152.0.4191.62 x64を実測固定。マニフェスト試験7/7、敵対的レビューPASS |
| OSL-006 | 完了 | 台帳CSVを解析しmanifestと照合。敵対的レビューPASS（指摘0件） |
| OSL-007 | 完了 | App build、focused 21/21成功。敵対的レビューPASS（指摘0件） |
| OSL-008 | 完了 | 開発版回帰、配布版build、MainWindowレビューPASS。今回の追加起動回帰102件成功。実GUI受入は後続 |
| OSL-009 | 完了 | 実payload、改訂ハーネス6合成＋18実項目成功。全指摘を反映・再レビューPASS |
| OSL-010 | 完了 | metadata/TSV/PRI/profile/raw XAML境界を反映。配布回帰と実候補生成成功、再レビューPASS |
| OSL-011 | 完了 | 専用profile試験・実publish・敵対的レビューPASS |
| OSL-012 | 完了 | `candidate-review-06`、内容manifest、SPDX 2.2 SBOM、検証サマリーを生成。全hash監査・全自動回帰・敵対的レビューPASS |
| OSL-013 | 完了 | candidate-review-06の公開layout、Contents、payload relation、SBOM、bundle headerを独立検証。PASS |
| OSL-014 | 本PC正常系PASS | candidate-review-06でcold Ready 44.023秒／warm 5.525秒、通常終了0、実Runtime・展開物不変を確認。clean OS・異常系・汎用smoke統合は別の未評価範囲 |
| OSL-015 | 完了 | candidate-review-06でnative imports、Worker health/protocol、FFmpeg、ffprobe、CPU/int8 model load、候補hash不変をPASS。ASR精度とclean OSは未評価 |
| OSL-016 | 手順作成済み・実行待ち | 6ケースの復旧手順を作成。実障害注入は未実行 |
| OSL-017 | BLOCKED / REVIEW_PENDING | candidate-review-06のlicense/notice/re配布条件をレビュー。法務、CVE、一般再配布承認は未成立 |
| OSL-018 | 完了 | 手順文書・参照確認・敵対的レビューPASS。署名主体/実配布先未選定は継続 |
| OSL-019 | 完了 | candidate-review-06を技術評価候補として固定。一般release承認ではない |
| OSL-020 | BLOCKED | production-channel clean Windows・標準ユーザー受入が未実施。Sandbox probeも完了証跡なし |
| OSL-021 | 明示スキップ | 今回は利用者指示により署名・実配布受入を実施しない |
| OSL-022 | 完了 | portable distribution runbookとMSIX READMEを同期。敵対的レビュー反映済み |
| OSL-023 | 完了 | getting-started/開発入口を候補評価導線へ同期。リンクと構成を再レビュー中 |
| OSL-024 | 完了 | architecture overviewを候補・展開cache・保存境界へ同期。clean OS等の未評価範囲を明記 |
| OSL-025 | 完了 | security/privacy・qualityを候補証跡、固定user-data、未完了gateへ同期 |
| OSL-026 | 完了 | README/troubleshootingをcandidate-review-06、cold/warm条件、一般配布未実施へ同期 |
| OSL-027 | 完了 | active計画§0.7へOSL状態・PK/Q/V2境界を同期。履歴snapshotは変更しない |
| OSL-028 | 未着手 | 全差分・証拠・文書同期の最終独立レビュー。OSL-020/021の未成立範囲を分離する |

## レビュー記録

### OSL-001

- 成果物: `docs/requirement-definition.md`のNFR-009/AC-007、範囲・優先度・未決事項の追補。
- 検証: 要求ID、AC-007の9項目、既存FR-034保持、全相対リンク、diff、診断を確認。アプリ実行試験ではない。
- 指摘・反映: 非採用モデルの一律同梱禁止が固定wheel内の台帳済みSilero付随資産と衝突。新規取得・追加同梱・有効化を禁止しつつ、既存固定資産のhash・許諾・notice保持を明示。再レビューPASS。

### OSL-002〜004

- ADR-0010とADR-0006の限定置換注記を作成。Windows配布だけを置換し、Azure・履歴を保持。レビューPASS。
- 別worktreeの限定PoCではSDK標準single-file publish、UI Readyを2回確認。EXE 238,282,100 bytes、展開536files。Insider/system Evergreen、小payloadでありclean OS等へ一般化しない。
- `tests/Acceptance/OneActionLaunch.md`を作成し、clean初回と補助検証、実Runtime、保存先、失敗・再展開、MOTWを分離。手順実装済み・実行未実施。

### OSL-005〜007

- 公式Fixed 152.0.4191.62 x64 CABを固定。展開548files / 837,201,247 bytes、全tree digestと代表2binaryのMicrosoft署名を記録。focused 7/7、レビューPASS。
- 第三者台帳へFixed Runtime専用行を追加。`redistribution-review-pending`を維持。
- Coordinatorへ明示folder契約を追加。focused 21/21、例外・path非公開、既存開発経路を保持してレビューPASS。

### OSL-008 / OSL-010 / OSL-011

- Fixed検出/初期化へ同一folder、実user-data folderとBrowserProcessIdのEXEをDB/bridge前に照合。失敗・終了時はowned WebViewを一度だけ閉じる。
- WebMessageを初回navigation前に有効化し、navigation＋React mount後の既存ping応答postまでReadyを待つ。JS受領ACKまでは保証しない。Web 13件、native起動関連102件成功、再レビューPASS。
- MSBuild連携ではmetadata評価順、payload内TSV、PRI衝突、非有効MRT hook、同一profile二重登録、開発profile/raw XAML混入を実ビルドで発見・修正。vendor bytesとXAML compile/PRI/XBFは保持。
- 専用single-file profileの設定・import・既存定数保持を検証。通常ビルドへ適用しない。

### OSL-009

- 初回レビュー指摘: 過剰な独自WinTrust、manifest値の重複pin、実正例/失敗後cleanup未検証。いずれも最小化・実証して再レビューPASS。
- payload: 3,260files / 1,819,381,920 bytes、digest `229fd9a6a95ae7fe4fb306e4e8a8253434e4408fc54319b9b48046ae4ca1c2a3`。
- 改訂run `review-73ff1eb5255e479581ec08308c5338cb`: 6合成＋18実項目成功。manifestの一意・Ordinal・完全key set・宣言digest、破損Worker拒否、partial cleanup、元入力不変を確認。結果SHA-256 `aa42672fb4382a45e3cd38a37abef07c45621166decc917292221c6d690af9e8`。
- Python legacy集合hashはtr-TRで不一致を実証。既存hash互換の`en-US`並びを明示し、tr-TRの実runtime検証で2,695files、native imports6件、health/protocol、tree不変を確認。

### OSL-012と16件の総合敵対的レビュー

- 初期builderの各失敗attemptは`.partial`として保全し、完成候補へ昇格していない。
  - candidate-osl012-01: 同一profile二重登録。
  - candidate-osl012-02: 統合CLR hostへ独立`coreclr.dll`を誤要求。
  - candidate-review-01: SBOM検証の生パラメーターを公開証拠として誤検査。
  - candidate-review-02: `ValidationErrors`の`Count=0` / `Errors=[]`形状を空辞書と誤認。
- [総合敵対的レビュー](../docs/dev/reviews/one-action-launch-adversarial-review.md)では、目的適合性・内容妥当性・整合性・品質運用性・根拠性の5軸で重複を除く16件（Critical 4、Major 9、Minor 3）を検出。全件へ最小修正を反映し再レビューした。
- 完成候補`artifacts/portable/candidate-review-03`:
  - 単一EXE: 2,058,629,690 bytes、SHA-256 `ff282f4d7e7135a475c7b159a43c4db30475265a63a8e9d93bb310359de7b4dc`。
  - Contents: 3,794files / 2,057,722,173 bytes、digest `73be339155140b21df05b185099ee053e77fe569b6152df945f59546d9cfe3b6`。
  - SPDX 2.2: 3,794files / 176packages。公開JSONの絶対path0件、開発用禁止物0件、`releaseApproved=false`。
  - release manifest SHA-256 `9634b5147f74a7feb130aee94e9855b5bb9c901a2518e8dc665043142990f998`。EXE/SBOM/公開検証サマリーとのhash一致を独立監査。
- 標準Release全回帰: Architecture 175/175、Core 665成功・1環境gate、Infrastructure 726成功・21環境gate、App 1,182成功・3環境gate、Web Vitest 474/474、accessibility 4/4、asset 6/6、Python 104/104。失敗0。環境gateはPASSへ変換していない。
- OSL-012のローカル候補生成は完了したが、実WebView、clean OS、署名、第三者条件、一般提供、業務E2Eを完了扱いにしない。

### OSL-014 このPCでの再実行（2026-09-06追記）

- 利用者の明示指示に従い、現在のPC（Windows 11 Enterprise Insider Preview build 29648、x64、非昇格）で再実行。製品コード・候補EXEを変更せず、案件操作や無関係なプロセス停止は行っていない。
- 対象candidate-review-06 SHA-256: `159161e3426c566cac6ea43bf6faa6587a2c1e192af81339341f83aa340a092e`。初回に存在しなかったDBが先行試行で作成されたため、それ以降は明示的な継続として保全。最終runではDBを含む325ファイルのbackupを検証。
- 最終run `host-20260906T131215-e5dee148`: cold Ready 44.023秒／warm 5.525秒、通常終了exit 0、強制終了なし。記録したWebView2子孫6＋7件すべて終了、最終専用path再走査でも残留0。
- 実展開3,793ファイル／2,047,587,645 bytesが候補manifestと全件一致し、cold/warm終了前後と独立再ハッシュで不変。候補EXEと最終試行前後のDB hashも一致。
- 結果JSON SHA-256: `8034468fe685c813263476cc59c8b1f0b9cc89c82b12d1152f138dc240a79392`。先行2試行のハーネス集計誤り・追加JS文言待ちは失敗履歴として保全し、製品障害や無関係なEdgeが原因とは断定しない。
- [詳細記録](../docs/dev/reviews/osl-014-local-host-verification.md)。PASSはこのPCの正常系に限定。JS受領ACK独立確認、異常系、clean OS／offline、署名・一般配布は未評価。OSL-020の合格や全タスク完了へ読み替えない。

### OSL-018 署名運用

- `docs/dev/packaging/portable-signing.md`を作成。署名後hash、内包binary、実取得/MOTW、保護維持、未選定時の一般提供保留を定義しレビューPASS。
- 署名、証明書store操作、外部配布は未実施。

## 利用者から追加された終了処理

- 全OSLタスク完了後にのみ、`CHANGELOG.md`の`Unreleased`へKeep a Changelog形式で追加する。OSL-013以降が未完了のため未実施。
- `hve`、Markdown query Skill、Code Query skillのPATCH更新: 対象なし。repository inventoryと`hubradio-web` / `hubradio-worker`を確認し、指定3パッケージは本リポジトリに存在しない。無関係なパッケージへ読み替えない。
- commit、push、公開release作成、証明書購入、信頼store変更は実施していない。
