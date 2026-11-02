// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { DisclosureOutcome, DisclosureResult, DisclosureService } from './disclosureProtocol'
import { DisclosurePanel, disclosureMessages } from './DisclosurePanel'

const project = '96000000-0000-4000-8000-000000000001'
const element = { kind: 'AutoSubtitle', elementRef: 'subtitle-track', provider: 'p', modelIdentifier: 'local-model', modelVersion: '1.2.3', recordedAt: 't' } as const
const label = { destination: 'YouTube', labelText: 'AI 字幕を含みます', addedAt: 't' }
const listed = (over: Partial<DisclosureResult> = {}): DisclosureResult => ({ status: 'listed', elements: [element], labels: [], ...over })

function fakeService(list: DisclosureOutcome, add: DisclosureOutcome = { status: 'added', elements: [element], labels: [label] }) {
  const service: DisclosureService = {
    list: vi.fn(() => Promise.resolve(list)),
    addLabel: vi.fn(() => Promise.resolve(add)),
    dispose: vi.fn(),
  }
  return service
}

describe('NFR-BR-005 AC-126 DisclosurePanel', () => {
  let container: HTMLDivElement
  let root: Root

  beforeEach(() => {
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    vi.unstubAllGlobals()
  })

  async function render(service: DisclosureService | null) {
    await act(async () => { root.render(<DisclosurePanel projectId={project} service={service} />) })
  }

  async function type(name: string, value: string) {
    const input = within(container).getByLabelText<HTMLInputElement>(name)
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value)
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
  }

  const click = async (name: string) => {
    await act(async () => { within(container).getByRole('button', { name }).click() })
  }

  it('lists the recorded AI elements and the labels', async () => {
    const service = fakeService(listed({ labels: [label] }))
    await render(service)
    expect(service.list).toHaveBeenCalledWith(project)
    expect(container.textContent).toContain('自動字幕（subtitle-track / local-model 1.2.3）')
    expect(container.textContent).toContain('YouTube: AI 字幕を含みます')
  })

  it('adds a label and shows the refreshed list', async () => {
    const service = fakeService(listed())
    await render(service)
    expect(container.textContent).toContain(disclosureMessages.noLabels)
    await type('公開先', 'YouTube')
    await type('ラベルの文字', 'AI 字幕を含みます')
    await click('ラベルを追加')
    expect(service.addLabel).toHaveBeenCalledWith(project, 'YouTube', 'AI 字幕を含みます')
    expect(container.textContent).toContain('YouTube: AI 字幕を含みます')
    expect(container.querySelector('[data-ai-disclosure-message]')!.textContent).toContain(disclosureMessages.added)
  })

  it('asks for both fields and reports forbidden or unavailable without raw details', async () => {
    const service = fakeService(listed(), { status: 'forbidden', elements: [], labels: [] })
    await render(service)
    await click('ラベルを追加')
    expect(service.addLabel).not.toHaveBeenCalled()
    expect(container.textContent).toContain(disclosureMessages.emptyDestination)
    await type('公開先', 'YouTube')
    await click('ラベルを追加')
    expect(container.textContent).toContain(disclosureMessages.emptyLabel)
    await type('ラベルの文字', 'x')
    await click('ラベルを追加')
    expect(container.textContent).toContain(disclosureMessages.forbidden)
  })

  it('is unavailable without a host service', async () => {
    await render(null)
    expect(container.textContent).toContain(disclosureMessages.unavailableEnvironment)
    expect(within(container).getByRole<HTMLButtonElement>('button', { name: 'ラベルを追加' }).disabled).toBe(true)
  })
})
