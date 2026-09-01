export interface GenericUiMaskProps {
  readonly visible: boolean
  readonly audioOnly: boolean
}

export function GenericUiMask({
  visible,
  audioOnly,
}: GenericUiMaskProps) {
  if (!visible || audioOnly) {
    return null
  }

  return (
    <div
      className="generic-ui-mask"
      role="note"
      data-mask-kind="generic-offline"
      data-platform-fidelity="not-claimed"
    >
      <span>汎用 UI mask</span>
      <span aria-hidden="true" />
      <span>プラットフォーム固有 UI の忠実な再現ではありません</span>
    </div>
  )
}