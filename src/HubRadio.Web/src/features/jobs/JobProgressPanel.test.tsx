// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { BridgeClient } from '../../bridge'
import {
  JOBS_CANCEL_METHOD,
  JOBS_GET_STATUS_METHOD,
  JOBS_RETRY_METHOD,
  type BridgeJobResponse,
  type BridgeJobSnapshot,
} from '../../bridge/protocol'
import { AppStateProvider } from '../../state'
import { JobProgressPanel, type JobProgressPanelProps } from './JobProgressPanel'
import { requestErrorLabels } from './jobPresentation'

const jobId = '11112222-3333-4444-8555-666677778888'
const otherJobId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'

describe('JobProgressPanel keyboard flow', () => {
  it('submits a canonical job lookup with typed keyboard input and Enter', async () => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
    const container = document.createElement('div')
    document.body.append(container)
    const root = createRoot(container)
    const client = new FakeJobClient()

    try {
      act(() => {
        root.render(
          <AppStateProvider
            createClient={() => client as unknown as BridgeClient}
          >
            <JobProgressPanel />
          </AppStateProvider>,
        )
      })
      const user = userEvent.setup({ document })
      const input = requiredElement<HTMLInputElement>(container, '#job-id')
      act(() => input.focus())

      await act(async () => user.keyboard(jobId))
      expect(input.value).toBe(jobId)
      await act(async () => user.keyboard('[Enter]'))

      expect(client.statusRequests).toHaveLength(1)
      expect(client.statusRequests[0]?.jobId).toBe(jobId)
      expect(
        requiredElement<HTMLButtonElement>(
          container,
          '.job-lookup button[type="submit"]',
        ).disabled,
      ).toBe(true)
    } finally {
      act(() => root.unmount())
      container.remove()
      delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
        .IS_REACT_ACT_ENVIRONMENT
    }

    expect(client.disposed).toBe(true)
    expect(client.abandoned).toHaveLength(1)
  })
})

describe('JobProgressPanel workflow context', () => {
  let panel: ReturnType<typeof createPanelHarness>

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
    vi.useFakeTimers()
    panel = createPanelHarness()
  })

  afterEach(() => {
    panel.dispose()
    vi.restoreAllMocks()
    vi.useRealTimers()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  it('monitors a canonical context once across state updates and fresh context objects', async () => {
    await panel.render({ context: { jobId } })
    expect(panel.client.statusRequests.map((request) => request.jobId)).toEqual([jobId])
    expect(panel.container.querySelector('.job-lookup')).toBeNull()
    expect(panel.container.querySelector('input')).toBeNull()

    await panel.render({ context: { jobId } })
    act(() => panel.client.respondToLatestStatus())
    await panel.render({ context: { jobId }, active: true })

    expect(panel.client.statusRequests).toHaveLength(1)
    expect(panel.client.abandoned).toEqual([])
    expect(panel.container.textContent).toContain('実行中')
    expect(panel.container.textContent).not.toContain(jobId)
  })

  it('supports the jobId shorthand and the undefined standalone form', async () => {
    await panel.render({ jobId })
    expect(panel.client.statusRequests.map((request) => request.jobId)).toEqual([jobId])
    expect(panel.container.querySelector('.job-lookup')).toBeNull()

    await panel.render({ jobId: null })
    expect(panel.container.querySelector('.job-lookup')).toBeNull()
    expect(panel.container.textContent).toContain('未開始')

    await panel.render({})
    expect(panel.container.querySelector('#job-id')).not.toBeNull()
    expect(panel.client.statusRequests).toHaveLength(1)
  })

  it.each([
    { name: 'jobId null', props: { jobId: null } },
    { name: 'context null', props: { context: { jobId: null } } },
    { name: 'context null over a shorthand ID', props: { context: { jobId: null }, jobId } },
  ])('shows an unstarted panel for $name without any lookup', async ({ props }) => {
    await panel.render(props)

    expect(panel.container.textContent).toContain('未開始')
    expect(panel.container.querySelector('form')).toBeNull()
    expect(panel.container.querySelector('.job-card')).toBeNull()
    expect(panel.client.statusRequests).toEqual([])
    expect(panel.client.abandoned).toEqual([])
    expect(panel.client.cancellationRequests).toEqual([])
  })

  it('keeps the poll interval and pending request across inactive and active transitions', async () => {
    await panel.render({ context: { jobId }, active: true })
    act(() => panel.client.respondToLatestStatus())
    const clearInterval = vi.spyOn(window, 'clearInterval')

    act(() => { vi.advanceTimersByTime(750) })
    await panel.render({ context: { jobId }, active: false })
    expect(requiredElement<HTMLElement>(panel.container, 'section').hidden).toBe(true)
    act(() => { vi.advanceTimersByTime(750) })
    expect(panel.client.statusRequests).toHaveLength(2)

    await panel.render({ context: { jobId }, active: true })
    expect(requiredElement<HTMLElement>(panel.container, 'section').hidden).toBe(false)
    expect(panel.client.statusRequests).toHaveLength(2)
    act(() => panel.client.respondToLatestStatus())
    act(() => { vi.advanceTimersByTime(1_500) })

    expect(panel.client.statusRequests).toHaveLength(3)
    expect(clearInterval).not.toHaveBeenCalled()
    expect(panel.client.abandoned).toEqual([])
    expect(panel.client.cancellationRequests).toEqual([])
    expect(panel.container.textContent).toContain('実行中')
  })

  it('starts monitoring and polls even when initially inactive', async () => {
    await panel.render({ context: { jobId }, active: false })
    expect(panel.client.statusRequests).toHaveLength(1)
    act(() => panel.client.respondToLatestStatus({ state: 'queued' }))
    act(() => { vi.advanceTimersByTime(1_500) })

    expect(panel.client.statusRequests).toHaveLength(2)
    expect(panel.client.abandoned).toEqual([])
    expect(panel.client.cancellationRequests).toEqual([])
  })

  it('clears the previous project snapshot and pending request on explicit null', async () => {
    await panel.render({ context: { jobId } })
    act(() => panel.client.respondToLatestStatus({
      state: 'failed',
      failure: 'timedOut',
      retryability: 'retryable',
    }))
    expect(panel.container.textContent).toContain('処理がタイムアウトしました。')
    act(() => requiredElement<HTMLButtonElement>(
      panel.container, '.job-actions .button-secondary',
    ).click())
    const pendingRequest = latest(panel.client.statusRequests)

    await panel.render({ context: { jobId: null } })
    expect(panel.client.abandoned).toEqual([pendingRequest.requestId])
    expect(panel.container.textContent).toContain('未開始')
    expect(panel.container.textContent).not.toContain('メディア取り込み')
    expect(panel.container.textContent).not.toContain('処理がタイムアウトしました。')
    expect(panel.container.querySelector('.job-status-region')).toBeNull()
    expect(panel.container.querySelector('form')).toBeNull()

    act(() => panel.client.respondToLatestStatus())
    act(() => { vi.advanceTimersByTime(10_000) })
    expect(panel.container.querySelector('.job-card')).toBeNull()
    expect(panel.client.statusRequests).toHaveLength(2)
    expect(panel.client.cancellationRequests).toEqual([])
  })

  it('switches changed IDs despite an old pending request and ignores its late response', async () => {
    await panel.render({ context: { jobId } })
    const oldRequest = latest(panel.client.statusRequests)
    await panel.render({ context: { jobId: otherJobId } })

    expect(panel.client.statusRequests.map((request) => request.jobId)).toEqual([jobId, otherJobId])
    expect(panel.client.abandoned).toEqual([oldRequest.requestId])
    act(() => panel.client.emit({
      version: 1,
      requestId: oldRequest.requestId,
      method: JOBS_GET_STATUS_METHOD,
      result: createSnapshot(),
    }))
    expect(panel.container.querySelector('.job-card')).toBeNull()

    act(() => panel.client.respondToLatestStatus({
      kind: 'finalRender', detailsRevision: null, reconciliation: 'notApplicable',
    }))
    await panel.render({ context: { jobId: otherJobId } })
    expect(panel.container.textContent).toContain('最終レンダー')
    expect(panel.container.textContent).not.toContain('メディア取り込み')
    expect(panel.client.statusRequests).toHaveLength(2)
    expect(panel.client.cancellationRequests).toEqual([])
  })

  it.each([
    ['empty', ''],
    ['whitespace', '   '],
    ['malformed', 'not-a-job-id'],
    ['nil UUID', '00000000-0000-0000-0000-000000000000'],
    ['uppercase UUID', otherJobId.toUpperCase()],
    ['padded UUID', ` ${jobId}`],
    ['newline suffix', `${jobId}\n`],
  ])('fails closed for a %s context ID without exposing a lookup', async (_name, invalidJobId) => {
    await panel.render({ context: { jobId: invalidJobId } })

    expect(panel.container.querySelector('form')).toBeNull()
    expect(panel.container.querySelector('input')).toBeNull()
    expect(panel.container.textContent).toContain('未取得')
    expect(panel.client.statusRequests).toEqual([])
    expect(panel.client.cancellationRequests).toEqual([])
  })

  it('removes stale state when a supplied ID becomes invalid rather than falling back to lookup', async () => {
    await panel.render({ jobId })
    act(() => panel.client.respondToLatestStatus())
    await panel.render({ jobId: 'invalid' })
    act(() => { vi.advanceTimersByTime(10_000) })

    expect(panel.container.textContent).not.toContain('実行中')
    expect(panel.container.textContent).toContain('未取得')
    expect(panel.container.querySelector('form')).toBeNull()
    expect(panel.client.statusRequests).toHaveLength(1)
    expect(panel.client.cancellationRequests).toEqual([])
  })

  it.each(['forbidden', 'notFound'] as const)(
    'keeps %s failures visible without lookup and allows a pending-safe refresh',
    async (code) => {
      await panel.render({ context: { jobId } })
      act(() => panel.client.emit({
        version: 1,
        requestId: latest(panel.client.statusRequests).requestId,
        method: JOBS_GET_STATUS_METHOD,
        error: { code },
      }))
      await panel.render({ context: { jobId } })
      act(() => { vi.advanceTimersByTime(6_000) })
      expect(panel.client.statusRequests).toHaveLength(1)
      expect(panel.container.textContent).toContain(requestErrorLabels[code])
      expect(panel.container.querySelector('form')).toBeNull()

      const refresh = requiredElement<HTMLButtonElement>(
        panel.container, '.job-actions .button-secondary',
      )
      expect(refresh.disabled).toBe(false)
      act(() => refresh.click())
      expect(refresh.disabled).toBe(true)
      act(() => refresh.click())
      expect(panel.client.statusRequests).toHaveLength(2)
      act(() => panel.client.respondToLatestStatus())
      expect(panel.container.querySelector('.request-error')).toBeNull()
      expect(panel.container.textContent).toContain('実行中')
    },
  )

  it('continues conflict recovery polling without a snapshot in the background', async () => {
    await panel.render({ context: { jobId }, active: false })
    act(() => panel.client.emit({
      version: 1,
      requestId: latest(panel.client.statusRequests).requestId,
      method: JOBS_GET_STATUS_METHOD,
      error: { code: 'conflict' },
    }))
    act(() => { vi.advanceTimersByTime(1_500) })

    expect(panel.client.statusRequests).toHaveLength(2)
    expect(panel.client.abandoned).toEqual([])
    expect(panel.client.cancellationRequests).toEqual([])
  })

  it('folds only technical metadata into one closed details and preserves retry feedback', async () => {
    await panel.render({ context: { jobId } })
    act(() => panel.client.respondToLatestStatus({
      state: 'failed', failure: 'timedOut', retryability: 'retryable',
    }))
    const details = requiredElement<HTMLDetailsElement>(panel.container, 'details')
    expect(panel.container.querySelectorAll('details')).toHaveLength(1)
    expect(details.open).toBe(false)
    expect(details.querySelector('summary')?.textContent).toBe('処理の詳細')
    expect(Array.from(details.querySelectorAll('dt'), (item) => item.textContent))
      .toEqual(['試行', '更新日時', 'リビジョン'])
    expect(details.textContent).not.toContain('処理がタイムアウトしました。')
    expect(panel.container.querySelector('.notice')?.textContent)
      .toContain('処理がタイムアウトしました。')

    const retry = requiredElement<HTMLButtonElement>(panel.container, '.job-actions .button-primary')
    act(() => retry.click())
    expect(retry.disabled).toBe(true)
    act(() => retry.click())
    expect(panel.client.retryRequests).toHaveLength(1)
    const request = latest(panel.client.retryRequests)
    expect(request.jobId).toBe(jobId)
    expect(request.expectedRevision).toBe(2)
    act(() => panel.client.emit({
      version: 1,
      requestId: request.requestId,
      method: JOBS_RETRY_METHOD,
      result: {
        outcome: 'queued',
        job: createSnapshot({ state: 'queued', revision: 3, progressRatio: null }),
      },
    }))
    expect(panel.container.textContent).toContain('再キュー済み')
    expect(panel.container.textContent).toContain('ジョブを再キューしました。')
  })

  it.each(['failed', 'interrupted'] as const)(
    'keeps %s failures, retry restrictions and reconciliation outside details',
    async (state) => {
      await panel.render({ context: { jobId } })
      act(() => panel.client.respondToLatestStatus({
        state,
        failure: 'appRestarted',
        retryability: 'nonRetryable',
        reconciliation: 'required',
        warnings: ['lowResolution'],
      }))
      expect(requiredElement<HTMLButtonElement>(
        panel.container, '.job-actions .button-primary',
      ).disabled).toBe(true)
      expect(panel.container.textContent).toContain('このジョブは安全に再試行できません。')
      expect(panel.container.querySelector('.notice-warning')?.textContent)
        .toContain('再解析してから続行してください。')
      expect(panel.container.querySelector('.warning-list')?.textContent)
        .toContain('映像の解像度が低い可能性があります。')
      expect(panel.container.querySelector('.notice-neutral')?.closest('details')).toBeNull()
    },
  )

  it('preserves indeterminate progress and pending-safe cancellation feedback', async () => {
    await panel.render({ context: { jobId } })
    act(() => panel.client.respondToLatestStatus({ progressRatio: null }))
    expect(requiredElement<HTMLProgressElement>(panel.container, 'progress').hasAttribute('value'))
      .toBe(false)
    const cancel = requiredElement<HTMLButtonElement>(panel.container, '.job-actions .button-danger')
    act(() => cancel.click())
    expect(cancel.disabled).toBe(true)
    act(() => cancel.click())
    expect(panel.client.cancellationRequests).toHaveLength(1)
    const request = latest(panel.client.cancellationRequests)
    expect(request.expectedRevision).toBe(2)
    act(() => panel.client.emit({
      version: 1,
      requestId: request.requestId,
      method: JOBS_CANCEL_METHOD,
      result: {
        outcome: 'requested',
        job: createSnapshot({ revision: 3, progressRatio: null, cancellationRequested: true }),
      },
    }))
    expect(panel.container.textContent).toContain('キャンセル要求済み')
    expect(panel.container.textContent).toContain('キャンセルを要求しました。')
    expect(panel.container.querySelector('.job-actions .button-danger')).toBeNull()
  })
})

function createPanelHarness() {
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  const client = new FakeJobClient()
  // Keep the provider client factory stable when only panel props change.
  const createClient = () => client as unknown as BridgeClient

  return {
    container,
    client,
    async render(props: JobProgressPanelProps) {
      await act(async () => {
        root.render(
          <AppStateProvider createClient={createClient}>
            <JobProgressPanel {...props} />
          </AppStateProvider>,
        )
      })
    },
    dispose() {
      act(() => root.unmount())
      container.remove()
    },
  }
}

function createSnapshot(overrides: Partial<BridgeJobSnapshot> = {}): BridgeJobSnapshot {
  return {
    jobId,
    kind: 'mediaIngest',
    state: 'running',
    currentAttemptId: '99999999-8888-4777-8666-555555555555',
    currentAttemptNumber: 1,
    revision: 2,
    detailsRevision: 1,
    updatedAt: '2026-09-07T01:00:00.0000000Z',
    progressRatio: 0.4,
    lastProgressAt: '2026-09-07T01:00:00.0000000Z',
    lastHeartbeatAt: '2026-09-07T01:00:00.0000000Z',
    failure: null,
    retryability: 'notApplicable',
    cancellationRequested: false,
    warnings: [],
    reconciliation: 'notRequired',
    ...overrides,
  }
}

class FakeJobClient {
  readonly statusRequests: Array<Readonly<{
    requestId: string
    jobId: string
  }>> = []
  readonly cancellationRequests: Array<Readonly<{
    requestId: string
    jobId: string
    expectedRevision: number
  }>> = []
  readonly retryRequests: Array<Readonly<{
    requestId: string
    jobId: string
    expectedRevision: number
  }>> = []
  readonly abandoned: string[] = []
  disposed = false
  private jobListener: ((response: BridgeJobResponse) => void) | null = null

  onPingResponse(): () => void {
    return () => undefined
  }

  onJobResponse(listener: (response: BridgeJobResponse) => void): () => void {
    this.jobListener = listener
    return () => { this.jobListener = null }
  }

  sendJobStatus(requestId: string, requestedJobId: string): void {
    this.statusRequests.push({ requestId, jobId: requestedJobId })
  }

  sendJobCancellation(requestId: string, requestedJobId: string, expectedRevision: number): void {
    this.cancellationRequests.push({ requestId, jobId: requestedJobId, expectedRevision })
  }

  sendJobRetry(requestId: string, requestedJobId: string, expectedRevision: number): void {
    this.retryRequests.push({ requestId, jobId: requestedJobId, expectedRevision })
  }

  emit(response: BridgeJobResponse): void {
    this.jobListener?.(response)
  }

  respondToLatestStatus(overrides: Partial<BridgeJobSnapshot> = {}): void {
    const request = latest(this.statusRequests)
    this.emit({
      version: 1,
      requestId: request.requestId,
      method: JOBS_GET_STATUS_METHOD,
      result: createSnapshot({ jobId: request.jobId, ...overrides }),
    })
  }

  abandonJobRequest(requestId: string): void {
    this.abandoned.push(requestId)
  }

  dispose(): void {
    this.disposed = true
  }
}

function latest<T>(items: readonly T[]): T {
  const item = items.at(-1)
  if (item === undefined) {
    throw new Error('Expected a recorded job request.')
  }
  return item
}

function requiredElement<T extends Element>(
  parent: ParentNode,
  selector: string,
): T {
  const element = parent.querySelector(selector)
  if (element === null) {
    throw new Error(`Expected element matching ${selector}.`)
  }
  return element as T
}
