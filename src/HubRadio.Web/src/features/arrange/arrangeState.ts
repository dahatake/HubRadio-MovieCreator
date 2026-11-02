// NFR-UX-005 / NFR-UX-002: pure layout model for a screen's components (panels).
// Panels stay mounted in a fixed DOM order; this state only says where they appear, so input,
// selection and filters inside a panel survive every operation.

export type ArrangeDirection = 'default' | 'row' | 'column'

export interface ArrangeState {
  /** Visual order of panel ids (CSS order). */
  readonly order: readonly string[]
  readonly hidden: readonly string[]
  /** Panels grouped into one tab strip (shown when 2 or more). */
  readonly tabbed: readonly string[]
  readonly activeTab: string | null
  readonly maximized: string | null
  readonly direction: ArrangeDirection
  /** Relative size 1..4; absent = 1. */
  readonly sizes: Readonly<Record<string, number>>
}

export type ArrangeAction =
  | { readonly type: 'move'; readonly id: string; readonly delta: -1 | 1 }
  | { readonly type: 'toggleTab'; readonly id: string }
  | { readonly type: 'selectTab'; readonly id: string }
  | { readonly type: 'hide'; readonly id: string }
  | { readonly type: 'show'; readonly id: string }
  | { readonly type: 'toggleMaximize'; readonly id: string }
  | { readonly type: 'setDirection'; readonly direction: ArrangeDirection }
  | { readonly type: 'resize'; readonly id: string; readonly delta: -1 | 1 }
  | { readonly type: 'reset'; readonly panelIds: readonly string[]; readonly preset?: ArrangePreset }

export const MIN_SIZE = 1
export const MAX_SIZE = 4

/**
 * NFR-UX-003: a persona's default way of showing a screen (order, direction, relative sizes). The user can
 * change it and 「既定に戻す」 returns to it. Unknown ids are ignored.
 */
export interface ArrangePreset {
  readonly order?: readonly string[]
  readonly direction?: ArrangeDirection
  readonly sizes?: Readonly<Record<string, number>>
}

export function defaultArrangeState(panelIds: readonly string[], preset?: ArrangePreset): ArrangeState {
  const first = (preset?.order ?? []).filter(id => panelIds.includes(id))
  const order = [...new Set([...first, ...panelIds])]
  const sizes: Record<string, number> = {}
  for (const [id, size] of Object.entries(preset?.sizes ?? {})) {
    if (panelIds.includes(id) && Number.isInteger(size) && size > MIN_SIZE && size <= MAX_SIZE) sizes[id] = size
  }
  return { order, hidden: [], tabbed: [], activeTab: null, maximized: null, direction: preset?.direction ?? 'default', sizes }
}

export function isDefaultArrangement(state: ArrangeState, panelIds: readonly string[], preset?: ArrangePreset): boolean {
  const base = defaultArrangeState(panelIds, preset)
  const sameSizes = Object.keys(base.sizes).length === Object.keys(state.sizes).length &&
    Object.entries(base.sizes).every(([id, size]) => state.sizes[id] === size)
  return state.order.join('\n') === base.order.join('\n') && state.hidden.length === 0 && state.tabbed.length === 0 &&
    state.maximized === null && state.direction === base.direction && sameSizes
}

export function sizeOf(state: ArrangeState, id: string): number {
  return state.sizes[id] ?? 1
}

function without(list: readonly string[], id: string): string[] {
  return list.filter(item => item !== id)
}

export function arrangeReducer(state: ArrangeState, action: ArrangeAction): ArrangeState {
  if (action.type === 'reset') return defaultArrangeState(action.panelIds, action.preset)
  if (action.type === 'setDirection') return { ...state, direction: action.direction }
  const id = action.id
  if (!state.order.includes(id)) return state
  switch (action.type) {
    case 'move': {
      const index = state.order.indexOf(id)
      const next = index + action.delta
      if (next < 0 || next >= state.order.length) return state
      const order = state.order.slice()
      ;[order[index], order[next]] = [order[next]!, order[index]!]
      return { ...state, order }
    }
    case 'toggleTab': {
      if (state.tabbed.includes(id)) {
        const tabbed = without(state.tabbed, id)
        return { ...state, tabbed, activeTab: state.activeTab === id ? tabbed[0] ?? null : state.activeTab }
      }
      return { ...state, tabbed: [...state.tabbed, id], activeTab: state.activeTab ?? id }
    }
    case 'selectTab':
      return state.tabbed.includes(id) ? { ...state, activeTab: id } : state
    case 'hide': {
      const visible = state.order.filter(item => !state.hidden.includes(item))
      if (state.hidden.includes(id) || visible.length <= 1) return state
      return { ...state, hidden: [...state.hidden, id], maximized: state.maximized === id ? null : state.maximized }
    }
    case 'show':
      return { ...state, hidden: without(state.hidden, id) }
    case 'toggleMaximize':
      return { ...state, maximized: state.maximized === id ? null : id, hidden: without(state.hidden, id) }
    case 'resize': {
      const size = Math.min(MAX_SIZE, Math.max(MIN_SIZE, sizeOf(state, id) + action.delta))
      const sizes = { ...state.sizes }
      if (size === 1) delete sizes[id]
      else sizes[id] = size
      return { ...state, sizes }
    }
  }
}
