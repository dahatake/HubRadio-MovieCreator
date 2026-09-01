import { describe, expect, it } from 'vitest'

import type { PlayerExactFrameSelection } from '../player'
import {
  parseThumbnailCancelResponse,
  parseThumbnailGenerateResponse,
  parseThumbnailReadResponse,
} from './thumbnailProtocol'
import {
  initialThumbnailState,
  selectionMatchesSnapshot,
  selectThumbnailTemplate,
  thumbnailStateReducer,
  type ThumbnailGenerationExpectation,
  type ThumbnailState,
} from './thumbnailState'
import {
  createThumbnailCancelResponseValue,
  createThumbnailGenerateResponseValue,
  createThumbnailReadResponseValue,
  thumbnailGenerationId,
  thumbnailJobId,
  thumbnailPlayerEvidenceDigest,
  thumbnailProjectId,
  thumbnailRequestId,
  thumbnailSourceAssetId,
} from './thumbnailTestFixture'

const secondRequestId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'

describe('thumbnail state', () => {
  it('accepts read evidence only when player project, source, and digest bindings match', () => {
    const selection = createSelection()
    const requested = requestRead(initialThumbnailState, selection)
    const response = requireReadResponse(createThumbnailReadResponseValue())
    const ready = thumbnailStateReducer(requested, {
      type: 'readReceived',
      response,
    })

    expect(ready.status).toBe('ready')
    expect(ready.snapshot).not.toBeNull()
    expect(ready.selectedTemplateId).toBe(
      response.result.status === 'available'
        ? response.result.snapshot.templates[0]?.templateId
        : null,
    )
    expect(selectionMatchesSnapshot(selection, ready.snapshot)).toBe(true)

    const mismatched = requestRead(initialThumbnailState, {
      ...selection,
      playerEvidenceDigest: 'f'.repeat(64),
    })
    expect(
      thumbnailStateReducer(mismatched, {
        type: 'readReceived',
        response,
      }).status,
    ).toBe('mismatched')
  })

  it('accepts source PTS only inside the half-open presentation range', () => {
    const selection = createSelection()
    const ready = createReadyState(selection)
    expect(selectionMatchesSnapshot(selection, ready.snapshot)).toBe(true)

    const exclusiveEnd: PlayerExactFrameSelection = {
      ...selection,
      frame: {
        ...selection.frame,
        sourcePresentationTime: { value: '7', rate: '60' },
      },
    }
    const beforeStart: PlayerExactFrameSelection = {
      ...selection,
      frame: {
        ...selection.frame,
        sourcePresentationTime: { value: '-11', rate: '100' },
      },
    }

    expect(selectionMatchesSnapshot(exclusiveEnd, ready.snapshot)).toBe(false)
    expect(selectionMatchesSnapshot(beforeStart, ready.snapshot)).toBe(false)
  })

  it('accepts generated output only when every source, exact PTS, template, and approval binding matches', () => {
    const selection = createSelection()
    const ready = createReadyState(selection)
    const expectation = createExpectation(ready, selection)
    const generating = thumbnailStateReducer(ready, {
      type: 'generationRequested',
      requestId: thumbnailRequestId,
      expectation,
    })
    const generated = thumbnailStateReducer(generating, {
      type: 'generationReceived',
      response: requireGenerateResponse(createThumbnailGenerateResponseValue()),
    })

    expect(generated.status).toBe('generated')
    expect(generated.output?.generationId).toBe(thumbnailGenerationId)
    expect(generated.output?.selection.sourcePresentationTime).toEqual({
      value: '-1',
      rate: '20',
    })

    const mutations: Array<(value: GenerateValue) => void> = [
      (value) => {
        value.result.output.source.ptsIndexDigest = 'd'.repeat(64)
      },
      (value) => {
        value.result.output.source.frameMapDigest = 'd'.repeat(64)
      },
      (value) => {
        value.result.output.selection.proxyFrameSequence = 2
      },
      (value) => {
        value.result.output.selection.sourceFrameSequence = 8
      },
      (value) => {
        value.result.output.selection.sourcePresentationTime = {
          value: '0',
          rate: '1',
        }
      },
      (value) => {
        value.result.output.template.approvalDigest = 'd'.repeat(64)
      },
      (value) => {
        value.result.output.template.templateVersionDigest = 'd'.repeat(64)
      },
      (value) => {
        value.result.output.template.renderProfileDigest = 'd'.repeat(64)
      },
      (value) => {
        value.result.output.template.layoutSpecDigest = 'd'.repeat(64)
      },
      (value) => {
        value.result.output.width = 640
      },
    ]
    for (const mutate of mutations) {
      const changed = cloneGenerateValue()
      mutate(changed)
      const parsed = requireGenerateResponse(changed)
      const rejected = thumbnailStateReducer(generating, {
        type: 'generationReceived',
        response: parsed,
      })
      expect(rejected.status).toBe('mismatched')
      expect(rejected.output).toBeNull()
      expect(rejected.snapshot).toBeNull()
    }
  })

  it('keeps one operation pending across cancel races and ignores late or unrelated responses', () => {
    const selection = createSelection()
    const ready = createReadyState(selection)
    const generating = thumbnailStateReducer(ready, {
      type: 'generationRequested',
      requestId: thumbnailRequestId,
      expectation: createExpectation(ready, selection),
    })
    const cancelling = thumbnailStateReducer(generating, {
      type: 'cancelRequested',
      requestId: secondRequestId,
      generationId: thumbnailGenerationId,
    })

    expect(cancelling.status).toBe('cancelling')
    expect(cancelling.pendingGeneration?.generationId).toBe(thumbnailGenerationId)
    expect(
      thumbnailStateReducer(cancelling, {
        type: 'cancelRequested',
        requestId: thumbnailRequestId,
        generationId: thumbnailGenerationId,
      }),
    ).toBe(cancelling)

    const late = createThumbnailCancelResponseValue('requested')
    late.requestId = thumbnailRequestId
    expect(
      thumbnailStateReducer(cancelling, {
        type: 'cancelReceived',
        response: requireCancelResponse(late),
      }),
    ).toBe(cancelling)

    const accepted = createThumbnailCancelResponseValue('requested')
    accepted.requestId = secondRequestId
    const cancelAccepted = thumbnailStateReducer(cancelling, {
      type: 'cancelReceived',
      response: requireCancelResponse(accepted),
    })
    expect(cancelAccepted.status).toBe('generating')
    expect(cancelAccepted.cancelStatus).toBe('requested')
    expect(cancelAccepted.pendingGeneration).not.toBeNull()

    const cancelledResponse = {
      version: 1,
      requestId: thumbnailRequestId,
      method: 'thumbnail.generate',
      result: { status: 'cancelled', output: null },
    }
    const cancelled = thumbnailStateReducer(cancelAccepted, {
      type: 'generationReceived',
      response: requireGenerateResponse(cancelledResponse),
    })
    expect(cancelled.status).toBe('cancelled')
    expect(cancelled.pendingGeneration).toBeNull()
  })

  it('retains the exact generation identity only for an identical send retry', () => {
    const selection = createSelection()
    const ready = createReadyState(selection)
    const expectation = createExpectation(ready, selection)
    const generating = thumbnailStateReducer(ready, {
      type: 'generationRequested',
      requestId: thumbnailRequestId,
      expectation,
    })
    const failed = thumbnailStateReducer(generating, {
      type: 'generationSendFailed',
      requestId: thumbnailRequestId,
    })

    expect(failed.status).toBe('bridgeSendFailed')
    expect(failed.retryGeneration?.generationId).toBe(thumbnailGenerationId)
    expect(failed.retryGeneration?.frame.sourcePresentationTime).toEqual({
      value: '-1',
      rate: '20',
    })

    const changedSelection: PlayerExactFrameSelection = {
      ...selection,
      frame: {
        ...selection.frame,
        proxyFrameSequence: 2,
      },
    }
    expect(
      selectionMatchesSnapshot(changedSelection, failed.snapshot),
    ).toBe(true)
    expect(failed.retryGeneration?.frame.proxyFrameSequence).toBe(1)
  })

  it('clears stale trusted evidence and never keeps a previous output', () => {
    const selection = createSelection()
    const ready = createReadyState(selection)
    const generating = thumbnailStateReducer(ready, {
      type: 'generationRequested',
      requestId: thumbnailRequestId,
      expectation: createExpectation(ready, selection),
    })
    const stale = thumbnailStateReducer(generating, {
      type: 'generationReceived',
      response: requireGenerateResponse({
        version: 1,
        requestId: thumbnailRequestId,
        method: 'thumbnail.generate',
        result: { status: 'stale', output: null },
      }),
    })

    expect(stale.status).toBe('stale')
    expect(stale.snapshot).toBeNull()
    expect(stale.selectedTemplateId).toBeNull()
    expect(stale.output).toBeNull()
  })
})

function requestRead(
  state: ThumbnailState,
  selection: PlayerExactFrameSelection,
): ThumbnailState {
  return thumbnailStateReducer(state, {
    type: 'readRequested',
    requestId: thumbnailRequestId,
    binding: {
      jobId: selection.jobId,
      projectId: selection.projectId,
      sourceAssetId: selection.sourceAssetId,
      playerEvidenceDigest: selection.playerEvidenceDigest,
    },
  })
}

function createReadyState(
  selection: PlayerExactFrameSelection,
): ThumbnailState {
  return thumbnailStateReducer(requestRead(initialThumbnailState, selection), {
    type: 'readReceived',
    response: requireReadResponse(createThumbnailReadResponseValue()),
  })
}

function createExpectation(
  state: ThumbnailState,
  selection: PlayerExactFrameSelection,
): ThumbnailGenerationExpectation {
  if (state.snapshot === null) {
    throw new Error('Expected a thumbnail snapshot.')
  }
  const template = selectThumbnailTemplate(state)
  if (template === null) {
    throw new Error('Expected an approved thumbnail template.')
  }
  return {
    jobId: selection.jobId,
    generationId: thumbnailGenerationId,
    snapshot: state.snapshot,
    template,
    frame: selection.frame,
  }
}

function createSelection(): PlayerExactFrameSelection & {
  frame: PlayerExactFrameSelection['frame']
} {
  return {
    jobId: thumbnailJobId,
    projectId: thumbnailProjectId,
    sourceAssetId: thumbnailSourceAssetId,
    playerEvidenceDigest: thumbnailPlayerEvidenceDigest,
    frame: {
      proxyFrameSequence: 1,
      proxyPresentationTime: { value: '1001', rate: '30000' },
      sourceFrameSequence: 7,
      sourcePresentationTime: { value: '-1', rate: '20' },
      outputPresentationTime: null,
      proxyDuration: { value: '1', rate: '30' },
      waveformChannels: [],
      spriteCell: null,
    },
  }
}

function requireReadResponse(value: unknown) {
  const response = parseThumbnailReadResponse(value)
  if (response === null) {
    throw new Error('Expected a valid thumbnail read response.')
  }
  return response
}

function requireGenerateResponse(value: unknown) {
  const response = parseThumbnailGenerateResponse(value)
  if (response === null) {
    throw new Error('Expected a valid thumbnail generation response.')
  }
  return response
}

function requireCancelResponse(value: unknown) {
  const response = parseThumbnailCancelResponse(value)
  if (response === null) {
    throw new Error('Expected a valid thumbnail cancellation response.')
  }
  return response
}

type GenerateValue = ReturnType<typeof cloneGenerateValue>

function cloneGenerateValue() {
  return JSON.parse(JSON.stringify(createThumbnailGenerateResponseValue())) as {
    version: number
    requestId: string
    method: string
    result: {
      status: string
      output: {
        width: number
        source: {
          ptsIndexDigest: string
          frameMapDigest: string
        }
        selection: {
          proxyFrameSequence: number
          sourceFrameSequence: number
          sourcePresentationTime: { value: string; rate: string }
        }
        template: {
          approvalDigest: string
          templateVersionDigest: string
          renderProfileDigest: string
          layoutSpecDigest: string
        }
      }
    }
  }
}
