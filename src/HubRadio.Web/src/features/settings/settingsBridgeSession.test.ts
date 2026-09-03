import { describe, expect, it, vi } from 'vitest'
import { createSettingsReadRequest, type SettingsRequest, type SettingsResponse } from './settingsProtocol'
import { startSettingsBridgeSession, type SettingsBridgeClient } from './settingsBridgeSession'

const id = '11112222-3333-4444-8555-666677778888'

describe('settingsBridgeSession', () => {
  it('correlates one pending request and abandons it on disposal', () => {
    let listener: ((response: SettingsResponse) => void) | null = null
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn(), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
      onSettingsResponse: vi.fn((next) => { listener = next; return vi.fn() }),
    }
    const dispatch = vi.fn()
    const session = startSettingsBridgeSession(() => client, dispatch)
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
    const session = startSettingsBridgeSession(() => client, dispatch)
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
    const session = startSettingsBridgeSession(() => client, dispatch)
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
    vi.useFakeTimers()
    try {
      let listener: ((response: SettingsResponse) => void) | null = null
      const client: SettingsBridgeClient = {
        sendSettings: vi.fn(), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
        onSettingsResponse: vi.fn((next) => { listener = next; return vi.fn() }),
      }
      const dispatch = vi.fn()
      const session = startSettingsBridgeSession(() => client, dispatch)
      const request = createSettingsReadRequest(id, 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee')
      expect(session.send(request)).toBe(true)

      vi.advanceTimersByTime(5_000)
      listener!({ version: 1, requestId: request.requestId, method: request.method, result: { status: 'notFound', snapshot: null } })

      expect(client.abandonSettingsRequest).toHaveBeenCalledWith(request.requestId)
      expect(dispatch).toHaveBeenLastCalledWith({ type: 'bridgeUnavailable' })
      expect(dispatch).toHaveBeenCalledTimes(2)
      session.dispose()
    } finally {
      vi.useRealTimers()
    }
  })
})
