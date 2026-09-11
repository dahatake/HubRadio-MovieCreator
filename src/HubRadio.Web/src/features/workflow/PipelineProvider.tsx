import { createContext, useCallback, useContext, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { createHostBridgeClient } from '../../bridge'
import { createPipelineSession, initialPipelineState, type PipelineBridgeClient, type PipelineSession, type PipelineState } from './pipelineSession'
import type { PipelineRegenerationParams } from './pipelineProtocol'

interface PipelineContextValue {
  readonly state: PipelineState
  readonly refresh: () => boolean
  readonly cancel: () => boolean
  readonly retry: () => boolean
  readonly regenerate: (params: PipelineRegenerationParams) => boolean
}
const Context = createContext<PipelineContextValue | null>(null)

export function PipelineProvider({ children, projectId, createClient = createHostBridgeClient }: Readonly<{
  children: ReactNode
  projectId: string | null
  createClient?: () => PipelineBridgeClient
}>) {
  const [state, setState] = useState(initialPipelineState)
  const session = useRef<PipelineSession | null>(null)
  const factory = useRef(createClient)
  const target = useRef(projectId)
  factory.current = createClient
  target.current = projectId
  useLayoutEffect(() => {
    const owned = createPipelineSession(() => factory.current(), setState)
    session.current = owned
    owned.selectProject(target.current)
    return () => { if (session.current === owned) session.current = null; owned.dispose() }
  }, [])
  useLayoutEffect(() => { session.current?.selectProject(projectId) }, [projectId])
  const refresh = useCallback(() => session.current?.read() ?? false, [])
  const cancel = useCallback(() => session.current?.cancel() ?? false, [])
  const retry = useCallback(() => session.current?.retry() ?? false, [])
  const regenerate = useCallback((params: PipelineRegenerationParams) => session.current?.regenerate(params) ?? false, [])
  const value = useMemo<PipelineContextValue>(() => ({
    // Do not expose one render of the old project before the layout effect runs.
    state: state.projectId === projectId ? state : { ...initialPipelineState, projectId },
    refresh, cancel, retry, regenerate,
  }), [state, projectId, refresh, cancel, retry, regenerate])
  return <Context.Provider value={value}>{children}</Context.Provider>
}
export function usePipeline(): PipelineContextValue {
  const value = useContext(Context)
  if (value === null) throw new Error('usePipeline must be used within PipelineProvider.')
  return value
}