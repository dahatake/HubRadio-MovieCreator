import { BRIDGE_PROTOCOL_VERSION } from '../../bridge/protocol'
import type { RationalTime } from '../player/playerProtocol'

export const ANALYSIS_READ_METHOD = 'analysis.read' as const
export const ANALYSIS_REJECT_METHOD = 'analysis.reject' as const
export const ANALYSIS_ADOPT_METHOD = 'analysis.adopt' as const
export const ANALYSIS_UPDATE_TOPIC_METHOD = 'analysis.updateTopic' as const
export const ANALYSIS_MAX_REQUEST_UTF8_BYTES = 64 * 1024
export const ANALYSIS_MAX_RESPONSE_UTF8_BYTES = 4 * 1024 * 1024

const entityIdPattern =
  /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const digestPattern = /^[0-9a-f]{64}$/
const signedIntegerPattern = /^(?:0|-?[1-9][0-9]{0,18})$/
const nonnegativeIntegerPattern = /^(?:0|[1-9][0-9]{0,18})$/
const positiveIntegerPattern = /^[1-9][0-9]{0,18}$/
const timestampPattern =
  /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})\.(\d{7})\+00:00$/
const int64Minimum = -9_223_372_036_854_775_808n
const int64Maximum = 9_223_372_036_854_775_807n

export type AnalysisMethod =
  | typeof ANALYSIS_READ_METHOD
  | typeof ANALYSIS_REJECT_METHOD
  | typeof ANALYSIS_ADOPT_METHOD
  | typeof ANALYSIS_UPDATE_TOPIC_METHOD

export type AnalysisStatus =
  | 'available'
  | 'forbidden'
  | 'unavailable'
  | 'staleBase'
  | 'conflict'
  | 'noChange'
  | 'invalid'

export type AnalysisProducer =
  | 'termCorrection'
  | 'silence'
  | 'editorialPhrase'
  | 'repetition'
  | 'topicBoundary'
  | 'audioReview'
  | 'contextDeviation'
  | 'emphasis'

export type AnalysisSuggestionKind =
  | 'deleteRange'
  | 'topicBoundary'
  | 'highlight'
  | 'termCorrection'

export type AnalysisDisplayKind =
  | 'termCorrection'
  | 'deleteCandidate'
  | 'topicBoundary'
  | 'highlight'
  | 'technicalWarning'

export interface AnalysisTimeRange {
  readonly start: RationalTime
  readonly duration: RationalTime
}

export interface AnalysisSource {
  readonly sourceSpanId: string
  readonly range: AnalysisTimeRange
}

export interface AnalysisSpeaker {
  readonly speakerId: string
  readonly performerId: string | null
  readonly format: 'fullName' | 'surname' | 'role' | 'hidden' | null
  readonly isAssigned: boolean
  readonly isVisible: boolean
  readonly text: string | null
  readonly titleConfirmedAt: string | null
  readonly titleOrAffiliationConfirmationRequired: boolean
  readonly source: AnalysisSource
}

export interface AnalysisTopicMainSpeaker {
  readonly speakerId: string
  readonly performerId: string | null
  readonly displayVisible: boolean
  readonly displayText: string | null
  readonly titleOrAffiliationConfirmationRequired: boolean
  readonly overlapDuration: RationalTime
}

export type AnalysisMetadataOrigin =
  | 'unset'
  | 'deterministicPlaceholder'
  | 'projectSnapshot'
  | 'deterministicTranscriptOverlap'
  | 'manualEditorial'

export type AnalysisMetadataField =
  | 'initial'
  | 'title'
  | 'summary'
  | 'mainSpeaker'
  | 'keywords'
  | 'importantStatements'
  | 'audience'
  | 'publicationCautions'

export interface AnalysisTopic {
  readonly topicId: string
  readonly topicRangeRevisionId: string
  readonly metadataId: string
  readonly metadataRevisionId: string
  readonly parentMetadataRevisionId: string | null
  readonly range: AnalysisTimeRange
  readonly startBoundaryCandidateId: string | null
  readonly endBoundaryCandidateId: string | null
  readonly title: string
  readonly titleOrigin: AnalysisMetadataOrigin
  readonly titleRequiresManualReview: boolean
  readonly summary: string | null
  readonly summaryOrigin: AnalysisMetadataOrigin
  readonly mainSpeaker: AnalysisTopicMainSpeaker | null
  readonly mainSpeakerOrigin: AnalysisMetadataOrigin
  readonly mainSpeakerTieUnresolved: boolean
  readonly keywords: readonly string[]
  readonly keywordsOrigin: AnalysisMetadataOrigin
  readonly importantStatementSourceSpanIds: readonly string[]
  readonly importantStatementsOrigin: AnalysisMetadataOrigin
  readonly audience: string
  readonly audienceOrigin: AnalysisMetadataOrigin
  readonly publicationCautions: readonly string[]
  readonly publicationCautionsOrigin: AnalysisMetadataOrigin
  readonly changedField: AnalysisMetadataField
  readonly createdAt: string
  readonly reason: string
}

export interface AnalysisDetection {
  readonly detectionId: string
  readonly kind:
    | 'silence'
    | 'repetition'
    | 'preparationConversation'
    | 'recordingProblem'
    | 'contextDeviation'
    | 'audioEvent'
    | 'editorialInstruction'
    | 'topicBoundary'
  readonly description: string
  readonly confidence: number | null
}

export interface AnalysisTermPayload {
  readonly producer: 'termCorrection'
  readonly sourceRevisionId: string
  readonly sourceRevisionDigest: string
  readonly sourceSpanId: string
  readonly tokenId: string
  readonly termScopeKind: 'project' | 'series'
  readonly termScopeId: string
  readonly termEntryId: string
  readonly termEntryVersion: number
  readonly dictionaryReading: string
  readonly observedText: string
  readonly proposedText: string
  readonly matchKind:
    | 'writtenFormCompatibilityVariant'
    | 'writtenFormCaseVariant'
    | 'dictionaryReading'
}

export interface AnalysisSilencePayload {
  readonly producer: 'silence'
  readonly detection: AnalysisDetection
  readonly thresholdVersion: string
  readonly analysisStartSample: string
  readonly analysisEndSample: string
  readonly noiseDb: number
  readonly minimumDuration: RationalTime
  readonly sampleRoundingRule: string
  readonly productionApproved: false
  readonly automaticDeletionAllowed: false
}

export interface AnalysisEditorialPayload {
  readonly producer: 'editorialPhrase'
  readonly detection: AnalysisDetection
  readonly sourceRevisionId: string
  readonly sourceRevisionDigest: string
  readonly dictionaryVersion: string
  readonly matchedPhrase: string
  readonly normalizedSentenceStart: number
  readonly normalizedMatchLength: number
  readonly usesUtteranceTimingFallback: boolean
  readonly usesPartialTokenCoverage: boolean
}

export interface AnalysisRepetitionPayload {
  readonly producer: 'repetition'
  readonly candidateKey: string
  readonly sourceRevisionId: string
  readonly sourceRevisionDigest: string
  readonly sourceSpanId: string
  readonly evidenceTokenIds: readonly string[]
  readonly ruleVersion: string
  readonly automaticDeletionAllowed: boolean
  readonly humanReviewRequired: boolean
  readonly features: Readonly<{
    rule: string
    comparisonSourceSpanId: string
    comparisonTokenIds: readonly string[]
    matchingTokenCount: number
    exactNfkcMatch: boolean
    structurallyAdjacent: boolean
    sameSpeakerRequired: boolean
    candidateIsLaterOccurrence: boolean
    retentionChoiceRequired: boolean
  }>
}

export interface AnalysisTopicBoundaryPayload {
  readonly producer: 'topicBoundary'
  readonly detection: AnalysisDetection
  readonly sourceRevisionId: string
  readonly sourceRevisionDigest: string
  readonly suggestedBoundary: RationalTime
  readonly ruleVersion: string
  readonly features: Readonly<{
    previousUtteranceId: string
    currentUtteranceId: string
    previousSourceSpanId: string
    currentSourceSpanId: string
    previousSpeakerId: string
    currentSpeakerId: string
    previousRange: AnalysisTimeRange
    currentRange: AnalysisTimeRange
    observedGap: RationalTime
    speakerChanged: boolean
    structurallyAdjacent: boolean
    semanticClassificationPerformed: boolean
    approvedPauseOrSimilarityThresholdApplied: boolean
    optionalShotBoundaryUsed: boolean
  }>
}

export interface AnalysisAudioPayload {
  readonly producer: 'audioReview'
  readonly detection: AnalysisDetection
  readonly eventKind: 'cough' | 'noise' | 'wait' | 'recordingQuality'
  readonly evidenceMethods: readonly (
    | 'manualListening'
    | 'ffmpegSilence'
    | 'waveformLowEnergy'
    | 'waveformClipping'
    | 'yamnetHeld'
    | 'sileroVadBenchmarkOnly'
  )[]
  readonly analysisStartSample: string
  readonly analysisEndSample: string
  readonly manualListeningConfirmed: boolean
  readonly technicalWarningOnly: boolean
  readonly automaticDeletionAllowed: boolean
  readonly explicitHumanDecisionRequired: boolean
}

export interface AnalysisContextPayload {
  readonly producer: 'contextDeviation'
  readonly detection: AnalysisDetection
  readonly sourceRevisionId: string
  readonly sourceRevisionDigest: string
  readonly ruleVersion: string
  readonly features: Readonly<{
    position:
      | 'leadingBeforeFirstTopic'
      | 'betweenTopicRanges'
      | 'trailingAfterLastTopic'
    utteranceId: string
    sourceSpanId: string
    speakerId: string
    utteranceRange: AnalysisTimeRange
    previousTopicId: string | null
    nextTopicId: string | null
    distanceToNearestTopicRange: RationalTime
    fullyOutsideExplicitTopicRanges: boolean
    semanticDistanceComputed: boolean
    timeThresholdApplied: boolean
    automaticDeletionAllowed: boolean
  }>
  readonly explicitHumanDecisionRequired: boolean
}

export interface AnalysisEmphasisPayload {
  readonly producer: 'emphasis'
  readonly sourceRevisionId: string
  readonly sourceRevisionDigest: string
  readonly topicMetadataRevisionId: string
  readonly ruleVersion: string
  readonly text: string
  readonly textSource: 'originalTranscript' | 'manualSummary'
  readonly isExactOriginalText: boolean
  readonly reasons: readonly (
    | 'importantStatement'
    | 'number'
    | 'productName'
    | 'publicationCaution'
    | 'keyword'
    | 'answerAfterExplicitQuestion'
    | 'topicTransitionSummary'
    | 'speechSegment'
  )[]
  readonly termEvidence: readonly Readonly<{
    termEntryId: string
    termEntryVersion: number
    writtenForm: string
  }>[]
  readonly automaticAdoptionAllowed: boolean
}

export type AnalysisCandidatePayload =
  | AnalysisTermPayload
  | AnalysisSilencePayload
  | AnalysisEditorialPayload
  | AnalysisRepetitionPayload
  | AnalysisTopicBoundaryPayload
  | AnalysisAudioPayload
  | AnalysisContextPayload
  | AnalysisEmphasisPayload

export interface AnalysisDecision {
  readonly decisionId: string
  readonly candidateId: string
  readonly producer: AnalysisProducer
  readonly suggestionKind: AnalysisSuggestionKind
  readonly decision: 'rejected' | 'modified' | 'adopted'
  readonly statusAfterDecision: 'reviewed' | 'rejected' | 'adopted'
  readonly sourceDigest: string
  readonly decidedAt: string
  readonly reason: string
  readonly replacementCandidateId: string | null
  readonly resultingTranscriptRevisionId: string | null
  readonly adoptedReference: Readonly<{
    candidateId: string
    sourceSpanId: string
  }> | null
}

export interface AnalysisCandidate {
  readonly candidateId: string
  readonly modelRunId: string
  readonly producer: AnalysisProducer
  readonly suggestionKind: AnalysisSuggestionKind
  readonly displayKind: AnalysisDisplayKind
  readonly status: 'proposed' | 'reviewed' | 'rejected' | 'adopted'
  readonly freshness: 'current' | 'stale'
  readonly canAdopt: boolean
  readonly canReject: boolean
  readonly sourceDigest: string
  readonly source: AnalysisSource
  readonly reason: string
  readonly confidence: number | null
  readonly missingContext: string | null
  readonly warning: string | null
  readonly parentCandidateId: string | null
  readonly replacementCandidateId: string | null
  readonly decision: AnalysisDecision | null
  readonly payload: AnalysisCandidatePayload
}

export interface AnalysisSensitivity {
  readonly revisionId: string
  readonly ruleSetVersion: string
  readonly isComplete: boolean
  readonly requiresExternalOutputWarning: boolean
  readonly findings: readonly Readonly<{
    utteranceId: string
    sourceSpanId: string
    kind:
      | 'emailAddress'
      | 'internalNetworkLocation'
      | 'credentialPattern'
      | 'dictionaryTerm'
      | 'customerName'
      | 'unreleasedProduct'
      | 'personalInformation'
      | 'financialInformation'
      | 'confidentialMaterial'
      | 'futurePlan'
    reasonCode: string
    sourceRange: AnalysisTimeRange
  }>[]
}

export type AnalysisStagedChange =
  | Readonly<{
      kind: 'transcriptRevision'
      candidateId: string
      sourceRevisionId: string
      resultingRevisionId: string
      persisted: false
    }>
  | Readonly<{
      kind: 'editOperation'
      candidateId: string
      operation: Readonly<{
        kind: 'delete' | 'split'
        elementId: string
        splitOffset: RationalTime | null
        rightElementId: string | null
      }>
      persisted: false
    }>
  | Readonly<{
      kind: 'cue'
      candidateId: string
      cue: Readonly<{
        cueId: string
        kind: 'graphic'
        outputRange: AnalysisTimeRange
        regionName: string | null
      }>
      persisted: false
    }>
  | Readonly<{
      kind: 'topicMetadataRevision'
      topicId: string
      revisionId: string
      parentRevisionId: string
      field: Exclude<AnalysisMetadataField, 'initial'>
      persisted: false
    }>

export interface AnalysisSnapshot {
  readonly projectId: string
  readonly revisionId: string
  readonly revisionDigest: string
  readonly createdAt: string
  readonly sourceRevisionId: string
  readonly sourceRevisionDigest: string
  readonly speakers: readonly AnalysisSpeaker[]
  readonly topics: readonly AnalysisTopic[]
  readonly candidates: readonly AnalysisCandidate[]
  readonly sensitivity: AnalysisSensitivity
  readonly stagedChanges: readonly AnalysisStagedChange[]
  readonly adoptionPersistence: 'notPersisted'
}

export type AnalysisTopicUpdate =
  | Readonly<{ field: 'title' | 'audience'; text: string }>
  | Readonly<{ field: 'summary'; text: string | null }>
  | Readonly<{ field: 'mainSpeaker'; speakerId: string | null }>
  | Readonly<{
      field: 'keywords' | 'publicationCautions'
      values: readonly string[]
    }>
  | Readonly<{
      field: 'importantStatements'
      sourceSpanIds: readonly string[]
    }>

export interface AnalysisReadRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof ANALYSIS_READ_METHOD
  readonly params: Readonly<{ projectId: string }>
}

export interface AnalysisDecisionParameters {
  readonly projectId: string
  readonly revisionId: string
  readonly digest: string
  readonly candidateId: string
  readonly reason: string
}

export interface AnalysisDecisionRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof ANALYSIS_REJECT_METHOD | typeof ANALYSIS_ADOPT_METHOD
  readonly params: Readonly<AnalysisDecisionParameters>
}

export interface AnalysisUpdateTopicParameters {
  readonly projectId: string
  readonly revisionId: string
  readonly digest: string
  readonly topicId: string
  readonly expectedTopicRevisionId: string
  readonly update: AnalysisTopicUpdate
  readonly reason: string
}

export interface AnalysisUpdateTopicRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof ANALYSIS_UPDATE_TOPIC_METHOD
  readonly params: Readonly<AnalysisUpdateTopicParameters>
}

export type AnalysisRequest =
  | AnalysisReadRequest
  | AnalysisDecisionRequest
  | AnalysisUpdateTopicRequest

export type AnalysisResponse = Readonly<{
  version: typeof BRIDGE_PROTOCOL_VERSION
  requestId: string
  method: AnalysisMethod
  result:
    | Readonly<{ status: 'available'; snapshot: AnalysisSnapshot }>
    | Readonly<{
        status: Exclude<AnalysisStatus, 'available'>
        snapshot: null
      }>
}>

export function createAnalysisReadRequest(
  requestId: string,
  projectId: string,
): AnalysisReadRequest {
  assertEntityId(requestId, 'request ID')
  assertEntityId(projectId, 'project ID')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: ANALYSIS_READ_METHOD,
    params: Object.freeze({ projectId }),
  })
}

export function createAnalysisAdoptRequest(
  requestId: string,
  parameters: AnalysisDecisionParameters,
): AnalysisDecisionRequest {
  return createDecisionRequest(requestId, ANALYSIS_ADOPT_METHOD, parameters)
}

export function createAnalysisRejectRequest(
  requestId: string,
  parameters: AnalysisDecisionParameters,
): AnalysisDecisionRequest {
  return createDecisionRequest(requestId, ANALYSIS_REJECT_METHOD, parameters)
}

export function createAnalysisUpdateTopicRequest(
  requestId: string,
  parameters: AnalysisUpdateTopicParameters,
): AnalysisUpdateTopicRequest {
  assertOcc(requestId, parameters)
  assertEntityId(parameters.topicId, 'topic ID')
  assertEntityId(parameters.expectedTopicRevisionId, 'topic revision ID')
  assertTopicUpdate(parameters.update)
  assertRequiredText(parameters.reason, 'topic update reason')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: ANALYSIS_UPDATE_TOPIC_METHOD,
    params: Object.freeze({
      projectId: parameters.projectId,
      revisionId: parameters.revisionId,
      digest: parameters.digest,
      topicId: parameters.topicId,
      expectedTopicRevisionId: parameters.expectedTopicRevisionId,
      update: freezeTopicUpdate(parameters.update),
      reason: parameters.reason,
    }),
  })
}

export function parseAnalysisResponse(value: unknown): AnalysisResponse | null {
  if (
    !isWithinUtf8Limit(value, ANALYSIS_MAX_RESPONSE_UTF8_BYTES) ||
    !hasExactKeys(value, ['version', 'requestId', 'method', 'result']) ||
    value.version !== BRIDGE_PROTOCOL_VERSION ||
    !isEntityId(value.requestId) ||
    !isAnalysisMethod(value.method) ||
    !hasExactKeys(value.result, ['status', 'snapshot'])
  ) {
    return null
  }

  if (value.result.status === 'available') {
    if (!isAnalysisSnapshot(value.result.snapshot)) return null
  } else if (
    value.result.snapshot !== null ||
    !isOneOf(value.result.status, failureStatuses)
  ) {
    return null
  }

  return cloneAndFreeze(value) as AnalysisResponse
}

function createDecisionRequest(
  requestId: string,
  method: typeof ANALYSIS_REJECT_METHOD | typeof ANALYSIS_ADOPT_METHOD,
  parameters: AnalysisDecisionParameters,
): AnalysisDecisionRequest {
  assertOcc(requestId, parameters)
  assertEntityId(parameters.candidateId, 'candidate ID')
  assertRequiredText(parameters.reason, 'decision reason')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method,
    params: Object.freeze({
      projectId: parameters.projectId,
      revisionId: parameters.revisionId,
      digest: parameters.digest,
      candidateId: parameters.candidateId,
      reason: parameters.reason,
    }),
  })
}

function assertOcc(
  requestId: string,
  parameters: Readonly<{
    projectId: string
    revisionId: string
    digest: string
  }>,
): void {
  assertEntityId(requestId, 'request ID')
  assertEntityId(parameters.projectId, 'project ID')
  assertEntityId(parameters.revisionId, 'analysis revision ID')
  if (!digestPattern.test(parameters.digest)) {
    throw new Error('The analysis revision digest must be canonical SHA-256.')
  }
}

function assertTopicUpdate(update: AnalysisTopicUpdate): void {
  switch (update.field) {
    case 'title':
    case 'audience':
      assertRequiredText(update.text, `${update.field} value`)
      return
    case 'summary':
      if (update.text !== null) assertRequiredText(update.text, 'summary value')
      return
    case 'mainSpeaker':
      if (update.speakerId !== null) {
        assertEntityId(update.speakerId, 'main speaker ID')
      }
      return
    case 'keywords':
    case 'publicationCautions':
      assertUniqueText(update.values, `${update.field} values`)
      return
    case 'importantStatements':
      assertUniqueIds(update.sourceSpanIds, 'important statement source-span IDs')
      return
  }

  const exhaustiveUpdate: never = update
  throw new Error(`Unsupported analysis topic update: ${String(exhaustiveUpdate)}`)
}

function freezeTopicUpdate(update: AnalysisTopicUpdate): AnalysisTopicUpdate {
  switch (update.field) {
    case 'keywords':
    case 'publicationCautions':
      return Object.freeze({ field: update.field, values: Object.freeze([...update.values]) })
    case 'importantStatements':
      return Object.freeze({
        field: update.field,
        sourceSpanIds: Object.freeze([...update.sourceSpanIds]),
      })
    default:
      return Object.freeze({ ...update })
  }
}

function freezeBoundedRequest<T extends AnalysisRequest>(request: T): T {
  if (!isWithinUtf8Limit(request, ANALYSIS_MAX_REQUEST_UTF8_BYTES)) {
    throw new Error('The analysis bridge request exceeds the protocol byte limit.')
  }
  return Object.freeze(request)
}

function isAnalysisSnapshot(value: unknown): value is AnalysisSnapshot {
  if (
    !hasExactKeys(value, snapshotKeys) ||
    !isEntityId(value.projectId) ||
    !isEntityId(value.revisionId) ||
    !isDigest(value.revisionDigest) ||
    !isCanonicalTimestamp(value.createdAt) ||
    !isEntityId(value.sourceRevisionId) ||
    !isDigest(value.sourceRevisionDigest) ||
    !Array.isArray(value.speakers) ||
    !Array.isArray(value.topics) ||
    !Array.isArray(value.candidates) ||
    !Array.isArray(value.stagedChanges) ||
    value.adoptionPersistence !== 'notPersisted'
  ) {
    return false
  }

  if (
    !value.speakers.every(isSpeaker) ||
    !hasUnique(value.speakers, 'speakerId') ||
    !value.topics.every(isTopic) ||
    !hasUnique(value.topics, 'topicId') ||
    !hasUnique(value.topics, 'metadataId') ||
    !value.candidates.every(isCandidate) ||
    !hasUnique(value.candidates, 'candidateId') ||
    !isSensitivity(value.sensitivity) ||
    value.sensitivity.revisionId !== value.sourceRevisionId ||
    !value.stagedChanges.every(isStagedChange)
  ) {
    return false
  }

  const candidatesById = new Map(
    value.candidates.map((candidate) => [candidate.candidateId, candidate]),
  )
  const topicsById = new Map(
    value.topics.map((topic) => [topic.topicId, topic]),
  )
  return hasValidCandidateRelationships(value.candidates, candidatesById) &&
    hasValidStagedRelationships(
      value.stagedChanges,
      candidatesById,
      topicsById,
    )
}

function hasValidCandidateRelationships(
  candidates: readonly AnalysisCandidate[],
  candidatesById: ReadonlyMap<string, AnalysisCandidate>,
): boolean {
  const decisionIds = new Set<string>()
  const replacementIds = new Set<string>()
  const resultingRevisionIds = new Set<string>()
  for (const candidate of candidates) {
    if (
      candidate.parentCandidateId === candidate.candidateId ||
      candidate.replacementCandidateId === candidate.candidateId ||
      (candidate.parentCandidateId !== null &&
        candidate.parentCandidateId === candidate.replacementCandidateId)
    ) {
      return false
    }

    const canReject = candidate.freshness === 'current' &&
      candidate.status === 'proposed' &&
      candidate.decision === null &&
      candidate.replacementCandidateId === null
    if (candidate.canReject !== canReject) return false

    const decision = candidate.decision
    if (decision === null) {
      if (candidate.replacementCandidateId !== null) return false
      continue
    }

    if (
      decisionIds.has(decision.decisionId) ||
      decision.replacementCandidateId !== candidate.replacementCandidateId ||
      (candidate.displayKind === 'technicalWarning' &&
        decision.decision === 'adopted') ||
      (decision.decision === 'adopted' &&
        candidate.suggestionKind !== 'termCorrection' &&
        decision.adoptedReference?.sourceSpanId !==
          candidate.source.sourceSpanId)
    ) {
      return false
    }
    decisionIds.add(decision.decisionId)

    if (decision.replacementCandidateId !== null) {
      if (replacementIds.has(decision.replacementCandidateId)) return false
      replacementIds.add(decision.replacementCandidateId)
    }
    if (decision.resultingTranscriptRevisionId !== null) {
      if (resultingRevisionIds.has(decision.resultingTranscriptRevisionId)) {
        return false
      }
      resultingRevisionIds.add(decision.resultingTranscriptRevisionId)
    }
  }

  for (const candidate of candidates) {
    if (candidate.parentCandidateId !== null) {
      const parent = candidatesById.get(candidate.parentCandidateId)
      if (parent !== undefined &&
        (parent.replacementCandidateId !== candidate.candidateId ||
          parent.decision?.decision !== 'modified' ||
          parent.producer !== candidate.producer ||
          parent.suggestionKind !== candidate.suggestionKind)) {
        return false
      }
    }
    if (candidate.replacementCandidateId !== null) {
      const replacement = candidatesById.get(candidate.replacementCandidateId)
      if (replacement !== undefined &&
        (replacement.parentCandidateId !== candidate.candidateId ||
          replacement.producer !== candidate.producer ||
          replacement.suggestionKind !== candidate.suggestionKind)) {
        return false
      }
    }
  }

  return true
}

function hasValidStagedRelationships(
  stagedChanges: readonly AnalysisStagedChange[],
  candidatesById: ReadonlyMap<string, AnalysisCandidate>,
  topicsById: ReadonlyMap<string, AnalysisTopic>,
): boolean {
  const stagedCandidateIds = new Set<string>()
  const stagedTopicRevisionIds = new Set<string>()
  const topicStages = new Map<
    string,
    Extract<
      AnalysisStagedChange,
      { readonly kind: 'topicMetadataRevision' }
    >[]
  >()

  for (const change of stagedChanges) {
    if (change.kind === 'topicMetadataRevision') {
      if (!topicsById.has(change.topicId) ||
        stagedTopicRevisionIds.has(change.revisionId)) {
        return false
      }
      stagedTopicRevisionIds.add(change.revisionId)
      const stages = topicStages.get(change.topicId) ?? []
      stages.push(change)
      topicStages.set(change.topicId, stages)
      continue
    }

    const candidate = candidatesById.get(change.candidateId)
    if (candidate === undefined ||
      stagedCandidateIds.has(change.candidateId) ||
      candidate.status !== 'adopted' ||
      candidate.decision?.decision !== 'adopted') {
      return false
    }
    stagedCandidateIds.add(change.candidateId)

    switch (change.kind) {
      case 'transcriptRevision':
        if (candidate.payload.producer !== 'termCorrection' ||
          change.sourceRevisionId !== candidate.payload.sourceRevisionId ||
          change.resultingRevisionId !==
            candidate.decision.resultingTranscriptRevisionId) {
          return false
        }
        break
      case 'editOperation': {
        const expectedOperation = candidate.suggestionKind === 'deleteRange'
          ? 'delete'
          : candidate.suggestionKind === 'topicBoundary'
            ? 'split'
            : null
        if (change.operation.kind !== expectedOperation) return false
        break
      }
      case 'cue':
        if (candidate.payload.producer !== 'emphasis' ||
          candidate.suggestionKind !== 'highlight') {
          return false
        }
        break
    }
  }

  for (const [topicId, stages] of topicStages) {
    const topic = topicsById.get(topicId)
    if (topic === undefined) return false
    const chainIds = new Set<string>()
    let previousRevisionId: string | null = null
    for (const stage of stages) {
      if (previousRevisionId === null) {
        chainIds.add(stage.parentRevisionId)
      } else if (stage.parentRevisionId !== previousRevisionId) {
        return false
      }
      if (chainIds.has(stage.revisionId)) return false
      chainIds.add(stage.revisionId)
      previousRevisionId = stage.revisionId
    }

    const current = stages[stages.length - 1]!
    if (topic.metadataRevisionId !== current.revisionId ||
      topic.parentMetadataRevisionId !== current.parentRevisionId ||
      topic.changedField !== current.field) {
      return false
    }
  }

  return true
}

function isSpeaker(value: unknown): value is AnalysisSpeaker {
  if (
    !hasExactKeys(value, speakerKeys) ||
    !isEntityId(value.speakerId) ||
    !isNullableEntityId(value.performerId) ||
    (value.format !== null && !isOneOf(value.format, speakerFormats)) ||
    typeof value.isAssigned !== 'boolean' ||
    typeof value.isVisible !== 'boolean' ||
    !isNullableRequiredText(value.text) ||
    !isNullableTimestamp(value.titleConfirmedAt) ||
    typeof value.titleOrAffiliationConfirmationRequired !== 'boolean' ||
    !isSource(value.source)
  ) {
    return false
  }

  return value.performerId === null
    ? value.format === null && !value.isAssigned && value.isVisible &&
        value.text !== null && value.titleConfirmedAt === null &&
        !value.titleOrAffiliationConfirmationRequired
    : value.format !== null && value.isAssigned &&
        (value.format === 'hidden'
          ? !value.isVisible && value.text === null
          : value.isVisible && value.text !== null)
}

function isTopic(value: unknown): value is AnalysisTopic {
  if (
    !hasExactKeys(value, topicKeys) ||
    !isEntityId(value.topicId) ||
    !isEntityId(value.topicRangeRevisionId) ||
    !isEntityId(value.metadataId) ||
    !isEntityId(value.metadataRevisionId) ||
    !isNullableEntityId(value.parentMetadataRevisionId) ||
    !isTimeRange(value.range) ||
    !isNullableEntityId(value.startBoundaryCandidateId) ||
    !isNullableEntityId(value.endBoundaryCandidateId) ||
    !isRequiredText(value.title) ||
    !isOneOf(value.titleOrigin, metadataOrigins) ||
    typeof value.titleRequiresManualReview !== 'boolean' ||
    !isNullableRequiredText(value.summary) ||
    !isOneOf(value.summaryOrigin, metadataOrigins) ||
    (value.mainSpeaker !== null && !isTopicMainSpeaker(value.mainSpeaker)) ||
    !isOneOf(value.mainSpeakerOrigin, metadataOrigins) ||
    typeof value.mainSpeakerTieUnresolved !== 'boolean' ||
    !isUniqueTextArray(value.keywords) ||
    !isOneOf(value.keywordsOrigin, metadataOrigins) ||
    !isUniqueIdArray(value.importantStatementSourceSpanIds) ||
    !isOneOf(value.importantStatementsOrigin, metadataOrigins) ||
    !isRequiredText(value.audience) ||
    !isOneOf(value.audienceOrigin, metadataOrigins) ||
    !isUniqueTextArray(value.publicationCautions) ||
    !isOneOf(value.publicationCautionsOrigin, metadataOrigins) ||
    !isOneOf(value.changedField, metadataFields) ||
    !isCanonicalTimestamp(value.createdAt) ||
    !isRequiredText(value.reason)
  ) {
    return false
  }

  return (
    value.titleRequiresManualReview ===
      (value.titleOrigin === 'deterministicPlaceholder') &&
    (value.summary === null) === (value.summaryOrigin === 'unset') &&
    (value.mainSpeaker === null) === (value.mainSpeakerOrigin === 'unset') &&
    (!value.mainSpeakerTieUnresolved || value.mainSpeaker === null) &&
    (value.keywords.length === 0) === (value.keywordsOrigin === 'unset') &&
    (value.importantStatementSourceSpanIds.length === 0) ===
      (value.importantStatementsOrigin === 'unset') &&
    (value.publicationCautions.length === 0) ===
      (value.publicationCautionsOrigin === 'unset') &&
    (value.changedField === 'initial') ===
      (value.parentMetadataRevisionId === null)
  )
}

function isTopicMainSpeaker(value: unknown): value is AnalysisTopicMainSpeaker {
  return (
    hasExactKeys(value, topicMainSpeakerKeys) &&
    isEntityId(value.speakerId) &&
    isNullableEntityId(value.performerId) &&
    typeof value.displayVisible === 'boolean' &&
    isNullableRequiredText(value.displayText) &&
    value.displayVisible === (value.displayText !== null) &&
    typeof value.titleOrAffiliationConfirmationRequired === 'boolean' &&
    isPositiveRational(value.overlapDuration)
  )
}

function isCandidate(value: unknown): value is AnalysisCandidate {
  if (
    !hasExactKeys(value, candidateKeys) ||
    !isEntityId(value.candidateId) ||
    !isEntityId(value.modelRunId) ||
    !isOneOf(value.producer, producers) ||
    !isOneOf(value.suggestionKind, suggestionKinds) ||
    !isOneOf(value.displayKind, displayKinds) ||
    !isOneOf(value.status, suggestionStatuses) ||
    !isOneOf(value.freshness, freshnessValues) ||
    typeof value.canAdopt !== 'boolean' ||
    typeof value.canReject !== 'boolean' ||
    !isDigest(value.sourceDigest) ||
    !isSource(value.source) ||
    !isRequiredText(value.reason) ||
    !isConfidence(value.confidence) ||
    !isNullableRequiredText(value.missingContext) ||
    !isNullableRequiredText(value.warning) ||
    !isNullableEntityId(value.parentCandidateId) ||
    !isNullableEntityId(value.replacementCandidateId) ||
    (value.decision !== null && !isDecision(value.decision)) ||
    !isPayload(value.payload)
  ) {
    return false
  }

  const candidate = value as unknown as AnalysisCandidate
  if (
    candidate.payload.producer !== candidate.producer ||
    !candidateShapeMatches(candidate) ||
    (candidate.decision === null) !== (candidate.status === 'proposed') ||
    (candidate.decision !== null &&
      (candidate.decision.candidateId !== candidate.candidateId ||
        candidate.decision.producer !== candidate.producer ||
        candidate.decision.suggestionKind !== candidate.suggestionKind ||
        candidate.decision.statusAfterDecision !== candidate.status)) ||
    (candidate.canAdopt && !candidate.canReject) ||
    ((candidate.canAdopt || candidate.canReject) &&
      (candidate.freshness !== 'current' ||
        candidate.status !== 'proposed' ||
        candidate.decision !== null)) ||
    (candidate.displayKind === 'technicalWarning' && candidate.canAdopt)
  ) {
    return false
  }

  return true
}

function candidateShapeMatches(candidate: AnalysisCandidate): boolean {
  switch (candidate.payload.producer) {
    case 'termCorrection':
      return candidate.producer === 'termCorrection' &&
        candidate.suggestionKind === 'termCorrection' &&
        candidate.displayKind === 'termCorrection' &&
        candidate.payload.sourceSpanId === candidate.source.sourceSpanId
    case 'topicBoundary':
      return candidate.producer === 'topicBoundary' &&
        candidate.suggestionKind === 'topicBoundary' &&
        candidate.displayKind === 'topicBoundary'
    case 'emphasis':
      return candidate.producer === 'emphasis' &&
        candidate.suggestionKind === 'highlight' &&
        candidate.displayKind === 'highlight'
    case 'audioReview':
      return candidate.producer === 'audioReview' &&
        candidate.suggestionKind === 'deleteRange' &&
        candidate.displayKind ===
          (candidate.payload.technicalWarningOnly
            ? 'technicalWarning'
            : 'deleteCandidate')
    default:
      return candidate.producer === candidate.payload.producer &&
        candidate.suggestionKind === 'deleteRange' &&
        candidate.displayKind === 'deleteCandidate'
  }
}

function isDecision(value: unknown): value is AnalysisDecision {
  if (
    !hasExactKeys(value, decisionKeys) ||
    !isEntityId(value.decisionId) ||
    !isEntityId(value.candidateId) ||
    !isOneOf(value.producer, producers) ||
    !isOneOf(value.suggestionKind, suggestionKinds) ||
    !isOneOf(value.decision, decisionKinds) ||
    !isOneOf(value.statusAfterDecision, decidedStatuses) ||
    !isDigest(value.sourceDigest) ||
    !isCanonicalTimestamp(value.decidedAt) ||
    !isRequiredText(value.reason) ||
    !isNullableEntityId(value.replacementCandidateId) ||
    !isNullableEntityId(value.resultingTranscriptRevisionId) ||
    (value.adoptedReference !== null &&
      (!hasExactKeys(value.adoptedReference, ['candidateId', 'sourceSpanId']) ||
        !isEntityId(value.adoptedReference.candidateId) ||
        !isEntityId(value.adoptedReference.sourceSpanId)))
  ) {
    return false
  }

  if (value.decision === 'rejected') {
    return value.statusAfterDecision === 'rejected' &&
      value.replacementCandidateId === null &&
      value.resultingTranscriptRevisionId === null &&
      value.adoptedReference === null
  }
  if (value.decision === 'modified') {
    return value.statusAfterDecision === 'reviewed' &&
      value.replacementCandidateId !== null &&
      value.resultingTranscriptRevisionId === null &&
      value.adoptedReference === null
  }
  return value.statusAfterDecision === 'adopted' &&
    (value.suggestionKind === 'termCorrection'
      ? value.resultingTranscriptRevisionId !== null &&
        value.adoptedReference === null
      : value.resultingTranscriptRevisionId === null &&
        value.adoptedReference?.candidateId === value.candidateId)
}

function isPayload(value: unknown): value is AnalysisCandidatePayload {
  if (!hasOwnString(value, 'producer')) return false
  switch (value.producer) {
    case 'termCorrection': return isTermPayload(value)
    case 'silence': return isSilencePayload(value)
    case 'editorialPhrase': return isEditorialPayload(value)
    case 'repetition': return isRepetitionPayload(value)
    case 'topicBoundary': return isTopicBoundaryPayload(value)
    case 'audioReview': return isAudioPayload(value)
    case 'contextDeviation': return isContextPayload(value)
    case 'emphasis': return isEmphasisPayload(value)
    default: return false
  }
}

function isTermPayload(value: unknown): value is AnalysisTermPayload {
  return hasExactKeys(value, termPayloadKeys) &&
    value.producer === 'termCorrection' &&
    isEntityId(value.sourceRevisionId) && isDigest(value.sourceRevisionDigest) &&
    isEntityId(value.sourceSpanId) && isEntityId(value.tokenId) &&
    (value.termScopeKind === 'project' || value.termScopeKind === 'series') &&
    isEntityId(value.termScopeId) && isEntityId(value.termEntryId) &&
    isNonnegativeInt32(value.termEntryVersion) &&
    isRequiredText(value.dictionaryReading) && isRequiredText(value.observedText) &&
    isRequiredText(value.proposedText) && value.observedText !== value.proposedText &&
    isOneOf(value.matchKind, termMatchKinds)
}

function isSilencePayload(value: unknown): value is AnalysisSilencePayload {
  return hasExactKeys(value, silencePayloadKeys) && value.producer === 'silence' &&
    isDetection(value.detection) && isRequiredText(value.thresholdVersion) &&
    isNonnegativeInt64String(value.analysisStartSample) &&
    isNonnegativeInt64String(value.analysisEndSample) &&
    BigInt(value.analysisEndSample) > BigInt(value.analysisStartSample) &&
    typeof value.noiseDb === 'number' && Number.isFinite(value.noiseDb) &&
    isPositiveRational(value.minimumDuration) &&
    isRequiredText(value.sampleRoundingRule) &&
    value.productionApproved === false && value.automaticDeletionAllowed === false
}

function isEditorialPayload(value: unknown): value is AnalysisEditorialPayload {
  return hasExactKeys(value, editorialPayloadKeys) &&
    value.producer === 'editorialPhrase' && isDetection(value.detection) &&
    isEntityId(value.sourceRevisionId) && isDigest(value.sourceRevisionDigest) &&
    isRequiredText(value.dictionaryVersion) && isRequiredText(value.matchedPhrase) &&
    isNonnegativeInt32(value.normalizedSentenceStart) &&
    isPositiveInt32(value.normalizedMatchLength) &&
    typeof value.usesUtteranceTimingFallback === 'boolean' &&
    typeof value.usesPartialTokenCoverage === 'boolean'
}

function isRepetitionPayload(value: unknown): value is AnalysisRepetitionPayload {
  if (!hasExactKeys(value, repetitionPayloadKeys) ||
    value.producer !== 'repetition' || !isDigest(value.candidateKey) ||
    !isEntityId(value.sourceRevisionId) || !isDigest(value.sourceRevisionDigest) ||
    !isEntityId(value.sourceSpanId) || !isUniqueIdArray(value.evidenceTokenIds) ||
    value.evidenceTokenIds.length === 0 || !isRequiredText(value.ruleVersion) ||
    typeof value.automaticDeletionAllowed !== 'boolean' ||
    typeof value.humanReviewRequired !== 'boolean' ||
    !hasExactKeys(value.features, repetitionFeatureKeys)) return false
  const features = value.features
  return isRequiredText(features.rule) && isEntityId(features.comparisonSourceSpanId) &&
    isUniqueIdArray(features.comparisonTokenIds) &&
    isPositiveInt32(features.matchingTokenCount) &&
    features.matchingTokenCount === features.comparisonTokenIds.length &&
    typeof features.exactNfkcMatch === 'boolean' &&
    typeof features.structurallyAdjacent === 'boolean' &&
    typeof features.sameSpeakerRequired === 'boolean' &&
    typeof features.candidateIsLaterOccurrence === 'boolean' &&
    typeof features.retentionChoiceRequired === 'boolean'
}

function isTopicBoundaryPayload(value: unknown): value is AnalysisTopicBoundaryPayload {
  if (!hasExactKeys(value, topicBoundaryPayloadKeys) ||
    value.producer !== 'topicBoundary' || !isDetection(value.detection) ||
    !isEntityId(value.sourceRevisionId) || !isDigest(value.sourceRevisionDigest) ||
    !isRational(value.suggestedBoundary) || !isRequiredText(value.ruleVersion) ||
    !hasExactKeys(value.features, topicBoundaryFeatureKeys)) return false
  const features = value.features
  return isEntityId(features.previousUtteranceId) &&
    isEntityId(features.currentUtteranceId) &&
    isEntityId(features.previousSourceSpanId) &&
    isEntityId(features.currentSourceSpanId) &&
    isEntityId(features.previousSpeakerId) && isEntityId(features.currentSpeakerId) &&
    isTimeRange(features.previousRange) && isTimeRange(features.currentRange) &&
    isRational(features.observedGap) &&
    typeof features.speakerChanged === 'boolean' &&
    typeof features.structurallyAdjacent === 'boolean' &&
    typeof features.semanticClassificationPerformed === 'boolean' &&
    typeof features.approvedPauseOrSimilarityThresholdApplied === 'boolean' &&
    typeof features.optionalShotBoundaryUsed === 'boolean'
}

function isAudioPayload(value: unknown): value is AnalysisAudioPayload {
  if (!hasExactKeys(value, audioPayloadKeys) || value.producer !== 'audioReview' ||
    !isDetection(value.detection) || !isOneOf(value.eventKind, audioEventKinds) ||
    !isUniqueEnumArray(value.evidenceMethods, audioEvidenceMethods) ||
    !isNonnegativeInt64String(value.analysisStartSample) ||
    !isNonnegativeInt64String(value.analysisEndSample) ||
    BigInt(value.analysisEndSample) <= BigInt(value.analysisStartSample) ||
    typeof value.manualListeningConfirmed !== 'boolean' ||
    typeof value.technicalWarningOnly !== 'boolean' ||
    typeof value.automaticDeletionAllowed !== 'boolean' ||
    typeof value.explicitHumanDecisionRequired !== 'boolean') return false
  return value.technicalWarningOnly === (value.eventKind === 'recordingQuality') &&
    (!value.technicalWarningOnly || !value.automaticDeletionAllowed)
}

function isContextPayload(value: unknown): value is AnalysisContextPayload {
  if (!hasExactKeys(value, contextPayloadKeys) ||
    value.producer !== 'contextDeviation' || !isDetection(value.detection) ||
    !isEntityId(value.sourceRevisionId) || !isDigest(value.sourceRevisionDigest) ||
    !isRequiredText(value.ruleVersion) ||
    !hasExactKeys(value.features, contextFeatureKeys) ||
    typeof value.explicitHumanDecisionRequired !== 'boolean') return false
  const features = value.features
  return isOneOf(features.position, contextPositions) &&
    isEntityId(features.utteranceId) && isEntityId(features.sourceSpanId) &&
    isEntityId(features.speakerId) && isTimeRange(features.utteranceRange) &&
    isNullableEntityId(features.previousTopicId) &&
    isNullableEntityId(features.nextTopicId) &&
    isRational(features.distanceToNearestTopicRange) &&
    typeof features.fullyOutsideExplicitTopicRanges === 'boolean' &&
    typeof features.semanticDistanceComputed === 'boolean' &&
    typeof features.timeThresholdApplied === 'boolean' &&
    typeof features.automaticDeletionAllowed === 'boolean'
}

function isEmphasisPayload(value: unknown): value is AnalysisEmphasisPayload {
  return hasExactKeys(value, emphasisPayloadKeys) && value.producer === 'emphasis' &&
    isEntityId(value.sourceRevisionId) && isDigest(value.sourceRevisionDigest) &&
    isEntityId(value.topicMetadataRevisionId) && isRequiredText(value.ruleVersion) &&
    isRequiredText(value.text) &&
    (value.textSource === 'originalTranscript' || value.textSource === 'manualSummary') &&
    typeof value.isExactOriginalText === 'boolean' &&
    isUniqueEnumArray(value.reasons, emphasisReasons) &&
    Array.isArray(value.termEvidence) && value.termEvidence.every(isTermEvidence) &&
    hasUnique(value.termEvidence, 'termEntryId') &&
    typeof value.automaticAdoptionAllowed === 'boolean'
}

function isTermEvidence(value: unknown): value is AnalysisEmphasisPayload['termEvidence'][number] {
  return hasExactKeys(value, ['termEntryId', 'termEntryVersion', 'writtenForm']) &&
    isEntityId(value.termEntryId) && isNonnegativeInt32(value.termEntryVersion) &&
    isRequiredText(value.writtenForm)
}

function isDetection(value: unknown): value is AnalysisDetection {
  return hasExactKeys(value, ['detectionId', 'kind', 'description', 'confidence']) &&
    isEntityId(value.detectionId) && isOneOf(value.kind, detectionKinds) &&
    isRequiredText(value.description) && isConfidence(value.confidence)
}

function isSensitivity(value: unknown): value is AnalysisSensitivity {
  return hasExactKeys(value, sensitivityKeys) && isEntityId(value.revisionId) &&
    isRequiredText(value.ruleSetVersion) && typeof value.isComplete === 'boolean' &&
    typeof value.requiresExternalOutputWarning === 'boolean' &&
    value.requiresExternalOutputWarning ===
      (!value.isComplete || (Array.isArray(value.findings) && value.findings.length !== 0)) &&
    Array.isArray(value.findings) && value.findings.every(isSensitivityFinding)
}

function isSensitivityFinding(value: unknown): boolean {
  return hasExactKeys(value, sensitivityFindingKeys) &&
    isEntityId(value.utteranceId) && isEntityId(value.sourceSpanId) &&
    isOneOf(value.kind, sensitivityKinds) && isRequiredText(value.reasonCode) &&
    value.reasonCode === sensitivityReasonCodes[value.kind] &&
    isTimeRange(value.sourceRange)
}

function isStagedChange(value: unknown): value is AnalysisStagedChange {
  if (!hasOwnString(value, 'kind')) return false
  switch (value.kind) {
    case 'transcriptRevision':
      return hasExactKeys(value, transcriptStageKeys) &&
        isEntityId(value.candidateId) && isEntityId(value.sourceRevisionId) &&
        isEntityId(value.resultingRevisionId) &&
        value.sourceRevisionId !== value.resultingRevisionId && value.persisted === false
    case 'editOperation':
      return hasExactKeys(value, editStageKeys) && isEntityId(value.candidateId) &&
        isEditOperation(value.operation) && value.persisted === false
    case 'cue':
      return hasExactKeys(value, cueStageKeys) && isEntityId(value.candidateId) &&
        isCue(value.cue) && value.persisted === false
    case 'topicMetadataRevision':
      return hasExactKeys(value, topicStageKeys) && isEntityId(value.topicId) &&
        isEntityId(value.revisionId) && isEntityId(value.parentRevisionId) &&
        value.revisionId !== value.parentRevisionId &&
        isOneOf(value.field, editableMetadataFields) && value.persisted === false
    default:
      return false
  }
}

function isEditOperation(value: unknown): boolean {
  if (!hasExactKeys(value, ['kind', 'elementId', 'splitOffset', 'rightElementId']) ||
    !isEntityId(value.elementId)) return false
  return value.kind === 'delete'
    ? value.splitOffset === null && value.rightElementId === null
    : value.kind === 'split' && isPositiveRational(value.splitOffset) &&
        isEntityId(value.rightElementId) && value.rightElementId !== value.elementId
}

function isCue(value: unknown): boolean {
  return hasExactKeys(value, ['cueId', 'kind', 'outputRange', 'regionName']) &&
    isEntityId(value.cueId) && value.kind === 'graphic' &&
    isTimeRange(value.outputRange) && isNullableRequiredText(value.regionName)
}

function isSource(value: unknown): value is AnalysisSource {
  return hasExactKeys(value, ['sourceSpanId', 'range']) &&
    isEntityId(value.sourceSpanId) && isTimeRange(value.range)
}

function isTimeRange(value: unknown): value is AnalysisTimeRange {
  return hasExactKeys(value, ['start', 'duration']) && isRational(value.start) &&
    isPositiveRational(value.duration) && addRationals(value.start, value.duration) !== null
}

function isPositiveRational(value: unknown): value is RationalTime {
  return isRational(value) && BigInt(value.value) > 0n
}

function isRational(value: unknown): value is RationalTime {
  if (!hasExactKeys(value, ['value', 'rate']) ||
    typeof value.value !== 'string' || typeof value.rate !== 'string' ||
    !signedIntegerPattern.test(value.value) || !positiveIntegerPattern.test(value.rate)) {
    return false
  }
  const numerator = BigInt(value.value)
  const denominator = BigInt(value.rate)
  return numerator >= int64Minimum && numerator <= int64Maximum &&
    denominator <= int64Maximum && greatestCommonDivisor(abs(numerator), denominator) === 1n
}

function addRationals(left: RationalTime, right: RationalTime): RationalTime | null {
  const numerator = BigInt(left.value) * BigInt(right.rate) +
    BigInt(right.value) * BigInt(left.rate)
  const denominator = BigInt(left.rate) * BigInt(right.rate)
  const divisor = greatestCommonDivisor(abs(numerator), denominator)
  const reducedNumerator = numerator / divisor
  const reducedDenominator = denominator / divisor
  return reducedNumerator < int64Minimum || reducedNumerator > int64Maximum ||
    reducedDenominator > int64Maximum
    ? null
    : { value: reducedNumerator.toString(), rate: reducedDenominator.toString() }
}

function isCanonicalTimestamp(value: unknown): value is string {
  if (typeof value !== 'string') return false
  const match = timestampPattern.exec(value)
  if (match === null || match[1] === '0000') return false
  const milliseconds = `${value.slice(0, 19)}.${match[7]!.slice(0, 3)}Z`
  const timestamp = new Date(milliseconds)
  return Number.isFinite(timestamp.valueOf()) && timestamp.toISOString() === milliseconds
}

function isNullableTimestamp(value: unknown): value is string | null {
  return value === null || isCanonicalTimestamp(value)
}

function isRequiredText(value: unknown): value is string {
  return typeof value === 'string' && value.length !== 0 && value === value.trim() &&
    /\S/u.test(value) && !containsUnpairedSurrogate(value)
}

function isNullableRequiredText(value: unknown): value is string | null {
  return value === null || isRequiredText(value)
}

function isConfidence(value: unknown): value is number | null {
  return value === null ||
    (typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 1)
}

function isNonnegativeInt64String(value: unknown): value is string {
  return typeof value === 'string' && nonnegativeIntegerPattern.test(value) &&
    BigInt(value) <= int64Maximum
}

function isNonnegativeInt32(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) &&
    value >= 0 && value <= 2_147_483_647
}

function isPositiveInt32(value: unknown): value is number {
  return isNonnegativeInt32(value) && value > 0
}

function isUniqueTextArray(value: unknown): value is readonly string[] {
  return Array.isArray(value) && value.every(isRequiredText) &&
    new Set(value).size === value.length
}

function isUniqueIdArray(value: unknown): value is readonly string[] {
  return Array.isArray(value) && value.every(isEntityId) &&
    new Set(value).size === value.length
}

function isUniqueEnumArray<T extends string>(
  value: unknown,
  allowed: readonly T[],
): value is readonly T[] {
  return Array.isArray(value) && value.every((item) => isOneOf(item, allowed)) &&
    new Set(value).size === value.length
}

function assertEntityId(value: string, description: string): void {
  if (!entityIdPattern.test(value)) {
    throw new Error(`The analysis ${description} must be a non-empty lowercase UUID.`)
  }
}

function assertRequiredText(value: string, description: string): void {
  if (!isRequiredText(value)) {
    throw new Error(`The analysis ${description} must be nonblank valid text without surrounding whitespace.`)
  }
}

function assertUniqueText(values: readonly string[], description: string): void {
  if (!isUniqueTextArray(values)) {
    throw new Error(`The analysis ${description} must be unique valid text values.`)
  }
}

function assertUniqueIds(values: readonly string[], description: string): void {
  if (!isUniqueIdArray(values)) {
    throw new Error(`The analysis ${description} must be unique canonical IDs.`)
  }
}

function hasUnique<TKey extends string>(
  values: readonly Record<TKey, unknown>[],
  key: TKey,
): boolean {
  const seen = new Set<unknown>()
  return values.every((value) => {
    const item = value[key]
    if (typeof item !== 'string' || seen.has(item)) return false
    seen.add(item)
    return true
  })
}

function isAnalysisMethod(value: unknown): value is AnalysisMethod {
  return isOneOf(value, analysisMethods)
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

function isOneOf<T extends string>(value: unknown, allowed: readonly T[]): value is T {
  return typeof value === 'string' && allowed.includes(value as T)
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

function isWithinUtf8Limit(value: unknown, maximumBytes: number): boolean {
  try {
    const serialized = JSON.stringify(value)
    return serialized !== undefined &&
      new TextEncoder().encode(serialized).byteLength <= maximumBytes
  } catch {
    return false
  }
}

function cloneAndFreeze<T>(value: T): T {
  return deepFreeze(JSON.parse(JSON.stringify(value)) as T)
}

function deepFreeze<T>(value: T): T {
  if (typeof value === 'object' && value !== null) {
    for (const child of Object.values(value)) deepFreeze(child)
    Object.freeze(value)
  }
  return value
}

function hasOwnString(value: unknown, key: string): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) &&
    Object.prototype.hasOwnProperty.call(value, key) &&
    typeof (value as Record<string, unknown>)[key] === 'string'
}

function hasExactKeys<const TKey extends string>(
  value: unknown,
  expectedKeys: readonly TKey[],
): value is Record<TKey, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
  const keys = Object.keys(value)
  return keys.length === expectedKeys.length &&
    expectedKeys.every((key) => Object.prototype.hasOwnProperty.call(value, key))
}

const analysisMethods: readonly AnalysisMethod[] = [
  ANALYSIS_READ_METHOD,
  ANALYSIS_REJECT_METHOD,
  ANALYSIS_ADOPT_METHOD,
  ANALYSIS_UPDATE_TOPIC_METHOD,
]
const failureStatuses: readonly Exclude<AnalysisStatus, 'available'>[] = [
  'forbidden', 'unavailable', 'staleBase', 'conflict', 'noChange', 'invalid',
]
const producers: readonly AnalysisProducer[] = [
  'termCorrection', 'silence', 'editorialPhrase', 'repetition',
  'topicBoundary', 'audioReview', 'contextDeviation', 'emphasis',
]
const suggestionKinds: readonly AnalysisSuggestionKind[] = [
  'deleteRange', 'topicBoundary', 'highlight', 'termCorrection',
]
const displayKinds: readonly AnalysisDisplayKind[] = [
  'termCorrection', 'deleteCandidate', 'topicBoundary', 'highlight', 'technicalWarning',
]
const suggestionStatuses = ['proposed', 'reviewed', 'rejected', 'adopted'] as const
const decidedStatuses = ['reviewed', 'rejected', 'adopted'] as const
const freshnessValues = ['current', 'stale'] as const
const decisionKinds = ['rejected', 'modified', 'adopted'] as const
const speakerFormats = ['fullName', 'surname', 'role', 'hidden'] as const
const metadataOrigins: readonly AnalysisMetadataOrigin[] = [
  'unset', 'deterministicPlaceholder', 'projectSnapshot',
  'deterministicTranscriptOverlap', 'manualEditorial',
]
const metadataFields: readonly AnalysisMetadataField[] = [
  'initial', 'title', 'summary', 'mainSpeaker', 'keywords',
  'importantStatements', 'audience', 'publicationCautions',
]
const editableMetadataFields = metadataFields.slice(1) as readonly Exclude<AnalysisMetadataField, 'initial'>[]
const detectionKinds: readonly AnalysisDetection['kind'][] = [
  'silence', 'repetition', 'preparationConversation', 'recordingProblem',
  'contextDeviation', 'audioEvent', 'editorialInstruction', 'topicBoundary',
]
const termMatchKinds: readonly AnalysisTermPayload['matchKind'][] = [
  'writtenFormCompatibilityVariant', 'writtenFormCaseVariant', 'dictionaryReading',
]
const audioEventKinds: readonly AnalysisAudioPayload['eventKind'][] = [
  'cough', 'noise', 'wait', 'recordingQuality',
]
const audioEvidenceMethods: readonly AnalysisAudioPayload['evidenceMethods'][number][] = [
  'manualListening', 'ffmpegSilence', 'waveformLowEnergy', 'waveformClipping',
  'yamnetHeld', 'sileroVadBenchmarkOnly',
]
const contextPositions: readonly AnalysisContextPayload['features']['position'][] = [
  'leadingBeforeFirstTopic', 'betweenTopicRanges', 'trailingAfterLastTopic',
]
const emphasisReasons: readonly AnalysisEmphasisPayload['reasons'][number][] = [
  'importantStatement', 'number', 'productName', 'publicationCaution', 'keyword',
  'answerAfterExplicitQuestion', 'topicTransitionSummary', 'speechSegment',
]
const sensitivityKinds: readonly AnalysisSensitivity['findings'][number]['kind'][] = [
  'emailAddress', 'internalNetworkLocation', 'credentialPattern', 'dictionaryTerm',
  'customerName', 'unreleasedProduct', 'personalInformation',
  'financialInformation', 'confidentialMaterial', 'futurePlan',
]
const sensitivityReasonCodes: Readonly<Record<AnalysisSensitivity['findings'][number]['kind'], string>> = {
  emailAddress: 'sensitivity.email-address',
  internalNetworkLocation: 'sensitivity.internal-network-location',
  credentialPattern: 'sensitivity.credential-pattern',
  dictionaryTerm: 'sensitivity.dictionary-term',
  customerName: 'sensitivity.customer-name',
  unreleasedProduct: 'sensitivity.unreleased-product',
  personalInformation: 'sensitivity.personal-information',
  financialInformation: 'sensitivity.financial-information',
  confidentialMaterial: 'sensitivity.confidential-material',
  futurePlan: 'sensitivity.future-plan',
}

const snapshotKeys = [
  'projectId', 'revisionId', 'revisionDigest', 'createdAt', 'sourceRevisionId',
  'sourceRevisionDigest', 'speakers', 'topics', 'candidates', 'sensitivity',
  'stagedChanges', 'adoptionPersistence',
] as const
const speakerKeys = [
  'speakerId', 'performerId', 'format', 'isAssigned', 'isVisible', 'text',
  'titleConfirmedAt', 'titleOrAffiliationConfirmationRequired', 'source',
] as const
const topicKeys = [
  'topicId', 'topicRangeRevisionId', 'metadataId', 'metadataRevisionId',
  'parentMetadataRevisionId', 'range', 'startBoundaryCandidateId',
  'endBoundaryCandidateId', 'title', 'titleOrigin', 'titleRequiresManualReview',
  'summary', 'summaryOrigin', 'mainSpeaker', 'mainSpeakerOrigin',
  'mainSpeakerTieUnresolved', 'keywords', 'keywordsOrigin',
  'importantStatementSourceSpanIds', 'importantStatementsOrigin', 'audience',
  'audienceOrigin', 'publicationCautions', 'publicationCautionsOrigin',
  'changedField', 'createdAt', 'reason',
] as const
const topicMainSpeakerKeys = [
  'speakerId', 'performerId', 'displayVisible', 'displayText',
  'titleOrAffiliationConfirmationRequired', 'overlapDuration',
] as const
const candidateKeys = [
  'candidateId', 'modelRunId', 'producer', 'suggestionKind', 'displayKind',
  'status', 'freshness', 'canAdopt', 'canReject', 'sourceDigest', 'source',
  'reason', 'confidence', 'missingContext', 'warning', 'parentCandidateId',
  'replacementCandidateId', 'decision', 'payload',
] as const
const decisionKeys = [
  'decisionId', 'candidateId', 'producer', 'suggestionKind', 'decision',
  'statusAfterDecision', 'sourceDigest', 'decidedAt', 'reason',
  'replacementCandidateId', 'resultingTranscriptRevisionId', 'adoptedReference',
] as const
const termPayloadKeys = [
  'producer', 'sourceRevisionId', 'sourceRevisionDigest', 'sourceSpanId',
  'tokenId', 'termScopeKind', 'termScopeId', 'termEntryId', 'termEntryVersion',
  'dictionaryReading', 'observedText', 'proposedText', 'matchKind',
] as const
const silencePayloadKeys = [
  'producer', 'detection', 'thresholdVersion', 'analysisStartSample',
  'analysisEndSample', 'noiseDb', 'minimumDuration', 'sampleRoundingRule',
  'productionApproved', 'automaticDeletionAllowed',
] as const
const editorialPayloadKeys = [
  'producer', 'detection', 'sourceRevisionId', 'sourceRevisionDigest',
  'dictionaryVersion', 'matchedPhrase', 'normalizedSentenceStart',
  'normalizedMatchLength', 'usesUtteranceTimingFallback', 'usesPartialTokenCoverage',
] as const
const repetitionPayloadKeys = [
  'producer', 'candidateKey', 'sourceRevisionId', 'sourceRevisionDigest',
  'sourceSpanId', 'evidenceTokenIds', 'ruleVersion', 'automaticDeletionAllowed',
  'humanReviewRequired', 'features',
] as const
const repetitionFeatureKeys = [
  'rule', 'comparisonSourceSpanId', 'comparisonTokenIds', 'matchingTokenCount',
  'exactNfkcMatch', 'structurallyAdjacent', 'sameSpeakerRequired',
  'candidateIsLaterOccurrence', 'retentionChoiceRequired',
] as const
const topicBoundaryPayloadKeys = [
  'producer', 'detection', 'sourceRevisionId', 'sourceRevisionDigest',
  'suggestedBoundary', 'ruleVersion', 'features',
] as const
const topicBoundaryFeatureKeys = [
  'previousUtteranceId', 'currentUtteranceId', 'previousSourceSpanId',
  'currentSourceSpanId', 'previousSpeakerId', 'currentSpeakerId', 'previousRange',
  'currentRange', 'observedGap', 'speakerChanged', 'structurallyAdjacent',
  'semanticClassificationPerformed', 'approvedPauseOrSimilarityThresholdApplied',
  'optionalShotBoundaryUsed',
] as const
const audioPayloadKeys = [
  'producer', 'detection', 'eventKind', 'evidenceMethods', 'analysisStartSample',
  'analysisEndSample', 'manualListeningConfirmed', 'technicalWarningOnly',
  'automaticDeletionAllowed', 'explicitHumanDecisionRequired',
] as const
const contextPayloadKeys = [
  'producer', 'detection', 'sourceRevisionId', 'sourceRevisionDigest',
  'ruleVersion', 'features', 'explicitHumanDecisionRequired',
] as const
const contextFeatureKeys = [
  'position', 'utteranceId', 'sourceSpanId', 'speakerId', 'utteranceRange',
  'previousTopicId', 'nextTopicId', 'distanceToNearestTopicRange',
  'fullyOutsideExplicitTopicRanges', 'semanticDistanceComputed',
  'timeThresholdApplied', 'automaticDeletionAllowed',
] as const
const emphasisPayloadKeys = [
  'producer', 'sourceRevisionId', 'sourceRevisionDigest',
  'topicMetadataRevisionId', 'ruleVersion', 'text', 'textSource',
  'isExactOriginalText', 'reasons', 'termEvidence', 'automaticAdoptionAllowed',
] as const
const sensitivityKeys = [
  'revisionId', 'ruleSetVersion', 'isComplete',
  'requiresExternalOutputWarning', 'findings',
] as const
const sensitivityFindingKeys = [
  'utteranceId', 'sourceSpanId', 'kind', 'reasonCode', 'sourceRange',
] as const
const transcriptStageKeys = [
  'kind', 'candidateId', 'sourceRevisionId', 'resultingRevisionId', 'persisted',
] as const
const editStageKeys = ['kind', 'candidateId', 'operation', 'persisted'] as const
const cueStageKeys = ['kind', 'candidateId', 'cue', 'persisted'] as const
const topicStageKeys = [
  'kind', 'topicId', 'revisionId', 'parentRevisionId', 'field', 'persisted',
] as const