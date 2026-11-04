// FR-144 GUI bridge protocol. Closed shapes mirror schemas/web-bridge.schema.json (proxy.*).

export const PROXY_GET_METHOD = 'proxy.get' as const
export const PROXY_REGENERATE_METHOD = 'proxy.regenerate' as const
export const PROXY_TIMEOUT_MS = 30_000
/** PARAM-011: the proxy's height in pixels (the host makes it; shown for the user). */
export const PROXY_HEIGHT_PX = 360
export const PROXY_MEDIA_PREFIX = 'https://player-media.appassets.example/source-only/proxy/'

export const PROXY_STATUSES = ['ready', 'missing', 'generating', 'failed', 'forbidden', 'unavailable'] as const
export type ProxyStatus = typeof PROXY_STATUSES[number]

export type ProxyResult = Readonly<{
  status: ProxyStatus
  reason: string | null
  uri: string | null
  height: number
}>

export type ProxyRequest = Readonly<{
  version: 1
  requestId: string
  method: typeof PROXY_GET_METHOD | typeof PROXY_REGENERATE_METHOD
  params: Readonly<{ draftId: string }>
}>

export type ProxyResponse = Readonly<{
  version: 1
  requestId: string
  method: typeof PROXY_GET_METHOD | typeof PROXY_REGENERATE_METHOD
  result: ProxyResult
}>

export type ProxyOutcome = ProxyResult | Readonly<{ status: 'timeout' }> | Readonly<{ status: 'disconnected' }>

const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const uriPattern = /^https:\/\/player-media\.appassets\.example\/source-only\/proxy\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\/[0-9a-f]{32}$/

function isId(value: unknown): value is string {
  return typeof value === 'string' && idPattern.test(value) && value !== '00000000-0000-0000-0000-000000000000'
}

function exactKeys(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
  const actual = Object.keys(value)
  return actual.length === keys.length && keys.every(key => Object.hasOwn(value, key))
}

export function createProxyRequest(
  requestId: string, method: ProxyRequest['method'], draftId: string,
): ProxyRequest {
  if (!isId(requestId) || !isId(draftId) || (method !== PROXY_GET_METHOD && method !== PROXY_REGENERATE_METHOD)) {
    throw new TypeError('Invalid proxy request.')
  }
  return Object.freeze({ version: 1, requestId, method, params: Object.freeze({ draftId }) })
}

function parseResult(value: unknown): ProxyResult | null {
  if (!exactKeys(value, ['status', 'reason', 'uri', 'height'])) return null
  const { status, reason, uri, height } = value
  if (typeof status !== 'string' || !(PROXY_STATUSES as readonly string[]).includes(status)) return null
  if (reason !== null && (typeof reason !== 'string' || reason.length > 300)) return null
  if (uri !== null && (typeof uri !== 'string' || !uriPattern.test(uri))) return null
  if (typeof height !== 'number' || !Number.isInteger(height) || height <= 0) return null
  if ((status === 'ready') !== (uri !== null)) return null
  return Object.freeze({ status: status as ProxyStatus, reason, uri, height })
}

export function parseProxyResponse(value: unknown): ProxyResponse | null {
  if (!exactKeys(value, ['version', 'requestId', 'method', 'result']) || value.version !== 1 || !isId(value.requestId)) return null
  if (value.method !== PROXY_GET_METHOD && value.method !== PROXY_REGENERATE_METHOD) return null
  const result = parseResult(value.result)
  return result === null ? null : Object.freeze({ version: 1, requestId: value.requestId, method: value.method, result })
}

export interface ProxyTransport {
  postMessage(message: ProxyRequest): void
  addEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
  removeEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
}

export interface ProxyService {
  /** The state of the Project's source-video proxy (and its media URI when ready). */
  get(draftId: string): Promise<ProxyOutcome>
  /** Makes the proxy again; the result is the state right after the request (normally generating). */
  regenerate(draftId: string): Promise<ProxyOutcome>
  dispose(): void
}

export interface ProxyServiceOptions {
  readonly createRequestId?: () => string
  readonly timeoutMilliseconds?: number
}

export function createProxyService(transport: ProxyTransport, options: ProxyServiceOptions = {}): ProxyService {
  const newId = options.createRequestId ?? (() => globalThis.crypto.randomUUID().toLowerCase())
  const timeout = options.timeoutMilliseconds ?? PROXY_TIMEOUT_MS
  const pending = new Map<string, (response: ProxyResponse | null) => void>()
  let disposed = false

  const receive = (event: { readonly data: unknown }): void => {
    const data = event.data
    if (typeof data !== 'object' || data === null || !('method' in data) ||
        (data.method !== PROXY_GET_METHOD && data.method !== PROXY_REGENERATE_METHOD)) return
    const response = parseProxyResponse(data)
    if (response === null) return
    const settle = pending.get(response.requestId)
    if (settle === undefined) return
    pending.delete(response.requestId)
    settle(response)
  }
  transport.addEventListener('message', receive)

  function send(method: ProxyRequest['method'], draftId: string): Promise<ProxyOutcome> {
    if (disposed) return Promise.resolve(Object.freeze({ status: 'disconnected' as const }))
    const request = createProxyRequest(newId(), method, draftId)
    return new Promise<ProxyResponse | 'timeout' | 'disconnected'>(resolve => {
      const timer = setTimeout(() => { pending.delete(request.requestId); resolve('timeout') }, timeout)
      pending.set(request.requestId, response => { clearTimeout(timer); resolve(response ?? 'disconnected') })
      try { transport.postMessage(request) }
      catch { pending.delete(request.requestId); clearTimeout(timer); resolve('disconnected') }
    }).then(settled => typeof settled === 'string' ? Object.freeze({ status: settled }) : settled.result)
  }

  return Object.freeze({
    get: (draftId: string) => send(PROXY_GET_METHOD, draftId),
    regenerate: (draftId: string) => send(PROXY_REGENERATE_METHOD, draftId),
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
export function createHostProxyService(): ProxyService | null {
  if (typeof window === 'undefined') return null
  const candidate = (window as Window & { readonly chrome?: { readonly webview?: unknown } }).chrome?.webview
  if (typeof candidate !== 'object' || candidate === null) return null
  const transport = candidate as Partial<ProxyTransport>
  if (typeof transport.postMessage !== 'function' || typeof transport.addEventListener !== 'function' ||
      typeof transport.removeEventListener !== 'function') return null
  return createProxyService(transport as ProxyTransport)
}
