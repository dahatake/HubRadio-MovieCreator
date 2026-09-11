import { describe, expect, it } from 'vitest'

import {
  parseTimelineApplyResponse,
  parseTimelineReadResponse,
  type TimelineApplyResponse,
  type TimelineReadResponse,
} from './timelineProtocol'
import {
  canRedoTimeline,
  canUndoTimeline,
  initialTimelineState,
  selectPendingTimelineOperations,
  timelineStateReducer,
  type TimelineState,
} from './timelineState'
import {
  createTimelineApplyResponseValue,
  createTimelineReadResponseValue,
  timelineAssetElementId,
  timelineProjectId,
  timelineRequestId,
  timelineSourceElementId,
  timelineVersionId,
} from './timelineTestFixture'

const secondRequestId = '8899aabb-ccdd-4eef-8011-223344556677'

describe('timeline pending edit state', () => {
  it('undoes and redoes only unsent operation groups', () => {
    let state = loadedState()
    state = timelineStateReducer(state, {
      type: 'operationsQueued',
      operations: [{ kind: 'delete', elementId: timelineSourceElementId }],
    })

    expect(state.phase).toBe('editing')
    expect(state.previewElements).toHaveLength(1)
    expect(canUndoTimeline(state)).toBe(true)
    expect(selectPendingTimelineOperations(state)).toHaveLength(1)

    state = timelineStateReducer(state, { type: 'undo' })
    expect(state.previewElements).toHaveLength(2)
    expect(state.pendingGroups).toHaveLength(0)
    expect(canRedoTimeline(state)).toBe(true)

    state = timelineStateReducer(state, { type: 'redo' })
    expect(state.previewElements).toHaveLength(1)
    expect(state.pendingGroups).toHaveLength(1)
  })

  it('installs apply success as a new immutable baseline and clears history', () => {
    let state = stateWithPendingDelete()
    state = timelineStateReducer(state, {
      type: 'applyRequested',
      requestId: secondRequestId,
    })
    const responseValue = createTimelineApplyResponseValue()
    responseValue.requestId = secondRequestId
    responseValue.result.snapshot.elements = [
      responseValue.result.snapshot.elements[1]!,
    ]
    responseValue.result.snapshot.elements[0]!.outputRange.start = {
      value: '0',
      rate: '1',
    }
    const response = parseTimelineApplyResponse(responseValue)
    if (response === null) {
      throw new Error('Expected a valid apply fixture.')
    }

    state = timelineStateReducer(state, { type: 'applyReceived', response })

    expect(state.phase).toBe('ready')
    expect(state.baseline?.revision).toBe(4)
    expect(state.pendingGroups).toHaveLength(0)
    expect(state.redoGroups).toHaveLength(0)
    expect(canUndoTimeline(state)).toBe(false)
  })

  it('blocks undo and redo until an OCC conflict is reloaded', () => {
    let state = stateWithPendingDelete()
    state = timelineStateReducer(state, {
      type: 'operationsQueued',
      operations: [{ kind: 'delete', elementId: timelineAssetElementId }],
    })
    state = timelineStateReducer(state, { type: 'undo' })
    expect(state.pendingGroups).toHaveLength(1)
    expect(state.redoGroups).toHaveLength(1)
    expect(canUndoTimeline(state)).toBe(true)
    expect(canRedoTimeline(state)).toBe(true)
    const localState = state

    state = timelineStateReducer(state, {
      type: 'applyRequested',
      requestId: secondRequestId,
    })
    state = timelineStateReducer(state, {
      type: 'applyReceived',
      response: conflictResponse(secondRequestId),
    })

    expect(state.phase).toBe('conflict')
    expect(state.issue).toBe('conflict')
    expect(state.intentValid).toBe(true)
    expect(state.pendingRequest).toBeNull()
    expect(state.baseline).toBe(localState.baseline)
    expect(state.previewElements).toBe(localState.previewElements)
    expect(state.pendingGroups).toBe(localState.pendingGroups)
    expect(state.redoGroups).toBe(localState.redoGroups)
    expect(canUndoTimeline(state)).toBe(false)
    expect(canRedoTimeline(state)).toBe(false)
    expect(timelineStateReducer(state, { type: 'undo' })).toBe(state)
    expect(timelineStateReducer(state, { type: 'redo' })).toBe(state)
  })

  it('preserves pending intent on OCC conflict and replays it after reload', () => {
    let state = stateWithPendingDelete()
    state = timelineStateReducer(state, {
      type: 'applyRequested',
      requestId: secondRequestId,
    })
    state = timelineStateReducer(state, {
      type: 'applyReceived',
      response: conflictResponse(secondRequestId),
    })

    expect(state.phase).toBe('conflict')
    expect(state.issue).toBe('conflict')
    expect(state.pendingGroups).toHaveLength(1)
    expect(state.previewElements).toHaveLength(1)

    state = timelineStateReducer(state, {
      type: 'readRequested',
      requestId: timelineRequestId,
      projectId: timelineProjectId,
      preservePending: true,
    })
    const reloadedValue = createTimelineReadResponseValue()
    reloadedValue.result.snapshot.revision = 4
    reloadedValue.result.snapshot.baseVersionId = timelineVersionId
    const reloaded = parseTimelineReadResponse(reloadedValue)
    if (reloaded === null) {
      throw new Error('Expected a valid concurrent reload fixture.')
    }
    state = timelineStateReducer(state, {
      type: 'readReceived',
      response: reloaded,
    })

    expect(state.phase).toBe('editing')
    expect(state.pendingGroups).toHaveLength(1)
    expect(state.previewElements).toHaveLength(1)
    expect(state.intentValid).toBe(true)
    expect(canUndoTimeline(state)).toBe(true)
    const reloadedPreview = state.previewElements

    state = timelineStateReducer(state, { type: 'undo' })
    expect(state.phase).toBe('ready')
    expect(state.previewElements).toEqual(state.baseline?.elements)
    expect(state.pendingGroups).toHaveLength(0)
    expect(canRedoTimeline(state)).toBe(true)

    state = timelineStateReducer(state, { type: 'redo' })
    expect(state.phase).toBe('editing')
    expect(state.previewElements).toEqual(reloadedPreview)
    expect(state.pendingGroups).toHaveLength(1)
  })

  it('keeps but disables intent that no longer applies after conflict reload', () => {
    let state = stateWithPendingDelete()
    state = timelineStateReducer(state, {
      type: 'applyRequested',
      requestId: secondRequestId,
    })
    state = timelineStateReducer(state, {
      type: 'applyReceived',
      response: conflictResponse(secondRequestId),
    })
    state = timelineStateReducer(state, {
      type: 'readRequested',
      requestId: timelineRequestId,
      projectId: timelineProjectId,
      preservePending: true,
    })
    const reloadedValue = createTimelineReadResponseValue()
    reloadedValue.result.snapshot.revision = 4
    reloadedValue.result.snapshot.baseVersionId = timelineVersionId
    reloadedValue.result.snapshot.elements = [
      reloadedValue.result.snapshot.elements[1]!,
    ]
    reloadedValue.result.snapshot.elements[0]!.outputRange.start = {
      value: '0',
      rate: '1',
    }
    const reloaded = parseTimelineReadResponse(reloadedValue)
    if (reloaded === null) {
      throw new Error('Expected a valid reload fixture.')
    }
    state = timelineStateReducer(state, {
      type: 'readReceived',
      response: reloaded,
    })

    expect(state.phase).toBe('conflict')
    expect(state.issue).toBe('intentInvalidAfterReload')
    expect(state.pendingGroups).toHaveLength(1)
    expect(state.intentValid).toBe(false)

    state = timelineStateReducer(state, { type: 'pendingDiscarded' })
    expect(state.phase).toBe('ready')
    expect(state.pendingGroups).toHaveLength(0)
    expect(state.intentValid).toBe(true)
  })

  it('preserves two invalid pending groups until explicit discard after reload', () => {
    let state = stateWithPendingDelete()
    state = timelineStateReducer(state, {
      type: 'operationsQueued',
      operations: [{ kind: 'delete', elementId: timelineAssetElementId }],
    })
    const pendingGroups = state.pendingGroups
    expect(pendingGroups).toHaveLength(2)
    state = timelineStateReducer(state, {
      type: 'applyRequested',
      requestId: secondRequestId,
    })
    state = timelineStateReducer(state, {
      type: 'applyReceived',
      response: conflictResponse(secondRequestId),
    })
    state = timelineStateReducer(state, {
      type: 'readRequested',
      requestId: timelineRequestId,
      projectId: timelineProjectId,
      preservePending: true,
    })
    const reloadedValue = createTimelineReadResponseValue()
    reloadedValue.result.snapshot.revision = 4
    reloadedValue.result.snapshot.baseVersionId = timelineVersionId
    reloadedValue.result.snapshot.elements = [
      reloadedValue.result.snapshot.elements[1]!,
    ]
    reloadedValue.result.snapshot.elements[0]!.outputRange.start = {
      value: '0',
      rate: '1',
    }
    const reloaded = parseTimelineReadResponse(reloadedValue)
    if (reloaded === null) {
      throw new Error('Expected a valid reload fixture without the first queued target.')
    }
    state = timelineStateReducer(state, {
      type: 'readReceived',
      response: reloaded,
    })

    expect(state.phase).toBe('conflict')
    expect(state.issue).toBe('intentInvalidAfterReload')
    expect(state.baseline).toBe(reloaded.result.snapshot)
    expect(state.baseline?.elements.map((element) => element.id)).toEqual([
      timelineAssetElementId,
    ])

    // Invalid intent must stay blocked even if a bridge failure changes the phase.
    for (const invalidState of [
      state,
      timelineStateReducer(state, { type: 'bridgeUnavailable' }),
    ]) {
      expect(invalidState.intentValid).toBe(false)
      expect(invalidState.pendingRequest).toBeNull()
      expect(invalidState.pendingGroups).toBe(pendingGroups)
      expect(invalidState.previewElements).toBe(state.baseline?.elements)
      expect(invalidState.redoGroups).toHaveLength(0)
      expect(canUndoTimeline(invalidState)).toBe(false)
      expect(canRedoTimeline(invalidState)).toBe(false)
      expect(timelineStateReducer(invalidState, { type: 'undo' })).toBe(invalidState)
      expect(timelineStateReducer(invalidState, { type: 'redo' })).toBe(invalidState)

      const discarded = timelineStateReducer(invalidState, { type: 'pendingDiscarded' })
      expect(discarded.phase).toBe('ready')
      expect(discarded.issue).toBeNull()
      expect(discarded.baseline).toBe(state.baseline)
      expect(discarded.previewElements).toBe(state.baseline?.elements)
      expect(discarded.pendingGroups).toHaveLength(0)
      expect(discarded.redoGroups).toHaveLength(0)
      expect(discarded.intentValid).toBe(true)
    }
  })

  it('rejects apply success that is not the exact OCC result for the pending preview', () => {
    const mutations: Array<
      (value: ReturnType<typeof createTimelineApplyResponseValue>) => void
    > = [
      (value) => {
        value.result.snapshot.workingCopyId =
          'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
      },
      (value) => {
        value.result.snapshot.revision += 1
      },
      (value) => {
        value.result.snapshot.baseVersionId = null
      },
      (value) => {
        value.result.snapshot.elements[0]!.id =
          '99999999-9999-4999-8999-999999999999'
      },
    ]

    for (const mutate of mutations) {
      let state = stateWithPendingDelete()
      state = timelineStateReducer(state, {
        type: 'applyRequested',
        requestId: secondRequestId,
      })
      const value = createTimelineApplyResponseValue()
      value.requestId = secondRequestId
      value.result.snapshot.elements = [value.result.snapshot.elements[1]!]
      value.result.snapshot.elements[0]!.outputRange.start = {
        value: '0',
        rate: '1',
      }
      mutate(value)
      const response = parseTimelineApplyResponse(value)
      if (response === null) {
        throw new Error('Expected a canonical adversarial apply response.')
      }

      state = timelineStateReducer(state, { type: 'applyReceived', response })

      expect(state.phase).toBe('editing')
      expect(state.issue).toBe('invalidResponse')
      expect(state.pendingGroups).toHaveLength(1)
    }
  })

  it('never replays pending intent against another project', () => {
    const state = stateWithPendingDelete()
    const otherProjectId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'

    expect(
      timelineStateReducer(state, {
        type: 'projectIdInputChanged',
        value: otherProjectId,
      }),
    ).toBe(state)
    expect(
      timelineStateReducer(state, {
        type: 'readRequested',
        requestId: secondRequestId,
        projectId: otherProjectId,
        preservePending: true,
      }),
    ).toBe(state)
  })
})

function loadedState(): TimelineState {
  let state = timelineStateReducer(initialTimelineState, {
    type: 'readRequested',
    requestId: timelineRequestId,
    projectId: timelineProjectId,
    preservePending: false,
  })
  return timelineStateReducer(state, {
    type: 'readReceived',
    response: readResponse(),
  })
}

function stateWithPendingDelete(): TimelineState {
  return timelineStateReducer(loadedState(), {
    type: 'operationsQueued',
    operations: [{ kind: 'delete', elementId: timelineSourceElementId }],
  })
}

function readResponse(): TimelineReadResponse {
  const response = parseTimelineReadResponse(createTimelineReadResponseValue())
  if (response === null) {
    throw new Error('Expected a valid read fixture.')
  }
  return response
}

function conflictResponse(requestId: string): TimelineApplyResponse {
  const response = parseTimelineApplyResponse({
    version: 1,
    requestId,
    method: 'timeline.apply',
    result: { status: 'conflict', snapshot: null },
  })
  if (response === null) {
    throw new Error('Expected a valid conflict response.')
  }
  return response
}