// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { WorkspaceLocationSection, workspaceLocationMessages } from './WorkspaceLocationSection'
import type { WorkspaceBrowseOutcome, WorkspaceLocationOutcome, WorkspaceLocationService } from './workspaceLocationProtocol'

const info = { status: 'available', currentPath: 'C:\\workspace', savedPath: null, defaultPath: 'C:\\workspace', restartRequired: false } as const
const failure = (status: 'invalid' | 'forbidden' | 'unavailable') =>
  ({ status, currentPath: null, savedPath: null, defaultPath: null, restartRequired: false }) as const

function fakeService(read: WorkspaceLocationOutcome = info) {
  const service: WorkspaceLocationService = {
    read: vi.fn(() => Promise.resolve(read)),
    update: vi.fn((_path: string | null) => Promise.resolve<WorkspaceLocationOutcome>(info)),
    browse: vi.fn(() => Promise.resolve<WorkspaceBrowseOutcome>({ status: 'cancelled', path: null })),
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

  // FR-064 AC-039 AC-042: 参照… fills the input only; it never calls update (save).
  it('FR-064 AC-039 AC-042 fills the input from the folder dialog without saving', async () => {
    const service = fakeService()
    vi.mocked(service.browse).mockResolvedValueOnce({ status: 'selected', path: 'D:\\picked' })
    await render(service)

    await click('参照…')

    expect(service.browse).toHaveBeenCalledTimes(1)
    expect(service.update).not.toHaveBeenCalled()
    expect(input().value).toBe('D:\\picked')
    await click('保存')
    expect(service.update).toHaveBeenCalledExactlyOnceWith('D:\\picked')
  })

  // FR-064 AC-040: cancel keeps the input; failure shows the message and keeps the input.
  it('FR-064 AC-040 keeps the input on cancel and on failure and shows the message', async () => {
    const service = fakeService()
    await render(service)
    await type('E:\\typed')

    await click('参照…')
    expect(input().value).toBe('E:\\typed')
    expect(status().textContent).toBe('')

    vi.mocked(service.browse).mockResolvedValueOnce({ status: 'failed', path: null })
    await click('参照…')
    expect(input().value).toBe('E:\\typed')
    expect(status().textContent).toBe('フォルダーを選べませんでした。場所を直接入力してください。')

    vi.mocked(service.browse).mockResolvedValueOnce({ status: 'timeout' })
    await click('参照…')
    expect(status().textContent).toBe(workspaceLocationMessages.browseFailed)
    expect(service.update).not.toHaveBeenCalled()
  })

  // FR-064 AC-040: no double press while the dialog is open.
  it('FR-064 AC-040 disables 参照… while the dialog is open', async () => {
    const service = fakeService()
    let finish: (value: WorkspaceBrowseOutcome) => void = () => undefined
    vi.mocked(service.browse).mockReturnValueOnce(new Promise<WorkspaceBrowseOutcome>(resolve => { finish = resolve }))
    await render(service)

    await click('参照…')
    expect(button('参照…').disabled).toBe(true)
    await act(async () => { button('参照…').click() })
    expect(service.browse).toHaveBeenCalledTimes(1)

    await act(async () => { finish({ status: 'cancelled', path: null }) })
    expect(button('参照…').disabled).toBe(false)
  })

  // FR-064 AC-041: forbidden from the host is reported and leaves the input unchanged.
  it('FR-064 AC-041 reports forbidden without changing the input', async () => {
    const service = fakeService()
    vi.mocked(service.browse).mockResolvedValueOnce({ status: 'forbidden', path: null })
    await render(service)

    await click('参照…')

    expect(status().textContent).toBe(workspaceLocationMessages.forbidden)
    expect(input().value).toBe('C:\\workspace')
  })

  // FR-064 AC-042: keyboard and assistive technology.
  it('FR-064 AC-042 exposes a named native button before the save button and a named input', async () => {
    await render(fakeService())

    const browse = button('参照…')
    expect(browse.tagName).toBe('BUTTON')
    expect(browse.type).toBe('button')
    expect(input().getAttribute('aria-label')).toBe('Workspaceフォルダーの場所')
    expect(input().compareDocumentPosition(browse) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(browse.compareDocumentPosition(button('保存')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })
})
