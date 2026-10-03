// @vitest-environment jsdom

import { fireEvent, within } from '@testing-library/dom'
import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SettingInspector, type SettingInspectorProps } from './SettingInspector'
import type { AnySettingsSnapshot, SettingDefinition, SettingValue } from './settingsProtocol'

const boolean = (booleanValue: boolean) => ({ kind: 'boolean', booleanValue } as const)
const integer = (integerValue: string) => ({ kind: 'integer', integerValue } as const)
const rational = (numerator: string, denominator: string) => ({ kind: 'rational', numerator, denominator } as const)
const text = (textValue: string) => ({ kind: 'text', textValue } as const)
const textList = (...textListValue: string[]) => ({ kind: 'textList', textListValue } as const)

function makeDefinition(defaultValue: SettingValue, overrides: Partial<SettingDefinition> = {}): SettingDefinition {
  return {
    key: 'editing.test', group: 'editing', isAdvanced: false, displayName: 'テスト設定', description: '設定の用途を説明します。', unit: 'none',
    defaultValue, minimum: null, maximum: null, allowedTextValues: [],
    evidence: [{ kind: 'specification', sourceId: 'R01' }, { kind: 'productDecision', sourceId: 'productDecision' }],
    ...overrides,
  }
}

const mounted: { root: Root; container: HTMLDivElement }[] = []
beforeEach(() => { vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true) })
afterEach(async () => {
  for (const { root, container } of mounted.splice(0)) {
    await act(async () => { root.unmount() })
    container.remove()
  }
  vi.unstubAllGlobals()
})

async function renderInspector(overrides: Partial<SettingInspectorProps> = {}) {
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  mounted.push({ root, container })
  const definition = overrides.definition ?? makeDefinition(boolean(false))
  const value = overrides.value ?? definition.defaultValue
  const onSave = vi.fn()
  const onReset = vi.fn()
  // Emulate the parent-owned draft; the inspector never owns this storage.
  const onChange = vi.fn((draft: SettingValue) => {
    props = { ...props, draft }
    root.render(<SettingInspector {...props} />)
  })
  let props: SettingInspectorProps = { definition, value, initialValue: definition.defaultValue, draft: value, scope: 'project', disabled: false, onChange, onSave, onReset, ...overrides }
  async function rerender(next: Partial<SettingInspectorProps> = {}) {
    props = { ...props, ...next }
    await act(async () => { root.render(<SettingInspector {...props} />) })
  }
  await rerender()
  return { container, root, queries: within(container), user: userEvent.setup(), onChange, onSave, onReset, rerender, get props() { return props } }
}

function summaryValue(container: HTMLElement, label: string) {
  return within(container).getByText(label, { selector: 'dt' }).nextElementSibling?.textContent
}

const valueCases = [
  { name: 'boolean', value: boolean(true), initialValue: boolean(false), draft: boolean(false), currentText: '有効', initialText: '無効', inputs: ['false'] },
  { name: 'integer', value: integer('20'), initialValue: integer('10'), draft: integer('30'), currentText: '20', initialText: '10', inputs: ['30'] },
  { name: 'rational', value: rational('30000', '1001'), initialValue: rational('24', '1'), draft: rational('30', '1'), currentText: '30000/1001', initialText: '24/1', inputs: ['30', '1'] },
  { name: 'text', value: text('現在の日本語'), initialValue: text('初期の日本語'), draft: text('編集中の日本語'), currentText: '現在の日本語', initialText: '初期の日本語', inputs: ['編集中の日本語'] },
  { name: 'textList', value: textList('現在の項目', '二番目'), initialValue: textList('初期の項目'), draft: textList('編集中の項目', '追加の項目'), currentText: '現在の項目\n二番目', initialText: '初期の項目', inputs: ['編集中の項目', '追加の項目'] },
] satisfies readonly { name: string; value: SettingValue; initialValue: SettingValue; draft: SettingValue; currentText: string; initialText: string; inputs: readonly string[] }[]

describe('SettingInspector', () => {
  it('shows the plain-language explanation of a bundled setting without opening any disclosure', async () => {
    const definition = makeDefinition(boolean(false), { key: 'output.directPostingEnabled', displayName: 'Direct posting' })
    const view = await renderInspector({ definition })
    const help = view.container.querySelector('section.setting-help')!

    expect(help.closest('details')).toBeNull()
    expect(within(help as HTMLElement).getByRole('heading', { level: 4 }).textContent).toBe('やさしい説明：直接投稿')
    expect(help.textContent).toContain('これは何の設定？')
    expect(help.textContent).toContain('YouTubeなどのサービスへ自動で投稿するか')
    expect(help.textContent).toContain('変えるとどうなる？')
    expect(help.textContent).toContain('投稿はしません')
    expect(help.textContent).toContain('初期値のままにしておくのが安全')
    expect(help.getAttribute('aria-labelledby')).toBe(help.querySelector('h4')!.id)
  })

  it('states honestly that a setting without a written explanation has none', async () => {
    const view = await renderInspector({ definition: makeDefinition(boolean(false)) })
    const help = view.container.querySelector('section.setting-help')!

    expect(help.querySelector('h4')!.textContent).toBe('やさしい説明：テスト設定')
    expect(help.textContent).toContain('まだ用意されていません')
  })

  it.each(valueCases)('distinguishes current, draft and initial values for $name', async ({ value, initialValue, draft, currentText, initialText, inputs }) => {
    const view = await renderInspector({ definition: makeDefinition(initialValue), value, initialValue, draft })
    expect(summaryValue(view.container, '現在値')).toBe(currentText)
    expect(summaryValue(view.container, '復元先（案件開始時の初期値）')).toBe(initialText)
    expect(summaryValue(view.container, 'プロファイル既定値')).toBe(initialText)
    const controls = Array.from(view.container.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>('input,select,textarea'))
    expect(controls.map(control => control.value)).toEqual(inputs)
    for (const control of controls) {
      expect(control.getAttribute('aria-invalid')).toBe('false')
      expect(control.getAttribute('aria-label') ?? control.labels?.[0]?.textContent).toContain('未保存の値')
    }
    expect(view.queries.getByRole('status').textContent).toBe('未保存の変更があります。')
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(false)
    expect(view.onChange).not.toHaveBeenCalled()
    expect(view.onSave).not.toHaveBeenCalled()
    expect(view.onReset).not.toHaveBeenCalled()
  })

  const definition = makeDefinition(integer('3'), { isAdvanced: true })
  const projectSnapshot: AnySettingsSnapshot = {
    projectId: '11112222-3333-4444-8555-666677778888', profileId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', profileName: 'test', profileVersion: '1.2.3',
    profileDigest: 'a'.repeat(64), initialSnapshotDigest: 'b'.repeat(64), snapshotDigest: 'c'.repeat(64), revision: 2, updatedAt: '2026-09-07T00:00:00.0000000+00:00', definitionCount: 1,
    definitions: [definition], initialValues: [{ key: definition.key, value: integer('5') }], values: [{ key: definition.key, value: integer('7') }],
  }
  const globalSnapshot: AnySettingsSnapshot = { ...projectSnapshot, projectId: null, initialValues: [{ key: definition.key, value: definition.defaultValue }] }

  it.each([
    { scope: 'global', snapshot: globalSnapshot, scopeLabel: '新しい動画の既定値', restoreLabel: '復元先（プロファイル初期値）', resetLabel: 'プロファイル初期値へ戻す', initial: '3' },
    { scope: 'project', snapshot: projectSnapshot, scopeLabel: 'この動画', restoreLabel: '復元先（案件開始時の初期値）', resetLabel: '案件開始時の値へ戻す', initial: '5' },
  ] as const)('shows the $scope restore source and one-level profile/evidence details', async ({ scope, snapshot, scopeLabel, restoreLabel, resetLabel, initial }) => {
    const view = await renderInspector({ scope, definition: snapshot.definitions[0]!, value: snapshot.values[0]!.value, initialValue: snapshot.initialValues[0]!.value, draft: integer('9'), profileVersion: snapshot.profileVersion })
    expect(view.queries.getByText(scopeLabel)).toBeDefined()
    expect(view.queries.getByText('詳細', { selector: '.setting-badge' })).toBeDefined()
    const details = view.container.querySelector('details')!
    expect(details.open).toBe(false)
    await act(async () => { await view.user.click(details.querySelector('summary')!) })
    expect(details.open).toBe(true)
    expect(view.container.querySelectorAll('details')).toHaveLength(1)
    expect(details.querySelector('details')).toBeNull()
    expect(summaryValue(view.container, restoreLabel)).toBe(initial)
    expect(summaryValue(view.container, 'プロファイル既定値')).toBe('3')
    expect(summaryValue(view.container, 'プロファイル版')).toBe('v1.2.3')
    expect(summaryValue(view.container, '用途')).toBe(definition.description)
    expect(summaryValue(view.container, '設定キー')).toBe(definition.key)
    expect(Array.from(details.querySelectorAll('li')).map(item => item.textContent)).toEqual(['R01 · specification', 'productDecision · productDecision'])
    await act(async () => { await view.user.click(view.queries.getByRole('button', { name: resetLabel })) })
    expect(view.onReset).toHaveBeenCalledExactlyOnceWith()
    expect(view.onChange).not.toHaveBeenCalled()
    expect(view.onSave).not.toHaveBeenCalled()
  })

  it('allows the parent header to own the profile version without inventing one', async () => {
    const view = await renderInspector()
    expect(view.queries.queryByText('プロファイル版', { selector: 'dt' })).toBeNull()
    expect(view.queries.queryByText('最小値', { selector: 'dt' })).toBeNull()
    expect(view.queries.queryByText('最大値', { selector: 'dt' })).toBeNull()
    expect(view.queries.getByRole('combobox').textContent).not.toContain('none')
  })

  it('changes a boolean accessibly and saves only on explicit submit, without claiming persistence', async () => {
    const view = await renderInspector()
    const control = view.queries.getByRole<HTMLSelectElement>('combobox', { name: 'テスト設定：未保存の値' })
    expect(Array.from(control.options).map(option => [option.value, option.textContent])).toEqual([['true', '有効'], ['false', '無効']])
    await act(async () => { await view.user.selectOptions(control, 'true'); await view.user.tab() })
    expect(view.onChange).toHaveBeenCalledExactlyOnceWith(boolean(true))
    expect(view.onSave).not.toHaveBeenCalled()
    expect(summaryValue(view.container, '現在値')).toBe('無効')
    const save = view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' })
    expect(save.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true')
    await act(async () => { await view.user.click(save) })
    expect(view.onSave).toHaveBeenCalledExactlyOnceWith()
    expect(view.onReset).not.toHaveBeenCalled()
    expect(summaryValue(view.container, '現在値')).toBe('無効')
    expect(view.queries.getByRole('status').textContent).toBe('未保存の変更があります。')
    await view.rerender({ value: boolean(true) })
    expect(save.disabled).toBe(true)
    expect(view.queries.getByRole('status').textContent).toBe('未保存の変更はありません。')
    expect(view.onSave).toHaveBeenCalledTimes(1)
  })

  it.each(['-9223372036854775808', '9007199254740993', '9223372036854775807'])('preserves integer string %s in the control and callback', async (input) => {
    const view = await renderInspector({ definition: makeDefinition(integer('0'), { unit: 'milliseconds' }) })
    const control = view.queries.getByRole<HTMLInputElement>('textbox', { name: 'テスト設定 (milliseconds)：未保存の値' })
    await act(async () => { fireEvent.change(control, { target: { value: input } }) })
    expect(control.value).toBe(input)
    expect(view.onChange).toHaveBeenCalledExactlyOnceWith(integer(input))
    expect(view.queries.queryByRole('alert')).toBeNull()
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(false)
    expect(view.onSave).not.toHaveBeenCalled()
  })

  it('retains a partial minus through parent rerenders before completing an integer', async () => {
    const view = await renderInspector({ definition: makeDefinition(integer('0'), { minimum: integer('-10'), maximum: integer('10') }) })
    const control = view.queries.getByRole<HTMLInputElement>('textbox', { name: 'テスト設定：未保存の値' })
    const save = view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' })
    expect(control.type).toBe('text')
    expect(control.inputMode).toBe('numeric')
    await act(async () => { await view.user.clear(control) })
    await act(async () => { await view.user.type(control, '-') })
    expect(control.value).toBe('-')
    expect(view.props.draft).toEqual(integer('-'))
    expect(view.onChange).toHaveBeenLastCalledWith(integer('-'))
    expect(view.queries.getByRole('alert').textContent).toContain('整数')
    expect(save.disabled).toBe(true)
    await view.rerender({ value: integer('2') })
    expect(control.value).toBe('-')
    expect(view.props.draft).toEqual(integer('-'))
    expect(save.disabled).toBe(true)
    await act(async () => { fireEvent.submit(view.container.querySelector('form')!) })
    expect(view.onSave).not.toHaveBeenCalled()
    await act(async () => { await view.user.type(control, '5') })
    expect(control.value).toBe('-5')
    expect(view.onChange).toHaveBeenLastCalledWith(integer('-5'))
    expect(view.queries.queryByRole('alert')).toBeNull()
    expect(save.disabled).toBe(false)
    expect(view.onSave).not.toHaveBeenCalled()
  })

  it('preserves both rational strings and their separately accessible labels', async () => {
    const view = await renderInspector({ definition: makeDefinition(rational('1', '1'), { unit: 'seconds' }) })
    const numerator = view.queries.getByRole<HTMLInputElement>('textbox', { name: 'テスト設定 分子 (seconds)：未保存の値' })
    const denominator = view.queries.getByRole<HTMLInputElement>('textbox', { name: 'テスト設定 分母：未保存の値' })
    await act(async () => { fireEvent.change(numerator, { target: { value: '9007199254740993' } }) })
    expect(view.onChange).toHaveBeenLastCalledWith(rational('9007199254740993', '1'))
    await act(async () => { fireEvent.change(denominator, { target: { value: '9223372036854775807' } }) })
    expect(view.onChange).toHaveBeenLastCalledWith(rational('9007199254740993', '9223372036854775807'))
    expect(numerator.value).toBe('9007199254740993')
    expect(denominator.value).toBe('9223372036854775807')
    expect(view.queries.queryByRole('alert')).toBeNull()
    expect(summaryValue(view.container, '現在値')).toBe('1/1 (seconds)')
    expect(view.onSave).not.toHaveBeenCalled()
  })

  it('uses only allowed text options and rejects an invalid external draft', async () => {
    const view = await renderInspector({ definition: makeDefinition(text('標準'), { allowedTextValues: ['標準', '高品質'] }) })
    const control = view.queries.getByRole<HTMLSelectElement>('combobox', { name: 'テスト設定：未保存の値' })
    expect(Array.from(control.options).map(option => option.value)).toEqual(['標準', '高品質'])
    await act(async () => { await view.user.selectOptions(control, '高品質') })
    expect(view.onChange).toHaveBeenCalledExactlyOnceWith(text('高品質'))
    expect(view.onSave).not.toHaveBeenCalled()
    await view.rerender({ draft: text('候補にない値') })
    expect(control.value).toBe('候補にない値')
    expect(control.selectedOptions[0]!.disabled).toBe(true)
    expect(view.queries.getByRole('alert').textContent).toBe('候補から値を選択してください。')
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(true)
  })

  it('keeps long Japanese, multiline and markup-like text reachable without interpreting or truncating it', async () => {
    const current = `<b>保存済みの文字列</b>\n${'日本語😀'.repeat(600)}`
    const initial = `初期値\n${'長い初期値'.repeat(600)}`
    const profileDefault = `既定値\n${'長い既定値'.repeat(600)}`
    const description = `用途\n${'説明の続き'.repeat(600)}`
    const view = await renderInspector({ definition: makeDefinition(text(profileDefault), { description }), value: text(current), initialValue: text(initial), draft: text(current) })
    const control = view.queries.getByRole<HTMLTextAreaElement>('textbox', { name: 'テスト設定：未保存の値' })
    expect(control.tagName).toBe('TEXTAREA')
    expect(control.value).toBe(current)
    expect(summaryValue(view.container, '現在値')).toBe(current)
    await act(async () => { await view.user.click(view.container.querySelector('summary')!) })
    expect(summaryValue(view.container, '復元先（案件開始時の初期値）')).toBe(initial)
    expect(summaryValue(view.container, 'プロファイル既定値')).toBe(profileDefault)
    expect(summaryValue(view.container, '用途')).toBe(description)
    expect(view.container.querySelector('b')).toBeNull()
    const changed = '編'.repeat(4096)
    await act(async () => { fireEvent.change(control, { target: { value: changed } }) })
    expect(control.value).toBe(changed)
    expect(view.onChange).toHaveBeenLastCalledWith(text(changed))
    expect(view.queries.queryByRole('alert')).toBeNull()
    await act(async () => { fireEvent.change(control, { target: { value: `${changed}追` } }) })
    expect(control.value).toBe(`${changed}追`)
    expect(view.onChange).toHaveBeenLastCalledWith(text(`${changed}追`))
    expect(view.queries.getByRole('alert').textContent).toContain('4096')
    expect(view.onSave).not.toHaveBeenCalled()
  })

  it('preserves embedded newlines when editing the second entry and remounts the unsaved list', async () => {
    const initial = textList('甲\n乙', '丙')
    const definition = makeDefinition(initial)
    const view = await renderInspector({ definition })
    const first = view.queries.getByRole<HTMLTextAreaElement>('textbox', { name: 'テスト設定 項目 1：未保存の値' })
    const second = view.queries.getByRole<HTMLTextAreaElement>('textbox', { name: 'テスト設定 項目 2：未保存の値' })
    expect(first.tagName).toBe('TEXTAREA')
    expect(first.value).toBe('甲\n乙')
    expect(second.value).toBe('丙')
    await act(async () => { fireEvent.change(second, { target: { value: '丁' } }) })
    expect(view.onChange).toHaveBeenCalledExactlyOnceWith(textList('甲\n乙', '丁'))
    expect(first.value).toBe('甲\n乙')
    expect(second.value).toBe('丁')
    expect(view.queries.queryByRole('alert')).toBeNull()
    const retained = view.props.draft
    await view.rerender({ value: textList('更新済み') })
    expect(first.value).toBe('甲\n乙')
    expect(second.value).toBe('丁')
    const other = makeDefinition(text('別の設定'), { key: 'editing.other' })
    await view.rerender({ definition: other, value: other.defaultValue, initialValue: other.defaultValue, draft: other.defaultValue })
    await act(async () => { view.root.render(null) })
    await view.rerender({ definition, value: initial, initialValue: initial, draft: retained })
    expect(view.queries.getAllByRole<HTMLTextAreaElement>('textbox').map(control => control.value)).toEqual(['甲\n乙', '丁'])
    expect(view.queries.getByRole('status').textContent).toBe('未保存の変更があります。')
    expect(view.onChange).toHaveBeenCalledTimes(1)
    expect(view.onSave).not.toHaveBeenCalled()
    expect(view.onReset).not.toHaveBeenCalled()
  })

  it('keeps long current values keyboard reachable and save actions outside the list scroller', async () => {
    const items = Array.from({ length: 12 }, (_, index) => `項目${index + 1}\n説明の続き`)
    const view = await renderInspector({ definition: makeDefinition(textList(...items)) })
    const current = view.container.querySelector<HTMLElement>('.setting-current-value')!
    expect(current.tabIndex).toBe(0)
    expect(current.getAttribute('aria-label')).toBe('保存済みの現在値')
    expect(summaryValue(view.container, '現在値')).toBe(items.join('\n'))
    await act(async () => { current.focus() })
    expect(document.activeElement).toBe(current)
    const editor = view.container.querySelector('.setting-list-editor')!
    expect(editor.querySelectorAll('textarea')).toHaveLength(items.length)
    expect(editor.contains(view.queries.getByRole('button', { name: '保存' }))).toBe(false)
    expect(view.container.querySelector('.setting-actions')?.parentElement?.tagName).toBe('FORM')
    expect(view.onChange).not.toHaveBeenCalled()
    expect(view.onSave).not.toHaveBeenCalled()
  })

  it('adds and removes entries without discarding blank or unfinished text-list drafts', async () => {
    const view = await renderInspector({ definition: makeDefinition(textList('甲')) })
    const add = view.queries.getByRole<HTMLButtonElement>('button', { name: '項目を追加' })
    expect(add.type).toBe('button')
    await act(async () => { await view.user.click(add) })
    expect(view.onChange).toHaveBeenLastCalledWith(textList('甲', ''))
    expect(view.queries.getAllByRole('textbox')).toHaveLength(2)
    const second = view.queries.getByRole<HTMLTextAreaElement>('textbox', { name: 'テスト設定 項目 2：未保存の値' })
    expect(second.value).toBe('')
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(true)
    await act(async () => { fireEvent.change(second, { target: { value: '乙\n' } }) })
    await view.rerender()
    expect(second.value).toBe('乙\n')
    expect(view.props.draft).toEqual(textList('甲', '乙\n'))
    expect(view.queries.getByRole('alert').textContent).toContain('各項目')
    await act(async () => { fireEvent.change(second, { target: { value: '乙' } }) })
    expect(view.onChange).toHaveBeenLastCalledWith(textList('甲', '乙'))
    expect(view.queries.queryByRole('alert')).toBeNull()
    const remove = view.queries.getByRole<HTMLButtonElement>('button', { name: 'テスト設定 項目 1 を削除' })
    expect(remove.type).toBe('button')
    await act(async () => { await view.user.click(remove) })
    expect(view.onChange).toHaveBeenLastCalledWith(textList('乙'))
    const remaining = view.queries.getByRole<HTMLTextAreaElement>('textbox', { name: 'テスト設定 項目 1：未保存の値' })
    expect(remaining.value).toBe('乙')
    await act(async () => { fireEvent.change(remaining, { target: { value: '' } }) })
    expect(view.onChange).toHaveBeenLastCalledWith(textList(''))
    expect(remaining.value).toBe('')
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(true)
    await act(async () => { await view.user.click(view.queries.getByRole('button', { name: 'テスト設定 項目 1 を削除' })) })
    expect(view.onChange).toHaveBeenLastCalledWith(textList())
    expect(view.queries.queryAllByRole('textbox')).toHaveLength(0)
    expect(view.queries.queryByRole('alert')).toBeNull()
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(false)
    await view.rerender({ value: textList(), initialValue: textList() })
    expect(summaryValue(view.container, '現在値')).toBe('（なし）')
    await act(async () => { await view.user.click(add) })
    expect(view.onChange).toHaveBeenLastCalledWith(textList(''))
    expect(view.queries.getByRole<HTMLTextAreaElement>('textbox').value).toBe('')
    expect(view.onSave).not.toHaveBeenCalled()
    expect(view.onReset).not.toHaveBeenCalled()
  })

  it('keeps duplicate multiline entries editable and removes only the selected entry', async () => {
    const view = await renderInspector({ definition: makeDefinition(textList('甲\n乙', '丙')) })
    const second = view.queries.getByRole('textbox', { name: 'テスト設定 項目 2：未保存の値' })
    await act(async () => { fireEvent.change(second, { target: { value: '甲\n乙' } }) })
    expect(view.onChange).toHaveBeenCalledExactlyOnceWith(textList('甲\n乙', '甲\n乙'))
    await view.rerender()
    expect(view.queries.getAllByRole<HTMLTextAreaElement>('textbox').map(control => control.value)).toEqual(['甲\n乙', '甲\n乙'])
    expect(view.queries.getByRole('alert').textContent).toContain('重複')
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(true)
    await act(async () => { await view.user.click(view.queries.getByRole('button', { name: 'テスト設定 項目 2 を削除' })) })
    expect(view.onChange).toHaveBeenLastCalledWith(textList('甲\n乙'))
    expect(view.queries.getByRole<HTMLTextAreaElement>('textbox').value).toBe('甲\n乙')
    expect(view.queries.queryByRole('alert')).toBeNull()
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(false)
    expect(view.onSave).not.toHaveBeenCalled()
  })

  it('limits additions to 64 entries and allows adding again after removal', async () => {
    const entries = Array.from({ length: 63 }, (_, index) => `項目${index + 1}`)
    const view = await renderInspector({ definition: makeDefinition(textList(...entries)) })
    const add = view.queries.getByRole<HTMLButtonElement>('button', { name: '項目を追加' })
    expect(add.disabled).toBe(false)
    await act(async () => { await view.user.click(add) })
    expect(view.onChange).toHaveBeenCalledExactlyOnceWith(textList(...entries, ''))
    expect(view.queries.getAllByRole('textbox')).toHaveLength(64)
    expect(add.disabled).toBe(true)
    await act(async () => { await view.user.click(add) })
    expect(view.onChange).toHaveBeenCalledTimes(1)
    await act(async () => { fireEvent.change(view.queries.getByRole('textbox', { name: 'テスト設定 項目 64：未保存の値' }), { target: { value: '末尾\n続き' } }) })
    expect(view.queries.queryByRole('alert')).toBeNull()
    expect(add.disabled).toBe(true)
    await act(async () => { await view.user.click(view.queries.getByRole('button', { name: 'テスト設定 項目 1 を削除' })) })
    expect(view.onChange).toHaveBeenLastCalledWith(textList(...entries.slice(1), '末尾\n続き'))
    expect(add.disabled).toBe(false)
    await act(async () => { await view.user.click(add) })
    expect(view.onChange).toHaveBeenLastCalledWith(textList(...entries.slice(1), '末尾\n続き', ''))
    expect(view.queries.getAllByRole('textbox')).toHaveLength(64)
    expect(add.disabled).toBe(true)
    expect(view.onSave).not.toHaveBeenCalled()
  })

  it.each([
    { name: 'integer', minimum: integer('10'), maximum: integer('20'), below: integer('9'), above: integer('21'), unit: 'count' },
    { name: 'int64 precision', minimum: integer('9007199254740992'), maximum: integer('9007199254740993'), below: integer('9007199254740991'), above: integer('9007199254740994'), unit: 'count' },
    { name: 'negative integer', minimum: integer('-10'), maximum: integer('-5'), below: integer('-11'), above: integer('-4'), unit: 'decibels' },
    { name: 'rational', minimum: rational('1', '3'), maximum: rational('2', '3'), below: rational('1', '4'), above: rational('3', '4'), unit: 'seconds' },
    { name: 'rational precision', minimum: rational('1', '2'), maximum: rational('9007199254740992', '9007199254740993'), below: rational('1', '3'), above: rational('1', '1'), unit: 'seconds' },
    { name: 'negative rational', minimum: rational('-3', '2'), maximum: rational('-1', '2'), below: rational('-2', '1'), above: rational('0', '1'), unit: 'decibels' },
  ] as const)('validates inclusive minimum/maximum exactly for $name', async ({ minimum, maximum, below, above, unit }) => {
    const view = await renderInspector({ definition: makeDefinition(minimum, { minimum, maximum, unit }), draft: below })
    const save = view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' })
    expect(view.queries.getByRole('alert').textContent).toContain('最小値')
    expect(view.queries.getByRole('alert').textContent).toContain(unit)
    expect(save.disabled).toBe(true)
    await view.rerender({ draft: above })
    expect(view.queries.getByRole('alert').textContent).toContain('最大値')
    expect(save.disabled).toBe(true)
    await act(async () => { fireEvent.submit(view.container.querySelector('form')!) })
    expect(view.onSave).not.toHaveBeenCalled()
    await view.rerender({ value: maximum, draft: minimum })
    expect(view.queries.queryByRole('alert')).toBeNull()
    expect(save.disabled).toBe(false)
    await view.rerender({ value: minimum, draft: maximum })
    expect(view.queries.queryByRole('alert')).toBeNull()
    expect(save.disabled).toBe(false)
    if (minimum.kind === 'integer' && maximum.kind === 'integer') {
      expect(summaryValue(view.container, '最小値')).toBe(`${minimum.integerValue} (${unit})`)
      expect(summaryValue(view.container, '最大値')).toBe(`${maximum.integerValue} (${unit})`)
    }
  })

  it.each([
    { name: 'empty integer', draft: integer('') },
    { name: 'decimal integer', draft: integer('1.5') },
    { name: 'exponent integer', draft: integer('1e3') },
    { name: 'noncanonical integer', draft: integer('01') },
    { name: 'integer overflow', draft: integer('9223372036854775808') },
    { name: 'integer underflow', draft: integer('-9223372036854775809') },
    { name: 'missing numerator', draft: rational('', '1') },
    { name: 'missing denominator', draft: rational('1', '') },
    { name: 'zero denominator', draft: rational('1', '0') },
    { name: 'negative denominator', draft: rational('1', '-1') },
    { name: 'rational numerator overflow', draft: rational('9223372036854775808', '1') },
    { name: 'rational denominator overflow', draft: rational('1', '9223372036854775808') },
    { name: 'empty text', draft: text('') },
    { name: 'whitespace text', draft: text(' ') },
    { name: 'untrimmed text', draft: text(' 前後の空白 ') },
    { name: 'invalid Unicode', draft: text('\ud800') },
    { name: 'duplicate list', draft: textList('甲', '甲') },
    { name: 'blank list entry', draft: textList('甲', '') },
    { name: 'untrimmed list entry', draft: textList(' 甲') },
    { name: 'overlong list entry', draft: textList('甲'.repeat(4097)) },
    { name: 'too many list entries', draft: textList(...Array.from({ length: 65 }, (_, index) => `項目${index}`)) },
  ])('announces $name, associates the error with controls and prevents submission', async ({ draft }) => {
    const initial = valueCases.find(item => item.value.kind === draft.kind)!.initialValue
    const view = await renderInspector({ definition: makeDefinition(initial), draft })
    const alert = view.queries.getByRole('alert')
    expect(alert.textContent?.length).toBeGreaterThan(0)
    for (const control of view.container.querySelectorAll('input,select,textarea')) {
      expect(control.getAttribute('aria-invalid')).toBe('true')
      expect(control.getAttribute('aria-describedby')?.split(' ')).toContain(alert.id)
    }
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(true)
    await act(async () => { fireEvent.submit(view.container.querySelector('form')!) })
    expect(view.onSave).not.toHaveBeenCalled()
    expect(view.onChange).not.toHaveBeenCalled()
    await view.rerender({ draft: initial })
    expect(view.queries.queryByRole('alert')).toBeNull()
    for (const control of view.container.querySelectorAll('input,select,textarea')) expect(control.getAttribute('aria-invalid')).toBe('false')
  })

  it.each(valueCases)('disables saving an unchanged $name and resetting an unchanged initial value', async ({ value }) => {
    const draft = value.kind === 'textList' ? textList(...value.textListValue) : { ...value }
    const view = await renderInspector({ definition: makeDefinition(value), value, initialValue: value, draft })
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(true)
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '案件開始時の値へ戻す' }).disabled).toBe(true)
    await act(async () => { fireEvent.submit(view.container.querySelector('form')!) })
    expect(view.onSave).not.toHaveBeenCalled()
  })

  it('compares rational fields rather than object property insertion order', async () => {
    const view = await renderInspector({ definition: makeDefinition(rational('3', '2')), draft: { denominator: '2', numerator: '3', kind: 'rational' } })
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(true)
    expect(view.queries.getByRole('status').textContent).toBe('未保存の変更はありません。')
  })

  it.each(valueCases)('disables all $name controls and mutation callbacks while keeping details reachable', async ({ value, initialValue, draft }) => {
    const view = await renderInspector({ definition: makeDefinition(initialValue), value, initialValue, draft, disabled: true })
    for (const control of view.container.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement | HTMLButtonElement>('input,select,textarea,button')) expect(control.disabled).toBe(true)
    await act(async () => {
      await view.user.click(view.queries.getByRole('button', { name: '保存' }))
      await view.user.click(view.queries.getByRole('button', { name: '案件開始時の値へ戻す' }))
      if (value.kind === 'textList') {
        await view.user.click(view.queries.getByRole('button', { name: '項目を追加' }))
        await view.user.click(view.queries.getByRole('button', { name: 'テスト設定 項目 1 を削除' }))
      }
      fireEvent.submit(view.container.querySelector('form')!)
      await view.user.click(view.container.querySelector('summary')!)
    })
    expect(view.container.querySelector('details')!.open).toBe(true)
    expect(view.onChange).not.toHaveBeenCalled()
    expect(view.onSave).not.toHaveBeenCalled()
    expect(view.onReset).not.toHaveBeenCalled()
  })

  it('resets by current versus initial, not by draft, and leaves draft replacement to the parent', async () => {
    const view = await renderInspector({ definition: makeDefinition(integer('1')), value: integer('2'), initialValue: integer('1'), draft: integer('') })
    const reset = view.queries.getByRole<HTMLButtonElement>('button', { name: '案件開始時の値へ戻す' })
    expect(reset.disabled).toBe(false)
    await act(async () => { await view.user.click(reset) })
    expect(view.onReset).toHaveBeenCalledExactlyOnceWith()
    expect(view.onChange).not.toHaveBeenCalled()
    expect(view.onSave).not.toHaveBeenCalled()
    expect(view.queries.getByRole<HTMLInputElement>('textbox').value).toBe('')
    expect(summaryValue(view.container, '現在値')).toBe('2')
    await view.rerender({ value: integer('1'), draft: integer('3') })
    expect(reset.disabled).toBe(true)
    expect(view.queries.getByRole<HTMLButtonElement>('button', { name: '保存' }).disabled).toBe(false)
  })

  it('is fully controlled and never derives a new draft from current-value updates', async () => {
    const onChange = vi.fn()
    const view = await renderInspector({ definition: makeDefinition(integer('1')), value: integer('2'), draft: integer('3'), onChange })
    const control = view.queries.getByRole<HTMLInputElement>('textbox')
    await act(async () => { fireEvent.change(control, { target: { value: '4' } }) })
    expect(onChange).toHaveBeenCalledExactlyOnceWith(integer('4'))
    expect(control.value).toBe('3')
    await view.rerender({ value: integer('5') })
    expect(control.value).toBe('3')
    await view.rerender({ draft: integer('4') })
    expect(control.value).toBe('4')
    expect(onChange).toHaveBeenCalledTimes(1)
    expect(view.onSave).not.toHaveBeenCalled()
    expect(view.onReset).not.toHaveBeenCalled()
  })

  it('renders the retained parent draft after selecting another row and remounting', async () => {
    const first = makeDefinition(text('保存済み'), { key: 'editing.first' })
    const second = makeDefinition(text('別の設定'), { key: 'editing.second' })
    const view = await renderInspector({ definition: first })
    await act(async () => { fireEvent.change(view.queries.getByRole('textbox'), { target: { value: '保持する下書き\n続き' } }) })
    const retained = view.props.draft
    await view.rerender({ definition: second, value: second.defaultValue, initialValue: second.defaultValue, draft: second.defaultValue })
    expect(view.queries.getByRole<HTMLTextAreaElement>('textbox').value).toBe('別の設定')
    await act(async () => { view.root.render(null) })
    await view.rerender({ definition: first, value: first.defaultValue, initialValue: first.defaultValue, draft: retained })
    expect(view.queries.getByRole<HTMLTextAreaElement>('textbox').value).toBe('保持する下書き\n続き')
    expect(summaryValue(view.container, '現在値')).toBe('保存済み')
    expect(view.onChange).toHaveBeenCalledTimes(1)
    expect(view.onSave).not.toHaveBeenCalled()
    expect(view.onReset).not.toHaveBeenCalled()
  })

  it('closes only its one-level details on Escape and returns focus without changing values', async () => {
    const view = await renderInspector({ draft: boolean(true) })
    const details = view.container.querySelector('details')!
    const summary = details.querySelector('summary')!
    await act(async () => { await view.user.click(summary) })
    expect(details.open).toBe(true)
    await act(async () => { fireEvent.keyDown(details, { key: 'Escape' }) })
    expect(details.open).toBe(false)
    expect(document.activeElement).toBe(summary)
    expect(view.queries.getByRole<HTMLSelectElement>('combobox').value).toBe('true')
    expect(view.onChange).not.toHaveBeenCalled()
    expect(view.onSave).not.toHaveBeenCalled()
    expect(view.onReset).not.toHaveBeenCalled()
  })

  it('keeps label and description ids distinct for simultaneous global/project inspectors', async () => {
    const view = await renderInspector({ definition: makeDefinition(rational('1', '1')) })
    await act(async () => { view.root.render(<><SettingInspector {...view.props} scope="global" /><SettingInspector {...view.props} scope="project" /></>) })
    const ids = Array.from(view.container.querySelectorAll('[id]')).map(element => element.id)
    expect(new Set(ids).size).toBe(ids.length)
    for (const form of view.container.querySelectorAll('form')) {
      const ownIds = new Set(Array.from(form.querySelectorAll('[id]')).map(element => element.id))
      for (const input of form.querySelectorAll('input')) {
        for (const reference of input.getAttribute('aria-describedby')!.split(' ')) expect(ownIds.has(reference)).toBe(true)
      }
    }
  })
})