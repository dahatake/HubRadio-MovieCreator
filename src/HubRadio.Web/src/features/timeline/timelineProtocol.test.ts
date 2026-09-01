import { describe, expect, it } from 'vitest'

import {
  TIMELINE_MAX_REQUEST_UTF8_BYTES,
  createTimelineApplyRequest,
  createTimelineReadRequest,
  parseTimelineApplyResponse,
  parseTimelineReadResponse,
  type TimelineOperation,
} from './timelineProtocol'
import {
  createTimelineApplyResponseValue,
  createTimelineReadResponseValue,
  timelineAssetElementId,
  timelineProjectId,
  timelineRequestId,
  timelineRightElementId,
  timelineSourceElementId,
} from './timelineTestFixture'

describe('timeline bridge protocol', () => {
  it('creates only closed typed read/apply requests within the 96 KiB envelope', () => {
    const operations: readonly TimelineOperation[] = [
      { kind: 'keep', elementId: timelineSourceElementId },
      { kind: 'delete', elementId: timelineAssetElementId },
      {
        kind: 'split',
        elementId: timelineSourceElementId,
        splitOffset: { value: '1001', rate: '48000' },
        rightElementId: timelineRightElementId,
      },
      { kind: 'move', elementId: timelineAssetElementId, targetIndex: 0 },
    ]
    const read = createTimelineReadRequest(timelineRequestId, timelineProjectId)
    const apply = createTimelineApplyRequest(
      timelineRequestId,
      timelineProjectId,
      3,
      operations,
    )

    expect(read).toEqual({
      version: 1,
      requestId: timelineRequestId,
      method: 'timeline.read',
      params: { projectId: timelineProjectId },
    })
    expect(apply.params.operations).toEqual(operations)
    expect(
      new TextEncoder().encode(JSON.stringify(apply)).byteLength,
    ).toBeLessThanOrEqual(TIMELINE_MAX_REQUEST_UTF8_BYTES)
    expect(Object.isFrozen(apply.params.operations)).toBe(true)
    const split = apply.params.operations[2]
    expect(split?.kind).toBe('split')
    if (split?.kind !== 'split') {
      throw new Error('Expected the third operation to be a split.')
    }
    expect(Object.isFrozen(split.splitOffset)).toBe(true)
  })

  it('rejects invalid exact operation fields and oversized batches', () => {
    const invalidOperations: readonly TimelineOperation[][] = [
      [],
      [{ kind: 'move', elementId: timelineSourceElementId, targetIndex: -1 }],
      [{ kind: 'move', elementId: timelineSourceElementId, targetIndex: 0.5 }],
      [
        {
          kind: 'split',
          elementId: timelineSourceElementId,
          splitOffset: { value: '2', rate: '2' },
          rightElementId: timelineRightElementId,
        },
      ],
      [
        {
          kind: 'split',
          elementId: timelineSourceElementId,
          splitOffset: { value: '0', rate: '1' },
          rightElementId: timelineRightElementId,
        },
      ],
      [
        {
          kind: 'split',
          elementId: timelineSourceElementId,
          splitOffset: { value: '1', rate: '2' },
          rightElementId: timelineSourceElementId,
        },
      ],
    ]

    for (const operations of invalidOperations) {
      expect(() =>
        createTimelineApplyRequest(
          timelineRequestId,
          timelineProjectId,
          3,
          operations,
        ),
      ).toThrow()
    }

    const oversized = Array.from({ length: 2_000 }, () => ({
      kind: 'keep' as const,
      elementId: timelineSourceElementId,
    }))
    expect(() =>
      createTimelineApplyRequest(
        timelineRequestId,
        timelineProjectId,
        3,
        oversized,
      ),
    ).toThrow('byte limit')
  })

  it('drops untyped command-shaped fields instead of sending them', () => {
    const adversarial = {
      kind: 'delete',
      elementId: timelineSourceElementId,
      command: 'ffmpeg -i C:\\private.mov',
      path: 'C:\\private.mov',
    } as TimelineOperation

    const request = createTimelineApplyRequest(
      timelineRequestId,
      timelineProjectId,
      3,
      [adversarial],
    )

    expect(request.params.operations).toEqual([
      { kind: 'delete', elementId: timelineSourceElementId },
    ])
    expect(JSON.stringify(request)).not.toContain('command')
    expect(JSON.stringify(request)).not.toContain('private.mov')
  })

  it('parses and freezes a contiguous exact snapshot', () => {
    const read = parseTimelineReadResponse(createTimelineReadResponseValue())
    const apply = parseTimelineApplyResponse(createTimelineApplyResponseValue())

    expect(read?.result.status).toBe('available')
    expect(apply?.result.status).toBe('available')
    if (read?.result.status !== 'available') {
      throw new Error('Expected an available timeline snapshot.')
    }
    expect(read.result.snapshot.elements[0]?.sourceRange?.start).toEqual({
      value: '-1',
      rate: '10',
    })
    expect(Object.isFrozen(read.result.snapshot)).toBe(true)
    expect(Object.isFrozen(read.result.snapshot.elements)).toBe(true)
    expect(Object.isFrozen(read.result.snapshot.elements[0]?.origin)).toBe(true)
  })

  it('rejects extended, discontinuous, aliased, or noncanonical snapshots', () => {
    const mutations: Array<(value: ReturnType<typeof mutableRead>) => void> = [
      (value) => {
        value.extra = true
      },
      (value) => {
        value.result.snapshot.elements[0]!.extra = true
      },
      (value) => {
        value.result.snapshot.elements[1]!.outputRange.start = {
          value: '1',
          rate: '2',
        }
      },
      (value) => {
        value.result.snapshot.elements[1]!.id = timelineSourceElementId
      },
      (value) => {
        value.result.snapshot.elements[0]!.outputRange.duration = {
          value: '2',
          rate: '2',
        }
      },
      (value) => {
        value.result.snapshot.elements[0]!.sourceRange = null
      },
      (value) => {
        value.result.snapshot.elements[1]!.sourceRange = {
          start: { value: '0', rate: '1' },
          duration: { value: '1', rate: '2' },
        }
      },
      (value) => {
        value.result.snapshot.updatedAt = '2026-08-31T02:03:04.0000000+00:00'
      },
      (value) => {
        value.result.snapshot.createdAt = '2026-02-31T02:03:04.0000000+00:00'
      },
      (value) => {
        value.result.snapshot.updatedAt += '\n'
      },
      (value) => {
        value.result.snapshot.elements[0]!.outputRange.duration = {
          value: '9223372036854775808',
          rate: '1',
        }
      },
    ]

    for (const mutate of mutations) {
      const value = mutableRead()
      mutate(value)
      expect(parseTimelineReadResponse(value)).toBeNull()
    }
  })

  it('requires status and nullable snapshot to agree exactly', () => {
    const denied = {
      version: 1,
      requestId: timelineRequestId,
      method: 'timeline.read',
      result: { status: 'forbidden', snapshot: null },
    }
    expect(parseTimelineReadResponse(denied)).toEqual(denied)
    expect(
      parseTimelineReadResponse({
        ...denied,
        result: { ...denied.result, actorSid: 'S-1-5-21-private' },
      }),
    ).toBeNull()
    expect(
      parseTimelineApplyResponse({
        ...denied,
        method: 'timeline.apply',
        result: { status: 'conflict', snapshot: null },
      }),
    ).not.toBeNull()
    expect(
      parseTimelineReadResponse({
        ...denied,
        result: {
          status: 'available',
          snapshot: createTimelineReadResponseValue().result.snapshot,
        },
      }),
    ).not.toBeNull()
  })
})

function mutableRead() {
  return JSON.parse(JSON.stringify(createTimelineReadResponseValue())) as {
    version: number
    requestId: string
    method: string
    extra?: boolean
    result: {
      status: string
      snapshot: {
        createdAt: string
        updatedAt: string
        elements: Array<{
          id: string
          extra?: boolean
          outputRange: {
            start: { value: string; rate: string }
            duration: { value: string; rate: string }
          }
          sourceRange: {
            start: { value: string; rate: string }
            duration: { value: string; rate: string }
          } | null
        }>
      }
    }
  }
}