import { useEffect, useMemo, useReducer, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import {
  arrangeReducer,
  defaultArrangeState,
  isDefaultArrangement,
  MAX_SIZE,
  MIN_SIZE,
  sizeOf,
  type ArrangeDirection,
  type ArrangePreset,
  type ArrangeState,
} from './arrangeState'
import { buildArrangeCommands, filterArrangeCommands, type ArrangeCommand } from './arrangeCommands'
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
  /** NFR-UX-003: the persona's default arrangement; 「既定に戻す」 returns to this. */
  readonly preset?: ArrangePreset
}

const directionLabels: Readonly<Record<ArrangeDirection, string>> = { default: '標準', row: '横に並べる', column: '縦に並べる' }

/**
 * NFR-UX-005: move / tab / hide / maximize / resize / direction for a screen's components, with every
 * operation reachable by keyboard (no drag). All panels stay mounted, so typed input, selection and filters survive.
 * NFR-UX-002: the arrangement is saved per screen and restored; 「既定に戻す」 asks first.
 */
export function ArrangeablePanels({ screenId, panels, className, storage, preset }: ArrangeablePanelsProps) {
  const idKey = panels.map(panel => panel.id).join('\n')
  const ids = useMemo(() => idKey.split('\n'), [idKey])
  const presetRef = useRef(preset)
  presetRef.current = preset
  const [state, dispatch] = useReducer(
    (current: ArrangeState, action: Parameters<typeof arrangeReducer>[1]) =>
      arrangeReducer(current, action.type === 'reset' ? { ...action, preset: presetRef.current } : action),
    undefined,
    () => loadArrangeState(screenId, ids, storage, preset),
  )
  const [query, setQuery] = useState('')
  const [editing, setEditing] = useState(false)
  const [moving, setMoving] = useState<string | null>(null)
  const [confirmReset, setConfirmReset] = useState(false)
  const [announcement, setAnnouncement] = useState('')
  const first = useRef(true)

  useEffect(() => {
    if (first.current) { first.current = false; return }
    if (isDefaultArrangement(state, ids, presetRef.current)) clearArrangeState(screenId, storage)
    else saveArrangeState(screenId, state, storage)
  }, [state, screenId, idKey, ids, storage])

  const labelOf = (id: string) => panels.find(panel => panel.id === id)?.label ?? id
  const act = (action: Parameters<typeof arrangeReducer>[1], message: string) => { dispatch(action); setAnnouncement(message) }
  const suppressedIds = new Set(panels.filter(panel => panel.suppressed === true).map(panel => panel.id))
  const applicable = ids.filter(id => !suppressedIds.has(id))
  // A restored state must never leave the screen without a panel (and its operation buttons): fall back to the first applicable one.
  const forcedVisible = applicable.length > 0 && applicable.every(id => state.hidden.includes(id)) ? applicable[0] : null
  const effHidden = (id: string) => state.hidden.includes(id) && id !== forcedVisible
  const effMaximized = state.maximized !== null && applicable.includes(state.maximized) && !effHidden(state.maximized) ? state.maximized : null
  const tabGroup = state.tabbed.filter(id => ids.includes(id) && !suppressedIds.has(id) && !effHidden(id))
  const tabsShown = tabGroup.length >= 2
  const activeTab = state.activeTab !== null && tabGroup.includes(state.activeTab) ? state.activeTab : tabGroup[0] ?? null
  const visibleCount = state.order.filter(id => !state.hidden.includes(id) && !suppressedIds.has(id)).length
  const customized = state.direction !== 'default' || Object.keys(state.sizes).length > 0
  const tabOrder = tabsShown ? Math.min(...tabGroup.map(id => state.order.indexOf(id))) * 2 + 1 : 0

  const matches = useMemo(
    () => query.trim() === '' ? [] : filterArrangeCommands(buildArrangeCommands(panels, state), query),
    [panels, state, query],
  )
  const runCommand = (command: ArrangeCommand) => {
    if (command.action.type === 'requestReset') setConfirmReset(true)
    else act(command.action, command.message)
    setQuery('')
  }

  const moveByKey = (event: KeyboardEvent, id: string) => {
    const delta = event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : 0
    if (delta === 0) return
    event.preventDefault()
    act({ type: 'move', id, delta }, `${labelOf(id)}を${delta < 0 ? '前' : '後ろ'}へ移動しました`)
  }

  return (
    <div className="arrange">
      <div className={`arrange-container ${className ?? ''}`.trim()} data-direction={state.direction} data-customized={customized ? 'true' : undefined}
        data-maximized={effMaximized ?? undefined}>
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
          const hiddenNow = effHidden(id) || panel.suppressed === true ||
            (effMaximized !== null && effMaximized !== id) || (tabsShown && tabGroup.includes(id) && activeTab !== id)
          const maximized = effMaximized === id
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
                <button type="button" aria-disabled={visibleCount <= 1 || undefined} aria-describedby={`arrange-desc-${screenId}-${id}`}
                  onClick={() => visibleCount <= 1
                    ? setAnnouncement('最後に残った表示中のコンポーネントは非表示にできません')
                    : act({ type: 'hide', id }, `${panel.label}を非表示にしました`)}>非表示</button>
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
        <div className="arrange-command" role="search" aria-label="配置のコマンド検索">
          <label htmlFor={`arrange-command-${screenId}`}>配置のコマンド検索</label>
          <input id={`arrange-command-${screenId}`} type="search" value={query} placeholder="例: プレビュー 最大化"
            role="combobox" aria-expanded={query.trim() !== ''} aria-controls={`arrange-command-list-${screenId}`} aria-autocomplete="list"
            onChange={event => setQuery(event.target.value)}
            onKeyDown={event => {
              if (event.key === 'Escape') { setQuery(''); return }
              if (event.key === 'ArrowDown') {
                event.preventDefault()
                event.currentTarget.parentElement?.querySelector<HTMLElement>('[role="option"]')?.focus()
              }
              if (event.key === 'Enter' && matches[0] !== undefined) { event.preventDefault(); runCommand(matches[0]) }
            }} />
          {query.trim() !== '' && (
            <ul id={`arrange-command-list-${screenId}`} role="listbox" aria-label="配置のコマンドの候補">
              {matches.length === 0 && <li role="status">一致するコマンドはありません</li>}
              {matches.map(command => (
                <li key={command.id} role="presentation">
                  <button type="button" role="option" aria-selected="false" onClick={() => runCommand(command)}
                    onKeyDown={event => {
                      const items = Array.from(event.currentTarget.closest('ul')?.querySelectorAll<HTMLElement>('[role="option"]') ?? [])
                      const at = items.indexOf(event.currentTarget)
                      if (event.key === 'ArrowDown') { event.preventDefault(); items[Math.min(items.length - 1, at + 1)]?.focus() }
                      if (event.key === 'ArrowUp') { event.preventDefault(); (at <= 0 ? event.currentTarget.closest('[role="search"]')?.querySelector<HTMLElement>('input') : items[at - 1])?.focus() }
                      if (event.key === 'Escape') { setQuery(''); event.currentTarget.closest('[role="search"]')?.querySelector<HTMLElement>('input')?.focus() }
                    }}>{command.label}</button>
                </li>
              ))}
            </ul>
          )}
        </div>
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
