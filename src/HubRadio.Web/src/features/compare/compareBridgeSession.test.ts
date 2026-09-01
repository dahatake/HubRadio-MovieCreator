import { describe, expect, it } from 'vitest'

import {
  createComparisonReadRequest,
  parseComparisonReadResponse,
  type ComparisonReadRequest,
  type ComparisonReadResponse,
} from './compareProtocol'
import {
  startComparisonBridgeSession,
  type ComparisonBridgeClient,
} from './compareBridgeSession'
import {
  comparisonBridgeUnavailable,
  comparisonRequestStarted,
  comparisonResponseReceived,
  type ComparisonUiAction,
} from './compareState'
import {
  comparisonLeftDigest,
  comparisonLeftVersionId,
  comparisonRequestId,
  comparisonRightDigest,
  comparisonRightVersionId,
  comparisonSecondRequestId,
  createComparisonResponseValue,
} from './compareTestFixture'

describe('comparison bridge session', () => {
  it('correlates four target values, rejects double submit, then permits next request', () => {
    const client = new FakeClient()
    const actions: ComparisonUiAction[] = []
    const session = startComparisonBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const first = createRequest(comparisonRequestId)
    const second = createRequest(comparisonSecondRequestId)

    expect(session.send(first)).toBe(true)
    expect(session.send(second)).toBe(false)
    expect(client.sent).toEqual([first])
    expect(actions).toEqual([comparisonRequestStarted(first)])

    client.emit(validResponse(comparisonSecondRequestId))
    const wrongTarget = createComparisonResponseValue(comparisonRequestId)
    wrongTarget.result.comparison.left.versionDigest = 'c'.repeat(64)
    client.emit(requiredResponse(wrongTarget))
    expect(actions).toHaveLength(1)

    const accepted = validResponse(comparisonRequestId)
    client.emit(accepted)
    expect(actions.at(-1)).toEqual(comparisonResponseReceived(accepted))
    expect(session.send(second)).toBe(true)
    expect(client.sent).toEqual([first, second])
  })

  it('accepts a correlated closed failure and clears the pending slot', () => {
    const client = new FakeClient()
    const actions: ComparisonUiAction[] = []
    const session = startComparisonBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const first = createRequest(comparisonRequestId)
    session.send(first)
    const failure: ComparisonReadResponse = {
      version: 1,
      requestId: comparisonRequestId,
      method: 'compare.read',
      result: { status: 'staleVersion', comparison: null },
    }

    client.emit(failure)
    expect(actions.at(-1)).toEqual(comparisonResponseReceived(failure))
    expect(session.send(createRequest(comparisonSecondRequestId))).toBe(true)
  })

  it('abandons transport correlation and emits no private send failure detail', () => {
    const client = new FakeClient()
    const actions: ComparisonUiAction[] = []
    const session = startComparisonBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    client.throwOnSend = true
    const request = createRequest(comparisonRequestId)

    expect(session.send(request)).toBe(false)
    expect(client.abandoned).toEqual([comparisonRequestId])
    expect(actions).toEqual([
      comparisonRequestStarted(request),
      comparisonBridgeUnavailable(),
    ])
    expect(JSON.stringify(actions)).not.toContain('C:\\private')

    client.throwOnSend = false
    expect(session.send(createRequest(comparisonSecondRequestId))).toBe(true)
  })

  it('maps client creation and subscription failures to a private-safe session', () => {
    const creationActions: ComparisonUiAction[] = []
    const unavailable = startComparisonBridgeSession(
      () => {
        throw new Error('C:\\private\\comparison.db')
      },
      (action) => creationActions.push(action),
    )
    expect(creationActions).toEqual([comparisonBridgeUnavailable()])

    const request = createRequest(comparisonRequestId)
    expect(unavailable.send(request)).toBe(false)
    expect(creationActions.slice(-2)).toEqual([
      comparisonRequestStarted(request),
      comparisonBridgeUnavailable(),
    ])

    const client = new FakeClient()
    client.throwOnSubscribe = true
    client.throwOnDispose = true
    const subscriptionActions: ComparisonUiAction[] = []
    expect(() => startComparisonBridgeSession(
      () => client,
      (action) => subscriptionActions.push(action),
    )).not.toThrow()
    expect(client.disposeCount).toBe(1)
    expect(subscriptionActions).toEqual([comparisonBridgeUnavailable()])
  })

  it('abandons one pending request and cleans up exactly once on disposal', () => {
    const client = new FakeClient()
    const actions: ComparisonUiAction[] = []
    const session = startComparisonBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const request = createRequest(comparisonRequestId)
    session.send(request)
    client.throwOnUnsubscribe = true
    client.throwOnDispose = true

    expect(() => {
      session.dispose()
      session.dispose()
    }).not.toThrow()
    expect(client.abandoned).toEqual([comparisonRequestId])
    expect(client.unsubscribeCount).toBe(1)
    expect(client.disposeCount).toBe(1)
    expect(session.send(request)).toBe(false)
    expect(actions.at(-1)).toEqual(comparisonBridgeUnavailable())
  })

  function createRequest(requestId: string): ComparisonReadRequest {
    return createComparisonReadRequest(
      requestId,
      comparisonLeftVersionId,
      comparisonLeftDigest,
      comparisonRightVersionId,
      comparisonRightDigest,
    )
  }

  function validResponse(requestId: string): ComparisonReadResponse {
    return requiredResponse(createComparisonResponseValue(requestId))
  }

  function requiredResponse(value: unknown): ComparisonReadResponse {
    const response = parseComparisonReadResponse(value)
    if (response === null) throw new Error('Expected a valid comparison response.')
    return response
  }
})

class FakeClient implements ComparisonBridgeClient {
  readonly sent: ComparisonReadRequest[] = []
  readonly abandoned: string[] = []
  throwOnSend = false
  throwOnSubscribe = false
  throwOnUnsubscribe = false
  throwOnDispose = false
  unsubscribeCount = 0
  disposeCount = 0
  private listener: ((response: ComparisonReadResponse) => void) | null = null

  sendComparison(request: ComparisonReadRequest): void {
    if (this.throwOnSend) {
      throw new Error('native stack C:\\private\\comparison.db')
    }
    this.sent.push(request)
  }

  abandonComparisonRequest(requestId: string): void {
    this.abandoned.push(requestId)
  }

  onComparisonResponse(
    listener: (response: ComparisonReadResponse) => void,
  ): () => void {
    if (this.throwOnSubscribe) {
      throw new Error('private native subscription stack')
    }
    this.listener = listener
    return () => {
      this.unsubscribeCount++
      this.listener = null
      if (this.throwOnUnsubscribe) {
        throw new Error('private native unsubscribe stack')
      }
    }
  }

  dispose(): void {
    this.disposeCount++
    this.listener = null
    if (this.throwOnDispose) {
      throw new Error('C:\\private\\comparison-dispose.db')
    }
  }

  emit(response: ComparisonReadResponse): void {
    this.listener?.(response)
  }
}