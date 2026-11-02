import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  createDisclosureAddLabelRequest, createDisclosureListRequest, createDisclosureService, parseDisclosureResponse,
  type DisclosureRequest, type DisclosureTransport,
} from './disclosureProtocol'

const id = (n: number): string => `95000000-0000-4000-8000-${n.toString().padStart(12, '0')}`
const element = { kind: 'AutoSubtitle', elementRef: 'subtitle-track', provider: 'p', modelIdentifier: 'm', modelVersion: '1', recordedAt: '2033-04-05T06:07:08.0000000+09:00' }
const label = { destination: 'YouTube', labelText: 'AI 字幕を含みます', addedAt: '2033-04-05T06:08:08.0000000+09:00' }
const envelope = (method: string, result: unknown, requestId = id(9)) => ({ version: 1, requestId, method, result })

function fakeTransport() {
  const listeners = new Set<(event: { readonly data: unknown }) => void>()
  const sent: DisclosureRequest[] = []
  const transport: DisclosureTransport = {
    postMessage: message => { sent.push(message) },
    addEventListener: (_type, listener) => { listeners.add(listener) },
    removeEventListener: (_type, listener) => { listeners.delete(listener) },
  }
  return { transport, sent, listeners, emit: (data: unknown) => { for (const listener of [...listeners]) listener({ data }) } }
}

describe('NFR-BR-005 AC-126 disclosure protocol', () => {
  afterEach(() => { vi.useRealTimers() })

  it('creates closed requests and rejects malformed ones', () => {
    expect(createDisclosureListRequest(id(1), id(2))).toEqual({ version: 1, requestId: id(1), method: 'disclosure.list', params: { projectId: id(2) } })
    expect(createDisclosureAddLabelRequest(id(1), id(2), 'YouTube', 'x').params).toEqual({ projectId: id(2), destination: 'YouTube', labelText: 'x' })
    expect(() => createDisclosureListRequest('nope', id(2))).toThrow(TypeError)
    expect(() => createDisclosureAddLabelRequest(id(1), id(2), ' ', 'x')).toThrow(TypeError)
    expect(() => createDisclosureAddLabelRequest(id(1), id(2), 'd'.repeat(65), 'x')).toThrow(TypeError)
    expect(() => createDisclosureAddLabelRequest(id(1), id(2), 'YouTube', 'l'.repeat(201))).toThrow(TypeError)
  })

  it('parses only closed responses', () => {
    const ok = parseDisclosureResponse(envelope('disclosure.list', { status: 'listed', elements: [element], labels: [label] }))
    expect(ok?.result.elements[0]?.kind).toBe('AutoSubtitle')
    expect(ok?.result.labels[0]?.destination).toBe('YouTube')
    for (const bad of [
      envelope('disclosure.list', { status: 'listed', elements: [{ ...element, kind: 'Other' }], labels: [] }),
      envelope('disclosure.list', { status: 'listed', elements: [{ ...element, path: 'C:\\x' }], labels: [] }),
      envelope('disclosure.list', { status: 'forbidden', elements: [element], labels: [] }),
      envelope('disclosure.list', { status: 'maybe', elements: [], labels: [] }),
      envelope('disclosure.list', { status: 'listed', elements: [], labels: [], extra: 1 }),
      envelope('stitch.run', { status: 'listed', elements: [], labels: [] }),
      { ...envelope('disclosure.list', { status: 'listed', elements: [], labels: [] }), version: 2 },
    ]) expect(parseDisclosureResponse(bad)).toBeNull()
  })

  it('correlates responses by request id and times out', async () => {
    vi.useFakeTimers()
    const { transport, sent, emit, listeners } = fakeTransport()
    const service = createDisclosureService(transport, { createRequestId: () => id(10), timeoutMilliseconds: 1000 })
    const listed = service.list(id(2))
    expect(sent[0]).toEqual({ version: 1, requestId: id(10), method: 'disclosure.list', params: { projectId: id(2) } })
    emit(envelope('disclosure.list', { status: 'listed', elements: [], labels: [] }, id(99)))
    emit(envelope('disclosure.list', { status: 'listed', elements: [element], labels: [label] }, id(10)))
    expect((await listed).status).toBe('listed')
    const added = service.addLabel(id(2), 'YouTube', 'x')
    expect(sent[1]).toMatchObject({ method: 'disclosure.addLabel' })
    await vi.advanceTimersByTimeAsync(1000)
    expect(await added).toEqual({ status: 'timeout' })
    service.dispose()
    expect(listeners.size).toBe(0)
    expect(await service.list(id(2))).toEqual({ status: 'disconnected' })
  })
})
