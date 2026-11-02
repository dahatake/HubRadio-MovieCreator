// NFR-BR-005 AC-126 GUI bridge protocol. Closed shapes mirror schemas/web-bridge.schema.json (disclosure.*).

export const DISCLOSURE_LIST_METHOD = 'disclosure.list' as const
export const DISCLOSURE_ADD_LABEL_METHOD = 'disclosure.addLabel' as const
export const DISCLOSURE_MAX_DESTINATION_LENGTH = 64
export const DISCLOSURE_MAX_LABEL_LENGTH = 200
export const DISCLOSURE_TIMEOUT_MS = 30_000

export const DISCLOSURE_ELEMENT_KINDS = ['SpeakerEstimate', 'AutoSubtitle', 'TopicSummaryDraft', 'SubstantialEdit'] as const
export type DisclosureElementKind = typeof DISCLOSURE_ELEMENT_KINDS[number]

export const DISCLOSURE_STATUSES = ['listed', 'added', 'forbidden', 'invalid', 'unavailable'] as const
export type DisclosureStatus = typeof DISCLOSURE_STATUSES[number]

export type DisclosureElement = Readonly<{
  kind: DisclosureElementKind
  elementRef: string
  provider: string
  modelIdentifier: string
  modelVersion: string
  recordedAt: string
}>

export type DisclosureLabel = Readonly<{ destination: string; labelText: string; addedAt: string }>

export type DisclosureResult = Readonly<{
  status: DisclosureStatus
  elements: readonly DisclosureElement[]
  labels: readonly DisclosureLabel[]
}>

export type DisclosureRequest =
  | Readonly<{ version: 1; requestId: string; method: typeof DISCLOSURE_LIST_METHOD; params: Readonly<{ projectId: string }> }>
  | Readonly<{
    version: 1
    requestId: string
    method: typeof DISCLOSURE_ADD_LABEL_METHOD
    params: Readonly<{ projectId: string; destination: string; labelText: string }>
  }>

export type DisclosureResponse = Readonly<{
  version: 1
  requestId: string
  method: typeof DISCLOSURE_LIST_METHOD | typeof DISCLOSURE_ADD_LABEL_METHOD
  result: DisclosureResult
}>

export type DisclosureOutcome = DisclosureResult | Readonly<{ status: 'timeout' }> | Readonly<{ status: 'disconnected' }>

const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const maxItems = 100

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

function boundedText(value: string, maximum: number): boolean {
  return value.trim().length > 0 && value.length <= maximum
}

export function createDisclosureListRequest(requestId: string, projectId: string): DisclosureRequest {
  if (!isId(requestId) || !isId(projectId)) throw new TypeError('Invalid disclosure request.')
  return Object.freeze({ version: 1, requestId, method: DISCLOSURE_LIST_METHOD, params: Object.freeze({ projectId }) })
}

export function createDisclosureAddLabelRequest(
  requestId: string, projectId: string, destination: string, labelText: string,
): DisclosureRequest {
  if (!isId(requestId) || !isId(projectId) || !boundedText(destination, DISCLOSURE_MAX_DESTINATION_LENGTH) ||
      !boundedText(labelText, DISCLOSURE_MAX_LABEL_LENGTH)) throw new TypeError('Invalid disclosure request.')
  return Object.freeze({
    version: 1, requestId, method: DISCLOSURE_ADD_LABEL_METHOD,
    params: Object.freeze({ projectId, destination, labelText }),
  })
}

function parseResult(value: unknown): DisclosureResult | null {
  if (!exactKeys(value, ['status', 'elements', 'labels'])) return null
  const { status, elements, labels } = value
  if (!oneOf(DISCLOSURE_STATUSES, status)) return null
  if (!Array.isArray(elements) || elements.length > maxItems || !Array.isArray(labels) || labels.length > maxItems) return null
  const parsedElements: DisclosureElement[] = []
  for (const element of elements) {
    if (!exactKeys(element, ['kind', 'elementRef', 'provider', 'modelIdentifier', 'modelVersion', 'recordedAt']) ||
        !oneOf(DISCLOSURE_ELEMENT_KINDS, element.kind) ||
        typeof element.elementRef !== 'string' || typeof element.provider !== 'string' ||
        typeof element.modelIdentifier !== 'string' || typeof element.modelVersion !== 'string' ||
        typeof element.recordedAt !== 'string') return null
    parsedElements.push(Object.freeze({
      kind: element.kind, elementRef: element.elementRef, provider: element.provider,
      modelIdentifier: element.modelIdentifier, modelVersion: element.modelVersion, recordedAt: element.recordedAt,
    }))
  }
  const parsedLabels: DisclosureLabel[] = []
  for (const label of labels) {
    if (!exactKeys(label, ['destination', 'labelText', 'addedAt']) || typeof label.destination !== 'string' ||
        label.destination.length > DISCLOSURE_MAX_DESTINATION_LENGTH || typeof label.labelText !== 'string' ||
        label.labelText.length > DISCLOSURE_MAX_LABEL_LENGTH || typeof label.addedAt !== 'string') return null
    parsedLabels.push(Object.freeze({ destination: label.destination, labelText: label.labelText, addedAt: label.addedAt }))
  }
  if ((status === 'forbidden' || status === 'invalid' || status === 'unavailable') &&
      (parsedElements.length > 0 || parsedLabels.length > 0)) return null
  return Object.freeze({ status, elements: Object.freeze(parsedElements), labels: Object.freeze(parsedLabels) })
}

export function parseDisclosureResponse(value: unknown): DisclosureResponse | null {
  if (!exactKeys(value, ['version', 'requestId', 'method', 'result']) || value.version !== 1 || !isId(value.requestId)) return null
  if (value.method !== DISCLOSURE_LIST_METHOD && value.method !== DISCLOSURE_ADD_LABEL_METHOD) return null
  const result = parseResult(value.result)
  return result === null ? null : Object.freeze({ version: 1, requestId: value.requestId, method: value.method, result })
}

export interface DisclosureTransport {
  postMessage(message: DisclosureRequest): void
  addEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
  removeEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
}

export interface DisclosureService {
  /** The AI-generated or substantially edited elements and the per-destination labels of a Project. */
  list(projectId: string): Promise<DisclosureOutcome>
  /** Adds one label for a publication destination; the result is the refreshed list. */
  addLabel(projectId: string, destination: string, labelText: string): Promise<DisclosureOutcome>
  dispose(): void
}

export interface DisclosureServiceOptions {
  readonly createRequestId?: () => string
  readonly timeoutMilliseconds?: number
}

export function createDisclosureService(transport: DisclosureTransport, options: DisclosureServiceOptions = {}): DisclosureService {
  const newId = options.createRequestId ?? (() => globalThis.crypto.randomUUID().toLowerCase())
  const timeout = options.timeoutMilliseconds ?? DISCLOSURE_TIMEOUT_MS
  const pending = new Map<string, (response: DisclosureResponse | null) => void>()
  let disposed = false

  const receive = (event: { readonly data: unknown }): void => {
    const data = event.data
    if (typeof data !== 'object' || data === null || !('method' in data) ||
        (data.method !== DISCLOSURE_LIST_METHOD && data.method !== DISCLOSURE_ADD_LABEL_METHOD)) return
    const response = parseDisclosureResponse(data)
    if (response === null) return
    const settle = pending.get(response.requestId)
    if (settle === undefined) return
    pending.delete(response.requestId)
    settle(response)
  }
  transport.addEventListener('message', receive)

  function send(build: (requestId: string) => DisclosureRequest): Promise<DisclosureOutcome> {
    if (disposed) return Promise.resolve(Object.freeze({ status: 'disconnected' as const }))
    const request = build(newId())
    return new Promise<DisclosureResponse | 'timeout' | 'disconnected'>(resolve => {
      const timer = setTimeout(() => { pending.delete(request.requestId); resolve('timeout') }, timeout)
      pending.set(request.requestId, response => { clearTimeout(timer); resolve(response ?? 'disconnected') })
      try { transport.postMessage(request) }
      catch { pending.delete(request.requestId); clearTimeout(timer); resolve('disconnected') }
    }).then(settled => typeof settled === 'string' ? Object.freeze({ status: settled }) : settled.result)
  }

  return Object.freeze({
    list: (projectId: string) => send(requestId => createDisclosureListRequest(requestId, projectId)),
    addLabel: (projectId: string, destination: string, labelText: string) =>
      send(requestId => createDisclosureAddLabelRequest(requestId, projectId, destination, labelText)),
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
export function createHostDisclosureService(): DisclosureService | null {
  if (typeof window === 'undefined') return null
  const candidate = (window as Window & { readonly chrome?: { readonly webview?: unknown } }).chrome?.webview
  if (typeof candidate !== 'object' || candidate === null) return null
  const transport = candidate as Partial<DisclosureTransport>
  if (typeof transport.postMessage !== 'function' || typeof transport.addEventListener !== 'function' ||
      typeof transport.removeEventListener !== 'function') return null
  return createDisclosureService(transport as DisclosureTransport)
}
