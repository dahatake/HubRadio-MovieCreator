import { describe, expect, it } from 'vitest'

import {
  createTranscriptReadRequest,
  parseTranscriptResponse,
  type TranscriptRequest,
  type TranscriptResponse,
} from './transcriptProtocol'
import {
  startTranscriptBridgeSession,
  type TranscriptBridgeClient,
} from './transcriptBridgeSession'
import type { TranscriptAction } from './transcriptState'
import {
  createTranscriptResponseValue,
  projectId,
  transcriptRequestId,
} from './transcriptTestFixture'

const secondRequestId = '11111111-1111-4111-8111-111111111111'

describe('transcript bridge session', () => {
  it('allows one in-flight request and releases it only for the exact response', () => {
    const client = new FakeTranscriptClient()
    const actions: TranscriptAction[] = []
    const session = startTranscriptBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const first = createTranscriptReadRequest(transcriptRequestId, projectId)
    const second = createTranscriptReadRequest(secondRequestId, projectId)

    session.send(first)
    session.send(second)
    expect(client.sent).toEqual([first])

    client.emit(responseFor(transcriptRequestId, 'transcript.editToken'))
    session.send(second)
    expect(client.sent).toEqual([first])

    client.emit(responseFor(transcriptRequestId, 'transcript.read'))
    session.send(second)
    expect(client.sent).toEqual([first, second])
    expect(actions.filter((action) => action.type === 'requestStarted')).toHaveLength(2)

    session.dispose()
    expect(client.disposed).toBe(true)
  })

  it('releases the in-flight slot when transport send fails', () => {
    const client = new FakeTranscriptClient()
    const actions: TranscriptAction[] = []
    const session = startTranscriptBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const first = createTranscriptReadRequest(transcriptRequestId, projectId)
    const second = createTranscriptReadRequest(secondRequestId, projectId)

    client.failNextSend = true
    session.send(first)
    session.send(second)

    expect(client.sent).toEqual([second])
    expect(actions.some((action) => action.type === 'sendFailed')).toBe(true)
  })

  it('fails closed when subscription setup and client cleanup both fail', () => {
    const client = new FakeTranscriptClient()
    client.throwOnSubscribe = true
    client.throwOnDispose = true
    const actions: TranscriptAction[] = []

    expect(() =>
      startTranscriptBridgeSession(
        () => client,
        (action) => actions.push(action),
      ),
    ).not.toThrow()

    expect(client.disposeCount).toBe(1)
    expect(actions).toEqual([{ type: 'bridgeUnavailable' }])
  })

  it('contains unsubscribe and client cleanup failures during disposal', () => {
    const client = new FakeTranscriptClient()
    client.throwOnUnsubscribe = true
    client.throwOnDispose = true
    const session = startTranscriptBridgeSession(() => client, () => {})

    expect(() => session.dispose()).not.toThrow()
    expect(() => session.dispose()).not.toThrow()
    expect(client.unsubscribeCount).toBe(1)
    expect(client.disposeCount).toBe(1)
  })
})

class FakeTranscriptClient implements TranscriptBridgeClient {
  readonly sent: TranscriptRequest[] = []
  disposed = false
  disposeCount = 0
  failNextSend = false
  throwOnDispose = false
  throwOnSubscribe = false
  throwOnUnsubscribe = false
  unsubscribeCount = 0
  private readonly listeners = new Set<(response: TranscriptResponse) => void>()

  sendTranscript(request: TranscriptRequest): void {
    if (this.failNextSend) {
      this.failNextSend = false
      throw new Error('transport failed')
    }
    this.sent.push(request)
  }

  onTranscriptResponse(
    listener: (response: TranscriptResponse) => void,
  ): () => void {
    if (this.throwOnSubscribe) throw new Error('subscription failed')
    this.listeners.add(listener)
    return () => {
      this.unsubscribeCount++
      if (this.throwOnUnsubscribe) throw new Error('unsubscribe failed')
      this.listeners.delete(listener)
    }
  }

  emit(response: TranscriptResponse): void {
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
  method: TranscriptResponse['method'],
): TranscriptResponse {
  const value = createTranscriptResponseValue(method)
  value.requestId = requestId
  const response = parseTranscriptResponse(value)
  if (response === null) throw new Error('Expected a valid transcript response.')
  return response
}
