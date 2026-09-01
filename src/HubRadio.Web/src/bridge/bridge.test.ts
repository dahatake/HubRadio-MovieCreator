import { describe, expect, it, vi } from 'vitest'

import {
  createBridgeClient,
  createHostBridgeClient,
  type BridgeMessageEvent,
  type BridgeTransport,
} from './index'
import {
  BRIDGE_PING_METHOD,
  BRIDGE_PROTOCOL_VERSION,
  createPingRequest,
  parsePingResponse,
} from './protocol'
import {
  PROJECT_LIST_METHOD,
  createProjectListRequest,
} from './projectProtocol'
import {
  createPlayerReadRequest,
  createPlayerSeekRequest,
} from '../features/player/playerProtocol'
import {
  createPlayerReadResponseValue,
  createPlayerSeekResponseValue,
  playerEvidenceDigest,
  playerJobId,
} from '../features/player/playerTestFixture'
import {
  TRANSCRIPT_READ_METHOD,
  createTranscriptReadRequest,
} from '../features/transcript/transcriptProtocol'
import {
  createTranscriptResponseValue,
  projectId,
} from '../features/transcript/transcriptTestFixture'
import {
  createTimelineApplyRequest,
  createTimelineReadRequest,
} from '../features/timeline/timelineProtocol'
import {
  createTimelineApplyResponseValue,
  createTimelineReadResponseValue,
  timelineProjectId,
  timelineRequestId,
  timelineSourceElementId,
} from '../features/timeline/timelineTestFixture'
import {
  createReviewAddCommentRequest,
  createReviewGetSnapshotRequest,
  REVIEW_ADD_COMMENT_METHOD,
  REVIEW_GET_SNAPSHOT_METHOD,
} from '../features/review/reviewProtocol'
import {
  createReviewResponseValue,
  reviewSecondRequestId,
  reviewVersionDigest,
  reviewVersionId,
} from '../features/review/reviewTestFixture'
import {
  createThumbnailCancelRequest,
  createThumbnailGenerateRequest,
  createThumbnailReadRequest,
} from '../features/thumbnails/thumbnailProtocol'
import {
  createThumbnailCancelResponseValue,
  createThumbnailGenerateResponseValue,
  createThumbnailReadResponseValue,
  thumbnailEvidenceDigest,
  thumbnailGenerationId,
  thumbnailJobId,
  thumbnailPlayerEvidenceDigest,
  thumbnailTemplateId,
} from '../features/thumbnails/thumbnailTestFixture'

const requestId = '00112233-4455-6677-8899-aabbccddeeff'

describe('web bridge protocol', () => {
  it('creates only the closed allowlisted ping request', () => {
    const request = createPingRequest(requestId)

    expect(request).toEqual({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId,
      method: BRIDGE_PING_METHOD,
      params: {},
    })
    expect(Object.isFrozen(request)).toBe(true)
    expect(Object.isFrozen(request.params)).toBe(true)
  })

  it.each([
    '',
    '00000000-0000-0000-0000-000000000000',
    '00112233-4455-6677-8899-AABBCCDDEEFF',
    `${requestId}\n`,
  ])('rejects invalid request ID %s', (invalidRequestId) => {
    expect(() => createPingRequest(invalidRequestId)).toThrow()
  })

  it('accepts an exact ping response and rejects schema or method changes', () => {
    const response = {
      version: 1,
      requestId,
      method: 'bridge.ping',
      result: { protocolVersion: 1 },
    }

    expect(parsePingResponse(response)).toEqual(response)
    expect(parsePingResponse({ ...response, extra: true })).toBeNull()
    expect(parsePingResponse({ ...response, method: 'project.open' })).toBeNull()
    expect(parsePingResponse({ ...response, result: { protocolVersion: 1, extra: true } })).toBeNull()
    expect(parsePingResponse({ ...response, requestId: requestId.toUpperCase() })).toBeNull()
    expect(parsePingResponse([])).toBeNull()
    expect(parsePingResponse(null)).toBeNull()
  })
})

describe('web bridge client', () => {
  it('wraps the transport without returning it and filters incoming messages', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onPingResponse(listener)

    client.sendPing(requestId)
    expect(transport.posted).toEqual([createPingRequest(requestId)])
    expect(client).not.toBe(transport)

    transport.emit({ method: 'project.open' })
    expect(listener).not.toHaveBeenCalled()

    transport.emit({
      version: 1,
      requestId,
      method: 'bridge.ping',
      result: { protocolVersion: 1 },
    })
    expect(listener).toHaveBeenCalledOnce()
  })

  it('sends only closed player read and evidence-bound seek requests', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)

    client.sendPlayerRead(requestId, playerJobId)
    client.sendPlayerSeek(requestId, playerJobId, playerEvidenceDigest, 2)

    expect(transport.posted).toEqual([
      createPlayerReadRequest(requestId, playerJobId),
      createPlayerSeekRequest(requestId, playerJobId, playerEvidenceDigest, 2),
    ])
  })

  it('routes exact player response methods and ignores unknown or extended input', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const readListener = vi.fn()
    const seekListener = vi.fn()
    client.onPlayerReadResponse(readListener)
    client.onPlayerSeekResponse(seekListener)

    transport.emit(createPlayerReadResponseValue())
    expect(readListener).toHaveBeenCalledOnce()
    expect(seekListener).not.toHaveBeenCalled()

    transport.emit(createPlayerSeekResponseValue())
    expect(readListener).toHaveBeenCalledOnce()
    expect(seekListener).toHaveBeenCalledOnce()

    transport.emit({
      ...createPlayerReadResponseValue(),
      privateException: 'C:\\private\\source.mov',
    })
    transport.emit({
      version: 1,
      requestId,
      method: 'player.delete',
      result: {},
    })
    expect(readListener).toHaveBeenCalledOnce()
    expect(seekListener).toHaveBeenCalledOnce()
  })

  it('routes only a fully correlated review response and prevents double submit', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onReviewResponse(listener)
    const first = createReviewGetSnapshotRequest(requestId, reviewVersionId)
    const second = createReviewAddCommentRequest(
      reviewSecondRequestId,
      reviewVersionId,
      reviewVersionDigest,
      'contentApprover',
      { value: '1', rate: '2' },
      '確認コメントです。',
    )

    client.sendReview(first)
    expect(transport.posted).toEqual([first])
    expect(() => client.sendReview(second)).toThrow('already pending')

    transport.emit(
      createReviewResponseValue(
        reviewSecondRequestId,
        REVIEW_GET_SNAPSHOT_METHOD,
      ),
    )
    transport.emit(
      createReviewResponseValue(requestId, REVIEW_ADD_COMMENT_METHOD),
    )
    transport.emit(createReviewResponseForDifferentVersion(requestId))
    transport.emit({
      ...createReviewResponseValue(requestId, REVIEW_GET_SNAPSHOT_METHOD),
      actorSid: 'S-1-5-21-private',
    })
    expect(listener).not.toHaveBeenCalled()

    const accepted = createReviewResponseValue(
      requestId,
      REVIEW_GET_SNAPSHOT_METHOD,
    )
    transport.emit(accepted)
    expect(listener).toHaveBeenCalledOnce()
    expect(listener).toHaveBeenCalledWith(expect.objectContaining({
      requestId,
      method: REVIEW_GET_SNAPSHOT_METHOD,
    }))

    client.sendReview(second)
    expect(transport.posted).toEqual([first, second])
  })

  it('clears review correlation after abandon and transport failure', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const first = createReviewGetSnapshotRequest(requestId, reviewVersionId)
    const second = createReviewGetSnapshotRequest(
      reviewSecondRequestId,
      reviewVersionId,
    )

    client.sendReview(first)
    client.abandonReviewRequest(requestId)
    client.sendReview(second)
    expect(transport.posted).toEqual([first, second])

    client.abandonReviewRequest(reviewSecondRequestId)
    transport.throwOnPost = true
    expect(() => client.sendReview(first)).toThrow('transport failed')
    transport.throwOnPost = false
    client.sendReview(first)
    expect(transport.posted.at(-1)).toEqual(first)
  })

  it('unsubscribes review listeners independently', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const retained = vi.fn()
    const removed = vi.fn()
    client.onReviewResponse(retained)
    const unsubscribe = client.onReviewResponse(removed)
    const request = createReviewGetSnapshotRequest(requestId, reviewVersionId)
    client.sendReview(request)

    unsubscribe()
    unsubscribe()
    transport.emit(
      createReviewResponseValue(requestId, REVIEW_GET_SNAPSHOT_METHOD),
    )

    expect(retained).toHaveBeenCalledOnce()
    expect(removed).not.toHaveBeenCalled()
  })

  it('sends and routes only strict thumbnail evidence operations', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const readListener = vi.fn()
    const generateListener = vi.fn()
    const cancelListener = vi.fn()
    client.onThumbnailReadResponse(readListener)
    client.onThumbnailGenerateResponse(generateListener)
    client.onThumbnailCancelResponse(cancelListener)
    const params = {
      jobId: thumbnailJobId,
      generationId: thumbnailGenerationId,
      digest: thumbnailEvidenceDigest,
      playerDigest: thumbnailPlayerEvidenceDigest,
      templateId: thumbnailTemplateId,
      proxyFrameSequence: 1,
    } as const

    client.sendThumbnailRead(requestId, thumbnailJobId)
    client.sendThumbnailGenerate(requestId, params)
    client.sendThumbnailCancellation(requestId, thumbnailGenerationId)
    expect(transport.posted).toEqual([
      createThumbnailReadRequest(requestId, thumbnailJobId),
      createThumbnailGenerateRequest(requestId, params),
      createThumbnailCancelRequest(requestId, thumbnailGenerationId),
    ])

    transport.emit(createThumbnailReadResponseValue())
    transport.emit(createThumbnailGenerateResponseValue())
    transport.emit(createThumbnailCancelResponseValue())
    expect(readListener).toHaveBeenCalledOnce()
    expect(generateListener).toHaveBeenCalledOnce()
    expect(cancelListener).toHaveBeenCalledOnce()

    transport.emit({
      ...createThumbnailGenerateResponseValue(),
      actorSid: 'S-1-5-21-private',
    })
    transport.emit({
      version: 1,
      requestId,
      method: 'thumbnail.recommend',
      result: {},
    })
    expect(generateListener).toHaveBeenCalledOnce()
  })

  it('unsubscribes player listeners independently', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const retained = vi.fn()
    const removed = vi.fn()
    client.onPlayerReadResponse(retained)
    const unsubscribe = client.onPlayerReadResponse(removed)

    unsubscribe()
    unsubscribe()
    transport.emit(createPlayerReadResponseValue())

    expect(retained).toHaveBeenCalledOnce()
    expect(removed).not.toHaveBeenCalled()
  })

  it('sends closed transcript requests and routes only strict responses', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    const unsubscribe = client.onTranscriptResponse(listener)
    const request = createTranscriptReadRequest(requestId, projectId)

    client.sendTranscript(request)
    expect(transport.posted).toEqual([request])

    transport.emit({
      ...createTranscriptResponseValue(),
      actorSid: 'S-1-5-18',
    })
    expect(listener).not.toHaveBeenCalled()

    transport.emit(createTranscriptResponseValue())
    expect(listener).toHaveBeenCalledOnce()
    expect(listener.mock.calls[0]?.[0].method).toBe(TRANSCRIPT_READ_METHOD)

    unsubscribe()
    transport.emit(createTranscriptResponseValue())
    expect(listener).toHaveBeenCalledOnce()
  })

  it('sends and routes only correlated timeline methods and project snapshots', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const readListener = vi.fn()
    const applyListener = vi.fn()
    client.onTimelineReadResponse(readListener)
    client.onTimelineApplyResponse(applyListener)
    const read = createTimelineReadRequest(timelineRequestId, timelineProjectId)
    const applyRequestId = '8899aabb-ccdd-4eef-8011-223344556677'
    const apply = createTimelineApplyRequest(
      applyRequestId,
      timelineProjectId,
      3,
      [{ kind: 'keep', elementId: timelineSourceElementId }],
    )

    transport.emit(createTimelineReadResponseValue())
    expect(readListener).not.toHaveBeenCalled()

    client.sendTimelineRead(read)
    expect(() => client.sendTimelineRead(read)).toThrow('already pending')
    const mismatched = createTimelineReadResponseValue()
    mismatched.result.snapshot.projectId =
      'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    transport.emit(mismatched)
    expect(readListener).not.toHaveBeenCalled()

    transport.emit(createTimelineReadResponseValue())
    expect(readListener).toHaveBeenCalledOnce()
    expect(applyListener).not.toHaveBeenCalled()

    client.sendTimelineApply(apply)
    const applyResponse = createTimelineApplyResponseValue()
    applyResponse.requestId = applyRequestId
    transport.emit(applyResponse)
    expect(applyListener).toHaveBeenCalledOnce()
    expect(transport.posted).toEqual([read, apply])
  })

  it('abandons timeline correlation and rejects closed extra response fields', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onTimelineReadResponse(listener)
    const read = createTimelineReadRequest(timelineRequestId, timelineProjectId)

    client.sendTimelineRead(read)
    client.abandonTimelineRequest(timelineRequestId)
    transport.emit(createTimelineReadResponseValue())
    expect(listener).not.toHaveBeenCalled()

    client.sendTimelineRead(read)
    transport.emit({ ...createTimelineReadResponseValue(), command: 'calc.exe' })
    expect(listener).not.toHaveBeenCalled()
    transport.emit(createTimelineReadResponseValue())
    expect(listener).toHaveBeenCalledOnce()
  })

  it('unsubscribes listeners and removes the transport handler on disposal', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    const unsubscribe = client.onPingResponse(listener)
    unsubscribe()

    transport.emit({
      version: 1,
      requestId,
      method: 'bridge.ping',
      result: { protocolVersion: 1 },
    })
    expect(listener).not.toHaveBeenCalled()

    client.dispose()
    client.dispose()
    expect(transport.listenerCount).toBe(0)
    expect(() => client.sendPing(requestId)).toThrow('disposed')
    expect(() => client.onPingResponse(listener)).toThrow('disposed')
    expect(() => client.sendPlayerRead(requestId, playerJobId)).toThrow('disposed')
    expect(() =>
      client.sendPlayerSeek(requestId, playerJobId, playerEvidenceDigest, 0),
    ).toThrow('disposed')
    expect(() => client.onPlayerReadResponse(listener)).toThrow('disposed')
    expect(() => client.onPlayerSeekResponse(listener)).toThrow('disposed')
    expect(() =>
      client.sendTranscript(createTranscriptReadRequest(requestId, projectId)),
    ).toThrow('disposed')
    expect(() => client.onTranscriptResponse(listener)).toThrow('disposed')
    expect(() =>
      client.sendTimelineRead(
        createTimelineReadRequest(timelineRequestId, timelineProjectId),
      ),
    ).toThrow('disposed')
    expect(() => client.onTimelineReadResponse(listener)).toThrow('disposed')
    expect(() => client.onTimelineApplyResponse(listener)).toThrow('disposed')
    expect(() => client.abandonTimelineRequest(timelineRequestId)).toThrow(
      'disposed',
    )
    const reviewRequest = createReviewGetSnapshotRequest(
      requestId,
      reviewVersionId,
    )
    expect(() => client.sendReview(reviewRequest)).toThrow('disposed')
    expect(() => client.abandonReviewRequest(requestId)).toThrow('disposed')
    expect(() => client.onReviewResponse(listener)).toThrow('disposed')
    expect(() => client.sendThumbnailRead(requestId, thumbnailJobId)).toThrow(
      'disposed',
    )
    expect(() =>
      client.sendThumbnailGenerate(requestId, {
        jobId: thumbnailJobId,
        generationId: thumbnailGenerationId,
        digest: thumbnailEvidenceDigest,
        playerDigest: thumbnailPlayerEvidenceDigest,
        templateId: thumbnailTemplateId,
        proxyFrameSequence: 1,
      }),
    ).toThrow('disposed')
    expect(() =>
      client.sendThumbnailCancellation(requestId, thumbnailGenerationId),
    ).toThrow('disposed')
    expect(() => client.onThumbnailReadResponse(listener)).toThrow('disposed')
    expect(() => client.onThumbnailGenerateResponse(listener)).toThrow('disposed')
    expect(() => client.onThumbnailCancelResponse(listener)).toThrow('disposed')
  })

  it('sends and emits only strict project protocol messages', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onProjectResponse(listener)
    const request = createProjectListRequest(requestId)

    client.sendProject(request)
    expect(transport.posted).toEqual([request])

    transport.emit({
      version: 1,
      requestId,
      method: PROJECT_LIST_METHOD,
      result: {
        projects: [],
        catalog: { outputProfiles: [], brandTemplates: [] },
        canCreate: true,
        extra: true,
      },
    })
    expect(listener).not.toHaveBeenCalled()

    transport.emit({
      version: 1,
      requestId,
      method: PROJECT_LIST_METHOD,
      result: {
        projects: [],
        catalog: { outputProfiles: [], brandTemplates: [] },
        canCreate: true,
      },
    })
    expect(listener).toHaveBeenCalledOnce()

    client.dispose()
    expect(() => client.sendProject(request)).toThrow('disposed')
    expect(() => client.onProjectResponse(listener)).toThrow('disposed')
  })

  it('does not fabricate a host transport outside WebView2', () => {
    expect(() => createHostBridgeClient()).toThrow('outside a browser window')
  })
})

class FakeTransport implements BridgeTransport {
  readonly posted: unknown[] = []
  throwOnPost = false
  private readonly listeners = new Set<(event: BridgeMessageEvent) => void>()

  get listenerCount(): number {
    return this.listeners.size
  }

  postMessage(message: Parameters<BridgeTransport['postMessage']>[0]): void {
    if (this.throwOnPost) {
      throw new Error('transport failed')
    }
    this.posted.push(message)
  }

  addEventListener(
    type: 'message',
    listener: (event: BridgeMessageEvent) => void,
  ): void {
    expect(type).toBe('message')
    this.listeners.add(listener)
  }

  removeEventListener(
    type: 'message',
    listener: (event: BridgeMessageEvent) => void,
  ): void {
    expect(type).toBe('message')
    this.listeners.delete(listener)
  }

  emit(data: unknown): void {
    for (const listener of this.listeners) {
      listener({ data })
    }
  }
}

function createReviewResponseForDifferentVersion(requestId: string) {
  const value = createReviewResponseValue(
    requestId,
    REVIEW_GET_SNAPSHOT_METHOD,
  )
  value.result.versionId = reviewSecondRequestId
  value.result.approvals[0]!.versionId = reviewSecondRequestId
  value.result.directParentDiff.toVersionId = reviewSecondRequestId
  return value
}
