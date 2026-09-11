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
import { PreviewModeControls, type PreviewModeControlsProps } from './PreviewModeControls'
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
    renderControls(createSelection())

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
    renderControls(createSelection())
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

      for (const input of container.querySelectorAll<HTMLInputElement>('input')) {
        expect(input.disabled).toBe(true)
        act(() => input.click())
      }
      const emptyCard = requiredElement<HTMLElement>('.preview-mode-card')
      expect(emptyCard.dataset.previewOrientation).toBe('source')
      expect(emptyCard.dataset.subtitlesVisible).toBe('true')
      expect(emptyCard.dataset.genericMask).toBe('false')
      expect(emptyCard.dataset.thumbnailReduced).toBe('false')
      expect(emptyCard.dataset.mobilePreview).toBe('false')
      expect(emptyCard.dataset.audioOnly).toBe('false')
      expect(fetchCalls).toBe(0)

      renderControls(createSelection())
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

  it('uses compact workflow controls with explicit proxy and platform fidelity limits', () => {
    renderControls(createSelection(), { context: { jobId: playerJobId } })

    expect(requiredElement<HTMLElement>('.preview-mode-workflow').hidden).toBe(false)
    expect(container.querySelector('.eyebrow, .preview-mode-notes, .field-help, form')).toBeNull()
    expect(requiredElement('.preview-mode-position').textContent).toBe(
      '代理フレーム 3・Source 1/20・Proxy 1/12',
    )
    const note = requiredElement<HTMLElement>('.preview-mode-fidelity')
    expect(note.dataset.outputVerification).toBe('not-verified')
    expect(note.dataset.platformFidelity).toBe('not-claimed')
    expect(note.textContent).toContain('最終出力の画質・コーデック、配信先 UI・セーフゾーンは未検証')
    expect(container.textContent).toContain('汎用 UI mask（配信先未検証）')
    expect([...container.querySelectorAll<HTMLInputElement>('input')].every((input) => !input.disabled)).toBe(true)
    expect(container.innerHTML).not.toContain(playerEvidenceDigest)
    expect(container.innerHTML).not.toContain(playerJobId)
    expect(container.innerHTML).not.toContain(playerProjectId)
    expect(container.innerHTML).not.toContain(playerSourceAssetId)
  })

  it.each([null, '', 'invalid', 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'])(
    'hides a stale selection for context %s and disables all modes without changing their state',
    (jobId) => {
      const selection = createSelection()
      renderControls(selection, { context: { jobId: playerJobId } })
      clickInput('input[value="portrait"]')
      const portrait = requiredElement<HTMLInputElement>('input[value="portrait"]')

      renderControls(selection, { context: { jobId } })

      expect(requiredElement('.preview-mode-position').getAttribute('data-media-available')).toBe('false')
      expect(requiredElement('.preview-mode-position').textContent).toBe('動画・媒体のプレビュー根拠がありません')
      expect(container.textContent).not.toContain('Source 1/20')
      for (const input of container.querySelectorAll<HTMLInputElement>('input')) {
        expect(input.disabled).toBe(true)
        act(() => input.click())
      }
      expect(requiredElement('input[value="portrait"]')).toBe(portrait)
      expect(portrait.checked).toBe(true)
      expect(selection.frame.outputPresentationTime).toBeNull()
    },
  )

  it('keeps the same controls and mode choices mounted while inactive and on return', () => {
    const selection = createSelection()
    renderControls(selection, { context: { jobId: playerJobId } })
    clickInput('input[value="portrait"]')
    const inputs = [...container.querySelectorAll<HTMLInputElement>('input')]
    for (const input of inputs.filter((candidate) => candidate.type === 'checkbox')) {
      act(() => input.click())
    }
    const checked = inputs.map((input) => input.checked)
    const card = requiredElement<HTMLElement>('.preview-mode-card')

    renderControls(selection, { context: { jobId: playerJobId }, active: false })

    expect(requiredElement('.preview-mode-card')).toBe(card)
    expect(card.hidden).toBe(true)
    expect(card.hasAttribute('inert')).toBe(true)
    expect(inputs.every((input) => input.disabled)).toBe(true)
    for (const input of inputs) act(() => input.click())
    expect(inputs.map((input) => input.checked)).toEqual(checked)

    renderControls(selection, { context: { jobId: playerJobId }, active: true })

    expect(requiredElement('.preview-mode-card')).toBe(card)
    expect(card.hidden).toBe(false)
    expect(card.hasAttribute('inert')).toBe(false)
    expect(inputs.every((input) => !input.disabled)).toBe(true)
    const returnedInputs = [...container.querySelectorAll('input')]
    expect(returnedInputs.every((input, index) => input === inputs[index])).toBe(true)
    expect(inputs.map((input) => input.checked)).toEqual(checked)
  })

  it('never mutates the supplied source, evidence identity, or actual frame to simulate output', () => {
    const selection = createSelection()
    Object.freeze(selection.frame)
    Object.freeze(selection)
    const before = JSON.stringify(selection)
    renderControls(selection, { context: { jobId: playerJobId } })
    clickInput('input[value="landscape"]')
    clickInput('input[value="portrait"]')
    for (const input of container.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')) {
      act(() => input.click())
    }

    expect(JSON.stringify(selection)).toBe(before)
    expect(selection.frame.outputPresentationTime).toBeNull()
    expect(selection).toMatchObject({
      jobId: playerJobId,
      projectId: playerProjectId,
      sourceAssetId: playerSourceAssetId,
      playerEvidenceDigest,
    })
    expect(requiredElement('.preview-mode-position').textContent).toBe('代理フレーム 3・Source 1/20・Proxy 1/12')
    expect(container.querySelector('video, canvas')).toBeNull()

    renderControls(null, { context: { jobId: playerJobId } })
    expect(requiredElement('.preview-mode-position').getAttribute('data-media-available')).toBe('false')
    expect([...container.querySelectorAll<HTMLInputElement>('input')].every((input) => input.disabled)).toBe(true)
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
    props: Omit<PreviewModeControlsProps, 'selection'> = {},
  ): void {
    act(() => {
      root.render(
        <PreviewModeProvider>
          <PreviewModeControls selection={selection} {...props} />
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