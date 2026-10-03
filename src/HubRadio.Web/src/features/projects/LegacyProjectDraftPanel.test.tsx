// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { ProjectDraft, ProjectDraftInput } from '../../bridge'
import {
  LegacyProjectDraftPanel,
  type LegacyProjectDraftPanelProps,
} from './LegacyProjectDraftPanel'
import type { ProjectStateContextValue } from './ProjectStateProvider'
import { ProjectWorkspaceView } from './ProjectWorkspace'
import { initialProjectState, type ProjectState } from './projectState'

const draftId = '11112222-3333-4444-8555-666677778888'
const otherDraftId = '21112222-3333-4444-8555-666677778888'
const profileId = '14b4f6b6-1ebc-55c3-9682-2c7e9ad226fa'
const otherProfileId = '24b4f6b6-1ebc-55c3-9682-2c7e9ad226fa'
const templateId = 'bf08c776-0f97-5373-a091-c70b60acd569'

describe('LegacyProjectDraftPanel', () => {
  let container: HTMLDivElement
  let root: Root

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  function renderWorkspace(state: ProjectState): void {
    act(() => root.render(<ProjectWorkspaceView {...contextFor(state)} />))
  }

  it('keeps five steps and draft-ID focus resets without overriding workspace errors', async () => {
    const state = legacyState()
    renderWorkspace(state)

    const steps = [...container.querySelectorAll<HTMLButtonElement>(
      '.wizard-progress button',
    )]
    expect(steps).toHaveLength(5)
    expect(container.querySelectorAll('.project-form > fieldset')).toHaveLength(5)
    expect(container.querySelector('#project-content-name')).not.toBeNull()
    expect(document.activeElement).toBe(container.querySelector('#wizard-title'))

    const user = userEvent.setup({ document })
    act(() => steps[0]!.focus())
    await act(async () => user.keyboard('[End]'))
    expect(steps[4]!.getAttribute('aria-current')).toBe('step')

    renderWorkspace({
      ...state,
      draft: { ...state.draft, revision: state.draft.revision + 1 },
    })
    expect(steps[4]!.getAttribute('aria-current')).toBe('step')
    expect(document.activeElement).toBe(steps[4])

    renderWorkspace({ ...state, draft: { ...state.draft, draftId: otherDraftId } })
    expect(steps[0]!.getAttribute('aria-current')).toBe('step')
    expect(document.activeElement).toBe(container.querySelector('#wizard-title'))

    renderWorkspace({
      ...state,
      error: {
        code: 'project.revision_conflict',
        message: '下書きが別の操作で更新されました。再読み込みしてください。',
      },
    })
    expect(document.activeElement).toBe(container.querySelector('[role="alert"]'))

    renderWorkspace(initialProjectState)
    expect(container.querySelector('.wizard-panel')).toBeNull()
    renderWorkspace(state)
    expect(document.activeElement).toBe(container.querySelector('#wizard-title'))
    expect(container.querySelector('.wizard-progress [aria-current="step"]')?.textContent)
      .toBe('1元動画と名前')
  })

  it('updates frozen inputs immutably while keeping catalog order, case and validation', async () => {
    const state = legacyState()
    const updateInput = vi.fn<(input: ProjectDraftInput) => void>()
    const props: LegacyProjectDraftPanelProps = {
      state,
      updateInput,
      saveDraft: vi.fn(),
      pickSource: vi.fn(),
      finalizeDraft: vi.fn(),
      closeDraft: vi.fn(),
    }
    act(() => root.render(<LegacyProjectDraftPanel {...props} />))

    const finish = container.querySelector<HTMLButtonElement>(
      '.wizard-navigation .primary-button',
    )!
    expect(finish.disabled).toBe(false)
    expect(container.querySelector('.project-list-panel')).toBeNull()
    expect(container.querySelector('.quick-start-panel')).toBeNull()

    const user = userEvent.setup({ document })
    const steps = [...container.querySelectorAll<HTMLButtonElement>(
      '.wizard-progress button',
    )]
    await act(async () => user.click(steps[2]!))
    const outputProfile = container.querySelector<HTMLInputElement>('.checkbox-grid input')!
    await act(async () => user.click(outputProfile))
    expect(updateInput).toHaveBeenLastCalledWith({
      ...state.input,
      outputProfileIds: [profileId, otherProfileId],
    })
    expect(updateInput.mock.lastCall![0]).not.toBe(state.input)
    expect(state.input.outputProfileIds).toEqual([otherProfileId])

    await act(async () => user.click(steps[3]!))
    const addPerformer = [...container.querySelectorAll<HTMLButtonElement>('button')]
      .find((button) => button.textContent?.trim() === '出演者を追加')!
    await act(async () => user.click(addPerformer))
    const updatedInput = updateInput.mock.lastCall![0]
    expect(updatedInput).toEqual({
      ...state.input,
      performerNames: ['Person', 'person', ''],
    })
    expect(updatedInput).not.toBe(state.input)
    expect(state.input.performerNames).toEqual(['Person', 'person'])

    act(() => root.render(
      <LegacyProjectDraftPanel {...props} state={{ ...state, input: updatedInput }} />,
    ))
    expect(finish.disabled).toBe(true)
    expect(container.querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled)
      .toBe(true)
  })
})

function contextFor(state: ProjectState): ProjectStateContextValue {
  return {
    state,
    refresh: () => undefined,
    createDraft: () => undefined,
    loadDraft: () => undefined,
    deleteProject: () => undefined,
    updateInput: () => undefined,
    saveDraft: () => undefined,
    pickSource: () => undefined,
    finalizeDraft: () => undefined,
    closeDraft: () => undefined,
    clearError: () => undefined,
  }
}

function legacyState(): ProjectState & { readonly draft: ProjectDraft } {
  const input = Object.freeze<ProjectDraftInput>({
    sourceCandidateId: '99990000-aaaa-4bbb-8ccc-ddddeeeeffff',
    contentName: 'Hubらじお',
    purpose: 'knowledge sharing',
    audience: 'developers',
    publicationScope: 'internalOnly',
    outputProfileIds: Object.freeze([otherProfileId]),
    language: 'ja-JP',
    performerNames: Object.freeze(['Person', 'person']),
    deadlineSelection: Object.freeze({ hasDeadline: false, value: null }),
    brandTemplateId: templateId,
  })
  return {
    ...initialProjectState,
    canCreate: true,
    catalog: {
      outputProfiles: [
        { id: profileId, name: 'long-form-1280x720' },
        { id: otherProfileId, name: 'vertical-short-1080x1920' },
      ],
      brandTemplates: [{ id: templateId, name: 'development-neutral-long-form' }],
    },
    draft: {
      ...input,
      draftId,
      revision: 2,
      isFinalized: false,
      projectId: null,
      sourceStatus: 'available',
      missingFields: [],
      updatedAt: '2026-09-01T12:00:00.0000000+00:00',
      sourceOnly: null,
    },
    input,
  }
}