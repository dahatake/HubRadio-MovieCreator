import {
  createJobCancelRequest,
  createJobRetryRequest,
  createJobStatusRequest,
  createPingRequest,
  JOBS_GET_STATUS_METHOD,
  parseJobResponse,
  parsePingResponse,
  type BridgeJobMethod,
  type BridgeJobResponse,
  type BridgePingResponse,
  type BridgeRequest,
} from './protocol'
import {
  parseProjectBridgeResponse,
  type ProjectBridgeRequest,
  type ProjectBridgeResponse,
} from './projectProtocol'
import {
  createPlayerReadRequest,
  createPlayerSeekRequest,
  parsePlayerReadResponse,
  parsePlayerSeekResponse,
  type PlayerReadRequest,
  type PlayerReadResponse,
  type PlayerSeekRequest,
  type PlayerSeekResponse,
} from '../features/player/playerProtocol'
import {
  parseTranscriptResponse,
  type TranscriptRequest,
  type TranscriptResponse,
} from '../features/transcript/transcriptProtocol'
import {
  parseTimelineApplyResponse,
  parseTimelineReadResponse,
  type TimelineApplyRequest,
  type TimelineApplyResponse,
  type TimelineBridgeRequest,
  type TimelineReadRequest,
  type TimelineReadResponse,
} from '../features/timeline/timelineProtocol'
import {
  parseReviewBridgeResponse,
  type ReviewBridgeMethod,
  type ReviewBridgeRequest,
  type ReviewBridgeResponse,
} from '../features/review/reviewProtocol'

export * from './projectProtocol'
export * from '../features/timeline/timelineProtocol'
export * from '../features/review/reviewProtocol'

export interface BridgeMessageEvent {
  readonly data: unknown
}

export interface BridgeTransport {
  postMessage(
    message:
      | BridgeRequest
      | ProjectBridgeRequest
      | PlayerReadRequest
      | PlayerSeekRequest
      | TranscriptRequest
      | TimelineBridgeRequest
      | ReviewBridgeRequest,
  ): void
  addEventListener(type: 'message', listener: (event: BridgeMessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: BridgeMessageEvent) => void): void
}

export interface BridgeClient {
  sendPing(requestId: string): void
  sendJobStatus(requestId: string, jobId: string): void
  sendJobCancellation(requestId: string, jobId: string, expectedRevision: number): void
  sendJobRetry(requestId: string, jobId: string, expectedRevision: number): void
  abandonJobRequest(requestId: string): void
  onPingResponse(listener: (response: BridgePingResponse) => void): () => void
  sendProject(request: ProjectBridgeRequest): void
  onProjectResponse(listener: (response: ProjectBridgeResponse) => void): () => void
  onJobResponse(listener: (response: BridgeJobResponse) => void): () => void
  sendPlayerRead(requestId: string, jobId: string): void
  onPlayerReadResponse(
    listener: (response: PlayerReadResponse) => void,
  ): () => void
  sendPlayerSeek(
    requestId: string,
    jobId: string,
    evidenceDigest: string,
    proxyFrameSequence: number,
  ): void
  onPlayerSeekResponse(
    listener: (response: PlayerSeekResponse) => void,
  ): () => void
  sendTranscript(request: TranscriptRequest): void
  onTranscriptResponse(
    listener: (response: TranscriptResponse) => void,
  ): () => void
  sendTimelineRead(request: TimelineReadRequest): void
  sendTimelineApply(request: TimelineApplyRequest): void
  abandonTimelineRequest(requestId: string): void
  onTimelineReadResponse(
    listener: (response: TimelineReadResponse) => void,
  ): () => void
  onTimelineApplyResponse(
    listener: (response: TimelineApplyResponse) => void,
  ): () => void
  sendReview(request: ReviewBridgeRequest): void
  abandonReviewRequest(requestId: string): void
  onReviewResponse(
    listener: (response: ReviewBridgeResponse) => void,
  ): () => void
  dispose(): void
}

interface PendingJobRequest {
  readonly method: BridgeJobMethod
  readonly jobId: string
}

interface PendingTimelineRequest {
  readonly method: TimelineBridgeRequest['method']
  readonly projectId: string
}

interface PendingReviewRequest {
  readonly method: ReviewBridgeMethod
  readonly versionId: string
}

export function createBridgeClient(transport: BridgeTransport): BridgeClient {
  const pingListeners = new Set<(response: BridgePingResponse) => void>()
  const projectListeners = new Set<(response: ProjectBridgeResponse) => void>()
  const jobListeners = new Set<(response: BridgeJobResponse) => void>()
  const pendingJobRequests = new Map<string, PendingJobRequest>()
  const playerReadListeners = new Set<
    (response: PlayerReadResponse) => void
  >()
  const playerSeekListeners = new Set<
    (response: PlayerSeekResponse) => void
  >()
  const transcriptListeners = new Set<
    (response: TranscriptResponse) => void
  >()
  const timelineReadListeners = new Set<
    (response: TimelineReadResponse) => void
  >()
  const timelineApplyListeners = new Set<
    (response: TimelineApplyResponse) => void
  >()
  const pendingTimelineRequests = new Map<string, PendingTimelineRequest>()
  const reviewListeners = new Set<
    (response: ReviewBridgeResponse) => void
  >()
  const pendingReviewRequests = new Map<string, PendingReviewRequest>()
  let disposed = false

  const receive = (event: BridgeMessageEvent): void => {
    const pingResponse = parsePingResponse(event.data)
    if (pingResponse !== null) {
      for (const listener of pingListeners) {
        listener(pingResponse)
      }

      return
    }

    const jobResponse = parseJobResponse(event.data)
    if (jobResponse !== null) {
      const pending = pendingJobRequests.get(jobResponse.requestId)
      if (
        pending === undefined ||
        pending.method !== jobResponse.method ||
        ('result' in jobResponse && responseJobId(jobResponse) !== pending.jobId)
      ) {
        return
      }

      pendingJobRequests.delete(jobResponse.requestId)
      for (const listener of jobListeners) {
        listener(jobResponse)
      }

      return
    }

    const projectResponse = parseProjectBridgeResponse(event.data)
    if (projectResponse !== null) {
      for (const listener of projectListeners) {
        listener(projectResponse)
      }

      return
    }

    const reviewResponse = parseReviewBridgeResponse(event.data)
    if (reviewResponse !== null) {
      const pending = pendingReviewRequests.get(reviewResponse.requestId)
      if (
        pending === undefined ||
        pending.method !== reviewResponse.method ||
        ('result' in reviewResponse &&
          reviewResponse.result.versionId !== pending.versionId)
      ) {
        return
      }

      pendingReviewRequests.delete(reviewResponse.requestId)
      for (const listener of reviewListeners) {
        listener(reviewResponse)
      }
      return
    }

    const playerReadResponse = parsePlayerReadResponse(event.data)
    if (playerReadResponse !== null) {
      for (const listener of playerReadListeners) {
        listener(playerReadResponse)
      }
      return
    }

    const playerSeekResponse = parsePlayerSeekResponse(event.data)
    if (playerSeekResponse !== null) {
      for (const listener of playerSeekListeners) {
        listener(playerSeekResponse)
      }
      return
    }

    const transcriptResponse = parseTranscriptResponse(event.data)
    if (transcriptResponse !== null) {
      for (const listener of transcriptListeners) {
        listener(transcriptResponse)
      }
      return
    }

    const timelineReadResponse = parseTimelineReadResponse(event.data)
    if (timelineReadResponse !== null) {
      const pending = pendingTimelineRequests.get(timelineReadResponse.requestId)
      if (
        pending === undefined ||
        pending.method !== timelineReadResponse.method ||
        (timelineReadResponse.result.status === 'available' &&
          timelineReadResponse.result.snapshot.projectId !== pending.projectId)
      ) {
        return
      }

      pendingTimelineRequests.delete(timelineReadResponse.requestId)
      for (const listener of timelineReadListeners) {
        listener(timelineReadResponse)
      }
      return
    }

    const timelineApplyResponse = parseTimelineApplyResponse(event.data)
    if (timelineApplyResponse !== null) {
      const pending = pendingTimelineRequests.get(timelineApplyResponse.requestId)
      if (
        pending === undefined ||
        pending.method !== timelineApplyResponse.method ||
        (timelineApplyResponse.result.status === 'available' &&
          timelineApplyResponse.result.snapshot.projectId !== pending.projectId)
      ) {
        return
      }

      pendingTimelineRequests.delete(timelineApplyResponse.requestId)
      for (const listener of timelineApplyListeners) {
        listener(timelineApplyResponse)
      }
    }
  }

  transport.addEventListener('message', receive)

  return Object.freeze({
    sendPing(requestId: string): void {
      throwIfDisposed(disposed)
      transport.postMessage(createPingRequest(requestId))
    },
    sendJobStatus(requestId: string, jobId: string): void {
      sendCorrelatedJobRequest(
        createJobStatusRequest(requestId, jobId),
        transport,
        pendingJobRequests,
        disposed,
      )
    },
    sendJobCancellation(
      requestId: string,
      jobId: string,
      expectedRevision: number,
    ): void {
      sendCorrelatedJobRequest(
        createJobCancelRequest(requestId, jobId, expectedRevision),
        transport,
        pendingJobRequests,
        disposed,
      )
    },
    sendJobRetry(
      requestId: string,
      jobId: string,
      expectedRevision: number,
    ): void {
      sendCorrelatedJobRequest(
        createJobRetryRequest(requestId, jobId, expectedRevision),
        transport,
        pendingJobRequests,
        disposed,
      )
    },
    abandonJobRequest(requestId: string): void {
      throwIfDisposed(disposed)
      pendingJobRequests.delete(requestId)
    },
    onPingResponse(listener: (response: BridgePingResponse) => void): () => void {
      throwIfDisposed(disposed)
      pingListeners.add(listener)
      return (): void => {
        pingListeners.delete(listener)
      }
    },
    onJobResponse(listener: (response: BridgeJobResponse) => void): () => void {
      throwIfDisposed(disposed)
      jobListeners.add(listener)
      return (): void => {
        jobListeners.delete(listener)
      }
    },
    sendProject(request: ProjectBridgeRequest): void {
      throwIfDisposed(disposed)
      transport.postMessage(request)
    },
    onProjectResponse(listener: (response: ProjectBridgeResponse) => void): () => void {
      throwIfDisposed(disposed)
      projectListeners.add(listener)
      return (): void => {
        projectListeners.delete(listener)
      }
    },
    sendPlayerRead(requestId: string, jobId: string): void {
      throwIfDisposed(disposed)
      transport.postMessage(createPlayerReadRequest(requestId, jobId))
    },
    onPlayerReadResponse(
      listener: (response: PlayerReadResponse) => void,
    ): () => void {
      throwIfDisposed(disposed)
      playerReadListeners.add(listener)
      return (): void => {
        playerReadListeners.delete(listener)
      }
    },
    sendPlayerSeek(
      requestId: string,
      jobId: string,
      evidenceDigest: string,
      proxyFrameSequence: number,
    ): void {
      throwIfDisposed(disposed)
      transport.postMessage(
        createPlayerSeekRequest(
          requestId,
          jobId,
          evidenceDigest,
          proxyFrameSequence,
        ),
      )
    },
    onPlayerSeekResponse(
      listener: (response: PlayerSeekResponse) => void,
    ): () => void {
      throwIfDisposed(disposed)
      playerSeekListeners.add(listener)
      return (): void => {
        playerSeekListeners.delete(listener)
      }
    },
    sendTranscript(request: TranscriptRequest): void {
      throwIfDisposed(disposed)
      transport.postMessage(request)
    },
    onTranscriptResponse(
      listener: (response: TranscriptResponse) => void,
    ): () => void {
      throwIfDisposed(disposed)
      transcriptListeners.add(listener)
      return (): void => {
        transcriptListeners.delete(listener)
      }
    },
    sendTimelineRead(request: TimelineReadRequest): void {
      sendCorrelatedTimelineRequest(
        request,
        transport,
        pendingTimelineRequests,
        disposed,
      )
    },
    sendTimelineApply(request: TimelineApplyRequest): void {
      sendCorrelatedTimelineRequest(
        request,
        transport,
        pendingTimelineRequests,
        disposed,
      )
    },
    abandonTimelineRequest(requestId: string): void {
      throwIfDisposed(disposed)
      pendingTimelineRequests.delete(requestId)
    },
    onTimelineReadResponse(
      listener: (response: TimelineReadResponse) => void,
    ): () => void {
      throwIfDisposed(disposed)
      timelineReadListeners.add(listener)
      return (): void => {
        timelineReadListeners.delete(listener)
      }
    },
    onTimelineApplyResponse(
      listener: (response: TimelineApplyResponse) => void,
    ): () => void {
      throwIfDisposed(disposed)
      timelineApplyListeners.add(listener)
      return (): void => {
        timelineApplyListeners.delete(listener)
      }
    },
    sendReview(request: ReviewBridgeRequest): void {
      sendCorrelatedReviewRequest(
        request,
        transport,
        pendingReviewRequests,
        disposed,
      )
    },
    abandonReviewRequest(requestId: string): void {
      throwIfDisposed(disposed)
      pendingReviewRequests.delete(requestId)
    },
    onReviewResponse(
      listener: (response: ReviewBridgeResponse) => void,
    ): () => void {
      throwIfDisposed(disposed)
      reviewListeners.add(listener)
      return (): void => {
        reviewListeners.delete(listener)
      }
    },
    dispose(): void {
      if (disposed) {
        return
      }

      disposed = true
      pingListeners.clear()
      projectListeners.clear()
      jobListeners.clear()
      pendingJobRequests.clear()
      playerReadListeners.clear()
      playerSeekListeners.clear()
      transcriptListeners.clear()
      timelineReadListeners.clear()
      timelineApplyListeners.clear()
      pendingTimelineRequests.clear()
      reviewListeners.clear()
      pendingReviewRequests.clear()
      transport.removeEventListener('message', receive)
    },
  })
}

export function createHostBridgeClient(): BridgeClient {
  if (typeof window === 'undefined') {
    throw new Error('The WebView2 bridge is unavailable outside a browser window.')
  }

  const candidate = (window as WebViewWindow).chrome?.webview
  if (!isBridgeTransport(candidate)) {
    throw new Error('The WebView2 bridge transport is unavailable.')
  }

  return createBridgeClient(candidate)
}

interface WebViewWindow extends Window {
  readonly chrome?: {
    readonly webview?: unknown
  }
}

function isBridgeTransport(value: unknown): value is BridgeTransport {
  if (typeof value !== 'object' || value === null) {
    return false
  }

  const candidate = value as Partial<BridgeTransport>
  return (
    typeof candidate.postMessage === 'function' &&
    typeof candidate.addEventListener === 'function' &&
    typeof candidate.removeEventListener === 'function'
  )
}

function throwIfDisposed(disposed: boolean): void {
  if (disposed) {
    throw new Error('The WebView2 bridge client is disposed.')
  }
}

function sendCorrelatedJobRequest(
  request: BridgeRequest & Readonly<{ method: BridgeJobMethod }>,
  transport: BridgeTransport,
  pendingRequests: Map<string, PendingJobRequest>,
  disposed: boolean,
): void {
  throwIfDisposed(disposed)
  if (pendingRequests.has(request.requestId)) {
    throw new Error('The bridge request ID is already pending.')
  }

  pendingRequests.set(request.requestId, {
    method: request.method,
    jobId: request.params.jobId,
  })
  try {
    transport.postMessage(request)
  } catch (error: unknown) {
    pendingRequests.delete(request.requestId)
    throw error
  }
}

function sendCorrelatedTimelineRequest(
  request: TimelineBridgeRequest,
  transport: BridgeTransport,
  pendingRequests: Map<string, PendingTimelineRequest>,
  disposed: boolean,
): void {
  throwIfDisposed(disposed)
  if (pendingRequests.has(request.requestId)) {
    throw new Error('The timeline bridge request ID is already pending.')
  }

  pendingRequests.set(request.requestId, {
    method: request.method,
    projectId: request.params.projectId,
  })
  try {
    transport.postMessage(request)
  } catch (error: unknown) {
    pendingRequests.delete(request.requestId)
    throw error
  }
}

function responseJobId(
  response: Exclude<BridgeJobResponse, { readonly error: unknown }>,
): string {
  return response.method === JOBS_GET_STATUS_METHOD
    ? response.result.jobId
    : response.result.job.jobId
}

function sendCorrelatedReviewRequest(
  request: ReviewBridgeRequest,
  transport: BridgeTransport,
  pendingRequests: Map<string, PendingReviewRequest>,
  disposed: boolean,
): void {
  throwIfDisposed(disposed)
  if (pendingRequests.size !== 0) {
    throw new Error('A review bridge request is already pending.')
  }

  pendingRequests.set(request.requestId, {
    method: request.method,
    versionId: request.params.versionId,
  })
  try {
    transport.postMessage(request)
  } catch (error: unknown) {
    pendingRequests.delete(request.requestId)
    throw error
  }
}
