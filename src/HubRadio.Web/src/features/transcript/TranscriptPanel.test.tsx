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
  parseTranscriptResponse,
  type TranscriptRequest,
  type TranscriptResponse,
} from './transcriptProtocol'
import {
  TranscriptPanelView,
  type TranscriptPanelViewProps,
} from './TranscriptPanel'
import type { TranscriptBridgeClient } from './transcriptBridgeSession'
import {
  createTranscriptResponseValue,
  performerId,
  projectId,
  revisionDigest,
  revisionId,
  speakerId,
  timedTokenId,
  transcriptRequestId,
  untimedTokenId,
} from './transcriptTestFixture'

describe('TranscriptPanelView', () => {
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

  it('renders exact hierarchy, confidence, untimed fallback, and escaped evidence', () => {
    const client = renderAvailable()

    expect(client.sent[0]).toMatchObject({
      method: 'transcript.read',
      params: { projectId },
    })
    expect(container.textContent).toContain('文: ハブ 未時刻')
    expect(container.textContent).toContain('認識確度 0.42')
    expect(container.textContent).toContain('認識確度 未提供')
    expect(container.textContent).toContain('低確度を判定する数値閾値は未承認')
    expect(container.textContent).toContain('単語時刻なし')
    expect(container.textContent).toContain('移動には発話の開始時刻を使用します。')
    expect(container.textContent).toContain('元の単語時刻が未提供')
    expect(container.textContent).toContain('ハブ → Hub')
    expect(container.textContent).toContain('確度 未提供')
    expect(container.querySelectorAll('.utterance-card')).toHaveLength(1)
    expect(container.querySelectorAll('.sentence-card')).toHaveLength(1)
    expect(container.querySelectorAll('.token-control')).toHaveLength(2)
    expect(container.innerHTML).not.toContain('<script')
    expect(container.textContent).not.toContain('S-1-5-')
    expect(container.textContent).not.toContain('C:\\')
  })

  it('edits by keyboard with snapshot OCC and waits for the native commit', () => {
    const client = renderAvailable()
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '認識結果を確認')
    clickButton('ハブ を編集')

    const edit = requiredElement<HTMLInputElement>('.token-editing input')
    setText(edit, 'Hub')
    pressKey(edit, 'Enter')

    expect(client.sent).toHaveLength(2)
    expect(client.sent[1]).toEqual({
      version: 1,
      requestId: client.sent[1]!.requestId,
      method: 'transcript.editToken',
      params: {
        projectId,
        revisionId,
        digest: revisionDigest,
        tokenId: timedTokenId,
        expectedText: 'ハブ',
        revisedText: 'Hub',
        reason: '認識結果を確認',
      },
    })
    expect(container.textContent).toContain('ハブ')
    expect(container.textContent).not.toContain('Hub 未時刻')
    expect(requiredElement<HTMLInputElement>('.token-editing input').disabled).toBe(true)

    emitEdited(client, 'available')

    expect(container.textContent).toContain('文: Hub 未時刻')
    expect(container.textContent).toContain('直前の文字差分')
    expect(container.textContent).toContain('失効')
    expect(container.textContent).toContain('用語候補')
    expect(container.textContent).toContain('手動再結合')
    expect(container.textContent).toContain('話者割当の結合')
    expect(container.querySelector('.token-editing')).toBeNull()
  })

  it('requires an explicit reason, supports Escape, and reports native no-op', () => {
    const client = renderAvailable()
    clickButton('ハブ を編集')
    const firstEdit = requiredElement<HTMLInputElement>('.token-editing input')
    setText(firstEdit, 'Hub')
    pressKey(firstEdit, 'Enter')
    expect(client.sent).toHaveLength(1)
    expect(container.textContent).toContain('変更理由を入力してください。')

    pressKey(firstEdit, 'Escape')
    expect(container.querySelector('.token-editing')).toBeNull()

    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '表記を確認')
    clickButton('ハブ を編集')
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    expect(client.sent[1]?.method).toBe('transcript.editToken')
    emitStatus(client, 1, 'noChange')
    expect(requiredElement<HTMLElement>('.transcript-status').dataset.transcriptStatus).toBe(
      'noChange',
    )
    expect(container.textContent).toContain('内容は変更されませんでした')
    expect(container.textContent).toContain('ハブ')
  })

  it.each(['staleBase', 'conflict'] as const)(
    'drops stale evidence and offers recovery after %s',
    (status) => {
      const client = renderAvailable()
      setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '表記を確認')
      clickButton('ハブ を編集')
      const edit = requiredElement<HTMLInputElement>('.token-editing input')
      setText(edit, 'Hub')
      pressKey(edit, 'Enter')

      emitStatus(client, 1, status)

      expect(requiredElement<HTMLElement>('.transcript-status').dataset.transcriptStatus).toBe(
        status,
      )
      expect(container.querySelector('.transcript-workspace')).toBeNull()
      const reload = buttonByText('最新版を読み込む')
      expect(reload.disabled).toBe(false)
      act(() => reload.click())
      expect(client.sent.at(-1)?.method).toBe('transcript.read')
    },
  )

  it('sets and removes a speaker with the exact speaker-revision OCC value', () => {
    const client = renderAvailable()
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '出演者を確認')
    clickButton('話者を設定')
    setText(requiredElement<HTMLInputElement>('.speaker-editor input'), '佐藤')
    setSelect(requiredElement<HTMLSelectElement>('.speaker-format-select'), 'surname')
    submit(requiredElement<HTMLFormElement>('.speaker-editor'))

    expect(client.sent[1]).toMatchObject({
      method: 'transcript.setSpeaker',
      params: {
        projectId,
        revisionId,
        digest: revisionDigest,
        expectedSpeakerRevisionId: null,
        speakerId,
        performerId,
        surname: '佐藤',
        displayFormat: 'surname',
        reason: '出演者を確認',
      },
    })

    emitSpeakerAssigned(client)
    expect(container.textContent).toContain('佐藤')
    clickButton('割当を解除')
    expect(client.sent[2]).toMatchObject({
      method: 'transcript.removeSpeaker',
      params: {
        expectedSpeakerRevisionId: '40000000-0000-4000-8000-000000000001',
        speakerId,
        reason: '出演者を確認',
      },
    })
  })

  it('adopts or rejects a term only with candidate and transcript OCC evidence', () => {
    const adoptClient = renderAvailable()
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '辞書候補を確認')
    clickButton('採用')
    expect(adoptClient.sent[1]).toMatchObject({
      method: 'transcript.adoptTerm',
      params: {
        projectId,
        revisionId,
        digest: revisionDigest,
        candidateId: '30000000-0000-4000-8000-000000000001',
        termEntryId: '10000000-0000-4000-8000-00000000000b',
        termEntryVersion: 3,
        reason: '辞書候補を確認',
      },
    })

    act(() => root.unmount())
    root = createRoot(container)
    container.replaceChildren()
    const rejectClient = renderAvailable()
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '辞書候補を確認')
    clickButton('却下')
    expect(rejectClient.sent[1]?.method).toBe('transcript.rejectTerm')
  })

  it('sends source navigation commands without touching browser media state', () => {
    const client = new FakeTranscriptClient()
    const sought: Parameters<PlayerSourceSeekHandler>[0][] = []
    const handler: PlayerSourceSeekHandler = (target) => {
      sought.push(target)
      return 'requested'
    }
    render(client, handler)
    emitRead(client)

    clickButton('未時刻 の元時刻へ移動')

    expect(sought).toEqual([
      { projectId, sourceTime: { value: '0', rate: '1' } },
    ])
    expect(container.textContent).toContain('元時刻への移動をプレイヤーへ要求しました。')
    expect(container.querySelector('video')).toBeNull()
  })

  it('cleans up its listener and client', () => {
    const client = new FakeTranscriptClient()
    render(client)
    expect(client.listenerCount).toBe(1)

    act(() => root.unmount())

    expect(client.listenerCount).toBe(0)
    expect(client.disposed).toBe(true)
    root = createRoot(container)
  })

  function renderAvailable(): FakeTranscriptClient {
    const client = new FakeTranscriptClient()
    render(client)
    emitRead(client)
    return client
  }

  function render(
    client: FakeTranscriptClient,
    sourceSeek?: PlayerSourceSeekHandler,
    props: Partial<TranscriptPanelViewProps> = {},
  ): void {
    act(() => {
      root.render(
        <PlayerNavigationProvider>
          {sourceSeek === undefined ? null : <SeekRegistration handler={sourceSeek} />}
          <TranscriptPanelView
            projectId={projectId}
            createClient={() => client}
            {...props}
          />
        </PlayerNavigationProvider>,
      )
    })
  }

  function emitRead(client: FakeTranscriptClient): void {
    const sent = client.sent[0]
    if (sent === undefined) throw new Error('Expected a transcript read request.')
    const value = createTranscriptResponseValue('transcript.read')
    value.requestId = sent.requestId
    client.emit(requireResponse(value))
  }

  function emitEdited(
    client: FakeTranscriptClient,
    status: 'available',
  ): void {
    const sent = client.sent[1]
    if (sent === undefined) throw new Error('Expected a transcript edit request.')
    const value = createTranscriptResponseValue('transcript.editToken')
    value.requestId = sent.requestId
    const snapshot = value.result.snapshot
    if (snapshot === null) throw new Error('Expected a mutable transcript snapshot.')
    snapshot.utterances[0]!.sentences[0]!.tokens[0]!.text = 'Hub'
    snapshot.utterances[0]!.sentences[0]!.text = 'Hub 未時刻'
    snapshot.utterances[0]!.text = 'Hub 未時刻'
    snapshot.termCandidates = []
    snapshot.parentRevisionId = revisionId
    snapshot.revisionId = '20000000-0000-4000-8000-000000000001'
    snapshot.revisionDigest = 'b'.repeat(64)
    snapshot.changeSource = 'human'
    snapshot.diff = [
      {
        kind: 'utterance',
        utteranceId: snapshot.utterances[0]!.id,
        sentenceId: null,
        tokenId: null,
        previousText: 'ハブ 未時刻',
        currentText: 'Hub 未時刻',
      },
      {
        kind: 'sentence',
        utteranceId: snapshot.utterances[0]!.id,
        sentenceId: snapshot.utterances[0]!.sentences[0]!.id,
        tokenId: null,
        previousText: 'ハブ 未時刻',
        currentText: 'Hub 未時刻',
      },
      {
        kind: 'token',
        utteranceId: snapshot.utterances[0]!.id,
        sentenceId: snapshot.utterances[0]!.sentences[0]!.id,
        tokenId: timedTokenId,
        previousText: 'ハブ',
        currentText: 'Hub',
      },
    ]
    snapshot.staleRange = {
      sourceRevisionId: revisionId,
      sourceRevisionDigest: revisionDigest,
      revisionId: snapshot.revisionId,
      revisionDigest: snapshot.revisionDigest,
    }
    snapshot.invalidation = {
      staleNodes: [
        'termCandidates',
        'editorialPhraseCandidates',
        'repetitionCandidates',
        'topicBoundaryCandidates',
        'topicRangeBinding',
        'contextDeviationCandidates',
        'speakerAssignmentBinding',
        'topicMetadata',
        'emphasisCandidates',
      ],
      recomputationNodes: [
        'termCandidates',
        'editorialPhraseCandidates',
        'repetitionCandidates',
        'topicBoundaryCandidates',
        'contextDeviationCandidates',
        'topicMetadata',
        'emphasisCandidates',
      ],
      manualRebindingNodes: ['topicRangeBinding', 'speakerAssignmentBinding'],
    }
    expect(status).toBe('available')
    client.emit(requireResponse(value))
  }

  function emitSpeakerAssigned(client: FakeTranscriptClient): void {
    const sent = client.sent[1]
    if (sent === undefined) throw new Error('Expected a speaker request.')
    const value = createTranscriptResponseValue('transcript.setSpeaker')
    value.requestId = sent.requestId
    const snapshot = value.result.snapshot
    if (snapshot === null) throw new Error('Expected a mutable transcript snapshot.')
    snapshot.speakerRevisionId = '40000000-0000-4000-8000-000000000001'
    snapshot.utterances[0]!.speaker = {
      speakerId,
      performerId,
      format: 'surname',
      isAssigned: true,
      isVisible: true,
      text: '佐藤',
      titleConfirmedAt: '2026-01-01T03:04:05.0000000+00:00',
      titleOrAffiliationConfirmationRequired: false,
    }
    client.emit(requireResponse(value))
  }

  function emitStatus(
    client: FakeTranscriptClient,
    sentIndex: number,
    status: 'staleBase' | 'conflict' | 'noChange',
  ): void {
    const sent = client.sent[sentIndex]
    if (sent === undefined) throw new Error('Expected a transcript request.')
    const value = createTranscriptResponseValue(sent.method)
    value.requestId = sent.requestId
    value.result = { status, snapshot: null }
    client.emit(requireResponse(value))
  }

  function requiredElement<T extends Element>(selector: string): T {
    const element = container.querySelector(selector)
    if (element === null) throw new Error(`Expected element matching ${selector}.`)
    return element as T
  }

  function buttonByText(text: string): HTMLButtonElement {
    const button = [...container.querySelectorAll('button')].find(
      (candidate) => candidate.textContent === text,
    )
    if (button === undefined) throw new Error(`Expected button ${text}.`)
    return button
  }

  function clickButton(labelOrText: string): void {
    const byLabel = container.querySelector<HTMLButtonElement>(
      `button[aria-label="${labelOrText}"]`,
    )
    const button = byLabel ?? buttonByText(labelOrText)
    act(() => button.click())
  }

  function pressKey(element: HTMLElement, key: string): void {
    act(() => {
      element.dispatchEvent(
        new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }),
      )
    })
  }

  function setText(element: HTMLInputElement, value: string): void {
    act(() => {
      setNativeValue(element, value)
      element.dispatchEvent(new Event('input', { bubbles: true }))
    })
  }

  function setSelect(element: HTMLSelectElement, value: string): void {
    act(() => {
      setNativeValue(element, value)
      element.dispatchEvent(new Event('change', { bubbles: true }))
    })
  }

  function submit(form: HTMLFormElement): void {
    act(() => {
      form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    })
  }
})

function SeekRegistration({ handler }: Readonly<{ handler: PlayerSourceSeekHandler }>) {
  usePlayerSourceSeekRegistration(handler)
  return null
}

class FakeTranscriptClient implements TranscriptBridgeClient {
  readonly sent: TranscriptRequest[] = []
  disposed = false
  private readonly listeners = new Set<(response: TranscriptResponse) => void>()

  get listenerCount(): number {
    return this.listeners.size
  }

  sendTranscript(request: TranscriptRequest): void {
    this.sent.push(request)
  }

  onTranscriptResponse(listener: (response: TranscriptResponse) => void): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  emit(response: TranscriptResponse): void {
    act(() => {
      for (const listener of this.listeners) listener(response)
    })
  }

  dispose(): void {
    this.disposed = true
    this.listeners.clear()
  }
}

function requireResponse(value: unknown): TranscriptResponse {
  const parsed = parseTranscriptResponse(value)
  if (parsed === null) throw new Error('Expected a valid transcript response fixture.')
  return parsed
}

function setNativeValue(element: HTMLInputElement | HTMLSelectElement, value: string): void {
  const prototype =
    element instanceof HTMLInputElement
      ? HTMLInputElement.prototype
      : HTMLSelectElement.prototype
  const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set
  if (setter === undefined) throw new Error('Expected a native value setter.')
  setter.call(element, value)
}

void transcriptRequestId
void untimedTokenId
