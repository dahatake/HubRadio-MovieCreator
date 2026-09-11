import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useReducer,
  useRef,
  type ReactNode,
} from 'react'

import { createHostBridgeClient } from '../../bridge'
import {
  createLayoutApplyRequest,
  createLayoutReadRequest,
  layoutSnapshotMatchesTarget,
  layoutSnapshotVersion,
  layoutSupportsManualEditing,
  type LayoutOperation,
  type LayoutReadOptions,
  type LayoutVersionContext,
} from './layoutProtocol'
import {
  startLayoutBridgeSession,
  type LayoutBridgeClient,
  type LayoutBridgeSession,
} from './layoutBridgeSession'
import {
  initialLayoutState,
  layoutBridgeUnavailable,
  layoutErrorCleared,
  layoutLocalInvalidRequest,
  layoutProjectIdChanged,
  layoutSnapshotVersionAdopted,
  layoutStateReducer,
  type LayoutUiState,
} from './layoutState'

export interface LayoutStateContextValue {
  readonly state: LayoutUiState
  readonly changeProjectId: (value: string) => void
  readonly loadSnapshot: (target?: LayoutVersionContext, options?: LayoutReadOptions) => void
  readonly refreshSnapshot: (target?: LayoutVersionContext, options?: LayoutReadOptions) => void
  /** Invalidate the result, but drain a pending mutation before another request. */
  readonly invalidatePendingContext?: () => void
  /** Adopt only the current correlated child; no read, rebase or draft reset. */
  readonly adoptSnapshotVersion?: (target: LayoutVersionContext) => void
  readonly applyOperation: (operation: LayoutOperation) => void
  readonly clearError: () => void
}

export interface LayoutStateProviderProps {
  readonly children: ReactNode
  readonly createClient?: () => LayoutBridgeClient
}

const LayoutStateContext = createContext<LayoutStateContextValue | undefined>(
  undefined,
)
const missingProviderMessage =
  'useLayoutState must be used within a LayoutStateProvider.'

export function LayoutStateProvider({
  children,
  createClient = createHostBridgeClient,
}: LayoutStateProviderProps) {
  const [state, dispatch] = useReducer(layoutStateReducer, initialLayoutState)
  const sessionRef = useRef<LayoutBridgeSession | null>(null)

  useEffect(() => {
    const session = startLayoutBridgeSession(createClient, dispatch)
    sessionRef.current = session
    return () => {
      if (sessionRef.current === session) {
        sessionRef.current = null
      }
      session.dispose()
    }
  }, [createClient])

  const send = useCallback((
    request: Parameters<LayoutBridgeSession['send']>[0],
    selection?: LayoutVersionContext | null,
  ) => {
    const session = sessionRef.current
    if (session === null) {
      dispatch(layoutBridgeUnavailable())
      return
    }
    session.send(request, selection)
  }, [])

  const changeProjectId = useCallback((value: string) => {
    if (value !== state.projectIdInput) sessionRef.current?.invalidatePendingContext()
    dispatch(layoutProjectIdChanged(value))
  }, [state.projectIdInput])

  const loadSnapshot = useCallback((target?: LayoutVersionContext, options?: LayoutReadOptions) => {
    try {
      send(createLayoutReadRequest(
        createRequestId(),
        target?.projectId ?? state.projectIdInput,
        null,
        target,
        options,
      ), target === undefined ? null : Object.freeze({ ...target }))
    } catch {
      dispatch(layoutLocalInvalidRequest())
    }
  }, [send, state.projectIdInput])

  const refreshSnapshot = useCallback((target?: LayoutVersionContext, options?: LayoutReadOptions) => {
    const latest = options === undefined ? (target === undefined ? state.latestInBranch : undefined) : options.latestInBranch
    const sameRoot = target !== undefined && state.branch != null &&
      target.projectId === state.branch.root.projectId && target.versionId === state.branch.root.versionId &&
      target.versionDigest === state.branch.root.versionDigest
    if (target !== undefined && !(latest === true && sameRoot) &&
      !(state.snapshot !== null && layoutSnapshotMatchesTarget(state.snapshot, target))) {
      loadSnapshot(target, options)
      return
    }
    // A latest refresh pins the ORIGINAL root even after child adoption. An
    // exact warning refresh stays exact; neither path changes dirty selection.
    const currentTarget = target ?? (latest === true ? state.branch?.root ?? state.target ?? undefined
      : state.target == null ? undefined : state.snapshot === null ? state.target : layoutSnapshotVersion(state.snapshot))
    try {
      send(createLayoutReadRequest(createRequestId(), state.projectIdInput, null, currentTarget,
        options ?? (latest === true ? { latestInBranch: true } : undefined)))
    } catch {
      dispatch(layoutLocalInvalidRequest())
    }
  }, [loadSnapshot, send, state.branch, state.latestInBranch, state.projectIdInput, state.snapshot, state.target])

  const invalidatePendingContext = useCallback(() => {
    sessionRef.current?.invalidatePendingContext()
  }, [])

  const adoptSnapshotVersion = useCallback((target: LayoutVersionContext) => {
    dispatch(layoutSnapshotVersionAdopted(target))
  }, [])

  const applyOperation = useCallback((operation: LayoutOperation) => {
    const snapshot = state.snapshot
    if (snapshot === null || state.pending !== null || state.status === 'conflict' || state.status === 'invalid' ||
      ((operation.kind === 'setManualLayout' || operation.kind === 'setKeyframes') &&
        !layoutSupportsManualEditing(snapshot))) {
      dispatch(layoutLocalInvalidRequest())
      return
    }
    if (state.target != null && (operation.kind === 'editSubtitle' || operation.kind === 'editTerop')) {
      const cues = operation.kind === 'editSubtitle' ? snapshot.subtitles : snapshot.terops
      const cue = cues.find(item => item.cueId === operation.cueId)
      // An effective snapshot may retain a cue revision from an ancestor.
      if (cue === undefined ||
        cue.revisionId !== operation.expectedRevisionId || cue.revisionDigest !== operation.expectedRevisionDigest) {
        dispatch(layoutLocalInvalidRequest())
        return
      }
    }
    try {
      send(createLayoutApplyRequest(
        createRequestId(),
        snapshot.projectId,
        snapshot.revision,
        operation,
        state.target == null ? undefined : layoutSnapshotVersion(snapshot),
      ))
    } catch {
      dispatch(layoutLocalInvalidRequest())
    }
  }, [send, state.pending, state.snapshot, state.status, state.target])

  const clearError = useCallback(() => {
    dispatch(layoutErrorCleared())
  }, [])

  const value = useMemo<LayoutStateContextValue>(
    () => ({
      state,
      changeProjectId,
      loadSnapshot,
      refreshSnapshot,
      invalidatePendingContext,
      adoptSnapshotVersion,
      applyOperation,
      clearError,
    }),
    [
      applyOperation,
      changeProjectId,
      clearError,
      loadSnapshot,
      refreshSnapshot,
      invalidatePendingContext,
      adoptSnapshotVersion,
      state,
    ],
  )

  return (
    <LayoutStateContext.Provider value={value}>
      {children}
    </LayoutStateContext.Provider>
  )
}

export function useLayoutState(): LayoutStateContextValue {
  const value = useContext(LayoutStateContext)
  if (value === undefined) {
    throw new Error(missingProviderMessage)
  }
  return value
}

function createRequestId(): string {
  return window.crypto.randomUUID().toLowerCase()
}
