// FR-035 GUI bridge protocol. Closed shapes mirror schemas/web-bridge.schema.json.

export const STITCH_PICK_CLIP_METHOD = 'stitch.pickClip' as const
export const STITCH_RUN_METHOD = 'stitch.run' as const
export const STITCH_PICK_TIMEOUT_MS = 600_000
export const STITCH_RUN_TIMEOUT_MS = 3_660_000

export const STITCH_FAILURE_CODES = [
  'stitch.input_path_invalid', 'stitch.input_not_found', 'stitch.input_unsupported', 'stitch.input_invalid_duration',
  'stitch.unsupported_geometry', 'stitch.unsupported_frame_rate', 'stitch.output_path_invalid', 'stitch.output_directory_missing',
  'stitch.output_exists', 'stitch.tool_unverified', 'stitch.encode_failed', 'stitch.timeout', 'stitch.output_verification_failed',
  'stitch.cancelled', 'stitch.internal_error',
] as const
export type StitchFailureCode = typeof STITCH_FAILURE_CODES[number]

const PICK_STATUSES = ['selected', 'cancelled', 'unsupported', 'forbidden', 'unavailable'] as const
const RUN_STATUSES = ['completed', 'cancelled', 'failed', 'notFound', 'invalid', 'forbidden', 'busy', 'unavailable'] as const

export type StitchPickClipResult =
  | Readonly<{ status: 'selected'; clipId: string; fileName: string }>
  | Readonly<{ status: Exclude<typeof PICK_STATUSES[number], 'selected'>; clipId: null; fileName: null }>

export type StitchRunResult =
  | Readonly<{ status: 'completed'; code: null; outputFileName: string; manifestFileName: string; durationSeconds: number }>
  | Readonly<{ status: 'failed'; code: StitchFailureCode; outputFileName: null; manifestFileName: null; durationSeconds: null }>
  | Readonly<{ status: Exclude<typeof RUN_STATUSES[number], 'completed' | 'failed'>; code: null; outputFileName: null; manifestFileName: null; durationSeconds: null }>

export interface StitchRunParams {
  readonly projectId: string
  readonly executionId: string
  readonly artifactId: string
  readonly artifactDigest: string
  readonly openingClipId: string | null
  readonly closingClipId: string | null
}

export type StitchRequest =
  | Readonly<{ version: 1; requestId: string; method: typeof STITCH_PICK_CLIP_METHOD; params: Readonly<Record<string, never>> }>
  | Readonly<{ version: 1; requestId: string; method: typeof STITCH_RUN_METHOD; params: StitchRunParams }>

export type StitchResponse =
  | Readonly<{ version: 1; requestId: string; method: typeof STITCH_PICK_CLIP_METHOD; result: StitchPickClipResult }>
  | Readonly<{ version: 1; requestId: string; method: typeof STITCH_RUN_METHOD; result: StitchRunResult }>

const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const digestPattern = /^[0-9a-f]{64}$/
const fileNamePattern = /^[^\\/:]{1,255}$/

export function isStitchId(value: unknown): value is string {
  return typeof value === 'string' && idPattern.test(value) && value !== '00000000-0000-0000-0000-000000000000'
}

export function createStitchPickClipRequest(requestId: string): StitchRequest {
  if (!isStitchId(requestId)) throw new TypeError('Invalid stitch request ID.')
  return Object.freeze({ version: 1, requestId, method: STITCH_PICK_CLIP_METHOD, params: Object.freeze({}) })
}

export function createStitchRunRequest(requestId: string, params: StitchRunParams): StitchRequest {
  if (!isStitchId(requestId) || !isStitchId(params.projectId) || !isStitchId(params.executionId) || !isStitchId(params.artifactId) ||
      typeof params.artifactDigest !== 'string' || !digestPattern.test(params.artifactDigest) ||
      (params.openingClipId !== null && !isStitchId(params.openingClipId)) ||
      (params.closingClipId !== null && !isStitchId(params.closingClipId)) ||
      (params.openingClipId === null && params.closingClipId === null)) throw new TypeError('Invalid stitch run parameters.')
  return Object.freeze({ version: 1, requestId, method: STITCH_RUN_METHOD, params: Object.freeze({
    projectId: params.projectId, executionId: params.executionId, artifactId: params.artifactId,
    artifactDigest: params.artifactDigest, openingClipId: params.openingClipId, closingClipId: params.closingClipId,
  }) })
}

function exactKeys(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
  const actual = Object.keys(value)
  return actual.length === keys.length && keys.every(key => Object.hasOwn(value, key))
}

function isFileName(value: unknown): value is string {
  return typeof value === 'string' && fileNamePattern.test(value)
}

function parsePickResult(value: unknown): StitchPickClipResult | null {
  if (!exactKeys(value, ['status', 'clipId', 'fileName'])) return null
  const { status, clipId, fileName } = value
  if (status === 'selected') return isStitchId(clipId) && isFileName(fileName) ? Object.freeze({ status, clipId, fileName }) : null
  return (PICK_STATUSES as readonly unknown[]).includes(status) && clipId === null && fileName === null
    ? Object.freeze({ status: status as Exclude<typeof PICK_STATUSES[number], 'selected'>, clipId: null, fileName: null }) : null
}

function parseRunResult(value: unknown): StitchRunResult | null {
  if (!exactKeys(value, ['status', 'code', 'outputFileName', 'manifestFileName', 'durationSeconds'])) return null
  const { status, code, outputFileName, manifestFileName, durationSeconds } = value
  if (status === 'completed') {
    return code === null && isFileName(outputFileName) && isFileName(manifestFileName) &&
      typeof durationSeconds === 'number' && Number.isFinite(durationSeconds) && durationSeconds > 0
      ? Object.freeze({ status, code: null, outputFileName, manifestFileName, durationSeconds }) : null
  }
  if (outputFileName !== null || manifestFileName !== null || durationSeconds !== null) return null
  if (status === 'failed') {
    return (STITCH_FAILURE_CODES as readonly unknown[]).includes(code)
      ? Object.freeze({ status, code: code as StitchFailureCode, outputFileName: null, manifestFileName: null, durationSeconds: null }) : null
  }
  return (RUN_STATUSES as readonly unknown[]).includes(status) && code === null
    ? Object.freeze({ status: status as Exclude<typeof RUN_STATUSES[number], 'completed' | 'failed'>, code: null,
      outputFileName: null, manifestFileName: null, durationSeconds: null }) : null
}

export function parseStitchResponse(value: unknown): StitchResponse | null {
  if (!exactKeys(value, ['version', 'requestId', 'method', 'result']) || value.version !== 1 || !isStitchId(value.requestId)) return null
  const requestId = value.requestId
  if (value.method === STITCH_PICK_CLIP_METHOD) {
    const result = parsePickResult(value.result)
    return result === null ? null : Object.freeze({ version: 1, requestId, method: STITCH_PICK_CLIP_METHOD, result })
  }
  if (value.method === STITCH_RUN_METHOD) {
    const result = parseRunResult(value.result)
    return result === null ? null : Object.freeze({ version: 1, requestId, method: STITCH_RUN_METHOD, result })
  }
  return null
}

export interface StitchTransport {
  postMessage(message: StitchRequest): void
  addEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
  removeEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
}

export type StitchOutcome<T> = T | Readonly<{ status: 'timeout' }> | Readonly<{ status: 'disconnected' }>

/** UI-facing service. Paths never appear here; the native host owns all dialogs and files. */
export interface BookendStitchService {
  pickClip(): Promise<StitchOutcome<StitchPickClipResult>>
  run(params: StitchRunParams): Promise<StitchOutcome<StitchRunResult>>
  dispose(): void
}

export interface BookendStitchServiceOptions {
  readonly createRequestId?: () => string
  readonly pickTimeoutMilliseconds?: number
  readonly runTimeoutMilliseconds?: number
}

type Settled<T> = T | 'timeout' | 'disconnected'

export function createBookendStitchService(transport: StitchTransport, options: BookendStitchServiceOptions = {}): BookendStitchService {
  const newId = options.createRequestId ?? (() => globalThis.crypto.randomUUID().toLowerCase())
  const pickTimeout = options.pickTimeoutMilliseconds ?? STITCH_PICK_TIMEOUT_MS
  const runTimeout = options.runTimeoutMilliseconds ?? STITCH_RUN_TIMEOUT_MS
  const pending = new Map<string, Readonly<{ method: StitchRequest['method']; settle: (response: StitchResponse | null) => void }>>()
  let disposed = false
  const receive = (event: { readonly data: unknown }): void => {
    const data = event.data
    if (typeof data !== 'object' || data === null || !('method' in data) ||
        typeof data.method !== 'string' || !data.method.startsWith('stitch.')) return
    const response = parseStitchResponse(data)
    if (response === null) return
    const waiting = pending.get(response.requestId)
    if (waiting === undefined || waiting.method !== response.method) return
    pending.delete(response.requestId)
    waiting.settle(response)
  }
  transport.addEventListener('message', receive)

  function send(request: StitchRequest, timeoutMs: number): Promise<Settled<StitchResponse>> {
    if (disposed) return Promise.resolve('disconnected')
    return new Promise(resolve => {
      const timer = setTimeout(() => { pending.delete(request.requestId); resolve('timeout') }, timeoutMs)
      pending.set(request.requestId, { method: request.method, settle: response => {
        clearTimeout(timer)
        resolve(response ?? 'disconnected')
      } })
      try { transport.postMessage(request) }
      catch { pending.delete(request.requestId); clearTimeout(timer); resolve('disconnected') }
    })
  }

  return Object.freeze({
    async pickClip(): Promise<StitchOutcome<StitchPickClipResult>> {
      const response = await send(createStitchPickClipRequest(newId()), pickTimeout)
      if (response === 'timeout' || response === 'disconnected') return Object.freeze({ status: response })
      return response.method === STITCH_PICK_CLIP_METHOD ? response.result : Object.freeze({ status: 'disconnected' })
    },
    async run(params: StitchRunParams): Promise<StitchOutcome<StitchRunResult>> {
      const response = await send(createStitchRunRequest(newId(), params), runTimeout)
      if (response === 'timeout' || response === 'disconnected') return Object.freeze({ status: response })
      return response.method === STITCH_RUN_METHOD ? response.result : Object.freeze({ status: 'disconnected' })
    },
    dispose() {
      if (disposed) return
      disposed = true
      transport.removeEventListener('message', receive)
      for (const waiting of pending.values()) waiting.settle(null)
      pending.clear()
    },
  })
}

/** Returns null when the WebView2 host transport is absent (for example in a plain browser). */
export function createHostBookendStitchService(): BookendStitchService | null {
  if (typeof window === 'undefined') return null
  const candidate = (window as Window & { readonly chrome?: { readonly webview?: unknown } }).chrome?.webview
  if (typeof candidate !== 'object' || candidate === null) return null
  const transport = candidate as Partial<StitchTransport>
  if (typeof transport.postMessage !== 'function' || typeof transport.addEventListener !== 'function' ||
      typeof transport.removeEventListener !== 'function') return null
  return createBookendStitchService(transport as StitchTransport)
}
