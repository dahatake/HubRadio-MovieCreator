// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ProxyPreview } from './ProxyPreview'
import {
  createProxyService, parseProxyResponse, PROXY_GET_METHOD, PROXY_REGENERATE_METHOD,
  type ProxyOutcome, type ProxyRequest, type ProxyService, type ProxyTransport,
} from './proxyProtocol'

const draftId = '11111111-1111-4111-8111-111111111111'
const requestId = '22222222-2222-4222-8222-222222222222'
const uri = `https://player-media.appassets.example/source-only/proxy/${draftId}/${'a'.repeat(32)}`
const ready = { status: 'ready', reason: null, uri, height: 360 } as const
const missing = { status: 'missing', reason: 'プロキシ動画がまだ作られていません。', uri: null, height: 360 } as const
const generating = { status: 'generating', reason: 'プロキシ動画を作成しています。', uri: null, height: 360 } as const
const failed = { status: 'failed', reason: '動画の変換に失敗しました。', uri: null, height: 360 } as const

function fakeService(first: ProxyOutcome, afterRegenerate: ProxyOutcome = generating) {
  let current = first
  const service: ProxyService = {
    get: vi.fn(() => Promise.resolve(current)),
    regenerate: vi.fn(() => { current = afterRegenerate; return Promise.resolve(current) }),
    dispose: vi.fn(),
  }
  return service
}

// FR-144 AC-177 / AC-178 (integration): the proxy player and its state display.
describe('FR-144 ProxyPreview', () => {
  let container: HTMLDivElement
  let root: Root

  beforeEach(() => {
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    vi.unstubAllGlobals()
  })

  async function render(service: ProxyService | null, props: { isActive?: boolean } = {}) {
    await act(async () => { root.render(<ProxyPreview draftId={draftId} service={service} pollMilliseconds={20} {...props} />) })
    await act(async () => { await Promise.resolve() })
  }

  // AC-177: a player with 再生 / 再生位置 appears in the 元動画のプロキシプレビュー region when the proxy is ready.
  it('AC-177 shows the player with play, seek and a regenerate action when the proxy is ready', async () => {
    await render(fakeService(ready))
    const region = within(container).getByRole('region', { name: '元動画のプロキシプレビュー' })
    expect(region.querySelector('video')?.getAttribute('src')).toBe(uri)
    expect(within(region).getByRole('button', { name: '再生' })).toBeTruthy()
    const slider = within(region).getByRole('slider', { name: '再生位置' })
    expect(slider).toBeTruthy()
    expect(within(region).getByRole('button', { name: '再生成' })).toBeTruthy()
    expect(region.textContent).not.toContain('状態：')
  })

  it('AC-177 swaps 再生 for 一時停止 while the video plays, and moves the position with the video', async () => {
    await render(fakeService(ready))
    const video = container.querySelector('video')!
    Object.defineProperty(video, 'duration', { value: 10, configurable: true })
    await act(async () => { video.dispatchEvent(new Event('durationchange')); video.dispatchEvent(new Event('play')) })
    expect(within(container).getByRole('button', { name: '一時停止' })).toBeTruthy()
    video.currentTime = 4
    await act(async () => { video.dispatchEvent(new Event('timeupdate')) })
    expect(within(container).getByRole<HTMLInputElement>('slider', { name: '再生位置' }).value).toBe('4')
    await act(async () => { video.dispatchEvent(new Event('pause')) })
    expect(within(container).getByRole('button', { name: '再生' })).toBeTruthy()
  })

  it('AC-177 seeks the video from the 再生位置 slider', async () => {
    await render(fakeService(ready))
    const video = container.querySelector('video')!
    Object.defineProperty(video, 'duration', { value: 10, configurable: true })
    await act(async () => { video.dispatchEvent(new Event('durationchange')) })
    const slider = within(container).getByRole<HTMLInputElement>('slider', { name: '再生位置' })
    await act(async () => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(slider, '8')
      slider.dispatchEvent(new Event('input', { bubbles: true }))
    })
    expect(video.currentTime).toBe(8)
  })

  // AC-178: not generated / generating / failed show a state and a reason in the player's place, and 再生成 starts it again.
  it.each([
    ['missing', missing, '未生成', '理由：'],
    ['generating', generating, '生成中', '理由：'],
    ['failed', failed, '失敗', '原因：'],
  ] as const)('AC-178 shows the %s state with its reason instead of the player', async (_name, outcome, label, reasonLabel) => {
    await render(fakeService(outcome))
    const region = within(container).getByRole('region', { name: '元動画のプロキシプレビュー' })
    expect(region.querySelector('video')).toBeNull()
    expect(region.textContent).toContain(`状態：${label}`)
    expect(region.textContent).toContain(reasonLabel + outcome.reason)
  })

  it('AC-178 regenerates when 再生成 is pressed and follows the state to ready', async () => {
    vi.useFakeTimers()
    try {
      const service = fakeService(failed, generating)
      await render(service)
      await act(async () => { within(container).getByRole<HTMLButtonElement>('button', { name: '再生成' }).click() })
      expect(service.regenerate).toHaveBeenCalledExactlyOnceWith(draftId)
      expect(container.textContent).toContain('状態：生成中')
      expect(within(container).getByRole<HTMLButtonElement>('button', { name: '再生成' }).disabled).toBe(true)
      ;(service.get as ReturnType<typeof vi.fn>).mockResolvedValue(ready)
      await act(async () => { await vi.advanceTimersByTimeAsync(40) })
      expect(container.querySelector('video')?.getAttribute('src')).toBe(uri)
    } finally { vi.useRealTimers() }
  })

  it('AC-178 does not break the settings when the host is absent or answers with an error', async () => {
    await render(null)
    expect(container.textContent).toContain('この環境ではプロキシ動画を表示できません。')
    expect(container.querySelector('video')).toBeNull()
    await render(fakeService({ status: 'unavailable', reason: null, uri: null, height: 360 }))
    expect(container.textContent).toContain('プロキシ動画の状態を確認できません。')
  })

  it('refreshes when the settings screen is opened again and pauses when it is closed', async () => {
    const service = fakeService(ready)
    await render(service, { isActive: true })
    const pause = vi.spyOn(container.querySelector('video')!, 'pause').mockImplementation(() => undefined)
    await render(service, { isActive: false })
    expect(pause).toHaveBeenCalled()
    await render(service, { isActive: true })
    expect(service.get).toHaveBeenCalledTimes(2)
  })
})

describe('FR-144 proxy bridge protocol', () => {
  it('builds exact requests and parses only closed responses', () => {
    const sent: ProxyRequest[] = []
    const listeners = new Set<(event: { data: unknown }) => void>()
    const transport: ProxyTransport = {
      postMessage: message => { sent.push(message) },
      addEventListener: (_type, listener) => { listeners.add(listener) },
      removeEventListener: (_type, listener) => { listeners.delete(listener) },
    }
    const service = createProxyService(transport, { createRequestId: () => requestId, timeoutMilliseconds: 1000 })
    const pending = service.regenerate(draftId)
    expect(sent[0]).toEqual({ version: 1, requestId, method: PROXY_REGENERATE_METHOD, params: { draftId } })
    for (const listener of listeners) listener({ data: { version: 1, requestId, method: PROXY_REGENERATE_METHOD, result: generating } })
    return expect(pending).resolves.toEqual(generating).finally(() => service.dispose())
  })

  it('rejects extra keys, a ready state without a URI, and a foreign URI', () => {
    const envelope = (result: unknown) => ({ version: 1, requestId, method: PROXY_GET_METHOD, result })
    expect(parseProxyResponse(envelope(ready))?.result).toEqual(ready)
    expect(parseProxyResponse(envelope({ ...ready, path: 'C:\\x' }))).toBeNull()
    expect(parseProxyResponse(envelope({ ...ready, uri: null }))).toBeNull()
    expect(parseProxyResponse(envelope({ ...ready, uri: 'https://example.com/x.mp4' }))).toBeNull()
    expect(parseProxyResponse(envelope({ ...missing, uri }))).toBeNull()
    expect(parseProxyResponse(envelope({ ...missing, status: 'maybe' }))).toBeNull()
  })
})
