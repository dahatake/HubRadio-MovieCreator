import { describe, expect, it } from 'vitest'

import {
  ANALYSIS_ADOPT_METHOD,
  ANALYSIS_READ_METHOD,
  parseAnalysisResponse,
  type AnalysisResponse,
} from './analysisProtocol'
import {
  analysisProjectId,
  analysisRequestId,
  createAnalysisResponseValue,
} from './analysisTestFixture'
import { analysisStateReducer, initialAnalysisState } from './analysisState'

describe('analysisStateReducer', () => {
  it('accepts only the pending correlated response for the selected project', () => {
    const started = analysisStateReducer(
      { ...initialAnalysisState, projectId: analysisProjectId },
      {
        type: 'requestStarted',
        requestId: analysisRequestId,
        method: ANALYSIS_READ_METHOD,
        projectId: analysisProjectId,
      },
    )
    const wrongMethod = createAnalysisResponseValue(ANALYSIS_ADOPT_METHOD)
    expect(analysisStateReducer(started, {
      type: 'responseReceived',
      response: requireResponse(wrongMethod),
    })).toBe(started)

    const available = analysisStateReducer(started, {
      type: 'responseReceived',
      response: requireResponse(createAnalysisResponseValue()),
    })
    expect(available.status).toBe('available')
    expect(available.snapshot?.projectId).toBe(analysisProjectId)
    expect(available.pending).toBeNull()
  })

  it('rejects a cross-project snapshot even with a correlated request', () => {
    const started = startRead()
    const value = createAnalysisResponseValue()
    const snapshot = value.result.snapshot
    if (snapshot === null) throw new Error('Expected an analysis snapshot.')
    value.result.snapshot = {
      ...snapshot,
      projectId: '20000000-0000-4000-8000-000000000001',
    }

    const state = analysisStateReducer(started, {
      type: 'responseReceived',
      response: requireResponse(value),
    })

    expect(state.status).toBe('invalid')
    expect(state.snapshot).toBeNull()
  })

  it('keeps the current snapshot for noChange but drops it after staleBase', () => {
    const available = analysisStateReducer(startRead(), {
      type: 'responseReceived',
      response: requireResponse(createAnalysisResponseValue()),
    })
    const mutationStarted = analysisStateReducer(available, {
      type: 'requestStarted',
      requestId: '00112233-4455-6677-8899-aabbccddeef0',
      method: ANALYSIS_ADOPT_METHOD,
      projectId: analysisProjectId,
    })

    const noChangeValue = createAnalysisResponseValue(ANALYSIS_ADOPT_METHOD)
    noChangeValue.requestId = '00112233-4455-6677-8899-aabbccddeef0'
    noChangeValue.result = { status: 'noChange', snapshot: null }
    const noChange = analysisStateReducer(mutationStarted, {
      type: 'responseReceived',
      response: requireResponse(noChangeValue),
    })
    expect(noChange.status).toBe('noChange')
    expect(noChange.snapshot).toBe(available.snapshot)

    const retryStarted = analysisStateReducer(noChange, {
      type: 'requestStarted',
      requestId: '00112233-4455-6677-8899-aabbccddeef1',
      method: ANALYSIS_ADOPT_METHOD,
      projectId: analysisProjectId,
    })
    const staleValue = createAnalysisResponseValue(ANALYSIS_ADOPT_METHOD)
    staleValue.requestId = '00112233-4455-6677-8899-aabbccddeef1'
    staleValue.result = { status: 'staleBase', snapshot: null }
    const stale = analysisStateReducer(retryStarted, {
      type: 'responseReceived',
      response: requireResponse(staleValue),
    })
    expect(stale.status).toBe('staleBase')
    expect(stale.snapshot).toBeNull()
  })
})

function startRead() {
  return analysisStateReducer(
    { ...initialAnalysisState, projectId: analysisProjectId },
    {
      type: 'requestStarted',
      requestId: analysisRequestId,
      method: ANALYSIS_READ_METHOD,
      projectId: analysisProjectId,
    },
  )
}

function requireResponse(value: unknown): AnalysisResponse {
  const response = parseAnalysisResponse(value)
  if (response === null) throw new Error('Expected a valid analysis response.')
  return response
}