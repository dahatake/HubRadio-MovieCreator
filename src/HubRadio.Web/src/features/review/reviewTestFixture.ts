import {
  REVIEW_GET_SNAPSHOT_METHOD,
  type ReviewActorRole,
  type ReviewBridgeMethod,
  type ReviewState,
} from './reviewProtocol'

export const reviewRequestId = '00112233-4455-6677-8899-aabbccddeeff'
export const reviewSecondRequestId = '10112233-4455-6677-8899-aabbccddeeff'
export const reviewVersionId = '11112222-3333-4444-8555-666677778888'
export const reviewParentVersionId = '21112222-3333-4444-8555-666677778888'
export const reviewCommentId = '31112222-3333-4444-8555-666677778888'
export const reviewSourceSpanId = '41112222-3333-4444-8555-666677778888'
export const reviewApprovalId = '51112222-3333-4444-8555-666677778888'
export const reviewStaleApprovalId = '61112222-3333-4444-8555-666677778888'
export const reviewElementId = '71112222-3333-4444-8555-666677778888'
export const reviewVersionDigest = 'a'.repeat(64)
export const reviewParentDigest = 'b'.repeat(64)
export const reviewTimestamp = '2036-07-08T09:10:11.0000000+00:00'

export function createReviewResponseValue(
  requestId = reviewRequestId,
  method: ReviewBridgeMethod = REVIEW_GET_SNAPSHOT_METHOD,
) {
  return {
    version: 1,
    requestId,
    method,
    result: {
      versionId: reviewVersionId,
      versionDigest: reviewVersionDigest,
      createdAt: reviewTimestamp,
      parentVersionId: reviewParentVersionId,
      isCurrentTarget: true,
      reviewState: 'contentReview' as ReviewState | null,
      reviewStateReason: '内容レビューを開始しました。' as string | null,
      reviewStateRecordedAt: reviewTimestamp as string | null,
      reviewStateActorRole: 'contentApprover' as ReviewActorRole | null,
      comments: [
        {
          id: reviewCommentId,
          outputTime: { value: '1', rate: '2' },
          sourceSpanId: reviewSourceSpanId,
          actorRole: 'contentApprover',
          createdAt: reviewTimestamp,
          text: '字幕の語尾を確認してください。',
        },
      ],
      approvals: [
        {
          id: reviewApprovalId,
          versionId: reviewVersionId,
          versionDigest: reviewVersionDigest,
          actorRole: 'contentApprover',
          approvedAt: reviewTimestamp,
          reason: '現在版の内容を承認します。',
          validity: 'valid',
        },
        {
          id: reviewStaleApprovalId,
          versionId: reviewParentVersionId,
          versionDigest: reviewParentDigest,
          actorRole: 'rightsApprover',
          approvedAt: reviewTimestamp,
          reason: '親版に対する承認です。',
          validity: 'stale',
        },
      ],
      directParentDiff: {
        fromVersionId: reviewParentVersionId,
        toVersionId: reviewVersionId,
        changes: [
          {
            kind: 'modified',
            elementId: reviewElementId,
            previousIndex: 0,
            currentIndex: 0,
          },
        ],
      },
      actions: [
        {
          kind: 'addComment',
          status: 'authorized',
          actorRoles: ['contentApprover', 'rightsApprover'],
        },
        {
          kind: 'requestChanges',
          status: 'authorized',
          actorRoles: ['contentApprover'],
        },
        {
          kind: 'recordApproval',
          status: 'authorized',
          actorRoles: ['contentApprover', 'rightsApprover'],
        },
      ],
    },
  }
}
