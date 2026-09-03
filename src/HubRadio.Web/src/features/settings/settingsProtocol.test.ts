import { describe, expect, it } from 'vitest'
import { createSettingsResetRequest, createSettingsUpdateRequest, parseSettingsResponse, type SettingsSnapshot } from './settingsProtocol'

const id = '11112222-3333-4444-8555-666677778888'
const digest = 'a'.repeat(64)
const booleanValue = { kind: 'boolean' as const, booleanValue: true }
export const snapshot: SettingsSnapshot = {
  projectId: id, profileId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', profileName: 'test', profileVersion: '1.0.0', profileDigest: digest, initialSnapshotDigest: digest, snapshotDigest: digest, revision: 0, updatedAt: '2026-09-03T00:00:00.0000000+00:00', definitionCount: 1,
  definitions: [{ key: 'subtitles.enabled', group: 'subtitlesAndGraphics', isAdvanced: false, displayName: 'Subtitles', description: 'Enable subtitles.', unit: 'none', defaultValue: booleanValue, minimum: null, maximum: null, allowedTextValues: [], evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }] }],
  initialValues: [{ key: 'subtitles.enabled', value: booleanValue }], values: [{ key: 'subtitles.enabled', value: booleanValue }],
}
function response(value: SettingsSnapshot = snapshot): unknown { return { version: 1, requestId: id, method: 'settings.read', result: { status: 'available', snapshot: value } } }

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
    expect(parseSettingsResponse(response({ ...snapshot, definitions: [{ ...definition, unit: 'minutes' as never }] }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, definitions: [{ ...definition, evidence: [{ kind: 'guess' as never, sourceId: 'R01' }] }] }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, definitions: [{ ...definition, evidence: [{ kind: 'productDecision', sourceId: 'R01' }] }] }))).toBeNull()
    expect(parseSettingsResponse(response({ ...snapshot, values: [{ key: definition.key, value: { kind: 'float' } as never }] }))).toBeNull()
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
