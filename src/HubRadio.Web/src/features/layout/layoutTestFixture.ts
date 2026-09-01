import {
  LAYOUT_READ_METHOD,
  type LayoutBridgeResponse,
} from './layoutProtocol'

export const layoutRequestId = '00112233-4455-4677-8899-aabbccddeeff'
export const layoutSecondRequestId = '10112233-4455-4677-8899-aabbccddeeff'
export const layoutProjectId = '21112222-3333-4444-8555-666677778888'
export const layoutSecondProjectId = '22112222-3333-4444-8555-666677778888'
export const layoutCompositionId = '31112222-3333-4444-8555-666677778888'
export const layoutSpecId = '41112222-3333-4444-8555-666677778888'
export const layoutSourceId = '51112222-3333-4444-8555-666677778888'
export const layoutTemplateId = '61112222-3333-4444-8555-666677778888'
export const layoutCueId = '71112222-3333-4444-8555-666677778888'
export const layoutCueRevisionId = '81112222-3333-4444-8555-666677778888'
export const layoutPlacementId = '91112222-3333-4444-8555-666677778888'
export const layoutTeropId = 'a1112222-3333-4444-8555-666677778888'
export const layoutTeropRevisionId = 'b1112222-3333-4444-8555-666677778888'
export const layoutBurnedManifestId = 'c1112222-3333-4444-8555-666677778888'
export const layoutFreeManifestId = 'd1112222-3333-4444-8555-666677778888'
export const layoutBurnedGraphId = 'e1112222-3333-4444-8555-666677778888'
export const layoutFreeGraphId = 'f1112222-3333-4444-8555-666677778888'
export const layoutCompositionDigest = 'a'.repeat(64)
export const layoutProfileDigest = 'b'.repeat(64)
export const layoutTemplateDigest = 'c'.repeat(64)
export const layoutDisplayDigest = 'd'.repeat(64)
export const layoutSubtitleEvidenceDigest = 'e'.repeat(64)
export const layoutCueDigest = 'f'.repeat(64)
export const layoutTeropDigest = '0'.repeat(64)

const zero = { numerator: '0', denominator: '1' } as const
const one = { numerator: '1', denominator: '1' } as const
const half = { numerator: '1', denominator: '2' } as const
const quarter = { numerator: '1', denominator: '4' } as const
const threeQuarters = { numerator: '3', denominator: '4' } as const
const oneThird = { numerator: '1', denominator: '3' } as const
const twoThirds = { numerator: '2', denominator: '3' } as const

export function createLayoutResponseValue(
  requestId = layoutRequestId,
  method: 'layout.read' | 'layout.apply' = LAYOUT_READ_METHOD,
) {
  return {
    version: 1,
    requestId,
    method,
    result: {
      status: 'available',
      snapshot: {
        projectId: layoutProjectId,
        revision: 3,
        compositionVersionId: layoutCompositionId,
        compositionVersionDigest: layoutCompositionDigest,
        persistence: 'notPersisted',
        output: {
          width: 1080,
          height: 1920,
          aspectWidth: 9,
          aspectHeight: 16,
        },
        layout: {
          layoutSpecId,
          regions: [
            {
              name: 'video',
              role: 'video',
              rect: { x: zero, y: zero, width: one, height: one },
              pixels: { x: 0, y: 0, width: 1080, height: 1920 },
            },
            {
              name: 'subtitle',
              role: 'subtitle',
              rect: {
                x: zero,
                y: threeQuarters,
                width: one,
                height: quarter,
              },
              pixels: { x: 0, y: 1440, width: 1080, height: 480 },
            },
            {
              name: 'graphic',
              role: 'graphic',
              rect: {
                x: zero,
                y: zero,
                width: one,
                height: quarter,
              },
              pixels: { x: 0, y: 0, width: 1080, height: 480 },
            },
          ],
          safeZones: [
            {
              name: 'output-frame-bounds',
              rect: { x: zero, y: zero, width: one, height: one },
              pixels: { x: 0, y: 0, width: 1080, height: 1920 },
            },
          ],
        },
        sources: [
          {
            sourceAssetId: layoutSourceId,
            displayWidth: 1080,
            displayHeight: 1920,
          },
        ],
        templates: [
          {
            templateId: layoutTemplateId,
            name: '承認済み縦型テンプレート',
            versionDigest: layoutTemplateDigest,
          },
        ],
        manualSelection: {
          mode: 'singleSubjectCrop',
          status: 'resolved',
          placements: [
            {
              sourceAssetId: layoutSourceId,
              sourceWidth: 1080,
              sourceHeight: 1920,
              sourceCrop: {
                rect: {
                  x: oneThird,
                  y: zero,
                  width: twoThirds,
                  height: one,
                },
                pixels: { x: 360, y: 0, width: 720, height: 1920 },
              },
              subject: {
                rect: {
                  x: oneThird,
                  y: quarter,
                  width: oneThird,
                  height: half,
                },
                pixels: { x: 360, y: 480, width: 360, height: 960 },
              },
              destination: {
                rect: {
                  x: oneThird,
                  y: zero,
                  width: twoThirds,
                  height: one,
                },
                pixels: { x: 360, y: 0, width: 720, height: 1920 },
              },
              destinationSlot: 'singleSubject',
              semanticRole: 'subject',
              treatment: 'standard',
            },
          ],
          template: null,
        },
        keyframeTracks: [
          {
            sourceAssetId: layoutSourceId,
            destinationSlot: 'singleSubject',
            sourceWidth: 1080,
            sourceHeight: 1920,
            outputWidth: 1080,
            outputHeight: 1920,
            range: {
              start: { value: '0', rate: '1' },
              duration: { value: '2', rate: '1' },
            },
            segments: [
              {
                range: {
                  start: { value: '0', rate: '1' },
                  duration: { value: '2', rate: '1' },
                },
                keyframes: [
                  {
                    time: { value: '0', rate: '1' },
                    sourceCrop: { x: zero, y: zero, width: half, height: half },
                    destination: { x: zero, y: zero, width: half, height: half },
                    interpolation: 'linear',
                  },
                  {
                    time: { value: '2', rate: '1' },
                    sourceCrop: { x: half, y: half, width: half, height: half },
                    destination: { x: half, y: half, width: half, height: half },
                    interpolation: 'hold',
                  },
                ],
              },
            ],
            evaluation: {
              requestedTime: { value: '1', rate: '1' },
              sourceCrop: { x: quarter, y: quarter, width: half, height: half },
              destination: { x: quarter, y: quarter, width: half, height: half },
              sourcePixels: { x: 270, y: 480, width: 540, height: 960 },
              destinationPixels: { x: 270, y: 480, width: 540, height: 960 },
              segmentIndex: 0,
            },
          },
        ],
        subtitles: [
          {
            cueId: layoutCueId,
            revisionId: layoutCueRevisionId,
            revisionDigest: layoutCueDigest,
            compositionVersionId: layoutCompositionId,
            text: '字幕テキスト、です。',
            style: 'normal',
            outputRange: {
              start: { value: '1', rate: '1' },
              duration: { value: '1', rate: '1' },
            },
            sourceRange: {
              start: { value: '0', rate: '1' },
              duration: { value: '2', rate: '1' },
            },
            revisionCount: 2,
            layoutResolution: {
              status: 'resolved',
              layoutSpecId,
              profileVersionDigest: layoutProfileDigest,
              globalHardConstraints: [],
              evaluations: [
                {
                  candidateId: layoutPlacementId,
                  regionName: 'subtitle',
                  rect: {
                    x: zero,
                    y: threeQuarters,
                    width: one,
                    height: quarter,
                  },
                  pixels: { x: 0, y: 1440, width: 1080, height: 480 },
                  hardConstraints: [],
                  softConstraints: [],
                  softCost: '0',
                },
              ],
              selectedCandidateId: layoutPlacementId,
            },
          },
        ],
        subtitleDisplays: [
          {
            profileVersionDigest: layoutDisplayDigest,
            cueMode: 'full',
            punctuationDisplay: 'preserve',
            selectedCueIds: [],
            sourceCueCount: 1,
            displayedCues: [
              {
                cueId: layoutCueId,
                text: '字幕テキスト、です。',
                textChanged: false,
              },
            ],
            omittedCueIds: [],
          },
          {
            profileVersionDigest: '1'.repeat(64),
            cueMode: 'full',
            punctuationDisplay: 'hideJapaneseCommaAndPeriod',
            selectedCueIds: [],
            sourceCueCount: 1,
            displayedCues: [
              {
                cueId: layoutCueId,
                text: '字幕テキストです',
                textChanged: true,
              },
            ],
            omittedCueIds: [],
          },
          {
            profileVersionDigest: '2'.repeat(64),
            cueMode: 'selected',
            punctuationDisplay: 'preserve',
            selectedCueIds: [layoutCueId],
            sourceCueCount: 1,
            displayedCues: [
              {
                cueId: layoutCueId,
                text: '字幕テキスト、です。',
                textChanged: false,
              },
            ],
            omittedCueIds: [],
          },
          {
            profileVersionDigest: '3'.repeat(64),
            cueMode: 'selected',
            punctuationDisplay: 'hideJapaneseCommaAndPeriod',
            selectedCueIds: [layoutCueId],
            sourceCueCount: 1,
            displayedCues: [
              {
                cueId: layoutCueId,
                text: '字幕テキストです',
                textChanged: true,
              },
            ],
            omittedCueIds: [],
          },
        ],
        terops: [
          {
            cueId: layoutTeropId,
            revisionId: layoutTeropRevisionId,
            revisionDigest: layoutTeropDigest,
            compositionVersionId: layoutCompositionId,
            sourceKind: 'manualEditorial',
            text: 'テロップ',
            style: 'emphasis',
            regionName: 'graphic',
            outputRange: {
              start: { value: '2', rate: '1' },
              duration: { value: '1', rate: '1' },
            },
            revisionCount: 1,
          },
        ],
        variantPair: {
          compositionVersionId: layoutCompositionId,
          compositionVersionDigest: layoutCompositionDigest,
          displayProfileDigest: layoutDisplayDigest,
          subtitleEvidenceDigest: layoutSubtitleEvidenceDigest,
          burnedIn: {
            deliveryManifestId: layoutBurnedManifestId,
            renderGraphId: layoutBurnedGraphId,
          },
          subtitleFree: {
            deliveryManifestId: layoutFreeManifestId,
            renderGraphId: layoutFreeGraphId,
            hasSidecar: true,
          },
        },
      },
    },
  }
}

export function setLayoutFixtureSubtitleText(
  value: ReturnType<typeof createLayoutResponseValue>,
  text: string,
): void {
  value.result.snapshot.subtitles[0]!.text = text
  for (const display of value.result.snapshot.subtitleDisplays) {
    const projected = display.punctuationDisplay === 'preserve'
      ? text
      : text.replace(/[、。､｡]/gu, '').trim()
    display.displayedCues[0]!.text = projected
    display.displayedCues[0]!.textChanged = projected !== text
  }
}

export function createLayoutFailureValue(
  status: Exclude<LayoutBridgeResponse['result']['status'], 'available'>,
  requestId = layoutRequestId,
  method: 'layout.read' | 'layout.apply' = LAYOUT_READ_METHOD,
) {
  return {
    version: 1,
    requestId,
    method,
    result: { status, snapshot: null },
  }
}
