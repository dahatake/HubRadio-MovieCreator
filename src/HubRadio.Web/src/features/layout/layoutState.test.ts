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

  it('drops evidence when project input changes or authorization is refused', () => {
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
      'forbidden',
      layoutSecondRequestId,
      LAYOUT_APPLY_METHOD,
    ))
    const failed = layoutStateReducer(
      pending,
      layoutResponseReceived(failure),
    )
    expect(failed.snapshot).toBeNull()
    expect(failed.status).toBe('forbidden')
    expect(failed.lastCompletedMethod).toBe(LAYOUT_APPLY_METHOD)
  })

  it.each(['conflict', 'invalid'] as const)(
    'retains only the same-project apply base on %s, including a failed explicit refresh',
    (status) => {
      const loaded = loadedState()
      const pending = layoutStateReducer(loaded, layoutRequestStarted(
        layoutSecondRequestId, LAYOUT_APPLY_METHOD, layoutProjectId,
      ))
      const failed = layoutStateReducer(pending, layoutResponseReceived(parseRequired(
        createLayoutFailureValue(status, layoutSecondRequestId, LAYOUT_APPLY_METHOD),
      )))
      expect(failed.snapshot).toBe(loaded.snapshot)
      expect(failed.snapshot?.revision).toBe(3)
      expect(failed.status).toBe(status)
      expect(failed.pending).toBeNull()
      expect(failed.lastCompletedMethod).toBe(LAYOUT_APPLY_METHOD)

      const refreshing = layoutStateReducer(failed, layoutRequestStarted(
        layoutRequestId, 'layout.read', layoutProjectId,
      ))
      const refreshFailed = layoutStateReducer(refreshing, layoutResponseReceived(parseRequired(
        createLayoutFailureValue(status, layoutRequestId),
      )))
      expect(refreshFailed.snapshot).toBe(loaded.snapshot)
      expect(refreshFailed.status).toBe(status)
      expect(refreshFailed.pending).toBeNull()
      expect(refreshFailed.lastCompletedMethod).toBe('layout.read')
    },
  )

  it.each(['conflict', 'invalid'] as const)(
    'retains the successful style ACK base when a correlated read reports %s',
    (status) => {
      const acknowledged = styleAcknowledgedState()
      expect(acknowledged.status).toBe('available')
      expect(acknowledged.snapshot?.revision).toBe(4)
      expect(acknowledged.snapshot?.subtitles[0]?.style).toBe('emphasis')
      expect(acknowledged.lastCompletedMethod).toBe(LAYOUT_APPLY_METHOD)
      const pending = layoutStateReducer(acknowledged, layoutRequestStarted(
        layoutRequestId, 'layout.read', layoutProjectId,
      ))
      const foreign = createLayoutResponseValue(layoutRequestId)
      foreign.result.snapshot.projectId = layoutSecondProjectId
      for (const value of [
        createLayoutFailureValue(status, layoutSecondRequestId),
        createLayoutFailureValue(status, layoutRequestId, LAYOUT_APPLY_METHOD),
        foreign,
      ]) {
        expect(layoutStateReducer(pending, layoutResponseReceived(parseRequired(value)))).toBe(pending)
      }

      const failure = layoutResponseReceived(parseRequired(createLayoutFailureValue(status)))
      const failed = layoutStateReducer(pending, failure)
      expect(failed.snapshot).toBe(acknowledged.snapshot)
      expect(failed.status).toBe(status)
      expect(failed.pending).toBeNull()
      expect(failed.lastCompletedMethod).toBe('layout.read')
      expect(layoutStateReducer(failed, failure)).toBe(failed)

      const retrying = layoutStateReducer(failed, layoutRequestStarted(
        layoutSecondRequestId, 'layout.read', layoutProjectId,
      ))
      const noEvidence = layoutStateReducer(retrying, layoutResponseReceived(parseRequired(
        createLayoutFailureValue('noChange', layoutSecondRequestId),
      )))
      expect(noEvidence.snapshot).toBe(acknowledged.snapshot)
      expect(noEvidence.status).toBe(status)
      expect(noEvidence.pending).toBeNull()

      const refreshing = layoutStateReducer(noEvidence, layoutRequestStarted(
        layoutRequestId, 'layout.read', layoutProjectId,
      ))
      const refreshed = layoutStateReducer(refreshing, layoutResponseReceived(parseRequired({
        ...createLayoutResponseValue(),
        result: { status: 'available', snapshot: acknowledged.snapshot },
      })))
      expect(refreshed.snapshot).toEqual(acknowledged.snapshot)
      expect(refreshed.status).toBe('available')
      expect(refreshed.pending).toBeNull()
      expect(refreshed.error).toBeNull()
      expect(refreshed.lastCompletedMethod).toBe('layout.read')
    },
  )

  it.each(['layout.read', LAYOUT_APPLY_METHOD, null] as const)(
    'retains an unverified same-project base on transport failure with pending method %s',
    (method) => {
      const acknowledged = styleAcknowledgedState()
      const pending = method === null ? acknowledged : layoutStateReducer(acknowledged, layoutRequestStarted(
        layoutRequestId, method, layoutProjectId,
      ))
      const failed = layoutStateReducer(pending, layoutBridgeUnavailable())
      expect(failed.snapshot).toBe(acknowledged.snapshot)
      expect(failed.snapshot?.revision).toBe(4)
      expect(failed.status).toBe('invalid')
      expect(failed.pending).toBeNull()
      expect(failed.error?.code).toBe('layout.operation_failed')
      expect(failed.lastCompletedMethod).toBeNull()

      const dismissed = layoutStateReducer(failed, layoutErrorCleared())
      expect(dismissed.error).toBeNull()
      expect(dismissed.status).toBe('invalid')
      const retrying = layoutStateReducer(dismissed, layoutRequestStarted(
        layoutSecondRequestId, 'layout.read', layoutProjectId,
      ))
      const noEvidence = layoutStateReducer(retrying, layoutResponseReceived(parseRequired(
        createLayoutFailureValue('noChange', layoutSecondRequestId),
      )))
      expect(noEvidence.snapshot).toBe(acknowledged.snapshot)
      expect(noEvidence.status).toBe('invalid')
      expect(noEvidence.pending).toBeNull()
    },
  )

  it('does not retain a foreign base or revive closed evidence on transport failure', () => {
    const acknowledged = styleAcknowledgedState()
    const foreign = layoutStateReducer(acknowledged, layoutRequestStarted(
      layoutRequestId, 'layout.read', layoutSecondProjectId,
    ))
    expect(layoutStateReducer(foreign, layoutBridgeUnavailable()).snapshot).toBeNull()
    const changed = layoutStateReducer(acknowledged, layoutProjectIdChanged(layoutSecondProjectId))
    expect(layoutStateReducer(changed, layoutBridgeUnavailable()).snapshot).toBeNull()
    expect(layoutStateReducer(initialLayoutState, layoutBridgeUnavailable()).snapshot).toBeNull()

    for (const status of ['forbidden', 'notFound', 'unavailable'] as const) {
      const pending = layoutStateReducer(acknowledged, layoutRequestStarted(
        layoutRequestId, 'layout.read', layoutProjectId,
      ))
      const closed = layoutStateReducer(pending, layoutResponseReceived(parseRequired(
        createLayoutFailureValue(status),
      )))
      expect(closed.snapshot).toBeNull()
      expect(closed.status).toBe(status)
      expect(layoutStateReducer(closed, layoutBridgeUnavailable()).snapshot).toBeNull()
    }
  })

  it.each(['conflict', 'invalid'] as const)(
    'does not fabricate or retain foreign evidence for a %s response',
    (status) => {
      const initialRead = layoutStateReducer(initialLayoutState, layoutRequestStarted(
        layoutRequestId, 'layout.read', layoutProjectId,
      ))
      expect(layoutStateReducer(initialRead, layoutResponseReceived(parseRequired(
        createLayoutFailureValue(status),
      ))).snapshot).toBeNull()

      for (const method of ['layout.read', LAYOUT_APPLY_METHOD] as const) {
        const foreignRequest = layoutStateReducer(loadedState(), layoutRequestStarted(
          layoutSecondRequestId, method, layoutSecondProjectId,
        ))
        const failure = layoutResponseReceived(parseRequired(createLayoutFailureValue(
          status, layoutSecondRequestId, method,
        )))
        expect(layoutStateReducer(foreignRequest, failure).snapshot).toBeNull()

        const pending = layoutStateReducer(loadedState(), layoutRequestStarted(
          layoutSecondRequestId, method, layoutProjectId,
        ))
        const changed = layoutStateReducer(pending, layoutProjectIdChanged(layoutSecondProjectId))
        expect(layoutStateReducer(changed, failure).snapshot).toBeNull()
      }
    },
  )

  it.each(['forbidden', 'notFound', 'unavailable'] as const)(
    'closes a retained apply base when refresh reports %s',
    (status) => {
      const pending = layoutStateReducer(loadedState(), layoutRequestStarted(
        layoutSecondRequestId, LAYOUT_APPLY_METHOD, layoutProjectId,
      ))
      const failed = layoutStateReducer(pending, layoutResponseReceived(parseRequired(
        createLayoutFailureValue('conflict', layoutSecondRequestId, LAYOUT_APPLY_METHOD),
      )))
      const refreshing = layoutStateReducer(failed, layoutRequestStarted(
        layoutRequestId, 'layout.read', layoutProjectId,
      ))
      const closed = layoutStateReducer(refreshing, layoutResponseReceived(parseRequired(
        createLayoutFailureValue(status),
      )))
      expect(closed.snapshot).toBeNull()
      expect(closed.status).toBe(status)
    },
  )

  it.each(['conflict', 'invalid'] as const)(
    'does not recover a rejected %s apply from a read noChange without current evidence',
    (status) => {
      const loaded = loadedState()
      const pending = layoutStateReducer(loaded, layoutRequestStarted(
        layoutSecondRequestId, LAYOUT_APPLY_METHOD, layoutProjectId,
      ))
      const failed = layoutStateReducer(pending, layoutResponseReceived(parseRequired(
        createLayoutFailureValue(status, layoutSecondRequestId, LAYOUT_APPLY_METHOD),
      )))
      const refreshing = layoutStateReducer(failed, layoutRequestStarted(
        layoutRequestId, 'layout.read', layoutProjectId,
      ))
      const noEvidence = layoutStateReducer(refreshing, layoutResponseReceived(parseRequired(
        createLayoutFailureValue('noChange'),
      )))
      expect(noEvidence.snapshot).toBe(loaded.snapshot)
      expect(noEvidence.status).toBe(status)
      expect(noEvidence.pending).toBeNull()
      expect(noEvidence.lastCompletedMethod).toBe('layout.read')
    },
  )

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

function styleAcknowledgedState() {
  const pending = layoutStateReducer(loadedState(), layoutRequestStarted(
    layoutSecondRequestId, LAYOUT_APPLY_METHOD, layoutProjectId,
  ))
  const value = createLayoutResponseValue(layoutSecondRequestId, LAYOUT_APPLY_METHOD)
  value.result.snapshot.revision = 4
  const cue = value.result.snapshot.subtitles[0]!
  cue.style = 'emphasis'
  cue.revisionId = '82112222-3333-4444-8555-666677778888'
  cue.revisionDigest = '4'.repeat(64)
  cue.revisionCount++
  return layoutStateReducer(pending, layoutResponseReceived(parseRequired(value)))
}

function parseRequired(value: unknown): LayoutBridgeResponse {
  const parsed = parseLayoutBridgeResponse(value)
  if (parsed === null) {
    throw new Error('Expected a valid layout response fixture.')
  }
  return parsed
}
