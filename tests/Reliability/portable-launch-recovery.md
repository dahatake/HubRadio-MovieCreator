# Portable launch recovery acceptance procedure

- Task: OSL-016
- Scope: portable single-EXE launch, self-extraction/re-expansion, launch-time and post-managed-entry recovery
- Status: **procedure implemented / execution NOT RUN（手順実装済み・実行未実施）**
- Result: every case starts as `NOT EVALUATED`; this document contains no execution result, timing, capacity value, hash, or recovery claim.

正本: [NFR-009 / AC-007](../../docs/requirement-definition.md)、[ADR-0010](../../docs/dev/decisions/ADR-0010-one-action-portable-launch.md)、[OSL-004 acceptance procedure](../Acceptance/OneActionLaunch.md)。本手順はOSL-020が使う異常系の受入手順であり、既存案件での実行、開発ホストのprofile差替え、保護機能の回避を許可しない。

## 1. 目的と境界

OSL-016は、次を区別して実証する。

1. 配布EXEまたは実行依存の欠落・破損で、managed entry（アプリが管理する起動処理）へ到達する前に失敗すること。
2. self-extraction／cache再生成の後も、配布payloadとユーザーデータを混同せず、案件・設定を失わないこと。
3. managed entry後に作られたユーザーデータを保持したまま、正常終了・再起動できること。
4. 容量不足・アクセス拒否・process startup failureを成功扱いせず、OS/Runtimeの実際の結果を記録すること。

白いwindow、process存在、window handle、失敗画面の表示だけでは起動成功としない。managed entry前の障害に対して、存在しないnative recovery panel、retryボタン、診断コード、再取得UIを「表示された」と作ってはならない。OSまたはRuntimeが返した実結果だけを記録する。

対象はOSL-019の固定候補EXEと、OSL-004で定義した対象Windows 11 24H2以降 x64の標準ユーザーである。署名・MOTW・実配布経路はAC-007.9／OSL-021の範囲外であり、未実施なら別途`BLOCKED`または`NOT EVALUATED`とする。

## 2. 判定規則

| 判定 | 適用条件 |
|---|---|
| `PASS` | 指定した新規使い捨てcaseで実行し、期待する安全な拒否または復旧と、全必須証跡を確認した。通常再起動のPASSは案件・設定保持まで確認した場合だけとする。 |
| `FAIL` | 試験可能だったが、期待と異なる。既存案件・ユーザーデータの消失、配布EXE／実行依存の変更、system fallback、download、管理者昇格、障害の成功扱いはFAIL。 |
| `BLOCKED` | clean VM、標準token、固定候補、観測手段、評価利用許諾、または容量・ACLを安全に準備できない、またはDefender等の保護が正規の試験を拒否した。保護を無効化して続行しない。 |
| `NOT EVALUATED` | 未実施、対象外、合格値未合意、または必要証跡を取得していない。未測定値を0・推定値・過去値で埋めない。 |

caseごとに判定する。1 caseのPASSを他caseへ展開しない。必須caseに`FAIL`、`BLOCKED`、`NOT EVALUATED`が残る場合、OSL-016全体をPASSにしない。

## 3. 共通の隔離と不変性

### 3.1 各caseの新規状態

各caseは、アプリを起動していない新しい使い捨てVM snapshotまたは同等に証明できる新規guest profileから開始する。case間でprofile、展開cache、WebView user data、案件DB、設定、環境変数、registry、ACL変更を共有しない。

各case内では、非機微fixtureを使って一つの検証用projectを作成し、次をbaselineとして記録する。

- project ID、設定revision、設定profile版・digest
- 元payload／元動画fixtureの識別子とdigest（本文・媒体metadataは保存しない）
- `%LOCALAPPDATA%\HubRadio.MovieCreator` の用途別相対path、案件DB、設定、WebView user dataの存在状態
- 候補EXEのSHA-256・byte数、component manifest/SBOMのdigest
- 実展開rootのmanifest、相対path、byte数、hash
- OS、edition、version、build、x64、標準token、offline状態、policy状態

「既存案件を保持した」と主張するcaseでは、故障注入前に案件と設定をmanaged entryで一度作成し、snapshotを保存してから故障を注入する。初回起動失敗caseでは、未使用profileを使い、DB/profileが作られなかったことを既存profile不在の推測から断定しない。

### 3.2 禁止事項

- 実利用者・開発者の案件、DB、設定、WebView profile、model cacheを使用・削除・上書きしない。
- EXE隣接payloadを直接改変して通常候補の証拠にしない。改変はcase専用の複製だけに行い、元候補はread-onlyで保全する。
- `TEMP`、`LOCALAPPDATA`全体、ユーザーprofile全体を削除して復旧成功にしない。
- Defender、SmartScreen、Smart App Control、Application Control、UAC、MOTW、ACL、registry、network policyを無効化・回避・書き換えして試験しない。
- 依存をdownload、pip/install、system WebView2への黙ったfallback、管理者昇格、手動設定で補ってPASSにしない。
- managed entry前の失敗を、アプリ内の架空のrecovery UIや架空の診断情報で補わない。

既存の補助スクリプトを利用できる場合は、[既存startup smoke](../smoke/Test-WebViewStartup.ps1)と[既存Python runtime verifier](../../packaging/python/verify-runtime.ps1)をそのまま使用し、scriptの版・hash・引数・実行結果を証跡へ記録する。OSL-016のために新しいlauncher、extractor、recovery framework、試験専用の製品flagを作らない。Q-007のjob/media recovery scriptはportable launch recoveryの実行証拠へ流用しない。

## 4. Recovery matrix

| Case | 故障段階 | オペレーター手順（各caseで新規profile） | 期待結果・PASS条件 | FAIL条件 | 最小証跡 |
|---|---|---|---|---|---|
| R-01 Cache regeneration | self-extraction後、または再展開可能なcache | 正常候補を新規profileで起動しmanaged entry到達後、アプリ終了を確認する。実観測した自己展開rootまたはアプリ管理の再生成可能cacheだけを特定し、case専用snapshotでそのcacheだけを除去する。payload EXE、候補manifest、案件DB、設定、WebView user data、元fixtureは残す。同じ候補をofflineで1回起動し、UI Ready、実Runtime、検証用project、変更済み設定を読み戻す。model cacheを対象にする場合はpayloadのmodel本体と区別し、再loadを別証跡にする。 | cacheだけが再生成される。候補EXE・実行依存treeがmanifestと一致し、system fallback/downloadなしでmanaged entry、Web UI bridge、設定画面へ到達する。project ID、設定revision、profile版/digest、元payload digestが保持される。 | payload・EXE・実行依存・案件DB・設定が変更／消失する、初期化で新規案件に置換される、network取得・手動setup・昇格を要求する、再生成できない欠落を成功扱いする。 | 除去対象の相対pathと事前hash、再生成後manifest/hash、before/afterの案件・設定比較、offline、実Runtime/bridge結果、child process対応、起動回数、終了結果。 |
| R-02 Missing or corrupt dependency | managed entry前の検出、または依存利用時の失敗 | 新規caseで正常候補の全依存をmanifest/hash照合する。case専用複製で、WebView2Runtime、Web asset、worker、FFmpeg/ffprobe、採用CPU modelの必要fileを**1 case 1対象**ずつ欠落または改変する。欠落／破損対象と変更前後hashを記録し、他の依存は変更しない。候補をofflineで1回起動し、失敗段階をOS/Runtimeまたは既存managed diagnosticから観測する。 | 欠落・破損を成功扱いせず、該当段階で拒否／失敗する。system Runtime、download、管理者昇格で隠さない。managed entry前ならOS/Runtime実結果だけを記録し、アプリのrecovery UIを要求しない。managed entry後に依存を検証する場合は既存の実診断だけを確認する。元候補と元profileは不変。 | Ready／bridge／案件作成まで到達したのに依存欠落を検出しない、system fallbackまたはdownloadで成功する、改変候補を通常候補として扱う、架空の診断・UIを証拠にする。 | 対象依存、元／改変hash、manifestとの照合、実起動段階、OS/Runtimeの表示・終了code、実child path/hash、network状態、profile/DBのbefore/after、保全した元候補hash。 |
| R-03 Access denied | EXE実行、展開root書込、依存読取、user-data書込を個別に注入 | 新規caseを4つに分ける。標準tokenのまま、(a)候補EXEの実行拒否、(b)新規展開先の書込拒否、(c)依存の読取拒否、(d) `%LOCALAPPDATA%\HubRadio.MovieCreator` のuser-data書込拒否をそれぞれ再現する。ACL準備・復帰は管理側で行い、試験中のアプリは昇格させない。各caseで候補を1回だけ起動し、拒否を戻した別snapshotで正常候補を再起動する。 | 各拒否が実際のOS/Runtime結果として観測され、success／Ready／設定保存成功に偽装されない。拒否解除後の新規caseで、元候補とfixtureを使い正常起動し、既存baseline（作成済みcaseではproject・設定）を保持する。 | 管理者昇格、ACL回避、保護解除、別user-dataへの黙った書込、実行依存への書込、拒否時の成功表示、元案件の初期化・消失。 | 対象相対path、標準tokenと実効権限、拒否の種類、OS/Runtimeの実結果・終了code、失敗段階、書込先、元候補／tree／user-dataのhashと存在比較、拒否解除後の再起動結果。 |
| R-04 Disk full | 自己展開先またはuser-data保存先の容量枯渇 | 新規caseの専用volume／仮想disk／安全な容量制約を使い、(a)自己展開中、(b)案件／WebView user-data書込中を別caseで再現する。OSの容量制約を作る前後の空き容量を記録し、実行中に正常host全体を無差別killしない。case終了後は管理者がsnapshot破棄または専用領域を安全に清掃する。容量を戻した別snapshotで同じ正常候補を1回起動し、baselineを確認する。 | 空き容量枯渇が実結果として検出され、起動・保存・Readyを成功扱いしない。元payload、元fixture、既存案件・設定・承認済みユーザーデータが不変で、容量回復後に正常候補で再開できる。部分fileはmanagedな一時物として明示的に検出・整理され、保護対象を消去しない。 | 開発hostの容量や推定値だけでPASS、既存案件の削除で継続、容量不足をdownload／別path／昇格で隠す、残留partialを検査せず成功、実際のENOSPC等を捏造する。 | 制約対象、開始／最小空き容量、実filesystem、注入時刻、OS/Runtimeのerror・終了code、部分fileの相対path/hash、payload・fixture・user-dataのbefore/after、容量回復後の再起動結果。 |
| R-05 Process startup failure | host／child process開始前または開始直後 | 新規caseで、OSL-019候補のprocess startupを妨げる許可済み・可逆な条件を1つだけ準備する。実行禁止・必要DLL／Runtime読取不能など、OS/Runtimeが実際に返す失敗だけを対象とする。`Environment.FailFast`等の既存Q-007 process-containment probeは本caseの代替にしない。候補を1回起動し、親／childの実PID、開始可否、終了結果を観測する。条件を戻した別snapshotで正常候補を起動する。 | startup failureをReady、bridge、設定画面、案件作成の成功としない。managed entry前ならアプリ内recovery UIを期待せず、OS/Runtimeの結果と未到達段階を記録する。managed entry後にchildだけが失敗した場合は、既存の実diagnosticとprocess cleanupだけを確認する。回復後は新規caseで正常起動する。 | 架空のstartup error／UI／retryを記録、別processのPIDを対象にする、無関係なWebView processをkillして成功、process残留を未確認、失敗後に元profileを掃除して成功にする。 | startup条件、候補hash、実PIDの対応、parent/childの開始・終了時刻、stdout/stderrまたはOS表示の秘匿済み要約、到達段階、残留process有無、元profile／候補treeの比較、回復後の正常起動。 |
| R-06 Normal restart | managed entry後の正常終了・warm restart | 新規caseで正常起動し、検証用projectを作成、設定1項目を変更して保存する。windowを通常終了し、関連childの終了を観測する。同じ候補を再起動し、既存projectを読み込み、変更済み設定・profile版/digest・元fixture digestを確認する。再展開を伴う再起動はR-01と分ける。 | 追加操作なしで同じ既存UIへ到達し、案件・設定・元payloadを保持する。配布EXE・展開済み実行依存は変更されない。正常終了後のchild残留がない。DBの監査更新は許容するが、意味的データ保持を比較する。 | 新規案件で置換、設定初期化、元payload変更、実行依存への書込、child残留、再起動を初回起動成功として記録、またはR-01〜R-05の故障を混ぜる。 | 起動／終了回数、候補前後hash、展開tree前後比較、project ID、設定revisionと値の意味比較、profile版/digest、元fixture digest、child終了、Ready／bridge結果。 |

各caseで「正常候補が正常に起動した」は負例注入の安全性を示す補助結果であり、AC-007.1〜8全体、業務E2E、認識精度、字幕品質、一般提供を示さない。

## 5. Managed entry到達段階の記録

各試行は、次の最初の到達段階を1つだけ記録する。後段のUIが無いことから前段未到達を推測しない。

| 段階 | 記録内容 |
|---|---|
| `OS_PROCESS_START` | EXEをOSが開始できたか、実PID、終了結果。開始不能ならOS/Runtimeの表示・error・終了codeのみ。 |
| `SELF_EXTRACTION` | 実展開rootの作成・更新、候補manifestとの照合。初回生成と後続のpayload変更を分離。 |
| `DEPENDENCY_DISCOVERY` | WebView2、worker、FFmpeg、model等の検出結果、実path・hash・版。system fallbackの有無。 |
| `MANAGED_ENTRY` | アプリ所有のstartup coordinator／初期化処理へ到達した事実。存在する実診断だけを記録。 |
| `USER_DATA_OPEN` | per-user user-data、DB、設定、WebView profileの実効保存先と開閉結果。 |
| `WEB_UI_READY` | Web UI bridgeの既存Ready契約と、必要な設定／project操作の結果。window存在だけでは不可。 |
| `RECOVERY_RESTART` | 故障解除後の新規caseでの正常再起動と、baseline保持の比較結果。 |

`MANAGED_ENTRY`より前の段階に、製品のnative recovery panel、再取得案内、retry操作を想定してはならない。既存UIの診断が実際に存在する場合のみ、表示名と秘匿済みdiagnostic codeを記録する。

## 6. Evidence record

OSL-020の各行は、`run-id / case-id / attempt / evidence-kind`で一意にする。以下をcaseごとに埋める。未取得欄は空欄のままにせず、`NOT EVALUATED`または`BLOCKED`と理由を書く。

| Field | Value |
|---|---|
| Run / case / attempt |  |
| 判定 | `NOT EVALUATED`（初期値） |
| Environment classification | clean production Windows / supplement / reference |
| Snapshot ID / standard-user proof |  |
| Candidate ID / EXE SHA-256 / bytes |  |
| source commit / manifest / SBOM digest |  |
| OS edition, version, build, architecture |  |
| Offline / policy / MOTW / protection state |  |
| Baseline project/settings/profile identifiers |  |
| Fixture identifier/digest |  |
| Fault target and injection kind |  |
| Managed-entry stage reached |  |
| Expected result / actual result |  |
| Payload/tree before-after comparison |  |
| User-data/project/settings before-after comparison |  |
| Process parent/child observation |  |
| OS/Runtime error or existing diagnostic summary |  |
| Evidence file references, bytes, SHA-256 |  |
| Failure / blocked / not-evaluated reason |  |
| Re-run precondition |  |

証跡には個人の絶対path、SID、アカウント名、GPS、端末名、撮影時刻、字幕／音声本文、秘密、token、全環境変数、全command lineを保存・共有しない。pathは用途別rootと相対pathへ正規化する。実際のhash、error、容量、時間を未実施のまま補完しない。

## 7. Completion gate

OSL-016の文書成果物は本書の手順定義で完了し、**この文書作成時点のprocedure statusはNOT RUN**である。OSL-020で実行するまで、R-01〜R-06をPASS、FAIL、BLOCKEDへ変更してはならない。実行後も、次を満たしたcaseだけをPASSにする。

- 新規使い捨てproject/profileと標準tokenを使用した。
- 元payload、元fixture、既存案件、ユーザーデータ、設定を保全した。
- 依存不足、容量不足、アクセス拒否、startup failureを成功扱いしなかった。
- system fallback、download、管理者昇格、保護回避、架空UI／診断を使わなかった。
- managed entry前後と、正常再起動・cache再生成を別々に記録した。
- case固有の期待、実結果、証跡、再開条件を記録した。

この手順の存在、既存Q-007 recoveryのPASS、unit test、開発ホスト起動、またはレビューだけで、OSL-016の実行受入・AC-007.6/7の成立・一般提供を主張しない。
