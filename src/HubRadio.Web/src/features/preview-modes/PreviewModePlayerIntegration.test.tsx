// @vitest-environment jsdom

import { act, useCallback, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import {
  parsePlayerReadResponse,
  parsePlayerSeekResponse,
  type PlayerExactFrameSelection,
  type PlayerReadResponse,
  type PlayerSeekResponse,
} from '../player'
import {
  PlayerPanel,
  type PlayerBridgeClient,
} from '../player/PlayerPanel'
import {
  createPlayerReadResponseValue,
  createPlayerSeekResponseValue,
  playerEvidenceDigest,
  playerJobId,
} from '../player/playerTestFixture'
import { PreviewModeControls } from './PreviewModeControls'
import {
  PreviewModeProvider,
  usePreviewModes,
} from './PreviewModeProvider'

describe('preview modes with the U-007 player', () => {
  let container: HTMLDivElement
  let root: Root
  let client: FakePlayerClient

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    client = new FakePlayerClient()
    act(() => {
      root.render(
        <PreviewModeProvider>
          <PreviewPlayer client={client} />
        </PreviewModeProvider>,
      )
    })
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  it('keeps one authoritative frame while every preview mode changes', () => {
    loadPlayer()
    seekToSecondFrame()
    expect(frameOutput()).toContain('2 / 3')
    expect(sharedPosition()).toContain('proxy frame 1')
    expect(client.sentSeeks).toHaveLength(1)

    clickInput('input[value="landscape"]')
    expect(videoStage().dataset.previewOrientation).toBe('landscape')
    clickInput('input[value="portrait"]')
    expect(videoStage().dataset.previewOrientation).toBe('portrait')

    clickToggle('字幕を表示')
    clickToggle('汎用 UI mask')
    expect(container.querySelector('[data-mask-kind="generic-offline"]'))
      .not.toBeNull()
    clickToggle('サムネイル縮小')
    clickToggle('mobile 配置')
    expect(playerWorkspace().dataset.mobilePreview).toBe('true')
    clickToggle('音声のみ')

    expect(playerWorkspace().dataset.audioOnly).toBe('true')
    expect(container.textContent).toContain('音声のみプレビュー')
    expect(container.querySelector('[data-mask-kind="generic-offline"]'))
      .toBeNull()
    expect(frameOutput()).toContain('2 / 3')
    expect(sharedPosition()).toContain('proxy frame 1')
    expect(client.sentSeeks).toHaveLength(1)
  })

  it('leaves mute entirely user controlled across mode switches', () => {
    loadPlayer()
    const video = requiredElement<HTMLVideoElement>('video')
    const mute = requiredButton('ミュート')
    expect(video.muted).toBe(false)
    expect(mute.getAttribute('aria-pressed')).toBe('false')

    act(() => mute.click())
    expect(video.muted).toBe(true)
    expect(requiredButton('ミュート解除').getAttribute('aria-pressed')).toBe(
      'true',
    )

    clickInput('input[value="portrait"]')
    clickToggle('音声のみ')
    clickToggle('mobile 配置')
    expect(video.muted).toBe(true)
    expect(client.sentSeeks).toEqual([])

    act(() => requiredButton('ミュート解除').click())
    expect(video.muted).toBe(false)
    expect(requiredButton('ミュート').getAttribute('aria-pressed')).toBe(
      'false',
    )
  })

  it('keeps missing video explicit and retains evidence without a mode seek', () => {
    loadPlayer()
    const video = requiredElement<HTMLVideoElement>('video')

    act(() => video.dispatchEvent(new Event('error', { bubbles: true })))
    expect(
      requiredElement<HTMLElement>('.player-status').dataset.playerStatus,
    ).toBe('mediaUnavailable')
    expect(container.textContent).toContain(
      '代理動画を読み込めません。根拠表示のみ利用できます',
    )

    clickInput('input[value="landscape"]')
    clickToggle('汎用 UI mask')
    clickToggle('音声のみ')
    expect(frameOutput()).toContain('1 / 3')
    expect(sharedPosition()).toContain('proxy frame 0')
    expect(client.sentSeeks).toEqual([])
    expect(
      requiredElement<HTMLElement>('.player-status').dataset.playerStatus,
    ).toBe('mediaUnavailable')
  })

  function loadPlayer(): void {
    const form = requiredElement<HTMLFormElement>('.player-load-form')
    act(() => {
      form.dispatchEvent(
        new Event('submit', { bubbles: true, cancelable: true }),
      )
    })
    const read = client.sentReads[0]
    if (read === undefined) {
      throw new Error('Expected one player read request.')
    }
    const value = createPlayerReadResponseValue()
    value.requestId = read.requestId
    const response = parsePlayerReadResponse(value)
    if (response === null) {
      throw new Error('Expected a valid player read response.')
    }
    act(() => client.emitRead(response))
  }

  function seekToSecondFrame(): void {
    act(() => {
      videoStage().dispatchEvent(
        new KeyboardEvent('keydown', {
          key: 'ArrowRight',
          bubbles: true,
          cancelable: true,
        }),
      )
    })
    const seek = client.sentSeeks[0]
    if (seek === undefined) {
      throw new Error('Expected one player seek request.')
    }
    const response = parsePlayerSeekResponse(
      createPlayerSeekResponseValue(
        seek.requestId,
        seek.proxyFrameSequence,
      ),
    )
    if (response === null) {
      throw new Error('Expected a valid player seek response.')
    }
    act(() => client.emitSeek(response))
  }

  function clickInput(selector: string): void {
    act(() => requiredElement<HTMLInputElement>(selector).click())
  }

  function clickToggle(label: string): void {
    const control = [...container.querySelectorAll('label')].find(
      (candidate) => candidate.textContent?.trim() === label,
    )?.querySelector<HTMLInputElement>('input')
    if (control === undefined || control === null) {
      throw new Error(`Expected toggle ${label}.`)
    }
    act(() => control.click())
  }

  function frameOutput(): string {
    return requiredElement<HTMLOutputElement>(
      '#proxy-frame-sequence + output',
    ).textContent ?? ''
  }

  function sharedPosition(): string {
    return requiredElement<HTMLOutputElement>(
      '.preview-mode-position',
    ).textContent ?? ''
  }

  function videoStage(): HTMLDivElement {
    return requiredElement('.video-stage')
  }

  function playerWorkspace(): HTMLDivElement {
    return requiredElement('.player-workspace')
  }

  function requiredButton(text: string): HTMLButtonElement {
    const button = [...container.querySelectorAll('button')].find(
      (candidate) => candidate.textContent?.trim() === text,
    )
    if (button === undefined) {
      throw new Error(`Expected button ${text}.`)
    }
    return button
  }

  function requiredElement<T extends Element>(selector: string): T {
    const element = container.querySelector(selector)
    if (element === null) {
      throw new Error(`Expected element matching ${selector}.`)
    }
    return element as T
  }
})

function PreviewPlayer({
  client,
}: Readonly<{ client: FakePlayerClient }>) {
  const { state } = usePreviewModes()
  const createClient = useCallback(() => client, [client])
  const [selection, setSelection] =
    useState<PlayerExactFrameSelection | null>(null)

  return (
    <>
      <PreviewModeControls selection={selection} />
      <PlayerPanel
        createClient={createClient}
        initialJobId={playerJobId}
        previewModes={state}
        onSelectionChange={setSelection}
      />
    </>
  )
}

class FakePlayerClient implements PlayerBridgeClient {
  readonly sentReads: Array<Readonly<{ requestId: string; jobId: string }>> = []
  readonly sentSeeks: Array<
    Readonly<{
      requestId: string
      jobId: string
      evidenceDigest: string
      proxyFrameSequence: number
    }>
  > = []
  private readonly readListeners = new Set<
    (response: PlayerReadResponse) => void
  >()
  private readonly seekListeners = new Set<
    (response: PlayerSeekResponse) => void
  >()

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
    expect(evidenceDigest).toBe(playerEvidenceDigest)
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
    this.readListeners.clear()
    this.seekListeners.clear()
  }
}