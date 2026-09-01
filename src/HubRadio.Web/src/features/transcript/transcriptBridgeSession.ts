import type { BridgeClient } from '../../bridge'
import type { TranscriptRequest } from './transcriptProtocol'
import type { TranscriptAction } from './transcriptState'

export type TranscriptBridgeClient = Pick<
  BridgeClient,
  'sendTranscript' | 'onTranscriptResponse' | 'dispose'
>

export interface TranscriptBridgeSession {
  readonly send: (request: TranscriptRequest) => void
  readonly dispose: () => void
}

export type TranscriptDispatch = (action: TranscriptAction) => void

export function startTranscriptBridgeSession(
  createClient: () => TranscriptBridgeClient,
  dispatch: TranscriptDispatch,
): TranscriptBridgeSession {
  let client: TranscriptBridgeClient
  try {
    client = createClient()
  } catch {
    dispatch({ type: 'bridgeUnavailable' })
    return unavailableSession(dispatch)
  }

  let pendingRequest: Readonly<{
    requestId: string
    method: TranscriptRequest['method']
  }> | null = null
  let unsubscribe: () => void
  try {
    unsubscribe = client.onTranscriptResponse((response) => {
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
    send(request: TranscriptRequest): void {
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
        client.sendTranscript(request)
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
  dispatch: TranscriptDispatch,
): TranscriptBridgeSession {
  let disposed = false
  return Object.freeze({
    send(request: TranscriptRequest): void {
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
