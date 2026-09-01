import {
  createContext,
  useContext,
  useMemo,
  useReducer,
  type Dispatch,
  type ReactNode,
} from 'react'

import {
  initialPreviewModeState,
  previewModeReducer,
  type PreviewModeAction,
  type PreviewModeState,
} from './previewModeState'

export interface PreviewModeContextValue {
  readonly state: PreviewModeState
  readonly dispatch: Dispatch<PreviewModeAction>
}

const PreviewModeContext = createContext<PreviewModeContextValue | undefined>(
  undefined,
)
const missingProviderMessage =
  'usePreviewModes must be used within a PreviewModeProvider.'

export function PreviewModeProvider({
  children,
}: Readonly<{ children: ReactNode }>) {
  const [state, dispatch] = useReducer(
    previewModeReducer,
    initialPreviewModeState,
  )
  const value = useMemo<PreviewModeContextValue>(
    () => ({ state, dispatch }),
    [state],
  )

  return (
    <PreviewModeContext.Provider value={value}>
      {children}
    </PreviewModeContext.Provider>
  )
}

export function usePreviewModes(): PreviewModeContextValue {
  const value = useContext(PreviewModeContext)
  if (value === undefined) {
    throw new Error(missingProviderMessage)
  }
  return value
}