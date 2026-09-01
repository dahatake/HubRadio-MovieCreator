// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import {
  parseReviewBridgeResponse,
  REVIEW_ADD_COMMENT_METHOD,
  REVIEW_GET_SNAPSHOT_METHOD,
  REVIEW_RECORD_APPROVAL_METHOD,
  type ReviewBridgeErrorResponse,
  type ReviewBridgeRequest,
  type ReviewBridgeResponse,
} from './reviewProtocol'
import { ReviewPanel } from './ReviewPanel'
import { ReviewStateProvider } from './ReviewStateProvider'
import type { ReviewBridgeClient } from './reviewBridgeSession'
import {
  createReviewResponseValue,
  reviewVersionDigest,
  reviewVersionId,
} from './reviewTestFixture'

describe('ReviewStateProvider integration', () => {
  let container: HTMLDivElement
  let root: Root
  let client: FakeReviewClient

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT =
      true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    client = new FakeReviewClient()
    act(() => {
      root.render(
        <ReviewStateProvider createClient={() => client}>
          <ReviewPanel />
        </ReviewStateProvider>,
      )
    })
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  it('loads an explicit version and sends an exact digest-bound timecoded comment', () => {
    loadSnapshot()
    const load = requiredSent(0)
    expect(load.method).toBe(REVIEW_GET_SNAPSHOT_METHOD)
    expect(load.params).toEqual({ versionId: reviewVersionId })

    completeSnapshot(load)
    expect(container.textContent).toContain(reviewVersionDigest)
    expect(container.textContent).toContain('出力 1/2')

    setControlValue(requiredElement<HTMLInputElement>('#review-comment-value'), '3')
    setControlValue(requiredElement<HTMLInputElement>('#review-comment-rate'), '2')
    setControlValue(
      requiredElement<HTMLSelectElement>('#review-comment-actor-role'),
      'rightsApprover',
    )
    setControlValue(
      requiredElement<HTMLTextAreaElement>('#review-comment-text'),
      '03/02 秒の字幕を確認してください。',
    )
    submit(requiredElement<HTMLFormElement>('#review-comment-text')
      .closest('form'))

    const comment = requiredSent(1)
    expect(comment.method).toBe(REVIEW_ADD_COMMENT_METHOD)
    expect(comment.params).toEqual({
      versionId: reviewVersionId,
      versionDigest: reviewVersionDigest,
      actorRole: 'rightsApprover',
      outputTime: { value: '3', rate: '2' },
      text: '03/02 秒の字幕を確認してください。',
    })
    expect(JSON.stringify(comment)).not.toContain('actorSid')
  })

  it('surfaces stale approval failure and reloads instead of retrying the mutation', () => {
    loadSnapshot()
    completeSnapshot(requiredSent(0))

    act(() => {
      requiredElement<HTMLButtonElement>(
        '#review-action-tab-recordApproval',
      ).click()
    })
    setControlValue(
      requiredElement<HTMLTextAreaElement>('#review-approval-reason'),
      '現在版の内容を承認します。',
    )
    submit(requiredElement<HTMLFormElement>('#review-approval-reason')
      .closest('form'))
    expect(client.sent).toHaveLength(1)

    setControlValue(
      requiredElement<HTMLSelectElement>('#review-approval-actor-role'),
      'contentApprover',
    )
    submit(requiredElement<HTMLFormElement>('#review-approval-reason')
      .closest('form'))

    const approval = requiredSent(1)
    expect(approval.method).toBe(REVIEW_RECORD_APPROVAL_METHOD)
    expect(approval.params).toEqual({
      versionId: reviewVersionId,
      versionDigest: reviewVersionDigest,
      actorRole: 'contentApprover',
      reason: '現在版の内容を承認します。',
    })
    const error: ReviewBridgeErrorResponse = {
      version: 1,
      requestId: approval.requestId,
      method: REVIEW_RECORD_APPROVAL_METHOD,
      error: {
        code: 'review.stale_version',
        message: '対象版は現行版ではありません。レビューを再読み込みしてください。',
      },
    }
    act(() => client.emit(error))

    const alert = requiredElement<HTMLElement>('.review-error[role="alert"]')
    expect(alert.textContent).toContain('対象版は現行版ではありません。')
    expect(container.textContent).toContain(reviewVersionDigest)

    act(() => {
      requiredElement<HTMLButtonElement>(
        '.review-error-actions .secondary-button',
      ).click()
    })
    const reload = requiredSent(2)
    expect(reload.method).toBe(REVIEW_GET_SNAPSHOT_METHOD)
    expect(reload.params).toEqual({ versionId: reviewVersionId })
    expect(client.sent.filter(
      (request) => request.method === REVIEW_RECORD_APPROVAL_METHOD,
    )).toHaveLength(1)
  })

  it('moves among review action panels with roving keyboard focus', async () => {
    loadSnapshot()
    completeSnapshot(requiredSent(0))
    const user = userEvent.setup({ document })
    const tabs = [...container.querySelectorAll<HTMLButtonElement>(
      '.review-action-tabs [role="tab"]',
    )]
    expect(tabs).toHaveLength(3)

    act(() => tabs[0]!.focus())
    await act(async () => user.keyboard('[End]'))
    expect(document.activeElement).toBe(tabs[2])
    expect(tabs[2]!.getAttribute('aria-selected')).toBe('true')
    expect(
      requiredElement<HTMLElement>('#review-action-panel-recordApproval')
        .hidden,
    ).toBe(false)

    await act(async () => user.keyboard('[Home]'))
    expect(document.activeElement).toBe(tabs[0])
    expect(tabs[0]!.getAttribute('aria-selected')).toBe('true')
  })

  it('abandons an in-flight request and disposes its native client on unmount', () => {
    loadSnapshot()
    const pending = requiredSent(0)

    act(() => root.unmount())
    expect(client.abandoned).toEqual([pending.requestId])
    expect(client.unsubscribeCount).toBe(1)
    expect(client.disposeCount).toBe(1)
    root = createRoot(container)
  })

  function loadSnapshot(): void {
    setControlValue(
      requiredElement<HTMLInputElement>('#review-version-id'),
      reviewVersionId,
    )
    submit(requiredElement<HTMLFormElement>('.review-load-form'))
  }

  function completeSnapshot(request: ReviewBridgeRequest): void {
    const value = createReviewResponseValue(request.requestId, request.method)
    const response = parseReviewBridgeResponse(value)
    if (response === null) {
      throw new Error('Expected a valid review snapshot response.')
    }
    act(() => client.emit(response))
  }

  function requiredSent(index: number): ReviewBridgeRequest {
    const request = client.sent[index]
    if (request === undefined) {
      throw new Error(`Expected sent review request ${index}.`)
    }
    return request
  }

  function requiredElement<T extends Element>(selector: string): T {
    const element = container.querySelector(selector)
    if (element === null) {
      throw new Error(`Expected element matching ${selector}.`)
    }
    return element as T
  }
})

class FakeReviewClient implements ReviewBridgeClient {
  readonly sent: ReviewBridgeRequest[] = []
  readonly abandoned: string[] = []
  unsubscribeCount = 0
  disposeCount = 0
  private listener: ((response: ReviewBridgeResponse) => void) | null = null

  sendReview(request: ReviewBridgeRequest): void {
    this.sent.push(request)
  }

  abandonReviewRequest(requestId: string): void {
    this.abandoned.push(requestId)
  }

  onReviewResponse(
    listener: (response: ReviewBridgeResponse) => void,
  ): () => void {
    this.listener = listener
    return () => {
      this.unsubscribeCount++
      this.listener = null
    }
  }

  dispose(): void {
    this.disposeCount++
    this.listener = null
  }

  emit(response: ReviewBridgeResponse): void {
    this.listener?.(response)
  }
}

function setControlValue(
  element: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement,
  value: string,
): void {
  const prototype = element instanceof HTMLTextAreaElement
    ? HTMLTextAreaElement.prototype
    : element instanceof HTMLSelectElement
      ? HTMLSelectElement.prototype
      : HTMLInputElement.prototype
  const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set
  if (setter === undefined) {
    throw new Error('Expected the native form-control value setter.')
  }
  act(() => {
    setter.call(element, value)
    element.dispatchEvent(new Event(
      element instanceof HTMLSelectElement ? 'change' : 'input',
      { bubbles: true },
    ))
  })
}

function submit(form: HTMLFormElement | null): void {
  if (form === null) {
    throw new Error('Expected a review form.')
  }
  act(() => {
    form.dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    )
  })
}
