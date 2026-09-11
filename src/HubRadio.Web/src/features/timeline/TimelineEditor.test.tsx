// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act, StrictMode, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  TimelineEditor,
  type TimelineBridgeClient,
  type TimelineEditorProps,
} from './TimelineEditor'
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
const otherProjectId = '99999999-9999-4999-8999-999999999999'

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
    vi.unstubAllGlobals()
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
    expect(client.sentReads).toHaveLength(0)
    expect(requiredElement<HTMLElement>('.timeline-card').hidden).toBe(false)
    expect(requiredElement('#timeline-title').textContent).toBe('編集タイムライン')
    expect(requiredElement('.timeline-card').getAttribute('aria-labelledby')).toBe('timeline-title')
    expect(requiredElement<HTMLInputElement>('#timeline-project-id').value).toBe(timelineProjectId)
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

  it('reads an explicit workflow project once without following rerenders or player IDs', async () => {
    const client = new FakeTimelineBridgeClient()
    const createRequestId = vi.fn(() => timelineRequestId)
    const props = {
      createClient: () => client,
      createRequestId,
      initialProjectId: otherProjectId,
    }
    await renderWorkflow({ ...props, context: { projectId: timelineProjectId } })

    expect(client.sentReads).toHaveLength(1)
    expect(client.sentReads[0]?.requestId).toBe(timelineRequestId)
    expect(client.sentReads[0]?.params.projectId).toBe(timelineProjectId)
    expect(container.querySelector('.timeline-load-form')).toBeNull()
    expect(requiredElement('#timeline-title').textContent).toBe('編集タイムライン')

    await renderWorkflow({ ...props, context: { projectId: timelineProjectId } })
    act(() => client.emitRead(readResponse(timelineRequestId)))
    await renderWorkflow({
      ...props,
      context: { projectId: timelineProjectId },
      initialProjectId: '',
      playerPosition: {
        projectId: otherProjectId,
        sourcePresentationTime: { value: '-3799', rate: '48000' },
        outputPresentationTime: { value: '1001', rate: '48000' },
      },
    })

    expect(client.sentReads).toHaveLength(1)
    expect(createRequestId).toHaveBeenCalledTimes(1)
    expect(client.disposed).toBe(false)
    expect(elementButtons()).toHaveLength(2)
    expect(elementButtons()[0]!.textContent).toContain(timelineSourceElementId.slice(0, 8))
    expect(elementButtons()[0]!.textContent).toContain('1001/24000')
    expect(container.textContent).toContain('再生位置 —')
  })

  it.each([false, true])(
    'loads on first activation only and retains the live session and pending edits across visibility changes (StrictMode=%s)',
    async (strictMode) => {
      const clients: FakeTimelineBridgeClient[] = []
      const createClient = vi.fn(() => {
        const client = new FakeTimelineBridgeClient()
        clients.push(client)
        return client
      })
      const createRequestId = vi.fn(() => timelineRequestId)
      const renderPane = async (active: boolean): Promise<void> => {
        await act(async () => {
          const editor = (
            <TimelineEditor
              createClient={createClient}
              createRequestId={createRequestId}
              context={{ projectId: timelineProjectId }}
              active={active}
            />
          )
          root.render(strictMode ? <StrictMode>{editor}</StrictMode> : editor)
        })
      }

      await renderPane(false)
      const setupCount = strictMode ? 2 : 1
      const liveClient = clients[clients.length - 1]!
      expect(createClient).toHaveBeenCalledTimes(setupCount)
      expect(clients.flatMap((client) => client.sentReads)).toHaveLength(0)
      expect(createRequestId).not.toHaveBeenCalled()
      expect(requiredElement('[data-timeline-phase]').getAttribute('data-timeline-phase')).toBe('idle')

      await renderPane(true)
      expect(liveClient.sentReads).toHaveLength(1)
      expect(liveClient.sentReads[0]?.params.projectId).toBe(timelineProjectId)
      await renderPane(false)
      await renderPane(true)
      expect(clients.flatMap((client) => client.sentReads)).toHaveLength(1)
      expect(requiredElement('[data-timeline-phase]').getAttribute('data-timeline-phase')).toBe('loading')
      expect(liveClient.listenerCount).toBe(2)
      expect(liveClient.disposed).toBe(false)

      act(() => liveClient.emitRead(readResponse(timelineRequestId)))
      const canvas = requiredElement<HTMLCanvasElement>('.timeline-canvas')
      act(() => elementButtons()[0]!.click())
      act(() => buttonByText('Delete').click())
      await renderPane(false)
      expect(requiredElement<HTMLElement>('.timeline-card').hidden).toBe(true)
      expect(container.textContent).toContain('未送信 1')
      await renderPane(true)
      expect(requiredElement('.timeline-canvas')).toBe(canvas)
      expect(elementButtons()).toHaveLength(1)
      expect(container.textContent).toContain('未送信 1')
      expect(buttonByText('未送信操作を元に戻す').disabled).toBe(false)

      act(() => buttonByText('未送信操作を元に戻す').click())
      expect(elementButtons()).toHaveLength(2)
      await renderPane(false)
      await renderPane(true)
      expect(buttonByText('やり直す').disabled).toBe(false)
      act(() => buttonByText('やり直す').click())
      expect(elementButtons()).toHaveLength(1)
      expect(container.textContent).toContain('未送信 1')
      expect(clients.flatMap((client) => client.sentReads)).toHaveLength(1)
      expect(createRequestId).toHaveBeenCalledTimes(1)
      expect(createClient).toHaveBeenCalledTimes(setupCount)
      expect(liveClient.listenerCount).toBe(2)
      expect(liveClient.disposed).toBe(false)
      expect(liveClient.sentApplies).toHaveLength(0)
    },
  )

  it.each(['unavailable', 'sendFailed'] as const)(
    'manually retries an initial contextual %s without raw IDs or automatic retries',
    async (failure) => {
      const client = new FakeTimelineBridgeClient()
      const createClient = vi.fn(() => client)
      const ids = [timelineRequestId, applyRequestId]
      const createRequestId = vi.fn(() => ids.shift() ?? applyRequestId)
      const sendRead = vi.spyOn(client, 'sendTimelineRead')
      if (failure === 'sendFailed') {
        sendRead.mockImplementationOnce(() => {
          throw new Error('Timeline read could not be sent.')
        })
      }
      const props = {
        createClient,
        createRequestId,
        initialProjectId: otherProjectId,
        context: { projectId: timelineProjectId },
      }
      await renderWorkflow(props)
      if (failure === 'unavailable') {
        act(() => client.emitRead({
          ...readResponse(timelineRequestId),
          result: { status: 'unavailable', snapshot: null },
        }))
      }

      expect(requiredElement('[data-timeline-phase]').getAttribute('data-timeline-phase')).toBe('error')
      expect(container.querySelector('#timeline-project-id')).toBeNull()
      expect(container.querySelector('.timeline-load-form')).toBeNull()
      expect(buttonByText('タイムラインの読み込みを再試行').disabled).toBe(false)
      await renderWorkflow({ ...props, active: false })
      expect(buttonByText('タイムラインの読み込みを再試行').disabled).toBe(true)
      await renderWorkflow({ ...props, active: true })
      expect(sendRead).toHaveBeenCalledTimes(1)
      expect(createRequestId).toHaveBeenCalledTimes(1)

      act(() => buttonByText('タイムラインの読み込みを再試行').click())
      expect(sendRead).toHaveBeenCalledTimes(2)
      expect(sendRead.mock.calls[1]?.[0].requestId).toBe(applyRequestId)
      expect(sendRead.mock.calls[1]?.[0].params.projectId).toBe(timelineProjectId)
      expect(requiredElement('[data-timeline-phase]').getAttribute('data-timeline-phase')).toBe('loading')
      await renderWorkflow({ ...props, active: false })
      await renderWorkflow({ ...props, active: true })
      expect(sendRead).toHaveBeenCalledTimes(2)
      expect(createRequestId).toHaveBeenCalledTimes(2)
      act(() => client.emitRead(readResponse(applyRequestId)))

      expect(requiredElement('[data-timeline-phase]').getAttribute('data-timeline-phase')).toBe('ready')
      expect(elementButtons()).toHaveLength(2)
      expect(container.querySelector('[role="alert"]')).toBeNull()
      expect(createClient).toHaveBeenCalledTimes(1)
      expect(client.listenerCount).toBe(2)
      expect(client.disposed).toBe(false)
    },
  )

  it.each([null, '', 'not-a-project-id'])(
    'never substitutes initialProjectId for an explicit %s context',
    async (projectId) => {
      const client = new FakeTimelineBridgeClient()
      const createClient = vi.fn(() => client)
      await renderWorkflow({
        createClient,
        createRequestId: () => timelineRequestId,
        initialProjectId: timelineProjectId,
        context: { projectId },
      })

      expect(client.sentReads).toHaveLength(0)
      expect(createClient).not.toHaveBeenCalled()
      expect(container.querySelector('#timeline-project-id')).toBeNull()
      expect(container.querySelector('.timeline-canvas')).toBeNull()
      expect(container.textContent).toContain('タイムラインは未読み込みです')
      expect(requiredElement('[data-timeline-phase]').getAttribute('data-timeline-phase')).toBe('idle')
    },
  )

  it('clears the old project and dirty history and ignores late responses after switching or clearing context', async () => {
    const firstClient = new FakeTimelineBridgeClient()
    const secondClient = new FakeTimelineBridgeClient()
    const clients = [firstClient, secondClient]
    const ids = [timelineRequestId, applyRequestId, timelineRightElementId]
    const createClient = vi.fn(() => clients.shift() ?? secondClient)
    const onDirtyChange = vi.fn()
    const props = {
      createClient,
      createRequestId: () => ids.shift() ?? timelineRightElementId,
      initialProjectId: timelineProjectId,
      onDirtyChange,
    }
    await renderWorkflow({ ...props, context: { projectId: timelineProjectId } })
    act(() => firstClient.emitRead(readResponse(timelineRequestId)))
    act(() => elementButtons()[0]!.click())
    act(() => buttonByText('Delete').click())
    act(() => buttonByText('1 操作を適用').click())
    expect(onDirtyChange).toHaveBeenLastCalledWith(true)

    await renderWorkflow({ ...props, context: { projectId: otherProjectId } })
    expect(firstClient.disposed).toBe(true)
    expect(firstClient.listenerCount).toBe(0)
    expect(secondClient.sentReads).toHaveLength(1)
    expect(secondClient.sentReads[0]?.params.projectId).toBe(otherProjectId)
    expect(elementButtons()).toHaveLength(0)
    expect(onDirtyChange).toHaveBeenLastCalledWith(false)
    act(() => firstClient.emitApply(appliedDeleteResponse(applyRequestId)))
    expect(container.querySelector('.timeline-canvas')).toBeNull()

    await renderWorkflow({ ...props, context: { projectId: null } })
    expect(secondClient.disposed).toBe(true)
    expect(secondClient.listenerCount).toBe(0)
    act(() => secondClient.emitRead(readResponse(timelineRightElementId, otherProjectId)))
    expect(createClient).toHaveBeenCalledTimes(2)
    expect(container.querySelector('.timeline-canvas')).toBeNull()
    expect(container.querySelector('.timeline-load-form')).toBeNull()
    expect(container.textContent).not.toContain('revision')
    expect(requiredElement('[data-timeline-phase]').getAttribute('data-timeline-phase')).toBe('idle')
    expect(onDirtyChange).toHaveBeenLastCalledWith(false)
  })

  it('accepts read and apply responses while inactive without stealing visible focus', async () => {
    const client = new FakeTimelineBridgeClient()
    const ids = [timelineRequestId, applyRequestId]
    const createClient = () => client
    const createRequestId = () => ids.shift() ?? applyRequestId
    const renderPane = async (active: boolean): Promise<void> => {
      await act(async () => {
        root.render(
          <>
            <button id="timeline-focus-target" type="button">現在の作業</button>
            <TimelineEditor
              createClient={createClient}
              createRequestId={createRequestId}
              context={{ projectId: timelineProjectId }}
              active={active}
            />
          </>,
        )
      })
    }
    viewportWidth = 0
    await renderPane(true)
    expect(client.sentReads).toHaveLength(1)
    await renderPane(false)
    const visibleButton = requiredElement<HTMLButtonElement>('#timeline-focus-target')
    act(() => visibleButton.focus())
    act(() => client.emitRead(readResponse(timelineRequestId)))
    const canvas = requiredElement<HTMLCanvasElement>('.timeline-canvas')

    expect(document.activeElement).toBe(visibleButton)
    expect(requiredElement<HTMLElement>('.timeline-card').hidden).toBe(true)
    expect(requiredElement('.timeline-card').hasAttribute('inert')).toBe(true)
    expect(canvas.tabIndex).toBe(-1)
    expect(requiredElement('output').textContent).toBe('1 px/s')

    viewportWidth = 640
    await renderPane(true)
    expect(requiredElement('output').textContent).not.toBe('1 px/s')
    expect(canvas.width).toBe(640 * window.devicePixelRatio)
    expect(canvas.tabIndex).toBe(0)
    expect(document.activeElement).toBe(visibleButton)
    act(() => elementButtons()[0]!.click())
    act(() => buttonByText('Delete').click())
    act(() => buttonByText('1 操作を適用').click())
    await renderPane(false)
    act(() => visibleButton.focus())
    act(() => client.emitApply(appliedDeleteResponse(applyRequestId)))

    expect(document.activeElement).toBe(visibleButton)
    expect(container.textContent).toContain('revision 4')
    expect(requiredElement<HTMLElement>('.timeline-card').hidden).toBe(true)
    await renderPane(true)
    expect(requiredElement('.timeline-canvas')).toBe(canvas)
    expect(document.activeElement).toBe(visibleButton)
    expect(client.sentReads).toHaveLength(1)
    expect(client.sentApplies[0]?.params.operations).toEqual([
      { kind: 'delete', elementId: timelineSourceElementId },
    ])
  })

  it('retains the mounted canvas, zoom, pan, selection and undo across hidden zero-width resize notifications', async () => {
    const resizeCallbacks = new Set<() => void>()
    vi.stubGlobal('ResizeObserver', class {
      private readonly callback: () => void

      constructor(callback: ResizeObserverCallback) {
        this.callback = () => callback([], this)
      }

      observe(): void { resizeCallbacks.add(this.callback) }
      unobserve(): void { resizeCallbacks.delete(this.callback) }
      disconnect(): void { resizeCallbacks.delete(this.callback) }
    })
    const client = new FakeTimelineBridgeClient()
    const props = {
      createClient: () => client,
      createRequestId: () => timelineRequestId,
      context: { projectId: timelineProjectId },
    }
    await renderWorkflow(props)
    act(() => client.emitRead(readResponse(timelineRequestId)))
    const canvas = requiredElement<HTMLCanvasElement>('.timeline-canvas')
    act(() => elementButtons()[0]!.click())
    act(() => buttonByText('Keep').click())
    act(() => buttonByText('＋').click())
    act(() => buttonByText('＋').click())
    act(() => canvas.dispatchEvent(new WheelEvent('wheel', {
      bubbles: true,
      cancelable: true,
      deltaX: 30,
    })))
    const zoom = requiredElement('output').textContent
    const readout = requiredElement('.timeline-readout').textContent
    const backingWidth = canvas.width
    const queuedResizeCallbacks = [...resizeCallbacks]
    expect(queuedResizeCallbacks).toHaveLength(1)
    expect(readout).not.toContain('scroll 0 px')

    viewportWidth = 0
    act(() => queuedResizeCallbacks.forEach((resize) => resize()))
    expect(canvas.width).toBe(backingWidth)
    await renderWorkflow({ ...props, active: false })
    act(() => {
      queuedResizeCallbacks.forEach((resize) => resize())
      canvas.dispatchEvent(new KeyboardEvent('keydown', {
        bubbles: true,
        key: 'z',
        ctrlKey: true,
      }))
      canvas.dispatchEvent(new WheelEvent('wheel', {
        bubbles: true,
        cancelable: true,
        ctrlKey: true,
        deltaY: -1,
      }))
    })
    expect(requiredElement('.timeline-canvas')).toBe(canvas)
    expect(canvas.width).toBe(backingWidth)
    expect(requiredElement('output').textContent).toBe(zoom)
    expect(requiredElement('.timeline-readout').textContent).toBe(readout)
    expect(elementButtons()[0]!.getAttribute('aria-pressed')).toBe('true')

    viewportWidth = 480
    await renderWorkflow({ ...props, active: true })
    expect(requiredElement('.timeline-canvas')).toBe(canvas)
    expect(canvas.style.width).toBe('480px')
    expect(requiredElement('output').textContent).toBe(zoom)
    expect(requiredElement('.timeline-readout').textContent).toBe(readout)
    expect(elementButtons()[0]!.getAttribute('aria-pressed')).toBe('true')
    expect(buttonByText('未送信操作を元に戻す').disabled).toBe(false)
    act(() => buttonByText('未送信操作を元に戻す').click())
    expect(container.textContent).toContain('未送信 0')
    act(() => buttonByText('やり直す').click())
    expect(container.textContent).toContain('未送信 1')
    expect(client.sentReads).toHaveLength(1)
    expect(client.disposed).toBe(false)
  })

  it('reports dirty directly from pending edits through undo, redo, visibility and apply acknowledgement', async () => {
    const client = new FakeTimelineBridgeClient()
    const ids = [timelineRequestId, applyRequestId]
    const onDirtyChange = vi.fn()
    const props = {
      createClient: () => client,
      createRequestId: () => ids.shift() ?? applyRequestId,
      context: { projectId: timelineProjectId },
      onDirtyChange,
    }
    await renderWorkflow(props)
    act(() => client.emitRead(readResponse(timelineRequestId)))
    act(() => elementButtons()[0]!.click())
    act(() => buttonByText('＋').click())
    expect(onDirtyChange.mock.calls).toEqual([[false]])

    act(() => buttonByText('Delete').click())
    act(() => buttonByText('未送信操作を元に戻す').click())
    act(() => buttonByText('やり直す').click())
    await renderWorkflow({ ...props, active: false })
    await renderWorkflow({ ...props, active: true })
    act(() => buttonByText('1 操作を適用').click())
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false], [true]])
    act(() => client.emitApply(appliedDeleteResponse(applyRequestId)))
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false], [true], [false]])
  })

  it.each([false, true])(
    'preserves pending responses and avoids inline aggregate-state notification loops (StrictMode=%s)',
    async (strictMode) => {
      const clients: FakeTimelineBridgeClient[] = []
      const createClient = vi.fn(() => {
        const client = new FakeTimelineBridgeClient()
        clients.push(client)
        return client
      })
      const ids = [timelineRequestId, applyRequestId]
      const onDirtyChange = vi.fn()

      function Parent() {
        const [dirty, setDirty] = useState({ timeline: false, settings: false })
        return (
          <>
            <button
              type="button"
              onClick={() => setDirty({ ...dirty, settings: !dirty.settings })}
            >
              親の状態を更新
            </button>
            <output id="timeline-parent-dirty">{JSON.stringify(dirty)}</output>
            <TimelineEditor
              createClient={() => createClient()}
              createRequestId={() => ids.shift() ?? applyRequestId}
              context={{ projectId: timelineProjectId }}
              onDirtyChange={(timeline) => {
                onDirtyChange(timeline)
                // Fail promptly instead of hanging if callback identity causes a loop.
                if (onDirtyChange.mock.calls.length > 6) {
                  throw new Error('Dirty notifications must not follow callback identity changes.')
                }
                setDirty({ ...dirty, timeline })
              }}
            />
          </>
        )
      }

      await act(async () => {
        root.render(strictMode ? <StrictMode><Parent /></StrictMode> : <Parent />)
      })

      const setupCount = strictMode ? 2 : 1
      const initialNotifications = strictMode ? [[false], [false]] : [[false]]
      expect(createClient).toHaveBeenCalledTimes(setupCount)
      expect(clients.flatMap((client) => client.sentReads)).toHaveLength(1)
      expect(onDirtyChange.mock.calls).toEqual(initialNotifications)
      for (const discardedClient of clients.slice(0, -1)) {
        expect(discardedClient.disposed).toBe(true)
        expect(discardedClient.listenerCount).toBe(0)
        expect(discardedClient.sentReads).toHaveLength(0)
      }
      const liveClient = clients[clients.length - 1]!
      expect(liveClient.sentReads[0]?.requestId).toBe(timelineRequestId)
      expect(requiredElement('[data-timeline-phase]').getAttribute('data-timeline-phase')).toBe('loading')

      // Replace both inline props while the automatic read is still pending.
      act(() => buttonByText('親の状態を更新').click())
      expect(createClient).toHaveBeenCalledTimes(setupCount)
      expect(liveClient.disposed).toBe(false)
      expect(liveClient.listenerCount).toBe(2)
      expect(onDirtyChange.mock.calls).toEqual(initialNotifications)
      act(() => liveClient.emitRead(readResponse(timelineRequestId)))
      expect(elementButtons()).toHaveLength(2)
      expect(requiredElement('[data-timeline-phase]').getAttribute('data-timeline-phase')).toBe('ready')

      act(() => elementButtons()[0]!.click())
      act(() => buttonByText('Delete').click())
      expect(requiredElement('#timeline-parent-dirty').textContent).toBe(
        JSON.stringify({ timeline: true, settings: true }),
      )
      expect(onDirtyChange.mock.calls).toEqual([...initialNotifications, [true]])
      act(() => buttonByText('1 操作を適用').click())
      expect(liveClient.sentApplies).toHaveLength(1)
      expect(liveClient.sentApplies[0]?.requestId).toBe(applyRequestId)

      // The pending apply must also survive a new factory and callback reference.
      act(() => buttonByText('親の状態を更新').click())
      expect(createClient).toHaveBeenCalledTimes(setupCount)
      expect(liveClient.disposed).toBe(false)
      expect(liveClient.listenerCount).toBe(2)
      expect(onDirtyChange.mock.calls).toEqual([...initialNotifications, [true]])
      expect(requiredElement('[data-timeline-phase]').getAttribute('data-timeline-phase')).toBe('applying')
      act(() => liveClient.emitApply(appliedDeleteResponse(applyRequestId)))

      expect(requiredElement('[data-timeline-phase]').getAttribute('data-timeline-phase')).toBe('ready')
      expect(container.textContent).toContain('revision 4')
      expect(container.textContent).toContain('未送信 0')
      expect(elementButtons()).toHaveLength(1)
      expect(buttonByText('0 操作を適用').disabled).toBe(true)
      expect(requiredElement('#timeline-parent-dirty').textContent).toBe(
        JSON.stringify({ timeline: false, settings: false }),
      )
      expect(onDirtyChange.mock.calls).toEqual([...initialNotifications, [true], [false]])
      expect(createClient).toHaveBeenCalledTimes(setupCount)
      expect(liveClient.disposed).toBe(false)
      expect(clients.flatMap((client) => client.sentReads)).toHaveLength(1)
      expect(clients.flatMap((client) => client.sentApplies)).toHaveLength(1)
    },
  )

  it('sends only one initial workflow read through the live StrictMode client', async () => {
    const clients: FakeTimelineBridgeClient[] = []
    const createClient = () => {
      const client = new FakeTimelineBridgeClient()
      clients.push(client)
      return client
    }
    const createRequestId = vi.fn(() => timelineRequestId)
    await act(async () => {
      root.render(
        <StrictMode>
          <TimelineEditor
            createClient={createClient}
            createRequestId={createRequestId}
            context={{ projectId: timelineProjectId }}
          />
        </StrictMode>,
      )
    })

    expect(clients.flatMap((client) => client.sentReads)).toHaveLength(1)
    expect(createRequestId).toHaveBeenCalledTimes(1)
    const liveClient = clients[clients.length - 1]!
    expect(liveClient.disposed).toBe(false)
    expect(liveClient.sentReads).toHaveLength(1)
    act(() => liveClient.emitRead(readResponse(timelineRequestId)))
    expect(elementButtons()).toHaveLength(2)
  })

  async function renderWorkflow(props: TimelineEditorProps): Promise<void> {
    await act(async () => {
      root.render(<TimelineEditor {...props} />)
    })
  }

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

function readResponse(requestId: string, projectId = timelineProjectId): TimelineReadResponse {
  const value = createTimelineReadResponseValue()
  value.requestId = requestId
  value.result.snapshot.projectId = projectId
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