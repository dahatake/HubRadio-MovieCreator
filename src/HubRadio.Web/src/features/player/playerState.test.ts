import { describe, expect, it } from 'vitest'

import {
  parsePlayerReadResponse,
  parsePlayerSeekResponse,
  type PlayerReadResponse,
  type PlayerSeekResponse,
} from './playerProtocol'
import {
  initialPlayerState,
  playerStateReducer,
  selectCurrentFrame,
  selectKeyboardSeekSequence,
  type PlayerAction,
  type PlayerState,
} from './playerState'
import {
  createPlayerReadResponseValue,
  createPlayerSeekResponseValue,
  otherPlayerRequestId,
  playerEvidenceDigest,
  playerJobId,
  playerRequestId,
} from './playerTestFixture'

describe('player state', () => {
  it('accepts only the matching read and stores one canonical frame sequence', () => {
    const loading = reduce({
      type: 'readRequested',
      requestId: playerRequestId,
      jobId: playerJobId,
    })
    const staleResponse = readResponse(otherPlayerRequestId)
    expect(playerStateReducer(loading, { type: 'readReceived', response: staleResponse })).toBe(
      loading,
    )

    const available = playerStateReducer(loading, {
      type: 'readReceived',
      response: readResponse(playerRequestId),
    })

    expect(available.status).toBe('available')
    expect(available.pendingReadRequestId).toBeNull()
    expect(available.currentFrameSequence).toBe(0)
    expect(selectCurrentFrame(available)?.sourcePresentationTime).toEqual({
      value: '-1',
      rate: '10',
    })
    expect(Object.keys(available).sort()).toEqual([
      'currentFrameSequence',
      'jobId',
      'jobIdInput',
      'pendingReadRequestId',
      'pendingSeek',
      'snapshot',
      'status',
    ])
    expect(Object.keys(available)).not.toContain('currentTime')
    expect(Object.keys(available)).not.toContain('sourceTime')
    expect(Object.keys(available)).not.toContain('outputTime')
  })

  it('ignores out-of-order and duplicate seek responses', () => {
    const available = availableState()
    const pending = playerStateReducer(available, {
      type: 'seekRequested',
      requestId: otherPlayerRequestId,
      proxyFrameSequence: 2,
    })
    expect(pending.pendingSeek).toEqual({
      requestId: otherPlayerRequestId,
      proxyFrameSequence: 2,
      evidenceDigest: playerEvidenceDigest,
    })

    const stale = seekResponse(playerRequestId, 2)
    expect(playerStateReducer(pending, { type: 'seekReceived', response: stale })).toBe(
      pending,
    )

    const accepted = playerStateReducer(pending, {
      type: 'seekReceived',
      response: seekResponse(otherPlayerRequestId, 2),
    })
    expect(accepted.status).toBe('available')
    expect(accepted.currentFrameSequence).toBe(2)
    expect(accepted.pendingSeek).toBeNull()
    expect(
      playerStateReducer(accepted, {
        type: 'seekReceived',
        response: seekResponse(otherPlayerRequestId, 2),
      }),
    ).toBe(accepted)
  })

  it('rejects a native seek position that differs from the loaded evidence', () => {
    const pending = playerStateReducer(availableState(), {
      type: 'seekRequested',
      requestId: otherPlayerRequestId,
      proxyFrameSequence: 2,
    })
    const value = createPlayerSeekResponseValue(otherPlayerRequestId, 2)
    const position = (value.result as {
      position: { sourceFrameSequence: number }
    }).position
    position.sourceFrameSequence = 1
    const response = parsePlayerSeekResponse(value)
    if (response === null) {
      throw new Error('Expected a structurally valid changed seek response.')
    }

    const rejected = playerStateReducer(pending, {
      type: 'seekReceived',
      response,
    })

    expect(rejected.status).toBe('mismatched')
    expect(rejected.snapshot).toBeNull()
    expect(rejected.currentFrameSequence).toBeNull()
  })

  it('does not clamp invalid seek requests and closes native invalid-time responses', () => {
    const available = availableState()
    expect(
      playerStateReducer(available, {
        type: 'seekRequested',
        requestId: otherPlayerRequestId,
        proxyFrameSequence: -1,
      }),
    ).toBe(available)
    expect(
      playerStateReducer(available, {
        type: 'seekRequested',
        requestId: otherPlayerRequestId,
        proxyFrameSequence: 3,
      }),
    ).toBe(available)
    expect(
      playerStateReducer(available, {
        type: 'seekRequested',
        requestId: otherPlayerRequestId,
        proxyFrameSequence: 0.5,
      }),
    ).toBe(available)
    expect(
      playerStateReducer(available, {
        type: 'playbackFrameObserved',
        proxyFrameSequence: 0.5,
      }),
    ).toBe(available)

    const pending = playerStateReducer(available, {
      type: 'seekRequested',
      requestId: otherPlayerRequestId,
      proxyFrameSequence: 2,
    })
    const invalid = parsePlayerSeekResponse({
      version: 1,
      requestId: otherPlayerRequestId,
      method: 'player.seek',
      result: { status: 'invalidTime', position: null },
    })
    if (invalid === null) {
      throw new Error('Expected a valid invalid-time response.')
    }
    const closed = playerStateReducer(pending, {
      type: 'seekReceived',
      response: invalid,
    })
    expect(closed.status).toBe('invalidTime')
    expect(closed.snapshot).toBeNull()
  })

  it('uses explicit keyboard boundaries without fabricating an adjacent frame', () => {
    const first = availableState()
    expect(selectKeyboardSeekSequence(first, 'ArrowLeft')).toBeNull()
    expect(selectKeyboardSeekSequence(first, 'Home')).toBeNull()
    expect(selectKeyboardSeekSequence(first, 'ArrowRight')).toBe(1)
    expect(selectKeyboardSeekSequence(first, 'End')).toBe(2)
    expect(selectKeyboardSeekSequence(first, 'PageDown')).toBeNull()

    const last = playerStateReducer(first, {
      type: 'playbackFrameObserved',
      proxyFrameSequence: 2,
    })
    expect(selectKeyboardSeekSequence(last, 'ArrowRight')).toBeNull()
    expect(selectKeyboardSeekSequence(last, 'End')).toBeNull()
    expect(selectKeyboardSeekSequence(last, 'ArrowLeft')).toBe(1)
  })

  it('keeps canonical evidence when media loading fails and recovers on can-play', () => {
    const available = availableState()
    const failed = playerStateReducer(available, { type: 'mediaUnavailable' })
    expect(failed.status).toBe('mediaUnavailable')
    expect(failed.snapshot).toBe(available.snapshot)
    expect(failed.currentFrameSequence).toBe(0)

    const recovered = playerStateReducer(failed, { type: 'mediaAvailable' })
    expect(recovered.status).toBe('available')
    expect(recovered.snapshot).toBe(available.snapshot)
  })

  it.each(['forbidden', 'unavailable', 'stale', 'mismatched'] as const)(
    'clears evidence for a %s read',
    (status) => {
      const loading = reduce({
        type: 'readRequested',
        requestId: playerRequestId,
        jobId: playerJobId,
      })
      const response = parsePlayerReadResponse({
        version: 1,
        requestId: playerRequestId,
        method: 'player.read',
        result: { status, snapshot: null },
      })
      if (response === null) {
        throw new Error('Expected a valid unavailable read response.')
      }

      const state = playerStateReducer(loading, {
        type: 'readReceived',
        response,
      })
      expect(state.status).toBe(status)
      expect(state.snapshot).toBeNull()
      expect(state.currentFrameSequence).toBeNull()
    },
  )
})

function availableState(): PlayerState {
  return reduce(
    {
      type: 'readRequested',
      requestId: playerRequestId,
      jobId: playerJobId,
    },
    {
      type: 'readReceived',
      response: readResponse(playerRequestId),
    },
  )
}

function readResponse(requestId: string): PlayerReadResponse {
  const value = createPlayerReadResponseValue()
  value.requestId = requestId
  const parsed = parsePlayerReadResponse(value)
  if (parsed === null) {
    throw new Error('Expected a valid player read response.')
  }
  return parsed
}

function seekResponse(
  requestId: string,
  proxyFrameSequence: number,
): PlayerSeekResponse {
  const parsed = parsePlayerSeekResponse(
    createPlayerSeekResponseValue(requestId, proxyFrameSequence),
  )
  if (parsed === null) {
    throw new Error('Expected a valid player seek response.')
  }
  return parsed
}

function reduce(...actions: readonly PlayerAction[]): PlayerState {
  return actions.reduce<PlayerState>(playerStateReducer, initialPlayerState)
}
