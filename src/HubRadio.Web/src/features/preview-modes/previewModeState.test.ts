import { describe, expect, it } from 'vitest'

import {
  initialPreviewModeState,
  previewModeReducer,
  type PreviewModeAction,
  type PreviewModeState,
} from './previewModeState'

describe('previewModeReducer', () => {
  it('starts from the existing display behavior without storing timeline authority', () => {
    expect(initialPreviewModeState).toEqual({
      orientation: null,
      subtitlesVisible: true,
      genericUiMaskVisible: false,
      thumbnailReduced: false,
      mobilePreview: false,
      audioOnly: false,
    })
    expect(Object.keys(initialPreviewModeState)).not.toContain(
      'currentFrameSequence',
    )
    expect(JSON.stringify(initialPreviewModeState)).not.toMatch(
      /presentationTime|sourceTime|proxyTime|seek/i,
    )
  })

  it('switches every display axis independently', () => {
    const actions: readonly PreviewModeAction[] = [
      { type: 'orientationChanged', orientation: 'landscape' },
      { type: 'orientationChanged', orientation: 'portrait' },
      { type: 'subtitlesVisibilityChanged', visible: false },
      { type: 'genericUiMaskVisibilityChanged', visible: true },
      { type: 'thumbnailReductionChanged', reduced: true },
      { type: 'mobilePreviewChanged', enabled: true },
      { type: 'audioOnlyChanged', enabled: true },
    ]

    const result = actions.reduce<PreviewModeState>(
      previewModeReducer,
      initialPreviewModeState,
    )

    expect(result).toEqual({
      orientation: 'portrait',
      subtitlesVisible: false,
      genericUiMaskVisible: true,
      thumbnailReduced: true,
      mobilePreview: true,
      audioOnly: true,
    })
    expect(initialPreviewModeState.orientation).toBeNull()
  })
})