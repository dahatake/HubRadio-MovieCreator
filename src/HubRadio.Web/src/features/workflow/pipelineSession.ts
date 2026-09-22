import type { BridgeClient } from '../../bridge'
import {
  createPipelineExecutionRequest, createPipelineReadRequest, createPipelineRegenerateRequest, createPipelineRestoreRequest,
  isPipelineLive, parsePipelineRequest, pipelineResponseMatchesRequest, revokePipelineMedia, verifiedPipelineResponse,
  type PipelineMethod, type PipelineRegenerationParams, type PipelineRequest, type PipelineResponse, type PipelineRestoreParams, type PipelineSnapshot,
  type PipelineVersionEntry, type PipelineVersionsResponse,
} from './pipelineProtocol'

export type PipelineBridgeClient = Pick<BridgeClient, 'sendPipeline' | 'onPipelineResponse' | 'abandonPipelineRequest' | 'dispose' | 'sendPipelineVersions' | 'onPipelineVersionsResponse'>
export interface PipelineState {
  readonly projectId: string | null
  readonly snapshot: PipelineSnapshot | null
  readonly status: 'idle' | 'loading' | PipelineResponse['result']['status']
  readonly pendingMethod: PipelineMethod | null
  readonly error: 'timeout' | 'unavailable' | 'invalidResponse' | null
  /** Retained data may be displayed, but cannot authorize another mutation. */
  readonly needsRefresh: boolean
  /** Null until the past-version list has been read for this project. */
  readonly versions: readonly PipelineVersionEntry[] | null
  readonly versionsStatus: 'idle' | 'loading' | PipelineVersionsResponse['result']['status']
  /** The head revision a restore must pin; null until the list has been read. */
  readonly workingCopyRevision: number | null
}
export const initialPipelineState: PipelineState = Object.freeze({ projectId: null, snapshot: null, status: 'idle', pendingMethod: null, error: null, needsRefresh: false, versions: null, versionsStatus: 'idle', workingCopyRevision: null })
export interface PipelineSession {
  selectProject(projectId: string | null): void
  read(): boolean
  cancel(): boolean
  retry(): boolean
  regenerate(params: PipelineRegenerationParams): boolean
  restore(params: PipelineRestoreParams): boolean
  /** Reads the past-version list once. It mutates nothing and grants no media. */
  readVersions(): boolean
  getState(): PipelineState
  dispose(): void
}
interface Pending {
  readonly request: PipelineRequest
  readonly baseline: PipelineSnapshot | null
}
export interface PipelineSessionOptions {
  readonly createRequestId?: () => string
  readonly pollMilliseconds?: number
  readonly timeoutMilliseconds?: number
}

/** One selected project, one outstanding request, one modest non-overlapping poll.
 * Hiding a route is intentionally absent from this API. Dispose abandons UI
 * correlation only; the native task owner continues the execution.
 */
export function createPipelineSession(
  createClient: () => PipelineBridgeClient,
  changed: (state: PipelineState) => void,
  options: PipelineSessionOptions = {},
): PipelineSession {
  const pollMs = options.pollMilliseconds ?? 1000
  const timeoutMs = options.timeoutMilliseconds ?? 30_000
  if (!Number.isSafeInteger(pollMs) || pollMs < 1000 || pollMs > 60_000 ||
      !Number.isSafeInteger(timeoutMs) || timeoutMs < 1000 || timeoutMs > 300_000) throw new RangeError('Invalid pipeline timer interval.')
  const newId = options.createRequestId ?? (() => globalThis.crypto.randomUUID().toLowerCase())
  let state = initialPipelineState
  let client: PipelineBridgeClient | null = null
  let unsubscribe: (() => void) | null = null
  let pending: Pending | null = null
  let uncertainMutation: Pending | null = null
  let poll: ReturnType<typeof setTimeout> | null = null
  let timeout: ReturnType<typeof setTimeout> | null = null
  let disposed = false
  let generation = 0

  function publish(patch: Partial<PipelineState>): void {
    state = Object.freeze({ ...state, ...patch })
    changed(state)
  }
  function clearTimers(): void {
    if (poll !== null) clearTimeout(poll)
    if (timeout !== null) clearTimeout(timeout)
    poll = null; timeout = null
  }
  function abandon(): void {
    clearTimers()
    if (pending !== null) { try { client?.abandonPipelineRequest(pending.request.requestId) } catch { /* already disposed */ } }
    pending = null
  }
  function schedulePoll(): void {
    if (disposed || pending !== null || state.snapshot === null || !isPipelineLive(state.snapshot.phase)) return
    if (poll !== null) clearTimeout(poll)
    poll = setTimeout(() => { poll = null; read() }, pollMs)
  }
  function fail(code: NonNullable<PipelineState['error']>): void {
    const attempted = pending
    if (attempted?.request.method !== 'pipeline.read' && attempted !== null) uncertainMutation = attempted
    revokePipelineMedia(state.snapshot)
    abandon()
    publish({ pendingMethod: null, error: code, needsRefresh: true, status: 'unavailable' })
    schedulePoll()
  }
  function receive(value: PipelineResponse): void {
    // Session validation is deliberate: injected clients are not trusted parsers.
    const sent = pending
    if (disposed || sent === null) return
    const response = verifiedPipelineResponse(value)
    if (response === null || !pipelineResponseMatchesRequest(response, sent.request) || state.projectId !== sent.request.params.projectId) return
    const next = response.result.snapshot
    const intent = sent.request.method === 'pipeline.read' ? uncertainMutation : null
    const readAttempt = intent === null ? sent : { request: sent.request, baseline: intent.baseline }
    // A read of the unchanged execution is authoritative too: the mutation has
    // not been accepted at this read. Only the saved intent may admit a new root.
    if (next !== null && !snapshotMatchesAttempt(next, readAttempt) && (intent === null || !snapshotMatchesAttempt(next, intent))) {
      revokePipelineMedia(next)
      fail('invalidResponse')
      return
    }
    abandon()
    if (next !== null) {
      uncertainMutation = null
      // Ordinary equivalent polls keep identity. After revocation only the newly
      // validated projection, not the old cached object, regains media authority.
      const snapshot = !state.needsRefresh && state.snapshot !== null && JSON.stringify(next) === JSON.stringify(state.snapshot) ? state.snapshot : next
      if (snapshot !== state.snapshot) revokePipelineMedia(state.snapshot)
      const status = response.result.status
      if (status !== 'available') revokePipelineMedia(snapshot)
      publish({ snapshot, status, pendingMethod: null, error: null, needsRefresh: status !== 'available' })
    } else {
      const status = response.result.status
      // Withdraw media on authorization/validation loss; never bless a cache.
      revokePipelineMedia(state.snapshot)
      publish({ status, pendingMethod: null, error: null, needsRefresh: true,
        ...(status === 'forbidden' || status === 'notFound' || status === 'invalid' ? { snapshot: null } : {}) })
    }
    schedulePoll()
  }
  function connect(): boolean {
    if (client !== null) return true
    try {
      client = createClient()
      unsubscribe = client.onPipelineResponse(receive)
      return true
    } catch {
      try { client?.dispose() } catch { /* no raw transport exception reaches the UI */ }
      client = null; unsubscribe = null
      revokePipelineMedia(state.snapshot)
      publish({ status: 'unavailable', pendingMethod: null, error: 'unavailable', needsRefresh: true })
      return false
    }
  }
  function send(request: PipelineRequest): boolean {
    if (disposed || pending !== null || state.projectId !== request.params.projectId || !connect()) return false
    const normalized = parsePipelineRequest(request)
    if (normalized === null) return false
    clearTimers()
    const sent: Pending = { request: normalized, baseline: state.snapshot }
    pending = sent
    const mutation = request.method !== 'pipeline.read'
    if (mutation) revokePipelineMedia(state.snapshot)
    publish({ pendingMethod: request.method, error: null, ...(mutation ? { needsRefresh: true } : {}),
      ...(state.snapshot === null ? { status: 'loading' } : {}) })
    timeout = setTimeout(() => { if (pending === sent) fail('timeout') }, timeoutMs)
    try { client!.sendPipeline(normalized); return true }
    catch { if (pending === sent) fail('unavailable'); return false }
  }
  function read(): boolean {
    if (state.projectId === null || disposed || pending !== null) return false
    try { return send(createPipelineReadRequest(newId(), state.projectId)) }
    catch { fail('unavailable'); return false }
  }
  function command(method: 'pipeline.retry' | 'pipeline.cancel'): boolean {
    const s = state.snapshot
    if (s === null || state.needsRefresh || state.status !== 'available' || pending !== null || uncertainMutation !== null) return false
    if (method === 'pipeline.cancel' ? !isPipelineLive(s.phase) : s.phase !== 'Failed' && s.phase !== 'Canceled') return false
    if (method === 'pipeline.retry' && (s.versionId !== null || s.result !== null)) return false
    try { return send(createPipelineExecutionRequest(newId(), method, { projectId: s.projectId, executionId: s.executionId, expectedRevision: s.revision })) }
    catch { return false }
  }
  let versionsPending: string | null = null
  let versionsUnsubscribe: (() => void) | null = null
  function readVersions(): boolean {
    const projectId = state.projectId
    if (projectId === null || disposed || versionsPending !== null || !connect()) return false
    const requestId = newId()
    versionsPending = requestId
    // The listener is installed once; it must compare against the project the
    // transport actually requested, not one captured by the first call.
    versionsUnsubscribe ??= client!.onPipelineVersionsResponse((response, requestedProjectId) => {
      if (disposed || response.requestId !== versionsPending || state.projectId !== requestedProjectId) return
      versionsPending = null
      publish({
        versionsStatus: response.result.status,
        versions: response.result.versions,
        workingCopyRevision: response.result.status === 'available' ? response.result.workingCopyRevision : null,
      })
    })
    publish({ versionsStatus: 'loading' })
    try { client!.sendPipelineVersions(requestId, projectId); return true }
    catch { versionsPending = null; publish({ versionsStatus: 'unavailable' }); return false }
  }
  return {
    getState: () => state,
    selectProject(projectId) {
      if (disposed || projectId === state.projectId) return
      // Validate before changing any state; null is a real unselected context.
      if (projectId !== null) { try { createPipelineReadRequest(projectId, projectId) } catch { return } }
      revokePipelineMedia(state.snapshot)
      abandon(); uncertainMutation = null
      versionsPending = null
      const selectedGeneration = ++generation
      state = Object.freeze({ ...initialPipelineState, projectId })
      changed(state)
      if (projectId !== null) queueMicrotask(() => { if (!disposed && generation === selectedGeneration) read() })
    },
    read,
    readVersions,
    cancel: () => command('pipeline.cancel'),
    retry: () => command('pipeline.retry'),
    regenerate(params) {
      const s = state.snapshot
      if (s === null || state.status !== 'available' || state.needsRefresh || uncertainMutation !== null || pending !== null || isPipelineLive(s.phase) ||
          s.resultStatus === 'invalid' || s.phase === 'ReconciliationRequired' || s.phase === 'ReselectionRequired' || s.versionId === null ||
          params.projectId !== s.projectId || params.expectedVersionId !== s.versionId || params.expectedVersionDigest !== s.versionDigest) return false
      try { return send(createPipelineRegenerateRequest(newId(), params)) } catch { return false }
    },
    restore(params) {
      const s = state.snapshot
      if (s === null || state.status !== 'available' || state.needsRefresh || uncertainMutation !== null || pending !== null || isPipelineLive(s.phase) ||
          s.resultStatus === 'invalid' || s.phase === 'ReconciliationRequired' || s.phase === 'ReselectionRequired' ||
          params.projectId !== s.projectId || params.targetVersionId === s.versionId) return false
      try {
        if (!send(createPipelineRestoreRequest(newId(), params))) return false
        // A restore creates a new version, so the retained list is stale from
        // this moment. Do not keep showing it as the current history.
        versionsPending = null
        publish({ versionsStatus: 'idle', versions: null, workingCopyRevision: null })
        return true
      } catch { return false }
    },
    dispose() {
      if (disposed) return
      disposed = true; generation++
      revokePipelineMedia(state.snapshot)
      abandon(); uncertainMutation = null; versionsPending = null
      try { unsubscribe?.() } catch { /* the transport may already be closed */ }
      try { versionsUnsubscribe?.() } catch { /* the transport may already be closed */ }
      unsubscribe = null
      versionsUnsubscribe = null
      try { client?.dispose() } catch { /* never cancel native work on cleanup failure */ }
      client = null
    },
  }
}

function snapshotMatchesAttempt(next: PipelineSnapshot, attempt: Pending): boolean {
  const { baseline: previous, request } = attempt
  if (next.projectId !== request.params.projectId) return false
  if (previous === null) return request.method === 'pipeline.read'
  if (request.method === 'pipeline.retry' || request.method === 'pipeline.regenerate' || request.method === 'pipeline.restore') {
    if (next.executionId === previous.executionId || next.rootJobId === previous.rootJobId) return false
    if (request.method === 'pipeline.regenerate') return next.parentVersionId === request.params.expectedVersionId &&
      next.parentDigest === request.params.expectedVersionDigest && next.settingsSnapshotDigest === request.params.expectedSettingsSnapshotDigest &&
      next.ingestJobId === previous.ingestJobId
    // Restoring regenerates from a NEW child version, so its parent is neither
    // the previous head nor the selected past version.
    if (request.method === 'pipeline.restore') return next.parentVersionId !== null &&
      next.parentVersionId !== previous.versionId && next.parentVersionId !== request.params.targetVersionId &&
      next.settingsSnapshotDigest === request.params.expectedSettingsSnapshotDigest &&
      next.ingestJobId === previous.ingestJobId
    return next.parentVersionId === previous.parentVersionId && next.parentDigest === previous.parentDigest &&
      next.settingsSnapshotDigest === previous.settingsSnapshotDigest
  }
  if (next.executionId !== previous.executionId || next.revision < previous.revision || next.rootJobId !== previous.rootJobId ||
      next.ingestJobId !== previous.ingestJobId || next.settingsSnapshotDigest !== previous.settingsSnapshotDigest ||
      next.parentVersionId !== previous.parentVersionId || next.parentDigest !== previous.parentDigest ||
      (previous.versionId !== null && (next.versionId !== previous.versionId || next.versionDigest !== previous.versionDigest))) return false
  if (next.revision === previous.revision) return next.phase === previous.phase && next.failureCode === previous.failureCode &&
    next.versionId === previous.versionId && next.versionDigest === previous.versionDigest &&
    (next.result === null || previous.result === null || JSON.stringify(next.result) === JSON.stringify(previous.result))
  // A terminal execution cannot resume in place. Retry owns a fresh execution.
  return isPipelineLive(previous.phase) && (isPipelineLive(next.phase)
    ? phaseOrder(next.phase) >= phaseOrder(previous.phase) : true)
}
function phaseOrder(phase: PipelineSnapshot['phase']): number {
  return ['Queued', 'Ingesting', 'Analyzing', 'Composing', 'Rendering', 'Evaluating', 'Delivering'].indexOf(phase)
}