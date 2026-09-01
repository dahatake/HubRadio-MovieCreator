import { describe, expect, it } from 'vitest'

import {
  parseDeliveryReadResponse,
  type DeliveryReadResponse,
} from './deliveryProtocol'
import {
  deliveryBridgeUnavailable,
  deliveryErrorCleared,
  deliveryLocalInvalidRequest,
  deliveryRequestStarted,
  deliveryResponseReceived,
  deliveryStateReducer,
  deliveryTraceRateChanged,
  deliveryTraceValueChanged,
  deliveryVersionIdChanged,
  initialDeliveryState,
  type DeliveryUiAction,
  type DeliveryUiState,
} from './deliveryState'
import {
  createDeliveryFailureValue,
  createDeliveryResponseValue,
  deliveryRequestId,
  deliverySecondRequestId,
  deliverySecondVersionId,
  deliveryVersionId,
} from './deliveryTestFixture'

const outputTime = { value: '1', rate: '2' } as const

describe('delivery state reducer', () => {
  it('starts empty without fabricating provider availability or evidence', () => {
    expect(initialDeliveryState).toEqual({
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
    expect(Object.isFrozen(initialDeliveryState)).toBe(true)
  })

  it('retains evidence only while the exact version target is unchanged', () => {
    const loaded = reduce(
      deliveryVersionIdChanged(deliveryVersionId),
      deliveryRequestStarted(deliveryRequestId, null),
      deliveryResponseReceived(availableResponse(deliveryRequestId, false)),
    )
    expect(deliveryStateReducer(
      loaded,
      deliveryVersionIdChanged(deliveryVersionId),
    ).snapshot).toBe(loaded.snapshot)

    const changed = deliveryStateReducer(
      loaded,
      deliveryVersionIdChanged(deliverySecondVersionId),
    )
    expect(changed.snapshot).toBeNull()
    expect(changed.trace).toBeNull()
    expect(changed.status).toBeNull()
  })

  it('allows one request and correlates the response request ID', () => {
    const pending = reduce(
      deliveryVersionIdChanged(deliveryVersionId),
      deliveryRequestStarted(deliveryRequestId, null),
    )
    expect(deliveryStateReducer(
      pending,
      deliveryRequestStarted(deliverySecondRequestId, outputTime),
    )).toBe(pending)
    expect(deliveryStateReducer(
      pending,
      deliveryResponseReceived(availableResponse(
        deliverySecondRequestId,
        false,
      )),
    )).toBe(pending)

    const completed = deliveryStateReducer(
      pending,
      deliveryResponseReceived(availableResponse(deliveryRequestId, false)),
    )
    expect(completed.pending).toBeNull()
    expect(completed.status).toBe('available')
    expect(completed.snapshot?.versionId).toBe(deliveryVersionId)
    expect(completed.lastCompletedOperation).toBe('snapshot')
  })

  it('stores exact trace evidence only after a correlated trace request', () => {
    const pending = reduce(
      deliveryVersionIdChanged(deliveryVersionId),
      deliveryRequestStarted(deliveryRequestId, outputTime),
    )
    const completed = deliveryStateReducer(
      pending,
      deliveryResponseReceived(availableResponse(deliveryRequestId, true)),
    )

    expect(completed.trace?.status).toBe('resolved')
    expect(completed.trace?.outputTime).toEqual(outputTime)
    expect(completed.lastCompletedOperation).toBe('trace')

    const edited = deliveryStateReducer(
      completed,
      deliveryTraceValueChanged('3'),
    )
    expect(edited.trace).toBeNull()
    expect(edited.snapshot).toBe(completed.snapshot)
    expect(edited.lastCompletedOperation).toBe('snapshot')
    expect(deliveryStateReducer(
      edited,
      deliveryTraceRateChanged('2'),
    ).traceRateInput).toBe('2')
  })

  it('clears every evidence object on native failure instead of showing partial data', () => {
    const loaded = reduce(
      deliveryVersionIdChanged(deliveryVersionId),
      deliveryRequestStarted(deliveryRequestId, null),
      deliveryResponseReceived(availableResponse(deliveryRequestId, false)),
      deliveryRequestStarted(deliverySecondRequestId, outputTime),
    )
    const failure = failureResponse(
      deliverySecondRequestId,
      'qualityControlFailed',
    )
    const failed = deliveryStateReducer(
      loaded,
      deliveryResponseReceived(failure),
    )

    expect(failed.status).toBe('qualityControlFailed')
    expect(failed.snapshot).toBeNull()
    expect(failed.trace).toBeNull()
    expect(failed.pending).toBeNull()
    expect(failed.lastCompletedOperation).toBe('trace')
  })

  it('maps local and transport failures to fixed private-safe errors', () => {
    const pending = reduce(
      deliveryVersionIdChanged(deliveryVersionId),
      deliveryRequestStarted(deliveryRequestId, null),
    )
    const unavailable = deliveryStateReducer(
      pending,
      deliveryBridgeUnavailable(),
    )
    expect(unavailable.snapshot).toBeNull()
    expect(unavailable.pending).toBeNull()
    expect(unavailable.error).toEqual({
      code: 'delivery.operation_failed',
      message: 'ネイティブホストへ配信証跡の要求を送信できませんでした。',
    })

    const invalid = deliveryStateReducer(
      unavailable,
      deliveryLocalInvalidRequest(),
    )
    expect(invalid.error).toEqual({
      code: 'delivery.invalid_request',
      message: '構成版 ID または約分済みの出力時刻を確認してください。',
    })
    expect(JSON.stringify(invalid)).not.toContain('C:\\')
    expect(JSON.stringify(invalid)).not.toContain('S-1-5-')
    expect(deliveryStateReducer(invalid, deliveryErrorCleared()).error).toBeNull()
  })
})

function reduce(...actions: readonly DeliveryUiAction[]): DeliveryUiState {
  return actions.reduce<DeliveryUiState>(
    deliveryStateReducer,
    initialDeliveryState,
  )
}

function availableResponse(
  requestId: string,
  includeTrace: boolean,
): DeliveryReadResponse {
  return parseRequired(createDeliveryResponseValue(requestId, includeTrace))
}

function failureResponse(
  requestId: string,
  status: Parameters<typeof createDeliveryFailureValue>[0],
): DeliveryReadResponse {
  return parseRequired(createDeliveryFailureValue(status, requestId))
}

function parseRequired(value: unknown): DeliveryReadResponse {
  const parsed = parseDeliveryReadResponse(value)
  if (parsed === null) {
    throw new Error('Expected a valid delivery response fixture.')
  }
  return parsed
}
