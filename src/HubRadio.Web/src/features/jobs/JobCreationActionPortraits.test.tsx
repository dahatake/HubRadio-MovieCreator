// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fixtureId } from '../workflow/pipelineTestFixture'
import { JobCreationAction } from './JobCreationAction'
import type { PortraitOutcome, PortraitService } from './portraitProtocol'

const entry = { id: 'char-0123456789ab', name: 'コパ', rights: 'unconfirmed' as const, subtitleColor: '#FFD400', rightsNote: null }
const listed = (portraits: readonly (typeof entry)[]): PortraitOutcome => ({ status: 'ok', portraitCount: portraits.length, portraits })
const flush = () => act(async () => { await Promise.resolve(); await Promise.resolve() })

// FR-137 (6) AC-155 AC-160: the registration form and the template list are connected to the bridge service.
describe('JobCreationAction standing-picture registration', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })
  const params = { projectId: fixtureId(1), expectedVersionId: fixtureId(5), expectedVersionDigest: 'a'.repeat(64), expectedSettingsRevision: 3, expectedSettingsSnapshotDigest: 'b'.repeat(64) }
  const render = (service: PortraitService | null) => act(() => root.render(<JobCreationAction nextJobNumber={2} params={params} disabledReason={null}
    pending={false} active onOpenSettings={vi.fn()} onConfirm={vi.fn(() => true)} styleTemplateId={null} onStyleTemplateChange={vi.fn()} portraitService={service} />))
  const dialogueRadio = () => [...container.querySelectorAll('input[type="radio"]')].find(radio => radio.closest('label')?.textContent?.includes('キャラクター対話')) as HTMLInputElement

  it('locks the character dialogue template while nothing is registered and unlocks it from the registered list, showing the saved rights state', async () => {
    const list = vi.fn(() => Promise.resolve(listed([])))
    const register = vi.fn(() => Promise.resolve(listed([entry])))
    render({ list, register, crop: vi.fn(), dispose: vi.fn() })
    await flush()
    expect(list).toHaveBeenCalledTimes(1)
    expect(dialogueRadio().disabled).toBe(true)
    expect(container.textContent).toContain('登録された立ち絵はありません')

    const text = container.querySelector('input[type="text"]') as HTMLInputElement
    const file = container.querySelector('input[type="file"]') as HTMLInputElement
    const confirmed = [...container.querySelectorAll('input[type="radio"]')].find(radio => radio.closest('label')?.textContent === '使用権は未確認') as HTMLInputElement
    const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
    await act(async () => { setValue.call(text, 'コパ'); text.dispatchEvent(new Event('input', { bubbles: true })) })
    await act(async () => {
      Object.defineProperty(file, 'files', { value: [new File([new Uint8Array([137, 80, 78, 71])], 'a.png', { type: 'image/png' })], configurable: true })
      file.dispatchEvent(new Event('change', { bubbles: true }))
    })
    await act(async () => { confirmed.click() })
    await act(async () => { (container.querySelector('button[type="submit"]') as HTMLButtonElement).form!.requestSubmit() })
    await flush()
    expect(register).toHaveBeenCalledWith(expect.objectContaining({ name: 'コパ', imageType: 'png', rights: 'unconfirmed' }))
    expect(container.textContent).toContain('使用権は未確認')
    expect(container.textContent).toContain('#FFD400')
    expect(container.textContent).toContain('立ち絵を登録しました')
    expect(dialogueRadio().disabled).toBe(false)
  })

  it('shows a closed message when registration is refused and keeps the template locked', async () => {
    render({ list: vi.fn(() => Promise.resolve(listed([]))), register: vi.fn(() => Promise.resolve({ status: 'invalid', portraitCount: 0, portraits: [] } as PortraitOutcome)), crop: vi.fn(), dispose: vi.fn() })
    await flush()
    expect(dialogueRadio().disabled).toBe(true)
  })

  it('hides the form when no bridge service exists', () => {
    render(null)
    expect(container.querySelector('.character-portrait-registration')).toBeNull()
  })
})
