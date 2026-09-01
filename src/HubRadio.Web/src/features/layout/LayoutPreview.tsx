import type {
  LayoutPixelRect,
  LayoutSnapshot,
  LayoutSubtitleDisplay,
  LayoutSubtitleCue,
} from './layoutProtocol'

export type LayoutVariantChoice = 'burnedIn' | 'subtitleFree' | null

export interface LayoutPreviewProps {
  readonly snapshot: LayoutSnapshot
  readonly variantChoice: LayoutVariantChoice
  readonly subtitleDisplay: LayoutSubtitleDisplay | null
}

export function LayoutPreview({
  snapshot,
  variantChoice,
  subtitleDisplay,
}: LayoutPreviewProps) {
  const showSubtitles = variantChoice !== 'subtitleFree'
  return (
    <figure className="layout-preview">
      <svg
        role="img"
        aria-labelledby="layout-preview-title layout-preview-description"
        viewBox={`0 0 ${snapshot.output.width} ${snapshot.output.height}`}
        data-native-width={snapshot.output.width}
        data-native-height={snapshot.output.height}
        data-aspect="9:16"
        data-variant={variantChoice ?? 'editor'}
        data-subtitle-profile={
          subtitleDisplay?.profileVersionDigest ?? 'unavailable'
        }
      >
        <title id="layout-preview-title">9対16 レイアウト preview</title>
        <desc id="layout-preview-description">
          ネイティブが返した整数 pixel rectangle を同じ viewBox 座標で表示します。
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
      <figcaption>
        {snapshot.output.width} × {snapshot.output.height} px・9:16。表示は native pixel evidence を SVG viewBox へ直接投影し、小数座標を authority にしません。
      </figcaption>
    </figure>
  )
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
