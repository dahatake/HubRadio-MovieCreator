import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  createBookendStitchService, createHostBookendStitchService, createStitchPickClipRequest, createStitchRunRequest,
  parseStitchResponse, STITCH_FAILURE_CODES, type StitchRequest, type StitchTransport,
} from './stitchProtocol'

const id = (n: number): string => `90000000-0000-4000-8000-${n.toString().padStart(12, '0')}`
const digest = 'c'.repeat(64)
const runParams = { projectId: id(1), executionId: id(2), artifactId: id(3), artifactDigest: digest, openingClipId: id(4), closingClipId: null }

function fakeTransport() {
  const listeners = new Set<(event: { readonly data: unknown }) => void>()
  const sent: StitchRequest[] = []
  const transport: StitchTransport = {
    postMessage: message => { sent.push(message) },
    addEventListener: (_type, listener) => { listeners.add(listener) },
    removeEventListener: (_type, listener) => { listeners.delete(listener) },
  }
  return { transport, sent, listeners, emit: (data: unknown) => { for (const listener of [...listeners]) listener({ data }) } }
}

describe('stitch protocol', () => {
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })

  it('creates closed requests and rejects malformed run parameters', () => {
    expect(createStitchPickClipRequest(id(9))).toEqual({ version: 1, requestId: id(9), method: 'stitch.pickClip', params: {} })
    expect(createStitchRunRequest(id(9), runParams)).toEqual({ version: 1, requestId: id(9), method: 'stitch.run', params: runParams })
    for (const params of [
      { ...runParams, openingClipId: null },
      { ...runParams, artifactDigest: digest.toUpperCase() },
      { ...runParams, projectId: 'abcdef00-0000-4000-8000-000000000001'.toUpperCase() },
      { ...runParams, closingClipId: 'x' },
    ]) expect(() => createStitchRunRequest(id(9), params)).toThrow(TypeError)
    expect(() => createStitchPickClipRequest('not-an-id')).toThrow(TypeError)
  })

  it('accepts only closed response shapes and bare file names', () => {
    const envelope = (method: string, result: unknown) => ({ version: 1, requestId: id(7), method, result })
    expect(parseStitchResponse(envelope('stitch.pickClip', { status: 'selected', clipId: id(4), fileName: 'op.mp4' }))?.result)
      .toEqual({ status: 'selected', clipId: id(4), fileName: 'op.mp4' })
    expect(parseStitchResponse(envelope('stitch.run', { status: 'completed', code: null, outputFileName: 'a.mp4', manifestFileName: 'a.stitch.json', durationSeconds: 5.5 }))?.result.status)
      .toBe('completed')
    for (const code of STITCH_FAILURE_CODES)
      expect(parseStitchResponse(envelope('stitch.run', { status: 'failed', code, outputFileName: null, manifestFileName: null, durationSeconds: null }))).not.toBeNull()
    for (const invalid of [
      envelope('stitch.pickClip', { status: 'selected', clipId: id(4), fileName: 'C:\\Users\\a\\op.mp4' }),
      envelope('stitch.pickClip', { status: 'selected', clipId: id(4), fileName: 'dir/op.mp4' }),
      envelope('stitch.pickClip', { status: 'cancelled', clipId: id(4), fileName: null }),
      envelope('stitch.pickClip', { status: 'selected', clipId: id(4), fileName: 'op.mp4', path: 'x' }),
      envelope('stitch.run', { status: 'failed', code: 'stitch.usage', outputFileName: null, manifestFileName: null, durationSeconds: null }),
      envelope('stitch.run', { status: 'completed', code: null, outputFileName: 'a.mp4', manifestFileName: 'a.stitch.json', durationSeconds: 0 }),
      envelope('stitch.run', { status: 'busy', code: 'stitch.timeout', outputFileName: null, manifestFileName: null, durationSeconds: null }),
      envelope('stitch.run', { status: 'unknown', code: null, outputFileName: null, manifestFileName: null, durationSeconds: null }),
      envelope('stitch.other', { status: 'busy' }),
      { ...envelope('stitch.run', { status: 'busy', code: null, outputFileName: null, manifestFileName: null, durationSeconds: null }), version: 2 },
    ]) expect(parseStitchResponse(invalid)).toBeNull()
  })

  it('correlates responses by request ID and method, then times out and disposes cleanly', async () => {
    vi.useFakeTimers()
    const { transport, sent, listeners, emit } = fakeTransport()
    let next = 10
    const service = createBookendStitchService(transport, { createRequestId: () => id(next++), pickTimeoutMilliseconds: 1000, runTimeoutMilliseconds: 2000 })
    const picked = service.pickClip()
    expect(sent[0]).toEqual({ version: 1, requestId: id(10), method: 'stitch.pickClip', params: {} })
    emit({ version: 1, requestId: id(99), method: 'stitch.pickClip', result: { status: 'cancelled', clipId: null, fileName: null } })
    emit({ version: 1, requestId: id(10), method: 'stitch.run', result: { status: 'busy', code: null, outputFileName: null, manifestFileName: null, durationSeconds: null } })
    emit({ version: 1, requestId: id(10), method: 'stitch.pickClip', result: { status: 'selected', clipId: id(5), fileName: 'op.mp4' } })
    await expect(picked).resolves.toEqual({ status: 'selected', clipId: id(5), fileName: 'op.mp4' })

    const running = service.run(runParams)
    expect(sent[1]).toMatchObject({ method: 'stitch.run', params: runParams })
    await vi.advanceTimersByTimeAsync(2000)
    await expect(running).resolves.toEqual({ status: 'timeout' })

    const abandoned = service.pickClip()
    service.dispose()
    await expect(abandoned).resolves.toEqual({ status: 'disconnected' })
    expect(listeners.size).toBe(0)
    await expect(service.pickClip()).resolves.toEqual({ status: 'disconnected' })
  })

  it('uses the documented default waits and reports no host outside WebView2', async () => {
    vi.useFakeTimers()
    const { transport } = fakeTransport()
    const service = createBookendStitchService(transport, { createRequestId: () => id(20) })
    const picked = service.pickClip()
    await vi.advanceTimersByTimeAsync(599_999)
    let settled = false
    void picked.then(() => { settled = true })
    await Promise.resolve()
    expect(settled).toBe(false)
    await vi.advanceTimersByTimeAsync(1)
    await expect(picked).resolves.toEqual({ status: 'timeout' })
    const running = service.run(runParams)
    await vi.advanceTimersByTimeAsync(3_659_999)
    let runSettled = false
    void running.then(() => { runSettled = true })
    await Promise.resolve()
    expect(runSettled).toBe(false)
    await vi.advanceTimersByTimeAsync(1)
    await expect(running).resolves.toEqual({ status: 'timeout' })
    expect(createHostBookendStitchService()).toBeNull()
  })
})
