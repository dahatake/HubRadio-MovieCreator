// @vitest-environment jsdom

import { act, StrictMode, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { BridgeClient } from '../../bridge'
import { initialPreviewModeState } from '../preview-modes/previewModeState'
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
  type PlayerPanelProps,
} from './PlayerPanel'
import { PlayerNavigationProvider, usePlayerSourceNavigation, type PlayerSourceSeekHandler } from './PlayerNavigationProvider'
import { PLAYER_SEEK_TIMEOUT_MS } from './playerBridgeSession'
import {
  createPlayerReadResponseValue,
  createPlayerSeekResponseValue,
  playerEvidenceDigest,
  playerJobId,
  playerProxyDigest,
  playerProjectId,
  playerSourceAssetId,
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
    vi.useRealTimers()
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

  it('releases a timed-out source navigation seek without losing evidence or staying busy forever', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const client = new FakeBridgeClient()
    let seekSource: PlayerSourceSeekHandler = () => 'unavailable'
    function ObserveNavigation() { seekSource = usePlayerSourceNavigation(); return null }
    await act(async () => root.render(<PlayerNavigationProvider>
      <ObserveNavigation />
      <PlayerPanel context={{ jobId: playerJobId }} createClient={() => client} />
    </PlayerNavigationProvider>))
    act(() => client.emitRead(createReadResponse(client.sentReads[0]!.requestId)))
    const video = requiredElement<HTMLVideoElement>('video')
    const before = timeDomainText()
    const target = { projectId: playerProjectId, sourceTime: { value: '1', rate: '20' } }
    act(() => {
      expect(seekSource(target)).toBe('requested')
      expect(seekSource(target)).toBe('busy')
    })
    const old = client.sentSeeks[0]!
    expect(requiredElement<HTMLInputElement>('#proxy-frame-sequence').disabled).toBe(true)
    await act(async () => { await vi.advanceTimersByTimeAsync(PLAYER_SEEK_TIMEOUT_MS) })
    expect(client.abandoned).toEqual([old.requestId])
    expect(client.sentSeeks).toHaveLength(1)
    expect(requiredElement<HTMLInputElement>('#proxy-frame-sequence').disabled).toBe(false)
    expect(requiredElement('.player-seek-notice').textContent).toContain('時間切れ')
    expect(timeDomainText()).toEqual(before)
    expect(requiredElement('video')).toBe(video)
    expect(vi.getTimerCount()).toBe(0)
    act(() => client.emitSeek(createSeekResponse(old.requestId, old.proxyFrameSequence)))
    expect(video.currentTime).toBe(0)
    expect(timeDomainText()).toEqual(before)
    act(() => { expect(seekSource(target)).toBe('requested') })
    expect(client.sentSeeks).toHaveLength(2)
    expect(container.querySelector('.player-seek-notice')).toBeNull()
    act(() => client.emitSeek(createSeekResponse(old.requestId, old.proxyFrameSequence)))
    expect(video.currentTime).toBe(0)
    completeSeek(client, 1)
    expect(video.currentTime).toBe(1 / 12)
    expect(vi.getTimerCount()).toBe(0)
    expect(client.sentReads).toHaveLength(1)
  })

  it('abandons a media-time assignment failure and permits another manual seek without a stranded pending slot', () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const client = new FakeBridgeClient()
    renderPlayer(client); completeRead(client)
    const video = requiredElement<HTMLVideoElement>('video')
    const stage = requiredElement<HTMLDivElement>('.video-stage')
    const before = timeDomainText()
    Object.defineProperty(video, 'currentTime', {
      configurable: true,
      get: () => 0,
      set: () => { throw new Error('Media time unavailable') },
    })
    pressKey(stage, 'ArrowRight')
    expect(() => completeSeek(client, 0)).not.toThrow()
    expect(client.abandoned).toEqual([client.sentSeeks[0]!.requestId])
    expect(requiredElement<HTMLElement>('.player-status').dataset.playerStatus).toBe('mediaUnavailable')
    expect(requiredElement<HTMLInputElement>('#proxy-frame-sequence').disabled).toBe(false)
    expect(timeDomainText()).toEqual(before)
    expect(vi.getTimerCount()).toBe(0)
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: 0 })
    pressKey(stage, 'ArrowRight')
    expect(client.sentSeeks).toHaveLength(2)
    completeSeek(client, 1)
    expect(video.currentTime).toBe(1001 / 30000)
    expect(vi.getTimerCount()).toBe(0)
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

  it('autoloads an explicit job once, including effect replay and new prop identities', async () => {
    const clients: FakeBridgeClient[] = []
    const createClient = vi.fn(() => {
      const client = new FakeBridgeClient()
      clients.push(client)
      return client
    })
    const render = async (): Promise<void> => {
      await act(async () => {
        root.render(
          <StrictMode>
            <PlayerPanel
              createClient={() => createClient()}
              context={{ jobId: playerJobId }}
              onPositionChange={() => {}}
              onSelectionChange={() => {}}
            />
          </StrictMode>,
        )
      })
    }

    await render()
    const client = clients.at(-1)!
    const read = client.sentReads[0]!
    expect(clients.flatMap((candidate) => candidate.sentReads)).toHaveLength(1)
    expect(read.jobId).toBe(playerJobId)
    expect(clients.reduce((sum, candidate) => sum + candidate.listenerCount, 0)).toBe(2)
    expect(container.querySelector('.player-load-form')).toBeNull()
    expect(container.querySelector('#player-job-id')).toBeNull()
    const created = createClient.mock.calls.length
    act(() => client.emitRead(createReadResponse(read.requestId)))
    const video = requiredElement<HTMLVideoElement>('video')

    await render()
    await render()

    expect(createClient).toHaveBeenCalledTimes(created)
    expect(client.readSubscriptions).toBe(1)
    expect(client.seekSubscriptions).toBe(1)
    expect(client.sentReads).toHaveLength(1)
    expect(client.sentSeeks).toEqual([])
    expect(requiredElement('video')).toBe(video)
    expect(requiredElement<HTMLElement>('.player-card').hidden).toBe(false)
    expect(container.textContent).toContain('最終出力の画質・コーデックを検証するものではありません')
  })

  it.each([
    null,
    '',
    'standalone',
    playerJobId.toUpperCase(),
    '00000000-0000-0000-0000-000000000000',
    '<script>invalid</script>',
  ])('keeps explicit missing or invalid context %s inert without falling back to initialJobId', async (jobId) => {
    const client = new FakeBridgeClient()
    const createClient = vi.fn(() => client)
    const onPositionChange = vi.fn()
    const onSelectionChange = vi.fn()

    await act(async () => {
      root.render(
        <PlayerPanel
          createClient={createClient}
          initialJobId={playerJobId}
          context={{ jobId }}
          onPositionChange={onPositionChange}
          onSelectionChange={onSelectionChange}
        />,
      )
    })

    expect(createClient).not.toHaveBeenCalled()
    expect(client.sentReads).toEqual([])
    expect(client.sentSeeks).toEqual([])
    expect(client.listenerCount).toBe(0)
    expect(container.querySelector('form, input, video, script')).toBeNull()
    expect(onPositionChange).toHaveBeenLastCalledWith(null)
    expect(onSelectionChange).toHaveBeenLastCalledWith(null)
    expect(requiredElement('.player-status').textContent).toBe(jobId === null
      ? 'プレビューする取込ジョブが選択されていません'
      : '選択した取込ジョブを確認できません')
  })

  it('uses the latest callbacks without replaying notifications, reconnecting, or losing a pending seek', async () => {
    const client = new FakeBridgeClient()
    const oldPosition = vi.fn()
    const oldSelection = vi.fn()
    await renderContextPlayer(client, playerJobId, {
      onPositionChange: oldPosition,
      onSelectionChange: oldSelection,
    })
    const response = createReadResponse(client.sentReads[0]!.requestId)
    act(() => client.emitRead(response))
    const video = requiredElement<HTMLVideoElement>('video')
    pressKey(requiredElement('.video-stage'), 'ArrowRight')
    const positionCount = oldPosition.mock.calls.length
    const selectionCount = oldSelection.mock.calls.length
    const latestPosition = vi.fn()
    const latestSelection = vi.fn()

    await renderContextPlayer(client, playerJobId, {
      onPositionChange: latestPosition,
      onSelectionChange: latestSelection,
    })

    expect(latestPosition).not.toHaveBeenCalled()
    expect(latestSelection).not.toHaveBeenCalled()
    expect(client.disposed).toBe(false)
    expect(client.readSubscriptions).toBe(1)
    expect(client.seekSubscriptions).toBe(1)
    expect(client.sentReads).toHaveLength(1)
    expect(client.sentSeeks).toHaveLength(1)
    expect(requiredElement('video')).toBe(video)
    expect(video.currentTime).toBe(0)
    completeSeek(client, 0)
    expect(video.currentTime).toBe(1001 / 30000)
    expect(latestPosition).toHaveBeenLastCalledWith({
      projectId: playerProjectId,
      sourcePresentationTime: { value: '0', rate: '1' },
      outputPresentationTime: null,
    })
    expect(latestSelection).toHaveBeenLastCalledWith({
      jobId: playerJobId,
      projectId: playerProjectId,
      sourceAssetId: playerSourceAssetId,
      playerEvidenceDigest,
      frame: response.result.status === 'available' ? response.result.snapshot.frames[1] : null,
    })
    expect(oldPosition).toHaveBeenCalledTimes(positionCount)
    expect(oldSelection).toHaveBeenCalledTimes(selectionCount)

    act(() => root.unmount())
    expect(latestPosition).toHaveBeenLastCalledWith(null)
    expect(latestSelection).toHaveBeenLastCalledWith(null)
    expect(oldPosition).toHaveBeenCalledTimes(positionCount)
    expect(oldSelection).toHaveBeenCalledTimes(selectionCount)
    root = createRoot(container)
  })

  it('does not loop when inline callbacks feed selection and position back into workspace state', async () => {
    const client = new FakeBridgeClient()
    let notifications = 0
    function Workspace() {
      const [position, setPosition] = useState<PlayerTimelinePosition | null>(null)
      const [selection, setSelection] = useState<PlayerExactFrameSelection | null>(null)
      const countNotification = (): void => {
        notifications += 1
        if (notifications > 12) throw new Error('Player notification feedback loop.')
      }
      return (
        <>
          <output data-workspace-project={position?.projectId}>
            {selection?.sourceAssetId ?? 'none'}
          </output>
          <PlayerPanel
            createClient={() => client}
            context={{ jobId: playerJobId }}
            onPositionChange={(next) => {
              countNotification()
              setPosition(next)
            }}
            onSelectionChange={(next) => {
              countNotification()
              setSelection(next)
            }}
          />
        </>
      )
    }

    await act(async () => root.render(<Workspace />))
    act(() => client.emitRead(createReadResponse(client.sentReads[0]!.requestId)))

    expect(requiredElement('[data-workspace-project]').textContent).toBe(playerSourceAssetId)
    expect(client.sentReads).toHaveLength(1)
    expect(client.readSubscriptions).toBe(1)
    expect(client.seekSubscriptions).toBe(1)
    expect(notifications).toBeLessThan(12)
  })

  it('clears old evidence on job changes and correlates job, source, digest, and frame only to the new response', async () => {
    const firstClient = new FakeBridgeClient()
    const nextClient = new FakeBridgeClient()
    const positions: Array<PlayerTimelinePosition | null> = []
    const selections: Array<PlayerExactFrameSelection | null> = []
    const callbacks = {
      onPositionChange: (position: PlayerTimelinePosition | null) => positions.push(position),
      onSelectionChange: (selection: PlayerExactFrameSelection | null) => selections.push(selection),
    }
    await renderContextPlayer(firstClient, playerJobId, callbacks)
    const oldRead = createReadResponse(firstClient.sentReads[0]!.requestId)
    act(() => firstClient.emitRead(oldRead))
    pressKey(requiredElement('.video-stage'), 'ArrowRight')
    const oldSeek = createSeekResponse(firstClient.sentSeeks[0]!.requestId, 1)

    await renderContextPlayer(nextClient, otherJobId, callbacks)

    expect(firstClient.disposed).toBe(true)
    expect(firstClient.listenerCount).toBe(0)
    expect(container.querySelector('video, .waveform-panel, .sprite-panel')).toBeNull()
    expect(positions.at(-1)).toBeNull()
    expect(selections.at(-1)).toBeNull()
    const positionStart = positions.length
    const selectionStart = selections.length
    act(() => {
      nextClient.emitRead(oldRead)
      nextClient.emitSeek(oldSeek)
    })
    expect(container.querySelector('video')).toBeNull()
    expect(positions).toHaveLength(positionStart)
    expect(selections).toHaveLength(selectionStart)

    const nextRead = createOtherProjectReadResponse(nextClient.sentReads[0]!.requestId)
    if (nextRead.result.status !== 'available') throw new Error('Expected new evidence.')
    act(() => nextClient.emitRead(nextRead))
    const video = requiredElement<HTMLVideoElement>('video')
    expect(selections.at(-1)).toEqual({
      jobId: otherJobId,
      projectId: otherProjectId,
      sourceAssetId: otherSourceAssetId,
      playerEvidenceDigest: otherEvidenceDigest,
      frame: nextRead.result.snapshot.frames[0],
    })
    expect(selections.at(-1)?.frame).toBe(nextRead.result.snapshot.frames[0])
    expect(positions.at(-1)?.projectId).toBe(otherProjectId)
    expect(video.getAttribute('src')).toBe(nextRead.result.snapshot.proxy.mediaReference)
    expect(container.innerHTML).not.toContain(playerProjectId)

    pressKey(requiredElement('.video-stage'), 'ArrowRight')
    expect(nextClient.sentSeeks[0]).toMatchObject({
      jobId: otherJobId,
      evidenceDigest: otherEvidenceDigest,
      proxyFrameSequence: 1,
    })
    act(() => nextClient.emitSeek(oldSeek))
    expect(video.currentTime).toBe(0)
    completeSeek(nextClient, 0)
    expect(video.currentTime).toBe(1001 / 30000)
    expect(selections.at(-1)?.frame).toBe(nextRead.result.snapshot.frames[1])
    expect(selections.slice(selectionStart).every((selection) => selection === null || (
      selection.jobId === otherJobId &&
      selection.projectId === otherProjectId &&
      selection.sourceAssetId === otherSourceAssetId &&
      selection.playerEvidenceDigest === otherEvidenceDigest
    ))).toBe(true)
    expect(nextClient.sentReads).toHaveLength(1)
  })

  it.each(['read', 'seek'] as const)('clears null context while %s is pending without new requests or stale callbacks', async (pending) => {
    const client = new FakeBridgeClient()
    const onPositionChange = vi.fn()
    const onSelectionChange = vi.fn()
    const callbacks = { onPositionChange, onSelectionChange }
    await renderContextPlayer(client, playerJobId, callbacks)
    const response = createReadResponse(client.sentReads[0]!.requestId)
    if (pending === 'seek') {
      act(() => client.emitRead(response))
      pressKey(requiredElement('.video-stage'), 'ArrowRight')
    }

    await renderContextPlayer(client, null, callbacks)
    const positionCount = onPositionChange.mock.calls.length
    const selectionCount = onSelectionChange.mock.calls.length
    act(() => {
      client.emitRead(response)
      const seek = client.sentSeeks[0]
      if (seek !== undefined) client.emitSeek(createSeekResponse(seek.requestId, 1))
    })
    await renderContextPlayer(client, null, callbacks)

    expect(requiredElement('.player-status').textContent).toBe('プレビューする取込ジョブが選択されていません')
    expect(container.querySelector('video, .waveform-panel, .sprite-panel, form')).toBeNull()
    expect(client.sentReads).toHaveLength(1)
    expect(client.sentSeeks).toHaveLength(pending === 'seek' ? 1 : 0)
    expect(client.listenerCount).toBe(0)
    expect(client.readSubscriptions).toBe(1)
    expect(onPositionChange).toHaveBeenCalledTimes(positionCount)
    expect(onSelectionChange).toHaveBeenCalledTimes(selectionCount)
    expect(onPositionChange).toHaveBeenLastCalledWith(null)
    expect(onSelectionChange).toHaveBeenLastCalledWith(null)
  })

  it('does not retain standalone evidence for an invalid context resembling an internal key', async () => {
    const client = new FakeBridgeClient()
    renderPlayer(client)
    expect(client.sentReads).toEqual([])
    completeRead(client)

    await renderContextPlayer(client, 'standalone')

    expect(container.querySelector('video, form')).toBeNull()
    expect(client.listenerCount).toBe(0)
    expect(client.sentReads).toHaveLength(1)

    const standaloneClient = new FakeBridgeClient()
    renderPlayer(standaloneClient)
    expect(requiredElement<HTMLInputElement>('#player-job-id').value).toBe(playerJobId)
    expect(container.querySelector('video')).toBeNull()
    expect(standaloneClient.sentReads).toEqual([])
  })

  it('pauses hidden media without changing its time or evidence and does not autoplay on return', async () => {
    const client = new FakeBridgeClient()
    const onPositionChange = vi.fn()
    const onSelectionChange = vi.fn()
    const callbacks = { onPositionChange, onSelectionChange }
    await renderContextPlayer(client, playerJobId, callbacks)
    act(() => client.emitRead(createReadResponse(client.sentReads[0]!.requestId)))
    const video = requiredElement<HTMLVideoElement>('video')
    const media = mockPlayback(video)
    act(() => {
      video.currentTime = 0.05
      video.dispatchEvent(new Event('timeupdate'))
      requiredElement<HTMLButtonElement>('.playback-toggle').click()
    })
    const selection = onSelectionChange.mock.calls.at(-1)![0]
    const positionCount = onPositionChange.mock.calls.length
    const selectionCount = onSelectionChange.mock.calls.length

    await renderContextPlayer(client, playerJobId, { ...callbacks, active: false })
    const card = requiredElement<HTMLElement>('.player-card')
    expect(card.hidden).toBe(true)
    expect(card.hasAttribute('inert')).toBe(true)
    expect(media.pause).toHaveBeenCalledTimes(1)
    expect(video.paused).toBe(true)
    expect(video.currentTime).toBe(0.05)
    expect(requiredElement('video')).toBe(video)
    pressKey(requiredElement('.video-stage'), ' ')
    pressKey(requiredElement('.video-stage'), 'ArrowRight')
    expect(media.play).toHaveBeenCalledTimes(1)
    expect(client.sentSeeks).toEqual([])

    await renderContextPlayer(client, playerJobId, { ...callbacks, active: true })

    expect(card.hidden).toBe(false)
    expect(card.hasAttribute('inert')).toBe(false)
    expect(requiredElement('video')).toBe(video)
    expect(video.currentTime).toBe(0.05)
    expect(video.paused).toBe(true)
    expect(video.autoplay).toBe(false)
    expect(requiredElement('.playback-toggle').textContent).toBe('再生')
    expect(media.play).toHaveBeenCalledTimes(1)
    expect(media.pause).toHaveBeenCalledTimes(1)
    expect(onPositionChange).toHaveBeenCalledTimes(positionCount)
    expect(onSelectionChange).toHaveBeenCalledTimes(selectionCount)
    expect(onSelectionChange.mock.calls.at(-1)![0]).toBe(selection)
    expect(client.sentReads).toHaveLength(1)
    expect(client.readSubscriptions).toBe(1)
    expect(client.seekSubscriptions).toBe(1)
  })

  it.each(['resolve', 'reject'] as const)('ignores a pending play that completes with %s after hiding and returning', async (outcome) => {
    const client = new FakeBridgeClient()
    await renderContextPlayer(client, playerJobId)
    act(() => client.emitRead(createReadResponse(client.sentReads[0]!.requestId)))
    const video = requiredElement<HTMLVideoElement>('video')
    let paused = true
    let resolvePlay!: () => void
    let rejectPlay!: (reason: Error) => void
    const pendingPlay = new Promise<void>((resolve, reject) => {
      resolvePlay = resolve
      rejectPlay = reject
    })
    const play = vi.fn(() => pendingPlay)
    const pause = vi.fn(() => {
      paused = true
      video.dispatchEvent(new Event('pause'))
    })
    Object.defineProperties(video, {
      paused: { configurable: true, get: () => paused },
      play: { configurable: true, value: play },
      pause: { configurable: true, value: pause },
    })
    video.currentTime = 0.05
    act(() => requiredElement<HTMLButtonElement>('.playback-toggle').click())

    await renderContextPlayer(client, playerJobId, { active: false })
    expect(pause).toHaveBeenCalledTimes(1)
    await renderContextPlayer(client, playerJobId, { active: true })
    await act(async () => {
      if (outcome === 'resolve') {
        paused = false
        video.dispatchEvent(new Event('play'))
        resolvePlay()
      } else {
        rejectPlay(new Error('Playback was interrupted by hiding the panel.'))
      }
      await pendingPlay.catch(() => {})
    })

    expect(video.paused).toBe(true)
    expect(video.currentTime).toBe(0.05)
    expect(play).toHaveBeenCalledTimes(1)
    expect(requiredElement('.playback-toggle').textContent).toBe('再生')
    expect(requiredElement<HTMLElement>('.player-status').dataset.playerStatus).toBe('available')
    expect(client.sentReads).toHaveLength(1)
    expect(client.sentSeeks).toEqual([])
  })

  it('changes display modes without rewriting the actual frame, media, source, or output time', async () => {
    const client = new FakeBridgeClient()
    const onSelectionChange = vi.fn()
    await renderContextPlayer(client, playerJobId, { onSelectionChange })
    const response = createReadResponseWithOutputTime(client.sentReads[0]!.requestId)
    if (response.result.status !== 'available') throw new Error('Expected evidence.')
    act(() => client.emitRead(response))
    const video = requiredElement<HTMLVideoElement>('video')
    const before = JSON.stringify(response.result.snapshot)
    const selection = onSelectionChange.mock.calls.at(-1)![0] as PlayerExactFrameSelection
    const notifications = onSelectionChange.mock.calls.length
    video.currentTime = 0.02

    for (const orientation of ['landscape', 'portrait'] as const) {
      await renderContextPlayer(client, playerJobId, {
        onSelectionChange,
        previewModes: {
          ...initialPreviewModeState,
          orientation,
          subtitlesVisible: false,
          genericUiMaskVisible: true,
          thumbnailReduced: true,
          mobilePreview: true,
          audioOnly: orientation === 'portrait',
        },
      })
      expect(requiredElement('video')).toBe(video)
      expect(video.currentTime).toBe(0.02)
      expect(timeDomainText()).toEqual(['Source-1/10', 'Proxy0/1', 'Output1001/48000'])
      expect(requiredElement<HTMLElement>('.video-stage').dataset.previewOrientation).toBe(orientation)
      if (orientation === 'landscape') {
        expect(requiredElement('[data-mask-kind="generic-offline"]').getAttribute('data-platform-fidelity')).toBe('not-claimed')
      }
    }
    expect(selection.frame).toBe(response.result.snapshot.frames[0])
    expect(selection).toMatchObject({
      jobId: playerJobId,
      projectId: playerProjectId,
      sourceAssetId: playerSourceAssetId,
      playerEvidenceDigest,
    })
    expect(JSON.stringify(response.result.snapshot)).toBe(before)
    expect(onSelectionChange).toHaveBeenCalledTimes(notifications)
    expect(client.sentReads).toHaveLength(1)
    expect(client.sentSeeks).toEqual([])
    expect(requiredElement('.player-card').getAttribute('data-output-verification')).toBe('not-verified')
  })

  async function renderContextPlayer(
    client: FakeBridgeClient,
    jobId: string | null,
    props: Omit<PlayerPanelProps, 'createClient' | 'context'> = {},
  ): Promise<void> {
    await act(async () => {
      root.render(
        <PlayerPanel
          createClient={() => client}
          initialJobId={playerJobId}
          {...props}
          context={{ jobId }}
        />,
      )
    })
  }

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
  readonly abandoned: string[] = []
  readonly sentSeeks: Array<
    Readonly<{
      requestId: string
      jobId: string
      evidenceDigest: string
      proxyFrameSequence: number
    }>
  > = []
  disposed = false
  readSubscriptions = 0
  seekSubscriptions = 0
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

  abandonPlayerRequest(requestId: string): void {
    this.abandoned.push(requestId)
  }

  onPlayerReadResponse(
    listener: (response: PlayerReadResponse) => void,
  ): () => void {
    this.readSubscriptions += 1
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
    this.seekSubscriptions += 1
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

const otherJobId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const otherProjectId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const otherSourceAssetId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const otherEvidenceDigest = 'a'.repeat(64)

function createOtherProjectReadResponse(requestId: string): PlayerReadResponse {
  const value = createPlayerReadResponseValue()
  value.requestId = requestId
  const snapshot = (value.result as {
    snapshot: {
      projectId: string
      sourceAssetId: string
      evidenceDigest: string
      proxy: { mediaReference: string }
      sprite: { imageReference: string }
    }
  }).snapshot
  snapshot.projectId = otherProjectId
  snapshot.sourceAssetId = otherSourceAssetId
  snapshot.evidenceDigest = otherEvidenceDigest
  snapshot.proxy.mediaReference = snapshot.proxy.mediaReference.replace(playerProjectId, otherProjectId)
  snapshot.sprite.imageReference = snapshot.sprite.imageReference.replace(playerProjectId, otherProjectId)
  const response = parsePlayerReadResponse(value)
  if (response === null) throw new Error('Expected a valid response for the other project.')
  return response
}

function mockPlayback(video: HTMLVideoElement) {
  let paused = true
  const play = vi.fn(() => {
    paused = false
    video.dispatchEvent(new Event('play'))
    return Promise.resolve()
  })
  const pause = vi.fn(() => {
    paused = true
    video.dispatchEvent(new Event('pause'))
  })
  Object.defineProperties(video, {
    paused: { configurable: true, get: () => paused },
    play: { configurable: true, value: play },
    pause: { configurable: true, value: pause },
  })
  return { play, pause }
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
