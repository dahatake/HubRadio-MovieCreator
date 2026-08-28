import type { AppState, SyncStatus } from './types'

export function selectSyncStatus(state: AppState): SyncStatus {
  if (state.pending !== null) {
    return 'pending'
  }

  if (state.lastError !== null) {
    return 'error'
  }

  if (state.acknowledgedRevision === state.localRevision) {
    return 'acknowledged'
  }

  return 'unsaved'
}

export function hasUnsavedChanges(state: AppState): boolean {
  return (
    state.acknowledgedRevision === null ||
    state.acknowledgedRevision !== state.localRevision
  )
}