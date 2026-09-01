// @vitest-environment jsdom

import { readFileSync } from 'node:fs'
import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import type { PlayerExactFrameSelection } from '../player'
import {
  playerEvidenceDigest,
  playerJobId,
  playerProjectId,
  playerSourceAssetId,
} from '../player/playerTestFixture'
import { GenericUiMask } from './GenericUiMask'
import { PreviewModeControls } from './PreviewModeControls'
import { PreviewModeProvider } from './PreviewModeProvider'

describe('PreviewModeControls', () => {
  let container: HTMLDivElement
  let root: Root

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
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

  it('uses keyboard-native controls for every canonical display choice', () => {
    renderControls(null)

    const orientationInputs = container.querySelectorAll<HTMLInputElement>(
      'input[type="radio"][name="preview-orientation"]',
    )
    const toggles = container.querySelectorAll<HTMLInputElement>(
      'input[type="checkbox"]',
    )
    expect(orientationInputs).toHaveLength(2)
    expect([...orientationInputs].map((input) => input.value)).toEqual([
      'landscape',
      'portrait',
    ])
    expect(toggles).toHaveLength(5)
    const controls = [...orientationInputs, ...toggles]
    expect(controls.every(
      (input) => input.closest('label') !== null,
    )).toBe(true)
    for (const input of controls) {
      act(() => input.focus())
      expect(input.tabIndex).toBe(0)
      expect(document.activeElement).toBe(input)
    }
    expect(container.textContent).toContain('字幕を表示')
    expect(container.textContent).toContain('汎用 UI mask')
    expect(container.textContent).toContain('サムネイル縮小')
    expect(container.textContent).toContain('mobile 配置')
    expect(container.textContent).toContain('音声のみ')
  })

  it('changes subtitle visibility and orientation with keyboard input', async () => {
    renderControls(null)
    const user = userEvent.setup({ document })
    const card = requiredElement<HTMLElement>('.preview-mode-card')
    const subtitles = [...container.querySelectorAll<HTMLInputElement>(
      'input[type="checkbox"]',
    )].find((input) => input.closest('label')?.textContent === '字幕を表示')
    if (subtitles === undefined) {
      throw new Error('Expected the subtitle visibility control.')
    }

    act(() => subtitles.focus())
    expect(subtitles.checked).toBe(true)
    await act(async () => user.keyboard('[Space]'))
    expect(subtitles.checked).toBe(false)
    expect(card.dataset.subtitlesVisible).toBe('false')

    const landscape = requiredElement<HTMLInputElement>(
      'input[value="landscape"]',
    )
    const portrait = requiredElement<HTMLInputElement>(
      'input[value="portrait"]',
    )
    act(() => landscape.focus())
    await act(async () => user.keyboard('[Space]'))
    expect(landscape.checked).toBe(true)
    expect(card.dataset.previewOrientation).toBe('landscape')

    act(() => portrait.focus())
    await act(async () => user.keyboard('[Space]'))
    expect(document.activeElement).toBe(portrait)
    expect(portrait.checked).toBe(true)
    expect(card.dataset.previewOrientation).toBe('portrait')
  })

  it('shows missing media explicitly while all switches remain offline', () => {
    let fetchCalls = 0
    const originalFetch = globalThis.fetch
    globalThis.fetch = (() => {
      fetchCalls += 1
      return Promise.reject(new Error('Network access is not allowed.'))
    }) as typeof fetch

    try {
      renderControls(null)
      expect(container.textContent).toContain(
        '動画・媒体のプレビュー根拠がありません',
      )

      clickInput('input[value="landscape"]')
      clickInput('input[value="portrait"]')
      for (const input of container.querySelectorAll<HTMLInputElement>(
        'input[type="checkbox"]',
      )) {
        act(() => input.click())
      }

      const card = requiredElement<HTMLElement>('.preview-mode-card')
      expect(card.dataset.previewOrientation).toBe('portrait')
      expect(card.dataset.subtitlesVisible).toBe('false')
      expect(card.dataset.genericMask).toBe('true')
      expect(card.dataset.thumbnailReduced).toBe('true')
      expect(card.dataset.mobilePreview).toBe('true')
      expect(card.dataset.audioOnly).toBe('true')
      expect(fetchCalls).toBe(0)
    } finally {
      globalThis.fetch = originalFetch
    }
  })

  it('reads the current U-007 frame tuple without copying a second time state', () => {
    renderControls(createSelection())

    const position = requiredElement<HTMLOutputElement>(
      '.preview-mode-position',
    )
    expect(position.dataset.mediaAvailable).toBe('true')
    expect(position.textContent).toBe(
      '共有 timeline: proxy frame 2・Source 1/20・Proxy 1/12',
    )
    expect(container.innerHTML).not.toContain(playerEvidenceDigest)
    expect(container.innerHTML).not.toContain(playerJobId)
    expect(container.innerHTML).not.toContain(playerProjectId)
    expect(container.innerHTML).not.toMatch(/file:\/\/|C:\\|S-1-5-/)
  })

  it('renders the generic mask deterministically without platform claims', () => {
    const first = renderToStaticMarkup(
      <GenericUiMask visible audioOnly={false} />,
    )
    const second = renderToStaticMarkup(
      <GenericUiMask visible audioOnly={false} />,
    )

    expect(first).toBe(second)
    expect(first).toContain('data-mask-kind="generic-offline"')
    expect(first).toContain('data-platform-fidelity="not-claimed"')
    expect(first).toContain('プラットフォーム固有 UI の忠実な再現ではありません')
    expect(first).not.toMatch(/<script|<img|https?:|azure/i)
    expect(
      renderToStaticMarkup(<GenericUiMask visible audioOnly />),
    ).toBe('')
  })

  it('contains no bridge, seek, network, dynamic HTML, or Azure dependency', () => {
    const sources = [
      './GenericUiMask.tsx',
      './PreviewModeControls.tsx',
      './PreviewModeProvider.tsx',
      './previewModeState.ts',
    ].map((relativePath) =>
      readFileSync(new URL(relativePath, import.meta.url), 'utf8'),
    ).join('\n')

    expect(sources).not.toContain('dangerouslySetInnerHTML')
    expect(sources).not.toContain('.innerHTML')
    expect(sources).not.toMatch(/\beval\s*\(|new Function|XMLHttpRequest|WebSocket/)
    expect(sources).not.toMatch(/\bfetch\s*\(|createHostBridgeClient/)
    expect(sources).not.toMatch(/sendPlayerSeek|requestSourceSeek|\bAzure\b/i)
  })

  function renderControls(
    selection: PlayerExactFrameSelection | null,
  ): void {
    act(() => {
      root.render(
        <PreviewModeProvider>
          <PreviewModeControls selection={selection} />
        </PreviewModeProvider>,
      )
    })
  }

  function clickInput(selector: string): void {
    act(() => requiredElement<HTMLInputElement>(selector).click())
  }

  function requiredElement<T extends Element>(selector: string): T {
    const element = container.querySelector(selector)
    if (element === null) {
      throw new Error(`Expected element matching ${selector}.`)
    }
    return element as T
  }
})

function createSelection(): PlayerExactFrameSelection {
  return {
    jobId: playerJobId,
    projectId: playerProjectId,
    sourceAssetId: playerSourceAssetId,
    playerEvidenceDigest,
    frame: {
      proxyFrameSequence: 2,
      proxyPresentationTime: { value: '1', rate: '12' },
      sourceFrameSequence: 2,
      sourcePresentationTime: { value: '1', rate: '20' },
      outputPresentationTime: null,
      proxyDuration: { value: '1', rate: '30' },
      waveformChannels: [],
      spriteCell: null,
    },
  }
}