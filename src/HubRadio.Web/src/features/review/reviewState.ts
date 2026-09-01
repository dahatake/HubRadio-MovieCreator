import type {
  ReviewAction,
  ReviewBridgeErrorCode,
  ReviewBridgeMethod,
  ReviewBridgeResponse,
  ReviewSnapshot,
} from './reviewProtocol'

export interface PendingReviewRequest {
  readonly requestId: string
  readonly method: ReviewBridgeMethod
}

export interface ReviewUiError {
  readonly code: ReviewBridgeErrorCode
  readonly message: string
}

export interface ReviewUiState {
  readonly versionIdInput: string
  readonly snapshot: ReviewSnapshot | null
  readonly pending: PendingReviewRequest | null
  readonly error: ReviewUiError | null
  readonly lastCompletedMethod: ReviewBridgeMethod | null
}

export type ReviewUiAction =
  | Readonly<{ type: 'versionIdChanged'; value: string }>
  | Readonly<{
      type: 'requestStarted'
      requestId: string
      method: ReviewBridgeMethod
    }>
  | Readonly<{ type: 'responseReceived'; response: ReviewBridgeResponse }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'localInvalidRequest' }>
  | Readonly<{ type: 'errorCleared' }>

export const initialReviewState: ReviewUiState = Object.freeze({
  versionIdInput: '',
  snapshot: null,
  pending: null,
  error: null,
  lastCompletedMethod: null,
})

export function reviewVersionIdChanged(value: string): ReviewUiAction {
  return { type: 'versionIdChanged', value }
}

export function reviewRequestStarted(
  requestId: string,
  method: ReviewBridgeMethod,
): ReviewUiAction {
  return { type: 'requestStarted', requestId, method }
}

export function reviewResponseReceived(
  response: ReviewBridgeResponse,
): ReviewUiAction {
  return { type: 'responseReceived', response }
}

export function reviewBridgeUnavailable(): ReviewUiAction {
  return { type: 'bridgeUnavailable' }
}

export function reviewLocalInvalidRequest(): ReviewUiAction {
  return { type: 'localInvalidRequest' }
}

export function reviewErrorCleared(): ReviewUiAction {
  return { type: 'errorCleared' }
}

export function reviewStateReducer(
  state: ReviewUiState,
  action: ReviewUiAction,
): ReviewUiState {
  switch (action.type) {
    case 'versionIdChanged':
      return {
        ...state,
        versionIdInput: action.value,
        snapshot:
          state.snapshot?.versionId === action.value ? state.snapshot : null,
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
        },
        error: null,
        lastCompletedMethod: null,
      }

    case 'responseReceived': {
      const pending = state.pending
      if (
        pending === null ||
        pending.requestId !== action.response.requestId ||
        pending.method !== action.response.method
      ) {
        return state
      }

      if ('error' in action.response) {
        return {
          ...state,
          pending: null,
          error: action.response.error,
          lastCompletedMethod: null,
        }
      }

      return {
        ...state,
        versionIdInput: action.response.result.versionId,
        snapshot: action.response.result,
        pending: null,
        error: null,
        lastCompletedMethod: action.response.method,
      }
    }

    case 'bridgeUnavailable':
      return {
        ...state,
        pending: null,
        error: {
          code: 'review.operation_failed',
          message: 'ネイティブホストへレビュー要求を送信できませんでした。',
        },
        lastCompletedMethod: null,
      }

    case 'localInvalidRequest':
      return {
        ...state,
        error: {
          code: 'review.invalid_request',
          message: '版 ID、時刻、または入力内容を確認してください。',
        },
        lastCompletedMethod: null,
      }

    case 'errorCleared':
      return {
        ...state,
        error: null,
      }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}

export function selectReviewActionStatus(
  snapshot: ReviewSnapshot,
  kind: ReviewSnapshot['actions'][number]['kind'],
): ReviewSnapshot['actions'][number]['status'] {
  return selectReviewAction(snapshot, kind).status
}

export function selectReviewAction(
  snapshot: ReviewSnapshot,
  kind: ReviewSnapshot['actions'][number]['kind'],
): ReviewAction {
  const action = snapshot.actions.find((candidate) => candidate.kind === kind)
  if (action === undefined) {
    throw new Error('The closed review snapshot is missing an action.')
  }
  return action
}
