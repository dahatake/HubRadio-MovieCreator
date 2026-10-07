import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  createHostWorkspaceLocationService, createWorkspaceLocationReadRequest, createWorkspaceLocationService,
  createWorkspaceLocationUpdateRequest, parseWorkspaceLocationResponse, type WorkspaceLocationRequest,
  type WorkspaceLocationTransport,
} from './workspaceLocationProtocol'

const id = (n: number): string => `91000000-0000-4000-8000-${n.toString().padStart(12, '0')}`
const available = { status: 'available', currentPath: 'C:\\workspace', savedPath: null, defaultPath: 'C:\\workspace', restartRequired: false }
const failure = (status: string) => ({ status, currentPath: null, savedPath: null, defaultPath: null, restartRequired: false })
const envelope = (method: string, result: unknown, requestId = id(1)) => ({ version: 1, requestId, method, result })

function fakeTransport() {
  const listeners = new Set<(event: { readonly data: unknown }) => void>()
  const sent: WorkspaceLocationRequest[] = []
  const transport: WorkspaceLocationTransport = {
    postMessage: message => { sent.push(message) },
    addEventListener: (_type, listener) => { listeners.add(listener) },
    removeEventListener: (_type, listener) => { listeners.delete(listener) },
  }
  return { transport, sent, listeners, emit: (data: unknown) => { for (const listener of [...listeners]) listener({ data }) } }
}

// FR-062 / AC-037.4
describe('FR-062 AC-037.4 workspace location protocol', () => {
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })

  it('creates closed requests', () => {
    expect(createWorkspaceLocationReadRequest(id(1))).toEqual({ version: 1, requestId: id(1), method: 'settings.workspace.read', params: {} })
    expect(createWorkspaceLocationUpdateRequest(id(1), 'D:\\ws')).toEqual({ version: 1, requestId: id(1), method: 'settings.workspace.update', params: { path: 'D:\\ws' } })
    expect(createWorkspaceLocationUpdateRequest(id(1), null).params).toEqual({ path: null })
    expect(() => createWorkspaceLocationReadRequest('nope')).toThrow(TypeError)
    expect(() => createWorkspaceLocationUpdateRequest(id(1), 'a'.repeat(261))).toThrow(TypeError)
    expect(createWorkspaceLocationUpdateRequest(id(1), 'a'.repeat(260))).toBeDefined()
  })

  it('accepts only the closed response shapes', () => {
    expect(parseWorkspaceLocationResponse(envelope('settings.workspace.read', available))?.result).toEqual(available)
    expect(parseWorkspaceLocationResponse(envelope('settings.workspace.update', { ...available, savedPath: 'D:\\ws', restartRequired: true }))?.result.restartRequired).toBe(true)
    for (const status of ['invalid', 'forbidden', 'unavailable']) {
      expect(parseWorkspaceLocationResponse(envelope('settings.workspace.update', failure(status)))?.result.status).toBe(status)
    }
    for (const invalid of [
      envelope('settings.workspace.read', { ...available, extra: 1 }),
      envelope('settings.workspace.read', { status: 'available', currentPath: 'C:\\w', savedPath: null, defaultPath: 'C:\\w' }),
      envelope('settings.workspace.read', { ...available, currentPath: null }),
      envelope('settings.workspace.read', { ...available, currentPath: '' }),
      envelope('settings.workspace.read', { ...available, savedPath: 5 }),
      envelope('settings.workspace.read', { ...available, defaultPath: 'a'.repeat(261) }),
      envelope('settings.workspace.read', { ...available, restartRequired: 'yes' }),
      envelope('settings.workspace.read', { ...failure('invalid'), currentPath: 'C:\\leak' }),
      envelope('settings.workspace.read', { ...failure('invalid'), restartRequired: true }),
      envelope('settings.workspace.read', failure('available')),
      envelope('settings.workspace.read', failure('conflict')),
      envelope('settings.workspace.other', available),
      envelope('settings.workspace.read', available, 'not-an-id'),
      { ...envelope('settings.workspace.read', available), version: 2 },
      { ...envelope('settings.workspace.read', available), extra: true },
      null, 'text', [],
    ]) expect(parseWorkspaceLocationResponse(invalid)).toBeNull()
  })

  it('correlates responses by request ID and method', async () => {
    const h = fakeTransport()
    const service = createWorkspaceLocationService(h.transport, { createRequestId: () => id(5) })

    const read = service.read()
    expect(h.sent).toEqual([{ version: 1, requestId: id(5), method: 'settings.workspace.read', params: {} }])
    h.emit(envelope('settings.workspace.update', available, id(5)))
    h.emit(envelope('settings.workspace.read', available, id(6)))
    h.emit(envelope('settings.workspace.read', { ...available, extra: 1 }, id(5)))
    h.emit(envelope('settings.workspace.read', { ...available, currentPath: 'C:\\answered' }, id(5)))
    expect(await read).toMatchObject({ status: 'available', currentPath: 'C:\\answered' })

    const update = service.update(null)
    expect(h.sent[1]).toEqual({ version: 1, requestId: id(5), method: 'settings.workspace.update', params: { path: null } })
    h.emit(envelope('settings.workspace.update', failure('forbidden'), id(5)))
    expect(await update).toEqual(failure('forbidden'))
    service.dispose()
  })

  it('times out, reports transport failures and disposes cleanly', async () => {
    vi.useFakeTimers()
    const h = fakeTransport()
    const service = createWorkspaceLocationService(h.transport, { createRequestId: () => id(5), timeoutMilliseconds: 50 })
    const slow = service.read()
    await vi.advanceTimersByTimeAsync(60)
    expect(await slow).toEqual({ status: 'timeout' })

    const waiting = service.read()
    service.dispose()
    expect(await waiting).toEqual({ status: 'disconnected' })
    expect(h.listeners.size).toBe(0)
    expect(await service.update('D:\\ws')).toEqual({ status: 'disconnected' })

    const broken = createWorkspaceLocationService({ ...fakeTransport().transport, postMessage: () => { throw new Error('closed') } })
    expect(await broken.read()).toEqual({ status: 'disconnected' })
    broken.dispose()
  })

  it('is unavailable without the WebView2 host', () => {
    vi.stubGlobal('window', {})
    expect(createHostWorkspaceLocationService()).toBeNull()
    vi.stubGlobal('window', { chrome: { webview: { postMessage: () => undefined } } })
    expect(createHostWorkspaceLocationService()).toBeNull()
  })
})
