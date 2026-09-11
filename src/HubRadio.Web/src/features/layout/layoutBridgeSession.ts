import type { BridgeClient } from '../../bridge'
import {
  layoutChildSnapshotMatchesOperation,
  layoutResponseMatchesRequest,
  layoutSnapshotVersion,
  type LayoutBranchContext,
  type LayoutBridgeRequest,
  type LayoutBridgeResponse,
  type LayoutSnapshot,
  type LayoutVersionContext,
} from './layoutProtocol'
import {
  layoutBridgeUnavailable,
  layoutLocalInvalidRequest,
  layoutPendingContextInvalidated,
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
  readonly send: (request: LayoutBridgeRequest, selection?: LayoutVersionContext | null) => boolean
  readonly invalidatePendingContext: () => void
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
  let pendingBranch: LayoutBranchContext | null = null
  const branches = new Map<string, LayoutBranchContext>()
  const versionRoots = new Map<string, string>()
  // Only correlated native evidence can establish the effective cue set. While
  // a request is pending this immutable base cannot advance underneath it.
  let acceptedSnapshot: LayoutSnapshot | null = null
  let invalidated = false
  let disposed = false
  let unsubscribe: () => void
  try {
    unsubscribe = client.onLayoutResponse((response) => {
      if (disposed || !matchesPending(response, pending)) {
        return
      }
      if (!invalidated && pending !== null && (!layoutResponseMatchesRequest(response, pending, pendingBranch) ||
        (response.result.status === 'available' && pending.method === 'layout.apply' &&
          pending.params.expectedVersionId !== undefined &&
          (pending.params.operation.kind === 'editSubtitle' || pending.params.operation.kind === 'editTerop') &&
          (acceptedSnapshot === null || !layoutChildSnapshotMatchesOperation(
            response.result.snapshot, acceptedSnapshot, pending.params.operation,
          ))))) {
        pending = null
        acceptedSnapshot = null
        // A local correlation failure, not an invented native response. Retain
        // the last known editor base through the existing unverified-state gate.
        dispatch(layoutBridgeUnavailable())
        return
      }
      if (invalidated || (response.result.status !== 'available' && response.result.status !== 'noChange')) {
        acceptedSnapshot = null
      } else if (response.result.status === 'available') {
        acceptedSnapshot = response.result.snapshot
        if (pendingBranch !== null) {
          const rootKey = versionKey(pendingBranch.root)
          const branch = Object.freeze({ root: pendingBranch.root,
            sequence: Math.max(pendingBranch.sequence, branches.get(rootKey)?.sequence ?? 0,
              response.result.durableSubtitles?.sequence ?? 0) })
          branches.set(rootKey, branch)
          versionRoots.set(rootKey, rootKey)
          versionRoots.set(versionKey(layoutSnapshotVersion(acceptedSnapshot)), rootKey)
        }
      }
      pending = null
      pendingBranch = null
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
    send(request: LayoutBridgeRequest, selection?: LayoutVersionContext | null): boolean {
      if (disposed) {
        dispatch(layoutBridgeUnavailable())
        return false
      }
      if (pending !== null) {
        return false
      }

      if (request.method === 'layout.apply' && request.params.expectedVersionId !== undefined) {
        const params = request.params
        const operation = params.operation
        const cue = operation.kind === 'editSubtitle'
          ? acceptedSnapshot?.subtitles.find(item => item.cueId === operation.cueId)
          : operation.kind === 'editTerop'
            ? acceptedSnapshot?.terops.find(item => item.cueId === operation.cueId) : undefined
        if (acceptedSnapshot === null || acceptedSnapshot.projectId !== params.projectId ||
          acceptedSnapshot.compositionVersionId !== params.expectedVersionId ||
          acceptedSnapshot.compositionVersionDigest !== params.expectedVersionDigest ||
          acceptedSnapshot.revision !== params.expectedRevision ||
          ((operation.kind === 'editSubtitle' || operation.kind === 'editTerop') &&
            (cue === undefined || cue.revisionId !== operation.expectedRevisionId ||
              cue.revisionDigest !== operation.expectedRevisionDigest))) {
          dispatch(layoutLocalInvalidRequest())
          return false
        }
      }

      pending = request
      const params = request.params
      const requestedVersion = params.expectedVersionId === undefined ? null : Object.freeze({
        projectId: params.projectId, versionId: params.expectedVersionId, versionDigest: params.expectedVersionDigest,
      })
      const key = requestedVersion === null ? null : versionKey(requestedVersion)
      pendingBranch = requestedVersion === null || key === null ? null
        : branches.get(versionRoots.get(key) ?? key) ?? Object.freeze({ root: requestedVersion, sequence: 0 })
      invalidated = false
      dispatch(layoutRequestStarted(
        request.requestId,
        request.method,
        request.params.projectId,
        request.params.expectedVersionId === undefined && selection === undefined
          ? undefined : { request, ...(selection === undefined ? {} : { selection }),
            ...(pendingBranch === null ? {} : { branch: pendingBranch }) },
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
        acceptedSnapshot = null
        dispatch(layoutBridgeUnavailable())
        return false
      }
    },
    invalidatePendingContext(): void {
      if (disposed || pending === null || invalidated) return
      invalidated = true
      dispatch(layoutPendingContextInvalidated())
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
    send(request: LayoutBridgeRequest, selection?: LayoutVersionContext | null): boolean {
      if (disposed) {
        return false
      }
      dispatch(layoutRequestStarted(
        request.requestId,
        request.method,
        request.params.projectId,
        request.params.expectedVersionId === undefined && selection === undefined
          ? undefined : { request, ...(selection === undefined ? {} : { selection }) },
      ))
      dispatch(layoutBridgeUnavailable())
      return false
    },
    invalidatePendingContext(): void {},
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

function versionKey(version: LayoutVersionContext): string {
  return JSON.stringify([version.projectId, version.versionId, version.versionDigest])
}
