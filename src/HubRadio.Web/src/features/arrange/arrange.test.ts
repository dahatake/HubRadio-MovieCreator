// NFR-UX-005 AC-129/130/131, NFR-UX-002 AC-127, FR-063 AC-038: arrangement reducer and persistence.
import { describe, expect, it } from 'vitest'
import { arrangeReducer, defaultArrangeState, isDefaultArrangement, sizeOf } from './arrangeState'
import { ARRANGE_STORAGE_PREFIX, loadArrangeState, saveArrangeState, sanitizeArrangeState, clearArrangeState, type ArrangeStorage } from './arrangeStorage'

const ids = ['a', 'b', 'c']

function memoryStorage(): ArrangeStorage & { data: Map<string, string> } {
  const data = new Map<string, string>()
  return { data, getItem: key => data.get(key) ?? null, setItem: (key, value) => { data.set(key, value) }, removeItem: key => { data.delete(key) } }
}

describe('arrangeReducer (NFR-UX-005 AC-129)', () => {
  it('moves a panel earlier and later and stops at the ends', () => {
    let s = defaultArrangeState(ids)
    s = arrangeReducer(s, { type: 'move', id: 'c', delta: -1 })
    expect(s.order).toEqual(['a', 'c', 'b'])
    expect(arrangeReducer(s, { type: 'move', id: 'a', delta: -1 })).toBe(s)
    expect(arrangeReducer(s, { type: 'move', id: 'b', delta: 1 })).toBe(s)
  })
  it('tabs, hides (never the last visible one), shows and maximizes', () => {
    let s = defaultArrangeState(ids)
    s = arrangeReducer(s, { type: 'toggleTab', id: 'a' })
    s = arrangeReducer(s, { type: 'toggleTab', id: 'b' })
    expect(s.tabbed).toEqual(['a', 'b'])
    expect(s.activeTab).toBe('a')
    s = arrangeReducer(s, { type: 'selectTab', id: 'b' })
    expect(s.activeTab).toBe('b')
    s = arrangeReducer(s, { type: 'toggleTab', id: 'b' })
    expect(s.activeTab).toBe('a')
    s = arrangeReducer(s, { type: 'hide', id: 'a' })
    s = arrangeReducer(s, { type: 'hide', id: 'b' })
    expect(s.hidden).toEqual(['a', 'b'])
    expect(arrangeReducer(s, { type: 'hide', id: 'c' }).hidden).toEqual(['a', 'b'])
    s = arrangeReducer(s, { type: 'show', id: 'a' })
    expect(s.hidden).toEqual(['b'])
    s = arrangeReducer(s, { type: 'toggleMaximize', id: 'c' })
    expect(s.maximized).toBe('c')
    expect(arrangeReducer(s, { type: 'toggleMaximize', id: 'c' }).maximized).toBeNull()
  })
  it('changes direction and clamps sizes', () => {
    let s = arrangeReducer(defaultArrangeState(ids), { type: 'setDirection', direction: 'column' })
    expect(s.direction).toBe('column')
    for (let i = 0; i < 6; i += 1) s = arrangeReducer(s, { type: 'resize', id: 'a', delta: 1 })
    expect(sizeOf(s, 'a')).toBe(4)
    for (let i = 0; i < 6; i += 1) s = arrangeReducer(s, { type: 'resize', id: 'a', delta: -1 })
    expect(sizeOf(s, 'a')).toBe(1)
    expect(s.sizes).toEqual({})
  })
  it('ignores unknown panels', () => {
    const s = defaultArrangeState(ids)
    expect(arrangeReducer(s, { type: 'hide', id: 'zzz' })).toBe(s)
  })
  it('reset returns the default arrangement (NFR-UX-002 AC-127)', () => {
    let s = arrangeReducer(defaultArrangeState(ids), { type: 'move', id: 'c', delta: -1 })
    s = arrangeReducer(s, { type: 'toggleMaximize', id: 'a' })
    expect(isDefaultArrangement(s, ids)).toBe(false)
    s = arrangeReducer(s, { type: 'reset', panelIds: ids })
    expect(isDefaultArrangement(s, ids)).toBe(true)
  })
})

describe('arrangeStorage (NFR-UX-002 AC-127)', () => {
  it('saves and restores per screen', () => {
    const storage = memoryStorage()
    const changed = arrangeReducer(defaultArrangeState(ids), { type: 'move', id: 'c', delta: -1 })
    expect(saveArrangeState('results', changed, storage)).toBe(true)
    expect(loadArrangeState('results', ids, storage)).toEqual(changed)
    expect(loadArrangeState('settings', ids, storage)).toEqual(defaultArrangeState(ids))
    expect(storage.data.has(ARRANGE_STORAGE_PREFIX + 'results')).toBe(true)
  })
  it('clear removes the saved arrangement', () => {
    const storage = memoryStorage()
    saveArrangeState('results', arrangeReducer(defaultArrangeState(ids), { type: 'hide', id: 'a' }), storage)
    clearArrangeState('results', storage)
    expect(loadArrangeState('results', ids, storage)).toEqual(defaultArrangeState(ids))
  })
  it('falls back to the default for corrupt or unavailable storage', () => {
    const storage = memoryStorage()
    storage.data.set(ARRANGE_STORAGE_PREFIX + 'results', '{not json')
    expect(loadArrangeState('results', ids, storage)).toEqual(defaultArrangeState(ids))
    expect(loadArrangeState('results', ids, null)).toEqual(defaultArrangeState(ids))
    expect(saveArrangeState('results', defaultArrangeState(ids), null)).toBe(false)
  })
  it('sanitizes unknown ids, new panels, bad sizes and an all-hidden layout', () => {
    const s = sanitizeArrangeState({ order: ['c', 'x', 'a'], hidden: ['x'], sizes: { a: 9, b: 2, q: 2 }, direction: 'diag', maximized: 'x' }, ids)
    expect(s.order).toEqual(['c', 'a', 'b'])
    expect(s.hidden).toEqual([])
    expect(s.sizes).toEqual({ b: 2 })
    expect(s.direction).toBe('default')
    expect(s.maximized).toBeNull()
    expect(sanitizeArrangeState({ order: ids, hidden: ids }, ids)).toEqual(defaultArrangeState(ids))
    expect(sanitizeArrangeState(42, ids)).toEqual(defaultArrangeState(ids))
  })
})
