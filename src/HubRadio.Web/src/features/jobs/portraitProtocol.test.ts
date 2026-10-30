// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest'
import {
  createPortraitListRequest, createPortraitRegisterRequest, createPortraitService, parsePortraitResponse, portraitFileToParams,
  type PortraitRegisterParams, type PortraitRequest, type PortraitTransport,
} from './portraitProtocol'

const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`
const entry = { id: 'char-0123456789ab', name: 'コパ', rights: 'confirmed', subtitleColor: '#FFD400', rightsNote: null }
const envelope = (result: unknown, requestId = id(1), method = 'portraits.list') => ({ version: 1, requestId, method, result })
const params: PortraitRegisterParams = { name: 'コパ', imageType: 'png', imageBase64: 'iVBORw0KGgo=', rights: 'confirmed', rightsNote: null }

function fakeTransport() {
  const sent: PortraitRequest[] = []
  let listener: ((event: { readonly data: unknown }) => void) | null = null
  const transport: PortraitTransport = {
    postMessage: message => { sent.push(message) },
    addEventListener: (_type, l) => { listener = l },
    removeEventListener: () => { listener = null },
  }
  return { transport, sent, deliver: (data: unknown) => listener?.({ data }) }
}

// FR-137 (6) AC-155 AC-160: the standing-picture bridge (portraits.list / portraits.register).
describe('portraitProtocol', () => {
  it('builds closed requests and refuses invalid ones', () => {
    expect(createPortraitListRequest(id(1))).toEqual({ version: 1, requestId: id(1), method: 'portraits.list', params: {} })
    expect(createPortraitRegisterRequest(id(2), params).params).toEqual(params)
    expect(() => createPortraitRegisterRequest(id(2), { ...params, name: ' ' })).toThrow(TypeError)
    expect(() => createPortraitRegisterRequest(id(2), { ...params, name: 'x'.repeat(41) })).toThrow(TypeError)
    expect(() => createPortraitRegisterRequest(id(2), { ...params, rights: 'maybe' as never })).toThrow(TypeError)
    expect(() => createPortraitRegisterRequest(id(2), { ...params, imageType: 'gif' as never })).toThrow(TypeError)
    expect(() => createPortraitRegisterRequest(id(2), { ...params, imageBase64: '' })).toThrow(TypeError)
    expect(() => createPortraitRegisterRequest('nope', params)).toThrow(TypeError)
  })

  it('parses only the closed result shape, keeping the saved rights state', () => {
    const ok = parsePortraitResponse(envelope({ status: 'ok', portraitCount: 1, portraits: [entry] }))
    expect(ok?.result.portraits[0]).toMatchObject({ name: 'コパ', rights: 'confirmed', subtitleColor: '#FFD400' })
    expect(parsePortraitResponse(envelope({ status: 'ok', portraitCount: 0, portraits: [] }, id(1), 'results.export'))).toBeNull()
    expect(parsePortraitResponse(envelope({ status: 'ok', portraitCount: 1, portraits: [{ ...entry, rights: 'x' }] }))).toBeNull()
    expect(parsePortraitResponse(envelope({ status: 'ok', portraitCount: 1, portraits: [{ ...entry, extra: 1 }] }))).toBeNull()
    expect(parsePortraitResponse(envelope({ status: 'ok', portraitCount: 1, portraits: [{ ...entry, subtitleColor: 'red' }] }))).toBeNull()
    expect(parsePortraitResponse(envelope({ status: 'done', portraitCount: 0, portraits: [] }))).toBeNull()
  })

  it('sends a request and resolves with the matching response; a closed or silent host reports disconnected / timeout', async () => {
    const h = fakeTransport()
    const service = createPortraitService(h.transport, { createRequestId: () => id(1) })
    const pending = service.list()
    expect(h.sent[0]).toMatchObject({ method: 'portraits.list' })
    h.deliver(envelope({ status: 'ok', portraitCount: 1, portraits: [entry] }))
    expect(await pending).toMatchObject({ status: 'ok', portraitCount: 1 })
    const registered = service.register(params)
    h.deliver(envelope({ status: 'ok', portraitCount: 1, portraits: [entry] }, id(1), 'portraits.register'))
    expect(await registered).toMatchObject({ status: 'ok' })
    service.dispose()
    expect(await service.list()).toEqual({ status: 'disconnected' })
    const slow = createPortraitService(fakeTransport().transport, { createRequestId: () => id(3), timeoutMilliseconds: 20 })
    expect(await slow.list()).toEqual({ status: 'timeout' })
  })

  it('turns a picked file into register parameters and refuses a kind or size the bridge cannot carry', async () => {
    const png = new File([new Uint8Array([137, 80, 78, 71])], 'a.png', { type: 'image/png' })
    expect(await portraitFileToParams(png, ' コパ ', 'unconfirmed')).toMatchObject({ name: 'コパ', imageType: 'png', rights: 'unconfirmed' })
    expect(await portraitFileToParams(new File([new Uint8Array([1])], 'a.gif', { type: 'image/gif' }), 'x', 'confirmed')).toBeNull()
    expect(vi.isMockFunction(portraitFileToParams)).toBe(false)
  })
})
