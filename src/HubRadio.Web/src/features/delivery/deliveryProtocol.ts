import { BRIDGE_PROTOCOL_VERSION } from '../../bridge/protocol'

export const DELIVERY_READ_METHOD = 'delivery.read' as const
export const DELIVERY_MAX_REQUEST_UTF8_BYTES = 512
export const DELIVERY_MAX_RESPONSE_UTF8_BYTES = 1024 * 1024

export type DeliveryReadStatus =
  | 'available'
  | 'forbidden'
  | 'unavailable'
  | 'incomplete'
  | 'mismatched'
  | 'qualityControlFailed'

export type DeliveryPublicationScope =
  | 'internalOnly'
  | 'specificGroupOnly'
  | 'externalCandidate'
  | 'externalPublicationApproved'
  | 'publicationForbidden'
  | 'pendingDecision'

export type DeliveryReviewState =
  | 'editing'
  | 'editorReview'
  | 'performerReview'
  | 'contentReview'
  | 'brandReview'
  | 'changesRequested'
  | 'approved'
  | 'publicationBlocked'
  | 'finalRendering'
  | 'qualityControl'
  | 'exported'
  | 'published'

export type DeliveryReviewActorRole =
  | 'editor'
  | 'performerReviewer'
  | 'contentApprover'
  | 'brandApprover'
  | 'rightsApprover'
  | 'administrator'

export type DeliveryApprovalActorRole = Extract<
  DeliveryReviewActorRole,
  'contentApprover' | 'brandApprover' | 'rightsApprover'
>

export type DeliveryRightsStatus = 'unconfirmed' | 'approved' | 'rejected'

export interface DeliveryRationalTime {
  readonly value: string
  readonly rate: string
}

export interface DeliveryArtifact {
  readonly artifactId: string
  readonly artifactType: string
  readonly mediaType: string
  readonly language: string | null
  readonly byteSize: string
  readonly sha256: string
}

export interface DeliveryReviewRecord {
  readonly reviewRecordId: string
  readonly state: DeliveryReviewState
  readonly actorRole: DeliveryReviewActorRole
  readonly recordedAt: string
}

export interface DeliveryApproval {
  readonly approvalId: string
  readonly actorRole: DeliveryApprovalActorRole
  readonly approvedAt: string
}

export interface DeliveryReviewEvidence {
  readonly records: readonly DeliveryReviewRecord[]
  readonly approvals: readonly DeliveryApproval[]
}

export interface DeliveryRightsEvidence {
  readonly assetId: string
  readonly sourceDigest: string
  readonly status: DeliveryRightsStatus
  readonly evidenceRecorded: boolean
  readonly allowedUse: string
  readonly externalPublicationAllowed: boolean
}

export interface DeliveryQcStream {
  readonly streamIndex: number
  readonly observedCount: number
  readonly presentationStart: DeliveryRationalTime
  readonly presentationEnd: DeliveryRationalTime
  readonly observedDuration: DeliveryRationalTime
  readonly evidenceDigest: string
}

export interface DeliveryQcSubtitleTiming {
  readonly streamIndex: number | null
  readonly observedCueCount: number | null
  readonly evidenceDigest: string | null
}

export interface DeliveryQcDuration {
  readonly expected: DeliveryRationalTime
  readonly videoDelta: DeliveryRationalTime
  readonly audioDelta: DeliveryRationalTime | null
  readonly audioVideoStartOffset: DeliveryRationalTime | null
  readonly audioVideoEndOffset: DeliveryRationalTime | null
}

export interface DeliveryQcLuma {
  readonly measuredFrameCount: number
  readonly minimumFrameAverage: string
  readonly maximumFrameAverage: string
  readonly meanFrameAverage: string
  readonly evidenceDigest: string
}

export type DeliveryQcMeasuredValue =
  | Readonly<{ kind: 'finite'; value: string }>
  | Readonly<{
      kind: 'negativeInfinity' | 'positiveInfinity'
      value: null
    }>

export interface DeliveryQcAudio {
  readonly integratedLoudnessLufs: DeliveryQcMeasuredValue
  readonly truePeakDbfs: DeliveryQcMeasuredValue
  readonly evidenceDigest: string
}

export interface DeliveryQcTool {
  readonly name: string
  readonly version: string
  readonly executableDigest: string
  readonly executableByteSize: string
}

export type DeliveryQcReviewArea =
  | 'duration'
  | 'audioVideoSynchronization'
  | 'subtitlePresentation'
  | 'screenLuma'
  | 'programLoudness'
  | 'truePeak'

export interface DeliveryQualityControl {
  readonly artifactId: string
  readonly sha256: string
  readonly byteSize: string
  readonly machineChecksPassed: true
  readonly humanReviewRequired: true
  readonly approvedNumericThresholdsApplied: false
  readonly canProceedToDelivery: false
  readonly decodedSuccessfully: true
  readonly probeEvidenceDigest: string
  readonly videoTiming: DeliveryQcStream
  readonly audioTiming: DeliveryQcStream | null
  readonly subtitleTiming: DeliveryQcSubtitleTiming
  readonly duration: DeliveryQcDuration
  readonly luma: DeliveryQcLuma
  readonly audio: DeliveryQcAudio | null
  readonly tools: readonly DeliveryQcTool[]
  readonly requiredHumanReviewAreas: readonly DeliveryQcReviewArea[]
}

export interface DeliverySourceSpan {
  readonly sourceSpanId: string
  readonly assetId: string
  readonly streamId: string
  readonly start: DeliveryRationalTime
  readonly end: DeliveryRationalTime
  readonly sourceDigest: string
}

export interface DeliveryTraceNotFound {
  readonly status: 'notFound'
  readonly outputTime: DeliveryRationalTime
  readonly originKind: null
  readonly originId: null
  readonly sourceTime: null
  readonly sourceSpan: null
}

export interface DeliverySourceSpanTrace {
  readonly status: 'resolved'
  readonly outputTime: DeliveryRationalTime
  readonly originKind: 'sourceSpan'
  readonly originId: string
  readonly sourceTime: DeliveryRationalTime
  readonly sourceSpan: DeliverySourceSpan
}

export interface DeliveryNonSpanTrace {
  readonly status: 'resolved'
  readonly outputTime: DeliveryRationalTime
  readonly originKind: 'asset' | 'editorialRecord'
  readonly originId: string
  readonly sourceTime: null
  readonly sourceSpan: null
}

export type DeliveryTrace =
  | DeliveryTraceNotFound
  | DeliverySourceSpanTrace
  | DeliveryNonSpanTrace

export interface DeliverySnapshot {
  readonly deliveryManifestId: string
  readonly projectId: string
  readonly versionId: string
  readonly versionDigest: string
  readonly renderManifestId: string
  readonly profileId: string
  readonly profileDigest: string
  readonly deliveryEvidenceDigest: string
  readonly usedSourceSpansDigest: string
  readonly approvalEvidenceDigest: string
  readonly publicationScope: DeliveryPublicationScope
  readonly artifacts: readonly DeliveryArtifact[]
  readonly review: DeliveryReviewEvidence
  readonly rights: readonly DeliveryRightsEvidence[]
  readonly qualityControl: DeliveryQualityControl
}

export interface DeliveryReadRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof DELIVERY_READ_METHOD
  readonly params: Readonly<{
    versionId: string
    outputTime: DeliveryRationalTime | null
  }>
}

export interface DeliveryAvailableResult {
  readonly status: 'available'
  readonly snapshot: DeliverySnapshot
  readonly trace: DeliveryTrace | null
}

export interface DeliveryFailureResult {
  readonly status: Exclude<DeliveryReadStatus, 'available'>
  readonly snapshot: null
  readonly trace: null
}

export type DeliveryReadResult = DeliveryAvailableResult | DeliveryFailureResult

export interface DeliveryReadResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof DELIVERY_READ_METHOD
  readonly result: DeliveryReadResult
}

export function createDeliveryReadRequest(
  requestId: string,
  versionId: string,
  outputTime: DeliveryRationalTime | null,
): DeliveryReadRequest {
  requireEntityId(requestId, 'A bridge request ID')
  requireEntityId(versionId, 'A composition version ID')
  const canonicalOutputTime = outputTime === null
    ? null
    : parseRationalTime(outputTime, false)
  if (outputTime !== null && canonicalOutputTime === null) {
    throw new Error(
      'A delivery output time must be a reduced nonnegative signed-64-bit rational.',
    )
  }

  const request: DeliveryReadRequest = Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: DELIVERY_READ_METHOD,
    params: Object.freeze({ versionId, outputTime: canonicalOutputTime }),
  })
  if (!isWithinUtf8Limit(request, DELIVERY_MAX_REQUEST_UTF8_BYTES)) {
    throw new Error('The delivery request exceeds the supported UTF-8 byte limit.')
  }
  return request
}

export function parseDeliveryReadResponse(
  value: unknown,
): DeliveryReadResponse | null {
  if (
    !isWithinUtf8Limit(value, DELIVERY_MAX_RESPONSE_UTF8_BYTES) ||
    !isRecordWithExactKeys(value, ['version', 'requestId', 'method', 'result']) ||
    value.version !== BRIDGE_PROTOCOL_VERSION ||
    !isEntityId(value.requestId) ||
    value.method !== DELIVERY_READ_METHOD ||
    !isRecordWithExactKeys(value.result, ['status', 'snapshot', 'trace'])
  ) {
    return null
  }

  const result = value.result
  if (deliveryFailureStatuses.has(result.status as DeliveryFailureResult['status'])) {
    if (result.snapshot !== null || result.trace !== null) {
      return null
    }
    return Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId: value.requestId,
      method: DELIVERY_READ_METHOD,
      result: Object.freeze({
        status: result.status as DeliveryFailureResult['status'],
        snapshot: null,
        trace: null,
      }),
    })
  }

  if (result.status !== 'available') {
    return null
  }
  const snapshot = parseSnapshot(result.snapshot)
  if (snapshot === null) {
    return null
  }
  const trace = result.trace === null
    ? null
    : parseTrace(result.trace, snapshot)
  if (result.trace !== null && trace === null) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method: DELIVERY_READ_METHOD,
    result: Object.freeze({ status: 'available', snapshot, trace }),
  })
}

export function isCanonicalDeliveryEntityId(value: string): boolean {
  return entityIdPattern.test(value)
}

export function isDeliveryOutputTime(value: DeliveryRationalTime): boolean {
  return parseRationalTime(value, false) !== null
}

export function deliveryOutputTimesEqual(
  left: DeliveryRationalTime,
  right: DeliveryRationalTime,
): boolean {
  return left.value === right.value && left.rate === right.rate
}

function parseSnapshot(value: unknown): DeliverySnapshot | null {
  if (
    !isRecordWithExactKeys(value, [
      'deliveryManifestId',
      'projectId',
      'versionId',
      'versionDigest',
      'renderManifestId',
      'profileId',
      'profileDigest',
      'deliveryEvidenceDigest',
      'usedSourceSpansDigest',
      'approvalEvidenceDigest',
      'publicationScope',
      'artifacts',
      'review',
      'rights',
      'qualityControl',
    ]) ||
    !isEntityId(value.deliveryManifestId) ||
    !isEntityId(value.projectId) ||
    !isEntityId(value.versionId) ||
    !isDigest(value.versionDigest) ||
    !isEntityId(value.renderManifestId) ||
    !isEntityId(value.profileId) ||
    !isDigest(value.profileDigest) ||
    !isDigest(value.deliveryEvidenceDigest) ||
    !isDigest(value.usedSourceSpansDigest) ||
    !isDigest(value.approvalEvidenceDigest) ||
    !publicationScopes.has(value.publicationScope as DeliveryPublicationScope)
  ) {
    return null
  }

  const artifacts = parseArtifacts(value.artifacts)
  const review = parseReview(value.review)
  const rights = parseRights(value.rights)
  const qualityControl = parseQualityControl(value.qualityControl)
  if (
    artifacts === null ||
    review === null ||
    rights === null ||
    qualityControl === null
  ) {
    return null
  }

  const qcArtifacts = artifacts.filter(
    (artifact) => artifact.artifactId === qualityControl.artifactId,
  )
  if (
    qcArtifacts.length !== 1 ||
    qcArtifacts[0]!.sha256 !== qualityControl.sha256 ||
    qcArtifacts[0]!.byteSize !== qualityControl.byteSize
  ) {
    return null
  }

  return Object.freeze({
    deliveryManifestId: value.deliveryManifestId,
    projectId: value.projectId,
    versionId: value.versionId,
    versionDigest: value.versionDigest,
    renderManifestId: value.renderManifestId,
    profileId: value.profileId,
    profileDigest: value.profileDigest,
    deliveryEvidenceDigest: value.deliveryEvidenceDigest,
    usedSourceSpansDigest: value.usedSourceSpansDigest,
    approvalEvidenceDigest: value.approvalEvidenceDigest,
    publicationScope: value.publicationScope as DeliveryPublicationScope,
    artifacts,
    review,
    rights,
    qualityControl,
  })
}

function parseArtifacts(value: unknown): readonly DeliveryArtifact[] | null {
  if (!isBoundedArray(value, 1, maximumCollectionCount)) {
    return null
  }
  const ids = new Set<string>()
  const artifacts: DeliveryArtifact[] = []
  for (const item of value) {
    if (
      !isRecordWithExactKeys(item, [
        'artifactId',
        'artifactType',
        'mediaType',
        'language',
        'byteSize',
        'sha256',
      ]) ||
      !isEntityId(item.artifactId) ||
      ids.has(item.artifactId) ||
      !isBoundedText(item.artifactType) ||
      !isBoundedText(item.mediaType) ||
      !(item.language === null || isBoundedText(item.language)) ||
      !isPositiveInt64String(item.byteSize) ||
      !isDigest(item.sha256)
    ) {
      return null
    }
    ids.add(item.artifactId)
    artifacts.push(Object.freeze({
      artifactId: item.artifactId,
      artifactType: item.artifactType,
      mediaType: item.mediaType,
      language: item.language,
      byteSize: item.byteSize,
      sha256: item.sha256,
    }))
  }
  return Object.freeze(artifacts)
}

function parseReview(value: unknown): DeliveryReviewEvidence | null {
  if (!isRecordWithExactKeys(value, ['records', 'approvals'])) {
    return null
  }
  const records = parseReviewRecords(value.records)
  const approvals = parseApprovals(value.approvals)
  if (records === null || approvals === null) {
    return null
  }
  return Object.freeze({ records, approvals })
}

function parseReviewRecords(
  value: unknown,
): readonly DeliveryReviewRecord[] | null {
  if (!isBoundedArray(value, 0, maximumCollectionCount)) {
    return null
  }
  const records: DeliveryReviewRecord[] = []
  const ids = new Set<string>()
  let previousSortKey: string | null = null
  for (const item of value) {
    if (
      !isRecordWithExactKeys(item, [
        'reviewRecordId',
        'state',
        'actorRole',
        'recordedAt',
      ]) ||
      !isEntityId(item.reviewRecordId) ||
      ids.has(item.reviewRecordId) ||
      !reviewStates.has(item.state as DeliveryReviewState) ||
      !reviewActorRoles.has(item.actorRole as DeliveryReviewActorRole) ||
      !isNativeTimestamp(item.recordedAt)
    ) {
      return null
    }
    const sortKey = `${item.recordedAt}\u0000${item.reviewRecordId}`
    if (previousSortKey !== null && sortKey <= previousSortKey) {
      return null
    }
    previousSortKey = sortKey
    ids.add(item.reviewRecordId)
    records.push(Object.freeze({
      reviewRecordId: item.reviewRecordId,
      state: item.state as DeliveryReviewState,
      actorRole: item.actorRole as DeliveryReviewActorRole,
      recordedAt: item.recordedAt,
    }))
  }
  return Object.freeze(records)
}

function parseApprovals(value: unknown): readonly DeliveryApproval[] | null {
  if (!isBoundedArray(value, 1, maximumCollectionCount)) {
    return null
  }
  const approvals: DeliveryApproval[] = []
  const ids = new Set<string>()
  let previousSortKey: string | null = null
  for (const item of value) {
    if (
      !isRecordWithExactKeys(item, ['approvalId', 'actorRole', 'approvedAt']) ||
      !isEntityId(item.approvalId) ||
      ids.has(item.approvalId) ||
      !reviewApprovalActorRoles.has(item.actorRole as DeliveryApprovalActorRole) ||
      !isNativeTimestamp(item.approvedAt)
    ) {
      return null
    }
    const sortKey = `${item.approvedAt}\u0000${item.approvalId}`
    if (previousSortKey !== null && sortKey <= previousSortKey) {
      return null
    }
    previousSortKey = sortKey
    ids.add(item.approvalId)
    approvals.push(Object.freeze({
      approvalId: item.approvalId,
      actorRole: item.actorRole as DeliveryApprovalActorRole,
      approvedAt: item.approvedAt,
    }))
  }
  return Object.freeze(approvals)
}

function parseRights(value: unknown): readonly DeliveryRightsEvidence[] | null {
  if (!isBoundedArray(value, 0, maximumCollectionCount)) {
    return null
  }
  const rights: DeliveryRightsEvidence[] = []
  let previousAssetId: string | null = null
  for (const item of value) {
    if (
      !isRecordWithExactKeys(item, [
        'assetId',
        'sourceDigest',
        'status',
        'evidenceRecorded',
        'allowedUse',
        'externalPublicationAllowed',
      ]) ||
      !isEntityId(item.assetId) ||
      !isDigest(item.sourceDigest) ||
      !rightsStatuses.has(item.status as DeliveryRightsStatus) ||
      typeof item.evidenceRecorded !== 'boolean' ||
      !isBoundedText(item.allowedUse) ||
      typeof item.externalPublicationAllowed !== 'boolean' ||
      (item.status === 'approved' && !item.evidenceRecorded) ||
      (item.externalPublicationAllowed && item.status !== 'approved') ||
      (previousAssetId !== null && item.assetId <= previousAssetId)
    ) {
      return null
    }
    previousAssetId = item.assetId
    rights.push(Object.freeze({
      assetId: item.assetId,
      sourceDigest: item.sourceDigest,
      status: item.status as DeliveryRightsStatus,
      evidenceRecorded: item.evidenceRecorded,
      allowedUse: item.allowedUse,
      externalPublicationAllowed: item.externalPublicationAllowed,
    }))
  }
  return Object.freeze(rights)
}

function parseQualityControl(value: unknown): DeliveryQualityControl | null {
  if (
    !isRecordWithExactKeys(value, [
      'artifactId',
      'sha256',
      'byteSize',
      'machineChecksPassed',
      'humanReviewRequired',
      'approvedNumericThresholdsApplied',
      'canProceedToDelivery',
      'decodedSuccessfully',
      'probeEvidenceDigest',
      'videoTiming',
      'audioTiming',
      'subtitleTiming',
      'duration',
      'luma',
      'audio',
      'tools',
      'requiredHumanReviewAreas',
    ]) ||
    !isEntityId(value.artifactId) ||
    !isDigest(value.sha256) ||
    !isPositiveInt64String(value.byteSize) ||
    value.machineChecksPassed !== true ||
    value.humanReviewRequired !== true ||
    value.approvedNumericThresholdsApplied !== false ||
    value.canProceedToDelivery !== false ||
    value.decodedSuccessfully !== true ||
    !isDigest(value.probeEvidenceDigest)
  ) {
    return null
  }

  const videoTiming = parseQcStream(value.videoTiming)
  const audioTiming = value.audioTiming === null
    ? null
    : parseQcStream(value.audioTiming)
  const subtitleTiming = parseSubtitleTiming(value.subtitleTiming)
  const duration = parseQcDuration(value.duration)
  const luma = parseQcLuma(value.luma)
  const audio = value.audio === null ? null : parseQcAudio(value.audio)
  const tools = parseQcTools(value.tools)
  const requiredHumanReviewAreas = parseReviewAreas(
    value.requiredHumanReviewAreas,
  )
  if (
    videoTiming === null ||
    (value.audioTiming !== null && audioTiming === null) ||
    subtitleTiming === null ||
    duration === null ||
    luma === null ||
    (value.audio !== null && audio === null) ||
    tools === null ||
    requiredHumanReviewAreas === null ||
    !qualityControlSemanticsAreValid(
      videoTiming,
      audioTiming,
      subtitleTiming,
      duration,
      luma,
      audio,
      requiredHumanReviewAreas,
    )
  ) {
    return null
  }

  return Object.freeze({
    artifactId: value.artifactId,
    sha256: value.sha256,
    byteSize: value.byteSize,
    machineChecksPassed: true,
    humanReviewRequired: true,
    approvedNumericThresholdsApplied: false,
    canProceedToDelivery: false,
    decodedSuccessfully: true,
    probeEvidenceDigest: value.probeEvidenceDigest,
    videoTiming,
    audioTiming,
    subtitleTiming,
    duration,
    luma,
    audio,
    tools,
    requiredHumanReviewAreas,
  })
}

function parseQcStream(value: unknown): DeliveryQcStream | null {
  if (
    !isRecordWithExactKeys(value, [
      'streamIndex',
      'observedCount',
      'presentationStart',
      'presentationEnd',
      'observedDuration',
      'evidenceDigest',
    ]) ||
    !isNonnegativeInt32(value.streamIndex) ||
    !isPositiveInt32(value.observedCount) ||
    !isDigest(value.evidenceDigest)
  ) {
    return null
  }
  const presentationStart = parseRationalTime(value.presentationStart, true)
  const presentationEnd = parseRationalTime(value.presentationEnd, true)
  const observedDuration = parseRationalTime(value.observedDuration, true)
  if (
    presentationStart === null ||
    presentationEnd === null ||
    observedDuration === null ||
    compareRational(observedDuration, zeroTime) <= 0 ||
    compareRational(presentationEnd, presentationStart) <= 0 ||
    !rationalDifferenceEquals(
      presentationEnd,
      presentationStart,
      observedDuration,
    )
  ) {
    return null
  }
  return Object.freeze({
    streamIndex: value.streamIndex,
    observedCount: value.observedCount,
    presentationStart,
    presentationEnd,
    observedDuration,
    evidenceDigest: value.evidenceDigest,
  })
}

function parseSubtitleTiming(value: unknown): DeliveryQcSubtitleTiming | null {
  if (
    !isRecordWithExactKeys(value, [
      'streamIndex',
      'observedCueCount',
      'evidenceDigest',
    ])
  ) {
    return null
  }
  if (
    value.streamIndex === null &&
    value.observedCueCount === null &&
    value.evidenceDigest === null
  ) {
    return Object.freeze({
      streamIndex: null,
      observedCueCount: null,
      evidenceDigest: null,
    })
  }
  if (
    !isNonnegativeInt32(value.streamIndex) ||
    !isPositiveInt32(value.observedCueCount) ||
    !isDigest(value.evidenceDigest)
  ) {
    return null
  }
  return Object.freeze({
    streamIndex: value.streamIndex,
    observedCueCount: value.observedCueCount,
    evidenceDigest: value.evidenceDigest,
  })
}

function parseQcDuration(value: unknown): DeliveryQcDuration | null {
  if (
    !isRecordWithExactKeys(value, [
      'expected',
      'videoDelta',
      'audioDelta',
      'audioVideoStartOffset',
      'audioVideoEndOffset',
    ])
  ) {
    return null
  }
  const expected = parseRationalTime(value.expected, true)
  const videoDelta = parseRationalTime(value.videoDelta, true)
  const audioDelta = parseNullableRational(value.audioDelta)
  const audioVideoStartOffset = parseNullableRational(
    value.audioVideoStartOffset,
  )
  const audioVideoEndOffset = parseNullableRational(value.audioVideoEndOffset)
  if (
    expected === null ||
    compareRational(expected, zeroTime) <= 0 ||
    videoDelta === null ||
    audioDelta === undefined ||
    audioVideoStartOffset === undefined ||
    audioVideoEndOffset === undefined
  ) {
    return null
  }
  return Object.freeze({
    expected,
    videoDelta,
    audioDelta,
    audioVideoStartOffset,
    audioVideoEndOffset,
  })
}

function parseQcLuma(value: unknown): DeliveryQcLuma | null {
  if (
    !isRecordWithExactKeys(value, [
      'measuredFrameCount',
      'minimumFrameAverage',
      'maximumFrameAverage',
      'meanFrameAverage',
      'evidenceDigest',
    ]) ||
    !isPositiveInt32(value.measuredFrameCount) ||
    !isDecimalString(value.minimumFrameAverage) ||
    !isDecimalString(value.maximumFrameAverage) ||
    !isDecimalString(value.meanFrameAverage) ||
    !isDigest(value.evidenceDigest) ||
    compareDecimals(value.minimumFrameAverage, '0') < 0 ||
    compareDecimals(value.maximumFrameAverage, '255') > 0 ||
    compareDecimals(value.minimumFrameAverage, value.meanFrameAverage) > 0 ||
    compareDecimals(value.meanFrameAverage, value.maximumFrameAverage) > 0
  ) {
    return null
  }
  return Object.freeze({
    measuredFrameCount: value.measuredFrameCount,
    minimumFrameAverage: value.minimumFrameAverage,
    maximumFrameAverage: value.maximumFrameAverage,
    meanFrameAverage: value.meanFrameAverage,
    evidenceDigest: value.evidenceDigest,
  })
}

function parseQcAudio(value: unknown): DeliveryQcAudio | null {
  if (
    !isRecordWithExactKeys(value, [
      'integratedLoudnessLufs',
      'truePeakDbfs',
      'evidenceDigest',
    ]) ||
    !isDigest(value.evidenceDigest)
  ) {
    return null
  }
  const integratedLoudnessLufs = parseMeasuredValue(
    value.integratedLoudnessLufs,
  )
  const truePeakDbfs = parseMeasuredValue(value.truePeakDbfs)
  if (integratedLoudnessLufs === null || truePeakDbfs === null) {
    return null
  }
  return Object.freeze({
    integratedLoudnessLufs,
    truePeakDbfs,
    evidenceDigest: value.evidenceDigest,
  })
}

function parseMeasuredValue(value: unknown): DeliveryQcMeasuredValue | null {
  if (!isRecordWithExactKeys(value, ['kind', 'value'])) {
    return null
  }
  if (value.kind === 'finite' && isDecimalString(value.value)) {
    return Object.freeze({ kind: 'finite', value: value.value })
  }
  if (
    (value.kind === 'negativeInfinity' || value.kind === 'positiveInfinity') &&
    value.value === null
  ) {
    return Object.freeze({ kind: value.kind, value: null })
  }
  return null
}

function parseQcTools(value: unknown): readonly DeliveryQcTool[] | null {
  if (!isBoundedArray(value, 1, maximumCollectionCount)) {
    return null
  }
  const tools: DeliveryQcTool[] = []
  const identities = new Set<string>()
  for (const item of value) {
    if (
      !isRecordWithExactKeys(item, [
        'name',
        'version',
        'executableDigest',
        'executableByteSize',
      ]) ||
      !isBoundedText(item.name) ||
      !isBoundedText(item.version) ||
      !isDigest(item.executableDigest) ||
      !isPositiveInt64String(item.executableByteSize)
    ) {
      return null
    }
    const identity = `${item.name}\u0000${item.version}\u0000${item.executableDigest}`
    if (identities.has(identity)) {
      return null
    }
    identities.add(identity)
    tools.push(Object.freeze({
      name: item.name,
      version: item.version,
      executableDigest: item.executableDigest,
      executableByteSize: item.executableByteSize,
    }))
  }
  return Object.freeze(tools)
}

function parseReviewAreas(
  value: unknown,
): readonly DeliveryQcReviewArea[] | null {
  if (!isBoundedArray(value, 1, qcReviewAreaOrder.length)) {
    return null
  }
  const areas: DeliveryQcReviewArea[] = []
  let previousIndex = -1
  for (const item of value) {
    const index = qcReviewAreaOrder.indexOf(item as DeliveryQcReviewArea)
    if (index <= previousIndex) {
      return null
    }
    previousIndex = index
    areas.push(item as DeliveryQcReviewArea)
  }
  return Object.freeze(areas)
}

function qualityControlSemanticsAreValid(
  video: DeliveryQcStream,
  audio: DeliveryQcStream | null,
  subtitle: DeliveryQcSubtitleTiming,
  duration: DeliveryQcDuration,
  luma: DeliveryQcLuma,
  audioMeasurements: DeliveryQcAudio | null,
  areas: readonly DeliveryQcReviewArea[],
): boolean {
  if (
    !rationalDifferenceEquals(
      video.observedDuration,
      duration.expected,
      duration.videoDelta,
    ) ||
    luma.measuredFrameCount !== video.observedCount ||
    (audio === null) !== (audioMeasurements === null)
  ) {
    return false
  }

  if (audio === null) {
    if (
      duration.audioDelta !== null ||
      duration.audioVideoStartOffset !== null ||
      duration.audioVideoEndOffset !== null
    ) {
      return false
    }
  } else if (
    audio.streamIndex === video.streamIndex ||
    duration.audioDelta === null ||
    duration.audioVideoStartOffset === null ||
    duration.audioVideoEndOffset === null ||
    !rationalDifferenceEquals(
      audio.observedDuration,
      duration.expected,
      duration.audioDelta,
    ) ||
    !rationalDifferenceEquals(
      audio.presentationStart,
      video.presentationStart,
      duration.audioVideoStartOffset,
    ) ||
    !rationalDifferenceEquals(
      audio.presentationEnd,
      video.presentationEnd,
      duration.audioVideoEndOffset,
    )
  ) {
    return false
  }

  if (
    subtitle.streamIndex !== null &&
    (subtitle.streamIndex === video.streamIndex ||
      subtitle.streamIndex === audio?.streamIndex ||
      !areas.includes('subtitlePresentation'))
  ) {
    return false
  }

  const expectedAreas: DeliveryQcReviewArea[] = ['duration']
  if (audio !== null) {
    expectedAreas.push('audioVideoSynchronization')
  }
  if (areas.includes('subtitlePresentation')) {
    expectedAreas.push('subtitlePresentation')
  }
  expectedAreas.push('screenLuma')
  if (audio !== null) {
    expectedAreas.push('programLoudness', 'truePeak')
  }
  return arraysEqual(areas, expectedAreas)
}

function parseTrace(
  value: unknown,
  snapshot: DeliverySnapshot,
): DeliveryTrace | null {
  if (
    !isRecordWithExactKeys(value, [
      'status',
      'outputTime',
      'originKind',
      'originId',
      'sourceTime',
      'sourceSpan',
    ])
  ) {
    return null
  }
  const outputTime = parseRationalTime(value.outputTime, false)
  if (outputTime === null) {
    return null
  }

  if (value.status === 'notFound') {
    if (
      value.originKind !== null ||
      value.originId !== null ||
      value.sourceTime !== null ||
      value.sourceSpan !== null
    ) {
      return null
    }
    return Object.freeze({
      status: 'notFound',
      outputTime,
      originKind: null,
      originId: null,
      sourceTime: null,
      sourceSpan: null,
    })
  }

  if (value.status !== 'resolved' || !isEntityId(value.originId)) {
    return null
  }
  if (value.originKind === 'sourceSpan') {
    const sourceTime = parseRationalTime(value.sourceTime, true)
    const sourceSpan = parseSourceSpan(value.sourceSpan)
    if (
      sourceTime === null ||
      sourceSpan === null ||
      value.originId !== sourceSpan.sourceSpanId ||
      compareRational(sourceTime, sourceSpan.start) < 0 ||
      compareRational(sourceTime, sourceSpan.end) >= 0 ||
      !snapshot.rights.some(
        (rights) =>
          rights.assetId === sourceSpan.assetId &&
          rights.sourceDigest === sourceSpan.sourceDigest,
      )
    ) {
      return null
    }
    return Object.freeze({
      status: 'resolved',
      outputTime,
      originKind: 'sourceSpan',
      originId: value.originId,
      sourceTime,
      sourceSpan,
    })
  }

  if (
    (value.originKind !== 'asset' && value.originKind !== 'editorialRecord') ||
    value.sourceTime !== null ||
    value.sourceSpan !== null ||
    (value.originKind === 'asset' &&
      !snapshot.rights.some((rights) => rights.assetId === value.originId))
  ) {
    return null
  }
  return Object.freeze({
    status: 'resolved',
    outputTime,
    originKind: value.originKind,
    originId: value.originId,
    sourceTime: null,
    sourceSpan: null,
  })
}

function parseSourceSpan(value: unknown): DeliverySourceSpan | null {
  if (
    !isRecordWithExactKeys(value, [
      'sourceSpanId',
      'assetId',
      'streamId',
      'start',
      'end',
      'sourceDigest',
    ]) ||
    !isEntityId(value.sourceSpanId) ||
    !isEntityId(value.assetId) ||
    !isBoundedText(value.streamId) ||
    !isDigest(value.sourceDigest)
  ) {
    return null
  }
  const start = parseRationalTime(value.start, true)
  const end = parseRationalTime(value.end, true)
  if (start === null || end === null || compareRational(end, start) <= 0) {
    return null
  }
  return Object.freeze({
    sourceSpanId: value.sourceSpanId,
    assetId: value.assetId,
    streamId: value.streamId,
    start,
    end,
    sourceDigest: value.sourceDigest,
  })
}

function parseNullableRational(
  value: unknown,
): DeliveryRationalTime | null | undefined {
  if (value === null) {
    return null
  }
  return parseRationalTime(value, true) ?? undefined
}

function parseRationalTime(
  value: unknown,
  allowNegative: boolean,
): DeliveryRationalTime | null {
  if (
    !isRecordWithExactKeys(value, ['value', 'rate']) ||
    !isCanonicalInt64(value.value, allowNegative) ||
    !isCanonicalPositiveInt64(value.rate)
  ) {
    return null
  }
  const numerator = BigInt(value.value)
  const denominator = BigInt(value.rate)
  if (greatestCommonDivisor(numerator, denominator) !== 1n) {
    return null
  }
  return Object.freeze({ value: value.value, rate: value.rate })
}

function compareRational(
  left: DeliveryRationalTime,
  right: DeliveryRationalTime,
): number {
  const comparison =
    BigInt(left.value) * BigInt(right.rate) -
    BigInt(right.value) * BigInt(left.rate)
  return comparison < 0n ? -1 : comparison > 0n ? 1 : 0
}

function rationalDifferenceEquals(
  minuend: DeliveryRationalTime,
  subtrahend: DeliveryRationalTime,
  expected: DeliveryRationalTime,
): boolean {
  const minuendValue = BigInt(minuend.value)
  const minuendRate = BigInt(minuend.rate)
  const subtrahendValue = BigInt(subtrahend.value)
  const subtrahendRate = BigInt(subtrahend.rate)
  const expectedValue = BigInt(expected.value)
  const expectedRate = BigInt(expected.rate)
  return (
    (minuendValue * subtrahendRate - subtrahendValue * minuendRate) *
      expectedRate ===
    expectedValue * minuendRate * subtrahendRate
  )
}

function isCanonicalInt64(value: unknown, allowNegative: boolean): value is string {
  if (
    typeof value !== 'string' ||
    !(allowNegative ? signedInt64Pattern : nonnegativeInt64Pattern).test(value)
  ) {
    return false
  }
  const parsed = BigInt(value)
  return parsed >= (allowNegative ? minimumInt64 : 0n) && parsed <= maximumInt64
}

function isCanonicalPositiveInt64(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    positiveInt64Pattern.test(value) &&
    BigInt(value) <= maximumInt64
  )
}

function isPositiveInt64String(value: unknown): value is string {
  return isCanonicalPositiveInt64(value)
}

function greatestCommonDivisor(left: bigint, right: bigint): bigint {
  let a = left < 0n ? -left : left
  let b = right
  while (b !== 0n) {
    const remainder = a % b
    a = b
    b = remainder
  }
  return a
}

function compareDecimals(left: string, right: string): number {
  const leftValue = parseDecimal(left)
  const rightValue = parseDecimal(right)
  const scale = Math.max(leftValue.scale, rightValue.scale)
  const normalizedLeft = leftValue.value * 10n ** BigInt(scale - leftValue.scale)
  const normalizedRight = rightValue.value * 10n ** BigInt(scale - rightValue.scale)
  return normalizedLeft < normalizedRight
    ? -1
    : normalizedLeft > normalizedRight
      ? 1
      : 0
}

function parseDecimal(value: string): { readonly value: bigint; readonly scale: number } {
  const negative = value.startsWith('-')
  const unsigned = negative ? value.slice(1) : value
  const [integer, fraction = ''] = unsigned.split('.')
  const magnitude = BigInt(`${integer}${fraction}`)
  return { value: negative ? -magnitude : magnitude, scale: fraction.length }
}

function isDecimalString(value: unknown): value is string {
  return typeof value === 'string' && decimalPattern.test(value)
}

function isEntityId(value: unknown): value is string {
  return typeof value === 'string' && entityIdPattern.test(value)
}

function isDigest(value: unknown): value is string {
  return typeof value === 'string' && digestPattern.test(value)
}

function isNativeTimestamp(value: unknown): value is string {
  if (typeof value !== 'string' || !nativeTimestampPattern.test(value)) {
    return false
  }
  const parsed = new Date(value)
  return (
    Number.isFinite(parsed.valueOf()) &&
    parsed.toISOString() === `${value.slice(0, 23)}Z`
  )
}

function isBoundedText(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    value.length > 0 &&
    value === value.trim() &&
    hasWellFormedUnicode(value) &&
    utf8Length(value) <= maximumTextUtf8Bytes
  )
}

function hasWellFormedUnicode(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const codeUnit = value.charCodeAt(index)
    if (codeUnit >= 0xd800 && codeUnit <= 0xdbff) {
      const next = value.charCodeAt(index + 1)
      if (!(next >= 0xdc00 && next <= 0xdfff)) {
        return false
      }
      index++
    } else if (codeUnit >= 0xdc00 && codeUnit <= 0xdfff) {
      return false
    }
  }
  return true
}

function isNonnegativeInt32(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= 2_147_483_647
  )
}

function isPositiveInt32(value: unknown): value is number {
  return isNonnegativeInt32(value) && value > 0
}

function isBoundedArray(
  value: unknown,
  minimum: number,
  maximum: number,
): value is unknown[] {
  return Array.isArray(value) && value.length >= minimum && value.length <= maximum
}

function isRecordWithExactKeys<const TKey extends string>(
  value: unknown,
  expectedKeys: readonly TKey[],
): value is Record<TKey, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false
  }
  const keys = Object.keys(value)
  return (
    keys.length === expectedKeys.length &&
    expectedKeys.every((key) => Object.prototype.hasOwnProperty.call(value, key))
  )
}

function isWithinUtf8Limit(value: unknown, maximumBytes: number): boolean {
  try {
    const serialized = JSON.stringify(value)
    return serialized !== undefined && utf8Length(serialized) <= maximumBytes
  } catch {
    return false
  }
}

function utf8Length(value: string): number {
  return new TextEncoder().encode(value).byteLength
}

function requireEntityId(value: string, description: string): void {
  if (!entityIdPattern.test(value)) {
    throw new Error(`${description} must be a non-empty lowercase UUID.`)
  }
}

function arraysEqual<T>(left: readonly T[], right: readonly T[]): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index])
}

const maximumCollectionCount = 1024
const maximumTextUtf8Bytes = 4096
const maximumInt64 = 9_223_372_036_854_775_807n
const minimumInt64 = -9_223_372_036_854_775_808n
const zeroTime: DeliveryRationalTime = Object.freeze({ value: '0', rate: '1' })
const entityIdPattern =
  /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const digestPattern = /^[0-9a-f]{64}$/
const signedInt64Pattern = /^(?:0|-?[1-9][0-9]{0,18})$/
const nonnegativeInt64Pattern = /^(?:0|[1-9][0-9]{0,18})$/
const positiveInt64Pattern = /^[1-9][0-9]{0,18}$/
const decimalPattern = /^-?(?:0|[1-9][0-9]{0,28})(?:\.[0-9]{1,28})?$/
const nativeTimestampPattern =
  /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}\+00:00$/
const publicationScopes = new Set<DeliveryPublicationScope>([
  'internalOnly',
  'specificGroupOnly',
  'externalCandidate',
  'externalPublicationApproved',
  'publicationForbidden',
  'pendingDecision',
])
const reviewStates = new Set<DeliveryReviewState>([
  'editing',
  'editorReview',
  'performerReview',
  'contentReview',
  'brandReview',
  'changesRequested',
  'approved',
  'publicationBlocked',
  'finalRendering',
  'qualityControl',
  'exported',
  'published',
])
const reviewActorRoles = new Set<DeliveryReviewActorRole>([
  'editor',
  'performerReviewer',
  'contentApprover',
  'brandApprover',
  'rightsApprover',
  'administrator',
])
const reviewApprovalActorRoles = new Set<DeliveryApprovalActorRole>([
  'contentApprover',
  'brandApprover',
  'rightsApprover',
])
const rightsStatuses = new Set<DeliveryRightsStatus>([
  'unconfirmed',
  'approved',
  'rejected',
])
const deliveryFailureStatuses = new Set<DeliveryFailureResult['status']>([
  'forbidden',
  'unavailable',
  'incomplete',
  'mismatched',
  'qualityControlFailed',
])
const qcReviewAreaOrder: readonly DeliveryQcReviewArea[] = [
  'duration',
  'audioVideoSynchronization',
  'subtitlePresentation',
  'screenLuma',
  'programLoudness',
  'truePeak',
]
