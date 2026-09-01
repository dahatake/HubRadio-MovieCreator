import { describe, expect, it } from 'vitest'

import {
  TRANSCRIPT_ADOPT_TERM_METHOD,
  TRANSCRIPT_EDIT_TOKEN_METHOD,
  TRANSCRIPT_READ_METHOD,
  TRANSCRIPT_REMOVE_SPEAKER_METHOD,
  TRANSCRIPT_REJECT_TERM_METHOD,
  TRANSCRIPT_SET_SPEAKER_METHOD,
  createTranscriptAdoptTermRequest,
  createTranscriptEditTokenRequest,
  createTranscriptReadRequest,
  createTranscriptRejectTermRequest,
  createTranscriptRemoveSpeakerRequest,
  createTranscriptSetSpeakerRequest,
  parseTranscriptResponse,
} from './transcriptProtocol'
import {
  candidateId,
  performerId,
  projectId,
  revisionDigest,
  revisionId,
  speakerId,
  speakerRevisionId,
  termEntryId,
  timedTokenId,
  transcriptRequestId,
  untimedTokenId,
  utteranceId,
  createTranscriptResponseValue,
} from './transcriptTestFixture'

describe('transcript bridge protocol', () => {
  it('creates bounded closed requests with operation-specific OCC', () => {
    const read = createTranscriptReadRequest(transcriptRequestId, projectId)
    const edit = createTranscriptEditTokenRequest(transcriptRequestId, {
      projectId,
      revisionId,
      digest: revisionDigest,
      tokenId: timedTokenId,
      expectedText: 'ハブ',
      revisedText: 'Hub',
      reason: '認識結果を修正',
    })
    const setSpeaker = createTranscriptSetSpeakerRequest(transcriptRequestId, {
      projectId,
      revisionId,
      digest: revisionDigest,
      expectedSpeakerRevisionId: null,
      speakerId,
      performerId,
      surname: '佐藤',
      displayFormat: 'surname',
      reason: '出演者を確認',
    })
    const removeSpeaker = createTranscriptRemoveSpeakerRequest(
      transcriptRequestId,
      {
        projectId,
        revisionId,
        digest: revisionDigest,
        expectedSpeakerRevisionId: speakerRevisionId,
        speakerId,
        reason: '割当を解除',
      },
    )
    const decision = {
      projectId,
      revisionId,
      digest: revisionDigest,
      candidateId,
      termEntryId,
      termEntryVersion: 3,
      reason: '候補を判断',
    } as const
    const adopt = createTranscriptAdoptTermRequest(transcriptRequestId, decision)
    const reject = createTranscriptRejectTermRequest(transcriptRequestId, decision)

    expect(read).toEqual({
      version: 1,
      requestId: transcriptRequestId,
      method: TRANSCRIPT_READ_METHOD,
      params: { projectId },
    })
    expect(edit.method).toBe(TRANSCRIPT_EDIT_TOKEN_METHOD)
    expect(setSpeaker.method).toBe(TRANSCRIPT_SET_SPEAKER_METHOD)
    expect(removeSpeaker.method).toBe(TRANSCRIPT_REMOVE_SPEAKER_METHOD)
    expect(adopt.method).toBe(TRANSCRIPT_ADOPT_TERM_METHOD)
    expect(reject.method).toBe(TRANSCRIPT_REJECT_TERM_METHOD)
    expect(Object.keys(edit.params)).toEqual([
      'projectId',
      'revisionId',
      'digest',
      'tokenId',
      'expectedText',
      'revisedText',
      'reason',
    ])
    expect(Object.isFrozen(edit)).toBe(true)
    expect(Object.isFrozen(edit.params)).toBe(true)
    expect(() =>
      createTranscriptEditTokenRequest(transcriptRequestId, {
        ...edit.params,
        revisedText: 'x'.repeat(17_000),
      }),
    ).toThrow(/byte limit/)
  })

  it.each([
    ['uppercase request ID', transcriptRequestId.toUpperCase(), projectId],
    ['empty project ID', transcriptRequestId, '00000000-0000-0000-0000-000000000000'],
    [
      'uppercase project ID',
      transcriptRequestId,
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'.toUpperCase(),
    ],
  ])('rejects noncanonical request identity: %s', (_name, request, project) => {
    expect(() => createTranscriptReadRequest(request, project)).toThrow()
  })

  it('parses and deeply freezes exact review evidence without inventing a confidence class', () => {
    const parsed = parseTranscriptResponse(createTranscriptResponseValue())

    expect(parsed?.method).toBe(TRANSCRIPT_READ_METHOD)
    expect(parsed?.result.status).toBe('available')
    if (parsed?.result.status !== 'available') {
      throw new Error('Expected an available transcript response.')
    }

    const snapshot = parsed.result.snapshot
    const tokens = snapshot.utterances[0]!.sentences[0]!.tokens
    expect(tokens[0]!.recognitionConfidence).toBe(0.42)
    expect(tokens[1]!.recognitionConfidence).toBeNull()
    expect(tokens[1]!.range).toBeNull()
    expect(tokens[1]!.effectiveSeekSourceTime).toEqual({ value: '0', rate: '1' })
    expect(tokens[1]!.seekUsesUtteranceRange).toBe(true)
    expect(tokens[1]!.timingReviewReasons).toEqual([
      'workerReported',
      'missingRawTiming',
    ])
    expect(snapshot.termCandidates[0]!.confidence).toBeNull()
    expect(snapshot.staleRange).toBeNull()
    expect(Object.keys(tokens[0]!)).not.toContain('confidenceClass')
    expect(Object.isFrozen(snapshot)).toBe(true)
    expect(Object.isFrozen(snapshot.utterances)).toBe(true)
    expect(Object.isFrozen(tokens[0])).toBe(true)
  })

  it('accepts every transcript method and requires status/payload agreement', () => {
    for (const method of [
      TRANSCRIPT_READ_METHOD,
      TRANSCRIPT_EDIT_TOKEN_METHOD,
      TRANSCRIPT_SET_SPEAKER_METHOD,
      TRANSCRIPT_REMOVE_SPEAKER_METHOD,
      TRANSCRIPT_ADOPT_TERM_METHOD,
      TRANSCRIPT_REJECT_TERM_METHOD,
    ] as const) {
      const available = createTranscriptResponseValue(method)
      expect(parseTranscriptResponse(available)?.result.status).toBe('available')

      for (const status of [
        'forbidden',
        'unavailable',
        'staleBase',
        'conflict',
        'noChange',
        'invalid',
      ] as const) {
        available.result = { status, snapshot: null }
        expect(parseTranscriptResponse(available)?.result.status).toBe(status)
      }
    }

    const mismatched = createTranscriptResponseValue()
    mismatched.result = { status: 'available', snapshot: null }
    expect(parseTranscriptResponse(mismatched)).toBeNull()

    const leaked = createTranscriptResponseValue()
    ;(leaked.result as unknown as Record<string, unknown>).actorSid = 'S-1-5-18'
    expect(parseTranscriptResponse(leaked)).toBeNull()
  })

  it('rejects unknown fields and broken hierarchy, timing, speaker, candidate, diff, or invalidation bindings', () => {
    const mutations: Array<(value: ReturnType<typeof createTranscriptResponseValue>) => void> = [
      (value) => {
        value.extra = true
      },
      (value) => {
        const snapshot = availableSnapshot(value)
        snapshot.extra = true
      },
      (value) => {
        availableSnapshot(value).utterances[0]!.sentences[0]!.tokens[1]!.id = timedTokenId
      },
      (value) => {
        availableSnapshot(value).utterances[0]!.sentences[0]!.tokens[1]!.effectiveSeekSourceTime = {
          value: '1',
          rate: '1',
        }
      },
      (value) => {
        availableSnapshot(value).utterances[0]!.sentences[0]!.tokens[1]!.seekUsesUtteranceRange = false
      },
      (value) => {
        availableSnapshot(value).utterances[0]!.sentences[0]!.tokens[0]!.range = {
          start: { value: '3', rate: '1' },
          duration: { value: '1', rate: '1' },
        }
      },
      (value) => {
        availableSnapshot(value).utterances[0]!.speaker.performerId = projectId
      },
      (value) => {
        availableSnapshot(value).termCandidates[0]!.tokenId = untimedTokenId
      },
      (value) => {
        availableSnapshot(value).termCandidates[0]!.observedText = '別表記'
      },
      (value) => {
        availableSnapshot(value).termDecisions.push({
          candidateId,
          decision: 'rejected',
          sourceRevisionId: revisionId,
          sourceRevisionDigest: revisionDigest,
          tokenId: timedTokenId,
          termEntryId,
          termEntryVersion: 3,
          decidedAt: '2026-01-02T03:05:05.0000000+00:00',
          reason: '重複判断',
          resultingRevisionId: null,
        })
      },
      (value) => {
        availableSnapshot(value).diff.push({
          kind: 'token',
          utteranceId,
          sentenceId: null,
          tokenId: timedTokenId,
          previousText: '旧表記',
          currentText: 'ハブ',
        })
      },
      (value) => {
        availableSnapshot(value).staleRange = {
          sourceRevisionId: revisionId,
          sourceRevisionDigest: revisionDigest,
          revisionId: '20000000-0000-4000-8000-000000000001',
          revisionDigest: 'b'.repeat(64),
        }
      },
      (value) => {
        availableSnapshot(value).invalidation.staleNodes = [
          'emphasisCandidates',
          'termCandidates',
        ]
      },
      (value) => {
        availableSnapshot(value).utterances[0]!.sentences[0]!.tokens[0]!.recognitionConfidence = 1.1
      },
      (value) => {
        availableSnapshot(value).utterances[0]!.range.duration = { value: '0', rate: '1' }
      },
      (value) => {
        availableSnapshot(value).createdAt = '2026-02-30T03:04:05.0000000+00:00'
      },
    ]

    for (const mutate of mutations) {
      const value = createTranscriptResponseValue()
      mutate(value)
      expect(parseTranscriptResponse(value)).toBeNull()
    }
  })

  it('rejects noncanonical rational values, timestamps, IDs, and digests', () => {
    const values = [
      createTranscriptResponseValue(),
      createTranscriptResponseValue(),
      createTranscriptResponseValue(),
      createTranscriptResponseValue(),
    ]
    availableSnapshot(values[0]!).revisionId =
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'.toUpperCase()
    availableSnapshot(values[1]!).revisionDigest = `${revisionDigest}\n`
    availableSnapshot(values[2]!).utterances[0]!.range.start = {
      value: '2',
      rate: '2',
    }
    availableSnapshot(values[3]!).createdAt = '2026-01-02T03:04:05.000Z'

    for (const value of values) {
      expect(parseTranscriptResponse(value)).toBeNull()
    }
  })
})

function availableSnapshot(value: ReturnType<typeof createTranscriptResponseValue>) {
  if (value.result.status !== 'available' || value.result.snapshot === null) {
    throw new Error('Expected an available mutable fixture.')
  }
  return value.result.snapshot
}
