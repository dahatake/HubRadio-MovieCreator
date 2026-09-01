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
  createReviewAddCommentRequest,
  createReviewGetSnapshotRequest,
  createReviewRecordApprovalRequest,
  createReviewRequestChangesRequest,
  type ReviewBridgeRequest,
  type ReviewApprovalActorRole,
  type ReviewAuthorActorRole,
  type ReviewRationalTime,
} from './reviewProtocol'
import {
  startReviewBridgeSession,
  type ReviewBridgeClient,
  type ReviewBridgeSession,
} from './reviewBridgeSession'
import {
  initialReviewState,
  reviewBridgeUnavailable,
  reviewErrorCleared,
  reviewLocalInvalidRequest,
  reviewStateReducer,
  reviewVersionIdChanged,
  selectReviewAction,
  selectReviewActionStatus,
  type ReviewUiState,
} from './reviewState'

export interface ReviewStateContextValue {
  readonly state: ReviewUiState
  readonly changeVersionId: (value: string) => void
  readonly loadSnapshot: () => void
  readonly refreshSnapshot: () => void
  readonly addComment: (
    actorRole: ReviewAuthorActorRole,
    outputTime: ReviewRationalTime,
    text: string,
  ) => void
  readonly requestChanges: (
    outputTime: ReviewRationalTime,
    text: string,
  ) => void
  readonly recordApproval: (
    actorRole: ReviewApprovalActorRole,
    reason: string,
  ) => void
  readonly clearError: () => void
}

export interface ReviewStateProviderProps {
  readonly children: ReactNode
  readonly createClient?: () => ReviewBridgeClient
}

const ReviewStateContext = createContext<ReviewStateContextValue | undefined>(
  undefined,
)
const missingProviderMessage =
  'useReviewState must be used within a ReviewStateProvider.'

export function ReviewStateProvider({
  children,
  createClient = createHostBridgeClient,
}: ReviewStateProviderProps) {
  const [state, dispatch] = useReducer(reviewStateReducer, initialReviewState)
  const sessionRef = useRef<ReviewBridgeSession | null>(null)

  useEffect(() => {
    const session = startReviewBridgeSession(createClient, dispatch)
    sessionRef.current = session
    return () => {
      if (sessionRef.current === session) {
        sessionRef.current = null
      }
      session.dispose()
    }
  }, [createClient])

  const send = useCallback((createRequest: () => ReviewBridgeRequest) => {
    let request: ReviewBridgeRequest
    try {
      request = createRequest()
    } catch {
      dispatch(reviewLocalInvalidRequest())
      return
    }

    const session = sessionRef.current
    if (session === null) {
      dispatch(reviewBridgeUnavailable())
      return
    }
    session.send(request)
  }, [])

  const changeVersionId = useCallback((value: string) => {
    dispatch(reviewVersionIdChanged(value))
  }, [])

  const loadSnapshot = useCallback(() => {
    send(() =>
      createReviewGetSnapshotRequest(createRequestId(), state.versionIdInput),
    )
  }, [send, state.versionIdInput])

  const refreshSnapshot = useCallback(() => {
    const versionId = state.snapshot?.versionId ?? state.versionIdInput
    send(() => createReviewGetSnapshotRequest(createRequestId(), versionId))
  }, [send, state.snapshot?.versionId, state.versionIdInput])

  const addComment = useCallback(
    (
      actorRole: ReviewAuthorActorRole,
      outputTime: ReviewRationalTime,
      text: string,
    ) => {
      const snapshot = state.snapshot
      if (
        snapshot === null ||
        !selectReviewAction(snapshot, 'addComment').actorRoles.includes(actorRole)
      ) {
        return
      }
      send(() =>
        createReviewAddCommentRequest(
          createRequestId(),
          snapshot.versionId,
          snapshot.versionDigest,
          actorRole,
          outputTime,
          text,
        ),
      )
    },
    [send, state.snapshot],
  )

  const requestChanges = useCallback(
    (outputTime: ReviewRationalTime, text: string) => {
      const snapshot = state.snapshot
      if (
        snapshot === null ||
        snapshot.reviewState === null ||
        selectReviewActionStatus(snapshot, 'requestChanges') !== 'authorized'
      ) {
        return
      }
      send(() =>
        createReviewRequestChangesRequest(
          createRequestId(),
          snapshot.versionId,
          snapshot.versionDigest,
          snapshot.reviewState!,
          outputTime,
          text,
        ),
      )
    },
    [send, state.snapshot],
  )

  const recordApproval = useCallback(
    (actorRole: ReviewApprovalActorRole, reason: string) => {
      const snapshot = state.snapshot
      if (
        snapshot === null ||
        !selectReviewAction(snapshot, 'recordApproval').actorRoles.includes(
          actorRole,
        )
      ) {
        return
      }
      send(() =>
        createReviewRecordApprovalRequest(
          createRequestId(),
          snapshot.versionId,
          snapshot.versionDigest,
          actorRole,
          reason,
        ),
      )
    },
    [send, state.snapshot],
  )

  const clearError = useCallback(() => {
    dispatch(reviewErrorCleared())
  }, [])

  const value = useMemo<ReviewStateContextValue>(
    () => ({
      state,
      changeVersionId,
      loadSnapshot,
      refreshSnapshot,
      addComment,
      requestChanges,
      recordApproval,
      clearError,
    }),
    [
      addComment,
      changeVersionId,
      clearError,
      loadSnapshot,
      recordApproval,
      refreshSnapshot,
      requestChanges,
      state,
    ],
  )

  return (
    <ReviewStateContext.Provider value={value}>
      {children}
    </ReviewStateContext.Provider>
  )
}

export function useReviewState(): ReviewStateContextValue {
  const value = useContext(ReviewStateContext)
  if (value === undefined) {
    throw new Error(missingProviderMessage)
  }
  return value
}

function createRequestId(): string {
  return window.crypto.randomUUID().toLowerCase()
}
