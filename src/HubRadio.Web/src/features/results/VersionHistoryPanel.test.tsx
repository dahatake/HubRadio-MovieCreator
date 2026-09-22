// @vitest-environment jsdom
import userEvent from '@testing-library/user-event'
import { within } from '@testing-library/dom'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fixtureId } from '../workflow/pipelineTestFixture'
import type { PipelineVersionEntry } from '../workflow/pipelineProtocol'
import { VersionHistoryPanel } from './VersionHistoryPanel'

const current: PipelineVersionEntry = Object.freeze({
  versionId: fixtureId(11), versionDigest: 'a'.repeat(64), parentVersionId: fixtureId(12),
  executionId: fixtureId(13), phase: 'Completed', hasResult: true, isCurrent: true,
})
const past: PipelineVersionEntry = Object.freeze({
  versionId: fixtureId(12), versionDigest: 'b'.repeat(64), parentVersionId: null,
  executionId: fixtureId(14), phase: 'Completed', hasResult: true, isCurrent: false,
})

describe('VersionHistoryPanel', () => {
  let container: HTMLElement
  let root: Root

  beforeEach(() => {
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
  })

  function render(props: Parameters<typeof VersionHistoryPanel>[0]): void {
    act(() => root.render(<VersionHistoryPanel {...props} />))
  }

  it('states that restoring adds a version instead of replacing anything', () => {
    render({ versions: [current, past] })
    expect(container.textContent).toContain('新しい版を作って成果物を作り直します')
    expect(container.textContent).toContain('今の版・過去の版・すでに保存した成果物は消えません')
    expect(container.textContent).not.toMatch(/削除|置き換え|上書き/)
  })

  it('separates a not-yet-read list, an empty read and an unavailable read', () => {
    render({ versions: null })
    expect(container.textContent).toContain('過去版はまだ取得していません')
    expect(container.querySelector('li')).toBeNull()

    render({ versions: [] })
    expect(container.textContent).toContain('過去版の記録はありません')
    expect(container.textContent).not.toContain('まだ取得していません')
    expect(container.querySelector('li')).toBeNull()

    render({ versions: null, unavailableReason: '過去版を取得できませんでした。' })
    expect(container.textContent).toContain('過去版を取得できませんでした。')
    expect(container.textContent).not.toContain('過去版の記録はありません')
    expect(container.textContent).not.toContain('まだ取得していません')
  })

  it('shows exactly the returned entries and marks the current one non-restorable', () => {
    render({ versions: [current, past] })
    const items = container.querySelectorAll('li')
    expect(items).toHaveLength(2)
    expect(items[0]!.getAttribute('data-version-id')).toBe(current.versionId)
    expect(items[0]!.getAttribute('data-version-current')).toBe('true')
    expect(within(items[0]! as HTMLElement).getByRole('button', { name: 'この版から作り直す' })
      .hasAttribute('disabled')).toBe(true)
    expect(within(items[1]! as HTMLElement).getByRole('button', { name: 'この版から作り直す' })
      .hasAttribute('disabled')).toBe(false)
    expect(container.textContent).toContain(past.versionDigest)
  })

  it('completes selection and restore with the keyboard and confirms before acting', async () => {
    const onRestore = vi.fn()
    render({ versions: [current, past], onRestore })
    const user = userEvent.setup({ document })

    const start = within(container.querySelectorAll('li')[1]! as HTMLElement)
      .getByRole('button', { name: 'この版から作り直す' })
    start.focus()
    expect(document.activeElement).toBe(start)
    await act(async () => { await user.keyboard('{Enter}') })
    expect(onRestore).not.toHaveBeenCalled()
    expect(container.textContent).toContain('この版から新しい版を作って作り直しますか')

    // Cancelling changes nothing.
    const cancel = within(container).getByRole('button', { name: '取り消す' })
    cancel.focus()
    await act(async () => { await user.keyboard('{Enter}') })
    expect(onRestore).not.toHaveBeenCalled()
    expect(container.textContent).not.toContain('この版から新しい版を作って作り直しますか')

    const reopen = within(container.querySelectorAll('li')[1]! as HTMLElement)
      .getByRole('button', { name: 'この版から作り直す' })
    reopen.focus()
    await act(async () => { await user.keyboard(' ') })
    const confirm = within(container).getByRole('button', { name: '作り直す' })
    confirm.focus()
    await act(async () => { await user.keyboard('{Enter}') })
    expect(onRestore).toHaveBeenCalledExactlyOnceWith(past.versionId, past.versionDigest)
  })

  it('does not start a restore while a native operation is pending', () => {
    const onRestore = vi.fn()
    render({ versions: [current, past], pending: true, onRestore })
    for (const button of container.querySelectorAll('button')) {
      expect(button.hasAttribute('disabled')).toBe(true)
    }
    expect(onRestore).not.toHaveBeenCalled()
  })

  it('shows why restoring is blocked instead of pretending an operation is running', () => {
    const onRestore = vi.fn()
    render({ versions: [current, past], disabledReason: '未保存の編集があります。', onRestore })
    expect(container.textContent).toContain('未保存の編集があります。')
    for (const button of container.querySelectorAll('button')) {
      expect(button.hasAttribute('disabled')).toBe(true)
    }
    expect(onRestore).not.toHaveBeenCalled()
  })

  it('drops an open confirmation when the selected version leaves the list', async () => {
    const onRestore = vi.fn()
    render({ versions: [current, past], onRestore })
    const user = userEvent.setup({ document })
    const start = within(container.querySelectorAll('li')[1]! as HTMLElement)
      .getByRole('button', { name: 'この版から作り直す' })
    start.focus()
    await act(async () => { await user.keyboard('{Enter}') })
    expect(container.textContent).toContain('この版から新しい版を作って作り直しますか')

    // A refreshed list that no longer contains the selection must not keep the
    // confirmation open against a version the caller can no longer restore.
    render({ versions: [current], onRestore })
    expect(container.textContent).not.toContain('この版から新しい版を作って作り直しますか')
    expect(within(container).queryByRole('button', { name: '作り直す' })).toBeNull()
    expect(onRestore).not.toHaveBeenCalled()
  })
})
