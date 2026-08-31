import { describe, expect, it, vi } from 'vitest'

import {
  createBridgeClient,
  type BridgeMessageEvent,
  type BridgeTransport,
} from './index'
import {
  BRIDGE_MAX_REQUEST_UTF8_BYTES,
  JOBS_CANCEL_METHOD,
  JOBS_GET_STATUS_METHOD,
  createJobCancelRequest,
  createJobRetryRequest,
  createJobStatusRequest,
  parseJobResponse,
  type BridgeJobSnapshot,
} from './protocol'

const requestId = '00112233-4455-6677-8899-aabbccddeeff'
const jobId = '11223344-5566-7788-99aa-bbccddeeff00'
const otherJobId = '44556677-8899-aabb-ccdd-eeff00112233'
const attemptId = '22334455-6677-8899-aabb-ccddeeff0011'

describe('job bridge protocol', () => {
  it('creates only exact bounded status, cancellation, and retry requests', () => {
    const status = createJobStatusRequest(requestId, jobId)
    const cancel = createJobCancelRequest(requestId, jobId, 7)
    const retry = createJobRetryRequest(requestId, jobId, 7)

    expect(status).toEqual({
      version: 1,
      requestId,
      method: 'jobs.getStatus',
      params: { jobId },
    })
    expect(cancel).toEqual({
      version: 1,
      requestId,
      method: 'jobs.cancel',
      params: { jobId, expectedRevision: 7 },
    })
    expect(retry).toEqual({
      version: 1,
      requestId,
      method: 'jobs.retry',
      params: { jobId, expectedRevision: 7 },
    })
    expect(new TextEncoder().encode(JSON.stringify(retry)).byteLength).toBeLessThanOrEqual(
      BRIDGE_MAX_REQUEST_UTF8_BYTES,
    )
    expect(Object.isFrozen(status.params)).toBe(true)
  })

  it.each([-1, 1.5, Number.NaN, 2_147_483_648])(
    'rejects invalid expected revision %s',
    (revision) => {
      expect(() => createJobCancelRequest(requestId, jobId, revision)).toThrow()
      expect(() => createJobRetryRequest(requestId, jobId, revision)).toThrow()
    },
  )

  it('parses an exact safe snapshot and stable command and error outcomes', () => {
    const snapshot = createSnapshot()
    const statusResponse = {
      version: 1,
      requestId,
      method: JOBS_GET_STATUS_METHOD,
      result: snapshot,
    }
    const cancelResponse = {
      version: 1,
      requestId,
      method: JOBS_CANCEL_METHOD,
      result: { outcome: 'requested', job: { ...snapshot, cancellationRequested: true } },
    }
    const errorResponse = {
      version: 1,
      requestId,
      method: JOBS_CANCEL_METHOD,
      error: { code: 'conflict' },
    }

    expect(parseJobResponse(statusResponse)).toEqual(statusResponse)
    expect(parseJobResponse(cancelResponse)).toEqual(cancelResponse)
    expect(parseJobResponse(errorResponse)).toEqual(errorResponse)
  })

  it('rejects unknown fields, methods, values, ordering, and semantic contradictions', () => {
    const snapshot = createSnapshot()
    const response = {
      version: 1,
      requestId,
      method: JOBS_GET_STATUS_METHOD,
      result: snapshot,
    }

    expect(parseJobResponse({ ...response, hostPath: 'C:\\private' })).toBeNull()
    expect(parseJobResponse({ ...response, method: 'jobs.delete' })).toBeNull()
    expect(
      parseJobResponse({
        ...response,
        result: { ...snapshot, progressMessage: '<img onerror=alert(1)>' },
      }),
    ).toBeNull()
    expect(
      parseJobResponse({ ...response, result: { ...snapshot, state: 'paused' } }),
    ).toBeNull()
    expect(
      parseJobResponse({
        ...response,
        result: { ...snapshot, updatedAt: '2026-02-31T01:02:03.0000000Z' },
      }),
    ).toBeNull()
    expect(
      parseJobResponse({
        ...response,
        result: {
          ...snapshot,
          warnings: ['variableRateFieldsDiffer', 'audioTrackAbsent'],
        },
      }),
    ).toBeNull()
    expect(
      parseJobResponse({
        ...response,
        result: { ...snapshot, state: 'queued', cancellationRequested: true },
      }),
    ).toBeNull()
  })

  it('rejects a response whose serialized representation exceeds the host boundary', () => {
    const response = {
      version: 1,
      requestId,
      method: JOBS_GET_STATUS_METHOD,
      result: createSnapshot(),
    }
    Object.defineProperty(response, 'toJSON', {
      enumerable: false,
      value: () => ({ payload: 'x'.repeat(5_000) }),
    })

    expect(parseJobResponse(response)).toBeNull()
  })
})

describe('job bridge client correlation', () => {
  it('delivers only the matching requested method once', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onJobResponse(listener)
    client.sendJobStatus(requestId, jobId)

    transport.emit({
      version: 1,
      requestId,
      method: JOBS_CANCEL_METHOD,
      error: { code: 'conflict' },
    })
    transport.emit({
      version: 1,
      requestId,
      method: JOBS_GET_STATUS_METHOD,
      result: createSnapshot({ jobId: otherJobId }),
    })
    transport.emit({
      version: 1,
      requestId,
      method: JOBS_GET_STATUS_METHOD,
      result: createSnapshot(),
    })
    transport.emit({
      version: 1,
      requestId,
      method: JOBS_GET_STATUS_METHOD,
      result: createSnapshot(),
    })

    expect(listener).toHaveBeenCalledOnce()
    expect(transport.posted).toEqual([createJobStatusRequest(requestId, jobId)])
  })

  it('removes pending correlation when the transport throws', () => {
    const transport = new FakeTransport()
    transport.throwOnPost = true
    const client = createBridgeClient(transport)

    expect(() => client.sendJobStatus(requestId, jobId)).toThrow('send failed')
    transport.throwOnPost = false
    expect(() => client.sendJobStatus(requestId, jobId)).not.toThrow()
  })

  it('drops a late response after its correlation is abandoned', () => {
    const transport = new FakeTransport()
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onJobResponse(listener)
    client.sendJobStatus(requestId, jobId)
    client.abandonJobRequest(requestId)

    transport.emit({
      version: 1,
      requestId,
      method: JOBS_GET_STATUS_METHOD,
      result: createSnapshot(),
    })

    expect(listener).not.toHaveBeenCalled()
  })
})

export function createSnapshot(
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

class FakeTransport implements BridgeTransport {
  readonly posted: unknown[] = []
  throwOnPost = false
  private readonly listeners = new Set<(event: BridgeMessageEvent) => void>()

  postMessage(message: Parameters<BridgeTransport['postMessage']>[0]): void {
    if (this.throwOnPost) {
      throw new Error('send failed')
    }

    this.posted.push(message)
  }

  addEventListener(
    _type: 'message',
    listener: (event: BridgeMessageEvent) => void,
  ): void {
    this.listeners.add(listener)
  }

  removeEventListener(
    _type: 'message',
    listener: (event: BridgeMessageEvent) => void,
  ): void {
    this.listeners.delete(listener)
  }

  emit(data: unknown): void {
    for (const listener of this.listeners) {
      listener({ data })
    }
  }
}