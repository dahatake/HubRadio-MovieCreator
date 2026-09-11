import { afterEach, describe, expect, it, vi } from 'vitest'
import { createPipelineSession, type PipelineBridgeClient } from './pipelineSession'
import { sourceOnlyMediaUrl, type PipelineRequest, type PipelineResponse } from './pipelineProtocol'
import { createCompletedPipeline, createPipelineSnapshot, fixtureId, pipelineProjectId, pipelineResponse } from './pipelineTestFixture'

afterEach(() => vi.useRealTimers())

function harness() {
  const requests: PipelineRequest[] = []
  const abandoned: string[] = []
  let listener: ((value: PipelineResponse) => void) | null = null
  let id = 1000
  const client: PipelineBridgeClient = { sendPipeline: request => { requests.push(request) },
    onPipelineResponse: callback => { listener = callback; return () => { listener = null } },
    abandonPipelineRequest: requestId => { abandoned.push(requestId) }, dispose: vi.fn() }
  const changed = vi.fn()
  const session = createPipelineSession(() => client, changed, { createRequestId: () => fixtureId(++id) })
  return { requests, abandoned, changed, client, session,
    async select(projectId: string | null = pipelineProjectId) { session.selectProject(projectId); await Promise.resolve() },
    reply(snapshot: Parameters<typeof pipelineResponse>[0], request = requests.at(-1)!) {
      listener?.(pipelineResponse(snapshot, request.requestId, request.method))
    },
    raw(value: unknown) { listener?.(value as PipelineResponse) },
  }
}

describe('pipeline session (no native execution)', () => {
  it('reads once per selected project, ignores inline repeats and stops polling terminal/notFound states', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const h = harness()
    await h.select()
    expect(h.requests.map(r => r.method)).toEqual(['pipeline.read'])
    await h.select()
    const s = createPipelineSnapshot()
    h.reply(s)
    await vi.advanceTimersByTimeAsync(999)
    expect(h.requests).toHaveLength(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(h.requests).toHaveLength(2)
    await vi.advanceTimersByTimeAsync(2000)
    expect(h.requests).toHaveLength(2) // no overlap while awaiting read
    h.reply({ ...s, revision: 1, phase: 'Canceled' })
    await vi.advanceTimersByTimeAsync(60_000)
    expect(h.requests).toHaveLength(2)
    await h.select(fixtureId(99)); h.reply(null)
    await vi.advanceTimersByTimeAsync(60_000)
    expect(h.requests).toHaveLength(3)
    h.session.dispose()
  })

  it('abandons old project reads and rejects old/future request IDs, methods and different project snapshots', async () => {
    const h = harness()
    await h.select()
    const old = h.requests[0]!
    await h.select(fixtureId(99))
    expect(h.abandoned).toContain(old.requestId)
    h.reply(createPipelineSnapshot(), old)
    expect(h.session.getState().snapshot).toBeNull()
    const current = h.requests[1]!
    h.raw(pipelineResponse(createPipelineSnapshot('Queued', fixtureId(99)), fixtureId(555)))
    h.raw(pipelineResponse(createPipelineSnapshot('Queued', fixtureId(99)), current.requestId, 'pipeline.cancel'))
    h.reply(createPipelineSnapshot())
    expect(h.session.getState().snapshot).toBeNull()
    h.reply(createPipelineSnapshot('Queued', fixtureId(99)))
    expect(h.session.getState().snapshot?.projectId).toBe(fixtureId(99))
    h.session.dispose()
  })

  it('rejects a future execution and same-revision rebinding without erasing the previous display', async () => {
    const h = harness()
    await h.select(); const completed = createCompletedPipeline(); h.reply(completed)
    const previous = h.session.getState().snapshot
    h.session.read()
    h.reply({ ...createPipelineSnapshot(), executionId: fixtureId(88), rootJobId: fixtureId(89) })
    expect(h.session.getState()).toMatchObject({ snapshot: previous, error: 'invalidResponse', needsRefresh: true })
    expect(h.session.retry()).toBe(false)
    h.session.read(); h.reply(completed)
    expect(h.session.getState().snapshot).toEqual(previous)
    expect(h.session.getState().snapshot).not.toBe(previous) // Freshly validated media, not the revoked cache.
    h.session.dispose()
  })

  it('uses execution revision for cancellation and a fresh root/execution for explicit retry', async () => {
    const h = harness(); await h.select()
    const s = createPipelineSnapshot('Analyzing'); s.revision = 3; h.reply(s)
    expect(h.session.cancel()).toBe(true)
    expect(h.session.cancel()).toBe(false)
    expect(h.requests.at(-1)).toMatchObject({ method: 'pipeline.cancel', params: { projectId: s.projectId, executionId: s.executionId, expectedRevision: 3 } })
    h.reply({ ...s, phase: 'Canceled', revision: 4 })
    expect(h.session.retry()).toBe(true)
    expect(h.requests.at(-1)).toMatchObject({ method: 'pipeline.retry', params: { projectId: s.projectId, executionId: s.executionId, expectedRevision: 4 } })
    h.reply({ ...s, executionId: fixtureId(88), rootJobId: fixtureId(89), revision: 0, phase: 'Queued' })
    expect(h.session.getState().snapshot?.rootJobId).toBe(fixtureId(89))
    expect(h.requests.some(r => r.method.startsWith('jobs.'))).toBe(false)
    h.session.dispose()
  })

  it('rejects retry acknowledgements that reuse the old root', async () => {
    const h = harness(); await h.select(); const s = createPipelineSnapshot('Failed'); h.reply(s)
    h.session.retry()
    h.reply({ ...s, executionId: fixtureId(88), phase: 'Queued', failureCode: null })
    expect(h.session.getState().error).toBe('invalidResponse')
    expect(h.session.getState().snapshot?.executionId).toBe(s.executionId)
    h.session.dispose()
  })

  it('retains native-validated previous results throughout regeneration and sends exact saved-setting OCC once', async () => {
    const h = harness(); await h.select(); const completed = createCompletedPipeline(); h.reply(completed)
    const params = { projectId: completed.projectId, expectedVersionId: completed.versionId!, expectedVersionDigest: completed.versionDigest!,
      expectedSettingsRevision: 7, expectedSettingsSnapshotDigest: 'e'.repeat(64) }
    expect(h.session.regenerate(params)).toBe(true)
    expect(h.session.regenerate(params)).toBe(false)
    expect(h.session.getState().snapshot?.result).toEqual(completed.result)
    expect(h.requests.at(-1)?.params).toEqual(params)
    const next = { ...createPipelineSnapshot(), executionId: fixtureId(88), rootJobId: fixtureId(89), settingsSnapshotDigest: params.expectedSettingsSnapshotDigest,
      parentVersionId: completed.versionId, parentDigest: completed.versionDigest, previousResult: completed.result }
    h.reply(next)
    expect(h.session.getState().snapshot?.previousResult).toEqual(completed.result)
    expect(h.session.regenerate(params)).toBe(false)
    h.session.read(); h.reply({ ...next, revision: 1, phase: 'Rendering' })
    expect(h.session.getState().snapshot?.previousResult).toEqual(completed.result)
    expect(h.requests.filter(r => r.method === 'pipeline.regenerate')).toHaveLength(1)
    h.session.dispose()
  })

  it('timeouts do not cancel work or accept a late response and disposal clears all scheduled reads', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const h = harness(); await h.select(); const s = createPipelineSnapshot(); const old = h.requests[0]!
    await vi.advanceTimersByTimeAsync(30_000)
    expect(h.session.getState().error).toBe('timeout')
    h.reply(s, old)
    expect(h.session.getState().snapshot).toBeNull()
    h.session.read(); h.reply(s)
    h.session.dispose()
    await vi.advanceTimersByTimeAsync(60_000)
    expect(h.requests.map(r => r.method)).toEqual(['pipeline.read', 'pipeline.read'])
    expect(h.client.dispose).toHaveBeenCalledOnce()
  })

  it('settles a missing-file diagnostic as invalid, revokes the old current video and recovers only from a valid read', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const h = harness(); await h.select()
    const s = createCompletedPipeline(); h.reply(s)
    const oldResult = h.session.getState().snapshot!.result!
    const artifactId = oldResult.artifacts[0]!.artifact.artifactId
    expect(sourceOnlyMediaUrl(oldResult, artifactId)).not.toBeNull()
    h.session.read(); h.reply({ ...s, resultStatus: 'invalid' })
    expect(h.session.getState()).toMatchObject({ status: 'invalid', pendingMethod: null, error: null, needsRefresh: true,
      snapshot: { resultStatus: 'invalid', result: s.result } })
    expect(sourceOnlyMediaUrl(oldResult, artifactId)).toBeNull()
    expect(sourceOnlyMediaUrl(h.session.getState().snapshot!.result!, artifactId)).toBeNull()
    const params = regenerationParams(s)
    expect(h.session.regenerate(params)).toBe(false)
    await vi.advanceTimersByTimeAsync(60_000)
    expect(h.session.getState().error).toBeNull()
    expect(h.requests).toHaveLength(2)
    h.session.read(); h.reply(s)
    expect(h.session.getState()).toMatchObject({ status: 'available', needsRefresh: false, error: null })
    expect(sourceOnlyMediaUrl(h.session.getState().snapshot!.result!, artifactId)).not.toBeNull()
    expect(sourceOnlyMediaUrl(oldResult, artifactId)).toBeNull()
    h.session.dispose()
  })

  it('revokes retained parent media on regeneration, invalid results and read failures until revalidated', async () => {
    const h = harness(); await h.select()
    const parent = createCompletedPipeline(); h.reply(parent)
    const old = h.session.getState().snapshot!.result!
    const artifactId = old.artifacts[0]!.artifact.artifactId
    expect(h.session.regenerate(regenerationParams(parent))).toBe(true)
    expect(h.session.getState().snapshot?.result).toBe(old) // Text may remain visible.
    expect(sourceOnlyMediaUrl(old, artifactId)).toBeNull()
    const next = { ...createPipelineSnapshot(), executionId: fixtureId(88), rootJobId: fixtureId(89),
      parentVersionId: parent.versionId, parentDigest: parent.versionDigest, previousResult: parent.result }
    h.reply(next)
    const verifiedParent = h.session.getState().snapshot!.previousResult!
    expect(sourceOnlyMediaUrl(verifiedParent, artifactId)).not.toBeNull()
    h.session.read(); h.reply({ ...next, resultStatus: 'invalid' })
    expect(h.session.getState().status).toBe('invalid')
    expect(sourceOnlyMediaUrl(verifiedParent, artifactId)).toBeNull()
    expect(sourceOnlyMediaUrl(h.session.getState().snapshot!.previousResult!, artifactId)).toBeNull()
    h.session.read(); h.reply(next)
    const revalidated = h.session.getState().snapshot!.previousResult!
    expect(sourceOnlyMediaUrl(revalidated, artifactId)).not.toBeNull()
    h.session.read()
    h.raw({ ...pipelineResponse(null, h.requests.at(-1)!.requestId), result: { status: 'unavailable', snapshot: null } })
    expect(h.session.getState().snapshot?.previousResult).toBe(revalidated)
    expect(sourceOnlyMediaUrl(revalidated, artifactId)).toBeNull()
    h.session.dispose()
  })

  it.each(['pipeline.regenerate', 'pipeline.retry', 'pipeline.cancel'] as const)(
    'unlocks an uncertain %s after an authoritative read of the same pre-mutation execution, never automatically resending', async method => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
      const h = harness(); await h.select()
      const s = method === 'pipeline.regenerate' ? createCompletedPipeline()
        : createPipelineSnapshot(method === 'pipeline.retry' ? 'Failed' : 'Analyzing')
      h.reply(s)
      const repeat = () => method === 'pipeline.regenerate' ? h.session.regenerate(regenerationParams(s))
        : method === 'pipeline.retry' ? h.session.retry() : h.session.cancel()
      expect(repeat()).toBe(true)
      const timedOut = h.requests.at(-1)!
      await vi.advanceTimersByTimeAsync(30_000)
      expect(h.session.getState()).toMatchObject({ error: 'timeout', needsRefresh: true, pendingMethod: null })
      expect(repeat()).toBe(false)
      expect(h.session.read()).toBe(true)
      h.reply(s)
      expect(h.session.getState()).toMatchObject({ status: 'available', error: null, needsRefresh: false })
      h.reply(s, timedOut)
      expect(h.requests.map(r => r.method)).toEqual(['pipeline.read', method, 'pipeline.read'])
      expect(repeat()).toBe(true)
      expect(h.requests.filter(r => r.method === method)).toHaveLength(2)
      h.session.dispose()
    })

  it('accepts a read of a new execution only against the saved regeneration parent/root/settings intent', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const h = harness(); await h.select()
    const parent = createCompletedPipeline(); h.reply(parent)
    const params = { ...regenerationParams(parent), expectedSettingsSnapshotDigest: 'e'.repeat(64) }
    h.session.regenerate(params)
    await vi.advanceTimersByTimeAsync(30_000)
    h.session.read()
    // Even losing the displayed snapshot cannot turn the next read into an unbound initial read.
    h.raw({ ...pipelineResponse(null, h.requests.at(-1)!.requestId), result: { status: 'invalid', snapshot: null } })
    h.session.read()
    const child = { ...createPipelineSnapshot(), executionId: fixtureId(88), rootJobId: fixtureId(89),
      settingsSnapshotDigest: params.expectedSettingsSnapshotDigest, parentVersionId: parent.versionId,
      parentDigest: parent.versionDigest, previousResult: parent.result }
    h.reply({ ...child, parentVersionId: fixtureId(77), previousResult: null })
    expect(h.session.getState().error).toBe('invalidResponse')
    h.session.read(); h.reply({ ...child, rootJobId: parent.rootJobId, previousResult: null })
    expect(h.session.getState().error).toBe('invalidResponse')
    h.session.read(); h.reply(child)
    expect(h.session.getState()).toMatchObject({ status: 'available', needsRefresh: false,
      snapshot: { executionId: child.executionId, rootJobId: child.rootJobId, parentVersionId: parent.versionId } })
    expect(h.requests.filter(r => r.method === 'pipeline.regenerate')).toHaveLength(1)
    h.session.dispose()
  })
})

function regenerationParams(s: ReturnType<typeof createPipelineSnapshot>) {
  if (s.versionId === null || s.versionDigest === null) throw new Error('Expected a parent version fixture.')
  return { projectId: s.projectId, expectedVersionId: s.versionId, expectedVersionDigest: s.versionDigest,
    expectedSettingsRevision: 7, expectedSettingsSnapshotDigest: s.settingsSnapshotDigest }
}