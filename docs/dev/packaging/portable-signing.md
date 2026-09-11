# 単一EXEの署名・実配布手順（OSL-018）

確認日: 2026-09-06。根拠は [NFR-009 / AC-007](../../requirement-definition.md)、[ADR-0010](../decisions/ADR-0010-one-action-portable-launch.md)、利用者がデフォルトを採用し全タスク実行を承認したD11〜D13、OSL-018〜021（承認内容は[詳細実装計画§0.7](../planning/implementation-plan.md#07-os-onlyの1操作起動2026-09-06追補)へ恒久化）。

## 1. 現在の状態と適用範囲

| 項目 | 現在の状態 |
| --- | --- |
| 署名発行元（publisher） | **未選定（NOT SELECTED）** |
| コード署名証明書・署名手段 | **未選定（NOT SELECTED）** |
| 実配布URL・配布範囲 | **未選定（NOT SELECTED）** |
| 署名・外部release | **本書の作成では実施していない**。実EXEの署名状態も未検査 |
| 一般提供gate OSL-021 | **BLOCKED**。外部入力と実受入証拠が未成立 |

**手順の文書化は一般提供gateのPASSではない。** 本タスクはこの文書だけを作成し、証明書ストアの列挙・変更、署名、起動・build、tool取得、配布操作を行わない。独立レビューは別途行う。

| 分岐 | 実施・判定の境界 |
| --- | --- |
| OSL-019 ローカル技術候補 | OSL-013〜017の成果物に基づき、評価利用が許される候補のnotice/SBOM、hash、実際の署名状態を固定する。署名・配布先未選定だけでは停止しない |
| OSL-020 ローカル起動検証 | OSL-004/019に基づき、クリーンOS・標準ユーザーでAC-007.1〜8の実結果を記録する。未署名・policy・環境制約を明記し、一般提供の成功とはしない |
| OSL-021 一般提供の別分岐 | OSL-018/019/020と、選定済み署名・配布条件を入力に、署名後の最終bytesで起動を再受入し、実取得/MOTW/policyを検証する |

許諾済みローカル作業やその文書化に、新しい署名承認待ちを追加しない。ただし評価許諾不明、必要環境不在、保護による起動拒否は該当試験をBLOCKEDとし、保護を解除してPASSにしない。

## 2. 運用担当者が入力を確定する項目

以下は、入力が入手できた時点で記録する**非秘密の識別情報**。本書では値を推定せず、署名・公開に進むのは具体の対象・操作が既存の許可範囲に含まれる場合だけとする。

| 入力 | 必要な内容 |
| --- | --- |
| 対象候補 | version、source commit、EXEのartifact相対path・署名前SHA-256、file inventory/component manifest/SBOMの版・digest、OSL-017/019/020の証跡参照 |
| 発行元・操作権限 | publisher名と証明書Subjectの対応、運用担当者、当該候補の署名利用を許す既存記録 |
| 公開証明書情報 | Subject、Issuer、serial、thumbprintとそのhash方式、有効期間、公開鍵algorithm、Code Signing EKU、信頼先rootと中間証明書の識別情報 |
| 署名・検証手段 | 既存の承認済みtoolの名称・版、鍵保管方式の非秘密の参照、承認済みtimestamp authority（TSA）の正確なURL・方式。新規署名はSHA-256、RFC 3161 timestampもSHA-256とする |
| 配布経路・範囲 | 正確な承認済み取得URLとredirect先、配布先の管理者、対象利用者・公開範囲、実取得試験と一般提供それぞれの既存許可の参照。認証token等は記録から除く |
| 検証環境 | Windowsのedition/version/build/channel、x64、クリーンOS・標準ユーザーの根拠、導入済み依存の初期状態、browser名・版、SmartScreen/SAC/組織policyの状態・識別情報、network条件 |

一般提供では、対象OSで信頼されるroot chainへ連なる、承認済みのコード署名証明書を使う。任意の証明書や自己署名で代用しない。Smart App Control（SAC）の署名互換性は確認時点の公式資料ではRSAが必要で、ECCは未対応である。[M2][M3]

秘密鍵・PFX・PIN・password・tokenを依頼／収集しない。鍵は既存の許可済み保管境界内だけで扱い、**Git、`.env`、ログ、共有証跡、配布payloadに一切保存しない**。本件で`.env`は不要。証明書の作成・購入、アカウント／サービス／配布基盤の新設、Azure等の選定・provisionは本書の権限に含まれない。配布URLやTSA URLを例から補完しない。

## 3. 署名と最終bytesの固定（将来のOSL-021実施手順）

1. **元候補を保存する。** OSL-019の候補・hashとOSL-020の実結果を保持し、署名版を別の候補として追跡する。未署名試験を署名後試験へ遡及変更しない。OSL-017/Q-010の第三者再配布条件・notice・SBOM・CVEの未確認は一般提供の保留条件として残す。
2. **全実行binaryを確認する。** 実payloadと実展開物から、外側EXE、全child EXE、実行・ロード対象のDLL、Python拡張の`.pyd`等を洗い出す。.NET/Windows App SDK、Fixed WebView2、Python/wheel native依存、FFmpeg/ffprobeを含め、相対path・由来・SHA-256・署名者・署名検証結果をinventoryへ対応付ける。拡張子だけの探索や代表1件の検査で全件確認としない。vendor署名の存在・有効性を推測しない。
3. **必要な内包物の署名はbundle前に解決する。** 既存vendor署名を一括上書きしない。第三者binaryへの署名追加もbytes/hashを変えるため、許諾・出所・既存の固定hash検証契約との両立を先に確認する。承認された派生物を扱える場合でも、upstream原本の固定hashを残し、署名前後hash・変更理由・署名主体・tool版を別に追跡し、検証側との対応をレビューする。既存契約と両立しない変更は一般提供をBLOCKEDとして担当タスクへ戻す。**固定manifest/catalog/hashや`releaseApproved`を黙って書き換えて通さない。** 保留・非採用モデルを追加しない。
4. **inventory/SBOMを更新してから最終bundleを作る。** 確定した内包bytesにfile inventory/component manifest、notice、標準形式SBOMを一致させ、tool/schema版・未解析範囲を記録する。必要な供給網検証を再実施し、既存SDKの単一EXEへbundleする。埋め込む文書の変更もこの時点で完了させる。独自の署名framework/scriptや別の配布方式は導入しない。
5. **bundle完了後の外側`HubRadio.App.exe`に署名する。** 選定済みの発行元・証明書を明示的に使用し、SHA-256署名と承認済みTSAのRFC 3161/SHA-256 timestampを付ける。証明書の自動選択で別主体を使わない。timestamp失敗を単なる警告として合格にしない。[M4][M6]
6. **署名の目的・信頼・timestampを検証する。** 承認済み検証手段で、ドライバー用ではなくAuthenticodeの認証検証policyを使用する。外側EXEの埋め込み署名と各内包物の全署名について、整合性、期待する署名者、Code Signing EKU（`1.3.6.1.5.5.7.3.3`）、対象OSでのroot chain・有効期間・失効確認の結果を記録する。timestampの存在だけでなく、対象署名との結び付き、TSAの署名・用途・信頼chain、署名証明書の有効期間内の時刻であることを確認する。照会不能・不明・警告を成功へ丸めない。有効なtimestampは期限後検証に役立つが、失効やpolicyを無条件に無視できる保証ではない。[M4][M6]
7. **最終EXE全bytesのSHA-256を固定する。** 全署名・timestamp処理後のファイル全体をhashし、これを`Hfinal`とする。Authenticode内部digestや証明書thumbprintとは区別する。[M5] サイズ、署名検証記録、最終file inventory/manifest・SBOMの参照とdigestを、外部の候補記録で`Hfinal`へ結び付ける。SBOMに外側EXEのchecksumを記す場合も外部成果物を更新し、自己参照hashを埋め込むためにEXEを変更しない。以後の再署名・timestamp追加・再bundle等は別hashの候補となり、照合と再受入をやり直す。

## 4. 最終候補の起動と実取得を再受入する

- **署名後の起動:** `Hfinal`と一致するEXEで、OSL-004/020のAC-007.1〜8を再実行する。対象はサポート中のproduction-channel Windows 11 24H2以降x64のクリーンOS・標準ユーザー。保存済みEXEへの1起動指示で既存UI、Web bridge、動画選択・設定操作へ到達することを確認する。実使用Fixed WebView2の版・配置、Python health/protocol/native imports、FFmpeg/ffprobe、採用CPUモデルのoffline loadも独立に確認する。
- **起動の境界:** 依存の手動導入・昇格・環境設定・起動時downloadなしを確認し、終了・再起動・再展開でもper-userデータと実行依存を壊さないことを検証する。cold/warm・可視UI時間・容量を実測し、許容値未合意の性能受入はPASSにしない。異常系は別の試験用copyへ隔離し、変更hashと期待する拒否を記録して`Hfinal`の原本を保全する。
- **実ダウンロード:** 既存許可の範囲で、承認された実URLから実際のbrowserで取得する。取得日時（UTC）、URL/redirect、browser版、network状態、保存されたEXEのhash、Mark of the Web（MOTW／`Zone.Identifier`）の有無・zoneを記録する。token・個人path等は共有証跡から除く。MOTWを削除・偽装しない。自然にMOTWが付かない経路はその事実と適用範囲を記録し、MOTW付き取得の受入とは表示しない。必要条件を再現できない試験はBLOCKEDとする。
- **同じbytesでの確認:** ダウンロード直後・初回実行前のSHA-256が`Hfinal`と一致することを確認する。取得物の署名検証と上記起動受入を対象policy下で実行し、実行後もEXEおよび展開済み実行依存のhashを照合する。配布元／経路がbytesを変えた場合は不一致として停止する。MOTWなしのローカルコピー試験で実ダウンロードのSmartScreen受入を代用しない。
- **保護の実結果:** SmartScreenの表示・発行元・警告／拒否、SACのOn／評価／Off、組織policy、全child EXE/DLL等の起動・ロードの許可／拒否・未検証範囲を記録する。SACのOff／評価モードでの成功をOnでの成功としない。OS側の信頼・失効照会とアプリの依存downloadは区別し、network条件も残す。

SmartScreenは発行元とファイルhashの評判を評価するため、**新しい署名済みbinaryでも警告が出得る**。証明書の種類や今回の成功だけで、全端末・将来版の無警告1操作起動を保証しない。[M1] SACはインターネット取得物以外も対象にする。**外側EXEの署名が内側の全実行EXE/DLLを許可するとは扱わない**。inventoryの署名検証と実policy下の起動・ロード結果の両方が必要である。[M1][M2]

通常起動が警告の迂回や追加操作を要する場合、その条件での1操作受入をPASSにしない。保護による拒否は該当試験をBLOCKEDとして記録する。対処に保護無効化、policy／registryの緩和、MOTW除去、受信者への自己署名証明書導入を含めない。

## 5. 証跡と一般提供の判定

将来の実施者はOSL-021所有の`docs/dev/reviews/portable-trust-validation.md`と検証artifactに、case ID、日時、環境、操作、期待値と実結果、`PASS`／`FAIL`／`BLOCKED`／`NOT EVALUATED`、理由、証跡参照を記録する。本タスクではそれらや実行台帳を作成・更新しない。

一般提供判定には、次の証拠が同じ候補について揃っている必要がある。

- 既存の提供許可とOSL-017/Q-010の再配布条件・notice・SBOM/CVEの確認。
- 承認済み発行元、コード署名用途、対象OSの信頼chain、timestampの検証結果と、全内包実行binaryの検査・未解決事項の解消。
- **署名後の保存物・配布物・実取得物・再受入したEXEがすべて`Hfinal`で一致**し、inventory/SBOMも実bytesに対応すること。
- 実取得経路・MOTW・対象policyの証拠と、保護を維持した最終bytesのAC-007.1〜9の受入結果。

外部入力の未選定、必要な信頼／取得証跡の不足は**OSL-021のBLOCKEDのまま**とする。手順だけ、署名toolの成功だけ、未測定やskipだけではPASSにしない。実結果は既存計画のOSL-028等のレビューへ渡す。起動受入をAC-001〜006、V2-013/V2-014の業務E2E・品質や製品全体の完了に読み替えない。

## 6. 公式資料

2026-09-06に確認。M1/M2はADR-0010および計画W07/W09の一次資料。補足資料は署名検証の説明に限定し、記載されたサービス／配布方式を本製品へ追加採用しない。

- [M1] [SmartScreen reputation](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation): 新規binaryの評判、署名の限界、SACとの区別。
- [M2] [Smart App Control overview](https://learn.microsoft.com/windows/apps/develop/smart-app-control/overview): 評価／強制モード、信頼されないbinaryの扱い。
- [M3] [Code signing for Smart App Control](https://learn.microsoft.com/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control): 信頼される署名者、RSA/ECCの互換性。
- [M4] [Time-stamping Authenticode signatures](https://learn.microsoft.com/windows/win32/seccrypto/time-stamping-authenticode-signatures): timestamp、SHA-256とRFC 3161。
- [M5] [Understanding executable file signing](https://learn.microsoft.com/windows/win32/secbp/understanding-pe-signatures): PE署名の対象範囲とファイル全体hashの違い。
- [M6] [SignTool](https://learn.microsoft.com/windows/win32/seccrypto/signtool): Authenticodeの検証policy、コード署名用途、全署名・timestamp・信頼の検証。