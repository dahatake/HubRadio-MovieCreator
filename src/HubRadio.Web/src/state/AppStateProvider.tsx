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
  localChanged,
  syncAcknowledged,
  syncFailed,
  syncRequested,
} from './appState'
import type { AppState } from './types'

export interface AppStateContextValue {
  readonly state: AppState
  readonly markChanged: () => void
  readonly synchronize: () => void
}

export interface AppStateProviderProps {
  readonly children: ReactNode
  readonly createClient?: () => BridgeClient
}

const AppStateContext = createContext<AppStateContextValue | undefined>(undefined)
const missingProviderMessage = 'useAppState must be used within an AppStateProvider.'

export function AppStateProvider({
  children,
  createClient = createHostBridgeClient,
}: AppStateProviderProps) {
  const [state, dispatch] = useReducer(appStateReducer, initialAppState)
  const clientRef = useRef<BridgeClient | null>(null)

  useEffect(() => {
    let client: BridgeClient
    try {
      client = createClient()
    } catch {
      dispatch(bridgeUnavailable())
      return
    }

    let unsubscribe: () => void
    try {
      unsubscribe = client.onPingResponse((response) => {
        dispatch(syncAcknowledged(response.requestId))
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
        unsubscribe()
      } finally {
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

  const value = useMemo<AppStateContextValue>(
    () => ({ state, markChanged, synchronize }),
    [markChanged, state, synchronize],
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