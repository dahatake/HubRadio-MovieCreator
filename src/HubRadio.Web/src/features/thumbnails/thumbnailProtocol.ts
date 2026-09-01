import {
  BRIDGE_MAX_THUMBNAIL_REQUEST_UTF8_BYTES,
  BRIDGE_MAX_THUMBNAIL_RESPONSE_UTF8_BYTES,
  BRIDGE_PROTOCOL_VERSION,
} from '../../bridge/protocol'
import type { RationalTime } from '../player/playerProtocol'

export const THUMBNAIL_READ_METHOD = 'thumbnail.read' as const
export const THUMBNAIL_GENERATE_METHOD = 'thumbnail.generate' as const
export const THUMBNAIL_CANCEL_METHOD = 'thumbnail.cancel' as const

const entityIdPattern =
  /^(?!00000000-0000-0000-0000-000000000000(?![\s\S]))[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![\s\S])/
const digestPattern = /^[0-9a-f]{64}(?![\s\S])/
const signedIntegerPattern = /^(?:0|-?[1-9][0-9]{0,18})(?![\s\S])/
const positiveIntegerPattern = /^[1-9][0-9]{0,18}(?![\s\S])/
const timestampPattern =
  /^(?!0000)\d{4}-(0[1-9]|1[0-2])-([0-2]\d|3[0-1])T([0-1]\d|2[0-3]):[0-5]\d:[0-5]\d\.\d{7}\+00:00$/
const thumbnailReferencePattern = new RegExp(
  '^https://player-media\\.appassets\\.example/media/v1/' +
    '((?!00000000-0000-0000-0000-000000000000/)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/' +
    'thumbnail/' +
    '((?!00000000-0000-0000-0000-000000000000/)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/' +
    '([0-9a-f]{64})(?![\\s\\S])',
)
const int64Minimum = -9_223_372_036_854_775_808n
const int64Maximum = 9_223_372_036_854_775_807n

export type ThumbnailReadStatus =
  | 'available'
  | 'forbidden'
  | 'unavailable'
  | 'stale'
  | 'mismatched'

export type ThumbnailGenerateStatus =
  | ThumbnailReadStatus
  | 'invalidTime'
  | 'rightsNotApproved'
  | 'templateUnavailable'
  | 'cancelled'
  | 'conflict'
  | 'failed'

export type ThumbnailCancellationOutcome =
  | 'requested'
  | 'alreadyRequested'
  | 'notRunning'
  | 'notFound'

export interface ThumbnailRights {
  readonly status: 'approved' | 'unconfirmed' | 'rejected'
  readonly externalPublicationAllowed: boolean
}

export interface ThumbnailTemplateSnapshot {
  readonly approvalId: string
  readonly approvalDigest: string
  readonly approvalRole: 'brandApprover'
  readonly approvedAt: string
  readonly templateId: string
  readonly name: string
  readonly templateVersionDigest: string
  readonly renderProfileId: string
  readonly renderProfileDigest: string
  readonly layoutSpecId: string
  readonly layoutSpecDigest: string
  readonly width: number
  readonly height: number
}

export interface ThumbnailSnapshot {
  readonly evidenceDigest: string
  readonly playerEvidenceDigest: string
  readonly projectId: string
  readonly sourceArtifactId: string
  readonly sourceAssetId: string
  readonly sourceContentDigest: string
  readonly sourceRightsDigest: string
  readonly sourcePtsIndexDigest: string
  readonly proxyFrameMapDigest: string
  readonly sourcePresentationStart: RationalTime
  readonly sourcePresentationEnd: RationalTime
  readonly rights: ThumbnailRights
  readonly templates: readonly ThumbnailTemplateSnapshot[]
}

export interface ThumbnailOutputSource {
  readonly artifactId: string
  readonly assetId: string
  readonly contentDigest: string
  readonly ptsIndexDigest: string
  readonly frameMapDigest: string
  readonly rightsStatus: 'approved'
  readonly externalPublicationAllowed: true
}

export interface ThumbnailOutputSelection {
  readonly proxyFrameSequence: number
  readonly sourceFrameSequence: number
  readonly sourcePresentationTime: RationalTime
  readonly selectedAt: string
}

export interface ThumbnailOutputTemplate {
  readonly approvalId: string
  readonly approvalDigest: string
  readonly approvalRole: 'brandApprover'
  readonly approvedAt: string
  readonly templateId: string
  readonly templateVersionDigest: string
  readonly renderProfileId: string
  readonly renderProfileDigest: string
  readonly layoutSpecId: string
  readonly layoutSpecDigest: string
}

export interface ThumbnailOutput {
  readonly generationId: string
  readonly artifactId: string
  readonly projectId: string
  readonly mediaReference: string
  readonly bindingDigest: string
  readonly contentDigest: string
  readonly byteSize: string
  readonly width: number
  readonly height: number
  readonly createdAt: string
  readonly wasAlreadyRegistered: boolean
  readonly source: ThumbnailOutputSource
  readonly selection: ThumbnailOutputSelection
  readonly template: ThumbnailOutputTemplate
  readonly origin: Readonly<{ kind: 'asset'; assetId: string }>
}

export interface ThumbnailReadRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof THUMBNAIL_READ_METHOD
  readonly params: Readonly<{ jobId: string }>
}

export interface ThumbnailGenerateParams {
  readonly jobId: string
  readonly generationId: string
  readonly digest: string
  readonly playerDigest: string
  readonly templateId: string
  readonly proxyFrameSequence: number
}

export interface ThumbnailGenerateRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof THUMBNAIL_GENERATE_METHOD
  readonly params: ThumbnailGenerateParams
}

export interface ThumbnailCancelRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof THUMBNAIL_CANCEL_METHOD
  readonly params: Readonly<{ generationId: string }>
}

export type ThumbnailBridgeRequest =
  | ThumbnailReadRequest
  | ThumbnailGenerateRequest
  | ThumbnailCancelRequest

export type ThumbnailReadResponse =
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof THUMBNAIL_READ_METHOD
      result: Readonly<{ status: 'available'; snapshot: ThumbnailSnapshot }>
    }>
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof THUMBNAIL_READ_METHOD
      result: Readonly<{
        status: Exclude<ThumbnailReadStatus, 'available'>
        snapshot: null
      }>
    }>

export type ThumbnailGenerateResponse =
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof THUMBNAIL_GENERATE_METHOD
      result: Readonly<{ status: 'available'; output: ThumbnailOutput }>
    }>
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof THUMBNAIL_GENERATE_METHOD
      result: Readonly<{
        status: Exclude<ThumbnailGenerateStatus, 'available'>
        output: null
      }>
    }>

export type ThumbnailCancelResponse =
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof THUMBNAIL_CANCEL_METHOD
      result: Readonly<{
        status: 'available'
        outcome: ThumbnailCancellationOutcome
      }>
    }>
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof THUMBNAIL_CANCEL_METHOD
      result: Readonly<{
        status: 'forbidden' | 'unavailable'
        outcome: null
      }>
    }>

export function isCanonicalThumbnailId(value: string): boolean {
  return entityIdPattern.test(value)
}

export function createThumbnailReadRequest(
  requestId: string,
  jobId: string,
): ThumbnailReadRequest {
  assertEntityId(requestId, 'request')
  assertEntityId(jobId, 'job')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: THUMBNAIL_READ_METHOD,
    params: Object.freeze({ jobId }),
  })
}

export function createThumbnailGenerateRequest(
  requestId: string,
  params: ThumbnailGenerateParams,
): ThumbnailGenerateRequest {
  assertEntityId(requestId, 'request')
  assertEntityId(params.jobId, 'job')
  assertEntityId(params.generationId, 'generation')
  assertDigest(params.digest, 'thumbnail evidence')
  assertDigest(params.playerDigest, 'player evidence')
  assertEntityId(params.templateId, 'template')
  if (!isNonnegativeInt32(params.proxyFrameSequence)) {
    throw new Error('A thumbnail proxy frame sequence must be a nonnegative Int32.')
  }

  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: THUMBNAIL_GENERATE_METHOD,
    params: Object.freeze({ ...params }),
  })
}

export function createThumbnailCancelRequest(
  requestId: string,
  generationId: string,
): ThumbnailCancelRequest {
  assertEntityId(requestId, 'request')
  assertEntityId(generationId, 'generation')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: THUMBNAIL_CANCEL_METHOD,
    params: Object.freeze({ generationId }),
  })
}

export function parseThumbnailReadResponse(
  value: unknown,
): ThumbnailReadResponse | null {
  if (!hasResponseEnvelope(value, THUMBNAIL_READ_METHOD)) {
    return null
  }
  if (!hasExactKeys(value.result, ['status', 'snapshot'])) {
    return null
  }

  const result = value.result
  if (result.status === 'available') {
    const snapshot = parseSnapshot(result.snapshot)
    return snapshot === null
      ? null
      : Object.freeze({
          version: BRIDGE_PROTOCOL_VERSION,
          requestId: value.requestId,
          method: THUMBNAIL_READ_METHOD,
          result: Object.freeze({ status: 'available', snapshot }),
        })
  }

  if (
    result.snapshot !== null ||
    !isOneOf(result.status, ['forbidden', 'unavailable', 'stale', 'mismatched'] as const)
  ) {
    return null
  }
  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method: THUMBNAIL_READ_METHOD,
    result: Object.freeze({ status: result.status, snapshot: null }),
  })
}

export function parseThumbnailGenerateResponse(
  value: unknown,
): ThumbnailGenerateResponse | null {
  if (!hasResponseEnvelope(value, THUMBNAIL_GENERATE_METHOD)) {
    return null
  }
  if (!hasExactKeys(value.result, ['status', 'output'])) {
    return null
  }

  const result = value.result
  if (result.status === 'available') {
    const output = parseOutput(result.output)
    return output === null
      ? null
      : Object.freeze({
          version: BRIDGE_PROTOCOL_VERSION,
          requestId: value.requestId,
          method: THUMBNAIL_GENERATE_METHOD,
          result: Object.freeze({ status: 'available', output }),
        })
  }

  if (
    result.output !== null ||
    !isOneOf(result.status, [
      'forbidden',
      'unavailable',
      'stale',
      'mismatched',
      'invalidTime',
      'rightsNotApproved',
      'templateUnavailable',
      'cancelled',
      'conflict',
      'failed',
    ] as const)
  ) {
    return null
  }
  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method: THUMBNAIL_GENERATE_METHOD,
    result: Object.freeze({ status: result.status, output: null }),
  })
}

export function parseThumbnailCancelResponse(
  value: unknown,
): ThumbnailCancelResponse | null {
  if (!hasResponseEnvelope(value, THUMBNAIL_CANCEL_METHOD)) {
    return null
  }
  if (!hasExactKeys(value.result, ['status', 'outcome'])) {
    return null
  }

  const result = value.result
  if (
    result.status === 'available' &&
    isOneOf(result.outcome, [
      'requested',
      'alreadyRequested',
      'notRunning',
      'notFound',
    ] as const)
  ) {
    return Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId: value.requestId,
      method: THUMBNAIL_CANCEL_METHOD,
      result: Object.freeze({ status: 'available', outcome: result.outcome }),
    })
  }
  if (
    result.outcome === null &&
    (result.status === 'forbidden' || result.status === 'unavailable')
  ) {
    return Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId: value.requestId,
      method: THUMBNAIL_CANCEL_METHOD,
      result: Object.freeze({ status: result.status, outcome: null }),
    })
  }
  return null
}

function parseSnapshot(value: unknown): ThumbnailSnapshot | null {
  if (
    !hasExactKeys(value, [
      'evidenceDigest',
      'playerEvidenceDigest',
      'projectId',
      'sourceArtifactId',
      'sourceAssetId',
      'sourceContentDigest',
      'sourceRightsDigest',
      'sourcePtsIndexDigest',
      'proxyFrameMapDigest',
      'sourcePresentationStart',
      'sourcePresentationEnd',
      'rights',
      'templates',
    ]) ||
    !isDigest(value.evidenceDigest) ||
    !isDigest(value.playerEvidenceDigest) ||
    !isEntityId(value.projectId) ||
    !isEntityId(value.sourceArtifactId) ||
    !isEntityId(value.sourceAssetId) ||
    !isDigest(value.sourceContentDigest) ||
    !isDigest(value.sourceRightsDigest) ||
    !isDigest(value.sourcePtsIndexDigest) ||
    !isDigest(value.proxyFrameMapDigest) ||
    !Array.isArray(value.templates)
  ) {
    return null
  }

  const sourcePresentationStart = parseRational(value.sourcePresentationStart)
  const sourcePresentationEnd = parseRational(value.sourcePresentationEnd)
  const rights = parseRights(value.rights)
  if (
    sourcePresentationStart === null ||
    sourcePresentationEnd === null ||
    compareRationals(sourcePresentationStart, sourcePresentationEnd) >= 0 ||
    rights === null
  ) {
    return null
  }

  const templates: ThumbnailTemplateSnapshot[] = []
  for (const candidate of value.templates) {
    const template = parseTemplate(candidate)
    const previous = templates[templates.length - 1]
    if (
      template === null ||
      (previous !== undefined && previous.templateId >= template.templateId)
    ) {
      return null
    }
    templates.push(template)
  }

  return Object.freeze({
    evidenceDigest: value.evidenceDigest,
    playerEvidenceDigest: value.playerEvidenceDigest,
    projectId: value.projectId,
    sourceArtifactId: value.sourceArtifactId,
    sourceAssetId: value.sourceAssetId,
    sourceContentDigest: value.sourceContentDigest,
    sourceRightsDigest: value.sourceRightsDigest,
    sourcePtsIndexDigest: value.sourcePtsIndexDigest,
    proxyFrameMapDigest: value.proxyFrameMapDigest,
    sourcePresentationStart,
    sourcePresentationEnd,
    rights,
    templates: Object.freeze(templates),
  })
}

function parseRights(value: unknown): ThumbnailRights | null {
  if (
    !hasExactKeys(value, ['status', 'externalPublicationAllowed']) ||
    !isOneOf(value.status, ['approved', 'unconfirmed', 'rejected'] as const) ||
    typeof value.externalPublicationAllowed !== 'boolean'
  ) {
    return null
  }
  return Object.freeze({
    status: value.status,
    externalPublicationAllowed: value.externalPublicationAllowed,
  })
}

function parseTemplate(value: unknown): ThumbnailTemplateSnapshot | null {
  if (
    !hasExactKeys(value, [
      'approvalId',
      'approvalDigest',
      'approvalRole',
      'approvedAt',
      'templateId',
      'name',
      'templateVersionDigest',
      'renderProfileId',
      'renderProfileDigest',
      'layoutSpecId',
      'layoutSpecDigest',
      'width',
      'height',
    ]) ||
    !isEntityId(value.approvalId) ||
    !isDigest(value.approvalDigest) ||
    value.approvalRole !== 'brandApprover' ||
    !isCanonicalTimestamp(value.approvedAt) ||
    !isEntityId(value.templateId) ||
    typeof value.name !== 'string' ||
    value.name.length === 0 ||
    !isDigest(value.templateVersionDigest) ||
    !isEntityId(value.renderProfileId) ||
    !isDigest(value.renderProfileDigest) ||
    !isEntityId(value.layoutSpecId) ||
    !isDigest(value.layoutSpecDigest) ||
    !isPositiveInt32(value.width) ||
    !isPositiveInt32(value.height)
  ) {
    return null
  }
  return Object.freeze({
    approvalId: value.approvalId,
    approvalDigest: value.approvalDigest,
    approvalRole: 'brandApprover',
    approvedAt: value.approvedAt,
    templateId: value.templateId,
    name: value.name,
    templateVersionDigest: value.templateVersionDigest,
    renderProfileId: value.renderProfileId,
    renderProfileDigest: value.renderProfileDigest,
    layoutSpecId: value.layoutSpecId,
    layoutSpecDigest: value.layoutSpecDigest,
    width: value.width,
    height: value.height,
  })
}

function parseOutput(value: unknown): ThumbnailOutput | null {
  if (
    !hasExactKeys(value, [
      'generationId',
      'artifactId',
      'projectId',
      'mediaReference',
      'bindingDigest',
      'contentDigest',
      'byteSize',
      'width',
      'height',
      'createdAt',
      'wasAlreadyRegistered',
      'source',
      'selection',
      'template',
      'origin',
    ]) ||
    !isEntityId(value.generationId) ||
    !isEntityId(value.artifactId) ||
    !isEntityId(value.projectId) ||
    typeof value.mediaReference !== 'string' ||
    !isDigest(value.bindingDigest) ||
    !isDigest(value.contentDigest) ||
    !isPositiveInt64String(value.byteSize) ||
    !isPositiveInt32(value.width) ||
    !isPositiveInt32(value.height) ||
    !isCanonicalTimestamp(value.createdAt) ||
    typeof value.wasAlreadyRegistered !== 'boolean'
  ) {
    return null
  }

  const reference = parseThumbnailReference(value.mediaReference)
  const source = parseOutputSource(value.source)
  const selection = parseOutputSelection(value.selection)
  const template = parseOutputTemplate(value.template)
  const origin = parseOutputOrigin(value.origin)
  if (
    reference === null ||
    reference.projectId !== value.projectId ||
    reference.artifactId !== value.artifactId ||
    reference.digest !== value.contentDigest ||
    source === null ||
    selection === null ||
    template === null ||
    origin === null ||
    origin.assetId !== source.assetId
  ) {
    return null
  }

  return Object.freeze({
    generationId: value.generationId,
    artifactId: value.artifactId,
    projectId: value.projectId,
    mediaReference: value.mediaReference,
    bindingDigest: value.bindingDigest,
    contentDigest: value.contentDigest,
    byteSize: value.byteSize,
    width: value.width,
    height: value.height,
    createdAt: value.createdAt,
    wasAlreadyRegistered: value.wasAlreadyRegistered,
    source,
    selection,
    template,
    origin,
  })
}

function parseOutputSource(value: unknown): ThumbnailOutputSource | null {
  if (
    !hasExactKeys(value, [
      'artifactId',
      'assetId',
      'contentDigest',
      'ptsIndexDigest',
      'frameMapDigest',
      'rightsStatus',
      'externalPublicationAllowed',
    ]) ||
    !isEntityId(value.artifactId) ||
    !isEntityId(value.assetId) ||
    !isDigest(value.contentDigest) ||
    !isDigest(value.ptsIndexDigest) ||
    !isDigest(value.frameMapDigest) ||
    value.rightsStatus !== 'approved' ||
    value.externalPublicationAllowed !== true
  ) {
    return null
  }
  return Object.freeze({
    artifactId: value.artifactId,
    assetId: value.assetId,
    contentDigest: value.contentDigest,
    ptsIndexDigest: value.ptsIndexDigest,
    frameMapDigest: value.frameMapDigest,
    rightsStatus: 'approved',
    externalPublicationAllowed: true,
  })
}

function parseOutputSelection(value: unknown): ThumbnailOutputSelection | null {
  if (
    !hasExactKeys(value, [
      'proxyFrameSequence',
      'sourceFrameSequence',
      'sourcePresentationTime',
      'selectedAt',
    ]) ||
    !isNonnegativeInt32(value.proxyFrameSequence) ||
    !isNonnegativeInt32(value.sourceFrameSequence) ||
    !isCanonicalTimestamp(value.selectedAt)
  ) {
    return null
  }
  const sourcePresentationTime = parseRational(value.sourcePresentationTime)
  return sourcePresentationTime === null
    ? null
    : Object.freeze({
        proxyFrameSequence: value.proxyFrameSequence,
        sourceFrameSequence: value.sourceFrameSequence,
        sourcePresentationTime,
        selectedAt: value.selectedAt,
      })
}

function parseOutputTemplate(value: unknown): ThumbnailOutputTemplate | null {
  if (
    !hasExactKeys(value, [
      'approvalId',
      'approvalDigest',
      'approvalRole',
      'approvedAt',
      'templateId',
      'templateVersionDigest',
      'renderProfileId',
      'renderProfileDigest',
      'layoutSpecId',
      'layoutSpecDigest',
    ]) ||
    !isEntityId(value.approvalId) ||
    !isDigest(value.approvalDigest) ||
    value.approvalRole !== 'brandApprover' ||
    !isCanonicalTimestamp(value.approvedAt) ||
    !isEntityId(value.templateId) ||
    !isDigest(value.templateVersionDigest) ||
    !isEntityId(value.renderProfileId) ||
    !isDigest(value.renderProfileDigest) ||
    !isEntityId(value.layoutSpecId) ||
    !isDigest(value.layoutSpecDigest)
  ) {
    return null
  }
  return Object.freeze({
    approvalId: value.approvalId,
    approvalDigest: value.approvalDigest,
    approvalRole: 'brandApprover',
    approvedAt: value.approvedAt,
    templateId: value.templateId,
    templateVersionDigest: value.templateVersionDigest,
    renderProfileId: value.renderProfileId,
    renderProfileDigest: value.renderProfileDigest,
    layoutSpecId: value.layoutSpecId,
    layoutSpecDigest: value.layoutSpecDigest,
  })
}

function parseOutputOrigin(
  value: unknown,
): Readonly<{ kind: 'asset'; assetId: string }> | null {
  if (
    !hasExactKeys(value, ['kind', 'assetId']) ||
    value.kind !== 'asset' ||
    !isEntityId(value.assetId)
  ) {
    return null
  }
  return Object.freeze({ kind: 'asset', assetId: value.assetId })
}

function parseThumbnailReference(value: string): Readonly<{
  projectId: string
  artifactId: string
  digest: string
}> | null {
  const match = thumbnailReferencePattern.exec(value)
  if (match === null) {
    return null
  }
  const [, projectId, artifactId, digest] = match
  return projectId === undefined || artifactId === undefined || digest === undefined
    ? null
    : Object.freeze({ projectId, artifactId, digest })
}

function parseRational(value: unknown): RationalTime | null {
  if (
    !hasExactKeys(value, ['value', 'rate']) ||
    typeof value.value !== 'string' ||
    typeof value.rate !== 'string' ||
    !signedIntegerPattern.test(value.value) ||
    !positiveIntegerPattern.test(value.rate)
  ) {
    return null
  }
  const numerator = BigInt(value.value)
  const denominator = BigInt(value.rate)
  if (
    numerator < int64Minimum ||
    numerator > int64Maximum ||
    denominator > int64Maximum ||
    greatestCommonDivisor(numerator < 0n ? -numerator : numerator, denominator) !== 1n
  ) {
    return null
  }
  return Object.freeze({ value: value.value, rate: value.rate })
}

function greatestCommonDivisor(left: bigint, right: bigint): bigint {
  let current = left
  let remainder = right
  while (remainder !== 0n) {
    const next = current % remainder
    current = remainder
    remainder = next
  }
  return current
}

function compareRationals(left: RationalTime, right: RationalTime): number {
  const difference =
    BigInt(left.value) * BigInt(right.rate) -
    BigInt(right.value) * BigInt(left.rate)
  return difference < 0n ? -1 : difference > 0n ? 1 : 0
}

function hasResponseEnvelope<TMethod extends string>(
  value: unknown,
  method: TMethod,
): value is Record<'version' | 'requestId' | 'method' | 'result', unknown> & {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: TMethod
} {
  return (
    isWithinUtf8Limit(value, BRIDGE_MAX_THUMBNAIL_RESPONSE_UTF8_BYTES) &&
    hasExactKeys(value, ['version', 'requestId', 'method', 'result']) &&
    value.version === BRIDGE_PROTOCOL_VERSION &&
    isEntityId(value.requestId) &&
    value.method === method
  )
}

function freezeBoundedRequest<T extends ThumbnailBridgeRequest>(request: T): T {
  if (!isWithinUtf8Limit(request, BRIDGE_MAX_THUMBNAIL_REQUEST_UTF8_BYTES)) {
    throw new Error('The thumbnail bridge request exceeds the protocol byte limit.')
  }
  return Object.freeze(request) as T
}

function assertEntityId(value: string, kind: string): void {
  if (!entityIdPattern.test(value)) {
    throw new Error(`A thumbnail ${kind} ID must be a non-empty lowercase UUID.`)
  }
}

function assertDigest(value: string, kind: string): void {
  if (!digestPattern.test(value)) {
    throw new Error(`A ${kind} digest must be canonical SHA-256.`)
  }
}

function isEntityId(value: unknown): value is string {
  return typeof value === 'string' && entityIdPattern.test(value)
}

function isDigest(value: unknown): value is string {
  return typeof value === 'string' && digestPattern.test(value)
}

function isCanonicalTimestamp(value: unknown): value is string {
  if (typeof value !== 'string' || !timestampPattern.test(value)) {
    return false
  }
  const parsed = new Date(value)
  return (
    Number.isFinite(parsed.valueOf()) &&
    parsed.toISOString() === `${value.slice(0, 23)}Z`
  )
}

function isPositiveInt64String(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    positiveIntegerPattern.test(value) &&
    BigInt(value) <= int64Maximum
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

function isPositiveInt32(value: unknown): value is number {
  return isNonnegativeInt32(value) && value > 0
}

function isOneOf<T extends string>(
  value: unknown,
  allowed: readonly T[],
): value is T {
  return typeof value === 'string' && allowed.includes(value as T)
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

function hasExactKeys<const TKey extends string>(
  value: unknown,
  expectedKeys: readonly TKey[],
): value is Record<TKey, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false
  }
  const keys = Object.keys(value)
  return (
    keys.length === expectedKeys.length &&
    expectedKeys.every((key) => Object.prototype.hasOwnProperty.call(value, key))
  )
}
