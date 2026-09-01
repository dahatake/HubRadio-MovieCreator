// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'

import type {
  ProjectDraft,
  ProjectDraftInput,
  ProjectListItem,
} from '../../bridge'
import {
  getWizardStepAfterKey,
  ProjectWorkspaceView,
} from './ProjectWorkspace'
import type { ProjectStateContextValue } from './ProjectStateProvider'
import { initialProjectState, type ProjectState } from './projectState'

const draftId = '11112222-3333-4444-8555-666677778888'
const candidateId = '99990000-aaaa-4bbb-8ccc-ddddeeeeffff'
const profileId = '14b4f6b6-1ebc-55c3-9682-2c7e9ad226fa'
const templateId = 'bf08c776-0f97-5373-a091-c70b60acd569'
const timestamp = '2026-09-01T12:00:00.0000000+00:00'
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

  it('renders all canonical project-list columns and escapes user-controlled text', () => {
    const markup = renderState({
      ...initialProjectState,
      canCreate: true,
      projects: [projectListItem(adversarialText)],
    })

    for (const column of [
      'コンテンツ名',
      'エピソード番号',
      '元動画',
      '担当者',
      '公開区分',
      '編集状態',
      'レビュー状態',
      '出力形式',
      '更新日時',
    ]) {
      expect(markup).toContain(column)
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
      /<button[^>]*disabled=""[^>]*>新しい案件<\/button>/,
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