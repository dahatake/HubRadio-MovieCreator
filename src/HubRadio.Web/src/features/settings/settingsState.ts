import { createSettingsReadRequest, type AnySettingsSnapshot, type SettingValue, type SettingsFileStatus, type SettingsMethod, type SettingsResponse, type SettingsSnapshot, type SettingsWorkspaceMethod, type SettingsWorkspaceRequest } from './settingsProtocol'
import type { WorkspaceSessionEvent, WorkspaceSessionRequest } from './settingsBridgeSession'

export type SettingsError = 'forbidden' | 'notFound' | 'unbound' | 'conflict' | 'invalid' | 'unavailable'
export interface SettingsState {
  readonly projectIdInput: string
  readonly snapshot: SettingsSnapshot | null
  readonly pendingMethod: SettingsMethod | null
  readonly error: SettingsError | null
  readonly needsRefresh: boolean
  readonly advancedVisible: boolean
  readonly lastCompletedMethod: SettingsMethod | null
}
export type SettingsAction =
  | Readonly<{ type: 'projectChanged'; value: string }>
  | Readonly<{ type: 'requestStarted'; method: SettingsMethod }>
  | Readonly<{ type: 'requestRejected' }>
  | Readonly<{ type: 'responseReceived'; response: SettingsResponse }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'advancedChanged'; value: boolean }>
  | Readonly<{ type: 'errorCleared' }>

export const initialSettingsState: SettingsState = Object.freeze({
  projectIdInput: '', snapshot: null, pendingMethod: null, error: null,
  needsRefresh: false, advancedVisible: false, lastCompletedMethod: null,
})

export function settingsStateReducer(state: SettingsState, action: SettingsAction): SettingsState {
  switch (action.type) {
    case 'projectChanged':
      return { ...state, projectIdInput: action.value, snapshot: null, pendingMethod: null, error: null, needsRefresh: false, lastCompletedMethod: null }
    case 'requestStarted':
      return { ...state, pendingMethod: action.method, error: null, lastCompletedMethod: null }
    case 'requestRejected':
      return { ...state, pendingMethod: null, error: 'invalid', lastCompletedMethod: null }
    case 'responseReceived': {
      const { result } = action.response
      if (result.status === 'available') {
        return { ...state, projectIdInput: result.snapshot.projectId, snapshot: result.snapshot, pendingMethod: null, error: null, needsRefresh: false, lastCompletedMethod: action.response.method }
      }
      return { ...state, pendingMethod: null, error: result.status, needsRefresh: result.status === 'conflict' || state.needsRefresh, lastCompletedMethod: null }
    }
    case 'bridgeUnavailable':
      return { ...state, pendingMethod: null, error: 'unavailable', lastCompletedMethod: null }
    case 'advancedChanged':
      return { ...state, advancedVisible: action.value }
    case 'errorCleared':
      return { ...state, error: null }
  }
}

export const settingsProjectChanged = (value: string): SettingsAction => ({ type: 'projectChanged', value })
export const settingsRequestStarted = (method: SettingsMethod): SettingsAction => ({ type: 'requestStarted', method })
export const settingsRequestRejected = (): SettingsAction => ({ type: 'requestRejected' })
export const settingsResponseReceived = (response: SettingsResponse): SettingsAction => ({ type: 'responseReceived', response })
export const settingsBridgeUnavailable = (): SettingsAction => ({ type: 'bridgeUnavailable' })
export const settingsAdvancedChanged = (value: boolean): SettingsAction => ({ type: 'advancedChanged', value })
export const settingsErrorCleared = (): SettingsAction => ({ type: 'errorCleared' })

// The project-only reducer above remains compatible with the pre-UX-025 panel.
export type WorkspaceSettingsDraftValues = Readonly<Partial<Record<string, SettingValue>>>
export type WorkspaceSettingsError = Exclude<SettingsFileStatus, 'available'> | 'timeout'
export interface WorkspaceSettingsPendingRequest extends WorkspaceSessionRequest {
  readonly key: string | null
  /** Sent update values, or immutable draft references captured at send for reset/import. */
  readonly draftValues: WorkspaceSettingsDraftValues
}
export interface WorkspaceSettingsState {
  readonly scope: 'global' | 'project'
  readonly projectId: string | null
  /** Saved DB state only; never an import candidate or a merge with drafts. */
  readonly snapshot: AnySettingsSnapshot | null
  readonly draftValues: WorkspaceSettingsDraftValues
  readonly pendingRequest: WorkspaceSettingsPendingRequest | null
  readonly error: WorkspaceSettingsError | null
  readonly needsRefresh: boolean
  readonly lastCompletedMethod: SettingsWorkspaceMethod | null
  readonly importPreview: AnySettingsSnapshot | null
  readonly fileSha256: string | null
  readonly exportNeedsConfirmation: boolean
}
export type WorkspaceSettingsAction =
  | Readonly<{ type: 'scopeChanged'; projectId: string | null }>
  | Readonly<{ type: 'draftChanged'; key: string; value: SettingValue }>
  | Readonly<{ type: 'draftsDiscarded' }>
  | Readonly<{ type: 'requestStarted'; request: SettingsWorkspaceRequest }>
  | Readonly<{ type: 'sessionEvent'; event: WorkspaceSessionEvent }>
  | Readonly<{ type: 'requestRejected' }>
  | Readonly<{ type: 'errorCleared' }>
  | Readonly<{ type: 'fileCleared' }>

const noDrafts: WorkspaceSettingsDraftValues = Object.freeze({})
const noFile = Object.freeze({ importPreview: null, fileSha256: null, exportNeedsConfirmation: false })
export const initialWorkspaceSettingsState: WorkspaceSettingsState = Object.freeze({
  scope: 'global', projectId: null, snapshot: null, draftValues: noDrafts,
  pendingRequest: null, error: null, needsRefresh: false, lastCompletedMethod: null, ...noFile,
})

export function workspaceSettingsReducer(state: WorkspaceSettingsState, action: WorkspaceSettingsAction): WorkspaceSettingsState {
  switch (action.type) {
    case 'scopeChanged':
      if (action.projectId === state.projectId || Object.keys(state.draftValues).length > 0) return state
      if (action.projectId !== null) {
        // Reuse the protocol's canonical UUID check, including its zero-ID rejection.
        try { createSettingsReadRequest(action.projectId, action.projectId) } catch { return { ...state, error: 'invalid', lastCompletedMethod: null } }
      }
      return { ...initialWorkspaceSettingsState, scope: action.projectId === null ? 'global' : 'project', projectId: action.projectId }
    case 'draftChanged': {
      const saved = state.snapshot?.values.find(item => item.key === action.key)
      if (saved === undefined) return state
      const draftValues = { ...state.draftValues }
      // Editing back to the old saved value during a write/refresh is still an edit:
      // its response might replace that saved value before the next render.
      if (sameSettingValue(saved.value, action.value) && !requestMayChangeKey(state.pendingRequest, action.key)) delete draftValues[action.key]
      else draftValues[action.key] = copySettingValue(action.value)
      return { ...state, draftValues, lastCompletedMethod: null }
    }
    case 'draftsDiscarded':
      return { ...state, draftValues: noDrafts }
    case 'requestStarted': {
      const request = action.request
      const projectId = 'projectId' in request.params ? request.params.projectId : null
      if (state.pendingRequest !== null || projectId !== state.projectId) return state
      const pendingRequest: WorkspaceSettingsPendingRequest = Object.freeze({
        requestId: request.requestId, method: request.method, projectId,
        key: 'key' in request.params ? request.params.key : null,
        draftValues: captureRequestDrafts(state, request),
      })
      return {
        ...state, pendingRequest, error: null, lastCompletedMethod: null,
        ...(isFileMethod(request.method) && request.method !== 'settings.file.apply' ? noFile : {}),
      }
    }
    case 'sessionEvent':
      return receiveWorkspaceEvent(state, action.event)
    case 'requestRejected':
      return state.pendingRequest !== null ? state : { ...state, error: 'invalid', lastCompletedMethod: null }
    case 'errorCleared':
      return { ...state, error: null }
    case 'fileCleared': {
      const pendingFile = state.pendingRequest !== null && isFileMethod(state.pendingRequest.method)
      return {
        ...state, ...noFile, pendingRequest: pendingFile ? null : state.pendingRequest,
        needsRefresh: state.needsRefresh || state.pendingRequest?.method === 'settings.file.apply',
        lastCompletedMethod: state.lastCompletedMethod !== null && isFileMethod(state.lastCompletedMethod) ? null : state.lastCompletedMethod,
      }
    }
  }
}

function receiveWorkspaceEvent(state: WorkspaceSettingsState, event: WorkspaceSessionEvent): WorkspaceSettingsState {
  // Bind the full request before session.send, which can respond synchronously.
  // A queued 'requested' event must not revive an abandoned/completed request.
  if (event.type === 'requested') return state
  const pending = state.pendingRequest
  if (event.type === 'failure' && event.request === null) {
    return pending !== null ? state : { ...state, error: event.code, lastCompletedMethod: null }
  }
  if (pending === null || event.request === null || !sameWorkspaceRequest(pending, event.request) || pending.projectId !== state.projectId) return state
  if (event.type === 'failure') {
    const uncertainWrite = event.code !== 'invalid' && isDatabaseMutation(pending.method)
    return {
      ...state, pendingRequest: null, error: event.code, lastCompletedMethod: null,
      needsRefresh: state.needsRefresh || uncertainWrite,
      ...(isFileMethod(pending.method) || uncertainWrite ? noFile : {}),
    }
  }
  const response = event.response
  const result = response.result
  if (response.requestId !== pending.requestId || response.method !== pending.method || (result.status === 'available' && result.snapshot.projectId !== state.projectId)) return state
  if (result.status !== 'available') {
    const overwrite = response.method === 'settings.file.export' && result.status === 'overwriteRequired'
    return {
      ...state, pendingRequest: null, error: result.status, lastCompletedMethod: null,
      needsRefresh: state.needsRefresh || result.status === 'conflict',
      ...(isFileMethod(response.method) || result.status === 'conflict' ? {
        ...noFile, exportNeedsConfirmation: overwrite,
        fileSha256: overwrite && result.status === 'overwriteRequired' ? result.fileSha256 : null,
      } : {}),
    }
  }
  const completed = { ...state, pendingRequest: null, error: null, lastCompletedMethod: response.method }
  if (response.method === 'settings.file.preview' && 'fileSha256' in result) {
    return { ...completed, importPreview: result.snapshot, fileSha256: result.fileSha256, exportNeedsConfirmation: false }
  }
  if (response.method === 'settings.file.export' && 'fileSha256' in result) {
    return { ...completed, ...noFile, fileSha256: result.fileSha256 }
  }
  return {
    ...completed, ...noFile, snapshot: result.snapshot, needsRefresh: false,
    draftValues: clearAcknowledgedDrafts(state.draftValues, pending, result.snapshot),
    fileSha256: response.method === 'settings.file.apply' && 'fileSha256' in result ? result.fileSha256 : null,
  }
}

function captureRequestDrafts(state: WorkspaceSettingsState, request: SettingsWorkspaceRequest): WorkspaceSettingsDraftValues {
  switch (request.method) {
    case 'settings.update':
    case 'settings.global.update':
      return Object.freeze({ [request.params.key]: copySettingValue(request.params.value) })
    case 'settings.reset':
    case 'settings.global.reset': {
      const value = ownDraft(state.draftValues, request.params.key)
      return value === undefined ? noDrafts : Object.freeze({ [request.params.key]: value })
    }
    case 'settings.resetAll':
    case 'settings.global.resetAll':
    case 'settings.file.apply':
      return Object.freeze({ ...state.draftValues })
    default:
      return noDrafts
  }
}
function clearAcknowledgedDrafts(current: WorkspaceSettingsDraftValues, pending: WorkspaceSettingsPendingRequest, savedSnapshot: AnySettingsSnapshot): WorkspaceSettingsDraftValues {
  const next = { ...current }
  let changed = false
  const isSave = pending.method === 'settings.update' || pending.method === 'settings.global.update'
  for (const [key, value] of Object.entries(pending.draftValues)) {
    const draft = ownDraft(current, key)
    if (draft === undefined || value === undefined) continue
    // Saves acknowledge the returned DB value. Reset/import may only discard the
    // same frozen draft captured at send, not a later equal-valued (ABA) edit.
    const acknowledged = isSave
      ? sameSettingValue(draft, savedSnapshot.values.find(item => item.key === key)?.value)
      : draft === value
    if (acknowledged) { delete next[key]; changed = true }
  }
  return changed ? next : current
}
function ownDraft(drafts: WorkspaceSettingsDraftValues, key: string): SettingValue | undefined {
  return Object.hasOwn(drafts, key) ? drafts[key] : undefined
}
function sameWorkspaceRequest(left: WorkspaceSessionRequest, right: WorkspaceSessionRequest): boolean {
  return left.requestId === right.requestId && left.method === right.method && left.projectId === right.projectId
}
function isFileMethod(method: SettingsWorkspaceMethod): boolean { return method.startsWith('settings.file.') }
function isDatabaseMutation(method: SettingsWorkspaceMethod): boolean {
  return method !== 'settings.read' && method !== 'settings.global.read' && method !== 'settings.file.export' && method !== 'settings.file.preview'
}
function requestMayChangeKey(request: WorkspaceSettingsPendingRequest | null, key: string): boolean {
  if (request === null || request.method === 'settings.file.export' || request.method === 'settings.file.preview') return false
  return request.key === null || request.key === key
}
function copySettingValue(value: SettingValue): SettingValue {
  return value.kind === 'textList' ? Object.freeze({ ...value, textListValue: Object.freeze([...value.textListValue]) }) : Object.freeze({ ...value })
}
function sameSettingValue(left: SettingValue | undefined, right: SettingValue | undefined): boolean {
  if (left === undefined || right === undefined) return left === right
  switch (left.kind) {
    case 'boolean': return right.kind === left.kind && left.booleanValue === right.booleanValue
    case 'integer': return right.kind === left.kind && left.integerValue === right.integerValue
    case 'rational': return right.kind === left.kind && left.numerator === right.numerator && left.denominator === right.denominator
    case 'text': return right.kind === left.kind && left.textValue === right.textValue
    case 'textList': return right.kind === left.kind && left.textListValue.length === right.textListValue.length && left.textListValue.every((value, index) => value === right.textListValue[index])
  }
}
