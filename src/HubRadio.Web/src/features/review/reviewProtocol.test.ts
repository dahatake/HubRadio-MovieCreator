import { describe, expect, it } from 'vitest'

import {
  createReviewAddCommentRequest,
  createReviewGetSnapshotRequest,
  createReviewRecordApprovalRequest,
  createReviewRequestChangesRequest,
  isReviewOutputTime,
  isReviewTextInput,
  parseReviewBridgeResponse,
  REVIEW_ADD_COMMENT_METHOD,
  REVIEW_GET_SNAPSHOT_METHOD,
  REVIEW_MAX_REQUEST_UTF8_BYTES,
  REVIEW_RECORD_APPROVAL_METHOD,
  REVIEW_REQUEST_CHANGES_METHOD,
} from './reviewProtocol'
import {
  createReviewResponseValue,
  reviewApprovalId,
  reviewParentVersionId,
  reviewRequestId,
  reviewSourceSpanId,
  reviewVersionDigest,
  reviewVersionId,
} from './reviewTestFixture'

const outputTime = { value: '1', rate: '2' } as const

describe('review bridge protocol', () => {
  it('creates closed target- and digest-bound requests without identity claims', () => {
    const requests = [
      createReviewGetSnapshotRequest(reviewRequestId, reviewVersionId),
      createReviewAddCommentRequest(
        reviewRequestId,
        reviewVersionId,
        reviewVersionDigest,
        'rightsApprover',
        outputTime,
        '確認コメントです。',
      ),
      createReviewRequestChangesRequest(
        reviewRequestId,
        reviewVersionId,
        reviewVersionDigest,
        'contentReview',
        outputTime,
        '修正してください。',
      ),
      createReviewRecordApprovalRequest(
        reviewRequestId,
        reviewVersionId,
        reviewVersionDigest,
        'contentApprover',
        '内容を承認します。',
      ),
    ]

    expect(requests.map((request) => request.method)).toEqual([
      REVIEW_GET_SNAPSHOT_METHOD,
      REVIEW_ADD_COMMENT_METHOD,
      REVIEW_REQUEST_CHANGES_METHOD,
      REVIEW_RECORD_APPROVAL_METHOD,
    ])
    for (const request of requests) {
      expect(Object.isFrozen(request)).toBe(true)
      expect(Object.isFrozen(request.params)).toBe(true)
      expect(
        new TextEncoder().encode(JSON.stringify(request)).byteLength,
      ).toBeLessThanOrEqual(REVIEW_MAX_REQUEST_UTF8_BYTES)
      expect(JSON.stringify(request)).not.toContain('actorSid')
    }
    expect(requests[1]?.params).toEqual({
      versionId: reviewVersionId,
      versionDigest: reviewVersionDigest,
      actorRole: 'rightsApprover',
      outputTime,
      text: '確認コメントです。',
    })
  })

  it('rejects noncanonical targets, digests, times, text, and excess UTF-8', () => {
    expect(() =>
      createReviewGetSnapshotRequest(
        reviewRequestId,
        `${reviewVersionId.slice(0, -1)}A`,
      ),
    ).toThrow()
    expect(() =>
      createReviewRecordApprovalRequest(
        reviewRequestId,
        reviewVersionId,
        reviewVersionDigest.toUpperCase(),
        'contentApprover',
        '承認します。',
      ),
    ).toThrow()

    for (const candidate of [
      { value: '-1', rate: '1' },
      { value: '01', rate: '1' },
      { value: '1', rate: '0' },
      { value: '2', rate: '4' },
      { value: '9223372036854775808', rate: '1' },
      { value: '1', rate: '9223372036854775808' },
    ]) {
      expect(isReviewOutputTime(candidate)).toBe(false)
      expect(() =>
        createReviewAddCommentRequest(
          reviewRequestId,
          reviewVersionId,
          reviewVersionDigest,
          'contentApprover',
          candidate,
          '確認します。',
        ),
      ).toThrow()
    }

    for (const text of [
      '',
      '   ',
      ' 前後空白',
      '前後空白 ',
      '\ud800',
      'あ'.repeat(5_462),
    ]) {
      expect(isReviewTextInput(text)).toBe(false)
    }
    expect(isReviewTextInput('あ'.repeat(5_461))).toBe(true)
  })

  it('parses and deeply freezes the complete exact snapshot', () => {
    const parsed = parseReviewBridgeResponse(createReviewResponseValue())

    expect(parsed?.method).toBe(REVIEW_GET_SNAPSHOT_METHOD)
    if (parsed === null || !('result' in parsed)) {
      throw new Error('Expected a review snapshot response.')
    }
    expect(parsed.result.versionDigest).toBe(reviewVersionDigest)
    expect(parsed.result.comments[0]?.sourceSpanId).toBe(reviewSourceSpanId)
    expect(parsed.result.comments[0]?.outputTime).toEqual(outputTime)
    expect(parsed.result.approvals.map((approval) => approval.validity)).toEqual([
      'valid',
      'stale',
    ])
    expect(parsed.result.directParentDiff?.fromVersionId).toBe(
      reviewParentVersionId,
    )
    expect(Object.isFrozen(parsed)).toBe(true)
    expect(Object.isFrozen(parsed.result)).toBe(true)
    expect(Object.isFrozen(parsed.result.comments)).toBe(true)
    expect(Object.isFrozen(parsed.result.comments[0]?.outputTime)).toBe(true)
    expect(Object.isFrozen(parsed.result.approvals)).toBe(true)
    expect(Object.isFrozen(parsed.result.directParentDiff?.changes)).toBe(true)
    expect(Object.isFrozen(parsed.result.actions)).toBe(true)
    expect(Object.isFrozen(parsed.result.actions[0]?.actorRoles)).toBe(true)
  })

  it('rejects unknown fields, private identity, and broken semantic bindings', () => {
    const mutations: Array<(value: ReviewResponseValue) => void> = [
      (value) => {
        value.extra = true
      },
      (value) => {
        value.result.extra = true
      },
      (value) => {
        value.result.actorSid = 'S-1-5-21-private'
      },
      (value) => {
        value.result.reviewStateReason = null
      },
      (value) => {
        value.result.comments[0]!.outputTime = { value: '2', rate: '4' }
      },
      (value) => {
        value.result.comments[0]!.text = 'あ'.repeat(5_462)
      },
      (value) => {
        value.result.comments.push({
          ...value.result.comments[0]!,
          id: value.result.comments[0]!.id,
        })
      },
      (value) => {
        value.result.approvals[0]!.versionId = reviewParentVersionId
      },
      (value) => {
        value.result.approvals[1]!.validity = 'valid'
      },
      (value) => {
        value.result.approvals[0]!.reason = 'あ'.repeat(5_462)
      },
      (value) => {
        value.result.approvals.push({
          ...value.result.approvals[0]!,
          id: reviewApprovalId,
        })
      },
      (value) => {
        value.result.directParentDiff!.fromVersionId = reviewVersionId
      },
      (value) => {
        value.result.directParentDiff!.changes[0]!.previousIndex = null
      },
      (value) => {
        value.result.actions.reverse()
      },
      (value) => {
        value.result.actions[1]!.status = 'targetNotCurrent'
      },
      (value) => {
        value.result.actions[0]!.actorRoles.reverse()
      },
      (value) => {
        value.result.actions[2]!.actorRoles = ['editor']
      },
    ]

    for (const mutate of mutations) {
      const value = cloneReviewValue()
      mutate(value)
      expect(parseReviewBridgeResponse(value)).toBeNull()
    }
  })

  it('distinguishes explicit unrecorded and publication-blocked states', () => {
    const unrecorded = cloneReviewValue()
    unrecorded.result.reviewState = null
    unrecorded.result.reviewStateReason = null
    unrecorded.result.reviewStateRecordedAt = null
    unrecorded.result.reviewStateActorRole = null
    unrecorded.result.actions[1]!.status = 'reviewStateUnrecorded'
    unrecorded.result.actions[1]!.actorRoles = []
    expect(parseReviewBridgeResponse(unrecorded)).not.toBeNull()

    const blocked = cloneReviewValue()
    blocked.result.reviewState = 'publicationBlocked'
    blocked.result.actions[1]!.status = 'publicationBlocked'
    blocked.result.actions[1]!.actorRoles = []
    expect(parseReviewBridgeResponse(blocked)).not.toBeNull()

    blocked.result.actions[2]!.status = 'stateNotEligible'
    expect(parseReviewBridgeResponse(blocked)).toBeNull()
  })

  it('rejects state-incompatible request-change and action-role semantics', () => {
    const editingWithReviewActions = cloneReviewValue()
    editingWithReviewActions.result.reviewState = 'editing'
    editingWithReviewActions.result.actions[1]!.status = 'authorized'
    expect(parseReviewBridgeResponse(editingWithReviewActions)).toBeNull()

    const mismatchedRequestRole = cloneReviewValue()
    mismatchedRequestRole.result.actions[1]!.actorRoles = ['rightsApprover']
    expect(parseReviewBridgeResponse(mismatchedRequestRole)).toBeNull()

    const authorizedWithoutRoles = cloneReviewValue()
    authorizedWithoutRoles.result.actions[0]!.actorRoles = []
    expect(parseReviewBridgeResponse(authorizedWithoutRoles)).toBeNull()

    const deniedWithRole = cloneReviewValue()
    deniedWithRole.result.actions[2]!.status = 'alreadyRecorded'
    expect(parseReviewBridgeResponse(deniedWithRole)).toBeNull()
  })

  it('accepts approvals independently of review state', () => {
    const editing = cloneReviewValue()
    editing.result.reviewState = 'editing'
    editing.result.actions[1]!.status = 'stateNotEligible'
    editing.result.actions[1]!.actorRoles = []
    expect(parseReviewBridgeResponse(editing)).not.toBeNull()

    const approved = cloneReviewValue()
    approved.result.reviewState = 'approved'
    approved.result.actions[1]!.status = 'stateNotEligible'
    approved.result.actions[1]!.actorRoles = []
    expect(parseReviewBridgeResponse(approved)).not.toBeNull()

    const qualityControl = cloneReviewValue()
    qualityControl.result.reviewState = 'qualityControl'
    qualityControl.result.actions[1]!.status = 'stateNotEligible'
    qualityControl.result.actions[1]!.actorRoles = []
    expect(parseReviewBridgeResponse(qualityControl)).not.toBeNull()

    const alreadyRecorded = cloneReviewValue()
    alreadyRecorded.result.actions[2]!.status = 'alreadyRecorded'
    alreadyRecorded.result.actions[2]!.actorRoles = []
    expect(parseReviewBridgeResponse(alreadyRecorded)).not.toBeNull()
  })

  it('accepts only closed correlated review errors', () => {
    const response = {
      version: 1,
      requestId: reviewRequestId,
      method: REVIEW_ADD_COMMENT_METHOD,
      error: {
        code: 'review.stale_version',
        message: '対象版は現行版ではありません。レビューを再読み込みしてください。',
      },
    }
    expect(parseReviewBridgeResponse(response)).toEqual(response)
    expect(
      parseReviewBridgeResponse({
        ...response,
        error: { ...response.error, privatePath: 'C:\\private\\source.mov' },
      }),
    ).toBeNull()
    expect(
      parseReviewBridgeResponse({
        ...response,
        error: { code: 'review.secret', message: 'secret' },
      }),
    ).toBeNull()
  })
})

type ReviewResponseValue = ReturnType<typeof cloneReviewValue>

function cloneReviewValue() {
  return JSON.parse(JSON.stringify(createReviewResponseValue())) as {
    version: number
    requestId: string
    method: string
    extra?: boolean
    result: {
      extra?: boolean
      actorSid?: string
      reviewState: string | null
      reviewStateReason: string | null
      reviewStateRecordedAt: string | null
      reviewStateActorRole: string | null
      comments: Array<{
        id: string
        outputTime: { value: string; rate: string }
        sourceSpanId: string
        actorRole: string
        createdAt: string
        text: string
      }>
      approvals: Array<{
        id: string
        versionId: string
        versionDigest: string
        actorRole: string
        approvedAt: string
        reason: string
        validity: string
      }>
      directParentDiff: {
        fromVersionId: string
        toVersionId: string
        changes: Array<{
          kind: string
          elementId: string
          previousIndex: number | null
          currentIndex: number | null
        }>
      } | null
      actions: Array<{ kind: string; status: string; actorRoles: string[] }>
    }
  }
}
