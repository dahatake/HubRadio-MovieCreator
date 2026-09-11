// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
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
  parseAnalysisResponse,
  type AnalysisRequest,
  type AnalysisResponse,
  type AnalysisSnapshot,
} from './analysisProtocol'
import {
  AnalysisPanel,
  AnalysisPanelView,
  type AnalysisPanelViewProps,
} from './AnalysisPanel'
import type { AnalysisBridgeClient } from './analysisBridgeSession'
import {
  analysisDigest,
  analysisMetadataRevisionId,
  analysisProjectId,
  analysisRevisionId,
  analysisSourceSpanId,
  analysisTopicId,
  createAllProducerCandidates,
  createAnalysisResponseValue,
} from './analysisTestFixture'

describe('AnalysisPanelView', () => {
  let container: HTMLDivElement
  let otherTab: HTMLButtonElement
  let root: Root

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    otherTab = document.createElement('button')
    otherTab.textContent = '別のタブ'
    document.body.append(otherTab, container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    otherTab.remove()
    vi.restoreAllMocks()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  it.each([
    { name: 'unselected', projectId: null },
    { name: 'empty', projectId: '' },
    { name: 'blank', projectId: ' ' },
    { name: 'malformed', projectId: 'not-a-project' },
    { name: 'nil UUID', projectId: '00000000-0000-0000-0000-000000000000' },
    { name: 'uppercase UUID', projectId: '10000000-0000-4000-8000-00000000000A' },
    { name: 'padded UUID', projectId: ` ${analysisProjectId}` },
    { name: 'newline-suffixed UUID', projectId: `${analysisProjectId}\n` },
  ])('does not connect or expose forms for $name workflow context', async ({ projectId }) => {
    const client = new FakeAnalysisClient()
    act(() => otherTab.focus())
    // The legacy shorthand remains valid, but explicit context must take precedence.
    await render(client, undefined, { context: { projectId } })

    expect(client.sent).toHaveLength(0)
    expect(client.listenerCount).toBe(0)
    expect(container.textContent).toContain('未選択')
    expect(container.querySelector('.analysis-workspace')).toBeNull()
    expect(container.querySelector('form, input, textarea, select, button')).toBeNull()
    expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('idle')
    expect(document.activeElement).toBe(otherTab)
  })

  it('accepts project-only context without a project provider and focuses visible results', async () => {
    const client = new FakeAnalysisClient()
    await act(async () => {
      otherTab.focus()
      root.render(
        <AnalysisPanel
          context={{ projectId: analysisProjectId }}
          createClient={client.createClient}
        />,
      )
    })

    expect(client.sent).toHaveLength(1)
    expect(client.sent[0]).toMatchObject({ method: 'analysis.read' })
    expect(client.sent[0]?.params).toEqual({ projectId: analysisProjectId })
    expect(container.querySelector('.analysis-workspace')).toBeNull()
    expect(document.activeElement).toBe(otherTab)
    emitRead(client)

    expect(requiredElement<HTMLElement>('.analysis-panel').hidden).toBe(false)
    expect(document.activeElement).toBe(requiredElement('#analysis-title'))
    expect(container.textContent).toContain('termCorrection の根拠')
    expect(container.textContent).toContain('判断・変更理由')
    expect(container.textContent).not.toContain('重要発言の元区間 ID')
    expect(container.querySelectorAll('.analysis-topic-field')).toHaveLength(6)
    expect(requiredElement<HTMLInputElement>('input', formContaining('タイトル')).value)
      .toBe('クラウド運用')
  })

  it.each([true, false])('reads once in StrictMode with changing factories (initially active: %s)', async (initiallyActive) => {
    const clients: FakeAnalysisClient[] = []
    const onDirtyChange = vi.fn()
    const createClient = (): AnalysisBridgeClient => {
      const client = new FakeAnalysisClient()
      clients.push(client)
      return client
    }
    const renderStrict = async (active = true): Promise<void> => {
      await act(async () => {
        root.render(
          <StrictMode>
            <AnalysisPanel
              context={{ projectId: analysisProjectId }}
              active={active}
              onDirtyChange={(dirty) => onDirtyChange(dirty)}
              createClient={() => createClient()}
            />
          </StrictMode>,
        )
      })
    }

    await renderStrict(initiallyActive)

    expect(clients).toHaveLength(2)
    expect(onDirtyChange.mock.calls).toEqual([[false]])
    const [discardedClient, client] = clients
    if (discardedClient === undefined || client === undefined) {
      throw new Error('Expected discarded and live StrictMode clients.')
    }
    expect(discardedClient.disposed).toBe(true)
    expect(discardedClient.listenerCount).toBe(0)
    expect(discardedClient.sent).toHaveLength(0)
    if (!initiallyActive) {
      expect(client.sent).toHaveLength(0)
      expect(requiredElement<HTMLElement>('.analysis-panel').hidden).toBe(true)
      expect(buttonByText('最新版を読み込む').disabled).toBe(true)
      clickButton('最新版を読み込む')
      await renderStrict(false)
      expect(clients).toHaveLength(2)
      expect(client.sent).toHaveLength(0)
      expect(client.listenerCount).toBe(1)
      expect(client.disposed).toBe(false)
      await renderStrict(true)
    }
    expect(client.sent).toHaveLength(1)
    expect(client.sent[0]).toMatchObject({
      method: 'analysis.read',
      params: { projectId: analysisProjectId },
    })

    // A new inline factory must keep the pending request and its response listener.
    await renderStrict()
    expect(clients).toHaveLength(2)
    expect(clients.flatMap((created) => created.sent)).toHaveLength(1)
    emitRead(client)
    const title = requiredElement<HTMLInputElement>('input', formContaining('タイトル'))
    const reason = requiredElement<HTMLInputElement>('.analysis-reason input')
    setText(title, '編集中のタイトル')
    setText(reason, '確認中の理由')
    act(() => title.focus())

    await renderStrict()

    expect(clients).toHaveLength(2)
    expect(clients.flatMap((created) => created.sent)).toHaveLength(1)
    expect(client.disposed).toBe(false)
    expect(client.listenerCount).toBe(1)
    expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('available')
    expect(requiredElement<HTMLInputElement>('input', formContaining('タイトル'))).toBe(title)
    expect(title.value).toBe('編集中のタイトル')
    expect(reason.value).toBe('確認中の理由')
    expect(document.activeElement).toBe(title)
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])

    // An explicit refresh still sends exactly one request through the live session.
    clickButton('最新版を読み込む')
    expect(client.sent).toHaveLength(2)
    expect(client.sent[1]).toMatchObject({
      method: 'analysis.read',
      params: { projectId: analysisProjectId },
    })
    expect(clients.flatMap((created) => created.sent)).toHaveLength(2)
  })

  it('loads once when an actual project is selected and ignores context object identity', async () => {
    const client = new FakeAnalysisClient()
    await render(client, undefined, { context: { projectId: null } })
    expect(client.sent).toHaveLength(0)

    await render(client, undefined, { context: { projectId: analysisProjectId } })
    expect(client.sent).toHaveLength(1)
    // A fresh context object while the read is pending must not restart the session.
    await render(client, undefined, { context: { projectId: analysisProjectId } })
    emitRead(client)
    const title = requiredElement<HTMLInputElement>('input', formContaining('タイトル'))
    setText(title, '編集中のタイトル')
    act(() => title.focus())
    await render(client, undefined, { context: { projectId: analysisProjectId } })

    expect(client.sent).toHaveLength(1)
    expect(client.listenerCount).toBe(1)
    expect(client.disposed).toBe(false)
    expect(title.value).toBe('編集中のタイトル')
    expect(document.activeElement).toBe(title)
  })

  it('refreshes unavailable analysis once per registration key, only when visible, without retry spam', async () => {
    const client = new FakeAnalysisClient()
    const context = { projectId: analysisProjectId, refreshKey: 'before-analysis' }
    await render(client, undefined, { context })
    emitStatus(client, 0, 'unavailable')
    for (const active of [true, false, true]) await render(client, undefined, { context: { ...context }, active })
    expect(client.sent).toHaveLength(1)
    const ready = { ...context, refreshKey: 'analysis-ready' }
    await render(client, undefined, { context: ready, active: false })
    expect(client.sent).toHaveLength(1)
    await render(client, undefined, { context: ready })
    expect(client.sent).toHaveLength(2)
    expect(client.sent[1]?.params).toEqual({ projectId: analysisProjectId })
    emitRead(client, 1)
    expect(container.querySelector('.analysis-workspace')).not.toBeNull()
    for (const active of [true, false, true]) await render(client, undefined, { context: { ...ready }, active })
    expect(client.sent).toHaveLength(2)
    const completed = { ...context, refreshKey: 'completed-registration' }
    await render(client, undefined, { context: completed })
    emitStatus(client, 2, 'unavailable')
    for (const active of [false, true, true]) await render(client, undefined, { context: { ...completed }, active })
    expect(client.sent).toHaveLength(3)
    expect(client.listenerCount).toBe(1)
    expect(client.disposed).toBe(false)
  })

  it('holds dirty analysis input DOM and selection across registration changes and reloads the latest key after discard', async () => {
    const client = new FakeAnalysisClient()
    const onDirtyChange = vi.fn()
    await render(client, undefined, { context: { projectId: analysisProjectId, refreshKey: 'old' }, onDirtyChange })
    emitRead(client)
    const title = requiredElement<HTMLInputElement>('input', formContaining('タイトル'))
    const reason = requiredElement<HTMLInputElement>('.analysis-reason input')
    setText(title, '保持するタイトル'); setText(reason, '保持する理由')
    title.setSelectionRange(1, 4)
    for (const [refreshKey, active] of [['ready', false], ['ready', true], ['completed', true]] as const) {
      await render(client, undefined, { context: { projectId: analysisProjectId, refreshKey }, active, onDirtyChange })
      expect(client.sent).toHaveLength(1)
      expect(requiredElement<HTMLInputElement>('input', formContaining('タイトル'))).toBe(title)
      expect(title.value).toBe('保持するタイトル')
      expect(title.selectionStart).toBe(1); expect(title.selectionEnd).toBe(4)
      expect(reason.value).toBe('保持する理由')
      expect(requiredElement('.analysis-registration-notice').textContent).toContain('未送信の入力を保持')
    }
    clickButton('未送信の入力を取り消す')
    await render(client, undefined, { context: { projectId: analysisProjectId, refreshKey: 'completed' }, onDirtyChange })
    expect(client.sent).toHaveLength(2)
    emitRead(client, 1)
    expect(container.querySelector('.analysis-registration-notice')).toBeNull()
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
    expect(client.listenerCount).toBe(1)
  })

  it('coalesces registration changes while a read is pending rather than dropping the newest registration', async () => {
    const client = new FakeAnalysisClient()
    await render(client, undefined, { context: { projectId: analysisProjectId, refreshKey: 'before' } })
    await render(client, undefined, { context: { projectId: analysisProjectId, refreshKey: 'ready' } })
    await render(client, undefined, { context: { projectId: analysisProjectId, refreshKey: 'latest' } })
    expect(client.sent).toHaveLength(1)
    emitStatus(client, 0, 'unavailable')
    await render(client, undefined, { context: { projectId: analysisProjectId, refreshKey: 'latest' } })
    expect(client.sent).toHaveLength(2)
    emitRead(client, 1)
    await render(client, undefined, { context: { projectId: analysisProjectId, refreshKey: 'latest' } })
    expect(client.sent).toHaveLength(2)
  })

  it('keeps candidates, reasons, source navigation and unsaved topic and speaker drafts across tabs', async () => {
    const client = new FakeAnalysisClient()
    const sought: Parameters<PlayerSourceSeekHandler>[0][] = []
    const sourceSeek: PlayerSourceSeekHandler = (target) => {
      sought.push(target)
      return 'requested'
    }
    await render(client, sourceSeek, { context: { projectId: analysisProjectId } })
    emitRead(client)
    const candidate = cardContaining('用語修正候補', '用語辞書')
    const reason = requiredElement<HTMLInputElement>('.analysis-reason input')
    const title = requiredElement<HTMLInputElement>('input', formContaining('タイトル'))
    const summary = requiredElement<HTMLTextAreaElement>('textarea', formContaining('要約'))
    const speaker = requiredElement<HTMLSelectElement>('select', formContaining('主話者'))
    setText(reason, '判断と主話者を確認')
    setText(title, '未保存のタイトル')
    setText(summary, '未保存の要約')
    act(() => {
      speaker.value = ''
      speaker.dispatchEvent(new Event('change', { bubbles: true }))
    })
    clickButtonIn(candidate, '元時刻へ移動')
    act(() => otherTab.focus())

    for (const active of [false, true, false, true]) {
      await render(client, sourceSeek, { context: { projectId: analysisProjectId }, active })

      expect(requiredElement<HTMLElement>('.analysis-panel').hidden).toBe(!active)
      expect(cardContaining('用語修正候補', '用語辞書')).toBe(candidate)
      expect(requiredElement<HTMLInputElement>('.analysis-reason input')).toBe(reason)
      expect(requiredElement<HTMLInputElement>('input', formContaining('タイトル'))).toBe(title)
      expect(requiredElement<HTMLSelectElement>('select', formContaining('主話者'))).toBe(speaker)
      expect(reason.value).toBe('判断と主話者を確認')
      expect(title.value).toBe('未保存のタイトル')
      expect(summary.value).toBe('未保存の要約')
      expect(speaker.value).toBe('')
      expect(container.textContent).toContain('元時刻への移動をプレイヤーへ要求しました。')
      expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('available')
      expect(client.sent).toHaveLength(1)
      expect(client.listenerCount).toBe(1)
      expect(client.disposed).toBe(false)
      expect(document.activeElement).toBe(otherTab)
    }

    clickButtonIn(candidate, '元時刻へ移動')
    expect(sought).toEqual([
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
      { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
    ])
    submit(formContaining('主話者'))
    expect(client.sent[1]).toMatchObject({
      method: 'analysis.updateTopic',
      params: {
        projectId: analysisProjectId,
        revisionId: analysisRevisionId,
        digest: analysisDigest,
        topicId: analysisTopicId,
        expectedTopicRevisionId: analysisMetadataRevisionId,
        update: { field: 'mainSpeaker', speakerId: null },
        reason: '判断と主話者を確認',
      },
    })
  })

  it.each(['inactive', 'hidden ancestor', 'inert ancestor', 'aria-hidden ancestor'])(
    'accepts a successful hidden load without focus or a later reset (%s)',
    async (visibility) => {
      const client = new FakeAnalysisClient()
      if (visibility === 'hidden ancestor') container.hidden = true
      if (visibility === 'inert ancestor') container.setAttribute('inert', '')
      if (visibility === 'aria-hidden ancestor') container.setAttribute('aria-hidden', 'true')
      act(() => otherTab.focus())
      await render(client, undefined, {
        context: { projectId: analysisProjectId },
      })
      // Inactive panels may finish an already-started read, but must not start one.
      if (visibility === 'inactive') {
        await render(client, undefined, {
          context: { projectId: analysisProjectId },
          active: false,
        })
      }
      emitRead(client)

      expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('available')
      expect(container.querySelectorAll('.analysis-candidate-card')).toHaveLength(8)
      expect(document.activeElement).toBe(otherTab)

      container.hidden = false
      container.removeAttribute('inert')
      container.removeAttribute('aria-hidden')
      await render(client, undefined, { context: { projectId: analysisProjectId }, active: true })
      expect(requiredElement<HTMLElement>('.analysis-panel').hidden).toBe(false)
      expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('available')
      expect(container.querySelectorAll('.analysis-candidate-card')).toHaveLength(8)
      expect(client.sent).toHaveLength(1)
      expect(client.disposed).toBe(false)
      expect(document.activeElement).toBe(otherTab)
    },
  )

  it('shows an unconnected state without forms when the host is unavailable', async () => {
    const client = new FakeAnalysisClient()
    await render(client, undefined, {
      context: { projectId: analysisProjectId },
      createClient: () => { throw new Error('Host unavailable') },
    })

    expect(container.textContent).toContain('未接続')
    expect(container.querySelector('.analysis-workspace')).toBeNull()
    expect(container.querySelector('form, input, textarea, select')).toBeNull()
    expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus)
      .toBe('bridgeUnavailable')
  })

  it.each(['unavailable', 'forbidden', 'invalid', 'staleBase', 'conflict', 'sendFailed'] as const)(
    'does not repeat a failed contextual auto-read (%s) and recovers with a manual read',
    async (status) => {
      const client = new FakeAnalysisClient()
      client.failNextSend = status === 'sendFailed'
      await render(client, undefined, { context: { projectId: analysisProjectId } })
      if (status !== 'sendFailed') emitStatus(client, 0, status)

      for (const active of [true, false, true, false, true]) {
        await render(client, undefined, { context: { projectId: analysisProjectId }, active })
        expect(client.sent).toHaveLength(1)
        expect(client.listenerCount).toBe(1)
        expect(client.disposed).toBe(false)
        expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe(status)
        expect(buttonByText('最新版を読み込む').disabled).toBe(!active)
      }

      clickButton('最新版を読み込む')
      expect(client.sent).toHaveLength(2)
      expect(client.sent[1]).toMatchObject({
        method: 'analysis.read',
        params: { projectId: analysisProjectId },
      })
      emitRead(client, 1)
      await render(client, undefined, { context: { projectId: analysisProjectId } })

      expect(client.sent).toHaveLength(2)
      expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('available')
      expect(container.querySelectorAll('.analysis-candidate-card')).toHaveLength(8)
    },
  )

  it('does not auto-retry request creation failures when a contextual panel is shown again', async () => {
    const client = new FakeAnalysisClient()
    const requestId = vi.spyOn(window.crypto, 'randomUUID').mockImplementationOnce(() => {
      throw new Error('Request ID unavailable')
    })
    await render(client, undefined, { context: { projectId: analysisProjectId } })
    expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('invalid')

    for (const active of [false, true, true]) {
      await render(client, undefined, { context: { projectId: analysisProjectId }, active })
    }
    expect(requestId).toHaveBeenCalledTimes(1)
    expect(client.sent).toHaveLength(0)

    clickButton('最新版を読み込む')
    expect(requestId).toHaveBeenCalledTimes(2)
    expect(client.sent).toHaveLength(1)
    emitRead(client)
    expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('available')
  })

  it('drops cleared sessions and initializes a fresh session when the same project is reopened', async () => {
    const client = new FakeAnalysisClient()
    await render(client, undefined, { context: { projectId: analysisProjectId } })
    emitRead(client)
    setText(requiredElement<HTMLInputElement>('.analysis-reason input'), '前の案件の理由')

    await render(client, undefined, { context: { projectId: null } })
    emitRead(client)

    expect(client.disposed).toBe(true)
    expect(client.listenerCount).toBe(0)
    expect(client.sent).toHaveLength(1)
    expect(container.textContent).toContain('未選択')
    expect(container.textContent).not.toContain('termCorrection の根拠')
    expect(container.querySelector('.analysis-workspace')).toBeNull()

    const freshClient = new FakeAnalysisClient()
    await render(freshClient, undefined, { context: { projectId: analysisProjectId }, active: false })
    expect(freshClient.sent).toHaveLength(0)
    expect(freshClient.listenerCount).toBe(1)
    await render(freshClient, undefined, { context: { projectId: analysisProjectId } })

    expect(freshClient.sent).toHaveLength(1)
    expect(freshClient.sent[0]).toMatchObject({
      method: 'analysis.read',
      params: { projectId: analysisProjectId },
    })
    expect(freshClient.listenerCount).toBe(1)
    expect(freshClient.disposed).toBe(false)
    expect(client.sent).toHaveLength(1)
    emitRead(freshClient)
    expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('available')
    expect(requiredElement<HTMLInputElement>('.analysis-reason input').value).toBe('')
  })

  it('retains the legacy raw-ID form and focus behavior when context is omitted', async () => {
    const client = new FakeAnalysisClient()
    act(() => otherTab.focus())
    await render(client)
    emitRead(client)

    expect(container.textContent).toContain('重要発言の元区間 ID')
    expect(container.querySelectorAll('.analysis-topic-field')).toHaveLength(7)
    expect(document.activeElement).toBe(otherTab)
  })

  it.each(['contextual', 'project provider'] as const)(
    'allows read-only snapshot reloads and source actions through the %s consumer',
    async (consumer) => {
      const client = new FakeAnalysisClient()
      const sought: Parameters<PlayerSourceSeekHandler>[0][] = []
      const sourceSeek: PlayerSourceSeekHandler = (target) => {
        sought.push(target)
        return 'requested'
      }
      const panel = consumer === 'contextual'
        ? <AnalysisPanel context={{ projectId: analysisProjectId }} readOnly createClient={client.createClient} />
        : (
          <ProjectStateProvider createClient={() => { throw new Error('Project host not used') }}>
            <AnalysisPanel projectId={analysisProjectId} readOnly createClient={client.createClient} />
          </ProjectStateProvider>
        )
      await act(async () => {
        root.render(
          <PlayerNavigationProvider>
            <SeekRegistration handler={sourceSeek} />
            {panel}
          </PlayerNavigationProvider>,
        )
      })
      emitRead(client)

      expect(container.textContent).toContain('参照専用')
      expect(container.textContent).toContain('termCorrection の根拠')
      expect(requiredElement<HTMLInputElement>('input', formContaining('タイトル')).value)
        .toBe('クラウド運用')
      const inputs = [...container.querySelectorAll<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(
        'input, textarea, select',
      )]
      expect(inputs).toHaveLength(consumer === 'contextual' ? 7 : 8)
      for (const input of inputs) expect(input.disabled).toBe(true)
      const mutations = [...container.querySelectorAll('button')].filter((button) =>
        button.type === 'submit' || button.textContent === '採用' || button.textContent === '却下',
      )
      expect(mutations).toHaveLength(consumer === 'contextual' ? 21 : 22)
      for (const button of mutations) {
        expect(button.disabled).toBe(true)
        act(() => button.click())
      }
      for (const form of container.querySelectorAll<HTMLFormElement>('.analysis-topic-field')) submit(form)
      expect(container.querySelector('.analysis-input-error')).toBeNull()

      for (const label of [
        '話者発話の元時刻へ移動',
        'トピックの元時刻へ移動',
        '元時刻へ移動',
        '指摘の元時刻へ移動',
      ]) {
        expect(buttonByText(label).disabled).toBe(false)
        clickButton(label)
      }
      expect(sought).toEqual([
        { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
        { projectId: analysisProjectId, sourceTime: { value: '0', rate: '1' } },
        { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
        { projectId: analysisProjectId, sourceTime: { value: '1', rate: '1' } },
      ])
      const transcriptLink = requiredElement<HTMLAnchorElement>('a[href="#transcript-title"]')
      expect(transcriptLink.closest('[hidden], [inert], fieldset[disabled]')).toBeNull()
      let transcriptNavigationAllowed = false
      document.addEventListener('click', (event) => {
        transcriptNavigationAllowed = !event.defaultPrevented
        event.preventDefault()
      }, { once: true })
      act(() => transcriptLink.click())
      expect(transcriptNavigationAllowed).toBe(true)
      expect(client.sent).toHaveLength(1)

      expect(buttonByText('最新版を読み込む').disabled).toBe(false)
      clickButton('最新版を読み込む')
      expect(client.sent.map((request) => request.method)).toEqual(['analysis.read', 'analysis.read'])
      expect(client.sent[1]?.params).toEqual({ projectId: analysisProjectId })
      const latest = createAnalysisResponseValue('analysis.read', createAllProducerCandidates())
      const snapshot = latest.result.snapshot
      const request = client.sent[1]
      if (snapshot === null || request === undefined) throw new Error('Expected a snapshot reload.')
      latest.requestId = request.requestId
      latest.result.snapshot = {
        ...snapshot,
        revisionId: '10000000-0000-4000-8000-000000000010',
        topics: snapshot.topics.map((topic) => ({
          ...topic,
          metadataRevisionId: '10000000-0000-4000-8000-000000000011',
          title: '更新済みのトピック',
        })),
      }
      client.emit(requireResponse(latest))

      expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('available')
      expect(requiredElement<HTMLInputElement>('input', formContaining('タイトル')).value)
        .toBe('更新済みのトピック')
      expect(buttonByText('最新版を読み込む').disabled).toBe(false)
      expect(requiredElement<HTMLInputElement>('.analysis-reason input').disabled).toBe(true)
      expect(client.sent).toHaveLength(2)
    },
  )

  it.each(['readOnly', 'inactive', 'pending'] as const)(
    'guards programmatic edits while %s and preserves standalone drafts when editing resumes',
    async (blockedBy) => {
      const client = new FakeAnalysisClient()
      await render(client)
      emitRead(client)
      const reason = requiredElement<HTMLInputElement>('.analysis-reason input')
      const titleForm = formContaining('タイトル')
      const title = requiredElement<HTMLInputElement>('input', titleForm)
      setText(reason, '保持する判断理由')
      setText(title, '未保存のタイトル')
      const inputs = [...container.querySelectorAll<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(
        'input, textarea, select',
      )]
      const values = inputs.map((input) => input.value)

      if (blockedBy === 'pending') {
        clickButton('最新版を読み込む')
      } else {
        await render(client, undefined, {
          readOnly: blockedBy === 'readOnly',
          active: blockedBy !== 'inactive',
        })
      }
      const sentCount = client.sent.length
      for (const input of inputs) {
        expect(input.disabled).toBe(true)
        if (input instanceof HTMLSelectElement) {
          act(() => {
            input.value = ''
            input.dispatchEvent(new Event('change', { bubbles: true }))
          })
        } else {
          setText(input, '禁止中の変更')
        }
      }
      // A form submit event bypasses a disabled submit button's native protection.
      for (const form of container.querySelectorAll<HTMLFormElement>('.analysis-topic-field')) submit(form)
      for (const button of container.querySelectorAll('button')) {
        if (button.textContent !== '採用' && button.textContent !== '却下') continue
        expect(button.disabled).toBe(true)
        act(() => button.click())
      }
      expect(inputs.map((input) => input.value)).toEqual(values)
      expect(client.sent).toHaveLength(sentCount)
      expect(container.querySelector('.analysis-input-error')).toBeNull()
      expect(buttonByText('最新版を読み込む').disabled).toBe(blockedBy !== 'readOnly')
      expect(buttonByText('トピックの元時刻へ移動').disabled).toBe(blockedBy !== 'readOnly')

      if (blockedBy === 'pending') emitRead(client, 1)
      else await render(client)

      expect(client.sent).toHaveLength(sentCount)
      expect(client.listenerCount).toBe(1)
      expect(client.disposed).toBe(false)
      expect(requiredElement<HTMLInputElement>('input', formContaining('タイトル'))).toBe(title)
      expect(inputs.map((input) => input.value)).toEqual(values)
      for (const input of inputs) expect(input.disabled).toBe(false)
      submit(titleForm)
      expect(client.sent).toHaveLength(sentCount + 1)
      expect(client.sent[sentCount]).toMatchObject({
        method: 'analysis.updateTopic',
        params: {
          reason: '保持する判断理由',
          update: { field: 'title', text: '未保存のタイトル' },
        },
      })
    },
  )

  it('integrates speakers, topics, every producer, safe sensitivity, and persistence state', async () => {
    const client = await renderAvailable()

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

  it('navigates speaker, topic, candidate, and sensitivity entries by source time', async () => {
    const client = new FakeAnalysisClient()
    const sought: Parameters<PlayerSourceSeekHandler>[0][] = []
    await render(client, (target) => {
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

  it('adopts with analysis OCC evidence and no web-supplied target', async () => {
    const client = await renderAvailable()
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

  it('enters a reason and adopts a candidate with keyboard input', async () => {
    const client = await renderAvailable()
    const user = userEvent.setup({ document })
    const reason = requiredElement<HTMLInputElement>('.analysis-reason input')
    act(() => reason.focus())
    await act(async () => user.keyboard('候補を確認'))

    const card = cardContaining('用語修正候補', '用語辞書')
    const adopt = [...card.querySelectorAll<HTMLButtonElement>('button')]
      .find((button) => button.textContent === '採用')
    if (adopt === undefined) throw new Error('Expected an adoption button.')
    act(() => adopt.focus())
    await act(async () => user.keyboard('[Enter]'))

    expect(document.activeElement).toBe(adopt)
    expect(client.sent[1]).toMatchObject({
      method: 'analysis.adopt',
      params: { reason: '候補を確認' },
    })
  })

  it('updates exactly one topic field with analysis and metadata OCC', async () => {
    const client = await renderAvailable()
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

  it('rejects a technical warning but never offers adoption', async () => {
    const client = await renderAvailable()
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

  it('requires an explicit reason and drops stale evidence before recovery', async () => {
    const client = await renderAvailable()
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

  it.each(['available', 'noChange'] as const)(
    'clears only the sent field and reason on %s, keeping other topics and fields without remounting',
    async (status) => {
      const client = new FakeAnalysisClient()
      const onDirtyChange = vi.fn()
      const snapshot = snapshotFixture(true)
      await render(client, undefined, { context: { projectId: analysisProjectId }, onDirtyChange })
      emitSnapshot(client, 0, snapshot)
      const first = topicCard(analysisTopicId)
      const second = topicCard(secondTopicId)
      const titleForm = formContaining('タイトル', first)
      const title = requiredElement<HTMLInputElement>('input', titleForm)
      const summaryForm = formContaining('要約', first)
      const summary = requiredElement<HTMLTextAreaElement>('textarea', summaryForm)
      const keywords = requiredElement<HTMLTextAreaElement>('textarea', formContaining('キーワード', first))
      const speaker = requiredElement<HTMLSelectElement>('select', formContaining('主話者', first))
      const audience = requiredElement<HTMLInputElement>('input', formContaining('対象視聴者', second))
      const cautions = requiredElement<HTMLTextAreaElement>('textarea', formContaining('公開時の注意', second))
      const reason = requiredElement<HTMLInputElement>('.analysis-reason input')
      setText(title, '送信するタイトル')
      setText(summary, '  まだ送らない要約\n\n')
      setText(keywords, ' Azure \n同じ語\n同じ語\n')
      act(() => {
        speaker.value = ''
        speaker.dispatchEvent(new Event('change', { bubbles: true }))
      })
      setText(audience, ' 別トピックの視聴者 ')
      setText(cautions, ' 別トピックの注意\n\n')
      setText(reason, '送信理由')
      submit(titleForm)

      expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
      expect(buttonByText('未送信の入力を取り消す').disabled).toBe(true)
      expect(client.sent[1]).toMatchObject({
        method: 'analysis.updateTopic', params: {
          topicId: analysisTopicId, expectedTopicRevisionId: analysisMetadataRevisionId,
          update: { field: 'title', text: '送信するタイトル' }, reason: '送信理由',
        },
      })
      if (status === 'available') emitSnapshot(client, 1, titleAcknowledged(snapshot))
      else emitStatus(client, 1, 'noChange')

      expect(topicCard(analysisTopicId)).toBe(first)
      expect(topicCard(secondTopicId)).toBe(second)
      expect(requiredElement('input', titleForm)).toBe(title)
      expect(requiredElement('textarea', summaryForm)).toBe(summary)
      expect(title.value).toBe(status === 'available' ? '送信するタイトル' : 'クラウド運用')
      expect(titleForm.querySelector('[data-input-status]')).toBeNull()
      expect(summary.value).toBe('  まだ送らない要約\n\n')
      expect(summary.readOnly).toBe(status === 'available')
      expect(keywords.value).toBe(' Azure \n同じ語\n同じ語\n')
      expect(requiredElement<HTMLInputElement | HTMLSelectElement>('input, select', formContaining('主話者', first)).value)
        .toBe('')
      expect(audience.value).toBe(' 別トピックの視聴者 ')
      expect(cautions.value).toBe(' 別トピックの注意\n\n')
      expect(reason.value).toBe('')
      expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
      expect(client.sent).toHaveLength(2)

      clickButtonIn(summaryForm, status === 'available'
        ? 'この入力を取り消して最新版から編集' : 'この入力を取り消す')
      expect(summary.value).toBe('運用上の判断を説明します。')
      expect(summary.readOnly).toBe(false)
      expect(keywords.value).toBe(' Azure \n同じ語\n同じ語\n')
      expect(audience.value).toBe(' 別トピックの視聴者 ')
      clickButton('未送信の入力を取り消す')

      expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
      expect(keywords.value).toBe('Azure')
      expect(audience.value).toBe('一般視聴者')
      expect(cautions.value).toBe('')
      expect(container.querySelectorAll('.analysis-staged-list li')).toHaveLength(status === 'available' ? 1 : 0)
      expect(container.textContent).toContain('ホスト受付済みの変更（未永続化）')
      expect(client.sent).toHaveLength(2)
    },
  )

  it.each(['staleBase', 'conflict', 'invalid', 'sendFailed', 'forbidden', 'unavailable'] as const)(
    'preserves copyable local values after %s and requires fresh evidence plus an explicit new baseline',
    async (status) => {
      const client = new FakeAnalysisClient()
      const onDirtyChange = vi.fn()
      const props = { context: { projectId: analysisProjectId }, onDirtyChange }
      const snapshot = snapshotFixture()
      await render(client, undefined, props)
      emitSnapshot(client, 0, snapshot)
      const reason = requiredElement<HTMLInputElement>('.analysis-reason input')
      setText(reason, '保持する理由')
      setText(requiredElement<HTMLInputElement>('input', formContaining('タイトル')), '送信するタイトル')
      setText(requiredElement<HTMLTextAreaElement>('textarea', formContaining('要約')), '  自分の原文\n\n')
      client.failNextSend = status === 'sendFailed'
      submit(formContaining('タイトル'))
      if (status !== 'sendFailed') emitStatus(client, 1, status)

      expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe(status)
      expect(container.querySelector('.analysis-workspace')).toBeNull()
      expect(container.querySelector('form, select')).toBeNull()
      for (const evidence of ['クラウド運用', '佐藤 花子', 'termCorrection の根拠', '運用上の判断を説明します。']) {
        expect(container.textContent).not.toContain(evidence)
      }
      const ownInputs = [...container.querySelectorAll<HTMLTextAreaElement>('.analysis-retained-inputs textarea')]
      expect(ownInputs.map((input) => input.value)).toEqual(['送信するタイトル', '  自分の原文\n\n'])
      for (const input of ownInputs) {
        expect(input.readOnly).toBe(true)
        expect(input.disabled).toBe(false)
      }
      act(() => { ownInputs[1]!.focus(); ownInputs[1]!.select() })
      expect(document.activeElement).toBe(ownInputs[1])
      expect(ownInputs[1]!.selectionEnd).toBe(ownInputs[1]!.value.length)
      expect(requiredElement('.analysis-reason input')).toBe(reason)
      expect(reason.readOnly).toBe(true)
      setText(reason, '根拠未確認の変更')
      expect(reason.value).toBe('保持する理由')
      expect(onDirtyChange.mock.calls).toEqual([[false], [true]])

      clickButton('最新版を読み込む')
      expect(client.sent).toHaveLength(3)
      expect(client.sent[2]).toMatchObject({ method: 'analysis.read', params: { projectId: analysisProjectId } })
      expect(ownInputs[1]!.value).toBe('  自分の原文\n\n')
      // The old mutation's late ACK must not satisfy the recovery read.
      emitSnapshot(client, 1, titleAcknowledged(snapshot))
      expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('loading')
      expect(container.querySelector('.analysis-workspace')).toBeNull()
      expect(reason.value).toBe('保持する理由')

      const refreshed: AnalysisSnapshot = {
        ...snapshot,
        revisionId: '10000000-0000-4000-8000-000000000040',
        revisionDigest: 'c'.repeat(64),
        topics: snapshot.topics.map((topic) => ({
          ...topic,
          metadataRevisionId: '10000000-0000-4000-8000-000000000041',
          title: 'ホスト側の新しいタイトル',
          summary: 'ホスト側の新しい要約',
        })),
      }
      act(() => otherTab.focus())
      emitSnapshot(client, 2, refreshed)
      expect(document.activeElement).toBe(otherTab)
      expect(container.querySelector('.analysis-retained-inputs')).toBeNull()
      const titleForm = formContaining('タイトル')
      const title = requiredElement<HTMLInputElement>('input', titleForm)
      const summary = requiredElement<HTMLTextAreaElement>('textarea', formContaining('要約'))
      expect(title.value).toBe('送信するタイトル')
      expect(summary.value).toBe('  自分の原文\n\n')
      expect(title.readOnly).toBe(true)
      expect(summary.readOnly).toBe(true)
      expect(title.disabled).toBe(false)
      expect(buttonByText('タイトルを保存').disabled).toBe(true)
      expect(buttonByText('要約を保存').disabled).toBe(true)
      expect(titleForm.textContent).toContain('入力の基準版が変わったため送信できません')
      expect(document.getElementById(title.getAttribute('aria-describedby')!)?.textContent)
        .toContain('コピーできます')
      setText(title, '旧基準のまま書き換えない')
      submit(titleForm)
      submit(formContaining('要約'))
      expect(title.value).toBe('送信するタイトル')
      expect(client.sent).toHaveLength(3)
      await render(client, undefined, props)
      expect(client.sent).toHaveLength(3)

      clickButtonIn(titleForm, 'この入力を取り消して最新版から編集')
      expect(title.value).toBe('ホスト側の新しいタイトル')
      expect(title.readOnly).toBe(false)
      expect(summary.value).toBe('  自分の原文\n\n')
      expect(summary.readOnly).toBe(true)
      setText(title, '新基準で再入力')
      submit(titleForm)
      expect(client.sent).toHaveLength(4)
      expect(client.sent[3]).toMatchObject({
        method: 'analysis.updateTopic', params: {
          projectId: analysisProjectId,
          revisionId: refreshed.revisionId,
          digest: refreshed.revisionDigest,
          expectedTopicRevisionId: refreshed.topics[0]!.metadataRevisionId,
          update: { field: 'title', text: '新基準で再入力' },
          reason: '保持する理由',
        },
      })
      expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
    },
  )

  it('retains removed-topic inputs separately without using another topic as their evidence', async () => {
    const client = new FakeAnalysisClient()
    const onDirtyChange = vi.fn()
    const snapshot = snapshotFixture(true)
    await render(client, undefined, { context: { projectId: analysisProjectId }, onDirtyChange })
    emitSnapshot(client, 0, snapshot)
    setText(requiredElement<HTMLTextAreaElement>('textarea', formContaining('要約', topicCard(analysisTopicId))), '消えたトピックの自分の原文')
    setText(requiredElement<HTMLInputElement>('input', formContaining('タイトル', topicCard(secondTopicId))), '残るトピックの入力')
    clickButton('最新版を読み込む')
    emitSnapshot(client, 1, { ...snapshot, topics: snapshot.topics.slice(1) })

    expect(container.querySelectorAll('.analysis-topic-card')).toHaveLength(1)
    const retained = requiredElement<HTMLElement>('.analysis-retained-inputs')
    const ownInput = requiredElement<HTMLTextAreaElement>('textarea', retained)
    expect(ownInput.value).toBe('消えたトピックの自分の原文')
    expect(ownInput.readOnly).toBe(true)
    expect(retained.querySelector('form, select')).toBeNull()
    expect(retained.textContent).not.toContain('クラウド運用')
    expect(retained.textContent).not.toContain('第二のトピック')
    expect(retained.textContent).not.toContain('運用上の判断を説明します。')
    expect(container.textContent).toContain('対応する解析結果を表示できないため送信できません')
    clickButtonIn(retained, 'この入力を取り消す')
    expect(container.querySelector('.analysis-retained-inputs')).toBeNull()
    expect(requiredElement<HTMLInputElement>('input', formContaining('タイトル')).value).toBe('残るトピックの入力')
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
    clickButton('未送信の入力を取り消す')
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
    expect(client.sent.map((request) => request.method)).toEqual(['analysis.read', 'analysis.read'])
  })

  it.each(['contextual', 'project provider'] as const)(
    'reports dirty transitions through the %s consumer without looping on an inline parent callback',
    async (consumer) => {
      const client = new FakeAnalysisClient()
      const notifications: boolean[] = []
      function Parent({ active = true, readOnly = false }: { active?: boolean; readOnly?: boolean }) {
        const [count, setCount] = useState(0)
        const props = {
          active, readOnly, createClient: client.createClient,
          onDirtyChange: (dirty: boolean) => {
            notifications.push(dirty)
            setCount((value) => value + 1)
          },
        }
        return (
          <>
            <output data-dirty-notifications>{count}</output>
            {consumer === 'contextual' ? (
              <AnalysisPanel context={{ projectId: analysisProjectId }} {...props} />
            ) : (
              <ProjectStateProvider createClient={() => { throw new Error('Project host not used') }}>
                <AnalysisPanel projectId={analysisProjectId} {...props} />
              </ProjectStateProvider>
            )}
          </>
        )
      }
      await act(async () => root.render(<Parent />))
      expect(notifications).toEqual([false])
      const snapshot = snapshotFixture()
      emitSnapshot(client, 0, snapshot)
      act(() => otherTab.focus())
      const reason = requiredElement<HTMLInputElement>('.analysis-reason input')
      const title = requiredElement<HTMLInputElement>('input', formContaining('タイトル'))
      setText(reason, '  ')
      expect(notifications).toEqual([false, true])
      setText(reason, '')
      expect(notifications).toEqual([false, true, false])
      setText(title, '送信するタイトル')
      setText(reason, '送信理由')
      submit(formContaining('タイトル'))
      expect(notifications).toEqual([false, true, false, true])
      expect(container.textContent).toContain('変更要求を送信中です')
      expect(buttonByText('未送信の入力を取り消す').disabled).toBe(true)
      clickButton('未送信の入力を取り消す')
      expect(reason.value).toBe('送信理由')
      const acknowledged = titleAcknowledged(snapshot)
      emitSnapshot(client, 1, acknowledged)

      expect(notifications).toEqual([false, true, false, true, false])
      expect(requiredElement('[data-dirty-notifications]').textContent).toBe('5')
      expect(reason.value).toBe('')
      expect(container.querySelectorAll('.analysis-staged-list li')).toHaveLength(1)
      expect(container.textContent).toContain('未永続化')
      expect(document.activeElement).toBe(otherTab)
      clickButton('最新版を読み込む')
      await act(async () => root.render(<Parent active={false} />))
      emitSnapshot(client, 2, acknowledged)
      expect(document.activeElement).toBe(otherTab)
      expect(notifications).toHaveLength(5)

      for (const props of [{ readOnly: true }, { active: false }, {}]) {
        await act(async () => root.render(<Parent {...props} />))
        if (props.readOnly === true || props.active === false) {
          expect(reason.disabled).toBe(true)
          setText(reason, '禁止中の理由')
          setText(title, '禁止中のタイトル')
          submit(formContaining('タイトル'))
          expect(reason.value).toBe('')
          expect(title.value).toBe('送信するタイトル')
        }
        expect(document.activeElement).toBe(otherTab)
        expect(notifications).toHaveLength(5)
      }
      expect(client.sent.map((request) => request.method)).toEqual([
        'analysis.read', 'analysis.updateTopic', 'analysis.read',
      ])
      expect(client.listenerCount).toBe(1)
      expect(client.disposed).toBe(false)
    },
  )

  it('reports current dirty state when the optional callback is attached after local editing', async () => {
    const client = await renderAvailable()
    const title = requiredElement<HTMLInputElement>('input', formContaining('タイトル'))
    setText(title, 'callback 接続前の入力')
    const notifications = vi.fn()
    await render(client, undefined, { onDirtyChange: (dirty) => notifications(dirty) })
    await render(client, undefined, { onDirtyChange: (dirty) => notifications(dirty) })
    expect(notifications.mock.calls).toEqual([[true]])
    setText(title, 'クラウド運用')
    expect(notifications.mock.calls).toEqual([[true], [false]])
    expect(client.sent).toHaveLength(1)
  })

  it('captures inputs before a synchronous native ACK without retaining acknowledged staging as dirty', async () => {
    const client = new FakeAnalysisClient()
    const onDirtyChange = vi.fn()
    const snapshot = snapshotFixture()
    await render(client, undefined, { onDirtyChange })
    emitSnapshot(client, 0, snapshot)
    setText(requiredElement<HTMLInputElement>('input', formContaining('タイトル')), '送信するタイトル')
    setText(requiredElement<HTMLInputElement>('.analysis-reason input'), '送信理由')
    client.onSend = (request) => {
      if (request.method !== 'analysis.updateTopic') return
      emitSnapshot(client, client.sent.length - 1, titleAcknowledged(snapshot))
    }
    submit(formContaining('タイトル'))

    expect(client.sent).toHaveLength(2)
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
    expect(container.querySelector('[data-input-status]')).toBeNull()
    expect(requiredElement<HTMLInputElement>('.analysis-reason input').value).toBe('')
    expect(container.querySelectorAll('.analysis-staged-list li')).toHaveLength(1)
  })

  it('blocks a second submit, edits and discard even before a pending send is committed to the DOM', async () => {
    const client = new FakeAnalysisClient()
    const onDirtyChange = vi.fn()
    await render(client, undefined, { onDirtyChange })
    emitRead(client)
    const titleForm = formContaining('タイトル')
    const title = requiredElement<HTMLInputElement>('input', titleForm)
    const reason = requiredElement<HTMLInputElement>('.analysis-reason input')
    setText(title, '送信するタイトル')
    setText(reason, '送信理由')
    const discard = buttonByText('未送信の入力を取り消す')
    act(() => {
      titleForm.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
      titleForm.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
      discard.click()
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(reason, '送信中に変更しない')
      reason.dispatchEvent(new Event('input', { bubbles: true }))
    })
    expect(client.sent).toHaveLength(2)
    expect(reason.value).toBe('送信理由')
    expect(title.value).toBe('送信するタイトル')
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
    emitStatus(client, 1, 'noChange')
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
  })

  it.each(['hidden', 'inert', 'aria-hidden'] as const)(
    'ignores programmatic inputs and local discard beneath a %s ancestor without focus or dirty notifications',
    async (attribute) => {
      const client = new FakeAnalysisClient()
      const onDirtyChange = vi.fn()
      await render(client, undefined, { onDirtyChange })
      emitRead(client)
      const reason = requiredElement<HTMLInputElement>('.analysis-reason input')
      const titleForm = formContaining('タイトル')
      const title = requiredElement<HTMLInputElement>('input', titleForm)
      setText(reason, '保持する理由')
      setText(title, '保持する入力')
      act(() => otherTab.focus())
      container.setAttribute(attribute, attribute === 'aria-hidden' ? 'true' : '')
      setText(reason, '非表示中の変更')
      setText(title, '非表示中の変更')
      submit(titleForm)
      clickButton('未送信の入力を取り消す')
      clickButton('この入力を取り消す')
      clickButton('最新版を読み込む')
      expect(reason.value).toBe('保持する理由')
      expect(title.value).toBe('保持する入力')
      expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
      expect(client.sent).toHaveLength(1)
      expect(document.activeElement).toBe(otherTab)
      container.removeAttribute(attribute)
      clickButton('未送信の入力を取り消す')
      expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
      expect(document.activeElement).toBe(otherTab)
    },
  )

  it('keeps raw legacy list fields separate from normalized noChange requests', async () => {
    const client = new FakeAnalysisClient()
    const onDirtyChange = vi.fn()
    await render(client, undefined, { onDirtyChange })
    emitRead(client)
    const keywordsForm = formContaining('キーワード')
    const keywords = requiredElement<HTMLTextAreaElement>('textarea', keywordsForm)
    const sourceIds = requiredElement<HTMLTextAreaElement>('textarea', formContaining('重要発言の元区間 ID'))
    const rawIds = ` ${analysisSourceSpanId} \n${analysisSourceSpanId}\n\n`
    setText(keywords, ' Azure \nAzure\n\n')
    setText(sourceIds, rawIds)
    setText(requiredElement<HTMLInputElement>('.analysis-reason input'), '送信理由')
    submit(keywordsForm)
    expect(client.sent[1]).toMatchObject({ params: { update: { field: 'keywords', values: ['Azure'] } } })
    emitStatus(client, 1, 'noChange')
    expect(keywords.value).toBe('Azure')
    expect(sourceIds.value).toBe(rawIds)
    expect(requiredElement<HTMLInputElement>('.analysis-reason input').value).toBe('')
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
    clickButton('未送信の入力を取り消す')
    expect(sourceIds.value).toBe(analysisSourceSpanId)
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
  })

  it.each(['採用', '却下'] as const)('clears only the sent reason after a candidate %s noChange', async (decision) => {
    const client = new FakeAnalysisClient()
    const onDirtyChange = vi.fn()
    await render(client, undefined, { onDirtyChange })
    emitRead(client)
    const summary = requiredElement<HTMLTextAreaElement>('textarea', formContaining('要約'))
    setText(summary, '採否とは別の入力')
    setText(requiredElement<HTMLInputElement>('.analysis-reason input'), '判断の理由')
    clickButtonIn(cardContaining('用語修正候補', '用語辞書'), decision)
    emitStatus(client, 1, 'noChange')
    expect(summary.value).toBe('採否とは別の入力')
    expect(summary.readOnly).toBe(false)
    expect(requiredElement<HTMLInputElement>('.analysis-reason input').value).toBe('')
    expect(onDirtyChange.mock.calls).toEqual([[false], [true]])
  })

  it('does not leak a previous project draft or accept cross-project evidence after a keyed switch', async () => {
    const first = new FakeAnalysisClient()
    const second = new FakeAnalysisClient()
    const onDirtyChange = vi.fn()
    await render(first, undefined, { context: { projectId: analysisProjectId }, onDirtyChange })
    emitRead(first)
    setText(requiredElement<HTMLInputElement>('input', formContaining('タイトル')), '前の案件だけの入力')
    setText(requiredElement<HTMLInputElement>('.analysis-reason input'), '前の案件だけの理由')
    submit(formContaining('タイトル'))
    await render(second, undefined, { context: { projectId: secondTopicId }, active: false, onDirtyChange })
    emitSnapshot(first, 1, titleAcknowledged(snapshotFixture()))
    expect(first.disposed).toBe(true)
    expect(first.listenerCount).toBe(0)
    expect(second.sent).toHaveLength(0)
    expect(container.querySelector('input, textarea, select')).toBeNull()
    expect(container.textContent).not.toContain('前の案件')
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
    await render(second, undefined, { context: { projectId: secondTopicId }, onDirtyChange })
    expect(second.sent[0]).toMatchObject({ method: 'analysis.read', params: { projectId: secondTopicId } })
    // A correlated response is still invalid if its snapshot belongs to the former project.
    emitSnapshot(second, 0, snapshotFixture())
    expect(requiredElement<HTMLElement>('.analysis-status').dataset.analysisStatus).toBe('invalid')
    expect(container.querySelector('.analysis-workspace')).toBeNull()
    expect(container.querySelector('input, textarea, select')).toBeNull()
    expect(onDirtyChange.mock.calls).toEqual([[false], [true], [false]])
  })

  it('cleans up its response listener and bridge client', async () => {
    const client = new FakeAnalysisClient()
    await render(client)
    expect(client.listenerCount).toBe(1)

    act(() => root.unmount())

    expect(client.listenerCount).toBe(0)
    expect(client.disposed).toBe(true)
    root = createRoot(container)
  })

  async function renderAvailable(): Promise<FakeAnalysisClient> {
    const client = new FakeAnalysisClient()
    await render(client)
    emitRead(client)
    return client
  }

  async function render(
    client: FakeAnalysisClient,
    sourceSeek?: PlayerSourceSeekHandler,
    props: Partial<AnalysisPanelViewProps> = {},
  ): Promise<void> {
    await act(async () => {
      root.render(
        <PlayerNavigationProvider>
          {sourceSeek === undefined ? null : <SeekRegistration handler={sourceSeek} />}
          <AnalysisPanelView
            projectId={analysisProjectId}
            createClient={client.createClient}
            {...props}
          />
        </PlayerNavigationProvider>,
      )
    })
  }

  function emitRead(client: FakeAnalysisClient, sentIndex = 0): void {
    const sent = client.sent[sentIndex]
    if (sent === undefined) throw new Error('Expected an analysis read request.')
    const value = createAnalysisResponseValue(
      'analysis.read',
      createAllProducerCandidates(),
    )
    value.requestId = sent.requestId
    client.emit(requireResponse(value))
  }

  function emitSnapshot(client: FakeAnalysisClient, sentIndex: number, snapshot: AnalysisSnapshot): void {
    const sent = client.sent[sentIndex]
    if (sent === undefined) throw new Error('Expected an analysis request.')
    const value = createAnalysisResponseValue(sent.method, createAllProducerCandidates())
    value.requestId = sent.requestId
    value.result.snapshot = snapshot
    client.emit(requireResponse(value))
  }

  function emitStatus(
    client: FakeAnalysisClient,
    sentIndex: number,
    status: 'staleBase' | 'conflict' | 'noChange' | 'unavailable' | 'forbidden' | 'invalid',
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

  function formContaining(text: string, parent: ParentNode = container): HTMLFormElement {
    const form = [...parent.querySelectorAll<HTMLFormElement>('.analysis-topic-field')]
      .find((candidate) => candidate.querySelector('label')?.textContent?.startsWith(text))
    if (form === undefined) throw new Error(`Expected topic form ${text}.`)
    return form
  }

  function topicCard(topicId: string): HTMLElement {
    return requiredElement<HTMLElement>(`.analysis-topic-card[data-topic-id="${topicId}"]`)
  }

  function buttonTexts(parent: ParentNode): string[] {
    return [...parent.querySelectorAll('button')].map((button) => button.textContent ?? '')
  }

  function setText(element: HTMLInputElement | HTMLTextAreaElement, value: string): void {
    act(() => {
      const prototype = element instanceof HTMLTextAreaElement
        ? HTMLTextAreaElement.prototype
        : HTMLInputElement.prototype
      const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set
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
  readonly createClient = (): AnalysisBridgeClient => this
  disposed = false
  failNextSend = false
  onSend: ((request: AnalysisRequest) => void) | undefined
  private readonly listeners = new Set<(response: AnalysisResponse) => void>()

  get listenerCount(): number {
    return this.listeners.size
  }

  sendAnalysis(request: AnalysisRequest): void {
    this.sent.push(request)
    if (this.failNextSend) {
      this.failNextSend = false
      throw new Error('Analysis send failed')
    }
    this.onSend?.(request)
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

const secondTopicId = '10000000-0000-4000-8000-000000000020'

function snapshotFixture(twoTopics = false): AnalysisSnapshot {
  const snapshot = createAnalysisResponseValue('analysis.read', createAllProducerCandidates()).result.snapshot
  if (snapshot === null) throw new Error('Expected a snapshot fixture.')
  return twoTopics ? {
    ...snapshot,
    topics: [...snapshot.topics, {
      ...snapshot.topics[0]!,
      topicId: secondTopicId,
      metadataId: '10000000-0000-4000-8000-000000000021',
      metadataRevisionId: '10000000-0000-4000-8000-000000000022',
      title: '第二のトピック',
    }],
  } : snapshot
}

function titleAcknowledged(snapshot: AnalysisSnapshot): AnalysisSnapshot {
  const topic = snapshot.topics.find((item) => item.topicId === analysisTopicId)
  if (topic === undefined) throw new Error('Expected the acknowledged topic.')
  const nextRevisionId = '10000000-0000-4000-8000-000000000030'
  return {
    ...snapshot,
    topics: snapshot.topics.map((item) => item.topicId === analysisTopicId ? {
      ...item,
      title: '送信するタイトル',
      titleOrigin: 'manualEditorial',
      metadataRevisionId: nextRevisionId,
      parentMetadataRevisionId: item.metadataRevisionId,
      changedField: 'title',
      reason: '送信理由',
    } : item),
    stagedChanges: [{
      kind: 'topicMetadataRevision', topicId: analysisTopicId,
      revisionId: nextRevisionId, parentRevisionId: topic.metadataRevisionId,
      field: 'title', persisted: false,
    }],
  }
}