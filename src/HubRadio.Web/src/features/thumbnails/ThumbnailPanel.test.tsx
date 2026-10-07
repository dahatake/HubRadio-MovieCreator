// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { PlayerExactFrameSelection } from '../player'
import {
  parseThumbnailCancelResponse,
  parseThumbnailGenerateResponse,
  parseThumbnailReadResponse,
  type ThumbnailCancelResponse,
  type ThumbnailGenerateParams,
  type ThumbnailGenerateResponse,
  type ThumbnailReadResponse,
} from './thumbnailProtocol'
import {
  ThumbnailPanel,
  type ThumbnailBridgeClient,
  type ThumbnailPanelProps,
} from './ThumbnailPanel'
import {
  createThumbnailCancelResponseValue,
  createThumbnailGenerateResponseValue,
  createThumbnailReadResponseValue,
  thumbnailApprovalDigest,
  thumbnailEvidenceDigest,
  thumbnailGenerationId,
  thumbnailJobId,
  thumbnailLayoutDigest,
  thumbnailOutputDigest,
  thumbnailPlayerEvidenceDigest,
  thumbnailProfileDigest,
  thumbnailProjectId,
  thumbnailRequestId,
  thumbnailSourceAssetId,
  thumbnailTemplateDigest,
  thumbnailTemplateId,
} from './thumbnailTestFixture'

const generationRequestId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const cancelRequestId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'

describe('ThumbnailPanel', () => {
  let container: HTMLDivElement
  let root: Root

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT =
      true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  it('shows exact Source, Proxy, and Output domains plus genuine approval evidence', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client)

    // FR-052: the selected frame is described by plain source/output positions, without raw PTS rationals or proxy frame numbers.
    expect(container.textContent).toContain('元動画の場面-0:00.1') // FR-052: a negative source time (-1/20) is shown as -0:00.1
    expect(container.textContent).toContain('完成した動画での位置不明')
    expect(container.textContent).not.toMatch(/PTS|-1\/20|1001\/30000|frame \d/)
    completeRead(client)

    expect(status().dataset.thumbnailStatus).toBe('ready')
    expect(container.textContent).toContain('外部公開可')
    // FR-052: template approval is shown as a date; approval role codes and digests are not shown.
    expect(container.textContent).toContain('承認日時')
    expect(container.textContent).not.toContain('BrandApprover')
    for (const digest of [thumbnailApprovalDigest, thumbnailTemplateDigest, thumbnailProfileDigest, thumbnailLayoutDigest]) {
      expect(container.textContent).not.toContain(digest)
    }
    expect(container.innerHTML).not.toMatch(/face|quality|recommend|score/i)
    expect(container.innerHTML).not.toContain('file://')
    expect(container.innerHTML).not.toContain('C:\\')
    expect(container.innerHTML).not.toContain('S-1-5-')
  })

  it('sends only identity and evidence bindings, then renders digest-bound PNG evidence', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client)
    completeRead(client)

    clickButton('選択フレームから生成')

    expect(client.generates).toEqual([
      {
        requestId: generationRequestId,
        params: {
          jobId: thumbnailJobId,
          generationId: thumbnailGenerationId,
          digest: thumbnailEvidenceDigest,
          playerDigest: thumbnailPlayerEvidenceDigest,
          templateId: thumbnailTemplateId,
          proxyFrameSequence: 1,
        },
      },
    ])
    expect(status().dataset.thumbnailStatus).toBe('generating')
    expect(requiredButton('生成をキャンセル').disabled).toBe(false)

    const value = createThumbnailGenerateResponseValue()
    value.requestId = generationRequestId
    act(() => client.emitGenerate(requireGenerateResponse(value)))

    expect(status().dataset.thumbnailStatus).toBe('generated')
    const image = requiredElement<HTMLImageElement>('.thumbnail-preview img')
    expect(image.getAttribute('src')).toContain('/thumbnail/')
    expect(image.getAttribute('src')).toContain(thumbnailOutputDigest)
    // FR-052: the generated PNG is described by scene and size; digests and raw PTS stay out of visible text.
    // FR-052: the negative source time (-1/20) is shown as -0:00.1.
    expect(image.getAttribute('alt')).toContain('元動画の -0:00.1 の場面から作ったサムネイル')
    expect(image.getAttribute('alt')).not.toMatch(/PTS|-1\/20/)
    expect(requiredElement('.thumbnail-output figcaption').textContent).toContain('2.0 KB')
    expect(container.textContent).toContain('生成結果の詳細')
    expect(container.textContent).not.toContain('SHA-256')
    expect(container.textContent).not.toContain(thumbnailOutputDigest)
    expect(container.textContent).not.toMatch(/PTS|-1\/20/)
  })

  it('loads evidence and starts generation with keyboard activation', async () => {
    const client = new FakeThumbnailClient()
    renderPanel(client)
    const user = userEvent.setup({ document })
    const load = requiredButton('信頼済み根拠を読み込む')
    act(() => load.focus())
    await act(async () => user.keyboard('[Enter]'))

    const read = client.reads[0]
    if (read === undefined) throw new Error('Expected read request.')
    const value = createThumbnailReadResponseValue()
    value.requestId = read.requestId
    act(() => client.emitRead(requireReadResponse(value)))

    const generate = requiredButton('選択フレームから生成')
    act(() => generate.focus())
    await act(async () => user.keyboard('[Enter]'))
    expect(document.activeElement).toBe(generate)
    expect(client.generates).toHaveLength(1)
    expect(client.generates[0]?.params.proxyFrameSequence).toBe(1)
  })

  it('reduces only the preview presentation and keeps native output dimensions', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client, true)
    completeRead(client)
    clickButton('選択フレームから生成')

    const value = createThumbnailGenerateResponseValue()
    value.requestId = generationRequestId
    const output = (
      value.result as {
        output: { width: number; height: number }
      }
    ).output
    act(() => client.emitGenerate(requireGenerateResponse(value)))

    expect(
      requiredElement<HTMLElement>('.thumbnail-card').dataset.thumbnailPreview,
    ).toBe('reduced')
    const image = requiredElement<HTMLImageElement>('.thumbnail-preview img')
    expect(image.getAttribute('width')).toBe(String(output.width))
    expect(image.getAttribute('height')).toBe(String(output.height))
    expect(container.textContent).toContain('縮小表示（拡大なし）')
  })

  it('keeps generation pending while cancellation is acknowledged, then accepts cancelled completion', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client)
    completeRead(client)
    clickButton('選択フレームから生成')
    clickButton('生成をキャンセル')

    expect(client.cancellations).toEqual([
      {
        requestId: cancelRequestId,
        generationId: thumbnailGenerationId,
      },
    ])
    expect(status().dataset.thumbnailStatus).toBe('cancelling')
    expect(requiredButton('生成をキャンセル').disabled).toBe(true)

    const cancelValue = createThumbnailCancelResponseValue('requested')
    cancelValue.requestId = cancelRequestId
    act(() => client.emitCancel(requireCancelResponse(cancelValue)))
    expect(status().dataset.thumbnailStatus).toBe('generating')
    expect(container.textContent).toContain('キャンセル要求を送信しました')

    act(() =>
      client.emitGenerate(
        requireGenerateResponse({
          version: 1,
          requestId: generationRequestId,
          method: 'thumbnail.generate',
          result: { status: 'cancelled', output: null },
        }),
      ),
    )
    expect(status().dataset.thumbnailStatus).toBe('cancelled')
    expect(requiredButton('生成をキャンセル').disabled).toBe(true)
  })

  it.each([
    ['approved', false],
    ['unconfirmed', false],
    ['rejected', false],
  ] as const)(
    'shows and holds canonical rights state %s/%s',
    (rightsStatus, externalPublicationAllowed) => {
      const client = new FakeThumbnailClient()
      renderPanel(client)
      clickButton('信頼済み根拠を読み込む')
      const read = client.reads[0]
      if (read === undefined) throw new Error('Expected read request.')
      const held = createThumbnailReadResponseValue()
      held.requestId = read.requestId
      held.result.snapshot.rights.status = rightsStatus
      held.result.snapshot.rights.externalPublicationAllowed =
        externalPublicationAllowed
      act(() => client.emitRead(requireReadResponse(held)))

      expect(container.textContent).toContain('生成不可')
      expect(container.textContent).toContain(`権利状態${rightsStatus}`)
      expect(requiredButton('選択フレームから生成').disabled).toBe(true)
      expect(client.generates).toEqual([])
    },
  )

  it('fails closed when player evidence changes or source PTS reaches the exclusive end', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client)
    completeRead(client)

    const changed: PlayerExactFrameSelection = {
      ...createSelection(),
      playerEvidenceDigest: 'f'.repeat(64),
    }
    act(() => {
      root.render(
        <ThumbnailPanel
          selection={changed}
          createClient={() => client}
          createId={createIdSequence()}
        />,
      )
    })
    expect(container.textContent).toContain('根拠と一致しません')
    expect(requiredButton('選択フレームから生成').disabled).toBe(true)

    const exclusiveEnd: PlayerExactFrameSelection = {
      ...createSelection(),
      frame: {
        ...createSelection().frame,
        sourcePresentationTime: { value: '7', rate: '60' },
      },
    }
    act(() => {
      root.render(
        <ThumbnailPanel
          selection={exclusiveEnd}
          createClient={() => client}
          createId={createIdSequence()}
        />,
      )
    })
    expect(container.textContent).toContain('根拠と一致しません')
    expect(requiredButton('選択フレームから生成').disabled).toBe(true)
  })

  it('starts hidden and inert without sending a read or a generation request', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client, false, { active: false })

    const panel = requiredElement<HTMLElement>('.thumbnail-card')
    expect(panel.hidden).toBe(true)
    expect(panel.hasAttribute('inert')).toBe(true)
    expect(requiredButton('信頼済み根拠を読み込む').disabled).toBe(true)
    clickButton('信頼済み根拠を読み込む')
    expect(client.reads).toEqual([])
    expect(client.generates).toEqual([])
    expect(client.cancellations).toEqual([])

    renderPanel(client)
    expect(panel.hidden).toBe(false)
    expect(panel.hasAttribute('inert')).toBe(false)
    expect(requiredButton('信頼済み根拠を読み込む').disabled).toBe(false)
  })

  it('retains a non-default template, exact frame, and completed PNG while hidden and on return', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client)
    const readValue = createThumbnailReadResponseValue()
    const alternate = {
      ...readValue.result.snapshot.templates[0]!,
      templateId: cancelRequestId,
      name: 'Another approved thumbnail',
    }
    readValue.result.snapshot.templates.push(alternate)
    completeRead(client, readValue)
    const selected = requiredElement<HTMLInputElement>(
      `input[value="${alternate.templateId}"]`,
    )
    act(() => selected.click())
    clickButton('選択フレームから生成')
    const generatedValue = createThumbnailGenerateResponseValue()
    generatedValue.result.output.template.templateId = alternate.templateId
    completeGeneration(client, generatedValue)
    const image = requiredElement<HTMLImageElement>('.thumbnail-preview img')
    expect(selected.checked).toBe(true)
    expect(client.generates[0]?.params.templateId).toBe(alternate.templateId)

    renderPanel(client, false, { active: false })
    expect(requiredElement<HTMLElement>('.thumbnail-card').hidden).toBe(true)
    expect(requiredElement('input:checked')).toBe(selected)
    expect(requiredElement('.thumbnail-preview img')).toBe(image)
    expect(requiredElement('fieldset').matches(':disabled')).toBe(true)
    clickButton('選択フレームから生成')
    clickButton('生成をキャンセル')

    renderPanel(client)
    expect(requiredElement('input:checked')).toBe(selected)
    expect(requiredElement('.thumbnail-preview img')).toBe(image)
    expect(image.getAttribute('alt')).toContain(alternate.name)
    // FR-052: the retained frame is shown without raw PTS rationals.
    expect(container.textContent).toContain('元動画の場面-0:00.1') // FR-052: a negative source time (-1/20) is shown as -0:00.1
    expect(container.textContent).not.toMatch(/PTS|-1\/20/)
    expect(status().dataset.thumbnailStatus).toBe('generated')
    expect(client.reads).toHaveLength(1)
    expect(client.generates).toHaveLength(1)
    expect(client.cancellations).toEqual([])
    expect(client.listenerCount).toBe(3)
    expect(client.disposed).toBe(false)
  })

  it('keeps pending generation across visibility changes and accepts hidden completion without moving focus', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client)
    completeRead(client)
    clickButton('選択フレームから生成')

    renderPanel(client, false, { active: false })
    expect(status().dataset.thumbnailStatus).toBe('generating')
    expect(requiredButton('生成をキャンセル').disabled).toBe(true)
    clickButton('生成をキャンセル')
    expect(client.cancellations).toEqual([])

    renderPanel(client)
    expect(status().dataset.thumbnailStatus).toBe('generating')
    expect(requiredButton('生成をキャンセル').disabled).toBe(false)
    renderPanel(client, false, { active: false })

    const outside = document.createElement('button')
    document.body.append(outside)
    act(() => outside.focus())
    const focus = vi.spyOn(HTMLElement.prototype, 'focus')
    try {
      act(() => client.emitRead(requireReadResponse(createThumbnailReadResponseValue())))
      act(() => client.emitCancel(requireCancelResponse(createThumbnailCancelResponseValue())))
      completeGeneration(client)

      expect(requiredElement<HTMLElement>('.thumbnail-card').hidden).toBe(true)
      expect(status().dataset.thumbnailStatus).toBe('generated')
      expect(focus).not.toHaveBeenCalled()
      expect(document.activeElement).toBe(outside)

      renderPanel(client)
      expect(status().dataset.thumbnailStatus).toBe('generated')
      expect(requiredElement<HTMLImageElement>('.thumbnail-preview img').src)
        .toContain(thumbnailOutputDigest)
      expect(document.activeElement).toBe(outside)
      expect(focus).not.toHaveBeenCalled()
      expect(client.reads).toHaveLength(1)
      expect(client.generates).toHaveLength(1)
      expect(client.cancellations).toEqual([])
      expect(client.listenerCount).toBe(3)
      expect(client.disposed).toBe(false)
    } finally {
      focus.mockRestore()
      outside.remove()
    }
  })

  it.each([
    ['approved', true],
    ['approved', false],
    ['unconfirmed', true],
    ['unconfirmed', false],
    ['rejected', true],
    ['rejected', false],
  ] as const)(
    'keeps internal workflow read-only and unavailable independently of legacy rights %s/%s',
    (rightsStatus, externalPublicationAllowed) => {
      const client = new FakeThumbnailClient()
      renderPanel(client, false, { context: { projectId: thumbnailProjectId } })
      expect(status().textContent).toBe('社内向けサムネイル生成は未接続です')
      // FR-052: the selected frame is described by plain source/output positions, without raw PTS rationals or proxy frame numbers.
      expect(container.textContent).toContain('元動画の場面-0:00.1') // FR-052: a negative source time (-1/20) is shown as -0:00.1
      expect(container.textContent).toContain('完成した動画での位置不明')
      expect(container.textContent).not.toMatch(/PTS|-1\/20|1001\/30000|frame \d/)

      const value = createThumbnailReadResponseValue()
      value.result.snapshot.rights.status = rightsStatus
      value.result.snapshot.rights.externalPublicationAllowed = externalPublicationAllowed
      completeRead(client, value)

      expect(status().dataset.thumbnailStatus).toBe('unavailable')
      expect(status().textContent).toBe('社内向けサムネイル生成は未接続です')
      expect(container.textContent).toContain(`権利状態${rightsStatus}`)
      expect(container.textContent).toContain('既存の外部公開用 API の根拠（参照のみ）')
      expect(container.textContent).not.toContain('生成不可')
      expect(container.textContent).not.toContain('場合にのみ生成できます')
      expect(container.querySelector('input, .thumbnail-actions, img, video, canvas, svg')).toBeNull()
      expect(container.innerHTML).not.toMatch(/data:image|blob:|\.gif/i)
      expect([...container.querySelectorAll('button')].map((button) => button.textContent))
        .toEqual(['信頼済み根拠を読み込む'])
      expect(client.reads).toEqual([{ requestId: thumbnailRequestId, jobId: thumbnailJobId }])
      expect(client.generates).toEqual([])
      expect(client.cancellations).toEqual([])
    },
  )

  it.each([
    { name: 'missing frame', selection: null, projectId: thumbnailProjectId },
    { name: 'missing project', selection: createSelection(), projectId: null },
    { name: 'different project', selection: createSelection(), projectId: cancelRequestId },
  ])('shows only the short workflow unavailable state for $name', ({ selection, projectId }) => {
    const client = new FakeThumbnailClient()
    renderPanel(client, false, { selection, context: { projectId } })

    expect(status().dataset.thumbnailStatus).toBe('unavailable')
    expect(status().textContent).toBe('サムネイルを表示する元フレームは未取得です')
    expect(container.querySelector('.thumbnail-time-grid, button, img, video, canvas, svg')).toBeNull()
    expect(container.textContent).not.toContain('上の代理動画プレイヤー')
    expect(container.innerHTML).not.toMatch(/data:image|blob:|\.gif/i)
    expect(client.reads).toEqual([])
    expect(client.generates).toEqual([])
    expect(client.cancellations).toEqual([])
  })

  it('never presents a late legacy completion as an internal or different-project workflow result', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client)
    completeRead(client)
    clickButton('選択フレームから生成')

    const otherContext = { projectId: cancelRequestId }
    renderPanel(client, false, { context: otherContext })
    completeGeneration(client)
    expect(status().textContent).toBe('サムネイルを表示する元フレームは未取得です')
    expect(container.querySelector('img, .thumbnail-time-grid, .thumbnail-evidence-list')).toBeNull()

    renderPanel(client, false, {
      context: otherContext,
      selection: { ...createSelection(), projectId: otherContext.projectId },
    })
    expect(status().textContent).toBe('社内向けサムネイル生成は未接続です')
    expect(container.querySelector('img, .thumbnail-evidence-list')).toBeNull()

    renderPanel(client, false, { context: { projectId: thumbnailProjectId } })
    expect(status().textContent).toBe('社内向けサムネイル生成は未接続です')
    expect(container.querySelector('img, .thumbnail-actions')).toBeNull()
    expect(container.querySelector('.thumbnail-evidence-list')).not.toBeNull()

    renderPanel(client)
    expect(status().dataset.thumbnailStatus).toBe('generated')
    expect(requiredElement<HTMLImageElement>('.thumbnail-preview img').src)
      .toContain(thumbnailOutputDigest)
    expect(client.generates).toHaveLength(1)
    expect(client.cancellations).toEqual([])
  })

  it('hides an old PNG and generated header for a different exact frame without losing the completed result', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client)
    completeRead(client)
    clickButton('選択フレームから生成')
    completeGeneration(client)

    renderPanel(client, false, {
      selection: {
        ...createSelection(),
        frame: {
          ...createSelection().frame,
          proxyFrameSequence: 2,
          sourceFrameSequence: 8,
          sourcePresentationTime: { value: '0', rate: '1' },
        },
      },
    })
    expect(status().dataset.thumbnailStatus).toBe('mismatched')
    expect(container.querySelector('img')).toBeNull()
    // FR-052: the newly selected frame is shown as a plain playback position.
    expect(container.textContent).toContain('元動画の場面0:00')
    expect(container.textContent).not.toMatch(/PTS|0\/1|frame 8/)

    renderPanel(client)
    expect(status().dataset.thumbnailStatus).toBe('generated')
    expect(requiredElement<HTMLImageElement>('.thumbnail-preview img').src)
      .toContain(thumbnailOutputDigest)
    expect(client.generates).toHaveLength(1)
    expect(client.cancellations).toEqual([])
  })

  it('keeps full hashes in collapsed details rather than the result header', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client)
    completeRead(client)
    clickButton('選択フレームから生成')
    completeGeneration(client)

    // FR-052: full hashes are no longer shown anywhere, so neither the header nor the collapsed details contain them.
    const digests = [...container.querySelectorAll('code')].filter(
      (code) => /[0-9a-f]{64}/.test(code.textContent ?? ''),
    )
    expect(digests).toHaveLength(0)
    expect(container.textContent).not.toMatch(/[0-9a-f]{64}/)
    const details = requiredElement<HTMLDetailsElement>('.thumbnail-output details')
    expect(details.open).toBe(false)
    expect(requiredElement('.thumbnail-heading').textContent).not.toMatch(/[0-9a-f]{64}/)
  })

  it('removes all native listeners and disposes its client', () => {
    const client = new FakeThumbnailClient()
    renderPanel(client)
    expect(client.listenerCount).toBe(3)

    act(() => root.unmount())

    expect(client.listenerCount).toBe(0)
    expect(client.disposed).toBe(true)
    root = createRoot(container)
  })

  function renderPanel(
    client: FakeThumbnailClient,
    previewReduced = false,
    props: Partial<ThumbnailPanelProps> = {},
  ): void {
    act(() => {
      root.render(
        <ThumbnailPanel
          selection={createSelection()}
          createClient={() => client}
          createId={createIdSequence()}
          previewReduced={previewReduced}
          {...props}
        />,
      )
    })
  }

  function completeRead(
    client: FakeThumbnailClient,
    value = createThumbnailReadResponseValue(),
  ): void {
    clickButton('信頼済み根拠を読み込む')
    const read = client.reads.at(-1)
    if (read === undefined) throw new Error('Expected read request.')
    value.requestId = read.requestId
    act(() => client.emitRead(requireReadResponse(value)))
  }

  function completeGeneration(
    client: FakeThumbnailClient,
    value = createThumbnailGenerateResponseValue(),
  ): void {
    const generate = client.generates.at(-1)
    if (generate === undefined) throw new Error('Expected generation request.')
    value.requestId = generate.requestId
    value.result.output.generationId = generate.params.generationId
    act(() => client.emitGenerate(requireGenerateResponse(value)))
  }

  function clickButton(text: string): void {
    act(() => requiredButton(text).click())
  }

  function requiredButton(text: string): HTMLButtonElement {
    const button = [...container.querySelectorAll('button')].find(
      (candidate) => candidate.textContent === text,
    )
    if (button === undefined) {
      throw new Error(`Expected button ${text}.`)
    }
    return button
  }

  function status(): HTMLElement {
    return requiredElement('[role="status"]')
  }

  function requiredElement<T extends Element>(selector: string): T {
    const element = container.querySelector(selector)
    if (element === null) throw new Error(`Expected element matching ${selector}.`)
    return element as T
  }
})

class FakeThumbnailClient implements ThumbnailBridgeClient {
  readonly reads: Array<Readonly<{ requestId: string; jobId: string }>> = []
  readonly generates: Array<
    Readonly<{ requestId: string; params: ThumbnailGenerateParams }>
  > = []
  readonly cancellations: Array<
    Readonly<{ requestId: string; generationId: string }>
  > = []
  disposed = false
  private readonly readListeners = new Set<
    (response: ThumbnailReadResponse) => void
  >()
  private readonly generateListeners = new Set<
    (response: ThumbnailGenerateResponse) => void
  >()
  private readonly cancelListeners = new Set<
    (response: ThumbnailCancelResponse) => void
  >()

  get listenerCount(): number {
    return (
      this.readListeners.size +
      this.generateListeners.size +
      this.cancelListeners.size
    )
  }

  sendThumbnailRead(requestId: string, jobId: string): void {
    this.reads.push({ requestId, jobId })
  }

  onThumbnailReadResponse(
    listener: (response: ThumbnailReadResponse) => void,
  ): () => void {
    this.readListeners.add(listener)
    return () => this.readListeners.delete(listener)
  }

  sendThumbnailGenerate(
    requestId: string,
    params: ThumbnailGenerateParams,
  ): void {
    this.generates.push({ requestId, params })
  }

  onThumbnailGenerateResponse(
    listener: (response: ThumbnailGenerateResponse) => void,
  ): () => void {
    this.generateListeners.add(listener)
    return () => this.generateListeners.delete(listener)
  }

  sendThumbnailCancellation(requestId: string, generationId: string): void {
    this.cancellations.push({ requestId, generationId })
  }

  onThumbnailCancelResponse(
    listener: (response: ThumbnailCancelResponse) => void,
  ): () => void {
    this.cancelListeners.add(listener)
    return () => this.cancelListeners.delete(listener)
  }

  emitRead(response: ThumbnailReadResponse): void {
    for (const listener of this.readListeners) listener(response)
  }

  emitGenerate(response: ThumbnailGenerateResponse): void {
    for (const listener of this.generateListeners) listener(response)
  }

  emitCancel(response: ThumbnailCancelResponse): void {
    for (const listener of this.cancelListeners) listener(response)
  }

  dispose(): void {
    this.disposed = true
    this.readListeners.clear()
    this.generateListeners.clear()
    this.cancelListeners.clear()
  }
}

function createSelection(): PlayerExactFrameSelection {
  return {
    jobId: thumbnailJobId,
    projectId: thumbnailProjectId,
    sourceAssetId: thumbnailSourceAssetId,
    playerEvidenceDigest: thumbnailPlayerEvidenceDigest,
    frame: {
      proxyFrameSequence: 1,
      proxyPresentationTime: { value: '1001', rate: '30000' },
      sourceFrameSequence: 7,
      sourcePresentationTime: { value: '-1', rate: '20' },
      outputPresentationTime: null,
      proxyDuration: { value: '1', rate: '30' },
      waveformChannels: [],
      spriteCell: null,
    },
  }
}

function createIdSequence(): () => string {
  const values = [
    thumbnailRequestId,
    thumbnailGenerationId,
    generationRequestId,
    cancelRequestId,
  ]
  let index = 0
  return () => values[index++] ?? 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'
}

function requireReadResponse(value: unknown): ThumbnailReadResponse {
  const response = parseThumbnailReadResponse(value)
  if (response === null) throw new Error('Expected thumbnail read response.')
  return response
}

function requireGenerateResponse(value: unknown): ThumbnailGenerateResponse {
  const response = parseThumbnailGenerateResponse(value)
  if (response === null) throw new Error('Expected thumbnail generate response.')
  return response
}

function requireCancelResponse(value: unknown): ThumbnailCancelResponse {
  const response = parseThumbnailCancelResponse(value)
  if (response === null) throw new Error('Expected thumbnail cancel response.')
  return response
}
