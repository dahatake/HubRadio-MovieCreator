export interface ExactRationalTime {
  readonly value: string
  readonly rate: string
}

const signedIntegerPattern = /^(?:0|-?[1-9][0-9]{0,18})(?![\s\S])/
const positiveIntegerPattern = /^[1-9][0-9]{0,18}(?![\s\S])/
const int64Minimum = -9_223_372_036_854_775_808n
const int64Maximum = 9_223_372_036_854_775_807n

export const exactZero: ExactRationalTime = Object.freeze({
  value: '0',
  rate: '1',
})

export function parseExactRationalTime(
  value: unknown,
): ExactRationalTime | null {
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
    greatestCommonDivisor(absolute(numerator), denominator) !== 1n
  ) {
    return null
  }

  return Object.freeze({ value: value.value, rate: value.rate })
}

export function createExactRationalTime(
  numerator: bigint,
  denominator: bigint,
): ExactRationalTime {
  if (denominator <= 0n) {
    throw new RangeError('An exact rational rate must be positive.')
  }

  const divisor = greatestCommonDivisor(absolute(numerator), denominator)
  const reducedNumerator = numerator / divisor
  const reducedDenominator = denominator / divisor
  if (
    reducedNumerator < int64Minimum ||
    reducedNumerator > int64Maximum ||
    reducedDenominator > int64Maximum
  ) {
    throw new RangeError('The exact rational cannot be represented by Int64 values.')
  }

  return Object.freeze({
    value: reducedNumerator.toString(),
    rate: reducedDenominator.toString(),
  })
}

export function addExactRationalTime(
  left: ExactRationalTime,
  right: ExactRationalTime,
): ExactRationalTime {
  const leftValue = BigInt(left.value)
  const leftRate = BigInt(left.rate)
  const rightValue = BigInt(right.value)
  const rightRate = BigInt(right.rate)
  return createExactRationalTime(
    leftValue * rightRate + rightValue * leftRate,
    leftRate * rightRate,
  )
}

export function subtractExactRationalTime(
  left: ExactRationalTime,
  right: ExactRationalTime,
): ExactRationalTime {
  const leftValue = BigInt(left.value)
  const leftRate = BigInt(left.rate)
  const rightValue = BigInt(right.value)
  const rightRate = BigInt(right.rate)
  return createExactRationalTime(
    leftValue * rightRate - rightValue * leftRate,
    leftRate * rightRate,
  )
}

export function compareExactRationalTime(
  left: ExactRationalTime,
  right: ExactRationalTime,
): number {
  const difference =
    BigInt(left.value) * BigInt(right.rate) -
    BigInt(right.value) * BigInt(left.rate)
  return difference < 0n ? -1 : difference > 0n ? 1 : 0
}

export function exactRationalTimesEqual(
  left: ExactRationalTime,
  right: ExactRationalTime,
): boolean {
  return left.value === right.value && left.rate === right.rate
}

export function isStrictlyPositiveExactTime(
  value: ExactRationalTime,
): boolean {
  return BigInt(value.value) > 0n
}

export function exactTimeIsInHalfOpenRange(
  value: ExactRationalTime,
  start: ExactRationalTime,
  duration: ExactRationalTime,
): boolean {
  return (
    compareExactRationalTime(value, start) >= 0 &&
    compareExactRationalTime(
      value,
      addExactRationalTime(start, duration),
    ) < 0
  )
}

/** Converts exact time to a display-only number. Never use this result in an operation. */
export function exactTimeToDerivedSeconds(value: ExactRationalTime): number {
  return Number(BigInt(value.value)) / Number(BigInt(value.rate))
}

export function formatExactRationalTime(value: ExactRationalTime): string {
  return `${value.value}/${value.rate}`
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

function absolute(value: bigint): bigint {
  return value < 0n ? -value : value
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