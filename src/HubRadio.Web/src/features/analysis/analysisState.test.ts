import { describe, expect, it } from 'vitest'

import {
  ANALYSIS_ADOPT_METHOD,
  ANALYSIS_READ_METHOD,
  parseAnalysisResponse,
  type AnalysisResponse,
  type AnalysisSnapshot,
} from './analysisProtocol'
import {
  analysisMetadataRevisionId,
  analysisProjectId,
  analysisRequestId,
  analysisTopicId,
  createAnalysisResponseValue,
} from './analysisTestFixture'
import {
  analysisStateReducer,
  analysisTopicInputIsCurrent,
  initialAnalysisState,
  isAnalysisDirty,
  type AnalysisState,
  type AnalysisTopicField,
} from './analysisState'

describe('analysisStateReducer', () => {
  it('accepts only the pending correlated response for the selected project', () => {
    const started = analysisStateReducer(
      { ...initialAnalysisState, projectId: analysisProjectId },
      {
        type: 'requestStarted',
        requestId: analysisRequestId,
        method: ANALYSIS_READ_METHOD,
        projectId: analysisProjectId,
      },
    )
    const wrongMethod = createAnalysisResponseValue(ANALYSIS_ADOPT_METHOD)
    expect(analysisStateReducer(started, {
      type: 'responseReceived',
      response: requireResponse(wrongMethod),
    })).toBe(started)

    const available = analysisStateReducer(started, {
      type: 'responseReceived',
      response: requireResponse(createAnalysisResponseValue()),
    })
    expect(available.status).toBe('available')
    expect(available.snapshot?.projectId).toBe(analysisProjectId)
    expect(available.pending).toBeNull()
  })

  it('rejects a cross-project snapshot even with a correlated request', () => {
    const started = startRead()
    const value = createAnalysisResponseValue()
    const snapshot = value.result.snapshot
    if (snapshot === null) throw new Error('Expected an analysis snapshot.')
    value.result.snapshot = {
      ...snapshot,
      projectId: '20000000-0000-4000-8000-000000000001',
    }

    const state = analysisStateReducer(started, {
      type: 'responseReceived',
      response: requireResponse(value),
    })

    expect(state.status).toBe('invalid')
    expect(state.snapshot).toBeNull()
  })

  it('keeps the current snapshot for noChange but drops it after staleBase', () => {
    const available = analysisStateReducer(startRead(), {
      type: 'responseReceived',
      response: requireResponse(createAnalysisResponseValue()),
    })
    const mutationStarted = analysisStateReducer(available, {
      type: 'requestStarted',
      requestId: '00112233-4455-6677-8899-aabbccddeef0',
      method: ANALYSIS_ADOPT_METHOD,
      projectId: analysisProjectId,
    })

    const noChangeValue = createAnalysisResponseValue(ANALYSIS_ADOPT_METHOD)
    noChangeValue.requestId = '00112233-4455-6677-8899-aabbccddeef0'
    noChangeValue.result = { status: 'noChange', snapshot: null }
    const noChange = analysisStateReducer(mutationStarted, {
      type: 'responseReceived',
      response: requireResponse(noChangeValue),
    })
    expect(noChange.status).toBe('noChange')
    expect(noChange.snapshot).toBe(available.snapshot)

    const retryStarted = analysisStateReducer(noChange, {
      type: 'requestStarted',
      requestId: '00112233-4455-6677-8899-aabbccddeef1',
      method: ANALYSIS_ADOPT_METHOD,
      projectId: analysisProjectId,
    })
    const staleValue = createAnalysisResponseValue(ANALYSIS_ADOPT_METHOD)
    staleValue.requestId = '00112233-4455-6677-8899-aabbccddeef1'
    staleValue.result = { status: 'staleBase', snapshot: null }
    const stale = analysisStateReducer(retryStarted, {
      type: 'responseReceived',
      response: requireResponse(staleValue),
    })
    expect(stale.status).toBe('staleBase')
    expect(stale.snapshot).toBeNull()
  })

  it('counts exact local inputs, not requests or ACKed native staging, as dirty', () => {
    expect(isAnalysisDirty(initialAnalysisState)).toBe(false)
    expect(isAnalysisDirty(startRead())).toBe(false)
    const snapshot = titleAcknowledged(snapshotFixture())
    const available = receive(startRead(), 'available', snapshot)
    expect(available.snapshot?.adoptionPersistence).toBe('notPersisted')
    expect(available.snapshot?.stagedChanges).toHaveLength(1)
    expect(isAnalysisDirty(available)).toBe(false)

    const reason = analysisStateReducer(available, { type: 'reasonInputChanged', value: ' \n ' })
    expect(isAnalysisDirty(reason)).toBe(true)
    const noReason = analysisStateReducer(reason, { type: 'reasonInputChanged', value: '' })
    const raw = changeTopic(noReason, 'keywords', ' Azure \nAzure\n\n')
    expect(raw.topicInputs[analysisTopicId]?.keywords?.value).toBe(' Azure \nAzure\n\n')
    expect(isAnalysisDirty(raw)).toBe(true)
    const reverted = changeTopic(raw, 'keywords', 'Azure')
    expect(reverted.topicInputs).toEqual({})
    expect(isAnalysisDirty(reverted)).toBe(false)
    expect(reverted.snapshot).toBe(available.snapshot)
  })

  it.each(['available', 'noChange'] as const)(
    'clears only the captured topic field and current reason on %s, preserving other exact bases',
    (status) => {
      const snapshot = snapshotFixture(true)
      let state = receive(startRead(), 'available', snapshot)
      state = changeTopic(state, 'title', '送信するタイトル')
      state = changeTopic(state, 'summary', '  別項目の原文\n\n')
      state = changeTopic(state, 'keywords', ' 別トピック \n重複\n重複\n', secondTopicId)
      state = changeTopic(state, 'mainSpeaker', '', secondTopicId)
      state = analysisStateReducer(state, { type: 'reasonInputChanged', value: '送信理由' })
      const otherSummary = state.topicInputs[analysisTopicId]?.summary
      const otherTopic = state.topicInputs[secondTopicId]
      const pending = startTopicWrite(state)

      const accepted = receive(pending, status, titleAcknowledged(snapshot))

      expect(accepted.topicInputs[analysisTopicId]?.title).toBeUndefined()
      expect(accepted.topicInputs[analysisTopicId]?.summary).toBe(otherSummary)
      expect(accepted.topicInputs[secondTopicId]).toBe(otherTopic)
      expect(accepted.reasonInput.value).toBe('')
      expect(accepted.pending).toBeNull()
      expect(isAnalysisDirty(accepted)).toBe(true)
      expect(otherSummary?.base).toEqual({
        projectId: snapshot.projectId,
        revisionId: snapshot.revisionId,
        revisionDigest: snapshot.revisionDigest,
        metadataRevisionId: analysisMetadataRevisionId,
        value: snapshot.topics[0]!.summary,
      })
      if (otherSummary === undefined || accepted.snapshot === null) throw new Error('Expected retained input.')
      expect(analysisTopicInputIsCurrent(
        accepted.snapshot, accepted.snapshot.topics[0]!, 'summary', otherSummary,
      )).toBe(status === 'noChange')
    },
  )

  it.each(['available', 'noChange'] as const)('does not erase A → B → A inputs on %s', (status) => {
    let state = receive(startRead(), 'available', snapshotFixture())
    state = changeTopic(state, 'title', '送信するタイトル')
    state = analysisStateReducer(state, { type: 'reasonInputChanged', value: '送信理由' })
    state = startTopicWrite(state)
    const captured = state.pending
    state = changeTopic(state, 'title', '中間の入力')
    state = changeTopic(state, 'title', '送信するタイトル')
    state = analysisStateReducer(state, { type: 'reasonInputChanged', value: '別の理由' })
    state = analysisStateReducer(state, { type: 'reasonInputChanged', value: '送信理由' })
    const laterTitle = state.topicInputs[analysisTopicId]?.title
    const laterReason = state.reasonInput
    expect(laterTitle).not.toBe(captured?.sentTopicInput?.input)
    expect(laterReason).not.toBe(captured?.sentReason)

    const accepted = receive(state, status, titleAcknowledged(snapshotFixture()))

    expect(accepted.topicInputs[analysisTopicId]?.title).toBe(laterTitle)
    expect(accepted.reasonInput).toBe(laterReason)
    expect(accepted.reasonInput.value).toBe('送信理由')
    expect(isAnalysisDirty(accepted)).toBe(true)
  })

  it.each(['topic', 'reason'] as const)('independently preserves a later %s input on ACK', (changed) => {
    let state = changeTopic(receive(startRead(), 'available', snapshotFixture()), 'title', '送信するタイトル')
    state = analysisStateReducer(state, { type: 'reasonInputChanged', value: '送信理由' })
    state = startTopicWrite(state)
    state = changed === 'topic'
      ? changeTopic(state, 'title', '後から入力')
      : analysisStateReducer(state, { type: 'reasonInputChanged', value: '後から入力' })

    const accepted = receive(state, 'available', titleAcknowledged(snapshotFixture()))

    expect(accepted.topicInputs[analysisTopicId]?.title?.value)
      .toBe(changed === 'topic' ? '後から入力' : undefined)
    expect(accepted.reasonInput.value).toBe(changed === 'reason' ? '後から入力' : '')
  })

  it('retains a return to the old baseline during a pending write as a later input', () => {
    const snapshot = snapshotFixture()
    let state = changeTopic(receive(startRead(), 'available', snapshot), 'title', '送信するタイトル')
    state = startTopicWrite(state)
    state = changeTopic(state, 'title', snapshot.topics[0]!.title)
    const laterTitle = state.topicInputs[analysisTopicId]?.title

    const accepted = receive(state, 'available', titleAcknowledged(snapshot))

    expect(laterTitle?.value).toBe('クラウド運用')
    expect(accepted.topicInputs[analysisTopicId]?.title).toBe(laterTitle)
    expect(isAnalysisDirty(accepted)).toBe(true)
  })

  it.each(['staleBase', 'conflict', 'invalid', 'forbidden', 'unavailable', 'sendFailed', 'bridgeUnavailable'] as const)(
    'retains only local inputs in state alongside hidden evidence after %s, including during recovery',
    (status) => {
      let state = changeTopic(receive(startRead(), 'available', snapshotFixture()), 'summary', '  自分の入力\n')
      state = analysisStateReducer(state, { type: 'reasonInputChanged', value: '保持する理由' })
      state = startTopicWrite(state, 'summary')
      const pending = state.pending!
      const failed = status === 'sendFailed'
        ? analysisStateReducer(state, { type: 'sendFailed', requestId: pending.requestId })
        : status === 'bridgeUnavailable'
          ? analysisStateReducer(state, { type: 'bridgeUnavailable' })
          : receive(state, status)
      expect(failed.snapshot).toBeNull()
      expect(failed.pending).toBeNull()
      expect(failed.topicInputs).toBe(state.topicInputs)
      expect(failed.reasonInput).toBe(state.reasonInput)
      expect(isAnalysisDirty(failed)).toBe(true)

      const recovering = analysisStateReducer(failed, {
        type: 'requestStarted', requestId: refreshRequestId,
        method: 'analysis.read', projectId: analysisProjectId,
      })
      expect(recovering.topicInputs).toBe(state.topicInputs)
      const lateAck = createAnalysisResponseValue('analysis.updateTopic')
      lateAck.requestId = pending.requestId
      expect(analysisStateReducer(recovering, {
        type: 'responseReceived', response: requireResponse(lateAck),
      })).toBe(recovering)

      const refreshed = receive(recovering, 'available', titleAcknowledged(snapshotFixture()))
      const topic = refreshed.snapshot!.topics[0]!
      const input = refreshed.topicInputs[analysisTopicId]!.summary!
      expect(input.value).toBe('  自分の入力\n')
      expect(analysisTopicInputIsCurrent(refreshed.snapshot!, topic, 'summary', input)).toBe(false)
      expect(changeTopic(refreshed, 'summary', '新基準に混ぜない')).toBe(refreshed)
      const discarded = analysisStateReducer(refreshed, {
        type: 'topicInputDiscarded', topicId: analysisTopicId, field: 'summary',
      })
      const newInput = changeTopic(discarded, 'summary', '自分で新基準を選択')
      expect(newInput.topicInputs[analysisTopicId]?.summary?.base.metadataRevisionId)
        .toBe(topic.metadataRevisionId)
      expect(newInput.reasonInput).toBe(state.reasonInput)
    },
  )

  it.each(['analysis revision', 'digest', 'metadata revision', 'baseline value', 'removed topic'] as const)(
    'never silently rebases a local field when a refresh changes its %s',
    (changed) => {
      const snapshot = snapshotFixture()
      const editing = changeTopic(receive(startRead(), 'available', snapshot), 'summary', '編集中')
      const pending = analysisStateReducer(editing, {
        type: 'requestStarted', requestId: refreshRequestId,
        method: 'analysis.read', projectId: analysisProjectId,
      })
      const refreshedSnapshot: AnalysisSnapshot = {
        ...snapshot,
        revisionId: changed === 'analysis revision' ? nextMetadataRevisionId : snapshot.revisionId,
        revisionDigest: changed === 'digest' ? 'f'.repeat(64) : snapshot.revisionDigest,
        topics: changed === 'removed topic' ? [] : snapshot.topics.map((topic) => ({
          ...topic,
          metadataRevisionId: changed === 'metadata revision' ? nextMetadataRevisionId : topic.metadataRevisionId,
          summary: changed === 'baseline value' ? '別の正規値' : topic.summary,
        })),
      }
      const refreshed = receive(pending, 'available', refreshedSnapshot)
      expect(refreshed.topicInputs).toBe(editing.topicInputs)
      expect(refreshed.topicInputs[analysisTopicId]?.summary?.value).toBe('編集中')
      expect(changeTopic(refreshed, 'summary', '勝手に基準を変更')).toBe(refreshed)
      expect(isAnalysisDirty(refreshed)).toBe(true)
    },
  )

  it('does not clear inputs on wrong request, wrong method, or cross-project ACKs', () => {
    let state = changeTopic(receive(startRead(), 'available', snapshotFixture()), 'title', '送信するタイトル')
    state = analysisStateReducer(state, { type: 'reasonInputChanged', value: '自分の理由' })
    const pending = startTopicWrite(state)
    const value = createAnalysisResponseValue('analysis.updateTopic')
    value.requestId = refreshRequestId
    expect(analysisStateReducer(pending, {
      type: 'responseReceived', response: requireResponse(value),
    })).toBe(pending)
    value.requestId = pending.pending!.requestId
    value.method = 'analysis.read'
    expect(analysisStateReducer(pending, {
      type: 'responseReceived', response: requireResponse(value),
    })).toBe(pending)
    value.method = 'analysis.updateTopic'
    value.result.snapshot = { ...snapshotFixture(), projectId: secondTopicId }
    const invalid = analysisStateReducer(pending, {
      type: 'responseReceived', response: requireResponse(value),
    })
    expect(invalid.status).toBe('invalid')
    expect(invalid.snapshot).toBeNull()
    expect(invalid.topicInputs).toBe(state.topicInputs)
    expect(invalid.reasonInput).toBe(state.reasonInput)
  })

  it('local discard cannot cancel a mutation, while a sent request alone is not dirty', () => {
    let state = changeTopic(receive(startRead(), 'available', snapshotFixture()), 'title', '送信するタイトル')
    state = analysisStateReducer(state, { type: 'reasonInputChanged', value: '送信理由' })
    const pending = startTopicWrite(state)
    const discarded = analysisStateReducer(pending, { type: 'localInputsDiscarded' })
    expect(discarded.pending).toBe(pending.pending)
    expect(discarded.snapshot).toBe(pending.snapshot)
    expect(discarded.topicInputs).toEqual({})
    expect(discarded.reasonInput.value).toBe('')
    expect(isAnalysisDirty(discarded)).toBe(false)
    const accepted = receive(discarded, 'available', titleAcknowledged(snapshotFixture()))
    expect(isAnalysisDirty(accepted)).toBe(false)
    expect(accepted.snapshot?.stagedChanges).toHaveLength(1)
    expect(accepted.operationMessage).toContain('未永続化')

    const editingAgain = changeTopic(accepted, 'summary', '別の入力')
    const clean = analysisStateReducer(editingAgain, { type: 'localInputsDiscarded' })
    expect(isAnalysisDirty(clean)).toBe(false)
    expect(clean.snapshot).toBe(accepted.snapshot)
    expect(clean.snapshot?.stagedChanges).toHaveLength(1)
  })

  it('resets local inputs only for a different project, not a session restart', () => {
    let editing = changeTopic(receive(startRead(), 'available', snapshotFixture()), 'summary', '自分の入力')
    editing = analysisStateReducer(editing, { type: 'reasonInputChanged', value: '自分の理由' })
    const sameProject = analysisStateReducer(editing, { type: 'projectChanged', projectId: analysisProjectId })
    expect(sameProject).toBe(editing)
    expect(analysisStateReducer(editing, { type: 'sessionStarted' })).toBe(editing)
    const differentProject = analysisStateReducer(editing, { type: 'projectChanged', projectId: secondTopicId })
    expect(differentProject).toEqual({ ...initialAnalysisState, projectId: secondTopicId })
    expect(isAnalysisDirty(differentProject)).toBe(false)
  })
})

const secondTopicId = '10000000-0000-4000-8000-000000000020'
const nextMetadataRevisionId = '10000000-0000-4000-8000-000000000030'
const mutationRequestId = '00112233-4455-6677-8899-aabbccddeef0'
const refreshRequestId = '00112233-4455-6677-8899-aabbccddeef1'

function snapshotFixture(twoTopics = false): AnalysisSnapshot {
  const snapshot = createAnalysisResponseValue().result.snapshot
  if (snapshot === null) throw new Error('Expected a snapshot fixture.')
  return twoTopics ? {
    ...snapshot,
    topics: [...snapshot.topics, {
      ...snapshot.topics[0]!,
      topicId: secondTopicId,
      metadataId: '10000000-0000-4000-8000-000000000021',
      metadataRevisionId: '10000000-0000-4000-8000-000000000022',
      title: '第二のトピック',
    }],
  } : snapshot
}

function titleAcknowledged(snapshot: AnalysisSnapshot): AnalysisSnapshot {
  return {
    ...snapshot,
    topics: snapshot.topics.map((topic) => topic.topicId === analysisTopicId ? {
      ...topic,
      title: '送信するタイトル',
      titleOrigin: 'manualEditorial',
      metadataRevisionId: nextMetadataRevisionId,
      parentMetadataRevisionId: topic.metadataRevisionId,
      changedField: 'title',
      reason: '送信理由',
    } : topic),
    stagedChanges: [{
      kind: 'topicMetadataRevision', topicId: analysisTopicId,
      revisionId: nextMetadataRevisionId, parentRevisionId: analysisMetadataRevisionId,
      field: 'title', persisted: false,
    }],
  }
}

function changeTopic(state: AnalysisState, field: AnalysisTopicField, value: string, topicId = analysisTopicId) {
  return analysisStateReducer(state, { type: 'topicInputChanged', topicId, field, value })
}

function startTopicWrite(state: AnalysisState, field: AnalysisTopicField = 'title') {
  return analysisStateReducer(state, {
    type: 'requestStarted', requestId: mutationRequestId,
    method: 'analysis.updateTopic', projectId: analysisProjectId,
    topicUpdate: { topicId: analysisTopicId, field },
  })
}

function receive(
  state: AnalysisState,
  status: AnalysisResponse['result']['status'],
  snapshot?: AnalysisSnapshot,
): AnalysisState {
  const pending = state.pending
  if (pending === null) throw new Error('Expected a pending request.')
  return analysisStateReducer(state, {
    type: 'responseReceived',
    response: requireResponse({
      version: 1, requestId: pending.requestId, method: pending.method,
      result: { status, snapshot: status === 'available' ? snapshot : null },
    }),
  })
}

function startRead() {
  return analysisStateReducer(
    { ...initialAnalysisState, projectId: analysisProjectId },
    {
      type: 'requestStarted',
      requestId: analysisRequestId,
      method: ANALYSIS_READ_METHOD,
      projectId: analysisProjectId,
    },
  )
}

function requireResponse(value: unknown): AnalysisResponse {
  const response = parseAnalysisResponse(value)
  if (response === null) throw new Error('Expected a valid analysis response.')
  return response
}