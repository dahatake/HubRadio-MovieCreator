import type { SettingsMethod, SettingsResponse, SettingsSnapshot } from './settingsProtocol'

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
