import {
  BRIDGE_MAX_PLAYER_RESPONSE_UTF8_BYTES,
  BRIDGE_PROTOCOL_VERSION,
} from '../../bridge/protocol'

export const PLAYER_READ_METHOD = 'player.read' as const
export const PLAYER_SEEK_METHOD = 'player.seek' as const

const uuidPattern =
  /^(?!00000000-0000-0000-0000-000000000000(?![\s\S]))[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![\s\S])/
const digestPattern = /^[0-9a-f]{64}(?![\s\S])/
const signedIntegerPattern = /^(?:0|-?[1-9][0-9]{0,18})(?![\s\S])/
const positiveIntegerPattern = /^[1-9][0-9]{0,18}(?![\s\S])/
const mediaReferencePattern = new RegExp(
  '^https://player-media\\.appassets\\.example/media/v1/' +
    '((?!00000000-0000-0000-0000-000000000000/)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/' +
    '(proxy|sprite)/' +
    '((?!00000000-0000-0000-0000-000000000000/)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/' +
    '([0-9a-f]{64})(?![\\s\\S])',
)
const int64Minimum = -9_223_372_036_854_775_808n
const int64Maximum = 9_223_372_036_854_775_807n
const maximumRequestUtf8Bytes = 256

export interface RationalTime {
  readonly value: string
  readonly rate: string
}

export interface PlayerPosition {
  readonly proxyFrameSequence: number
  readonly proxyPresentationTime: RationalTime
  readonly sourceFrameSequence: number
  readonly sourcePresentationTime: RationalTime
  readonly outputPresentationTime: RationalTime | null
}

export interface PlayerWaveformChannel {
  readonly channelIndex: number
  readonly minimumSample: number
  readonly maximumSample: number
}

export interface PlayerSpriteCell {
  readonly ordinal: number
  readonly column: number
  readonly row: number
}

export interface PlayerFrame extends PlayerPosition {
  readonly proxyDuration: RationalTime
  readonly waveformChannels: readonly PlayerWaveformChannel[]
  readonly spriteCell: PlayerSpriteCell | null
}

export interface PlayerSnapshot {
  readonly evidenceDigest: string
  readonly sourceAssetId: string
  readonly projectId: string
  readonly sourcePresentationStart: RationalTime
  readonly sourcePresentationEnd: RationalTime
  readonly proxy: Readonly<{
    mediaReference: string
    contentDigest: string
    ptsIndexDigest: string
    presentationStart: RationalTime
    presentationEnd: RationalTime
  }>
  readonly waveform: Readonly<{
    sampleRate: number
    channelCount: number
    channelLayout: string | null
  }>
  readonly sprite: Readonly<{
    imageReference: string
    imageDigest: string
    cellWidth: number
    cellHeight: number
    columns: number
    rows: number
  }>
  readonly frames: readonly PlayerFrame[]
}

export interface PlayerReadRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof PLAYER_READ_METHOD
  readonly params: Readonly<{ jobId: string }>
}

export interface PlayerSeekRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof PLAYER_SEEK_METHOD
  readonly params: Readonly<{
    jobId: string
    digest: string
    proxyFrameSequence: number
  }>
}

export type PlayerReadStatus =
  | 'available'
  | 'forbidden'
  | 'unavailable'
  | 'stale'
  | 'mismatched'
export type PlayerSeekStatus = PlayerReadStatus | 'invalidTime'

export type PlayerReadResponse =
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof PLAYER_READ_METHOD
      result: Readonly<{ status: 'available'; snapshot: PlayerSnapshot }>
    }>
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof PLAYER_READ_METHOD
      result: Readonly<{
        status: Exclude<PlayerReadStatus, 'available'>
        snapshot: null
      }>
    }>

export type PlayerSeekResponse =
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof PLAYER_SEEK_METHOD
      result: Readonly<{ status: 'available'; position: PlayerPosition }>
    }>
  | Readonly<{
      version: typeof BRIDGE_PROTOCOL_VERSION
      requestId: string
      method: typeof PLAYER_SEEK_METHOD
      result: Readonly<{
        status: Exclude<PlayerSeekStatus, 'available'>
        position: null
      }>
    }>

export function isCanonicalEntityId(value: string): boolean {
  return uuidPattern.test(value)
}

export function createPlayerReadRequest(
  requestId: string,
  jobId: string,
): PlayerReadRequest {
  assertRequestId(requestId)
  assertEntityId(jobId)
  return ensureBoundedRequest(
    Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId,
      method: PLAYER_READ_METHOD,
      params: Object.freeze({ jobId }),
    }),
  )
}

export function createPlayerSeekRequest(
  requestId: string,
  jobId: string,
  evidenceDigest: string,
  proxyFrameSequence: number,
): PlayerSeekRequest {
  assertRequestId(requestId)
  assertEntityId(jobId)
  if (!digestPattern.test(evidenceDigest)) {
    throw new Error('A player evidence digest must be canonical SHA-256.')
  }
  if (!isNonnegativeInt32(proxyFrameSequence)) {
    throw new Error('A proxy frame sequence must be a nonnegative Int32.')
  }

  return ensureBoundedRequest(
    Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId,
      method: PLAYER_SEEK_METHOD,
      params: Object.freeze({
        jobId,
        digest: evidenceDigest,
        proxyFrameSequence,
      }),
    }),
  )
}

export function parsePlayerReadResponse(value: unknown): PlayerReadResponse | null {
  if (
    !isWithinUtf8Limit(value, BRIDGE_MAX_PLAYER_RESPONSE_UTF8_BYTES) ||
    !hasExactKeys(value, ['version', 'requestId', 'method', 'result'])
  ) {
    return null
  }
  if (
    value.version !== BRIDGE_PROTOCOL_VERSION ||
    !isCanonicalRequestId(value.requestId) ||
    value.method !== PLAYER_READ_METHOD ||
    !hasExactKeys(value.result, ['status', 'snapshot'])
  ) {
    return null
  }

  const result = value.result
  if (result.status === 'available') {
    const snapshot = parseSnapshot(result.snapshot)
    if (snapshot === null) {
      return null
    }
    return Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId: value.requestId,
      method: PLAYER_READ_METHOD,
      result: Object.freeze({ status: 'available', snapshot }),
    })
  }

  if (
    result.snapshot !== null ||
    (result.status !== 'forbidden' &&
      result.status !== 'unavailable' &&
      result.status !== 'stale' &&
      result.status !== 'mismatched')
  ) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method: PLAYER_READ_METHOD,
    result: Object.freeze({ status: result.status, snapshot: null }),
  })
}

export function parsePlayerSeekResponse(value: unknown): PlayerSeekResponse | null {
  if (
    !isWithinUtf8Limit(value, BRIDGE_MAX_PLAYER_RESPONSE_UTF8_BYTES) ||
    !hasExactKeys(value, ['version', 'requestId', 'method', 'result'])
  ) {
    return null
  }
  if (
    value.version !== BRIDGE_PROTOCOL_VERSION ||
    !isCanonicalRequestId(value.requestId) ||
    value.method !== PLAYER_SEEK_METHOD ||
    !hasExactKeys(value.result, ['status', 'position'])
  ) {
    return null
  }

  const result = value.result
  if (result.status === 'available') {
    const position = parsePosition(result.position)
    if (position === null) {
      return null
    }
    return Object.freeze({
      version: BRIDGE_PROTOCOL_VERSION,
      requestId: value.requestId,
      method: PLAYER_SEEK_METHOD,
      result: Object.freeze({ status: 'available', position }),
    })
  }

  if (
    result.position !== null ||
    (result.status !== 'forbidden' &&
      result.status !== 'unavailable' &&
      result.status !== 'stale' &&
      result.status !== 'mismatched' &&
      result.status !== 'invalidTime')
  ) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method: PLAYER_SEEK_METHOD,
    result: Object.freeze({ status: result.status, position: null }),
  })
}

export function rationalToDerivedSeconds(value: RationalTime): number {
  return Number(BigInt(value.value)) / Number(BigInt(value.rate))
}

export function proxyTimeToDerivedMediaSeconds(
  snapshot: PlayerSnapshot,
  proxyTime: RationalTime,
): number {
  const proxyNumerator = BigInt(proxyTime.value)
  const proxyRate = BigInt(proxyTime.rate)
  const startNumerator = BigInt(snapshot.proxy.presentationStart.value)
  const startRate = BigInt(snapshot.proxy.presentationStart.rate)
  const offsetNumerator =
    proxyNumerator * startRate - startNumerator * proxyRate
  return Number(offsetNumerator) / Number(proxyRate * startRate)
}

export function formatRationalTime(value: RationalTime | null): string {
  return value === null ? '—' : `${value.value}/${value.rate}`
}

export function findProxyFrameAtDerivedMediaSeconds(
  snapshot: PlayerSnapshot,
  mediaSeconds: number,
): number | null {
  if (!Number.isFinite(mediaSeconds)) {
    return null
  }

  for (let index = snapshot.frames.length - 1; index >= 0; index -= 1) {
    const frame = snapshot.frames[index]!
    const nextFrame = snapshot.frames[index + 1]
    const start = proxyTimeToDerivedMediaSeconds(
      snapshot,
      frame.proxyPresentationTime,
    )
    const end = proxyTimeToDerivedMediaSeconds(
      snapshot,
      nextFrame?.proxyPresentationTime ?? snapshot.proxy.presentationEnd,
    )
    if (mediaSeconds >= start && mediaSeconds < end) {
      return frame.proxyFrameSequence
    }
  }

  return null
}

export const findProxyFrameAtDerivedSeconds =
  findProxyFrameAtDerivedMediaSeconds

export function positionMatchesFrame(
  position: PlayerPosition,
  frame: PlayerFrame,
): boolean {
  return (
    position.proxyFrameSequence === frame.proxyFrameSequence &&
    rationalsEqual(position.proxyPresentationTime, frame.proxyPresentationTime) &&
    position.sourceFrameSequence === frame.sourceFrameSequence &&
    rationalsEqual(position.sourcePresentationTime, frame.sourcePresentationTime) &&
    nullableRationalsEqual(
      position.outputPresentationTime,
      frame.outputPresentationTime,
    )
  )
}

function parseSnapshot(value: unknown): PlayerSnapshot | null {
  if (
    !hasExactKeys(value, [
      'evidenceDigest',
      'sourceAssetId',
      'projectId',
      'sourcePresentationStart',
      'sourcePresentationEnd',
      'proxy',
      'waveform',
      'sprite',
      'frames',
    ]) ||
    typeof value.evidenceDigest !== 'string' ||
    !digestPattern.test(value.evidenceDigest) ||
    typeof value.sourceAssetId !== 'string' ||
    !uuidPattern.test(value.sourceAssetId) ||
    typeof value.projectId !== 'string' ||
    !uuidPattern.test(value.projectId)
  ) {
    return null
  }

  const sourceStart = parseRational(value.sourcePresentationStart)
  const sourceEnd = parseRational(value.sourcePresentationEnd)
  const proxy = parseProxy(value.proxy)
  const waveform = parseWaveform(value.waveform)
  const sprite = parseSprite(value.sprite)
  if (
    sourceStart === null ||
    sourceEnd === null ||
    compareRationals(sourceStart, sourceEnd) >= 0 ||
    proxy === null ||
    waveform === null ||
    sprite === null ||
    !Array.isArray(value.frames) ||
    value.frames.length === 0
  ) {
    return null
  }

  const proxyReference = parseMediaReference(proxy.mediaReference)
  const spriteReference = parseMediaReference(sprite.imageReference)
  if (
    proxyReference === null ||
    proxyReference.kind !== 'proxy' ||
    proxyReference.projectId !== value.projectId ||
    proxyReference.digest !== proxy.contentDigest ||
    spriteReference === null ||
    spriteReference.kind !== 'sprite' ||
    spriteReference.projectId !== value.projectId ||
    spriteReference.digest !== sprite.imageDigest
  ) {
    return null
  }

  const frames: PlayerFrame[] = []
  let spriteCellCount = 0
  for (let index = 0; index < value.frames.length; index += 1) {
    const frame = parseFrame(value.frames[index])
    const previous = frames[index - 1]
    if (
      frame === null ||
      frame.proxyFrameSequence !== index ||
      compareRationals(frame.proxyDuration, zeroRational) <= 0 ||
      frame.waveformChannels.length !== waveform.channelCount ||
      !hasCanonicalChannels(frame.waveformChannels) ||
      compareRationals(frame.sourcePresentationTime, sourceStart) < 0 ||
      compareRationals(frame.sourcePresentationTime, sourceEnd) >= 0 ||
      (index === 0
        ? !rationalsEqual(frame.proxyPresentationTime, proxy.presentationStart)
        : previous === undefined ||
          !sumEquals(
            previous.proxyPresentationTime,
            previous.proxyDuration,
            frame.proxyPresentationTime,
          ) ||
          frame.sourceFrameSequence < previous.sourceFrameSequence ||
          compareRationals(
            frame.sourcePresentationTime,
            previous.sourcePresentationTime,
          ) < 0 ||
          (frame.sourceFrameSequence === previous.sourceFrameSequence
            ? !rationalsEqual(
                frame.sourcePresentationTime,
                previous.sourcePresentationTime,
              )
            : compareRationals(
                frame.sourcePresentationTime,
                previous.sourcePresentationTime,
              ) <= 0))
    ) {
      return null
    }

    if (frame.spriteCell !== null) {
      if (
        frame.spriteCell.ordinal !== spriteCellCount ||
        frame.spriteCell.column !== frame.spriteCell.ordinal % sprite.columns ||
        frame.spriteCell.row !==
          Math.floor(frame.spriteCell.ordinal / sprite.columns) ||
        frame.spriteCell.column >= sprite.columns ||
        frame.spriteCell.row >= sprite.rows
      ) {
        return null
      }
      spriteCellCount += 1
    }
    frames.push(frame)
  }

  const finalFrame = frames[frames.length - 1]!
  if (
    !sumEquals(
      finalFrame.proxyPresentationTime,
      finalFrame.proxyDuration,
      proxy.presentationEnd,
    ) ||
    spriteCellCount === 0 ||
    sprite.rows !== Math.ceil(spriteCellCount / sprite.columns)
  ) {
    return null
  }

  return Object.freeze({
    evidenceDigest: value.evidenceDigest,
    sourceAssetId: value.sourceAssetId,
    projectId: value.projectId,
    sourcePresentationStart: sourceStart,
    sourcePresentationEnd: sourceEnd,
    proxy,
    waveform,
    sprite,
    frames: Object.freeze(frames),
  })
}

function parseProxy(value: unknown): PlayerSnapshot['proxy'] | null {
  if (
    !hasExactKeys(value, [
      'mediaReference',
      'contentDigest',
      'ptsIndexDigest',
      'presentationStart',
      'presentationEnd',
    ]) ||
    typeof value.mediaReference !== 'string' ||
    typeof value.contentDigest !== 'string' ||
    !digestPattern.test(value.contentDigest) ||
    typeof value.ptsIndexDigest !== 'string' ||
    !digestPattern.test(value.ptsIndexDigest)
  ) {
    return null
  }
  const presentationStart = parseRational(value.presentationStart)
  const presentationEnd = parseRational(value.presentationEnd)
  if (
    presentationStart === null ||
    presentationEnd === null ||
    compareRationals(presentationStart, presentationEnd) >= 0
  ) {
    return null
  }
  return Object.freeze({
    mediaReference: value.mediaReference,
    contentDigest: value.contentDigest,
    ptsIndexDigest: value.ptsIndexDigest,
    presentationStart,
    presentationEnd,
  })
}

function parseWaveform(value: unknown): PlayerSnapshot['waveform'] | null {
  if (
    !hasExactKeys(value, ['sampleRate', 'channelCount', 'channelLayout']) ||
    !isNonnegativeInt32(value.sampleRate) ||
    !isNonnegativeInt32(value.channelCount)
  ) {
    return null
  }
  if (value.channelCount === 0) {
    if (value.sampleRate !== 0 || value.channelLayout !== null) {
      return null
    }
    return Object.freeze({ sampleRate: 0, channelCount: 0, channelLayout: null })
  }
  if (
    value.sampleRate === 0 ||
    typeof value.channelLayout !== 'string' ||
    value.channelLayout.length === 0 ||
    value.channelLayout.trim() !== value.channelLayout
  ) {
    return null
  }
  return Object.freeze({
    sampleRate: value.sampleRate,
    channelCount: value.channelCount,
    channelLayout: value.channelLayout,
  })
}

function parseSprite(value: unknown): PlayerSnapshot['sprite'] | null {
  if (
    !hasExactKeys(value, [
      'imageReference',
      'imageDigest',
      'cellWidth',
      'cellHeight',
      'columns',
      'rows',
    ]) ||
    typeof value.imageReference !== 'string' ||
    typeof value.imageDigest !== 'string' ||
    !digestPattern.test(value.imageDigest) ||
    !isPositiveInt32(value.cellWidth) ||
    !isPositiveInt32(value.cellHeight) ||
    !isPositiveInt32(value.columns) ||
    !isPositiveInt32(value.rows)
  ) {
    return null
  }
  return Object.freeze({
    imageReference: value.imageReference,
    imageDigest: value.imageDigest,
    cellWidth: value.cellWidth,
    cellHeight: value.cellHeight,
    columns: value.columns,
    rows: value.rows,
  })
}

function parseFrame(value: unknown): PlayerFrame | null {
  if (
    !hasExactKeys(value, [
      'proxyFrameSequence',
      'proxyPresentationTime',
      'sourceFrameSequence',
      'sourcePresentationTime',
      'outputPresentationTime',
      'proxyDuration',
      'waveformChannels',
      'spriteCell',
    ]) ||
    !Array.isArray(value.waveformChannels)
  ) {
    return null
  }
  const position = parsePositionProperties(value)
  const proxyDuration = parseRational(value.proxyDuration)
  const channels = value.waveformChannels.map(parseWaveformChannel)
  const spriteCell = value.spriteCell === null ? null : parseSpriteCell(value.spriteCell)
  if (
    position === null ||
    proxyDuration === null ||
    channels.some((channel) => channel === null) ||
    (value.spriteCell !== null && spriteCell === null)
  ) {
    return null
  }
  return Object.freeze({
    ...position,
    proxyDuration,
    waveformChannels: Object.freeze(channels as PlayerWaveformChannel[]),
    spriteCell,
  })
}

function parsePosition(value: unknown): PlayerPosition | null {
  if (
    !hasExactKeys(value, [
      'proxyFrameSequence',
      'proxyPresentationTime',
      'sourceFrameSequence',
      'sourcePresentationTime',
      'outputPresentationTime',
    ])
  ) {
    return null
  }
  return parsePositionProperties(value)
}

function parsePositionProperties(value: Record<string, unknown>): PlayerPosition | null {
  if (
    !isNonnegativeInt32(value.proxyFrameSequence) ||
    !isNonnegativeInt32(value.sourceFrameSequence)
  ) {
    return null
  }
  const proxyPresentationTime = parseRational(value.proxyPresentationTime)
  const sourcePresentationTime = parseRational(value.sourcePresentationTime)
  const outputPresentationTime =
    value.outputPresentationTime === null
      ? null
      : parseRational(value.outputPresentationTime)
  if (
    proxyPresentationTime === null ||
    sourcePresentationTime === null ||
    (value.outputPresentationTime !== null && outputPresentationTime === null)
  ) {
    return null
  }
  return Object.freeze({
    proxyFrameSequence: value.proxyFrameSequence,
    proxyPresentationTime,
    sourceFrameSequence: value.sourceFrameSequence,
    sourcePresentationTime,
    outputPresentationTime,
  })
}

function parseWaveformChannel(value: unknown): PlayerWaveformChannel | null {
  if (
    !hasExactKeys(value, ['channelIndex', 'minimumSample', 'maximumSample']) ||
    !isNonnegativeInt32(value.channelIndex) ||
    !isInt16(value.minimumSample) ||
    !isInt16(value.maximumSample) ||
    value.minimumSample > value.maximumSample
  ) {
    return null
  }
  return Object.freeze({
    channelIndex: value.channelIndex,
    minimumSample: value.minimumSample,
    maximumSample: value.maximumSample,
  })
}

function parseSpriteCell(value: unknown): PlayerSpriteCell | null {
  if (
    !hasExactKeys(value, ['ordinal', 'column', 'row']) ||
    !isNonnegativeInt32(value.ordinal) ||
    !isNonnegativeInt32(value.column) ||
    !isNonnegativeInt32(value.row)
  ) {
    return null
  }
  return Object.freeze({
    ordinal: value.ordinal,
    column: value.column,
    row: value.row,
  })
}

function parseRational(value: unknown): RationalTime | null {
  if (
    !hasExactKeys(value, ['value', 'rate']) ||
    typeof value.value !== 'string' ||
    typeof value.rate !== 'string' ||
    !signedIntegerPattern.test(value.value) ||
    !positiveIntegerPattern.test(value.rate)
  ) {
    return null
  }
  const numerator = BigInt(value.value)
  const denominator = BigInt(value.rate)
  if (
    numerator < int64Minimum ||
    numerator > int64Maximum ||
    denominator > int64Maximum ||
    greatestCommonDivisor(
      numerator < 0n ? -numerator : numerator,
      denominator,
    ) !== 1n
  ) {
    return null
  }
  return Object.freeze({ value: value.value, rate: value.rate })
}

function greatestCommonDivisor(left: bigint, right: bigint): bigint {
  let current = left
  let remainder = right
  while (remainder !== 0n) {
    const next = current % remainder
    current = remainder
    remainder = next
  }
  return current
}

function parseMediaReference(value: string): Readonly<{
  projectId: string
  kind: 'proxy' | 'sprite'
  artifactId: string
  digest: string
}> | null {
  const match = mediaReferencePattern.exec(value)
  if (match === null) {
    return null
  }
  const [, projectId, kind, artifactId, digest] = match
  if (
    projectId === undefined ||
    (kind !== 'proxy' && kind !== 'sprite') ||
    artifactId === undefined ||
    digest === undefined ||
    !uuidPattern.test(projectId) ||
    !uuidPattern.test(artifactId)
  ) {
    return null
  }
  return Object.freeze({ projectId, kind, artifactId, digest })
}

function hasCanonicalChannels(
  channels: readonly PlayerWaveformChannel[],
): boolean {
  return channels.every((channel, index) => channel.channelIndex === index)
}

function compareRationals(left: RationalTime, right: RationalTime): number {
  const difference =
    BigInt(left.value) * BigInt(right.rate) -
    BigInt(right.value) * BigInt(left.rate)
  return difference < 0n ? -1 : difference > 0n ? 1 : 0
}

function rationalsEqual(left: RationalTime, right: RationalTime): boolean {
  return compareRationals(left, right) === 0
}

function nullableRationalsEqual(
  left: RationalTime | null,
  right: RationalTime | null,
): boolean {
  return left === null ? right === null : right !== null && rationalsEqual(left, right)
}

function sumEquals(
  left: RationalTime,
  right: RationalTime,
  expected: RationalTime,
): boolean {
  const numerator =
    BigInt(left.value) * BigInt(right.rate) +
    BigInt(right.value) * BigInt(left.rate)
  const denominator = BigInt(left.rate) * BigInt(right.rate)
  return numerator * BigInt(expected.rate) === BigInt(expected.value) * denominator
}

function isCanonicalRequestId(value: unknown): value is string {
  return typeof value === 'string' && uuidPattern.test(value)
}

function assertRequestId(value: string): void {
  if (!uuidPattern.test(value)) {
    throw new Error('A bridge request ID must be a non-empty lowercase UUID.')
  }
}

function assertEntityId(value: string): void {
  if (!uuidPattern.test(value)) {
    throw new Error('A player job ID must be a non-empty lowercase UUID.')
  }
}

function ensureBoundedRequest<T extends PlayerReadRequest | PlayerSeekRequest>(
  request: T,
): T {
  if (new TextEncoder().encode(JSON.stringify(request)).byteLength > maximumRequestUtf8Bytes) {
    throw new Error('The player bridge request exceeds the protocol byte limit.')
  }
  return request
}

function isNonnegativeInt32(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= 2_147_483_647
  )
}

function isPositiveInt32(value: unknown): value is number {
  return isNonnegativeInt32(value) && value > 0
}

function isInt16(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= -32_768 &&
    value <= 32_767
  )
}

function isWithinUtf8Limit(value: unknown, maximumBytes: number): boolean {
  try {
    const serialized = JSON.stringify(value)
    return (
      serialized !== undefined &&
      new TextEncoder().encode(serialized).byteLength <= maximumBytes
    )
  } catch {
    return false
  }
}

function hasExactKeys(
  value: unknown,
  expectedKeys: readonly string[],
): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false
  }
  const keys = Object.keys(value)
  return (
    keys.length === expectedKeys.length &&
    expectedKeys.every((key) => Object.prototype.hasOwnProperty.call(value, key))
  )
}

const zeroRational: RationalTime = Object.freeze({ value: '0', rate: '1' })
