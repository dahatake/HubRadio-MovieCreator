import { describe, expect, it } from 'vitest'

import { parseTimelineReadResponse } from './timelineProtocol'
import { createTimelineReadResponseValue } from './timelineTestFixture'
import {
  TIMELINE_TRACK_HEIGHT,
  TIMELINE_TRACK_TOP,
  clampTimelineScroll,
  computeMoveTargetIndex,
  createTimelineLayout,
  fitTimelineZoom,
  hitTestTimelineElement,
  syncCanvasBackingStore,
  zoomTimelineAroundPointer,
} from './timelineGeometry'

describe('timeline Canvas geometry', () => {
  it('hit-tests stable element IDs with half-open clip boundaries', () => {
    const elements = snapshotElements()
    const layout = createTimelineLayout(elements, 24_000, 0)

    expect(
      hitTestTimelineElement(500, TIMELINE_TRACK_TOP + 4, layout),
    ).toEqual({ elementId: elements[0]!.id, index: 0 })
    expect(
      hitTestTimelineElement(1001, TIMELINE_TRACK_TOP + 4, layout),
    ).toEqual({ elementId: elements[1]!.id, index: 1 })
    expect(
      hitTestTimelineElement(
        500,
        TIMELINE_TRACK_TOP + TIMELINE_TRACK_HEIGHT,
        layout,
      ),
    ).toBeNull()
  })

  it('rejects out-of-range drags and returns only bounded integer move slots', () => {
    const elements = snapshotElements()
    expect(
      computeMoveTargetIndex(elements, elements[0]!.id, -1, 1400, 2400, 0),
    ).toBeNull()
    expect(
      computeMoveTargetIndex(elements, elements[0]!.id, 1400, 1400, 2400, 0),
    ).toBeNull()
    expect(
      computeMoveTargetIndex(elements, elements[0]!.id, 1300, 1400, 2400, 0),
    ).toBe(1)
    expect(
      computeMoveTargetIndex(elements, 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 10, 1400, 2400, 0),
    ).toBeNull()
  })

  it('keeps zoom and scroll finite and anchored to display geometry', () => {
    const elements = snapshotElements()
    const viewportWidth = 400
    const pointerX = 200
    const fit = fitTimelineZoom(elements, viewportWidth)
    const changed = zoomTimelineAroundPointer(
      fit,
      fit * 2,
      0,
      pointerX,
      viewportWidth,
      elements,
    )

    expect(fit).toBeGreaterThan(20)
    expect(changed.zoom).toBeGreaterThan(fit)
    expect(changed.scroll).toBeGreaterThanOrEqual(0)
    expect(clampTimelineScroll(10_000, 1200, 1000)).toBe(200)
  })

  it('accepts representable zoom without arbitrary product limits', () => {
    const elements = snapshotElements()
    const tiny = zoomTimelineAroundPointer(
      1,
      1e-308,
      0,
      0,
      400,
      elements,
    )
    const huge = zoomTimelineAroundPointer(
      1,
      1e300,
      0,
      0,
      400,
      elements,
    )
    const nonrepresentable = zoomTimelineAroundPointer(
      1,
      Number.POSITIVE_INFINITY,
      0,
      0,
      400,
      elements,
    )

    expect(tiny.zoom).toBe(1e-308)
    expect(huge.zoom).toBe(1e300)
    expect(nonrepresentable.zoom).toBe(1)
  })

  it('resizes Canvas backing pixels for DPR without changing CSS geometry', () => {
    const canvas = {
      width: 0,
      height: 0,
      style: { width: '', height: '' },
    } as unknown as HTMLCanvasElement

    expect(syncCanvasBackingStore(canvas, 640.4, 184, 2)).toEqual({
      width: 640,
      height: 184,
      scale: 2,
    })
    expect(canvas.width).toBe(1280)
    expect(canvas.height).toBe(368)
    expect(canvas.style.width).toBe('640px')
    expect(canvas.style.height).toBe('184px')
  })
})

function snapshotElements() {
  const response = parseTimelineReadResponse(createTimelineReadResponseValue())
  if (response?.result.status !== 'available') {
    throw new Error('Expected an available fixture snapshot.')
  }
  return response.result.snapshot.elements
}