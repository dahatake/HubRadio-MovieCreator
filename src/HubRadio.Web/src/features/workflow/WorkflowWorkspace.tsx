import { useCallback, useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState, type ReactNode } from 'react'

import { createHostBridgeClient } from '../../bridge'
import { AppIcon } from '../../components/AppIcons'
import { AnalysisPanel } from '../analysis/AnalysisPanel'
import { ComparePanel, useComparisonState } from '../compare'
import { useDeliveryState } from '../delivery'
import { DeliveryPanelView, type DeliveryPanelProps } from '../delivery/DeliveryPanel'
import { useLayoutState } from '../layout'
import { LayoutPanelView, type LayoutPanelProps } from '../layout/LayoutPanel'
import { hasTargetSubtitleEvidence, layoutChildSnapshotMatchesOperation, layoutSnapshotMatchesDurableHead, layoutSnapshotMatchesTarget, layoutSnapshotVersion, layoutSupportsManualEditing, type LayoutOperation, type LayoutSnapshot, type LayoutVersionContext } from '../layout/layoutProtocol'
import { PlayerPanel, type PlayerExactFrameSelection, type PlayerTimelinePosition } from '../player'
import { usePlayerSourceNavigation } from '../player/PlayerNavigationProvider'
import { findProxyFrameAtSourceTime, positionMatchesFrame, type PlayerSnapshot } from '../player/playerProtocol'
import { PreviewModeControls, usePreviewModes } from '../preview-modes'
import { AllProjectJobsPanel } from '../jobs/AllProjectJobsPanel'
import { JobCreationAction } from '../jobs/JobCreationAction'
import { ProjectJobList } from '../jobs/ProjectJobList'
import { editableJobSettings } from '../results/jobSettings'
import { ProjectWorkspace } from '../projects/ProjectWorkspace'
import { useProjectState } from '../projects/ProjectStateProvider'
import { ResultGallery, type ResultGalleryWarning } from '../results/ResultGallery'
import { JobResultsBrowser, type JobResultsView } from '../results/JobResultsBrowser'
import { deriveResultPresentation } from '../results/resultPresentation'
import { useReviewState } from '../review'
import { ReviewPanelView, type ReviewPanelProps } from '../review/ReviewPanel'
import { SettingsPanel, useSettingsWorkspace, type SettingsSnapshot } from '../settings'
import { SettingsRegenerationAction } from '../settings/SettingsOutcomePreview'
import { createHostBookendStitchService, type BookendStitchService } from '../stitch/stitchProtocol'
import { ThumbnailPanel } from '../thumbnails'
import { TimelineEditor } from '../timeline'
import type { TimelineSnapshot } from '../timeline/timelineProtocol'
import { TranscriptPanel } from '../transcript/TranscriptPanel'
import {
  initialWorkflowNavigation,
  projectListStepLabels,
  projectListSteps,
  visibleWorkflowStep,
  workflowSteps,
  selectWorkflowScreen,
  workflowNavigationReducer,
  workflowTools,
  selectReturningWorkflowWarningId,
  type WorkflowWarningContext,
  type WorkflowWarningTarget,
  type WorkflowStep,
  type WorkflowTool,
} from './workflowNavigation'
import { PipelineProvider, usePipeline } from './PipelineProvider'
import { GeneratedResultPreview, PipelineProgress, toWorkflowWarningTarget, type GeneratedPreviewSeek } from './pipelinePresentation'
import { isPipelineLive, pipelineRegistrationRefreshKey, sameResultBinding, sourceOnlyMediaUrl, type PipelineRegenerationParams, type PipelineRegenerationExtras, type PipelineVersionEntry, type SourceOnlyResultBinding, type SourceOnlyResultWarning } from './pipelineProtocol'
// Import the shell after feature components so its local layout rules win.
import { WorkflowTabs } from './WorkflowTabs'
import { ArrangeablePanels } from '../arrange/ArrangeablePanels'
import type { ArrangePreset } from '../arrange/arrangeState'

/**
 * NFR-UX-003 (AC-128): the editor persona's default for the 仕上がり screen — preview first, and the content
 * (timeline, compare table, text alternatives) given more room. The user can change it; 「既定に戻す」 returns here.
 */
const editorResultsPreset: ArrangePreset = Object.freeze({ order: ['preview', 'tools'], sizes: Object.freeze({ tools: 2 }) })
import './workflow.css'

const toolLabels: Readonly<Record<WorkflowTool, string>> = {
  analysis: '解析', transcript: '文字起こし', timeline: 'カット', layout: '字幕・画角',
  thumbnails: 'サムネイル', compare: '比較', review: 'レビュー履歴', evidence: '根拠',
}

interface EditorVersion { readonly projectId: string; readonly versionId: string; readonly versionDigest: string }
interface WarningOrigin {
  readonly binding: SourceOnlyResultBinding
  readonly warning: SourceOnlyResultWarning
  readonly target: WorkflowWarningTarget
  readonly waitingForSettings: boolean
  readonly settingsDigest: string | null
}
interface LayoutOpenIntent {
  /** Original artifact root, never replaced by a subtitle journal child. */
  readonly target: LayoutVersionContext
  readonly warning: WarningOrigin | null
}
interface LayoutReadReceipt {
  readonly intent: LayoutOpenIntent
  readonly error: string | null
  readonly snapshot: LayoutSnapshot | null
}

function sameEditorVersion(left: LayoutVersionContext | null | undefined, right: LayoutVersionContext | null | undefined): boolean {
  return left != null && right != null && left.projectId === right.projectId &&
    left.versionId === right.versionId && left.versionDigest === right.versionDigest
}

/** The session survives route changes and starts only for a finalized selection. */
export function WorkflowWorkspace({ connectionStatus }: Readonly<{ connectionStatus?: ReactNode }>) {
  usePreviewModes()
  const { state } = useProjectState()
  return <PipelineProvider projectId={state.draft?.isFinalized ? state.draft.projectId : null}>
    <WorkflowWorkspaceView connectionStatus={connectionStatus} />
  </PipelineProvider>
}

function WorkflowWorkspaceView({ connectionStatus }: Readonly<{ connectionStatus?: ReactNode }>) {
  const { state: previewModes } = usePreviewModes()
  const { state: projectState, closeDraft, loadDraft } = useProjectState()
  const pipeline = usePipeline()
  const layout = useLayoutState()
  useComparisonState()
  useReviewState()
  useDeliveryState()
  const settings = useSettingsWorkspace()
  const requestSourceSeek = usePlayerSourceNavigation()
  const [navigation, navigate] = useReducer(workflowNavigationReducer, initialWorkflowNavigation)
  const [bookendStitch, setBookendStitch] = useState<BookendStitchService | null>(null)
  useEffect(() => {
    // FR-035: one stitch transport per mounted workspace; absent outside WebView2.
    const service = createHostBookendStitchService()
    setBookendStitch(service)
    return () => service?.dispose()
  }, [])
  const [playerPosition, setPlayerPosition] = useState<PlayerTimelinePosition | null>(null)
  const [playerSelection, setPlayerSelection] = useState<PlayerExactFrameSelection | null>(null)
  const [sourceDirty, setSourceDirty] = useState(false)
  const [analysisDirty, setAnalysisDirty] = useState(false)
  const [settingsDirty, setSettingsDirty] = useState(false)
  const [transcriptDirty, setTranscriptDirty] = useState(false)
  const [timelineDirty, setTimelineDirty] = useState(false)
  const [layoutDirty, setLayoutDirty] = useState(false)
  const [reviewDirty, setReviewDirty] = useState(false)
  const dirtyFlags = useRef({ source: false, analysis: false, settings: false, transcript: false, timeline: false, layout: false, review: false })
  const reportDirty = useMemo(() => ({
    source: (value: boolean) => { dirtyFlags.current.source = value; setSourceDirty(value) },
    analysis: (value: boolean) => { dirtyFlags.current.analysis = value; setAnalysisDirty(value) },
    settings: (value: boolean) => { dirtyFlags.current.settings = value; setSettingsDirty(value) },
    transcript: (value: boolean) => { dirtyFlags.current.transcript = value; setTranscriptDirty(value) },
    timeline: (value: boolean) => { dirtyFlags.current.timeline = value; setTimelineDirty(value) },
    layout: (value: boolean) => { dirtyFlags.current.layout = value; setLayoutDirty(value) },
    review: (value: boolean) => { dirtyFlags.current.review = value; setReviewDirty(value) },
  }), [])
  const [editorVersion, setEditorVersion] = useState<EditorVersion | null>(null)
  const [timelineSnapshot, setTimelineSnapshot] = useState<TimelineSnapshot | null>(null)
  const [playerEvidence, setPlayerEvidence] = useState<Readonly<{ jobId: string; snapshot: PlayerSnapshot }> | null>(null)
  const [sourceSeekAck, setSourceSeekAck] = useState<Readonly<{ origin: WarningOrigin; error: string | null }> | null>(null)
  const [artifactSelection, setArtifactSelection] = useState<Readonly<{ binding: SourceOnlyResultBinding; artifactId: string; digest: string }> | null>(null)
  const [sourcePreview, setSourcePreview] = useState(false)
  const [resultView, setResultView] = useState<JobResultsView>('job')
  const [resultViewProjectId, setResultViewProjectId] = useState<string | null>(null)
  const [warningOrigin, setWarningOrigin] = useState<WarningOrigin | null>(null)
  const [warningNotice, setWarningNotice] = useState<string | null>(null)
  const [selectedWarningId, setSelectedWarningId] = useState<string | null>(null)
  const [layoutIntent, setLayoutIntent] = useState<LayoutOpenIntent | null>(null)
  const [layoutReadReceipt, setLayoutReadReceipt] = useState<LayoutReadReceipt | null>(null)
  const sourceSeekSent = useRef<WarningOrigin | null>(null)
  const canceledWarning = useRef<WarningOrigin | null>(null)
  const settingsOpener = useRef<HTMLElement | null>(null)
  const pendingFocus = useRef<HTMLElement | string | null>(null)
  const workspace = useRef<HTMLElement>(null)
  const projectOpen = projectState.draft !== null
  const selectedScreen = selectWorkflowScreen(navigation)
  // FR-061.1: the project list has no results/delivery tab; it shows the list instead.
  const screen = selectedScreen === 'settings' ? selectedScreen : visibleWorkflowStep(selectedScreen, projectOpen)
  const shownStep = visibleWorkflowStep(navigation.step, projectOpen)
  const settingsOpen = screen === 'settings'
  const resultsActive = screen === 'results'
  const draft = projectState.draft
  const activeProjectId = draft?.projectId ?? null
  const projectContext = useMemo(() => ({ projectId: activeProjectId }), [activeProjectId])
  const summary = draft?.sourceOnly ?? null
  const snapshot = pipeline.state.snapshot
  const settingsSnapshot = settings.state.snapshot
  const savedSettings: SettingsSnapshot | null = settingsSnapshot !== null && settingsSnapshot.projectId !== null &&
    settingsSnapshot.projectId === activeProjectId ? settingsSnapshot : null
  const pipelineCurrent = pipeline.state.status === 'available' && !pipeline.state.needsRefresh && snapshot !== null && snapshot.resultStatus !== 'invalid'
  const refreshKey = pipelineRegistrationRefreshKey(snapshot)
  const registrationContext = useMemo(() => ({ projectId: activeProjectId, refreshKey }), [activeProjectId, refreshKey])
  const presentation = useMemo(() => deriveResultPresentation({ sourceOnlySummary: summary, pipeline: snapshot,
    currentSettingsSnapshotDigest: savedSettings?.snapshotDigest }), [summary, snapshot, savedSettings?.snapshotDigest])
  const result = presentation.pipelineResult ?? null
  const resultArtifacts = result?.artifacts
  const resultWarnings = useMemo(() => resultArtifacts?.flatMap(a => a.warnings) ?? [], [resultArtifacts])
  const selectedArtifact = result === null ? null : artifactSelection !== null && sameResultBinding(artifactSelection.binding, result.binding)
    ? result.artifacts.find(a => a.artifact.artifactId === artifactSelection.artifactId && a.artifact.contentDigest === artifactSelection.digest) ?? null
    : result.artifacts.find(a => a.role === 'LandscapeClean') ?? null
  const ingestContext = useMemo(() => ({ jobId: snapshot === null || !pipelineCurrent ? null
    : (snapshot.phase === 'Queued' || snapshot.phase === 'Ingesting') && snapshot.parentVersionId === null &&
      !(playerEvidence?.jobId === snapshot.ingestJobId && playerEvidence.snapshot.projectId === snapshot.projectId)
      ? null : snapshot.ingestJobId }), [pipelineCurrent, snapshot?.projectId, snapshot?.ingestJobId, snapshot?.phase, snapshot?.parentVersionId, playerEvidence])
  const versionContext = useMemo(() => ({ versionId: editorVersion?.projectId === activeProjectId ? editorVersion.versionId : null }), [editorVersion, activeProjectId])
  const comparisonContext = useMemo(() => ({
    left: snapshot?.parentVersionId != null && snapshot.parentDigest !== null ? { versionId: snapshot.parentVersionId, versionDigest: snapshot.parentDigest } : null,
    right: snapshot?.versionId != null && snapshot.versionDigest !== null ? { versionId: snapshot.versionId, versionDigest: snapshot.versionDigest } : null,
  }), [snapshot?.parentVersionId, snapshot?.parentDigest, snapshot?.versionId, snapshot?.versionDigest])
  const dirtyTools = [
    { tool: 'analysis', dirty: analysisDirty },
    { tool: 'transcript', dirty: transcriptDirty },
    { tool: 'timeline', dirty: timelineDirty },
    { tool: 'layout', dirty: layoutDirty },
    { tool: 'review', dirty: reviewDirty },
  ] satisfies readonly { tool: WorkflowTool; dirty: boolean }[]
  const layoutOpening = resultsActive && navigation.activeTool === 'layout' && layoutIntent !== null && layoutReadReceipt?.intent !== layoutIntent
  const layoutBusy = layoutDirty || dirtyFlags.current.layout || layout.state.pending !== null || layoutOpening
  const navigationBlocked = settingsDirty || settings.isDirty || dirtyTools.some(item => item.dirty) || layoutBusy
  const selection = activeProjectId !== null && playerSelection?.projectId === activeProjectId && playerSelection.jobId === ingestContext.jobId
    ? playerSelection : null
  const position = selection !== null && playerPosition?.projectId === activeProjectId && (sourcePreview || result === null)
    ? playerPosition : null
  const toolActive = (tool: WorkflowTool): boolean => resultsActive && navigation.activeTool === tool
  const warningBindingCurrent = warningOrigin !== null && result !== null && sameResultBinding(warningOrigin.binding, result.binding) &&
    resultWarnings.some(w => w.id === warningOrigin.warning.id && JSON.stringify(w) === JSON.stringify(warningOrigin.warning))
  const warningIsCurrent = warningBindingCurrent && canceledWarning.current !== warningOrigin && pipelineCurrent
  const generatedSeek = useMemo<GeneratedPreviewSeek | null>(() => warningIsCurrent && navigation.warningVisit?.pending &&
    warningOrigin?.warning.target.kind === 'Preview' && warningOrigin.warning.target.timeBasis === 'Output'
      ? { warningId: warningOrigin.warning.id, target: warningOrigin.warning.target } : null,
  [warningIsCurrent, navigation.warningVisit?.pending, warningOrigin])
  const showGenerated = !sourcePreview && selectedArtifact !== null && result !== null
  // FR-063.1: the Job browser needs a created Project; the comparison uses the whole width.
  // FR-063.1: the results start in the per-Job view for every Project that is opened.
  if (resultViewProjectId !== activeProjectId) {
    setResultViewProjectId(activeProjectId)
    setResultView('job')
  }
  const jobBrowserShown = draft?.isFinalized === true && activeProjectId !== null
  const comparing = jobBrowserShown && resultsActive && navigation.activeTool === null && resultView === 'compare'
  const inputs = useRef({ activeProjectId, ingestJobId: ingestContext.jobId, editorVersion, warningOrigin, warningIsCurrent })
  inputs.current = { activeProjectId, ingestJobId: ingestContext.jobId, editorVersion, warningOrigin, warningIsCurrent }

  // A new clean version reloads only version-bound editors. A dirty editor keeps
  // its original binding, DOM, undo and text until explicitly saved/discarded.
  useEffect(() => {
    if (navigationBlocked || Object.values(dirtyFlags.current).some(Boolean)) return
    const versionId = snapshot?.versionId ?? snapshot?.parentVersionId ?? null
    const versionDigest = snapshot?.versionDigest ?? snapshot?.parentDigest ?? null
    if (activeProjectId !== null && versionId !== null && versionDigest !== null) {
      if (editorVersion?.projectId !== activeProjectId || editorVersion.versionId !== versionId || editorVersion.versionDigest !== versionDigest)
        setEditorVersion({ projectId: activeProjectId, versionId, versionDigest })
    } else if (editorVersion !== null && editorVersion.projectId !== activeProjectId) setEditorVersion(null)
  }, [activeProjectId, snapshot, navigationBlocked, editorVersion])

  const createTimelineClient = useCallback(() => {
    const client = createHostBridgeClient()
    const expected = inputs.current.editorVersion
    const stillBound = () => expected === null || (inputs.current.editorVersion?.versionId === expected.versionId &&
      inputs.current.editorVersion.versionDigest === expected.versionDigest && inputs.current.activeProjectId === expected.projectId)
    return { ...client,
      onTimelineReadResponse: (listener: Parameters<typeof client.onTimelineReadResponse>[0]) => client.onTimelineReadResponse(response => {
        if (!stillBound()) return
        if (response.result.status === 'available' && expected !== null &&
            (response.result.snapshot.projectId !== expected.projectId || response.result.snapshot.baseVersionId !== expected.versionId)) {
          setWarningNotice('タイムラインの基準版が対象版と異なるため、読み込み結果を使用しません。')
          listener({ ...response, result: { status: 'unavailable', snapshot: null } })
          return
        }
        setTimelineSnapshot(response.result.status === 'available' ? response.result.snapshot : null)
        listener(response)
      }),
      onTimelineApplyResponse: (listener: Parameters<typeof client.onTimelineApplyResponse>[0]) => client.onTimelineApplyResponse(response => {
        if (!stillBound() || (response.result.status === 'available' && expected !== null &&
            (response.result.snapshot.projectId !== expected.projectId || response.result.snapshot.baseVersionId !== expected.versionId))) return
        if (response.result.status === 'available') setTimelineSnapshot(response.result.snapshot)
        listener(response)
      }),
    }
  }, [])

  const createPlayerClient = useCallback(() => {
    const client = createHostBridgeClient()
    const bound = { projectId: inputs.current.activeProjectId, jobId: inputs.current.ingestJobId }
    const reads = new Set<string>()
    const seeks = new Map<string, Readonly<{ digest: string; sequence: number; origin: WarningOrigin | null }>>()
    let evidence: PlayerSnapshot | null = null
    const current = () => bound.projectId === inputs.current.activeProjectId && bound.jobId === inputs.current.ingestJobId
    return { ...client,
      sendPlayerRead(requestId: string, jobId: string) { if (!current() || jobId !== bound.jobId) return; reads.add(requestId); client.sendPlayerRead(requestId, jobId) },
      onPlayerReadResponse: (listener: Parameters<typeof client.onPlayerReadResponse>[0]) => client.onPlayerReadResponse(response => {
        if (!current() || !reads.has(response.requestId) || (response.result.status === 'available' && response.result.snapshot.projectId !== bound.projectId)) return
        reads.delete(response.requestId)
        evidence = response.result.status === 'available' ? response.result.snapshot : null
        setPlayerEvidence(evidence !== null && bound.jobId !== null ? { jobId: bound.jobId, snapshot: evidence } : null)
        listener(response)
      }),
      sendPlayerSeek(requestId: string, jobId: string, digest: string, sequence: number) {
        if (!current() || jobId !== bound.jobId || evidence?.evidenceDigest !== digest) return
        seeks.set(requestId, { digest, sequence, origin: inputs.current.warningIsCurrent ? inputs.current.warningOrigin : null })
        client.sendPlayerSeek(requestId, jobId, digest, sequence)
      },
      abandonPlayerRequest(requestId: string) {
        reads.delete(requestId)
        const pending = seeks.get(requestId)
        seeks.delete(requestId)
        if (pending?.origin != null && pending.origin === inputs.current.warningOrigin && inputs.current.warningIsCurrent) {
          setSourceSeekAck({ origin: pending.origin, error: '元動画の移動確認を完了できませんでした。' })
        }
      },
      onPlayerSeekResponse: (listener: Parameters<typeof client.onPlayerSeekResponse>[0]) => client.onPlayerSeekResponse(response => {
        const pending = seeks.get(response.requestId)
        const frame = pending === undefined ? undefined : evidence?.frames[pending.sequence]
        if (!current() || pending === undefined || evidence?.evidenceDigest !== pending.digest ||
            (response.result.status === 'available' && (frame === undefined || !positionMatchesFrame(response.result.position, frame)))) return
        if (pending.origin !== null && (pending.origin !== inputs.current.warningOrigin || !inputs.current.warningIsCurrent)) {
          // Clear the local seek, but never forward its obsolete success/time.
          seeks.delete(response.requestId)
          listener({ ...response, result: { status: 'mismatched', position: null } })
          return
        }
        listener(response)
        const stillPending = seeks.delete(response.requestId)
        if (stillPending && pending.origin !== null && pending.origin === inputs.current.warningOrigin && inputs.current.warningIsCurrent) {
          setSourceSeekAck({ origin: pending.origin, error: response.result.status === 'available' ? null
            : '元動画の指定時刻へ移動できません。先頭への代替移動は行いません。' })
        }
      }),
    }
  }, [])

  function warningContext(target: WorkflowWarningTarget): WorkflowWarningContext {
    return { projectId: activeProjectId, versionId: target.versionId, versionDigest: target.versionDigest, available: true,
      artifacts: result?.artifacts.filter(a => a.versionId === target.versionId && a.versionDigest === target.versionDigest).map(a => ({ artifactId: a.artifact.artifactId })),
      settings: savedSettings }
  }
  function warningReason(w: SourceOnlyResultWarning): string | null {
    if (toWorkflowWarningTarget(w.target) === null) return 'この種類の編集先は提供されていません。別の道具で代用しません。'
    if (!pipelineCurrent) return '現在の実行と成果物を再確認してください。'
    if (w.target.kind !== 'Preview' && snapshot !== null && isPipelineLive(snapshot.phase)) return '処理中は旧成果物の編集先を変更しません。完了後に確認してください。'
    if (w.target.kind === 'Settings' && settings.isDirty && settings.state.projectId !== activeProjectId) return '別の対象の未保存設定を保持しています。先に設定画面で確認してください。'
    if (w.target.kind === 'Settings' && savedSettings !== null && !settings.state.needsRefresh &&
      !savedSettings.definitions.some(d => d.key === w.target.settingKey)) return '指定された設定は、このProjectの設定一覧にありません。別の項目は選択しません。'
    const a = result?.artifacts.find(item => item.artifact.artifactId === w.target.artifactId && item.artifact.contentDigest === w.target.artifactDigest)
    if (a === undefined) return '警告に一致する成果物がありません。'
    if (w.target.kind === 'Preview' && (a.mediaType !== 'video/mp4' || sourceOnlyMediaUrl(result!, a.artifact.artifactId) === null ||
      (w.target.timeBasis === 'Source' && ingestContext.jobId === null))) return '指定された動画・時刻の再生先を提供できません。'
    if (w.target.kind === 'Editor' && (w.target.editorKind === 'Subtitles' || w.target.editorKind === 'Layout') && layoutBusy)
      return '字幕・画角の未適用入力または処理を保持しています。完了・取消後に警告を開き直してください。'
    return null
  }
  const warnings: readonly ResultGalleryWarning[] = resultWarnings.map(w => ({ id: w.id, message: `${w.ruleId}: ${w.status === 'Fail' ? 'FAIL' : 'WARNING'} · ${w.reasonCode}`,
    target: toWorkflowWarningTarget(w.target), available: warningReason(w) === null, unavailableReason: warningReason(w) ?? undefined }))

  function followWarning(target: WorkflowWarningTarget, warningId: string): void {
    const w = resultWarnings.find(item => item.id === warningId)
    if (!resultsActive || settingsOpen || navigation.activeTool !== null || result === null || w === undefined || warningReason(w) !== null || JSON.stringify(toWorkflowWarningTarget(w.target)) !== JSON.stringify(target)) return
    const artifact = result.artifacts.find(a => a.artifact.artifactId === w.target.artifactId && a.artifact.contentDigest === w.target.artifactDigest)
    if (artifact === undefined) return
    setArtifactSelection({ binding: result.binding, artifactId: artifact.artifact.artifactId, digest: artifact.artifact.contentDigest })
    const waiting = target.kind === 'settings' && (savedSettings === null || settings.state.pendingRequest !== null || settings.state.needsRefresh)
    const capturedTarget = Object.freeze(target.kind === 'preview' ? { ...target, time: Object.freeze({ ...target.time }) } : { ...target })
    const origin: WarningOrigin = Object.freeze({ binding: Object.freeze({ ...result.binding }),
      warning: Object.freeze({ ...w, target: Object.freeze({ ...w.target, time: w.target.time === null ? null : Object.freeze({ ...w.target.time }) }) }),
      target: capturedTarget, waitingForSettings: waiting, settingsDigest: target.kind === 'settings' ? savedSettings?.snapshotDigest ?? null : null })
    setWarningOrigin(origin); setSelectedWarningId(w.id); setWarningNotice('警告の対象を確認しています。')
    if (target.kind === 'editor' && target.tool === 'layout') {
      setLayoutIntent(Object.freeze({ target: Object.freeze({ projectId: target.projectId, versionId: target.versionId, versionDigest: target.versionDigest }), warning: origin }))
    }
    if (target.kind === 'preview') setSourcePreview(target.timeBasis === 'source')
    if (waiting) {
      if (settings.state.pendingRequest !== null || !settings.setScope(target.projectId) || !settings.load()) {
        setWarningOrigin(null); setWarningNotice('対象設定を読み込めません。未保存の入力は保持しています。')
      }
      return
    }
    navigate({ type: 'followWarning', target: capturedTarget, warningId, context: warningContext(capturedTarget) })
  }

  function finishWarning(error: string | null, focus?: HTMLElement | null): void {
    if (!warningIsCurrent || !inputs.current.warningIsCurrent || warningOrigin === null || navigation.warningVisit === null) return
    if (error !== null) { failWarning(warningOrigin, error); return }
    if (error === null && focus !== undefined && focus !== null && !focus.closest('[hidden], [inert]')) {
      if (!focus.hasAttribute('tabindex')) focus.tabIndex = -1
      focus.focus({ preventScroll: true })
    }
    setWarningNotice(error ?? '警告の対象へ移動しました。確認後に元の警告へ戻れます。')
    navigate({ type: 'warningDestinationHandled', target: navigation.warningVisit.target })
  }
  function failWarning(origin: WarningOrigin, error: string): void {
    if (inputs.current.warningOrigin !== origin) return
    canceledWarning.current = origin
    inputs.current.warningIsCurrent = false
    inputs.current.warningOrigin = null
    sourceSeekSent.current = null
    setSourceSeekAck(null); setSelectedWarningId(null); setWarningOrigin(null); setWarningNotice(error)
    if (navigation.warningVisit !== null) navigate({ type: 'warningDestinationFailed', target: navigation.warningVisit.target })
  }
  function returnWarning(): void {
    const origin = warningOrigin
    if (origin === null) return
    const settingsUnchanged = origin.settingsDigest === null || origin.settingsDigest === savedSettings?.snapshotDigest
    const layoutUnchanged = origin.target.kind !== 'editor' || origin.target.tool !== 'layout' ||
      (layoutReadReceipt?.intent.warning === origin && layoutReadReceipt.error === null && layout.state.pending === null &&
        (layout.state.status === 'available' || layout.state.status === 'noChange') && layout.state.snapshot !== null && layoutReadReceipt.snapshot !== null &&
        hasTargetSubtitleEvidence(layout.state.snapshot, origin.target, layoutReadReceipt.snapshot))
    const warningId = warningBindingCurrent && settingsUnchanged && layoutUnchanged && navigation.warningVisit?.pending === false
      ? selectReturningWorkflowWarningId(navigation, warningContext(origin.target), resultWarnings.map(w => w.id)) : null
    setSelectedWarningId(warningId)
    if (warningId !== null) pendingFocus.current = [...(workspace.current?.querySelectorAll<HTMLElement>('[data-warning-id]') ?? [])].find(element => element.dataset.warningId === warningId) ?? null
    setWarningNotice(warningId === null ? '対象の版・設定または結果が変わったため、元の警告への選択復元を中止しました。' : null)
    navigate({ type: 'returnFromWarning' }); setWarningOrigin(null)
  }

  useEffect(() => {
    if (!warningIsCurrent || warningOrigin === null) return
    if (warningOrigin.waitingForSettings && settings.state.pendingRequest === null) {
      if (savedSettings === null || settings.state.needsRefresh || !savedSettings.definitions.some(d => d.key === warningOrigin.warning.target.settingKey)) {
        failWarning(warningOrigin, 'このProjectの対象設定を提供できません。別の設定は選択しません。')
        return
      }
      const origin = { ...warningOrigin, waitingForSettings: false, settingsDigest: savedSettings.snapshotDigest }
      setWarningOrigin(origin)
      navigate({ type: 'followWarning', target: origin.target, warningId: origin.warning.id, context: warningContext(origin.target) })
    }
  }, [warningOrigin, warningIsCurrent, savedSettings, settings.state.pendingRequest, settings.state.needsRefresh])

  useEffect(() => {
    if (warningOrigin?.target.kind !== 'editor' || warningOrigin.target.tool !== 'layout' ||
      layoutReadReceipt?.intent.warning !== warningOrigin || layoutReadReceipt.error !== null ||
      layout.state.lastCompletedMethod !== 'layout.apply' || layout.state.lastVersionChange == null ||
      !sameEditorVersion(layout.state.target, layout.state.lastVersionChange.created) ||
      layout.state.snapshot === null || layoutSnapshotMatchesTarget(layout.state.snapshot, warningOrigin.target)) return
    setSelectedWarningId(null)
    setWarningNotice('字幕・テロップの編集で子版へ進みました。元の警告と同じ版としては扱いません。')
  }, [warningOrigin, layoutReadReceipt, layout.state])

  useEffect(() => {
    const visit = navigation.warningVisit
    if (!warningIsCurrent || warningOrigin === null || !visit?.pending) return
    const target = warningOrigin.target
    if (target.kind === 'settings') {
      if (settingsOpen && savedSettings?.definitions.some(d => d.key === target.key)) finishWarning(null)
    } else if (target.kind === 'evidence' && toolActive('evidence')) finishWarning(null, workspace.current?.querySelector('#workflow-result-evidence'))
    else if (target.kind === 'editor' && toolActive(target.tool)) {
      if (target.tool === 'timeline' && timelineSnapshot !== null) {
        if (timelineSnapshot.projectId !== target.projectId || timelineSnapshot.baseVersionId !== target.versionId || editorVersion?.versionDigest !== target.versionDigest)
          finishWarning('タイムラインが警告の対象版に一致しません。未送信の編集は保持しています。')
        else finishWarning(null, workspace.current?.querySelector('.timeline-canvas'))
      } else if (target.tool === 'layout' && layoutReadReceipt?.intent === layoutIntent && layoutReadReceipt?.intent.warning === warningOrigin) {
        if (layoutReadReceipt.error !== null) finishWarning(layoutReadReceipt.error)
        else if (layout.state.pending !== null) return
        else if (layout.state.status !== 'available' || layout.state.lastCompletedMethod !== 'layout.read' ||
          !sameEditorVersion(layout.state.target, target) || layout.state.snapshot === null || !layoutSnapshotMatchesTarget(layout.state.snapshot, target))
          finishWarning('字幕・画角の編集元が対象版と一致しないか、提供されていません。入力は保持しています。')
        else if (layoutReadReceipt.snapshot === null || !hasTargetSubtitleEvidence(layout.state.snapshot, target, layoutReadReceipt.snapshot)) finishWarning('対象版に結び付いた字幕 cue を確認できません。')
        else if (warningOrigin.warning.target.editorKind === 'Layout' && !layoutSupportsManualEditing(layout.state.snapshot))
          finishWarning('対象版は字幕専用です。画角編集の移動先としては扱いません。')
        else {
          const focus = workspace.current?.querySelector<HTMLElement>(warningOrigin.warning.target.editorKind === 'Subtitles' ? '#subtitle-editor-title' : '#layout-title')
          if (focus === undefined || focus === null || focus.closest('[hidden], [inert]')) finishWarning('対象版の編集欄を表示できません。')
          else finishWarning(null, focus)
        }
      }
    } else if (target.kind === 'preview' && target.timeBasis === 'source' && resultsActive && playerEvidence !== null) {
      const a = result?.artifacts.find(item => item.artifact.artifactId === target.artifactId)
      if (playerEvidence.jobId !== ingestContext.jobId || playerEvidence.snapshot.projectId !== target.projectId || playerEvidence.snapshot.sourceAssetId !== a?.source.artifactId) {
        finishWarning('元動画のプレビュー根拠が警告の対象素材に一致しません。'); return
      }
      if (sourceSeekAck?.origin === warningOrigin) { finishWarning(sourceSeekAck.error, workspace.current?.querySelector('.video-stage')); return }
      if (sourceSeekSent.current === warningOrigin) return
      if (findProxyFrameAtSourceTime(playerEvidence.snapshot, target.time) === null) { finishWarning('元動画の指定時刻が範囲外です。'); return }
      sourceSeekSent.current = warningOrigin
      if (requestSourceSeek({ projectId: target.projectId, sourceTime: target.time }) !== 'requested') finishWarning('元動画の指定時刻への移動を受け付けられませんでした。')
    }
  }, [navigation.warningVisit, warningIsCurrent, warningOrigin, settingsOpen, savedSettings, timelineSnapshot, editorVersion, layout.state, layoutIntent, layoutReadReceipt,
    playerEvidence, sourceSeekAck, resultsActive, ingestContext.jobId, result, requestSourceSeek])

  useEffect(() => {
    if (warningOrigin === null) return
    if (!warningBindingCurrent || !pipelineCurrent) { failWarning(warningOrigin, '対象の実行・版・成果物が変わったか未確認になったため、警告からの移動を中止しました。'); return }
    if (!warningIsCurrent) return
    if (!warningOrigin.waitingForSettings && !navigation.warningVisit?.pending) return
    const timer = setTimeout(() => {
      failWarning(warningOrigin, '警告の移動先を時間内に確認できませんでした。推測した対象へは移動しません。')
    }, 30_000)
    return () => clearTimeout(timer)
  }, [warningOrigin, warningIsCurrent, warningBindingCurrent, pipelineCurrent, navigation.warningVisit?.pending])

  // Read the past-version list once the project's draft is finalized and the
  // results screen is visible. It is a read; it accepts no work.
  useEffect(() => {
    if (!resultsActive || navigation.activeTool !== null || activeProjectId === null ||
        draft?.isFinalized !== true || pipeline.state.versionsStatus !== 'idle') {
      return
    }
    pipeline.readVersions()
  }, [resultsActive, navigation.activeTool, activeProjectId, draft?.isFinalized, pipeline])

  const regenerationReason = sourceDirty || navigationBlocked ? '未保存の編集があります。保存または取消後に再生成できます。'
    : settings.state.pendingRequest !== null || settings.state.needsRefresh ? '保存済み設定の確認を完了してください。'
      : savedSettings === null ? '設定を開いて、次に作成するJobの保存済み設定を確認してください。'
        : !pipelineCurrent || snapshot === null || snapshot.versionId === null || snapshot.versionDigest === null ? '再生成の親版を確認できません。'
          : isPipelineLive(snapshot.phase) ? '自動編集の完了後に再生成できます。'
            : snapshot.phase === 'ReconciliationRequired' || snapshot.phase === 'ReselectionRequired' ? '先に処理の整合性・元動画を確認してください。' : null
  const regenerationParams: PipelineRegenerationParams | null = savedSettings !== null && activeProjectId !== null && snapshot?.versionId != null && snapshot.versionDigest !== null
    // The backend captures subtitle journals for this MAIN rendered parent and
    // its shorts. An adopted subtitle child must never become this parent.
    ? { projectId: activeProjectId, expectedVersionId: snapshot.versionId, expectedVersionDigest: snapshot.versionDigest,
      expectedSettingsRevision: savedSettings.revision, expectedSettingsSnapshotDigest: savedSettings.snapshotDigest } : null
  function regenerate(params: PipelineRegenerationParams): boolean {
    return resultsActive && navigation.activeTool === null && !Object.values(dirtyFlags.current).some(Boolean) &&
      regenerationReason === null && JSON.stringify(params) === JSON.stringify(regenerationParams) && pipeline.regenerate(params)
  }
  // Restoring reuses the regeneration preconditions: it accepts a new root and
  // must not run while the workspace holds unsaved edits or an unconfirmed setting.
  const restoreReason = regenerationReason !== null ? regenerationReason
    : savedSettings === null || activeProjectId === null ? '保存済み設定を確認できません。'
      : pipeline.state.workingCopyRevision === null ? '過去版の一覧を取得してから作り直せます。' : null
  // FR-061.8: the same preconditions as regeneration, in Job wording. A running first Job has
  // no version yet, but its state is known, so it reports the running reason (AC-036.7).
  const jobCreationReason = sourceDirty || navigationBlocked ? '未保存の編集があります。保存または取消後にJobを作成できます。'
    : settings.state.pendingRequest !== null || settings.state.needsRefresh ? '設定の確認を完了してください。'
      : savedSettings === null ? '「次に作成するJobの設定を開く」で設定を確認すると、Jobを作成できます。'
        : !pipelineCurrent || snapshot === null ? '最新のJobの状態を確認できません。「ジョブの状態の確認」で状態を確認してください。'
          : isPipelineLive(snapshot.phase) ? '実行中のJobが終わると、新しいJobを作成できます（Jobは1つずつ順番に実行します）。'
            : snapshot.versionId === null || snapshot.versionDigest === null ? '最新のJobの状態を確認できません。「ジョブの状態の確認」で状態を確認してください。'
              : snapshot.phase === 'ReconciliationRequired' || snapshot.phase === 'ReselectionRequired' ? '先に最新のJobの整合性・元動画を確認してください。' : null
  // FR-061.7: a new Job is a regeneration from the latest Job, sent from the source screen.
  // FR-136: the production template picked for the next Job (null = none; the default output is unchanged).
  const [styleTemplateId, setStyleTemplateId] = useState<string | null>(null)
  function createJob(params: PipelineRegenerationParams, templateId: string | null = null, extras: PipelineRegenerationExtras = {}): boolean {
    return screen === 'source' && draft?.isFinalized === true && !Object.values(dirtyFlags.current).some(Boolean) &&
      jobCreationReason === null && regenerationReason === null && JSON.stringify(params) === JSON.stringify(regenerationParams) && pipeline.regenerate(params, templateId, extras)
  }
  const jobs = pipeline.state.versionsStatus === 'available' ? pipeline.state.versions : null

  // FR-061.13: copy one Job's varying settings into 「次に作成するJob」 by saving them one by one.
  const [settingsCopy, setSettingsCopy] = useState<Readonly<{
    jobNumber: number; keys: readonly string[]; index: number; sentKey: string | null
  }> | null>(null)
  const [settingsCopyStatus, setSettingsCopyStatus] = useState<string | null>(null)
  const settingsCopyReason = sourceDirty || dirtyTools.some(item => item.dirty) || layoutBusy ? '未保存の編集があります。保存または取消後に写せます。'
    : settings.isDirty ? '未保存の設定があります。保存または破棄してから写してください。'
      : settings.state.pendingRequest !== null || settings.state.needsRefresh || settings.state.error !== null ? '設定の確認を完了してください。'
        : savedSettings === null ? '「次に作成するJobの設定を開く」で設定を確認すると、Jobの設定を写せます。' : null
  function copyJobSettings(entry: PipelineVersionEntry, jobNumber: number): void {
    if (settingsCopy !== null || settingsCopyReason !== null || savedSettings === null) return
    const changed = editableJobSettings(entry).filter(item => {
      const saved = savedSettings.values.find(value => value.key === item.key)?.value
      return saved !== undefined && JSON.stringify(saved) !== JSON.stringify(item.value)
    })
    if (changed.length === 0) { setSettingsCopyStatus(`Job ${jobNumber}の設定は、すでに「次に作成するJob」と同じです。`); return }
    for (const item of changed) settings.changeDraft(item.key, item.value)
    setSettingsCopyStatus(null)
    setSettingsCopy({ jobNumber, keys: changed.map(item => item.key), index: 0, sentKey: null })
  }
  useEffect(() => {
    const run = settingsCopy
    if (run === null || settings.state.pendingRequest !== null) return
    const finish = (status: string) => { setSettingsCopy(null); setSettingsCopyStatus(status) }
    if (run.sentKey !== null) {
      if (settings.state.error !== null || Object.hasOwn(settings.state.draftValues, run.sentKey)) {
        settings.discardDrafts()
        finish(`Job ${run.jobNumber}の設定を写せませんでした（${run.index} / ${run.keys.length}項目を保存済み）。設定を開いて確認してください。`)
        return
      }
      const index = run.index + 1
      if (index >= run.keys.length) { finish(`Job ${run.jobNumber}の設定を「次に作成するJob」に保存しました（${run.keys.length}項目）。`); return }
      setSettingsCopy({ ...run, index, sentKey: null })
      return
    }
    const key = run.keys[run.index]!
    if (!settings.save(key)) {
      settings.discardDrafts()
      finish(`Job ${run.jobNumber}の設定を写せませんでした（${run.index} / ${run.keys.length}項目を保存済み）。設定を開いて確認してください。`)
      return
    }
    setSettingsCopy({ ...run, sentKey: key })
  }, [settingsCopy, settings])
  useEffect(() => { setSettingsCopyStatus(null) }, [activeProjectId])
  const jobSettingsCopy = draft?.isFinalized === true ? {
    disabledReason: settingsCopy !== null ? null : settingsCopyReason,
    busy: settingsCopy !== null,
    status: settingsCopy !== null ? `Job ${settingsCopy.jobNumber}の設定を保存しています（${settingsCopy.index} / ${settingsCopy.keys.length}項目）。` : settingsCopyStatus,
    onCopy: copyJobSettings,
  } : undefined
  const nextJobNumber = jobs === null ? null : jobs.length + 1

  // FR-061.5: re-read this project's Jobs when the source or job tab is shown and
  // whenever the latest Job changes; one read per (screen, execution, phase).
  const jobListKey = useRef<string | null>(null)
  const jobsVisible = (screen === 'source' || screen === 'processing') && draft?.isFinalized === true && activeProjectId !== null
  useEffect(() => {
    if (!jobsVisible) { jobListKey.current = null; return }
    const key = `${activeProjectId}:${screen}:${snapshot?.executionId ?? 'none'}:${snapshot?.phase ?? 'none'}`
    // Wait for the first state of the latest Job, so the list is read once, not twice.
    if (snapshot === null && (pipeline.state.status === 'idle' || pipeline.state.status === 'loading')) return
    if (jobListKey.current === key || pipeline.state.versionsStatus === 'loading') return
    if (pipeline.readVersions()) jobListKey.current = key
  }, [jobsVisible, activeProjectId, screen, snapshot?.executionId, snapshot?.phase, pipeline])
  function restore(targetVersionId: string, targetVersionDigest: string): void {
    const workingCopyRevision = pipeline.state.workingCopyRevision
    if (!resultsActive || navigation.activeTool !== null || Object.values(dirtyFlags.current).some(Boolean) ||
        restoreReason !== null || savedSettings === null || activeProjectId === null || workingCopyRevision === null) return
    pipeline.restore({
      projectId: activeProjectId, targetVersionId, targetVersionDigest,
      expectedWorkingCopyRevision: workingCopyRevision,
      expectedSettingsRevision: savedSettings.revision,
      expectedSettingsSnapshotDigest: savedSettings.snapshotDigest,
    })
  }

  useEffect(() => {
    const requested = pendingFocus.current
    pendingFocus.current = null
    if (requested === null) return
    const element = typeof requested === 'string'
      ? workspace.current?.querySelector<HTMLElement>(`#${requested}`) : requested
    // Run after child entry effects (not after responses), including quick start.
    if (element?.isConnected && !element.closest('[hidden], [inert]')) {
      element.focus({ preventScroll: true })
    } else if (navigation.returnToStep === null) {
      // A saved/discarded dirty notice may remove its own settings opener.
      workspace.current?.querySelector<HTMLElement>(`#workflow-tab-${navigation.step}`)
        ?.focus({ preventScroll: true })
    }
  }, [navigation])

  function selectStep(step: WorkflowStep): void {
    // A hidden tab's synthetic click must not bypass SettingsPanel's dirty guard.
    if (settingsOpen) return
    setWarningOrigin(null); setWarningNotice(null); setSelectedWarningId(null)
    pendingFocus.current = `workflow-tab-${step}`
    navigate({ type: 'selectStep', step })
  }

  // The single path back to the project list; it closes the open draft.
  const returnToProjectList = projectState.draft !== null && !settingsOpen && projectState.pending === null && !navigationBlocked
  function requestProjectList(): void {
    if (!returnToProjectList) return
    if (sourceDirty && !window.confirm('未保存の変更があります。変更を破棄して一覧へ戻りますか？')) return
    closeDraft()
    selectStep('source')
  }

  // FR-061.1: closing or deleting the open project from a project-only tab shows the list.
  useEffect(() => {
    if (navigation.returnToStep === null && navigation.step !== shownStep) navigate({ type: 'selectStep', step: shownStep })
  }, [navigation.returnToStep, navigation.step, shownStep])

  // FR-042: a successful 「編集を開始」 (project.finalizeDraft) moves to 自動編集 once.
  const finalizeRequested = useRef(false)
  useEffect(() => {
    if (projectState.pending?.method === 'project.finalizeDraft') { finalizeRequested.current = true; return }
    if (!finalizeRequested.current) return
    finalizeRequested.current = false
    if (projectState.error === null && draft?.isFinalized === true && navigation.step !== 'processing') selectStep('processing')
  })

  function selectTool(tool: WorkflowTool | null): void {
    if (settingsOpen) return
    if (tool === 'layout') {
      const target = selectedArtifact !== null && activeProjectId !== null
        ? { projectId: activeProjectId, versionId: selectedArtifact.versionId, versionDigest: selectedArtifact.versionDigest } : null
      if (target === null) {
        setWarningOrigin(null); setSelectedWarningId(null)
        setWarningNotice('成果物の対象版・digest を確認できません。別の版は読み込みません。')
        pendingFocus.current = 'workflow-tool-layout'
        navigate({ type: 'selectTool', tool })
        return
      }
      if (!sameEditorVersion(layoutIntent?.target, target) || layoutIntent?.warning !== null ||
        !resultsActive || navigation.activeTool !== 'layout') {
        if (layoutBusy) {
          setWarningNotice('未適用入力または処理があるため、字幕・画角は元の版を保持しています。完了・取消後に対象を選び直してください。')
          pendingFocus.current = 'workflow-tool-layout'
          navigate({ type: 'selectTool', tool })
          setWarningOrigin(null); setSelectedWarningId(null)
          return
        }
        setLayoutIntent(Object.freeze({ target: Object.freeze(target), warning: null }))
      }
    }
    setWarningOrigin(null); setWarningNotice(null); setSelectedWarningId(null)
    pendingFocus.current = `workflow-tool-${tool ?? 'overview'}`
    navigate({ type: 'selectTool', tool })
  }

  function openSettings(opener: HTMLElement): void {
    if (settingsOpen) return
    setWarningOrigin(null); setWarningNotice(null); setSelectedWarningId(null)
    settingsOpener.current = opener
    navigate({ type: 'openSettings' })
  }

  function closeSettings(): void {
    // Only the panel invokes this, after its own save/discard/cancel confirmation.
    if (warningOrigin !== null && navigation.warningVisit?.target.kind === 'settings') { returnWarning(); return }
    pendingFocus.current = settingsOpener.current ?? `workflow-tab-${navigation.step}`
    navigate({ type: 'closeSettings' })
  }

  return (
    <main ref={workspace} className="app-shell workflow-shell" aria-labelledby="app-title" data-workflow-screen={screen}>
      <a className="skip-link" href="#project-content">メインコンテンツへ移動</a>
      <header className="workflow-header">
        <div className="brand-lockup">
          <span className="brand-mark" aria-hidden="true">H</span>
          <div><p className="eyebrow">Hubらじお</p><h1 id="app-title">Movie Creator</h1></div>
        </div>
        <div className="workflow-current-project">
          <p className="workflow-project-name" aria-label="現在の動画">{draft?.displayName ?? draft?.contentName ?? '元動画を選択してください'}</p>
          <p className="workflow-project-state">
            {draft === null ? '未選択' : draft.isFinalized ? 'Project作成済み' : '確認待ち'}
            {(sourceDirty || navigationBlocked) && ' · 未保存の入力あり'}
          </p>
        </div>
        <div className="workflow-header-controls">
          {connectionStatus}
          <div className="workflow-header-actions" hidden={settingsOpen} inert={settingsOpen}>
            <button type="button" disabled={!returnToProjectList} onClick={requestProjectList} aria-controls="workflow-panel-source">Project一覧へ戻る</button>
            <button id="workflow-open-settings" type="button" onClick={event => openSettings(event.currentTarget)} aria-controls="workflow-settings">
              <AppIcon name="settings" />設定
            </button>
          </div>
        </div>
      </header>

      <div className="workflow-navigation" hidden={settingsOpen} inert={settingsOpen}>
        <WorkflowTabs selectedStep={shownStep} onSelect={selectStep}
          steps={projectOpen ? workflowSteps : projectListSteps} labels={projectOpen ? undefined : projectListStepLabels} />
      </div>

      <div id="project-content" className="workflow-body" tabIndex={-1}>
        {warningNotice !== null && <div className="workflow-dirty-notice" role="status">
          <span>{warningNotice}</span>
          {warningOrigin !== null && !settingsOpen && <button type="button" onClick={returnWarning}>元の警告へ戻る</button>}
        </div>}
        <WorkflowSurface id="workflow-panel-source" labelledBy="workflow-tab-source" role="tabpanel" className="workflow-screen" active={screen === 'source'}>
          {navigationBlocked && (
            <div className="workflow-dirty-notice" role="group" aria-label="未保存の編集へ戻る">
              <span>編集内容を保持しています。</span>
              {dirtyTools.filter(item => item.dirty).map(({ tool }) => (
                <button key={tool} type="button" onClick={() => selectTool(tool)}>{toolLabels[tool]}の編集へ戻る</button>
              ))}
              {settingsDirty && <button type="button" onClick={event => openSettings(event.currentTarget)}>設定の編集へ戻る</button>}
            </div>
          )}
          <ProjectWorkspace active={screen === 'source'} onDirtyChange={reportDirty.source} navigationBlocked={navigationBlocked} />
          {draft?.isFinalized === true && <div className="workflow-project-jobs">
            <JobCreationAction nextJobNumber={nextJobNumber} params={regenerationParams} disabledReason={jobCreationReason}
              pending={pipeline.state.pendingMethod !== null} active={screen === 'source'}
              onOpenSettings={() => {
                const opener = workspace.current?.querySelector<HTMLElement>('#workflow-open-settings')
                if (opener) openSettings(opener)
              }}
              styleTemplateId={styleTemplateId} onStyleTemplateChange={setStyleTemplateId} projectId={activeProjectId}
              onConfirm={createJob} />
            <ProjectJobList versions={jobs} status={pipeline.state.versionsStatus} copy={jobSettingsCopy} projectId={activeProjectId} />
          </div>}
        </WorkflowSurface>

        <WorkflowSurface id="workflow-panel-processing" labelledBy="workflow-tab-processing" role="tabpanel" className="workflow-screen" active={screen === 'processing'}>
          {!projectOpen && <AllProjectJobsPanel active={screen === 'processing'} projects={projectState.projects}
            onOpenJob={draftId => { if (projectState.pending === null) loadDraft(draftId) }}
            openDisabledReason={projectState.pending !== null ? 'Projectの操作を処理しています。完了後に開けます。' : null} />}
          <div hidden={!projectOpen}>
          {/* Visibility belongs to the surface; monitoring must not stop on tab changes. */}
          <PipelineProgress active={screen === 'processing' && projectOpen} />
          {projectOpen && snapshot === null && <p className="workflow-boundary-note" role="status">
            {draft?.isFinalized
              ? 'Projectは作成済みですが、編集処理の開始は確認できていません。ホストの状態を確認してください。'
              : '元動画を選択して開始を確認すると、自動編集の状態を取得します。'}
          </p>}
          {draft?.isFinalized === true && <ProjectJobList versions={jobs} status={pipeline.state.versionsStatus} copy={jobSettingsCopy} projectId={activeProjectId} />}
          {projectOpen && <div className="workflow-return"><button type="button" onClick={() => selectStep('source')}>元動画へ戻る</button></div>}
          </div>
        </WorkflowSurface>

        <WorkflowSurface id="workflow-panel-results" labelledBy="workflow-tab-results" role="tabpanel" className="workflow-screen workflow-results-screen" active={resultsActive}>
          {pipeline.state.status === 'invalid' && <p className="workflow-boundary-note" role="status">成果物の検証に失敗しました（invalid）。保持された参照は診断用です。再確認が成功するまで媒体プレビュー・再生成は利用できません。</p>}
          {result === null && <p className="workflow-boundary-note" role="status">実成果物は未取得です。作成予定や処理完了だけでは生成済み・保存済みとは表示しません。</p>}
          {result !== null && pipeline.state.needsRefresh && <p className="workflow-boundary-note" role="status">
            最新状態を確認できないため、直前に検証済みの結果を保持表示しています。変更操作の前に「ジョブの状態の確認」画面で状態を再確認してください。
          </p>}
          <div className="workflow-tools" role="group" aria-label="仕上がりの道具">
            <button id="workflow-tool-overview" type="button" aria-pressed={navigation.activeTool === null} aria-controls="workflow-tool-panel-overview" onClick={() => selectTool(null)}>Jobの仕上がり</button>
            {workflowTools.map(tool => (
              <button id={`workflow-tool-${tool}`} key={tool} type="button" aria-pressed={navigation.activeTool === tool} aria-controls={`workflow-tool-panel-${tool}`} onClick={() => selectTool(tool)}>{toolLabels[tool]}</button>
            ))}
          </div>
          <ArrangeablePanels screenId="results" preset={editorResultsPreset} className={`workflow-results-content${comparing ? ' is-comparing' : ''}`} panels={[
            { id: 'preview', label: 'プレビュー', suppressed: comparing, children: (
            <div className="workflow-preview" hidden={comparing}>
              {result !== null && selectedArtifact !== null && <>
                <div role="group" aria-label="プレビューの対象">
                  <button type="button" aria-pressed={!sourcePreview} onClick={() => setSourcePreview(false)}>生成された成果物</button>
                  <button type="button" aria-pressed={sourcePreview} onClick={() => setSourcePreview(true)}>元動画（代理）</button>
                </div>
                <WorkflowSurface id="workflow-generated-preview" className="workflow-generated-preview" active={resultsActive && showGenerated && !comparing}>
                  <GeneratedResultPreview result={result} artifact={selectedArtifact} active={resultsActive && showGenerated && !comparing} seek={generatedSeek}
                    onHandled={(intent, error) => { if (intent === generatedSeek && warningIsCurrent) finishWarning(error) }} />
                </WorkflowSurface>
              </>}
              <PlayerPanel createClient={createPlayerClient} context={ingestContext} active={resultsActive && !showGenerated && !comparing} previewModes={previewModes} onPositionChange={setPlayerPosition} onSelectionChange={setPlayerSelection} />
              <details className="workflow-preview-options" hidden={showGenerated} inert={showGenerated}>
                <summary>表示モード</summary>
                <PreviewModeControls context={ingestContext} active={resultsActive && !showGenerated && !comparing} selection={selection} />
              </details>
            </div>) },
            { id: 'tools', label: '仕上がりの内容', children: (
            <div className="workflow-tool-panels">
              <WorkflowSurface id="workflow-tool-panel-overview" labelledBy="workflow-tool-overview" role="region" className="workflow-tool-panel" active={resultsActive && navigation.activeTool === null}>
                {summary === null && result === null ? (
                  <div className="workflow-empty"><h2>仕上がり一覧</h2><p>予定の成果物は未取得です。元動画を選ぶと、固定された予定を確認できます。</p><button type="button" onClick={() => selectStep('source')}>元動画を選ぶ画面へ</button></div>
                ) : (() => {
                  const gallery = <ResultGallery presentation={presentation} warnings={warnings} selectedWarningId={selectedWarningId} onFollowWarning={followWarning}
                    bookendStitch={bookendStitch}
                    onSelectArtifact={artifactId => {
                      const a = result?.artifacts.find(item => item.artifact.artifactId === artifactId)
                      if (!resultsActive || settingsOpen || navigation.activeTool !== null || result === null || a === undefined || sourceOnlyMediaUrl(result, artifactId) === null) return
                      setArtifactSelection({ binding: result.binding, artifactId, digest: a.artifact.contentDigest }); setSourcePreview(false)
                    }} />
                  return jobBrowserShown ? <JobResultsBrowser
                    versions={pipeline.state.versionsStatus === 'notFound' ? [] : pipeline.state.versions}
                    unavailableReason={pipeline.state.versionsStatus === 'idle' || pipeline.state.versionsStatus === 'loading' ||
                      pipeline.state.versionsStatus === 'available' || pipeline.state.versionsStatus === 'notFound'
                      ? null : 'Jobの一覧を取得できませんでした。もう一度お試しください。'}
                    projectId={activeProjectId}
                    pending={pipeline.state.pendingMethod !== null}
                    disabledReason={restoreReason}
                    onRestore={restore}
                    view={resultView}
                    onViewChange={setResultView}
                    currentResults={gallery} /> : gallery
                })()}
                {draft?.isFinalized && <SettingsRegenerationAction params={regenerationParams} disabledReason={regenerationReason}
                  active={resultsActive && navigation.activeTool === null} pending={pipeline.state.pendingMethod !== null} onConfirm={regenerate} />}
              </WorkflowSurface>
              <WorkflowSurface id="workflow-tool-panel-analysis" labelledBy="workflow-tool-analysis" role="region" className="workflow-tool-panel" active={toolActive('analysis')}>
                <div className="workflow-readonly" onClick={event => {
                  if (event.target instanceof Element && event.target.closest('a[href="#transcript-title"]')) {
                    event.preventDefault()
                    selectTool('transcript')
                  }
                }}>
                  <AnalysisPanel context={registrationContext} active={toolActive('analysis')} onDirtyChange={reportDirty.analysis} />
                </div>
              </WorkflowSurface>
              <WorkflowSurface id="workflow-tool-panel-transcript" labelledBy="workflow-tool-transcript" role="region" className="workflow-tool-panel" active={toolActive('transcript')}>
                <TranscriptPanel context={registrationContext} active={toolActive('transcript')} onDirtyChange={reportDirty.transcript} />
              </WorkflowSurface>
              <WorkflowSurface id="workflow-tool-panel-timeline" labelledBy="workflow-tool-timeline" role="region" className="workflow-tool-panel" active={toolActive('timeline')}>
                <TimelineEditor key={editorVersion === null ? 'unbound' : `${editorVersion.projectId}:${editorVersion.versionId}:${editorVersion.versionDigest}`}
                  createClient={createTimelineClient} context={projectContext} active={toolActive('timeline')} playerPosition={position} onDirtyChange={reportDirty.timeline} />
              </WorkflowSurface>
              <WorkflowSurface id="workflow-tool-panel-layout" labelledBy="workflow-tool-layout" role="region" className="workflow-tool-panel" active={toolActive('layout')}>
                <BoundLayoutPanel projectId={activeProjectId} intent={layoutIntent} onRead={setLayoutReadReceipt}
                  active={toolActive('layout')} previewModes={previewModes} onDirtyChange={reportDirty.layout} />
              </WorkflowSurface>
              <WorkflowSurface id="workflow-tool-panel-thumbnails" labelledBy="workflow-tool-thumbnails" role="region" className="workflow-tool-panel" active={toolActive('thumbnails')}>
                <ThumbnailPanel context={projectContext} active={toolActive('thumbnails')} selection={selection} previewReduced={previewModes.thumbnailReduced} />
              </WorkflowSurface>
              <WorkflowSurface id="workflow-tool-panel-compare" labelledBy="workflow-tool-compare" role="region" className="workflow-tool-panel" active={toolActive('compare')}>
                <ComparePanel context={comparisonContext} active={toolActive('compare')} />
              </WorkflowSurface>
              <WorkflowSurface id="workflow-tool-panel-review" labelledBy="workflow-tool-review" role="region" className="workflow-tool-panel" active={toolActive('review')}>
                <BoundReviewPanel binding={editorVersion} context={versionContext} active={toolActive('review')} onDirtyChange={reportDirty.review} />
              </WorkflowSurface>
              <WorkflowSurface id="workflow-tool-panel-evidence" labelledBy="workflow-tool-evidence" role="region" className="workflow-tool-panel" active={toolActive('evidence')}>
                {selectedArtifact !== null && <section aria-labelledby="workflow-result-evidence" data-evidence-artifact={selectedArtifact.artifact.artifactId}>
                  <h3 id="workflow-result-evidence" tabIndex={-1}>成果物の評価根拠</h3>
                  <p>版: <code>{selectedArtifact.versionId}</code> · <code>{selectedArtifact.versionDigest}</code></p>
                  <p>成果物: <code>{selectedArtifact.artifact.artifactId}</code> · <code>{selectedArtifact.artifact.contentDigest}</code></p>
                  <ul>{selectedArtifact.evidence.map(e => <li key={e.kind}>{e.kind}: <code>{e.artifact.artifactId}</code> · <code>{e.artifact.contentDigest}</code></li>)}</ul>
                </section>}
                <BoundDeliveryPanel binding={editorVersion} context={versionContext} active={toolActive('evidence') && result === null} />
              </WorkflowSurface>
            </div>) },
          ]} />
        </WorkflowSurface>

        <WorkflowSurface id="workflow-settings" className="workflow-screen workflow-settings-screen" active={settingsOpen}>
          <SettingsPanel isActive={settingsOpen} activeProjectId={activeProjectId} projectOpen={projectOpen} onClose={closeSettings} onDirtyChange={reportDirty.settings}
            initialSettingKey={warningIsCurrent && warningOrigin?.target.kind === 'settings' && !warningOrigin.waitingForSettings ? warningOrigin.target.key : null} />
        </WorkflowSurface>
      </div>
    </main>
  )
}

/** One owner for branch-head/editor and exact/warning reads. Panel autoload/invalidation is disabled here: a
 * route change must not invalidate an already accepted native mutation. */
function BoundLayoutPanel({ projectId, intent, onRead, ...props }: LayoutPanelProps & {
  readonly projectId: string | null
  readonly intent: LayoutOpenIntent | null
  readonly onRead: (receipt: LayoutReadReceipt) => void
}) {
  const api = useLayoutState()
  const [adopted, setAdopted] = useState<Readonly<{ intent: LayoutOpenIntent; target: LayoutVersionContext; snapshot: LayoutSnapshot | null }> | null>(null)
  const dirty = useRef(false)
  const requested = useRef<LayoutOpenIntent | null>(null)
  const wasActive = useRef(false)
  const attempt = useRef<Readonly<{ intent: LayoutOpenIntent; target: LayoutVersionContext; latestInBranch: boolean; before: typeof api.state }> | null>(null)
  const submitted = useRef<Readonly<{ intent: LayoutOpenIntent; before: typeof api.state; snapshot: LayoutSnapshot; operation: LayoutOperation }> | null>(null)
  const s = api.state.snapshot
  const base = adopted?.intent === intent ? adopted?.target ?? null : intent?.target ?? null
  const accepted = adopted?.intent === intent ? adopted?.snapshot ?? null : null
  const read = attempt.current
  const readCompleted = read !== null && read.intent === intent && api.state !== read.before && api.state.pending === null
  const freshRead = readCompleted && api.state.status === 'available' && api.state.error === null &&
    api.state.lastCompletedMethod === 'layout.read' && s !== null && s !== read.before.snapshot
  const durable = api.state.durableSubtitles
  const latestRead = freshRead && read.latestInBranch && sameEditorVersion(read.target, read.intent.target) &&
    sameEditorVersion(api.state.branch?.root, read.intent.target) && durable != null &&
    durable.rootVersionId === read.intent.target.versionId && durable.headVersionDigest === read.intent.target.versionDigest &&
    durable.sequence >= (sameEditorVersion(read.before.branch?.root, read.intent.target) ? read.before.branch?.sequence ?? 0 : 0) &&
    layoutSnapshotMatchesDurableHead(s, durable)
  const exactRead = freshRead && layoutSnapshotMatchesTarget(s, read.target) &&
    (read.latestInBranch ? durable == null && (read.before.branch?.sequence ?? 0) === 0
      : sameEditorVersion(base, read.target) && sameEditorVersion(api.state.target, read.target))
  // A first pinned read is authoritative for the registered snapshot, including
  // mixed revision versions. Later reads of that version must retain its cues.
  // A coherent latest head can advance versions; a same-version reread must
  // still preserve the previously accepted mixed-cue evidence (A -> B -> A).
  const readAccepted = (exactRead || latestRead) && hasTargetSubtitleEvidence(s, layoutSnapshotVersion(s),
    accepted !== null && layoutSnapshotMatchesTarget(s, layoutSnapshotVersion(accepted)) ? accepted : s)
  const candidate = submitted.current
  const change = api.state.lastVersionChange
  const childAccepted = intent !== null && requested.current === intent && adopted?.intent === intent &&
    base !== null && s !== null && api.state.pending === null && api.state.status === 'available' && api.state.error === null &&
    api.state.lastCompletedMethod === 'layout.apply' && change != null && sameEditorVersion(base, change.source) &&
    candidate !== null && candidate.intent === intent && api.state !== candidate.before && accepted !== null &&
    layoutSnapshotMatchesTarget(candidate.snapshot, base) && hasTargetSubtitleEvidence(candidate.snapshot, base, accepted) &&
    layoutSnapshotMatchesTarget(s, change.created) && layoutChildSnapshotMatchesOperation(s, candidate.snapshot, candidate.operation)
  const binding = childAccepted ? change.created : readAccepted ? layoutSnapshotVersion(s) : base
  const knownCurrent = accepted !== null && s !== null && base !== null && api.state.pending === null && api.state.error === null &&
    (api.state.status === 'available' || api.state.status === 'noChange') && hasTargetSubtitleEvidence(s, base, accepted)
  const evidence = readAccepted || childAccepted || knownCurrent ? s : accepted
  const active = props.active !== false && (intent === null || intent.target.projectId === projectId)
  const context = useMemo(() => ({ projectId: binding?.projectId ?? null, versionId: binding?.versionId ?? null,
    versionDigest: binding?.versionDigest ?? null, root: intent?.target,
    readMode: intent?.warning == null ? 'latestInBranch' as const : 'exact' as const }),
  [binding?.projectId, binding?.versionId, binding?.versionDigest, intent])
  const onDirtyChange = useCallback((value: boolean) => { dirty.current = value; props.onDirtyChange?.(value) }, [props.onDirtyChange])

  useLayoutEffect(() => {
    if (intent !== null && binding !== null && (adopted?.intent !== intent || !sameEditorVersion(adopted.target, binding) || adopted.snapshot !== evidence))
      setAdopted({ intent, target: binding, snapshot: evidence })
    if (candidate !== null && api.state !== candidate.before && api.state.pending === null) submitted.current = null
  }, [adopted, api.state, binding, candidate, evidence, intent])

  useEffect(() => {
    const entering = active && !wasActive.current && intent?.warning === null
    wasActive.current = active
    if (!active || intent === null || (requested.current === intent && !entering) || dirty.current || api.state.pending !== null) return
    let cancelled = false
    queueMicrotask(() => {
      if (cancelled || dirty.current) return
      requested.current = intent
      const latestInBranch = intent.warning === null
      attempt.current = { intent, target: intent.target, latestInBranch, before: api.state }
      if (latestInBranch && sameEditorVersion(api.state.branch?.root, intent.target)) {
        api.refreshSnapshot(intent.target, { latestInBranch: true })
      } else api.loadSnapshot(intent.target, latestInBranch ? { latestInBranch: true } : undefined)
    })
    return () => { cancelled = true }
  }, [active, intent, api.loadSnapshot, api.refreshSnapshot, api.state])

  useEffect(() => {
    if (!readCompleted) return
    attempt.current = null
    // Refreshing an adopted child does not certify the original warning's version.
    if (!sameEditorVersion(read.target, read.intent.target)) return
    const error = !exactRead && !latestRead ? '字幕・画角の対象版を新しい native 読込で確認できません。別の版は使用しません。'
      : !readAccepted ? '対象版に結び付いた字幕 cue を確認できません。'
        : read.intent.warning?.warning.target.editorKind === 'Layout' && !layoutSupportsManualEditing(s)
          ? '対象版は字幕専用です。画角編集の移動先としては扱いません。' : null
    onRead({ intent: read.intent, error, snapshot: error === null ? s : null })
  }, [exactRead, latestRead, onRead, read, readAccepted, readCompleted, s])

  const mismatch = binding === null || (s !== null && !layoutSnapshotMatchesTarget(s, binding))
  const missingCues = !mismatch && s !== null && (evidence === null || !hasTargetSubtitleEvidence(s, binding!, evidence))
  const retained = accepted !== null && binding !== null && layoutSnapshotMatchesTarget(accepted, binding) ? accepted : null
  const state: typeof api.state = mismatch || missingCues
    ? { ...api.state, snapshot: retained, target: binding, projectIdInput: binding?.projectId ?? api.state.projectIdInput,
      status: retained === null ? 'unavailable' : 'invalid', lastCompletedMethod: null } : api.state
  return <>
    {props.active && !active && <p role="status">選択中のProjectと編集元が異なるため、旧Projectの入力を保持して操作を停止しています。対象の成果物を選び直してください。</p>}
    {binding !== null && (mismatch || missingCues) && <p role={props.active ? 'status' : undefined}>対象版・digest と字幕 cue を確認できません。入力を保持し、適用を停止しています。</p>}
    <LayoutPanelView {...api} {...props} active={active} context={context} state={state} onDirtyChange={onDirtyChange}
      loadSnapshot={ignoreLayoutAutoload} changeProjectId={ignoreLayoutAutoload} invalidatePendingContext={ignoreLayoutAutoload}
      refreshSnapshot={() => {
        if (active && binding !== null && intent !== null && api.state.pending === null) {
          const latestInBranch = intent.warning === null
          const target = latestInBranch ? intent.target : binding
          attempt.current = { intent, target, latestInBranch, before: api.state }
          api.refreshSnapshot(target, latestInBranch ? { latestInBranch: true } : undefined)
        }
      }}
      applyOperation={operation => {
        if (active && !mismatch && !missingCues && intent !== null && requested.current === intent && s !== null && api.state.pending === null &&
          (api.state.status === 'available' || api.state.status === 'noChange')) {
          submitted.current = { intent, before: api.state, snapshot: s, operation }
          api.applyOperation(operation)
        }
      }} />
  </>
}

function ignoreLayoutAutoload(): void {}

/** Legacy review reads lack an expected digest; retain the last bound workspace. */
function BoundReviewPanel({ binding, ...props }: ReviewPanelProps & { readonly binding: EditorVersion | null }) {
  const api = useReviewState()
  const previous = useRef<typeof api.state.snapshot>(null)
  const s = api.state.snapshot
  const mismatch = binding !== null && s !== null && (s.versionId !== binding.versionId || s.versionDigest !== binding.versionDigest)
  if (!mismatch && s !== null) previous.current = s
  const retained = previous.current?.versionId === props.context?.versionId ? previous.current : null
  const state: typeof api.state = mismatch ? { ...api.state, snapshot: retained, lastCompletedMethod: null,
    error: { code: 'review.operation_failed', message: '対象版・digest に一致しない応答を拒否しました。入力は保持しています。' } } : api.state
  return <ReviewPanelView {...api} {...props} state={state}
    addComment={(...args) => { if (!mismatch) api.addComment(...args) }}
    requestChanges={(...args) => { if (!mismatch) api.requestChanges(...args) }}
    recordApproval={(...args) => { if (!mismatch) api.recordApproval(...args) }} />
}

function BoundDeliveryPanel({ binding, ...props }: DeliveryPanelProps & { readonly binding: EditorVersion | null }) {
  const api = useDeliveryState()
  const s = api.state.snapshot
  const mismatch = binding !== null && s !== null && (s.projectId !== binding.projectId || s.versionId !== binding.versionId || s.versionDigest !== binding.versionDigest)
  const state: typeof api.state = mismatch ? { ...api.state, status: 'mismatched', snapshot: null, trace: null } : api.state
  return <DeliveryPanelView {...api} {...props} state={state} lookupTrace={() => { if (!mismatch) api.lookupTrace() }} />
}

/** Keep DOM/editor state; hiding is neither closeDraft nor cancellation. */
function WorkflowSurface({ active, children, id, labelledBy, role, className }: Readonly<{
  active: boolean
  children: ReactNode
  id: string
  labelledBy?: string
  role?: 'tabpanel' | 'region'
  className: string
}>) {
  const surface = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    if (active) return
    const element = surface.current
    for (const media of element?.querySelectorAll<HTMLMediaElement>('video, audio') ?? []) {
      if (!media.paused) media.pause()
    }
    const focused = element?.ownerDocument.activeElement
    if (focused instanceof HTMLElement && element?.contains(focused)) focused.blur()
  }, [active])

  return (
    <div ref={surface} id={id} role={role} aria-labelledby={labelledBy} className={className} hidden={!active} inert={!active} tabIndex={role === 'tabpanel' ? 0 : undefined}
      onFocusCapture={event => { if (!active) event.target.blur() }}
      onPlayCapture={event => {
        if (!active && event.target instanceof HTMLMediaElement) event.target.pause()
      }}>
      {children}
    </div>
  )
}