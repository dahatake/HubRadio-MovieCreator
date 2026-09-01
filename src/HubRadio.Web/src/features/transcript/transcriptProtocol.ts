import { BRIDGE_PROTOCOL_VERSION } from '../../bridge/protocol'
import type { RationalTime } from '../player/playerProtocol'

export const TRANSCRIPT_READ_METHOD = 'transcript.read' as const
export const TRANSCRIPT_EDIT_TOKEN_METHOD = 'transcript.editToken' as const
export const TRANSCRIPT_SET_SPEAKER_METHOD = 'transcript.setSpeaker' as const
export const TRANSCRIPT_REMOVE_SPEAKER_METHOD = 'transcript.removeSpeaker' as const
export const TRANSCRIPT_ADOPT_TERM_METHOD = 'transcript.adoptTerm' as const
export const TRANSCRIPT_REJECT_TERM_METHOD = 'transcript.rejectTerm' as const
export const TRANSCRIPT_MAX_REQUEST_UTF8_BYTES = 16 * 1024
export const TRANSCRIPT_MAX_RESPONSE_UTF8_BYTES = 4 * 1024 * 1024

const entityIdPattern =
  /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const digestPattern = /^[0-9a-f]{64}$/
const signedIntegerPattern = /^(?:0|-?[1-9][0-9]{0,18})$/
const positiveIntegerPattern = /^[1-9][0-9]{0,18}$/
const timestampPattern =
  /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})\.(\d{7})\+00:00$/
const int64Minimum = -9_223_372_036_854_775_808n
const int64Maximum = 9_223_372_036_854_775_807n

export type TranscriptMethod =
  | typeof TRANSCRIPT_READ_METHOD
  | typeof TRANSCRIPT_EDIT_TOKEN_METHOD
  | typeof TRANSCRIPT_SET_SPEAKER_METHOD
  | typeof TRANSCRIPT_REMOVE_SPEAKER_METHOD
  | typeof TRANSCRIPT_ADOPT_TERM_METHOD
  | typeof TRANSCRIPT_REJECT_TERM_METHOD

export type TranscriptStatus =
  | 'available'
  | 'forbidden'
  | 'unavailable'
  | 'staleBase'
  | 'conflict'
  | 'noChange'
  | 'invalid'

export type TranscriptSpeakerDisplayFormat =
  | 'fullName'
  | 'surname'
  | 'role'
  | 'hidden'

export type TranscriptTimingReviewReason =
  | 'workerReported'
  | 'missingRawTiming'
  | 'nonPositiveRawDuration'
  | 'outsideUtterance'
  | 'outsideAnalysisAudio'
  | 'nonPositiveRoundedDuration'
  | 'overlapsPreviousTimedToken'

export type TranscriptDependencyNode =
  | 'termCandidates'
  | 'editorialPhraseCandidates'
  | 'repetitionCandidates'
  | 'topicBoundaryCandidates'
  | 'topicRangeBinding'
  | 'contextDeviationCandidates'
  | 'speakerAssignmentBinding'
  | 'topicMetadata'
  | 'emphasisCandidates'

export interface TranscriptTimeRange {
  readonly start: RationalTime
  readonly duration: RationalTime
}

export interface TranscriptPerformer {
  readonly id: string
  readonly displayName: string
  readonly role: string
  readonly titleConfirmedAt: string | null
}

export interface TranscriptSpeakerDisplay {
  readonly speakerId: string
  readonly performerId: string | null
  readonly format: TranscriptSpeakerDisplayFormat | null
  readonly isAssigned: boolean
  readonly isVisible: boolean
  readonly text: string | null
  readonly titleConfirmedAt: string | null
  readonly titleOrAffiliationConfirmationRequired: boolean
}

export interface TranscriptToken {
  readonly id: string
  readonly text: string
  readonly range: TranscriptTimeRange | null
  readonly recognitionConfidence: number | null
  readonly manualTimingAdjustmentRequired: boolean
  readonly timingReviewReasons: readonly TranscriptTimingReviewReason[]
  readonly effectiveSeekSourceTime: RationalTime
  readonly seekUsesUtteranceRange: boolean
}

export interface TranscriptSentence {
  readonly id: string
  readonly text: string
  readonly tokens: readonly TranscriptToken[]
}

export interface TranscriptUtterance {
  readonly id: string
  readonly sourceSpanId: string
  readonly text: string
  readonly range: TranscriptTimeRange
  readonly recognitionConfidence: number | null
  readonly manualTimingAdjustmentRequired: boolean
  readonly timingReviewReasons: readonly TranscriptTimingReviewReason[]
  readonly speaker: TranscriptSpeakerDisplay
  readonly sentences: readonly TranscriptSentence[]
}

export interface TranscriptTermCandidate {
  readonly candidateId: string
  readonly sourceSpanId: string
  readonly tokenId: string
  readonly termEntryId: string
  readonly termEntryVersion: number
  readonly dictionaryReading: string
  readonly observedText: string
  readonly proposedText: string
  readonly matchKind:
    | 'writtenFormCompatibilityVariant'
    | 'writtenFormCaseVariant'
    | 'dictionaryReading'
  readonly confidence: number | null
}

export interface TranscriptTermDecision {
  readonly candidateId: string
  readonly decision: 'adopted' | 'rejected'
  readonly sourceRevisionId: string
  readonly sourceRevisionDigest: string
  readonly tokenId: string
  readonly termEntryId: string
  readonly termEntryVersion: number
  readonly decidedAt: string
  readonly reason: string
  readonly resultingRevisionId: string | null
}

export interface TranscriptTextChange {
  readonly kind: 'utterance' | 'sentence' | 'token'
  readonly utteranceId: string
  readonly sentenceId: string | null
  readonly tokenId: string | null
  readonly previousText: string
  readonly currentText: string
}

export interface TranscriptStaleRange {
  readonly sourceRevisionId: string
  readonly sourceRevisionDigest: string
  readonly revisionId: string
  readonly revisionDigest: string
}

export interface TranscriptInvalidation {
  readonly staleNodes: readonly TranscriptDependencyNode[]
  readonly recomputationNodes: readonly TranscriptDependencyNode[]
  readonly manualRebindingNodes: readonly TranscriptDependencyNode[]
}

export interface TranscriptSnapshot {
  readonly projectId: string
  readonly revisionId: string
  readonly revisionDigest: string
  readonly parentRevisionId: string | null
  readonly changeSource: 'machine' | 'human'
  readonly createdAt: string
  readonly speakerRevisionId: string | null
  readonly performers: readonly TranscriptPerformer[]
  readonly utterances: readonly TranscriptUtterance[]
  readonly termCandidates: readonly TranscriptTermCandidate[]
  readonly termDecisions: readonly TranscriptTermDecision[]
  readonly diff: readonly TranscriptTextChange[]
  readonly staleRange: TranscriptStaleRange | null
  readonly invalidation: TranscriptInvalidation
}

export interface TranscriptReadRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof TRANSCRIPT_READ_METHOD
  readonly params: Readonly<{ projectId: string }>
}

export interface TranscriptEditTokenParameters {
  readonly projectId: string
  readonly revisionId: string
  readonly digest: string
  readonly tokenId: string
  readonly expectedText: string
  readonly revisedText: string
  readonly reason: string
}

export interface TranscriptSetSpeakerParameters {
  readonly projectId: string
  readonly revisionId: string
  readonly digest: string
  readonly expectedSpeakerRevisionId: string | null
  readonly speakerId: string
  readonly performerId: string
  readonly surname: string
  readonly displayFormat: TranscriptSpeakerDisplayFormat
  readonly reason: string
}

export interface TranscriptRemoveSpeakerParameters {
  readonly projectId: string
  readonly revisionId: string
  readonly digest: string
  readonly expectedSpeakerRevisionId: string | null
  readonly speakerId: string
  readonly reason: string
}

export interface TranscriptTermDecisionParameters {
  readonly projectId: string
  readonly revisionId: string
  readonly digest: string
  readonly candidateId: string
  readonly termEntryId: string
  readonly termEntryVersion: number
  readonly reason: string
}

export interface TranscriptEditTokenRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof TRANSCRIPT_EDIT_TOKEN_METHOD
  readonly params: Readonly<TranscriptEditTokenParameters>
}

export interface TranscriptSetSpeakerRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof TRANSCRIPT_SET_SPEAKER_METHOD
  readonly params: Readonly<TranscriptSetSpeakerParameters>
}

export interface TranscriptRemoveSpeakerRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof TRANSCRIPT_REMOVE_SPEAKER_METHOD
  readonly params: Readonly<TranscriptRemoveSpeakerParameters>
}

export interface TranscriptTermDecisionRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method:
    | typeof TRANSCRIPT_ADOPT_TERM_METHOD
    | typeof TRANSCRIPT_REJECT_TERM_METHOD
  readonly params: Readonly<TranscriptTermDecisionParameters>
}

export type TranscriptRequest =
  | TranscriptReadRequest
  | TranscriptEditTokenRequest
  | TranscriptSetSpeakerRequest
  | TranscriptRemoveSpeakerRequest
  | TranscriptTermDecisionRequest

export type TranscriptResponse = Readonly<{
  version: typeof BRIDGE_PROTOCOL_VERSION
  requestId: string
  method: TranscriptMethod
  result:
    | Readonly<{ status: 'available'; snapshot: TranscriptSnapshot }>
    | Readonly<{
        status: Exclude<TranscriptStatus, 'available'>
        snapshot: null
      }>
}>

const snapshotKeys = [
  'projectId',
  'revisionId',
  'revisionDigest',
  'parentRevisionId',
  'changeSource',
  'createdAt',
  'speakerRevisionId',
  'performers',
  'utterances',
  'termCandidates',
  'termDecisions',
  'diff',
  'staleRange',
  'invalidation',
] as const
const performerKeys = ['id', 'displayName', 'role', 'titleConfirmedAt'] as const
const utteranceKeys = [
  'id',
  'sourceSpanId',
  'text',
  'range',
  'recognitionConfidence',
  'manualTimingAdjustmentRequired',
  'timingReviewReasons',
  'speaker',
  'sentences',
] as const
const speakerKeys = [
  'speakerId',
  'performerId',
  'format',
  'isAssigned',
  'isVisible',
  'text',
  'titleConfirmedAt',
  'titleOrAffiliationConfirmationRequired',
] as const
const sentenceKeys = ['id', 'text', 'tokens'] as const
const tokenKeys = [
  'id',
  'text',
  'range',
  'recognitionConfidence',
  'manualTimingAdjustmentRequired',
  'timingReviewReasons',
  'effectiveSeekSourceTime',
  'seekUsesUtteranceRange',
] as const
const candidateKeys = [
  'candidateId',
  'sourceSpanId',
  'tokenId',
  'termEntryId',
  'termEntryVersion',
  'dictionaryReading',
  'observedText',
  'proposedText',
  'matchKind',
  'confidence',
] as const
const decisionKeys = [
  'candidateId',
  'decision',
  'sourceRevisionId',
  'sourceRevisionDigest',
  'tokenId',
  'termEntryId',
  'termEntryVersion',
  'decidedAt',
  'reason',
  'resultingRevisionId',
] as const
const diffKeys = [
  'kind',
  'utteranceId',
  'sentenceId',
  'tokenId',
  'previousText',
  'currentText',
] as const
const staleRangeKeys = [
  'sourceRevisionId',
  'sourceRevisionDigest',
  'revisionId',
  'revisionDigest',
] as const
const timingReasons: readonly TranscriptTimingReviewReason[] = [
  'workerReported',
  'missingRawTiming',
  'nonPositiveRawDuration',
  'outsideUtterance',
  'outsideAnalysisAudio',
  'nonPositiveRoundedDuration',
  'overlapsPreviousTimedToken',
]
const dependencyNodes: readonly TranscriptDependencyNode[] = [
  'termCandidates',
  'editorialPhraseCandidates',
  'repetitionCandidates',
  'topicBoundaryCandidates',
  'topicRangeBinding',
  'contextDeviationCandidates',
  'speakerAssignmentBinding',
  'topicMetadata',
  'emphasisCandidates',
]
const recomputationNodes: readonly TranscriptDependencyNode[] = [
  'termCandidates',
  'editorialPhraseCandidates',
  'repetitionCandidates',
  'topicBoundaryCandidates',
  'contextDeviationCandidates',
  'topicMetadata',
  'emphasisCandidates',
]
const manualRebindingNodes: readonly TranscriptDependencyNode[] = [
  'topicRangeBinding',
  'speakerAssignmentBinding',
]
const displayFormats: readonly TranscriptSpeakerDisplayFormat[] = [
  'fullName',
  'surname',
  'role',
  'hidden',
]
const candidateMatchKinds: readonly TranscriptTermCandidate['matchKind'][] = [
  'writtenFormCompatibilityVariant',
  'writtenFormCaseVariant',
  'dictionaryReading',
]
const failureStatuses: readonly Exclude<TranscriptStatus, 'available'>[] = [
  'forbidden',
  'unavailable',
  'staleBase',
  'conflict',
  'noChange',
  'invalid',
]

export function createTranscriptReadRequest(
  requestId: string,
  projectId: string,
): TranscriptReadRequest {
  assertEntityId(requestId, 'request ID')
  assertEntityId(projectId, 'project ID')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: TRANSCRIPT_READ_METHOD,
    params: Object.freeze({ projectId }),
  })
}

export function createTranscriptEditTokenRequest(
  requestId: string,
  parameters: TranscriptEditTokenParameters,
): TranscriptEditTokenRequest {
  assertRequestAndOcc(requestId, parameters)
  assertEntityId(parameters.tokenId, 'token ID')
  assertRequiredText(parameters.expectedText, 'expected token text')
  assertRequiredText(parameters.revisedText, 'revised token text')
  assertRequiredText(parameters.reason, 'edit reason')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: TRANSCRIPT_EDIT_TOKEN_METHOD,
    params: Object.freeze({
      projectId: parameters.projectId,
      revisionId: parameters.revisionId,
      digest: parameters.digest,
      tokenId: parameters.tokenId,
      expectedText: parameters.expectedText,
      revisedText: parameters.revisedText,
      reason: parameters.reason,
    }),
  })
}

export function createTranscriptSetSpeakerRequest(
  requestId: string,
  parameters: TranscriptSetSpeakerParameters,
): TranscriptSetSpeakerRequest {
  assertRequestAndOcc(requestId, parameters)
  assertNullableEntityId(
    parameters.expectedSpeakerRevisionId,
    'expected speaker revision ID',
  )
  assertEntityId(parameters.speakerId, 'speaker ID')
  assertEntityId(parameters.performerId, 'performer ID')
  assertRequiredText(parameters.surname, 'speaker surname')
  if (!displayFormats.includes(parameters.displayFormat)) {
    throw new Error('A speaker display format must be supported.')
  }
  assertRequiredText(parameters.reason, 'speaker change reason')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: TRANSCRIPT_SET_SPEAKER_METHOD,
    params: Object.freeze({
      projectId: parameters.projectId,
      revisionId: parameters.revisionId,
      digest: parameters.digest,
      expectedSpeakerRevisionId: parameters.expectedSpeakerRevisionId,
      speakerId: parameters.speakerId,
      performerId: parameters.performerId,
      surname: parameters.surname,
      displayFormat: parameters.displayFormat,
      reason: parameters.reason,
    }),
  })
}

export function createTranscriptRemoveSpeakerRequest(
  requestId: string,
  parameters: TranscriptRemoveSpeakerParameters,
): TranscriptRemoveSpeakerRequest {
  assertRequestAndOcc(requestId, parameters)
  assertNullableEntityId(
    parameters.expectedSpeakerRevisionId,
    'expected speaker revision ID',
  )
  assertEntityId(parameters.speakerId, 'speaker ID')
  assertRequiredText(parameters.reason, 'speaker removal reason')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: TRANSCRIPT_REMOVE_SPEAKER_METHOD,
    params: Object.freeze({
      projectId: parameters.projectId,
      revisionId: parameters.revisionId,
      digest: parameters.digest,
      expectedSpeakerRevisionId: parameters.expectedSpeakerRevisionId,
      speakerId: parameters.speakerId,
      reason: parameters.reason,
    }),
  })
}

export function createTranscriptAdoptTermRequest(
  requestId: string,
  parameters: TranscriptTermDecisionParameters,
): TranscriptTermDecisionRequest {
  return createTermDecisionRequest(
    requestId,
    TRANSCRIPT_ADOPT_TERM_METHOD,
    parameters,
  )
}

export function createTranscriptRejectTermRequest(
  requestId: string,
  parameters: TranscriptTermDecisionParameters,
): TranscriptTermDecisionRequest {
  return createTermDecisionRequest(
    requestId,
    TRANSCRIPT_REJECT_TERM_METHOD,
    parameters,
  )
}

export function parseTranscriptResponse(value: unknown): TranscriptResponse | null {
  if (
    !isWithinUtf8Limit(value, TRANSCRIPT_MAX_RESPONSE_UTF8_BYTES) ||
    !hasExactKeys(value, ['version', 'requestId', 'method', 'result']) ||
    value.version !== BRIDGE_PROTOCOL_VERSION ||
    !isEntityId(value.requestId) ||
    !isTranscriptMethod(value.method) ||
    !hasExactKeys(value.result, ['status', 'snapshot'])
  ) {
    return null
  }

  const method = value.method
  const result = value.result
  if (result.status === 'available') {
    const snapshot = parseSnapshot(result.snapshot)
    return snapshot === null
      ? null
      : Object.freeze({
          version: BRIDGE_PROTOCOL_VERSION,
          requestId: value.requestId,
          method,
          result: Object.freeze({ status: 'available', snapshot }),
        })
  }

  if (result.snapshot !== null || !isOneOf(result.status, failureStatuses)) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method,
    result: Object.freeze({ status: result.status, snapshot: null }),
  })
}

function createTermDecisionRequest(
  requestId: string,
  method:
    | typeof TRANSCRIPT_ADOPT_TERM_METHOD
    | typeof TRANSCRIPT_REJECT_TERM_METHOD,
  parameters: TranscriptTermDecisionParameters,
): TranscriptTermDecisionRequest {
  assertRequestAndOcc(requestId, parameters)
  assertEntityId(parameters.candidateId, 'term candidate ID')
  assertEntityId(parameters.termEntryId, 'term entry ID')
  if (!isNonnegativeInt32(parameters.termEntryVersion)) {
    throw new Error('A term entry version must be a nonnegative Int32.')
  }
  assertRequiredText(parameters.reason, 'term decision reason')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method,
    params: Object.freeze({
      projectId: parameters.projectId,
      revisionId: parameters.revisionId,
      digest: parameters.digest,
      candidateId: parameters.candidateId,
      termEntryId: parameters.termEntryId,
      termEntryVersion: parameters.termEntryVersion,
      reason: parameters.reason,
    }),
  })
}

function assertRequestAndOcc(
  requestId: string,
  parameters: Readonly<{
    projectId: string
    revisionId: string
    digest: string
  }>,
): void {
  assertEntityId(requestId, 'request ID')
  assertEntityId(parameters.projectId, 'project ID')
  assertEntityId(parameters.revisionId, 'revision ID')
  if (!digestPattern.test(parameters.digest)) {
    throw new Error('A transcript revision digest must be canonical SHA-256.')
  }
}

function freezeBoundedRequest<T extends TranscriptRequest>(request: T): T {
  if (!isWithinUtf8Limit(request, TRANSCRIPT_MAX_REQUEST_UTF8_BYTES)) {
    throw new Error('The transcript bridge request exceeds the protocol byte limit.')
  }
  return Object.freeze(request)
}

function parseSnapshot(value: unknown): TranscriptSnapshot | null {
  if (
    !hasExactKeys(value, snapshotKeys) ||
    !isEntityId(value.projectId) ||
    !isEntityId(value.revisionId) ||
    !isDigest(value.revisionDigest) ||
    !isNullableEntityId(value.parentRevisionId) ||
    (value.changeSource !== 'machine' && value.changeSource !== 'human') ||
    !isCanonicalTimestamp(value.createdAt) ||
    !isNullableEntityId(value.speakerRevisionId) ||
    !Array.isArray(value.performers) ||
    !Array.isArray(value.utterances) ||
    !Array.isArray(value.termCandidates) ||
    !Array.isArray(value.termDecisions) ||
    !Array.isArray(value.diff)
  ) {
    return null
  }

  const performers = value.performers.map(parsePerformer)
  if (performers.some(isNull) || !hasUniqueIds(performers, 'id')) {
    return null
  }
  const frozenPerformers = performers as TranscriptPerformer[]
  const performersById = new Map(
    frozenPerformers.map((performer) => [performer.id, performer] as const),
  )

  const hierarchy = parseUtterances(value.utterances, performersById)
  if (hierarchy === null) {
    return null
  }

  const candidates = value.termCandidates.map((candidate) =>
    parseCandidate(candidate, hierarchy.tokensById),
  )
  if (candidates.some(isNull) || !hasUniqueIds(candidates, 'candidateId')) {
    return null
  }
  const frozenCandidates = candidates as TranscriptTermCandidate[]

  const decisions = value.termDecisions.map((decision) =>
    parseDecision(decision, hierarchy.tokensById),
  )
  if (
    decisions.some(isNull) ||
    !hasUniqueIds(decisions, 'candidateId') ||
    frozenCandidates.some((candidate) =>
      (decisions as TranscriptTermDecision[]).some(
        (decision) => decision.candidateId === candidate.candidateId,
      ),
    )
  ) {
    return null
  }

  const diff = value.diff.map((change) =>
    parseDiff(change, hierarchy.utterancesById, hierarchy.sentencesById, hierarchy.tokensById),
  )
  if (diff.some(isNull) || !hasUniqueDiffTargets(diff as TranscriptTextChange[])) {
    return null
  }

  const staleRange =
    value.staleRange === null ? null : parseStaleRange(value.staleRange)
  if (
    (value.staleRange !== null && staleRange === null) ||
    (diff.length === 0) !== (staleRange === null) ||
    (staleRange !== null &&
      (value.parentRevisionId === null ||
        staleRange.sourceRevisionId !== value.parentRevisionId ||
        staleRange.revisionId !== value.revisionId ||
        staleRange.revisionDigest !== value.revisionDigest))
  ) {
    return null
  }

  const invalidation = parseInvalidation(
    value.invalidation,
    diff.length !== 0,
  )
  if (invalidation === null) {
    return null
  }

  return Object.freeze({
    projectId: value.projectId,
    revisionId: value.revisionId,
    revisionDigest: value.revisionDigest,
    parentRevisionId: value.parentRevisionId,
    changeSource: value.changeSource,
    createdAt: value.createdAt,
    speakerRevisionId: value.speakerRevisionId,
    performers: Object.freeze(frozenPerformers),
    utterances: hierarchy.utterances,
    termCandidates: Object.freeze(frozenCandidates),
    termDecisions: Object.freeze(decisions as TranscriptTermDecision[]),
    diff: Object.freeze(diff as TranscriptTextChange[]),
    staleRange,
    invalidation,
  })
}

function parsePerformer(value: unknown): TranscriptPerformer | null {
  if (
    !hasExactKeys(value, performerKeys) ||
    !isEntityId(value.id) ||
    !isRequiredText(value.displayName) ||
    !isRequiredText(value.role) ||
    !isNullableTimestamp(value.titleConfirmedAt)
  ) {
    return null
  }
  return Object.freeze({
    id: value.id,
    displayName: value.displayName,
    role: value.role,
    titleConfirmedAt: value.titleConfirmedAt,
  })
}

function parseUtterances(
  values: readonly unknown[],
  performersById: ReadonlyMap<string, TranscriptPerformer>,
): Readonly<{
  utterances: readonly TranscriptUtterance[]
  utterancesById: ReadonlyMap<string, TranscriptUtterance>
  sentencesById: ReadonlyMap<string, TranscriptSentence>
  tokensById: ReadonlyMap<string, TranscriptTokenBinding>
}> | null {
  const utterances: TranscriptUtterance[] = []
  const utterancesById = new Map<string, TranscriptUtterance>()
  const sentencesById = new Map<string, TranscriptSentence>()
  const tokensById = new Map<string, TranscriptTokenBinding>()
  const sourceSpanIds = new Set<string>()
  const speakersById = new Map<string, TranscriptSpeakerDisplay>()
  let previousUtteranceStart: RationalTime | null = null

  for (const value of values) {
    if (
      !hasExactKeys(value, utteranceKeys) ||
      !isEntityId(value.id) ||
      utterancesById.has(value.id) ||
      !isEntityId(value.sourceSpanId) ||
      sourceSpanIds.has(value.sourceSpanId) ||
      !isRequiredText(value.text) ||
      !Array.isArray(value.sentences) ||
      !isConfidence(value.recognitionConfidence) ||
      typeof value.manualTimingAdjustmentRequired !== 'boolean'
    ) {
      return null
    }
    const range = parseTimeRange(value.range, true)
    const reasons = parseCanonicalStringArray(value.timingReviewReasons, timingReasons)
    const speaker = parseSpeaker(value.speaker, performersById)
    if (
      range === null ||
      reasons === null ||
      speaker === null ||
      value.manualTimingAdjustmentRequired !== (reasons.length !== 0) ||
      (previousUtteranceStart !== null &&
        compareRationals(range.start, previousUtteranceStart) < 0)
    ) {
      return null
    }
    previousUtteranceStart = range.start

    const previousSpeaker = speakersById.get(speaker.speakerId)
    if (previousSpeaker !== undefined && !speakerDisplaysEqual(previousSpeaker, speaker)) {
      return null
    }
    speakersById.set(speaker.speakerId, speaker)

    const sentences: TranscriptSentence[] = []
    let previousTimedEnd: RationalTime | null = null
    for (const sentenceValue of value.sentences) {
      if (
        !hasExactKeys(sentenceValue, sentenceKeys) ||
        !isEntityId(sentenceValue.id) ||
        sentencesById.has(sentenceValue.id) ||
        !isRequiredText(sentenceValue.text) ||
        !Array.isArray(sentenceValue.tokens) ||
        sentenceValue.tokens.length === 0
      ) {
        return null
      }
      const tokens: TranscriptToken[] = []
      for (const tokenValue of sentenceValue.tokens) {
        const token = parseToken(tokenValue, range)
        if (token === null || tokensById.has(token.id)) {
          return null
        }
        if (token.range !== null) {
          const tokenEnd = addRationals(token.range.start, token.range.duration)
          if (
            tokenEnd === null ||
            (previousTimedEnd !== null &&
              compareRationals(token.range.start, previousTimedEnd) < 0)
          ) {
            return null
          }
          previousTimedEnd = tokenEnd
        }
        tokens.push(token)
      }
      const sentence = Object.freeze({
        id: sentenceValue.id,
        text: sentenceValue.text,
        tokens: Object.freeze(tokens),
      })
      sentences.push(sentence)
      sentencesById.set(sentence.id, sentence)
      for (const token of tokens) {
        tokensById.set(token.id, Object.freeze({
          token,
          sentence,
          utteranceId: value.id,
          sourceSpanId: value.sourceSpanId,
        }))
      }
    }
    if (sentences.length === 0) {
      return null
    }

    const utterance = Object.freeze({
      id: value.id,
      sourceSpanId: value.sourceSpanId,
      text: value.text,
      range,
      recognitionConfidence: value.recognitionConfidence,
      manualTimingAdjustmentRequired: value.manualTimingAdjustmentRequired,
      timingReviewReasons: reasons,
      speaker,
      sentences: Object.freeze(sentences),
    })
    utterances.push(utterance)
    utterancesById.set(utterance.id, utterance)
  }

  return Object.freeze({
    utterances: Object.freeze(utterances),
    utterancesById,
    sentencesById,
    tokensById,
  })
}

function parseSpeaker(
  value: unknown,
  performersById: ReadonlyMap<string, TranscriptPerformer>,
): TranscriptSpeakerDisplay | null {
  if (
    !hasExactKeys(value, speakerKeys) ||
    !isEntityId(value.speakerId) ||
    !isNullableEntityId(value.performerId) ||
    (value.format !== null && !isOneOf(value.format, displayFormats)) ||
    typeof value.isAssigned !== 'boolean' ||
    typeof value.isVisible !== 'boolean' ||
    (value.text !== null && !isRequiredText(value.text)) ||
    !isNullableTimestamp(value.titleConfirmedAt) ||
    typeof value.titleOrAffiliationConfirmationRequired !== 'boolean'
  ) {
    return null
  }

  if (value.performerId === null) {
    if (
      value.format !== null ||
      value.isAssigned ||
      !value.isVisible ||
      value.text === null ||
      value.titleConfirmedAt !== null ||
      value.titleOrAffiliationConfirmationRequired
    ) {
      return null
    }
  } else {
    const performer = performersById.get(value.performerId)
    if (
      performer === undefined ||
      value.format === null ||
      !value.isAssigned ||
      value.titleConfirmedAt !== performer.titleConfirmedAt ||
      value.titleOrAffiliationConfirmationRequired !==
        (value.titleConfirmedAt === null) ||
      (value.format === 'hidden'
        ? value.isVisible || value.text !== null
        : !value.isVisible || value.text === null) ||
      (value.format === 'fullName' && value.text !== performer.displayName) ||
      (value.format === 'role' && value.text !== performer.role)
    ) {
      return null
    }
  }

  return Object.freeze({
    speakerId: value.speakerId,
    performerId: value.performerId,
    format: value.format,
    isAssigned: value.isAssigned,
    isVisible: value.isVisible,
    text: value.text,
    titleConfirmedAt: value.titleConfirmedAt,
    titleOrAffiliationConfirmationRequired:
      value.titleOrAffiliationConfirmationRequired,
  })
}

function parseToken(
  value: unknown,
  utteranceRange: TranscriptTimeRange,
): TranscriptToken | null {
  if (
    !hasExactKeys(value, tokenKeys) ||
    !isEntityId(value.id) ||
    !isRequiredText(value.text) ||
    !isConfidence(value.recognitionConfidence) ||
    typeof value.manualTimingAdjustmentRequired !== 'boolean' ||
    typeof value.seekUsesUtteranceRange !== 'boolean'
  ) {
    return null
  }
  const range = value.range === null ? null : parseTimeRange(value.range, true)
  const reasons = parseCanonicalStringArray(value.timingReviewReasons, timingReasons)
  const effectiveSeekSourceTime = parseRational(value.effectiveSeekSourceTime)
  if (
    (value.range !== null && range === null) ||
    reasons === null ||
    effectiveSeekSourceTime === null ||
    value.seekUsesUtteranceRange !== (range === null) ||
    value.manualTimingAdjustmentRequired !==
      (range === null || reasons.length !== 0)
  ) {
    return null
  }
  const expectedSeek = range?.start ?? utteranceRange.start
  if (
    !rationalsEqual(effectiveSeekSourceTime, expectedSeek) ||
    (range !== null && !rangeContainsRange(utteranceRange, range))
  ) {
    return null
  }
  return Object.freeze({
    id: value.id,
    text: value.text,
    range,
    recognitionConfidence: value.recognitionConfidence,
    manualTimingAdjustmentRequired: value.manualTimingAdjustmentRequired,
    timingReviewReasons: reasons,
    effectiveSeekSourceTime,
    seekUsesUtteranceRange: value.seekUsesUtteranceRange,
  })
}

function parseCandidate(
  value: unknown,
  tokensById: ReadonlyMap<string, TranscriptTokenBinding>,
): TranscriptTermCandidate | null {
  if (
    !hasExactKeys(value, candidateKeys) ||
    !isEntityId(value.candidateId) ||
    !isEntityId(value.sourceSpanId) ||
    !isEntityId(value.tokenId) ||
    !isEntityId(value.termEntryId) ||
    !isNonnegativeInt32(value.termEntryVersion) ||
    !isRequiredText(value.dictionaryReading) ||
    !isRequiredText(value.observedText) ||
    !isRequiredText(value.proposedText) ||
    value.observedText === value.proposedText ||
    !isOneOf(value.matchKind, candidateMatchKinds) ||
    !isConfidence(value.confidence)
  ) {
    return null
  }
  const binding = tokensById.get(value.tokenId)
  if (
    binding === undefined ||
    binding.sourceSpanId !== value.sourceSpanId ||
    binding.token.text !== value.observedText
  ) {
    return null
  }
  return Object.freeze({
    candidateId: value.candidateId,
    sourceSpanId: value.sourceSpanId,
    tokenId: value.tokenId,
    termEntryId: value.termEntryId,
    termEntryVersion: value.termEntryVersion,
    dictionaryReading: value.dictionaryReading,
    observedText: value.observedText,
    proposedText: value.proposedText,
    matchKind: value.matchKind,
    confidence: value.confidence,
  })
}

function parseDecision(
  value: unknown,
  tokensById: ReadonlyMap<string, TranscriptTokenBinding>,
): TranscriptTermDecision | null {
  if (
    !hasExactKeys(value, decisionKeys) ||
    !isEntityId(value.candidateId) ||
    (value.decision !== 'adopted' && value.decision !== 'rejected') ||
    !isEntityId(value.sourceRevisionId) ||
    !isDigest(value.sourceRevisionDigest) ||
    !isEntityId(value.tokenId) ||
    !tokensById.has(value.tokenId) ||
    !isEntityId(value.termEntryId) ||
    !isNonnegativeInt32(value.termEntryVersion) ||
    !isCanonicalTimestamp(value.decidedAt) ||
    !isRequiredText(value.reason) ||
    !isNullableEntityId(value.resultingRevisionId) ||
    (value.decision === 'adopted') !== (value.resultingRevisionId !== null)
  ) {
    return null
  }
  return Object.freeze({
    candidateId: value.candidateId,
    decision: value.decision,
    sourceRevisionId: value.sourceRevisionId,
    sourceRevisionDigest: value.sourceRevisionDigest,
    tokenId: value.tokenId,
    termEntryId: value.termEntryId,
    termEntryVersion: value.termEntryVersion,
    decidedAt: value.decidedAt,
    reason: value.reason,
    resultingRevisionId: value.resultingRevisionId,
  })
}

function parseDiff(
  value: unknown,
  utterancesById: ReadonlyMap<string, TranscriptUtterance>,
  sentencesById: ReadonlyMap<string, TranscriptSentence>,
  tokensById: ReadonlyMap<string, TranscriptTokenBinding>,
): TranscriptTextChange | null {
  if (
    !hasExactKeys(value, diffKeys) ||
    (value.kind !== 'utterance' &&
      value.kind !== 'sentence' &&
      value.kind !== 'token') ||
    !isEntityId(value.utteranceId) ||
    !isNullableEntityId(value.sentenceId) ||
    !isNullableEntityId(value.tokenId) ||
    !isRequiredText(value.previousText) ||
    !isRequiredText(value.currentText) ||
    value.previousText === value.currentText
  ) {
    return null
  }

  let currentTargetText: string | null = null
  if (value.kind === 'utterance') {
    if (value.sentenceId !== null || value.tokenId !== null) return null
    currentTargetText = utterancesById.get(value.utteranceId)?.text ?? null
  } else if (value.kind === 'sentence') {
    if (value.sentenceId === null || value.tokenId !== null) return null
    const sentence = sentencesById.get(value.sentenceId)
    const firstToken = sentence?.tokens[0]
    const binding = firstToken === undefined ? undefined : tokensById.get(firstToken.id)
    if (binding?.utteranceId !== value.utteranceId) return null
    currentTargetText = sentence?.text ?? null
  } else {
    if (value.sentenceId === null || value.tokenId === null) return null
    const binding = tokensById.get(value.tokenId)
    if (
      binding === undefined ||
      binding.utteranceId !== value.utteranceId ||
      binding.sentence.id !== value.sentenceId
    ) {
      return null
    }
    currentTargetText = binding.token.text
  }
  if (currentTargetText !== value.currentText) {
    return null
  }
  return Object.freeze({
    kind: value.kind,
    utteranceId: value.utteranceId,
    sentenceId: value.sentenceId,
    tokenId: value.tokenId,
    previousText: value.previousText,
    currentText: value.currentText,
  })
}

function parseStaleRange(value: unknown): TranscriptStaleRange | null {
  if (
    !hasExactKeys(value, staleRangeKeys) ||
    !isEntityId(value.sourceRevisionId) ||
    !isDigest(value.sourceRevisionDigest) ||
    !isEntityId(value.revisionId) ||
    !isDigest(value.revisionDigest) ||
    value.sourceRevisionId === value.revisionId ||
    value.sourceRevisionDigest === value.revisionDigest
  ) {
    return null
  }
  return Object.freeze({
    sourceRevisionId: value.sourceRevisionId,
    sourceRevisionDigest: value.sourceRevisionDigest,
    revisionId: value.revisionId,
    revisionDigest: value.revisionDigest,
  })
}

function parseInvalidation(
  value: unknown,
  hasDiff: boolean,
): TranscriptInvalidation | null {
  if (
    !hasExactKeys(value, [
      'staleNodes',
      'recomputationNodes',
      'manualRebindingNodes',
    ])
  ) {
    return null
  }
  const stale = parseCanonicalStringArray(value.staleNodes, dependencyNodes)
  const recomputation = parseCanonicalStringArray(
    value.recomputationNodes,
    dependencyNodes,
  )
  const manual = parseCanonicalStringArray(
    value.manualRebindingNodes,
    dependencyNodes,
  )
  if (
    stale === null ||
    recomputation === null ||
    manual === null ||
    (hasDiff
      ? !sameStrings(stale, dependencyNodes) ||
        !sameStrings(recomputation, recomputationNodes) ||
        !sameStrings(manual, manualRebindingNodes)
      : stale.length !== 0 || recomputation.length !== 0 || manual.length !== 0)
  ) {
    return null
  }
  return Object.freeze({
    staleNodes: stale,
    recomputationNodes: recomputation,
    manualRebindingNodes: manual,
  })
}

function parseTimeRange(
  value: unknown,
  requirePositiveDuration: boolean,
): TranscriptTimeRange | null {
  if (!hasExactKeys(value, ['start', 'duration'])) {
    return null
  }
  const start = parseRational(value.start)
  const duration = parseRational(value.duration)
  if (
    start === null ||
    duration === null ||
    compareRationals(duration, zeroRational) < (requirePositiveDuration ? 1 : 0) ||
    addRationals(start, duration) === null
  ) {
    return null
  }
  return Object.freeze({ start, duration })
}

function parseRational(value: unknown): RationalTime | null {
  if (
    !hasExactKeys(value, ['value', 'rate']) ||
    typeof value.value !== 'string' ||
    typeof value.rate !== 'string' ||
    !signedIntegerPattern.test(value.value) ||
    !positiveIntegerPattern.test(value.rate)
  ) {
    return null
  }
  const numerator = BigInt(value.value)
  const denominator = BigInt(value.rate)
  if (
    numerator < int64Minimum ||
    numerator > int64Maximum ||
    denominator > int64Maximum ||
    greatestCommonDivisor(abs(numerator), denominator) !== 1n
  ) {
    return null
  }
  return Object.freeze({ value: value.value, rate: value.rate })
}

function addRationals(left: RationalTime, right: RationalTime): RationalTime | null {
  const numerator =
    BigInt(left.value) * BigInt(right.rate) +
    BigInt(right.value) * BigInt(left.rate)
  const denominator = BigInt(left.rate) * BigInt(right.rate)
  const divisor = greatestCommonDivisor(abs(numerator), denominator)
  const reducedNumerator = numerator / divisor
  const reducedDenominator = denominator / divisor
  if (
    reducedNumerator < int64Minimum ||
    reducedNumerator > int64Maximum ||
    reducedDenominator > int64Maximum
  ) {
    return null
  }
  return Object.freeze({
    value: reducedNumerator.toString(),
    rate: reducedDenominator.toString(),
  })
}

function rangeContainsRange(
  container: TranscriptTimeRange,
  candidate: TranscriptTimeRange,
): boolean {
  const containerEnd = addRationals(container.start, container.duration)
  const candidateEnd = addRationals(candidate.start, candidate.duration)
  return (
    containerEnd !== null &&
    candidateEnd !== null &&
    compareRationals(candidate.start, container.start) >= 0 &&
    compareRationals(candidateEnd, containerEnd) <= 0
  )
}

function parseCanonicalStringArray<T extends string>(
  value: unknown,
  canonicalOrder: readonly T[],
): readonly T[] | null {
  if (!Array.isArray(value) || value.length > canonicalOrder.length) {
    return null
  }
  let previousIndex = -1
  const parsed: T[] = []
  for (const item of value) {
    const index = canonicalOrder.indexOf(item as T)
    if (typeof item !== 'string' || index <= previousIndex) {
      return null
    }
    previousIndex = index
    parsed.push(item as T)
  }
  return Object.freeze(parsed)
}

function hasUniqueIds<
  TKey extends string,
  TValue extends Record<TKey, string> | null,
>(values: readonly TValue[], key: TKey): boolean {
  const seen = new Set<string>()
  for (const value of values) {
    if (value === null || !seen.add(value[key])) return false
  }
  return true
}

function hasUniqueDiffTargets(values: readonly TranscriptTextChange[]): boolean {
  const targets = new Set<string>()
  for (const value of values) {
    const target = `${value.kind}\u0000${value.utteranceId}\u0000${value.sentenceId ?? ''}\u0000${value.tokenId ?? ''}`
    if (!targets.add(target)) return false
  }
  return true
}

function speakerDisplaysEqual(
  left: TranscriptSpeakerDisplay,
  right: TranscriptSpeakerDisplay,
): boolean {
  return (
    left.speakerId === right.speakerId &&
    left.performerId === right.performerId &&
    left.format === right.format &&
    left.isAssigned === right.isAssigned &&
    left.isVisible === right.isVisible &&
    left.text === right.text &&
    left.titleConfirmedAt === right.titleConfirmedAt &&
    left.titleOrAffiliationConfirmationRequired ===
      right.titleOrAffiliationConfirmationRequired
  )
}

function isCanonicalTimestamp(value: unknown): value is string {
  if (typeof value !== 'string') return false
  const match = timestampPattern.exec(value)
  if (match === null || match[1] === '0000') return false
  const isoMilliseconds = `${value.slice(0, 19)}.${match[7]!.slice(0, 3)}Z`
  const timestamp = new Date(isoMilliseconds)
  return Number.isFinite(timestamp.valueOf()) && timestamp.toISOString() === isoMilliseconds
}

function isNullableTimestamp(value: unknown): value is string | null {
  return value === null || isCanonicalTimestamp(value)
}

function isRequiredText(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    value.length !== 0 &&
    value === value.trim() &&
    /\S/u.test(value) &&
    !containsUnpairedSurrogate(value)
  )
}

function containsUnpairedSurrogate(value: string): boolean {
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index)
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(index + 1)
      if (!(next >= 0xdc00 && next <= 0xdfff)) return true
      index += 1
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return true
    }
  }
  return false
}

function isConfidence(value: unknown): value is number | null {
  return (
    value === null ||
    (typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 1)
  )
}

function isTranscriptMethod(value: unknown): value is TranscriptMethod {
  return (
    value === TRANSCRIPT_READ_METHOD ||
    value === TRANSCRIPT_EDIT_TOKEN_METHOD ||
    value === TRANSCRIPT_SET_SPEAKER_METHOD ||
    value === TRANSCRIPT_REMOVE_SPEAKER_METHOD ||
    value === TRANSCRIPT_ADOPT_TERM_METHOD ||
    value === TRANSCRIPT_REJECT_TERM_METHOD
  )
}

function isEntityId(value: unknown): value is string {
  return typeof value === 'string' && entityIdPattern.test(value)
}

function isNullableEntityId(value: unknown): value is string | null {
  return value === null || isEntityId(value)
}

function isDigest(value: unknown): value is string {
  return typeof value === 'string' && digestPattern.test(value)
}

function assertEntityId(value: string, description: string): void {
  if (!entityIdPattern.test(value)) {
    throw new Error(`A transcript ${description} must be a non-empty lowercase UUID.`)
  }
}

function assertNullableEntityId(
  value: string | null,
  description: string,
): void {
  if (value !== null) assertEntityId(value, description)
}

function assertRequiredText(value: string, description: string): void {
  if (!isRequiredText(value)) {
    throw new Error(`The transcript ${description} must be nonblank valid text without surrounding whitespace.`)
  }
}

function isNonnegativeInt32(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= 2_147_483_647
  )
}

function compareRationals(left: RationalTime, right: RationalTime): number {
  const difference =
    BigInt(left.value) * BigInt(right.rate) -
    BigInt(right.value) * BigInt(left.rate)
  return difference < 0n ? -1 : difference > 0n ? 1 : 0
}

function rationalsEqual(left: RationalTime, right: RationalTime): boolean {
  return left.value === right.value && left.rate === right.rate
}

function greatestCommonDivisor(left: bigint, right: bigint): bigint {
  let current = left
  let remainder = right
  while (remainder !== 0n) {
    const next = current % remainder
    current = remainder
    remainder = next
  }
  return current
}

function abs(value: bigint): bigint {
  return value < 0n ? -value : value
}

function sameStrings(left: readonly string[], right: readonly string[]): boolean {
  return (
    left.length === right.length &&
    left.every((value, index) => value === right[index])
  )
}

function isOneOf<T extends string>(value: unknown, allowed: readonly T[]): value is T {
  return typeof value === 'string' && allowed.includes(value as T)
}

function isNull<T>(value: T | null): value is null {
  return value === null
}

function isWithinUtf8Limit(value: unknown, maximumBytes: number): boolean {
  try {
    const serialized = JSON.stringify(value)
    return (
      serialized !== undefined &&
      new TextEncoder().encode(serialized).byteLength <= maximumBytes
    )
  } catch {
    return false
  }
}

function hasExactKeys<const TKey extends string>(
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

interface TranscriptTokenBinding {
  readonly token: TranscriptToken
  readonly sentence: TranscriptSentence
  readonly utteranceId: string
  readonly sourceSpanId: string
}

const zeroRational: RationalTime = Object.freeze({ value: '0', rate: '1' })
