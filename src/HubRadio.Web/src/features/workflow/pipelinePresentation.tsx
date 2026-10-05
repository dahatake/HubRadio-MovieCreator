import { useEffect, useRef, useState } from 'react'
import { useAppState } from '../../state'
import { pipelineArtifactLabels } from '../results/ResultGallery'
import { usePipeline } from './PipelineProvider'
import { isPipelineLive, sameResultBinding, sourceOnlyMediaUrl, type PipelinePhase, type SourceOnlyResultArtifact, type SourceOnlyResultBinding, type SourceOnlyResultDto, type SourceOnlyResultTarget } from './pipelineProtocol'
import type { WorkflowWarningTarget } from './workflowNavigation'

export const pipelinePhaseLabels: Readonly<Record<PipelinePhase, string>> = {
  Queued: '待機中', Ingesting: '元動画を取り込み中', Analyzing: '解析中', Composing: '構成中', Rendering: '生成中',
  Evaluating: '自動評価中', Delivering: '社内用成果物を保存中', Completed: '完了', Failed: '失敗', Canceled: '取消済み',
  ReselectionRequired: '元動画の再選択が必要', ReconciliationRequired: '整合性の再確認が必要',
}

const pipelineStages = ['Queued', 'Ingesting', 'Analyzing', 'Composing', 'Rendering', 'Evaluating', 'Delivering', 'Completed'] as const satisfies readonly PipelinePhase[]

/** FR-042: where the run is, by phase only; no percentage is invented. */
function PipelineStages({ phase }: Readonly<{ phase: PipelinePhase }>) {
  const index = pipelineStages.findIndex(stage => stage === phase)
  if (index < 0) return null
  const total = pipelineStages.length
  return <>
    <p className="pipeline-stage-summary" role="status">進み具合：{index + 1} / {total} 段階（{pipelinePhaseLabels[phase]}）</p>
    <ol className="pipeline-stages" aria-label="自動編集の段階">
      {pipelineStages.map((stage, i) => {
        const state = phase === 'Completed' || i < index ? 'done' : i === index ? 'current' : 'pending'
        return <li key={stage} data-stage-state={state} aria-current={state === 'current' ? 'step' : undefined}>
          {pipelinePhaseLabels[stage]}：{state === 'done' ? '完了' : state === 'current' ? '実行中' : '未着手'}
        </li>
      })}
    </ol>
  </>
}

/** The legacy job widget operates jobs.retry in place; pipeline retry instead
 * creates a new execution/root. Share monitoring, never those command buttons.
 */
export function PipelineProgress({ active = true }: Readonly<{ active?: boolean }>) {
  const pipeline = usePipeline()
  const { state, monitorJob, stopMonitoringJob, refreshJob } = useAppState()
  const s = pipeline.state.snapshot
  const rootJobId = s?.rootJobId ?? null
  const monitor = state.jobMonitor
  const job = monitor.trackedJobId === rootJobId && monitor.snapshot?.jobId === rootJobId ? monitor.snapshot : null
  const live = s !== null && isPipelineLive(s.phase)
  useEffect(() => {
    let canceled = false
    queueMicrotask(() => {
      if (canceled || rootJobId === monitor.trackedJobId) return
      if (monitor.trackedJobId !== null) stopMonitoringJob()
      if (rootJobId !== null) monitorJob(rootJobId)
    })
    return () => { canceled = true }
  }, [rootJobId, monitor.trackedJobId, monitorJob, stopMonitoringJob])
  useEffect(() => {
    if (!live || rootJobId === null || monitor.trackedJobId !== rootJobId || monitor.pending !== null) return
    const timer = setTimeout(refreshJob, 1500)
    return () => clearTimeout(timer)
  }, [live, rootJobId, monitor.trackedJobId, monitor.pending, monitor.snapshot, refreshJob])
  const disabled = !active || pipeline.state.pendingMethod !== null || pipeline.state.needsRefresh
  return <section className="job-panel job-panel-compact" aria-labelledby="job-panel-title" hidden={false}>
    <h2 id="job-panel-title">ジョブの進行状況</h2>
    <p role="status" data-pipeline-phase={s?.phase ?? 'none'}>{s === null
      ? pipeline.state.projectId === null ? '確定済みの案件を選択してください。' : pipeline.state.pendingMethod !== null ? '自動編集の状態を確認しています。' : '編集処理の開始は確認できていません。'
      : pipelinePhaseLabels[s.phase]}</p>
    {s !== null && <PipelineStages phase={s.phase} />}
    {live && <progress aria-label="自動編集の進行状況" max={1} value={job?.progressRatio ?? undefined} />}
    {s?.failureCode != null && <p role="status">{s.failureCode}</p>}
    {pipeline.state.error !== null && <p role="alert">{pipeline.state.error === 'timeout'
      ? '応答が時間内に届きませんでした。処理を停止したとは判断しません。'
      : pipeline.state.error === 'invalidResponse' ? '実行・版の対応が異なる応答を拒否しました。' : 'ホストの処理状況を取得できません。'}</p>}
    {['forbidden', 'invalid', 'notFound', 'conflict'].includes(pipeline.state.status) && <p role="status">処理状態を利用できません: {pipeline.state.status}</p>}
    {s?.phase === 'ReconciliationRequired' && <p>保存状態の確認が必要です。自動で再試行・削除はしません。</p>}
    <div className="job-actions" aria-label="自動編集の操作">
      {pipeline.state.projectId !== null && <button type="button" disabled={!active || pipeline.state.pendingMethod !== null} onClick={pipeline.refresh}>最新状態を取得</button>}
      {live && <button type="button" disabled={disabled} onClick={pipeline.cancel}>自動編集を取消</button>}
      {(s?.phase === 'Failed' || s?.phase === 'Canceled') && <button type="button" disabled={disabled} onClick={pipeline.retry}>自動編集を再試行</button>}
    </div>
    {s !== null && <details><summary>実行の参照</summary><p>実行 <code>{s.executionId}</code> · revision {s.revision}</p><p>root job <code>{s.rootJobId}</code></p></details>}
  </section>
}

export interface GeneratedPreviewSeek {
  readonly warningId: string
  readonly target: SourceOnlyResultTarget
}
interface PendingGeneratedSeek {
  readonly intent: GeneratedPreviewSeek
  readonly element: HTMLVideoElement
  readonly url: string
  readonly binding: SourceOnlyResultBinding
  readonly seconds: number
  applied: boolean
  timer: ReturnType<typeof setTimeout> | null
}
export function GeneratedResultPreview({ result, artifact, active, seek, onHandled }: Readonly<{
  result: SourceOnlyResultDto
  artifact: SourceOnlyResultArtifact
  active: boolean
  seek: GeneratedPreviewSeek | null
  onHandled: (seek: GeneratedPreviewSeek, error: string | null) => void
}>) {
  const url = sourceOnlyMediaUrl(result, artifact.artifact.artifactId)
  const video = useRef<HTMLVideoElement>(null)
  const live = useRef({ result, artifact, active, seek, url, onHandled })
  live.current = { result, artifact, active, seek, url, onHandled }
  const completed = useRef<GeneratedPreviewSeek | null>(null)
  const pendingSeek = useRef<PendingGeneratedSeek | null>(null)
  const [unavailableUrl, setUnavailableUrl] = useState<string | null>(null)
  function release(pending: PendingGeneratedSeek): void {
    if (pending.timer !== null) clearTimeout(pending.timer)
    pending.timer = null
    if (pendingSeek.current === pending) pendingSeek.current = null
  }
  function finish(pending: PendingGeneratedSeek, error: string | null): void {
    if (pendingSeek.current !== pending) return
    release(pending)
    completed.current = pending.intent
    if (error === null) pending.element.focus({ preventScroll: true })
    live.current.onHandled(pending.intent, error)
  }
  function isCurrent(pending: PendingGeneratedSeek): boolean {
    const current = live.current
    const t = pending.intent.target
    const a = current.artifact
    return current.active && current.seek === pending.intent && video.current === pending.element &&
      sameResultBinding(current.result.binding, pending.binding) && t.projectId === current.result.binding.projectId &&
      t.versionId === a.versionId && t.versionDigest === a.versionDigest &&
      t.artifactId === a.artifact.artifactId && t.artifactDigest === a.artifact.contentDigest &&
      sourceOnlyMediaUrl(current.result, a.artifact.artifactId) === pending.url &&
      pending.element.getAttribute('src') === pending.url &&
      (pending.element.currentSrc === '' || pending.element.currentSrc === pending.url) &&
      !pending.element.closest('[hidden], [inert], [aria-hidden="true"]')
  }
  function withinMedia(pending: PendingGeneratedSeek): boolean {
    return Number.isFinite(pending.seconds) && pending.seconds >= 0 &&
      Number.isFinite(pending.element.duration) && pending.seconds < pending.element.duration
  }
  function applySeek(element: HTMLVideoElement): void {
    const pending = pendingSeek.current
    if (pending === null || pending.element !== element || pending.applied) return
    if (!isCurrent(pending) || !withinMedia(pending)) {
      finish(pending, '成果物の指定時刻・媒体を確認できません。先頭や近い時刻へは移動しません。')
      return
    }
    pending.applied = true
    try {
      if (!element.paused) element.pause()
      element.currentTime = pending.seconds
    }
    catch { finish(pending, '成果物の指定時刻へ移動できませんでした。') }
  }
  function seeked(element: HTMLVideoElement): void {
    const pending = pendingSeek.current
    if (pending === null || pending.element !== element || !pending.applied) return
    finish(pending, isCurrent(pending) && withinMedia(pending) && !element.seeking &&
      Number.isFinite(element.currentTime) && element.currentTime === pending.seconds
      ? null : '成果物のシーク完了を指定した媒体・時刻で確認できませんでした。')
  }
  useEffect(() => {
    const element = video.current
    if (!active) {
      if (element !== null && !element.paused) element.pause()
      return
    }
    if (seek === null || completed.current === seek) return
    const t = seek.target
    if (element === null || url === null || artifact.mediaType !== 'video/mp4' || t.kind !== 'Preview' ||
        t.timeBasis !== 'Output' || t.time === null || !Number.isSafeInteger(t.time.value) ||
        !Number.isSafeInteger(t.time.rate) || t.time.rate <= 0) {
      completed.current = seek
      live.current.onHandled(seek, '成果物の指定時刻・媒体を確認できません。')
      return
    }
    const pending: PendingGeneratedSeek = { intent: seek, element, url, binding: result.binding,
      seconds: t.time.value / t.time.rate, applied: false, timer: null }
    pendingSeek.current = pending
    if (!isCurrent(pending) || !Number.isFinite(pending.seconds) || pending.seconds < 0) {
      finish(pending, '警告の対象媒体が変わったため、指定時刻への移動を中止しました。')
      return
    }
    pending.timer = setTimeout(() => finish(pending, '成果物のシーク完了を時間内に確認できませんでした。'), 30_000)
    if (element.readyState >= 1) applySeek(element)
    return () => {
      // The same intent cannot be rebound to a replacement execution/media URL.
      // StrictMode cleanup with the same live binding only releases the timer.
      const current = live.current
      if (current.seek === pending.intent && current.active &&
        (current.url !== pending.url || !sameResultBinding(current.result.binding, pending.binding))) {
        finish(pending, '警告の対象媒体が変わったため、指定時刻への移動を中止しました。')
      } else release(pending)
    }
    // Callback/result object identities can change on a poll; they must not reset the deadline.
  }, [active, seek, url, artifact.versionId, artifact.versionDigest, artifact.artifact.artifactId, artifact.artifact.contentDigest, artifact.mediaType])
  return <section className="player-card player-workflow" data-preview-kind="generated" aria-label="生成された成果物のプレビュー">
    <h2>{pipelineArtifactLabels[artifact.role]}</h2>
    <p>生成された実成果物です（代理動画ではありません）。評価: {artifact.evaluation?.aggregateStatus ?? 'NOT EVALUATED'}</p>
    {url === null ? <p role="status">この成果物の媒体プレビューは提供されていません。代理動画で代用しません。</p> : artifact.mediaType === 'image/png'
      ? <img src={url} alt={pipelineArtifactLabels[artifact.role]} style={{ maxWidth: '100%' }} />
      : <video key={url} ref={video} src={url} controls playsInline preload="metadata" tabIndex={0}
          aria-label="生成された動画" style={{ maxWidth: '100%' }} onLoadedMetadata={event => applySeek(event.currentTarget)}
          onSeeked={event => seeked(event.currentTarget)}
          onPlay={event => { if (!live.current.active) event.currentTarget.pause() }}
          onError={event => {
            if (video.current !== event.currentTarget) return
            setUnavailableUrl(live.current.url)
            const pending = pendingSeek.current
            if (pending !== null) finish(pending, '成果物の動画を読み込めません。代理動画への自動切替は行いません。')
          }} />}
    {url !== null && unavailableUrl === url && <p role="status">動画を読み込めません。実ファイルの状態を再確認してください。</p>}
  </section>
}

/** Convert the actual DTO names; never rename Audio/Metadata to an unrelated tool. */
export function toWorkflowWarningTarget(t: SourceOnlyResultTarget): WorkflowWarningTarget | null {
  const binding = { projectId: t.projectId, versionId: t.versionId, versionDigest: t.versionDigest,
    ...(t.artifactId === null ? {} : { artifactId: t.artifactId }) }
  switch (t.kind) {
    case 'Preview': return t.time === null || t.timeBasis === null ? null : { ...binding, kind: 'preview', timeBasis: t.timeBasis === 'Source' ? 'source' : 'output', time: { value: String(t.time.value), rate: String(t.time.rate) } }
    case 'Settings': return t.settingKey === null ? null : { ...binding, kind: 'settings', key: t.settingKey }
    case 'Evidence': return { ...binding, kind: 'evidence' }
    case 'Editor': return t.editorKind === 'Timeline' ? { ...binding, kind: 'editor', tool: 'timeline' }
      : t.editorKind === 'Subtitles' || t.editorKind === 'Layout' ? { ...binding, kind: 'editor', tool: 'layout' } : null
  }
}