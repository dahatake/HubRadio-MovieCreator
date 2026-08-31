import { describe, expect, it } from 'vitest'

import type { BridgeJobSnapshot } from '../../bridge/protocol'
import {
  jobStatusLabels,
  selectJobDisplayStatus,
  selectJobKindLabel,
  warningLabels,
} from './jobPresentation'

describe('job presentation', () => {
  it.each([
    [snapshot({ state: 'queued', currentAttemptId: null, currentAttemptNumber: 0 }), 'queued'],
    [snapshot({ state: 'queued' }), 'requeued'],
    [snapshot(), 'running'],
    [snapshot({ cancellationRequested: true }), 'cancelRequested'],
    [snapshot({ state: 'failed', failure: 'other', retryability: 'retryable' }), 'retryableFailed'],
    [snapshot({ state: 'failed', failure: 'other', retryability: 'nonRetryable' }), 'nonRetryableFailed'],
    [snapshot({ state: 'cancelled' }), 'cancelled'],
    [snapshot({ state: 'interrupted', failure: 'appRestarted', retryability: 'retryable' }), 'interrupted'],
    [snapshot({ state: 'succeeded', progressRatio: 1 }), 'succeeded'],
  ] as const)('derives %s as %s from durable fields', (value, expected) => {
    expect(selectJobDisplayStatus(value)).toBe(expected)
    expect(jobStatusLabels[expected]).not.toBe('')
  })

  it('uses closed labels for unknown kinds and warning codes', () => {
    expect(selectJobKindLabel(snapshot({ kind: 'unknown' }))).toBe('不明なジョブ')
    expect(Object.keys(warningLabels)).toHaveLength(8)
    expect(Object.values(warningLabels).every((label) => !label.includes('<'))).toBe(true)
  })
})

function snapshot(
  overrides: Partial<BridgeJobSnapshot> = {},
): BridgeJobSnapshot {
  return {
    jobId: '11223344-5566-7788-99aa-bbccddeeff00',
    kind: 'finalRender',
    state: 'running',
    currentAttemptId: '22334455-6677-8899-aabb-ccddeeff0011',
    currentAttemptNumber: 1,
    revision: 3,
    detailsRevision: null,
    updatedAt: '2026-08-07T01:02:03.0000000Z',
    progressRatio: 0.25,
    lastProgressAt: '2026-08-07T01:02:04.0000000Z',
    lastHeartbeatAt: '2026-08-07T01:02:05.0000000Z',
    failure: null,
    retryability: 'notApplicable',
    cancellationRequested: false,
    warnings: [],
    reconciliation: 'notApplicable',
    ...overrides,
  }
}