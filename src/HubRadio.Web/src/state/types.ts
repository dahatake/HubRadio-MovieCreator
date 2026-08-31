import type {
  BridgeErrorCode,
  BridgeJobResponse,
  BridgeJobSnapshot,
  JobCancellationOutcome,
  JobRetryOutcome,
} from '../bridge/protocol'

export type SyncError = 'bridge_unavailable' | 'bridge_send_failed'

export interface PendingSynchronization {
  readonly requestId: string
  readonly revision: number
}

export interface AppState {
  readonly localRevision: number
  readonly acknowledgedRevision: number | null
  readonly pending: PendingSynchronization | null
  readonly lastError: SyncError | null
  readonly jobMonitor: JobMonitorState
}

export type JobOperation = 'status' | 'cancel' | 'retry'
export type JobRequestError =
  | 'bridge_unavailable'
  | 'bridge_send_failed'
  | 'bridge_timeout'
  | BridgeErrorCode

export interface PendingJobRequest {
  readonly requestId: string
  readonly operation: JobOperation
  readonly jobId: string
}

export type JobCommandResult =
  | Readonly<{
      operation: 'cancel'
      outcome: JobCancellationOutcome
    }>
  | Readonly<{
      operation: 'retry'
      outcome: JobRetryOutcome
    }>

export interface JobMonitorState {
  readonly trackedJobId: string | null
  readonly snapshot: BridgeJobSnapshot | null
  readonly pending: PendingJobRequest | null
  readonly lastCommand: JobCommandResult | null
  readonly lastError: JobRequestError | null
}

export type SyncStatus = 'unsaved' | 'pending' | 'acknowledged' | 'error'

export type AppAction =
  | Readonly<{ type: 'localChanged' }>
  | Readonly<{ type: 'syncRequested'; requestId: string }>
  | Readonly<{ type: 'syncAcknowledged'; requestId: string }>
  | Readonly<{ type: 'syncFailed'; requestId: string; error: SyncError }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{
      type: 'jobRequestStarted'
      requestId: string
      operation: JobOperation
      jobId: string
    }>
  | Readonly<{ type: 'jobResponseReceived'; response: BridgeJobResponse }>
  | Readonly<{
      type: 'jobRequestFailed'
      requestId: string
      error: Extract<
        JobRequestError,
        'bridge_unavailable' | 'bridge_send_failed' | 'bridge_timeout'
      >
    }>
  | Readonly<{ type: 'jobTrackingStopped' }>