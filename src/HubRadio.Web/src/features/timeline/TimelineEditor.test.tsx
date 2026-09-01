// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { TimelineEditor, type TimelineBridgeClient } from './TimelineEditor'
import {
  parseTimelineApplyResponse,
  parseTimelineReadResponse,
  type TimelineApplyRequest,
  type TimelineApplyResponse,
  type TimelineReadRequest,
  type TimelineReadResponse,
} from './timelineProtocol'
import {
  createTimelineApplyResponseValue,
  createTimelineReadResponseValue,
  timelineAssetElementId,
  timelineProjectId,
  timelineRequestId,
  timelineRightElementId,
  timelineSourceElementId,
} from './timelineTestFixture'

const applyRequestId = '8899aabb-ccdd-4eef-8011-223344556677'

describe('TimelineEditor', () => {
  let container: HTMLDivElement
  let root: Root
  let viewportWidth: number
  let devicePixelRatioDescriptor: PropertyDescriptor | undefined

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT =
      true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    viewportWidth = 640
    devicePixelRatioDescriptor = Object.getOwnPropertyDescriptor(
      window,
      'devicePixelRatio',
    )
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
      () =>
        ({
          x: 0,
          y: 0,
          top: 0,
          left: 0,
          right: viewportWidth,
          bottom: 184,
          width: viewportWidth,
          height: 184,
          toJSON: () => ({}),
        }) as DOMRect,
    )
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    vi.restoreAllMocks()
    if (devicePixelRatioDescriptor === undefined) {
      delete (window as unknown as Record<string, unknown>).devicePixelRatio
    } else {
      Object.defineProperty(
        window,
        'devicePixelRatio',
        devicePixelRatioDescriptor,
      )
    }
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  it('queues typed edits, undoes locally, and installs apply success as baseline', () => {
    const client = new FakeTimelineBridgeClient()
    const ids = [timelineRequestId, applyRequestId]
    renderTimeline(client, () => ids.shift() ?? applyRequestId)
    submitLoad()
    expect(client.sentReads).toHaveLength(1)
    act(() => client.emitRead(readResponse(client.sentReads[0]!.requestId)))

    expect(elementButtons()).toHaveLength(2)
    act(() => elementButtons()[0]!.click())
    act(() => buttonByText('Delete').click())
    expect(elementButtons()).toHaveLength(1)
    expect(container.textContent).toContain('未送信 1')
    expect(requiredElement<HTMLInputElement>('#timeline-project-id').disabled).toBe(true)
    expect(buttonByText('タイムラインを開く').disabled).toBe(true)

    act(() => buttonByText('未送信操作を元に戻す').click())
    expect(elementButtons()).toHaveLength(2)
    act(() => buttonByText('やり直す').click())
    expect(elementButtons()).toHaveLength(1)

    act(() => buttonByText('1 操作を適用').click())
    expect(client.sentApplies).toHaveLength(1)
    expect(client.sentApplies[0]?.params.operations).toEqual([
      { kind: 'delete', elementId: timelineSourceElementId },
    ])
    expect(JSON.stringify(client.sentApplies[0])).not.toMatch(
      /command|ffmpeg|filePath|sourcePath/i,
    )

    act(() =>
      client.emitApply(
        appliedDeleteResponse(client.sentApplies[0]!.requestId),
      ),
    )
    expect(container.textContent).toContain('revision 4')
    expect(container.textContent).toContain('未送信 0')
    expect(buttonByText('未送信操作を元に戻す').disabled).toBe(true)
  })

  it('selects the accessible element fallback and deletes by keyboard', async () => {
    const client = new FakeTimelineBridgeClient()
    renderTimeline(client, () => timelineRequestId)
    submitLoad()
    act(() => client.emitRead(readResponse(client.sentReads[0]!.requestId)))
    const user = userEvent.setup({ document })
    const firstElement = elementButtons()[0]!

    act(() => firstElement.focus())
    await act(async () => user.keyboard('[Enter]'))
    expect(firstElement.getAttribute('aria-pressed')).toBe('true')

    const canvas = requiredElement<HTMLCanvasElement>('.timeline-canvas')
    act(() => canvas.focus())
    await act(async () => user.keyboard('[Delete]'))
    expect(document.activeElement).toBe(canvas)
    expect(elementButtons()).toHaveLength(1)
    expect(container.textContent).toContain('未送信 1')
  })

  it('splits only at the exact synchronized playhead, never from pixel geometry', () => {
    const client = new FakeTimelineBridgeClient()
    const ids = [timelineRequestId, timelineRightElementId]
    renderTimeline(
      client,
      () => ids.shift() ?? applyRequestId,
      {
        projectId: timelineProjectId,
        sourcePresentationTime: { value: '-3799', rate: '48000' },
        outputPresentationTime: { value: '1001', rate: '48000' },
      },
    )
    submitLoad()
    act(() => client.emitRead(readResponse(client.sentReads[0]!.requestId)))

    expect(container.textContent).toContain('再生位置 1001/48000')
    act(() => elementButtons()[0]!.click())
    act(() => buttonByText('再生位置で分割').click())

    expect(elementButtons()).toHaveLength(3)
    expect(container.textContent).toContain(timelineRightElementId.slice(0, 8))
    expect(container.textContent).toContain('未送信 1')
  })

  it('selects on click and queues move only after an in-range drag', () => {
    const client = new FakeTimelineBridgeClient()
    const ids = [timelineRequestId, applyRequestId]
    renderTimeline(client, () => ids.shift() ?? applyRequestId)
    submitLoad()
    act(() => client.emitRead(readResponse(client.sentReads[0]!.requestId)))
    const canvas = requiredElement<HTMLCanvasElement>('.timeline-canvas')

    dispatchPointer(canvas, 'pointerdown', 300, 80)
    dispatchPointer(canvas, 'pointerup', 300, 80)

    expect(container.textContent).toContain('未送信 0')
    expect(elementButtons()[1]!.getAttribute('aria-pressed')).toBe('true')

    dispatchPointer(canvas, 'pointerdown', 300, 80)
    dispatchPointer(canvas, 'pointermove', 10, 80)
    dispatchPointer(canvas, 'pointerup', 10, 80)

    expect(container.textContent).toContain('未送信 1')
    expect(elementButtons()[0]!.textContent).toContain('44444444')
    act(() => buttonByText('1 操作を適用').click())
    expect(client.sentApplies[0]?.params.operations).toEqual([
      {
        kind: 'move',
        elementId: timelineAssetElementId,
        targetIndex: 0,
      },
    ])
  })

  it('rejects moves released beyond any Canvas edge without sending intent', () => {
    const client = new FakeTimelineBridgeClient()
    renderTimeline(client, () => timelineRequestId)
    submitLoad()
    act(() => client.emitRead(readResponse(client.sentReads[0]!.requestId)))
    const canvas = requiredElement<HTMLCanvasElement>('.timeline-canvas')

    for (const [releaseX, releaseY] of [
      [-1, 80],
      [640, 80],
      [10, -1],
      [10, 184],
    ]) {
      dispatchPointer(canvas, 'pointerdown', 300, 80)
      dispatchPointer(canvas, 'pointermove', releaseX, releaseY)
      dispatchPointer(canvas, 'pointerup', releaseX, releaseY)
    }

    expect(container.textContent).toContain('未送信 0')
    expect(elementButtons()[0]!.textContent).toContain('33333333')
    expect(client.sentApplies).toHaveLength(0)
  })

  it('keeps an oversized local history undoable when the closed request cannot be created', () => {
    const client = new FakeTimelineBridgeClient()
    renderTimeline(client, () => timelineRequestId)
    submitLoad()
    act(() => client.emitRead(readResponse(client.sentReads[0]!.requestId)))
    act(() => elementButtons()[0]!.click())
    act(() =>
      elementButtons()[1]!.dispatchEvent(
        new MouseEvent('click', {
          bubbles: true,
          cancelable: true,
          ctrlKey: true,
        }),
      ),
    )
    const keep = buttonByText('Keep')

    act(() => {
      for (let index = 0; index < 800; index += 1) keep.click()
    })

    expect(container.textContent).toContain('未送信 1600')
    act(() => buttonByText('1600 操作を適用').click())
    expect(client.sentApplies).toHaveLength(0)
    expect(container.textContent).toContain(
      'タイムライン要求を送信できませんでした。',
    )
    expect(buttonByText('未送信操作を元に戻す').disabled).toBe(false)
  })

  it('mounts and resizes Canvas backing pixels with the current DPR', () => {
    Object.defineProperty(window, 'devicePixelRatio', {
      configurable: true,
      value: 2,
    })
    const client = new FakeTimelineBridgeClient()
    renderTimeline(client, () => timelineRequestId)
    submitLoad()
    act(() => client.emitRead(readResponse(client.sentReads[0]!.requestId)))
    const canvas = requiredElement<HTMLCanvasElement>('.timeline-canvas')

    expect(canvas.width).toBe(1280)
    expect(canvas.height).toBe(368)
    expect(canvas.style.width).toBe('640px')

    viewportWidth = 320
    Object.defineProperty(window, 'devicePixelRatio', {
      configurable: true,
      value: 1.5,
    })
    act(() => window.dispatchEvent(new Event('resize')))

    expect(canvas.width).toBe(480)
    expect(canvas.height).toBe(276)
    expect(canvas.style.width).toBe('320px')
    expect(canvas.style.height).toBe('184px')
  })

  it('keeps adversarial project input inert and cleans up bridge listeners', () => {
    const client = new FakeTimelineBridgeClient()
    renderTimeline(client, () => timelineRequestId, null, '<script>bad()</script>')
    const input = requiredElement<HTMLInputElement>('#timeline-project-id')
    const submit = buttonByText('タイムラインを開く')

    expect(input.value).toBe('<script>bad()</script>')
    expect(submit.disabled).toBe(true)
    expect(container.querySelector('script')).toBeNull()
    expect(client.listenerCount).toBe(2)

    act(() => root.unmount())
    expect(client.listenerCount).toBe(0)
    expect(client.disposed).toBe(true)
    root = createRoot(container)
  })

  function renderTimeline(
    client: FakeTimelineBridgeClient,
    createRequestId: () => string,
    playerPosition: Parameters<typeof TimelineEditor>[0]['playerPosition'] = null,
    initialProjectId = timelineProjectId,
  ): void {
    act(() => {
      root.render(
        <TimelineEditor
          createClient={() => client}
          createRequestId={createRequestId}
          initialProjectId={initialProjectId}
          playerPosition={playerPosition}
        />,
      )
    })
  }

  function submitLoad(): void {
    const form = requiredElement<HTMLFormElement>('.timeline-load-form')
    act(() =>
      form.dispatchEvent(
        new Event('submit', { bubbles: true, cancelable: true }),
      ),
    )
  }

  function elementButtons(): HTMLButtonElement[] {
    return [...container.querySelectorAll<HTMLButtonElement>('.timeline-element-list button')]
  }

  function buttonByText(text: string): HTMLButtonElement {
    const button = [...container.querySelectorAll<HTMLButtonElement>('button')].find(
      (candidate) => candidate.textContent?.trim() === text,
    )
    if (button === undefined) {
      throw new Error(`Expected a button with text ${text}.`)
    }
    return button
  }

  function requiredElement<T extends Element>(selector: string): T {
    const element = container.querySelector(selector)
    if (element === null) {
      throw new Error(`Expected an element matching ${selector}.`)
    }
    return element as T
  }

  function dispatchPointer(
    target: HTMLCanvasElement,
    type: 'pointerdown' | 'pointermove' | 'pointerup',
    clientX: number,
    clientY: number,
  ): void {
    const event = new MouseEvent(type, {
      bubbles: true,
      cancelable: true,
      button: 0,
      clientX,
      clientY,
    })
    Object.defineProperty(event, 'pointerId', { value: 1 })
    act(() => target.dispatchEvent(event))
  }
})

class FakeTimelineBridgeClient implements TimelineBridgeClient {
  readonly sentReads: TimelineReadRequest[] = []
  readonly sentApplies: TimelineApplyRequest[] = []
  disposed = false
  private readonly readListeners = new Set<
    (response: TimelineReadResponse) => void
  >()
  private readonly applyListeners = new Set<
    (response: TimelineApplyResponse) => void
  >()

  get listenerCount(): number {
    return this.readListeners.size + this.applyListeners.size
  }

  sendTimelineRead(request: TimelineReadRequest): void {
    this.sentReads.push(request)
  }

  sendTimelineApply(request: TimelineApplyRequest): void {
    this.sentApplies.push(request)
  }

  abandonTimelineRequest(_requestId: string): void {}

  onTimelineReadResponse(
    listener: (response: TimelineReadResponse) => void,
  ): () => void {
    this.readListeners.add(listener)
    return () => this.readListeners.delete(listener)
  }

  onTimelineApplyResponse(
    listener: (response: TimelineApplyResponse) => void,
  ): () => void {
    this.applyListeners.add(listener)
    return () => this.applyListeners.delete(listener)
  }

  emitRead(response: TimelineReadResponse): void {
    for (const listener of this.readListeners) listener(response)
  }

  emitApply(response: TimelineApplyResponse): void {
    for (const listener of this.applyListeners) listener(response)
  }

  dispose(): void {
    this.disposed = true
    this.readListeners.clear()
    this.applyListeners.clear()
  }
}

function readResponse(requestId: string): TimelineReadResponse {
  const value = createTimelineReadResponseValue()
  value.requestId = requestId
  const response = parseTimelineReadResponse(value)
  if (response === null) throw new Error('Expected a valid timeline read response.')
  return response
}

function appliedDeleteResponse(requestId: string): TimelineApplyResponse {
  const value = createTimelineApplyResponseValue()
  value.requestId = requestId
  value.result.snapshot.elements = [value.result.snapshot.elements[1]!]
  value.result.snapshot.elements[0]!.outputRange.start = {
    value: '0',
    rate: '1',
  }
  const response = parseTimelineApplyResponse(value)
  if (response === null) throw new Error('Expected a valid timeline apply response.')
  return response
}