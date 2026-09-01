import type { BridgeClient } from '../../bridge'
import type { AnalysisRequest } from './analysisProtocol'
import type { AnalysisAction } from './analysisState'

export type AnalysisBridgeClient = Pick<
  BridgeClient,
  'sendAnalysis' | 'onAnalysisResponse' | 'dispose'
>

export interface AnalysisBridgeSession {
  readonly send: (request: AnalysisRequest) => void
  readonly dispose: () => void
}

export type AnalysisDispatch = (action: AnalysisAction) => void

export function startAnalysisBridgeSession(
  createClient: () => AnalysisBridgeClient,
  dispatch: AnalysisDispatch,
): AnalysisBridgeSession {
  let client: AnalysisBridgeClient
  try {
    client = createClient()
  } catch {
    dispatch({ type: 'bridgeUnavailable' })
    return unavailableSession(dispatch)
  }

  let pendingRequest: Readonly<{
    requestId: string
    method: AnalysisRequest['method']
  }> | null = null
  let unsubscribe: () => void
  try {
    unsubscribe = client.onAnalysisResponse((response) => {
      if (
        pendingRequest?.requestId === response.requestId &&
        pendingRequest.method === response.method
      ) {
        pendingRequest = null
      }
      dispatch({ type: 'responseReceived', response })
    })
  } catch {
    ignoreCleanupFailure(() => client.dispose())
    dispatch({ type: 'bridgeUnavailable' })
    return unavailableSession(dispatch)
  }

  let disposed = false
  return Object.freeze({
    send(request: AnalysisRequest): void {
      if (disposed) {
        dispatch({ type: 'bridgeUnavailable' })
        return
      }
      if (pendingRequest !== null) return
      pendingRequest = Object.freeze({
        requestId: request.requestId,
        method: request.method,
      })
      dispatch({
        type: 'requestStarted',
        requestId: request.requestId,
        method: request.method,
        projectId: request.params.projectId,
      })
      try {
        client.sendAnalysis(request)
      } catch {
        pendingRequest = null
        dispatch({ type: 'sendFailed', requestId: request.requestId })
      }
    },
    dispose(): void {
      if (disposed) return
      disposed = true
      pendingRequest = null
      ignoreCleanupFailure(unsubscribe)
      ignoreCleanupFailure(() => client.dispose())
    },
  })
}

function ignoreCleanupFailure(cleanup: () => void): void {
  try {
    cleanup()
  } catch {
    // Cleanup is best-effort and must not expose transport details to the UI.
  }
}

function unavailableSession(
  dispatch: AnalysisDispatch,
): AnalysisBridgeSession {
  let disposed = false
  return Object.freeze({
    send(request: AnalysisRequest): void {
      if (disposed) return
      dispatch({
        type: 'requestStarted',
        requestId: request.requestId,
        method: request.method,
        projectId: request.params.projectId,
      })
      dispatch({ type: 'bridgeUnavailable' })
    },
    dispose(): void {
      disposed = true
    },
  })
}