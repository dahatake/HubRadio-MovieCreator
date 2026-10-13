// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { WorkspaceBrowseOutcome } from '../settings/workspaceLocationProtocol'
import type { JobExportOutcome, JobExportParams, JobExportResult, JobExportService } from './jobExportProtocol'
import { describeJobExport, JobExportPanel, jobExportMessages } from './JobExportPanel'

const project = '93000000-0000-4000-8000-000000000001'
const execution = '93000000-0000-4000-8000-000000000002'
const completed = (over: Partial<JobExportResult> = {}): JobExportOutcome => ({
  status: 'completed', destinationReason: null, savedCount: 0, files: [], failures: [], ...over,
})

function fakeExport(outcome: JobExportOutcome) {
  const service: JobExportService = {
    export: vi.fn((_params: JobExportParams) => Promise.resolve(outcome)),
    dispose: vi.fn(),
  }
  return service
}

function fakeBrowse(outcome: WorkspaceBrowseOutcome) {
  return { browse: vi.fn(() => Promise.resolve(outcome)) }
}

describe('FR-132 AC-138 AC-140 JobExportPanel', () => {
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

  async function render(
    service: JobExportService | null,
    browse: ReturnType<typeof fakeBrowse> | null = fakeBrowse({ status: 'cancelled', path: null }),
    extra: { disabledReason?: string | null } = {},
  ) {
    await act(async () => {
      root.render(<JobExportPanel projectId={project} executionId={execution} jobName="Job 2" service={service} browseService={browse} {...extra} />)
    })
  }

  const input = () => within(container).getByLabelText<HTMLInputElement>('書き出し先のフォルダー')
  const status = () => container.querySelector('[data-job-export-message]')!
  const button = (name: string) => within(container).getByRole<HTMLButtonElement>('button', { name })

  async function type(value: string) {
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input(), value)
      input().dispatchEvent(new Event('input', { bubbles: true }))
    })
  }

  async function click(name: string) {
    await act(async () => { button(name).click(); await Promise.resolve() })
  }

  // FR-132 AC-136 AC-140: typed folder, saved with the Job's ids and name
  it('sends the typed folder with the Job and shows the saved count and files', async () => {
    const service = fakeExport(completed({
      savedCount: 2,
      files: [{ kind: 'longClean', fileName: 'p_Job 2_長尺クリーン.mp4' }, { kind: 'thumbnailLandscape', fileName: 'p_Job 2_サムネイル.png' }],
    }))
    await render(service)

    await type('  D:\\out  ')
    await click('書き出し')

    expect(service.export).toHaveBeenCalledExactlyOnceWith({ projectId: project, executionId: execution, destination: 'D:\\out', jobName: 'Job 2' })
    expect(status().textContent).toContain('2件のファイルを保存しました。')
    expect(status().textContent).toContain('保存: 長尺（クリーン版）: p_Job 2_長尺クリーン.mp4')
    expect(status().getAttribute('role')).toBe('status')
    expect(status().getAttribute('aria-live')).toBe('polite')
  })

  // FR-132 AC-138: partial failure — the count is the saved files, each failure has its reason in words
  it('lists each failed file with its reason in Japanese, not by colour alone', async () => {
    const service = fakeExport(completed({
      savedCount: 1,
      files: [{ kind: 'longClean', fileName: 'a.mp4' }],
      failures: [{ kind: 'shortBurnIn', reason: 'diskFull' }, { kind: 'thumbnailLandscape', reason: 'missing' }, { kind: 'longBurnIn', reason: 'notWritable' }],
    }))
    await render(service)
    await type('D:\\out')
    await click('書き出し')

    const text = status().textContent ?? ''
    expect(text).toContain('1件のファイルを保存しました。保存できなかったファイルが3件あります。')
    expect(text).toContain('失敗: 短尺（字幕焼き込み版）: 空き容量がありません')
    expect(text).toContain('失敗: サムネイル（横長）: 成果物がありません')
    expect(text).toContain('失敗: 長尺（字幕焼き込み版）: 書き込めません')
    expect(text).not.toMatch(/[A-Z]:\\|Exception|S-1-5/)
  })

  // FR-132 AC-138: no deliverable → 0 saved, not an error
  it('says nothing was saved when the Job has no deliverables', () => {
    expect(describeJobExport(completed())).toBe(jobExportMessages.nothingToSave)
  })

  // FR-132 AC-140: a missing / unwritable folder is reported and nothing is claimed saved
  it('shows the reason for an unusable folder and keeps the input', async () => {
    const service = fakeExport({ status: 'destinationInvalid', destinationReason: 'notFound', savedCount: 0, files: [], failures: [] })
    await render(service)
    await type('D:\\missing')
    await click('書き出し')

    expect(status().textContent).toContain('書き出し先のフォルダーが見つかりません')
    expect(status().textContent).toContain('ファイルは保存していません')
    expect(input().value).toBe('D:\\missing')
  })

  // FR-132 AC-139: forbidden
  it('shows the forbidden message', async () => {
    await render(fakeExport({ status: 'forbidden', destinationReason: null, savedCount: 0, files: [], failures: [] }))
    await type('D:\\out')
    await click('書き出し')
    expect(status().textContent).toBe(jobExportMessages.forbidden)
  })

  // FR-132 AC-140: 参照… fills the input; cancel keeps it; failure shows the FR-064 message
  it('fills the input from the folder dialog, keeps it on cancel and shows the FR-064 message when it cannot open', async () => {
    const picked = fakeBrowse({ status: 'selected', path: 'E:\\picked' })
    await render(fakeExport(completed()), picked)
    await click('参照…')
    expect(input().value).toBe('E:\\picked')

    act(() => root.unmount())
    root = createRoot(container)
    const cancelled = fakeBrowse({ status: 'cancelled', path: null })
    await render(fakeExport(completed()), cancelled)
    await type('F:\\typed')
    await click('参照…')
    expect(input().value).toBe('F:\\typed')
    expect(status().textContent).toBe('')

    act(() => root.unmount())
    root = createRoot(container)
    await render(fakeExport(completed()), fakeBrowse({ status: 'failed', path: null }))
    await click('参照…')
    expect(status().textContent).toBe('フォルダーを選べませんでした。場所を直接入力してください。')
  })

  // FR-132 AC-140: keyboard + assistive technology
  it('exposes the input and both buttons by name and saves with Enter', async () => {
    const service = fakeExport(completed())
    await render(service)
    expect(button('参照…')).toBeTruthy()
    expect(button('書き出し')).toBeTruthy()
    await type('D:\\out')
    await act(async () => {
      input().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
      await Promise.resolve()
    })
    expect(service.export).toHaveBeenCalledTimes(1)
    expect(container.querySelector('section')!.getAttribute('aria-labelledby')).toBeTruthy()
  })

  it('does not send an empty folder and is disabled when the Job is not completed or the host is absent', async () => {
    const service = fakeExport(completed())
    await render(service)
    await click('書き出し')
    expect(service.export).not.toHaveBeenCalled()
    expect(status().textContent).toBe(jobExportMessages.empty)

    act(() => root.unmount())
    root = createRoot(container)
    await render(service, null, { disabledReason: jobExportMessages.notCompleted })
    expect(button('書き出し').disabled).toBe(true)
    expect(container.textContent).toContain(jobExportMessages.notCompleted)

    act(() => root.unmount())
    root = createRoot(container)
    await render(null)
    expect(button('書き出し').disabled).toBe(true)
    expect(container.textContent).toContain(jobExportMessages.unavailableEnvironment)
  })
})
