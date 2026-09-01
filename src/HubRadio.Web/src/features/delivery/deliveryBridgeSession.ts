import type { BridgeClient } from '../../bridge'
import {
  deliveryOutputTimesEqual,
  type DeliveryReadRequest,
  type DeliveryReadResponse,
} from './deliveryProtocol'
import {
  deliveryBridgeUnavailable,
  deliveryRequestStarted,
  deliveryResponseReceived,
  type DeliveryUiAction,
} from './deliveryState'

export type DeliveryBridgeClient = Pick<
  BridgeClient,
  | 'sendDelivery'
  | 'abandonDeliveryRequest'
  | 'onDeliveryResponse'
  | 'dispose'
>

export type DeliveryDispatch = (action: DeliveryUiAction) => void

export interface DeliveryBridgeSession {
  readonly send: (request: DeliveryReadRequest) => boolean
  readonly dispose: () => void
}

export function startDeliveryBridgeSession(
  createClient: () => DeliveryBridgeClient,
  dispatch: DeliveryDispatch,
): DeliveryBridgeSession {
  let client: DeliveryBridgeClient
  try {
    client = createClient()
  } catch {
    dispatch(deliveryBridgeUnavailable())
    return unavailableSession(dispatch)
  }

  let pending: DeliveryReadRequest | null = null
  let disposed = false
  let unsubscribe: () => void
  try {
    unsubscribe = client.onDeliveryResponse((response) => {
      if (!matchesPending(response, pending)) {
        return
      }
      pending = null
      dispatch(deliveryResponseReceived(response))
    })
  } catch {
    try {
      client.dispose()
    } catch {
      // The fixed unavailable state must not depend on transport cleanup.
    }
    dispatch(deliveryBridgeUnavailable())
    return unavailableSession(dispatch)
  }

  return Object.freeze({
    send(request: DeliveryReadRequest): boolean {
      if (disposed) {
        dispatch(deliveryBridgeUnavailable())
        return false
      }
      if (pending !== null) {
        return false
      }

      pending = request
      dispatch(deliveryRequestStarted(
        request.requestId,
        request.params.outputTime,
      ))
      try {
        client.sendDelivery(request)
        return true
      } catch {
        try {
          client.abandonDeliveryRequest(request.requestId)
        } catch {
          // Public state remains a fixed, private-safe transport error.
        }
        pending = null
        dispatch(deliveryBridgeUnavailable())
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
          client.abandonDeliveryRequest(pending.requestId)
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

function unavailableSession(
  dispatch: DeliveryDispatch,
): DeliveryBridgeSession {
  let disposed = false
  return Object.freeze({
    send(request: DeliveryReadRequest): boolean {
      if (disposed) {
        return false
      }
      dispatch(deliveryRequestStarted(
        request.requestId,
        request.params.outputTime,
      ))
      dispatch(deliveryBridgeUnavailable())
      return false
    },
    dispose(): void {
      disposed = true
    },
  })
}

function matchesPending(
  response: DeliveryReadResponse,
  pending: DeliveryReadRequest | null,
): boolean {
  if (
    pending === null ||
    response.requestId !== pending.requestId ||
    response.method !== pending.method
  ) {
    return false
  }
  if (response.result.status !== 'available') {
    return true
  }
  if (response.result.snapshot.versionId !== pending.params.versionId) {
    return false
  }

  const responseTime = response.result.trace?.outputTime ?? null
  return pending.params.outputTime === null
    ? responseTime === null
    : responseTime !== null &&
        deliveryOutputTimesEqual(responseTime, pending.params.outputTime)
}
