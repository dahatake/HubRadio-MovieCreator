// @vitest-environment jsdom

import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import {
  PlayerNavigationProvider,
  usePlayerSourceSeekRegistration,
  type PlayerSourceSeekHandler,
} from '../player'
import {
  parseAnalysisResponse,
  type AnalysisRequest,
  type AnalysisResponse,
} from './analysisProtocol'
import {
  AnalysisPanelView,
  type AnalysisPanelViewProps,
} from './AnalysisPanel'
import type { AnalysisBridgeClient } from './analysisBridgeSession'
import {
  analysisDigest,
  analysisMetadataRevisionId,
  analysisProjectId,
  analysisRevisionId,
  analysisTopicId,
  createAllProducerCandidates,
  createAnalysisResponseValue,
} from './analysisTestFixture'

describe('AnalysisPanelView', () => {
  let container: HTMLDivElement
  let root: Root

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
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

  it('integrates speakers, topics, every producer, safe sensitivity, and persistence state', () => {
    const client = renderAvailable()

    expect(client.sent[0]).toMatchObject({
      method: 'analysis.read',
      params: { projectId: analysisProjectId },
    })
    expect(container.textContent).toContain('佐藤 花子')
    expect(container.textContent).toContain('クラウド運用')
    expect(container.textContent).toContain('用語辞書')
    expect(container.textContent).toContain('無音解析')
    expect(container.textContent).toContain('編集指示語句')
    expect(container.textContent).toContain('反復解析')
    expect(container.textContent).toContain('トピック境界解析')
    expect(container.textContent).toContain('音声レビュー')
    expect(container.textContent).toContain('文脈逸脱解析')
    expect(container.textContent).toContain('強調解析')
    expect(container.textContent).toContain('見どころ候補')
    expect(container.textContent).toContain('削除候補')
    expect(container.textContent).toContain('注意候補')
    expect(container.textContent).toContain('確度未提供')
    expect(container.textContent).toContain('認証情報形式の可能性')
    expect(container.textContent).toContain('一致した実値は表示せず')
    expect(container.textContent).toContain('自動保存されません')
    expect(container.textContent).toContain('未永続化')
    expect(container.querySelector('a[href="#transcript-title"]')).not.toBeNull()
    expect(container.textContent).not.toContain('S-1-5-')
    expect(container.textContent).not.toContain('C:\\')

    for (const label of [
      'タイトルを保存',
      '要約を保存',
      '主話者を保存',
      'キーワードを保存',
      '重要発言の元区間 IDを保存',
      '対象視聴者を保存',
      '公開時の注意を保存',
    ]) {
      expect(buttonByText(label)).not.toBeNull()
    }

    const technicalWarning = cardContaining('技術的な注意', '音声レビュー')
    expect(buttonTexts(technicalWarning)).toContain('却下')
    expect(buttonTexts(technicalWarning)).not.toContain('採用')
  })

  it('navigates speaker, topic, candidate, and sensitivity entries by source time', () => {
    const client = new FakeAnalysisClient()
    const sought: Parameters<PlayerSourceSeekHandler>[0][] = []
    render(client, (target) => {
      sought.push(target)
      return 'requested'
    })
    emitRead(client)

    clickButton('話者発話の元時刻へ移動')
    clickButton('トピックの元時刻へ移動')
    for (const producer of [
      '用語辞書',
      '無音解析',
      '編集指示語句',
      '反復解析',
      'トピック境界解析',
      '音声レビュー',
      '文脈逸脱解析',
      '強調解析',
    ]) {
      clickButtonIn(cardContaining(producer), '元時刻へ移動')
    }
    clickButton('指摘の元時刻へ移動')

    expect(sought).toEqual([
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
      { projectId: analysisProjectId, sourceTime: { value: '0', rate: '1' } },
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
    ])
    expect(container.textContent).toContain('元時刻への移動をプレイヤーへ要求しました。')
  })

  it('adopts with analysis OCC evidence and no web-supplied target', () => {
    const client = renderAvailable()
    setText(requiredElement<HTMLInputElement>('.analysis-reason input'), '候補を確認')
    clickButtonIn(cardContaining('用語修正候補', '用語辞書'), '採用')

    expect(client.sent[1]).toEqual({
      version: 1,
      requestId: client.sent[1]!.requestId,
      method: 'analysis.adopt',
      params: {
        projectId: analysisProjectId,
        revisionId: analysisRevisionId,
        digest: analysisDigest,
        candidateId: createAllProducerCandidates()[0]!.candidateId,
        reason: '候補を確認',
      },
    })
    expect(JSON.stringify(client.sent[1])).not.toMatch(/elementId|cueId|splitOffset/u)
  })

  it('updates exactly one topic field with analysis and metadata OCC', () => {
    const client = renderAvailable()
    setText(requiredElement<HTMLInputElement>('.analysis-reason input'), 'タイトルを確認')
    const titleForm = formContaining('タイトル')
    setText(requiredElement<HTMLInputElement>('input', titleForm), 'クラウド運用の要点')
    submit(titleForm)

    expect(client.sent[1]).toEqual({
      version: 1,
      requestId: client.sent[1]!.requestId,
      method: 'analysis.updateTopic',
      params: {
        projectId: analysisProjectId,
        revisionId: analysisRevisionId,
        digest: analysisDigest,
        topicId: analysisTopicId,
        expectedTopicRevisionId: analysisMetadataRevisionId,
        update: { field: 'title', text: 'クラウド運用の要点' },
        reason: 'タイトルを確認',
      },
    })
  })

  it('rejects a technical warning but never offers adoption', () => {
    const client = renderAvailable()
    setText(requiredElement<HTMLInputElement>('.analysis-reason input'), '技術警告を確認')
    const card = cardContaining('技術的な注意', '音声レビュー')
    expect(buttonTexts(card)).not.toContain('採用')
    clickButtonIn(card, '却下')

    expect(client.sent[1]).toMatchObject({
      method: 'analysis.reject',
      params: {
        candidateId: createAllProducerCandidates()[5]!.candidateId,
        reason: '技術警告を確認',
      },
    })
  })

  it('requires an explicit reason and drops stale evidence before recovery', () => {
    const client = renderAvailable()
    clickButtonIn(cardContaining('用語修正候補', '用語辞書'), '採用')
    expect(client.sent).toHaveLength(1)
    expect(container.textContent).toContain('採否の理由を入力してください。')

    setText(requiredElement<HTMLInputElement>('.analysis-reason input'), '候補を確認')
    clickButtonIn(cardContaining('用語修正候補', '用語辞書'), '採用')
    emitStatus(client, 1, 'staleBase')

    expect(container.querySelector('.analysis-workspace')).toBeNull()
    expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('staleBase')
    const reload = buttonByText('最新版を読み込む')
    expect(reload.disabled).toBe(false)
  })

  it('cleans up its response listener and bridge client', () => {
    const client = new FakeAnalysisClient()
    render(client)
    expect(client.listenerCount).toBe(1)

    act(() => root.unmount())

    expect(client.listenerCount).toBe(0)
    expect(client.disposed).toBe(true)
    root = createRoot(container)
  })

  function renderAvailable(): FakeAnalysisClient {
    const client = new FakeAnalysisClient()
    render(client)
    emitRead(client)
    return client
  }

  function render(
    client: FakeAnalysisClient,
    sourceSeek?: PlayerSourceSeekHandler,
    props: Partial<AnalysisPanelViewProps> = {},
  ): void {
    act(() => {
      root.render(
        <PlayerNavigationProvider>
          {sourceSeek === undefined ? null : <SeekRegistration handler={sourceSeek} />}
          <AnalysisPanelView
            projectId={analysisProjectId}
            createClient={() => client}
            {...props}
          />
        </PlayerNavigationProvider>,
      )
    })
  }

  function emitRead(client: FakeAnalysisClient): void {
    const sent = client.sent[0]
    if (sent === undefined) throw new Error('Expected an analysis read request.')
    const value = createAnalysisResponseValue(
      'analysis.read',
      createAllProducerCandidates(),
    )
    value.requestId = sent.requestId
    client.emit(requireResponse(value))
  }

  function emitStatus(
    client: FakeAnalysisClient,
    sentIndex: number,
    status: 'staleBase' | 'conflict' | 'noChange',
  ): void {
    const sent = client.sent[sentIndex]
    if (sent === undefined) throw new Error('Expected an analysis request.')
    const value = createAnalysisResponseValue(sent.method)
    value.requestId = sent.requestId
    value.result = { status, snapshot: null }
    client.emit(requireResponse(value))
  }

  function requiredElement<T extends Element>(
    selector: string,
    parent: ParentNode = container,
  ): T {
    const element = parent.querySelector(selector)
    if (element === null) throw new Error(`Expected element matching ${selector}.`)
    return element as T
  }

  function buttonByText(text: string): HTMLButtonElement {
    const button = [...container.querySelectorAll('button')]
      .find((candidate) => candidate.textContent === text)
    if (button === undefined) throw new Error(`Expected button ${text}.`)
    return button
  }

  function clickButton(text: string): void {
    act(() => buttonByText(text).click())
  }

  function clickButtonIn(parent: ParentNode, text: string): void {
    const button = [...parent.querySelectorAll('button')]
      .find((candidate) => candidate.textContent === text)
    if (button === undefined) throw new Error(`Expected button ${text}.`)
    act(() => button.click())
  }

  function cardContaining(...texts: string[]): HTMLElement {
    const card = [...container.querySelectorAll<HTMLElement>('.analysis-candidate-card')]
      .find((candidate) => texts.every((text) => candidate.textContent?.includes(text)))
    if (card === undefined) throw new Error(`Expected candidate card containing ${texts.join(', ')}.`)
    return card
  }

  function formContaining(text: string): HTMLFormElement {
    const form = [...container.querySelectorAll<HTMLFormElement>('.analysis-topic-field')]
      .find((candidate) => candidate.querySelector('label')?.textContent?.startsWith(text))
    if (form === undefined) throw new Error(`Expected topic form ${text}.`)
    return form
  }

  function buttonTexts(parent: ParentNode): string[] {
    return [...parent.querySelectorAll('button')].map((button) => button.textContent ?? '')
  }

  function setText(element: HTMLInputElement, value: string): void {
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set
      if (setter === undefined) throw new Error('Expected the native input value setter.')
      setter.call(element, value)
      element.dispatchEvent(new Event('input', { bubbles: true }))
    })
  }

  function submit(form: HTMLFormElement): void {
    act(() => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
  }
})

function SeekRegistration({ handler }: Readonly<{ handler: PlayerSourceSeekHandler }>) {
  usePlayerSourceSeekRegistration(handler)
  return null
}

class FakeAnalysisClient implements AnalysisBridgeClient {
  readonly sent: AnalysisRequest[] = []
  disposed = false
  private readonly listeners = new Set<(response: AnalysisResponse) => void>()

  get listenerCount(): number {
    return this.listeners.size
  }

  sendAnalysis(request: AnalysisRequest): void {
    this.sent.push(request)
  }

  onAnalysisResponse(listener: (response: AnalysisResponse) => void): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  emit(response: AnalysisResponse): void {
    act(() => {
      for (const listener of this.listeners) listener(response)
    })
  }

  dispose(): void {
    this.disposed = true
    this.listeners.clear()
  }
}

function requireResponse(value: unknown): AnalysisResponse {
  const parsed = parseAnalysisResponse(value)
  if (parsed === null) throw new Error('Expected a valid analysis response fixture.')
  return parsed
}