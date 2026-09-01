import { describe, expect, it } from 'vitest'

import {
  LAYOUT_APPLY_METHOD,
  parseLayoutBridgeResponse,
  type LayoutBridgeResponse,
} from './layoutProtocol'
import {
  initialLayoutState,
  layoutBridgeUnavailable,
  layoutErrorCleared,
  layoutLocalInvalidRequest,
  layoutProjectIdChanged,
  layoutRequestStarted,
  layoutResponseReceived,
  layoutStateReducer,
} from './layoutState'
import {
  createLayoutFailureValue,
  createLayoutResponseValue,
  layoutProjectId,
  layoutRequestId,
  layoutSecondProjectId,
  layoutSecondRequestId,
} from './layoutTestFixture'

describe('layout state reducer', () => {
  it('accepts only the correlated method and project snapshot', () => {
    const pending = layoutStateReducer(
      initialLayoutState,
      layoutRequestStarted(layoutRequestId, 'layout.read', layoutProjectId),
    )
    const wrongMethod = parseRequired(createLayoutResponseValue(
      layoutRequestId,
      LAYOUT_APPLY_METHOD,
    ))
    expect(layoutStateReducer(
      pending,
      layoutResponseReceived(wrongMethod),
    )).toBe(pending)

    const wrongProjectValue = createLayoutResponseValue(layoutRequestId)
    wrongProjectValue.result.snapshot.projectId = layoutSecondProjectId
    const wrongProject = parseRequired(wrongProjectValue)
    expect(layoutStateReducer(
      pending,
      layoutResponseReceived(wrongProject),
    )).toBe(pending)

    const accepted = parseRequired(createLayoutResponseValue(layoutRequestId))
    const completed = layoutStateReducer(
      pending,
      layoutResponseReceived(accepted),
    )
    expect(completed.snapshot?.projectId).toBe(layoutProjectId)
    expect(completed.projectIdInput).toBe(layoutProjectId)
    expect(completed.status).toBe('available')
    expect(completed.pending).toBeNull()
    expect(completed.lastCompletedMethod).toBe('layout.read')
  })

  it('drops stale evidence when project input changes or a closed failure arrives', () => {
    const loaded = loadedState()
    const changed = layoutStateReducer(
      loaded,
      layoutProjectIdChanged(layoutSecondProjectId),
    )
    expect(changed.snapshot).toBeNull()
    expect(changed.status).toBeNull()

    const pending = layoutStateReducer(
      loaded,
      layoutRequestStarted(
        layoutSecondRequestId,
        LAYOUT_APPLY_METHOD,
        layoutProjectId,
      ),
    )
    const failure = parseRequired(createLayoutFailureValue(
      'conflict',
      layoutSecondRequestId,
      LAYOUT_APPLY_METHOD,
    ))
    const failed = layoutStateReducer(
      pending,
      layoutResponseReceived(failure),
    )
    expect(failed.snapshot).toBeNull()
    expect(failed.status).toBe('conflict')
    expect(failed.lastCompletedMethod).toBe(LAYOUT_APPLY_METHOD)
  })

  it('retains the current snapshot when an apply is a no-op', () => {
    const loaded = loadedState()
    const snapshot = loaded.snapshot
    const pending = layoutStateReducer(
      loaded,
      layoutRequestStarted(
        layoutSecondRequestId,
        LAYOUT_APPLY_METHOD,
        layoutProjectId,
      ),
    )
    const noChange = parseRequired(createLayoutFailureValue(
      'noChange',
      layoutSecondRequestId,
      LAYOUT_APPLY_METHOD,
    ))

    const completed = layoutStateReducer(
      pending,
      layoutResponseReceived(noChange),
    )

    expect(completed.snapshot).toBe(snapshot)
    expect(completed.status).toBe('noChange')
    expect(completed.pending).toBeNull()
    expect(completed.lastCompletedMethod).toBe(LAYOUT_APPLY_METHOD)
  })

  it('keeps a single pending request and exposes fixed private-safe errors', () => {
    const first = layoutStateReducer(
      initialLayoutState,
      layoutRequestStarted(layoutRequestId, 'layout.read', layoutProjectId),
    )
    expect(layoutStateReducer(
      first,
      layoutRequestStarted(
        layoutSecondRequestId,
        LAYOUT_APPLY_METHOD,
        layoutProjectId,
      ),
    )).toBe(first)

    const unavailable = layoutStateReducer(first, layoutBridgeUnavailable())
    expect(unavailable.pending).toBeNull()
    expect(unavailable.error?.code).toBe('layout.operation_failed')
    expect(JSON.stringify(unavailable.error)).not.toContain('C:\\private')

    const invalid = layoutStateReducer(unavailable, layoutLocalInvalidRequest())
    expect(invalid.error?.code).toBe('layout.invalid_request')
    expect(layoutStateReducer(invalid, layoutErrorCleared()).error).toBeNull()
  })
})

function loadedState() {
  const pending = layoutStateReducer(
    initialLayoutState,
    layoutRequestStarted(layoutRequestId, 'layout.read', layoutProjectId),
  )
  return layoutStateReducer(
    pending,
    layoutResponseReceived(parseRequired(createLayoutResponseValue())),
  )
}

function parseRequired(value: unknown): LayoutBridgeResponse {
  const parsed = parseLayoutBridgeResponse(value)
  if (parsed === null) {
    throw new Error('Expected a valid layout response fixture.')
  }
  return parsed
}
