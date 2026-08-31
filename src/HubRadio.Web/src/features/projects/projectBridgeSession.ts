import {
  createProjectListRequest,
  type BridgeClient,
  type ProjectBridgeRequest,
} from '../../bridge'
import {
  projectBridgeUnavailable,
  projectRequestStarted,
  projectResponseReceived,
  type ProjectAction,
} from './projectState'

export interface ProjectBridgeSession {
  readonly send: (request: ProjectBridgeRequest) => void
  readonly dispose: () => void
}

export type ProjectDispatch = (action: ProjectAction) => void

export function startProjectBridgeSession(
  createClient: () => BridgeClient,
  createRequestId: () => string,
  dispatch: ProjectDispatch,
): ProjectBridgeSession {
  let client: BridgeClient
  try {
    client = createClient()
  } catch {
    dispatch(projectBridgeUnavailable())
    return unavailableSession(dispatch)
  }

  let unsubscribe: () => void
  try {
    unsubscribe = client.onProjectResponse((response) => {
      dispatch(projectResponseReceived(response))
    })
  } catch {
    client.dispose()
    dispatch(projectBridgeUnavailable())
    return unavailableSession(dispatch)
  }

  let disposed = false
  const session: ProjectBridgeSession = Object.freeze({
    send(request: ProjectBridgeRequest): void {
      if (disposed) {
        dispatch(projectBridgeUnavailable())
        return
      }

      dispatch(projectRequestStarted(request.requestId, request.method))
      try {
        client.sendProject(request)
      } catch {
        dispatch(projectBridgeUnavailable())
      }
    },
    dispose(): void {
      if (disposed) {
        return
      }

      disposed = true
      try {
        unsubscribe()
      } finally {
        client.dispose()
      }
    },
  })

  session.send(createProjectListRequest(createRequestId()))
  return session
}

function unavailableSession(dispatch: ProjectDispatch): ProjectBridgeSession {
  let disposed = false
  return Object.freeze({
    send(request: ProjectBridgeRequest): void {
      if (disposed) {
        return
      }

      dispatch(projectRequestStarted(request.requestId, request.method))
      dispatch(projectBridgeUnavailable())
    },
    dispose(): void {
      disposed = true
    },
  })
}