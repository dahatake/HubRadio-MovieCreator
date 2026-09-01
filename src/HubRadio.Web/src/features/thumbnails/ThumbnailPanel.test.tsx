// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

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
} from './ThumbnailPanel'
import {
  createThumbnailCancelResponseValue,
  createThumbnailGenerateResponseValue,
  createThumbnailReadResponseValue,
  thumbnailEvidenceDigest,
  thumbnailGenerationId,
  thumbnailJobId,
  thumbnailOutputDigest,
  thumbnailPlayerEvidenceDigest,
  thumbnailProjectId,
  thumbnailRequestId,
  thumbnailSourceAssetId,
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

    expect(container.textContent).toContain('Source PTS-1/20frame 7')
    expect(container.textContent).toContain('Proxy PTS1001/30000frame 1')
    expect(container.textContent).toContain('Output PTS—')
    completeRead(client)

    expect(status().dataset.thumbnailStatus).toBe('ready')
    expect(container.textContent).toContain('外部公開可')
    expect(container.textContent).toContain('BrandApprover')
    expect(container.textContent).toContain('approval 777777')
    expect(container.textContent).toContain('template 888888')
    expect(container.textContent).toContain('profile 999999')
    expect(container.textContent).toContain('layout aaaaaa')
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
    expect(image.getAttribute('alt')).toContain('Source PTS -1/20')
    expect(container.textContent).toContain('output SHA-256')
    expect(container.textContent).toContain(thumbnailOutputDigest)
    expect(container.textContent).toContain('exact source PTS-1/20')
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
  ): void {
    act(() => {
      root.render(
        <ThumbnailPanel
          selection={createSelection()}
          createClient={() => client}
          createId={createIdSequence()}
          previewReduced={previewReduced}
        />,
      )
    })
  }

  function completeRead(client: FakeThumbnailClient): void {
    clickButton('信頼済み根拠を読み込む')
    const read = client.reads[0]
    if (read === undefined) throw new Error('Expected read request.')
    const value = createThumbnailReadResponseValue()
    value.requestId = read.requestId
    act(() => client.emitRead(requireReadResponse(value)))
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
