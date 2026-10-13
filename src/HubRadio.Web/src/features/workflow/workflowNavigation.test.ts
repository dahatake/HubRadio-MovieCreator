import { describe, expect, it } from 'vitest'
import { JOBS_GET_STATUS_METHOD, type BridgeJobSnapshot } from '../../bridge/protocol'
import {
  appStateReducer,
  initialAppState,
  jobRequestStarted,
  jobResponseReceived,
} from '../../state/appState'
import {
  getWorkflowTabFocusStep,
  initialWorkflowNavigation,
  isWorkflowStep,
  isWorkflowTabActivationKey,
  resolveWorkflowWarningTarget,
  selectPendingWorkflowWarning,
  selectReturningWorkflowWarningId,
  selectWorkflowScreen,
  workflowNavigationReducer,
  workflowStepLabels,
  workflowSteps,
  workflowTools,
  type WorkflowNavigationAction,
  type WorkflowNavigationState,
  type WorkflowStep,
  type WorkflowTool,
  type WorkflowWarningBlockReason,
  type WorkflowWarningContext,
  type WorkflowWarningTarget,
} from './workflowNavigation'

// UI-only fixtures: these IDs and records are never supplied by production navigation.
const projectId = '10000000-0000-4000-8000-000000000001'
const versionId = '10000000-0000-4000-8000-000000000002'
const artifactId = '10000000-0000-4000-8000-000000000003'
const otherId = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee'
const warningId = 'subtitle-presentation-warning'
const binding = { projectId, versionId, versionDigest: 'a'.repeat(64) }
const context: WorkflowWarningContext = Object.freeze<WorkflowWarningContext>({
  ...binding,
  available: true,
  artifacts: Object.freeze([Object.freeze({ artifactId })]),
  settings: {
    projectId,
    definitions: [{
      key: 'subtitles.enabled',
      group: 'subtitlesAndGraphics',
      isAdvanced: false,
      displayName: 'Subtitles',
      description: 'Enable subtitles.',
      unit: 'none',
      defaultValue: { kind: 'boolean', booleanValue: true },
      minimum: null,
      maximum: null,
      allowedTextValues: [],
      evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }],
    }],
  },
})
const previewTarget = {
  ...binding, artifactId, kind: 'preview', timeBasis: 'source',
  time: { value: '1001', rate: '30000' },
} as const satisfies WorkflowWarningTarget
const settingsTarget = {
  ...binding, kind: 'settings', key: 'subtitles.enabled',
} as const satisfies WorkflowWarningTarget
const evidenceTarget = { ...binding, kind: 'evidence' } as const satisfies WorkflowWarningTarget
const editorTarget = { ...binding, kind: 'editor', tool: 'timeline' } as const satisfies WorkflowWarningTarget
const stateKeys = ['activeTool', 'returnToStep', 'step', 'warningVisit']

describe('workflow navigation', () => {
  it('  starts with three fixed labeled display steps and no domain state', () => {
      expect(workflowSteps).toEqual(['source', 'processing', 'results'])
    // FR-056 AC-031.2: the processing step is shown as 「ジョブの状態の確認」.
      // FR-061 AC-036: the 「保存」 step was removed (request 2026-10-13).
      expect(workflowSteps.map(step => workflowStepLabels[step])).toEqual(['元動画', 'ジョブの状態の確認', '仕上がり'])
    expect(initialWorkflowNavigation).toEqual({ step: 'source', returnToStep: null, activeTool: null, warningVisit: null })
    expect(Object.keys(initialWorkflowNavigation).sort()).toEqual(stateKeys)
    expect(selectWorkflowScreen(initialWorkflowNavigation)).toBe('source')
    expect(selectPendingWorkflowWarning(initialWorkflowNavigation, context)).toBeNull()
    expect(selectReturningWorkflowWarningId(initialWorkflowNavigation, context, [warningId])).toBeNull()
    expect(workflowNavigationReducer(initialWorkflowNavigation, { type: 'returnFromWarning' })).toBe(initialWorkflowNavigation)
  })

  it('allows every forward and backward tab selection without a processing gate', () => {
    for (const from of workflowSteps) {
      for (const step of workflowSteps) {
        const state = reduce({ type: 'selectStep', step: from }, { type: 'selectStep', step })
        expect(state.step).toBe(step)
        expect(selectWorkflowScreen(state)).toBe(step)
        expect(Object.keys(state).sort()).toEqual(stateKeys)
      }
    }
  })

  it('rejects unknown steps rather than choosing a first or nearest step', () => {
    const state = reduce({ type: 'selectStep', step: 'results' })
    for (const step of ['settings', 'running', 'Results', '', '__proto__', null, undefined, 0, {}, 'delivery']) {
      expect(isWorkflowStep(step)).toBe(false)
      expect(workflowNavigationReducer(state, { type: 'selectStep', step: step as WorkflowStep })).toBe(state)
      expect(getWorkflowTabFocusStep(step as WorkflowStep, 'ArrowRight')).toBeNull()
    }
    expect(workflowSteps.every(isWorkflowStep)).toBe(true)
  })

  it('opens independent settings and returns to each original tab and tool', () => {
    for (const step of workflowSteps) {
      const origin = reduce({ type: 'selectTool', tool: 'transcript' }, { type: 'selectStep', step })
      const opened = workflowNavigationReducer(origin, { type: 'openSettings' })
      expect(opened.step).toBe(step)
      expect(opened.returnToStep).toBe(step)
      expect(opened.activeTool).toBe('transcript')
      expect(selectWorkflowScreen(opened)).toBe('settings')
      expect(workflowNavigationReducer(opened, { type: 'closeSettings' })).toEqual(origin)
    }
  })

  it('does not overwrite the settings return step on repeated opens', () => {
    const origin = reduce({ type: 'selectStep', step: 'processing' })
    const opened = workflowNavigationReducer(origin, { type: 'openSettings' })
    expect(workflowNavigationReducer(opened, { type: 'openSettings' })).toBe(opened)
    expect(workflowNavigationReducer(origin, { type: 'closeSettings' })).toBe(origin)
    expect(workflowNavigationReducer(opened, { type: 'closeSettings' })).toEqual(origin)
  })

  it('closes settings to its tab even after following a settings warning from within settings', () => {
    const origin = reduce({ type: 'selectTool', tool: 'review' }, { type: 'selectStep', step: 'processing' })
    const settings = workflowNavigationReducer(origin, { type: 'openSettings' })
    const visited = workflowNavigationReducer(settings, follow(settingsTarget))
    expect(selectReturningWorkflowWarningId(visited, context, [warningId])).toBe(warningId)
    expect(workflowNavigationReducer(visited, { type: 'returnFromWarning' })).toEqual(settings)
    const closed = workflowNavigationReducer(visited, { type: 'closeSettings' })
    expect(closed).toEqual(origin)
    expect(selectWorkflowScreen(closed)).toBe('processing')
    expect(selectPendingWorkflowWarning(closed, context)).toBeNull()
  })

  it('lets an explicit tab selection leave settings without resetting the remembered tool', () => {
    const state = reduce(
      { type: 'selectTool', tool: 'layout' },
      { type: 'openSettings' },
      { type: 'selectStep', step: 'source' },
    )
    expect(selectWorkflowScreen(state)).toBe('source')
    expect(state.returnToStep).toBeNull()
    expect(state.activeTool).toBe('layout')
  })

  it('selects only the eight known tools and remembers them across tab round trips', () => {
    expect(workflowTools).toEqual(['analysis', 'transcript', 'timeline', 'layout', 'thumbnails', 'compare', 'review', 'evidence'])
    for (const tool of workflowTools) {
      const state = reduce(
        { type: 'selectTool', tool },
        { type: 'selectStep', step: 'processing' },
        { type: 'selectStep', step: 'results' },
      )
      expect(state.activeTool).toBe(tool)
      expect(state.step).toBe('results')
      expect(resolveWorkflowWarningTarget({ ...editorTarget, tool }, context).status).toBe('ready')
    }
    expect(reduce({ type: 'selectTool', tool: 'timeline' }, { type: 'selectTool', tool: null }).activeTool).toBeNull()
  })

  it('ignores unknown tools without replacing the existing view', () => {
    const state = reduce({ type: 'selectTool', tool: 'compare' })
    for (const tool of ['player', 'settings', 'unknown', '', '__proto__', undefined, 1]) {
      expect(workflowNavigationReducer(state, { type: 'selectTool', tool: tool as WorkflowTool })).toBe(state)
      expect(resolveWorkflowWarningTarget({ ...editorTarget, tool }, context)).toEqual({ status: 'blocked', reason: 'invalidTarget' })
    }
  })

  it('keeps real job monitoring separate from manual tab changes and progress responses', () => {
    const requestId = '00112233-4455-6677-8899-aabbccddeeff'
    const running: BridgeJobSnapshot = Object.freeze<BridgeJobSnapshot>({
      jobId: otherId, kind: 'mediaIngest', state: 'running',
      currentAttemptId: artifactId, currentAttemptNumber: 1, revision: 1,
      detailsRevision: null, updatedAt: '2026-09-07T00:00:00.0000000Z',
      progressRatio: null, lastProgressAt: null, lastHeartbeatAt: null,
      failure: null, retryability: 'notApplicable', cancellationRequested: false,
      warnings: [], reconciliation: 'unknown',
    })
    const tracked = appStateReducer(
      appStateReducer(initialAppState, jobRequestStarted(requestId, 'status', otherId)),
      jobResponseReceived({ version: 1, requestId, method: JOBS_GET_STATUS_METHOD, result: running }),
    )
    const before = structuredClone(tracked)
    const navigation = reduce(
      { type: 'selectStep', step: 'processing' },
      { type: 'openSettings' },
      { type: 'closeSettings' },
      { type: 'selectStep', step: 'source' },
    )
    expect(tracked).toEqual(before)
    expect(tracked.jobMonitor.snapshot).toBe(running)
    const progress = jobResponseReceived({
      version: 1, requestId, method: JOBS_GET_STATUS_METHOD,
      result: { ...running, progressRatio: 0.5, lastProgressAt: '2026-09-07T00:00:01.0000000Z' },
    })
    const updated = appStateReducer(
      appStateReducer(tracked, jobRequestStarted(requestId, 'status', otherId)), progress,
    )
    expect(updated.jobMonitor.snapshot?.progressRatio).toBe(0.5)
    expect(updated.jobMonitor.trackedJobId).toBe(otherId)
    expect(navigation.step).toBe('source')
    expect(workflowNavigationReducer(navigation, progress as unknown as WorkflowNavigationAction)).toBe(navigation)
  })

  it('routes only the four warning destination kinds without selecting domain objects', () => {
    const cases = [
      { target: previewTarget, screen: 'results', tool: null },
      { target: settingsTarget, screen: 'settings', tool: 'review' },
      { target: evidenceTarget, screen: 'results', tool: 'evidence' },
      { target: editorTarget, screen: 'results', tool: 'timeline' },
    ] as const
    for (const item of cases) {
      const state = reduce({ type: 'selectTool', tool: 'review' }, { type: 'selectStep', step: 'processing' }, follow(item.target))
      expect(selectWorkflowScreen(state)).toBe(item.screen)
      expect(state.activeTool).toBe(item.tool)
      expect(state.warningVisit?.target).toEqual(item.target)
      expect(state.warningVisit?.pending).toBe(true)
      expect(Object.keys(state).sort()).toEqual(stateKeys)
      expect(selectPendingWorkflowWarning(state, context)?.status).toBe('ready')
    }
  })

  it('preserves canonical Int64 rational times exactly, including signed source timestamps', () => {
    for (const time of [
      { value: '9007199254740993', rate: '2' },
      { value: '9223372036854775807', rate: '1' },
      { value: '-9223372036854775808', rate: '1' },
      { value: '1001', rate: '30000' },
      { value: '0', rate: '1' },
    ]) {
      const target = { ...previewTarget, time }
      const state = reduce(follow(target))
      expect(state.warningVisit?.target).toEqual(target)
      expect(selectPendingWorkflowWarning(state, context)).toEqual({ status: 'ready', target })
    }
    expect(resolveWorkflowWarningTarget({ ...previewTarget, timeBasis: 'output' }, context).status).toBe('ready')
    expect(resolveWorkflowWarningTarget({ ...previewTarget, timeBasis: 'output', time: { value: '0', rate: '1' } }, context).status).toBe('ready')
  })

  it('rejects malformed, noncanonical or ambiguous times rather than rounding or seeking frame zero', () => {
    const invalidTimes: readonly unknown[] = [
      null, undefined, 0, { value: 1, rate: '30' }, { value: '1', rate: 30 },
      { value: '1', rate: '0' }, { value: '1', rate: '-1' },
      { value: '01', rate: '30' }, { value: '-0', rate: '1' },
      { value: '1.5', rate: '30' }, { value: '2', rate: '4' },
      { value: '9223372036854775808', rate: '1' },
      { value: '-9223372036854775809', rate: '1' },
      { value: '1', rate: '9223372036854775808' },
      { value: '1\n', rate: '30' }, { value: '1', rate: '30', seconds: 0 },
    ]
    for (const time of invalidTimes) expectBlocked({ ...previewTarget, time }, 'invalidTarget')
    for (const timeBasis of [null, undefined, 'proxy', 'seconds']) {
      expectBlocked({ ...previewTarget, timeBasis }, 'invalidTarget')
    }
    expectBlocked({ ...previewTarget, timeBasis: 'output', time: { value: '-1', rate: '30' } }, 'invalidTarget')
  })

  it('rejects missing identities and does not substitute current provider IDs', () => {
    for (const key of ['projectId', 'versionId', 'versionDigest']) {
      for (const value of [null, undefined, '']) expectBlocked({ ...previewTarget, [key]: value }, 'invalidTarget')
      const missing: Record<string, unknown> = { ...previewTarget }
      delete missing[key]
      expectBlocked(missing, 'invalidTarget')
    }
  })

  it('uses canonical entity IDs and exact digest strings without trimming or case conversion', () => {
    for (const key of ['projectId', 'versionId', 'artifactId']) {
      for (const value of [0, '', 'not-an-id', '00000000-0000-0000-0000-000000000000', otherId.toUpperCase(), `${otherId}\n`]) {
        expectBlocked({ ...previewTarget, [key]: value }, 'invalidTarget')
      }
    }
    for (const versionDigest of ['A'.repeat(64), 'a'.repeat(63), 'a'.repeat(65), `${binding.versionDigest}\n`, 'g'.repeat(64)]) {
      expectBlocked({ ...previewTarget, versionDigest }, 'invalidTarget')
    }
    expectBlocked({ ...previewTarget, artifactId: null }, 'invalidTarget')
  })

  it('rejects unknown kinds, extra fields and missing kind-specific references', () => {
    for (const target of [null, undefined, [], {}, 'preview', { ...previewTarget, kind: 'url' }, { ...previewTarget, kind: '__proto__' }, { ...evidenceTarget, path: '/first-frame' }, { ...settingsTarget, key: null }, { ...editorTarget, tool: null }]) {
      expectBlocked(target, 'invalidTarget')
    }
    for (const [target, key] of [[previewTarget, 'time'], [previewTarget, 'timeBasis'], [settingsTarget, 'key'], [editorTarget, 'tool']] as const) {
      const missing: Record<string, unknown> = { ...target }
      delete missing[key]
      expectBlocked(missing, 'invalidTarget')
    }
  })

  it('fails closed on absent current context and wrong project, version or digest', () => {
    for (const key of ['projectId', 'versionId', 'versionDigest']) {
      expectBlocked(previewTarget, 'missingContext', { ...context, [key]: null })
      expectBlocked(previewTarget, 'missingContext', { ...context, [key]: 'invalid' })
    }
    expectBlocked(previewTarget, 'projectMismatch', { ...context, projectId: otherId })
    expectBlocked(previewTarget, 'versionMismatch', { ...context, versionId: otherId })
    expectBlocked(previewTarget, 'digestMismatch', { ...context, versionDigest: 'b'.repeat(64) })
  })

  it('requires any specified artifact to exist in the bound version without picking a replacement', () => {
    expectBlocked(previewTarget, 'artifactUnavailable', { ...context, artifacts: undefined })
    expectBlocked(previewTarget, 'artifactUnavailable', { ...context, artifacts: [] })
    expectBlocked({ ...previewTarget, artifactId: otherId }, 'artifactUnavailable')
    expect(resolveWorkflowWarningTarget(previewTarget, context).status).toBe('ready')
    expect(resolveWorkflowWarningTarget(evidenceTarget, { ...context, artifacts: [] }).status).toBe('ready')
  })

  it('accepts only an actual setting definition belonging to the warning project', () => {
    expect(resolveWorkflowWarningTarget(settingsTarget, context).status).toBe('ready')
    for (const key of ['unknown.key', 'Subtitles.enabled', 'subtitles.enabled\n']) {
      expectBlocked({ ...settingsTarget, key }, 'settingUnavailable')
    }
    expectBlocked(settingsTarget, 'settingUnavailable', { ...context, settings: null })
    expectBlocked(settingsTarget, 'settingUnavailable', { ...context, settings: { projectId, definitions: [] } })
    expectBlocked(settingsTarget, 'settingUnavailable', { ...context, settings: { ...context.settings!, projectId: otherId } })
  })

  it('requires explicit destination availability without disabling ordinary tabs or settings', () => {
    const unavailable = { ...context, available: false }
    for (const target of [previewTarget, settingsTarget, evidenceTarget, editorTarget]) expectBlocked(target, 'unavailable', unavailable)
    const state = reduce({ type: 'selectStep', step: 'results' }, { type: 'openSettings' })
    expect(selectWorkflowScreen(state)).toBe('settings')
    expect(state.step).toBe('results')
  })

  it('suspends a hidden pending destination in settings and resumes without losing its return context', () => {
    const state = reduce(follow(previewTarget))
    const pending = selectPendingWorkflowWarning(state, context)
    expect(pending?.status).toBe('ready')
    const hidden = workflowNavigationReducer(state, { type: 'openSettings' })
    expect(selectPendingWorkflowWarning(hidden, context)).toBeNull()
    expect(hidden.warningVisit).toBe(state.warningVisit)
    const visible = workflowNavigationReducer(hidden, { type: 'closeSettings' })
    expect(visible).toEqual(state)
    expect(selectPendingWorkflowWarning(visible, context)).toEqual(pending)
  })

  it('consumes only the current pending intent and never treats navigation as warning resolution', () => {
    const first = reduce(follow(previewTarget))
    const second = workflowNavigationReducer(first, follow(previewTarget, context, 'another-warning'))
    expect(workflowNavigationReducer(second, { type: 'warningDestinationHandled', target: first.warningVisit!.target })).toBe(second)
    const pending = selectPendingWorkflowWarning(second, context)
    expect(pending?.status).toBe('ready')
    if (pending?.status !== 'ready') throw new Error('Expected a pending display intent.')
    const handled = workflowNavigationReducer(second, { type: 'warningDestinationHandled', target: pending.target })
    expect(handled.warningVisit?.pending).toBe(false)
    expect(handled.warningVisit?.returnTo.warningId).toBe('another-warning')
    expect(selectReturningWorkflowWarningId(handled, context, ['another-warning'])).toBe('another-warning')
    expect(selectPendingWorkflowWarning(handled, context)).toBeNull()
    expect(workflowNavigationReducer(handled, { type: 'warningDestinationHandled', target: pending.target })).toBe(handled)
    expect(Object.keys(handled).sort()).toEqual(stateKeys)
  })

  it('clears only the failed pending association without changing the current screen or restoring its warning', () => {
    const first = reduce(follow(previewTarget))
    const current = workflowNavigationReducer(first, follow(previewTarget, context, 'new-warning'))
    expect(workflowNavigationReducer(current, { type: 'warningDestinationFailed', target: first.warningVisit!.target })).toBe(current)
    const failed = workflowNavigationReducer(current, { type: 'warningDestinationFailed', target: current.warningVisit!.target })
    expect(selectWorkflowScreen(failed)).toBe(selectWorkflowScreen(current))
    expect(failed.activeTool).toBe(current.activeTool)
    expect(failed.warningVisit).toBeNull()
    expect(selectReturningWorkflowWarningId(failed, context, ['new-warning'])).toBeNull()
    expect(workflowNavigationReducer(failed, { type: 'warningDestinationHandled', target: current.warningVisit!.target })).toBe(failed)
  })

  it('restores only an extant warning ID and never substitutes the first available warning', () => {
    const state = reduce(follow(previewTarget))
    expect(state.warningVisit?.returnTo.warningId).toBe(warningId)
    expect(selectReturningWorkflowWarningId(state, context, ['first-warning', warningId])).toBe(warningId)
    expect(selectReturningWorkflowWarningId(state, context, ['first-warning'])).toBeNull()
    expect(selectReturningWorkflowWarningId(state, context, [])).toBeNull()
    for (const supplied of [null, undefined, '']) {
      const withoutId = reduce({ type: 'followWarning', target: previewTarget, context, warningId: supplied })
      expect(withoutId.warningVisit?.returnTo.warningId).toBeNull()
      expect(selectReturningWorkflowWarningId(withoutId, context, [warningId])).toBeNull()
    }
  })

  it('returns from every warning kind to the original view and lets the provider retain the warning selection', () => {
    const origin = reduce(    { type: 'selectTool', tool: 'review' }, { type: 'selectStep', step: 'processing' })
    for (const target of [previewTarget, settingsTarget, evidenceTarget, editorTarget]) {
      const visited = workflowNavigationReducer(origin, follow(target))
      const selectedWarningId = selectReturningWorkflowWarningId(visited, context, [warningId])
      const returned = workflowNavigationReducer(visited, { type: 'returnFromWarning' })
      expect(returned).toEqual(origin)
      expect(selectedWarningId).toBe(warningId)
      expect(returned.warningVisit).toBeNull()
      if (target.kind === 'settings') expect(workflowNavigationReducer(visited, { type: 'closeSettings' })).toEqual(origin)
    }
    const settingsOrigin = workflowNavigationReducer(origin, { type: 'openSettings' })
    expect(workflowNavigationReducer(workflowNavigationReducer(settingsOrigin, follow(evidenceTarget)), { type: 'returnFromWarning' })).toEqual(settingsOrigin)
  })

  it('revalidates pending and return identities after asynchronous context changes', () => {
    const state = reduce(follow(previewTarget))
    for (const changed of [
      { ...context, projectId: otherId },
      { ...context, versionId: otherId },
      { ...context, versionDigest: 'b'.repeat(64) },
      { ...context, projectId: null },
    ]) {
      expect(selectPendingWorkflowWarning(state, changed)?.status).toBe('blocked')
      expect(selectReturningWorkflowWarningId(state, changed, [warningId])).toBeNull()
    }
    const unavailable = { ...context, available: false }
    expect(selectPendingWorkflowWarning(state, unavailable)).toEqual({ status: 'blocked', reason: 'unavailable' })
    expect(selectReturningWorkflowWarningId(state, unavailable, [warningId])).toBe(warningId)
    expect(workflowNavigationReducer(state, { type: 'returnFromWarning' })).toEqual(initialWorkflowNavigation)
  })

  it('replaces the single warning visit instead of building a router history stack', () => {
    const first = reduce(follow(editorTarget))
    const second = workflowNavigationReducer(first, follow(evidenceTarget, context, 'second-warning'))
    expect(second.warningVisit?.target).toEqual(evidenceTarget)
    expect(second.warningVisit?.returnTo).toEqual({ step: 'results', returnToStep: null, activeTool: 'timeline', warningId: 'second-warning' })
    expect(Object.keys(second).sort()).toEqual(stateKeys)
    expect(Object.keys(second.warningVisit!).sort()).toEqual(['pending', 'returnTo', 'target'])
    const returned = workflowNavigationReducer(second, { type: 'returnFromWarning' })
    expect(returned.activeTool).toBe('timeline')
    expect(returned.warningVisit).toBeNull()
    expect(workflowNavigationReducer(returned, { type: 'returnFromWarning' })).toBe(returned)
  })

  it('abandons pending warning work on explicit tab or tool selection', () => {
    const state = reduce(follow(previewTarget))
    for (const action of [{ type: 'selectStep', step: 'processing' }, { type: 'selectTool', tool: 'analysis' }] as const) {
      const moved = workflowNavigationReducer(state, action)
      expect(moved.warningVisit).toBeNull()
      expect(selectPendingWorkflowWarning(moved, context)).toBeNull()
      expect(selectReturningWorkflowWarningId(moved, context, [warningId])).toBeNull()
    }
  })

  it('does not mutate input state or retain mutable target payloads or provider snapshots', () => {
    const time: { value: string; rate: string } = { ...previewTarget.time }
    const target = { ...previewTarget, time }
    const state = workflowNavigationReducer(initialWorkflowNavigation, follow(target))
    expect(initialWorkflowNavigation.warningVisit).toBeNull()
    expect(state.warningVisit?.target).not.toBe(target)
    target.projectId = otherId
    target.time.value = '0'
    expect(state.warningVisit?.target).toEqual(previewTarget)
    expect(Object.keys(state).sort()).toEqual(stateKeys)
    expect(Object.keys(state.warningVisit!.returnTo).sort()).toEqual(['activeTool', 'returnToStep', 'step', 'warningId'])
    const frozen = Object.freeze(state)
    expect(workflowNavigationReducer(frozen, { type: 'selectStep', step: 'source' }).step).toBe('source')
    expect(frozen.step).toBe('results')
  })

  it('moves manual-tab focus with wrapping arrows, Home and End without selecting a tab', () => {
    const state = reduce({ type: 'selectStep', step: 'processing' })
    for (let index = 0; index < workflowSteps.length; index += 1) {
      const step = workflowSteps[index]!
      expect(getWorkflowTabFocusStep(step, 'ArrowLeft')).toBe(      workflowSteps[(index + 2) % 3])
            expect(getWorkflowTabFocusStep(step, 'ArrowRight')).toBe(workflowSteps[(index + 1) % 3])
      expect(getWorkflowTabFocusStep(step, 'Home')).toBe('source')
            expect(getWorkflowTabFocusStep(step, 'End')).toBe('results')
    }
    expect(state.step).toBe('processing')
  })

  it('activates only on Enter or Space and leaves vertical arrows and Tab to the browser', () => {
    for (const key of ['Enter', ' ']) {
      expect(isWorkflowTabActivationKey(key)).toBe(true)
      expect(getWorkflowTabFocusStep('processing', key)).toBeNull()
    }
    for (const key of ['ArrowUp', 'ArrowDown', 'Tab', 'Escape', 'PageDown', 'Spacebar', '']) {
      expect(isWorkflowTabActivationKey(key)).toBe(false)
      expect(getWorkflowTabFocusStep('processing', key)).toBeNull()
    }
    for (const key of ['ArrowLeft', 'ArrowRight', 'Home', 'End']) expect(isWorkflowTabActivationKey(key)).toBe(false)
  })
})

function reduce(...actions: readonly WorkflowNavigationAction[]): WorkflowNavigationState {
  return actions.reduce<WorkflowNavigationState>(workflowNavigationReducer, initialWorkflowNavigation)
}

function follow(target: unknown, current = context, selectedWarningId: string | null = warningId): WorkflowNavigationAction {
  return { type: 'followWarning', target, context: current, warningId: selectedWarningId }
}

function expectBlocked(
  target: unknown,
  reason: WorkflowWarningBlockReason,
  current: WorkflowWarningContext = context,
): void {
  const origin = reduce({ type: 'selectStep', step: 'processing' })
  expect(resolveWorkflowWarningTarget(target, current)).toEqual({ status: 'blocked', reason })
  expect(workflowNavigationReducer(origin, follow(target, current))).toBe(origin)
  expect(selectPendingWorkflowWarning(origin, current)).toBeNull()
}