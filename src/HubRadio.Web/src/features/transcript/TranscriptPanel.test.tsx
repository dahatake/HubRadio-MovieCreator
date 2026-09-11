// @vitest-environment jsdom

import { act, StrictMode, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  PlayerNavigationProvider,
  usePlayerSourceSeekRegistration,
  type PlayerSourceSeekHandler,
} from '../player'
import { ProjectStateProvider } from '../projects/ProjectStateProvider'
import {
  parseTranscriptResponse,
  type TranscriptRequest,
  type TranscriptResponse,
} from './transcriptProtocol'
import {
  TranscriptPanel,
  TranscriptPanelView,
  type TranscriptPanelProps,
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
    vi.useFakeTimers({ toFake: ['queueMicrotask'] })
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT =
      true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    vi.restoreAllMocks()
    vi.useRealTimers()
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

  it.each(['staleBase', 'conflict', 'invalid'] as const)(
    'keeps a copyable draft and permits an explicit same-base refresh and retry after %s',
    (status) => {
      const client = new FakeTranscriptClient()
      const dirtyChanges: boolean[] = []
      render(client, undefined, { context: { projectId }, onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
      emitRead(client)
      setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '表記を確認')
      clickButton('ハブ を編集')
      const edit = requiredElement<HTMLInputElement>('.token-editing input')
      setText(edit, 'Hub')
      pressKey(edit, 'Enter')

      emitStatus(client, 1, status)

      expect(requiredElement<HTMLElement>('.transcript-status').dataset.transcriptStatus).toBe(
        status,
      )
      expect(requiredElement<HTMLElement>('.transcript-workspace').dataset.transcriptStale).toBe('true')
      expect(container.textContent).toContain('前回取得した文字起こしは未確認です。')
      expect(container.textContent).not.toContain('現在の文字起こし')
      expect(requiredElement<HTMLInputElement>('.token-editing input')).toBe(edit)
      expect(edit.value).toBe('Hub')
      expect(edit.disabled).toBe(false)
      expect(edit.readOnly).toBe(true)
      edit.focus()
      edit.select()
      expect(edit.selectionStart).toBe(0)
      expect(edit.selectionEnd).toBe(edit.value.length)
      expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('表記を確認')
      for (const label of ['保存', '話者を設定', '割当を解除', '採用', '却下']) {
        expect(buttonByText(label).disabled).toBe(true)
        clickButton(label)
      }
      pressKey(edit, 'Enter')
      const reload = buttonByText('最新版を読み込む')
      expect(reload.disabled).toBe(false)
      act(() => reload.click())
      expect(client.sent.map((request) => request.method)).toEqual([
        'transcript.read', 'transcript.editToken', 'transcript.read',
      ])
      expect(buttonByText('未保存の入力を取り消す').disabled).toBe(true)
      // A late acknowledgement of the failed mutation cannot consume the refresh or draft.
      emitStatus(client, 1, 'noChange')
      expect(requiredElement<HTMLElement>('.transcript-status').dataset.transcriptStatus).toBe('loading')
      emitRead(client, 2)
      expect(requiredElement<HTMLInputElement>('.token-editing input')).toBe(edit)
      expect(edit.value).toBe('Hub')
      expect(edit.readOnly).toBe(false)
      expect(dirtyChanges).toEqual([false, true])
      expect(buttonByText('保存').disabled).toBe(false)
      pressKey(edit, 'Enter')
      expect(client.sent[3]).toMatchObject({
        method: 'transcript.editToken',
        params: { projectId, revisionId, digest: revisionDigest, tokenId: timedTokenId, expectedText: 'ハブ', revisedText: 'Hub', reason: '表記を確認' },
      })
      emitStatus(client, 3, 'noChange')
      expect(container.querySelector('.token-editing')).toBeNull()
      expect(dirtyChanges).toEqual([false, true, false])
    },
  )

  it.each(['revision', 'token', 'source span', 'text', 'creation time'] as const)(
    'requires explicit copy/cancel/reapply when refresh changes the draft %s',
    (change) => {
      const client = new FakeTranscriptClient()
      const dirtyChanges: boolean[] = []
      render(client, undefined, { context: { projectId }, onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
      emitRead(client)
      setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '再取得では保存しない理由')
      clickButton('ハブ を編集')
      setText(requiredElement<HTMLInputElement>('.token-editing input'), '控えて貼り直す本文')
      pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
      emitStatus(client, 1, 'conflict')
      clickButton('最新版を読み込む')
      const value = createTranscriptResponseValue()
      value.requestId = client.sent[2]!.requestId
      const snapshot = value.result.snapshot!
      const utterance = snapshot.utterances[0]!
      const token = utterance.sentences[0]!.tokens[0]!
      snapshot.termCandidates = []
      if (change === 'revision') {
        snapshot.revisionId = '20000000-0000-4000-8000-000000000001'
        snapshot.revisionDigest = 'b'.repeat(64)
      } else if (change === 'token') {
        token.id = '80000000-0000-4000-8000-000000000001'
      } else if (change === 'source span') {
        utterance.sourceSpanId = '80000000-0000-4000-8000-000000000002'
      } else if (change === 'text') {
        token.text = '更新された語'
        utterance.text = utterance.sentences[0]!.text = '更新された語 未時刻'
      } else {
        snapshot.createdAt = '2026-01-03T03:04:05.0000000+00:00'
      }
      client.emit(requireResponse(value))

      const edit = requiredElement<HTMLInputElement>('.token-editing input')
      expect(edit.value).toBe('控えて貼り直す本文')
      expect(edit.disabled).toBe(false)
      expect(edit.readOnly).toBe(true)
      expect(buttonByText('保存').disabled).toBe(true)
      expect(container.textContent).toContain('文字修正の基準が更新されました。')
      pressKey(edit, 'Enter')
      expect(client.sent).toHaveLength(3)
      clickButton('最新版を読み込む')
      value.requestId = client.sent[3]!.requestId
      client.emit(requireResponse(value))
      expect(requiredElement<HTMLInputElement>('.token-editing input').readOnly).toBe(true)
      const copied = requiredElement<HTMLInputElement>('.token-editing input').value
      clickButton('取消')
      expect(container.querySelector('.token-editing')).toBeNull()
      expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('再取得では保存しない理由')
      expect(dirtyChanges).toEqual([false, true])
      clickButton(`${token.text} を編集`)
      const reapplied = requiredElement<HTMLInputElement>('.token-editing input')
      expect(reapplied.value).toBe(token.text)
      expect(reapplied.readOnly).toBe(false)
      setText(reapplied, copied)
      pressKey(reapplied, 'Enter')
      expect(client.sent[4]).toMatchObject({
        method: 'transcript.editToken',
        params: {
          projectId, revisionId: snapshot.revisionId, digest: snapshot.revisionDigest,
          tokenId: token.id, expectedText: token.text, revisedText: copied, reason: '再取得では保存しない理由',
        },
      })
    },
  )

  it.each(['forbidden', 'unavailable'] as const)('shows only local draft fields while evidence is %s', (status) => {
    const client = renderAvailable()
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '自分で入力した理由')
    clickButton('話者を設定')
    setText(requiredElement<HTMLInputElement>('.speaker-editor input'), '自分で入力した姓')
    clickButton('ハブ を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), '自分で入力した本文')
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    emitStatus(client, 1, status)

    expect(container.querySelector('.transcript-workspace')).toBeNull()
    expect(container.querySelector('.utterance-card')).toBeNull()
    expect(container.textContent).not.toContain('文: ハブ 未時刻')
    expect(container.textContent).not.toContain('Speaker 1')
    expect(container.textContent).not.toContain('佐藤 花子')
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('自分で入力した理由')
    expect(requiredElement<HTMLInputElement>('.speaker-editor input').value).toBe('自分で入力した姓')
    const draft = requiredElement<HTMLInputElement>('.token-editing input')
    expect(draft.value).toBe('自分で入力した本文')
    expect(draft.disabled).toBe(false)
    expect(draft.readOnly).toBe(true)
    expect(buttonByText('保存').disabled).toBe(true)
    expect(buttonByText('設定').disabled).toBe(true)
    expect(buttonByText('最新版を読み込む').disabled).toBe(false)
    pressKey(draft, 'Enter')
    submit(requiredElement<HTMLFormElement>('.speaker-editor'))
    expect(client.sent).toHaveLength(2)
    clickButton('最新版を読み込む')
    emitRead(client, 2)
    expect(requiredElement<HTMLInputElement>('.token-editing input').readOnly).toBe(true)
    expect(requiredElement<HTMLInputElement>('.speaker-editor input').value).toBe('自分で入力した姓')
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('自分で入力した理由')
  })

  it('does not unlock mutations when a refresh returns noChange without a fresh snapshot', () => {
    const client = renderAvailable()
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '再取得を確認')
    clickButton('ハブ を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), '保持する本文')
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    emitStatus(client, 1, 'conflict')
    clickButton('最新版を読み込む')
    emitStatus(client, 2, 'noChange')

    expect(requiredElement<HTMLElement>('.transcript-status').dataset.transcriptStatus).toBe('invalid')
    expect(requiredElement<HTMLInputElement>('.token-editing input').readOnly).toBe(true)
    expect(buttonByText('保存').disabled).toBe(true)
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    expect(client.sent).toHaveLength(3)
    clickButton('最新版を読み込む')
    emitRead(client, 3)
    expect(requiredElement<HTMLInputElement>('.token-editing input').value).toBe('保持する本文')
    expect(buttonByText('保存').disabled).toBe(false)
  })

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

    expect(requiredElement<HTMLInputElement>('.speaker-editor input').value).toBe('佐藤')
    expect(requiredElement<HTMLInputElement>('.speaker-editor input').disabled).toBe(true)
    expect(requiredElement<HTMLSelectElement>('.speaker-format-select').value).toBe('surname')
    emitSpeakerAssigned(client)
    expect(container.querySelector('.speaker-editor')).toBeNull()
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

  it('uses only the explicit project context and keeps revision IDs out of workflow output', () => {
    const client = new FakeTranscriptClient()
    render(client, undefined, {
      projectId: otherProjectId,
      context: { projectId },
    })
    emitRead(client)

    expect(client.sent).toHaveLength(1)
    expect(client.sent[0]).toMatchObject({ method: 'transcript.read', params: { projectId } })
    expect(Object.keys(client.sent[0]!.params)).toEqual(['projectId'])
    expect(container.textContent).toContain('現在の文字起こし')
    expect(container.textContent).not.toContain(projectId)
    expect(container.textContent).not.toContain(revisionId)
    expect(container.textContent).not.toContain(revisionDigest)
    expect(container.querySelector('input[name="projectId"]')).toBeNull()

    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '表記を確認')
    clickButton('ハブ を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), 'Hub')
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    emitEdited(client, 'available')

    expect(container.textContent).toContain('文: Hub 未時刻')
    expect(container.textContent).toContain('話者割当の結合')
    expect(container.querySelector('.transcript-stale-evidence')).toBeNull()
    expect(container.textContent).not.toMatch(/[0-9a-f]{8}-[0-9a-f-]{27}/u)
    expect(container.textContent).not.toContain(revisionDigest)
    expect(client.sent.map((request) => request.method)).toEqual([
      'transcript.read', 'transcript.editToken',
    ])
  })

  it.each([
    { name: 'null context', context: null },
    { name: 'missing ID', context: {} },
    { name: 'null ID', context: { projectId: null } },
    { name: 'undefined ID', context: { projectId: undefined } },
    { name: 'empty ID', context: { projectId: '' } },
    { name: 'whitespace ID', context: { projectId: ' ' } },
    { name: 'malformed ID', context: { projectId: 'not-a-project' } },
    { name: 'zero ID', context: { projectId: '00000000-0000-0000-0000-000000000000' } },
    { name: 'uppercase ID', context: { projectId: 'ABCDEFAB-CDEF-ABCD-EFAB-CDEFABCDEFAB' } },
    { name: 'untrimmed ID', context: { projectId: `${projectId}\n` } },
  ])('does not fill or read a fallback project for $name', ({ context }) => {
    const client = new FakeTranscriptClient()
    let creations = 0
    renderPanel(client, {
      projectId,
      context,
      createClient: () => {
        creations += 1
        return client
      },
    })

    expect(creations).toBe(0)
    expect(client.sent).toHaveLength(0)
    expect(client.listenerCount).toBe(0)
    expect(container.querySelector('.transcript-workspace')).toBeNull()
    expect(container.querySelector('input')).toBeNull()
    expect(container.textContent).toContain('確定済み案件を案件一覧から開いてください。')
    expect(container.textContent).not.toContain(projectId)
    expect(buttonByText('最新版を読み込む').disabled).toBe(true)
  })

  it('preserves the standalone projectId API and legacy revision evidence when context is undefined', () => {
    const client = new FakeTranscriptClient()
    renderPanel(client, { projectId, context: undefined })
    emitRead(client)

    expect(client.sent).toHaveLength(1)
    expect(client.sent[0]?.params).toEqual({ projectId })
    expect(container.textContent).toContain(`版 ${revisionId}`)
    expect(requiredElement<HTMLElement>('.transcript-panel').hidden).toBe(false)
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').disabled).toBe(false)
  })

  it('defers the initial read until active and does not reread on visibility or factory identity changes', () => {
    const client = new FakeTranscriptClient()
    render(client, undefined, { context: { projectId }, active: false })
    expect(client.sent).toHaveLength(0)
    expect(requiredElement<HTMLElement>('.transcript-panel').hidden).toBe(true)

    render(client, undefined, { context: { projectId }, active: true })
    expect(client.sent).toHaveLength(1)
    emitRead(client)

    // render supplies a fresh context object and createClient function each time.
    render(client, undefined, { context: { projectId }, active: false })
    render(client, undefined, { context: { projectId }, active: true })
    render(client, undefined, { context: { projectId }, active: true })

    expect(client.sent).toHaveLength(1)
    expect(client.disposed).toBe(false)
    expect(client.listenerCount).toBe(1)
    expect(container.textContent).toContain('文: ハブ 未時刻')
  })

  it('loads newly registered transcripts once after an unavailable read, defers hidden refreshes and never retries the same key', () => {
    const client = new FakeTranscriptClient()
    render(client, undefined, { context: { projectId, refreshKey: 'before-analysis' } })
    emitStatus(client, 0, 'unavailable')
    for (const active of [true, false, true]) render(client, undefined, { context: { projectId, refreshKey: 'before-analysis' }, active })
    expect(client.sent).toHaveLength(1)
    render(client, undefined, { context: { projectId, refreshKey: 'analysis-ready' }, active: false })
    expect(client.sent).toHaveLength(1)
    render(client, undefined, { context: { projectId, refreshKey: 'analysis-ready' } })
    expect(client.sent).toHaveLength(2)
    expect(client.sent[1]?.params).toEqual({ projectId })
    emitRead(client, 1)
    expect(container.textContent).toContain('文: ハブ 未時刻')
    for (const active of [true, false, true]) render(client, undefined, { context: { projectId, refreshKey: 'analysis-ready' }, active })
    expect(client.sent).toHaveLength(2)
    render(client, undefined, { context: { projectId, refreshKey: 'completed' } })
    emitStatus(client, 2, 'unavailable')
    for (const active of [false, true, true]) render(client, undefined, { context: { projectId, refreshKey: 'completed' }, active })
    expect(client.sent).toHaveLength(3)
    expect(client.listenerCount).toBe(1)
    expect(client.disposed).toBe(false)
  })

  it('retains dirty transcript DOM and selection on registration updates and reads only the latest key after discard', () => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean) => { dirtyChanges.push(dirty) }
    render(client, undefined, { context: { projectId, refreshKey: 'old' }, onDirtyChange })
    emitRead(client)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '保持する理由')
    clickButton('ハブ を編集')
    const edit = requiredElement<HTMLInputElement>('.token-editing input')
    setText(edit, '保持する本文'); edit.setSelectionRange(1, 4)
    for (const [refreshKey, active] of [['ready', false], ['ready', true], ['completed', true]] as const) {
      render(client, undefined, { context: { projectId, refreshKey }, active, onDirtyChange })
      expect(client.sent).toHaveLength(1)
      expect(requiredElement('.token-editing input')).toBe(edit)
      expect(edit.value).toBe('保持する本文')
      expect(edit.selectionStart).toBe(1); expect(edit.selectionEnd).toBe(4)
      expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('保持する理由')
      expect(requiredElement('.transcript-registration-notice').textContent).toContain('未保存の入力を保持')
    }
    clickButton('未保存の入力を取り消す')
    act(() => vi.runAllTicks())
    expect(client.sent).toHaveLength(2)
    emitRead(client, 1)
    expect(container.querySelector('.transcript-registration-notice')).toBeNull()
    expect(dirtyChanges).toEqual([false, true, false])
    expect(client.listenerCount).toBe(1)
  })

  it('coalesces refresh keys received during an outstanding transcript read', () => {
    const client = new FakeTranscriptClient()
    render(client, undefined, { context: { projectId, refreshKey: 'before' } })
    render(client, undefined, { context: { projectId, refreshKey: 'ready' } })
    render(client, undefined, { context: { projectId, refreshKey: 'latest' } })
    expect(client.sent).toHaveLength(1)
    emitStatus(client, 0, 'unavailable')
    act(() => vi.runAllTicks())
    expect(client.sent).toHaveLength(2)
    emitRead(client, 1)
    render(client, undefined, { context: { projectId, refreshKey: 'latest' } })
    expect(client.sent).toHaveLength(2)
  })

  it('retains unsaved text, selected word, exact source time and revision across hide/unhide without stealing focus', () => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
    const sought: Parameters<PlayerSourceSeekHandler>[0][] = []
    const sourceSeek: PlayerSourceSeekHandler = (target) => {
      sought.push(target)
      return 'requested'
    }
    const props = { context: { projectId }, onDirtyChange }
    render(client, sourceSeek, props)
    const value = createTranscriptResponseValue()
    value.requestId = client.sent[0]!.requestId
    const token = value.result.snapshot!.utterances[0]!.sentences[0]!.tokens[0]!
    token.range = {
      start: { value: '1', rate: '3' },
      duration: { value: '1', rate: '3' },
    }
    token.effectiveSeekSourceTime = token.range.start
    client.emit(requireResponse(value))
    clickButton('ハブ の元時刻へ移動')
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '未保存の変更理由')
    clickButton('ハブ を編集')
    const edit = requiredElement<HTMLInputElement>('.token-editing input')
    setText(edit, '未保存の本文')
    edit.setSelectionRange(1, 4)
    const panel = requiredElement<HTMLElement>('.transcript-panel')
    panel.scrollTop = 37
    expect(document.activeElement).toBe(edit)
    expect(buttonByText('最新版を読み込む').disabled).toBe(false)
    const blur = vi.spyOn(edit, 'blur')

    render(client, sourceSeek, { ...props, active: false })

    expect(requiredElement<HTMLElement>('.transcript-panel')).toBe(panel)
    expect(panel.hidden).toBe(true)
    expect(panel.hasAttribute('inert')).toBe(true)
    expect(requiredElement<HTMLInputElement>('.token-editing input')).toBe(edit)
    expect(edit.disabled).toBe(true)
    // jsdom does not blur disabled inputs; verify the request and external focus separately.
    expect(blur).toHaveBeenCalledOnce()
    pressKey(edit, 'Escape')
    pressKey(edit, 'Enter')
    const outside = requiredElement<HTMLButtonElement>('.outside-transcript')
    outside.focus()
    // A late duplicate read must not replace the draft or focus the hidden editor.
    client.emit(requireResponse(value))
    render(client, sourceSeek, { ...props, active: false })
    render(client, sourceSeek, { ...props, active: true })

    expect(panel.hidden).toBe(false)
    expect(panel.hasAttribute('inert')).toBe(false)
    expect(panel.scrollTop).toBe(37)
    expect(panel.style.overflow).toBe('auto')
    expect(requiredElement<HTMLInputElement>('.token-editing input')).toBe(edit)
    expect(edit.value).toBe('未保存の本文')
    expect(edit.selectionStart).toBe(1)
    expect(edit.selectionEnd).toBe(4)
    expect(edit.disabled).toBe(false)
    expect(document.activeElement).toBe(outside)
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('未保存の変更理由')
    expect(container.textContent).toContain('元時刻への移動をプレイヤーへ要求しました。')
    expect(sought).toEqual([{ projectId, sourceTime: { value: '1', rate: '3' } }])
    expect(dirtyChanges).toEqual([false, true])
    expect(client.sent).toHaveLength(1)
    expect(client.disposed).toBe(false)

    pressKey(edit, 'Enter')
    expect(client.sent[1]).toMatchObject({
      method: 'transcript.editToken',
      params: { projectId, revisionId, digest: revisionDigest, tokenId: timedTokenId, revisedText: '未保存の本文' },
    })
  })

  it('retains the speaker fields and reports reason-only and speaker drafts to the shell', () => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
    const props = { context: { projectId }, onDirtyChange }
    render(client, undefined, props)
    emitRead(client)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '確認中')
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '')
    expect(dirtyChanges).toEqual([false, true, false])

    clickButton('話者を設定')
    const surname = requiredElement<HTMLInputElement>('.speaker-editor input')
    setText(surname, '未保存の姓')
    setSelect(requiredElement<HTMLSelectElement>('.speaker-format-select'), 'surname')
    render(client, undefined, { ...props, active: false })
    render(client, undefined, { ...props, active: true })

    expect(requiredElement<HTMLInputElement>('.speaker-editor input')).toBe(surname)
    expect(surname.value).toBe('未保存の姓')
    expect(requiredElement<HTMLSelectElement>('.speaker-format-select').value).toBe('surname')
    expect(dirtyChanges.at(-1)).toBe(true)
    expect(client.sent).toHaveLength(1)
    clickButton('取消')
    expect(dirtyChanges.at(-1)).toBe(false)

    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '終了時も未保存')
    expect(dirtyChanges.at(-1)).toBe(true)
    act(() => root.unmount())
    expect(dirtyChanges.at(-1)).toBe(false)
    root = createRoot(container)
  })

  it('guards the public panel against project changes without remounting or overwriting a dirty draft', () => {
    const first = new FakeTranscriptClient()
    const second = new FakeTranscriptClient()
    renderPanel(first, { context: { projectId } })
    emitRead(first)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '元の案件の理由')
    clickButton('ハブ を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), '元の案件の未保存本文')

    renderPanel(second, { context: { projectId: otherProjectId } })

    expect(second.sent).toHaveLength(0)
    expect(first.disposed).toBe(false)
    expect(first.listenerCount).toBe(1)
    expect(container.querySelector('.transcript-workspace')).toBeNull()
    expect(container.textContent).not.toContain('ハブ')
    expect(container.textContent).not.toContain('元の案件の理由')
    expect(container.textContent).toContain('未保存の入力または保存中の変更を保持しています。')
    expect(buttonByText('最新版を読み込む').disabled).toBe(true)

    // No shell callback is required for the panel's fail-safe draft retention.
    renderPanel(first, { context: { projectId }, active: false })
    const edit = requiredElement<HTMLInputElement>('.token-editing input')
    expect(edit.value).toBe('元の案件の未保存本文')
    expect(document.activeElement).not.toBe(edit)
    renderPanel(first, { context: { projectId }, active: true })
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('元の案件の理由')
    expect(first.sent).toHaveLength(1)
    pressKey(edit, 'Escape')
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '')

    renderPanel(second, { context: { projectId: otherProjectId } })
    expect(first.disposed).toBe(true)
    expect(second.sent).toHaveLength(1)
    expect(second.sent[0]?.params).toEqual({ projectId: otherProjectId })
    emitRead(second, 0, otherProjectId)
    expect(container.querySelector('.token-editing')).toBeNull()
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('')
  })

  it('does not abandon or overwrite a pending mutation when hidden or given another project', () => {
    const first = new FakeTranscriptClient()
    const second = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
    render(first, undefined, { context: { projectId }, onDirtyChange })
    emitRead(first)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '保存を待つ')
    clickButton('ハブ を編集')
    const edit = requiredElement<HTMLInputElement>('.token-editing input')
    setText(edit, 'Hub')
    pressKey(edit, 'Enter')
    const mutation = first.sent[1]
    expect(buttonByText('未保存の入力を取り消す').disabled).toBe(true)

    render(second, undefined, { context: { projectId: otherProjectId }, active: false, onDirtyChange })
    emitRead(first)
    expect(first.disposed).toBe(false)
    expect(first.sent).toHaveLength(2)
    expect(first.sent[1]).toBe(mutation)
    expect(second.sent).toHaveLength(0)
    expect(dirtyChanges.at(-1)).toBe(true)
    expect(container.querySelector('.transcript-workspace')).toBeNull()

    emitEdited(first, 'available')
    expect(first.disposed).toBe(true)
    expect(second.sent).toHaveLength(0)
    expect(dirtyChanges.at(-1)).toBe(false)
    expect(container.textContent).not.toContain('文: Hub 未時刻')

    render(second, undefined, { context: { projectId: otherProjectId }, active: true, onDirtyChange })
    expect(second.sent).toHaveLength(1)
    expect(second.sent[0]?.params).toEqual({ projectId: otherProjectId })
    emitRead(second, 0, otherProjectId)
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('')
  })

  it('clears the dirty signal only after a confirmed mutation without clearing the reusable reason', () => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
    render(client, undefined, { context: { projectId }, onDirtyChange })
    emitRead(client)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '表記を確認')
    clickButton('ハブ を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), 'Hub')
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    expect(dirtyChanges).toEqual([false, true])

    emitEdited(client, 'available')
    expect(dirtyChanges).toEqual([false, true, false])
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('表記を確認')
    expect(buttonByText('最新版を読み込む').disabled).toBe(false)
    expect(client.sent).toHaveLength(2)
  })

  it('handles synchronous acknowledgements without marking a reason-only refresh as saved', () => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    render(client, undefined, { onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
    emitRead(client)
    vi.spyOn(client, 'sendTranscript').mockImplementation((request) => {
      client.sent.push(request)
      const value = createTranscriptResponseValue(request.method)
      value.requestId = request.requestId
      if (request.method !== 'transcript.read') value.result = { status: 'noChange', snapshot: null }
      client.emit(requireResponse(value))
    })
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '確定した理由')
    clickButton('ハブ を編集')
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    expect(container.querySelector('.token-editing')).toBeNull()
    expect(dirtyChanges).toEqual([false, true, false])

    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '読み込みだけの未保存理由')
    clickButton('最新版を読み込む')

    expect(dirtyChanges).toEqual([false, true, false, true])
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('読み込みだけの未保存理由')
    expect(client.sent.map((request) => request.method)).toEqual([
      'transcript.read', 'transcript.editToken', 'transcript.read',
    ])
    clickButton('未保存の入力を取り消す')
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('確定した理由')
    expect(dirtyChanges).toEqual([false, true, false, true, false])
  })

  it('notifies an inline parent callback only when dirty changes and uses its latest closure', () => {
    const client = new FakeTranscriptClient()
    const notifications: string[] = []
    function InlineParent({ label }: Readonly<{ label: string }>) {
      const [dirtyPanels, setDirtyPanels] = useState({ transcript: false, settings: true })
      return (
        <>
          <output data-dirty-panels>{JSON.stringify(dirtyPanels)}</output>
          <TranscriptPanelView
            projectId={projectId}
            context={{ projectId }}
            createClient={() => client}
            onDirtyChange={(dirty) => {
              notifications.push(`${label}:${dirty}`)
              if (notifications.length > 10) throw new Error('Dirty notification render loop.')
              setDirtyPanels((current) => ({ ...current, transcript: dirty }))
            }}
          />
        </>
      )
    }

    act(() => root.render(<InlineParent label="first" />))
    act(() => vi.runAllTicks())
    emitRead(client)
    expect(notifications).toEqual(['first:false'])
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '確認中')
    expect(notifications).toEqual(['first:false', 'first:true'])
    expect(requiredElement<HTMLOutputElement>('[data-dirty-panels]').value).toBe(
      '{"transcript":true,"settings":true}',
    )

    act(() => root.render(<InlineParent label="latest" />))
    expect(notifications).toEqual(['first:false', 'first:true'])
    clickButton('未保存の入力を取り消す')
    expect(notifications).toEqual(['first:false', 'first:true', 'latest:false'])
    expect(requiredElement<HTMLOutputElement>('[data-dirty-panels]').value).toBe(
      '{"transcript":false,"settings":true}',
    )
    expect(client.sent).toHaveLength(1)
  })

  it.each(['conflict', 'staleBase', 'forbidden', 'unavailable', 'invalid'] as const)(
    'retains the original target after a second edit with the saved reason fails with %s',
    (status) => {
      const first = new FakeTranscriptClient()
      const second = new FakeTranscriptClient()
      const dirtyChanges: boolean[] = []
      const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
      const props = { context: { projectId }, onDirtyChange }
      render(first, undefined, props)
      emitRead(first)
      setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '同じ変更理由')
      clickButton('ハブ を編集')
      setText(requiredElement<HTMLInputElement>('.token-editing input'), 'Hub')
      pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
      emitEdited(first, 'available')
      expect(dirtyChanges).toEqual([false, true, false])

      clickButton('Hub を編集')
      setText(requiredElement<HTMLInputElement>('.token-editing input'), '保持する改訂本文')
      pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
      expect(first.sent[2]).toMatchObject({
        method: 'transcript.editToken',
        params: {
          projectId,
          revisionId: '20000000-0000-4000-8000-000000000001',
          digest: 'b'.repeat(64),
          tokenId: timedTokenId,
          expectedText: 'Hub',
          revisedText: '保持する改訂本文',
          reason: '同じ変更理由',
        },
      })
      render(second, undefined, { ...props, context: { projectId: otherProjectId }, active: false })
      emitStatus(first, 2, status)
      expect(dirtyChanges).toEqual([false, true, false, true])
      expect(first.disposed).toBe(false)
      expect(second.sent).toHaveLength(0)

      render(first, undefined, { ...props, active: false })
      render(first, undefined, props)
      expect(requiredElement<HTMLElement>('.transcript-status').dataset.transcriptStatus).toBe(status)
      expect(buttonByText('最新版を読み込む').disabled).toBe(false)
      expect(buttonByText('未保存の入力を取り消す').disabled).toBe(false)
      expect(first.sent).toHaveLength(3)
      render(second, undefined, { ...props, context: { projectId: otherProjectId } })
      expect(container.textContent).toContain('未保存の入力または保存中の変更を保持しています。')
      expect(container.querySelector('.transcript-workspace')).toBeNull()

      clickButton('未保存の入力を取り消す')
      act(() => vi.runAllTicks())
      expect(dirtyChanges).toEqual([false, true, false, true, false])
      expect(first.disposed).toBe(true)
      expect(first.sent).toHaveLength(3)
      expect(second.sent).toHaveLength(1)
      expect(second.sent[0]?.params).toEqual({ projectId: otherProjectId })
      emitRead(second, 0, otherProjectId)
      expect(container.querySelector('.token-editing')).toBeNull()
      expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('')
    },
  )

  it.each(['available', 'noChange'] as const)('retains speaker inputs until the native %s acknowledgement', (status) => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
    const props = { context: { projectId }, onDirtyChange }
    render(client, undefined, props)
    emitRead(client)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '出演者を確認')
    clickButton('話者を設定')
    const surname = requiredElement<HTMLInputElement>('.speaker-editor input')
    setText(surname, '佐藤')
    setSelect(requiredElement<HTMLSelectElement>('.speaker-format-select'), 'surname')
    submit(requiredElement<HTMLFormElement>('.speaker-editor'))

    expect(requiredElement<HTMLInputElement>('.speaker-editor input')).toBe(surname)
    expect(surname.disabled).toBe(true)
    render(client, undefined, { ...props, active: false })
    render(client, undefined, props)
    expect(requiredElement<HTMLInputElement>('.speaker-editor input')).toBe(surname)
    expect(surname.value).toBe('佐藤')
    expect(requiredElement<HTMLSelectElement>('.speaker-editor select').value).toBe(performerId)
    expect(requiredElement<HTMLSelectElement>('.speaker-format-select').value).toBe('surname')
    expect(dirtyChanges).toEqual([false, true])
    expect(buttonByText('未保存の入力を取り消す').disabled).toBe(true)
    if (status === 'available') emitSpeakerAssigned(client)
    else emitStatus(client, 1, status)
    expect(container.querySelector('.speaker-editor')).toBeNull()
    expect(dirtyChanges).toEqual([false, true, false])
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('出演者を確認')
    expect(client.sent).toHaveLength(2)
  })

  it('retains a failed speaker mutation with a reused reason until explicit cancellation', () => {
    const first = new FakeTranscriptClient()
    const second = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
    const props = { context: { projectId }, onDirtyChange }
    render(first, undefined, props)
    emitRead(first)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '出演者を確認')
    clickButton('話者を設定')
    setText(requiredElement<HTMLInputElement>('.speaker-editor input'), '佐藤')
    submit(requiredElement<HTMLFormElement>('.speaker-editor'))
    emitSpeakerAssigned(first)
    expect(dirtyChanges).toEqual([false, true, false])

    clickButton('話者を設定')
    setText(requiredElement<HTMLInputElement>('.speaker-editor input'), '保持する姓')
    setSelect(requiredElement<HTMLSelectElement>('.speaker-format-select'), 'surname')
    submit(requiredElement<HTMLFormElement>('.speaker-editor'))
    expect(requiredElement<HTMLInputElement>('.speaker-editor input').value).toBe('保持する姓')
    render(second, undefined, { ...props, context: { projectId: otherProjectId } })
    emitStatus(first, 2, 'conflict')
    expect(dirtyChanges).toEqual([false, true, false, true])
    expect(first.disposed).toBe(false)
    expect(second.sent).toHaveLength(0)
    expect(buttonByText('未保存の入力を取り消す').disabled).toBe(false)

    clickButton('未保存の入力を取り消す')
    act(() => vi.runAllTicks())
    expect(dirtyChanges).toEqual([false, true, false, true, false])
    expect(first.disposed).toBe(true)
    expect(first.sent).toHaveLength(3)
    expect(second.sent).toHaveLength(1)
    emitRead(second, 0, otherProjectId)
    expect(container.querySelector('.speaker-editor')).toBeNull()
  })

  it('retains a failed mutation marker without a local edit when the reason is already saved', () => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
    render(client, undefined, { context: { projectId }, onDirtyChange })
    emitRead(client)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '出演者を確認')
    clickButton('話者を設定')
    setText(requiredElement<HTMLInputElement>('.speaker-editor input'), '佐藤')
    submit(requiredElement<HTMLFormElement>('.speaker-editor'))
    emitSpeakerAssigned(client)
    clickButton('割当を解除')
    emitStatus(client, 2, 'conflict')

    expect(dirtyChanges).toEqual([false, true, false, true])
    expect(buttonByText('最新版を読み込む').disabled).toBe(false)
    expect(buttonByText('未保存の入力を取り消す').disabled).toBe(false)
    clickButton('未保存の入力を取り消す')
    expect(dirtyChanges).toEqual([false, true, false, true, false])
    expect(client.sent).toHaveLength(3)
    expect(buttonByText('最新版を読み込む').disabled).toBe(false)
  })

  it.each(['text', 'speaker'] as const)('recovers a failed %s send without discarding or automatically retrying the draft', (kind) => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
    const props = { context: { projectId }, onDirtyChange }
    render(client, undefined, props)
    emitRead(client)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '同じ変更理由')
    clickButton('ハブ を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), 'Hub')
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    emitEdited(client, 'available')

    client.failNextSend = true
    if (kind === 'text') {
      clickButton('Hub を編集')
      setText(requiredElement<HTMLInputElement>('.token-editing input'), '送信できなかった本文')
      pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    } else {
      clickButton('話者を設定')
      setText(requiredElement<HTMLInputElement>('.speaker-editor input'), '送信できなかった姓')
      submit(requiredElement<HTMLFormElement>('.speaker-editor'))
    }
    expect(requiredElement<HTMLElement>('.transcript-status').dataset.transcriptStatus).toBe('sendFailed')
    expect(dirtyChanges).toEqual([false, true, false, true])
    expect(buttonByText('最新版を読み込む').disabled).toBe(false)
    render(client, undefined, { ...props, active: false })
    render(client, undefined, props)
    expect(client.sent).toHaveLength(2)
    expect(buttonByText('未保存の入力を取り消す').disabled).toBe(false)

    const selector = kind === 'text' ? '.token-editing input' : '.speaker-editor input'
    const draft = requiredElement<HTMLInputElement>(selector)
    const text = kind === 'text' ? '送信できなかった本文' : '送信できなかった姓'
    expect(draft.value).toBe(text)
    expect(draft.disabled).toBe(false)
    expect(draft.readOnly).toBe(true)
    clickButton('最新版を読み込む')
    expect(client.sent[2]).toMatchObject({ method: 'transcript.read', params: { projectId } })
    emitEdited(client, 'available', timedTokenId, 2)
    expect(requiredElement<HTMLInputElement>(selector)).toBe(draft)
    expect(draft.value).toBe(text)
    expect(draft.readOnly).toBe(false)
    expect(dirtyChanges).toEqual([false, true, false, true])
    if (kind === 'text') pressKey(draft, 'Enter')
    else submit(requiredElement<HTMLFormElement>('.speaker-editor'))
    expect(client.sent[3]).toMatchObject({
      method: kind === 'text' ? 'transcript.editToken' : 'transcript.setSpeaker',
      params: { revisionId: '20000000-0000-4000-8000-000000000001', digest: 'b'.repeat(64), reason: '同じ変更理由' },
    })
    emitStatus(client, 3, 'noChange')
    expect(container.querySelector('.token-editing')).toBeNull()
    expect(container.querySelector('.speaker-editor')).toBeNull()
    expect(dirtyChanges).toEqual([false, true, false, true, false])
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('同じ変更理由')
  })

  it('preserves reason, speaker and independent token drafts on refresh without replacing the last acknowledged reason', () => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    render(client, undefined, { onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
    emitRead(client)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '前回ACKの理由')
    clickButton('ハブ を編集')
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    emitStatus(client, 1, 'noChange')
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '再取得ではACKしない理由')
    clickButton('話者を設定')
    const surname = requiredElement<HTMLInputElement>('.speaker-editor input')
    setText(surname, '保持する姓')
    setSelect(requiredElement<HTMLSelectElement>('.speaker-format-select'), 'surname')
    clickButton('ハブ を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), '独立した本文')
    submit(requiredElement<HTMLFormElement>('.speaker-editor'))
    emitStatus(client, 2, 'conflict')
    clickButton('最新版を読み込む')
    emitRead(client, 3)

    expect(requiredElement<HTMLInputElement>('.speaker-editor input')).toBe(surname)
    expect(surname.value).toBe('保持する姓')
    expect(surname.readOnly).toBe(false)
    expect(requiredElement<HTMLSelectElement>('.speaker-format-select').value).toBe('surname')
    expect(requiredElement<HTMLInputElement>('.token-editing input').value).toBe('独立した本文')
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('再取得ではACKしない理由')
    expect(dirtyChanges).toEqual([false, true, false, true])
    act(() => requiredElement<HTMLButtonElement>('.token-edit-actions button:last-child').click())
    act(() => requiredElement<HTMLButtonElement>('.speaker-editor button[type="button"]').click())
    expect(dirtyChanges).toEqual([false, true, false, true])
    clickButton('未保存の入力を取り消す')
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('前回ACKの理由')
    expect(dirtyChanges).toEqual([false, true, false, true, false])
    expect(client.sent.map((request) => request.method)).toEqual([
      'transcript.read', 'transcript.editToken', 'transcript.setSpeaker', 'transcript.read',
    ])
  })

  it.each(['revision', 'speaker revision', 'source span', 'speaker', 'performer'] as const)(
    'does not rebase a speaker draft when refresh changes its %s',
    (change) => {
      const client = renderAvailable()
      setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '話者の基準を確認')
      clickButton('ハブ を編集')
      setText(requiredElement<HTMLInputElement>('.token-editing input'), '別の下書き')
      clickButton('話者を設定')
      setText(requiredElement<HTMLInputElement>('.speaker-editor input'), '貼り直す姓')
      submit(requiredElement<HTMLFormElement>('.speaker-editor'))
      emitStatus(client, 1, 'conflict')
      clickButton('最新版を読み込む')
      const value = createTranscriptResponseValue()
      value.requestId = client.sent[2]!.requestId
      const snapshot = value.result.snapshot!
      const replacementId = '80000000-0000-4000-8000-000000000001'
      if (change === 'revision') snapshot.revisionId = replacementId
      else if (change === 'speaker revision') snapshot.speakerRevisionId = replacementId
      else if (change === 'source span') {
        snapshot.utterances[0]!.sourceSpanId = replacementId
        snapshot.termCandidates = []
      } else if (change === 'speaker') snapshot.utterances[0]!.speaker.speakerId = replacementId
      else snapshot.performers = []
      client.emit(requireResponse(value))

      const surname = requiredElement<HTMLInputElement>('.speaker-editor input')
      expect(surname.value).toBe('貼り直す姓')
      expect(surname.disabled).toBe(false)
      expect(surname.readOnly).toBe(true)
      expect(requiredElement<HTMLSelectElement>('.speaker-editor select').value).toBe(performerId)
      expect(buttonByText('設定').disabled).toBe(true)
      expect(container.textContent).toContain('話者設定の基準が更新されました。')
      submit(requiredElement<HTMLFormElement>('.speaker-editor'))
      expect(client.sent).toHaveLength(3)
      act(() => requiredElement<HTMLButtonElement>('.speaker-editor button[type="button"]').click())
      expect(container.querySelector('.speaker-editor')).toBeNull()
      expect(requiredElement<HTMLInputElement>('.token-editing input').value).toBe('別の下書き')
      expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('話者の基準を確認')
    },
  )

  it('does not clear an unsubmitted speaker draft when a text edit succeeds', () => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    render(client, undefined, { onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
    emitRead(client)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '表記を確認')
    clickButton('話者を設定')
    const surname = requiredElement<HTMLInputElement>('.speaker-editor input')
    setText(surname, '未送信の姓')
    clickButton('ハブ を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), 'Hub')
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    emitEdited(client, 'available')

    expect(container.querySelector('.token-editing')).toBeNull()
    expect(requiredElement<HTMLInputElement>('.speaker-editor input')).toBe(surname)
    expect(surname.value).toBe('未送信の姓')
    expect(surname.readOnly).toBe(true)
    expect(buttonByText('設定').disabled).toBe(true)
    expect(dirtyChanges).toEqual([false, true])
    clickButton('取消')
    expect(dirtyChanges).toEqual([false, true, false])
    expect(client.sent).toHaveLength(2)
  })

  it.each([
    ['transcript.setSpeaker', 'available'],
    ['transcript.setSpeaker', 'noChange'],
    ['transcript.adoptTerm', 'available'],
    ['transcript.adoptTerm', 'noChange'],
    ['transcript.rejectTerm', 'available'],
    ['transcript.rejectTerm', 'noChange'],
  ] as const)('keeps an independent token draft dirty after %s %s until cancellation releases a project switch', (method, status) => {
    const first = new FakeTranscriptClient()
    const second = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
    const props = { context: { projectId }, onDirtyChange }
    render(first, undefined, props)
    emitRead(first)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '別の変更だけを保存')
    clickButton(method === 'transcript.setSpeaker' ? 'ハブ を編集' : '未時刻 を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), '独立した未送信の本文')
    if (method === 'transcript.setSpeaker') {
      clickButton('話者を設定')
      setText(requiredElement<HTMLInputElement>('.speaker-editor input'), '佐藤')
      submit(requiredElement<HTMLFormElement>('.speaker-editor'))
    } else {
      clickButton(method === 'transcript.adoptTerm' ? '採用' : '却下')
    }
    expect(first.sent.map((request) => request.method)).toEqual(['transcript.read', method])
    expect(requiredElement<HTMLInputElement>('.token-editing input').disabled).toBe(true)
    expect(buttonByText('未保存の入力を取り消す').disabled).toBe(true)

    render(second, undefined, { ...props, context: { projectId: otherProjectId } })
    if (status === 'noChange') emitStatus(first, 1, status)
    else if (method === 'transcript.setSpeaker') emitSpeakerAssigned(first)
    else if (method === 'transcript.adoptTerm') emitEdited(first, 'available')
    else {
      const value = createTranscriptResponseValue(method)
      value.requestId = first.sent[1]!.requestId
      value.result.snapshot!.termCandidates = []
      first.emit(requireResponse(value))
    }
    act(() => vi.runAllTicks())

    expect(dirtyChanges).toEqual([false, true])
    expect(first.disposed).toBe(false)
    expect(first.listenerCount).toBe(1)
    expect(second.sent).toHaveLength(0)
    expect(second.listenerCount).toBe(0)
    expect(container.querySelector('.transcript-workspace')).toBeNull()
    expect(container.textContent).not.toContain('独立した未送信の本文')
    expect(container.textContent).toContain('未保存の入力または保存中の変更を保持しています。')
    expect(buttonByText('未保存の入力を取り消す').disabled).toBe(false)

    render(first, undefined, props)
    const edit = requiredElement<HTMLInputElement>('.token-editing input')
    const stale = method === 'transcript.adoptTerm' && status === 'available'
    expect(edit.value).toBe('独立した未送信の本文')
    expect(edit.disabled).toBe(false)
    expect(edit.readOnly).toBe(stale)
    expect(buttonByText('保存').disabled).toBe(stale)
    expect(container.querySelector('.speaker-editor')).toBeNull()
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('別の変更だけを保存')
    expect(buttonByText('最新版を読み込む').disabled).toBe(false)
    if (stale) {
      expect(container.textContent).toContain('文字修正の基準が更新されました。')
      pressKey(edit, 'Enter')
      clickButton('Hub を編集')
      expect(requiredElement<HTMLInputElement>('.token-editing input').value).toBe('独立した未送信の本文')
    }
    expect(first.sent).toHaveLength(2)
    expect(dirtyChanges).toEqual([false, true])

    render(second, undefined, { ...props, context: { projectId: otherProjectId } })
    expect(second.sent).toHaveLength(0)
    clickButton('未保存の入力を取り消す')
    act(() => vi.runAllTicks())
    expect(dirtyChanges).toEqual([false, true, false])
    expect(first.disposed).toBe(true)
    expect(first.sent).toHaveLength(2)
    expect(second.sent).toHaveLength(1)
    expect(second.sent[0]?.params).toEqual({ projectId: otherProjectId })
    emitRead(second, 0, otherProjectId)
    expect(container.querySelector('.token-editing')).toBeNull()
    expect(container.querySelector('.speaker-editor')).toBeNull()
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('')
    expect(container.textContent).not.toContain('独立した未送信の本文')
  })

  it('retains a stale draft whose token is absent without applying it to a replacement token', () => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    render(client, undefined, { onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
    emitRead(client)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '辞書候補だけを保存')
    clickButton('ハブ を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), '失われた単語の未送信本文')
    clickButton('採用')
    emitEdited(client, 'available', '80000000-0000-4000-8000-000000000001')

    expect(dirtyChanges).toEqual([false, true])
    expect(requiredElement<HTMLInputElement>('.transcript-preserved-draft input').value).toBe('失われた単語の未送信本文')
    expect(requiredElement<HTMLInputElement>('.transcript-preserved-draft input').readOnly).toBe(true)
    expect(container.textContent).toContain('文字修正の基準が更新されました。')
    expect(container.textContent).toContain('失われた単語の未送信本文')
    expect(requiredElement<HTMLButtonElement>('button[aria-label="Hub を編集"]').disabled).toBe(true)
    clickButton('Hub を編集')
    expect(requiredElement<HTMLInputElement>('.transcript-preserved-draft input').value).toBe('失われた単語の未送信本文')
    expect(buttonByText('最新版を読み込む').disabled).toBe(false)
    expect(client.sent.map((request) => request.method)).toEqual(['transcript.read', 'transcript.adoptTerm'])

    clickButton('未保存の入力を取り消す')
    expect(dirtyChanges).toEqual([false, true, false])
    clickButton('最新版を読み込む')
    expect(client.sent[2]).toMatchObject({ method: 'transcript.read', params: { projectId } })
    emitRead(client, 2)
    expect(container.querySelector('.token-editing')).toBeNull()
    expect(container.textContent).not.toContain('失われた単語の未送信本文')
  })

  it('sends only one effective initial read during StrictMode effect replay', () => {
    const clients: FakeTranscriptClient[] = []
    const createClient = (): FakeTranscriptClient => {
      const client = new FakeTranscriptClient()
      clients.push(client)
      return client
    }
    const renderStrict = (active: boolean): void => {
      act(() => root.render(
        <StrictMode>
          <TranscriptPanelView projectId={projectId} context={{ projectId }} active={active} createClient={createClient} />
        </StrictMode>,
      ))
    }
    renderStrict(true)
    expect(clients).toHaveLength(2)
    expect(clients.flatMap((client) => client.sent)).toHaveLength(0)
    act(() => vi.runAllTicks())
    expect(clients[0]?.disposed).toBe(true)
    expect(clients[0]?.listenerCount).toBe(0)
    expect(clients[0]?.sent).toHaveLength(0)
    expect(clients[1]?.sent).toHaveLength(1)
    emitRead(clients[1]!)
    renderStrict(false)
    act(() => vi.runAllTicks())
    renderStrict(true)
    act(() => vi.runAllTicks())
    expect(clients).toHaveLength(2)
    expect(clients[1]?.sent).toHaveLength(1)
    expect(clients[1]?.listenerCount).toBe(1)
    expect(container.textContent).toContain('文: ハブ 未時刻')
  })

  it.each(['hidden', 'changed project', 'cleared project', 'unmounted'] as const)(
    'cancels a queued initial read when %s before the microtask runs',
    (change) => {
      const first = new FakeTranscriptClient()
      const second = new FakeTranscriptClient()
      render(first, undefined, { context: { projectId } }, false)
      expect(first.sent).toHaveLength(0)
      if (change === 'hidden') {
        render(first, undefined, { context: { projectId }, active: false }, false)
      } else if (change === 'changed project') {
        render(second, undefined, { context: { projectId: otherProjectId } }, false)
      } else if (change === 'cleared project') {
        render(first, undefined, { context: null }, false)
      } else {
        act(() => root.unmount())
        root = createRoot(container)
      }
      act(() => vi.runAllTicks())
      expect(first.sent).toHaveLength(0)
      if (change === 'hidden') {
        expect(first.disposed).toBe(false)
        render(first, undefined, { context: { projectId }, active: true })
        expect(first.sent).toHaveLength(1)
      } else {
        expect(first.disposed).toBe(true)
        expect(first.listenerCount).toBe(0)
      }
      expect(second.sent).toHaveLength(change === 'changed project' ? 1 : 0)
      if (change === 'changed project') expect(second.sent[0]?.params).toEqual({ projectId: otherProjectId })
    },
  )

  it('rejects old request responses and queued callbacks after a clean project switch', () => {
    const first = new FakeTranscriptClient()
    const second = new FakeTranscriptClient()
    render(first, undefined, { context: { projectId } })
    const value = createTranscriptResponseValue()
    value.requestId = first.sent[0]!.requestId
    const oldResponse = requireResponse(value)
    const deliverQueued = first.captureDelivery(oldResponse)

    render(second, undefined, { context: { projectId: otherProjectId } })
    expect(first.disposed).toBe(true)
    expect(first.listenerCount).toBe(0)
    expect(second.sent).toHaveLength(1)
    expect(second.sent[0]?.params).toEqual({ projectId: otherProjectId })

    deliverQueued()
    second.emit(oldResponse)
    expect(container.querySelector('.transcript-workspace')).toBeNull()
    expect(requiredElement<HTMLElement>('.transcript-status').dataset.transcriptStatus).toBe('loading')
    emitRead(second, 0, otherProjectId)
    expect(container.textContent).toContain('文: ハブ 未時刻')
    second.emit(oldResponse)
    expect(requiredElement<HTMLElement>('.transcript-status').dataset.transcriptStatus).toBe('available')
    expect(second.sent).toHaveLength(1)
  })

  it('allows only explicit draft cancellation to release a failed mutation before switching projects', () => {
    const first = new FakeTranscriptClient()
    const second = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
    render(first, undefined, { context: { projectId }, onDirtyChange })
    emitRead(first)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '失敗しても保持する理由')
    clickButton('ハブ を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), 'Hub')
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    render(second, undefined, { context: { projectId: otherProjectId }, onDirtyChange })
    expect(buttonByText('未保存の入力を取り消す').disabled).toBe(true)

    emitStatus(first, 1, 'conflict')
    expect(second.sent).toHaveLength(0)
    expect(first.disposed).toBe(false)
    expect(dirtyChanges.at(-1)).toBe(true)
    expect(container.querySelector('.transcript-workspace')).toBeNull()
    expect(buttonByText('未保存の入力を取り消す').disabled).toBe(false)

    clickButton('未保存の入力を取り消す')
    act(() => vi.runAllTicks())
    expect(first.disposed).toBe(true)
    expect(dirtyChanges.at(-1)).toBe(false)
    expect(first.sent.map((request) => request.method)).toEqual([
      'transcript.read', 'transcript.editToken',
    ])
    expect(second.sent).toHaveLength(1)
    expect(second.sent[0]?.params).toEqual({ projectId: otherProjectId })
  })

  it('does not display a wrong-project snapshot even with the matching request ID', () => {
    const client = new FakeTranscriptClient()
    render(client, undefined, { context: { projectId: otherProjectId } })
    emitRead(client)

    expect(container.querySelector('.transcript-workspace')).toBeNull()
    expect(container.textContent).not.toContain('ハブ')
    expect(requiredElement<HTMLElement>('.transcript-status').dataset.transcriptStatus).toBe('invalid')
    clickButton('最新版を読み込む')
    expect(client.sent).toHaveLength(2)
    expect(client.sent[1]?.params).toEqual({ projectId: otherProjectId })
    emitRead(client, 1, otherProjectId)
    expect(container.textContent).toContain('文: ハブ 未時刻')
  })

  it.each(['transcript.editToken', 'transcript.read'] as const)('rejects a wrong-project %s response without exposing it or acknowledging local drafts', (method) => {
    const client = new FakeTranscriptClient()
    const dirtyChanges: boolean[] = []
    render(client, undefined, { context: { projectId }, onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
    emitRead(client)
    setText(requiredElement<HTMLInputElement>('.transcript-reason input'), '自分の案件の理由')
    clickButton('話者を設定')
    setText(requiredElement<HTMLInputElement>('.speaker-editor input'), '未送信の姓')
    clickButton('ハブ を編集')
    setText(requiredElement<HTMLInputElement>('.token-editing input'), '自分の案件の本文')
    pressKey(requiredElement<HTMLInputElement>('.token-editing input'), 'Enter')
    if (method === 'transcript.read') {
      emitStatus(client, 1, 'conflict')
      clickButton('最新版を読み込む')
    }
    const rejectedIndex = method === 'transcript.read' ? 2 : 1
    const value = createTranscriptResponseValue(method)
    value.requestId = client.sent[rejectedIndex]!.requestId
    const foreign = value.result.snapshot!
    foreign.projectId = otherProjectId
    foreign.termCandidates = []
    foreign.utterances[0]!.text = foreign.utterances[0]!.sentences[0]!.text = '別案件の秘密 未時刻'
    foreign.utterances[0]!.sentences[0]!.tokens[0]!.text = '別案件の秘密'
    client.emit(requireResponse(value))

    expect(requiredElement<HTMLElement>('.transcript-status').dataset.transcriptStatus).toBe('invalid')
    expect(container.innerHTML).not.toContain('別案件の秘密')
    expect(container.textContent).not.toContain(otherProjectId)
    expect(requiredElement<HTMLInputElement>('.token-editing input').value).toBe('自分の案件の本文')
    expect(requiredElement<HTMLInputElement>('.token-editing input').readOnly).toBe(true)
    expect(buttonByText('保存').disabled).toBe(true)
    clickButton('最新版を読み込む')
    expect(client.sent[rejectedIndex + 1]?.params).toEqual({ projectId })
    emitRead(client, rejectedIndex + 1)
    expect(requiredElement<HTMLInputElement>('.token-editing input').readOnly).toBe(false)
    expect(requiredElement<HTMLInputElement>('.speaker-editor input').value).toBe('未送信の姓')
    expect(dirtyChanges).toEqual([false, true])
    clickButton('未保存の入力を取り消す')
    expect(requiredElement<HTMLInputElement>('.transcript-reason input').value).toBe('')
    expect(dirtyChanges).toEqual([false, true, false])
    expect(client.sent).toHaveLength(rejectedIndex + 2)
  })

  it.each([null, {}, { projectId: '' }])('removes previous project output when selection becomes %j', (context) => {
    const client = new FakeTranscriptClient()
    render(client, undefined, { context: { projectId } })
    emitRead(client)
    render(client, undefined, { context })

    expect(client.sent).toHaveLength(1)
    expect(client.disposed).toBe(true)
    expect(container.querySelector('.transcript-workspace')).toBeNull()
    expect(container.textContent).not.toContain('ハブ')
    expect(container.textContent).toContain('確定済み案件を案件一覧から開いてください。')
    expect(buttonByText('最新版を読み込む').disabled).toBe(true)
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
    flushInitialRead = true,
  ): void {
    act(() => {
      root.render(
        <PlayerNavigationProvider>
          {sourceSeek === undefined ? null : <SeekRegistration handler={sourceSeek} />}
          <button type="button" className="outside-transcript">別の道具</button>
          <TranscriptPanelView
            projectId={projectId}
            createClient={() => client}
            {...props}
          />
        </PlayerNavigationProvider>,
      )
    })
    if (flushInitialRead) act(() => vi.runAllTicks())
  }

  function renderPanel(
    client: FakeTranscriptClient,
    props: TranscriptPanelProps,
  ): void {
    act(() => {
      root.render(
        <ProjectStateProvider createClient={unavailableProjectClient}>
          <TranscriptPanel createClient={() => client} {...props} />
        </ProjectStateProvider>,
      )
    })
    act(() => vi.runAllTicks())
  }

  function emitRead(
    client: FakeTranscriptClient,
    sentIndex = 0,
    targetProjectId = projectId,
  ): void {
    const sent = client.sent[sentIndex]
    if (sent === undefined) throw new Error('Expected a transcript read request.')
    const value = createTranscriptResponseValue('transcript.read')
    value.requestId = sent.requestId
    value.result.snapshot!.projectId = targetProjectId
    client.emit(requireResponse(value))
  }

  function emitEdited(
    client: FakeTranscriptClient,
    status: 'available',
    tokenId = timedTokenId,
    sentIndex = 1,
  ): void {
    const sent = client.sent[sentIndex]
    if (sent === undefined) throw new Error('Expected a transcript edit request.')
    const value = createTranscriptResponseValue(sent.method)
    value.requestId = sent.requestId
    const snapshot = value.result.snapshot
    if (snapshot === null) throw new Error('Expected a mutable transcript snapshot.')
    snapshot.utterances[0]!.sentences[0]!.tokens[0]!.id = tokenId
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
        tokenId,
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
    status: Exclude<TranscriptResponse['result']['status'], 'available'>,
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

const otherProjectId = '90000000-0000-4000-8000-000000000001'

function unavailableProjectClient(): never {
  throw new Error('Project transport is unavailable in this transcript test.')
}

class FakeTranscriptClient implements TranscriptBridgeClient {
  readonly sent: TranscriptRequest[] = []
  disposed = false
  failNextSend = false
  private readonly listeners = new Set<(response: TranscriptResponse) => void>()

  get listenerCount(): number {
    return this.listeners.size
  }

  sendTranscript(request: TranscriptRequest): void {
    if (this.failNextSend) {
      this.failNextSend = false
      throw new Error('Transcript transport failed.')
    }
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

  captureDelivery(response: TranscriptResponse): () => void {
    const listeners = [...this.listeners]
    return () => {
      act(() => {
        for (const listener of listeners) listener(response)
      })
    }
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
