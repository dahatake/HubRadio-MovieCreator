import { BRIDGE_PROTOCOL_VERSION } from '../../bridge/protocol'
import {
  addExactRationalTime,
  compareExactRationalTime,
  exactRationalTimesEqual,
  exactZero,
  isStrictlyPositiveExactTime,
  parseExactRationalTime,
  type ExactRationalTime,
} from './rationalTime'

export const TIMELINE_READ_METHOD = 'timeline.read' as const
export const TIMELINE_APPLY_METHOD = 'timeline.apply' as const
export const TIMELINE_MAX_REQUEST_UTF8_BYTES = 96 * 1024
export const TIMELINE_MAX_RESPONSE_UTF8_BYTES = 1024 * 1024

const canonicalIdPattern =
  /^(?!00000000-0000-0000-0000-000000000000(?![\s\S]))[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![\s\S])/
const timestampPattern =
  /^(?!0000)\d{4}-(0[1-9]|1[0-2])-([0-2]\d|3[0-1])T([0-1]\d|2[0-3]):[0-5]\d:[0-5]\d\.\d{7}\+00:00(?![\s\S])/

export type TimelineOrigin =
  | Readonly<{ kind: 'sourceSpan'; sourceSpanId: string }>
  | Readonly<{ kind: 'asset'; assetId: string }>
  | Readonly<{ kind: 'editorialRecord'; editorialRecordId: string }>

export interface TimelineTimeRange {
  readonly start: ExactRationalTime
  readonly duration: ExactRationalTime
}

export interface TimelineElement {
  readonly id: string
  readonly outputRange: TimelineTimeRange
  readonly origin: TimelineOrigin
  readonly sourceRange: TimelineTimeRange | null
}

export interface TimelineSnapshot {
  readonly workingCopyId: string
  readonly projectId: string
  readonly baseVersionId: string | null
  readonly revision: number
  readonly createdAt: string
  readonly updatedAt: string
  readonly elements: readonly TimelineElement[]
}

export type TimelineOperation =
  | Readonly<{ kind: 'keep'; elementId: string }>
  | Readonly<{ kind: 'delete'; elementId: string }>
  | Readonly<{
      kind: 'split'
      elementId: string
      splitOffset: ExactRationalTime
      rightElementId: string
    }>
  | Readonly<{ kind: 'move'; elementId: string; targetIndex: number }>

export interface TimelineReadRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof TIMELINE_READ_METHOD
  readonly params: Readonly<{ projectId: string }>
}

export interface TimelineApplyRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof TIMELINE_APPLY_METHOD
  readonly params: Readonly<{
    projectId: string
    expectedRevision: number
    operations: readonly TimelineOperation[]
  }>
}

export type TimelineBridgeRequest = TimelineReadRequest | TimelineApplyRequest

export type TimelineReadResponse =
  | AvailableTimelineResponse<typeof TIMELINE_READ_METHOD>
  | UnavailableTimelineResponse<
      typeof TIMELINE_READ_METHOD,
      'forbidden' | 'notFound' | 'unavailable'
    >

export type TimelineApplyResponse =
  | AvailableTimelineResponse<typeof TIMELINE_APPLY_METHOD>
  | UnavailableTimelineResponse<
      typeof TIMELINE_APPLY_METHOD,
      'forbidden' | 'notFound' | 'conflict' | 'invalidOperation' | 'unavailable'
    >

interface AvailableTimelineResponse<TMethod extends string> {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: TMethod
  readonly result: Readonly<{
    status: 'available'
    snapshot: TimelineSnapshot
  }>
}

interface UnavailableTimelineResponse<
  TMethod extends string,
  TStatus extends string,
> {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: TMethod
  readonly result: Readonly<{
    status: TStatus
    snapshot: null
  }>
}

export function isCanonicalTimelineId(value: string): boolean {
  return canonicalIdPattern.test(value)
}

export function createTimelineReadRequest(
  requestId: string,
  projectId: string,
): TimelineReadRequest {
  assertCanonicalId(requestId, 'request')
  assertCanonicalId(projectId, 'project')
  return ensureBoundedRequest(
    Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId,
      method: TIMELINE_READ_METHOD,
      params: Object.freeze({ projectId }),
    }),
  )
}

export function createTimelineApplyRequest(
  requestId: string,
  projectId: string,
  expectedRevision: number,
  operations: readonly TimelineOperation[],
): TimelineApplyRequest {
  assertCanonicalId(requestId, 'request')
  assertCanonicalId(projectId, 'project')
  if (!isNonnegativeInt32(expectedRevision)) {
    throw new RangeError('An expected timeline revision must be a nonnegative Int32.')
  }
  if (operations.length === 0) {
    throw new RangeError('A timeline apply request requires at least one operation.')
  }

  const normalizedOperations = Object.freeze(
    operations.map(normalizeTimelineOperation),
  )
  return ensureBoundedRequest(
    Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId,
      method: TIMELINE_APPLY_METHOD,
      params: Object.freeze({
        projectId,
        expectedRevision,
        operations: normalizedOperations,
      }),
    }),
  )
}

export function parseTimelineReadResponse(
  value: unknown,
): TimelineReadResponse | null {
  return parseTimelineResponse(
    value,
    TIMELINE_READ_METHOD,
    ['forbidden', 'notFound', 'unavailable'],
  )
}

export function parseTimelineApplyResponse(
  value: unknown,
): TimelineApplyResponse | null {
  return parseTimelineResponse(
    value,
    TIMELINE_APPLY_METHOD,
    ['forbidden', 'notFound', 'conflict', 'invalidOperation', 'unavailable'],
  )
}

function parseTimelineResponse<
  TMethod extends typeof TIMELINE_READ_METHOD | typeof TIMELINE_APPLY_METHOD,
  TStatus extends string,
>(
  value: unknown,
  method: TMethod,
  unavailableStatuses: readonly TStatus[],
):
  | AvailableTimelineResponse<TMethod>
  | UnavailableTimelineResponse<TMethod, TStatus>
  | null {
  if (
    !isWithinUtf8Limit(value, TIMELINE_MAX_RESPONSE_UTF8_BYTES) ||
    !hasExactKeys(value, ['version', 'requestId', 'method', 'result']) ||
    value.version !== BRIDGE_PROTOCOL_VERSION ||
    !isCanonicalUnknownId(value.requestId) ||
    value.method !== method ||
    !hasExactKeys(value.result, ['status', 'snapshot'])
  ) {
    return null
  }

  const result = value.result
  if (result.status === 'available') {
    const snapshot = parseTimelineSnapshot(result.snapshot)
    if (snapshot === null) {
      return null
    }
    return Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId: value.requestId,
      method,
      result: Object.freeze({ status: 'available', snapshot }),
    })
  }

  if (
    result.snapshot !== null ||
    typeof result.status !== 'string' ||
    !unavailableStatuses.includes(result.status as TStatus)
  ) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method,
    result: Object.freeze({ status: result.status as TStatus, snapshot: null }),
  })
}

function parseTimelineSnapshot(value: unknown): TimelineSnapshot | null {
  if (
    !hasExactKeys(value, [
      'workingCopyId',
      'projectId',
      'baseVersionId',
      'revision',
      'createdAt',
      'updatedAt',
      'elements',
    ]) ||
    !isCanonicalUnknownId(value.workingCopyId) ||
    !isCanonicalUnknownId(value.projectId) ||
    !isNullableCanonicalId(value.baseVersionId) ||
    !isNonnegativeInt32(value.revision) ||
    !isCanonicalTimestamp(value.createdAt) ||
    !isCanonicalTimestamp(value.updatedAt) ||
    value.createdAt > value.updatedAt ||
    !Array.isArray(value.elements)
  ) {
    return null
  }

  const elements: TimelineElement[] = []
  const ids = new Set<string>()
  let expectedStart = exactZero
  try {
    for (const candidate of value.elements) {
      const element = parseTimelineElement(candidate)
      if (
        element === null ||
        ids.has(element.id) ||
        !exactRationalTimesEqual(element.outputRange.start, expectedStart)
      ) {
        return null
      }
      ids.add(element.id)
      elements.push(element)
      expectedStart = addExactRationalTime(
        element.outputRange.start,
        element.outputRange.duration,
      )
    }
  } catch {
    return null
  }

  return Object.freeze({
    workingCopyId: value.workingCopyId,
    projectId: value.projectId,
    baseVersionId: value.baseVersionId,
    revision: value.revision,
    createdAt: value.createdAt,
    updatedAt: value.updatedAt,
    elements: Object.freeze(elements),
  })
}

function parseTimelineElement(value: unknown): TimelineElement | null {
  if (
    !hasExactKeys(value, ['id', 'outputRange', 'origin', 'sourceRange']) ||
    !isCanonicalUnknownId(value.id)
  ) {
    return null
  }

  const outputRange = parseTimelineRange(value.outputRange, true)
  const origin = parseTimelineOrigin(value.origin)
  const sourceRange =
    value.sourceRange === null
      ? null
      : parseTimelineRange(value.sourceRange, false)
  if (outputRange === null || origin === null) {
    return null
  }
  if (
    origin.kind === 'sourceSpan'
      ? sourceRange === null ||
        !exactRationalTimesEqual(sourceRange.duration, outputRange.duration)
      : sourceRange !== null
  ) {
    return null
  }

  return Object.freeze({ id: value.id, outputRange, origin, sourceRange })
}

function parseTimelineRange(
  value: unknown,
  requireNonnegativeStart: boolean,
): TimelineTimeRange | null {
  if (!hasExactKeys(value, ['start', 'duration'])) {
    return null
  }
  const start = parseExactRationalTime(value.start)
  const duration = parseExactRationalTime(value.duration)
  if (
    start === null ||
    duration === null ||
    !isStrictlyPositiveExactTime(duration) ||
    (requireNonnegativeStart && compareExactRationalTime(start, exactZero) < 0)
  ) {
    return null
  }
  try {
    addExactRationalTime(start, duration)
  } catch {
    return null
  }
  return Object.freeze({ start, duration })
}

function parseTimelineOrigin(value: unknown): TimelineOrigin | null {
  if (!hasAtLeastKind(value) || typeof value.kind !== 'string') {
    return null
  }
  switch (value.kind) {
    case 'sourceSpan':
      return hasExactKeys(value, ['kind', 'sourceSpanId']) &&
        isCanonicalUnknownId(value.sourceSpanId)
        ? Object.freeze({ kind: 'sourceSpan', sourceSpanId: value.sourceSpanId })
        : null
    case 'asset':
      return hasExactKeys(value, ['kind', 'assetId']) &&
        isCanonicalUnknownId(value.assetId)
        ? Object.freeze({ kind: 'asset', assetId: value.assetId })
        : null
    case 'editorialRecord':
      return hasExactKeys(value, ['kind', 'editorialRecordId']) &&
        isCanonicalUnknownId(value.editorialRecordId)
        ? Object.freeze({
            kind: 'editorialRecord',
            editorialRecordId: value.editorialRecordId,
          })
        : null
    default:
      return null
  }
}

function normalizeTimelineOperation(
  operation: TimelineOperation,
): TimelineOperation {
  assertCanonicalId(operation.elementId, 'timeline element')
  switch (operation.kind) {
    case 'keep':
      return Object.freeze({ kind: 'keep', elementId: operation.elementId })
    case 'delete':
      return Object.freeze({ kind: 'delete', elementId: operation.elementId })
    case 'split': {
      assertCanonicalId(operation.rightElementId, 'right timeline element')
      const splitOffset = parseExactRationalTime(operation.splitOffset)
      if (
        splitOffset === null ||
        !isStrictlyPositiveExactTime(splitOffset) ||
        operation.rightElementId === operation.elementId
      ) {
        throw new RangeError('A split operation requires a positive canonical offset and a distinct right ID.')
      }
      return Object.freeze({
        kind: 'split',
        elementId: operation.elementId,
        splitOffset,
        rightElementId: operation.rightElementId,
      })
    }
    case 'move':
      if (!isNonnegativeInt32(operation.targetIndex)) {
        throw new RangeError('A move target index must be a nonnegative Int32.')
      }
      return Object.freeze({
        kind: 'move',
        elementId: operation.elementId,
        targetIndex: operation.targetIndex,
      })
  }

  const exhaustiveOperation: never = operation
  throw new TypeError(`Unsupported timeline operation: ${String(exhaustiveOperation)}`)
}

function ensureBoundedRequest<T extends TimelineBridgeRequest>(request: T): T {
  if (!isWithinUtf8Limit(request, TIMELINE_MAX_REQUEST_UTF8_BYTES)) {
    throw new RangeError('The timeline bridge request exceeds the protocol byte limit.')
  }
  return request
}

function assertCanonicalId(value: string, kind: string): void {
  if (!canonicalIdPattern.test(value)) {
    throw new TypeError(`A ${kind} ID must be a non-empty lowercase UUID.`)
  }
}

function isCanonicalUnknownId(value: unknown): value is string {
  return typeof value === 'string' && canonicalIdPattern.test(value)
}

function isNullableCanonicalId(value: unknown): value is string | null {
  return value === null || isCanonicalUnknownId(value)
}

function isCanonicalTimestamp(value: unknown): value is string {
  if (typeof value !== 'string' || !timestampPattern.test(value)) {
    return false
  }
  const millisecondTimestamp = `${value.slice(0, 23)}Z`
  const parsed = new Date(millisecondTimestamp)
  return (
    Number.isFinite(parsed.valueOf()) &&
    parsed.toISOString() === millisecondTimestamp
  )
}

function isNonnegativeInt32(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= 2_147_483_647
  )
}

function isWithinUtf8Limit(value: unknown, maximumBytes: number): boolean {
  try {
    const serialized = JSON.stringify(value)
    return (
      serialized !== undefined &&
      new TextEncoder().encode(serialized).byteLength <= maximumBytes
    )
  } catch {
    return false
  }
}

function hasAtLeastKind(
  value: unknown,
): value is Record<string, unknown> & Readonly<{ kind: unknown }> {
  return (
    typeof value === 'object' &&
    value !== null &&
    !Array.isArray(value) &&
    Object.prototype.hasOwnProperty.call(value, 'kind')
  )
}

function hasExactKeys(
  value: unknown,
  expectedKeys: readonly string[],
): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false
  }
  const keys = Object.keys(value)
  return (
    keys.length === expectedKeys.length &&
    expectedKeys.every((key) => Object.prototype.hasOwnProperty.call(value, key))
  )
}
