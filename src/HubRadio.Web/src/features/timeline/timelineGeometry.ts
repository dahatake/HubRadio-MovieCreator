import { exactTimeToDerivedSeconds } from './rationalTime'
import type { TimelineElement } from './timelineProtocol'

export const TIMELINE_CANVAS_HEIGHT = 184
export const TIMELINE_RULER_HEIGHT = 44
export const TIMELINE_TRACK_TOP = 58
export const TIMELINE_TRACK_HEIGHT = 86

export interface TimelineElementLayout {
  readonly element: TimelineElement
  readonly index: number
  readonly x: number
  readonly width: number
}

export interface TimelineElementHit {
  readonly elementId: string
  readonly index: number
}

export function createTimelineLayout(
  elements: readonly TimelineElement[],
  pixelsPerSecond: number,
  scrollOffset: number,
): readonly TimelineElementLayout[] {
  validateViewportNumbers(pixelsPerSecond, scrollOffset)
  return Object.freeze(
    elements.map((element, index) =>
      Object.freeze({
        element,
        index,
        x:
          exactTimeToDerivedSeconds(element.outputRange.start) *
            pixelsPerSecond -
          scrollOffset,
        width:
          exactTimeToDerivedSeconds(element.outputRange.duration) *
          pixelsPerSecond,
      }),
    ),
  )
}

export function hitTestTimelineElement(
  x: number,
  y: number,
  layout: readonly TimelineElementLayout[],
): TimelineElementHit | null {
  if (
    !Number.isFinite(x) ||
    !Number.isFinite(y) ||
    y < TIMELINE_TRACK_TOP ||
    y >= TIMELINE_TRACK_TOP + TIMELINE_TRACK_HEIGHT
  ) {
    return null
  }

  for (let index = layout.length - 1; index >= 0; index -= 1) {
    const candidate = layout[index]!
    if (x >= candidate.x && x < candidate.x + candidate.width) {
      return Object.freeze({
        elementId: candidate.element.id,
        index: candidate.index,
      })
    }
  }
  return null
}

export function computeMoveTargetIndex(
  elements: readonly TimelineElement[],
  draggedElementId: string,
  pointerX: number,
  viewportWidth: number,
  pixelsPerSecond: number,
  scrollOffset: number,
): number | null {
  if (
    !Number.isFinite(pointerX) ||
    !Number.isFinite(viewportWidth) ||
    viewportWidth <= 0 ||
    pointerX < 0 ||
    pointerX >= viewportWidth
  ) {
    return null
  }
  validateViewportNumbers(pixelsPerSecond, scrollOffset)
  if (!elements.some((element) => element.id === draggedElementId)) {
    return null
  }

  const worldX = pointerX + scrollOffset
  let targetIndex = 0
  for (const element of elements) {
    if (element.id === draggedElementId) {
      continue
    }
    const midpoint =
      (exactTimeToDerivedSeconds(element.outputRange.start) +
        exactTimeToDerivedSeconds(element.outputRange.duration) / 2) *
      pixelsPerSecond
    if (worldX >= midpoint) {
      targetIndex += 1
    }
  }
  return targetIndex
}

export function totalTimelineWidth(
  elements: readonly TimelineElement[],
  pixelsPerSecond: number,
): number {
  if (!Number.isFinite(pixelsPerSecond) || pixelsPerSecond <= 0) {
    throw new RangeError('Timeline zoom must be finite and positive.')
  }
  const last = elements[elements.length - 1]
  if (last === undefined) {
    return 0
  }
  const width =
    (exactTimeToDerivedSeconds(last.outputRange.start) +
      exactTimeToDerivedSeconds(last.outputRange.duration)) *
    pixelsPerSecond
  if (!Number.isFinite(width) || !(width > 0)) {
    throw new RangeError('The timeline width is not representable for this zoom.')
  }
  return width
}

export function clampTimelineScroll(
  requestedOffset: number,
  contentWidth: number,
  viewportWidth: number,
): number {
  if (
    !Number.isFinite(requestedOffset) ||
    !Number.isFinite(contentWidth) ||
    !Number.isFinite(viewportWidth)
  ) {
    return 0
  }
  return Math.min(
    Math.max(0, requestedOffset),
    Math.max(0, contentWidth - Math.max(0, viewportWidth)),
  )
}

export function fitTimelineZoom(
  elements: readonly TimelineElement[],
  viewportWidth: number,
): number {
  const unscaledWidth = totalTimelineWidth(elements, 1)
  if (
    !(unscaledWidth > 0) ||
    !Number.isFinite(viewportWidth) ||
    !(viewportWidth > 0)
  ) {
    return 1
  }
  const zoom = viewportWidth / unscaledWidth
  if (!Number.isFinite(zoom) || !(zoom > 0)) {
    throw new RangeError('The fitted timeline zoom is not representable.')
  }
  return zoom
}

export function zoomTimelineAroundPointer(
  currentZoom: number,
  requestedZoom: number,
  currentScroll: number,
  pointerX: number,
  viewportWidth: number,
  elements: readonly TimelineElement[],
): Readonly<{ zoom: number; scroll: number }> {
  validateViewportNumbers(currentZoom, currentScroll)
  if (!Number.isFinite(viewportWidth) || viewportWidth < 0) {
    throw new RangeError('Timeline viewport width must be finite and nonnegative.')
  }
  const zoom = isRepresentableZoom(requestedZoom, elements)
    ? requestedZoom
    : currentZoom
  const safePointer = Number.isFinite(pointerX)
    ? Math.min(Math.max(0, pointerX), Math.max(0, viewportWidth))
    : 0
  const worldSeconds = (safePointer + currentScroll) / currentZoom
  const requestedScroll = worldSeconds * zoom - safePointer
  return Object.freeze({
    zoom,
    scroll: clampTimelineScroll(
      requestedScroll,
      totalTimelineWidth(elements, zoom),
      viewportWidth,
    ),
  })
}

function isRepresentableZoom(
  zoom: number,
  elements: readonly TimelineElement[],
): boolean {
  if (!Number.isFinite(zoom) || !(zoom > 0)) {
    return false
  }
  if (!Number.isFinite(1 / zoom)) {
    return false
  }
  try {
    return Number.isFinite(totalTimelineWidth(elements, zoom))
  } catch {
    return false
  }
}

export function syncCanvasBackingStore(
  canvas: HTMLCanvasElement,
  cssWidth: number,
  cssHeight: number,
  devicePixelRatio: number,
): Readonly<{ width: number; height: number; scale: number }> {
  const width = Math.max(0, Math.round(cssWidth))
  const height = Math.max(0, Math.round(cssHeight))
  const scale =
    Number.isFinite(devicePixelRatio) && devicePixelRatio > 0
      ? devicePixelRatio
      : 1
  const backingWidth = Math.max(1, Math.round(width * scale))
  const backingHeight = Math.max(1, Math.round(height * scale))
  if (canvas.width !== backingWidth) canvas.width = backingWidth
  if (canvas.height !== backingHeight) canvas.height = backingHeight
  canvas.style.width = `${width}px`
  canvas.style.height = `${height}px`
  return Object.freeze({ width, height, scale })
}

function validateViewportNumbers(
  pixelsPerSecond: number,
  scrollOffset: number,
): void {
  if (!Number.isFinite(pixelsPerSecond) || pixelsPerSecond <= 0) {
    throw new RangeError('Timeline zoom must be finite and positive.')
  }
  if (!Number.isFinite(scrollOffset) || scrollOffset < 0) {
    throw new RangeError('Timeline scroll must be finite and nonnegative.')
  }
}