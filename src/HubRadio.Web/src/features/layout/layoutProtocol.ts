import { BRIDGE_PROTOCOL_VERSION } from '../../bridge/protocol'

export const LAYOUT_READ_METHOD = 'layout.read' as const
export const LAYOUT_APPLY_METHOD = 'layout.apply' as const
export const LAYOUT_MAX_REQUEST_UTF8_BYTES = 64 * 1024
export const LAYOUT_MAX_RESPONSE_UTF8_BYTES = 4 * 1024 * 1024

export type LayoutBridgeStatus =
  | 'available'
  | 'forbidden'
  | 'notFound'
  | 'conflict'
  | 'noChange'
  | 'invalid'
  | 'unavailable'

export type ManualLayoutMode =
  | 'singleSubjectCrop'
  | 'twoSpeakerStack'
  | 'speakerMaterialStack'
  | 'materialWithSpeakerInset'
  | 'importantRegionZoom'
  | 'blurredSourceBackground'
  | 'fixedTemplatePlacement'

export type ManualDestinationSlot =
  | 'singleSubject'
  | 'topSpeaker'
  | 'bottomSpeaker'
  | 'stackedSpeaker'
  | 'stackedMaterial'
  | 'mainMaterial'
  | 'speakerInset'
  | 'zoomedImportantRegion'
  | 'blurredBackground'
  | 'foregroundSubject'
  | 'templateVideo'

export type LayoutCueStyle = 'normal' | 'emphasis' | 'caution' | 'quote'
export type LayoutInterpolation = 'hold' | 'linear'
export type LayoutSubtitleCueMode = 'full' | 'selected'
export type LayoutSubtitlePunctuation =
  | 'preserve'
  | 'hideJapaneseCommaAndPeriod'

export interface LayoutRational {
  readonly numerator: string
  readonly denominator: string
}

export interface LayoutRationalTime {
  readonly value: string
  readonly rate: string
}

export interface LayoutTimeRange {
  readonly start: LayoutRationalTime
  readonly duration: LayoutRationalTime
}

export interface LayoutRect {
  readonly x: LayoutRational
  readonly y: LayoutRational
  readonly width: LayoutRational
  readonly height: LayoutRational
}

export interface LayoutPixelRect {
  readonly x: number
  readonly y: number
  readonly width: number
  readonly height: number
}

export interface LayoutResolvedGeometry {
  readonly rect: LayoutRect
  readonly pixels: LayoutPixelRect
}

export interface LayoutManualSourceInput {
  readonly sourceAssetId: string
  readonly crop: LayoutRect
  readonly subject: LayoutRect
  readonly destination: LayoutRect
}

export interface LayoutManualOperation {
  readonly kind: 'setManualLayout'
  readonly mode: ManualLayoutMode
  readonly staticCropDecision:
    | 'singleStaticCropSufficient'
    | 'manualKeyframesRequired'
  readonly sources: readonly LayoutManualSourceInput[]
  readonly templateId: string | null
}

export interface LayoutKeyframeInput {
  readonly time: LayoutRationalTime
  readonly sourceCrop: LayoutRect
  readonly destination: LayoutRect
  readonly interpolation: LayoutInterpolation
}

export interface LayoutKeyframeSegmentInput {
  readonly range: LayoutTimeRange
  readonly keyframes: readonly LayoutKeyframeInput[]
}

export interface LayoutKeyframeOperation {
  readonly kind: 'setKeyframes'
  readonly sourceAssetId: string
  readonly destinationSlot: ManualDestinationSlot
  readonly segments: readonly LayoutKeyframeSegmentInput[]
  readonly evaluationTime: LayoutRationalTime
}

export type LayoutSubtitleEdit =
  | Readonly<{ kind: 'text'; text: string }>
  | Readonly<{ kind: 'timing'; outputRange: LayoutTimeRange }>
  | Readonly<{ kind: 'style'; style: LayoutCueStyle }>
  | Readonly<{ kind: 'placement'; regionName: string; rect: LayoutRect }>

export type LayoutTeropEdit =
  | Readonly<{ kind: 'text'; text: string }>
  | Readonly<{ kind: 'timing'; outputRange: LayoutTimeRange }>
  | Readonly<{ kind: 'style'; style: LayoutCueStyle }>
  | Readonly<{ kind: 'region'; regionName: string }>

export interface LayoutSubtitleOperation {
  readonly kind: 'editSubtitle'
  readonly cueId: string
  readonly expectedRevisionId: string
  readonly expectedRevisionDigest: string
  readonly edit: LayoutSubtitleEdit
  readonly reason: string
}

export interface LayoutTeropOperation {
  readonly kind: 'editTerop'
  readonly cueId: string
  readonly expectedRevisionId: string
  readonly expectedRevisionDigest: string
  readonly edit: LayoutTeropEdit
  readonly reason: string
}

export type LayoutOperation =
  | LayoutManualOperation
  | LayoutKeyframeOperation
  | LayoutSubtitleOperation
  | LayoutTeropOperation

export interface LayoutVersionContext {
  readonly projectId: string
  readonly versionId: string
  readonly versionDigest: string
}

export interface LayoutReadOptions {
  readonly latestInBranch?: true
}

export interface LayoutDurableSubtitles {
  readonly rootVersionId: string
  readonly headVersionId: string
  readonly headVersionDigest: string
  readonly sequence: number
  readonly journalDigest: string | null
}

/** Local, correlated evidence only; never serialized as request parameters. */
export interface LayoutBranchContext {
  readonly root: LayoutVersionContext
  readonly sequence: number
}

type LayoutVersionParameters =
  | Readonly<{ expectedVersionId: string; expectedVersionDigest: string }>
  | Readonly<{ expectedVersionId?: never; expectedVersionDigest?: never }>

type LayoutReadVersionParameters =
  | Readonly<{ expectedVersionId: string; expectedVersionDigest: string; latestInBranch?: true }>
  | Readonly<{ expectedVersionId?: never; expectedVersionDigest?: never; latestInBranch?: never }>

export interface LayoutReadRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof LAYOUT_READ_METHOD
  readonly params: Readonly<{
    projectId: string
    evaluationTime: LayoutRationalTime | null
  } & LayoutReadVersionParameters>
}

export interface LayoutApplyRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof LAYOUT_APPLY_METHOD
  readonly params: Readonly<{
    projectId: string
    expectedRevision: number
    operation: LayoutOperation
  } & LayoutVersionParameters>
}

export type LayoutBridgeRequest = LayoutReadRequest | LayoutApplyRequest

export interface LayoutOutput {
  readonly width: number
  readonly height: number
  readonly aspectWidth: number
  readonly aspectHeight: number
}

export interface LayoutResolvedRegion {
  readonly name: string
  readonly role: 'video' | 'material' | 'subtitle' | 'graphic'
  readonly rect: LayoutRect
  readonly pixels: LayoutPixelRect
}

export interface LayoutResolvedSafeZone {
  readonly name: 'output-frame-bounds'
  readonly rect: LayoutRect
  readonly pixels: LayoutPixelRect
}

export interface LayoutProjection {
  readonly layoutSpecId: string
  readonly regions: readonly LayoutResolvedRegion[]
  readonly safeZones: readonly LayoutResolvedSafeZone[]
}

export interface LayoutSource {
  readonly sourceAssetId: string
  readonly displayWidth: number
  readonly displayHeight: number
}

export interface LayoutTemplate {
  readonly templateId: string
  readonly name: string
  readonly versionDigest: string
}

export interface LayoutManualPlacement {
  readonly sourceAssetId: string
  readonly sourceWidth: number
  readonly sourceHeight: number
  readonly sourceCrop: LayoutResolvedGeometry
  readonly subject: LayoutResolvedGeometry
  readonly destination: LayoutResolvedGeometry
  readonly destinationSlot: ManualDestinationSlot
  readonly semanticRole:
    | 'subject'
    | 'speaker'
    | 'material'
    | 'importantRegion'
    | 'templateVideo'
  readonly treatment: 'standard' | 'blurredBackground'
}

export interface LayoutManualSelection {
  readonly mode: ManualLayoutMode
  readonly status: 'resolved' | 'manualKeyframesRequired'
  readonly placements: readonly LayoutManualPlacement[]
  readonly template: LayoutTemplate | null
}

export interface LayoutKeyframeEvaluation {
  readonly requestedTime: LayoutRationalTime
  readonly sourceCrop: LayoutRect
  readonly destination: LayoutRect
  readonly sourcePixels: LayoutPixelRect
  readonly destinationPixels: LayoutPixelRect
  readonly segmentIndex: number
}

export interface LayoutKeyframeTrack {
  readonly sourceAssetId: string
  readonly destinationSlot: ManualDestinationSlot
  readonly sourceWidth: number
  readonly sourceHeight: number
  readonly outputWidth: number
  readonly outputHeight: number
  readonly range: LayoutTimeRange
  readonly segments: readonly LayoutKeyframeSegmentInput[]
  readonly evaluation: LayoutKeyframeEvaluation
}

export type LayoutHardConstraint =
  | 'noPlacementCandidate'
  | 'sourceTimingAdjustmentRequired'
  | 'requiredSafeZoneMissing'
  | 'outsideOutputFrame'
  | 'unknownLayoutRegion'
  | 'layoutRegionIsNotSubtitle'
  | 'outsideSubtitleRegion'
  | 'outsideRequiredSafeZone'
  | 'prohibitedRegionOverlap'

export type LayoutSoftConstraint =
  | 'faceRegionOverlap'
  | 'materialRegionOverlap'
  | 'graphicRegionOverlap'
  | 'placementChanged'

export interface LayoutSubtitleEvaluation {
  readonly candidateId: string
  readonly regionName: string
  readonly rect: LayoutRect
  readonly pixels: LayoutPixelRect | null
  readonly hardConstraints: readonly LayoutHardConstraint[]
  readonly softConstraints: readonly LayoutSoftConstraint[]
  readonly softCost: string
}

export interface LayoutSubtitleResolution {
  readonly status: 'resolved' | 'manualAdjustmentRequired'
  readonly layoutSpecId: string
  readonly profileVersionDigest: string
  readonly globalHardConstraints: readonly LayoutHardConstraint[]
  readonly evaluations: readonly LayoutSubtitleEvaluation[]
  readonly selectedCandidateId: string | null
}

export interface LayoutSubtitleCue {
  readonly cueId: string
  readonly revisionId: string
  readonly revisionDigest: string
  readonly compositionVersionId: string
  readonly text: string
  readonly style: LayoutCueStyle
  readonly outputRange: LayoutTimeRange
  readonly sourceRange: LayoutTimeRange
  readonly revisionCount: number
  readonly layoutResolution: LayoutSubtitleResolution
}

export interface LayoutSubtitleDisplayedCue {
  readonly cueId: string
  readonly text: string
  readonly textChanged: boolean
}

export interface LayoutSubtitleDisplay {
  readonly profileVersionDigest: string
  readonly cueMode: LayoutSubtitleCueMode
  readonly punctuationDisplay: LayoutSubtitlePunctuation
  readonly selectedCueIds: readonly string[]
  readonly sourceCueCount: number
  readonly displayedCues: readonly LayoutSubtitleDisplayedCue[]
  readonly omittedCueIds: readonly string[]
}

export interface LayoutTeropCue {
  readonly cueId: string
  readonly revisionId: string
  readonly revisionDigest: string
  readonly compositionVersionId: string
  readonly sourceKind: 'adoptedSuggestion' | 'manualEditorial'
  readonly text: string
  readonly style: LayoutCueStyle
  readonly regionName: string
  readonly outputRange: LayoutTimeRange
  readonly revisionCount: number
}

export interface LayoutVariantPair {
  readonly compositionVersionId: string
  readonly compositionVersionDigest: string
  readonly displayProfileDigest: string
  readonly subtitleEvidenceDigest: string
  readonly burnedIn: Readonly<{
    deliveryManifestId: string
    renderGraphId: string
  }>
  readonly subtitleFree: Readonly<{
    deliveryManifestId: string
    renderGraphId: string
    hasSidecar: boolean
  }>
}

export interface LayoutSnapshot {
  readonly projectId: string
  readonly revision: number
  readonly compositionVersionId: string
  readonly compositionVersionDigest: string
  readonly persistence: 'notPersisted' | 'persisted'
  /** Native omits this field for an editable vertical layout; it never sends false. */
  readonly subtitleOnly?: true
  readonly output: LayoutOutput
  readonly layout: LayoutProjection
  readonly sources: readonly LayoutSource[]
  readonly templates: readonly LayoutTemplate[]
  readonly manualSelection: LayoutManualSelection | null
  readonly keyframeTracks: readonly LayoutKeyframeTrack[]
  readonly subtitles: readonly LayoutSubtitleCue[]
  readonly subtitleDisplays: readonly LayoutSubtitleDisplay[]
  readonly terops: readonly LayoutTeropCue[]
  readonly variantPair: LayoutVariantPair | null
}

export type LayoutBridgeResponse =
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof LAYOUT_READ_METHOD | typeof LAYOUT_APPLY_METHOD
      result: Readonly<{ status: 'available'; snapshot: LayoutSnapshot; durableSubtitles?: LayoutDurableSubtitles }>
    }>
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof LAYOUT_READ_METHOD | typeof LAYOUT_APPLY_METHOD
      result: Readonly<{
        status: Exclude<LayoutBridgeStatus, 'available'>
        snapshot: null
      }>
    }>

export function createLayoutReadRequest(
  requestId: string,
  projectId: string,
  evaluationTime: LayoutRationalTime | null,
  target?: LayoutVersionContext,
  options?: LayoutReadOptions,
): LayoutReadRequest {
  requireCanonicalId(requestId, 'request')
  requireCanonicalId(projectId, 'project')
  const normalizedTime = evaluationTime === null
    ? null
    : requireTime(evaluationTime, 'evaluation')
  return ensureBoundedRequest(Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: LAYOUT_READ_METHOD,
    params: Object.freeze({ projectId, evaluationTime: normalizedTime, ...readVersionParameters(projectId, target, options) }),
  }))
}

export function createLayoutApplyRequest(
  requestId: string,
  projectId: string,
  expectedRevision: number,
  operation: LayoutOperation,
  target?: LayoutVersionContext,
): LayoutApplyRequest {
  requireCanonicalId(requestId, 'request')
  requireCanonicalId(projectId, 'project')
  if (!isNonnegativeInt32(expectedRevision)) {
    throw new RangeError('An expected layout revision must be a nonnegative Int32.')
  }
  return ensureBoundedRequest(Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: LAYOUT_APPLY_METHOD,
    params: Object.freeze({
      projectId,
      expectedRevision,
      operation: normalizeOperation(operation),
      ...versionParameters(projectId, target),
    }),
  }))
}

function versionParameters(projectId: string, target?: LayoutVersionContext): LayoutVersionParameters {
  if (target === undefined) return {}
  if (target.projectId !== projectId) throw new TypeError('The layout target must belong to the requested project.')
  requireCanonicalId(target.versionId, 'composition version')
  requireDigest(target.versionDigest, 'composition version')
  return { expectedVersionId: target.versionId, expectedVersionDigest: target.versionDigest }
}

function readVersionParameters(
  projectId: string,
  target?: LayoutVersionContext,
  options?: LayoutReadOptions,
): LayoutReadVersionParameters {
  const pins = versionParameters(projectId, target)
  if (options === undefined) return pins
  const latest = Object.hasOwn(options, 'latestInBranch')
  if (!hasExactKeys(options, latest ? ['latestInBranch'] : []) ||
    (latest && (options.latestInBranch !== true || pins.expectedVersionId === undefined))) {
    throw new TypeError('Latest branch reads require true and both composition version pins.')
  }
  return latest && pins.expectedVersionId !== undefined ? { ...pins, latestInBranch: true } : pins
}

export function layoutSnapshotMatchesTarget(snapshot: LayoutSnapshot, target: LayoutVersionContext): boolean {
  return snapshot.projectId === target.projectId &&
    snapshot.compositionVersionId === target.versionId &&
    snapshot.compositionVersionDigest === target.versionDigest
}

/** Inherited cues require evidence already accepted by an exact native read or
 * an operation-correlated child. A cue's version identifies its revision, not
 * necessarily the composition snapshot that contains that revision. */
export function hasTargetSubtitleEvidence(
  snapshot: LayoutSnapshot,
  target: LayoutVersionContext,
  acceptedSnapshot?: LayoutSnapshot,
): boolean {
  return layoutSnapshotMatchesTarget(snapshot, target) && snapshot.subtitles.length > 0 &&
    snapshot.subtitles.every(cue => cue.text.trim().length > 0) &&
    (acceptedSnapshot === undefined
      ? snapshot.subtitles.every(cue => cue.compositionVersionId === target.versionId)
      : layoutSnapshotMatchesTarget(acceptedSnapshot, target) &&
        unchangedLayoutCuesMatch(snapshot.subtitles, acceptedSnapshot.subtitles) &&
        unchangedLayoutCuesMatch(snapshot.terops, acceptedSnapshot.terops))
}

/** WithSubtitle/WithTerop replace only the edited cue and retain all others.
 * No parent field is exposed on the wire; use the actual submitted candidate
 * and its previously accepted snapshot instead of accepting arbitrary ancestry. */
export function layoutChildSnapshotMatchesOperation(
  snapshot: LayoutSnapshot,
  previous: LayoutSnapshot,
  operation: LayoutOperation,
): boolean {
  if (operation.kind !== 'editSubtitle' && operation.kind !== 'editTerop') return false
  const previousCues = operation.kind === 'editSubtitle' ? previous.subtitles : previous.terops
  const cues = operation.kind === 'editSubtitle' ? snapshot.subtitles : snapshot.terops
  const before = previousCues.find(cue => cue.cueId === operation.cueId)
  const edited = cues.find(cue => cue.cueId === operation.cueId)
  return snapshot.projectId === previous.projectId &&
    snapshot.compositionVersionId !== previous.compositionVersionId &&
    ![...previous.subtitles, ...previous.terops].some(cue => cue.compositionVersionId === snapshot.compositionVersionId) &&
    snapshot.compositionVersionDigest === previous.compositionVersionDigest &&
    snapshot.revision === previous.revision + 1 && snapshot.variantPair === null &&
    before !== undefined &&
    before.revisionId === operation.expectedRevisionId && before.revisionDigest === operation.expectedRevisionDigest &&
    edited !== undefined && edited.compositionVersionId === snapshot.compositionVersionId &&
    edited.revisionId !== before.revisionId && edited.revisionDigest !== before.revisionDigest &&
    edited.revisionCount === before.revisionCount + 1 && cueMatchesEdit(edited, operation.edit) &&
    unchangedLayoutCuesMatch(snapshot.subtitles, previous.subtitles, operation.kind === 'editSubtitle' ? operation.cueId : null) &&
    unchangedLayoutCuesMatch(snapshot.terops, previous.terops, operation.kind === 'editTerop' ? operation.cueId : null)
}

function unchangedLayoutCuesMatch(
  actual: readonly (LayoutSubtitleCue | LayoutTeropCue)[],
  previous: readonly (LayoutSubtitleCue | LayoutTeropCue)[],
  editedCueId: string | null = null,
): boolean {
  const known = new Map(previous.map(cue => [cue.cueId, cue]))
  return actual.length === previous.length && known.size === previous.length &&
    new Set(actual.map(cue => cue.cueId)).size === actual.length && actual.every(cue =>
      known.has(cue.cueId) && (cue.cueId === editedCueId || JSON.stringify(cue) === JSON.stringify(known.get(cue.cueId))))
}

export function layoutSnapshotVersion(snapshot: LayoutSnapshot): LayoutVersionContext {
  return Object.freeze({
    projectId: snapshot.projectId,
    versionId: snapshot.compositionVersionId,
    versionDigest: snapshot.compositionVersionDigest,
  })
}

export function layoutSupportsManualEditing(snapshot: LayoutSnapshot): boolean {
  return snapshot.subtitleOnly !== true &&
    BigInt(snapshot.output.aspectWidth) * 16n === BigInt(snapshot.output.aspectHeight) * 9n
}

export function isCanonicalLayoutVersionDigest(value: unknown): value is string {
  return isDigest(value)
}

/** Correlate only fields actually returned by LayoutSubtitleBridgeJson (no parent fields). */
export function layoutResponseMatchesRequest(
  response: LayoutBridgeResponse,
  request: LayoutBridgeRequest,
  branch?: LayoutBranchContext | null,
): boolean {
  if (response.requestId !== request.requestId || response.method !== request.method) return false
  if (response.result.status !== 'available') return true
  const snapshot = response.result.snapshot
  const durable = response.result.durableSubtitles
  const params = request.params
  if (snapshot.projectId !== params.projectId) return false
  if (!durableResultMatchesSnapshot(snapshot, durable)) return false
  // Preserve the legacy unpinned transport contract. The provider pins all mutations.
  if (params.expectedVersionId === undefined) return durable === undefined
  const root = branch?.root ?? {
    projectId: params.projectId, versionId: params.expectedVersionId, versionDigest: params.expectedVersionDigest,
  }
  if (root.projectId !== params.projectId || root.versionDigest !== params.expectedVersionDigest ||
    (durable !== undefined && (durable.rootVersionId !== root.versionId ||
      durable.headVersionDigest !== root.versionDigest))) return false
  const sameVersion = snapshot.compositionVersionId === params.expectedVersionId &&
    snapshot.compositionVersionDigest === params.expectedVersionDigest
  if (request.method === LAYOUT_READ_METHOD) {
    if (request.params.latestInBranch !== true) return sameVersion
    // Only a latest read may introduce an unobserved child. Legacy exact-shaped
    // replies can still be used without claiming a saved branch or advancing it.
    return durable === undefined ? sameVersion && (branch?.sequence ?? 0) === 0
      : layoutSnapshotMatchesDurableHead(snapshot, durable) && durable.sequence >= (branch?.sequence ?? 0)
  }
  // An apply/replay attests its committed row, not necessarily today's latest
  // branch head. Never compare its sequence against the observed head floor.
  if (durable !== undefined && !layoutSnapshotMatchesDurableHead(snapshot, durable)) return false
  if (snapshot.revision !== request.params.expectedRevision + 1) return false
  const operation = request.params.operation
  if (operation.kind === 'setManualLayout' || operation.kind === 'setKeyframes') {
    return sameVersion && layoutSupportsManualEditing(snapshot)
  }
  // Subtitle/terop edits create a fresh composition. The parent is checked by
  // native against the request's expected binding, not included in its response.
  const cues = operation.kind === 'editSubtitle' ? snapshot.subtitles : snapshot.terops
  const cue = cues.find(item => item.cueId === operation.cueId)
  return snapshot.compositionVersionId !== params.expectedVersionId &&
    snapshot.compositionVersionDigest === params.expectedVersionDigest &&
    snapshot.variantPair === null && cue !== undefined &&
    cue.compositionVersionId === snapshot.compositionVersionId &&
    cue.revisionId !== operation.expectedRevisionId &&
    cue.revisionDigest !== operation.expectedRevisionDigest &&
    cueMatchesEdit(cue, operation.edit)
}

export function layoutSnapshotMatchesDurableHead(snapshot: LayoutSnapshot, durable: LayoutDurableSubtitles): boolean {
  return parseDurableSubtitles(durable) !== null && snapshot.compositionVersionId === durable.headVersionId &&
    snapshot.compositionVersionDigest === durable.headVersionDigest && snapshot.revision === durable.sequence
}

function durableResultMatchesSnapshot(snapshot: LayoutSnapshot, durable?: LayoutDurableSubtitles): boolean {
  if (durable === undefined) return snapshot.persistence === 'notPersisted'
  return parseDurableSubtitles(durable) !== null &&
    snapshot.compositionVersionDigest === durable.headVersionDigest &&
    snapshot.persistence === (durable.sequence > 0 && layoutSnapshotMatchesDurableHead(snapshot, durable)
      ? 'persisted' : 'notPersisted')
}

function parseDurableSubtitles(value: unknown): LayoutDurableSubtitles | null {
  if (!hasExactKeys(value, ['rootVersionId', 'headVersionId', 'headVersionDigest', 'sequence', 'journalDigest']) ||
    !isCanonicalId(value.rootVersionId) || !isCanonicalId(value.headVersionId) || !isDigest(value.headVersionDigest) ||
    !isNonnegativeInt32(value.sequence) ||
    (value.sequence === 0 ? value.journalDigest !== null || value.rootVersionId !== value.headVersionId
      : !isDigest(value.journalDigest) || value.rootVersionId === value.headVersionId)) return null
  const journalDigest = value.journalDigest
  if (journalDigest !== null && !isDigest(journalDigest)) return null
  return Object.freeze({
    rootVersionId: value.rootVersionId, headVersionId: value.headVersionId, headVersionDigest: value.headVersionDigest,
    sequence: value.sequence, journalDigest,
  })
}

function cueMatchesEdit(cue: LayoutSubtitleCue | LayoutTeropCue, edit: LayoutSubtitleEdit | LayoutTeropEdit): boolean {
  switch (edit.kind) {
    case 'text': return cue.text === edit.text
    case 'style': return cue.style === edit.style
    case 'timing': return layoutTimesEqual(cue.outputRange.start, edit.outputRange.start) &&
      layoutTimesEqual(cue.outputRange.duration, edit.outputRange.duration)
    case 'region': return 'regionName' in cue && cue.regionName === edit.regionName
    case 'placement': return 'layoutResolution' in cue && cue.layoutResolution.evaluations.some(candidate =>
      candidate.regionName === edit.regionName && rectsEqual(candidate.rect, edit.rect))
  }
}

export function parseLayoutReadResponse(value: unknown): LayoutBridgeResponse | null {
  const response = parseLayoutBridgeResponse(value)
  return response?.method === LAYOUT_READ_METHOD ? response : null
}

export function parseLayoutApplyResponse(value: unknown): LayoutBridgeResponse | null {
  const response = parseLayoutBridgeResponse(value)
  return response?.method === LAYOUT_APPLY_METHOD ? response : null
}

export function parseLayoutBridgeResponse(value: unknown): LayoutBridgeResponse | null {
  const hasDurableSubtitles = isRecord(value) && isRecord(value.result) && Object.hasOwn(value.result, 'durableSubtitles')
  if (
    !isWithinUtf8Limit(value, LAYOUT_MAX_RESPONSE_UTF8_BYTES) ||
    !hasExactKeys(value, ['version', 'requestId', 'method', 'result']) ||
    value.version !== BRIDGE_PROTOCOL_VERSION ||
    !isCanonicalId(value.requestId) ||
    (value.method !== LAYOUT_READ_METHOD && value.method !== LAYOUT_APPLY_METHOD) ||
    !hasExactKeys(value.result, ['status', 'snapshot', ...(hasDurableSubtitles ? ['durableSubtitles'] : [])])
  ) {
    return null
  }

  const method = value.method
  if (value.result.status === 'available') {
    const snapshot = parseSnapshot(value.result.snapshot)
    const durableSubtitles = hasDurableSubtitles ? parseDurableSubtitles(value.result.durableSubtitles) : undefined
    if (snapshot === null || durableSubtitles === null || !durableResultMatchesSnapshot(snapshot, durableSubtitles)) {
      return null
    }
    return Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId: value.requestId,
      method,
      result: Object.freeze({ status: 'available' as const, snapshot,
        ...(durableSubtitles === undefined ? {} : { durableSubtitles }) }),
    })
  }

  if (
    hasDurableSubtitles ||
    value.result.snapshot !== null ||
    !layoutFailureStatuses.has(value.result.status as never)
  ) {
    return null
  }
  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method,
    result: Object.freeze({
      status: value.result.status as Exclude<LayoutBridgeStatus, 'available'>,
      snapshot: null,
    }),
  })
}

export function layoutTimesEqual(
  left: LayoutRationalTime,
  right: LayoutRationalTime,
): boolean {
  return left.value === right.value && left.rate === right.rate
}

export function isCanonicalLayoutEntityId(value: unknown): value is string {
  return isCanonicalId(value)
}

function normalizeOperation(operation: LayoutOperation): LayoutOperation {
  const candidate = operation as unknown
  if (!hasAtLeastKind(candidate) || typeof candidate.kind !== 'string') {
    throw new TypeError('A layout operation must have a supported kind.')
  }
  switch (candidate.kind) {
    case 'setManualLayout':
      return normalizeManualOperation(candidate)
    case 'setKeyframes':
      return normalizeKeyframeOperation(candidate)
    case 'editSubtitle':
      return normalizeCueOperation(candidate, true)
    case 'editTerop':
      return normalizeCueOperation(candidate, false)
    default:
      throw new TypeError('A layout operation must have a supported kind.')
  }
}

function normalizeManualOperation(value: Record<string, unknown>): LayoutManualOperation {
  if (
    !manualModes.has(value.mode as ManualLayoutMode) ||
    !staticCropDecisions.has(value.staticCropDecision as never) ||
    !Array.isArray(value.sources)
  ) {
    throw new TypeError('A manual layout operation is invalid.')
  }
  const mode = value.mode as ManualLayoutMode
  const expectedCount = modesWithTwoSources.has(mode) ? 2 : 1
  if (value.sources.length !== expectedCount) {
    throw new RangeError('The manual layout mode has an invalid source count.')
  }
  const sources = Object.freeze(value.sources.map((source) => {
    if (!isRecord(source)) {
      throw new TypeError('A manual layout source is invalid.')
    }
    requireCanonicalId(source.sourceAssetId, 'source asset')
    return Object.freeze({
      sourceAssetId: source.sourceAssetId,
      crop: requireNormalizedRect(source.crop, 'source crop'),
      subject: requireNormalizedRect(source.subject, 'subject'),
      destination: requireNormalizedRect(source.destination, 'destination'),
    })
  }))
  const templateId = value.templateId
  if (mode === 'fixedTemplatePlacement') {
    requireCanonicalId(templateId, 'template')
  } else if (templateId !== null) {
    throw new TypeError('Only fixed template placement accepts a template ID.')
  }
  return Object.freeze({
    kind: 'setManualLayout',
    mode,
    staticCropDecision: value.staticCropDecision as LayoutManualOperation['staticCropDecision'],
    sources,
    templateId: templateId as string | null,
  })
}

function normalizeKeyframeOperation(value: Record<string, unknown>): LayoutKeyframeOperation {
  requireCanonicalId(value.sourceAssetId, 'source asset')
  if (
    !destinationSlots.has(value.destinationSlot as ManualDestinationSlot) ||
    !Array.isArray(value.segments) ||
    value.segments.length < 1 ||
    value.segments.length > maximumKeyframeCount
  ) {
    throw new TypeError('A keyframe operation is invalid.')
  }
  let keyframeCount = 0
  const segments = Object.freeze(value.segments.map((segment) => {
    const parsed = normalizeKeyframeSegment(segment)
    keyframeCount += parsed.keyframes.length
    if (keyframeCount > maximumKeyframeCount) {
      throw new RangeError('A keyframe operation exceeds the 256-keyframe limit.')
    }
    return parsed
  }))
  validateSegmentContinuity(segments)
  const evaluationTime = requireTime(value.evaluationTime, 'evaluation')
  const trackRange = rangeFromSegments(segments)
  if (!timeIsWithinClosedRange(evaluationTime, trackRange)) {
    throw new RangeError('The keyframe evaluation time must be within the track.')
  }
  return Object.freeze({
    kind: 'setKeyframes',
    sourceAssetId: value.sourceAssetId as string,
    destinationSlot: value.destinationSlot as ManualDestinationSlot,
    segments,
    evaluationTime,
  })
}

function normalizeKeyframeSegment(value: unknown): LayoutKeyframeSegmentInput {
  if (!isRecord(value) || !Array.isArray(value.keyframes) || value.keyframes.length < 1) {
    throw new TypeError('A keyframe segment is invalid.')
  }
  if (value.keyframes.length > maximumKeyframeCount) {
    throw new RangeError('A keyframe operation exceeds the 256-keyframe limit.')
  }
  const range = requireTimeRange(value.range, 'keyframe segment')
  const keyframes = Object.freeze(value.keyframes.map((keyframe) => {
    if (!isRecord(keyframe) || !interpolations.has(keyframe.interpolation as LayoutInterpolation)) {
      throw new TypeError('A layout keyframe is invalid.')
    }
    return Object.freeze({
      time: requireTime(keyframe.time, 'keyframe'),
      sourceCrop: requireNormalizedRect(keyframe.sourceCrop, 'keyframe source crop'),
      destination: requireNormalizedRect(keyframe.destination, 'keyframe destination'),
      interpolation: keyframe.interpolation as LayoutInterpolation,
    })
  }))
  if (!layoutTimesEqual(keyframes[0]!.time, range.start) || keyframes.at(-1)!.interpolation !== 'hold') {
    throw new TypeError('A keyframe segment must start with a keyframe and end with hold.')
  }
  for (let index = 0; index < keyframes.length; index++) {
    const current = keyframes[index]!
    if (
      compareTime(current.time, range.start) < 0 ||
      compareTime(current.time, rangeEnd(range)) > 0 ||
      (index > 0 && compareTime(current.time, keyframes[index - 1]!.time) <= 0)
    ) {
      throw new TypeError('Layout keyframe times must be ordered within their segment.')
    }
  }
  return Object.freeze({ range, keyframes })
}

function normalizeCueOperation(
  value: Record<string, unknown>,
  subtitle: boolean,
): LayoutSubtitleOperation | LayoutTeropOperation {
  requireCanonicalId(value.cueId, 'cue')
  requireCanonicalId(value.expectedRevisionId, 'cue revision')
  requireDigest(value.expectedRevisionDigest, 'cue revision')
  const reason = requireText(value.reason, 'cue edit reason')
  const edit = normalizeCueEdit(value.edit, subtitle)
  return subtitle
    ? Object.freeze({
        kind: 'editSubtitle',
        cueId: value.cueId as string,
        expectedRevisionId: value.expectedRevisionId as string,
        expectedRevisionDigest: value.expectedRevisionDigest as string,
        edit: edit as LayoutSubtitleEdit,
        reason,
      })
    : Object.freeze({
        kind: 'editTerop',
        cueId: value.cueId as string,
        expectedRevisionId: value.expectedRevisionId as string,
        expectedRevisionDigest: value.expectedRevisionDigest as string,
        edit: edit as LayoutTeropEdit,
        reason,
      })
}

function normalizeCueEdit(value: unknown, subtitle: boolean): LayoutSubtitleEdit | LayoutTeropEdit {
  if (!hasAtLeastKind(value) || typeof value.kind !== 'string') {
    throw new TypeError('A cue edit is invalid.')
  }
  switch (value.kind) {
    case 'text':
      return Object.freeze({ kind: 'text', text: requireText(value.text, 'cue text') })
    case 'timing':
      return Object.freeze({ kind: 'timing', outputRange: requireTimeRange(value.outputRange, 'cue') })
    case 'style':
      if (!cueStyles.has(value.style as LayoutCueStyle)) {
        throw new TypeError('A cue style is invalid.')
      }
      return Object.freeze({ kind: 'style', style: value.style as LayoutCueStyle })
    case 'placement':
      if (!subtitle) {
        throw new TypeError('Terop edits do not accept subtitle placement.')
      }
      return Object.freeze({
        kind: 'placement',
        regionName: requireText(value.regionName, 'subtitle region'),
        rect: requireRect(value.rect, 'subtitle placement'),
      })
    case 'region':
      if (subtitle) {
        throw new TypeError('Subtitle edits do not accept a terop region.')
      }
      return Object.freeze({
        kind: 'region',
        regionName: requireText(value.regionName, 'terop region'),
      })
    default:
      throw new TypeError('A cue edit kind is invalid.')
  }
}

function parseSnapshot(value: unknown): LayoutSnapshot | null {
  const hasSubtitleOnly = isRecord(value) && Object.hasOwn(value, 'subtitleOnly')
  if (!hasExactKeys(value, [
    'projectId',
    'revision',
    'compositionVersionId',
    'compositionVersionDigest',
    'persistence',
    'output',
    'layout',
    'sources',
    'templates',
    'manualSelection',
    'keyframeTracks',
    'subtitles',
    'subtitleDisplays',
    'terops',
    'variantPair',
    ...(hasSubtitleOnly ? ['subtitleOnly'] : []),
  ]) ||
    (hasSubtitleOnly && value.subtitleOnly !== true) ||
    !isCanonicalId(value.projectId) ||
    !isNonnegativeInt32(value.revision) ||
    !isCanonicalId(value.compositionVersionId) ||
    !isDigest(value.compositionVersionDigest) ||
    (value.persistence !== 'notPersisted' && value.persistence !== 'persisted') ||
    !Array.isArray(value.sources) ||
    !Array.isArray(value.templates) ||
    !Array.isArray(value.keyframeTracks) ||
    !Array.isArray(value.subtitles) ||
    !Array.isArray(value.subtitleDisplays) ||
    !Array.isArray(value.terops)
  ) {
    return null
  }

  const output = parseOutput(value.output)
  if (output === null || (!hasSubtitleOnly &&
    BigInt(output.aspectWidth) * 16n !== BigInt(output.aspectHeight) * 9n)) {
    return null
  }
  const layout = parseProjection(value.layout, output, hasSubtitleOnly)
  const sources = parseUniqueArray(value.sources, parseSource, (item) => item.sourceAssetId)
  const templates = parseUniqueArray(value.templates, parseTemplate, (item) => item.templateId)
  if (layout === null || sources === null || (!hasSubtitleOnly && sources.length === 0) || templates === null ||
    (hasSubtitleOnly && (value.manualSelection !== null || value.keyframeTracks.length !== 0 ||
      layout.regions.some(region => region.role !== 'subtitle' && region.role !== 'graphic')))) {
    return null
  }
  const sourceMap = new Map(sources.map((source) => [source.sourceAssetId, source]))
  const manualSelection = value.manualSelection === null
    ? null
    : parseManualSelection(value.manualSelection, sourceMap, output)
  if (value.manualSelection !== null && manualSelection === null) {
    return null
  }
  const keyframeTracks = parseUniqueArray(
    value.keyframeTracks,
    (item) => parseKeyframeTrack(item, sourceMap, output),
    (item) => `${item.sourceAssetId}\u0000${item.destinationSlot}`,
  )
  const subtitles = parseUniqueArray(value.subtitles, (item) => parseSubtitle(item, output), (item) => item.cueId)
  const subtitleDisplays = subtitles === null
    ? null
    : parseUniqueArray(
        value.subtitleDisplays,
        (item) => parseSubtitleDisplay(item, subtitles),
        (item) => item.profileVersionDigest,
      )
  const terops = parseUniqueArray(value.terops, parseTerop, (item) => item.cueId)
  const variantPair = value.variantPair === null
    ? null
    : parseVariantPair(value.variantPair, value.compositionVersionId, value.compositionVersionDigest)
  if (
    keyframeTracks === null ||
    subtitles === null ||
    subtitleDisplays === null ||
    (subtitleDisplays.length > 0 && subtitles.some(
      (cue) => !isSubtitleDisplayText(cue.text),
    )) ||
    new Set(subtitleDisplays.map((display) =>
      `${display.cueMode}\u0000${display.punctuationDisplay}`)).size !== subtitleDisplays.length ||
    terops === null ||
    terops.some((terop) => !layout.regions.some((region) =>
      region.name === terop.regionName && region.role === 'graphic')) ||
    (value.variantPair !== null && variantPair === null) ||
    (variantPair !== null && !subtitleDisplays.some((display) =>
      display.profileVersionDigest === variantPair.displayProfileDigest))
  ) {
    return null
  }

  return Object.freeze({
    projectId: value.projectId,
    revision: value.revision,
    compositionVersionId: value.compositionVersionId,
    compositionVersionDigest: value.compositionVersionDigest,
    persistence: value.persistence,
    ...(hasSubtitleOnly ? { subtitleOnly: true as const } : {}),
    output,
    layout,
    sources,
    templates,
    manualSelection,
    keyframeTracks,
    subtitles,
    subtitleDisplays,
    terops,
    variantPair,
  })
}

function parseOutput(value: unknown): LayoutOutput | null {
  if (!hasExactKeys(value, ['width', 'height', 'aspectWidth', 'aspectHeight']) ||
    !isPositiveInt32(value.width) ||
    !isPositiveInt32(value.height) ||
    !isPositiveInt32(value.aspectWidth) ||
    !isPositiveInt32(value.aspectHeight) ||
    BigInt(value.width) * BigInt(value.aspectHeight) !== BigInt(value.height) * BigInt(value.aspectWidth) ||
    (BigInt(value.aspectWidth) * 16n !== BigInt(value.aspectHeight) * 9n &&
      BigInt(value.aspectWidth) * 9n !== BigInt(value.aspectHeight) * 16n)
  ) {
    return null
  }
  return Object.freeze({
    width: value.width,
    height: value.height,
    aspectWidth: value.aspectWidth,
    aspectHeight: value.aspectHeight,
  })
}

function parseProjection(value: unknown, output: LayoutOutput, subtitleOnly = false): LayoutProjection | null {
  if (!hasExactKeys(value, ['layoutSpecId', 'regions', 'safeZones']) ||
    !isCanonicalId(value.layoutSpecId) ||
    !Array.isArray(value.regions) ||
    (!subtitleOnly && value.regions.length === 0) ||
    !Array.isArray(value.safeZones) ||
    value.safeZones.length !== 1
  ) {
    return null
  }
  const regions = parseUniqueArray(value.regions, (item) => parseRegion(item, output), (item) => item.name)
  const safeZone = parseSafeZone(value.safeZones[0], output)
  if (regions === null || safeZone === null) {
    return null
  }
  return Object.freeze({
    layoutSpecId: value.layoutSpecId,
    regions,
    safeZones: Object.freeze([safeZone]),
  })
}

function parseRegion(value: unknown, output: LayoutOutput): LayoutResolvedRegion | null {
  if (!hasExactKeys(value, ['name', 'role', 'rect', 'pixels']) ||
    !isResponseText(value.name) ||
    !regionRoles.has(value.role as LayoutResolvedRegion['role'])
  ) {
    return null
  }
  const rect = parseNormalizedRect(value.rect)
  const pixels = parsePixelRect(value.pixels)
  if (rect === null || pixels === null || !rectProjectsToPixels(rect, pixels, output.width, output.height)) {
    return null
  }
  return Object.freeze({
    name: value.name,
    role: value.role as LayoutResolvedRegion['role'],
    rect,
    pixels,
  })
}

function parseSafeZone(value: unknown, output: LayoutOutput): LayoutResolvedSafeZone | null {
  if (!hasExactKeys(value, ['name', 'rect', 'pixels']) || value.name !== 'output-frame-bounds') {
    return null
  }
  const rect = parseNormalizedRect(value.rect)
  const pixels = parsePixelRect(value.pixels)
  if (
    rect === null ||
    pixels === null ||
    !isFullFrame(rect) ||
    pixels.x !== 0 ||
    pixels.y !== 0 ||
    pixels.width !== output.width ||
    pixels.height !== output.height
  ) {
    return null
  }
  return Object.freeze({ name: 'output-frame-bounds', rect, pixels })
}

function parseSource(value: unknown): LayoutSource | null {
  if (!hasExactKeys(value, ['sourceAssetId', 'displayWidth', 'displayHeight']) ||
    !isCanonicalId(value.sourceAssetId) ||
    !isPositiveInt32(value.displayWidth) ||
    !isPositiveInt32(value.displayHeight)
  ) {
    return null
  }
  return Object.freeze({
    sourceAssetId: value.sourceAssetId,
    displayWidth: value.displayWidth,
    displayHeight: value.displayHeight,
  })
}

function parseTemplate(value: unknown): LayoutTemplate | null {
  if (!hasExactKeys(value, ['templateId', 'name', 'versionDigest']) ||
    !isCanonicalId(value.templateId) ||
    !isResponseText(value.name) ||
    !isDigest(value.versionDigest)
  ) {
    return null
  }
  return Object.freeze({
    templateId: value.templateId,
    name: value.name,
    versionDigest: value.versionDigest,
  })
}

function parseManualSelection(
  value: unknown,
  sources: ReadonlyMap<string, LayoutSource>,
  output: LayoutOutput,
): LayoutManualSelection | null {
  if (!hasExactKeys(value, ['mode', 'status', 'placements', 'template']) ||
    !manualModes.has(value.mode as ManualLayoutMode) ||
    !manualStatuses.has(value.status as LayoutManualSelection['status']) ||
    !Array.isArray(value.placements)
  ) {
    return null
  }
  const mode = value.mode as ManualLayoutMode
  const expected = expectedManualPlacements[mode]
  if (value.placements.length !== expected.length) {
    return null
  }
  const placements: LayoutManualPlacement[] = []
  for (let index = 0; index < value.placements.length; index++) {
    const placement = parseManualPlacement(value.placements[index], sources, output)
    const expectation = expected[index]!
    if (
      placement === null ||
      placement.destinationSlot !== expectation.slot ||
      placement.semanticRole !== expectation.role ||
      placement.treatment !== expectation.treatment
    ) {
      return null
    }
    placements.push(placement)
  }
  const template = value.template === null ? null : parseTemplate(value.template)
  if (
    (value.template !== null && template === null) ||
    (mode === 'fixedTemplatePlacement') !== (template !== null)
  ) {
    return null
  }
  return Object.freeze({
    mode,
    status: value.status as LayoutManualSelection['status'],
    placements: Object.freeze(placements),
    template,
  })
}

function parseManualPlacement(
  value: unknown,
  sources: ReadonlyMap<string, LayoutSource>,
  output: LayoutOutput,
): LayoutManualPlacement | null {
  if (!hasExactKeys(value, [
    'sourceAssetId',
    'sourceWidth',
    'sourceHeight',
    'sourceCrop',
    'subject',
    'destination',
    'destinationSlot',
    'semanticRole',
    'treatment',
  ]) ||
    !isCanonicalId(value.sourceAssetId) ||
    !isPositiveInt32(value.sourceWidth) ||
    !isPositiveInt32(value.sourceHeight) ||
    !destinationSlots.has(value.destinationSlot as ManualDestinationSlot) ||
    !semanticRoles.has(value.semanticRole as LayoutManualPlacement['semanticRole']) ||
    !treatments.has(value.treatment as LayoutManualPlacement['treatment'])
  ) {
    return null
  }
  const source = sources.get(value.sourceAssetId)
  if (source === undefined || source.displayWidth !== value.sourceWidth || source.displayHeight !== value.sourceHeight) {
    return null
  }
  const sourceCrop = parseResolvedGeometry(value.sourceCrop, value.sourceWidth, value.sourceHeight)
  const subject = parseResolvedGeometry(value.subject, value.sourceWidth, value.sourceHeight)
  const destination = parseResolvedGeometry(value.destination, output.width, output.height)
  if (sourceCrop === null || subject === null || destination === null || !rectContains(sourceCrop.rect, subject.rect)) {
    return null
  }
  return Object.freeze({
    sourceAssetId: value.sourceAssetId,
    sourceWidth: value.sourceWidth,
    sourceHeight: value.sourceHeight,
    sourceCrop,
    subject,
    destination,
    destinationSlot: value.destinationSlot as ManualDestinationSlot,
    semanticRole: value.semanticRole as LayoutManualPlacement['semanticRole'],
    treatment: value.treatment as LayoutManualPlacement['treatment'],
  })
}

function parseResolvedGeometry(value: unknown, width: number, height: number): LayoutResolvedGeometry | null {
  if (!hasExactKeys(value, ['rect', 'pixels'])) {
    return null
  }
  const rect = parseNormalizedRect(value.rect)
  const pixels = parsePixelRect(value.pixels)
  return rect !== null && pixels !== null && rectProjectsToPixels(rect, pixels, width, height)
    ? Object.freeze({ rect, pixels })
    : null
}

function parseKeyframeTrack(
  value: unknown,
  sources: ReadonlyMap<string, LayoutSource>,
  output: LayoutOutput,
): LayoutKeyframeTrack | null {
  if (!hasExactKeys(value, [
    'sourceAssetId',
    'destinationSlot',
    'sourceWidth',
    'sourceHeight',
    'outputWidth',
    'outputHeight',
    'range',
    'segments',
    'evaluation',
  ]) ||
    !isCanonicalId(value.sourceAssetId) ||
    !destinationSlots.has(value.destinationSlot as ManualDestinationSlot) ||
    !isPositiveInt32(value.sourceWidth) ||
    !isPositiveInt32(value.sourceHeight) ||
    value.outputWidth !== output.width ||
    value.outputHeight !== output.height ||
    !Array.isArray(value.segments) ||
    value.segments.length === 0
  ) {
    return null
  }
  const source = sources.get(value.sourceAssetId)
  if (source === undefined || source.displayWidth !== value.sourceWidth || source.displayHeight !== value.sourceHeight) {
    return null
  }
  const range = parseTimeRange(value.range)
  const segments: LayoutKeyframeSegmentInput[] = []
  for (const item of value.segments) {
    const segment = parseKeyframeSegment(item, value.sourceWidth, value.sourceHeight, output)
    if (segment === null) {
      return null
    }
    segments.push(segment)
  }
  if (
    range === null ||
    !segmentsDescribeRange(segments, range) ||
    !keyframeEndpointGeometryIsValid(
      segments,
      value.sourceWidth,
      value.sourceHeight,
      output,
    )
  ) {
    return null
  }
  const evaluation = parseKeyframeEvaluation(
    value.evaluation,
    value.sourceWidth,
    value.sourceHeight,
    output,
    segments,
    range,
  )
  if (evaluation === null) {
    return null
  }
  return Object.freeze({
    sourceAssetId: value.sourceAssetId,
    destinationSlot: value.destinationSlot as ManualDestinationSlot,
    sourceWidth: value.sourceWidth,
    sourceHeight: value.sourceHeight,
    outputWidth: output.width,
    outputHeight: output.height,
    range,
    segments: Object.freeze(segments),
    evaluation,
  })
}

function parseKeyframeSegment(
  value: unknown,
  sourceWidth: number,
  sourceHeight: number,
  output: LayoutOutput,
): LayoutKeyframeSegmentInput | null {
  if (!hasExactKeys(value, ['range', 'keyframes']) || !Array.isArray(value.keyframes) || value.keyframes.length === 0) {
    return null
  }
  const range = parseTimeRange(value.range)
  if (range === null) {
    return null
  }
  const keyframes: LayoutKeyframeInput[] = []
  for (const item of value.keyframes) {
    if (!hasExactKeys(item, ['time', 'sourceCrop', 'destination', 'interpolation']) ||
      !interpolations.has(item.interpolation as LayoutInterpolation)
    ) {
      return null
    }
    const time = parseTime(item.time)
    const sourceCrop = parseNormalizedRect(item.sourceCrop)
    const destination = parseNormalizedRect(item.destination)
    if (
      time === null ||
      sourceCrop === null ||
      destination === null ||
      projectRect(sourceCrop, sourceWidth, sourceHeight) === null ||
      projectRect(destination, output.width, output.height) === null ||
      (keyframes.length === 0 && !layoutTimesEqual(time, range.start)) ||
      (keyframes.length > 0 && compareTime(time, keyframes.at(-1)!.time) <= 0) ||
      compareTime(time, range.start) < 0 ||
      compareTime(time, rangeEnd(range)) > 0
    ) {
      return null
    }
    keyframes.push(Object.freeze({
      time,
      sourceCrop,
      destination,
      interpolation: item.interpolation as LayoutInterpolation,
    }))
  }
  if (keyframes.at(-1)!.interpolation !== 'hold') {
    return null
  }
  return Object.freeze({ range, keyframes: Object.freeze(keyframes) })
}

function parseKeyframeEvaluation(
  value: unknown,
  sourceWidth: number,
  sourceHeight: number,
  output: LayoutOutput,
  segments: readonly LayoutKeyframeSegmentInput[],
  trackRange: LayoutTimeRange,
): LayoutKeyframeEvaluation | null {
  if (!hasExactKeys(value, [
    'requestedTime',
    'sourceCrop',
    'destination',
    'sourcePixels',
    'destinationPixels',
    'segmentIndex',
  ]) || !isNonnegativeInt32(value.segmentIndex) || value.segmentIndex >= segments.length) {
    return null
  }
  const requestedTime = parseTime(value.requestedTime)
  const sourceCrop = parseNormalizedRect(value.sourceCrop)
  const destination = parseNormalizedRect(value.destination)
  const sourcePixels = parsePixelRect(value.sourcePixels)
  const destinationPixels = parsePixelRect(value.destinationPixels)
  if (
    requestedTime === null ||
    sourceCrop === null ||
    destination === null ||
    sourcePixels === null ||
    destinationPixels === null ||
    !timeIsWithinClosedRange(requestedTime, trackRange) ||
    selectedSegmentIndex(segments, requestedTime, trackRange) !== value.segmentIndex ||
    !rectProjectsToPixels(sourceCrop, sourcePixels, sourceWidth, sourceHeight) ||
    !rectProjectsToPixels(destination, destinationPixels, output.width, output.height)
  ) {
    return null
  }
  const exactKeyframe = segments[value.segmentIndex]!.keyframes.find((keyframe) =>
    layoutTimesEqual(keyframe.time, requestedTime))
  if (exactKeyframe !== undefined &&
    (!rectsEqual(exactKeyframe.sourceCrop, sourceCrop) || !rectsEqual(exactKeyframe.destination, destination))
  ) {
    return null
  }
  const expected = evaluateKeyframePixels(
    segments,
    value.segmentIndex,
    requestedTime,
    sourceWidth,
    sourceHeight,
    output,
  )
  if (
    expected === null ||
    !pixelRectsEqual(expected.source, sourcePixels) ||
    !pixelRectsEqual(expected.destination, destinationPixels) ||
    !rectsEqual(canonicalRect(expected.source, sourceWidth, sourceHeight), sourceCrop) ||
    !rectsEqual(canonicalRect(expected.destination, output.width, output.height), destination)
  ) {
    return null
  }
  return Object.freeze({
    requestedTime,
    sourceCrop,
    destination,
    sourcePixels,
    destinationPixels,
    segmentIndex: value.segmentIndex,
  })
}

function parseSubtitle(value: unknown, output: LayoutOutput): LayoutSubtitleCue | null {
  if (!hasExactKeys(value, [
    'cueId',
    'revisionId',
    'revisionDigest',
    'compositionVersionId',
    'text',
    'style',
    'outputRange',
    'sourceRange',
    'revisionCount',
    'layoutResolution',
  ]) ||
    !isCanonicalId(value.cueId) ||
    !isCanonicalId(value.revisionId) ||
    !isDigest(value.revisionDigest) ||
    !isCanonicalId(value.compositionVersionId) ||
    !isSubtitleDisplayText(value.text) ||
    !cueStyles.has(value.style as LayoutCueStyle) ||
    !isPositiveInt32(value.revisionCount)
  ) {
    return null
  }
  const outputRange = parseTimeRange(value.outputRange)
  const sourceRange = parseTimeRange(value.sourceRange)
  const layoutResolution = parseSubtitleResolution(value.layoutResolution, output)
  if (outputRange === null || sourceRange === null || layoutResolution === null) {
    return null
  }
  return Object.freeze({
    cueId: value.cueId,
    revisionId: value.revisionId,
    revisionDigest: value.revisionDigest,
    compositionVersionId: value.compositionVersionId,
    text: value.text,
    style: value.style as LayoutCueStyle,
    outputRange,
    sourceRange,
    revisionCount: value.revisionCount,
    layoutResolution,
  })
}

function parseSubtitleResolution(value: unknown, output: LayoutOutput): LayoutSubtitleResolution | null {
  if (!hasExactKeys(value, [
    'status',
    'layoutSpecId',
    'profileVersionDigest',
    'globalHardConstraints',
    'evaluations',
    'selectedCandidateId',
  ]) ||
    !subtitleStatuses.has(value.status as LayoutSubtitleResolution['status']) ||
    !isCanonicalId(value.layoutSpecId) ||
    !isDigest(value.profileVersionDigest) ||
    !Array.isArray(value.globalHardConstraints) ||
    !Array.isArray(value.evaluations) ||
    !(value.selectedCandidateId === null || isCanonicalId(value.selectedCandidateId))
  ) {
    return null
  }
  const globalHardConstraints = parseEnumArray(value.globalHardConstraints, hardConstraints)
  const evaluations = parseUniqueArray(
    value.evaluations,
    (item) => parseSubtitleEvaluation(item, output),
    (item) => item.candidateId,
  )
  if (globalHardConstraints === null || evaluations === null) {
    return null
  }
  const selected = value.selectedCandidateId
  if (
    (value.status === 'resolved') !== (selected !== null) ||
    (selected !== null && !evaluations.some((evaluation) =>
      evaluation.candidateId === selected && evaluation.hardConstraints.length === 0))
  ) {
    return null
  }
  return Object.freeze({
    status: value.status as LayoutSubtitleResolution['status'],
    layoutSpecId: value.layoutSpecId,
    profileVersionDigest: value.profileVersionDigest,
    globalHardConstraints,
    evaluations,
    selectedCandidateId: selected,
  })
}

function parseSubtitleDisplay(
  value: unknown,
  sourceCues: readonly LayoutSubtitleCue[],
): LayoutSubtitleDisplay | null {
  if (!hasExactKeys(value, [
    'profileVersionDigest',
    'cueMode',
    'punctuationDisplay',
    'selectedCueIds',
    'sourceCueCount',
    'displayedCues',
    'omittedCueIds',
  ]) ||
    !isDigest(value.profileVersionDigest) ||
    !subtitleCueModes.has(value.cueMode as LayoutSubtitleCueMode) ||
    !subtitlePunctuationDisplays.has(
      value.punctuationDisplay as LayoutSubtitlePunctuation,
    ) ||
    !isNonnegativeInt32(value.sourceCueCount) ||
    value.sourceCueCount !== sourceCues.length ||
    !Array.isArray(value.displayedCues)
  ) {
    return null
  }
  const selectedCueIds = parseCanonicalIdArray(value.selectedCueIds)
  const displayedCues = parseUniqueArray(
    value.displayedCues,
    parseSubtitleDisplayedCue,
    (cue) => cue.cueId,
  )
  const omittedCueIds = parseCanonicalIdArray(value.omittedCueIds)
  if (
    selectedCueIds === null ||
    displayedCues === null ||
    omittedCueIds === null
  ) {
    return null
  }

  const cueMode = value.cueMode as LayoutSubtitleCueMode
  const punctuationDisplay =
    value.punctuationDisplay as LayoutSubtitlePunctuation
  const sourceIds = new Set(sourceCues.map((cue) => cue.cueId))
  const selectedIds = new Set(selectedCueIds)
  if (
    (cueMode === 'full' && selectedCueIds.length !== 0) ||
    selectedCueIds.some((cueId) => !sourceIds.has(cueId))
  ) {
    return null
  }

  const expectedDisplayed: LayoutSubtitleDisplayedCue[] = []
  const expectedOmitted: string[] = []
  const orderedSources = [...sourceCues].sort(compareSubtitleCueOrder)
  for (const source of orderedSources) {
    if (cueMode === 'selected' && !selectedIds.has(source.cueId)) {
      continue
    }
    const text = punctuationDisplay === 'preserve'
      ? source.text
      : hideJapaneseCommaAndPeriod(source.text)
    if (text.length === 0) {
      expectedOmitted.push(source.cueId)
      continue
    }
    expectedDisplayed.push({
      cueId: source.cueId,
      text,
      textChanged: text !== source.text,
    })
  }
  if (
    !displayedCuesEqual(displayedCues, expectedDisplayed) ||
    !stringArraysEqual(omittedCueIds, expectedOmitted)
  ) {
    return null
  }

  return Object.freeze({
    profileVersionDigest: value.profileVersionDigest,
    cueMode,
    punctuationDisplay,
    selectedCueIds,
    sourceCueCount: value.sourceCueCount,
    displayedCues,
    omittedCueIds,
  })
}

function parseSubtitleDisplayedCue(
  value: unknown,
): LayoutSubtitleDisplayedCue | null {
  if (!hasExactKeys(value, ['cueId', 'text', 'textChanged']) ||
    !isCanonicalId(value.cueId) ||
    !isResponseText(value.text) ||
    typeof value.textChanged !== 'boolean'
  ) {
    return null
  }
  return Object.freeze({
    cueId: value.cueId,
    text: value.text,
    textChanged: value.textChanged,
  })
}

function parseSubtitleEvaluation(value: unknown, output: LayoutOutput): LayoutSubtitleEvaluation | null {
  if (!hasExactKeys(value, [
    'candidateId',
    'regionName',
    'rect',
    'pixels',
    'hardConstraints',
    'softConstraints',
    'softCost',
  ]) ||
    !isCanonicalId(value.candidateId) ||
    !isResponseText(value.regionName) ||
    !Array.isArray(value.hardConstraints) ||
    !Array.isArray(value.softConstraints) ||
    !isCanonicalInt64(value.softCost, false)
  ) {
    return null
  }
  const rect = parseRect(value.rect)
  const hard = parseEnumArray(value.hardConstraints, hardConstraints)
  const soft = parseEnumArray(value.softConstraints, softConstraints)
  if (rect === null || hard === null || soft === null) {
    return null
  }
  const expectedPixels = projectRectIfNormalized(rect, output.width, output.height)
  const pixels = value.pixels === null ? null : parsePixelRect(value.pixels)
  if (
    (expectedPixels === null) !== (pixels === null) ||
    (expectedPixels !== null && pixels !== null && !pixelRectsEqual(expectedPixels, pixels))
  ) {
    return null
  }
  return Object.freeze({
    candidateId: value.candidateId,
    regionName: value.regionName,
    rect,
    pixels,
    hardConstraints: hard,
    softConstraints: soft,
    softCost: value.softCost,
  })
}

function parseTerop(value: unknown): LayoutTeropCue | null {
  if (!hasExactKeys(value, [
    'cueId',
    'revisionId',
    'revisionDigest',
    'compositionVersionId',
    'sourceKind',
    'text',
    'style',
    'regionName',
    'outputRange',
    'revisionCount',
  ]) ||
    !isCanonicalId(value.cueId) ||
    !isCanonicalId(value.revisionId) ||
    !isDigest(value.revisionDigest) ||
    !isCanonicalId(value.compositionVersionId) ||
    !teropSourceKinds.has(value.sourceKind as LayoutTeropCue['sourceKind']) ||
    !isResponseText(value.text) ||
    !cueStyles.has(value.style as LayoutCueStyle) ||
    !isResponseText(value.regionName) ||
    !isPositiveInt32(value.revisionCount)
  ) {
    return null
  }
  const outputRange = parseTimeRange(value.outputRange)
  if (outputRange === null) {
    return null
  }
  return Object.freeze({
    cueId: value.cueId,
    revisionId: value.revisionId,
    revisionDigest: value.revisionDigest,
    compositionVersionId: value.compositionVersionId,
    sourceKind: value.sourceKind as LayoutTeropCue['sourceKind'],
    text: value.text,
    style: value.style as LayoutCueStyle,
    regionName: value.regionName,
    outputRange,
    revisionCount: value.revisionCount,
  })
}

function parseVariantPair(value: unknown, compositionVersionId: string, compositionVersionDigest: string): LayoutVariantPair | null {
  if (!hasExactKeys(value, [
    'compositionVersionId',
    'compositionVersionDigest',
    'displayProfileDigest',
    'subtitleEvidenceDigest',
    'burnedIn',
    'subtitleFree',
  ]) ||
    value.compositionVersionId !== compositionVersionId ||
    value.compositionVersionDigest !== compositionVersionDigest ||
    !isDigest(value.displayProfileDigest) ||
    !isDigest(value.subtitleEvidenceDigest) ||
    !hasExactKeys(value.burnedIn, ['deliveryManifestId', 'renderGraphId']) ||
    !isCanonicalId(value.burnedIn.deliveryManifestId) ||
    !isCanonicalId(value.burnedIn.renderGraphId) ||
    !hasExactKeys(value.subtitleFree, ['deliveryManifestId', 'renderGraphId', 'hasSidecar']) ||
    !isCanonicalId(value.subtitleFree.deliveryManifestId) ||
    !isCanonicalId(value.subtitleFree.renderGraphId) ||
    typeof value.subtitleFree.hasSidecar !== 'boolean'
  ) {
    return null
  }
  return Object.freeze({
    compositionVersionId,
    compositionVersionDigest,
    displayProfileDigest: value.displayProfileDigest,
    subtitleEvidenceDigest: value.subtitleEvidenceDigest,
    burnedIn: Object.freeze({
      deliveryManifestId: value.burnedIn.deliveryManifestId,
      renderGraphId: value.burnedIn.renderGraphId,
    }),
    subtitleFree: Object.freeze({
      deliveryManifestId: value.subtitleFree.deliveryManifestId,
      renderGraphId: value.subtitleFree.renderGraphId,
      hasSidecar: value.subtitleFree.hasSidecar,
    }),
  })
}

function parseTimeRange(value: unknown): LayoutTimeRange | null {
  if (!hasExactKeys(value, ['start', 'duration'])) {
    return null
  }
  const start = parseTime(value.start)
  const duration = parseTime(value.duration)
  if (start === null || duration === null || compareTime(duration, zeroTime) <= 0) {
    return null
  }
  return Object.freeze({ start, duration })
}

function parseCanonicalIdArray(value: unknown): readonly string[] | null {
  if (!Array.isArray(value)) {
    return null
  }
  const result: string[] = []
  const seen = new Set<string>()
  for (const item of value) {
    if (!isCanonicalId(item) || seen.has(item)) {
      return null
    }
    seen.add(item)
    result.push(item)
  }
  return Object.freeze(result)
}

function compareSubtitleCueOrder(
  left: LayoutSubtitleCue,
  right: LayoutSubtitleCue,
): number {
  const byStart = compareTime(left.outputRange.start, right.outputRange.start)
  if (byStart !== 0) {
    return byStart
  }
  const byEnd = compareTime(rangeEnd(left.outputRange), rangeEnd(right.outputRange))
  if (byEnd !== 0) {
    return byEnd
  }
  return left.cueId < right.cueId ? -1 : left.cueId > right.cueId ? 1 : 0
}

function hideJapaneseCommaAndPeriod(value: string): string {
  return trimDotNetWhitespace(value.replace(/[、。､｡]/gu, ''))
}

function isSubtitleDisplayText(value: unknown): value is string {
  return isResponseText(value) &&
    value === trimDotNetWhitespace(value) &&
    !isDotNetWhiteSpaceOnly(value) &&
    ![...value].some((character) => {
      const code = character.codePointAt(0)!
      return isUnicodeControl(code) && code !== 0x09 && code !== 0x0a && code !== 0x0d
    })
}

function trimDotNetWhitespace(value: string): string {
  return value.replace(dotNetWhitespaceAtEndsPattern, '')
}

function isDotNetWhiteSpaceOnly(value: string): boolean {
  return value.length === 0 || [...value].every((character) =>
    dotNetWhitespaceCharacterPattern.test(character))
}

function isUnicodeControl(value: number): boolean {
  return value <= 0x1f || (value >= 0x7f && value <= 0x9f)
}

function displayedCuesEqual(
  actual: readonly LayoutSubtitleDisplayedCue[],
  expected: readonly LayoutSubtitleDisplayedCue[],
): boolean {
  return actual.length === expected.length && actual.every((cue, index) => {
    const expectedCue = expected[index]
    return expectedCue !== undefined &&
      cue.cueId === expectedCue.cueId &&
      cue.text === expectedCue.text &&
      cue.textChanged === expectedCue.textChanged
  })
}

function stringArraysEqual(
  actual: readonly string[],
  expected: readonly string[],
): boolean {
  return actual.length === expected.length && actual.every(
    (value, index) => value === expected[index],
  )
}

function parseTime(value: unknown): LayoutRationalTime | null {
  if (!hasExactKeys(value, ['value', 'rate']) ||
    !isCanonicalInt64(value.value, false) ||
    !isCanonicalInt64(value.rate, true) ||
    gcd(BigInt(value.value), BigInt(value.rate)) !== 1n
  ) {
    return null
  }
  return Object.freeze({ value: value.value, rate: value.rate })
}

function parseRect(value: unknown): LayoutRect | null {
  if (!hasExactKeys(value, ['x', 'y', 'width', 'height'])) {
    return null
  }
  const x = parseRational(value.x, false)
  const y = parseRational(value.y, false)
  const width = parseRational(value.width, true)
  const height = parseRational(value.height, true)
  return x !== null && y !== null && width !== null && height !== null
    ? Object.freeze({ x, y, width, height })
    : null
}

function parseNormalizedRect(value: unknown): LayoutRect | null {
  const rect = parseRect(value)
  return rect !== null && isNormalizedRect(rect) ? rect : null
}

function parseRational(value: unknown, positive: boolean): LayoutRational | null {
  if (!hasExactKeys(value, ['numerator', 'denominator']) ||
    !isCanonicalInt64(value.numerator, positive) ||
    !isCanonicalInt64(value.denominator, true) ||
    gcd(BigInt(value.numerator), BigInt(value.denominator)) !== 1n
  ) {
    return null
  }
  return Object.freeze({ numerator: value.numerator, denominator: value.denominator })
}

function parsePixelRect(value: unknown): LayoutPixelRect | null {
  if (!hasExactKeys(value, ['x', 'y', 'width', 'height']) ||
    !isNonnegativeInt32(value.x) ||
    !isNonnegativeInt32(value.y) ||
    !isPositiveInt32(value.width) ||
    !isPositiveInt32(value.height)
  ) {
    return null
  }
  return Object.freeze({ x: value.x, y: value.y, width: value.width, height: value.height })
}

interface PixelSizeBase {
  readonly width: number
  readonly height: number
}

interface KeyframePixels {
  readonly source: LayoutPixelRect
  readonly destination: LayoutPixelRect
}

function keyframeEndpointGeometryIsValid(
  segments: readonly LayoutKeyframeSegmentInput[],
  sourceWidth: number,
  sourceHeight: number,
  output: LayoutOutput,
): boolean {
  const first = segments[0]?.keyframes[0]
  if (first === undefined) {
    return false
  }
  const firstSource = projectRect(first.sourceCrop, sourceWidth, sourceHeight)
  const firstDestination = projectRect(
    first.destination,
    output.width,
    output.height,
  )
  if (
    firstSource === null ||
    firstDestination === null ||
    !pixelAspectsEqual(firstSource, firstDestination)
  ) {
    return false
  }
  const sourceBase = pixelSizeBase(firstSource)
  const destinationBase = pixelSizeBase(firstDestination)
  for (const segment of segments) {
    for (const keyframe of segment.keyframes) {
      const source = projectRect(
        keyframe.sourceCrop,
        sourceWidth,
        sourceHeight,
      )
      const destination = projectRect(
        keyframe.destination,
        output.width,
        output.height,
      )
      if (
        source === null ||
        destination === null ||
        !pixelAspectsEqual(source, destination) ||
        pixelScale(source, sourceBase) === null ||
        pixelScale(destination, destinationBase) === null
      ) {
        return false
      }
    }
  }
  return true
}

function evaluateKeyframePixels(
  segments: readonly LayoutKeyframeSegmentInput[],
  segmentIndex: number,
  time: LayoutRationalTime,
  sourceWidth: number,
  sourceHeight: number,
  output: LayoutOutput,
): KeyframePixels | null {
  const first = segments[0]?.keyframes[0]
  const segment = segments[segmentIndex]
  if (first === undefined || segment === undefined) {
    return null
  }
  const firstSource = projectRect(first.sourceCrop, sourceWidth, sourceHeight)
  const firstDestination = projectRect(
    first.destination,
    output.width,
    output.height,
  )
  if (firstSource === null || firstDestination === null) {
    return null
  }
  const sourceBase = pixelSizeBase(firstSource)
  const destinationBase = pixelSizeBase(firstDestination)

  for (let index = 0; index < segment.keyframes.length; index++) {
    const left = segment.keyframes[index]!
    const leftPixels = keyframePixels(
      left,
      sourceWidth,
      sourceHeight,
      output,
    )
    if (leftPixels === null) {
      return null
    }
    if (
      layoutTimesEqual(time, left.time) ||
      index === segment.keyframes.length - 1
    ) {
      return leftPixels
    }
    const right = segment.keyframes[index + 1]!
    if (compareTime(time, right.time) < 0) {
      if (left.interpolation === 'hold') {
        return leftPixels
      }
      const rightPixels = keyframePixels(
        right,
        sourceWidth,
        sourceHeight,
        output,
      )
      if (rightPixels === null) {
        return null
      }
      const progress = rationalDivide(
        rationalDifference(time, left.time),
        rationalDifference(right.time, left.time),
      )
      const source = interpolatePixelRect(
        leftPixels.source,
        rightPixels.source,
        progress,
        sourceWidth,
        sourceHeight,
        sourceBase,
      )
      const destination = interpolatePixelRect(
        leftPixels.destination,
        rightPixels.destination,
        progress,
        output.width,
        output.height,
        destinationBase,
      )
      return source === null || destination === null
        ? null
        : Object.freeze({ source, destination })
    }
  }
  return null
}

function keyframePixels(
  keyframe: LayoutKeyframeInput,
  sourceWidth: number,
  sourceHeight: number,
  output: LayoutOutput,
): KeyframePixels | null {
  const source = projectRect(
    keyframe.sourceCrop,
    sourceWidth,
    sourceHeight,
  )
  const destination = projectRect(
    keyframe.destination,
    output.width,
    output.height,
  )
  return source === null || destination === null
    ? null
    : Object.freeze({ source, destination })
}

function interpolatePixelRect(
  left: LayoutPixelRect,
  right: LayoutPixelRect,
  progress: ExactRational,
  gridWidth: number,
  gridHeight: number,
  sizeBase: PixelSizeBase,
): LayoutPixelRect | null {
  const leftScale = pixelScale(left, sizeBase)
  const rightScale = pixelScale(right, sizeBase)
  if (leftScale === null || rightScale === null) {
    return null
  }
  const scale = roundNearestToEven(
    interpolateInteger(leftScale, rightScale, progress),
  )
  if (scale <= 0n || scale > BigInt(2_147_483_647)) {
    return null
  }
  const width = BigInt(sizeBase.width) * scale
  const height = BigInt(sizeBase.height) * scale
  if (width > BigInt(gridWidth) || height > BigInt(gridHeight)) {
    return null
  }
  const resolvedWidth = Number(width)
  const resolvedHeight = Number(height)
  const x = constrainPosition(
    roundNearestToEven(interpolateInteger(left.x, right.x, progress)),
    gridWidth - resolvedWidth,
  )
  const y = constrainPosition(
    roundNearestToEven(interpolateInteger(left.y, right.y, progress)),
    gridHeight - resolvedHeight,
  )
  return Object.freeze({
    x,
    y,
    width: resolvedWidth,
    height: resolvedHeight,
  })
}

function interpolateInteger(
  left: number,
  right: number,
  progress: ExactRational,
): ExactRational {
  return rationalAdd(
    normalizeRational(BigInt(left), 1n),
    rationalMultiply(
      normalizeRational(BigInt(right - left), 1n),
      progress,
    ),
  )
}

function roundNearestToEven(value: ExactRational): bigint {
  const quotient = value.numerator / value.denominator
  const remainder = value.numerator % value.denominator
  const doubled = (remainder < 0n ? -remainder : remainder) * 2n
  if (
    doubled < value.denominator ||
    (doubled === value.denominator && quotient % 2n === 0n)
  ) {
    return quotient
  }
  return quotient + (remainder < 0n ? -1n : 1n)
}

function constrainPosition(value: bigint, maximum: number): number {
  return Number(value < 0n ? 0n : value > BigInt(maximum) ? BigInt(maximum) : value)
}

function pixelSizeBase(rect: LayoutPixelRect): PixelSizeBase {
  const divisor = numberGcd(rect.width, rect.height)
  return Object.freeze({
    width: rect.width / divisor,
    height: rect.height / divisor,
  })
}

function pixelScale(
  rect: LayoutPixelRect,
  sizeBase: PixelSizeBase,
): number | null {
  if (
    rect.width % sizeBase.width !== 0 ||
    rect.height % sizeBase.height !== 0 ||
    rect.width / sizeBase.width !== rect.height / sizeBase.height
  ) {
    return null
  }
  const scale = rect.width / sizeBase.width
  return scale > 0 ? scale : null
}

function pixelAspectsEqual(
  left: LayoutPixelRect,
  right: LayoutPixelRect,
): boolean {
  return BigInt(left.width) * BigInt(right.height) ===
    BigInt(right.width) * BigInt(left.height)
}

function canonicalRect(
  rect: LayoutPixelRect,
  width: number,
  height: number,
): LayoutRect {
  return Object.freeze({
    x: canonicalRational(rect.x, width),
    y: canonicalRational(rect.y, height),
    width: canonicalRational(rect.width, width),
    height: canonicalRational(rect.height, height),
  })
}

function canonicalRational(numerator: number, denominator: number): LayoutRational {
  const normalized = normalizeRational(BigInt(numerator), BigInt(denominator))
  return Object.freeze({
    numerator: normalized.numerator.toString(),
    denominator: normalized.denominator.toString(),
  })
}

function numberGcd(left: number, right: number): number {
  let a = left
  let b = right
  while (b !== 0) {
    const remainder = a % b
    a = b
    b = remainder
  }
  return a
}

function requireTime(value: unknown, description: string): LayoutRationalTime {
  const parsed = parseTime(value)
  if (parsed === null) {
    throw new TypeError(`A ${description} time must be a reduced signed-Int64 rational.`)
  }
  return parsed
}

function requireTimeRange(value: unknown, description: string): LayoutTimeRange {
  const parsed = parseTimeRange(value)
  if (parsed === null) {
    throw new TypeError(`A ${description} range must have canonical exact time and positive duration.`)
  }
  return parsed
}

function requireRect(value: unknown, description: string): LayoutRect {
  const parsed = parseRect(value)
  if (parsed === null) {
    throw new TypeError(`A ${description} rectangle must use canonical reduced Int64 rationals.`)
  }
  return parsed
}

function requireNormalizedRect(value: unknown, description: string): LayoutRect {
  const parsed = parseNormalizedRect(value)
  if (parsed === null) {
    throw new TypeError(`A ${description} rectangle must be normalized within the unit frame.`)
  }
  return parsed
}

function requireCanonicalId(value: unknown, description: string): asserts value is string {
  if (!isCanonicalId(value)) {
    throw new TypeError(`A ${description} ID must be a non-empty lowercase UUID.`)
  }
}

function requireDigest(value: unknown, description: string): asserts value is string {
  if (!isDigest(value)) {
    throw new TypeError(`A ${description} digest must be lowercase SHA-256 evidence.`)
  }
}

function requireText(value: unknown, description: string): string {
  if (!isProtocolText(value)) {
    throw new TypeError(`A ${description} must be trimmed nonblank text within 16 KiB.`)
  }
  return value
}

function validateSegmentContinuity(segments: readonly LayoutKeyframeSegmentInput[]): void {
  for (let index = 1; index < segments.length; index++) {
    if (!layoutTimesEqual(segments[index]!.range.start, rangeEnd(segments[index - 1]!.range))) {
      throw new TypeError('Keyframe segments must be ordered and contiguous.')
    }
  }
}

function segmentsDescribeRange(segments: readonly LayoutKeyframeSegmentInput[], range: LayoutTimeRange): boolean {
  if (segments.length === 0) {
    return false
  }
  for (let index = 1; index < segments.length; index++) {
    if (!layoutTimesEqual(segments[index]!.range.start, rangeEnd(segments[index - 1]!.range))) {
      return false
    }
  }
  return layoutTimesEqual(segments[0]!.range.start, range.start) &&
    rationalEquals(rationalDifference(rangeEnd(segments.at(-1)!.range), range.start), timeToRational(range.duration))
}

function rangeFromSegments(segments: readonly LayoutKeyframeSegmentInput[]): LayoutTimeRange {
  const start = segments[0]!.range.start
  const end = rangeEnd(segments.at(-1)!.range)
  return Object.freeze({ start, duration: rationalToTime(rationalDifference(end, start)) })
}

function selectedSegmentIndex(
  segments: readonly LayoutKeyframeSegmentInput[],
  time: LayoutRationalTime,
  range: LayoutTimeRange,
): number {
  if (layoutTimesEqual(time, rangeEnd(range))) {
    return segments.length - 1
  }
  return segments.findIndex((segment) =>
    compareTime(time, segment.range.start) >= 0 && compareTime(time, rangeEnd(segment.range)) < 0)
}

function timeIsWithinClosedRange(value: LayoutRationalTime, range: LayoutTimeRange): boolean {
  return compareTime(value, range.start) >= 0 && compareTime(value, rangeEnd(range)) <= 0
}

function rangeEnd(range: LayoutTimeRange): LayoutRationalTime {
  return rationalToTime(rationalAdd(timeToRational(range.start), timeToRational(range.duration)))
}

function compareTime(left: LayoutRationalTime, right: LayoutRationalTime): number {
  return compareRational(timeToRational(left), timeToRational(right))
}

function isNormalizedRect(rect: LayoutRect): boolean {
  return compareRational(toExact(rect.x), zeroRational) >= 0 &&
    compareRational(toExact(rect.y), zeroRational) >= 0 &&
    compareRational(rationalAdd(toExact(rect.x), toExact(rect.width)), oneRational) <= 0 &&
    compareRational(rationalAdd(toExact(rect.y), toExact(rect.height)), oneRational) <= 0
}

function rectContains(outer: LayoutRect, inner: LayoutRect): boolean {
  return compareRational(toExact(inner.x), toExact(outer.x)) >= 0 &&
    compareRational(toExact(inner.y), toExact(outer.y)) >= 0 &&
    compareRational(rationalAdd(toExact(inner.x), toExact(inner.width)), rationalAdd(toExact(outer.x), toExact(outer.width))) <= 0 &&
    compareRational(rationalAdd(toExact(inner.y), toExact(inner.height)), rationalAdd(toExact(outer.y), toExact(outer.height))) <= 0
}

function isFullFrame(rect: LayoutRect): boolean {
  return rationalEquals(toExact(rect.x), zeroRational) &&
    rationalEquals(toExact(rect.y), zeroRational) &&
    rationalEquals(toExact(rect.width), oneRational) &&
    rationalEquals(toExact(rect.height), oneRational)
}

function rectProjectsToPixels(
  rect: LayoutRect,
  pixels: LayoutPixelRect,
  width: number,
  height: number,
): boolean {
  const projected = projectRect(rect, width, height)
  return projected !== null && pixelRectsEqual(projected, pixels)
}

function projectRectIfNormalized(rect: LayoutRect, width: number, height: number): LayoutPixelRect | null {
  return isNormalizedRect(rect) ? projectRect(rect, width, height) : null
}

function projectRect(rect: LayoutRect, width: number, height: number): LayoutPixelRect | null {
  const x = projectCoordinate(rect.x, width)
  const y = projectCoordinate(rect.y, height)
  const projectedWidth = projectCoordinate(rect.width, width)
  const projectedHeight = projectCoordinate(rect.height, height)
  if (
    x === null ||
    y === null ||
    projectedWidth === null ||
    projectedHeight === null ||
    x < 0 ||
    y < 0 ||
    projectedWidth <= 0 ||
    projectedHeight <= 0 ||
    x + projectedWidth > width ||
    y + projectedHeight > height
  ) {
    return null
  }
  return Object.freeze({ x, y, width: projectedWidth, height: projectedHeight })
}

function projectCoordinate(value: LayoutRational, extent: number): number | null {
  const numerator = BigInt(value.numerator) * BigInt(extent)
  const denominator = BigInt(value.denominator)
  if (numerator % denominator !== 0n) {
    return null
  }
  const projected = numerator / denominator
  return projected >= BigInt(-2_147_483_648) && projected <= BigInt(2_147_483_647)
    ? Number(projected)
    : null
}

function rectsEqual(left: LayoutRect, right: LayoutRect): boolean {
  return rationalEquals(toExact(left.x), toExact(right.x)) &&
    rationalEquals(toExact(left.y), toExact(right.y)) &&
    rationalEquals(toExact(left.width), toExact(right.width)) &&
    rationalEquals(toExact(left.height), toExact(right.height))
}

function pixelRectsEqual(left: LayoutPixelRect, right: LayoutPixelRect): boolean {
  return left.x === right.x && left.y === right.y && left.width === right.width && left.height === right.height
}

interface ExactRational {
  readonly numerator: bigint
  readonly denominator: bigint
}

function toExact(value: LayoutRational): ExactRational {
  return normalizeRational(BigInt(value.numerator), BigInt(value.denominator))
}

function timeToRational(value: LayoutRationalTime): ExactRational {
  return normalizeRational(BigInt(value.value), BigInt(value.rate))
}

function rationalToTime(value: ExactRational): LayoutRationalTime {
  const normalized = normalizeRational(value.numerator, value.denominator)
  if (normalized.numerator < minimumInt64 || normalized.numerator > maximumInt64 || normalized.denominator > maximumInt64) {
    throw new RangeError('Exact time arithmetic exceeds signed Int64.')
  }
  return Object.freeze({ value: normalized.numerator.toString(), rate: normalized.denominator.toString() })
}

function normalizeRational(numerator: bigint, denominator: bigint): ExactRational {
  if (denominator === 0n) {
    throw new RangeError('A rational denominator must be positive.')
  }
  if (denominator < 0n) {
    numerator = -numerator
    denominator = -denominator
  }
  const divisor = gcd(numerator, denominator)
  return { numerator: numerator / divisor, denominator: denominator / divisor }
}

function rationalAdd(left: ExactRational, right: ExactRational): ExactRational {
  return normalizeRational(
    left.numerator * right.denominator + right.numerator * left.denominator,
    left.denominator * right.denominator,
  )
}

function rationalMultiply(left: ExactRational, right: ExactRational): ExactRational {
  return normalizeRational(
    left.numerator * right.numerator,
    left.denominator * right.denominator,
  )
}

function rationalDivide(left: ExactRational, right: ExactRational): ExactRational {
  if (right.numerator === 0n) {
    throw new RangeError('A rational divisor must be non-zero.')
  }
  return normalizeRational(
    left.numerator * right.denominator,
    left.denominator * right.numerator,
  )
}

function rationalDifference(left: LayoutRationalTime, right: LayoutRationalTime): ExactRational {
  const a = timeToRational(left)
  const b = timeToRational(right)
  return normalizeRational(
    a.numerator * b.denominator - b.numerator * a.denominator,
    a.denominator * b.denominator,
  )
}

function compareRational(left: ExactRational, right: ExactRational): number {
  const difference = left.numerator * right.denominator - right.numerator * left.denominator
  return difference < 0n ? -1 : difference > 0n ? 1 : 0
}

function rationalEquals(left: ExactRational, right: ExactRational): boolean {
  return compareRational(left, right) === 0
}

function gcd(left: bigint, right: bigint): bigint {
  let a = left < 0n ? -left : left
  let b = right < 0n ? -right : right
  while (b !== 0n) {
    const remainder = a % b
    a = b
    b = remainder
  }
  return a
}

function parseUniqueArray<T>(
  value: readonly unknown[],
  parse: (item: unknown) => T | null,
  key: (item: T) => string,
): readonly T[] | null {
  const items: T[] = []
  const keys = new Set<string>()
  for (const candidate of value) {
    const item = parse(candidate)
    if (item === null || keys.has(key(item))) {
      return null
    }
    keys.add(key(item))
    items.push(item)
  }
  return Object.freeze(items)
}

function parseEnumArray<T extends string>(value: readonly unknown[], allowed: ReadonlySet<T>): readonly T[] | null {
  const parsed: T[] = []
  const seen = new Set<T>()
  for (const item of value) {
    if (typeof item !== 'string' || !allowed.has(item as T) || seen.has(item as T)) {
      return null
    }
    seen.add(item as T)
    parsed.push(item as T)
  }
  return Object.freeze(parsed)
}

function ensureBoundedRequest<T extends LayoutBridgeRequest>(request: T): T {
  if (!isWithinUtf8Limit(request, LAYOUT_MAX_REQUEST_UTF8_BYTES)) {
    throw new RangeError('The layout bridge request exceeds the 64 KiB UTF-8 limit.')
  }
  return request
}

function isWithinUtf8Limit(value: unknown, maximumBytes: number): boolean {
  try {
    const serialized = JSON.stringify(value)
    return serialized !== undefined && utf8Length(serialized) <= maximumBytes
  } catch {
    return false
  }
}

function isProtocolText(value: unknown): value is string {
  return typeof value === 'string' &&
    value.length > 0 &&
    value === value.trim() &&
    hasWellFormedUnicode(value) &&
    utf8Length(value) <= maximumTextUtf8Bytes
}

function isResponseText(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && hasWellFormedUnicode(value)
}

function hasWellFormedUnicode(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index)
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(index + 1)
      if (!(next >= 0xdc00 && next <= 0xdfff)) {
        return false
      }
      index++
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return false
    }
  }
  return true
}

function utf8Length(value: string): number {
  return new TextEncoder().encode(value).byteLength
}

function isCanonicalId(value: unknown): value is string {
  return typeof value === 'string' && canonicalIdPattern.test(value)
}

function isDigest(value: unknown): value is string {
  return typeof value === 'string' && digestPattern.test(value)
}

function isCanonicalInt64(value: unknown, positive: boolean): value is string {
  if (typeof value !== 'string' || !(positive ? positiveInt64Pattern : signedInt64Pattern).test(value)) {
    return false
  }
  const parsed = BigInt(value)
  return parsed >= (positive ? 1n : minimumInt64) && parsed <= maximumInt64
}

function isNonnegativeInt32(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= 2_147_483_647
}

function isPositiveInt32(value: unknown): value is number {
  return isNonnegativeInt32(value) && value > 0
}

function hasAtLeastKind(value: unknown): value is Record<string, unknown> & { readonly kind: unknown } {
  return isRecord(value) && Object.prototype.hasOwnProperty.call(value, 'kind')
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function hasExactKeys<const TKey extends string>(
  value: unknown,
  expected: readonly TKey[],
): value is Record<TKey, unknown> {
  if (!isRecord(value)) {
    return false
  }
  const keys = Object.keys(value)
  return keys.length === expected.length && expected.every((key) => Object.prototype.hasOwnProperty.call(value, key))
}

const maximumKeyframeCount = 256
const maximumTextUtf8Bytes = 16 * 1024
const maximumInt64 = 9_223_372_036_854_775_807n
const minimumInt64 = -9_223_372_036_854_775_808n
const zeroTime: LayoutRationalTime = Object.freeze({ value: '0', rate: '1' })
const zeroRational: ExactRational = Object.freeze({ numerator: 0n, denominator: 1n })
const oneRational: ExactRational = Object.freeze({ numerator: 1n, denominator: 1n })
const canonicalIdPattern = /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const digestPattern = /^[0-9a-f]{64}$/
const signedInt64Pattern = /^(?:0|-?[1-9][0-9]{0,18})$/
const positiveInt64Pattern = /^[1-9][0-9]{0,18}$/
const dotNetWhitespaceCharacterPattern =
  /^[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]$/u
const dotNetWhitespaceAtEndsPattern =
  /^[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+|[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+$/gu
const manualModes = new Set<ManualLayoutMode>([
  'singleSubjectCrop',
  'twoSpeakerStack',
  'speakerMaterialStack',
  'materialWithSpeakerInset',
  'importantRegionZoom',
  'blurredSourceBackground',
  'fixedTemplatePlacement',
])
const modesWithTwoSources = new Set<ManualLayoutMode>([
  'twoSpeakerStack',
  'speakerMaterialStack',
  'materialWithSpeakerInset',
  'blurredSourceBackground',
])
const staticCropDecisions = new Set<LayoutManualOperation['staticCropDecision']>([
  'singleStaticCropSufficient',
  'manualKeyframesRequired',
])
const destinationSlots = new Set<ManualDestinationSlot>([
  'singleSubject',
  'topSpeaker',
  'bottomSpeaker',
  'stackedSpeaker',
  'stackedMaterial',
  'mainMaterial',
  'speakerInset',
  'zoomedImportantRegion',
  'blurredBackground',
  'foregroundSubject',
  'templateVideo',
])
const interpolations = new Set<LayoutInterpolation>(['hold', 'linear'])
const cueStyles = new Set<LayoutCueStyle>(['normal', 'emphasis', 'caution', 'quote'])
const subtitleCueModes = new Set<LayoutSubtitleCueMode>(['full', 'selected'])
const subtitlePunctuationDisplays = new Set<LayoutSubtitlePunctuation>([
  'preserve',
  'hideJapaneseCommaAndPeriod',
])
const regionRoles = new Set<LayoutResolvedRegion['role']>(['video', 'material', 'subtitle', 'graphic'])
const semanticRoles = new Set<LayoutManualPlacement['semanticRole']>(['subject', 'speaker', 'material', 'importantRegion', 'templateVideo'])
const treatments = new Set<LayoutManualPlacement['treatment']>(['standard', 'blurredBackground'])
const manualStatuses = new Set<LayoutManualSelection['status']>(['resolved', 'manualKeyframesRequired'])
const subtitleStatuses = new Set<LayoutSubtitleResolution['status']>(['resolved', 'manualAdjustmentRequired'])
const teropSourceKinds = new Set<LayoutTeropCue['sourceKind']>(['adoptedSuggestion', 'manualEditorial'])
const hardConstraints = new Set<LayoutHardConstraint>([
  'noPlacementCandidate',
  'sourceTimingAdjustmentRequired',
  'requiredSafeZoneMissing',
  'outsideOutputFrame',
  'unknownLayoutRegion',
  'layoutRegionIsNotSubtitle',
  'outsideSubtitleRegion',
  'outsideRequiredSafeZone',
  'prohibitedRegionOverlap',
])
const softConstraints = new Set<LayoutSoftConstraint>([
  'faceRegionOverlap',
  'materialRegionOverlap',
  'graphicRegionOverlap',
  'placementChanged',
])
const layoutFailureStatuses = new Set<Exclude<LayoutBridgeStatus, 'available'>>([
  'forbidden',
  'notFound',
  'conflict',
  'noChange',
  'invalid',
  'unavailable',
])
const expectedManualPlacements: Readonly<Record<ManualLayoutMode, readonly Readonly<{
  slot: ManualDestinationSlot
  role: LayoutManualPlacement['semanticRole']
  treatment: LayoutManualPlacement['treatment']
}>[]>> = Object.freeze({
  singleSubjectCrop: [Object.freeze({ slot: 'singleSubject', role: 'subject', treatment: 'standard' })],
  twoSpeakerStack: [
    Object.freeze({ slot: 'topSpeaker', role: 'speaker', treatment: 'standard' }),
    Object.freeze({ slot: 'bottomSpeaker', role: 'speaker', treatment: 'standard' }),
  ],
  speakerMaterialStack: [
    Object.freeze({ slot: 'stackedSpeaker', role: 'speaker', treatment: 'standard' }),
    Object.freeze({ slot: 'stackedMaterial', role: 'material', treatment: 'standard' }),
  ],
  materialWithSpeakerInset: [
    Object.freeze({ slot: 'mainMaterial', role: 'material', treatment: 'standard' }),
    Object.freeze({ slot: 'speakerInset', role: 'speaker', treatment: 'standard' }),
  ],
  importantRegionZoom: [Object.freeze({ slot: 'zoomedImportantRegion', role: 'importantRegion', treatment: 'standard' })],
  blurredSourceBackground: [
    Object.freeze({ slot: 'blurredBackground', role: 'subject', treatment: 'blurredBackground' }),
    Object.freeze({ slot: 'foregroundSubject', role: 'subject', treatment: 'standard' }),
  ],
  fixedTemplatePlacement: [Object.freeze({ slot: 'templateVideo', role: 'templateVideo', treatment: 'standard' })],
})
