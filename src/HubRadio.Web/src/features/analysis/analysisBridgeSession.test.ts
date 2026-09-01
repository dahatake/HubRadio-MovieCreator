import { describe, expect, it } from 'vitest'

import {
  createAnalysisReadRequest,
  parseAnalysisResponse,
  type AnalysisRequest,
  type AnalysisResponse,
} from './analysisProtocol'
import {
  startAnalysisBridgeSession,
  type AnalysisBridgeClient,
} from './analysisBridgeSession'
import type { AnalysisAction } from './analysisState'
import {
  analysisProjectId,
  analysisRequestId,
  createAnalysisResponseValue,
} from './analysisTestFixture'

const secondRequestId = '11111111-1111-4111-8111-111111111111'

describe('analysis bridge session', () => {
  it('allows one in-flight request and releases it only for the exact response', () => {
    const client = new FakeAnalysisClient()
    const actions: AnalysisAction[] = []
    const session = startAnalysisBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const first = createAnalysisReadRequest(analysisRequestId, analysisProjectId)
    const second = createAnalysisReadRequest(secondRequestId, analysisProjectId)

    session.send(first)
    session.send(second)
    expect(client.sent).toEqual([first])

    client.emit(responseFor(analysisRequestId, 'analysis.adopt'))
    session.send(second)
    expect(client.sent).toEqual([first])

    client.emit(responseFor(analysisRequestId, 'analysis.read'))
    session.send(second)
    expect(client.sent).toEqual([first, second])
    expect(actions.filter((action) => action.type === 'requestStarted')).toHaveLength(2)

    session.dispose()
    expect(client.disposed).toBe(true)
  })

  it('releases the in-flight slot when transport send fails', () => {
    const client = new FakeAnalysisClient()
    const actions: AnalysisAction[] = []
    const session = startAnalysisBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const first = createAnalysisReadRequest(analysisRequestId, analysisProjectId)
    const second = createAnalysisReadRequest(secondRequestId, analysisProjectId)

    client.failNextSend = true
    session.send(first)
    session.send(second)

    expect(client.sent).toEqual([second])
    expect(actions.some((action) => action.type === 'sendFailed')).toBe(true)
  })

  it('contains subscription and cleanup failures', () => {
    const client = new FakeAnalysisClient()
    client.throwOnSubscribe = true
    client.throwOnDispose = true
    const actions: AnalysisAction[] = []

    expect(() => startAnalysisBridgeSession(
      () => client,
      (action) => actions.push(action),
    )).not.toThrow()

    expect(client.disposeCount).toBe(1)
    expect(actions).toEqual([{ type: 'bridgeUnavailable' }])
  })

  it('contains unsubscribe and client cleanup failures during disposal', () => {
    const client = new FakeAnalysisClient()
    client.throwOnUnsubscribe = true
    client.throwOnDispose = true
    const session = startAnalysisBridgeSession(() => client, () => {})

    expect(() => session.dispose()).not.toThrow()
    expect(() => session.dispose()).not.toThrow()
    expect(client.unsubscribeCount).toBe(1)
    expect(client.disposeCount).toBe(1)
  })
})

class FakeAnalysisClient implements AnalysisBridgeClient {
  readonly sent: AnalysisRequest[] = []
  disposed = false
  disposeCount = 0
  failNextSend = false
  throwOnDispose = false
  throwOnSubscribe = false
  throwOnUnsubscribe = false
  unsubscribeCount = 0
  private readonly listeners = new Set<(response: AnalysisResponse) => void>()

  sendAnalysis(request: AnalysisRequest): void {
    if (this.failNextSend) {
      this.failNextSend = false
      throw new Error('transport failed')
    }
    this.sent.push(request)
  }

  onAnalysisResponse(listener: (response: AnalysisResponse) => void): () => void {
    if (this.throwOnSubscribe) throw new Error('subscription failed')
    this.listeners.add(listener)
    return () => {
      this.unsubscribeCount++
      if (this.throwOnUnsubscribe) throw new Error('unsubscribe failed')
      this.listeners.delete(listener)
    }
  }

  emit(response: AnalysisResponse): void {
    for (const listener of this.listeners) listener(response)
  }

  dispose(): void {
    this.disposeCount++
    this.disposed = true
    this.listeners.clear()
    if (this.throwOnDispose) throw new Error('client cleanup failed')
  }
}

function responseFor(
  requestId: string,
  method: AnalysisResponse['method'],
): AnalysisResponse {
  const value = createAnalysisResponseValue(method)
  value.requestId = requestId
  const response = parseAnalysisResponse(value)
  if (response === null) throw new Error('Expected a valid analysis response.')
  return response
}