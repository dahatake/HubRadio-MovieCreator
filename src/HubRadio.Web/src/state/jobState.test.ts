import { describe, expect, it } from 'vitest'

import {
  JOBS_CANCEL_METHOD,
  JOBS_GET_STATUS_METHOD,
  JOBS_RETRY_METHOD,
  type BridgeJobCancelResponse,
  type BridgeJobRetryResponse,
  type BridgeJobSnapshot,
  type BridgeJobStatusResponse,
} from '../bridge/protocol'
import { selectJobDisplayStatus } from '../features/jobs/jobPresentation'
import {
  appStateReducer,
  initialAppState,
  jobRequestFailed,
  jobRequestStarted,
  jobResponseReceived,
  type AppState,
  type JobOperation,
} from './index'

const jobId = '11223344-5566-7788-99aa-bbccddeeff00'
const attemptId = '22334455-6677-8899-aabb-ccddeeff0011'
const firstRequest = '00112233-4455-6677-8899-aabbccddeeff'
const secondRequest = '33445566-7788-99aa-bbcc-ddeeff001122'

describe('durable job state reducer', () => {
  it('accepts a requested snapshot and ignores duplicate terminal responses', () => {
    const succeeded = snapshot({
      state: 'succeeded',
      progressRatio: 1,
      revision: 4,
    })
    const response = statusResponse(firstRequest, succeeded)
    const loaded = receive(initialAppState, 'status', firstRequest, response)

    expect(loaded.jobMonitor.snapshot).toEqual(succeeded)
    expect(loaded.jobMonitor.pending).toBeNull()
    expect(appStateReducer(loaded, jobResponseReceived(response))).toBe(loaded)
  })

  it('rejects older revisions, attempts, progress, and details without rollback', () => {
    const current = snapshot()
    const loaded = receive(
      initialAppState,
      'status',
      firstRequest,
      statusResponse(firstRequest, current),
    )

    const candidates: BridgeJobSnapshot[] = [
      snapshot({ revision: 2 }),
      snapshot({ revision: 4, currentAttemptNumber: 0, currentAttemptId: null }),
      snapshot({
        lastProgressAt: '2026-08-07T01:02:03.0000000Z',
        progressRatio: 0.1,
      }),
      snapshot({ detailsRevision: 1, warnings: [] }),
    ]

    let state = loaded
    for (const candidate of candidates) {
      state = appStateReducer(
        state,
        jobRequestStarted(secondRequest, 'status', jobId),
      )
      state = appStateReducer(
        state,
        jobResponseReceived(statusResponse(secondRequest, candidate)),
      )
      expect(state.jobMonitor.snapshot).toBe(current)
    }
  })

  it('accepts same-revision progress, heartbeat, details, and cancellation evidence', () => {
    const current = snapshot()
    let state = receive(
      initialAppState,
      'status',
      firstRequest,
      statusResponse(firstRequest, current),
    )
    const advanced = snapshot({
      detailsRevision: 3,
      warnings: ['audioTrackAbsent', 'durationFieldsDiffer', 'lowResolution'],
      lastProgressAt: '2026-08-07T01:02:06.0000000Z',
      progressRatio: 0.5,
      lastHeartbeatAt: '2026-08-07T01:02:07.0000000Z',
      cancellationRequested: true,
    })

    state = receive(
      state,
      'status',
      secondRequest,
      statusResponse(secondRequest, advanced),
    )

    expect(state.jobMonitor.snapshot).toEqual(advanced)
  })

  it('does not optimistically cancel and accepts a completion race from native state', () => {
    const running = snapshot()
    let state = receive(
      initialAppState,
      'status',
      firstRequest,
      statusResponse(firstRequest, running),
    )
    state = appStateReducer(
      state,
      jobRequestStarted(secondRequest, 'cancel', jobId),
    )
    expect(state.jobMonitor.snapshot).toBe(running)
    expect(state.jobMonitor.snapshot?.cancellationRequested).toBe(false)

    const succeeded = snapshot({
      state: 'succeeded',
      revision: 4,
      progressRatio: 1,
    })
    const response: BridgeJobCancelResponse = {
      version: 1,
      requestId: secondRequest,
      method: JOBS_CANCEL_METHOD,
      result: { outcome: 'notRunning', job: succeeded },
    }
    state = appStateReducer(state, jobResponseReceived(response))

    expect(state.jobMonitor.snapshot).toEqual(succeeded)
    expect(state.jobMonitor.lastCommand).toEqual({
      operation: 'cancel',
      outcome: 'notRunning',
    })
  })

  it('preserves a nonretryable failure when native denies retry', () => {
    const failed = snapshot({
      state: 'failed',
      failure: 'other',
      retryability: 'nonRetryable',
    })
    let state = receive(
      initialAppState,
      'status',
      firstRequest,
      statusResponse(firstRequest, failed),
    )
    state = appStateReducer(
      state,
      jobRequestStarted(secondRequest, 'retry', jobId),
    )
    const response: BridgeJobRetryResponse = {
      version: 1,
      requestId: secondRequest,
      method: JOBS_RETRY_METHOD,
      result: { outcome: 'errorNotRetryable', job: failed },
    }
    state = appStateReducer(state, jobResponseReceived(response))

    expect(selectJobDisplayStatus(state.jobMonitor.snapshot!)).toBe(
      'nonRetryableFailed',
    )
    expect(state.jobMonitor.lastCommand).toEqual({
      operation: 'retry',
      outcome: 'errorNotRetryable',
    })
  })

  it('represents restart interruption and derives requeued only from durable fields', () => {
    const interrupted = snapshot({
      state: 'interrupted',
      failure: 'appRestarted',
      retryability: 'retryable',
    })
    let state = receive(
      initialAppState,
      'status',
      firstRequest,
      statusResponse(firstRequest, interrupted),
    )
    expect(selectJobDisplayStatus(state.jobMonitor.snapshot!)).toBe('interrupted')

    state = appStateReducer(
      state,
      jobRequestStarted(secondRequest, 'retry', jobId),
    )
    const requeued = snapshot({
      state: 'queued',
      revision: 4,
      failure: null,
      retryability: 'notApplicable',
      progressRatio: null,
      lastProgressAt: null,
      lastHeartbeatAt: null,
    })
    const response: BridgeJobRetryResponse = {
      version: 1,
      requestId: secondRequest,
      method: JOBS_RETRY_METHOD,
      result: { outcome: 'queued', job: requeued },
    }
    state = appStateReducer(state, jobResponseReceived(response))

    expect(selectJobDisplayStatus(state.jobMonitor.snapshot!)).toBe('requeued')
  })

  it('keeps the durable snapshot on OCC conflict and ignores mismatched responses', () => {
    const current = snapshot()
    let state = receive(
      initialAppState,
      'status',
      firstRequest,
      statusResponse(firstRequest, current),
    )
    state = appStateReducer(
      state,
      jobRequestStarted(secondRequest, 'cancel', jobId),
    )
    const unrelated = {
      version: 1 as const,
      requestId: firstRequest,
      method: JOBS_CANCEL_METHOD,
      error: { code: 'conflict' as const },
    }
    expect(appStateReducer(state, jobResponseReceived(unrelated))).toBe(state)

    const conflict = {
      ...unrelated,
      requestId: secondRequest,
    }
    state = appStateReducer(state, jobResponseReceived(conflict))
    expect(state.jobMonitor.snapshot).toBe(current)
    expect(state.jobMonitor.lastError).toBe('conflict')
    expect(state.jobMonitor.pending).toBeNull()
  })

  it('clears a timed-out request without fabricating or removing durable state', () => {
    const current = snapshot()
    let state = receive(
      initialAppState,
      'status',
      firstRequest,
      statusResponse(firstRequest, current),
    )
    state = appStateReducer(
      state,
      jobRequestStarted(secondRequest, 'status', jobId),
    )
    state = appStateReducer(
      state,
      jobRequestFailed(secondRequest, 'bridge_timeout'),
    )

    expect(state.jobMonitor.snapshot).toBe(current)
    expect(state.jobMonitor.pending).toBeNull()
    expect(state.jobMonitor.lastError).toBe('bridge_timeout')
  })
})

function receive(
  state: AppState,
  operation: JobOperation,
  requestId: string,
  response: BridgeJobStatusResponse,
): AppState {
  return appStateReducer(
    appStateReducer(state, jobRequestStarted(requestId, operation, jobId)),
    jobResponseReceived(response),
  )
}

function statusResponse(
  requestId: string,
  value: BridgeJobSnapshot,
): BridgeJobStatusResponse {
  return {
    version: 1,
    requestId,
    method: JOBS_GET_STATUS_METHOD,
    result: value,
  }
}

function snapshot(
  overrides: Partial<BridgeJobSnapshot> = {},
): BridgeJobSnapshot {
  return {
    jobId,
    kind: 'mediaIngest',
    state: 'running',
    currentAttemptId: attemptId,
    currentAttemptNumber: 1,
    revision: 3,
    detailsRevision: 2,
    updatedAt: '2026-08-07T01:02:03.0000000Z',
    progressRatio: 0.25,
    lastProgressAt: '2026-08-07T01:02:04.0000000Z',
    lastHeartbeatAt: '2026-08-07T01:02:05.0000000Z',
    failure: null,
    retryability: 'notApplicable',
    cancellationRequested: false,
    warnings: ['audioTrackAbsent', 'lowResolution'],
    reconciliation: 'required',
    ...overrides,
  }
}