# V2-008 標準Composition計画 敵対的再レビュー

- 対象: `9f80cd52f1c699b120d101da8ca8f9dc4ecbeabb..e60c4c77435e13f44fdcd72a142192e26d69adec`と、その再レビュー修正
- 実施日: 2026-09-05
- 目的: 元動画1本から、社内限定長尺、根拠付き短尺候補、字幕variant、実frameサムネイル候補、不成立理由、来歴を、捏造せず決定的に計画・保存・再生できること
- 判定: **PASS — Critical 0件（検出2件は修正・再検証済み）**

## 指摘一覧

| No. | 軸 | 重大度 | 指摘箇所 | 問題の説明 | 修正案 |
|-----|-----|--------|---------|-----------|--------|
| 1 | 目的適合性 | Critical | `StandardCompositionPlanner.Create` | 根拠付きProposed候補があっても人の明示採用がなければ短尺0本となり、FR-007のsource-only自動候補生成を満たさない。 | **修正済み:** Proposedのまま候補化し、`GroundedCandidate`と`ExplicitAdoption`を型で区別。人の採用statusは変更しない。 |
| 2 | 目的適合性 | Critical | `ValidateThumbnailEvidence` / `TranscriptImporter` | 実文字起こしspanは`audio:*`だがthumbnailは`video:*`との一致を要求し、実frame正経路を必ず拒否した。 | **修正済み:** 同一asset/digestだけを結合条件とし、audio transcriptとvideo frameを別stream証拠として保持。 |
| 3 | 内容の妥当性 | Major | `ValidateThumbnailEvidence` | thumbnail frameを発話span内に限定し、冒頭・間・末尾の正当な実frameを拒否した。 | **修正済み:** PTS/frame-map/sprite-indexで検証済みのframeは発話有無と独立に許可。 |
| 4 | 内容の妥当性 | Major | `StandardThumbnailPlan` | PTS index/domainが許容する負PTSを拒否し、正常な媒体を扱えなかった。 | **修正済み:** frame sequenceだけを非負必須とし、source PTSの符号を保持。負PTS統合test追加。 |
| 5 | 品質・運用性 | Major | `FromValidatedPreview` | 3件選択後にloopを終了し、4件目以降のsprite cellをsemantic検証しなかった。 | **修正済み:** 全cellをframe map/PTSへ照合してから最大3件へ縮約。suffix改変負例を追加。 |
| 6 | 目的適合性 | Major | `FromValidatedPreview` | M-010が時間軸全体から最大100枚を作るのに先頭3枚だけを採用し、候補が冒頭へ偏った。 | **修正済み:** 検証済み一意frameの先頭・中間・末尾を決定的に選択。 |
| 7 | 根拠性・不確実性管理 | Major | omission生成 | 根拠付き候補が存在しても`NoGroundedCandidate`と記録し、実際の不成立理由と矛盾した。 | **修正済み:** 根拠付きProposed候補を候補化。真に0件のときだけ`NoGroundedCandidate`を使用。 |
| 8 | 整合性 | Major | evidence/plan digest version | 上記semantic変更をv1のまま適用すると、同じ版名が異なる意味を持つ。 | **修正済み:** evidence schema、selection rule、plan digest domainをv2へ更新し、旧v1をfail-closed。 |
| 9 | 整合性 | Major | `implementation-plan.md` 冒頭 | `V2-000`〜`V2-003`のみ完了とし、現在のmain履歴と矛盾した。 | **修正済み:** `V2-000`〜`V2-009`完了、次は`V2-010`と更新。 |
| 10 | 整合性 | Major | `implementation-plan.md` §0.4 | `V2-004`〜`V2-009`各行が未完了表記のままだった。 | **修正済み:** 各taskを完了表記にし、個別reviewへリンク。 |
| 11 | 整合性 | Major | `implementation-plan.md` V2-012 | 表ではV2-006依存が欠落し、同文書DAGと矛盾した。 | **修正済み:** V2-012依存へV2-006を追加。 |
| 12 | 目的適合性 | Major | `README.md` | quick start、settings、auto-decision、標準成果物を未実装と一般利用者へ誤表示した。 | **修正済み:** 実装済みplan/UIと未完了のevaluation/render/deliveryを分離。 |
| 13 | 整合性 | Major | `requirement-definition.md` 状態 | 要求正本の状態がV2-001完了で停止していた。 | **修正済み:** V2-009までの完了とV2-010以降の未完了を明記。 |
| 14 | 整合性 | Major | `docs/dev/README.md` 状態 | 開発者入口がquick start等を未実装と案内した。 | **修正済み:** V2-009までの現状と残taskを同期。 |
| 15 | 品質・運用性 | Minor | `docs/dev/README.md` 文書構成 | review一覧がV2-001で止まり、V2-002〜009へ到達しにくかった。 | **修正済み:** V2-005/V2-008リンクと後続review群を追記。 |
| 16 | 整合性 | Major | `architecture/overview.md` 状態 | quick start、settings、標準Compositionを未実装と記載した。 | **修正済み:** 実装済み範囲と未実装のevaluation/deliveryを同期。 |
| 17 | 整合性 | Major | `architecture/overview.md` bridge | allowlistを`bridge.ping`だけとしたが、実装はproject/settings/jobs等のclosed methodsを持つ。 | **修正済み:** 実在するclosed method群として説明。 |
| 18 | 整合性 | Major | architecture component inventory | Render IR、ArtifactStore、Python workerをplanned扱いし、実装と矛盾した。 | **修正済み:** overviewとSVGのcomponent状態を更新。 |
| 19 | 根拠性・不確実性管理 | Major | `quality.md` | 品質入口がV2-001以降を未実装とし、利用すべきreview証拠を示さなかった。 | **修正済み:** V2-009までの範囲とV2-008 reviewリンクを追加。 |
| 20 | 品質・運用性 | Major | `security-privacy.md` | quick start/settings/decisionを未実装・未試験と記載し、個別敵対試験の存在を反映していなかった。 | **修正済み:** task別試験済み範囲と未完了の統合security gateを分離。 |
| 21 | 品質・運用性 | Minor | `troubleshooting.md` 冒頭 | 編集GUI全体を未実装とし、実装済みquick start/settings UIを無視した。 | **修正済み:** 一部UI実装済み、end-to-end未完了へ修正。 |
| 22 | 根拠性・不確実性管理 | Major | accessibility test report | V2-005/V2-006を未実装と書き、実装済みだが当該旧runでは未評価という事実と混同した。 | **修正済み:** 「後から実装・本run未対象」へ訂正し、PASSへ転用しない。 |
| 23 | 整合性 | Major | `system-overview.svg` | UI、worker、Render IR、ArtifactStore、footerが旧状態を表示した。 | **修正済み:** 現行component状態へ同期し、SVG parseを確認。 |
| 24 | 根拠性・不確実性管理 | Minor | 初回V2-008 review header | task commitだけをlocal mainとし、closure commit `e60c4c7`を示さなかった。 | **修正済み:** closure commitと再レビューへのsupersede注記を追加。 |
| 25 | 根拠性・不確実性管理 | Minor | 初回review §1 aggregate hash | per-file manifestがなく、集約hashを文書だけから再構築できない。 | **修正済み:** 現在証拠へ流用せず履歴receipt限定と注記。生manifestの後付け捏造はしない。 |
| 26 | 根拠性・不確実性管理 | Major | 初回review §6.2 | payload生成fixture/helper/commandが追跡されず、4 hashを再現できない。 | **修正済み:** v1履歴receiptへ降格。v2は追跡済みschema testとsemantic replayを正本化。 |
| 27 | 根拠性・不確実性管理 | Major | 初回review §6.3 | full runの時刻、run ID、log artifactがなく、現在treeの証拠として固定できない。 | **修正済み:** 初回表を履歴限定とし、本再レビューでcanonical commandを再実行して件数を記録。 |
| 28 | 根拠性・不確実性管理 | Minor | 初回review R19 | 当時失敗した2 test名がなく、現在から特定不能だった。 | **修正済み:** 不明を明記し、推測で補完しない。 |
| 29 | 根拠性・不確実性管理 | Minor | 初回review R20 | serializer一本化の具体的symbolがなかった。 | **修正済み:** Core serializer、draft store、project serviceを明記。 |
| 30 | 品質・運用性 | Minor | Core `AssemblyInfo.cs` | 単一Infrastructure testのためCore全internalをfriend公開していた。 | **修正済み:** friendを削除し、公開stored payloadの意味検証へ変更。 |
| 31 | 品質・運用性 | Minor | planner/codec/schema | 2,000行超の実装2件と2,400行超schemaに責務が集中している。 | **未修正:** 現時点の分割は大規模・高リスクで、動作欠陥の修正に不要。次の実変更で自然な境界が生じるまでYAGNIを優先。 |
| 32 | 品質・運用性 | Minor | `StandardCompositionPlanningFailureReason.SettingsMismatch` | 多数の設定不一致を1 reasonへ畳み、開発時診断が弱い。 | **未修正:** 公開例外へ設定keyを出すと情報面とAPIを広げる。現要件はprivate-safe分類で満たすため、必要性を実測するまで追加しない。 |
| 33 | 品質・運用性 | Major | `VersionService.LoadVersion` | acyclic lineageの深さ・累積evidence量に上限がなく、長い履歴で同期replay時間が無制限に増える。 | **未修正:** cycle検出、反復walk、各4 MiB上限は存在する。根拠のない任意depthを追加すると正当な履歴を拒否するため、V2-013/014の実測で閾値または取消契約を決める。 |
| 34 | 品質・運用性 | Minor | `DeserializeCanonical` | closed-shape検査で最大4 MiBを再serializeし、lineageごとに割当が増える。 | **未修正:** 再serializeはunknown/default/shape差を拒否する安全境界。測定したbottleneckなしに削除・複雑化しない。 |
| 35 | 目的適合性 | Major | `StandardShortFormPlan` | FR-007が求める各短尺候補の選定理由・不足文脈をplan公開面が保持せず、後続UIが表示できなかった。 | **修正済み:** 既存Suggestionのreason、missing context、warningをstrict UTF-8・bounded値としてplan/digestへ保持し、semantic replayをtest。 |

### サマリー

- Critical: 0件（未解決。検出2件は全修正済み）
- Major: 1件（未解決。検出23件中22件を修正、1件は理由付き見送り）
- Minor: 3件（未解決。検出10件中7件を修正、3件は理由付き見送り）
- 合格判定: **PASS（未解決Critical = 0）**

検出時の内訳はCritical 2件、Major 23件、Minor 10件、合計35件である。

## 修正内容

- Proposedな根拠付きhighlightを、Suggestionを採用済みに改変せず自動短尺候補へ選定するよう変更した。
- `GroundedCandidate`と`ExplicitAdoption`をplan digestへ固定した。
- 各短尺候補へ既存Suggestionの選定理由、不足文脈、warningを保持した。
- audio transcript spanとvideo thumbnail frameを同一streamと誤認する検査を削除した。
- 発話外frameと負PTSを保持し、sprite全cellを検証して先頭・中間・末尾を選ぶよう変更した。
- evidence schema、selection rule、plan digest domainをv2へ更新した。
- Coreの不要なtest friend assemblyを削除した。
- 利用者・開発者向けの現行状態文書、DAG依存、architecture図をV2-009完了状態へ同期した。
- 初回reviewの再現不能receiptを履歴限定とし、現在の根拠へ流用しない注記を追加した。

## 検証

### Focused

- `HubRadio.App.Tests` StandardComposition: **82成功 / 0失敗**
- `HubRadio.Core.Tests` CompositionSchema: **12成功 / 0失敗**
- `HubRadio.Infrastructure.Tests` ProjectCreationDraftStore: **12成功 / 0失敗**
- SVG XML parse: PASS
- standard composition evidence schema JSON parse: PASS
- `git diff --check`: PASS

StandardCompositionの82件はMTPの実行case数である。3ファイルに76 `[TestMethod]`があり、そのうち3メソッドの本体を計9 `[DataRow]`へ置換して実行するため、$76 - 3 + 9 = 82$となる。

### Canonical Release全回帰

2026-09-05に`test.ps1 -Configuration Release`をPowerShell Core 7.6.5で再実行した。

| Runner | 成功 | 失敗 | environment-gated |
|---|---:|---:|---:|
| Architecture .NET | 46 | 0 | 0 |
| Core .NET | 665 | 0 | 1 |
| Infrastructure .NET | 726 | 0 | 21 |
| App .NET | 1,124 | 0 | 3 |
| Web Vitest | 469 | 0 | 0 |
| Accessibility Node | 4 | 0 | 0 |
| Web asset/dependency | 6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

.NETは2,586件中2,561成功、25件environment-gated、failure 0。Web/Nodeは479成功、Pythonは104成功、failure 0だった。restore、npm audit（脆弱性0）、hash-locked Python restore、`pip check`、TypeScript、production Vite build、format/analyzer、warnings-as-errors buildも成功した。637.41 kBのVite chunk warningは残り、性能PASSへ読み替えない。

## 不採用と残余範囲

- 「source PTSが5秒なのでhookは3秒違反」は不採用。短尺はそのsource spanから開始するため、短尺内配置は0秒である。
- JSON closed-shape再serializeをdigest比較だけへ置換する案は不採用。digest一致はunknown/default propertyを含むclosed DTO shapeを保証しない。
- SQLite transaction内のparent/project照合に別transactionが割り込むという指摘は、同一transactionのsnapshot/lockingを無視した仮説で、再現手順が成立しなかった。
- 実render、自動代理評価、delivery、人による知覚、platform UI、法的適合、signed install、clean machineは本再レビューのPASS範囲外であり、V2-010〜V2-014に残る。
