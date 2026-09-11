// @vitest-environment jsdom

import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, expectTypeOf, it, vi } from 'vitest'
import { SettingsStateProvider, SettingsWorkspaceStateProvider, useSettingsState, useSettingsWorkspace, type SettingsContextValue, type SettingsWorkspaceContextValue } from './SettingsStateProvider'
import type { SettingsBridgeClient, SettingsWorkspaceBridgeClient } from './settingsBridgeSession'
import { parseSettingsWorkspaceResponse, type AnySettingsSnapshot, type GlobalSettingsSnapshot, type SettingValue, type SettingsFileStatus, type SettingsSnapshot, type SettingsWorkspaceRequest, type SettingsWorkspaceResponse } from './settingsProtocol'

const projectId = '11112222-3333-4444-8555-666677778888'
const otherProjectId = '22223333-4444-4555-8666-777788889999'
const key = 'subtitles.enabled'
const otherKey = 'editing.cutCount'
const enabled: SettingValue = { kind: 'boolean', booleanValue: true }
const disabled: SettingValue = { kind: 'boolean', booleanValue: false }
const four: SettingValue = { kind: 'integer', integerValue: '4' }
const five: SettingValue = { kind: 'integer', integerValue: '5' }
const fileSha256 = 'd'.repeat(64)
const otherFileSha256 = 'e'.repeat(64)
const projectSnapshot: SettingsSnapshot = {
  projectId, profileId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', profileName: 'Workspace fixture', profileVersion: '1.0.0',
  profileDigest: 'a'.repeat(64), initialSnapshotDigest: 'a'.repeat(64), snapshotDigest: 'b'.repeat(64), revision: 9,
  updatedAt: '2026-09-07T00:00:00.0000000+00:00', definitionCount: 2,
  definitions: [
    { key, group: 'subtitlesAndGraphics', isAdvanced: false, displayName: 'Subtitles', description: 'Enable subtitles.', unit: 'none', defaultValue: enabled, minimum: null, maximum: null, allowedTextValues: [], evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }] },
    { key: otherKey, group: 'editing', isAdvanced: true, displayName: 'Cuts', description: 'Cut count.', unit: 'count', defaultValue: { kind: 'integer', integerValue: '0' }, minimum: { kind: 'integer', integerValue: '0' }, maximum: null, allowedTextValues: [], evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }] },
  ],
  initialValues: [{ key, value: disabled }, { key: otherKey, value: { kind: 'integer', integerValue: '0' } }],
  values: [{ key, value: enabled }, { key: otherKey, value: { kind: 'integer', integerValue: '0' } }],
}
const globalSnapshot: GlobalSettingsSnapshot = {
  ...projectSnapshot, projectId: null, revision: 0, snapshotDigest: 'f'.repeat(64),
  initialValues: projectSnapshot.definitions.map(definition => ({ key: definition.key, value: definition.defaultValue })),
}
const scopes: readonly { scope: string; target: AnySettingsSnapshot }[] = [{ scope: 'global', target: globalSnapshot }, { scope: 'project', target: projectSnapshot }]
const cleanups = new Set<() => Promise<void>>()

beforeEach(() => { vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true) })

afterEach(async () => {
  try { for (const cleanup of [...cleanups]) await cleanup() }
  finally { vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals() }
})

describe('SettingsWorkspaceStateProvider', () => {
  it('exports the UX-025 API without widening the old project-only consumer', () => {
    expectTypeOf<SettingsContextValue['state']['snapshot']>().toEqualTypeOf<SettingsSnapshot | null>()
    expectTypeOf<SettingsContextValue['state']['pendingMethod']>().toEqualTypeOf<'settings.read' | 'settings.update' | 'settings.reset' | 'settings.resetAll' | null>()
    expectTypeOf<SettingsWorkspaceContextValue['setScope']>().toEqualTypeOf<(projectId: string | null) => boolean>()
    expectTypeOf<SettingsWorkspaceContextValue['changeDraft']>().toEqualTypeOf<(key: string, value: SettingValue) => void>()
    expectTypeOf<SettingsWorkspaceContextValue['save']>().toEqualTypeOf<(key: string) => boolean>()
    expectTypeOf<SettingsWorkspaceContextValue['exportFile']>().toEqualTypeOf<(expectedFileSha256: string | null) => boolean>()
    expectTypeOf<SettingsWorkspaceContextValue['state']['snapshot']>().toEqualTypeOf<AnySettingsSnapshot | null>()
  })

  it('uses one workspace client, explicitly reads global without a project and accepts revision zero', async () => {
    const harness = await mountWorkspace()
    expect(harness.createClient).toHaveBeenCalledOnce()
    expect(harness.client.onSettingsWorkspaceResponse).toHaveBeenCalledOnce()
    expect(harness.client.sendSettingsWorkspace).not.toHaveBeenCalled()
    expect(harness.read().state).toMatchObject({ scope: 'global', projectId: null, snapshot: null })
    await act(async () => { expect(harness.read().load()).toBe(true); expect(harness.read().refresh()).toBe(false) })
    const request = harness.lastRequest()
    expect(request).toMatchObject({ method: 'settings.global.read', params: {} })
    expect(harness.read().state.pendingRequest).toMatchObject({ requestId: request.requestId, method: request.method, projectId: null })
    await act(async () => { harness.emit(available(request, globalSnapshot)) })
    expect(harness.read().state.snapshot?.revision).toBe(0)
    expect(harness.read().state.snapshot?.initialValues).toEqual(globalSnapshot.initialValues)
    expect(harness.read().isDirty).toBe(false)
    expect(harness.read().state.lastCompletedMethod).toBe('settings.global.read')
  })

  it('observes consecutive draft/save actions immediately and preserves a newer edit to the saved key', async () => {
    const harness = await mountWorkspace()
    await loadTarget(harness, globalSnapshot)
    const api = harness.read()
    await act(async () => {
      api.changeDraft(key, disabled)
      api.changeDraft(otherKey, four)
      expect(api.save(key)).toBe(true)
      expect(api.save(otherKey)).toBe(false)
      api.changeDraft(key, enabled)
    })
    const request = harness.lastRequest()
    expect(request).toMatchObject({ method: 'settings.global.update', params: { ...identity(globalSnapshot), key, value: disabled } })
    expect(Object.hasOwn(request.params, 'projectId')).toBe(false)
    await act(async () => { harness.emit(available(request, withValue(globalSnapshot, key, disabled))) })
    expect(harness.read().state.draftValues).toEqual({ [key]: enabled, [otherKey]: four })
    await act(async () => { expect(api.save(key)).toBe(true) })
    const second = harness.lastRequest()
    expect(second.requestId).not.toBe(request.requestId)
    expect(second.params).toMatchObject({ expectedRevision: 1, value: enabled })
    await act(async () => { harness.emit(available(second, withValue(withValue(globalSnapshot, key, disabled), key, enabled))) })
    expect(harness.read().state.draftValues).toEqual({ [otherKey]: four })
  })

  it('keeps drafts and one session when panels are hidden, removed and remounted for another group/page', async () => {
    const harness = await mountWorkspace()
    await loadTarget(harness, globalSnapshot)
    await act(async () => { harness.read().changeDraft(key, disabled); harness.read().changeDraft(otherKey, four); harness.read().save(key) })
    const request = harness.lastRequest()
    await harness.render({ hidden: true, panelKey: 'editing-page-2' })
    expect(harness.read().state.draftValues).toEqual({ [key]: disabled, [otherKey]: four })
    await harness.render({ show: false })
    await act(async () => { harness.emit(available(request, withValue(globalSnapshot, key, disabled))) })
    await harness.render({ panelKey: 'subtitles-page-1' })
    expect(harness.read().state.draftValues).toEqual({ [otherKey]: four })
    expect(harness.read().state.snapshot?.revision).toBe(1)
    expect(harness.createClient).toHaveBeenCalledOnce()
    expect(harness.client.onSettingsWorkspaceResponse).toHaveBeenCalledOnce()
    expect(harness.client.dispose).not.toHaveBeenCalled()
  })

  it('guards dirty scope changes, then supports discard/switch/load in one event without reusing global identity', async () => {
    const harness = await mountWorkspace()
    await loadTarget(harness, globalSnapshot)
    const api = harness.read()
    await act(async () => { api.changeDraft(key, disabled); api.save(key); expect(api.setScope(projectId)).toBe(false) })
    const abandoned = harness.lastRequest()
    expect(harness.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
    expect(harness.read().state.projectId).toBeNull()
    await act(async () => { api.discardDrafts(); expect(api.setScope(projectId)).toBe(true); expect(api.load()).toBe(true) })
    const request = harness.lastRequest()
    expect(request).toMatchObject({ method: 'settings.read', params: { projectId } })
    expect(harness.client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(abandoned.requestId)
    expect(harness.read().state.snapshot).toBeNull()
    await act(async () => { harness.emit(available(abandoned, withValue(globalSnapshot, key, disabled))) })
    expect(harness.read().state.snapshot).toBeNull()
    await act(async () => { harness.emit(available(request, projectSnapshot)); api.changeDraft(key, disabled); api.save(key) })
    expect(harness.lastRequest().params).toMatchObject({ projectId, ...identity(projectSnapshot), key, value: disabled })
  })

  it('rejects invalid scopes and ignores responses from an abandoned project or a mismatched current project', async () => {
    const harness = await mountWorkspace()
    await act(async () => { expect(harness.read().setScope('global')).toBe(false) })
    expect(harness.read().state.error).toBe('invalid')
    expect(harness.client.sendSettingsWorkspace).not.toHaveBeenCalled()
    await act(async () => { harness.read().setScope(projectId); harness.read().load() })
    const abandoned = harness.lastRequest()
    await act(async () => { harness.read().setScope(otherProjectId); harness.read().load() })
    const request = harness.lastRequest()
    await act(async () => {
      harness.emit(available(abandoned, projectSnapshot))
      harness.emit(available(request, projectSnapshot))
      harness.emit({ ...available(request, { ...projectSnapshot, projectId: otherProjectId }), requestId: abandoned.requestId })
    })
    expect(harness.read().state.pendingRequest?.requestId).toBe(request.requestId)
    expect(harness.read().state.snapshot).toBeNull()
    await act(async () => { harness.emit(available(request, { ...projectSnapshot, projectId: otherProjectId })) })
    expect(harness.read().state.snapshot?.projectId).toBe(otherProjectId)
  })

  it.each(scopes)('uses the latest $scope identity for reset/resetAll and does not reset another draft', async ({ target }) => {
    const harness = await mountWorkspace()
    await loadTarget(harness, target)
    await act(async () => { harness.read().changeDraft(key, disabled); harness.read().changeDraft(otherKey, four); expect(harness.read().reset(key)).toBe(true) })
    const request = harness.lastRequest()
    const method = target.projectId === null ? 'settings.global.reset' : 'settings.reset'
    expect(request).toMatchObject({ method, params: { ...identity(target), key } })
    expect(Object.hasOwn(request.params, 'projectId')).toBe(target.projectId !== null)
    expect(harness.read().state.draftValues).toEqual({ [key]: disabled, [otherKey]: four })
    const resetSnapshot = withValue(target, key, target.initialValues.find(selection => selection.key === key)!.value)
    await act(async () => { harness.emit(available(request, resetSnapshot)) })
    expect(harness.read().state.draftValues).toEqual({ [otherKey]: four })
    await act(async () => { expect(harness.read().resetAll()).toBe(true) })
    const resetAll = harness.lastRequest()
    expect(resetAll).toMatchObject({ method: target.projectId === null ? 'settings.global.resetAll' : 'settings.resetAll', params: identity(resetSnapshot) })
    expect(harness.read().state.draftValues).toEqual({ [otherKey]: four })
    await act(async () => { harness.read().changeDraft(otherKey, five); harness.emit(available(resetAll, { ...resetSnapshot, revision: resetSnapshot.revision + 1, values: resetSnapshot.initialValues })) })
    expect(harness.read().state.draftValues).toEqual({ [otherKey]: five })
  })

  describe.each(scopes)('$scope ABA acknowledgements', ({ target }) => {
    it.each(['reset', 'resetAll', 'applyFile'] as const)('preserves a 4 -> 5 -> 4 edit during %s after DB returns 0', async operation => {
      const harness = await mountWorkspace()
      await loadTarget(harness, target)
      const api = harness.read()
      const updated = operation === 'reset'
        ? withValue(target, otherKey, { kind: 'integer', integerValue: '0' })
        : { ...target, revision: target.revision + 1, snapshotDigest: 'c'.repeat(64), values: target.initialValues }
      await act(async () => { api.changeDraft(key, disabled); api.changeDraft(otherKey, four) })
      if (operation === 'applyFile') {
        await act(async () => { expect(api.previewFile()).toBe(true) })
        await act(async () => { harness.emit(available(harness.lastRequest(), { ...updated, revision: target.revision })) })
      }
      const captured = harness.read().state.draftValues[otherKey]
      await act(async () => {
        expect(operation === 'reset' ? api.reset(otherKey) : operation === 'resetAll' ? api.resetAll() : api.applyFile()).toBe(true)
        api.changeDraft(otherKey, five)
        api.changeDraft(otherKey, four)
      })
      const request = harness.lastRequest()
      expect(request.method).toBe(operation === 'applyFile' ? 'settings.file.apply' : target.projectId === null ? `settings.global.${operation}` : `settings.${operation}`)
      expect(harness.read().state.pendingRequest?.requestId).toBe(request.requestId)
      expect(harness.read().state.pendingRequest?.draftValues[otherKey]).toBe(captured)
      const edited = harness.read().state.draftValues[otherKey]
      expect(edited).toEqual(four)
      expect(edited).not.toBe(captured)
      await act(async () => { harness.emit(available(request, updated)) })
      expect(harness.read().state.snapshot?.values.find(item => item.key === otherKey)?.value).toEqual({ kind: 'integer', integerValue: '0' })
      expect(harness.read().state.draftValues).toEqual(operation === 'reset' ? { [key]: disabled, [otherKey]: four } : { [otherKey]: four })
      expect(harness.read().state.draftValues[otherKey]).toBe(edited)
      expect(harness.read().state.pendingRequest).toBeNull()
      expect(harness.read().isDirty).toBe(true)
    })

    it.each(['4', '0'])('clears a saved ABA draft only if the returned DB value %s matches it', async integerValue => {
      const harness = await mountWorkspace()
      await loadTarget(harness, target)
      const api = harness.read()
      await act(async () => {
        api.changeDraft(otherKey, four)
        expect(api.save(otherKey)).toBe(true)
        api.changeDraft(otherKey, five)
        api.changeDraft(otherKey, four)
      })
      const request = harness.lastRequest()
      expect(request.params).toMatchObject({ key: otherKey, value: four })
      const acknowledged: SettingValue = { kind: 'integer', integerValue }
      await act(async () => { harness.emit(available(request, withValue(target, otherKey, acknowledged))) })
      expect(harness.read().state.snapshot?.values.find(item => item.key === otherKey)?.value).toEqual(acknowledged)
      expect(harness.read().state.draftValues).toEqual(integerValue === '4' ? {} : { [otherKey]: four })
      expect(harness.read().isDirty).toBe(integerValue !== '4')
    })
  })

  it.each(scopes)('exports saved $scope values only, preserves dirty drafts and requires an explicit overwrite hash', async ({ target }) => {
    const harness = await mountWorkspace()
    await loadTarget(harness, target)
    const saved = harness.read().state.snapshot
    await act(async () => { harness.read().changeDraft(key, disabled); expect(harness.read().exportFile(null)).toBe(true) })
    const request = harness.lastRequest()
    expect(request).toMatchObject({ method: 'settings.file.export', params: { projectId: target.projectId, ...identity(target), expectedFileSha256: null } })
    expect(Object.hasOwn(request.params, 'values')).toBe(false)
    await act(async () => { harness.emit(failed(request, 'overwriteRequired', otherFileSha256)) })
    expect(harness.read().state).toMatchObject({ error: 'overwriteRequired', exportNeedsConfirmation: true, fileSha256: otherFileSha256, importPreview: null })
    expect(harness.read().state.snapshot).toBe(saved)
    expect(harness.read().isDirty).toBe(true)
    await act(async () => { harness.read().clearError() })
    expect(harness.read().state.fileSha256).toBe(otherFileSha256)
    await act(async () => { expect(harness.read().exportFile(otherFileSha256)).toBe(true) })
    const retry = harness.lastRequest()
    expect(retry.params).toMatchObject({ expectedFileSha256: otherFileSha256, ...identity(target) })
    await act(async () => { harness.emit(available(retry, target)) })
    expect(harness.read().state.snapshot).toBe(saved)
    expect(harness.read().state.draftValues).toEqual({ [key]: disabled })
    expect(harness.read().state).toMatchObject({ exportNeedsConfirmation: false, fileSha256, importPreview: null })
    await act(async () => { expect(harness.read().applyFile()).toBe(false); harness.read().clearFile() })
    expect(harness.read().state.fileSha256).toBeNull()
    expect(harness.read().state.draftValues).toEqual({ [key]: disabled })
  })

  it.each(scopes)('previews $scope without DB mutation and applies against saved identity plus the preview hash', async ({ target }) => {
    const harness = await mountWorkspace()
    await loadTarget(harness, target)
    const saved = harness.read().state.snapshot
    await act(async () => { expect(harness.read().applyFile()).toBe(false); harness.read().changeDraft(otherKey, four); harness.read().previewFile() })
    const preview = harness.lastRequest()
    expect(preview).toMatchObject({ method: 'settings.file.preview', params: { projectId: target.projectId, ...identity(target) } })
    const candidate = { ...withValue(target, key, disabled), revision: target.revision }
    await act(async () => { harness.emit(available(preview, candidate)) })
    expect(harness.read().state.snapshot).toBe(saved)
    expect(harness.read().state.importPreview).toEqual(candidate)
    expect(harness.read().state.draftValues).toEqual({ [otherKey]: four })
    await act(async () => { expect(harness.read().applyFile()).toBe(true) })
    const apply = harness.lastRequest()
    expect(apply).toMatchObject({ method: 'settings.file.apply', params: { projectId: target.projectId, ...identity(target), expectedFileSha256: fileSha256 } })
    expect(apply.params).not.toMatchObject({ expectedSnapshotDigest: candidate.snapshotDigest })
    expect(harness.read().state.snapshot).toBe(saved)
    await act(async () => { harness.read().changeDraft(otherKey, five); harness.emit(available(apply, { ...candidate, revision: target.revision + 1 })) })
    expect(harness.read().state.snapshot?.values).toEqual(candidate.values)
    expect(harness.read().state.draftValues).toEqual({ [otherKey]: five })
    expect(harness.read().state.importPreview).toBeNull()
    expect(harness.read().state.lastCompletedMethod).toBe('settings.file.apply')
  })

  it('keeps conflicts and drafts until refresh and uses refreshed identity on the next save', async () => {
    const harness = await mountWorkspace()
    await loadTarget(harness, globalSnapshot)
    const saved = harness.read().state.snapshot
    await act(async () => { harness.read().changeDraft(key, disabled); harness.read().save(key) })
    await act(async () => { harness.emit(failed(harness.lastRequest(), 'conflict')); harness.read().clearError() })
    expect(harness.read().state.needsRefresh).toBe(true)
    expect(harness.read().state.snapshot).toBe(saved)
    expect(harness.read().state.draftValues).toEqual({ [key]: disabled })
    const calls = harness.client.sendSettingsWorkspace.mock.calls.length
    await act(async () => {
      const api = harness.read()
      expect(api.save(key)).toBe(false); expect(api.reset(key)).toBe(false); expect(api.resetAll()).toBe(false)
      expect(api.exportFile(null)).toBe(false); expect(api.previewFile()).toBe(false); expect(api.applyFile()).toBe(false)
    })
    expect(harness.client.sendSettingsWorkspace).toHaveBeenCalledTimes(calls)
    await act(async () => { expect(harness.read().refresh()).toBe(true) })
    const refreshed = { ...globalSnapshot, revision: 12, snapshotDigest: '9'.repeat(64) }
    await act(async () => { harness.emit(available(harness.lastRequest(), refreshed)) })
    expect(harness.read().state.needsRefresh).toBe(false)
    expect(harness.read().state.draftValues).toEqual({ [key]: disabled })
    await act(async () => { harness.read().save(key) })
    expect(harness.lastRequest().params).toMatchObject(identity(refreshed))
  })

  it('retains invalid drafts, rejects unsafe values locally and does not leak transport exception text', async () => {
    const harness = await mountWorkspace()
    await loadTarget(harness, globalSnapshot)
    const calls = harness.client.sendSettingsWorkspace.mock.calls.length
    await act(async () => { harness.read().changeDraft(otherKey, { kind: 'integer', integerValue: '' }); expect(harness.read().save(otherKey)).toBe(false) })
    expect(harness.read().state.error).toBe('invalid')
    expect(harness.read().state.draftValues[otherKey]).toEqual({ kind: 'integer', integerValue: '' })
    await act(async () => { expect(harness.read().exportFile('not-a-hash')).toBe(false) })
    expect(harness.client.sendSettingsWorkspace).toHaveBeenCalledTimes(calls)
    harness.client.sendSettingsWorkspace.mockImplementationOnce(() => { throw new Error('private transport secret') })
    await act(async () => { harness.read().changeDraft(otherKey, four); expect(harness.read().save(otherKey)).toBe(false) })
    expect(harness.read().state).toMatchObject({ error: 'unavailable', needsRefresh: true, pendingRequest: null })
    expect(harness.read().state.draftValues).toEqual({ [otherKey]: four })
    expect(JSON.stringify(harness.read().state)).not.toContain('private transport secret')
  })

  it('times out without retries and ignores late results while retaining write drafts for refresh', async () => {
    vi.useFakeTimers()
    const harness = await mountWorkspace()
    await act(async () => { harness.read().load() })
    const abandoned = harness.lastRequest()
    await act(async () => { vi.advanceTimersByTime(5_000) })
    expect(harness.read().state).toMatchObject({ error: 'timeout', pendingRequest: null, snapshot: null, needsRefresh: false })
    await act(async () => { harness.emit(available(abandoned, globalSnapshot)); vi.advanceTimersByTime(20_000) })
    expect(harness.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    expect(harness.read().state.snapshot).toBeNull()
    await loadTarget(harness, globalSnapshot)
    expect(harness.lastRequest().requestId).not.toBe(abandoned.requestId)
    await act(async () => { harness.read().changeDraft(key, disabled); harness.read().save(key) })
    const write = harness.lastRequest()
    await act(async () => { vi.advanceTimersByTime(5_000); harness.emit(available(write, withValue(globalSnapshot, key, disabled))) })
    expect(harness.read().state).toMatchObject({ error: 'timeout', pendingRequest: null, needsRefresh: true, draftValues: { [key]: disabled } })
    expect(harness.read().state.snapshot?.revision).toBe(0)
    expect(vi.getTimerCount()).toBe(0)
  })

  it('abandons a cleared file request, prevents late preview resurrection and invalidates preview after refresh', async () => {
    const harness = await mountWorkspace()
    await loadTarget(harness, globalSnapshot)
    await act(async () => { harness.read().changeDraft(key, disabled); harness.read().previewFile() })
    const cancelled = harness.lastRequest()
    await act(async () => { harness.read().clearFile(); harness.emit(available(cancelled, globalSnapshot)) })
    expect(harness.client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(cancelled.requestId)
    expect(harness.read().state).toMatchObject({ pendingRequest: null, importPreview: null, fileSha256: null, draftValues: { [key]: disabled } })
    await act(async () => { harness.read().previewFile() })
    await act(async () => { harness.emit(available(harness.lastRequest(), globalSnapshot)); harness.read().refresh() })
    await act(async () => { harness.emit(available(harness.lastRequest(), globalSnapshot)) })
    expect(harness.read().state.importPreview).toBeNull()
    expect(harness.read().applyFile()).toBe(false)
  })

  it('disposes a replaced client, ignores its stale callbacks and requires refresh after abandoning a write', async () => {
    const harness = await mountWorkspace()
    await loadTarget(harness, globalSnapshot)
    await act(async () => { harness.read().changeDraft(key, disabled); harness.read().save(key) })
    const oldRequest = harness.lastRequest()
    const replacement = workspaceClient()
    await harness.render({ createClient: replacement.createClient })
    expect(harness.client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(oldRequest.requestId)
    expect(harness.client.dispose).toHaveBeenCalledOnce()
    expect(harness.unsubscribe).toHaveBeenCalledOnce()
    expect(replacement.createClient).toHaveBeenCalledOnce()
    expect(harness.read().state).toMatchObject({ needsRefresh: true, pendingRequest: null, error: 'unavailable', draftValues: { [key]: disabled } })
    await act(async () => { harness.emit(available(oldRequest, withValue(globalSnapshot, key, disabled))); harness.read().refresh() })
    expect(harness.read().state.snapshot?.revision).toBe(0)
    expect(replacement.lastRequest().method).toBe('settings.global.read')
    await harness.unmount()
    expect(replacement.client.dispose).toHaveBeenCalledOnce()
    expect(replacement.client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(replacement.lastRequest().requestId)
  })

  it('reports a private-safe setup failure and permits a synchronous response on a replacement client', async () => {
    const harness = await mountWorkspace()
    await harness.render({ createClient: () => { throw new Error('private setup secret') } })
    expect(harness.read().state).toMatchObject({ error: 'unavailable', snapshot: null })
    expect(JSON.stringify(harness.read().state)).not.toContain('private setup secret')
    const replacement = workspaceClient()
    replacement.client.sendSettingsWorkspace.mockImplementation(request => { replacement.emit(available(request, globalSnapshot)) })
    await harness.render({ createClient: replacement.createClient })
    await act(async () => { expect(harness.read().load()).toBe(true) })
    expect(harness.read().state).toMatchObject({ pendingRequest: null, error: null, lastCompletedMethod: 'settings.global.read' })
    expect(harness.read().state.snapshot?.revision).toBe(0)
  })

  it('still accepts a fake with only the legacy client members in the original provider', async () => {
    const client: SettingsBridgeClient = { sendSettings: vi.fn(), abandonSettingsRequest: vi.fn(), onSettingsResponse: vi.fn(() => vi.fn()), dispose: vi.fn() }
    let current: SettingsContextValue | undefined
    function LegacyConsumer() { current = useSettingsState(); return null }
    const container = document.createElement('div')
    const root = createRoot(container)
    try {
      await act(async () => { root.render(<SettingsStateProvider createClient={() => client}><LegacyConsumer /></SettingsStateProvider>) })
      expect(client.onSettingsResponse).toHaveBeenCalledOnce()
      expect(current?.state.projectIdInput).toBe('')
      expect(current?.state.pendingMethod).toBeNull()
    } finally { await act(async () => { root.unmount() }); container.remove() }
    expect(client.dispose).toHaveBeenCalledOnce()
  })
})

function workspaceClient() {
  let listener: ((response: SettingsWorkspaceResponse) => void) | undefined
  const unsubscribe = vi.fn<() => void>()
  const client = {
    sendSettingsWorkspace: vi.fn<SettingsWorkspaceBridgeClient['sendSettingsWorkspace']>(),
    abandonSettingsWorkspaceRequest: vi.fn<SettingsWorkspaceBridgeClient['abandonSettingsWorkspaceRequest']>(),
    onSettingsWorkspaceResponse: vi.fn<SettingsWorkspaceBridgeClient['onSettingsWorkspaceResponse']>(next => { listener = next; return unsubscribe }),
    dispose: vi.fn<() => void>(),
  } satisfies SettingsWorkspaceBridgeClient
  return {
    client, unsubscribe, createClient: vi.fn(() => client),
    emit(response: SettingsWorkspaceResponse): void { listener?.(response) },
    lastRequest(): SettingsWorkspaceRequest {
      const call = client.sendSettingsWorkspace.mock.calls.at(-1)
      if (call === undefined) throw new Error('No settings workspace request was sent.')
      return call[0]
    },
  }
}
async function mountWorkspace() {
  const harness = workspaceClient()
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  let current: SettingsWorkspaceContextValue | undefined
  function Consumer() { current = useSettingsWorkspace(); return <output>{current.state.scope}</output> }
  const unmount = async () => { if (!cleanups.delete(unmount)) return; await act(async () => { root.unmount() }); container.remove() }
  cleanups.add(unmount)
  const render = async ({ show = true, hidden = false, panelKey = 'first', createClient = harness.createClient }: { show?: boolean; hidden?: boolean; panelKey?: string; createClient?: () => SettingsWorkspaceBridgeClient } = {}) => {
    await act(async () => { root.render(<SettingsWorkspaceStateProvider createClient={createClient}><div hidden={hidden}>{show && <Consumer key={panelKey} />}</div></SettingsWorkspaceStateProvider>) })
  }
  await render()
  return { ...harness, render, unmount, read(): SettingsWorkspaceContextValue { if (current === undefined) throw new Error('Workspace consumer is not mounted.'); return current } }
}
async function loadTarget(harness: Awaited<ReturnType<typeof mountWorkspace>>, target: AnySettingsSnapshot): Promise<void> {
  await act(async () => { expect(harness.read().setScope(target.projectId)).toBe(true); expect(harness.read().load()).toBe(true) })
  await act(async () => { harness.emit(available(harness.lastRequest(), target)) })
}
function identity(target: AnySettingsSnapshot) {
  return { expectedRevision: target.revision, expectedProfileId: target.profileId, expectedProfileVersion: target.profileVersion, expectedProfileDigest: target.profileDigest, expectedSnapshotDigest: target.snapshotDigest }
}
function available(request: SettingsWorkspaceRequest, target: AnySettingsSnapshot): SettingsWorkspaceResponse {
  return requiredResponse({ version: 1, requestId: request.requestId, method: request.method, result: request.method.startsWith('settings.file.') ? { status: 'available', snapshot: target, fileSha256 } : { status: 'available', snapshot: target } })
}
function failed(request: SettingsWorkspaceRequest, status: Exclude<SettingsFileStatus, 'available'>, hash = fileSha256): SettingsWorkspaceResponse {
  return requiredResponse({ version: 1, requestId: request.requestId, method: request.method, result: request.method.startsWith('settings.file.') ? { status, snapshot: null, fileSha256: status === 'overwriteRequired' ? hash : null } : { status, snapshot: null } })
}
function requiredResponse(value: unknown): SettingsWorkspaceResponse {
  const response = parseSettingsWorkspaceResponse(value)
  if (response === null) throw new Error('Invalid settings workspace response fixture.')
  return response
}
function withValue(target: AnySettingsSnapshot, settingKey: string, value: SettingValue): AnySettingsSnapshot {
  return { ...target, revision: target.revision + 1, snapshotDigest: 'c'.repeat(64), values: target.values.map(selection => selection.key === settingKey ? { key: settingKey, value } : selection) }
}