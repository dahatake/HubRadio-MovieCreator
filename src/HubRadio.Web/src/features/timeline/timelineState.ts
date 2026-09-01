import {
  TIMELINE_APPLY_METHOD,
  TIMELINE_READ_METHOD,
  isCanonicalTimelineId,
  type TimelineApplyResponse,
  type TimelineElement,
  type TimelineOperation,
  type TimelineReadResponse,
  type TimelineSnapshot,
} from './timelineProtocol'
import { exactRationalTimesEqual } from './rationalTime'
import {
  flattenTimelineOperationGroups,
  replayTimelineOperations,
} from './timelineModel'

export type TimelinePhase =
  | 'idle'
  | 'loading'
  | 'ready'
  | 'editing'
  | 'applying'
  | 'conflict'
  | 'error'

export type TimelineIssue =
  | 'bridgeUnavailable'
  | 'sendFailed'
  | 'forbidden'
  | 'notFound'
  | 'unavailable'
  | 'invalidOperation'
  | 'conflict'
  | 'intentInvalidAfterReload'
  | 'invalidResponse'

interface PendingTimelineRequest {
  readonly requestId: string
  readonly projectId: string
  readonly method: typeof TIMELINE_READ_METHOD | typeof TIMELINE_APPLY_METHOD
  readonly preservePending: boolean
}

export interface TimelineState {
  readonly projectIdInput: string
  readonly projectId: string | null
  readonly phase: TimelinePhase
  readonly issue: TimelineIssue | null
  readonly baseline: TimelineSnapshot | null
  readonly previewElements: TimelineSnapshot['elements']
  readonly pendingGroups: readonly (readonly TimelineOperation[])[]
  readonly redoGroups: readonly (readonly TimelineOperation[])[]
  readonly selectedElementIds: readonly string[]
  readonly pendingRequest: PendingTimelineRequest | null
  readonly intentValid: boolean
}

export type TimelineAction =
  | Readonly<{ type: 'projectIdInputChanged'; value: string }>
  | Readonly<{
      type: 'readRequested'
      requestId: string
      projectId: string
      preservePending: boolean
    }>
  | Readonly<{ type: 'readReceived'; response: TimelineReadResponse }>
  | Readonly<{
      type: 'operationsQueued'
      operations: readonly TimelineOperation[]
    }>
  | Readonly<{ type: 'undo' }>
  | Readonly<{ type: 'redo' }>
  | Readonly<{ type: 'pendingDiscarded' }>
  | Readonly<{ type: 'applyRequested'; requestId: string }>
  | Readonly<{ type: 'applyReceived'; response: TimelineApplyResponse }>
  | Readonly<{ type: 'elementSelected'; elementId: string; toggle: boolean }>
  | Readonly<{ type: 'selectionCleared' }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'sendFailed'; requestId: string }>

export const initialTimelineState: TimelineState = Object.freeze({
  projectIdInput: '',
  projectId: null,
  phase: 'idle',
  issue: null,
  baseline: null,
  previewElements: Object.freeze([]),
  pendingGroups: Object.freeze([]),
  redoGroups: Object.freeze([]),
  selectedElementIds: Object.freeze([]),
  pendingRequest: null,
  intentValid: true,
})

export function timelineStateReducer(
  state: TimelineState,
  action: TimelineAction,
): TimelineState {
  switch (action.type) {
    case 'projectIdInputChanged':
      if (state.pendingGroups.length > 0) {
        return state
      }
      return { ...state, projectIdInput: action.value, issue: null }

    case 'readRequested':
      if (
        state.pendingRequest !== null ||
        !isCanonicalTimelineId(action.requestId) ||
        !isCanonicalTimelineId(action.projectId) ||
        (action.preservePending &&
          (state.baseline === null ||
            action.projectId !== state.baseline.projectId))
      ) {
        return state
      }
      return {
        ...state,
        projectIdInput: action.projectId,
        projectId: action.preservePending ? state.projectId : action.projectId,
        phase: 'loading',
        issue: null,
        baseline: action.preservePending ? state.baseline : null,
        previewElements: action.preservePending
          ? state.previewElements
          : Object.freeze([]),
        pendingGroups: action.preservePending
          ? state.pendingGroups
          : Object.freeze([]),
        redoGroups: action.preservePending
          ? state.redoGroups
          : Object.freeze([]),
        selectedElementIds: action.preservePending
          ? state.selectedElementIds
          : Object.freeze([]),
        pendingRequest: Object.freeze({
          requestId: action.requestId,
          projectId: action.projectId,
          method: TIMELINE_READ_METHOD,
          preservePending: action.preservePending,
        }),
        intentValid: true,
      }

    case 'readReceived':
      return receiveRead(state, action.response)

    case 'operationsQueued':
      return queueOperations(state, action.operations)

    case 'undo':
      return undoPendingGroup(state)

    case 'redo':
      return redoPendingGroup(state)

    case 'pendingDiscarded':
      if (state.baseline === null || state.phase === 'applying') {
        return state
      }
      return {
        ...state,
        phase: 'ready',
        issue: null,
        previewElements: state.baseline.elements,
        pendingGroups: Object.freeze([]),
        redoGroups: Object.freeze([]),
        selectedElementIds: filterSelection(
          state.selectedElementIds,
          state.baseline.elements,
        ),
        intentValid: true,
      }

    case 'applyRequested':
      if (
        state.pendingRequest !== null ||
        state.baseline === null ||
        state.pendingGroups.length === 0 ||
        !state.intentValid ||
        !isCanonicalTimelineId(action.requestId)
      ) {
        return state
      }
      return {
        ...state,
        phase: 'applying',
        issue: null,
        pendingRequest: Object.freeze({
          requestId: action.requestId,
          projectId: state.baseline.projectId,
          method: TIMELINE_APPLY_METHOD,
          preservePending: true,
        }),
      }

    case 'applyReceived':
      return receiveApply(state, action.response)

    case 'elementSelected': {
      if (!state.previewElements.some((element) => element.id === action.elementId)) {
        return state
      }
      const selected = new Set(
        action.toggle ? state.selectedElementIds : [],
      )
      if (action.toggle && selected.has(action.elementId)) {
        selected.delete(action.elementId)
      } else {
        selected.add(action.elementId)
      }
      return { ...state, selectedElementIds: Object.freeze([...selected]) }
    }

    case 'selectionCleared':
      return state.selectedElementIds.length === 0
        ? state
        : { ...state, selectedElementIds: Object.freeze([]) }

    case 'bridgeUnavailable':
      return {
        ...state,
        phase: 'error',
        issue: 'bridgeUnavailable',
        pendingRequest: null,
      }

    case 'sendFailed':
      if (state.pendingRequest?.requestId !== action.requestId) {
        return state
      }
      return {
        ...state,
        phase: state.baseline === null
          ? 'error'
          : state.pendingGroups.length === 0
            ? 'ready'
            : 'editing',
        issue: 'sendFailed',
        pendingRequest: null,
      }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}

export function selectPendingTimelineOperations(
  state: TimelineState,
): readonly TimelineOperation[] {
  return flattenTimelineOperationGroups(state.pendingGroups)
}

export function canUndoTimeline(state: TimelineState): boolean {
  return state.pendingRequest === null && state.pendingGroups.length > 0
}

export function canRedoTimeline(state: TimelineState): boolean {
  return state.pendingRequest === null && state.redoGroups.length > 0
}

function receiveRead(
  state: TimelineState,
  response: TimelineReadResponse,
): TimelineState {
  const pending = state.pendingRequest
  if (
    pending === null ||
    pending.method !== TIMELINE_READ_METHOD ||
    pending.requestId !== response.requestId ||
    response.method !== TIMELINE_READ_METHOD
  ) {
    return state
  }
  if (response.result.status !== 'available') {
    return {
      ...state,
      phase: pending.preservePending ? 'conflict' : 'error',
      issue: response.result.status,
      pendingRequest: null,
    }
  }
  const snapshot = response.result.snapshot
  if (snapshot.projectId !== pending.projectId) {
    return {
      ...state,
      phase: pending.preservePending ? 'conflict' : 'error',
      issue: 'invalidResponse',
      pendingRequest: null,
    }
  }

  if (!pending.preservePending || state.pendingGroups.length === 0) {
    return installBaseline(state, snapshot)
  }

  if (
    state.baseline === null ||
    snapshot.workingCopyId !== state.baseline.workingCopyId ||
    snapshot.createdAt !== state.baseline.createdAt ||
    snapshot.revision <= state.baseline.revision
  ) {
    return {
      ...state,
      phase: 'conflict',
      issue: 'invalidResponse',
      pendingRequest: null,
    }
  }

  try {
    const previewElements = replayTimelineOperations(
      snapshot.elements,
      flattenTimelineOperationGroups(state.pendingGroups),
    )
    return {
      ...state,
      projectId: snapshot.projectId,
      phase: 'editing',
      issue: null,
      baseline: snapshot,
      previewElements,
      redoGroups: Object.freeze([]),
      selectedElementIds: filterSelection(
        state.selectedElementIds,
        previewElements,
      ),
      pendingRequest: null,
      intentValid: true,
    }
  } catch {
    return {
      ...state,
      projectId: snapshot.projectId,
      phase: 'conflict',
      issue: 'intentInvalidAfterReload',
      baseline: snapshot,
      previewElements: snapshot.elements,
      redoGroups: Object.freeze([]),
      selectedElementIds: filterSelection(
        state.selectedElementIds,
        snapshot.elements,
      ),
      pendingRequest: null,
      intentValid: false,
    }
  }
}

function receiveApply(
  state: TimelineState,
  response: TimelineApplyResponse,
): TimelineState {
  const pending = state.pendingRequest
  if (
    pending === null ||
    pending.method !== TIMELINE_APPLY_METHOD ||
    pending.requestId !== response.requestId ||
    response.method !== TIMELINE_APPLY_METHOD
  ) {
    return state
  }
  if (response.result.status === 'available') {
    const snapshot = response.result.snapshot
    if (
      state.baseline === null ||
      !isExpectedApplySnapshot(
        state.baseline,
        state.previewElements,
        pending.projectId,
        snapshot,
      )
    ) {
      return {
        ...state,
        phase: 'editing',
        issue: 'invalidResponse',
        pendingRequest: null,
      }
    }
    return installBaseline(state, snapshot)
  }

  if (response.result.status === 'conflict') {
    return {
      ...state,
      phase: 'conflict',
      issue: 'conflict',
      pendingRequest: null,
    }
  }
  return {
    ...state,
    phase: state.pendingGroups.length === 0 ? 'ready' : 'editing',
    issue: response.result.status,
    pendingRequest: null,
  }
}

function installBaseline(
  state: TimelineState,
  snapshot: TimelineSnapshot,
): TimelineState {
  return {
    ...state,
    projectIdInput: snapshot.projectId,
    projectId: snapshot.projectId,
    phase: 'ready',
    issue: null,
    baseline: snapshot,
    previewElements: snapshot.elements,
    pendingGroups: Object.freeze([]),
    redoGroups: Object.freeze([]),
    selectedElementIds: filterSelection(
      state.selectedElementIds,
      snapshot.elements,
    ),
    pendingRequest: null,
    intentValid: true,
  }
}

function queueOperations(
  state: TimelineState,
  operations: readonly TimelineOperation[],
): TimelineState {
  if (
    state.baseline === null ||
    state.pendingRequest !== null ||
    state.phase === 'conflict' ||
    operations.length === 0 ||
    !state.intentValid
  ) {
    return state
  }
  const group = Object.freeze([...operations])
  const pendingGroups = Object.freeze([...state.pendingGroups, group])
  try {
    const previewElements = replayTimelineOperations(
      state.baseline.elements,
      flattenTimelineOperationGroups(pendingGroups),
    )
    return {
      ...state,
      phase: 'editing',
      issue: null,
      previewElements,
      pendingGroups,
      redoGroups: Object.freeze([]),
      selectedElementIds: filterSelection(
        state.selectedElementIds,
        previewElements,
      ),
    }
  } catch {
    return { ...state, issue: 'invalidOperation' }
  }
}

function undoPendingGroup(state: TimelineState): TimelineState {
  if (!canUndoTimeline(state) || state.baseline === null) {
    return state
  }
  const group = state.pendingGroups[state.pendingGroups.length - 1]!
  const pendingGroups = Object.freeze(state.pendingGroups.slice(0, -1))
  const previewElements = replayTimelineOperations(
    state.baseline.elements,
    flattenTimelineOperationGroups(pendingGroups),
  )
  return {
    ...state,
    phase: pendingGroups.length === 0 ? 'ready' : 'editing',
    issue: null,
    previewElements,
    pendingGroups,
    redoGroups: Object.freeze([group, ...state.redoGroups]),
    selectedElementIds: filterSelection(
      state.selectedElementIds,
      previewElements,
    ),
    intentValid: true,
  }
}

function redoPendingGroup(state: TimelineState): TimelineState {
  if (!canRedoTimeline(state) || state.baseline === null) {
    return state
  }
  const [group, ...remainingRedo] = state.redoGroups
  if (group === undefined) {
    return state
  }
  const pendingGroups = Object.freeze([...state.pendingGroups, group])
  try {
    const previewElements = replayTimelineOperations(
      state.baseline.elements,
      flattenTimelineOperationGroups(pendingGroups),
    )
    return {
      ...state,
      phase: 'editing',
      issue: null,
      previewElements,
      pendingGroups,
      redoGroups: Object.freeze(remainingRedo),
      selectedElementIds: filterSelection(
        state.selectedElementIds,
        previewElements,
      ),
      intentValid: true,
    }
  } catch {
    return {
      ...state,
      phase: 'conflict',
      issue: 'intentInvalidAfterReload',
      intentValid: false,
    }
  }
}

function filterSelection(
  selectedIds: readonly string[],
  elements: TimelineSnapshot['elements'],
): readonly string[] {
  const existing = new Set(elements.map((element) => element.id))
  return Object.freeze(selectedIds.filter((id) => existing.has(id)))
}

function isExpectedApplySnapshot(
  baseline: TimelineSnapshot,
  previewElements: TimelineSnapshot['elements'],
  projectId: string,
  snapshot: TimelineSnapshot,
): boolean {
  return (
    snapshot.projectId === projectId &&
    snapshot.workingCopyId === baseline.workingCopyId &&
    snapshot.createdAt === baseline.createdAt &&
    snapshot.updatedAt >= baseline.updatedAt &&
    snapshot.revision === baseline.revision + 1 &&
    snapshot.baseVersionId !== null &&
    snapshot.baseVersionId !== baseline.baseVersionId &&
    timelineElementsEqual(snapshot.elements, previewElements)
  )
}

function timelineElementsEqual(
  left: readonly TimelineElement[],
  right: readonly TimelineElement[],
): boolean {
  return left.length === right.length && left.every((element, index) => {
    const other = right[index]
    return other !== undefined &&
      element.id === other.id &&
      rangesEqual(element.outputRange, other.outputRange) &&
      originsEqual(element.origin, other.origin) &&
      (element.sourceRange === null
        ? other.sourceRange === null
        : other.sourceRange !== null &&
          rangesEqual(element.sourceRange, other.sourceRange))
  })
}

function rangesEqual(
  left: TimelineElement['outputRange'],
  right: TimelineElement['outputRange'],
): boolean {
  return exactRationalTimesEqual(left.start, right.start) &&
    exactRationalTimesEqual(left.duration, right.duration)
}

function originsEqual(
  left: TimelineElement['origin'],
  right: TimelineElement['origin'],
): boolean {
  if (left.kind !== right.kind) return false
  switch (left.kind) {
    case 'sourceSpan':
      return right.kind === 'sourceSpan' && left.sourceSpanId === right.sourceSpanId
    case 'asset':
      return right.kind === 'asset' && left.assetId === right.assetId
    case 'editorialRecord':
      return right.kind === 'editorialRecord' &&
        left.editorialRecordId === right.editorialRecordId
  }
}