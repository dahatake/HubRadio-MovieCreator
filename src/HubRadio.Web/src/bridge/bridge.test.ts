import { describe, expect, it, vi } from 'vitest'

import {
  createBridgeClient,
  createHostBridgeClient,
  type BridgeMessageEvent,
  type BridgeTransport,
} from './index'
import {
  BRIDGE_PING_METHOD,
  BRIDGE_PROTOCOL_VERSION,
  createPingRequest,
  parsePingResponse,
} from './protocol'
import {
  PROJECT_LIST_METHOD,
  createProjectListRequest,
} from './projectProtocol'

const requestId = '00112233-4455-6677-8899-aabbccddeeff'

describe('web bridge protocol', () => {
  it('creates only the closed allowlisted ping request', () => {
    const request = createPingRequest(requestId)

    expect(request).toEqual({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId,
      method: BRIDGE_PING_METHOD,
      params: {},
    })
    expect(Object.isFrozen(request)).toBe(true)
    expect(Object.isFrozen(request.params)).toBe(true)
  })

  it.each([
    '',
    '00000000-0000-0000-0000-000000000000',
    '00112233-4455-6677-8899-AABBCCDDEEFF',
    'not-a-uuid',
  ])('rejects invalid request ID %s', (invalidRequestId) => {
    expect(() => createPingRequest(invalidRequestId)).toThrow()
  })

  it('accepts an exact ping response and rejects schema or method changes', () => {
    const response = {
      version: 1,
      requestId,
      method: 'bridge.ping',
      result: { protocolVersion: 1 },
    }

    expect(parsePingResponse(response)).toEqual(response)
    expect(parsePingResponse({ ...response, extra: true })).toBeNull()
    expect(parsePingResponse({ ...response, method: 'project.open' })).toBeNull()
    expect(parsePingResponse({ ...response, result: { protocolVersion: 1, extra: true } })).toBeNull()
    expect(parsePingResponse({ ...response, requestId: requestId.toUpperCase() })).toBeNull()
    expect(parsePingResponse([])).toBeNull()
    expect(parsePingResponse(null)).toBeNull()
  })
})

describe('web bridge client', () => {
  it('wraps the transport without returning it and filters incoming messages', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onPingResponse(listener)

    client.sendPing(requestId)
    expect(transport.posted).toEqual([createPingRequest(requestId)])
    expect(client).not.toBe(transport)

    transport.emit({ method: 'project.open' })
    expect(listener).not.toHaveBeenCalled()

    transport.emit({
      version: 1,
      requestId,
      method: 'bridge.ping',
      result: { protocolVersion: 1 },
    })
    expect(listener).toHaveBeenCalledOnce()
  })

  it('unsubscribes listeners and removes the transport handler on disposal', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    const unsubscribe = client.onPingResponse(listener)
    unsubscribe()

    transport.emit({
      version: 1,
      requestId,
      method: 'bridge.ping',
      result: { protocolVersion: 1 },
    })
    expect(listener).not.toHaveBeenCalled()

    client.dispose()
    client.dispose()
    expect(transport.listenerCount).toBe(0)
    expect(() => client.sendPing(requestId)).toThrow('disposed')
    expect(() => client.onPingResponse(listener)).toThrow('disposed')
  })

  it('sends and emits only strict project protocol messages', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onProjectResponse(listener)
    const request = createProjectListRequest(requestId)

    client.sendProject(request)
    expect(transport.posted).toEqual([request])

    transport.emit({
      version: 1,
      requestId,
      method: PROJECT_LIST_METHOD,
      result: {
        projects: [],
        catalog: { outputProfiles: [], brandTemplates: [] },
        canCreate: true,
        extra: true,
      },
    })
    expect(listener).not.toHaveBeenCalled()

    transport.emit({
      version: 1,
      requestId,
      method: PROJECT_LIST_METHOD,
      result: {
        projects: [],
        catalog: { outputProfiles: [], brandTemplates: [] },
        canCreate: true,
      },
    })
    expect(listener).toHaveBeenCalledOnce()

    client.dispose()
    expect(() => client.sendProject(request)).toThrow('disposed')
    expect(() => client.onProjectResponse(listener)).toThrow('disposed')
  })

  it('does not fabricate a host transport outside WebView2', () => {
    expect(() => createHostBridgeClient()).toThrow('outside a browser window')
  })
})

class FakeTransport implements BridgeTransport {
  readonly posted: unknown[] = []
  private readonly listeners = new Set<(event: BridgeMessageEvent) => void>()

  get listenerCount(): number {
    return this.listeners.size
  }

  postMessage(message: Parameters<BridgeTransport['postMessage']>[0]): void {
    this.posted.push(message)
  }

  addEventListener(
    type: 'message',
    listener: (event: BridgeMessageEvent) => void,
  ): void {
    expect(type).toBe('message')
    this.listeners.add(listener)
  }

  removeEventListener(
    type: 'message',
    listener: (event: BridgeMessageEvent) => void,
  ): void {
    expect(type).toBe('message')
    this.listeners.delete(listener)
  }

  emit(data: unknown): void {
    for (const listener of this.listeners) {
      listener({ data })
    }
  }
}