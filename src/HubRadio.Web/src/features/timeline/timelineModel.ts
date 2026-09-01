import {
  addExactRationalTime,
  compareExactRationalTime,
  exactRationalTimesEqual,
  exactTimeIsInHalfOpenRange,
  exactZero,
  subtractExactRationalTime,
  type ExactRationalTime,
} from './rationalTime'
import {
  isCanonicalTimelineId,
  type TimelineElement,
  type TimelineOperation,
  type TimelineTimeRange,
} from './timelineProtocol'

export type TrimEdge = 'start' | 'end'

export interface TimelinePlayerPosition {
  readonly projectId: string
  readonly sourcePresentationTime: ExactRationalTime
  readonly outputPresentationTime: ExactRationalTime | null
}

export function replayTimelineOperations(
  baseline: readonly TimelineElement[],
  operations: readonly TimelineOperation[],
): readonly TimelineElement[] {
  let current = Object.freeze([...baseline])
  validateCanonicalTimeline(current)
  const reservedElementIds = new Set(current.map((element) => element.id))
  for (const operation of operations) {
    if (
      operation.kind === 'split' &&
      reservedElementIds.has(operation.rightElementId)
    ) {
      throw new RangeError(
        'A split right element ID must be new within the edit batch.',
      )
    }
    current = applyTimelineOperation(current, operation)
    if (operation.kind === 'split') {
      reservedElementIds.add(operation.rightElementId)
    }
  }
  return current
}

export function applyTimelineOperation(
  elements: readonly TimelineElement[],
  operation: TimelineOperation,
): readonly TimelineElement[] {
  validateCanonicalTimeline(elements)
  const targetIndex = findUniqueElementIndex(elements, operation.elementId)
  const edited = [...elements]

  switch (operation.kind) {
    case 'keep':
      break
    case 'delete':
      edited.splice(targetIndex, 1)
      break
    case 'split':
      splitElement(edited, targetIndex, operation)
      break
    case 'move': {
      const remainingCount = edited.length - 1
      if (operation.targetIndex < 0 || operation.targetIndex > remainingCount) {
        throw new RangeError('A move target index is outside the timeline.')
      }
      const [target] = edited.splice(targetIndex, 1)
      if (target === undefined) {
        throw new Error('The move target disappeared from the timeline.')
      }
      edited.splice(operation.targetIndex, 0, target)
      break
    }
  }

  return reflowTimeline(edited)
}

export function createSplitOperationAtOutputTime(
  elements: readonly TimelineElement[],
  elementId: string,
  outputTime: ExactRationalTime,
  rightElementId: string,
): TimelineOperation | null {
  if (!isCanonicalTimelineId(rightElementId)) {
    return null
  }
  const element = elements.find((candidate) => candidate.id === elementId)
  if (
    element === undefined ||
    elements.some((candidate) => candidate.id === rightElementId) ||
    !exactTimeIsInHalfOpenRange(
      outputTime,
      element.outputRange.start,
      element.outputRange.duration,
    ) ||
    exactRationalTimesEqual(outputTime, element.outputRange.start)
  ) {
    return null
  }

  return Object.freeze({
    kind: 'split',
    elementId,
    splitOffset: subtractExactRationalTime(
      outputTime,
      element.outputRange.start,
    ),
    rightElementId,
  })
}

export function createTrimOperations(
  elements: readonly TimelineElement[],
  outputTime: ExactRationalTime,
  edge: TrimEdge,
  createRightElementId: () => string,
): readonly TimelineOperation[] | null {
  if (elements.length === 0 || compareExactRationalTime(outputTime, exactZero) <= 0) {
    return null
  }
  const timelineEnd = rangeEnd(elements[elements.length - 1]!.outputRange)
  if (compareExactRationalTime(outputTime, timelineEnd) >= 0) {
    return null
  }

  const containingIndex = elements.findIndex((element) =>
    exactTimeIsInHalfOpenRange(
      outputTime,
      element.outputRange.start,
      element.outputRange.duration,
    ),
  )
  if (containingIndex < 0) {
    return null
  }

  const target = elements[containingIndex]!
  const onBoundary = exactRationalTimesEqual(
    outputTime,
    target.outputRange.start,
  )
  const operations: TimelineOperation[] = []

  if (edge === 'start') {
    for (let index = 0; index < containingIndex; index += 1) {
      operations.push(Object.freeze({
        kind: 'delete',
        elementId: elements[index]!.id,
      }))
    }
    if (!onBoundary) {
      const rightElementId = createRightElementId()
      const split = createSplitOperationAtOutputTime(
        elements,
        target.id,
        outputTime,
        rightElementId,
      )
      if (split === null) {
        return null
      }
      operations.push(split)
      operations.push(Object.freeze({ kind: 'delete', elementId: target.id }))
    }
  } else {
    if (!onBoundary) {
      const rightElementId = createRightElementId()
      const split = createSplitOperationAtOutputTime(
        elements,
        target.id,
        outputTime,
        rightElementId,
      )
      if (split === null) {
        return null
      }
      operations.push(split)
      operations.push(Object.freeze({ kind: 'delete', elementId: rightElementId }))
    }
    const firstRemovedIndex = onBoundary ? containingIndex : containingIndex + 1
    for (let index = firstRemovedIndex; index < elements.length; index += 1) {
      operations.push(Object.freeze({
        kind: 'delete',
        elementId: elements[index]!.id,
      }))
    }
  }

  return operations.length === 0 ? null : Object.freeze(operations)
}

export function mapOutputTimeToSource(
  elements: readonly TimelineElement[],
  outputTime: ExactRationalTime,
): ExactRationalTime | null {
  const element = elements.find((candidate) =>
    exactTimeIsInHalfOpenRange(
      outputTime,
      candidate.outputRange.start,
      candidate.outputRange.duration,
    ),
  )
  if (element?.sourceRange === null || element === undefined) {
    return null
  }
  return addExactRationalTime(
    element.sourceRange.start,
    subtractExactRationalTime(outputTime, element.outputRange.start),
  )
}

export function mapUniqueSourceTimeToOutput(
  elements: readonly TimelineElement[],
  sourceTime: ExactRationalTime,
): ExactRationalTime | null {
  const matches = elements.filter(
    (candidate) =>
      candidate.sourceRange !== null &&
      exactTimeIsInHalfOpenRange(
        sourceTime,
        candidate.sourceRange.start,
        candidate.sourceRange.duration,
      ),
  )
  if (matches.length !== 1) {
    return null
  }
  const match = matches[0]!
  return addExactRationalTime(
    match.outputRange.start,
    subtractExactRationalTime(sourceTime, match.sourceRange!.start),
  )
}

export function resolveTimelinePlayhead(
  elements: readonly TimelineElement[],
  playerPosition: TimelinePlayerPosition | null,
  projectId: string,
  preferSourceTime = false,
): ExactRationalTime | null {
  if (playerPosition === null || playerPosition.projectId !== projectId) {
    return null
  }
  if (preferSourceTime) {
    return mapUniqueSourceTimeToOutput(
      elements,
      playerPosition.sourcePresentationTime,
    )
  }
  if (
    playerPosition.outputPresentationTime !== null &&
    elements.some((element) =>
      exactTimeIsInHalfOpenRange(
        playerPosition.outputPresentationTime!,
        element.outputRange.start,
        element.outputRange.duration,
      ),
    )
  ) {
    return playerPosition.outputPresentationTime
  }
  return mapUniqueSourceTimeToOutput(
    elements,
    playerPosition.sourcePresentationTime,
  )
}

export function flattenTimelineOperationGroups(
  groups: readonly (readonly TimelineOperation[])[],
): readonly TimelineOperation[] {
  return Object.freeze(groups.flatMap((group) => group))
}

function splitElement(
  elements: TimelineElement[],
  targetIndex: number,
  operation: Extract<TimelineOperation, Readonly<{ kind: 'split' }>>,
): void {
  const target = elements[targetIndex]!
  if (
    compareExactRationalTime(operation.splitOffset, exactZero) <= 0 ||
    compareExactRationalTime(
      operation.splitOffset,
      target.outputRange.duration,
    ) >= 0 ||
    elements.some((element) => element.id === operation.rightElementId)
  ) {
    throw new RangeError('A split operation is invalid for its target element.')
  }

  const rightDuration = subtractExactRationalTime(
    target.outputRange.duration,
    operation.splitOffset,
  )
  const rightStart = addExactRationalTime(
    target.outputRange.start,
    operation.splitOffset,
  )
  const leftSourceRange =
    target.sourceRange === null
      ? null
      : freezeRange(target.sourceRange.start, operation.splitOffset)
  const rightSourceRange =
    target.sourceRange === null
      ? null
      : freezeRange(
          addExactRationalTime(
            target.sourceRange.start,
            operation.splitOffset,
          ),
          rightDuration,
        )
  const left = freezeElement(
    target,
    freezeRange(target.outputRange.start, operation.splitOffset),
    leftSourceRange,
  )
  const right = Object.freeze({
    id: operation.rightElementId,
    outputRange: freezeRange(rightStart, rightDuration),
    origin: target.origin,
    sourceRange: rightSourceRange,
  })
  elements.splice(targetIndex, 1, left, right)
}

function reflowTimeline(
  elements: readonly TimelineElement[],
): readonly TimelineElement[] {
  let outputStart = exactZero
  const reflowed = elements.map((element) => {
    const outputRange = freezeRange(outputStart, element.outputRange.duration)
    const reflowedElement = freezeElement(
      element,
      outputRange,
      element.sourceRange,
    )
    outputStart = rangeEnd(outputRange)
    return reflowedElement
  })
  return Object.freeze(reflowed)
}

function validateCanonicalTimeline(elements: readonly TimelineElement[]): void {
  let expectedStart = exactZero
  const ids = new Set<string>()
  for (const element of elements) {
    if (
      ids.has(element.id) ||
      !exactRationalTimesEqual(element.outputRange.start, expectedStart) ||
      compareExactRationalTime(element.outputRange.duration, exactZero) <= 0
    ) {
      throw new Error('The timeline is not a unique contiguous canonical sequence.')
    }
    ids.add(element.id)
    expectedStart = rangeEnd(element.outputRange)
  }
}

function findUniqueElementIndex(
  elements: readonly TimelineElement[],
  elementId: string,
): number {
  let match = -1
  for (let index = 0; index < elements.length; index += 1) {
    if (elements[index]!.id !== elementId) {
      continue
    }
    if (match >= 0) {
      throw new Error('A timeline operation target is not unique.')
    }
    match = index
  }
  if (match < 0) {
    throw new Error('A timeline operation target does not exist.')
  }
  return match
}

function freezeElement(
  element: TimelineElement,
  outputRange: TimelineTimeRange,
  sourceRange: TimelineTimeRange | null,
): TimelineElement {
  return Object.freeze({
    id: element.id,
    outputRange,
    origin: element.origin,
    sourceRange,
  })
}

function freezeRange(
  start: ExactRationalTime,
  duration: ExactRationalTime,
): TimelineTimeRange {
  return Object.freeze({ start, duration })
}

function rangeEnd(range: TimelineTimeRange): ExactRationalTime {
  return addExactRationalTime(range.start, range.duration)
}