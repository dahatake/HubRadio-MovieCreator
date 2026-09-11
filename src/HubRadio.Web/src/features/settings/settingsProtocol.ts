export const SETTINGS_METHODS = [
  'settings.read',
  'settings.update',
  'settings.reset',
  'settings.resetAll',
] as const

export const SETTINGS_GLOBAL_METHODS = [
  'settings.global.read',
  'settings.global.update',
  'settings.global.reset',
  'settings.global.resetAll',
] as const
export const SETTINGS_FILE_METHODS = [
  'settings.file.export',
  'settings.file.preview',
  'settings.file.apply',
] as const
export const SETTINGS_WORKSPACE_METHODS = [
  ...SETTINGS_METHODS, ...SETTINGS_GLOBAL_METHODS, ...SETTINGS_FILE_METHODS,
] as const

export const MAX_SETTINGS_REQUEST_UTF8_BYTES = 64 * 1024
export const MAX_SETTINGS_RESPONSE_UTF8_BYTES = 4 * 1024 * 1024

export type SettingsMethod = (typeof SETTINGS_METHODS)[number]
export type SettingsGlobalMethod = (typeof SETTINGS_GLOBAL_METHODS)[number]
export type SettingsFileMethod = (typeof SETTINGS_FILE_METHODS)[number]
export type SettingsWorkspaceMethod = (typeof SETTINGS_WORKSPACE_METHODS)[number]
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
export type SettingsFileStatus =
  | SettingsStatus | 'fileNotFound' | 'profileMismatch' | 'snapshotMismatch'
  | 'overwriteRequired'

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

export interface GlobalSettingsSnapshot extends Omit<SettingsSnapshot, 'projectId'> {
  readonly projectId: null
}
export type AnySettingsSnapshot = SettingsSnapshot | GlobalSettingsSnapshot

export interface GlobalSettingsMutationIdentity {
  readonly expectedRevision: number
  readonly expectedProfileId: string
  readonly expectedProfileVersion: string
  readonly expectedProfileDigest: string
  readonly expectedSnapshotDigest: string
}
interface MutationIdentity extends GlobalSettingsMutationIdentity {
  readonly projectId: string
}
export interface SettingsFileMutationIdentity extends GlobalSettingsMutationIdentity {
  readonly projectId: string | null
}

// Keep these four project methods compatible with the existing bridge client.
export type SettingsRequest =
  | Readonly<{ version: 1; requestId: string; method: 'settings.read'; params: { projectId: string } }>
  | Readonly<{ version: 1; requestId: string; method: 'settings.update'; params: MutationIdentity & { key: string; value: SettingValue } }>
  | Readonly<{ version: 1; requestId: string; method: 'settings.reset'; params: MutationIdentity & { key: string } }>
  | Readonly<{ version: 1; requestId: string; method: 'settings.resetAll'; params: MutationIdentity }>
export type SettingsGlobalRequest =
  | Readonly<{ version: 1; requestId: string; method: 'settings.global.read'; params: Readonly<Record<string, never>> }>
  | Readonly<{ version: 1; requestId: string; method: 'settings.global.update'; params: GlobalSettingsMutationIdentity & { key: string; value: SettingValue } }>
  | Readonly<{ version: 1; requestId: string; method: 'settings.global.reset'; params: GlobalSettingsMutationIdentity & { key: string } }>
  | Readonly<{ version: 1; requestId: string; method: 'settings.global.resetAll'; params: GlobalSettingsMutationIdentity }>
export type SettingsFileRequest =
  | Readonly<{ version: 1; requestId: string; method: 'settings.file.export'; params: SettingsFileMutationIdentity & { expectedFileSha256: string | null } }>
  | Readonly<{ version: 1; requestId: string; method: 'settings.file.preview'; params: SettingsFileMutationIdentity }>
  | Readonly<{ version: 1; requestId: string; method: 'settings.file.apply'; params: SettingsFileMutationIdentity & { expectedFileSha256: string } }>
/** UX-049: use this union for all 11 methods; SettingsRequest stays project-only. */
export type SettingsWorkspaceRequest = SettingsRequest | SettingsGlobalRequest | SettingsFileRequest

export type SettingsResult =
  | Readonly<{ status: 'available'; snapshot: SettingsSnapshot }>
  | Readonly<{ status: Exclude<SettingsStatus, 'available'>; snapshot: null }>
export interface SettingsResponse {
  readonly version: 1
  readonly requestId: string
  readonly method: SettingsMethod
  readonly result: SettingsResult
}
export type SettingsGlobalResult =
  | Readonly<{ status: 'available'; snapshot: GlobalSettingsSnapshot }>
  | Readonly<{ status: Exclude<SettingsStatus, 'available'>; snapshot: null }>
export interface SettingsGlobalResponse {
  readonly version: 1
  readonly requestId: string
  readonly method: SettingsGlobalMethod
  readonly result: SettingsGlobalResult
}
export type SettingsFileResult =
  | Readonly<{ status: 'available'; snapshot: AnySettingsSnapshot; fileSha256: string }>
  | Readonly<{ status: 'overwriteRequired'; snapshot: null; fileSha256: string }>
  | Readonly<{ status: Exclude<SettingsFileStatus, 'available' | 'overwriteRequired'>; snapshot: null; fileSha256: null }>
export interface SettingsFileResponse {
  readonly version: 1
  readonly requestId: string
  readonly method: SettingsFileMethod
  readonly result: SettingsFileResult
}
/** UX-049: pair with parseSettingsWorkspaceResponse; correlate requests in the client. */
export type SettingsWorkspaceResponse = SettingsResponse | SettingsGlobalResponse | SettingsFileResponse

const idPattern = /^(?!00000000-0000-0000-0000-000000000000(?![\s\S]))[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![\s\S])/
const digestPattern = /^[0-9a-f]{64}(?![\s\S])/
const versionPattern = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?![\s\S])/
const timestampPattern =
  /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})\.(\d{7})\+00:00(?![\s\S])/
const keyPattern = /^[a-z][A-Za-z0-9]*(\.[a-z][A-Za-z0-9]*)*(?![\s\S])/
const integerPattern = /^(0|-?[1-9][0-9]*)(?![\s\S])/
const positiveIntegerPattern = /^[1-9][0-9]*(?![\s\S])/
const ledgerIdPattern = /^[UCR][0-9]{2}(?![\s\S])/
const int64Minimum = -(2n ** 63n)
const int64Maximum = (2n ** 63n) - 1n
const int32Maximum = (2 ** 31) - 1
const groups: readonly SettingsGroup[] = ['projectAndOutput', 'editing', 'subtitlesAndGraphics', 'audioAndAssets', 'privacyAndStorage']
const units: readonly SettingsUnit[] = ['none', 'count', 'milliseconds', 'seconds', 'frames', 'pixels', 'hertz', 'bitsPerSecond', 'percent', 'charactersPerSecond', 'fullWidthCharacters', 'decibels', 'decibelsFullScale', 'loudnessUnitsFullScale']
const evidenceKinds: readonly SettingsEvidenceKind[] = ['specification', 'officialRecommendation', 'peerReviewedFinding', 'practicalGuidance', 'currentImplementation', 'productDecision', 'userDecision']
const failureStatuses: readonly Exclude<SettingsStatus, 'available'>[] = ['forbidden', 'notFound', 'unbound', 'conflict', 'invalid', 'unavailable']
const fileFailureStatuses: readonly Exclude<SettingsFileStatus, 'available' | 'overwriteRequired'>[] = [...failureStatuses, 'fileNotFound', 'profileMismatch', 'snapshotMismatch']

export function createSettingsReadRequest(requestId: string, projectId: string): SettingsRequest {
  requireId(requestId); requireId(projectId)
  return { version: 1, requestId, method: 'settings.read', params: { projectId } }
}
export function createSettingsUpdateRequest(requestId: string, snapshot: SettingsSnapshot, key: string, value: SettingValue): SettingsRequest {
  requireId(requestId)
  if (!definitionAcceptsValue(requireDefinition(snapshot, key), value)) throw new Error('The setting value does not satisfy its closed definition.')
  return { version: 1, requestId, method: 'settings.update', params: { ...identity(snapshot), key, value } }
}
export function createSettingsResetRequest(requestId: string, snapshot: SettingsSnapshot, key: string): SettingsRequest {
  requireId(requestId); requireDefinition(snapshot, key)
  return { version: 1, requestId, method: 'settings.reset', params: { ...identity(snapshot), key } }
}
export function createSettingsResetAllRequest(requestId: string, snapshot: SettingsSnapshot): SettingsRequest {
  requireId(requestId)
  return { version: 1, requestId, method: 'settings.resetAll', params: identity(snapshot) }
}
export function createSettingsGlobalReadRequest(requestId: string): Extract<SettingsGlobalRequest, { method: 'settings.global.read' }> {
  requireId(requestId)
  return { version: 1, requestId, method: 'settings.global.read', params: {} }
}
// Accept the workspace snapshot union, but never reuse a project's identity for global mutations.
export function createSettingsGlobalUpdateRequest(requestId: string, snapshot: AnySettingsSnapshot, key: string, value: SettingValue): Extract<SettingsGlobalRequest, { method: 'settings.global.update' }> {
  requireId(requestId)
  const mutation = globalIdentity(snapshot)
  if (!definitionAcceptsValue(requireDefinition(snapshot, key), value)) throw new Error('The setting value does not satisfy its closed definition.')
  return { version: 1, requestId, method: 'settings.global.update', params: { ...mutation, key, value } }
}
export function createSettingsGlobalResetRequest(requestId: string, snapshot: AnySettingsSnapshot, key: string): Extract<SettingsGlobalRequest, { method: 'settings.global.reset' }> {
  requireId(requestId); requireDefinition(snapshot, key)
  return { version: 1, requestId, method: 'settings.global.reset', params: { ...globalIdentity(snapshot), key } }
}
export function createSettingsGlobalResetAllRequest(requestId: string, snapshot: AnySettingsSnapshot): Extract<SettingsGlobalRequest, { method: 'settings.global.resetAll' }> {
  requireId(requestId)
  return { version: 1, requestId, method: 'settings.global.resetAll', params: globalIdentity(snapshot) }
}
/** Pass null for create-if-absent; an overwrite needs the confirmed original-file hash. */
export function createSettingsFileExportRequest(requestId: string, snapshot: AnySettingsSnapshot, expectedFileSha256: string | null): Extract<SettingsFileRequest, { method: 'settings.file.export' }> {
  requireId(requestId)
  if (expectedFileSha256 !== null) requireDigest(expectedFileSha256)
  return { version: 1, requestId, method: 'settings.file.export', params: { ...fileIdentity(snapshot), expectedFileSha256 } }
}
export function createSettingsFilePreviewRequest(requestId: string, snapshot: AnySettingsSnapshot): Extract<SettingsFileRequest, { method: 'settings.file.preview' }> {
  requireId(requestId)
  return { version: 1, requestId, method: 'settings.file.preview', params: fileIdentity(snapshot) }
}
/** Use the saved target snapshot, not preview candidate values, and the preview's fileSha256. */
export function createSettingsFileApplyRequest(requestId: string, snapshot: AnySettingsSnapshot, expectedFileSha256: string): Extract<SettingsFileRequest, { method: 'settings.file.apply' }> {
  requireId(requestId); requireDigest(expectedFileSha256)
  return { version: 1, requestId, method: 'settings.file.apply', params: { ...fileIdentity(snapshot), expectedFileSha256 } }
}
export function isSettingsRequestWithinLimit(request: SettingsWorkspaceRequest): boolean {
  return withinUtf8Limit(request, MAX_SETTINGS_REQUEST_UTF8_BYTES)
}
function identity(snapshot: SettingsSnapshot): MutationIdentity {
  requireId(snapshot.projectId)
  return { projectId: snapshot.projectId, ...expectedIdentity(snapshot) }
}
function globalIdentity(snapshot: AnySettingsSnapshot): GlobalSettingsMutationIdentity {
  if (snapshot.projectId !== null) throw new Error('A global settings snapshot is required.')
  return expectedIdentity(snapshot)
}
function fileIdentity(snapshot: AnySettingsSnapshot): SettingsFileMutationIdentity {
  if (snapshot.projectId !== null) requireId(snapshot.projectId)
  return { projectId: snapshot.projectId, ...expectedIdentity(snapshot) }
}
function expectedIdentity(snapshot: AnySettingsSnapshot): GlobalSettingsMutationIdentity {
  requireId(snapshot.profileId); requireDigest(snapshot.profileDigest); requireDigest(snapshot.snapshotDigest)
  if (!nonnegativeInt32(snapshot.revision) || !isProfileVersion(snapshot.profileVersion)) throw new Error('A canonical settings revision and profile version are required.')
  return { expectedRevision: snapshot.revision, expectedProfileId: snapshot.profileId, expectedProfileVersion: snapshot.profileVersion, expectedProfileDigest: snapshot.profileDigest, expectedSnapshotDigest: snapshot.snapshotDigest }
}
function requireDefinition(snapshot: AnySettingsSnapshot, key: string): SettingDefinition {
  requireKey(key)
  const definition = snapshot.definitions.find(item => item.key === key)
  if (definition === undefined) throw new Error('The setting key is not part of the closed profile.')
  return definition
}

export function parseSettingsResponse(value: unknown): SettingsResponse | null {
  if (!responseEnvelope(value) || !oneOf(value.method, SETTINGS_METHODS)) return null
  const result = parseResult(value.result)
  return result === null ? null : { version: 1, requestId: value.requestId, method: value.method, result }
}
export function parseSettingsGlobalResponse(value: unknown): SettingsGlobalResponse | null {
  if (!responseEnvelope(value) || !oneOf(value.method, SETTINGS_GLOBAL_METHODS)) return null
  const result = parseGlobalResult(value.result)
  return result === null ? null : { version: 1, requestId: value.requestId, method: value.method, result }
}
export function parseSettingsFileResponse(value: unknown): SettingsFileResponse | null {
  if (!responseEnvelope(value) || !oneOf(value.method, SETTINGS_FILE_METHODS)) return null
  const result = parseFileResult(value.result)
  return result === null ? null : { version: 1, requestId: value.requestId, method: value.method, result }
}
/** All 11 methods. UX-049 owns requestId/method/scope/OCC correlation and stale-response rejection. */
export function parseSettingsWorkspaceResponse(value: unknown): SettingsWorkspaceResponse | null {
  if (!object(value)) return null
  if (oneOf(value.method, SETTINGS_METHODS)) return parseSettingsResponse(value)
  if (oneOf(value.method, SETTINGS_GLOBAL_METHODS)) return parseSettingsGlobalResponse(value)
  if (oneOf(value.method, SETTINGS_FILE_METHODS)) return parseSettingsFileResponse(value)
  return null
}
function responseEnvelope(value: unknown): value is Readonly<{ version: 1; requestId: string; method: unknown; result: unknown }> {
  return exactObject(value, ['version', 'requestId', 'method', 'result']) && value.version === 1 && isId(value.requestId) && withinUtf8Limit(value, MAX_SETTINGS_RESPONSE_UTF8_BYTES)
}
function parseResult(value: unknown): SettingsResult | null {
  if (!exactObject(value, ['status', 'snapshot']) || typeof value.status !== 'string') return null
  if (value.status !== 'available') return oneOf(value.status, failureStatuses) && value.snapshot === null ? { status: value.status, snapshot: null } : null
  const snapshot = parseSnapshot(value.snapshot)
  return snapshot === null || snapshot.projectId === null ? null : { status: 'available', snapshot }
}
function parseGlobalResult(value: unknown): SettingsGlobalResult | null {
  if (!exactObject(value, ['status', 'snapshot']) || typeof value.status !== 'string') return null
  if (value.status !== 'available') return oneOf(value.status, failureStatuses) && value.snapshot === null ? { status: value.status, snapshot: null } : null
  const snapshot = parseSnapshot(value.snapshot)
  return snapshot === null || snapshot.projectId !== null ? null : { status: 'available', snapshot }
}
function parseFileResult(value: unknown): SettingsFileResult | null {
  if (!exactObject(value, ['status', 'snapshot', 'fileSha256'])) return null
  if (value.status === 'available') {
    if (!isDigest(value.fileSha256)) return null
    const snapshot = parseSnapshot(value.snapshot)
    return snapshot === null ? null : { status: 'available', snapshot, fileSha256: value.fileSha256 }
  }
  if (value.snapshot !== null) return null
  if (value.status === 'overwriteRequired') return isDigest(value.fileSha256) ? { status: 'overwriteRequired', snapshot: null, fileSha256: value.fileSha256 } : null
  return oneOf(value.status, fileFailureStatuses) && value.fileSha256 === null ? { status: value.status, snapshot: null, fileSha256: null } : null
}
function parseSnapshot(value: unknown): AnySettingsSnapshot | null {
  const keys = ['projectId','profileId','profileName','profileVersion','profileDigest','initialSnapshotDigest','snapshotDigest','revision','updatedAt','definitionCount','definitions','initialValues','values']
  if (!exactObject(value, keys) || (value.projectId !== null && !isId(value.projectId)) || !isId(value.profileId) || !nonblank(value.profileName) || !isProfileVersion(value.profileVersion) || !isDigest(value.profileDigest) || !isDigest(value.initialSnapshotDigest) || !isDigest(value.snapshotDigest) || !nonnegativeInt32(value.revision) || !isCanonicalTimestamp(value.updatedAt) || !nonnegativeInt32(value.definitionCount) || !Array.isArray(value.definitions) || !Array.isArray(value.initialValues) || !Array.isArray(value.values)) return null
  const definitions = value.definitions.map(parseDefinition)
  const initialValues = value.initialValues.map(parseSelection)
  const values = value.values.map(parseSelection)
  if (definitions.length === 0 || initialValues.length === 0 || values.length === 0 || !definitions.every(present) || !initialValues.every(present) || !values.every(present) || value.definitionCount !== definitions.length) return null
  const definitionKeys = definitions.map(item => item.key)
  if (!unique(definitionKeys) || !sameKeys(definitionKeys, initialValues.map(item => item.key)) || !sameKeys(definitionKeys, values.map(item => item.key))) return null
  for (const definition of definitions) {
    const current = values.find(item => item.key === definition.key)
    const initial = initialValues.find(item => item.key === definition.key)
    if (current === undefined || initial === undefined || !definitionAcceptsValue(definition, current.value) || !definitionAcceptsValue(definition, initial.value)) return null
    // Parsed values have a fixed property order; global reset targets are profile defaults.
    if (value.projectId === null && JSON.stringify(initial.value) !== JSON.stringify(definition.defaultValue)) return null
  }
  return { projectId: value.projectId, profileId: value.profileId, profileName: value.profileName, profileVersion: value.profileVersion, profileDigest: value.profileDigest, initialSnapshotDigest: value.initialSnapshotDigest, snapshotDigest: value.snapshotDigest, revision: value.revision, updatedAt: value.updatedAt, definitionCount: value.definitionCount, definitions, initialValues, values }
}
function parseDefinition(value: unknown): SettingDefinition | null {
  const keys = ['key','group','isAdvanced','displayName','description','unit','defaultValue','minimum','maximum','allowedTextValues','evidence']
  if (!exactObject(value, keys) || !isKey(value.key) || !oneOf(value.group, groups) || typeof value.isAdvanced !== 'boolean' || !nonblank(value.displayName) || !nonblank(value.description) || !oneOf(value.unit, units) || !Array.isArray(value.allowedTextValues) || !value.allowedTextValues.every(nonblank) || !unique(value.allowedTextValues) || !Array.isArray(value.evidence) || value.evidence.length === 0) return null
  const defaultValue = parseValue(value.defaultValue)
  const minimum = value.minimum === null ? null : parseValue(value.minimum)
  const maximum = value.maximum === null ? null : parseValue(value.maximum)
  const evidence = value.evidence.map(parseEvidence)
  if (defaultValue === null || (value.minimum !== null && minimum === null) || (value.maximum !== null && maximum === null) || !evidence.every(present)) return null
  const definition = { key: value.key, group: value.group, isAdvanced: value.isAdvanced, displayName: value.displayName, description: value.description, unit: value.unit, defaultValue, minimum, maximum, allowedTextValues: value.allowedTextValues, evidence }
  return validDefinition(definition) ? definition : null
}
function parseEvidence(value: unknown): SettingEvidence | null {
  if (!exactObject(value, ['kind','sourceId']) || !oneOf(value.kind, evidenceKinds) || !validEvidencePair(value.kind, value.sourceId)) return null
  return { kind: value.kind, sourceId: value.sourceId }
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
  if (typeof value !== 'string' || value.length > 20 || !(positive ? positiveIntegerPattern : integerPattern).test(value)) return false
  const parsed = BigInt(value)
  return parsed >= (positive ? 1n : int64Minimum) && parsed <= int64Maximum
}
function isProfileVersion(value: unknown): value is string {
  return typeof value === 'string' && versionPattern.test(value) && value.split('.').every(component => component.length <= 10 && BigInt(component) <= BigInt(int32Maximum))
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
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false
      index += 1
    } else if (code >= 0xdc00 && code <= 0xdfff) return false
  }
  return true
}
function withinUtf8Limit(value: unknown, maximum: number): boolean {
  try {
    const json = JSON.stringify(value)
    return typeof json === 'string' && new TextEncoder().encode(json).byteLength <= maximum
  } catch {
    return false
  }
}
function oneOf<T extends string>(value: unknown, choices: readonly T[]): value is T { return choices.some(choice => choice === value) }
function present<T>(value: T | null): value is T { return value !== null }
function requireId(value: string): void { if (!isId(value)) throw new Error('A canonical ID is required.') }
function requireDigest(value: string): void { if (!isDigest(value)) throw new Error('A canonical SHA-256 digest is required.') }
function requireKey(value: string): void { if (!isKey(value)) throw new Error('A canonical settings key is required.') }
function isId(value: unknown): value is string { return typeof value === 'string' && idPattern.test(value) }
function isDigest(value: unknown): value is string { return typeof value === 'string' && digestPattern.test(value) }
function isKey(value: unknown): value is string { return typeof value === 'string' && value.length <= 128 && keyPattern.test(value) }
function object(value: unknown): value is Record<string, unknown> { return typeof value === 'object' && value !== null && !Array.isArray(value) }
function exactObject(value: unknown, keys: readonly string[]): value is Record<string, unknown> { return object(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key)) }
function nonempty(value: unknown): value is string { return typeof value === 'string' && value.length > 0 }
function nonblank(value: unknown): value is string { return nonempty(value) && !/^\s|\s$/u.test(value) }
function nonnegativeInt32(value: unknown): value is number { return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= int32Maximum }
function unique(values: readonly string[]): boolean { return new Set(values).size === values.length }
function sameKeys(left: readonly string[], right: readonly string[]): boolean { return left.length === right.length && unique(right) && left.every(key => right.includes(key)) }
