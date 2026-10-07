// @vitest-environment jsdom

import { getByRole, queryByRole } from '@testing-library/dom'
import { act, StrictMode, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  parseReviewBridgeResponse,
  REVIEW_ADD_COMMENT_METHOD,
  REVIEW_GET_SNAPSHOT_METHOD,
  REVIEW_RECORD_APPROVAL_METHOD,
  type ReviewAction,
  type ReviewBridgeErrorCode,
  type ReviewBridgeRequest,
  type ReviewBridgeResponse,
  type ReviewBridgeResultResponse,
  type ReviewSnapshot,
} from './reviewProtocol'
import {
  getReviewActionAfterKey,
  ReviewPanel,
  ReviewPanelView,
  type ReviewPanelProps,
} from './ReviewPanel'
import { ReviewStateProvider, type ReviewStateContextValue } from './ReviewStateProvider'
import type { ReviewBridgeClient } from './reviewBridgeSession'
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
const otherVersionId = '81112222-3333-4444-8555-666677778888'
const otherVersionDigest = 'c'.repeat(64)

describe('ReviewPanel', () => {
  // FR-052: the pinned target, comment time, approvals and diff are shown in plain language without IDs/digests.
  it('renders the exact target, full digest, exact comment time, approvals, and direct-parent diff', () => {
    const markup = renderSnapshot(snapshot())
    // The standalone load form keeps the user-entered version ID as its input value; only displayed content is checked here.
    const displayed = markup.replace(/<input[^>]*>/g, '')

    for (const identifier of [
      reviewVersionId, reviewVersionDigest, reviewSourceSpanId, reviewParentVersionId, reviewParentDigest, reviewElementId,
    ]) {
      expect(displayed).not.toContain(identifier)
    }
    expect(markup).not.toContain(`${reviewVersionDigest.slice(0, 12)}…`)
    expect(markup).toContain('内容レビュー')
    expect(markup).toContain('動画の 0:00.5 の位置')
    expect(markup).not.toContain('1/2')
    expect(markup).toContain('字幕の語尾を確認してください。')
    expect(markup).toContain('有効')
    expect(markup).toContain('失効')
    expect(markup).toContain('<dt>直接の親版</dt><dd>あり</dd>')
    expect(markup).toContain('失効した承認は別の構成版に固定された履歴です。')
    expect(markup).toContain('直接の親版との差分 1 件を表示')
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

  it.each([null, '', 'NOT-A-VERSION', otherVersionId])(
    'does not render previous evidence even before effects run for context %s',
    (versionId) => {
      const markup = renderToStaticMarkup(
        <ReviewPanelView
          {...contextFor({
            ...initialReviewState,
            versionIdInput: reviewVersionId,
            snapshot: snapshot(),
            lastCompletedMethod: REVIEW_RECORD_APPROVAL_METHOD,
          }, {})}
          context={{ versionId }}
        />,
      )
      expect(markup).toContain('旧形式のレビュー履歴')
      expect(markup).not.toContain('id="review-version-id"')
      expect(markup).not.toContain(reviewVersionDigest)
      expect(markup).not.toContain('字幕の語尾を確認してください。')
      expect(markup).not.toContain('承認を記録しました。')
      expect(markup).not.toContain('id="review-comment-text"')
    },
  )
})

describe('ReviewPanel UX037 with the actual ReviewStateProvider', () => {
  let container: HTMLDivElement
  let root: Root
  let client: PanelReviewClient
  let createClient: () => ReviewBridgeClient

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    client = new PanelReviewClient()
    createClient = () => client
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    vi.restoreAllMocks()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT
  })

  it('synchronizes the provider input before loading the actual context ID once', async () => {
    await renderPanel()
    expect(client.sent).toEqual([])
    setControlValue(control('#review-version-id'), otherVersionId)
    expect(client.sent).toEqual([])

    await renderPanel({ context: { versionId: reviewVersionId } })
    expect(client.sent).toHaveLength(1)
    expect(sent(0).method).toBe(REVIEW_GET_SNAPSHOT_METHOD)
    expect(sent(0).params).toEqual({ versionId: reviewVersionId })
    expect(container.querySelector('#review-version-id')).toBeNull()
    await complete(sent(0))

    await renderPanel({ context: { versionId: reviewVersionId }, active: false })
    await renderPanel({ context: { versionId: reviewVersionId }, active: true })
    await renderPanel({ context: { versionId: reviewVersionId } })
    expect(client.sent).toHaveLength(1)
    // FR-052: the loaded version is recognised by its content, not by its digest.
    expectReviewVersionShown()

    await renderPanel({ context: { versionId: otherVersionId } })
    expect(client.sent).toHaveLength(2)
    expect(sent(1).params).toEqual({ versionId: otherVersionId })
    expect(container.textContent).not.toContain(reviewVersionDigest)
    expect(container.querySelector('#review-comment-text')).toBeNull()
    await complete(sent(1))
    // FR-052: the loaded version is recognised by its content, not by its digest.
    expectOtherVersionShown()
  })

  it('waits until active and survives StrictMode without sending to an uninstalled session', async () => {
    await act(async () => {
      root.render(
        <StrictMode>
          <ReviewStateProvider createClient={createClient}>
            <ReviewPanel context={{ versionId: reviewVersionId }} active={false} />
          </ReviewStateProvider>
        </StrictMode>,
      )
    })
    expect(client.sent).toEqual([])
    await act(async () => {
      root.render(
        <StrictMode>
          <ReviewStateProvider createClient={createClient}>
            <ReviewPanel context={{ versionId: reviewVersionId }} />
          </ReviewStateProvider>
        </StrictMode>,
      )
    })
    expect(client.sent).toHaveLength(1)
    expect(sent(0).params).toEqual({ versionId: reviewVersionId })
    await complete(sent(0))
    expect(container.querySelector('.review-error')).toBeNull()
    // FR-052: the loaded version is recognised by its content, not by its digest.
    expectReviewVersionShown()
  })

  it.each([
    null,
    '',
    'NOT-A-VERSION',
    ' 11112222-3333-4444-8555-666677778888 ',
    'A1112222-3333-4444-8555-666677778888',
    '00000000-0000-0000-0000-000000000000',
  ])('treats invalid or null context %s as unselected, never as a legacy fallback', async (versionId) => {
    const onDirtyChange = vi.fn()
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
    await complete(sent(0))
    await renderPanel({ context: { versionId }, onDirtyChange })

    expect(client.sent).toHaveLength(1)
    expect(container.querySelector('#review-version-id')).toBeNull()
    expect(container.querySelector('.review-workspace')).toBeNull()
    expect(container.textContent).toContain('レビュー対象は未選択です')
    expect(container.textContent).not.toContain(reviewVersionDigest)
    expect(container.textContent).not.toContain('字幕の語尾を確認してください。')
    expect(onDirtyChange.mock.calls).toEqual([[false]])

    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
    expect(client.sent).toHaveLength(2)
    expect(container.querySelector('.review-workspace')).toBeNull()
    await complete(sent(1))
    // FR-052: the loaded version is recognised by its content, not by its digest.
    expectReviewVersionShown()
  })

  it('holds a pending read across target changes and never displays its late old response', async () => {
    const onDirtyChange = vi.fn()
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
    const oldRead = sent(0)
    await renderPanel({ context: { versionId: otherVersionId }, onDirtyChange })
    expect(client.sent).toHaveLength(1)
    expect(container.textContent).toContain('未保存の入力・処理を保持しています。')
    expect(container.querySelector('.review-workspace')).toBeNull()
    expect(onDirtyChange.mock.calls).toEqual([[false]])

    await complete(oldRead)
    expect(client.sent).toHaveLength(2)
    expect(sent(1).params).toEqual({ versionId: otherVersionId })
    expect(container.textContent).not.toContain(reviewVersionDigest)
    await complete(sent(1))
    // FR-052: the loaded version is recognised by its content, not by its digest.
    expectOtherVersionShown()
    expect(onDirtyChange.mock.calls).toEqual([[false]])
  })

  it('does not loop when the parent recreates context and the dirty callback with object state', async () => {
    const notifications = vi.fn()
    function InlineParent({ tick = 0 }: { tick?: number }) {
      const [flags, setFlags] = useState({ review: false })
      return (
        <ReviewStateProvider createClient={createClient}>
          <span data-dirty={String(flags.review)} data-tick={tick} />
          <ReviewPanel
            context={{ versionId: reviewVersionId }}
            onDirtyChange={(dirty) => {
              notifications(dirty)
              if (notifications.mock.calls.length > 10) throw new Error('Dirty notification loop')
              setFlags((previous) => ({ ...previous, review: dirty }))
            }}
          />
        </ReviewStateProvider>
      )
    }

    await act(async () => { root.render(<InlineParent />) })
    await complete(sent(0))
    expect(notifications.mock.calls).toEqual([[false]])
    fillComment()
    expect(notifications.mock.calls).toEqual([[false], [true]])
    await act(async () => { root.render(<InlineParent tick={1} />) })
    expect(notifications.mock.calls).toEqual([[false], [true]])
    expect(client.sent).toHaveLength(1)

    submitComment()
    expect(notifications.mock.calls).toEqual([[false], [true]])
    await complete(sent(1))
    expect(notifications.mock.calls).toEqual([[false], [true], [false]])
    expect(container.querySelector('[data-dirty]')?.getAttribute('data-dirty')).toBe('false')
    expect(client.sent).toHaveLength(2)
  })

  it('notifies only dirty transitions and uses the latest callback without cleanup churn', async () => {
    const first = vi.fn()
    const second = vi.fn()
    const third = vi.fn()
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange: first })
    await complete(sent(0))
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange: second })
    expect(first.mock.calls).toEqual([[false]])
    expect(second).not.toHaveBeenCalled()
    setControlValue(control('#review-comment-text'), '未保存のコメント')
    expect(second.mock.calls).toEqual([[true]])
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange: third })
    expect(third).not.toHaveBeenCalled()
    click('未保存の入力を取り消す')
    expect(third.mock.calls).toEqual([[false]])
    expect(first.mock.calls).toEqual([[false]])
    expect(second.mock.calls).toEqual([[true]])
  })

  it.each([
    ['#review-comment-value', '3'],
    ['#review-comment-rate', '2'],
    ['#review-comment-text', ' '],
    ['#review-comment-actor-role', 'rightsApprover'],
  ])('retains a draft in %s across hidden, invalid, and different targets until explicit cancellation', async (selector, value) => {
    const onDirtyChange = vi.fn()
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
    await complete(sent(0))
    setControlValue(control(selector), value)
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])

    for (const target of [otherVersionId, null, 'NOT-A-VERSION']) {
      await renderPanel({ context: { versionId: target }, active: false, onDirtyChange })
      await renderPanel({ context: { versionId: target }, active: true, onDirtyChange })
      expect(container.querySelector('.review-workspace')).toBeNull()
      expect(container.textContent).not.toContain(reviewVersionDigest)
      expect(container.textContent).toContain('未保存の入力・処理を保持しています。')
      expect(client.sent).toHaveLength(1)
      expect(onDirtyChange).toHaveBeenLastCalledWith(true)
      await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
      expect(control(selector).value).toBe(value)
    }

    await renderPanel({ context: { versionId: otherVersionId }, onDirtyChange })
    click('未保存の入力を取り消す')
    // Flush the deferred read for the newly accepted selection, without running any timers.
    await act(async () => {})
    expect(client.sent).toHaveLength(2)
    expect(sent(1).params).toEqual({ versionId: otherVersionId })
    await complete(sent(1))
    expect(control('#review-comment-text').value).toBe('')
    expect(control('#review-comment-value').value).toBe('0')
    expect(control('#review-comment-rate').value).toBe('1')
    expect(control('#review-comment-actor-role').value).toBe('')
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
  })

  it('sends only an explicit, native-role-authorized legacy comment and waits for its actual acknowledgement', async () => {
    const onDirtyChange = vi.fn()
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
    await complete(sent(0))
    setControlValue(control('#review-comment-value'), '3')
    setControlValue(control('#review-comment-rate'), '2')
    setControlValue(control('#review-comment-text'), '03/02 秒の字幕を確認してください。')
    submitComment()
    expect(client.sent).toHaveLength(1)
    setControlValue(control('#review-comment-actor-role'), 'rightsApprover')
    submitComment()

    const request = sent(1)
    expect(request.method).toBe(REVIEW_ADD_COMMENT_METHOD)
    expect(request.params).toEqual({
      versionId: reviewVersionId,
      versionDigest: reviewVersionDigest,
      actorRole: 'rightsApprover',
      outputTime: { value: '3', rate: '2' },
      text: '03/02 秒の字幕を確認してください。',
    })
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
    expect(control('#review-comment-text').value).toBe('03/02 秒の字幕を確認してください。')
    expect(control('#review-comment-text').matches(':disabled')).toBe(true)
    expect(getByRole(container, 'button', { name: '未保存の入力を取り消す' }).matches(':disabled')).toBe(true)

    await renderPanel({ context: { versionId: otherVersionId }, onDirtyChange })
    expect(client.sent).toHaveLength(2)
    expect(container.querySelector('.review-workspace')).toBeNull()
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
    await complete(request)
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
    expect(control('#review-comment-text').value).toBe('03/02 秒の字幕を確認してください。')
    expect(container.querySelector('.review-comment-list')?.textContent).toContain('03/02 秒の字幕を確認してください。')
    expect(container.querySelector('.review-operation-status')?.textContent).toBe('旧形式の履歴にコメントを記録しました。')
    expect(client.sent.map((item) => item.method)).toEqual([REVIEW_GET_SNAPSHOT_METHOD, REVIEW_ADD_COMMENT_METHOD])

    // Even unchanged, already saved input becomes dirty while another send is pending.
    submitComment()
    expect(onDirtyChange).toHaveBeenLastCalledWith(true)
    await reject(sent(2), 'review.operation_failed')
    expect(onDirtyChange).toHaveBeenLastCalledWith(false)
    expect(control('#review-comment-text').value).toBe('03/02 秒の字幕を確認してください。')
    expect(container.querySelector('.review-operation-status')?.textContent).not.toContain('記録しました')
  })

  it.each(['review.conflict', 'review.stale_version', 'review.not_authorized', 'review.operation_failed'] as const)(
    'keeps unsaved comments dirty after %s and never retries or invents a success',
    async (code) => {
      const onDirtyChange = vi.fn()
      await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
      await complete(sent(0))
      fillComment()
      submitComment()
      await renderPanel({ context: { versionId: otherVersionId }, onDirtyChange })
      await reject(sent(1), code)
      expect(client.sent).toHaveLength(2)
      expect(onDirtyChange).toHaveBeenLastCalledWith(true)
      expect(container.querySelector('.review-workspace')).toBeNull()
      await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
      expect(control('#review-comment-text').value).toBe('字幕を確認してください。')
      expect(control('#review-comment-actor-role').value).toBe('rightsApprover')
      expect(container.querySelector('.review-error')?.textContent).toContain('記録できませんでした。')
      expect(container.querySelector('.review-operation-status')?.textContent).not.toContain('記録しました')
      click('履歴を再読み込み')
      await complete(sent(2))
      expect(onDirtyChange).toHaveBeenLastCalledWith(true)
      expect(control('#review-comment-text').value).toBe('字幕を確認してください。')
      expect(client.sent.filter((item) => item.method === REVIEW_ADD_COMMENT_METHOD)).toHaveLength(1)
    },
  )

  it('keeps a draft after a synchronous transport failure without retry loops', async () => {
    const onDirtyChange = vi.fn()
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
    await complete(sent(0))
    fillComment()
    client.failSend = true
    submitComment()
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
    expect(client.sent).toHaveLength(2)
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
    expect(control('#review-comment-text').value).toBe('字幕を確認してください。')
    expect(container.textContent).toContain('ネイティブホストへレビュー要求を送信できませんでした。')
    expect(container.querySelector('.review-operation-status')?.textContent).not.toContain('記録しました')
  })

  it('retains hidden drafts, blurs without refocusing, and rejects synthetic hidden actions', async () => {
    const onDirtyChange = vi.fn()
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
    await complete(sent(0))
    fillComment()
    const textarea = control('#review-comment-text')
    act(() => textarea.focus())
    const blur = vi.spyOn(textarea, 'blur')
    await renderPanel({ context: { versionId: reviewVersionId }, active: false, onDirtyChange })
    const panel = container.querySelector<HTMLElement>('.review-panel')!
    expect(panel.hidden).toBe(true)
    expect(panel.hasAttribute('inert')).toBe(true)
    expect(blur).toHaveBeenCalled()
    expect(queryByRole(container, 'button', { name: 'コメントを記録' })).toBeNull()
    expect(textarea.matches(':disabled')).toBe(true)
    setControlValue(textarea, '非表示中の変更')
    submitComment()
    expect(client.sent).toHaveLength(1)
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])

    const outside = document.createElement('button')
    document.body.append(outside)
    try {
      act(() => outside.focus())
      // jsdom does not faithfully blur a disabled input; verify the explicit
      // blur call above and external-focus preservation on reactivation instead.
      await renderPanel({ context: { versionId: reviewVersionId }, active: true, onDirtyChange })
      expect(document.activeElement).toBe(outside)
      expect(control('#review-comment-text')).toBe(textarea)
      expect(textarea.value).toBe('字幕を確認してください。')
      expect(panel.hidden).toBe(false)
      expect(panel.hasAttribute('inert')).toBe(false)
      expect(onDirtyChange).toHaveBeenLastCalledWith(true)
      expect(client.sent).toHaveLength(1)
    } finally {
      outside.remove()
    }
  })

  it.each(['authorized', 'actorNotAuthorized', 'targetNotCurrent'] as const)(
    'exposes only optional comments supported by the native permission model (%s)',
    async (status) => {
      const onDirtyChange = vi.fn()
      await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
      const base = snapshot()
      const actions: readonly ReviewAction[] = status === 'targetNotCurrent'
        ? base.actions.map((action) => ({ ...action, status, actorRoles: [] }))
        : base.actions.map((action) => action.kind === 'addComment'
          ? { ...action, status, actorRoles: status === 'authorized' ? action.actorRoles : [] }
          : action)
      await complete(sent(0), { actions, isCurrentTarget: status !== 'targetNotCurrent' })
      expect(container.textContent).toContain('旧形式のレビュー履歴')
      expect(container.textContent).toContain('自己レビューの完了記録は未接続です。')
      expect(container.textContent).toContain('コメントは任意で、書き出しの必須条件ではありません。')
      expect(container.querySelector('#review-comment-text') !== null).toBe(status === 'authorized')
      expect(container.querySelector('#review-changes-text')).toBeNull()
      expect(container.querySelector('#review-approval-reason')).toBeNull()
      expect(container.querySelector('[role="tablist"]')).toBeNull()
      expect(container.querySelector('.review-approval-table')).not.toBeNull()
      expect(container.querySelector('.review-comment-list')).not.toBeNull()
      expect(container.querySelector('[required]')).toBeNull()
      expect(container.textContent).not.toMatch(/SelfReviewed|自己レビュー完了済み|PASS|エクスポート可能|最終レンダーを開始/)
      expect(client.sent.map((request) => request.method)).toEqual([REVIEW_GET_SNAPSHOT_METHOD])
      expect(onDirtyChange.mock.calls).toEqual([[false]])
    },
  )

  it.each([null, 'approved', 'publicationBlocked'] as const)(
    'keeps legacy state %s as history, not an automatic self-review result or export gate',
    async (reviewState) => {
      const onDirtyChange = vi.fn()
      await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange })
      const value = createReviewResponseValue()
      value.result.reviewState = reviewState
      value.result.actions[1]!.status = reviewState === null
        ? 'reviewStateUnrecorded' : reviewState === 'publicationBlocked'
          ? 'publicationBlocked' : 'stateNotEligible'
      value.result.actions[1]!.actorRoles = []
      if (reviewState === null) {
        value.result.reviewStateReason = null
        value.result.reviewStateRecordedAt = null
        value.result.reviewStateActorRole = null
      }
      await complete(sent(0), parseSnapshot(value))
      expect(container.querySelector('.review-operation-status')?.textContent).toBe('旧形式の履歴を表示しています。')
      expect(container.textContent).toContain('旧形式の履歴から自己レビュー完了・書き出し可否を判定しません。')
      expect(container.querySelector('.publication-blocked')).toBeNull()
      expect(container.querySelector('#review-approval-reason')).toBeNull()
      expect(container.querySelector('.review-state-badge')?.textContent).toBe(
        reviewState === null ? '状態未記録' : reviewState === 'approved' ? '承認済み' : '公開停止',
      )
      expect(onDirtyChange.mock.calls).toEqual([[false]])
      expect(client.sent).toHaveLength(1)
    },
  )

  it('leaves unavailable history and self-review unconfirmed instead of phantom PASS', async () => {
    const onDirtyChange = vi.fn()
    const unavailable = vi.fn((): ReviewBridgeClient => { throw new Error('Host unavailable') })
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange }, unavailable)
    await renderPanel({ context: { versionId: reviewVersionId }, onDirtyChange }, unavailable)
    expect(unavailable).toHaveBeenCalledTimes(1)
    expect(client.sent).toEqual([])
    expect(container.querySelector('.review-workspace')).toBeNull()
    expect(container.textContent).toContain('自己レビューの完了記録は未接続です。')
    expect(container.textContent).toContain('旧形式の履歴を確認できませんでした。')
    expect(container.textContent).not.toMatch(/PASS|SelfReviewed|承認済み|記録しました|エクスポート可能/)
    expect(onDirtyChange.mock.calls).toEqual([[false]])
    click('閉じる')
    expect(container.querySelector('.review-operation-status')?.textContent).toBe('旧形式の履歴は未取得です。')
    expect(unavailable).toHaveBeenCalledTimes(1)
  })

  it('preserves standalone legacy actions, their drafts, and the keyboard selection while hidden', async () => {
    const onDirtyChange = vi.fn()
    await renderPanel({ onDirtyChange })
    expect(client.sent).toEqual([])
    setControlValue(control('#review-version-id'), reviewVersionId)
    submitForm(container.querySelector('.review-load-form'))
    await complete(sent(0))
    expect(container.querySelectorAll('[role="tab"]')).toHaveLength(3)
    setControlValue(control('#review-comment-text'), '単体画面のコメント')
    setControlValue(control('#review-changes-text'), '単体画面の変更依頼')
    setControlValue(control('#review-approval-reason'), '単体画面の承認理由')
    click('承認')
    const firstTab = getByRole(container, 'tab', { name: 'コメント' })
    const approvalTab = getByRole(container, 'tab', { name: '承認' })
    const focus = vi.spyOn(firstTab, 'focus')
    await renderPanel({ active: false, onDirtyChange })
    act(() => approvalTab.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true })))
    expect(focus).not.toHaveBeenCalled()
    expect(approvalTab.getAttribute('aria-selected')).toBe('true')
    await renderPanel({ onDirtyChange })
    expect(control('#review-comment-text').value).toBe('単体画面のコメント')
    expect(control('#review-changes-text').value).toBe('単体画面の変更依頼')
    expect(control('#review-approval-reason').value).toBe('単体画面の承認理由')
    setControlValue(control('#review-version-id'), otherVersionId)
    expect(control('#review-version-id').value).toBe(reviewVersionId)
    expect(container.textContent).toContain('未保存の入力・処理を保持しています。')
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
    expect(client.sent).toHaveLength(1)

    setControlValue(control('#review-approval-actor-role'), 'contentApprover')
    submitForm(control('#review-approval-reason').closest('form'))
    expect(sent(1).method).toBe(REVIEW_RECORD_APPROVAL_METHOD)
    await complete(sent(1))
    // Other forms still contain unsaved input; an approval does not clear them.
    expect(onDirtyChange).toHaveBeenLastCalledWith(true)
    click('未保存の入力を取り消す')
    expect(onDirtyChange).toHaveBeenLastCalledWith(false)
  })

  it('does not let an external provider target change reuse an unsaved draft', async () => {
    const onDirtyChange = vi.fn()
    const changeVersionId = vi.fn()
    const loadSnapshot = vi.fn()
    const renderView = async (current: ReviewSnapshot) => {
      await act(async () => {
        root.render(
          <ReviewPanelView
            {...contextFor({
              ...initialReviewState,
              versionIdInput: current.versionId,
              snapshot: current,
            }, { changeVersionId, loadSnapshot })}
            context={{ versionId: current.versionId }}
            onDirtyChange={onDirtyChange}
          />,
        )
      })
    }
    await renderView(snapshot())
    setControlValue(control('#review-comment-text'), '元の版だけのコメント')
    const before = loadSnapshot.mock.calls.length
    await renderView(snapshotForVersion(otherVersionId))
    expect(container.querySelector('.review-workspace')).toBeNull()
    expect(container.textContent).toContain('未保存の入力・処理を保持しています。')
    expect(loadSnapshot).toHaveBeenCalledTimes(before)
    expect(changeVersionId).not.toHaveBeenCalled()
    expect(onDirtyChange).toHaveBeenLastCalledWith(true)
    await renderView(snapshot())
    expect(control('#review-comment-text').value).toBe('元の版だけのコメント')
  })

  async function renderPanel(
    props: ReviewPanelProps = {},
    factory = createClient,
  ): Promise<void> {
    await act(async () => {
      root.render(
        <ReviewStateProvider createClient={factory}>
          <ReviewPanel {...props} />
        </ReviewStateProvider>,
      )
    })
  }

  async function complete(
    request: ReviewBridgeRequest,
    overrides: Partial<ReviewSnapshot> = {},
  ): Promise<void> {
    const base = snapshotForVersion(request.params.versionId)
    const result: ReviewSnapshot = request.method === REVIEW_ADD_COMMENT_METHOD
      ? {
          ...base,
          comments: [...base.comments, {
            id: request.requestId,
            outputTime: request.params.outputTime,
            sourceSpanId: reviewSourceSpanId,
            actorRole: request.params.actorRole,
            createdAt: base.createdAt,
            text: request.params.text,
          }],
          ...overrides,
        }
      : { ...base, ...overrides }
    const response = parseReviewBridgeResponse({
      version: 1, requestId: request.requestId, method: request.method, result,
    })
    if (response === null) throw new Error('Expected a valid native review response')
    await act(async () => { client.emit(response) })
  }

  async function reject(request: ReviewBridgeRequest, code: ReviewBridgeErrorCode): Promise<void> {
    await act(async () => {
      client.emit({
        version: 1,
        requestId: request.requestId,
        method: request.method,
        error: { code, message: '記録できませんでした。' },
      })
    })
  }

  function sent(index: number): ReviewBridgeRequest {
    const request = client.sent[index]
    if (request === undefined) throw new Error(`Expected review request ${index}`)
    return request
  }

  function control(selector: string): HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement {
    const element = container.querySelector(selector)
    if (!(element instanceof HTMLInputElement) && !(element instanceof HTMLTextAreaElement) &&
      !(element instanceof HTMLSelectElement)) throw new Error(`Expected form control ${selector}`)
    return element
  }

  function click(name: string): void {
    act(() => getByRole(container, name === '承認' ? 'tab' : 'button', { name }).click())
  }

  function fillComment(): void {
    setControlValue(control('#review-comment-text'), '字幕を確認してください。')
    setControlValue(control('#review-comment-actor-role'), 'rightsApprover')
  }

  function submitComment(): void {
    submitForm(control('#review-comment-text').closest('form'))
  }

  function expectReviewVersionShown(): void {
    expect(container.querySelector('.review-workspace')).not.toBeNull()
    expect(container.textContent).toContain('字幕の語尾を確認してください。')
    expect(container.querySelectorAll('[data-approval-validity="valid"]')).toHaveLength(1)
    expect(container.textContent).not.toContain(reviewVersionId)
    expect(container.textContent).not.toContain(reviewVersionDigest)
  }

  function expectOtherVersionShown(): void {
    expect(container.querySelector('.review-workspace')).not.toBeNull()
    expect(container.textContent).not.toContain('字幕の語尾を確認してください。')
    expect(container.querySelectorAll('[data-approval-validity="valid"]')).toHaveLength(0)
    expect(container.querySelectorAll('[data-approval-validity="stale"]')).toHaveLength(2)
    expect(container.textContent).not.toContain(otherVersionId)
    expect(container.textContent).not.toContain(otherVersionDigest)
  }
})

class PanelReviewClient implements ReviewBridgeClient {
  readonly sent: ReviewBridgeRequest[] = []
  failSend = false
  private listener: ((response: ReviewBridgeResponse) => void) | null = null

  sendReview(request: ReviewBridgeRequest): void {
    this.sent.push(request)
    if (this.failSend) throw new Error('Transport unavailable')
  }

  abandonReviewRequest(_requestId: string): void {}

  onReviewResponse(listener: (response: ReviewBridgeResponse) => void): () => void {
    this.listener = listener
    return () => { this.listener = null }
  }

  dispose(): void { this.listener = null }

  emit(response: ReviewBridgeResponse): void { this.listener?.(response) }
}

function setControlValue(
  element: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement,
  value: string,
): void {
  const prototype = element instanceof HTMLTextAreaElement
    ? HTMLTextAreaElement.prototype : element instanceof HTMLSelectElement
      ? HTMLSelectElement.prototype : HTMLInputElement.prototype
  const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set
  if (setter === undefined) throw new Error('Expected the native value setter')
  act(() => {
    setter.call(element, value)
    element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }))
  })
}

function submitForm(form: HTMLFormElement | null): void {
  if (form === null) throw new Error('Expected a review form')
  act(() => { form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })) })
}

function snapshotForVersion(versionId: string): ReviewSnapshot {
  const base = snapshot()
  if (versionId === reviewVersionId) return base
  return {
    ...base,
    versionId,
    versionDigest: otherVersionDigest,
    parentVersionId: reviewVersionId,
    comments: [],
    approvals: base.approvals.map((approval) => ({ ...approval, validity: 'stale' })),
    directParentDiff: {
      fromVersionId: reviewVersionId,
      toVersionId: versionId,
      changes: [],
    },
  }
}

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
