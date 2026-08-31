import {
  isCanonicalEntityId,
  positionMatchesFrame,
  type PlayerFrame,
  type PlayerReadResponse,
  type PlayerSeekResponse,
  type PlayerSnapshot,
} from './playerProtocol'

export type PlayerStatus =
  | 'idle'
  | 'loading'
  | 'available'
  | 'forbidden'
  | 'unavailable'
  | 'stale'
  | 'mismatched'
  | 'invalidTime'
  | 'mediaUnavailable'
  | 'bridgeUnavailable'
  | 'bridgeSendFailed'

export interface PendingPlayerSeek {
  readonly requestId: string
  readonly proxyFrameSequence: number
  readonly evidenceDigest: string
}

export interface PlayerState {
  readonly jobIdInput: string
  readonly jobId: string | null
  readonly status: PlayerStatus
  readonly pendingReadRequestId: string | null
  readonly pendingSeek: PendingPlayerSeek | null
  readonly snapshot: PlayerSnapshot | null
  readonly currentFrameSequence: number | null
}

export type PlayerAction =
  | Readonly<{ type: 'jobIdChanged'; value: string }>
  | Readonly<{ type: 'readRequested'; requestId: string; jobId: string }>
  | Readonly<{ type: 'readReceived'; response: PlayerReadResponse }>
  | Readonly<{
      type: 'seekRequested'
      requestId: string
      proxyFrameSequence: number
    }>
  | Readonly<{ type: 'seekReceived'; response: PlayerSeekResponse }>
  | Readonly<{ type: 'playbackFrameObserved'; proxyFrameSequence: number }>
  | Readonly<{ type: 'mediaUnavailable' }>
  | Readonly<{ type: 'mediaAvailable' }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'bridgeSendFailed'; requestId: string }>

export const initialPlayerState: PlayerState = {
  jobIdInput: '',
  jobId: null,
  status: 'idle',
  pendingReadRequestId: null,
  pendingSeek: null,
  snapshot: null,
  currentFrameSequence: null,
}

export function playerStateReducer(
  state: PlayerState,
  action: PlayerAction,
): PlayerState {
  switch (action.type) {
    case 'jobIdChanged':
      return { ...state, jobIdInput: action.value }

    case 'readRequested':
      if (
        !isCanonicalEntityId(action.requestId) ||
        !isCanonicalEntityId(action.jobId)
      ) {
        return state
      }
      return {
        ...state,
        jobId: action.jobId,
        status: 'loading',
        pendingReadRequestId: action.requestId,
        pendingSeek: null,
        snapshot: null,
        currentFrameSequence: null,
      }

    case 'readReceived': {
      if (action.response.requestId !== state.pendingReadRequestId) {
        return state
      }
      if (action.response.result.status !== 'available') {
        return {
          ...state,
          status: action.response.result.status,
          pendingReadRequestId: null,
          pendingSeek: null,
          snapshot: null,
          currentFrameSequence: null,
        }
      }
      return {
        ...state,
        status: 'available',
        pendingReadRequestId: null,
        pendingSeek: null,
        snapshot: action.response.result.snapshot,
        currentFrameSequence: 0,
      }
    }

    case 'seekRequested':
      if (
        state.snapshot === null ||
        state.jobId === null ||
        !isCanonicalEntityId(action.requestId) ||
        !Number.isInteger(action.proxyFrameSequence) ||
        action.proxyFrameSequence < 0 ||
        action.proxyFrameSequence >= state.snapshot.frames.length
      ) {
        return state
      }
      return {
        ...state,
        pendingSeek: {
          requestId: action.requestId,
          proxyFrameSequence: action.proxyFrameSequence,
          evidenceDigest: state.snapshot.evidenceDigest,
        },
      }

    case 'seekReceived': {
      const pending = state.pendingSeek
      if (pending === null || action.response.requestId !== pending.requestId) {
        return state
      }
      if (action.response.result.status !== 'available') {
        return {
          ...state,
          status: action.response.result.status,
          pendingSeek: null,
          pendingReadRequestId: null,
          snapshot: null,
          currentFrameSequence: null,
        }
      }
      const expectedFrame = state.snapshot?.frames[pending.proxyFrameSequence]
      if (
        expectedFrame === undefined ||
        action.response.result.position.proxyFrameSequence !==
          pending.proxyFrameSequence ||
        !positionMatchesFrame(action.response.result.position, expectedFrame)
      ) {
        return {
          ...state,
          status: 'mismatched',
          pendingSeek: null,
          pendingReadRequestId: null,
          snapshot: null,
          currentFrameSequence: null,
        }
      }
      return {
        ...state,
        status:
          state.status === 'mediaUnavailable'
            ? 'mediaUnavailable'
            : 'available',
        pendingSeek: null,
        currentFrameSequence: pending.proxyFrameSequence,
      }
    }

    case 'playbackFrameObserved':
      if (
        state.snapshot === null ||
        !Number.isInteger(action.proxyFrameSequence) ||
        action.proxyFrameSequence < 0 ||
        action.proxyFrameSequence >= state.snapshot.frames.length ||
        action.proxyFrameSequence === state.currentFrameSequence
      ) {
        return state
      }
      return {
        ...state,
        currentFrameSequence: action.proxyFrameSequence,
      }

    case 'mediaUnavailable':
      if (state.snapshot === null) {
        return state
      }
      return { ...state, status: 'mediaUnavailable', pendingSeek: null }

    case 'mediaAvailable':
      if (state.snapshot === null || state.status !== 'mediaUnavailable') {
        return state
      }
      return { ...state, status: 'available' }

    case 'bridgeUnavailable':
      return {
        ...state,
        status: 'bridgeUnavailable',
        pendingReadRequestId: null,
        pendingSeek: null,
        snapshot: null,
        currentFrameSequence: null,
      }

    case 'bridgeSendFailed':
      if (
        action.requestId !== state.pendingReadRequestId &&
        action.requestId !== state.pendingSeek?.requestId
      ) {
        return state
      }
      return {
        ...state,
        status: 'bridgeSendFailed',
        pendingReadRequestId: null,
        pendingSeek: null,
        snapshot: null,
        currentFrameSequence: null,
      }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}

export function selectCurrentFrame(state: PlayerState): PlayerFrame | null {
  if (state.snapshot === null || state.currentFrameSequence === null) {
    return null
  }
  return state.snapshot.frames[state.currentFrameSequence] ?? null
}

export function selectKeyboardSeekSequence(
  state: PlayerState,
  key: string,
): number | null {
  if (state.snapshot === null || state.currentFrameSequence === null) {
    return null
  }
  switch (key) {
    case 'ArrowLeft':
      return state.currentFrameSequence === 0
        ? null
        : state.currentFrameSequence - 1
    case 'ArrowRight':
      return state.currentFrameSequence === state.snapshot.frames.length - 1
        ? null
        : state.currentFrameSequence + 1
    case 'Home':
      return state.currentFrameSequence === 0 ? null : 0
    case 'End': {
      const finalSequence = state.snapshot.frames.length - 1
      return state.currentFrameSequence === finalSequence ? null : finalSequence
    }
    default:
      return null
  }
}
