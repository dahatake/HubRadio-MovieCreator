import type {
  LayoutPixelRect,
  LayoutSnapshot,
  LayoutSubtitleDisplay,
  LayoutSubtitleCue,
} from './layoutProtocol'
import { GenericUiMask } from '../preview-modes/GenericUiMask'
import {
  initialPreviewModeState,
  type PreviewModeState,
} from '../preview-modes/previewModeState'

export type LayoutVariantChoice = 'burnedIn' | 'subtitleFree' | null

export interface LayoutPreviewProps {
  readonly snapshot: LayoutSnapshot
  readonly variantChoice: LayoutVariantChoice
  readonly subtitleDisplay: LayoutSubtitleDisplay | null
  readonly previewModes?: PreviewModeState
}

export function LayoutPreview({
  snapshot,
  variantChoice,
  subtitleDisplay,
  previewModes = initialPreviewModeState,
}: LayoutPreviewProps) {
  const showSubtitles =
    previewModes.subtitlesVisible && variantChoice !== 'subtitleFree'
  const aspect = `${snapshot.output.aspectWidth}:${snapshot.output.aspectHeight}`
  const aspectRatio = `${snapshot.output.width} / ${snapshot.output.height}`
  return (
    <figure
      className="layout-preview"
      data-requested-preview-orientation={previewModes.orientation ?? 'source'}
      data-preview-subtitles={String(previewModes.subtitlesVisible)}
      data-audio-only={String(previewModes.audioOnly)}
    >
      <div className="layout-preview-stage">
        {previewModes.audioOnly ? (
          <div className="audio-only-placeholder" role="note" style={{ aspectRatio }}>
            <strong>音声のみプレビュー</strong>
            <span>{aspect} の映像表示を隠しています。</span>
          </div>
        ) : (
          <>
            <svg
              role="img"
              aria-labelledby="layout-preview-title layout-preview-description"
              viewBox={`0 0 ${snapshot.output.width} ${snapshot.output.height}`}
              style={{ aspectRatio }}
              data-native-width={snapshot.output.width}
              data-native-height={snapshot.output.height}
              data-aspect={aspect}
              data-subtitle-only={snapshot.subtitleOnly === true ? 'true' : 'false'}
              data-variant={variantChoice ?? 'editor'}
              data-subtitle-profile={
                subtitleDisplay?.profileVersionDigest ?? 'unavailable'
              }
            >
              <title id="layout-preview-title">{`${aspect} ${snapshot.subtitleOnly ? '字幕配置' : 'レイアウト'} preview`}</title>
              <desc id="layout-preview-description">
                字幕・映像の位置を、作成される動画と同じ比率で表示します。
              </desc>
              <rect
                className="layout-preview-background"
                x={0}
                y={0}
                width={snapshot.output.width}
                height={snapshot.output.height}
              />
              {snapshot.layout.regions.map((region) => (
                <rect
                  key={`region-${region.name}`}
                  className={`layout-preview-region role-${region.role}`}
                  {...svgRect(region.pixels)}
                  data-region={region.name}
                >
                  <title>{`${region.name} (${region.role})`}</title>
                </rect>
              ))}
              {snapshot.manualSelection?.placements.map((placement) => (
                <rect
                  key={`manual-${placement.destinationSlot}`}
                  className="layout-preview-manual"
                  {...svgRect(placement.destination.pixels)}
                  data-destination-slot={placement.destinationSlot}
                >
                  <title>{`manual destination: ${placement.destinationSlot}`}</title>
                </rect>
              ))}
              {snapshot.keyframeTracks.map((track) => (
                <rect
                  key={`keyframe-${track.sourceAssetId}-${track.destinationSlot}`}
                  className="layout-preview-keyframe"
                  {...svgRect(track.evaluation.destinationPixels)}
                  data-keyframe-slot={track.destinationSlot}
                >
                  <title>{`keyframe evaluation: ${track.destinationSlot}`}</title>
                </rect>
              ))}
              {showSubtitles && subtitleDisplay?.displayedCues.map((displayedCue) => {
                const sourceCue = snapshot.subtitles.find(
                  (cue) => cue.cueId === displayedCue.cueId,
                )
                return sourceCue === undefined ? null : (
                  <SubtitleOverlay
                    key={displayedCue.cueId}
                    cue={sourceCue}
                    displayText={displayedCue.text}
                  />
                )
              })}
              {snapshot.terops.map((cue) => {
                const region = snapshot.layout.regions.find(
                  (candidate) => candidate.name === cue.regionName,
                )
                return region === undefined ? null : (
                  <g key={cue.cueId} data-terop-cue={cue.cueId}>
                    <rect
                      className="layout-preview-terop"
                      {...svgRect(region.pixels)}
                    />
                    <text
                      className="layout-preview-terop-text"
                      x={region.pixels.x}
                      y={region.pixels.y}
                      dominantBaseline="hanging"
                    >
                      {cue.text}
                    </text>
                  </g>
                )
              })}
              {snapshot.layout.safeZones.map((safeZone) => (
                <rect
                  key={safeZone.name}
                  className="layout-preview-safe-zone"
                  {...svgRect(safeZone.pixels)}
                  data-safe-zone={safeZone.name}
                >
                  <title>{safeZone.name}</title>
                </rect>
              ))}
            </svg>
            <GenericUiMask
              visible={previewModes.genericUiMaskVisible}
              audioOnly={previewModes.audioOnly}
            />
          </>
        )}
      </div>
      <figcaption>
        {snapshot.output.width} × {snapshot.output.height} px・{aspect}。枠は作成される動画での字幕・映像の位置を示します。{' '}
        {snapshot.subtitleOnly && '字幕・グラフィックの配置だけを表示し、映像の画角は投影していません。 '}
        {orientationDescription(previewModes.orientation, aspect)}
      </figcaption>
    </figure>
  )
}

function orientationDescription(
  orientation: PreviewModeState['orientation'],
  aspect: string,
): string {
  switch (orientation) {
    case null:
      return `表示枠は動画の比率（${aspect}）に合わせています。`
    case 'landscape':
      return `横長の確認枠で表示しています。動画の配置は変わりません。`
    case 'portrait':
      return `縦型の確認枠で表示しています。動画の配置は変わりません。`
  }
}

function SubtitleOverlay({
  cue,
  displayText,
}: Readonly<{
  cue: LayoutSubtitleCue
  displayText: string
}>) {
  if (
    cue.layoutResolution.status !== 'resolved' ||
    cue.layoutResolution.selectedCandidateId === null
  ) {
    return null
  }
  const candidate = cue.layoutResolution.evaluations.find(
    (item) => item.candidateId === cue.layoutResolution.selectedCandidateId,
  )
  if (candidate?.pixels === null || candidate?.pixels === undefined) {
    return null
  }
  return (
    <g data-subtitle-cue={cue.cueId}>
      <rect
        className="layout-preview-subtitle"
        {...svgRect(candidate.pixels)}
      />
      <text
        className="layout-preview-subtitle-text"
        x={candidate.pixels.x}
        y={candidate.pixels.y}
        dominantBaseline="hanging"
      >
        {displayText}
      </text>
    </g>
  )
}

function svgRect(rect: LayoutPixelRect) {
  return {
    x: rect.x,
    y: rect.y,
    width: rect.width,
    height: rect.height,
  }
}
