// @vitest-environment jsdom
import { fireEvent } from '@testing-library/dom'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { GeneratedResultPreview, type GeneratedPreviewSeek } from './pipelinePresentation'
import { revokePipelineMedia, verifiedPipelineResponse } from './pipelineProtocol'
import { createCompletedPipeline, createPipelineArtifact, pipelineResponse, setPipelineWarningTarget } from './pipelineTestFixture'

describe('generated output seek completion (DOM events, not native/media verification)', () => {
  let root: Root
  let container: HTMLDivElement
  beforeEach(() => {
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    container = document.createElement('div'); document.body.append(container)
    root = createRoot(container)
  })
  afterEach(async () => {
    await act(async () => root.unmount())
    container.remove(); vi.restoreAllMocks(); vi.useRealTimers(); vi.unstubAllGlobals()
  })

  function fixture() {
    const value = createCompletedPipeline()
    setPipelineWarningTarget(value.result!, { kind: 'Preview', timeBasis: 'Output', time: { value: 2, rate: 1 }, settingKey: null })
    value.result!.artifacts.push(createPipelineArtifact(value.result!, 'LandscapeBurnIn', 50))
    const snapshot = verifiedPipelineResponse(pipelineResponse(value))!.result.snapshot!
    const result = snapshot.result!
    const artifact = result.artifacts[0]!
    const warning = artifact.warnings[0]!
    const seek: GeneratedPreviewSeek = { warningId: warning.id, target: warning.target }
    const handled = vi.fn()
    return { snapshot, result, artifact, seek, handled,
      async render(intent: GeneratedPreviewSeek | null = seek, selected = artifact, active = true) {
        await act(async () => root.render(<GeneratedResultPreview result={result} artifact={selected} active={active}
          seek={intent} onHandled={(request, error) => handled(request, error)} />))
      },
    }
  }
  function video(): HTMLVideoElement {
    const element = container.querySelector('video')
    if (element === null) throw new Error('Expected a generated video.')
    return element
  }

  it('queues a jsdom selectionchange task on focus even without a seek intent', async () => {
    const h = fixture(); await h.render(null)
    const element = video()
    const selectionChanged = vi.fn()
    document.addEventListener('selectionchange', selectionChanged)
    try {
      expect(vi.getTimerCount()).toBe(0)
      act(() => element.focus({ preventScroll: true }))
      expect(document.activeElement).toBe(element)
      // jsdom HTMLOrSVGElementImpl.focus -> SelectionImpl._associateRange
      // schedules selectionchange with setTimeout(..., 0), not a seek deadline.
      expect(vi.getTimerCount()).toBe(1)
      expect(selectionChanged).not.toHaveBeenCalled()
      await act(async () => { await vi.advanceTimersByTimeAsync(0) })
      expect(selectionChanged).toHaveBeenCalledOnce()
      expect(vi.getTimerCount()).toBe(0)
      expect(h.handled).not.toHaveBeenCalled()
    } finally {
      document.removeEventListener('selectionchange', selectionChanged)
    }
  })

  it('does not complete or focus on assignment/metadata/timeupdate; requires one exact seeked event', async () => {
    const h = fixture(); await h.render(null)
    const element = video()
    Object.defineProperty(element, 'duration', { configurable: true, value: 10 })
    const schedule = vi.spyOn(globalThis, 'setTimeout')
    const clear = vi.spyOn(globalThis, 'clearTimeout')
    const focus = vi.spyOn(element, 'focus')
    await h.render()
    expect(schedule).toHaveBeenCalledExactlyOnceWith(expect.any(Function), 30_000)
    const deadline = schedule.mock.results[0]!.value
    await act(async () => { fireEvent.seeked(element) }) // No seek was applied yet.
    expect(h.handled).not.toHaveBeenCalled()
    await act(async () => { fireEvent.loadedMetadata(element); fireEvent.timeUpdate(element) })
    expect(element.currentTime).toBe(2)
    expect(h.handled).not.toHaveBeenCalled()
    expect(document.activeElement).not.toBe(element)
    await act(async () => { fireEvent.seeked(element); fireEvent.seeked(element) })
    expect(h.handled).toHaveBeenCalledExactlyOnceWith(h.seek, null)
    expect(document.activeElement).toBe(element)
    expect(clear).toHaveBeenCalledExactlyOnceWith(deadline)
    expect(focus).toHaveBeenCalledExactlyOnceWith({ preventScroll: true })
    expect(clear.mock.invocationCallOrder[0]!).toBeLessThan(focus.mock.invocationCallOrder[0]!)
    // Drain only the focus-triggered 0ms selectionchange task; a leaked 30s
    // deadline must still fail the zero-timer assertion below.
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    expect(vi.getTimerCount()).toBe(0)
  })

  it('pauses playback before seeking so completion can confirm the exact requested time without autoplay', async () => {
    const h = fixture(); await h.render(null)
    const element = video()
    let paused = false
    const pause = vi.fn(() => { paused = true })
    Object.defineProperties(element, {
      duration: { configurable: true, value: 10 }, readyState: { configurable: true, value: 1 },
      paused: { configurable: true, get: () => paused }, pause: { configurable: true, value: pause },
    })
    await h.render()
    expect(pause).toHaveBeenCalledOnce()
    expect(element.currentTime).toBe(2)
    expect(h.handled).not.toHaveBeenCalled()
    await act(async () => { fireEvent.seeked(element) })
    expect(h.handled).toHaveBeenCalledExactlyOnceWith(h.seek, null)
    expect(paused).toBe(true)
    expect(element.autoplay).toBe(false)
  })

  it.each([NaN, Infinity, -Infinity, 0, 2])('rejects invalid/boundary duration %s without a nearby-time fallback', async duration => {
    const h = fixture(); await h.render(null)
    const element = video()
    Object.defineProperty(element, 'duration', { configurable: true, value: duration })
    await h.render()
    await act(async () => { fireEvent.loadedMetadata(element); fireEvent.seeked(element) })
    expect(h.handled).toHaveBeenCalledExactlyOnceWith(h.seek, expect.any(String))
    expect(element.currentTime).toBe(0)
    expect(document.activeElement).not.toBe(element)
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(['time', 'nonfinite time', 'duration', 'seeking', 'src', 'currentSrc', 'revoked'] as const)(
    'fails a seeked event whose current %s no longer matches', async change => {
      const h = fixture(); await h.render(null)
      const element = video()
      Object.defineProperties(element, { duration: { configurable: true, value: 10 }, readyState: { configurable: true, value: 1 } })
      await h.render()
      expect(element.currentTime).toBe(2)
      if (change === 'time') element.currentTime = 2.000001
      if (change === 'nonfinite time') Object.defineProperty(element, 'currentTime', { configurable: true, value: NaN })
      if (change === 'duration') Object.defineProperty(element, 'duration', { configurable: true, value: Infinity })
      if (change === 'seeking') Object.defineProperty(element, 'seeking', { configurable: true, value: true })
      if (change === 'src') element.setAttribute('src', 'https://example.invalid/different.mp4')
      if (change === 'currentSrc') Object.defineProperty(element, 'currentSrc', { configurable: true, value: 'https://example.invalid/different.mp4' })
      if (change === 'revoked') revokePipelineMedia(h.snapshot)
      await act(async () => { fireEvent.seeked(element); fireEvent.seeked(element) })
      expect(h.handled).toHaveBeenCalledExactlyOnceWith(h.seek, expect.any(String))
      expect(document.activeElement).not.toBe(element)
      expect(vi.getTimerCount()).toBe(0)
    })

  it('never completes an old media intent after selecting another artifact', async () => {
    const h = fixture(); await h.render(null)
    const old = video()
    Object.defineProperties(old, { duration: { configurable: true, value: 10 }, readyState: { configurable: true, value: 1 } })
    await h.render()
    await h.render(h.seek, h.result.artifacts[1]!)
    const current = video()
    expect(current).not.toBe(old)
    expect(current.currentTime).toBe(0)
    expect(h.handled).toHaveBeenCalledExactlyOnceWith(h.seek, expect.any(String))
    await act(async () => { fireEvent.seeked(old); fireEvent.seeked(current) })
    expect(h.handled).toHaveBeenCalledTimes(1)
    expect(document.activeElement).not.toBe(current)
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each([false, true])('keeps its deadline across callback rerenders and requires a new intent after timeout (metadata=%s)', async metadata => {
    const h = fixture(); await h.render(null)
    const element = video()
    Object.defineProperties(element, { duration: { configurable: true, value: 10 }, readyState: { configurable: true, value: metadata ? 1 : 0 } })
    await h.render()
    await act(async () => { await vi.advanceTimersByTimeAsync(29_999) })
    await h.render()
    expect(h.handled).not.toHaveBeenCalled()
    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(h.handled).toHaveBeenCalledExactlyOnceWith(h.seek, expect.stringContaining('時間内'))
    expect(vi.getTimerCount()).toBe(0)
    await act(async () => { fireEvent.loadedMetadata(element); fireEvent.seeked(element) })
    await h.render()
    expect(h.handled).toHaveBeenCalledTimes(1)
    const schedule = vi.spyOn(globalThis, 'setTimeout')
    const clear = vi.spyOn(globalThis, 'clearTimeout')
    const repeated = { ...h.seek }
    await h.render(repeated)
    expect(schedule).toHaveBeenCalledExactlyOnceWith(expect.any(Function), 30_000)
    const deadline = schedule.mock.results[0]!.value
    expect(h.handled).toHaveBeenCalledTimes(1)
    await act(async () => { fireEvent.loadedMetadata(element); fireEvent.seeked(element) })
    expect(h.handled).toHaveBeenLastCalledWith(repeated, null)
    expect(h.handled).toHaveBeenCalledTimes(2)
    expect(clear).toHaveBeenCalledExactlyOnceWith(deadline)
    // Keep the cleanup assertion without counting jsdom's focus notification.
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(['error', 'hidden', 'unmounted'] as const)('cleans up the pending timer on %s and ignores late seeked events', async outcome => {
    const h = fixture(); await h.render(null)
    const element = video()
    Object.defineProperty(element, 'duration', { configurable: true, value: 10 })
    await h.render()
    expect(vi.getTimerCount()).toBe(1)
    if (outcome === 'error') await act(async () => { fireEvent.error(element) })
    else if (outcome === 'hidden') await h.render(null, h.artifact, false)
    else await act(async () => root.render(null))
    expect(vi.getTimerCount()).toBe(0)
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000); fireEvent.seeked(element) })
    expect(h.handled).toHaveBeenCalledTimes(outcome === 'error' ? 1 : 0)
    if (outcome === 'error') expect(h.handled).toHaveBeenLastCalledWith(h.seek, expect.any(String))
  })
})