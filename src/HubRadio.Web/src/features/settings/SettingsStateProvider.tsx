import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState, type ReactNode } from 'react'
import { createHostBridgeClient } from '../../bridge'
import { createSettingsReadRequest, createSettingsResetAllRequest, createSettingsResetRequest, createSettingsUpdateRequest, createSettingsGlobalReadRequest, createSettingsGlobalUpdateRequest, createSettingsGlobalResetRequest, createSettingsGlobalResetAllRequest, createSettingsFileExportRequest, createSettingsFilePreviewRequest, createSettingsFileApplyRequest, type AnySettingsSnapshot, type SettingValue, type SettingsRequest, type SettingsWorkspaceRequest } from './settingsProtocol'
import { startSettingsBridgeSession, startSettingsWorkspaceBridgeSession, type SettingsBridgeClient, type SettingsBridgeSession, type SettingsWorkspaceBridgeClient, type SettingsWorkspaceBridgeSession } from './settingsBridgeSession'
import { initialSettingsState, settingsAdvancedChanged, settingsBridgeUnavailable, settingsErrorCleared, settingsProjectChanged, settingsRequestRejected, settingsStateReducer, initialWorkspaceSettingsState, workspaceSettingsReducer, type SettingsState, type WorkspaceSettingsAction, type WorkspaceSettingsState } from './settingsState'

export interface SettingsContextValue {
  readonly state: SettingsState
  readonly changeProjectId: (value: string) => void
  readonly load: () => void
  readonly refresh: () => void
  readonly update: (key: string, value: SettingValue) => void
  readonly reset: (key: string) => void
  readonly resetAll: () => void
  readonly setAdvancedVisible: (value: boolean) => void
  readonly clearError: () => void
}
export interface SettingsStateProviderProps { readonly children: ReactNode; readonly createClient?: () => SettingsBridgeClient }
const Context = createContext<SettingsContextValue | undefined>(undefined)

export function SettingsStateProvider({ children, createClient = createHostBridgeClient }: SettingsStateProviderProps) {
  const [state, dispatch] = useReducer(settingsStateReducer, initialSettingsState)
  const sessionRef = useRef<SettingsBridgeSession | null>(null)
  useEffect(() => { const session = startSettingsBridgeSession(createClient, dispatch); sessionRef.current = session; return () => { if (sessionRef.current === session) sessionRef.current = null; session.dispose() } }, [createClient])
  const send = useCallback((factory: () => SettingsRequest) => {
    let request: SettingsRequest
    try { request = factory() } catch { dispatch(settingsRequestRejected()); return }
    sessionRef.current?.send(request) ?? dispatch(settingsBridgeUnavailable())
  }, [])
  const load = useCallback(() => send(() => createSettingsReadRequest(id(), state.projectIdInput.trim())), [send, state.projectIdInput])
  const refresh = useCallback(() => send(() => createSettingsReadRequest(id(), state.snapshot?.projectId ?? state.projectIdInput.trim())), [send, state.projectIdInput, state.snapshot?.projectId])
  const update = useCallback((key: string, value: SettingValue) => { if (state.snapshot !== null && !state.needsRefresh) send(() => createSettingsUpdateRequest(id(), state.snapshot!, key, value)) }, [send, state.needsRefresh, state.snapshot])
  const reset = useCallback((key: string) => { if (state.snapshot !== null && !state.needsRefresh) send(() => createSettingsResetRequest(id(), state.snapshot!, key)) }, [send, state.needsRefresh, state.snapshot])
  const resetAll = useCallback(() => { if (state.snapshot !== null && !state.needsRefresh) send(() => createSettingsResetAllRequest(id(), state.snapshot!)) }, [send, state.needsRefresh, state.snapshot])
  const changeProjectId = useCallback((projectId: string) => { sessionRef.current?.abandonPending(); dispatch(settingsProjectChanged(projectId)) }, [])
  const value = useMemo<SettingsContextValue>(() => ({ state, changeProjectId, load, refresh, update, reset, resetAll, setAdvancedVisible: value => dispatch(settingsAdvancedChanged(value)), clearError: () => dispatch(settingsErrorCleared()) }), [changeProjectId, load, refresh, reset, resetAll, state, update])
  return <Context.Provider value={value}>{children}</Context.Provider>
}
export function useSettingsState(): SettingsContextValue { const value = useContext(Context); if (value === undefined) throw new Error('useSettingsState must be used within SettingsStateProvider.'); return value }

export interface SettingsWorkspaceContextValue {
  readonly state: WorkspaceSettingsState
  readonly isDirty: boolean
  /** False leaves a dirty scope untouched. Confirm in UI, discardDrafts(), then retry. */
  readonly setScope: (projectId: string | null) => boolean
  readonly changeDraft: (key: string, value: SettingValue) => void
  /** Boolean results mean request accepted, not DB/file success; observe state. */
  readonly load: () => boolean
  readonly refresh: () => boolean
  readonly save: (key: string) => boolean
  readonly reset: (key: string) => boolean
  /** UI must confirm destructive operations before calling resetAll/applyFile. */
  readonly resetAll: () => boolean
  readonly discardDrafts: () => void
  /** Export always uses saved values. UI warns about drafts; null never overwrites. */
  readonly exportFile: (expectedFileSha256: string | null) => boolean
  readonly previewFile: () => boolean
  readonly applyFile: () => boolean
  readonly clearError: () => void
  readonly clearFile: () => void
}
export interface SettingsWorkspaceStateProviderProps { readonly children: ReactNode; readonly createClient?: () => SettingsWorkspaceBridgeClient }
const WorkspaceContext = createContext<SettingsWorkspaceContextValue | undefined>(undefined)

/**
 * UX-025/039 integration replaces the legacy provider with this one; do not nest
 * both to serve the same workspace. This provider owns exactly one session/state.
 * Keep it mounted above settings groups/pages/tabs. Reading is explicit via load.
 */
export function SettingsWorkspaceStateProvider({ children, createClient = createHostBridgeClient }: SettingsWorkspaceStateProviderProps) {
  const [state, setState] = useState(initialWorkspaceSettingsState)
  // The ref points to the same reducer result as React, not a second DB snapshot.
  // Sequential actions in one event (discard -> scope -> load) see current state.
  const stateRef = useRef(state)
  const sessionRef = useRef<SettingsWorkspaceBridgeSession | null>(null)
  const getCurrentState = useCallback((): WorkspaceSettingsState => stateRef.current, [])
  const dispatch = useCallback((action: WorkspaceSettingsAction) => {
    const next = workspaceSettingsReducer(stateRef.current, action)
    stateRef.current = next
    setState(next)
  }, [])
  useLayoutEffect(() => {
    // Replacing a client abandons correlation, not a native write already sent.
    const pending = stateRef.current.pendingRequest
    if (pending !== null) dispatch({ type: 'sessionEvent', event: { type: 'failure', request: pending, code: 'unavailable' } })
    const session = startSettingsWorkspaceBridgeSession(createClient, event => dispatch({ type: 'sessionEvent', event }))
    sessionRef.current = session
    return () => { if (sessionRef.current === session) sessionRef.current = null; session.dispose() }
  }, [createClient, dispatch])
  const send = useCallback((factory: (current: WorkspaceSettingsState) => SettingsWorkspaceRequest | null): boolean => {
    const current = getCurrentState()
    if (current.pendingRequest !== null) return false
    let request: SettingsWorkspaceRequest | null
    try { request = factory(current) } catch { dispatch({ type: 'requestRejected' }); return false }
    if (request === null) return false
    dispatch({ type: 'requestStarted', request })
    const pending = getCurrentState().pendingRequest
    if (pending === null || pending.requestId !== request.requestId) return false
    const accepted = sessionRef.current?.send(request) ?? false
    if (!accepted && getCurrentState().pendingRequest?.requestId === request.requestId) {
      dispatch({ type: 'sessionEvent', event: { type: 'failure', request: pending, code: 'unavailable' } })
    }
    return accepted
  }, [dispatch, getCurrentState])
  const withSnapshot = useCallback((factory: (snapshot: AnySettingsSnapshot, current: WorkspaceSettingsState) => SettingsWorkspaceRequest | null) => send(current =>
    current.snapshot === null || current.needsRefresh ? null : factory(current.snapshot, current)), [send])
  const setScope = useCallback((projectId: string | null): boolean => {
    const action: WorkspaceSettingsAction = { type: 'scopeChanged', projectId }
    const current = stateRef.current
    const next = workspaceSettingsReducer(current, action)
    if (next !== current && next.projectId === projectId) sessionRef.current?.abandonPending()
    dispatch(action)
    return stateRef.current.projectId === projectId
  }, [dispatch])
  const changeDraft = useCallback((key: string, value: SettingValue) => dispatch({ type: 'draftChanged', key, value }), [dispatch])
  const load = useCallback(() => send(current => current.projectId === null ? createSettingsGlobalReadRequest(id()) : createSettingsReadRequest(id(), current.projectId)), [send])
  const save = useCallback((key: string) => withSnapshot((snapshot, current) => {
    const draft = Object.hasOwn(current.draftValues, key) ? current.draftValues[key] : undefined
    if (draft === undefined) return null
    return snapshot.projectId === null ? createSettingsGlobalUpdateRequest(id(), snapshot, key, draft) : createSettingsUpdateRequest(id(), snapshot, key, draft)
  }), [withSnapshot])
  const reset = useCallback((key: string) => withSnapshot(snapshot => snapshot.projectId === null ? createSettingsGlobalResetRequest(id(), snapshot, key) : createSettingsResetRequest(id(), snapshot, key)), [withSnapshot])
  const resetAll = useCallback(() => withSnapshot(snapshot => snapshot.projectId === null ? createSettingsGlobalResetAllRequest(id(), snapshot) : createSettingsResetAllRequest(id(), snapshot)), [withSnapshot])
  const discardDrafts = useCallback(() => dispatch({ type: 'draftsDiscarded' }), [dispatch])
  const exportFile = useCallback((expectedFileSha256: string | null) => withSnapshot(snapshot => createSettingsFileExportRequest(id(), snapshot, expectedFileSha256)), [withSnapshot])
  const previewFile = useCallback(() => withSnapshot(snapshot => createSettingsFilePreviewRequest(id(), snapshot)), [withSnapshot])
  const applyFile = useCallback(() => withSnapshot((snapshot, current) => current.importPreview === null || current.fileSha256 === null
    ? null : createSettingsFileApplyRequest(id(), snapshot, current.fileSha256)), [withSnapshot])
  const clearError = useCallback(() => dispatch({ type: 'errorCleared' }), [dispatch])
  const clearFile = useCallback(() => {
    if (stateRef.current.pendingRequest?.method.startsWith('settings.file.')) sessionRef.current?.abandonPending()
    dispatch({ type: 'fileCleared' })
  }, [dispatch])
  const value = useMemo<SettingsWorkspaceContextValue>(() => ({
    state, isDirty: Object.keys(state.draftValues).length > 0, setScope, changeDraft,
    load, refresh: load, save, reset, resetAll, discardDrafts, exportFile, previewFile, applyFile, clearError, clearFile,
  }), [state, setScope, changeDraft, load, save, reset, resetAll, discardDrafts, exportFile, previewFile, applyFile, clearError, clearFile])
  return <WorkspaceContext.Provider value={value}>{children}</WorkspaceContext.Provider>
}
export function useSettingsWorkspace(): SettingsWorkspaceContextValue {
  const value = useContext(WorkspaceContext)
  if (value === undefined) throw new Error('useSettingsWorkspace must be used within SettingsWorkspaceStateProvider.')
  return value
}
function id(): string { return window.crypto.randomUUID().toLowerCase() }
