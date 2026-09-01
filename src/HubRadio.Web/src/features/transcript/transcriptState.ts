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
}

export interface TranscriptEditDraft {
  readonly tokenId: string
  readonly expectedText: string
  readonly revisedText: string
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
        : {
            ...state,
            status: state.snapshot === null ? 'idle' : 'available',
            pending: null,
            edit: null,
          }

    case 'projectChanged':
      if (action.projectId === state.projectId) return state
      return {
        ...initialTranscriptState,
        projectId: action.projectId,
      }

    case 'requestStarted':
      if (state.projectId !== action.projectId || state.pending !== null) {
        return state
      }
      return {
        ...state,
        status: action.method === 'transcript.read' ? 'loading' : state.status,
        pending: {
          requestId: action.requestId,
          method: action.method,
          projectId: action.projectId,
        },
        navigationMessage: null,
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
            edit: null,
          }
        }
        return {
          ...state,
          status: 'available',
          pending: null,
          snapshot: result.snapshot,
          edit: null,
        }
      }

      if (result.status === 'noChange') {
        return {
          ...state,
          status: 'noChange',
          pending: null,
          edit: null,
        }
      }

      return {
        ...state,
        status: result.status,
        pending: null,
        snapshot: null,
        edit: null,
      }
    }

    case 'requestInvalid':
      return {
        ...state,
        status: 'invalid',
        pending: null,
        snapshot: null,
        edit: null,
      }

    case 'bridgeUnavailable':
      return {
        ...state,
        status: 'bridgeUnavailable',
        pending: null,
        snapshot: null,
        edit: null,
      }

    case 'sendFailed':
      if (state.pending?.requestId !== action.requestId) return state
      return {
        ...state,
        status: 'sendFailed',
        pending: null,
        snapshot: null,
        edit: null,
      }

    case 'editStarted':
      if (state.snapshot === null || state.pending !== null) return state
      return {
        ...state,
        edit: {
          tokenId: action.token.id,
          expectedText: action.token.text,
          revisedText: action.token.text,
        },
      }

    case 'editChanged':
      if (state.edit === null) return state
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

export function findTranscriptToken(
  snapshot: TranscriptSnapshot,
  tokenId: string,
): TranscriptToken | null {
  for (const utterance of snapshot.utterances) {
    for (const sentence of utterance.sentences) {
      const token = sentence.tokens.find((candidate) => candidate.id === tokenId)
      if (token !== undefined) return token
    }
  }
  return null
}
