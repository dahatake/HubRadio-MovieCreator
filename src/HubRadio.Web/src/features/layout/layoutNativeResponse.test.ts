import { describe, expect, it } from 'vitest'

import {
  createLayoutApplyRequest,
  createLayoutReadRequest,
  layoutChildSnapshotMatchesOperation,
  layoutResponseMatchesRequest,
  layoutSnapshotVersion,
  layoutSupportsManualEditing,
  parseLayoutBridgeResponse,
  type LayoutBridgeResponse,
  type LayoutOperation,
} from './layoutProtocol'
import {
  initialLayoutState,
  layoutRequestStarted,
  layoutResponseReceived,
  layoutStateReducer,
} from './layoutState'
import {
  createLayoutResponseValue,
  createSubtitleOnlyLayoutResponseValue,
  layoutCompositionDigest,
  layoutCompositionId,
  layoutCueDigest,
  layoutCueId,
  layoutCueRevisionId,
  layoutProjectId,
  layoutRequestId,
  layoutSecondRequestId,
  layoutTeropDigest,
  layoutTeropId,
  layoutTeropRevisionId,
} from './layoutTestFixture'

const target = { projectId: layoutProjectId, versionId: layoutCompositionId, versionDigest: layoutCompositionDigest }
const childId = '32112222-3333-4444-8555-666677778888'

describe('native layout response shapes and operation correlation', () => {
  it.each([true, false])('accepts native subtitle-only evidence (landscape=%s) without inventing geometry', landscape => {
    const value = createSubtitleOnlyLayoutResponseValue(layoutRequestId, landscape)
    const snapshot = available(value)
    expect(snapshot).toEqual(value.result.snapshot)
    expect(snapshot.subtitleOnly).toBe(true)
    expect(snapshot.sources).toEqual([])
    expect(snapshot.manualSelection).toBeNull()
    expect(snapshot.keyframeTracks).toEqual([])
    expect(layoutSupportsManualEditing(snapshot)).toBe(false)
    expect(Object.isFrozen(snapshot.output)).toBe(true)
    expect(Object.isFrozen(snapshot.subtitles[0]?.layoutResolution.evaluations)).toBe(true)
  })

  it('keeps legacy vertical parsing closed and permits empty subtitle-only projections', () => {
    expect(layoutSupportsManualEditing(available(createLayoutResponseValue()))).toBe(true)
    const value = createSubtitleOnlyLayoutResponseValue()
    const snapshot = {
      ...value.result.snapshot, subtitles: [], subtitleDisplays: [], terops: [],
      layout: { ...value.result.snapshot.layout, regions: [] },
    }
    expect(parseLayoutBridgeResponse({ ...value, result: { ...value.result, snapshot } })).not.toBeNull()
  })

  it('rejects unknown fields, false/null flags, manual geometry and unflagged landscape', () => {
    const value = createSubtitleOnlyLayoutResponseValue()
    const snapshot = value.result.snapshot
    const legacy = createLayoutResponseValue().result.snapshot
    for (const invalid of [
      { ...snapshot, parentVersionId: layoutCompositionId },
      { ...snapshot, actor: 'not-a-contract-field' },
      { ...snapshot, subtitleOnly: false },
      { ...snapshot, subtitleOnly: null },
      { ...snapshot, manualSelection: legacy.manualSelection },
      { ...snapshot, keyframeTracks: legacy.keyframeTracks },
      { ...snapshot, layout: { ...snapshot.layout, regions: legacy.layout.regions } },
      { ...snapshot, output: { ...snapshot.output, extra: true } },
    ]) {
      expect(parseLayoutBridgeResponse({ ...value, result: { ...value.result, snapshot: invalid } })).toBeNull()
    }
    const unflagged = { ...snapshot } as Record<string, unknown>
    delete unflagged.subtitleOnly
    expect(parseLayoutBridgeResponse({ ...value, result: { ...value.result, snapshot: unflagged } })).toBeNull()
  })

  it('refuses a correct ID with the wrong digest on a pinned read', () => {
    const response = required(createLayoutResponseValue())
    const request = createLayoutReadRequest(layoutRequestId, layoutProjectId, null, {
      ...target, versionDigest: '9'.repeat(64),
    })
    expect(layoutResponseMatchesRequest(response, request)).toBe(false)
  })

  it.each(['editSubtitle', 'editTerop'] as const)('accepts a real %s child without a nonexistent parent field', kind => {
    const operation: Extract<LayoutOperation, { kind: 'editSubtitle' | 'editTerop' }> = {
      kind, cueId: kind === 'editSubtitle' ? layoutCueId : layoutTeropId,
      expectedRevisionId: kind === 'editSubtitle' ? layoutCueRevisionId : layoutTeropRevisionId,
      expectedRevisionDigest: kind === 'editSubtitle' ? layoutCueDigest : layoutTeropDigest,
      edit: { kind: 'style', style: 'quote' }, reason: 'style を確認',
    }
    const request = createLayoutApplyRequest(layoutSecondRequestId, layoutProjectId, 3, operation, target)
    const value = createLayoutResponseValue(layoutSecondRequestId, 'layout.apply')
    const base = value.result.snapshot
    const revise = <T extends { compositionVersionId: string; revisionId: string; revisionDigest: string; revisionCount: number; style: string }>(cue: T) => ({
      ...cue, compositionVersionId: childId, revisionId: '85112222-3333-4444-8555-666677778888',
      revisionDigest: '7'.repeat(64), revisionCount: cue.revisionCount + 1, style: 'quote',
    })
    const snapshot = {
      ...base, revision: 4, compositionVersionId: childId,
      variantPair: null,
      subtitles: kind === 'editSubtitle' ? base.subtitles.map(cue => cue.cueId === operation.cueId ? revise(cue) : cue) : base.subtitles,
      terops: kind === 'editTerop' ? base.terops.map(cue => cue.cueId === operation.cueId ? revise(cue) : cue) : base.terops,
    }
    const response = required({ ...value, result: { status: 'available', snapshot } })
    expect(layoutResponseMatchesRequest(response, request)).toBe(true)
    expect(snapshot.compositionVersionDigest).toBe(request.params.expectedVersionDigest)
    expect(layoutChildSnapshotMatchesOperation(available({ ...value, result: { status: 'available', snapshot } }), available(value), operation)).toBe(true)
    expect(kind === 'editSubtitle' ? snapshot.terops : snapshot.subtitles).toEqual(kind === 'editSubtitle' ? base.terops : base.subtitles)
    expect(snapshot).not.toHaveProperty('parentVersionId')
    const wrongEdit = { ...operation, edit: { kind: 'style' as const, style: 'normal' as const } }
    expect(layoutResponseMatchesRequest(response, createLayoutApplyRequest(
      layoutSecondRequestId, layoutProjectId, 3, wrongEdit, target,
    ))).toBe(false)

    const read = createLayoutReadRequest(layoutRequestId, layoutProjectId, null, target)
    const loading = layoutStateReducer(initialLayoutState, layoutRequestStarted(
      read.requestId, read.method, layoutProjectId, { request: read, selection: target },
    ))
    const loaded = layoutStateReducer(loading, layoutResponseReceived(required(createLayoutResponseValue())))
    const pending = layoutStateReducer(loaded, layoutRequestStarted(
      request.requestId, request.method, layoutProjectId, { request },
    ))
    const applied = layoutStateReducer(pending, layoutResponseReceived(response))
    expect(applied.target).toEqual(target)
    expect(applied.lastVersionChange).toEqual({ source: target, created: layoutSnapshotVersion(available({ ...value, result: { status: 'available', snapshot } })) })
    expect(applied.snapshot?.compositionVersionId).toBe(childId)

    for (const invalid of [
      { ...snapshot, revision: 3 },
      { ...snapshot, compositionVersionId: layoutCompositionId },
      { ...snapshot, compositionVersionDigest: '8'.repeat(64) },
      { ...snapshot, subtitles: base.subtitles, terops: base.terops },
    ]) {
      expect(layoutResponseMatchesRequest(required({ ...value, result: { status: 'available', snapshot: invalid } }), request)).toBe(false)
    }
    const malicious = required({ ...value, result: { status: 'available', snapshot: { ...snapshot, compositionVersionDigest: '8'.repeat(64) } } })
    const rejected = layoutStateReducer(pending, layoutResponseReceived(malicious))
    expect(rejected.snapshot).toBe(loaded.snapshot)
    expect(rejected.target).toEqual(target)
    expect(rejected.lastVersionChange).toBeNull()
    expect(rejected.status).toBe('invalid')
    expect(rejected.error).not.toBeNull()
  })

  it('does not accept a child or changed digest for a version-preserving keyframe mutation', () => {
    const snapshot = available(createLayoutResponseValue())
    const track = snapshot.keyframeTracks[0]!
    const request = createLayoutApplyRequest(layoutSecondRequestId, layoutProjectId, 3, {
      kind: 'setKeyframes', sourceAssetId: track.sourceAssetId, destinationSlot: track.destinationSlot,
      segments: track.segments, evaluationTime: track.evaluation.requestedTime,
    }, target)
    const value = createLayoutResponseValue(layoutSecondRequestId, 'layout.apply')
    const revised = { ...snapshot, revision: 4, variantPair: null }
    expect(layoutResponseMatchesRequest(required({ ...value, result: { status: 'available', snapshot: revised } }), request)).toBe(true)
    for (const invalid of [
      { ...revised, compositionVersionId: childId },
      { ...revised, compositionVersionDigest: '8'.repeat(64) },
      { ...revised, revision: 5 },
    ]) {
      expect(layoutResponseMatchesRequest(required({ ...value, result: { status: 'available', snapshot: invalid } }), request)).toBe(false)
    }
  })
})

function required(value: unknown): LayoutBridgeResponse {
  const response = parseLayoutBridgeResponse(value)
  if (response === null) throw new Error('Invalid native-shaped fixture.')
  return response
}

function available(value: unknown) {
  const response = required(value)
  if (response.result.status !== 'available') throw new Error('Expected available evidence.')
  return response.result.snapshot
}