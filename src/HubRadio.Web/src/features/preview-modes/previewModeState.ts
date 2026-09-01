export type PreviewOrientation = 'landscape' | 'portrait'

export interface PreviewModeState {
  readonly orientation: PreviewOrientation | null
  readonly subtitlesVisible: boolean
  readonly genericUiMaskVisible: boolean
  readonly thumbnailReduced: boolean
  readonly mobilePreview: boolean
  readonly audioOnly: boolean
}

export type PreviewModeAction =
  | Readonly<{
      type: 'orientationChanged'
      orientation: PreviewOrientation
    }>
  | Readonly<{ type: 'subtitlesVisibilityChanged'; visible: boolean }>
  | Readonly<{ type: 'genericUiMaskVisibilityChanged'; visible: boolean }>
  | Readonly<{ type: 'thumbnailReductionChanged'; reduced: boolean }>
  | Readonly<{ type: 'mobilePreviewChanged'; enabled: boolean }>
  | Readonly<{ type: 'audioOnlyChanged'; enabled: boolean }>

export const initialPreviewModeState: PreviewModeState = Object.freeze({
  orientation: null,
  subtitlesVisible: true,
  genericUiMaskVisible: false,
  thumbnailReduced: false,
  mobilePreview: false,
  audioOnly: false,
})

export function previewModeReducer(
  state: PreviewModeState,
  action: PreviewModeAction,
): PreviewModeState {
  switch (action.type) {
    case 'orientationChanged':
      return { ...state, orientation: action.orientation }
    case 'subtitlesVisibilityChanged':
      return { ...state, subtitlesVisible: action.visible }
    case 'genericUiMaskVisibilityChanged':
      return { ...state, genericUiMaskVisible: action.visible }
    case 'thumbnailReductionChanged':
      return { ...state, thumbnailReduced: action.reduced }
    case 'mobilePreviewChanged':
      return { ...state, mobilePreview: action.enabled }
    case 'audioOnlyChanged':
      return { ...state, audioOnly: action.enabled }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}