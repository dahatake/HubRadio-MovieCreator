# `Q-008` セキュリティ敵対試験 報告書

**対象要求:** `Q-008`（[implementation-plan.md](../planning/implementation-plan.md) :773。依存 `M-012`・`S-014`・`Z-009`（該当時）、要求 `NFR-004`・`BR-003`）
**実施日:** 2026-09-24〜2026-09-25（ローカル、Windows x64、.NET SDK 10.0.400）
**base commit:** `7677074`（未コミットの作業ツリー変更を含む状態で実行）

> [!CAUTION]
> 本書は**既存の試験を実行した結果の転記**である。本タスクでは新規の試験を書いていない。
> 「未確認」と書いた項目は、試験が無い・実行できなかった・範囲外のいずれかであり、安全であることを示さない。
> 完了条件のうち「残余リスクは責任者承認」は満たしていない（§8）。

---

## 1. 結果の要約

| 領域 | 実行した試験 | 終了コード | 件数 | 未確認として残るもの |
|---|---|---:|---|---|
| media | `MediaImportSecurityTests` | 0 | 8件中 成功7・skip 1・失敗0 | 固定版（hash 固定）の実 FFprobe による破損 container 拒否（skip）、任意の resource bomb、production の resource 閾値 |
| bridge | `BridgeProtocolSecurityTests`、Architecture の Web sink 規則 | 0 / 0 | 175件 成功175、9件 成功9 | 追加すべき bridge 試験の対象 |
| worker | `test_protocol_security.py` | 0 | 9件 成功9 | worker 単体で追加できる具体的な試験 |
| FFmpeg | `ProcessRunnerTests`、Architecture の shell 規則 | 0 / 0 | 44件 成功44、9件 成功9 | Render IR／FFmpeg compile への敵対入力、固定版（hash 固定）の実 FFprobe（media と同じ） |
| prompt | `AnalysisSchemaTests` | 0 | 11件 成功11 | `Z-009`（根拠なしの提案を採用不可にする検査と敵対試験）の完了 |

Critical 未解決: 0（確認した範囲）
（上表の実行で失敗は0件、Critical と判断した所見も0件。ただし「未確認」の欄に挙げた領域は試験していないため、この件数に含まれていない）

## 2. media

- コマンド: `dotnet test --project tests\HubRadio.Infrastructure.Tests\HubRadio.Infrastructure.Tests.csproj --no-build --filter "FullyQualifiedName~MediaImportSecurityTests"`
- media exit code: 0
- 確認できたこと（[MediaImportSecurityTests.cs](../../../tests/HubRadio.Infrastructure.Tests/MediaImportSecurityTests.cs)）: traversal・UNC・device・ADS を含む source（:35）と portable workspace の path（:77）を拒否、option 風・metacharacter を含む leaf 名を全 tool 要求から隔離（:120）、破損・途中切れ container の signature を持つ入力で非0の probe 結果が fail-closed（:205。合成の process 結果を与える試験で、実 ffprobe は起動しない）、巨大・深層 metadata を内容を複製せず拒否（:299）、巨大な出力を上限で打切り（:355）、停止しない tool を期限で終了し path を開示しない（:396）
- skip: `ProbeAsync_CanonicalFfprobeRejectsSyntheticCorruptContainersWhenAvailable`（:253）。WinGet 配下に byte 数・SHA-256 が固定値と一致する ffprobe.exe が無いと Inconclusive になる。実行端末の WinGet の FFmpeg は固定版（[getting-started.md](../getting-started.md) §4）と異なる build だった。**成功には数えない**。本タスクの完了判定では、この1件だけを未確認と記録したうえで除外した（計画からの逸脱として利用者の確認待ち）
- 未確認: 任意の media resource bomb、production の resource 閾値（`M-012` の証拠範囲外。implementation-plan.md :21）

## 3. bridge（WebView bridge／Web）

- コマンド: `dotnet test --project tests\HubRadio.App.Tests\HubRadio.App.Tests.csproj --no-build --filter "FullyQualifiedName~BridgeProtocolSecurityTests"`、`dotnet test --project tests\Architecture.Tests\Architecture.Tests.csproj --no-build --filter "FullyQualifiedName~UnsafeWebContent|FullyQualifiedName~ProductWebSourceUsesTextRenderingWithoutUnsafeHtmlOrDynamicCodeSinks"`
- bridge exit code: 0
- 確認できたこと（[BridgeProtocolSecurityTests.cs](../../../tests/HubRadio.App.Tests/BridgeProtocolSecurityTests.cs)）: 許可一覧に無い method（script、`;calc`、Unicode の似た文字、option 風、大文字違い、余分な段）を `MethodNotAllowed` で拒否し拒否理由に method 名を反映しない（:53、31件）、Web から渡す identity・保存先 path・download URL・`__proto__` などを `SchemaViolation` で拒否（:78〜:152）、settings／pipeline の schema・identity・project scope・revision・上限（`settings.global.*`／`settings.file.*` は request 64 KiB・response 4 MiB、pipeline は request 1 KiB・response 8 MiB）
- 確認できたこと（[ArchitectureTests.cs](../../../tests/Architecture.Tests/ArchitectureTests.cs)）: 製品 Web source に、規則で定めた危険な raw HTML／動的 code の sink が見つからない（:280。陰性対照 :354、React の text 補間を許す :366）。規則外の書き方は対象外
- 未確認: Q-008 として追加すべき bridge 試験の対象。未実装の業務 UI（`S-014` の証拠範囲外。implementation-plan.md :23）

## 4. worker（Python）

- コマンド: `src\HubRadio.Worker\.venv\Scripts\python.exe -m pytest src\HubRadio.Worker\tests\test_protocol_security.py -q -rA`
- worker exit code: 0
- 確認できたこと（[test_protocol_security.py](../../../src/HubRadio.Worker/tests/test_protocol_security.py)）: 未知の RPC method を固定 error 1行・exit 1 で拒否し stdout／stderr に method 名を出さない（:52、4件）、未知・省略形の CLI option と filter 風 option を exit 2・usage 表示で拒否し Traceback を出さない（:80、5件）
- 未確認: worker 単体で追加できる具体的な試験。reparse point・別 account の ACL は §7 の残余リスクとして扱う

## 5. FFmpeg／ffprobe／process 境界

- コマンド: `dotnet test --project tests\HubRadio.Infrastructure.Tests\HubRadio.Infrastructure.Tests.csproj --no-build --filter "FullyQualifiedName~ProcessRunnerTests"`（`DOTNET_PROCESSOR_COUNT=1`）、`dotnet test --project tests\Architecture.Tests\Architecture.Tests.csproj --no-build --filter "FullyQualifiedName~Shell"`。`MediaImportSecurityTests` は §2 の結果を参照
- ffmpeg exit code: 0
- 確認できたこと（[ProcessRunnerTests.cs](../../../tests/HubRadio.Infrastructure.Tests/ProcessRunnerTests.cs)）: shell を介さず敵対的な引数が別々の token のまま渡る（:291）、巨大な出力を上限で打切り両 pipe を読み切る（:386）、期限切れで kill（:425）、NUL・不正 UTF-16 の引数や相対・空白付きの実行ファイル名を拒否
- 確認できたこと（ArchitectureTests.cs）: 製品 source に、規則で定めた shell 使用の pattern（`UseShellExecute = true`、既知の shell 実行ファイル名の直書き、Python の `shell=True`・`os.system`、Node の `exec` など）が見つからない（:268。陰性対照 :339、許可側 :442）。間接的・動的に渡される実行ファイル名は対象外
- 未確認: Render IR／FFmpeg compile（filter graph の組立て）への敵対入力（`S-014` の後に実装され証拠範囲外。implementation-plan.md :23）、固定版（hash 固定）の実 FFprobe の破損 container 拒否（§2 の skip）

## 6. prompt／grounding

- コマンド: `dotnet test --project tests\HubRadio.Core.Tests\HubRadio.Core.Tests.csproj --no-build --filter "FullyQualifiedName~AnalysisSchemaTests"`
- prompt exit code: 0
- 確認できたこと（[AnalysisSchemaTests.cs](../../../tests/HubRadio.Core.Tests/AnalysisSchemaTests.cs)）: 解析 schema に prompt 本文・文字起こし本文・file path・採用記録 ID・API key の property 名が無い（:314）。ModelRun／Detection／Suggestion の schema の形（:19〜:264）。これらは保存形式の制約であり、prompt injection に対する実行時の防御を示すものではない
- 登録済みの fixture: grounded ID／prompt-injection の合成 control（[tests/Fixtures/README.md](../../../tests/Fixtures/README.md) :79）。本実行でこの fixture を使う試験は走らせていない
- 未確認: `Z-009`（implementation-plan.md :703「schema適合を真実性とみなさず、根拠なしを採用不可」）の完了。計画上の名前 `AzureSuggestionValidator` の実装は本リポジトリに見つからない（file 名での検索）。敵対試験の有無は未確認

## 7. 残余リスク

| # | リスク | 根拠 | 状態 |
|---|---|---|---|
| 1 | 固定版（hash 固定）の実 FFprobe による破損 container 拒否が本実行で未確認 | §2 の skip | 固定版 FFmpeg を入れた端末で §2 を再実行すれば確認できる |
| 2 | 任意の media resource bomb、production の resource 閾値 | implementation-plan.md :21 | 閾値が未定義。本実行では該当する試験を確認していない |
| 3 | Render IR／FFmpeg compile への敵対入力 | implementation-plan.md :23 | 対象 API と危険な filter の一覧が未確認。本実行では該当する試験を確認していない |
| 4 | `Z-009` の根拠検査の完了が未確認（cloud 提案を使う場合） | implementation-plan.md :703、:773 | 計画上の名前 `AzureSuggestionValidator` の実装は見つからない。敵対試験の有無は未確認 |
| 5 | 環境依存の reparse point、別 account の ACL | implementation-plan.md :23 | `S-014` の証拠範囲外 |
| 6 | 未実装の業務 UI | implementation-plan.md :23 | 実装後に bridge 試験の対象を見直す |

## 8. 承認

承認: 未承認
（責任者の action。本タスクでは承認していない。Q-008 の完了条件「残余リスクは責任者承認」は未達）
