// @vitest-environment jsdom

import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { ComparePanel } from './ComparePanel'
import { ComparisonStateProvider } from './CompareStateProvider'
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