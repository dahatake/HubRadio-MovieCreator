// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fixtureId } from '../workflow/pipelineTestFixture'
import type { PipelineRegenerationParams } from '../workflow/pipelineProtocol'
import { JobCreationAction } from './JobCreationAction'

const params: PipelineRegenerationParams = {
  projectId: fixtureId(1), expectedVersionId: fixtureId(5), expectedVersionDigest: 'a'.repeat(64),
  expectedSettingsRevision: 3, expectedSettingsSnapshotDigest: 'b'.repeat(64),
}

// FR-061 AC-036
describe('JobCreationAction', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })

  type Props = Parameters<typeof JobCreationAction>[0]
  const base = (): Props => ({
    nextJobNumber: 4, params, disabledReason: null, pending: false, active: true, onOpenSettings: vi.fn(), onConfirm: vi.fn(() => true),
  })
  const render = (props: Props) => act(() => root.render(<JobCreationAction {...props} />))
  const button = (name: string) => [...container.querySelectorAll('button')].find(b => b.textContent === name) as HTMLButtonElement
  const click = (name: string) => act(() => { button(name).click() })
  const dialog = () => container.querySelector('[role="alertdialog"]')

  it('renders the heading, explanation and opens settings', () => {
    const props = base()
    render(props)
    expect(container.querySelector('h2')?.textContent).toBe('新しいJobを作成')
    expect(container.textContent).toContain('Jobは1つずつ順番に実行します。')
    click('次に作成するJobの設定を開く')
    expect(props.onOpenSettings).toHaveBeenCalledOnce()
    render({ ...props, openSettingsDisabled: true })
    expect(button('次に作成するJobの設定を開く').disabled).toBe(true)
    render({ ...props, active: false })
    expect(button('次に作成するJobの設定を開く').disabled).toBe(true)
  })

  it('disables creation with a reason, while pending, inactive or without params', () => {
    render({ ...base(), disabledReason: '実行中です。' })
    expect(button('新しいJobを作成').disabled).toBe(true)
    expect(container.querySelector('[role="status"]')?.textContent).toBe('実行中です。')
    render({ ...base(), pending: true })
    expect(button('新しいJobを作成').disabled).toBe(true)
    expect(container.textContent).toContain('Jobの作成の受付を確認しています。')
    render({ ...base(), active: false })
    expect(button('新しいJobを作成').disabled).toBe(true)
    render({ ...base(), params: null })
    expect(button('新しいJobを作成').disabled).toBe(true)
  })

  it('confirms with the frozen params, focuses 取消, shows no identifiers and sends once', () => {
    const props = base()
    render(props)
    click('新しいJobを作成')
    expect(dialog()?.getAttribute('aria-label')).toBe('Jobの作成の確認')
    expect(dialog()?.textContent).toContain('Job 4を作成しますか')
    expect(document.activeElement).toBe(button('取消'))
    expect(container.textContent).not.toContain(params.expectedVersionId)
    expect(container.textContent).not.toContain(params.expectedVersionDigest)
    expect(container.textContent).not.toContain(params.expectedSettingsSnapshotDigest)
    const confirm = button('作成する')
    act(() => { confirm.click(); confirm.click() })
    expect(props.onConfirm).toHaveBeenCalledOnce()
    const sent = vi.mocked(props.onConfirm).mock.calls[0][0]
    expect(sent).toEqual(params)
    expect(Object.isFrozen(sent)).toBe(true)
    expect(dialog()).toBeNull()
  })

  it('uses generic wording when the number is unknown', () => {
    render({ ...base(), nextJobNumber: null })
    click('新しいJobを作成')
    expect(dialog()?.textContent).toContain('新しいJobを作成しますか')
  })

  it('cancels with the button and Escape, returning focus to the opener', () => {
    const props = base()
    render(props)
    click('新しいJobを作成')
    click('取消')
    expect(dialog()).toBeNull()
    expect(document.activeElement).toBe(button('新しいJobを作成'))
    click('新しいJobを作成')
    act(() => { dialog()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    expect(dialog()).toBeNull()
    expect(document.activeElement).toBe(button('新しいJobを作成'))
    expect(props.onConfirm).not.toHaveBeenCalled()
  })

  it('refuses to send when params changed or became unavailable while open', () => {
    const props = base()
    render(props)
    click('新しいJobを作成')
    render({ ...props, params: { ...params, expectedSettingsRevision: 4 } })
    expect(dialog()?.textContent).toContain('確認中に設定または状態が変わりました。取り消して状態を確認してください。')
    expect(button('作成する').disabled).toBe(true)
    render({ ...props, params: null })
    expect(button('作成する').disabled).toBe(true)
    click('作成する')
    expect(props.onConfirm).not.toHaveBeenCalled()
  })

  it('keeps the dialog with the changed-state message when sending is rejected', () => {
    const props = { ...base(), onConfirm: vi.fn(() => false) }
    render(props)
    click('新しいJobを作成')
    click('作成する')
    expect(props.onConfirm).toHaveBeenCalledOnce()
    expect(dialog()?.textContent).toContain('確認中に設定または状態が変わりました。')
    expect(button('作成する').disabled).toBe(true)
  })
})
