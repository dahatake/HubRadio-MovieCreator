import { useEffect, useId, useRef, useState } from 'react'
import { createHostBridgeClient, type BridgeClient } from '../../bridge'
import type { ProjectListItem } from '../../bridge/projectProtocol'
import { getProjectDisplayName } from '../projects/projectState'
import {
  createPipelineExecutionRequest, createPipelineReadRequest, isPipelineLive, pipelineResponseMatchesRequest, verifiedPipelineResponse,
  type PipelineRequest, type PipelineResponse, type PipelineVersionsResponse,
} from '../workflow/pipelineProtocol'
import { pipelinePhaseLabels } from '../workflow/pipelinePresentation'
import { buildProjectJobRows, formatJobCreatedAt, mergeLatestSnapshot, sortAllJobRows, type LatestJobSnapshot, type ProjectJobRow } from './projectJobs'

type JobsClient = Pick<BridgeClient, 'sendPipelineVersions' | 'onPipelineVersionsResponse' | 'sendPipeline' | 'onPipelineResponse' | 'dispose'>

const REQUEST_TIMEOUT_MS = 15_000
const AUTO_REFRESH_MS = 10_000

interface Target { readonly draftId: string, readonly projectId: string, readonly projectName: string }

type Pending =
  | { readonly kind: 'versions', readonly requestId: string, readonly projectId: string, readonly resolve: (response: unknown) => void }
  | { readonly kind: 'read', readonly requestId: string, readonly request: PipelineRequest, readonly resolve: (response: unknown) => void }

interface Run {
  readonly generation: number
  client: JobsClient | null
  unsubscribe: (() => void) | null
  unsubscribePipeline: (() => void) | null
  pending: Pending | null
  timer: ReturnType<typeof setTimeout> | null
}

interface Operation {
  readonly kind: 'cancel' | 'retry'
  readonly row: ProjectJobRow
}

interface OperationHandle {
  readonly result: Promise<PipelineResponse | null>
  abort(): void
}

/** FR-061.11: one pipeline request on its own client; resolves null on timeout, send failure or abort. */
function sendOnce(make: () => JobsClient, request: PipelineRequest): OperationHandle {
  let client: JobsClient | null = null
  let unsubscribe: (() => void) | null = null
  let timer: ReturnType<typeof setTimeout> | null = null
  let finished = false
  let settle: (response: PipelineResponse | null) => void = () => undefined
  const finish = (response: PipelineResponse | null) => {
    if (finished) return
    finished = true
    if (timer !== null) clearTimeout(timer)
    try { unsubscribe?.() } catch { /* already closed */ }
    try { client?.dispose() } catch { /* already closed */ }
    settle(response)
  }
  const result = new Promise<PipelineResponse | null>(resolve => {
    settle = resolve
    try {
      client = make()
      unsubscribe = client.onPipelineResponse(value => {
        const response = verifiedPipelineResponse(value)
        if (response === null) {
          if (typeof value === 'object' && value !== null && (value as { requestId?: unknown }).requestId === request.requestId) finish(null)
          return
        }
        if (!pipelineResponseMatchesRequest(response, request)) return
        finish(response)
      })
      timer = setTimeout(() => finish(null), REQUEST_TIMEOUT_MS)
      client.sendPipeline(request)
    } catch {
      finish(null)
    }
  })
  return { result, abort: () => finish(null) }
}

function newRequestId(): string {
  return globalThis.crypto.randomUUID().toLowerCase()
}

function release(run: Run): void {
  if (run.timer !== null) clearTimeout(run.timer)
  run.timer = null
  const pending = run.pending
  run.pending = null
  pending?.resolve(null)
  discardClient(run)
}

function discardClient(run: Run): void {
  try { run.unsubscribe?.() } catch { /* already closed */ }
  try { run.unsubscribePipeline?.() } catch { /* already closed */ }
  try { run.client?.dispose() } catch { /* already closed */ }
  run.unsubscribe = null
  run.unsubscribePipeline = null
  run.client = null
}

function ensureClient(run: Run, make: () => JobsClient): JobsClient {
  if (run.client !== null) return run.client
  const client = make()
  run.client = client
  run.unsubscribe = client.onPipelineVersionsResponse((value, projectId) => {
    const pending = run.pending
    if (pending === null || pending.kind !== 'versions' || value.requestId !== pending.requestId || projectId !== pending.projectId) return
    settlePending(run, pending, value)
  })
  run.unsubscribePipeline = client.onPipelineResponse(value => {
    const pending = run.pending
    if (pending === null || pending.kind !== 'read') return
    const response = verifiedPipelineResponse(value)
    if (response === null || response.requestId !== pending.requestId || !pipelineResponseMatchesRequest(response, pending.request)) return
    settlePending(run, pending, response)
  })
  return client
}

function settlePending(run: Run, pending: Pending, value: unknown): void {
  if (run.timer !== null) clearTimeout(run.timer)
  run.timer = null
  run.pending = null
  pending.resolve(value)
}

/** Sends one request and waits for its response; response is null on timeout or send failure. */
function exchange(run: Run, init: Pick<Pending, 'kind' | 'requestId'> & ({ projectId: string } | { request: PipelineRequest }), send: () => void): Promise<{ response: unknown, threw: boolean }> {
  return new Promise(resolve => {
    const pending = { ...init, resolve: (response: unknown) => resolve({ response, threw: false }) } as Pending
    run.pending = pending
    run.timer = setTimeout(() => {
      run.timer = null
      run.pending = null
      resolve({ response: null, threw: false })
    }, REQUEST_TIMEOUT_MS)
    try { send() } catch {
      if (run.timer !== null) clearTimeout(run.timer)
      run.timer = null
      run.pending = null
      resolve({ response: null, threw: true })
    }
  })
}

/**
 * FR-061: every finalized Project's Jobs, newest first, read one request at a time.
 * FR-061.10: each Project's latest Job is included even before it has a version (pipeline.read).
 * FR-061.11: the latest Job can be cancelled or retried from the table after confirmation.
 * FR-061.12: while a latest Job is live, the table is re-read every 10 seconds.
 */
export function AllProjectJobsPanel({ active, projects, createClient = createHostBridgeClient, onOpenJob, openDisabledReason = null }: Readonly<{
  active: boolean
  projects: readonly ProjectListItem[]
  createClient?: () => JobsClient
  onOpenJob: (draftId: string) => void
  openDisabledReason?: string | null
}>) {
  const headingId = useId()
  const [rows, setRows] = useState<readonly ProjectJobRow[]>([])
  const [failedNames, setFailedNames] = useState<readonly string[]>([])
  const [reading, setReading] = useState(false)
  const [done, setDone] = useState(0)
  const [hasRead, setHasRead] = useState(false)
  const [latestByProject, setLatestByProject] = useState<ReadonlyMap<string, LatestJobSnapshot>>(new Map())
  const [operating, setOperating] = useState(false)
  const [dialog, setDialog] = useState<Operation | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const generation = useRef(0)
  const currentRun = useRef<Run | null>(null)
  const operationToken = useRef(0)
  const operationHandle = useRef<OperationHandle | null>(null)
  const opener = useRef<HTMLElement | null>(null)
  const cancelButton = useRef<HTMLButtonElement | null>(null)
  const latest = useRef({ projects, createClient })
  latest.current = { projects, createClient }
  const total = targetsOf(projects).length
  const anyLive = rows.some(row => row.isLatest && isPipelineLive(row.phase))
  const autoScheduled = active && hasRead && !reading && !operating && anyLive

  function start(): void {
    const targets = targetsOf(latest.current.projects)
    const make = latest.current.createClient
    if (currentRun.current !== null) release(currentRun.current)
    const run: Run = { generation: ++generation.current, client: null, unsubscribe: null, unsubscribePipeline: null, pending: null, timer: null }
    currentRun.current = run
    setReading(true)
    setDone(0)
    void execute(run, targets, make)
  }

  async function execute(run: Run, targets: readonly Target[], make: () => JobsClient): Promise<void> {
    const collected: ProjectJobRow[] = []
    const failed: string[] = []
    const latestSnapshots = new Map<string, LatestJobSnapshot>()
    const stale = () => generation.current !== run.generation
    let broken = false
    for (let index = 0; index < targets.length; index += 1) {
      const target = targets[index]
      if (broken) { failed.push(target.projectName); continue }
      let response: PipelineVersionsResponse | null = null
      try {
        const client = ensureClient(run, make)
        const requestId = newRequestId()
        const exchanged = await exchange(run, { kind: 'versions', requestId, projectId: target.projectId }, () => client.sendPipelineVersions(requestId, target.projectId))
        response = exchanged.response as PipelineVersionsResponse | null
        if (exchanged.threw) broken = true
      } catch {
        broken = true
      }
      if (stale()) return
      let projectRows: ProjectJobRow[] = []
      let versionsRead = false
      if (response === null) {
        failed.push(target.projectName)
        // A timed-out request may still count as pending on the client; start the next one on a fresh client.
        if (!broken) discardClient(run)
      } else if (response.result.status === 'available') {
        projectRows = buildProjectJobRows(target, response.result.versions)
        versionsRead = true
      } else if (response.result.status === 'notFound') {
        versionsRead = true
      } else {
        failed.push(target.projectName)
      }
      if (versionsRead) {
        let snapshot: LatestJobSnapshot | null = null
        try {
          const client = ensureClient(run, make)
          const request = createPipelineReadRequest(newRequestId(), target.projectId)
          const exchanged = await exchange(run, { kind: 'read', requestId: request.requestId, request }, () => client.sendPipeline(request))
          if (stale()) return
          const read = exchanged.response as PipelineResponse | null
          const value = read !== null && (read.result.status === 'available' || read.result.status === 'notFound') ? read.result.snapshot : null
          if (value !== null) {
            snapshot = { executionId: value.executionId, revision: value.revision, phase: value.phase, versionId: value.versionId, hasResult: value.result !== null }
          }
          if (read === null) discardClient(run)
        } catch {
          discardClient(run)
        }
        if (stale()) return
        if (snapshot !== null) latestSnapshots.set(target.projectId, snapshot)
        projectRows = mergeLatestSnapshot(target, projectRows, snapshot)
      }
      collected.push(...projectRows)
      setDone(index + 1)
    }
    if (stale()) return
    release(run)
    currentRun.current = null
    setRows(sortAllJobRows(collected))
    setLatestByProject(latestSnapshots)
    setFailedNames(failed)
    setHasRead(true)
    setReading(false)
  }

  function closeDialog(): void {
    setDialog(null)
    const element = opener.current
    opener.current = null
    if (element !== null && element.isConnected) element.focus()
  }

  async function operate(operation: Operation): Promise<void> {
    const { kind, row } = operation
    closeDialog()
    const snapshot = latestByProject.get(row.projectId)
    const token = ++operationToken.current
    setOperating(true)
    setNotice(null)
    let accepted = false
    if (snapshot !== undefined && snapshot.executionId === row.executionId) {
      try {
        const request = createPipelineExecutionRequest(newRequestId(), kind === 'cancel' ? 'pipeline.cancel' : 'pipeline.retry', {
          projectId: row.projectId, executionId: row.executionId, expectedRevision: snapshot.revision,
        })
        const handle = sendOnce(latest.current.createClient, request)
        operationHandle.current = handle
        const response = await handle.result
        accepted = response !== null && response.result.status === 'available'
      } catch { /* reported as a failure below */ }
    }
    if (operationToken.current !== token) return
    operationHandle.current = null
    const subject = `「${row.projectName}」のJob ${row.jobNumber}`
    setNotice(accepted
      ? `${subject}の${kind === 'cancel' ? '取り消し' : '再試行'}を受け付けました。`
      : `${subject}を操作できませんでした。状態が変わった可能性があります。`)
    setOperating(false)
    start()
  }

  useEffect(() => () => {
    generation.current += 1
    operationToken.current += 1
    operationHandle.current?.abort()
    operationHandle.current = null
    if (currentRun.current !== null) release(currentRun.current)
    currentRun.current = null
  }, [])

  useEffect(() => {
    if (active) start()
    // start() reads the latest props through a ref; only an activation should trigger a read.
  }, [active])

  useEffect(() => {
    if (!autoScheduled) return undefined
    const timer = setTimeout(start, AUTO_REFRESH_MS)
    return () => clearTimeout(timer)
  }, [autoScheduled])

  useEffect(() => {
    if (dialog !== null) cancelButton.current?.focus()
  }, [dialog])

  const busy = reading || operating
  const failedLines = failedNames.map((name, index) => <p key={`${index}-${name}`}>「{name}」のジョブを取得できませんでした。</p>)
  return <section aria-labelledby={headingId}>
    <h2 id={headingId}>すべてのProjectのジョブ</h2>
    <p>すべてのProjectのジョブ（自動編集の実行）の状態を新しい順に表示します。ジョブを開くと、そのProjectの「ジョブの状態の確認」画面へ移ります。</p>
    <button type="button" disabled={busy} onClick={start}>最新の状態に更新</button>
    {reading && <p role="status">ジョブの状態を取得しています（{done} / {total} Project）。</p>}
    {autoScheduled && <p>実行中のジョブがあるあいだは、10秒ごとに自動で更新します。</p>}
    {notice !== null && <p role="status">{notice}</p>}
    {total === 0
      ? <p>ジョブはまだありません。Project一覧で元動画を選んで「編集を開始」すると、ジョブが作られます。</p>
      : <>
        {failedLines}
        {!reading && hasRead && failedNames.length === 0 && rows.length === 0 && <p>ジョブはまだありません。</p>}
      </>}
    {openDisabledReason !== null && rows.length > 0 && <p role="status">{openDisabledReason}</p>}
    {rows.length > 0 && <table>
      <caption>ジョブの一覧（{rows.length}件）</caption>
      <thead><tr><th scope="col">Project</th><th scope="col">ジョブ</th><th scope="col">状態</th><th scope="col">作成日時</th><th scope="col">操作</th></tr></thead>
      <tbody>
        {rows.map(row => {
          const snapshot = row.isLatest ? latestByProject.get(row.projectId) : undefined
          const known = snapshot !== undefined && snapshot.executionId === row.executionId
          const canCancel = known && isPipelineLive(row.phase)
          const canRetry = known && (row.phase === 'Failed' || row.phase === 'Canceled') && snapshot.versionId === null && !snapshot.hasResult
          return <tr key={`${row.draftId}:${row.executionId}`} data-job-phase={row.phase}>
            <td>{row.projectName}</td>
            <td>Job {row.jobNumber}{row.isLatest ? '（最新）' : ''}</td>
            <td>{pipelinePhaseLabels[row.phase]}</td>
            <td>{formatJobCreatedAt(row.createdAt)}</td>
            <td>
              <button type="button" aria-label={`「${row.projectName}」のJob ${row.jobNumber}を開く`} disabled={openDisabledReason !== null} onClick={() => onOpenJob(row.draftId)}>開く</button>
              {canCancel && <button type="button" aria-label={`「${row.projectName}」のJob ${row.jobNumber}を取り消す`} disabled={busy}
                onClick={event => { opener.current = event.currentTarget; setDialog({ kind: 'cancel', row }) }}>取消</button>}
              {canRetry && <button type="button" aria-label={`「${row.projectName}」のJob ${row.jobNumber}を再試行する`} disabled={busy}
                onClick={event => { opener.current = event.currentTarget; setDialog({ kind: 'retry', row }) }}>再試行</button>}
            </td>
          </tr>
        })}
      </tbody>
    </table>}
    {dialog !== null && <section role="alertdialog" aria-label="ジョブの操作の確認" onKeyDown={event => { if (event.key === 'Escape') { event.stopPropagation(); closeDialog() } }}>
      <p>{dialog.kind === 'cancel'
        ? `「${dialog.row.projectName}」のJob ${dialog.row.jobNumber}を取り消しますか。取り消すと、このJobの処理は止まります。`
        : `「${dialog.row.projectName}」のJob ${dialog.row.jobNumber}を再試行しますか。`}</p>
      <button type="button" disabled={busy} onClick={() => { void operate(dialog) }}>実行する</button>
      <button type="button" ref={cancelButton} onClick={closeDialog}>やめる</button>
    </section>}
  </section>
}

function targetsOf(projects: readonly ProjectListItem[]): Target[] {
  const targets: Target[] = []
  for (const project of projects) {
    if (project.state === 'finalized' && project.projectId !== null) {
      targets.push({ draftId: project.draftId, projectId: project.projectId, projectName: getProjectDisplayName(project) ?? '名称未設定のProject' })
    }
  }
  return targets
}
