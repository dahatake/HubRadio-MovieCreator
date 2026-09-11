// @vitest-environment jsdom

import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fireEvent, within } from '@testing-library/dom'
import userEvent from '@testing-library/user-event'
import { act, StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, expectTypeOf, it, vi } from 'vitest'
import { SettingsPanel, type SettingsPanelProps } from './SettingsPanel'
import { SettingsWorkspaceStateProvider, useSettingsWorkspace, type SettingsWorkspaceContextValue } from './SettingsStateProvider'
import type { SettingsWorkspaceBridgeClient } from './settingsBridgeSession'
import { parseSettingsWorkspaceResponse, type AnySettingsSnapshot, type GlobalSettingsSnapshot, type SettingDefinition, type SettingValue, type SettingsFileStatus, type SettingsSnapshot, type SettingsWorkspaceRequest, type SettingsWorkspaceResponse } from './settingsProtocol'

const projectId = '11112222-3333-4444-8555-666677778888'
const otherProjectId = '22223333-4444-4555-8666-777788889999'
const titleKey = 'project.title'
const countKey = 'project.verticalShortCandidateCount'
const advancedKey = 'subtitles.enabled'
const fileSha256 = 'd'.repeat(64)
const groupNames = ['案件と出力', '編集', '字幕とグラフィック', '音声と素材', 'プライバシーと保存'] as const
const definitions: readonly SettingDefinition[] = [
  definition(titleKey, { displayName: 'タイトル', defaultValue: text('プロファイルのタイトル') }),
  definition(countKey, { displayName: '短尺候補数', defaultValue: integer('3') }),
  definition('output.landscapeResolution', { displayName: '横長の予定サイズ', defaultValue: text('1280x720') }),
  definition('output.frameRate', { displayName: 'フレームレート', defaultValue: { kind: 'rational', numerator: '30000', denominator: '1001' } }),
  ...Array.from({ length: 4 }, (_, index) => definition(`project.extra${index + 1}`, { displayName: `追加項目 ${index + 1}` })),
  definition('editing.cutCount', { group: 'editing', displayName: 'カット数', defaultValue: integer('0') }),
  definition(advancedKey, { group: 'subtitlesAndGraphics', displayName: '字幕を付ける', isAdvanced: true }),
  definition('audio.searchTerms', { group: 'audioAndAssets', displayName: '素材の検索語', defaultValue: { kind: 'textList', textListValue: ['声', '音楽'] } }),
  definition('privacy.retention', { group: 'privacyAndStorage', displayName: '保存期間', defaultValue: integer('30') }),
]
const globalSnapshot: GlobalSettingsSnapshot = {
  projectId: null, profileId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', profileName: 'Settings fixture', profileVersion: '1.0.0',
  profileDigest: 'a'.repeat(64), initialSnapshotDigest: 'a'.repeat(64), snapshotDigest: 'b'.repeat(64), revision: 0,
  updatedAt: '2026-09-07T00:00:00.0000000+00:00', definitionCount: definitions.length, definitions,
  initialValues: definitions.map(item => ({ key: item.key, value: item.defaultValue })),
  values: definitions.map(item => ({ key: item.key, value: item.key === titleKey ? text('保存済みタイトル') : item.defaultValue })),
}
const projectSnapshot: SettingsSnapshot = {
  ...globalSnapshot, projectId, revision: 7, snapshotDigest: 'c'.repeat(64), initialSnapshotDigest: 'e'.repeat(64),
  initialValues: globalSnapshot.initialValues.map(item => item.key === titleKey ? { key: titleKey, value: text('この動画の初期タイトル') } : item),
}
const targets: readonly { scope: string; snapshot: AnySettingsSnapshot }[] = [
  { scope: 'global', snapshot: globalSnapshot }, { scope: 'project', snapshot: projectSnapshot },
]
const cleanups = new Set<() => Promise<void>>()
const externalElements = new Set<HTMLElement>()

afterEach(async () => {
  try { for (const cleanup of [...cleanups]) await cleanup() }
  finally {
    for (const element of externalElements) element.remove()
    externalElements.clear()
    vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals()
  }
})

describe('SettingsPanel UX-025', () => {
  it('exposes the exact shell props and a private-safe page without raw ID input', () => {
    expectTypeOf<SettingsPanelProps>().toEqualTypeOf<{
      readonly isActive?: boolean
      readonly activeProjectId?: string | null
      readonly onClose?: () => void
      readonly initialSettingKey?: string | null
      readonly onDirtyChange?: (dirty: boolean) => void
    }>()
    const h = workspaceClient()
    const markup = renderToStaticMarkup(<SettingsWorkspaceStateProvider createClient={h.createClient}><SettingsPanel /></SettingsWorkspaceStateProvider>)
    expect(markup).toContain('>設定</h2>')
    expect(markup).toContain('新しい動画の既定値')
    expect(markup).toContain('この動画')
    expect(markup).not.toMatch(/settings-project-id|案件 ID|actorSid|sourcePath|<input/)
    expect(h.client.sendSettingsWorkspace).not.toHaveBeenCalled()
  })

  it.each(targets)('loads $scope once on first visible open and keeps the selection on reopen', async ({ snapshot }) => {
    const outside = externalButton()
    outside.focus()
    const h = await mount({ isActive: false, activeProjectId: snapshot.projectId })
    expect(h.client.sendSettingsWorkspace).not.toHaveBeenCalled()
    expect(document.activeElement).toBe(outside)
    expect(h.container.querySelector('.settings-panel')?.hasAttribute('hidden')).toBe(true)
    await h.render({ isActive: true })
    expect(h.lastRequest()).toMatchObject(snapshot.projectId === null
      ? { method: 'settings.global.read', params: {} } : { method: 'settings.read', params: { projectId } })
    await respond(h, snapshot)
    await selectRow(h, '短尺候補数')
    await change(h, '4')
    await h.render({ isActive: false })
    outside.focus()
    await h.render({ isActive: false, onClose: () => undefined })
    expect(document.activeElement).toBe(outside)
    await h.render({ isActive: true })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    expect(h.createClient).toHaveBeenCalledOnce()
    expect(h.read().state.draftValues).toEqual({ [countKey]: integer('4') })
    expect(editor(h).value).toBe('4')
    expect(h.container.querySelector('input[value="' + projectId + '"]')).toBeNull()
  })

  it('preserves a user-selected global scope on ordinary reopen for the same project', async () => {
    const h = await mount({ activeProjectId: projectId })
    await respond(h, projectSnapshot)
    await click(h, '新しい動画の既定値')
    await respond(h, globalSnapshot)
    await selectRow(h, '短尺候補数')
    await h.render({ isActive: false })
    await h.render({ isActive: true })
    expect(h.read().state.projectId).toBeNull()
    expect(h.read().state.snapshot?.projectId).toBeNull()
    expect(h.button('新しい動画の既定値').getAttribute('aria-pressed')).toBe('true')
    expect(h.button('この動画').getAttribute('aria-pressed')).toBe('false')
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    expect(editor(h).value).toBe('3')
    expect(h.client.sendSettingsWorkspace.mock.calls.map(([request]) => request.method)).toEqual(['settings.read', 'settings.global.read'])
  })

  it('defers the initial read until the replayed StrictMode session is ready', async () => {
    const h = await mount({}, true)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    expect(h.lastRequest().method).toBe('settings.global.read')
    await respond(h, globalSnapshot)
    expect(h.read().state.snapshot?.revision).toBe(0)
    expect(h.read().state.error).toBeNull()
    expect(h.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
  })

  it.each(['', 'not-a-project', '00000000-0000-0000-0000-000000000000', 'AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE'])('uses global for invalid context %j without sending or displaying that ID', async activeProjectId => {
    const h = await mount({ activeProjectId })
    expect(h.lastRequest()).toMatchObject({ method: 'settings.global.read', params: {} })
    expect(h.button('この動画').disabled).toBe(true)
    expect(h.container.querySelector('input')).toBeNull()
    if (activeProjectId.length > 0) expect(h.container.textContent).not.toContain(activeProjectId)
    await respond(h, globalSnapshot)
    expect(h.button('新しい動画の既定値').getAttribute('aria-pressed')).toBe('true')
  })

  it('does not loop on failure, timeout, error dismissal or unrelated rerenders', async () => {
    vi.useFakeTimers()
    const h = await mount()
    const outside = externalButton()
    outside.focus()
    await act(async () => { vi.advanceTimersByTime(5_000) })
    expect(h.read().state.error).toBe('timeout')
    expect(within(h.container).getByRole('alert').textContent).toContain('時間内に完了しませんでした')
    expect(document.activeElement).toBe(outside)
    await click(h, 'エラーを閉じる')
    await h.render({ onDirtyChange: () => undefined })
    await act(async () => { vi.advanceTimersByTime(20_000) })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    await click(h, '設定を再読み込み')
    await act(async () => { h.emit(failed(h.lastRequest(), 'unavailable')) })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    await click(h, '設定を再読み込み')
    await respond(h, globalSnapshot)
    expect(h.read().state.snapshot?.projectId).toBeNull()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(3)
  })

  it('uses five manual-activation tabs with ArrowLeft/Right, Home/End and native Enter/Space', async () => {
    const user = userEvent.setup()
    const h = await mount()
    await respond(h, globalSnapshot)
    const tabs = within(h.container).getAllByRole('tab') as HTMLButtonElement[]
    expect(tabs.map(tab => tab.textContent)).toEqual(groupNames)
    for (const tab of tabs) {
      const target = document.getElementById(tab.getAttribute('aria-controls')!)!
      expect(target.getAttribute('role')).toBe('tabpanel')
      expect(target.getAttribute('aria-labelledby')).toBe(tab.id)
    }
    await act(async () => { tabs[0]!.focus(); await user.keyboard('{End}') })
    expect(document.activeElement).toBe(tabs[4])
    expect(tabs[0]!.getAttribute('aria-selected')).toBe('true')
    await act(async () => { await user.keyboard('{Home}{ArrowLeft}') })
    expect(document.activeElement).toBe(tabs[4])
    await act(async () => { await user.keyboard('{ArrowRight}{ArrowRight}') })
    expect(document.activeElement).toBe(tabs[1])
    expect(tabs[0]!.getAttribute('aria-selected')).toBe('true')
    expect(fireEvent.keyDown(tabs[1]!, { key: 'ArrowUp' })).toBe(true)
    expect(fireEvent.keyDown(tabs[1]!, { key: 'ArrowDown' })).toBe(true)
    await act(async () => { await user.keyboard('{Enter}') })
    expect(tabs[1]!.getAttribute('aria-selected')).toBe('true')
    await act(async () => { await user.keyboard('{ArrowRight} ') })
    expect(tabs[2]!.getAttribute('aria-selected')).toBe('true')
    await act(async () => { await user.tab() })
    expect(document.activeElement).toBe(within(h.container).getByRole('tabpanel', { name: '字幕とグラフィック' }))
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
  })

  it('reaches all 123 shipped keys once through five groups and six-row pages', async () => {
    type InventoryEntry = Pick<SettingDefinition, 'key' | 'group' | 'displayName' | 'description' | 'isAdvanced'>
    const profile = JSON.parse(readFileSync(resolve(process.cwd(), '../HubRadio.App/DefaultSettings/Profiles/1.0.0.json'), 'utf8')) as { settings: readonly InventoryEntry[] }
    // Real shipped key inventory; deliberately synthetic values are not render evidence.
    const inventory = profile.settings.map(item => definition(item.key, {
      group: item.group, displayName: item.displayName, description: item.description, isAdvanced: item.isAdvanced,
    }))
    const fixture: GlobalSettingsSnapshot = { ...globalSnapshot, definitionCount: inventory.length, definitions: inventory,
      initialValues: inventory.map(item => ({ key: item.key, value: item.defaultValue })),
      values: inventory.map(item => ({ key: item.key, value: item.defaultValue })) }
    expect(inventory).toHaveLength(123)
    const h = await mount()
    await respond(h, fixture)
    const visited: string[] = []
    for (const name of groupNames) {
      await selectGroup(h, name)
      const advanced = within(h.container).getByRole('checkbox', { name: '詳細設定を表示' }) as HTMLInputElement
      // Toggling on also starts this group's complete inventory at page one.
      if (advanced.checked) await act(async () => { advanced.click() })
      await act(async () => { advanced.click() })
      do {
        const rows = listRows(h)
        expect(rows.length).toBeGreaterThan(0)
        expect(rows.length).toBeLessThanOrEqual(6)
        visited.push(...rows.map(row => row.querySelector('code')!.textContent!))
        if (h.button('次のページ').disabled) break
        await click(h, '次のページ')
      } while (visited.length <= inventory.length)
    }
    expect(visited).toHaveLength(123)
    expect(new Set(visited).size).toBe(123)
    expect([...visited].sort()).toEqual(inventory.map(item => item.key).sort())
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
  })

  it('keeps a selected draft through search, a partial last page, group changes and details navigation', async () => {
    const h = await mount()
    await respond(h, globalSnapshot)
    await change(h, '未保存タイトル')
    await click(h, '次のページ')
    expect(listRows(h)).toHaveLength(2)
    expect(h.button('次のページ').disabled).toBe(true)
    expect(editor(h).value).toBe('未保存タイトル')
    await act(async () => { fireEvent.change(within(h.container).getByRole('searchbox'), { target: { value: '一致しない検索' } }) })
    expect(listRows(h)).toHaveLength(0)
    expect(h.container.textContent).toContain('該当する設定はありません')
    await click(h, '予定を確認')
    await selectGroup(h, '音声と素材')
    await selectGroup(h, '案件と出力')
    expect(editor(h).value).toBe('未保存タイトル')
    expect(h.read().state.draftValues).toEqual({ [titleKey]: text('未保存タイトル') })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
  })

  it.each(targets)('saves one $scope key with current identity while preserving other drafts and provenance', async ({ snapshot }) => {
    const onDirtyChange = vi.fn<(dirty: boolean) => void>()
    const h = await mount({ activeProjectId: snapshot.projectId, onDirtyChange })
    await respond(h, snapshot)
    await change(h, '別項目の未保存値')
    await selectRow(h, '短尺候補数')
    await change(h, '4')
    const form = h.container.querySelector('.setting-inspector')
    const save = h.button('保存')
    await act(async () => { save.click(); save.click() })
    const request = h.lastRequest()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    expect(request).toMatchObject({ method: snapshot.projectId === null ? 'settings.global.update' : 'settings.update', params: { ...identity(snapshot), key: countKey, value: integer('4') } })
    expect(Object.hasOwn(request.params, 'projectId')).toBe(snapshot.projectId !== null)
    await respond(h, withValue(snapshot, countKey, integer('4')))
    expect(h.container.querySelector('.setting-inspector')).toBe(form)
    expect(h.read().state.draftValues).toEqual({ [titleKey]: text('別項目の未保存値') })
    expect(onDirtyChange).toHaveBeenLastCalledWith(true)
    await selectRow(h, 'タイトル')
    expect(editor(h).value).toBe('別項目の未保存値')
    const details = h.container.querySelector<HTMLDetailsElement>('.setting-inspector details')!
    await act(async () => { details.open = true })
    expect(details.textContent).toContain(snapshot.projectId === null ? '復元先（プロファイル初期値）' : '復元先（案件開始時の初期値）')
    expect(details.textContent).toContain(snapshot.projectId === null ? 'プロファイルのタイトル' : 'この動画の初期タイトル')
    expect(details.textContent).toContain('プロファイル既定値')
    expect(details.textContent).toContain('productDecision')
    expect(h.read().state.snapshot?.initialValues).toEqual(snapshot.initialValues)
    await click(h, '保存')
    expect(h.lastRequest().params).toMatchObject({ expectedRevision: snapshot.revision + 1, key: titleKey })
  })

  it('confirms a dirty individual reset and preserves other inputs until the response', async () => {
    const h = await mount({ activeProjectId: projectId })
    await respond(h, projectSnapshot)
    await change(h, '破棄前のタイトル')
    await selectRow(h, '短尺候補数')
    await change(h, '4')
    await selectRow(h, 'タイトル')
    await click(h, '案件開始時の値へ戻す')
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    expect(h.dialog().textContent).toContain('他の項目の未保存値は保持')
    await click(h, '初期値へ戻す')
    expect(h.lastRequest()).toMatchObject({ method: 'settings.reset', params: { projectId, ...identity(projectSnapshot), key: titleKey } })
    expect(h.read().state.draftValues).toEqual({ [titleKey]: text('破棄前のタイトル'), [countKey]: integer('4') })
    await respond(h, withValue(projectSnapshot, titleKey, text('この動画の初期タイトル')))
    expect(h.read().state.draftValues).toEqual({ [countKey]: integer('4') })
    expect(editor(h).value).toBe('この動画の初期タイトル')
  })

  it.each([
    { key: 'output.frameRate', input: '9007199254740993', value: { kind: 'rational', numerator: '9007199254740993', denominator: '1001' } as SettingValue },
    { key: 'audio.searchTerms', input: '声\n同じ項目内の改行', value: { kind: 'textList', textListValue: ['声\n同じ項目内の改行', '音楽'] } as SettingValue },
  ])('passes exact $key inspector drafts to the existing save operation', async ({ key, input, value }) => {
    const h = await mount({ initialSettingKey: key })
    await respond(h, globalSnapshot)
    await change(h, input)
    expect(h.read().state.draftValues).toEqual({ [key]: value })
    await click(h, '保存')
    expect(h.lastRequest()).toMatchObject({ method: 'settings.global.update', params: { ...identity(globalSnapshot), key, value } })
    await respond(h, withValue(globalSnapshot, key, value))
    expect(h.read().isDirty).toBe(false)
    expect(editor(h).value).toBe(input)
  })

  it('reports dirty transitions once rather than on callback identity changes', async () => {
    const first = vi.fn<(dirty: boolean) => void>()
    const replacement = vi.fn<(dirty: boolean) => void>()
    const h = await mount({ onDirtyChange: first })
    await respond(h, globalSnapshot)
    expect(first.mock.calls).toEqual([[false]])
    await h.render({ onDirtyChange: replacement })
    expect(replacement).not.toHaveBeenCalled()
    await change(h, '変更した値')
    expect(replacement.mock.calls).toEqual([[true]])
    await h.render({ onDirtyChange: first })
    expect(first.mock.calls).toEqual([[false]])
    await change(h, '保存済みタイトル')
    expect(first.mock.calls).toEqual([[false], [false]])
  })

  it.each(targets)('confirms the $scope reset-all destination and retains drafts on reset failure', async ({ snapshot }) => {
    const h = await mount({ activeProjectId: snapshot.projectId })
    await respond(h, snapshot)
    await change(h, '未保存のまま保持')
    await click(h, '初期値へすべて戻す')
    expect(h.dialog().textContent).toContain(snapshot.projectId === null ? 'プロファイル初期値' : '案件開始時の初期値')
    expect(document.activeElement).toBe(h.button('取消'))
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    await click(h, '取消')
    expect(h.read().isDirty).toBe(true)
    await click(h, '初期値へすべて戻す')
    await click(h, '初期値へ戻す')
    const request = h.lastRequest()
    expect(request).toMatchObject({ method: snapshot.projectId === null ? 'settings.global.resetAll' : 'settings.resetAll', params: identity(snapshot) })
    expect(h.read().isDirty).toBe(true)
    await act(async () => { h.emit(failed(request, 'conflict')) })
    expect(h.read().state.draftValues).toEqual({ [titleKey]: text('未保存のまま保持') })
    expect(h.read().state.snapshot?.initialValues).toEqual(snapshot.initialValues)
    expect(h.button('初期値へすべて戻す').disabled).toBe(true)
    await click(h, '設定を再読み込み')
    const refreshed = { ...snapshot, revision: snapshot.revision + 2, snapshotDigest: 'f'.repeat(64) }
    await respond(h, refreshed)
    expect(editor(h).value).toBe('未保存のまま保持')
    await click(h, '初期値へすべて戻す')
    await click(h, '初期値へ戻す')
    expect(h.lastRequest().params).toMatchObject(identity(refreshed))
    await respond(h, { ...refreshed, revision: refreshed.revision + 1, values: refreshed.initialValues })
    expect(h.read().isDirty).toBe(false)
    expect(h.read().state.snapshot?.initialValues).toEqual(snapshot.initialValues)
  })

  it('guards automatic dirty scope changes with cancel, return-to-input and explicit discard', async () => {
    const h = await mount()
    await respond(h, globalSnapshot)
    await change(h, '未保存の既定値')
    await h.render({ activeProjectId: projectId })
    expect(h.dialog().textContent).toContain('「この動画」へ切り替えます')
    expect(h.read().state.projectId).toBeNull()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    await click(h, '取消')
    await h.render({ onClose: () => undefined })
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    expect(h.button('新しい動画の既定値').getAttribute('aria-pressed')).toBe('true')
    expect(editor(h).value).toBe('未保存の既定値')
    await click(h, 'この動画')
    await click(h, '未保存項目へ戻る')
    expect(document.activeElement).toBe(editor(h))
    expect(h.read().isDirty).toBe(true)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    await click(h, 'この動画')
    await click(h, '変更を破棄して切り替える')
    expect(h.read().isDirty).toBe(false)
    expect(h.lastRequest()).toMatchObject({ method: 'settings.read', params: { projectId } })
    await respond(h, projectSnapshot)
    await change(h, '案件側の編集')
    await click(h, '保存')
    expect(h.lastRequest().params).toMatchObject({ projectId, expectedRevision: 7, expectedSnapshotDigest: projectSnapshot.snapshotDigest })
  })

  it('keeps the old project explicit after cancelling a context switch and never loads while hidden', async () => {
    const h = await mount({ activeProjectId: projectId })
    await respond(h, projectSnapshot)
    await change(h, '旧動画の編集')
    await h.render({ isActive: false, activeProjectId: otherProjectId })
    const outside = externalButton()
    outside.focus()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    expect(h.read().state.projectId).toBe(projectId)
    expect(document.activeElement).toBe(outside)
    await h.render({ isActive: true })
    await click(h, '取消')
    expect(h.container.textContent).toContain('切替前の動画の設定を保持しています')
    expect(h.button('この動画').getAttribute('aria-pressed')).toBe('false')
    expect(h.button('初期値へすべて戻す').disabled).toBe(true)
    await click(h, '設定ファイル')
    expect(within(h.container).queryByRole('button', { name: 'setting.txtに保存' })).toBeNull()
    await click(h, '項目を編集')
    expect(editor(h).value).toBe('旧動画の編集')
    await click(h, '保存')
    expect(h.lastRequest().params).toMatchObject({ projectId, expectedRevision: 7 })
  })

  it('abandons an old project read and rejects its late response without raw IDs', async () => {
    const h = await mount({ activeProjectId: projectId })
    const abandoned = h.lastRequest()
    await h.render({ activeProjectId: otherProjectId })
    expect(h.client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(abandoned.requestId)
    expect(h.lastRequest()).toMatchObject({ method: 'settings.read', params: { projectId: otherProjectId } })
    await act(async () => { h.emit(available(abandoned, projectSnapshot)) })
    expect(h.read().state.snapshot).toBeNull()
    await respond(h, { ...projectSnapshot, projectId: otherProjectId })
    expect(h.read().state.snapshot?.projectId).toBe(otherProjectId)
    expect(h.container.textContent).not.toContain(projectId)
    expect(h.container.textContent).not.toContain(otherProjectId)
  })

  it('waits for an in-flight save before following a new active project', async () => {
    const h = await mount({ activeProjectId: projectId, onClose: vi.fn() })
    await respond(h, projectSnapshot)
    await change(h, '保存中の値')
    await click(h, '保存')
    const write = h.lastRequest()
    await h.render({ activeProjectId: otherProjectId })
    expect(h.read().state.pendingRequest?.requestId).toBe(write.requestId)
    expect(h.button('設定を閉じて戻る').disabled).toBe(true)
    expect(h.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    await act(async () => { h.emit(available(write, withValue(projectSnapshot, titleKey, text('保存中の値')))) })
    expect(h.lastRequest()).toMatchObject({ method: 'settings.read', params: { projectId: otherProjectId } })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(3)
    expect(h.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
  })

  it('does not steal focus for hidden replies and preserves a newer draft for reopening', async () => {
    const h = await mount()
    await respond(h, globalSnapshot)
    await change(h, '送信する値')
    await click(h, '保存')
    const write = h.lastRequest()
    await h.render({ isActive: false })
    const outside = externalButton()
    outside.focus()
    await act(async () => {
      h.read().changeDraft(titleKey, text('応答待ちの新しい値'))
      h.emit(available(write, withValue(globalSnapshot, titleKey, text('送信する値'))))
    })
    expect(document.activeElement).toBe(outside)
    expect(h.read().state.draftValues).toEqual({ [titleKey]: text('応答待ちの新しい値') })
    expect(h.container.querySelector('.settings-panel')?.hasAttribute('inert')).toBe(true)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    await h.render({ isActive: true })
    expect(editor(h).value).toBe('応答待ちの新しい値')
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
  })

  it.each(targets)('keeps $scope drafts and dirty notification when returning without saving and reopening the same active project', async ({ snapshot }) => {
    const onClose = vi.fn<() => void>()
    const onDirtyChange = vi.fn<(dirty: boolean) => void>()
    const h = await mount({ activeProjectId: projectId, onClose, onDirtyChange })
    await respond(h, projectSnapshot)
    if (snapshot.projectId === null) {
      await click(h, '新しい動画の既定値')
      await respond(h, globalSnapshot)
    }
    await change(h, '保持する未保存タイトル')
    await selectRow(h, '短尺候補数')
    await change(h, '4')
    const state = h.read().state
    const requests = h.client.sendSettingsWorkspace.mock.calls.slice()
    await click(h, '設定を閉じて戻る')
    expect(h.dialog().textContent).toContain('入力を保持して画面を閉じます。保存はしません。')
    expect(h.button('未保存のまま戻る').disabled).toBe(false)
    expect(onClose).not.toHaveBeenCalled()
    await click(h, '未保存のまま戻る')
    expect(onClose).toHaveBeenCalledOnce()
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    expect(h.read().state).toBe(state)
    expect(h.read().isDirty).toBe(true)
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
    expect(h.client.sendSettingsWorkspace.mock.calls).toEqual(requests)
    await h.render({ isActive: false })
    expect(h.read().state).toBe(state)
    expect(h.read().isDirty).toBe(true)
    expect(onDirtyChange).toHaveBeenLastCalledWith(true)
    await h.render({ isActive: true, activeProjectId: projectId })
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    expect(h.read().state.projectId).toBe(snapshot.projectId)
    expect(editor(h).value).toBe('4')
    await selectRow(h, 'タイトル')
    expect(editor(h).value).toBe('保持する未保存タイトル')
    expect(h.read().state.draftValues).toEqual({ [titleKey]: text('保持する未保存タイトル'), [countKey]: integer('4') })
    expect(h.read().state).toBe(state)
    expect(h.read().isDirty).toBe(true)
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
    expect(h.createClient).toHaveBeenCalledOnce()
    expect(h.client.sendSettingsWorkspace.mock.calls).toEqual(requests)
    expect(h.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
  })

  it('disables holding and discarding on close during a pending write, including ordinary close and Escape', async () => {
    const onClose = vi.fn<() => void>()
    const onDirtyChange = vi.fn<(dirty: boolean) => void>()
    const h = await mount({ activeProjectId: projectId, onClose, onDirtyChange })
    await respond(h, projectSnapshot)
    await change(h, '保存中のタイトル')
    await selectRow(h, '短尺候補数')
    await change(h, '4')
    const close = h.button('設定を閉じて戻る')
    await click(h, '設定を閉じて戻る')
    // Another consumer of the mounted provider can start a write while confirmation is open.
    await act(async () => { expect(h.read().save(titleKey)).toBe(true) })
    const pendingState = h.read().state
    expect(h.lastRequest()).toMatchObject({ method: 'settings.update', params: { projectId, key: titleKey, value: text('保存中のタイトル') } })
    expect(close.disabled).toBe(true)
    expect(h.button('未保存のまま戻る').disabled).toBe(true)
    expect(h.button('変更を破棄して閉じる').disabled).toBe(true)
    await click(h, '未保存のまま戻る')
    await click(h, '変更を破棄して閉じる')
    expect(onClose).not.toHaveBeenCalled()
    expect(h.read().state).toBe(pendingState)
    await click(h, '取消')
    await act(async () => {
      close.click()
      fireEvent.keyDown(within(h.container).getByRole('heading', { name: '設定', level: 2 }), { key: 'Escape' })
    })
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    expect(onClose).not.toHaveBeenCalled()
    expect(h.read().state).toBe(pendingState)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    await respond(h, withValue(projectSnapshot, titleKey, text('保存中のタイトル')))
    expect(close.disabled).toBe(false)
    await click(h, '設定を閉じて戻る')
    expect(h.button('未保存のまま戻る').disabled).toBe(false)
    await click(h, '未保存のまま戻る')
    expect(onClose).toHaveBeenCalledOnce()
    expect(h.read().state.draftValues).toEqual({ [countKey]: integer('4') })
    expect(h.read().isDirty).toBe(true)
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    expect(h.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
  })

  it('restores the opener on clean close and confirms dirty close without silent saves', async () => {
    const user = userEvent.setup()
    const outside = externalButton()
    outside.focus()
    const onClose = vi.fn<() => void>()
    const onDirtyChange = vi.fn<(dirty: boolean) => void>()
    const h = await mount({ onClose, onDirtyChange })
    await respond(h, globalSnapshot)
    await click(h, '設定を閉じて戻る')
    expect(onClose).toHaveBeenCalledOnce()
    expect(document.activeElement).toBe(outside)
    await h.render({ isActive: false })
    await h.render({ isActive: true })
    await change(h, '閉じる前の未保存値')
    const close = h.button('設定を閉じて戻る')
    await act(async () => { close.focus(); close.click() })
    expect(document.activeElement).toBe(h.button('取消'))
    await act(async () => { await user.tab() })
    expect(document.activeElement).toBe(h.button('未保存項目へ戻る'))
    await act(async () => { await user.tab({ shift: true }) })
    expect(document.activeElement).toBe(h.button('取消'))
    await act(async () => { await user.keyboard('{Escape}') })
    expect(document.activeElement).toBe(close)
    expect(h.read().isDirty).toBe(true)
    expect(onClose).toHaveBeenCalledOnce()
    await click(h, '設定を閉じて戻る')
    await click(h, '未保存項目へ戻る')
    expect(document.activeElement).toBe(editor(h))
    expect(editor(h).value).toBe('閉じる前の未保存値')
    expect(h.read().isDirty).toBe(true)
    expect(onDirtyChange).toHaveBeenLastCalledWith(true)
    await click(h, '設定を閉じて戻る')
    await click(h, '変更を破棄して閉じる')
    expect(h.read().isDirty).toBe(false)
    expect(onDirtyChange).toHaveBeenLastCalledWith(false)
    expect(onClose).toHaveBeenCalledTimes(2)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    expect(document.activeElement).toBe(outside)
    await h.render({ isActive: false })
    await h.render({ isActive: true })
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    expect(editor(h).value).toBe('保存済みタイトル')
    expect(h.read().state.draftValues).toEqual({})
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
  })

  it('does not overwrite an external destination focus selected by the shell', async () => {
    const opener = externalButton()
    const destination = externalButton()
    opener.focus()
    const h = await mount({ onClose: () => destination.focus() })
    await respond(h, globalSnapshot)
    await click(h, '設定を閉じて戻る')
    expect(document.activeElement).toBe(destination)
    await h.render({ isActive: false })
    expect(document.activeElement).toBe(destination)
  })

  it('reveals an advanced initialSettingKey only after visible data arrives and respects missing keys', async () => {
    const h = await mount({ isActive: false, initialSettingKey: advancedKey })
    expect(h.client.sendSettingsWorkspace).not.toHaveBeenCalled()
    await h.render({ isActive: true })
    await respond(h, globalSnapshot)
    expect(within(h.container).getByRole('tab', { name: '字幕とグラフィック' }).getAttribute('aria-selected')).toBe('true')
    expect((within(h.container).getByRole('checkbox', { name: '詳細設定を表示' }) as HTMLInputElement).checked).toBe(true)
    expect(document.activeElement).toBe(editor(h))
    expect(editor(h).tagName).toBe('SELECT')
    await change(h, 'true')
    const focused = editor(h)
    await h.render({ onClose: () => undefined })
    expect(document.activeElement).toBe(focused)
    expect(h.read().state.draftValues).toEqual({ [advancedKey]: { kind: 'boolean', booleanValue: true } })
    await h.render({ initialSettingKey: 'unknown.setting' })
    expect(h.container.querySelector('.setting-inspector')).toBeNull()
    expect(h.container.textContent).toContain('指定された設定項目はこの一覧にありません')
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
  })

  it.each([null, advancedKey])('reopens the same project from global for a warning after initial key %j', async initialSettingKey => {
    const h = await mount({ activeProjectId: projectId, initialSettingKey })
    await respond(h, projectSnapshot)
    await click(h, '新しい動画の既定値')
    await respond(h, globalSnapshot)
    await selectGroup(h, '案件と出力')
    await h.render({ isActive: false })
    await h.render({ isActive: true, initialSettingKey: advancedKey })
    expect(h.lastRequest()).toMatchObject({ method: 'settings.read', params: { projectId } })
    expect(h.read().state.snapshot).toBeNull()
    await respond(h, withValue(projectSnapshot, advancedKey, { kind: 'boolean', booleanValue: true }))
    expect(h.read().state.snapshot?.projectId).toBe(projectId)
    expect(h.button('この動画').getAttribute('aria-pressed')).toBe('true')
    expect(within(h.container).getByRole('tab', { name: '字幕とグラフィック' }).getAttribute('aria-selected')).toBe('true')
    expect((within(h.container).getByRole('checkbox', { name: '詳細設定を表示' }) as HTMLInputElement).checked).toBe(true)
    expect(editor(h).value).toBe('true')
    expect(document.activeElement).toBe(editor(h))
    await h.render({ onClose: () => undefined })
    expect(h.createClient).toHaveBeenCalledOnce()
    expect(h.client.sendSettingsWorkspace.mock.calls.map(([request]) => request.method)).toEqual([
      'settings.read', 'settings.global.read', 'settings.read',
    ])
  })

  it('confirms a warning reopen from dirty global and keeps the draft after cancellation', async () => {
    const h = await mount({ activeProjectId: projectId })
    await respond(h, projectSnapshot)
    await click(h, '新しい動画の既定値')
    await respond(h, globalSnapshot)
    await change(h, '未保存の既定値')
    const drafts = h.read().state.draftValues
    await h.render({ isActive: false })
    await h.render({ isActive: true, initialSettingKey: advancedKey })
    expect(h.dialog().textContent).toContain('「この動画」へ切り替えます')
    expect(h.read().state.projectId).toBeNull()
    expect(h.read().state.draftValues).toBe(drafts)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    await click(h, '取消')
    await h.render({ onClose: () => undefined })
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    expect(editor(h).value).toBe('未保存の既定値')
    expect(h.read().state.draftValues).toBe(drafts)
    await change(h, '取消後も編集中の既定値')
    await h.render({ onDirtyChange: () => undefined })
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    expect(h.button('新しい動画の既定値').getAttribute('aria-pressed')).toBe('true')
    expect(h.read().state.draftValues).toEqual({ [titleKey]: text('取消後も編集中の既定値') })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    await h.render({ isActive: false })
    await h.render({ isActive: true })
    await click(h, '変更を破棄して切り替える')
    expect(h.read().isDirty).toBe(false)
    expect(h.lastRequest()).toMatchObject({ method: 'settings.read', params: { projectId } })
    await respond(h, projectSnapshot)
    expect(within(h.container).getByRole('tab', { name: '字幕とグラフィック' }).getAttribute('aria-selected')).toBe('true')
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    expect(h.client.sendSettingsWorkspace.mock.calls.map(([request]) => request.method)).toEqual([
      'settings.read', 'settings.global.read', 'settings.read',
    ])
  })

  it('follows changed warning keys without duplicating an in-flight read or mutation', async () => {
    const h = await mount({ activeProjectId: projectId })
    await respond(h, projectSnapshot)
    await click(h, '新しい動画の既定値')
    await respond(h, globalSnapshot)
    await h.render({ initialSettingKey: countKey })
    const read = h.lastRequest()
    expect(read).toMatchObject({ method: 'settings.read', params: { projectId } })
    await h.render({ initialSettingKey: advancedKey })
    await h.render({ initialSettingKey: countKey })
    expect(h.read().state.pendingRequest?.requestId).toBe(read.requestId)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(3)
    await respond(h, projectSnapshot)
    expect(editor(h).value).toBe('3')
    await change(h, '4')
    await click(h, '保存')
    const write = h.lastRequest()
    expect(write).toMatchObject({ method: 'settings.update', params: { projectId, ...identity(projectSnapshot), key: countKey, value: integer('4') } })
    await h.render({ initialSettingKey: advancedKey })
    await h.render({ initialSettingKey: titleKey })
    expect(h.read().state.pendingRequest?.requestId).toBe(write.requestId)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(4)
    await act(async () => { h.emit(available(write, withValue(projectSnapshot, countKey, integer('4')))) })
    expect(editor(h).value).toBe('保存済みタイトル')
    expect(h.read().isDirty).toBe(false)
    await h.render({ initialSettingKey: advancedKey })
    expect(within(h.container).getByRole('tab', { name: '字幕とグラフィック' }).getAttribute('aria-selected')).toBe('true')
    expect(h.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
    expect(h.client.sendSettingsWorkspace.mock.calls.map(([request]) => request.method)).toEqual([
      'settings.read', 'settings.global.read', 'settings.read', 'settings.update',
    ])
  })

  it.each(['unavailable', 'timeout'] as const)('does not retry a warning read after %s, dismissal or unrelated rerenders', async error => {
    vi.useFakeTimers()
    const h = await mount({ activeProjectId: projectId })
    await respond(h, projectSnapshot)
    await click(h, '新しい動画の既定値')
    await respond(h, globalSnapshot)
    await h.render({ isActive: false })
    await h.render({ isActive: true, initialSettingKey: advancedKey })
    expect(h.lastRequest()).toMatchObject({ method: 'settings.read', params: { projectId } })
    await act(async () => {
      if (error === 'timeout') vi.advanceTimersByTime(5_000)
      else h.emit(failed(h.lastRequest(), error))
    })
    expect(h.read().state.error).toBe(error)
    await click(h, 'エラーを閉じる')
    await h.render({ onClose: () => undefined })
    await act(async () => { vi.advanceTimersByTime(20_000) })
    expect(h.read().state.snapshot).toBeNull()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(3)
    await click(h, '設定を再読み込み')
    await respond(h, projectSnapshot)
    expect(within(h.container).getByRole('tab', { name: '字幕とグラフィック' }).getAttribute('aria-selected')).toBe('true')
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(4)
  })

  it('keeps long values and initial evidence readable without executing markup or closing the page', async () => {
    const onClose = vi.fn<() => void>()
    const literal = '<img src=x onerror="alert(1)">日本語\n' + '長い設定値'.repeat(500) + '末尾'
    const h = await mount({ activeProjectId: projectId, onClose })
    await respond(h, withValue(projectSnapshot, titleKey, text(literal)))
    expect(h.container.querySelector('.setting-inspector .setting-value-summary dd')!.textContent).toBe(literal)
    expect(editor(h).value).toBe(literal)
    expect(h.container.querySelector('img, script')).toBeNull()
    const evidence = h.container.querySelector<HTMLDetailsElement>('.setting-inspector details')!
    const summary = evidence.querySelector('summary')!
    await act(async () => { evidence.open = true; summary.focus(); fireEvent.keyDown(summary, { key: 'Escape' }) })
    expect(evidence.open).toBe(false)
    expect(document.activeElement).toBe(summary)
    expect(onClose).not.toHaveBeenCalled()
    expect(evidence.textContent).toContain('この動画の初期タイトル')
    expect(evidence.textContent).toContain('productDecision')
    const provenance = h.container.querySelector<HTMLDetailsElement>('.settings-provenance')!
    expect(provenance.textContent).toContain(projectSnapshot.initialSnapshotDigest)
    const provenanceSummary = provenance.querySelector('summary')!
    await act(async () => { provenance.open = true; provenanceSummary.focus(); fireEvent.keyDown(provenanceSummary, { key: 'Escape' }) })
    expect(provenance.open).toBe(false)
    expect(document.activeElement).toBe(provenanceSummary)
    expect(onClose).not.toHaveBeenCalled()
    expect(h.read().state.snapshot?.initialValues).toEqual(projectSnapshot.initialValues)
  })

  it('keeps invalid input in provider state without enabling save', async () => {
    const h = await mount()
    await respond(h, globalSnapshot)
    await selectRow(h, '短尺候補数')
    await change(h, '9223372036854775808')
    expect(h.read().state.draftValues).toEqual({ [countKey]: integer('9223372036854775808') })
    expect(h.button('保存').disabled).toBe(true)
    expect(within(h.container).getByRole('alert').textContent).toContain('64 ビット')
    await selectGroup(h, '編集')
    await selectGroup(h, '案件と出力')
    expect(editor(h).value).toBe('9223372036854775808')
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
  })

  it('delegates dirty export and import confirmation to SettingsFileActions without duplicate confirmation', async () => {
    const h = await mount()
    await respond(h, globalSnapshot)
    await change(h, 'ファイルには出さない未保存値')
    const saved = h.read().state.snapshot
    const drafts = h.read().state.draftValues
    const focus = vi.spyOn(editor(h), 'focus')
    const detailPane = h.container.querySelector<HTMLDivElement>('.settings-detail-pane')!
    await click(h, '設定ファイル')
    detailPane.scrollTop = 100
    await click(h, 'setting.txtに保存')
    expect(within(h.container).getAllByRole('group', { name: '未保存項目があります' })).toHaveLength(1)
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    await click(h, '未保存項目へ戻る')
    expect(document.activeElement).toBe(editor(h))
    expect(focus).toHaveBeenLastCalledWith({ preventScroll: false })
    expect(detailPane.scrollTop).toBe(0)
    expect(h.read().state.draftValues).toBe(drafts)
    await click(h, '設定ファイル')
    await click(h, 'setting.txtに保存')
    await click(h, '保存済み値だけを出力')
    expect(h.lastRequest()).toMatchObject({ method: 'settings.file.export', params: { ...identity(globalSnapshot), expectedFileSha256: null } })
    await act(async () => { h.emit(failed(h.lastRequest(), 'overwriteRequired')) })
    expect(within(h.container).getAllByRole('group', { name: 'setting.txtを上書きしますか？' })).toHaveLength(1)
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    await click(h, '上書きする')
    expect(h.lastRequest().params).toMatchObject({ expectedFileSha256: fileSha256 })
    await respond(h, globalSnapshot)
    expect(h.read().state.draftValues).toBe(drafts)
    await click(h, 'setting.txtから読み込む')
    const preview = { ...withValue(globalSnapshot, countKey, integer('5')), revision: globalSnapshot.revision }
    await respond(h, preview)
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.read().state.draftValues).toBe(drafts)
    await click(h, 'この設定を適用')
    expect(within(h.container).getAllByRole('group', { name: '読み込んだ設定を適用しますか？' })).toHaveLength(1)
    expect(within(h.container).queryByRole('alertdialog')).toBeNull()
    await click(h, '未保存の変更を破棄して適用する')
    expect(h.lastRequest()).toMatchObject({ method: 'settings.file.apply', params: { ...identity(globalSnapshot), expectedFileSha256: fileSha256 } })
    expect(h.read().state.draftValues).toBe(drafts)
    await respond(h, { ...preview, revision: 1 })
    expect(h.read().state.draftValues).toEqual({})
    expect(h.container.textContent).toContain('既存の動画や成果物は更新しません')
    expect(h.client.sendSettingsWorkspace.mock.calls.map(([request]) => request.method)).toEqual([
      'settings.global.read', 'settings.file.export', 'settings.file.export', 'settings.file.preview', 'settings.file.apply',
    ])
  })

  it('shows planned saved-versus-draft outcomes without generating or claiming applied video settings', async () => {
    const h = await mount()
    await respond(h, globalSnapshot)
    await selectRow(h, '短尺候補数')
    await change(h, '4')
    await click(h, '予定を確認')
    const preview = h.container.querySelector('.settings-outcome-preview')!
    expect(preview.textContent).toContain('3 本（目標）')
    expect(preview.textContent).toContain('4 本（目標）')
    expect(preview.textContent).toContain('1280 × 720 px')
    expect(preview.textContent).toContain('未評価（NOT EVALUATED）')
    expect(preview.textContent).toContain('実際の出力は未更新')
    expect(h.container.textContent).toContain('動画への再適用機能は未接続')
    expect(h.read().state.snapshot?.values).toEqual(globalSnapshot.values)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
  })

  it('invalidates destructive confirmation if another consumer refreshes its snapshot', async () => {
    const h = await mount()
    await respond(h, globalSnapshot)
    await change(h, '消してはいけない編集')
    await click(h, '初期値へすべて戻す')
    await act(async () => { h.read().refresh() })
    await respond(h, { ...globalSnapshot, revision: 9, snapshotDigest: '9'.repeat(64) })
    expect(h.dialog().textContent).toContain('確認中に設定状態が変わりました')
    expect(h.button('初期値へ戻す').disabled).toBe(true)
    await click(h, '初期値へ戻す')
    expect(h.read().state.draftValues).toEqual({ [titleKey]: text('消してはいけない編集') })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    await click(h, '取消')
    expect(editor(h).value).toBe('消してはいけない編集')
  })

  it('uses scoped existing theme tokens and permits long-content and narrow-screen scrolling', () => {
    const css = readFileSync(resolve(process.cwd(), 'src/features/settings/settings.css'), 'utf8')
    expect(css).not.toMatch(/@import|https?:|#[\da-fA-F]{3,8}\b|rgba?\(|hsla?\(|:root/)
    expect(css).toContain('var(--cp-text)')
    expect(css).toContain('var(--cp-accent)')
    expect(css).toContain('.settings-panel .settings-list-groups { display: none; }')
    expect(css).toMatch(/\.settings-detail-pane\s*\{[^}]*overflow: auto/s)
    expect(css).toContain('max-block-size: none; overflow: visible;')
    expect(css).toContain('white-space: pre-wrap')
    // This checks CSS intent only; actual viewport/reflow acceptance belongs to UX-041.
  })
})

function workspaceClient() {
  let listener: ((response: SettingsWorkspaceResponse) => void) | undefined
  const client = {
    sendSettingsWorkspace: vi.fn<SettingsWorkspaceBridgeClient['sendSettingsWorkspace']>(),
    abandonSettingsWorkspaceRequest: vi.fn<SettingsWorkspaceBridgeClient['abandonSettingsWorkspaceRequest']>(),
    onSettingsWorkspaceResponse: vi.fn<SettingsWorkspaceBridgeClient['onSettingsWorkspaceResponse']>(next => { listener = next; return vi.fn() }),
    dispose: vi.fn<() => void>(),
  } satisfies SettingsWorkspaceBridgeClient
  return {
    client, createClient: vi.fn(() => client),
    emit(response: SettingsWorkspaceResponse): void { listener?.(response) },
    lastRequest(): SettingsWorkspaceRequest {
      const call = client.sendSettingsWorkspace.mock.calls.at(-1)
      if (call === undefined) throw new Error('No settings workspace request was sent.')
      return call[0]
    },
  }
}
async function mount(initialProps: SettingsPanelProps = {}, strict = false) {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  const client = workspaceClient()
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  let props = initialProps
  let current: SettingsWorkspaceContextValue | undefined
  function Observer() { current = useSettingsWorkspace(); return null }
  const unmount = async () => { if (!cleanups.delete(unmount)) return; await act(async () => { root.unmount() }); container.remove() }
  cleanups.add(unmount)
  const render = async (changes: Partial<SettingsPanelProps> = {}) => {
    props = { ...props, ...changes }
    await act(async () => {
      const page = <SettingsWorkspaceStateProvider createClient={client.createClient}><Observer /><SettingsPanel {...props} /></SettingsWorkspaceStateProvider>
      root.render(strict ? <StrictMode>{page}</StrictMode> : page)
    })
  }
  await render()
  return {
    ...client, container, render,
    read(): SettingsWorkspaceContextValue { if (current === undefined) throw new Error('Settings observer is not mounted.'); return current },
    button(name: string): HTMLButtonElement { return within(container).getByRole('button', { name }) as HTMLButtonElement },
    dialog(): HTMLElement { return within(container).getByRole('alertdialog') },
  }
}
type Harness = Awaited<ReturnType<typeof mount>>
async function click(h: Harness, name: string): Promise<void> { await act(async () => { h.button(name).click() }) }
async function respond(h: Harness, snapshot: AnySettingsSnapshot): Promise<void> { await act(async () => { h.emit(available(h.lastRequest(), snapshot)) }) }
async function selectGroup(h: Harness, name: string): Promise<void> { await act(async () => { (within(h.container).getByRole('tab', { name }) as HTMLButtonElement).click() }) }
async function selectRow(h: Harness, name: string): Promise<void> {
  const list = h.container.querySelector<HTMLElement>('.settings-list-items')!
  await act(async () => { (within(list).getByRole('button', { name }) as HTMLButtonElement).click() })
}
function listRows(h: Harness): HTMLButtonElement[] { return Array.from(h.container.querySelectorAll<HTMLButtonElement>('.settings-list-items .settings-list-row')) }
function editor(h: Harness): HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement {
  const control = h.container.querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>('.setting-inspector input, .setting-inspector textarea, .setting-inspector select')
  if (control === null) throw new Error('No setting value control is rendered.')
  return control
}
async function change(h: Harness, value: string): Promise<void> { await act(async () => { fireEvent.change(editor(h), { target: { value } }) }) }
function externalButton(): HTMLButtonElement {
  const element = document.createElement('button')
  element.textContent = '設定の呼出し元'
  document.body.append(element)
  externalElements.add(element)
  return element
}
function definition(key: string, overrides: Partial<SettingDefinition> = {}): SettingDefinition {
  return { key, displayName: key, description: '検証用の設定です。', group: 'projectAndOutput', isAdvanced: false, unit: 'none',
    defaultValue: { kind: 'boolean', booleanValue: false }, minimum: null, maximum: null, allowedTextValues: [],
    evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }], ...overrides }
}
function text(textValue: string): SettingValue { return { kind: 'text', textValue } }
function integer(integerValue: string): SettingValue { return { kind: 'integer', integerValue } }
function identity(snapshot: AnySettingsSnapshot) {
  return { expectedRevision: snapshot.revision, expectedProfileId: snapshot.profileId, expectedProfileVersion: snapshot.profileVersion, expectedProfileDigest: snapshot.profileDigest, expectedSnapshotDigest: snapshot.snapshotDigest }
}
function withValue(snapshot: AnySettingsSnapshot, key: string, value: SettingValue): AnySettingsSnapshot {
  return { ...snapshot, revision: snapshot.revision + 1, snapshotDigest: 'f'.repeat(64), values: snapshot.values.map(item => item.key === key ? { key, value } : item) }
}
function available(request: SettingsWorkspaceRequest, snapshot: AnySettingsSnapshot): SettingsWorkspaceResponse {
  return response({ version: 1, requestId: request.requestId, method: request.method,
    result: request.method.startsWith('settings.file.') ? { status: 'available', snapshot, fileSha256 } : { status: 'available', snapshot } })
}
function failed(request: SettingsWorkspaceRequest, status: Exclude<SettingsFileStatus, 'available'>): SettingsWorkspaceResponse {
  return response({ version: 1, requestId: request.requestId, method: request.method,
    result: request.method.startsWith('settings.file.') ? { status, snapshot: null, fileSha256: status === 'overwriteRequired' ? fileSha256 : null } : { status, snapshot: null } })
}
function response(value: unknown): SettingsWorkspaceResponse {
  const parsed = parseSettingsWorkspaceResponse(value)
  if (parsed === null) throw new Error('Invalid settings panel response fixture.')
  return parsed
}
