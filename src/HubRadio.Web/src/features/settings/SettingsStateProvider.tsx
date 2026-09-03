import { createContext, useCallback, useContext, useEffect, useMemo, useReducer, useRef, type ReactNode } from 'react'
import { createHostBridgeClient } from '../../bridge'
import { createSettingsReadRequest, createSettingsResetAllRequest, createSettingsResetRequest, createSettingsUpdateRequest, type SettingValue, type SettingsRequest } from './settingsProtocol'
import { startSettingsBridgeSession, type SettingsBridgeClient, type SettingsBridgeSession } from './settingsBridgeSession'
import { initialSettingsState, settingsAdvancedChanged, settingsBridgeUnavailable, settingsErrorCleared, settingsProjectChanged, settingsRequestRejected, settingsStateReducer, type SettingsState } from './settingsState'

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
function id(): string { return window.crypto.randomUUID().toLowerCase() }
