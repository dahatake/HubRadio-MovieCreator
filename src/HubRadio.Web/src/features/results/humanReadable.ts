// FR-052: shared wording for the 仕上がり screen. Values a user cannot judge (IDs, digests,
// raw rational times, internal codes) are turned into plain Japanese here instead of in each panel.
import type { EvaluationStatus } from '../workflow/pipelineProtocol'

type Numeric = number | string

/** "8.5秒", "1分17秒", "1時間2分3秒". Rounds to 0.1 s below one minute and to whole seconds above. */
export function formatDuration(numerator: Numeric, denominator: Numeric): string {
  const value = Number(numerator)
  const rate = Number(denominator)
  if (!Number.isFinite(value) || !Number.isFinite(rate) || rate <= 0) return '不明'
  const seconds = value / rate
  if (seconds < 0) return '不明'
  if (seconds < 60) {
    const rounded = Math.round(seconds * 10) / 10
    return `${Number.isInteger(rounded) ? rounded.toFixed(0) : rounded.toFixed(1)}秒`
  }
  const total = Math.round(seconds)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const rest = total % 60
  return `${hours > 0 ? `${hours}時間` : ''}${hours > 0 || minutes > 0 ? `${minutes}分` : ''}${rest}秒`
}

/** A playback position such as "1:17" or "0:08.5", for times inside a video. */
export function formatTimestamp(numerator: Numeric, denominator: Numeric): string {
  const value = Number(numerator)
  const rate = Number(denominator)
  if (!Number.isFinite(value) || !Number.isFinite(rate) || rate <= 0) return '不明'
  // A source may start slightly before 0 (e.g. audio priming); show it as "-0:00.1".
  if (value < 0) return `-${formatTimestamp(-value, rate)}`
  const tenths = Math.round((value / rate) * 10)
  const minutes = Math.floor(tenths / 600)
  const seconds = (tenths % 600) / 10
  const secondsText = Number.isInteger(seconds) ? String(seconds).padStart(2, '0') : seconds.toFixed(1).padStart(4, '0')
  return `${minutes}:${secondsText}`
}

/** "12.3 MB" style sizes with 1000-based units, as Windows Explorer users usually read them. */
export function formatByteSize(byteCount: Numeric): string {
  const bytes = Number(byteCount)
  if (!Number.isFinite(bytes) || bytes < 0) return '不明'
  const units = ['バイト', 'KB', 'MB', 'GB', 'TB']
  let size = bytes
  let unit = 0
  while (size >= 1000 && unit < units.length - 1) {
    size /= 1000
    unit += 1
  }
  return unit === 0 ? `${size} バイト` : `${size.toFixed(1)} ${units[unit]}`
}

export const evaluationStatusLabels: Readonly<Record<EvaluationStatus, string>> = {
  PASS: '問題なし',
  WARNING: '要確認',
  FAIL: '問題あり',
  'NOT EVALUATED': '未評価',
}

export function evaluationStatusLabel(status: EvaluationStatus | null | undefined): string {
  return status === null || status === undefined ? evaluationStatusLabels['NOT EVALUATED'] : evaluationStatusLabels[status]
}

const evaluationRuleLabels: Readonly<Record<string, string>> = {
  codec: '動画の形式',
  fullDecode: '最後まで再生できるか',
  duration: '動画の長さ',
  avSync: '映像と音声のずれ',
  subtitlePresence: '字幕が入っているか',
  subtitleReadingSpeed: '字幕を読み切れる速さか',
  subtitleLineLength: '字幕1行の文字数',
  subtitleLineCount: '字幕の行数',
  subtitleTiming: '字幕の表示時間と間隔',
  subtitleSafeZone: '字幕が画面からはみ出さないか',
  loudness: '音量',
  truePeak: '音割れ',
  provenance: '元動画との対応',
  settingsBinding: '設定どおりに作られたか',
  keyboard: 'キーボードで操作できるか',
  focus: '操作中の場所が見えるか',
  contrast: '文字の見やすさ',
  accessibilityTree: '読み上げソフトへの対応',
  subtitleSemanticCompleteness: '字幕が話の内容をすべて伝えているか',
  nonSpeechSoundCoverage: '効果音や音楽の字幕',
  speakerInformationCoverage: '誰が話しているかの表示',
  mainContentUnderstanding: '内容の分かりやすさ',
  humanReadability: '人が読んだときの読みやすさ',
  naturalness: '仕上がりの自然さ',
  perceivedAudioQuality: '音の聞きやすさ',
  emotionalFit: '雰囲気が合っているか',
  legalCompliance: '権利・法令の確認',
}

export function evaluationRuleLabel(ruleId: string): string {
  return evaluationRuleLabels[ruleId] ?? 'その他の確認項目'
}

const failureCodeLabels: Readonly<Record<string, string>> = {
  GenerationFailed: '作成に失敗しました',
  GenerationCanceled: '作成を取り消しました',
  GenerationReconciliationRequired: '作成途中のファイルの確認が必要です',
  NoArtifacts: '成果物が作られませんでした',
  PrimaryArtifactMissing: '主な成果物が見つかりません',
  BindingMismatch: '元動画・設定との対応が確認できません',
  SettingsBindingMismatch: '作成時の設定と一致しません',
  VersionNotPersisted: '編集結果を保存できませんでした',
  ArtifactValidationFailed: 'ファイルの確認に失敗しました',
  QualityControlFailed: '品質の確認で問題が見つかりました',
  Canceled: '取り消しました',
  EvidencePersistenceFailed: '確認記録を保存できませんでした',
  EvaluationPersistenceFailed: '評価結果を保存できませんでした',
  WorkflowConflict: 'ほかの操作と重なりました',
  StoredEvidenceInvalid: '保存済みの記録を読み取れません',
  DeliveryNotValidated: '保存先への書き出しを確認できません',
  EvidencePromotionReconciliationRequired: '記録の保存途中の確認が必要です',
}

export function failureCodeLabel(code: string): string {
  return failureCodeLabels[code] ?? '作成中に問題が発生しました'
}

/** Roles such as "vertical-burn-in-<id>" carry an internal ID; only the kind is shown. */
export function generationRoleLabel(role: string): string {
  if (role === 'plan') return '構成'
  if (role === 'music') return 'BGM'
  if (role === 'video') return '映像'
  if (role === 'human-evaluation') return '人による確認'
  if (role === 'landscape-subtitles') return '長尺の字幕'
  if (role === 'landscape-clean') return '長尺・クリーン版'
  if (role === 'landscape-burn-in') return '長尺・字幕焼き込み版'
  if (role === 'landscape-srt') return 'SRT 字幕'
  if (role === 'landscape-vtt') return 'WebVTT 字幕'
  if (role.startsWith('vertical-subtitles-')) return '短尺の字幕'
  if (role.startsWith('vertical-burn-in-')) return '短尺・字幕焼き込み版'
  if (role.startsWith('thumbnail-landscape-')) return '横長サムネイル'
  if (role.startsWith('thumbnail-vertical-')) return '縦型サムネイル'
  const omissionRoles: Readonly<Record<string, string>> = {
    ShortForm: '短尺動画', Thumbnail: 'サムネイル', LandscapeSubtitles: '長尺の字幕', VerticalSubtitles: '短尺の字幕',
    VerticalCallToAction: '短尺の呼びかけ', PreRollMargin: '冒頭の余白', Opening: 'オープニング', ProgramName: '番組名',
    EpisodeNumber: '回数', EpisodeTitle: '回のタイトル', PerformerIntroduction: '出演者紹介',
    PublicationScopeIndicator: '公開範囲の表示', Body: '本編', Topic: '話題', Summary: 'まとめ', CallToAction: '呼びかけ',
    Ending: 'エンディング', PostRollMargin: '末尾の余白', Transcript: '文字起こし', UsedSourceRanges: '元動画のどこを使ったかの一覧',
  }
  return omissionRoles[role] ?? 'その他'
}

const generationCodeLabels: Readonly<Record<string, string>> = {
  SubjectRegionUnavailable: '人物の位置を見つけられず、画面中央で切り抜きました',
  FixedLayoutFallback: '決まった配置で作成しました',
  BlurredBackgroundFallback: 'ぼかした背景で余白を埋めました',
  PlatformUiUnmeasured: 'SNS の操作ボタンとの重なりは未確認です',
  CandidateLimitApplied: '候補の数を上限までに絞りました',
  ThumbnailQualityNotEvaluated: 'サムネイルの見栄えは未評価です',
  LibraryAbsent: 'BGM 用の音楽フォルダーがありません',
  LibraryEmpty: 'BGM 用の音楽がありません',
  LibraryInaccessible: 'BGM 用の音楽フォルダーを読めません',
  UnsafeLibraryLocation: 'BGM 用の音楽フォルダーの場所が安全ではありません',
  TrackUnreadable: '読めない音楽ファイルがありました',
  DuplicateContent: '同じ音楽ファイルが重複していました',
  SourceChangedDuringScan: '確認中に音楽ファイルが変わりました',
  NoEligibleTracks: '使える音楽がありませんでした',
  CatalogUnavailable: '音楽の一覧を作れませんでした',
  UnsupportedCueKind: 'BGM を付けられない場面がありました',
  SpeechOverlap: '話し声と重なるため BGM を付けませんでした',
  DialogueMusicDisabled: '会話中の BGM は付けない設定です',
  NoTrackLongEnough: '長さの足りる音楽がありませんでした',
  NoEligibleNonDialogueCues: 'BGM を付けられる場面がありませんでした',
  UntaggedSourceColorFallback: '元動画の色情報がないため標準の色で作成しました',
  HdrToneMappedToSdrBt709NotQualityApproval: 'HDR の元動画を通常の色に変換しました（見え方は未確認）',
  MusicLibraryUnavailableAtRenderCompletedWithoutMusic: '音楽を読めなかったため BGM なしで作成しました',
  SilentAudioLoudnessNotMeasurable: '無音のため音量を測れませんでした',
  'NOT EVALUATED': '字幕が内容をすべて伝えているかは未評価です',
  'platform-ui-mask-not-measured': 'SNS の操作ボタンとの重なりは未確認です',
  'opaque-setting-explicitly-overridden-by-transparent-background-request': '字幕の背景は透明にしました',
  'line-height-rounded-inward-to-pixels': '字幕の大きさを画面に合わせて調整しました',
  'source-gaps-preserved-instead-of-retimed': '字幕は話している時間だけ表示しました',
  'cue-ends-may-extend-into-following-silence-starts-never-move': '字幕は話し終わった後も少し長く表示します',
  'cue-end-extended-into-following-silence': '読みやすいよう、字幕を話し終わった後も少し長く表示しました',
  'shown-below-minimum-duration': '一部の字幕は表示時間が短めです',
  'shown-above-maximum-reading-speed': '早口の部分は、字幕を読む速さの目安を超えています',
  'shown-with-gap-below-minimum': '一部の字幕は間を空けずに続けて表示しました',
  'untimed-token-shown-between-timed-neighbours': '時刻が分からない言葉は前後の字幕と一緒に表示しました',
  'cue-boundary-whitespace-trimmed': '字幕の前後の空白を除きました',
  'no-real-asr-cues': '話し声を認識できなかったため字幕はありません',
  'disabled-by-settings': '字幕を付けない設定です',
  'outside-output-cuts': 'カットした部分の字幕は表示していません',
}

/** Internal reason codes that are not listed fall back to a plain, non-technical sentence. */
export function generationCodeLabel(code: string): string {
  return generationCodeLabels[code] ?? '一部の字幕・素材を表示できませんでした'
}

/** Groups identical (role, message) pairs so repeated internal records read as one line with a count. */
export function summarizeGenerationNotes(items: readonly { readonly role: string, readonly code: string }[]): { readonly text: string, readonly count: number }[] {
  const counts = new Map<string, number>()
  for (const item of items) {
    const text = `${generationRoleLabel(item.role)}: ${generationCodeLabel(item.code)}`
    counts.set(text, (counts.get(text) ?? 0) + 1)
  }
  return [...counts].map(([text, count]) => ({ text, count }))
}
