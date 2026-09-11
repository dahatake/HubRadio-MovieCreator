import { describe, expect, expectTypeOf, it } from 'vitest'
import { snapshot } from './settingsProtocol.test'
import { createSettingsReadRequest, createSettingsGlobalReadRequest, createSettingsUpdateRequest, createSettingsGlobalUpdateRequest, createSettingsResetRequest, createSettingsGlobalResetRequest, createSettingsResetAllRequest, createSettingsGlobalResetAllRequest, createSettingsFileExportRequest, createSettingsFilePreviewRequest, createSettingsFileApplyRequest, parseSettingsWorkspaceResponse, type AnySettingsSnapshot, type GlobalSettingsSnapshot, type SettingValue, type SettingsFileStatus, type SettingsSnapshot, type SettingsWorkspaceRequest, type SettingsWorkspaceResponse } from './settingsProtocol'
import type { WorkspaceSessionEvent, WorkspaceSessionRequest } from './settingsBridgeSession'
import { initialSettingsState, settingsProjectChanged, settingsRequestStarted, settingsResponseReceived, settingsStateReducer, initialWorkspaceSettingsState, workspaceSettingsReducer, type SettingsState, type WorkspaceSettingsState } from './settingsState'

describe('settingsStateReducer', () => {
  it('requires refresh after conflict and accepts a refreshed snapshot', () => {
    const conflict = settingsStateReducer(initialSettingsState, settingsResponseReceived({ version: 1, requestId: snapshot.projectId, method: 'settings.update', result: { status: 'conflict', snapshot: null } }))
    expect(conflict.needsRefresh).toBe(true)
    expect(conflict.error).toBe('conflict')
    const refreshed = settingsStateReducer(conflict, settingsResponseReceived({ version: 1, requestId: snapshot.projectId, method: 'settings.read', result: { status: 'available', snapshot } }))
    expect(refreshed.needsRefresh).toBe(false)
    expect(refreshed.snapshot).toBe(snapshot)
  })

  it('clears pending state and the old snapshot when the project changes', () => {
    const pending = settingsStateReducer(initialSettingsState, settingsRequestStarted('settings.read'))
    const changed = settingsStateReducer(pending, settingsProjectChanged('new-project'))

    expect(changed.projectIdInput).toBe('new-project')
    expect(changed.pendingMethod).toBeNull()
    expect(changed.snapshot).toBeNull()
  })
})

const requestId = '01010101-2222-4333-8444-555555555555'
const nextRequestId = '02020202-2222-4333-8444-555555555555'
const otherProjectId = 'cccccccc-dddd-4eee-8fff-aaaaaaaaaaaa'
const key = 'subtitles.enabled'
const otherKey = 'editing.cutCount'
const thirdKey = 'layout.name'
const enabled: SettingValue = { kind: 'boolean', booleanValue: true }
const disabled: SettingValue = { kind: 'boolean', booleanValue: false }
const four: SettingValue = { kind: 'integer', integerValue: '4' }
const five: SettingValue = { kind: 'integer', integerValue: '5' }
const fileSha256 = 'd'.repeat(64)
const otherFileSha256 = 'e'.repeat(64)
const projectSnapshot: SettingsSnapshot = {
  ...snapshot, revision: 9, definitionCount: 3,
  definitions: [snapshot.definitions[0]!,
    { ...snapshot.definitions[0]!, key: otherKey, group: 'editing', defaultValue: { kind: 'integer', integerValue: '0' } },
    { ...snapshot.definitions[0]!, key: thirdKey, group: 'projectAndOutput', defaultValue: { kind: 'text', textValue: 'standard' } },
  ],
  initialValues: [{ key, value: disabled }, { key: otherKey, value: { kind: 'integer', integerValue: '0' } }, { key: thirdKey, value: { kind: 'text', textValue: 'standard' } }],
  values: [{ key, value: enabled }, { key: otherKey, value: { kind: 'integer', integerValue: '0' } }, { key: thirdKey, value: { kind: 'text', textValue: 'standard' } }],
}
const globalSnapshot: GlobalSettingsSnapshot = {
  ...projectSnapshot, projectId: null, revision: 0, snapshotDigest: 'b'.repeat(64),
  initialValues: projectSnapshot.definitions.map(definition => ({ key: definition.key, value: definition.defaultValue })),
}
const scopes: readonly { scope: string; target: AnySettingsSnapshot }[] = [
  { scope: 'global', target: globalSnapshot }, { scope: 'project', target: projectSnapshot },
]

describe('workspaceSettingsReducer scope and correlation', () => {
  it('keeps the legacy snapshot type narrow and starts in unloaded global scope', () => {
    expectTypeOf<SettingsState['snapshot']>().toEqualTypeOf<SettingsSnapshot | null>()
    expectTypeOf<WorkspaceSettingsState['snapshot']>().toEqualTypeOf<AnySettingsSnapshot | null>()
    expectTypeOf<WorkspaceSettingsState['projectId']>().toEqualTypeOf<string | null>()
    expect(initialWorkspaceSettingsState).toMatchObject({ scope: 'global', projectId: null, snapshot: null, pendingRequest: null, draftValues: {}, importPreview: null, fileSha256: null })
    const loaded = loadState(globalSnapshot)
    expect(loaded.snapshot).toBe(globalSnapshot)
    expect(loaded.snapshot?.revision).toBe(0)
    expect(loaded.snapshot?.initialValues).toEqual(globalSnapshot.definitions.map(definition => ({ key: definition.key, value: definition.defaultValue })))
    expect(loaded.lastCompletedMethod).toBe('settings.global.read')
  })

  it('holds only the current scope and never carries a global revision or cached project into another scope', () => {
    let state = loadState(globalSnapshot)
    for (const target of [projectSnapshot, { ...projectSnapshot, projectId: otherProjectId, revision: 23 }, globalSnapshot]) {
      state = workspaceSettingsReducer(state, { type: 'scopeChanged', projectId: target.projectId })
      expect(state.snapshot).toBeNull()
      expect(state.scope).toBe(target.projectId === null ? 'global' : 'project')
      expect(state.projectId).toBe(target.projectId)
      expect(state.pendingRequest).toBeNull()
      const request = readRequest(target)
      state = respond(start(state, request), request, target)
      expect(state.snapshot).toBe(target)
      expect(state.snapshot?.revision).toBe(target.revision)
    }
    expect(globalSnapshot.revision).toBe(0)
    expect(projectSnapshot.revision).toBe(9)
  })

  it('guards dirty switching, leaves the same scope alone and switches only after explicit discard', () => {
    const dirty = change(loadState(globalSnapshot), otherKey, four)
    const request = createSettingsFilePreviewRequest(requestId, globalSnapshot)
    const pending = start(dirty, request)
    expect(workspaceSettingsReducer(pending, { type: 'scopeChanged', projectId: projectSnapshot.projectId })).toBe(pending)
    expect(workspaceSettingsReducer(pending, { type: 'scopeChanged', projectId: null })).toBe(pending)
    expect(pending.pendingRequest).toMatchObject({ requestId, method: request.method, projectId: null })
    const discarded = workspaceSettingsReducer(pending, { type: 'draftsDiscarded' })
    expect(discarded.snapshot).toBe(globalSnapshot)
    const changed = workspaceSettingsReducer(discarded, { type: 'scopeChanged', projectId: projectSnapshot.projectId })
    expect(changed).toEqual({ ...initialWorkspaceSettingsState, scope: 'project', projectId: projectSnapshot.projectId })
    expect(respond(changed, request, globalSnapshot)).toBe(changed)
  })

  it.each(['', 'global', '00000000-0000-0000-0000-000000000000', `${otherProjectId}\n`, otherProjectId.toUpperCase()])('rejects noncanonical project scope %j without dropping saved state', projectId => {
    const loaded = loadState(globalSnapshot)
    const invalid = workspaceSettingsReducer(loaded, { type: 'scopeChanged', projectId })
    expect(invalid.error).toBe('invalid')
    expect(invalid.projectId).toBeNull()
    expect(invalid.snapshot).toBe(globalSnapshot)
  })

  it('ignores unknown drafts and removes an explicit edit back to the saved value when idle', () => {
    const loaded = loadState(globalSnapshot)
    expect(change(loaded, 'unknown.key', disabled)).toBe(loaded)
    const dirty = change(loaded, key, disabled)
    expect(change(dirty, key, enabled).draftValues).toEqual({})
    expect(change(initialWorkspaceSettingsState, key, disabled)).toBe(initialWorkspaceSettingsState)
  })

  it('requires matching session and response IDs, methods and scope, and ignores duplicate or abandoned events', () => {
    const request = createSettingsFilePreviewRequest(requestId, globalSnapshot)
    const pending = start(loadState(globalSnapshot), request)
    const response = available(request, globalSnapshot)
    const metadata = requestMetadata(request)
    for (const mismatched of [
      { ...metadata, requestId: nextRequestId },
      { ...metadata, method: 'settings.file.export' as const },
      { ...metadata, projectId: projectSnapshot.projectId },
    ]) {
      expect(event(pending, { type: 'response', request: mismatched, response })).toBe(pending)
      expect(event(pending, { type: 'failure', request: mismatched, code: 'timeout' })).toBe(pending)
      expect(event(pending, { type: 'requested', request: mismatched })).toBe(pending)
    }
    expect(event(pending, { type: 'response', request: metadata, response: { ...response, requestId: nextRequestId } })).toBe(pending)
    expect(event(pending, { type: 'response', request: metadata, response: available(createSettingsFileExportRequest(requestId, globalSnapshot, null), globalSnapshot) })).toBe(pending)
    expect(respond(pending, request, projectSnapshot)).toBe(pending)
    expect(start(pending, { ...request, requestId: nextRequestId })).toBe(pending)
    expect(start(loadState(globalSnapshot), readRequest(projectSnapshot))).toEqual(loadState(globalSnapshot))
    const complete = respond(pending, request, globalSnapshot)
    expect(respond(complete, request, globalSnapshot)).toBe(complete)
    expect(event(complete, { type: 'requested', request: metadata })).toBe(complete)
    const switched = workspaceSettingsReducer(pending, { type: 'scopeChanged', projectId: projectSnapshot.projectId })
    const next = readRequest(projectSnapshot, nextRequestId)
    const nextPending = start(switched, next)
    expect(respond(nextPending, request, globalSnapshot)).toBe(nextPending)
    expect(event(nextPending, { type: 'failure', request: metadata, code: 'unavailable' })).toBe(nextPending)
    expect(respond(nextPending, next, { ...projectSnapshot, projectId: otherProjectId })).toBe(nextPending)
    expect(respond(nextPending, next, projectSnapshot).snapshot).toBe(projectSnapshot)
  })
})

describe('workspaceSettingsReducer drafts', () => {
  it.each(scopes)('clears only the acknowledged $scope saved key, even if unrelated drafts equal returned DB values', ({ target }) => {
    const dirty = change(change(loadState(target), key, disabled), otherKey, four)
    const request = updateRequest(target, key, disabled)
    const pending = start(dirty, request)
    expect(pending.draftValues).toBe(dirty.draftValues)
    expect(pending.pendingRequest).toMatchObject({ requestId, method: request.method, projectId: target.projectId, key, draftValues: { [key]: disabled } })
    const updated = withValue(withValue(target, key, disabled), otherKey, four)
    const saved = respond(pending, request, updated)
    expect(saved.snapshot).toBe(updated)
    expect(saved.draftValues).toEqual({ [otherKey]: four })
    expect(saved.lastCompletedMethod).toBe(request.method)
    expect(target.values.find(item => item.key === key)?.value).toEqual(enabled)
  })

  it.each(scopes)('retains a $scope edit back to the old DB value while that key is being saved', ({ target }) => {
    const request = updateRequest(target, key, disabled)
    const pending = start(change(loadState(target), key, disabled), request)
    const editedAgain = change(pending, key, enabled)
    expect(editedAgain.draftValues[key]).toEqual(enabled)
    const saved = respond(editedAgain, request, withValue(target, key, disabled))
    expect(saved.draftValues[key]).toEqual(enabled)
    expect(saved.snapshot?.values.find(item => item.key === key)?.value).toEqual(disabled)
  })

  it('compares every value kind structurally without coercion and captures mutable inputs by value', () => {
    const cases: readonly { initial: SettingValue; draft: SettingValue }[] = [
      { initial: enabled, draft: disabled },
      { initial: { kind: 'integer', integerValue: '0' }, draft: { kind: 'integer', integerValue: '9007199254740993' } },
      { initial: { kind: 'rational', numerator: '0', denominator: '1' }, draft: { denominator: '3', numerator: '1', kind: 'rational' } },
      { initial: { kind: 'text', textValue: 'initial' }, draft: { kind: 'text', textValue: 'updated' } },
      { initial: { kind: 'textList', textListValue: [] }, draft: { kind: 'textList', textListValue: ['one', 'two'] } },
    ]
    for (const { initial, draft } of cases) {
      const target: GlobalSettingsSnapshot = {
        ...globalSnapshot, definitionCount: 1,
        definitions: [{ ...globalSnapshot.definitions[0]!, defaultValue: initial }],
        values: [{ key, value: initial }], initialValues: [{ key, value: initial }],
      }
      const request = updateRequest(target, key, draft)
      const pending = start(change(loadState(target), key, draft), request)
      const equivalent = change(pending, key, { ...draft })
      expect(respond(equivalent, request, withValue(target, key, draft)).draftValues).toEqual({})
    }
    const list: string[] = ['sent']
    const listTarget: GlobalSettingsSnapshot = {
      ...globalSnapshot, definitionCount: 1,
      definitions: [{ ...globalSnapshot.definitions[0]!, defaultValue: { kind: 'textList', textListValue: [] } }],
      values: [{ key, value: { kind: 'textList', textListValue: [] } }], initialValues: [{ key, value: { kind: 'textList', textListValue: [] } }],
    }
    const value: SettingValue = { kind: 'textList', textListValue: list }
    const request = updateRequest(listTarget, key, value)
    const pending = start(change(loadState(listTarget), key, value), request)
    list.push('changed by caller')
    expect(pending.draftValues[key]).toEqual({ kind: 'textList', textListValue: ['sent'] })
    expect(pending.pendingRequest?.draftValues[key]).toEqual({ kind: 'textList', textListValue: ['sent'] })
  })

  it.each(scopes)('keeps every $scope draft across refresh and clears only an explicitly reset key', ({ target }) => {
    const dirty = change(change(loadState(target), key, disabled), otherKey, four)
    const read = readRequest(target)
    const refreshed = respond(start(dirty, read), read, target)
    expect(refreshed.draftValues).toBe(dirty.draftValues)
    const reset = target.projectId === null ? createSettingsGlobalResetRequest(requestId, target, key) : createSettingsResetRequest(requestId, target, key)
    const initialValue = target.initialValues.find(item => item.key === key)!.value
    const pending = start(refreshed, reset)
    expect(pending.draftValues).toBe(dirty.draftValues)
    const resetState = respond(pending, reset, withValue(target, key, initialValue))
    expect(resetState.draftValues).toEqual({ [otherKey]: four })
    expect(resetState.snapshot?.initialValues).toBe(target.initialValues)
    expect(respond(change(pending, key, enabled), reset, withValue(target, key, initialValue)).draftValues[key]).toEqual(enabled)
  })

  it.each(scopes)('defers $scope reset-all draft removal until success and preserves edits made during it', ({ target }) => {
    const dirty = change(change(loadState(target), key, disabled), otherKey, four)
    const request = target.projectId === null ? createSettingsGlobalResetAllRequest(requestId, target) : createSettingsResetAllRequest(requestId, target)
    const pending = start(dirty, request)
    expect(pending.draftValues).toBe(dirty.draftValues)
    const failed = receiveFailure(pending, request, 'invalid')
    expect(failed.draftValues).toBe(dirty.draftValues)
    expect(failed.snapshot).toBe(target)
    const resetSnapshot = { ...target, revision: target.revision + 1, values: target.initialValues }
    expect(respond(pending, request, resetSnapshot).draftValues).toEqual({})
    const edited = change(change(pending, otherKey, five), thirdKey, { kind: 'text', textValue: 'portrait' })
    expect(respond(edited, request, resetSnapshot).draftValues).toEqual({ [otherKey]: five, [thirdKey]: { kind: 'text', textValue: 'portrait' } })
  })

  describe.each(scopes)('$scope ABA acknowledgements', ({ target }) => {
    it.each(['reset', 'resetAll', 'applyFile'] as const)('preserves a 4 -> 5 -> 4 edit during %s and clears only unchanged captured drafts', operation => {
      let ready = change(change(loadState(target), key, disabled), otherKey, four)
      const updated = operation === 'reset'
        ? withValue(target, otherKey, { kind: 'integer', integerValue: '0' })
        : { ...target, revision: target.revision + 1, snapshotDigest: 'c'.repeat(64), values: target.initialValues }
      if (operation === 'applyFile') {
        const preview = createSettingsFilePreviewRequest(requestId, target)
        ready = respond(start(ready, preview), preview, { ...updated, revision: target.revision })
      }
      const request = operation === 'reset'
        ? (target.projectId === null ? createSettingsGlobalResetRequest(nextRequestId, target, otherKey) : createSettingsResetRequest(nextRequestId, target, otherKey))
        : operation === 'resetAll'
          ? (target.projectId === null ? createSettingsGlobalResetAllRequest(nextRequestId, target) : createSettingsResetAllRequest(nextRequestId, target))
          : createSettingsFileApplyRequest(nextRequestId, target, fileSha256)
      const captured = ready.draftValues[otherKey]
      expect(captured).toEqual(four)
      expect(captured).not.toBe(four)
      const pending = start(ready, request)
      expect(pending.pendingRequest?.draftValues[otherKey]).toBe(captured)
      expect(respond(pending, request, updated).draftValues).toEqual(operation === 'reset' ? { [key]: disabled } : {})

      // Reuse the caller's original object: each actual edit must get a new identity.
      const edited = change(change(pending, otherKey, five), otherKey, four)
      expect(edited.draftValues[otherKey]).toEqual(captured)
      expect(edited.draftValues[otherKey]).not.toBe(captured)
      const completed = respond(edited, request, updated)
      expect(completed.snapshot).toBe(updated)
      expect(completed.snapshot?.values.find(item => item.key === otherKey)?.value).toEqual({ kind: 'integer', integerValue: '0' })
      expect(completed.draftValues).toEqual(operation === 'reset' ? { [key]: disabled, [otherKey]: four } : { [otherKey]: four })
      expect(completed.draftValues[otherKey]).toBe(edited.draftValues[otherKey])
      expect(completed.pendingRequest).toBeNull()
    })

    it('clears a saved ABA draft only when it matches the acknowledged DB value', () => {
      const dirty = change(change(loadState(target), key, disabled), otherKey, four)
      const request = updateRequest(target, otherKey, four)
      const pending = start(dirty, request)
      const edited = change(pending, otherKey, five)
      const aba = change(edited, otherKey, four)
      const saved = withValue(target, otherKey, four)
      expect(respond(aba, request, saved).draftValues).toEqual({ [key]: disabled })
      expect(respond(edited, request, saved).draftValues).toEqual({ [key]: disabled, [otherKey]: five })
      const differentAcknowledgement = withValue(target, otherKey, { kind: 'integer', integerValue: '0' })
      expect(respond(aba, request, differentAcknowledgement).draftValues).toEqual({ [key]: disabled, [otherKey]: four })
    })
  })

  it('does not recreate explicitly discarded drafts from an in-flight acknowledgement', () => {
    const request = updateRequest(globalSnapshot, key, disabled)
    const pending = start(change(loadState(globalSnapshot), key, disabled), request)
    const discarded = workspaceSettingsReducer(pending, { type: 'draftsDiscarded' })
    expect(discarded.snapshot).toBe(globalSnapshot)
    expect(respond(discarded, request, withValue(globalSnapshot, key, disabled)).draftValues).toEqual({})
  })
})

describe('workspaceSettingsReducer files and failures', () => {
  it.each(scopes)('separates $scope import candidates from saved DB state until explicit apply succeeds', ({ target }) => {
    const dirty = change(loadState(target), otherKey, four)
    const preview = createSettingsFilePreviewRequest(requestId, target)
    const candidate = { ...withValue(target, key, disabled), revision: target.revision }
    const previewed = respond(start(dirty, preview), preview, candidate)
    expect(previewed.snapshot).toBe(target)
    expect(previewed.draftValues).toBe(dirty.draftValues)
    expect(previewed.importPreview).toBe(candidate)
    expect(previewed.fileSha256).toBe(fileSha256)
    // Use the saved state for apply identity, never candidate.snapshotDigest.
    const apply = createSettingsFileApplyRequest(nextRequestId, previewed.snapshot!, previewed.fileSha256!)
    expect(apply.params.expectedSnapshotDigest).toBe(target.snapshotDigest)
    const pending = start(previewed, apply)
    expect(pending.snapshot).toBe(target)
    expect(pending.draftValues).toBe(dirty.draftValues)
    const appliedSnapshot = { ...candidate, revision: target.revision + 1 }
    expect(respond(pending, apply, appliedSnapshot).draftValues).toEqual({})
    const applied = respond(change(pending, otherKey, five), apply, appliedSnapshot)
    expect(applied.snapshot).toBe(appliedSnapshot)
    expect(applied.draftValues).toEqual({ [otherKey]: five })
    expect(applied.importPreview).toBeNull()
    expect(applied.fileSha256).toBe(fileSha256)
  })

  it('keeps overwrite warnings and export hashes separate from drafts and the old DB snapshot', () => {
    const dirty = change(loadState(globalSnapshot), key, disabled)
    const request = createSettingsFileExportRequest(requestId, globalSnapshot, null)
    const warning = receiveFailure(start(dirty, request), request, 'overwriteRequired', otherFileSha256)
    expect(warning).toMatchObject({ error: 'overwriteRequired', exportNeedsConfirmation: true, fileSha256: otherFileSha256, importPreview: null, lastCompletedMethod: null })
    expect(warning.snapshot).toBe(globalSnapshot)
    expect(warning.draftValues).toBe(dirty.draftValues)
    const dismissedError = workspaceSettingsReducer(warning, { type: 'errorCleared' })
    expect(dismissedError.exportNeedsConfirmation).toBe(true)
    expect(dismissedError.fileSha256).toBe(otherFileSha256)
    const retry = createSettingsFileExportRequest(nextRequestId, globalSnapshot, otherFileSha256)
    const exported = respond(start(dismissedError, retry), retry, { ...globalSnapshot })
    expect(exported.snapshot).toBe(globalSnapshot)
    expect(exported.draftValues).toBe(dirty.draftValues)
    expect(exported).toMatchObject({ exportNeedsConfirmation: false, fileSha256, importPreview: null, error: null, lastCompletedMethod: 'settings.file.export' })
    const cleared = workspaceSettingsReducer(warning, { type: 'fileCleared' })
    expect(cleared).toMatchObject({ importPreview: null, fileSha256: null, exportNeedsConfirmation: false })
    expect(cleared.snapshot).toBe(globalSnapshot)
    expect(cleared.draftValues).toBe(dirty.draftValues)
  })

  it.each(['forbidden', 'notFound', 'unbound', 'conflict', 'invalid', 'unavailable', 'fileNotFound', 'profileMismatch', 'snapshotMismatch', 'overwriteRequired'] as const)('preserves saved state/drafts and rejects stale preview metadata on file status %s', status => {
    const dirty = change(loadState(globalSnapshot), key, disabled)
    const request = createSettingsFilePreviewRequest(requestId, globalSnapshot)
    const failed = receiveFailure(start(dirty, request), request, status)
    expect(failed.snapshot).toBe(globalSnapshot)
    expect(failed.draftValues).toBe(dirty.draftValues)
    expect(failed).toMatchObject({ error: status, pendingRequest: null, importPreview: null, fileSha256: null, exportNeedsConfirmation: false, lastCompletedMethod: null, needsRefresh: status === 'conflict' })
  })

  it('retains conflict drafts through error dismissal and an explicit refresh', () => {
    const dirty = change(loadState(globalSnapshot), key, disabled)
    const request = updateRequest(globalSnapshot, key, disabled)
    const conflict = receiveFailure(start(dirty, request), request, 'conflict')
    expect(conflict.snapshot).toBe(globalSnapshot)
    expect(conflict.draftValues).toBe(dirty.draftValues)
    const dismissed = workspaceSettingsReducer(conflict, { type: 'errorCleared' })
    expect(dismissed.error).toBeNull()
    expect(dismissed.needsRefresh).toBe(true)
    const read = readRequest(globalSnapshot, nextRequestId)
    const refreshed = respond(start(dismissed, read), read, { ...globalSnapshot, revision: 1 })
    expect(refreshed.needsRefresh).toBe(false)
    expect(refreshed.draftValues).toBe(dirty.draftValues)
  })

  it.each(['invalid', 'unavailable', 'timeout'] as const)('retains drafts on local %s and requires a read after an uncertain write', code => {
    const dirty = change(loadState(globalSnapshot), key, disabled)
    const request = updateRequest(globalSnapshot, key, disabled)
    const pending = start(dirty, request)
    const failed = event(pending, { type: 'failure', request: requestMetadata(request), code })
    expect(failed.snapshot).toBe(globalSnapshot)
    expect(failed.draftValues).toBe(dirty.draftValues)
    expect(failed).toMatchObject({ error: code, pendingRequest: null, lastCompletedMethod: null, needsRefresh: code !== 'invalid' })
    expect(respond(failed, request, withValue(globalSnapshot, key, disabled))).toBe(failed)
    const read = readRequest(globalSnapshot)
    expect(event(start(dirty, read), { type: 'failure', request: requestMetadata(read), code }).needsRefresh).toBe(false)
    expect(workspaceSettingsReducer(pending, { type: 'requestRejected' })).toBe(pending)
    expect(workspaceSettingsReducer(dirty, { type: 'requestRejected' }).draftValues).toBe(dirty.draftValues)
  })

  it('handles setup failures without fabricating a loaded snapshot or clearing a live request', () => {
    const failure: WorkspaceSessionEvent = { type: 'failure', request: null, code: 'unavailable' }
    expect(event(initialWorkspaceSettingsState, failure)).toMatchObject({ error: 'unavailable', snapshot: null })
    const pending = start(initialWorkspaceSettingsState, readRequest(globalSnapshot))
    expect(event(pending, failure)).toBe(pending)
  })

  it('invalidates preview bindings on a saved-state refresh, and ignores file responses after explicit clear', () => {
    const dirty = change(loadState(globalSnapshot), otherKey, four)
    const preview = createSettingsFilePreviewRequest(requestId, globalSnapshot)
    const previewed = respond(start(dirty, preview), preview, globalSnapshot)
    const read = readRequest(globalSnapshot, nextRequestId)
    const refreshed = respond(start(previewed, read), read, globalSnapshot)
    expect(refreshed).toMatchObject({ importPreview: null, fileSha256: null, exportNeedsConfirmation: false })
    expect(refreshed.draftValues).toBe(dirty.draftValues)
    const cancelled = workspaceSettingsReducer(start(dirty, preview), { type: 'fileCleared' })
    expect(cancelled.pendingRequest).toBeNull()
    expect(cancelled.needsRefresh).toBe(false)
    expect(respond(cancelled, preview, globalSnapshot)).toBe(cancelled)
    const apply = createSettingsFileApplyRequest(nextRequestId, globalSnapshot, fileSha256)
    const clearedApply = workspaceSettingsReducer(start(previewed, apply), { type: 'fileCleared' })
    expect(clearedApply.needsRefresh).toBe(true)
    expect(clearedApply.draftValues).toBe(dirty.draftValues)
    expect(respond(clearedApply, apply, { ...globalSnapshot, revision: 1 })).toBe(clearedApply)
  })
})

function readRequest(target: AnySettingsSnapshot, id = requestId): SettingsWorkspaceRequest {
  return target.projectId === null ? createSettingsGlobalReadRequest(id) : createSettingsReadRequest(id, target.projectId)
}
function updateRequest(target: AnySettingsSnapshot, settingKey: string, value: SettingValue): SettingsWorkspaceRequest {
  return target.projectId === null ? createSettingsGlobalUpdateRequest(requestId, target, settingKey, value) : createSettingsUpdateRequest(requestId, target, settingKey, value)
}
function requestMetadata(request: SettingsWorkspaceRequest): WorkspaceSessionRequest {
  return { requestId: request.requestId, method: request.method, projectId: 'projectId' in request.params ? request.params.projectId : null }
}
function start(state: WorkspaceSettingsState, request: SettingsWorkspaceRequest): WorkspaceSettingsState {
  return workspaceSettingsReducer(state, { type: 'requestStarted', request })
}
function event(state: WorkspaceSettingsState, sessionEvent: WorkspaceSessionEvent): WorkspaceSettingsState {
  return workspaceSettingsReducer(state, { type: 'sessionEvent', event: sessionEvent })
}
function change(state: WorkspaceSettingsState, settingKey: string, value: SettingValue): WorkspaceSettingsState {
  return workspaceSettingsReducer(state, { type: 'draftChanged', key: settingKey, value })
}
function available(request: SettingsWorkspaceRequest, target: AnySettingsSnapshot, hash = fileSha256): SettingsWorkspaceResponse {
  // Validate fixture shape, but keep the original snapshot reference for nonmutation assertions.
  const wire = { version: 1, requestId: request.requestId, method: request.method, result: request.method.startsWith('settings.file.') ? { status: 'available', snapshot: target, fileSha256: hash } : { status: 'available', snapshot: target } }
  const parsed = parseSettingsWorkspaceResponse(wire)
  if (parsed === null) throw new Error('Invalid workspace settings fixture.')
  return { ...parsed, result: { ...parsed.result, status: 'available', snapshot: target } } as SettingsWorkspaceResponse
}
function respond(state: WorkspaceSettingsState, request: SettingsWorkspaceRequest, target: AnySettingsSnapshot): WorkspaceSettingsState {
  return event(state, { type: 'response', request: requestMetadata(request), response: available(request, target) })
}
function receiveFailure(state: WorkspaceSettingsState, request: SettingsWorkspaceRequest, status: Exclude<SettingsFileStatus, 'available'>, hash = fileSha256): WorkspaceSettingsState {
  const response = parseSettingsWorkspaceResponse({
    version: 1, requestId: request.requestId, method: request.method,
    result: request.method.startsWith('settings.file.') ? { status, snapshot: null, fileSha256: status === 'overwriteRequired' ? hash : null } : { status, snapshot: null },
  })
  if (response === null) throw new Error('Invalid workspace settings failure fixture.')
  return event(state, { type: 'response', request: requestMetadata(request), response })
}
function loadState(target: AnySettingsSnapshot): WorkspaceSettingsState {
  const state = workspaceSettingsReducer(initialWorkspaceSettingsState, { type: 'scopeChanged', projectId: target.projectId })
  const request = readRequest(target)
  return respond(start(state, request), request, target)
}
function withValue(target: AnySettingsSnapshot, settingKey: string, value: SettingValue): AnySettingsSnapshot {
  return { ...target, revision: target.revision + 1, snapshotDigest: 'c'.repeat(64), values: target.values.map(selection => selection.key === settingKey ? { key: settingKey, value } : selection) }
}
