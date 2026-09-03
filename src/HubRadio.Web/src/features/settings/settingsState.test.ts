import { describe, expect, it } from 'vitest'
import { snapshot } from './settingsProtocol.test'
import { initialSettingsState, settingsProjectChanged, settingsRequestStarted, settingsResponseReceived, settingsStateReducer } from './settingsState'

describe('settingsStateReducer', () => {
  it('requires refresh after conflict and accepts a refreshed snapshot', () => {
    const conflict = settingsStateReducer(initialSettingsState, settingsResponseReceived({ version: 1, requestId: snapshot.projectId, method: 'settings.update', result: { status: 'conflict', snapshot: null } }))
    expect(conflict.needsRefresh).toBe(true)
    expect(conflict.error).toBe('conflict')
    const refreshed = settingsStateReducer(conflict, settingsResponseReceived({ version: 1, requestId: snapshot.projectId, method: 'settings.read', result: { status: 'available', snapshot } }))
    expect(refreshed.needsRefresh).toBe(false)
    expect(refreshed.snapshot).toBe(snapshot)
  })

  it('clears pending state and the old snapshot when the project changes', () => {
    const pending = settingsStateReducer(initialSettingsState, settingsRequestStarted('settings.read'))
    const changed = settingsStateReducer(pending, settingsProjectChanged('new-project'))

    expect(changed.projectIdInput).toBe('new-project')
    expect(changed.pendingMethod).toBeNull()
    expect(changed.snapshot).toBeNull()
  })
})
