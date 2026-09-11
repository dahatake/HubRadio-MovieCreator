import { afterEach, describe, expect, it, vi } from 'vitest'
import { createPlayerBridgeSession, PLAYER_SEEK_TIMEOUT_MS } from './playerBridgeSession'

afterEach(() => { vi.useRealTimers() })

describe('local player seek ownership', () => {
  it('abandons a timed-out request once and permits a new manual seek', () => {
    vi.useFakeTimers()
    const abandonPlayerRequest = vi.fn()
    const expired = vi.fn()
    const session = createPlayerBridgeSession({ abandonPlayerRequest }, expired)
    expect(session.trackSeek('first')).toBe(true)
    expect(session.trackSeek('second')).toBe(false)
    vi.advanceTimersByTime(PLAYER_SEEK_TIMEOUT_MS)
    expect(abandonPlayerRequest).toHaveBeenCalledExactlyOnceWith('first')
    expect(expired).toHaveBeenCalledExactlyOnceWith('first')
    expect(session.trackSeek('second')).toBe(true)
    session.releaseSeek('first') // A late response cannot release another request.
    expect(vi.getTimerCount()).toBe(1)
    session.releaseSeek('second')
    expect(vi.getTimerCount()).toBe(0)
    vi.advanceTimersByTime(PLAYER_SEEK_TIMEOUT_MS)
    expect(expired).toHaveBeenCalledTimes(1)
    session.dispose()
  })

  it('releases the pending slot even if the optional bridge abandon fails', () => {
    vi.useFakeTimers()
    const expired = vi.fn()
    const session = createPlayerBridgeSession({ abandonPlayerRequest() { throw new Error('Closed transport') } }, expired)
    session.trackSeek('first')
    expect(() => vi.advanceTimersByTime(PLAYER_SEEK_TIMEOUT_MS)).not.toThrow()
    expect(expired).toHaveBeenCalledExactlyOnceWith('first')
    expect(session.trackSeek('second')).toBe(true)
    session.dispose()
    expect(vi.getTimerCount()).toBe(0)
  })

  it('cleans up on disposal without reporting timeout or canceling native work', () => {
    vi.useFakeTimers()
    const expired = vi.fn()
    const session = createPlayerBridgeSession({}, expired)
    session.trackSeek('first')
    session.dispose(); session.dispose()
    expect(vi.getTimerCount()).toBe(0)
    vi.advanceTimersByTime(PLAYER_SEEK_TIMEOUT_MS)
    expect(expired).not.toHaveBeenCalled()
    expect(session.trackSeek('second')).toBe(false)
  })
})