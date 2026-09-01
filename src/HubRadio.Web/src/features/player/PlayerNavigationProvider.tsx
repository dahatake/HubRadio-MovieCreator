import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  type ReactNode,
} from 'react'

import type { RationalTime } from './playerProtocol'

export type PlayerSourceSeekOutcome =
  | 'requested'
  | 'unavailable'
  | 'projectMismatch'
  | 'busy'
  | 'outOfRange'

export interface PlayerSourceSeekTarget {
  readonly projectId: string
  readonly sourceTime: RationalTime
}

export type PlayerSourceSeekHandler = (
  target: PlayerSourceSeekTarget,
) => PlayerSourceSeekOutcome

interface PlayerNavigationContextValue {
  readonly requestSourceSeek: PlayerSourceSeekHandler
  readonly registerSourceSeekHandler: (
    handler: PlayerSourceSeekHandler,
  ) => () => void
}

const unavailableContext: PlayerNavigationContextValue = Object.freeze({
  requestSourceSeek: () => 'unavailable',
  registerSourceSeekHandler: () => () => undefined,
})

const PlayerNavigationContext =
  createContext<PlayerNavigationContextValue>(unavailableContext)

export function PlayerNavigationProvider({
  children,
}: Readonly<{ children: ReactNode }>) {
  const handlerRef = useRef<PlayerSourceSeekHandler | null>(null)

  const registerSourceSeekHandler = useCallback(
    (handler: PlayerSourceSeekHandler): (() => void) => {
      handlerRef.current = handler
      return (): void => {
        if (handlerRef.current === handler) {
          handlerRef.current = null
        }
      }
    },
    [],
  )

  const requestSourceSeek = useCallback<PlayerSourceSeekHandler>((target) => {
    return handlerRef.current?.(target) ?? 'unavailable'
  }, [])

  const value = useMemo<PlayerNavigationContextValue>(
    () => ({ requestSourceSeek, registerSourceSeekHandler }),
    [registerSourceSeekHandler, requestSourceSeek],
  )

  return (
    <PlayerNavigationContext.Provider value={value}>
      {children}
    </PlayerNavigationContext.Provider>
  )
}

export function usePlayerSourceNavigation(): PlayerSourceSeekHandler {
  return useContext(PlayerNavigationContext).requestSourceSeek
}

export function usePlayerSourceSeekRegistration(
  handler: PlayerSourceSeekHandler,
): void {
  const register = useContext(PlayerNavigationContext).registerSourceSeekHandler
  useEffect(() => register(handler), [handler, register])
}
