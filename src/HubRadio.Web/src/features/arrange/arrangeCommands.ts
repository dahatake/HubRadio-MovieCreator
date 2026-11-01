// NFR-UX-005 AC-130: the commands behind the arrangement command search (keyboard, no drag).
import { MAX_SIZE, MIN_SIZE, sizeOf, type ArrangeAction, type ArrangeState } from './arrangeState'

export interface ArrangeCommand {
  readonly id: string
  readonly label: string
  /** An arrangement action, or the screen-level 「既定に戻す」 (which still asks for confirmation). */
  readonly action: ArrangeAction | { readonly type: 'requestReset' }
  readonly message: string
}

export function buildArrangeCommands(
  panels: readonly { readonly id: string; readonly label: string }[],
  state: ArrangeState,
): ArrangeCommand[] {
  const commands: ArrangeCommand[] = []
  const visibleCount = state.order.filter(id => !state.hidden.includes(id)).length
  for (const { id, label } of panels) {
    const position = state.order.indexOf(id)
    if (position < 0) continue
    const hidden = state.hidden.includes(id)
    if (hidden) commands.push({ id: `show:${id}`, label: `${label}を表示`, action: { type: 'show', id }, message: `${label}を表示しました` })
    else if (visibleCount > 1) commands.push({ id: `hide:${id}`, label: `${label}を非表示`, action: { type: 'hide', id }, message: `${label}を非表示にしました` })
    if (position > 0) commands.push({ id: `up:${id}`, label: `${label}を前へ移動`, action: { type: 'move', id, delta: -1 }, message: `${label}を前へ移動しました` })
    if (position < state.order.length - 1) commands.push({ id: `down:${id}`, label: `${label}を後ろへ移動`, action: { type: 'move', id, delta: 1 }, message: `${label}を後ろへ移動しました` })
    commands.push({
      id: `max:${id}`, label: state.maximized === id ? `${label}の最大化を戻す` : `${label}を最大化`,
      action: { type: 'toggleMaximize', id }, message: state.maximized === id ? `${label}の最大化を戻しました` : `${label}を最大化しました`,
    })
    commands.push({
      id: `tab:${id}`, label: state.tabbed.includes(id) ? `${label}のタブ化を解除` : `${label}をタブにまとめる`,
      action: { type: 'toggleTab', id }, message: state.tabbed.includes(id) ? `${label}のタブ化を解除しました` : `${label}をタブにまとめました`,
    })
    if (sizeOf(state, id) < MAX_SIZE) commands.push({ id: `grow:${id}`, label: `${label}を大きく`, action: { type: 'resize', id, delta: 1 }, message: `${label}を大きくしました` })
    if (sizeOf(state, id) > MIN_SIZE) commands.push({ id: `shrink:${id}`, label: `${label}を小さく`, action: { type: 'resize', id, delta: -1 }, message: `${label}を小さくしました` })
  }
  commands.push({ id: 'direction:row', label: '横に並べる', action: { type: 'setDirection', direction: 'row' }, message: '並べる方向を「横に並べる」にしました' })
  commands.push({ id: 'direction:column', label: '縦に並べる', action: { type: 'setDirection', direction: 'column' }, message: '並べる方向を「縦に並べる」にしました' })
  commands.push({ id: 'reset', label: '既定に戻す', action: { type: 'requestReset' }, message: '' })
  return commands
}

export function filterArrangeCommands(commands: readonly ArrangeCommand[], query: string): ArrangeCommand[] {
  const terms = query.normalize('NFKC').toLowerCase().split(/\s+/).filter(term => term !== '')
  if (terms.length === 0) return [...commands]
  return commands.filter(command => {
    const text = command.label.normalize('NFKC').toLowerCase()
    return terms.every(term => text.includes(term))
  })
}
