import { describe, expect, it } from 'vitest'

import { renderTimelineCanvas, type TimelineCanvasPalette } from './timelineRenderer'

const palette: TimelineCanvasPalette = {
  background: '#fff',
  ruler: '#eee',
  text: '#111',
  mutedText: '#555',
  border: '#777',
  sourceFill: '#acf',
  assetFill: '#caf',
  editorialFill: '#fca',
  selected: '#c14',
  playhead: '#f00',
  dropTarget: '#08f',
}

describe('timeline Canvas renderer', () => {
  it.each([Number.MIN_VALUE, 1e-308, 1e308])(
    'keeps ruler coordinates finite at representable zoom %s',
    (pixelsPerSecond) => {
      const coordinates: number[] = []
      let paths = 0
      const context = {
        clearRect: (...values: number[]) => coordinates.push(...values),
        fillRect: (...values: number[]) => coordinates.push(...values),
        measureText: () => ({ width: 64 }) as TextMetrics,
        beginPath: () => {
          paths += 1
        },
        moveTo: (...values: number[]) => coordinates.push(...values),
        lineTo: (...values: number[]) => coordinates.push(...values),
        stroke: () => {},
        fillText: (_text: string, ...values: number[]) =>
          coordinates.push(...values),
        strokeStyle: '',
        fillStyle: '',
        lineWidth: 1,
        font: '',
      } as unknown as CanvasRenderingContext2D

      expect(() =>
        renderTimelineCanvas(context, {
          width: 400,
          pixelsPerSecond,
          scrollOffset: 0,
          layout: [],
          selectedIds: new Set<string>(),
          draggedElementId: null,
          dropTargetIndex: null,
          playheadSeconds: null,
          palette,
        }),
      ).not.toThrow()
      expect(coordinates.every(Number.isFinite)).toBe(true)
      expect(paths).toBeLessThan(500)
    },
  )
})
