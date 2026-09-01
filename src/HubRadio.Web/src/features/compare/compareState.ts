import type {
  ComparisonFailureStatus,
  ComparisonReadRequest,
  ComparisonReadResponse,
  ComparisonSnapshot,
} from './compareProtocol'

export type ComparisonInputField =
  | 'leftVersionId'
  | 'leftVersionDigest'
  | 'rightVersionId'
  | 'rightVersionDigest'

export interface PendingComparisonRequest {
  readonly requestId: string
  readonly target: ComparisonReadRequest['params']
}

export interface ComparisonUiError {
  readonly code: ComparisonFailureStatus | 'invalidRequest'
  readonly message: string
}

export interface ComparisonUiState {
  readonly leftVersionIdInput: string
  readonly leftVersionDigestInput: string
  readonly rightVersionIdInput: string
  readonly rightVersionDigestInput: string
  readonly comparison: ComparisonSnapshot | null
  readonly pending: PendingComparisonRequest | null
  readonly error: ComparisonUiError | null
}

export type ComparisonUiAction =
  | Readonly<{
      type: 'inputChanged'
      field: ComparisonInputField
      value: string
    }>
  | Readonly<{
      type: 'requestStarted'
      requestId: string
      target: ComparisonReadRequest['params']
    }>
  | Readonly<{ type: 'responseReceived'; response: ComparisonReadResponse }>
  | Readonly<{ type: 'bridgeUnavailable' }>
  | Readonly<{ type: 'localInvalidRequest' }>
  | Readonly<{ type: 'errorCleared' }>

export const initialComparisonState: ComparisonUiState = Object.freeze({
  leftVersionIdInput: '',
  leftVersionDigestInput: '',
  rightVersionIdInput: '',
  rightVersionDigestInput: '',
  comparison: null,
  pending: null,
  error: null,
})

export function comparisonInputChanged(
  field: ComparisonInputField,
  value: string,
): ComparisonUiAction {
  return { type: 'inputChanged', field, value }
}

export function comparisonRequestStarted(
  request: ComparisonReadRequest,
): ComparisonUiAction {
  return {
    type: 'requestStarted',
    requestId: request.requestId,
    target: request.params,
  }
}

export function comparisonResponseReceived(
  response: ComparisonReadResponse,
): ComparisonUiAction {
  return { type: 'responseReceived', response }
}

export function comparisonBridgeUnavailable(): ComparisonUiAction {
  return { type: 'bridgeUnavailable' }
}

export function comparisonLocalInvalidRequest(): ComparisonUiAction {
  return { type: 'localInvalidRequest' }
}

export function comparisonErrorCleared(): ComparisonUiAction {
  return { type: 'errorCleared' }
}

export function comparisonStateReducer(
  state: ComparisonUiState,
  action: ComparisonUiAction,
): ComparisonUiState {
  switch (action.type) {
    case 'inputChanged':
      return {
        ...state,
        [inputPropertyNames[action.field]]: action.value,
        comparison: null,
        error: null,
      }

    case 'requestStarted':
      if (state.pending !== null) {
        return state
      }
      return {
        ...state,
        comparison: null,
        pending: {
          requestId: action.requestId,
          target: action.target,
        },
        error: null,
      }

    case 'responseReceived': {
      const pending = state.pending
      if (
        pending === null ||
        pending.requestId !== action.response.requestId
      ) {
        return state
      }
      if (action.response.result.status !== 'available') {
        const status = action.response.result.status
        return {
          ...state,
          comparison: null,
          pending: null,
          error: {
            code: status,
            message: failureMessages[status],
          },
        }
      }

      const comparison = action.response.result.comparison
      if (!comparisonMatchesTarget(comparison, pending.target)) {
        return state
      }
      return {
        ...state,
        leftVersionIdInput: comparison.left.versionId,
        leftVersionDigestInput: comparison.left.versionDigest,
        rightVersionIdInput: comparison.right.versionId,
        rightVersionDigestInput: comparison.right.versionDigest,
        comparison,
        pending: null,
        error: null,
      }
    }

    case 'bridgeUnavailable':
      return {
        ...state,
        comparison: null,
        pending: null,
        error: {
          code: 'unavailable',
          message: failureMessages.unavailable,
        },
      }

    case 'localInvalidRequest':
      return {
        ...state,
        comparison: null,
        error: {
          code: 'invalidRequest',
          message: '左右の版 ID と完全な SHA-256 digest を確認してください。',
        },
      }

    case 'errorCleared':
      return { ...state, error: null }
  }

  const exhaustiveAction: never = action
  return exhaustiveAction
}

function comparisonMatchesTarget(
  comparison: ComparisonSnapshot,
  target: ComparisonReadRequest['params'],
): boolean {
  return comparison.left.versionId === target.leftVersionId &&
    comparison.left.versionDigest === target.leftVersionDigest &&
    comparison.right.versionId === target.rightVersionId &&
    comparison.right.versionDigest === target.rightVersionDigest
}

const inputPropertyNames: Readonly<Record<
  ComparisonInputField,
  keyof Pick<
    ComparisonUiState,
    | 'leftVersionIdInput'
    | 'leftVersionDigestInput'
    | 'rightVersionIdInput'
    | 'rightVersionDigestInput'
  >
>> = {
  leftVersionId: 'leftVersionIdInput',
  leftVersionDigest: 'leftVersionDigestInput',
  rightVersionId: 'rightVersionIdInput',
  rightVersionDigest: 'rightVersionDigestInput',
}

const failureMessages: Readonly<Record<ComparisonFailureStatus, string>> = {
  forbidden: '現在の Windows ユーザーには構成版を比較する権限がありません。',
  notFound: '指定された構成版を確認できませんでした。版 ID を見直してください。',
  staleVersion: '版 ID と内容 digest が一致しません。最新の完全 digest を指定してください。',
  invalidComparison: '異なる案件の構成版は比較できません。',
  unavailable: 'ネイティブホストへ比較要求を送信できませんでした。',
}