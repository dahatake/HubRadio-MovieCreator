import type {
  TranscriptMethod,
  TranscriptResponse,
  TranscriptSnapshot,
  TranscriptToken,
} from './transcriptProtocol'

export type TranscriptUiStatus =
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

export interface PendingTranscriptRequest {
  readonly requestId: string
  readonly method: TranscriptMethod
  readonly projectId: string
  readonly submittedEdit?: TranscriptEditDraft | null
}

export interface TranscriptEditDraft {
  readonly tokenId: string
  readonly expectedText: string
  readonly revisedText: string
  readonly stale?: boolean
}

export interface TranscriptState {
  readonly projectId: string | null
  readonly status: TranscriptUiStatus
  readonly pending: PendingTranscriptRequest | null
  readonly snapshot: TranscriptSnapshot | null
  readonly edit: TranscriptEditDraft | null
  readonly navigationMessage: string | null
}

export type TranscriptAction =
  | Readonly<{ type: 'sessionStarted' }>
  | Readonly<{ type: 'projectChanged'; projectId: string | null }>
  | Readonly<{
      type: 'requestStarted'
      requestId: string
      method: TranscriptMethod
      projectId: string
    }>
  | Readonly<{ type: 'responseReceived'; response: TranscriptResponse }>
  | Readonly<{ type: 'requestInvalid' }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'sendFailed'; requestId: string }>
  | Readonly<{ type: 'editStarted'; token: TranscriptToken }>
  | Readonly<{ type: 'editChanged'; value: string }>
  | Readonly<{ type: 'editCancelled' }>
  | Readonly<{ type: 'navigationReported'; message: string | null }>

export const initialTranscriptState: TranscriptState = Object.freeze({
  projectId: null,
  status: 'idle',
  pending: null,
  snapshot: null,
  edit: null,
  navigationMessage: null,
})

export function transcriptStateReducer(
  state: TranscriptState,
  action: TranscriptAction,
): TranscriptState {
  switch (action.type) {
    case 'sessionStarted':
      return state.pending === null
        ? state
        : preserveCancellableDraft(state, 'sendFailed')

    case 'projectChanged':
      if (action.projectId === state.projectId) return state
      return {
        ...initialTranscriptState,
        projectId: action.projectId,
      }

    case 'requestStarted':
      if (
        state.projectId !== action.projectId || state.pending !== null ||
        (action.method !== 'transcript.read' && (
          state.snapshot === null ||
          (state.status !== 'available' && state.status !== 'noChange') ||
          (action.method === 'transcript.editToken' && state.edit?.stale === true)
        ))
      ) {
        return state
      }
      return {
        ...state,
        status: action.method === 'transcript.read' ? 'loading' : state.status,
        pending: {
          requestId: action.requestId,
          method: action.method,
          projectId: action.projectId,
          ...(action.method === 'transcript.editToken' ? { submittedEdit: state.edit } : {}),
        },
        navigationMessage: null,
      }

    case 'responseReceived': {
      const pending = state.pending
      if (
        pending === null ||
        pending.projectId !== state.projectId ||
        action.response.requestId !== pending.requestId ||
        action.response.method !== pending.method
      ) {
        return state
      }

      const result = action.response.result
      // An acknowledgement covers only the unchanged draft sent by this request.
      const edit = pending.method === 'transcript.editToken' && pending.submittedEdit === state.edit
        ? null
        : state.edit
      if (result.status === 'available') {
        if (result.snapshot.projectId !== pending.projectId) {
          return preserveCancellableDraft(state, 'invalid')
        }
        return {
          ...state,
          status: 'available',
          pending: null,
          snapshot: result.snapshot,
          edit: edit !== null && !hasSameEditBase(state.snapshot, result.snapshot, edit)
            ? { ...edit, stale: true }
            : edit,
        }
      }

      if (result.status === 'noChange') {
        // A read without evidence cannot reconcile a retained, unverified snapshot.
        if (pending.method === 'transcript.read') return preserveCancellableDraft(state, 'invalid')
        return {
          ...state,
          status: 'noChange',
          pending: null,
          edit,
        }
      }

      return preserveCancellableDraft(state, result.status)
    }

    case 'requestInvalid':
      return preserveCancellableDraft(state, 'invalid')

    case 'bridgeUnavailable':
      return preserveCancellableDraft(state, 'bridgeUnavailable')

    case 'sendFailed':
      if (state.pending?.requestId !== action.requestId) return state
      return preserveCancellableDraft(state, 'sendFailed')

    case 'editStarted':
      if (
        state.snapshot === null || state.pending !== null || state.edit?.stale === true ||
        (state.status !== 'available' && state.status !== 'noChange') ||
        findTranscriptToken(state.snapshot, action.token.id)?.text !== action.token.text
      ) return state
      return {
        ...state,
        edit: {
          tokenId: action.token.id,
          expectedText: action.token.text,
          revisedText: action.token.text,
        },
      }

    case 'editChanged':
      if (state.edit === null || state.edit.stale === true) return state
      return {
        ...state,
        edit: { ...state.edit, revisedText: action.value },
      }

    case 'editCancelled':
      return state.edit === null ? state : { ...state, edit: null }

    case 'navigationReported':
      return { ...state, navigationMessage: action.message }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}

function preserveCancellableDraft(
  state: TranscriptState,
  status: TranscriptUiStatus,
): TranscriptState {
  return {
    ...state,
    status,
    pending: null,
    // Retained evidence is unverified; only an available refresh enables mutations.
    snapshot: status === 'forbidden' || status === 'unavailable' ? null : state.snapshot,
  }
}

function hasSameEditBase(
  previous: TranscriptSnapshot | null,
  current: TranscriptSnapshot,
  edit: TranscriptEditDraft,
): boolean {
  if (
    previous === null || previous.projectId !== current.projectId ||
    previous.revisionId !== current.revisionId || previous.revisionDigest !== current.revisionDigest ||
    previous.parentRevisionId !== current.parentRevisionId ||
    previous.changeSource !== current.changeSource || previous.createdAt !== current.createdAt
  ) return false
  const before = findTranscriptTokenBinding(previous, edit.tokenId)
  const after = findTranscriptTokenBinding(current, edit.tokenId)
  return before !== null && after !== null &&
    before.sourceSpanId === after.sourceSpanId && before.utteranceId === after.utteranceId &&
    before.sentenceId === after.sentenceId && before.token.text === edit.expectedText &&
    after.token.text === edit.expectedText
}

export function findTranscriptToken(
  snapshot: TranscriptSnapshot,
  tokenId: string,
): TranscriptToken | null {
  return findTranscriptTokenBinding(snapshot, tokenId)?.token ?? null
}

function findTranscriptTokenBinding(snapshot: TranscriptSnapshot, tokenId: string) {
  for (const utterance of snapshot.utterances) {
    for (const sentence of utterance.sentences) {
      const token = sentence.tokens.find((candidate) => candidate.id === tokenId)
      if (token !== undefined) {
        return { token, sourceSpanId: utterance.sourceSpanId, utteranceId: utterance.id, sentenceId: sentence.id }
      }
    }
  }
  return null
}
