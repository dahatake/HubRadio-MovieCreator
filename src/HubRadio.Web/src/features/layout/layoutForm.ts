import type {
  LayoutRational,
  LayoutRationalTime,
  LayoutRect,
  LayoutTimeRange,
  ManualLayoutMode,
} from './layoutProtocol'

export interface LayoutRectText {
  readonly x: string
  readonly y: string
  readonly width: string
  readonly height: string
}

export interface LayoutTimeRangeText {
  readonly startValue: string
  readonly startRate: string
  readonly durationValue: string
  readonly durationRate: string
}

export const manualLayoutModes: readonly ManualLayoutMode[] = Object.freeze([
  'singleSubjectCrop',
  'twoSpeakerStack',
  'speakerMaterialStack',
  'materialWithSpeakerInset',
  'importantRegionZoom',
  'blurredSourceBackground',
  'fixedTemplatePlacement',
])

export const manualLayoutModeLabels: Readonly<Record<ManualLayoutMode, string>> =
  Object.freeze({
    singleSubjectCrop: '単一被写体クロップ',
    twoSpeakerStack: '話者2名の上下配置',
    speakerMaterialStack: '話者と資料の上下配置',
    materialWithSpeakerInset: '資料と話者インセット',
    importantRegionZoom: '重要領域ズーム',
    blurredSourceBackground: 'ぼかし背景と前景被写体',
    fixedTemplatePlacement: '固定テンプレート配置',
  })

export const manualLayoutSourceCounts: Readonly<Record<ManualLayoutMode, number>> =
  Object.freeze({
    singleSubjectCrop: 1,
    twoSpeakerStack: 2,
    speakerMaterialStack: 2,
    materialWithSpeakerInset: 2,
    importantRegionZoom: 1,
    blurredSourceBackground: 2,
    fixedTemplatePlacement: 1,
  })

export function layoutRectToText(rect: LayoutRect): LayoutRectText {
  return Object.freeze({
    x: rationalToText(rect.x),
    y: rationalToText(rect.y),
    width: rationalToText(rect.width),
    height: rationalToText(rect.height),
  })
}

export function parseNormalizedLayoutRect(
  value: LayoutRectText,
): LayoutRect | null {
  const x = parseRational(value.x, false)
  const y = parseRational(value.y, false)
  const width = parseRational(value.width, true)
  const height = parseRational(value.height, true)
  if (
    x === null ||
    y === null ||
    width === null ||
    height === null ||
    compare(add(x, width), one) > 0 ||
    compare(add(y, height), one) > 0
  ) {
    return null
  }
  return Object.freeze({ x, y, width, height })
}

export function parseExactLayoutRect(value: LayoutRectText): LayoutRect | null {
  const x = parseSignedRational(value.x, false)
  const y = parseSignedRational(value.y, false)
  const width = parseSignedRational(value.width, true)
  const height = parseSignedRational(value.height, true)
  return x === null || y === null || width === null || height === null
    ? null
    : Object.freeze({ x, y, width, height })
}

export function layoutTimeRangeToText(
  range: LayoutTimeRange,
): LayoutTimeRangeText {
  return Object.freeze({
    startValue: range.start.value,
    startRate: range.start.rate,
    durationValue: range.duration.value,
    durationRate: range.duration.rate,
  })
}

export function parseLayoutTimeRange(
  value: LayoutTimeRangeText,
): LayoutTimeRange | null {
  const start = parseRationalTime(value.startValue, value.startRate, false)
  const duration = parseRationalTime(
    value.durationValue,
    value.durationRate,
    true,
  )
  return start === null || duration === null
    ? null
    : Object.freeze({ start, duration })
}

export function getRovingIndexAfterKey(
  currentIndex: number,
  key: string,
  length: number,
): number | null {
  if (!Number.isInteger(currentIndex) || currentIndex < 0 || currentIndex >= length) {
    return null
  }
  switch (key) {
    case 'ArrowLeft':
    case 'ArrowUp':
      return currentIndex === 0 ? length - 1 : currentIndex - 1
    case 'ArrowRight':
    case 'ArrowDown':
      return currentIndex === length - 1 ? 0 : currentIndex + 1
    case 'Home':
      return 0
    case 'End':
      return length - 1
    default:
      return null
  }
}

function rationalToText(value: LayoutRational): string {
  return `${value.numerator}/${value.denominator}`
}

function parseRational(value: string, positive: boolean): LayoutRational | null {
  const match = /^(0|[1-9][0-9]{0,18})\/([1-9][0-9]{0,18})$/.exec(value)
  if (match === null) {
    return null
  }
  const numerator = BigInt(match[1]!)
  const denominator = BigInt(match[2]!)
  if (
    (positive && numerator === 0n) ||
    numerator > maximumInt64 ||
    denominator > maximumInt64 ||
    gcd(numerator, denominator) !== 1n
  ) {
    return null
  }
  return Object.freeze({
    numerator: numerator.toString(),
    denominator: denominator.toString(),
  })
}

function parseSignedRational(
  value: string,
  positive: boolean,
): LayoutRational | null {
  const match = /^(0|-?[1-9][0-9]{0,18})\/([1-9][0-9]{0,18})$/.exec(value)
  if (match === null) {
    return null
  }
  const numerator = BigInt(match[1]!)
  const denominator = BigInt(match[2]!)
  if (
    (positive && numerator <= 0n) ||
    numerator < minimumInt64 ||
    numerator > maximumInt64 ||
    denominator > maximumInt64 ||
    gcd(numerator, denominator) !== 1n
  ) {
    return null
  }
  return Object.freeze({
    numerator: numerator.toString(),
    denominator: denominator.toString(),
  })
}

function parseRationalTime(
  value: string,
  rate: string,
  positiveValue: boolean,
): LayoutRationalTime | null {
  if (
    !/^(0|-?[1-9][0-9]{0,18})$/.test(value) ||
    !/^[1-9][0-9]{0,18}$/.test(rate)
  ) {
    return null
  }
  const numerator = BigInt(value)
  const denominator = BigInt(rate)
  if (
    (positiveValue && numerator <= 0n) ||
    numerator < minimumInt64 ||
    numerator > maximumInt64 ||
    denominator > maximumInt64 ||
    gcd(numerator, denominator) !== 1n
  ) {
    return null
  }
  return Object.freeze({ value: numerator.toString(), rate: denominator.toString() })
}

function add(left: LayoutRational, right: LayoutRational): ExactRational {
  return normalize(
    BigInt(left.numerator) * BigInt(right.denominator) +
      BigInt(right.numerator) * BigInt(left.denominator),
    BigInt(left.denominator) * BigInt(right.denominator),
  )
}

function compare(left: ExactRational, right: ExactRational): number {
  const difference = left.numerator * right.denominator -
    right.numerator * left.denominator
  return difference < 0n ? -1 : difference > 0n ? 1 : 0
}

function normalize(numerator: bigint, denominator: bigint): ExactRational {
  const divisor = gcd(numerator, denominator)
  return {
    numerator: numerator / divisor,
    denominator: denominator / divisor,
  }
}

function gcd(left: bigint, right: bigint): bigint {
  let a = left < 0n ? -left : left
  let b = right < 0n ? -right : right
  while (b !== 0n) {
    const remainder = a % b
    a = b
    b = remainder
  }
  return a
}

interface ExactRational {
  readonly numerator: bigint
  readonly denominator: bigint
}

const one: ExactRational = Object.freeze({ numerator: 1n, denominator: 1n })
const minimumInt64 = -9_223_372_036_854_775_808n
const maximumInt64 = 9_223_372_036_854_775_807n
