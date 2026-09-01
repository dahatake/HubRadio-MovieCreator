// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import {
  parseDeliveryReadResponse,
  type DeliveryReadRequest,
  type DeliveryReadResponse,
} from './deliveryProtocol'
import { DeliveryPanel } from './DeliveryPanel'
import { DeliveryStateProvider } from './DeliveryStateProvider'
import type { DeliveryBridgeClient } from './deliveryBridgeSession'
import {
  createDeliveryFailureValue,
  createDeliveryResponseValue,
  deliveryVersionDigest,
  deliveryVersionId,
} from './deliveryTestFixture'

describe('DeliveryStateProvider integration', () => {
  let container: HTMLDivElement
  let root: Root
  let client: FakeDeliveryClient

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT =
      true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    client = new FakeDeliveryClient()
    act(() => {
      root.render(
        <DeliveryStateProvider createClient={() => client}>
          <DeliveryPanel />
        </DeliveryStateProvider>,
      )
    })
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  it('loads an exact version then sends an exact arbitrary output-time lookup', () => {
    loadSnapshot()
    const load = requiredSent(0)
    expect(load.params).toEqual({
      versionId: deliveryVersionId,
      outputTime: null,
    })

    completeAvailable(load, false)
    expect(container.textContent).toContain(deliveryVersionDigest)
    expect(container.textContent).toContain('内部限定')

    act(() => {
      requiredElement<HTMLButtonElement>(
        '#delivery-evidence-tab-trace',
      ).click()
    })
    setControlValue(
      requiredElement<HTMLInputElement>('#delivery-trace-value'),
      '1',
    )
    setControlValue(
      requiredElement<HTMLInputElement>('#delivery-trace-rate'),
      '2',
    )
    submit(requiredElement<HTMLFormElement>('.delivery-trace-form'))

    const trace = requiredSent(1)
    expect(trace.params).toEqual({
      versionId: deliveryVersionId,
      outputTime: { value: '1', rate: '2' },
    })
    completeAvailable(trace, true)
    expect(container.textContent).toContain('由来を解決しました')
    expect(container.textContent).toContain('1/2')
  })

  it('surfaces QC failure without retaining previously rendered evidence', () => {
    loadSnapshot()
    const load = requiredSent(0)
    completeAvailable(load, false)
    expect(container.textContent).toContain(deliveryVersionDigest)

    act(() => {
      requiredElement<HTMLButtonElement>('.delivery-load-row .secondary-button')
        .click()
    })
    const refresh = requiredSent(1)
    const failed = parseRequired(createDeliveryFailureValue(
      'qualityControlFailed',
      refresh.requestId,
    ))
    act(() => client.emit(failed))

    expect(container.textContent).toContain('品質確認が失敗したため')
    expect(container.textContent).not.toContain(deliveryVersionDigest)
    expect(container.querySelector('.delivery-workspace')).toBeNull()
  })

  it('does not send an unreduced trace time', () => {
    loadSnapshot()
    completeAvailable(requiredSent(0), false)
    act(() => {
      requiredElement<HTMLButtonElement>(
        '#delivery-evidence-tab-trace',
      ).click()
    })
    setControlValue(
      requiredElement<HTMLInputElement>('#delivery-trace-value'),
      '2',
    )
    setControlValue(
      requiredElement<HTMLInputElement>('#delivery-trace-rate'),
      '4',
    )

    const button = requiredElement<HTMLButtonElement>(
      '.delivery-trace-form button[type="submit"]',
    )
    expect(button.disabled).toBe(true)
    submit(requiredElement<HTMLFormElement>('.delivery-trace-form'))
    expect(client.sent).toHaveLength(1)
  })

  it('moves through every evidence section with roving keyboard focus', async () => {
    loadSnapshot()
    completeAvailable(requiredSent(0), false)
    const user = userEvent.setup({ document })
    const tabs = [...container.querySelectorAll<HTMLButtonElement>(
      '.delivery-evidence-tabs [role="tab"]',
    )]
    expect(tabs).toHaveLength(6)

    act(() => tabs[0]!.focus())
    await act(async () => user.keyboard('[End]'))
    expect(document.activeElement).toBe(tabs[5])
    expect(tabs[5]!.getAttribute('aria-selected')).toBe('true')
    expect(
      requiredElement<HTMLElement>('#delivery-evidence-panel-trace').hidden,
    ).toBe(false)

    await act(async () => user.keyboard('[Home]'))
    expect(document.activeElement).toBe(tabs[0])
    expect(tabs[0]!.getAttribute('aria-selected')).toBe('true')
  })

  it('abandons an in-flight request and disposes its native client on unmount', () => {
    loadSnapshot()
    const pending = requiredSent(0)

    act(() => root.unmount())
    expect(client.abandoned).toEqual([pending.requestId])
    expect(client.unsubscribeCount).toBe(1)
    expect(client.disposeCount).toBe(1)
    root = createRoot(container)
  })

  function loadSnapshot(): void {
    setControlValue(
      requiredElement<HTMLInputElement>('#delivery-version-id'),
      deliveryVersionId,
    )
    submit(requiredElement<HTMLFormElement>('.delivery-load-form'))
  }

  function completeAvailable(
    request: DeliveryReadRequest,
    includeTrace: boolean,
  ): void {
    const response = parseRequired(
      createDeliveryResponseValue(request.requestId, includeTrace),
    )
    act(() => client.emit(response))
  }

  function requiredSent(index: number): DeliveryReadRequest {
    const request = client.sent[index]
    if (request === undefined) {
      throw new Error(`Expected sent delivery request ${index}.`)
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

class FakeDeliveryClient implements DeliveryBridgeClient {
  readonly sent: DeliveryReadRequest[] = []
  readonly abandoned: string[] = []
  unsubscribeCount = 0
  disposeCount = 0
  private listener: ((response: DeliveryReadResponse) => void) | null = null

  sendDelivery(request: DeliveryReadRequest): void {
    this.sent.push(request)
  }

  abandonDeliveryRequest(requestId: string): void {
    this.abandoned.push(requestId)
  }

  onDeliveryResponse(
    listener: (response: DeliveryReadResponse) => void,
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

  emit(response: DeliveryReadResponse): void {
    this.listener?.(response)
  }
}

function parseRequired(value: unknown): DeliveryReadResponse {
  const parsed = parseDeliveryReadResponse(value)
  if (parsed === null) {
    throw new Error('Expected a valid delivery response fixture.')
  }
  return parsed
}

function setControlValue(element: HTMLInputElement, value: string): void {
  const setter = Object.getOwnPropertyDescriptor(
    HTMLInputElement.prototype,
    'value',
  )?.set
  if (setter === undefined) {
    throw new Error('Expected the native input value setter.')
  }
  act(() => {
    setter.call(element, value)
    element.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

function submit(form: HTMLFormElement): void {
  act(() => {
    form.dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    )
  })
}
