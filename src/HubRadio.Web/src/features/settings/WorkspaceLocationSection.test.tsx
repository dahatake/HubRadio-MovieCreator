// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { WorkspaceLocationSection, workspaceLocationMessages } from './WorkspaceLocationSection'
import type { WorkspaceLocationOutcome, WorkspaceLocationService } from './workspaceLocationProtocol'

const info = { status: 'available', currentPath: 'C:\\workspace', savedPath: null, defaultPath: 'C:\\workspace', restartRequired: false } as const
const failure = (status: 'invalid' | 'forbidden' | 'unavailable') =>
  ({ status, currentPath: null, savedPath: null, defaultPath: null, restartRequired: false }) as const

function fakeService(read: WorkspaceLocationOutcome = info) {
  const service: WorkspaceLocationService = {
    read: vi.fn(() => Promise.resolve(read)),
    update: vi.fn((_path: string | null) => Promise.resolve<WorkspaceLocationOutcome>(info)),
    dispose: vi.fn(),
  }
  return service
}

// FR-062 / AC-037.4
describe('FR-062 AC-037.4 WorkspaceLocationSection', () => {
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

  async function render(service: WorkspaceLocationService | null) {
    await act(async () => { root.render(<WorkspaceLocationSection service={service} />) })
    await act(async () => { await Promise.resolve() })
  }

  const input = () => within(container).getByLabelText<HTMLInputElement>('次回起動から使う場所')
  const status = () => container.querySelector('[data-workspace-message]')!
  const button = (name: string) => within(container).getByRole<HTMLButtonElement>('button', { name })

  async function type(value: string) {
    await act(async () => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input(), value)
      input().dispatchEvent(new Event('input', { bubbles: true }))
    })
  }

  async function click(name: string) {
    await act(async () => { button(name).click(); await Promise.resolve() })
  }

  it('renders the heading, current and default location and an accessible status region', async () => {
    const service = fakeService()
    await render(service)

    expect(within(container).getByRole('heading', { name: 'Workspaceフォルダー' })).toBeTruthy()
    expect(container.querySelector('[data-workspace-path="current"]')!.textContent).toBe('C:\\workspace')
    expect(container.querySelector('[data-workspace-path="default"]')!.textContent).toBe('C:\\workspace')
    expect(container.querySelector('[data-workspace-path="saved"]')).toBeNull()
    expect(input().value).toBe('C:\\workspace')
    expect(status().getAttribute('role')).toBe('status')
    expect(status().getAttribute('aria-live')).toBe('polite')
    expect(service.read).toHaveBeenCalledTimes(1)
  })

  it('shows the saved next-start location only when a restart is required', async () => {
    await render(fakeService({ ...info, savedPath: 'D:\\data\\ws', restartRequired: true }))

    expect(container.querySelector('[data-workspace-path="saved"]')!.textContent).toBe('D:\\data\\ws')
    expect(within(container).getByText('保存済みで次回起動から使う場所')).toBeTruthy()
    expect(input().value).toBe('D:\\data\\ws')
  })

  it('saves the typed path and shows the restart note', async () => {
    const service = fakeService()
    vi.mocked(service.update).mockResolvedValueOnce({ ...info, savedPath: 'D:\\ws', restartRequired: true })
    await render(service)

    await type('  D:\\ws  ')
    await click('保存')

    expect(service.update).toHaveBeenCalledExactlyOnceWith('D:\\ws')
    expect(status().textContent).toBe('保存しました。アプリを再起動すると新しい場所を使います。今ある案件のファイルは自動では移動されません。')
    expect(container.querySelector('[data-workspace-path="saved"]')!.textContent).toBe('D:\\ws')
  })

  it('resets to the default by sending null', async () => {
    const service = fakeService({ ...info, currentPath: 'D:\\old', savedPath: 'D:\\old' })
    vi.mocked(service.update).mockResolvedValueOnce({ ...info, currentPath: 'D:\\old', savedPath: null, restartRequired: true })
    await render(service)

    await click('初期値に戻す')

    expect(service.update).toHaveBeenCalledExactlyOnceWith(null)
    expect(status().textContent).toBe(workspaceLocationMessages.saved)
    expect(input().value).toBe('C:\\workspace')
  })

  it('shows plain-language messages for invalid, forbidden and unavailable results', async () => {
    const service = fakeService()
    await render(service)
    await type('\\\\server\\share')

    vi.mocked(service.update).mockResolvedValueOnce(failure('invalid'))
    await click('保存')
    expect(status().textContent).toBe('この場所は使えません。ローカルのドライブ上の、空のフォルダー（または新しいフォルダー）を指定してください。')
    expect(input().value).toBe('\\\\server\\share')

    vi.mocked(service.update).mockResolvedValueOnce(failure('forbidden'))
    await click('保存')
    expect(status().textContent).toBe(workspaceLocationMessages.forbidden)

    vi.mocked(service.update).mockResolvedValueOnce({ status: 'timeout' })
    await click('保存')
    expect(status().textContent).toBe(workspaceLocationMessages.unavailable)
  })

  it('does not send an empty path', async () => {
    const service = fakeService()
    await render(service)
    await type('   ')
    await click('保存')

    expect(service.update).not.toHaveBeenCalled()
    expect(status().textContent).toBe(workspaceLocationMessages.empty)
  })

  it('disables editing and explains when the host bridge is absent', async () => {
    await render(null)

    expect(input().disabled).toBe(true)
    expect(button('保存').disabled).toBe(true)
    expect(button('初期値に戻す').disabled).toBe(true)
    expect(container.textContent).toContain(workspaceLocationMessages.unavailableEnvironment)
  })

  it('reports a failed initial read without enabling a stale view', async () => {
    await render(fakeService(failure('forbidden')))

    expect(status().textContent).toBe(workspaceLocationMessages.forbidden)
    expect(container.querySelector('[data-workspace-path]')).toBeNull()
  })
})
