import type {
  LayoutBridgeRequest,
  LayoutBridgeResponse,
  LayoutBridgeStatus,
  LayoutSnapshot,
} from './layoutProtocol'

export interface PendingLayoutRequest {
  readonly requestId: string
  readonly method: LayoutBridgeRequest['method']
  readonly projectId: string
}

export interface LayoutUiError {
  readonly code: 'layout.invalid_request' | 'layout.operation_failed'
  readonly message: string
}

export interface LayoutUiState {
  readonly projectIdInput: string
  readonly snapshot: LayoutSnapshot | null
  readonly status: LayoutBridgeStatus | null
  readonly pending: PendingLayoutRequest | null
  readonly error: LayoutUiError | null
  readonly lastCompletedMethod: LayoutBridgeRequest['method'] | null
}

export type LayoutUiAction =
  | Readonly<{ type: 'projectIdChanged'; value: string }>
  | Readonly<{
      type: 'requestStarted'
      requestId: string
      method: LayoutBridgeRequest['method']
      projectId: string
    }>
  | Readonly<{ type: 'responseReceived'; response: LayoutBridgeResponse }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'localInvalidRequest' }>
  | Readonly<{ type: 'errorCleared' }>

export const initialLayoutState: LayoutUiState = Object.freeze({
  projectIdInput: '',
  snapshot: null,
  status: null,
  pending: null,
  error: null,
  lastCompletedMethod: null,
})

export function layoutProjectIdChanged(value: string): LayoutUiAction {
  return { type: 'projectIdChanged', value }
}

export function layoutRequestStarted(
  requestId: string,
  method: LayoutBridgeRequest['method'],
  projectId: string,
): LayoutUiAction {
  return { type: 'requestStarted', requestId, method, projectId }
}

export function layoutResponseReceived(
  response: LayoutBridgeResponse,
): LayoutUiAction {
  return { type: 'responseReceived', response }
}

export function layoutBridgeUnavailable(): LayoutUiAction {
  return { type: 'bridgeUnavailable' }
}

export function layoutLocalInvalidRequest(): LayoutUiAction {
  return { type: 'localInvalidRequest' }
}

export function layoutErrorCleared(): LayoutUiAction {
  return { type: 'errorCleared' }
}

export function layoutStateReducer(
  state: LayoutUiState,
  action: LayoutUiAction,
): LayoutUiState {
  switch (action.type) {
    case 'projectIdChanged':
      return {
        ...state,
        projectIdInput: action.value,
        snapshot: state.snapshot?.projectId === action.value
          ? state.snapshot
          : null,
        status: state.snapshot?.projectId === action.value
          ? state.status
          : null,
        error: null,
        lastCompletedMethod: null,
      }

    case 'requestStarted':
      if (state.pending !== null) {
        return state
      }
      return {
        ...state,
        pending: {
          requestId: action.requestId,
          method: action.method,
          projectId: action.projectId,
        },
        error: null,
      }

    case 'responseReceived': {
      const pending = state.pending
      const response = action.response
      if (
        pending === null ||
        pending.requestId !== response.requestId ||
        pending.method !== response.method ||
        (response.result.status === 'available' &&
          response.result.snapshot.projectId !== pending.projectId)
      ) {
        return state
      }
      if (response.result.status !== 'available') {
        if (response.result.status === 'noChange') {
          return {
            ...state,
            status: 'noChange',
            pending: null,
            error: null,
            lastCompletedMethod: response.method,
          }
        }
        return {
          ...state,
          snapshot: null,
          status: response.result.status,
          pending: null,
          error: null,
          lastCompletedMethod: response.method,
        }
      }
      return {
        ...state,
        projectIdInput: response.result.snapshot.projectId,
        snapshot: response.result.snapshot,
        status: 'available',
        pending: null,
        error: null,
        lastCompletedMethod: response.method,
      }
    }

    case 'bridgeUnavailable':
      return {
        ...state,
        snapshot: null,
        status: null,
        pending: null,
        error: {
          code: 'layout.operation_failed',
          message: 'ネイティブホストへレイアウト要求を送信できませんでした。',
        },
        lastCompletedMethod: null,
      }

    case 'localInvalidRequest':
      return {
        ...state,
        error: {
          code: 'layout.invalid_request',
          message: 'プロジェクト ID、版、座標、時刻、または編集内容を確認してください。',
        },
      }

    case 'errorCleared':
      return { ...state, error: null }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}
