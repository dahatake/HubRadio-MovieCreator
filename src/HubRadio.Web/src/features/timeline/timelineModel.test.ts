import { describe, expect, it } from 'vitest'

import {
  createExactRationalTime,
  exactRationalTimesEqual,
} from './rationalTime'
import {
  applyTimelineOperation,
  createSplitOperationAtOutputTime,
  createTrimOperations,
  mapOutputTimeToSource,
  mapUniqueSourceTimeToOutput,
  replayTimelineOperations,
  resolveTimelinePlayhead,
} from './timelineModel'
import { parseTimelineReadResponse } from './timelineProtocol'
import {
  createTimelineReadResponseValue,
  timelineAssetElementId,
  timelineProjectId,
  timelineRightElementId,
  timelineSourceElementId,
} from './timelineTestFixture'

describe('timeline exact preview model', () => {
  it('replays split/move/delete with stable IDs and exact source mapping', () => {
    const baseline = snapshotElements()
    const splitOffset = createExactRationalTime(1001n, 48000n)
    const preview = replayTimelineOperations(baseline, [
      {
        kind: 'split',
        elementId: timelineSourceElementId,
        splitOffset,
        rightElementId: timelineRightElementId,
      },
      { kind: 'move', elementId: timelineAssetElementId, targetIndex: 0 },
      { kind: 'delete', elementId: timelineSourceElementId },
    ])

    expect(preview.map((element) => element.id)).toEqual([
      timelineAssetElementId,
      timelineRightElementId,
    ])
    expect(preview[0]?.outputRange.start).toEqual({ value: '0', rate: '1' })
    expect(preview[1]?.outputRange.start).toEqual({ value: '1', rate: '2' })
    expect(preview[1]?.sourceRange?.start).toEqual({
      value: '-3799',
      rate: '48000',
    })
    expect(baseline.map((element) => element.id)).toEqual([
      timelineSourceElementId,
      timelineAssetElementId,
    ])
  })

  it('creates split and trim batches only from exact playhead time', () => {
    const elements = snapshotElements()
    const splitTime = createExactRationalTime(1001n, 48000n)
    const split = createSplitOperationAtOutputTime(
      elements,
      timelineSourceElementId,
      splitTime,
      timelineRightElementId,
    )
    const trimStart = createTrimOperations(
      elements,
      splitTime,
      'start',
      () => timelineRightElementId,
    )
    const trimEnd = createTrimOperations(
      elements,
      splitTime,
      'end',
      () => timelineRightElementId,
    )

    expect(split).toMatchObject({
      kind: 'split',
      splitOffset: { value: '1001', rate: '48000' },
    })
    expect(trimStart?.map((operation) => operation.kind)).toEqual([
      'split',
      'delete',
    ])
    expect(trimEnd?.map((operation) => operation.kind)).toEqual([
      'split',
      'delete',
      'delete',
    ])
    expect(
      replayTimelineOperations(elements, trimStart ?? []).map(
        (element) => element.id,
      ),
    ).toEqual([timelineRightElementId, timelineAssetElementId])
    expect(
      replayTimelineOperations(elements, trimEnd ?? []).map(
        (element) => element.id,
      ),
    ).toEqual([timelineSourceElementId])
  })

  it('maps VFR source/output times exactly and rejects ambiguous source mappings', () => {
    const elements = snapshotElements()
    const outputTime = createExactRationalTime(1001n, 48000n)
    const sourceTime = mapOutputTimeToSource(elements, outputTime)

    expect(sourceTime).toEqual({ value: '-3799', rate: '48000' })
    const mappedBack = mapUniqueSourceTimeToOutput(elements, sourceTime!)
    expect(mappedBack).not.toBeNull()
    expect(exactRationalTimesEqual(mappedBack!, outputTime)).toBe(true)
    expect(
      resolveTimelinePlayhead(
        elements,
        {
          projectId: timelineProjectId,
          sourcePresentationTime: sourceTime!,
          outputPresentationTime: null,
        },
        timelineProjectId,
      ),
    ).toEqual(outputTime)
    expect(
      resolveTimelinePlayhead(
        elements,
        {
          projectId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
          sourcePresentationTime: sourceTime!,
          outputPresentationTime: outputTime,
        },
        timelineProjectId,
      ),
    ).toBeNull()

    const moved = replayTimelineOperations(elements, [
      { kind: 'move', elementId: timelineSourceElementId, targetIndex: 1 },
    ])
    expect(
      resolveTimelinePlayhead(
        moved,
        {
          projectId: timelineProjectId,
          sourcePresentationTime: sourceTime!,
          outputPresentationTime: outputTime,
        },
        timelineProjectId,
        true,
      ),
    ).toEqual({ value: '25001', rate: '48000' })
    const withoutSource = replayTimelineOperations(elements, [
      { kind: 'delete', elementId: timelineSourceElementId },
    ])
    expect(
      resolveTimelinePlayhead(
        withoutSource,
        {
          projectId: timelineProjectId,
          sourcePresentationTime: sourceTime!,
          outputPresentationTime: outputTime,
        },
        timelineProjectId,
        true,
      ),
    ).toBeNull()

    const splitAndMoved = replayTimelineOperations(elements, [
      {
        kind: 'split',
        elementId: timelineSourceElementId,
        splitOffset: outputTime,
        rightElementId: timelineRightElementId,
      },
      { kind: 'move', elementId: timelineRightElementId, targetIndex: 0 },
    ])
    expect(mapUniqueSourceTimeToOutput(splitAndMoved, sourceTime!)).not.toBeNull()
  })

  it('rejects out-of-range operations instead of clamping', () => {
    expect(() =>
      replayTimelineOperations(snapshotElements(), [
        { kind: 'move', elementId: timelineSourceElementId, targetIndex: 2 },
      ]),
    ).toThrow('outside')
    expect(() =>
      applyTimelineOperation(snapshotElements(), {
        kind: 'split',
        elementId: timelineSourceElementId,
        splitOffset: createExactRationalTime(1001n, 48000n),
        rightElementId: timelineSourceElementId,
      }),
    ).toThrow('invalid')
    expect(() =>
      replayTimelineOperations(snapshotElements(), [
        { kind: 'delete', elementId: timelineSourceElementId },
        {
          kind: 'split',
          elementId: timelineAssetElementId,
          splitOffset: createExactRationalTime(1n, 4n),
          rightElementId: timelineSourceElementId,
        },
      ]),
    ).toThrow('must be new')
    expect(
      createSplitOperationAtOutputTime(
        snapshotElements(),
        timelineSourceElementId,
        createExactRationalTime(0n, 1n),
        timelineRightElementId,
      ),
    ).toBeNull()
  })
})

function snapshotElements() {
  const response = parseTimelineReadResponse(createTimelineReadResponseValue())
  if (response?.result.status !== 'available') {
    throw new Error('Expected an available fixture snapshot.')
  }
  return response.result.snapshot.elements
}