// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CharacterPortraitRegistration } from './CharacterPortraitRegistration'

// FR-137 AC-155: the user registers their own standing picture with an explicit rights state.
describe('CharacterPortraitRegistration', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })

  it('cannot register until a name, an image and a rights state are all given', () => {
    const onRegister = vi.fn()
    act(() => root.render(<CharacterPortraitRegistration portraits={[]} onRegister={onRegister} />))
    expect(container.textContent).toContain('登録された立ち絵はありません')
    expect(container.textContent).toContain('同梱していません')
    expect((container.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true)
    expect(onRegister).not.toHaveBeenCalled()
  })

  it('shows each registered character with the rights state and the subtitle color as text', () => {
    act(() => root.render(<CharacterPortraitRegistration onRegister={vi.fn()} portraits={[
      { id: 'a', name: 'コパ', rights: 'confirmed', subtitleColor: '#FFD400' },
      { id: 'b', name: '相方', rights: 'unconfirmed', subtitleColor: '#4FC3F7' },
    ]} />))
    expect(container.textContent).toContain('コパ')
    expect(container.textContent).toContain('使用権を確認済み')
    expect(container.textContent).toContain('使用権は未確認')
    expect(container.textContent).toContain('#4FC3F7')
  })
})