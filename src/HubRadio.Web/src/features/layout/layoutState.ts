import {
  layoutResponseMatchesRequest,
  layoutSnapshotMatchesDurableHead,
  layoutSnapshotMatchesTarget,
  layoutSnapshotVersion,
  type LayoutBranchContext,
  type LayoutBridgeRequest,
  type LayoutBridgeResponse,
  type LayoutBridgeStatus,
  type LayoutDurableSubtitles,
  type LayoutSnapshot,
  type LayoutVersionContext,
} from './layoutProtocol'

export interface LayoutRequestContext {
  readonly request: LayoutBridgeRequest
  /** undefined retains the selection; null explicitly selects the legacy project aggregate. */
  readonly selection?: LayoutVersionContext | null
  readonly branch?: LayoutBranchContext
}

export interface LayoutVersionChange {
  readonly source: LayoutVersionContext
  readonly created: LayoutVersionContext
}

export interface PendingLayoutRequest {
  readonly requestId: string
  readonly method: LayoutBridgeRequest['method']
  readonly projectId: string
  readonly context?: LayoutRequestContext
  readonly invalidated?: true
}

export interface LayoutUiError {
  readonly code: 'layout.invalid_request' | 'layout.operation_failed'
  readonly message: string
}

export interface LayoutUiState {
  readonly projectIdInput: string
  /** Selected read context; a verified child snapshot does not change this selection. */
  readonly target?: LayoutVersionContext | null
  /** Original generated root and greatest observed journal sequence; adoption never replaces the root. */
  readonly branch?: LayoutBranchContext | null
  readonly durableSubtitles?: LayoutDurableSubtitles | null
  readonly latestInBranch?: true
  /** Last correlated apply or latest-read child transition, not a fabricated parent field. */
  readonly lastVersionChange?: LayoutVersionChange | null
  readonly snapshot: LayoutSnapshot | null
  readonly status: LayoutBridgeStatus | null
  readonly pending: PendingLayoutRequest | null
  readonly error: LayoutUiError | null
  readonly lastCompletedMethod: LayoutBridgeRequest['method'] | null
}

export type LayoutUiAction =
  | Readonly<{ type: 'projectIdChanged'; value: string }>
  | Readonly<{ type: 'snapshotVersionAdopted'; target: LayoutVersionContext }>
  | Readonly<{
      type: 'requestStarted'
      requestId: string
      method: LayoutBridgeRequest['method']
      projectId: string
      context?: LayoutRequestContext
    }>
  | Readonly<{ type: 'pendingContextInvalidated' }>
  | Readonly<{ type: 'responseReceived'; response: LayoutBridgeResponse }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'localInvalidRequest' }>
  | Readonly<{ type: 'errorCleared' }>

export const initialLayoutState: LayoutUiState = Object.freeze({
  projectIdInput: '',
  target: null,
  branch: null,
  durableSubtitles: null,
  lastVersionChange: null,
  snapshot: null,
  status: null,
  pending: null,
  error: null,
  lastCompletedMethod: null,
})

export function layoutProjectIdChanged(value: string): LayoutUiAction {
  return { type: 'projectIdChanged', value }
}

export function layoutRequestStarted(
  requestId: string,
  method: LayoutBridgeRequest['method'],
  projectId: string,
  context?: LayoutRequestContext,
): LayoutUiAction {
  return { type: 'requestStarted', requestId, method, projectId, ...(context === undefined ? {} : { context }) }
}

export function layoutPendingContextInvalidated(): LayoutUiAction {
  return { type: 'pendingContextInvalidated' }
}

export function layoutSnapshotVersionAdopted(target: LayoutVersionContext): LayoutUiAction {
  return { type: 'snapshotVersionAdopted', target }
}

export function layoutCanAdoptSnapshotVersion(state: LayoutUiState, target: LayoutVersionContext): boolean {
  return state.pending?.invalidated !== true &&
    (state.status === 'available' || state.status === 'noChange') &&
    state.snapshot !== null && state.lastVersionChange != null &&
    layoutSnapshotMatchesTarget(state.snapshot, target) &&
    layoutSnapshotMatchesTarget(state.snapshot, state.lastVersionChange.created)
}

export function layoutHasPersistedSubtitles(state: LayoutUiState): boolean {
  const { snapshot, durableSubtitles, branch } = state
  return (state.status === 'available' || state.status === 'noChange') &&
    snapshot !== null && snapshot.persistence === 'persisted' && durableSubtitles != null && branch != null &&
    durableSubtitles.sequence > 0 && branch.sequence >= durableSubtitles.sequence &&
    branch.root.projectId === snapshot.projectId && branch.root.versionId === durableSubtitles.rootVersionId &&
    branch.root.versionDigest === durableSubtitles.headVersionDigest &&
    layoutSnapshotMatchesDurableHead(snapshot, durableSubtitles)
}

export function layoutResponseReceived(
  response: LayoutBridgeResponse,
): LayoutUiAction {
  return { type: 'responseReceived', response }
}

export function layoutBridgeUnavailable(): LayoutUiAction {
  return { type: 'bridgeUnavailable' }
}

export function layoutLocalInvalidRequest(): LayoutUiAction {
  return { type: 'localInvalidRequest' }
}

export function layoutErrorCleared(): LayoutUiAction {
  return { type: 'errorCleared' }
}

export function layoutStateReducer(
  state: LayoutUiState,
  action: LayoutUiAction,
): LayoutUiState {
  const hasSameProjectBase = state.snapshot?.projectId === state.projectIdInput &&
    (state.pending === null || state.pending.projectId === state.projectIdInput)

  switch (action.type) {
    case 'snapshotVersionAdopted':
      return layoutCanAdoptSnapshotVersion(state, action.target) && !sameSelection(state.target ?? null, action.target)
        ? { ...state, target: Object.freeze({ ...action.target }) } : state

    case 'projectIdChanged':
      return {
        ...state,
        projectIdInput: action.value,
        target: action.value === state.projectIdInput ? state.target : null,
        branch: action.value === state.projectIdInput ? state.branch : null,
        durableSubtitles: action.value === state.projectIdInput ? state.durableSubtitles : null,
        latestInBranch: action.value === state.projectIdInput ? state.latestInBranch : undefined,
        lastVersionChange: action.value === state.projectIdInput ? state.lastVersionChange : null,
        pending: state.pending !== null && action.value !== state.projectIdInput
          ? { ...state.pending, invalidated: true } : state.pending,
        snapshot: state.snapshot?.projectId === action.value
          ? state.snapshot
          : null,
        status: state.snapshot?.projectId === action.value
          ? state.status
          : null,
        error: null,
        lastCompletedMethod: null,
      }

    case 'requestStarted': {
      if (state.pending !== null) {
        return state
      }
      const target = action.context?.selection === undefined ? state.target ?? null : action.context.selection
      const changedSelection = !sameSelection(state.target ?? null, target)
      return {
        ...state,
        ...(action.context === undefined ? {} : {
          projectIdInput: action.projectId,
          target,
          snapshot: changedSelection || state.snapshot?.projectId !== action.projectId ? null : state.snapshot,
          status: changedSelection || state.snapshot?.projectId !== action.projectId ? null : state.status,
          branch: action.context.branch ?? (changedSelection ? null : state.branch),
          durableSubtitles: changedSelection ? null : state.durableSubtitles,
          lastVersionChange: changedSelection ? null : state.lastVersionChange,
          ...(action.context.request.method === 'layout.read'
            ? { latestInBranch: action.context.request.params.latestInBranch } : {}),
        }),
        pending: {
          requestId: action.requestId,
          method: action.method,
          projectId: action.projectId,
          ...(action.context === undefined ? {} : { context: action.context }),
        },
        error: null,
      }
    }

    case 'pendingContextInvalidated':
      return state.pending === null || state.pending.invalidated ? state : {
        ...state,
        pending: { ...state.pending, invalidated: true },
        lastCompletedMethod: null,
      }

    case 'responseReceived': {
      const pending = state.pending
      const response = action.response
      if (
        pending === null ||
        pending.requestId !== response.requestId ||
        pending.method !== response.method ||
        (response.result.status === 'available' &&
          response.result.snapshot.projectId !== pending.projectId)
      ) {
        return state
      }
      if (pending.invalidated) {
        // Drain, do not cancel a possibly committed native mutation or ACK its
        // draft. The old mounted base remains available only for recovery.
        return {
          ...state,
          pending: null,
          status: state.snapshot === null ? null : 'invalid',
          error: null,
          lastCompletedMethod: null,
        }
      }
      if (pending.context === undefined
        ? response.result.status === 'available' && (response.result.durableSubtitles !== undefined || response.result.snapshot.persistence !== 'notPersisted')
        : !layoutResponseMatchesRequest(response, pending.context.request, pending.context.branch ?? state.branch)) {
        return layoutStateReducer(state, layoutBridgeUnavailable())
      }
      if (response.result.status !== 'available') {
        if (response.result.status === 'noChange') {
          return {
            ...state,
            // A read without fresh evidence cannot recover an unverified base.
            status: response.method === 'layout.read' &&
              (state.status === 'conflict' || state.status === 'invalid')
              ? state.status : 'noChange',
            pending: null,
            error: null,
            lastCompletedMethod: response.method,
          }
        }
        // A read can fail after a successful partial ACK, not just a rejected
        // apply. Keep the same-project last-known base only for recovery;
        // authorization, absence and native unavailability stay closed.
        const retainRecoveryBase =
          (response.result.status === 'conflict' || response.result.status === 'invalid') &&
          hasSameProjectBase
        return {
          ...state,
          snapshot: retainRecoveryBase ? state.snapshot : null,
          status: response.result.status,
          pending: null,
          error: null,
          lastCompletedMethod: response.method,
        }
      }
      const request = pending.context?.request
      const requestedVersion = request?.params.expectedVersionId === undefined ? null : {
        projectId: request.params.projectId, versionId: request.params.expectedVersionId,
        versionDigest: request.params.expectedVersionDigest,
      }
      const branch = pending.context?.branch ?? state.branch ??
        (requestedVersion === null ? null : { root: requestedVersion, sequence: 0 })
      const source = state.snapshot === null ? requestedVersion : layoutSnapshotVersion(state.snapshot)
      const followsChild = source !== null && requestedVersion !== null &&
        (response.method === 'layout.apply' || (request?.method === 'layout.read' && request.params.latestInBranch === true)) &&
        !layoutSnapshotMatchesTarget(response.result.snapshot, source)
      return {
        ...state,
        projectIdInput: response.result.snapshot.projectId,
        snapshot: response.result.snapshot,
        branch: branch === null ? null : { root: branch.root,
          sequence: Math.max(branch.sequence, response.result.durableSubtitles?.sequence ?? 0) },
        durableSubtitles: response.result.durableSubtitles ?? null,
        lastVersionChange: followsChild
          ? { source, created: layoutSnapshotVersion(response.result.snapshot) }
          : state.lastVersionChange,
        status: 'available',
        pending: null,
        error: null,
        lastCompletedMethod: response.method,
      }
    }

    case 'bridgeUnavailable':
      return {
        ...state,
        snapshot: hasSameProjectBase ? state.snapshot : null,
        // Reuse the unverified-evidence gate even after dismissing the error
        // or retrying a read. The error, not a completed method, marks transport failure.
        status: hasSameProjectBase ? 'invalid' : null,
        pending: null,
        error: {
          code: 'layout.operation_failed',
          message: 'ネイティブホストへレイアウト要求を送信できませんでした。',
        },
        lastCompletedMethod: null,
      }

    case 'localInvalidRequest':
      return {
        ...state,
        error: {
          code: 'layout.invalid_request',
          message: 'プロジェクト ID、版、座標、時刻、または編集内容を確認してください。',
        },
      }

    case 'errorCleared':
      return { ...state, error: null }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}

function sameSelection(left: LayoutVersionContext | null, right: LayoutVersionContext | null): boolean {
  return left === right || (left !== null && right !== null && left.projectId === right.projectId &&
    left.versionId === right.versionId && left.versionDigest === right.versionDigest)
}
