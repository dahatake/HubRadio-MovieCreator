# U-015 / Q-009 アクセシビリティ評価記録（旧受入と版0.2への対応）

- 評価日: 2026-09-02
- 対象: `HubRadio.MovieCreator` の主要 Web UI flow
- 基準 commit: `89e67fc14bbf058a6597e32783461f701dd98d30`
- 要求: `NFR-005`、実装計画 `U-015` / `Q-009`

> **HUMAN EVALUATION: PENDING**
>
> **U-015 acceptance: PENDING / NOT ACCEPTED**
>
> 本記録の自動試験とブラウザー自動操作だけでは U-015 / Q-009 を受入済みにしない。代表素材、実 WebView2 host、物理キーボード、実表示環境を使った人手確認が完了するまで release gate を開けない。

> [!IMPORTANT]
> 上記3行と人手17項目の`PENDING`は、要求版0.1に対する2026-09-02時点の歴史的事実であり、`PASS`へ変更しない。一方、要求版0.2と[ADR-0009](../decisions/ADR-0009-source-only-defaults-and-automated-evaluation.md)は、人手確認を社内限定quick startの必須release gateにせず、自動判定可能項目を`Automated proxy`として受け入れる。これは「人手評価が完了した」という意味ではない。版0.2のquick startと設定UIは本記録後に実装されたため、本書の既存自動PASSだけでそれらや`V2-010/V2-014`を完了扱いにしない。
>
> 本書の自動結果は旧要求版0.1に対する既存Web UIの証拠である。後から実装された版0.2のquick start（`V2-005`）、設定画面（`V2-006`）と、未実装の成果物・任意確認画面（`V2-012`）を対象にしていない。別の版付き評価recordを作るまで、既存442 test等を新UIの合格証拠へ転用しない。

## 判定

| 評価面 | 状態 | 根拠 |
|---|---|---|
| 自動 keyboard interaction | PASS | 主要 flow の focused control に対する Enter、Space、矢印、Home、End、Delete と form 入力を component test で実行 |
| focus の authored CSS contract | PASS | 共通 `:focus-visible` が 3 px outline / 3 px offset を宣言することを専用試験で確認 |
| light / dark token contrast | PASS | 通常文字と filled control の対象 token pair が 4.5:1 以上であることを専用試験で確認 |
| theme-aware description text | PASS | `.description` が固定色でなく `--cp-text-muted` を使うことを専用試験で確認 |
| 非色依存の機械確認 | PASS | visible text、`aria-current`、`aria-selected`、`aria-pressed`、role/status と形状・境界を併用する状態を試験 |
| subtitle control の自動操作 | PASS | 表示切替、表示 profile / variant、Cue 編集と revision 作成を keyboard interaction で確認 |
| mute control の自動操作 | PASS | visible label、accessible name、`aria-pressed`、`video.muted` の同期を確認 |
| 人手による keyboard / focus / color / subtitle / SDH / mute 理解 | **PENDING** | 未実施 |
| U-015 総合受入 | **PENDING / NOT ACCEPTED** | 人手評価が必須 |

### 要求版0.2での読み方

| 証拠 | 版0.2での状態 | 制限 |
|---|---|---|
| 既存keyboard component test | `Automated proxy evidence: reusable` | 新quick start・settings・results UIは未対象 |
| authored `:focus-visible` contract | `Automated proxy evidence: reusable` | 実WebView2/DPIでの知覚的視認性は未評価 |
| contrast token計算 | `Automated proxy evidence: reusable` | 新UIの全stateは未対象 |
| 非色依存semantic contract | `Automated proxy evidence: reusable` | 人の状態理解は未評価 |
| 字幕・mute controlの自動操作 | `Automated proxy evidence: reusable` | 新しい字幕profile、実動画の理解・可読性は未評価 |
| 人手17項目 | `HUMAN EVALUATION: PENDING` | 任意評価として残し、自動PASSへ変換しない |

版0.2の正式な自動代理評価は`V2-010`で規則版、実行環境、入力・出力digest、`PASS/WARNING/FAIL/NOT EVALUATED`を新しいevidenceへ記録する。本書の旧総合受入欄は変更しない。

## 自動試験結果

2026-09-02 に Windows 11 Enterprise Insider Preview 10.0.29648（64-bit）、PowerShell 7.6.5、Node.js 24.19.0、npm 11.17.0、.NET SDK 10.0.400、Python 3.12.10 で実行した。

- Web typecheck: PASS
- Web production build: PASS（production runtime closure は React、React DOM、Scheduler の3 package）
- Vitest: **56 files / 442 tests PASS**
- 専用 accessibility contract: **4 / 4 PASS**
  - light / dark の通常文字 token pair: 4.5:1 以上
  - light / dark の filled control text: 4.5:1 以上
  - 共通 keyboard focus declaration: 3 px outline / 3 px offset
  - `.description`: theme-aware muted text token
- production asset / dependency checks: **6 / 6 PASS**
- `npm audit`: info / low / moderate / high / critical のすべて **0**
- .NET automated tests:
  - Architecture: 46 PASS
  - Core: 637 PASS / 1 environment-gated inconclusive
  - Infrastructure: 688 PASS / 19 environment-gated inconclusive
  - App: 957 PASS / 3 environment-gated inconclusive
- Python worker: **104 PASS**

環境依存で inconclusive となった .NET 試験は symbolic link、別 account ACL、非 Windows path などであり、本記録では PASS に読み替えない。

### Keyboard interaction の対象

- 案件作成 wizard: Home / End による step 移動、Enter による確定
- ジョブ進捗: ID 入力、Enter による照会
- 解析結果: 理由入力、Enter による候補採用
- preview mode: Space による字幕表示、縦横、音声のみ等の切替
- player: Enter / Space による mute 切替
- timeline: accessible element 選択と Delete
- layout / subtitle: roving focus、Tab 順、本文・理由入力、Enter による Cue revision 作成
- review / delivery: Home / End による tab 移動と selected panel の同期
- thumbnail: Enter による evidence 読込と生成開始

## ローカルブラウザー自動確認

ビルド済み `dist` を `127.0.0.1` のみで配信し、VS Code integrated browser の Chromium 148 / Electron 42.8.1（viewport 1440 × 1000、automation DPR 1.0）で確認した。偽 WebView bridge は ping と閉じた `player.read` fixture だけに応答し、`player-media.appassets.example` への媒体要求は遮断した。

観測結果:

- production CSS が外部 stylesheet として読み込まれた。
- skip link は focus 時に画面内へ現れ、solid outline を表示した。
- `#project-content` は `tabIndex=-1` を持ち、fragment activation 時の focus target になった。
- focused subtitle checkbox の状態が `true` から `false` へ変わり、`data-subtitles-visible` と一致した。
- 音声のみ切替後、映像は `aria-hidden=true` となり、「音声のみプレビュー」「映像を隠しています」という非色依存の説明が表示された。
- mute は `ミュート` / `ミュート解除`、accessible name、`aria-pressed=false/true`、`video.muted=false/true` が双方向に同期した。
- 外部媒体を遮断した状態でも player evidence と操作状態が残り、媒体未読込を visible status で示した。

### 自動ブラウザー確認の制限

- integrated browser page は自動化中に `document.visibilityState=hidden` だった。物理的な Tab / Shift+Tab とブラウザー既定の Enter / Space activation の体感・順序は、この結果から受入済みとしない。
- focus rule の成果物宣言は 3 px だが、embedded browser の計算値は host scaling の影響を受けた。実 WebView2 と対象 DPI での見た目は人手確認へ残す。
- 偽 bridge と固定 fixture を使用したため、native host、実媒体、字幕同期、実音声、SDH 内容は評価していない。
- この確認は screen reader、Windows High Contrast / forced colors、色覚多様性、人の理解度を評価していない。

## 人手評価チェックリスト

以下はすべて未実施である。担当者、OS build、WebView2 runtime、display、解像度、DPI scaling、theme、代表素材、実施日時、観測結果、修正後再確認を追記すること。

| 人手確認項目 | 状態 | 完了条件 |
|---|---|---|
| 全主要 flow の Tab / Shift+Tab 順序 | **PENDING** | 表示順と操作順が一致し、focus loss / trap / 意図しない飛越がない |
| skip link の物理 keyboard 操作 | **PENDING** | 起動直後の Tab で表示され、Enter 後に主要 content へ focus が移る |
| Enter / Space / 矢印 / Home / End / Delete | **PENDING** | native WebView2 上で各 control の説明どおり動き、誤操作がない |
| focus indicator の視認性 | **PENDING** | light / dark、100% / 125% / 150% 以上の対象 DPI、scroll / overlay 状態で見失わない |
| focus indicator の非遮蔽 | **PENDING** | viewport 端、card、dialog、sticky / clipped 領域で indicator が隠れない |
| light / dark の文字・control contrast | **PENDING** | 実描画、hover、disabled、error、selected、focus を人が確認する |
| High Contrast / forced colors | **PENDING** | Windows の対象 mode で文字、境界、focus、状態が識別できる |
| 色だけに依存しない状態理解 | **PENDING** | warning / error / pending / selected / approved / muted を文言・形状・semantic から区別できる |
| 字幕表示切替の理解 | **PENDING** | 字幕あり / なし、burned-in / subtitle-free、全文 / 選択、句読点表示を誤認しない |
| 字幕の可読性と同期 | **PENDING** | 代表動画で文字量、背景、配置、時刻同期、縦横表示を確認する |
| 字幕 Cue 編集 flow | **PENDING** | keyboard のみで Cue 選択、本文・時刻・style・配置、理由、確定、元時刻移動ができる |
| SDH / 非発話音 cue の内容理解 | **PENDING** | 代表素材で話者識別、音楽・効果音・重要な非発話音の必要情報が理解できるか判断する |
| mute 状態の理解 | **PENDING** | 見た目と読み上げの双方で現在状態と押下後の結果を誤認しない |
| mute / 無音での主要内容理解 | **PENDING** | 代表素材を mute しても字幕・SDH・画面説明から主要内容を理解できる |
| 音声のみ mode の理解 | **PENDING** | 映像が隠れていること、再生位置・波形・mute 操作が継続することを理解できる |
| zoom / reflow / 320 px 幅 | **PENDING** | 主要操作と情報が欠落・重複・重なりなく利用できる |
| screen reader の名称・状態・順序 | **PENDING** | 対象 reader で control 名、role、状態、status 更新、エラーが理解できる |

## 旧要求版0.1の残存gate

1. 上記の全 `PENDING` 項目を、対象となる実 Windows / WebView2 / DPI と代表動画で人が実施する。
2. failure があれば修正し、自動試験と該当人手 flow を再実行する。
3. 評価者と証跡を記録し、`HUMAN EVALUATION` と `U-015 acceptance` を明示的な承認判断で更新する。
4. Q-009 は本記録の人手確認が完了するまで未完了とする。

要求版0.1に対しては **U-015 は受入未完了であり、Q-009 の人手gateも未完了**である。この未実施事実は維持する。要求版0.2では`U-015/Q-009`を変更taskとして扱い、人手gateを閉じたと偽らず、自動代理評価と任意human observationへ分離して再受入する。
