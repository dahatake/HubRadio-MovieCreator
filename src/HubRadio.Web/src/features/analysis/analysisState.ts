import type {
  AnalysisMethod,
  AnalysisResponse,
  AnalysisSnapshot,
} from './analysisProtocol'

export type AnalysisUiStatus =
  | 'idle'
  | 'loading'
  | 'available'
  | 'forbidden'
  | 'unavailable'
  | 'staleBase'
  | 'conflict'
  | 'noChange'
  | 'invalid'
  | 'bridgeUnavailable'
  | 'sendFailed'

export interface PendingAnalysisRequest {
  readonly requestId: string
  readonly method: AnalysisMethod
  readonly projectId: string
}

export interface AnalysisState {
  readonly projectId: string | null
  readonly status: AnalysisUiStatus
  readonly pending: PendingAnalysisRequest | null
  readonly snapshot: AnalysisSnapshot | null
  readonly navigationMessage: string | null
  readonly operationMessage: string | null
}

export type AnalysisAction =
  | Readonly<{ type: 'sessionStarted' }>
  | Readonly<{ type: 'projectChanged'; projectId: string | null }>
  | Readonly<{
      type: 'requestStarted'
      requestId: string
      method: AnalysisMethod
      projectId: string
    }>
  | Readonly<{ type: 'responseReceived'; response: AnalysisResponse }>
  | Readonly<{ type: 'requestInvalid' }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'sendFailed'; requestId: string }>
  | Readonly<{ type: 'navigationReported'; message: string | null }>
  | Readonly<{ type: 'operationReported'; message: string | null }>

export const initialAnalysisState: AnalysisState = Object.freeze({
  projectId: null,
  status: 'idle',
  pending: null,
  snapshot: null,
  navigationMessage: null,
  operationMessage: null,
})

export function analysisStateReducer(
  state: AnalysisState,
  action: AnalysisAction,
): AnalysisState {
  switch (action.type) {
    case 'sessionStarted':
      return state.pending === null
        ? state
        : {
            ...state,
            status: state.snapshot === null ? 'idle' : 'available',
            pending: null,
          }

    case 'projectChanged':
      if (action.projectId === state.projectId) return state
      return { ...initialAnalysisState, projectId: action.projectId }

    case 'requestStarted':
      if (state.projectId !== action.projectId || state.pending !== null) return state
      return {
        ...state,
        status: action.method === 'analysis.read' ? 'loading' : state.status,
        pending: {
          requestId: action.requestId,
          method: action.method,
          projectId: action.projectId,
        },
        navigationMessage: null,
        operationMessage: null,
      }

    case 'responseReceived': {
      const pending = state.pending
      if (
        pending === null ||
        action.response.requestId !== pending.requestId ||
        action.response.method !== pending.method
      ) {
        return state
      }

      const result = action.response.result
      if (result.status === 'available') {
        if (result.snapshot.projectId !== pending.projectId) {
          return {
            ...state,
            status: 'invalid',
            pending: null,
            snapshot: null,
            operationMessage: null,
          }
        }
        return {
          ...state,
          status: 'available',
          pending: null,
          snapshot: result.snapshot,
          operationMessage:
            pending.method === 'analysis.read'
              ? null
              : '変更を作業中データへ反映しました。まだ保存されていません。',
        }
      }

      if (result.status === 'noChange') {
        return {
          ...state,
          status: 'noChange',
          pending: null,
          operationMessage: '内容は変更されませんでした。',
        }
      }

      return {
        ...state,
        status: result.status,
        pending: null,
        snapshot: null,
        operationMessage: null,
      }
    }

    case 'requestInvalid':
      return {
        ...state,
        status: 'invalid',
        pending: null,
        snapshot: null,
        operationMessage: null,
      }

    case 'bridgeUnavailable':
      return {
        ...state,
        status: 'bridgeUnavailable',
        pending: null,
        snapshot: null,
        operationMessage: null,
      }

    case 'sendFailed':
      if (state.pending?.requestId !== action.requestId) return state
      return {
        ...state,
        status: 'sendFailed',
        pending: null,
        snapshot: null,
        operationMessage: null,
      }

    case 'navigationReported':
      return { ...state, navigationMessage: action.message }

    case 'operationReported':
      return { ...state, operationMessage: action.message }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}