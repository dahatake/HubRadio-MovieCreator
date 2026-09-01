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
import {
  parseComparisonReadResponse,
  type ComparisonReadRequest,
  type ComparisonReadResponse,
} from '../features/compare/compareProtocol'
import {
  deliveryOutputTimesEqual,
  parseDeliveryReadResponse,
  type DeliveryReadRequest,
  type DeliveryReadResponse,
} from '../features/delivery/deliveryProtocol'
import {
  createThumbnailCancelRequest,
  createThumbnailGenerateRequest,
  createThumbnailReadRequest,
  parseThumbnailCancelResponse,
  parseThumbnailGenerateResponse,
  parseThumbnailReadResponse,
  type ThumbnailBridgeRequest,
  type ThumbnailCancelResponse,
  type ThumbnailGenerateParams,
  type ThumbnailGenerateResponse,
  type ThumbnailReadResponse,
} from '../features/thumbnails/thumbnailProtocol'

export * from './projectProtocol'
export * from '../features/timeline/timelineProtocol'
export * from '../features/review/reviewProtocol'
export * from '../features/compare/compareProtocol'
export * from '../features/delivery/deliveryProtocol'

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
      | ReviewBridgeRequest
      | ComparisonReadRequest
      | DeliveryReadRequest
      | ThumbnailBridgeRequest,
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
  sendComparison(request: ComparisonReadRequest): void
  abandonComparisonRequest(requestId: string): void
  onComparisonResponse(
    listener: (response: ComparisonReadResponse) => void,
  ): () => void
  sendDelivery(request: DeliveryReadRequest): void
  abandonDeliveryRequest(requestId: string): void
  onDeliveryResponse(
    listener: (response: DeliveryReadResponse) => void,
  ): () => void
  sendThumbnailRead(requestId: string, jobId: string): void
  onThumbnailReadResponse(
    listener: (response: ThumbnailReadResponse) => void,
  ): () => void
  sendThumbnailGenerate(
    requestId: string,
    params: ThumbnailGenerateParams,
  ): void
  onThumbnailGenerateResponse(
    listener: (response: ThumbnailGenerateResponse) => void,
  ): () => void
  sendThumbnailCancellation(requestId: string, generationId: string): void
  onThumbnailCancelResponse(
    listener: (response: ThumbnailCancelResponse) => void,
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

interface PendingComparisonRequest {
  readonly leftVersionId: string
  readonly leftVersionDigest: string
  readonly rightVersionId: string
  readonly rightVersionDigest: string
}

interface PendingDeliveryRequest {
  readonly versionId: string
  readonly outputTime: DeliveryReadRequest['params']['outputTime']
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
  const comparisonListeners = new Set<
    (response: ComparisonReadResponse) => void
  >()
  const pendingComparisonRequests = new Map<
    string,
    PendingComparisonRequest
  >()
  const deliveryListeners = new Set<
    (response: DeliveryReadResponse) => void
  >()
  const pendingDeliveryRequests = new Map<string, PendingDeliveryRequest>()
  const thumbnailReadListeners = new Set<
    (response: ThumbnailReadResponse) => void
  >()
  const thumbnailGenerateListeners = new Set<
    (response: ThumbnailGenerateResponse) => void
  >()
  const thumbnailCancelListeners = new Set<
    (response: ThumbnailCancelResponse) => void
  >()
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

    const comparisonResponse = parseComparisonReadResponse(event.data)
    if (comparisonResponse !== null) {
      const pending = pendingComparisonRequests.get(comparisonResponse.requestId)
      const comparison = comparisonResponse.result.comparison
      if (
        pending === undefined ||
        (comparison !== null &&
          (comparison.left.versionId !== pending.leftVersionId ||
            comparison.left.versionDigest !== pending.leftVersionDigest ||
            comparison.right.versionId !== pending.rightVersionId ||
            comparison.right.versionDigest !== pending.rightVersionDigest))
      ) {
        return
      }

      pendingComparisonRequests.delete(comparisonResponse.requestId)
      for (const listener of comparisonListeners) {
        listener(comparisonResponse)
      }
      return
    }

    const deliveryResponse = parseDeliveryReadResponse(event.data)
    if (deliveryResponse !== null) {
      const pending = pendingDeliveryRequests.get(deliveryResponse.requestId)
      if (
        pending === undefined ||
        !deliveryResponseMatchesPending(deliveryResponse, pending)
      ) {
        return
      }

      pendingDeliveryRequests.delete(deliveryResponse.requestId)
      for (const listener of deliveryListeners) {
        listener(deliveryResponse)
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

    const thumbnailReadResponse = parseThumbnailReadResponse(event.data)
    if (thumbnailReadResponse !== null) {
      for (const listener of thumbnailReadListeners) {
        listener(thumbnailReadResponse)
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

    const thumbnailGenerateResponse = parseThumbnailGenerateResponse(event.data)
    if (thumbnailGenerateResponse !== null) {
      for (const listener of thumbnailGenerateListeners) {
        listener(thumbnailGenerateResponse)
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
      return
    }

    const thumbnailCancelResponse = parseThumbnailCancelResponse(event.data)
    if (thumbnailCancelResponse !== null) {
      for (const listener of thumbnailCancelListeners) {
        listener(thumbnailCancelResponse)
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
    sendComparison(request: ComparisonReadRequest): void {
      sendCorrelatedComparisonRequest(
        request,
        transport,
        pendingComparisonRequests,
        disposed,
      )
    },
    abandonComparisonRequest(requestId: string): void {
      throwIfDisposed(disposed)
      pendingComparisonRequests.delete(requestId)
    },
    onComparisonResponse(
      listener: (response: ComparisonReadResponse) => void,
    ): () => void {
      throwIfDisposed(disposed)
      comparisonListeners.add(listener)
      return (): void => {
        comparisonListeners.delete(listener)
      }
    },
    sendDelivery(request: DeliveryReadRequest): void {
      sendCorrelatedDeliveryRequest(
        request,
        transport,
        pendingDeliveryRequests,
        disposed,
      )
    },
    abandonDeliveryRequest(requestId: string): void {
      throwIfDisposed(disposed)
      pendingDeliveryRequests.delete(requestId)
    },
    onDeliveryResponse(
      listener: (response: DeliveryReadResponse) => void,
    ): () => void {
      throwIfDisposed(disposed)
      deliveryListeners.add(listener)
      return (): void => {
        deliveryListeners.delete(listener)
      }
    },
    sendThumbnailRead(requestId: string, jobId: string): void {
      throwIfDisposed(disposed)
      transport.postMessage(createThumbnailReadRequest(requestId, jobId))
    },
    onThumbnailReadResponse(
      listener: (response: ThumbnailReadResponse) => void,
    ): () => void {
      throwIfDisposed(disposed)
      thumbnailReadListeners.add(listener)
      return (): void => {
        thumbnailReadListeners.delete(listener)
      }
    },
    sendThumbnailGenerate(
      requestId: string,
      params: ThumbnailGenerateParams,
    ): void {
      throwIfDisposed(disposed)
      transport.postMessage(createThumbnailGenerateRequest(requestId, params))
    },
    onThumbnailGenerateResponse(
      listener: (response: ThumbnailGenerateResponse) => void,
    ): () => void {
      throwIfDisposed(disposed)
      thumbnailGenerateListeners.add(listener)
      return (): void => {
        thumbnailGenerateListeners.delete(listener)
      }
    },
    sendThumbnailCancellation(requestId: string, generationId: string): void {
      throwIfDisposed(disposed)
      transport.postMessage(
        createThumbnailCancelRequest(requestId, generationId),
      )
    },
    onThumbnailCancelResponse(
      listener: (response: ThumbnailCancelResponse) => void,
    ): () => void {
      throwIfDisposed(disposed)
      thumbnailCancelListeners.add(listener)
      return (): void => {
        thumbnailCancelListeners.delete(listener)
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
      comparisonListeners.clear()
      pendingComparisonRequests.clear()
      deliveryListeners.clear()
      pendingDeliveryRequests.clear()
      thumbnailReadListeners.clear()
      thumbnailGenerateListeners.clear()
      thumbnailCancelListeners.clear()
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

function sendCorrelatedComparisonRequest(
  request: ComparisonReadRequest,
  transport: BridgeTransport,
  pendingRequests: Map<string, PendingComparisonRequest>,
  disposed: boolean,
): void {
  throwIfDisposed(disposed)
  if (pendingRequests.size !== 0) {
    throw new Error('A comparison bridge request is already pending.')
  }

  pendingRequests.set(request.requestId, {
    leftVersionId: request.params.leftVersionId,
    leftVersionDigest: request.params.leftVersionDigest,
    rightVersionId: request.params.rightVersionId,
    rightVersionDigest: request.params.rightVersionDigest,
  })
  try {
    transport.postMessage(request)
  } catch (error: unknown) {
    pendingRequests.delete(request.requestId)
    throw error
  }
}

function sendCorrelatedDeliveryRequest(
  request: DeliveryReadRequest,
  transport: BridgeTransport,
  pendingRequests: Map<string, PendingDeliveryRequest>,
  disposed: boolean,
): void {
  throwIfDisposed(disposed)
  if (pendingRequests.size !== 0) {
    throw new Error('A delivery bridge request is already pending.')
  }

  pendingRequests.set(request.requestId, {
    versionId: request.params.versionId,
    outputTime: request.params.outputTime,
  })
  try {
    transport.postMessage(request)
  } catch (error: unknown) {
    pendingRequests.delete(request.requestId)
    throw error
  }
}

function deliveryResponseMatchesPending(
  response: DeliveryReadResponse,
  pending: PendingDeliveryRequest,
): boolean {
  if (response.result.status !== 'available') {
    return true
  }
  if (response.result.snapshot.versionId !== pending.versionId) {
    return false
  }

  const responseTime = response.result.trace?.outputTime ?? null
  return pending.outputTime === null
    ? responseTime === null
    : responseTime !== null &&
        deliveryOutputTimesEqual(responseTime, pending.outputTime)
}
