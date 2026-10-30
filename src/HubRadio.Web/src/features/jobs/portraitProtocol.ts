// FR-137 (6) GUI bridge protocol for the user's standing pictures. Closed shapes mirror schemas/web-bridge.schema.json
// (portraits.list / portraits.register). The app bundles no picture; no path, exception text or SID is ever received.

export const PORTRAITS_LIST_METHOD = 'portraits.list' as const
export const PORTRAITS_REGISTER_METHOD = 'portraits.register' as const
export const PORTRAIT_NAME_MAX_LENGTH = 40
export const PORTRAIT_NOTE_MAX_LENGTH = 200
/** The bridge carries at most 4 MiB of image bytes (base64 in the request). */
export const PORTRAIT_IMAGE_MAX_BYTES = 4 * 1024 * 1024
export const PORTRAIT_TIMEOUT_MS = 30_000

export const PORTRAIT_STATUSES = ['ok', 'forbidden', 'invalid', 'unavailable'] as const
export type PortraitStatus = typeof PORTRAIT_STATUSES[number]

export const PORTRAIT_IMAGE_TYPES = ['png', 'jpeg', 'webp'] as const
export type PortraitImageType = typeof PORTRAIT_IMAGE_TYPES[number]

export type PortraitRightsState = 'confirmed' | 'unconfirmed'

export type PortraitEntry = Readonly<{
  id: string
  name: string
  rights: PortraitRightsState
  subtitleColor: string
  rightsNote: string | null
}>

export type PortraitRegisterParams = Readonly<{
  name: string
  imageType: PortraitImageType
  imageBase64: string
  rights: PortraitRightsState
  rightsNote: string | null
}>

export type PortraitMethod = typeof PORTRAITS_LIST_METHOD | typeof PORTRAITS_REGISTER_METHOD

export type PortraitRequest =
  | Readonly<{ version: 1; requestId: string; method: typeof PORTRAITS_LIST_METHOD; params: Readonly<Record<string, never>> }>
  | Readonly<{ version: 1; requestId: string; method: typeof PORTRAITS_REGISTER_METHOD; params: PortraitRegisterParams }>

export type PortraitResult = Readonly<{ status: PortraitStatus; portraitCount: number; portraits: readonly PortraitEntry[] }>

export type PortraitResponse = Readonly<{ version: 1; requestId: string; method: PortraitMethod; result: PortraitResult }>

export type PortraitOutcome = PortraitResult | Readonly<{ status: 'timeout' }> | Readonly<{ status: 'disconnected' }>

const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const colorPattern = /^#[0-9A-Fa-f]{6}$/
const portraitIdPattern = /^char-[0-9a-f]{12}$/
const maxItems = 16

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

export function createPortraitListRequest(requestId: string): PortraitRequest {
  if (!isId(requestId)) throw new TypeError('Invalid portrait request.')
  return Object.freeze({ version: 1, requestId, method: PORTRAITS_LIST_METHOD, params: Object.freeze({}) })
}

export function createPortraitRegisterRequest(requestId: string, params: PortraitRegisterParams): PortraitRequest {
  const name = params.name.trim()
  if (!isId(requestId) || name.length === 0 || name.length > PORTRAIT_NAME_MAX_LENGTH || /\p{Cc}/u.test(name) ||
      !oneOf(PORTRAIT_IMAGE_TYPES, params.imageType) || !oneOf(['confirmed', 'unconfirmed'] as const, params.rights) ||
      params.imageBase64.length === 0 || params.imageBase64.length > Math.ceil(PORTRAIT_IMAGE_MAX_BYTES / 3) * 4 ||
      (params.rightsNote !== null && (params.rightsNote.length > PORTRAIT_NOTE_MAX_LENGTH || /\p{Cc}/u.test(params.rightsNote)))) {
    throw new TypeError('Invalid portrait request.')
  }
  return Object.freeze({
    version: 1, requestId, method: PORTRAITS_REGISTER_METHOD,
    params: Object.freeze({ name, imageType: params.imageType, imageBase64: params.imageBase64, rights: params.rights, rightsNote: params.rightsNote }),
  })
}

function parseResult(value: unknown): PortraitResult | null {
  if (!exactKeys(value, ['status', 'portraitCount', 'portraits'])) return null
  const { status, portraitCount, portraits } = value
  if (!oneOf(PORTRAIT_STATUSES, status)) return null
  if (typeof portraitCount !== 'number' || !Number.isInteger(portraitCount) || portraitCount < 0) return null
  if (!Array.isArray(portraits) || portraits.length > maxItems) return null
  const entries: PortraitEntry[] = []
  for (const item of portraits) {
    if (!exactKeys(item, ['id', 'name', 'rights', 'subtitleColor', 'rightsNote'])) return null
    if (typeof item.id !== 'string' || !portraitIdPattern.test(item.id)) return null
    if (typeof item.name !== 'string' || item.name.length === 0 || item.name.length > PORTRAIT_NAME_MAX_LENGTH) return null
    if (!oneOf(['confirmed', 'unconfirmed'] as const, item.rights)) return null
    if (typeof item.subtitleColor !== 'string' || !colorPattern.test(item.subtitleColor)) return null
    if (item.rightsNote !== null && (typeof item.rightsNote !== 'string' || item.rightsNote.length > PORTRAIT_NOTE_MAX_LENGTH)) return null
    entries.push(Object.freeze({ id: item.id, name: item.name, rights: item.rights, subtitleColor: item.subtitleColor, rightsNote: item.rightsNote }))
  }
  if (portraitCount < entries.length) return null
  return Object.freeze({ status, portraitCount, portraits: Object.freeze(entries) })
}

export function parsePortraitResponse(value: unknown): PortraitResponse | null {
  if (!exactKeys(value, ['version', 'requestId', 'method', 'result']) || value.version !== 1 || !isId(value.requestId)) return null
  if (value.method !== PORTRAITS_LIST_METHOD && value.method !== PORTRAITS_REGISTER_METHOD) return null
  const result = parseResult(value.result)
  return result === null ? null : Object.freeze({ version: 1, requestId: value.requestId, method: value.method, result })
}

export interface PortraitTransport {
  postMessage(message: PortraitRequest): void
  addEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
  removeEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
}

export interface PortraitService {
  /** The registered standing pictures with the rights status the user saved. */
  list(): Promise<PortraitOutcome>
  /** Registers the user's own picture; the rights status (確認済み / 未確認) is saved with it. */
  register(params: PortraitRegisterParams): Promise<PortraitOutcome>
  dispose(): void
}

export interface PortraitServiceOptions {
  readonly createRequestId?: () => string
  readonly timeoutMilliseconds?: number
}

export function createPortraitService(transport: PortraitTransport, options: PortraitServiceOptions = {}): PortraitService {
  const newId = options.createRequestId ?? (() => globalThis.crypto.randomUUID().toLowerCase())
  const timeout = options.timeoutMilliseconds ?? PORTRAIT_TIMEOUT_MS
  const pending = new Map<string, { readonly method: PortraitMethod; readonly settle: (response: PortraitResponse | null) => void }>()
  let disposed = false

  const receive = (event: { readonly data: unknown }): void => {
    const data = event.data
    if (typeof data !== 'object' || data === null || !('method' in data) ||
        (data.method !== PORTRAITS_LIST_METHOD && data.method !== PORTRAITS_REGISTER_METHOD)) return
    const response = parsePortraitResponse(data)
    if (response === null) return
    const entry = pending.get(response.requestId)
    if (entry === undefined || entry.method !== response.method) return
    pending.delete(response.requestId)
    entry.settle(response)
  }
  transport.addEventListener('message', receive)

  const send = (request: PortraitRequest): Promise<PortraitOutcome> => {
    if (disposed) return Promise.resolve(Object.freeze({ status: 'disconnected' as const }))
    return new Promise<PortraitResponse | 'timeout' | 'disconnected'>(resolve => {
      const timer = setTimeout(() => { pending.delete(request.requestId); resolve('timeout') }, timeout)
      pending.set(request.requestId, { method: request.method, settle: response => { clearTimeout(timer); resolve(response ?? 'disconnected') } })
      try { transport.postMessage(request) }
      catch { pending.delete(request.requestId); clearTimeout(timer); resolve('disconnected') }
    }).then(settled => typeof settled === 'string' ? Object.freeze({ status: settled }) : settled.result)
  }

  return Object.freeze({
    list: () => send(createPortraitListRequest(newId())),
    register: (params: PortraitRegisterParams) => send(createPortraitRegisterRequest(newId(), params)),
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
export function createHostPortraitService(): PortraitService | null {
  if (typeof window === 'undefined') return null
  const candidate = (window as Window & { readonly chrome?: { readonly webview?: unknown } }).chrome?.webview
  if (typeof candidate !== 'object' || candidate === null) return null
  const transport = candidate as Partial<PortraitTransport>
  if (typeof transport.postMessage !== 'function' || typeof transport.addEventListener !== 'function' ||
      typeof transport.removeEventListener !== 'function') return null
  return createPortraitService(transport as PortraitTransport)
}

function readBuffer(file: File): Promise<ArrayBuffer> {
  if (typeof file.arrayBuffer === 'function') return file.arrayBuffer()
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(reader.result as ArrayBuffer)
    reader.onerror = () => reject(reader.error ?? new Error('read failed'))
    reader.readAsArrayBuffer(file)
  })
}

/** Reads a File into the register parameters (kind from its type, bytes as base64). Returns null for a file the bridge cannot carry. */
export async function portraitFileToParams(
  file: File, name: string, rights: PortraitRightsState, rightsNote: string | null = null,
): Promise<PortraitRegisterParams | null> {
  const imageType = file.type === 'image/png' ? 'png' : file.type === 'image/jpeg' ? 'jpeg' : file.type === 'image/webp' ? 'webp' : null
  if (imageType === null || file.size === 0 || file.size > PORTRAIT_IMAGE_MAX_BYTES) return null
  const bytes = new Uint8Array(await readBuffer(file))
  let binary = ''
  for (let offset = 0; offset < bytes.length; offset += 0x8000) binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000))
  return { name: name.trim(), imageType, imageBase64: globalThis.btoa(binary), rights, rightsNote }
}
