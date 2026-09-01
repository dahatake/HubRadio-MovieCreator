import type { PlayerExactFrameSelection } from '../player'
import type {
  ThumbnailCancelResponse,
  ThumbnailCancellationOutcome,
  ThumbnailGenerateResponse,
  ThumbnailOutput,
  ThumbnailReadResponse,
  ThumbnailSnapshot,
  ThumbnailTemplateSnapshot,
} from './thumbnailProtocol'

export type ThumbnailStatus =
  | 'idle'
  | 'loading'
  | 'ready'
  | 'generating'
  | 'cancelling'
  | 'generated'
  | 'forbidden'
  | 'unavailable'
  | 'stale'
  | 'mismatched'
  | 'invalidTime'
  | 'rightsNotApproved'
  | 'templateUnavailable'
  | 'cancelled'
  | 'conflict'
  | 'failed'
  | 'bridgeUnavailable'
  | 'bridgeSendFailed'

export type ThumbnailCancelStatus =
  | ThumbnailCancellationOutcome
  | 'forbidden'
  | 'unavailable'
  | 'sendFailed'
  | null

export interface ThumbnailReadBinding {
  readonly jobId: string
  readonly projectId: string
  readonly sourceAssetId: string
  readonly playerEvidenceDigest: string
}

export interface ThumbnailGenerationExpectation {
  readonly jobId: string
  readonly generationId: string
  readonly snapshot: ThumbnailSnapshot
  readonly template: ThumbnailTemplateSnapshot
  readonly frame: PlayerExactFrameSelection['frame']
}

export interface PendingThumbnailGeneration
  extends ThumbnailGenerationExpectation {
  readonly requestId: string
}

export interface ThumbnailState {
  readonly status: ThumbnailStatus
  readonly pendingRead: Readonly<{
    requestId: string
    binding: ThumbnailReadBinding
  }> | null
  readonly pendingGeneration: PendingThumbnailGeneration | null
  readonly retryGeneration: ThumbnailGenerationExpectation | null
  readonly pendingCancel: Readonly<{
    requestId: string
    generationId: string
  }> | null
  readonly cancelStatus: ThumbnailCancelStatus
  readonly snapshot: ThumbnailSnapshot | null
  readonly selectedTemplateId: string | null
  readonly output: ThumbnailOutput | null
}

export type ThumbnailAction =
  | Readonly<{
      type: 'readRequested'
      requestId: string
      binding: ThumbnailReadBinding
    }>
  | Readonly<{ type: 'readReceived'; response: ThumbnailReadResponse }>
  | Readonly<{ type: 'templateSelected'; templateId: string }>
  | Readonly<{
      type: 'generationRequested'
      requestId: string
      expectation: ThumbnailGenerationExpectation
    }>
  | Readonly<{
      type: 'generationReceived'
      response: ThumbnailGenerateResponse
    }>
  | Readonly<{
      type: 'cancelRequested'
      requestId: string
      generationId: string
    }>
  | Readonly<{ type: 'cancelReceived'; response: ThumbnailCancelResponse }>
  | Readonly<{ type: 'readSendFailed'; requestId: string }>
  | Readonly<{ type: 'generationSendFailed'; requestId: string }>
  | Readonly<{ type: 'cancelSendFailed'; requestId: string }>
  | Readonly<{ type: 'bridgeUnavailable' }>

export const initialThumbnailState: ThumbnailState = {
  status: 'idle',
  pendingRead: null,
  pendingGeneration: null,
  retryGeneration: null,
  pendingCancel: null,
  cancelStatus: null,
  snapshot: null,
  selectedTemplateId: null,
  output: null,
}

export function thumbnailStateReducer(
  state: ThumbnailState,
  action: ThumbnailAction,
): ThumbnailState {
  switch (action.type) {
    case 'readRequested':
      if (state.pendingGeneration !== null) {
        return state
      }
      return {
        ...initialThumbnailState,
        status: 'loading',
        pendingRead: Object.freeze({
          requestId: action.requestId,
          binding: action.binding,
        }),
      }

    case 'readReceived': {
      const pending = state.pendingRead
      if (pending === null || action.response.requestId !== pending.requestId) {
        return state
      }
      if (action.response.result.status !== 'available') {
        return {
          ...initialThumbnailState,
          status: action.response.result.status,
        }
      }
      const snapshot = action.response.result.snapshot
      if (!snapshotMatchesReadBinding(snapshot, pending.binding)) {
        return { ...initialThumbnailState, status: 'mismatched' }
      }
      return {
        ...initialThumbnailState,
        status: 'ready',
        snapshot,
        selectedTemplateId: snapshot.templates[0]?.templateId ?? null,
      }
    }

    case 'templateSelected':
      if (
        state.snapshot === null ||
        !state.snapshot.templates.some(
          (template) => template.templateId === action.templateId,
        ) ||
        state.pendingGeneration !== null
      ) {
        return state
      }
      return {
        ...state,
        status: 'ready',
        selectedTemplateId: action.templateId,
        retryGeneration: null,
        cancelStatus: null,
        output: null,
      }

    case 'generationRequested':
      if (
        state.snapshot === null ||
        state.pendingGeneration !== null ||
        state.selectedTemplateId !== action.expectation.template.templateId ||
        !expectationUsesSnapshot(action.expectation, state.snapshot)
      ) {
        return state
      }
      return {
        ...state,
        status: 'generating',
        pendingGeneration: Object.freeze({
          requestId: action.requestId,
          ...action.expectation,
        }),
        retryGeneration: null,
        pendingCancel: null,
        cancelStatus: null,
        output: null,
      }

    case 'generationReceived': {
      const pending = state.pendingGeneration
      if (pending === null || action.response.requestId !== pending.requestId) {
        return state
      }
      if (action.response.result.status !== 'available') {
        const clearSnapshot =
          action.response.result.status === 'stale' ||
          action.response.result.status === 'mismatched' ||
          action.response.result.status === 'templateUnavailable'
        return {
          ...state,
          status: action.response.result.status,
          pendingGeneration: null,
          retryGeneration: null,
          pendingCancel: null,
          snapshot: clearSnapshot ? null : state.snapshot,
          selectedTemplateId: clearSnapshot ? null : state.selectedTemplateId,
          output: null,
        }
      }
      if (!outputMatchesExpectation(action.response.result.output, pending)) {
        return { ...initialThumbnailState, status: 'mismatched' }
      }
      return {
        ...state,
        status: 'generated',
        pendingGeneration: null,
        retryGeneration: null,
        pendingCancel: null,
        output: action.response.result.output,
      }
    }

    case 'cancelRequested':
      if (
        state.pendingGeneration?.generationId !== action.generationId ||
        state.pendingCancel !== null
      ) {
        return state
      }
      return {
        ...state,
        status: 'cancelling',
        pendingCancel: Object.freeze({
          requestId: action.requestId,
          generationId: action.generationId,
        }),
        cancelStatus: null,
      }

    case 'cancelReceived':
      if (
        state.pendingCancel === null ||
        action.response.requestId !== state.pendingCancel.requestId
      ) {
        return state
      }
      return {
        ...state,
        status: state.pendingGeneration === null ? state.status : 'generating',
        pendingCancel: null,
        cancelStatus:
          action.response.result.status === 'available'
            ? action.response.result.outcome
            : action.response.result.status,
      }

    case 'readSendFailed':
      if (state.pendingRead?.requestId !== action.requestId) {
        return state
      }
      return { ...initialThumbnailState, status: 'bridgeSendFailed' }

    case 'generationSendFailed':
      if (state.pendingGeneration?.requestId !== action.requestId) {
        return state
      }
      return {
        ...state,
        status: 'bridgeSendFailed',
        retryGeneration: copyExpectation(state.pendingGeneration),
        pendingGeneration: null,
        pendingCancel: null,
        cancelStatus: null,
        output: null,
      }

    case 'cancelSendFailed':
      if (state.pendingCancel?.requestId !== action.requestId) {
        return state
      }
      return {
        ...state,
        status: state.pendingGeneration === null ? state.status : 'generating',
        pendingCancel: null,
        cancelStatus: 'sendFailed',
      }

    case 'bridgeUnavailable':
      return { ...initialThumbnailState, status: 'bridgeUnavailable' }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}

export function selectThumbnailTemplate(
  state: ThumbnailState,
): ThumbnailTemplateSnapshot | null {
  if (state.snapshot === null || state.selectedTemplateId === null) {
    return null
  }
  return (
    state.snapshot.templates.find(
      (template) => template.templateId === state.selectedTemplateId,
    ) ?? null
  )
}

export function selectionMatchesSnapshot(
  selection: PlayerExactFrameSelection | null,
  snapshot: ThumbnailSnapshot | null,
): boolean {
  const sourcePresentationTime = selection?.frame.sourcePresentationTime
  return (
    selection !== null &&
    snapshot !== null &&
    sourcePresentationTime !== undefined &&
    selection.projectId === snapshot.projectId &&
    selection.sourceAssetId === snapshot.sourceAssetId &&
    selection.playerEvidenceDigest === snapshot.playerEvidenceDigest &&
    compareRationals(
      sourcePresentationTime,
      snapshot.sourcePresentationStart,
    ) >= 0 &&
    compareRationals(sourcePresentationTime, snapshot.sourcePresentationEnd) < 0
  )
}

export function expectationMatchesCurrent(
  expectation: ThumbnailGenerationExpectation | null,
  selection: PlayerExactFrameSelection,
  snapshot: ThumbnailSnapshot,
  template: ThumbnailTemplateSnapshot,
): boolean {
  return (
    expectation !== null &&
    expectation.jobId === selection.jobId &&
    expectation.generationId.length > 0 &&
    expectation.snapshot.evidenceDigest === snapshot.evidenceDigest &&
    expectation.snapshot.playerEvidenceDigest === snapshot.playerEvidenceDigest &&
    expectation.template.templateId === template.templateId &&
    expectation.template.approvalDigest === template.approvalDigest &&
    expectation.template.templateVersionDigest === template.templateVersionDigest &&
    expectation.template.renderProfileDigest === template.renderProfileDigest &&
    expectation.template.layoutSpecDigest === template.layoutSpecDigest &&
    framesEqual(expectation.frame, selection.frame)
  )
}

function snapshotMatchesReadBinding(
  snapshot: ThumbnailSnapshot,
  binding: ThumbnailReadBinding,
): boolean {
  return (
    snapshot.projectId === binding.projectId &&
    snapshot.sourceAssetId === binding.sourceAssetId &&
    snapshot.playerEvidenceDigest === binding.playerEvidenceDigest
  )
}

function expectationUsesSnapshot(
  expectation: ThumbnailGenerationExpectation,
  snapshot: ThumbnailSnapshot,
): boolean {
  return (
    expectation.snapshot === snapshot &&
    snapshot.templates.includes(expectation.template) &&
    expectation.jobId.length > 0 &&
    expectation.generationId.length > 0
  )
}

function outputMatchesExpectation(
  output: ThumbnailOutput,
  expectation: ThumbnailGenerationExpectation,
): boolean {
  const snapshot = expectation.snapshot
  const template = expectation.template
  const frame = expectation.frame
  return (
    output.generationId === expectation.generationId &&
    output.projectId === snapshot.projectId &&
    output.source.artifactId === snapshot.sourceArtifactId &&
    output.source.assetId === snapshot.sourceAssetId &&
    output.source.contentDigest === snapshot.sourceContentDigest &&
    output.source.ptsIndexDigest === snapshot.sourcePtsIndexDigest &&
    output.source.frameMapDigest === snapshot.proxyFrameMapDigest &&
    output.source.rightsStatus === snapshot.rights.status &&
    output.source.externalPublicationAllowed ===
      snapshot.rights.externalPublicationAllowed &&
    output.selection.proxyFrameSequence === frame.proxyFrameSequence &&
    output.selection.sourceFrameSequence === frame.sourceFrameSequence &&
    rationalsEqual(
      output.selection.sourcePresentationTime,
      frame.sourcePresentationTime,
    ) &&
    output.template.approvalId === template.approvalId &&
    output.template.approvalDigest === template.approvalDigest &&
    output.template.approvalRole === template.approvalRole &&
    output.template.approvedAt === template.approvedAt &&
    output.template.templateId === template.templateId &&
    output.template.templateVersionDigest === template.templateVersionDigest &&
    output.template.renderProfileId === template.renderProfileId &&
    output.template.renderProfileDigest === template.renderProfileDigest &&
    output.template.layoutSpecId === template.layoutSpecId &&
    output.template.layoutSpecDigest === template.layoutSpecDigest &&
    output.width === template.width &&
    output.height === template.height
  )
}

function copyExpectation(
  pending: PendingThumbnailGeneration,
): ThumbnailGenerationExpectation {
  return Object.freeze({
    jobId: pending.jobId,
    generationId: pending.generationId,
    snapshot: pending.snapshot,
    template: pending.template,
    frame: pending.frame,
  })
}

function framesEqual(
  left: PlayerExactFrameSelection['frame'],
  right: PlayerExactFrameSelection['frame'],
): boolean {
  return (
    left.proxyFrameSequence === right.proxyFrameSequence &&
    left.sourceFrameSequence === right.sourceFrameSequence &&
    rationalsEqual(left.proxyPresentationTime, right.proxyPresentationTime) &&
    rationalsEqual(left.sourcePresentationTime, right.sourcePresentationTime) &&
    nullableRationalsEqual(
      left.outputPresentationTime,
      right.outputPresentationTime,
    )
  )
}

function rationalsEqual(
  left: Readonly<{ value: string; rate: string }>,
  right: Readonly<{ value: string; rate: string }>,
): boolean {
  return left.value === right.value && left.rate === right.rate
}

function compareRationals(
  left: Readonly<{ value: string; rate: string }>,
  right: Readonly<{ value: string; rate: string }>,
): number {
  const difference =
    BigInt(left.value) * BigInt(right.rate) -
    BigInt(right.value) * BigInt(left.rate)
  return difference < 0n ? -1 : difference > 0n ? 1 : 0
}

function nullableRationalsEqual(
  left: Readonly<{ value: string; rate: string }> | null,
  right: Readonly<{ value: string; rate: string }> | null,
): boolean {
  return left === null
    ? right === null
    : right !== null && rationalsEqual(left, right)
}
