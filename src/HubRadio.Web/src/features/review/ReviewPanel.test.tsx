import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'

import {
  parseReviewBridgeResponse,
  type ReviewAction,
  type ReviewBridgeResultResponse,
  type ReviewSnapshot,
} from './reviewProtocol'
import { getReviewActionAfterKey, ReviewPanelView } from './ReviewPanel'
import type { ReviewStateContextValue } from './ReviewStateProvider'
import { initialReviewState, type ReviewUiState } from './reviewState'
import {
  createReviewResponseValue,
  reviewElementId,
  reviewParentDigest,
  reviewParentVersionId,
  reviewSourceSpanId,
  reviewVersionDigest,
  reviewVersionId,
} from './reviewTestFixture'

const adversarialText =
  '<script>globalThis.compromised=true</script><img src=x onerror=alert(1)>&tail'

describe('ReviewPanel', () => {
  it('renders the exact target, full digest, exact comment time, approvals, and direct-parent diff', () => {
    const markup = renderSnapshot(snapshot())

    expect(markup).toContain(reviewVersionId)
    expect(markup).toContain(reviewVersionDigest)
    expect(markup).not.toContain(`${reviewVersionDigest.slice(0, 12)}…`)
    expect(markup).toContain('内容レビュー')
    expect(markup).toContain('出力 1/2')
    expect(markup).toContain(reviewSourceSpanId)
    expect(markup).toContain('字幕の語尾を確認してください。')
    expect(markup).toContain('有効')
    expect(markup).toContain('失効')
    expect(markup).toContain(reviewParentVersionId)
    expect(markup).toContain(reviewParentDigest)
    expect(markup).toContain('失効した承認は別の構成版に固定された履歴です。')
    expect(markup).toContain('直接の親版との差分 1 件を表示')
    expect(markup).toContain(reviewElementId)
    expect(markup).toContain('位置 0 → 0')
  })

  it('renders an explicit unrecorded state without fabricating editing', () => {
    const value = createReviewResponseValue()
    value.result.reviewState = null
    value.result.reviewStateReason = null
    value.result.reviewStateRecordedAt = null
    value.result.reviewStateActorRole = null
    value.result.actions[1]!.status = 'reviewStateUnrecorded'
    value.result.actions[1]!.actorRoles = []
    const markup = renderSnapshot(parseSnapshot(value))

    expect(markup).toContain('状態未記録')
    expect(markup).toContain('レビュー状態は未記録です。')
    expect(markup).toContain('`editing` として推測・補完していません。')
  })

  it('treats publication blocked as terminal and offers no recovery action', () => {
    const value = createReviewResponseValue()
    value.result.reviewState = 'publicationBlocked'
    value.result.reviewStateReason = '個人情報の公開範囲を確認できません。'
    value.result.actions[1]!.status = 'publicationBlocked'
    value.result.actions[1]!.actorRoles = []
    const markup = renderSnapshot(parseSnapshot(value))

    expect(markup).toContain('この構成版は公開停止です')
    expect(markup).toContain('個人情報の公開範囲を確認できません。')
    expect(markup).toContain('公開停止はこの版の終端状態です。')
    expect(markup).toContain('この画面は復旧操作を提供しません。')
    expect(markup).not.toContain('公開停止を解除')
    expect(markup).not.toContain('編集へ戻す')
  })

  it('does not infer policy prerequisites or claim render or export authorization', () => {
    const markup = renderSnapshot(snapshot())

    expect(markup).not.toContain('Workflow prerequisites')
    expect(markup).not.toContain('必要な承認根拠の記録')
    expect(markup).not.toContain('エクスポート可能')
    expect(markup).not.toContain('最終レンダーを開始')
  })

  it('uses native action statuses verbatim and disables every denied action', () => {
    const deniedActions: readonly ReviewAction[] = [
      { kind: 'addComment', status: 'actorNotAuthorized', actorRoles: [] },
      { kind: 'requestChanges', status: 'stateNotEligible', actorRoles: [] },
      { kind: 'recordApproval', status: 'targetNotCurrent', actorRoles: [] },
    ]
    const markup = renderSnapshot({ ...snapshot(), actions: deniedActions })

    expect(markup).toContain(
      '現在の Windows ユーザーには、この操作の役割が割り当てられていません。',
    )
    expect(markup).toContain('現在のレビュー状態では、この操作を実行できません。')
    expect(markup).toContain('この版は現行の作業対象ではないため、書き込みできません。')
    expect(markup.match(/<fieldset disabled="">/g)).toHaveLength(3)
  })

  it('requires an explicit choice among native-authorized acting roles', () => {
    const markup = renderSnapshot(snapshot())

    expect(markup).toContain('この操作で使用する役割')
    expect(markup).toContain('役割を選択')
    expect(markup).toContain('value="contentApprover"')
    expect(markup).toContain('value="rightsApprover"')
    expect(markup).toContain('現在の割り当てと再照合されます。')
  })

  it('provides a bounded accessible keyboard tab flow', () => {
    expect(getReviewActionAfterKey(0, 'ArrowRight')).toBe(1)
    expect(getReviewActionAfterKey(1, 'ArrowDown')).toBe(2)
    expect(getReviewActionAfterKey(2, 'ArrowRight')).toBe(0)
    expect(getReviewActionAfterKey(0, 'ArrowLeft')).toBe(2)
    expect(getReviewActionAfterKey(2, 'ArrowUp')).toBe(1)
    expect(getReviewActionAfterKey(1, 'Home')).toBe(0)
    expect(getReviewActionAfterKey(0, 'End')).toBe(2)
    expect(getReviewActionAfterKey(0, 'Tab')).toBeNull()
    expect(getReviewActionAfterKey(-1, 'ArrowRight')).toBeNull()
    expect(getReviewActionAfterKey(3, 'ArrowLeft')).toBeNull()

    const markup = renderSnapshot(snapshot())
    expect(markup).toContain('role="tablist"')
    expect(markup.match(/role="tab"/g)).toHaveLength(3)
    expect(markup.match(/role="tabpanel"/g)).toHaveLength(3)
  })

  it('escapes adversarial native review text and exposes no identity or path', () => {
    const base = snapshot()
    const hostile: ReviewSnapshot = {
      ...base,
      reviewStateReason: adversarialText,
      comments: [{ ...base.comments[0]!, text: adversarialText }],
      approvals: [{ ...base.approvals[0]!, reason: adversarialText }],
    }
    const markup = renderSnapshot(hostile)

    expect(markup).toContain('&lt;script&gt;')
    expect(markup).toContain('&lt;img')
    expect(markup).toContain('&amp;tail')
    expect(markup).not.toContain('<script')
    expect(markup).not.toContain('<img')
    expect(markup).not.toContain('S-1-5-')
    expect(markup).not.toContain('C:\\')
  })

  it('requires an explicit canonical version ID before loading', () => {
    const loadSnapshot = vi.fn()
    const emptyMarkup = renderContext({
      ...initialReviewState,
      versionIdInput: 'NOT-A-VERSION',
    }, { loadSnapshot })
    expect(emptyMarkup).toMatch(
      /<button type="submit" disabled="">レビューを開く<\/button>/,
    )
    expect(emptyMarkup).toContain('レビュー対象は未選択です')

    const validMarkup = renderContext({
      ...initialReviewState,
      versionIdInput: reviewVersionId,
    }, { loadSnapshot })
    expect(validMarkup).toMatch(
      /<button type="submit">レビューを開く<\/button>/,
    )
  })
})

function renderSnapshot(value: ReviewSnapshot): string {
  return renderContext({
    ...initialReviewState,
    versionIdInput: value.versionId,
    snapshot: value,
  })
}

function renderContext(
  state: ReviewUiState,
  overrides: Partial<ReviewStateContextValue> = {},
): string {
  return renderToStaticMarkup(
    <ReviewPanelView {...contextFor(state, overrides)} />,
  )
}

function contextFor(
  state: ReviewUiState,
  overrides: Partial<ReviewStateContextValue>,
): ReviewStateContextValue {
  return {
    state,
    changeVersionId: () => undefined,
    loadSnapshot: () => undefined,
    refreshSnapshot: () => undefined,
    addComment: () => undefined,
    requestChanges: () => undefined,
    recordApproval: () => undefined,
    clearError: () => undefined,
    ...overrides,
  }
}

function snapshot(): ReviewSnapshot {
  return parseSnapshot(createReviewResponseValue())
}

function parseSnapshot(value: ReturnType<typeof createReviewResponseValue>): ReviewSnapshot {
  const parsed = parseReviewBridgeResponse(value)
  if (parsed === null || !('result' in parsed)) {
    throw new Error('Expected a valid review snapshot response.')
  }
  return (parsed as ReviewBridgeResultResponse).result
}
