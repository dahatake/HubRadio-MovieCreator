import { describe, expect, it } from 'vitest'

import {
  TRANSCRIPT_EDIT_TOKEN_METHOD,
  TRANSCRIPT_READ_METHOD,
  parseTranscriptResponse,
} from './transcriptProtocol'
import {
  initialTranscriptState,
  transcriptStateReducer,
  type TranscriptState,
} from './transcriptState'
import {
  createTranscriptResponseValue,
  projectId,
  timedTokenId,
  transcriptRequestId,
} from './transcriptTestFixture'

describe('transcript state', () => {
  it('accepts only the pending method/request/project response', () => {
    const pending = transcriptStateReducer(
      transcriptStateReducer(initialTranscriptState, {
        type: 'projectChanged',
        projectId,
      }),
      {
        type: 'requestStarted',
        requestId: transcriptRequestId,
        method: TRANSCRIPT_READ_METHOD,
        projectId,
      },
    )
    const parsed = parseTranscriptResponse(createTranscriptResponseValue())
    if (parsed === null) throw new Error('Expected valid response fixture.')

    expect(
      transcriptStateReducer(pending, {
        type: 'responseReceived',
        response: { ...parsed, requestId: '11111111-1111-4111-8111-111111111111' },
      }),
    ).toBe(pending)
    const accepted = transcriptStateReducer(pending, {
      type: 'responseReceived',
      response: parsed,
    })
    expect(accepted.status).toBe('available')
    expect(accepted.snapshot?.projectId).toBe(projectId)
    expect(accepted.pending).toBeNull()
  })

  it('uses the native result as the only optimistic-concurrency commit', () => {
    const availableValue = createTranscriptResponseValue()
    availableValue.method = TRANSCRIPT_EDIT_TOKEN_METHOD
    const available = parseTranscriptResponse(availableValue)
    if (available?.result.status !== 'available') {
      throw new Error('Expected available edit fixture.')
    }
    let state: TranscriptState = {
      ...initialTranscriptState,
      projectId,
      status: 'available' as const,
      snapshot: available.result.snapshot,
    }
    const token = available.result.snapshot.utterances[0]!.sentences[0]!.tokens[0]!
    state = transcriptStateReducer(state, { type: 'editStarted', token })
    state = transcriptStateReducer(state, { type: 'editChanged', value: 'Hub' })
    expect(state.snapshot?.utterances[0]!.sentences[0]!.tokens[0]!.text).toBe('ハブ')

    state = transcriptStateReducer(state, {
      type: 'requestStarted',
      requestId: transcriptRequestId,
      method: TRANSCRIPT_EDIT_TOKEN_METHOD,
      projectId,
    })
    expect(state.snapshot?.utterances[0]!.sentences[0]!.tokens[0]!.text).toBe('ハブ')

    const nextValue = createTranscriptResponseValue(TRANSCRIPT_EDIT_TOKEN_METHOD)
    nextValue.result.snapshot!.utterances[0]!.sentences[0]!.tokens[0]!.text = 'Hub'
    nextValue.result.snapshot!.utterances[0]!.sentences[0]!.text = 'Hub 未時刻'
    nextValue.result.snapshot!.utterances[0]!.text = 'Hub 未時刻'
    nextValue.result.snapshot!.termCandidates = []
    nextValue.result.snapshot!.parentRevisionId = nextValue.result.snapshot!.revisionId
    nextValue.result.snapshot!.revisionId = '20000000-0000-4000-8000-000000000001'
    nextValue.result.snapshot!.revisionDigest = 'b'.repeat(64)
    nextValue.result.snapshot!.changeSource = 'human'
    nextValue.result.snapshot!.speakerRevisionId = null
    nextValue.result.snapshot!.diff = [
      {
        kind: 'utterance',
        utteranceId: nextValue.result.snapshot!.utterances[0]!.id,
        sentenceId: null,
        tokenId: null,
        previousText: 'ハブ 未時刻',
        currentText: 'Hub 未時刻',
      },
      {
        kind: 'sentence',
        utteranceId: nextValue.result.snapshot!.utterances[0]!.id,
        sentenceId: nextValue.result.snapshot!.utterances[0]!.sentences[0]!.id,
        tokenId: null,
        previousText: 'ハブ 未時刻',
        currentText: 'Hub 未時刻',
      },
      {
        kind: 'token',
        utteranceId: nextValue.result.snapshot!.utterances[0]!.id,
        sentenceId: nextValue.result.snapshot!.utterances[0]!.sentences[0]!.id,
        tokenId: timedTokenId,
        previousText: 'ハブ',
        currentText: 'Hub',
      },
    ]
    nextValue.result.snapshot!.staleRange = {
      sourceRevisionId: nextValue.result.snapshot!.parentRevisionId,
      sourceRevisionDigest: 'a'.repeat(64),
      revisionId: nextValue.result.snapshot!.revisionId,
      revisionDigest: nextValue.result.snapshot!.revisionDigest,
    }
    nextValue.result.snapshot!.invalidation = {
      staleNodes: [
        'termCandidates',
        'editorialPhraseCandidates',
        'repetitionCandidates',
        'topicBoundaryCandidates',
        'topicRangeBinding',
        'contextDeviationCandidates',
        'speakerAssignmentBinding',
        'topicMetadata',
        'emphasisCandidates',
      ],
      recomputationNodes: [
        'termCandidates',
        'editorialPhraseCandidates',
        'repetitionCandidates',
        'topicBoundaryCandidates',
        'contextDeviationCandidates',
        'topicMetadata',
        'emphasisCandidates',
      ],
      manualRebindingNodes: ['topicRangeBinding', 'speakerAssignmentBinding'],
    }
    const next = parseTranscriptResponse(nextValue)
    if (next === null) throw new Error('Expected valid edited response.')

    state = transcriptStateReducer(state, { type: 'responseReceived', response: next })
    expect(state.status).toBe('available')
    expect(state.snapshot?.utterances[0]!.sentences[0]!.tokens[0]!.text).toBe('Hub')
    expect(state.edit).toBeNull()
  })

  it.each(['staleBase', 'conflict', 'forbidden', 'invalid'] as const)(
    'closes stale evidence on %s',
    (status) => {
      const read = parseTranscriptResponse(createTranscriptResponseValue())
      if (read?.result.status !== 'available') throw new Error('Expected fixture.')
      const value = createTranscriptResponseValue(TRANSCRIPT_EDIT_TOKEN_METHOD)
      value.result = { status, snapshot: null }
      const response = parseTranscriptResponse(value)
      if (response === null) throw new Error('Expected failure response.')
      const state = transcriptStateReducer(
        {
          ...initialTranscriptState,
          projectId,
          status: 'available',
          snapshot: read.result.snapshot,
          pending: {
            requestId: transcriptRequestId,
            method: TRANSCRIPT_EDIT_TOKEN_METHOD,
            projectId,
          },
        },
        { type: 'responseReceived', response },
      )
      expect(state.status).toBe(status)
      expect(state.snapshot).toBeNull()
    },
  )

  it('retains the canonical snapshot but closes editing on an explicit no-op', () => {
    const read = parseTranscriptResponse(createTranscriptResponseValue())
    if (read?.result.status !== 'available') throw new Error('Expected fixture.')
    const noChangeValue = createTranscriptResponseValue(TRANSCRIPT_EDIT_TOKEN_METHOD)
    noChangeValue.result = { status: 'noChange', snapshot: null }
    const response = parseTranscriptResponse(noChangeValue)
    if (response === null) throw new Error('Expected no-change response.')
    const token = read.result.snapshot.utterances[0]!.sentences[0]!.tokens[0]!
    const state = transcriptStateReducer(
      {
        ...initialTranscriptState,
        projectId,
        status: 'available',
        snapshot: read.result.snapshot,
        edit: { tokenId: token.id, expectedText: token.text, revisedText: token.text },
        pending: {
          requestId: transcriptRequestId,
          method: TRANSCRIPT_EDIT_TOKEN_METHOD,
          projectId,
        },
      },
      { type: 'responseReceived', response },
    )
    expect(state.status).toBe('noChange')
    expect(state.snapshot).toBe(read.result.snapshot)
    expect(state.edit).toBeNull()
  })
})
