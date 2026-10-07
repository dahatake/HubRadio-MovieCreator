import {
  BRIDGE_MAX_PROJECT_REQUEST_UTF8_BYTES,
  BRIDGE_PROTOCOL_VERSION,
} from './protocol'

export const PROJECT_LIST_METHOD = 'project.list' as const
export const PROJECT_CREATE_DRAFT_METHOD = 'project.createDraft' as const
export const PROJECT_LOAD_DRAFT_METHOD = 'project.loadDraft' as const
export const PROJECT_SAVE_DRAFT_METHOD = 'project.saveDraft' as const
export const PROJECT_PICK_SOURCE_METHOD = 'project.pickSource' as const
export const PROJECT_FINALIZE_DRAFT_METHOD = 'project.finalizeDraft' as const
export const PROJECT_DELETE_METHOD = 'project.delete' as const
export const PROJECT_RENAME_METHOD = 'project.rename' as const
/** Longest display name (in Unicode code points) the host accepts. */
export const PROJECT_DISPLAY_NAME_MAX_LENGTH = 100
export const MAX_BRIDGE_REQUEST_UTF8_BYTES =
  BRIDGE_MAX_PROJECT_REQUEST_UTF8_BYTES

export type ProjectBridgeMethod =
  | typeof PROJECT_LIST_METHOD
  | typeof PROJECT_CREATE_DRAFT_METHOD
  | typeof PROJECT_LOAD_DRAFT_METHOD
  | typeof PROJECT_SAVE_DRAFT_METHOD
  | typeof PROJECT_PICK_SOURCE_METHOD
  | typeof PROJECT_FINALIZE_DRAFT_METHOD
  | typeof PROJECT_DELETE_METHOD
  | typeof PROJECT_RENAME_METHOD

export type PublicationScope =
  | 'internalOnly'
  | 'specificGroupOnly'
  | 'externalCandidate'
  | 'externalPublicationApproved'
  | 'publicationForbidden'
  | 'pendingDecision'

export type SourceStatus = 'notSelected' | 'available' | 'reselectionRequired'

export type ProjectCreationMissingField =
  | 'sourceMedia'
  | 'contentName'
  | 'purpose'
  | 'audience'
  | 'publicationScope'
  | 'outputFormats'
  | 'language'
  | 'performerNames'
  | 'deadline'
  | 'brandTemplate'

export type ProjectDeadlineInput =
  | null
  | Readonly<{ hasDeadline: false; value: null }>
  | Readonly<{ hasDeadline: true; value: string }>

export interface ProjectDraftInput {
  readonly sourceCandidateId: string | null
  readonly contentName: string | null
  readonly purpose: string | null
  readonly audience: string | null
  readonly publicationScope: PublicationScope | null
  readonly outputProfileIds: readonly string[]
  readonly language: string | null
  readonly performerNames: readonly string[]
  readonly deadlineSelection: ProjectDeadlineInput
  readonly brandTemplateId: string | null
}

export interface ProjectCatalogOption {
  readonly id: string
  readonly name: string
}

export interface ProjectCatalog {
  readonly outputProfiles: readonly ProjectCatalogOption[]
  readonly brandTemplates: readonly ProjectCatalogOption[]
}

export interface ProjectThumbnailReference {
  readonly executionId: string
  readonly artifactId: string
  readonly contentDigest: string
}

export interface ProjectListItem {
  readonly draftId: string
  readonly projectId: string | null
  readonly state: 'draft' | 'finalized'
  readonly contentName: string | null
  readonly displayName?: string | null
  readonly episodeNumber: null
  readonly sourceStatus: SourceStatus
  readonly owner: null
  readonly publicationScope: PublicationScope | null
  readonly editState: null
  readonly reviewState: null
  readonly outputFormats: readonly string[]
  readonly updatedAt: string
  /** Generated landscape thumbnail; omitted by hosts that predate FR-045. */
  readonly thumbnail?: ProjectThumbnailReference | null
  /** FR-053: poster frame of the source video, shown when there is no generated thumbnail. */
  readonly poster?: ProjectPosterReference | null
}

export interface ProjectPosterReference {
  readonly contentDigest: string
}

/** FR-053: the host serves a poster only while its Project is listed, after re-verifying the digest. */
export function projectPosterUrl(draftId: string, poster: ProjectPosterReference): string {
  return `https://player-media.appassets.example/source-only/poster/${draftId}/${poster.contentDigest}`
}

/** The host serves only artifacts of a stored execution that it revalidates on every read. */
export function projectThumbnailUrl(
  projectId: string,
  thumbnail: ProjectThumbnailReference,
): string {
  return `https://player-media.appassets.example/source-only/${projectId}/${thumbnail.executionId}/${thumbnail.artifactId}/${thumbnail.contentDigest}`
}

export interface SourceOnlyProjectSummary {
  readonly profileId: string
  readonly profileVersion: string
  readonly profileDigest: string
  readonly settingsSnapshotDigest: string
  readonly artifactSet: readonly string[]
}

export interface ProjectDraft {
  readonly draftId: string
  readonly revision: number
  readonly isFinalized: boolean
  readonly projectId: string | null
  readonly sourceCandidateId: string | null
  readonly sourceStatus: SourceStatus
  readonly contentName: string | null
  readonly displayName?: string | null
  readonly purpose: string | null
  readonly audience: string | null
  readonly publicationScope: PublicationScope | null
  readonly outputProfileIds: readonly string[]
  readonly language: string | null
  readonly performerNames: readonly string[]
  readonly deadlineSelection: ProjectDeadlineInput
  readonly brandTemplateId: string | null
  readonly missingFields: readonly ProjectCreationMissingField[]
  readonly updatedAt: string
  readonly sourceOnly: SourceOnlyProjectSummary | null
  /** FR-054: settings the built-in preset chose for this video; omitted by hosts that predate FR-054. */
  readonly autoSettings?: readonly ProjectAutoSetting[] | null
}

export interface ProjectAutoSetting {
  readonly key: string
  readonly label: string
  readonly valueText: string
  readonly reason: string
}

interface ProjectRequestBase<M extends ProjectBridgeMethod, P> {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: M
  readonly params: Readonly<P>
}

export type ProjectListRequest = ProjectRequestBase<
  typeof PROJECT_LIST_METHOD,
  Record<string, never>
>
export type ProjectCreateDraftRequest = ProjectRequestBase<
  typeof PROJECT_CREATE_DRAFT_METHOD,
  Record<string, never>
>
export type ProjectLoadDraftRequest = ProjectRequestBase<
  typeof PROJECT_LOAD_DRAFT_METHOD,
  { draftId: string }
>
export type ProjectSaveDraftRequest = ProjectRequestBase<
  typeof PROJECT_SAVE_DRAFT_METHOD,
  { draftId: string; expectedRevision: number; input: ProjectDraftInput }
>
export type ProjectPickSourceRequest = ProjectRequestBase<
  typeof PROJECT_PICK_SOURCE_METHOD,
  Record<string, never>
>
export type ProjectFinalizeDraftRequest = ProjectRequestBase<
  typeof PROJECT_FINALIZE_DRAFT_METHOD,
  { draftId: string; expectedRevision: number; input: ProjectDraftInput }
>
export type ProjectDeleteRequest = ProjectRequestBase<
  typeof PROJECT_DELETE_METHOD,
  { draftId: string }
>

export type ProjectRenameRequest = ProjectRequestBase<
  typeof PROJECT_RENAME_METHOD,
  { draftId: string; displayName: string }
>

export type ProjectBridgeRequest =
  | ProjectListRequest
  | ProjectCreateDraftRequest
  | ProjectLoadDraftRequest
  | ProjectSaveDraftRequest
  | ProjectPickSourceRequest
  | ProjectFinalizeDraftRequest
  | ProjectDeleteRequest
  | ProjectRenameRequest

export interface ProjectListResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof PROJECT_LIST_METHOD
  readonly result: Readonly<{
    projects: readonly ProjectListItem[]
    catalog: ProjectCatalog
    canCreate: boolean
  }>
}

export type DraftResponseMethod =
  | typeof PROJECT_CREATE_DRAFT_METHOD
  | typeof PROJECT_LOAD_DRAFT_METHOD
  | typeof PROJECT_SAVE_DRAFT_METHOD
  | typeof PROJECT_FINALIZE_DRAFT_METHOD
  | typeof PROJECT_RENAME_METHOD

export interface ProjectDraftResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: DraftResponseMethod
  readonly result: Readonly<{ draft: ProjectDraft }>
}

export interface ProjectPickSourceResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof PROJECT_PICK_SOURCE_METHOD
  readonly result: Readonly<{ candidateId: string | null }>
}

export interface ProjectDeleteResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof PROJECT_DELETE_METHOD
  readonly result: Readonly<{ deletedDraftId: string; filesRemoved: boolean }>
}

export type ProjectBridgeErrorCode =
  | 'project.already_finalized'
  | 'project.catalog_unavailable'
  | 'project.delete_blocked'
  | 'project.incomplete'
  | 'project.invalid_request'
  | 'project.not_authorized'
  | 'project.not_found'
  | 'project.operation_failed'
  | 'project.revision_conflict'
  | 'project.runtime_unavailable'
  | 'project.settings_mismatch'
  | 'project.settings_unavailable'
  | 'project.source_selection_canceled'
  | 'project.source_reselection_required'
  | 'project.unsafe_source_name'

export interface ProjectBridgeErrorResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: ProjectBridgeMethod
  readonly error: Readonly<{
    code: ProjectBridgeErrorCode
    message: string
  }>
}

export type ProjectBridgeResponse =
  | ProjectListResponse
  | ProjectDraftResponse
  | ProjectPickSourceResponse
  | ProjectDeleteResponse
  | ProjectBridgeErrorResponse

export function createProjectListRequest(requestId: string): ProjectListRequest {
  requireEntityId(requestId, 'A bridge request ID')
  return createEmptyRequest(requestId, PROJECT_LIST_METHOD)
}

export function createProjectCreateDraftRequest(
  requestId: string,
): ProjectCreateDraftRequest {
  requireEntityId(requestId, 'A bridge request ID')
  return createEmptyRequest(requestId, PROJECT_CREATE_DRAFT_METHOD)
}

export function createProjectLoadDraftRequest(
  requestId: string,
  draftId: string,
): ProjectLoadDraftRequest {
  requireEntityId(requestId, 'A bridge request ID')
  requireEntityId(draftId, 'A project draft ID')
  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: PROJECT_LOAD_DRAFT_METHOD,
    params: Object.freeze({ draftId }),
  })
}

export function createProjectSaveDraftRequest(
  requestId: string,
  draftId: string,
  expectedRevision: number,
  input: ProjectDraftInput,
): ProjectSaveDraftRequest {
  return createDraftMutationRequest(
    requestId,
    draftId,
    expectedRevision,
    input,
    PROJECT_SAVE_DRAFT_METHOD,
  )
}

export function createProjectFinalizeDraftRequest(
  requestId: string,
  draftId: string,
  expectedRevision: number,
  input: ProjectDraftInput,
): ProjectFinalizeDraftRequest {
  return createDraftMutationRequest(
    requestId,
    draftId,
    expectedRevision,
    input,
    PROJECT_FINALIZE_DRAFT_METHOD,
  )
}

export function createProjectPickSourceRequest(
  requestId: string,
): ProjectPickSourceRequest {
  requireEntityId(requestId, 'A bridge request ID')
  return createEmptyRequest(requestId, PROJECT_PICK_SOURCE_METHOD)
}

export function createProjectDeleteRequest(
  requestId: string,
  draftId: string,
): ProjectDeleteRequest {
  requireEntityId(requestId, 'A bridge request ID')
  requireEntityId(draftId, 'A project draft ID')
  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: PROJECT_DELETE_METHOD,
    params: Object.freeze({ draftId }),
  })
}

/** Returns the name as the host stores it (trimmed), or null when the host would reject it. */
export function normalizeProjectDisplayName(value: string): string | null {
  const name = value.trim().normalize('NFC')
  const length = Array.from(name).length
  if (length === 0 || length > PROJECT_DISPLAY_NAME_MAX_LENGTH) {
    return null
  }

  for (const character of name) {
    const codePoint = character.codePointAt(0)!
    if (
      codePoint <= 0x1f ||
      (codePoint >= 0x7f && codePoint <= 0x9f) ||
      codePoint === 0x2028 ||
      codePoint === 0x2029 ||
      (codePoint >= 0x202a && codePoint <= 0x202e) ||
      (codePoint >= 0x2066 && codePoint <= 0x2069) ||
      (codePoint >= 0xd800 && codePoint <= 0xdfff)
    ) {
      return null
    }
  }

  return name
}

export function createProjectRenameRequest(
  requestId: string,
  draftId: string,
  displayName: string,
): ProjectRenameRequest {
  requireEntityId(requestId, 'A bridge request ID')
  requireEntityId(draftId, 'A project draft ID')
  const normalized = normalizeProjectDisplayName(displayName)
  if (normalized === null) {
    throw new Error('A project display name must be 1 to 100 printable characters.')
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: PROJECT_RENAME_METHOD,
    params: Object.freeze({ draftId, displayName: normalized }),
  })
}

export function parseProjectDraftInput(value: unknown): ProjectDraftInput | null {
  if (
    !isRecordWithExactKeys(value, [
      'sourceCandidateId',
      'contentName',
      'purpose',
      'audience',
      'publicationScope',
      'outputProfileIds',
      'language',
      'performerNames',
      'deadlineSelection',
      'brandTemplateId',
    ]) ||
    !isNullOrEntityId(value.sourceCandidateId) ||
    !isNullOrString(value.contentName) ||
    !isNullOrString(value.purpose) ||
    !isNullOrString(value.audience) ||
    !isNullOrPublicationScope(value.publicationScope) ||
    !isUniqueEntityIdArray(value.outputProfileIds) ||
    !isNullOrString(value.language) ||
    !isUniqueStringArray(value.performerNames) ||
    !isDeadlineInput(value.deadlineSelection) ||
    !isNullOrEntityId(value.brandTemplateId)
  ) {
    return null
  }

  return freezeDraftInput({
    sourceCandidateId: value.sourceCandidateId,
    contentName: value.contentName,
    purpose: value.purpose,
    audience: value.audience,
    publicationScope: value.publicationScope,
    outputProfileIds: value.outputProfileIds,
    language: value.language,
    performerNames: value.performerNames,
    deadlineSelection: value.deadlineSelection,
    brandTemplateId: value.brandTemplateId,
  })
}

export function isProjectDraftInputWithinRequestLimit(
  input: ProjectDraftInput,
): boolean {
  const largestMutationRequest = {
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: 'ffffffff-ffff-ffff-ffff-ffffffffffff',
    method: PROJECT_FINALIZE_DRAFT_METHOD,
    params: {
      draftId: 'ffffffff-ffff-ffff-ffff-ffffffffffff',
      expectedRevision: 2147483647,
      input,
    },
  }
  return utf8Length(JSON.stringify(largestMutationRequest)) <= MAX_BRIDGE_REQUEST_UTF8_BYTES
}

export function parseProjectBridgeResponse(value: unknown): ProjectBridgeResponse | null {
  if (!isRecord(value)) {
    return null
  }

  const keys = Object.keys(value)
  const isResult = hasExactKeys(keys, ['version', 'requestId', 'method', 'result'])
  const isError = hasExactKeys(keys, ['version', 'requestId', 'method', 'error'])
  if (
    (!isResult && !isError) ||
    value.version !== BRIDGE_PROTOCOL_VERSION ||
    !isEntityId(value.requestId) ||
    !isProjectBridgeMethod(value.method)
  ) {
    return null
  }

  if (isError) {
    return parseErrorResponse(value)
  }

  switch (value.method) {
    case PROJECT_LIST_METHOD:
      return parseListResult(value)
    case PROJECT_CREATE_DRAFT_METHOD:
    case PROJECT_LOAD_DRAFT_METHOD:
    case PROJECT_SAVE_DRAFT_METHOD:
    case PROJECT_FINALIZE_DRAFT_METHOD:
    case PROJECT_RENAME_METHOD:
      return parseDraftResult(value, value.method)
    case PROJECT_PICK_SOURCE_METHOD:
      return parsePickSourceResult(value)
    case PROJECT_DELETE_METHOD:
      return parseDeleteResult(value)
  }
}

function createEmptyRequest<M extends
  | typeof PROJECT_LIST_METHOD
  | typeof PROJECT_CREATE_DRAFT_METHOD
  | typeof PROJECT_PICK_SOURCE_METHOD>(
  requestId: string,
  method: M,
): Extract<ProjectBridgeRequest, { method: M }> {
  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method,
    params: Object.freeze({}),
  }) as Extract<ProjectBridgeRequest, { method: M }>
}

function createDraftMutationRequest<M extends
  | typeof PROJECT_SAVE_DRAFT_METHOD
  | typeof PROJECT_FINALIZE_DRAFT_METHOD>(
  requestId: string,
  draftId: string,
  expectedRevision: number,
  input: ProjectDraftInput,
  method: M,
): Extract<ProjectBridgeRequest, { method: M }> {
  requireEntityId(requestId, 'A bridge request ID')
  requireEntityId(draftId, 'A project draft ID')
  if (!isNonnegativeInt32(expectedRevision)) {
    throw new Error('An expected project draft revision must be a non-negative 32-bit integer.')
  }

  const parsedInput = parseProjectDraftInput(input)
  if (parsedInput === null) {
    throw new Error('The project draft input does not match the closed bridge schema.')
  }

  const request = Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method,
    params: Object.freeze({ draftId, expectedRevision, input: parsedInput }),
  }) as Extract<ProjectBridgeRequest, { method: M }>
  if (!isProjectRequestWithinLimit(request)) {
    throw new Error('The project request exceeds the supported UTF-8 byte limit.')
  }

  return request
}

function parseListResult(value: Record<string, unknown>): ProjectListResponse | null {
  const result = value.result
  if (
    value.method !== PROJECT_LIST_METHOD ||
    !isRecordWithExactKeys(result, ['projects', 'catalog', 'canCreate']) ||
    !Array.isArray(result.projects) ||
    typeof result.canCreate !== 'boolean'
  ) {
    return null
  }

  const projects: ProjectListItem[] = []
  for (const candidate of result.projects) {
    const project = parseProjectListItem(candidate)
    if (project === null) {
      return null
    }

    projects.push(project)
  }

  const catalog = parseCatalog(result.catalog)
  if (catalog === null || !hasUniqueIds(projects, (project) => project.draftId)) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId as string,
    method: PROJECT_LIST_METHOD,
    result: Object.freeze({
      projects: Object.freeze(projects),
      catalog,
      canCreate: result.canCreate,
    }),
  })
}

function parseDraftResult(
  value: Record<string, unknown>,
  method: DraftResponseMethod,
): ProjectDraftResponse | null {
  const result = value.result
  if (!isRecordWithExactKeys(result, ['draft'])) {
    return null
  }

  const draft = parseProjectDraft(result.draft)
  if (draft === null) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId as string,
    method,
    result: Object.freeze({ draft }),
  })
}

function parsePickSourceResult(
  value: Record<string, unknown>,
): ProjectPickSourceResponse | null {
  const result = value.result
  if (
    value.method !== PROJECT_PICK_SOURCE_METHOD ||
    !isRecordWithExactKeys(result, ['candidateId']) ||
    !isNullOrEntityId(result.candidateId)
  ) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId as string,
    method: PROJECT_PICK_SOURCE_METHOD,
    result: Object.freeze({ candidateId: result.candidateId }),
  })
}

function parseDeleteResult(
  value: Record<string, unknown>,
): ProjectDeleteResponse | null {
  const result = value.result
  if (
    value.method !== PROJECT_DELETE_METHOD ||
    !isRecordWithExactKeys(result, ['deletedDraftId', 'filesRemoved']) ||
    !isEntityId(result.deletedDraftId) ||
    typeof result.filesRemoved !== 'boolean'
  ) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId as string,
    method: PROJECT_DELETE_METHOD,
    result: Object.freeze({
      deletedDraftId: result.deletedDraftId,
      filesRemoved: result.filesRemoved,
    }),
  })
}

function parseErrorResponse(
  value: Record<string, unknown>,
): ProjectBridgeErrorResponse | null {
  const error = value.error
  if (
    !isRecordWithExactKeys(error, ['code', 'message']) ||
    !isProjectBridgeErrorCode(error.code) ||
    typeof error.message !== 'string' ||
    error.message.length === 0
  ) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId as string,
    method: value.method as ProjectBridgeMethod,
    error: Object.freeze({ code: error.code, message: error.message }),
  })
}

function parseProjectListItem(value: unknown): ProjectListItem | null {
  const optionalKeys = [
    ...(isRecord(value) && 'displayName' in value ? ['displayName'] : []),
    ...(isRecord(value) && 'thumbnail' in value ? ['thumbnail'] : []),
    ...(isRecord(value) && 'poster' in value ? ['poster'] : []),
  ]
  if (
    !isRecordWithExactKeys(value, [
      'draftId',
      'projectId',
      'state',
      'contentName',
      'episodeNumber',
      'sourceStatus',
      'owner',
      'publicationScope',
      'editState',
      'reviewState',
      'outputFormats',
      'updatedAt',
      ...optionalKeys,
    ]) ||
    !isEntityId(value.draftId) ||
    !isNullOrEntityId(value.projectId) ||
    (value.state !== 'draft' && value.state !== 'finalized') ||
    !isNullOrString(value.contentName) ||
    !isOptionalDisplayName(value) ||
    value.episodeNumber !== null ||
    !isSourceStatus(value.sourceStatus) ||
    value.owner !== null ||
    !isNullOrPublicationScope(value.publicationScope) ||
    value.editState !== null ||
    value.reviewState !== null ||
    !isStringArray(value.outputFormats) ||
    !isNativeTimestamp(value.updatedAt) ||
    (value.state === 'draft' && value.projectId !== null) ||
    (value.state === 'finalized' && value.projectId === null)
  ) {
    return null
  }

  let thumbnail: ProjectThumbnailReference | null = null
  if ('thumbnail' in value) {
    thumbnail = parseProjectThumbnail(value.thumbnail)
    if (
      (value.thumbnail !== null && thumbnail === null) ||
      (thumbnail !== null && value.projectId === null)
    ) {
      return null
    }
  }

  let poster: ProjectPosterReference | null = null
  if ('poster' in value) {
    if (value.poster !== null) {
      if (!isRecordWithExactKeys(value.poster, ['contentDigest']) || !isContentDigest(value.poster.contentDigest)) {
        return null
      }
      poster = Object.freeze({ contentDigest: value.poster.contentDigest })
    }
  }

  return Object.freeze({
    draftId: value.draftId,
    projectId: value.projectId,
    state: value.state,
    contentName: value.contentName,
    ...('displayName' in value ? { displayName: value.displayName as string | null } : {}),
    episodeNumber: null,
    sourceStatus: value.sourceStatus,
    owner: null,
    publicationScope: value.publicationScope,
    editState: null,
    reviewState: null,
    outputFormats: Object.freeze([...value.outputFormats]),
    updatedAt: value.updatedAt,
    ...('thumbnail' in value ? { thumbnail } : {}),
    ...('poster' in value ? { poster } : {}),
  })
}

function parseProjectThumbnail(value: unknown): ProjectThumbnailReference | null {
  if (
    !isRecordWithExactKeys(value, ['executionId', 'artifactId', 'contentDigest']) ||
    !isEntityId(value.executionId) ||
    !isEntityId(value.artifactId) ||
    !isContentDigest(value.contentDigest)
  ) {
    return null
  }

  return Object.freeze({
    executionId: value.executionId,
    artifactId: value.artifactId,
    contentDigest: value.contentDigest,
  })
}

function parseCatalog(value: unknown): ProjectCatalog | null {
  if (!isRecordWithExactKeys(value, ['outputProfiles', 'brandTemplates'])) {
    return null
  }

  const outputProfiles = parseCatalogOptions(value.outputProfiles)
  const brandTemplates = parseCatalogOptions(value.brandTemplates)
  if (outputProfiles === null || brandTemplates === null) {
    return null
  }

  return Object.freeze({ outputProfiles, brandTemplates })
}

function parseCatalogOptions(value: unknown): readonly ProjectCatalogOption[] | null {
  if (!Array.isArray(value)) {
    return null
  }

  const options: ProjectCatalogOption[] = []
  for (const item of value) {
    if (
      !isRecordWithExactKeys(item, ['id', 'name']) ||
      !isEntityId(item.id) ||
      typeof item.name !== 'string' ||
      item.name.length === 0
    ) {
      return null
    }

    options.push(Object.freeze({ id: item.id, name: item.name }))
  }

  return hasUniqueIds(options, (option) => option.id) ? Object.freeze(options) : null
}

function parseProjectDraft(value: unknown): ProjectDraft | null {
  if (
    !isRecordWithExactKeys(value, withOptionalDisplayName(value, [
      'draftId',
      'revision',
      'isFinalized',
      'projectId',
      'sourceCandidateId',
      'sourceStatus',
      'contentName',
      'purpose',
      'audience',
      'publicationScope',
      'outputProfileIds',
      'language',
      'performerNames',
      'deadlineSelection',
      'brandTemplateId',
      'missingFields',
      'updatedAt',
      'sourceOnly',
      ...(isRecord(value) && 'autoSettings' in value ? ['autoSettings'] : []),
    ])) ||
    !isEntityId(value.draftId) ||
    !isNonnegativeInt32(value.revision) ||
    typeof value.isFinalized !== 'boolean' ||
    !isNullOrEntityId(value.projectId) ||
    !isNullOrEntityId(value.sourceCandidateId) ||
    !isSourceStatus(value.sourceStatus) ||
    !isNullOrString(value.contentName) ||
    !isOptionalDisplayName(value) ||
    !isNullOrString(value.purpose) ||
    !isNullOrString(value.audience) ||
    !isNullOrPublicationScope(value.publicationScope) ||
    !isUniqueEntityIdArray(value.outputProfileIds) ||
    !isNullOrString(value.language) ||
    !isUniqueStringArray(value.performerNames) ||
    !isDeadlineInput(value.deadlineSelection, true) ||
    !isNullOrEntityId(value.brandTemplateId) ||
    !isUniqueMissingFieldArray(value.missingFields) ||
    !isNativeTimestamp(value.updatedAt) ||
    !isNullOrSourceOnlyProjectSummary(value.sourceOnly) ||
    ('autoSettings' in value && value.autoSettings !== null && !isAutoSettingArray(value.autoSettings)) ||
    (value.isFinalized && value.projectId === null) ||
    (!value.isFinalized && value.projectId !== null)
  ) {
    return null
  }

  return Object.freeze({
    draftId: value.draftId,
    revision: value.revision,
    isFinalized: value.isFinalized,
    projectId: value.projectId,
    sourceCandidateId: value.sourceCandidateId,
    sourceStatus: value.sourceStatus,
    contentName: value.contentName,
    ...('displayName' in value ? { displayName: value.displayName as string | null } : {}),
    purpose: value.purpose,
    audience: value.audience,
    publicationScope: value.publicationScope,
    outputProfileIds: Object.freeze([...value.outputProfileIds]),
    language: value.language,
    performerNames: Object.freeze([...value.performerNames]),
    deadlineSelection: freezeDeadlineInput(value.deadlineSelection),
    brandTemplateId: value.brandTemplateId,
    missingFields: Object.freeze([...value.missingFields]),
    updatedAt: value.updatedAt,
    sourceOnly: freezeSourceOnlyProjectSummary(value.sourceOnly),
    ...('autoSettings' in value
      ? { autoSettings: value.autoSettings === null ? null : Object.freeze((value.autoSettings as readonly ProjectAutoSetting[])
          .map(item => Object.freeze({ key: item.key, label: item.label, valueText: item.valueText, reason: item.reason }))) }
      : {}),
  })
}

function isAutoSettingArray(value: unknown): value is readonly ProjectAutoSetting[] {
  const text = (item: unknown): boolean => typeof item === 'string' && item.length > 0 && item.length <= 512
  return Array.isArray(value) && value.length <= 32 && value.every(item =>
    isRecordWithExactKeys(item, ['key', 'label', 'valueText', 'reason']) &&
    text(item.key) && text(item.label) && text(item.valueText) && text(item.reason))
}

function isNullOrSourceOnlyProjectSummary(
  value: unknown,
): value is SourceOnlyProjectSummary | null {
  return value === null || (
    isRecordWithExactKeys(value, [
      'profileId',
      'profileVersion',
      'profileDigest',
      'settingsSnapshotDigest',
      'artifactSet',
    ]) &&
    isEntityId(value.profileId) &&
    typeof value.profileVersion === 'string' &&
    settingsProfileVersionPattern.test(value.profileVersion) &&
    isContentDigest(value.profileDigest) &&
    isContentDigest(value.settingsSnapshotDigest) &&
    isUniqueNonemptyStringArray(value.artifactSet)
  )
}

function freezeSourceOnlyProjectSummary(
  value: SourceOnlyProjectSummary | null,
): SourceOnlyProjectSummary | null {
  return value === null
    ? null
    : Object.freeze({
        ...value,
        artifactSet: Object.freeze([...value.artifactSet]),
      })
}

function freezeDraftInput(input: ProjectDraftInput): ProjectDraftInput {
  return Object.freeze({
    ...input,
    outputProfileIds: Object.freeze([...input.outputProfileIds]),
    performerNames: Object.freeze([...input.performerNames]),
    deadlineSelection: freezeDeadlineInput(input.deadlineSelection),
  })
}

function freezeDeadlineInput(value: ProjectDeadlineInput): ProjectDeadlineInput {
  return value === null ? null : Object.freeze({ ...value })
}

function isDeadlineInput(
  value: unknown,
  nativeTimestampOnly = false,
): value is ProjectDeadlineInput {
  if (value === null) {
    return true
  }

  if (
    !isRecordWithExactKeys(value, ['hasDeadline', 'value']) ||
    typeof value.hasDeadline !== 'boolean'
  ) {
    return false
  }

  if (!value.hasDeadline) {
    return value.value === null
  }

  return nativeTimestampOnly
    ? isNativeTimestamp(value.value)
    : isInputTimestamp(value.value)
}

function isInputTimestamp(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    (nativeTimestampPattern.test(value) || browserTimestampPattern.test(value)) &&
    Number.isFinite(Date.parse(value))
  )
}

function isNativeTimestamp(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    nativeTimestampPattern.test(value) &&
    Number.isFinite(Date.parse(value))
  )
}

// Hosts that rename projects send displayName; older payloads omit it.
function withOptionalDisplayName(value: unknown, keys: readonly string[]): readonly string[] {
  return isRecord(value) && 'displayName' in value ? [...keys, 'displayName'] : keys
}

function isOptionalDisplayName(value: Record<string, unknown>): boolean {
  return !('displayName' in value) || isNullOrString(value.displayName)
}

function isRecordWithExactKeys(
  value: unknown,
  expectedKeys: readonly string[],
): value is Record<string, unknown> {
  return isRecord(value) && hasExactKeys(Object.keys(value), expectedKeys)
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function hasExactKeys(keys: readonly string[], expectedKeys: readonly string[]): boolean {
  return (
    keys.length === expectedKeys.length &&
    expectedKeys.every((key) => keys.includes(key))
  )
}

function isNullOrString(value: unknown): value is string | null {
  return value === null || typeof value === 'string'
}

function isEntityId(value: unknown): value is string {
  return typeof value === 'string' && entityIdPattern.test(value)
}

function isNullOrEntityId(value: unknown): value is string | null {
  return value === null || isEntityId(value)
}

function isPublicationScope(value: unknown): value is PublicationScope {
  return publicationScopes.has(value as PublicationScope)
}

function isNullOrPublicationScope(value: unknown): value is PublicationScope | null {
  return value === null || isPublicationScope(value)
}

function isSourceStatus(value: unknown): value is SourceStatus {
  return sourceStatuses.has(value as SourceStatus)
}

function isUniqueMissingFieldArray(
  value: unknown,
): value is ProjectCreationMissingField[] {
  return (
    Array.isArray(value) &&
    value.every((item) => missingFields.has(item as ProjectCreationMissingField)) &&
    new Set(value).size === value.length
  )
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string')
}

function isUniqueStringArray(value: unknown): value is string[] {
  return isStringArray(value) && new Set(value).size === value.length
}

function isUniqueNonemptyStringArray(value: unknown): value is string[] {
  return (
    isUniqueStringArray(value) &&
    value.length > 0 &&
    value.every((item) => item.length > 0)
  )
}

function isContentDigest(value: unknown): value is string {
  return typeof value === 'string' && contentDigestPattern.test(value)
}

function isUniqueEntityIdArray(value: unknown): value is string[] {
  return (
    Array.isArray(value) &&
    value.every(isEntityId) &&
    new Set(value).size === value.length
  )
}

function isNonnegativeInt32(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= 2147483647
  )
}

function isProjectBridgeMethod(value: unknown): value is ProjectBridgeMethod {
  return projectBridgeMethods.has(value as ProjectBridgeMethod)
}

function isProjectBridgeErrorCode(value: unknown): value is ProjectBridgeErrorCode {
  return projectBridgeErrorCodes.has(value as ProjectBridgeErrorCode)
}

function requireEntityId(value: string, description: string): void {
  if (!entityIdPattern.test(value)) {
    throw new Error(`${description} must be a non-empty lowercase UUID.`)
  }
}

function hasUniqueIds<T>(items: readonly T[], selectId: (item: T) => string): boolean {
  return new Set(items.map(selectId)).size === items.length
}

function isProjectRequestWithinLimit(request: ProjectBridgeRequest): boolean {
  return utf8Length(JSON.stringify(request)) <= MAX_BRIDGE_REQUEST_UTF8_BYTES
}

function utf8Length(value: string): number {
  return new TextEncoder().encode(value).byteLength
}

const entityIdPattern =
  /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const nativeTimestampPattern =
  /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}\+00:00$/
const browserTimestampPattern =
  /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$/
const contentDigestPattern = /^[0-9a-f]{64}$/
const settingsProfileVersionPattern = /^[0-9]+\.[0-9]+\.[0-9]+$/

const projectBridgeMethods = new Set<ProjectBridgeMethod>([
  PROJECT_LIST_METHOD,
  PROJECT_CREATE_DRAFT_METHOD,
  PROJECT_LOAD_DRAFT_METHOD,
  PROJECT_SAVE_DRAFT_METHOD,
  PROJECT_PICK_SOURCE_METHOD,
  PROJECT_FINALIZE_DRAFT_METHOD,
  PROJECT_DELETE_METHOD,
  PROJECT_RENAME_METHOD,
])

const publicationScopes = new Set<PublicationScope>([
  'internalOnly',
  'specificGroupOnly',
  'externalCandidate',
  'externalPublicationApproved',
  'publicationForbidden',
  'pendingDecision',
])

const sourceStatuses = new Set<SourceStatus>([
  'notSelected',
  'available',
  'reselectionRequired',
])

const missingFields = new Set<ProjectCreationMissingField>([
  'sourceMedia',
  'contentName',
  'purpose',
  'audience',
  'publicationScope',
  'outputFormats',
  'language',
  'performerNames',
  'deadline',
  'brandTemplate',
])

const projectBridgeErrorCodes = new Set<ProjectBridgeErrorCode>([
  'project.already_finalized',
  'project.catalog_unavailable',
  'project.delete_blocked',
  'project.incomplete',
  'project.invalid_request',
  'project.not_authorized',
  'project.not_found',
  'project.operation_failed',
  'project.revision_conflict',
  'project.runtime_unavailable',
  'project.settings_mismatch',
  'project.settings_unavailable',
  'project.source_selection_canceled',
  'project.source_reselection_required',
  'project.unsafe_source_name',
])