import { afterEach, beforeEach, describe, expect, expectTypeOf, it, vi } from 'vitest'
import { createBridgeClient, type BridgeClient, type BridgeMessageEvent, type BridgeTransport } from '../../bridge'
import {
  SETTINGS_WORKSPACE_METHODS,
  createSettingsReadRequest, createSettingsUpdateRequest, createSettingsResetRequest, createSettingsResetAllRequest,
  createSettingsGlobalReadRequest, createSettingsGlobalUpdateRequest, createSettingsGlobalResetRequest, createSettingsGlobalResetAllRequest,
  createSettingsFileExportRequest, createSettingsFilePreviewRequest, createSettingsFileApplyRequest,
  parseSettingsWorkspaceResponse,
  type AnySettingsSnapshot, type GlobalSettingsSnapshot, type SettingsRequest, type SettingsResponse,
  type SettingsSnapshot, type SettingsWorkspaceRequest, type SettingsWorkspaceResponse,
} from './settingsProtocol'
import {
  startSettingsBridgeSession, startSettingsWorkspaceBridgeSession,
  type SettingsBridgeClient, type SettingsBridgeSession, type SettingsDispatch,
  type SettingsWorkspaceBridgeClient, type SettingsWorkspaceBridgeSession, type SettingsWorkspaceDispatch,
  type WorkspaceSessionEvent, type WorkspaceSessionRequest,
} from './settingsBridgeSession'

const id = '11112222-3333-4444-8555-666677778888'
const secondRequestId = '22222222-3333-4444-8555-666677778888'
const thirdRequestId = '33333333-4444-4555-8666-777788889999'
const projectId = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee'
const otherProjectId = 'bbbbbbbb-cccc-4ddd-8eee-ffffffffffff'
const key = 'subtitles.enabled'
const enabled = { kind: 'boolean', booleanValue: true } as const
const disabled = { kind: 'boolean', booleanValue: false } as const
const fileSha256 = 'd'.repeat(64)
const previousFileSha256 = 'e'.repeat(64)
const projectSnapshot: SettingsSnapshot = {
  projectId, profileId: otherProjectId, profileName: 'Session fixture', profileVersion: '1.0.0',
  profileDigest: 'f'.repeat(64), initialSnapshotDigest: 'a'.repeat(64), snapshotDigest: 'b'.repeat(64),
  revision: 3, updatedAt: '2026-09-07T00:00:00.0000000+00:00', definitionCount: 1,
  definitions: [{ key, group: 'subtitlesAndGraphics', isAdvanced: false, displayName: 'Subtitles', description: 'Enable subtitles.', unit: 'none', defaultValue: enabled, minimum: null, maximum: null, allowedTextValues: [], evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }] }],
  initialValues: [{ key, value: enabled }], values: [{ key, value: disabled }],
}
const globalSnapshot: GlobalSettingsSnapshot = { ...projectSnapshot, projectId: null, revision: 7 }
const scopes: readonly { scope: string; target: AnySettingsSnapshot }[] = [
  { scope: 'project', target: projectSnapshot }, { scope: 'global', target: globalSnapshot },
]
const projectRequests: readonly SettingsRequest[] = [
  createSettingsReadRequest(id, projectId), createSettingsUpdateRequest(id, projectSnapshot, key, enabled),
  createSettingsResetRequest(id, projectSnapshot, key), createSettingsResetAllRequest(id, projectSnapshot),
]
const workspaceCases = scopes.flatMap(({ scope, target }) => {
  const requests: readonly SettingsWorkspaceRequest[] = [
    ...(target.projectId === null ? [
      createSettingsGlobalReadRequest(id), createSettingsGlobalUpdateRequest(id, target, key, enabled),
      createSettingsGlobalResetRequest(id, target, key), createSettingsGlobalResetAllRequest(id, target),
    ] : projectRequests),
    createSettingsFileExportRequest(id, target, null), createSettingsFilePreviewRequest(id, target),
    createSettingsFileApplyRequest(id, target, fileSha256),
  ]
  return requests.map(request => ({ scope, target, request, method: request.method }))
})
const sessions = new Set<{ dispose(): void }>()

beforeEach(() => { vi.useFakeTimers() })
afterEach(() => {
  try {
    for (const session of sessions) session.dispose()
    expect(vi.getTimerCount()).toBe(0)
  } finally {
    sessions.clear()
    vi.useRealTimers()
  }
})

function trackSession<T extends { dispose(): void }>(session: T): T { sessions.add(session); return session }

describe('settingsBridgeSession', () => {
  it('correlates one pending request and abandons it on disposal', () => {
    let listener: ((response: SettingsResponse) => void) | null = null
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn(), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
      onSettingsResponse: vi.fn((next) => { listener = next; return vi.fn() }),
    }
    const dispatch = vi.fn()
    const session = trackSession(startSettingsBridgeSession(() => client, dispatch))
    const request = createSettingsReadRequest(id, 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee')
    expect(session.send(request)).toBe(true)
    expect(session.send({ ...request, requestId: '22222222-3333-4444-8555-666677778888' })).toBe(false)
    listener!({ version: 1, requestId: '33333333-4444-4555-8666-777788889999', method: 'settings.read', result: { status: 'notFound', snapshot: null } })
    expect(dispatch).toHaveBeenCalledTimes(1)
    session.dispose()
    expect(client.abandonSettingsRequest).toHaveBeenCalledWith(id)
    expect(client.dispose).toHaveBeenCalledOnce()
  })

  it('abandons a pending project request and ignores its late response', () => {
    let listener: ((response: SettingsResponse) => void) | null = null
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn(), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
      onSettingsResponse: vi.fn((next) => { listener = next; return vi.fn() }),
    }
    const dispatch = vi.fn()
    const session = trackSession(startSettingsBridgeSession(() => client, dispatch))
    const request = createSettingsReadRequest(id, 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee')
    expect(session.send(request)).toBe(true)

    session.abandonPending()
    listener!({ version: 1, requestId: request.requestId, method: request.method, result: { status: 'notFound', snapshot: null } })

    expect(client.abandonSettingsRequest).toHaveBeenCalledWith(request.requestId)
    expect(dispatch).toHaveBeenCalledTimes(1)
  })

  it('rejects an oversized request before it becomes pending', () => {
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn(), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
      onSettingsResponse: vi.fn(() => vi.fn()),
    }
    const dispatch = vi.fn()
    const session = trackSession(startSettingsBridgeSession(() => client, dispatch))
    const request = {
      version: 1,
      requestId: id,
      method: 'settings.update',
      params: {
        projectId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
        expectedRevision: 0,
        expectedProfileId: 'bbbbbbbb-cccc-4ddd-8eee-ffffffffffff',
        expectedProfileVersion: '1.0.0',
        expectedProfileDigest: 'a'.repeat(64),
        expectedSnapshotDigest: 'b'.repeat(64),
        key: 'assets.musicEligibleSections',
        value: {
          kind: 'textList',
          textListValue: Array.from({ length: 17 }, (_, index) => `${index}`.padEnd(4096, 'x')),
        },
      },
    } as const satisfies SettingsRequest

    expect(session.send(request)).toBe(false)
    expect(client.sendSettings).not.toHaveBeenCalled()
    expect(dispatch).toHaveBeenLastCalledWith({ type: 'requestRejected' })
  })

  it('abandons an unanswered request after the bounded timeout', () => {
    let listener: ((response: SettingsResponse) => void) | null = null
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn(), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
      onSettingsResponse: vi.fn((next) => { listener = next; return vi.fn() }),
    }
    const dispatch = vi.fn()
    const session = trackSession(startSettingsBridgeSession(() => client, dispatch))
    const request = createSettingsReadRequest(id, 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee')
    expect(session.send(request)).toBe(true)

    vi.advanceTimersByTime(5_000)
    listener!({ version: 1, requestId: request.requestId, method: request.method, result: { status: 'notFound', snapshot: null } })

    expect(client.abandonSettingsRequest).toHaveBeenCalledWith(request.requestId)
    expect(dispatch).toHaveBeenLastCalledWith({ type: 'bridgeUnavailable' })
    expect(dispatch).toHaveBeenCalledTimes(2)
    session.dispose()
  })

  it.each(projectRequests)('adapts $method to the existing provider actions using only the four legacy client members', request => {
    let listener: ((response: SettingsResponse) => void) | undefined
    const unsubscribe = vi.fn()
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn(), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
      onSettingsResponse: vi.fn(next => { listener = next; return unsubscribe }),
    }
    const dispatch = vi.fn<SettingsDispatch>()
    const session = trackSession(startSettingsBridgeSession(() => client, dispatch))
    const response: SettingsResponse = { version: 1, requestId: request.requestId, method: request.method, result: { status: 'available', snapshot: projectSnapshot } }

    expect(session.send(request)).toBe(true)
    listener!(response)
    listener!(response)

    expect(dispatch.mock.calls).toEqual([
      [{ type: 'requestStarted', method: request.method }], [{ type: 'responseReceived', response }],
    ])
    expect(client.sendSettings).toHaveBeenCalledExactlyOnceWith(request)
    expect(vi.getTimerCount()).toBe(0)
    session.dispose()
    session.dispose()
    expect(unsubscribe).toHaveBeenCalledOnce()
    expect(client.dispose).toHaveBeenCalledOnce()
    expect(client.abandonSettingsRequest).not.toHaveBeenCalled()
  })

  it('keeps fixed legacy unavailable actions on initialization and send failures', () => {
    const dispatch = vi.fn<SettingsDispatch>()
    const unavailable = trackSession(startSettingsBridgeSession(() => { throw new Error('private setup detail') }, dispatch))
    const request = createSettingsReadRequest(id, projectId)
    expect(unavailable.send(request)).toBe(false)
    expect(dispatch.mock.calls).toEqual([
      [{ type: 'bridgeUnavailable' }], [{ type: 'requestStarted', method: request.method }], [{ type: 'bridgeUnavailable' }],
    ])
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn(() => { throw new Error('private send detail') }),
      abandonSettingsRequest: vi.fn(), onSettingsResponse: vi.fn(() => vi.fn()), dispose: vi.fn(),
    }
    dispatch.mockClear()
    const session = trackSession(startSettingsBridgeSession(() => client, dispatch))
    expect(session.send(request)).toBe(false)
    expect(dispatch.mock.calls).toEqual([[{ type: 'requestStarted', method: request.method }], [{ type: 'bridgeUnavailable' }]])
    expect(client.abandonSettingsRequest).toHaveBeenCalledExactlyOnceWith(id)
    expect(vi.getTimerCount()).toBe(0)
  })
})

describe('settings workspace bridge session', () => {
  it('exports a concrete workspace API and typed events without widening the legacy API', () => {
    expect(new Set(workspaceCases.map(item => item.method))).toEqual(new Set(SETTINGS_WORKSPACE_METHODS))
    expect(new Set(workspaceCases.map(item => item.method)).size).toBe(11)
    expectTypeOf<SettingsBridgeClient>().toEqualTypeOf<Pick<BridgeClient, 'sendSettings' | 'abandonSettingsRequest' | 'onSettingsResponse' | 'dispose'>>()
    expectTypeOf<SettingsWorkspaceBridgeClient>().toEqualTypeOf<Pick<BridgeClient, 'sendSettingsWorkspace' | 'abandonSettingsWorkspaceRequest' | 'onSettingsWorkspaceResponse' | 'dispose'>>()
    expectTypeOf<SettingsBridgeSession['send']>().toEqualTypeOf<(request: SettingsRequest) => boolean>()
    expectTypeOf<SettingsWorkspaceBridgeSession['send']>().toEqualTypeOf<(request: SettingsWorkspaceRequest) => boolean>()
    expectTypeOf<SettingsWorkspaceBridgeSession['abandonPending']>().toEqualTypeOf<() => void>()
    expectTypeOf<SettingsWorkspaceBridgeSession['dispose']>().toEqualTypeOf<() => void>()
    expectTypeOf<typeof startSettingsWorkspaceBridgeSession>().toEqualTypeOf<(createClient: () => SettingsWorkspaceBridgeClient, dispatch: SettingsWorkspaceDispatch) => SettingsWorkspaceBridgeSession>()
    expectTypeOf<WorkspaceSessionEvent['type']>().toEqualTypeOf<'requested' | 'response' | 'failure'>()
    expectTypeOf<WorkspaceSessionRequest['projectId']>().toEqualTypeOf<string | null>()
    expectTypeOf<WorkspaceSessionRequest['method']>().toEqualTypeOf<SettingsWorkspaceRequest['method']>()
    expectTypeOf<Extract<WorkspaceSessionEvent, { type: 'response' }>['response']>().toEqualTypeOf<SettingsWorkspaceResponse>()
    expectTypeOf<Extract<WorkspaceSessionEvent, { type: 'failure' }>['code']>().toEqualTypeOf<'invalid' | 'unavailable' | 'timeout'>()
    expectTypeOf<Extract<WorkspaceSessionEvent, { type: 'failure' }>['request']>().toEqualTypeOf<WorkspaceSessionRequest | null>()
  })

  it.each(workspaceCases)('sends $method ($scope) once, correlates replies and keeps one pending timer', ({ request, target }) => {
    const { client, session, dispatch, emit } = workspaceSession()
    const context = { requestId: request.requestId, method: request.method, projectId: target.projectId }
    const response = available(request, target)
    const next = { ...request, requestId: secondRequestId }

    emit(response)
    expect(dispatch).not.toHaveBeenCalled()
    expect(session.send(request)).toBe(true)
    expect(client.sendSettingsWorkspace).toHaveBeenCalledExactlyOnceWith(request)
    expect(client.sendSettingsWorkspace.mock.calls[0]?.[0]).toBe(request)
    expect(dispatch).toHaveBeenCalledExactlyOnceWith({ type: 'requested', request: context })
    expect(session.send(next)).toBe(false)
    expect(session.send(createSettingsGlobalReadRequest(thirdRequestId))).toBe(false)
    emit({ ...response, requestId: secondRequestId })
    const wrongMethod = request.method === 'settings.global.read'
      ? createSettingsReadRequest(id, projectId) : createSettingsGlobalReadRequest(id)
    emit(available(wrongMethod, wrongMethod.method === 'settings.read' ? projectSnapshot : globalSnapshot))
    if (target.projectId !== null || request.method.startsWith('settings.file.')) {
      emit(available(request, { ...target, projectId: otherProjectId }))
    }
    expect(dispatch).toHaveBeenCalledOnce()
    expect(vi.getTimerCount()).toBe(1)

    emit(response)
    emit(response)
    expect(dispatch.mock.calls).toEqual([[{ type: 'requested', request: context }], [{ type: 'response', request: context, response }]])
    expect(vi.getTimerCount()).toBe(0)
    expect(client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
    expect(session.send(next)).toBe(true)
    emit(response)
    expect(dispatch).toHaveBeenCalledTimes(3)
    expect(vi.getTimerCount()).toBe(1)
    emit({ ...response, requestId: secondRequestId })
    expect(dispatch).toHaveBeenCalledTimes(4)
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(workspaceCases)('times out $method ($scope) at exactly 5000 ms without retry or late delivery', ({ request, target }) => {
    const { client, session, dispatch, emit } = workspaceSession()
    const context = { requestId: request.requestId, method: request.method, projectId: target.projectId }
    expect(session.send(request)).toBe(true)
    vi.advanceTimersByTime(4_999)
    expect(dispatch).toHaveBeenCalledOnce()
    expect(client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(dispatch).toHaveBeenLastCalledWith({ type: 'failure', request: context, code: 'timeout' })
    expect(client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(request.requestId)
    expect(vi.getTimerCount()).toBe(0)
    vi.advanceTimersByTime(20_000)
    emit(available(request, target))
    expect(dispatch).toHaveBeenCalledTimes(2)
    expect(client.sendSettingsWorkspace).toHaveBeenCalledOnce()

    const next = { ...request, requestId: secondRequestId }
    expect(session.send(next)).toBe(true)
    emit(available(request, target))
    expect(dispatch).toHaveBeenCalledTimes(3)
    expect(vi.getTimerCount()).toBe(1)
    emit(available(next, target))
    expect(dispatch).toHaveBeenCalledTimes(4)
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(workspaceCases)('forwards native failure statuses for $method ($scope) without losing hashes or resending', ({ request, target }) => {
    const { client, session, dispatch, emit } = workspaceSession()
    const file = request.method.startsWith('settings.file.')
    const statuses = ['forbidden', 'notFound', 'unbound', 'conflict', 'invalid', 'unavailable', ...(file ? ['fileNotFound', 'profileMismatch', 'snapshotMismatch', 'overwriteRequired'] : [])]
    for (const [index, status] of statuses.entries()) {
      const attempt = { ...request, requestId: `${String(index + 1).padStart(8, '0')}-3333-4444-8555-666677778888` }
      const response = requiredResponse({
        version: 1, requestId: attempt.requestId, method: attempt.method,
        result: file ? { status, snapshot: null, fileSha256: status === 'overwriteRequired' ? previousFileSha256 : null } : { status, snapshot: null },
      })
      expect(session.send(attempt)).toBe(true)
      emit({ ...response, requestId: thirdRequestId })
      expect(dispatch).toHaveBeenCalledTimes(index * 2 + 1)
      emit(response)
      emit(response)
      expect(dispatch).toHaveBeenCalledTimes((index + 1) * 2)
      expect(dispatch).toHaveBeenLastCalledWith({ type: 'response', request: { requestId: attempt.requestId, method: attempt.method, projectId: target.projectId }, response })
      expect(vi.getTimerCount()).toBe(0)
      vi.advanceTimersByTime(10_000)
      expect(client.sendSettingsWorkspace).toHaveBeenCalledTimes(index + 1)
    }
    expect(client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
    expect(dispatch.mock.calls.some(([event]) => event.type === 'failure')).toBe(false)
  })

  it.each(workspaceCases)('abandons $method ($scope) before changing scope and ignores responses during abandonment', ({ request, target }) => {
    const { client, session, dispatch, emit } = workspaceSession()
    const response = available(request, target)
    session.send(request)
    client.abandonSettingsWorkspaceRequest.mockImplementationOnce(() => { emit(response) })
    session.abandonPending()
    session.abandonPending()
    emit(response)
    expect(dispatch).toHaveBeenCalledOnce()
    expect(client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(request.requestId)
    expect(vi.getTimerCount()).toBe(0)

    const nextTarget = target.projectId === null ? projectSnapshot : globalSnapshot
    const next = createSettingsFilePreviewRequest(secondRequestId, nextTarget)
    expect(session.send(next)).toBe(true)
    emit(response)
    expect(dispatch).toHaveBeenCalledTimes(2)
    emit(available(next, nextTarget))
    expect(dispatch).toHaveBeenLastCalledWith({ type: 'response', request: { requestId: secondRequestId, method: next.method, projectId: nextTarget.projectId }, response: available(next, nextTarget) })
    vi.advanceTimersByTime(10_000)
    expect(dispatch).toHaveBeenCalledTimes(3)
    expect(vi.getTimerCount()).toBe(0)
  })

  it('distinguishes consecutive project scopes as well as global and file scopes', () => {
    const { session, dispatch, emit } = workspaceSession()
    const first = createSettingsFilePreviewRequest(id, projectSnapshot)
    const nextTarget: SettingsSnapshot = { ...projectSnapshot, projectId: otherProjectId }
    const next = createSettingsFilePreviewRequest(secondRequestId, nextTarget)
    session.send(first)
    session.abandonPending()
    session.send(next)
    emit(available(first, projectSnapshot))
    emit(available(next, projectSnapshot))
    expect(dispatch).toHaveBeenCalledTimes(2)
    expect(vi.getTimerCount()).toBe(1)
    emit(available(next, nextTarget))
    expect(dispatch).toHaveBeenCalledTimes(3)
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(workspaceCases)('cleans up $method ($scope) exactly once and ignores all calls after disposal', ({ request, target }) => {
    const { client, session, dispatch, emit, unsubscribe } = workspaceSession()
    session.send(request)
    session.dispose()
    session.dispose()
    session.abandonPending()
    emit(available(request, target))
    expect(session.send({ ...request, requestId: secondRequestId })).toBe(false)
    vi.advanceTimersByTime(10_000)
    expect(dispatch).toHaveBeenCalledOnce()
    expect(client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    expect(client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(request.requestId)
    expect(unsubscribe).toHaveBeenCalledOnce()
    expect(client.dispose).toHaveBeenCalledOnce()
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(workspaceCases)('clears a failed $method ($scope) send without retrying the mutation', ({ request, target }) => {
    const { client, session, dispatch, emit } = workspaceSession()
    client.sendSettingsWorkspace.mockImplementationOnce(() => { throw new Error('private transport detail') })
    client.abandonSettingsWorkspaceRequest.mockImplementationOnce(() => { throw new Error('private abandonment detail') })
    expect(session.send(request)).toBe(false)
    expect(dispatch.mock.calls).toEqual([
      [{ type: 'requested', request: { requestId: id, method: request.method, projectId: target.projectId } }],
      [{ type: 'failure', request: { requestId: id, method: request.method, projectId: target.projectId }, code: 'unavailable' }],
    ])
    expect(client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(id)
    emit(available(request, target))
    vi.advanceTimersByTime(20_000)
    expect(dispatch).toHaveBeenCalledTimes(2)
    expect(client.sendSettingsWorkspace).toHaveBeenCalledOnce()
    expect(vi.getTimerCount()).toBe(0)
    const next = createSettingsGlobalReadRequest(secondRequestId)
    expect(session.send(next)).toBe(true)
    emit(available(next, globalSnapshot))
    expect(dispatch).toHaveBeenCalledTimes(4)
    expect(vi.getTimerCount()).toBe(0)
  })

  it('rejects oversized global requests before transport/pending and permits a later request', () => {
    const { client, session, dispatch, emit } = workspaceSession()
    const original = createSettingsGlobalUpdateRequest(id, globalSnapshot, key, enabled)
    const request: SettingsWorkspaceRequest = { ...original, params: { ...original.params, value: { kind: 'textList', textListValue: Array.from({ length: 17 }, (_, index) => `${index}`.padEnd(4096, 'x')) } } }
    expect(session.send(request)).toBe(false)
    expect(client.sendSettingsWorkspace).not.toHaveBeenCalled()
    expect(dispatch).toHaveBeenCalledExactlyOnceWith({ type: 'failure', request: { requestId: id, method: original.method, projectId: null }, code: 'invalid' })
    expect(vi.getTimerCount()).toBe(0)
    const next = createSettingsGlobalReadRequest(secondRequestId)
    expect(session.send(next)).toBe(true)
    emit(available(next, globalSnapshot))
    expect(vi.getTimerCount()).toBe(0)
  })

  it('captures immutable requestId/method/scope instead of retaining mutable caller params', () => {
    const { client, session, dispatch, emit } = workspaceSession()
    const original = createSettingsFilePreviewRequest(id, globalSnapshot)
    const request = { ...original, params: { ...original.params } }
    session.send(request)
    request.requestId = secondRequestId
    // Simulate a caller changing the discriminator after handing off a valid request.
    Object.assign(request, { method: 'settings.file.apply' })
    request.params.projectId = projectId
    const context = { requestId: id, method: original.method, projectId: null }
    expect(dispatch).toHaveBeenCalledExactlyOnceWith({ type: 'requested', request: context })
    expect(Object.isFrozen(dispatch.mock.calls[0]?.[0].request)).toBe(true)
    emit(available(original, globalSnapshot))
    expect(dispatch).toHaveBeenLastCalledWith({ type: 'response', request: context, response: available(original, globalSnapshot) })
    expect(vi.getTimerCount()).toBe(0)

    const second = { ...original, requestId: thirdRequestId, params: { ...original.params } }
    session.send(second)
    second.requestId = secondRequestId
    second.params.projectId = otherProjectId
    vi.advanceTimersByTime(5_000)
    expect(client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(thirdRequestId)
    expect(dispatch).toHaveBeenLastCalledWith({ type: 'failure', request: { requestId: thirdRequestId, method: original.method, projectId: null }, code: 'timeout' })
  })

  it.each(['create', 'subscribe'] as const)('reports a fixed setup failure when %s throws and disposes any partial client once', stage => {
    const { client, emit } = workspaceClient()
    const dispatch = vi.fn<SettingsWorkspaceDispatch>()
    const subscribe = client.onSettingsWorkspaceResponse.getMockImplementation()!
    if (stage === 'subscribe') {
      client.onSettingsWorkspaceResponse.mockImplementation(listener => { subscribe(listener); throw new Error('private subscription detail') })
      client.dispose.mockImplementation(() => { throw new Error('private disposal detail') })
    }
    const createClient = vi.fn(() => { if (stage === 'create') throw new Error('private factory detail'); return client })
    const session = trackSession(startSettingsWorkspaceBridgeSession(createClient, dispatch))
    expect(dispatch).toHaveBeenCalledExactlyOnceWith({ type: 'failure', request: null, code: 'unavailable' })
    const request = createSettingsGlobalReadRequest(id)
    expect(session.send(request)).toBe(false)
    expect(dispatch.mock.calls).toEqual([
      [{ type: 'failure', request: null, code: 'unavailable' }],
      [{ type: 'requested', request: { requestId: id, method: request.method, projectId: null } }],
      [{ type: 'failure', request: { requestId: id, method: request.method, projectId: null }, code: 'unavailable' }],
    ])
    if (stage === 'subscribe') emit(available(request, globalSnapshot))
    session.dispose()
    session.dispose()
    session.abandonPending()
    expect(session.send(request)).toBe(false)
    expect(dispatch).toHaveBeenCalledTimes(3)
    expect(createClient).toHaveBeenCalledOnce()
    expect(client.dispose).toHaveBeenCalledTimes(stage === 'subscribe' ? 1 : 0)
    expect(client.sendSettingsWorkspace).not.toHaveBeenCalled()
    expect(vi.getTimerCount()).toBe(0)
  })

  it('continues all disposal steps even when abandonment, unsubscribe and client disposal throw', () => {
    const { client, session, dispatch, emit, unsubscribe } = workspaceSession()
    const request = createSettingsGlobalReadRequest(id)
    session.send(request)
    client.abandonSettingsWorkspaceRequest.mockImplementation(() => { emit(available(request, globalSnapshot)); throw new Error('private abandon detail') })
    unsubscribe.mockImplementation(() => { throw new Error('private unsubscribe detail') })
    client.dispose.mockImplementation(() => { throw new Error('private disposal detail') })
    expect(() => session.dispose()).not.toThrow()
    expect(() => session.dispose()).not.toThrow()
    emit(available(request, globalSnapshot))
    expect(dispatch).toHaveBeenCalledOnce()
    expect(client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(id)
    expect(unsubscribe).toHaveBeenCalledOnce()
    expect(client.dispose).toHaveBeenCalledOnce()
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(['abandon', 'timeout'] as const)('keeps local %s authoritative when the client cannot abandon', operation => {
    const { client, session, dispatch, emit } = workspaceSession()
    const request = createSettingsFileApplyRequest(id, globalSnapshot, fileSha256)
    session.send(request)
    client.abandonSettingsWorkspaceRequest.mockImplementationOnce(() => { emit(available(request, globalSnapshot)); throw new Error('private abandon detail') })
    if (operation === 'abandon') session.abandonPending()
    else vi.advanceTimersByTime(5_000)
    expect(dispatch).toHaveBeenCalledTimes(operation === 'abandon' ? 1 : 2)
    expect(vi.getTimerCount()).toBe(0)
    expect(session.send(createSettingsGlobalReadRequest(secondRequestId))).toBe(true)
    emit(available(request, globalSnapshot))
    expect(vi.getTimerCount()).toBe(1)
    session.abandonPending()
    expect(vi.getTimerCount()).toBe(0)
  })

  it('registers pending before a synchronous response and leaves no timeout afterward', () => {
    const { client, session, dispatch, emit } = workspaceSession()
    const request = createSettingsGlobalReadRequest(id)
    const response = available(request, globalSnapshot)
    client.sendSettingsWorkspace.mockImplementationOnce(() => { emit(response) })
    expect(session.send(request)).toBe(true)
    expect(dispatch.mock.calls.map(([event]) => event.type)).toEqual(['requested', 'response'])
    expect(vi.getTimerCount()).toBe(0)
    vi.advanceTimersByTime(10_000)
    expect(dispatch).toHaveBeenCalledTimes(2)
  })

  it.each([false, true])('preserves a reentrant next request after a synchronous response (outer send throws: %s)', throwsAfterResponse => {
    const { client, session, dispatch, emit } = workspaceSession()
    const first = createSettingsGlobalReadRequest(id)
    const next = createSettingsFilePreviewRequest(secondRequestId, projectSnapshot)
    dispatch.mockImplementation(event => {
      if (event.type === 'response' && event.request.requestId === id) {
        expect(vi.getTimerCount()).toBe(0)
        expect(session.send(next)).toBe(true)
      }
    })
    client.sendSettingsWorkspace.mockImplementationOnce(() => {
      emit(available(first, globalSnapshot))
      if (throwsAfterResponse) throw new Error('private send detail after response')
    })
    expect(session.send(first)).toBe(!throwsAfterResponse)
    expect(client.sendSettingsWorkspace.mock.calls).toEqual([[first], [next]])
    expect(dispatch.mock.calls.map(([event]) => event.type)).toEqual(['requested', 'response', 'requested'])
    expect(client.abandonSettingsWorkspaceRequest).not.toHaveBeenCalled()
    expect(vi.getTimerCount()).toBe(1)
    vi.advanceTimersByTime(4_999)
    expect(dispatch).toHaveBeenCalledTimes(3)
    vi.advanceTimersByTime(1)
    expect(dispatch).toHaveBeenLastCalledWith({ type: 'failure', request: { requestId: secondRequestId, method: next.method, projectId }, code: 'timeout' })
    expect(client.abandonSettingsWorkspaceRequest).toHaveBeenCalledExactlyOnceWith(secondRequestId)
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(['abandon', 'dispose'] as const)('does not send after a requested callback calls %s', operation => {
    const { client, session, dispatch } = workspaceSession()
    dispatch.mockImplementation(event => {
      if (event.type === 'requested') {
        if (operation === 'abandon') session.abandonPending()
        else session.dispose()
      }
    })
    expect(session.send(createSettingsGlobalReadRequest(id))).toBe(false)
    expect(client.sendSettingsWorkspace).not.toHaveBeenCalled()
    expect(dispatch).toHaveBeenCalledOnce()
    expect(vi.getTimerCount()).toBe(0)
  })

  it('releases pending if the requested callback throws before the transport call', () => {
    const { client, session, dispatch, emit } = workspaceSession()
    const error = new Error('consumer callback failed')
    dispatch.mockImplementationOnce(() => { throw error })
    expect(() => session.send(createSettingsGlobalReadRequest(id))).toThrow(error)
    expect(client.sendSettingsWorkspace).not.toHaveBeenCalled()
    expect(vi.getTimerCount()).toBe(0)
    const next = createSettingsGlobalReadRequest(secondRequestId)
    expect(session.send(next)).toBe(true)
    emit(available(next, globalSnapshot))
    expect(vi.getTimerCount()).toBe(0)
  })
})

describe('settings workspace session with the actual bridge client', () => {
  it.each(workspaceCases)('retains the actual client binding for $method ($scope) after mismatched replies', ({ request, target }) => {
    const { transport, emit } = workspaceTransport()
    const dispatch = vi.fn<SettingsWorkspaceDispatch>()
    const session = trackSession(startSettingsWorkspaceBridgeSession(() => createBridgeClient(transport), dispatch))
    const response = available(request, target)
    expect(session.send(request)).toBe(true)
    emit({ ...response, requestId: secondRequestId })
    emit({ ...response, method: request.method === 'settings.global.read' ? 'settings.read' : 'settings.global.read' })
    emit({ ...response, result: { ...response.result, snapshot: { ...target, projectId: target.projectId === null ? projectId : null } } })
    emit({ ...response, result: { ...response.result, snapshot: { ...target, projectId: otherProjectId } } })
    emit({ ...response, unexpected: true })
    expect(dispatch).toHaveBeenCalledOnce()
    expect(vi.getTimerCount()).toBe(1)
    emit(response)
    emit(response)
    expect(dispatch).toHaveBeenCalledTimes(2)
    expect(dispatch).toHaveBeenLastCalledWith({ type: 'response', request: { requestId: id, method: request.method, projectId: target.projectId }, response })
    expect(transport.postMessage).toHaveBeenCalledExactlyOnceWith(request)
    expect(vi.getTimerCount()).toBe(0)
    session.dispose()
    expect(transport.removeEventListener).toHaveBeenCalledOnce()
  })

  it.each(scopes)('preserves $scope preview/apply/export identities and file hashes without transforming responses', ({ target }) => {
    const { transport, emit } = workspaceTransport()
    const dispatch = vi.fn<SettingsWorkspaceDispatch>()
    const session = trackSession(startSettingsWorkspaceBridgeSession(() => createBridgeClient(transport), dispatch))
    const candidate = { ...target, snapshotDigest: 'c'.repeat(64), values: [{ key, value: enabled }] }
    const preview = createSettingsFilePreviewRequest(id, target)
    session.send(preview)
    emit(available(preview, { ...candidate, revision: target.revision + 1 }))
    emit(available(preview, { ...candidate, profileDigest: '9'.repeat(64) }))
    expect(dispatch).toHaveBeenCalledOnce()
    const previewResponse = available(preview, candidate)
    emit(previewResponse)
    expect(dispatch).toHaveBeenLastCalledWith({ type: 'response', request: { requestId: id, method: preview.method, projectId: target.projectId }, response: previewResponse })
    expect(vi.getTimerCount()).toBe(0)

    const apply = createSettingsFileApplyRequest(secondRequestId, target, fileSha256)
    const applied = { ...candidate, revision: target.revision + 1 }
    expect(apply.params.expectedSnapshotDigest).toBe(target.snapshotDigest)
    expect(apply.params.expectedSnapshotDigest).not.toBe(candidate.snapshotDigest)
    session.send(apply)
    emit(available(apply, applied, previousFileSha256))
    emit(available(apply, candidate))
    emit(available(apply, { ...applied, revision: target.revision + 2 }))
    expect(dispatch).toHaveBeenCalledTimes(3)
    const applyResponse = available(apply, applied)
    emit(applyResponse)
    expect(dispatch).toHaveBeenLastCalledWith({ type: 'response', request: { requestId: secondRequestId, method: apply.method, projectId: target.projectId }, response: applyResponse })
    expect(vi.getTimerCount()).toBe(0)

    const exportRequest = createSettingsFileExportRequest(thirdRequestId, applied, previousFileSha256)
    session.send(exportRequest)
    emit(available(exportRequest, { ...applied, revision: applied.revision + 1 }))
    emit(available(exportRequest, { ...applied, snapshotDigest: target.snapshotDigest }))
    expect(dispatch).toHaveBeenCalledTimes(5)
    const exportResponse = available(exportRequest, applied)
    emit(exportResponse)
    expect(dispatch).toHaveBeenLastCalledWith({ type: 'response', request: { requestId: thirdRequestId, method: exportRequest.method, projectId: target.projectId }, response: exportResponse })
    vi.advanceTimersByTime(10_000)
    expect(dispatch).toHaveBeenCalledTimes(6)
    expect(transport.postMessage.mock.calls).toEqual([[preview], [apply], [exportRequest]])
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(['abandon', 'timeout'] as const)('releases the real client pending slot after %s and rejects old global responses', operation => {
    const { transport, emit } = workspaceTransport()
    const dispatch = vi.fn<SettingsWorkspaceDispatch>()
    const session = trackSession(startSettingsWorkspaceBridgeSession(() => createBridgeClient(transport), dispatch))
    const first = createSettingsGlobalResetAllRequest(id, globalSnapshot)
    const next = createSettingsFilePreviewRequest(secondRequestId, projectSnapshot)
    session.send(first)
    if (operation === 'abandon') session.abandonPending()
    else vi.advanceTimersByTime(5_000)
    const count = dispatch.mock.calls.length
    expect(session.send(next)).toBe(true)
    emit(available(first, globalSnapshot))
    expect(dispatch).toHaveBeenCalledTimes(count + 1)
    emit(available(next, projectSnapshot))
    expect(dispatch).toHaveBeenCalledTimes(count + 2)
    expect(transport.postMessage.mock.calls).toEqual([[first], [next]])
    expect(vi.getTimerCount()).toBe(0)
  })
})

function workspaceClient() {
  let listener: ((response: SettingsWorkspaceResponse) => void) | undefined
  const unsubscribe = vi.fn<() => void>()
  const client = {
    sendSettingsWorkspace: vi.fn<SettingsWorkspaceBridgeClient['sendSettingsWorkspace']>(),
    abandonSettingsWorkspaceRequest: vi.fn<SettingsWorkspaceBridgeClient['abandonSettingsWorkspaceRequest']>(),
    onSettingsWorkspaceResponse: vi.fn<SettingsWorkspaceBridgeClient['onSettingsWorkspaceResponse']>(next => { listener = next; return unsubscribe }),
    dispose: vi.fn<() => void>(),
  } satisfies SettingsWorkspaceBridgeClient
  // Retain the callback deliberately, so tests can simulate already-queued late delivery.
  return { client, unsubscribe, emit(response: SettingsWorkspaceResponse): void { listener?.(response) } }
}

function workspaceSession() {
  const harness = workspaceClient()
  const dispatch = vi.fn<SettingsWorkspaceDispatch>()
  const session = trackSession(startSettingsWorkspaceBridgeSession(() => harness.client, dispatch))
  return { ...harness, dispatch, session }
}

function available(request: SettingsWorkspaceRequest, target: AnySettingsSnapshot, hash = fileSha256): SettingsWorkspaceResponse {
  return requiredResponse({
    version: 1, requestId: request.requestId, method: request.method,
    result: request.method.startsWith('settings.file.')
      ? { status: 'available', snapshot: target, fileSha256: hash }
      : { status: 'available', snapshot: target },
  })
}

function requiredResponse(value: unknown): SettingsWorkspaceResponse {
  const response = parseSettingsWorkspaceResponse(value)
  if (response === null) throw new Error('Invalid settings response fixture.')
  return response
}

function workspaceTransport() {
  const listeners = new Set<(event: BridgeMessageEvent) => void>()
  const transport = {
    postMessage: vi.fn<BridgeTransport['postMessage']>(),
    addEventListener: vi.fn<BridgeTransport['addEventListener']>((_type, listener) => { listeners.add(listener) }),
    removeEventListener: vi.fn<BridgeTransport['removeEventListener']>((_type, listener) => { listeners.delete(listener) }),
  } satisfies BridgeTransport
  return { transport, emit(data: unknown): void { for (const listener of listeners) listener({ data }) } }
}
