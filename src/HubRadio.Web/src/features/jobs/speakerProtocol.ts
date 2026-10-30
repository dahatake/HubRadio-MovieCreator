// FR-140 GUI bridge protocol for the speaker assignment of a Project. Closed shapes mirror schemas/web-bridge.schema.json
// (speakers.get / speakers.assign / speakers.confirm). A draft is never shown as confirmed; no path, exception text or SID is received.

export const SPEAKERS_GET_METHOD = 'speakers.get' as const
export const SPEAKERS_ASSIGN_METHOD = 'speakers.assign' as const
export const SPEAKERS_CONFIRM_METHOD = 'speakers.confirm' as const
export const SPEAKER_TIMEOUT_MS = 30_000
export const SPEAKER_MAX_CUE_INDEX = 9_999
export const SPEAKER_MAX_ASSIGNMENTS = 200

export const SPEAKER_STATUSES = ['ok', 'forbidden', 'invalid', 'unavailable'] as const
export type SpeakerStatus = typeof SPEAKER_STATUSES[number]
export type SpeakerState = 'none' | 'draft' | 'confirmed'

export type SpeakerMethod = typeof SPEAKERS_GET_METHOD | typeof SPEAKERS_ASSIGN_METHOD | typeof SPEAKERS_CONFIRM_METHOD

export type SpeakerAssignmentEntry = Readonly<{ cueIndex: number; speakerId: string }>

export type SpeakerResult = Readonly<{
  status: SpeakerStatus
  state: SpeakerState
  label: string
  estimated: boolean
  assignments: readonly SpeakerAssignmentEntry[]
}>

export type SpeakerRequest =
  | Readonly<{ version: 1; requestId: string; method: typeof SPEAKERS_GET_METHOD; params: Readonly<{ projectId: string }> }>
  | Readonly<{ version: 1; requestId: string; method: typeof SPEAKERS_CONFIRM_METHOD; params: Readonly<{ projectId: string }> }>
  | Readonly<{
    version: 1; requestId: string; method: typeof SPEAKERS_ASSIGN_METHOD
    params: Readonly<{ projectId: string; cueIndex: number | null; speakerId: string | null }>
  }>

export type SpeakerResponse = Readonly<{ version: 1; requestId: string; method: SpeakerMethod; result: SpeakerResult }>
export type SpeakerOutcome = SpeakerResult | Readonly<{ status: 'timeout' }> | Readonly<{ status: 'disconnected' }>

const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const speakerIdPattern = /^char-[0-9a-f]{12}$/

const isId = (value: unknown): value is string =>
  typeof value === 'string' && idPattern.test(value) && value !== '00000000-0000-0000-0000-000000000000'

function exactKeys(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
  const actual = Object.keys(value)
  return actual.length === keys.length && keys.every(key => Object.hasOwn(value, key))
}

const oneOf = <T extends string>(list: readonly T[], value: unknown): value is T =>
  typeof value === 'string' && (list as readonly string[]).includes(value)

function requireIds(requestId: string, projectId: string): void {
  if (!isId(requestId) || !isId(projectId)) throw new TypeError('Invalid speaker request.')
}

export function createSpeakerGetRequest(requestId: string, projectId: string): SpeakerRequest {
  requireIds(requestId, projectId)
  return Object.freeze({ version: 1, requestId, method: SPEAKERS_GET_METHOD, params: Object.freeze({ projectId }) })
}

export function createSpeakerConfirmRequest(requestId: string, projectId: string): SpeakerRequest {
  requireIds(requestId, projectId)
  return Object.freeze({ version: 1, requestId, method: SPEAKERS_CONFIRM_METHOD, params: Object.freeze({ projectId }) })
}

/** A null cue asks for the system's estimate (a draft); a cue changes (or, with a null speaker, clears) that cue's speaker. */
export function createSpeakerAssignRequest(
  requestId: string, projectId: string, cueIndex: number | null, speakerId: string | null,
): SpeakerRequest {
  requireIds(requestId, projectId)
  const cueOk = cueIndex === null || (Number.isInteger(cueIndex) && cueIndex >= 0 && cueIndex <= SPEAKER_MAX_CUE_INDEX)
  const speakerOk = speakerId === null || speakerIdPattern.test(speakerId)
  if (!cueOk || !speakerOk || (cueIndex === null && speakerId !== null)) throw new TypeError('Invalid speaker request.')
  return Object.freeze({ version: 1, requestId, method: SPEAKERS_ASSIGN_METHOD, params: Object.freeze({ projectId, cueIndex, speakerId }) })
}

function parseResult(value: unknown): SpeakerResult | null {
  if (!exactKeys(value, ['status', 'state', 'label', 'estimated', 'assignments'])) return null
  const { status, state, label, estimated, assignments } = value
  if (!oneOf(SPEAKER_STATUSES, status) || !oneOf(['none', 'draft', 'confirmed'] as const, state)) return null
  if (typeof label !== 'string' || label.length > 40 || typeof estimated !== 'boolean') return null
  if (!Array.isArray(assignments) || assignments.length > SPEAKER_MAX_ASSIGNMENTS) return null
  const entries: SpeakerAssignmentEntry[] = []
  for (const item of assignments) {
    if (!exactKeys(item, ['cueIndex', 'speakerId'])) return null
    const { cueIndex, speakerId } = item
    if (typeof cueIndex !== 'number' || !Number.isInteger(cueIndex) || cueIndex < 0 || cueIndex > SPEAKER_MAX_CUE_INDEX) return null
    if (typeof speakerId !== 'string' || !speakerIdPattern.test(speakerId)) return null
    entries.push(Object.freeze({ cueIndex, speakerId }))
  }
  return Object.freeze({ status, state, label, estimated, assignments: Object.freeze(entries) })
}

export function parseSpeakerResponse(value: unknown): SpeakerResponse | null {
  if (!exactKeys(value, ['version', 'requestId', 'method', 'result']) || value.version !== 1 || !isId(value.requestId)) return null
  if (!oneOf([SPEAKERS_GET_METHOD, SPEAKERS_ASSIGN_METHOD, SPEAKERS_CONFIRM_METHOD] as const, value.method)) return null
  const result = parseResult(value.result)
  return result === null ? null : Object.freeze({ version: 1, requestId: value.requestId, method: value.method, result })
}

export interface SpeakerTransport {
  postMessage(message: SpeakerRequest): void
  addEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
  removeEventListener(type: 'message', listener: (event: { readonly data: unknown }) => void): void
}

export interface SpeakerService {
  /** The speaker assignment of the Project (none / 下書き / 確定済み). */
  get(projectId: string): Promise<SpeakerOutcome>
  /** 話者を割り当てる: the system's estimate is saved as a draft. */
  estimate(projectId: string): Promise<SpeakerOutcome>
  /** Changes (or clears with null) the speaker of one displayed cue; the result is a draft. */
  assign(projectId: string, cueIndex: number, speakerId: string | null): Promise<SpeakerOutcome>
  /** 確認して確定: the user confirms the draft. Only a confirmed assignment reaches the output. */
  confirm(projectId: string): Promise<SpeakerOutcome>
  dispose(): void
}

export interface SpeakerServiceOptions {
  readonly createRequestId?: () => string
  readonly timeoutMilliseconds?: number
}

export function createSpeakerService(transport: SpeakerTransport, options: SpeakerServiceOptions = {}): SpeakerService {
  const newId = options.createRequestId ?? (() => globalThis.crypto.randomUUID().toLowerCase())
  const timeout = options.timeoutMilliseconds ?? SPEAKER_TIMEOUT_MS
  const pending = new Map<string, { readonly method: SpeakerMethod; readonly settle: (response: SpeakerResponse | null) => void }>()
  let disposed = false

  const receive = (event: { readonly data: unknown }): void => {
    const response = parseSpeakerResponse(event.data)
    if (response === null) return
    const entry = pending.get(response.requestId)
    if (entry === undefined || entry.method !== response.method) return
    pending.delete(response.requestId)
    entry.settle(response)
  }
  transport.addEventListener('message', receive)

  const send = (request: SpeakerRequest): Promise<SpeakerOutcome> => {
    if (disposed) return Promise.resolve(Object.freeze({ status: 'disconnected' as const }))
    return new Promise<SpeakerResponse | 'timeout' | 'disconnected'>(resolve => {
      const timer = setTimeout(() => { pending.delete(request.requestId); resolve('timeout') }, timeout)
      pending.set(request.requestId, { method: request.method, settle: response => { clearTimeout(timer); resolve(response ?? 'disconnected') } })
      try { transport.postMessage(request) }
      catch { pending.delete(request.requestId); clearTimeout(timer); resolve('disconnected') }
    }).then(settled => typeof settled === 'string' ? Object.freeze({ status: settled }) : settled.result)
  }

  return Object.freeze({
    get: (projectId: string) => send(createSpeakerGetRequest(newId(), projectId)),
    estimate: (projectId: string) => send(createSpeakerAssignRequest(newId(), projectId, null, null)),
    assign: (projectId: string, cueIndex: number, speakerId: string | null) => send(createSpeakerAssignRequest(newId(), projectId, cueIndex, speakerId)),
    confirm: (projectId: string) => send(createSpeakerConfirmRequest(newId(), projectId)),
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
export function createHostSpeakerService(): SpeakerService | null {
  if (typeof window === 'undefined') return null
  const candidate = (window as Window & { readonly chrome?: { readonly webview?: unknown } }).chrome?.webview
  if (typeof candidate !== 'object' || candidate === null) return null
  const transport = candidate as Partial<SpeakerTransport>
  if (typeof transport.postMessage !== 'function' || typeof transport.addEventListener !== 'function' ||
      typeof transport.removeEventListener !== 'function') return null
  return createSpeakerService(transport as SpeakerTransport)
}
