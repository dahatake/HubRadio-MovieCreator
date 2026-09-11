// @vitest-environment jsdom

import { act, useEffect } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { ComparePanel, type ComparePanelProps } from './ComparePanel'
import {
  ComparisonStateProvider,
  useComparisonState,
  type ComparisonStateContextValue,
} from './CompareStateProvider'
import type { ComparisonBridgeClient } from './compareBridgeSession'
import {
  parseComparisonReadResponse,
  type ComparisonReadRequest,
  type ComparisonReadResponse,
} from './compareProtocol'
import {
  comparisonLeftDigest,
  comparisonLeftVersionId,
  comparisonRightDigest,
  comparisonRightVersionId,
  createComparisonResponseValue,
  hostileComparisonReason,
} from './compareTestFixture'

describe('ComparisonStateProvider integration', () => {
  let container: HTMLDivElement
  let root: Root
  let mounted: boolean
  let client: FakeComparisonClient

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    mounted = true
    client = new FakeComparisonClient()
    act(() => {
      root.render(
        <ComparisonStateProvider createClient={() => client}>
          <ComparePanel />
        </ComparisonStateProvider>,
      )
    })
  })

  afterEach(() => {
    if (mounted) {
      act(() => root.unmount())
    }
    container.remove()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  it('sends one exact request and safely focuses durable comparison evidence', () => {
    fillTargets()
    const submitButton = requiredElement<HTMLButtonElement>(
      '.comparison-submit-row button',
    )
    expect(submitButton.disabled).toBe(false)

    submit(requiredElement<HTMLFormElement>('.comparison-form'))
    expect(client.sent).toHaveLength(1)
    const request = requiredSent(0)
    expect(request.params).toEqual({
      leftVersionId: comparisonLeftVersionId,
      leftVersionDigest: comparisonLeftDigest,
      rightVersionId: comparisonRightVersionId,
      rightVersionDigest: comparisonRightDigest,
    })
    expect(JSON.stringify(request)).not.toContain('actor')
    expect(JSON.stringify(request)).not.toContain('path')
    expect(submitButton.disabled).toBe(true)

    submit(requiredElement<HTMLFormElement>('.comparison-form'))
    expect(client.sent).toHaveLength(1)

    completeAvailable(request)
    const resultTitle = requiredElement<HTMLHeadingElement>(
      '#comparison-result-title',
    )
    expect(document.activeElement).toBe(resultTitle)
    expect(container.textContent).toContain(comparisonLeftDigest)
    expect(container.textContent).toContain(comparisonRightDigest)
    expect(container.textContent).toContain('9/2 秒')
    expect(container.textContent).toContain(hostileComparisonReason)
    expect(container.querySelector('img')).toBeNull()
    expect(container.innerHTML).toContain('&lt;img')
    expect(
      (window as Window & { __comparison_attack?: boolean })
        .__comparison_attack,
    ).toBeUndefined()

    const featureRows = container.querySelectorAll(
      '.comparison-feature-table tbody tr',
    )
    expect(featureRows).toHaveLength(8)
    expect(container.textContent).toContain('想定公開先')
    for (const row of featureRows) {
      expect(row.textContent?.match(/データ未提供/g)).toHaveLength(2)
    }
  })

  it('supports wrapped arrow, Home, and End navigation with one tab stop', () => {
    fillTargets()
    submit(requiredElement<HTMLFormElement>('.comparison-form'))
    completeAvailable(requiredSent(0))
    const tabs = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="tab"]'),
    )
    expect(tabs).toHaveLength(3)
    expect(tabs.map((tab) => tab.tabIndex)).toEqual([0, -1, -1])

    act(() => {
      tabs[0]!.focus()
      tabs[0]!.dispatchEvent(new KeyboardEvent('keydown', {
        key: 'ArrowRight',
        bubbles: true,
        cancelable: true,
      }))
    })
    expect(document.activeElement).toBe(tabs[1])
    expect(requiredElement<HTMLElement>('#comparison-panel-ranges').hidden)
      .toBe(false)
    expect(container.textContent).toContain('開始 21/2 秒')

    act(() => {
      tabs[1]!.dispatchEvent(new KeyboardEvent('keydown', {
        key: 'ArrowLeft',
        bubbles: true,
        cancelable: true,
      }))
    })
    act(() => {
      tabs[0]!.focus()
      tabs[0]!.dispatchEvent(new KeyboardEvent('keydown', {
        key: 'ArrowLeft',
        bubbles: true,
        cancelable: true,
      }))
    })
    expect(document.activeElement).toBe(tabs[2])
    expect(tabs[2]!.getAttribute('aria-selected')).toBe('true')
    expect(requiredElement<HTMLElement>('#comparison-panel-changes').hidden)
      .toBe(false)
    expect(container.textContent).toContain('採用差し替え')

    act(() => {
      tabs[2]!.dispatchEvent(new KeyboardEvent('keydown', {
        key: 'Home',
        bubbles: true,
        cancelable: true,
      }))
    })
    expect(document.activeElement).toBe(tabs[0])
    expect(tabs.map((tab) => tab.tabIndex)).toEqual([0, -1, -1])

    act(() => {
      tabs[0]!.dispatchEvent(new KeyboardEvent('keydown', {
        key: 'End',
        bubbles: true,
        cancelable: true,
      }))
    })
    expect(document.activeElement).toBe(tabs[2])
  })

  it('labels a fully identical same-version comparison without changes', () => {
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-left-version-id'),
      comparisonLeftVersionId,
    )
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-left-version-digest'),
      comparisonLeftDigest,
    )
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-right-version-id'),
      comparisonLeftVersionId,
    )
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-right-version-digest'),
      comparisonLeftDigest,
    )
    submit(requiredElement<HTMLFormElement>('.comparison-form'))
    const request = requiredSent(0)
    const value = createComparisonResponseValue(request.requestId)
    value.result.comparison.right = structuredClone(value.result.comparison.left)
    value.result.comparison.elementChanges = []
    value.result.comparison.adoptionChanges = []
    const response = parseComparisonReadResponse(value)
    if (response === null) {
      throw new Error('Expected a valid same-version response.')
    }

    act(() => client.emit(response))
    expect(container.textContent).toContain('同一版')
    expect(container.textContent).toContain('要素の変更はありません。')
    expect(container.textContent).toContain('Suggestion 採用の変更はありません。')
  })

  it('rejects invalid local targets and shows only fixed native failure text', () => {
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-left-version-id'),
      `${comparisonLeftVersionId.slice(0, -1)}A`,
    )
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-left-version-digest'),
      comparisonLeftDigest,
    )
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-right-version-id'),
      comparisonRightVersionId,
    )
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-right-version-digest'),
      comparisonRightDigest,
    )
    const form = requiredElement<HTMLFormElement>('.comparison-form')
    const button = requiredElement<HTMLButtonElement>(
      '.comparison-submit-row button',
    )
    expect(button.disabled).toBe(true)
    submit(form)
    expect(client.sent).toHaveLength(0)

    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-left-version-id'),
      comparisonLeftVersionId,
    )
    submit(form)
    const request = requiredSent(0)
    const failure: ComparisonReadResponse = {
      version: 1,
      requestId: request.requestId,
      method: 'compare.read',
      result: { status: 'invalidComparison', comparison: null },
    }
    act(() => client.emit(failure))

    const alert = requiredElement<HTMLElement>('.comparison-error[role="alert"]')
    expect(alert.textContent).toContain('異なる案件の構成版は比較できません。')
    expect(alert.textContent).not.toContain('projectId')
    expect(alert.textContent).not.toContain('C:\\')
    expect(alert.textContent).not.toContain('exception')
  })

  it('abandons one in-flight request and disposes the client on unmount', () => {
    fillTargets()
    submit(requiredElement<HTMLFormElement>('.comparison-form'))
    const request = requiredSent(0)

    act(() => root.unmount())
    mounted = false
    expect(client.abandoned).toEqual([request.requestId])
    expect(client.unsubscribeCount).toBe(1)
    expect(client.disposeCount).toBe(1)
  })

  function fillTargets(): void {
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-left-version-id'),
      comparisonLeftVersionId,
    )
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-left-version-digest'),
      comparisonLeftDigest,
    )
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-right-version-id'),
      comparisonRightVersionId,
    )
    setControlValue(
      requiredElement<HTMLInputElement>('#comparison-right-version-digest'),
      comparisonRightDigest,
    )
  }

  function completeAvailable(request: ComparisonReadRequest): void {
    const response = parseComparisonReadResponse(
      createComparisonResponseValue(request.requestId),
    )
    if (response === null) {
      throw new Error('Expected a valid comparison response.')
    }
    act(() => client.emit(response))
  }

  function requiredSent(index: number): ComparisonReadRequest {
    const request = client.sent[index]
    if (request === undefined) {
      throw new Error(`Expected sent comparison request ${index}.`)
    }
    return request
  }

  function requiredElement<T extends Element>(selector: string): T {
    const element = container.querySelector(selector)
    if (element === null) {
      throw new Error(`Expected element matching ${selector}.`)
    }
    return element as T
  }
})

describe('ComparePanel workflow context', () => {
  let container: HTMLDivElement
  let outside: HTMLButtonElement
  let root: Root
  let client: FakeComparisonClient
  let createClient: () => ComparisonBridgeClient
  let state: ComparisonStateContextValue['state']

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    outside = document.createElement('button')
    outside.textContent = '別の画面'
    document.body.append(container, outside)
    outside.focus()
    root = createRoot(container)
    client = new FakeComparisonClient()
    createClient = () => client
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    outside.remove()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  it.each([
    { name: 'both missing', context: { left: null, right: null } },
    { name: 'left missing', context: { ...selectedPair(), left: null } },
    { name: 'right missing', context: { ...selectedPair(), right: null } },
    {
      name: 'invalid left ID',
      context: {
        ...selectedPair(),
        left: { ...selectedPair().left, versionId: 'invalid' },
      },
    },
    {
      name: 'empty right ID',
      context: {
        ...selectedPair(),
        right: {
          ...selectedPair().right,
          versionId: '00000000-0000-0000-0000-000000000000',
        },
      },
    },
    {
      name: 'short left digest',
      context: {
        ...selectedPair(),
        left: { ...selectedPair().left, versionDigest: 'a'.repeat(63) },
      },
    },
    {
      name: 'uppercase right digest',
      context: {
        ...selectedPair(),
        right: { ...selectedPair().right, versionDigest: 'B'.repeat(64) },
      },
    },
  ])('keeps an unavailable workflow pair disabled without a read: $name', ({ context }) => {
    renderPanel({ context })

    expect(container.querySelector('form')).toBeNull()
    expect(container.querySelector('input')).toBeNull()
    expect(requiredElement<HTMLButtonElement>('.comparison-submit-row button').disabled)
      .toBe(true)
    expect(requiredElement<HTMLElement>('[role="status"]').textContent)
      .toBe('比較する2版が未選択です。')
    expect(container.textContent).not.toContain('UUID')
    expect(container.textContent).not.toContain('指定してください')
    expect(client.sent).toHaveLength(0)
    expect(state).toMatchObject({
      leftVersionIdInput: '',
      leftVersionDigestInput: '',
      rightVersionIdInput: '',
      rightVersionDigestInput: '',
      pending: null,
      comparison: null,
    })
  })

  it('loads the exact explicit pair once after inputs commit, not on object replacement or ACK', () => {
    renderPanel({ context: selectedPair() })

    expect(container.querySelector('form')).toBeNull()
    expect(container.querySelector('input')).toBeNull()
    expect(client.sent).toHaveLength(1)
    const request = requiredSent(0)
    expect(request.params).toEqual({
      leftVersionId: comparisonLeftVersionId,
      leftVersionDigest: comparisonLeftDigest,
      rightVersionId: comparisonRightVersionId,
      rightVersionDigest: comparisonRightDigest,
    })
    expect(state.pending?.target).toEqual(request.params)
    expect(state.error).toBeNull()

    renderPanel({ context: selectedPair() })
    expect(client.sent).toHaveLength(1)
    emitAvailable(request)
    expect(document.activeElement)
      .toBe(requiredElement<HTMLHeadingElement>('#comparison-result-title'))
    renderPanel({ context: selectedPair() })
    expect(client.sent).toHaveLength(1)
    expect(state.pending).toBeNull()
    expect(state.comparison?.left.versionId).toBe(comparisonLeftVersionId)
    expect(state.comparison?.right.versionDigest).toBe(comparisonRightDigest)
    expect(client.disposeCount).toBe(0)
  })

  it.each(['left', 'right'] as const)('loads a changed %s digest even when both version IDs stay the same', (side) => {
    renderPanel({ context: selectedPair() })
    emitAvailable(requiredSent(0))
    const context = selectedPair()
    context[side].versionDigest = 'c'.repeat(64)
    renderPanel({ context })

    expect(client.sent).toHaveLength(2)
    const request = requiredSent(1)
    expect(request.params[`${side}VersionDigest`]).toBe('c'.repeat(64))
    expect(request.params.leftVersionId).toBe(comparisonLeftVersionId)
    expect(request.params.rightVersionId).toBe(comparisonRightVersionId)
    emitAvailable(request)
    renderPanel({ context: structuredClone(context) })
    expect(client.sent).toHaveLength(2)
    expect(state.comparison?.[side].versionDigest).toBe('c'.repeat(64))
  })

  it('preserves the pending identity and waits for its ACK before loading only the latest pair', () => {
    renderPanel({ context: selectedPair() })
    const first = requiredSent(0)
    const pending = state.pending
    const skipped = selectedPair()
    skipped.right.versionDigest = 'c'.repeat(64)
    renderPanel({ context: skipped })
    const latest = { left: selectedPair().right, right: selectedPair().left }
    renderPanel({ context: latest })

    expect(client.sent).toHaveLength(1)
    expect(client.abandoned).toHaveLength(0)
    expect(state.pending).toBe(pending)
    expect(state).toMatchObject({
      leftVersionIdInput: comparisonLeftVersionId,
      leftVersionDigestInput: comparisonLeftDigest,
      rightVersionIdInput: comparisonRightVersionId,
      rightVersionDigestInput: comparisonRightDigest,
    })

    emitAvailable({
      ...first,
      params: { ...first.params, rightVersionDigest: 'd'.repeat(64) },
    })
    expect(state.pending).toBe(pending)
    expect(client.sent).toHaveLength(1)

    emitAvailable(first)
    expect(client.sent).toHaveLength(2)
    const second = requiredSent(1)
    expect(second.params).toEqual({
      leftVersionId: comparisonRightVersionId,
      leftVersionDigest: comparisonRightDigest,
      rightVersionId: comparisonLeftVersionId,
      rightVersionDigest: comparisonLeftDigest,
    })
    expect(state.pending?.target).toEqual(second.params)
    expect(state.comparison).toBeNull()
    expect(state.error).toBeNull()
    expect(container.querySelector('#comparison-result-title')).toBeNull()
    expect(document.activeElement).toBe(outside)

    emitAvailable(first)
    expect(state.pending?.requestId).toBe(second.requestId)
    emitAvailable(second)
    expect(state.comparison?.left.versionId).toBe(comparisonRightVersionId)
    expect(state.comparison?.right.versionDigest).toBe(comparisonLeftDigest)
    expect(client.sent).toHaveLength(2)
  })

  it('does not fill a missing side or show an old result when a pair is cleared during a read', () => {
    renderPanel({ context: selectedPair() })
    const request = requiredSent(0)
    const pending = state.pending
    renderPanel({ context: { left: selectedPair().left, right: null } })

    expect(state.pending).toBe(pending)
    expect(requiredElement<HTMLButtonElement>('.comparison-submit-row button').disabled)
      .toBe(true)
    emitAvailable(request)

    expect(client.sent).toHaveLength(1)
    expect(state.comparison?.right.versionId).toBe(comparisonRightVersionId)
    expect(container.querySelector('#comparison-result-title')).toBeNull()
    expect(container.querySelector('form')).toBeNull()
    expect(requiredElement<HTMLElement>('[role="status"]').textContent)
      .toContain('未選択')
    expect(requiredElement<HTMLButtonElement>('.comparison-submit-row button').disabled)
      .toBe(true)
    expect(document.activeElement).toBe(outside)
  })

  it('neither changes inputs nor reads while inactive and never focuses an inactive response on return', () => {
    renderPanel({ context: selectedPair(), active: false })
    expect(client.sent).toHaveLength(0)
    expect(state.leftVersionIdInput).toBe('')
    expect(state.rightVersionIdInput).toBe('')
    expect(document.activeElement).toBe(outside)

    renderPanel({ context: selectedPair(), active: true })
    const request = requiredSent(0)
    renderPanel({ context: selectedPair(), active: false })
    emitAvailable(request)
    expect(state.comparison?.left.versionId).toBe(comparisonLeftVersionId)
    expect(state.comparison?.right.versionId).toBe(comparisonRightVersionId)
    expect(document.activeElement).toBe(outside)

    renderPanel({ context: selectedPair(), active: true })
    expect(document.activeElement).toBe(outside)
    expect(client.sent).toHaveLength(1)
  })

  it('blurs a focused result when hidden and restores the pair and selected tab without reloading', () => {
    renderPanel({ context: selectedPair() })
    emitAvailable(requiredSent(0))
    const panel = requiredElement<HTMLElement>('.comparison-panel')
    const resultTitle = requiredElement<HTMLHeadingElement>('#comparison-result-title')
    const ranges = requiredElement<HTMLButtonElement>('#comparison-tab-ranges')
    const loadedState = state
    act(() => {
      ranges.click()
      resultTitle.focus()
    })
    expect(document.activeElement).toBe(resultTitle)

    renderPanel({ context: selectedPair(), active: false })
    expect(panel.hidden).toBe(true)
    expect(panel.hasAttribute('inert')).toBe(true)
    expect(panel.contains(document.activeElement)).toBe(false)
    expect(document.activeElement).toBe(document.body)
    // Check tab stops without simulating native Tab in JSDOM.
    const tabStops = Array.from(panel.querySelectorAll<HTMLElement>(
      'a[href], button, input, select, textarea, [tabindex]',
    )).filter((element) => element.tabIndex >= 0 && !element.matches(':disabled'))
    expect(tabStops).toEqual([])
    expect(state).toBe(loadedState)
    expect(client.sent).toHaveLength(1)

    outside.focus()
    renderPanel({ context: selectedPair(), active: true })
    expect(requiredElement<HTMLElement>('.comparison-panel')).toBe(panel)
    expect(panel.hidden).toBe(false)
    expect(panel.hasAttribute('inert')).toBe(false)
    expect(requiredElement<HTMLHeadingElement>('#comparison-result-title')).toBe(resultTitle)
    expect(requiredElement<HTMLButtonElement>('#comparison-tab-ranges')).toBe(ranges)
    expect(ranges.disabled).toBe(false)
    expect(ranges.tabIndex).toBe(0)
    expect(ranges.getAttribute('aria-selected')).toBe('true')
    expect(requiredElement<HTMLElement>('#comparison-panel-ranges').hidden).toBe(false)
    expect(state).toBe(loadedState)
    expect(state).toMatchObject({
      leftVersionIdInput: comparisonLeftVersionId,
      leftVersionDigestInput: comparisonLeftDigest,
      rightVersionIdInput: comparisonRightVersionId,
      rightVersionDigestInput: comparisonRightDigest,
      pending: null,
    })
    expect(document.activeElement).toBe(outside)
    expect(client.sent).toHaveLength(1)
    expect(client.abandoned).toHaveLength(0)
    expect(client.disposeCount).toBe(0)
  })

  it('preserves the selected result tab and external focus through inactivity', () => {
    renderPanel({ context: selectedPair() })
    emitAvailable(requiredSent(0))
    const ranges = requiredElement<HTMLButtonElement>('#comparison-tab-ranges')
    const changes = requiredElement<HTMLButtonElement>('#comparison-tab-changes')
    act(() => ranges.click())
    outside.focus()

    renderPanel({ context: selectedPair(), active: false })
    expect(ranges.disabled).toBe(true)
    expect(ranges.tabIndex).toBe(-1)
    act(() => {
      ranges.dispatchEvent(new KeyboardEvent('keydown', {
        key: 'End',
        bubbles: true,
        cancelable: true,
      }))
      changes.click()
    })
    expect(ranges.getAttribute('aria-selected')).toBe('true')
    expect(requiredElement<HTMLElement>('#comparison-panel-ranges').hidden)
      .toBe(false)
    expect(document.activeElement).toBe(outside)

    const changedContext = selectedPair()
    changedContext.right.versionDigest = 'c'.repeat(64)
    renderPanel({ context: changedContext, active: false })
    expect(container.querySelector('#comparison-result-title')).toBeNull()
    expect(state.leftVersionIdInput).toBe(comparisonLeftVersionId)
    expect(state.rightVersionDigestInput).toBe(comparisonRightDigest)
    expect(client.sent).toHaveLength(1)

    renderPanel({ context: selectedPair(), active: true })
    const restoredRanges = requiredElement<HTMLButtonElement>('#comparison-tab-ranges')
    expect(restoredRanges.disabled).toBe(false)
    expect(restoredRanges.tabIndex).toBe(0)
    expect(restoredRanges.getAttribute('aria-selected')).toBe('true')
    expect(requiredElement<HTMLElement>('#comparison-panel-ranges').hidden)
      .toBe(false)
    expect(document.activeElement).toBe(outside)
    expect(client.sent).toHaveLength(1)
  })

  it.each(['notFound', 'staleVersion'] as const)('disables a rejected workflow pair without auto-retry or manual UUID guidance: %s', (status) => {
    renderPanel({ context: selectedPair() })
    const request = requiredSent(0)
    act(() => client.emit({
      version: 1,
      requestId: request.requestId,
      method: 'compare.read',
      result: { status, comparison: null },
    }))
    renderPanel({ context: selectedPair() })

    expect(client.sent).toHaveLength(1)
    expect(container.querySelector('form')).toBeNull()
    expect(requiredElement<HTMLButtonElement>('.comparison-submit-row button').disabled)
      .toBe(true)
    expect(requiredElement<HTMLElement>('[role="alert"]').textContent)
      .toContain('選択した2版を確認できませんでした。')
    expect(container.textContent).not.toContain('版 ID を見直してください')
    expect(container.textContent).not.toContain('UUID')

    const next = selectedPair()
    next.right.versionDigest = 'c'.repeat(64)
    renderPanel({ context: next })
    expect(client.sent).toHaveLength(2)
    expect(requiredSent(1).params.rightVersionDigest).toBe('c'.repeat(64))
    expect(state.error).toBeNull()
  })

  it('keeps the original standalone fields when context is explicitly undefined', () => {
    renderPanel({ context: undefined })

    const panel = requiredElement<HTMLElement>('.comparison-panel')
    expect(panel.hidden).toBe(false)
    expect(panel.hasAttribute('inert')).toBe(false)
    expect(container.querySelector('.comparison-form')).not.toBeNull()
    expect(container.querySelectorAll('.comparison-form input')).toHaveLength(4)
    expect(container.textContent).toContain('小文字 UUID')
    expect(client.sent).toHaveLength(0)
  })

  function selectedPair() {
    return {
      left: { versionId: comparisonLeftVersionId, versionDigest: comparisonLeftDigest },
      right: { versionId: comparisonRightVersionId, versionDigest: comparisonRightDigest },
    }
  }

  function StateProbe() {
    const value = useComparisonState()
    useEffect(() => {
      state = value.state
    }, [value.state])
    return null
  }

  function renderPanel(props: ComparePanelProps): void {
    act(() => {
      root.render(
        <ComparisonStateProvider createClient={createClient}>
          <ComparePanel {...props} />
          <StateProbe />
        </ComparisonStateProvider>,
      )
    })
  }

  function emitAvailable(request: ComparisonReadRequest): void {
    const value = createComparisonResponseValue(request.requestId)
    value.result.comparison.left.versionId = request.params.leftVersionId
    value.result.comparison.left.versionDigest = request.params.leftVersionDigest
    value.result.comparison.right.versionId = request.params.rightVersionId
    value.result.comparison.right.versionDigest = request.params.rightVersionDigest
    const response = parseComparisonReadResponse(value)
    if (response === null) {
      throw new Error('Expected a valid comparison response for the requested pair.')
    }
    act(() => client.emit(response))
  }

  function requiredSent(index: number): ComparisonReadRequest {
    const request = client.sent[index]
    if (request === undefined) {
      throw new Error(`Expected sent comparison request ${index}.`)
    }
    return request
  }

  function requiredElement<T extends Element>(selector: string): T {
    const element = container.querySelector(selector)
    if (element === null) {
      throw new Error(`Expected element matching ${selector}.`)
    }
    return element as T
  }
})

class FakeComparisonClient implements ComparisonBridgeClient {
  readonly sent: ComparisonReadRequest[] = []
  readonly abandoned: string[] = []
  unsubscribeCount = 0
  disposeCount = 0
  private listener: ((response: ComparisonReadResponse) => void) | null = null

  sendComparison(request: ComparisonReadRequest): void {
    this.sent.push(request)
  }

  abandonComparisonRequest(requestId: string): void {
    this.abandoned.push(requestId)
  }

  onComparisonResponse(
    listener: (response: ComparisonReadResponse) => void,
  ): () => void {
    this.listener = listener
    return () => {
      this.unsubscribeCount++
      this.listener = null
    }
  }

  dispose(): void {
    this.disposeCount++
    this.listener = null
  }

  emit(response: ComparisonReadResponse): void {
    this.listener?.(response)
  }
}

function setControlValue(element: HTMLInputElement, value: string): void {
  const setter = Object.getOwnPropertyDescriptor(
    HTMLInputElement.prototype,
    'value',
  )?.set
  if (setter === undefined) {
    throw new Error('Expected the native form-control value setter.')
  }
  act(() => {
    setter.call(element, value)
    element.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

function submit(form: HTMLFormElement): void {
  act(() => {
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  })
}