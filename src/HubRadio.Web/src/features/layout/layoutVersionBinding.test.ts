import { describe, expect, it } from 'vitest'

import { startLayoutBridgeSession, type LayoutBridgeClient } from './layoutBridgeSession'
import {
  createLayoutApplyRequest,
  createLayoutReadRequest,
  hasTargetSubtitleEvidence,
  layoutChildSnapshotMatchesOperation,
  layoutResponseMatchesRequest,
  layoutSnapshotVersion,
  parseLayoutBridgeResponse,
  type LayoutBridgeResponse,
  type LayoutOperation,
  type LayoutSnapshot,
  type LayoutVersionContext,
} from './layoutProtocol'
import { layoutBridgeUnavailable, type LayoutUiAction } from './layoutState'
import {
  createLayoutResponseValue,
  layoutCompositionDigest,
  layoutCompositionId,
  layoutCueDigest,
  layoutCueId,
  layoutCueRevisionId,
  layoutProjectId,
  layoutRequestId,
  layoutSecondRequestId,
} from './layoutTestFixture'

const first: LayoutVersionContext = {
  projectId: layoutProjectId, versionId: layoutCompositionId, versionDigest: layoutCompositionDigest,
}
const second: LayoutVersionContext = {
  projectId: layoutProjectId, versionId: '32112222-3333-4444-8555-666677778888', versionDigest: '9'.repeat(64),
}

describe('exact layout version binding', () => {
  it('keeps legacy reads unbound and serializes both exact target fields for two short versions', () => {
    expect(createLayoutReadRequest(layoutRequestId, layoutProjectId, null).params).toEqual({
      projectId: layoutProjectId, evaluationTime: null,
    })
    for (const target of [first, second]) {
      expect(createLayoutReadRequest(layoutRequestId, layoutProjectId, null, target).params).toEqual({
        projectId: layoutProjectId, evaluationTime: null,
        expectedVersionId: target.versionId, expectedVersionDigest: target.versionDigest,
      })
    }
  })

  it('pins mutation parameters without changing the cue operation contract', () => {
    const operation = {
      kind: 'editSubtitle' as const, cueId: layoutCueId, expectedRevisionId: layoutCueRevisionId,
      expectedRevisionDigest: layoutCueDigest, edit: { kind: 'text' as const, text: '修正字幕' }, reason: '本文修正',
    }
    expect(createLayoutApplyRequest(layoutRequestId, layoutProjectId, 3, operation, second).params).toEqual({
      projectId: layoutProjectId, expectedRevision: 3, operation,
      expectedVersionId: second.versionId, expectedVersionDigest: second.versionDigest,
    })
    expect(createLayoutApplyRequest(layoutRequestId, layoutProjectId, 3, operation).params).not.toHaveProperty('expectedVersionId')
  })

  it('rejects incomplete or cross-project targets rather than generating placeholder fields', () => {
    expect(() => createLayoutReadRequest(layoutRequestId, layoutProjectId, null, { ...first, versionDigest: '' })).toThrow()
    expect(() => createLayoutReadRequest(layoutRequestId, layoutProjectId, null, { ...first, versionId: '' })).toThrow()
    expect(() => createLayoutReadRequest(layoutRequestId, layoutProjectId, null, { ...first, projectId: second.versionId })).toThrow()
  })

  it('requires exact snapshot and cue identity, not an empty subtitle panel', () => {
    const response = parseLayoutBridgeResponse(createLayoutResponseValue())
    if (response?.result.status !== 'available') throw new Error('Invalid fixture')
    const snapshot = response.result.snapshot
    expect(hasTargetSubtitleEvidence(snapshot, first)).toBe(true)
    expect(hasTargetSubtitleEvidence(snapshot, second)).toBe(false)
    expect(hasTargetSubtitleEvidence({ ...snapshot, subtitles: [] }, first)).toBe(false)
    expect(hasTargetSubtitleEvidence({ ...snapshot, subtitles: snapshot.subtitles.map(cue => ({
      ...cue, compositionVersionId: second.versionId,
    })) }, first)).toBe(false)
  })

  it.each(['editSubtitle', 'editTerop'] as const)('accepts only known unmodified cues inherited by a correlated %s child', kind => {
    const response = parseLayoutBridgeResponse(createLayoutResponseValue())
    if (response?.result.status !== 'available') throw new Error('Invalid fixture')
    const previous = withSecondSubtitle(response.result.snapshot)
    const before = kind === 'editSubtitle' ? previous.subtitles[0]! : previous.terops[0]!
    const operation: Extract<LayoutOperation, { kind: 'editSubtitle' | 'editTerop' }> = {
      kind, cueId: before.cueId, expectedRevisionId: before.revisionId, expectedRevisionDigest: before.revisionDigest,
      edit: { kind: 'style', style: 'quote' }, reason: '選択した cue だけを修正',
    }
    const revise = <T extends typeof before>(cue: T): T => cue.cueId !== operation.cueId ? cue : {
      ...cue, compositionVersionId: second.versionId, revisionId: '85112222-3333-4444-8555-666677778888',
      revisionDigest: '7'.repeat(64), revisionCount: cue.revisionCount + 1, style: 'quote',
    }
    const child: LayoutSnapshot = {
      ...previous, revision: previous.revision + 1, compositionVersionId: second.versionId, variantPair: null,
      subtitles: kind === 'editSubtitle' ? previous.subtitles.map(revise) : previous.subtitles,
      terops: kind === 'editTerop' ? previous.terops.map(revise) : previous.terops,
    }
    const target = layoutSnapshotVersion(child)
    expect(child.subtitles).toHaveLength(2)
    expect(child.subtitles[1]).toEqual(previous.subtitles[1])
    expect(child.compositionVersionDigest).toBe(first.versionDigest)
    expect(layoutChildSnapshotMatchesOperation(child, previous, operation)).toBe(true)
    expect(hasTargetSubtitleEvidence(child, target)).toBe(false)
    expect(hasTargetSubtitleEvidence(child, target, child)).toBe(true)
    expect(hasTargetSubtitleEvidence(child, target, previous)).toBe(false)

    for (const invalid of [
      { ...child, compositionVersionDigest: second.versionDigest },
      { ...child, subtitles: [child.subtitles[0]!] },
      { ...child, subtitles: [child.subtitles[0]!, child.subtitles[0]!] },
      ...[
        { compositionVersionId: child.compositionVersionId },
        { compositionVersionId: '34112222-3333-4444-8555-666677778888' },
        { revisionId: '86112222-3333-4444-8555-666677778888' },
        { revisionDigest: '6'.repeat(64) },
        { text: '操作していない本文の改ざん' },
        { cueId: '73112222-3333-4444-8555-666677778888' },
      ].map(change => ({ ...child, subtitles: [child.subtitles[0]!, { ...child.subtitles[1]!, ...change }] })),
      { ...child, terops: child.terops.map(cue => ({ ...cue, sourceKind: 'adoptedSuggestion' as const,
        ...(kind === 'editTerop' ? { revisionCount: cue.revisionCount + 1 } : {}) })) },
    ]) {
      expect(layoutChildSnapshotMatchesOperation(invalid, previous, operation)).toBe(false)
      expect(hasTargetSubtitleEvidence(invalid, target, child)).toBe(false)
    }
    expect(layoutChildSnapshotMatchesOperation(child, previous, {
      ...operation, expectedRevisionDigest: '6'.repeat(64),
    })).toBe(false)
    expect(layoutChildSnapshotMatchesOperation(child, previous, {
      ...operation, edit: { kind: 'style', style: 'normal' },
    })).toBe(false)

    // The next edit still inherits the untouched cue from the original version,
    // not from the immediate preceding child's ID.
    const edited = kind === 'editSubtitle' ? child.subtitles[0]! : child.terops[0]!
    const nextOperation: Extract<LayoutOperation, { kind: 'editSubtitle' | 'editTerop' }> = {
      ...operation, expectedRevisionId: edited.revisionId, expectedRevisionDigest: edited.revisionDigest,
      edit: { kind: 'style', style: 'caution' },
    }
    const nextCue = <T extends typeof edited>(cue: T): T => cue.cueId !== nextOperation.cueId ? cue : {
      ...cue, compositionVersionId: '34112222-3333-4444-8555-666677778888',
      revisionId: '86112222-3333-4444-8555-666677778888', revisionDigest: '6'.repeat(64),
      revisionCount: cue.revisionCount + 1, style: 'caution',
    }
    const next: LayoutSnapshot = {
      ...child, compositionVersionId: '34112222-3333-4444-8555-666677778888', revision: child.revision + 1,
      subtitles: kind === 'editSubtitle' ? child.subtitles.map(nextCue) : child.subtitles,
      terops: kind === 'editTerop' ? child.terops.map(nextCue) : child.terops,
    }
    expect(layoutChildSnapshotMatchesOperation(next, child, nextOperation)).toBe(true)
    const reusedAncestor = {
      ...next, compositionVersionId: previous.compositionVersionId,
      subtitles: next.subtitles.map(cue => cue.compositionVersionId === next.compositionVersionId
        ? { ...cue, compositionVersionId: previous.compositionVersionId } : cue),
      terops: next.terops.map(cue => cue.compositionVersionId === next.compositionVersionId
        ? { ...cue, compositionVersionId: previous.compositionVersionId } : cue),
    }
    expect(layoutChildSnapshotMatchesOperation(reusedAncestor, child, nextOperation)).toBe(false)
  })

  it('accepts a fresh exact read of a registered mixed-cue child without treating a different snapshot version as current', () => {
    const response = parseLayoutBridgeResponse(createLayoutResponseValue())
    if (response?.result.status !== 'available') throw new Error('Invalid fixture')
    const previous = withSecondSubtitle(response.result.snapshot)
    const child: LayoutSnapshot = {
      ...previous, revision: previous.revision + 1, compositionVersionId: second.versionId, variantPair: null,
      subtitles: previous.subtitles.map((cue, index) => index === 0 ? {
        ...cue, compositionVersionId: second.versionId, revisionId: '85112222-3333-4444-8555-666677778888',
        revisionDigest: '7'.repeat(64), revisionCount: cue.revisionCount + 1, style: 'quote',
      } : cue),
    }
    const target = layoutSnapshotVersion(child)
    const read = createLayoutReadRequest(layoutRequestId, layoutProjectId, null, target)
    const exact = { ...response, result: { status: 'available' as const, snapshot: child } }
    expect(layoutResponseMatchesRequest(exact, read)).toBe(true)
    expect(hasTargetSubtitleEvidence(child, target, child)).toBe(true)
    expect(layoutResponseMatchesRequest(response, read)).toBe(false)
    expect(hasTargetSubtitleEvidence(previous, target, child)).toBe(false)
    expect(hasTargetSubtitleEvidence({ ...child, compositionVersionDigest: '9'.repeat(64) }, target, child)).toBe(false)
  })

  it('refuses a same-project wrong-version read and rejects its late response during the next read', () => {
    const actions: LayoutUiAction[] = []
    let listener: ((response: LayoutBridgeResponse) => void) | undefined
    const client: LayoutBridgeClient = {
      sendLayout() {}, abandonLayoutRequest() {}, dispose() {},
      onLayoutResponse(value) { listener = value; return () => { listener = undefined } },
    }
    const session = startLayoutBridgeSession(() => client, action => actions.push(action))
    session.send(createLayoutReadRequest(layoutRequestId, layoutProjectId, null, second))
    const wrong = parseLayoutBridgeResponse(createLayoutResponseValue())
    if (wrong === null) throw new Error('Invalid fixture')
    listener?.(wrong)
    expect(actions.at(-1)).toEqual(layoutBridgeUnavailable())
    expect(session.send(createLayoutReadRequest(layoutSecondRequestId, layoutProjectId, null, first))).toBe(true)
    const count = actions.length
    listener?.(wrong)
    expect(actions).toHaveLength(count)
    const correct = parseLayoutBridgeResponse(createLayoutResponseValue(layoutSecondRequestId))
    if (correct === null) throw new Error('Invalid fixture')
    listener?.(correct)
    expect(actions.at(-1)).toEqual({ type: 'responseReceived', response: correct })
    session.dispose()
  })
})

function withSecondSubtitle(snapshot: LayoutSnapshot): LayoutSnapshot {
  const cue = {
    ...snapshot.subtitles[0]!, cueId: '72112222-3333-4444-8555-666677778888',
    revisionId: '82112222-3333-4444-8555-666677778888', revisionDigest: '5'.repeat(64), text: '継承する字幕',
    outputRange: { start: { value: '3', rate: '1' }, duration: { value: '1', rate: '1' } },
  }
  return {
    ...snapshot, subtitles: [...snapshot.subtitles, cue],
    subtitleDisplays: snapshot.subtitleDisplays.map(display => ({
      ...display, sourceCueCount: 2, displayedCues: display.cueMode === 'full'
        ? [...display.displayedCues, { cueId: cue.cueId, text: cue.text, textChanged: false }] : display.displayedCues,
    })),
  }
}