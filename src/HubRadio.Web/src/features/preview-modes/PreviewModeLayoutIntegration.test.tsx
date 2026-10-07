import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'

import {
  LayoutPreview,
  parseLayoutBridgeResponse,
  type LayoutSnapshot,
} from '../layout'
import {
  createLayoutResponseValue,
  layoutRequestId,
} from '../layout/layoutTestFixture'
import {
  initialPreviewModeState,
  type PreviewModeState,
} from './previewModeState'

describe('preview modes with U-010 layout evidence', () => {
  it('toggles subtitles and a generic mask without changing 9:16 evidence', () => {
    const snapshot = createSnapshot()
    const display = snapshot.subtitleDisplays[0]
    if (display === undefined) {
      throw new Error('Expected subtitle display evidence.')
    }
    const previewModes: PreviewModeState = {
      ...initialPreviewModeState,
      orientation: 'landscape',
      subtitlesVisible: false,
      genericUiMaskVisible: true,
    }

    const markup = renderToStaticMarkup(
      <LayoutPreview
        snapshot={snapshot}
        variantChoice={null}
        subtitleDisplay={display}
        previewModes={previewModes}
      />,
    )

    expect(markup).toContain('data-requested-preview-orientation="landscape"')
    expect(markup).toContain('data-aspect="9:16"')
    expect(markup).toContain('data-preview-subtitles="false"')
    expect(markup).not.toContain('data-subtitle-cue=')
    expect(markup).toContain('data-mask-kind="generic-offline"')
    // FR-052: the landscape frame note is plain language and keeps the 9:16 layout unchanged.
    expect(markup).toContain(
      '横長の確認枠で表示しています。動画の配置は変わりません。',
    )
    expect(markup).not.toContain('native layout evidence')
  })

  it('makes audio-only explicit without fabricating visual output', () => {
    const snapshot = createSnapshot()
    const previewModes: PreviewModeState = {
      ...initialPreviewModeState,
      audioOnly: true,
      genericUiMaskVisible: true,
    }

    const markup = renderToStaticMarkup(
      <LayoutPreview
        snapshot={snapshot}
        variantChoice={null}
        subtitleDisplay={snapshot.subtitleDisplays[0] ?? null}
        previewModes={previewModes}
      />,
    )

    expect(markup).toContain('data-audio-only="true"')
    expect(markup).toContain('音声のみプレビュー')
    // FR-052: the audio-only note names the 9:16 aspect without internal jargon.
    expect(markup).toContain('9:16 の映像表示を隠しています')
    expect(markup).not.toContain('<svg')
    expect(markup).not.toContain('data-mask-kind=')
  })
})

function createSnapshot(): LayoutSnapshot {
  const response = parseLayoutBridgeResponse(
    createLayoutResponseValue(layoutRequestId),
  )
  if (
    response === null ||
    response.result.status !== 'available'
  ) {
    throw new Error('Expected available layout fixture evidence.')
  }
  return response.result.snapshot
}