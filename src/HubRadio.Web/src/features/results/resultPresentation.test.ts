import { describe, expect, it } from 'vitest'
import { verifiedPipelineResponse } from '../workflow/pipelineProtocol'
import { createCompletedPipeline, createPipelineArtifact, createPipelineSnapshot, fixtureId, pipelineResponse } from '../workflow/pipelineTestFixture'
import type { SourceOnlyProjectSummary } from '../../bridge/projectProtocol'
import {
  JOBS_GET_STATUS_METHOD,
  parseJobResponse,
  type BridgeJobSnapshot,
} from '../../bridge/protocol'
import {
  parseDeliveryReadResponse,
  type DeliveryArtifact,
  type DeliveryAvailableResult,
} from '../delivery/deliveryProtocol'
import {
  createDeliveryFailureValue,
  createDeliveryResponseValue,
  deliveryRequestId,
  deliverySubtitleArtifactId,
  deliveryVideoArtifactId,
} from '../delivery/deliveryTestFixture'
import {
  deriveResultPresentation,
  type ResultKind,
  type ResultPresentation,
} from './resultPresentation'

// Test-only plans/records. These six names are the bundled 1.0.0 artifactSet,
// not six generated files or counts encoded in the names.
const defaultArtifactSet = [
  'landscapeLongForm',
  'verticalShortCandidates',
  'subtitleBurnInVariant',
  'cleanVariant',
  'subtitleFiles',
  'thumbnailCandidates',
] as const
const settingsDigest = 'a'.repeat(64)
const jobId = '10000000-0000-4000-8000-000000000001'
const otherJobId = '10000000-0000-4000-8000-000000000002'
const attemptId = '10000000-0000-4000-8000-000000000003'

describe('result presentation plans', () => {
  it('returns no invented cards, counts, job or evaluation when inputs are absent', () => {
    const empty = {
      cards: [], planState: 'unavailable', deliveryStatus: null,
      jobStatus: null, evaluation: 'NOT EVALUATED',
    }
    expect(deriveResultPresentation()).toEqual(empty)
    expect(deriveResultPresentation({})).toEqual(empty)
    expect(deriveResultPresentation({
      sourceOnlySummary: null, delivery: null,
      currentSettingsSnapshotDigest: null, jobMonitor: null,
    })).toEqual(empty)
  })

  it('maps the actual default set without using variant or candidate labels as counts', () => {
    const view = deriveResultPresentation({ sourceOnlySummary: summary() })
    expect(view.cards.map(card => card.kind)).toEqual([
      'videoLong', 'videoShort', 'text', 'thumbnail', 'video',
    ])
    expect(card(view, 'video').plannedLabels).toEqual(['字幕焼き込み版', 'クリーン版'])
    expect(card(view, 'videoShort').plannedLabels).toEqual(['短尺動画候補'])
    for (const item of view.cards) {
      expect(item.state).toBe('planned')
      expect(item).not.toHaveProperty('count')
      expect(item.artifacts).toEqual([])
      expect(item.saveState).toBe('unavailable')
    }
    expect(view.planState).toBe('planned')
    expect(view.evaluation).toBe('NOT EVALUATED')
  })

  it.each([
    ['landscapeLongForm', 'videoLong'],
    ['verticalShortCandidates', 'videoShort'],
    ['subtitleBurnInVariant', 'video'],
    ['cleanVariant', 'video'],
    ['subtitleFiles', 'text'],
    ['thumbnailCandidates', 'thumbnail'],
    ['landscapeVideo', 'video'],
    ['verticalShorts', 'videoShort'],
    ['verticalShort', 'videoShort'],
    ['長尺動画', 'videoLong'],
    ['短尺動画', 'videoShort'],
    ['字幕ファイル', 'text'],
    ['文字起こし', 'text'],
    ['サムネイル候補', 'thumbnail'],
  ] as const)('groups only the known exact label %s as %s', (label, kind) => {
    const view = deriveResultPresentation({ sourceOnlySummary: summary([label]) })
    expect(view.cards).toHaveLength(1)
    expect(view.cards[0]).toMatchObject({ kind, state: 'planned' })
    expect(view.cards[0]).not.toHaveProperty('count')
  })

  it.each([
    { name: 'prototype key', value: '__proto__' },
    { name: 'constructor key', value: 'constructor' },
    { name: 'object method', value: 'toString' },
    { name: 'HTML payload', value: '<img src=x onerror="alert(1)">' },
    { name: 'number suffix', value: 'verticalShortCandidates999' },
    { name: 'Japanese count', value: '長尺動画 3本' },
    { name: 'catalog dimensions', value: 'long-form-1280x720' },
    { name: 'different case', value: 'ThumbnailCandidates' },
    { name: 'long value', value: '動画'.repeat(5000) },
  ])('uses a fixed safe unknown label, not a parser or HTML: $name', ({ value }) => {
    const view = deriveResultPresentation({ sourceOnlySummary: summary([value]) })
    expect(view.cards).toHaveLength(1)
    expect(view.cards[0]).toEqual({
      id: 'result-other', kind: 'other', title: 'その他の成果物', state: 'planned',
      plannedLabels: ['その他の予定成果物'], artifacts: [], saveState: 'unavailable',
    })
    expect(view.evaluation).toBe('NOT EVALUATED')
  })

  it('keeps kind IDs/order stable and does not count aliases or unknown labels as files', () => {
    const values = ['字幕ファイル', 'subtitleFiles', 'unknown-one', 'unknown-two', 'landscapeLongForm']
    const first = deriveResultPresentation({ sourceOnlySummary: summary(values) })
    const reordered = deriveResultPresentation({ sourceOnlySummary: summary([...values].reverse()) })
    expect(first.cards.map(item => item.id)).toEqual(reordered.cards.map(item => item.id))
    expect(card(first, 'text').plannedLabels).toEqual(['字幕ファイル'])
    expect(card(first, 'other').plannedLabels).toEqual(['その他の予定成果物'])
    expect(new Set(first.cards.map(item => item.id)).size).toBe(first.cards.length)
    expect(first.cards.every(item => !('count' in item))).toBe(true)
  })

  it('does not fill older/unknown profiles with current defaults or infer staleness from age', () => {
    const old = {
      ...summary(['legacy-output']), profileVersion: '0.1.0',
      profileId: otherJobId, profileDigest: 'b'.repeat(64),
    }
    const view = deriveResultPresentation({ sourceOnlySummary: old })
    expect(view.planState).toBe('planned')
    expect(view.cards.map(item => item.kind)).toEqual(['other'])
    expect(view.cards[0]?.state).toBe('planned')
  })

  it('marks only an actual same-project settings digest difference as a stale plan', () => {
    const sourceOnlySummary = summary()
    const view = deriveResultPresentation({
      sourceOnlySummary, currentSettingsSnapshotDigest: 'b'.repeat(64),
    })
    expect(view.planState).toBe('stale')
    expect(view.cards.every(item => item.state === 'stale')).toBe(true)
    expect(view.cards.every(item => !('count' in item))).toBe(true)
    expect(sourceOnlySummary.settingsSnapshotDigest).toBe(settingsDigest)
  })

  it('does not compare missing or malformed digests, and keeps matching settings planned', () => {
    for (const currentSettingsSnapshotDigest of [
      undefined, null, '', 'old', 'B'.repeat(64), `${'b'.repeat(64)}\n`, settingsDigest,
    ]) {
      const view = deriveResultPresentation({ sourceOnlySummary: summary(), currentSettingsSnapshotDigest })
      expect(view.planState).toBe('planned')
      expect(view.cards.every(item => item.state === 'planned')).toBe(true)
    }
    expect(deriveResultPresentation({ currentSettingsSnapshotDigest: settingsDigest }).planState).toBe('unavailable')
  })
})

describe('result presentation validated delivery', () => {
  it('derives real files and local registration, never automatic evaluation or approval', () => {
    const delivery = availableDelivery()
    expect(delivery.snapshot.qualityControl.machineChecksPassed).toBe(true)
    expect(delivery.snapshot.review.approvals.length).toBeGreaterThan(0)
    const view = deriveResultPresentation({ delivery })
    expect(view.cards.map(item => item.kind)).toEqual(['text', 'video'])
    expect(card(view, 'video')).toMatchObject({ state: 'generated', count: 1, saveState: 'saved' })
    expect(card(view, 'text')).toMatchObject({ state: 'generated', count: 1, saveState: 'saved' })
    expect(card(view, 'video').artifacts[0]).toBe(delivery.snapshot.artifacts[0])
    expect(card(view, 'video').artifacts[0]?.artifactId).toBe(deliveryVideoArtifactId)
    expect(card(view, 'text').artifacts[0]?.artifactId).toBe(deliverySubtitleArtifactId)
    expect(view.evaluation).toBe('NOT EVALUATED')
    expect(view.planState).toBe('unavailable')
    expect(view.deliveryStatus).toBe('available')
    expect(Object.keys(view).sort()).toEqual(['cards', 'deliveryStatus', 'evaluation', 'jobStatus', 'planState'])
  })

  it('preserves zero classified files without inventing candidates, reasons or previews', () => {
    const view = deriveResultPresentation({ sourceOnlySummary: summary(), delivery: availableDelivery() })
    for (const kind of ['videoLong', 'videoShort', 'thumbnail'] as const) {
      expect(card(view, kind)).toMatchObject({ state: 'notProduced', count: 0, artifacts: [], saveState: 'unavailable' })
    }
    expect(card(view, 'video').count).toBe(1)
    expect(card(view, 'video').plannedLabels).toHaveLength(2)
    for (const item of view.cards) {
      expect(Object.keys(item).sort()).toEqual([
        'artifacts', 'count', 'id', 'kind', 'plannedLabels', 'saveState', 'state', 'title',
      ])
    }
    // QC duration/cue count does not provide per-card duration, aspect or preview.
    expect(card(view, 'text').count).toBe(1)
    expect(view.evaluation).toBe('NOT EVALUATED')
  })

  it('uses known types and declared video/text media families without guessing image purposes', () => {
    const value = createDeliveryResponseValue(deliveryRequestId, false)
    value.result.snapshot.artifacts.push(
      artifact(1, 'landscapeLongForm', 'video/mp4'),
      artifact(2, 'verticalShortCandidates', 'video/mp4'),
      artifact(3, 'thumbnail', 'image/jpeg'),
      artifact(4, 'transcript', 'application/json'),
      artifact(5, 'chapters', 'application/json'),
      artifact(6, 'publishing-text', 'application/json'),
      artifact(7, 'editing-instructions', 'application/json'),
      artifact(8, 'unknown-video-3-1280x720', 'video/webm'),
      artifact(9, 'notes', 'text/plain'),
      artifact(10, 'logo', 'image/png'),
      artifact(11, 'review-evidence', 'application/json'),
      artifact(12, '<script>alert(1)</script>', 'application/octet-stream'),
    )
    const delivery = availableDelivery(value)
    const view = deriveResultPresentation({ delivery })
    expect(view.cards.map(item => [item.kind, item.count])).toEqual([
      ['videoLong', 1], ['videoShort', 1], ['text', 6],
      ['thumbnail', 1], ['video', 2], ['other', 3],
    ])
    expect(card(view, 'other').title).toBe('その他の成果物')
    expect(card(view, 'other').artifacts.at(-1)).toBe(delivery.snapshot.artifacts.at(-1))
    expect(view.cards.flatMap(item => item.artifacts).map(item => item.artifactId).sort())
      .toEqual(delivery.snapshot.artifacts.map(item => item.artifactId).sort())
  })

  it('does not reclassify a master as long/short using the plan, profile, QC duration or filename-like text', () => {
    const view = deriveResultPresentation({
      sourceOnlySummary: summary(['landscapeLongForm', 'verticalShortCandidates']),
      delivery: availableDelivery(),
    })
    expect(card(view, 'video').artifacts.map(item => item.artifactId)).toEqual([deliveryVideoArtifactId])
    expect(card(view, 'videoLong').count).toBe(0)
    expect(card(view, 'videoShort').count).toBe(0)
    expect(card(view, 'video')).not.toHaveProperty('duration')
    expect(card(view, 'video')).not.toHaveProperty('aspectRatio')
  })

  it('compares settings only to the source summary, never to a render profile or an unbound artifact', () => {
    const delivery = availableDelivery()
    const matching = deriveResultPresentation({
      sourceOnlySummary: summary(), delivery, currentSettingsSnapshotDigest: settingsDigest,
    })
    expect(delivery.snapshot.profileDigest).not.toBe(summary().profileDigest)
    expect(matching.planState).toBe('planned')
    const changed = deriveResultPresentation({
      sourceOnlySummary: summary(), delivery, currentSettingsSnapshotDigest: 'c'.repeat(64),
    })
    expect(changed.planState).toBe('stale')
    expect(card(changed, 'thumbnail').state).toBe('stale')
    expect(card(changed, 'thumbnail').count).toBe(0)
    expect(card(changed, 'video')).toMatchObject({ state: 'generated', count: 1, saveState: 'saved' })
    expect(card(changed, 'text')).toMatchObject({ state: 'generated', count: 1, saveState: 'saved' })
    expect(deriveResultPresentation({ delivery, currentSettingsSnapshotDigest: settingsDigest }).planState).toBe('unavailable')
  })

  it.each([
    'unavailable', 'forbidden', 'incomplete', 'mismatched', 'qualityControlFailed',
  ] as const)('keeps plans separate from delivery failure %s', (status) => {
    const response = parseDeliveryReadResponse(createDeliveryFailureValue(status))
    if (response === null) throw new Error('Expected a validated failure fixture.')
    const view = deriveResultPresentation({ sourceOnlySummary: summary(), delivery: response.result })
    expect(view.deliveryStatus).toBe(status)
    expect(view.evaluation).toBe('NOT EVALUATED')
    expect(view.cards.every(item => item.state === 'planned' && !('count' in item))).toBe(true)
    expect(view.cards.every(item => item.artifacts.length === 0 && item.saveState === 'unavailable')).toBe(true)
    expect(deriveResultPresentation({ delivery: response.result }).cards).toEqual([])
  })

  it('does not treat an available status without a snapshot, or a failed read retaining old data, as available files', () => {
    for (const delivery of [
      { status: 'available', snapshot: null },
      { status: 'unavailable', snapshot: availableDelivery().snapshot },
      { status: null, snapshot: availableDelivery().snapshot },
    ] as const) {
      const view = deriveResultPresentation({ sourceOnlySummary: summary(), delivery })
      expect(view.cards.every(item => item.state === 'planned' && !('count' in item))).toBe(true)
      expect(view.cards.every(item => item.artifacts.length === 0 && item.saveState === 'unavailable')).toBe(true)
    }
  })

  it('does not substitute an empty available manifest rejected by the existing parser', () => {
    const value = createDeliveryResponseValue(deliveryRequestId, false)
    value.result.snapshot.artifacts = []
    expect(parseDeliveryReadResponse(value)).toBeNull()
    const view = deriveResultPresentation({ sourceOnlySummary: summary(), delivery: null })
    expect(view.cards.every(item => item.state === 'planned' && !('count' in item))).toBe(true)
  })

  it('preserves the protocol maximum of actual files without capping candidates to a planned default', () => {
    const value = createDeliveryResponseValue(deliveryRequestId, false)
    value.result.snapshot.artifacts.push(...Array.from(
      { length: 1022 }, (_, index) => artifact(index + 1, 'thumbnail', 'image/jpeg'),
    ))
    const delivery = availableDelivery(value)
    const view = deriveResultPresentation({ sourceOnlySummary: summary(), delivery })
    expect(card(view, 'thumbnail').count).toBe(1022)
    expect(card(view, 'thumbnail').artifacts).toHaveLength(1022)
    expect(view.cards.reduce((total, item) => total + (item.count ?? 0), 0)).toBe(1024)
    expect(view.evaluation).toBe('NOT EVALUATED')
  })

  it('keeps group IDs stable across actual input order and does not mutate/freeze caller-owned inputs', () => {
    const sourceOnlySummary = summary()
    const delivery = availableDelivery()
    const input = { sourceOnlySummary, delivery }
    const before = structuredClone(input)
    const first = deriveResultPresentation(input)
    expect(input).toEqual(before)
    expect(Object.isFrozen(sourceOnlySummary)).toBe(false)
    expect(Object.isFrozen(first)).toBe(true)
    expect(Object.isFrozen(first.cards)).toBe(true)
    for (const item of first.cards) {
      expect(Object.isFrozen(item)).toBe(true)
      expect(Object.isFrozen(item.artifacts)).toBe(true)
      expect(Object.isFrozen(item.plannedLabels)).toBe(true)
    }
    const value = createDeliveryResponseValue(deliveryRequestId, false)
    value.result.snapshot.artifacts.reverse()
    const second = deriveResultPresentation({
      sourceOnlySummary: summary([...defaultArtifactSet].reverse()), delivery: availableDelivery(value),
    })
    expect(first.cards.map(item => [item.id, item.kind, item.count]))
      .toEqual(second.cards.map(item => [item.id, item.kind, item.count]))
    expect(deriveResultPresentation(input)).toEqual(first)
  })
})

describe('result presentation actual job status', () => {
  it.each([
    [{ state: 'queued' }, 'queued', 'planned'],
    [{ state: 'queued', currentAttemptNumber: 1 }, 'requeued', 'planned'],
    [{ state: 'running' }, 'running', 'processing'],
    [{ state: 'running', cancellationRequested: true }, 'cancelRequested', 'processing'],
    [{ state: 'failed', retryability: 'retryable' }, 'retryableFailed', 'failed'],
    [{ state: 'failed', retryability: 'nonRetryable' }, 'nonRetryableFailed', 'failed'],
    [{ state: 'cancelled' }, 'cancelled', 'unavailable'],
    [{ state: 'interrupted' }, 'interrupted', 'unavailable'],
    [{ state: 'succeeded' }, 'succeeded', 'unavailable'],
  ] as const)('shows actual render status %j without inventing output-specific progress', (patch, status, state) => {
    const job = actualJob(patch)
    const view = deriveResultPresentation({
      sourceOnlySummary: summary(['landscapeLongForm', 'thumbnailCandidates']),
      jobMonitor: { trackedJobId: jobId, snapshot: job },
    })
    expect(view.jobStatus).toBe(status)
    expect(card(view, 'video')).toMatchObject({ state, artifacts: [], saveState: 'unavailable' })
    expect(card(view, 'video')).not.toHaveProperty('count')
    expect(card(view, 'videoLong').state).toBe('planned')
    expect(card(view, 'thumbnail').state).toBe('planned')
    expect(view.evaluation).toBe('NOT EVALUATED')
  })

  it('does not create outputs from a successful ingest, unknown job, or 100% progress', () => {
    for (const kind of ['mediaIngest', 'unknown', 'finalRender'] as const) {
      const job = actualJob({ kind, state: 'succeeded', progressRatio: 1 })
      const view = deriveResultPresentation({
        sourceOnlySummary: summary(), jobMonitor: { trackedJobId: jobId, snapshot: job },
      })
      expect(view.jobStatus).toBe('succeeded')
      expect(view.cards.every(item => item.state !== 'generated' && !('count' in item))).toBe(true)
      expect(view.cards.every(item => item.saveState === 'unavailable')).toBe(true)
      expect(view.evaluation).toBe('NOT EVALUATED')
    }
  })

  it('does not project ingest failures/running onto planned outputs or synthesize warning destinations', () => {
    for (const state of ['running', 'failed'] as const) {
      const job = actualJob({ kind: 'mediaIngest', state, warnings: ['lowResolution'] })
      const view = deriveResultPresentation({
        sourceOnlySummary: summary(), jobMonitor: { trackedJobId: jobId, snapshot: job },
      })
      expect(view.cards.every(item => item.state === 'planned')).toBe(true)
      expect(view).not.toHaveProperty('warningTargets')
    }
  })

  it('ignores untracked/mismatched snapshots and pending tracking without a snapshot', () => {
    for (const jobMonitor of [
      { trackedJobId: null, snapshot: actualJob() },
      { trackedJobId: otherJobId, snapshot: actualJob({ state: 'failed' }) },
      { trackedJobId: jobId, snapshot: null },
    ]) {
      const view = deriveResultPresentation({ sourceOnlySummary: summary(), jobMonitor })
      expect(view.jobStatus).toBeNull()
      expect(view.cards.every(item => item.state === 'planned')).toBe(true)
    }
  })

  it('does not hide already evidenced files when a later render is running or fails', () => {
    for (const state of ['running', 'failed'] as const) {
      const job = actualJob({ state })
      const view = deriveResultPresentation({
        delivery: availableDelivery(), jobMonitor: { trackedJobId: jobId, snapshot: job },
      })
      expect(card(view, 'video')).toMatchObject({ state: 'generated', count: 1, saveState: 'saved' })
      expect(view.jobStatus).toBe(state === 'running' ? 'running' : 'retryableFailed')
      expect(view.evaluation).toBe('NOT EVALUATED')
    }
  })
})

describe('result presentation native pipeline projection', () => {
  it('compares saved settings with the current execution, not the original quick-start digest after regeneration', () => {
    const s = createPipelineSnapshot('Rendering')
    const pipeline = verifiedPipelineResponse(pipelineResponse(s))!.result.snapshot!
    expect(deriveResultPresentation({ sourceOnlySummary: summary(), pipeline, currentSettingsSnapshotDigest: s.settingsSnapshotDigest }).planState).toBe('planned')
    expect(deriveResultPresentation({ sourceOnlySummary: summary(), pipeline, currentSettingsSnapshotDigest: 'e'.repeat(64) }).planState).toBe('stale')
  })

  it('classifies real PascalCase artifact roles and keeps evaluation and delivered evidence separate', () => {
    const s = createCompletedPipeline()
    s.result!.artifacts.push(createPipelineArtifact(s.result!, 'VerticalBurnIn', 20), createPipelineArtifact(s.result!, 'LandscapeThumbnail', 30), createPipelineArtifact(s.result!, 'SubRip', 40))
    s.result!.artifacts[1]!.delivered = false
    s.result!.artifacts[1]!.delivery = null
    const pipeline = verifiedPipelineResponse(pipelineResponse(s))!.result.snapshot!
    const view = deriveResultPresentation({ pipeline })
    expect(view.cards.map(item => [item.kind, item.count])).toEqual([['videoLong', 1], ['videoShort', 1], ['text', 1], ['thumbnail', 1]])
    expect(card(view, 'videoLong').saveState).toBe('saved')
    expect(card(view, 'videoShort').saveState).toBe('unavailable')
    expect(card(view, 'videoShort').pipelineArtifacts?.[0]?.delivered).toBe(false)
    expect(view.resultSource).toBe('current')
    expect(view.evaluation).toBe('NOT EVALUATED')
    expect(card(view, 'thumbnail').artifacts).toEqual([]) // never a fabricated legacy DTO
  })

  it('shows the explicit same-project parent while the current execution has no output, without relabeling it current', () => {
    const completed = createCompletedPipeline()
    const s = { ...createPipelineSnapshot('Rendering'), executionId: fixtureId(90), rootJobId: fixtureId(91),
      parentVersionId: completed.versionId, parentDigest: completed.versionDigest, previousResult: completed.result }
    const view = deriveResultPresentation({ pipeline: verifiedPipelineResponse(pipelineResponse(s))!.result.snapshot })
    expect(view.resultSource).toBe('previous')
    expect(view.pipelineResult?.binding.executionId).toBe(completed.executionId)
    expect(card(view, 'videoLong').count).toBe(1)
  })

  it.each(['Queued', 'Rendering', 'Completed', 'Failed', 'Canceled'] as const)('does not invent files from phase %s without a result', phase => {
    const view = deriveResultPresentation({ sourceOnlySummary: summary(), pipeline: verifiedPipelineResponse(pipelineResponse(createPipelineSnapshot(phase)))!.result.snapshot })
    expect(view.pipelineResult).toBeUndefined()
    expect(view.evaluation).toBe('NOT EVALUATED')
    expect(view.cards.every(item => item.state !== 'generated' && item.count === undefined && item.saveState === 'unavailable')).toBe(true)
  })
})

function summary(artifactSet: readonly string[] = defaultArtifactSet): SourceOnlyProjectSummary {
  return {
    profileId: '8d75a1fe-d10e-4aa7-87c5-3ac2d06d1f9b',
    profileVersion: '1.0.0', profileDigest: 'd'.repeat(64),
    settingsSnapshotDigest: settingsDigest, artifactSet,
  }
}

function card(view: ResultPresentation, kind: ResultKind) {
  const found = view.cards.find(item => item.kind === kind)
  if (found === undefined) throw new Error(`Expected fixture card ${kind}.`)
  return found
}

function availableDelivery(
  value = createDeliveryResponseValue(deliveryRequestId, false),
): DeliveryAvailableResult {
  const response = parseDeliveryReadResponse(value)
  if (response?.result.status !== 'available') throw new Error('Expected a validated delivery fixture.')
  return response.result
}

function artifact(index: number, artifactType: string, mediaType: string): DeliveryArtifact {
  return {
    artifactId: `b0000000-0000-4000-8000-${index.toString().padStart(12, '0')}`,
    artifactType, mediaType, language: null, byteSize: '123', sha256: 'c'.repeat(64),
  }
}

function actualJob(patch: Partial<BridgeJobSnapshot> = {}): BridgeJobSnapshot {
  const state = patch.state ?? 'running'
  const attemptNumber = patch.currentAttemptNumber ?? (state === 'queued' ? 0 : 1)
  const response = parseJobResponse({
    version: 1, requestId: deliveryRequestId, method: JOBS_GET_STATUS_METHOD,
    result: {
      jobId, kind: 'finalRender', state,
      currentAttemptId: attemptNumber === 0 ? null : attemptId,
      currentAttemptNumber: attemptNumber, revision: 1, detailsRevision: null,
      updatedAt: '2026-09-07T00:00:00.0000000Z', progressRatio: null,
      lastProgressAt: null, lastHeartbeatAt: null,
      failure: state === 'failed' ? 'other' : state === 'interrupted' ? 'appRestarted' : null,
      retryability: state === 'failed' || state === 'interrupted' ? 'retryable' : 'notApplicable',
      cancellationRequested: false, warnings: [],
      reconciliation: patch.kind === 'mediaIngest' ? 'unknown' : 'notApplicable',
      ...patch,
    },
  })
  if (response === null || 'error' in response || response.method !== JOBS_GET_STATUS_METHOD) {
    throw new Error('Expected a validated job fixture.')
  }
  return response.result
}