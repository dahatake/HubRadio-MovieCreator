// @vitest-environment jsdom

import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import type { BridgeClient } from '../../bridge'
import {
  parsePlayerReadResponse,
  parsePlayerSeekResponse,
  type PlayerReadResponse,
  type PlayerSeekResponse,
  type PlayerTimelinePosition,
} from './playerProtocol'
import {
  PlayerPanel,
  type PlayerBridgeClient,
  type PlayerExactFrameSelection,
} from './PlayerPanel'
import {
  createPlayerReadResponseValue,
  createPlayerSeekResponseValue,
  playerEvidenceDigest,
  playerJobId,
  playerProxyDigest,
  playerProjectId,
  playerSpriteDigest,
} from './playerTestFixture'

describe('PlayerPanel', () => {
  let container: HTMLDivElement
  let root: Root

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT =
      true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  it('rejects a noncanonical job ID and keeps adversarial input inert', () => {
    const client = new FakeBridgeClient()
    const adversarialJobId = '<script>globalThis.compromised=true</script>'
    renderPlayer(client, adversarialJobId)

    const input = requiredElement<HTMLInputElement>('#player-job-id')
    const button = requiredElement<HTMLButtonElement>('.player-load-row button')
    expect(input.value).toBe(adversarialJobId)
    expect(button.disabled).toBe(true)
    expect(container.querySelector('script')).toBeNull()
    expect(
      (globalThis as { compromised?: boolean }).compromised,
    ).toBeUndefined()

    submitLoad()
    expect(client.sentReads).toEqual([])
  })

  it('renders opaque media, exact time domains, VFR waveform, and responsive sprite geometry', () => {
    const client = new FakeBridgeClient()
    renderPlayer(client)
    completeRead(client)

    expect(requiredElement<HTMLElement>('[role="status"]').dataset.playerStatus).toBe(
      'available',
    )
    const video = requiredElement<HTMLVideoElement>('video')
    expect(video.hasAttribute('controls')).toBe(false)
    expect(
      requiredElement<HTMLButtonElement>('.playback-toggle').getAttribute(
        'aria-label',
      ),
    ).toBe('代理動画を再生')
    expect(video.getAttribute('src')).toBe(
      `https://player-media.appassets.example/media/v1/${playerProjectId}` +
        `/proxy/44444444-4444-4444-8444-444444444444/${playerProxyDigest}`,
    )
    expect(timeDomainText()).toEqual(['Source-1/10', 'Proxy0/1', 'Output—'])
    expect(requiredElement<SVGElement>('.waveform-panel svg').getAttribute('viewBox')).toBe(
      '0 0 1000 160',
    )
    expect(
      requiredElement<SVGElement>('.waveform-panel svg').getAttribute(
        'preserveAspectRatio',
      ),
    ).toBe('none')
    expect(requiredElement<SVGElement>('.sprite-panel svg').getAttribute('viewBox')).toBe(
      '0 0 160 90',
    )
    expect(requiredElement<SVGImageElement>('.sprite-panel image').getAttribute('href')).toBe(
      `https://player-media.appassets.example/media/v1/${playerProjectId}` +
        `/sprite/66666666-6666-4666-8666-666666666666/${playerSpriteDigest}`,
    )
    expect(requiredElement<HTMLOutputElement>('output').textContent).toContain('1 / 3')
    expect(container.textContent).not.toContain('C:\\')
    expect(container.innerHTML).not.toContain('file://')
    expect(container.innerHTML).not.toContain('S-1-5-')
  })

  it('publishes exact source and output frame time to the timeline integration', () => {
    const client = new FakeBridgeClient()
    const positions: Array<PlayerTimelinePosition | null> = []
    renderPlayer(client, playerJobId, (position) => positions.push(position))
    submitLoad()
    const read = client.sentReads[0]
    if (read === undefined) {
      throw new Error('Expected one player read request.')
    }
    const response = createReadResponseWithOutputTime(read.requestId)

    act(() => client.emitRead(response))

    expect(positions.at(-1)).toEqual({
      projectId: playerProjectId,
      sourcePresentationTime: { value: '-1', rate: '10' },
      outputPresentationTime: { value: '1001', rate: '48000' },
    })
    expect(Object.isFrozen(positions.at(-1))).toBe(true)
  })

  it('keeps browser seek controls closed while supporting play and pause', () => {
    const client = new FakeBridgeClient()
    renderPlayer(client)
    completeRead(client)
    const stage = requiredElement<HTMLDivElement>('.video-stage')
    const video = requiredElement<HTMLVideoElement>('video')
    const button = requiredElement<HTMLButtonElement>('.playback-toggle')
    let paused = true
    Object.defineProperty(video, 'paused', {
      configurable: true,
      get: () => paused,
    })
    Object.defineProperty(video, 'play', {
      configurable: true,
      value: () => {
        paused = false
        video.dispatchEvent(new Event('play'))
        return Promise.resolve()
      },
    })
    Object.defineProperty(video, 'pause', {
      configurable: true,
      value: () => {
        paused = true
        video.dispatchEvent(new Event('pause'))
      },
    })

    act(() => button.click())
    expect(button.textContent).toBe('一時停止')
    expect(client.sentSeeks).toEqual([])

    pressKey(stage, ' ')
    expect(button.textContent).toBe('再生')
    expect(client.sentSeeks).toEqual([])
  })

  it('round-trips keyboard seeks through native evidence before changing video time', () => {
    const client = new FakeBridgeClient()
    renderPlayer(client)
    completeRead(client)

    const stage = requiredElement<HTMLDivElement>('.video-stage')
    const video = requiredElement<HTMLVideoElement>('video')
    expect(video.currentTime).toBe(0)

    pressKey(stage, 'ArrowLeft')
    expect(client.sentSeeks).toEqual([])

    pressKey(stage, 'ArrowRight')
    expect(client.sentSeeks).toHaveLength(1)
    expect(client.sentSeeks[0]).toMatchObject({
      jobId: playerJobId,
      evidenceDigest: playerEvidenceDigest,
      proxyFrameSequence: 1,
    })
    expect(video.currentTime).toBe(0)

    completeSeek(client, 0)
    expect(video.currentTime).toBe(1001 / 30000)
    expect(timeDomainText()).toEqual([
      'Source0/1',
      'Proxy1001/30000',
      'Output—',
    ])
    expect(container.textContent).toContain(
      'この代理フレームに対応する sprite cell はありません。',
    )

    pressKey(stage, 'End')
    expect(client.sentSeeks[1]?.proxyFrameSequence).toBe(2)
    completeSeek(client, 1)
    expect(video.currentTime).toBe(1 / 12)
    expect(timeDomainText()).toEqual(['Source1/20', 'Proxy1/12', 'Output—'])

    pressKey(stage, 'ArrowRight')
    pressKey(stage, 'End')
    expect(client.sentSeeks).toHaveLength(2)
  })

  it('keeps negative proxy PTS canonical while offsetting browser media time', () => {
    const client = new FakeBridgeClient()
    renderPlayer(client)
    submitLoad()
    const read = client.sentReads[0]
    if (read === undefined) {
      throw new Error('Expected one player read request.')
    }
    act(() => client.emitRead(createNegativeProxyReadResponse(read.requestId)))

    const stage = requiredElement<HTMLDivElement>('.video-stage')
    const video = requiredElement<HTMLVideoElement>('video')
    expect(video.currentTime).toBe(0)
    expect(timeDomainText()).toEqual(['Source-1/10', 'Proxy-1/20', 'Output—'])

    act(() => video.dispatchEvent(new Event('timeupdate')))
    expect(requiredElement<HTMLOutputElement>('output').textContent).toContain(
      '1 / 3',
    )

    pressKey(stage, 'ArrowRight')
    const seek = client.sentSeeks[0]
    if (seek === undefined) {
      throw new Error('Expected one player seek request.')
    }
    act(() =>
      client.emitSeek(
        createNegativeProxySeekResponse(
          seek.requestId,
          seek.proxyFrameSequence,
        ),
      ),
    )

    expect(video.currentTime).toBe(1001 / 30000)
    expect(timeDomainText()).toEqual([
      'Source0/1',
      'Proxy-499/30000',
      'Output—',
    ])
  })

  it('shares the exact U-007 frame tuple without collapsing time domains', () => {
    const client = new FakeBridgeClient()
    const observed: Array<PlayerExactFrameSelection | null> = []
    renderPlayer(
      client,
      playerJobId,
      undefined,
      (selection) => observed.push(selection),
    )
    completeRead(client)

    expect(observed.at(-1)).toMatchObject({
      jobId: playerJobId,
      projectId: playerProjectId,
      playerEvidenceDigest,
      frame: {
        proxyFrameSequence: 0,
        proxyPresentationTime: { value: '0', rate: '1' },
        sourceFrameSequence: 0,
        sourcePresentationTime: { value: '-1', rate: '10' },
        outputPresentationTime: null,
      },
    })

    pressKey(requiredElement('.video-stage'), 'ArrowRight')
    completeSeek(client, 0)
    expect(observed.at(-1)?.frame).toMatchObject({
      proxyFrameSequence: 1,
      proxyPresentationTime: { value: '1001', rate: '30000' },
      sourceFrameSequence: 1,
      sourcePresentationTime: { value: '0', rate: '1' },
      outputPresentationTime: null,
    })
  })

  it('retains canonical evidence when media fails and recovers on can-play', () => {
    const client = new FakeBridgeClient()
    renderPlayer(client)
    completeRead(client)
    const video = requiredElement<HTMLVideoElement>('video')

    act(() => video.dispatchEvent(new Event('error', { bubbles: true })))
    expect(requiredElement<HTMLElement>('[role="status"]').dataset.playerStatus).toBe(
      'mediaUnavailable',
    )
    expect(timeDomainText()).toEqual(['Source-1/10', 'Proxy0/1', 'Output—'])
    expect(requiredElement<HTMLVideoElement>('video')).toBe(video)

    act(() => video.dispatchEvent(new Event('canplay', { bubbles: true })))
    expect(requiredElement<HTMLElement>('[role="status"]').dataset.playerStatus).toBe(
      'available',
    )
  })

  it('shows a closed authorization denial without retaining evidence', () => {
    const client = new FakeBridgeClient()
    renderPlayer(client)
    submitLoad()
    const read = client.sentReads[0]
    if (read === undefined) {
      throw new Error('Expected one player read request.')
    }
    const response = parsePlayerReadResponse({
      version: 1,
      requestId: read.requestId,
      method: 'player.read',
      result: { status: 'forbidden', snapshot: null },
    })
    if (response === null) {
      throw new Error('Expected a valid forbidden player response.')
    }

    act(() => client.emitRead(response))

    const status = requiredElement<HTMLElement>('[role="status"]')
    expect(status.dataset.playerStatus).toBe('forbidden')
    expect(status.textContent).toBe('このプレビューを表示する権限がありません')
    expect(container.querySelector('video')).toBeNull()
    expect(container.innerHTML).not.toContain('file://')
  })

  it('cleans up both bridge listeners and disposes its client', () => {
    const client = new FakeBridgeClient()
    renderPlayer(client)
    expect(client.listenerCount).toBe(2)

    act(() => root.unmount())

    expect(client.listenerCount).toBe(0)
    expect(client.disposed).toBe(true)
    root = createRoot(container)
  })

  function renderPlayer(
    client: FakeBridgeClient,
    initialJobId = playerJobId,
    onPositionChange?: (position: PlayerTimelinePosition | null) => void,
    onSelectionChange?: (selection: PlayerExactFrameSelection | null) => void,
  ): void {
    act(() => {
      root.render(
        <PlayerPanel
          createClient={() => client}
          initialJobId={initialJobId}
          onPositionChange={onPositionChange}
          onSelectionChange={onSelectionChange}
        />,
      )
    })
  }

  function submitLoad(): void {
    const form = requiredElement<HTMLFormElement>('form')
    act(() => {
      form.dispatchEvent(
        new Event('submit', { bubbles: true, cancelable: true }),
      )
    })
  }

  function completeRead(client: FakeBridgeClient): void {
    submitLoad()
    const sent = client.sentReads[0]
    if (sent === undefined) {
      throw new Error('Expected one player read request.')
    }
    act(() => client.emitRead(createReadResponse(sent.requestId)))
  }

  function completeSeek(client: FakeBridgeClient, sentIndex: number): void {
    const sent = client.sentSeeks[sentIndex]
    if (sent === undefined) {
      throw new Error('Expected a player seek request.')
    }
    act(() =>
      client.emitSeek(
        createSeekResponse(sent.requestId, sent.proxyFrameSequence),
      ),
    )
  }

  function pressKey(element: HTMLElement, key: string): void {
    act(() => {
      element.dispatchEvent(
        new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }),
      )
    })
  }

  function timeDomainText(): string[] {
    return [...container.querySelectorAll('.time-domain')].map(
      (element) => element.textContent ?? '',
    )
  }

  function requiredElement<T extends Element>(selector: string): T {
    const element = container.querySelector(selector)
    if (element === null) {
      throw new Error(`Expected element matching ${selector}.`)
    }
    return element as T
  }
})

class FakeBridgeClient implements PlayerBridgeClient {
  readonly sentReads: Array<Readonly<{ requestId: string; jobId: string }>> = []
  readonly sentSeeks: Array<
    Readonly<{
      requestId: string
      jobId: string
      evidenceDigest: string
      proxyFrameSequence: number
    }>
  > = []
  disposed = false
  private readonly readListeners = new Set<
    (response: PlayerReadResponse) => void
  >()
  private readonly seekListeners = new Set<
    (response: PlayerSeekResponse) => void
  >()

  get listenerCount(): number {
    return this.readListeners.size + this.seekListeners.size
  }

  sendPing(_requestId: string): void {}

  onPingResponse(
    _listener: Parameters<BridgeClient['onPingResponse']>[0],
  ): () => void {
    return () => {}
  }

  sendPlayerRead(requestId: string, jobId: string): void {
    this.sentReads.push({ requestId, jobId })
  }

  onPlayerReadResponse(
    listener: (response: PlayerReadResponse) => void,
  ): () => void {
    this.readListeners.add(listener)
    return () => this.readListeners.delete(listener)
  }

  sendPlayerSeek(
    requestId: string,
    jobId: string,
    evidenceDigest: string,
    proxyFrameSequence: number,
  ): void {
    this.sentSeeks.push({
      requestId,
      jobId,
      evidenceDigest,
      proxyFrameSequence,
    })
  }

  onPlayerSeekResponse(
    listener: (response: PlayerSeekResponse) => void,
  ): () => void {
    this.seekListeners.add(listener)
    return () => this.seekListeners.delete(listener)
  }

  emitRead(response: PlayerReadResponse): void {
    for (const listener of this.readListeners) listener(response)
  }

  emitSeek(response: PlayerSeekResponse): void {
    for (const listener of this.seekListeners) listener(response)
  }

  dispose(): void {
    this.disposed = true
    this.readListeners.clear()
    this.seekListeners.clear()
  }
}

function createReadResponse(requestId: string): PlayerReadResponse {
  const value = createPlayerReadResponseValue()
  value.requestId = requestId
  const parsed = parsePlayerReadResponse(value)
  if (parsed === null) {
    throw new Error('Expected a valid player read response.')
  }
  return parsed
}

function createReadResponseWithOutputTime(
  requestId: string,
): PlayerReadResponse {
  const value = createPlayerReadResponseValue()
  value.requestId = requestId
  const firstFrame = (
    value.result as {
      snapshot: {
        frames: Array<{
          outputPresentationTime: { value: string; rate: string } | null
        }>
      }
    }
  ).snapshot.frames[0]
  if (firstFrame === undefined) {
    throw new Error('Expected the player fixture to contain a frame.')
  }
  firstFrame.outputPresentationTime = { value: '1001', rate: '48000' }
  const parsed = parsePlayerReadResponse(value)
  if (parsed === null) {
    throw new Error('Expected a valid player read response with output time.')
  }
  return parsed
}

function createSeekResponse(
  requestId: string,
  proxyFrameSequence: number,
): PlayerSeekResponse {
  const parsed = parsePlayerSeekResponse(
    createPlayerSeekResponseValue(requestId, proxyFrameSequence),
  )
  if (parsed === null) {
    throw new Error('Expected a valid player seek response.')
  }
  return parsed
}

function createNegativeProxyReadResponse(requestId: string): PlayerReadResponse {
  const value = createPlayerReadResponseValue()
  value.requestId = requestId
  const snapshot = (
    value.result as {
      snapshot: {
        proxy: {
          presentationStart: { value: string; rate: string }
          presentationEnd: { value: string; rate: string }
        }
        frames: Array<{
          proxyPresentationTime: { value: string; rate: string }
        }>
      }
    }
  ).snapshot
  snapshot.proxy.presentationStart = { value: '-1', rate: '20' }
  snapshot.frames[0]!.proxyPresentationTime = { value: '-1', rate: '20' }
  snapshot.frames[1]!.proxyPresentationTime = {
    value: '-499',
    rate: '30000',
  }
  snapshot.frames[2]!.proxyPresentationTime = { value: '1', rate: '30' }
  snapshot.proxy.presentationEnd = { value: '1', rate: '15' }
  const parsed = parsePlayerReadResponse(value)
  if (parsed === null) {
    throw new Error('Expected valid negative-start player evidence.')
  }
  return parsed
}

function createNegativeProxySeekResponse(
  requestId: string,
  proxyFrameSequence: number,
): PlayerSeekResponse {
  const value = createPlayerSeekResponseValue(requestId, proxyFrameSequence)
  const position = (
    value.result as {
      position: {
        proxyPresentationTime: { value: string; rate: string }
      }
    }
  ).position
  const proxyTimes = [
    { value: '-1', rate: '20' },
    { value: '-499', rate: '30000' },
    { value: '1', rate: '30' },
  ] as const
  position.proxyPresentationTime = proxyTimes[proxyFrameSequence]!
  const parsed = parsePlayerSeekResponse(value)
  if (parsed === null) {
    throw new Error('Expected a valid negative-start seek response.')
  }
  return parsed
}
