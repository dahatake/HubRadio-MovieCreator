# Hubらじお マルチフォーマット動画自動編集ソフトウェア 要求定義書

**文書種別:** 要求定義書

**対象:** Hubらじお、および同種の企業向けトーク・インタビュー動画

**版:** 0.2 Draft

**初版作成日:** 2026年8月26日

**再baseline日:** 2026年9月2日

**起動・配布要求の追補日:** 2026年9月6日（OSL-001、利用者がデフォルト案を採用）

**UI/UX要求の追補承認日:** 2026年9月7日（UX-001、第2版方針A01〜A20の既定案と実装を利用者が明示承認。恒久化先は[ADR-0011](dev/decisions/ADR-0011-compact-workflow-and-settings-file.md)）

**オープニング・クロージング結合要求の追補日:** 2026年10月1日（FR-035、AC-008。§0 変更履歴を参照）

**状態:** 要求再baseline済み。`V2-000`〜`V2-009`を実装・レビュー済み。`V2-010`以降の自動代理評価、state、実render／delivery、最終受入は未完了

OS-onlyの1操作起動要求を追加した。この追補は起動実装、クリーンOS検証、署名、一般提供の完了を意味しない。

UI/UX追補は承認済み要求の追加であり、UI・設定fileの実装／受入完了や現行V2 backendの完成を意味しない。上記の実装状況・過去の状態記録は変更しない。

**既定値の根拠:** [既定設定調査](dev/research/default-settings-research.md)

**設計判断:** [ADR-0009](dev/decisions/ADR-0009-source-only-defaults-and-automated-evaluation.md)

---

## 文書の構成

本書は要求定義書の索引である。要求の本文・受入基準・仮定・質問票は、次の境界ごとのファイルにある（2026-10-07に本書が300KBを超えたため分割した。章・節の番号は分割前のまま）。要求IDで検索すると、索引の行と、境界ファイルの見出し（`## FR\-061`など）の下の要求だけが取り出せる。

| ファイル | 境界 | 収める節 |
| --- | --- | --- |
| [requirements/common.md](requirements/common.md) | 全体 | §1 文書の目的、§2 背景と課題、§3 目標、§4 対象範囲、§5 想定利用者、§6 入力要件、§7 基本処理フロー、§16 優先順位、§17 初期リリース案、§18 初期検証シナリオ、§19 未決事項（全体）、§20 完了条件 |
| [requirements/editing.md](requirements/editing.md) | 自動編集の生成 | §8のFR-001〜FR-035・FR-047・FR-048・FR-051・FR-054・FR-057〜FR-060、§9 編集ルール、§10 テンプレート要求、§15のAC-001〜AC-003・AC-005・AC-006・AC-008・AC-022・AC-023・AC-026・AC-029・AC-032〜AC-035、§19.1・§19.9・§19.10・§19.13・§19.14（質問票Q-001〜Q-007） |
| [requirements/workspace.md](requirements/workspace.md) | 画面とProject管理 | §8のFR-036〜FR-046・FR-049・FR-050・FR-052・FR-053・FR-055・FR-056・FR-061・FR-062・FR-063、§14 画面要求、§15のAC-009〜AC-021・AC-024・AC-025・AC-027・AC-028・AC-030・AC-031・AC-036・AC-037・AC-038、§19.2〜§19.8・§19.11・§19.12・§19.15・§19.16・§19.17（質問票Q-008〜） |
| [requirements/platform.md](requirements/platform.md) | 非機能・ブランド・データ | §11 非機能要求（NFR-001〜NFR-009）、§12 ブランド・コンプライアンス要求（BR-001〜BR-005）、§13 データ管理要求、§15のAC-004・AC-007 |

## 要求の索引

優先度は、要求の表にMUST／SHOULD／MAYがある場合はその最上位、表が無い要求は「§16参照」（[common.md](requirements/common.md)の§16 優先順位）とする。決定状態の欄が無い既存の要求は承認済みとして扱う。

| 要求ID | 題名 | 決定状態 | 優先度 | ファイル |
| --- | --- | --- | --- | --- |
| FR-001 | 動画取り込み | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-002 | 文字起こし | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-003 | 話者識別 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-004 | 用語辞書 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-005 | 不要区間検出 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-006 | トピック分割 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-007 | 見どころ候補抽出 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-008 | 長尺版生成 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-009 | YouTube横長版 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-010 | 縦型ショート版 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-011 | フック生成 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-012 | 字幕生成 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-013 | 強調テロップ | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-014 | テロップのタイミング | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-015 | オープニング | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-016 | シーンチェンジ | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-017 | ジャンプカット緩和 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-018 | 資料・画像差し込み | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-019 | BGMと効果音 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-020 | 音声補正 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-021 | サムネイル候補 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-022 | クイズ・パズル形式 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-023 | バズ型・高関心型短尺編集 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-024 | CTA | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-025 | 投稿付帯情報 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-026 | プレビュー | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-027 | 編集指示 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-028 | 複数案比較 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-029 | 任意確認と自動代理評価 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-030 | バージョン管理 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-031 | 出力 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-032 | 元動画との追跡性 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-033 | 設定管理 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-034 | 自動代理評価 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-035 | オープニング・クロージング動画の結合 | 承認済み（決定状態の欄なし。既存の要求として扱う） | MUST | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-036 | 編集開始の失敗の原因提示と自動修復 | 承認済み（決定状態の欄なし。既存の要求として扱う） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-037 | 案件一覧からの案件の新規作成と削除 | 承認済み（依頼 2026-10-03） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-038 | 設定画面の各項目のやさしい説明 | 承認済み（依頼 2026-10-03） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-039 | 処理中の現在作業の表示 | 承認済み（依頼 2026-10-05） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-040 | 案件名の変更と案件の状態の表示 | 承認済み（依頼 2026-10-05） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-041 | 設定画面の選択肢を、操作している場所のすぐ近くで選ぶ | 承認済み（依頼 2026-10-05） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-042 | 「編集を開始」後の自動移動と、自動編集の進み具合の表示 | 承認済み（依頼 2026-10-05） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-043 | 設定画面で「この動画」を選んで案件内の設定を変更する | 承認済み（依頼 2026-10-05） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-044 | 「案件一覧へ戻る」を画面上部の1か所に一元化する | 承認済み（依頼 2026-10-05） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-045 | 案件一覧での動画のサムネイルの表示 | 承認済み（依頼 2026-10-05） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-046 | 案件の詳細画面での案件削除 | 承認済み（依頼 2026-10-05） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-047 | 自動編集の音声末尾の許容と、失敗時の原因・対応策の表示 | 承認済み（依頼 2026-10-05） | MUST | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-048 | 設定の選択肢の拡張（既定設定profile 1.1.0）と初期値の方針 | 承認済み（依頼 2026-10-05） | MUST | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-049 | 実行ジョブの履歴の`job`フォルダへの保存 | 承認済み（依頼 2026-10-06） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-050 | 設定画面からの、変えられない項目と「根拠」の除外と、選択肢による値の選択 | 承認済み（依頼 2026-10-06） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-051 | 字幕焼き込み版で、話した内容を字幕として表示する | 承認済み（依頼 2026-10-06） | MUST | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-052 | 仕上がり画面に、利用者が判断できる情報だけを表示する | 承認済み（依頼 2026-10-06） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-053 | すべてのProjectにサムネイルを作り、一覧に表示する | 承認済み（依頼 2026-10-06） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-054 | Projectの登録時に、動画に合わせて自動編集の設定を自動で決める | 承認済み（依頼 2026-10-06） | MUST | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-055 | 1つの元動画から複数の動画を作り、設定の違いと仕上がりを見比べる | 承認済み（依頼 2026-10-06） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-056 | 呼称の変更 | 承認済み（依頼 2026-10-06） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-057 | 話の間を詰める（カットの強さ） | 承認済み（回答 2026-10-07） | MUST | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-058 | 縦長の元動画からは縦長の長尺動画を作る | 承認済み（回答 2026-10-07） | MUST | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-059 | 強調テロップを入れる | 承認済み（回答 2026-10-07） | MUST | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-060 | 効果音（SE）を入れる | 承認済み（回答 2026-10-07） | MUST | [editing.md](requirements/editing.md)（自動編集の生成） |
| FR-061 | 画面上部の表示を「Project一覧」と「Project」で分け、Jobごとに設定してJobを作る | 承認済み（依頼 2026-10-07） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-062 | Workspaceフォルダーの初期値を`\workspace`にし、設定画面から変更できるようにする | 承認済み（依頼 2026-10-07） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| FR-063 | 仕上がり画面を、Jobごとに見て、複数のJobを映像と設定で比べる構成にする | 承認済み（依頼 2026-10-07） | MUST | [workspace.md](requirements/workspace.md)（画面とProject管理） |
| NFR-001 | 操作性 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| NFR-002 | 性能 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| NFR-003 | 信頼性 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| NFR-004 | セキュリティ | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| NFR-005 | アクセシビリティ | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| NFR-006 | 監査性 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| NFR-007 | 拡張性 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| NFR-008 | 事実性 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| NFR-009 | 配布と初回起動 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| BR-001 | ブランド一貫性 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| BR-002 | 人物・役職 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| BR-003 | 機密情報 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| BR-004 | 権利管理 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |
| BR-005 | AI生成表示 | 承認済み（決定状態の欄なし。既存の要求として扱う） | §16参照 | [platform.md](requirements/platform.md)（非機能・ブランド・データ） |

---

## 0\. 変更履歴

本節は2026-10-01に新設した。それ以前の追補は上記の文書情報（追補日）に記録している。削除した要求IDは再利用しない。

| 日付 | 依頼の要約 | 追加 | 変更 | 削除 |
| --- | --- | --- | --- | --- |
| 2026-10-01 | 成果物動画の前後に、利用者が指定する動画（mp4等）をOpening → 生成動画 → Closingの順で結合する機能を追加 | FR-035（FR-035.1〜FR-035.12）、AC-008（AC-008.1〜AC-008.10）、§19.1の仮定A-035-01〜A-035-10 | §4.1（対象への1項目追加）、FR-015（FR-035との関係を追記）、§16 Must（1項目追加） | なし |
| 2026-10-03 | 「編集を開始」で「操作を完了できませんでした」となり先へ進めない問題の原因調査、原因と対応策の提示、自動修復 | FR-036（FR-036.1〜FR-036.7）、AC-009（AC-009.1〜AC-009.6）、§19.2の仮定A-036-01〜A-036-03 | §16 Must（1項目追加）、エラーcode `project.runtime_unavailable`の追加（`project.finalizeDraft`の依存失敗が返すcodeを`project.operation_failed`から変更） | なし |
| 2026-10-03 | 案件削除時に、案件のワークスペース内のファイルも削除（FR-037の拡張） | FR-037.10、FR-037.11、AC-010.11〜AC-010.13 | FR-037.3（確認文）、FR-037.4（成功応答に`filesRemoved`）、FR-037.6（論理削除の範囲）、FR-037の対象外、A-037-02（上書き）、AC-010.4、AC-010.8 | なし |
| 2026-10-03 | 案件一覧画面での案件の新規作成・削除、設定画面の各項目へ初心者向けの詳細説明を表示 | FR-037（FR-037.1〜FR-037.9）、FR-038（FR-038.1〜FR-038.6）、AC-010（AC-010.1〜AC-010.10）、AC-011（AC-011.1〜AC-011.6）、§19.3の仮定A-037-01〜A-037-06、`project.delete`、エラーcode `project.delete_blocked`、DB schema version 13（`project_creation_draft_deletions`）、操作`DeleteProject` | §4.1、§14.1（案件一覧）、§14.4、§16 Must、案件一覧の主ボタン文言（「元動画を選ぶ」→「新しい案件を作成」。操作は従来の`project.createDraft`のまま） | なし |
| 2026-10-05 | 「編集を開始」を押した後、アプリが現在何をしているかを表示する | FR-039（FR-039.1〜FR-039.5）、AC-012（AC-012.1〜AC-012.5）、§19.4の仮定A-039-01〜A-039-02 | §16 Must（1項目追加） | なし |
| 2026-10-05 | 案件一覧での既存案件の削除、「編集を開始」後の作業内容の表示、案件の状態（案件名・社内限定・確認待ち等）の画面上での編集、設定画面の選択肢を操作している場所のすぐ近くで選べるようにする | FR-040（FR-040.1〜FR-040.7）、FR-041（FR-041.1〜FR-041.5）、AC-013〜AC-016、`project.rename`、DB schema version 14（`project_display_names`）、§19.5の仮定A-040-01〜A-040-05、A-041-01、TBD-040-01〜TBD-040-02、AI提案P-041-01 | §16 Must（2項目追加）、案件の表示名の優先（一覧・ヘッダー・かんたん編集の見出し） | なし |
| 2026-10-05 | 「編集を開始」後に自動で「自動編集」画面へ進み、処理の進み具合（段階）を分かるようにする。設定画面で「この動画」を選んで案件内の設定を変更できることの確認と、選べない場合の案内 | FR-042（FR-042.1〜FR-042.4）、FR-043（FR-043.1〜FR-043.3）、AC-017、AC-018、§19.6の仮定A-042-01〜A-042-03、A-043-01、TBD-043-01 | §16 Must（2項目追加）、FR-039.2の完了後の案内の「「自動編集」画面で処理状況を確認できます」は、画面が自動で移るため、移動後に確認できる状態を指す（文面は変更しない） | なし |
| 2026-10-05 | 「案件一覧へ戻る」を画面上部の1か所（旧「動画の切替」、表示名「案件一覧へ戻る」）に一元化し、重複するボタンを削除 | FR-044（FR-044.1〜FR-044.3）、AC-019 | FR-037/FR-040の画面内の「一覧へ戻る」「別の動画を選ぶ」「取消」（削除） | なし |
| 2026-10-05 | 案件一覧の各案件に、動画のサムネイルを表示 | FR-045（FR-045.1〜FR-045.6）、AC-020（AC-020.1〜AC-020.5）、`project.list`の各案件の`thumbnail`、§19.7の仮定A-045-01〜A-045-04 | §16 Must（1項目追加） | なし |
| 2026-10-05 | 案件の詳細画面に、開いている案件自身を削除する「案件削除」ボタンと削除機能を追加（機能追加。削除は既存の`project.delete`を再利用） | FR-046（FR-046.1〜FR-046.4）、AC-021（AC-021.1〜AC-021.4）、§19.8の仮定A-046-01〜A-046-03 | §16 Must（1項目追加）、削除成功時に開いている案件を閉じる反映（`projectState.ts`） | なし |
| 2026-10-05 | 「自動編集」が`source_only.unsupported_source_timing`で失敗する不具合（承認素材`hubradio-sample.mp4`で発生）の修正と、自動編集の失敗時に原因と対応策を画面に表示する（不具合修正＋機能追加） | FR-047（FR-047.1〜FR-047.4）、AC-022（AC-022.1〜AC-022.5）、§19.9の仮定A-047-01〜A-047-03 | §16 Must（1項目追加）、ジョブの進行状況の失敗code表示（codeだけの表示→説明・原因・対応策・codeの表示）、各音声区間のffmpeg filter（`apad`の追加） | なし |
| 2026-10-05 | 設定の初期値を動画に合わせて最適にしたい、選べる項目を選べるようにしたい（既定設定profile 1.1.0の追加、縦型ショートの本数・サムネイル候補の数・つかみの開始を生成処理が守る） | FR-048（FR-048.1〜FR-048.5）、AC-023（AC-023.1〜AC-023.5）、既定設定profile 1.1.0、§19.10の仮定A-048-01〜A-048-04、AI提案P-048-01〜P-048-02 | §16 Must（1項目追加）、globalの既定値を起動時に1.1.0へ移す、合成計画の設定検査（本数・候補数・つかみの範囲） | なし |
| 2026-10-06 | アプリケーションの実行ジョブの履歴を`/job/`に保存する（デバッグ・エラー時のサポート用。機能追加） | FR-049（FR-049.1〜FR-049.10）、AC-024（AC-024.1〜AC-024.9）、§19.11の仮定A-049-01〜A-049-05、AI提案P-049-01〜P-049-02、ファイル形式`hubradio.job-history.v1` | §16 Must（1項目追加） | なし |
| 2026-10-06 | 設定画面で、変えられない項目を除外し、変えられる項目は選択肢から選べるようにする。設定画面から「根拠」を除外する（変更＋削除） | FR-050（FR-050.1〜FR-050.9）、AC-025（AC-025.1〜AC-025.8）、分類表`settingEditorCatalog.json`（`hubradio.setting-editor-catalog.v1`）、§19.12の仮定A-050-01〜A-050-07、競合C-050-01、AI提案P-050-01〜P-050-03 | §16 Must（1項目追加）。§6.4・FR-033・§14.4・AC-006の「全設定」「根拠区分」の画面表示と、FR-038.1の開閉の名前「初期値・用途・根拠」は、FR-050で上書き（承認済みの文面は変更しない。C-050-01）。FR-038.4の汎用の文、FR-038.2の説明3項目の文 | 設定画面の「根拠」の表示（要求IDの削除は無し） |
| 2026-10-01 | FR-035の結合機能をGUI（仕上がり画面）へ統合 | FR-035.13〜FR-035.21、GUI応答status表、AC-008.11〜AC-008.16、仮定A-035-11〜A-035-13 | FR-035の目的と境界・対象外、A-035-01（GUI対象外→GUIとCLIの両方を提供）、§4.1、§14.2、§16 Must | なし |
| 2026-10-06 | 自動編集の設定を動画に合わせて自動で決める（ネット検索なしの組み込みの規則）、1つの元動画から複数の動画を作って設定の違いを比べる、「案件」→「Project」・「自動編集」→「ジョブの状態の確認」への呼称変更、字幕焼き込み版に字幕がほぼ入らない不具合の修正、仕上がり画面からのID等の除外、サムネイルを必ず作る（機能追加＋変更＋不具合修正） | FR-051〜FR-056、AC-026〜AC-031、§19.13の仮定A-051-01〜A-056-02、競合C-051-01、質問票Q-001〜Q-005、AI提案P-053-01・P-055-01、字幕の生成規則`standard-subtitle-builder.full-coverage-source-tokens.v3`、`project.list`の`poster`、下書きの`autoSettings`、`pipeline.versions`の各版の`createdAt`・`settings`・`outputs`、研究メモ`docs/dev/research/buzz-editing-best-practices.md` | FR-045の対象外（「元動画のフレームからの一覧用画像の新規生成」「下書きや生成前の案件への仮の画像」を対象外から外し、FR-053で対象にした）、FR-045.3（`thumbnail`が無い場合はFR-053の`poster`を表示）、FR-042・§14の画面名「自動編集」（表示名を「ジョブの状態の確認」に変更、FR-056）、§16 Must（6項目追加）、新しい実行の字幕の生成規則（v2→v3。保存済みの結果はv2のまま再現） | なし |
| 2026-10-07 | 質問票Q-006（A）・Q-007（B）の回答の反映: 文字起こしの語の時刻を無音に合わせて直し、発話の中の間も詰める | FR-057.5・FR-057.6、AC-032.5、記録`source-only.asr-timing-correction.v1`（規則`fr057-asr-word-edge-to-silence.v1`） | FR-057.2の「発話と発話のあいだ」（語と語のあいだに拡張）、A-057-01、P-057-01（採用）、P-060-01（不採用） | なし |
| 2026-10-07 | 質問票Q-001〜Q-005の回答（Q-002は利用者の指定B、ほかは最良の選択A）の反映: 話の間を詰める、縦長の元動画から縦長の長尺動画を作る、強調テロップ、効果音（機能追加＋変更） | FR-057〜FR-060、AC-032〜AC-035、既定設定profile 1.2.0（`editing.pauseTightening`・`output.longFormOrientation`・`subtitles.emphasisTelopAmount`・`audio.soundEffectsEnabled`）、§19.14の仮定A-057-01〜A-060-02、AI提案P-057-01・P-060-01、規則`fr057.pause-tightening`・`fr059-emphasis-telop.v1`・`fr060-sound-effect.v1`、`source-only.pause-detection.v1` | FR-054（表FR-054-T1を規則v2に。FR-054.6・AC-029.5のBLOCKEDを解消）、§19.13（回答の記録、競合C-051-01の解消）、表FR-050-T1（変更できる項目に4項目追加）、§16 Must（4項目追加）、新しいProjectの既定設定（profile 1.1.0→1.2.0。作成済みのProjectの設定は変えない） | なし |
| 2026-10-07 | 画面上部のタブと「設定」を、Project一覧（全Projectのジョブ、全体の初期値）とProject（元動画・ジョブの状態の確認・仕上がり・保存、Jobごとの設定）で分け、Projectの「元動画」から複数のJobを作れるようにする（機能追加＋変更）。あわせて、本書が300KBを超えたため、索引（本書）と境界ごとのファイル（`docs/requirements/`）に分割した（章・節の番号と本文は変えない） | FR-061（FR-061.1〜FR-061.9）、AC-036（AC-036.1〜AC-036.8）、§19.15の仮定A-061-01〜A-061-06、AI提案P-061-01・P-061-02、質問票Q-008〜Q-010（未回答。推奨案で仮実装） | FR-043（対象の名前「新しい動画の既定値」→「全体の初期値」、「この動画」→「次に作成するJob」。作成済みのProjectでは「全体の初期値」を表示しない。旧文面は残し、変更の注記を追加）、AC-031.2（4つのタブはProjectを開いているときの確認とする注記）、画面の文言（設定の対象を指す「この動画」→「次に作成するJob」。FR-055の仕上がり画面の案内文、FR-054の自動設定の説明、自動編集の失敗時の対応策の文を含む）、利用者向けの説明書（`docs/ui-workflow-guide.md`、`docs/beginner-guide.md`）、§14（4状態タブはProjectを開いているときの表示とする注記）、§16 Must（1項目追加）、文書の構成（索引と`docs/requirements/common.md`・`editing.md`・`workspace.md`・`platform.md`への分割、要求の索引の追加） | なし |
| 2026-10-07 | 質問票Q-011（利用者の指定B）・Q-012（最良の選択A）の回答の反映: 比べるJobを2つから2〜4つ（最大2×2）に広げ、手動確認AC-038.5を削除（変更） | FR-063.5〜FR-063.7の書き直し（Jobの数2〜4、欄の呼び名「{k}つ目」、設定の比較の列を2〜4列に。旧文面の要旨はFR-063の「Q-011への回答による変更」）、決定記録（§19.16） | FR-063の題名（「2つのJob」→「複数のJob」）、FR-063.1の見方の切り替え「2つのJobを比較」→「Jobを比較」と説明、AC-038.3・AC-038.4、A-063-01・A-063-02・A-063-04、利用者向けの説明書、カタログ、CHANGELOG | AC-038.5（手動確認。IDは再利用しない） |
| 2026-10-07 | 仕上がり画面の構成が分かりにくいため、動画編集ツールの版の比較を参考に、Jobごとの結果を見て、映像と設定項目で2つのJobを比べられるようにする（変更＋機能追加） | FR-063（FR-063.1〜FR-063.8）、AC-038（AC-038.1〜AC-038.5）、§19.16の仮定A-063-01〜A-063-09、AI提案P-063-01・P-063-02、質問票Q-011・Q-012（未回答。推奨案で仮実装） | 仕上がり画面の道具の名前（「成果物一覧」→「Jobの仕上がり」）、FR-055.2の「作成した動画（n本）」の一覧（「動画 n」）をFR-063のJobの一覧に置き換え（承認済みの文面は残し、FR-063に注記）、A-061-01（仕上がり画面の一覧の名前を「Job n」に）、FR-061.8の理由の判定の順（実行中の最初のJobで「実行中」を出す。AC-036.7に合わせた実装の修正）とAC-036.4の試験の応答の修正（§15のAC-036の注記）、利用者向けの説明書（`docs/ui-workflow-guide.md`、`docs/beginner-guide.md`）、§16 Must（1項目追加）、カタログ（FR-055の画面の部品を`JobResultsBrowser.tsx`・`jobSettings.tsx`へ移した） | 画面の部品`VersionHistoryPanel`（FR-063の`JobResultsBrowser`に置き換え。要求IDの削除は無し） |
| 2026-10-07 | `Workspace`フォルダーの初期値を`\workspace`にし、設定画面から変更できるようにする（変更＋機能追加） | FR-062（FR-062.1〜FR-062.7）、AC-037（AC-037.1〜AC-037.5）、§19.17の仮定A-062-01〜A-062-07、AI提案P-062-01・P-062-02、質問票Q-013（回答 2026-10-07: B。FR-062.2に反映）、bridge `settings.workspace.read`・`settings.workspace.update`、設定ファイル`workspace-location.json` | Workspaceフォルダーの場所（従来は`%LOCALAPPDATA%\HubRadio.MovieCreator\Workspace`に固定。新しい環境は`<ドライブ>\workspace`、従来の場所に`hubradio.db`がある環境は次回の起動で`<ドライブ>\workspace`へ移動（同じドライブ内の名前の変更。移せない場合は従来の場所のまま）。FR-062.2）、利用者向けの説明書・開発文書の保存先の記述（`README.md`、`docs/dev/troubleshooting.md`）、設定画面（「Workspaceフォルダー」欄の追加） | なし |
| 2026-10-07 | 質問票Q-008〜Q-010の回答（いずれも最良の選択A）と、AI提案P-061-01・P-061-02の承認の反映: 作成済みのJobの設定を「次に作成するJob」へ写す、全ProjectのJobの表に版の無い最新のJobを含め、表から取消・再試行し、実行中は自動で読み直す（機能追加） | FR-061.10〜FR-061.13、AC-036.9〜AC-036.12、仮定A-061-07・A-061-08 | FR-061の対象外、FR-061.2（作成日時の無い最新の行の並び）・FR-061.3（自動の読み直し）の適用範囲（変更の注記）、A-061-04（置き換え）、§19.15（Q-008〜Q-010を回答済み、P-061-01・P-061-02を採用） | なし |

---
