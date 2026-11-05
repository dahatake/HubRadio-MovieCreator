// FR-145 / NFR-REL-002 GUI bridge protocol for the automatic template selection of a Project. Closed shapes mirror
// schemas/web-bridge.schema.json (templateAuto.get / templateAuto.touch). No path, exception text or SID is received.

export const TEMPLATE_AUTO_GET_METHOD = 'templateAuto.get' as const
export const TEMPLATE_AUTO_TOUCH_METHOD = 'templateAuto.touch' as const
export const TEMPLATE_AUTO_TIMEOUT_MS = 10_000

export const TEMPLATE_AUTO_STATUSES = ['ok', 'forbidden', 'invalid', 'unavailable'] as const
export const TEMPLATE_AUTO_STATES = ['none', 'waiting', 'analyzing', 'selected', 'cancelled', 'skipped'] as const
export type TemplateAutoStatus = typeof TEMPLATE_AUTO_STATUSES[number]
export type TemplateAutoState = typeof TEMPLATE_AUTO_STATES[number]
export type TemplateAutoMethod = typeof TEMPLATE_AUTO_GET_METHOD | typeof TEMPLATE_AUTO_TOUCH_METHOD

export type TemplateAutoJobOrigin = 'auto' | 'manual' | 'none'
export const TEMPLATE_AUTO_JOB_ORIGINS = ['auto', 'manual', 'none'] as const
export type TemplateAutoResult = Readonly<{ status: TemplateAutoStatus; state: TemplateAutoState; templateId: string | null; reason: string; jobOrigin: TemplateAutoJobOrigin | null }>
export type TemplateAutoRequest = Readonly<{ version: 1; requestId: string; method: TemplateAutoMethod; params: Readonly<{ projectId: string }> }>
export type TemplateAutoResponse = Readonly<{ version: 1; requestId: string; method: TemplateAutoMethod; result: TemplateAutoResult }>
export type TemplateAutoOutcome = TemplateAutoResult | Readonly<{ status: 'timeout' }> | Readonly<{ status: 'disconnected' }>

const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const templateIdPattern = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/
const isId = (value: unknown): value is string => typeof value === 'string' && idPattern.test(value) && value !== '00000000-0000-0000-0000-000000000000'
const oneOf = <T extends string>(list: readonly T[], value: unknown): value is T => typeof value === 'string' && (list as readonly string[]).includes(value)

function exactKeys(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
  const actual = Object.keys(value)
  return actual.length === keys.length && keys.every(key => Object.hasOwn(value, key))
}

export function createTemplateAutoRequest(requestId: string, method: TemplateAutoMethod, projectId: string): TemplateAutoRequest {
  if (!isId(requestId) || !isId(projectId)) throw new TypeError('Invalid template auto-selection request.')
  return Object.freeze({ version: 1, requestId, method, params: Object.freeze({ projectId }) })
}

function parseResult(value: unknown): TemplateAutoResult | null {
  if (!exactKeys(value, ['status', 'state', 'templateId', 'reason', 'jobOrigin'])) return null
  const { status, state, templateId, reason, jobOrigin } = value
  if (!oneOf(TEMPLATE_AUTO_STATUSES, status) || !oneOf(TEMPLATE_AUTO_STATES, state)) return null
  if (jobOrigin !== null && !oneOf(TEMPLATE_AUTO_JOB_ORIGINS, jobOrigin)) return null
  if (templateId !== null && (typeof templateId !== 'string' || templateId.length > 64 || !templateIdPattern.test(templateId))) return null
  if (typeof reason !== 'string' || reason.length > 400) return null
  return Object.freeze({ status, state, templateId, reason, jobOrigin })
}

export function parseTemplateAutoResponse(value: unknown): TemplateAutoResponse | null {
  if (!exactKeys(value, ['version', 'requestId', 'method', 'result']) || value.version !== 1 || !isId(value.requestId)) return null
  if (!oneOf([TEMPLATE_AUTO_GET_METHOD, TEMPLATE_AUTO_TOUCH_METHOD] as const, value.method)) return null
  const result = parseResult(value.result)
  return result === null ? null : Object.freeze({ version: 1, requestId: value.requestId, method: value.method, result })
}

export interface TemplateAutoTransport {
  postMessage(message: TemplateAutoRequest): void
  addEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
  removeEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
}

export interface TemplateAutoService {
  /** The automatic selection of the Project: 解析中, the chosen template and the reason, or why none was chosen. */
  get(projectId: string): Promise<TemplateAutoOutcome>
  /** The user chose a template themself: an automatic selection still running is cancelled and never overwrites the choice. */
  touch(projectId: string): Promise<TemplateAutoOutcome>
  dispose(): void
}

export function createTemplateAutoService(transport: TemplateAutoTransport, options: { readonly createRequestId?: () => string; readonly timeoutMilliseconds?: number } = {}): TemplateAutoService {
  const newId = options.createRequestId ?? (() => globalThis.crypto.randomUUID().toLowerCase())
  const timeout = options.timeoutMilliseconds ?? TEMPLATE_AUTO_TIMEOUT_MS
  const pending = new Map<string, { readonly method: TemplateAutoMethod; readonly settle: (response: TemplateAutoResponse | null) => void }>()
  let disposed = false

  const receive = (event: { readonly data: unknown }): void => {
    const response = parseTemplateAutoResponse(event.data)
    if (response === null) return
    const entry = pending.get(response.requestId)
    if (entry === undefined || entry.method !== response.method) return
    pending.delete(response.requestId)
    entry.settle(response)
  }
  transport.addEventListener('message', receive)

  const send = (method: TemplateAutoMethod, projectId: string): Promise<TemplateAutoOutcome> => {
    if (disposed) return Promise.resolve(Object.freeze({ status: 'disconnected' as const }))
    const request = createTemplateAutoRequest(newId(), method, projectId)
    return new Promise<TemplateAutoResponse | 'timeout' | 'disconnected'>(resolve => {
      const timer = setTimeout(() => { pending.delete(request.requestId); resolve('timeout') }, timeout)
      pending.set(request.requestId, { method, settle: response => { clearTimeout(timer); resolve(response ?? 'disconnected') } })
      try { transport.postMessage(request) }
      catch { pending.delete(request.requestId); clearTimeout(timer); resolve('disconnected') }
    }).then(settled => typeof settled === 'string' ? Object.freeze({ status: settled }) : settled.result)
  }

  return Object.freeze({
    get: (projectId: string) => send(TEMPLATE_AUTO_GET_METHOD, projectId),
    touch: (projectId: string) => send(TEMPLATE_AUTO_TOUCH_METHOD, projectId),
    dispose() {
      if (disposed) return
      disposed = true
      transport.removeEventListener('message', receive)
      for (const entry of [...pending.values()]) entry.settle(null)
      pending.clear()
    },
  })
}

/** Returns null when the WebView2 host transport is absent (for example in a plain browser). */
export function createHostTemplateAutoService(): TemplateAutoService | null {
  if (typeof window === 'undefined') return null
  const candidate = (window as Window & { readonly chrome?: { readonly webview?: unknown } }).chrome?.webview
  if (typeof candidate !== 'object' || candidate === null) return null
  const transport = candidate as Partial<TemplateAutoTransport>
  if (typeof transport.postMessage !== 'function' || typeof transport.addEventListener !== 'function' ||
      typeof transport.removeEventListener !== 'function') return null
  return createTemplateAutoService(transport as TemplateAutoTransport)
}
