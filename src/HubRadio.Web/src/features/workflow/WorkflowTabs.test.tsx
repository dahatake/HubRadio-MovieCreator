// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { AppIcon, type AppIconName } from '../../components/AppIcons'
import { WorkflowTabs } from './WorkflowTabs'
import { getWorkflowTabFocusStep, projectListStepLabels, projectListSteps, visibleWorkflowStep, workflowStepLabels, workflowSteps, type WorkflowStep } from './workflowNavigation'
import workflowCss from './workflow.css?inline'

const iconNames: Readonly<Record<WorkflowStep, AppIconName>> = {
  source: 'video', processing: 'processing', results: 'play', delivery: 'save',
}

describe('WorkflowTabs', () => {
  let container: HTMLDivElement
  let root: Root
  let style: HTMLStyleElement
  let onSelect = vi.fn<(step: WorkflowStep) => void>()

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    style = document.createElement('style')
    style.textContent = workflowCss
    document.head.append(style)
    onSelect = vi.fn<(step: WorkflowStep) => void>()
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    style.remove()
    vi.unstubAllGlobals()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT
  })

  it('renders four labeled icon tabs and linked panel IDs', () => {
    renderTabs()
    const tablist = within(container).getByRole('tablist', { name: '動画の作業' })
    expect(tablist.getAttribute('aria-orientation')).toBe('horizontal')
    expect(within(tablist).getAllByRole('tab')).toHaveLength(4)
    expect(within(tablist).queryAllByRole('button')).toHaveLength(0)
    expect(within(container).getAllByRole('tabpanel', { hidden: true })).toHaveLength(4)

    for (const step of workflowSteps) {
      const tab = within(tablist).getByRole('tab', { name: workflowStepLabels[step] })
      const panel = container.querySelector<HTMLElement>(`#workflow-panel-${step}`)!
      expect(tab.tagName).toBe('BUTTON')
      expect(tab.getAttribute('type')).toBe('button')
      expect(tab.id).toBe(`workflow-tab-${step}`)
      expect(tab.getAttribute('aria-controls')).toBe(panel.id)
      expect(panel.getAttribute('aria-labelledby')).toBe(tab.id)
      expect(tab.querySelector('.workflow-tab-label')?.textContent).toBe(workflowStepLabels[step])
      expect(tab.querySelector('svg')?.outerHTML).toBe(renderToStaticMarkup(<AppIcon name={iconNames[step]} />))
      expect(tab.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true')
      expect(tab.querySelector('svg')?.getAttribute('focusable')).toBe('false')
      expect(tab.hasAttribute('aria-pressed')).toBe(false)
    }
    const ids = [...container.querySelectorAll('[id]')].map(element => element.id)
    expect(ids).toHaveLength(8)
    expect(new Set(ids).size).toBe(8)
    expect(onSelect).not.toHaveBeenCalled()
  })

  it.each(workflowSteps)('keeps only %s selected and tabbable', selectedStep => {
    renderTabs(selectedStep)
    expectSelection(selectedStep)
    expect(within(container).getAllByRole('tab', { selected: true })).toEqual([tabFor(selectedStep)])
    expect(within(container).getAllByRole('tabpanel')).toEqual([
      container.querySelector(`#workflow-panel-${selectedStep}`),
    ])
    expect(onSelect).not.toHaveBeenCalled()
  })

  it('moves and wraps arrow focus without activation', async () => {
    renderTabs('results')
    const user = userEvent.setup({ document })
    act(() => tabFor('results').focus())
    const moves = [
      ['[ArrowRight]', 'delivery'],
      ['[ArrowRight]', 'source'],
      ['[ArrowLeft]', 'delivery'],
      ['[ArrowLeft]', 'results'],
      ['[ArrowLeft]', 'processing'],
      ['[ArrowLeft]', 'source'],
      ['[ArrowRight]', 'processing'],
    ] as const

    for (const [key, step] of moves) {
      await act(async () => user.keyboard(key))
      expect(document.activeElement).toBe(tabFor(step))
      expectSelection('results', step)
      expect(onSelect).not.toHaveBeenCalled()
    }
  })

  it.each([
    ['ArrowLeft', 'source'], ['ArrowRight', 'results'],
    ['Home', 'source'], ['End', 'delivery'],
  ] as const)('handles %s focus movement with default prevention', (key, step) => {
    renderTabs('processing')
    act(() => tabFor('processing').focus())
    expect(dispatchKey('processing', key).defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(tabFor(step))
    expectSelection('processing', step)
    expect(onSelect).not.toHaveBeenCalled()
  })

  it.each(['ArrowUp', 'ArrowDown', 'Tab', 'Escape', 'PageUp', 'PageDown'])(
    'leaves %s default behavior untouched', key => {
      renderTabs('processing')
      act(() => tabFor('processing').focus())
      expect(dispatchKey('processing', key).defaultPrevented).toBe(false)
      expect(document.activeElement).toBe(tabFor('processing'))
      expectSelection('processing')
      expect(onSelect).not.toHaveBeenCalled()
    },
  )

  it.each([
    { key: 'Enter', sequence: '[Enter]' },
    { key: 'Space', sequence: '[Space]' },
  ])('activates exactly once on native $key', async ({ sequence }) => {
    renderTabs()
    const user = userEvent.setup({ document })
    act(() => tabFor('source').focus())
    await act(async () => user.keyboard('[ArrowRight]'))
    expect(onSelect).not.toHaveBeenCalled()
    await act(async () => user.keyboard(sequence))
    expect(onSelect).toHaveBeenCalledExactlyOnceWith('processing')
    expectSelection('source', 'processing')

    renderTabs('processing')
    expectSelection('processing')
    expect(document.activeElement).toBe(tabFor('processing'))
    expect(onSelect).toHaveBeenCalledTimes(1)
  })

  it('activates Space on keyup only', async () => {
    renderTabs()
    const user = userEvent.setup({ document })
    act(() => tabFor('delivery').focus())
    await act(async () => user.keyboard('[Space>]'))
    expect(onSelect).not.toHaveBeenCalled()
    await act(async () => user.keyboard('[/Space]'))
    expect(onSelect).toHaveBeenCalledExactlyOnceWith('delivery')
    expectSelection('source', 'delivery')
  })

  it('allows every tab click without gating', async () => {
    renderTabs('delivery', 'processing')
    const user = userEvent.setup({ document })
    for (const step of ['source', 'results', 'processing', 'delivery'] as const) {
      expect(tabFor(step).disabled).toBe(false)
      expect(tabFor(step).hasAttribute('aria-disabled')).toBe(false)
      await act(async () => user.click(tabFor(step)))
      expect(onSelect).toHaveBeenLastCalledWith(step)
      renderTabs(step, 'processing')
      expectSelection(step)
    }
    expect(onSelect.mock.calls).toEqual([['source'], ['results'], ['processing'], ['delivery']])
  })

  it('keeps controlled updates from stealing focus', async () => {
    renderTabs()
    const user = userEvent.setup({ document })
    await act(async () => user.click(tabFor('delivery')))
    expect(onSelect).toHaveBeenCalledExactlyOnceWith('delivery')
    expectSelection('source', 'delivery')

    const outside = within(container).getByRole('button', { name: '作業の前' })
    act(() => outside.focus())
    for (const step of workflowSteps) {
      renderTabs(step)
      expectSelection(step)
      expect(document.activeElement).toBe(outside)
    }
    expect(onSelect).toHaveBeenCalledTimes(1)
  })

  it('keeps controlled updates from moving focus within the tablist', async () => {
    renderTabs('results')
    const user = userEvent.setup({ document })
    act(() => tabFor('results').focus())
    await act(async () => user.keyboard('[ArrowLeft]'))

    for (const step of workflowSteps) {
      renderTabs(step)
      expectSelection(step, 'processing')
      expect(document.activeElement).toBe(tabFor('processing'))
    }
    const before = within(container).getByRole('button', { name: '作業の前' })
    act(() => before.focus())
    expectSelection('delivery')
    await act(async () => user.tab())
    expect(document.activeElement).toBe(tabFor('delivery'))
    expectSelection('delivery')
    expect(onSelect).not.toHaveBeenCalled()
  })

  it('restores the selected tab stop when blur has no related target', () => {
    renderTabs('results')
    act(() => tabFor('processing').focus())
    expectSelection('results', 'processing')
    act(() => tabFor('processing').blur())
    expect(document.activeElement).toBe(document.body)
    expectSelection('results')
    expect(onSelect).not.toHaveBeenCalled()
  })

  it('keeps processing independent from selection', () => {
    renderTabs('delivery', 'processing')
    const outside = within(container).getByRole('button', { name: '作業の前' })
    act(() => outside.focus())

    for (const step of workflowSteps) {
      renderTabs('delivery', step)
      expect(within(container).getByRole('tab', { name: new RegExp(`^${workflowStepLabels[step]}\\s*処理中$`) })).toBe(tabFor(step))
      expect(container.querySelectorAll('.workflow-tab-status')).toHaveLength(1)
      expectSelection('delivery')
      expect(document.activeElement).toBe(outside)
      expect(workflowSteps.every(candidate => !tabFor(candidate).disabled)).toBe(true)
    }
    renderTabs('delivery')
    expect(container.querySelector('.workflow-tab-status')).toBeNull()
    expectSelection('delivery')
    expect(onSelect).not.toHaveBeenCalled()
  })

  it.each([
    ['[ArrowLeft]', 'processing'], ['[ArrowRight]', 'delivery'],
  ] as const)('exits into the active panel after %s and skips hidden panels', async (key, focusedStep) => {
    renderTabs('results')
    const user = userEvent.setup({ document })
    const before = within(container).getByRole('button', { name: '作業の前' })
    const after = within(container).getByRole('button', { name: '作業の後' })
    const panel = within(container).getByRole('tabpanel', { name: '仕上がり' })
    const control = within(panel).getByRole('button', { name: '仕上がりの操作' })
    act(() => before.focus())

    await act(async () => user.tab())
    expect(document.activeElement).toBe(tabFor('results'))
    await act(async () => user.keyboard(key))
    expect(document.activeElement).toBe(tabFor(focusedStep))
    expectSelection('results', focusedStep)
    for (const target of [panel, control, after]) {
      await act(async () => user.tab())
      expect(document.activeElement).toBe(target)
      expectSelection('results')
    }
    for (const target of [control, panel, tabFor('results'), before]) {
      await act(async () => user.tab({ shift: true }))
      expect(document.activeElement).toBe(target)
    }
    expectSelection('results')
    expect(onSelect).not.toHaveBeenCalled()
  })

  it.each([
    ['[ArrowLeft]', 'processing'], ['[ArrowRight]', 'delivery'],
  ] as const)('exits before the tablist after %s then Shift+Tab and re-enters at selection', async (key, focusedStep) => {
    renderTabs('results')
    const user = userEvent.setup({ document })
    const before = within(container).getByRole('button', { name: '作業の前' })
    const panel = within(container).getByRole('tabpanel', { name: '仕上がり' })
    act(() => panel.focus())

    await act(async () => user.tab({ shift: true }))
    expect(document.activeElement).toBe(tabFor('results'))
    await act(async () => user.keyboard(key))
    expect(document.activeElement).toBe(tabFor(focusedStep))
    expectSelection('results', focusedStep)
    await act(async () => user.tab({ shift: true }))
    expect(document.activeElement).toBe(before)
    expectSelection('results')
    await act(async () => user.tab())
    expect(document.activeElement).toBe(tabFor('results'))
    expectSelection('results')
    expect(onSelect).not.toHaveBeenCalled()
  })

  it('honors hidden on shell regions and panels', () => {
    renderTabs()
    for (const className of ['workflow-header', 'workflow-tabs', 'workflow-body', 'workflow-actions', 'workflow-shell']) {
      const element = container.querySelector<HTMLElement>(`.${className}`)!
      expect(getComputedStyle(element).display).not.toBe('none')
      element.hidden = true
      expect(getComputedStyle(element).display).toBe('none')
      element.hidden = false
    }
    const panels = within(container).getAllByRole('tabpanel', { hidden: true })
    expect(panels).toHaveLength(4)
    for (const step of workflowSteps) {
      const panel = container.querySelector<HTMLElement>(`#workflow-panel-${step}`)!
      expect(panel.hidden).toBe(step !== 'source')
      if (panel.hidden) expect(getComputedStyle(panel).display).toBe('none')
      expect(within(panel).getByRole('button', { hidden: true }).textContent).toBe(`${workflowStepLabels[step]}の操作`)
    }
  })

  it('makes no bridge or network calls', async () => {
    const postMessage = vi.fn()
    const fetch = vi.fn()
    vi.stubGlobal('chrome', { webview: { postMessage, addEventListener: vi.fn(), removeEventListener: vi.fn() } })
    vi.stubGlobal('fetch', fetch)
    renderTabs('source', 'processing')
    const user = userEvent.setup({ document })

    for (const step of workflowSteps) {
      await act(async () => user.click(tabFor(step)))
      renderTabs(step, 'processing')
    }
    await act(async () => user.keyboard('[Home][ArrowRight][Enter][End][Space]'))
    expect(onSelect).toHaveBeenCalledTimes(6)
    act(() => root.render(null))
    expect(postMessage).not.toHaveBeenCalled()
    expect(fetch).not.toHaveBeenCalled()
  })

  // Caller-owned panel fixtures only, not a production Workspace renderer.
  function renderTabs(selectedStep: WorkflowStep = 'source', processingStep: WorkflowStep | null = null): void {
    act(() => {
      root.render(
        <div className="workflow-shell">
          <header className="workflow-header"><button type="button">作業の前</button></header>
          <WorkflowTabs selectedStep={selectedStep} onSelect={onSelect} processingStep={processingStep} />
          <main className="workflow-body">
            {workflowSteps.map(step => (
              <section
                key={step}
                role="tabpanel"
                id={`workflow-panel-${step}`}
                aria-labelledby={`workflow-tab-${step}`}
                hidden={selectedStep !== step}
                tabIndex={0}
              >
                <button type="button">{workflowStepLabels[step]}の操作</button>
              </section>
            ))}
          </main>
          <footer className="workflow-actions"><button type="button">作業の後</button></footer>
        </div>,
      )
    })
  }

  function tabFor(step: WorkflowStep): HTMLButtonElement {
    const tab = container.querySelector<HTMLButtonElement>(`#workflow-tab-${step}`)
    if (tab === null) throw new Error(`Missing workflow tab: ${step}`)
    return tab
  }

  function expectSelection(selectedStep: WorkflowStep, tabbableStep: WorkflowStep = selectedStep): void {
    for (const step of workflowSteps) {
      expect(tabFor(step).getAttribute('aria-selected')).toBe(String(step === selectedStep))
      expect(tabFor(step).tabIndex).toBe(step === tabbableStep ? 0 : -1)
    }
  }

  function dispatchKey(step: WorkflowStep, key: string): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true })
    act(() => { tabFor(step).dispatchEvent(event) })
    return event
  }
})
// FR-061 AC-036.1
describe('WorkflowTabs project list (FR-061.1)', () => {
  it('shows only the given steps with label overrides and moves focus among them', async () => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    const container = document.createElement('div')
    document.body.append(container)
    const root = createRoot(container)
    const onSelect = vi.fn<(step: WorkflowStep) => void>()
    try {
      await act(async () => root.render(<WorkflowTabs selectedStep="source" onSelect={onSelect} steps={projectListSteps} labels={projectListStepLabels} />))
      const tabs = within(container).getAllByRole('tab')
      expect(tabs.map(tab => tab.textContent)).toEqual(['Project一覧', 'ジョブの状態の確認'])
      expect(container.querySelector('#workflow-tab-results, #workflow-tab-delivery')).toBeNull()
      const user = userEvent.setup({ document })
      await act(async () => { tabs[0]!.focus(); await user.keyboard('{End}') })
      expect(document.activeElement).toBe(tabs[1])
      await act(async () => { await user.keyboard('{ArrowRight}') })
      expect(document.activeElement).toBe(tabs[0])
      await act(async () => { await user.keyboard('{ArrowLeft}{Enter}') })
      expect(onSelect).toHaveBeenCalledWith('processing')
    } finally {
      act(() => root.unmount())
      container.remove()
    }
  })

  it('keeps the four project tabs, and maps hidden project-only steps back to the list', () => {
    expect(getWorkflowTabFocusStep('processing', 'ArrowRight', projectListSteps)).toBe('source')
    expect(getWorkflowTabFocusStep('results', 'ArrowRight', projectListSteps)).toBeNull()
    expect(getWorkflowTabFocusStep('delivery', 'Home')).toBe('source')
    expect(visibleWorkflowStep('results', false)).toBe('source')
    expect(visibleWorkflowStep('delivery', false)).toBe('source')
    expect(visibleWorkflowStep('processing', false)).toBe('processing')
    expect(visibleWorkflowStep('results', true)).toBe('results')
    expect(renderToStaticMarkup(<WorkflowTabs selectedStep="source" onSelect={() => undefined} />).match(/role="tab"/g)).toHaveLength(workflowSteps.length)
  })
})
