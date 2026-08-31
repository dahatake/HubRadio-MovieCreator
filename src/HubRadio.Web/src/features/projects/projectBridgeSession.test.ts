import { describe, expect, it } from 'vitest'

import {
  PROJECT_LIST_METHOD,
  createProjectPickSourceRequest,
  type ProjectBridgeRequest,
  type ProjectBridgeResponse,
} from '../../bridge'
import {
  projectBridgeUnavailable,
  projectRequestStarted,
  projectResponseReceived,
  type ProjectAction,
} from './projectState'
import {
  startProjectBridgeSession,
  type ProjectBridgeClient,
} from './projectBridgeSession'

const requestId = '00112233-4455-6677-8899-aabbccddeeff'
const nextRequestId = '10112233-4455-6677-8899-aabbccddeeff'

describe('project bridge session', () => {
  it('subscribes, requests the list on start, forwards responses, and cleans up once', () => {
    const client = new FakeClient()
    const actions: ProjectAction[] = []
    const session = startProjectBridgeSession(
      () => client,
      () => requestId,
      (action) => actions.push(action),
    )

    expect(client.sent).toHaveLength(1)
    expect(client.sent[0]).toMatchObject({
      requestId,
      method: PROJECT_LIST_METHOD,
      params: {},
    })
    expect(actions).toEqual([
      projectRequestStarted(requestId, PROJECT_LIST_METHOD),
    ])

    const response = listResponse()
    client.emit(response)
    expect(actions.at(-1)).toEqual(projectResponseReceived(response))

    session.dispose()
    session.dispose()
    expect(client.unsubscribeCount).toBe(1)
    expect(client.disposeCount).toBe(1)

    session.send(createProjectPickSourceRequest(nextRequestId))
    expect(client.sent).toHaveLength(1)
    expect(actions.at(-1)).toEqual(projectBridgeUnavailable())
  })

  it('maps create and subscription failures to a private-safe unavailable action', () => {
    const createActions: ProjectAction[] = []
    const unavailable = startProjectBridgeSession(
      () => {
        throw new Error('C:\\private\\database.sqlite')
      },
      () => requestId,
      (action) => createActions.push(action),
    )
    expect(createActions).toEqual([projectBridgeUnavailable()])

    unavailable.send(createProjectPickSourceRequest(nextRequestId))
    expect(createActions.slice(-2)).toEqual([
      projectRequestStarted(nextRequestId, 'project.pickSource'),
      projectBridgeUnavailable(),
    ])
    expect(JSON.stringify(createActions)).not.toContain('C:\\private')

    const client = new FakeClient()
    client.throwOnSubscribe = true
    const subscribeActions: ProjectAction[] = []
    startProjectBridgeSession(
      () => client,
      () => requestId,
      (action) => subscribeActions.push(action),
    )
    expect(client.disposeCount).toBe(1)
    expect(subscribeActions).toEqual([projectBridgeUnavailable()])
  })

  it('clears pending state through unavailable when transport send fails', () => {
    const client = new FakeClient()
    const actions: ProjectAction[] = []
    const session = startProjectBridgeSession(
      () => client,
      () => requestId,
      (action) => actions.push(action),
    )
    actions.length = 0
    client.throwOnSend = true

    session.send(createProjectPickSourceRequest(nextRequestId))

    expect(actions).toEqual([
      projectRequestStarted(nextRequestId, 'project.pickSource'),
      projectBridgeUnavailable(),
    ])
    expect(JSON.stringify(actions)).not.toContain('native stack')
  })
})

class FakeClient implements ProjectBridgeClient {
  readonly sent: ProjectBridgeRequest[] = []
  throwOnSend = false
  throwOnSubscribe = false
  unsubscribeCount = 0
  disposeCount = 0
  private projectListener: ((response: ProjectBridgeResponse) => void) | null = null

  sendPing(): void {
    throw new Error('Not used by project tests.')
  }

  onPingResponse(): () => void {
    throw new Error('Not used by project tests.')
  }

  sendJobStatus(): void {
    throw new Error('Not used by project tests.')
  }

  sendJobCancellation(): void {
    throw new Error('Not used by project tests.')
  }

  sendJobRetry(): void {
    throw new Error('Not used by project tests.')
  }

  abandonJobRequest(): void {
    throw new Error('Not used by project tests.')
  }

  onJobResponse(): () => void {
    throw new Error('Not used by project tests.')
  }

  sendProject(request: ProjectBridgeRequest): void {
    if (this.throwOnSend) {
      throw new Error('native stack C:\\private\\source.mov')
    }

    this.sent.push(request)
  }

  onProjectResponse(
    listener: (response: ProjectBridgeResponse) => void,
  ): () => void {
    if (this.throwOnSubscribe) {
      throw new Error('native stack')
    }

    this.projectListener = listener
    return () => {
      this.unsubscribeCount++
      this.projectListener = null
    }
  }

  dispose(): void {
    this.disposeCount++
    this.projectListener = null
  }

  emit(response: ProjectBridgeResponse): void {
    this.projectListener?.(response)
  }
}

function listResponse(): ProjectBridgeResponse {
  return {
    version: 1,
    requestId,
    method: PROJECT_LIST_METHOD,
    result: {
      projects: [],
      catalog: { outputProfiles: [], brandTemplates: [] },
      canCreate: true,
    },
  }
}