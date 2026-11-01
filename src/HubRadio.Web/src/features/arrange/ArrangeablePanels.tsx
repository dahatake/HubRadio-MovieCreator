import { useEffect, useMemo, useReducer, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import {
  arrangeReducer,
  defaultArrangeState,
  isDefaultArrangement,
  MAX_SIZE,
  MIN_SIZE,
  sizeOf,
  type ArrangeDirection,
  type ArrangeState,
} from './arrangeState'
import { clearArrangeState, loadArrangeState, saveArrangeState, type ArrangeStorage } from './arrangeStorage'
import './arrange.css'

export interface ArrangePanel {
  readonly id: string
  readonly label: string
  /** The panel is not applicable right now (e.g. replaced by a comparison); it stays mounted. */
  readonly suppressed?: boolean
  readonly children: ReactNode
}

export interface ArrangeablePanelsProps {
  /** Per-screen persistence key (NFR-UX-002: per user, per screen). */
  readonly screenId: string
  readonly panels: readonly ArrangePanel[]
  readonly className?: string
  readonly storage?: ArrangeStorage | null
}

const directionLabels: Readonly<Record<ArrangeDirection, string>> = { default: '標準', row: '横に並べる', column: '縦に並べる' }

/**
 * NFR-UX-005: move / tab / hide / maximize / resize / direction for a screen's components, with every
 * operation reachable by keyboard (no drag). All panels stay mounted, so typed input, selection and filters survive.
 * NFR-UX-002: the arrangement is saved per screen and restored; 「既定に戻す」 asks first.
 */
export function ArrangeablePanels({ screenId, panels, className, storage }: ArrangeablePanelsProps) {
  const idKey = panels.map(panel => panel.id).join('\n')
  const ids = useMemo(() => idKey.split('\n'), [idKey])
  const [state, dispatch] = useReducer(
    (current: ArrangeState, action: Parameters<typeof arrangeReducer>[1]) => arrangeReducer(current, action),
    undefined,
    () => loadArrangeState(screenId, ids, storage),
  )
  const [editing, setEditing] = useState(false)
  const [moving, setMoving] = useState<string | null>(null)
  const [confirmReset, setConfirmReset] = useState(false)
  const [announcement, setAnnouncement] = useState('')
  const first = useRef(true)

  useEffect(() => {
    if (first.current) { first.current = false; return }
    if (isDefaultArrangement(state, ids)) clearArrangeState(screenId, storage)
    else saveArrangeState(screenId, state, storage)
  }, [state, screenId, idKey, ids, storage])

  const labelOf = (id: string) => panels.find(panel => panel.id === id)?.label ?? id
  const act = (action: Parameters<typeof arrangeReducer>[1], message: string) => { dispatch(action); setAnnouncement(message) }
  const tabGroup = state.tabbed.filter(id => ids.includes(id))
  const tabsShown = tabGroup.length >= 2
  const activeTab = state.activeTab !== null && tabGroup.includes(state.activeTab) ? state.activeTab : tabGroup[0] ?? null
  const visibleCount = state.order.filter(id => !state.hidden.includes(id)).length
  const customized = state.direction !== 'default' || Object.keys(state.sizes).length > 0
  const tabOrder = tabsShown ? Math.min(...tabGroup.map(id => state.order.indexOf(id))) * 2 + 1 : 0

  const moveByKey = (event: KeyboardEvent, id: string) => {
    const delta = event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : 0
    if (delta === 0) return
    event.preventDefault()
    act({ type: 'move', id, delta }, `${labelOf(id)}を${delta < 0 ? '前' : '後ろ'}へ移動しました`)
  }

  return (
    <div className="arrange">
      <div className={`arrange-container ${className ?? ''}`.trim()} data-direction={state.direction} data-customized={customized ? 'true' : undefined}
        data-maximized={state.maximized ?? undefined}>
        {tabsShown && (
          <div className="arrange-tabs" role="tablist" aria-label="タブにまとめたコンポーネント" style={{ order: tabOrder }}>
            {tabGroup.map(id => (
              <button key={id} type="button" role="tab" aria-selected={activeTab === id} tabIndex={activeTab === id ? 0 : -1}
                onClick={() => dispatch({ type: 'selectTab', id })}
                onKeyDown={event => {
                  const at = tabGroup.indexOf(id)
                  const next = event.key === 'ArrowRight' ? tabGroup[(at + 1) % tabGroup.length] : event.key === 'ArrowLeft' ? tabGroup[(at - 1 + tabGroup.length) % tabGroup.length] : undefined
                  if (next === undefined) return
                  event.preventDefault(); dispatch({ type: 'selectTab', id: next })
                  event.currentTarget.parentElement?.querySelector<HTMLElement>(`[data-tab-id="${next}"]`)?.focus()
                }} data-tab-id={id}>{labelOf(id)}</button>
            ))}
          </div>
        )}
        {panels.map(panel => {
          const id = panel.id
          const hiddenNow = state.hidden.includes(id) || panel.suppressed === true ||
            (state.maximized !== null && state.maximized !== id) || (tabsShown && tabGroup.includes(id) && activeTab !== id)
          const maximized = state.maximized === id
          const tabbed = state.tabbed.includes(id)
          const position = state.order.indexOf(id)
          return (
            <section key={id} className="arrange-panel" aria-label={panel.label} hidden={hiddenNow} inert={hiddenNow}
              data-panel-id={id} style={{ order: position * 2 + 2, flexGrow: sizeOf(state, id) }}>
              {/* DOM order keeps the content first so Tab from a tablist still lands on its panel; CSS shows the bar on top. */}
              <div className="arrange-panel-body">{panel.children}</div>
              <div className="arrange-panel-bar" role="toolbar" aria-label={`${panel.label}の配置`}>
                <span className="arrange-panel-title" aria-hidden="true">{panel.label}</span>
                <button type="button" aria-expanded={moving === id} aria-describedby={`arrange-desc-${screenId}-${id}`}
                  onClick={() => setMoving(current => current === id ? null : id)} onKeyDown={event => moveByKey(event, id)}>移動</button>
                <button type="button" aria-pressed={tabbed} aria-describedby={`arrange-desc-${screenId}-${id}`}
                  onClick={() => act({ type: 'toggleTab', id }, tabbed ? `${panel.label}のタブ化を解除しました` : `${panel.label}をタブにまとめました`)}>タブ化</button>
                <button type="button" disabled={visibleCount <= 1} aria-describedby={`arrange-desc-${screenId}-${id}`}
                  onClick={() => act({ type: 'hide', id }, `${panel.label}を非表示にしました`)}>非表示</button>
                <button type="button" aria-pressed={maximized} aria-describedby={`arrange-desc-${screenId}-${id}`}
                  onClick={() => act({ type: 'toggleMaximize', id }, maximized ? `${panel.label}の最大化を戻しました` : `${panel.label}を最大化しました`)}>最大化</button>
                <span id={`arrange-desc-${screenId}-${id}`} className="arrange-sr-only">{panel.label}</span>
                {moving === id && <>
                  <button type="button" disabled={position === 0} onClick={() => act({ type: 'move', id, delta: -1 }, `${panel.label}を前へ移動しました`)}>前へ</button>
                  <button type="button" disabled={position === ids.length - 1} onClick={() => act({ type: 'move', id, delta: 1 }, `${panel.label}を後ろへ移動しました`)}>後ろへ</button>
                </>}
                {editing && <>
                  <button type="button" disabled={sizeOf(state, id) >= MAX_SIZE} onClick={() => act({ type: 'resize', id, delta: 1 }, `${panel.label}を大きくしました`)}>大きく</button>
                  <button type="button" disabled={sizeOf(state, id) <= MIN_SIZE} onClick={() => act({ type: 'resize', id, delta: -1 }, `${panel.label}を小さくしました`)}>小さく</button>
                </>}
              </div>
            </section>
          )
        })}
      </div>
      <div className="arrange-toolbar" role="toolbar" aria-label="画面の配置">
        <button type="button" aria-pressed={editing} onClick={() => setEditing(value => !value)}>配置を変更</button>
        <button type="button" onClick={() => setConfirmReset(true)}>既定に戻す</button>
        {editing && <div className="arrange-direction" role="group" aria-label="並べる方向">
          {(['default', 'row', 'column'] as const).map(direction => (
            <button key={direction} type="button" aria-pressed={state.direction === direction}
              onClick={() => act({ type: 'setDirection', direction }, `並べる方向を「${directionLabels[direction]}」にしました`)}>{directionLabels[direction]}</button>
          ))}
        </div>}
        {state.hidden.map(id => (
          <button key={id} type="button" onClick={() => act({ type: 'show', id }, `${labelOf(id)}を表示しました`)}>{labelOf(id)}を表示</button>
        ))}
        <span className="arrange-status" role="status">{announcement}</span>
      </div>
      {confirmReset && (
        <div className="arrange-confirm" role="alertdialog" aria-label="配置を既定に戻す確認" aria-describedby={`arrange-confirm-${screenId}`}>
          <p id={`arrange-confirm-${screenId}`}>この画面の配置を既定に戻します。入力中の内容は失われません。よろしいですか？</p>
          <button type="button" autoFocus onClick={() => {
            dispatch({ type: 'reset', panelIds: ids }); clearArrangeState(screenId, storage)
            setConfirmReset(false); setMoving(null); setAnnouncement('配置を既定に戻しました')
          }}>既定に戻す（確定）</button>
          <button type="button" onClick={() => setConfirmReset(false)}>やめる</button>
        </div>
      )}
    </div>
  )
}

export { defaultArrangeState }
