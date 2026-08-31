import { describe, expect, it } from 'vitest'

import {
  PROJECT_CREATE_DRAFT_METHOD,
  PROJECT_FINALIZE_DRAFT_METHOD,
  PROJECT_LIST_METHOD,
  PROJECT_LOAD_DRAFT_METHOD,
  PROJECT_PICK_SOURCE_METHOD,
  PROJECT_SAVE_DRAFT_METHOD,
  type ProjectBridgeErrorResponse,
  type ProjectBridgeResponse,
  type ProjectDraft,
  type ProjectDraftInput,
  type ProjectListResponse,
} from '../../bridge'
import {
  emptyProjectInput,
  getMissingProjectFields,
  getProjectInputSourceStatus,
  initialProjectState,
  isProjectInputComplete,
  isProjectInputDirty,
  isProjectInputFinalizable,
  isProjectInputSaveable,
  projectDraftClosed,
  projectInputChanged,
  projectRequestStarted,
  projectResponseReceived,
  projectStateReducer,
  type ProjectAction,
  type ProjectState,
} from './projectState'

const requestId = '00112233-4455-6677-8899-aabbccddeeff'
const staleRequestId = '10112233-4455-6677-8899-aabbccddeeff'
const draftId = '11112222-3333-4444-8555-666677778888'
const otherDraftId = '21112222-3333-4444-8555-666677778888'
const projectId = '22223333-4444-4555-8666-777788889999'
const candidateId = '99990000-aaaa-4bbb-8ccc-ddddeeeeffff'
const replacementCandidateId = '89990000-aaaa-4bbb-8ccc-ddddeeeeffff'
const profileId = '14b4f6b6-1ebc-55c3-9682-2c7e9ad226fa'
const templateId = 'bf08c776-0f97-5373-a091-c70b60acd569'
const timestamp = '2026-09-01T12:00:00.0000000+00:00'

describe('project state reducer', () => {
  it('starts with only canonical state and all ten required fields missing', () => {
    expect(initialProjectState).toEqual({
      projects: [],
      catalog: { outputProfiles: [], brandTemplates: [] },
      canCreate: false,
      draft: null,
      input: emptyProjectInput,
      pending: null,
      error: null,
      sourcePickerCanceled: false,
    })
    expect(getMissingProjectFields(emptyProjectInput)).toEqual([
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
    expect(isProjectInputComplete(emptyProjectInput)).toBe(false)
  })

  it('recognizes a complete ten-field input without storing derived state', () => {
    const input = completeInput()
    const state = reduce(projectInputChanged(input))

    expect(getMissingProjectFields(state.input)).toEqual([])
    expect(isProjectInputComplete(state.input)).toBe(true)
    expect(Object.keys(state).sort()).toEqual(
      Object.keys(initialProjectState).sort(),
    )
  })

  it('requires a live source token for finalization and accepts a fresh replacement', () => {
    const input = completeInput()
    const sourceLostDraft = {
      ...draft(),
      sourceStatus: 'reselectionRequired' as const,
    }
    const lostStatus = getProjectInputSourceStatus(input, sourceLostDraft)

    expect(lostStatus).toBe('reselectionRequired')
    expect(getMissingProjectFields(input, lostStatus)).toEqual(['sourceMedia'])
    expect(isProjectInputFinalizable(input, sourceLostDraft)).toBe(false)

    const replacement = {
      ...input,
      sourceCandidateId: replacementCandidateId,
    }
    expect(getProjectInputSourceStatus(replacement, sourceLostDraft)).toBe('available')
    expect(isProjectInputFinalizable(replacement, sourceLostDraft)).toBe(true)
  })

  it('allows valid incomplete drafts but blocks native-invalid intermediate text', () => {
    expect(isProjectInputSaveable(emptyProjectInput)).toBe(true)
    expect(isProjectInputSaveable({
      ...emptyProjectInput,
      contentName: ' trailing ',
    })).toBe(false)
    expect(isProjectInputSaveable({
      ...emptyProjectInput,
      performerNames: ['person', 'person'],
    })).toBe(false)
    expect(isProjectInputSaveable({
      ...emptyProjectInput,
      purpose: '\u200d\ufe0f',
    })).toBe(false)
    expect(isProjectInputSaveable({
      ...emptyProjectInput,
      audience: '界'.repeat(40_000),
    })).toBe(false)
    expect(isProjectInputComplete({
      ...completeInput(),
      language: ' ja-JP',
    })).toBe(false)
  })

  it('correlates request ID and method and ignores stale or duplicate responses', () => {
    const pending = reduce(
      projectRequestStarted(requestId, PROJECT_LIST_METHOD),
    )
    const staleId = projectStateReducer(
      pending,
      projectResponseReceived(listResponse(staleRequestId)),
    )
    const staleMethod = projectStateReducer(
      pending,
      projectResponseReceived({
        version: 1,
        requestId,
        method: PROJECT_PICK_SOURCE_METHOD,
        result: { candidateId },
      }),
    )

    expect(staleId).toBe(pending)
    expect(staleMethod).toBe(pending)

    const accepted = projectStateReducer(
      pending,
      projectResponseReceived(listResponse(requestId)),
    )
    expect(accepted.projects).toHaveLength(2)
    expect(accepted.canCreate).toBe(true)
    expect(accepted.pending).toBeNull()
    expect(
      projectStateReducer(
        accepted,
        projectResponseReceived(listResponse(requestId)),
      ),
    ).toBe(accepted)
  })

  it.each([
    PROJECT_CREATE_DRAFT_METHOD,
    PROJECT_LOAD_DRAFT_METHOD,
    PROJECT_SAVE_DRAFT_METHOD,
    PROJECT_FINALIZE_DRAFT_METHOD,
  ] as const)('loads canonical draft input for %s', (method) => {
    const response = draftResponse(method, draft())
    const state = reduce(
      projectRequestStarted(requestId, method),
      projectResponseReceived(response),
    )

    expect(state.draft).toEqual(draft())
    expect(state.input).toEqual(completeInput())
    expect(state.projects[0]).toMatchObject({
      draftId,
      contentName: 'Hubらじお',
      outputFormats: [],
    })
  })

  it('upserts and deterministically sorts drafts by timestamp then draft ID', () => {
    const listed = reduce(
      projectRequestStarted(requestId, PROJECT_LIST_METHOD),
      projectResponseReceived(listResponse(requestId)),
    )
    const updated = projectStateReducer(
      {
        ...listed,
        pending: {
          requestId,
          method: PROJECT_SAVE_DRAFT_METHOD,
        },
      },
      projectResponseReceived(
        draftResponse(PROJECT_SAVE_DRAFT_METHOD, {
          ...draft(),
          updatedAt: '2026-09-03T12:00:00.0000000+00:00',
        }),
      ),
    )

    expect(updated.projects.map((project) => project.draftId)).toEqual([
      draftId,
      otherDraftId,
    ])
    expect(updated.projects.filter((project) => project.draftId === draftId)).toHaveLength(1)
  })

  it('derives dirty state and closes a draft without changing the project list', () => {
    const loaded = reduce(
      projectRequestStarted(requestId, PROJECT_LOAD_DRAFT_METHOD),
      projectResponseReceived(
        draftResponse(PROJECT_LOAD_DRAFT_METHOD, draft()),
      ),
    )
    expect(isProjectInputDirty(loaded.input, loaded.draft)).toBe(false)

    const changed = projectStateReducer(
      loaded,
      projectInputChanged({ ...loaded.input, contentName: 'Changed' }),
    )
    expect(isProjectInputDirty(changed.input, changed.draft)).toBe(true)

    const closed = projectStateReducer(changed, projectDraftClosed())
    expect(closed.draft).toBeNull()
    expect(closed.input).toBe(emptyProjectInput)
    expect(closed.projects).toBe(changed.projects)
  })

  it('keeps the stale draft on OCC conflict so the caller can explicitly reload it', () => {
    const loaded = reduce(
      projectRequestStarted(requestId, PROJECT_LOAD_DRAFT_METHOD),
      projectResponseReceived(
        draftResponse(PROJECT_LOAD_DRAFT_METHOD, draft()),
      ),
      projectRequestStarted(requestId, PROJECT_SAVE_DRAFT_METHOD),
    )
    const conflicted = projectStateReducer(
      loaded,
      projectResponseReceived(
        errorResponse(PROJECT_SAVE_DRAFT_METHOD, 'project.revision_conflict'),
      ),
    )

    expect(conflicted.draft?.revision).toBe(2)
    expect(conflicted.input).toEqual(completeInput())
    expect(conflicted.error?.code).toBe('project.revision_conflict')
    expect(conflicted.pending).toBeNull()
  })

  it('preserves native authorization failure without exposing private detail', () => {
    const state = reduce(
      projectRequestStarted(requestId, PROJECT_LIST_METHOD),
      projectResponseReceived(
        errorResponse(PROJECT_LIST_METHOD, 'project.not_authorized'),
      ),
    )

    expect(state.error).toEqual({
      code: 'project.not_authorized',
      message: 'この操作を実行する権限がありません。',
    })
    expect(JSON.stringify(state)).not.toContain('S-1-5-')
    expect(JSON.stringify(state)).not.toContain('C:\\')
  })

  it('keeps the previous source on cancellation and replaces it only with an opaque token', () => {
    const withInput = reduce(projectInputChanged(completeInput()))
    const canceled = projectStateReducer(
      {
        ...withInput,
        pending: { requestId, method: PROJECT_PICK_SOURCE_METHOD },
      },
      projectResponseReceived({
        version: 1,
        requestId,
        method: PROJECT_PICK_SOURCE_METHOD,
        result: { candidateId: null },
      }),
    )

    expect(canceled.input.sourceCandidateId).toBe(candidateId)
    expect(canceled.sourcePickerCanceled).toBe(true)

    const replaced = projectStateReducer(
      {
        ...canceled,
        pending: { requestId, method: PROJECT_PICK_SOURCE_METHOD },
      },
      projectResponseReceived({
        version: 1,
        requestId,
        method: PROJECT_PICK_SOURCE_METHOD,
        result: { candidateId: replacementCandidateId },
      }),
    )
    expect(replaced.input.sourceCandidateId).toBe(replacementCandidateId)
    expect(JSON.stringify(replaced)).not.toContain('source.mov')
  })
})

function reduce(...actions: readonly ProjectAction[]): ProjectState {
  return actions.reduce<ProjectState>(projectStateReducer, initialProjectState)
}

function completeInput(): ProjectDraftInput {
  return {
    sourceCandidateId: candidateId,
    contentName: 'Hubらじお',
    purpose: 'knowledge sharing',
    audience: 'developers',
    publicationScope: 'internalOnly',
    outputProfileIds: [profileId],
    language: 'ja-JP',
    performerNames: ['person'],
    deadlineSelection: { hasDeadline: false, value: null },
    brandTemplateId: templateId,
  }
}

function draft(): ProjectDraft {
  return {
    draftId,
    revision: 2,
    isFinalized: false,
    projectId: null,
    ...completeInput(),
    sourceStatus: 'available',
    missingFields: [],
    updatedAt: timestamp,
  }
}

function draftResponse<M extends
  | typeof PROJECT_CREATE_DRAFT_METHOD
  | typeof PROJECT_LOAD_DRAFT_METHOD
  | typeof PROJECT_SAVE_DRAFT_METHOD
  | typeof PROJECT_FINALIZE_DRAFT_METHOD>(
  method: M,
  value: ProjectDraft,
): ProjectBridgeResponse {
  return {
    version: 1,
    requestId,
    method,
    result: { draft: value },
  }
}

function listResponse(id: string): ProjectListResponse {
  return {
    version: 1,
    requestId: id,
    method: PROJECT_LIST_METHOD,
    result: {
      projects: [
        {
          draftId,
          projectId: null,
          state: 'draft',
          contentName: null,
          episodeNumber: null,
          sourceStatus: 'notSelected',
          owner: null,
          publicationScope: null,
          editState: null,
          reviewState: null,
          outputFormats: [],
          updatedAt: timestamp,
        },
        {
          draftId: otherDraftId,
          projectId,
          state: 'finalized',
          contentName: 'Finalized',
          episodeNumber: null,
          sourceStatus: 'reselectionRequired',
          owner: null,
          publicationScope: 'externalCandidate',
          editState: null,
          reviewState: null,
          outputFormats: ['vertical-short-1080x1920'],
          updatedAt: '2026-09-02T12:00:00.0000000+00:00',
        },
      ],
      catalog: {
        outputProfiles: [{ id: profileId, name: 'long-form-1280x720' }],
        brandTemplates: [{ id: templateId, name: 'development-neutral-long-form' }],
      },
      canCreate: true,
    },
  }
}

function errorResponse(
  method: ProjectBridgeErrorResponse['method'],
  code: ProjectBridgeErrorResponse['error']['code'],
): ProjectBridgeErrorResponse {
  const messages: Record<ProjectBridgeErrorResponse['error']['code'], string> = {
    'project.already_finalized': 'この案件はすでに作成済みです。',
    'project.incomplete': '必須入力をすべて完了してください。',
    'project.invalid_request': '入力内容を確認してください。',
    'project.not_authorized': 'この操作を実行する権限がありません。',
    'project.not_found': '指定された下書きが見つかりません。',
    'project.operation_failed': '案件操作を完了できませんでした。',
    'project.revision_conflict': '下書きが別の操作で更新されました。再読み込みしてください。',
    'project.source_reselection_required': '元動画をもう一度選択してください。',
  }
  return {
    version: 1,
    requestId,
    method,
    error: { code, message: messages[code] },
  }
}