import { BRIDGE_PROTOCOL_VERSION } from '../../bridge/protocol'

export const COMPARISON_READ_METHOD = 'compare.read' as const
export const COMPARISON_MAX_REQUEST_UTF8_BYTES = 4 * 1024
export const COMPARISON_MAX_RESPONSE_UTF8_BYTES = 1024 * 1024

export interface ComparisonRationalTime {
  readonly value: string
  readonly rate: string
}

export interface ComparisonTimeRange {
  readonly start: ComparisonRationalTime
  readonly duration: ComparisonRationalTime
}

export interface UsedSourceRange {
  readonly elementId: string
  readonly sourceSpanId: string
  readonly sourceRange: ComparisonTimeRange
  readonly outputRange: ComparisonTimeRange
}

export interface ComparisonOpeningHook {
  readonly status: 'recorded'
  readonly elementId: string
  readonly origin: 'sourceSpan' | 'asset' | 'editorialRecord'
  readonly outputStart: ComparisonRationalTime
  readonly outputDuration: ComparisonRationalTime
  readonly adopted: boolean
}

export interface ComparisonTransitions {
  readonly status: 'recorded'
  readonly cutCount: number
}

export interface ComparisonDerivedSummary {
  readonly status: 'recorded'
  readonly summary: string
}

export interface ComparisonFeatureAvailability {
  readonly openingHook: 'unavailable' | ComparisonOpeningHook
  readonly subtitleStyle: 'unavailable' | ComparisonDerivedSummary
  readonly telopCount: 'unavailable' | ComparisonDerivedSummary
  readonly transitions: 'unavailable' | ComparisonTransitions
  readonly effects: 'unavailable' | ComparisonDerivedSummary
  readonly callToAction: 'unavailable' | ComparisonDerivedSummary
  readonly destination: 'unavailable' | ComparisonDerivedSummary
  readonly warnings: 'unavailable' | ComparisonDerivedSummary
}

export interface ComparisonVersionSnapshot {
  readonly versionId: string
  readonly versionDigest: string
  readonly createdAt: string
  readonly changeReason: string
  readonly playbackDuration: ComparisonRationalTime
  readonly usedSourceRanges: readonly UsedSourceRange[]
  readonly features: ComparisonFeatureAvailability
}

export type ComparisonElementChangeKind =
  | 'added'
  | 'removed'
  | 'moved'
  | 'modified'

export interface ComparisonElementChange {
  readonly kind: ComparisonElementChangeKind
  readonly elementId: string
  readonly previousIndex: number | null
  readonly currentIndex: number | null
}

export type ComparisonAdoptionChangeKind =
  | 'adopted'
  | 'removed'
  | 'replaced'

export interface ComparisonAdoptionChange {
  readonly kind: ComparisonAdoptionChangeKind
  readonly elementId: string
  readonly previousSuggestionId: string | null
  readonly currentSuggestionId: string | null
}

export interface ComparisonSnapshot {
  readonly left: ComparisonVersionSnapshot
  readonly right: ComparisonVersionSnapshot
  readonly elementChanges: readonly ComparisonElementChange[]
  readonly adoptionChanges: readonly ComparisonAdoptionChange[]
}

export interface ComparisonReadRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof COMPARISON_READ_METHOD
  readonly params: Readonly<{
    leftVersionId: string
    leftVersionDigest: string
    rightVersionId: string
    rightVersionDigest: string
  }>
}

export type ComparisonFailureStatus =
  | 'forbidden'
  | 'notFound'
  | 'staleVersion'
  | 'invalidComparison'
  | 'unavailable'

export type ComparisonReadResult =
  | Readonly<{
      status: 'available'
      comparison: ComparisonSnapshot
    }>
  | Readonly<{
      status: ComparisonFailureStatus
      comparison: null
    }>

export interface ComparisonReadResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof COMPARISON_READ_METHOD
  readonly result: ComparisonReadResult
}

export function createComparisonReadRequest(
  requestId: string,
  leftVersionId: string,
  leftVersionDigest: string,
  rightVersionId: string,
  rightVersionDigest: string,
): ComparisonReadRequest {
  requireEntityId(requestId, 'A bridge request ID')
  requireVersionTarget(leftVersionId, leftVersionDigest, 'The left version')
  requireVersionTarget(rightVersionId, rightVersionDigest, 'The right version')

  const request: ComparisonReadRequest = Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: COMPARISON_READ_METHOD,
    params: Object.freeze({
      leftVersionId,
      leftVersionDigest,
      rightVersionId,
      rightVersionDigest,
    }),
  })
  if (!isWithinUtf8Limit(request, COMPARISON_MAX_REQUEST_UTF8_BYTES)) {
    throw new Error('The comparison request exceeds the supported UTF-8 byte limit.')
  }
  return request
}

export function parseComparisonReadResponse(
  value: unknown,
): ComparisonReadResponse | null {
  if (
    !isWithinUtf8Limit(value, COMPARISON_MAX_RESPONSE_UTF8_BYTES) ||
    !isRecordWithExactKeys(value, [
      'version',
      'requestId',
      'method',
      'result',
    ]) ||
    value.version !== BRIDGE_PROTOCOL_VERSION ||
    !isEntityId(value.requestId) ||
    value.method !== COMPARISON_READ_METHOD ||
    !isRecordWithExactKeys(value.result, ['status', 'comparison'])
  ) {
    return null
  }

  const result = value.result
  if (result.status !== 'available') {
    if (
      !comparisonFailureStatuses.has(result.status as ComparisonFailureStatus) ||
      result.comparison !== null
    ) {
      return null
    }
    return Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId: value.requestId,
      method: COMPARISON_READ_METHOD,
      result: Object.freeze({
        status: result.status as ComparisonFailureStatus,
        comparison: null,
      }),
    })
  }

  const comparison = parseComparison(result.comparison)
  if (comparison === null) {
    return null
  }
  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method: COMPARISON_READ_METHOD,
    result: Object.freeze({ status: 'available', comparison }),
  })
}

export function isCanonicalComparisonEntityId(value: string): boolean {
  return entityIdPattern.test(value)
}

export function isCanonicalComparisonDigest(value: string): boolean {
  return digestPattern.test(value)
}

function parseComparison(value: unknown): ComparisonSnapshot | null {
  if (!isRecordWithExactKeys(value, [
    'left',
    'right',
    'elementChanges',
    'adoptionChanges',
  ])) {
    return null
  }
  const left = parseVersion(value.left)
  const right = parseVersion(value.right)
  const elementChanges = parseElementChanges(value.elementChanges)
  if (left === null || right === null || elementChanges === null) {
    return null
  }
  const adoptionChanges = parseAdoptionChanges(
    value.adoptionChanges,
    elementChanges,
  )
  if (
    adoptionChanges === null ||
    (left.versionId === right.versionId && (
      !versionsAreIdentical(left, right) ||
      elementChanges.length !== 0 ||
      adoptionChanges.length !== 0
    ))
  ) {
    return null
  }
  return Object.freeze({ left, right, elementChanges, adoptionChanges })
}

function versionsAreIdentical(
  left: ComparisonVersionSnapshot,
  right: ComparisonVersionSnapshot,
): boolean {
  return left.versionId === right.versionId &&
    left.versionDigest === right.versionDigest &&
    left.createdAt === right.createdAt &&
    left.changeReason === right.changeReason &&
    rationalTimeIsIdentical(left.playbackDuration, right.playbackDuration) &&
    left.usedSourceRanges.length === right.usedSourceRanges.length &&
    left.usedSourceRanges.every((range, index) => {
      const candidate = right.usedSourceRanges[index]
      return candidate !== undefined &&
        range.elementId === candidate.elementId &&
        range.sourceSpanId === candidate.sourceSpanId &&
        timeRangeIsIdentical(range.sourceRange, candidate.sourceRange) &&
        timeRangeIsIdentical(range.outputRange, candidate.outputRange)
    })
}

function timeRangeIsIdentical(
  left: ComparisonTimeRange,
  right: ComparisonTimeRange,
): boolean {
  return rationalTimeIsIdentical(left.start, right.start) &&
    rationalTimeIsIdentical(left.duration, right.duration)
}

function rationalTimeIsIdentical(
  left: ComparisonRationalTime,
  right: ComparisonRationalTime,
): boolean {
  return left.value === right.value && left.rate === right.rate
}

function parseVersion(value: unknown): ComparisonVersionSnapshot | null {
  if (
    !isRecordWithExactKeys(value, [
      'versionId',
      'versionDigest',
      'createdAt',
      'changeReason',
      'playbackDuration',
      'usedSourceRanges',
      'features',
    ]) ||
    !isEntityId(value.versionId) ||
    !isContentDigest(value.versionDigest) ||
    !isNativeTimestamp(value.createdAt) ||
    !isSubstantiveTrimmedString(value.changeReason)
  ) {
    return null
  }
  const playbackDuration = parseRationalTime(value.playbackDuration, false, true)
  const usedSourceRanges = parseUsedSourceRanges(value.usedSourceRanges)
  const features = parseFeatureAvailability(value.features)
  if (
    playbackDuration === null ||
    usedSourceRanges === null ||
    features === null ||
    usedSourceRanges.some((range) =>
      !rationalSumIsAtMost(
        range.outputRange.start,
        range.outputRange.duration,
        playbackDuration,
      ),
    )
  ) {
    return null
  }
  return Object.freeze({
    versionId: value.versionId,
    versionDigest: value.versionDigest,
    createdAt: value.createdAt,
    changeReason: value.changeReason,
    playbackDuration,
    usedSourceRanges,
    features,
  })
}

function parseUsedSourceRanges(
  value: unknown,
): readonly UsedSourceRange[] | null {
  if (!Array.isArray(value)) {
    return null
  }
  const ranges: UsedSourceRange[] = []
  const elementIds = new Set<string>()
  for (const candidate of value) {
    if (
      !isRecordWithExactKeys(candidate, [
        'elementId',
        'sourceSpanId',
        'sourceRange',
        'outputRange',
      ]) ||
      !isEntityId(candidate.elementId) ||
      elementIds.has(candidate.elementId) ||
      !isEntityId(candidate.sourceSpanId)
    ) {
      return null
    }
    const sourceRange = parseTimeRange(candidate.sourceRange, true)
    const outputRange = parseTimeRange(candidate.outputRange, false)
    if (
      sourceRange === null ||
      outputRange === null ||
      !rationalEquals(sourceRange.duration, outputRange.duration)
    ) {
      return null
    }
    elementIds.add(candidate.elementId)
    ranges.push(Object.freeze({
      elementId: candidate.elementId,
      sourceSpanId: candidate.sourceSpanId,
      sourceRange,
      outputRange,
    }))
  }
  return Object.freeze(ranges)
}

function parseFeatureAvailability(
  value: unknown,
): ComparisonFeatureAvailability | null {
  if (!isRecordWithExactKeys(value, featureNames)) {
    return null
  }
  const plain = [
    'subtitleStyle',
    'telopCount',
    'effects',
    'callToAction',
    'destination',
    'warnings',
  ] as const
  const summaries = {} as Record<(typeof plain)[number], ComparisonDerivedSummary | 'unavailable'>
  for (const name of plain) {
    const parsed = parseDerivedSummary(value[name])
    if (parsed === null) {
      return null
    }
    summaries[name] = parsed
  }
  let openingHook: ComparisonFeatureAvailability['openingHook'] = 'unavailable'
  if (value.openingHook !== 'unavailable') {
    const hook = value.openingHook
    if (
      !isRecordWithExactKeys(hook, [
        'status',
        'elementId',
        'origin',
        'outputStart',
        'outputDuration',
        'adopted',
      ]) ||
      hook.status !== 'recorded' ||
      !isEntityId(hook.elementId) ||
      (hook.origin !== 'sourceSpan' &&
        hook.origin !== 'asset' &&
        hook.origin !== 'editorialRecord') ||
      typeof hook.adopted !== 'boolean'
    ) {
      return null
    }
    const outputStart = parseRationalTime(hook.outputStart, false, true)
    const outputDuration = parseRationalTime(hook.outputDuration, false, false)
    if (outputStart === null || outputDuration === null) {
      return null
    }
    openingHook = Object.freeze({
      status: 'recorded',
      elementId: hook.elementId,
      origin: hook.origin,
      outputStart,
      outputDuration,
      adopted: hook.adopted,
    })
  }
  let transitions: ComparisonFeatureAvailability['transitions'] = 'unavailable'
  if (value.transitions !== 'unavailable') {
    const t = value.transitions
    if (
      !isRecordWithExactKeys(t, ['status', 'cutCount']) ||
      t.status !== 'recorded' ||
      typeof t.cutCount !== 'number' ||
      !Number.isInteger(t.cutCount) ||
      t.cutCount < 0
    ) {
      return null
    }
    transitions = Object.freeze({ status: 'recorded', cutCount: t.cutCount })
  }
  return Object.freeze({
    openingHook,
    subtitleStyle: summaries.subtitleStyle,
    telopCount: summaries.telopCount,
    transitions,
    effects: summaries.effects,
    callToAction: summaries.callToAction,
    destination: summaries.destination,
    warnings: summaries.warnings,
  })
}

function parseDerivedSummary(
  value: unknown,
): ComparisonDerivedSummary | 'unavailable' | null {
  if (value === 'unavailable') {
    return 'unavailable'
  }
  if (
    !isRecordWithExactKeys(value, ['status', 'summary']) ||
    value.status !== 'recorded' ||
    typeof value.summary !== 'string' ||
    value.summary.length === 0 ||
    value.summary.length > 512
  ) {
    return null
  }
  return Object.freeze({ status: 'recorded', summary: value.summary })
}

function parseElementChanges(
  value: unknown,
): readonly ComparisonElementChange[] | null {
  if (!Array.isArray(value)) {
    return null
  }
  const changes: ComparisonElementChange[] = []
  const elementIds = new Set<string>()
  let previousPhase = -1
  let previousIndex = -1
  for (const candidate of value) {
    if (
      !isRecordWithExactKeys(candidate, [
        'kind',
        'elementId',
        'previousIndex',
        'currentIndex',
      ]) ||
      !elementChangeKinds.has(candidate.kind as ComparisonElementChangeKind) ||
      !isEntityId(candidate.elementId) ||
      elementIds.has(candidate.elementId) ||
      !isNullOrNonnegativeInt32(candidate.previousIndex) ||
      !isNullOrNonnegativeInt32(candidate.currentIndex) ||
      !indicesMatchChangeKind(
        candidate.kind as ComparisonElementChangeKind,
        candidate.previousIndex,
        candidate.currentIndex,
      )
    ) {
      return null
    }
    const phase = changePhase(candidate.kind as ComparisonElementChangeKind)
    const phaseIndex = phase === 0
      ? candidate.previousIndex!
      : candidate.currentIndex!
    if (
      phase < previousPhase ||
      (phase === previousPhase && phaseIndex <= previousIndex)
    ) {
      return null
    }
    previousPhase = phase
    previousIndex = phaseIndex
    elementIds.add(candidate.elementId)
    changes.push(Object.freeze({
      kind: candidate.kind as ComparisonElementChangeKind,
      elementId: candidate.elementId,
      previousIndex: candidate.previousIndex,
      currentIndex: candidate.currentIndex,
    }))
  }
  return Object.freeze(changes)
}

function parseAdoptionChanges(
  value: unknown,
  elementChanges: readonly ComparisonElementChange[],
): readonly ComparisonAdoptionChange[] | null {
  if (!Array.isArray(value)) {
    return null
  }
  const changes: ComparisonAdoptionChange[] = []
  const elementIds = new Set<string>()
  const changeIndexByElementId = new Map(
    elementChanges.map((change, index) => [change.elementId, index]),
  )
  let previousChangeIndex = -1
  for (const candidate of value) {
    if (
      !isRecordWithExactKeys(candidate, [
        'kind',
        'elementId',
        'previousSuggestionId',
        'currentSuggestionId',
      ]) ||
      !adoptionChangeKinds.has(candidate.kind as ComparisonAdoptionChangeKind) ||
      !isEntityId(candidate.elementId) ||
      elementIds.has(candidate.elementId) ||
      !isNullOrEntityId(candidate.previousSuggestionId) ||
      !isNullOrEntityId(candidate.currentSuggestionId) ||
      !suggestionIdsMatchAdoptionKind(
        candidate.kind as ComparisonAdoptionChangeKind,
        candidate.previousSuggestionId,
        candidate.currentSuggestionId,
      )
    ) {
      return null
    }
    const elementChangeIndex = changeIndexByElementId.get(candidate.elementId)
    const elementChange = elementChanges[elementChangeIndex ?? -1]
    if (
      elementChangeIndex === undefined ||
      elementChangeIndex <= previousChangeIndex ||
      elementChange === undefined ||
      !adoptionKindMatchesElementChange(
        candidate.kind as ComparisonAdoptionChangeKind,
        elementChange.kind,
      )
    ) {
      return null
    }
    previousChangeIndex = elementChangeIndex
    elementIds.add(candidate.elementId)
    changes.push(Object.freeze({
      kind: candidate.kind as ComparisonAdoptionChangeKind,
      elementId: candidate.elementId,
      previousSuggestionId: candidate.previousSuggestionId,
      currentSuggestionId: candidate.currentSuggestionId,
    }))
  }
  return Object.freeze(changes)
}

function parseTimeRange(
  value: unknown,
  allowNegativeStart: boolean,
): ComparisonTimeRange | null {
  if (!isRecordWithExactKeys(value, ['start', 'duration'])) {
    return null
  }
  const start = parseRationalTime(value.start, allowNegativeStart, true)
  const duration = parseRationalTime(value.duration, false, false)
  return start === null ||
    duration === null ||
    !rationalSumIsRepresentable(start, duration)
    ? null
    : Object.freeze({ start, duration })
}

function parseRationalTime(
  value: unknown,
  allowNegative: boolean,
  allowZero: boolean,
): ComparisonRationalTime | null {
  if (
    !isRecordWithExactKeys(value, ['value', 'rate']) ||
    !isCanonicalSignedInt64(value.value) ||
    !isCanonicalPositiveInt64(value.rate)
  ) {
    return null
  }
  const numerator = BigInt(value.value)
  const denominator = BigInt(value.rate)
  if (
    (!allowNegative && numerator < 0n) ||
    (!allowZero && numerator === 0n) ||
    greatestCommonDivisor(numerator, denominator) !== 1n
  ) {
    return null
  }
  return Object.freeze({ value: value.value, rate: value.rate })
}

function changePhase(kind: ComparisonElementChangeKind): number {
  if (kind === 'removed') return 0
  if (kind === 'added') return 1
  return 2
}

function indicesMatchChangeKind(
  kind: ComparisonElementChangeKind,
  previousIndex: number | null,
  currentIndex: number | null,
): boolean {
  switch (kind) {
    case 'added':
      return previousIndex === null && currentIndex !== null
    case 'removed':
      return previousIndex !== null && currentIndex === null
    case 'moved':
      return previousIndex !== null &&
        currentIndex !== null &&
        previousIndex !== currentIndex
    case 'modified':
      return previousIndex !== null && currentIndex !== null
  }
}

function suggestionIdsMatchAdoptionKind(
  kind: ComparisonAdoptionChangeKind,
  previousId: string | null,
  currentId: string | null,
): boolean {
  switch (kind) {
    case 'adopted':
      return previousId === null && currentId !== null
    case 'removed':
      return previousId !== null && currentId === null
    case 'replaced':
      return previousId !== null && currentId !== null && previousId !== currentId
  }
}

function adoptionKindMatchesElementChange(
  adoptionKind: ComparisonAdoptionChangeKind,
  elementKind: ComparisonElementChangeKind,
): boolean {
  if (adoptionKind === 'adopted') {
    return elementKind === 'added' || elementKind === 'modified'
  }
  if (adoptionKind === 'removed') {
    return elementKind === 'removed' || elementKind === 'modified'
  }
  return elementKind === 'modified'
}

function rationalEquals(
  left: ComparisonRationalTime,
  right: ComparisonRationalTime,
): boolean {
  return BigInt(left.value) * BigInt(right.rate) ===
    BigInt(right.value) * BigInt(left.rate)
}

function rationalSumIsRepresentable(
  left: ComparisonRationalTime,
  right: ComparisonRationalTime,
): boolean {
  const leftValue = BigInt(left.value)
  const leftRate = BigInt(left.rate)
  const rightValue = BigInt(right.value)
  const rightRate = BigInt(right.rate)
  const numerator = leftValue * rightRate + rightValue * leftRate
  const rate = leftRate * rightRate
  const divisor = greatestCommonDivisor(numerator, rate)
  const reducedValue = numerator / divisor
  const reducedRate = rate / divisor
  return reducedValue >= minInt64 &&
    reducedValue <= maxInt64 &&
    reducedRate <= maxInt64
}

function rationalSumIsAtMost(
  left: ComparisonRationalTime,
  right: ComparisonRationalTime,
  limit: ComparisonRationalTime,
): boolean {
  const leftValue = BigInt(left.value)
  const leftRate = BigInt(left.rate)
  const rightValue = BigInt(right.value)
  const rightRate = BigInt(right.rate)
  const limitValue = BigInt(limit.value)
  const limitRate = BigInt(limit.rate)
  return (leftValue * rightRate + rightValue * leftRate) * limitRate <=
    limitValue * leftRate * rightRate
}

function greatestCommonDivisor(left: bigint, right: bigint): bigint {
  let a = left < 0n ? -left : left
  let b = right
  while (b !== 0n) {
    const remainder = a % b
    a = b
    b = remainder
  }
  return a
}

function requireVersionTarget(
  versionId: string,
  versionDigest: string,
  description: string,
): void {
  requireEntityId(versionId, `${description} ID`)
  if (!digestPattern.test(versionDigest)) {
    throw new Error(`${description} digest must be 64 lowercase hexadecimal characters.`)
  }
}

function requireEntityId(value: string, description: string): void {
  if (!entityIdPattern.test(value)) {
    throw new Error(`${description} must be a non-empty lowercase UUID.`)
  }
}

function isEntityId(value: unknown): value is string {
  return typeof value === 'string' && entityIdPattern.test(value)
}

function isNullOrEntityId(value: unknown): value is string | null {
  return value === null || isEntityId(value)
}

function isContentDigest(value: unknown): value is string {
  return typeof value === 'string' && digestPattern.test(value)
}

function isCanonicalSignedInt64(value: unknown): value is string {
  if (typeof value !== 'string' || !signedInt64Pattern.test(value)) {
    return false
  }
  const parsed = BigInt(value)
  return parsed >= minInt64 && parsed <= maxInt64
}

function isCanonicalPositiveInt64(value: unknown): value is string {
  return typeof value === 'string' &&
    positiveInt64Pattern.test(value) &&
    BigInt(value) <= maxInt64
}

function isNullOrNonnegativeInt32(value: unknown): value is number | null {
  return value === null || (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= 2_147_483_647
  )
}

function isNativeTimestamp(value: unknown): value is string {
  if (typeof value !== 'string' || !nativeTimestampPattern.test(value)) {
    return false
  }
  const parsed = new Date(value)
  return Number.isFinite(parsed.valueOf()) &&
    parsed.toISOString() === `${value.slice(0, 23)}Z`
}

function isSubstantiveTrimmedString(value: unknown): value is string {
  return typeof value === 'string' &&
    value.length > 0 &&
    value === value.trim() &&
    hasWellFormedUnicode(value)
}

function hasWellFormedUnicode(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const codeUnit = value.charCodeAt(index)
    if (codeUnit >= 0xd800 && codeUnit <= 0xdbff) {
      const next = value.charCodeAt(index + 1)
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false
      index++
    } else if (codeUnit >= 0xdc00 && codeUnit <= 0xdfff) {
      return false
    }
  }
  return true
}

function isRecordWithExactKeys<const TKey extends string>(
  value: unknown,
  expectedKeys: readonly TKey[],
): value is Record<TKey, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false
  }
  const keys = Object.keys(value)
  return keys.length === expectedKeys.length &&
    expectedKeys.every((key) => Object.prototype.hasOwnProperty.call(value, key))
}

function isWithinUtf8Limit(value: unknown, maximumBytes: number): boolean {
  try {
    const serialized = JSON.stringify(value)
    return serialized !== undefined &&
      new TextEncoder().encode(serialized).byteLength <= maximumBytes
  } catch {
    return false
  }
}

const minInt64 = -9_223_372_036_854_775_808n
const maxInt64 = 9_223_372_036_854_775_807n
const entityIdPattern =
  /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const digestPattern = /^[0-9a-f]{64}$/
const signedInt64Pattern = /^(?:0|-?[1-9][0-9]{0,18})$/
const positiveInt64Pattern = /^[1-9][0-9]{0,18}$/
const nativeTimestampPattern =
  /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}\+00:00$/
const featureNames = [
  'openingHook',
  'subtitleStyle',
  'telopCount',
  'transitions',
  'effects',
  'callToAction',
  'destination',
  'warnings',
] as const
const comparisonFailureStatuses = new Set<ComparisonFailureStatus>([
  'forbidden',
  'notFound',
  'staleVersion',
  'invalidComparison',
  'unavailable',
])
const elementChangeKinds = new Set<ComparisonElementChangeKind>([
  'added',
  'removed',
  'moved',
  'modified',
])
const adoptionChangeKinds = new Set<ComparisonAdoptionChangeKind>([
  'adopted',
  'removed',
  'replaced',
])