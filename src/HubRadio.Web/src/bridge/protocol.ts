export const BRIDGE_PROTOCOL_VERSION = 1 as const
export const BRIDGE_MAX_REQUEST_UTF8_BYTES = 256 as const
export const BRIDGE_MAX_RESPONSE_UTF8_BYTES = 4096 as const
export const BRIDGE_PING_METHOD = 'bridge.ping' as const
export const JOBS_GET_STATUS_METHOD = 'jobs.getStatus' as const
export const JOBS_CANCEL_METHOD = 'jobs.cancel' as const
export const JOBS_RETRY_METHOD = 'jobs.retry' as const

const requestIdPattern =
  /^(?!00000000-0000-0000-0000-000000000000(?![\s\S]))[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![\s\S])/
const timestampPattern =
  /^(?!0000)\d{4}-(0[1-9]|1[0-2])-([0-2]\d|3[0-1])T([0-1]\d|2[0-3]):[0-5]\d:[0-5]\d\.\d{7}Z$/

export type BridgeJobMethod =
  | typeof JOBS_GET_STATUS_METHOD
  | typeof JOBS_CANCEL_METHOD
  | typeof JOBS_RETRY_METHOD
export type BridgeMethod = typeof BRIDGE_PING_METHOD | BridgeJobMethod
export type JobKind = 'mediaIngest' | 'finalRender' | 'unknown'
export type JobState =
  | 'queued'
  | 'running'
  | 'succeeded'
  | 'failed'
  | 'cancelled'
  | 'interrupted'
export type JobRetryability = 'notApplicable' | 'retryable' | 'nonRetryable'
export type JobFailure = 'timedOut' | 'appRestarted' | 'other'
export type JobReconciliation =
  | 'notApplicable'
  | 'notRequired'
  | 'required'
  | 'unknown'
export type JobWarningCode =
  | 'audioTrackAbsent'
  | 'durationFieldsDiffer'
  | 'lowResolution'
  | 'multipleAudioStreams'
  | 'multipleVideoStreams'
  | 'signatureFormatMismatch'
  | 'unrecognizedContainerSignature'
  | 'variableRateFieldsDiffer'
export type JobCancellationOutcome =
  | 'requested'
  | 'alreadyRequested'
  | 'notRunning'
  | 'notTracked'
export type JobRetryOutcome =
  | 'queued'
  | 'stateNotEligible'
  | 'errorNotRetryable'
  | 'coordinatorRunActive'
export type BridgeErrorCode =
  | 'forbidden'
  | 'notFound'
  | 'conflict'
  | 'unavailable'

export interface BridgePingRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof BRIDGE_PING_METHOD
  readonly params: Readonly<Record<string, never>>
}

export interface BridgeJobStatusRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof JOBS_GET_STATUS_METHOD
  readonly params: Readonly<{ jobId: string }>
}

export interface BridgeJobCommandRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof JOBS_CANCEL_METHOD | typeof JOBS_RETRY_METHOD
  readonly params: Readonly<{
    jobId: string
    expectedRevision: number
  }>
}

export type BridgeRequest =
  | BridgePingRequest
  | BridgeJobStatusRequest
  | BridgeJobCommandRequest

export interface BridgePingResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof BRIDGE_PING_METHOD
  readonly result: Readonly<{
    protocolVersion: typeof BRIDGE_PROTOCOL_VERSION
  }>
}

export interface BridgeJobSnapshot {
  readonly jobId: string
  readonly kind: JobKind
  readonly state: JobState
  readonly currentAttemptId: string | null
  readonly currentAttemptNumber: number
  readonly revision: number
  readonly detailsRevision: number | null
  readonly updatedAt: string
  readonly progressRatio: number | null
  readonly lastProgressAt: string | null
  readonly lastHeartbeatAt: string | null
  readonly failure: JobFailure | null
  readonly retryability: JobRetryability
  readonly cancellationRequested: boolean
  readonly warnings: readonly JobWarningCode[]
  readonly reconciliation: JobReconciliation
}

export interface BridgeJobStatusResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof JOBS_GET_STATUS_METHOD
  readonly result: BridgeJobSnapshot
}

export interface BridgeJobCancelResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof JOBS_CANCEL_METHOD
  readonly result: Readonly<{
    outcome: JobCancellationOutcome
    job: BridgeJobSnapshot
  }>
}

export interface BridgeJobRetryResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof JOBS_RETRY_METHOD
  readonly result: Readonly<{
    outcome: JobRetryOutcome
    job: BridgeJobSnapshot
  }>
}

export interface BridgeErrorResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: BridgeMethod
  readonly error: Readonly<{ code: BridgeErrorCode }>
}

export type BridgeJobResponse =
  | BridgeJobStatusResponse
  | BridgeJobCancelResponse
  | BridgeJobRetryResponse
  | (BridgeErrorResponse & Readonly<{ method: BridgeJobMethod }>)

const snapshotKeys = [
  'jobId',
  'kind',
  'state',
  'currentAttemptId',
  'currentAttemptNumber',
  'revision',
  'detailsRevision',
  'updatedAt',
  'progressRatio',
  'lastProgressAt',
  'lastHeartbeatAt',
  'failure',
  'retryability',
  'cancellationRequested',
  'warnings',
  'reconciliation',
] as const
const jobKinds: readonly JobKind[] = ['mediaIngest', 'finalRender', 'unknown']
const jobStates: readonly JobState[] = [
  'queued',
  'running',
  'succeeded',
  'failed',
  'cancelled',
  'interrupted',
]
const retryabilities: readonly JobRetryability[] = [
  'notApplicable',
  'retryable',
  'nonRetryable',
]
const failures: readonly JobFailure[] = ['timedOut', 'appRestarted', 'other']
const reconciliations: readonly JobReconciliation[] = [
  'notApplicable',
  'notRequired',
  'required',
  'unknown',
]
const warningCodes: readonly JobWarningCode[] = [
  'audioTrackAbsent',
  'durationFieldsDiffer',
  'lowResolution',
  'multipleAudioStreams',
  'multipleVideoStreams',
  'signatureFormatMismatch',
  'unrecognizedContainerSignature',
  'variableRateFieldsDiffer',
]
const cancellationOutcomes: readonly JobCancellationOutcome[] = [
  'requested',
  'alreadyRequested',
  'notRunning',
  'notTracked',
]
const retryOutcomes: readonly JobRetryOutcome[] = [
  'queued',
  'stateNotEligible',
  'errorNotRetryable',
  'coordinatorRunActive',
]
const errorCodes: readonly BridgeErrorCode[] = [
  'forbidden',
  'notFound',
  'conflict',
  'unavailable',
]

export function isCanonicalBridgeId(value: string): boolean {
  return requestIdPattern.test(value)
}

export function createPingRequest(requestId: string): BridgePingRequest {
  validateId(requestId, 'request')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: BRIDGE_PING_METHOD,
    params: Object.freeze({}),
  })
}

export function parsePingResponse(value: unknown): BridgePingResponse | null {
  if (
    !isWithinUtf8Limit(value, BRIDGE_MAX_RESPONSE_UTF8_BYTES) ||
    !isRecordWithExactKeys(value, ['version', 'requestId', 'method', 'result'])
  ) {
    return null
  }

  const result = value.result
  if (
    value.version !== BRIDGE_PROTOCOL_VERSION ||
    typeof value.requestId !== 'string' ||
    !requestIdPattern.test(value.requestId) ||
    value.method !== BRIDGE_PING_METHOD ||
    !isRecordWithExactKeys(result, ['protocolVersion']) ||
    result.protocolVersion !== BRIDGE_PROTOCOL_VERSION
  ) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method: BRIDGE_PING_METHOD,
    result: Object.freeze({ protocolVersion: BRIDGE_PROTOCOL_VERSION }),
  })
}

export function createJobStatusRequest(
  requestId: string,
  jobId: string,
): BridgeJobStatusRequest {
  validateId(requestId, 'request')
  validateId(jobId, 'job')
  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: JOBS_GET_STATUS_METHOD,
    params: Object.freeze({ jobId }),
  })
}

export function createJobCancelRequest(
  requestId: string,
  jobId: string,
  expectedRevision: number,
): BridgeJobCommandRequest {
  return createJobCommandRequest(
    requestId,
    JOBS_CANCEL_METHOD,
    jobId,
    expectedRevision,
  )
}

export function createJobRetryRequest(
  requestId: string,
  jobId: string,
  expectedRevision: number,
): BridgeJobCommandRequest {
  return createJobCommandRequest(
    requestId,
    JOBS_RETRY_METHOD,
    jobId,
    expectedRevision,
  )
}

export function parseJobResponse(value: unknown): BridgeJobResponse | null {
  if (!isWithinUtf8Limit(value, BRIDGE_MAX_RESPONSE_UTF8_BYTES)) {
    return null
  }

  if (
    isRecordWithExactKeys(value, ['version', 'requestId', 'method', 'error'])
  ) {
    if (!hasValidEnvelope(value) || !isJobMethod(value.method)) {
      return null
    }

    const error = value.error
    if (
      !isRecordWithExactKeys(error, ['code']) ||
      !isOneOf(error.code, errorCodes)
    ) {
      return null
    }

    return Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId: value.requestId,
      method: value.method,
      error: Object.freeze({ code: error.code }),
    })
  }

  if (
    !isRecordWithExactKeys(value, ['version', 'requestId', 'method', 'result']) ||
    !hasValidEnvelope(value) ||
    !isJobMethod(value.method)
  ) {
    return null
  }

  if (value.method === JOBS_GET_STATUS_METHOD) {
    const snapshot = parseJobSnapshot(value.result)
    return snapshot === null
      ? null
      : Object.freeze({
          version: BRIDGE_PROTOCOL_VERSION,
          requestId: value.requestId,
          method: JOBS_GET_STATUS_METHOD,
          result: snapshot,
        })
  }

  const result = value.result
  if (!isRecordWithExactKeys(result, ['outcome', 'job'])) {
    return null
  }

  const snapshot = parseJobSnapshot(result.job)
  if (snapshot === null) {
    return null
  }

  if (value.method === JOBS_CANCEL_METHOD) {
    if (!isOneOf(result.outcome, cancellationOutcomes)) {
      return null
    }

    return Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId: value.requestId,
      method: JOBS_CANCEL_METHOD,
      result: Object.freeze({ outcome: result.outcome, job: snapshot }),
    })
  }

  if (!isOneOf(result.outcome, retryOutcomes)) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method: JOBS_RETRY_METHOD,
    result: Object.freeze({ outcome: result.outcome, job: snapshot }),
  })
}

function createJobCommandRequest(
  requestId: string,
  method: typeof JOBS_CANCEL_METHOD | typeof JOBS_RETRY_METHOD,
  jobId: string,
  expectedRevision: number,
): BridgeJobCommandRequest {
  validateId(requestId, 'request')
  validateId(jobId, 'job')
  if (!isInt32(expectedRevision)) {
    throw new Error('An expected job revision must be a nonnegative 32-bit integer.')
  }

  return freezeBoundedRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method,
    params: Object.freeze({ jobId, expectedRevision }),
  })
}

function freezeBoundedRequest<T extends BridgeRequest>(request: T): T {
  if (!isWithinUtf8Limit(request, BRIDGE_MAX_REQUEST_UTF8_BYTES)) {
    throw new Error('The bridge request exceeds the supported size.')
  }

  return Object.freeze(request) as T
}

function validateId(value: string, kind: 'request' | 'job'): void {
  if (!requestIdPattern.test(value)) {
    throw new Error(`A bridge ${kind} ID must be a non-empty lowercase UUID.`)
  }
}

function parseJobSnapshot(value: unknown): BridgeJobSnapshot | null {
  if (!isRecordWithExactKeys(value, snapshotKeys)) {
    return null
  }

  if (
    typeof value.jobId !== 'string' ||
    !requestIdPattern.test(value.jobId) ||
    !isOneOf(value.kind, jobKinds) ||
    !isOneOf(value.state, jobStates) ||
    !isNullableId(value.currentAttemptId) ||
    !isInt32(value.currentAttemptNumber) ||
    !isInt32(value.revision) ||
    !isNullableInt32(value.detailsRevision) ||
    !isCanonicalTimestamp(value.updatedAt) ||
    !isNullableRatio(value.progressRatio) ||
    !isNullableTimestamp(value.lastProgressAt) ||
    !isNullableTimestamp(value.lastHeartbeatAt) ||
    !isNullableOneOf(value.failure, failures) ||
    !isOneOf(value.retryability, retryabilities) ||
    typeof value.cancellationRequested !== 'boolean' ||
    !isCanonicalWarnings(value.warnings) ||
    !isOneOf(value.reconciliation, reconciliations)
  ) {
    return null
  }

  if (
    (value.currentAttemptNumber === 0) !== (value.currentAttemptId === null) ||
    (requiresAttempt(value.state) && value.currentAttemptNumber === 0) ||
    (value.retryability !== 'notApplicable') !==
      (value.state === 'failed' || value.state === 'interrupted') ||
    (value.cancellationRequested && value.state !== 'running') ||
    (value.kind !== 'mediaIngest' &&
      (value.detailsRevision !== null ||
        value.warnings.length !== 0 ||
        value.reconciliation !== 'notApplicable')) ||
    (value.kind === 'mediaIngest' && value.reconciliation === 'notApplicable')
  ) {
    return null
  }

  return Object.freeze({
    jobId: value.jobId,
    kind: value.kind,
    state: value.state,
    currentAttemptId: value.currentAttemptId,
    currentAttemptNumber: value.currentAttemptNumber,
    revision: value.revision,
    detailsRevision: value.detailsRevision,
    updatedAt: value.updatedAt,
    progressRatio: value.progressRatio,
    lastProgressAt: value.lastProgressAt,
    lastHeartbeatAt: value.lastHeartbeatAt,
    failure: value.failure,
    retryability: value.retryability,
    cancellationRequested: value.cancellationRequested,
    warnings: Object.freeze([...value.warnings]),
    reconciliation: value.reconciliation,
  })
}

function hasValidEnvelope(
  value: Record<string, unknown>,
): value is Record<string, unknown> & {
  version: typeof BRIDGE_PROTOCOL_VERSION
  requestId: string
} {
  return (
    value.version === BRIDGE_PROTOCOL_VERSION &&
    typeof value.requestId === 'string' &&
    requestIdPattern.test(value.requestId)
  )
}

function isJobMethod(value: unknown): value is BridgeJobMethod {
  return (
    value === JOBS_GET_STATUS_METHOD ||
    value === JOBS_CANCEL_METHOD ||
    value === JOBS_RETRY_METHOD
  )
}

function isCanonicalWarnings(value: unknown): value is JobWarningCode[] {
  if (!Array.isArray(value) || value.length > warningCodes.length) {
    return false
  }

  let previousIndex = -1
  for (const warning of value) {
    const index = warningCodes.indexOf(warning as JobWarningCode)
    if (index <= previousIndex) {
      return false
    }

    previousIndex = index
  }

  return true
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

function isNullableTimestamp(value: unknown): value is string | null {
  return value === null || isCanonicalTimestamp(value)
}

function isNullableId(value: unknown): value is string | null {
  return value === null || (typeof value === 'string' && requestIdPattern.test(value))
}

function isNullableRatio(value: unknown): value is number | null {
  return (
    value === null ||
    (typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 1)
  )
}

function isInt32(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= 2_147_483_647
  )
}

function isNullableInt32(value: unknown): value is number | null {
  return value === null || isInt32(value)
}

function isNullableOneOf<T extends string>(
  value: unknown,
  allowed: readonly T[],
): value is T | null {
  return value === null || isOneOf(value, allowed)
}

function isOneOf<T extends string>(
  value: unknown,
  allowed: readonly T[],
): value is T {
  return typeof value === 'string' && allowed.includes(value as T)
}

function requiresAttempt(state: JobState): boolean {
  return (
    state === 'running' ||
    state === 'succeeded' ||
    state === 'failed' ||
    state === 'interrupted'
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

function isRecordWithExactKeys<const TKey extends string>(
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