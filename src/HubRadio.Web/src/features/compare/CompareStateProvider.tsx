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
import { createComparisonReadRequest } from './compareProtocol'
import {
  startComparisonBridgeSession,
  type ComparisonBridgeClient,
  type ComparisonBridgeSession,
} from './compareBridgeSession'
import {
  comparisonBridgeUnavailable,
  comparisonErrorCleared,
  comparisonInputChanged,
  comparisonLocalInvalidRequest,
  comparisonStateReducer,
  initialComparisonState,
  type ComparisonInputField,
  type ComparisonUiState,
} from './compareState'

export interface ComparisonStateContextValue {
  readonly state: ComparisonUiState
  readonly changeInput: (field: ComparisonInputField, value: string) => void
  readonly compareVersions: () => void
  readonly clearError: () => void
}

export interface ComparisonStateProviderProps {
  readonly children: ReactNode
  readonly createClient?: () => ComparisonBridgeClient
}

const ComparisonStateContext = createContext<
  ComparisonStateContextValue | undefined
>(undefined)
const missingProviderMessage =
  'useComparisonState must be used within a ComparisonStateProvider.'

export function ComparisonStateProvider({
  children,
  createClient = createHostBridgeClient,
}: ComparisonStateProviderProps) {
  const [state, dispatch] = useReducer(
    comparisonStateReducer,
    initialComparisonState,
  )
  const sessionRef = useRef<ComparisonBridgeSession | null>(null)

  useEffect(() => {
    const session = startComparisonBridgeSession(createClient, dispatch)
    sessionRef.current = session
    return () => {
      if (sessionRef.current === session) {
        sessionRef.current = null
      }
      session.dispose()
    }
  }, [createClient])

  const changeInput = useCallback(
    (field: ComparisonInputField, value: string) => {
      dispatch(comparisonInputChanged(field, value))
    },
    [],
  )

  const compareVersions = useCallback(() => {
    let requestId: string
    try {
      requestId = createRequestId()
    } catch {
      dispatch(comparisonBridgeUnavailable())
      return
    }

    let request
    try {
      request = createComparisonReadRequest(
        requestId,
        state.leftVersionIdInput,
        state.leftVersionDigestInput,
        state.rightVersionIdInput,
        state.rightVersionDigestInput,
      )
    } catch {
      dispatch(comparisonLocalInvalidRequest())
      return
    }

    const session = sessionRef.current
    if (session === null) {
      dispatch(comparisonBridgeUnavailable())
      return
    }
    session.send(request)
  }, [
    state.leftVersionDigestInput,
    state.leftVersionIdInput,
    state.rightVersionDigestInput,
    state.rightVersionIdInput,
  ])

  const clearError = useCallback(() => {
    dispatch(comparisonErrorCleared())
  }, [])

  const value = useMemo<ComparisonStateContextValue>(
    () => ({ state, changeInput, compareVersions, clearError }),
    [changeInput, clearError, compareVersions, state],
  )

  return (
    <ComparisonStateContext.Provider value={value}>
      {children}
    </ComparisonStateContext.Provider>
  )
}

export function useComparisonState(): ComparisonStateContextValue {
  const value = useContext(ComparisonStateContext)
  if (value === undefined) {
    throw new Error(missingProviderMessage)
  }
  return value
}

function createRequestId(): string {
  return window.crypto.randomUUID().toLowerCase()
}