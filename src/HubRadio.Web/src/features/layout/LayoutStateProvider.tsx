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

import { createHostBridgeClient } from '../../bridge'
import {
  createLayoutApplyRequest,
  createLayoutReadRequest,
  type LayoutOperation,
} from './layoutProtocol'
import {
  startLayoutBridgeSession,
  type LayoutBridgeClient,
  type LayoutBridgeSession,
} from './layoutBridgeSession'
import {
  initialLayoutState,
  layoutBridgeUnavailable,
  layoutErrorCleared,
  layoutLocalInvalidRequest,
  layoutProjectIdChanged,
  layoutStateReducer,
  type LayoutUiState,
} from './layoutState'

export interface LayoutStateContextValue {
  readonly state: LayoutUiState
  readonly changeProjectId: (value: string) => void
  readonly loadSnapshot: () => void
  readonly refreshSnapshot: () => void
  readonly applyOperation: (operation: LayoutOperation) => void
  readonly clearError: () => void
}

export interface LayoutStateProviderProps {
  readonly children: ReactNode
  readonly createClient?: () => LayoutBridgeClient
}

const LayoutStateContext = createContext<LayoutStateContextValue | undefined>(
  undefined,
)
const missingProviderMessage =
  'useLayoutState must be used within a LayoutStateProvider.'

export function LayoutStateProvider({
  children,
  createClient = createHostBridgeClient,
}: LayoutStateProviderProps) {
  const [state, dispatch] = useReducer(layoutStateReducer, initialLayoutState)
  const sessionRef = useRef<LayoutBridgeSession | null>(null)

  useEffect(() => {
    const session = startLayoutBridgeSession(createClient, dispatch)
    sessionRef.current = session
    return () => {
      if (sessionRef.current === session) {
        sessionRef.current = null
      }
      session.dispose()
    }
  }, [createClient])

  const send = useCallback((request: Parameters<LayoutBridgeSession['send']>[0]) => {
    const session = sessionRef.current
    if (session === null) {
      dispatch(layoutBridgeUnavailable())
      return
    }
    session.send(request)
  }, [])

  const changeProjectId = useCallback((value: string) => {
    dispatch(layoutProjectIdChanged(value))
  }, [])

  const loadSnapshot = useCallback(() => {
    try {
      send(createLayoutReadRequest(
        createRequestId(),
        state.projectIdInput,
        null,
      ))
    } catch {
      dispatch(layoutLocalInvalidRequest())
    }
  }, [send, state.projectIdInput])

  const refreshSnapshot = useCallback(() => {
    loadSnapshot()
  }, [loadSnapshot])

  const applyOperation = useCallback((operation: LayoutOperation) => {
    const snapshot = state.snapshot
    if (snapshot === null) {
      dispatch(layoutLocalInvalidRequest())
      return
    }
    try {
      send(createLayoutApplyRequest(
        createRequestId(),
        snapshot.projectId,
        snapshot.revision,
        operation,
      ))
    } catch {
      dispatch(layoutLocalInvalidRequest())
    }
  }, [send, state.snapshot])

  const clearError = useCallback(() => {
    dispatch(layoutErrorCleared())
  }, [])

  const value = useMemo<LayoutStateContextValue>(
    () => ({
      state,
      changeProjectId,
      loadSnapshot,
      refreshSnapshot,
      applyOperation,
      clearError,
    }),
    [
      applyOperation,
      changeProjectId,
      clearError,
      loadSnapshot,
      refreshSnapshot,
      state,
    ],
  )

  return (
    <LayoutStateContext.Provider value={value}>
      {children}
    </LayoutStateContext.Provider>
  )
}

export function useLayoutState(): LayoutStateContextValue {
  const value = useContext(LayoutStateContext)
  if (value === undefined) {
    throw new Error(missingProviderMessage)
  }
  return value
}

function createRequestId(): string {
  return window.crypto.randomUUID().toLowerCase()
}
