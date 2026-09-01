import {
  TRANSCRIPT_READ_METHOD,
  type TranscriptMethod,
} from './transcriptProtocol'

export const transcriptRequestId = '00112233-4455-6677-8899-aabbccddeeff'
export const projectId = '10000000-0000-4000-8000-000000000001'
export const speakerId = '10000000-0000-4000-8000-000000000002'
export const performerId = '10000000-0000-4000-8000-000000000003'
export const timedTokenId = '10000000-0000-4000-8000-000000000004'
export const revisionId = '10000000-0000-4000-8000-000000000005'
export const sourceSpanId = '10000000-0000-4000-8000-000000000006'
export const untimedTokenId = '10000000-0000-4000-8000-000000000007'
export const sentenceId = '10000000-0000-4000-8000-000000000008'
export const utteranceId = '10000000-0000-4000-8000-000000000009'
export const termEntryId = '10000000-0000-4000-8000-00000000000b'
export const candidateId = '30000000-0000-4000-8000-000000000001'
export const speakerRevisionId = '40000000-0000-4000-8000-000000000001'
export const revisionDigest = 'a'.repeat(64)

export function createTranscriptResponseValue(
  method: TranscriptMethod = TRANSCRIPT_READ_METHOD,
): MutableTranscriptResponseValue {
  return {
    version: 1,
    requestId: transcriptRequestId,
    method,
    result: {
      status: 'available' as string,
      snapshot: {
        projectId,
        revisionId,
        revisionDigest,
        parentRevisionId: null as string | null,
        changeSource: 'machine',
        createdAt: '2026-01-02T03:04:05.0000000+00:00',
        speakerRevisionId: null as string | null,
        performers: [
          {
            id: performerId,
            displayName: '佐藤 花子',
            role: '司会',
            titleConfirmedAt: '2026-01-01T03:04:05.0000000+00:00' as string | null,
          },
        ],
        utterances: [
          {
            id: utteranceId,
            sourceSpanId,
            text: 'ハブ 未時刻',
            range: {
              start: { value: '0', rate: '1' },
              duration: { value: '2', rate: '1' },
            },
            recognitionConfidence: null as number | null,
            manualTimingAdjustmentRequired: false,
            timingReviewReasons: [] as string[],
            speaker: {
              speakerId,
              performerId: null as string | null,
              format: null as string | null,
              isAssigned: false,
              isVisible: true,
              text: 'Speaker 1' as string | null,
              titleConfirmedAt: null as string | null,
              titleOrAffiliationConfirmationRequired: false,
            },
            sentences: [
              {
                id: sentenceId,
                text: 'ハブ 未時刻',
                tokens: [
                  {
                    id: timedTokenId,
                    text: 'ハブ',
                    range: {
                      start: { value: '0', rate: '1' },
                      duration: { value: '1', rate: '1' },
                    } as MutableRange | null,
                    recognitionConfidence: 0.42 as number | null,
                    manualTimingAdjustmentRequired: false,
                    timingReviewReasons: [] as string[],
                    effectiveSeekSourceTime: { value: '0', rate: '1' },
                    seekUsesUtteranceRange: false,
                  },
                  {
                    id: untimedTokenId,
                    text: '未時刻',
                    range: null as MutableRange | null,
                    recognitionConfidence: null as number | null,
                    manualTimingAdjustmentRequired: true,
                    timingReviewReasons: ['workerReported', 'missingRawTiming'] as string[],
                    effectiveSeekSourceTime: { value: '0', rate: '1' },
                    seekUsesUtteranceRange: true,
                  },
                ],
              },
            ],
          },
        ],
        termCandidates: [
          {
            candidateId,
            sourceSpanId,
            tokenId: timedTokenId,
            termEntryId,
            termEntryVersion: 3,
            dictionaryReading: 'ハブ',
            observedText: 'ハブ',
            proposedText: 'Hub',
            matchKind: 'dictionaryReading',
            confidence: null as number | null,
          },
        ],
        termDecisions: [] as MutableTermDecision[],
        diff: [] as MutableDiff[],
        staleRange: null as MutableStaleRange | null,
        invalidation: {
          staleNodes: [] as string[],
          recomputationNodes: [] as string[],
          manualRebindingNodes: [] as string[],
        },
      },
    } as MutableResult,
  }
}

export interface MutableTranscriptResponseValue {
  version: number
  requestId: string
  method: string
  result: MutableResult
  extra?: boolean
}

interface MutableRational {
  value: string
  rate: string
}

interface MutableRange {
  start: MutableRational
  duration: MutableRational
}

interface MutableTermDecision {
  candidateId: string
  decision: string
  sourceRevisionId: string
  sourceRevisionDigest: string
  tokenId: string
  termEntryId: string
  termEntryVersion: number
  decidedAt: string
  reason: string
  resultingRevisionId: string | null
}

interface MutableDiff {
  kind: string
  utteranceId: string
  sentenceId: string | null
  tokenId: string | null
  previousText: string
  currentText: string
}

interface MutableStaleRange {
  sourceRevisionId: string
  sourceRevisionDigest: string
  revisionId: string
  revisionDigest: string
}

interface MutableResult {
  status: string
  snapshot: MutableSnapshot | null
  actorSid?: string
}


interface MutableSnapshot {
  projectId: string
  revisionId: string
  revisionDigest: string
  parentRevisionId: string | null
  changeSource: string
  createdAt: string
  speakerRevisionId: string | null
  performers: Array<{
    id: string
    displayName: string
    role: string
    titleConfirmedAt: string | null
  }>
  utterances: Array<{
    id: string
    sourceSpanId: string
    text: string
    range: MutableRange
    recognitionConfidence: number | null
    manualTimingAdjustmentRequired: boolean
    timingReviewReasons: string[]
    speaker: {
      speakerId: string
      performerId: string | null
      format: string | null
      isAssigned: boolean
      isVisible: boolean
      text: string | null
      titleConfirmedAt: string | null
      titleOrAffiliationConfirmationRequired: boolean
    }
    sentences: Array<{
      id: string
      text: string
      tokens: Array<{
        id: string
        text: string
        range: MutableRange | null
        recognitionConfidence: number | null
        manualTimingAdjustmentRequired: boolean
        timingReviewReasons: string[]
        effectiveSeekSourceTime: MutableRational
        seekUsesUtteranceRange: boolean
      }>
    }>
  }>
  termCandidates: Array<{
    candidateId: string
    sourceSpanId: string
    tokenId: string
    termEntryId: string
    termEntryVersion: number
    dictionaryReading: string
    observedText: string
    proposedText: string
    matchKind: string
    confidence: number | null
  }>
  termDecisions: MutableTermDecision[]
  diff: MutableDiff[]
  staleRange: MutableStaleRange | null
  invalidation: {
    staleNodes: string[]
    recomputationNodes: string[]
    manualRebindingNodes: string[]
  }
  extra?: boolean
}
