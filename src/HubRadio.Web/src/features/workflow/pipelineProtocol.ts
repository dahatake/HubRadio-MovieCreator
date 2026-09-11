import { BRIDGE_PROTOCOL_VERSION, isCanonicalBridgeId } from '../../bridge/protocol'

// Web projection of SourceOnlyResultCodec.ToDto, NOT its persistence JSON (which
// contains native-only recovery locators). Keep enum casing from that codec.
type Reader<T> = (value: unknown) => T
type Shape = Readonly<Record<string, Reader<unknown>>>
type Decoded<S extends Shape> = { readonly [K in keyof S]: ReturnType<S[K]> }
const safe = Number.MAX_SAFE_INTEGER
const int32 = 2_147_483_647
export const PIPELINE_MAX_RESPONSE_BYTES = 8 * 1024 * 1024 + 16 * 1024
export const PIPELINE_METHODS = ['pipeline.read', 'pipeline.regenerate', 'pipeline.retry', 'pipeline.cancel'] as const
export const PIPELINE_PHASES = [
  'Queued', 'Ingesting', 'Analyzing', 'Composing', 'Rendering', 'Evaluating', 'Delivering',
  'Completed', 'Failed', 'Canceled', 'ReselectionRequired', 'ReconciliationRequired',
] as const
export type PipelinePhase = typeof PIPELINE_PHASES[number]
export type PipelineMethod = typeof PIPELINE_METHODS[number]
export const SOURCE_ONLY_ROLES = [
  'LandscapeClean', 'LandscapeBurnIn', 'VerticalBurnIn', 'SubRip', 'WebVtt', 'LandscapeThumbnail', 'VerticalThumbnail',
] as const
export const EVALUATION_RULES = [
  'codec', 'fullDecode', 'duration', 'avSync', 'subtitlePresence', 'subtitleReadingSpeed',
  'subtitleLineLength', 'subtitleLineCount', 'subtitleTiming', 'subtitleSafeZone', 'loudness',
  'truePeak', 'provenance', 'settingsBinding', 'keyboard', 'focus', 'contrast', 'accessibilityTree',
] as const
export const EVALUATION_LIMITATIONS = [
  'subtitleSemanticCompleteness', 'nonSpeechSoundCoverage', 'speakerInformationCoverage',
  'mainContentUnderstanding', 'humanReadability', 'naturalness', 'perceivedAudioQuality', 'emotionalFit', 'legalCompliance',
] as const
const failureCodes = [
  'GenerationFailed', 'GenerationCanceled', 'GenerationReconciliationRequired', 'NoArtifacts', 'PrimaryArtifactMissing',
  'BindingMismatch', 'SettingsBindingMismatch', 'VersionNotPersisted', 'ArtifactValidationFailed', 'QualityControlFailed',
  'Canceled', 'EvidencePersistenceFailed', 'EvaluationPersistenceFailed', 'WorkflowConflict', 'StoredEvidenceInvalid',
  'DeliveryNotValidated', 'EvidencePromotionReconciliationRequired',
] as const
const evidenceKinds = ['Graph', 'Manifest', 'RenderingInputs', 'QualityControl', 'DeliveryReceipt'] as const
const evaluationStatuses = ['PASS', 'WARNING', 'FAIL', 'NOT EVALUATED'] as const
export type EvaluationStatus = typeof evaluationStatuses[number]

function generationRole(value: unknown): string {
  requireValid(typeof value === 'string' && value.length <= 128)
  if (['plan', 'music', 'video', 'landscape-subtitles', 'human-evaluation', 'landscape-clean', 'landscape-burn-in', 'landscape-srt', 'landscape-vtt'].includes(value)) return value
  const prefix = ['vertical-subtitles-', 'vertical-burn-in-', 'thumbnail-landscape-', 'thumbnail-vertical-'].find(p => value.startsWith(p))
  requireValid(prefix !== undefined && isCanonicalBridgeId(value.slice(prefix.length)))
  return value
}

// Exact SourceOnlyResultCodec producer vocabulary, not a free-text/status reader.
// Preserve reason bytes in generation evidence and projection; never turn NOT EVALUATED into PASS.
const generationWarningCodes: readonly string[] = [
  'SubjectRegionUnavailable', 'FixedLayoutFallback', 'BlurredBackgroundFallback',
  'PlatformUiUnmeasured', 'CandidateLimitApplied', 'ThumbnailQualityNotEvaluated',
  'LibraryAbsent', 'LibraryEmpty', 'LibraryInaccessible', 'UnsafeLibraryLocation',
  'TrackUnreadable', 'DuplicateContent', 'SourceChangedDuringScan', 'NoEligibleTracks',
  'CatalogUnavailable', 'UnsupportedCueKind', 'SpeechOverlap', 'DialogueMusicDisabled', 'NoTrackLongEnough',
  'NoEligibleNonDialogueCues', 'UntaggedSourceColorFallback', 'HdrToneMappedToSdrBt709NotQualityApproval',
  'MusicLibraryUnavailableAtRenderCompletedWithoutMusic', 'SilentAudioLoudnessNotMeasurable',
  'no-real-asr-cues', 'unresolved-output-source-span', 'disabled-by-settings', 'missing-token-timing',
  'unmatched-text-without-token-timing', 'overlapping-token-timing', 'cut-crosses-token-without-inner-timing',
  'outside-output-cuts', 'token-too-long-without-inner-timing', 'unsupported-source-display-text',
  'text-cannot-split-without-inner-token-timing', 'renderer-cue-limit', 'no-versioned-safe-zone', 'layout-not-resolved',
  'no-displayable-source-text', 'no-grapheme-safe-token-partition', 'line-too-long-without-feasible-token-partition',
  'too-many-source-lines', 'duration-too-long-without-feasible-token-partition', 'source-duration-below-minimum',
  'source-reading-speed-exceeded', 'no-feasible-partition-with-source-gaps', 'no-feasible-exact-token-partition',
  'cue-boundary-whitespace-trimmed', 'output-cues-overlap', 'output-gap-below-minimum-without-retiming',
  'line-height-rounded-inward-to-pixels', 'opaque-setting-explicitly-overridden-by-transparent-background-request',
  'platform-ui-mask-not-measured', 'NOT EVALUATED', 'source-gaps-preserved-instead-of-retimed',
]
function generationWarningCode(value: unknown): string {
  requireValid(typeof value === 'string' && value.length > 0 && value.length <= 128 && generationWarningCodes.includes(value))
  return value
}

function requireValid(condition: unknown): asserts condition {
  if (!condition) throw new TypeError('Invalid pipeline contract.')
}
function object<S extends Shape>(shape: S): Reader<Decoded<S>> {
  const keys = Object.keys(shape)
  return value => {
    requireValid(typeof value === 'object' && value !== null && !Array.isArray(value))
    const record = value as Record<string, unknown>
    requireValid(Object.keys(record).length === keys.length && keys.every(key => Object.hasOwn(record, key)))
    return Object.freeze(Object.fromEntries(keys.map(key => [key, shape[key]!(record[key])]))) as Decoded<S>
  }
}
function enumeration<const T extends readonly (string | number)[]>(values: T): Reader<T[number]> {
  return value => { requireValid(values.includes(value as T[number])); return value as T[number] }
}
function nullable<T>(read: Reader<T>): Reader<T | null> { return value => value === null ? null : read(value) }
function array<T>(read: Reader<T>, maximum: number): Reader<readonly T[]> {
  return value => {
    requireValid(Array.isArray(value) && value.length <= maximum)
    return Object.freeze(Array.from(value, read))
  }
}
function integer(minimum = 0, maximum = int32): Reader<number> {
  return value => {
    requireValid(typeof value === 'number' && Number.isSafeInteger(value) && !Object.is(value, -0) && value >= minimum && value <= maximum)
    return value
  }
}
function decimal(minimum = -safe, maximum = safe): Reader<number> {
  return value => {
    requireValid(typeof value === 'number' && Number.isFinite(value) && value >= minimum && value <= maximum)
    return value
  }
}
function text(pattern: RegExp, maximum: number): Reader<string> {
  return value => {
    requireValid(typeof value === 'string' && value.length > 0 && value.length <= maximum && pattern.test(value))
    return value
  }
}
const id: Reader<string> = value => {
  requireValid(typeof value === 'string' && isCanonicalBridgeId(value))
  return value
}
const digest = text(/^[0-9a-f]{64}$(?![\s\S])/, 64)
const code = text(/^[a-z][a-z0-9]*(?:_[a-z0-9]+)*(?:\.[a-z][a-z0-9]*(?:_[a-z0-9]+)*)+$(?![\s\S])/, 128)
const settingKey = text(/^[a-z][a-zA-Z0-9]*(?:\.[a-z][a-zA-Z0-9]*)*$(?![\s\S])/, 128)
const bool: Reader<boolean> = value => { requireValid(typeof value === 'boolean'); return value }
const version: Reader<string> = value => {
  const result = text(/^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$(?![\s\S])/, 32)(value)
  requireValid(result.split('.').every(part => Number(part) <= int32))
  return result
}
const timestamp: Reader<string> = value => {
  const result = text(/^(?!0000)\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00$(?![\s\S])/, 33)(value)
  const milliseconds = `${result.slice(0, 23)}Z`
  requireValid(new Date(milliseconds).toISOString() === milliseconds)
  return result
}
const timeShape = object({ value: integer(-safe, safe), rate: integer(1, safe) })
export type PipelineTime = ReturnType<typeof timeShape>
const time: Reader<PipelineTime> = value => {
  const result = timeShape(value)
  let a = BigInt(Math.abs(result.value)); let b = BigInt(result.rate)
  while (b !== 0n) { const remainder = a % b; a = b; b = remainder }
  requireValid(a === 1n)
  return result
}
function compareTime(a: PipelineTime, b: PipelineTime): bigint {
  return BigInt(a.value) * BigInt(b.rate) - BigInt(b.value) * BigInt(a.rate)
}
function sameTime(a: PipelineTime | null, b: PipelineTime | null): boolean {
  return a === null || b === null ? a === b : a.value === b.value && a.rate === b.rate
}
function offsetEquals(offset: PipelineTime, a: PipelineTime, b: PipelineTime): boolean {
  return BigInt(offset.value) * BigInt(a.rate) * BigInt(b.rate) ===
    (BigInt(a.value) * BigInt(b.rate) - BigInt(b.value) * BigInt(a.rate)) * BigInt(offset.rate)
}
function unique<T>(values: readonly T[], key: (value: T) => string): void {
  requireValid(new Set(values.map(key)).size === values.length)
}

const reference = object({ artifactId: id, contentDigest: digest, byteSize: integer(1, safe), kind: enumeration(['Original', 'Render', 'Evidence', 'Delivery'] as const) })
const evidence = object({ kind: enumeration(evidenceKinds), artifact: reference })
const binding = object({ projectId: id, executionId: id, rootJobId: id, settingsSnapshotDigest: digest, versionId: id, versionDigest: digest })
export type SourceOnlyResultBinding = ReturnType<typeof binding>
const frame = object({
  sourceAssetId: id, sourceSpanId: id, sourceDigest: digest, sourcePtsIndexDigest: digest,
  sourceFrameSequence: integer(), sourcePresentationTime: time, sourceFrameDuration: time,
  sourceWidth: integer(1), sourceHeight: integer(1), width: integer(2, 4096), height: integer(2, 4096),
  extractionRule: enumeration(['decode-original-select-source-sequence-contain-v1'] as const),
})
const measuredValue = object({ kind: enumeration(['Finite', 'NegativeInfinity', 'PositiveInfinity'] as const), value: nullable(decimal()) })
const cue = object({ cueId: id, start: time, duration: time, characterCount: integer(0, 32768), maximumLineCharacters: integer(0, 32768), lineCount: integer(1, 1024), safeZoneSatisfied: bool })
const measurements = object({
  expectedDuration: time, videoStart: time, videoEnd: time, audioStart: nullable(time), audioEnd: nullable(time),
  videoFrameCount: integer(1), audioFrameCount: nullable(integer(1)), probeDigest: digest, videoTimingDigest: digest,
  audioTimingDigest: nullable(digest), subtitleTimingDigest: nullable(digest), lumaDigest: digest, audioDigest: nullable(digest),
  minimumLuma: decimal(0, 255), maximumLuma: decimal(0, 255), meanLuma: decimal(0, 255),
  loudness: nullable(measuredValue), truePeak: nullable(measuredValue), decodedSuccessfully: bool,
  cues: array(cue, 256), videoTimeBase: time, audioTimeBase: nullable(time), videoTimingContinuous: bool,
  videoFrameCadenceExact: bool, outputDigitalSilence: bool, audioVideoStartOffset: nullable(time), audioVideoEndOffset: nullable(time),
})

const warningTarget = object({
  kind: enumeration(['Preview', 'Settings', 'Evidence', 'Editor'] as const), projectId: id, versionId: id, versionDigest: digest,
  artifactId: nullable(id), artifactDigest: nullable(digest), timeBasis: nullable(enumeration(['Source', 'Output'] as const)),
  time: nullable(time), settingKey: nullable(settingKey), editorKind: nullable(enumeration(['Timeline', 'Subtitles', 'Audio', 'Layout', 'Metadata'] as const)),
})
export type SourceOnlyResultTarget = ReturnType<typeof warningTarget>
const warning = object({ id: text(/^[0-9a-f-]+:[a-zA-Z]+$(?![\s\S])/, 128), ruleId: enumeration(EVALUATION_RULES), status: enumeration(['Warning', 'Fail'] as const), reasonCode: code, target: warningTarget })
export type SourceOnlyResultWarning = ReturnType<typeof warning>
const evaluationTarget = object({
  kind: enumeration(['preview', 'settings', 'evidence', 'editor'] as const), projectId: id,
  compositionVersionId: id, compositionVersionDigest: digest, artifactId: nullable(id), artifactDigest: nullable(digest),
  timeBasis: nullable(enumeration(['source', 'output'] as const)), time: nullable(time), settingKey: nullable(settingKey),
  editorKind: nullable(enumeration(['timeline', 'subtitles', 'audio', 'layout', 'metadata'] as const)),
})
const evaluationRule = object({
  ruleId: enumeration([...EVALUATION_RULES, ...EVALUATION_LIMITATIONS] as const), category: enumeration(['machine', 'humanLimitation'] as const),
  scope: enumeration(['artifact', 'userInterface', 'humanReview'] as const), status: enumeration(evaluationStatuses),
  reasonCode: code, evidenceDigest: nullable(digest), target: nullable(evaluationTarget),
})
const evaluation = object({
  schemaVersion: enumeration(['1.0.0'] as const), evaluationId: id, jobId: id, projectId: id,
  compositionVersionId: id, compositionVersionDigest: digest, artifactId: id, artifactDigest: digest,
  settingsProfileId: id, settingsProfileVersion: version, settingsProfileDigest: digest, settingsSnapshotDigest: digest,
  ruleSetVersion: enumeration(['2.0.0'] as const), environmentVersion: enumeration(['source-only.output-evaluation.v2'] as const),
  environmentDigest: digest, toolVersionsDigest: digest, modelVersionsDigest: nullable(digest), templateDigest: nullable(digest), musicDigest: nullable(digest),
  startedAt: timestamp, completedAt: timestamp, evaluatedAt: timestamp, inputDigest: digest, outputDigest: digest,
  aggregateStatus: enumeration(evaluationStatuses), statusCounts: object({ pass: integer(0, 27), warning: integer(0, 27), fail: integer(0, 27), notEvaluated: integer(0, 27) }),
  rules: array(evaluationRule, 18), limitations: array(evaluationRule, 9),
})
const artifact = object({
  role: enumeration(SOURCE_ONLY_ROLES), renditionId: nullable(id), artifact: reference,
  mediaType: enumeration(['video/mp4', 'image/png', 'application/x-subrip', 'text/vtt'] as const), versionId: id, versionDigest: digest,
  source: reference, graphDigest: digest, manifestDigest: nullable(digest), evidence: array(evidence, 4),
  evaluation: nullable(evaluation), evaluationDigest: nullable(digest), measurements: nullable(measurements), failureCode: nullable(enumeration(failureCodes)),
  qcFailureReason: nullable(enumeration([
    'GraphInvalid', 'ManifestBindingMismatch', 'SubtitleEvidenceInvalid', 'RegisteredArtifactInvalid', 'ArtifactBindingMismatch',
    'ToolVerificationFailed', 'ProcessTimedOut', 'ProcessFailed', 'OutputTruncated', 'InvalidEncoding', 'ProbeOutputInvalid',
    'ContainerMismatch', 'StreamTopologyMismatch', 'StreamMetadataMismatch', 'TimingOutputInvalid', 'NonMonotonicTiming',
    'DecodeFailed', 'LumaOutputInvalid', 'LoudnessOutputInvalid',
  ] as const)),
  qcFailureStage: nullable(enumeration(['Evidence', 'Probe', 'VideoTiming', 'AudioTiming', 'SubtitleTiming', 'Decode', 'ScreenLuma', 'AudioLoudness'] as const)),
  warnings: array(warning, 18), delivered: bool, delivery: nullable(object({ artifact: reference, receipt: evidence })),
  thumbnailPlanId: nullable(id), frameEvidence: nullable(frame),
  qcApplicability: enumeration(['VideoOutputQc', 'SubtitleFileOnly', 'ImageFileOnly'] as const), profileId: nullable(id), profileDigest: nullable(digest),
})
export type SourceOnlyResultArtifact = ReturnType<typeof artifact>
const resultShape = object({
  schemaVersion: enumeration(['source-only.result.v2'] as const), binding,
  generationDisposition: enumeration(['Completed', 'Failed', 'Cancelled', 'ReconciliationRequired', 'GeneratedWithOmissions'] as const),
  artifacts: array(artifact, 16),
  omissions: array(object({
    role: enumeration(['ShortForm', 'Thumbnail', 'LandscapeSubtitles', 'VerticalSubtitles', 'VerticalCallToAction', 'PreRollMargin', 'Opening', 'ProgramName', 'EpisodeNumber', 'EpisodeTitle', 'PerformerIntroduction', 'PublicationScopeIndicator', 'Body', 'Topic', 'Summary', 'CallToAction', 'Ending', 'PostRollMargin', 'Other'] as const),
    reason: enumeration(['NoGroundedCandidate', 'NoAdditionalGroundedCandidate', 'NoMeasuredSourceFrame', 'NoAdditionalMeasuredSourceFrame', 'NoDisplayCues', 'UntimedPlanMetadataNotRendered', 'ThumbnailRendererNotConnected', 'CallToActionRendererNotConnected', 'UnrecognizedOmission'] as const), renditionId: nullable(id),
  }), 128),
  failures: array(object({ code: enumeration(failureCodes), artifactId: nullable(id), promotionArtifact: nullable(reference), evidenceKind: nullable(enumeration(evidenceKinds)) }), 64),
  reconciliationArtifacts: array(reference, 80),
  transcriptReferences: array(object({ revisionId: id, revisionDigest: digest, sourceArtifactId: id }), 8),
  primaryWorkflow: nullable(object({ artifactId: id, revision: integer(1), canonicalDigest: digest, state: enumeration(['Ready', 'ReadyWithWarnings', 'Failed'] as const), evaluationId: id })),
  stagingCleanupRequired: bool,
  generationWarnings: array(object({ role: generationRole, code: generationWarningCode }), 256),
})
export type SourceOnlyResultDto = ReturnType<typeof resultShape>

function validateTarget(target: SourceOnlyResultTarget, a: SourceOnlyResultArtifact, b: SourceOnlyResultBinding): void {
  requireValid(target.projectId === b.projectId && target.versionId === a.versionId && target.versionDigest === a.versionDigest &&
    target.artifactId === a.artifact.artifactId && target.artifactDigest === a.artifact.contentDigest)
  requireValid((target.kind === 'Preview') === (target.time !== null && target.timeBasis !== null))
  if (target.kind !== 'Preview') requireValid(target.time === null && target.timeBasis === null)
  requireValid((target.kind === 'Settings') === (target.settingKey !== null) && (target.kind === 'Editor') === (target.editorKind !== null))
  if (target.time !== null) {
    requireValid(target.time.value >= 0)
    if (target.timeBasis === 'Output' && a.measurements !== null) requireValid(compareTime(target.time, a.measurements.expectedDuration) < 0n)
  }
}
function fromEvaluationTarget(t: ReturnType<typeof evaluationTarget>): SourceOnlyResultTarget {
  const kinds = { preview: 'Preview', settings: 'Settings', evidence: 'Evidence', editor: 'Editor' } as const
  const editors = { timeline: 'Timeline', subtitles: 'Subtitles', audio: 'Audio', layout: 'Layout', metadata: 'Metadata' } as const
  return { kind: kinds[t.kind], projectId: t.projectId, versionId: t.compositionVersionId, versionDigest: t.compositionVersionDigest,
    artifactId: t.artifactId, artifactDigest: t.artifactDigest, timeBasis: t.timeBasis === null ? null : t.timeBasis === 'source' ? 'Source' : 'Output',
    time: t.time, settingKey: t.settingKey, editorKind: t.editorKind === null ? null : editors[t.editorKind] }
}
function targetsEqual(a: SourceOnlyResultTarget, b: SourceOnlyResultTarget): boolean {
  return a.kind === b.kind && a.projectId === b.projectId && a.versionId === b.versionId && a.versionDigest === b.versionDigest &&
    a.artifactId === b.artifactId && a.artifactDigest === b.artifactDigest && a.timeBasis === b.timeBasis && sameTime(a.time, b.time) &&
    a.settingKey === b.settingKey && a.editorKind === b.editorKind
}
function validateMeasurements(m: NonNullable<SourceOnlyResultArtifact['measurements']>): void {
  requireValid(m.expectedDuration.value > 0 && compareTime(m.videoEnd, m.videoStart) > 0n && m.videoTimeBase.value > 0 &&
    m.minimumLuma <= m.meanLuma && m.meanLuma <= m.maximumLuma)
  const hasAudio = m.audioStart !== null
  for (const field of [m.audioEnd, m.audioFrameCount, m.audioTimingDigest, m.audioDigest, m.loudness, m.truePeak, m.audioTimeBase, m.audioVideoStartOffset, m.audioVideoEndOffset]) requireValid(hasAudio === (field !== null))
  if (m.audioStart !== null && m.audioEnd !== null && m.audioTimeBase !== null && m.audioVideoStartOffset !== null && m.audioVideoEndOffset !== null) {
    requireValid(compareTime(m.audioEnd, m.audioStart) > 0n && m.audioTimeBase.value > 0 &&
      offsetEquals(m.audioVideoStartOffset, m.audioStart, m.videoStart) && offsetEquals(m.audioVideoEndOffset, m.audioEnd, m.videoEnd))
  }
  for (const v of [m.loudness, m.truePeak]) if (v !== null) requireValid((v.kind === 'Finite') === (v.value !== null))
  requireValid(m.outputDigitalSilence === (hasAudio && m.loudness?.kind === 'NegativeInfinity' && m.truePeak?.kind === 'NegativeInfinity'))
  unique(m.cues, c => c.cueId)
  for (const c of m.cues) requireValid(c.start.value >= 0 && c.duration.value > 0)
}
function validateEvaluation(a: SourceOnlyResultArtifact, b: SourceOnlyResultBinding): void {
  const e = a.evaluation
  requireValid((e === null) === (a.evaluationDigest === null))
  if (e === null) { requireValid(a.warnings.length === 0); return }
  requireValid(e.projectId === b.projectId && e.jobId === b.rootJobId && e.settingsSnapshotDigest === b.settingsSnapshotDigest &&
    e.compositionVersionId === a.versionId && e.compositionVersionDigest === a.versionDigest &&
    e.artifactId === a.artifact.artifactId && e.artifactDigest === a.artifact.contentDigest && e.outputDigest === a.artifact.contentDigest &&
    e.startedAt <= e.completedAt && e.completedAt <= e.evaluatedAt && e.rules.length === 18 && e.limitations.length === 9)
  const qc = a.evidence.find(item => item.kind === 'QualityControl')
  requireValid(qc !== undefined)
  e.rules.forEach((r, index) => {
    requireValid(r.ruleId === EVALUATION_RULES[index] && r.category === 'machine' && r.scope === (index < 14 ? 'artifact' : 'userInterface'))
    requireValid(r.reasonCode.startsWith('source_only.') || (r.ruleId === 'avSync' && r.status === 'WARNING' && r.reasonCode === 'timing.numeric_tolerance_unapproved'))
    if (index >= 14 || (a.mediaType !== 'video/mp4' && ['duration', 'avSync', 'loudness', 'truePeak', 'fullDecode'].includes(r.ruleId))) requireValid(r.status === 'NOT EVALUATED')
    if (r.status === 'NOT EVALUATED') requireValid(r.evidenceDigest === null && r.target === null)
    else requireValid(r.evidenceDigest === qc.artifact.contentDigest)
    if (r.status === 'PASS') requireValid(r.target === null)
    if (r.status === 'WARNING' || r.status === 'FAIL') requireValid(r.target !== null)
    if (r.target !== null) validateTarget(fromEvaluationTarget(r.target), a, b)
  })
  e.limitations.forEach((r, index) => requireValid(r.ruleId === EVALUATION_LIMITATIONS[index] && r.category === 'humanLimitation' &&
    r.scope === 'humanReview' && r.status === 'NOT EVALUATED' && r.reasonCode === 'evaluation.human_review_required' && r.evidenceDigest === null && r.target === null))
  const all = [...e.rules, ...e.limitations]
  for (const [key, status] of [['pass', 'PASS'], ['warning', 'WARNING'], ['fail', 'FAIL'], ['notEvaluated', 'NOT EVALUATED']] as const) requireValid(e.statusCounts[key] === all.filter(r => r.status === status).length)
  requireValid(e.aggregateStatus === aggregateEvaluation(e.rules.map(r => r.status)))
  const expected = e.rules.filter(r => r.status === 'WARNING' || r.status === 'FAIL')
  requireValid(a.warnings.length === expected.length)
  a.warnings.forEach((w, index) => {
    const r = expected[index]!
    requireValid(w.id === `${a.artifact.artifactId}:${r.ruleId}` && w.ruleId === r.ruleId &&
      w.status === (r.status === 'WARNING' ? 'Warning' : 'Fail') && w.reasonCode === r.reasonCode && r.target !== null &&
      targetsEqual(w.target, fromEvaluationTarget(r.target)))
  })
}
export function aggregateEvaluation(statuses: readonly EvaluationStatus[]): EvaluationStatus {
  return statuses.includes('FAIL') ? 'FAIL' : statuses.length === 0 || statuses.includes('NOT EVALUATED') ? 'NOT EVALUATED'
    : statuses.includes('WARNING') ? 'WARNING' : 'PASS'
}
function readResult(value: unknown): SourceOnlyResultDto {
  const r = resultShape(value)
  unique(r.artifacts, a => a.artifact.artifactId)
  unique(r.artifacts, a => `${a.role}:${a.renditionId}:${a.thumbnailPlanId}`)
  unique(r.reconciliationArtifacts, a => a.artifactId)
  unique(r.transcriptReferences, t => t.revisionId)
  requireValid(r.artifacts.filter(a => a.role === 'VerticalBurnIn').length <= 3)
  const versions = new Map([[r.binding.versionId, r.binding.versionDigest]])
  const references = new Map<string, ReturnType<typeof reference>>()
  function remember(ref: ReturnType<typeof reference>): void {
    const previous = references.get(ref.artifactId)
    requireValid(previous === undefined || (previous.contentDigest === ref.contentDigest && previous.byteSize === ref.byteSize && previous.kind === ref.kind))
    references.set(ref.artifactId, ref)
  }
  for (const a of r.artifacts) {
    const image = a.role === 'LandscapeThumbnail' || a.role === 'VerticalThumbnail'
    const video = a.role === 'LandscapeClean' || a.role === 'LandscapeBurnIn' || a.role === 'VerticalBurnIn'
    requireValid(a.mediaType === (video ? 'video/mp4' : image ? 'image/png' : a.role === 'SubRip' ? 'application/x-subrip' : 'text/vtt') &&
      a.qcApplicability === (video ? 'VideoOutputQc' : image ? 'ImageFileOnly' : 'SubtitleFileOnly') &&
      (a.role === 'VerticalBurnIn') === (a.renditionId !== null) && image === (a.thumbnailPlanId !== null) &&
      (a.frameEvidence === null || image) && (a.profileId === null) === (a.profileDigest === null) &&
      a.artifact.kind === 'Render' && a.source.kind === 'Original' && a.artifact.artifactId !== a.source.artifactId)
    if (!image && a.role !== 'VerticalBurnIn') requireValid(a.versionId === r.binding.versionId && a.versionDigest === r.binding.versionDigest)
    requireValid(!versions.has(a.versionId) || versions.get(a.versionId) === a.versionDigest)
    versions.set(a.versionId, a.versionDigest)
    remember(a.artifact); remember(a.source)
    unique(a.evidence, e => e.kind)
    for (const e of a.evidence) { requireValid(e.kind !== 'DeliveryReceipt' && e.artifact.kind === 'Evidence'); remember(e.artifact) }
    if (a.manifestDigest === null) requireValid(a.failureCode !== null && a.evidence.length === 0 && a.evaluation === null)
    requireValid((a.qcFailureReason === null) === (a.qcFailureStage === null) && (a.qcFailureReason === null || a.measurements === null))
    if (a.measurements !== null) { requireValid(video); validateMeasurements(a.measurements) }
    const f = a.frameEvidence
    if (f !== null) requireValid(f.sourceDigest === a.source.contentDigest && f.sourcePresentationTime.value >= 0 && f.sourceFrameDuration.value > 0 && f.width % 2 === 0 && f.height % 2 === 0)
    validateEvaluation(a, r.binding)
    if (a.failureCode === null) requireValid(a.evaluation !== null && a.evidence.length === 4 && a.profileId !== null &&
      (!video || a.measurements !== null) && (!image || f !== null))
    requireValid(a.delivered === (a.delivery !== null))
    if (a.delivery !== null) requireValid(a.failureCode === null && a.evaluation !== null &&
      !a.evaluation.rules.some(rule => rule.status === 'FAIL') && a.delivery.artifact.kind === 'Delivery' &&
      a.delivery.receipt.kind === 'DeliveryReceipt' && a.delivery.receipt.artifact.kind === 'Evidence' &&
      a.delivery.artifact.contentDigest === a.artifact.contentDigest && a.delivery.artifact.byteSize === a.artifact.byteSize)
    if (a.delivery !== null) {
      remember(a.delivery.artifact); remember(a.delivery.receipt.artifact)
      const requiredRules = video ? ['codec', 'fullDecode', 'duration', 'provenance', 'settingsBinding'] : ['codec', 'provenance', 'settingsBinding']
      requireValid(requiredRules.every(key => a.evaluation?.rules.find(rule => rule.ruleId === key)?.status === 'PASS'))
    }
  }
  for (const o of r.omissions) requireValid((o.role === 'VerticalCallToAction' || o.role === 'VerticalSubtitles') === (o.renditionId !== null))
  for (const a of r.reconciliationArtifacts) { requireValid(a.kind === 'Render' || a.kind === 'Evidence' || a.kind === 'Delivery'); remember(a) }
  for (const f of r.failures) {
    if (f.promotionArtifact === null) requireValid(f.evidenceKind === null)
    else requireValid(f.code === 'EvidencePromotionReconciliationRequired' && f.promotionArtifact.kind === 'Evidence' &&
      f.evidenceKind !== null && f.evidenceKind !== 'DeliveryReceipt' && r.reconciliationArtifacts.some(a =>
        a.artifactId === f.promotionArtifact!.artifactId && a.contentDigest === f.promotionArtifact!.contentDigest && a.byteSize === f.promotionArtifact!.byteSize && a.kind === f.promotionArtifact!.kind))
  }
  if (r.primaryWorkflow !== null) {
    const w = r.primaryWorkflow
    const primary = r.artifacts.find(a => a.artifact.artifactId === w.artifactId)
    requireValid(primary?.role === 'LandscapeClean' && primary.evaluation?.evaluationId === w.evaluationId)
    const rules = primary.evaluation.rules
    const blocked = rules.some(rule => rule.status === 'FAIL') || ['codec', 'fullDecode', 'duration', 'provenance', 'settingsBinding'].some(key => rules.find(rule => rule.ruleId === key)?.status !== 'PASS')
    requireValid(w.state === (blocked ? 'Failed' : rules.some(rule => rule.status !== 'PASS') ? 'ReadyWithWarnings' : 'Ready'))
  }
  return r
}

const snapshotShape = object({
  projectId: id, executionId: id, revision: integer(), phase: enumeration(PIPELINE_PHASES), rootJobId: id, ingestJobId: id,
  settingsSnapshotDigest: digest, versionId: nullable(id), versionDigest: nullable(digest), parentVersionId: nullable(id), parentDigest: nullable(digest),
  resultStatus: enumeration(['none', 'valid', 'invalid'] as const), result: nullable(readResult), previousResult: nullable(readResult), failureCode: nullable(code),
})
export type PipelineSnapshot = ReturnType<typeof snapshotShape>
const readSnapshot: Reader<PipelineSnapshot> = value => {
  const s = snapshotShape(value)
  requireValid(s.rootJobId !== s.ingestJobId && (s.versionId === null) === (s.versionDigest === null) &&
    (s.parentVersionId === null) === (s.parentDigest === null) &&
    (s.resultStatus !== 'valid' || s.result !== null) && (s.resultStatus !== 'none' || s.result === null))
  if (s.versionId !== null && s.parentVersionId !== null) requireValid(s.versionId !== s.parentVersionId)
  if (s.result !== null) {
    const b = s.result.binding
    requireValid(b.projectId === s.projectId && b.executionId === s.executionId && b.rootJobId === s.rootJobId &&
      b.settingsSnapshotDigest === s.settingsSnapshotDigest && b.versionId === s.versionId && b.versionDigest === s.versionDigest)
  }
  if (s.previousResult !== null) {
    const b = s.previousResult.binding
    // The native projection has revalidated actual files for this exact parent.
    // No arbitrary cached execution becomes a parent by having the same project.
    requireValid(b.projectId === s.projectId && b.executionId !== s.executionId && b.rootJobId !== s.rootJobId &&
      b.versionId === s.parentVersionId && b.versionDigest === s.parentDigest)
  }
  return s
}
const readParams = object({ projectId: id })
const executionParams = object({ projectId: id, executionId: id, expectedRevision: integer() })
const regenerationParams = object({ projectId: id, expectedVersionId: id, expectedVersionDigest: digest, expectedSettingsRevision: integer(), expectedSettingsSnapshotDigest: digest })
export type PipelineRegenerationParams = ReturnType<typeof regenerationParams>
type Request<M extends PipelineMethod, P> = Readonly<{ version: typeof BRIDGE_PROTOCOL_VERSION; requestId: string; method: M; params: P }>
export type PipelineRequest = Request<'pipeline.read', ReturnType<typeof readParams>> |
  Request<'pipeline.retry' | 'pipeline.cancel', ReturnType<typeof executionParams>> | Request<'pipeline.regenerate', PipelineRegenerationParams>
export function createPipelineReadRequest(requestId: string, projectId: string): Extract<PipelineRequest, { method: 'pipeline.read' }> {
  return Object.freeze({ version: BRIDGE_PROTOCOL_VERSION, requestId: id(requestId), method: 'pipeline.read', params: readParams({ projectId }) })
}
export function createPipelineRegenerateRequest(requestId: string, params: PipelineRegenerationParams): Extract<PipelineRequest, { method: 'pipeline.regenerate' }> {
  return Object.freeze({ version: BRIDGE_PROTOCOL_VERSION, requestId: id(requestId), method: 'pipeline.regenerate', params: regenerationParams(params) })
}
export function createPipelineExecutionRequest(requestId: string, method: 'pipeline.retry' | 'pipeline.cancel', params: ReturnType<typeof executionParams>): Extract<PipelineRequest, { method: 'pipeline.retry' | 'pipeline.cancel' }> {
  requireValid(method === 'pipeline.retry' || method === 'pipeline.cancel')
  return Object.freeze({ version: BRIDGE_PROTOCOL_VERSION, requestId: id(requestId), method, params: executionParams(params) })
}
export function parsePipelineRequest(value: unknown): PipelineRequest | null {
  try {
    const envelope = object({ version: enumeration([BRIDGE_PROTOCOL_VERSION] as const), requestId: id, method: enumeration(PIPELINE_METHODS), params: (v: unknown) => v })(value)
    if (envelope.method === 'pipeline.read') return createPipelineReadRequest(envelope.requestId, readParams(envelope.params).projectId)
    if (envelope.method === 'pipeline.regenerate') return createPipelineRegenerateRequest(envelope.requestId, regenerationParams(envelope.params))
    return createPipelineExecutionRequest(envelope.requestId, envelope.method, executionParams(envelope.params))
  } catch { return null }
}
const responseShape = object({
  version: enumeration([BRIDGE_PROTOCOL_VERSION] as const), requestId: id, method: enumeration(PIPELINE_METHODS),
  result: object({ status: enumeration(['available', 'notFound', 'conflict', 'forbidden', 'unavailable', 'invalid'] as const), snapshot: nullable(readSnapshot) }),
})
export type PipelineResponse = ReturnType<typeof responseShape>
export function parsePipelineResponse(value: unknown): PipelineResponse | null {
  try {
    const result = responseShape(value)
    const { status, snapshot } = result.result
    requireValid(status === 'available' ? snapshot !== null && snapshot.resultStatus !== 'invalid'
      : status === 'invalid' ? snapshot === null || snapshot.resultStatus === 'invalid' : snapshot === null)
    requireValid(new TextEncoder().encode(JSON.stringify(result)).byteLength <= PIPELINE_MAX_RESPONSE_BYTES)
    // Invalid may carry the exact codec DTO for diagnostics, including its parent.
    // Neither that DTO nor reconciliation references authorize media.
    if (status === 'available' && snapshot !== null) {
      for (const projected of [snapshot.result, snapshot.previousResult]) if (projected !== null) verifiedResults.add(projected)
    }
    return result
  } catch { return null }
}
export function pipelineResponseMatchesRequest(response: PipelineResponse, request: PipelineRequest): boolean {
  if (response.requestId !== request.requestId || response.method !== request.method) return false
  const s = response.result.snapshot
  if (s === null) return response.result.status !== 'available'
  if (s.projectId !== request.params.projectId) return false
  if (request.method === 'pipeline.regenerate') return s.parentVersionId === request.params.expectedVersionId &&
    s.parentDigest === request.params.expectedVersionDigest && s.settingsSnapshotDigest === request.params.expectedSettingsSnapshotDigest
  if (request.method === 'pipeline.cancel') return s.executionId === request.params.executionId && s.revision >= request.params.expectedRevision
  if (request.method === 'pipeline.retry') return s.executionId !== request.params.executionId
  return true
}
export function isPipelineLive(phase: PipelinePhase): boolean { return PIPELINE_PHASES.indexOf(phase) >= 0 && PIPELINE_PHASES.indexOf(phase) < 7 }
/** Registration milestones, not the execution's polling/progress revision. */
export function pipelineRegistrationRefreshKey(snapshot: PipelineSnapshot | null): string | undefined {
  if (snapshot === null) return undefined
  const phase = snapshot.phase
  const milestone = phase === 'Queued' || phase === 'Ingesting' || phase === 'Analyzing' ? 'before-analysis'
    : phase === 'Composing' ? 'analysis-ready'
      : phase === 'Rendering' || phase === 'Evaluating' || phase === 'Delivering' ? 'composition-ready' : phase
  const transcripts = snapshot.result?.transcriptReferences.map(t => `${t.revisionId}:${t.revisionDigest}:${t.sourceArtifactId}`).sort() ?? []
  return JSON.stringify([snapshot.executionId, milestone, snapshot.versionId, snapshot.versionDigest, transcripts])
}
export function sameResultBinding(a: SourceOnlyResultBinding, b: SourceOnlyResultBinding): boolean {
  return a.projectId === b.projectId && a.executionId === b.executionId && a.rootJobId === b.rootJobId &&
    a.settingsSnapshotDigest === b.settingsSnapshotDigest && a.versionId === b.versionId && a.versionDigest === b.versionDigest
}
/** Only decoded, native-file-validated projections can authorize a media URL. */
const verifiedResults = new WeakSet<SourceOnlyResultDto>()
export function verifiedPipelineResponse(value: unknown): PipelineResponse | null {
  return parsePipelineResponse(value)
}
/** Withdraw retained display data's media capability until a new validated read. */
export function revokePipelineMedia(snapshot: PipelineSnapshot | null): void {
  for (const result of [snapshot?.result, snapshot?.previousResult]) if (result != null) verifiedResults.delete(result)
}
export function sourceOnlyMediaUrl(result: SourceOnlyResultDto, artifactId: string): string | null {
  if (!verifiedResults.has(result)) return null
  const a = result.artifacts.find(item => item.artifact.artifactId === artifactId)
  if (a === undefined || (a.mediaType !== 'video/mp4' && a.mediaType !== 'image/png')) return null
  return `https://player-media.appassets.example/source-only/${result.binding.projectId}/${result.binding.executionId}/${a.artifact.artifactId}/${a.artifact.contentDigest}`
}