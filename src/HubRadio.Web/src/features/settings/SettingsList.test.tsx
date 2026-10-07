// @vitest-environment jsdom

import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fireEvent, within } from '@testing-library/dom'
import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SettingsList, type SettingsListProps } from './SettingsList'
import type { SettingDefinition, SettingSelection, SettingValue, SettingsGroup } from './settingsProtocol'

const groups = [
  ['projectAndOutput', 'Projectと出力'],
  ['editing', '編集'],
  ['subtitlesAndGraphics', '字幕とグラフィック'],
  ['audioAndAssets', '音声と素材'],
  ['privacyAndStorage', 'プライバシーと保存'],
] as const

function definition(key: string, changes: Partial<SettingDefinition> = {}): SettingDefinition {
  return {
    key, group: 'projectAndOutput', isAdvanced: false, displayName: key, description: '設定の説明です。',
    unit: 'none', defaultValue: { kind: 'boolean', booleanValue: false }, minimum: null, maximum: null,
    allowedTextValues: [], evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }], ...changes,
  }
}

function many(count: number, group: SettingsGroup = 'projectAndOutput'): readonly SettingDefinition[] {
  return Array.from({ length: count }, (_, index) => definition(`${group}.setting${index + 1}`, { group, displayName: `設定 ${index + 1}` }))
}

let container: HTMLDivElement
let root: Root
let user: ReturnType<typeof userEvent.setup>

beforeEach(() => {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  user = userEvent.setup()
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  vi.unstubAllGlobals()
})

function mount(definitions: readonly SettingDefinition[], overrides: Partial<SettingsListProps> = {}) {
  const onSelect = vi.fn<(key: string) => void>()
  let props: SettingsListProps = {
    definitions, values: definitions.map(item => ({ key: item.key, value: item.defaultValue })),
    selectedKey: null, onSelect, ...overrides,
  }
  function rerender(changes: Partial<SettingsListProps>) {
    props = { ...props, ...changes }
    act(() => root.render(<SettingsList {...props} />))
  }
  rerender({})
  return { screen: within(container), onSelect, rerender }
}

async function click(element: HTMLElement) {
  await act(async () => { await user.click(element) })
}

function search(query: string) {
  act(() => { fireEvent.change(within(container).getByRole('searchbox', { name: '設定を検索' }), { target: { value: query } }) })
}

function rows() {
  return within(within(container).getByRole('list', { name: /の設定$/ })).queryAllByRole('button')
}

function visibleKeys() {
  return rows().map(row => row.querySelector('code')!.textContent)
}

describe('SettingsList', () => {
  it('keeps all five Japanese groups visible as native buttons, even for empty groups', async () => {
    const definitions = groups.map(([group, displayName]) => definition(`${group}.enabled`, { group, displayName }))
    const { screen, onSelect, rerender } = mount(definitions)
    const groupButtons = within(screen.getByRole('group', { name: '設定の分類' })).getAllByRole('button')
    expect(groupButtons.map(button => button.textContent)).toEqual(groups.map(([, name]) => name))
    expect(screen.queryByRole('tablist')).toBeNull()
    for (const [index, [group]] of groups.entries()) {
      await click(groupButtons[index]!)
      expect(visibleKeys()).toEqual([`${group}.enabled`])
      expect(groupButtons.filter(button => button.getAttribute('aria-pressed') === 'true')).toEqual([groupButtons[index]])
    }
    rerender({ definitions: [], values: [] })
    expect(groupButtons.every(button => !button.hasAttribute('hidden') && !(button as HTMLButtonElement).disabled)).toBe(true)
    expect(screen.getByRole('status').textContent).toBe('0件 · 0 / 0ページ')
    expect(onSelect).not.toHaveBeenCalled()
  })

  it('reaches all 123 shipped keys exactly once through five groups and bounded six-row pages', async () => {
    type InventoryEntry = Pick<SettingDefinition, 'key' | 'group' | 'displayName' | 'description' | 'isAdvanced'>
    const shippedProfileJson = readFileSync(resolve(process.cwd(), '../HubRadio.App/DefaultSettings/Profiles/1.1.0.json'), 'utf8')
    const profile = JSON.parse(shippedProfileJson) as { settings: readonly InventoryEntry[] }
    // Use the shipped inventory, not a generated set of 123 keys. Wire-value formatting is tested separately.
    const definitions = profile.settings.map(item => definition(item.key, {
      group: item.group, displayName: item.displayName, description: item.description, isAdvanced: item.isAdvanced,
    }))
    expect(definitions).toHaveLength(123)
    expect(new Set(definitions.map(item => item.key)).size).toBe(123)
    const { screen, onSelect } = mount(definitions)
    const groupButtons = within(screen.getByRole('group', { name: '設定の分類' }))
    await click(screen.getByRole('checkbox', { name: '詳細設定を表示' }))
    const visited: (string | null)[] = []
    for (const [group, name] of groups) {
      await click(groupButtons.getByRole('button', { name }))
      const expected = definitions.filter(item => item.group === group).map(item => item.key)
      expect(expected.length).toBeGreaterThan(0)
      const pages = Math.ceil(expected.length / 6)
      for (let page = 0; page < pages; page += 1) {
        expect(visibleKeys()).toEqual(expected.slice(page * 6, (page + 1) * 6))
        expect(rows().length).toBeLessThanOrEqual(6)
        visited.push(...visibleKeys())
        const next = screen.getByRole('button', { name: '次のページ' }) as HTMLButtonElement
        expect(next.disabled).toBe(page === pages - 1)
        expect((screen.getByRole('button', { name: '前のページ' }) as HTMLButtonElement).disabled).toBe(page === 0)
        if (page < pages - 1) await click(next)
      }
    }
    expect(visited).toHaveLength(123)
    expect(new Set(visited).size).toBe(123)
    expect([...visited].sort()).toEqual(definitions.map(item => item.key).sort())
    expect(onSelect).not.toHaveBeenCalled()
  })

  it('filters advanced definitions with one toggle and recovers from zero search results', async () => {
    const regular = definition('project.basic', { displayName: '基本設定' })
    const advanced = definition('project.advanced', { displayName: '高度な設定', isAdvanced: true })
    const { screen } = mount([regular, advanced])
    expect(visibleKeys()).toEqual([regular.key])
    search('高度な設定')
    expect(rows()).toHaveLength(0)
    expect(screen.getByRole('status').textContent).toBe('0件 · 0 / 0ページ')
    expect((screen.getByRole('button', { name: '前のページ' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: '次のページ' }) as HTMLButtonElement).disabled).toBe(true)
    const toggle = screen.getByRole('checkbox', { name: '詳細設定を表示' }) as HTMLInputElement
    await click(toggle)
    expect(toggle.checked).toBe(true)
    expect(visibleKeys()).toEqual([advanced.key])
    search('一致しない文字列')
    expect(rows()).toHaveLength(0)
    expect(screen.getByText(/該当する設定はありません。/)).not.toBeNull()
    search('')
    expect(visibleKeys()).toEqual([regular.key, advanced.key])
    await click(toggle)
    expect(visibleKeys()).toEqual([regular.key])
    expect(container.querySelector('details')).toBeNull()
  })

  it('shows six rows, a partial final page, exact counts, and usable previous/next boundaries', async () => {
    const definitions = many(13)
    const { screen } = mount(definitions)
    const next = screen.getByRole('button', { name: '次のページ' }) as HTMLButtonElement
    const previous = screen.getByRole('button', { name: '前のページ' }) as HTMLButtonElement
    expect(screen.getByRole('status').textContent).toBe('1–6 / 13件 · 1 / 3ページ')
    expect(previous.disabled).toBe(true)
    await click(next)
    expect(screen.getByRole('status').textContent).toBe('7–12 / 13件 · 2 / 3ページ')
    await click(next)
    expect(visibleKeys()).toEqual([definitions[12]!.key])
    expect(screen.getByRole('status').textContent).toBe('13–13 / 13件 · 3 / 3ページ')
    expect(next.disabled).toBe(true)
    await click(next)
    expect(rows()).toHaveLength(1)
    await click(previous)
    expect(visibleKeys()).toEqual(definitions.slice(6, 12).map(item => item.key))
    search('設定 13')
    expect(screen.getByRole('status').textContent).toBe('1–1 / 1件 · 1 / 1ページ')
    search('')
    expect(screen.getByRole('status').textContent).toBe('1–6 / 13件 · 1 / 3ページ')
    await click(next)
    await click(screen.getByRole('checkbox', { name: '詳細設定を表示' }))
    expect(previous.disabled).toBe(true)
    await click(next)
    await click(screen.getByRole('button', { name: '編集' }))
    expect(screen.getByRole('status').textContent).toBe('0件 · 0 / 0ページ')
    await click(screen.getByRole('button', { name: 'Projectと出力' }))
    expect(screen.getByRole('status').textContent).toBe('1–6 / 13件 · 1 / 3ページ')
  })

  it('clamps the displayed page when definitions shrink without changing selection', async () => {
    const definitions = many(13)
    const { screen, rerender, onSelect } = mount(definitions, { selectedKey: definitions[12]!.key })
    expect(screen.getByRole('status').textContent).toBe('13–13 / 13件 · 3 / 3ページ')
    rerender({ definitions: definitions.slice(0, 7) })
    expect(screen.getByRole('status').textContent).toBe('7–7 / 7件 · 2 / 2ページ')
    expect(within(screen.getByRole('group', { name: '選択中の設定' })).getByText(definitions[12]!.key)).not.toBeNull()
    await click(screen.getByRole('button', { name: '前のページ' }))
    expect(rows()).toHaveLength(6)
    expect(onSelect).not.toHaveBeenCalled()
  })

  it('searches Japanese names, keys and descriptions literally, without searching values or fuzzy matching', () => {
    const definitions = [
      definition('subtitles.enabled', { displayName: '日本語の字幕', description: '文字を表示する設定' }),
      definition('output.frameRate', { displayName: '映像の速度', description: '小数のフレームレートに対応' }),
      definition('project.title', { displayName: 'タイトル', description: '動画の見出し' }),
    ]
    mount(definitions, { values: [{ key: definitions[2]!.key, value: { kind: 'text', textValue: '値だけの検索語' } }] })
    for (const [query, key] of [['日本語', definitions[0]!.key], ['  OUTPUT.FRAMERATE  ', definitions[1]!.key], ['小数のフレーム', definitions[1]!.key]] as const) {
      search(query)
      expect(visibleKeys()).toEqual([key])
    }
    search('値だけの検索語')
    expect(rows()).toHaveLength(0)
    search('subtitels.enabled')
    expect(rows()).toHaveLength(0)
    search('   ')
    expect(rows()).toHaveLength(3)
  })

  it('leaves selection to the parent and retains its full name and draft badge across filters and pages', async () => {
    const longName = '日本語の長い設定名と省略してはいけない末尾'.repeat(12)
    const definitions = many(13).map((item, index) => index === 0 ? { ...item, displayName: longName } : item)
    const { screen, onSelect, rerender } = mount(definitions, { changedKeys: [definitions[0]!.key, definitions[8]!.key] })
    expect(rows().every(row => row.getAttribute('aria-pressed') === 'false')).toBe(true)
    await click(screen.getByRole('button', { name: longName }))
    expect(onSelect).toHaveBeenCalledExactlyOnceWith(definitions[0]!.key)
    expect(rows().every(row => row.getAttribute('aria-pressed') === 'false')).toBe(true)
    rerender({ selectedKey: definitions[0]!.key })
    expect(screen.getByRole('button', { name: longName, pressed: true, description: /未保存/ })).not.toBeNull()
    const selection = within(screen.getByRole('group', { name: '選択中の設定' }))
    await click(screen.getByRole('button', { name: '次のページ' }))
    expect(selection.getByText(longName).textContent).toBe(longName)
    expect(selection.getByText('未保存')).not.toBeNull()
    expect(selection.getByText('現在の一覧には表示されていません。')).not.toBeNull()
    expect(screen.getByRole('button', { name: definitions[8]!.displayName, description: /未保存/ })).not.toBeNull()
    search('存在しない検索')
    await click(screen.getByRole('button', { name: '編集' }))
    expect(rows()).toHaveLength(0)
    expect(selection.getByText(longName).textContent).toBe(longName)
    await click(selection.getByRole('button', { name: '選択項目を表示' }))
    expect((screen.getByRole('searchbox') as HTMLInputElement).value).toBe('')
    expect(screen.getByRole('button', { name: 'Projectと出力', pressed: true })).not.toBeNull()
    expect(screen.getByRole('button', { name: longName, pressed: true })).not.toBeNull()
    expect(document.activeElement).toBe(selection.getByRole('button', { name: '選択項目を表示' }))
    expect(onSelect).toHaveBeenCalledTimes(1)
    rerender({ changedKeys: [] })
    expect(selection.queryByText('未保存')).toBeNull()
    expect(within(rows()[0]!).queryByText('未保存')).toBeNull()
  })

  it('reveals a new parent key across groups, advanced filtering and pages, preserving matching searches', async () => {
    const otherGroup = many(14, 'privacyAndStorage').map((item, index) => ({ ...item, isAdvanced: index === 13 }))
    const definitions = [...many(1), ...otherGroup]
    const { screen, rerender, onSelect } = mount(definitions, { selectedKey: definitions[0]!.key })
    search('見つからない')
    rerender({ selectedKey: otherGroup[13]!.key })
    expect((screen.getByRole('searchbox') as HTMLInputElement).value).toBe('')
    expect(screen.getByRole('button', { name: 'プライバシーと保存', pressed: true })).not.toBeNull()
    expect((screen.getByRole('checkbox', { name: '詳細設定を表示' }) as HTMLInputElement).checked).toBe(true)
    expect(screen.getByRole('status').textContent).toBe('13–14 / 14件 · 3 / 3ページ')
    expect(screen.getByRole('button', { name: otherGroup[13]!.displayName, pressed: true })).not.toBeNull()
    search('設定')
    rerender({ selectedKey: otherGroup[12]!.key })
    expect((screen.getByRole('searchbox') as HTMLInputElement).value).toBe('設定')
    expect(screen.getByRole('status').textContent).toBe('13–14 / 14件 · 3 / 3ページ')
    await click(screen.getByRole('button', { name: '前のページ' }))
    expect(screen.getByRole('status').textContent).toBe('7–12 / 14件 · 2 / 3ページ')
    expect(onSelect).not.toHaveBeenCalled()
  })

  it('does not undo typing, group changes or advanced toggles when the selected key is unchanged', async () => {
    const selected = definition('project.advanced', { displayName: '詳細設定の項目', isAdvanced: true })
    const definitions = [selected, definition('project.caption', { displayName: '日本語字幕' })]
    const { screen, rerender } = mount(definitions, { selectedKey: selected.key })
    await act(async () => { await user.type(screen.getByRole('searchbox'), '日本語') })
    expect((screen.getByRole('searchbox') as HTMLInputElement).value).toBe('日本語')
    await click(screen.getByRole('checkbox', { name: '詳細設定を表示' }))
    await click(screen.getByRole('button', { name: '編集' }))
    rerender({ definitions: definitions.map(item => ({ ...item })), values: [{ key: selected.key, value: { kind: 'boolean', booleanValue: true } }] })
    expect((screen.getByRole('searchbox') as HTMLInputElement).value).toBe('日本語')
    expect(screen.getByRole('button', { name: '編集', pressed: true })).not.toBeNull()
    expect((screen.getByRole('checkbox') as HTMLInputElement).checked).toBe(false)
    await click(screen.getByRole('button', { name: '選択項目を表示' }))
    expect((screen.getByRole('searchbox') as HTMLInputElement).value).toBe('')
    expect((screen.getByRole('checkbox') as HTMLInputElement).checked).toBe(true)
    expect(screen.getByRole('button', { name: selected.displayName, pressed: true, description: /現在値：有効/ })).not.toBeNull()
  })

  it('reveals a selected key when definitions arrive later and does not invent a replacement for unknown keys', () => {
    const definitions = many(14, 'audioAndAssets')
    const selected = definitions[13]!
    const { screen, rerender, onSelect } = mount([], { selectedKey: selected.key })
    expect(screen.getByText('この一覧に定義がありません。')).not.toBeNull()
    expect(screen.queryByRole('button', { name: '選択項目を表示' })).toBeNull()
    rerender({ definitions })
    expect(screen.getByRole('button', { name: '音声と素材', pressed: true })).not.toBeNull()
    expect(screen.getByRole('button', { name: selected.displayName, pressed: true })).not.toBeNull()
    expect(screen.getByRole('status').textContent).toBe('13–14 / 14件 · 3 / 3ページ')
    rerender({ selectedKey: 'unknown.setting' })
    expect(screen.getByText('unknown.setting')).not.toBeNull()
    expect(rows().every(row => row.getAttribute('aria-pressed') === 'false')).toBe(true)
    rerender({ selectedKey: null })
    expect(within(screen.getByRole('group', { name: '選択中の設定' })).getByText('未選択')).not.toBeNull()
    expect(onSelect).not.toHaveBeenCalled()
  })

  it('supports native Tab, Enter and Space navigation with complete accessible row names', async () => {
    const item = definition('editing.enabled', { group: 'editing', displayName: '読み上げる設定名', isAdvanced: true })
    const { screen, onSelect } = mount([item])
    const groupButtons = within(screen.getByRole('group', { name: '設定の分類' })).getAllByRole('button')
    groupButtons[0]!.focus()
    await act(async () => { await user.tab() })
    expect(document.activeElement).toBe(groupButtons[1])
    await act(async () => { await user.keyboard('{Enter}') })
    expect(groupButtons[1]!.getAttribute('aria-pressed')).toBe('true')
    const toggle = screen.getByRole('checkbox', { name: '詳細設定を表示' })
    toggle.focus()
    await act(async () => { await user.keyboard(' ') })
    const row = screen.getByRole('button', { name: item.displayName, description: /editing\.enabled 現在値：無効 詳細/ })
    row.focus()
    await act(async () => { await user.keyboard('{Enter}'); await user.keyboard(' ') })
    expect(onSelect.mock.calls).toEqual([[item.key], [item.key]])
    expect(within(row).queryByRole('textbox')).toBeNull()
    expect(container.querySelector('form')).toBeNull()
    expect(screen.queryByRole('button', { name: /^(保存|リセット)$/ })).toBeNull()
    expect(screen.getByRole('searchbox', { name: '設定を検索', description: '選択中の分類の名前・キー・説明を検索します。' })).not.toBeNull()
    expect(screen.getByRole('navigation', { name: '設定一覧のページ' })).not.toBeNull()
  })

  it('only locks controls when explicitly disabled and preserves readable selection', async () => {
    const definitions = many(7)
    const { screen, rerender, onSelect } = mount(definitions, { selectedKey: definitions[0]!.key })
    expect((screen.getByRole('button', { name: definitions[0]!.displayName }) as HTMLButtonElement).disabled).toBe(false)
    rerender({ disabled: true })
    expect(screen.getAllByRole('button').every(button => (button as HTMLButtonElement).disabled)).toBe(true)
    expect((screen.getByRole('searchbox') as HTMLInputElement).disabled).toBe(true)
    expect((screen.getByRole('checkbox') as HTMLInputElement).disabled).toBe(true)
    await click(rows()[1]!)
    expect(onSelect).not.toHaveBeenCalled()
    expect(within(screen.getByRole('group', { name: '選択中の設定' })).getByText(definitions[0]!.displayName)).not.toBeNull()
    rerender({ disabled: false })
    await click(rows()[1]!)
    expect(onSelect).toHaveBeenCalledExactlyOnceWith(definitions[1]!.key)
  })

  it.each<{ name: string; value: SettingValue; text: string }>([
    { name: 'enabled boolean', value: { kind: 'boolean', booleanValue: true }, text: '有効' },
    { name: 'disabled boolean', value: { kind: 'boolean', booleanValue: false }, text: '無効' },
    { name: 'zero integer', value: { kind: 'integer', integerValue: '0' }, text: '0' },
    { name: 'Int64 integer', value: { kind: 'integer', integerValue: '9223372036854775807' }, text: '9223372036854775807' },
    { name: 'exact rational', value: { kind: 'rational', numerator: '-9007199254740993', denominator: '1001' }, text: '-9007199254740993/1001' },
    { name: 'literal text', value: { kind: 'text', textValue: '<script>日本語</script>' }, text: '<script>日本語</script>' },
    { name: 'text list', value: { kind: 'textList', textListValue: ['字幕', 'タイトル'] }, text: '字幕、タイトル' },
    { name: 'empty text list', value: { kind: 'textList', textListValue: [] }, text: 'なし' },
  ])('formats $name without converting precise numbers or interpreting markup', ({ value, text }) => {
    const item = definition('project.value', { displayName: '表示する値', defaultValue: value })
    const definitions: readonly SettingDefinition[] = Object.freeze([Object.freeze(item)])
    const values: readonly SettingSelection[] = Object.freeze([Object.freeze({ key: item.key, value })])
    const { screen } = mount(definitions, { values })
    expect(screen.getByRole('button', { name: item.displayName, description: `${item.key} 現在値：${text}` })).not.toBeNull()
    expect(within(rows()[0]!).getByText(`現在値：${text}`)).not.toBeNull()
    expect(container.querySelector('script')).toBeNull()
    expect(values[0]!.value).toBe(value)
  })

  it('shows missing current values as unavailable rather than substituting profile defaults', () => {
    const item = definition('project.value', { defaultValue: { kind: 'text', textValue: 'これは既定値です' } })
    const { screen } = mount([item], { values: [] })
    expect(screen.getByRole('button', { name: item.displayName, description: `${item.key} 現在値：未取得` })).not.toBeNull()
    expect(screen.queryByText('これは既定値です')).toBeNull()
  })
})