import { describe, expect, it } from 'vitest'

import {
  createDeliveryReadRequest,
  DELIVERY_MAX_REQUEST_UTF8_BYTES,
  DELIVERY_READ_METHOD,
  isDeliveryOutputTime,
  parseDeliveryReadResponse,
} from './deliveryProtocol'
import {
  createDeliveryFailureValue,
  createDeliveryResponseValue,
  deliveryAssetId,
  deliveryRequestId,
  deliverySourceDigest,
  deliverySourceSpanId,
  deliveryVersionId,
  deliveryVideoArtifactId,
  deliveryVideoDigest,
} from './deliveryTestFixture'

const outputTime = { value: '1', rate: '2' } as const

describe('delivery bridge protocol', () => {
  it('creates a closed version-bound request with explicit optional exact time', () => {
    const request = createDeliveryReadRequest(
      deliveryRequestId,
      deliveryVersionId,
      outputTime,
    )
    const withoutTrace = createDeliveryReadRequest(
      deliveryRequestId,
      deliveryVersionId,
      null,
    )

    expect(request).toEqual({
      version: 1,
      requestId: deliveryRequestId,
      method: DELIVERY_READ_METHOD,
      params: { versionId: deliveryVersionId, outputTime },
    })
    expect(withoutTrace.params.outputTime).toBeNull()
    expect(Object.isFrozen(request)).toBe(true)
    expect(Object.isFrozen(request.params)).toBe(true)
    expect(Object.isFrozen(request.params.outputTime)).toBe(true)
    expect(
      new TextEncoder().encode(JSON.stringify(request)).byteLength,
    ).toBeLessThanOrEqual(DELIVERY_MAX_REQUEST_UTF8_BYTES)
    expect(JSON.stringify(request)).not.toContain('actorSid')
  })

  it('rejects noncanonical IDs and non-reduced or out-of-range lookup times', () => {
    expect(() => createDeliveryReadRequest(
      deliveryRequestId.toUpperCase(),
      deliveryVersionId,
      null,
    )).toThrow()
    expect(() => createDeliveryReadRequest(
      deliveryRequestId,
      '00000000-0000-0000-0000-000000000000',
      null,
    )).toThrow()

    for (const candidate of [
      { value: '-1', rate: '1' },
      { value: '01', rate: '1' },
      { value: '1', rate: '0' },
      { value: '2', rate: '4' },
      { value: '9223372036854775808', rate: '1' },
      { value: '1', rate: '9223372036854775808' },
    ]) {
      expect(isDeliveryOutputTime(candidate)).toBe(false)
      expect(() => createDeliveryReadRequest(
        deliveryRequestId,
        deliveryVersionId,
        candidate,
      )).toThrow()
    }
  })

  it('parses and deeply freezes complete evidence and exact trace bindings', () => {
    const parsed = parseDeliveryReadResponse(createDeliveryResponseValue())

    expect(parsed?.method).toBe(DELIVERY_READ_METHOD)
    if (parsed === null || parsed.result.status !== 'available') {
      throw new Error('Expected available delivery evidence.')
    }
    expect(parsed.result.snapshot.versionId).toBe(deliveryVersionId)
    expect(parsed.result.snapshot.artifacts[0]?.sha256).toBe(deliveryVideoDigest)
    expect(parsed.result.snapshot.qualityControl.artifactId).toBe(
      deliveryVideoArtifactId,
    )
    expect(parsed.result.snapshot.qualityControl.humanReviewRequired).toBe(true)
    expect(
      parsed.result.snapshot.qualityControl.approvedNumericThresholdsApplied,
    ).toBe(false)
    expect(parsed.result.snapshot.qualityControl.canProceedToDelivery).toBe(false)
    expect(parsed.result.trace?.outputTime).toEqual(outputTime)
    expect(parsed.result.trace?.originId).toBe(deliverySourceSpanId)
    expect(Object.isFrozen(parsed)).toBe(true)
    expect(Object.isFrozen(parsed.result)).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot)).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot.artifacts)).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot.artifacts[0])).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot.review.approvals)).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot.qualityControl.videoTiming)).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot.qualityControl.tools)).toBe(true)
    expect(Object.isFrozen(parsed.result.trace?.sourceSpan)).toBe(true)
  })

  it('accepts only closed failures with no partial snapshot or trace', () => {
    for (const status of [
      'forbidden',
      'unavailable',
      'incomplete',
      'mismatched',
      'qualityControlFailed',
    ] as const) {
      const value = createDeliveryFailureValue(status)
      expect(parseDeliveryReadResponse(value)).toEqual(value)

      expect(parseDeliveryReadResponse({
        ...value,
        result: { ...value.result, snapshot: {} },
      })).toBeNull()
      expect(parseDeliveryReadResponse({
        ...value,
        result: { ...value.result, trace: {} },
      })).toBeNull()
    }
    expect(parseDeliveryReadResponse(createDeliveryFailureValue(
      'qualityControlFailed',
    ))?.result.status).toBe('qualityControlFailed')
  })

  it('rejects missing QC artifacts, wrong digests, and private or unknown fields', () => {
    const missing = cloneDeliveryValue()
    missing.result.snapshot.artifacts = missing.result.snapshot.artifacts.filter(
      (artifact) => artifact.artifactId !== deliveryVideoArtifactId,
    )
    expect(parseDeliveryReadResponse(missing)).toBeNull()

    const wrongDigest = cloneDeliveryValue()
    wrongDigest.result.snapshot.artifacts[0]!.sha256 = '0'.repeat(64)
    expect(parseDeliveryReadResponse(wrongDigest)).toBeNull()

    const privateRoot = cloneDeliveryValue()
    ;(privateRoot as unknown as Record<string, unknown>).storageReference =
      'C:\\private\\master.mp4'
    expect(parseDeliveryReadResponse(privateRoot)).toBeNull()

    const privateArtifact = cloneDeliveryValue()
    ;(privateArtifact.result.snapshot.artifacts[0] as unknown as Record<string, unknown>)
      .storageReference = 'projects/private/master.mp4'
    expect(parseDeliveryReadResponse(privateArtifact)).toBeNull()
  })

  it('rejects broken trace identity, range, and source-rights bindings', () => {
    const wrongOrigin = cloneDeliveryValue()
    requiredSourceSpanTrace(wrongOrigin).originId = deliveryAssetId
    expect(parseDeliveryReadResponse(wrongOrigin)).toBeNull()

    const exclusiveEnd = cloneDeliveryValue()
    requiredSourceSpanTrace(exclusiveEnd).sourceTime = { value: '2', rate: '1' }
    expect(parseDeliveryReadResponse(exclusiveEnd)).toBeNull()

    const wrongSourceDigest = cloneDeliveryValue()
    requiredSourceSpanTrace(wrongSourceDigest).sourceSpan.sourceDigest =
      '2'.repeat(64)
    expect(parseDeliveryReadResponse(wrongSourceDigest)).toBeNull()

    const notFound = cloneDeliveryValue()
    ;(notFound.result as { trace: unknown }).trace = {
      status: 'notFound',
      outputTime: { value: '10', rate: '1' },
      originKind: null,
      originId: null,
      sourceTime: null,
      sourceSpan: null,
    }
    const parsed = parseDeliveryReadResponse(notFound)
    expect(parsed?.result.trace?.status).toBe('notFound')
  })

  it('rejects QC arithmetic drift, false readiness, and inconsistent review areas', () => {
    const wrongDuration = cloneDeliveryValue()
    wrongDuration.result.snapshot.qualityControl.duration.videoDelta = {
      value: '1',
      rate: '1',
    }
    expect(parseDeliveryReadResponse(wrongDuration)).toBeNull()

    const fabricatedThresholds = cloneDeliveryValue()
    fabricatedThresholds.result.snapshot.qualityControl
      .approvedNumericThresholdsApplied = true
    expect(parseDeliveryReadResponse(fabricatedThresholds)).toBeNull()

    const fabricatedReadiness = cloneDeliveryValue()
    fabricatedReadiness.result.snapshot.qualityControl.canProceedToDelivery = true
    expect(parseDeliveryReadResponse(fabricatedReadiness)).toBeNull()

    const missingHumanArea = cloneDeliveryValue()
    missingHumanArea.result.snapshot.qualityControl.requiredHumanReviewAreas.pop()
    expect(parseDeliveryReadResponse(missingHumanArea)).toBeNull()

    const duplicateStream = cloneDeliveryValue()
    duplicateStream.result.snapshot.qualityControl.audioTiming!.streamIndex = 0
    expect(parseDeliveryReadResponse(duplicateStream)).toBeNull()

    const wrongLumaFrameCount = cloneDeliveryValue()
    wrongLumaFrameCount.result.snapshot.qualityControl.luma.measuredFrameCount--
    expect(parseDeliveryReadResponse(wrongLumaFrameCount)).toBeNull()

    const sidecarWithoutSubtitleReview = cloneDeliveryValue()
    sidecarWithoutSubtitleReview.result.snapshot.qualityControl
      .requiredHumanReviewAreas.splice(2, 1)
    expect(parseDeliveryReadResponse(sidecarWithoutSubtitleReview)).toBeNull()
  })

  it('accepts burned-in subtitle review evidence without a subtitle stream', () => {
    const burnedIn = cloneDeliveryValue()
    ;(burnedIn.result.snapshot.qualityControl as { subtitleTiming: unknown })
      .subtitleTiming = {
      streamIndex: null,
      observedCueCount: null,
      evidenceDigest: null,
    }

    const parsed = parseDeliveryReadResponse(burnedIn)

    expect(parsed?.result.status).toBe('available')
    if (parsed === null || parsed.result.status !== 'available') {
      throw new Error('Expected burned-in subtitle QC evidence.')
    }
    expect(parsed.result.snapshot.qualityControl.requiredHumanReviewAreas)
      .toContain('subtitlePresentation')
    expect(parsed.result.snapshot.qualityControl.subtitleTiming.streamIndex)
      .toBeNull()
  })

  it('accepts only approval-capable roles in approval evidence', () => {
    const invalid = cloneDeliveryValue()
    invalid.result.snapshot.review.approvals[0]!.actorRole = 'editor'

    expect(parseDeliveryReadResponse(invalid)).toBeNull()
  })

  it('preserves internal-only scope without deriving external publication permission', () => {
    const value = cloneDeliveryValue()
    value.result.snapshot.publicationScope = 'internalOnly'
    value.result.snapshot.rights[0]!.externalPublicationAllowed = true

    const parsed = parseDeliveryReadResponse(value)
    if (parsed === null || parsed.result.status !== 'available') {
      throw new Error('Expected valid independent publication and rights evidence.')
    }
    expect(parsed.result.snapshot.publicationScope).toBe('internalOnly')
    expect(parsed.result.snapshot.rights[0]?.externalPublicationAllowed).toBe(true)
  })

  it('rejects malformed text, oversized text, invalid timestamps, and excess response bytes', () => {
    const malformedText = cloneDeliveryValue()
    malformedText.result.snapshot.rights[0]!.allowedUse = '\ud800'
    expect(parseDeliveryReadResponse(malformedText)).toBeNull()

    const oversizedText = cloneDeliveryValue()
    oversizedText.result.snapshot.rights[0]!.allowedUse = 'あ'.repeat(1_366) + 'ab'
    expect(parseDeliveryReadResponse(oversizedText)).toBeNull()

    const invalidTimestamp = cloneDeliveryValue()
    invalidTimestamp.result.snapshot.review.approvals[0]!.approvedAt =
      '2036-02-30T09:10:11.0000000+00:00'
    expect(parseDeliveryReadResponse(invalidTimestamp)).toBeNull()

    const response = cloneDeliveryValue()
    response.result.snapshot.rights[0]!.allowedUse = 'a'.repeat(1_048_576)
    expect(parseDeliveryReadResponse(response)).toBeNull()
  })

  it('requires rights evidence to match status semantics', () => {
    const approvedWithoutEvidence = cloneDeliveryValue()
    approvedWithoutEvidence.result.snapshot.rights[0]!.evidenceRecorded = false
    expect(parseDeliveryReadResponse(approvedWithoutEvidence)).toBeNull()

    const unconfirmedExternal = cloneDeliveryValue()
    unconfirmedExternal.result.snapshot.rights[0]!.status = 'unconfirmed'
    expect(parseDeliveryReadResponse(unconfirmedExternal)).toBeNull()

    const valid = cloneDeliveryValue()
    valid.result.snapshot.rights[0]!.sourceDigest = deliverySourceDigest
    expect(parseDeliveryReadResponse(valid)).not.toBeNull()
  })
})

type DeliveryResponseValue = ReturnType<typeof createDeliveryResponseValue>

function cloneDeliveryValue(): DeliveryResponseValue {
  return JSON.parse(
    JSON.stringify(createDeliveryResponseValue()),
  ) as DeliveryResponseValue
}

function requiredSourceSpanTrace(value: DeliveryResponseValue) {
  const trace = value.result.trace
  if (trace === null) {
    throw new Error('Expected a source-span trace fixture.')
  }
  return trace
}
