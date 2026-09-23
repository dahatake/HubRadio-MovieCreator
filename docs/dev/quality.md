# 品質管理

この文書は、Stage 0、既存製品基盤、要求版0.2の後続実装で守る品質管理の入口です。測定していない品質、未実装のテスト、実施していない人手評価を完了済みとは扱いません。

## 現在の品質境界

**今回対象の52単位はすべて完了**。UI/UXの54単位から利用者指定の`UX-041/043`を除いた範囲です。最終静的検査、runtime出力コピー照合、UX-048最終独立レビュー、記録確定まで完了し、未解決の重大指摘はありません。対象52単位の完了条件成立を記録した後、package `0.1.1.0`と`CHANGELOG.md`のUnreleased追記を反映済みです。除外項目をPASSへ変更せず、一般提供の承認とも区別します。

実warning、job、版、成果物、評価、保存・再open、明示再生成・retryの製品経路は接続済みです。9月8日のA17／E契約未受領／noJobという状態は履歴であり、現在状態には適用しません。限定的なlegacy経路の理由付き`Unavailable`は許容された能力境界として残し、未実装の承認や永続化を捏造しません。2026-09-02・07・08の結果と未実施の人手評価は以下と[アクセシビリティ評価記録](accessibility/test-report.md)に保全します。

既存実装と検証証拠には次の仕組みがあります。

- PowerShell 7 parser と、対象に応じた Python compile／import 検査
- benchmark envelope の JSON Schema 検証
- fixture の実行前後 size、更新日時、SHA-256 検査
- FFmpeg／ffprobe、model、主要 runtime artifact の version／hash 記録
- timeout、取消時の子孫 process 終了、process dispose
- staging から最終成果物への原子的昇格
- output／manifest pair の失敗時 rollback
- ffprobe profile 検査と `-xerror` 全編 decode
- 想定外の0件や失敗を解釈するための positive control
- raw run とレビュー済み canonical summary の分離
- 未測定値、単回観測、未承認閾値の明示
- .NET/Web/Pythonのfocused test、component回帰、全回帰
- keyboard interaction、focus CSS、contrast token、非色依存semanticの自動contract
- media、bridge、worker、process、path、render境界の合成敵対試験

詳細は[Stage 0ベンチマーク](benchmarks/README.md)、[実装進行成果物の敵対的レビュー](reviews/implementation-adversarial-review.md)、[アクセシビリティ評価記録](accessibility/test-report.md)を参照してください。過去のPASSは、そのrunが対象にしたsurfaceだけの証拠である。

## 2026-09-23 `P2-11`/`P2-12` 生成段階の不一致解消と実素材受入の再実行

**結論から述べる。`V2-014` は合格していない。** 2026-09-22に不合格だった生成前検査は通過するようになり、実素材から標準成果物が実際に生成されたが、自動代理評価の `loudness` と `truePeak` が `FAIL` となり納品へ到達していない。

### 修正（production）

`LongFormProvenanceSnapshot` のコンストラクタで `SourceSpans` を保存時と同じ正規順 `(AssetId, StreamId, Range.Start, Range.End, Id)` に整列するようにした。`src/` の差分は **`src/HubRadio.App/Compositions/LongFormComposer.cs` の1ファイルのみ**（16行追加・1行削除）である。比較を行う `StandardArtifactGenerator.ValidateCheckpoint` と `StandardRenderGraphFactory.Digest<T>` は**1行も変更していない**。すなわち**比較条件の緩和による症状隠しではない**。

- **回帰試験:** `StandardCompositionOrchestratorTests.RestoredPlanMatchesTheCheckpointedPlanWhenProvenanceArrivesUnordered` を追加した。修正を一時的に戻して再ビルドし、**plan digest不一致で失敗することを実測**（`expected: e92107d5…` / `actual: 7d24af9f…`）。修正を戻すと成功する。
- **後方互換:** 保存される並びは従来から codec 側の正規順であり、今回の変更でも**変わらない**。`PlanDigest` は long-form 側が `span.Id` で整列してから算出するため順序非依存で、既存の保存済みdigestは引き続き一致する。

### 実素材受入の再実行（実測）

実行root `artifacts/validation/approved-media-3d95736adfa048f5a45a6de1fe4209b5` を保全している。

| 項目 | 結果 |
|---|---|
| `ValidateCheckpoint` の7条件 | **すべて一致**（2026-09-22は `cond2.plan` のみ不一致） |
| 長尺16:9 | **1本生成**。承認済みffprobeで `1920x1080` / `h264` / `24000/1001` / `60.060000` を実測（素材は1280×720。**アップスケールを実測で確認**） |
| サムネイル | **6枚生成**（16:9×3、9:16×3） |
| 付帯成果物 | `transcript.json` / `used-source-ranges.json` の**2種を生成・昇格** |
| 短尺 | **0本**。不成立理由 `NoGroundedCandidate` を記録。空・重複動画は作られていない |
| 字幕 | **0件**。不成立理由 `NoDisplayCues`。読速・cue間隔・最小長の未解決warningを実測 |
| 自動代理評価 | `LandscapeClean`: pass 6 / **fail 2** / notEvaluated 19。thumbnail 6件: 各 pass 3 / notEvaluated 24 |
| 停止 | `source_only.output_evaluation_failed`。成果物7件すべて `delivered=false` |
| 所要 | scenario 221.5秒（予算9000秒）、実行段 150.2秒（予算5400秒）、準備 70.2秒（予算1800秒）。**いずれも予算内**。ただし納品まで含めた上限は未測定 |

**`loudness` / `truePeak` の `FAIL`（新たに判明した未解決事項）。** 入力素材は `-16.71` LUFS / `-1.88` dBTP で**要求の許容範囲内**だったが、単一passの `loudnorm`（`dynamic`）適用後の出力は `-14.27` LUFS / `-0.97` dBTP となり**範囲外**になった。**判定基準を緩める対処は行わない。** 修正方針は未決定であり、`FfmpegCompiler` を含む複数のproductionファイルへ波及するため利用者判断事項として記録した。詳細は[受入記録 §3.6](../../benchmarks/results/initial-pilot.md)。

### 回帰（本機実測、2026-09-23）

**下表は、他の検証を同時実行していない状態で測定した確定値である。**

| runner | 合計 | 成功 | skip | 失敗 |
|---|---:|---:|---:|---:|
| Architecture | 209 | 209 | 0 | 0 |
| Core | 732 | 731 | 1 | 0 |
| App | **2429** | **2420** | 9 | **0** |
| Infrastructure | 1362 | 1327 | 34 | **1** |
| Web（vitest 85 files） | 2075 | 2075 | 0 | 0 |
| Web accessibility / assets | 6 / 6 | 6 / 6 | 0 | 0 |
| Python worker | 170 | 169 | 1 | 0 |

- App の合計が `2428` から `2429` へ増えたのは、本節の回帰試験を1件追加したためである。
- Architecture は package版を `0.1.3.0` へ更新した**後**の実行である。
- `dotnet format --verify-no-changes` は終了コード `0`。ビルドは警告0・エラー0。

**Infrastructure の失敗1件について。** 失敗したのは `MediaImportSecurityTests.NonterminatingTool_IsKilledAtDeadlineWithoutPathDisclosure`（5.02秒で時間切れ）である。**これは本変更に起因しない。** 根拠は次のとおり。

1. 当該テストを**単独で実行すると成功する**（1/1成功）。
2. `P2-30` の変更を一時的に戻して再ビルドし、同一条件で全件実行したところ、**まったく同じテストが同じ件数構成（1362/1327/34/1）で失敗した**。すなわち**変更前後で同一**であり、`P2-30` の変更が持ち込んだ失敗ではない。
3. 当該テストも締切（deadline）に依存する時間敏感な試験であり、本日観測した他の時間切れ（Web、App）と同種の事象である。

**この1件を「成功」や「既知のため問題なし」と読み替えない。** 原因の特定は本タスクの範囲外であり、**未解決の環境依存failureとして記録する**。

### `P2-30` — Infrastructure の WER依存failure

**採った対処: 選択肢(b)（環境を検出して `Inconclusive`）。** ただし**検査内容は1つも削っていない**。

- 補助プロセスは `Environment.FailFast` を呼ぶ**直前**に自身のPIDをmarker fileへ書く（`ProcessRunnerTests.cs`）。テストはそのPIDがcrash hostと一致することを確認してから、**従来どおり15秒の合格予算**で待つ。
- 15秒を超えても当該プロセスが生存している場合に限り、Windows Error Reporting による終了遅延と判定する。その場合も**job objectの解放、親・継承した子孫の終了、非0終了コード、標準出力が空であること、標準エラーが一時ディレクトリを漏らさないことを、遅延後に改めて全て検証**したうえで `Assert.Inconclusive` を報告する。メッセージには "Inconclusive is not success" を含める。
- **WERが干渉しない環境では上記の分岐に入らず、従来の検証経路がそのまま実行される。** 実際、本日の負荷のない実行では当該テストは**成功**しており（失敗一覧に現れない）、`Inconclusive` 分岐は使われていない。
- `Q-007`（`L-S` reliability）の判定は**変更していない**。`src/` は変更していない。新規NuGet依存は無い。

**`Inconclusive`／skipは成功ではない。** 全回帰の成功件数へ加算しない。

### Web の既定タイムアウトに関する判定（`P2-31`）

2026-09-22の記録「既定タイムアウトで常に0とは主張しない」に対し、本日追加の実測を行った。

| # | 実測 | 結論 |
|---|---|---|
| W-1 | **負荷のない状態での既定設定 `npm test` は 85 files / 2,075件すべて成功**（所要 114.25秒）。`test:accessibility` 6/6、`test:assets` 6/6 も成功。本日の最終確認でも同じ結果を再現した | 既定タイムアウトで落ちるのは**テスト側の欠陥ではない** |
| W-2 | CPU負荷をかけた状態では、`WorkflowWorkspace.test.tsx` / `ResultGallery.test.tsx` が `5000ms` で時間切れ。いずれも**時間切れのみ**で、実時間依存のassertionや欠落した待機は見つからなかった | 原因は**本機の負荷**であり、実時間依存ではない |
| W-3 | 上記2ファイルにsuite単位で `30,000ms` のタイムアウトを設定して既定設定で3回連続実行したところ、**1回目は別ファイル**（`SettingsList.test.tsx` / `SettingsPanel.test.tsx` / `TimelineEditor.test.tsx`）が `5000ms` で時間切れし 22件失敗。2回目・3回目は 2,075件全成功 | **対象ファイルを絞ったタイムアウト延長では安定しない。** 時間切れは負荷に応じてファイル間を移動する |

**採った対処: コードを変更しない。** 理由は次の3点である。

1. W-1 より、**既定設定そのものに欠陥はない**。負荷のない状態では全件成功する。
2. W-3 より、**2ファイルへのタイムアウト延長は目的（既定設定での安定）を達成しない**。時間切れは他のファイルへ移るため、この変更は問題を解決しないまま差分だけを増やす。
3. 影響を受け得るファイルは5件以上あり、そのうち2件だけを延長する根拠が無い。全体のタイムアウトを引き上げる案は、**実際に遅くなった退行を見逃しやすくする**ため採らない。

**したがって `npm test` は「既定設定・負荷のない状態で全件成功」とだけ記録し、「常に失敗0」とは引き続き主張しない。** 本判定で新規npm依存は追加しておらず、`test:accessibility` / `test:assets` も削除していない。エージェントが一時的に加えた2ファイルのタイムアウト変更は**取り消した**（`git checkout -- src/HubRadio.Web`）。

### 本節で主張しないこと

- `V2-014` の合格、`Q-011`/`Q-012` の前進、`releaseApproved` の変更はいずれも**していない**。
- ASR精度・知覚品質・人手評価・clean OS・署名は**未実施**であり `PASS` としない。
- 納品まで通す統合試験（`P2-20`）は**未実装**である。

### package版の更新

今回の修正は**productionコードを含む**ため、開発用App packageを `0.1.2.0` から **`0.1.3.0`** へPATCH更新した（`Package.appxmanifest`、`build-package.ps1`、`verify-package.ps1`、版を固定している `Architecture.Tests.MsixPackagingProjectTests`）。更新後の Architecture runner 209件は全成功である。**版更新後のMSIX作成・インストール・検査は実行していない。**

### 受入素材の変更（2026-09-23 05:55）

**利用者が受入素材を `sample/hubradio-sample.mp4` へ変更した。** 本節の上記の測定・実行結果はすべて**変更前の素材**（`sample/Copilot  あなたの毎日の AI コンパニオン.mp4`）に対するものであり、履歴として保全する。

| 項目 | 変更前 | **変更後** |
|---|---|---|
| SHA-256 | `07904c1d…` | **`fb8d304f…`** |
| byte size | 9,971,886 | **117,030,068** |
| duration | 60.093243 秒 | **77.366667 秒** |
| 解像度・向き | 1280×720（16:9 横型） | **1080×1920（9:16 縦型）** |
| 映像 profile | `High` | **`Main`** |
| frame rate | 23.976 | **30.000** |
| 音声 | AAC-LC 44.1 kHz 2ch | **AAC-LC 48 kHz 2ch** |
| 音量（素材そのもの） | -16.70 LUFS / -1.88 dBTP（**基準内**） | **-21.19 LUFS**（基準外）/ -1.68 dBTP |

**新素材でのpipeline E2Eは未実施である。** 測定のみを[受入記録 §4](../../benchmarks/results/initial-pilot.md)に記録した。**`V2-014` は引き続き不合格**であり、本節の全回帰件数・判定は新素材での結果を含まない。受入harnessの素材digest定数（`SourceOnlyApprovedMediaPipelineTests`）と要求定義書§18は新素材へ更新済みである。

**新素材で判明した重要な差分:** 単一pass・2-passのいずれでも**loudnessは基準内に入るが True Peak が上限 `-1` を超える**（-0.99 / -0.90）。変更前素材と合わせて2素材で、filter段の `TP=-1.00` がAAC符号化後に上振れする同方向の観測を得た。**断定はしない**（2素材の観測であり一般性は未確認）。詳細は[受入記録 §4.4](../../benchmarks/results/initial-pilot.md)。
## 2026-09-22 `V2-013` 付帯成果物追加の全回帰

本節は追加のみで、以下の2026-09-09以前の記録は一切変更していない。

標準成果物setへ文字起こし（`transcript.json`）と使用区間一覧（`used-source-ranges.json`）を追加した変更に対する実測である。**本機で `test.ps1` を一括実行したのではなく、`build.ps1` 後に runner ごとに実行した記録**である。

| runner | 合計 | 成功 | skip | 失敗 |
|---|---:|---:|---:|---:|
| Architecture | 209 | 209 | 0 | 0 |
| Core | 732 | 731 | 1 | 0 |
| Infrastructure | 1362 | 1327 | 34 | **1** |
| App | 2420 | 2412 | 8 | 0 |
| Web Vitest（84 files） | 2067 | 2067 | 0 | 0 |
| Python worker | 170 | 169 | 1 | 0 |
| **合計** | **6960** | **6915** | **44** | **1** |

ビルドは警告0・エラー0。`git diff --check` は0件。

**変更前ベースラインとの差分（本機で同日に実測）**

| runner | ベースライン成功／skip／失敗 | 変更後 | 差分 |
|---|---|---|---|
| Architecture | 209／0／0 | 209／0／0 | 変化なし |
| Core | 731／1／0 | 731／1／0 | 変化なし |
| Infrastructure | 1327／34／**1** | 1327／34／**1** | 変化なし |
| App | 2402／8／0 | 2412／8／0 | 成功+10 |
| Web | 2040／0／**26** | 2067／0／0 | 成功+27、失敗-26 |
| Python worker | 169／1／0 | 169／1／0 | 変化なし |

**skipを成功へ読み替えていない。** 文書既載の「6,926成功・35 skip」は別環境・別実行単位の記録であり、本機のベースライン実測（6,878成功・44 skip・27失敗）とは**一致しない**。本節は本機の実測値のみを記載する。

### 失敗1件の切り分け（環境依存と実測で特定）

`HubRadio.Infrastructure.Tests.ProcessRunnerTests.RunAsync_HostCrashClosesJobAndKillsRootAndInheritedDescendant` は**本変更前のベースラインでも同一に失敗**し、単独実行でも再現する。原因は本機の Windows Error Reporting 処理により `Environment.FailFast` したプロセスの終了が遅延することで、最小の検証プログラムで3回測定した所要時間は 28.89秒 / 9.04秒 / 6.17秒であり、テストの15秒固定予算を超える。**本変更に起因しない。** 当該テストおよび `ProcessRunner` は本変更で1行も変更していない。

### Web 26件の切り分け（環境依存と実測で特定）

ベースラインの26失敗はすべて vitest 既定の5000msタイムアウト超過であり、`--testTimeout=30000` で `SettingsList.test.tsx`（21件）・`WorkflowWorkspace.test.tsx`（65件）ともに全件成功することを実測した。**製品欠陥ではない。** 変更後の実行では既定タイムアウトでも失敗0だったが、これは実行時の負荷条件によるものであり、**本変更がタイムアウトを改善したとは主張しない**。

### 本節で測定していないこと

- 実素材（`sample/Copilot  あなたの毎日の AI コンパニオン.mp4`）によるpipeline E2E — **未実施**
- ASR精度・知覚品質・A/V同期・loudness・True Peak の実測 — **未実施**
- 人手評価17項目 — **`PENDING` のまま**
- `UX-044` — **未測定のまま**
- clean OS受入・署名・一般配布 — **対象外・未実施**
- `releaseApproved=false` を維持する

## 2026-09-22 `V2-012` 過去版復元の全回帰

本節も追加のみで、2026-09-09以前の記録は変更していない。

過去版の一覧（`pipeline.versions`）と復元（`pipeline.restore`）を追加した変更に対する実測である。

| runner | 合計 | 成功 | skip | 失敗 |
|---|---:|---:|---:|---:|
| Architecture | 209 | 209 | 0 | 0 |
| Core | 732 | 731 | 1 | 0 |
| Infrastructure | 1362 | 1327 | 34 | **1** |
| App | 2424 | 2416 | 8 | 0 |
| Web Vitest（85 files） | 2073 | 2073 | 0 | 0（※） |
| Python worker | 170 | 169 | 1 | 0 |

ビルドは警告0・エラー0。`git diff --check` は0件。

- **Infrastructure の1件**は前節と同一の `ProcessRunnerTests.RunAsync_HostCrashClosesJobAndKillsRootAndInheritedDescendant` であり、環境依存として前節に根拠を記録済み。本変更に起因しない。
- **※ Web:** 既定タイムアウト（5000ms）での実行では本機の負荷により4件がタイムアウトした。`--testTimeout=30000` で当該3ファイル143件が全件成功することを実測し、**製品欠陥ではなく実行環境の速度依存**と確定した。上表の失敗0は延長タイムアウトでの確認結果であり、**既定タイムアウトで常に0であるとは主張しない。**
- 本変更が実際に壊した既存テストは1件（`WorkflowWorkspace.test.tsx` の画面往復時の要求列assert）で、読取専用の `pipeline.versions` を除外する形へ更新し、変更操作が0件であることを明示的にassertするよう強化した。

### 再現しなかった断続的失敗の記録（隠さずに記載する）

最終確認として同一コードで App runner を3回実行した。結果は **2,416成功／0失敗**、**2,415成功／1失敗**、**2,416成功／0失敗** であった。2回目の失敗は**どのテストであったかを記録できておらず、再現もしていない**。

- したがって「App runner は常に失敗0である」とは主張しない。上表の失敗0は3回中2回で観測した結果である。
- 失敗したテストを特定できていない以上、**環境依存であるとも断定しない。** 未解決の観測として本節に残す。

## 2026-09-22 自己成果物の敵対的レビューと修正後の全回帰

本節も追加のみで、2026-09-09以前の記録は変更していない。

作成者と独立した観点で本作業の成果物を再レビューし、**要件未達のCritical 2件**を含む指摘を反映した後の実測である。

| runner | 合計 | 成功 | skip | 失敗 |
|---|---:|---:|---:|---:|
| Architecture | 209 | 209 | 0 | 0 |
| Core | 732 | 731 | 1 | 0 |
| Infrastructure | 1362 | 1327 | 34 | **1** |
| App | 2427 | 2419 | 8 | 0 |
| Web Vitest（85 files） | 2075 | 2075 | 0 | 0（※） |
| Python worker | 170 | 169 | 1 | 0 |

ビルドは警告0・エラー0。`git diff --check` は0件。

### 修正したCritical 2件（いずれも「機能が成立しない」欠陥）

1. **納品が付帯成果物を落としていた。** `SourceOnlyDeliveryReceipt.CopyResult` / `Reconciliation` が `AncillaryArtifacts` を引き継がず、`DeliverResultAsync` の不変条件に必ず抵触していた。**付帯成果物の生成に成功した実行はすべて納品段階で失敗する**状態だった。両メソッドで引き継ぐよう修正し、回帰試験を追加した。
2. **派生候補spanを必ず拒否していた。** provenance検査が「tokenが属する発話の範囲」が span に収まることを要求していたが、強調・編集候補spanの範囲は発話の**部分区間**であり包含関係が逆だった。**候補が1件でもある実素材では付帯成果物が常に不成立**になる状態だった。tokenが実際に根拠づけられる範囲（tokenの実測範囲、なければ発話範囲）で判定するよう修正し、部分区間spanを受け付ける試験と偽造spanを拒否する試験の両方を追加した。

いずれも既存の自動試験では検出できていなかった。**pipelineを納品まで通す統合試験が無いことが根本原因**であり、この不足は解消していない。

### あわせて修正した主な問題

- 過去版一覧の応答が要求した案件IDと突き合わされず、別案件の応答を表示し得た。
- `dispose()` が過去版レーンの listener と保留要求を解放していなかった。
- 過去版の listener が最初の案件IDを捕捉し、案件切替後の応答を取りこぼしていた。
- 復元成功後に一覧が再取得されず、新しい版が出ないまま古い「現在の版」を表示し続けていた。
- 復元できない理由を「処理中」として表示し、理由を画面に出していなかった。
- 一覧未取得時に復元ボタンが無反応だった。

### 失敗1件の切り分け

`ProcessRunnerTests.RunAsync_HostCrashClosesJobAndKillsRootAndInheritedDescendant` は前節までと同一の Windows Error Reporting 依存の事象であり、本変更に起因しない。

**※ Web:** 既定タイムアウト（5000ms）での実行では本機の負荷により2件がタイムアウトした。`--testTimeout=30000` で当該2ファイル99件が全件成功することを実測した。上表の失敗0は延長タイムアウトでの確認結果であり、**既定タイムアウトで常に0であるとは主張しない。**

## 2026-09-22 package版の更新

付帯成果物・過去版復元の追加に伴い、開発用App packageを `0.1.1.0` から **`0.1.2.0`** へPATCH更新した（`Package.appxmanifest`、`build-package.ps1`、`verify-package.ps1`、および版を固定している `Architecture.Tests.MsixPackagingProjectTests`）。更新後の Architecture runner 209件は全成功である。

**MSIX の作成・署名・インストール・一般提供は実施していない。** 版番号の更新はそれらの実施を意味しない。`releaseApproved=false` を維持する。

### 本節で測定していないこと

- 復元が実際に子版と成果物を生成するpipeline E2E — **未実施**
- native GUI 上での復元操作 — **未実施**（`UX-041`/`UX-043` は対象外）

## 2026-09-22 `V2-014` 実素材受入の実行結果（不合格）

> この節は、下の「2026-09-22 `V2-014`（実素材受入）の状態」（同日・実行前の記録）を**置き換える**。旧節は履歴として保全する。

**結果: 不合格（FAIL）。実施したが合格していない。** 承認済み受入素材 `sample/Copilot  あなたの毎日の AI コンパニオン.mp4`（SHA-256 `07904c1d…` / 9,971,886 bytes）でsource-only pipelineを実行した。

- 外部素材を受け付けるopt-in E2E [SourceOnlyApprovedMediaPipelineTests](../../tests/HubRadio.App.Tests/SourceOnlyApprovedMediaPipelineTests.cs) を新規追加した。**productionコードは1行も変更していない。** 環境変数未設定時は `Inconclusive` となり、`test.ps1` の全回帰には影響しない。
- 実ingest（60.09秒素材）・実ASR・実構成までは到達したが、生成段階の事前検査 `StandardArtifactGenerator.ValidateCheckpoint` が `PreparationNotCheckpointed` を投げ、pipelineは `source_only.reconciliation_required` で停止した。**標準成果物は1件も生成されず、納品も評価も行われていない。**
- 3回の試行で同一箇所・同一codeを再現した。**同一コード・同一機で既存の合成4秒媒体E2E（`RealSourceOnlyPipeline_DeliversTheEntireGeneratedSet_AndReopensWithoutRerunning`）は納品・再読込まで成功する**ため、全般的なregressionでも環境不備でもなく**実素材固有**である。
- 切り分けの実測: 復元（`VersionService.GetStandardComposition`）は成功し**決定的**（2回復元して全digest一致）、`FormatException`・`InvalidOperationException` は未発生、実行中の `planDigest` と保存済み `Plan.PlanDigest` は一致。
- **不一致箇所を同定した（追試）。** `CompositionReady` が生成器へ渡すのと同一の preparation をテストへ渡すことを利用し、**productionコードを変更せずに** `ValidateCheckpoint` の7条件を再現した。不一致は **`Plan` のみ**で、差分23件はすべて `LongForm.Provenance.SourceSpans` に集中する。各要素を整列して比較すると**両者は同一集合**であり（`sameSetIgnoringOrder=true`）、**順序だけが異なる**。`Digest<T>` は配列順序に依存するため、これだけで生成が拒否される。合成4秒媒体は machine transcript の utterance が**0件**（保全された実行rootの成果物で実測）でspanが全体包絡の1件のみのため順序差が生じ得ず、同一コードでも成功する。異なる案件で2回再現した。
- **未確定:** 実行中と復元後のどちらの順序が正本かは決めていない。修正はFR-032の並びの意味づけと保存済みcanonical JSON・digestへの影響評価を伴うため、**本作業では実施していない**。
- 要求§18の記録6項目は①（入力digest・設定profile版・tool/model版）と②の一部のみ取得。**③字幕判定・④codec/同期/loudness/True Peak・⑤`OriginRef`・⑥自動代理評価は未取得**である。詳細は[benchmarks/results/initial-pilot.md §2](../../benchmarks/results/initial-pilot.md)。
- `tests/Acceptance/AC001_LongForm.cs` と `tests/Acceptance/Must_VerticalManual.cs` は**一切変更していない**。実測で成立した項目が無いため、`Q000Complete`/`Q001Complete` は `false`、人手15項目は `PENDING` のままである。
- 時間予算は4秒合成媒体の実績からの**推定**であり、本素材での上限は**検証されていない**（機能failureで停止したため）。準備段階の実測は17.5〜47秒だが、初回のcold実行のみ300秒を超過した。
- 性能・ASR精度・知覚品質・人手評価・native GUI・clean OS・署名・外部公開は**測定も実施もしておらず、`PASS` としない**。人手17項目 `PENDING`、`UX-044` 未測定、**`releaseApproved=false` を維持**する。

### 同日の全回帰（`V2-014` の変更反映後、本機で実測）

`build.ps1 -Configuration Release` は**0警告・0エラー**、`dotnet format --verify-no-changes` は終了コード0、`git diff --check` は0件。

| runner | 合計 | 成功 | skip | 失敗 |
|---|---:|---:|---:|---:|
| Architecture | 209 | 209 | 0 | 0 |
| Core | 732 | 731 | 1 | 0 |
| Infrastructure | 1362 | 1327 | 34 | **1** |
| App | 2428 | 2419 | 9 | 0 |
| Web | 2075 | 2075 | 0 | 0 |
| Python worker | 170 | 169 | 1 | 0 |

- 同日の前回記録との差分は App の **+1件（合計2427→2428、skip 8→9）** のみで、増加分は新規opt-in受入テストである。環境変数未設定のため `Inconclusive` となりskipに計上される。**skipを成功へ読み替えていない。**
- Infrastructure の失敗1件は `RunAsync_HostCrashClosesJobAndKillsRootAndInheritedDescendant` で、host crash時のprocess tree終了を検査する**環境依存（Windows Error Reporting設定）の既知failure**である。同日の前回記録と同一であり、今回の変更（`tests/HubRadio.App.Tests/` のテスト追加のみ）とは別assemblyで因果関係がない。**失敗0とは記録しない。**

## 2026-09-22 `V2-014`（実素材受入）の状態

**未実施。** 承認済み受入素材 `sample/Copilot  あなたの毎日の AI コンパニオン.mp4` によるpipeline E2Eは実行していない。

- 実施済みなのは素材特性の測定のみである（承認済みffprobe `n8.1.2-34-g9b6c8969e0-20260731` による再測定。[benchmarks/results/initial-pilot.md](../../benchmarks/results/initial-pilot.md)）。
- 既存の実E2E（`SourceOnlyRealPipelineTests`）は4秒の合成媒体専用で、外部素材パスを受け取る入口を持たない。外部素材を受け付けるopt-inシナリオ（計画の `T-30`）は**作成していない**。
- したがって要求定義書§18の記録6項目（入力digest・自動候補・字幕判定・codec/同期/loudness判定・`OriginRef`・自動代理評価）は**いずれも未取得**である。
- `tests/Acceptance/AC001_LongForm.cs` と `tests/Acceptance/Must_VerticalManual.cs` のチェックリストは**一切変更していない**。実測していない項目を `PASS` に変えていない。

## 2026-09-09 機能・全回帰の確認（現在）

以下はmain側で確認済みの最新runの記録であり、この文書更新で再実行した結果ではない。現行の全回帰はshard・runner別の実行であり、標準`test.ps1`を一括実行した記録ではない。総計は**6926成功・35 skip・失敗0**。focused再実行や別実行の実統合PASSを加算しない。

| runner | 成功 | skip | 失敗 | 確認済み原本／run ID |
|---|---:|---:|---:|---|
| App（全UID、ALL 2410） | 2402 | 8 | 0 | `app-shards-20260909T150051Z-0e9f12c6` summary |
| Infrastructure | 1337 | 25 | 0 | `observed-test-20260909T150315642Z-9705b12c` |
| Core | 731 | 1 | 0 | `observed-test-20260909T152906585Z-b2c5f617` |
| Architecture（PRI hook修正後） | 209 | 0 | 0 | `observed-test-20260909T163146676Z-0543b2b4` |
| Web Vitest（84 files） | 2066 | 0 | 0 | `web-all-20260909T134816937Z-b185d9e1` |
| accessibility／assets | 6／6 | 0 | 0 | main側の専用検証確認記録 |
| Python worker | 169 | 1 | 0 | `worker-all-fixed-20260909T111238857Z.log` |

Appの8 skipは**環境条件4件＋opt-in実統合4件**。後者4件は下記の別runでPASSしており、8件すべてを環境skipと記載せず、Appの2402成功へ足さない。Core最新runとArchitectureはログ読取まで確認済み。

| 別実行の実統合 | 結果 | 確認済みrun ID |
|---|---|---|
| 実ローカルASR→生成・QC→全7成果物保存→再open（39ファイルの空白修正・token同値確認後） | PASS（1件） | `observed-e2e-20260909T163411105Z-97f44d5a` |
| 明示再生成 | PASS | `observed-e2e-20260909T145632533Z-ba46a2bd` |
| retry | PASS | `observed-e2e-20260909T145852256Z-bac8df13` |
| 既知の合成Machine cueを手動字幕編集→保存・再読込→実FFmpeg／QC | PASS | `observed-test-20260909T145059046Z-3a086056` |

E2E入力は4秒の合成媒体であり、`V2-014`でもsample／提供実MOVは使用していない。実ASRの実行成功はASR精度の受入ではなく、既知Machine cueの字幕試験も実ASR由来の字幕とはしない。性能、知覚品質、人手評価のPASSを含めない。旧QC失敗と前回initial E2E `observed-e2e-20260909T144834883Z-41b8cdcc`は履歴として残し、最新runへ重複加算しない。

最終.NET format（`20260909T163633`）、PowerShell parser 35ファイル、`pip check`、`diff check`、Web production buildはPASS。39ファイルのC# token同値は`token-verification.json`で確認済みで、sourceの0-byte該当はなし。Viteの約848KB bundle警告は残り、性能受入のPASSとはしない。

runtime App build（`20260909T162001902Z`）は終了コード0・エラー0・警告0。`artifacts/validation/app-runtime-20260909c`のPRIは3760 bytesで、コピー済みrootを照合済み。通常MRTの`PackagingOutputs`とMSIXのPRI入力について、対象sourceの完全一致による除外を最小試験2/2/4で確認し、すべてPASSした。新出力の確認であり、旧`candidate-review-06`は変更していない。

根本原因6件・修正後証拠は[9月9日レビュー追補](reviews/ui-ux-adversarial-review.md#6-2026-09-09-実統合の修正後レビュー現在)を参照。対象52 ID完全一致・欠落0・重複0と18 raw原本hashは[詳細実装計画§0.8.2](planning/implementation-plan.md#082-対象52単位の完了と実接続の限定検証2026-09-10-jst追補)と`artifacts/validation/final-evidence-hashes-20260909c.json`へ記録済み。UX-048最終独立レビューは閉鎖済み。`UX-044`は未測定、人手17項目は`PENDING`を維持し、一般提供は`releaseApproved=false`のままである。

## 2026-09-08 再開後の最終ローカル検証（履歴）

解析の未送信編集を案件切替guardへ統合し、V2-010/011の評価入力相関、非同期出力認可、履歴不変性、再評価・backup/restoreを修正した。敵対的レビュー指摘の反映後に、最新表示で見つかった320px幅のroot横あふれも修正し、標準Release検証を再実行した。

| runner | 成功 | 環境skip | 失敗 |
|---|---:|---:|---:|
| Architecture | 192 | 0 | 0 |
| Core | 726 | 1 | 0 |
| Infrastructure | 1006 | 25 | 0 |
| App | 1464 | 3 | 0 |
| Web Vitest（75 files） | 1811 | 0 | 0 |
| accessibility／assets | 6／6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

原本`artifacts/validation/uiux-v2-full-20260908-b.log`、SHA-256 `e514dcb9b857a99b967195948ef472c4fc0fd1dcdaf2418e552e94a836ad5998`。format、warn-as-error build（警告0・エラー0）、依存・型・asset検証を含む全runner成功。環境skip29件やfocused再実行を成功数へ重複加算しない。Web/App manifestは共通SHA-256 `d60f1ff6c1fe06477bbfaca88e82f01e01e2b7811a8461c0f5af78331edd834c`で表示再測定対象と一致する。

明暗50画面・123設定・CSS文字32pxの5操作・解析guardを再確認。DPR1.25・visibility hiddenのfixture証拠であり、実native・物理入力・実媒体・人の理解は未評価。前のDPR約1の失敗測定と同条件の因果比較にはしない。[受入追補](../../tests/Acceptance/UIUX_CompactWorkflow.md#再開後の表示再確認2026-09-08)へ原本と制限を記録した。

`AutomatedEvaluationEngine`は供給された観測の集約器で、上のtest件数を実動画の自動評価結果へ転用しない。FS配送とDB receiptは完全に原子的ではなく、callback成功後のreceipt失敗ではreconciliationが必要。本番E契約・実WebView・最終UX-048は未完了で、全54単位完了後のCHANGELOG/PATCH条件も未成立である。

同日時点の範囲は本節の履歴表、指摘・反映・再確認は[UI/UX中間レビュー](reviews/ui-ux-adversarial-review.md)を参照する。以下9月7日の結果もその時点の履歴で、現在bytesの証拠へ読み替えない。

## 2026-09-07 UI/UX枝の検証結果（履歴）

4タブと独立設定、全体／案件scope、未保存保持、strictな`setting.txt`の明示保存・復元を実装し、最終assetで標準Release検証を実行した。実警告・実job開始・実成果物・再生成のE契約は未受領であり、通しの動画編集・納品や全54タスクの完了を意味しない。

| runner | 成功 | 環境skip | 失敗 |
|---|---:|---:|---:|
| Architecture | 192 | 0 | 0 |
| Core | 665 | 1 | 0 |
| Infrastructure | 897 | 25 | 0 |
| App | 1464 | 3 | 0 |
| Web Vitest | 1766 | 0 | 0 |
| accessibility／assets | 5／6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

skip29件はreparse point、所有者変更、非Windows経路、別account、Stage 0実PTS等の環境条件による。成功へ換算しない。`SettingsFileIntegrationTests`10件は上記Appの内数で、一時root/DBの実handler・backup/restoreを検証した。個別実行341件などの件数を全回帰に加算しない。

標準`test.ps1`は依存復元、format、warn-as-errorの.NET build、Web型・dependency/dist検証を含めて終了コード0。最終privateログ`artifacts/validation/uiux-full-verification-recovery-reviewed-20260907.log`のSHA-256は`f9e24d404885af796b5f6f54d344fbcc2198100e7b01247b8fff35d2ed72af65`。これは追加レビューで確認したfile置換競合、部分ACK/失敗後の入力保持、file操作のfocus復帰の修正を含む。前段の1660 Web tests時点のrun、EPERM、書式違反、旧payload fixture、ACL比較で失敗したログは削除せず、修正後runと分ける。Viteの500kB超bundle警告は残り、性能受入のPASSとはしない。

最終Web manifestとApp出力内のmanifestはSHA-256 `f58db99cfb263f0c5bbbc204bebc2c4fc983c86e6e3d8c0692bb88f9b5ead13e`で一致。追加修正後にfixtureの明暗50画面と123設定の表示測定、CSS文字拡大の5操作到達を再実行した。原本`uiux-browser-measurements-339a592a-02dc-44c1-a9e5-bcca93252c6f.json`のSHA-256は`87e7f8c461acb90d3a87a2780c27631831be4b09435e73f67b8e48561ea60394`。詳細は[比較・受入手順](../../tests/Acceptance/UIUX_CompactWorkflow.md#追加レビュー修正後の再確認2026-09-07)を参照する。実WebView2、物理keyboard、人の理解、実媒体は未評価。

file試験175件中171成功・4環境skip、Web recovery276成功のfocused試験は上記全回帰の内数である。file runnerに最低成功172を指定した試行のexit9は171成功との差によるもので、skipを成功へ足さず最低171で再実行してexit0を確認した。これらの試行も別記録として残す。

### 同梱noticeの実payload照合

既存候補を上書きせず`artifacts/portable/uiux-payload-current`へ新規の依存payloadを生成した。manifest自身を除く3260 file／1,819,389,846 bytes、集合SHA-256 `d3bbb21bec58cde1bf0a9691d397fbd42908624dc70ae85fd658e4a2d8117044`。`Licenses/THIRD-PARTY-NOTICES-NONFINAL.md`は現在のnoticeとSHA-256 `de211e7dfb2cc37b232bea502feff008b6d040594331bc00e97e18c5c3082946`で一致し、payload manifestにも結合される。

payload manifest SHA-256は`cd3073de0db4ad5f46b8bb146a28819077b68c968d9d1dea7dfd604fcb5e458a`。`appPublishRequired=true`、`releaseApproved=false`を維持する。**これは依存payloadであり、新しい単一EXE・署名・完全SBOM・一般提供候補を作成した証拠ではない。** `candidate-review-06`は旧証拠のまま保全している。

全54単位の完了には至っていないため、利用者が全完了後に指定したCHANGELOG Unreleased追記とpackage PATCH増分はまだ実施しない。この時点の実施範囲と保留は本節および[UI/UX中間レビュー](reviews/ui-ux-adversarial-review.md)の履歴として保全する。

## 1操作起動候補の品質gate

`candidate-review-06`はbounded local technical evaluationの候補です。公開候補の構成・SBOM・検証サマリーと、BuildLogs／ComponentInputsを含むprivate原本は公開candidateの兄弟ディレクトリへ分離して保全します。これはfilesystem ACLによる保護境界ではありません。公開サマリーはprivate原本の代替でも、候補全体のrelease authorizationでもありません。

- 本PCの正常系では、同梱Fixed WebView2 `152.0.4191.62`、cold/warmのnative Ready、通常終了、対象子孫processの終了、実行中の展開物と候補manifestの照合・不変性を確認した。この結果は当該PC・当該候補・正常系の観測に限定する。
- 実行入力とSDK展開cacheはrunごとの専用領域で追跡し、WebView2 user dataはportable実装の固定per-user pathを使用するためrun単位の分離を主張しない。workspace／案件DBとprivate証跡は削除せず、backup・hash照合・対象process限定で既存データへ影響しないことをrunごとに確認する。入力やprivate原本を通常の共有品質証拠へ混在させない。
- production-channel clean OSと標準ユーザーでの通常・異常系、offline、欠落・破損・容量不足・アクセス拒否、JavaScript受領ACKの独立確認、署名・MOTW・実配布はPASSではない。clean OS受入は現在BLOCKEDであり、保護を解除して結果を置き換えない。
- Fixed Runtimeやapp-local native依存の更新は、更新対象の版・内容・供給網条件を再確認し、候補、SBOM、証拠、起動受入を再作成・再照合する。既存候補のPASSを更新後のbytesへ遡及しない。

`releaseApproved=false`を維持する。license、notice、完全なSBOM／Model BOM、CVE、再配布条件、法的判断、clean OS、署名、実配布、業務E2Eがopen gateである間、一般提供の品質gateは成立しない。候補の供給網判断は[release-candidate review](licenses/release-review.md)、本PCでの観測値と未評価範囲は[OSL-014記録](reviews/osl-014-local-host-verification.md)を参照してください。

## 品質の判断原則

### 測定事実と判断を分ける

- 実行時間、件数、hash、status は raw 証拠から機械照合します。
- 採用、保留、手動化は canonical summary の判断として分離します。
- 0件は対象が存在しない証拠にも、検出器が正しい証拠にもなりません。
- JSON Schema 適合は、文章の正しさや意味の根拠を保証しません。
- 同一環境の byte 一致を、異なる PC／GPU／driver の再現性へ一般化しません。

### 任意確認と手動フォールバックを維持する

精度や利用条件が未確認の機能は、値を推測して有効化しません。手動話者割当、手動topic metadata、手動layout、字幕調整、手動試聴など、利用者が任意に修正できる経路を残します。これらを社内限定quick startの必須承認gateにせず、安全なfallbackで自動処理を完了できる設計にします。

### 自動代理評価の境界を明示する

- codec、全編decode、A/V同期、字幕、safe zone、loudness、True Peak、来歴、keyboard/focus/contrast等、closedな機械判定だけを自動代理評価へ含めます。
- 結果は`PASS`、`WARNING`、`FAIL`、`NOT EVALUATED`、規則版、実行環境、digestを記録します。
- `Automated proxy: PASS`を`HUMAN EVALUATION: PASS`、知覚品質保証、理解度保証、法的適合と呼びません。
- 既存の人手17項目`PENDING`は履歴として維持し、自動PASSへ書き換えません。

### 失敗証拠を消さない

- 過去の raw run は書き換えません。
- 問題レポートは既存内容を消さず追記します。
- 修正前後の run ID と SHA-256 を canonical やレビューへ残します。
- 未完成の staging は成功成果物として表示しません。

## 敵対的レビュー

各タスクの成果物を、次の5軸で独立に確認します。

1. **目的適合性:** 要求、task、利用者の目的を満たすか
2. **内容の妥当性:** 値、時刻、型、例、処理結果が内部整合しているか
3. **整合性:** 要求、ADR、実装、schema、証拠の間に矛盾がないか
4. **根拠性・不確実性管理:** 実測、推測、未測定、外部資料の範囲を区別しているか
5. **品質・運用性:** 失敗、取消、復旧、秘匿、再実行、配布を安全に扱えるか

レビューでは、実ファイル・実行結果・一次資料で確認できる問題だけを採用します。推測による指摘は反映しません。修正後に同じ観点を再確認し、未解決の重大問題がないことを確認してから次の依存タスクへ進みます。

既存の記録:

- [実現方式調査の敵対的レビュー](reviews/adversarial-review.md)
- [実装進行成果物の敵対的レビュー](reviews/implementation-adversarial-review.md)
- [要求版0.2・既定設定・再baseline敵対的レビュー](reviews/v2-defaults-adversarial-review.md)
- [V2-001版付き既定設定domain/schema敵対的レビュー](reviews/v2-001-default-settings-profile-adversarial-review.md)
- [V2-008標準Composition計画敵対的再レビュー](reviews/v2-008-standard-composition-adversarial-rereview.md)
- [UI/UX改善・V2中核の中間レビュー](reviews/ui-ux-adversarial-review.md)

## 変更ごとの最小確認

### 文書

- 現在、計画、条件付きの表現を分ける
- 正本の優先順位と相対リンクを確認する
- 外部数値を仕様、公式推奨、査読知見、広告資料、実務知見、製品判断に分類する
- URL、取得日、表示版・更新日、適用scope、直接支えるclaimを記録する
- 広告仕様をorganic/general仕様へ読み替えない
- パス変更時は、参照元と ADR-0001 の hash を同期する
- 未実装の UI、運用、性能値を作らない
- SVG は XML、アクセシビリティ、表示範囲を確認する

### PowerShell／Python の Stage 0 ツール

- parser／compile と対象環境での import
- 正常、失敗、timeout の machine-readable 結果
- source 不変、path／内容秘匿、孤児 process なし
- schema の過去 run 互換
- 0件／失敗を判定する control

### 後続の製品コード

製品コードの確認項目は、対応する実装計画タスクと受入基準に従います。テスト framework、architecture test、accessibility、backup／restore、MSIX は、実装前に空の仕組みだけを作りません。

## 完了条件

タスクを完了とする前に、次を満たします。

- 対象要求と依存 task が特定されている
- 変更したファイルと検証結果が一致している
- 正常系だけでなく、その task に実在する失敗経路を確認した
- 内容、秘密、PII、raw metadata を不要に保存していない
- 測定不能・未承認の項目を明記した
- 自動代理評価と人手評価を明確に区別した
- 敵対的レビューの実在指摘を修正し、再検証した

全体の gate と依存関係は [詳細実装計画](planning/implementation-plan.md) を参照してください。
