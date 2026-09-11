import type {
  AnalysisMethod,
  AnalysisResponse,
  AnalysisSnapshot,
  AnalysisTopic,
  AnalysisTopicUpdate,
} from './analysisProtocol'

export type AnalysisUiStatus =
  | 'idle'
  | 'loading'
  | 'available'
  | 'forbidden'
  | 'unavailable'
  | 'staleBase'
  | 'conflict'
  | 'noChange'
  | 'invalid'
  | 'bridgeUnavailable'
  | 'sendFailed'

export type AnalysisTopicField = AnalysisTopicUpdate['field']

export interface AnalysisTopicInput {
  readonly value: string
  readonly base: Readonly<{
    projectId: string
    revisionId: string
    revisionDigest: string
    metadataRevisionId: string
    value: string
  }>
}

export type AnalysisTopicInputs = Readonly<Partial<Record<AnalysisTopicField, AnalysisTopicInput>>>

interface AnalysisReasonInput {
  readonly value: string
}

export interface PendingAnalysisRequest {
  readonly requestId: string
  readonly method: AnalysisMethod
  readonly projectId: string
  readonly sentReason: AnalysisReasonInput | null
  readonly sentTopicInput: Readonly<{
    topicId: string
    field: AnalysisTopicField
    input: AnalysisTopicInput
  }> | null
}

export interface AnalysisState {
  readonly projectId: string | null
  readonly status: AnalysisUiStatus
  readonly pending: PendingAnalysisRequest | null
  readonly snapshot: AnalysisSnapshot | null
  readonly navigationMessage: string | null
  readonly operationMessage: string | null
  readonly reasonInput: AnalysisReasonInput
  readonly topicInputs: Readonly<Record<string, AnalysisTopicInputs>>
}

export type AnalysisAction =
  | Readonly<{ type: 'sessionStarted' }>
  | Readonly<{ type: 'projectChanged'; projectId: string | null }>
  | Readonly<{
      type: 'requestStarted'
      requestId: string
      method: AnalysisMethod
      projectId: string
      topicUpdate?: Readonly<{ topicId: string; field: AnalysisTopicField }>
    }>
  | Readonly<{ type: 'responseReceived'; response: AnalysisResponse }>
  | Readonly<{ type: 'requestInvalid' }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'sendFailed'; requestId: string }>
  | Readonly<{ type: 'navigationReported'; message: string | null }>
  | Readonly<{ type: 'operationReported'; message: string | null }>
  | Readonly<{ type: 'reasonInputChanged'; value: string }>
  | Readonly<{
      type: 'topicInputChanged'
      topicId: string
      field: AnalysisTopicField
      value: string
    }>
  | Readonly<{ type: 'topicInputDiscarded'; topicId: string; field: AnalysisTopicField }>
  | Readonly<{ type: 'localInputsDiscarded' }>

const emptyReason: AnalysisReasonInput = Object.freeze({ value: '' })
const emptyTopicInputs: AnalysisState['topicInputs'] = Object.freeze({})

export const initialAnalysisState: AnalysisState = Object.freeze({
  projectId: null,
  status: 'idle',
  pending: null,
  snapshot: null,
  navigationMessage: null,
  operationMessage: null,
  reasonInput: emptyReason,
  topicInputs: emptyTopicInputs,
})

export function analysisStateReducer(
  state: AnalysisState,
  action: AnalysisAction,
): AnalysisState {
  switch (action.type) {
    case 'sessionStarted':
      return state.pending === null
        ? state
        : {
            ...state,
            status: state.snapshot === null ? 'idle' : 'available',
            pending: null,
          }

    case 'projectChanged':
      if (action.projectId === state.projectId) return state
      return { ...initialAnalysisState, projectId: action.projectId }

    case 'requestStarted': {
      if (state.projectId !== action.projectId || state.pending !== null) return state
      const topicUpdate = action.method === 'analysis.updateTopic' ? action.topicUpdate : undefined
      const input = topicUpdate === undefined
        ? undefined
        : state.topicInputs[topicUpdate.topicId]?.[topicUpdate.field]
      return {
        ...state,
        status: action.method === 'analysis.read' ? 'loading' : state.status,
        pending: {
          requestId: action.requestId,
          method: action.method,
          projectId: action.projectId,
          sentReason: action.method === 'analysis.read' ? null : state.reasonInput,
          sentTopicInput: topicUpdate === undefined || input === undefined
            ? null
            : { ...topicUpdate, input },
        },
        navigationMessage: null,
        operationMessage: null,
      }
    }

    case 'responseReceived': {
      const pending = state.pending
      if (
        pending === null ||
        action.response.requestId !== pending.requestId ||
        action.response.method !== pending.method
      ) {
        return state
      }

      const result = action.response.result
      if (result.status === 'available') {
        if (result.snapshot.projectId !== pending.projectId) {
          return {
            ...state,
            status: 'invalid',
            pending: null,
            snapshot: null,
            operationMessage: null,
          }
        }
        return {
          ...state,
          ...clearAcceptedInputs(state, pending),
          status: 'available',
          pending: null,
          snapshot: result.snapshot,
          operationMessage:
            pending.method === 'analysis.read'
              ? null
              : '変更をホストのメモリ内へ反映しました（未永続化）。保存完了ではありません。',
        }
      }

      if (result.status === 'noChange') {
        return {
          ...state,
          ...clearAcceptedInputs(state, pending),
          status: 'noChange',
          pending: null,
          operationMessage: '内容は変更されませんでした。',
        }
      }

      return {
        ...state,
        status: result.status,
        pending: null,
        snapshot: null,
        operationMessage: null,
      }
    }

    case 'requestInvalid':
      return {
        ...state,
        status: 'invalid',
        pending: null,
        snapshot: null,
        operationMessage: null,
      }

    case 'bridgeUnavailable':
      return {
        ...state,
        status: 'bridgeUnavailable',
        pending: null,
        snapshot: null,
        operationMessage: null,
      }

    case 'sendFailed':
      if (state.pending?.requestId !== action.requestId) return state
      return {
        ...state,
        status: 'sendFailed',
        pending: null,
        snapshot: null,
        operationMessage: null,
      }

    case 'navigationReported':
      return { ...state, navigationMessage: action.message }

    case 'operationReported':
      return { ...state, operationMessage: action.message }

    case 'reasonInputChanged':
      return action.value === state.reasonInput.value
        ? state
        : { ...state, reasonInput: { value: action.value } }

    case 'topicInputChanged': {
      const snapshot = state.snapshot
      const topic = snapshot?.topics.find((item) => item.topicId === action.topicId)
      if (snapshot === null || snapshot.projectId !== state.projectId || topic === undefined) return state
      const input = state.topicInputs[action.topicId]?.[action.field]
      if (input !== undefined && !analysisTopicInputIsCurrent(snapshot, topic, action.field, input)) return state
      if (input?.value === action.value) return state
      const base = input?.base ?? {
        projectId: snapshot.projectId,
        revisionId: snapshot.revisionId,
        revisionDigest: snapshot.revisionDigest,
        metadataRevisionId: topic.metadataRevisionId,
        value: getAnalysisTopicValue(topic, action.field),
      }
      const sentInput = state.pending?.sentTopicInput
      // An in-flight write may advance the baseline: returning to its old value
      // is then a new input, not permission for that write's ACK to erase it.
      if (action.value === base.value &&
        !(sentInput?.topicId === action.topicId && sentInput.field === action.field)) {
        return {
          ...state,
          topicInputs: removeTopicInput(state.topicInputs, action.topicId, action.field),
        }
      }
      return {
        ...state,
        topicInputs: {
          ...state.topicInputs,
          [action.topicId]: {
            ...state.topicInputs[action.topicId],
            [action.field]: { value: action.value, base },
          },
        },
      }
    }

    case 'topicInputDiscarded':
      return {
        ...state,
        topicInputs: removeTopicInput(state.topicInputs, action.topicId, action.field),
      }

    case 'localInputsDiscarded':
      // This never rolls back ACKed native changes or cancels a pending request.
      return { ...state, reasonInput: emptyReason, topicInputs: emptyTopicInputs }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}

export function hasLocalAnalysisInputs(state: AnalysisState): boolean {
  return state.reasonInput.value !== '' || Object.keys(state.topicInputs).length !== 0
}

export function isAnalysisDirty(state: AnalysisState): boolean {
  return hasLocalAnalysisInputs(state)
}

export function getAnalysisTopicValue(topic: AnalysisTopic, field: AnalysisTopicField): string {
  switch (field) {
    case 'title': return topic.title
    case 'summary': return topic.summary ?? ''
    case 'mainSpeaker': return topic.mainSpeaker?.speakerId ?? ''
    case 'keywords': return topic.keywords.join('\n')
    case 'importantStatements': return topic.importantStatementSourceSpanIds.join('\n')
    case 'audience': return topic.audience
    case 'publicationCautions': return topic.publicationCautions.join('\n')
  }
}

export function analysisTopicInputIsCurrent(
  snapshot: AnalysisSnapshot,
  topic: AnalysisTopic,
  field: AnalysisTopicField,
  input: AnalysisTopicInput,
): boolean {
  return input.base.projectId === snapshot.projectId &&
    input.base.revisionId === snapshot.revisionId &&
    input.base.revisionDigest === snapshot.revisionDigest &&
    input.base.metadataRevisionId === topic.metadataRevisionId &&
    input.base.value === getAnalysisTopicValue(topic, field) &&
    (field !== 'mainSpeaker' || input.value === '' ||
      snapshot.speakers.some((speaker) => speaker.speakerId === input.value))
}

function clearAcceptedInputs(
  state: AnalysisState,
  pending: PendingAnalysisRequest,
): Pick<AnalysisState, 'reasonInput' | 'topicInputs'> {
  const sent = pending.sentTopicInput
  return {
    // Compare immutable input identities, not text: A → B → A is a new input.
    reasonInput: state.reasonInput === pending.sentReason ? emptyReason : state.reasonInput,
    topicInputs: sent !== null && state.topicInputs[sent.topicId]?.[sent.field] === sent.input
      ? removeTopicInput(state.topicInputs, sent.topicId, sent.field)
      : state.topicInputs,
  }
}

function removeTopicInput(
  inputs: AnalysisState['topicInputs'],
  topicId: string,
  field: AnalysisTopicField,
): AnalysisState['topicInputs'] {
  if (inputs[topicId]?.[field] === undefined) return inputs
  const fields = { ...inputs[topicId] }
  delete fields[field]
  const remaining = { ...inputs }
  if (Object.keys(fields).length === 0) delete remaining[topicId]
  else remaining[topicId] = fields
  return remaining
}