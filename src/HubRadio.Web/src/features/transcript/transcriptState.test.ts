import { describe, expect, it } from 'vitest'

import {
  TRANSCRIPT_EDIT_TOKEN_METHOD,
  TRANSCRIPT_READ_METHOD,
  parseTranscriptResponse,
  type TranscriptMethod,
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
    expect(
      transcriptStateReducer(pending, {
        type: 'responseReceived',
        response: { ...parsed, method: TRANSCRIPT_EDIT_TOKEN_METHOD },
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

  it.each(['staleBase', 'conflict', 'invalid'] as const)(
    'retains the last known snapshot and cancellable edit on recoverable %s',
    (status) => {
      const pending = createPendingEditState()
      const value = createTranscriptResponseValue(TRANSCRIPT_EDIT_TOKEN_METHOD)
      value.result = { status, snapshot: null }
      const response = parseTranscriptResponse(value)
      if (response === null) throw new Error('Expected failure response.')
      const state = transcriptStateReducer(
        pending,
        { type: 'responseReceived', response },
      )
      expect(state.status).toBe(status)
      expect(state.pending).toBeNull()
      expect(state.snapshot).toBe(pending.snapshot)
      expect(state.projectId).toBe(projectId)
      expect(state.edit).toBe(pending.edit)
      expect(state.edit?.revisedText).toBe('保存に失敗しても保持する本文')
      expect(transcriptStateReducer(state, { type: 'editCancelled' }).edit).toBeNull()
    },
  )

  it.each(['forbidden', 'unavailable'] as const)(
    'hides evidence but retains the local edit on %s',
    (status) => {
      const pending = createPendingEditState()
      const value = createTranscriptResponseValue(TRANSCRIPT_EDIT_TOKEN_METHOD)
      value.result = { status, snapshot: null }
      const response = parseTranscriptResponse(value)
      if (response === null) throw new Error('Expected failure response.')

      const state = transcriptStateReducer(pending, { type: 'responseReceived', response })

      expect(state.status).toBe(status)
      expect(state.pending).toBeNull()
      expect(state.snapshot).toBeNull()
      expect(state.edit).toBe(pending.edit)
      expect(transcriptStateReducer(state, { type: 'editCancelled' }).edit).toBeNull()
    },
  )

  it.each([
    { action: { type: 'requestInvalid' }, status: 'invalid' },
    { action: { type: 'bridgeUnavailable' }, status: 'bridgeUnavailable' },
    { action: { type: 'sendFailed', requestId: transcriptRequestId }, status: 'sendFailed' },
  ] as const)('retains the submitted edit and last known snapshot on $action.type', ({ action, status }) => {
    const pending = createPendingEditState()
    const state = transcriptStateReducer(pending, action)
    expect(state.status).toBe(status)
    expect(state.pending).toBeNull()
    expect(state.snapshot).toBe(pending.snapshot)
    expect(state.edit).toBe(pending.edit)
    expect(state.edit?.revisedText).toBe('保存に失敗しても保持する本文')
    expect(transcriptStateReducer(state, { type: 'editCancelled' }).edit).toBeNull()
  })

  it('retains the submitted edit when an available response names a different project', () => {
    const pending = createPendingEditState()
    const value = createTranscriptResponseValue(TRANSCRIPT_EDIT_TOKEN_METHOD)
    value.result.snapshot!.projectId = '90000000-0000-4000-8000-000000000001'
    const response = parseTranscriptResponse(value)
    if (response === null) throw new Error('Expected a valid wrong-project response.')
    const state = transcriptStateReducer(pending, { type: 'responseReceived', response })
    expect(state.status).toBe('invalid')
    expect(state.pending).toBeNull()
    expect(state.snapshot).toBe(pending.snapshot)
    expect(state.projectId).toBe(projectId)
    expect(state.edit).toBe(pending.edit)
    expect(state.edit?.revisedText).toBe('保存に失敗しても保持する本文')
  })

  it('allows a same-base read after failure without treating the read as an edit acknowledgement', () => {
    const pending = createPendingEditState()
    const failed = transcriptStateReducer(pending, {
      type: 'sendFailed', requestId: transcriptRequestId,
    })
    const reading = transcriptStateReducer(failed, {
      type: 'requestStarted', requestId: refreshRequestId, method: TRANSCRIPT_READ_METHOD, projectId,
    })
    const value = createTranscriptResponseValue()
    value.requestId = refreshRequestId
    const response = parseTranscriptResponse(value)
    if (response === null) throw new Error('Expected refresh response.')

    const refreshed = transcriptStateReducer(reading, { type: 'responseReceived', response })

    expect(reading.status).toBe('loading')
    expect(reading.snapshot).toBe(pending.snapshot)
    expect(refreshed.status).toBe('available')
    expect(refreshed.edit).toBe(pending.edit)
    expect(refreshed.edit?.stale).not.toBe(true)
    const retry = transcriptStateReducer(refreshed, {
      type: 'requestStarted', requestId: transcriptRequestId, method: TRANSCRIPT_EDIT_TOKEN_METHOD, projectId,
    })
    expect(retry.pending?.submittedEdit).toBe(pending.edit)
  })

  it.each([
    'revision', 'digest', 'text', 'replacement token', 'missing token',
    'source span', 'utterance', 'sentence', 'change source', 'creation time', 'parent revision',
  ] as const)('keeps a draft stale after refresh changes its %s, until explicit cancellation', (change) => {
    const pending = createPendingEditState()
    const failed = transcriptStateReducer(pending, { type: 'requestInvalid' })
    const reading = transcriptStateReducer(failed, {
      type: 'requestStarted', requestId: refreshRequestId, method: TRANSCRIPT_READ_METHOD, projectId,
    })
    const value = createTranscriptResponseValue()
    value.requestId = refreshRequestId
    const snapshot = value.result.snapshot!
    const utterance = snapshot.utterances[0]!
    const sentence = utterance.sentences[0]!
    const token = sentence.tokens[0]!
    snapshot.termCandidates = []
    const replacementId = '80000000-0000-4000-8000-000000000001'
    switch (change) {
      case 'revision': snapshot.revisionId = replacementId; break
      case 'digest': snapshot.revisionDigest = 'b'.repeat(64); break
      case 'text':
        token.text = '別の本文'
        sentence.text = utterance.text = '別の本文 未時刻'
        break
      case 'replacement token': token.id = replacementId; break
      case 'missing token':
        sentence.tokens.shift()
        sentence.text = utterance.text = '未時刻'
        break
      case 'source span': utterance.sourceSpanId = replacementId; break
      case 'utterance': utterance.id = replacementId; break
      case 'sentence': sentence.id = replacementId; break
      case 'change source': snapshot.changeSource = 'human'; break
      case 'creation time': snapshot.createdAt = '2026-01-03T03:04:05.0000000+00:00'; break
      case 'parent revision': snapshot.parentRevisionId = replacementId; break
    }
    const response = parseTranscriptResponse(value)
    if (response === null) throw new Error('Expected changed-base refresh response.')

    const refreshed = transcriptStateReducer(reading, { type: 'responseReceived', response })

    expect(refreshed.status).toBe('available')
    expect(refreshed.edit).toEqual({ ...pending.edit, stale: true })
    const rereading = transcriptStateReducer(refreshed, {
      type: 'requestStarted', requestId: refreshRequestId, method: TRANSCRIPT_READ_METHOD, projectId,
    })
    const reread = transcriptStateReducer(rereading, { type: 'responseReceived', response })
    expect(reread.edit?.stale).toBe(true)
    expect(reread.edit?.expectedText).toBe('ハブ')
    expect(reread.edit?.revisedText).toBe('保存に失敗しても保持する本文')
    expect(transcriptStateReducer(reread, { type: 'editChanged', value: '上書きしない' })).toBe(reread)
    expect(transcriptStateReducer(reread, {
      type: 'requestStarted', requestId: transcriptRequestId, method: TRANSCRIPT_EDIT_TOKEN_METHOD, projectId,
    })).toBe(reread)
    if (response.result.status !== 'available') throw new Error('Expected snapshot.')
    const currentToken = response.result.snapshot.utterances[0]!.sentences[0]!.tokens[0]!
    expect(transcriptStateReducer(reread, { type: 'editStarted', token: currentToken })).toBe(reread)
    const cancelled = transcriptStateReducer(reread, { type: 'editCancelled' })
    const newEdit = transcriptStateReducer(cancelled, { type: 'editStarted', token: currentToken })
    expect(newEdit.edit).toEqual({
      tokenId: currentToken.id, expectedText: currentToken.text, revisedText: currentToken.text,
    })
  })

  it.each(['forbidden', 'unavailable'] as const)(
    'does not infer a draft base after evidence was hidden by %s',
    (status) => {
      const pending = createPendingEditState()
      const failureValue = createTranscriptResponseValue(TRANSCRIPT_EDIT_TOKEN_METHOD)
      failureValue.result = { status, snapshot: null }
      const failure = parseTranscriptResponse(failureValue)
      if (failure === null) throw new Error('Expected failure response.')
      const failed = transcriptStateReducer(pending, { type: 'responseReceived', response: failure })
      const reading = transcriptStateReducer(failed, {
        type: 'requestStarted', requestId: refreshRequestId, method: TRANSCRIPT_READ_METHOD, projectId,
      })
      const value = createTranscriptResponseValue()
      value.requestId = refreshRequestId
      const response = parseTranscriptResponse(value)
      if (response === null) throw new Error('Expected read response.')

      const refreshed = transcriptStateReducer(reading, { type: 'responseReceived', response })

      expect(refreshed.edit).toEqual({ ...pending.edit, stale: true })
      expect(refreshed.status).toBe('available')
    },
  )

  it('does not unlock a retained snapshot with a noChange read or an interrupted request', () => {
    const pending = createPendingEditState()
    const interrupted = transcriptStateReducer(pending, { type: 'sessionStarted' })
    expect(interrupted.edit).toBe(pending.edit)
    expect(interrupted.snapshot).toBe(pending.snapshot)
    expect(interrupted.status).toBe('sendFailed')
    const reading = transcriptStateReducer(interrupted, {
      type: 'requestStarted', requestId: refreshRequestId, method: TRANSCRIPT_READ_METHOD, projectId,
    })
    const value = createTranscriptResponseValue()
    value.requestId = refreshRequestId
    value.result = { status: 'noChange', snapshot: null }
    const response = parseTranscriptResponse(value)
    if (response === null) throw new Error('Expected no-change read response.')
    const state = transcriptStateReducer(reading, { type: 'responseReceived', response })
    expect(state.status).toBe('invalid')
    expect(state.snapshot).toBe(pending.snapshot)
    expect(state.edit).toBe(pending.edit)
    expect(state.pending).toBeNull()
    for (const method of [
      'transcript.editToken', 'transcript.setSpeaker', 'transcript.removeSpeaker',
      'transcript.adoptTerm', 'transcript.rejectTerm',
    ] as const) {
      expect(transcriptStateReducer(state, {
        type: 'requestStarted', requestId: transcriptRequestId, method, projectId,
      })).toBe(state)
    }
    expect(transcriptStateReducer(state, {
      type: 'editStarted', token: pending.snapshot!.utterances[0]!.sentences[0]!.tokens[1]!,
    })).toBe(state)
  })

  it('retains the canonical snapshot but closes editing on an explicit no-op', () => {
    const read = parseTranscriptResponse(createTranscriptResponseValue())
    if (read?.result.status !== 'available') throw new Error('Expected fixture.')
    const noChangeValue = createTranscriptResponseValue(TRANSCRIPT_EDIT_TOKEN_METHOD)
    noChangeValue.result = { status: 'noChange', snapshot: null }
    const response = parseTranscriptResponse(noChangeValue)
    if (response === null) throw new Error('Expected no-change response.')
    const token = read.result.snapshot.utterances[0]!.sentences[0]!.tokens[0]!
    const pending = transcriptStateReducer(
      {
        ...initialTranscriptState,
        projectId,
        status: 'available',
        snapshot: read.result.snapshot,
        edit: { tokenId: token.id, expectedText: token.text, revisedText: token.text },
      },
      {
        type: 'requestStarted',
        requestId: transcriptRequestId,
        method: TRANSCRIPT_EDIT_TOKEN_METHOD,
        projectId,
      },
    )
    const state = transcriptStateReducer(pending, { type: 'responseReceived', response })
    expect(state.status).toBe('noChange')
    expect(state.snapshot).toBe(read.result.snapshot)
    expect(state.edit).toBeNull()
  })

  it.each(
    (['transcript.setSpeaker', 'transcript.removeSpeaker', 'transcript.adoptTerm', 'transcript.rejectTerm'] as const)
      .flatMap((method) => (['available', 'noChange'] as const).map((status) => ({ method, status }))),
  )('retains an independent token draft after $method $status', ({ method, status }) => {
    const pending = createPendingEditState(method)
    const value = createTranscriptResponseValue(method)
    if (status === 'noChange') value.result = { status, snapshot: null }
    const response = parseTranscriptResponse(value)
    if (response === null) throw new Error('Expected mutation response.')

    const state = transcriptStateReducer(pending, { type: 'responseReceived', response })

    expect(state.status).toBe(status)
    expect(state.pending).toBeNull()
    expect(state.projectId).toBe(projectId)
    expect(state.snapshot).not.toBeNull()
    expect(state.edit).toBe(pending.edit)
    expect(state.edit?.revisedText).toBe('保存に失敗しても保持する本文')
    expect(transcriptStateReducer(state, { type: 'editCancelled' }).edit).toBeNull()
  })

  it.each(['available', 'noChange'] as const)(
    'does not clear a token draft changed while awaiting its %s acknowledgement',
    (status) => {
      const pending = createPendingEditState()
      const changed = transcriptStateReducer(pending, {
        type: 'editChanged', value: '送信後に変えた未保存本文',
      })
      const value = createTranscriptResponseValue(TRANSCRIPT_EDIT_TOKEN_METHOD)
      if (status === 'noChange') value.result = { status, snapshot: null }
      const response = parseTranscriptResponse(value)
      if (response === null) throw new Error('Expected edit response.')

      const state = transcriptStateReducer(changed, { type: 'responseReceived', response })

      expect(pending.pending?.submittedEdit).toBe(pending.edit)
      expect(state.status).toBe(status)
      expect(state.pending).toBeNull()
      expect(state.edit).toBe(changed.edit)
      expect(state.edit?.revisedText).toBe('送信後に変えた未保存本文')
    },
  )
})

const refreshRequestId = '11111111-1111-4111-8111-111111111111'

function createPendingEditState(method: TranscriptMethod = TRANSCRIPT_EDIT_TOKEN_METHOD): TranscriptState {
  const read = parseTranscriptResponse(createTranscriptResponseValue())
  if (read?.result.status !== 'available') throw new Error('Expected available fixture.')
  const token = read.result.snapshot.utterances[0]!.sentences[0]!.tokens[0]!
  return transcriptStateReducer(
    {
      ...initialTranscriptState,
      projectId,
      status: 'available',
      snapshot: read.result.snapshot,
      edit: { tokenId: token.id, expectedText: token.text, revisedText: '保存に失敗しても保持する本文' },
    },
    { type: 'requestStarted', requestId: transcriptRequestId, method, projectId },
  )
}
