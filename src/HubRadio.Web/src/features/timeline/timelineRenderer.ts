import {
  TIMELINE_CANVAS_HEIGHT,
  TIMELINE_RULER_HEIGHT,
  TIMELINE_TRACK_HEIGHT,
  TIMELINE_TRACK_TOP,
  type TimelineElementLayout,
} from './timelineGeometry'

export interface TimelineCanvasPalette {
  readonly background: string
  readonly ruler: string
  readonly text: string
  readonly mutedText: string
  readonly border: string
  readonly sourceFill: string
  readonly assetFill: string
  readonly editorialFill: string
  readonly selected: string
  readonly playhead: string
  readonly dropTarget: string
}

export interface TimelineRenderState {
  readonly width: number
  readonly pixelsPerSecond: number
  readonly scrollOffset: number
  readonly layout: readonly TimelineElementLayout[]
  readonly selectedIds: ReadonlySet<string>
  readonly draggedElementId: string | null
  readonly dropTargetIndex: number | null
  readonly playheadSeconds: number | null
  readonly palette: TimelineCanvasPalette
}

export function renderTimelineCanvas(
  context: CanvasRenderingContext2D,
  state: TimelineRenderState,
): void {
  const { width, palette } = state
  context.clearRect(0, 0, width, TIMELINE_CANVAS_HEIGHT)
  context.fillStyle = palette.background
  context.fillRect(0, 0, width, TIMELINE_CANVAS_HEIGHT)
  drawRuler(context, state)

  context.fillStyle = palette.ruler
  context.fillRect(0, TIMELINE_TRACK_TOP, width, TIMELINE_TRACK_HEIGHT)

  for (const item of state.layout) {
    if (item.x + item.width < 0 || item.x > width) {
      continue
    }
    const selected = state.selectedIds.has(item.element.id)
    const dragged = state.draggedElementId === item.element.id
    context.globalAlpha = dragged ? 0.5 : 1
    context.fillStyle = selected
      ? palette.selected
      : item.element.origin.kind === 'sourceSpan'
        ? palette.sourceFill
        : item.element.origin.kind === 'asset'
          ? palette.assetFill
          : palette.editorialFill
    context.fillRect(
      item.x + 1,
      TIMELINE_TRACK_TOP + 1,
      Math.max(0, item.width - 2),
      TIMELINE_TRACK_HEIGHT - 2,
    )
    context.strokeStyle = selected ? palette.selected : palette.border
    context.lineWidth = selected ? 3 : 1
    context.strokeRect(
      item.x + 0.5,
      TIMELINE_TRACK_TOP + 0.5,
      Math.max(0, item.width - 1),
      TIMELINE_TRACK_HEIGHT - 1,
    )
    context.globalAlpha = 1

    if (item.width >= 42) {
      context.save()
      context.beginPath()
      context.rect(
        item.x + 5,
        TIMELINE_TRACK_TOP + 4,
        Math.max(0, item.width - 10),
        TIMELINE_TRACK_HEIGHT - 8,
      )
      context.clip()
      context.fillStyle = palette.text
      context.font = '700 12px "Segoe UI", sans-serif'
      context.fillText(
        originLabel(item.element.origin.kind),
        item.x + 9,
        TIMELINE_TRACK_TOP + 24,
      )
      context.fillStyle = palette.mutedText
      context.font = '11px "Cascadia Mono", monospace'
      context.fillText(
        item.element.id.slice(0, 8),
        item.x + 9,
        TIMELINE_TRACK_TOP + 45,
      )
      context.fillText(
        `${item.element.outputRange.duration.value}/${item.element.outputRange.duration.rate}`,
        item.x + 9,
        TIMELINE_TRACK_TOP + 66,
      )
      context.restore()
    }
  }

  if (state.dropTargetIndex !== null) {
    const dropX = getDropMarkerX(state)
    context.strokeStyle = palette.dropTarget
    context.lineWidth = 4
    context.beginPath()
    context.moveTo(dropX, TIMELINE_TRACK_TOP - 7)
    context.lineTo(dropX, TIMELINE_TRACK_TOP + TIMELINE_TRACK_HEIGHT + 7)
    context.stroke()
  }

  if (state.playheadSeconds !== null && Number.isFinite(state.playheadSeconds)) {
    const playheadX =
      state.playheadSeconds * state.pixelsPerSecond - state.scrollOffset
    if (playheadX >= 0 && playheadX <= width) {
      context.strokeStyle = palette.playhead
      context.lineWidth = 2
      context.beginPath()
      context.moveTo(playheadX, 0)
      context.lineTo(playheadX, TIMELINE_CANVAS_HEIGHT)
      context.stroke()
      context.fillStyle = palette.playhead
      context.beginPath()
      context.moveTo(playheadX - 6, 0)
      context.lineTo(playheadX + 6, 0)
      context.lineTo(playheadX, 9)
      context.closePath()
      context.fill()
    }
  }
}

function drawRuler(
  context: CanvasRenderingContext2D,
  state: TimelineRenderState,
): void {
  context.strokeStyle = state.palette.border
  context.fillStyle = state.palette.mutedText
  context.lineWidth = 1
  context.font = '11px "Cascadia Mono", monospace'
  const minimumSpacing = Math.max(
    1,
    context.measureText('00:00.000').width * 1.25,
  )
  const majorSeconds = chooseMajorTickSeconds(
    state.pixelsPerSecond,
    minimumSpacing,
  )
  const tickWidth = majorSeconds * state.pixelsPerSecond
  if (
    !Number.isFinite(tickWidth) ||
    tickWidth < minimumSpacing
  ) {
    return
  }
  const firstTick = Math.max(0, Math.floor(state.scrollOffset / tickWidth))
  const visibleTickCount = Math.ceil(state.width / tickWidth) + 2
  for (let offset = 0; offset < visibleTickCount; offset += 1) {
    const tick = firstTick + offset
    const second = tick * majorSeconds
    const x = second * state.pixelsPerSecond - state.scrollOffset
    if (!Number.isFinite(second) || !Number.isFinite(x)) {
      continue
    }
    context.beginPath()
    context.moveTo(Math.round(x) + 0.5, TIMELINE_RULER_HEIGHT - 12)
    context.lineTo(Math.round(x) + 0.5, TIMELINE_RULER_HEIGHT)
    context.stroke()
    context.fillText(formatRulerSecond(second), x + 4, 19)
  }
}

function chooseMajorTickSeconds(
  pixelsPerSecond: number,
  minimumSpacing: number,
): number {
  const requestedSeconds = minimumSpacing / pixelsPerSecond
  if (!Number.isFinite(requestedSeconds)) {
    return Number.MAX_VALUE
  }
  const magnitude = 10 ** Math.floor(Math.log10(requestedSeconds))
  for (const multiplier of [1, 2, 5, 10]) {
    const seconds = magnitude * multiplier
    if (seconds >= requestedSeconds && Number.isFinite(seconds)) {
      return seconds
    }
  }
  return requestedSeconds
}

function formatRulerSecond(value: number): string {
  if (value < 1) return `${value.toFixed(2)}s`
  if (value < 60) return `${value.toFixed(value % 1 === 0 ? 0 : 1)}s`
  const minutes = Math.floor(value / 60)
  const seconds = Math.floor(value % 60)
  return `${minutes}:${seconds.toString().padStart(2, '0')}`
}

function getDropMarkerX(state: TimelineRenderState): number {
  const visible = state.layout.filter(
    (item) => item.element.id !== state.draggedElementId,
  )
  const item = visible[state.dropTargetIndex ?? 0]
  if (item !== undefined) {
    return item.x
  }
  const final = visible[visible.length - 1]
  return final === undefined ? 0 : final.x + final.width
}

function originLabel(kind: TimelineElementLayout['element']['origin']['kind']): string {
  switch (kind) {
    case 'sourceSpan':
      return 'Source span'
    case 'asset':
      return 'Asset'
    case 'editorialRecord':
      return 'Editorial'
  }
}
