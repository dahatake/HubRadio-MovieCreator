import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useReducer,
  useRef,
  type ReactNode,
} from 'react'

import { createHostBridgeClient, type BridgeClient } from '../bridge'
import {
  appStateReducer,
  bridgeUnavailable,
  initialAppState,
  jobRequestFailed,
  jobRequestStarted,
  jobResponseReceived,
  jobTrackingStopped,
  localChanged,
  syncAcknowledged,
  syncFailed,
  syncRequested,
} from './appState'
import type { AppState, JobOperation } from './types'

export interface AppStateContextValue {
  readonly state: AppState
  readonly markChanged: () => void
  readonly synchronize: () => void
  readonly monitorJob: (jobId: string) => void
  readonly refreshJob: () => void
  readonly cancelJob: () => void
  readonly retryJob: () => void
  readonly stopMonitoringJob: () => void
}

export interface AppStateProviderProps {
  readonly children: ReactNode
  readonly createClient?: () => BridgeClient
}

const AppStateContext = createContext<AppStateContextValue | undefined>(undefined)
const missingProviderMessage = 'useAppState must be used within an AppStateProvider.'
const jobRequestTimeoutMilliseconds = 5_000

export function AppStateProvider({
  children,
  createClient = createHostBridgeClient,
}: AppStateProviderProps) {
  const [state, dispatch] = useReducer(appStateReducer, initialAppState)
  const clientRef = useRef<BridgeClient | null>(null)
  const pendingJobRequestRef = useRef<string | null>(null)
  const pendingJobTimeoutRef = useRef<number | null>(null)
  const stateRef = useRef(state)
  stateRef.current = state

  useEffect(() => {
    let client: BridgeClient
    try {
      client = createClient()
    } catch {
      dispatch(bridgeUnavailable())
      return
    }

    let unsubscribePing: () => void
    let unsubscribeJob: () => void
    try {
      unsubscribePing = client.onPingResponse((response) => {
        dispatch(syncAcknowledged(response.requestId))
      })
      unsubscribeJob = client.onJobResponse((response) => {
        if (pendingJobRequestRef.current === response.requestId) {
          pendingJobRequestRef.current = null
          if (pendingJobTimeoutRef.current !== null) {
            window.clearTimeout(pendingJobTimeoutRef.current)
            pendingJobTimeoutRef.current = null
          }
        }

        dispatch(jobResponseReceived(response))
      })
    } catch {
      client.dispose()
      dispatch(bridgeUnavailable())
      return
    }

    clientRef.current = client

    return () => {
      if (clientRef.current === client) {
        clientRef.current = null
      }

      try {
        unsubscribePing()
        unsubscribeJob()
      } finally {
        if (pendingJobTimeoutRef.current !== null) {
          window.clearTimeout(pendingJobTimeoutRef.current)
          pendingJobTimeoutRef.current = null
        }

        const pendingRequestId = pendingJobRequestRef.current
        if (pendingRequestId !== null) {
          try {
            client.abandonJobRequest(pendingRequestId)
          } catch {
            // The client is disposed immediately below; no transport state escapes.
          }
        }

        pendingJobRequestRef.current = null
        client.dispose()
      }
    }
  }, [createClient])

  const markChanged = useCallback(() => {
    dispatch(localChanged())
  }, [])

  const synchronize = useCallback(() => {
    const requestId = window.crypto.randomUUID().toLowerCase()
    dispatch(syncRequested(requestId))

    const client = clientRef.current
    if (client === null) {
      dispatch(bridgeUnavailable())
      return
    }

    try {
      client.sendPing(requestId)
    } catch {
      dispatch(syncFailed(requestId, 'bridge_send_failed'))
    }
  }, [])

  const sendJobRequest = useCallback(
    (operation: JobOperation, jobId: string, expectedRevision?: number) => {
      if (pendingJobRequestRef.current !== null) {
        return
      }

      const requestId = window.crypto.randomUUID().toLowerCase()
      pendingJobRequestRef.current = requestId
      dispatch(jobRequestStarted(requestId, operation, jobId))

      const client = clientRef.current
      if (client === null) {
        pendingJobRequestRef.current = null
        dispatch(jobRequestFailed(requestId, 'bridge_unavailable'))
        return
      }

      try {
        switch (operation) {
          case 'status':
            client.sendJobStatus(requestId, jobId)
            break
          case 'cancel':
            if (expectedRevision === undefined) {
              throw new Error('A cancellation requires the current job revision.')
            }

            client.sendJobCancellation(requestId, jobId, expectedRevision)
            break
          case 'retry':
            if (expectedRevision === undefined) {
              throw new Error('A retry requires the current job revision.')
            }

            client.sendJobRetry(requestId, jobId, expectedRevision)
            break
        }

        if (pendingJobRequestRef.current === requestId) {
          pendingJobTimeoutRef.current = window.setTimeout(() => {
            if (pendingJobRequestRef.current !== requestId) {
              return
            }

            pendingJobRequestRef.current = null
            pendingJobTimeoutRef.current = null
            try {
              client.abandonJobRequest(requestId)
            } catch {
              // Timeout remains authoritative even if disposal won the race.
            }

            dispatch(jobRequestFailed(requestId, 'bridge_timeout'))
          }, jobRequestTimeoutMilliseconds)
        }
      } catch {
        pendingJobRequestRef.current = null
        if (pendingJobTimeoutRef.current !== null) {
          window.clearTimeout(pendingJobTimeoutRef.current)
          pendingJobTimeoutRef.current = null
        }
        dispatch(jobRequestFailed(requestId, 'bridge_send_failed'))
      }
    },
    [],
  )

  const monitorJob = useCallback(
    (jobId: string) => {
      sendJobRequest('status', jobId)
    },
    [sendJobRequest],
  )

  const refreshJob = useCallback(() => {
    const jobId = stateRef.current.jobMonitor.trackedJobId
    if (jobId !== null) {
      sendJobRequest('status', jobId)
    }
  }, [sendJobRequest])

  const cancelJob = useCallback(() => {
    const snapshot = stateRef.current.jobMonitor.snapshot
    if (snapshot !== null) {
      sendJobRequest('cancel', snapshot.jobId, snapshot.revision)
    }
  }, [sendJobRequest])

  const retryJob = useCallback(() => {
    const snapshot = stateRef.current.jobMonitor.snapshot
    if (snapshot !== null) {
      sendJobRequest('retry', snapshot.jobId, snapshot.revision)
    }
  }, [sendJobRequest])

  const stopMonitoringJob = useCallback(() => {
    const pendingRequestId = pendingJobRequestRef.current
    if (pendingRequestId !== null) {
      clientRef.current?.abandonJobRequest(pendingRequestId)
    }

    if (pendingJobTimeoutRef.current !== null) {
      window.clearTimeout(pendingJobTimeoutRef.current)
      pendingJobTimeoutRef.current = null
    }

    pendingJobRequestRef.current = null
    dispatch(jobTrackingStopped())
  }, [])

  const value = useMemo<AppStateContextValue>(
    () => ({
      state,
      markChanged,
      synchronize,
      monitorJob,
      refreshJob,
      cancelJob,
      retryJob,
      stopMonitoringJob,
    }),
    [
      cancelJob,
      markChanged,
      monitorJob,
      refreshJob,
      retryJob,
      state,
      stopMonitoringJob,
      synchronize,
    ],
  )

  return <AppStateContext.Provider value={value}>{children}</AppStateContext.Provider>
}

export function useAppState(): AppStateContextValue {
  const value = useContext(AppStateContext)
  if (value === undefined) {
    throw new Error(missingProviderMessage)
  }

  return value
}