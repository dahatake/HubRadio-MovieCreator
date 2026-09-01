import { describe, expect, it } from 'vitest'

import {
  createDeliveryReadRequest,
  parseDeliveryReadResponse,
  type DeliveryReadRequest,
  type DeliveryReadResponse,
} from './deliveryProtocol'
import {
  startDeliveryBridgeSession,
  type DeliveryBridgeClient,
} from './deliveryBridgeSession'
import {
  deliveryBridgeUnavailable,
  deliveryRequestStarted,
  deliveryResponseReceived,
  type DeliveryUiAction,
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

describe('delivery bridge session', () => {
  it('correlates version and exact lookup time while rejecting double submit', () => {
    const client = new FakeClient()
    const actions: DeliveryUiAction[] = []
    const session = startDeliveryBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const first = createDeliveryReadRequest(
      deliveryRequestId,
      deliveryVersionId,
      null,
    )
    const second = createDeliveryReadRequest(
      deliverySecondRequestId,
      deliveryVersionId,
      outputTime,
    )

    expect(session.send(first)).toBe(true)
    expect(session.send(second)).toBe(false)
    expect(client.sent).toEqual([first])
    expect(actions).toEqual([
      deliveryRequestStarted(deliveryRequestId, null),
    ])

    client.emit(availableResponse(deliverySecondRequestId, false))
    client.emit(differentVersionResponse(deliveryRequestId, false))
    client.emit(availableResponse(deliveryRequestId, true))
    expect(actions).toHaveLength(1)

    const accepted = availableResponse(deliveryRequestId, false)
    client.emit(accepted)
    expect(actions.at(-1)).toEqual(deliveryResponseReceived(accepted))

    expect(session.send(second)).toBe(true)
    client.emit(differentLookupTimeResponse(deliverySecondRequestId))
    expect(actions.at(-1)).toEqual(
      deliveryRequestStarted(deliverySecondRequestId, outputTime),
    )
    const traced = availableResponse(deliverySecondRequestId, true)
    client.emit(traced)
    expect(actions.at(-1)).toEqual(deliveryResponseReceived(traced))
  })

  it('accepts a correlated closed failure and clears the pending slot', () => {
    const client = new FakeClient()
    const actions: DeliveryUiAction[] = []
    const session = startDeliveryBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const first = createDeliveryReadRequest(
      deliveryRequestId,
      deliveryVersionId,
      null,
    )
    session.send(first)
    const failure = failureResponse(deliveryRequestId, 'qualityControlFailed')
    client.emit(failure)
    expect(actions.at(-1)).toEqual(deliveryResponseReceived(failure))

    expect(session.send(createDeliveryReadRequest(
      deliverySecondRequestId,
      deliveryVersionId,
      null,
    ))).toBe(true)
  })

  it('abandons correlation and emits only a fixed private-safe error on send failure', () => {
    const client = new FakeClient()
    const actions: DeliveryUiAction[] = []
    const session = startDeliveryBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    client.throwOnSend = true
    const request = createDeliveryReadRequest(
      deliveryRequestId,
      deliveryVersionId,
      null,
    )

    expect(session.send(request)).toBe(false)
    expect(client.abandoned).toEqual([deliveryRequestId])
    expect(actions).toEqual([
      deliveryRequestStarted(deliveryRequestId, null),
      deliveryBridgeUnavailable(),
    ])
    expect(JSON.stringify(actions)).not.toContain('C:\\private')

    client.throwOnSend = false
    expect(session.send(request)).toBe(true)
  })

  it('maps client creation and subscription failures to an unavailable session', () => {
    const createActions: DeliveryUiAction[] = []
    const unavailable = startDeliveryBridgeSession(
      () => {
        throw new Error('C:\\private\\delivery.db')
      },
      (action) => createActions.push(action),
    )
    expect(createActions).toEqual([deliveryBridgeUnavailable()])

    const request = createDeliveryReadRequest(
      deliveryRequestId,
      deliveryVersionId,
      null,
    )
    expect(unavailable.send(request)).toBe(false)
    expect(createActions.slice(-2)).toEqual([
      deliveryRequestStarted(deliveryRequestId, null),
      deliveryBridgeUnavailable(),
    ])

    const client = new FakeClient()
    client.throwOnSubscribe = true
    client.throwOnDispose = true
    const subscribeActions: DeliveryUiAction[] = []
    startDeliveryBridgeSession(
      () => client,
      (action) => subscribeActions.push(action),
    )
    expect(client.disposeCount).toBe(1)
    expect(subscribeActions).toEqual([deliveryBridgeUnavailable()])
  })

  it('abandons one pending request and cleans up exactly once on disposal', () => {
    const client = new FakeClient()
    const actions: DeliveryUiAction[] = []
    const session = startDeliveryBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const request = createDeliveryReadRequest(
      deliveryRequestId,
      deliveryVersionId,
      outputTime,
    )
    session.send(request)

    client.throwOnUnsubscribe = true
    client.throwOnDispose = true
    expect(() => session.dispose()).not.toThrow()
    expect(() => session.dispose()).not.toThrow()
    expect(client.abandoned).toEqual([deliveryRequestId])
    expect(client.unsubscribeCount).toBe(1)
    expect(client.disposeCount).toBe(1)
    expect(session.send(request)).toBe(false)
    expect(actions.at(-1)).toEqual(deliveryBridgeUnavailable())
  })
})

class FakeClient implements DeliveryBridgeClient {
  readonly sent: DeliveryReadRequest[] = []
  readonly abandoned: string[] = []
  throwOnSend = false
  throwOnSubscribe = false
  throwOnUnsubscribe = false
  throwOnDispose = false
  unsubscribeCount = 0
  disposeCount = 0
  private listener: ((response: DeliveryReadResponse) => void) | null = null

  sendDelivery(request: DeliveryReadRequest): void {
    if (this.throwOnSend) {
      throw new Error('native stack C:\\private\\delivery.db')
    }
    this.sent.push(request)
  }

  abandonDeliveryRequest(requestId: string): void {
    this.abandoned.push(requestId)
  }

  onDeliveryResponse(
    listener: (response: DeliveryReadResponse) => void,
  ): () => void {
    if (this.throwOnSubscribe) {
      throw new Error('native subscription stack')
    }
    this.listener = listener
    return () => {
      this.unsubscribeCount++
      this.listener = null
      if (this.throwOnUnsubscribe) {
        throw new Error('private unsubscribe failure')
      }
    }
  }

  dispose(): void {
    this.disposeCount++
    this.listener = null
    if (this.throwOnDispose) {
      throw new Error('private dispose failure')
    }
  }

  emit(response: DeliveryReadResponse): void {
    this.listener?.(response)
  }
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

function differentVersionResponse(
  requestId: string,
  includeTrace: boolean,
): DeliveryReadResponse {
  const value = createDeliveryResponseValue(requestId, includeTrace)
  value.result.snapshot.versionId = deliverySecondVersionId
  return parseRequired(value)
}

function differentLookupTimeResponse(requestId: string): DeliveryReadResponse {
  const value = createDeliveryResponseValue(requestId, true)
  if (value.result.trace === null) {
    throw new Error('Expected trace fixture.')
  }
  value.result.trace.outputTime = { value: '3', rate: '4' }
  return parseRequired(value)
}

function parseRequired(value: unknown): DeliveryReadResponse {
  const parsed = parseDeliveryReadResponse(value)
  if (parsed === null) {
    throw new Error('Expected a valid delivery response fixture.')
  }
  return parsed
}
