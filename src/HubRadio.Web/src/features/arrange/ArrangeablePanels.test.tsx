// @vitest-environment jsdom
// NFR-UX-005 AC-129/AC-130 (keyboard), NFR-UX-002 AC-127 (save / restore / reset with confirmation), FR-063 AC-038.
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { ArrangeablePanels } from './ArrangeablePanels'
import { ARRANGE_STORAGE_PREFIX, type ArrangeStorage } from './arrangeStorage'

function memoryStorage(): ArrangeStorage & { data: Map<string, string> } {
  const data = new Map<string, string>()
  return { data, getItem: key => data.get(key) ?? null, setItem: (key, value) => { data.set(key, value) }, removeItem: key => { data.delete(key) } }
}

function Screen({ storage }: { storage: ArrangeStorage }) {
  return (
    <ArrangeablePanels screenId="t" storage={storage} panels={[
      { id: 'one', label: '一つ目', children: <input aria-label="入力" /> },
      { id: 'two', label: '二つ目', children: <p>内容2</p> },
    ]} />
  )
}

describe('ArrangeablePanels', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })

  const panel = (id: string) => container.querySelector<HTMLElement>(`[data-panel-id="${id}"]`)!
  const buttons = (name: string) => [...container.querySelectorAll<HTMLButtonElement>('button')].filter(b => b.textContent === name)
  const click = (b: HTMLElement | undefined) => { act(() => { b!.click() }) }
  const key = (b: HTMLElement, k: string) => { act(() => { b.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true })) }) }
  const show = (storage: ArrangeStorage) => act(() => root.render(<Screen storage={storage} />))

  it('keeps typed input through move / tab / hide / maximize (AC-129, AC-038)', () => {
    show(memoryStorage())
    const input = container.querySelector('input')!
    input.value = 'typed'
    click(buttons('移動')[0]); click(buttons('後ろへ')[0])
    click(buttons('タブ化')[0])
    click(buttons('最大化')[0])
    expect(panel('two').hidden).toBe(true)
    click(buttons('最大化')[0])
    click(buttons('非表示')[1])
    expect(panel('two').hidden).toBe(true)
    expect(container.querySelector('input')).toBe(input)
    expect(input.value).toBe('typed')
    expect(buttons('二つ目を表示')).toHaveLength(1)
  })

  // NFR-UX-005 AC-129: E2E-024 sequence — each operation is looked up by name (first reachable one) and pressed once, in order.
  it('keeps every operation reachable and the input intact for 移動→タブ化→非表示→最大化 (AC-129)', () => {
    show(memoryStorage())
    const input = container.querySelector('input')!
    input.value = 'typed-in-progress'
    const reachable = (name: string) => buttons(name).find(b => !b.closest('[hidden]') && !b.disabled)
    for (const op of ['移動', 'タブ化', '非表示', '最大化']) {
      const target = reachable(op)
      expect(target, `${op} reachable`).toBeDefined()
      click(target)
      expect(input.value).toBe('typed-in-progress')
    }
    expect(container.querySelector('input')).toBe(input)
    for (const op of ['移動', 'タブ化', '非表示', '最大化', '配置を変更', '既定に戻す']) expect(reachable(op), `${op} still reachable`).toBeDefined()
    // The last visible panel cannot be hidden, but its button stays present and pressable (no state change, announced).
    click(reachable('非表示'))
    expect(container.querySelectorAll('[data-panel-id]:not([hidden])')).toHaveLength(1)
    expect(container.querySelector('[role="status"]')!.textContent).toContain('非表示にできません')
  })

  it('moves with the keyboard without dragging (AC-130)', () => {
    show(memoryStorage())
    const move = buttons('移動')[0]!
    key(move, 'ArrowRight')
    expect(panel('one').style.order).toBe('4')
    expect(panel('two').style.order).toBe('2')
    expect(container.querySelector('[role="status"]')!.textContent).toContain('後ろ')
    key(move, 'ArrowLeft')
    expect(panel('one').style.order).toBe('2')
  })

  it('shows a tab strip for 2+ tabbed panels, one panel at a time', () => {
    show(memoryStorage())
    for (const b of buttons('タブ化')) click(b)
    expect(container.querySelector('[role="tablist"]')).not.toBeNull()
    expect(panel('one').hidden).toBe(false)
    expect(panel('two').hidden).toBe(true)
    click([...container.querySelectorAll<HTMLElement>('[role="tab"]')].find(t => t.textContent === '二つ目'))
    expect(panel('two').hidden).toBe(false)
    expect(panel('one').hidden).toBe(true)
  })

  it('saves, restores after a remount, and resets only after confirmation (AC-127)', () => {
    const storage = memoryStorage()
    show(storage)
    click(buttons('移動')[0]); click(buttons('後ろへ')[0])
    expect(storage.data.has(ARRANGE_STORAGE_PREFIX + 't')).toBe(true)
    act(() => root.unmount())
    root = createRoot(container)
    show(storage)
    expect(panel('one').style.order).toBe('4')
    click(buttons('既定に戻す')[0])
    expect(panel('one').style.order).toBe('4') // confirmation first
    click(buttons('やめる')[0])
    expect(panel('one').style.order).toBe('4')
    click(buttons('既定に戻す')[0])
    click(buttons('既定に戻す（確定）')[0])
    expect(panel('one').style.order).toBe('2')
    expect(storage.data.has(ARRANGE_STORAGE_PREFIX + 't')).toBe(false)
  })

  it('offers direction and size controls in 配置を変更 mode', () => {
    show(memoryStorage())
    expect(buttons('縦に並べる')).toHaveLength(0)
    click(buttons('配置を変更')[0])
    click(buttons('縦に並べる')[0])
    expect(container.querySelector('.arrange-container')!.getAttribute('data-direction')).toBe('column')
    click(buttons('大きく')[0])
    expect(panel('one').style.flexGrow).toBe('2')
  })
})
