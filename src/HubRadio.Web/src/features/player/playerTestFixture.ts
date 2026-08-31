export const playerRequestId = '00112233-4455-6677-8899-aabbccddeeff'
export const otherPlayerRequestId = '11223344-5566-4788-99aa-bbccddeeff00'
export const playerJobId = '33333333-3333-4333-8333-3333333333ab'
export const playerProjectId = '22222222-2222-4222-8222-222222222222'
export const playerSourceAssetId = '11111111-1111-4111-8111-111111111111'
export const playerEvidenceDigest = 'e'.repeat(64)
export const playerProxyDigest = 'c'.repeat(64)
export const playerProxyPtsDigest = 'd'.repeat(64)
export const playerSpriteDigest = '9'.repeat(64)

const proxyArtifactId = '44444444-4444-4444-8444-444444444444'
const spriteArtifactId = '66666666-6666-4666-8666-666666666666'

export function createPlayerReadResponseValue(): Record<string, unknown> {
  return {
    version: 1,
    requestId: playerRequestId,
    method: 'player.read',
    result: {
      status: 'available',
      snapshot: {
        evidenceDigest: playerEvidenceDigest,
        sourceAssetId: playerSourceAssetId,
        projectId: playerProjectId,
        sourcePresentationStart: { value: '-1', rate: '10' },
        sourcePresentationEnd: { value: '3', rate: '20' },
        proxy: {
          mediaReference:
            `https://player-media.appassets.example/media/v1/${playerProjectId}` +
            `/proxy/${proxyArtifactId}/${playerProxyDigest}`,
          contentDigest: playerProxyDigest,
          ptsIndexDigest: playerProxyPtsDigest,
          presentationStart: { value: '0', rate: '1' },
          presentationEnd: { value: '7', rate: '60' },
        },
        waveform: {
          sampleRate: 8000,
          channelCount: 1,
          channelLayout: 'mono',
        },
        sprite: {
          imageReference:
            `https://player-media.appassets.example/media/v1/${playerProjectId}` +
            `/sprite/${spriteArtifactId}/${playerSpriteDigest}`,
          imageDigest: playerSpriteDigest,
          cellWidth: 160,
          cellHeight: 90,
          columns: 2,
          rows: 1,
        },
        frames: [
          {
            proxyFrameSequence: 0,
            proxyPresentationTime: { value: '0', rate: '1' },
            sourceFrameSequence: 0,
            sourcePresentationTime: { value: '-1', rate: '10' },
            outputPresentationTime: null,
            proxyDuration: { value: '1001', rate: '30000' },
            waveformChannels: [
              { channelIndex: 0, minimumSample: -100, maximumSample: 100 },
            ],
            spriteCell: { ordinal: 0, column: 0, row: 0 },
          },
          {
            proxyFrameSequence: 1,
            proxyPresentationTime: { value: '1001', rate: '30000' },
            sourceFrameSequence: 1,
            sourcePresentationTime: { value: '0', rate: '1' },
            outputPresentationTime: null,
            proxyDuration: { value: '1499', rate: '30000' },
            waveformChannels: [
              { channelIndex: 0, minimumSample: -101, maximumSample: 101 },
            ],
            spriteCell: null,
          },
          {
            proxyFrameSequence: 2,
            proxyPresentationTime: { value: '1', rate: '12' },
            sourceFrameSequence: 2,
            sourcePresentationTime: { value: '1', rate: '20' },
            outputPresentationTime: null,
            proxyDuration: { value: '1', rate: '30' },
            waveformChannels: [
              { channelIndex: 0, minimumSample: -102, maximumSample: 102 },
            ],
            spriteCell: { ordinal: 1, column: 1, row: 0 },
          },
        ],
      },
    },
  }
}

export function createPlayerSeekResponseValue(
  requestId = playerRequestId,
  proxyFrameSequence = 2,
): Record<string, unknown> {
  const read = createPlayerReadResponseValue()
  const snapshot = (read.result as { snapshot: { frames: unknown[] } }).snapshot
  const frame = snapshot.frames[proxyFrameSequence] as Record<string, unknown>
  return {
    version: 1,
    requestId,
    method: 'player.seek',
    result: {
      status: 'available',
      position: {
        proxyFrameSequence: frame.proxyFrameSequence,
        proxyPresentationTime: frame.proxyPresentationTime,
        sourceFrameSequence: frame.sourceFrameSequence,
        sourcePresentationTime: frame.sourcePresentationTime,
        outputPresentationTime: frame.outputPresentationTime,
      },
    },
  }
}
