// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, expectTypeOf, it, vi } from 'vitest'
import { SettingsFileActions, type SettingsFileActionsProps } from './SettingsFileActions'
import { SettingsWorkspaceStateProvider, useSettingsWorkspace, type SettingsWorkspaceContextValue } from './SettingsStateProvider'
import type { SettingsWorkspaceBridgeClient } from './settingsBridgeSession'
import { parseSettingsWorkspaceResponse, type AnySettingsSnapshot, type GlobalSettingsSnapshot, type SettingDefinition, type SettingValue, type SettingsFileStatus, type SettingsSnapshot, type SettingsWorkspaceRequest, type SettingsWorkspaceResponse } from './settingsProtocol'

const projectId = '11112222-3333-4444-8555-666677778888'
const fileSha256 = 'd'.repeat(64)
const otherFileSha256 = 'e'.repeat(64)
const enabledKey = 'subtitles.enabled'
const countKey = 'editing.cutCount'
const titleKey = 'project.title'
const importedText = '<img src=x onerror="alert(1)">\n日本語 & <script>text only</script>'
const definitions: readonly SettingDefinition[] = [
  definition(enabledKey, '字幕を付ける', boolean(true)),
  definition(countKey, 'カット数', integer('0')),
  definition('output.frameRate', 'フレームレート', rational('30000', '1001')),
  definition(titleKey, 'タイトル', text('初期タイトル')),
  definition('audio.searchTerms', '素材の検索語', { kind: 'textList', textListValue: ['甲、乙'] }),
  { ...definition('subtitles.maxLines', '字幕の行数', integer('2')), isAdvanced: true },
  definition('output.width', '幅', integer('1920')),
  definition('project.verticalShortCandidateCount', '縦型候補数', integer('3')),
  definition('thumbnails.candidateCount', '変更しない項目', integer('3')),
]
const globalSnapshot: GlobalSettingsSnapshot = {
  projectId: null, profileId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', profileName: 'File actions fixture', profileVersion: '1.0.0',
  profileDigest: 'a'.repeat(64), initialSnapshotDigest: 'a'.repeat(64), snapshotDigest: 'b'.repeat(64), revision: 0,
  updatedAt: '2026-09-07T00:00:00.0000000+00:00', definitionCount: definitions.length, definitions,
  initialValues: definitions.map(item => ({ key: item.key, value: item.defaultValue })),
  values: definitions.map(item => ({ key: item.key, value: item.key === titleKey ? text('保存済みのタイトル') : item.defaultValue })),
}
const projectSnapshot: SettingsSnapshot = { ...globalSnapshot, projectId, revision: 7, snapshotDigest: 'c'.repeat(64) }
const targets: readonly { scope: string; label: string; snapshot: AnySettingsSnapshot }[] = [
  { scope: 'global', label: '新しい動画の既定値', snapshot: globalSnapshot },
  { scope: 'project', label: 'この動画', snapshot: projectSnapshot },
]
const changedValues: Readonly<Record<string, SettingValue>> = {
  [enabledKey]: boolean(false),
  [countKey]: integer('9223372036854775807'),
  'output.frameRate': rational('9007199254740993', '9007199254740992'),
  [titleKey]: text(importedText),
  'audio.searchTerms': { kind: 'textList', textListValue: ['甲', '乙'] },
  'subtitles.maxLines': integer('3'),
  'output.width': integer('1280'),
  'project.verticalShortCandidateCount': integer('2'),
}
const cleanups = new Set<() => Promise<void>>()

afterEach(async () => {
  try { for (const cleanup of [...cleanups]) await cleanup() }
  finally { vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals() }
})

describe('SettingsFileActions workspace API', () => {
  it('exposes only visibility and input-return props and uses the current workspace provider', async () => {
    expectTypeOf<SettingsFileActionsProps>().toEqualTypeOf<{
      readonly active?: boolean
      readonly onReturnToDraft?: () => void
    }>()
    const h = await mount()
    expect(h.createClient).toHaveBeenCalledOnce()
    expect(h.client.onSettingsWorkspaceResponse).toHaveBeenCalledOnce()
    expect(h.client.sendSettingsWorkspace).not.toHaveBeenCalled()
    expect(h.button('setting.txtに保存').disabled).toBe(true)
    expect(h.button('setting.txtから読み込む').disabled).toBe(true)
    expect(h.container.textContent).toContain('先に対象の設定を読み込んでください')
    await act(async () => { expect(h.read().load()).toBe(true) })
    expect(h.container.querySelector('section')?.getAttribute('aria-busy')).toBe('true')
    await click(h, 'setting.txtに保存')
    await click(h, 'setting.txtから読み込む')
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    await act(async () => { h.emit(available(h.lastRequest(), globalSnapshot)) })
    expect(h.button('setting.txtに保存').disabled).toBe(false)
    expect(h.button('setting.txtから読み込む').disabled).toBe(false)
  })

  it.each(targets)('exports saved $scope identity with null first and blocks pending reentry', async ({ snapshot }) => {
    const h = await mount()
    await load(h, snapshot)
    const saved = h.read().state.snapshot
    await act(async () => {
      h.button('setting.txtに保存').click()
      h.button('setting.txtに保存').click()
      h.button('setting.txtから読み込む').click()
    })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    const request = h.lastRequest()
    expect(request).toMatchObject({ method: 'settings.file.export', params: { projectId: snapshot.projectId, ...identity(snapshot), expectedFileSha256: null } })
    expect(Object.keys(request.params).sort()).toEqual([...Object.keys(identity(snapshot)), 'projectId', 'expectedFileSha256'].sort())
    expect(h.button('setting.txtに保存').disabled).toBe(true)
    expect(h.button('setting.txtから読み込む').disabled).toBe(true)
    expect(h.container.textContent).not.toContain('出力しました')
    await act(async () => { h.emit(available(request, snapshot)) })
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.container.textContent).toContain('保存済み設定をsetting.txtに出力しました')
    await click(h, 'setting.txtに保存')
    expect(h.lastRequest().params).toMatchObject({ expectedFileSha256: null })
  })

  it('also blocks a double activation when the fake client responds synchronously', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    h.client.sendSettingsWorkspace.mockImplementationOnce(request => { h.emit(available(request, globalSnapshot)) })
    const save = h.button('setting.txtに保存')
    const read = h.button('setting.txtから読み込む')
    await act(async () => { save.click(); save.click(); read.click() })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    expect(h.read().state.pendingRequest).toBeNull()
    expect(h.read().state.lastCompletedMethod).toBe('settings.file.export')
  })
})

describe('SettingsFileActions export confirmations', () => {
  it.each(targets)('offers all three dirty $scope choices without saving or discarding inputs', async ({ snapshot, label }) => {
    const onReturnToDraft = vi.fn<() => void>()
    const h = await mount({ onReturnToDraft })
    await load(h, snapshot)
    await act(async () => {
      h.read().changeDraft(countKey, integer('4'))
      h.read().changeDraft(titleKey, text('未保存タイトル'))
      expect(h.read().setScope(snapshot.projectId === null ? projectId : null)).toBe(false)
    })
    const saved = h.read().state.snapshot
    const drafts = h.read().state.draftValues
    await click(h, 'setting.txtに保存')
    const warning = within(h.container).getByRole('group', { name: '未保存項目があります' })
    expect(warning.textContent).toContain(label)
    expect(within(warning).getAllByRole('button').map(button => button.textContent)).toEqual(['未保存項目へ戻る', '保存済み値だけを出力', '取消'])
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    await click(h, '未保存項目へ戻る')
    expect(onReturnToDraft).toHaveBeenCalledOnce()
    expect(h.read().state.draftValues).toBe(drafts)
    expect(within(h.container).queryByRole('group', { name: '未保存項目があります' })).toBeNull()
    await click(h, 'setting.txtに保存')
    await click(h, '取消')
    expect(h.read().state.draftValues).toBe(drafts)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    await click(h, 'setting.txtに保存')
    await click(h, '保存済み値だけを出力')
    const request = h.lastRequest()
    expect(request).toMatchObject({ method: 'settings.file.export', params: { ...identity(snapshot), expectedFileSha256: null } })
    expect(h.read().state.draftValues).toBe(drafts)
    await act(async () => { h.emit(available(request, snapshot)) })
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.read().state.draftValues).toBe(drafts)
    expect(h.read().isDirty).toBe(true)
  })

  it('explains the missing input-return callback instead of offering a working-looking no-op', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    await act(async () => { h.read().changeDraft(countKey, integer('4')) })
    await click(h, 'setting.txtに保存')
    expect(h.button('未保存項目へ戻る').disabled).toBe(true)
    expect(h.container.textContent).toContain('設定一覧から選んで保存してください')
    await click(h, '未保存項目へ戻る')
    expect(h.read().isDirty).toBe(true)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledOnce()
  })

  it.each(targets)('requires an explicit returned file hash to overwrite $scope, never a path or inferred file scope', async ({ snapshot, label }) => {
    const h = await mount()
    await load(h, snapshot)
    await click(h, 'setting.txtに保存')
    await act(async () => { h.emit(failed(h.lastRequest(), 'overwriteRequired', otherFileSha256)); h.read().clearError() })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    expect(h.read().state).toMatchObject({ exportNeedsConfirmation: true, fileSha256: otherFileSha256, error: null })
    const confirmation = within(h.container).getByRole('group', { name: 'setting.txtを上書きしますか？' })
    expect(confirmation.textContent).toContain(label)
    expect(confirmation.textContent).toContain(otherFileSha256)
    expect(confirmation.textContent).not.toMatch(/[A-Za-z]:\\|LOCALAPPDATA|\\\\/)
    expect(confirmation.querySelector<HTMLDetailsElement>('details')?.open).toBe(false)
    expect(h.button('setting.txtに保存').disabled).toBe(true)
    const overwrite = h.button('上書きする')
    await act(async () => { overwrite.click(); overwrite.click() })
    const request = h.lastRequest()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(3)
    expect(request).toMatchObject({ method: 'settings.file.export', params: { projectId: snapshot.projectId, ...identity(snapshot), expectedFileSha256: otherFileSha256 } })
    expect(h.container.textContent).not.toContain('出力しました')
    await act(async () => { h.emit(available(request, snapshot)) })
    expect(h.read().state.exportNeedsConfirmation).toBe(false)
    expect(h.container.textContent).toContain('出力しました')
  })

  it.each(['取消', '未保存項目へ戻る'])('keeps drafts and the saved snapshot when an overwrite is ended with %s', async choice => {
    const onReturnToDraft = vi.fn<() => void>()
    const h = await mount({ onReturnToDraft })
    await load(h, globalSnapshot)
    await click(h, 'setting.txtに保存')
    await act(async () => {
      h.emit(failed(h.lastRequest(), 'overwriteRequired'))
      // Even drafts added after the initial clean export must be disclosed.
      h.read().changeDraft(countKey, integer('4'))
    })
    const saved = h.read().state.snapshot
    const drafts = h.read().state.draftValues
    expect(h.container.textContent).toContain('未保存項目があります。保存済み値だけを出力')
    await click(h, choice)
    expect(onReturnToDraft).toHaveBeenCalledTimes(choice === '未保存項目へ戻る' ? 1 : 0)
    expect(h.read().state).toMatchObject({ fileSha256: null, exportNeedsConfirmation: false, error: null })
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.read().state.draftValues).toBe(drafts)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    await click(h, 'setting.txtに保存')
    expect(within(h.container).getByRole('group', { name: '未保存項目があります' })).toBeTruthy()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
  })

  it('does not automatically retry an overwrite when the file changed after confirmation', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    await click(h, 'setting.txtに保存')
    await act(async () => { h.emit(failed(h.lastRequest(), 'overwriteRequired', otherFileSha256)) })
    await click(h, '上書きする')
    await act(async () => { h.emit(failed(h.lastRequest(), 'conflict')) })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(3)
    expect(h.read().state).toMatchObject({ needsRefresh: true, fileSha256: null, exportNeedsConfirmation: false })
    expect(within(h.container).queryByRole('button', { name: '上書きする' })).toBeNull()
    expect(h.button('setting.txtに保存').disabled).toBe(true)
    expect(h.container.textContent).not.toContain('出力しました')
  })
})

describe('SettingsFileActions read-only import preview', () => {
  it.each(targets)('compares saved $scope values, not drafts/defaults, showing six changed rows at a time', async ({ snapshot }) => {
    const h = await mount()
    await load(h, snapshot)
    await act(async () => { h.read().changeDraft(enabledKey, boolean(false)); h.read().changeDraft(countKey, integer('4')) })
    const saved = h.read().state.snapshot
    const drafts = h.read().state.draftValues
    const before = JSON.stringify({ saved, drafts })
    await click(h, 'setting.txtから読み込む')
    const request = h.lastRequest()
    expect(request).toMatchObject({ method: 'settings.file.preview', params: { projectId: snapshot.projectId, ...identity(snapshot) } })
    const candidate = preview(snapshot)
    await act(async () => { h.emit(available(request, { ...candidate, definitions: candidate.definitions.map(item => ({ ...item, displayName: 'ファイル側の表示名' })) })) })
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.read().state.draftValues).toBe(drafts)
    const table = within(h.container).getByRole('table', { name: '保存済み設定とsetting.txtの差分' })
    expect(table.querySelectorAll('tbody tr')).toHaveLength(6)
    expect(cellValues(h, enabledKey)).toEqual(['有効', '無効'])
    expect(cellValues(h, countKey)).toEqual(['0', '9223372036854775807'])
    expect(cellValues(h, 'output.frameRate')).toEqual(['30000/1001', '9007199254740993/9007199254740992'])
    expect(cellValues(h, titleKey)).toEqual(['保存済みのタイトル', importedText])
    expect(cellValues(h, 'audio.searchTerms')).toEqual(['["甲、乙"]', '["甲","乙"]'])
    expect(table.textContent).toContain('字幕の行数')
    expect(table.textContent).not.toContain('初期タイトル')
    expect(table.textContent).not.toContain('ファイル側の表示名')
    expect(table.textContent).not.toContain('変更しない項目')
    expect(table.querySelector('input, textarea, select, img, script')).toBeNull()
    const keys = Array.from(table.querySelectorAll('tbody tr'), row => row.getAttribute('data-setting-key'))
    expect(h.button('前のページ').disabled).toBe(true)
    expect(h.container.textContent).toContain('1–6 / 8件 · 1 / 2ページ')
    await click(h, '次のページ')
    expect(table.querySelectorAll('tbody tr')).toHaveLength(2)
    keys.push(...Array.from(table.querySelectorAll('tbody tr'), row => row.getAttribute('data-setting-key')))
    expect(keys).toEqual(Object.keys(changedValues))
    expect(new Set(keys).size).toBe(8)
    expect(h.button('次のページ').disabled).toBe(true)
    await click(h, '前のページ')
    expect(table.querySelectorAll('tbody tr')).toHaveLength(6)
    expect(JSON.stringify({ saved, drafts })).toBe(before)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
  })

  it('shows no diff for identical reordered values without discarding drafts or writing the DB', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    await act(async () => { h.read().changeDraft(countKey, integer('4')) })
    await click(h, 'setting.txtから読み込む')
    await act(async () => { h.emit(available(h.lastRequest(), { ...globalSnapshot, values: [...globalSnapshot.values].reverse() })) })
    expect(h.container.textContent).toContain('保存済み設定との差分はありません')
    expect(h.container.querySelector('table')).toBeNull()
    expect(h.button('この設定を適用').disabled).toBe(true)
    await click(h, 'この設定を適用')
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    expect(h.read().state.draftValues).toEqual({ [countKey]: integer('4') })
  })

  it('cancels the preview and apply confirmation without discarding inputs or applying anything', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    await act(async () => { h.read().changeDraft(countKey, integer('4')) })
    const saved = h.read().state.snapshot
    const drafts = h.read().state.draftValues
    await readPreview(h, globalSnapshot)
    await click(h, 'この設定を適用')
    await click(h, '読込を取り消す')
    expect(h.read().state).toMatchObject({ importPreview: null, fileSha256: null })
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.read().state.draftValues).toBe(drafts)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    expect(h.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
  })
})

describe('SettingsFileActions explicit apply', () => {
  it.each(targets)('confirms $scope plus raw file hash and applies saved target identity only on the second action', async ({ snapshot, label }) => {
    const h = await mount()
    await load(h, snapshot)
    const saved = h.read().state.snapshot
    await readPreview(h, snapshot, otherFileSha256)
    await click(h, 'この設定を適用')
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    const confirmation = within(h.container).getByRole('group', { name: '読み込んだ設定を適用しますか？' })
    expect(confirmation.textContent).toContain(label)
    expect(confirmation.textContent).toContain(otherFileSha256)
    expect(confirmation.textContent).toContain(snapshot.snapshotDigest)
    expect(confirmation.textContent).not.toContain('未保存の変更も破棄')
    expect(confirmation.textContent).not.toMatch(/[A-Za-z]:\\|LOCALAPPDATA|\\\\/)
    await click(h, '適用する')
    const request = h.lastRequest()
    expect(request).toMatchObject({ method: 'settings.file.apply', params: { projectId: snapshot.projectId, ...identity(snapshot), expectedFileSha256: otherFileSha256 } })
    expect(request.params).not.toMatchObject({ expectedSnapshotDigest: preview(snapshot).snapshotDigest })
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.container.textContent).not.toContain('に適用しました')
    await act(async () => { h.emit(available(request, { ...preview(snapshot), revision: snapshot.revision + 1 }, otherFileSha256)) })
    expect(h.read().state.snapshot?.values).toEqual(preview(snapshot).values)
    expect(h.read().state.importPreview).toBeNull()
    expect(h.container.textContent).toContain(`「${label}」に適用しました`)
  })

  it.each(targets)('retains dirty $scope values until acknowledgement and preserves concurrent ABA/new edits', async ({ snapshot }) => {
    const h = await mount()
    await load(h, snapshot)
    await act(async () => { h.read().changeDraft(countKey, integer('4')); h.read().changeDraft(enabledKey, boolean(false)) })
    await readPreview(h, snapshot)
    const saved = h.read().state.snapshot
    const drafts = h.read().state.draftValues
    const captured = drafts[countKey]
    await click(h, 'この設定を適用')
    expect(h.container.textContent).toContain('未保存の変更も破棄する操作です。適用成功までは保持')
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    const apply = h.button('未保存の変更を破棄して適用する')
    await act(async () => { apply.click(); apply.click() })
    const request = h.lastRequest()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(3)
    expect(h.read().state.pendingRequest?.draftValues[countKey]).toBe(captured)
    expect(h.read().state.draftValues).toBe(drafts)
    expect(h.read().state.snapshot).toBe(saved)
    expect(Array.from(h.container.querySelectorAll('button')).every(button => button.matches(':disabled'))).toBe(true)
    await click(h, '読込を取り消す')
    expect(h.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
    await act(async () => {
      h.read().changeDraft(countKey, integer('5'))
      h.read().changeDraft(countKey, integer('4'))
      h.read().changeDraft(titleKey, text('適用待ちの新しい入力'))
    })
    const edited = h.read().state.draftValues[countKey]
    expect(edited).not.toBe(captured)
    await act(async () => { h.emit(available(request, { ...preview(snapshot), revision: snapshot.revision + 1 })) })
    expect(h.read().state.draftValues).toEqual({ [countKey]: integer('4'), [titleKey]: text('適用待ちの新しい入力') })
    expect(h.read().state.draftValues[countKey]).toBe(edited)
    expect(h.read().isDirty).toBe(true)
  })

  it('requires refresh and a new preview after conflict, retaining drafts and using the refreshed identity', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    await act(async () => { h.read().changeDraft(countKey, integer('4')) })
    const saved = h.read().state.snapshot
    await readPreview(h, globalSnapshot)
    await click(h, 'この設定を適用')
    await click(h, '未保存の変更を破棄して適用する')
    await act(async () => { h.emit(failed(h.lastRequest(), 'conflict')) })
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.read().state.draftValues).toEqual({ [countKey]: integer('4') })
    expect(h.read().state).toMatchObject({ needsRefresh: true, importPreview: null, fileSha256: null })
    expect(h.button('setting.txtに保存').disabled).toBe(true)
    expect(h.button('setting.txtから読み込む').disabled).toBe(true)
    await click(h, '閉じる')
    expect(h.button('setting.txtから読み込む').disabled).toBe(true)
    await click(h, '設定を再読み込み')
    const refreshed = { ...globalSnapshot, revision: 12, snapshotDigest: '9'.repeat(64) }
    expect(h.lastRequest().method).toBe('settings.global.read')
    await act(async () => { h.emit(available(h.lastRequest(), refreshed)) })
    expect(h.read().state.draftValues).toEqual({ [countKey]: integer('4') })
    expect(within(h.container).queryByRole('button', { name: 'この設定を適用' })).toBeNull()
    await readPreview(h, refreshed, otherFileSha256)
    await click(h, 'この設定を適用')
    await click(h, '未保存の変更を破棄して適用する')
    expect(h.lastRequest()).toMatchObject({ method: 'settings.file.apply', params: { ...identity(refreshed), expectedFileSha256: otherFileSha256 } })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(6)
  })
})

describe('SettingsFileActions failures and visibility', () => {
  const failures: readonly { status: Exclude<SettingsFileStatus, 'available' | 'overwriteRequired'>; message: string }[] = [
    { status: 'forbidden', message: '権限がありません' },
    { status: 'notFound', message: '対象の動画が見つかりません' },
    { status: 'unbound', message: '設定がまだ関連付けられていません' },
    { status: 'conflict', message: '設定またはファイルが別の操作で変更されました' },
    { status: 'invalid', message: '形式・設定値・サイズを確認' },
    { status: 'unavailable', message: '操作結果を確認できません' },
    { status: 'fileNotFound', message: 'setting.txtが見つかりません' },
    { status: 'profileMismatch', message: '設定プロファイルが適用対象と一致しません' },
    { status: 'snapshotMismatch', message: '設定内容とダイジェストが一致しません' },
  ]

  it.each(failures)('shows the closed $status reason without replacing saved values or drafts', async ({ status, message }) => {
    const h = await mount()
    await load(h, globalSnapshot)
    await act(async () => { h.read().changeDraft(countKey, integer('4')) })
    const saved = h.read().state.snapshot
    const drafts = h.read().state.draftValues
    await click(h, 'setting.txtから読み込む')
    await act(async () => { h.emit(failed(h.lastRequest(), status)) })
    expect(within(h.container).getByRole('alert').textContent).toContain(message)
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.read().state.draftValues).toBe(drafts)
    expect(h.read().state.importPreview).toBeNull()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
    expect(h.container.textContent).not.toContain('に適用しました')
  })

  it('never displays a transport exception or a PC absolute path as the error reason', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    h.client.sendSettingsWorkspace.mockImplementationOnce(() => { throw new Error('C:\\Users\\private\\setting.txt: private transport detail') })
    await click(h, 'setting.txtに保存')
    expect(within(h.container).getByRole('alert').textContent).toContain('操作結果を確認できません')
    expect(h.container.textContent).not.toContain('private')
    expect(h.container.textContent).not.toContain('C:\\')
    expect(h.container.textContent).not.toContain('出力しました')
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
  })

  it('retains dirty inputs after apply timeout and ignores late success without automatic retries', async () => {
    vi.useFakeTimers()
    const h = await mount()
    await load(h, globalSnapshot)
    await act(async () => { h.read().changeDraft(countKey, integer('4')) })
    const saved = h.read().state.snapshot
    await readPreview(h, globalSnapshot)
    await click(h, 'この設定を適用')
    await click(h, '未保存の変更を破棄して適用する')
    const request = h.lastRequest()
    await act(async () => { vi.advanceTimersByTime(5_000) })
    expect(within(h.container).getByRole('alert').textContent).toContain('時間内に完了しませんでした')
    expect(h.read().state).toMatchObject({ needsRefresh: true, pendingRequest: null, importPreview: null, fileSha256: null })
    await act(async () => { h.emit(available(request, { ...preview(globalSnapshot), revision: 1 })); vi.advanceTimersByTime(20_000) })
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.read().state.draftValues).toEqual({ [countKey]: integer('4') })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(3)
    expect(h.button('setting.txtに保存').disabled).toBe(true)
    expect(h.container.textContent).not.toContain('に適用しました')
  })

  it('does not carry a global preview, page or apply confirmation into another scope', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    await readPreview(h, globalSnapshot)
    await click(h, '次のページ')
    await click(h, 'この設定を適用')
    await act(async () => { expect(h.read().setScope(projectId)).toBe(true); expect(h.read().load()).toBe(true) })
    expect(h.container.querySelector('table')).toBeNull()
    expect(h.container.textContent).not.toContain('読み込んだ設定を適用しますか？')
    expect(h.container.textContent).toContain('対象：この動画')
    expect(h.button('setting.txtから読み込む').disabled).toBe(true)
    await act(async () => { h.emit(available(h.lastRequest(), projectSnapshot)) })
    await readPreview(h, projectSnapshot)
    expect(h.container.textContent).toContain('1–6 / 8件 · 1 / 2ページ')
    expect(h.button('この設定を適用')).toBeTruthy()
    expect(within(h.container).queryByRole('button', { name: '適用する' })).toBeNull()
    expect(h.client.sendSettingsWorkspace.mock.calls.some(([request]) => request.method === 'settings.file.apply')).toBe(false)
  })

  it('removes inactive controls but keeps the provider, pending preview and drafts without focus or automatic sends', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    await act(async () => { h.read().changeDraft(countKey, integer('4')) })
    await click(h, 'setting.txtから読み込む')
    const request = h.lastRequest()
    const drafts = h.read().state.draftValues
    const external = document.createElement('button')
    document.body.append(external)
    try {
      await h.render({ active: false })
      external.focus()
      expect(h.container.querySelector('button, input, summary')).toBeNull()
      expect(h.read().state.pendingRequest?.requestId).toBe(request.requestId)
      await act(async () => { h.emit(available(request, preview(globalSnapshot))) })
      expect(document.activeElement).toBe(external)
      await h.render({ active: true })
      expect(document.activeElement).toBe(external)
      expect(h.read().state.draftValues).toBe(drafts)
      expect(h.button('この設定を適用')).toBeTruthy()
      await click(h, 'この設定を適用')
      await h.render({ active: false })
      await h.render({ active: true })
      expect(h.button('この設定を適用')).toBeTruthy()
      expect(within(h.container).queryByRole('button', { name: '未保存の変更を破棄して適用する' })).toBeNull()
      expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
      expect(h.createClient).toHaveBeenCalledOnce()
      expect(h.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
      expect(h.client.dispose).not.toHaveBeenCalled()
    } finally { external.remove() }
  })
})

describe('SettingsFileActions focus continuity', () => {
  it.each(targets)('keeps the focused read button and section through a successful $scope preview response', async ({ snapshot }) => {
    const h = await mount()
    await load(h, snapshot)
    const section = h.container.querySelector('section')
    const read = h.button('setting.txtから読み込む')
    const saved = h.read().state.snapshot
    const drafts = h.read().state.draftValues
    await focusAndClick(h, 'setting.txtから読み込む')
    expect(h.button('setting.txtから読み込む')).toBe(read)
    const refocus = vi.spyOn(read, 'focus')
    await act(async () => { h.emit(available(h.lastRequest(), preview(snapshot))) })
    expect(h.container.querySelector('section')).toBe(section)
    expect(h.button('setting.txtから読み込む')).toBe(read)
    expect(read.isConnected).toBe(true)
    expect(read.disabled).toBe(false)
    expect(document.activeElement).toBe(read)
    expect(refocus).not.toHaveBeenCalled()
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.read().state.draftValues).toBe(drafts)
  })

  it('keeps the focused export button through a successful response with a new raw file hash', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    const section = h.container.querySelector('section')
    const save = h.button('setting.txtに保存')
    await focusAndClick(h, 'setting.txtに保存')
    const refocus = vi.spyOn(save, 'focus')
    await act(async () => { h.emit(available(h.lastRequest(), globalSnapshot, otherFileSha256)) })
    expect(h.container.querySelector('section')).toBe(section)
    expect(h.button('setting.txtに保存')).toBe(save)
    expect(save.disabled).toBe(false)
    expect(document.activeElement).toBe(save)
    expect(refocus).not.toHaveBeenCalled()
  })

  it.each(['上書きする', '取消'])('focuses interactive overwrite cancel, then returns to the same export button after %s', async choice => {
    const h = await mount()
    await load(h, globalSnapshot)
    const section = h.container.querySelector('section')
    const save = h.button('setting.txtに保存')
    const read = h.button('setting.txtから読み込む')
    await focusAndClick(h, 'setting.txtに保存')
    await act(async () => { h.emit(failed(h.lastRequest(), 'overwriteRequired', otherFileSha256)) })
    const cancel = h.button('取消')
    expect(document.activeElement).toBe(cancel)
    expect(cancel.matches(':disabled')).toBe(false)
    expect(h.button('setting.txtに保存')).toBe(save)
    expect(h.button('setting.txtから読み込む')).toBe(read)
    expect(save.disabled).toBe(true)
    const source = h.button(choice)
    await focusAndClick(h, choice)
    expect(source.isConnected).toBe(false)
    if (choice === '上書きする') {
      expect(h.lastRequest()).toMatchObject({ method: 'settings.file.export', params: { expectedFileSha256: otherFileSha256 } })
      expect(save.disabled).toBe(true)
      await act(async () => { h.emit(available(h.lastRequest(), globalSnapshot, otherFileSha256)) })
    }
    expect(h.container.querySelector('section')).toBe(section)
    expect(h.button('setting.txtに保存')).toBe(save)
    expect(save.disabled).toBe(false)
    expect(document.activeElement).toBe(save)
    expect(cancel.isConnected).toBe(false)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(choice === '上書きする' ? 3 : 2)
  })

  it.each(['保存済み値だけを出力', '取消'])('returns focus from a removed dirty confirmation after %s without changing drafts', async choice => {
    const h = await mount()
    await load(h, globalSnapshot)
    await act(async () => { h.read().changeDraft(countKey, integer('4')) })
    const saved = h.read().state.snapshot
    const drafts = h.read().state.draftValues
    const save = h.button('setting.txtに保存')
    await focusAndClick(h, 'setting.txtに保存')
    expect(document.activeElement).toBe(h.button('取消'))
    const source = h.button(choice)
    await focusAndClick(h, choice)
    expect(source.isConnected).toBe(false)
    if (choice === '保存済み値だけを出力') {
      expect(save.disabled).toBe(true)
      await act(async () => { h.emit(available(h.lastRequest(), globalSnapshot)) })
    }
    expect(h.button('setting.txtに保存')).toBe(save)
    expect(document.activeElement).toBe(save)
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.read().state.draftValues).toBe(drafts)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(choice === '保存済み値だけを出力' ? 2 : 1)
  })

  it.each(['適用する', '読込を取り消す'])('returns focus to the same read button when %s removes the preview controls', async choice => {
    const h = await mount()
    await load(h, globalSnapshot)
    const section = h.container.querySelector('section')
    const read = h.button('setting.txtから読み込む')
    await readPreview(h, globalSnapshot)
    const openConfirmation = h.button('この設定を適用')
    await focusAndClick(h, 'この設定を適用')
    expect(openConfirmation.isConnected).toBe(false)
    expect(document.activeElement).toBe(h.button('読込を取り消す'))
    const source = h.button(choice)
    await focusAndClick(h, choice)
    if (choice === '適用する') {
      expect(read.disabled).toBe(true)
      await act(async () => { h.emit(available(h.lastRequest(), { ...preview(globalSnapshot), revision: 1 })) })
    }
    expect(source.isConnected).toBe(false)
    expect(h.container.querySelector('section')).toBe(section)
    expect(h.button('setting.txtから読み込む')).toBe(read)
    expect(read.disabled).toBe(false)
    expect(document.activeElement).toBe(read)
    expect(h.read().state.importPreview).toBeNull()
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(choice === '適用する' ? 3 : 2)
  })

  it.each([
    { operation: 'apply', failure: 'conflict', returnTo: 'setting.txtから読み込む' },
    { operation: 'apply', failure: 'timeout', returnTo: 'setting.txtから読み込む' },
    { operation: 'apply', failure: 'sendFailed', returnTo: 'setting.txtから読み込む' },
    { operation: 'overwrite', failure: 'conflict', returnTo: 'setting.txtに保存' },
  ] as const)('focuses enabled refresh after $operation $failure, then returns to the stable $returnTo button', async ({ operation, failure, returnTo }) => {
    if (failure === 'timeout') vi.useFakeTimers()
    const h = await mount()
    await load(h, globalSnapshot)
    const section = h.container.querySelector('section')
    const read = h.button('setting.txtから読み込む')
    const save = h.button('setting.txtに保存')
    const target = h.button(returnTo)
    if (operation === 'apply') {
      await readPreview(h, globalSnapshot)
      await focusAndClick(h, 'この設定を適用')
    } else {
      await focusAndClick(h, 'setting.txtに保存')
      await act(async () => { h.emit(failed(h.lastRequest(), 'overwriteRequired', otherFileSha256)) })
    }
    const actionName = operation === 'apply' ? '適用する' : '上書きする'
    const source = h.button(actionName)
    if (failure === 'sendFailed') {
      h.client.sendSettingsWorkspace.mockImplementationOnce(() => { throw new Error('Test-only apply send failure') })
    }
    await focusAndClick(h, actionName)
    if (failure === 'timeout') await act(async () => { vi.advanceTimersByTime(5_000) })
    else if (failure === 'conflict') await act(async () => { h.emit(failed(h.lastRequest(), 'conflict')) })
    const refresh = h.button('設定を再読み込み')
    expect(source.isConnected).toBe(false)
    expect(document.activeElement).toBe(refresh)
    expect(refresh.matches(':disabled')).toBe(false)
    expect(read.disabled).toBe(true)
    expect(save.disabled).toBe(true)
    expect(h.read().state).toMatchObject({
      needsRefresh: true, pendingRequest: null, importPreview: null, fileSha256: null, exportNeedsConfirmation: false,
      error: failure === 'sendFailed' ? 'unavailable' : failure,
    })
    expect(h.container.querySelector('section')).toBe(section)
    expect(h.button('setting.txtから読み込む')).toBe(read)
    expect(h.button('setting.txtに保存')).toBe(save)
    await act(async () => { source.click() })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(3)
    const refreshFocus = vi.spyOn(refresh, 'focus')
    const returnFocus = vi.spyOn(target, 'focus')
    await focusAndClick(h, '設定を再読み込み')
    expect(h.lastRequest().method).toBe('settings.global.read')
    expect(refresh.matches(':disabled')).toBe(true)
    await h.render()
    expect(refreshFocus).toHaveBeenCalledOnce() // Only the explicit focusAndClick, never the disabled control.
    expect(returnFocus).not.toHaveBeenCalled()
    await act(async () => { h.emit(available(h.lastRequest(), { ...globalSnapshot, revision: 1, snapshotDigest: '9'.repeat(64) })) })
    expect(refresh.isConnected).toBe(false)
    expect(within(h.container).queryByRole('button', { name: '設定を再読み込み' })).toBeNull()
    expect(h.container.querySelector('section')).toBe(section)
    expect(h.button('setting.txtから読み込む')).toBe(read)
    expect(h.button('setting.txtに保存')).toBe(save)
    expect(target.matches(':disabled')).toBe(false)
    expect(document.activeElement).toBe(target)
    expect(returnFocus).toHaveBeenCalledOnce()
    expect(refreshFocus).toHaveBeenCalledOnce()
    expect(h.read().state.needsRefresh).toBe(false)
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(4)
  })

  it.each([
    { phase: 'failure', owner: 'editor' },
    { phase: 'failure', owner: 'body' },
    { phase: 'failure', owner: 'inactive' },
    { phase: 'refresh', owner: 'editor' },
    { phase: 'refresh', owner: 'body' },
    { phase: 'refresh', owner: 'inactive' },
  ] as const)('respects $owner focus on a late $phase instead of reclaiming it for recovery', async ({ phase, owner }) => {
    const h = await mount()
    await load(h, globalSnapshot)
    await readPreview(h, globalSnapshot)
    await focusAndClick(h, 'この設定を適用')
    await focusAndClick(h, '適用する')
    if (phase === 'refresh') {
      await act(async () => { h.emit(failed(h.lastRequest(), 'conflict')) })
      await focusAndClick(h, '設定を再読み込み')
    }
    const request = h.lastRequest()
    const editor = document.createElement('input')
    document.body.append(editor)
    try {
      if (owner === 'inactive') await h.render({ active: false })
      editor.focus()
      editor.value = '別項目の編集中'
      if (owner === 'body') editor.blur()
      const chosenFocus = owner === 'body' ? document.body : editor
      expect(document.activeElement).toBe(chosenFocus)
      expect(h.read().state.pendingRequest?.requestId).toBe(request.requestId)
      await act(async () => {
        h.emit(phase === 'failure' ? failed(request, 'conflict') : available(request, { ...globalSnapshot, revision: 1 }))
      })
      expect(document.activeElement).toBe(chosenFocus)
      if (owner === 'inactive') {
        expect(h.container.querySelector('button')).toBeNull()
        await h.render({ active: true })
        expect(document.activeElement).toBe(chosenFocus)
      }
      await act(async () => { h.read().clearError() })
      expect(document.activeElement).toBe(chosenFocus)
      expect(editor.value).toBe('別項目の編集中')
      expect(h.read().state.needsRefresh).toBe(phase === 'failure')
      if (phase === 'failure') expect(h.button('設定を再読み込み').matches(':disabled')).toBe(false)
      expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(phase === 'failure' ? 3 : 4)
      expect(h.createClient).toHaveBeenCalledOnce()
      expect(h.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
    } finally { editor.remove() }
  })

  it('respects an external input chosen during a synchronous apply send failure', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    await readPreview(h, globalSnapshot)
    await focusAndClick(h, 'この設定を適用')
    const editor = document.createElement('input')
    document.body.append(editor)
    try {
      h.client.sendSettingsWorkspace.mockImplementationOnce(() => {
        editor.focus()
        throw new Error('Test-only apply send failure')
      })
      await focusAndClick(h, '適用する')
      expect(document.activeElement).toBe(editor)
      expect(h.read().state).toMatchObject({ needsRefresh: true, pendingRequest: null, error: 'unavailable' })
      expect(h.button('設定を再読み込み').matches(':disabled')).toBe(false)
      expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(3)
    } finally { editor.remove() }
  })

  it.each(['preview', 'overwrite', 'apply'])('does not steal focus from another editor on a late %s response', async operation => {
    const h = await mount()
    await load(h, globalSnapshot)
    if (operation === 'apply') {
      await readPreview(h, globalSnapshot)
      await focusAndClick(h, 'この設定を適用')
      await focusAndClick(h, '適用する')
    } else {
      await focusAndClick(h, operation === 'preview' ? 'setting.txtから読み込む' : 'setting.txtに保存')
    }
    const request = h.lastRequest()
    const editor = document.createElement('input')
    document.body.append(editor)
    try {
      editor.focus()
      editor.value = '別項目の編集中'
      await act(async () => { h.read().changeDraft(titleKey, text(editor.value)) })
      const drafts = h.read().state.draftValues
      await act(async () => {
        h.emit(operation === 'overwrite' ? failed(request, 'overwriteRequired')
          : available(request, operation === 'apply' ? { ...preview(globalSnapshot), revision: 1 } : preview(globalSnapshot)))
      })
      expect(document.activeElement).toBe(editor)
      expect(editor.value).toBe('別項目の編集中')
      expect(h.read().state.draftValues).toBe(drafts)
      if (operation === 'overwrite') expect(h.button('取消').matches(':disabled')).toBe(false)
    } finally { editor.remove() }
  })

  it('does not focus a late overwrite confirmation while inactive or when it is shown again', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    await focusAndClick(h, 'setting.txtに保存')
    const request = h.lastRequest()
    const editor = document.createElement('input')
    document.body.append(editor)
    try {
      await h.render({ active: false })
      editor.focus()
      await act(async () => { h.emit(failed(request, 'overwriteRequired')) })
      expect(h.container.querySelector('button')).toBeNull()
      expect(document.activeElement).toBe(editor)
      await h.render({ active: true })
      expect(h.button('取消').matches(':disabled')).toBe(false)
      expect(document.activeElement).toBe(editor)
      expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
      expect(h.createClient).toHaveBeenCalledOnce()
      expect(h.client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
    } finally { editor.remove() }
  })
})

describe('SettingsFileActions identity-bound transient state', () => {
  it('invalidates dirty export confirmation on a new revision without replacing controls or taking editor focus', async () => {
    const h = await mount()
    await load(h, globalSnapshot)
    await act(async () => { h.read().changeDraft(countKey, integer('4')) })
    const drafts = h.read().state.draftValues
    const section = h.container.querySelector('section')
    const save = h.button('setting.txtに保存')
    const read = h.button('setting.txtから読み込む')
    await focusAndClick(h, 'setting.txtに保存')
    const stale = h.button('保存済み値だけを出力')
    const editor = document.createElement('input')
    document.body.append(editor)
    try {
      editor.focus()
      await act(async () => { expect(h.read().refresh()).toBe(true) })
      const refreshed = { ...globalSnapshot, revision: 1, snapshotDigest: '9'.repeat(64) }
      await act(async () => { h.emit(available(h.lastRequest(), refreshed)) })
      expect(within(h.container).queryByRole('group', { name: '未保存項目があります' })).toBeNull()
      expect(h.container.querySelector('section')).toBe(section)
      expect(h.button('setting.txtに保存')).toBe(save)
      expect(h.button('setting.txtから読み込む')).toBe(read)
      expect(document.activeElement).toBe(editor)
      expect(h.read().state.draftValues).toBe(drafts)
      expect(stale.isConnected).toBe(false)
      await act(async () => { stale.click() })
      expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(2)
      await click(h, 'setting.txtに保存')
      expect(document.activeElement).toBe(editor)
      await click(h, '保存済み値だけを出力')
      expect(h.lastRequest()).toMatchObject({ method: 'settings.file.export', params: identity(refreshed) })
    } finally { editor.remove() }
  })

  it.each(['raw hash', 'preview digest'])('resets page and stale apply confirmation on a changed %s, including a return to the original identity', async changed => {
    const h = await mount()
    await load(h, globalSnapshot)
    const read = h.button('setting.txtから読み込む')
    await readPreview(h, globalSnapshot)
    await click(h, '次のページ')
    await click(h, 'この設定を適用')
    await act(async () => { h.read().changeDraft(countKey, integer('4')) })
    expect(h.container.textContent).toContain('7–8 / 8件 · 2 / 2ページ')
    const stale = h.button('未保存の変更を破棄して適用する')
    const saved = h.read().state.snapshot
    const drafts = h.read().state.draftValues
    const candidate = changed === 'preview digest' ? { ...preview(globalSnapshot), snapshotDigest: '8'.repeat(64) } : preview(globalSnapshot)
    await act(async () => { expect(h.read().previewFile()).toBe(true) })
    await act(async () => { h.emit(available(h.lastRequest(), candidate, changed === 'raw hash' ? otherFileSha256 : fileSha256)) })
    expect(h.button('setting.txtから読み込む')).toBe(read)
    expect(h.container.textContent).toContain('1–6 / 8件 · 1 / 2ページ')
    expect(within(h.container).queryByRole('button', { name: '未保存の変更を破棄して適用する' })).toBeNull()
    expect(stale.isConnected).toBe(false)
    await act(async () => { stale.click() })
    expect(h.client.sendSettingsWorkspace).toHaveBeenCalledTimes(3)
    await readPreview(h, globalSnapshot)
    expect(h.container.textContent).toContain('1–6 / 8件 · 1 / 2ページ')
    expect(within(h.container).queryByRole('button', { name: '未保存の変更を破棄して適用する' })).toBeNull()
    expect(h.button('この設定を適用')).toBeTruthy()
    expect(h.read().state.snapshot).toBe(saved)
    expect(h.read().state.draftValues).toBe(drafts)
    expect(h.client.sendSettingsWorkspace.mock.calls.some(([request]) => request.method === 'settings.file.apply')).toBe(false)
    await click(h, 'この設定を適用')
    await click(h, '未保存の変更を破棄して適用する')
    expect(h.lastRequest()).toMatchObject({ method: 'settings.file.apply', params: { ...identity(globalSnapshot), expectedFileSha256: fileSha256 } })
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
    client, createClient: vi.fn(() => client),
    emit(response: SettingsWorkspaceResponse): void { listener?.(response) },
    lastRequest(): SettingsWorkspaceRequest {
      const call = client.sendSettingsWorkspace.mock.calls.at(-1)
      if (call === undefined) throw new Error('No workspace request was sent.')
      return call[0]
    },
  }
}

async function mount(props: SettingsFileActionsProps = {}) {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  const client = workspaceClient()
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  let current: SettingsWorkspaceContextValue | undefined
  function Observer() { current = useSettingsWorkspace(); return null }
  const unmount = async () => { if (!cleanups.delete(unmount)) return; await act(async () => { root.unmount() }); container.remove() }
  cleanups.add(unmount)
  const render = async (next: SettingsFileActionsProps = props) => {
    await act(async () => {
      root.render(<SettingsWorkspaceStateProvider createClient={client.createClient}><Observer /><SettingsFileActions {...next} /></SettingsWorkspaceStateProvider>)
    })
  }
  await render()
  return {
    ...client, container, render,
    read(): SettingsWorkspaceContextValue { if (current === undefined) throw new Error('Workspace observer is not mounted.'); return current },
    button(name: string): HTMLButtonElement { return within(container).getByRole('button', { name }) as HTMLButtonElement },
  }
}
type Harness = Awaited<ReturnType<typeof mount>>

async function click(h: Harness, name: string): Promise<void> { await act(async () => { h.button(name).click() }) }
async function focusAndClick(h: Harness, name: string): Promise<void> {
  await act(async () => {
    const button = h.button(name)
    button.focus()
    expect(document.activeElement).toBe(button)
    button.click()
  })
}
async function load(h: Harness, snapshot: AnySettingsSnapshot): Promise<void> {
  await act(async () => { expect(h.read().setScope(snapshot.projectId)).toBe(true); expect(h.read().load()).toBe(true) })
  await act(async () => { h.emit(available(h.lastRequest(), snapshot)) })
}
async function readPreview(h: Harness, snapshot: AnySettingsSnapshot, hash = fileSha256): Promise<void> {
  await click(h, 'setting.txtから読み込む')
  await act(async () => { h.emit(available(h.lastRequest(), preview(snapshot), hash)) })
}
function cellValues(h: Harness, key: string): (string | null)[] {
  const row = h.container.querySelector(`[data-setting-key="${key}"]`)
  expect(row).not.toBeNull()
  return Array.from(row!.querySelectorAll('td'), cell => cell.textContent)
}
function identity(snapshot: AnySettingsSnapshot) {
  return { expectedRevision: snapshot.revision, expectedProfileId: snapshot.profileId, expectedProfileVersion: snapshot.profileVersion, expectedProfileDigest: snapshot.profileDigest, expectedSnapshotDigest: snapshot.snapshotDigest }
}
function preview(snapshot: AnySettingsSnapshot): AnySettingsSnapshot {
  return { ...snapshot, snapshotDigest: 'f'.repeat(64), values: snapshot.values.map(item => ({ key: item.key, value: changedValues[item.key] ?? item.value })) }
}
function available(request: SettingsWorkspaceRequest, snapshot: AnySettingsSnapshot, hash = fileSha256): SettingsWorkspaceResponse {
  return response({ version: 1, requestId: request.requestId, method: request.method, result: request.method.startsWith('settings.file.') ? { status: 'available', snapshot, fileSha256: hash } : { status: 'available', snapshot } })
}
function failed(request: SettingsWorkspaceRequest, status: Exclude<SettingsFileStatus, 'available'>, hash = fileSha256): SettingsWorkspaceResponse {
  return response({ version: 1, requestId: request.requestId, method: request.method, result: { status, snapshot: null, fileSha256: status === 'overwriteRequired' ? hash : null } })
}
function response(value: unknown): SettingsWorkspaceResponse {
  const parsed = parseSettingsWorkspaceResponse(value)
  if (parsed === null) throw new Error('Invalid settings workspace response fixture.')
  return parsed
}
function definition(key: string, displayName: string, defaultValue: SettingValue): SettingDefinition {
  return { key, displayName, defaultValue, group: 'projectAndOutput', isAdvanced: false, description: 'Test-only setting.', unit: 'none', minimum: null, maximum: null, allowedTextValues: [], evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }] }
}
function boolean(booleanValue: boolean): SettingValue { return { kind: 'boolean', booleanValue } }
function integer(integerValue: string): SettingValue { return { kind: 'integer', integerValue } }
function rational(numerator: string, denominator: string): SettingValue { return { kind: 'rational', numerator, denominator } }
function text(textValue: string): SettingValue { return { kind: 'text', textValue } }