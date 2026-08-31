import { describe, expect, it } from 'vitest'

import {
  createPlayerReadRequest,
  createPlayerSeekRequest,
  findProxyFrameAtDerivedMediaSeconds,
  parsePlayerReadResponse,
  parsePlayerSeekResponse,
  positionMatchesFrame,
  proxyTimeToDerivedMediaSeconds,
  rationalToDerivedSeconds,
} from './playerProtocol'
import {
  createPlayerReadResponseValue,
  createPlayerSeekResponseValue,
  playerEvidenceDigest,
  playerJobId,
  playerProjectId,
  playerRequestId,
} from './playerTestFixture'

describe('player bridge protocol', () => {
  it('creates bounded closed read and evidence-bound seek requests', () => {
    const read = createPlayerReadRequest(playerRequestId, playerJobId)
    const seek = createPlayerSeekRequest(
      playerRequestId,
      playerJobId,
      playerEvidenceDigest,
      2,
    )

    expect(read).toEqual({
      version: 1,
      requestId: playerRequestId,
      method: 'player.read',
      params: { jobId: playerJobId },
    })
    expect(seek).toEqual({
      version: 1,
      requestId: playerRequestId,
      method: 'player.seek',
      params: {
        jobId: playerJobId,
        digest: playerEvidenceDigest,
        proxyFrameSequence: 2,
      },
    })
    expect(new TextEncoder().encode(JSON.stringify(seek)).byteLength).toBeLessThanOrEqual(256)
    const maximumSeek = createPlayerSeekRequest(
      playerRequestId,
      playerJobId,
      playerEvidenceDigest,
      2_147_483_647,
    )
    expect(
      new TextEncoder().encode(JSON.stringify(maximumSeek)).byteLength,
    ).toBeLessThanOrEqual(256)
    expect(Object.isFrozen(read)).toBe(true)
    expect(Object.isFrozen(seek.params)).toBe(true)
  })

  it.each([
    ['', playerJobId, playerEvidenceDigest, 0],
    [playerRequestId.toUpperCase(), playerJobId, playerEvidenceDigest, 0],
    [`${playerRequestId}\n`, playerJobId, playerEvidenceDigest, 0],
    [playerRequestId, '00000000-0000-0000-0000-000000000000', playerEvidenceDigest, 0],
    [playerRequestId, playerJobId.toUpperCase(), playerEvidenceDigest, 0],
    [playerRequestId, playerJobId, playerEvidenceDigest.toUpperCase(), 0],
    [playerRequestId, playerJobId, `${playerEvidenceDigest}\n`, 0],
    [playerRequestId, playerJobId, playerEvidenceDigest, -1],
    [playerRequestId, playerJobId, playerEvidenceDigest, 0.5],
    [playerRequestId, playerJobId, playerEvidenceDigest, 2_147_483_648],
  ])(
    'rejects noncanonical read or seek input %#',
    (requestId, jobId, digest, sequence) => {
      expect(() =>
        createPlayerSeekRequest(requestId, jobId, digest, sequence),
      ).toThrow()
    },
  )

  it('parses and freezes negative-source-start VFR evidence without float authority', () => {
    const parsed = parsePlayerReadResponse(createPlayerReadResponseValue())

    expect(parsed?.result.status).toBe('available')
    if (parsed?.result.status !== 'available') {
      throw new Error('Expected available player evidence.')
    }
    expect(parsed.result.snapshot.projectId).toBe(playerProjectId)
    expect(parsed.result.snapshot.sourcePresentationStart).toEqual({
      value: '-1',
      rate: '10',
    })
    expect(parsed.result.snapshot.frames.map((frame) => frame.proxyPresentationTime)).toEqual([
      { value: '0', rate: '1' },
      { value: '1001', rate: '30000' },
      { value: '1', rate: '12' },
    ])
    expect(parsed.result.snapshot.frames[2]?.outputPresentationTime).toBeNull()
    expect(Object.isFrozen(parsed.result.snapshot)).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot.frames)).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot.frames[0]?.waveformChannels)).toBe(true)
  })

  it('preserves a negative observed proxy start without normalizing it', () => {
    const value = createNegativeProxyStartValue()

    const parsed = parsePlayerReadResponse(value)

    expect(parsed?.result.status).toBe('available')
    if (parsed?.result.status === 'available') {
      expect(parsed.result.snapshot.proxy.presentationStart).toEqual({
        value: '-1',
        rate: '20',
      })
      expect(parsed.result.snapshot.frames[0]?.proxyPresentationTime).toEqual({
        value: '-1',
        rate: '20',
      })
      expect(
        proxyTimeToDerivedMediaSeconds(
          parsed.result.snapshot,
          parsed.result.snapshot.frames[0]!.proxyPresentationTime,
        ),
      ).toBe(0)
      expect(findProxyFrameAtDerivedMediaSeconds(parsed.result.snapshot, 0)).toBe(0)
      expect(
        findProxyFrameAtDerivedMediaSeconds(
          parsed.result.snapshot,
          1001 / 30000,
        ),
      ).toBe(1)
      expect(
        findProxyFrameAtDerivedMediaSeconds(parsed.result.snapshot, 1 / 12),
      ).toBe(2)
      expect(
        findProxyFrameAtDerivedMediaSeconds(parsed.result.snapshot, 7 / 60),
      ).toBeNull()
    }
  })

  it('rejects unknown fields and every broken cross-binding', () => {
    const mutations: Array<(value: PlayerValue) => void> = [
      (value) => {
        value.extra = true
      },
      (value) => {
        value.result.snapshot.proxy.extra = true
      },
      (value) => {
        value.result.snapshot.proxy.mediaReference = 'https://evil.example/proxy.mp4'
      },
      (value) => {
        value.result.snapshot.proxy.mediaReference =
          value.result.snapshot.proxy.mediaReference.replace(
            playerProjectId,
            'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
          )
      },
      (value) => {
        value.result.snapshot.proxy.contentDigest = 'f'.repeat(64)
      },
      (value) => {
        value.result.snapshot.proxy.contentDigest = `${'f'.repeat(64)}\n`
      },
      (value) => {
        value.result.snapshot.proxy.mediaReference += '\n'
      },
      (value) => {
        value.result.snapshot.sprite.imageDigest = 'f'.repeat(64)
      },
      (value) => {
        value.result.snapshot.frames[1]!.proxyFrameSequence = 4
      },
      (value) => {
        value.result.snapshot.frames[1]!.proxyPresentationTime = {
          value: '0',
          rate: '1',
        }
      },
      (value) => {
        value.result.snapshot.frames[1]!.proxyDuration = {
          value: '0',
          rate: '1',
        }
      },
      (value) => {
        value.result.snapshot.frames[0]!.proxyDuration = {
          value: '1',
          rate: '100',
        }
      },
      (value) => {
        value.result.snapshot.proxy.presentationEnd = {
          value: '8',
          rate: '60',
        }
      },
      (value) => {
        value.result.snapshot.frames[0]!.sourcePresentationTime = {
          value: '-2',
          rate: '10',
        }
      },
      (value) => {
        value.result.snapshot.frames[0]!.proxyPresentationTime = {
          value: '9223372036854775808',
          rate: '1',
        }
      },
      (value) => {
        value.result.snapshot.frames[0]!.proxyPresentationTime = {
          value: '0\n',
          rate: '1',
        }
      },
      (value) => {
        value.result.snapshot.sourcePresentationStart = {
          value: '-2',
          rate: '20',
        }
      },
      (value) => {
        value.result.snapshot.frames[1]!.sourceFrameSequence = 0
      },
      (value) => {
        value.result.snapshot.frames[0]!.waveformChannels[0]!.channelIndex = 1
      },
      (value) => {
        value.result.snapshot.waveform.sampleRate = 0
      },
      (value) => {
        value.result.snapshot.waveform.channelLayout = ' mono '
      },
      (value) => {
        value.result.snapshot.frames[2]!.spriteCell!.column = 2
      },
      (value) => {
        value.result.snapshot.frames[2]!.spriteCell!.ordinal = 2
      },
      (value) => {
        value.result.snapshot.sprite.rows = 2
      },
    ]

    for (const mutate of mutations) {
      const value = clonePlayerValue()
      mutate(value)
      expect(parsePlayerReadResponse(value)).toBeNull()
    }
  })

  it('requires status and nullable payload to agree exactly', () => {
    const unavailable = {
      version: 1,
      requestId: playerRequestId,
      method: 'player.read',
      result: { status: 'unavailable', snapshot: null },
    }
    expect(parsePlayerReadResponse(unavailable)).toEqual(unavailable)
    const forbidden = {
      ...unavailable,
      result: { status: 'forbidden', snapshot: null },
    }
    expect(parsePlayerReadResponse(forbidden)).toEqual(forbidden)
    expect(
      parsePlayerReadResponse({
        ...forbidden,
        result: {
          status: 'forbidden',
          snapshot: null,
          actorSid: 'S-1-5-21-private',
        },
      }),
    ).toBeNull()
    expect(
      parsePlayerSeekResponse({
        version: 1,
        requestId: playerRequestId,
        method: 'player.seek',
        result: { status: 'forbidden', position: null },
      }),
    ).not.toBeNull()
    expect(
      parsePlayerReadResponse({
        ...unavailable,
        result: { status: 'available', snapshot: null },
      }),
    ).toBeNull()
    expect(
      parsePlayerReadResponse({
        ...unavailable,
        result: { status: 'private-error', snapshot: null },
      }),
    ).toBeNull()
  })

  it('parses exact seek positions and rejects changed or extended envelopes', () => {
    const parsed = parsePlayerSeekResponse(createPlayerSeekResponseValue())
    expect(parsed?.result.status).toBe('available')
    if (parsed?.result.status !== 'available') {
      throw new Error('Expected an available seek response.')
    }
    const read = parsePlayerReadResponse(createPlayerReadResponseValue())
    if (read?.result.status !== 'available') {
      throw new Error('Expected available player evidence.')
    }
    expect(positionMatchesFrame(parsed.result.position, read.result.snapshot.frames[2]!)).toBe(true)

    const extended = createPlayerSeekResponseValue()
    ;(extended.result as Record<string, unknown>).extra = true
    expect(parsePlayerSeekResponse(extended)).toBeNull()
    const fractional = createPlayerSeekResponseValue()
    const position = (fractional.result as { position: Record<string, unknown> }).position
    position.proxyFrameSequence = 0.5
    expect(parsePlayerSeekResponse(fractional)).toBeNull()
  })

  it('maps browser seconds only onto canonical half-open frame evidence', () => {
    const parsed = parsePlayerReadResponse(createPlayerReadResponseValue())
    if (parsed?.result.status !== 'available') {
      throw new Error('Expected available player evidence.')
    }
    const snapshot = parsed.result.snapshot

    expect(findProxyFrameAtDerivedMediaSeconds(snapshot, 0)).toBe(0)
    expect(findProxyFrameAtDerivedMediaSeconds(snapshot, 1001 / 30000)).toBe(1)
    expect(findProxyFrameAtDerivedMediaSeconds(snapshot, 1 / 12)).toBe(2)
    expect(findProxyFrameAtDerivedMediaSeconds(snapshot, 7 / 60)).toBeNull()
    expect(findProxyFrameAtDerivedMediaSeconds(snapshot, Number.NaN)).toBeNull()
    expect(rationalToDerivedSeconds(snapshot.frames[1]!.proxyPresentationTime)).toBe(
      1001 / 30000,
    )
  })
})

type PlayerValue = ReturnType<typeof clonePlayerValue>

function clonePlayerValue() {
  return JSON.parse(JSON.stringify(createPlayerReadResponseValue())) as {
    version: number
    requestId: string
    method: string
    extra?: boolean
    result: {
      status: string
      snapshot: {
        sourcePresentationStart: { value: string; rate: string }
        proxy: {
          mediaReference: string
          contentDigest: string
          presentationStart: { value: string; rate: string }
          presentationEnd: { value: string; rate: string }
          extra?: boolean
        }
        waveform: {
          sampleRate: number
          channelLayout: string | null
        }
        sprite: {
          imageDigest: string
          rows: number
        }
        frames: Array<{
          proxyFrameSequence: number
          proxyPresentationTime: { value: string; rate: string }
          sourceFrameSequence: number
          sourcePresentationTime: { value: string; rate: string }
          proxyDuration: { value: string; rate: string }
          waveformChannels: Array<{ channelIndex: number }>
          spriteCell: { ordinal: number; column: number } | null
        }>
      }
    }
  }
}

function createNegativeProxyStartValue(): PlayerValue {
  const value = clonePlayerValue()
  value.result.snapshot.proxy.presentationStart = { value: '-1', rate: '20' }
  value.result.snapshot.frames[0]!.proxyPresentationTime = {
    value: '-1',
    rate: '20',
  }
  value.result.snapshot.frames[1]!.proxyPresentationTime = {
    value: '-499',
    rate: '30000',
  }
  value.result.snapshot.frames[2]!.proxyPresentationTime = {
    value: '1',
    rate: '30',
  }
  value.result.snapshot.proxy.presentationEnd = { value: '1', rate: '15' }
  return value
}
