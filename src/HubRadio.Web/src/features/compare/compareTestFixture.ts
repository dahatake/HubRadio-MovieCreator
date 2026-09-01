import { COMPARISON_READ_METHOD } from './compareProtocol'

export const comparisonRequestId = '00112233-4455-6677-8899-aabbccddeeff'
export const comparisonSecondRequestId = '10112233-4455-6677-8899-aabbccddeeff'
export const comparisonLeftVersionId = '11112222-3333-4444-8555-666677778888'
export const comparisonRightVersionId = '21112222-3333-4444-8555-666677778888'
export const comparisonLeftDigest = 'a'.repeat(64)
export const comparisonRightDigest = 'b'.repeat(64)
export const comparisonSourceElementId = '31112222-3333-4444-8555-666677778888'
export const comparisonMovedElementId = '41112222-3333-4444-8555-666677778888'
export const comparisonRemovedElementId = '51112222-3333-4444-8555-666677778888'
export const comparisonAddedElementId = '61112222-3333-4444-8555-666677778888'
export const comparisonSourceSpanId = '71112222-3333-4444-8555-666677778888'
export const comparisonAddedSourceSpanId = '81112222-3333-4444-8555-666677778888'
export const comparisonPreviousSuggestionId = '91112222-3333-4444-8555-666677778888'
export const comparisonCurrentSuggestionId = 'a1112222-3333-4444-8555-666677778888'
export const comparisonAddedSuggestionId = 'b1112222-3333-4444-8555-666677778888'
export const hostileComparisonReason =
  '<img src=x onerror="window.__comparison_attack=true"> & exact reason'

export function createComparisonResponseValue(
  requestId = comparisonRequestId,
) {
  return {
    version: 1,
    requestId,
    method: COMPARISON_READ_METHOD,
    result: {
      status: 'available',
      comparison: {
        left: {
          versionId: comparisonLeftVersionId,
          versionDigest: comparisonLeftDigest,
          createdAt: '2036-08-09T10:11:12.0000000+00:00',
          changeReason: 'Initial exact composition.',
          playbackDuration: { value: '9', rate: '2' },
          usedSourceRanges: [
            {
              elementId: comparisonSourceElementId,
              sourceSpanId: comparisonSourceSpanId,
              sourceRange: {
                start: { value: '21', rate: '2' },
                duration: { value: '2', rate: '1' },
              },
              outputRange: {
                start: { value: '0', rate: '1' },
                duration: { value: '2', rate: '1' },
              },
            },
          ],
          features: unavailableFeatures(),
        },
        right: {
          versionId: comparisonRightVersionId,
          versionDigest: comparisonRightDigest,
          createdAt: '2036-08-09T10:12:12.0000000+00:00',
          changeReason: hostileComparisonReason,
          playbackDuration: { value: '13', rate: '3' },
          usedSourceRanges: [
            {
              elementId: comparisonSourceElementId,
              sourceSpanId: comparisonSourceSpanId,
              sourceRange: {
                start: { value: '21', rate: '2' },
                duration: { value: '2', rate: '1' },
              },
              outputRange: {
                start: { value: '0', rate: '1' },
                duration: { value: '2', rate: '1' },
              },
            },
            {
              elementId: comparisonAddedElementId,
              sourceSpanId: comparisonAddedSourceSpanId,
              sourceRange: {
                start: { value: '20', rate: '1' },
                duration: { value: '4', rate: '3' },
              },
              outputRange: {
                start: { value: '3', rate: '1' },
                duration: { value: '4', rate: '3' },
              },
            },
          ],
          features: unavailableFeatures(),
        },
        elementChanges: [
          {
            kind: 'removed',
            elementId: comparisonRemovedElementId,
            previousIndex: 2,
            currentIndex: null,
          },
          {
            kind: 'added',
            elementId: comparisonAddedElementId,
            previousIndex: null,
            currentIndex: 2,
          },
          {
            kind: 'moved',
            elementId: comparisonMovedElementId,
            previousIndex: 1,
            currentIndex: 0,
          },
          {
            kind: 'modified',
            elementId: comparisonSourceElementId,
            previousIndex: 0,
            currentIndex: 1,
          },
        ],
        adoptionChanges: [
          {
            kind: 'adopted',
            elementId: comparisonAddedElementId,
            previousSuggestionId: null,
            currentSuggestionId: comparisonAddedSuggestionId,
          },
          {
            kind: 'replaced',
            elementId: comparisonSourceElementId,
            previousSuggestionId: comparisonPreviousSuggestionId,
            currentSuggestionId: comparisonCurrentSuggestionId,
          },
        ],
      },
    },
  }
}

function unavailableFeatures() {
  return {
    openingHook: 'unavailable',
    subtitleStyle: 'unavailable',
    telopCount: 'unavailable',
    transitions: 'unavailable',
    effects: 'unavailable',
    callToAction: 'unavailable',
    destination: 'unavailable',
    warnings: 'unavailable',
  }
}