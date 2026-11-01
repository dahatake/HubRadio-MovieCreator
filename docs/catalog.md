# カタログ

既存の機能・API・テーブル・共通部品の対応表です。1 行 1 項目の短い表にし、詳細はリンク先のファイルに任せます。
機能の表は rd-author（要求 ID・題名・決定状態）と implementer（実装ファイル・テスト・共通部品）が更新します。
未実装の要求は、実装ファイルとテストの欄を「未実装」とします。

## 機能

| 要求 ID | 題名 | 決定状態 | 実装ファイル | テスト | 使っている共通部品 |
|---|---|---|---|---|---|
| FR-001 | 動画取り込み | 承認済み（依頼 2026-08-26） | src/HubRadio.App/Projects/BuzzEditingPreset.cs; src/HubRadio.App/Projects/ProjectAutoSettingsStore.cs; src/HubRadio.App/Projects/ProjectCreationService.cs; src/HubRadio.App/Web/ProjectBridgeHandler.cs; src/HubRadio.Web/src/features/projects/SourceMediaDetails.tsx; src/HubRadio.Web/src/features/projects/QuickStartPanel.tsx; src/HubRadio.Web/src/bridge/projectProtocol.ts | tests/HubRadio.App.Tests/BuzzEditingPresetTests.cs; tests/HubRadio.App.Tests/WebBridgeSchemaTests.cs; src/HubRadio.Web/src/features/projects/QuickStartPanel.test.tsx; src/HubRadio.Web/src/bridge/projectProtocol.test.ts; tests/system/e2e/chk10-media-output.ps1 | なし |
| FR-002 | 文字起こし | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-003 | 話者識別 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-004 | 用語辞書 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-005 | 不要区間検出 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-006 | トピック分割 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-007 | 見どころ候補抽出 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-008 | 長尺版生成 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-009 | YouTube横長版 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-010 | 縦型ショート版 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-011 | フック生成 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-012 | 字幕生成 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-013 | 強調テロップ | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-014 | テロップのタイミング | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-015 | オープニング | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-016 | シーンチェンジ | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-017 | ジャンプカット緩和 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-018 | 資料・画像差し込み | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-019 | BGMと効果音 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-020 | 音声補正 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-021 | サムネイル候補 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-022 | クイズ・パズル形式 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-023 | バズ型・高関心型短尺編集 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-024 | CTA | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-025 | 投稿付帯情報 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-026 | プレビュー | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-027 | 編集指示 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-028 | 複数案比較 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-029 | 任意確認と自動代理評価 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-030 | バージョン管理 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-031 | 出力 | 承認済み（依頼 2026-08-26） | src/HubRadio.App/Projects/SourceOnlyDeliveryService.cs | tests/HubRadio.App.Tests/SourceOnlyDeliveryServiceTests.cs; tests/system/e2e/chk10-media-output.ps1 | なし |
| FR-032 | 元動画との追跡性 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-033 | 設定管理 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-034 | 自動代理評価 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-035 | オープニング・クロージング動画の結合 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-036 | 編集開始の失敗の原因提示と自動修復 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-037 | Project一覧からのProjectの新規作成と削除 | 承認済み（依頼 2026-10-03） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-038 | 設定画面の各項目のやさしい説明 | 承認済み（依頼 2026-10-03） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-039 | 処理中の現在作業の表示 | 承認済み（依頼 2026-10-05） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-040 | Project名の変更とProjectの状態の表示 | 承認済み（依頼 2026-10-05） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-041 | 設定画面の選択肢を、操作している場所のすぐ近くで選ぶ | 承認済み（依頼 2026-10-05） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-042 | 「編集を開始」後の自動移動と、自動編集の進み具合の表示 | 承認済み（依頼 2026-10-05） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-043 | 設定画面で、Projectごとの設定を選んで変更する | 承認済み（依頼 2026-10-05） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-044 | 「Project一覧へ戻る」を画面上部の1か所に一元化する | 承認済み（依頼 2026-10-05） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-045 | Project一覧での動画のサムネイルの表示 | 承認済み（依頼 2026-10-05） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-046 | Projectの詳細画面でのProject削除 | 承認済み（依頼 2026-10-05） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-047 | 自動編集の音声末尾の許容と、失敗時の原因・対応策の表示 | 承認済み（依頼 2026-10-05） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-048 | 設定の選択肢の拡張（既定設定profile 1.2.0 のみ）と初期値の方針 | 承認済み（依頼 2026-10-05） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-049 | 実行ジョブの履歴の`job`フォルダへの保存 | 承認済み（依頼 2026-10-06） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-050 | 設定画面からの、変えられない項目と「根拠」の除外と、選択肢による値の選択 | 承認済み（依頼 2026-10-06） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-051 | 字幕焼き込み版で、話した内容を字幕として表示する | 承認済み（依頼 2026-10-06） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-052 | 仕上がり画面に、利用者が判断できる情報だけを表示する | 承認済み（依頼 2026-10-06） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-053 | すべてのProjectにサムネイルを作り、一覧に表示する | 承認済み（依頼 2026-10-06） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-054 | Projectの登録時に、動画に合わせて自動編集の設定を自動で決める | 承認済み（依頼 2026-10-06） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-055 | 1つの元動画から複数の動画を作り、設定の違いと仕上がりを見比べる | 承認済み（依頼 2026-10-06） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-056 | 呼称の変更 | 承認済み（依頼 2026-10-06） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-057 | 話の間を詰める（カットの強さ） | 承認済み（依頼 2026-10-07） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-058 | 縦長の元動画からは縦長の長尺動画を作る | 承認済み（依頼 2026-10-07） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-059 | 強調テロップを入れる | 承認済み（依頼 2026-10-07） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-060 | 効果音（SE）を入れる | 承認済み（依頼 2026-10-07） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-061 | 画面上部の表示を「Project一覧」と「Project」で分け、Jobごとに設定してJobを作る | 承認済み（依頼 2026-10-07。「保存」タブの廃止は依頼 2026-10-13 による文面の変更） | src/HubRadio.Web/src/features/workflow/workflowNavigation.ts、src/HubRadio.Web/src/features/workflow/WorkflowTabs.tsx、src/HubRadio.Web/src/features/workflow/WorkflowWorkspace.tsx、src/HubRadio.App/Media/UiThreadInvoker.cs、src/HubRadio.App/Media/MediaPicker.cs、「保存」タブと画面は削除 | src/HubRadio.Web/src/features/workflow/workflowNavigation.test.ts、src/HubRadio.Web/src/features/workflow/WorkflowTabs.test.tsx、src/HubRadio.Web/src/features/workflow/WorkflowWorkspace.test.tsx、tests/HubRadio.App.Tests/UiThreadInvokerTests.cs | UI スレッド実行 |
| FR-062 | Workspaceフォルダーの初期値を、実行ファイルのフォルダーの`\workspace`にし、設定画面から変更できるようにする | 承認済み（依頼 2026-10-07） | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-064 | Workspaceフォルダーを、フォルダー選択ダイアログ（参照…）で指定できるようにする | 承認済み（依頼 2026-10-08） | src/HubRadio.App/Web/WorkspaceLocationBridgeHandler.cs, src/HubRadio.App/Media/WorkspaceFolderPicker.cs, src/HubRadio.Web/src/features/settings/WorkspaceLocationSection.tsx, src/HubRadio.Web/src/features/settings/workspaceLocationProtocol.ts | tests/HubRadio.App.Tests/WorkspaceLocationBridgeTests.cs, src/HubRadio.Web/src/features/settings/WorkspaceLocationSection.test.tsx, src/HubRadio.Web/src/features/settings/workspaceLocationProtocol.test.ts | Web Bridge プロトコル, UI スレッド実行 |
| FR-063 | 仕上がり画面を、Jobごとに見て、複数のJobを映像と設定で比べる構成にする | 承認済み（依頼 2026-10-07） | 未確認（AU-002）。配置変更（AC-038 末尾）: src/HubRadio.Web/src/features/arrange/ArrangeablePanels.tsx、src/HubRadio.Web/src/features/workflow/WorkflowWorkspace.tsx | 未確認（AU-002）。配置変更: src/HubRadio.Web/src/features/arrange/ArrangeablePanels.test.tsx | コンポーネントの配置 |
| FR-131 | すべての画面で Job ごとのサムネイルを表示する | 承認済み（依頼 2026-10-13） | src/HubRadio.Web/src/features/jobs/JobThumbnail.tsx, src/HubRadio.Web/src/features/jobs/ProjectJobList.tsx, src/HubRadio.Web/src/features/jobs/AllProjectJobsPanel.tsx, src/HubRadio.Web/src/features/jobs/projectJobs.ts, src/HubRadio.Web/src/features/results/JobResultsBrowser.tsx, src/HubRadio.Web/src/features/jobs/jobs.css | src/HubRadio.Web/src/features/jobs/JobThumbnail.test.tsx | Job サムネイル |
| FR-132 | 仕上がり画面で、Job の動画とサムネイルを外部のフォルダーへ保存（書き出し）する | 承認済み（依頼 2026-10-13） | src/HubRadio.App/Web/JobExportBridge.cs, src/HubRadio.App/Web/Bridge.cs, src/HubRadio.App/Projects/ProjectApplicationRuntime.cs, src/HubRadio.Web/src/features/results/JobExportPanel.tsx, src/HubRadio.Web/src/features/results/jobExportProtocol.ts, src/HubRadio.Web/src/features/results/JobResultsBrowser.tsx, src/HubRadio.Web/src/features/results/results.css, src/HubRadio.App/Projects/SourceOnlyResult.cs, src/HubRadio.App/Rendering/StandardArtifactGenerator.cs, src/HubRadio.App/Rendering/StandardRenderGraphFactory.cs, src/HubRadio.App/Projects/SourceOnlyOutputEvaluation.cs, src/HubRadio.Web/src/features/workflow/pipelineProtocol.ts | tests/HubRadio.App.Tests/JobExportBridgeTests.cs, tests/HubRadio.App.Tests/StandardArtifactGeneratorTests.cs, src/HubRadio.Web/src/features/results/JobExportPanel.test.tsx, src/HubRadio.Web/src/features/results/jobExportProtocol.test.ts | Web Bridge プロトコル, Job 書き出し |
| FR-133 | Workspace 配下の Project フォルダー名を、GUID ではなく Project 名にする | 承認済み（依頼 2026-10-13。重複・使えない文字の拒否の細部は承認待ち＝Q-020） | src/HubRadio.Infrastructure/Files/ProjectFolders.cs、src/HubRadio.App/Projects/ProjectCreationService.cs、src/HubRadio.App/Projects/ProjectWorkspaceFileRemover.cs、src/HubRadio.Infrastructure/Files/WorkspacePathSafety.cs、src/HubRadio.Infrastructure/Diagnostics/StableErrorCodes.cs | tests/HubRadio.Infrastructure.Tests/ProjectFoldersTests.cs、tests/HubRadio.App.Tests/ProjectCreationServiceTests.cs、tests/HubRadio.Infrastructure.Tests/DiagnosticErrorCodeTests.cs | なし |
| FR-134 | 既存の GUID 名のフォルダーを、データを失わずに Project 名のフォルダーへ移行する | 承認済み（依頼 2026-10-13。移行できない Project の扱いは承認待ち＝Q-020） | src/HubRadio.Infrastructure/Files/ProjectFolders.cs、src/HubRadio.App/Projects/ProjectCreationService.cs、src/HubRadio.App/Projects/ProjectApplicationRuntime.cs、src/HubRadio.Infrastructure/Recovery/WorkspaceRecovery.cs | tests/HubRadio.Infrastructure.Tests/ProjectFoldersTests.cs、tests/HubRadio.App.Tests/ProjectCreationServiceTests.cs | なし |
| FR-135 | users-guide に、workspace のフォルダー構造の説明を作成する | 承認済み（依頼 2026-10-13） | users-guide/app/05-workspace-structure.md、users-guide/README.md | tests/toolkit/test_workspace_guide.py | なし |
| NFR-UX-001 | 操作性 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-PERF-001 | 性能 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-REL-001 | 信頼性 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-SEC-001 | セキュリティ | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-A11Y-001 | アクセシビリティ | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-AUD-001 | 監査性 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-EXT-001 | 拡張性 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-FACT-001 | 事実性 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-DIST-001 | 配布と初回起動 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-BR-001 | ブランド一貫性 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-BR-002 | 人物・役職 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-BR-003 | 機密情報 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-BR-004 | 権利管理 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-BR-005 | AI生成表示 | 承認済み（依頼 2026-08-26） | 未確認（AU-002） | 未確認（AU-002） | なし |
| NFR-UX-002 | レイアウトの保存・復元と既定への復元 | 承認済み | src/HubRadio.Web/src/features/arrange/arrangeStorage.ts、src/HubRadio.Web/src/features/arrange/ArrangeablePanels.tsx、src/HubRadio.Web/src/features/arrange/arrangeState.ts | src/HubRadio.Web/src/features/arrange/arrange.test.ts、src/HubRadio.Web/src/features/arrange/ArrangeablePanels.test.tsx | コンポーネントの配置 |
| NFR-UX-005 | 画面のコンポーネントの配置を利用者が変更できる | 承認済み | src/HubRadio.Web/src/features/arrange/ArrangeablePanels.tsx、src/HubRadio.Web/src/features/arrange/arrangeState.ts、src/HubRadio.Web/src/features/arrange/arrange.css、src/HubRadio.Web/src/features/workflow/WorkflowWorkspace.tsx、src/HubRadio.Web/src/features/settings/SettingsPanel.tsx | src/HubRadio.Web/src/features/arrange/arrange.test.ts、src/HubRadio.Web/src/features/arrange/ArrangeablePanels.test.tsx | コンポーネントの配置、デザイン基盤 |
| NFR-UX-003 | ペルソナ別の既定の情報表現 | 承認済み | 未確認（AU-002） | 未確認（AU-002） | なし |
| FR-136 | 演出テンプレートを選ぶだけで、字幕・テロップ・強調・効果音・配置が一括で適用される | 承認済み | src/HubRadio.App/StyleTemplates/StyleTemplateApplier.cs、src/HubRadio.App/StyleTemplates/JobStyleTemplateRecord.cs、src/HubRadio.App/Rendering/StandardRenderConfiguration.cs、src/HubRadio.App/Rendering/StandardRenderGraphFactory.cs、src/HubRadio.App/Rendering/StandardTemplateSubtitles.cs、src/HubRadio.App/Rendering/StandardMetadataGraphics.cs、src/HubRadio.App/Projects/SourceOnlyPipeline.cs、src/HubRadio.App/Web/SourceOnlyBridge.cs、src/HubRadio.Web/src/features/workflow/pipelineProtocol.ts、src/HubRadio.Web/src/features/jobs/StyleTemplatePicker.tsx、src/HubRadio.Web/src/features/jobs/JobCreationAction.tsx、src/HubRadio.Infrastructure/Rendering/FfmpegCompiler.cs、src/HubRadio.Web/src/features/jobs/jobs.css | tests/HubRadio.App.Tests/StyleTemplateTests.cs、tests/HubRadio.App.Tests/StyleTemplateLayoutTests.cs、src/HubRadio.Web/src/features/jobs/StyleTemplatePicker.test.tsx、src/HubRadio.Web/src/features/workflow/pipelineProtocol.test.ts | 演出テンプレート定義、デザイン基盤 |
| FR-137 | 演出テンプレートの種類と、各テンプレートの見せ方 | 承認済み | src/HubRadio.App/StyleTemplates/StyleTemplateCatalog.cs、src/HubRadio.App/StyleTemplates/StyleTemplateDefinition.cs、src/HubRadio.App/StyleTemplates/StyleTemplateApplier.cs、src/HubRadio.App/Rendering/StandardTemplateSubtitles.cs、src/HubRadio.App/StyleTemplates/StyleTemplateFonts.cs、src/HubRadio.App/StyleTemplates/CharacterPortraitStore.cs、src/HubRadio.Web/src/features/jobs/styleTemplates.ts、src/HubRadio.Web/src/features/jobs/StyleTemplatePicker.tsx、src/HubRadio.Web/src/features/jobs/CharacterPortraitRegistration.tsx、src/HubRadio.Web/src/features/jobs/JobCreationAction.tsx、src/HubRadio.Web/src/features/jobs/portraitProtocol.ts、src/HubRadio.App/Web/PortraitBridge.cs、src/HubRadio.App/Web/Bridge.cs、src/HubRadio.App/Rendering/StandardRenderConfiguration.cs、src/HubRadio.App/Rendering/StandardRenderGraphFactory.cs、src/HubRadio.App/Rendering/StandardArtifactGenerator.cs、src/HubRadio.Infrastructure/Rendering/FfmpegCompiler.cs、schemas/web-bridge.schema.json | tests/HubRadio.App.Tests/StyleTemplateTests.cs、tests/HubRadio.App.Tests/StyleTemplateLayoutTests.cs、tests/HubRadio.App.Tests/TemplateFontsAndCharacterDialogueTests.cs、src/HubRadio.Web/src/features/jobs/StyleTemplatePicker.test.tsx、src/HubRadio.Web/src/features/jobs/CharacterPortraitRegistration.test.tsx、src/HubRadio.Web/src/features/jobs/portraitProtocol.test.ts、src/HubRadio.Web/src/features/jobs/JobCreationActionPortraits.test.tsx、tests/HubRadio.App.Tests/PortraitBridgeTests.cs、tests/HubRadio.App.Tests/WebBridgeSchemaTests.cs、tests/HubRadio.Infrastructure.Tests/FfmpegCompilerTests.cs | 演出テンプレート定義、同梱書体、キャラクター立ち絵ストア |
| FR-138 | 対談番組と縦型 3 段のレイアウト、縦型の余白 | 承認済み | src/HubRadio.Infrastructure/Rendering/FfmpegCompiler.cs、src/HubRadio.App/Rendering/StandardRenderGraphFactory.cs、src/HubRadio.App/StyleTemplates/StyleTemplateLayout.cs、src/HubRadio.App/StyleTemplates/StyleTemplateRenderRecord.cs、src/HubRadio.App/Rendering/StandardTemplateSubtitles.cs、src/HubRadio.App/Rendering/StandardMetadataGraphics.cs、src/HubRadio.App/Profiles/StandardRenderProfileFactory.cs、src/HubRadio.App/Compositions/StandardCompositionOrchestrator.cs、src/HubRadio.App/Compositions/StandardCompositionEvidenceCodec.cs、src/HubRadio.App/Projects/SourceOnlyPipeline.cs、src/HubRadio.Web/src/features/jobs/talkShowInput.ts、src/HubRadio.Web/src/features/jobs/TalkShowInputPanel.tsx | tests/HubRadio.App.Tests/StyleTemplateLayoutTests.cs、tests/HubRadio.App.Tests/VerticalThreeBandTests.cs、tests/HubRadio.App.Tests/TalkShowRenderingTests.cs、tests/HubRadio.App.Tests/BundledRenderProfileTests.cs、tests/HubRadio.App.Tests/SourceOnlyOutputEvaluationTests.cs、src/HubRadio.Web/src/features/jobs/TalkShowInputPanel.test.tsx | 演出テンプレート定義、演出テンプレートの配置と色 |
| FR-139 | 演出テンプレートの品質・権利・表現の制約 | 承認済み | src/HubRadio.App/StyleTemplates/StyleTemplateColor.cs、src/HubRadio.App/StyleTemplates/StyleTemplateClaims.cs、src/HubRadio.App/StyleTemplates/JobStyleTemplateRecord.cs、src/HubRadio.App/StyleTemplates/StyleTemplateFonts.cs、packaging/template-fonts/、src/HubRadio.App/Rendering/StandardArtifactGenerator.cs、src/HubRadio.App/Rendering/StandardMetadataGraphics.cs | tests/HubRadio.App.Tests/StyleTemplateLayoutTests.cs、tests/HubRadio.App.Tests/TemplateFontsAndCharacterDialogueTests.cs | 演出テンプレート定義、演出テンプレートの配置と色、同梱書体 |
| FR-140 | 字幕への話者の割り当てと、立ち絵の切り出し登録 | 承認済み | src/HubRadio.App/StyleTemplates/CueSpeakerAssignments.cs、src/HubRadio.App/StyleTemplates/PortraitImageCropper.cs、src/HubRadio.App/StyleTemplates/CharacterPortraitStore.cs、src/HubRadio.App/StyleTemplates/StyleTemplateRenderRecord.cs、src/HubRadio.App/StyleTemplates/StyleTemplateApplier.cs、src/HubRadio.App/Rendering/StandardRenderGraphFactory.cs、src/HubRadio.App/Projects/SourceOnlyPipeline.cs、src/HubRadio.App/Web/PortraitBridge.cs、src/HubRadio.App/Web/Bridge.cs、src/HubRadio.App/Rendering/StandardTemplateSubtitles.cs、src/HubRadio.App/Rendering/StandardTemplateSubtitleBytes.cs、src/HubRadio.Web/src/features/jobs/speakerProtocol.ts、src/HubRadio.Web/src/features/jobs/SpeakerAssignmentPanel.tsx、src/HubRadio.Web/src/features/jobs/PortraitCropForm.tsx、src/HubRadio.Web/src/features/jobs/portraitProtocol.ts、src/HubRadio.Web/src/features/jobs/JobCreationAction.tsx、schemas/web-bridge.schema.json | tests/HubRadio.App.Tests/CueSpeakerAndWordTimingTests.cs、tests/HubRadio.App.Tests/CueSpeakerStoredSubtitleTests.cs、tests/HubRadio.App.Tests/SpeakerBridgeTests.cs、tests/HubRadio.App.Tests/WebBridgeSchemaTests.cs、src/HubRadio.Web/src/features/jobs/speakerAssignment.test.tsx | 演出テンプレート定義、キャラクター立ち絵ストア |
| FR-141 | 対談番組の話題サイドテロップと名前・肩書き帯の入力・確認 | 承認済み | src/HubRadio.App/StyleTemplates/TalkShowInput.cs、src/HubRadio.App/StyleTemplates/TalkShowRendering.cs、src/HubRadio.App/StyleTemplates/StyleTemplateRenderRecord.cs、src/HubRadio.App/Rendering/StandardMetadataGraphics.cs、src/HubRadio.App/Rendering/StandardRenderGraphFactory.cs、src/HubRadio.App/Rendering/StandardRenderConfiguration.cs、src/HubRadio.App/Rendering/StandardTemplateSubtitleBytes.cs、src/HubRadio.App/Projects/SourceOnlyPipeline.cs、src/HubRadio.App/Projects/SourceOnlyResultInputValidation.cs、src/HubRadio.App/Web/SourceOnlyBridge.cs、src/HubRadio.Web/src/features/jobs/talkShowInput.ts、src/HubRadio.Web/src/features/jobs/TalkShowInputPanel.tsx、src/HubRadio.Web/src/features/jobs/JobCreationAction.tsx、src/HubRadio.Web/src/features/workflow/pipelineProtocol.ts、src/HubRadio.Web/src/features/workflow/pipelineSession.ts、src/HubRadio.Web/src/features/workflow/PipelineProvider.tsx、schemas/web-bridge.schema.json | tests/HubRadio.App.Tests/TalkShowAndVerticalPlacementTests.cs、tests/HubRadio.App.Tests/TalkShowRenderingTests.cs、tests/HubRadio.App.Tests/WebBridgeSchemaTests.cs、src/HubRadio.Web/src/features/jobs/TalkShowInputPanel.test.tsx、src/HubRadio.Web/src/features/jobs/JobCreationActionLayout.test.tsx、src/HubRadio.Web/src/features/workflow/pipelineProtocol.test.ts | 演出テンプレートの配置と色 |
| FR-142 | ショート飛び出しの単語ごとのタイミング | 承認済み | src/HubRadio.App/StyleTemplates/CueWordTiming.cs、src/HubRadio.App/StyleTemplates/StyleTemplateRenderRecord.cs、src/HubRadio.App/Rendering/StandardRenderGraphFactory.cs、src/HubRadio.App/Rendering/StandardRenderConfiguration.cs、src/HubRadio.App/Projects/SourceOnlyPipeline.cs | tests/HubRadio.App.Tests/CueSpeakerAndWordTimingTests.cs | 演出テンプレート定義 |
| FR-143 | 縦型の既定字幕位置と余白・字幕領域・出力検査の整合 | 承認済み | src/HubRadio.App/StyleTemplates/VerticalPlacementInspector.cs、src/HubRadio.App/StyleTemplates/StyleTemplateRenderRecord.cs、src/HubRadio.App/StyleTemplates/JobStyleTemplateRecord.cs、src/HubRadio.App/Rendering/StandardTemplateSubtitles.cs、src/HubRadio.App/Rendering/StandardRenderGraphFactory.cs、src/HubRadio.App/Rendering/StandardArtifactGenerator.cs、src/HubRadio.Infrastructure/Rendering/OutputQc.cs、src/HubRadio.Infrastructure/Rendering/OutputQcPlacementEvidence.cs、src/HubRadio.App/Projects/SourceOnlyPipeline.cs、src/HubRadio.Web/src/features/jobs/JobCreationAction.tsx、schemas/web-bridge.schema.json | tests/HubRadio.App.Tests/TalkShowAndVerticalPlacementTests.cs、tests/HubRadio.App.Tests/TalkShowRenderingTests.cs、src/HubRadio.Web/src/features/jobs/JobCreationActionLayout.test.tsx | 演出テンプレートの配置と色 |

## API・イベント

| 名前 | 定義ファイル | 関連する要求 ID |
|---|---|---|
| Bridge `settings.workspace.browse` | schemas/web-bridge.schema.json | FR-064, FR-132 |
| Bridge `results.export` | schemas/web-bridge.schema.json | FR-132 |
| Bridge `portraits.list`・`portraits.register` | schemas/web-bridge.schema.json | FR-137 |
| Bridge `portraits.crop`・`speakers.get`・`speakers.assign`・`speakers.confirm` | schemas/web-bridge.schema.json | FR-140 |
| Bridge `pipeline.regenerate`（任意の `styleTemplateId`・`talkShowInput`・`subtitleIntoMargin`） | schemas/web-bridge.schema.json | FR-061, FR-136, FR-141, FR-143 |

## テーブル

| テーブル名 | 定義ファイル | 正本のシステム | 関連する要求 ID |
|---|---|---|---|

## 共通部品

| 部品名 | ファイル | 用途 | 使っている要求 ID |
|---|---|---|---|
| Web Bridge プロトコル | schemas/web-bridge.schema.json | WebView と本体の間の要求・応答の契約 | FR-064, FR-132 |
| Job 書き出し | src/HubRadio.App/Web/JobExportBridge.cs | Job の動画・サムネイルを選んだフォルダーへ上書きなしで保存（連番・件数・失敗理由）。フォルダー選択は FR-064 の `settings.workspace.browse` を再利用 | FR-132 |
| Job サムネイル | src/HubRadio.Web/src/features/jobs/JobThumbnail.tsx | Job 単位のサムネイル・代替表示・代替テキスト・選択可否（Project 単位の FR-045/053 の画像参照を再利用） | FR-131 |
| UI スレッド実行 | src/HubRadio.App/Media/UiThreadInvoker.cs | OS のファイル・フォルダー選択ダイアログを必ず UI スレッドで開く（初回起動の新規 Workspace で別スレッドから開いて異常終了する不具合の修正） | FR-061, FR-064 |
| 演出テンプレート定義 | src/HubRadio.App/StyleTemplates/StyleTemplateCatalog.cs | 演出テンプレート（ID・版・用途・縦横・見た目の例・色・書体・動き・効果音・配置）を宣言的な定義として持ち、FR-059/FR-060 の規則の上に適用する（PARAM-006/007 の上限つき） | FR-136, FR-137, FR-138, FR-139, FR-140, FR-142 |
| 同梱書体 | src/HubRadio.App/StyleTemplates/StyleTemplateFonts.cs、packaging/template-fonts/ | 太ゴシック（Noto Sans JP Black）・丸ゴシック（M PLUS Rounded 1c）・ポップ体（Potta One）を SIL OFL 1.1 のライセンスつきで同梱し（ビルドで TemplateFonts へコピー）、ファイルの digest を確かめて版を読み取り、Job の style-template.json に名前・版・ライセンスを記録する。利用者の PC の書体は使わない | FR-137, FR-139 |
| キャラクター立ち絵ストア | src/HubRadio.App/StyleTemplates/CharacterPortraitStore.cs | 利用者が登録した立ち絵（使用権の確認状態＝確認済み・未確認、キャラごとの字幕色）を保持し、1 つも登録がなければキャラクター対話のテンプレートを選べなくする。標準の立ち絵は同梱しない（NFR-BR-004） | FR-137, FR-140 |
| 演出テンプレートの配置と色 | src/HubRadio.App/StyleTemplates/StyleTemplateLayout.cs | 縦型 3 段の枠・余白（PARAM-005）・対談番組の話題サイドテロップと名前の帯・字幕 1 画面 20 字、文字色と背景の contrast（4.5:1）、効果保証・模倣表現の検査 | FR-138, FR-139, FR-141, FR-143 |
| デザイン基盤 | src/HubRadio.Web/src/styles.css | `--cp-*` デザイントークンとテーマ（ライト・ダーク）。新しい画面は値を直書きせずこれを使う | FR-136, NFR-UX-005 |
| コンポーネントの配置 | src/HubRadio.Web/src/features/arrange/ArrangeablePanels.tsx | 画面のコンポーネント（パネル）の移動・タブ化・非表示・最大化・方向・大きさの変更（キーボード可）、画面ごとの保存・復元（WebView2 のローカルプロファイルの localStorage）、確認つきの既定に戻す | NFR-UX-002, NFR-UX-005, FR-063 |
