import type {
  DeliveryRationalTime,
  DeliveryReadResponse,
  DeliveryReadStatus,
  DeliverySnapshot,
  DeliveryTrace,
} from './deliveryProtocol'

export interface PendingDeliveryRequest {
  readonly requestId: string
  readonly outputTime: DeliveryRationalTime | null
}

export interface DeliveryUiError {
  readonly code: 'delivery.invalid_request' | 'delivery.operation_failed'
  readonly message: string
}

export type DeliveryCompletedOperation = 'snapshot' | 'trace'

export interface DeliveryUiState {
  readonly versionIdInput: string
  readonly traceValueInput: string
  readonly traceRateInput: string
  readonly snapshot: DeliverySnapshot | null
  readonly trace: DeliveryTrace | null
  readonly status: DeliveryReadStatus | null
  readonly pending: PendingDeliveryRequest | null
  readonly error: DeliveryUiError | null
  readonly lastCompletedOperation: DeliveryCompletedOperation | null
}

export type DeliveryUiAction =
  | Readonly<{ type: 'versionIdChanged'; value: string }>
  | Readonly<{ type: 'traceValueChanged'; value: string }>
  | Readonly<{ type: 'traceRateChanged'; value: string }>
  | Readonly<{
      type: 'requestStarted'
      requestId: string
      outputTime: DeliveryRationalTime | null
    }>
  | Readonly<{ type: 'responseReceived'; response: DeliveryReadResponse }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'localInvalidRequest' }>
  | Readonly<{ type: 'errorCleared' }>

export const initialDeliveryState: DeliveryUiState = Object.freeze({
  versionIdInput: '',
  traceValueInput: '0',
  traceRateInput: '1',
  snapshot: null,
  trace: null,
  status: null,
  pending: null,
  error: null,
  lastCompletedOperation: null,
})

export function deliveryVersionIdChanged(value: string): DeliveryUiAction {
  return { type: 'versionIdChanged', value }
}

export function deliveryTraceValueChanged(value: string): DeliveryUiAction {
  return { type: 'traceValueChanged', value }
}

export function deliveryTraceRateChanged(value: string): DeliveryUiAction {
  return { type: 'traceRateChanged', value }
}

export function deliveryRequestStarted(
  requestId: string,
  outputTime: DeliveryRationalTime | null,
): DeliveryUiAction {
  return { type: 'requestStarted', requestId, outputTime }
}

export function deliveryResponseReceived(
  response: DeliveryReadResponse,
): DeliveryUiAction {
  return { type: 'responseReceived', response }
}

export function deliveryBridgeUnavailable(): DeliveryUiAction {
  return { type: 'bridgeUnavailable' }
}

export function deliveryLocalInvalidRequest(): DeliveryUiAction {
  return { type: 'localInvalidRequest' }
}

export function deliveryErrorCleared(): DeliveryUiAction {
  return { type: 'errorCleared' }
}

export function deliveryStateReducer(
  state: DeliveryUiState,
  action: DeliveryUiAction,
): DeliveryUiState {
  switch (action.type) {
    case 'versionIdChanged':
      return {
        ...state,
        versionIdInput: action.value,
        snapshot: state.snapshot?.versionId === action.value
          ? state.snapshot
          : null,
        trace: null,
        status: state.snapshot?.versionId === action.value
          ? state.status
          : null,
        error: null,
        lastCompletedOperation: null,
      }

    case 'traceValueChanged':
      return {
        ...state,
        traceValueInput: action.value,
        trace: null,
        error: null,
        lastCompletedOperation:
          state.snapshot === null ? null : 'snapshot',
      }

    case 'traceRateChanged':
      return {
        ...state,
        traceRateInput: action.value,
        trace: null,
        error: null,
        lastCompletedOperation:
          state.snapshot === null ? null : 'snapshot',
      }

    case 'requestStarted':
      if (state.pending !== null) {
        return state
      }
      return {
        ...state,
        pending: {
          requestId: action.requestId,
          outputTime: action.outputTime,
        },
        error: null,
      }

    case 'responseReceived': {
      const pending = state.pending
      if (
        pending === null ||
        pending.requestId !== action.response.requestId
      ) {
        return state
      }
      const operation: DeliveryCompletedOperation =
        pending.outputTime === null ? 'snapshot' : 'trace'
      if (action.response.result.status !== 'available') {
        return {
          ...state,
          snapshot: null,
          trace: null,
          status: action.response.result.status,
          pending: null,
          error: null,
          lastCompletedOperation: operation,
        }
      }
      return {
        ...state,
        versionIdInput: action.response.result.snapshot.versionId,
        snapshot: action.response.result.snapshot,
        trace: action.response.result.trace,
        status: 'available',
        pending: null,
        error: null,
        lastCompletedOperation: operation,
      }
    }

    case 'bridgeUnavailable':
      return {
        ...state,
        snapshot: null,
        trace: null,
        status: null,
        pending: null,
        error: {
          code: 'delivery.operation_failed',
          message: 'ネイティブホストへ配信証跡の要求を送信できませんでした。',
        },
        lastCompletedOperation: null,
      }

    case 'localInvalidRequest':
      return {
        ...state,
        error: {
          code: 'delivery.invalid_request',
          message: '構成版 ID または約分済みの出力時刻を確認してください。',
        },
        lastCompletedOperation: null,
      }

    case 'errorCleared':
      return { ...state, error: null }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}
