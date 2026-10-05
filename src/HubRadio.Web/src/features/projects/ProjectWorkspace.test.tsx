// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import userEvent from '@testing-library/user-event'
import { act, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  createBridgeClient,
  type BridgeMessageEvent,
  type BridgeTransport,
  type ProjectBridgeResponse,
  type ProjectDraft,
  type ProjectDraftInput,
  type ProjectListItem,
} from '../../bridge'
import {
  getWizardStepAfterKey,
  ProjectWorkspace,
  ProjectWorkspaceView,
  type ProjectWorkspaceProps,
  type ProjectWorkspaceViewProps,
} from './ProjectWorkspace'
import {
  ProjectStateProvider,
  useProjectState,
  type ProjectStateContextValue,
} from './ProjectStateProvider'
import { initialProjectState, type ProjectState } from './projectState'

const draftId = '11112222-3333-4444-8555-666677778888'
const finalizedProjectId = 'abcdef01-2222-4333-8444-555566667777'
const candidateId = '99990000-aaaa-4bbb-8ccc-ddddeeeeffff'
const profileId = '14b4f6b6-1ebc-55c3-9682-2c7e9ad226fa'
const templateId = 'bf08c776-0f97-5373-a091-c70b60acd569'
const timestamp = '2026-09-01T12:00:00.0000000+00:00'
const profileDigest = 'a'.repeat(64)
const snapshotDigest = 'b'.repeat(64)
const adversarialText =
  '<script>globalThis.compromised=true</script><img src=x onerror=alert(1)>&tail'

describe('ProjectWorkspace', () => {
  it('supports a bounded keyboard flow across every wizard step', () => {
    expect(getWizardStepAfterKey(0, 'ArrowRight')).toBe(1)
    expect(getWizardStepAfterKey(1, 'ArrowDown')).toBe(2)
    expect(getWizardStepAfterKey(2, 'End')).toBe(4)
    expect(getWizardStepAfterKey(4, 'ArrowRight')).toBe(0)
    expect(getWizardStepAfterKey(0, 'ArrowLeft')).toBe(4)
    expect(getWizardStepAfterKey(4, 'ArrowUp')).toBe(3)
    expect(getWizardStepAfterKey(3, 'Home')).toBe(0)
    expect(getWizardStepAfterKey(0, 'Tab')).toBeNull()
    expect(getWizardStepAfterKey(-1, 'ArrowRight')).toBeNull()
    expect(getWizardStepAfterKey(5, 'ArrowLeft')).toBeNull()

    const input = completeInput()
    const markup = renderState({
      ...initialProjectState,
      canCreate: true,
      catalog: {
        outputProfiles: [{ id: profileId, name: 'long-form-1280x720' }],
        brandTemplates: [{ id: templateId, name: 'development-neutral-long-form' }],
      },
      draft: projectDraft(input),
      input,
    })
    expect(markup).toContain('<form')
    expect(markup).toMatch(/<button[^>]*type="submit"[^>]*>下書きを保存<\/button>/)
    expect(markup).toMatch(/<button[^>]*type="button"[^>]*>案件を作成<\/button>/)
  })

  it('executes wizard navigation and finalization with keyboard input', async () => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
    const container = document.createElement('div')
    document.body.append(container)
    const root = createRoot(container)
    const finalizeDraft = vi.fn()
    const input = completeInput()
    const state: ProjectState = {
      ...initialProjectState,
      canCreate: true,
      catalog: {
        outputProfiles: [{ id: profileId, name: 'long-form-1280x720' }],
        brandTemplates: [{ id: templateId, name: 'development-neutral-long-form' }],
      },
      draft: projectDraft(input),
      input,
    }

    try {
      act(() => {
        root.render(
          <ProjectWorkspaceView
            {...contextFor(state)}
            finalizeDraft={finalizeDraft}
          />,
        )
      })
      const user = userEvent.setup({ document })
      const steps = [...container.querySelectorAll<HTMLButtonElement>(
        '.wizard-progress button',
      )]
      expect(steps).toHaveLength(5)

      act(() => steps[0]!.focus())
      await act(async () => user.keyboard('[End]'))
      expect(document.activeElement).toBe(steps[4])
      expect(steps[4]!.getAttribute('aria-current')).toBe('step')

      await act(async () => user.keyboard('[Home]'))
      expect(document.activeElement).toBe(steps[0])
      expect(steps[0]!.getAttribute('aria-current')).toBe('step')

      await act(async () => user.keyboard('[End]'))
      const finish = [...container.querySelectorAll<HTMLButtonElement>('button')]
        .find((button) => button.textContent?.trim() === '案件を作成')
      if (finish === undefined) {
        throw new Error('Expected the project finalization button.')
      }
      act(() => finish.focus())
      await act(async () => user.keyboard('[Enter]'))
      expect(finalizeDraft).toHaveBeenCalledOnce()
    } finally {
      act(() => root.unmount())
      container.remove()
      delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
        .IS_REACT_ACT_ENVIRONMENT
    }
  })

  it('renders the five supplied card details and escapes names without empty legacy columns', () => {
    const markup = renderState({
      ...initialProjectState,
      canCreate: true,
      projects: [projectListItem(adversarialText)],
    })
    const container = document.createElement('div')
    container.innerHTML = markup

    // Name, persisted state, source status, update time, and the reopen action.
    expect(container.querySelector('.project-card-name')?.textContent).toBe(adversarialText)
    expect(container.querySelector('.state-badge')?.textContent).toBe('下書き')
    expect(container.querySelector('.source-status')?.textContent).toBe('選択済み')
    expect(container.querySelector('time')?.getAttribute('datetime')).toBe(timestamp)
    expect(container.querySelector('time')?.textContent).toBe('2026/09/01 12:00 UTC')
    expect(container.querySelector('.project-card-open')?.getAttribute('aria-label'))
      .toBe(`${adversarialText}を開く`)
    expect(container.querySelectorAll('.project-card')).toHaveLength(1)
    expect([...container.querySelectorAll('.project-card-metadata dt')]
      .map((term) => term.textContent)).toEqual(['元動画', '更新日時'])
    expect(container.querySelector('table, .table-scroll')).toBeNull()
    for (const emptyColumn of [
      'エピソード番号',
      '担当者',
      '公開区分',
      '編集状態',
      'レビュー状態',
      '出力形式',
      '未設定',
    ]) {
      expect(markup).not.toContain(emptyColumn)
    }
    expect(markup).toContain('&lt;script&gt;')
    expect(markup).toContain('&lt;img')
    expect(markup).toContain('&amp;tail')
    expect(markup).not.toContain('<script')
    expect(markup).not.toContain('<img')
  })

  it('renders all ten required inputs and all six publication scopes', () => {
    const input = completeInput()
    const markup = renderState({
      ...initialProjectState,
      canCreate: true,
      catalog: {
        outputProfiles: [{ id: profileId, name: 'long-form-1280x720' }],
        brandTemplates: [{ id: templateId, name: 'development-neutral-long-form' }],
      },
      draft: projectDraft(input),
      input,
    })

    for (const label of [
      '元動画ファイル',
      'コンテンツ名',
      '動画の目的',
      '想定視聴者',
      '公開範囲',
      '希望する出力形式',
      '使用言語',
      '出演者名',
      '必要な公開期限',
      'ブランドテンプレート',
    ]) {
      expect(markup).toContain(label)
    }
    for (const scope of [
      '社内限定',
      '特定グループ限定',
      '外部公開候補',
      '外部公開承認済み',
      '公開不可',
      '判定待ち',
    ]) {
      expect(markup).toContain(scope)
    }
    expect(markup).toContain('必須項目がすべて入力されています。')
    expect(markup).toContain('案件を作成')
    expect(markup).not.toContain(candidateId)
    expect(markup).not.toContain(draftId)
  })

  it('disables creation without native authorization', () => {
    const markup = renderState({
      ...initialProjectState,
      catalog: {
        outputProfiles: [{ id: profileId, name: 'long-form-1280x720' }],
        brandTemplates: [{ id: templateId, name: 'development-neutral-long-form' }],
      },
    })

    expect(markup).toContain('案件を作成する権限がありません')
    expect(markup).toMatch(
      /<button[^>]*disabled=""[^>]*>新しい案件を作成<\/button>/,
    )
  })

  it('offers explicit OCC recovery without exposing private native details', () => {
    const input = completeInput()
    const markup = renderState({
      ...initialProjectState,
      draft: projectDraft(input),
      input,
      error: {
        code: 'project.revision_conflict',
        message: '下書きが別の操作で更新されました。再読み込みしてください。',
      },
    })

    expect(markup).toContain('role="alert"')
    expect(markup).toContain('最新版を再読み込み')
    expect(markup).not.toContain('C:\\private')
    expect(markup).not.toContain('S-1-5-')
  })

  it('blocks finalization until a persisted unavailable source is reselected', () => {
    const input = completeInput()
    const markup = renderState({
      ...initialProjectState,
      canCreate: true,
      catalog: {
        outputProfiles: [{ id: profileId, name: 'long-form-1280x720' }],
        brandTemplates: [{ id: templateId, name: 'development-neutral-long-form' }],
      },
      draft: {
        ...projectDraft(input),
        sourceStatus: 'reselectionRequired',
      },
      input,
    })

    expect(markup).toContain('再選択が必要')
    expect(markup).toContain('案件作成まで残り 1 項目')
    expect(markup).toMatch(
      /<button[^>]*class="primary-button"[^>]*disabled=""[^>]*>案件を作成<\/button>/,
    )
    expect(markup).not.toContain('必須項目がすべて入力されています。')
  })

  it('renders private-safe source-only provenance and locks its fixed defaults', () => {
    const input = completeInput()
    const markup = renderState({
      ...initialProjectState,
      canCreate: true,
      catalog: {
        outputProfiles: [{ id: profileId, name: 'long-form-1280x720' }],
        brandTemplates: [{ id: templateId, name: 'development-neutral-long-form' }],
      },
      draft: sourceOnlyDraft(input),
      input,
    })
    const container = document.createElement('div')
    container.innerHTML = markup
    const provenance = container.querySelector<HTMLDetailsElement>('details.completion-summary')

    expect(markup).toContain('かんたん編集')
    expect(markup).toContain('一回の確認')
    expect(markup).toContain('社内限定')
    expect(container.querySelector('.quick-start-content-name')?.textContent).toBe(input.contentName)
    expect(markup).toContain('data-preview-state="planned"')
    expect([...container.querySelectorAll('.settings-outcome-preview .review-list dt')]
      .map((term) => term.textContent)).toEqual(['長尺動画', '短尺動画'])
    expect([...container.querySelectorAll('.settings-outcome-preview .review-list dd')]
      .map((description) => description.textContent)).toEqual(['長尺動画 予定', '短尺動画 予定'])
    expect(markup).toContain('この動画に固定された設定の要約です。現在の全体設定では補完しません。')
    expect(markup).toContain('解像度・候補数・字幕設定の詳細は未取得です。')
    expect(markup).toContain('生成・保存状況：未確認。品質評価：未評価（NOT EVALUATED）。')
    expect(markup).not.toContain('landscapeLongForm')
    expect(markup).not.toContain('verticalShort')
    expect(provenance?.open).toBe(false)
    expect(provenance?.querySelector('summary')?.textContent).toBe('適用する既定設定')
    expect(provenance?.textContent).toContain('1.0.0')
    expect(provenance?.textContent).toContain(profileDigest)
    expect(provenance?.textContent).toContain(snapshotDigest)
    expect(provenance?.innerHTML).toContain('開始前の警告</dt><dd>0 件')
    expect(container.querySelectorAll('.quick-start-actions button[type="submit"]')).toHaveLength(1)
    expect(container.querySelector<HTMLButtonElement>('.quick-start-actions button[type="submit"]')?.disabled).toBe(false)
    expect(markup).toMatch(
      /<button[^>]*class="primary-button"[^>]*type="submit"[^>]*>編集を開始<\/button>/,
    )
    expect(markup).not.toContain('>取消</button>')
    expect(markup).not.toContain('wizard-progress')
    expect(markup).not.toContain('<fieldset')
    expect(markup).not.toContain('コンテンツ名')
    expect(markup).not.toContain(profileId)
    expect(markup).not.toContain(templateId)
    expect(markup).not.toContain(candidateId)
    expect(markup).not.toContain(draftId)
    expect(markup).not.toContain('C:\\private')
    expect(markup).not.toContain('S-1-5-')

    provenance?.remove()
    expect(container.innerHTML).not.toContain(profileDigest)
    expect(container.innerHTML).not.toContain(snapshotDigest)
  })

  it('focuses quick start and activates only its start action', async () => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
    const container = document.createElement('div')
    document.body.append(container)
    const root = createRoot(container)
    const finalizeDraft = vi.fn()
    const input = completeInput()
    const state: ProjectState = {
      ...initialProjectState,
      canCreate: true,
      draft: sourceOnlyDraft(input),
      input,
    }

    try {
      act(() => {
        root.render(
          <ProjectWorkspaceView
            {...contextFor(state)}
            finalizeDraft={finalizeDraft}
          />,
        )
      })
      expect(document.activeElement?.id).toBe('quick-start-title')
      expect(container.querySelector('.wizard-progress')).toBeNull()

      const user = userEvent.setup({ document })
      const start = [...container.querySelectorAll<HTMLButtonElement>('button')]
        .find((button) => button.textContent?.trim() === '編集を開始')
      if (start === undefined) {
        throw new Error('Expected quick-start actions.')
      }

      act(() => start.focus())
      await act(async () => user.keyboard('[Enter]'))
      expect(finalizeDraft).toHaveBeenCalledOnce()
    } finally {
      act(() => root.unmount())
      container.remove()
      delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
        .IS_REACT_ACT_ENVIRONMENT
    }
  })

  it('blocks duplicate quick-start actions while a request is pending', () => {
    const input = completeInput()
    const markup = renderState({
      ...initialProjectState,
      canCreate: true,
      draft: sourceOnlyDraft(input),
      input,
      pending: {
        requestId: '00112233-4455-6677-8899-aabbccddeeff',
        method: 'project.finalizeDraft',
      },
    })

    expect(markup).toMatch(
      /<button[^>]*class="primary-button"[^>]*type="submit"[^>]*disabled=""[^>]*>編集を開始<\/button>/,
    )
    expect(markup).toContain('操作を処理しています。')
  })

  it('requires reselection without exposing the unavailable source token', () => {
    const input = completeInput()
    const markup = renderState({
      ...initialProjectState,
      canCreate: true,
      draft: {
        ...sourceOnlyDraft(input),
        sourceStatus: 'reselectionRequired',
      },
      input,
    })
    const container = document.createElement('div')
    container.innerHTML = markup
    const provenance = container.querySelector<HTMLDetailsElement>('details.completion-summary')

    expect(markup).toContain('再選択が必要')
    expect(markup).toContain('元動画を選び直す')
    expect(provenance?.open).toBe(false)
    expect(provenance?.innerHTML).toContain('開始前の警告</dt><dd>1 件')
    expect(markup).toMatch(
      /<button[^>]*class="primary-button"[^>]*disabled=""[^>]*>編集を開始<\/button>/,
    )
    expect(markup).not.toContain(candidateId)

    provenance?.remove()
    expect(container.textContent).toContain('編集を開始するには、元動画をもう一度選択してください。')
    expect(container.textContent).toContain('元動画の再選択を待っています。')
    expect(container.innerHTML).not.toContain(profileDigest)
    expect(container.innerHTML).not.toContain(snapshotDigest)
  })

  it('withholds unknown source-only artifacts, escapes visible names, and preserves the legacy wizard', () => {
    const input = completeInput()
    const quickStartMarkup = renderState({
      ...initialProjectState,
      draft: {
        ...sourceOnlyDraft(input),
        sourceOnly: {
          ...sourceOnlyDraft(input).sourceOnly!,
          artifactSet: [adversarialText],
        },
      },
      input,
    })
    const namedInput = { ...input, contentName: adversarialText }
    const namedMarkup = renderState({
      ...initialProjectState,
      draft: sourceOnlyDraft(namedInput),
      input: namedInput,
    })
    const legacyMarkup = renderState({
      ...initialProjectState,
      draft: projectDraft(input),
      input,
    })

    expect(quickStartMarkup).toContain('<dt>その他の成果物</dt>')
    expect(quickStartMarkup).toContain('その他の予定成果物')
    expect(quickStartMarkup).toContain('data-preview-state="planned"')
    expect(quickStartMarkup).toContain('生成・保存状況：未確認。品質評価：未評価（NOT EVALUATED）。')
    expect(quickStartMarkup).not.toContain(adversarialText)
    expect(quickStartMarkup).not.toContain('&lt;script&gt;')
    expect(quickStartMarkup).not.toContain('&lt;img')
    expect(quickStartMarkup).not.toContain('globalThis.compromised')
    expect(quickStartMarkup).not.toContain('onerror')
    expect(quickStartMarkup).not.toContain('<script')
    expect(quickStartMarkup).not.toContain('<img')
    expect(namedMarkup).toContain('&lt;script&gt;')
    expect(namedMarkup).toContain('&lt;img')
    expect(namedMarkup).toContain('&amp;tail')
    expect(namedMarkup).not.toContain(adversarialText)
    expect(namedMarkup).not.toContain('<script')
    expect(namedMarkup).not.toContain('<img')
    expect(legacyMarkup).toContain('wizard-progress')
    expect(legacyMarkup).toContain('案件作成ウィザード')
    expect(legacyMarkup).not.toContain('かんたん編集')
  })
})

describe('ProjectWorkspace workflow integration (UX022)', () => {
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

  function renderView(props: ProjectWorkspaceViewProps): void {
    act(() => root.render(<ProjectWorkspaceView {...props} />))
  }

  function button(name: string): HTMLButtonElement {
    return within(container).getByRole('button', { name, hidden: true }) as HTMLButtonElement
  }

  function draftState(sourceOnly: boolean): ProjectState {
    const input = completeInput()
    return {
      ...initialProjectState,
      canCreate: true,
      projects: [projectListItem('保存済みの案件')],
      draft: sourceOnly ? sourceOnlyDraft(input) : projectDraft(input),
      input,
    }
  }

  function callbacks(state: ProjectState): ProjectStateContextValue {
    return {
      state,
      refresh: vi.fn(),
      createDraft: vi.fn(),
      loadDraft: vi.fn(),
      deleteProject: vi.fn(),
      renameProject: vi.fn(),
      updateInput: vi.fn(),
      saveDraft: vi.fn(),
      pickSource: vi.fn(),
      finalizeDraft: vi.fn(),
      closeDraft: vi.fn(),
      clearError: vi.fn(),
    }
  }

  function expectNoOperations(props: ProjectStateContextValue): void {
    for (const name of [
      'refresh', 'createDraft', 'loadDraft', 'deleteProject', 'renameProject', 'updateInput', 'saveDraft',
      'pickSource', 'finalizeDraft', 'closeDraft', 'clearError',
    ] as const) {
      expect(props[name]).not.toHaveBeenCalled()
    }
  }

  it('renders only the list, quick start, or legacy wizard from the current draft', () => {
    const quick = draftState(true)
    renderView(callbacks({ ...initialProjectState, projects: quick.projects }))
    expect(container.querySelectorAll('.project-card')).toHaveLength(1)
    expect(container.querySelector('.quick-start-panel, .wizard-panel')).toBeNull()

    renderView(callbacks(quick))
    expect(container.querySelectorAll('.quick-start-panel')).toHaveLength(1)
    expect(container.querySelectorAll('.quick-start-actions button[type="submit"]')).toHaveLength(1)
    expect(container.querySelector('.project-list-panel, .project-card, .wizard-panel')).toBeNull()
    expect(container.textContent).not.toContain('保存済みの案件')

    renderView(callbacks(draftState(false)))
    expect(container.querySelectorAll('.wizard-progress button')).toHaveLength(5)
    expect(container.querySelectorAll('.project-form > fieldset')).toHaveLength(5)
    expect(container.querySelector('.project-list-panel, .project-card, .quick-start-panel')).toBeNull()
    expect(container.querySelector('table, .table-scroll')).toBeNull()
  })

  it.each([
    ['draft', 'notSelected', '下書き', '未選択'],
    ['finalized', 'reselectionRequired', '作成済み', '再選択が必要'],
  ] as const)('reopens a %s card by draft ID without inventing processing state', async (state, sourceStatus, stateLabel, sourceLabel) => {
    const props = callbacks({
      ...initialProjectState,
      projects: [{
        ...projectListItem('保存済みの案件'),
        state,
        sourceStatus,
        projectId: state === 'finalized' ? finalizedProjectId : null,
      }],
    })
    renderView(props)
    expect(container.querySelector('.state-badge')?.textContent).toBe(stateLabel)
    expect(container.querySelector('.source-status')?.textContent).toBe(sourceLabel)
    expect(container.textContent).not.toMatch(/生成済み|生成中|編集完了/)
    const user = userEvent.setup({ document })
    await act(async () => user.click(button('保存済みの案件を開く')))
    expect(props.loadDraft).toHaveBeenCalledExactlyOnceWith(draftId)
    expect(props.createDraft).not.toHaveBeenCalled()
    expect(props.finalizeDraft).not.toHaveBeenCalled()
  })

  it.each(['pending', 'other editor dirty', 'inactive'] as const)('disables creation, card reopening and paging while %s', async (reason) => {
    const props: ProjectWorkspaceViewProps = {
      ...callbacks({
        ...initialProjectState,
        canCreate: true,
        projects: Array.from({ length: 7 }, (_, index) => ({
          ...projectListItem(`案件 ${index + 1}`),
          draftId: `00000000-0000-4000-8000-${(index + 1).toString().padStart(12, '0')}`,
        })),
        pending: reason === 'pending'
          ? { requestId: 'list-request', method: 'project.list' }
          : null,
      }),
      active: reason !== 'inactive',
      navigationBlocked: reason === 'other editor dirty',
    }
    renderView(props)
    const user = userEvent.setup({ document })
    for (const name of ['新しい案件を作成', '案件 1を開く', '次のページ']) {
      expect(button(name).disabled).toBe(true)
      await act(async () => user.click(button(name)))
    }
    expectNoOperations(props)
    expect(container.querySelectorAll('.project-card')).toHaveLength(6)
    if (reason === 'other editor dirty') {
      expect(container.textContent).toContain('別の画面に未保存の変更があります')
      // A catalog/list refresh is not a project switch.
      expect(button('一覧を更新').disabled).toBe(false)
    }

    renderView({ ...props, active: true, navigationBlocked: false, state: { ...props.state, pending: null } })
    expect(button('新しい案件を作成').disabled).toBe(false)
    expect(button('案件 1を開く').disabled).toBe(false)
    await act(async () => user.click(button('案件 1を開く')))
    expect(props.loadDraft).toHaveBeenCalledExactlyOnceWith(props.state.projects[0]!.draftId)
  })

  it('deletes a listed project only after the user confirms', async () => {
    const props = callbacks({
      ...initialProjectState,
      canCreate: true,
      projects: [projectListItem('消す案件'), { ...projectListItem('残す案件'), draftId: '21112222-3333-4444-8555-666677778888' }],
    })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    renderView(props)
    const user = userEvent.setup({ document })

    await act(async () => user.click(button('消す案件を削除')))
    expect(confirm).toHaveBeenCalledOnce()
    expect(confirm.mock.calls[0]![0]).toContain('案件「消す案件」を削除しますか？')
    expect(confirm.mock.calls[0]![0]).toContain('元に戻せません')
    expect(confirm.mock.calls[0]![0]).toContain('作業ファイルと、アプリの保存領域に書き出したファイルも削除されます')
    expect(confirm.mock.calls[0]![0]).toContain('選んだ元の動画ファイルは削除されません')
    expectNoOperations(props)

    confirm.mockReturnValue(true)
    await act(async () => user.click(button('消す案件を削除')))
    expect(props.deleteProject).toHaveBeenCalledExactlyOnceWith(draftId)
    expect(props.createDraft).not.toHaveBeenCalled()
    expect(props.loadDraft).not.toHaveBeenCalled()
  })

  // FR-046 AC-021
  it('deletes the open project from the detail screen only after confirmation', async () => {
    const props = callbacks(draftState(true))
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    renderView(props)
    const user = userEvent.setup({ document })

    await act(async () => user.click(button('案件削除')))
    expect(confirm).toHaveBeenCalledOnce()
    expect(confirm.mock.calls[0]![0]).toContain('案件「Hubらじお」を削除しますか？')
    expect(confirm.mock.calls[0]![0]).toContain('元に戻せません')
    expect(confirm.mock.calls[0]![0]).toContain('選んだ元の動画ファイルは削除されません')
    expectNoOperations(props)

    confirm.mockReturnValue(true)
    await act(async () => user.click(button('案件削除')))
    expect(props.deleteProject).toHaveBeenCalledExactlyOnceWith(draftId)
    expect(props.finalizeDraft).not.toHaveBeenCalled()
  })

  // FR-046 AC-021
  it.each([true, false])('shows the detail delete button on the %s source-only detail screen', (sourceOnly) => {
    renderView(callbacks(draftState(sourceOnly)))
    expect(container.querySelectorAll('.project-detail-actions button')).toHaveLength(1)
    expect(button('案件削除').disabled).toBe(false)
  })

  // FR-046 AC-021
  it('hides the detail delete button without permission and disables it while pending', async () => {
    renderView(callbacks({ ...draftState(true), canCreate: false }))
    expect(within(container).queryByRole('button', { name: '案件削除', hidden: true })).toBeNull()

    const props = callbacks({
      ...draftState(true),
      pending: { requestId: 'r', method: 'project.finalizeDraft' },
    })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderView(props)
    expect(button('案件削除').disabled).toBe(true)
    await act(async () => userEvent.setup({ document }).click(button('案件削除')))
    expect(confirm).not.toHaveBeenCalled()
    expectNoOperations(props)
  })

  // FR-040 AC-013
  it('renames the open project through the name editor and keeps the card name in sync', async () => {
    const props = callbacks(draftState(true))
    renderView(props)
    const user = userEvent.setup({ document })
    const input = within(container).getByLabelText<HTMLInputElement>('案件名（この画面と一覧に表示される名前）')

    expect(input.value).toBe('Hubらじお')
    expect(button('案件名を保存').disabled).toBe(true)
    await act(async () => { await user.clear(input); await user.type(input, '  社内向け 第1回 ') })
    expect(button('案件名を保存').disabled).toBe(false)
    await act(async () => user.click(button('案件名を保存')))

    expect(props.renameProject).toHaveBeenCalledExactlyOnceWith(draftId, '社内向け 第1回')
    expect(props.finalizeDraft).not.toHaveBeenCalled()
    expect(props.saveDraft).not.toHaveBeenCalled()
  })

  // FR-040 AC-013
  it('shows the renamed display name in the project card and the quick start while the source name is kept', () => {
    const state = draftState(true)
    renderView(callbacks({ ...state, draft: { ...state.draft!, displayName: '表示名' } }))
    expect(container.querySelector('.quick-start-content-name')?.textContent).toBe('表示名')
    expect(within(container).getByLabelText<HTMLInputElement>('案件名（この画面と一覧に表示される名前）').value).toBe('表示名')
    expect(container.textContent).toContain('元の名前：Hubらじお')

    renderView(callbacks({
      ...initialProjectState,
      canCreate: true,
      projects: [{ ...projectListItem('元の名前'), displayName: '一覧の表示名' }],
    }))
    expect(container.querySelector('.project-card-name')?.textContent).toBe('一覧の表示名')
    expect(button('一覧の表示名を開く')).toBeDefined()
  })

  // FR-040 AC-014
  it.each(['pending', 'inactive', 'no authority'] as const)('offers no working rename while %s', async (reason) => {
    const state = draftState(true)
    const props: ProjectWorkspaceViewProps = {
      ...callbacks({
        ...state,
        canCreate: reason !== 'no authority',
        pending: reason === 'pending' ? { requestId: 'r1', method: 'project.saveDraft' } : null,
      }),
      active: reason !== 'inactive',
    }
    renderView(props)

    if (reason === 'no authority') {
      expect(container.querySelector('.project-name-editor')).toBeNull()
    } else {
      expect(within(container).getByLabelText<HTMLInputElement>('案件名（この画面と一覧に表示される名前）').disabled).toBe(reason === 'pending')
      expect(within(container).getByRole<HTMLButtonElement>('button', { name: '案件名を保存', hidden: true }).disabled).toBe(true)
    }
    expect(props.renameProject).not.toHaveBeenCalled()
  })

  // FR-040 AC-014
  it('blocks an invalid name in the editor and explains why without calling the host', async () => {
    const props = callbacks(draftState(true))
    renderView(props)
    const user = userEvent.setup({ document })
    const input = within(container).getByLabelText<HTMLInputElement>('案件名（この画面と一覧に表示される名前）')

    await act(async () => user.clear(input))
    expect(within(container).getByRole('alert').textContent).toContain('1〜100 文字')
    expect(input.getAttribute('aria-invalid')).toBe('true')
    expect(button('案件名を保存').disabled).toBe(true)
    await act(async () => user.type(input, '{Enter}'))
    await act(async () => { await user.clear(input); await user.type(input, 'あ'.repeat(101)) })
    expect(button('案件名を保存').disabled).toBe(true)
    expect(props.renameProject).not.toHaveBeenCalled()
  })

  // FR-040 AC-015
  it('states that the publication scope is fixed and how the pending state changes', () => {
    renderView(callbacks(draftState(true)))
    const note = container.querySelector('.project-status-note')!
    expect(note.textContent).toContain('公開範囲は「社内限定」で固定されており、変更できません')
    expect(note.textContent).toContain('「編集を開始」を押すと「作成済み」に変わります')

    const state = draftState(true)
    renderView(callbacks({ ...state, draft: { ...state.draft!, isFinalized: true, projectId: '22223333-4444-4555-8666-777788889999' } }))
    expect(container.querySelector('.project-status-note')!.textContent).toContain('「確認待ち」に戻すことはできません')
  })

  it('shows a status notice when the project was deleted but some files remain', () => {
    renderView(callbacks({ ...initialProjectState, notice: '一部のファイルを削除できませんでした。' }))
    expect(container.querySelector('.permission-note')?.textContent).toBe('一部のファイルを削除できませんでした。')
    renderView(callbacks(initialProjectState))
    expect(container.querySelector('.permission-note')).toBeNull()
  })

  it.each(['pending', 'other editor dirty', 'inactive', 'no authority'] as const)('offers no working delete while %s', async (reason) => {
    const props: ProjectWorkspaceViewProps = {
      ...callbacks({
        ...initialProjectState,
        canCreate: reason !== 'no authority',
        projects: [projectListItem('消す案件')],
        pending: reason === 'pending'
          ? { requestId: 'list-request', method: 'project.list' }
          : null,
      }),
      active: reason !== 'inactive',
      navigationBlocked: reason === 'other editor dirty',
    }
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderView(props)
    const user = userEvent.setup({ document })

    if (reason === 'no authority') {
      expect(within(container).queryByRole('button', { name: '消す案件を削除', hidden: true })).toBeNull()
    } else {
      expect(button('消す案件を削除').disabled).toBe(true)
      await act(async () => user.click(button('消す案件を削除')))
    }
    expect(confirm).not.toHaveBeenCalled()
    expectNoOperations(props)
  })

  it('blocks destructive OCC reload while its own draft input is dirty', async () => {
    const saved = draftState(false)
    const props = callbacks({
      ...saved,
      input: { ...saved.input, contentName: '未保存の名前' },
      error: { code: 'project.revision_conflict', message: '再読み込みしてください。' },
    })
    renderView(props)
    expect(button('最新版を再読み込み').disabled).toBe(true)
    const user = userEvent.setup({ document })
    await act(async () => user.click(button('最新版を再読み込み')))
    expectNoOperations(props)

    renderView({ ...props, state: { ...props.state, input: saved.input } })
    expect(button('最新版を再読み込み').disabled).toBe(false)
    await act(async () => user.click(button('最新版を再読み込み')))
    expect(props.loadDraft).toHaveBeenCalledExactlyOnceWith(draftId)
  })

  it.each(['pending', 'other editor dirty', 'inactive'] as const)('guards all quick-start departure and mutation callbacks while %s', async (reason) => {
    const saved = draftState(true)
    const props: ProjectWorkspaceViewProps = {
      ...callbacks({
        ...saved,
        draft: { ...saved.draft!, sourceStatus: 'reselectionRequired' },
        input: { ...saved.input, contentName: '未保存の名前' },
        pending: reason === 'pending'
          ? { requestId: 'save-request', method: 'project.saveDraft' }
          : null,
      }),
      active: reason !== 'inactive',
      navigationBlocked: reason === 'other editor dirty',
    }
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderView(props)
    const user = userEvent.setup({ document })
    for (const name of ['元動画を選び直す']) {
      await act(async () => user.click(button(name)))
    }
    // A synthetic submit bypasses button disabled state, but not the workspace guard.
    renderView({ ...props, state: { ...props.state, draft: saved.draft } })
    act(() => container.querySelector('form')!.dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    ))
    expect(confirm).not.toHaveBeenCalled()
    expectNoOperations(props)
  })

  it('shows the cause and a retry action when the editing runtime is unavailable, and retries finalization', async () => {
    const saved = draftState(true)
    const message = '編集を開始できません。動画処理ツール（Tools\\ffmpeg.exe）が見つかりません。'
    const props = callbacks({
      ...saved,
      error: { code: 'project.runtime_unavailable', message },
    })
    renderView(props)
    expect(container.querySelector('[role="alert"]')?.textContent).toContain(message)
    const user = userEvent.setup({ document })
    await act(async () => user.click(button('もう一度試す（自動で再確認）')))
    expect(props.finalizeDraft).toHaveBeenCalledOnce()
  })

  it.each(['list', 'quick', 'legacy'] as const)('never focuses inactive %s content or its error, and keeps error focus on activation', (kind) => {
    const state = kind === 'list' ? initialProjectState : draftState(kind === 'quick')
    const props = callbacks({
      ...state,
      error: { code: 'project.operation_failed', message: '操作を確認してください。' },
    })
    act(() => otherControl.focus())
    const focus = vi.spyOn(HTMLElement.prototype, 'focus')
    renderView({ ...props, active: false })
    expect(focus).not.toHaveBeenCalled()
    expect(document.activeElement).toBe(otherControl)
    expect(container.querySelector('.project-workspace')?.hasAttribute('hidden')).toBe(true)
    expect(container.querySelector('.project-workspace')?.hasAttribute('inert')).toBe(true)
    if (props.state.draft !== null) {
      renderView({
        ...props,
        active: false,
        state: { ...props.state, draft: { ...props.state.draft, draftId: finalizedProjectId } },
      })
      expect(focus).not.toHaveBeenCalled()
    }
    expectNoOperations(props)

    renderView({ ...props, active: true })
    expect(document.activeElement).toBe(container.querySelector('[role="alert"]'))
    expect(container.querySelector('.project-workspace')?.hasAttribute('hidden')).toBe(false)
    expect(container.querySelector('.project-workspace')?.hasAttribute('inert')).toBe(false)
    expectNoOperations(props)
  })

  it.each([true, false])('preserves mounted draft UI and dirty state across tab visibility (sourceOnly=%s)', async (sourceOnly) => {
    const saved = draftState(sourceOnly)
    const props = callbacks({ ...saved, input: { ...saved.input, contentName: '未保存の名前' } })
    const onDirtyChange = vi.fn()
    renderView({ ...props, onDirtyChange })
    const user = userEvent.setup({ document })
    const details = container.querySelector<HTMLDetailsElement>('details')!
    const lastStep = container.querySelectorAll<HTMLButtonElement>('.wizard-progress button')[4]
    if (sourceOnly) {
      await act(async () => user.click(details.querySelector('summary')!))
      expect(details.open).toBe(true)
    } else {
      await act(async () => user.click(lastStep!))
      expect(lastStep!.getAttribute('aria-current')).toBe('step')
    }
    act(() => otherControl.focus())
    const focus = vi.spyOn(HTMLElement.prototype, 'focus')
    renderView({ ...props, active: false, onDirtyChange })
    expect(document.activeElement).toBe(otherControl)
    expect(focus).not.toHaveBeenCalled()
    expect(onDirtyChange.mock.calls).toEqual([[true]])
    expectNoOperations(props)

    renderView({ ...props, active: true, onDirtyChange })
    expect(document.activeElement?.id).toBe(sourceOnly ? 'quick-start-title' : 'wizard-title')
    if (sourceOnly) {
      expect(container.querySelector('details')).toBe(details)
      expect(details.open).toBe(true)
    } else {
      expect(container.querySelectorAll('.wizard-progress button')[4]).toBe(lastStep)
      expect(lastStep!.getAttribute('aria-current')).toBe('step')
      expect(container.querySelector<HTMLInputElement>('#project-content-name')?.value).toBe('未保存の名前')
    }
    expect(onDirtyChange.mock.calls).toEqual([[true]])
    expectNoOperations(props)
  })

  it('notifies derived identity and dirty changes without loops from inline parent callbacks or visibility', () => {
    const onProjectChange = vi.fn()
    const onDirtyChange = vi.fn()
    const callbacksForParent = callbacks(initialProjectState)
    function Parent({ state, active = true }: { state: ProjectState; active?: boolean }) {
      const [, update] = useState(0)
      return (
        <ProjectWorkspaceView
          {...callbacksForParent}
          state={state}
          active={active}
          onProjectChange={(value) => {
            onProjectChange(value)
            if (onProjectChange.mock.calls.length > 5) throw new Error('Repeated project notification.')
            update((count) => count + 1)
          }}
          onDirtyChange={(value) => {
            onDirtyChange(value)
            if (onDirtyChange.mock.calls.length > 5) throw new Error('Repeated dirty notification.')
            update((count) => count + 1)
          }}
        />
      )
    }
    act(() => root.render(<Parent state={initialProjectState} />))
    expect(onProjectChange.mock.calls).toEqual([[null]])
    expect(onDirtyChange.mock.calls).toEqual([[false]])
    const saved = draftState(true)
    const dirty = { ...saved, input: { ...saved.input, contentName: '未保存の名前' } }
    act(() => root.render(<Parent state={dirty} />))
    act(() => root.render(<Parent state={dirty} active={false} />))
    act(() => root.render(<Parent state={dirty} />))
    expect(onProjectChange.mock.calls).toEqual([[null]])
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])

    const finalized = {
      ...saved,
      draft: { ...saved.draft!, projectId: finalizedProjectId, isFinalized: true },
    }
    act(() => root.render(<Parent state={finalized} active={false} />))
    act(() => root.render(<Parent state={finalized} />))
    expect(onProjectChange.mock.calls).toEqual([[null], [finalizedProjectId]])
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
    act(() => root.render(<Parent state={initialProjectState} />))
    expect(onProjectChange.mock.calls).toEqual([[null], [finalizedProjectId], [null]])
    expectNoOperations(callbacksForParent)
  })

  it('uses the existing provider for one native finalize request without a separate frontend job-start request', async () => {
    const requests: Array<Parameters<BridgeTransport['postMessage']>[0]> = []
    const listeners = new Set<(event: BridgeMessageEvent) => void>()
    const transport: BridgeTransport = {
      postMessage: (request) => { requests.push(request) },
      addEventListener: (_type, listener) => { listeners.add(listener) },
      removeEventListener: (_type, listener) => { listeners.delete(listener) },
    }
    const createClient = vi.fn(() => createBridgeClient(transport))
    const onProjectChange = vi.fn()
    const onDirtyChange = vi.fn()
    function Parent(props: ProjectWorkspaceProps) {
      // The shell reads the existing provider, rather than mirroring project state.
      const { state } = useProjectState()
      return (
        <>
          <output data-testid="parent-project">{state.draft?.projectId ?? '未選択'}</output>
          <ProjectWorkspace {...props} onProjectChange={onProjectChange} onDirtyChange={onDirtyChange} />
        </>
      )
    }
    function renderParent(active: boolean): void {
      act(() => root.render(
        <ProjectStateProvider createClient={createClient}>
          <Parent active={active} />
        </ProjectStateProvider>,
      ))
    }
    function emit(response: ProjectBridgeResponse): void {
      act(() => { for (const listener of listeners) listener({ data: response }) })
    }
    renderParent(false)
    expect(requests.map((request) => request.method)).toEqual(['project.list'])
    renderParent(true)
    renderParent(false)
    renderParent(true)
    expect(createClient).toHaveBeenCalledOnce()
    expect(requests).toHaveLength(1)
    expect(within(container).getByTestId('parent-project').textContent).toBe('未選択')
    expect(button('新しい案件を作成').disabled).toBe(true)
    emit({
      version: 1,
      requestId: requests[0]!.requestId,
      method: 'project.list',
      result: {
        projects: [],
        catalog: {
          outputProfiles: [{ id: profileId, name: 'long-form-1280x720' }],
          brandTemplates: [{ id: templateId, name: 'development-neutral-long-form' }],
        },
        canCreate: true,
      },
    })
    expect(container.textContent).toContain('「新しい案件を作成」から始められます')
    const user = userEvent.setup({ document })
    await act(async () => user.click(button('新しい案件を作成')))
    expect(requests.map((request) => request.method)).toEqual(['project.list', 'project.createDraft'])
    expect(requests[1]!.params).toEqual({})
    expect(container.querySelector('.wizard-panel, .quick-start-panel')).toBeNull()
    expect(container.querySelector('.project-list-panel')).not.toBeNull()
    expect(button('新しい案件を作成').disabled).toBe(true)
    renderParent(false)
    renderParent(true)
    expect(requests).toHaveLength(2)

    const input = completeInput()
    const draft = sourceOnlyDraft(input)
    emit({ version: 1, requestId: requests[1]!.requestId, method: 'project.createDraft', result: { draft } })
    expect(container.querySelector('.project-list-panel, .wizard-panel')).toBeNull()
    expect(container.querySelectorAll('.quick-start-actions button[type="submit"]')).toHaveLength(1)
    expect(within(container).getByTestId('parent-project').textContent).toBe('未選択')
    renderParent(false)
    renderParent(true)
    expect(requests).toHaveLength(2)
    await act(async () => user.click(button('編集を開始')))
    expect(requests[2]).toMatchObject({
      method: 'project.finalizeDraft',
      params: { draftId, expectedRevision: draft.revision, input },
    })
    await act(async () => user.click(button('編集を開始')))
    renderParent(false)
    expect(requests).toHaveLength(3)
    act(() => otherControl.focus())
    const focus = vi.spyOn(HTMLElement.prototype, 'focus')
    emit({
      version: 1,
      requestId: requests[2]!.requestId,
      method: 'project.finalizeDraft',
      result: { draft: { ...draft, revision: draft.revision + 1, isFinalized: true, projectId: finalizedProjectId } },
    })
    expect(focus).not.toHaveBeenCalled()
    expect(document.activeElement).toBe(otherControl)
    expect(within(container).getByTestId('parent-project').textContent).toBe(finalizedProjectId)
    expect(container.textContent).toContain('案件を作成しました。自動編集画面で処理状況を確認できます。')
    expect(container.querySelector('.project-list-panel')).toBeNull()
    expect(onProjectChange.mock.calls).toEqual([[null], [finalizedProjectId]])
    expect(onDirtyChange.mock.calls).toEqual([[false]])
    renderParent(true)
    expect(within(container).getByTestId('parent-project').textContent).toBe(finalizedProjectId)
    expect(document.activeElement?.id).toBe('quick-start-title')
    expect(requests).toHaveLength(3)
    expect(within(container).queryByRole('button', { name: '別の動画を選ぶ' })).toBeNull()
    expect(onProjectChange.mock.calls).toEqual([[null], [finalizedProjectId]])
    // Native finalization accepts/schedules the real pipeline. This transport
    // fixture verifies no additional frontend job-start request, not native execution.
    expect(requests.map((request) => request.method)).toEqual([
      'project.list', 'project.createDraft', 'project.finalizeDraft',
    ])
    expect(createClient).toHaveBeenCalledOnce()
  })
})

function renderState(state: ProjectState): string {
  return renderToStaticMarkup(
    <ProjectWorkspaceView {...contextFor(state)} />,
  )
}

function contextFor(state: ProjectState): ProjectStateContextValue {
  return {
    state,
    refresh: () => undefined,
    createDraft: () => undefined,
    loadDraft: () => undefined,
    deleteProject: () => undefined,
    renameProject: () => undefined,
    updateInput: () => undefined,
    saveDraft: () => undefined,
    pickSource: () => undefined,
    finalizeDraft: () => undefined,
    closeDraft: () => undefined,
    clearError: () => undefined,
  }
}

function completeInput(): ProjectDraftInput {
  return {
    sourceCandidateId: candidateId,
    contentName: 'Hubらじお',
    purpose: 'knowledge sharing',
    audience: 'developers',
    publicationScope: 'internalOnly',
    outputProfileIds: [profileId],
    language: 'ja-JP',
    performerNames: ['person'],
    deadlineSelection: { hasDeadline: false, value: null },
    brandTemplateId: templateId,
  }
}

function projectDraft(input: ProjectDraftInput): ProjectDraft {
  return {
    draftId,
    revision: 2,
    isFinalized: false,
    projectId: null,
    sourceStatus: 'available',
    ...input,
    missingFields: [],
    updatedAt: timestamp,
    sourceOnly: null,
  }
}

function projectListItem(contentName: string): ProjectListItem {
  return {
    draftId,
    projectId: null,
    state: 'draft',
    contentName,
    episodeNumber: null,
    sourceStatus: 'available',
    owner: null,
    publicationScope: 'internalOnly',
    editState: null,
    reviewState: null,
    outputFormats: [adversarialText],
    updatedAt: timestamp,
  }
}

function sourceOnlyDraft(input: ProjectDraftInput): ProjectDraft {
  return {
    ...projectDraft(input),
    sourceOnly: {
      profileId,
      profileVersion: '1.0.0',
      profileDigest,
      settingsSnapshotDigest: snapshotDigest,
      artifactSet: ['landscapeLongForm', 'verticalShort'],
    },
  }
}