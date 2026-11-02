import { describe, expect, it } from 'vitest'

import {
  COMPARISON_MAX_REQUEST_UTF8_BYTES,
  COMPARISON_READ_METHOD,
  createComparisonReadRequest,
  parseComparisonReadResponse,
  type ComparisonFailureStatus,
} from './compareProtocol'
import {
  comparisonAddedElementId,
  comparisonLeftDigest,
  comparisonLeftVersionId,
  comparisonRequestId,
  comparisonRightDigest,
  comparisonRightVersionId,
  createComparisonResponseValue,
  hostileComparisonReason,
} from './compareTestFixture'

describe('comparison protocol', () => {
  it('creates one exact, digest-bound, immutable request', () => {
    const request = createComparisonReadRequest(
      comparisonRequestId,
      comparisonLeftVersionId,
      comparisonLeftDigest,
      comparisonRightVersionId,
      comparisonRightDigest,
    )

    expect(request).toEqual({
      version: 1,
      requestId: comparisonRequestId,
      method: COMPARISON_READ_METHOD,
      params: {
        leftVersionId: comparisonLeftVersionId,
        leftVersionDigest: comparisonLeftDigest,
        rightVersionId: comparisonRightVersionId,
        rightVersionDigest: comparisonRightDigest,
      },
    })
    expect(Object.isFrozen(request)).toBe(true)
    expect(Object.isFrozen(request.params)).toBe(true)
    expect(new TextEncoder().encode(JSON.stringify(request)).byteLength)
      .toBeLessThanOrEqual(COMPARISON_MAX_REQUEST_UTF8_BYTES)
  })

  it('rejects noncanonical IDs and stale-shaped digests before sending', () => {
    expect(() => createComparisonReadRequest(
      comparisonRequestId.toUpperCase(),
      comparisonLeftVersionId,
      comparisonLeftDigest,
      comparisonRightVersionId,
      comparisonRightDigest,
    )).toThrow('lowercase UUID')
    expect(() => createComparisonReadRequest(
      comparisonRequestId,
      comparisonLeftVersionId,
      comparisonLeftDigest.toUpperCase(),
      comparisonRightVersionId,
      comparisonRightDigest,
    )).toThrow('64 lowercase')
    expect(() => createComparisonReadRequest(
      comparisonRequestId,
      comparisonLeftVersionId,
      comparisonLeftDigest,
      '00000000-0000-0000-0000-000000000000',
      comparisonRightDigest,
    )).toThrow('lowercase UUID')
  })

  it('parses and deeply freezes exact durable comparison evidence', () => {
    const response = parseComparisonReadResponse(createComparisonResponseValue())

    expect(response?.result.status).toBe('available')
    if (response?.result.status !== 'available') {
      throw new Error('Expected an available comparison response.')
    }
    const comparison = response.result.comparison
    expect(comparison.left.versionId).toBe(comparisonLeftVersionId)
    expect(comparison.right.versionDigest).toBe(comparisonRightDigest)
    expect(comparison.right.changeReason).toBe(hostileComparisonReason)
    expect(comparison.left.playbackDuration).toEqual({ value: '9', rate: '2' })
    expect(comparison.right.usedSourceRanges).toHaveLength(2)
    expect(comparison.elementChanges.map((change) => change.kind)).toEqual([
      'removed',
      'added',
      'moved',
      'modified',
    ])
    expect(comparison.adoptionChanges.map((change) => change.kind)).toEqual([
      'adopted',
      'replaced',
    ])
    expect(Object.isFrozen(response)).toBe(true)
    expect(Object.isFrozen(response.result)).toBe(true)
    expect(Object.isFrozen(comparison)).toBe(true)
    expect(Object.isFrozen(comparison.left)).toBe(true)
    expect(Object.isFrozen(comparison.left.usedSourceRanges)).toBe(true)
    expect(Object.isFrozen(comparison.left.usedSourceRanges[0])).toBe(true)
    expect(Object.isFrozen(comparison.elementChanges)).toBe(true)
    expect(Object.isFrozen(comparison.elementChanges[0])).toBe(true)
    expect(Object.isFrozen(comparison.adoptionChanges[0])).toBe(true)
  })

  it('FR-028 AC-076: parses recorded opening hook and transitions per plan', () => {
    const value = createComparisonResponseValue()
    const hook = {
      status: 'recorded',
      elementId: comparisonAddedElementId,
      origin: 'sourceSpan',
      outputStart: { value: '0', rate: '1' },
      outputDuration: { value: '2', rate: '1' },
      adopted: true,
    }
    value.result.comparison.left.features.openingHook = hook as never
    value.result.comparison.right.features.transitions =
      { status: 'recorded', cutCount: 3 } as never
    const response = parseComparisonReadResponse(value)
    if (response?.result.status !== 'available') {
      throw new Error('Expected an available comparison response.')
    }
    const { left, right } = response.result.comparison
    expect(left.features.openingHook).toEqual(hook)
    expect(right.features.transitions).toEqual({ status: 'recorded', cutCount: 3 })
    expect(Object.keys(left.features)).toHaveLength(8)

    const bad = createComparisonResponseValue()
    bad.result.comparison.left.features.transitions =
      { status: 'recorded', cutCount: -1 } as never
    expect(parseComparisonReadResponse(bad)).toBeNull()
  })

  it('accepts only closed private-safe failure statuses', () => {
    const statuses: ComparisonFailureStatus[] = [
      'forbidden',
      'notFound',
      'staleVersion',
      'invalidComparison',
      'unavailable',
    ]
    for (const status of statuses) {
      const value = {
        version: 1,
        requestId: comparisonRequestId,
        method: COMPARISON_READ_METHOD,
        result: { status, comparison: null },
      }
      expect(parseComparisonReadResponse(value)).toEqual(value)
    }

    expect(parseComparisonReadResponse({
      version: 1,
      requestId: comparisonRequestId,
      method: COMPARISON_READ_METHOD,
      result: {
        status: 'unavailable',
        comparison: null,
        privateException: 'C:\\private\\versions.db',
      },
    })).toBeNull()
    expect(parseComparisonReadResponse({
      version: 1,
      requestId: comparisonRequestId,
      method: COMPARISON_READ_METHOD,
      result: { status: 'stale', comparison: null },
    })).toBeNull()
  })

  it('rejects unknown fields and malformed exact-version bindings', () => {
    const rootExtra = cloneFixture()
    Object.assign(rootExtra, { actorSid: 'S-1-5-21-private' })
    const resultExtra = cloneFixture()
    Object.assign(resultExtra.result, { path: 'C:\\private.mov' })
    const comparisonExtra = cloneFixture()
    Object.assign(comparisonExtra.result.comparison, { command: 'ffmpeg -i x' })
    const versionExtra = cloneFixture()
    Object.assign(versionExtra.result.comparison.left, { projectId: comparisonRequestId })
    const featureExtra = cloneFixture()
    Object.assign(featureExtra.result.comparison.left.features, { inferredHook: 'first' })
    const uppercaseId = cloneFixture()
    uppercaseId.result.comparison.left.versionId =
      comparisonRequestId.toUpperCase()
    const uppercaseDigest = cloneFixture()
    uppercaseDigest.result.comparison.right.versionDigest =
      `ABCDEF${comparisonRightDigest.slice(6)}`

    for (const value of [
      rootExtra,
      resultExtra,
      comparisonExtra,
      versionExtra,
      featureExtra,
      uppercaseId,
      uppercaseDigest,
    ]) {
      expect(parseComparisonReadResponse(value)).toBeNull()
    }
  })

  it('rejects invalid rationals, duplicate evidence, and inferred features', () => {
    const unreduced = cloneFixture()
    unreduced.result.comparison.left.playbackDuration = { value: '2', rate: '4' }
    const negativePlayback = cloneFixture()
    negativePlayback.result.comparison.left.playbackDuration = {
      value: '-1',
      rate: '1',
    }
    const zeroDuration = cloneFixture()
    zeroDuration.result.comparison.left.usedSourceRanges[0]!.sourceRange.duration = {
      value: '0',
      rate: '1',
    }
    const durationMismatch = cloneFixture()
    durationMismatch.result.comparison.left.usedSourceRanges[0]!
      .sourceRange.duration = { value: '1', rate: '1' }
    const duplicateRange = cloneFixture()
    duplicateRange.result.comparison.right.usedSourceRanges.push(
      structuredClone(duplicateRange.result.comparison.right.usedSourceRanges[0]!),
    )
    const inventedFeature = cloneFixture()
    inventedFeature.result.comparison.left.features.openingHook = 'available'
    const malformedTimestamp = cloneFixture()
    malformedTimestamp.result.comparison.left.createdAt =
      '2036-08-09T10:11:12.000Z'
    const unrepresentableRangeEnd = cloneFixture()
    unrepresentableRangeEnd.result.comparison.left.usedSourceRanges[0]!
      .sourceRange.start = {
        value: '9223372036854775807',
        rate: '1',
      }
    const unrepresentableRangeRate = cloneFixture()
    unrepresentableRangeRate.result.comparison.left.usedSourceRanges[0]!
      .sourceRange.start = {
        value: '1',
        rate: '9223372036854775807',
      }
    unrepresentableRangeRate.result.comparison.left.usedSourceRanges[0]!
      .sourceRange.duration = {
        value: '1',
        rate: '9223372036854775806',
      }
    unrepresentableRangeRate.result.comparison.left.usedSourceRanges[0]!
      .outputRange.duration = {
        value: '1',
        rate: '9223372036854775806',
      }
    const playbackShorterThanUsedOutput = cloneFixture()
    playbackShorterThanUsedOutput.result.comparison.left.playbackDuration = {
      value: '1',
      rate: '1',
    }

    for (const value of [
      unreduced,
      negativePlayback,
      zeroDuration,
      durationMismatch,
      duplicateRange,
      inventedFeature,
      malformedTimestamp,
      unrepresentableRangeEnd,
      unrepresentableRangeRate,
      playbackShorterThanUsedOutput,
    ]) {
      expect(parseComparisonReadResponse(value)).toBeNull()
    }
  })

  it('rejects noncanonical change ordering and inconsistent adoption changes', () => {
    const outOfOrder = cloneFixture()
    outOfOrder.result.comparison.elementChanges = [
      outOfOrder.result.comparison.elementChanges[1]!,
      outOfOrder.result.comparison.elementChanges[0]!,
      ...outOfOrder.result.comparison.elementChanges.slice(2),
    ]
    const duplicateElement = cloneFixture()
    duplicateElement.result.comparison.elementChanges[1]!.elementId =
      duplicateElement.result.comparison.elementChanges[0]!.elementId
    const invalidMovedIndices = cloneFixture()
    invalidMovedIndices.result.comparison.elementChanges[2]!.previousIndex = 0
    const adoptionPair = cloneFixture()
    adoptionPair.result.comparison.adoptionChanges[0]!.previousSuggestionId =
      comparisonLeftVersionId
    const unrelatedAdoption = cloneFixture()
    unrelatedAdoption.result.comparison.adoptionChanges[0]!.elementId =
      comparisonRightVersionId
    const reversedAdoptions = cloneFixture()
    reversedAdoptions.result.comparison.adoptionChanges.reverse()
    const unchangedReplacement = cloneFixture()
    unchangedReplacement.result.comparison.adoptionChanges[1]!
      .currentSuggestionId = unchangedReplacement.result.comparison
        .adoptionChanges[1]!.previousSuggestionId!

    for (const value of [
      outOfOrder,
      duplicateElement,
      invalidMovedIndices,
      adoptionPair,
      unrelatedAdoption,
      reversedAdoptions,
      unchangedReplacement,
    ]) {
      expect(parseComparisonReadResponse(value)).toBeNull()
    }
  })

  it('accepts a same-version empty diff and rejects invented same-version changes', () => {
    const same = cloneFixture()
    same.result.comparison.right = structuredClone(same.result.comparison.left)
    same.result.comparison.elementChanges = []
    same.result.comparison.adoptionChanges = []
    expect(parseComparisonReadResponse(same)).not.toBeNull()

    const inventedChange = structuredClone(same)
    inventedChange.result.comparison.elementChanges = [{
      kind: 'added',
      elementId: comparisonAddedElementId,
      previousIndex: null,
      currentIndex: 0,
    }]
    expect(parseComparisonReadResponse(inventedChange)).toBeNull()

    const conflictingDigest = structuredClone(same)
    conflictingDigest.result.comparison.right.versionDigest =
      comparisonRightDigest
    expect(parseComparisonReadResponse(conflictingDigest)).toBeNull()

    const conflictingEvidence = structuredClone(same)
    conflictingEvidence.result.comparison.right.changeReason =
      'Conflicting evidence for the same version.'
    expect(parseComparisonReadResponse(conflictingEvidence)).toBeNull()
  })

  it('rejects responses beyond the independent 1 MiB boundary', () => {
    const oversized = cloneFixture()
    oversized.result.comparison.right.changeReason = 'x'.repeat(1024 * 1024)
    expect(parseComparisonReadResponse(oversized)).toBeNull()
  })

  function cloneFixture(): ReturnType<typeof createComparisonResponseValue> {
    return structuredClone(createComparisonResponseValue())
  }
})