# 要求版0.2・既定設定・再baseline 敵対的レビュー

- 対象task: `V2-000`
- 対象commit: `bba03e408e79625e1b927db14755203613331b97`から作成した文書専用detached worktree
- レビュー日: 2026-09-02
- 状態: **PASS（文書baselineのみ）**
- production実装: 利用者指示により停止中

> [!IMPORTANT]
> このPASSは、要求、引用、ADR、task再分類、文書整合性に対する判定である。source-only UI、設定profile、render、自動代理評価の実装、実素材成果物、人手評価、外部公開、cross-account隔離、社内法務承認、signed package、release readinessをPASSにしない。

## 1. 対象

- [`docs/requirement-definition.md`](../../requirement-definition.md) 版0.2 Draft
- [`docs/dev/research/default-settings-research.md`](../research/default-settings-research.md)
- [`ADR-0009`](../decisions/ADR-0009-source-only-defaults-and-automated-evaluation.md)と部分置換対象ADR
- [`docs/dev/planning/implementation-plan.md`](../planning/implementation-plan.md) §0
- architecture、security/privacy、quality、accessibility、README、Superseded作業計画
- `Music/`およびproduction codeの非変更境界

レビューは次の3方向を独立に実施した。

1. 要求・UX・事実性
2. 外部出典・数値・適用scope
3. task/DAG・ADR・security・履歴保全

各指摘は実ファイルまたは一次資料で再現し、再現できない提案は採用しなかった。修正後に同じ3方向で再レビューし、Critical/Majorの未解決0件を確認した。

## 2. 採用した指摘と修正

| ID | 重要度 | 指摘 | 対応 |
|---|---|---|---|
| V2R-01 | Major | Git管理外`Music/`がdetached worktreeへ現れず、元worktreeの棚卸しと混同し得る | 棚卸し場所を明記。不在・空・読取不能時はBGMなし+warningとした |
| V2R-02 | Critical | FR-005の自動適用範囲が、8検出カテゴリ全体へ拡張解釈され得る | 自動除外は明示cut、隣接完全一致反復、冒頭末尾無音の3種類**だけ**と明記 |
| V2R-03 | Major | 5つの設定群と各設定の配置が未対応 | 通常表示／1段の詳細設定への割当表を追加 |
| V2R-04 | Major | 既存Web自動試験が新quick start UIを検証したように見え得る | 旧UI証拠であり`V2-005/V2-006/V2-012`は対象外と明記 |
| V2R-05 | Major | 外部配信資料を社内成果物へ使う理由が弱い | 互換profileの出発点という製品判断を明記し、外部service適合を非claim化 |
| V2R-06 | Major | YouTube R02のContent Manager partner scopeが弱く見えた | partner向け推奨の転用と一般account未検証を表・要求・台帳へ明記 |
| V2R-07 | Major | Netflix Japanese、Timing、Generalのduration/gap/in/out表現に競合と助動詞差がある | $5/6$秒を製品正本にし、0.5秒と“20 frames (or 4/5 sec)”を不採用、gap/in/outの原則・許容・例外を分離 |
| V2R-08 | Minor | BBCの行数とline-heightを固定の最終表示値と誤読し得る | 推奨最大・例外、authoring computed value、device表示との分離を明記 |
| V2R-09 | Minor | PLOS研究の2実験を一括して12/16/20 cps・74人と読めた | Exp.1/Exp.2、eye-tracking 60/64、text reductionとの交絡を分離 |
| V2R-10 | Major | 自動字幕をSDH適合済みと誤認し得る | `日本語SDH指向字幕候補`へ改称し、非発話音・話者・理解の網羅性を`NOT EVALUATED`にした |
| V2R-11 | Major | hash順BGM選択が音楽的適合を保証したように見え得る | technical probe、cue長、loop禁止、speech非重複へ限定し、音楽的適合とspeech理解を`NOT EVALUATED`にした |
| V2R-12 | Major | BBC汎用領域をplatform safe zone適合と誤認し得る | platform UI maskを既定未確認、fallback+warning、TikTok/LinkedIn適合非claimとした |
| V2R-13 | Major | FR-025のmetadata捏造防止がV2実装・評価・deliveryに明示対応していない | `V2-004/V2-010/V2-013`へ根拠必須、未生成、根拠なしPASS禁止を追加 |
| V2R-14 | Minor | `DefaultSettingsProfile`のsemantic version運用が未定義 | 初期`1.0.0`、MAJOR/MINOR/PATCH、digest必須、同版異内容禁止を追加 |
| V2R-15 | Major | 84件の旧`変更`taskをどのV2 taskが閉じるか暗黙 | 84件すべてをV2責務へ1回ずつ割り当てる表を追加 |

## 3. 不採用または重要度を変更した指摘

| 指摘 | 判断 | 根拠 |
|---|---|---|
| `Music/`がdetached worktreeにないため棚卸しが虚偽 | 不採用 | Git管理外assetを意図的にcopyしないworktree構成。元worktreeの36 MP3 / 149,081,539 bytesというread-only棚卸しと場所を明記した |
| `S-003`削除に合わせACL/path防御コードもproduction buildから削除する | 不採用 | 利用者が削除したのはcross-account**受入要件**。防御コード削除はscopeを超え、同一利用者の安全性を弱める。既存防御維持を明記した |
| BBC汎用字幕領域はTikTok safe zoneとして矛盾するためCritical | 重要度変更 | 文書は当初からTikTok適合をclaimせず、可変mask未測定時の汎用fallbackだった。誤読防止のwarningを追加したが、事実上のCritical矛盾ではない |
| 既存人手17項目を自動代理評価PASSへ変更する | 不採用 | 未実施人手評価の捏造になる。旧`HUMAN EVALUATION: PENDING`を維持し、新しい機械評価を別recordにする |
| 広告・podcast・broadcast資料を社内動画へ一切使わない | 不採用 | 数値を直接要件へ一般化せず、製品判断・限定転用として使うことは可能。広告/organic、podcast/video、broadcast/onlineのscopeを明記した |

## 4. 引用監査

[`default-settings-research.md`](../research/default-settings-research.md)の参照IDは、利用者決定`U01`、repository証拠`C01〜C05`、外部資料`R01〜R26`の計32件である。

- 利用ID: 32
- 定義ID: 32
- 未定義: 0
- 未使用: 0
- 重複定義: 0

R01〜R26を資料群ごとに再確認した。未解決の引用誤りは0件だった。特に次を分離した。

- R02はYouTube Studio Content Manager partner向けであり、一般account互換を保証しない。
- R09〜R12は広告資料であり、organic動画の普遍的最適値にしない。
- R13はpodcast、R15はbroadcastの推奨であり、社内動画への利用は製品判断である。
- R16の20 dBはLevel AAAかつ録音済みaudio-only speechが対象で、動画AA要件ではない。
- R17〜R19のduration競合と算術不一致を記録し、本製品は有理時間$5/6$秒を選ぶ。
- R20はBBC authoring/online guidanceであり、日本語文字数はNetflix由来の製品profileと組み合わせる。
- R21は日本語を対象にせず、速度とtext reductionを完全分離していない。
- R23〜R26は限定された研究・実務知見で、0 msや250 msを外部推奨値としない。

取得不能または静的取得が不安定だったW3C canonical page、YouTube通常thumbnail pixel、Cutting 2014本文は、公式mirror照合または不採用として台帳§8に残した。

## 5. task集合監査

絶対パスから独立に集合を再計算した。

| 対象 | 結果 |
|---|---:|
| 旧task定義行 | 203 |
| 旧task一意数 | 203 |
| `維持` | 100 |
| `変更` | 84 |
| `将来` | 17 |
| `削除` | 2 |
| 4分類の重複 | 0 |
| 4分類の欠落・余分 | 0 |
| V2 task | `V2-000〜V2-014`の15件、各1回 |
| 旧`変更`からV2責務への割当 | 84件、重複0、欠落0、余分0 |

分類件数は進捗件数ではない。旧`124/203`、`139/203`、`160/203`は履歴snapshotであり、要求版0.2の現在完了数として使用しない。

## 6. 機械検証

レビュー修正後に次を確認した。

- `git diff --check`: PASS
- 変更範囲: `README.md`、`docs/**`、当時の一時作業記録だけ
- production code、schema、test、infra、package、benchmark、sample: 変更0
- `Music/`: Git status上の変更0、内容変更・stage 0
- `system-overview.svg`: XML parse PASS
- 変更・新規Markdown: local link/anchorの欠落0
- 要求ID: `FR-001〜034`、`NFR-001〜008`、`BR-001〜005`、`AC-001〜006`が各1回
- 変更・新規file: NUL 0、0 byte 0
- VS Code diagnostics: 確認対象にerror 0

### 6.1 canonical Release全回帰

`pwsh.exe -NoLogo -NoProfile -File .\test.ps1 -Configuration Release`をPowerShell 7.6.5で実行し、全runnerが成功した。

| Runner | 成功 | failure | environment-gated inconclusive / skip |
|---|---:|---:|---:|
| Architecture .NET | 46 | 0 | 0 |
| Core .NET | 637 | 0 | 1 |
| Infrastructure .NET | 679 | 0 | 28 |
| App .NET | 959 | 0 | 3 |
| Web Vitest | 442 | 0 | 0 |
| Accessibility Node contract | 4 | 0 | 0 |
| Web asset negative/control | 6 | 0 | 0 |
| Python worker | 104 | 0 | 0 |

.NET restore/build、Web typecheck/build、production dependency/asset検証、Python hash-locked dependency整合、Python syntax検証も成功した。環境依存32件はsymbolic link、pinned FFmpeg不在、非Windows経路、cross-account ACL等であり、PASSへ数えていない。Viteは612.03 kBのchunk size warningを出したがbuild/test failureではなく、要求版0.2のUI実装時に実測して扱う。

## 7. 残余リスク

- 自動代理評価は、人の可読性、理解、自然さ、音質、感情、法的適合を保証しない。
- SDH指向字幕候補は、必要な非発話音と話者情報の網羅性を保証しない。
- BGMの曲調、ブランド適合、speechの知覚的理解は未評価である。
- TikTok等の実platform UI maskは未測定で、BBC汎用字幕領域はplatform適合証拠ではない。
- cross-account隔離は受入済みとしない。既存ACL/path防御は維持する。
- clean Windows、signed package、完全SBOM/notice/CVE/codec条件、release readinessは未完了である。
- production品質・性能閾値と実素材成果物は未測定である。

## 8. 判定

Critical/Majorの未解決指摘は0件である。`V2-000`の文書baselineは受入可能と判定する。

`V2-001`以降はproduction実装であり、利用者が明示的に実装再開を指示するまで開始しない。各task完了後は[`implementation-plan.md` §0.6](../planning/implementation-plan.md#06-全task共通の実行契約)に従い、focused test、回帰、独立した敵対的レビュー、修正後の再試験・再レビューを必須とする。
