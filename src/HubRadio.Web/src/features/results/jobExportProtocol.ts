// FR-132 GUI bridge protocol. Closed shapes mirror schemas/web-bridge.schema.json (results.export).

export const JOB_EXPORT_METHOD = 'results.export' as const
export const JOB_EXPORT_MAX_PATH_LENGTH = 260
export const JOB_EXPORT_MAX_JOB_NAME_LENGTH = 40
/** Copying large videos can take a while; the native side answers only when every file is done. */
export const JOB_EXPORT_TIMEOUT_MS = 30 * 60_000

export const JOB_EXPORT_KINDS = ['longClean', 'longBurnIn', 'shortClean', 'shortBurnIn', 'thumbnailLandscape', 'thumbnailVertical'] as const
export type JobExportKind = typeof JOB_EXPORT_KINDS[number]

export const JOB_EXPORT_FAILURE_REASONS = ['missing', 'notWritable', 'diskFull', 'unavailable'] as const
export type JobExportFailureReason = typeof JOB_EXPORT_FAILURE_REASONS[number]

export const JOB_EXPORT_STATUSES = ['completed', 'forbidden', 'invalid', 'notFound', 'destinationInvalid', 'busy', 'unavailable'] as const
export type JobExportStatus = typeof JOB_EXPORT_STATUSES[number]

export const JOB_EXPORT_DESTINATION_REASONS = ['notFound', 'notFolder', 'notWritable'] as const
export type JobExportDestinationReason = typeof JOB_EXPORT_DESTINATION_REASONS[number]

export type JobExportParams = Readonly<{
  projectId: string
  executionId: string
  destination: string
  jobName: string | null
}>

export type JobExportRequest = Readonly<{ version: 1; requestId: string; method: typeof JOB_EXPORT_METHOD; params: JobExportParams }>

export type JobExportResult = Readonly<{
  status: JobExportStatus
  destinationReason: JobExportDestinationReason | null
  savedCount: number
  files: readonly Readonly<{ kind: JobExportKind; fileName: string }>[]
  failures: readonly Readonly<{ kind: JobExportKind; reason: JobExportFailureReason }>[]
}>

export type JobExportResponse = Readonly<{ version: 1; requestId: string; method: typeof JOB_EXPORT_METHOD; result: JobExportResult }>

export type JobExportOutcome = JobExportResult | Readonly<{ status: 'timeout' }> | Readonly<{ status: 'disconnected' }>

const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const fileNamePattern = /^[^\\/:]+$/
const maxItems = 32

function isId(value: unknown): value is string {
  return typeof value === 'string' && idPattern.test(value) && value !== '00000000-0000-0000-0000-000000000000'
}

function exactKeys(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
  const actual = Object.keys(value)
  return actual.length === keys.length && keys.every(key => Object.hasOwn(value, key))
}

function oneOf<T extends string>(list: readonly T[], value: unknown): value is T {
  return typeof value === 'string' && (list as readonly string[]).includes(value)
}

export function createJobExportRequest(requestId: string, params: JobExportParams): JobExportRequest {
  const name = params.jobName
  if (!isId(requestId) || !isId(params.projectId) || !isId(params.executionId) ||
      params.destination.length === 0 || params.destination.length > JOB_EXPORT_MAX_PATH_LENGTH ||
      (name !== null && (name.length === 0 || name.length > JOB_EXPORT_MAX_JOB_NAME_LENGTH))) {
    throw new TypeError('Invalid job export request.')
  }
  return Object.freeze({
    version: 1, requestId, method: JOB_EXPORT_METHOD,
    params: Object.freeze({ projectId: params.projectId, executionId: params.executionId, destination: params.destination, jobName: name }),
  })
}

function parseResult(value: unknown): JobExportResult | null {
  if (!exactKeys(value, ['status', 'destinationReason', 'savedCount', 'files', 'failures'])) return null
  const { status, destinationReason, savedCount, files, failures } = value
  if (!oneOf(JOB_EXPORT_STATUSES, status)) return null
  if (destinationReason !== null && !oneOf(JOB_EXPORT_DESTINATION_REASONS, destinationReason)) return null
  if (typeof savedCount !== 'number' || !Number.isInteger(savedCount) || savedCount < 0 || savedCount > maxItems) return null
  if (!Array.isArray(files) || files.length > maxItems || !Array.isArray(failures) || failures.length > maxItems) return null
  const savedFiles: { kind: JobExportKind; fileName: string }[] = []
  for (const file of files) {
    if (!exactKeys(file, ['kind', 'fileName']) || !oneOf(JOB_EXPORT_KINDS, file.kind) ||
        typeof file.fileName !== 'string' || file.fileName.length === 0 || file.fileName.length > 255 || !fileNamePattern.test(file.fileName)) return null
    savedFiles.push(Object.freeze({ kind: file.kind, fileName: file.fileName }))
  }
  const failed: { kind: JobExportKind; reason: JobExportFailureReason }[] = []
  for (const failure of failures) {
    if (!exactKeys(failure, ['kind', 'reason']) || !oneOf(JOB_EXPORT_KINDS, failure.kind) || !oneOf(JOB_EXPORT_FAILURE_REASONS, failure.reason)) return null
    failed.push(Object.freeze({ kind: failure.kind, reason: failure.reason }))
  }
  // The count always equals the files that were really saved.
  if (savedCount !== savedFiles.length) return null
  if (status !== 'completed' && (savedFiles.length > 0 || failed.length > 0)) return null
  if ((status === 'destinationInvalid') !== (destinationReason !== null)) return null
  return Object.freeze({ status, destinationReason, savedCount, files: Object.freeze(savedFiles), failures: Object.freeze(failed) })
}

export function parseJobExportResponse(value: unknown): JobExportResponse | null {
  if (!exactKeys(value, ['version', 'requestId', 'method', 'result']) || value.version !== 1 || !isId(value.requestId)) return null
  if (value.method !== JOB_EXPORT_METHOD) return null
  const result = parseResult(value.result)
  return result === null ? null : Object.freeze({ version: 1, requestId: value.requestId, method: JOB_EXPORT_METHOD, result })
}

export interface JobExportTransport {
  postMessage(message: JobExportRequest): void
  addEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
  removeEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
}

export interface JobExportService {
  /** Saves the Job's videos and thumbnails into the folder. Existing files are never overwritten. */
  export(params: JobExportParams): Promise<JobExportOutcome>
  dispose(): void
}

export interface JobExportServiceOptions {
  readonly createRequestId?: () => string
  readonly timeoutMilliseconds?: number
}

export function createJobExportService(transport: JobExportTransport, options: JobExportServiceOptions = {}): JobExportService {
  const newId = options.createRequestId ?? (() => globalThis.crypto.randomUUID().toLowerCase())
  const timeout = options.timeoutMilliseconds ?? JOB_EXPORT_TIMEOUT_MS
  const pending = new Map<string, (response: JobExportResponse | null) => void>()
  let disposed = false

  const receive = (event: { readonly data: unknown }): void => {
    const data = event.data
    if (typeof data !== 'object' || data === null || !('method' in data) || data.method !== JOB_EXPORT_METHOD) return
    const response = parseJobExportResponse(data)
    if (response === null) return
    const settle = pending.get(response.requestId)
    if (settle === undefined) return
    pending.delete(response.requestId)
    settle(response)
  }
  transport.addEventListener('message', receive)

  return Object.freeze({
    export(params: JobExportParams): Promise<JobExportOutcome> {
      if (disposed) return Promise.resolve(Object.freeze({ status: 'disconnected' as const }))
      const request = createJobExportRequest(newId(), params)
      return new Promise<JobExportResponse | 'timeout' | 'disconnected'>(resolve => {
        const timer = setTimeout(() => { pending.delete(request.requestId); resolve('timeout') }, timeout)
        pending.set(request.requestId, response => { clearTimeout(timer); resolve(response ?? 'disconnected') })
        try { transport.postMessage(request) }
        catch { pending.delete(request.requestId); clearTimeout(timer); resolve('disconnected') }
      }).then(settled => typeof settled === 'string' ? Object.freeze({ status: settled }) : settled.result)
    },
    dispose() {
      if (disposed) return
      disposed = true
      transport.removeEventListener('message', receive)
      for (const settle of [...pending.values()]) settle(null)
      pending.clear()
    },
  })
}

/** Returns null when the WebView2 host transport is absent (for example in a plain browser). */
export function createHostJobExportService(): JobExportService | null {
  if (typeof window === 'undefined') return null
  const candidate = (window as Window & { readonly chrome?: { readonly webview?: unknown } }).chrome?.webview
  if (typeof candidate !== 'object' || candidate === null) return null
  const transport = candidate as Partial<JobExportTransport>
  if (typeof transport.postMessage !== 'function' || typeof transport.addEventListener !== 'function' ||
      typeof transport.removeEventListener !== 'function') return null
  return createJobExportService(transport as JobExportTransport)
}
