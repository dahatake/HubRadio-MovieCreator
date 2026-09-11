import { describe, expect, it } from 'vitest'

import { startLayoutBridgeSession, type LayoutBridgeClient } from './layoutBridgeSession'
import {
  createLayoutApplyRequest,
  createLayoutReadRequest,
  layoutResponseMatchesRequest,
  layoutSnapshotVersion,
  parseLayoutBridgeResponse,
  type LayoutBridgeResponse,
  type LayoutOperation,
  type LayoutSnapshot,
} from './layoutProtocol'
import { initialLayoutState, layoutHasPersistedSubtitles, layoutRequestStarted, layoutResponseReceived, layoutStateReducer } from './layoutState'
import {
  createLayoutResponseValue,
  layoutCompositionDigest,
  layoutCompositionId,
  layoutProjectId,
  layoutRequestId,
} from './layoutTestFixture'

const root = { projectId: layoutProjectId, versionId: layoutCompositionId, versionDigest: layoutCompositionDigest }
const latest = { latestInBranch: true } as const
const id = (sequence: number) => `00000000-0000-4000-8000-${sequence.toString().padStart(12, '0')}`

describe('durable subtitle head response contract', () => {
  it('adds only a true latest flag to a read with both pins, leaving exact and apply requests unchanged', () => {
    const request = createLayoutReadRequest(layoutRequestId, root.projectId, null, root, latest)
    expect(request.params).toEqual({ projectId: root.projectId, evaluationTime: null,
      expectedVersionId: root.versionId, expectedVersionDigest: root.versionDigest, latestInBranch: true })
    expect(createLayoutReadRequest(layoutRequestId, root.projectId, null, root).params).not.toHaveProperty('latestInBranch')
    expect(createLayoutApplyRequest(layoutRequestId, root.projectId, 0, operation(snapshot(0)), root).params)
      .not.toHaveProperty('latestInBranch')
    expect(() => createLayoutReadRequest(layoutRequestId, root.projectId, null, undefined, latest)).toThrow()
    for (const options of [{ latestInBranch: false }, { latestInBranch: null }, { latestInBranch: 1 },
      { latestInBranch: true, saved: true }, null]) {
      expect(() => Reflect.apply(createLayoutReadRequest, undefined, [layoutRequestId, root.projectId, null, root, options])).toThrow()
    }
  })

  it.each([0, 1, 2])('parses the exact five metadata keys and trusts persistence only for the matching positive row (%s)', sequence => {
    const response = required(value(sequence))
    if (response.result.status !== 'available') throw new Error('Expected available fixture.')
    expect(Object.keys(response.result.durableSubtitles!)).toEqual([
      'rootVersionId', 'headVersionId', 'headVersionDigest', 'sequence', 'journalDigest',
    ])
    expect(response.result.snapshot.persistence).toBe(sequence > 0 ? 'persisted' : 'notPersisted')
    expect(response.result.durableSubtitles?.journalDigest === null).toBe(sequence === 0)
    expect(Object.isFrozen(response.result.durableSubtitles)).toBe(true)
    expect(layoutResponseMatchesRequest(response, createLayoutReadRequest(layoutRequestId, root.projectId, null, root, latest))).toBe(true)
  })

  it('keeps exact historical snapshots separate from current branch metadata', () => {
    const head = value(2)
    const historical = required({ ...head, result: { ...head.result, snapshot: snapshot(0) } })
    expect(layoutResponseMatchesRequest(historical, createLayoutReadRequest(layoutRequestId, root.projectId, null, root))).toBe(true)
    expect(layoutResponseMatchesRequest(historical, createLayoutReadRequest(layoutRequestId, root.projectId, null, root, latest))).toBe(false)
    expect(layoutResponseMatchesRequest(required(head), createLayoutReadRequest(layoutRequestId, root.projectId, null, root))).toBe(false)
    expect(historical.result.snapshot?.persistence).toBe('notPersisted')
  })

  it('keeps metadata and snapshot objects closed and rejects unproved saved flags', () => {
    const good = value(1)
    const metadata = good.result.durableSubtitles
    for (const durableSubtitles of [null, {}, { ...metadata, saved: true }, { ...metadata, sequence: -1 },
      { ...metadata, sequence: 1.5 }, { ...metadata, sequence: 2147483648 }, { ...metadata, journalDigest: null },
      { ...metadata, journalDigest: 'A'.repeat(64) }, { ...metadata, rootVersionId: metadata.headVersionId },
      { ...metadata, headVersionDigest: 'A'.repeat(64) }]) {
      expect(parseLayoutBridgeResponse({ ...good, result: { ...good.result, durableSubtitles } })).toBeNull()
    }
    const zero = value(0)
    for (const durableSubtitles of [{ ...zero.result.durableSubtitles, journalDigest: 'a'.repeat(64) },
      { ...zero.result.durableSubtitles, headVersionId: id(50) }]) {
      expect(parseLayoutBridgeResponse({ ...zero, result: { ...zero.result, durableSubtitles } })).toBeNull()
    }
    for (const result of [
      { status: 'available', snapshot: good.result.snapshot },
      { ...good.result, saved: true },
      { ...good.result, snapshot: { ...good.result.snapshot, saved: true } },
      { ...good.result, snapshot: { ...good.result.snapshot, persistence: 'saved' } },
      { ...good.result, snapshot: { ...good.result.snapshot, persistence: 'notPersisted' } },
      { ...good.result, snapshot: { ...good.result.snapshot, revision: 2 } },
      { ...good.result, snapshot: { ...good.result.snapshot, compositionVersionId: id(50) } },
      { ...good.result, durableSubtitles: { ...metadata, headVersionDigest: '9'.repeat(64) } },
      { status: 'unavailable', snapshot: null, durableSubtitles: metadata },
    ]) expect(parseLayoutBridgeResponse({ ...good, result })).toBeNull()
  })

  it('rejects a different branch, changed content digest or old head even if the row is internally coherent', () => {
    const request = createLayoutReadRequest(layoutRequestId, root.projectId, null, root, latest)
    const good = value(1)
    const wrongRoot = required({ ...good, result: { ...good.result,
      durableSubtitles: { ...good.result.durableSubtitles, rootVersionId: id(50) } } })
    const wrongDigest = required({ ...good, result: { ...good.result,
      snapshot: { ...good.result.snapshot, compositionVersionDigest: '9'.repeat(64) },
      durableSubtitles: { ...good.result.durableSubtitles, headVersionDigest: '9'.repeat(64) } } })
    expect(layoutResponseMatchesRequest(wrongRoot, request)).toBe(false)
    expect(layoutResponseMatchesRequest(wrongDigest, request)).toBe(false)
    expect(layoutResponseMatchesRequest(required(good), request, { root, sequence: 2 })).toBe(false)
    expect(layoutResponseMatchesRequest(required(good), request, { root, sequence: 1 })).toBe(true)
    const childRead = createLayoutReadRequest(layoutRequestId, root.projectId, null, layoutSnapshotVersion(snapshot(1)), latest)
    // An arbitrary requested child cannot declare its own generated root.
    expect(layoutResponseMatchesRequest(required(good), childRead)).toBe(false)
    expect(layoutResponseMatchesRequest(required(good), childRead, { root, sequence: 1 })).toBe(true)
  })

  it('allows the committed apply/replay row below the known latest sequence without lowering that floor', () => {
    const h = sessionHarness()
    h.session.send(createLayoutReadRequest(layoutRequestId, root.projectId, null, root), root)
    const history = value(3)
    h.emit({ ...history, result: { ...history.result, snapshot: snapshot(0) } })
    const apply = createLayoutApplyRequest(id(101), root.projectId, 0, operation(snapshot(0)), root)
    h.session.send(apply)
    h.emit(value(1, apply.requestId, 'layout.apply'))
    expect(h.state().snapshot?.compositionVersionId).toBe(id(1))
    expect(h.state().branch).toEqual({ root, sequence: 3 })
    expect(layoutHasPersistedSubtitles(h.state())).toBe(true)
    const read = createLayoutReadRequest(id(102), root.projectId, null, root, latest)
    h.session.send(read)
    h.emit(value(2, read.requestId))
    expect(h.state().status).toBe('invalid')
    expect(h.state().snapshot?.compositionVersionId).toBe(id(1))
    expect(h.state().branch).toEqual({ root, sequence: 3 })
    h.session.dispose()
  })

  it('retains the original root over child ACKs and rejects a child pretending to be its own root', () => {
    for (const wrongRoot of [false, true]) {
      const h = sessionHarness()
      h.session.send(createLayoutReadRequest(layoutRequestId, root.projectId, null, root, latest), root)
      h.emit(value(1))
      const before = h.state().snapshot!
      const apply = createLayoutApplyRequest(id(101), root.projectId, before.revision, operation(before, 'caution'), layoutSnapshotVersion(before))
      expect(h.session.send(apply)).toBe(true)
      const child = value(2, apply.requestId, 'layout.apply')
      h.emit({ ...child, result: { ...child.result, durableSubtitles: {
        ...child.result.durableSubtitles, rootVersionId: wrongRoot ? before.compositionVersionId : root.versionId,
      } } })
      expect(h.state().branch?.root).toEqual(root)
      expect(h.state().status).toBe(wrongRoot ? 'invalid' : 'available')
      expect(h.state().snapshot?.compositionVersionId).toBe(wrongRoot ? before.compositionVersionId : id(2))
      h.session.dispose()
    }
  })

  it('preserves legacy metadata-absent notPersisted responses and never labels an arbitrary snapshot flag saved', () => {
    const h = sessionHarness()
    h.session.send(createLayoutReadRequest(layoutRequestId, root.projectId, null, root), root)
    h.emit(createLayoutResponseValue())
    expect(h.state().status).toBe('available')
    expect(layoutHasPersistedSubtitles(h.state())).toBe(false)
    expect(layoutHasPersistedSubtitles({ ...h.state(), snapshot: { ...snapshot(0), persistence: 'persisted' } })).toBe(false)
    h.session.dispose()
  })

  it('requires a pinned response context and valid journal metadata before exposing a saved label', () => {
    const unpinned = layoutStateReducer(initialLayoutState, layoutRequestStarted(layoutRequestId, 'layout.read', root.projectId))
    const rejected = layoutStateReducer(unpinned, layoutResponseReceived(required(value(1))))
    expect(rejected.snapshot).toBeNull()
    expect(layoutHasPersistedSubtitles(rejected)).toBe(false)
    const h = sessionHarness()
    h.session.send(createLayoutReadRequest(layoutRequestId, root.projectId, null, root, latest), root)
    h.emit(value(1))
    expect(layoutHasPersistedSubtitles(h.state())).toBe(true)
    expect(layoutHasPersistedSubtitles({ ...h.state(), durableSubtitles: {
      ...value(1).result.durableSubtitles, journalDigest: null,
    } })).toBe(false)
    h.session.dispose()
  })
})

function value(sequence: number, requestId = layoutRequestId, method: 'layout.read' | 'layout.apply' = 'layout.read') {
  const original = createLayoutResponseValue(requestId, method)
  const versionId = sequence === 0 ? root.versionId : id(sequence)
  return { ...original, result: { status: 'available', snapshot: {
    ...original.result.snapshot, revision: sequence, compositionVersionId: versionId, variantPair: null,
    persistence: sequence > 0 ? 'persisted' : 'notPersisted',
    subtitles: original.result.snapshot.subtitles.map(cue => sequence === 0 ? cue : ({ ...cue,
      compositionVersionId: versionId, revisionId: id(sequence + 10), revisionDigest: sequence.toString(16).padStart(64, '0'),
      revisionCount: cue.revisionCount + sequence, style: sequence === 1 ? 'quote' : 'caution',
    })),
  }, durableSubtitles: {
    rootVersionId: root.versionId, headVersionId: versionId, headVersionDigest: root.versionDigest,
    sequence, journalDigest: sequence === 0 ? null : sequence.toString(16).padStart(64, '0'),
  } } }
}

function required(input: unknown): LayoutBridgeResponse {
  const response = parseLayoutBridgeResponse(input)
  if (response === null) throw new Error('Invalid durable response fixture.')
  return response
}

function snapshot(sequence: number): LayoutSnapshot {
  const response = required(value(sequence))
  if (response.result.status !== 'available') throw new Error('Expected available fixture.')
  return response.result.snapshot
}

function operation(base: LayoutSnapshot, style: 'quote' | 'caution' = 'quote'): LayoutOperation {
  const cue = base.subtitles[0]!
  return { kind: 'editSubtitle', cueId: cue.cueId, expectedRevisionId: cue.revisionId,
    expectedRevisionDigest: cue.revisionDigest, edit: { kind: 'style', style }, reason: '契約検証' }
}

function sessionHarness() {
  let state = initialLayoutState
  let listener: ((response: LayoutBridgeResponse) => void) | undefined
  const client: LayoutBridgeClient = {
    sendLayout() {}, abandonLayoutRequest() {}, dispose() {},
    onLayoutResponse(value) { listener = value; return () => { listener = undefined } },
  }
  const session = startLayoutBridgeSession(() => client, action => { state = layoutStateReducer(state, action) })
  return { session, state: () => state, emit: (input: unknown) => { listener?.(required(input)) } }
}