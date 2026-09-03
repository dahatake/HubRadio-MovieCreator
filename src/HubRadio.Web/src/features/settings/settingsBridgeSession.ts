import type { BridgeClient } from '../../bridge'
import { isSettingsRequestWithinLimit, type SettingsRequest, type SettingsResponse } from './settingsProtocol'
import { settingsBridgeUnavailable, settingsRequestRejected, settingsRequestStarted, settingsResponseReceived, type SettingsAction } from './settingsState'

export type SettingsBridgeClient = Pick<BridgeClient, 'sendSettings' | 'abandonSettingsRequest' | 'onSettingsResponse' | 'dispose'>
export type SettingsDispatch = (action: SettingsAction) => void
export interface SettingsBridgeSession { readonly send: (request: SettingsRequest) => boolean; readonly abandonPending: () => void; readonly dispose: () => void }
const settingsRequestTimeoutMilliseconds = 5_000

export function startSettingsBridgeSession(createClient: () => SettingsBridgeClient, dispatch: SettingsDispatch): SettingsBridgeSession {
  let client: SettingsBridgeClient
  try { client = createClient() } catch { dispatch(settingsBridgeUnavailable()); return unavailable(dispatch) }
  let pending: SettingsRequest | null = null
  let timeout: ReturnType<typeof setTimeout> | null = null
  let disposed = false
  let unsubscribe: () => void
  const clearTimeoutIfPending = (): void => {
    if (timeout === null) return
    clearTimeout(timeout)
    timeout = null
  }
  try {
    unsubscribe = client.onSettingsResponse((response) => {
      if (!matches(response, pending)) return
      clearTimeoutIfPending()
      pending = null
      dispatch(settingsResponseReceived(response))
    })
  } catch { client.dispose(); dispatch(settingsBridgeUnavailable()); return unavailable(dispatch) }
  return Object.freeze({
    send(request: SettingsRequest): boolean {
      if (disposed) { dispatch(settingsBridgeUnavailable()); return false }
      if (pending !== null) return false
      if (!isSettingsRequestWithinLimit(request)) { dispatch(settingsRequestRejected()); return false }
      pending = request
      dispatch(settingsRequestStarted(request.method))
      try {
        client.sendSettings(request)
        if (pending?.requestId === request.requestId) {
          timeout = setTimeout(() => {
            if (pending?.requestId !== request.requestId) return
            pending = null
            timeout = null
            try { client.abandonSettingsRequest(request.requestId) } catch { /* timeout remains authoritative */ }
            dispatch(settingsBridgeUnavailable())
          }, settingsRequestTimeoutMilliseconds)
        }
        return true
      } catch {
        try { client.abandonSettingsRequest(request.requestId) } catch { /* private-safe state below */ }
        clearTimeoutIfPending(); pending = null; dispatch(settingsBridgeUnavailable()); return false
      }
    },
    abandonPending(): void {
      if (disposed || pending === null) return
      try { client.abandonSettingsRequest(pending.requestId) } catch { /* local state still abandons the request */ }
      clearTimeoutIfPending()
      pending = null
    },
    dispose(): void {
      if (disposed) return
      disposed = true
      if (pending !== null) { try { client.abandonSettingsRequest(pending.requestId) } catch { /* continue */ }; pending = null }
      clearTimeoutIfPending()
      try { unsubscribe() } finally { client.dispose() }
    },
  })
}
function unavailable(dispatch: SettingsDispatch): SettingsBridgeSession {
  let disposed = false
  return Object.freeze({ send(request: SettingsRequest) { if (disposed) return false; dispatch(settingsRequestStarted(request.method)); dispatch(settingsBridgeUnavailable()); return false }, abandonPending() { /* no transport */ }, dispose() { disposed = true } })
}
function matches(response: SettingsResponse, request: SettingsRequest | null): boolean {
  return request !== null && response.requestId === request.requestId && response.method === request.method && (response.result.status !== 'available' || response.result.snapshot.projectId === request.params.projectId)
}
