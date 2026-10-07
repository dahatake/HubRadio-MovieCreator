// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fixtureId } from '../workflow/pipelineTestFixture'
import type { PipelineVersionEntry } from '../workflow/pipelineProtocol'
import { pipelinePhaseLabels } from '../workflow/pipelinePresentation'
import { ProjectJobList } from './ProjectJobList'

const entry = (n: number, phase: PipelineVersionEntry['phase'], isCurrent: boolean): PipelineVersionEntry => ({
  versionId: fixtureId(n), versionDigest: 'c'.repeat(64), parentVersionId: null, executionId: fixtureId(100 + n),
  phase, hasResult: true, isCurrent, createdAt: '2026-02-01T00:00:00Z', settings: [], outputs: [],
})

// FR-061 AC-036
describe('ProjectJobList', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })
  const render = (props: Parameters<typeof ProjectJobList>[0]) => act(() => root.render(<ProjectJobList {...props} />))

  it.each([[null, 'idle'], [null, 'loading'], [[], 'loading']] as const)('shows loading for %j / %s', (versions, status) => {
    render({ versions, status })
    expect(container.textContent).toContain('ジョブの一覧を取得しています。')
    expect(container.querySelector('ol')).toBeNull()
  })

  it('shows an empty message for notFound or an empty list', () => {
    render({ versions: [], status: 'notFound' })
    expect(container.textContent).toContain('このProjectのジョブはまだありません。')
    render({ versions: [], status: 'available' })
    expect(container.textContent).toContain('このProjectのジョブはまだありません。')
  })

  it.each(['conflict', 'forbidden', 'unavailable', 'invalid'])('shows a failure for %s', status => {
    render({ versions: null, status })
    expect(container.textContent).toContain('ジョブの一覧を取得できませんでした。')
  })

  it('lists jobs newest first with numbers, labels and no identifiers', () => {
    const versions = [entry(3, 'Completed', true), entry(2, 'Failed', false), entry(1, 'Completed', false)]
    render({ versions, status: 'available' })
    expect(container.querySelector('h2')?.textContent).toBe('このProjectのジョブ（3件）')
    const items = [...container.querySelectorAll('ol[aria-label="ジョブの一覧"] > li')]
    expect(items.map(li => li.querySelector('h3')?.textContent)).toEqual(['Job 3（いまの版）', 'Job 2', 'Job 1'])
    expect(items.map(li => li.getAttribute('data-job-phase'))).toEqual(['Completed', 'Failed', 'Completed'])
    expect(items[1].textContent).toContain(`状態: ${pipelinePhaseLabels.Failed}`)
    expect(items[1].textContent).toContain(`作成日時: ${new Date('2026-02-01T00:00:00Z').toLocaleString('ja-JP')}`)
    for (const v of versions) {
      expect(container.textContent).not.toContain(v.versionId)
      expect(container.textContent).not.toContain(v.executionId)
    }
    expect(container.textContent).not.toContain('c'.repeat(64))
  })

  it('accepts a custom heading', () => {
    render({ versions: [entry(1, 'Completed', true)], status: 'available', heading: '見出し' })
    expect(container.querySelector('h2')?.textContent).toBe('見出し（1件）')
  })
})

// FR-061 AC-036.9
describe('ProjectJobList copying a Job’s settings (FR-061.13)', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })
  const count = (value: string) => ({ key: 'project.verticalShortCandidateCount', value: { kind: 'integer' as const, integerValue: value } })
  const versions = [
    { ...entry(2, 'Completed', true), settings: [count('3')] },
    { ...entry(1, 'Completed', false), settings: [count('1')] },
  ]
  const button = (name: string) => [...container.querySelectorAll('button')].find(item => (item.getAttribute('aria-label') ?? item.textContent) === name) as HTMLButtonElement

  it('confirms the varying items and calls onCopy once with the Job and its number', () => {
    const onCopy = vi.fn()
    act(() => root.render(<ProjectJobList versions={versions} status="available" copy={{ disabledReason: null, busy: false, status: null, onCopy }} />))
    act(() => button('Job 1の設定を次のJobに使う').click())
    const dialog = container.querySelector('[role="alertdialog"]')!
    expect(dialog.textContent).toContain('Job 1の設定のうち、Jobごとに違う次の1項目を「次に作成するJob」に保存します。')
    expect(dialog.textContent).toContain(': 1')
    expect(document.activeElement).toBe(button('やめる'))
    act(() => button('保存する').click())
    expect(onCopy).toHaveBeenCalledExactlyOnceWith(versions[1], 1)
    expect(container.querySelector('[role="alertdialog"]')).toBeNull()
  })

  it('cancels with やめる or Escape without copying, and shows the reason and status', () => {
    const onCopy = vi.fn()
    act(() => root.render(<ProjectJobList versions={versions} status="available" copy={{ disabledReason: null, busy: false, status: null, onCopy }} />))
    act(() => button('Job 2の設定を次のJobに使う').click())
    act(() => button('やめる').click())
    expect(document.activeElement).toBe(button('Job 2の設定を次のJobに使う'))
    act(() => button('Job 2の設定を次のJobに使う').click())
    act(() => { container.querySelector('[role="alertdialog"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    expect(container.querySelector('[role="alertdialog"]')).toBeNull()
    expect(onCopy).not.toHaveBeenCalled()
    act(() => root.render(<ProjectJobList versions={versions} status="available" copy={{ disabledReason: '設定の確認を完了してください。', busy: false, status: '保存しました。', onCopy }} />))
    expect(button('Job 2の設定を次のJobに使う').disabled).toBe(true)
    expect(container.textContent).toContain('設定の確認を完了してください。')
    expect(container.querySelector('[role="status"]')?.textContent).toBe('保存しました。')
  })

  it('offers no copy for Jobs without varying settings or without the copy prop', () => {
    act(() => root.render(<ProjectJobList versions={[entry(1, 'Completed', true)]} status="available" copy={{ disabledReason: null, busy: false, status: null, onCopy: vi.fn() }} />))
    expect(container.textContent).not.toContain('このJobの設定を次のJobに使う')
    act(() => root.render(<ProjectJobList versions={versions} status="available" />))
    expect(container.textContent).not.toContain('このJobの設定を次のJobに使う')
  })
})
