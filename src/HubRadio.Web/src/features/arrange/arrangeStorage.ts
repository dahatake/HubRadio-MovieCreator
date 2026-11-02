// NFR-UX-002: per user (the OS profile that owns the WebView2 data folder) and per screen persistence
// of a screen's arrangement. Stored in the local profile; nothing is sent anywhere.
import { defaultArrangeState, MAX_SIZE, MIN_SIZE, type ArrangeDirection, type ArrangePreset, type ArrangeState } from './arrangeState'

export const ARRANGE_STORAGE_PREFIX = 'hubradio.layout.v1.'

export type ArrangeStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

function defaultStorage(): ArrangeStorage | null {
  try {
    return typeof window !== 'undefined' ? window.localStorage : null
  } catch {
    return null
  }
}

const directions: readonly ArrangeDirection[] = ['default', 'row', 'column']

/** Accepts only a well-formed stored value; unknown panels are dropped and new panels appended. */
export function sanitizeArrangeState(value: unknown, panelIds: readonly string[], preset?: ArrangePreset): ArrangeState {
  const base = defaultArrangeState(panelIds, preset)
  if (typeof value !== 'object' || value === null) return base
  const raw = value as Record<string, unknown>
  const known = new Set(panelIds)
  const ids = (list: unknown): string[] =>
    Array.isArray(list) ? [...new Set(list.filter((item): item is string => typeof item === 'string' && known.has(item)))] : []
  const stored = ids(raw.order)
  const order = [...stored, ...panelIds.filter(id => !stored.includes(id))]
  const hidden = ids(raw.hidden)
  if (order.length - hidden.length < 1) return base
  const tabbed = ids(raw.tabbed)
  const sizes: Record<string, number> = {}
  if (typeof raw.sizes === 'object' && raw.sizes !== null) {
    for (const [id, size] of Object.entries(raw.sizes)) {
      if (known.has(id) && typeof size === 'number' && Number.isInteger(size) && size > MIN_SIZE && size <= MAX_SIZE) sizes[id] = size
    }
  }
  const activeTab = typeof raw.activeTab === 'string' && tabbed.includes(raw.activeTab) ? raw.activeTab : tabbed[0] ?? null
  const maximized = typeof raw.maximized === 'string' && known.has(raw.maximized) && !hidden.includes(raw.maximized) ? raw.maximized : null
  const direction = directions.includes(raw.direction as ArrangeDirection) ? (raw.direction as ArrangeDirection) : 'default'
  return { order, hidden, tabbed, activeTab, maximized, direction, sizes }
}

export function loadArrangeState(screenId: string, panelIds: readonly string[], storage: ArrangeStorage | null = defaultStorage(), preset?: ArrangePreset): ArrangeState {
  if (storage === null) return defaultArrangeState(panelIds, preset)
  try {
    const text = storage.getItem(ARRANGE_STORAGE_PREFIX + screenId)
    return text === null ? defaultArrangeState(panelIds, preset) : sanitizeArrangeState(JSON.parse(text), panelIds, preset)
  } catch {
    return defaultArrangeState(panelIds, preset)
  }
}

export function saveArrangeState(screenId: string, state: ArrangeState, storage: ArrangeStorage | null = defaultStorage()): boolean {
  if (storage === null) return false
  try {
    storage.setItem(ARRANGE_STORAGE_PREFIX + screenId, JSON.stringify(state))
    return true
  } catch {
    return false
  }
}

export function clearArrangeState(screenId: string, storage: ArrangeStorage | null = defaultStorage()): void {
  try {
    storage?.removeItem(ARRANGE_STORAGE_PREFIX + screenId)
  } catch {
    // The in-memory arrangement is already reset.
  }
}
