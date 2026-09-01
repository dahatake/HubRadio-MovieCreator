import type { BridgeClient } from '../../bridge'
import type {
  ReviewBridgeRequest,
  ReviewBridgeResponse,
} from './reviewProtocol'
import {
  reviewBridgeUnavailable,
  reviewRequestStarted,
  reviewResponseReceived,
  type ReviewUiAction,
} from './reviewState'

export type ReviewBridgeClient = Pick<
  BridgeClient,
  | 'sendReview'
  | 'abandonReviewRequest'
  | 'onReviewResponse'
  | 'dispose'
>

export type ReviewDispatch = (action: ReviewUiAction) => void

export interface ReviewBridgeSession {
  readonly send: (request: ReviewBridgeRequest) => boolean
  readonly dispose: () => void
}

export function startReviewBridgeSession(
  createClient: () => ReviewBridgeClient,
  dispatch: ReviewDispatch,
): ReviewBridgeSession {
  let client: ReviewBridgeClient
  try {
    client = createClient()
  } catch {
    dispatch(reviewBridgeUnavailable())
    return unavailableSession(dispatch)
  }

  let pending: ReviewBridgeRequest | null = null
  let disposed = false
  let unsubscribe: () => void
  try {
    unsubscribe = client.onReviewResponse((response) => {
      if (!matchesPending(response, pending)) {
        return
      }
      pending = null
      dispatch(reviewResponseReceived(response))
    })
  } catch {
    client.dispose()
    dispatch(reviewBridgeUnavailable())
    return unavailableSession(dispatch)
  }

  return Object.freeze({
    send(request: ReviewBridgeRequest): boolean {
      if (disposed) {
        dispatch(reviewBridgeUnavailable())
        return false
      }
      if (pending !== null) {
        return false
      }

      pending = request
      dispatch(reviewRequestStarted(request.requestId, request.method))
      try {
        client.sendReview(request)
        return true
      } catch {
        try {
          client.abandonReviewRequest(request.requestId)
        } catch {
          // The public state remains a fixed, private-safe unavailable error.
        }
        pending = null
        dispatch(reviewBridgeUnavailable())
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
          client.abandonReviewRequest(pending.requestId)
        } catch {
          // Disposal must continue even if the transport is already unavailable.
        }
        pending = null
      }
      try {
        unsubscribe()
      } finally {
        client.dispose()
      }
    },
  })
}

function unavailableSession(dispatch: ReviewDispatch): ReviewBridgeSession {
  let disposed = false
  return Object.freeze({
    send(request: ReviewBridgeRequest): boolean {
      if (disposed) {
        return false
      }
      dispatch(reviewRequestStarted(request.requestId, request.method))
      dispatch(reviewBridgeUnavailable())
      return false
    },
    dispose(): void {
      disposed = true
    },
  })
}

function matchesPending(
  response: ReviewBridgeResponse,
  pending: ReviewBridgeRequest | null,
): boolean {
  return (
    pending !== null &&
    response.requestId === pending.requestId &&
    response.method === pending.method &&
    ('error' in response || response.result.versionId === pending.params.versionId)
  )
}
