// FR-062 Workspace folder location bridge protocol. Closed shapes mirror schemas/web-bridge.schema.json.

export const WORKSPACE_LOCATION_READ_METHOD = 'settings.workspace.read' as const
export const WORKSPACE_LOCATION_UPDATE_METHOD = 'settings.workspace.update' as const
export const WORKSPACE_LOCATION_BROWSE_METHOD = 'settings.workspace.browse' as const
export const WORKSPACE_LOCATION_MAX_PATH_LENGTH = 260
export const WORKSPACE_LOCATION_TIMEOUT_MS = 30_000
export const WORKSPACE_LOCATION_BROWSE_TIMEOUT_MS = 30 * 60_000

export type WorkspaceLocationFailureStatus = 'invalid' | 'forbidden' | 'unavailable'

export type WorkspaceLocationResult =
  | Readonly<{ status: 'available'; currentPath: string; savedPath: string | null; defaultPath: string; restartRequired: boolean }>
  | Readonly<{ status: WorkspaceLocationFailureStatus; currentPath: null; savedPath: null; defaultPath: null; restartRequired: false }>

export type WorkspaceLocationRequest =
  | Readonly<{ version: 1; requestId: string; method: typeof WORKSPACE_LOCATION_READ_METHOD; params: Readonly<Record<string, never>> }>
  | Readonly<{ version: 1; requestId: string; method: typeof WORKSPACE_LOCATION_UPDATE_METHOD; params: Readonly<{ path: string | null }> }>
  | Readonly<{ version: 1; requestId: string; method: typeof WORKSPACE_LOCATION_BROWSE_METHOD; params: Readonly<Record<string, never>> }>

/** FR-064: the folder chosen in the native dialog. Only `selected` carries a path. */
export type WorkspaceBrowseResult =
  | Readonly<{ status: 'selected'; path: string }>
  | Readonly<{ status: 'cancelled' | 'failed' | 'forbidden' | 'unavailable'; path: null }>

export type WorkspaceBrowseResponse = Readonly<{
  version: 1
  requestId: string
  method: typeof WORKSPACE_LOCATION_BROWSE_METHOD
  result: WorkspaceBrowseResult
}>

export type WorkspaceBrowseOutcome = WorkspaceBrowseResult | Readonly<{ status: 'timeout' }> | Readonly<{ status: 'disconnected' }>

export type WorkspaceLocationResponse = Readonly<{
  version: 1
  requestId: string
  method: WorkspaceLocationRequest['method']
  result: WorkspaceLocationResult
}>

const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const FAILURE_STATUSES: readonly unknown[] = ['invalid', 'forbidden', 'unavailable']

function isRequestId(value: unknown): value is string {
  return typeof value === 'string' && idPattern.test(value) && value !== '00000000-0000-0000-0000-000000000000'
}

function isPathText(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= WORKSPACE_LOCATION_MAX_PATH_LENGTH
}

export function createWorkspaceLocationReadRequest(requestId: string): WorkspaceLocationRequest {
  if (!isRequestId(requestId)) throw new TypeError('Invalid workspace location request ID.')
  return Object.freeze({ version: 1, requestId, method: WORKSPACE_LOCATION_READ_METHOD, params: Object.freeze({}) })
}

/** A null path resets to the default location. */
export function createWorkspaceLocationUpdateRequest(requestId: string, path: string | null): WorkspaceLocationRequest {
  if (!isRequestId(requestId) || (path !== null && (typeof path !== 'string' || path.length > WORKSPACE_LOCATION_MAX_PATH_LENGTH))) {
    throw new TypeError('Invalid workspace location update request.')
  }
  return Object.freeze({ version: 1, requestId, method: WORKSPACE_LOCATION_UPDATE_METHOD, params: Object.freeze({ path }) })
}

function exactKeys(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
  const actual = Object.keys(value)
  return actual.length === keys.length && keys.every(key => Object.hasOwn(value, key))
}

export function createWorkspaceBrowseRequest(requestId: string): WorkspaceLocationRequest {
  if (!isRequestId(requestId)) throw new TypeError('Invalid workspace browse request ID.')
  return Object.freeze({ version: 1, requestId, method: WORKSPACE_LOCATION_BROWSE_METHOD, params: Object.freeze({}) })
}

const BROWSE_FAILURES: readonly unknown[] = ['cancelled', 'failed', 'forbidden', 'unavailable']

function parseBrowseResult(value: unknown): WorkspaceBrowseResult | null {
  if (!exactKeys(value, ['status', 'path'])) return null
  const { status, path } = value
  if (status === 'selected') return isPathText(path) ? Object.freeze({ status, path }) : null
  return BROWSE_FAILURES.includes(status) && path === null
    ? Object.freeze({ status: status as 'cancelled' | 'failed' | 'forbidden' | 'unavailable', path: null })
    : null
}

export function parseWorkspaceBrowseResponse(value: unknown): WorkspaceBrowseResponse | null {
  if (!exactKeys(value, ['version', 'requestId', 'method', 'result']) || value.version !== 1 || !isRequestId(value.requestId)) return null
  if (value.method !== WORKSPACE_LOCATION_BROWSE_METHOD) return null
  const result = parseBrowseResult(value.result)
  return result === null ? null : Object.freeze({ version: 1, requestId: value.requestId, method: WORKSPACE_LOCATION_BROWSE_METHOD, result })
}

function parseResult(value: unknown): WorkspaceLocationResult | null {
  if (!exactKeys(value, ['status', 'currentPath', 'savedPath', 'defaultPath', 'restartRequired'])) return null
  const { status, currentPath, savedPath, defaultPath, restartRequired } = value
  if (status === 'available') {
    return isPathText(currentPath) && (savedPath === null || isPathText(savedPath)) && isPathText(defaultPath) && typeof restartRequired === 'boolean'
      ? Object.freeze({ status, currentPath, savedPath, defaultPath, restartRequired }) : null
  }
  return FAILURE_STATUSES.includes(status) && currentPath === null && savedPath === null && defaultPath === null && restartRequired === false
    ? Object.freeze({ status: status as WorkspaceLocationFailureStatus, currentPath: null, savedPath: null, defaultPath: null, restartRequired: false as const })
    : null
}

export function parseWorkspaceLocationResponse(value: unknown): WorkspaceLocationResponse | null {
  if (!exactKeys(value, ['version', 'requestId', 'method', 'result']) || value.version !== 1 || !isRequestId(value.requestId)) return null
  if (value.method !== WORKSPACE_LOCATION_READ_METHOD && value.method !== WORKSPACE_LOCATION_UPDATE_METHOD) return null
  const result = parseResult(value.result)
  return result === null ? null : Object.freeze({ version: 1, requestId: value.requestId, method: value.method, result })
}

export interface WorkspaceLocationTransport {
  postMessage(message: WorkspaceLocationRequest): void
  addEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
  removeEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
}

export type WorkspaceLocationOutcome = WorkspaceLocationResult | Readonly<{ status: 'timeout' }> | Readonly<{ status: 'disconnected' }>

export interface WorkspaceLocationService {
  read(): Promise<WorkspaceLocationOutcome>
  /** A null path resets to the default location. */
  update(path: string | null): Promise<WorkspaceLocationOutcome>
  /** FR-064: opens the native folder dialog. Returns the chosen path only; nothing is saved. */
  browse(): Promise<WorkspaceBrowseOutcome>
  dispose(): void
}

export interface WorkspaceLocationServiceOptions {
  readonly createRequestId?: () => string
  readonly timeoutMilliseconds?: number
  /** The folder dialog stays open until the user closes it, so it waits longer than a plain request. */
  readonly browseTimeoutMilliseconds?: number
}

type Settled = WorkspaceLocationResponse | WorkspaceBrowseResponse | 'timeout' | 'disconnected'

export function createWorkspaceLocationService(
  transport: WorkspaceLocationTransport,
  options: WorkspaceLocationServiceOptions = {},
): WorkspaceLocationService {
  const newId = options.createRequestId ?? (() => globalThis.crypto.randomUUID().toLowerCase())
  const timeout = options.timeoutMilliseconds ?? WORKSPACE_LOCATION_TIMEOUT_MS
  const browseTimeout = options.browseTimeoutMilliseconds ?? WORKSPACE_LOCATION_BROWSE_TIMEOUT_MS
  const pending = new Map<string, Readonly<{ method: WorkspaceLocationRequest['method']; settle: (response: WorkspaceLocationResponse | WorkspaceBrowseResponse | null) => void }>>()
  let disposed = false

  const receive = (event: { readonly data: unknown }): void => {
    const data = event.data
    if (typeof data !== 'object' || data === null || !('method' in data) ||
        typeof data.method !== 'string' || !data.method.startsWith('settings.workspace.')) return
    const response = data.method === WORKSPACE_LOCATION_BROWSE_METHOD
      ? parseWorkspaceBrowseResponse(data) : parseWorkspaceLocationResponse(data)
    if (response === null) return
    const waiting = pending.get(response.requestId)
    if (waiting === undefined || waiting.method !== response.method) return
    pending.delete(response.requestId)
    waiting.settle(response)
  }
  transport.addEventListener('message', receive)

  function send(request: WorkspaceLocationRequest): Promise<WorkspaceLocationOutcome | WorkspaceBrowseOutcome> {
    if (disposed) return Promise.resolve(Object.freeze({ status: 'disconnected' as const }))
    const limit = request.method === WORKSPACE_LOCATION_BROWSE_METHOD ? browseTimeout : timeout
    return new Promise<Settled>(resolve => {
      const timer = setTimeout(() => { pending.delete(request.requestId); resolve('timeout') }, limit)
      pending.set(request.requestId, { method: request.method, settle: response => {
        clearTimeout(timer)
        resolve(response ?? 'disconnected')
      } })
      try { transport.postMessage(request) }
      catch { pending.delete(request.requestId); clearTimeout(timer); resolve('disconnected') }
    }).then(settled => typeof settled === 'string' ? Object.freeze({ status: settled }) : settled.result)
  }

  return Object.freeze({
    read: () => send(createWorkspaceLocationReadRequest(newId())) as Promise<WorkspaceLocationOutcome>,
    update: (path: string | null) => send(createWorkspaceLocationUpdateRequest(newId(), path)) as Promise<WorkspaceLocationOutcome>,
    browse: () => send(createWorkspaceBrowseRequest(newId())) as Promise<WorkspaceBrowseOutcome>,
    dispose() {
      if (disposed) return
      disposed = true
      transport.removeEventListener('message', receive)
      for (const waiting of [...pending.values()]) waiting.settle(null)
      pending.clear()
    },
  })
}

/** Returns null when the WebView2 host transport is absent (for example in a plain browser). */
export function createHostWorkspaceLocationService(): WorkspaceLocationService | null {
  if (typeof window === 'undefined') return null
  const candidate = (window as Window & { readonly chrome?: { readonly webview?: unknown } }).chrome?.webview
  if (typeof candidate !== 'object' || candidate === null) return null
  const transport = candidate as Partial<WorkspaceLocationTransport>
  if (typeof transport.postMessage !== 'function' || typeof transport.addEventListener !== 'function' ||
      typeof transport.removeEventListener !== 'function') return null
  return createWorkspaceLocationService(transport as WorkspaceLocationTransport)
}
