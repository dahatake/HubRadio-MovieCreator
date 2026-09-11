import { describe, expect, expectTypeOf, it, vi } from 'vitest'

import {
  createBridgeClient,
  createHostBridgeClient,
  type BridgeClient,
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
  createComparisonReadRequest,
} from '../features/compare/compareProtocol'
import {
  comparisonLeftDigest,
  comparisonLeftVersionId,
  comparisonRequestId,
  comparisonRightDigest,
  comparisonRightVersionId,
  comparisonSecondRequestId,
  createComparisonResponseValue,
} from '../features/compare/compareTestFixture'
import {
  createDeliveryReadRequest,
} from '../features/delivery/deliveryProtocol'
import {
  createDeliveryResponseValue,
  deliverySecondRequestId,
  deliverySecondVersionId,
  deliveryVersionId,
} from '../features/delivery/deliveryTestFixture'
import {
  LAYOUT_APPLY_METHOD,
  createLayoutApplyRequest,
  createLayoutReadRequest,
} from '../features/layout/layoutProtocol'
import {
  createLayoutResponseValue,
  layoutCueDigest,
  layoutCueId,
  layoutCueRevisionId,
  layoutProjectId,
  layoutRequestId,
  layoutSecondProjectId,
  layoutSecondRequestId,
} from '../features/layout/layoutTestFixture'
import {
  SETTINGS_METHODS,
  SETTINGS_WORKSPACE_METHODS,
  createSettingsReadRequest,
  createSettingsUpdateRequest,
  createSettingsResetRequest,
  createSettingsResetAllRequest,
  createSettingsGlobalReadRequest,
  createSettingsGlobalUpdateRequest,
  createSettingsGlobalResetRequest,
  createSettingsGlobalResetAllRequest,
  createSettingsFileExportRequest,
  createSettingsFilePreviewRequest,
  createSettingsFileApplyRequest,
  parseSettingsWorkspaceResponse,
  type AnySettingsSnapshot,
  type GlobalSettingsSnapshot,
  type SettingsRequest,
  type SettingsResponse,
  type SettingsSnapshot,
  type SettingsWorkspaceRequest,
  type SettingsWorkspaceResponse,
} from '../features/settings/settingsProtocol'
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

  it('routes only the fully correlated four-part comparison target', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onComparisonResponse(listener)
    const first = createComparisonReadRequest(
      comparisonRequestId,
      comparisonLeftVersionId,
      comparisonLeftDigest,
      comparisonRightVersionId,
      comparisonRightDigest,
    )
    const second = createComparisonReadRequest(
      comparisonSecondRequestId,
      comparisonLeftVersionId,
      comparisonLeftDigest,
      comparisonRightVersionId,
      comparisonRightDigest,
    )

    client.sendComparison(first)
    expect(transport.posted).toEqual([first])
    expect(() => client.sendComparison(second)).toThrow('already pending')

    transport.emit(createComparisonResponseValue(comparisonSecondRequestId))
    const wrongVersion = createComparisonResponseValue(comparisonRequestId)
    wrongVersion.result.comparison.left.versionId = comparisonSecondRequestId
    transport.emit(wrongVersion)
    const wrongDigest = createComparisonResponseValue(comparisonRequestId)
    wrongDigest.result.comparison.right.versionDigest = 'c'.repeat(64)
    transport.emit(wrongDigest)
    transport.emit({
      ...createComparisonResponseValue(comparisonRequestId),
      actorSid: 'S-1-5-21-private',
    })
    expect(listener).not.toHaveBeenCalled()

    const accepted = createComparisonResponseValue(comparisonRequestId)
    transport.emit(accepted)
    expect(listener).toHaveBeenCalledOnce()
    expect(listener).toHaveBeenCalledWith(expect.objectContaining({
      requestId: comparisonRequestId,
      method: 'compare.read',
    }))
    client.sendComparison(second)
    expect(transport.posted).toEqual([first, second])
  })

  it('clears comparison correlation after abandon and transport failure', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const first = createComparisonReadRequest(
      comparisonRequestId,
      comparisonLeftVersionId,
      comparisonLeftDigest,
      comparisonRightVersionId,
      comparisonRightDigest,
    )
    const second = createComparisonReadRequest(
      comparisonSecondRequestId,
      comparisonLeftVersionId,
      comparisonLeftDigest,
      comparisonRightVersionId,
      comparisonRightDigest,
    )

    client.sendComparison(first)
    client.abandonComparisonRequest(comparisonRequestId)
    client.sendComparison(second)
    expect(transport.posted).toEqual([first, second])

    client.abandonComparisonRequest(comparisonSecondRequestId)
    transport.throwOnPost = true
    expect(() => client.sendComparison(first)).toThrow('transport failed')
    transport.throwOnPost = false
    client.sendComparison(first)
    expect(transport.posted.at(-1)).toEqual(first)
  })

  it('routes only a fully correlated delivery response and prevents double submit', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onDeliveryResponse(listener)
    const first = createDeliveryReadRequest(
      requestId,
      deliveryVersionId,
      null,
    )
    const second = createDeliveryReadRequest(
      deliverySecondRequestId,
      deliveryVersionId,
      { value: '1', rate: '2' },
    )

    client.sendDelivery(first)
    expect(transport.posted).toEqual([first])
    expect(() => client.sendDelivery(second)).toThrow('already pending')

    transport.emit(createDeliveryResponseValue(deliverySecondRequestId, false))
    transport.emit(createDeliveryResponseForDifferentVersion(requestId, false))
    transport.emit(createDeliveryResponseValue(requestId, true))
    transport.emit({
      ...createDeliveryResponseValue(requestId, false),
      storageReference: 'C:\\private\\master.mp4',
    })
    expect(listener).not.toHaveBeenCalled()

    transport.emit(createDeliveryResponseValue(requestId, false))
    expect(listener).toHaveBeenCalledOnce()
    expect(listener).toHaveBeenCalledWith(expect.objectContaining({
      requestId,
      method: 'delivery.read',
    }))

    client.sendDelivery(second)
    const wrongTime = createDeliveryResponseValue(deliverySecondRequestId, true)
    if (wrongTime.result.trace === null) {
      throw new Error('Expected delivery trace fixture.')
    }
    wrongTime.result.trace.outputTime = { value: '3', rate: '4' }
    transport.emit(wrongTime)
    expect(listener).toHaveBeenCalledOnce()
    transport.emit(createDeliveryResponseValue(deliverySecondRequestId, true))
    expect(listener).toHaveBeenCalledTimes(2)
  })

  it('clears delivery correlation after abandon and transport failure', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const first = createDeliveryReadRequest(
      requestId,
      deliveryVersionId,
      null,
    )
    const second = createDeliveryReadRequest(
      deliverySecondRequestId,
      deliveryVersionId,
      null,
    )

    client.sendDelivery(first)
    client.abandonDeliveryRequest(requestId)
    client.sendDelivery(second)
    expect(transport.posted).toEqual([first, second])

    client.abandonDeliveryRequest(deliverySecondRequestId)
    transport.throwOnPost = true
    expect(() => client.sendDelivery(first)).toThrow('transport failed')
    transport.throwOnPost = false
    client.sendDelivery(first)
    expect(transport.posted.at(-1)).toEqual(first)
  })

  it('routes only fully method- and project-correlated layout responses', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onLayoutResponse(listener)
    const read = createLayoutReadRequest(
      layoutRequestId,
      layoutProjectId,
      null,
    )
    const apply = createLayoutApplyRequest(
      layoutSecondRequestId,
      layoutProjectId,
      3,
      {
        kind: 'editSubtitle',
        cueId: layoutCueId,
        expectedRevisionId: layoutCueRevisionId,
        expectedRevisionDigest: layoutCueDigest,
        edit: { kind: 'text', text: '更新字幕' },
        reason: '字幕を修正します。',
      },
    )

    client.sendLayout(read)
    expect(transport.posted).toEqual([read])
    expect(() => client.sendLayout(read)).toThrow('already pending')

    transport.emit(createLayoutResponseValue(layoutSecondRequestId))
    transport.emit(createLayoutResponseValue(layoutRequestId, LAYOUT_APPLY_METHOD))
    const wrongProject = createLayoutResponseValue(layoutRequestId)
    wrongProject.result.snapshot.projectId = layoutSecondProjectId
    transport.emit(wrongProject)
    transport.emit({
      ...createLayoutResponseValue(layoutRequestId),
      sourcePath: 'C:\\private\\source.mov',
    })
    expect(listener).not.toHaveBeenCalled()

    transport.emit(createLayoutResponseValue(layoutRequestId))
    expect(listener).toHaveBeenCalledOnce()
    expect(listener).toHaveBeenCalledWith(expect.objectContaining({
      requestId: layoutRequestId,
      method: 'layout.read',
    }))

    client.sendLayout(apply)
    transport.emit(createLayoutResponseValue(
      layoutSecondRequestId,
      LAYOUT_APPLY_METHOD,
    ))
    expect(listener).toHaveBeenCalledTimes(2)
    expect(transport.posted).toEqual([read, apply])
  })

  it('clears layout correlation after abandon and transport failure', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const first = createLayoutReadRequest(
      layoutRequestId,
      layoutProjectId,
      null,
    )
    const second = createLayoutReadRequest(
      layoutSecondRequestId,
      layoutProjectId,
      null,
    )

    client.sendLayout(first)
    client.abandonLayoutRequest(layoutRequestId)
    client.sendLayout(second)
    expect(transport.posted).toEqual([first, second])

    client.abandonLayoutRequest(layoutSecondRequestId)
    transport.throwOnPost = true
    expect(() => client.sendLayout(first)).toThrow('transport failed')
    transport.throwOnPost = false
    client.sendLayout(first)
    expect(transport.posted.at(-1)).toEqual(first)
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
    const comparisonRequest = createComparisonReadRequest(
      comparisonRequestId,
      comparisonLeftVersionId,
      comparisonLeftDigest,
      comparisonRightVersionId,
      comparisonRightDigest,
    )
    expect(() => client.sendComparison(comparisonRequest)).toThrow('disposed')
    expect(() => client.abandonComparisonRequest(comparisonRequestId)).toThrow(
      'disposed',
    )
    expect(() => client.onComparisonResponse(listener)).toThrow('disposed')
    const deliveryRequest = createDeliveryReadRequest(
      requestId,
      deliveryVersionId,
      null,
    )
    expect(() => client.sendDelivery(deliveryRequest)).toThrow('disposed')
    expect(() => client.abandonDeliveryRequest(requestId)).toThrow('disposed')
    expect(() => client.onDeliveryResponse(listener)).toThrow('disposed')
    const layoutRequest = createLayoutReadRequest(
      layoutRequestId,
      layoutProjectId,
      null,
    )
    expect(() => client.sendLayout(layoutRequest)).toThrow('disposed')
    expect(() => client.abandonLayoutRequest(layoutRequestId)).toThrow(
      'disposed',
    )
    expect(() => client.onLayoutResponse(listener)).toThrow('disposed')
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

describe('settings workspace bridge client', () => {
  const secondRequestId = '22334455-6677-4899-8aab-bbccddeeff00'
  const otherProjectId = '99999999-8888-4777-8666-555555555555'
  const key = 'subtitles.enabled'
  const enabled = { kind: 'boolean', booleanValue: true } as const
  const disabled = { kind: 'boolean', booleanValue: false } as const
  const fileSha256 = 'd'.repeat(64)
  const previousFileSha256 = 'e'.repeat(64)
  const projectSnapshot: SettingsSnapshot = {
    projectId: '11112222-3333-4444-8555-666677778888',
    profileId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    profileName: 'Bridge settings',
    profileVersion: '1.0.0',
    profileDigest: 'f'.repeat(64),
    initialSnapshotDigest: 'a'.repeat(64),
    snapshotDigest: 'b'.repeat(64),
    revision: 3,
    updatedAt: '2026-09-07T00:00:00.0000000+00:00',
    definitionCount: 1,
    definitions: [{
      key,
      group: 'subtitlesAndGraphics',
      isAdvanced: false,
      displayName: 'Subtitles',
      description: 'Enable subtitles.',
      unit: 'none',
      defaultValue: enabled,
      minimum: null,
      maximum: null,
      allowedTextValues: [],
      evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }],
    }],
    initialValues: [{ key, value: enabled }],
    values: [{ key, value: disabled }],
  }
  const globalSnapshot: GlobalSettingsSnapshot = {
    ...projectSnapshot,
    projectId: null,
    revision: 7,
  }
  const targets: readonly AnySettingsSnapshot[] = [projectSnapshot, globalSnapshot]
  const scopes = targets.map(target => ({
    target,
    scope: target.projectId === null ? 'global' : 'project',
  }))
  const cases = scopes.flatMap(({ target, scope }) =>
    workspaceRequests(target).map(request => ({
      method: request.method,
      request,
      target,
      scope,
    })),
  )

  function projectRequests(target: SettingsSnapshot): readonly SettingsRequest[] {
    return [
      createSettingsReadRequest(requestId, target.projectId),
      createSettingsUpdateRequest(requestId, target, key, enabled),
      createSettingsResetRequest(requestId, target, key),
      createSettingsResetAllRequest(requestId, target),
    ]
  }

  function workspaceRequests(target: AnySettingsSnapshot): readonly SettingsWorkspaceRequest[] {
    const requests = target.projectId === null
      ? [
          createSettingsGlobalReadRequest(requestId),
          createSettingsGlobalUpdateRequest(requestId, target, key, enabled),
          createSettingsGlobalResetRequest(requestId, target, key),
          createSettingsGlobalResetAllRequest(requestId, target),
        ]
      : projectRequests(target)
    return [
      ...requests,
      createSettingsFileExportRequest(requestId, target, null),
      createSettingsFilePreviewRequest(requestId, target),
      createSettingsFileApplyRequest(requestId, target, fileSha256),
    ]
  }

  function responseSnapshot(
    request: SettingsWorkspaceRequest,
    target: AnySettingsSnapshot,
  ): AnySettingsSnapshot {
    if (
      request.method === 'settings.read' ||
      request.method === 'settings.global.read' ||
      request.method === 'settings.file.export'
    ) {
      return target
    }
    return {
      ...target,
      revision: request.method === 'settings.file.preview'
        ? target.revision
        : target.revision + 1,
      snapshotDigest: target.initialSnapshotDigest,
      values: target.initialValues,
    }
  }

  function available(request: SettingsWorkspaceRequest, snapshot: AnySettingsSnapshot) {
    const result = { status: 'available' as const, snapshot }
    return {
      version: 1 as const,
      requestId: request.requestId,
      method: request.method,
      result: request.method.startsWith('settings.file.')
        ? { ...result, fileSha256 }
        : result,
    }
  }

  it('exposes additive workspace signatures and transports all 11 actual request types', () => {
    expect(new Set(cases.map(item => item.method))).toEqual(new Set(SETTINGS_WORKSPACE_METHODS))
    expect(new Set(cases.map(item => item.method)).size).toBe(11)
    expectTypeOf<BridgeClient['sendSettings']>().toEqualTypeOf<(request: SettingsRequest) => void>()
    expectTypeOf<BridgeClient['onSettingsResponse']>().toEqualTypeOf<(listener: (response: SettingsResponse) => void) => () => void>()
    expectTypeOf<BridgeClient['sendSettingsWorkspace']>().toEqualTypeOf<(request: SettingsWorkspaceRequest) => void>()
    expectTypeOf<BridgeClient['onSettingsWorkspaceResponse']>().toEqualTypeOf<(listener: (response: SettingsWorkspaceResponse) => void) => () => void>()
    expectTypeOf<BridgeClient['abandonSettingsWorkspaceRequest']>().toEqualTypeOf<(requestId: string) => void>()
    expectTypeOf<BridgeClient['abandonSettingsRequest']>().toEqualTypeOf<(requestId: string) => void>()
    expectTypeOf<Extract<Parameters<BridgeTransport['postMessage']>[0], { method: SettingsWorkspaceRequest['method'] }>>().toEqualTypeOf<SettingsWorkspaceRequest>()
    expectTypeOf<import('./index').SettingsWorkspaceRequest>().toEqualTypeOf<SettingsWorkspaceRequest>()
    expectTypeOf<import('./index').SettingsWorkspaceResponse>().toEqualTypeOf<SettingsWorkspaceResponse>()
  })

  it.each(cases)('round-trips $method ($scope) once without leaking to the legacy listener', ({ request, target }) => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const workspaceListener = vi.fn()
    const legacyListener = vi.fn()
    client.onSettingsWorkspaceResponse(workspaceListener)
    client.onSettingsResponse(legacyListener)

    client.sendSettingsWorkspace(request)
    expect(transport.posted).toEqual([request])
    expect(transport.posted[0]).toBe(request)
    const response = available(request, responseSnapshot(request, target))
    const wire: unknown = JSON.parse(JSON.stringify(response))
    expect(parseSettingsWorkspaceResponse(wire)).not.toBeNull()
    transport.emit(wire)
    transport.emit(wire)

    expect(workspaceListener).toHaveBeenCalledExactlyOnceWith(response)
    expect(legacyListener).toHaveBeenCalledTimes(
      SETTINGS_METHODS.some(method => method === request.method) ? 1 : 0,
    )
  })

  it.each(projectRequests(projectSnapshot))('preserves legacy $method and also notifies workspace subscribers once', request => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const legacyListener = vi.fn()
    const workspaceListener = vi.fn()
    client.onSettingsResponse(legacyListener)
    client.onSettingsWorkspaceResponse(workspaceListener)
    client.sendSettings(request)
    const response = available(request, responseSnapshot(request, projectSnapshot))

    transport.emit(response)
    transport.emit(response)

    expect(transport.posted).toEqual([request])
    expect(legacyListener).toHaveBeenCalledExactlyOnceWith(response)
    expect(workspaceListener).toHaveBeenCalledExactlyOnceWith(response)
  })

  it.each(cases)('retains $method ($scope) pending after stale IDs, wrong methods/scopes, and invalid input', ({ request, target }) => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    const legacyListener = vi.fn()
    client.onSettingsWorkspaceResponse(listener)
    client.onSettingsResponse(legacyListener)
    client.sendSettingsWorkspace(request)
    const snapshot = responseSnapshot(request, target)
    const response = available(request, snapshot)
    const wrongMethod = request.method === 'settings.global.read'
      ? available(createSettingsReadRequest(requestId, projectSnapshot.projectId), projectSnapshot)
      : available(createSettingsGlobalReadRequest(requestId), globalSnapshot)
    const wrongScope = target.projectId === null ? otherProjectId : null

    transport.emit({ ...response, requestId: secondRequestId })
    transport.emit(wrongMethod)
    transport.emit({ ...response, method: 'settings.file.delete' })
    transport.emit({ ...response, unexpected: true })
    transport.emit(available(request, { ...snapshot, projectId: wrongScope }))
    transport.emit(available(request, { ...snapshot, projectId: otherProjectId }))
    transport.emit(available(request, { ...snapshot, profileVersion: '01.0.0' }))
    expect(listener).not.toHaveBeenCalled()
    expect(legacyListener).not.toHaveBeenCalled()
    const next = { ...request, requestId: secondRequestId }
    expect(() => client.sendSettingsWorkspace(next)).toThrow('already pending')

    transport.emit(response)
    expect(listener).toHaveBeenCalledExactlyOnceWith(response)
    client.sendSettingsWorkspace(next)
    transport.emit(response)
    expect(listener).toHaveBeenCalledOnce()
    expect(transport.posted).toEqual([request, next])
  })

  it.each(cases.filter(({ request }) =>
    request.method !== 'settings.read' && request.method !== 'settings.global.read',
  ))('pins $method ($scope) to the expected profile and revision, not just the project', ({ request, target }) => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onSettingsWorkspaceResponse(listener)
    client.sendSettingsWorkspace(request)
    const snapshot = responseSnapshot(request, target)
    const mismatches: readonly Partial<SettingsSnapshot>[] = [
      { profileId: otherProjectId },
      { profileVersion: '2.0.0' },
      { profileDigest: '9'.repeat(64) },
      { revision: target.revision - 1 },
      { revision: target.revision + 2 },
    ]
    for (const mismatch of mismatches) {
      const response = available(request, { ...snapshot, ...mismatch })
      expect(parseSettingsWorkspaceResponse(response)).not.toBeNull()
      transport.emit(response)
    }
    expect(listener).not.toHaveBeenCalled()

    const response = available(request, snapshot)
    transport.emit(response)
    expect(listener).toHaveBeenCalledExactlyOnceWith(response)
  })

  it.each(scopes)('accepts a changed preview digest at current revision zero for $scope, then applies against saved identity', ({ target }) => {
    const saved = {
      ...target,
      revision: 0,
      snapshotDigest: target.initialSnapshotDigest,
      values: target.initialValues,
    }
    const candidate = {
      ...saved,
      snapshotDigest: 'b'.repeat(64),
      values: [{ key, value: disabled }],
    }
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onSettingsWorkspaceResponse(listener)
    const preview = createSettingsFilePreviewRequest(requestId, saved)
    client.sendSettingsWorkspace(preview)
    const advanced = available(preview, { ...candidate, revision: 1 })
    expect(parseSettingsWorkspaceResponse(advanced)).not.toBeNull()
    transport.emit(advanced)
    expect(listener).not.toHaveBeenCalled()

    const response = available(preview, candidate)
    transport.emit(response)
    expect(listener).toHaveBeenCalledExactlyOnceWith(response)
    expect(candidate.snapshotDigest).not.toBe(saved.snapshotDigest)
    expect(candidate.initialValues).toEqual(saved.initialValues)
    const apply = createSettingsFileApplyRequest(secondRequestId, saved, fileSha256)
    expect(apply.params.expectedRevision).toBe(0)
    expect(apply.params.expectedSnapshotDigest).toBe(saved.snapshotDigest)
    expect(apply.params.expectedSnapshotDigest).not.toBe(candidate.snapshotDigest)
    client.sendSettingsWorkspace(apply)
    transport.emit(available(apply, { ...candidate, revision: 1 }))
    expect(listener).toHaveBeenCalledTimes(2)
  })

  it.each(scopes)('accepts a $scope update no-op only with the unchanged saved digest', ({ target }) => {
    const request = target.projectId === null
      ? createSettingsGlobalUpdateRequest(requestId, target, key, disabled)
      : createSettingsUpdateRequest(requestId, target, key, disabled)
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onSettingsWorkspaceResponse(listener)
    client.sendSettingsWorkspace(request)

    transport.emit(available(request, {
      ...target,
      snapshotDigest: target.initialSnapshotDigest,
      values: target.initialValues,
    }))
    expect(listener).not.toHaveBeenCalled()
    const response = available(request, target)
    transport.emit(response)
    expect(listener).toHaveBeenCalledExactlyOnceWith(response)
  })

  it.each(scopes)('exports the saved $scope snapshot and accepts a new file hash, not the overwrite hash', ({ target }) => {
    const request = createSettingsFileExportRequest(requestId, target, previousFileSha256)
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onSettingsWorkspaceResponse(listener)
    client.sendSettingsWorkspace(request)

    transport.emit(available(request, { ...target, snapshotDigest: target.initialSnapshotDigest }))
    transport.emit(available(request, { ...target, revision: target.revision + 1 }))
    expect(listener).not.toHaveBeenCalled()
    const response = available(request, target)
    transport.emit(response)
    expect(listener).toHaveBeenCalledExactlyOnceWith(response)
    expect(fileSha256).not.toBe(request.params.expectedFileSha256)
    expect(fileSha256).not.toBe(target.snapshotDigest)
  })

  it.each(scopes)('requires the preview file hash on a successful $scope apply without equating it to a snapshot digest', ({ target }) => {
    const request = createSettingsFileApplyRequest(requestId, target, fileSha256)
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onSettingsWorkspaceResponse(listener)
    client.sendSettingsWorkspace(request)
    const snapshot = responseSnapshot(request, target)
    const response = available(request, snapshot)

    transport.emit({ ...response, result: { ...response.result, fileSha256: previousFileSha256 } })
    transport.emit(available(request, { ...snapshot, revision: target.revision }))
    expect(listener).not.toHaveBeenCalled()
    transport.emit(response)
    expect(listener).toHaveBeenCalledExactlyOnceWith(response)
    expect(snapshot.snapshotDigest).not.toBe(target.snapshotDigest)
    expect(fileSha256).not.toBe(snapshot.snapshotDigest)
  })

  it.each(cases)('delivers strict $method ($scope) failures once and releases pending, including overwriteRequired hashes', ({ request }) => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    const legacyListener = vi.fn()
    client.onSettingsWorkspaceResponse(listener)
    client.onSettingsResponse(legacyListener)
    const file = request.method.startsWith('settings.file.')
    const commonStatuses = ['forbidden', 'notFound', 'unbound', 'conflict', 'invalid', 'unavailable']
    const statuses = file
      ? [...commonStatuses, 'fileNotFound', 'profileMismatch', 'snapshotMismatch', 'overwriteRequired']
      : commonStatuses

    for (const [index, status] of statuses.entries()) {
      client.sendSettingsWorkspace(request)
      const result = file
        ? { status, snapshot: null, fileSha256: status === 'overwriteRequired' ? previousFileSha256 : null }
        : { status, snapshot: null }
      const response = { version: 1, requestId: request.requestId, method: request.method, result }
      expect(parseSettingsWorkspaceResponse(response)).not.toBeNull()
      transport.emit({ ...response, requestId: secondRequestId })
      if (file) {
        transport.emit({
          ...response,
          result: { ...result, fileSha256: status === 'overwriteRequired' ? null : fileSha256 },
        })
      }
      expect(listener).toHaveBeenCalledTimes(index)
      transport.emit(response)
      transport.emit(response)
      expect(listener).toHaveBeenCalledTimes(index + 1)
      expect(listener).toHaveBeenLastCalledWith(response)
    }
    expect(legacyListener).toHaveBeenCalledTimes(
      SETTINGS_METHODS.some(method => method === request.method) ? statuses.length : 0,
    )
  })

  it.each(['abandonSettingsRequest', 'abandonSettingsWorkspaceRequest'] as const)('shares pending across both send APIs and cancels either with %s', abandon => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    const legacyListener = vi.fn()
    client.onSettingsWorkspaceResponse(listener)
    client.onSettingsResponse(legacyListener)
    const first = createSettingsReadRequest(requestId, projectSnapshot.projectId)
    const second = createSettingsFilePreviewRequest(secondRequestId, globalSnapshot)

    client.sendSettings(first)
    expect(() => client.sendSettingsWorkspace(second)).toThrow('already pending')
    client[abandon](secondRequestId)
    expect(() => client.sendSettingsWorkspace(second)).toThrow('already pending')
    client[abandon](requestId)
    client.sendSettingsWorkspace(second)
    expect(() => client.sendSettings(first)).toThrow('already pending')
    transport.emit(available(first, projectSnapshot))
    const candidate = responseSnapshot(second, globalSnapshot)
    transport.emit(available(second, { ...candidate, projectId: projectSnapshot.projectId }))
    expect(listener).not.toHaveBeenCalled()
    expect(legacyListener).not.toHaveBeenCalled()

    client[abandon](secondRequestId)
    transport.emit(available(second, candidate))
    expect(listener).not.toHaveBeenCalled()
    client.sendSettings(first)
    transport.emit(available(first, projectSnapshot))
    expect(listener).toHaveBeenCalledOnce()
    expect(legacyListener).toHaveBeenCalledOnce()
    expect(transport.posted).toEqual([first, second, first])

    transport.throwOnPost = true
    expect(() => client.sendSettings(first)).toThrow('transport failed')
    transport.throwOnPost = false
    client.sendSettingsWorkspace(second)
    transport.emit(available(second, candidate))
    expect(listener).toHaveBeenCalledTimes(2)
    expect(legacyListener).toHaveBeenCalledOnce()
  })

  it.each(cases)('clears $method ($scope) pending on transport failure and permits retry', ({ request, target }) => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onSettingsWorkspaceResponse(listener)
    const response = available(request, responseSnapshot(request, target))
    transport.throwOnPost = true

    expect(() => client.sendSettingsWorkspace(request)).toThrow('transport failed')
    transport.emit(response)
    expect(listener).not.toHaveBeenCalled()
    expect(transport.posted).toEqual([])

    transport.throwOnPost = false
    client.sendSettingsWorkspace(request)
    transport.emit(response)
    expect(listener).toHaveBeenCalledExactlyOnceWith(response)
    expect(transport.posted).toEqual([request])
  })

  it('unsubscribes legacy and workspace listeners independently', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const legacy = vi.fn()
    const removedLegacy = vi.fn()
    const workspace = vi.fn()
    const removedWorkspace = vi.fn()
    client.onSettingsResponse(legacy)
    const unsubscribeLegacy = client.onSettingsResponse(removedLegacy)
    client.onSettingsWorkspaceResponse(workspace)
    const unsubscribeWorkspace = client.onSettingsWorkspaceResponse(removedWorkspace)
    unsubscribeLegacy()
    unsubscribeLegacy()
    unsubscribeWorkspace()
    unsubscribeWorkspace()

    const project = createSettingsReadRequest(requestId, projectSnapshot.projectId)
    client.sendSettings(project)
    transport.emit(available(project, projectSnapshot))
    const global = createSettingsGlobalReadRequest(secondRequestId)
    client.sendSettingsWorkspace(global)
    transport.emit(available(global, globalSnapshot))

    expect(legacy).toHaveBeenCalledOnce()
    expect(workspace).toHaveBeenCalledTimes(2)
    expect(removedLegacy).not.toHaveBeenCalled()
    expect(removedWorkspace).not.toHaveBeenCalled()
  })

  it.each(cases)('disposes $method ($scope) pending and both listener families without late delivery', ({ request, target }) => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const legacy = vi.fn()
    const workspace = vi.fn()
    const unsubscribeLegacy = client.onSettingsResponse(legacy)
    const unsubscribeWorkspace = client.onSettingsWorkspaceResponse(workspace)
    client.sendSettingsWorkspace(request)
    client.dispose()
    client.dispose()
    unsubscribeLegacy()
    unsubscribeWorkspace()
    expect(transport.listenerCount).toBe(0)
    const response = available(request, responseSnapshot(request, target))
    transport.emit(response)
    expect(legacy).not.toHaveBeenCalled()
    expect(workspace).not.toHaveBeenCalled()

    const project = createSettingsReadRequest(requestId, projectSnapshot.projectId)
    expect(() => client.sendSettings(project)).toThrow('disposed')
    expect(() => client.sendSettingsWorkspace(request)).toThrow('disposed')
    expect(() => client.abandonSettingsRequest(requestId)).toThrow('disposed')
    expect(() => client.abandonSettingsWorkspaceRequest(requestId)).toThrow('disposed')
    expect(() => client.onSettingsResponse(legacy)).toThrow('disposed')
    expect(() => client.onSettingsWorkspaceResponse(workspace)).toThrow('disposed')

    const replacement = createBridgeClient(transport)
    const nextListener = vi.fn()
    replacement.onSettingsWorkspaceResponse(nextListener)
    replacement.sendSettingsWorkspace(request)
    transport.emit(response)
    expect(nextListener).toHaveBeenCalledExactlyOnceWith(response)
    expect(workspace).not.toHaveBeenCalled()
    expect(legacy).not.toHaveBeenCalled()
    replacement.dispose()
  })

  it.each(['sendSettings', 'sendSettingsWorkspace'] as const)('registers pending before synchronous %s transport replies and clears it before callbacks', send => {
    const first = createSettingsReadRequest(requestId, projectSnapshot.projectId)
    const second = createSettingsReadRequest(secondRequestId, projectSnapshot.projectId)
    class ReplyingTransport extends FakeTransport {
      override postMessage(message: Parameters<BridgeTransport['postMessage']>[0]): void {
        super.postMessage(message)
        this.emit({ ...available(first, projectSnapshot), requestId: message.requestId })
      }
    }
    const transport = new ReplyingTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn((response: SettingsWorkspaceResponse) => {
      if (response.requestId === requestId) client.sendSettingsWorkspace(second)
    })
    client.onSettingsWorkspaceResponse(listener)

    client[send](first)

    expect(listener).toHaveBeenCalledTimes(2)
    expect(transport.posted).toEqual([first, second])
  })

  it('captures the target identity rather than retaining mutable request params', () => {
    const original = createSettingsFilePreviewRequest(requestId, globalSnapshot)
    const params = { ...original.params }
    const request = { ...original, params }
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onSettingsWorkspaceResponse(listener)
    client.sendSettingsWorkspace(request)

    params.projectId = otherProjectId
    params.expectedRevision = 99
    params.expectedProfileId = otherProjectId
    params.expectedProfileVersion = '9.0.0'
    params.expectedProfileDigest = '9'.repeat(64)
    params.expectedSnapshotDigest = '9'.repeat(64)
    const response = available(original, responseSnapshot(original, globalSnapshot))
    transport.emit(response)

    expect(listener).toHaveBeenCalledExactlyOnceWith(response)
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

function createDeliveryResponseForDifferentVersion(
  requestId: string,
  includeTrace: boolean,
) {
  const value = createDeliveryResponseValue(requestId, includeTrace)
  value.result.snapshot.versionId = deliverySecondVersionId
  return value
}
