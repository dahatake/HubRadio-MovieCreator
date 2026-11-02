// @vitest-environment jsdom
// NFR-UX-005 AC-130 (command search without dragging), NFR-UX-003 AC-128 (persona default, changeable, restorable).
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { ArrangeablePanels } from './ArrangeablePanels'
import { buildArrangeCommands, filterArrangeCommands } from './arrangeCommands'
import { defaultArrangeState, isDefaultArrangement, type ArrangePreset } from './arrangeState'
import { ARRANGE_STORAGE_PREFIX, loadArrangeState, type ArrangeStorage } from './arrangeStorage'

function memoryStorage(): ArrangeStorage & { data: Map<string, string> } {
  const data = new Map<string, string>()
  return { data, getItem: key => data.get(key) ?? null, setItem: (key, value) => { data.set(key, value) }, removeItem: key => { data.delete(key) } }
}

const ids = ['preview', 'tools']
const preset: ArrangePreset = { order: ['preview', 'tools'], sizes: { tools: 2 } }

describe('persona preset (NFR-UX-003 AC-128)', () => {
  it('builds the persona default and treats it as the default arrangement', () => {
    const state = defaultArrangeState(['tools', 'preview'], preset)
    expect(state.order).toEqual(['preview', 'tools'])
    expect(state.sizes).toEqual({ tools: 2 })
    expect(isDefaultArrangement(state, ['tools', 'preview'], preset)).toBe(true)
    expect(isDefaultArrangement({ ...state, sizes: {} }, ids, preset)).toBe(false)
  })

  it('loads the preset when nothing is saved and ignores bad preset sizes', () => {
    expect(loadArrangeState('x', ids, memoryStorage(), preset).sizes).toEqual({ tools: 2 })
    expect(defaultArrangeState(ids, { sizes: { tools: 9, nope: 2 } }).sizes).toEqual({})
  })
})

describe('arrangement command search (NFR-UX-005 AC-130)', () => {
  it('lists move / resize / show / hide / maximize commands and filters by words', () => {
    const state = defaultArrangeState(ids)
    const commands = buildArrangeCommands([{ id: 'preview', label: 'プレビュー' }, { id: 'tools', label: '内容' }], state)
    const labels = commands.map(c => c.label)
    expect(labels).toEqual(expect.arrayContaining(['プレビューを後ろへ移動', 'プレビューを大きく', 'プレビューを最大化', '内容を非表示', '既定に戻す']))
    expect(labels).not.toContain('プレビューを前へ移動')
    expect(filterArrangeCommands(commands, 'プレビュー 最大').map(c => c.label)).toEqual(['プレビューを最大化'])
    expect(filterArrangeCommands(commands, '')).toHaveLength(commands.length)
  })

  describe('in the component', () => {
    let container: HTMLElement
    let root: Root
    beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
    afterEach(() => { act(() => root.unmount()); container.remove() })
    const panel = (id: string) => container.querySelector<HTMLElement>(`[data-panel-id="${id}"]`)!
    const buttons = (name: string) => [...container.querySelectorAll<HTMLButtonElement>('button')].filter(b => b.textContent === name)
    const type = (text: string) => {
      const input = container.querySelector<HTMLInputElement>('input[role="combobox"]')!
      act(() => {
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, text)
        input.dispatchEvent(new Event('input', { bubbles: true }))
      })
      return input
    }
    const render = (storage: ArrangeStorage) => act(() => root.render(
      <ArrangeablePanels screenId="t" storage={storage} preset={preset} panels={[
        { id: 'preview', label: 'プレビュー', children: <input aria-label="入力" /> },
        { id: 'tools', label: '内容', children: <p>内容</p> },
      ]} />))

    it('starts from the persona default, changes by command, and 既定に戻す returns to the persona default', () => {
      const storage = memoryStorage()
      render(storage)
      expect(panel('tools').style.flexGrow).toBe('2')
      expect(storage.data.size).toBe(0)
      const typed = container.querySelector<HTMLInputElement>('input[aria-label="入力"]')!
      typed.value = 'keep'
      const input = type('プレビュー 後ろ')
      act(() => { input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true })) })
      expect(panel('preview').style.order).toBe('4')
      expect(storage.data.has(ARRANGE_STORAGE_PREFIX + 't')).toBe(true)
      type('既定に戻す')
      act(() => { container.querySelector<HTMLElement>('[role="option"]')!.click() })
      act(() => { buttons('既定に戻す（確定）')[0]!.click() })
      expect(panel('preview').style.order).toBe('2')
      expect(panel('tools').style.flexGrow).toBe('2')
      expect(storage.data.size).toBe(0)
      expect(container.querySelector('input[aria-label="入力"]')).toBe(typed)
      expect(typed.value).toBe('keep')
    })

    it('hides through the command list and reports no match', () => {
      render(memoryStorage())
      type('内容を非表示')
      act(() => { container.querySelector<HTMLElement>('[role="option"]')!.click() })
      expect(panel('tools').hidden).toBe(true)
      type('存在しない')
      expect(container.querySelector('[role="listbox"]')!.textContent).toContain('一致するコマンドはありません')
    })
  })
})
