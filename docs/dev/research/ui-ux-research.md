# HubRadio MovieCreator UI/UX研究記録

- 対象タスク: **UX-002のみ**。
- 作成日・本タスクの資料再取得日: **2026-09-07**。
- 入力: 2026-09-07承認済みのUI/UX改善第2版方針 §12（[ADR-0011](../decisions/ADR-0011-compact-workflow-and-settings-file.md)へ恒久化）、[要求定義書](../../requirement-definition.md) 版0.2 Draft。
- 承認の扱い: **2026-09-07の利用者指示でA01〜A20の既定案と実装開始が承認された**。計画に残る承認待ちの記述はその作成時点の状態であり、本書は最新の指示を前提にする。ただし本タスクの変更範囲はこの研究文書だけである。
- 状態: 主担当による文書検証・独立レビュー待ち。UI実装、画面実測、研究実験の再現、製品受入を報告する文書ではない。

## 1. 結論と対象範囲

**戻れる状態タブ＋独立した設定画面＋実結果を中心にした表示**を、要求と公開知見を組み合わせた本製品の設計判断として支持する。重要な操作を初期表示し、必要な詳細へ明確に移動できること、修正・復帰を可能にすること、能力・限界・操作の結果を伝えることが共通の根拠である。これらの資料から、特定のタブ数、画面寸法、操作時間の改善率が実証されたとは結論しない。

対象はWindows上の単一利用者向けsource-only quick startと、その設定・進捗・成果物・任意編集の情報設計である。要求の次の境界を維持する。

- 元動画1本と開始確認1回を維持する。4タブを4段階の必須入力や承認へ変更しない（§6.1、§7、NFR-001、AC-001）。
- 設定の現在値・初期値・用途・根拠・復元操作を確認可能にし、既存Compositionへ遡及適用しない（§6.4、FR-033、AC-006）。
- 任意のpreview・修正・比較・履歴への移動を提供し、自動代理評価と人の確認を分離する（FR-026〜030、FR-034、NFR-008）。
- 社内限定・ローカル出力・非破壊編集を維持する。研究資料を理由にcloud、agent、行動学習、外部投稿、新renderer等を追加しない。
- 動画・字幕・音声の既定値は[既定設定調査](default-settings-research.md)の担当範囲であり、本書では変更しない。

## 2. 資料の読み方と日付の規則

計画§12の出典ID **S01〜S11を維持**する。S08、S10、S11は複数文書の集合であり、「11件の独立した実験」という意味ではない。

| 区分 | 本書で扱う証拠 | 扱わないもの |
|---|---|---|
| 査読会議論文の書誌・要旨 | S01・S03の著者、題名、掲載会議・月、要旨の範囲 | 未抽出の論文本文、実験表、効果量、再現結果 |
| 著者による公式解説 | S02のHTML本文と指針一覧 | S01と独立した追試、全指針を満たせば成功するという保証 |
| 実務知見・著者公開原則 | S04〜S07の発行元・著者が公開した本文 | 本製品・日本語利用者に対する対照実験や普遍的最適値 |
| OSの公式設計指針 | S08のWindows向け本文 | React/WebView2への直接の実装命令、全端末対応の証明 |
| アクセシビリティの公式原文 | S09のAPGパターン解説、S10のWCAG該当基準ソース | APGと規範仕様の同一視、製品のWCAG適合認証 |
| 配布元の技術・許諾資料 | S11のREADMEとMIT License | ユーザビリティ効果の実証、動画等の権利や一般提供の承認 |
| 要求・利用者決定・製品判断 | 本書§4と計画のA01〜A20 | 外部研究が同じ構成・数値を直接推奨したという主張 |

日付と確認範囲は次のように区別する。

1. **前回閲覧日**は計画§12が記録した2026-09-07。本タスクでも同日の公開HTML／公式リポジトリ本文を取得し、以下で採用する箇所を照合した。検索結果だけを本文の代用にはしない。
2. **発行日・掲載月・最終review日・書籍の版情報**は、それぞれの表示どおりに記す。月しか分からない資料に日を補わない。
3. **`ms.date`と`updated_at`は別々のmetadata**として記録する。初出日、論文の発行日、localeを越えた一意の更新日とはみなさない。
4. 日付表示を確認できないものは**未確認**とする。閲覧日、URL中の年月、著作権年を発行・更新日に流用しない。
5. W3CとFluentの`main`参照は可変であり、本タスクではcommit・原本bytesのhashを固定していない。PDFリンクの所在確認は本文抽出ではない。

## 3. 出典台帳と採用範囲

### S01 — Guidelines for Human-AI Interaction

- **著者・書誌:** Saleema Amershiほか、CHI 2019、ACM、**2019年5月**。研究機関の[書誌・要旨ページ][S01]で確認。
- **取得範囲:** HTMLの書誌・要旨。ページ掲載の[著者公開manuscript PDF][S01-PDF]は**本文未抽出**。PDFの保存・図表確認・実験再現は行っていない。
- **採用:** 人とAIの相互作用に関する設計指針が、査読会議論文として提示されたという位置付け。具体的な指針の説明には本文を確認できたS02を使う。
- **限界:** 論文全文を読了したとは記録しない。要旨に研究の評価が記載されていても、本製品での誤操作率低下、時間短縮、理解度向上を裏付ける実測として使わない。

### S02 — Guidelines for human-AI interaction design

- **著者・発行日:** Saleema Amershi、Mihaela Vorvoreanu、Eric Horvitz。Microsoft Research Blog、**2019-02-01**（Published表示）。[著者公式HTML本文][S02]を確認。
- **確認箇所:** 指針を整理した背景、GUI製品で開発・検討したという適用範囲、単純なチェックリストではなくトレードオフの検討に用いるという留保、末尾の指針一覧。

以下は原文の短い要約であり、本製品への具体化は§4に分ける。

| 原文の指針 | 確認した内容の要約 |
|---|---|
| G1・G2 | できることと、その能力の限界・誤り得ることを利用者に伝える |
| G4 | 現在の作業や状況に関係する情報を表示する |
| G9 | 誤りを編集・修正・回復しやすくする |
| G11 | システムの振る舞いの理由へアクセスできるようにする |
| G16 | 利用者の操作が今後の振る舞いに及ぼす影響を、更新または説明で伝える |
| G17 | システムの監視対象や振る舞いを全体として調整できる手段を提供する |

- **限界:** S01の著者解説であり、別の独立実験ではない。G16は重い再生成の即時完了を要求しない。G17から`setting.txt`形式や保存先は導けない。行動学習等の指針も含まれるが、それを本製品へ新機能として追加する根拠にはしない。

### S03 — Principles of Mixed-Initiative User Interfaces

- **著者・書誌:** Eric Horvitz、Proceedings of CHI '99、ACM Press、**1999年5月**。[研究機関の書誌・要旨][S03]を確認。
- **取得範囲:** HTML書誌・要旨。[掲載manuscript PDF][S03-PDF]は**本文未抽出**。PDF URLの`2016/11`は論文の発行日ではない。
- **採用:** 自動化と利用者の直接操作を組み合わせるという背景。要旨の例は日程・会議管理のLookoutである。
- **限界:** 動画編集UIの比較実験ではない。介入タイミング、推論方式、実験条件等の詳細を、未抽出本文から確認済みとして引用しない。

### S04 — Progressive Disclosure

- **著者・発行日:** Jakob Nielsen、Nielsen Norman Group、**2006-12-03**。[一次記事本文][S04]の初期／二次表示、設計条件、staged disclosureとの比較を確認。
- **採用:** 頻用・重要機能を初期表示し、高度・低頻度機能は明確な入口の先へ移す。初期表示と詳細の分け方、詳細への入口の発見しやすさが重要である。
- **限定:** 必須の線形手順であるstaged disclosureは、任意の詳細表示と異なる。独立した工程には有用だが、相互依存して往復する作業を分断しすぎると移動負担が増えるという本文の区別を採る。
- **使わない主張:** すべてのwizardが悪い、1画面なら最適、タブは4個が科学的上限、といった一般化。記事の他分野の例をHubRadioの実測に換算しない。

### S05 — The Eight Golden Rules of Interface Design

- **著者・版情報:** Ben Shneidermanの[大学サイト上の公開本文][S05]。ページはShneiderman、Plaisant、Cohen、Jacobs、Elmqvistによる『Designing the User Interface』**第6版、Pearson、2016年5月、§3.3.4**に基づくと記載。
- **日付の範囲:** 2016年5月は参照書籍の版情報であり、Webページの発行・最終更新日は未確認。書籍全体は読了していない。
- **採用:** 用語・操作の一貫性、操作に対応するfeedback、誤りからの回復、逆操作、利用者による制御、画面をまたぐ記憶負荷の削減。
- **限界:** 本文自身が分野ごとの検証・調整を必要としている。本文中の「7±2」をタブ数・設定数・一覧page件数の制限に転用しない。既存undoや版管理が正しく動くことを、この原則だけで保証しない。

### S06 — Icon Usability

- **著者・発行日:** Aurora Harley、Nielsen Norman Group、**2014-07-27**。[一次記事本文][S06]のアイコンの曖昧さ、文字ラベル、相対的な目立ちやすさを確認。
- **採用:** とりわけnavigationでは、意味を説明する文字ラベルを常時表示する。hoverしなければ意味が分からない構成を避ける。
- **限界:** スマートフォンで目立つアイコンが、要素の多いデスクトップでも同じように発見されるとは限らない。日本語ラベルの文言やFluentの個別アイコンの理解度は未評価。アイコンだけのUI、tooltipだけの説明を「モダンだから分かりやすい」としない。

### S07 — Interaction Cost

- **著者・日付:** Raluca Budiu、Nielsen Norman Group、初出**2013-08-31**、**Last reviewed 2024-10-14**。[一次記事本文][S07]の定義と構成要素を確認。review日を初出日と混同しない。
- **採用:** 目的達成の負担には、読む、探す、理解する、スクロール、クリック・タッチ、入力、待機、注意の切替、情報の記憶が含まれる。各要素の重みは利用者や機器によって異なる。
- **限界:** スクロール数やクリック数だけでは総負担を測れない。本製品で測っていない認知負荷を点数化しない。無スクロール化によって文字が小さくなる、必要情報が隠れる、タブ往復が増える場合も検討対象とする。

### S08 — Windowsのnavigation／responsive design指針

- **発行元・原典:** Microsoft Learn、[Navigation basics for Windows apps][S08-navigation]（公式検索見出しは「Navigation design basics for Windows apps」）、[Responsive design techniques][S08-responsive]。
- **取得範囲:** navigationの原則・構造・top navigation・list/details、responsiveのreposition・resize・reflow・show/hide・re-architect・adaptive layout。localeなしURLは公式日本語頁へ解決されたため、**英語原文も取得して照合**した。
- **日付:** 次は本タスクで取得した**en-usのmetadata**。計画§12の前回`ms.date`観測値と一致するが、記事の初出日ではない。

| 文書 | `ms.date`の日付部 | `updated_at`（en-us、UTC） | 初出日 |
|---|---|---|---|
| Navigation | 2025-04-03 | 2026-06-27T05:03:00Z | 未確認 |
| Responsive design | 2026-07-22 | 2026-07-17T02:50:00Z | 未確認 |

日本語頁の`updated_at`は順に2026-07-05T11:59:00Z、2026-07-17T03:49:00Zで、英語頁とは異なる。Responsiveの`ms.date`と`updated_at`の前後関係も推測補正しない。metadata、翻訳頁、発行日を別々に扱う。

- **採用:** 深すぎる階層や、関連内容へ移るための不要な上位階層への往復を避ける。広いwindowでは再配置や列・list/detail表示で領域を利用する。
- **限界:** Windows/XAML向け公式指針の情報設計上の考え方を参考にする。`Frame`、`NavigationView`、開閉可能な`TabView`の導入命令ではない。XAMLのeffective pixelsを本製品のCSS viewport測定と混同しない。1280×720や固定4タブはこの資料の規定ではない。

### S09 — Tabs Pattern（WAI-ARIA APG）

- **発行元・原典:** W3C、[公式aria-practicesリポジトリのTabs Pattern][S09]。`main`のHTML原文を確認。個別ページの発行・更新日と固定commitは未確認。
- **取得経路:** 計画§12では公開サイトが403だったため公式リポジトリ原文を使用している。本タスクでもリポジトリ原文を再取得した。公開サイトの現在の到達性を再検証したという意味ではない。
- **採用:** `tablist`・`tab`・`tabpanel`、対応する`aria-controls`・`aria-labelledby`、選択中の`aria-selected`を整合させる。Tabで選択中タブへ入り、次のTabでpanelまたはその適切な内容へ進む。
- **keyboardの区別:** 横タブでは左右矢印でfocusを移動し、手動選択方式ではEnter／Spaceで表示を選ぶ。上下矢印は横タブが奪わない。**Home／EndはAPGでは任意**だが、第2版§5は本製品で採用する。
- **限界:** APGは実装支援のパターン解説であり、規範仕様そのものや適合認証ではない。遅延なくpanelを表示できる場合の自動選択も説明されている。手動選択は本製品の選択であり、W3Cが一律必須としているとは書かない。4タブ、jobの寿命、undo保持、動画pauseは別の製品契約である。

### S10 — WCAGのreflow・拡大・対象サイズ・focus

- **発行元・取得範囲:** W3C公式`wcag`リポジトリの下表4基準の原文。個別ソースの発行・更新日、固定commitは未確認。`main`上のソースを確認したことを、固定された公開Recommendation版との同一性確認や全基準の適合判定とは呼ばない。

| 原典・基準 | Level | 本文で確認した条件と例外 |
|---|---|---|
| [Reflow][S10-reflow]、SC 1.4.10 | AA | 情報・機能を失わず、縦スクロール内容では幅320 CSS px相当、横スクロール内容では高さ256 CSS px相当で二方向scrollを要求しない。利用・意味のため二次元配置を必要とする部分は例外 |
| [Resize Text][S10-resize]、SC 1.4.4 | AA | 支援技術なしで200%まで文字を拡大しても内容・機能を失わない。captionsとimages of textはこの基準の例外 |
| [Target Size (Minimum)][S10-target]、SC 2.5.8 | AA | pointer入力対象は原則24×24 CSS px以上。間隔、同じpageの適合する代替control、文中等、未変更のuser-agent control、必要不可欠／法的に必要な表示に例外がある |
| [Focus Not Obscured (Minimum)][S10-focus]、SC 2.4.11 | AA | keyboard focusを得たcomponentが、制作者のcontentによって**完全には隠れない**。利用者が再配置できるUIは初期位置を扱う等の注記がある |

- **reflowの解釈:** 320 CSS pxは原文注記で1280 CSS px幅の400% zoom相当と説明される。200%文字拡大と同一条件ではない。二次元配置の例には動画等があるが、動画編集アプリの設定や全navigationを丸ごと例外にしない。一方向scrollを許容するので「全条件でscroll禁止」の根拠にはならない。
- **対象サイズの解釈:** 間隔例外は、小さい各対象のbounding box中心に直径24 CSS pxの円を置き、他の対象または他の小さい対象の円と交差しないという条件である。単に余白があればよいわけではない。アイコンの描画寸法ではなく操作対象の領域を扱う。
- **focusの解釈:** 「完全に隠れない」というMinimumを「一部も隠れてはいけない」と引用し直さない。本製品で固定header/footerによる遮蔽を避ける設計は、最小基準の引用と分けて検証する。
- **限界:** この4項目はWCAG全体の網羅審査ではない。既存のcontrast、keyboard、字幕等の要求は要求定義NFR-005と既定設定調査§4.8を維持する。機械検査を人の可読性・理解の実施証拠へ換算しない。

### S11 — Fluent UI System IconsとMIT License

- **発行元・原典:** Microsoft、[Fluent UI System Icons README][S11-readme]、[Fluent UI System Icons (svg) README][S11-svg]、[MIT License][S11-license]。公式リポジトリ`main`の本文を確認。
- **日付・版:** 個別文書の発行・更新日は未確認。LICENSEの**Copyright (c) 2020 Microsoft Corporation**は著作権表記であり、READMEの発行日や採用asset版ではない。本タスクでは採用commit・asset hashを固定していない。
- **採用:** SVGの提供と`regular`／`filled`の単色styleを確認。必要なsubsetを既存themeと組み合わせる候補にする。
- **許諾条件:** MITの利用・変更・配布許諾には、コピーまたは実質的部分へ著作権表示と許諾表示を含める条件があり、無保証条項もある。採用する版・hash・noticeと実配布payloadの照合は計画UX-015／043の責務で、本書では実施していない。
- **限界:** READMEの導入例を理由に新しいNPM依存、icon font、CDNを追加しない。JSX subsetと現行themeの維持は製品判断である。MIT確認は動画・音源等の利用権や一般提供承認とは別であり、認識しやすさはS06と実画面で別に検討する。

## 4. 知見を設計へ適用する際の区分

以下の具体案は**利用者が承認した第2版の既定案、または同版の設計初期値**である。「研究で実証された事実」「実装済み」「実寸検証済み」を意味しない。A01〜A20の全契約は第2版§7を参照し、本書で変更しない。

| 製品判断・対応箇所 | 参考知見と要求 | 適用範囲・外挿しない内容 |
|---|---|---|
| **元動画／自動編集／仕上がり／保存の4状態タブ**（A03・A04） | S04・S05・S08・S09、要求§7、FR-026/030 | 戻れる表示先として採用。4という数と日本語ラベルは製品選択。タブ移動だけでjob取消、reset、再実行を起こさず、必須wizardへ戻さない |
| **独立設定＋5分類＋一覧／選択項目**（A05・A11・A15・A20） | S02 G4/G17、S04・S08、要求§6.4/14.4、FR-033 | 既存全設定へ到達可能にし、全体／案件scopeと未保存値を区別。個別保存で他draftを失わない。旧wizardは互換経路へ分離。分類数・page件数は論文の最適数ではない |
| **結果と変更の可視化**（A12） | S02 G1/G2/G9/G11/G16、S05、FR-026〜028/031/034 | 予定／生成中／実生成／変更後未生成／不成立と、実保存・評価・人の操作を分ける。同時刻の比較を目指すが、未生成画像や未接続機能を実結果として示さない |
| **1280×720 CSS px・Web zoom 100%を基準**（A01・A02） | S07・S08・S10、NFR-001/005 | 通常4画面の主要操作・状態・成果物要約、設定1pageをpage scrollなしで扱うという製品受入条件。最小動作環境の実測値でも動画出力解像度でもない。長文・timeline・拡大は例外を明示する |
| **通常control約40px、touch向け主要操作約44px**（第2版§5） | S06・S07・S10、NFR-005 | 画面密度と操作性のための設計初期値。本文16px相当・補助14px相当も製品案。40/44は論文の最適値やWCAG AAの必須値ではない。実操作領域・間隔・拡大時の到達性は別に測る |
| **現行theme＋必要なFluent単色アイコン＋日本語ラベル**（A13・A14） | S05・S06・S11、NFR-005 | Reactと現行light/darkを継承。色だけに状態を依存させない。スマートフォン風の外観をmobile OS対応やUI kit全面置換へ広げない |
| **SQLite正本＋`setting.txt`の明示保存／読込**（A06〜A09・A19） | S05の制御・回復、FR-033、NFR-003/004、第2版§6 | Workspace外の固定保存先、strict digest検証、raw 4 MiB／canonical 1 MiBは工学的な製品契約であり、UX研究が指定した形式・上限ではない。自動同期・digest緩和を追加しない |
| **明示再生成、追加の必須確認なし、実backend依存、任意の利用者観察**（A10・A16〜A18） | S02 G9/G16、S03・S05、FR-029/030/034、AC-001/006 | 設定保存と子版生成を区別。未接続は未接続として表示。人の理解確認を自動試験で代用せず、未実施なら未評価のまま残すが、必須の生成gateにしない |

### 4.1 代替案との比較

これは実験結果のランキングではなく、要求との整合性を含む選択理由である。

| 代替案 | 検討上の問題・トレードオフ |
|---|---|
| 長いページを余白削減だけで圧縮 | 不要情報を探し続ける負担が残り得る。文字・操作領域の縮小や見切れ隠蔽で無scrollを達成しない（S07・S10） |
| すべてを必須の順次wizardへ分割 | 元動画以外の必須入力や確認を増やすならAC-001と合わない。編集と比較の往復を増やす恐れもある。ただし独立工程一般でのwizardを否定しない（S04） |
| アイコンだけのnavigation | 意味の推測やhoverによる確認を増やす。可視ラベルを残す判断を採る（S06） |
| 状態タブ＋独立設定 | 現在の作業に集中しやすいという設計仮説を採る一方、隠れた詳細の発見性、tab往復、focus・未保存値・再生位置の保持が新しい確認点になる（S02・S04・S05・S08・S09） |

必要情報や失敗理由まで隠してはならない。設定・詳細への入口を常時発見可能にし、通常画面へ戻ったときの文脈を保持する。具体的な効果は未測定である。

## 5. 現行状態を誤って引用しないための注意

本タスクではアプリコードの再監査や実行をしていない。以下は**第2版§3の静的確認記録の引継ぎ**であり、現在のUI動作の新たな実証ではない。

- **K02:** `sourceOnly !== null`は既存quick start、`null`は旧wizard。新規経路を「10項目必須wizard」と記載しない。要求定義§2.2や2026-09-02の既定設定調査§3にある旧課題を、2026-09-07の現行実装へ無条件に転記しない。
- **K07・K08:** 設定UIは既に5分類、現在値・initial・profile既定値、個別保存とresetを持ち、同梱profileは123項目と記録されている。本研究の目的は既存の表示・到達性の改善であって、「設定画面が存在しない」という欠陥の新設ではない。
- **K10:** snapshotには全キー・型・範囲の検証があり、strict parserにはprofile・content digest等の検査がある。「設定値の検証なし」とは記載せず、UI変更のために既存検証を弱めない。
- 設定を保存できることと、その値を使用する処理が接続済みであることは別。予定成果物数を実在件数、DB保存を動画更新、render成功をローカル昇格完了へ読み替えない（第2版K04・K05・K15、§4.4）。
- Workspace外への設定exportは新しい保存先設計であり、既存backup/restoreによる実データ消失を実証したという主張ではない（第2版K13・K14、§6.2）。

## 6. 限界と主担当への読取専用検証案

### 6.1 未評価・未実施

- S01・S03の**manuscript PDF本文は未抽出**。本文中の表・詳細な実験条件・効果量は本書の証拠に含めない。
- 調査は計画§12の指定資料を中心とした限定的なdesk researchであり、網羅的なsystematic reviewではない。一次HTMLの和文要約を用い、原文・図版を全文転載していない。
- 日付未確認の文書、可変`main`参照、未固定のasset版が残る。閲覧日を記録しても原本bytesの再現性は保証されない。
- 本製品の実viewport、OS scale、keyboard/focus、拡大、contrast、操作時間、誤操作、理解度は本タスクでは測っていない。jsdomや文書の整合確認だけでも実geometry・人の理解の証拠にはならない。
- アプリ試験、配布payload検査、利用者walkthrough、独立レビューは本タスクで実施していない。G-UI、G-LIVE、WCAG適合、製品全体の合格を宣言しない。

### 6.2 読取専用での確認提案（実施結果ではない）

1. このファイルをMarkdown previewで開き、見出し、表、相対リンク、S01〜S11と各原典への対応を確認する。
2. 原典の該当本文と照合し、タイトル、著者、発行／review／metadata／取得日の区分、PDF未抽出、可変参照の限界を確認する。
3. 4タブ、1280×720 CSS px、40/44px、file形式・上限が**製品判断**として書かれ、研究由来の必須値や実測値へ置き換わっていないことを確認する。
4. 要求定義§6.4/7/14、FR-026〜034、NFR-001/005/008、AC-001/006と比較し、開始確認1回・非破壊・任意修正・評価区分を壊していないことを確認する。
5. 第2版K02/K07/K10と比較し、旧wizard・設定UI・検証の不存在という誤認を再導入していないことを確認する。レビュー結果は主担当の独立した記録で判断する。

以上はUX-002の引継ぎ事項であり、他タスクの着手・完了を意味しない。

[S01]: https://www.microsoft.com/en-us/research/publication/guidelines-for-human-ai-interaction/
[S01-PDF]: https://www.microsoft.com/en-us/research/wp-content/uploads/2019/01/Guidelines-for-Human-AI-Interaction-camera-ready.pdf
[S02]: https://www.microsoft.com/en-us/research/blog/guidelines-for-human-ai-interaction-design/
[S03]: https://www.microsoft.com/en-us/research/publication/principles-mixed-initiative-user-interfaces-2/
[S03-PDF]: https://www.microsoft.com/en-us/research/wp-content/uploads/2016/11/chi99horvitz-1.pdf
[S04]: https://www.nngroup.com/articles/progressive-disclosure/
[S05]: https://www.cs.umd.edu/~ben/goldenrules.html
[S06]: https://www.nngroup.com/articles/icon-usability/
[S07]: https://www.nngroup.com/articles/interaction-cost-definition/
[S08-navigation]: https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics
[S08-responsive]: https://learn.microsoft.com/en-us/windows/apps/design/layout/responsive-design
[S09]: https://raw.githubusercontent.com/w3c/aria-practices/main/content/patterns/tabs/tabs-pattern.html
[S10-reflow]: https://raw.githubusercontent.com/w3c/wcag/main/guidelines/sc/21/reflow.html
[S10-resize]: https://raw.githubusercontent.com/w3c/wcag/main/guidelines/sc/20/resize-text.html
[S10-target]: https://raw.githubusercontent.com/w3c/wcag/main/guidelines/sc/22/target-size-minimum.html
[S10-focus]: https://raw.githubusercontent.com/w3c/wcag/main/guidelines/sc/22/focus-not-obscured-minimum.html
[S11-readme]: https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/README.md
[S11-svg]: https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/packages/svg-icons/README.md
[S11-license]: https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/LICENSE