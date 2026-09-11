// @vitest-environment jsdom
import { act, StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PipelineProvider, usePipeline } from './PipelineProvider'
import type { PipelineRequest, PipelineResponse } from './pipelineProtocol'
import type { PipelineBridgeClient } from './pipelineSession'
import { createPipelineSnapshot, fixtureId, pipelineProjectId, pipelineResponse } from './pipelineTestFixture'

afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('PipelineProvider stable lifecycle', () => {
  it.each([false, true])('does not restart on inline factories, same-project rerenders or routes (StrictMode=%s)', async strict => {
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    let sequence = 2000
    vi.spyOn(window.crypto, 'randomUUID').mockImplementation(() => fixtureId(++sequence) as ReturnType<Crypto['randomUUID']>)
    const posted: PipelineRequest[] = []
    let callback: ((response: PipelineResponse) => void) | null = null
    const client: PipelineBridgeClient = { sendPipeline: request => { posted.push(request) }, abandonPipelineRequest: vi.fn(), dispose: vi.fn(),
      onPipelineResponse: listener => { callback = listener; return () => { callback = null } } }
    const factory = vi.fn(() => client)
    let current: () => ReturnType<typeof usePipeline> = () => { throw new Error('Provider is not mounted.') }
    function Probe({ page }: { page: string }) { const value = usePipeline(); current = () => value; return <p>{page}: {value.state.projectId}</p> }
    const container = document.createElement('div'); document.body.append(container)
    const root = createRoot(container)
    function tree(projectId: string | null, page: string) {
      const value = <PipelineProvider projectId={projectId} createClient={() => factory()}><Probe page={page} /></PipelineProvider>
      return strict ? <StrictMode>{value}</StrictMode> : value
    }
    try {
      await act(async () => root.render(tree(pipelineProjectId, 'source')))
      expect(factory).toHaveBeenCalledOnce()
      expect(posted).toHaveLength(1)
      await act(async () => callback?.(pipelineResponse(createPipelineSnapshot(), posted[0]!.requestId)))
      const before = current().state.snapshot
      await act(async () => root.render(tree(pipelineProjectId, 'results')))
      expect(current().state.snapshot).toBe(before)
      expect(factory).toHaveBeenCalledOnce()
      await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
      expect(posted).toHaveLength(2)
      const oldRequest = posted[1]!
      await act(async () => root.render(tree(fixtureId(999), 'source')))
      expect(container.textContent).not.toContain(pipelineProjectId)
      expect(current().state.snapshot).toBeNull()
      await act(async () => callback?.(pipelineResponse(createPipelineSnapshot(), oldRequest.requestId)))
      expect(current().state.snapshot).toBeNull()
      await act(async () => root.render(tree(null, 'source')))
      const count = posted.length
      await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
      expect(posted).toHaveLength(count)
      expect(posted.every(request => request.method === 'pipeline.read')).toBe(true)
    } finally { await act(async () => root.unmount()); container.remove() }
    expect(client.dispose).toHaveBeenCalledOnce()
  })
})