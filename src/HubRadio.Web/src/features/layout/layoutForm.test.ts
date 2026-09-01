import { describe, expect, it } from 'vitest'

import {
  getRovingIndexAfterKey,
  layoutRectToText,
  layoutTimeRangeToText,
  manualLayoutModes,
  manualLayoutSourceCounts,
  parseExactLayoutRect,
  parseLayoutTimeRange,
  parseNormalizedLayoutRect,
} from './layoutForm'

describe('layout exact form helpers', () => {
  it('round-trips canonical normalized rational rectangles without floats', () => {
    const rect = parseNormalizedLayoutRect({
      x: '1/3',
      y: '0/1',
      width: '2/3',
      height: '1/1',
    })

    expect(rect).not.toBeNull()
    expect(rect === null ? null : layoutRectToText(rect)).toEqual({
      x: '1/3',
      y: '0/1',
      width: '2/3',
      height: '1/1',
    })
    expect(Object.isFrozen(rect)).toBe(true)
  })

  it.each([
    ['01/2', '0/1', '1/2', '1/1'],
    ['1/2', '0/1', '2/4', '1/1'],
    ['3/4', '0/1', '1/2', '1/1'],
    ['0/1', '0/1', '0/1', '1/1'],
    ['-1/2', '0/1', '1/2', '1/1'],
    ['0.5', '0/1', '1/2', '1/1'],
  ])('rejects noncanonical or out-of-frame geometry', (x, y, width, height) => {
    expect(parseNormalizedLayoutRect({ x, y, width, height })).toBeNull()
  })

  it('keeps all seven real modes and their exact source arity', () => {
    expect(manualLayoutModes).toHaveLength(7)
    expect(manualLayoutSourceCounts).toEqual({
      singleSubjectCrop: 1,
      twoSpeakerStack: 2,
      speakerMaterialStack: 2,
      materialWithSpeakerInset: 2,
      importantRegionZoom: 1,
      blurredSourceBackground: 2,
      fixedTemplatePlacement: 1,
    })
  })

  it('parses canonical signed placement and exact positive-duration timing', () => {
    expect(parseExactLayoutRect({
      x: '-1/4',
      y: '0/1',
      width: '3/2',
      height: '1/1',
    })).toEqual({
      x: { numerator: '-1', denominator: '4' },
      y: { numerator: '0', denominator: '1' },
      width: { numerator: '3', denominator: '2' },
      height: { numerator: '1', denominator: '1' },
    })
    const range = parseLayoutTimeRange({
      startValue: '-1',
      startRate: '2',
      durationValue: '3',
      durationRate: '2',
    })
    expect(range).not.toBeNull()
    expect(range === null ? null : layoutTimeRangeToText(range)).toEqual({
      startValue: '-1',
      startRate: '2',
      durationValue: '3',
      durationRate: '2',
    })
    expect(parseLayoutTimeRange({
      startValue: '0',
      startRate: '1',
      durationValue: '0',
      durationRate: '1',
    })).toBeNull()
  })

  it('supports wraparound arrow, Home, and End navigation', () => {
    expect(getRovingIndexAfterKey(0, 'ArrowUp', 7)).toBe(6)
    expect(getRovingIndexAfterKey(6, 'ArrowDown', 7)).toBe(0)
    expect(getRovingIndexAfterKey(3, 'Home', 7)).toBe(0)
    expect(getRovingIndexAfterKey(3, 'End', 7)).toBe(6)
    expect(getRovingIndexAfterKey(3, 'Enter', 7)).toBeNull()
  })
})
