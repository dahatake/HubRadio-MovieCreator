// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fixtureId } from '../workflow/pipelineTestFixture'
import { JobCreationAction } from './JobCreationAction'
import { StyleTemplatePicker } from './StyleTemplatePicker'
import { styleTemplateOptions } from './styleTemplates'

// FR-136 AC-150, FR-137 AC-153
describe('StyleTemplatePicker', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })

  it('lists name, purpose, orientation and a preview example for every template; the character dialogue one is locked until a portrait is registered (AC-155)', () => {
    act(() => root.render(<StyleTemplatePicker selectedId={null} onSelect={vi.fn()} />))
    expect(styleTemplateOptions.map(option => option.id)).toEqual(['explainer', 'short-pop', 'variety', 'talk-show', 'vertical-3-tier', 'handmade', 'character-dialogue'])
    for (const option of styleTemplateOptions) {
      expect(container.textContent).toContain(option.name)
      expect(container.textContent).toContain(option.purpose)
      expect(container.textContent).toContain(option.orientation === 'vertical' ? '縦型' : '横長')
      expect(container.querySelector(`[aria-label="見た目の例: ${option.previewDescription}"]`)?.textContent).toBe(option.sampleText)
    }
    // 8 radios: "no template" plus the seven; the character one cannot be chosen while no portrait is registered
    const radios = [...container.querySelectorAll('input[type="radio"]')] as HTMLInputElement[]
    expect(radios).toHaveLength(8)
    expect(radios[7].disabled).toBe(true)
    expect(container.textContent).toContain('立ち絵が 1 つも登録されていないため')
    act(() => root.render(<StyleTemplatePicker selectedId={null} onSelect={vi.fn()} portraitCount={1} />))
    expect((container.querySelectorAll('input[type="radio"]')[7] as HTMLInputElement).disabled).toBe(false)
    act(() => root.render(<StyleTemplatePicker selectedId={null} onSelect={vi.fn()} />))
    expect((container.querySelector('input[type="radio"]') as HTMLInputElement).checked).toBe(true)
  })

  it('selects one template with a single choice and can go back to none', () => {
    const onSelect = vi.fn()
    act(() => root.render(<StyleTemplatePicker selectedId="variety" onSelect={onSelect} />))
    expect(container.querySelector('[role="status"]')?.textContent).toContain('バラエティ')
    const radios = [...container.querySelectorAll('input[type="radio"]')] as HTMLInputElement[]
    act(() => radios[1].click())
    expect(onSelect).toHaveBeenCalledWith('explainer')
    act(() => radios[0].click())
    expect(onSelect).toHaveBeenCalledWith(null)
  })

  it('is part of the Job creation screen only when a handler is given', () => {
    const base = {
      nextJobNumber: 2, params: { projectId: fixtureId(1), expectedVersionId: fixtureId(5), expectedVersionDigest: 'a'.repeat(64), expectedSettingsRevision: 3, expectedSettingsSnapshotDigest: 'b'.repeat(64) },
      disabledReason: null, pending: false, active: true, onOpenSettings: vi.fn(), onConfirm: vi.fn(() => true),
    }
    act(() => root.render(<JobCreationAction {...base} />))
    expect(container.querySelector('fieldset')).toBeNull()
    const onStyleTemplateChange = vi.fn()
    act(() => root.render(<JobCreationAction {...base} styleTemplateId="handmade" onStyleTemplateChange={onStyleTemplateChange} />))
    expect(container.querySelector('legend')?.textContent).toContain('演出テンプレート')
    expect((container.querySelector('input[type="radio"]:checked') as HTMLInputElement).closest('label')?.textContent).toContain('手作り感')
  })

  // FR-136 AC-150: the selected template goes along with the confirmed Job creation.
  it('sends the selected template with the confirmed Job creation', () => {
    const onConfirm = vi.fn(() => true)
    const params = { projectId: fixtureId(1), expectedVersionId: fixtureId(5), expectedVersionDigest: 'a'.repeat(64), expectedSettingsRevision: 3, expectedSettingsSnapshotDigest: 'b'.repeat(64) }
    act(() => root.render(<JobCreationAction nextJobNumber={2} params={params} disabledReason={null} pending={false} active onOpenSettings={vi.fn()}
      onConfirm={onConfirm} styleTemplateId="talk-show" onStyleTemplateChange={vi.fn()} />))
    const button = (label: string) => [...container.querySelectorAll('button')].find(item => item.textContent === label) as HTMLButtonElement
    act(() => button('新しいJobを作成').click())
    act(() => button('作成する').click())
    expect(onConfirm).toHaveBeenCalledWith(params, 'talk-show')
  })
})
