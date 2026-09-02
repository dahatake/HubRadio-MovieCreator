# HubRadio MovieCreator 既定設定調査

- 調査日: 2026-09-02
- 対象: 単一ユーザー向け source-only quick start、動画編集、字幕、音声、配信形式、アクセシビリティ
- 適用先: `docs/requirement-definition.md` 版 0.2 Draft、`ADR-0009-source-only-defaults-and-automated-evaluation.md`
- 実装状態: 本書は要求と既定値の根拠であり、実装完了を示さない

## 1. 結論

初期操作は**元動画を1本選ぶだけ**とし、それ以外は版付きの既定設定から補完する。通常画面には、元動画の選択、適用中プリセットの短い要約、編集開始／取消、進捗、成果物だけを表示する。全設定は専用の設定画面で現在値、既定値、根拠区分、初期値へ戻す操作を確認できるようにする。[U01][R01]

採用する中心原則は次のとおり。

1. 既定の公開範囲は社内限定とし、単一のWindows標準ユーザーで完結させる。[U01]
2. 出力は互換性を優先し、MP4、H.264、AAC-LC、4:2:0、プログレッシブ、48 kHzを基本とする。フレームレートは原則として元素材を維持する。[R02]
3. 軽微な編集はハードカットを基本とし、トランジションは意味上の境界だけに限定する。ハードカットの`0 ms`は製品上の定義であり、論文が0秒を推奨したという意味ではない。[R23][R25]
4. 日本語の既定は`SDH指向字幕候補`とし、最大7文字/秒、横書き16全角文字/行、2行を上限とする。必要な非発話音・話者情報の網羅性を自動保証せず、既定では`NOT EVALUATED`とする。[R16][R17]
5. 音声測定はITU-R BS.1770-5に従い、オンライン会話コンテンツの製品既定を-16 LKFS、True Peak -1 dBFS以下とする。ただし-16はApple Podcasts向け推奨を転用した**製品判断**であり、YouTube等の必須値ではない。[R13][R14]
6. `Music/`は利用者が社内利用を許可したローカル音源ライブラリとして扱う。本編会話中のBGMは既定で無効、オープニング、話題境界、エンディングだけを自動選択対象とする。[U01][R16]
7. 人手承認、別Windowsアカウント、社内法務部門を必須ゲートにしない。自動代理評価の合格を`human PASS`とは表記せず、評価範囲と未評価事項を残す。[U01][R16]

## 2. 事実性と採用区分

| 区分 | 意味 |
|---|---|
| 仕様 | 標準または配信先が明示する適合条件 |
| 公式推奨 | 標準化団体、OS、配信先が示す推奨。別用途へそのまま一般化しない |
| 査読知見 | 査読研究の条件内で得られた結果。製品利用者へ同じ効果を保証しない |
| 実務知見 | プロ編集者・制作事業者が公開した実務上の助言 |
| 現行実装値 | 現在のコードまたはfixtureに存在する値。production既定として承認済みとは限らない |
| 製品判断 | 上記の制約を組み合わせて本製品が選ぶ初期値。外部資料がその数値を直接命令したとは主張しない |

根拠の強さは、`A: 標準・公式仕様`、`B: 査読研究`、`C: 公式推奨`、`D: 実務資料`、`P: 本製品判断または利用者決定`で示す。

## 3. 現状との差

`Music/`の棚卸しは、文書編集用detached worktreeを作る前に元worktreeのリポジトリrootで実施した。音源はGit管理外であるため、detached worktreeに複製されていないことを「元worktreeにも存在しない」と読み替えない。文書worktreeへcopyして検証したり、Gitへ追加したりしない。[C05]

| 領域 | 現在確認できる状態 | 要求との差 | 根拠 |
|---|---|---|---|
| 案件作成 | `emptyProjectInput`は元動画を含む10項目がすべてnull/空で、5段階wizardが全10項目を必須化 | 元動画だけでは開始できない | [C01] |
| 設定画面 | 専用の全体設定画面は確認できない | 既定値の閲覧、変更、resetができない | [C01] |
| 16:9 | 1280×720、30000/1001 fps、H.264/AAC、字幕焼込ON | 720pと固定fpsは配信推奨を反映したproduction既定ではなく、Stage 0 fixture由来 | [C02] |
| 9:16 | 1080×1920、30000/1001 fps、H.264/AAC、字幕焼込OFF | SNS用quick startで字幕が自動表示されない | [C02] |
| セーフゾーン | 全画面矩形のみ、`platformSafeZonesMeasured=false` | 字幕領域と配信先UIの回避が未定義 | [C02] |
| ブランド | `development-neutral-placeholder` | 本番ブランド素材がなくても動く点はよいが、placeholderを承認済みブランドと表示できない | [C02] |
| レビュー | 多役割、人手承認、別アカウント試験をrelease gateにしている | 単一ユーザー、自動代理評価という利用者決定と衝突 | [U01][C03] |
| BGM | 権利レコード前提のみで、自動利用するローカルライブラリ既定がない | `Music/`の社内利用決定を反映していない | [U01] |

## 4. 採用する既定設定

### 4.1 操作・案件

| 設定 | 初期値 | 区分 / 強さ | 根拠と適用範囲 |
|---|---|---|---|
| 起動画面 | `かんたん編集` | 製品判断 / P | 設定数を最小化しsmart defaultsを使うWindowsガイドに従う。[R01] |
| 必須の案件入力 | 元動画1本のみ | 利用者決定 / P | その他は以下の既定値または解析結果で補完する。[U01] |
| 処理開始 | 動画選択後に適用内容を1画面で要約し、`編集を開始`で開始 | 製品判断 / P | 無確認の即時実行ではなく、入力操作は動画指定だけに保つ。[U01][R01] |
| コンテンツ名 | 元ファイル名の拡張子を除いた名前 | 製品判断 / P | 入力値を創作せず決定的に補完する。 |
| 目的 | `社内向け再利用動画の自動編集` | 製品判断 / P | 現在の許可範囲を狭く保つ。[U01] |
| 想定視聴者 | `社内視聴者` | 製品判断 / P | 外部公開を暗黙に仮定しない。[U01] |
| 公開範囲 | `社内限定` | 利用者決定 / P | `Music/`の利用許可も社内利用に限定される。[U01] |
| 言語 | 自動検出、判定不能時`ja-JP` | 製品判断 / P | HubRadioの日本語用途を既定にし、検出結果とfallbackを表示する。 |
| 出演者 | 必須入力なし。匿名話者ラベルから開始 | 製品判断 / P | 氏名・肩書を推測しない。 |
| 公開期限 | `なし` | 製品判断 / P | 存在しない期限を作らない。 |
| ブランド | `内蔵ニュートラル（ロゴなし）` | 製品判断 / P | 現行placeholderを承認済みブランドとは呼ばず、外部assetなしでも完了可能にする。[C02] |
| quick start成果物 | 16:9長尺1本、9:16短尺候補3本、字幕焼込版、字幕なし版、字幕file、サムネイル候補3枚 | 製品判断 / P | 要求定義の初期検証構成を日常操作の標準セットへ簡略化する。[U01] |
| 自動代理評価 | ON | 利用者決定 / P | 結果は`自動代理評価`と明記し、人手評価と同一視しない。[U01][R16] |
| 設定適用 | 変更直後に新規案件へ適用。既存版は作成時snapshotを維持 | 公式推奨+製品判断 / C/P | Windowsガイドは設定変更の即時反映を推奨する。再現性のため既存Compositionは遡及変更しない。[R01] |

### 4.2 設定画面

設定画面は5つのトップレベルグループにまとめる。各行は`現在値`、`初期値`、`この値の用途`、`初期値に戻す`を持つ。全設定を一度に戻す操作も提供する。[R01]

1. 案件と出力
2. 編集
3. 字幕とグラフィック
4. 音声と素材
5. プライバシーと保存

高度なサブ設定は`SettingsExpander`相当の展開UIに置き、ネストは1段までとする。設定項目自体を5個に制限するのではなく、トップレベルのまとまりを5個に保つという本製品上の解釈とする。[R01]

| トップレベル群 | 通常表示する設定 | 1段の詳細設定へ置く項目 |
|---|---|---|
| 案件と出力 | 成果物set、言語、公開範囲、16:9/9:16、字幕variant | container、codec、profile、解像度、frame rate、bitrate、色空間、短尺探索範囲、end-screen余白、直接投稿 |
| 編集 | 編集強度、明示cut、完全一致反復、冒頭末尾無音、opener、hook、CTA | 無音threshold/padding、transition種別/duration、J/L cut、jump-cut fallback、各構成規則 |
| 字幕とグラフィック | 字幕ON/OFF、字幕profile、焼込variant、位置、サムネイル候補数 | 読速、行長、行数、cue時間/間隔、font、色、背景、line height、字幕領域、platform UI mask、thumbnail canvas |
| 音声と素材 | loudness profile、BGM利用、BGM library、本編BGM | True Peak、channel/bitrate、noise/hum、duck量、stinger、track選択規則 |
| プライバシーと保存 | 利用者、workspace、自動削除、backup、cloud OFF | cross-account scope、cloud payload範囲、保持、外部destination、任意review/evaluation表示 |

通常表示は頻繁に意味を選ぶ項目と安全状態の要約、詳細設定は互換性・計測・timing等の専門値とする。詳細設定に置いても検索、現在値、初期値、resetを省略しない。

### 4.3 映像出力

YouTube等の公開資料を採る理由は、MP4/H.264/AAC/BT.709という保守的な製品profileと、将来profileを分離しやすい出発点を得るためである。R02はYouTube Studio Content Managerを使うpartner向けページであり、この資料だけで一般accountへのupload互換性を証明しない。社内限定成果物が各外部serviceへ適合済み、upload可能、公開承認済みであることは受入条件にしない。外部service固有の適合確認は将来の外部公開scopeで別に行う。[R02][R03]

| 設定 | 初期値 | 区分 / 強さ | 根拠と制限 |
|---|---|---|---|
| コンテナ | MP4 | partner向け公式推奨の転用+製品判断 / C/P | YouTube Content Manager partner向けupload encoding guidance。一般accountへのupload適合は未検証。[R02] |
| 映像codec | H.264 High Profile、プログレッシブ、4:2:0 | partner向け公式推奨の転用+製品判断 / C/P | YouTube Content Manager partner向け値を製品profileの出発点にする。一般upload互換性はこの資料だけで保証しない。[R02] |
| 音声codec | AAC-LC、48 kHz | partner向け公式推奨の転用+製品判断 / C/P | YouTube Content Manager partner向けupload encoding guidance。[R02] |
| 音声channel/bitrate | 元がmonoならmono 128 kbps、stereoならstereo 384 kbps | partner向け公式推奨の転用+製品判断 / C/P | YouTube Content Manager向け値。存在しないstereo情報を合成しない。[R02] |
| frame rate | 元素材を維持 | partner向け公式推奨の転用+製品判断 / C/P | 同ページの収録時と同じframe rateという推奨を製品判断へ転用。[R02] |
| 16:9解像度 | 1920×1080 | 公式推奨 / C | YouTubeの16:9 1080p推奨解像度。[R03] |
| 9:16解像度 | 1080×1920 | 製品判断 / P | 1080p相当の縦型。TikTok広告の最低540×960、LinkedIn広告の最大1080×1920を満たすが、両資料は広告文脈である。[R11][R12] |
| SDR色空間 | BT.709 | partner向け公式推奨の転用+製品判断 / C/P | YouTube Content Manager向けSDR guidance。[R02] |
| HDR入力 | quick startではSDR BT.709へ明示tone mapし、警告を記録 | 製品判断 / P | 異なる色空間を黙って誤解釈しない。HDR保持は詳細設定。 |
| 1080p映像bitrate | 24/25/30 fpsは8 Mbps、48/50/60 fpsは12 Mbps | partner向け公式推奨の転用+製品判断 / C/P | YouTube Content Manager向けSDR 1080p推奨。[R02] |
| 短尺の目標時間 | 30秒、探索範囲15〜60秒 | 製品判断 / P | LinkedInの15〜30秒は**広告placement向け推奨**、YouTube Shortsの3分は分類上限であり、普遍的な最適時間ではない。30秒は本製品の初期探索中心値。[R04][R12] |
| Shorts上限 | 3分 | 仕様 / A | 正方形または縦型で最大3分というYouTubeの分類条件。quick startの目標値とは分離する。[R04] |
| YouTube end screen余白 | 対象動画が25秒以上なら末尾5秒。詳細設定は5〜20秒 | 仕様+製品判断 / A/P | YouTubeが許容する末尾5〜20秒の最小値を既定にする。[R05] |
| 直接投稿 | OFF / 初期対象外 | 利用者決定+製品判断 / P | 出力と公開を分離し、quick startはローカル成果物までとする。[U01] |

### 4.4 編集

| 設定 | 初期値 | 区分 / 強さ | 根拠と制限 |
|---|---|---|---|
| 編集強度 | `標準` | 製品判断 / P | 安全な決定論規則を自動適用し、曖昧な意味編集は候補に留める。 |
| 冒頭・末尾無音 | 2.0秒以上を検出し自動短縮、発話前後0.30秒を保持 | 現行実装値+製品判断 / P | `-50 dB / 2.0 s`は既存Stage 0規則を採用し直す値で、外部標準ではない。[C04] |
| 明示的な「ここはカット」 | 該当発話を自動除外、元版と理由を保持 | 利用者決定+製品判断 / P | source-only完了のための可逆規則。 |
| 完全一致の隣接反復 | 後側を自動除外。同一話者・正規化後完全一致のみ | 現行実装値+製品判断 / P | fuzzy/semantic一致は自動削除しない。[C04] |
| 咳・雑音・脱線 | 自動削除OFF、注意候補のみ | 製品判断 / P | 誤削除で意味を変えない。 |
| 通常の映像接続 | ハードカット、duration `0 ms` | 査読知見+製品定義 / B/P | match-action研究はstraight cut近傍の許容窓を示す。`0 ms`は本製品で「transitionなし」を表す値。[R23] |
| 大きな話題境界 | title card優先、crossfadeはOFF | 製品判断 / P | 効果を常用せず、必要な意味境界だけへ限定する。[R25] |
| crossfade duration | 有効化時250 ms | 製品判断 / P | 外部資料が250 msを規定したとは主張しない。短く控えめな初期値として版管理する。 |
| J/L cut | `自動`。発話・無音境界から長さを決め、固定秒数を持たない | 実務知見+製品判断 / D/P | 音声を映像より先行／継続させる手法として利用する。[R24] |
| jump cut緩和 | 承認済みの元映像、資料、crop/zoom、cutawayだけを使用 | 実務知見+製品判断 / D/P | cutawayは主動作から補助対象へ切り替え、物語情報を見せる手法。存在しない事実映像を生成しない。[R26] |
| short opener | OFF | 公式広告知見+製品判断 / C/P | TikTok広告では価値提示を最初の3秒、hookを最初の6秒に置く推奨がある。広告以外への保証ではないが、前置きを置かない判断を補強する。[R09] |
| long opener | OFF。title overlayだけ | 実務知見+製品判断 / D/P | 不要・反復・注意を散らす瞬間を削る実務助言に合わせる。[R25] |
| short hook | 元発言内の問い・結論を最初の3秒以内へ配置できる場合だけ自動採用 | 公式広告知見+製品判断 / C/P | TikTok広告文脈の3秒/6秒を構成ヒントとしてのみ使い、元発言にない断定を作らない。[R09][R10] |
| CTA | shortから対応長尺がある場合だけ`本編を見る`を1件。その他はOFF | 製品判断 / P | URLや行動を捏造しない。 |

### 4.5 日本語字幕

quick startは、日本語の同言語`SDH指向字幕候補`を既定とする。Netflix値はNetflix納品規則、BBC値はBBCのprepared/online subtitle guidanceであり、本製品の普遍的標準ではない。日本語固有の文字数はNetflix、画面比率別の字高と字幕領域はBBCを参考にした製品プロファイルとして採用する。自動生成だけでcaption/SDHの意味的完全性またはWCAG適合を主張しない。[R16][R17][R20]

| 設定 | 初期値 | 区分 / 強さ | 根拠と制限 |
|---|---|---|---|
| 字幕生成 | ON | 仕様+製品判断 / A/P | WCAG 2.2 SC 1.2.2は録画済み同期mediaのaudioにcaptionを要求する。[R16] |
| profile | `日本語SDH指向字幕候補` | 製品判断 / P | 発話に加え、根拠を持って検出できた話者・非発話音を対象にする。網羅性は既定`NOT EVALUATED`。[R16][R17] |
| 読速 | 最大7全角文字/秒 | 公式推奨 / C | Netflix Japanese SDH上限。[R17] |
| 横書き行長 | 最大16全角文字/行 | 公式推奨 / C | Netflix Japanese SDH。半角は0.5文字として数える。[R17] |
| 縦書き行長 | 最大11全角文字/行 | 公式推奨 / C | Netflix Japanese SDH。[R17] |
| 行数 | 最大2行 | 公式推奨 / C | Netflix Japanese subtitle/SDH。[R17] |
| cue最短 | $5/6$秒（約0.833秒） | 公式推奨+製品判断 / C/P | 全Netflix timed text向けGeneral Requirementsの時間値を、本製品の日本語字幕profileでも正本にする。Japanese guide単独の0.5秒とTiming Guideの20 frame（本文併記の4/5秒とは算術不一致）は採用せず、有理時間を優先する。[R17][R18][R19] |
| cue最長 | 7秒 | 公式推奨 / C | Netflix General Requirements。[R19] |
| cue間隔 | 最小2 frame。0.5秒未満のgapは2 frameへ閉じ、0.5秒以上は保持 | 公式推奨+製品判断 / C/P | Netflixは全frame rateで最小2 frame、gapは2 frameまたは0.5秒以上とする。[R18] |
| in cue | 原則audioの最初のframe。1〜2 frame以内は許容し、shot-change規則を優先可能 | 公式推奨 / C | Netflix timing guidanceの原則、許容差、例外を分離。[R18] |
| out cue | 直後に次cueがなければ原則audio終了後少なくとも約0.5秒。dialogueがshotをまたがない場合はshot changeへ同期 | 公式推奨 / C | Netflix timing guidance。長く残る場合や例外は全編再生で判断する。[R18] |
| 改行 | 句・節・句読点など自然な統語境界を優先 | 査読知見 / B | 非統語的分割は認知負荷を高めたが、理解度低下は確認されなかった。[R22] |
| 通常文字 | 白`#FFFFFF` | 公式推奨 / C | BBC/Netflix。[R17][R20] |
| 背景 | 黒`#000000`、不透明 | 公式推奨 / C | BBCはwhite on blackとsolid black backgroundを要求する。[R20] |
| font | `Yu Gothic UI, Yu Gothic, Meiryo, proportionalSansSerif` | 製品判断 / P | Windowsで利用可能な日本語system sans-serifを優先。外部資料がこのfallback列を規定したとは主張しない。 |
| 16:9 authored line height | active video高の7.5% | 公式推奨の転用+製品判断 / C/P | BBCのauthoring範囲7〜8%内のcomputed例示値を焼込profileへ採用。device側の最終表示値ではない。[R20] |
| 9:16 authored line height | active video高の4.2% | 公式推奨の転用+製品判断 / C/P | BBCのauthoring範囲3.9〜4.5%内のcomputed例示値を焼込profileへ採用。device側の最終表示値ではない。[R20] |
| 16:9 subtitle領域 | 中央75%幅 × 中央90%高の内側 | 公式推奨 / C | BBC online guidance。[R20] |
| 9:16 subtitle領域 | 中央90%幅 × 中央75%高の内側 | 公式推奨 / C | BBC online guidance。[R20] |
| 位置 | 下部中央。顔、口、資料、既存文字と衝突時は上／横へ移動 | 公式推奨 / C | BBCとNetflix Japanese positioning guidance。[R17][R20] |
| SNS UI回避 | platform UI maskは既定`未確認`。BBC汎用字幕領域へfallbackしwarning | 仕様+製品判断 / A/P | TikTok ad safe zoneはcaption長やformat等で変わるため、単一pixel値を固定せず、TikTok/organic適合済みとも表示しない。[R11][R20] |
| 9:16焼込 | ON | 製品判断 / P | platform側caption有無に依存せずquick start成果物を理解可能にする。 |
| 16:9出力 | 焼込版と字幕なし版を両方生成し、SRT/VTTを添付 | 製品判断 / P | 利用先のclosed caption対応と再編集を両立する。 |
| 通常翻訳字幕profile | 最大4文字/秒、13全角文字/行、最短$5/6$秒、最大2行 | 公式推奨+製品判断 / C/P | Japanese guideは最大4 cpsと最短0.5秒を記すが、全file向けGeneral/Timing guideとの競合を避け、製品profileは$5/6$秒へ統一する。[R17][R18][R19] |

査読研究は英語・ポーランド語・スペイン語の成人74人を対象とした。Experiment 1は未知のHungarian音声で12/16/20 cps、Experiment 2は理解可能なEnglish音声で12/20 cpsを比較し、eye-tracking解析対象は品質除外後それぞれ60人と64人だった。研究条件内では速い字幕にも概ね対応し、遅い字幕で再読が増えたが、速度とtext reductionの効果を完全には分離できていない。日本語を対象にせず、7文字/秒を直接導く資料でもないため、「遅いほど常に良い」と仮定しない補助根拠にだけ使う。[R21]

### 4.6 音声とBGM

| 設定 | 初期値 | 区分 / 強さ | 根拠と制限 |
|---|---|---|---|
| loudness測定 | ITU-R BS.1770-5 | 仕様 / A | 同文書は測定algorithmであり、-16/-23の運用target自体を規定しない。[R14] |
| online/internal target | -16 LKFS、許容±1 dB | 公式推奨の転用+製品判断 / C/P | Apple Podcastsの会話音声推奨を社内・online talk既定へ転用。YouTube必須値ではない。[R13] |
| True Peak | -1 dBFS以下 | 公式推奨の転用+製品判断 / C/P | Apple Podcasts向け推奨。[R13] |
| broadcast profile | -23 LUFS | 公式推奨 / C | EBU R128 v5のprogramme loudness推奨。quick startでは選択しない。[R15] |
| noise/hum reduction | `自動提案、既定適用OFF` | 製品判断 / P | 声質変化を避け、明示有効化時はbefore/afterを保存する。 |
| BGM library | workspace外の`Music/`をread-only参照 | 利用者決定 / P | 36 MP3という元worktreeでの棚卸し結果はライブラリ存在確認であり、各曲の内容・権利条件を推測しない。directory不在・空・読取不能ならBGMなしで完了し、warningを記録する。[U01][C05] |
| BGM利用範囲 | 社内限定成果物のみ | 利用者決定 / P | 外部用profileでは自動除外する。[U01] |
| BGM選択 | decode可能でcue以上の長さを持つtrackを、開始時library snapshotの内容hash順で選択 | 製品判断 / P | 同じ入力と設定から同じ選択を再現する。ファイル名を意味分類に使わず、自動loopもしない。該当trackがなければBGMなし。[U01] |
| title/topic/end BGM利用 | ON（適格区間がある場合だけ） | 利用者決定+製品判断 / P | speechと重ならないtitle card、topic card、end cardだけを対象にする。対応区間がなければ挿入しない。[U01] |
| 本編会話中BGM | OFF | 製品判断 / P | speechの理解を最優先する。[R16] |
| 本編BGMを有効化した場合 | foreground speechより20 dB低くduck | 公式基準の限定転用+製品判断 / A/P | WCAG 1.4.7の20 dBはLevel AAAかつ録音済みaudio-only speechが対象。動画AAの普遍要件ではないため、詳細設定の保守値としてのみ使う。[R16] |
| stinger | 1〜2秒の例外を許可、speechと重ねない | 公式基準の限定転用+製品判断 / A/P | WCAG 1.4.7のoccasional sound例外を参考にするが、同SCの適用範囲を拡大主張しない。[R16] |
| 音楽的適合性 | `NOT EVALUATED` | 製品判断 / P | 内容を解析せず、曲調、雰囲気、ブランド適合を自動保証しない。利用者は任意にpreview、変更、無効化できる。 |
| speech/BGMの知覚的理解 | `NOT EVALUATED` | 製品判断 / P | 20 dB差の機械測定が通っても、人が発話を理解できることやmix品質をPASSとしない。 |

### 4.7 サムネイル

| 設定 | 初期値 | 区分 / 強さ | 根拠と制限 |
|---|---|---|---|
| 候補数 | 3 | 製品判断 / P | 選択肢を出しつつ通常画面を複雑にしない。 |
| 16:9 canvas | 1280×720 | 製品判断 / P | 16:9の実用的な初期値。現行profile値を流用するが、YouTubeの普遍的必須値とは表記しない。[C02] |
| 9:16 canvas | 1080×1920 | 製品判断 / P | YouTube Shorts thumbnailは9:16が推奨される。[R08] |
| 画像内容 | 元動画の実frameのみ。生成顔・生成場面OFF | 公式policy+製品判断 / A/P | 動画にない内容を期待させる誤解thumbnailを禁止するYouTube policy。[R07] |
| 文言 | 短く、動画内容を正確に表す | 公式推奨 / C | titleはaccurate/succinct、thumbnailは複雑にしすぎないというYouTube guidance。[R06] |
| 選択 | blur/露出/余白等の客観値で候補化し、自動代理評価で1枚を推奨 | 製品判断 / P | 表情の良否・感情を未検証modelで断定しない。 |

### 4.8 アクセシビリティと自動代理評価

| 設定 | 初期値 | 区分 / 強さ | 根拠と制限 |
|---|---|---|---|
| UI target | WCAG 2.2 Level AA | 仕様+製品判断 / A/P | conformance claimは実際の全page/process評価が完了するまで行わない。[R16] |
| 通常文字contrast | 4.5:1以上 | 仕様 / A | SC 1.4.3 Level AA。large textは3:1の例外がある。[R16] |
| keyboard | 全機能をkeyboard interfaceで操作可能 | 仕様 / A | SC 2.1.1 Level A。[R16] |
| focus | keyboard focus indicatorを表示 | 仕様 / A | SC 2.4.7 Level AA。[R16] |
| caption | 録画済み同期mediaに字幕 | 仕様 / A | SC 1.2.2 Level A。[R16] |
| 自動代理評価項目 | keyboard event、focus順・可視style、contrast計算、accessibility tree、字幕有無・読速・行長・同期、safe zone、decode/A-V/loudness/traceability | 製品判断 / P | 機械判定できる項目だけをclosedな判定表にする。[U01][R16] |
| 自動代理評価の成功名 | `Automated proxy: PASS` | 利用者決定+製品判断 / P | `HUMAN EVALUATION: PASS`とは書かない。[U01] |
| 字幕の意味的完全性 | `NOT EVALUATED` | 製品判断 / P | 必要な非発話音、話者情報、主要内容の理解をclosedな自動検査だけで網羅保証しない。[R16][R17] |
| 判断不能 | 内部成果物はwarning付きで許可、外部用は保留 | 製品判断 / P | source-onlyの社内利用を妨げず、未評価範囲を隠さない。 |

### 4.9 保存・privacy・cloud

| 設定 | 初期値 | 区分 / 強さ | 根拠と制限 |
|---|---|---|---|
| 利用者 | 現在のWindows標準ユーザー1人 | 利用者決定 / P | 別accountを通常利用・受入の前提にしない。[U01] |
| workspace | `%LOCALAPPDATA%\HubRadio.MovieCreator\Workspace` | 製品判断 / P | 標準ユーザーが書き込めるper-user既定。詳細設定で変更可能。 |
| cross-account保証 | 対象外 | 利用者決定 / P | S-003型の別account拒否はinitial release gateから削除する。[U01] |
| 自動期限削除 | OFF | 現行判断+製品判断 / P | 日数を捏造せず、手動cache清掃だけを許可する。[C03] |
| backup | 手動、既定保存先なし | 現行判断 / P | 保存先がない状態を保護済みと表示しない。[C03] |
| cloud文章提案 | OFF | 現行判断+製品判断 / P | local/manualで完了可能にし、Azure資源の存在を必要性とみなさない。[C03] |
| cloudへ送れる内容 | 有効化時も選択・mask済み文字起こしspanだけ | 現行判断 / A/P | 動画、音声、画像、path、SID、全文transcriptは送らない。[C03] |
| 社内法務approval | 要求・release gateから削除 | 利用者決定 / P | 第三者license、notice、利用規約、再配布条件そのものは削除しない。[U01] |
| review/approval | 自動代理評価を既定。利用者self-reviewは任意 | 利用者決定 / P | 多役割reviewを必須にしない。[U01] |

## 5. 配信先資料の適用範囲

| 資料 | 採用する用途 | 採用しない一般化 |
|---|---|---|
| TikTok performance ads | 9:16、最初の3/6秒、text overlayの構成ヒント | organic動画の普遍的な最適時間・読速とは呼ばない |
| TikTok Auction In-Feed Ads | 最低解像度とsafe zoneが可変である事実 | 単一の固定safe-zone pixelを全TikTok表示へ適用しない |
| LinkedIn video ads | 9:16対応範囲、15〜30秒という広告placement推奨 | organic投稿の必須時間とは呼ばない |
| Apple Podcasts | spoken audioのloudness/true peak初期値 | YouTube・動画配信の公式targetとは呼ばない |
| EBU R128 | broadcast用の別profile | online quick startへ-23 LUFSを強制しない |
| WCAG 1.4.7 | BGM詳細設定の保守的参考値 | 動画のLevel AA要件とは呼ばない |
| Netflix Japanese | 日本語Subtitle/SDH profileの出発点 | Netflix以外の配信先適合を保証しない |
| BBC Subtitle Guidelines | aspect別の字幕領域・字高 | 日本語文字数規則としては使わない |

## 6. 既定値の版管理

初期`DefaultSettingsProfile`の版は`1.0.0`とし、`MAJOR.MINOR.PATCH`を使う。

- `MAJOR`: setting keyの意味、単位、安全scope、削除等、明示migrationを要する非互換変更。
- `MINOR`: setting追加または新規案件へ適用する初期値・規則の変更。既存snapshotは変更しない。
- `PATCH`: 値と処理を変えない出典metadata、説明、誤記の修正。canonical provenanceが変わる場合は新digestを持つ。

versionだけで同一性を決めず、canonical contentのSHA-256 digestも必須とする。同じversionへ異なるcontentを再発行しない。

- 既定値集合は`DefaultSettingsProfile`としてsemantic versionと内容digestを持つ。
- 新規案件は開始時のprofile snapshotをCompositionへ保存する。
- 外部仕様のURL、ページ表示版／更新日、取得日、採用理由をprofile release noteへ保存する。
- URLに更新日が表示されない場合は、更新日を推測せず`表示なし`と記録する。
- 配信先UIのsafe zoneは変化し得るため、固定値ではなく`platform + profile version + verifiedAt`で管理し、未確認時はBBC由来の汎用字幕領域へfallbackする。[R11][R20]
- 既定値更新は既存案件へ遡及適用しない。利用者が明示的に新profileへ移行した場合だけ子版を作る。

## 7. 出典台帳

すべて2026-09-02に確認した。ページが表示する更新日だけを記載し、表示のない日付は推測していない。

### 利用者決定・リポジトリ

- **[U01]** 利用者決定（2026-09-02）: `sample/20260615_024326000_iOS.MOV`の利用を全許可、`Music/`を社内利用に許可、自動代理評価を許可、cross-account要件と社内法務approval gateを削除、source-only quick start、全設定の初期値表示、実装中断。
- **[C01]** 現行案件UI: `src/HubRadio.Web/src/features/projects/projectState.ts`の`emptyProjectInput`、`src/HubRadio.Web/src/features/projects/ProjectWorkspace.tsx`の5段階・10項目wizard。
- **[C02]** 現行render profile: `src/HubRadio.App/Profiles/16x9/*.json`、`src/HubRadio.App/Profiles/9x16/vertical-short.json`。全16:9は1280×720、9:16は1080×1920、30000/1001、H.264/AAC。templateはdevelopment placeholder、platform safe zoneは未測定。
- **[C03]** 旧方針: `docs/dev/decisions/ADR-0002-product-scope.md`〜`ADR-0008-bounded-azure-evaluation.md`、`docs/dev/accessibility/test-report.md`。
- **[C04]** 現行解析規則: `docs/dev/planning/implementation-plan.md`のA-007/A-009。無音は`-50 dB / 2.0 s`、反復は構造的隣接・NFKC完全一致の候補規則。
- **[C05]** read-only棚卸し（2026-09-02）: 文書用detached worktree作成前の元worktreeで、リポジトリrootのGit管理外`Music/`に36 MP3、合計149,081,539 bytesを確認。音源はdetached worktreeへcopyせず、内容評価・曲名推測・ファイル変更も実施していない。

### UIと設定

- **[R01]** Microsoft, “Guidelines for app settings,” Microsoft Learn, page metadata `ms.date: 2026-04-08`. https://learn.microsoft.com/windows/apps/design/app-settings/guidelines-for-app-settings  
  Smart defaults、設定数の最小化、専用settings entry、即時反映、関連設定のgroup化、トップレベル4〜5、advanced sub-optionのexpander、1段以内のnestingを確認。

### 動画配信仕様・推奨

- **[R02]** Google/YouTube Help, “YouTube recommended upload encoding settings,” 更新日表示なし. https://support.google.com/youtube/answer/1722171?hl=en  
  MP4、H.264 High Profile、progressive、4:2:0、source frame rate、AAC-LC、48 kHz、1080p SDR 8/12 Mbps、BT.709を確認。ページ冒頭は機能をYouTube Studio Content Manager partner向けと限定しているため、同値を製品profileへ転用する判断と一般accountへのupload適合を分離する。
- **[R03]** Google/YouTube Help, “Video resolution & aspect ratios,” 更新日表示なし. https://support.google.com/youtube/answer/6375112?hl=en  
  標準16:9、1080p 1920×1080、playerのaspect自動適応を確認。
- **[R04]** Google/YouTube Help, “Understand three-minute YouTube Shorts,” 更新日表示なし. https://support.google.com/youtube/answer/15424877?hl=en  
  square/verticalのShorts最大3分と適用日を確認。
- **[R05]** Google/YouTube Help, “Add end screens to videos,” 更新日表示なし. https://support.google.com/youtube/answer/6388789?hl=en  
  end screenは末尾5〜20秒、対象動画は25秒以上を確認。
- **[R06]** Google/YouTube Help, “Thumbnail & title tips,” 更新日表示なし. https://support.google.com/youtube/answer/12340300?hl=en  
  titleの正確さ・簡潔さ、thumbnailを複雑にしすぎないことを確認。
- **[R07]** Google/YouTube Help, “Thumbnails policy,” 更新日表示なし. https://support.google.com/youtube/answer/9229980?hl=en  
  動画にない内容があると誤認させるthumbnailを禁止。
- **[R08]** Google/YouTube Help, “Add custom thumbnails on YouTube,” 更新日表示なし. https://support.google.com/youtube/answer/72431?hl=en  
  Shorts upload thumbnailの推奨aspect 9:16を確認。通常動画のpixel数は動的折りたたみ本文から安定取得できなかったため、本書は公式値として断定しない。
- **[R09]** TikTok for Business, “Creative best practices for performance ads,” last updated June 2025. https://ads.tiktok.com/resources/help/article/creative-best-practices?lang=en  
  **広告文脈**で9:16、720p以上、UI safe zone、hookを最初の6秒、propositionを最初の3秒、5〜10 words/secを推奨。
- **[R10]** TikTok for Business, Michelle Lim, “The Power of Creative Elements in TikTok ads,” 2022-11-28. https://ads.tiktok.com/business/creativecenter/quicktok/online/Power_Creative_Elements/pc/en  
  **広告研究文脈**で最初の2秒の価値、5〜10 words/sec、simple/authentic treatmentを確認。
- **[R11]** TikTok for Business, “TikTok Auction In-Feed Ads,” last updated June 2026. https://ads.tiktok.com/resources/help/article/tiktok-auction-in-feed-ads?lang=en  
  **広告仕様**でvertical 9:16を推奨、最低540×960、safe zoneはdimension、caption長、追加formatで変化すると明記。
- **[R12]** LinkedIn Help, “Video ads advertising specifications,” page displayed “Last updated: 4 weeks ago” at retrieval. https://www.linkedin.com/help/lms/answer/a424737/video-ads-advertising-specifications?lang=en  
  **広告仕様**で9:16 360×640〜1080×1920、MP4、H.264/VP8、AAC/MPEG4、3秒〜30分、全placement向け15〜30秒推奨を確認。

### 音声

- **[R13]** Apple Podcasts for Creators, “Audio requirements,” 更新日表示なし. https://podcasters.apple.com/support/893-audio-requirements  
  podcast audioの推奨としてoverall loudness -16 dB LKFS ±1 dB、true peak -1 dB FS以下、BS.1770-5測定を確認。
- **[R14]** ITU-R, “Recommendation BS.1770-5: Algorithms to measure audio programme loudness and true-peak audio level,” approved 2023-11-22, in force. https://www.itu.int/rec/R-REC-BS.1770-5-202311-I/en  
  測定algorithmの仕様。配信target値を規定する資料としては扱わない。
- **[R15]** European Broadcasting Union, “EBU R 128 Loudness normalisation and permitted maximum level of audio signals,” Version 5.0, 2023-11-21/November 2023. https://tech.ebu.ch/publications/r128  
  average programme loudness -23 LUFSのbroadcast recommendation。

### アクセシビリティ

- **[R16]** W3C, “Web Content Accessibility Guidelines (WCAG) 2.2,” W3C Recommendation, canonical URL https://www.w3.org/TR/WCAG22/ ; official source mirror https://github.com/w3c/wcag/blob/main/guidelines/index.html  
  取得時にcanonical pageはHTTP 403だったため公式repositoryのnormative sourceも照合。SC 1.2.2 Captions (Prerecorded) Level A、1.4.3 Contrast (Minimum) Level AA 4.5:1、2.1.1 Keyboard Level A、2.4.7 Focus Visible Level AA、1.4.7 Low or No Background Audio Level AAA（録音済みaudio-only foreground speech、20 dB）を確認。

### 字幕

- **[R17]** Netflix Partner Help Center, “Japanese Timed Text Style Guide,” latest changelog entry 2025-12-19. https://partnerhelp.netflixstudios.com/hc/en-us/articles/215767517-Japanese-Timed-Text-Style-Guide  
  Japanese通常字幕: 横13/縦11全角文字、4 cps、最短0.5秒、2行。Japanese SDH: 横16/縦11全角文字、7 cps、2行。白、proportional sans-serif、positioning等を確認。
- **[R18]** Netflix Partner Help Center, “Timed Text Style Guide: Subtitle Timing Guidelines,” latest changelog entry 2025-07-04. https://partnerhelp.netflixstudios.com/hc/en-us/articles/360051554394-Timed-Text-Style-Guide-Subtitle-Timing-Guidelines  
  in-timeは原則audioの最初のframe、1〜2 frame差は許容、後続cueなしではout-timeを原則audio後少なくとも約0.5秒、条件付きshot-change同期、全frame rateで最小gap 2 frame、gapは2 frameまたは0.5秒以上を確認。最短duration本文の“20 frames (or 4/5 sec)”は24 fpsの20 frameおよびGeneral guideの$5/6$秒と算術上一致しないため、その秒換算を製品値に採用しない。
- **[R19]** Netflix Partner Help Center, “Timed Text Style Guide: General Requirements,” latest displayed changelog entry 2022-10-07. https://partnerhelp.netflixstudios.com/hc/en-us/articles/215758617-Timed-Text-Style-Guide-General-Requirements  
  最短$5/6$秒（約0.833秒）、最大7秒、最大2行、自然なline treatmentを確認。language-specific guideも併読する指示があるため、競合は本書で明示した。
- **[R20]** BBC, “Subtitle Guidelines,” Version 1.2.5, March 2026. https://www.bbc.co.uk/accessibility/forproducts/guides/subtitles/  
  160〜180 wpm、自然なline break、16:9は推奨最大2行・central 75% horizontal/90% vertical、9:16は推奨最大3行・central 90% horizontal/75% vertical、white on solid black、authoring line heightは16:9で7〜8%、9:16で3.9〜4.5%等を確認。重要映像を隠さない場合は追加行を許すBBC例外がある。本製品は日本語Netflix profileを組み合わせ、より厳しい最大2行を製品判断として採用する。
- **[R21]** Szarkowska, A.; Gerber-Morón, O., “Viewers can keep up with fast subtitles: Evidence from eye movements,” PLOS ONE 13(6), e0199331, published 2018-06-19. https://doi.org/10.1371/journal.pone.0199331  
  英語・ポーランド語・スペイン語の全参加者74人。Experiment 1は12/16/20 cps・未知のHungarian音声、Experiment 2は12/20 cps・理解可能なEnglish音声。eye-tracking解析は品質除外後60人/64人。研究条件内で速い字幕にも対応し、遅い字幕で再読が増えたが、速度とtext reductionの効果を完全には分離できない。日本語defaultの直接根拠にはしない。
- **[R22]** Gerber-Morón, O.; Szarkowska, A.; Woll, B., “The impact of text segmentation on subtitle reading,” Journal of Eye Movement Research 11(4), published 2018-06-30. https://doi.org/10.16910/11.4.2  
  非統語的な字幕分割は認知負荷を高めたが、理解度への悪影響は確認されなかった。

### 編集実務・研究

- **[R23]** Langkjær, B.; Gregersen, A.; Cabak Rédei, A.; van de Weijer, J.; Innes-Ker, Å., “Matching on action: Effects of action speed and viewpoint on perceived continuity across match-action film edits,” Psychology of Aesthetics, Creativity, and the Arts 17(5), 553–561, 2023 (online 2022). https://doi.org/10.1037/aca0000446  
  39人・8 clipsの条件で平均選好はstraight cutから1 frame未満にあり、straight cut中心の許容窓を示した。全編集の普遍則や0 ms transitionの規定とは扱わない。
- **[R24]** Reuben Evans, “Insider Tips: Save Time with Final Cut Pro’s Precision Editor,” Frame.io Insider, 2023-04-05, updated 2023-12-14. https://blog.frame.io/2023/04/05/insider-tips-final-cut-pro-precision-editor/  
  J/L cutを映像より音声を先行または継続させる編集として説明。固定durationは示していない。
- **[R25]** Ron Dawson, “5 Essential Strategies to Cut the Fat and Tighten Your Edit,” Frame.io Insider, 2017-07-24, updated 2018-06-29. https://blog.frame.io/2017/07/24/tighten-your-edit/  
  物語・messageに寄与しない、注意を散らす、反復、不要な瞬間を削る実務助言。数値thresholdの根拠にはしない。
- **[R26]** Adobe, “A guide to cutaway shots,” date not displayed. https://www.adobe.com/creativecloud/video/production/cinematography/camera-shots-and-angles/cut-away-shot.html  
  cutawayを主動作から対象・場所等へ切り替える補助shotと定義し、説明ではなく必要な物語要素を見せる用途を確認。

## 8. 取得できなかった資料の扱い

- W3C canonical Recommendationは取得時にHTTP 403だったため、canonical URLを維持しつつW3C公式GitHub sourceを照合した。[R16]
- YouTube通常動画thumbnailの折りたたみ内pixel値は静的取得で安定確認できなかったため、公式値として採用していない。[R08]
- “Event segmentation and seven types of narrative discontinuity in popular movies”はCrossrefで書誌（James E. Cutting, Acta Psychologica 149, 69–77, 2014, DOI `10.1016/j.actpsy.2014.03.003`）を確認したが、publisher本文はHTTP 403、PMCはcookie challengeだった。要旨本文を根拠にした製品claimは本書へ採用していない。
