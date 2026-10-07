import {
  PROJECT_CREATE_DRAFT_METHOD,
  PROJECT_DELETE_METHOD,
  PROJECT_FINALIZE_DRAFT_METHOD,
  PROJECT_LIST_METHOD,
  PROJECT_LOAD_DRAFT_METHOD,
  PROJECT_PICK_SOURCE_METHOD,
  PROJECT_RENAME_METHOD,
  PROJECT_SAVE_DRAFT_METHOD,
  type ProjectBridgeErrorCode,
  type ProjectBridgeMethod,
  type ProjectBridgeResponse,
  type ProjectCatalog,
  type ProjectCreationMissingField,
  type ProjectDraft,
  type ProjectDraftInput,
  type ProjectListItem,
  type SourceStatus,
  isProjectDraftInputWithinRequestLimit,
  parseProjectDraftInput,
} from '../../bridge'

export interface PendingProjectRequest {
  readonly requestId: string
  readonly method: ProjectBridgeMethod
}

export interface ProjectUiError {
  readonly code: ProjectBridgeErrorCode
  readonly message: string
}

export interface ProjectState {
  readonly projects: readonly ProjectListItem[]
  readonly catalog: ProjectCatalog
  readonly canCreate: boolean
  readonly draft: ProjectDraft | null
  readonly input: ProjectDraftInput
  readonly pending: PendingProjectRequest | null
  readonly error: ProjectUiError | null
  /** A completed operation that needs the user's attention (not an error). */
  readonly notice: string | null
  readonly sourcePickerCanceled: boolean
}

export type ProjectAction =
  | Readonly<{
      type: 'requestStarted'
      requestId: string
      method: ProjectBridgeMethod
    }>
  | Readonly<{ type: 'responseReceived'; response: ProjectBridgeResponse }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'inputChanged'; input: ProjectDraftInput }>
  | Readonly<{ type: 'draftClosed' }>
  | Readonly<{ type: 'errorCleared' }>

export const filesNotRemovedNotice =
  'Projectは一覧から削除しましたが、一部のファイルを削除できませんでした。ファイルが他のアプリで使用中の可能性があります。'

export const emptyProjectInput: ProjectDraftInput = Object.freeze({
  sourceCandidateId: null,
  contentName: null,
  purpose: null,
  audience: null,
  publicationScope: null,
  outputProfileIds: Object.freeze([]),
  language: null,
  performerNames: Object.freeze([]),
  deadlineSelection: null,
  brandTemplateId: null,
})

const emptyCatalog: ProjectCatalog = Object.freeze({
  outputProfiles: Object.freeze([]),
  brandTemplates: Object.freeze([]),
})

export const initialProjectState: ProjectState = Object.freeze({
  projects: Object.freeze([]),
  catalog: emptyCatalog,
  canCreate: false,
  draft: null,
  input: emptyProjectInput,
  pending: null,
  error: null,
  notice: null,
  sourcePickerCanceled: false,
})

export function projectRequestStarted(
  requestId: string,
  method: ProjectBridgeMethod,
): ProjectAction {
  return { type: 'requestStarted', requestId, method }
}

export function projectResponseReceived(response: ProjectBridgeResponse): ProjectAction {
  return { type: 'responseReceived', response }
}

export function projectBridgeUnavailable(): ProjectAction {
  return { type: 'bridgeUnavailable' }
}

export function projectInputChanged(input: ProjectDraftInput): ProjectAction {
  return { type: 'inputChanged', input }
}

export function projectDraftClosed(): ProjectAction {
  return { type: 'draftClosed' }
}

export function projectErrorCleared(): ProjectAction {
  return { type: 'errorCleared' }
}

/** The name shown to the user: the renamed display name, else the name derived from the source. */
export function getProjectDisplayName(
  project: Readonly<{ displayName?: string | null; contentName: string | null }>,
): string | null {
  return project.displayName ?? project.contentName
}

export function projectStateReducer(
  state: ProjectState,
  action: ProjectAction,
): ProjectState {
  switch (action.type) {
    case 'requestStarted':
      return {
        ...state,
        pending: {
          requestId: action.requestId,
          method: action.method,
        },
        error: null,
        notice: null,
        sourcePickerCanceled: false,
      }

    case 'responseReceived': {
      const pending = state.pending
      const response = action.response
      if (
        pending === null ||
        pending.requestId !== response.requestId ||
        pending.method !== response.method
      ) {
        return state
      }

      if ('error' in response) {
        return {
          ...state,
          pending: null,
          error: response.error,
          sourcePickerCanceled: false,
        }
      }

      switch (response.method) {
        case PROJECT_LIST_METHOD:
          return {
            ...state,
            projects: sortProjects(response.result.projects),
            catalog: response.result.catalog,
            canCreate: response.result.canCreate,
            pending: null,
            error: null,
          }

        case PROJECT_CREATE_DRAFT_METHOD:
        case PROJECT_LOAD_DRAFT_METHOD:
        case PROJECT_SAVE_DRAFT_METHOD:
        case PROJECT_FINALIZE_DRAFT_METHOD: {
          const draft = response.result.draft
          return {
            ...state,
            projects: upsertProject(state.projects, state.catalog, draft),
            draft,
            input: inputFromDraft(draft),
            pending: null,
            error: null,
            sourcePickerCanceled: false,
          }
        }

        // Only the display name changes, so unsaved input edits stay as they are.
        case PROJECT_RENAME_METHOD: {
          const draft = response.result.draft
          return {
            ...state,
            projects: upsertProject(state.projects, state.catalog, draft),
            draft: state.draft?.draftId === draft.draftId ? draft : state.draft,
            pending: null,
            error: null,
            sourcePickerCanceled: false,
          }
        }

        case PROJECT_DELETE_METHOD: {
          const deletedDraftId = response.result.deletedDraftId
          const openDraftDeleted = state.draft?.draftId === deletedDraftId
          return {
            ...state,
            projects: state.projects.filter((project) => project.draftId !== deletedDraftId),
            // Deleting the open project returns to the list instead of leaving a dead detail screen.
            draft: openDraftDeleted ? null : state.draft,
            input: openDraftDeleted ? emptyProjectInput : state.input,
            pending: null,
            error: null,
            notice: response.result.filesRemoved ? null : filesNotRemovedNotice,
            sourcePickerCanceled: false,
          }
        }

        case PROJECT_PICK_SOURCE_METHOD:
          if (response.result.candidateId === null) {
            return {
              ...state,
              pending: null,
              error: null,
              sourcePickerCanceled: true,
            }
          }

          return {
            ...state,
            input: {
              ...state.input,
              sourceCandidateId: response.result.candidateId,
            },
            pending: null,
            error: null,
            sourcePickerCanceled: false,
          }
      }
    }

    case 'bridgeUnavailable':
      return {
        ...state,
        pending: null,
        error: {
          code: 'project.operation_failed',
          message: 'ホストとの接続を確認できませんでした。',
        },
        sourcePickerCanceled: false,
      }

    case 'inputChanged':
      return {
        ...state,
        input: action.input,
        error: null,
        sourcePickerCanceled: false,
      }

    case 'draftClosed':
      return {
        ...state,
        draft: null,
        input: emptyProjectInput,
        pending: null,
        error: null,
        sourcePickerCanceled: false,
      }

    case 'errorCleared':
      return {
        ...state,
        error: null,
      }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}

export function getMissingProjectFields(
  input: ProjectDraftInput,
  sourceStatus: SourceStatus = input.sourceCandidateId === null
    ? 'notSelected'
    : 'available',
): readonly ProjectCreationMissingField[] {
  const missing: ProjectCreationMissingField[] = []
  if (sourceStatus !== 'available') missing.push('sourceMedia')
  if (!hasSubstantiveText(input.contentName)) missing.push('contentName')
  if (!hasSubstantiveText(input.purpose)) missing.push('purpose')
  if (!hasSubstantiveText(input.audience)) missing.push('audience')
  if (input.publicationScope === null) missing.push('publicationScope')
  if (input.outputProfileIds.length === 0) missing.push('outputFormats')
  if (!hasSubstantiveText(input.language)) missing.push('language')
  if (
    input.performerNames.length === 0 ||
    input.performerNames.some((name) => !hasSubstantiveText(name))
  ) {
    missing.push('performerNames')
  }
  if (input.deadlineSelection === null) missing.push('deadline')
  if (input.brandTemplateId === null) missing.push('brandTemplate')
  return Object.freeze(missing)
}

export function isProjectInputComplete(input: ProjectDraftInput): boolean {
  return isProjectInputSaveable(input) && getMissingProjectFields(input).length === 0
}

export function getProjectInputSourceStatus(
  input: ProjectDraftInput,
  draft: ProjectDraft | null,
): SourceStatus {
  if (input.sourceCandidateId === null) {
    return 'notSelected'
  }

  return input.sourceCandidateId === draft?.sourceCandidateId
    ? draft.sourceStatus
    : 'available'
}

export function isProjectInputFinalizable(
  input: ProjectDraftInput,
  draft: ProjectDraft | null,
): boolean {
  const sourceStatus = getProjectInputSourceStatus(input, draft)
  return (
    isProjectInputSaveable(input) &&
    getMissingProjectFields(input, sourceStatus).length === 0
  )
}

export function isProjectInputSaveable(input: ProjectDraftInput): boolean {
  if (parseProjectDraftInput(input) === null) {
    return false
  }

  const optionalText = [
    input.contentName,
    input.purpose,
    input.audience,
    input.language,
  ]
  if (
    !isProjectDraftInputWithinRequestLimit(input) ||
    optionalText.some(
      (value) => value !== null && !isValidDomainText(value),
    ) ||
    input.performerNames.some(
      (value) => !isValidDomainText(value),
    ) ||
    new Set(input.performerNames).size !== input.performerNames.length
  ) {
    return false
  }

  return true
}

export function isProjectInputDirty(
  input: ProjectDraftInput,
  draft: ProjectDraft | null,
): boolean {
  if (draft === null) {
    return false
  }

  return (
    input.sourceCandidateId !== draft.sourceCandidateId ||
    input.contentName !== draft.contentName ||
    input.purpose !== draft.purpose ||
    input.audience !== draft.audience ||
    input.publicationScope !== draft.publicationScope ||
    !sameStrings(input.outputProfileIds, draft.outputProfileIds) ||
    input.language !== draft.language ||
    !sameStrings(input.performerNames, draft.performerNames) ||
    !sameDeadline(input.deadlineSelection, draft.deadlineSelection) ||
    input.brandTemplateId !== draft.brandTemplateId
  )
}

function inputFromDraft(draft: ProjectDraft): ProjectDraftInput {
  return {
    sourceCandidateId: draft.sourceCandidateId,
    contentName: draft.contentName,
    purpose: draft.purpose,
    audience: draft.audience,
    publicationScope: draft.publicationScope,
    outputProfileIds: draft.outputProfileIds,
    language: draft.language,
    performerNames: draft.performerNames,
    deadlineSelection: draft.deadlineSelection,
    brandTemplateId: draft.brandTemplateId,
  }
}

function upsertProject(
  projects: readonly ProjectListItem[],
  catalog: ProjectCatalog,
  draft: ProjectDraft,
): readonly ProjectListItem[] {
  const namesById = new Map(
    catalog.outputProfiles.map((option) => [option.id, option.name] as const),
  )
  const previous = projects.find((project) => project.draftId === draft.draftId)
  const item: ProjectListItem = {
    draftId: draft.draftId,
    projectId: draft.projectId,
    state: draft.isFinalized ? 'finalized' : 'draft',
    contentName: draft.contentName,
    displayName: draft.displayName ?? null,
    episodeNumber: null,
    sourceStatus: draft.sourceStatus,
    owner: null,
    publicationScope: draft.publicationScope,
    editState: null,
    reviewState: null,
    outputFormats: draft.outputProfileIds.flatMap((id) => {
      const name = namesById.get(id)
      return name === undefined ? [] : [name]
    }),
    updatedAt: draft.updatedAt,
    ...(previous?.thumbnail === undefined ? {} : { thumbnail: previous.thumbnail }),
    ...(previous?.poster === undefined ? {} : { poster: previous.poster }),
  }
  const withoutCurrent = projects.filter((project) => project.draftId !== draft.draftId)
  return sortProjects([...withoutCurrent, item])
}

function sortProjects(
  projects: readonly ProjectListItem[],
): readonly ProjectListItem[] {
  return Object.freeze(
    [...projects].sort(
      (left, right) =>
        right.updatedAt.localeCompare(left.updatedAt) ||
        left.draftId.localeCompare(right.draftId),
    ),
  )
}

function hasSubstantiveText(value: string | null): boolean {
  return value !== null && value.trim().length > 0
}

function isValidDomainText(value: string): boolean {
  if (value.length === 0 || value !== value.trim()) {
    return false
  }

  let hasSubstantiveScalar = false
  for (const scalar of value) {
    const codePoint = scalar.codePointAt(0)
    if (
      codePoint === undefined ||
      (codePoint >= 0xd800 && codePoint <= 0xdfff)
    ) {
      return false
    }

    if (
      !nonSubstantiveCategoryPattern.test(scalar) &&
      !isNonSubstantiveSpecialScalar(codePoint)
    ) {
      hasSubstantiveScalar = true
    }
  }

  return hasSubstantiveScalar
}

function isNonSubstantiveSpecialScalar(value: number): boolean {
  return (
    value === 0x115f ||
    value === 0x1160 ||
    value === 0x2800 ||
    value === 0x3164 ||
    value === 0xffa0 ||
    (value >= 0xfe00 && value <= 0xfe0f) ||
    (value >= 0xe0100 && value <= 0xe01ef)
  )
}

const nonSubstantiveCategoryPattern =
  /[\p{Cc}\p{Cf}\p{Mn}\p{Mc}\p{Me}\p{Zs}\p{Zl}\p{Zp}]/u

function sameStrings(left: readonly string[], right: readonly string[]): boolean {
  return (
    left.length === right.length &&
    left.every((value, index) => value === right[index])
  )
}

function sameDeadline(
  left: ProjectDraftInput['deadlineSelection'],
  right: ProjectDraftInput['deadlineSelection'],
): boolean {
  return (
    left === right ||
    (left !== null &&
      right !== null &&
      left.hasDeadline === right.hasDeadline &&
      left.value === right.value)
  )
}