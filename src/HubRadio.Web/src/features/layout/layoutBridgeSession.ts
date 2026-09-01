import type { BridgeClient } from '../../bridge'
import type {
  LayoutBridgeRequest,
  LayoutBridgeResponse,
} from './layoutProtocol'
import {
  layoutBridgeUnavailable,
  layoutRequestStarted,
  layoutResponseReceived,
  type LayoutUiAction,
} from './layoutState'

export type LayoutBridgeClient = Pick<
  BridgeClient,
  | 'sendLayout'
  | 'abandonLayoutRequest'
  | 'onLayoutResponse'
  | 'dispose'
>

export type LayoutDispatch = (action: LayoutUiAction) => void

export interface LayoutBridgeSession {
  readonly send: (request: LayoutBridgeRequest) => boolean
  readonly dispose: () => void
}

export function startLayoutBridgeSession(
  createClient: () => LayoutBridgeClient,
  dispatch: LayoutDispatch,
): LayoutBridgeSession {
  let client: LayoutBridgeClient
  try {
    client = createClient()
  } catch {
    dispatch(layoutBridgeUnavailable())
    return unavailableSession(dispatch)
  }

  let pending: LayoutBridgeRequest | null = null
  let disposed = false
  let unsubscribe: () => void
  try {
    unsubscribe = client.onLayoutResponse((response) => {
      if (!matchesPending(response, pending)) {
        return
      }
      pending = null
      dispatch(layoutResponseReceived(response))
    })
  } catch {
    try {
      client.dispose()
    } catch {
      // The fixed unavailable state must not depend on transport cleanup.
    }
    dispatch(layoutBridgeUnavailable())
    return unavailableSession(dispatch)
  }

  return Object.freeze({
    send(request: LayoutBridgeRequest): boolean {
      if (disposed) {
        dispatch(layoutBridgeUnavailable())
        return false
      }
      if (pending !== null) {
        return false
      }

      pending = request
      dispatch(layoutRequestStarted(
        request.requestId,
        request.method,
        request.params.projectId,
      ))
      try {
        client.sendLayout(request)
        return true
      } catch {
        try {
          client.abandonLayoutRequest(request.requestId)
        } catch {
          // Public state remains a fixed, private-safe transport error.
        }
        pending = null
        dispatch(layoutBridgeUnavailable())
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
          client.abandonLayoutRequest(pending.requestId)
        } catch {
          // Disposal continues even when the native transport is unavailable.
        }
        pending = null
      }
      try {
        unsubscribe()
      } catch {
        // Continue closing the underlying client after listener cleanup fails.
      }
      try {
        client.dispose()
      } catch {
        // React cleanup must not surface private transport failures.
      }
    },
  })
}

function unavailableSession(dispatch: LayoutDispatch): LayoutBridgeSession {
  let disposed = false
  return Object.freeze({
    send(request: LayoutBridgeRequest): boolean {
      if (disposed) {
        return false
      }
      dispatch(layoutRequestStarted(
        request.requestId,
        request.method,
        request.params.projectId,
      ))
      dispatch(layoutBridgeUnavailable())
      return false
    },
    dispose(): void {
      disposed = true
    },
  })
}

function matchesPending(
  response: LayoutBridgeResponse,
  pending: LayoutBridgeRequest | null,
): boolean {
  return pending !== null &&
    response.requestId === pending.requestId &&
    response.method === pending.method &&
    (response.result.status !== 'available' ||
      response.result.snapshot.projectId === pending.params.projectId)
}
