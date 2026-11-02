// @vitest-environment jsdom

import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fireEvent, within } from '@testing-library/dom'
import userEvent from '@testing-library/user-event'
import { act, StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { BridgeMessageEvent, BridgeTransport, ProjectDraft, ProjectDraftInput, ProjectListItem } from '../../bridge'
import { AppStateProvider, useAppState } from '../../state'
import type { AnalysisSnapshot } from '../analysis/analysisProtocol'
import { analysisTopicId, createAnalysisResponseValue } from '../analysis/analysisTestFixture'
import { ComparisonStateProvider } from '../compare'
import { DeliveryStateProvider } from '../delivery'
import { LayoutStateProvider, useLayoutState } from '../layout'
import { parseLayoutBridgeResponse, type LayoutSnapshot, type LayoutVersionContext } from '../layout/layoutProtocol'
import { createLayoutResponseValue, createSubtitleOnlyLayoutResponseValue } from '../layout/layoutTestFixture'
import { PlayerNavigationProvider } from '../player'
import { parsePlayerReadResponse } from '../player/playerProtocol'
import { createPlayerReadResponseValue, createPlayerSeekResponseValue, playerProjectId } from '../player/playerTestFixture'
import { PreviewModeProvider } from '../preview-modes'
import { ProjectStateProvider, useProjectState } from '../projects/ProjectStateProvider'
import { ReviewStateProvider } from '../review'
import { SettingsWorkspaceStateProvider, useSettingsWorkspace, type GlobalSettingsSnapshot, type SettingDefinition } from '../settings'
import { createTimelineReadResponseValue, timelineSourceElementId } from '../timeline/timelineTestFixture'
import { parseTranscriptResponse } from '../transcript/transcriptProtocol'
import { createTranscriptResponseValue, projectId, revisionDigest, revisionId, timedTokenId } from '../transcript/transcriptTestFixture'
import { WorkflowWorkspace } from './WorkflowWorkspace'
import type { PipelineSnapshot, SourceOnlyResultArtifact } from './pipelineProtocol'
import { createCompletedPipeline, createPipelineArtifact, createPipelineSnapshot, fixtureId, setPipelineWarningTarget } from './pipelineTestFixture'
import { projectListStepLabels, projectListSteps, workflowStepLabels, workflowTools, type WorkflowStep, type WorkflowTool } from './workflowNavigation'

// All identities and responses below are test fixtures, never production bindings.
const draftId = '11112222-3333-4444-8555-666677778888'
const sourceCandidateId = '99990000-aaaa-4bbb-8ccc-ddddeeeeffff'
const profileId = '14b4f6b6-1ebc-55c3-9682-2c7e9ad226fa'
const templateId = 'bf08c776-0f97-5373-a091-c70b60acd569'
const timestamp = '2026-09-07T00:00:00.0000000+00:00'
const titleKey = 'project.title'
const countKey = 'project.verticalShortCandidateCount'
const definitions: readonly SettingDefinition[] = [
  {
    key: titleKey, displayName: 'タイトル', description: '統合試験用の設定', group: 'projectAndOutput',
    isAdvanced: false, unit: 'none', defaultValue: { kind: 'text', textValue: '保存済みタイトル' },
    minimum: null, maximum: null, allowedTextValues: [], evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }],
  },
  {
    key: countKey, displayName: '短尺候補数', description: '統合試験用の候補数', group: 'projectAndOutput',
    isAdvanced: false, unit: 'count', defaultValue: { kind: 'integer', integerValue: '3' },
    minimum: { kind: 'integer', integerValue: '0' }, maximum: { kind: 'integer', integerValue: '3' }, allowedTextValues: [], evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }],
  },
]
const globalSnapshot: GlobalSettingsSnapshot = {
  projectId: null, profileId, profileName: 'Workflow settings fixture', profileVersion: '1.0.0',
  profileDigest: 'a'.repeat(64), initialSnapshotDigest: 'a'.repeat(64), snapshotDigest: 'b'.repeat(64),
  revision: 0, updatedAt: timestamp, definitions, definitionCount: definitions.length,
  initialValues: definitions.map(item => ({ key: item.key, value: item.defaultValue })),
  values: definitions.map(item => ({ key: item.key, value: item.defaultValue })),
}
const cleanupFunctions = new Set<() => Promise<void>>()

afterEach(async () => {
  try { for (const cleanup of [...cleanupFunctions]) await cleanup() }
  finally { vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals() }
})

describe('WorkflowWorkspace UX-039A', () => {
  // FR-061 AC-036
  it('composes real providers into the project-list tabs and exactly one visible screen', async () => {
    const h = await mount()
    await h.reply(h.last('project.list'), listResult())
    const tablist = within(h.container).getByRole('tablist', { name: '動画の作業' })
    expect(within(tablist).getAllByRole('tab').map(tab => tab.textContent))
      .toEqual(['Project一覧', 'ジョブの状態の確認'])
    const projectWorkspace = h.element('.project-workspace')
    const jobPanel = h.element<HTMLElement>('.job-panel')
    const listenerCount = h.listeners.size
    const initialRequests = [...h.requests]

    for (const step of projectListSteps) {
      await h.selectStep(step)
      expectScreen(h, step)
      expect(h.element('.project-workspace')).toBe(projectWorkspace)
      expect(h.element('.job-panel')).toBe(jobPanel)
      // Its parent controls visibility; the one monitoring component stays active.
      expect(jobPanel.hidden).toBe(false)
      expectNoRawIds(h)
    }
    expect(h.requests).toEqual(initialRequests)
    expect(h.listeners.size).toBe(listenerCount)
    expect(h.project().state.draft).toBeNull()
    expect(h.app().state.jobMonitor.trackedJobId).toBeNull()
    expect(h.container.querySelector('video[src], audio[src], img[src]')).toBeNull()
    const ids = [...h.container.querySelectorAll('[id]')].map(element => element.id)
    expect(new Set(ids).size).toBe(ids.length)
    expect(h.element<HTMLAnchorElement>('.skip-link').getAttribute('href')).toBe('#project-content')
    expect(h.element<HTMLElement>('#project-content').tabIndex).toBe(-1)
    expect(h.element('#app-title').textContent).toBe('Movie Creator')
  })

  it('preserves manual tab activation, links, focus and the visible panel entry stop', async () => {
    const h = await mount()
    await openUnfinalizedDraft(h)
    const user = userEvent.setup({ document })
    await act(async () => { h.tab('source').focus(); await user.keyboard('{ArrowRight}') })
    expect(document.activeElement).toBe(h.tab('processing'))
    expectScreen(h, 'source')
    await act(async () => { await user.keyboard('{Enter}') })
    expectScreen(h, 'processing')
    expect(document.activeElement).toBe(h.tab('processing'))
    await act(async () => { await user.tab() })
    expect(document.activeElement).toBe(h.element('#workflow-panel-processing'))
    await act(async () => { await user.tab({ shift: true }); await user.keyboard('{End} ') })
    expectScreen(h, 'results')
    expect(document.activeElement).toBe(h.tab('results'))
    expect(h.requests.map(request => request.method)).toEqual(['project.list', 'project.createDraft'])
  })

  it.each([false, true])('opens global settings without a created project exactly once and restores tool and setting selection (StrictMode=%s)', async strict => {
    const h = await mount(strict)
    await openUnfinalizedDraft(h)
    expect(h.requests.filter(request => request.method.startsWith('settings.'))).toHaveLength(0)
    await h.selectStep('results')
    await h.selectTool('transcript')
    const transcript = h.element('.transcript-panel')
    const opener = h.element<HTMLButtonElement>('#workflow-open-settings')
    await act(async () => { opener.focus(); opener.click() })
    expectScreen(h, 'settings')
    expect(h.last('settings.global.read').params).toEqual({})
    await h.reply(h.last('settings.global.read'), { status: 'available', snapshot: globalSnapshot })
    expect(h.button('次に作成するJob').disabled).toBe(true)
    // FR-043 AC-018
    expect(h.element('.settings-scope-note').textContent).toContain('「次に作成するJob」は、「編集を開始」でProjectを作成すると選べます。')
    await h.click('短尺候補数')
    const setting = h.element<HTMLInputElement>('.setting-inspector input:checked')
    expect(setting.value).toBe('3')
    await h.click('設定を閉じて戻る')
    expectScreen(h, 'results')
    expect(document.activeElement).toBe(opener)
    expect(h.tool('transcript').getAttribute('aria-pressed')).toBe('true')
    expect(h.element('.transcript-panel')).toBe(transcript)
    await h.click('設定')
    expect(h.element('.setting-inspector input:checked')).toBe(setting)
    expect(setting.value).toBe('3')
    expect(h.requests.filter(request => request.method === 'settings.global.read')).toHaveLength(1)
    expect(h.settings().state.projectId).toBeNull()
    expect(h.requests.filter(request => request.method === 'settings.read')).toHaveLength(0)
    expectNoRawIds(h)
  })

  it('does not let hidden tabs, tools or the header bypass dirty settings confirmation', async () => {
    const h = await mount()
    await openUnfinalizedDraft(h)
    await h.selectStep('results')
    await h.selectTool('compare')
    await h.click('設定')
    await h.reply(h.last('settings.global.read'), { status: 'available', snapshot: globalSnapshot })
    await h.change('.setting-inspector textarea', '保持する設定の下書き')
    const drafts = h.settings().state.draftValues
    await act(async () => {
      // Programmatic clicks deliberately bypass DOM visibility, not the shell guard.
      fireEvent.click(h.tab('source'))
      fireEvent.click(h.tool('timeline'))
      fireEvent.click(h.element('.workflow-header-actions button'))
    })
    expectScreen(h, 'settings')
    expect(h.settings().state.draftValues).toBe(drafts)
    await h.click('設定を閉じて戻る')
    expect(within(h.container).getByRole('alertdialog').textContent).toContain('未保存の設定があります')
    await h.click('取消')
    expectScreen(h, 'settings')
    await h.click('設定を閉じて戻る')
    await h.click('未保存項目へ戻る')
    expect(document.activeElement).toBe(h.element('.setting-inspector textarea'))
    expect(h.settings().state.draftValues).toBe(drafts)
    // FR-062: the Workspace folder section reads its own location once; it is not part of the settings workspace state.
    expect(h.requests.filter(request => request.method.startsWith('settings.') && !request.method.startsWith('settings.workspace.')).map(request => request.method))
      .toEqual(['settings.global.read'])
    await h.click('設定を閉じて戻る')
    await h.click('変更を破棄して閉じる')
    expectScreen(h, 'results')
    expect(h.tool('compare').getAttribute('aria-pressed')).toBe('true')
    expect(h.settings().isDirty).toBe(false)
    expect(document.activeElement).toBe(h.element('#workflow-open-settings'))
  })

  it('keeps every editor mounted but exposes one tool and the same preview in results', async () => {
    const h = await mount()
    await openUnfinalizedDraft(h)
    await h.selectStep('results')
    const preview = h.element('.player-card')
    const modes = h.element('.preview-mode-card')
    const panels = new Map(workflowTools.map(tool => [tool, h.element(`#workflow-tool-panel-${tool}`)]))
    const initialRequests = [...h.requests]
    for (const tool of workflowTools) {
      await h.selectTool(tool)
      const visibleTools = [...h.container.querySelectorAll<HTMLElement>('.workflow-tool-panel')]
        .filter(element => element.closest('[hidden], [inert]') === null)
      expect(visibleTools).toEqual([panels.get(tool)])
      expect(h.element('.player-card')).toBe(preview)
      expect(preview.closest('[hidden], [inert]')).toBeNull()
      expect(h.element('.preview-mode-card')).toBe(modes)
      expectNoRawIds(h)
      for (const [name, panel] of panels) expect(h.element(`#workflow-tool-panel-${name}`)).toBe(panel)
    }
    await h.selectStep('source')
    expect(preview.closest('[hidden]')).not.toBeNull()
    await h.selectStep('results')
    expect(h.tool('evidence').getAttribute('aria-pressed')).toBe('true')
    expect(h.requests).toEqual(initialRequests)
    expect(h.container.querySelector('.thumbnail-actions, .thumbnail-template-list')).toBeNull()
    expect(h.element('.workflow-readonly').tagName).toBe('DIV')
  })

  it('keeps unsaved settings when returning and blocks switching the video until resolved', async () => {
    const h = await mount()
    await openFinalizedProject(h)
    await h.click('設定')
    await h.reply(h.last('settings.read'), { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    await h.change('.setting-inspector textarea', '保持する設定')
    const drafts = h.settings().state.draftValues
    await h.click('設定を閉じて戻る')
    await h.click('未保存のまま戻る')
    expectScreen(h, 'source')
    expect(h.settings().state.draftValues).toBe(drafts)
    expect(h.button('Project一覧へ戻る').disabled).toBe(true)
    await h.click('設定の編集へ戻る')
    expect(h.element<HTMLTextAreaElement>('.setting-inspector textarea').value).toBe('保持する設定')
    expect(h.requests.filter(request => request.method === 'settings.read')).toHaveLength(1)
    expect(h.requests.filter(request => /update|reset|settings\.file/.test(request.method))).toHaveLength(0)
  })

  it('loads hidden tools only on first display and leaves analysis refresh available', async () => {
    const h = await mount()
    await openFinalizedProject(h)
    expect(h.requests.filter(request => request.method === 'analysis.read' || request.method === 'timeline.read')).toHaveLength(0)
    await h.selectStep('results')
    await h.selectTool('analysis')
    await h.reply(h.last('analysis.read'), { status: 'unavailable', snapshot: null })
    expect(h.button('最新版を読み込む').disabled).toBe(false)
    await h.click('最新版を読み込む')
    await h.reply(h.last('analysis.read'), { status: 'unavailable', snapshot: null })
    await h.selectStep('source')
    await h.selectStep('results')
    expect(h.requests.filter(request => request.method === 'analysis.read')).toHaveLength(2)
    expect(h.requests.filter(request => request.method === 'timeline.read')).toHaveLength(0)
    await h.selectTool('timeline')
    expect(h.requests.filter(request => request.method === 'timeline.read')).toHaveLength(1)
  })

  it('integrates editable analysis drafts with the video-switch guard for UX-032', async () => {
    const h = await mount()
    await openFinalizedProject(h)
    await h.selectStep('results')
    await h.selectTool('analysis')
    const read = h.last('analysis.read')
    const available = createAnalysisResponseValue()
    const snapshot = available.result.snapshot
    if (snapshot === null) throw new Error('Expected an analysis snapshot.')
    await h.reply(read, available.result)

    const reason = h.element<HTMLInputElement>('.analysis-reason input')
    const title = h.element<HTMLInputElement>('.analysis-topic-field input[type="text"]')
    const summary = h.element<HTMLTextAreaElement>('.analysis-topic-field textarea')
    expect(reason.disabled).toBe(false)
    expect(title.disabled).toBe(false)
    expect(h.element('.analysis-workspace').textContent).toContain('作業中データ')
    expect(h.element('.analysis-workspace').textContent).toContain('自動保存されません')
    expect(h.container.textContent).not.toContain('解析は参照専用です')

    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(false)
    expect(h.container.querySelector('.workflow-dirty-notice')).toBeNull()
    await h.selectStep('results')
    expect(h.element('.analysis-reason input')).toBe(reason)

    await h.change('.analysis-reason input', '理由だけの未送信入力')
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(true)
    await h.click('Project一覧へ戻る')
    expect(h.project().state.draft?.projectId).toBe(projectId)
    await h.click('解析の編集へ戻る')
    expect(h.element('.analysis-reason input')).toBe(reason)
    expect(reason.value).toBe('理由だけの未送信入力')
    await act(async () => { h.element<HTMLButtonElement>('.analysis-local-inputs button').click() })
    expect(reason.value).toBe('')

    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(false)
    await h.selectStep('results')
    await h.change('.analysis-topic-field input[type="text"]', '送信するタイトル')
    await h.change('.analysis-topic-field textarea', '別項目の未送信要約')
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(true)
    await h.click('解析の編集へ戻る')
    expect(h.element('.analysis-topic-field input[type="text"]')).toBe(title)
    expect(h.element('.analysis-topic-field textarea')).toBe(summary)
    expect(title.value).toBe('送信するタイトル')
    expect(summary.value).toBe('別項目の未送信要約')

    await h.change('.analysis-reason input', 'タイトル変更の理由')
    await h.click('タイトルを保存')
    const update = h.last('analysis.updateTopic')
    expect(update.params).toMatchObject({
      projectId,
      topicId: analysisTopicId,
      update: { field: 'title', text: '送信するタイトル' },
      reason: 'タイトル変更の理由',
    })
    await h.reply(update, {
      status: 'available',
      snapshot: acknowledgeAnalysisTitle(snapshot, '送信するタイトル', 'タイトル変更の理由'),
    })

    expect(title.value).toBe('送信するタイトル')
    expect(summary.value).toBe('別項目の未送信要約')
    expect(summary.readOnly).toBe(true)
    expect(reason.value).toBe('')
    expect(h.container.querySelectorAll('.analysis-staged-list li')).toHaveLength(1)
    await act(async () => { h.element<HTMLButtonElement>('[data-input-status="stale"] button').click() })
    expect(summary.value).toBe('運用上の判断を説明します。')
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(false)
    expect(h.container.textContent).toContain('未永続化')
    await h.selectStep('results')

    await h.change('.analysis-topic-field input[type="text"]', '失敗後も保持するタイトル')
    await h.change('.analysis-reason input', '失敗後も保持する理由')
    await h.click('タイトルを保存')
    const failedUpdate = h.last('analysis.updateTopic')
    await h.reply(failedUpdate, { status: 'conflict', snapshot: null })

    expect(h.element<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('conflict')
    expect(h.element<HTMLInputElement>('.analysis-reason input').value).toBe('失敗後も保持する理由')
    expect([...h.container.querySelectorAll<HTMLTextAreaElement>('.analysis-retained-inputs textarea')]
      .map(input => input.value)).toContain('失敗後も保持するタイトル')
    await act(async () => { h.element<HTMLButtonElement>('.analysis-retained-inputs button').click() })
    expect(h.container.querySelector('.analysis-retained-inputs')).toBeNull()
    expect(h.element<HTMLInputElement>('.analysis-reason input').value).toBe('失敗後も保持する理由')
    await act(async () => { h.element<HTMLButtonElement>('.analysis-local-inputs button').click() })
    expect(h.container.querySelector('.analysis-reason input')).toBeNull()
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(false)
    await h.click('Project一覧へ戻る')
    expect(h.project().state.draft).toBeNull()
    expect(h.requests.filter(request => request.method === 'analysis.read')).toHaveLength(1)
  })

  // FR-040 AC-013
  it('renames the open project from the source screen and shows the new name in the header', async () => {
    const h = await mount()
    await h.reply(h.last('project.list'), listResult())
    await h.click('新しいProjectを作成')
    const draft = sourceOnlyDraft(false)
    await h.reply(h.last('project.createDraft'), { draft })
    const input = within(h.container).getByLabelText<HTMLInputElement>('Project名（この画面と一覧に表示される名前）')
    await act(async () => { fireEvent.change(input, { target: { value: '  見出しの名前 ' } }) })
    await h.click('Project名を保存')

    expect(h.last('project.rename').params).toEqual({ draftId: draft.draftId, displayName: '見出しの名前' })
    await h.reply(h.last('project.rename'), { draft: { ...draft, displayName: '見出しの名前' } })
    expect(h.element('.workflow-project-name').textContent).toBe('見出しの名前')
    expect(h.project().state.draft?.contentName).toBe(draft.contentName)
  })

  it('uses the confirmed source-only draft for the name and plans without fabricating a job, version, preview or files', async () => {
    const h = await mount()
    await h.reply(h.last('project.list'), listResult())
    await h.click('新しいProjectを作成')
    expect(h.last('project.createDraft').params).toEqual({})
    const draft = sourceOnlyDraft(false)
    await h.reply(h.last('project.createDraft'), { draft })
    expect(h.project().state.draft?.projectId).toBeNull()
    expect(h.element('.workflow-project-name').textContent).toBe(draft.contentName)
    expect(h.container.querySelector('.wizard-progress')).toBeNull()
    const quickStart = h.element('.quick-start-panel')
    await h.selectStep('results')
    expect(h.element('.result-gallery').textContent).toContain('作成予定')
    // FR-052: the raw status stays in data-evaluation; the visible label is plain language.
    expect(h.element('[data-evaluation]').getAttribute('data-evaluation')).toBe('NOT EVALUATED')
    expect(h.element('[data-evaluation]').textContent).toBe('未評価')
    expect(h.container.querySelector('[data-result-state="generated"], [data-save-state="saved"]')).toBeNull()
    expect(h.container.querySelector('video[src], img[src]')).toBeNull()
    await h.selectStep('source')
    expect(h.element('.quick-start-panel')).toBe(quickStart)
    await h.click('編集を開始')
    await h.click('編集を開始')
    const finalize = h.last('project.finalizeDraft')
    expect(finalize.params).toMatchObject({ draftId, expectedRevision: draft.revision, input: completeInput() })
    expect(h.requests.filter(request => request.method === 'project.finalizeDraft')).toHaveLength(1)
    await h.selectStep('processing')
    await h.reply(finalize, { draft: { ...draft, revision: draft.revision + 1, isFinalized: true, projectId } })
    expectScreen(h, 'processing')
    expect(document.activeElement).toBe(h.tab('processing'))
    expect(h.project().state.draft?.projectId).toBe(projectId)
    expect(h.element('#workflow-panel-processing').textContent).toContain('Projectは作成済みですが、編集処理の開始は確認できていません')
    expect(h.last('pipeline.read').params).toEqual({ projectId })
    expect(h.requests.filter(request => request.method === 'pipeline.read')).toHaveLength(1)
    expect(h.app().state.jobMonitor.trackedJobId).toBeNull()
    expect(h.requests.filter(request => /^(job\.|player\.|thumbnail\.|review\.|delivery\.|comparison\.)/.test(request.method))).toHaveLength(0)
    expectNoRawIds(h)
    await h.click('設定')
    expect(h.last('settings.read').params).toEqual({ projectId })
    expect(h.requests.filter(request => request.method === 'settings.global.read')).toHaveLength(0)
  })

  // FR-042 AC-017
  it('moves to the ジョブの状態の確認 screen by itself after a successful 編集を開始, and stays put on failure', async () => {
    const h = await mount()
    await h.reply(h.last('project.list'), listResult())
    await h.click('新しいProjectを作成')
    const draft = sourceOnlyDraft(false)
    await h.reply(h.last('project.createDraft'), { draft })
    await h.click('編集を開始')
    expectScreen(h, 'source')
    const failed = h.last('project.finalizeDraft')
    await act(async () => { for (const listener of [...h.listeners]) listener({ data: { version: 1, requestId: failed.requestId, method: failed.method, error: { code: 'project.operation_failed', message: '失敗' } } }) })
    expectScreen(h, 'source')
    await h.click('編集を開始')
    await h.reply(h.last('project.finalizeDraft'), { draft: { ...draft, revision: draft.revision + 1, isFinalized: true, projectId } })
    expectScreen(h, 'processing')
    expect(document.activeElement).toBe(h.tab('processing'))
    await h.selectStep('source')
    expectScreen(h, 'source')
  })

  // NFR-UX-005 AC-129: the ジョブの状態の確認 screen's progress and Job list are separate arrangeable components
  it('arranges the 進捗 and Jobの履歴 components on the ジョブの状態の確認 screen', async () => {
    const h = await mount()
    await h.reply(h.last('project.list'), listResult())
    await h.click('新しいProjectを作成')
    const draft = sourceOnlyDraft(false)
    await h.reply(h.last('project.createDraft'), { draft })
    await h.click('編集を開始')
    await h.reply(h.last('project.finalizeDraft'), { draft: { ...draft, revision: draft.revision + 1, isFinalized: true, projectId } })
    expectScreen(h, 'processing')
    const panels = [...h.container.querySelectorAll('[data-panel-id="progress"], [data-panel-id="jobs"]')]
    expect(panels.map(panel => panel.getAttribute('aria-label'))).toEqual(['処理の進捗', 'Jobの履歴'])
    expect(within(h.container).getAllByRole('tab', { name: 'ジョブの状態の確認' })).toHaveLength(1)
  })

  // FR-043 AC-018
  it('lets a created project switch settings to 次に作成するJob and read that project’s settings', async () => {
    const h = await mount()
    await h.reply(h.last('project.list'), listResult())
    await h.click('新しいProjectを作成')
    const draft = sourceOnlyDraft(false)
    await h.reply(h.last('project.createDraft'), { draft })
    await h.click('編集を開始')
    await h.reply(h.last('project.finalizeDraft'), { draft: { ...draft, revision: draft.revision + 1, isFinalized: true, projectId } })
    await h.click('設定')
    expect(h.last('settings.read').params).toEqual({ projectId })
    await h.reply(h.last('settings.read'), { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    expect(h.button('次に作成するJob').disabled).toBe(false)
    expect(h.button('次に作成するJob').getAttribute('aria-pressed')).toBe('true')
    // FR-061.6: a created project edits only its next Job.
    expect(within(h.container).queryByRole('button', { name: '全体の初期値' })).toBeNull()
    await h.click('短尺候補数')
    await act(async () => { h.element<HTMLInputElement>('.setting-inspector input[value="1"]').click() })
    await h.click('保存')
    expect(h.last('settings.update').params).toMatchObject({ projectId, key: countKey, value: { kind: 'integer', integerValue: '1' } })
    expect(h.element('.settings-scope-note').textContent).toContain('編集中：次に作成するJob。')
  })

  it('does not let changed global defaults replace a fixed source-only summary', async () => {
    const h = await mount()
    await h.reply(h.last('project.list'), listResult())
    await h.click('新しいProjectを作成')
    await h.reply(h.last('project.createDraft'), { draft: sourceOnlyDraft(false) })
    await h.selectStep('results')
    const gallery = h.element('.result-gallery')
    const before = gallery.textContent
    await h.click('設定')
    await h.reply(h.last('settings.global.read'), { status: 'available', snapshot: globalSnapshot })
    await h.click('短尺候補数')
    await act(async () => { h.element<HTMLInputElement>('.setting-inspector input[value="1"]').click() })
    await h.click('保存')
    expect(h.last('settings.global.update').params).toMatchObject({ key: countKey, value: { kind: 'integer', integerValue: '1' } })
    await h.reply(h.last('settings.global.update'), { status: 'available', snapshot: {
      ...globalSnapshot, revision: 1, snapshotDigest: 'c'.repeat(64),
      values: globalSnapshot.values.map(item => item.key === countKey ? { key: countKey, value: { kind: 'integer', integerValue: '1' } } : item),
    } })
    await h.click('設定を閉じて戻る')
    expect(h.element('.result-gallery')).toBe(gallery)
    expect(gallery.textContent).toBe(before)
    expect(h.project().state.draft?.sourceOnly?.settingsSnapshotDigest).toBe('b'.repeat(64))
    expect(h.container.querySelector('[data-plan-state="stale"]')).toBeNull()
  })

  it('retains transcript text, selection and OCC across navigation and blocks project changes until save acknowledgement', async () => {
    const h = await mount()
    await openFinalizedProject(h)
    await h.selectStep('results')
    await h.selectTool('transcript')
    const read = h.last('transcript.read')
    await h.reply(read, createTranscriptResponseValue().result)
    await h.change('.transcript-reason input', '表記を確認')
    await h.click('ハブ を編集')
    await h.change('.token-editing input', 'Hub')
    const edit = h.element<HTMLInputElement>('.token-editing input')
    edit.setSelectionRange(1, 3)
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(true)
    await h.click('Project一覧へ戻る')
    expect(h.project().state.draft?.projectId).toBe(projectId)
    expect(h.element('.token-editing input')).toBe(edit)
    expect(edit.value).toBe('Hub')
    expect(edit.closest('[inert]')).not.toBeNull()
    await h.click('文字起こしの編集へ戻る')
    expectScreen(h, 'results')
    expect(h.tool('transcript').getAttribute('aria-pressed')).toBe('true')
    expect(h.element('.token-editing input')).toBe(edit)
    expect(edit.selectionStart).toBe(1)
    expect(edit.selectionEnd).toBe(3)
    await h.click('保存')
    const write = h.last('transcript.editToken')
    expect(write.params).toEqual({ projectId, revisionId, digest: revisionDigest, tokenId: timedTokenId, expectedText: 'ハブ', revisedText: 'Hub', reason: '表記を確認' })
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(true)
    await h.reply(write, editedTranscriptResult())
    expect(document.activeElement).toBe(h.tab('source'))
    expect(h.button('Project一覧へ戻る').disabled).toBe(false)
    expect(h.requests.filter(request => request.method === 'transcript.read')).toHaveLength(1)
    await h.selectStep('results')
    expect(h.element('.transcript-panel').textContent).toContain('文: Hub 未時刻')
    expect(h.container.querySelector('.token-editing')).toBeNull()
    await h.selectStep('source')
    await h.click('Project一覧へ戻る')
    expect(h.project().state.draft).toBeNull()
    expect(h.element('.workflow-project-name').textContent).toBe('元動画を選択してください')
    expect(h.container.querySelector('.result-gallery')).toBeNull()
    expect(h.requests.filter(request => /cancel|retry|finalizeDraft/.test(request.method))).toHaveLength(0)
  })

  it('preserves timeline selection and undo across settings and prevents project-switch loss', async () => {
    const h = await mount()
    await openFinalizedProject(h)
    const response = createTimelineReadResponseValue()
    response.result.snapshot.projectId = projectId
    await h.selectStep('results')
    await h.selectTool('timeline')
    await h.reply(h.last('timeline.read'), response.result)
    const canvas = h.element('.timeline-canvas')
    const first = h.element<HTMLButtonElement>('.timeline-element-list button')
    await act(async () => { first.click() })
    await h.click('Delete')
    expect(h.container.querySelectorAll('.timeline-element-list button')).toHaveLength(1)
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(true)
    await h.click('カットの編集へ戻る')
    await h.click('設定')
    await h.reply(h.last('settings.read'), { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    await h.click('設定を閉じて戻る')
    expect(h.tool('timeline').getAttribute('aria-pressed')).toBe('true')
    expect(h.element('.timeline-canvas')).toBe(canvas)
    expect(h.button('未送信操作を元に戻す').disabled).toBe(false)
    await h.click('未送信操作を元に戻す')
    expect(h.container.querySelectorAll('.timeline-element-list button')).toHaveLength(2)
    await h.click('やり直す')
    expect(h.container.querySelectorAll('.timeline-element-list button')).toHaveLength(1)
    await h.click('1 操作を適用')
    expect(h.last('timeline.apply').params).toMatchObject({ projectId, expectedRevision: 3, operations: [{ kind: 'delete', elementId: timelineSourceElementId }] })
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(true)
    expect(h.project().state.draft?.projectId).toBe(projectId)
    expect(h.requests.filter(request => request.method === 'timeline.read')).toHaveLength(1)
    expect(h.requests.filter(request => /cancel|retry|finalizeDraft/.test(request.method))).toHaveLength(0)
  })

  it.each(['processing', 'settings'] as const)('pauses hidden media without resetting time, unmounting or autoplay when visiting %s', async destination => {
    const h = await mount()
    await openUnfinalizedDraft(h)
    await h.selectStep('results')
    const preview = h.element('.player-card')
    // DOM-only media guard fixture: no fabricated player response, URL or real playback.
    const audio = document.createElement('audio')
    let paused = false
    Object.defineProperty(audio, 'paused', { configurable: true, get: () => paused })
    audio.currentTime = 23.5
    const pause = vi.spyOn(audio, 'pause').mockImplementation(() => { paused = true })
    const play = vi.spyOn(audio, 'play').mockResolvedValue()
    h.element('.workflow-preview').append(audio)
    if (destination === 'settings') await h.click('設定')
    else await h.selectStep(destination)
    expect(pause).toHaveBeenCalledOnce()
    expect(audio.currentTime).toBe(23.5)
    expect(audio.isConnected).toBe(true)
    expect(audio.closest('[hidden], [inert]')).not.toBeNull()
    paused = false
    await act(async () => { fireEvent.play(audio) })
    expect(pause).toHaveBeenCalledTimes(2)
    if (destination === 'settings') {
      await h.reply(h.last('settings.global.read'), { status: 'available', snapshot: globalSnapshot })
      await h.click('設定を閉じて戻る')
    } else await h.selectStep('results')
    expect(h.element('.player-card')).toBe(preview)
    expect(audio.currentTime).toBe(23.5)
    expect(play).not.toHaveBeenCalled()
    expect(audio.closest('[hidden], [inert]')).toBeNull()
  })

  it('does not clear another editor\'s dirty guard when one editor cancels its input', async () => {
    const h = await mount()
    await openFinalizedProject(h)
    const timeline = createTimelineReadResponseValue()
    timeline.result.snapshot.projectId = projectId
    await h.selectStep('results')
    await h.selectTool('timeline')
    await h.reply(h.last('timeline.read'), timeline.result)
    await act(async () => { h.element<HTMLButtonElement>('.timeline-element-list button').click() })
    await h.click('Delete')
    await h.selectTool('transcript')
    await h.reply(h.last('transcript.read'), createTranscriptResponseValue().result)
    await h.change('.transcript-reason input', '未保存の理由')
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(true)
    expect(h.button('カットの編集へ戻る')).toBeTruthy()
    await h.click('文字起こしの編集へ戻る')
    await h.click('未保存の入力を取り消す')
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(true)
    expect(within(h.container).queryByRole('button', { name: '文字起こしの編集へ戻る' })).toBeNull()
    await h.click('カットの編集へ戻る')
    await h.click('未送信操作を元に戻す')
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(false)
    expect(h.project().state.draft?.projectId).toBe(projectId)
    expect(h.requests.filter(request => /apply|editToken|cancel|retry/.test(request.method))).toHaveLength(0)
  })

  it('keeps errors and synthetic focus in hidden editors from taking focus from the active screen', async () => {
    const h = await mount()
    await openFinalizedProject(h)
    await h.selectStep('results')
    await h.selectTool('transcript')
    const read = h.last('transcript.read')
    await h.selectStep('processing')
    const focused = document.activeElement
    await h.reply(read, { status: 'unavailable', snapshot: null })
    expect(document.activeElement).toBe(focused)
    await act(async () => { h.element<HTMLElement>('#workflow-tool-panel-transcript button').focus() })
    expect(h.element('#workflow-tool-panel-transcript').contains(document.activeElement)).toBe(false)
    expectScreen(h, 'processing')
    expect(h.requests.filter(request => request.method === 'transcript.read')).toHaveLength(1)
  })

  it('scopes compact CSS to the workflow and keeps base CSS before feature imports', () => {
    const css = readFileSync(resolve(process.cwd(), 'src/features/workflow/workflow.css'), 'utf8')
    const main = readFileSync(resolve(process.cwd(), 'src/main.tsx'), 'utf8')
    expect(css).not.toMatch(/@import|https?:|#[\da-fA-F]{3,8}\b|rgba?\(|hsla?\(/)
    expect(css).toContain('var(--cp-text)')
    expect(css).toContain('minmax(0, 1fr)')
    expect(css).not.toMatch(/overflow\s*:\s*(?:hidden|clip)/)
    expect(css).toContain('overflow: auto')
    expect(css).toContain('overflow: visible')
    expect(main.indexOf("import './styles.css'")).toBeLessThan(main.indexOf("import App from './App'"))
    expect(main).toContain('<SettingsWorkspaceStateProvider>')
    expect(main).not.toContain('<SettingsStateProvider>')
    // CSS intent only. 1280x720 geometry / real WebView audio remain UX-041 checks.
  })
})

describe('WorkflowWorkspace UX-039B / UX-040B-D live connection (synthetic host only)', () => {
  it('automatically reads the finalized selection, monitors its root and reads player evidence using ingest, not draft/root IDs', async () => {
    const h = await mount(true)
    const snapshot = createPipelineSnapshot('Analyzing', projectId)
    await openFinalizedProject(h, snapshot)
    expect(h.requests.filter(request => request.method === 'pipeline.read')).toHaveLength(1)
    expect(h.last('jobs.getStatus').params).toEqual({ jobId: snapshot.rootJobId })
    expect(h.last('player.read').params).toEqual({ jobId: snapshot.ingestJobId })
    expect(h.app().state.jobMonitor.trackedJobId).toBe(snapshot.rootJobId)
    await h.reply(h.last('player.read'), { status: 'unavailable', snapshot: null })
    const requests = [...h.requests]
    await h.selectStep('processing')
    expect(h.element('[data-pipeline-phase]').textContent).toBe('解析中')
    // FR-042 AC-017
    const stages = [...h.container.querySelectorAll('.pipeline-stages li')].map(item => [item.getAttribute('data-stage-state'), item.textContent])
    expect(stages.map(([state]) => state)).toEqual(['done', 'done', 'current', 'pending', 'pending', 'pending', 'pending', 'pending'])
    expect(stages[2]![1]).toBe('解析中：実行中')
    expect(h.element('.pipeline-stage-summary').textContent).toBe('進み具合：3 / 8 段階（解析中）')
    expect(h.element('.pipeline-stages li[aria-current="step"]').textContent).toContain('解析中')
    await h.selectStep('results'); await h.selectStep('source')
    // Switching screens may add the read-only past-version list read, but must
    // not repeat or accept any other work.
    expect(h.requests.filter(request => request.method !== 'pipeline.versions')).toEqual(requests.filter(request => request.method !== 'pipeline.versions'))
    expect(h.requests.filter(request => request.method === 'pipeline.versions').length).toBeLessThanOrEqual(1)
    expect(h.requests.filter(request => /pipeline\.(cancel|retry|regenerate|restore)|jobs\.(cancel|retry)/.test(request.method))).toHaveLength(0)
    expect(h.container.querySelector('video[src*="source-only"], [data-result-state="generated"]')).toBeNull()
  })

  it('cancels and explicitly retries a failed/canceled execution with its revision and binds the newly acknowledged root', async () => {
    const h = await mount()
    const snapshot = { ...createPipelineSnapshot('Analyzing', projectId), revision: 3 }
    await openFinalizedProject(h, snapshot)
    await h.selectStep('processing')
    const cancel = h.button('自動編集を取消')
    await act(async () => { cancel.click(); cancel.click() })
    const canceled = h.last('pipeline.cancel')
    expect(canceled.params).toEqual({ projectId, executionId: snapshot.executionId, expectedRevision: 3 })
    expect(h.requests.filter(request => request.method === 'pipeline.cancel')).toHaveLength(1)
    await h.reply(canceled, { status: 'available', snapshot: { ...snapshot, phase: 'Canceled', revision: 4 } })
    await h.click('自動編集を再試行')
    const retry = h.last('pipeline.retry')
    expect(retry.params).toEqual({ projectId, executionId: snapshot.executionId, expectedRevision: 4 })
    await h.reply(retry, { status: 'available', snapshot: { ...createPipelineSnapshot('Queued', projectId), executionId: fixtureId(90), rootJobId: fixtureId(91) } })
    expect(h.app().state.jobMonitor.trackedJobId).toBe(fixtureId(91))
    expect(h.last('jobs.getStatus').params).toEqual({ jobId: fixtureId(91) })
    expect(h.requests.some(request => request.method === 'jobs.retry' || request.method === 'jobs.cancel')).toBe(false)
  })

  it('shows actual videos/evaluation/delivery and regenerates only after explicit confirmation against saved settings', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    await openFinalizedProject(h, completed)
    await h.selectStep('results')
    expect(h.element<HTMLVideoElement>('[data-preview-kind="generated"] video').controls).toBe(true)
    expect(h.element<HTMLVideoElement>('[data-preview-kind="generated"] video').src).toContain(`/source-only/${projectId}/${completed.executionId}/`)
    // FR-052: the delivered artifact is described as saved in plain language.
    expect(h.element('[data-artifact-delivered]').getAttribute('data-artifact-delivered')).toBe('true')
    expect(h.element('[data-artifact-delivered]').textContent).toContain('保存済み（ファイルを確認済み）')
    expect(h.button('保存済み設定で再生成').disabled).toBe(true)
    await h.click('設定')
    await h.reply(h.last('settings.read'), { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    await h.change('.setting-inspector textarea', '保存後に生成するタイトル')
    await h.click('保存')
    await h.reply(h.last('settings.update'), { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 8, snapshotDigest: 'c'.repeat(64),
      values: globalSnapshot.values.map(item => item.key === titleKey ? { key: titleKey, value: { kind: 'text', textValue: '保存後に生成するタイトル' } } : item) } })
    expect(h.requests.filter(request => request.method === 'pipeline.regenerate')).toHaveLength(0)
    await h.click('設定を閉じて戻る')
    await h.click('保存済み設定で再生成')
    expect(within(h.container).getByRole('alertdialog').textContent).toContain('以前の成果物は削除しません')
    await h.click('取消')
    expect(h.requests.filter(request => request.method === 'pipeline.regenerate')).toHaveLength(0)
    await h.click('保存済み設定で再生成')
    const confirm = h.button('確認して再生成')
    await act(async () => { confirm.click(); confirm.click() })
    const request = h.last('pipeline.regenerate')
    expect(request.params).toEqual({ projectId, expectedVersionId: completed.versionId, expectedVersionDigest: completed.versionDigest,
      expectedSettingsRevision: 8, expectedSettingsSnapshotDigest: 'c'.repeat(64) })
    expect(h.requests.filter(request => request.method === 'pipeline.regenerate')).toHaveLength(1)
    expect(h.element('[data-artifact-id]').getAttribute('data-artifact-id')).toBe(completed.result!.artifacts[0]!.artifact.artifactId)
    await h.reply(request, { status: 'available', snapshot: { ...createPipelineSnapshot('Queued', projectId), executionId: fixtureId(90), rootJobId: fixtureId(91),
      settingsSnapshotDigest: 'c'.repeat(64), parentVersionId: completed.versionId, parentDigest: completed.versionDigest, previousResult: completed.result } })
    expect(h.element('[data-result-source]').getAttribute('data-result-source')).toBe('previous')
    expect(h.element('[data-result-source]').textContent).toContain('前回の成果物')
    expect(h.element<HTMLVideoElement>('[data-preview-kind="generated"] video').src).toContain(`/${completed.executionId}/`)
    expect(h.button('保存済み設定で再生成').disabled).toBe(true)
  })

  // FR-063 AC-038.4
  it('organises the results by Job and widens the screen to compare two Jobs', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    await openFinalizedProject(h, completed)
    await h.selectStep('results')
    const job = (versionId: string, executionId: string, isCurrent: boolean, speed: string, hour: string, artifact: number) => ({
      versionId, versionDigest: (isCurrent ? 'a' : 'b').repeat(64), parentVersionId: null, executionId, phase: 'Completed', hasResult: true, isCurrent,
      createdAt: `2026-10-07T${hour}:00:00.0000000+00:00`,
      settings: [{ key: 'subtitles.readingSpeedMaximum', value: { kind: 'integer', integerValue: speed } }],
      outputs: [{ role: 'LandscapeBurnIn', artifactId: fixtureId(artifact), contentDigest: String(artifact % 10).repeat(64), mediaType: 'video/mp4' }],
    })
    await h.reply(h.last('pipeline.versions'), { status: 'available', workingCopyRevision: 1, versions: [
      job(completed.versionId!, completed.executionId, true, '9', '13', 71), job(fixtureId(70), fixtureId(73), false, '7', '12', 72),
    ] })
    expect(h.element('#workflow-tool-overview').textContent).toBe('Jobの仕上がり')
    expect(within(h.container).getByRole('heading', { name: 'Jobごとの仕上がり（2件）' })).toBeTruthy()
    // The current Job keeps its detailed results and the generated preview.
    expect(h.element('[data-artifact-delivered]').textContent).toContain('保存済み（ファイルを確認済み）')
    expect(h.element<HTMLVideoElement>('[data-preview-kind="generated"] video').src).toContain(`/${completed.executionId}/`)

    const rail = within(h.container).getByRole('list', { name: 'Jobの一覧' })
    await act(async () => { within(rail).getByRole('button', { name: /Job 1/ }).click() })
    expect(within(h.container).getByRole('list', { name: 'Job 1の動画・画像' }).querySelector('video')!.getAttribute('src'))
      .toBe(`https://player-media.appassets.example/source-only/${projectId}/${fixtureId(73)}/${fixtureId(72)}/${'2'.repeat(64)}`)
    expect(h.container.querySelector('[data-artifact-delivered]')).toBeNull()

    await h.click('Jobを比較')
    expect(h.element('.workflow-results-content').classList.contains('is-comparing')).toBe(true)
    expect(h.element<HTMLElement>('.workflow-preview').hidden).toBe(true)
    const row = within(h.container).getByRole('table', { name: '設定の比較' }).querySelector('[data-setting-key="subtitles.readingSpeedMaximum"]')!
    expect([...row.querySelectorAll('td')].map(cell => cell.textContent)).toEqual(['7', '9'])
    expect(h.requests.filter(request => request.method === 'pipeline.regenerate' || request.method === 'pipeline.restore')).toHaveLength(0)

    await h.click('Jobごとに見る')
    expect(h.element('.workflow-results-content').classList.contains('is-comparing')).toBe(false)
    expect(h.element<HTMLElement>('.workflow-preview').hidden).toBe(false)
  })

  it('loads a warning setting in the correct project/scope and returns focus to the exact warning without clearing drafts', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    await openFinalizedProject(h, completed)
    await h.selectStep('results')
    const warningId = completed.result!.artifacts[0]!.warnings[0]!.id
    await h.click('設定へ')
    expectScreen(h, 'results') // wait for actual definitions; do not guess the group
    const read = h.last('settings.read')
    expect(read.params).toEqual({ projectId })
    await h.reply(read, { status: 'available', snapshot: { ...globalSnapshot, projectId: fixtureId(999), revision: 7 } })
    expectScreen(h, 'results')
    await h.reply(read, { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    expectScreen(h, 'settings')
    expect(h.settings().state.projectId).toBe(projectId)
    expect(document.activeElement).toBe(h.element('.setting-inspector textarea'))
    await h.change('.setting-inspector textarea', '警告からの未保存編集を保持')
    await h.click('設定を閉じて戻る')
    await h.click('未保存のまま戻る')
    expectScreen(h, 'results')
    expect(h.settings().isDirty).toBe(true)
    const restored = h.element(`[data-warning-id="${warningId}"]`)
    expect(restored.getAttribute('aria-current')).toBe('true')
    expect(document.activeElement).toBe(restored)
    expect(h.requests.filter(request => request.method === 'pipeline.regenerate')).toHaveLength(0)
  })

  it('seeks the generated output at the exact warning time after metadata and ignores that seek after leaving', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Preview', timeBasis: 'Output', time: { value: 2, rate: 1 }, settingKey: null })
    await openFinalizedProject(h, completed)
    await h.selectStep('results')
    const video = h.element<HTMLVideoElement>('[data-preview-kind="generated"] video')
    Object.defineProperty(video, 'duration', { configurable: true, value: 10 })
    await h.click('該当時刻へ')
    await act(async () => { fireEvent.loadedMetadata(video) })
    expect(video.currentTime).toBe(2)
    expect(document.activeElement).not.toBe(video)
    expect(h.element('.workflow-dirty-notice').textContent).toContain('警告の対象を確認しています')
    await act(async () => { fireEvent.seeked(video) })
    expect(document.activeElement).toBe(video)
    expect(h.element('.workflow-dirty-notice').textContent).toContain('警告の対象へ移動しました')
    await h.click('元の警告へ戻る')
    expect(document.activeElement?.getAttribute('data-warning-id')).toBe(completed.result!.artifacts[0]!.warnings[0]!.id)
    await h.click('該当時刻へ')
    await h.selectStep('source')
    video.currentTime = 0
    await act(async () => { fireEvent.loadedMetadata(video) })
    expect(video.currentTime).toBe(0)
    expect(document.activeElement).toBe(h.tab('source'))
    expect(h.requests.filter(request => request.method === 'player.seek')).toHaveLength(0)
  })

  it('accepts missing-file invalid diagnostics, withdraws current media and disables regeneration until a verified read', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    await openFinalizedProject(h, completed)
    await h.click('設定')
    await h.reply(h.last('settings.read'), { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    await h.click('設定を閉じて戻る'); await h.selectStep('results')
    expect(h.button('保存済み設定で再生成').disabled).toBe(false)
    const oldVideo = h.element<HTMLVideoElement>('[data-preview-kind="generated"] video')
    await h.selectStep('processing'); await h.click('最新状態を取得')
    await h.reply(h.last('pipeline.read'), { status: 'invalid', snapshot: { ...completed, resultStatus: 'invalid' } })
    expect(h.element('.job-panel').textContent).toContain('処理状態を利用できません: invalid')
    expect(h.button('最新状態を取得').disabled).toBe(false)
    await act(async () => { await vi.advanceTimersByTimeAsync(30_000) })
    expect(h.element('.job-panel').textContent).not.toContain('応答が時間内に届きませんでした')
    await h.selectStep('results')
    expect(h.element('#workflow-panel-results').textContent).toContain('保持された参照は診断用')
    expect(h.container.querySelector('video[src], img[src]')).toBeNull()
    expect(oldVideo.isConnected).toBe(false)
    expect(h.button('保存済み設定で再生成').disabled).toBe(true)
    await h.click('保存済み設定で再生成')
    expect(h.requests.filter(request => request.method === 'pipeline.regenerate')).toHaveLength(0)
    await h.selectStep('processing'); await h.click('最新状態を取得')
    await h.reply(h.last('pipeline.read'), { status: 'available', snapshot: completed })
    await h.selectStep('results')
    expect(h.element<HTMLVideoElement>('[data-preview-kind="generated"] video').src).toBe(oldVideo.src)
    expect(h.element('[data-preview-kind="generated"] video')).not.toBe(oldVideo)
    expect(h.button('保存済み設定で再生成').disabled).toBe(false)
  })

  it('fails and clears a timed-out output warning association instead of reporting assignment as success', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Preview', timeBasis: 'Output', time: { value: 2, rate: 1 }, settingKey: null })
    await openFinalizedProject(h, completed); await h.selectStep('results')
    const video = h.element<HTMLVideoElement>('[data-preview-kind="generated"] video')
    Object.defineProperty(video, 'duration', { configurable: true, value: 10 })
    await h.click('該当時刻へ')
    await act(async () => { fireEvent.loadedMetadata(video) })
    expect(video.currentTime).toBe(2)
    expect(document.activeElement).not.toBe(video)
    await act(async () => { await vi.advanceTimersByTimeAsync(30_000) })
    expect(h.element('.workflow-dirty-notice').textContent).toContain('時間内')
    expect(within(h.container).queryByRole('button', { name: '元の警告へ戻る' })).toBeNull()
    expect(h.element('[data-warning-id]').getAttribute('aria-current')).not.toBe('true')
    await act(async () => { fireEvent.seeked(video) })
    expect(document.activeElement).not.toBe(video)
    expect(h.element('.workflow-dirty-notice').textContent).not.toContain('警告の対象へ移動しました')
    expect(h.requests.filter(request => request.method === 'player.seek')).toHaveLength(0)
  })

  it('reloads analysis/transcript on registration milestones, not poll revisions, and holds dirty input until discarded', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const h = await mount()
    const analyzing = createPipelineSnapshot('Analyzing', projectId)
    await openFinalizedProject(h, analyzing); await h.selectStep('results')
    await h.selectTool('analysis')
    await h.reply(h.last('analysis.read'), { status: 'unavailable', snapshot: null })
    await h.selectTool('transcript')
    await h.reply(h.last('transcript.read'), { status: 'unavailable', snapshot: null })
    const reads = (method: string) => h.requests.filter(request => request.method === method).length
    async function poll(next: PipelineSnapshot) {
      await h.selectStep('processing'); await h.click('最新状態を取得')
      await h.reply(h.last('pipeline.read'), { status: 'available', snapshot: next })
    }
    await poll({ ...analyzing, phase: 'Composing', revision: 1 })
    expect(reads('analysis.read')).toBe(1); expect(reads('transcript.read')).toBe(1)
    await h.selectStep('results')
    expect(reads('transcript.read')).toBe(2)
    await h.reply(h.last('transcript.read'), createTranscriptResponseValue().result)
    await h.selectTool('analysis')
    expect(reads('analysis.read')).toBe(2)
    await h.reply(h.last('analysis.read'), createAnalysisResponseValue().result)
    for (const revision of [2, 3, 4]) {
      await poll({ ...analyzing, phase: 'Composing', revision })
      await h.selectStep('results')
    }
    expect(reads('analysis.read')).toBe(2); expect(reads('transcript.read')).toBe(2)
    const title = h.element<HTMLInputElement>('.analysis-topic-field input[type="text"]')
    await h.change('.analysis-topic-field input[type="text"]', '登録更新中も保持する本文')
    title.setSelectionRange(1, 5)
    await poll({ ...analyzing, phase: 'Rendering', revision: 5, versionId: fixtureId(95), versionDigest: 'd'.repeat(64) })
    await h.selectStep('results')
    expect(reads('analysis.read')).toBe(2); expect(reads('transcript.read')).toBe(2)
    expect(h.element('.analysis-registration-notice').textContent).toContain('未送信の入力を保持')
    expect(h.element('.analysis-topic-field input[type="text"]')).toBe(title)
    expect(title.value).toBe('登録更新中も保持する本文')
    expect(title.selectionStart).toBe(1); expect(title.selectionEnd).toBe(5)
    await h.click('未送信の入力を取り消す')
    expect(reads('analysis.read')).toBe(3)
    await h.reply(h.last('analysis.read'), createAnalysisResponseValue().result)
    await h.selectTool('transcript')
    expect(reads('transcript.read')).toBe(3)
    await h.reply(h.last('transcript.read'), createTranscriptResponseValue().result)
    await h.selectTool('analysis'); await h.selectTool('transcript')
    expect(reads('analysis.read')).toBe(3); expect(reads('transcript.read')).toBe(3)
  })

  it('correlates source warning seeks with the ingest evidence and refuses a late seek after navigation', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Preview', timeBasis: 'Source', time: { value: 1, rate: 20 }, settingKey: null })
    await openFinalizedProject(h, completed)
    const parsed = parsePlayerReadResponse(createPlayerReadResponseValue())
    if (parsed?.result.status !== 'available') throw new Error('Invalid player test fixture.')
    const player = parsed.result.snapshot
    await h.reply(h.last('player.read'), { status: 'available', snapshot: { ...player, projectId,
      sourceAssetId: completed.result!.artifacts[0]!.source.artifactId,
      proxy: { ...player.proxy, mediaReference: player.proxy.mediaReference.replace(playerProjectId, projectId) },
      sprite: { ...player.sprite, imageReference: player.sprite.imageReference.replace(playerProjectId, projectId) } } })
    await h.selectStep('results'); await h.click('該当時刻へ')
    const seek = h.last('player.seek')
    expect(seek.params).toEqual({ jobId: completed.ingestJobId, digest: player.evidenceDigest, proxyFrameSequence: 2 })
    await h.reply(seek, createPlayerSeekResponseValue(seek.requestId, 2).result)
    const video = h.element<HTMLVideoElement>('[data-preview-kind="proxy"] video')
    expect(video.currentTime).toBe(1 / 12)
    expect(document.activeElement).toBe(h.element('.video-stage'))
    await h.click('元の警告へ戻る')
    await h.click('該当時刻へ')
    const late = h.last('player.seek')
    await h.selectStep('processing')
    video.currentTime = 0
    await h.reply(late, createPlayerSeekResponseValue(late.requestId, 2).result)
    expect(video.currentTime).toBe(0)
    expect(document.activeElement).toBe(h.tab('processing'))
  })

  it('compares the acknowledged parent and new version using all four exact identity fields', async () => {
    const h = await mount()
    const parent = createCompletedPipeline(projectId)
    await openFinalizedProject(h, parent)
    await h.click('設定')
    await h.reply(h.last('settings.read'), { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    await h.click('設定を閉じて戻る'); await h.selectStep('results')
    await h.click('保存済み設定で再生成'); await h.click('確認して再生成')
    const child = createCompletedPipeline(projectId)
    child.executionId = fixtureId(90); child.rootJobId = fixtureId(91)
    child.parentVersionId = parent.versionId; child.parentDigest = parent.versionDigest
    child.versionId = fixtureId(92); child.versionDigest = 'c'.repeat(64)
    child.previousResult = parent.result
    child.result!.binding = { ...child.result!.binding, executionId: child.executionId, rootJobId: child.rootJobId,
      versionId: child.versionId, versionDigest: child.versionDigest }
    child.result!.artifacts = [createPipelineArtifact(child.result!, 'LandscapeClean', 60)]
    await h.reply(h.last('pipeline.regenerate'), { status: 'available', snapshot: child })
    await h.selectTool('compare')
    expect(h.last('compare.read').params).toEqual({ leftVersionId: parent.versionId, leftVersionDigest: parent.versionDigest,
      rightVersionId: child.versionId, rightVersionDigest: child.versionDigest })
  })

  it('loads and focuses the bound timeline for an editor warning, keeps unsent edits on return and blocks regeneration', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind: 'Timeline', settingKey: null })
    await openFinalizedProject(h, completed)
    await h.selectStep('results')
    await h.click('編集へ')
    expect(h.tool('timeline').getAttribute('aria-pressed')).toBe('true')
    const timeline = createTimelineReadResponseValue()
    timeline.result.snapshot.projectId = projectId
    timeline.result.snapshot.baseVersionId = completed.versionId
    await h.reply(h.last('timeline.read'), timeline.result)
    const canvas = h.element('.timeline-canvas')
    expect(document.activeElement).toBe(canvas)
    await act(async () => { h.element<HTMLButtonElement>('.timeline-element-list button').click() })
    await h.click('Delete')
    await h.click('元の警告へ戻る')
    expect(h.element('.timeline-canvas')).toBe(canvas)
    expect(h.container.querySelectorAll('.timeline-element-list button')).toHaveLength(1)
    expect(h.button('保存済み設定で再生成').disabled).toBe(true)
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(true)
    await h.click('カットの編集へ戻る')
    expect(h.element('.timeline-canvas')).toBe(canvas)
    expect(h.button('未送信操作を元に戻す').disabled).toBe(false)
  })

  it.each(['Audio', 'Metadata'] as const)('does not guess a replacement for the unavailable %s editor', async editorKind => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind, settingKey: null })
    await openFinalizedProject(h, completed); await h.selectStep('results')
    expect(h.button('移動先未取得').disabled).toBe(true)
    expect(h.element('.result-gallery-warnings').textContent).toContain('別の道具で代用しません')
    expect(h.requests.filter(request => request.method === 'layout.read' || request.method === 'timeline.read')).toHaveLength(0)
  })

  it.each([false, true])('reads fresh exact 16:9 subtitles before focusing and restores the captured warning (StrictMode=%s)', async strict => {
    const h = await mount(strict)
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind: 'Subtitles', settingKey: null })
    await openFinalizedProject(h, completed); await h.selectStep('results')
    const artifact = completed.result!.artifacts[0]!
    const target = artifactLayoutTarget(artifact)
    const warning = h.element(`[data-warning-id="${artifact.warnings[0]!.id}"]`)
    expect(h.button('編集へ').disabled).toBe(false)
    expect(warning.closest('[hidden], [inert]')).toBeNull()
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(0)
    await h.click('編集へ')
    const first = h.last('layout.read')
    expect(first.params).toEqual({ projectId, evaluationTime: null, expectedVersionId: target.versionId, expectedVersionDigest: target.versionDigest })
    expect(h.container.textContent).not.toContain('警告の対象へ移動しました')
    const snapshot = layoutSnapshotFor(target, true)
    await h.reply(first, { status: 'available', snapshot })
    expect(h.layout().state.target).toEqual(target)
    expect(document.activeElement).toBe(h.element('#subtitle-editor-title'))
    expect(h.element<HTMLTextAreaElement>('[name="subtitle-text"]').value).toBe(snapshot.subtitles[0]!.text)
    expect(h.container.querySelector('.manual-layout-form, .keyframe-form')).toBeNull()
    expect(h.element('.workflow-dirty-notice').textContent).toContain('警告の対象へ移動しました')
    await h.click('元の警告へ戻る')
    expect(document.activeElement).toBe(warning)
    expect(warning.getAttribute('aria-current')).toBe('true')
    await h.click('編集へ')
    const second = h.last('layout.read')
    expect(second.requestId).not.toBe(first.requestId)
    expect(second.params).toEqual(first.params)
    expect(h.container.textContent).not.toContain('警告の対象へ移動しました')
    await h.reply(first, { status: 'available', snapshot }) // duplicate of the previous visit
    expect(h.layout().state.pending?.requestId).toBe(second.requestId)
    expect(h.container.textContent).not.toContain('警告の対象へ移動しました')
    await h.reply(second, { status: 'available', snapshot })
    expect(document.activeElement).toBe(h.element('#subtitle-editor-title'))
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(2)
  })

  it.each(['Subtitles', 'Layout'] as const)('loads each of two shorts for its own %s warning, never the pipeline master', async editorKind => {
    const h = await mount()
    const completed = twoShortsPipeline(editorKind)
    await openFinalizedProject(h, completed); await h.selectStep('results')
    for (const artifact of completed.result!.artifacts.filter(item => item.role === 'VerticalBurnIn')) {
      await act(async () => { h.element<HTMLButtonElement>(`[data-warning-id="${artifact.warnings[0]!.id}"] button`).click() })
      const request = h.last('layout.read')
      expect(request.params).toEqual({ projectId, evaluationTime: null, expectedVersionId: artifact.versionId, expectedVersionDigest: artifact.versionDigest })
      expect(request.params).not.toHaveProperty('expectedVersionId', completed.versionId)
      await h.reply(request, { status: 'available', snapshot: layoutSnapshotFor(artifactLayoutTarget(artifact)) })
      expect(document.activeElement).toBe(h.element(editorKind === 'Subtitles' ? '#subtitle-editor-title' : '#layout-title'))
      await h.click('元の警告へ戻る')
      expect(document.activeElement?.getAttribute('data-warning-id')).toBe(artifact.warnings[0]!.id)
    }
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(2)
  })

  it('opens the explicitly previewed artifact version/digest directly and reads each new selection once', async () => {
    const h = await mount(true)
    const completed = twoShortsPipeline()
    await openFinalizedProject(h, completed); await h.selectStep('results')
    for (const artifact of [...completed.result!.artifacts.filter(item => item.role === 'VerticalBurnIn')].reverse()) {
      await selectPipelineArtifact(h, artifact)
      await h.selectTool('layout')
      const request = h.last('layout.read')
      expect(request.params).toEqual({ projectId, evaluationTime: null, expectedVersionId: artifact.versionId,
        expectedVersionDigest: artifact.versionDigest, latestInBranch: true })
      await h.reply(request, { status: 'available', snapshot: layoutSnapshotFor(artifactLayoutTarget(artifact)) })
      // FR-052: the identity shows plain output information; the displayed version is checked via provider state.
      expectLayoutIdentity(h, artifact.versionId)
      await h.click('Jobの仕上がり')
    }
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(2)
  })

  it.each([false, true])('opens and reloads the durable head using the original artifact root without bouncing back (StrictMode=%s)', async strict => {
    const h = await mount(strict)
    const completed = twoShortsPipeline()
    const artifact = completed.result!.artifacts.find(item => item.role === 'VerticalBurnIn')!
    const target = artifactLayoutTarget(artifact)
    const original = { ...layoutSnapshotFor(target), revision: 0 }
    const first = subtitleChildSnapshot(original)
    const second = { ...subtitleChildSnapshot(first, fixtureId(994)), subtitles: first.subtitles.map((cue, index) => index !== 1 ? cue : {
      ...cue, compositionVersionId: fixtureId(994), revisionId: fixtureId(993), revisionDigest: '8'.repeat(64),
      revisionCount: cue.revisionCount + 1, style: 'caution' as const,
    }) }
    await openFinalizedProject(h, completed); await h.selectStep('results')
    await selectPipelineArtifact(h, artifact); await h.selectTool('layout')
    const initial = h.last('layout.read')
    expect(initial.params).toEqual({ projectId, evaluationTime: null, expectedVersionId: target.versionId,
      expectedVersionDigest: target.versionDigest, latestInBranch: true })
    await h.reply(initial, durableLayoutResult(first, target))
    expect(h.layout().state.target?.versionId).toBe(first.compositionVersionId)
    expect(h.layout().state.branch).toEqual({ root: target, sequence: 1 })
    // FR-052: the identity shows plain output information; the displayed version is checked via provider state.
    expectLayoutIdentity(h, first.compositionVersionId)
    expect(h.element('.layout-persistence-notice').textContent).toContain('字幕変更は保存済み／動画は変更後未生成')
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(1)
    await h.click('Jobの仕上がり'); await h.selectTool('layout')
    const reopen = h.last('layout.read')
    expect(reopen.requestId).not.toBe(initial.requestId)
    expect(reopen.params).toEqual(initial.params)
    await h.reply(reopen, durableLayoutResult(second, target))
    expect(h.layout().state.target?.versionId).toBe(second.compositionVersionId)
    // FR-052: the identity shows plain output information; the displayed version is checked via provider state.
    expectLayoutIdentity(h, second.compositionVersionId)
    expect(h.container.querySelector('.layout-target-guard')).toBeNull()
    await h.click('最新版を再読み込み')
    const reload = h.last('layout.read')
    expect(reload.params).toEqual(initial.params)
    await h.reply(reload, durableLayoutResult(second, target))
    expect(h.layout().state.target?.versionId).toBe(second.compositionVersionId)
    expect(h.layout().state.branch).toEqual({ root: target, sequence: 2 })
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(3)
  })

  it('reads the old artifact exactly for a warning even after its saved head has been opened', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind: 'Subtitles', settingKey: null })
    const target = artifactLayoutTarget(completed.result!.artifacts[0]!)
    const original = { ...layoutSnapshotFor(target, true), revision: 0 }
    const head = subtitleChildSnapshot(original)
    await openFinalizedProject(h, completed); await h.selectStep('results'); await h.selectTool('layout')
    await h.reply(h.last('layout.read'), durableLayoutResult(head, target))
    expect(h.layout().state.target?.versionId).toBe(head.compositionVersionId)
    await h.click('Jobの仕上がり'); await h.click('編集へ')
    const warningRead = h.last('layout.read')
    expect(warningRead.params).toEqual({ projectId, evaluationTime: null,
      expectedVersionId: target.versionId, expectedVersionDigest: target.versionDigest })
    await h.reply(warningRead, durableLayoutResult(original, target, head))
    expect(h.layout().state.target).toEqual(target)
    expect(h.layout().state.snapshot?.compositionVersionId).toBe(original.compositionVersionId)
    expect(h.layout().state.snapshot?.persistence).toBe('notPersisted')
    expect(h.layout().state.durableSubtitles?.headVersionId).toBe(head.compositionVersionId)
    expect(h.element('.layout-persistence-notice').textContent).not.toContain('字幕変更は保存済み／動画は変更後未生成')
    expect(document.activeElement).toBe(h.element('#subtitle-editor-title'))
    await h.click('元の警告へ戻る')
    expect(h.element('[data-warning-id]').getAttribute('aria-current')).toBe('true')
    expect(document.activeElement).toBe(h.element('[data-warning-id]'))
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(2)
  })

  it('keeps old result files and regenerates from the main rendered parent after a durable short subtitle edit', async () => {
    const h = await mount()
    const completed = twoShortsPipeline()
    const artifact = completed.result!.artifacts.find(item => item.role === 'VerticalBurnIn')!
    const target = artifactLayoutTarget(artifact)
    const original = { ...layoutSnapshotFor(target), revision: 0 }
    await openFinalizedProject(h, completed)
    await h.click('設定')
    await h.reply(h.last('settings.read'), { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    await h.click('設定を閉じて戻る'); await h.selectStep('results'); await selectPipelineArtifact(h, artifact)
    const oldVideo = h.element<HTMLVideoElement>('[data-preview-kind="generated"] video').src
    const gallery = h.element('.result-gallery')
    await h.selectTool('layout')
    await h.reply(h.last('layout.read'), durableLayoutResult(original, target))
    await h.change('[name="subtitle-reason"]', '短尺字幕だけを保存')
    await h.change('[name="subtitle-edit-kind"]', 'style'); await h.change('[name="subtitle-style"]', 'quote')
    await h.click('字幕 revision を作成')
    const head = subtitleChildSnapshot(original)
    await h.reply(h.last('layout.apply'), durableLayoutResult(head, target))
    expect(h.layout().state.target?.versionId).toBe(head.compositionVersionId)
    expect(h.layout().state.branch?.root).toEqual(target)
    expect(h.element('.layout-panel').getAttribute('data-layout-dirty')).toBe('false')
    await h.click('Jobの仕上がり')
    expect(h.element('.result-gallery')).toBe(gallery)
    expect(h.element<HTMLVideoElement>('[data-preview-kind="generated"] video').src).toBe(oldVideo)
    expect(h.element(`[data-artifact-id="${artifact.artifact.artifactId}"]`)).toBeTruthy()
    await h.click('保存済み設定で再生成'); await h.click('確認して再生成')
    const regenerate = h.last('pipeline.regenerate')
    expect(regenerate.params).toEqual({ projectId, expectedVersionId: completed.versionId, expectedVersionDigest: completed.versionDigest,
      expectedSettingsRevision: 7, expectedSettingsSnapshotDigest: globalSnapshot.snapshotDigest })
    expect(regenerate.params).not.toHaveProperty('expectedVersionId', head.compositionVersionId)
    expect(regenerate.params).not.toHaveProperty('expectedVersionId', target.versionId)
    await h.reply(regenerate, { status: 'available', snapshot: { ...createPipelineSnapshot('Queued', projectId),
      executionId: fixtureId(90), rootJobId: fixtureId(91), parentVersionId: completed.versionId,
      parentDigest: completed.versionDigest, previousResult: completed.result } })
    expect(h.element('[data-result-source]').getAttribute('data-result-source')).toBe('previous')
    expect(h.element(`[data-artifact-id="${artifact.artifact.artifactId}"]`)).toBeTruthy()
  }, 20_000)

  it.each(['unavailable', 'wrongVersion', 'wrongDigest', 'missingCues', 'subtitleOnlyLayout', 'noChange'] as const)(
    'fails closed without focusing or silently loading a default for %s', async failure => {
      const h = await mount()
      const completed = createCompletedPipeline(projectId)
      setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind: failure === 'subtitleOnlyLayout' ? 'Layout' : 'Subtitles', settingKey: null })
      await openFinalizedProject(h, completed); await h.selectStep('results'); await h.click('編集へ')
      const request = h.last('layout.read')
      const target = artifactLayoutTarget(completed.result!.artifacts[0]!)
      const matching = layoutSnapshotFor(target, true)
      const snapshot = failure === 'wrongVersion' ? layoutSnapshotFor({ ...target, versionId: fixtureId(999) }, true)
        : failure === 'wrongDigest' ? layoutSnapshotFor({ ...target, versionDigest: '9'.repeat(64) }, true)
          : failure === 'missingCues' ? { ...matching, subtitles: [], subtitleDisplays: [] } : matching
      await h.reply(request, failure === 'unavailable' || failure === 'noChange' ? { status: failure, snapshot: null } : { status: 'available', snapshot })
      expect(h.layout().state.pending).toBeNull()
      expect(h.container.textContent).not.toContain('警告の対象へ移動しました')
      expect(document.activeElement?.id).not.toBe('subtitle-editor-title')
      expect(document.activeElement?.id).not.toBe('layout-title')
      expect(within(h.container).queryByRole('button', { name: '元の警告へ戻る' })).toBeNull()
      expect(h.element('[data-warning-id]').getAttribute('aria-current')).not.toBe('true')
      expect(h.requests.filter(item => item.method === 'layout.read')).toHaveLength(1)
    },
  )

  it('keeps dirty old forms, caret and binding when another short is selected, and still sends accepted edits to the old version', async () => {
    const h = await mount()
    const completed = twoShortsPipeline()
    const [first, second] = completed.result!.artifacts.filter(item => item.role === 'VerticalBurnIn')
    await openFinalizedProject(h, completed); await h.selectStep('results')
    await selectPipelineArtifact(h, first!); await h.selectTool('layout')
    await h.reply(h.last('layout.read'), { status: 'available', snapshot: layoutSnapshotFor(artifactLayoutTarget(first!)) })
    const form = h.element('.cue-edit-form')
    const text = h.element<HTMLTextAreaElement>('[name="subtitle-text"]')
    const keyframe = h.element<HTMLInputElement>('#keyframe-0-0-destination-x')
    await h.change('[name="subtitle-text"]', '元の版の未送信本文を保持')
    await h.change('[name="subtitle-reason"]', '元の版への変更理由')
    await h.change('#keyframe-0-0-destination-x', '1/3')
    text.setSelectionRange(1, 5)
    await h.click('Jobの仕上がり'); await selectPipelineArtifact(h, second!)
    const warning = h.element<HTMLButtonElement>(`[data-warning-id="${second!.warnings[0]!.id}"] button`)
    expect(warning.disabled).toBe(true)
    await act(async () => { fireEvent.click(warning) })
    await h.selectTool('layout')
    expect(h.layout().state.target).toEqual(artifactLayoutTarget(first!))
    expect(h.element('.cue-edit-form')).toBe(form)
    expect(h.element('[name="subtitle-text"]')).toBe(text)
    expect(h.element('#keyframe-0-0-destination-x')).toBe(keyframe)
    expect(text.value).toBe('元の版の未送信本文を保持')
    expect(text.selectionStart).toBe(1); expect(text.selectionEnd).toBe(5)
    expect(keyframe.value).toBe('1/3')
    expect(h.element<HTMLElement>('.layout-workspace').hidden).toBe(false)
    expect(h.container.querySelector('.layout-target-guard')).toBeNull()
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(1)
    await h.click('字幕 revision を作成')
    expect(h.last('layout.apply').params).toMatchObject({ expectedVersionId: first!.versionId, expectedVersionDigest: first!.versionDigest,
      operation: { edit: { kind: 'text', text: '元の版の未送信本文を保持' } } })
    expect(h.layout().state.pending?.invalidated).not.toBe(true)
    await h.selectStep('source')
    expect(h.button('Project一覧へ戻る').disabled).toBe(true)
  })

  it('holds a pending read target without invalidation and requires a new explicit selection after it drains', async () => {
    const h = await mount()
    const completed = twoShortsPipeline()
    const [first, second] = completed.result!.artifacts.filter(item => item.role === 'VerticalBurnIn')
    await openFinalizedProject(h, completed); await h.selectStep('results')
    await selectPipelineArtifact(h, first!); await h.selectTool('layout')
    const oldRead = h.last('layout.read')
    await h.click('Jobの仕上がり'); await selectPipelineArtifact(h, second!); await h.selectTool('layout')
    expect(h.layout().state.pending?.requestId).toBe(oldRead.requestId)
    expect(h.layout().state.pending?.invalidated).not.toBe(true)
    const firstSnapshot = layoutSnapshotFor(artifactLayoutTarget(first!))
    await h.reply(oldRead, { status: 'available', snapshot: firstSnapshot })
    expect(h.layout().state.snapshot?.compositionVersionId).toBe(first!.versionId)
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(1)
    await h.selectTool('layout')
    const next = h.last('layout.read')
    expect(next.params).toMatchObject({ expectedVersionId: second!.versionId, expectedVersionDigest: second!.versionDigest })
    await h.reply(oldRead, { status: 'available', snapshot: firstSnapshot })
    expect(h.layout().state.pending?.requestId).toBe(next.requestId)
    await h.reply(next, { status: 'available', snapshot: layoutSnapshotFor(artifactLayoutTarget(second!)) })
    expect(h.layout().state.snapshot?.compositionVersionId).toBe(second!.versionId)
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(2)
  })

  it('does not let a late warning read change focus or restore a canceled warning association', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind: 'Subtitles', settingKey: null })
    await openFinalizedProject(h, completed); await h.selectStep('results'); await h.click('編集へ')
    const read = h.last('layout.read')
    await h.selectStep('processing')
    await h.reply(read, { status: 'available', snapshot: layoutSnapshotFor(artifactLayoutTarget(completed.result!.artifacts[0]!), true) })
    expect(document.activeElement).toBe(h.tab('processing'))
    expectScreen(h, 'processing')
    expect(h.container.textContent).not.toContain('警告の対象へ移動しました')
    expect(within(h.container).queryByRole('button', { name: '元の警告へ戻る' })).toBeNull()
    expect(h.element('[data-warning-id]').getAttribute('aria-current')).not.toBe('true')
    expect(h.layout().state.pending).toBeNull()
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(1)
  })

  it('adopts a partial subtitle child ACK without rebasing unsent text or matching the old warning on return', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind: 'Subtitles', settingKey: null })
    const target = artifactLayoutTarget(completed.result!.artifacts[0]!)
    const original = { ...layoutSnapshotFor(target, true), revision: 0 }
    await openFinalizedProject(h, completed); await h.selectStep('results'); await h.click('編集へ')
    await h.reply(h.last('layout.read'), durableLayoutResult(original, target))
    const form = h.element('.cue-edit-form')
    await h.change('[name="subtitle-text"]', 'style ACK 後も保持する未送信本文')
    await h.change('[name="subtitle-reason"]', '本文にも残す変更理由')
    await h.change('[name="subtitle-edit-kind"]', 'style')
    await h.change('[name="subtitle-style"]', 'quote')
    await h.click('字幕 revision を作成')
    const apply = h.last('layout.apply')
    expect(apply.params).toMatchObject({ expectedVersionId: target.versionId, expectedVersionDigest: target.versionDigest })
    const child = subtitleChildSnapshot(original)
    expect(child.subtitles).toHaveLength(2)
    expect(child.subtitles[1]).toEqual(original.subtitles[1])
    expect(child.terops).toEqual(original.terops)
    await h.reply(apply, durableLayoutResult(child, target))
    const childTarget = { projectId, versionId: child.compositionVersionId, versionDigest: child.compositionVersionDigest }
    expect(h.layout().state.target).toEqual(childTarget)
    expect(h.layout().state.branch?.root).toEqual(target)
    expect(h.element('.layout-persistence-notice').textContent).toContain('字幕変更は保存済み／動画は変更後未生成')
    expect(h.element('.cue-edit-form')).toBe(form)
    expect(h.container.querySelector('.layout-target-guard')).toBeNull()
    await h.change('[name="subtitle-edit-kind"]', 'text')
    expect(h.element<HTMLTextAreaElement>('[name="subtitle-text"]').value).toBe('style ACK 後も保持する未送信本文')
    await h.click('元の警告へ戻る')
    expect(h.element('[data-warning-id]').getAttribute('aria-current')).not.toBe('true')
    expect(h.element('.workflow-dirty-notice').textContent).toContain('選択復元を中止')
    await h.selectTool('layout')
    expect(h.element('.cue-edit-form')).toBe(form)
    expect(h.layout().state.target).toEqual(childTarget)
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(1)
    await h.click('字幕 revision を作成')
    expect(h.requests.filter(request => request.method === 'layout.apply')).toHaveLength(1)
    await act(async () => { h.element<HTMLElement>('.layout-draft-guard summary').click() })
    await h.click('この Cue の最新版を確認して入力を引き継ぐ')
    await h.click('字幕 revision を作成')
    expect(h.last('layout.apply').params).toMatchObject({ expectedVersionId: childTarget.versionId, expectedVersionDigest: childTarget.versionDigest,
      operation: { edit: { kind: 'text', text: 'style ACK 後も保持する未送信本文' } } })
  })

  it('accepts an in-flight child after leaving and reloads only its original short root when clean', async () => {
    const h = await mount()
    const completed = twoShortsPipeline()
    const [first, second] = completed.result!.artifacts.filter(item => item.role === 'VerticalBurnIn')
    await openFinalizedProject(h, completed); await h.selectStep('results')
    await selectPipelineArtifact(h, first!); await h.selectTool('layout')
    const target = artifactLayoutTarget(first!)
    const original = { ...layoutSnapshotFor(target), revision: 0 }
    await h.reply(h.last('layout.read'), durableLayoutResult(original, target))
    await h.change('[name="subtitle-reason"]', 'style の変更理由')
    await h.change('[name="subtitle-edit-kind"]', 'style'); await h.change('[name="subtitle-style"]', 'quote')
    await h.click('字幕 revision を作成')
    const apply = h.last('layout.apply')
    await h.click('Jobの仕上がり'); await selectPipelineArtifact(h, second!); await h.selectTool('layout')
    expect(h.layout().state.pending?.invalidated).not.toBe(true)
    await h.selectStep('processing')
    const child = subtitleChildSnapshot(original)
    await h.reply(apply, durableLayoutResult(child, target))
    expect(document.activeElement).toBe(h.tab('processing'))
    expect(h.layout().state.snapshot?.compositionVersionId).toBe(child.compositionVersionId)
    expect(h.element('.layout-panel').getAttribute('data-layout-dirty')).toBe('false')
    await h.selectStep('results')
    expect(h.last('layout.read').params).toMatchObject({ expectedVersionId: target.versionId,
      expectedVersionDigest: target.versionDigest, latestInBranch: true })
    await h.reply(h.last('layout.read'), durableLayoutResult(child, target))
    // FR-052: the identity shows plain output information; the displayed version is checked via provider state.
    expectLayoutIdentity(h, child.compositionVersionId)
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(2)
    await h.click('最新版を再読み込み')
    expect(h.last('layout.read').params).toMatchObject({ expectedVersionId: target.versionId,
      expectedVersionDigest: target.versionDigest, latestInBranch: true })
    await h.reply(h.last('layout.read'), durableLayoutResult(child, target))
    expect(h.layout().state.target?.versionId).toBe(child.compositionVersionId)
    expect(h.element('.layout-panel').getAttribute('data-layout-status')).toBe('available')
    // FR-052: the identity shows plain output information; the displayed version is checked via provider state.
    expectLayoutIdentity(h, child.compositionVersionId)
    expect(h.layout().state.snapshot?.subtitles[1]).toEqual(original.subtitles[1])
  })

  it.each([false, true])('accepts an initial exact read of a registered mixed-cue child and returns to its warning (StrictMode=%s)', async strict => {
    const h = await mount(strict)
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind: 'Subtitles', settingKey: null })
    const target = artifactLayoutTarget(completed.result!.artifacts[0]!)
    const parent = layoutSnapshotFor({ ...target, versionId: fixtureId(990) }, true)
    const child = subtitleChildSnapshot(parent, target.versionId)
    await openFinalizedProject(h, completed); await h.selectStep('results'); await h.click('編集へ')
    const read = h.last('layout.read')
    expect(read.params).toMatchObject({ expectedVersionId: target.versionId, expectedVersionDigest: target.versionDigest })
    await h.reply(read, { status: 'available', snapshot: child })
    expect(h.layout().state.target).toEqual(target)
    expect(h.layout().state.snapshot?.subtitles.map(cue => cue.compositionVersionId)).toEqual([target.versionId, parent.compositionVersionId])
    expect(document.activeElement).toBe(h.element('#subtitle-editor-title'))
    expect(h.element('.layout-panel').getAttribute('data-layout-status')).toBe('available')
    await h.click('元の警告へ戻る')
    expect(h.element('[data-warning-id]').getAttribute('aria-current')).toBe('true')
    expect(document.activeElement).toBe(h.element('[data-warning-id]'))
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(1)
  })

  it('adopts a terop child while retaining inherited subtitles and their unsent form', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind: 'Subtitles', settingKey: null })
    const target = artifactLayoutTarget(completed.result!.artifacts[0]!)
    const original = layoutSnapshotFor(target, true)
    await openFinalizedProject(h, completed); await h.selectStep('results'); await h.click('編集へ')
    await h.reply(h.last('layout.read'), { status: 'available', snapshot: original })
    const subtitleForm = h.element('.cue-edit-form')
    const text = h.element<HTMLTextAreaElement>('[name="subtitle-text"]')
    await h.change('[name="subtitle-text"]', 'テロップ更新後も保持する字幕下書き')
    text.setSelectionRange(1, 5)
    await h.change('[name="terop-reason"]', 'テロップの style だけを変更')
    await h.change('[name="terop-edit-kind"]', 'style'); await h.change('[name="terop-style"]', 'quote')
    await h.click('テロップ revision を作成')
    const request = h.last('layout.apply')
    expect(request.params).toMatchObject({ expectedVersionId: target.versionId, expectedVersionDigest: target.versionDigest,
      operation: { kind: 'editTerop', cueId: original.terops[0]!.cueId, edit: { kind: 'style', style: 'quote' } } })
    const child = teropChildSnapshot(original)
    await h.reply(request, { status: 'available', snapshot: child })
    expect(h.layout().state.target).toEqual({ projectId, versionId: child.compositionVersionId, versionDigest: target.versionDigest })
    expect(h.layout().state.snapshot?.subtitles).toEqual(original.subtitles)
    expect(h.element('.cue-edit-form')).toBe(subtitleForm)
    expect(h.element('[name="subtitle-text"]')).toBe(text)
    expect(text.value).toBe('テロップ更新後も保持する字幕下書き')
    expect(text.selectionStart).toBe(1); expect(text.selectionEnd).toBe(5)
    expect(h.container.querySelector('.layout-target-guard')).toBeNull()
    // FR-052: the identity shows plain output information; the displayed version is checked via provider state.
    expectLayoutIdentity(h, child.compositionVersionId)
    await h.click('最新版を再読み込み')
    expect(h.last('layout.read').params).toMatchObject({ expectedVersionId: child.compositionVersionId, expectedVersionDigest: target.versionDigest })
    await h.reply(h.last('layout.read'), { status: 'available', snapshot: child })
    expect(h.element('.layout-panel').getAttribute('data-layout-status')).toBe('available')
    expect(h.element('[name="subtitle-text"]')).toBe(text)
    expect(text.value).toBe('テロップ更新後も保持する字幕下書き')
  })

  it.each(['subtitle', 'terop'] as const)('rejects a %s child with a random composition digest and recovers only the accepted parent', async kind => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    const target = artifactLayoutTarget(completed.result!.artifacts[0]!)
    const original = layoutSnapshotFor(target, true)
    await openFinalizedProject(h, completed); await h.selectStep('results'); await h.selectTool('layout')
    await h.reply(h.last('layout.read'), { status: 'available', snapshot: original })
    await h.change(`[name="${kind}-reason"]`, '不正 digest を受け入れない')
    await h.change(`[name="${kind}-edit-kind"]`, 'style'); await h.change(`[name="${kind}-style"]`, 'quote')
    await h.click(kind === 'subtitle' ? '字幕 revision を作成' : 'テロップ revision を作成')
    const child = kind === 'subtitle' ? subtitleChildSnapshot(original) : teropChildSnapshot(original)
    await h.reply(h.last('layout.apply'), { status: 'available', snapshot: { ...child, compositionVersionDigest: '9'.repeat(64) } })
    expect(h.layout().state.target).toEqual(target)
    expect(h.layout().state.snapshot?.compositionVersionId).toBe(target.versionId)
    expect(h.layout().state.lastVersionChange).toBeNull()
    expect(h.element('.layout-panel').getAttribute('data-layout-status')).toBe('invalid')
    expect(h.element('.layout-identity').textContent).not.toContain(child.compositionVersionId)
    expect(h.element<HTMLInputElement>(`[name="${kind}-reason"]`).value).toBe('不正 digest を受け入れない')
    await h.click('最新版を再読み込み')
    expect(h.last('layout.read').params).toMatchObject({ expectedVersionId: target.versionId, expectedVersionDigest: target.versionDigest })
    await h.reply(h.last('layout.read'), { status: 'available', snapshot: original })
    expect(h.element('.layout-panel').getAttribute('data-layout-status')).toBe('available')
    expect(h.layout().state.target).toEqual(target)
  })

  it.each(['wrongVersion', 'wrongDigest', 'inheritedVersion', 'inheritedRevision', 'inheritedStyle'] as const)(
    'recovers an accepted mixed-cue child after a %s refresh without accepting stale evidence', async failure => {
      const h = await mount()
      const completed = createCompletedPipeline(projectId)
      setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind: 'Subtitles', settingKey: null })
      const target = artifactLayoutTarget(completed.result!.artifacts[0]!)
      const original = layoutSnapshotFor(target, true)
      await openFinalizedProject(h, completed); await h.selectStep('results'); await h.click('編集へ')
      await h.reply(h.last('layout.read'), { status: 'available', snapshot: original })
      await h.change('[name="subtitle-reason"]', '子版に進む')
      await h.change('[name="subtitle-edit-kind"]', 'style'); await h.change('[name="subtitle-style"]', 'quote')
      await h.click('字幕 revision を作成')
      const child = subtitleChildSnapshot(original)
      await h.reply(h.last('layout.apply'), { status: 'available', snapshot: child })
      const childTarget = { projectId, versionId: child.compositionVersionId, versionDigest: child.compositionVersionDigest }
      await h.click('最新版を再読み込み')
      const badRead = h.last('layout.read')
      const invalid = failure === 'wrongVersion' ? original
        : failure === 'wrongDigest' ? { ...child, compositionVersionDigest: '9'.repeat(64) }
          : { ...child, subtitles: child.subtitles.map((cue, index) => index !== 1 ? cue : {
            ...cue, ...(failure === 'inheritedVersion' ? { compositionVersionId: fixtureId(991) }
              : failure === 'inheritedRevision' ? { revisionId: fixtureId(992) } : { style: 'caution' as const }),
          }) }
      await h.reply(badRead, { status: 'available', snapshot: invalid })
      expect(h.element('.layout-panel').getAttribute('data-layout-status')).toBe('invalid')
      // FR-052: the identity shows plain output information; the displayed version is checked via provider state.
      expectLayoutIdentity(h, child.compositionVersionId)
      expect(h.button('最新版を再読み込み').disabled).toBe(false)
      await h.click('最新版を再読み込み')
      const recovery = h.last('layout.read')
      expect(recovery.requestId).not.toBe(badRead.requestId)
      expect(recovery.params).toMatchObject({ expectedVersionId: childTarget.versionId, expectedVersionDigest: childTarget.versionDigest })
      await h.reply(badRead, { status: 'available', snapshot: invalid })
      expect(h.layout().state.pending?.requestId).toBe(recovery.requestId)
      await h.reply(recovery, { status: 'available', snapshot: child })
      expect(h.layout().state.target).toEqual(childTarget)
      expect(h.layout().state.snapshot?.subtitles).toEqual(child.subtitles)
      expect(h.element('.layout-panel').getAttribute('data-layout-status')).toBe('available')
      // FR-052: the identity shows plain output information; the displayed version is checked via provider state.
      expectLayoutIdentity(h, child.compositionVersionId)
      expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(3)
    },
  )

  it('rejects changed inherited cues in a child ACK, retains the old input and permits an exact recovery read', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind: 'Subtitles', settingKey: null })
    const target = artifactLayoutTarget(completed.result!.artifacts[0]!)
    const original = layoutSnapshotFor(target, true)
    await openFinalizedProject(h, completed); await h.selectStep('results'); await h.click('編集へ')
    await h.reply(h.last('layout.read'), { status: 'available', snapshot: original })
    await h.change('[name="subtitle-reason"]', '一つだけ変更する要求')
    await h.change('[name="subtitle-edit-kind"]', 'style'); await h.change('[name="subtitle-style"]', 'quote')
    await h.click('字幕 revision を作成')
    const child = subtitleChildSnapshot(original)
    await h.reply(h.last('layout.apply'), { status: 'available', snapshot: {
      ...child, subtitles: child.subtitles.map(cue => ({ ...cue, compositionVersionId: child.compositionVersionId })),
    } })
    expect(h.layout().state.target).toEqual(target)
    expect(h.element('.layout-panel').getAttribute('data-layout-status')).toBe('invalid')
    // FR-052: the identity shows plain output information; the displayed version is checked via provider state.
    expectLayoutIdentity(h, target.versionId)
    expect(h.element('.layout-identity').textContent).not.toContain(child.compositionVersionId)
    expect(h.container.textContent).not.toContain('字幕・テロップの編集で子版へ進みました')
    expect(h.element<HTMLInputElement>('[name="subtitle-reason"]').value).toBe('一つだけ変更する要求')
    await h.click('字幕 revision を作成')
    expect(h.requests.filter(request => request.method === 'layout.apply')).toHaveLength(1)
    expect(h.button('最新版を再読み込み').disabled).toBe(false)
    await h.click('最新版を再読み込み')
    expect(h.last('layout.read').params).toMatchObject({ expectedVersionId: target.versionId, expectedVersionDigest: target.versionDigest })
    await h.reply(h.last('layout.read'), { status: 'available', snapshot: original })
    expect(h.element('.layout-panel').getAttribute('data-layout-status')).toBe('available')
    expect(h.element<HTMLInputElement>('[name="subtitle-reason"]').value).toBe('一つだけ変更する要求')
  })

  it('does not adopt an older child ACK into a newly captured warning visit', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    setPipelineWarningTarget(completed.result!, { kind: 'Editor', editorKind: 'Subtitles', settingKey: null })
    const target = artifactLayoutTarget(completed.result!.artifacts[0]!)
    const original = layoutSnapshotFor(target, true)
    await openFinalizedProject(h, completed); await h.selectStep('results'); await h.click('編集へ')
    await h.reply(h.last('layout.read'), { status: 'available', snapshot: original })
    await h.change('[name="subtitle-reason"]', 'style の変更理由')
    await h.change('[name="subtitle-edit-kind"]', 'style'); await h.change('[name="subtitle-style"]', 'quote')
    await h.click('字幕 revision を作成')
    await h.reply(h.last('layout.apply'), { status: 'available', snapshot: subtitleChildSnapshot(original) })
    await h.click('元の警告へ戻る')
    expect(h.element('[data-warning-id]').getAttribute('aria-current')).not.toBe('true')
    await h.click('編集へ')
    const read = h.last('layout.read')
    expect(read.params).toMatchObject({ expectedVersionId: target.versionId, expectedVersionDigest: target.versionDigest })
    expect(h.container.textContent).not.toContain('警告の対象へ移動しました')
    await h.reply(read, { status: 'available', snapshot: original })
    expect(h.layout().state.target).toEqual(target)
    // FR-052: the identity shows plain output information; the displayed version is checked via provider state.
    expectLayoutIdentity(h, target.versionId)
    expect(document.activeElement).toBe(h.element('#subtitle-editor-title'))
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(2)
  })

  it('retains but hides the bound editor after project deselection and never reads or applies a guessed target', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    await openFinalizedProject(h, completed); await h.selectStep('results'); await h.selectTool('layout')
    await h.reply(h.last('layout.read'), { status: 'available', snapshot: layoutSnapshotFor(artifactLayoutTarget(completed.result!.artifacts[0]!), true) })
    const form = h.element<HTMLFormElement>('.cue-edit-form')
    await h.selectStep('source'); await h.click('Project一覧へ戻る')
    expect(h.project().state.draft).toBeNull()
    // FR-061.1: the project list has no results tab; the editor stays mounted but hidden.
    expect(h.container.querySelector('#workflow-tab-results')).toBeNull()
    expect(h.element('.cue-edit-form')).toBe(form)
    expect(form.closest('[hidden], [inert]')).not.toBeNull()
    await act(async () => { fireEvent.submit(form) })
    expect(h.requests.filter(request => request.method === 'layout.read')).toHaveLength(1)
    expect(h.requests.filter(request => request.method === 'layout.apply')).toHaveLength(0)
  })

  it('refuses late setting destinations without guessing or interrupting the current page', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    await openFinalizedProject(h, completed)
    await h.selectStep('results'); await h.click('設定へ')
    const read = h.last('settings.read')
    await h.selectStep('processing')
    await h.reply(read, { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    expectScreen(h, 'processing')
    expect(document.activeElement).toBe(h.tab('processing'))
    expect(h.requests.filter(request => request.method === 'pipeline.regenerate')).toHaveLength(0)
  })
})

async function mount(strict = false) {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  let sequence = 0
  vi.spyOn(window.crypto, 'randomUUID').mockImplementation(() => `90000000-0000-4000-8000-${(++sequence).toString().padStart(12, '0')}`)
  const requests: Array<Parameters<BridgeTransport['postMessage']>[0]> = []
  const listeners = new Set<(event: BridgeMessageEvent) => void>()
  const transport: BridgeTransport = {
    postMessage: request => { requests.push(request) },
    addEventListener: (_type, listener) => { listeners.add(listener) },
    removeEventListener: (_type, listener) => { listeners.delete(listener) },
  }
  vi.stubGlobal('chrome', { webview: transport })
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  let project: ReturnType<typeof useProjectState> | undefined
  let settings: ReturnType<typeof useSettingsWorkspace> | undefined
  let app: ReturnType<typeof useAppState> | undefined
  let layout: ReturnType<typeof useLayoutState> | undefined
  function ObserveProviders() { project = useProjectState(); settings = useSettingsWorkspace(); app = useAppState(); layout = useLayoutState(); return null }
  const cleanup = async () => { if (!cleanupFunctions.delete(cleanup)) return; await act(async () => root.unmount()); container.remove() }
  cleanupFunctions.add(cleanup)
  const tree = (
    <AppStateProvider>
      <ProjectStateProvider>
        <PreviewModeProvider>
          <PlayerNavigationProvider>
            <LayoutStateProvider>
              <ComparisonStateProvider>
                <ReviewStateProvider>
                  <DeliveryStateProvider>
                    <SettingsWorkspaceStateProvider>
                      <ObserveProviders />
                      <WorkflowWorkspace />
                    </SettingsWorkspaceStateProvider>
                  </DeliveryStateProvider>
                </ReviewStateProvider>
              </ComparisonStateProvider>
            </LayoutStateProvider>
          </PlayerNavigationProvider>
        </PreviewModeProvider>
      </ProjectStateProvider>
    </AppStateProvider>
  )
  await act(async () => root.render(strict ? <StrictMode>{tree}</StrictMode> : tree))
  function element<T extends Element = HTMLElement>(selector: string): T {
    const result = container.querySelector<T>(selector)
    if (result === null) throw new Error(`Missing workflow element: ${selector}`)
    return result
  }
  // FR-062: the Workspace folder section in the settings screen has its own 保存 button.
  function button(name: string): HTMLButtonElement {
    const matches = within(container).getAllByRole('button', { name }).filter(element => element.closest('.settings-workspace-location') === null)
    if (matches.length !== 1) throw new Error(`Expected one "${name}" button, found ${matches.length}.`)
    return matches[0] as HTMLButtonElement
  }
  function tab(step: WorkflowStep): HTMLButtonElement { return element(`#workflow-tab-${step}`) }
  function tool(name: WorkflowTool): HTMLButtonElement { return element(`#workflow-tool-${name}`) }
  return {
    container, requests, listeners, element, button, tab, tool,
    project() { if (project === undefined) throw new Error('Project provider not mounted.'); return project },
    settings() { if (settings === undefined) throw new Error('Settings provider not mounted.'); return settings },
    app() { if (app === undefined) throw new Error('App provider not mounted.'); return app },
    layout() { if (layout === undefined) throw new Error('Layout provider not mounted.'); return layout },
    last(method: string) {
      const request = requests.filter(item => item.method === method).at(-1)
      if (request === undefined) throw new Error(`Expected a ${method} request.`)
      return request
    },
    async reply(request: Parameters<BridgeTransport['postMessage']>[0], result: unknown) {
      await act(async () => { for (const listener of [...listeners]) listener({ data: { version: 1, requestId: request.requestId, method: request.method, result } }) })
    },
    async click(name: string) { await act(async () => { button(name).click() }) },
    async selectStep(step: WorkflowStep) { await act(async () => { tab(step).click() }) },
    async selectTool(name: WorkflowTool) { await act(async () => { tool(name).click() }) },
    async change(selector: string, value: string) { await act(async () => { fireEvent.change(element(selector), { target: { value } }) }) },
  }
}

type Harness = Awaited<ReturnType<typeof mount>>
function expectLayoutIdentity(h: Harness, versionId: string): void {
  expect(h.layout().state.snapshot?.compositionVersionId).toBe(versionId)
  const identity = h.element('.layout-identity').textContent
  expect(identity).toContain('画面の大きさ')
  expect(identity).not.toContain(versionId)
}
function artifactLayoutTarget(artifact: SourceOnlyResultArtifact): LayoutVersionContext {
  return { projectId, versionId: artifact.versionId, versionDigest: artifact.versionDigest }
}
function twoShortsPipeline(editorKind: 'Subtitles' | 'Layout' = 'Subtitles') {
  const completed = createCompletedPipeline(projectId)
  for (const seed of [20, 30]) {
    const artifact = createPipelineArtifact(completed.result!, 'VerticalBurnIn', seed)
    completed.result!.artifacts.push(artifact)
    setPipelineWarningTarget({ ...completed.result!, artifacts: [artifact] }, { kind: 'Editor', editorKind, settingKey: null })
  }
  return completed
}
async function selectPipelineArtifact(h: Harness, artifact: SourceOnlyResultArtifact): Promise<void> {
  await act(async () => { h.element<HTMLButtonElement>(`[data-artifact-id="${artifact.artifact.artifactId}"] > button`).click() })
}
function layoutSnapshotFor(target: LayoutVersionContext, subtitleOnly = false): LayoutSnapshot {
  const value = subtitleOnly ? createSubtitleOnlyLayoutResponseValue() : createLayoutResponseValue()
  const snapshot = value.result.snapshot
  snapshot.projectId = target.projectId
  snapshot.compositionVersionId = target.versionId
  snapshot.compositionVersionDigest = target.versionDigest
  if (snapshot.variantPair !== null) {
    snapshot.variantPair.compositionVersionId = target.versionId
    snapshot.variantPair.compositionVersionDigest = target.versionDigest
  }
  for (const cue of [...snapshot.subtitles, ...snapshot.terops]) cue.compositionVersionId = target.versionId
  // Native carries unmodified cue revisions forward rather than rewriting them
  // all on every child. Keep a second cue in every workflow layout fixture.
  const inherited = { ...snapshot.subtitles[0]!, cueId: fixtureId(996), revisionId: fixtureId(995), revisionDigest: '5'.repeat(64),
    text: '継承する二つ目の字幕', outputRange: { start: { value: '3', rate: '1' }, duration: { value: '1', rate: '1' } } }
  snapshot.subtitles.push(inherited)
  for (const display of snapshot.subtitleDisplays) {
    display.sourceCueCount = snapshot.subtitles.length
    if (display.cueMode === 'full') display.displayedCues.push({ cueId: inherited.cueId, text: inherited.text, textChanged: false })
  }
  const parsed = parseLayoutBridgeResponse(value)
  if (parsed?.result.status !== 'available') throw new Error('Invalid workflow layout fixture.')
  return parsed.result.snapshot
}
function subtitleChildSnapshot(base: LayoutSnapshot, versionId = fixtureId(998)): LayoutSnapshot {
  return {
    ...base, revision: base.revision + 1, compositionVersionId: versionId, variantPair: null,
    subtitles: base.subtitles.map((cue, index) => index !== 0 ? cue : ({ ...cue, compositionVersionId: versionId,
      revisionId: fixtureId(997), revisionDigest: '7'.repeat(64), revisionCount: cue.revisionCount + 1, style: 'quote' })),
  }
}
function durableLayoutResult(snapshot: LayoutSnapshot, root: LayoutVersionContext, head: LayoutSnapshot = snapshot) {
  return { status: 'available', snapshot: { ...snapshot,
    persistence: head.revision > 0 && snapshot.revision === head.revision &&
      snapshot.compositionVersionId === head.compositionVersionId && snapshot.compositionVersionDigest === head.compositionVersionDigest
      ? 'persisted' : 'notPersisted',
  }, durableSubtitles: { rootVersionId: root.versionId, headVersionId: head.compositionVersionId,
    headVersionDigest: head.compositionVersionDigest, sequence: head.revision,
    journalDigest: head.revision === 0 ? null : head.revision.toString(16).padStart(64, '0') } }
}
function teropChildSnapshot(base: LayoutSnapshot): LayoutSnapshot {
  const versionId = fixtureId(998)
  return {
    ...base, revision: base.revision + 1, compositionVersionId: versionId, variantPair: null,
    terops: base.terops.map((cue, index) => index !== 0 ? cue : ({ ...cue, compositionVersionId: versionId,
      revisionId: fixtureId(997), revisionDigest: '7'.repeat(64), revisionCount: cue.revisionCount + 1, style: 'quote' })),
  }
}
function expectScreen(h: Harness, screen: WorkflowStep | 'settings'): void {
  expect(h.element('main').getAttribute('data-workflow-screen')).toBe(screen)
  const panels = [...h.container.querySelectorAll<HTMLElement>('.workflow-screen')]
  expect(panels.filter(panel => !panel.hidden).map(panel => panel.id))
    .toEqual([screen === 'settings' ? 'workflow-settings' : `workflow-panel-${screen}`])
  for (const panel of panels) expect(panel.hasAttribute('inert')).toBe(panel.hidden)
  // FR-061.1: the project list shows   two tabs, an open project three.
  const listMode = h.project().state.draft === null
  const labels: Readonly<Partial<Record<WorkflowStep, string>>> = listMode ? projectListStepLabels : workflowStepLabels
    expect(within(h.container).queryAllByRole('tab', { name: /^(元動画|Project一覧|ジョブの状態の確認|仕上がり)$/ })).toHaveLength(screen === 'settings' ? 0 : listMode ? 2 : 3)
  if (screen !== 'settings') expect(within(h.container).getByRole('tabpanel', { name: labels[screen] }).id).toBe(`workflow-panel-${screen}`)
}
function expectNoRawIds(h: Harness): void {
  expect(h.container.querySelector('#job-id, #player-job-id, #timeline-project-id, #layout-project-id, #review-version-id, #delivery-version-id, #comparison-left-version-id, #comparison-right-version-id, #settings-project-id')).toBeNull()
  for (const input of h.container.querySelectorAll<HTMLInputElement>('input')) {
    expect(input.value).not.toMatch(/^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/)
  }
}
function completeInput(): ProjectDraftInput {
  return { sourceCandidateId, contentName: '統合試験の元動画', purpose: 'knowledge sharing', audience: 'developers', publicationScope: 'internalOnly', outputProfileIds: [profileId], language: 'ja-JP', performerNames: ['person'], deadlineSelection: { hasDeadline: false, value: null }, brandTemplateId: templateId }
}
function sourceOnlyDraft(finalized: boolean): ProjectDraft {
  return {
    ...completeInput(), draftId, revision: 2, isFinalized: finalized, projectId: finalized ? projectId : null, sourceStatus: 'available', missingFields: [], updatedAt: timestamp,
    sourceOnly: { profileId, profileVersion: '1.0.0', profileDigest: 'a'.repeat(64), settingsSnapshotDigest: 'b'.repeat(64), artifactSet: ['landscapeLongForm', 'verticalShortCandidates', 'subtitleFiles', 'thumbnailCandidates'] },
  }
}
function listResult(projects: readonly ProjectListItem[] = []) {
  return { projects, canCreate: true, catalog: { outputProfiles: [{ id: profileId, name: 'long-form-1280x720' }], brandTemplates: [{ id: templateId, name: 'development-neutral-long-form' }] } }
}
/** FR-061.1: project-only tabs (results, delivery) exist only while a project is open. */
async function openUnfinalizedDraft(h: Harness): Promise<void> {
  await h.reply(h.last('project.list'), listResult())
  await h.click('新しいProjectを作成')
  await h.reply(h.last('project.createDraft'), { draft: sourceOnlyDraft(false) })
}
async function openFinalizedProject(h: Harness, pipeline: PipelineSnapshot | null = null): Promise<void> {
  const draft = sourceOnlyDraft(true)
  const project: ProjectListItem = { draftId, projectId, state: 'finalized', contentName: draft.contentName, episodeNumber: null, sourceStatus: 'available', owner: null, publicationScope: 'internalOnly', editState: null, reviewState: null, outputFormats: ['long-form-1280x720'], updatedAt: timestamp }
  await h.reply(h.last('project.list'), listResult([project]))
  await h.click(`${draft.contentName}を開く`)
  await h.reply(h.last('project.loadDraft'), { draft })
  expect(h.project().state.draft?.projectId).toBe(projectId)
  await h.reply(h.last('pipeline.read'), { status: pipeline === null ? 'notFound' : 'available', snapshot: pipeline })
}
function editedTranscriptResult() {
  const response = createTranscriptResponseValue('transcript.editToken')
  const snapshot = response.result.snapshot!
  snapshot.utterances[0]!.sentences[0]!.tokens[0]!.text = 'Hub'
  snapshot.utterances[0]!.sentences[0]!.text = 'Hub 未時刻'
  snapshot.utterances[0]!.text = 'Hub 未時刻'
  snapshot.termCandidates = []
  snapshot.parentRevisionId = revisionId
  snapshot.revisionId = '20000000-0000-4000-8000-000000000001'
  snapshot.revisionDigest = 'b'.repeat(64)
  snapshot.changeSource = 'human'
  const utterance = snapshot.utterances[0]!
  const sentence = utterance.sentences[0]!
  snapshot.diff = [
    { kind: 'utterance', utteranceId: utterance.id, sentenceId: null, tokenId: null, previousText: 'ハブ 未時刻', currentText: utterance.text },
    { kind: 'sentence', utteranceId: utterance.id, sentenceId: sentence.id, tokenId: null, previousText: 'ハブ 未時刻', currentText: sentence.text },
    { kind: 'token', utteranceId: utterance.id, sentenceId: sentence.id, tokenId: timedTokenId, previousText: 'ハブ', currentText: 'Hub' },
  ]
  snapshot.staleRange = { sourceRevisionId: revisionId, sourceRevisionDigest: revisionDigest, revisionId: snapshot.revisionId, revisionDigest: snapshot.revisionDigest }
  snapshot.invalidation = {
    staleNodes: ['termCandidates', 'editorialPhraseCandidates', 'repetitionCandidates', 'topicBoundaryCandidates', 'topicRangeBinding', 'contextDeviationCandidates', 'speakerAssignmentBinding', 'topicMetadata', 'emphasisCandidates'],
    recomputationNodes: ['termCandidates', 'editorialPhraseCandidates', 'repetitionCandidates', 'topicBoundaryCandidates', 'contextDeviationCandidates', 'topicMetadata', 'emphasisCandidates'],
    manualRebindingNodes: ['topicRangeBinding', 'speakerAssignmentBinding'],
  }
  const parsed = parseTranscriptResponse(response)
  if (parsed === null) throw new Error('Invalid transcript mutation fixture.')
  return parsed.result
}

function acknowledgeAnalysisTitle(
  snapshot: AnalysisSnapshot,
  title: string,
  reason: string,
): AnalysisSnapshot {
  const topic = snapshot.topics.find(item => item.topicId === analysisTopicId)
  if (topic === undefined) throw new Error('Expected the analysis topic.')
  const nextRevisionId = '10000000-0000-4000-8000-000000000050'
  return {
    ...snapshot,
    topics: snapshot.topics.map(item => item.topicId === analysisTopicId ? {
      ...item,
      title,
      titleOrigin: 'manualEditorial',
      metadataRevisionId: nextRevisionId,
      parentMetadataRevisionId: item.metadataRevisionId,
      changedField: 'title',
      reason,
    } : item),
    stagedChanges: [...snapshot.stagedChanges, {
      kind: 'topicMetadataRevision',
      topicId: analysisTopicId,
      revisionId: nextRevisionId,
      parentRevisionId: topic.metadataRevisionId,
      field: 'title',
      persisted: false,
    }],
  }
}
// FR-061 AC-036
describe('WorkflowWorkspace FR-061 project list and Jobs', () => {
  function versionEntry(snapshot: PipelineSnapshot, createdAt = '2026-10-07T01:00:00.0000000+00:00') {
    return {
      versionId: snapshot.versionId, versionDigest: snapshot.versionDigest, parentVersionId: null, executionId: snapshot.executionId,
      phase: snapshot.phase, hasResult: snapshot.result !== null, isCurrent: true, createdAt, settings: [], outputs: [],
    }
  }
  function finalizedItem(): ProjectListItem {
    const draft = sourceOnlyDraft(true)
    return { draftId, projectId, state: 'finalized', contentName: draft.contentName, episodeNumber: null, sourceStatus: 'available', owner: null, publicationScope: 'internalOnly', editState: null, reviewState: null, outputFormats: ['long-form-1280x720'], updatedAt: timestamp }
  }

  // FR-061 AC-036.1 AC-036.3
  it('lists every created project’s Jobs in the project list and opens one on its job screen', async () => {
    const h = await mount()
    const item = finalizedItem()
    await h.reply(h.last('project.list'), listResult([item]))
    expect(h.container.querySelector('#workflow-tab-results')).toBeNull()
    await h.selectStep('processing')
    expectScreen(h, 'processing')
    const read = h.last('pipeline.versions')
    expect(read.params).toEqual({ projectId })
    const completed = createCompletedPipeline(projectId)
    await h.reply(read, { status: 'available', workingCopyRevision: 1, versions: [versionEntry(completed)] })
    // FR-061.10: the panel also reads each project's latest Job.
    const latest = h.last('pipeline.read')
    expect(latest.params).toEqual({ projectId })
    await h.reply(latest, { status: 'available', snapshot: completed })
    const table = within(h.element('#workflow-panel-processing')).getByRole('table')
    expect(table.textContent).toContain('Job 1（最新）')
    expect(table.textContent).toContain(item.contentName!)
    expectNoRawIds(h)
    await h.click(`「${item.contentName}」のJob 1を開く`)
    expect(h.last('project.loadDraft').params).toEqual({ draftId })
    await h.reply(h.last('project.loadDraft'), { draft: sourceOnlyDraft(true) })
    expect(h.project().state.draft?.projectId).toBe(projectId)
    expect(within(h.container).getAllByRole('tab').map(tab => tab.textContent)).toEqual(['元動画', 'ジョブの状態の確認', '仕上がり'])
    expect(h.tab('processing').getAttribute('aria-selected')).toBe('true')
    await h.reply(h.last('pipeline.read'), { status: 'available', snapshot: completed })
    expectScreen(h, 'processing')
    await h.selectStep('results')
    await h.selectStep('source'); await h.click('Project一覧へ戻る')
    expectScreen(h, 'source')
    expect(within(h.container).getAllByRole('tab').map(tab => tab.textContent)).toEqual(['Project一覧', 'ジョブの状態の確認'])
  })

  // FR-061 AC-036.4 AC-036.6 AC-036.7
  it('creates a new Job from the source screen with the saved next-Job settings and lists the project’s Jobs', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    await openFinalizedProject(h, completed)
    const versions = h.last('pipeline.versions')
    await h.reply(versions, { status: 'available', workingCopyRevision: 1, versions: [versionEntry(completed)] })
    const source = h.element('#workflow-panel-source')
    // FR-061.5: the latest Job's phase arrived while the first read was pending, so the list is read once more.
    await h.reply(h.last('pipeline.versions'), { status: 'available', workingCopyRevision: 1, versions: [versionEntry(completed)] })
    expect(within(source).getByRole('heading', { name: /このProjectのジョブ/ }).textContent).toContain('1件')
    expect(within(source).getByRole('button', { name: '新しいJobを作成' })).toHaveProperty('disabled', true)
    expect(source.textContent).toContain('「次に作成するJobの設定を開く」で設定を確認すると、Jobを作成できます。')
    await h.click('次に作成するJobの設定を開く')
    expectScreen(h, 'settings')
    expect(h.last('settings.read').params).toEqual({ projectId })
    await h.reply(h.last('settings.read'), { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    await h.click('設定を閉じて戻る')
    expectScreen(h, 'source')
    // FR-061.5: showing the source tab again re-reads the Job list.
    await h.reply(h.last('pipeline.versions'), { status: 'available', workingCopyRevision: 1, versions: [versionEntry(completed)] })
    const create = within(source).getByRole('button', { name: '新しいJobを作成' }) as HTMLButtonElement
    expect(create.disabled).toBe(false)
    await act(async () => { create.click() })
    expect(within(source).getByRole('alertdialog').textContent).toContain('Job 2を作成しますか')
    expect(h.requests.filter(request => request.method === 'pipeline.regenerate')).toHaveLength(0)
    await h.click('作成する')
    const regenerate = h.last('pipeline.regenerate')
    expect(regenerate.params).toEqual({ projectId, expectedVersionId: completed.versionId, expectedVersionDigest: completed.versionDigest,
      expectedSettingsRevision: 7, expectedSettingsSnapshotDigest: globalSnapshot.snapshotDigest })
    expect(h.requests.filter(request => request.method === 'pipeline.regenerate')).toHaveLength(1)
    expectScreen(h, 'source')
    await h.selectStep('processing')
    expect(within(h.element('#workflow-panel-processing')).getByRole('heading', { name: /このProjectのジョブ/ })).toBeTruthy()
  })

  // FR-061 AC-036.9
  it('copies a Job’s varying settings into 次に作成するJob by saving them one by one', async () => {
    const h = await mount()
    const completed = createCompletedPipeline(projectId)
    await openFinalizedProject(h, completed)
    const count = (value: string) => ({ key: countKey, value: { kind: 'integer', integerValue: value } })
    const older = { ...versionEntry(completed, '2026-10-07T00:00:00.0000000+00:00'), versionId: fixtureId(880), executionId: fixtureId(881), isCurrent: false, settings: [count('1')] }
    const listed = { status: 'available', workingCopyRevision: 1, versions: [{ ...versionEntry(completed), settings: [count('3')] }, older] }
    await h.reply(h.last('pipeline.versions'), listed)
    const source = h.element('#workflow-panel-source')
    const copy = () => within(source).getByRole('button', { name: 'Job 1の設定を次のJobに使う' }) as HTMLButtonElement
    expect(copy().disabled).toBe(true)
    expect(source.textContent).toContain('「次に作成するJobの設定を開く」で設定を確認すると、Jobの設定を写せます。')
    await h.click('次に作成するJobの設定を開く')
    await h.reply(h.last('settings.read'), { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    await h.click('設定を閉じて戻る')
    // FR-061.5: showing the source screen again re-reads the Jobs.
    await h.reply(h.last('pipeline.versions'), listed)
    await act(async () => { copy().click() })
    await act(async () => { within(source).getByRole('button', { name: '保存する' }).click() })
    const update = h.last('settings.update')
    expect(update.params).toMatchObject({ projectId, key: countKey, value: { kind: 'integer', integerValue: '1' } })
    expect(h.requests.filter(request => request.method === 'settings.update')).toHaveLength(1)
    await h.reply(update, { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 8, snapshotDigest: 'd'.repeat(64),
      values: globalSnapshot.values.map(item => item.key === countKey ? count('1') : item) } })
    expect(source.textContent).toContain('Job 1の設定を「次に作成するJob」に保存しました（1項目）。')
    expect(h.settings().isDirty).toBe(false)
    await act(async () => { copy().click() })
    await act(async () => { within(source).getByRole('button', { name: '保存する' }).click() })
    expect(source.textContent).toContain('Job 1の設定は、すでに「次に作成するJob」と同じです。')
    expect(h.requests.filter(request => request.method === 'settings.update')).toHaveLength(1)
  })
  // FR-061 AC-036.7
  it('does not create a Job while the latest Job is running', async () => {
    const h = await mount()
    const running = createPipelineSnapshot('Analyzing', projectId)
    await openFinalizedProject(h, running)
    await h.click('次に作成するJobの設定を開く')
    await h.reply(h.last('settings.read'), { status: 'available', snapshot: { ...globalSnapshot, projectId, revision: 7 } })
    await h.click('設定を閉じて戻る')
    const source = h.element('#workflow-panel-source')
    expect((within(source).getByRole('button', { name: '新しいJobを作成' }) as HTMLButtonElement).disabled).toBe(true)
    expect(source.textContent).toContain('実行中のJobが終わると、新しいJobを作成できます（Jobは1つずつ順番に実行します）。')
  })
})
