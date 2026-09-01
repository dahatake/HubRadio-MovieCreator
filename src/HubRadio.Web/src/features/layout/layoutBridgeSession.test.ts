import { describe, expect, it } from 'vitest'

import {
  LAYOUT_APPLY_METHOD,
  createLayoutApplyRequest,
  createLayoutReadRequest,
  parseLayoutBridgeResponse,
  type LayoutBridgeRequest,
  type LayoutBridgeResponse,
} from './layoutProtocol'
import {
  startLayoutBridgeSession,
  type LayoutBridgeClient,
} from './layoutBridgeSession'
import {
  layoutBridgeUnavailable,
  layoutRequestStarted,
  layoutResponseReceived,
  type LayoutUiAction,
} from './layoutState'
import {
  createLayoutFailureValue,
  createLayoutResponseValue,
  layoutCueDigest,
  layoutCueId,
  layoutCueRevisionId,
  layoutProjectId,
  layoutRequestId,
  layoutSecondProjectId,
  layoutSecondRequestId,
} from './layoutTestFixture'

describe('layout bridge session', () => {
  it('correlates request, method, and project while rejecting double submit', () => {
    const client = new FakeClient()
    const actions: LayoutUiAction[] = []
    const session = startLayoutBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const read = createLayoutReadRequest(
      layoutRequestId,
      layoutProjectId,
      null,
    )
    const apply = createApplyRequest()

    expect(session.send(read)).toBe(true)
    expect(session.send(apply)).toBe(false)
    expect(client.sent).toEqual([read])
    expect(actions).toEqual([
      layoutRequestStarted(layoutRequestId, 'layout.read', layoutProjectId),
    ])

    client.emit(parseRequired(createLayoutResponseValue(layoutSecondRequestId)))
    client.emit(parseRequired(createLayoutResponseValue(
      layoutRequestId,
      LAYOUT_APPLY_METHOD,
    )))
    const wrongProject = createLayoutResponseValue(layoutRequestId)
    wrongProject.result.snapshot.projectId = layoutSecondProjectId
    client.emit(parseRequired(wrongProject))
    expect(actions).toHaveLength(1)

    const accepted = parseRequired(createLayoutResponseValue(layoutRequestId))
    client.emit(accepted)
    expect(actions.at(-1)).toEqual(layoutResponseReceived(accepted))

    expect(session.send(apply)).toBe(true)
    const applied = parseRequired(createLayoutResponseValue(
      layoutSecondRequestId,
      LAYOUT_APPLY_METHOD,
    ))
    client.emit(applied)
    expect(actions.at(-1)).toEqual(layoutResponseReceived(applied))
  })

  it('accepts a correlated closed failure and clears the pending slot', () => {
    const client = new FakeClient()
    const actions: LayoutUiAction[] = []
    const session = startLayoutBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const read = createLayoutReadRequest(
      layoutRequestId,
      layoutProjectId,
      null,
    )
    session.send(read)
    const failure = parseRequired(createLayoutFailureValue(
      'conflict',
      layoutRequestId,
    ))
    client.emit(failure)
    expect(actions.at(-1)).toEqual(layoutResponseReceived(failure))

    expect(session.send(createLayoutReadRequest(
      layoutSecondRequestId,
      layoutProjectId,
      null,
    ))).toBe(true)
  })

  it('abandons correlation and emits only a fixed private-safe error on send failure', () => {
    const client = new FakeClient()
    const actions: LayoutUiAction[] = []
    const session = startLayoutBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    client.throwOnSend = true
    const request = createLayoutReadRequest(
      layoutRequestId,
      layoutProjectId,
      null,
    )

    expect(session.send(request)).toBe(false)
    expect(client.abandoned).toEqual([layoutRequestId])
    expect(actions).toEqual([
      layoutRequestStarted(layoutRequestId, 'layout.read', layoutProjectId),
      layoutBridgeUnavailable(),
    ])
    expect(JSON.stringify(actions)).not.toContain('C:\\private')

    client.throwOnSend = false
    expect(session.send(request)).toBe(true)
  })

  it('maps client creation and subscription failures to an unavailable session', () => {
    const createActions: LayoutUiAction[] = []
    const unavailable = startLayoutBridgeSession(
      () => {
        throw new Error('C:\\private\\layout.db')
      },
      (action) => createActions.push(action),
    )
    expect(createActions).toEqual([layoutBridgeUnavailable()])

    const request = createLayoutReadRequest(
      layoutRequestId,
      layoutProjectId,
      null,
    )
    expect(unavailable.send(request)).toBe(false)
    expect(createActions.slice(-2)).toEqual([
      layoutRequestStarted(layoutRequestId, 'layout.read', layoutProjectId),
      layoutBridgeUnavailable(),
    ])

    const client = new FakeClient()
    client.throwOnSubscribe = true
    client.throwOnDispose = true
    const subscribeActions: LayoutUiAction[] = []
    startLayoutBridgeSession(
      () => client,
      (action) => subscribeActions.push(action),
    )
    expect(client.disposeCount).toBe(1)
    expect(subscribeActions).toEqual([layoutBridgeUnavailable()])
  })

  it('abandons one pending request and cleans up exactly once on disposal', () => {
    const client = new FakeClient()
    const actions: LayoutUiAction[] = []
    const session = startLayoutBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const request = createLayoutReadRequest(
      layoutRequestId,
      layoutProjectId,
      null,
    )
    session.send(request)

    client.throwOnUnsubscribe = true
    client.throwOnDispose = true
    expect(() => session.dispose()).not.toThrow()
    expect(() => session.dispose()).not.toThrow()
    expect(client.abandoned).toEqual([layoutRequestId])
    expect(client.unsubscribeCount).toBe(1)
    expect(client.disposeCount).toBe(1)
    expect(session.send(request)).toBe(false)
    expect(actions.at(-1)).toEqual(layoutBridgeUnavailable())
  })
})

class FakeClient implements LayoutBridgeClient {
  readonly sent: LayoutBridgeRequest[] = []
  readonly abandoned: string[] = []
  throwOnSend = false
  throwOnSubscribe = false
  throwOnUnsubscribe = false
  throwOnDispose = false
  unsubscribeCount = 0
  disposeCount = 0
  private listener: ((response: LayoutBridgeResponse) => void) | null = null

  sendLayout(request: LayoutBridgeRequest): void {
    if (this.throwOnSend) {
      throw new Error('native stack C:\\private\\layout.db')
    }
    this.sent.push(request)
  }

  abandonLayoutRequest(requestId: string): void {
    this.abandoned.push(requestId)
  }

  onLayoutResponse(
    listener: (response: LayoutBridgeResponse) => void,
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

  emit(response: LayoutBridgeResponse): void {
    this.listener?.(response)
  }
}

function createApplyRequest(): LayoutBridgeRequest {
  return createLayoutApplyRequest(
    layoutSecondRequestId,
    layoutProjectId,
    3,
    {
      kind: 'editSubtitle',
      cueId: layoutCueId,
      expectedRevisionId: layoutCueRevisionId,
      expectedRevisionDigest: layoutCueDigest,
      edit: { kind: 'text', text: '更新字幕' },
      reason: '字幕を修正します。',
    },
  )
}

function parseRequired(value: unknown): LayoutBridgeResponse {
  const parsed = parseLayoutBridgeResponse(value)
  if (parsed === null) {
    throw new Error('Expected a valid layout response fixture.')
  }
  return parsed
}
