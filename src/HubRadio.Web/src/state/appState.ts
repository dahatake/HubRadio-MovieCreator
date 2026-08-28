import type { AppAction, AppState, SyncError } from './types'

export const initialAppState: AppState = {
  localRevision: 0,
  acknowledgedRevision: null,
  pending: null,
  lastError: null,
}

export function localChanged(): AppAction {
  return { type: 'localChanged' }
}

export function syncRequested(requestId: string): AppAction {
  return { type: 'syncRequested', requestId }
}

export function syncAcknowledged(requestId: string): AppAction {
  return { type: 'syncAcknowledged', requestId }
}

export function syncFailed(requestId: string, error: SyncError): AppAction {
  return { type: 'syncFailed', requestId, error }
}

export function bridgeUnavailable(): AppAction {
  return { type: 'bridgeUnavailable' }
}

export function appStateReducer(state: AppState, action: AppAction): AppState {
  switch (action.type) {
    case 'localChanged':
      return {
        ...state,
        localRevision: state.localRevision + 1,
      }

    case 'syncRequested':
      return {
        ...state,
        pending: {
          requestId: action.requestId,
          revision: state.localRevision,
        },
        lastError: null,
      }

    case 'syncAcknowledged': {
      const pending = state.pending
      if (pending === null || pending.requestId !== action.requestId) {
        return state
      }

      return {
        ...state,
        acknowledgedRevision: pending.revision,
        pending: null,
      }
    }

    case 'syncFailed':
      if (state.pending === null || state.pending.requestId !== action.requestId) {
        return state
      }

      return {
        ...state,
        pending: null,
        lastError: action.error,
      }

    case 'bridgeUnavailable':
      return {
        ...state,
        pending: null,
        lastError: 'bridge_unavailable',
      }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}