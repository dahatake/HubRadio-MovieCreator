import { describe, expect, it } from 'vitest'

import {
  addExactRationalTime,
  compareExactRationalTime,
  createExactRationalTime,
  exactTimeIsInHalfOpenRange,
  exactTimeToDerivedSeconds,
  parseExactRationalTime,
  subtractExactRationalTime,
} from './rationalTime'

describe('exact timeline rational time', () => {
  it('performs canonical BigInt arithmetic without floating-point authority', () => {
    const oneThird = createExactRationalTime(1n, 3n)
    const oneSixth = createExactRationalTime(1n, 6n)
    const ntscFrame = createExactRationalTime(1001n, 24000n)

    expect(addExactRationalTime(oneThird, oneSixth)).toEqual({
      value: '1',
      rate: '2',
    })
    expect(subtractExactRationalTime(ntscFrame, oneSixth)).toEqual({
      value: '-2999',
      rate: '24000',
    })
    expect(compareExactRationalTime(ntscFrame, oneSixth)).toBe(-1)
    expect(exactTimeToDerivedSeconds(ntscFrame)).toBe(1001 / 24000)
  })

  it('uses exact half-open range boundaries for VFR positions', () => {
    const start = createExactRationalTime(-1n, 10n)
    const duration = createExactRationalTime(1001n, 24000n)
    const end = addExactRationalTime(start, duration)

    expect(exactTimeIsInHalfOpenRange(start, start, duration)).toBe(true)
    expect(exactTimeIsInHalfOpenRange(end, start, duration)).toBe(false)
    expect(
      exactTimeIsInHalfOpenRange(
        createExactRationalTime(-1999n, 24000n),
        start,
        duration,
      ),
    ).toBe(true)
  })

  it('rejects noncanonical or out-of-range wire rationals', () => {
    expect(parseExactRationalTime({ value: '2', rate: '2' })).toBeNull()
    expect(parseExactRationalTime({ value: '01', rate: '1' })).toBeNull()
    expect(parseExactRationalTime({ value: '0', rate: '2' })).toBeNull()
    expect(
      parseExactRationalTime({ value: '9223372036854775808', rate: '1' }),
    ).toBeNull()
    expect(
      parseExactRationalTime({ value: '-9223372036854775808', rate: '1' }),
    ).toEqual({ value: '-9223372036854775808', rate: '1' })
    expect(() =>
      createExactRationalTime(9_223_372_036_854_775_807n, 1n).value,
    ).not.toThrow()
    expect(() =>
      addExactRationalTime(
        createExactRationalTime(9_223_372_036_854_775_807n, 1n),
        createExactRationalTime(1n, 1n),
      ),
    ).toThrow('Int64')
  })
})