# ADR-0009: source-only quick start、版付き既定値、自動代理評価

- 状態: Accepted
- 決定日: 2026-09-02
- 決定者: product ownerである利用者
- 要求正本: [`docs/requirement-definition.md`](../../requirement-definition.md)
- 根拠台帳: [`docs/dev/research/default-settings-research.md`](../research/default-settings-research.md)
- 実装状態: production実装は利用者指示により一時停止中

## 背景

版0.1の要求と203 task baselineは、10項目の案件入力、複数業務役割、段階的な人手承認、別WindowsアカウントのACL受入、社内法務部門を含むrelease gateを前提としていた。この構成では、利用者が求める「編集前動画を指定するだけ」の操作にならない。

2026-09-02に利用者は次を決定した。

1. quick startでは元動画だけを指定する。
2. 全設定を調査済み初期値とともに設定画面へ置く。
3. 初期利用は単一Windows利用者、社内限定とする。
4. 自動代理評価を正式な検証方法として許可する。
5. cross-account requirement、複数役割の必須承認、社内法務部門の承認gateを削除する。
6. リポジトリ直下の`Music/`を社内限定で自由利用できるローカル音源ライブラリとする。
7. production実装を一時停止し、先に要求、根拠、ADR、計画を再baselineする。

旧文書と実装証拠は、当時の事実を示す履歴として保全する。過去の人手`PENDING`を遡及的に`PASS`へ変更せず、新しい要求への適用可否を別に判断する。

## 決定

### 1. 利用形態

初期リリースはWindows 11 x64の単一PC、現在のWindows標準ユーザー1人、local-first、社内限定とする。

- 初回利用者を暗黙の複数業務roleへ割り当てない。
- 人が行った操作はactor SIDで記録する。
- AI、worker、FFmpeg、自動代理評価器はsystem actorとして区別する。
- 複数利用者、別account境界、組織role、外部公開workflowは別要求とmigrationなしに追加しない。

`S-003`をactive受入taskから削除することは、既存のworkspace ACL作成・path境界・同一利用者のaccess checkをproduction codeから削除する指示ではない。既存防御は維持し、別accountからの隔離を初期scopeで保証・受入済みとは表示しない。将来複数利用者scopeを追加する場合は、その時点で脅威分析とcross-account受入を再設定する。

### 2. source-only quick start

必須入力は元動画1本だけとする。ファイル選択後に、システムは安全な案件名、社内限定区分、言語fallback、匿名話者、ニュートラルtemplate、成果物set、設定profileを補完し、1画面で要約する。利用者の必須操作は`編集を開始`または取消だけとする。

氏名、肩書、期限、URL、CTA、外部公開先等を推測しない。値がなければ匿名、なし、ロゴなし等の安全な状態を使う。

### 3. 版付き既定値と設定画面

既定値集合を`DefaultSettingsProfile`としてsemantic version、canonical content、SHA-256 digest、根拠release noteとともに管理する。案件開始時のsnapshotをCompositionへ固定し、将来の既定値変更を既存版へ遡及適用しない。

専用設定画面は次の5群を持つ。

1. 案件と出力
2. 編集
3. 字幕とグラフィック
4. 音声と素材
5. プライバシーと保存

各設定は現在値、初期値、用途、根拠区分、個別resetを表示し、全体resetも提供する。高度な設定は1段の展開UIへ置き、quick start画面へ全項目を露出しない。

数値と規則は[既定設定調査](../research/default-settings-research.md)を根拠台帳とする。外部資料が直接規定しない値は`製品判断`と明記する。広告仕様、organic upload仕様、標準、査読研究、実務助言を相互に読み替えない。

### 4. 自動decisionと可逆性

明示カット、構造的に隣接する完全一致反復、版付き冒頭・末尾無音規則等、意味を変えないclosedな規則はquick startで自動適用できる。結果は自動decision、理由、規則版、`SourceSpan`、親版とともに子`CompositionVersion`へ記録する。

意味、感情、脱線、咳、雑音、顔表情等の曖昧な判断を、自動削除または確定事実にしない。候補またはwarningとして残す。すべての自動decisionは元動画を変更せず、無効化または修正できる。

### 5. 自動代理評価

社内限定quick startの既定評価は自動代理評価とする。

- 判定は`PASS`、`WARNING`、`FAIL`、`NOT EVALUATED`を区別する。
- `Automated proxy: PASS`を`HUMAN EVALUATION: PASS`、人手承認、知覚品質保証、法的適合と表記しない。
- 規則版、実行環境、入力・出力digest、判定詳細、未評価範囲を保存する。
- codec、全編decode、duration、A/V同期、字幕の有無・読速・行長・同期・safe zone、loudness、True Peak、来歴、UIのkeyboard/focus/contrast等、機械判定可能なclosed contractを対象にする。
- 可読性、理解、自然さ、感情、声質、法的判断等は、人が実施していなければ`NOT EVALUATED`または明示的な制限として残す。

技術的破損、来歴欠落、許可範囲外素材は出力をblockする。機械判定不能な知覚項目はwarningとし、社内限定成果物の生成そのものを複数役割承認待ちにしない。

### 6. 任意確認と状態

社内限定quick startの最小状態は次とする。

`Editing → AutomatedEvaluation → Ready | ReadyWithWarnings | Failed → Exported`

- `Failed`は技術破損、来歴欠落、利用範囲違反等のclosedなblock条件に使う。
- 利用者は任意に`SelfReviewed`または`ChangesRequested`イベントを追加できる。
- 内容変更は子`CompositionVersion`を作り、自動代理評価を再実行する。
- 同じ内容digestの技術的再renderは、設定・tool条件を記録して同じCompositionから実行できる。
- 旧`Review`、`Approval`、role、状態は既存データ読取と履歴監査のため保持できるが、quick startの必須遷移にしない。

### 7. 社内限定BGM

`Music/`を利用者が社内利用を許可したread-onlyローカル音源ライブラリとして扱う。

- ファイル本体を移動、改名、変更しない。
- 開始時snapshotの内容hash順から決定的に選択し、使用track digestを記録する。
- ファイル名から曲調、権利、用途を推測しない。
- quick startはオープニング、話題境界、エンディングだけを自動対象とし、本編会話中は既定OFFとする。
- `Music/`由来音源を外部用profileへ自動利用しない。

この決定は利用者による社内利用許可であり、第三者の再配布権、外部公開権、個別曲の一般的な権利状態を証明するものではない。

### 8. 外部公開とcloud

初期releaseの出力はローカル・社内限定までとし、直接投稿を行わない。YouTube、TikTok、LinkedIn等の資料はencoding、layout、safe zoneの参考にできるが、互換profileを公開承認済みと表示しない。

既存のAzure限定評価経路は既定OFFのまま維持する。cloudを有効化しても、選択・mask・確認済み文字起こしspanだけという最小payload境界を維持する。このADRはAzure resourceの存在をcloud必要性、production利用、外部公開許可へ拡張しない。

### 9. 社内法務gateの削除範囲

社内法務部門という特定の担当者による承認を、source-only社内限定vertical sliceの開始・完了gateにしない。

次は削除しない。

- 第三者license、notice、利用規約、再配布条件の確認
- SBOM、Model BOM、CVE、codec/patent条件の記録
- 未確認事項をrelease済みまたは適合済みと表示しない制約
- 外部公開scopeを将来追加する場合の別途の権利・privacy・security判断

「法務承認待ちを削除する」ことを「法的義務が存在しない」と読み替えてはならない。

## 置換範囲

| 既存ADR | このADRが置き換える範囲 | 維持する範囲 |
|---|---|---|
| [ADR-0002](ADR-0002-product-scope.md) | Stage 1の社内成果物に複数role/reviewを必須とする部分 | Windows単一PC、SQLite、local-first、非破壊、初期対象外 |
| [ADR-0003](ADR-0003-local-identity-and-review.md) | 社内限定の既定review経路、承認自動省略禁止、承認roleの初期割当、複数roleをquick start gateにする部分 | actor SID、監査event、digest-boundな任意review、変更時の子版、外部scopeへ拡大しない原則 |
| [ADR-0004](ADR-0004-data-classification.md) | 社内限定成果物のfinal render前に人手review/approvalを必須にする部分 | 社内限定既定、公開範囲の自動拡大禁止、cloud最小payload、AIだけで外部公開可にしない原則 |
| [ADR-0005](ADR-0005-storage-retention.md) | 別Windows accountからの拒否を初期受入にする部分 | managed workspace、不変original、staging/atomic move、保持期間を捏造しない、backup/restore |
| [ADR-0007](ADR-0007-stage-0-technology-selection.md) | 保留task再開に「別の人手承認者」を要求する部分。単一product ownerの明示判断で再計画できる | 採用技術、condition-false/heldの事実、未測定値、manual fallback、release未承認境界 |
| [ADR-0001](ADR-0001-source-of-truth.md) | 置換なし | 要求・review・ADR・research・計画の優先順位と変更規則を全文維持 |
| [ADR-0006](ADR-0006-distribution-and-azure.md) | 置換なし | local-first配布、API key禁止、署名・install・release未承認境界を全文維持 |
| [ADR-0008](ADR-0008-bounded-azure-evaluation.md) | 置換なし | Azure限定評価resource、最小payload、runtime gate、production未承認境界を全文維持 |

旧ADR本文は履歴を保つため書き換えず、各冒頭へ本ADRによる部分置換注記だけを加える。

## task baselineへの影響

旧203 taskの完了数は、旧要求に対する履歴snapshotとしてのみ保持する。新要求後の現在完了数へ転用しない。

各taskを次へ分類する。

- `維持`: 新要求でも成果物と証拠をそのまま利用する
- `変更`: 実装または受入契約を変えて再検証する
- `削除`: 新scopeでは実行しない。過去実装・証拠は消さない
- `新規`: source-only、既定値、設定画面、自動代理評価等のために追加する
- `将来`: 外部公開、複数利用者等の別要求が成立するまで開始しない

再分類が完了するまで、旧`124/203`、`139/203`、`160/203`や残件数を現在値として表記しない。

## 結果

### 利点

- 元動画だけで処理を開始でき、利用者の必須入力と待ち時間を減らせる。
- 設定値と根拠を可視化し、既定値変更後も再現性を維持できる。
- 社内限定範囲で自動処理と自動評価を完結しつつ、元動画と履歴を保全できる。
- 実施していない人手評価や外部承認を捏造せず、機械評価の範囲を明確にできる。

### 費用・リスク

- 自動代理評価だけでは知覚品質、理解、自然さ、法的適合を保証できない。
- 単一利用者scopeは別accountからの防御を受入済みとはしない。
- 既存のrole/review実装と新stateをmigrationまたは互換層で扱う必要がある。
- 配信先仕様、safe zone、既定値の継続的な再確認が必要になる。

### 緩和

- 自動評価の未評価範囲を明示し、任意previewと修正経路を残す。
- 外部公開と直接投稿を初期対象外に保つ。
- 設定profileと引用台帳を版管理し、広告資料等のscopeを明記する。
- 旧証拠を改ざんせず、新要求の受入結果を別recordとして作る。

## 却下した代替案

1. **10項目wizardを維持する:** 利用者が求めるsource-only操作と矛盾する。
2. **既定値をUIへ表示せず暗黙適用する:** 利用者が変更・reset・根拠確認できず、再現性も弱い。
3. **人手17項目を自動的にPASSへ変える:** 実施していない評価の捏造になる。
4. **全自動decisionを無条件採用する:** 意味変更、誤削除、偽の人物・権利判断につながる。
5. **旧203 taskをそのまま続行する:** 変更済み要求に対する進捗を表さない。
6. **`Music/`を外部公開にも利用する:** 利用者が与えた許可範囲を超える。

## 検証条件

このADRの文書受入には次を必要とする。

- 要求、研究台帳、ADR、architecture、計画の相互整合性
- 出典URL、取得日、資料scope、数値の直接性の監査
- 旧進捗値を現在値として使っていないこと
- `Music/`を変更またはstageしていないこと
- production codeを変更していないこと
- 独立した敵対的レビュー
