// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { ProjectDraft, ProjectDraftInput } from '../../bridge'
import { QuickStartPanel, type QuickStartPanelProps } from './QuickStartPanel'

const draftId = '11112222-3333-4444-8555-666677778888'
const candidateId = '99990000-aaaa-4bbb-8ccc-ddddeeeeffff'
const reselectedCandidateId = '11110000-aaaa-4bbb-8ccc-ddddeeeeffff'
const profileId = '14b4f6b6-1ebc-55c3-9682-2c7e9ad226fa'
const profileDigest = 'a'.repeat(64)
const snapshotDigest = 'b'.repeat(64)
const sourceOnly = Object.freeze({
  profileId,
  profileVersion: '1.0.0',
  profileDigest,
  settingsSnapshotDigest: snapshotDigest,
  artifactSet: Object.freeze(['landscapeLongForm', 'verticalShort']),
})
const input: ProjectDraftInput = Object.freeze({
  sourceCandidateId: candidateId,
  contentName: 'Hubらじお',
  purpose: 'knowledge sharing',
  audience: 'developers',
  publicationScope: 'internalOnly',
  outputProfileIds: Object.freeze([profileId]),
  language: 'ja-JP',
  performerNames: Object.freeze(['person']),
  deadlineSelection: Object.freeze({ hasDeadline: false, value: null }),
  brandTemplateId: 'bf08c776-0f97-5373-a091-c70b60acd569',
})
const draft: ProjectDraft = Object.freeze({
  ...input,
  draftId,
  revision: 2,
  isFinalized: false,
  projectId: null,
  sourceStatus: 'available',
  missingFields: Object.freeze([]),
  updatedAt: '2026-09-07T00:00:00.0000000+00:00',
  sourceOnly,
})

const actEnvironment = globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
let previousActEnvironment: boolean | undefined
let container: HTMLDivElement
let otherControl: HTMLButtonElement
let root: Root

beforeEach(() => {
  previousActEnvironment = actEnvironment.IS_REACT_ACT_ENVIRONMENT
  actEnvironment.IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  otherControl = document.createElement('button')
  otherControl.textContent = '別の画面'
  document.body.append(otherControl, container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  otherControl.remove()
  vi.restoreAllMocks()
  if (previousActEnvironment === undefined) {
    delete actEnvironment.IS_REACT_ACT_ENVIRONMENT
  } else {
    actEnvironment.IS_REACT_ACT_ENVIRONMENT = previousActEnvironment
  }
})

describe('QuickStartPanel', () => {
  it('attests internal editing rights in the same single start confirmation, without another required form', () => {
    const props = panelProps()
    renderPanel(props)
    expect(within(container).getByText('開始すると、選択した元動画を社内編集に使用する権限があることを確認したものとして記録します。外部公開は許可しません。')).toBeTruthy()
    expect(container.querySelector('input[type="checkbox"], [role="dialog"], [role="alertdialog"]')).toBeNull()
    expect(container.querySelector('form')?.getAttribute('aria-describedby')).toBe('quick-start-rights')
    submitDirectly()
    expect(props.finalizeDraft).toHaveBeenCalledOnce()
  })

  it('shows the selected name read-only with an internal-only badge and compact shell', () => {
    renderPanel(panelProps())

    expect(container.querySelector('.quick-start-compact')).not.toBeNull()
    expect(within(container).getByRole('heading', { name: 'かんたん編集' })).toBeTruthy()
    expect(within(container).getByRole('heading', { name: '元動画' })).toBeTruthy()
    expect(container.querySelector('.quick-start-content-name')?.textContent).toBe(input.contentName)
    expect(within(container).getByText('社内限定').classList.contains('state-badge')).toBe(true)
    expect(container.querySelector('.source-status')?.textContent).toBe('選択済み')
    expect(container.querySelector('input, textarea, select, fieldset, .wizard-progress')).toBeNull()
    expect(container.querySelector('img, video, canvas')).toBeNull()
    expect([...container.querySelectorAll('button')].map(button => button.textContent)).toEqual([
      '編集を開始',
    ])
    expect(actionButton('編集を開始').disabled).toBe(false)
  })

  it('preserves and escapes the full name without exposing private paths or native IDs', () => {
    const contentName = `${'長いProject名・'.repeat(80)}<script>alert(1)</script><img src=x onerror=alert(1)>&tail`
    const sourcePath = 'C:\\Users\\private\\Videos\\source.mp4'
    const privateDraft = { ...draft, contentName, sourcePath }
    renderPanel(panelProps({ draft: privateDraft, input: { ...input, contentName } }))

    const name = container.querySelector('.quick-start-content-name')!
    expect(name.textContent).toBe(contentName)
    expect(name.getAttribute('title')).toBe(contentName)
    expect(container.querySelector('script, img, video, canvas')).toBeNull()
    expect(container.innerHTML).toContain('&lt;script&gt;')
    for (const privateValue of [sourcePath, draftId, candidateId, profileId, input.brandTemplateId!]) {
      expect(container.innerHTML).not.toContain(privateValue)
    }
  })

  it('uses only the supplied source-only summary for planned outputs', () => {
    renderPanel(panelProps({
      draft: {
        ...draft,
        sourceOnly: {
          ...sourceOnly,
          profileVersion: '0.1.0',
          artifactSet: ['verticalShortCandidates', 'thumbnailCandidates'],
        },
      },
    }))

    const preview = within(container).getByRole('region', { name: '設定による予定成果物' })
    expect([...preview.querySelectorAll('[data-result-id]')].map(row => row.getAttribute('data-result-id')))
      .toEqual(['result-videoShort', 'result-thumbnail'])
    expect(preview.textContent).toContain('短尺動画候補')
    expect(preview.textContent).toContain('サムネイル候補')
    expect(preview.textContent).toContain('保存済み設定')
    expect(preview.textContent).toContain('解像度・候補数・字幕設定の詳細は未取得')
    expect(preview.textContent).not.toMatch(/1920|1080|1280|720|\d+\s*[本枚]/)
    expect(preview.querySelector('[data-setting-key]')).toBeNull()
    expect(preview.querySelector('[data-preview-state]')?.getAttribute('data-preview-state')).toBe('planned')
    expect(preview.textContent).toContain('生成・保存状況：未確認')
    expect(preview.textContent).toContain('未評価（NOT EVALUATED）')
  })

  // FR-054 AC-029.4: the settings chosen for this video are listed with their reasons; none is shown without a record.
  it('lists the automatically chosen settings and their reasons only when the host recorded them', () => {
    renderPanel(panelProps({
      draft: {
        ...draft,
        autoSettings: [{
          key: 'editing.shortHookMaximumStart', label: 'つかみ（最初の見せ場）の開始', valueText: '2秒以内',
          reason: 'ショート動画は最初の1〜3秒で見続けるかが決まるため、見せ場を2秒以内に置くようにしました。',
        }],
      },
    }))
    const section = within(container).getByRole('region', { name: '動画に合わせて自動で設定した項目' })
    expect(section.textContent).toContain('つかみ（最初の見せ場）の開始: 2秒以内')
    expect(section.textContent).toContain('最初の1〜3秒')
    expect(section.querySelector('[data-setting-key="editing.shortHookMaximumStart"]')).not.toBeNull()

    renderPanel(panelProps({ draft: { ...draft, autoSettings: [] } }))
    expect(within(container).getByRole('region', { name: '動画に合わせて自動で設定した項目' }).textContent)
      .toContain('標準の設定のまま')

    renderPanel(panelProps({ draft: { ...draft, autoSettings: null } }))
    expect(within(container).queryByRole('region', { name: '動画に合わせて自動で設定した項目' })).toBeNull()
  })

  it('keeps provenance in one initially closed disclosure instead of an overview review-list', () => {
    renderPanel(panelProps())

    expect(container.querySelectorAll('details')).toHaveLength(1)
    const details = disclosure()
    expect(details.open).toBe(false)
    expect(details.querySelector('summary')?.textContent).toBe('適用する既定設定')
    expect(within(container).getByText(sourceOnly.profileVersion).closest('details')).toBe(details)
    // FR-052: digests are not shown to the user.
    expect(container.textContent).not.toContain(profileDigest)
    expect(container.textContent).not.toContain(snapshotDigest)
    expect(details.querySelector('.review-list')).toBeNull()
    expect([...details.querySelectorAll('dt')].map(term => term.textContent)).toEqual([
      '設定プロファイル版', '開始前の警告',
    ])
    for (const list of container.querySelectorAll('.review-list')) {
      expect(list.closest('.settings-outcome-preview')).not.toBeNull()
    }
    expect(container.querySelector('.quick-start-artifacts')).toBeNull()
    expect(container.textContent).not.toContain('landscapeLongForm')
  })

  it.each([
    { name: 'empty', artifactSet: [], label: '予定成果物は未取得です。', state: 'unavailable' },
    { name: 'unknown', artifactSet: ['<img src=x onerror=alert(1)>'], label: 'その他の予定成果物', state: 'planned' },
  ])('does not invent outputs or expose raw artifact names for an $name summary', ({ artifactSet, label, state }) => {
    renderPanel(panelProps({ draft: { ...draft, sourceOnly: { ...sourceOnly, artifactSet } } }))

    expect(container.textContent).toContain(label)
    expect(container.querySelector('[data-preview-state]')?.getAttribute('data-preview-state')).toBe(state)
    expect(container.textContent).not.toMatch(/長尺動画|短尺動画|生成済み|生成中|onerror/)
    expect(container.querySelector('img, video, canvas')).toBeNull()
  })

  it('rejects a legacy draft without a source-only summary', () => {
    expect(() => renderToStaticMarkup(
      <QuickStartPanel {...panelProps({ draft: { ...draft, sourceOnly: null } })} />,
    )).toThrow('Quick start requires a source-only project summary.')
  })

  it('focuses by default and invokes only the existing start callback', async () => {
    const props = panelProps()
    renderPanel(props)
    const user = userEvent.setup({ document })

    expect(document.activeElement).toBe(heading())
    expect(actionButton('編集を開始').type).toBe('submit')
    act(() => actionButton('編集を開始').focus())
    await act(async () => user.keyboard('[Enter]'))
    expect(props.finalizeDraft).toHaveBeenCalledExactlyOnceWith()
    expect(props.pickSource).not.toHaveBeenCalled()
    expect(status().textContent).toBe('この内容でProjectを作成できます。')
    expect(container.textContent).not.toContain('Projectを作成しました。')

    expect(props.finalizeDraft).toHaveBeenCalledOnce()
    expect(props.pickSource).not.toHaveBeenCalled()
  })

  it.each(['available', 'reselectionRequired'] as const)('blocks actions and guarded submits while pending with source %s', async sourceStatus => {
    const props = panelProps({
      draft: { ...draft, sourceStatus },
      pending: { requestId: 'pending-request', method: 'project.finalizeDraft' },
    })
    renderPanel(props)
    const user = userEvent.setup({ document })

    for (const button of container.querySelectorAll('button')) {
      expect(button.disabled).toBe(true)
      await act(async () => user.click(button))
    }
    submitDirectly()
    expect(props.finalizeDraft).not.toHaveBeenCalled()
    expect(props.pickSource).not.toHaveBeenCalled()
    expect(status().textContent).toBe('操作を処理しています。')
    expect(container.textContent).not.toMatch(/生成中|生成済み/)

    renderPanel({ ...props, pending: null })
    expect(actionButton('編集を開始').disabled).toBe(sourceStatus !== 'available')
  })

  // FR-039.1 FR-039.5 AC-012.4
  it('shows the activity notice only while a request is pending and keeps the status text', () => {
    renderPanel(panelProps({ pending: { requestId: 'r1', method: 'project.finalizeDraft' } }))
    expect(container.querySelector('.operation-activity h3')!.textContent).toBe('いま行っていること：編集の受付')
    expect(status().textContent).toBe('操作を処理しています。')

    renderPanel(panelProps({ pending: null }))
    expect(container.querySelector('.operation-activity')).toBeNull()
  })

  it.each(['notSelected', 'reselectionRequired'] as const)('requires native reselection before starting with source %s', async sourceStatus => {
    const sourceCandidateId = sourceStatus === 'notSelected' ? null : candidateId
    const props = panelProps({
      draft: { ...draft, sourceStatus, sourceCandidateId },
      input: { ...input, sourceCandidateId },
    })
    renderPanel(props)
    const user = userEvent.setup({ document })

    expect(container.querySelector('.source-status')?.textContent)
      .toBe(sourceStatus === 'notSelected' ? '未選択' : '再選択が必要')
    expect(actionButton('編集を開始').disabled).toBe(true)
    expect(status().textContent).toBe('元動画の再選択を待っています。')
    submitDirectly()
    await act(async () => user.click(actionButton('元動画を選び直す')))
    expect(props.pickSource).toHaveBeenCalledOnce()
    expect(props.finalizeDraft).not.toHaveBeenCalled()
    expect(actionButton('編集を開始').disabled).toBe(true)

    renderPanel({ ...props, input: { ...props.input, sourceCandidateId: reselectedCandidateId } })
    expect(container.querySelector('.source-status')?.textContent).toBe('選択済み')
    expect(within(container).queryByRole('button', { name: '元動画を選び直す' })).toBeNull()
    expect(actionButton('編集を開始').disabled).toBe(false)
    await act(async () => user.click(actionButton('編集を開始')))
    expect(props.finalizeDraft).toHaveBeenCalledExactlyOnceWith()
    expect(container.innerHTML).not.toContain(reselectedCandidateId)
  })

  it('retains completeness validation even when the source is available', () => {
    const props = panelProps({ input: { ...input, purpose: null } })
    renderPanel(props)

    expect(container.querySelector('.source-status')?.textContent).toBe('選択済み')
    expect(actionButton('編集を開始').disabled).toBe(true)
    expect(status().textContent).toBe('Projectの作成に必要な情報を確認してください。')
    submitDirectly()
    expect(props.finalizeDraft).not.toHaveBeenCalled()
    expect(container.querySelector('input, textarea, select')).toBeNull()
  })

  it.each(['available', 'reselectionRequired'] as const)('reports project creation, not automatic processing, after finalization with source %s', async sourceStatus => {
    const props = panelProps({
      draft: { ...draft, sourceStatus, isFinalized: true, projectId: 'abcdef01-2222-4333-8444-555566667777' },
    })
    renderPanel(props)

    expect(within(container).getByRole('heading', { name: 'Projectの内容' })).toBeTruthy()
    expect(container.querySelector('.state-badge.finalized')?.textContent).toBe('作成済み')
    expect(status().textContent).toBe('Projectを作成しました。「ジョブの状態の確認」画面で処理状況を確認できます。')
    expect(status().getAttribute('aria-live')).toBe('polite')
    expect([...container.querySelectorAll('button')].map(button => button.textContent)).toEqual([])
    expect(container.querySelector('form, progress, img, video, canvas')).toBeNull()
    expect(container.textContent).not.toMatch(/生成中|生成済み|処理を開始しました/)
    expect(container.textContent).toContain('生成・保存状況：未確認')
    expect(props.finalizeDraft).not.toHaveBeenCalled()
    expect(props.pickSource).not.toHaveBeenCalled()
  })

  it('does not steal focus while hidden and focuses on activation or an active draft change', () => {
    const props = panelProps({ active: false })
    act(() => otherControl.focus())
    const focus = vi.spyOn(HTMLElement.prototype, 'focus')
    renderPanel(props)
    const nextDraft = { ...draft, draftId: 'abcdef01-2222-4333-8444-555566667777' }
    renderPanel({ ...props, draft: nextDraft })
    expect(focus).not.toHaveBeenCalled()
    expect(document.activeElement).toBe(otherControl)

    renderPanel({ ...props, draft: nextDraft, active: true })
    expect(document.activeElement).toBe(heading())
    expect(focus).toHaveBeenCalledOnce()

    act(() => otherControl.focus())
    focus.mockClear()
    renderPanel({
      ...props,
      active: true,
      draft: { ...nextDraft, revision: 3 },
      pending: { requestId: 'pending-request', method: 'project.saveDraft' },
    })
    expect(document.activeElement).toBe(otherControl)
    expect(focus).not.toHaveBeenCalled()

    renderPanel({ ...props, active: true })
    expect(document.activeElement).toBe(heading())
    expect(focus).toHaveBeenCalledOnce()
  })

  it('preserves the source and open disclosure when becoming inactive without resetting or closing', async () => {
    const props = panelProps({ active: true })
    const before = JSON.stringify({ draft: props.draft, input: props.input })
    renderPanel(props)
    const details = disclosure()
    const user = userEvent.setup({ document })
    await act(async () => user.click(details.querySelector('summary')!))
    expect(details.open).toBe(true)
    act(() => otherControl.focus())
    const focus = vi.spyOn(HTMLElement.prototype, 'focus')

    renderPanel({ ...props, active: false })
    expect(document.activeElement).toBe(otherControl)
    expect(focus).not.toHaveBeenCalled()
    expect(disclosure()).toBe(details)
    expect(details.open).toBe(true)
    expect(container.querySelector('.quick-start-content-name')?.textContent).toBe(input.contentName)
    expect(container.querySelector('.source-status')?.textContent).toBe('選択済み')

    renderPanel(props)
    expect(document.activeElement).toBe(heading())
    expect(disclosure()).toBe(details)
    expect(details.open).toBe(true)
    expect(JSON.stringify({ draft: props.draft, input: props.input })).toBe(before)
    expect(props.finalizeDraft).not.toHaveBeenCalled()
    expect(props.pickSource).not.toHaveBeenCalled()
  })
})

function panelProps(overrides: Partial<QuickStartPanelProps> = {}): QuickStartPanelProps {
  return {
    draft,
    input,
    pending: null,
    finalizeDraft: vi.fn(),
    pickSource: vi.fn(),
    ...overrides,
  }
}

function renderPanel(props: QuickStartPanelProps) {
  act(() => {
    // The workspace owner hides the view without unmounting its draft.
    root.render(<div hidden={props.active === false}><QuickStartPanel {...props} /></div>)
  })
}

function heading() {
  return within(container).getByRole('heading', { name: 'かんたん編集' })
}

function actionButton(name: string): HTMLButtonElement {
  return within(container).getByRole('button', { name }) as HTMLButtonElement
}

function disclosure(): HTMLDetailsElement {
  return container.querySelector('details')!
}

function status() {
  return container.querySelector('.operation-status')!
}

function submitDirectly() {
  act(() => {
    container.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  })
}