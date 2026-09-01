import type { BridgeClient } from '../../bridge'
import type {
  ComparisonReadRequest,
  ComparisonReadResponse,
} from './compareProtocol'
import {
  comparisonBridgeUnavailable,
  comparisonRequestStarted,
  comparisonResponseReceived,
  type ComparisonUiAction,
} from './compareState'

export type ComparisonBridgeClient = Pick<
  BridgeClient,
  | 'sendComparison'
  | 'abandonComparisonRequest'
  | 'onComparisonResponse'
  | 'dispose'
>

export type ComparisonDispatch = (action: ComparisonUiAction) => void

export interface ComparisonBridgeSession {
  readonly send: (request: ComparisonReadRequest) => boolean
  readonly dispose: () => void
}

export function startComparisonBridgeSession(
  createClient: () => ComparisonBridgeClient,
  dispatch: ComparisonDispatch,
): ComparisonBridgeSession {
  let client: ComparisonBridgeClient
  try {
    client = createClient()
  } catch {
    dispatch(comparisonBridgeUnavailable())
    return unavailableSession(dispatch)
  }

  let pending: ComparisonReadRequest | null = null
  let disposed = false
  let unsubscribe: () => void
  try {
    unsubscribe = client.onComparisonResponse((response) => {
      if (!matchesPending(response, pending)) {
        return
      }
      pending = null
      dispatch(comparisonResponseReceived(response))
    })
  } catch {
    disposeClientSafely(client)
    dispatch(comparisonBridgeUnavailable())
    return unavailableSession(dispatch)
  }

  return Object.freeze({
    send(request: ComparisonReadRequest): boolean {
      if (disposed) {
        dispatch(comparisonBridgeUnavailable())
        return false
      }
      if (pending !== null) {
        return false
      }

      pending = request
      dispatch(comparisonRequestStarted(request))
      try {
        client.sendComparison(request)
        return true
      } catch {
        try {
          client.abandonComparisonRequest(request.requestId)
        } catch {
          // The public state remains a fixed, private-safe unavailable error.
        }
        pending = null
        dispatch(comparisonBridgeUnavailable())
        return false
      }
    },
    dispose(): void {
      if (disposed) {
        return
      }
      disposed = true
      if (pending !== null) {
        try {
          client.abandonComparisonRequest(pending.requestId)
        } catch {
          // Cleanup continues even if the transport is already unavailable.
        }
        pending = null
      }
      try {
        unsubscribe()
      } catch {
        // Cleanup failures never cross the private-safe UI boundary.
      }
      disposeClientSafely(client)
    },
  })
}

function disposeClientSafely(client: ComparisonBridgeClient): void {
  try {
    client.dispose()
  } catch {
    // Cleanup failures never cross the private-safe UI boundary.
  }
}

function unavailableSession(
  dispatch: ComparisonDispatch,
): ComparisonBridgeSession {
  let disposed = false
  return Object.freeze({
    send(request: ComparisonReadRequest): boolean {
      if (disposed) {
        return false
      }
      dispatch(comparisonRequestStarted(request))
      dispatch(comparisonBridgeUnavailable())
      return false
    },
    dispose(): void {
      disposed = true
    },
  })
}

function matchesPending(
  response: ComparisonReadResponse,
  pending: ComparisonReadRequest | null,
): boolean {
  if (pending === null || response.requestId !== pending.requestId) {
    return false
  }
  const comparison = response.result.comparison
  return comparison === null || (
    comparison.left.versionId === pending.params.leftVersionId &&
    comparison.left.versionDigest === pending.params.leftVersionDigest &&
    comparison.right.versionId === pending.params.rightVersionId &&
    comparison.right.versionDigest === pending.params.rightVersionDigest
  )
}