import { describe, expect, it } from 'vitest'

import {
  LAYOUT_APPLY_METHOD,
  LAYOUT_MAX_REQUEST_UTF8_BYTES,
  LAYOUT_MAX_RESPONSE_UTF8_BYTES,
  createLayoutApplyRequest,
  createLayoutReadRequest,
  parseLayoutApplyResponse,
  parseLayoutReadResponse,
  type LayoutKeyframeInput,
  type LayoutManualOperation,
  type LayoutOperation,
} from './layoutProtocol'
import {
  createLayoutFailureValue,
  createLayoutResponseValue,
  layoutCompositionDigest,
  layoutCompositionId,
  layoutCueDigest,
  layoutCueId,
  layoutCueRevisionId,
  layoutProjectId,
  layoutRequestId,
  layoutSourceId,
} from './layoutTestFixture'

const zero = { numerator: '0', denominator: '1' } as const
const one = { numerator: '1', denominator: '1' } as const
const half = { numerator: '1', denominator: '2' } as const
const fullRect = { x: zero, y: zero, width: one, height: one } as const

describe('layout bridge protocol', () => {
  it('creates closed read and no-face manual requests within the 64 KiB envelope', () => {
    const read = createLayoutReadRequest(layoutRequestId, layoutProjectId, null)
    const adversarial = {
      kind: 'setManualLayout',
      mode: 'singleSubjectCrop',
      staticCropDecision: 'singleStaticCropSufficient',
      sources: [
        {
          sourceAssetId: layoutSourceId,
          crop: fullRect,
          subject: { x: zero, y: zero, width: half, height: half },
          destination: fullRect,
          faceCandidateId: 'not-authoritative',
          provider: 'not-authoritative',
        },
      ],
      templateId: null,
      actorSid: 'S-1-5-21-private',
      sourcePath: 'C:\\private\\source.mov',
    } as unknown as LayoutManualOperation
    const apply = createLayoutApplyRequest(
      layoutRequestId,
      layoutProjectId,
      3,
      adversarial,
    )

    expect(read).toEqual({
      version: 1,
      requestId: layoutRequestId,
      method: 'layout.read',
      params: { projectId: layoutProjectId, evaluationTime: null },
    })
    expect(apply.method).toBe(LAYOUT_APPLY_METHOD)
    expect(apply.params.operation).toEqual({
      kind: 'setManualLayout',
      mode: 'singleSubjectCrop',
      staticCropDecision: 'singleStaticCropSufficient',
      sources: [{
        sourceAssetId: layoutSourceId,
        crop: fullRect,
        subject: { x: zero, y: zero, width: half, height: half },
        destination: fullRect,
      }],
      templateId: null,
    })
    expect(JSON.stringify(apply)).not.toMatch(/actorSid|sourcePath|provider|faceCandidate/i)
    expect(new TextEncoder().encode(JSON.stringify(apply)).byteLength)
      .toBeLessThanOrEqual(LAYOUT_MAX_REQUEST_UTF8_BYTES)
    expect(Object.isFrozen(apply.params.operation)).toBe(true)
    if (apply.params.operation.kind !== 'setManualLayout') {
      throw new Error('Expected a manual layout operation.')
    }
    expect(Object.isFrozen(apply.params.operation.sources)).toBe(true)
  })

  it('rejects invented modes, wrong source arity, templates, and noncanonical rationals', () => {
    const base = {
      kind: 'setManualLayout',
      mode: 'singleSubjectCrop',
      staticCropDecision: 'singleStaticCropSufficient',
      sources: [{
        sourceAssetId: layoutSourceId,
        crop: fullRect,
        subject: fullRect,
        destination: fullRect,
      }],
      templateId: null,
    } as const
    const invalid: LayoutOperation[] = [
      { ...base, mode: 'automaticFaceCrop' } as unknown as LayoutOperation,
      { ...base, sources: [] } as unknown as LayoutOperation,
      { ...base, templateId: layoutSourceId } as unknown as LayoutOperation,
      {
        ...base,
        sources: [{
          ...base.sources[0],
          crop: { ...fullRect, width: { numerator: '2', denominator: '2' } },
        }],
      } as unknown as LayoutOperation,
      {
        ...base,
        sources: [{
          ...base.sources[0],
          crop: { ...fullRect, x: { numerator: '-1', denominator: '2' } },
        }],
      } as unknown as LayoutOperation,
    ]

    for (const operation of invalid) {
      expect(() => createLayoutApplyRequest(
        layoutRequestId,
        layoutProjectId,
        0,
        operation,
      )).toThrow()
    }
  })

  it('enforces ordered exact keyframes and the aggregate 256-keyframe limit', () => {
    const keyframe = (value: number): LayoutKeyframeInput => ({
      time: { value: String(value), rate: '1' },
      sourceCrop: fullRect,
      destination: fullRect,
      interpolation: value === 256 ? 'hold' : 'linear',
    })
    const operation = {
      kind: 'setKeyframes',
      sourceAssetId: layoutSourceId,
      destinationSlot: 'singleSubject',
      segments: [{
        range: {
          start: { value: '0', rate: '1' },
          duration: { value: '256', rate: '1' },
        },
        keyframes: Array.from({ length: 257 }, (_, index) => keyframe(index)),
      }],
      evaluationTime: { value: '1', rate: '1' },
    } as const

    expect(() => createLayoutApplyRequest(
      layoutRequestId,
      layoutProjectId,
      0,
      operation,
    )).toThrow('256')

    const nonReduced = {
      ...operation,
      segments: [{
        range: {
          start: { value: '0', rate: '1' },
          duration: { value: '2', rate: '1' },
        },
        keyframes: [
          { ...keyframe(0), time: { value: '0', rate: '1' } },
          { ...keyframe(2), time: { value: '2', rate: '2' } },
        ],
      }],
    } as unknown as LayoutOperation
    expect(() => createLayoutApplyRequest(
      layoutRequestId,
      layoutProjectId,
      0,
      nonReduced,
    )).toThrow()
  })

  it('creates immutable cue edits pinned to aggregate and cue OCC evidence', () => {
    const request = createLayoutApplyRequest(
      layoutRequestId,
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

    expect(request.params.expectedRevision).toBe(3)
    expect(request.params.operation).toEqual({
      kind: 'editSubtitle',
      cueId: layoutCueId,
      expectedRevisionId: layoutCueRevisionId,
      expectedRevisionDigest: layoutCueDigest,
      edit: { kind: 'text', text: '更新字幕' },
      reason: '字幕を修正します。',
    })
    expect(Object.isFrozen(request.params.operation)).toBe(true)
    if (request.params.operation.kind !== 'editSubtitle') {
      throw new Error('Expected a subtitle edit.')
    }
    expect(Object.isFrozen(request.params.operation.edit)).toBe(true)
  })

  it('parses and deeply freezes native 9:16 pixels, cues, and variant evidence', () => {
    const parsed = parseLayoutReadResponse(createLayoutResponseValue())

    expect(parsed?.result.status).toBe('available')
    if (parsed === null || parsed.result.status !== 'available') {
      throw new Error('Expected an available layout snapshot.')
    }
    const snapshot = parsed.result.snapshot
    expect(snapshot.output).toEqual({
      width: 1080,
      height: 1920,
      aspectWidth: 9,
      aspectHeight: 16,
    })
    expect(snapshot.manualSelection?.placements[0]?.sourceCrop).toEqual({
      rect: {
        x: { numerator: '1', denominator: '3' },
        y: zero,
        width: { numerator: '2', denominator: '3' },
        height: one,
      },
      pixels: { x: 360, y: 0, width: 720, height: 1920 },
    })
    expect(snapshot.keyframeTracks[0]?.evaluation.destinationPixels)
      .toEqual({ x: 270, y: 480, width: 540, height: 960 })
    expect(snapshot.subtitles[0]?.layoutResolution.status).toBe('resolved')
    expect(snapshot.subtitleDisplays).toHaveLength(4)
    expect(snapshot.subtitleDisplays[1]).toMatchObject({
      cueMode: 'full',
      punctuationDisplay: 'hideJapaneseCommaAndPeriod',
      displayedCues: [{
        cueId: layoutCueId,
        text: '字幕テキストです',
        textChanged: true,
      }],
    })
    expect(snapshot.terops[0]?.sourceKind).toBe('manualEditorial')
    expect(snapshot.variantPair?.compositionVersionId).toBe(layoutCompositionId)
    expect(snapshot.variantPair?.compositionVersionDigest).toBe(layoutCompositionDigest)
    expect(Object.isFrozen(snapshot)).toBe(true)
    expect(Object.isFrozen(snapshot.layout.safeZones[0]?.rect.x)).toBe(true)
    expect(Object.isFrozen(snapshot.manualSelection?.placements)).toBe(true)
    expect(Object.isFrozen(snapshot.keyframeTracks[0]?.segments[0]?.keyframes)).toBe(true)
    expect(Object.isFrozen(snapshot.subtitles[0]?.layoutResolution.evaluations)).toBe(true)
    expect(Object.isFrozen(snapshot.subtitleDisplays[1]?.displayedCues)).toBe(true)
    expect(Object.isFrozen(snapshot.variantPair?.subtitleFree)).toBe(true)
  })

  it('rejects browser-rounded geometry, wrong aspect, unsafe envelope, and stale variants', () => {
    const cases: Array<(value: MutableLayoutResponse) => void> = [
      (value) => {
        value.result.snapshot.output.aspectWidth = 16
        value.result.snapshot.output.aspectHeight = 9
      },
      (value) => {
        value.result.snapshot.layout.safeZones[0]!.name = 'guessed-safe-zone'
      },
      (value) => {
        value.result.snapshot.manualSelection!.placements[0]!
          .sourceCrop.pixels.x = 359
      },
      (value) => {
        ;(value.result.snapshot.layout.regions[1]!.rect as unknown as {
          y: { numerator: string; denominator: string }
        }).y = {
          numerator: '7500001',
          denominator: '10000000',
        }
      },
      (value) => {
        value.result.snapshot.variantPair!.compositionVersionDigest = '1'.repeat(64)
      },
      (value) => {
        value.result.snapshot.subtitleDisplays[1]!.displayedCues[0]!.text =
          'native policy と一致しない表示'
      },
      (value) => {
        value.result.snapshot.variantPair!.displayProfileDigest = '9'.repeat(64)
      },
      (value) => {
        value.result.snapshot.subtitleDisplays[1]!.sourceCueCount = 2
      },
      (value) => {
        value.result.snapshot.subtitleDisplays[1]!.displayedCues[0]!
          .textChanged = false
      },
      (value) => {
        value.result.snapshot.subtitleDisplays[2]!.selectedCueIds = [
          'aa112222-3333-4444-8555-666677778888',
        ]
      },
      (value) => {
        const duplicate = value.result.snapshot.subtitleDisplays[3]!
        duplicate.cueMode = 'full'
        duplicate.selectedCueIds = []
      },
      (value) => {
        value.result.snapshot.subtitleDisplays[3]!.profileVersionDigest =
          value.result.snapshot.subtitleDisplays[2]!.profileVersionDigest
      },
      (value) => {
        value.result.snapshot.layout.regions[2]!.role = 'video'
      },
      (value) => {
        ;(value.result.snapshot.subtitles[0]!.layoutResolution as unknown as {
          selectedCandidateId: string | null
        }).selectedCandidateId = null
      },
      (value) => {
        ;(value.result.snapshot.keyframeTracks[0]!.evaluation as unknown as {
          destination: typeof fullRect
        }).destination = fullRect
        value.result.snapshot.keyframeTracks[0]!.evaluation.destinationPixels = {
          x: 0,
          y: 0,
          width: 1080,
          height: 1920,
        }
      },
    ]

    for (const mutate of cases) {
      const value = cloneLayoutValue()
      mutate(value)
      expect(parseLayoutReadResponse(value)).toBeNull()
    }
  })

  it('accepts native-equivalent omission when punctuation removal leaves no text', () => {
    const value = cloneLayoutValue()
    value.result.snapshot.subtitles[0]!.text = '、。､｡'
    for (const display of value.result.snapshot.subtitleDisplays) {
      if (display.punctuationDisplay === 'preserve') {
        display.displayedCues[0]!.text = '、。､｡'
        display.displayedCues[0]!.textChanged = false
      } else {
        const hidden = display as unknown as {
          displayedCues: unknown[]
          omittedCueIds: string[]
        }
        hidden.displayedCues = []
        hidden.omittedCueIds = [layoutCueId]
      }
    }

    const parsed = parseLayoutReadResponse(value)

    expect(parsed?.result.status).toBe('available')
    if (parsed === null || parsed.result.status !== 'available') {
      throw new Error('Expected punctuation-only display evidence to parse.')
    }
    expect(parsed.result.snapshot.subtitleDisplays[1]?.displayedCues).toEqual([])
    expect(parsed.result.snapshot.subtitleDisplays[1]?.omittedCueIds)
      .toEqual([layoutCueId])
  })

  it('uses .NET rather than ECMAScript trim semantics for display evidence', () => {
    const value = cloneLayoutValue()
    const sourceText = '\uFEFF字幕、です。'
    value.result.snapshot.subtitles[0]!.text = sourceText
    for (const display of value.result.snapshot.subtitleDisplays) {
      const projected = display.punctuationDisplay === 'preserve'
        ? sourceText
        : '\uFEFF字幕です'
      display.displayedCues[0]!.text = projected
      display.displayedCues[0]!.textChanged = projected !== sourceText
    }

    expect(parseLayoutReadResponse(value)?.result.status).toBe('available')
  })

  it('accepts only closed failures and rejects private or oversized response data', () => {
    for (const status of [
      'forbidden',
      'notFound',
      'conflict',
      'noChange',
      'invalid',
      'unavailable',
    ] as const) {
      const failure = createLayoutFailureValue(status)
      expect(parseLayoutReadResponse(failure)).toEqual(failure)
      expect(parseLayoutReadResponse({
        ...failure,
        result: { ...failure.result, snapshot: {} },
      })).toBeNull()
    }

    const privateValue = cloneLayoutValue()
    ;(privateValue.result.snapshot as unknown as Record<string, unknown>)
      .actorSid = 'S-1-5-21-private'
    expect(parseLayoutReadResponse(privateValue)).toBeNull()

    const oversized = cloneLayoutValue()
    oversized.result.snapshot.templates[0]!.name =
      'x'.repeat(LAYOUT_MAX_RESPONSE_UTF8_BYTES)
    expect(parseLayoutReadResponse(oversized)).toBeNull()
  })

  it('routes responses only through their exact read/apply parser', () => {
    const apply = createLayoutResponseValue(layoutRequestId, LAYOUT_APPLY_METHOD)

    expect(parseLayoutApplyResponse(apply)?.method).toBe(LAYOUT_APPLY_METHOD)
    expect(parseLayoutReadResponse(apply)).toBeNull()
  })
})

type MutableLayoutResponse = ReturnType<typeof createLayoutResponseValue>

function cloneLayoutValue(): MutableLayoutResponse {
  return JSON.parse(JSON.stringify(createLayoutResponseValue())) as MutableLayoutResponse
}
