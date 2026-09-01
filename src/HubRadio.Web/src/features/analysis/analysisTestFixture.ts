import {
  ANALYSIS_READ_METHOD,
  type AnalysisCandidate,
  type AnalysisMethod,
  type AnalysisSnapshot,
} from './analysisProtocol'

export const analysisRequestId = '00112233-4455-6677-8899-aabbccddeeff'
export const analysisProjectId = '10000000-0000-4000-8000-000000000001'
export const analysisRevisionId = '10000000-0000-4000-8000-000000000002'
export const analysisSourceRevisionId = '10000000-0000-4000-8000-000000000003'
export const analysisSpeakerId = '10000000-0000-4000-8000-000000000004'
export const analysisPerformerId = '10000000-0000-4000-8000-000000000005'
export const analysisSourceSpanId = '10000000-0000-4000-8000-000000000006'
export const analysisTopicId = '10000000-0000-4000-8000-000000000007'
export const analysisTopicRangeRevisionId = '10000000-0000-4000-8000-000000000008'
export const analysisMetadataId = '10000000-0000-4000-8000-000000000009'
export const analysisMetadataRevisionId = '10000000-0000-4000-8000-00000000000a'
export const analysisModelRunId = '10000000-0000-4000-8000-00000000000b'
export const analysisDigest = 'a'.repeat(64)
export const analysisSourceDigest = 'b'.repeat(64)

export interface MutableAnalysisResponseValue {
  version: number
  requestId: string
  method: string
  result: {
    status: string
    snapshot: AnalysisSnapshot | null
    actorSid?: string
  }
  extra?: boolean
}

export function createAnalysisResponseValue(
  method: AnalysisMethod = ANALYSIS_READ_METHOD,
  candidates: readonly AnalysisCandidate[] = [createAnalysisCandidate('termCorrection', 1)],
): MutableAnalysisResponseValue {
  return {
    version: 1,
    requestId: analysisRequestId,
    method,
    result: {
      status: 'available',
      snapshot: {
        projectId: analysisProjectId,
        revisionId: analysisRevisionId,
        revisionDigest: analysisDigest,
        createdAt: '2026-01-02T03:04:05.0000000+00:00',
        sourceRevisionId: analysisSourceRevisionId,
        sourceRevisionDigest: analysisSourceDigest,
        speakers: [
          {
            speakerId: analysisSpeakerId,
            performerId: analysisPerformerId,
            format: 'fullName',
            isAssigned: true,
            isVisible: true,
            text: '佐藤 花子',
            titleConfirmedAt: '2026-01-01T03:04:05.0000000+00:00',
            titleOrAffiliationConfirmationRequired: false,
            source: createSource(),
          },
        ],
        topics: [
          {
            topicId: analysisTopicId,
            topicRangeRevisionId: analysisTopicRangeRevisionId,
            metadataId: analysisMetadataId,
            metadataRevisionId: analysisMetadataRevisionId,
            parentMetadataRevisionId: null,
            range: createRange('0', '10'),
            startBoundaryCandidateId: null,
            endBoundaryCandidateId: null,
            title: 'クラウド運用',
            titleOrigin: 'projectSnapshot',
            titleRequiresManualReview: false,
            summary: '運用上の判断を説明します。',
            summaryOrigin: 'projectSnapshot',
            mainSpeaker: {
              speakerId: analysisSpeakerId,
              performerId: analysisPerformerId,
              displayVisible: true,
              displayText: '佐藤 花子',
              titleOrAffiliationConfirmationRequired: false,
              overlapDuration: { value: '8', rate: '1' },
            },
            mainSpeakerOrigin: 'deterministicTranscriptOverlap',
            mainSpeakerTieUnresolved: false,
            keywords: ['Azure'],
            keywordsOrigin: 'projectSnapshot',
            importantStatementSourceSpanIds: [analysisSourceSpanId],
            importantStatementsOrigin: 'projectSnapshot',
            audience: '一般視聴者',
            audienceOrigin: 'projectSnapshot',
            publicationCautions: [],
            publicationCautionsOrigin: 'unset',
            changedField: 'initial',
            createdAt: '2026-01-02T03:04:05.0000000+00:00',
            reason: '初期metadata',
          },
        ],
        candidates,
        sensitivity: {
          revisionId: analysisSourceRevisionId,
          ruleSetVersion: 'sensitivity-v1',
          isComplete: true,
          requiresExternalOutputWarning: true,
          findings: [
            {
              utteranceId: '10000000-0000-4000-8000-00000000000c',
              sourceSpanId: analysisSourceSpanId,
              kind: 'credentialPattern',
              reasonCode: 'sensitivity.credential-pattern',
              sourceRange: createRange('1', '1'),
            },
          ],
        },
        stagedChanges: [],
        adoptionPersistence: 'notPersisted',
      },
    },
  }
}

export function createAllProducerCandidates(): readonly AnalysisCandidate[] {
  return [
    createAnalysisCandidate('termCorrection', 1),
    createAnalysisCandidate('silence', 2),
    createAnalysisCandidate('editorialPhrase', 3),
    createAnalysisCandidate('repetition', 4),
    createAnalysisCandidate('topicBoundary', 5),
    createAnalysisCandidate('audioReview', 6),
    createAnalysisCandidate('contextDeviation', 7),
    createAnalysisCandidate('emphasis', 8),
  ]
}

export function createAnalysisCandidate(
  producer: AnalysisCandidate['producer'],
  sequence: number,
): AnalysisCandidate {
  const candidateId = entityId(300, sequence)
  const detectionId = entityId(400, sequence)
  const common = {
    candidateId,
    modelRunId: analysisModelRunId,
    producer,
    status: 'proposed' as const,
    freshness: 'current' as const,
    canReject: true,
    sourceDigest: analysisSourceDigest,
    source: createSource(),
    reason: `${producer} の根拠`,
    confidence: sequence % 2 === 0 ? null : 0.75,
    missingContext: null,
    warning: null,
    parentCandidateId: null,
    replacementCandidateId: null,
    decision: null,
  }
  const detection = {
    detectionId,
    kind: 'audioEvent' as const,
    description: `${producer} の検出根拠`,
    confidence: 0.75,
  }

  switch (producer) {
    case 'termCorrection':
      return {
        ...common,
        suggestionKind: 'termCorrection',
        displayKind: 'termCorrection',
        canAdopt: true,
        payload: {
          producer,
          sourceRevisionId: analysisSourceRevisionId,
          sourceRevisionDigest: analysisSourceDigest,
          sourceSpanId: analysisSourceSpanId,
          tokenId: entityId(500, sequence),
          termScopeKind: 'project',
          termScopeId: analysisProjectId,
          termEntryId: entityId(600, sequence),
          termEntryVersion: 3,
          dictionaryReading: 'ハブ',
          observedText: 'ハブ',
          proposedText: 'Hub',
          matchKind: 'dictionaryReading',
        },
      }
    case 'silence':
      return {
        ...common,
        suggestionKind: 'deleteRange',
        displayKind: 'deleteCandidate',
        canAdopt: true,
        payload: {
          producer,
          detection: { ...detection, kind: 'silence' },
          thresholdVersion: 'benchmark-only-v1',
          analysisStartSample: '0',
          analysisEndSample: '48000',
          noiseDb: -42.5,
          minimumDuration: { value: '1', rate: '2' },
          sampleRoundingRule: 'floor-start-ceiling-end',
          productionApproved: false,
          automaticDeletionAllowed: false,
        },
      }
    case 'editorialPhrase':
      return {
        ...common,
        suggestionKind: 'deleteRange',
        displayKind: 'deleteCandidate',
        canAdopt: true,
        payload: {
          producer,
          detection: { ...detection, kind: 'editorialInstruction' },
          sourceRevisionId: analysisSourceRevisionId,
          sourceRevisionDigest: analysisSourceDigest,
          dictionaryVersion: 'editorial-v1',
          matchedPhrase: 'ここをカット',
          normalizedSentenceStart: 0,
          normalizedMatchLength: 6,
          usesUtteranceTimingFallback: false,
          usesPartialTokenCoverage: false,
        },
      }
    case 'repetition':
      return {
        ...common,
        suggestionKind: 'deleteRange',
        displayKind: 'deleteCandidate',
        canAdopt: true,
        payload: {
          producer,
          candidateKey: 'c'.repeat(64),
          sourceRevisionId: analysisSourceRevisionId,
          sourceRevisionDigest: analysisSourceDigest,
          sourceSpanId: analysisSourceSpanId,
          evidenceTokenIds: [entityId(500, sequence)],
          ruleVersion: 'repetition-v1',
          automaticDeletionAllowed: false,
          humanReviewRequired: true,
          features: {
            rule: 'exact-adjacent',
            comparisonSourceSpanId: entityId(700, sequence),
            comparisonTokenIds: [entityId(800, sequence)],
            matchingTokenCount: 1,
            exactNfkcMatch: true,
            structurallyAdjacent: true,
            sameSpeakerRequired: true,
            candidateIsLaterOccurrence: true,
            retentionChoiceRequired: true,
          },
        },
      }
    case 'topicBoundary':
      return {
        ...common,
        suggestionKind: 'topicBoundary',
        displayKind: 'topicBoundary',
        canAdopt: true,
        payload: {
          producer,
          detection: { ...detection, kind: 'topicBoundary' },
          sourceRevisionId: analysisSourceRevisionId,
          sourceRevisionDigest: analysisSourceDigest,
          suggestedBoundary: { value: '5', rate: '1' },
          ruleVersion: 'topic-v1',
          features: {
            previousUtteranceId: entityId(900, 1),
            currentUtteranceId: entityId(900, 2),
            previousSourceSpanId: entityId(901, 1),
            currentSourceSpanId: analysisSourceSpanId,
            previousSpeakerId: entityId(902, 1),
            currentSpeakerId: analysisSpeakerId,
            previousRange: createRange('0', '4'),
            currentRange: createRange('5', '4'),
            observedGap: { value: '1', rate: '1' },
            speakerChanged: true,
            structurallyAdjacent: true,
            semanticClassificationPerformed: false,
            approvedPauseOrSimilarityThresholdApplied: false,
            optionalShotBoundaryUsed: false,
          },
        },
      }
    case 'audioReview':
      return {
        ...common,
        suggestionKind: 'deleteRange',
        displayKind: 'technicalWarning',
        canAdopt: false,
        payload: {
          producer,
          detection: { ...detection, kind: 'recordingProblem' },
          eventKind: 'recordingQuality',
          evidenceMethods: ['waveformClipping'],
          analysisStartSample: '0',
          analysisEndSample: '48000',
          manualListeningConfirmed: false,
          technicalWarningOnly: true,
          automaticDeletionAllowed: false,
          explicitHumanDecisionRequired: true,
        },
      }
    case 'contextDeviation':
      return {
        ...common,
        suggestionKind: 'deleteRange',
        displayKind: 'deleteCandidate',
        canAdopt: true,
        payload: {
          producer,
          detection: { ...detection, kind: 'contextDeviation' },
          sourceRevisionId: analysisSourceRevisionId,
          sourceRevisionDigest: analysisSourceDigest,
          ruleVersion: 'context-v1',
          features: {
            position: 'trailingAfterLastTopic',
            utteranceId: entityId(900, sequence),
            sourceSpanId: analysisSourceSpanId,
            speakerId: analysisSpeakerId,
            utteranceRange: createRange('10', '2'),
            previousTopicId: analysisTopicId,
            nextTopicId: null,
            distanceToNearestTopicRange: { value: '1', rate: '1' },
            fullyOutsideExplicitTopicRanges: true,
            semanticDistanceComputed: false,
            timeThresholdApplied: false,
            automaticDeletionAllowed: false,
          },
          explicitHumanDecisionRequired: true,
        },
      }
    case 'emphasis':
      return {
        ...common,
        suggestionKind: 'highlight',
        displayKind: 'highlight',
        canAdopt: true,
        payload: {
          producer,
          sourceRevisionId: analysisSourceRevisionId,
          sourceRevisionDigest: analysisSourceDigest,
          topicMetadataRevisionId: analysisMetadataRevisionId,
          ruleVersion: 'emphasis-v1',
          text: '運用を自動化します',
          textSource: 'originalTranscript',
          isExactOriginalText: true,
          reasons: ['importantStatement'],
          termEvidence: [
            {
              termEntryId: entityId(600, sequence),
              termEntryVersion: 3,
              writtenForm: 'Azure',
            },
          ],
          automaticAdoptionAllowed: false,
        },
      }
  }
}

function createSource() {
  return {
    sourceSpanId: analysisSourceSpanId,
    range: createRange('1', '2'),
  }
}

function createRange(start: string, duration: string) {
  return {
    start: { value: start, rate: '1' },
    duration: { value: duration, rate: '1' },
  }
}

function entityId(prefix: number, sequence: number): string {
  return `${prefix.toString().padStart(8, '0')}-0000-4000-8000-${sequence
    .toString()
    .padStart(12, '0')}`
}