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
}

export type SyncStatus = 'unsaved' | 'pending' | 'acknowledged' | 'error'

export type AppAction =
  | Readonly<{ type: 'localChanged' }>
  | Readonly<{ type: 'syncRequested'; requestId: string }>
  | Readonly<{ type: 'syncAcknowledged'; requestId: string }>
  | Readonly<{ type: 'syncFailed'; requestId: string; error: SyncError }>
  | Readonly<{ type: 'bridgeUnavailable' }>