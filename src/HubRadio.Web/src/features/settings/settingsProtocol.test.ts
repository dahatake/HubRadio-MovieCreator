import { describe, expect, expectTypeOf, it } from 'vitest'
import {
  MAX_SETTINGS_REQUEST_UTF8_BYTES, MAX_SETTINGS_RESPONSE_UTF8_BYTES,
  SETTINGS_METHODS, SETTINGS_GLOBAL_METHODS, SETTINGS_FILE_METHODS, SETTINGS_WORKSPACE_METHODS,
  createSettingsReadRequest, createSettingsUpdateRequest, createSettingsResetRequest, createSettingsResetAllRequest,
  createSettingsGlobalReadRequest, createSettingsGlobalUpdateRequest, createSettingsGlobalResetRequest, createSettingsGlobalResetAllRequest,
  createSettingsFileExportRequest, createSettingsFilePreviewRequest, createSettingsFileApplyRequest,
  isSettingsRequestWithinLimit, parseSettingsResponse, parseSettingsGlobalResponse, parseSettingsFileResponse, parseSettingsWorkspaceResponse, parseValue,
  type AnySettingsSnapshot, type GlobalSettingsSnapshot, type GlobalSettingsMutationIdentity,
  type SettingsFileMutationIdentity, type SettingsRequest, type SettingsResponse,
  type SettingsSnapshot, type SettingsWorkspaceMethod, type SettingsWorkspaceRequest, type SettingsWorkspaceResponse, type SettingValue,
} from './settingsProtocol'

const id = '11112222-3333-4444-8555-666677778888'
const digest = 'a'.repeat(64)
const booleanValue = { kind: 'boolean' as const, booleanValue: true }
const falseValue = { kind: 'boolean' as const, booleanValue: false }
const key = 'subtitles.enabled'
const fileSha256 = 'c'.repeat(64)
export const snapshot: SettingsSnapshot = {
  projectId: id, profileId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', profileName: 'test', profileVersion: '1.0.0', profileDigest: digest, initialSnapshotDigest: digest, snapshotDigest: digest, revision: 0, updatedAt: '2026-09-03T00:00:00.0000000+00:00', definitionCount: 1,
  definitions: [{ key: 'subtitles.enabled', group: 'subtitlesAndGraphics', isAdvanced: false, displayName: 'Subtitles', description: 'Enable subtitles.', unit: 'none', defaultValue: booleanValue, minimum: null, maximum: null, allowedTextValues: [], evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }] }],
  initialValues: [{ key: 'subtitles.enabled', value: booleanValue }], values: [{ key: 'subtitles.enabled', value: booleanValue }],
}
const globalSnapshot: GlobalSettingsSnapshot = {
  ...snapshot, projectId: null, revision: 7, snapshotDigest: 'b'.repeat(64), values: [{ key, value: falseValue }],
}
const targets: readonly AnySettingsSnapshot[] = [snapshot, globalSnapshot]
const failureStatuses = ['forbidden', 'notFound', 'unbound', 'conflict', 'invalid', 'unavailable'] as const
const fileFailureStatuses = [...failureStatuses, 'fileNotFound', 'profileMismatch', 'snapshotMismatch'] as const
const responseCases: readonly { method: SettingsWorkspaceMethod; target: AnySettingsSnapshot; scope: string }[] = [
  ...SETTINGS_METHODS.map(method => ({ method, target: snapshot, scope: 'project' })),
  ...SETTINGS_GLOBAL_METHODS.map(method => ({ method, target: globalSnapshot, scope: 'global' })),
  ...SETTINGS_FILE_METHODS.flatMap(method => targets.map(target => ({ method, target, scope: target.projectId === null ? 'global' : 'project' }))),
]
function message(method: unknown, result: unknown) { return { version: 1, requestId: id, method, result } }
function availableResponse(method: SettingsWorkspaceMethod, value: unknown) {
  return message(method, method.startsWith('settings.file.')
    ? { status: 'available', snapshot: value, fileSha256 }
    : { status: 'available', snapshot: value })
}
function response(value: unknown = snapshot): unknown { return availableResponse('settings.read', value) }
function expectedIdentity(target: AnySettingsSnapshot): GlobalSettingsMutationIdentity {
  return {
    expectedRevision: target.revision, expectedProfileId: target.profileId, expectedProfileVersion: target.profileVersion,
    expectedProfileDigest: target.profileDigest, expectedSnapshotDigest: target.snapshotDigest,
  }
}
function valueFields(value: SettingValue) {
  return {
    definitions: [{ ...snapshot.definitions[0]!, defaultValue: value }],
    initialValues: [{ key, value }], values: [{ key, value }],
  }
}

describe('settingsProtocol', () => {
  it('derives and validates the definition count and exact key inventory', () => {
    expect(parseSettingsResponse(response())).not.toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, definitionCount: 123 }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, values: [{ key: 'unknown.key', value: booleanValue }] }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, definitionCount: 0, definitions: [], initialValues: [], values: [] }))).toBeNull()
  })
  it('rejects unknown closed values, units, evidence, and fields', () => {
    expect(parseSettingsResponse({ ...(response() as object), actorSid: 'S-1-5-18' })).toBeNull()
    const definition = snapshot.definitions[0]!
    expect(parseSettingsResponse(response({ ...snapshot, definitions: [{ ...definition, unit: 'minutes' }] }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, definitions: [{ ...definition, evidence: [{ kind: 'guess', sourceId: 'R01' }] }] }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, definitions: [{ ...definition, evidence: [{ kind: 'productDecision', sourceId: 'R01' }] }] }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, values: [{ key: definition.key, value: { kind: 'float' } }] }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, values: [{ key: definition.key, value: { kind: 'integer', integerValue: '9223372036854775808' } }] }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, profileName: ' test' }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, revision: 2147483648 }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, definitionCount: 2147483648 }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, updatedAt: 'x' }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, updatedAt: '2026-09-03T00:00:00.000Z' }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, updatedAt: '2026-02-30T03:04:05.0000000+00:00' }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, updatedAt: '2026-02-31T25:61:61.0000000+00:00' }))).toBeNull()
  })
  it('pins the full OCC identity in mutations', () => {
    const request = createSettingsUpdateRequest(id, snapshot, 'subtitles.enabled', { kind: 'boolean', booleanValue: false })
    expect(request.params).toMatchObject({ expectedRevision: 0, expectedProfileId: snapshot.profileId, expectedProfileVersion: '1.0.0', expectedProfileDigest: digest, expectedSnapshotDigest: digest })
  })
  it('rejects outbound values that do not satisfy the selected definition', () => {
    expect(() => createSettingsUpdateRequest(id, snapshot, 'unknown.key', booleanValue)).toThrow()
    expect(() => createSettingsUpdateRequest(id, snapshot, 'subtitles.enabled', { kind: 'text', textValue: 'true' })).toThrow()
    expect(() => createSettingsResetRequest(id, snapshot, 'unknown.key')).toThrow()
  })
  it('accepts every closed setting value kind', () => {
    const values = [
      { kind: 'boolean', booleanValue: true },
      { kind: 'integer', integerValue: '-1' },
      { kind: 'rational', numerator: '-1', denominator: '2' },
      { kind: 'text', textValue: 'internalOnly' },
      { kind: 'textList', textListValue: ['first', 'second'] },
    ] as const
    const definitions = values.map((value, index) => ({
      key: `test.value${index}`,
      group: 'projectAndOutput' as const,
      isAdvanced: false,
      displayName: `Value ${index}`,
      description: `Closed value ${index}.`,
      unit: 'none' as const,
      defaultValue: value,
      minimum: null,
      maximum: null,
      allowedTextValues: [],
      evidence: [{ kind: 'productDecision' as const, sourceId: 'productDecision' }],
    }))
    const selections = definitions.map(definition => ({ key: definition.key, value: definition.defaultValue }))
    const allKinds: SettingsSnapshot = {
      ...snapshot,
      definitionCount: definitions.length,
      definitions,
      initialValues: selections,
      values: selections,
    }

    expect(parseSettingsResponse(response(allKinds))).not.toBeNull()
  })
})

describe('settings workspace requests', () => {
  it('keeps project exports narrow and exposes the all-11 workspace aliases for UX-049', () => {
    expect(SETTINGS_METHODS).toStrictEqual(['settings.read', 'settings.update', 'settings.reset', 'settings.resetAll'])
    expect(SETTINGS_GLOBAL_METHODS).toStrictEqual(['settings.global.read', 'settings.global.update', 'settings.global.reset', 'settings.global.resetAll'])
    expect(SETTINGS_FILE_METHODS).toStrictEqual(['settings.file.export', 'settings.file.preview', 'settings.file.apply'])
    expect(SETTINGS_WORKSPACE_METHODS).toStrictEqual([...SETTINGS_METHODS, ...SETTINGS_GLOBAL_METHODS, ...SETTINGS_FILE_METHODS])
    expect(new Set(SETTINGS_WORKSPACE_METHODS).size).toBe(11)
    expectTypeOf<SettingsSnapshot['projectId']>().toEqualTypeOf<string>()
    expectTypeOf<GlobalSettingsSnapshot['projectId']>().toEqualTypeOf<null>()
    expectTypeOf<SettingsFileMutationIdentity['projectId']>().toEqualTypeOf<string | null>()
    expectTypeOf<SettingsRequest['method']>().toEqualTypeOf<'settings.read' | 'settings.update' | 'settings.reset' | 'settings.resetAll'>()
    expectTypeOf<SettingsResponse['method']>().toEqualTypeOf<SettingsRequest['method']>()
    expectTypeOf<SettingsRequest['params']['projectId']>().toEqualTypeOf<string>()
    expectTypeOf<SettingsWorkspaceRequest['method']>().toEqualTypeOf<SettingsWorkspaceMethod>()
    expectTypeOf<SettingsWorkspaceResponse['method']>().toEqualTypeOf<SettingsWorkspaceMethod>()
    expectTypeOf<ReturnType<typeof parseSettingsResponse>>().toEqualTypeOf<SettingsResponse | null>()
    expectTypeOf<ReturnType<typeof parseSettingsWorkspaceResponse>>().toEqualTypeOf<SettingsWorkspaceResponse | null>()
  })

  it('preserves the exact four legacy request shapes', () => {
    const identity = { projectId: id, ...expectedIdentity(snapshot) }
    const requests: readonly SettingsRequest[] = [
      createSettingsReadRequest(id, id), createSettingsUpdateRequest(id, snapshot, key, falseValue),
      createSettingsResetRequest(id, snapshot, key), createSettingsResetAllRequest(id, snapshot),
    ]
    expect(requests).toStrictEqual([
      { version: 1, requestId: id, method: 'settings.read', params: { projectId: id } },
      { version: 1, requestId: id, method: 'settings.update', params: { ...identity, key, value: falseValue } },
      { version: 1, requestId: id, method: 'settings.reset', params: { ...identity, key } },
      { version: 1, requestId: id, method: 'settings.resetAll', params: identity },
    ])
  })

  it('builds global requests without projectId and uses the global revision and digest', () => {
    const target: AnySettingsSnapshot = globalSnapshot
    const identity = expectedIdentity(target)
    const requests = [
      createSettingsGlobalReadRequest(id), createSettingsGlobalUpdateRequest(id, target, key, booleanValue),
      createSettingsGlobalResetRequest(id, target, key), createSettingsGlobalResetAllRequest(id, target),
    ]
    expect(requests).toStrictEqual([
      { version: 1, requestId: id, method: 'settings.global.read', params: {} },
      { version: 1, requestId: id, method: 'settings.global.update', params: { ...identity, key, value: booleanValue } },
      { version: 1, requestId: id, method: 'settings.global.reset', params: { ...identity, key } },
      { version: 1, requestId: id, method: 'settings.global.resetAll', params: identity },
    ])
    expect(identity.expectedRevision).toBe(7)
    expect(identity.expectedSnapshotDigest).not.toBe(snapshot.snapshotDigest)
    for (const request of requests) {
      expect(Object.hasOwn(request.params, 'projectId')).toBe(false)
      expect(isSettingsRequestWithinLimit(request)).toBe(true)
    }
  })

  it('builds file requests for both scopes with explicit nullable projectId and exact hash fields', () => {
    for (const target of targets) {
      const identity = { projectId: target.projectId, ...expectedIdentity(target) }
      expect(createSettingsFileExportRequest(id, target, null)).toStrictEqual({
        version: 1, requestId: id, method: 'settings.file.export', params: { ...identity, expectedFileSha256: null },
      })
      expect(createSettingsFileExportRequest(id, target, fileSha256)).toStrictEqual({
        version: 1, requestId: id, method: 'settings.file.export', params: { ...identity, expectedFileSha256: fileSha256 },
      })
      expect(createSettingsFilePreviewRequest(id, target)).toStrictEqual({
        version: 1, requestId: id, method: 'settings.file.preview', params: identity,
      })
      expect(createSettingsFileApplyRequest(id, target, fileSha256)).toStrictEqual({
        version: 1, requestId: id, method: 'settings.file.apply', params: { ...identity, expectedFileSha256: fileSha256 },
      })
      const wire: unknown = JSON.parse(JSON.stringify(createSettingsFilePreviewRequest(id, target)))
      expect(wire).toStrictEqual({ version: 1, requestId: id, method: 'settings.file.preview', params: identity })
    }
  })

  it('rejects cross-scope global mutations and does not add scope to legacy builders', () => {
    expect(() => createSettingsGlobalUpdateRequest(id, snapshot, key, booleanValue)).toThrow()
    expect(() => createSettingsGlobalResetRequest(id, snapshot, key)).toThrow()
    expect(() => createSettingsGlobalResetAllRequest(id, snapshot)).toThrow()
    expect(() => {
      // @ts-expect-error Legacy project builders must not accept a global snapshot.
      createSettingsResetAllRequest(id, globalSnapshot)
    }).toThrow()
  })

  it('requires a hash for apply and explicit null or a hash for export', () => {
    for (const hash of ['', ' ', 'A'.repeat(64), 'g'.repeat(64), 'a'.repeat(63), 'a'.repeat(65), `${fileSha256}\n`]) {
      expect(() => createSettingsFileExportRequest(id, globalSnapshot, hash)).toThrow()
      expect(() => createSettingsFileApplyRequest(id, globalSnapshot, hash)).toThrow()
    }
    expect(() => {
      // @ts-expect-error Apply cannot use a nullable hash.
      createSettingsFileApplyRequest(id, globalSnapshot, null)
    }).toThrow()
    expect(() => {
      // @ts-expect-error Missing apply hashes are not silently filled in.
      createSettingsFileApplyRequest(id, globalSnapshot)
    }).toThrow()
    expect(() => {
      // @ts-expect-error Export must explicitly select create-if-absent or a confirmed hash.
      createSettingsFileExportRequest(id, globalSnapshot)
    }).toThrow()
  })

  it('validates canonical request IDs for all 11 builders and file target IDs', () => {
    const builders: readonly ((requestId: string) => SettingsWorkspaceRequest)[] = [
      requestId => createSettingsReadRequest(requestId, id),
      requestId => createSettingsUpdateRequest(requestId, snapshot, key, booleanValue),
      requestId => createSettingsResetRequest(requestId, snapshot, key),
      requestId => createSettingsResetAllRequest(requestId, snapshot),
      createSettingsGlobalReadRequest,
      requestId => createSettingsGlobalUpdateRequest(requestId, globalSnapshot, key, booleanValue),
      requestId => createSettingsGlobalResetRequest(requestId, globalSnapshot, key),
      requestId => createSettingsGlobalResetAllRequest(requestId, globalSnapshot),
      requestId => createSettingsFileExportRequest(requestId, globalSnapshot, null),
      requestId => createSettingsFilePreviewRequest(requestId, globalSnapshot),
      requestId => createSettingsFileApplyRequest(requestId, globalSnapshot, fileSha256),
    ]
    for (const invalidId of ['', 'global', '00000000-0000-0000-0000-000000000000', snapshot.profileId.toUpperCase(), `${id}\n`, ` ${id}`, `{${id}}`]) {
      for (const build of builders) expect(() => build(invalidId)).toThrow()
      expect(() => createSettingsReadRequest(id, invalidId)).toThrow()
      expect(() => createSettingsFilePreviewRequest(id, { ...snapshot, projectId: invalidId })).toThrow()
    }
  })

  it('rejects noncanonical OCC identities rather than copying them into requests', () => {
    const invalidIdentities: readonly Partial<Omit<SettingsSnapshot, 'projectId'>>[] = [
      { revision: -1 }, { revision: 0.5 }, { revision: 2147483648 }, { revision: Number.NaN }, { revision: Number.POSITIVE_INFINITY },
      { profileId: '00000000-0000-0000-0000-000000000000' }, { profileId: `${snapshot.profileId}\n` },
      { profileVersion: '01.0.0' }, { profileVersion: '1.0.0\n' }, { profileVersion: '1.0' },
      { profileVersion: '2147483648.0.0' }, { profileVersion: '0.2147483648.0' }, { profileVersion: '0.0.2147483648' },
      { profileDigest: digest.toUpperCase() }, { profileDigest: `${digest}\n` }, { snapshotDigest: '' }, { snapshotDigest: digest.slice(1) },
    ]
    for (const patch of invalidIdentities) {
      for (const target of targets) {
        const invalid: AnySettingsSnapshot = { ...target, ...patch }
        expect(() => createSettingsFileExportRequest(id, invalid, null)).toThrow()
        expect(() => createSettingsFilePreviewRequest(id, invalid)).toThrow()
        expect(() => createSettingsFileApplyRequest(id, invalid, fileSha256)).toThrow()
        if (invalid.projectId === null) {
          expect(() => createSettingsGlobalUpdateRequest(id, invalid, key, booleanValue)).toThrow()
          expect(() => createSettingsGlobalResetRequest(id, invalid, key)).toThrow()
          expect(() => createSettingsGlobalResetAllRequest(id, invalid)).toThrow()
        } else {
          expect(() => createSettingsUpdateRequest(id, invalid, key, booleanValue)).toThrow()
          expect(() => createSettingsResetRequest(id, invalid, key)).toThrow()
          expect(() => createSettingsResetAllRequest(id, invalid)).toThrow()
        }
      }
    }
  })

  it('reuses closed key and value validation for global mutations', () => {
    for (const invalidKey of ['', 'unknown.key', `${key}\n`, 'subtitles..enabled', 'a'.repeat(129)]) {
      expect(() => createSettingsGlobalUpdateRequest(id, globalSnapshot, invalidKey, booleanValue)).toThrow()
      expect(() => createSettingsGlobalResetRequest(id, globalSnapshot, invalidKey)).toThrow()
    }
    expect(() => createSettingsGlobalUpdateRequest(id, globalSnapshot, key, { kind: 'text', textValue: 'true' })).toThrow()
    const extraValue = { ...booleanValue, actorSid: 'S-1-5-18' }
    expect(() => createSettingsGlobalUpdateRequest(id, globalSnapshot, key, extraValue)).toThrow()
    const extraSnapshot = { ...globalSnapshot, path: 'private', command: 'not-a-wire-field' }
    expect(createSettingsFilePreviewRequest(id, extraSnapshot).params).toStrictEqual({ projectId: null, ...expectedIdentity(globalSnapshot) })
  })
})

describe('settings workspace responses', () => {
  it.each(responseCases)('parses $method ($scope) and keeps family parsers separate', ({ method, target }) => {
    const wire = availableResponse(method, target)
    expect(parseSettingsWorkspaceResponse(wire)).toStrictEqual(wire)
    expect(parseSettingsResponse(wire)).toStrictEqual(SETTINGS_METHODS.some(item => item === method) ? wire : null)
    expect(parseSettingsGlobalResponse(wire)).toStrictEqual(SETTINGS_GLOBAL_METHODS.some(item => item === method) ? wire : null)
    expect(parseSettingsFileResponse(wire)).toStrictEqual(SETTINGS_FILE_METHODS.some(item => item === method) ? wire : null)
  })

  it.each([...SETTINGS_METHODS, ...SETTINGS_GLOBAL_METHODS])('keeps the exact old status/snapshot shape for %s', method => {
    for (const status of failureStatuses) {
      const wire = message(method, { status, snapshot: null })
      expect(parseSettingsWorkspaceResponse(wire)).toStrictEqual(wire)
      expect(parseSettingsResponse(wire)).toStrictEqual(SETTINGS_METHODS.some(item => item === method) ? wire : null)
      expect(parseSettingsWorkspaceResponse(message(method, { status, snapshot }))).toBeNull()
      expect(parseSettingsWorkspaceResponse(message(method, { status, snapshot: null, fileSha256: null }))).toBeNull()
    }
    for (const status of ['fileNotFound', 'profileMismatch', 'snapshotMismatch', 'overwriteRequired', 'success', 'available\n', null]) {
      expect(parseSettingsWorkspaceResponse(message(method, { status, snapshot: null }))).toBeNull()
    }
    const target = method.startsWith('settings.global.') ? globalSnapshot : snapshot
    expect(parseSettingsWorkspaceResponse(message(method, { status: 'available', snapshot: target, fileSha256 }))).toBeNull()
    expect(parseSettingsWorkspaceResponse(message(method, { status: 'available', snapshot: null }))).toBeNull()
  })

  it.each(SETTINGS_FILE_METHODS)('enforces every closed file status/hash/snapshot combination for %s', method => {
    for (const status of fileFailureStatuses) {
      const wire = message(method, { status, snapshot: null, fileSha256: null })
      expect(parseSettingsFileResponse(wire)).toStrictEqual(wire)
      expect(parseSettingsWorkspaceResponse(wire)).toStrictEqual(wire)
      expect(parseSettingsFileResponse(message(method, { status, snapshot: null, fileSha256 }))).toBeNull()
      expect(parseSettingsFileResponse(message(method, { status, snapshot: globalSnapshot, fileSha256: null }))).toBeNull()
    }
    // The schema shares this result across all three file methods, not only export.
    const overwrite = message(method, { status: 'overwriteRequired', snapshot: null, fileSha256 })
    expect(parseSettingsFileResponse(overwrite)).toStrictEqual(overwrite)
    const invalidResults: readonly unknown[] = [
      { status: 'overwriteRequired', snapshot: null, fileSha256: null },
      { status: 'overwriteRequired', snapshot, fileSha256 },
      { status: 'available', snapshot: null, fileSha256 },
      { status: 'available', snapshot, fileSha256: null },
      { status: 'available', snapshot },
      { status: 'available', snapshot, fileSha256, path: 'private' },
      { status: 'available', snapshot, fileSha256, fileBody: '{}' },
      { status: 'available', snapshot, fileSha256, error: 'private exception' },
      { status: 'success', snapshot: null, fileSha256: null },
      { status: 'conflict\n', snapshot: null, fileSha256: null },
      { status: null, snapshot: null, fileSha256: null },
    ]
    for (const result of invalidResults) expect(parseSettingsWorkspaceResponse(message(method, result))).toBeNull()
    for (const hash of ['', 'A'.repeat(64), 'a'.repeat(63), 'a'.repeat(65), `${fileSha256}\n`, 0, undefined]) {
      for (const status of ['available', 'overwriteRequired']) {
        expect(parseSettingsWorkspaceResponse(message(method, { status, snapshot: status === 'available' ? snapshot : null, fileSha256: hash }))).toBeNull()
      }
    }
  })

  it('requires global null scope, project canonical scope, and profile defaults only for global initial values', () => {
    for (const method of SETTINGS_METHODS) expect(parseSettingsWorkspaceResponse(availableResponse(method, globalSnapshot))).toBeNull()
    for (const method of SETTINGS_GLOBAL_METHODS) expect(parseSettingsWorkspaceResponse(availableResponse(method, snapshot))).toBeNull()
    const projectInitial = { ...snapshot, initialValues: [{ key, value: falseValue }], values: [{ key, value: falseValue }] }
    expect(parseSettingsResponse(response(projectInitial))).not.toBeNull()
    expect(parseSettingsFileResponse(availableResponse('settings.file.preview', projectInitial))).not.toBeNull()
    const invalidGlobal = { ...projectInitial, projectId: null }
    expect(parseSettingsGlobalResponse(availableResponse('settings.global.read', invalidGlobal))).toBeNull()
    expect(parseSettingsFileResponse(availableResponse('settings.file.preview', invalidGlobal))).toBeNull()
    for (const projectId of ['', 'global', '00000000-0000-0000-0000-000000000000', undefined, 0]) {
      for (const method of SETTINGS_FILE_METHODS) expect(parseSettingsFileResponse(availableResponse(method, { ...snapshot, projectId }))).toBeNull()
    }
  })

  it.each(responseCases)('rejects missing or extra envelope/snapshot properties for $method ($scope)', ({ method, target }) => {
    const wire = availableResponse(method, target)
    for (const field of ['version', 'requestId', 'method', 'result']) {
      const invalid: Record<string, unknown> = { ...wire }
      delete invalid[field]
      expect(parseSettingsWorkspaceResponse(invalid)).toBeNull()
    }
    for (const field of Object.keys(target)) {
      const invalid: Record<string, unknown> = { ...target }
      delete invalid[field]
      expect(parseSettingsWorkspaceResponse(availableResponse(method, invalid))).toBeNull()
    }
    expect(parseSettingsWorkspaceResponse({ ...wire, actorSid: 'S-1-5-18' })).toBeNull()
    expect(parseSettingsWorkspaceResponse({ ...wire, error: { code: 'unavailable' } })).toBeNull()
    expect(parseSettingsWorkspaceResponse(availableResponse(method, { ...target, fileSha256 }))).toBeNull()
    for (const version of [0, 2, '1', null]) expect(parseSettingsWorkspaceResponse({ ...wire, version })).toBeNull()
    for (const requestId of ['', `${id}\n`, '00000000-0000-0000-0000-000000000000', null]) expect(parseSettingsWorkspaceResponse({ ...wire, requestId })).toBeNull()
  })

  it.each(responseCases)('rejects malformed identities and inventories for $method ($scope)', ({ method, target }) => {
    const definition = target.definitions[0]!
    const invalidChanges: readonly Record<string, unknown>[] = [
      { profileId: null }, { profileId: '00000000-0000-0000-0000-000000000000' }, { profileId: `${target.profileId}\n` },
      { profileDigest: digest.toUpperCase() }, { snapshotDigest: `${digest}\n` }, { initialSnapshotDigest: digest.slice(1) },
      { revision: -1 }, { revision: 0.5 }, { revision: '0' }, { revision: Number.NaN }, { revision: 2147483648 },
      { profileVersion: '01.0.0' }, { profileVersion: '1.0.0\n' }, { profileVersion: '2147483648.0.0' },
      { profileName: '' }, { profileName: ' test' }, { updatedAt: `${snapshot.updatedAt}\n` }, { updatedAt: '2026-02-30T00:00:00.0000000+00:00' },
      { definitionCount: 0 }, { definitionCount: 2 }, { definitions: [definition, definition], definitionCount: 2 },
      { definitions: [{ ...definition, command: 'not-a-wire-field' }] }, { definitions: [{ ...definition, group: 'unknown' }] },
      { definitions: [{ ...definition, evidence: [{ kind: 'productDecision', sourceId: 'R01' }] }] },
      { definitions: [{ ...definition, evidence: [{ kind: 'currentImplementation', sourceId: 'C01\n' }] }] },
      { definitions: [{ ...definition, evidence: [{ kind: 'productDecision', sourceId: 'productDecision', path: 'private' }] }] },
      { initialValues: [] }, { values: [] }, { values: [target.values[0], target.values[0]] },
      { initialValues: [{ key: 'unknown.key', value: booleanValue }] },
      { values: [{ key, value: booleanValue, path: 'private' }] },
      { values: [{ key, value: { ...booleanValue, actorSid: 'S-1-5-18' } }] },
    ]
    for (const patch of invalidChanges) expect(parseSettingsWorkspaceResponse(availableResponse(method, { ...target, ...patch }))).toBeNull()
  })

  it('preserves zero values and the native Int32 upper bounds without equating file and snapshot digests', () => {
    for (const revision of [0, 2147483647]) {
      for (const profileVersion of ['0.0.0', '2147483647.2147483647.2147483647']) {
        const target: GlobalSettingsSnapshot = { ...globalSnapshot, revision, profileVersion, profileDigest: '0'.repeat(64) }
        expect(createSettingsGlobalResetAllRequest(id, target).params).toStrictEqual(expectedIdentity(target))
        const wire = availableResponse('settings.file.export', target)
        expect(parseSettingsWorkspaceResponse(wire)).toStrictEqual(wire)
      }
    }
  })

  it('preserves preview candidates at revision zero and builds apply against saved state', () => {
    for (const target of [snapshot, { ...snapshot, projectId: null }]) {
      const candidate = { ...target, snapshotDigest: 'd'.repeat(64), values: [{ key, value: falseValue }] }
      const wire = availableResponse('settings.file.preview', candidate)
      expect(parseSettingsWorkspaceResponse(wire)).toStrictEqual(wire)
      expect(target.revision).toBe(0)
      expect(target.values).toStrictEqual([{ key, value: booleanValue }])
      const apply = createSettingsFileApplyRequest(id, target, fileSha256)
      expect(apply.params.expectedSnapshotDigest).toBe(target.snapshotDigest)
      expect(apply.params.expectedSnapshotDigest).not.toBe(candidate.snapshotDigest)
      expect(apply.params.expectedRevision).toBe(0)
    }
  })

  it('rejects methods outside the finite contract and non-object messages', () => {
    for (const method of ['settings.global.write', 'settings.global.resetall', 'settings.file.read', 'settings.file.apply.extra', 'settings.file.apply;calc', 'settings.global.re\u0430d', 'Settings.read', 'settings.read\n', null]) {
      expect(parseSettingsWorkspaceResponse(message(method, { status: 'unavailable', snapshot: null }))).toBeNull()
    }
    for (const value of [undefined, null, true, 0, [], JSON.stringify(response())]) expect(parseSettingsWorkspaceResponse(value)).toBeNull()
  })
})

describe('settings workspace values and limits', () => {
  it('round-trips all five value kinds, Int64 extremes, and zero strings without numeric coercion or trimming', () => {
    const values: readonly SettingValue[] = [
      falseValue, { kind: 'integer', integerValue: '0' },
      { kind: 'integer', integerValue: '-9223372036854775808' }, { kind: 'integer', integerValue: '9223372036854775807' },
      { kind: 'integer', integerValue: '9007199254740993' }, { kind: 'rational', numerator: '0', denominator: '1' },
      { kind: 'rational', numerator: '-9223372036854775808', denominator: '9223372036854775807' },
      { kind: 'text', textValue: '0' }, { kind: 'text', textValue: '日本語の設定 🎬' },
      { kind: 'textList', textListValue: [] }, { kind: 'textList', textListValue: ['0', '日本語', 'two words'] },
    ]
    for (const value of values) {
      const target: GlobalSettingsSnapshot = { ...globalSnapshot, ...valueFields(value) }
      const request = createSettingsGlobalUpdateRequest(id, target, key, value)
      const requestWire: unknown = JSON.parse(JSON.stringify(request))
      expect(requestWire).toStrictEqual(request)
      expect(parseValue(value)).toStrictEqual(value)
      for (const method of [...SETTINGS_GLOBAL_METHODS, ...SETTINGS_FILE_METHODS]) {
        const wire = availableResponse(method, target)
        const received: unknown = JSON.parse(JSON.stringify(wire))
        expect(parseSettingsWorkspaceResponse(received)).toStrictEqual(wire)
      }
    }
  })

  it('rejects numeric coercion, noncanonical numbers, invalid Unicode, and blank or padded text', () => {
    const invalid: readonly unknown[] = [
      { kind: 'integer', integerValue: 1 }, { kind: 'integer', integerValue: '9223372036854775808' },
      { kind: 'integer', integerValue: '-9223372036854775809' }, { kind: 'integer', integerValue: '00' },
      { kind: 'integer', integerValue: '-0' }, { kind: 'integer', integerValue: '+1' },
      { kind: 'integer', integerValue: '1e1' }, { kind: 'integer', integerValue: '1\n' },
      { kind: 'rational', numerator: '0', denominator: '0' }, { kind: 'rational', numerator: '1', denominator: '-1' },
      { kind: 'rational', numerator: '1', denominator: '9223372036854775808' }, { kind: 'rational', numerator: '1.0', denominator: '2' },
      { kind: 'text', textValue: '' }, { kind: 'text', textValue: ' ' }, { kind: 'text', textValue: ' 0' },
      { kind: 'text', textValue: '0 ' }, { kind: 'text', textValue: '0\n' },
      { kind: 'text', textValue: '\ud800' }, { kind: 'text', textValue: '\udc00' }, { kind: 'text', textValue: 'a'.repeat(4097) },
      { kind: 'textList', textListValue: ['first', 'first'] }, { kind: 'textList', textListValue: [''] },
      { kind: 'textList', textListValue: Array.from({ length: 65 }, (_, index) => String(index)) },
      { kind: 'boolean', booleanValue: 0 }, { kind: 'boolean', booleanValue: true, textValue: 'true' },
    ]
    for (const value of invalid) {
      expect(parseValue(value)).toBeNull()
      const target = { ...globalSnapshot, values: [{ key, value }] }
      expect(parseSettingsGlobalResponse(availableResponse('settings.global.read', target))).toBeNull()
      expect(parseSettingsFileResponse(availableResponse('settings.file.preview', target))).toBeNull()
    }
  })

  it('uses exact integer/rational ranges and closed text allowlists for global updates and file snapshots', () => {
    const cases: readonly { accepted: SettingValue; rejected: SettingValue; allowedTextValues: readonly string[] }[] = [
      { accepted: { kind: 'integer', integerValue: '9007199254740993' }, rejected: { kind: 'integer', integerValue: '9007199254740992' }, allowedTextValues: [] },
      { accepted: { kind: 'rational', numerator: '9007199254740993', denominator: '9007199254740992' }, rejected: { kind: 'rational', numerator: '1', denominator: '1' }, allowedTextValues: [] },
      { accepted: { kind: 'text', textValue: 'internalOnly' }, rejected: { kind: 'text', textValue: 'externalCandidate' }, allowedTextValues: ['internalOnly'] },
    ]
    for (const { accepted, rejected, allowedTextValues } of cases) {
      const fields = valueFields(accepted)
      const target: GlobalSettingsSnapshot = {
        ...globalSnapshot, ...fields,
        definitions: [{ ...fields.definitions[0]!, minimum: accepted.kind === 'text' ? null : accepted, maximum: accepted.kind === 'text' ? null : accepted, allowedTextValues }],
      }
      expect(createSettingsGlobalUpdateRequest(id, target, key, accepted).params.value).toStrictEqual(accepted)
      expect(() => createSettingsGlobalUpdateRequest(id, target, key, rejected)).toThrow()
      expect(parseSettingsFileResponse(availableResponse('settings.file.preview', target))).not.toBeNull()
      expect(parseSettingsFileResponse(availableResponse('settings.file.preview', { ...target, values: [{ key, value: rejected }] }))).toBeNull()
    }
  })

  it('keeps the 64 KiB request budget for the workspace union and counts UTF-8 bytes', () => {
    expect(MAX_SETTINGS_REQUEST_UTF8_BYTES).toBe(64 * 1024)
    const value: SettingValue = { kind: 'textList', textListValue: Array.from({ length: 8 }, (_, index) => `${index}${'あ'.repeat(4095)}`) }
    const project: SettingsSnapshot = { ...snapshot, ...valueFields(value) }
    const global: GlobalSettingsSnapshot = { ...globalSnapshot, ...valueFields(value) }
    for (const request of [createSettingsUpdateRequest(id, project, key, value), createSettingsGlobalUpdateRequest(id, global, key, value)]) {
      expect(JSON.stringify(request).length).toBeLessThan(MAX_SETTINGS_REQUEST_UTF8_BYTES)
      expect(isSettingsRequestWithinLimit(request)).toBe(false)
    }
    for (const target of targets) {
      expect(isSettingsRequestWithinLimit(createSettingsFileExportRequest(id, target, null))).toBe(true)
      expect(isSettingsRequestWithinLimit(createSettingsFilePreviewRequest(id, target))).toBe(true)
      expect(isSettingsRequestWithinLimit(createSettingsFileApplyRequest(id, target, fileSha256))).toBe(true)
    }
  })

  it('retains the 4 MiB response budget for project, global, and file messages', () => {
    expect(MAX_SETTINGS_RESPONSE_UTF8_BYTES).toBe(4 * 1024 * 1024)
    const length = Math.floor(MAX_SETTINGS_RESPONSE_UTF8_BYTES / 3)
    for (const { method, target } of responseCases.filter(item => item.method === 'settings.read' || item.method === 'settings.global.read' || item.method === 'settings.file.preview')) {
      expect(parseSettingsWorkspaceResponse(availableResponse(method, { ...target, profileName: 'a'.repeat(length) }))).not.toBeNull()
      expect(parseSettingsWorkspaceResponse(availableResponse(method, { ...target, profileName: 'あ'.repeat(length) }))).toBeNull()
    }
    const cyclic: Record<string, unknown> = message('settings.file.preview', null)
    cyclic.result = cyclic
    expect(parseSettingsWorkspaceResponse(cyclic)).toBeNull()
  })
})
