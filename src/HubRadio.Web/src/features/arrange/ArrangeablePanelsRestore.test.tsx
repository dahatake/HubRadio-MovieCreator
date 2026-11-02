// @vitest-environment jsdom
// NFR-UX-005 AC-129 / NFR-UX-002 AC-127: any restored arrangement (maximized / tabbed / hidden / all) still shows the
// four operation buttons on a visible panel, the sequence move -> tab -> hide -> maximize works, and typed input survives.
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { ArrangeablePanels } from './ArrangeablePanels'
import { ARRANGE_STORAGE_PREFIX, type ArrangeStorage } from './arrangeStorage'

function storageWith(state: unknown): ArrangeStorage {
  const data = new Map<string, string>([[ARRANGE_STORAGE_PREFIX + 't', JSON.stringify(state)]])
  return { getItem: key => data.get(key) ?? null, setItem: (key, value) => { data.set(key, value) }, removeItem: key => { data.delete(key) } }
}

const base = { order: ['one', 'two', 'three'], hidden: [], tabbed: [], activeTab: null, maximized: null, direction: 'default', sizes: {} }
const states: Record<string, unknown> = {
  maximized: { ...base, maximized: 'two' },
  tabbed: { ...base, tabbed: ['one', 'two'], activeTab: 'two' },
  hidden: { ...base, hidden: ['one', 'three'] },
  all: { ...base, hidden: ['one'], tabbed: ['two', 'three'], activeTab: 'three', maximized: 'three' },
  allHidden: { ...base, hidden: ['one', 'two'] },
}

describe('ArrangeablePanels restored states', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })

  const visibleButtons = (name: string) => [...container.querySelectorAll<HTMLButtonElement>('button')]
    .filter(b => b.textContent === name && b.closest('[hidden]') === null)
  const click = (b: HTMLElement | undefined) => { act(() => { b!.click() }) }

  for (const [name, state] of Object.entries(states)) {
    it(`finds the four operations and keeps input after restoring "${name}" (AC-129)`, () => {
      act(() => root.render(
        <ArrangeablePanels screenId="t" storage={storageWith(state)} panels={[
          { id: 'one', label: '一', children: <input aria-label="入力1" /> },
          { id: 'two', label: '二', children: <input aria-label="入力2" /> },
          { id: 'three', label: '三', children: <input aria-label="入力3" /> },
        ]} />))
      for (const label of ['移動', 'タブ化', '非表示', '最大化']) expect(visibleButtons(label).length, label).toBeGreaterThan(0)
      for (const label of ['配置を変更', '既定に戻す']) expect([...container.querySelectorAll('button')].some(b => b.textContent === label)).toBe(true)
      const input = container.querySelector<HTMLInputElement>('input')!
      input.value = 'typed'
      click(visibleButtons('移動')[0]); click(visibleButtons('タブ化')[0])
      click(visibleButtons('非表示')[0]); click(visibleButtons('最大化')[0])
      expect(container.querySelector<HTMLInputElement>('input')!.value).toBe('typed')
      for (const label of ['移動', 'タブ化', '最大化']) expect(visibleButtons(label).length, label).toBeGreaterThan(0)
    })
  }

  it('shows a panel even if the stored maximized panel is suppressed, and an applicable panel if all are hidden', () => {
    act(() => root.render(
      <ArrangeablePanels screenId="t" storage={storageWith({ ...base, maximized: 'two', hidden: ['one'] })} panels={[
        { id: 'one', label: '一', children: <p>1</p> },
        { id: 'two', label: '二', suppressed: true, children: <p>2</p> },
        { id: 'three', label: '三', children: <p>3</p> },
      ]} />))
    expect(visibleButtons('移動').length).toBeGreaterThan(0)
  })
})
