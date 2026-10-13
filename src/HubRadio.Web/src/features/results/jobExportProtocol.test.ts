import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  createHostJobExportService, createJobExportRequest, createJobExportService, parseJobExportResponse,
  type JobExportRequest, type JobExportTransport,
} from './jobExportProtocol'

const id = (n: number): string => `92000000-0000-4000-8000-${n.toString().padStart(12, '0')}`
const params = { projectId: id(1), executionId: id(2), destination: 'D:\\out', jobName: 'Job 1' }
const empty = { status: 'completed', destinationReason: null, savedCount: 0, files: [], failures: [] }
const envelope = (result: unknown, requestId = id(9), method = 'results.export') => ({ version: 1, requestId, method, result })

function fakeTransport() {
  const listeners = new Set<(event: { readonly data: unknown }) => void>()
  const sent: JobExportRequest[] = []
  const transport: JobExportTransport = {
    postMessage: message => { sent.push(message) },
    addEventListener: (_type, listener) => { listeners.add(listener) },
    removeEventListener: (_type, listener) => { listeners.delete(listener) },
  }
  return { transport, sent, listeners, emit: (data: unknown) => { for (const listener of [...listeners]) listener({ data }) } }
}

// FR-132 AC-136 AC-138: the request/response pair is closed; the count always equals the saved files.
describe('FR-132 AC-136 AC-138 job export protocol', () => {
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })

  it('creates a closed request and rejects malformed input', () => {
    expect(createJobExportRequest(id(9), params)).toEqual({ version: 1, requestId: id(9), method: 'results.export', params })
    expect(createJobExportRequest(id(9), { ...params, jobName: null }).params.jobName).toBeNull()
    expect(() => createJobExportRequest('nope', params)).toThrow(TypeError)
    expect(() => createJobExportRequest(id(9), { ...params, destination: '' })).toThrow(TypeError)
    expect(() => createJobExportRequest(id(9), { ...params, destination: 'x'.repeat(261) })).toThrow(TypeError)
    expect(() => createJobExportRequest(id(9), { ...params, jobName: 'x'.repeat(41) })).toThrow(TypeError)
    expect(() => createJobExportRequest(id(9), { ...params, projectId: 'bad' })).toThrow(TypeError)
  })

  it('parses only closed results whose count matches the saved files', () => {
    const files = [{ kind: 'longClean', fileName: 'p_Job 1_長尺クリーン.mp4' }, { kind: 'thumbnailLandscape', fileName: 'p_Job 1_サムネイル.png' }]
    const failures = [{ kind: 'shortBurnIn', reason: 'diskFull' }]
    const ok = (result: unknown) => parseJobExportResponse(envelope(result))
    expect(ok({ ...empty, savedCount: 2, files, failures })?.result.savedCount).toBe(2)
    expect(ok(empty)?.result.status).toBe('completed')
    expect(ok({ ...empty, status: 'forbidden' })?.result.status).toBe('forbidden')
    expect(ok({ ...empty, status: 'destinationInvalid', destinationReason: 'notWritable' })?.result.destinationReason).toBe('notWritable')
    for (const bad of [
      { ...empty, savedCount: 1 }, // count does not match
      { ...empty, savedCount: 2, files: [files[0]] },
      { ...empty, status: 'forbidden', savedCount: 1, files: [files[0]] }, // nothing saved when refused
      { ...empty, status: 'destinationInvalid' }, // reason is required
      { ...empty, destinationReason: 'notFound' }, // reason only with destinationInvalid
      { ...empty, status: 'other' },
      { ...empty, savedCount: 1, files: [{ kind: 'longClean', fileName: 'C:\\leak\\a.mp4' }] }, // never a path
      { ...empty, failures: [{ kind: 'longClean', reason: 'boom: System.IO.IOException' }] }, // closed reasons only
      { ...empty, extra: 1 },
    ]) expect(ok(bad)).toBeNull()
    expect(parseJobExportResponse(envelope(empty, id(9), 'stitch.run'))).toBeNull()
  })

  it('correlates the response by request ID and times out', async () => {
    const h = fakeTransport()
    const service = createJobExportService(h.transport, { createRequestId: () => id(9) })
    const pending = service.export(params)
    expect(h.sent[0]).toEqual({ version: 1, requestId: id(9), method: 'results.export', params })
    h.emit(envelope(empty, id(8))) // someone else's response is ignored
    h.emit(envelope({ ...empty, status: 'busy' }, id(9)))
    expect((await pending).status).toBe('busy')

    vi.useFakeTimers()
    const slow = createJobExportService(fakeTransport().transport, { createRequestId: () => id(7), timeoutMilliseconds: 50 }).export(params)
    await vi.advanceTimersByTimeAsync(60)
    expect(await slow).toEqual({ status: 'timeout' })
    service.dispose()
  })

  it('reports disconnected after dispose or a failed post, and finds no host outside WebView2', async () => {
    const h = fakeTransport()
    const service = createJobExportService(h.transport)
    service.dispose()
    expect(h.listeners.size).toBe(0)
    expect(await service.export(params)).toEqual({ status: 'disconnected' })

    const broken = createJobExportService({ ...fakeTransport().transport, postMessage: () => { throw new Error('closed') } })
    expect(await broken.export(params)).toEqual({ status: 'disconnected' })
    expect(createHostJobExportService()).toBeNull()
  })
})
