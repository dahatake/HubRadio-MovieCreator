import { BRIDGE_PROTOCOL_VERSION } from '../../bridge/protocol'

export const REVIEW_GET_SNAPSHOT_METHOD = 'review.getSnapshot' as const
export const REVIEW_ADD_COMMENT_METHOD = 'review.addComment' as const
export const REVIEW_REQUEST_CHANGES_METHOD = 'review.requestChanges' as const
export const REVIEW_RECORD_APPROVAL_METHOD = 'review.recordApproval' as const
export const REVIEW_MAX_REQUEST_UTF8_BYTES = 24 * 1024
export const REVIEW_MAX_RESPONSE_UTF8_BYTES = 1024 * 1024
export const REVIEW_MAX_TEXT_UTF8_BYTES = 16 * 1024

export type ReviewBridgeMethod =
  | typeof REVIEW_GET_SNAPSHOT_METHOD
  | typeof REVIEW_ADD_COMMENT_METHOD
  | typeof REVIEW_REQUEST_CHANGES_METHOD
  | typeof REVIEW_RECORD_APPROVAL_METHOD

export type ReviewState =
  | 'editing'
  | 'editorReview'
  | 'performerReview'
  | 'contentReview'
  | 'brandReview'
  | 'changesRequested'
  | 'approved'
  | 'publicationBlocked'
  | 'finalRendering'
  | 'qualityControl'
  | 'exported'
  | 'published'

export type ReviewActorRole =
  | 'editor'
  | 'performerReviewer'
  | 'contentApprover'
  | 'brandApprover'
  | 'rightsApprover'
  | 'administrator'

export type ReviewAuthorActorRole = Exclude<ReviewActorRole, 'administrator'>

export type ReviewApprovalActorRole =
  | 'contentApprover'
  | 'brandApprover'
  | 'rightsApprover'

export interface ReviewRationalTime {
  readonly value: string
  readonly rate: string
}

export interface ReviewComment {
  readonly id: string
  readonly outputTime: ReviewRationalTime
  readonly sourceSpanId: string
  readonly actorRole: ReviewActorRole
  readonly createdAt: string
  readonly text: string
}

export type ReviewApprovalValidity = 'valid' | 'stale'

export interface ReviewApproval {
  readonly id: string
  readonly versionId: string
  readonly versionDigest: string
  readonly actorRole: ReviewApprovalActorRole
  readonly approvedAt: string
  readonly reason: string
  readonly validity: ReviewApprovalValidity
}

export type ReviewCompositionChangeKind =
  | 'added'
  | 'removed'
  | 'moved'
  | 'modified'

export interface ReviewCompositionChange {
  readonly kind: ReviewCompositionChangeKind
  readonly elementId: string
  readonly previousIndex: number | null
  readonly currentIndex: number | null
}

export interface ReviewDirectParentDiff {
  readonly fromVersionId: string
  readonly toVersionId: string
  readonly changes: readonly ReviewCompositionChange[]
}

export type ReviewActionKind =
  | 'addComment'
  | 'requestChanges'
  | 'recordApproval'

export type ReviewActionStatus =
  | 'authorized'
  | 'actorNotAuthorized'
  | 'alreadyRecorded'
  | 'stateNotEligible'
  | 'targetNotCurrent'
  | 'reviewStateUnrecorded'
  | 'publicationBlocked'

export interface ReviewAction {
  readonly kind: ReviewActionKind
  readonly status: ReviewActionStatus
  readonly actorRoles: readonly ReviewAuthorActorRole[]
}

export interface ReviewSnapshot {
  readonly versionId: string
  readonly versionDigest: string
  readonly createdAt: string
  readonly parentVersionId: string | null
  readonly isCurrentTarget: boolean
  readonly reviewState: ReviewState | null
  readonly reviewStateReason: string | null
  readonly reviewStateRecordedAt: string | null
  readonly reviewStateActorRole: ReviewActorRole | null
  readonly comments: readonly ReviewComment[]
  readonly approvals: readonly ReviewApproval[]
  readonly directParentDiff: ReviewDirectParentDiff | null
  readonly actions: readonly ReviewAction[]
}

interface ReviewRequestBase<M extends ReviewBridgeMethod, P> {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: M
  readonly params: Readonly<P>
}

export type ReviewGetSnapshotRequest = ReviewRequestBase<
  typeof REVIEW_GET_SNAPSHOT_METHOD,
  { versionId: string }
>

export type ReviewAddCommentRequest = ReviewRequestBase<
  typeof REVIEW_ADD_COMMENT_METHOD,
  {
    versionId: string
    versionDigest: string
    actorRole: ReviewAuthorActorRole
    outputTime: ReviewRationalTime
    text: string
  }
>

export type ReviewRequestChangesRequest = ReviewRequestBase<
  typeof REVIEW_REQUEST_CHANGES_METHOD,
  {
    versionId: string
    versionDigest: string
    expectedState: ReviewState
    outputTime: ReviewRationalTime
    text: string
  }
>

export type ReviewRecordApprovalRequest = ReviewRequestBase<
  typeof REVIEW_RECORD_APPROVAL_METHOD,
  {
    versionId: string
    versionDigest: string
    actorRole: ReviewApprovalActorRole
    reason: string
  }
>

export type ReviewBridgeRequest =
  | ReviewGetSnapshotRequest
  | ReviewAddCommentRequest
  | ReviewRequestChangesRequest
  | ReviewRecordApprovalRequest

export interface ReviewBridgeResultResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: ReviewBridgeMethod
  readonly result: ReviewSnapshot
}

export type ReviewBridgeErrorCode =
  | 'review.conflict'
  | 'review.invalid_request'
  | 'review.not_authorized'
  | 'review.not_found'
  | 'review.operation_failed'
  | 'review.stale_version'

export interface ReviewBridgeErrorResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: ReviewBridgeMethod
  readonly error: Readonly<{
    code: ReviewBridgeErrorCode
    message: string
  }>
}

export type ReviewBridgeResponse =
  | ReviewBridgeResultResponse
  | ReviewBridgeErrorResponse

export function createReviewGetSnapshotRequest(
  requestId: string,
  versionId: string,
): ReviewGetSnapshotRequest {
  requireEntityId(requestId, 'A bridge request ID')
  requireEntityId(versionId, 'A composition version ID')
  return freezeRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: REVIEW_GET_SNAPSHOT_METHOD,
    params: Object.freeze({ versionId }),
  })
}

export function createReviewAddCommentRequest(
  requestId: string,
  versionId: string,
  versionDigest: string,
  actorRole: ReviewAuthorActorRole,
  outputTime: ReviewRationalTime,
  text: string,
): ReviewAddCommentRequest {
  requireEntityId(requestId, 'A bridge request ID')
  requireVersionTarget(versionId, versionDigest)
  requireReviewAuthorRole(actorRole)
  const canonicalOutputTime = requireOutputTime(outputTime)
  requireReviewText(text, 'Review-comment text')
  return freezeRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: REVIEW_ADD_COMMENT_METHOD,
    params: Object.freeze({
      versionId,
      versionDigest,
      actorRole,
      outputTime: canonicalOutputTime,
      text,
    }),
  })
}

export function createReviewRequestChangesRequest(
  requestId: string,
  versionId: string,
  versionDigest: string,
  expectedState: ReviewState,
  outputTime: ReviewRationalTime,
  text: string,
): ReviewRequestChangesRequest {
  requireEntityId(requestId, 'A bridge request ID')
  requireVersionTarget(versionId, versionDigest)
  if (!reviewStates.has(expectedState)) {
    throw new Error('An expected review state must be a closed review-state value.')
  }
  const canonicalOutputTime = requireOutputTime(outputTime)
  requireReviewText(text, 'Change-request text')
  return freezeRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: REVIEW_REQUEST_CHANGES_METHOD,
    params: Object.freeze({
      versionId,
      versionDigest,
      expectedState,
      outputTime: canonicalOutputTime,
      text,
    }),
  })
}

export function createReviewRecordApprovalRequest(
  requestId: string,
  versionId: string,
  versionDigest: string,
  actorRole: ReviewApprovalActorRole,
  reason: string,
): ReviewRecordApprovalRequest {
  requireEntityId(requestId, 'A bridge request ID')
  requireVersionTarget(versionId, versionDigest)
  if (!approvalActorRoles.has(actorRole)) {
    throw new Error('An approval actor role must be a closed approval-role value.')
  }
  requireReviewText(reason, 'An approval reason')
  return freezeRequest({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: REVIEW_RECORD_APPROVAL_METHOD,
    params: Object.freeze({ versionId, versionDigest, actorRole, reason }),
  })
}

export function parseReviewBridgeResponse(
  value: unknown,
): ReviewBridgeResponse | null {
  if (!isWithinUtf8Limit(value, REVIEW_MAX_RESPONSE_UTF8_BYTES)) {
    return null
  }

  if (isRecordWithExactKeys(value, ['version', 'requestId', 'method', 'error'])) {
    if (!hasValidEnvelope(value)) {
      return null
    }
    const error = value.error
    if (
      !isRecordWithExactKeys(error, ['code', 'message']) ||
      !reviewErrorCodes.has(error.code as ReviewBridgeErrorCode) ||
      !isSubstantiveTrimmedString(error.message)
    ) {
      return null
    }
    return Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId: value.requestId,
      method: value.method,
      error: Object.freeze({
        code: error.code as ReviewBridgeErrorCode,
        message: error.message,
      }),
    })
  }

  if (
    !isRecordWithExactKeys(value, ['version', 'requestId', 'method', 'result']) ||
    !hasValidEnvelope(value)
  ) {
    return null
  }

  const snapshot = parseReviewSnapshot(value.result)
  if (snapshot === null) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method: value.method,
    result: snapshot,
  })
}

export function isCanonicalReviewEntityId(value: string): boolean {
  return entityIdPattern.test(value)
}

export function isCanonicalReviewDigest(value: string): boolean {
  return digestPattern.test(value)
}

export function isReviewOutputTime(
  value: ReviewRationalTime,
): boolean {
  return parseRationalTime(value) !== null
}

export function isReviewTextInput(value: string): boolean {
  return (
    isSubstantiveTrimmedString(value) &&
    hasWellFormedUnicode(value) &&
    utf8Length(value) <= REVIEW_MAX_TEXT_UTF8_BYTES
  )
}

function parseReviewSnapshot(value: unknown): ReviewSnapshot | null {
  if (
    !isRecordWithExactKeys(value, [
      'versionId',
      'versionDigest',
      'createdAt',
      'parentVersionId',
      'isCurrentTarget',
      'reviewState',
      'reviewStateReason',
      'reviewStateRecordedAt',
      'reviewStateActorRole',
      'comments',
      'approvals',
      'directParentDiff',
      'actions',
    ]) ||
    !isEntityId(value.versionId) ||
    !isContentDigest(value.versionDigest) ||
    !isNativeTimestamp(value.createdAt) ||
    !isNullOrEntityId(value.parentVersionId) ||
    typeof value.isCurrentTarget !== 'boolean' ||
    !isNullOrReviewState(value.reviewState) ||
    !isNullOrSubstantiveString(value.reviewStateReason) ||
    !isNullOrTimestamp(value.reviewStateRecordedAt) ||
    !isNullOrActorRole(value.reviewStateActorRole)
  ) {
    return null
  }

  const stateMetadataIsNull =
    value.reviewStateReason === null &&
    value.reviewStateRecordedAt === null &&
    value.reviewStateActorRole === null
  const stateMetadataIsComplete =
    value.reviewStateReason !== null &&
    value.reviewStateRecordedAt !== null &&
    value.reviewStateActorRole !== null
  if (
    value.reviewState === null
      ? !stateMetadataIsNull
      : !stateMetadataIsComplete
  ) {
    return null
  }

  const comments = parseComments(value.comments)
  const approvals = parseApprovals(
    value.approvals,
    value.versionId,
    value.versionDigest,
  )
  const directParentDiff = parseDirectParentDiff(
    value.directParentDiff,
    value.parentVersionId,
    value.versionId,
  )
  const actions = parseActions(
    value.actions,
    value.isCurrentTarget,
    value.reviewState,
  )
  if (
    comments === null ||
    approvals === null ||
    directParentDiff === undefined ||
    actions === null
  ) {
    return null
  }

  return Object.freeze({
    versionId: value.versionId,
    versionDigest: value.versionDigest,
    createdAt: value.createdAt,
    parentVersionId: value.parentVersionId,
    isCurrentTarget: value.isCurrentTarget,
    reviewState: value.reviewState,
    reviewStateReason: value.reviewStateReason,
    reviewStateRecordedAt: value.reviewStateRecordedAt,
    reviewStateActorRole: value.reviewStateActorRole,
    comments,
    approvals,
    directParentDiff,
    actions,
  })
}

function parseComments(value: unknown): readonly ReviewComment[] | null {
  if (!Array.isArray(value)) {
    return null
  }

  const comments: ReviewComment[] = []
  const ids = new Set<string>()
  for (const item of value) {
    if (
      !isRecordWithExactKeys(item, [
        'id',
        'outputTime',
        'sourceSpanId',
        'actorRole',
        'createdAt',
        'text',
      ]) ||
      !isEntityId(item.id) ||
      ids.has(item.id) ||
      !isEntityId(item.sourceSpanId) ||
      !reviewActorRoles.has(item.actorRole as ReviewActorRole) ||
      !isNativeTimestamp(item.createdAt) ||
      typeof item.text !== 'string' ||
      !isReviewTextInput(item.text)
    ) {
      return null
    }
    const outputTime = parseRationalTime(item.outputTime)
    if (outputTime === null) {
      return null
    }
    ids.add(item.id)
    comments.push(Object.freeze({
      id: item.id,
      outputTime,
      sourceSpanId: item.sourceSpanId,
      actorRole: item.actorRole as ReviewActorRole,
      createdAt: item.createdAt,
      text: item.text,
    }))
  }
  return Object.freeze(comments)
}

function parseApprovals(
  value: unknown,
  currentVersionId: string,
  currentVersionDigest: string,
): readonly ReviewApproval[] | null {
  if (!Array.isArray(value)) {
    return null
  }

  const approvals: ReviewApproval[] = []
  const ids = new Set<string>()
  for (const item of value) {
    if (
      !isRecordWithExactKeys(item, [
        'id',
        'versionId',
        'versionDigest',
        'actorRole',
        'approvedAt',
        'reason',
        'validity',
      ]) ||
      !isEntityId(item.id) ||
      ids.has(item.id) ||
      !isEntityId(item.versionId) ||
      !isContentDigest(item.versionDigest) ||
      !approvalActorRoles.has(item.actorRole as ReviewApprovalActorRole) ||
      !isNativeTimestamp(item.approvedAt) ||
      typeof item.reason !== 'string' ||
      !isReviewTextInput(item.reason) ||
      !approvalValidities.has(item.validity as ReviewApprovalValidity)
    ) {
      return null
    }

    const targetsCurrentVersion =
      item.versionId === currentVersionId &&
      item.versionDigest === currentVersionDigest
    if ((item.validity === 'valid') !== targetsCurrentVersion) {
      return null
    }

    ids.add(item.id)
    approvals.push(Object.freeze({
      id: item.id,
      versionId: item.versionId,
      versionDigest: item.versionDigest,
      actorRole: item.actorRole as ReviewApprovalActorRole,
      approvedAt: item.approvedAt,
      reason: item.reason,
      validity: item.validity as ReviewApprovalValidity,
    }))
  }
  return Object.freeze(approvals)
}

function parseDirectParentDiff(
  value: unknown,
  parentVersionId: string | null,
  currentVersionId: string,
): ReviewDirectParentDiff | null | undefined {
  if (value === null) {
    return parentVersionId === null ? null : undefined
  }
  if (
    parentVersionId === null ||
    !isRecordWithExactKeys(value, ['fromVersionId', 'toVersionId', 'changes']) ||
    value.fromVersionId !== parentVersionId ||
    value.toVersionId !== currentVersionId ||
    !Array.isArray(value.changes)
  ) {
    return undefined
  }

  const changes: ReviewCompositionChange[] = []
  const elementIds = new Set<string>()
  for (const item of value.changes) {
    if (
      !isRecordWithExactKeys(item, [
        'kind',
        'elementId',
        'previousIndex',
        'currentIndex',
      ]) ||
      !compositionChangeKinds.has(item.kind as ReviewCompositionChangeKind) ||
      !isEntityId(item.elementId) ||
      elementIds.has(item.elementId) ||
      !isNullOrNonnegativeInt32(item.previousIndex) ||
      !isNullOrNonnegativeInt32(item.currentIndex) ||
      !indicesMatchChangeKind(
        item.kind as ReviewCompositionChangeKind,
        item.previousIndex,
        item.currentIndex,
      )
    ) {
      return undefined
    }
    elementIds.add(item.elementId)
    changes.push(Object.freeze({
      kind: item.kind as ReviewCompositionChangeKind,
      elementId: item.elementId,
      previousIndex: item.previousIndex,
      currentIndex: item.currentIndex,
    }))
  }

  return Object.freeze({
    fromVersionId: value.fromVersionId,
    toVersionId: value.toVersionId,
    changes: Object.freeze(changes),
  })
}

function parseActions(
  value: unknown,
  isCurrentTarget: boolean,
  state: ReviewState | null,
): readonly ReviewAction[] | null {
  if (!Array.isArray(value) || value.length !== actionKinds.length) {
    return null
  }

  const actions: ReviewAction[] = []
  for (let index = 0; index < actionKinds.length; index++) {
    const item = value[index]
    const expectedKind = actionKinds[index]
    if (
      !isRecordWithExactKeys(item, ['kind', 'status', 'actorRoles']) ||
      item.kind !== expectedKind ||
      !actionStatuses.has(item.status as ReviewActionStatus)
    ) {
      return null
    }
    const actorRoles = parseActionActorRoles(item.actorRoles)
    if (actorRoles === null) {
      return null
    }
    actions.push(Object.freeze({
      kind: expectedKind,
      status: item.status as ReviewActionStatus,
      actorRoles,
    }))
  }

  if (!isCurrentTarget) {
    return actions.every(
      (action) =>
        action.status === 'targetNotCurrent' && action.actorRoles.length === 0,
    )
      ? Object.freeze(actions)
      : null
  }

  if (actions.some((action) => action.status === 'targetNotCurrent')) {
    return null
  }

  if (!actions.every((action) => actionStatusMatchesState(action, state))) {
    return null
  }

  return Object.freeze(actions)
}

function actionStatusMatchesState(
  action: ReviewAction,
  state: ReviewState | null,
): boolean {
  if (action.kind === 'addComment') {
    return (
      (action.status === 'authorized' && action.actorRoles.length > 0) ||
      (action.status === 'actorNotAuthorized' && action.actorRoles.length === 0)
    )
  }

  if (action.kind === 'recordApproval') {
    const rolesAreApprovers = action.actorRoles.every((role) =>
      approvalActorRoles.has(role as ReviewApprovalActorRole),
    )
    return rolesAreApprovers && (
      (action.status === 'authorized' && action.actorRoles.length > 0) ||
      ((action.status === 'actorNotAuthorized' ||
        action.status === 'alreadyRecorded') &&
        action.actorRoles.length === 0)
    )
  }

  if (state === null) {
    return action.status === 'reviewStateUnrecorded' && action.actorRoles.length === 0
  }
  if (state === 'publicationBlocked') {
    return action.status === 'publicationBlocked' && action.actorRoles.length === 0
  }

  const requiredRole = requestChangesRoleByState[state]
  if (requiredRole === undefined) {
    return action.status === 'stateNotEligible' && action.actorRoles.length === 0
  }
  return (
    (action.status === 'authorized' &&
      action.actorRoles.length === 1 &&
      action.actorRoles.includes(requiredRole)) ||
    (action.status === 'actorNotAuthorized' && action.actorRoles.length === 0)
  )
}

function parseActionActorRoles(
  value: unknown,
): readonly ReviewAuthorActorRole[] | null {
  if (!Array.isArray(value)) {
    return null
  }

  const roles: ReviewAuthorActorRole[] = []
  let previousIndex = -1
  for (const candidate of value) {
    const index = reviewAuthorRoleOrder.indexOf(
      candidate as ReviewAuthorActorRole,
    )
    if (index <= previousIndex) {
      return null
    }
    previousIndex = index
    roles.push(candidate as ReviewAuthorActorRole)
  }
  return Object.freeze(roles)
}

function parseRationalTime(value: unknown): ReviewRationalTime | null {
  if (
    !isRecordWithExactKeys(value, ['value', 'rate']) ||
    !isCanonicalNonnegativeInt64(value.value) ||
    !isCanonicalPositiveInt64(value.rate)
  ) {
    return null
  }

  const numerator = BigInt(value.value)
  const denominator = BigInt(value.rate)
  if (greatestCommonDivisor(numerator, denominator) !== 1n) {
    return null
  }
  return Object.freeze({ value: value.value, rate: value.rate })
}

function requireOutputTime(value: ReviewRationalTime): ReviewRationalTime {
  const parsed = parseRationalTime(value)
  if (parsed === null) {
    throw new Error(
      'A review output time must be a reduced nonnegative signed-64-bit rational.',
    )
  }
  return parsed
}

function requireVersionTarget(versionId: string, versionDigest: string): void {
  requireEntityId(versionId, 'A composition version ID')
  if (!digestPattern.test(versionDigest)) {
    throw new Error('A composition version digest must be 64 lowercase hexadecimal characters.')
  }
}

function requireReviewAuthorRole(actorRole: ReviewAuthorActorRole): void {
  if (!reviewAuthorActorRoles.has(actorRole)) {
    throw new Error('A review actor role must be a closed author-role value.')
  }
}

function requireReviewText(value: string, description: string): void {
  if (!isReviewTextInput(value)) {
    throw new Error(
      `${description} must be trimmed, nonblank, valid Unicode, and at most 16 KiB UTF-8.`,
    )
  }
}

function freezeRequest<T extends ReviewBridgeRequest>(request: T): T {
  if (!isWithinUtf8Limit(request, REVIEW_MAX_REQUEST_UTF8_BYTES)) {
    throw new Error('The review request exceeds the supported UTF-8 byte limit.')
  }
  return Object.freeze(request) as T
}

function hasValidEnvelope(
  value: Record<string, unknown>,
): value is Record<string, unknown> & {
  version: typeof BRIDGE_PROTOCOL_VERSION
  requestId: string
  method: ReviewBridgeMethod
} {
  return (
    value.version === BRIDGE_PROTOCOL_VERSION &&
    isEntityId(value.requestId) &&
    reviewMethods.has(value.method as ReviewBridgeMethod)
  )
}

function indicesMatchChangeKind(
  kind: ReviewCompositionChangeKind,
  previousIndex: number | null,
  currentIndex: number | null,
): boolean {
  switch (kind) {
    case 'added':
      return previousIndex === null && currentIndex !== null
    case 'removed':
      return previousIndex !== null && currentIndex === null
    case 'moved':
      return (
        previousIndex !== null &&
        currentIndex !== null &&
        previousIndex !== currentIndex
      )
    case 'modified':
      return previousIndex !== null && currentIndex !== null
  }
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

function isCanonicalNonnegativeInt64(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    nonnegativeInt64Pattern.test(value) &&
    BigInt(value) <= maxInt64
  )
}

function isCanonicalPositiveInt64(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    positiveInt64Pattern.test(value) &&
    BigInt(value) <= maxInt64
  )
}

function isNativeTimestamp(value: unknown): value is string {
  if (typeof value !== 'string' || !nativeTimestampPattern.test(value)) {
    return false
  }
  const parsed = new Date(value)
  return (
    Number.isFinite(parsed.valueOf()) &&
    parsed.toISOString() === `${value.slice(0, 23)}Z`
  )
}

function isNullOrTimestamp(value: unknown): value is string | null {
  return value === null || isNativeTimestamp(value)
}

function isNullOrReviewState(value: unknown): value is ReviewState | null {
  return value === null || reviewStates.has(value as ReviewState)
}

function isNullOrActorRole(value: unknown): value is ReviewActorRole | null {
  return value === null || reviewActorRoles.has(value as ReviewActorRole)
}

function isNullOrSubstantiveString(value: unknown): value is string | null {
  return value === null || isSubstantiveTrimmedString(value)
}

function isSubstantiveTrimmedString(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    value.length > 0 &&
    value.trim().length > 0 &&
    value === value.trim() &&
    hasWellFormedUnicode(value)
  )
}

function hasWellFormedUnicode(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const codeUnit = value.charCodeAt(index)
    if (codeUnit >= 0xd800 && codeUnit <= 0xdbff) {
      const next = value.charCodeAt(index + 1)
      if (!(next >= 0xdc00 && next <= 0xdfff)) {
        return false
      }
      index++
    } else if (codeUnit >= 0xdc00 && codeUnit <= 0xdfff) {
      return false
    }
  }
  return true
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

function isNonnegativeInt32(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= 2_147_483_647
  )
}

function isNullOrNonnegativeInt32(value: unknown): value is number | null {
  return value === null || isNonnegativeInt32(value)
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

function isWithinUtf8Limit(value: unknown, maximumBytes: number): boolean {
  try {
    const serialized = JSON.stringify(value)
    return serialized !== undefined && utf8Length(serialized) <= maximumBytes
  } catch {
    return false
  }
}

function utf8Length(value: string): number {
  return new TextEncoder().encode(value).byteLength
}

function requireEntityId(value: string, description: string): void {
  if (!entityIdPattern.test(value)) {
    throw new Error(`${description} must be a non-empty lowercase UUID.`)
  }
}

const maxInt64 = 9_223_372_036_854_775_807n
const entityIdPattern =
  /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const digestPattern = /^[0-9a-f]{64}$/
const nonnegativeInt64Pattern = /^(?:0|[1-9][0-9]{0,18})$/
const positiveInt64Pattern = /^[1-9][0-9]{0,18}$/
const nativeTimestampPattern =
  /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}\+00:00$/

const reviewMethods = new Set<ReviewBridgeMethod>([
  REVIEW_GET_SNAPSHOT_METHOD,
  REVIEW_ADD_COMMENT_METHOD,
  REVIEW_REQUEST_CHANGES_METHOD,
  REVIEW_RECORD_APPROVAL_METHOD,
])
const reviewStates = new Set<ReviewState>([
  'editing',
  'editorReview',
  'performerReview',
  'contentReview',
  'brandReview',
  'changesRequested',
  'approved',
  'publicationBlocked',
  'finalRendering',
  'qualityControl',
  'exported',
  'published',
])
const reviewActorRoles = new Set<ReviewActorRole>([
  'editor',
  'performerReviewer',
  'contentApprover',
  'brandApprover',
  'rightsApprover',
  'administrator',
])
const reviewAuthorRoleOrder: readonly ReviewAuthorActorRole[] = [
  'editor',
  'performerReviewer',
  'contentApprover',
  'brandApprover',
  'rightsApprover',
]
const reviewAuthorActorRoles = new Set<ReviewAuthorActorRole>(
  reviewAuthorRoleOrder,
)
const approvalActorRoles = new Set<ReviewApprovalActorRole>([
  'contentApprover',
  'brandApprover',
  'rightsApprover',
])
const approvalValidities = new Set<ReviewApprovalValidity>(['valid', 'stale'])
const compositionChangeKinds = new Set<ReviewCompositionChangeKind>([
  'added',
  'removed',
  'moved',
  'modified',
])
const actionKinds: readonly ReviewActionKind[] = [
  'addComment',
  'requestChanges',
  'recordApproval',
]
const actionStatuses = new Set<ReviewActionStatus>([
  'authorized',
  'actorNotAuthorized',
  'alreadyRecorded',
  'stateNotEligible',
  'targetNotCurrent',
  'reviewStateUnrecorded',
  'publicationBlocked',
])
const requestChangesRoleByState: Partial<
  Record<ReviewState, ReviewAuthorActorRole>
> = {
  editorReview: 'editor',
  performerReview: 'performerReviewer',
  contentReview: 'contentApprover',
  brandReview: 'brandApprover',
}
const reviewErrorCodes = new Set<ReviewBridgeErrorCode>([
  'review.conflict',
  'review.invalid_request',
  'review.not_authorized',
  'review.not_found',
  'review.operation_failed',
  'review.stale_version',
])
