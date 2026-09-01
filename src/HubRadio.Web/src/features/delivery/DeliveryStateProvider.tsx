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
  createDeliveryReadRequest,
  type DeliveryRationalTime,
} from './deliveryProtocol'
import {
  startDeliveryBridgeSession,
  type DeliveryBridgeClient,
  type DeliveryBridgeSession,
} from './deliveryBridgeSession'
import {
  deliveryBridgeUnavailable,
  deliveryErrorCleared,
  deliveryLocalInvalidRequest,
  deliveryStateReducer,
  deliveryTraceRateChanged,
  deliveryTraceValueChanged,
  deliveryVersionIdChanged,
  initialDeliveryState,
  type DeliveryUiState,
} from './deliveryState'

export interface DeliveryStateContextValue {
  readonly state: DeliveryUiState
  readonly changeVersionId: (value: string) => void
  readonly changeTraceValue: (value: string) => void
  readonly changeTraceRate: (value: string) => void
  readonly loadSnapshot: () => void
  readonly refreshSnapshot: () => void
  readonly lookupTrace: () => void
  readonly clearError: () => void
}

export interface DeliveryStateProviderProps {
  readonly children: ReactNode
  readonly createClient?: () => DeliveryBridgeClient
}

const DeliveryStateContext = createContext<
  DeliveryStateContextValue | undefined
>(undefined)
const missingProviderMessage =
  'useDeliveryState must be used within a DeliveryStateProvider.'

export function DeliveryStateProvider({
  children,
  createClient = createHostBridgeClient,
}: DeliveryStateProviderProps) {
  const [state, dispatch] = useReducer(
    deliveryStateReducer,
    initialDeliveryState,
  )
  const sessionRef = useRef<DeliveryBridgeSession | null>(null)

  useEffect(() => {
    const session = startDeliveryBridgeSession(createClient, dispatch)
    sessionRef.current = session
    return () => {
      if (sessionRef.current === session) {
        sessionRef.current = null
      }
      session.dispose()
    }
  }, [createClient])

  const send = useCallback((outputTime: DeliveryRationalTime | null) => {
    let request
    try {
      request = createDeliveryReadRequest(
        createRequestId(),
        state.versionIdInput,
        outputTime,
      )
    } catch {
      dispatch(deliveryLocalInvalidRequest())
      return
    }

    const session = sessionRef.current
    if (session === null) {
      dispatch(deliveryBridgeUnavailable())
      return
    }
    session.send(request)
  }, [state.versionIdInput])

  const changeVersionId = useCallback((value: string) => {
    dispatch(deliveryVersionIdChanged(value))
  }, [])

  const changeTraceValue = useCallback((value: string) => {
    dispatch(deliveryTraceValueChanged(value))
  }, [])

  const changeTraceRate = useCallback((value: string) => {
    dispatch(deliveryTraceRateChanged(value))
  }, [])

  const loadSnapshot = useCallback(() => send(null), [send])
  const refreshSnapshot = useCallback(() => send(null), [send])
  const lookupTrace = useCallback(() => {
    send({
      value: state.traceValueInput,
      rate: state.traceRateInput,
    })
  }, [send, state.traceRateInput, state.traceValueInput])

  const clearError = useCallback(() => {
    dispatch(deliveryErrorCleared())
  }, [])

  const value = useMemo<DeliveryStateContextValue>(
    () => ({
      state,
      changeVersionId,
      changeTraceValue,
      changeTraceRate,
      loadSnapshot,
      refreshSnapshot,
      lookupTrace,
      clearError,
    }),
    [
      changeTraceRate,
      changeTraceValue,
      changeVersionId,
      clearError,
      loadSnapshot,
      lookupTrace,
      refreshSnapshot,
      state,
    ],
  )

  return (
    <DeliveryStateContext.Provider value={value}>
      {children}
    </DeliveryStateContext.Provider>
  )
}

export function useDeliveryState(): DeliveryStateContextValue {
  const value = useContext(DeliveryStateContext)
  if (value === undefined) {
    throw new Error(missingProviderMessage)
  }
  return value
}

function createRequestId(): string {
  return window.crypto.randomUUID().toLowerCase()
}
