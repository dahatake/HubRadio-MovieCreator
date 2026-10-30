import { readFileSync } from 'node:fs'
import { describe, expect, it, vi } from 'vitest'
import { createBridgeClient, type BridgeMessageEvent, type BridgeTransport } from '../../bridge'
import { createPipelineExecutionRequest, createPipelineReadRequest, createPipelineRegenerateRequest, parsePipelineRequest, parsePipelineResponse,
  parsePipelineVersionsResponse, pipelineRegistrationRefreshKey, pipelineResponseMatchesRequest, sourceOnlyMediaUrl, verifiedPipelineResponse } from './pipelineProtocol'
import { createCompletedPipeline, createPipelineArtifact, createPipelineSnapshot, fixtureId, pipelineResponse,
  pipelineProjectId, pipelineVersionDigest, pipelineVersionId, setPipelineWarningTarget } from './pipelineTestFixture'

describe('pipeline native v2 protocol (synthetic fixtures only)', () => {
  it('mirrors every field in the actual native result projection rather than inventing a web DTO', () => {
    const native = readFileSync(new URL('../../../../HubRadio.App/Projects/SourceOnlyResultCodec.cs', import.meta.url), 'utf8')
    const value = createCompletedPipeline()
    const r = value.result!
    const a = r.artifacts[0]!
    const samples: Record<string, unknown> = {
      SourceOnlyResultDto: r, SourceOnlyResultBindingDto: r.binding, SourceOnlyResultArtifactDto: a,
      SourceOnlyResultReferenceDto: a.artifact, SourceOnlyResultEvidenceDto: a.evidence[0],
      SourceOnlyResultMeasurementsDto: a.measurements, SourceOnlyResultTargetDto: a.warnings[0]!.target,
      SourceOnlyResultWarningDto: a.warnings[0], SourceOnlyResultDeliveryDto: a.delivery,
    }
    for (const [name, sample] of Object.entries(samples)) {
      const signature = new RegExp(`record ${name}\\(([\\s\\S]*?)\\);`).exec(native)?.[1]
      expect(signature, name).toBeDefined()
      // Strip attributes, generic/array brackets and default values before reading each field name.
      const fields = signature!.split(',').map(parameter => parameter.replace(/\[[^\]]*\]/g, ' ').split('=')[0]!.trim().split(/\s+/).at(-1)!)
        .map(field => field[0]!.toLowerCase() + field.slice(1)).sort()
      expect(Object.keys(sample as object).sort(), name).toEqual(fields)
    }
    expect(parsePipelineResponse(pipelineResponse(value))).not.toBeNull()
  })

  it.each(['LandscapeClean', 'LandscapeBurnIn', 'VerticalBurnIn', 'SubRip', 'WebVtt', 'LandscapeThumbnail', 'VerticalThumbnail'] as const)(
    'accepts actual %s role/media/evaluation shapes and uses only the local bound URL', role => {
      const s = createCompletedPipeline()
      s.result!.artifacts = [createPipelineArtifact(s.result!, role, 50)]
      const response = verifiedPipelineResponse(pipelineResponse(s))
      expect(response).not.toBeNull()
      const result = response!.result.snapshot!.result!
      const a = result.artifacts[0]!
      const expected = a.mediaType === 'video/mp4' || a.mediaType === 'image/png'
        ? `https://player-media.appassets.example/source-only/${s.projectId}/${s.executionId}/${a.artifact.artifactId}/${a.artifact.contentDigest}` : null
      expect(sourceOnlyMediaUrl(result, a.artifact.artifactId)).toBe(expected)
      expect(sourceOnlyMediaUrl(s.result!, a.artifact.artifactId)).toBeNull()
      expect(sourceOnlyMediaUrl(result, fixtureId(999))).toBeNull()
    })

  it('retains the real generation warning vector without changing NOT EVALUATED or authorizing delivery', () => {
    const s = createCompletedPipeline()
    s.result!.generationWarnings = [
      { role: 'landscape-subtitles', code: 'opaque-setting-explicitly-overridden-by-transparent-background-request' },
      { role: 'landscape-subtitles', code: 'NOT EVALUATED' },
      { role: `vertical-subtitles-${fixtureId(91)}`, code: 'NOT EVALUATED' },
      { role: 'human-evaluation', code: 'NOT EVALUATED' },
    ]
    s.result!.artifacts[0]!.delivered = false
    s.result!.artifacts[0]!.delivery = null
    const parsed = parsePipelineResponse(pipelineResponse(s))
    expect(parsed).not.toBeNull()
    const result = parsed!.result.snapshot!.result!
    expect(result.generationWarnings).toEqual(s.result!.generationWarnings)
    expect(result.artifacts[0]!.evaluation).toEqual(s.result!.artifacts[0]!.evaluation)
    expect(result.artifacts[0]!.evaluation!.aggregateStatus).toBe('NOT EVALUATED')
    expect(result.artifacts[0]!.evaluation!.limitations.every(r => r.status === 'NOT EVALUATED')).toBe(true)
    expect(result.artifacts[0]!.delivered).toBe(false)
    expect(result.artifacts[0]!.delivery).toBeNull()
    // Failure diagnostics keep exactly the same warnings; their presence does not grant media access.
    s.phase = 'Failed'; s.resultStatus = 'invalid'; s.result!.generationDisposition = 'Failed'
    const diagnostic = parsePipelineResponse(pipelineResponse(s))!
    expect(diagnostic).not.toBeNull()
    expect(diagnostic.result.snapshot!.result!.generationWarnings).toEqual(s.result!.generationWarnings)
    expect(sourceOnlyMediaUrl(diagnostic.result.snapshot!.result!, s.result!.artifacts[0]!.artifact.artifactId)).toBeNull()
  })

  it('uses exactly the native closed warning vocabulary and accepts every catalog entry', () => {
    const native = readFileSync(new URL('../../../../HubRadio.App/Projects/SourceOnlyResultCodec.cs', import.meta.url), 'utf8')
    const web = readFileSync(new URL('./pipelineProtocol.ts', import.meta.url), 'utf8')
    const nativeList = /GenerationWarningCodes = Array\.AsReadOnly\(new\[\]\s*\{([\s\S]*?)\}\);/.exec(native)?.[1]
    const webList = /const generationWarningCodes: readonly string\[\] = \[([\s\S]*?)\]/.exec(web)?.[1]
    expect(nativeList).toBeDefined(); expect(webList).toBeDefined()
    const nativeCodes = Array.from(nativeList!.matchAll(/"([^"]+)"/g), m => m[1]!)
    const webCodes = Array.from(webList!.matchAll(/'([^']+)'/g), m => m[1]!)
    expect(nativeCodes.length).toBeGreaterThan(0)
    expect(new Set(nativeCodes).size).toBe(nativeCodes.length)
    expect(webCodes).toEqual(nativeCodes)
    for (const code of nativeCodes) {
      const s = createCompletedPipeline()
      s.result!.generationWarnings = [{ role: 'landscape-subtitles', code }]
      expect(parsePipelineResponse(pipelineResponse(s)), code).not.toBeNull()
    }
  })

  it.each(['plan', 'music', 'video', 'landscape-subtitles', 'human-evaluation', 'landscape-clean', 'landscape-burn-in',
    'landscape-srt', 'landscape-vtt', `vertical-subtitles-${fixtureId(91)}`, `vertical-burn-in-${fixtureId(91)}`,
    `thumbnail-landscape-${fixtureId(91)}`, `thumbnail-vertical-${fixtureId(91)}`])('accepts closed generation warning role %s', role => {
    const s = createCompletedPipeline()
    s.result!.generationWarnings = [{ role, code: 'NOT EVALUATED' }]
    expect(parsePipelineResponse(pipelineResponse(s))).not.toBeNull()
  })

  it.each([
    null, '', 'UnknownManualCode', 'private source text', 'C:\\private\\source.mov', '/private/source.mov',
    'file:///private/source.mov', 'missing-token-timing/private', 'NOT EVALUATED\n', 'NOT\tEVALUATED', 'NOT\0EVALUATED',
    'NOT\u00a0EVALUATED', 'NOT  EVALUATED', ' NOT EVALUATED', 'NOT EVALUATED ', 'not evaluated', 'NOT_EVALUATED',
    'PASS', 'Assigned', 'a'.repeat(128), 'a'.repeat(129),
  ])('rejects unknown, private or malformed warning code %j', code => {
    const s = createCompletedPipeline()
    Object.assign(s.result!, { generationWarnings: [{ role: 'landscape-subtitles', code }] })
    expect(parsePipelineResponse(pipelineResponse(s))).toBeNull()
  })

  it.each([
    null, '', 'manual', 'landscape-subtitles/private', 'human-evaluation-private', 'landscape-subtitles\n',
    'vertical-subtitles-', 'vertical-subtitles-../private', 'vertical-subtitles-AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA',
    `thumbnail-landscape-${fixtureId(91)}/private`, 'a'.repeat(129),
  ])('rejects unknown or unsafe warning role %j', role => {
    const s = createCompletedPipeline()
    Object.assign(s.result!, { generationWarnings: [{ role, code: 'NOT EVALUATED' }] })
    expect(parsePipelineResponse(pipelineResponse(s))).toBeNull()
  })

  it('keeps the generation warning count bounded and rejects extra fields', () => {
    const s = createCompletedPipeline()
    s.result!.generationWarnings = Array.from({ length: 256 }, () => ({ role: 'landscape-subtitles', code: 'NOT EVALUATED' }))
    expect(parsePipelineResponse(pipelineResponse(s))).not.toBeNull()
    s.result!.generationWarnings.push({ role: 'human-evaluation', code: 'NOT EVALUATED' })
    expect(parsePipelineResponse(pipelineResponse(s))).toBeNull()
    s.result!.generationWarnings = [{ role: 'landscape-subtitles', code: 'NOT EVALUATED' }]
    Object.assign(s.result!.generationWarnings[0]!, { sourcePath: 'C:\\private\\source.mov' })
    expect(parsePipelineResponse(pipelineResponse(s))).toBeNull()
  })

  it.each([
    ['snapshot extra', (s: ReturnType<typeof createCompletedPipeline>) => Object.assign(s, { sourcePath: 'C:\\private\\source.mov' })],
    ['result extra', (s: ReturnType<typeof createCompletedPipeline>) => Object.assign(s.result!, { nativeReconciliationAttempts: [] })],
    ['artifact extra', (s: ReturnType<typeof createCompletedPipeline>) => Object.assign(s.result!.artifacts[0]!, { mediaReference: 'https://example.invalid/movie.mp4' })],
    ['evaluation extra', (s: ReturnType<typeof createCompletedPipeline>) => Object.assign(s.result!.artifacts[0]!.evaluation!, { score: 100 })],
    ['wrong project', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.binding.projectId = fixtureId(90) }],
    ['wrong root', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.binding.rootJobId = fixtureId(90) }],
    ['wrong execution', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.binding.executionId = fixtureId(90) }],
    ['wrong settings', (s: ReturnType<typeof createCompletedPipeline>) => { s.settingsSnapshotDigest = '9'.repeat(64) }],
    ['wrong version', (s: ReturnType<typeof createCompletedPipeline>) => { s.versionId = fixtureId(90) }],
    ['wrong digest', (s: ReturnType<typeof createCompletedPipeline>) => { s.versionDigest = '9'.repeat(64) }],
    ['same root/ingest', (s: ReturnType<typeof createCompletedPipeline>) => { s.ingestJobId = s.rootJobId }],
    ['unsafe revision', (s: ReturnType<typeof createCompletedPipeline>) => { s.revision = Number.MAX_SAFE_INTEGER + 1 }],
    ['unsafe bytes', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.artifact.byteSize = Number.MAX_SAFE_INTEGER + 1 }],
    ['fractional time', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.measurements!.expectedDuration.value = 0.5 }],
    ['zero time rate', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.measurements!.expectedDuration.rate = 0 }],
    ['unreduced time', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.measurements!.expectedDuration = { value: 20, rate: 2 } }],
    ['NaN luma', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.measurements!.meanLuma = NaN }],
    ['unbounded luma', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.measurements!.maximumLuma = 256 }],
    ['unexpected rendition', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.renditionId = fixtureId(90) }],
    ['wrong warning digest', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.warnings[0]!.target.artifactDigest = '9'.repeat(64) }],
    ['invented setting', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.warnings[0]!.target.settingKey = 'invented.key' }],
    ['invented warning id', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.warnings[0]!.id = `${fixtureId(90)}:loudness` }],
    ['false aggregate', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.evaluation!.aggregateStatus = 'PASS' }],
    ['false counts', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.evaluation!.statusCounts.pass++ }],
    ['no human limitations', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.evaluation!.limitations = [] }],
    ['fake delivered', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.delivery = null }],
    ['delivery mismatch', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts[0]!.delivery!.artifact.contentDigest = '9'.repeat(64) }],
    ['duplicate artifact', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts.push(s.result!.artifacts[0]!) }],
    ['too many artifacts', (s: ReturnType<typeof createCompletedPipeline>) => { s.result!.artifacts = Array.from({ length: 17 }, () => s.result!.artifacts[0]!) }],
  ] as const)('fails closed: %s', (_name, change) => {
    const s = createCompletedPipeline()
    change(s)
    expect(parsePipelineResponse(pipelineResponse(s))).toBeNull()
  })

  it('rejects source/output time bounds and mismatched warning/evaluation target kinds', () => {
    const s = createCompletedPipeline()
    setPipelineWarningTarget(s.result!, { kind: 'Preview', timeBasis: 'Output', time: { value: 2, rate: 1 }, settingKey: null })
    expect(parsePipelineResponse(pipelineResponse(s))).not.toBeNull()
    for (const time of [{ value: -1, rate: 1 }, { value: 10, rate: 1 }, { value: Number.MAX_SAFE_INTEGER + 1, rate: 1 }]) {
      setPipelineWarningTarget(s.result!, { time })
      expect(parsePipelineResponse(pipelineResponse(s))).toBeNull()
    }
  })

  it('accepts previous results only from a different execution/root and exactly the same-project parent', () => {
    const s = createPipelineSnapshot()
    s.executionId = fixtureId(91); s.rootJobId = fixtureId(92)
    s.parentVersionId = pipelineVersionId; s.parentDigest = pipelineVersionDigest
    s.previousResult = createCompletedPipeline().result
    expect(parsePipelineResponse(pipelineResponse(s))).not.toBeNull()
    for (const change of [
      { parentVersionId: fixtureId(99) }, { parentDigest: '9'.repeat(64) }, { projectId: fixtureId(99) },
      { executionId: s.previousResult!.binding.executionId }, { rootJobId: s.previousResult!.binding.rootJobId },
    ]) expect(parsePipelineResponse(pipelineResponse({ ...s, ...change }))).toBeNull()
    expect(parsePipelineResponse(pipelineResponse({ ...s, resultStatus: 'invalid', result: s.previousResult }))).toBeNull()
  })

  // FR-136: the selected template ID rides on the Job-creation request; without one the request is unchanged.
  it('adds the selected production template to the regenerate request only when one is selected', () => {
    const base = { projectId: pipelineProjectId, expectedVersionId: pipelineVersionId, expectedVersionDigest: pipelineVersionDigest,
      expectedSettingsRevision: 3, expectedSettingsSnapshotDigest: 'e'.repeat(64) }
    const plain = createPipelineRegenerateRequest(fixtureId(1000), base)
    expect(Object.keys(plain.params)).not.toContain('styleTemplateId')
    const styled = createPipelineRegenerateRequest(fixtureId(1000), base, 'short-pop')
    expect(styled.params).toMatchObject({ ...base, styleTemplateId: 'short-pop' })
    expect(parsePipelineRequest(styled)).toEqual(styled)
    expect(parsePipelineRequest(plain)).toEqual(plain)
    expect(() => createPipelineRegenerateRequest(fixtureId(1000), base, 'Short Pop!')).toThrow()
    expect(parsePipelineRequest({ ...styled, params: { ...styled.params, styleTemplateId: '' } })).toBeNull()
  })

  it('creates closed OCC requests, rejects extended params, and correlates success and failure', () => {
    const s = createCompletedPipeline()
    const read = createPipelineReadRequest(fixtureId(1000), pipelineProjectId)
    const regenerate = createPipelineRegenerateRequest(fixtureId(1000), { projectId: pipelineProjectId,
      expectedVersionId: pipelineVersionId, expectedVersionDigest: pipelineVersionDigest, expectedSettingsRevision: 3, expectedSettingsSnapshotDigest: 'e'.repeat(64) })
    expect(Object.keys(regenerate.params).sort()).toEqual(['projectId', 'expectedVersionId', 'expectedVersionDigest', 'expectedSettingsRevision', 'expectedSettingsSnapshotDigest'].sort())
    expect(parsePipelineRequest({ ...regenerate, params: { ...regenerate.params, automatic: true } })).toBeNull()
    expect(() => createPipelineExecutionRequest(fixtureId(1000), 'pipeline.retry', { projectId: s.projectId, executionId: s.executionId, expectedRevision: 0.5 })).toThrow()
    const response = pipelineResponse(s)
    expect(pipelineResponseMatchesRequest(response, read)).toBe(true)
    expect(pipelineResponseMatchesRequest({ ...response, requestId: fixtureId(999) }, read)).toBe(false)
    expect(pipelineResponseMatchesRequest(response, regenerate)).toBe(false)
    expect(pipelineResponseMatchesRequest({ ...pipelineResponse(null), result: { status: 'conflict', snapshot: null } }, read)).toBe(true)
    expect(parsePipelineResponse({ ...response, result: { status: 'forbidden', snapshot: s } })).toBeNull()
  })

  it.each([true, false])('accepts invalid snapshots with diagnostic DTO=%s without authorizing media', diagnostic => {
    const s = createCompletedPipeline()
    s.resultStatus = 'invalid'
    if (!diagnostic) s.result = null
    const value = pipelineResponse(s)
    const parsed = verifiedPipelineResponse(value)
    expect(parsed).not.toBeNull()
    expect(parsed!.result).toMatchObject({ status: 'invalid', snapshot: { resultStatus: 'invalid', phase: 'Completed' } })
    expect(parsed!.result.snapshot!.result).toEqual(s.result)
    if (parsed!.result.snapshot!.result !== null) {
      const result = parsed!.result.snapshot!.result!
      expect(sourceOnlyMediaUrl(result, result.artifacts[0]!.artifact.artifactId)).toBeNull()
    }
    expect(pipelineResponseMatchesRequest(parsed!, createPipelineReadRequest(value.requestId, s.projectId))).toBe(true)
    expect(parsePipelineResponse({ ...value, result: { status: 'available', snapshot: s } })).toBeNull()
    expect(parsePipelineResponse({ ...value, result: { status: 'invalid', snapshot: { ...s, resultStatus: 'valid' } } })).toBeNull()
    expect(parsePipelineResponse({ ...value, result: { status: 'invalid', snapshot: null } })).not.toBeNull()
  })

  it('never authorizes a previous result included in an invalid diagnostic snapshot', () => {
    const parent = createCompletedPipeline()
    const s = createPipelineSnapshot()
    s.executionId = fixtureId(91); s.rootJobId = fixtureId(92)
    s.parentVersionId = parent.versionId; s.parentDigest = parent.versionDigest
    s.previousResult = parent.result; s.resultStatus = 'invalid'
    const parsed = verifiedPipelineResponse(pipelineResponse(s))!
    expect(parsed).not.toBeNull()
    const previous = parsed.result.snapshot!.previousResult!
    expect(sourceOnlyMediaUrl(previous, previous.artifacts[0]!.artifact.artifactId)).toBeNull()
  })

  it('accepts Delivery reconciliation references as diagnostics, never as playable artifacts', () => {
    const s = createCompletedPipeline()
    s.phase = 'ReconciliationRequired'
    s.result!.generationDisposition = 'ReconciliationRequired'
    const reference = { artifactId: fixtureId(990), contentDigest: 'd'.repeat(64), byteSize: 128, kind: 'Delivery' as const }
    s.result!.reconciliationArtifacts = [reference]
    const result = verifiedPipelineResponse(pipelineResponse(s))!.result.snapshot!.result!
    expect(result.reconciliationArtifacts).toEqual([reference])
    expect(sourceOnlyMediaUrl(result, reference.artifactId)).toBeNull()
    expect(sourceOnlyMediaUrl(result, result.artifacts[0]!.delivery!.artifact.artifactId)).toBeNull()
  })

  it('derives registration freshness from meaningful milestones/identity/references, never poll revisions', () => {
    expect(pipelineRegistrationRefreshKey(null)).toBeUndefined()
    const s = createPipelineSnapshot('Analyzing')
    const before = pipelineRegistrationRefreshKey(s)
    for (const phase of ['Queued', 'Ingesting', 'Analyzing'] as const) {
      expect(pipelineRegistrationRefreshKey({ ...s, phase, revision: 200 })).toBe(before)
    }
    const ready = pipelineRegistrationRefreshKey({ ...s, phase: 'Composing' })
    expect(ready).not.toBe(before)
    expect(pipelineRegistrationRefreshKey({ ...s, phase: 'Composing', revision: 300 })).toBe(ready)
    const rendering = { ...s, phase: 'Rendering' as const, versionId: pipelineVersionId, versionDigest: pipelineVersionDigest }
    const composition = pipelineRegistrationRefreshKey(rendering)
    for (const phase of ['Rendering', 'Evaluating', 'Delivering'] as const) {
      expect(pipelineRegistrationRefreshKey({ ...rendering, phase, revision: 500 })).toBe(composition)
    }
    expect(pipelineRegistrationRefreshKey({ ...rendering, executionId: fixtureId(88) })).not.toBe(composition)
    const completed = createCompletedPipeline()
    const noReferences = pipelineRegistrationRefreshKey(completed)
    completed.result!.transcriptReferences = [
      { revisionId: fixtureId(991), revisionDigest: 'd'.repeat(64), sourceArtifactId: fixtureId(9) },
      { revisionId: fixtureId(992), revisionDigest: 'e'.repeat(64), sourceArtifactId: fixtureId(9) },
    ]
    const registered = pipelineRegistrationRefreshKey(completed)
    expect(registered).not.toBe(noReferences)
    completed.result!.transcriptReferences.reverse()
    expect(pipelineRegistrationRefreshKey(completed)).toBe(registered)
  })

  it('keeps the bridge whitelist correlated, immutable, bounded to one request and disposable', () => {
    const posted: unknown[] = []
    const listeners = new Set<(event: BridgeMessageEvent) => void>()
    const transport: BridgeTransport = { postMessage: request => posted.push(request),
      addEventListener: (_type, listener) => { listeners.add(listener) }, removeEventListener: (_type, listener) => { listeners.delete(listener) } }
    const emit = (data: unknown) => { for (const listener of listeners) listener({ data }) }
    const client = createBridgeClient(transport)
    const listener = vi.fn()
    client.onPipelineResponse(listener)
    const request = createPipelineReadRequest(fixtureId(1000), pipelineProjectId)
    const mutable = { ...request, params: { ...request.params } }
    client.sendPipeline(mutable)
    mutable.params.projectId = fixtureId(999)
    expect(() => client.sendPipeline(request)).toThrow('already pending')
    emit(pipelineResponse(createCompletedPipeline(fixtureId(999))))
    emit(pipelineResponse(createCompletedPipeline(), fixtureId(999)))
    emit({ ...pipelineResponse(createCompletedPipeline()), method: 'pipeline.delete' })
    expect(listener).not.toHaveBeenCalled()
    emit(pipelineResponse(createCompletedPipeline()))
    emit(pipelineResponse(createCompletedPipeline()))
    expect(listener).toHaveBeenCalledOnce()
    expect(posted).toEqual([request])
    client.sendPipeline(request); client.abandonPipelineRequest(request.requestId)
    emit(pipelineResponse(createCompletedPipeline()))
    expect(listener).toHaveBeenCalledOnce()
    client.dispose()
    expect(listeners.size).toBe(0)
    expect(() => client.sendPipeline(request)).toThrow('disposed')
  })
})

// FR-055 AC-030.1
describe('pipeline.versions entries with created time, differing settings and outputs', () => {
  const base = {
    versionId: fixtureId(41), versionDigest: 'a'.repeat(64), parentVersionId: null, executionId: fixtureId(42),
    phase: 'Completed', hasResult: true, isCurrent: true,
  }
  const extended = {
    ...base,
    createdAt: '2026-10-06T12:46:17.5700147+00:00',
    settings: [{ key: 'subtitles.readingSpeedMaximum', value: { kind: 'integer', integerValue: '9' } }],
    outputs: [{ role: 'LandscapeBurnIn', artifactId: fixtureId(43), contentDigest: 'b'.repeat(64), mediaType: 'video/mp4' }],
  }
  const response = (entry: object) => ({
    version: 1, requestId: fixtureId(40), method: 'pipeline.versions',
    result: { status: 'available', workingCopyRevision: 1, versions: [entry] },
  })

  it('accepts the FR-055 fields together, or the older entry without them', () => {
    expect(parsePipelineVersionsResponse(response(extended))?.result.versions[0]).toEqual(extended)
    expect(parsePipelineVersionsResponse(response(base))?.result.versions[0]).toEqual(base)
  })

  it('rejects partial, duplicated or malformed FR-055 fields', () => {
    const { outputs: _outputs, ...partial } = extended
    for (const bad of [
      partial,
      { ...extended, createdAt: '2026-10-06T12:46:17Z' },
      { ...extended, settings: [...extended.settings, ...extended.settings] },
      { ...extended, settings: [{ key: 'x', value: { kind: 'integer', integerValue: 9 } }] },
      { ...extended, outputs: [{ ...extended.outputs[0], mediaType: 'text/plain' }] },
      { ...extended, outputs: [{ ...extended.outputs[0], path: 'C:\\x.mp4' }] },
      { ...extended, extra: true },
    ]) {
      expect(parsePipelineVersionsResponse(response(bad))).toBeNull()
    }
  })
})

