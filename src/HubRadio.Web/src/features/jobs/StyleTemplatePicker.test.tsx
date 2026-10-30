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

  it('lists name, purpose, orientation and a preview example for every template, without a character dialogue one', () => {
    act(() => root.render(<StyleTemplatePicker selectedId={null} onSelect={vi.fn()} />))
    expect(styleTemplateOptions.map(option => option.id)).toEqual(['explainer', 'short-pop', 'variety', 'talk-show', 'vertical-3-tier', 'handmade'])
    for (const option of styleTemplateOptions) {
      expect(container.textContent).toContain(option.name)
      expect(container.textContent).toContain(option.purpose)
      expect(container.textContent).toContain(option.orientation === 'vertical' ? '縦型' : '横長')
      expect(container.querySelector(`[aria-label="見た目の例: ${option.previewDescription}"]`)?.textContent).toBe(option.sampleText)
    }
    expect(container.textContent).not.toContain('キャラクター')
    // 7 radios: "no template" plus the six
    expect(container.querySelectorAll('input[type="radio"]')).toHaveLength(7)
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
})
