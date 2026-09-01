import { describe, expect, it } from 'vitest'

import {
  createComparisonReadRequest,
  parseComparisonReadResponse,
  type ComparisonFailureStatus,
  type ComparisonReadResponse,
} from './compareProtocol'
import {
  comparisonBridgeUnavailable,
  comparisonInputChanged,
  comparisonLocalInvalidRequest,
  comparisonRequestStarted,
  comparisonResponseReceived,
  comparisonStateReducer,
  initialComparisonState,
} from './compareState'
import {
  comparisonLeftDigest,
  comparisonLeftVersionId,
  comparisonRequestId,
  comparisonRightDigest,
  comparisonRightVersionId,
  comparisonSecondRequestId,
  createComparisonResponseValue,
} from './compareTestFixture'

describe('comparison state reducer', () => {
  it('accepts only the exact pending target and stores its comparison', () => {
    const request = createRequest()
    let state = comparisonStateReducer(
      initialComparisonState,
      comparisonRequestStarted(request),
    )

    state = comparisonStateReducer(
      state,
      comparisonResponseReceived(validResponse(comparisonSecondRequestId)),
    )
    expect(state.pending?.requestId).toBe(comparisonRequestId)
    expect(state.comparison).toBeNull()

    const mismatchedValue = createComparisonResponseValue(comparisonRequestId)
    mismatchedValue.result.comparison.right.versionDigest = 'c'.repeat(64)
    state = comparisonStateReducer(
      state,
      comparisonResponseReceived(requiredResponse(mismatchedValue)),
    )
    expect(state.pending?.requestId).toBe(comparisonRequestId)
    expect(state.comparison).toBeNull()

    state = comparisonStateReducer(
      state,
      comparisonResponseReceived(validResponse(comparisonRequestId)),
    )
    expect(state.pending).toBeNull()
    expect(state.comparison?.left.versionId).toBe(comparisonLeftVersionId)
    expect(state.comparison?.right.versionDigest).toBe(comparisonRightDigest)
    expect(state.leftVersionDigestInput).toBe(comparisonLeftDigest)
  })

  it('keeps only one pending request and clears displayed evidence on input change', () => {
    const request = createRequest()
    const pending = comparisonStateReducer(
      initialComparisonState,
      comparisonRequestStarted(request),
    )
    expect(comparisonStateReducer(
      pending,
      comparisonRequestStarted(createComparisonReadRequest(
        comparisonSecondRequestId,
        comparisonLeftVersionId,
        comparisonLeftDigest,
        comparisonRightVersionId,
        comparisonRightDigest,
      )),
    )).toBe(pending)

    const loaded = comparisonStateReducer(
      pending,
      comparisonResponseReceived(validResponse(comparisonRequestId)),
    )
    const changed = comparisonStateReducer(
      loaded,
      comparisonInputChanged('rightVersionDigest', 'd'.repeat(64)),
    )
    expect(changed.rightVersionDigestInput).toBe('d'.repeat(64))
    expect(changed.comparison).toBeNull()
    expect(changed.error).toBeNull()
  })

  it('maps every native failure status to fixed private-safe text', () => {
    const statuses: ComparisonFailureStatus[] = [
      'forbidden',
      'notFound',
      'staleVersion',
      'invalidComparison',
      'unavailable',
    ]
    for (const status of statuses) {
      const pending = comparisonStateReducer(
        initialComparisonState,
        comparisonRequestStarted(createRequest()),
      )
      const response: ComparisonReadResponse = {
        version: 1,
        requestId: comparisonRequestId,
        method: 'compare.read',
        result: { status, comparison: null },
      }
      const state = comparisonStateReducer(
        pending,
        comparisonResponseReceived(response),
      )
      expect(state.error?.code).toBe(status)
      expect(state.pending).toBeNull()
      expect(JSON.stringify(state.error)).not.toContain('C:\\private')
      expect(JSON.stringify(state.error)).not.toContain('exception')
    }
  })

  it('never leaves previously loaded evidence visible during or after refresh failure', () => {
    const request = createRequest()
    const loaded = comparisonStateReducer(
      comparisonStateReducer(
        initialComparisonState,
        comparisonRequestStarted(request),
      ),
      comparisonResponseReceived(validResponse(comparisonRequestId)),
    )
    expect(loaded.comparison).not.toBeNull()

    const refreshing = comparisonStateReducer(
      loaded,
      comparisonRequestStarted(request),
    )
    expect(refreshing.comparison).toBeNull()

    const failure: ComparisonReadResponse = {
      version: 1,
      requestId: comparisonRequestId,
      method: 'compare.read',
      result: { status: 'unavailable', comparison: null },
    }
    const failed = comparisonStateReducer(
      refreshing,
      comparisonResponseReceived(failure),
    )
    expect(failed.comparison).toBeNull()
    expect(failed.pending).toBeNull()
    expect(failed.error?.code).toBe('unavailable')
  })

  it('uses fixed local validation and bridge-unavailable errors', () => {
    const invalid = comparisonStateReducer(
      initialComparisonState,
      comparisonLocalInvalidRequest(),
    )
    expect(invalid.error).toEqual({
      code: 'invalidRequest',
      message: '左右の版 ID と完全な SHA-256 digest を確認してください。',
    })

    const unavailable = comparisonStateReducer(
      invalid,
      comparisonBridgeUnavailable(),
    )
    expect(unavailable.error?.code).toBe('unavailable')
    expect(unavailable.pending).toBeNull()
  })

  function createRequest() {
    return createComparisonReadRequest(
      comparisonRequestId,
      comparisonLeftVersionId,
      comparisonLeftDigest,
      comparisonRightVersionId,
      comparisonRightDigest,
    )
  }

  function validResponse(requestId: string): ComparisonReadResponse {
    return requiredResponse(createComparisonResponseValue(requestId))
  }

  function requiredResponse(value: unknown): ComparisonReadResponse {
    const response = parseComparisonReadResponse(value)
    if (response === null) {
      throw new Error('Expected a valid comparison response.')
    }
    return response
  }
})