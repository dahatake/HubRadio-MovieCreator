import { describe, expect, it } from 'vitest'

import {
  PROJECT_CREATE_DRAFT_METHOD,
  PROJECT_DELETE_METHOD,
  PROJECT_FINALIZE_DRAFT_METHOD,
  PROJECT_LIST_METHOD,
  PROJECT_LOAD_DRAFT_METHOD,
  PROJECT_PICK_SOURCE_METHOD,
  PROJECT_RENAME_METHOD,
  PROJECT_SAVE_DRAFT_METHOD,
  createProjectCreateDraftRequest,
  createProjectDeleteRequest,
  createProjectFinalizeDraftRequest,
  createProjectListRequest,
  createProjectLoadDraftRequest,
  createProjectPickSourceRequest,
  createProjectRenameRequest,
  createProjectSaveDraftRequest,
  isProjectDraftInputWithinRequestLimit,
  normalizeProjectDisplayName,
  parseProjectBridgeResponse,
  parseProjectDraftInput,
  type ProjectDraftInput,
} from './projectProtocol'

const requestId = '00112233-4455-6677-8899-aabbccddeeff'
const draftId = '11112222-3333-4444-8555-66667777aabb'
const projectId = '22223333-4444-4555-8666-777788889999'
const candidateId = '99990000-aaaa-4bbb-8ccc-ddddeeeeffff'
const profileId = '14b4f6b6-1ebc-55c3-9682-2c7e9ad226fa'
const templateId = 'bf08c776-0f97-5373-a091-c70b60acd569'
const timestamp = '2026-09-01T12:00:00.0000000+00:00'

describe('project bridge requests', () => {
  it('constructs only the six allowlisted exact request shapes', () => {
    const input = completeInput()
    const requests = [
      createProjectListRequest(requestId),
      createProjectCreateDraftRequest(requestId),
      createProjectLoadDraftRequest(requestId, draftId),
      createProjectSaveDraftRequest(requestId, draftId, 2, input),
      createProjectPickSourceRequest(requestId),
      createProjectFinalizeDraftRequest(requestId, draftId, 2, input),
    ]

    expect(requests.map((request) => request.method)).toEqual([
      PROJECT_LIST_METHOD,
      PROJECT_CREATE_DRAFT_METHOD,
      PROJECT_LOAD_DRAFT_METHOD,
      PROJECT_SAVE_DRAFT_METHOD,
      PROJECT_PICK_SOURCE_METHOD,
      PROJECT_FINALIZE_DRAFT_METHOD,
    ])
    for (const request of requests) {
      expect(Object.isFrozen(request)).toBe(true)
      expect(Object.isFrozen(request.params)).toBe(true)
      expect(Object.keys(request).sort()).toEqual([
        'method',
        'params',
        'requestId',
        'version',
      ])
    }
  })

  it('constructs the frozen project.delete request with only a canonical draft ID', () => {
    const request = createProjectDeleteRequest(requestId, draftId)

    expect(request).toEqual({
      version: 1,
      requestId,
      method: PROJECT_DELETE_METHOD,
      params: { draftId },
    })
    expect(Object.isFrozen(request)).toBe(true)
    expect(Object.isFrozen(request.params)).toBe(true)
    expect(() => createProjectDeleteRequest(requestId, draftId.toUpperCase())).toThrow()
    expect(() => createProjectDeleteRequest(requestId, 'not-an-id')).toThrow()
    expect(() => createProjectDeleteRequest('bad', draftId)).toThrow()
  })

  // FR-040 AC-013
  it('constructs the frozen project.rename request with the trimmed display name', () => {
    const request = createProjectRenameRequest(requestId, draftId, '  社内向け 第1回  ')

    expect(request).toEqual({
      version: 1,
      requestId,
      method: PROJECT_RENAME_METHOD,
      params: { draftId, displayName: '社内向け 第1回' },
    })
    expect(Object.isFrozen(request)).toBe(true)
    expect(Object.isFrozen(request.params)).toBe(true)
    expect(() => createProjectRenameRequest(requestId, draftId.toUpperCase(), '名前')).toThrow()
    expect(() => createProjectRenameRequest('bad', draftId, '名前')).toThrow()
    expect(() => createProjectRenameRequest(requestId, draftId, '   ')).toThrow()
  })

  // FR-040 AC-014
  it('normalizes display names like the host: 1 to 100 code points, no control or bidirectional characters', () => {
    expect(normalizeProjectDisplayName(' a ')).toBe('a')
    expect(normalizeProjectDisplayName('あ'.repeat(100))).toBe('あ'.repeat(100))
    expect(normalizeProjectDisplayName('😀'.repeat(100))).toBe('😀'.repeat(100))
    expect(normalizeProjectDisplayName('e\u0301')).toBe('\u00e9')
    for (const invalid of ['', '   ', 'あ'.repeat(101), '😀'.repeat(101), 'a\nb', 'a\u0000b', 'a\u202Eb', 'a\u2066b', 'a\u2028b', 'a\u007fb', '\ud800']) {
      expect(normalizeProjectDisplayName(invalid)).toBeNull()
    }
  })

  // FR-040 AC-013
  it('accepts a project.rename draft result with a display name and rejects extra keys', () => {
    const response = draftResponse(PROJECT_RENAME_METHOD)
    const renamed = {
      ...response,
      result: { draft: { ...response.result.draft, displayName: '別の名前' } },
    }
    expect(parseProjectBridgeResponse(renamed)).toEqual(renamed)
    expect(parseProjectBridgeResponse({ ...renamed, result: { draft: { ...renamed.result.draft, displayName: null } } })).not.toBeNull()
    expect(parseProjectBridgeResponse({ ...renamed, result: { draft: { ...renamed.result.draft, displayName: 1 } } })).toBeNull()
    expect(parseProjectBridgeResponse({ ...renamed, result: { draft: { ...renamed.result.draft, extra: 1 } } })).toBeNull()
  })

  it('deep-copies and freezes mutation input', () => {
    const input = completeInput()
    const request = createProjectSaveDraftRequest(requestId, draftId, 0, input)

    expect(request.params.input).not.toBe(input)
    expect(Object.isFrozen(request.params.input)).toBe(true)
    expect(Object.isFrozen(request.params.input.outputProfileIds)).toBe(true)
    expect(Object.isFrozen(request.params.input.performerNames)).toBe(true)
    expect(Object.isFrozen(request.params.input.deadlineSelection)).toBe(true)
  })

  it.each([
    ['', draftId, 0],
    [requestId.toUpperCase(), draftId, 0],
    [requestId, draftId.toUpperCase(), 0],
    [requestId, draftId, -1],
    [requestId, draftId, 2147483648],
    [requestId, draftId, 0.5],
  ])('rejects invalid IDs or revisions', (request, draft, revision) => {
    expect(() =>
      createProjectSaveDraftRequest(
        request as string,
        draft as string,
        revision as number,
        completeInput(),
      ),
    ).toThrow()
  })

  it('rejects open, forged, duplicate, and inconsistent draft inputs', () => {
    const exact = completeInput()
    expect(parseProjectDraftInput(exact)).not.toBeNull()
    expect(parseProjectDraftInput({ ...exact, extra: true })).toBeNull()
    expect(parseProjectDraftInput({ ...exact, publicationScope: 'InternalOnly' })).toBeNull()
    expect(
      parseProjectDraftInput({
        ...exact,
        outputProfileIds: [profileId, profileId],
      }),
    ).toBeNull()
    expect(
      parseProjectDraftInput({
        ...exact,
        performerNames: ['person', 'person'],
      }),
    ).toBeNull()
    expect(
      parseProjectDraftInput({
        ...exact,
        deadlineSelection: { hasDeadline: false, value: timestamp },
      }),
    ).toBeNull()
    expect(() =>
      createProjectFinalizeDraftRequest(
        requestId,
        draftId,
        0,
        { ...exact, extra: true } as ProjectDraftInput,
      ),
    ).toThrow('closed bridge schema')
  })

  it('rejects a mutation before posting when its UTF-8 envelope exceeds 96 KiB', () => {
    const oversized = {
      ...completeInput(),
      contentName: '界'.repeat(40_000),
    }

    expect(isProjectDraftInputWithinRequestLimit(oversized)).toBe(false)
    expect(() =>
      createProjectSaveDraftRequest(requestId, draftId, 0, oversized),
    ).toThrow('UTF-8 byte limit')
  })
})

describe('project bridge responses', () => {
  // FR-045 AC-020.3
  it('accepts a closed list thumbnail reference and rejects forged ones', () => {
    const base = listResponse()
    const finalized = { ...base.result.projects[0], state: 'finalized', projectId: draftId }
    const thumbnail = { executionId: draftId, artifactId: profileId, contentDigest: 'c'.repeat(64) }
    const withThumbnail = (value: unknown, project: object = finalized) => ({
      ...base,
      result: { ...base.result, projects: [{ ...project, thumbnail: value }] },
    })

    const accepted = withThumbnail(thumbnail)
    expect(parseProjectBridgeResponse(accepted)).toEqual(accepted)
    expect(parseProjectBridgeResponse(withThumbnail(null))).not.toBeNull()
    expect(parseProjectBridgeResponse(base)).not.toBeNull()
    for (const forged of [
      { ...thumbnail, extra: 1 },
      { ...thumbnail, contentDigest: 'C'.repeat(64) },
      { ...thumbnail, contentDigest: 'c'.repeat(63) },
      { ...thumbnail, artifactId: draftId.toUpperCase() },
      { executionId: draftId, artifactId: profileId },
      'https://player-media.appassets.example/x',
      undefined,
    ]) {
      expect(parseProjectBridgeResponse(withThumbnail(forged))).toBeNull()
    }
    expect(parseProjectBridgeResponse(withThumbnail(thumbnail, base.result.projects[0]))).toBeNull()
  })

  it('accepts an exact truthful list and keeps unknown columns null', () => {
    const response = listResponse()
    const parsed = parseProjectBridgeResponse(response)

    expect(parsed).toEqual(response)
    expect(parsed).not.toBe(response)
    if (parsed === null || !('result' in parsed) || parsed.method !== PROJECT_LIST_METHOD) {
      throw new Error('Expected a list response.')
    }

    expect(parsed.result.projects[0]).toMatchObject({
      episodeNumber: null,
      owner: null,
      editState: null,
      reviewState: null,
    })
    expect(Object.isFrozen(parsed.result.projects)).toBe(true)
    expect(Object.isFrozen(parsed.result.catalog.outputProfiles)).toBe(true)
  })

  it('accepts draft results for create, load, save, and finalize only', () => {
    for (const method of [
      PROJECT_CREATE_DRAFT_METHOD,
      PROJECT_LOAD_DRAFT_METHOD,
      PROJECT_SAVE_DRAFT_METHOD,
      PROJECT_FINALIZE_DRAFT_METHOD,
    ] as const) {
      const response = draftResponse(method)
      expect(parseProjectBridgeResponse(response)).toEqual(response)
    }

    expect(
      parseProjectBridgeResponse({
        ...draftResponse(PROJECT_SAVE_DRAFT_METHOD),
        method: PROJECT_PICK_SOURCE_METHOD,
      }),
    ).toBeNull()
  })

  it('accepts a closed source-only summary and rejects forged metadata', () => {
    const response = draftResponse(PROJECT_CREATE_DRAFT_METHOD)
    const sourceOnly = {
      profileId,
      profileVersion: '1.0.0',
      profileDigest: 'a'.repeat(64),
      settingsSnapshotDigest: 'b'.repeat(64),
      artifactSet: ['landscapeVideo', 'verticalShorts'],
    }

    const parsed = parseProjectBridgeResponse({
      ...response,
      result: { draft: { ...response.result.draft, sourceOnly } },
    })
    expect(parsed).not.toBeNull()
    if (parsed === null || !('result' in parsed) || parsed.method !== PROJECT_CREATE_DRAFT_METHOD) {
      throw new Error('Expected a source-only draft response.')
    }

    expect(parsed.result.draft.sourceOnly).toEqual(sourceOnly)
    expect(Object.isFrozen(parsed.result.draft.sourceOnly)).toBe(true)
    expect(Object.isFrozen(parsed.result.draft.sourceOnly?.artifactSet)).toBe(true)
    expect(
      parseProjectBridgeResponse({
        ...response,
        result: {
          draft: {
            ...response.result.draft,
            sourceOnly: { ...sourceOnly, sourcePath: 'C:\\private\\source.mov' },
          },
        },
      }),
    ).toBeNull()
    expect(
      parseProjectBridgeResponse({
        ...response,
        result: {
          draft: {
            ...response.result.draft,
            sourceOnly: { ...sourceOnly, profileDigest: 'not-a-digest' },
          },
        },
      }),
    ).toBeNull()
  })

  it('accepts opaque picker cancellation or token without any path field', () => {
    expect(
      parseProjectBridgeResponse({
        version: 1,
        requestId,
        method: PROJECT_PICK_SOURCE_METHOD,
        result: { candidateId: null },
      }),
    ).not.toBeNull()
    expect(
      parseProjectBridgeResponse({
        version: 1,
        requestId,
        method: PROJECT_PICK_SOURCE_METHOD,
        result: { candidateId, path: 'C:\\private\\source.mov' },
      }),
    ).toBeNull()
  })

  it('accepts only an exact project.delete result and the delete_blocked error', () => {
    const response = {
      version: 1,
      requestId,
      method: PROJECT_DELETE_METHOD,
      result: { deletedDraftId: draftId, filesRemoved: true },
    }
    expect(parseProjectBridgeResponse(response)).toEqual(response)
    expect(parseProjectBridgeResponse({ ...response, result: { ...response.result, filesRemoved: false } })).not.toBeNull()
    expect(parseProjectBridgeResponse({ ...response, result: { deletedDraftId: draftId } })).toBeNull()
    expect(parseProjectBridgeResponse({ ...response, result: { ...response.result, filesRemoved: 'yes' } })).toBeNull()
    expect(parseProjectBridgeResponse({ ...response, result: {} })).toBeNull()
    expect(
      parseProjectBridgeResponse({ ...response, result: { ...response.result, path: 'C:\\x' } }),
    ).toBeNull()
    expect(
      parseProjectBridgeResponse({ ...response, result: { ...response.result, deletedDraftId: draftId.toUpperCase() } }),
    ).toBeNull()
    expect(
      parseProjectBridgeResponse({ ...response, method: PROJECT_LOAD_DRAFT_METHOD }),
    ).toBeNull()

    const blocked = {
      version: 1,
      requestId,
      method: PROJECT_DELETE_METHOD,
      error: {
        code: 'project.delete_blocked',
        message: '処理中のジョブがあるため、この案件は削除できません。',
      },
    }
    expect(parseProjectBridgeResponse(blocked)).toEqual(blocked)
  })

  it('rejects extra nested keys, duplicate IDs, wrong casing, and false status claims', () => {
    const list = listResponse()
    expect(parseProjectBridgeResponse({ ...list, extra: true })).toBeNull()
    expect(
      parseProjectBridgeResponse({
        ...list,
        result: { ...list.result, extra: true },
      }),
    ).toBeNull()
    expect(
      parseProjectBridgeResponse({
        ...list,
        result: {
          ...list.result,
          catalog: {
            ...list.result.catalog,
            outputProfiles: [
              list.result.catalog.outputProfiles[0],
              list.result.catalog.outputProfiles[0],
            ],
          },
        },
      }),
    ).toBeNull()
    expect(
      parseProjectBridgeResponse({
        ...draftResponse(PROJECT_LOAD_DRAFT_METHOD),
        result: {
          draft: {
            ...draftResponse(PROJECT_LOAD_DRAFT_METHOD).result.draft,
            draftId: draftId.toUpperCase(),
          },
        },
      }),
    ).toBeNull()
    expect(
      parseProjectBridgeResponse({
        ...draftResponse(PROJECT_LOAD_DRAFT_METHOD),
        result: {
          draft: {
            ...draftResponse(PROJECT_LOAD_DRAFT_METHOD).result.draft,
            isFinalized: false,
            projectId,
          },
        },
      }),
    ).toBeNull()
  })

  it('accepts only stable closed private-safe errors', () => {
    const error = {
      version: 1,
      requestId,
      method: PROJECT_SAVE_DRAFT_METHOD,
      error: {
        code: 'project.revision_conflict',
        message: '下書きが別の操作で更新されました。再読み込みしてください。',
      },
    }
    expect(parseProjectBridgeResponse(error)).toEqual(error)
    expect(
      parseProjectBridgeResponse({
        ...error,
        error: { ...error.error, exception: 'C:\\private\\source.mov' },
      }),
    ).toBeNull()
    expect(
      parseProjectBridgeResponse({
        ...error,
        error: { code: 'future.error', message: 'detail' },
      }),
    ).toBeNull()
  })

  it('preserves adversarial user text as inert data for React interpolation', () => {
    const payload = '<script>globalThis.compromised=true</script>&tail'
    const response = draftResponse(PROJECT_LOAD_DRAFT_METHOD, payload)
    const parsed = parseProjectBridgeResponse(response)

    expect(parsed).not.toBeNull()
    if (parsed === null || !('result' in parsed) || parsed.method !== PROJECT_LOAD_DRAFT_METHOD) {
      throw new Error('Expected a draft response.')
    }

    expect(parsed.result.draft.contentName).toBe(payload)
  })
})

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

function listResponse() {
  return {
    version: 1,
    requestId,
    method: PROJECT_LIST_METHOD,
    result: {
      projects: [
        {
          draftId,
          projectId: null,
          state: 'draft',
          contentName: '<script>alert(1)</script>',
          episodeNumber: null,
          sourceStatus: 'available',
          owner: null,
          publicationScope: 'internalOnly',
          editState: null,
          reviewState: null,
          outputFormats: ['long-form-1280x720'],
          updatedAt: timestamp,
        },
      ],
      catalog: {
        outputProfiles: [{ id: profileId, name: 'long-form-1280x720' }],
        brandTemplates: [{ id: templateId, name: 'development-neutral-long-form' }],
      },
      canCreate: true,
    },
  } as const
}

function draftResponse(method: string, contentName = 'Hubらじお') {
  return {
    version: 1,
    requestId,
    method,
    result: {
      draft: {
        draftId,
        revision: 2,
        isFinalized: false,
        projectId: null,
        sourceCandidateId: candidateId,
        sourceStatus: 'available',
        contentName,
        purpose: 'knowledge sharing',
        audience: 'developers',
        publicationScope: 'internalOnly',
        outputProfileIds: [profileId],
        language: 'ja-JP',
        performerNames: ['person'],
        deadlineSelection: { hasDeadline: false, value: null },
        brandTemplateId: templateId,
        missingFields: [],
        updatedAt: timestamp,
        sourceOnly: null,
      },
    },
  }
}