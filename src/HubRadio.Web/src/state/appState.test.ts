import { describe, expect, it } from 'vitest'

import {
  appStateReducer,
  bridgeUnavailable,
  hasUnsavedChanges,
  initialAppState,
  localChanged,
  selectSyncStatus,
  syncAcknowledged,
  syncFailed,
  syncRequested,
  type AppAction,
  type AppState,
} from './index'

const requestId = '00112233-4455-6677-8899-aabbccddeeff'
const otherRequestId = '11223344-5566-7788-99aa-bbccddeeff00'
const stateKeys = [
  'acknowledgedRevision',
  'jobMonitor',
  'lastError',
  'localRevision',
  'pending',
]

describe('app state', () => {
  it('starts unsaved with only the canonical state fields', () => {
    expect(initialAppState).toEqual({
      localRevision: 0,
      acknowledgedRevision: null,
      pending: null,
      lastError: null,
      jobMonitor: {
        trackedJobId: null,
        snapshot: null,
        pending: null,
        lastCommand: null,
        lastError: null,
      },
    })
    expect(selectSyncStatus(initialAppState)).toBe('unsaved')
    expect(hasUnsavedChanges(initialAppState)).toBe(true)
    expect(Object.keys(initialAppState).sort()).toEqual(stateKeys)
  })

  it('increments the local revision for each local change', () => {
    const state = reduce(localChanged(), localChanged())

    expect(state.localRevision).toBe(2)
    expect(selectSyncStatus(state)).toBe('unsaved')
    expect(hasUnsavedChanges(state)).toBe(true)
  })

  it('captures the current revision when synchronization is requested and clears errors', () => {
    const state = reduce(
      localChanged(),
      bridgeUnavailable(),
      syncRequested(requestId),
    )

    expect(state.pending).toEqual({ requestId, revision: 1 })
    expect(state.lastError).toBeNull()
    expect(selectSyncStatus(state)).toBe('pending')
  })

  it('acknowledges only the revision captured by the matching request', () => {
    const state = reduce(
      localChanged(),
      syncRequested(requestId),
      syncAcknowledged(requestId),
    )

    expect(state.acknowledgedRevision).toBe(1)
    expect(state.pending).toBeNull()
    expect(selectSyncStatus(state)).toBe('acknowledged')
    expect(hasUnsavedChanges(state)).toBe(false)
  })

  it('ignores stale and duplicate acknowledgements', () => {
    const pending = reduce(localChanged(), syncRequested(requestId))
    expect(appStateReducer(pending, syncAcknowledged(otherRequestId))).toBe(pending)

    const acknowledged = appStateReducer(pending, syncAcknowledged(requestId))
    expect(appStateReducer(acknowledged, syncAcknowledged(requestId))).toBe(acknowledged)
  })

  it('remains unsaved when a local edit occurs while a request is pending', () => {
    const state = reduce(
      localChanged(),
      syncRequested(requestId),
      localChanged(),
      syncAcknowledged(requestId),
    )

    expect(state.localRevision).toBe(2)
    expect(state.acknowledgedRevision).toBe(1)
    expect(state.pending).toBeNull()
    expect(selectSyncStatus(state)).toBe('unsaved')
    expect(hasUnsavedChanges(state)).toBe(true)
  })

  it('records a matching synchronization failure and ignores a stale failure', () => {
    const pending = reduce(localChanged(), syncRequested(requestId))
    expect(appStateReducer(pending, syncFailed(otherRequestId, 'bridge_send_failed'))).toBe(
      pending,
    )

    const failed = appStateReducer(
      pending,
      syncFailed(requestId, 'bridge_send_failed'),
    )
    expect(failed.pending).toBeNull()
    expect(failed.lastError).toBe('bridge_send_failed')
    expect(selectSyncStatus(failed)).toBe('error')
  })

  it('clears a previous error when a new synchronization starts', () => {
    const state = reduce(
      localChanged(),
      syncRequested(requestId),
      syncFailed(requestId, 'bridge_send_failed'),
      syncRequested(otherRequestId),
    )

    expect(state.pending).toEqual({ requestId: otherRequestId, revision: 1 })
    expect(state.lastError).toBeNull()
    expect(selectSyncStatus(state)).toBe('pending')
  })

  it('clears pending synchronization when the bridge is unavailable', () => {
    const state = reduce(
      localChanged(),
      syncRequested(requestId),
      bridgeUnavailable(),
    )

    expect(state.pending).toBeNull()
    expect(state.lastError).toBe('bridge_unavailable')
    expect(selectSyncStatus(state)).toBe('error')
  })

  it('derives status with pending, error, acknowledgement, and unsaved precedence', () => {
    const acknowledged: AppState = {
      ...initialAppState,
      acknowledgedRevision: 0,
    }
    const errored: AppState = {
      ...acknowledged,
      lastError: 'bridge_send_failed',
    }
    const pending: AppState = {
      ...errored,
      pending: { requestId, revision: 0 },
    }

    expect(selectSyncStatus(pending)).toBe('pending')
    expect(selectSyncStatus(errored)).toBe('error')
    expect(selectSyncStatus(acknowledged)).toBe('acknowledged')
    expect(selectSyncStatus(initialAppState)).toBe('unsaved')
  })

  it('never adds derived fields during state transitions', () => {
    const states = [
      reduce(localChanged()),
      reduce(localChanged(), syncRequested(requestId)),
      reduce(localChanged(), syncRequested(requestId), syncAcknowledged(requestId)),
      reduce(localChanged(), syncRequested(requestId), syncFailed(requestId, 'bridge_send_failed')),
      reduce(bridgeUnavailable()),
    ]

    for (const state of states) {
      expect(Object.keys(state).sort()).toEqual(stateKeys)
    }
  })
})

function reduce(...actions: readonly AppAction[]): AppState {
  return actions.reduce<AppState>(appStateReducer, initialAppState)
}