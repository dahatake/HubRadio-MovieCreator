import { describe, expect, it } from 'vitest'

import {
  createReviewAddCommentRequest,
  createReviewGetSnapshotRequest,
  parseReviewBridgeResponse,
  REVIEW_ADD_COMMENT_METHOD,
  REVIEW_GET_SNAPSHOT_METHOD,
  type ReviewBridgeErrorResponse,
  type ReviewBridgeRequest,
  type ReviewBridgeResponse,
} from './reviewProtocol'
import {
  startReviewBridgeSession,
  type ReviewBridgeClient,
} from './reviewBridgeSession'
import {
  reviewBridgeUnavailable,
  reviewRequestStarted,
  reviewResponseReceived,
  type ReviewUiAction,
} from './reviewState'
import {
  createReviewResponseValue,
  reviewRequestId,
  reviewSecondRequestId,
  reviewVersionDigest,
  reviewVersionId,
} from './reviewTestFixture'

const outputTime = { value: '1', rate: '2' } as const

describe('review bridge session', () => {
  it('correlates a response, rejects double submit, then permits the next request', () => {
    const client = new FakeClient()
    const actions: ReviewUiAction[] = []
    const session = startReviewBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const first = createReviewGetSnapshotRequest(reviewRequestId, reviewVersionId)
    const second = createReviewAddCommentRequest(
      reviewSecondRequestId,
      reviewVersionId,
      reviewVersionDigest,
      'contentApprover',
      outputTime,
      '確認コメントです。',
    )

    expect(session.send(first)).toBe(true)
    expect(session.send(second)).toBe(false)
    expect(client.sent).toEqual([first])
    expect(actions).toEqual([
      reviewRequestStarted(reviewRequestId, REVIEW_GET_SNAPSHOT_METHOD),
    ])

    client.emit(snapshotResponse(reviewSecondRequestId, REVIEW_GET_SNAPSHOT_METHOD))
    client.emit(snapshotResponse(reviewRequestId, REVIEW_ADD_COMMENT_METHOD))
    client.emit(mismatchedVersionResponse(reviewRequestId))
    expect(actions).toHaveLength(1)

    const accepted = snapshotResponse(reviewRequestId, REVIEW_GET_SNAPSHOT_METHOD)
    client.emit(accepted)
    expect(actions.at(-1)).toEqual(reviewResponseReceived(accepted))
    expect(session.send(second)).toBe(true)
    expect(client.sent).toEqual([first, second])
  })

  it('accepts a correlated closed error and clears the session pending slot', () => {
    const client = new FakeClient()
    const actions: ReviewUiAction[] = []
    const session = startReviewBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const request = createReviewAddCommentRequest(
      reviewRequestId,
      reviewVersionId,
      reviewVersionDigest,
      'contentApprover',
      outputTime,
      '確認コメントです。',
    )
    session.send(request)
    const response: ReviewBridgeErrorResponse = {
      version: 1,
      requestId: reviewRequestId,
      method: REVIEW_ADD_COMMENT_METHOD,
      error: {
        code: 'review.stale_version',
        message: '対象版は現行版ではありません。レビューを再読み込みしてください。',
      },
    }

    client.emit(response)
    expect(actions.at(-1)).toEqual(reviewResponseReceived(response))
    expect(
      session.send(
        createReviewGetSnapshotRequest(reviewSecondRequestId, reviewVersionId),
      ),
    ).toBe(true)
  })

  it('abandons transport correlation and clears UI pending state when send fails', () => {
    const client = new FakeClient()
    const actions: ReviewUiAction[] = []
    const session = startReviewBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    client.throwOnSend = true
    const request = createReviewGetSnapshotRequest(reviewRequestId, reviewVersionId)

    expect(session.send(request)).toBe(false)
    expect(client.abandoned).toEqual([reviewRequestId])
    expect(actions).toEqual([
      reviewRequestStarted(reviewRequestId, REVIEW_GET_SNAPSHOT_METHOD),
      reviewBridgeUnavailable(),
    ])
    expect(JSON.stringify(actions)).not.toContain('C:\\private')

    client.throwOnSend = false
    expect(
      session.send(
        createReviewGetSnapshotRequest(reviewSecondRequestId, reviewVersionId),
      ),
    ).toBe(true)
  })

  it('maps client creation and subscription failures to a private-safe session', () => {
    const createActions: ReviewUiAction[] = []
    const unavailable = startReviewBridgeSession(
      () => {
        throw new Error('C:\\private\\review.db')
      },
      (action) => createActions.push(action),
    )
    expect(createActions).toEqual([reviewBridgeUnavailable()])

    const request = createReviewGetSnapshotRequest(reviewRequestId, reviewVersionId)
    expect(unavailable.send(request)).toBe(false)
    expect(createActions.slice(-2)).toEqual([
      reviewRequestStarted(reviewRequestId, REVIEW_GET_SNAPSHOT_METHOD),
      reviewBridgeUnavailable(),
    ])
    expect(JSON.stringify(createActions)).not.toContain('C:\\private')

    const client = new FakeClient()
    client.throwOnSubscribe = true
    const subscribeActions: ReviewUiAction[] = []
    startReviewBridgeSession(
      () => client,
      (action) => subscribeActions.push(action),
    )
    expect(client.disposeCount).toBe(1)
    expect(subscribeActions).toEqual([reviewBridgeUnavailable()])
  })

  it('abandons one pending request and cleans up exactly once on disposal', () => {
    const client = new FakeClient()
    const actions: ReviewUiAction[] = []
    const session = startReviewBridgeSession(
      () => client,
      (action) => actions.push(action),
    )
    const request = createReviewGetSnapshotRequest(reviewRequestId, reviewVersionId)
    session.send(request)

    session.dispose()
    session.dispose()
    expect(client.abandoned).toEqual([reviewRequestId])
    expect(client.unsubscribeCount).toBe(1)
    expect(client.disposeCount).toBe(1)
    expect(session.send(request)).toBe(false)
    expect(actions.at(-1)).toEqual(reviewBridgeUnavailable())
  })
})

class FakeClient implements ReviewBridgeClient {
  readonly sent: ReviewBridgeRequest[] = []
  readonly abandoned: string[] = []
  throwOnSend = false
  throwOnSubscribe = false
  unsubscribeCount = 0
  disposeCount = 0
  private listener: ((response: ReviewBridgeResponse) => void) | null = null

  sendReview(request: ReviewBridgeRequest): void {
    if (this.throwOnSend) {
      throw new Error('native stack C:\\private\\review.db')
    }
    this.sent.push(request)
  }

  abandonReviewRequest(requestId: string): void {
    this.abandoned.push(requestId)
  }

  onReviewResponse(
    listener: (response: ReviewBridgeResponse) => void,
  ): () => void {
    if (this.throwOnSubscribe) {
      throw new Error('native subscription stack')
    }
    this.listener = listener
    return () => {
      this.unsubscribeCount++
      this.listener = null
    }
  }

  dispose(): void {
    this.disposeCount++
    this.listener = null
  }

  emit(response: ReviewBridgeResponse): void {
    this.listener?.(response)
  }
}

function snapshotResponse(
  requestId: string,
  method: typeof REVIEW_GET_SNAPSHOT_METHOD | typeof REVIEW_ADD_COMMENT_METHOD,
): ReviewBridgeResponse {
  const parsed = parseReviewBridgeResponse(
    createReviewResponseValue(requestId, method),
  )
  if (parsed === null) {
    throw new Error('Expected a valid review response.')
  }
  return parsed
}

function mismatchedVersionResponse(requestId: string): ReviewBridgeResponse {
  const value = createReviewResponseValue(requestId, REVIEW_GET_SNAPSHOT_METHOD)
  value.result.versionId = reviewSecondRequestId
  value.result.approvals[0]!.versionId = reviewSecondRequestId
  value.result.directParentDiff.toVersionId = reviewSecondRequestId
  const parsed = parseReviewBridgeResponse(value)
  if (parsed === null) {
    throw new Error('Expected a valid but differently targeted response.')
  }
  return parsed
}
