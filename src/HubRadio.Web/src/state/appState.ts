import {
  JOBS_CANCEL_METHOD,
  JOBS_GET_STATUS_METHOD,
  JOBS_RETRY_METHOD,
  type BridgeJobResponse,
  type BridgeJobSnapshot,
} from '../bridge/protocol'
import type {
  AppAction,
  AppState,
  JobCommandResult,
  JobOperation,
  SyncError,
} from './types'

export const initialAppState: AppState = {
  localRevision: 0,
  acknowledgedRevision: null,
  pending: null,
  lastError: null,
  jobMonitor: {
    trackedJobId: null,
    snapshot: null,
    pending: null,
    lastCommand: null,
    lastError: null,
  },
}

export function localChanged(): AppAction {
  return { type: 'localChanged' }
}

export function syncRequested(requestId: string): AppAction {
  return { type: 'syncRequested', requestId }
}

export function syncAcknowledged(requestId: string): AppAction {
  return { type: 'syncAcknowledged', requestId }
}

export function syncFailed(requestId: string, error: SyncError): AppAction {
  return { type: 'syncFailed', requestId, error }
}

export function bridgeUnavailable(): AppAction {
  return { type: 'bridgeUnavailable' }
}

export function jobRequestStarted(
  requestId: string,
  operation: JobOperation,
  jobId: string,
): AppAction {
  return { type: 'jobRequestStarted', requestId, operation, jobId }
}

export function jobResponseReceived(response: BridgeJobResponse): AppAction {
  return { type: 'jobResponseReceived', response }
}

export function jobRequestFailed(
  requestId: string,
  error: 'bridge_unavailable' | 'bridge_send_failed' | 'bridge_timeout',
): AppAction {
  return { type: 'jobRequestFailed', requestId, error }
}

export function jobTrackingStopped(): AppAction {
  return { type: 'jobTrackingStopped' }
}

export function appStateReducer(state: AppState, action: AppAction): AppState {
  switch (action.type) {
    case 'localChanged':
      return {
        ...state,
        localRevision: state.localRevision + 1,
      }

    case 'syncRequested':
      return {
        ...state,
        pending: {
          requestId: action.requestId,
          revision: state.localRevision,
        },
        lastError: null,
      }

    case 'syncAcknowledged': {
      const pending = state.pending
      if (pending === null || pending.requestId !== action.requestId) {
        return state
      }

      return {
        ...state,
        acknowledgedRevision: pending.revision,
        pending: null,
      }
    }

    case 'syncFailed':
      if (state.pending === null || state.pending.requestId !== action.requestId) {
        return state
      }

      return {
        ...state,
        pending: null,
        lastError: action.error,
      }

    case 'bridgeUnavailable':
      return {
        ...state,
        pending: null,
        lastError: 'bridge_unavailable',
      }

    case 'jobRequestStarted': {
      const monitor = state.jobMonitor
      if (monitor.pending !== null) {
        return state
      }

      const startsTracking = action.operation === 'status'
      if (!startsTracking && monitor.trackedJobId !== action.jobId) {
        return state
      }

      const switchesJob = monitor.trackedJobId !== action.jobId
      return {
        ...state,
        jobMonitor: {
          trackedJobId: action.jobId,
          snapshot: switchesJob ? null : monitor.snapshot,
          pending: {
            requestId: action.requestId,
            operation: action.operation,
            jobId: action.jobId,
          },
          lastCommand:
            switchesJob || action.operation !== 'status'
              ? null
              : monitor.lastCommand,
          lastError: null,
        },
      }
    }

    case 'jobResponseReceived':
      return reduceJobResponse(state, action.response)

    case 'jobRequestFailed': {
      const pendingJob = state.jobMonitor.pending
      if (pendingJob === null || pendingJob.requestId !== action.requestId) {
        return state
      }

      return {
        ...state,
        jobMonitor: {
          ...state.jobMonitor,
          pending: null,
          lastError: action.error,
        },
      }
    }

    case 'jobTrackingStopped':
      if (
        state.jobMonitor.trackedJobId === null &&
        state.jobMonitor.pending === null
      ) {
        return state
      }

      return {
        ...state,
        jobMonitor: initialAppState.jobMonitor,
      }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}

function reduceJobResponse(
  state: AppState,
  response: BridgeJobResponse,
): AppState {
  const monitor = state.jobMonitor
  const pending = monitor.pending
  if (
    pending === null ||
    pending.requestId !== response.requestId ||
    operationForMethod(response.method) !== pending.operation
  ) {
    return state
  }

  if ('error' in response) {
    return {
      ...state,
      jobMonitor: {
        ...monitor,
        pending: null,
        lastError: response.error.code,
      },
    }
  }

  const snapshot =
    response.method === JOBS_GET_STATUS_METHOD
      ? response.result
      : response.result.job
  if (
    snapshot.jobId !== pending.jobId ||
    snapshot.jobId !== monitor.trackedJobId
  ) {
    return state
  }

  const acceptedSnapshot = shouldAcceptSnapshot(monitor.snapshot, snapshot)
    ? snapshot
    : monitor.snapshot
  const command = commandResult(response)
  return {
    ...state,
    jobMonitor: {
      ...monitor,
      snapshot: acceptedSnapshot,
      pending: null,
      lastCommand: command ?? monitor.lastCommand,
      lastError: null,
    },
  }
}

function operationForMethod(
  method:
    | typeof JOBS_GET_STATUS_METHOD
    | typeof JOBS_CANCEL_METHOD
    | typeof JOBS_RETRY_METHOD,
): JobOperation {
  switch (method) {
    case JOBS_GET_STATUS_METHOD:
      return 'status'
    case JOBS_CANCEL_METHOD:
      return 'cancel'
    case JOBS_RETRY_METHOD:
      return 'retry'
  }
}

function commandResult(
  response: Exclude<BridgeJobResponse, { readonly error: unknown }>,
): JobCommandResult | null {
  switch (response.method) {
    case JOBS_GET_STATUS_METHOD:
      return null
    case JOBS_CANCEL_METHOD:
      return { operation: 'cancel', outcome: response.result.outcome }
    case JOBS_RETRY_METHOD:
      return { operation: 'retry', outcome: response.result.outcome }
  }
}

function shouldAcceptSnapshot(
  current: BridgeJobSnapshot | null,
  incoming: BridgeJobSnapshot,
): boolean {
  if (current === null) {
    return true
  }

  if (
    current.jobId !== incoming.jobId ||
    incoming.revision < current.revision ||
    incoming.currentAttemptNumber < current.currentAttemptNumber
  ) {
    return false
  }

  if (
    incoming.revision > current.revision ||
    incoming.currentAttemptNumber > current.currentAttemptNumber
  ) {
    return incoming.revision > current.revision
  }

  if (
    incoming.kind !== current.kind ||
    incoming.state !== current.state ||
    incoming.currentAttemptId !== current.currentAttemptId ||
    incoming.updatedAt !== current.updatedAt ||
    incoming.failure !== current.failure ||
    incoming.retryability !== current.retryability ||
    isNullableNumberOlder(incoming.detailsRevision, current.detailsRevision) ||
    isNullableTimestampOlder(incoming.lastProgressAt, current.lastProgressAt) ||
    isNullableTimestampOlder(incoming.lastHeartbeatAt, current.lastHeartbeatAt) ||
    (current.cancellationRequested && !incoming.cancellationRequested)
  ) {
    return false
  }

  const detailsAdvanced =
    incoming.detailsRevision !== current.detailsRevision &&
    incoming.detailsRevision !== null
  const progressAdvanced = incoming.lastProgressAt !== current.lastProgressAt
  const heartbeatAdvanced = incoming.lastHeartbeatAt !== current.lastHeartbeatAt
  const cancellationAdvanced =
    incoming.cancellationRequested && !current.cancellationRequested
  const reconciliationChanged = incoming.reconciliation !== current.reconciliation

  if (!detailsAdvanced && !sameWarnings(incoming.warnings, current.warnings)) {
    return false
  }

  if (!progressAdvanced && incoming.progressRatio !== current.progressRatio) {
    return false
  }

  return (
    detailsAdvanced ||
    progressAdvanced ||
    heartbeatAdvanced ||
    cancellationAdvanced ||
    reconciliationChanged ||
    !sameSnapshot(incoming, current)
  )
}

function isNullableNumberOlder(
  incoming: number | null,
  current: number | null,
): boolean {
  return (
    (incoming === null && current !== null) ||
    (incoming !== null && current !== null && incoming < current)
  )
}

function isNullableTimestampOlder(
  incoming: string | null,
  current: string | null,
): boolean {
  return (
    (incoming === null && current !== null) ||
    (incoming !== null && current !== null && incoming < current)
  )
}

function sameWarnings(
  left: readonly string[],
  right: readonly string[],
): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index])
}

function sameSnapshot(
  left: BridgeJobSnapshot,
  right: BridgeJobSnapshot,
): boolean {
  return (
    left.jobId === right.jobId &&
    left.kind === right.kind &&
    left.state === right.state &&
    left.currentAttemptId === right.currentAttemptId &&
    left.currentAttemptNumber === right.currentAttemptNumber &&
    left.revision === right.revision &&
    left.detailsRevision === right.detailsRevision &&
    left.updatedAt === right.updatedAt &&
    left.progressRatio === right.progressRatio &&
    left.lastProgressAt === right.lastProgressAt &&
    left.lastHeartbeatAt === right.lastHeartbeatAt &&
    left.failure === right.failure &&
    left.retryability === right.retryability &&
    left.cancellationRequested === right.cancellationRequested &&
    sameWarnings(left.warnings, right.warnings) &&
    left.reconciliation === right.reconciliation
  )
}