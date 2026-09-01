import { describe, expect, it } from 'vitest'

import {
  parseReviewBridgeResponse,
  REVIEW_ADD_COMMENT_METHOD,
  REVIEW_GET_SNAPSHOT_METHOD,
  REVIEW_RECORD_APPROVAL_METHOD,
  type ReviewBridgeErrorResponse,
  type ReviewBridgeMethod,
  type ReviewBridgeResultResponse,
} from './reviewProtocol'
import {
  initialReviewState,
  reviewBridgeUnavailable,
  reviewErrorCleared,
  reviewLocalInvalidRequest,
  reviewRequestStarted,
  reviewResponseReceived,
  reviewStateReducer,
  reviewVersionIdChanged,
  selectReviewActionStatus,
  type ReviewUiAction,
  type ReviewUiState,
} from './reviewState'
import {
  createReviewResponseValue,
  reviewRequestId,
  reviewSecondRequestId,
  reviewVersionId,
} from './reviewTestFixture'

describe('review state reducer', () => {
  it('starts with an explicit empty, non-fabricated state', () => {
    expect(initialReviewState).toEqual({
      versionIdInput: '',
      snapshot: null,
      pending: null,
      error: null,
      lastCompletedMethod: null,
    })
    expect(Object.isFrozen(initialReviewState)).toBe(true)
  })

  it('retains only a snapshot that exactly matches the edited version target', () => {
    const loaded = reduce(
      reviewVersionIdChanged(reviewVersionId),
      reviewRequestStarted(reviewRequestId, REVIEW_GET_SNAPSHOT_METHOD),
      reviewResponseReceived(snapshotResponse()),
    )

    const unchanged = reviewStateReducer(
      loaded,
      reviewVersionIdChanged(reviewVersionId),
    )
    expect(unchanged.snapshot).toBe(loaded.snapshot)

    const changed = reviewStateReducer(
      loaded,
      reviewVersionIdChanged(reviewSecondRequestId),
    )
    expect(changed.versionIdInput).toBe(reviewSecondRequestId)
    expect(changed.snapshot).toBeNull()
    expect(changed.lastCompletedMethod).toBeNull()
  })

  it('allows only one pending request and correlates both ID and method', () => {
    const pending = reduce(
      reviewVersionIdChanged(reviewVersionId),
      reviewRequestStarted(reviewRequestId, REVIEW_GET_SNAPSHOT_METHOD),
    )
    expect(
      reviewStateReducer(
        pending,
        reviewRequestStarted(reviewSecondRequestId, REVIEW_ADD_COMMENT_METHOD),
      ),
    ).toBe(pending)

    const wrongId = reviewStateReducer(
      pending,
      reviewResponseReceived(
        snapshotResponse(reviewSecondRequestId, REVIEW_GET_SNAPSHOT_METHOD),
      ),
    )
    const wrongMethod = reviewStateReducer(
      pending,
      reviewResponseReceived(
        snapshotResponse(reviewRequestId, REVIEW_ADD_COMMENT_METHOD),
      ),
    )
    expect(wrongId).toBe(pending)
    expect(wrongMethod).toBe(pending)

    const accepted = reviewStateReducer(
      pending,
      reviewResponseReceived(snapshotResponse()),
    )
    expect(accepted.pending).toBeNull()
    expect(accepted.snapshot?.versionId).toBe(reviewVersionId)
    expect(accepted.lastCompletedMethod).toBe(REVIEW_GET_SNAPSHOT_METHOD)
    expect(
      reviewStateReducer(
        accepted,
        reviewResponseReceived(snapshotResponse()),
      ),
    ).toBe(accepted)
  })

  it('replaces the snapshot only after a correlated mutation response', () => {
    const loaded = reduce(
      reviewVersionIdChanged(reviewVersionId),
      reviewRequestStarted(reviewRequestId, REVIEW_GET_SNAPSHOT_METHOD),
      reviewResponseReceived(snapshotResponse()),
    )
    const mutationPending = reviewStateReducer(
      loaded,
      reviewRequestStarted(reviewSecondRequestId, REVIEW_ADD_COMMENT_METHOD),
    )
    const mutation = snapshotResponse(
      reviewSecondRequestId,
      REVIEW_ADD_COMMENT_METHOD,
    )
    const completed = reviewStateReducer(
      mutationPending,
      reviewResponseReceived(mutation),
    )

    expect(completed.snapshot).toBe(mutation.result)
    expect(completed.lastCompletedMethod).toBe(REVIEW_ADD_COMMENT_METHOD)
    expect(completed.error).toBeNull()
  })

  it('preserves the last snapshot for explicit stale-version recovery', () => {
    const loaded = reduce(
      reviewVersionIdChanged(reviewVersionId),
      reviewRequestStarted(reviewRequestId, REVIEW_GET_SNAPSHOT_METHOD),
      reviewResponseReceived(snapshotResponse()),
      reviewRequestStarted(reviewSecondRequestId, REVIEW_RECORD_APPROVAL_METHOD),
    )
    const response = errorResponse(
      reviewSecondRequestId,
      REVIEW_RECORD_APPROVAL_METHOD,
      'review.stale_version',
      '対象版は現行版ではありません。レビューを再読み込みしてください。',
    )
    const failed = reviewStateReducer(
      loaded,
      reviewResponseReceived(response),
    )

    expect(failed.snapshot).toBe(loaded.snapshot)
    expect(failed.pending).toBeNull()
    expect(failed.error).toEqual(response.error)
    expect(failed.lastCompletedMethod).toBeNull()
    expect(reviewStateReducer(failed, reviewErrorCleared()).error).toBeNull()
  })

  it('maps local and transport failures to fixed private-safe errors', () => {
    const pending = reduce(
      reviewVersionIdChanged(reviewVersionId),
      reviewRequestStarted(reviewRequestId, REVIEW_GET_SNAPSHOT_METHOD),
    )
    const unavailable = reviewStateReducer(pending, reviewBridgeUnavailable())
    expect(unavailable.pending).toBeNull()
    expect(unavailable.error).toEqual({
      code: 'review.operation_failed',
      message: 'ネイティブホストへレビュー要求を送信できませんでした。',
    })

    const invalid = reviewStateReducer(
      unavailable,
      reviewLocalInvalidRequest(),
    )
    expect(invalid.error).toEqual({
      code: 'review.invalid_request',
      message: '版 ID、時刻、または入力内容を確認してください。',
    })
    expect(JSON.stringify(invalid)).not.toContain('C:\\')
    expect(JSON.stringify(invalid)).not.toContain('S-1-5-')
  })

  it('uses only native action entries and fails closed when one is absent', () => {
    const snapshot = snapshotResponse().result
    expect(selectReviewActionStatus(snapshot, 'recordApproval')).toBe(
      'authorized',
    )

    expect(() =>
      selectReviewActionStatus(
        { ...snapshot, actions: snapshot.actions.slice(0, 2) },
        'recordApproval',
      ),
    ).toThrow('missing an action')
  })
})

function reduce(...actions: readonly ReviewUiAction[]): ReviewUiState {
  return actions.reduce<ReviewUiState>(reviewStateReducer, initialReviewState)
}

function snapshotResponse(
  requestId = reviewRequestId,
  method: ReviewBridgeMethod = REVIEW_GET_SNAPSHOT_METHOD,
): ReviewBridgeResultResponse {
  const parsed = parseReviewBridgeResponse(
    createReviewResponseValue(requestId, method),
  )
  if (parsed === null || !('result' in parsed)) {
    throw new Error('Expected a valid review snapshot response.')
  }
  return parsed
}

function errorResponse(
  requestId: string,
  method: ReviewBridgeMethod,
  code: ReviewBridgeErrorResponse['error']['code'],
  message: string,
): ReviewBridgeErrorResponse {
  return {
    version: 1,
    requestId,
    method,
    error: { code, message },
  }
}
