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

import {
  createHostBridgeClient,
  createProjectCreateDraftRequest,
  createProjectDeleteRequest,
  createProjectFinalizeDraftRequest,
  createProjectListRequest,
  createProjectLoadDraftRequest,
  createProjectPickSourceRequest,
  createProjectSaveDraftRequest,
  type BridgeClient,
  type ProjectBridgeRequest,
  type ProjectDraftInput,
} from '../../bridge'
import {
  startProjectBridgeSession,
  type ProjectBridgeSession,
} from './projectBridgeSession'
import {
  initialProjectState,
  isProjectInputFinalizable,
  isProjectInputSaveable,
  projectBridgeUnavailable,
  projectDraftClosed,
  projectErrorCleared,
  projectInputChanged,
  projectStateReducer,
  type ProjectState,
} from './projectState'

export interface ProjectStateContextValue {
  readonly state: ProjectState
  readonly refresh: () => void
  readonly createDraft: () => void
  readonly loadDraft: (draftId: string) => void
  readonly deleteProject: (draftId: string) => void
  readonly updateInput: (input: ProjectDraftInput) => void
  readonly saveDraft: () => void
  readonly pickSource: () => void
  readonly finalizeDraft: () => void
  readonly closeDraft: () => void
  readonly clearError: () => void
}

export interface ProjectStateProviderProps {
  readonly children: ReactNode
  readonly createClient?: () => BridgeClient
}

const ProjectStateContext = createContext<ProjectStateContextValue | undefined>(undefined)
const missingProviderMessage =
  'useProjectState must be used within a ProjectStateProvider.'

export function ProjectStateProvider({
  children,
  createClient = createHostBridgeClient,
}: ProjectStateProviderProps) {
  const [state, dispatch] = useReducer(projectStateReducer, initialProjectState)
  const sessionRef = useRef<ProjectBridgeSession | null>(null)

  useEffect(() => {
    const session = startProjectBridgeSession(createClient, createRequestId, dispatch)
    sessionRef.current = session

    return () => {
      if (sessionRef.current === session) {
        sessionRef.current = null
      }

      session.dispose()
    }
  }, [createClient])

  const send = useCallback((request: ProjectBridgeRequest) => {
    const session = sessionRef.current
    if (session === null) {
      dispatch(projectBridgeUnavailable())
      return
    }

    session.send(request)
  }, [])

  const refresh = useCallback(() => {
    send(createProjectListRequest(createRequestId()))
  }, [send])

  const createDraft = useCallback(() => {
    send(createProjectCreateDraftRequest(createRequestId()))
  }, [send])

  const loadDraft = useCallback(
    (draftId: string) => {
      send(createProjectLoadDraftRequest(createRequestId(), draftId))
    },
    [send],
  )

  const deleteProject = useCallback(
    (draftId: string) => {
      send(createProjectDeleteRequest(createRequestId(), draftId))
    },
    [send],
  )

  const updateInput = useCallback((input: ProjectDraftInput) => {
    dispatch(projectInputChanged(input))
  }, [])

  const saveDraft = useCallback(() => {
    if (
      state.draft === null ||
      state.draft.isFinalized ||
      !isProjectInputSaveable(state.input)
    ) {
      return
    }

    send(
      createProjectSaveDraftRequest(
        createRequestId(),
        state.draft.draftId,
        state.draft.revision,
        state.input,
      ),
    )
  }, [send, state.draft, state.input])

  const pickSource = useCallback(() => {
    send(createProjectPickSourceRequest(createRequestId()))
  }, [send])

  const finalizeDraft = useCallback(() => {
    if (
      state.draft === null ||
      state.draft.isFinalized ||
      !isProjectInputFinalizable(state.input, state.draft)
    ) {
      return
    }

    send(
      createProjectFinalizeDraftRequest(
        createRequestId(),
        state.draft.draftId,
        state.draft.revision,
        state.input,
      ),
    )
  }, [send, state.draft, state.input])

  const closeDraft = useCallback(() => {
    dispatch(projectDraftClosed())
  }, [])

  const clearError = useCallback(() => {
    dispatch(projectErrorCleared())
  }, [])

  const value = useMemo<ProjectStateContextValue>(
    () => ({
      state,
      refresh,
      createDraft,
      loadDraft,
      deleteProject,
      updateInput,
      saveDraft,
      pickSource,
      finalizeDraft,
      closeDraft,
      clearError,
    }),
    [
      clearError,
      closeDraft,
      createDraft,
      deleteProject,
      finalizeDraft,
      loadDraft,
      pickSource,
      refresh,
      saveDraft,
      state,
      updateInput,
    ],
  )

  return (
    <ProjectStateContext.Provider value={value}>
      {children}
    </ProjectStateContext.Provider>
  )
}

export function useProjectState(): ProjectStateContextValue {
  const value = useContext(ProjectStateContext)
  if (value === undefined) {
    throw new Error(missingProviderMessage)
  }

  return value
}

function createRequestId(): string {
  return window.crypto.randomUUID().toLowerCase()
}