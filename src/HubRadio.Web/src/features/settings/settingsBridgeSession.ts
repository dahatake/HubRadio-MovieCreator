import type { BridgeClient } from '../../bridge'
import { SETTINGS_METHODS, isSettingsRequestWithinLimit, type SettingsRequest, type SettingsResponse, type SettingsWorkspaceRequest, type SettingsWorkspaceResponse } from './settingsProtocol'
import { settingsBridgeUnavailable, settingsRequestRejected, settingsRequestStarted, settingsResponseReceived, type SettingsAction } from './settingsState'

export type SettingsBridgeClient = Pick<BridgeClient, 'sendSettings' | 'abandonSettingsRequest' | 'onSettingsResponse' | 'dispose'>
export type SettingsDispatch = (action: SettingsAction) => void
export interface SettingsBridgeSession { readonly send: (request: SettingsRequest) => boolean; readonly abandonPending: () => void; readonly dispose: () => void }

export type SettingsWorkspaceBridgeClient = Pick<BridgeClient, 'sendSettingsWorkspace' | 'abandonSettingsWorkspaceRequest' | 'onSettingsWorkspaceResponse' | 'dispose'>
/** Immutable correlation metadata, not the mutable request payload. Null projectId means global. */
export interface WorkspaceSessionRequest {
  readonly requestId: string
  readonly method: SettingsWorkspaceRequest['method']
  readonly projectId: string | null
}
export type WorkspaceSessionEvent =
  | Readonly<{ type: 'requested'; request: WorkspaceSessionRequest }>
  | Readonly<{ type: 'response'; request: WorkspaceSessionRequest; response: SettingsWorkspaceResponse }>
  | Readonly<{ type: 'failure'; request: WorkspaceSessionRequest | null; code: 'invalid' | 'unavailable' | 'timeout' }>
export type SettingsWorkspaceDispatch = (event: WorkspaceSessionEvent) => void
export interface SettingsWorkspaceBridgeSession {
  /** One pending request across all scopes. Use a fresh requestId for every attempt. */
  readonly send: (request: SettingsWorkspaceRequest) => boolean
  /** Call before changing scope/project. Abandoning correlation does not undo a native mutation. */
  readonly abandonPending: () => void
  readonly dispose: () => void
}
const settingsRequestTimeoutMilliseconds = 5_000

/** Project-only adapter retained until the provider/state migrate in UX-014. */
export function startSettingsBridgeSession(createClient: () => SettingsBridgeClient, dispatch: SettingsDispatch): SettingsBridgeSession {
  return startSettingsWorkspaceBridgeSession(() => {
    const client = createClient()
    return {
      sendSettingsWorkspace(request): void {
        if (!isProjectRequest(request)) throw new Error('The legacy settings session only supports project requests.')
        client.sendSettings(request)
      },
      abandonSettingsWorkspaceRequest: requestId => client.abandonSettingsRequest(requestId),
      onSettingsWorkspaceResponse: listener => client.onSettingsResponse(listener),
      dispose: () => client.dispose(),
    }
  }, event => {
    switch (event.type) {
      case 'requested': {
        const method = SETTINGS_METHODS.find(candidate => candidate === event.request.method)
        if (method !== undefined) dispatch(settingsRequestStarted(method))
        break
      }
      case 'response':
        if (isProjectResponse(event.response)) dispatch(settingsResponseReceived(event.response))
        break
      case 'failure':
        dispatch(event.code === 'invalid' ? settingsRequestRejected() : settingsBridgeUnavailable())
        break
    }
  })
}

/**
 * The client owns wire validation and OCC/profile/file-hash matching (UX-049).
 * The session owns lifetime/correlation; native failure statuses remain response events.
 * Local failures contain no exception text. Only setup failures have request: null.
 * Neither failures nor timeouts automatically resend requests, especially mutations.
 */
export function startSettingsWorkspaceBridgeSession(createClient: () => SettingsWorkspaceBridgeClient, dispatch: SettingsWorkspaceDispatch): SettingsWorkspaceBridgeSession {
  let client: SettingsWorkspaceBridgeClient | null = null
  let pending: WorkspaceSessionRequest | null = null
  let timeout: ReturnType<typeof setTimeout> | null = null
  let disposed = false
  let unsubscribe: (() => void) | null = null
  const clearPending = (): WorkspaceSessionRequest | null => {
    const current = pending
    pending = null
    if (timeout !== null) clearTimeout(timeout)
    timeout = null
    return current
  }
  const abandon = (request: WorkspaceSessionRequest): void => {
    try { client?.abandonSettingsWorkspaceRequest(request.requestId) } catch { /* local abandonment remains authoritative */ }
  }
  const failPending = (request: WorkspaceSessionRequest, code: 'unavailable' | 'timeout'): void => {
    // A synchronous callback can complete this request and start another one.
    if (disposed || pending !== request) return
    clearPending()
    abandon(request)
    if (!disposed) dispatch({ type: 'failure', request, code })
  }
  try {
    client = createClient()
    unsubscribe = client.onSettingsWorkspaceResponse(response => {
      const current = pending
      if (disposed || client === null || current === null || !matches(response, current)) return
      clearPending()
      dispatch({ type: 'response', request: current, response })
    })
  } catch {
    const failedClient = client
    client = null
    try { failedClient?.dispose() } catch { /* initialization failure must remain private-safe */ }
    dispatch({ type: 'failure', request: null, code: 'unavailable' })
  }
  return Object.freeze({
    send(request: SettingsWorkspaceRequest): boolean {
      if (disposed || pending !== null) return false
      const current: WorkspaceSessionRequest = Object.freeze({
        requestId: request.requestId,
        method: request.method,
        projectId: 'projectId' in request.params ? request.params.projectId : null,
      })
      if (!isSettingsRequestWithinLimit(request)) { dispatch({ type: 'failure', request: current, code: 'invalid' }); return false }
      pending = current
      try { dispatch({ type: 'requested', request: current }) } catch (error: unknown) {
        if (pending === current) clearPending()
        throw error
      }
      // A requested callback may have changed scope or disposed the session.
      if (disposed || pending !== current) return false
      if (client === null) {
        clearPending()
        dispatch({ type: 'failure', request: current, code: 'unavailable' })
        return false
      }
      try {
        client.sendSettingsWorkspace(request)
      } catch {
        failPending(current, 'unavailable')
        return false
      }
      if (!disposed && pending === current) {
        timeout = setTimeout(() => failPending(current, 'timeout'), settingsRequestTimeoutMilliseconds)
      }
      return true
    },
    abandonPending(): void {
      if (disposed) return
      const current = clearPending()
      if (current !== null) abandon(current)
    },
    dispose(): void {
      if (disposed) return
      disposed = true
      const current = clearPending()
      if (current !== null) abandon(current)
      try { unsubscribe?.() } catch { /* continue closing the client */ }
      try { client?.dispose() } catch { /* React cleanup must not expose transport exceptions */ }
    },
  })
}

function matches(response: SettingsWorkspaceResponse, request: WorkspaceSessionRequest): boolean {
  return response.requestId === request.requestId && response.method === request.method && (response.result.status !== 'available' || response.result.snapshot.projectId === request.projectId)
}
function isProjectRequest(request: SettingsWorkspaceRequest): request is SettingsRequest {
  return SETTINGS_METHODS.some(method => method === request.method)
}
function isProjectResponse(response: SettingsWorkspaceResponse): response is SettingsResponse {
  return SETTINGS_METHODS.some(method => method === response.method)
}
