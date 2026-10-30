/**
 * FR-136 / FR-137: the production templates shown in the Job creation screen. The ids and versions match the
 * definitions in src/HubRadio.App/StyleTemplates/StyleTemplateCatalog.cs. The character dialogue template (Q-022 A) cannot be
 * chosen until the user registered a standing picture of their own (the app bundles none).
 */
export type StyleTemplateOrientation = 'landscape' | 'vertical'

export interface StyleTemplateOption {
  readonly id: string
  readonly version: number
  readonly name: string
  readonly purpose: string
  readonly orientation: StyleTemplateOrientation
  readonly previewDescription: string
  readonly sampleText: string
  readonly textColor: string
  readonly outlineColor: string
  readonly emphasisColor: string
  /** FR-137 (6): true when the template can be chosen only after at least one standing picture is registered. */
  readonly requiresPortraits?: boolean
}

export const styleTemplateOptions: readonly StyleTemplateOption[] = Object.freeze([
  { id: 'explainer', version: 1, name: '解説', purpose: '解説・講座など、話の内容を全文字幕で伝える動画', orientation: 'landscape', previewDescription: '全文の字幕に、キーワードだけ色を変えた強調テロップを重ねます。', sampleText: 'ここが大事なポイントです', textColor: '#FFFFFF', outlineColor: '#000000', emphasisColor: '#FFD400' },
  { id: 'short-pop', version: 1, name: 'ショート飛び出し', purpose: '縦型ショートで、1〜3 語ずつ勢いよく見せたい動画', orientation: 'vertical', previewDescription: '1〜3 語ずつ表示し、話している語だけ色が変わって約 130% から 100% に縮みます。', sampleText: 'これ 本当に すごい', textColor: '#FFFFFF', outlineColor: '#000000', emphasisColor: '#FF3B7A' },
  { id: 'variety', version: 1, name: 'バラエティ', purpose: 'ツッコミや心の声を自由な位置に重ねる、にぎやかな動画', orientation: 'landscape', previewDescription: 'ツッコミ・心の声が飛び出し、揺れ、回転し、出現時に効果音が鳴ります。', sampleText: 'なんでやねん！', textColor: '#FFFFFF', outlineColor: '#E60012', emphasisColor: '#FFEB00' },
  { id: 'talk-show', version: 1, name: '対談番組', purpose: '2 人以上の対談・インタビュー番組', orientation: 'landscape', previewDescription: '話題・タイトル・帯を持つ落ち着いた対談向けの見せ方です。', sampleText: '今日のテーマ', textColor: '#FFFFFF', outlineColor: '#1A1A1A', emphasisColor: '#4FC3F7' },
  { id: 'vertical-3-tier', version: 1, name: '縦型 3 段', purpose: '縦型で、上段・中段・下段に分けて見せたい動画', orientation: 'vertical', previewDescription: '上にタイトル、中央に映像、下に字幕を置く縦型の 3 段構成です。', sampleText: '3 段で見やすく', textColor: '#FFFFFF', outlineColor: '#000000', emphasisColor: '#FFD400' },
  { id: 'handmade', version: 1, name: '手作り感', purpose: '作り込みすぎない、親しみのある手作り風の動画', orientation: 'landscape', previewDescription: '太いゴシックの白文字に黒縁、強調だけネオン色で控えめに見せます。', sampleText: 'ゆるっと いきます', textColor: '#FFFFFF', outlineColor: '#000000', emphasisColor: '#39FF14' },
  { id: 'character-dialogue', version: 1, name: 'キャラクター対話', purpose: '登録した立ち絵と台詞字幕で、キャラクターどうしの会話を見せる動画（立ち絵の登録が必要）', orientation: 'landscape', previewDescription: '登録した立ち絵の横に台詞字幕を出し、キャラごとに字幕の色が変わります。', sampleText: 'ねえ、聞いてる？', textColor: '#FFFFFF', outlineColor: '#000000', emphasisColor: '#FFD400', requiresPortraits: true },
] satisfies StyleTemplateOption[])

export const orientationLabel = (orientation: StyleTemplateOrientation): string => orientation === 'vertical' ? '縦型' : '横長'
