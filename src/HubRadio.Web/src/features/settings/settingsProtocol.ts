export const SETTINGS_METHODS = [
  'settings.read',
  'settings.update',
  'settings.reset',
  'settings.resetAll',
] as const

export const MAX_SETTINGS_REQUEST_UTF8_BYTES = 64 * 1024

export type SettingsMethod = (typeof SETTINGS_METHODS)[number]
export type SettingsGroup =
  | 'projectAndOutput'
  | 'editing'
  | 'subtitlesAndGraphics'
  | 'audioAndAssets'
  | 'privacyAndStorage'
export type SettingsUnit =
  | 'none' | 'count' | 'milliseconds' | 'seconds' | 'frames' | 'pixels'
  | 'hertz' | 'bitsPerSecond' | 'percent' | 'charactersPerSecond'
  | 'fullWidthCharacters' | 'decibels' | 'decibelsFullScale'
  | 'loudnessUnitsFullScale'
export type SettingsEvidenceKind =
  | 'specification' | 'officialRecommendation' | 'peerReviewedFinding'
  | 'practicalGuidance' | 'currentImplementation' | 'productDecision'
  | 'userDecision'
export type SettingsStatus =
  | 'available' | 'forbidden' | 'notFound' | 'unbound' | 'conflict'
  | 'invalid' | 'unavailable'

export type SettingValue =
  | Readonly<{ kind: 'boolean'; booleanValue: boolean }>
  | Readonly<{ kind: 'integer'; integerValue: string }>
  | Readonly<{ kind: 'rational'; numerator: string; denominator: string }>
  | Readonly<{ kind: 'text'; textValue: string }>
  | Readonly<{ kind: 'textList'; textListValue: readonly string[] }>

export interface SettingEvidence {
  readonly kind: SettingsEvidenceKind
  readonly sourceId: string
}
export interface SettingDefinition {
  readonly key: string
  readonly group: SettingsGroup
  readonly isAdvanced: boolean
  readonly displayName: string
  readonly description: string
  readonly unit: SettingsUnit
  readonly defaultValue: SettingValue
  readonly minimum: SettingValue | null
  readonly maximum: SettingValue | null
  readonly allowedTextValues: readonly string[]
  readonly evidence: readonly SettingEvidence[]
}
export interface SettingSelection {
  readonly key: string
  readonly value: SettingValue
}
export interface SettingsSnapshot {
  readonly projectId: string
  readonly profileId: string
  readonly profileName: string
  readonly profileVersion: string
  readonly profileDigest: string
  readonly initialSnapshotDigest: string
  readonly snapshotDigest: string
  readonly revision: number
  readonly updatedAt: string
  readonly definitionCount: number
  readonly definitions: readonly SettingDefinition[]
  readonly initialValues: readonly SettingSelection[]
  readonly values: readonly SettingSelection[]
}

interface MutationIdentity {
  readonly projectId: string
  readonly expectedRevision: number
  readonly expectedProfileId: string
  readonly expectedProfileVersion: string
  readonly expectedProfileDigest: string
  readonly expectedSnapshotDigest: string
}
export type SettingsRequest =
  | Readonly<{ version: 1; requestId: string; method: 'settings.read'; params: { projectId: string } }>
  | Readonly<{ version: 1; requestId: string; method: 'settings.update'; params: MutationIdentity & { key: string; value: SettingValue } }>
  | Readonly<{ version: 1; requestId: string; method: 'settings.reset'; params: MutationIdentity & { key: string } }>
  | Readonly<{ version: 1; requestId: string; method: 'settings.resetAll'; params: MutationIdentity }>
export type SettingsResult =
  | Readonly<{ status: 'available'; snapshot: SettingsSnapshot }>
  | Readonly<{ status: Exclude<SettingsStatus, 'available'>; snapshot: null }>
export interface SettingsResponse {
  readonly version: 1
  readonly requestId: string
  readonly method: SettingsMethod
  readonly result: SettingsResult
}

const idPattern = /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const digestPattern = /^[0-9a-f]{64}$/
const versionPattern = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/
const timestampPattern =
  /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})\.(\d{7})\+00:00$/
const keyPattern = /^[a-z][A-Za-z0-9]*(\.[a-z][A-Za-z0-9]*)*$/
const integerPattern = /^(0|-?[1-9][0-9]*)$/
const positiveIntegerPattern = /^[1-9][0-9]*$/
const ledgerIdPattern = /^[UCR][0-9]{2}$/
const int64Minimum = -(2n ** 63n)
const int64Maximum = (2n ** 63n) - 1n
const int32Maximum = (2 ** 31) - 1
const groups = new Set<SettingsGroup>(['projectAndOutput', 'editing', 'subtitlesAndGraphics', 'audioAndAssets', 'privacyAndStorage'])
const units = new Set<SettingsUnit>(['none', 'count', 'milliseconds', 'seconds', 'frames', 'pixels', 'hertz', 'bitsPerSecond', 'percent', 'charactersPerSecond', 'fullWidthCharacters', 'decibels', 'decibelsFullScale', 'loudnessUnitsFullScale'])
const evidenceKinds = new Set<SettingsEvidenceKind>(['specification', 'officialRecommendation', 'peerReviewedFinding', 'practicalGuidance', 'currentImplementation', 'productDecision', 'userDecision'])
const failureStatuses = new Set<Exclude<SettingsStatus, 'available'>>(['forbidden', 'notFound', 'unbound', 'conflict', 'invalid', 'unavailable'])

export function createSettingsReadRequest(requestId: string, projectId: string): SettingsRequest {
  requireId(requestId); requireId(projectId)
  return { version: 1, requestId, method: 'settings.read', params: { projectId } }
}
export function createSettingsUpdateRequest(requestId: string, snapshot: SettingsSnapshot, key: string, value: SettingValue): SettingsRequest {
  requireId(requestId); requireKey(key)
  const definition = snapshot.definitions.find(item => item.key === key)
  if (definition === undefined || !definitionAcceptsValue(definition, value)) throw new Error('The setting value does not satisfy its closed definition.')
  return { version: 1, requestId, method: 'settings.update', params: { ...identity(snapshot), key, value } }
}
export function createSettingsResetRequest(requestId: string, snapshot: SettingsSnapshot, key: string): SettingsRequest {
  requireId(requestId); requireKey(key)
  if (!snapshot.definitions.some(item => item.key === key)) throw new Error('The setting key is not part of the closed profile.')
  return { version: 1, requestId, method: 'settings.reset', params: { ...identity(snapshot), key } }
}
export function createSettingsResetAllRequest(requestId: string, snapshot: SettingsSnapshot): SettingsRequest {
  requireId(requestId)
  return { version: 1, requestId, method: 'settings.resetAll', params: identity(snapshot) }
}
export function isSettingsRequestWithinLimit(request: SettingsRequest): boolean {
  return new TextEncoder().encode(JSON.stringify(request)).byteLength <= MAX_SETTINGS_REQUEST_UTF8_BYTES
}
function identity(snapshot: SettingsSnapshot): MutationIdentity {
  return { projectId: snapshot.projectId, expectedRevision: snapshot.revision, expectedProfileId: snapshot.profileId, expectedProfileVersion: snapshot.profileVersion, expectedProfileDigest: snapshot.profileDigest, expectedSnapshotDigest: snapshot.snapshotDigest }
}

export function parseSettingsResponse(value: unknown): SettingsResponse | null {
  if (!exactObject(value, ['version', 'requestId', 'method', 'result']) || value.version !== 1 || !isId(value.requestId) || !isMethod(value.method)) return null
  const result = parseResult(value.result)
  return result === null ? null : { version: 1, requestId: value.requestId, method: value.method, result }
}
function parseResult(value: unknown): SettingsResult | null {
  if (!exactObject(value, ['status', 'snapshot']) || typeof value.status !== 'string') return null
  if (value.status !== 'available') return failureStatuses.has(value.status as never) && value.snapshot === null ? { status: value.status as Exclude<SettingsStatus, 'available'>, snapshot: null } : null
  const snapshot = parseSnapshot(value.snapshot)
  return snapshot === null ? null : { status: 'available', snapshot }
}
function parseSnapshot(value: unknown): SettingsSnapshot | null {
  const keys = ['projectId','profileId','profileName','profileVersion','profileDigest','initialSnapshotDigest','snapshotDigest','revision','updatedAt','definitionCount','definitions','initialValues','values']
  if (!exactObject(value, keys) || !isId(value.projectId) || !isId(value.profileId) || !nonblank(value.profileName) || typeof value.profileVersion !== 'string' || !versionPattern.test(value.profileVersion) || !isDigest(value.profileDigest) || !isDigest(value.initialSnapshotDigest) || !isDigest(value.snapshotDigest) || !nonnegativeInt32(value.revision) || !isCanonicalTimestamp(value.updatedAt) || !nonnegativeInt32(value.definitionCount) || !Array.isArray(value.definitions) || !Array.isArray(value.initialValues) || !Array.isArray(value.values)) return null
  const definitions = value.definitions.map(parseDefinition)
  const initialValues = value.initialValues.map(parseSelection)
  const values = value.values.map(parseSelection)
  if (definitions.length === 0 || initialValues.length === 0 || values.length === 0 || definitions.some(item => item === null) || initialValues.some(item => item === null) || values.some(item => item === null) || value.definitionCount !== definitions.length) return null
  const definitionKeys = definitions.map(item => item!.key)
  if (!unique(definitionKeys) || !sameKeys(definitionKeys, initialValues.map(item => item!.key)) || !sameKeys(definitionKeys, values.map(item => item!.key))) return null
  for (let index = 0; index < definitions.length; index += 1) {
    const definition = definitions[index]!
    const current = values.find(item => item!.key === definition.key)!
    const initial = initialValues.find(item => item!.key === definition.key)!
    if (!definitionAcceptsValue(definition, current.value) || !definitionAcceptsValue(definition, initial.value)) return null
  }
  return { projectId: value.projectId, profileId: value.profileId, profileName: value.profileName, profileVersion: value.profileVersion, profileDigest: value.profileDigest, initialSnapshotDigest: value.initialSnapshotDigest, snapshotDigest: value.snapshotDigest, revision: value.revision, updatedAt: value.updatedAt, definitionCount: value.definitionCount, definitions: definitions as SettingDefinition[], initialValues: initialValues as SettingSelection[], values: values as SettingSelection[] }
}
function parseDefinition(value: unknown): SettingDefinition | null {
  const keys = ['key','group','isAdvanced','displayName','description','unit','defaultValue','minimum','maximum','allowedTextValues','evidence']
  if (!exactObject(value, keys) || !isKey(value.key) || !groups.has(value.group as never) || typeof value.isAdvanced !== 'boolean' || !nonblank(value.displayName) || !nonblank(value.description) || !units.has(value.unit as never) || !Array.isArray(value.allowedTextValues) || !value.allowedTextValues.every(nonblank) || !unique(value.allowedTextValues) || !Array.isArray(value.evidence) || value.evidence.length === 0) return null
  const defaultValue = parseValue(value.defaultValue)
  const minimum = value.minimum === null ? null : parseValue(value.minimum)
  const maximum = value.maximum === null ? null : parseValue(value.maximum)
  const evidence = value.evidence.map(parseEvidence)
  if (defaultValue === null || (value.minimum !== null && minimum === null) || (value.maximum !== null && maximum === null) || evidence.some(item => item === null)) return null
  const definition = { key: value.key, group: value.group as SettingsGroup, isAdvanced: value.isAdvanced, displayName: value.displayName, description: value.description, unit: value.unit as SettingsUnit, defaultValue, minimum, maximum, allowedTextValues: value.allowedTextValues, evidence: evidence as SettingEvidence[] }
  return validDefinition(definition) ? definition : null
}
function parseEvidence(value: unknown): SettingEvidence | null {
  if (!exactObject(value, ['kind','sourceId']) || !evidenceKinds.has(value.kind as never) || !validEvidencePair(value.kind as SettingsEvidenceKind, value.sourceId)) return null
  return { kind: value.kind as SettingsEvidenceKind, sourceId: value.sourceId }
}
function parseSelection(value: unknown): SettingSelection | null {
  if (!exactObject(value, ['key','value']) || !isKey(value.key)) return null
  const parsed = parseValue(value.value)
  return parsed === null ? null : { key: value.key, value: parsed }
}
export function parseValue(value: unknown): SettingValue | null {
  if (!object(value) || typeof value.kind !== 'string') return null
  if (value.kind === 'boolean' && exactObject(value, ['kind','booleanValue']) && typeof value.booleanValue === 'boolean') return { kind: 'boolean', booleanValue: value.booleanValue }
  if (value.kind === 'integer' && exactObject(value, ['kind','integerValue']) && isInt64String(value.integerValue, false)) return { kind: 'integer', integerValue: value.integerValue }
  if (value.kind === 'rational' && exactObject(value, ['kind','numerator','denominator']) && isInt64String(value.numerator, false) && isInt64String(value.denominator, true)) return { kind: 'rational', numerator: value.numerator, denominator: value.denominator }
  if (value.kind === 'text' && exactObject(value, ['kind','textValue']) && boundedText(value.textValue)) return { kind: 'text', textValue: value.textValue }
  if (value.kind === 'textList' && exactObject(value, ['kind','textListValue']) && Array.isArray(value.textListValue) && value.textListValue.length <= 64 && value.textListValue.every(boundedText) && unique(value.textListValue)) return { kind: 'textList', textListValue: value.textListValue }
  return null
}
function validEvidencePair(kind: SettingsEvidenceKind, sourceId: unknown): sourceId is string {
  if (typeof sourceId !== 'string') return false
  if (sourceId === 'productDecision') return kind === 'productDecision'
  if (!ledgerIdPattern.test(sourceId)) return false
  if (sourceId[0] === 'U') return kind === 'userDecision'
  if (sourceId[0] === 'C') return kind === 'currentImplementation'
  return kind === 'specification' || kind === 'officialRecommendation' || kind === 'peerReviewedFinding' || kind === 'practicalGuidance'
}
function validDefinition(definition: SettingDefinition): boolean {
  const numeric = definition.defaultValue.kind === 'integer' || definition.defaultValue.kind === 'rational'
  if (!numeric && (definition.unit !== 'none' || definition.minimum !== null || definition.maximum !== null)) return false
  if (definition.allowedTextValues.length > 0 && definition.defaultValue.kind !== 'text') return false
  if (definition.minimum !== null && definition.minimum.kind !== definition.defaultValue.kind) return false
  if (definition.maximum !== null && definition.maximum.kind !== definition.defaultValue.kind) return false
  return definitionAcceptsValue(definition, definition.defaultValue)
}
function definitionAcceptsValue(definition: SettingDefinition, value: SettingValue): boolean {
  if (parseValue(value) === null || value.kind !== definition.defaultValue.kind) return false
  if (value.kind === 'text' && definition.allowedTextValues.length > 0 && !definition.allowedTextValues.includes(value.textValue)) return false
  if (definition.minimum !== null && compareNumeric(value, definition.minimum) < 0) return false
  if (definition.maximum !== null && compareNumeric(value, definition.maximum) > 0) return false
  return true
}
function compareNumeric(left: SettingValue, right: SettingValue): number {
  if (left.kind === 'integer' && right.kind === 'integer') return compareBigInt(BigInt(left.integerValue), BigInt(right.integerValue))
  if (left.kind === 'rational' && right.kind === 'rational') return compareBigInt(BigInt(left.numerator) * BigInt(right.denominator), BigInt(right.numerator) * BigInt(left.denominator))
  throw new Error('Only settings values of the same numeric kind can be compared.')
}
function compareBigInt(left: bigint, right: bigint): number { return left < right ? -1 : left > right ? 1 : 0 }
function isInt64String(value: unknown, positive: boolean): value is string {
  if (typeof value !== 'string' || !(positive ? positiveIntegerPattern : integerPattern).test(value)) return false
  const parsed = BigInt(value)
  return parsed >= (positive ? 1n : int64Minimum) && parsed <= int64Maximum
}
function isCanonicalTimestamp(value: unknown): value is string {
  if (typeof value !== 'string') return false
  const match = timestampPattern.exec(value)
  if (match === null || match[1] === '0000') return false
  const isoMilliseconds = `${value.slice(0, 19)}.${match[7]!.slice(0, 3)}Z`
  const timestamp = new Date(isoMilliseconds)
  return Number.isFinite(timestamp.valueOf()) && timestamp.toISOString() === isoMilliseconds
}
function boundedText(value: unknown): value is string { return nonblank(value) && value.length <= 4096 && hasValidUnicode(value) }
function hasValidUnicode(value: string): boolean {
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index)
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(index + 1)
      if (next < 0xdc00 || next > 0xdfff) return false
      index += 1
    } else if (code >= 0xdc00 && code <= 0xdfff) return false
  }
  return true
}
function isMethod(value: unknown): value is SettingsMethod { return typeof value === 'string' && (SETTINGS_METHODS as readonly string[]).includes(value) }
function requireId(value: string): void { if (!isId(value)) throw new Error('A canonical ID is required.') }
function requireKey(value: string): void { if (!isKey(value)) throw new Error('A canonical settings key is required.') }
function isId(value: unknown): value is string { return typeof value === 'string' && idPattern.test(value) }
function isDigest(value: unknown): value is string { return typeof value === 'string' && digestPattern.test(value) }
function isKey(value: unknown): value is string { return typeof value === 'string' && value.length <= 128 && keyPattern.test(value) }
function object(value: unknown): value is Record<string, unknown> { return typeof value === 'object' && value !== null && !Array.isArray(value) }
function exactObject(value: unknown, keys: readonly string[]): value is Record<string, any> { return object(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key)) }
function nonempty(value: unknown): value is string { return typeof value === 'string' && value.length > 0 }
function nonblank(value: unknown): value is string { return nonempty(value) && !/^\s|\s$/u.test(value) }
function nonnegativeInt32(value: unknown): value is number { return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= int32Maximum }
function unique(values: readonly string[]): boolean { return new Set(values).size === values.length }
function sameKeys(left: readonly string[], right: readonly string[]): boolean { return left.length === right.length && unique(right) && left.every(key => right.includes(key)) }
