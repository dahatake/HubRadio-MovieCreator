// @vitest-environment jsdom

import { act, StrictMode, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { PlayerNavigationProvider } from '../player'
import { initialPreviewModeState } from '../preview-modes/previewModeState'
import { LayoutPanel, LayoutPanelView, type LayoutPanelProps } from './LayoutPanel'
import { LayoutStateProvider } from './LayoutStateProvider'
import type { LayoutBridgeClient } from './layoutBridgeSession'
import {
  parseLayoutBridgeResponse,
  type LayoutBridgeRequest,
  type LayoutBridgeResponse,
  type LayoutSnapshot,
} from './layoutProtocol'
import {
  createLayoutFailureValue,
  createLayoutResponseValue,
  layoutCueId,
  layoutCueRevisionId,
  layoutProjectId,
  layoutSecondProjectId,
  setLayoutFixtureSubtitleText,
} from './layoutTestFixture'

describe('LayoutPanel workflow integration', () => {
  let container: HTMLDivElement
  let root: Root
  let client: FakeLayoutClient
  const createClient = (): LayoutBridgeClient => client

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
    vi.useFakeTimers({ toFake: ['queueMicrotask'] })
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    client = new FakeLayoutClient()
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    vi.useRealTimers()
    vi.restoreAllMocks()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
  })

  it('keeps the standalone ID form and explicit read when context is undefined', () => {
    renderPanel()
    flushAutoLoad()
    expect(client.sent).toHaveLength(0)
    setValue(element<HTMLInputElement>('#layout-project-id'), layoutProjectId)
    submit(element<HTMLFormElement>('.layout-load-form'))
    expect(request(0)).toMatchObject({
      method: 'layout.read',
      params: { projectId: layoutProjectId, evaluationTime: null },
    })
    completeAvailable(0)
    expect(container.querySelector('.layout-load-form')).not.toBeNull()
    expect(container.querySelector('.layout-workspace')).not.toBeNull()
  })

  it('uses the committed provider input and loads once per new project, not context object', () => {
    renderPanel({ context: { projectId: layoutProjectId } })
    expect(container.querySelector('.layout-load-form')).toBeNull()
    expect(container.querySelector('#layout-project-id')).toBeNull()
    expect(client.sent).toHaveLength(0)

    // First commit changes the provider input; only the next render can use
    // its zero-argument loadSnapshot closure with the correct project ID.
    act(() => vi.runAllTicks())
    expect(client.sent).toHaveLength(0)
    act(() => vi.runAllTicks())
    expect(request(0)).toMatchObject({
      method: 'layout.read',
      params: { projectId: layoutProjectId, evaluationTime: null },
    })
    completeAvailable(0)
    const workspace = element('.layout-workspace')

    renderPanel({ context: { projectId: layoutProjectId } })
    flushAutoLoad()
    expect(client.sent).toHaveLength(1)
    expect(element('.layout-workspace')).toBe(workspace)

    renderPanel({ context: { projectId: layoutSecondProjectId } })
    expect(element<HTMLElement>('.layout-workspace').hidden).toBe(true)
    flushAutoLoad()
    expect(client.sent).toHaveLength(2)
    expect(request(1).params.projectId).toBe(layoutSecondProjectId)
    completeAvailable(1)
    renderPanel({ context: { projectId: layoutSecondProjectId } })
    flushAutoLoad()
    expect(client.sent).toHaveLength(2)
    expect(client.subscribeCount).toBe(1)
    expect(client.disposeCount).toBe(0)
  })

  it('cancels StrictMode effect replay instead of sending an extra initial read', () => {
    renderPanel({ context: { projectId: layoutProjectId } }, true)
    flushAutoLoad()
    expect(client.sent).toHaveLength(1)
    expect(request(0).params.projectId).toBe(layoutProjectId)
    completeAvailable(0)
    renderPanel({ context: { projectId: layoutProjectId } }, true)
    flushAutoLoad()
    expect(client.sent).toHaveLength(1)
  })

  it('cancels queued reads for superseded or hidden targets', () => {
    renderPanel({ context: { projectId: layoutProjectId } })
    act(() => vi.runAllTicks())
    renderPanel({ context: { projectId: layoutSecondProjectId }, active: false })
    flushAutoLoad()
    expect(client.sent).toHaveLength(0)

    renderPanel({ context: { projectId: layoutSecondProjectId } })
    flushAutoLoad()
    expect(client.sent).toHaveLength(1)
    expect(request(0).params.projectId).toBe(layoutSecondProjectId)
  })

  it.each([null, '', 'invalid', '21112222-3333-4444-8555-66667777ABCD'])(
    'does not fall back to the standalone form or fabricate evidence for %s',
    (projectId) => {
      renderPanel({ context: { projectId } })
      flushAutoLoad()
      expect(client.sent).toHaveLength(0)
      expect(container.querySelector('.layout-load-form')).toBeNull()
      expect(container.querySelector('.layout-workspace')).toBeNull()
      expect(container.querySelector('svg')).toBeNull()
      expect(container.querySelector('input[name="subtitle-display-profile"]')).toBeNull()

      renderPanel({ context: { projectId: layoutProjectId } })
      flushAutoLoad()
      expect(client.sent).toHaveLength(1)
      expect(request(0).params.projectId).toBe(layoutProjectId)
    },
  )

  it.each(['unavailable', 'forbidden', 'notFound', 'conflict', 'invalid'] as const)(
    'keeps the native %s response closed, with no profiles, layouts or counts',
    (status) => {
      renderPanel({ context: { projectId: layoutProjectId } })
      flushAutoLoad()
      complete(createLayoutFailureValue(status, request(0).requestId))
      expect(element('.layout-panel').getAttribute('data-layout-status')).toBe(status)
      expect(container.querySelector('.layout-failure[role="alert"]')).not.toBeNull()
      expect(container.querySelector('.layout-workspace')).toBeNull()
      expect(container.querySelector('.layout-evidence-badge')).toBeNull()
      expect(container.querySelector('svg, .layout-preview-controls, .subtitle-editor')).toBeNull()
      if (status === 'unavailable') {
        expect(container.textContent).toContain('production layout provider は利用できません')
      }

      renderPanel({ context: { projectId: layoutProjectId }, active: false })
      flushAutoLoad()
      renderPanel({ context: { projectId: layoutProjectId }, active: true })
      flushAutoLoad()
      expect(client.sent).toHaveLength(1)
      act(() => button('最新版を再読み込み').click())
      expect(client.sent).toHaveLength(2)
      expect(request(1).params.projectId).toBe(layoutProjectId)
    },
  )

  it('keeps preview choices, manual controls and drafts mounted through inactive tabs', () => {
    const dirtyChanges: boolean[] = []
    const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
    loadAvailable({ onDirtyChange })
    const panel = element<HTMLElement>('.layout-panel')
    const workspace = element<HTMLElement>('.layout-workspace')
    const profile = element<HTMLInputElement>(
      `input[name="subtitle-display-profile"][value="${'1'.repeat(64)}"]`,
    )
    act(() => profile.click())
    expect(dirtyChanges).toEqual([false])

    const modes = container.querySelectorAll<HTMLButtonElement>('.manual-mode-grid [role="radio"]')
    act(() => modes[6]!.click())
    expect(container.querySelectorAll('.manual-mode-grid [role="radio"]')).toHaveLength(7)
    expect(element('.manual-mode-grid [role="radio"]:last-child')).toBe(modes[6])
    expect(modes[6]!.getAttribute('aria-checked')).toBe('true')
    const coordinate = element<HTMLInputElement>('#keyframe-0-0-destination-x')
    setValue(coordinate, '1/4')
    const text = element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea')
    setValue(text, 'タブを戻るまで保持する字幕')
    act(() => text.focus())
    const blur = vi.spyOn(text, 'blur')
    expect(dirtyChanges).toEqual([false, true])

    renderPanel({ context: { projectId: layoutProjectId }, active: false, onDirtyChange })
    flushAutoLoad()
    expect(element('.layout-panel')).toBe(panel)
    expect(panel.hidden).toBe(true)
    expect(panel.hasAttribute('inert')).toBe(true)
    expect(panel.style.display).toBe('none')
    expect(element('.layout-workspace')).toBe(workspace)
    expect(blur).toHaveBeenCalled()
    // jsdom does not implement all disabled/inert focus behavior. Assert the
    // blur call separately, then check no focus is stolen from an outside control.
    const outside = document.createElement('button')
    container.append(outside)
    outside.focus()
    act(() => modes[0]!.click())
    act(() => modes[0]!.dispatchEvent(new KeyboardEvent('keydown', {
      key: 'End', bubbles: true, cancelable: true,
    })))
    submit(element<HTMLFormElement>('.keyframe-form'))
    expect(client.sent).toHaveLength(1)

    renderPanel({
      context: { projectId: layoutProjectId }, active: true, onDirtyChange,
      previewModes: { ...initialPreviewModeState, mobilePreview: true },
    })
    flushAutoLoad()
    expect(element('.layout-workspace')).toBe(workspace)
    expect(element('#keyframe-0-0-destination-x')).toBe(coordinate)
    expect(coordinate.value).toBe('1/4')
    expect(element('.subtitle-editor-grid > section:first-child textarea')).toBe(text)
    expect(text.value).toBe('タブを戻るまで保持する字幕')
    expect(profile.checked).toBe(true)
    expect(modes[6]!.getAttribute('aria-checked')).toBe('true')
    expect(document.activeElement).toBe(outside)
    expect(dirtyChanges).toEqual([false, true])
    expect(client.sent).toHaveLength(1)
    expect(client.subscribeCount).toBe(1)
    expect(client.disposeCount).toBe(0)
    outside.remove()
  })

  it('does not initialize while inactive or focus a hidden native failure', () => {
    renderPanel({ context: { projectId: layoutProjectId }, active: false })
    flushAutoLoad()
    expect(client.sent).toHaveLength(0)
    renderPanel({ context: { projectId: layoutProjectId } })
    flushAutoLoad()
    expect(client.sent).toHaveLength(1)
    renderPanel({ context: { projectId: layoutProjectId }, active: false })

    const outside = document.createElement('button')
    container.append(outside)
    outside.focus()
    complete(createLayoutFailureValue('unavailable', request(0).requestId))
    expect(element('.layout-failure').getAttribute('role')).toBeNull()
    expect(element('.layout-operation-status').getAttribute('aria-live')).toBe('off')
    expect(document.activeElement).toBe(outside)
    renderPanel({ context: { projectId: layoutProjectId }, active: true })
    flushAutoLoad()
    expect(document.activeElement).toBe(outside)
    expect(client.sent).toHaveLength(1)
    outside.remove()
  })

  it.each([layoutSecondProjectId, null, 'invalid'])(
    'retains dirty drafts for target %s until return or explicit discard',
    (projectId) => {
      const dirtyChanges: boolean[] = []
      const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
      loadAvailable({ onDirtyChange })
      const workspace = element<HTMLElement>('.layout-workspace')
      const text = element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea')
      setValue(text, '別案件へ持ち込まない入力')

      renderPanel({ context: { projectId }, onDirtyChange })
      flushAutoLoad()
      expect(client.sent).toHaveLength(1)
      expect(element('.layout-workspace')).toBe(workspace)
      expect(workspace.hidden).toBe(true)
      expect(workspace.hasAttribute('inert')).toBe(true)
      expect(container.querySelector('.layout-target-guard')).not.toBeNull()
      submit(element<HTMLFormElement>('.keyframe-form'))
      expect(client.sent).toHaveLength(1)

      renderPanel({ context: { projectId: layoutProjectId }, onDirtyChange })
      flushAutoLoad()
      expect(container.querySelector('.layout-target-guard')).toBeNull()
      expect(element('.layout-workspace')).toBe(workspace)
      expect(text.value).toBe('別案件へ持ち込まない入力')
      expect(client.sent).toHaveLength(1)
      expect(button('最新版を再読み込み').disabled).toBe(true)

      renderPanel({ context: { projectId }, onDirtyChange })
      act(() => button('入力を破棄して切り替える').click())
      flushAutoLoad()
      expect(dirtyChanges).toEqual([false, true, false])
      expect(workspace.isConnected).toBe(false)
      if (projectId === layoutSecondProjectId) {
        expect(client.sent).toHaveLength(2)
        expect(request(1).params.projectId).toBe(layoutSecondProjectId)
      } else {
        expect(client.sent).toHaveLength(1)
        expect(container.querySelector('.layout-workspace')).toBeNull()
      }
    },
  )

  it('waits for a pending read and requires confirmation even after its late response', () => {
    renderPanel({ context: { projectId: layoutProjectId } })
    flushAutoLoad()
    renderPanel({ context: { projectId: layoutSecondProjectId } })
    flushAutoLoad()
    expect(button('入力を破棄して切り替える').disabled).toBe(true)
    expect(client.sent).toHaveLength(1)
    completeAvailable(0)
    flushAutoLoad()
    // The old read is drained but its superseded snapshot is never adopted.
    expect(container.querySelector('.layout-workspace')).toBeNull()
    expect(button('入力を破棄して切り替える').disabled).toBe(false)
    expect(client.sent).toHaveLength(1)
    act(() => button('入力を破棄して切り替える').click())
    flushAutoLoad()
    expect(client.sent).toHaveLength(2)
    expect(request(1).params.projectId).toBe(layoutSecondProjectId)
    expect(client.abandoned).toEqual([])
    expect(client.disposeCount).toBe(0)
  })

  it('keeps a pending mutation bound to the old project and does not silently switch on success', () => {
    loadAvailable()
    setValue(element<HTMLInputElement>('#keyframe-0-0-destination-x'), '1/4')
    submit(element<HTMLFormElement>('.keyframe-form'))
    expect(request(1)).toMatchObject({
      method: 'layout.apply',
      params: { projectId: layoutProjectId, expectedRevision: 3 },
    })
    renderPanel({ context: { projectId: layoutSecondProjectId } })
    expect(button('入力を破棄して切り替える').disabled).toBe(true)
    completeAvailable(1, 4)
    flushAutoLoad()
    expect(client.sent).toHaveLength(2)
    expect(element<HTMLElement>('.layout-workspace').hidden).toBe(true)
    expect(container.querySelector('.layout-target-guard')).not.toBeNull()
    act(() => button('入力を破棄して切り替える').click())
    flushAutoLoad()
    expect(client.sent).toHaveLength(3)
    expect(request(2).params.projectId).toBe(layoutSecondProjectId)
  })

  it('acknowledges only the actually submitted editor on noChange, keeping other dirty inputs', () => {
    const dirtyChanges: boolean[] = []
    loadAvailable({ onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
    const workspace = element('.layout-workspace')
    setValue(element<HTMLInputElement>('#keyframe-0-0-destination-x'), '1/4')
    const text = element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea')
    setValue(text, '未送信の字幕を保持')
    setValue(element<HTMLInputElement>('input[name="subtitle-reason"]'), '字幕の変更理由')
    act(() => {
      // Two submissions before React commits pending must not overwrite which
      // editor was sent; the bridge session itself is also single-flight.
      element<HTMLFormElement>('.keyframe-form')
        .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
      element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form')
        .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    })
    expect(client.sent).toHaveLength(2)
    expect(request(1)).toMatchObject({ params: { operation: { kind: 'setKeyframes' } } })
    complete(createLayoutFailureValue('noChange', request(1).requestId, 'layout.apply'))
    submit(element<HTMLFormElement>('.keyframe-form'))
    complete(createLayoutFailureValue('noChange', request(2).requestId, 'layout.apply'))
    expect(element('.layout-workspace')).toBe(workspace)
    expect(text.value).toBe('未送信の字幕を保持')
    expect(dirtyChanges).toEqual([false, true])
    expect(button('最新版を再読み込み').disabled).toBe(true)
    act(() => button('未適用の入力を取り消す').click())
    expect(dirtyChanges).toEqual([false, true, false])
    expect(button('最新版を再読み込み').disabled).toBe(false)
  })

  it('keeps every unsubmitted editor dirty and mounted after an available keyframe ACK', () => {
    const dirtyChanges: boolean[] = []
    loadAvailable({ onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
    const workspace = element('.layout-workspace')
    const text = element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea')
    const reason = element<HTMLInputElement>('input[name="subtitle-reason"]')
    const terop = element<HTMLTextAreaElement>('.subtitle-editor-grid > section:last-child textarea')
    const manual = element<HTMLInputElement>('#manual-source-0-subject-x')
    const keyframe = element<HTMLInputElement>('#keyframe-0-0-destination-x')
    const profile = element<HTMLInputElement>(
      `input[name="subtitle-display-profile"][value="${'1'.repeat(64)}"]`,
    )
    act(() => profile.click())
    setValue(text, '未送信の字幕を保持')
    setValue(reason, '未送信の変更理由')
    setValue(terop, '未送信のテロップを保持')
    setValue(manual, '1/2')
    expect(manual.value).toBe('1/2')
    setValue(keyframe, '1/4')
    submit(element<HTMLFormElement>('.keyframe-form'))
    expect(element('#manual-source-0-subject-x')).toBe(manual)
    expect(manual.value).toBe('1/2')
    expect(request(1)).toMatchObject({
      method: 'layout.apply',
      params: { expectedRevision: 3, operation: { kind: 'setKeyframes' } },
    })

    complete(withAppliedKeyframe(availableValue(1, 4)))

    expect(element('.layout-workspace')).toBe(workspace)
    expect(element('.subtitle-editor-grid > section:first-child textarea')).toBe(text)
    expect(text.value).toBe('未送信の字幕を保持')
    expect(reason.value).toBe('未送信の変更理由')
    expect(element('.subtitle-editor-grid > section:last-child textarea')).toBe(terop)
    expect(terop.value).toBe('未送信のテロップを保持')
    expect(element('#manual-source-0-subject-x')).toBe(manual)
    expect(manual.value).toBe('1/2')
    expect(element('#keyframe-0-0-destination-x')).not.toBe(keyframe)
    expect(element<HTMLInputElement>('#keyframe-0-0-destination-x').value).toBe('1/4')
    expect(element('[data-keyframe-slot="singleSubject"]').getAttribute('x')).toBe('405')
    expect(profile.checked).toBe(true)
    expect(container.textContent).toContain('revision 4・未永続化')
    expect(container.querySelector('.layout-draft-guard')).toBeNull()
    expect(element('.layout-panel').getAttribute('data-layout-dirty')).toBe('true')
    expect(dirtyChanges).toEqual([false, true])
    expect(button('最新版を再読み込み').disabled).toBe(true)
    expect(client.sent).toHaveLength(2)

    act(() => button('未適用の入力を取り消す').click())
    expect(dirtyChanges).toEqual([false, true, false])
    expect(element<HTMLInputElement>('#keyframe-0-0-destination-x').value).toBe('1/4')
    expect(element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea').value)
      .toBe('字幕テキスト、です。')
  })

  it('keeps an unsent keyframe draft after a subtitle available ACK and submits it at the new revision', () => {
    const dirtyChanges: boolean[] = []
    loadAvailable({ onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
    const workspace = element('.layout-workspace')
    const keyframe = element<HTMLInputElement>('#keyframe-0-0-destination-x')
    const text = element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea')
    setValue(keyframe, '1/4')
    setValue(text, '適用済みの字幕')
    setValue(element<HTMLInputElement>('input[name="subtitle-reason"]'), '字幕を修正')
    submit(element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form'))
    expect(request(1)).toMatchObject({
      params: { expectedRevision: 3, operation: { kind: 'editSubtitle', edit: { text: '適用済みの字幕' } } },
    })
    const value = availableValue(1, 4)
    setCueRevision(value, 'subtitle', '適用済みの字幕')
    complete(value)

    expect(element('.layout-workspace')).toBe(workspace)
    expect(element('#keyframe-0-0-destination-x')).toBe(keyframe)
    expect(keyframe.value).toBe('1/4')
    expect(text.isConnected).toBe(false)
    expect(element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea').value)
      .toBe('適用済みの字幕')
    expect(element<HTMLInputElement>('input[name="subtitle-reason"]').value).toBe('')
    expect(element('.layout-preview-subtitle-text').textContent).toBe('適用済みの字幕')
    expect(container.querySelector('.layout-draft-guard')).toBeNull()
    expect(dirtyChanges).toEqual([false, true])
    expect(button('最新版を再読み込み').disabled).toBe(true)

    submit(element<HTMLFormElement>('.keyframe-form'))
    expect(request(2)).toMatchObject({
      params: {
        expectedRevision: 4,
        operation: {
          kind: 'setKeyframes',
          segments: [{ keyframes: [{ destination: { x: { numerator: '1', denominator: '4' } } }, {}] }],
        },
      },
    })
    const next = availableValue(2, 5)
    setCueRevision(next, 'subtitle', '適用済みの字幕')
    complete(withAppliedKeyframe(next))
    expect(dirtyChanges).toEqual([false, true, false])
    expect(element('.layout-panel').getAttribute('data-layout-dirty')).toBe('false')
    expect(button('最新版を再読み込み').disabled).toBe(false)

    // The acknowledged editor must now use native revision 5 as its baseline,
    // not latch its old draft or restore revision 3 on the next edit.
    const acknowledgedKeyframe = element<HTMLInputElement>('#keyframe-0-0-destination-x')
    expect(acknowledgedKeyframe).not.toBe(keyframe)
    expect(acknowledgedKeyframe.value).toBe('1/4')
    setValue(element<HTMLInputElement>('#keyframe-0-1-destination-x'), '1/3')
    submit(element<HTMLFormElement>('.keyframe-form'))
    expect(request(3)).toMatchObject({
      params: {
        expectedRevision: 5,
        operation: {
          kind: 'setKeyframes',
          segments: [{ keyframes: [
            { destination: { x: { numerator: '1', denominator: '4' } } },
            { destination: { x: { numerator: '1', denominator: '3' } } },
          ] }],
        },
      },
    })
    expect(dirtyChanges).toEqual([false, true, false, true])
  })

  it.each(['subtitle', 'terop'] as const)(
    'does not remount the other Cue editor on an available %s ACK',
    (submitted) => {
      const dirtyChanges: boolean[] = []
      loadAvailable({ onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
      const subtitle = element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea')
      const terop = element<HTMLTextAreaElement>('.subtitle-editor-grid > section:last-child textarea')
      setValue(subtitle, '字幕の入力')
      setValue(terop, 'テロップの入力')
      setValue(element<HTMLInputElement>('input[name="subtitle-reason"]'), '字幕の理由')
      setValue(element<HTMLInputElement>('input[name="terop-reason"]'), 'テロップの理由')
      const submittedSection = submitted === 'subtitle' ? 'first-child' : 'last-child'
      const otherSection = submitted === 'subtitle' ? 'last-child' : 'first-child'
      const other = submitted === 'subtitle' ? terop : subtitle
      const otherText = other.value
      submit(element<HTMLFormElement>(`.subtitle-editor-grid > section:${submittedSection} .cue-edit-form`))
      const value = availableValue(1, 4)
      setCueRevision(value, submitted, submitted === 'subtitle' ? '字幕の入力' : 'テロップの入力')
      complete(value)

      expect(element(`.subtitle-editor-grid > section:${otherSection} textarea`)).toBe(other)
      expect(other.value).toBe(otherText)
      expect(element<HTMLInputElement>(`input[name="${submitted}-reason"]`).value).toBe('')
      expect(container.querySelector('.layout-draft-guard')).toBeNull()
      expect(dirtyChanges).toEqual([false, true])
      expect(button('最新版を再読み込み').disabled).toBe(true)
    },
  )

  it.each(['cue revision', 'composition version', 'removed cue'] as const)(
    'retains a subtitle draft but refuses a stale save after an available change to %s',
    (change) => {
      const dirtyChanges: boolean[] = []
      const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
      loadAvailable({ onDirtyChange })
      const workspace = element('.layout-workspace')
      const text = element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea')
      const form = element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form')
      setValue(text, '破棄を確認するまで残す字幕')
      setValue(element<HTMLInputElement>('input[name="subtitle-reason"]'), '元の版で入力した理由')
      setValue(element<HTMLInputElement>('#keyframe-0-0-destination-x'), '1/4')
      submit(element<HTMLFormElement>('.keyframe-form'))
      const value = availableValue(1, 4)
      if (change === 'cue revision') {
        setCueRevision(value, 'subtitle', 'native で更新された字幕')
      } else if (change === 'composition version') {
        const compositionId = '32112222-3333-4444-8555-666677778888'
        value.result.snapshot.compositionVersionId = compositionId
        value.result.snapshot.variantPair.compositionVersionId = compositionId
        value.result.snapshot.subtitles[0]!.compositionVersionId = compositionId
        value.result.snapshot.terops[0]!.compositionVersionId = compositionId
      }
      const updated = withAppliedKeyframe(value)
      complete(change === 'removed cue' ? {
        ...updated,
        result: {
          ...updated.result,
          snapshot: { ...updated.result.snapshot, subtitles: [], subtitleDisplays: [], variantPair: null },
        },
      } : updated)

      expect(element('.layout-workspace')).toBe(workspace)
      expect(element('.subtitle-editor-grid > section:first-child textarea')).toBe(text)
      expect(text.value).toBe('破棄を確認するまで残す字幕')
      expect(element('.layout-draft-guard[role="alert"]').getAttribute('data-layout-needs-reload'))
        .toBe('subtitle')
      expect(container.textContent).toContain('編集開始時 revision 3 → 現在 revision 4')
      expect(container.textContent).toContain('この取消操作はすべての未適用の入力を破棄します')
      if (change === 'cue revision') {
        expect(element('.layout-preview-subtitle-text').textContent).toBe('native で更新された字幕')
      } else if (change === 'removed cue') {
        expect(container.querySelector('[data-subtitle-cue]')).toBeNull()
      }
      if (change !== 'cue revision') {
        expect(element('.layout-draft-guard').querySelector('button')).toBeNull()
      }
      setValue(text, '更新後も控えられるドラフト')
      submit(form)
      expect(client.sent).toHaveLength(2)
      expect(text.value).toBe('更新後も控えられるドラフト')
      expect(dirtyChanges).toEqual([false, true])

      renderPanel({ context: { projectId: layoutProjectId }, active: false, onDirtyChange })
      expect(element('.layout-draft-guard').getAttribute('role')).toBeNull()
      renderPanel({ context: { projectId: layoutProjectId }, onDirtyChange })
      expect(element('.subtitle-editor-grid > section:first-child textarea')).toBe(text)
      act(() => button('未適用の入力を取り消す').click())
      expect(container.querySelector('.layout-draft-guard')).toBeNull()
      expect(dirtyChanges).toEqual([false, true, false])
      expect(button('最新版を再読み込み').disabled).toBe(false)

      if (change === 'cue revision') {
        const current = element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea')
        expect(current.value).toBe('native で更新された字幕')
        setValue(current, '最新版を確認して編集')
        setValue(element<HTMLInputElement>('input[name="subtitle-reason"]'), '更新後の根拠を確認')
        submit(element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form'))
        expect(request(2)).toMatchObject({
          params: {
            expectedRevision: 4,
            operation: {
              kind: 'editSubtitle',
              expectedRevisionId: value.result.snapshot.subtitles[0]!.revisionId,
              expectedRevisionDigest: value.result.snapshot.subtitles[0]!.revisionDigest,
              edit: { kind: 'text', text: '最新版を確認して編集' },
            },
          },
        })
      }
    },
  )

  it.each(['changed track', 'removed track'] as const)(
    'retains keyframe inputs but blocks their stale base after a subtitle available ACK with %s',
    (change) => {
      const dirtyChanges: boolean[] = []
      loadAvailable({ onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
      const keyframe = element<HTMLInputElement>('#keyframe-0-0-destination-x')
      setValue(keyframe, '1/4')
      setValue(element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea'), '字幕を適用')
      setValue(element<HTMLInputElement>('input[name="subtitle-reason"]'), '字幕の理由')
      submit(element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form'))
      const value = availableValue(1, 4)
      setCueRevision(value, 'subtitle', '字幕を適用')
      const updated = withAppliedKeyframe(value)
      complete(change === 'removed track' ? {
        ...updated,
        result: { ...updated.result, snapshot: { ...updated.result.snapshot, keyframeTracks: [] } },
      } : updated)

      expect(element('#keyframe-0-0-destination-x')).toBe(keyframe)
      expect(keyframe.value).toBe('1/4')
      expect(element('.layout-draft-guard').getAttribute('data-layout-needs-reload')).toBe('keyframes')
      submit(element<HTMLFormElement>('.keyframe-form'))
      expect(client.sent).toHaveLength(2)
      expect(dirtyChanges).toEqual([false, true])
      act(() => button('未適用の入力を取り消す').click())
      expect(dirtyChanges).toEqual([false, true, false])
      expect(container.querySelector('.layout-draft-guard')).toBeNull()
      if (change === 'removed track') expect(container.querySelector('.keyframe-form')).toBeNull()
    },
  )

  it.each(['before pending commits', 'while pending'] as const)(
    'captures mutations of existing and future controls %s without acknowledging extra drafts',
    (timing) => {
      const dirtyChanges: boolean[] = []
      loadAvailable({ onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
      const workspace = element('.layout-workspace')
      const text = element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea')
      const reason = element<HTMLInputElement>('input[name="subtitle-reason"]')
      const keyframe = element<HTMLInputElement>('#keyframe-0-0-destination-x')
      const form = element<HTMLFormElement>('.keyframe-form')
      setValue(text, '送信前から未送信の字幕')
      setValue(reason, '送信前の理由')
      setValue(keyframe, '1/4')
      // No disabled fieldset or known form mapping: the outer capture guard
      // must protect a future control even before a pending render exists.
      const future = document.createElement('input')
      future.name = 'future-layout-control'
      const futureChange = vi.fn()
      future.addEventListener('input', futureChange)
      workspace.append(future)
      act(() => dispatchValue(future, '最初の入力'))
      expect(futureChange).toHaveBeenCalledTimes(1)
      const attemptChanges = (): void => {
        dispatchValue(text, 'pending 中の上書き')
        dispatchValue(reason, 'pending 中の理由')
        dispatchValue(keyframe, '1/3')
        dispatchValue(future, 'pending 中の新フィールド')
        element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form')
          .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
      }
      if (timing === 'before pending commits') {
        act(() => {
          form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
          attemptChanges()
        })
      } else {
        submit(form)
        act(attemptChanges)
      }
      expect(workspace.hasAttribute('inert')).toBe(true)
      expect(form.querySelector('fieldset')?.disabled).toBe(true)
      expect(future.disabled).toBe(false)
      expect(futureChange).toHaveBeenCalledTimes(1)
      expect(client.sent).toHaveLength(2)
      expect(request(1)).toMatchObject({
        params: {
          operation: {
            kind: 'setKeyframes',
            segments: [{ keyframes: [{ destination: { x: { numerator: '1', denominator: '4' } } }, {}] }],
          },
        },
      })

      complete(withAppliedKeyframe(availableValue(1, 4)))
      expect(element('.layout-workspace')).toBe(workspace)
      expect(element('.subtitle-editor-grid > section:first-child textarea')).toBe(text)
      expect(text.value).toBe('送信前から未送信の字幕')
      expect(reason.value).toBe('送信前の理由')
      expect(dirtyChanges).toEqual([false, true])
      expect(button('最新版を再読み込み').disabled).toBe(true)
      act(() => dispatchValue(future, '応答後の入力'))
      expect(futureChange).toHaveBeenCalledTimes(2)
      future.remove()
    },
  )

  it.each([
    { status: 'available', workflow: true },
    { status: 'noChange', workflow: true },
    { status: 'available', workflow: false },
    { status: 'noChange', workflow: false },
  ] as const)(
    'retains unsent text and reason after a $status style ACK (workflow=$workflow)',
    ({ status, workflow }) => {
      const dirtyChanges: boolean[] = []
      const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
      if (workflow) loadAvailable({ onDirtyChange })
      else {
        renderPanel({ onDirtyChange })
        setValue(element<HTMLInputElement>('#layout-project-id'), layoutProjectId)
        submit(element<HTMLFormElement>('.layout-load-form'))
        completeAvailable(0)
      }
      const workspace = element('.layout-workspace')
      const form = element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form')
      const reason = element<HTMLInputElement>('[name="subtitle-reason"]')
      const editKind = element<HTMLSelectElement>('[name="subtitle-edit-kind"]')
      setValue(element<HTMLTextAreaElement>('[name="subtitle-text"]'), 'まだ送信していない本文')
      setValue(reason, '本文にも必要な変更理由')
      setValue(editKind, 'style')
      const style = element<HTMLSelectElement>('[name="subtitle-style"]')
      const appliedStyle = status === 'available' ? 'emphasis' : 'normal'
      if (status === 'available') setValue(style, appliedStyle)
      submit(form)
      expect(request(1)).toMatchObject({
        params: {
          expectedRevision: 3,
          operation: {
            kind: 'editSubtitle', cueId: layoutCueId,
            expectedRevisionId: layoutCueRevisionId,
            edit: { kind: 'style', style: appliedStyle }, reason: '本文にも必要な変更理由',
          },
        },
      })
      expect(JSON.stringify(request(1))).not.toContain('まだ送信していない本文')
      const acknowledged = availableValue(1, 4)
      if (status === 'available') {
        setCueRevision(acknowledged, 'subtitle', acknowledged.result.snapshot.subtitles[0]!.text)
        acknowledged.result.snapshot.subtitles[0]!.style = appliedStyle
        complete(acknowledged)
      } else complete(createLayoutFailureValue('noChange', request(1).requestId, 'layout.apply'))

      expect(element('.layout-workspace')).toBe(workspace)
      expect(element('.subtitle-editor-grid > section:first-child .cue-edit-form')).toBe(form)
      expect(element('[name="subtitle-edit-kind"]')).toBe(editKind)
      expect(editKind.value).toBe('style')
      expect(element('[name="subtitle-style"]')).toBe(style)
      expect(style.value).toBe(appliedStyle)
      expect(element('[name="subtitle-reason"]')).toBe(reason)
      expect(reason.value).toBe('本文にも必要な変更理由')
      expect(element('.layout-panel').getAttribute('data-layout-dirty')).toBe('true')
      expect(dirtyChanges).toEqual([false, true])
      flushAutoLoad()
      expect(client.sent).toHaveLength(2)

      setValue(editKind, 'text')
      const retainedText = element<HTMLTextAreaElement>('[name="subtitle-text"]')
      expect(retainedText.value).toBe('まだ送信していない本文')
      if (status === 'available') {
        submit(form)
        expect(client.sent).toHaveLength(2)
        expect(element('.layout-draft-guard').getAttribute('data-layout-needs-reload')).toBe('subtitle')
        expect(container.textContent).toContain('編集開始時 revision 3 → 現在 revision 4')
        const evidence = element<HTMLDetailsElement>('.layout-draft-guard details')
        expect(evidence.textContent).toContain(layoutCueRevisionId)
        expect(evidence.textContent).toContain(acknowledged.result.snapshot.subtitles[0]!.revisionId)
        act(() => element<HTMLElement>('.layout-draft-guard summary').click())
        expect(evidence.open).toBe(true)
        act(() => button('この Cue の最新版を確認して入力を引き継ぐ').click())
        expect(client.sent).toHaveLength(2)
        expect(element('[name="subtitle-text"]')).toBe(retainedText)
        expect(retainedText.value).toBe('まだ送信していない本文')
        expect(reason.value).toBe('本文にも必要な変更理由')
        expect(element('.layout-reviewed-draft').textContent).toContain('明示確認済み revision 4')
      } else {
        expect(container.querySelector('.layout-draft-guard')).toBeNull()
        expect(button('最新版を再読み込み').disabled).toBe(true)
      }
      submit(form)
      expect(request(2)).toMatchObject({
        params: {
          expectedRevision: status === 'available' ? 4 : 3,
          operation: {
            kind: 'editSubtitle', cueId: layoutCueId,
            expectedRevisionId: status === 'available'
              ? acknowledged.result.snapshot.subtitles[0]!.revisionId : layoutCueRevisionId,
            expectedRevisionDigest: status === 'available' ? '4'.repeat(64) : 'f'.repeat(64),
            edit: { kind: 'text', text: 'まだ送信していない本文' },
            reason: '本文にも必要な変更理由',
          },
        },
      })
      const completed = availableValue(2, 5)
      setCueRevision(completed, 'subtitle', 'まだ送信していない本文')
      completed.result.snapshot.subtitles[0]!.revisionId = '84112222-3333-4444-8555-666677778888'
      completed.result.snapshot.subtitles[0]!.revisionCount = 4
      completed.result.snapshot.subtitles[0]!.style = appliedStyle
      complete(completed)
      expect(dirtyChanges).toEqual([false, true, false])
      expect(element('.layout-panel').getAttribute('data-layout-dirty')).toBe('false')
      expect(element<HTMLInputElement>('[name="subtitle-reason"]').value).toBe('')
      expect(element<HTMLTextAreaElement>('[name="subtitle-text"]').value).toBe('まだ送信していない本文')
    },
  )

  it('keeps unsent timing, placement, terop and keyframe inputs after a subtitle style ACK', () => {
    const dirtyChanges: boolean[] = []
    loadAvailable({ onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
    const keyframe = element<HTMLInputElement>('#keyframe-0-0-destination-x')
    const terop = element<HTMLTextAreaElement>('[name="terop-text"]')
    const teropReason = element<HTMLInputElement>('[name="terop-reason"]')
    const form = element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form')
    const editKind = element<HTMLSelectElement>('[name="subtitle-edit-kind"]')
    setValue(keyframe, '1/4')
    setValue(terop, '未送信のテロップ')
    setValue(teropReason, 'テロップ専用の理由')
    setValue(editKind, 'timing')
    setValue(element<HTMLInputElement>('[name="subtitle-startValue"]'), '3')
    setValue(element<HTMLInputElement>('[name="subtitle-durationValue"]'), '5')
    setValue(editKind, 'placement')
    setValue(element<HTMLInputElement>('[name="subtitle-x"]'), '-1/10')
    setValue(editKind, 'style')
    setValue(element<HTMLSelectElement>('[name="subtitle-style"]'), 'quote')
    setValue(element<HTMLInputElement>('[name="subtitle-reason"]'), 'ほかの字幕入力にも必要な理由')
    submit(form)
    const value = availableValue(1, 4)
    setCueRevision(value, 'subtitle', value.result.snapshot.subtitles[0]!.text)
    value.result.snapshot.subtitles[0]!.style = 'quote'
    complete(value)

    expect(element('.subtitle-editor-grid > section:first-child .cue-edit-form')).toBe(form)
    expect(element('#keyframe-0-0-destination-x')).toBe(keyframe)
    expect(keyframe.value).toBe('1/4')
    expect(element('[name="terop-text"]')).toBe(terop)
    expect(terop.value).toBe('未送信のテロップ')
    expect(teropReason.value).toBe('テロップ専用の理由')
    expect(element('.layout-draft-guard').getAttribute('data-layout-needs-reload')).toBe('subtitle')
    setValue(editKind, 'timing')
    expect(element<HTMLInputElement>('[name="subtitle-startValue"]').value).toBe('3')
    expect(element<HTMLInputElement>('[name="subtitle-durationValue"]').value).toBe('5')
    submit(form)
    expect(client.sent).toHaveLength(2)
    setValue(editKind, 'placement')
    expect(element<HTMLInputElement>('[name="subtitle-x"]').value).toBe('-1/10')
    expect(element<HTMLInputElement>('[name="subtitle-reason"]').value).toBe('ほかの字幕入力にも必要な理由')
    submit(form)
    expect(client.sent).toHaveLength(2)
    expect(dirtyChanges).toEqual([false, true])

    // An unchanged keyframe base is independent of the stale subtitle Cue.
    submit(element<HTMLFormElement>('.keyframe-form'))
    expect(request(2)).toMatchObject({ params: { expectedRevision: 4, operation: { kind: 'setKeyframes' } } })
    expect(dirtyChanges).toEqual([false, true])
  })

  it('acknowledges only the submitted Cue and retains another Cue form through selection and ACK', () => {
    const dirtyChanges: boolean[] = []
    renderPanel({ context: { projectId: layoutProjectId }, onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
    flushAutoLoad()
    complete(withSecondSubtitle(availableValue(0, 3)))
    expect(container.querySelector(`form[data-layout-cue-id="${secondSubtitleId}"]`)).toBeNull()
    const firstForm = element<HTMLFormElement>(`form[data-layout-cue-id="${layoutCueId}"]`)
    const firstText = element<HTMLTextAreaElement>(`form[data-layout-cue-id="${layoutCueId}"] textarea`)
    const firstReason = element<HTMLInputElement>(`form[data-layout-cue-id="${layoutCueId}"] [name="subtitle-reason"]`)
    setValue(firstText, '一番目の未送信本文')
    setValue(firstReason, '一番目の理由')
    const options = container.querySelectorAll<HTMLButtonElement>('[aria-label="字幕 Cue 一覧"] [role="option"]')
    act(() => options[1]!.click())
    const secondForm = element<HTMLFormElement>(`form[data-layout-cue-id="${secondSubtitleId}"]`)
    setValue(element<HTMLTextAreaElement>(`form[data-layout-cue-id="${secondSubtitleId}"] textarea`), '二番目の適用本文')
    setValue(element<HTMLInputElement>(`form[data-layout-cue-id="${secondSubtitleId}"] [name="subtitle-reason"]`), '二番目の理由')
    submit(firstForm)
    expect(client.sent).toHaveLength(1)
    submit(secondForm)
    expect(request(1)).toMatchObject({ params: { operation: { cueId: secondSubtitleId, edit: { text: '二番目の適用本文' } } } })
    const value = withSecondSubtitle(availableValue(1, 4), '二番目の適用本文')
    value.result.snapshot.subtitles[1]!.revisionId = '84112222-3333-4444-8555-666677778888'
    value.result.snapshot.subtitles[1]!.revisionCount++
    complete(value)

    expect(firstText.isConnected).toBe(true)
    expect(firstText.value).toBe('一番目の未送信本文')
    expect(firstReason.value).toBe('一番目の理由')
    expect(firstForm.closest<HTMLElement>('.cue-editor-slot')?.hidden).toBe(true)
    expect(firstForm.closest('.cue-editor-slot')?.hasAttribute('inert')).toBe(true)
    expect(secondForm.isConnected).toBe(false)
    expect(element<HTMLInputElement>(`form[data-layout-cue-id="${secondSubtitleId}"] [name="subtitle-reason"]`).value).toBe('')
    expect(container.querySelector('.layout-draft-guard')).toBeNull()
    expect(dirtyChanges).toEqual([false, true])
    act(() => options[0]!.click())
    expect(element(`form[data-layout-cue-id="${layoutCueId}"]`)).toBe(firstForm)
    expect(element(`form[data-layout-cue-id="${layoutCueId}"] textarea`)).toBe(firstText)
    submit(firstForm)
    expect(request(2)).toMatchObject({
      params: {
        expectedRevision: 4,
        operation: { cueId: layoutCueId, expectedRevisionId: layoutCueRevisionId, edit: { text: '一番目の未送信本文' } },
      },
    })
  })

  it.each([
    { failure: 'conflict', workflow: true },
    { failure: 'invalid', workflow: true },
    { failure: 'bridgeUnavailable', workflow: true },
    { failure: 'conflict', workflow: false },
    { failure: 'invalid', workflow: false },
    { failure: 'bridgeUnavailable', workflow: false },
  ] as const)(
    'preserves partial style ACK drafts through a $failure refresh and explicit review (workflow=$workflow)',
    ({ failure, workflow }) => {
      const dirtyChanges: boolean[] = []
      const onDirtyChange = (dirty: boolean): void => { dirtyChanges.push(dirty) }
      if (workflow) loadAvailable({ onDirtyChange })
      else {
        renderPanel({ onDirtyChange })
        setValue(element<HTMLInputElement>('#layout-project-id'), layoutProjectId)
        submit(element<HTMLFormElement>('.layout-load-form'))
        completeAvailable(0)
      }
      const workspace = element('.layout-workspace')
      const form = element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form')
      const reason = element<HTMLInputElement>('[name="subtitle-reason"]')
      const editKind = element<HTMLSelectElement>('[name="subtitle-edit-kind"]')
      setValue(element<HTMLTextAreaElement>('[name="subtitle-text"]'), '部分 ACK 後も未送信の本文')
      setValue(reason, '本文にも残す変更理由')
      setValue(editKind, 'style')
      setValue(element<HTMLSelectElement>('[name="subtitle-style"]'), 'emphasis')
      submit(form)
      expect(request(1)).toMatchObject({
        method: 'layout.apply',
        params: {
          expectedRevision: 3,
          operation: {
            kind: 'editSubtitle', cueId: layoutCueId, expectedRevisionId: layoutCueRevisionId,
            edit: { kind: 'style', style: 'emphasis' }, reason: '本文にも残す変更理由',
          },
        },
      })
      expect(JSON.stringify(request(1))).not.toContain('部分 ACK 後も未送信の本文')
      const acknowledged = availableValue(1, 4)
      const acknowledgedCue = acknowledged.result.snapshot.subtitles[0]!
      setCueRevision(acknowledged, 'subtitle', acknowledgedCue.text)
      acknowledgedCue.style = 'emphasis'
      complete(acknowledged)
      expect(element('.layout-panel').getAttribute('data-layout-status')).toBe('available')
      expect(element('.layout-draft-guard').getAttribute('data-layout-needs-reload')).toBe('subtitle')
      setValue(editKind, 'text')
      const text = element<HTMLTextAreaElement>('[name="subtitle-text"]')
      const keyframeForm = element<HTMLFormElement>('.keyframe-form')
      const keyframe = element<HTMLInputElement>('#keyframe-0-0-destination-x')
      const expectRetainedInputs = (): void => {
        expect(element('.layout-workspace')).toBe(workspace)
        expect(element('.subtitle-editor-grid > section:first-child .cue-edit-form')).toBe(form)
        expect(element('.keyframe-form')).toBe(keyframeForm)
        expect(element('#keyframe-0-0-destination-x')).toBe(keyframe)
        expect(keyframe.value).toBe('0/1')
        expect(element('[name="subtitle-text"]')).toBe(text)
        expect(text.value).toBe('部分 ACK 後も未送信の本文')
        expect(element('[name="subtitle-reason"]')).toBe(reason)
        expect(reason.value).toBe('本文にも残す変更理由')
        expect(element('[name="subtitle-edit-kind"]')).toBe(editKind)
        expect(editKind.value).toBe('text')
        expect(element('.layout-panel').getAttribute('data-layout-dirty')).toBe('true')
        expect(dirtyChanges).toEqual([false, true])
      }
      expectRetainedInputs()
      expect(button('最新版を再読み込み').disabled).toBe(false)

      if (failure === 'bridgeUnavailable') {
        vi.spyOn(client, 'sendLayout').mockImplementationOnce((attempt) => {
          client.sent.push(attempt)
          throw new Error('Simulated local transport failure.')
        })
      }
      act(() => button('最新版を再読み込み').click())
      expect(request(2)).toMatchObject({ method: 'layout.read', params: { projectId: layoutProjectId } })
      if (failure !== 'bridgeUnavailable') {
        complete(createLayoutFailureValue(failure, request(2).requestId))
      } else {
        expect(client.abandoned).toEqual([request(2).requestId])
        expect(container.querySelector('.layout-error')).not.toBeNull()
        act(() => element<HTMLButtonElement>('.layout-error button').click())
        expect(container.querySelector('.layout-error')).toBeNull()
      }
      const recoveryStatus = failure === 'bridgeUnavailable' ? 'invalid' : failure
      expectRetainedInputs()
      expect(element('.layout-panel').getAttribute('data-layout-status')).toBe(recoveryStatus)
      expect(container.textContent).toContain('revision 4・旧根拠（再確認が必要）・未永続化')
      expect(container.querySelector('.layout-draft-guard details')).toBeNull()
      submit(form)
      // An otherwise unchanged sibling must also stay gated until fresh evidence.
      submit(keyframeForm)
      flushAutoLoad()
      expect(client.sent).toHaveLength(3)
      expect(button('最新版を再読み込み').disabled).toBe(false)

      act(() => button('最新版を再読み込み').click())
      expect(request(3)).toMatchObject({ method: 'layout.read', params: { projectId: layoutProjectId } })
      const foreign = availableValue(3, 4)
      foreign.result.snapshot.projectId = layoutSecondProjectId
      for (const value of [
        createLayoutFailureValue('forbidden', request(2).requestId),
        createLayoutFailureValue('notFound', request(3).requestId, 'layout.apply'),
        foreign,
      ]) {
        complete(value)
        expect(element('.layout-operation-status').getAttribute('data-layout-pending')).toBe('true')
        expectRetainedInputs()
      }
      // Exact correlation completes the read, but noChange is not fresh evidence.
      complete(createLayoutFailureValue('noChange', request(3).requestId))
      expect(element('.layout-operation-status').getAttribute('data-layout-pending')).toBe('false')
      expect(element('.layout-panel').getAttribute('data-layout-status')).toBe(recoveryStatus)
      expect(container.querySelector('.layout-draft-guard details')).toBeNull()
      expectRetainedInputs()
      submit(form)
      submit(keyframeForm)
      expect(client.sent).toHaveLength(4)
      expect(button('最新版を再読み込み').disabled).toBe(false)

      act(() => button('最新版を再読み込み').click())
      expect(request(4)).toMatchObject({ method: 'layout.read', params: { projectId: layoutProjectId } })
      // Freshly re-read the exact ACK context, still changed from the dirty form's revision 3.
      complete({ ...acknowledged, requestId: request(4).requestId, method: request(4).method })
      expectRetainedInputs()
      expect(element('.layout-panel').getAttribute('data-layout-status')).toBe('available')
      expect(container.textContent).not.toContain('旧根拠（再確認が必要）')
      expect(element('.layout-draft-guard').getAttribute('data-layout-needs-reload')).toBe('subtitle')
      expect(container.textContent).toContain('編集開始時 revision 3 → 現在 revision 4')
      submit(form)
      expect(client.sent).toHaveLength(5)
      act(() => element<HTMLElement>('.layout-draft-guard summary').click())
      act(() => button('この Cue の最新版を確認して入力を引き継ぐ').click())
      expectRetainedInputs()
      expect(client.sent).toHaveLength(5)
      expect(element('.layout-reviewed-draft').textContent).toContain('明示確認済み revision 4')
      submit(form)
      expect(request(5)).toMatchObject({
        method: 'layout.apply',
        params: {
          projectId: layoutProjectId, expectedRevision: 4,
          operation: {
            kind: 'editSubtitle', cueId: layoutCueId,
            expectedRevisionId: acknowledgedCue.revisionId,
            expectedRevisionDigest: acknowledgedCue.revisionDigest,
            edit: { kind: 'text', text: '部分 ACK 後も未送信の本文' }, reason: '本文にも残す変更理由',
          },
        },
      })
      const completed = availableValue(5, 5)
      setCueRevision(completed, 'subtitle', '部分 ACK 後も未送信の本文')
      completed.result.snapshot.subtitles[0]!.revisionId = '84112222-3333-4444-8555-666677778888'
      completed.result.snapshot.subtitles[0]!.revisionDigest = '5'.repeat(64)
      completed.result.snapshot.subtitles[0]!.revisionCount = 4
      completed.result.snapshot.subtitles[0]!.style = 'emphasis'
      complete(completed)
      expect(dirtyChanges).toEqual([false, true, false])
      expect(element('.layout-panel').getAttribute('data-layout-dirty')).toBe('false')
    },
  )

  it.each(['forbidden', 'notFound', 'unavailable'] as const)(
    'hides partial style ACK evidence when the explicit read reports %s',
    (status) => {
      loadAvailable()
      const form = element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form')
      setValue(element<HTMLTextAreaElement>('[name="subtitle-text"]'), '再表示してはいけない未送信本文')
      setValue(element<HTMLInputElement>('[name="subtitle-reason"]'), '未送信の理由')
      setValue(element<HTMLSelectElement>('[name="subtitle-edit-kind"]'), 'style')
      setValue(element<HTMLSelectElement>('[name="subtitle-style"]'), 'emphasis')
      submit(form)
      const acknowledged = availableValue(1, 4)
      setCueRevision(acknowledged, 'subtitle', acknowledged.result.snapshot.subtitles[0]!.text)
      acknowledged.result.snapshot.subtitles[0]!.style = 'emphasis'
      complete(acknowledged)
      expect(element('.subtitle-editor-grid > section:first-child .cue-edit-form')).toBe(form)
      expect(button('最新版を再読み込み').disabled).toBe(false)
      act(() => button('最新版を再読み込み').click())
      expect(request(2).method).toBe('layout.read')
      complete(createLayoutFailureValue(status, request(2).requestId))
      expect(form.isConnected).toBe(false)
      expect(container.querySelector('.layout-workspace, .layout-draft-guard, .layout-reviewed-draft')).toBeNull()
      expect(container.querySelector('.layout-evidence-badge, svg, .layout-preview-controls')).toBeNull()
      expect(container.textContent).not.toContain('再表示してはいけない未送信本文')
      expect(container.textContent).not.toContain(layoutCueId)
      expect(element('.layout-panel').getAttribute('data-layout-status')).toBe(status)
      expect(client.sent).toHaveLength(3)
    },
  )

  it.each(['conflict', 'invalid'] as const)(
    'keeps the same forms and blocks stale writes after %s until an explicit refresh and retry',
    (status) => {
      const dirtyChanges: boolean[] = []
      loadAvailable({ onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
      const workspace = element('.layout-workspace')
      const keyframeForm = element<HTMLFormElement>('.keyframe-form')
      const subtitleForm = element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form')
      const keyframe = element<HTMLInputElement>('#keyframe-0-0-destination-x')
      const manual = element<HTMLInputElement>('#manual-source-0-subject-x')
      const text = element<HTMLTextAreaElement>('[name="subtitle-text"]')
      const reason = element<HTMLInputElement>('[name="subtitle-reason"]')
      const terop = element<HTMLTextAreaElement>('[name="terop-text"]')
      setValue(keyframe, '1/4')
      setValue(manual, '1/2')
      setValue(text, '失敗しても残す本文')
      setValue(reason, '失敗しても残す理由')
      setValue(terop, '失敗しても残すテロップ')
      submit(keyframeForm)
      complete(createLayoutFailureValue(status, request(1).requestId, 'layout.apply'))
      expect(element('.layout-workspace')).toBe(workspace)
      expect(element('.keyframe-form')).toBe(keyframeForm)
      expect(element('.subtitle-editor-grid > section:first-child .cue-edit-form')).toBe(subtitleForm)
      expect(element('#keyframe-0-0-destination-x')).toBe(keyframe)
      expect(keyframe.value).toBe('1/4')
      expect(element('#manual-source-0-subject-x')).toBe(manual)
      expect(manual.value).toBe('1/2')
      expect(element('[name="subtitle-text"]')).toBe(text)
      expect(text.value).toBe('失敗しても残す本文')
      expect(reason.value).toBe('失敗しても残す理由')
      expect(element('[name="terop-text"]')).toBe(terop)
      expect(terop.value).toBe('失敗しても残すテロップ')
      expect(container.textContent).toContain('revision 3・旧根拠（再確認が必要）・未永続化')
      expect(container.textContent).toContain('現在の revision は未確認')
      expect(dirtyChanges).toEqual([false, true])
      setValue(text, '失敗後も編集できる本文')
      submit(keyframeForm)
      submit(subtitleForm)
      submit(element<HTMLFormElement>('.manual-layout-form'))
      flushAutoLoad()
      expect(client.sent).toHaveLength(2)
      expect(button('最新版を再読み込み').disabled).toBe(false)

      act(() => button('最新版を再読み込み').click())
      expect(request(2)).toMatchObject({ method: 'layout.read', params: { projectId: layoutProjectId } })
      expect(element('.layout-workspace')).toBe(workspace)
      complete(createLayoutFailureValue(status, request(2).requestId))
      expect(element('.keyframe-form')).toBe(keyframeForm)
      expect(text.value).toBe('失敗後も編集できる本文')
      submit(keyframeForm)
      expect(client.sent).toHaveLength(3)
      act(() => button('最新版を再読み込み').click())
      completeAvailable(3, 4)
      expect(element('.layout-workspace')).toBe(workspace)
      expect(element('.keyframe-form')).toBe(keyframeForm)
      expect(element('[name="subtitle-text"]')).toBe(text)
      expect(text.value).toBe('失敗後も編集できる本文')
      expect(container.querySelector('.layout-draft-guard')).toBeNull()
      expect(client.sent).toHaveLength(4)
      submit(keyframeForm)
      expect(request(4)).toMatchObject({
        method: 'layout.apply',
        params: { expectedRevision: 4, operation: { kind: 'setKeyframes' } },
      })
      const failedRequest = request(1)
      if (failedRequest.method !== 'layout.apply') throw new Error('Expected the rejected apply.')
      expect(request(4).params).toMatchObject({ operation: failedRequest.params.operation })
      complete(withAppliedKeyframe(availableValue(4, 5)))
      expect(element('[name="subtitle-text"]')).toBe(text)
      expect(text.value).toBe('失敗後も編集できる本文')
      expect(dirtyChanges).toEqual([false, true])
    },
  )

  it.each(['conflict', 'invalid'] as const)(
    'requires explicit Cue review after refreshing a %s apply with changed evidence',
    (status) => {
      const dirtyChanges: boolean[] = []
      loadAvailable({ onDirtyChange: (dirty) => { dirtyChanges.push(dirty) } })
      const form = element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form')
      const text = element<HTMLTextAreaElement>('[name="subtitle-text"]')
      const reason = element<HTMLInputElement>('[name="subtitle-reason"]')
      setValue(text, '旧基準からの未送信本文')
      setValue(reason, '旧基準で入力した理由')
      submit(form)
      complete(createLayoutFailureValue(status, request(1).requestId, 'layout.apply'))
      act(() => button('最新版を再読み込み').click())
      const latest = availableValue(2, 4)
      setCueRevision(latest, 'subtitle', 'native 側の新しい本文')
      complete(latest)
      expect(element('.subtitle-editor-grid > section:first-child .cue-edit-form')).toBe(form)
      expect(element('[name="subtitle-text"]')).toBe(text)
      expect(text.value).toBe('旧基準からの未送信本文')
      expect(reason.value).toBe('旧基準で入力した理由')
      expect(element('.layout-preview-subtitle-text').textContent).toBe('native 側の新しい本文')
      expect(container.textContent).toContain('編集開始時 revision 3 → 現在 revision 4')
      submit(form)
      expect(client.sent).toHaveLength(3)
      act(() => element<HTMLElement>('.layout-draft-guard summary').click())
      act(() => button('この Cue の最新版を確認して入力を引き継ぐ').click())
      expect(client.sent).toHaveLength(3)
      expect(element('[name="subtitle-text"]')).toBe(text)
      expect(reason.value).toBe('旧基準で入力した理由')
      submit(form)
      expect(request(3)).toMatchObject({
        params: {
          expectedRevision: 4,
          operation: {
            expectedRevisionId: latest.result.snapshot.subtitles[0]!.revisionId,
            expectedRevisionDigest: latest.result.snapshot.subtitles[0]!.revisionDigest,
            edit: { kind: 'text', text: '旧基準からの未送信本文' }, reason: '旧基準で入力した理由',
          },
        },
      })
      expect(dirtyChanges).toEqual([false, true])
    },
  )

  it.each(['conflict', 'invalid'] as const)(
    'does not unlock a %s apply when an explicit refresh returns noChange',
    (status) => {
      loadAvailable()
      const form = element<HTMLFormElement>('.keyframe-form')
      const input = element<HTMLInputElement>('#keyframe-0-0-destination-x')
      setValue(input, '1/4')
      submit(form)
      complete(createLayoutFailureValue(status, request(1).requestId, 'layout.apply'))
      act(() => button('最新版を再読み込み').click())
      complete(createLayoutFailureValue('noChange', request(2).requestId))
      expect(element('.keyframe-form')).toBe(form)
      expect(element('#keyframe-0-0-destination-x')).toBe(input)
      expect(input.value).toBe('1/4')
      expect(element('.layout-panel').getAttribute('data-layout-status')).toBe(status)
      expect(element('.layout-panel').getAttribute('data-layout-dirty')).toBe('true')
      submit(form)
      expect(client.sent).toHaveLength(3)
      expect(button('最新版を再読み込み').disabled).toBe(false)
    },
  )

  it.each(['forbidden', 'notFound', 'unavailable'] as const)(
    'hides even previously reviewed Cue evidence after an apply returns %s',
    (status) => {
      loadAvailable()
      const form = element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form')
      const editKind = element<HTMLSelectElement>('[name="subtitle-edit-kind"]')
      setValue(element<HTMLTextAreaElement>('[name="subtitle-text"]'), 'まだ送らない本文')
      setValue(element<HTMLInputElement>('[name="subtitle-reason"]'), '保持する理由')
      setValue(editKind, 'style')
      setValue(element<HTMLSelectElement>('[name="subtitle-style"]'), 'emphasis')
      submit(form)
      const value = availableValue(1, 4)
      setCueRevision(value, 'subtitle', value.result.snapshot.subtitles[0]!.text)
      value.result.snapshot.subtitles[0]!.style = 'emphasis'
      complete(value)
      act(() => element<HTMLElement>('.layout-draft-guard summary').click())
      act(() => button('この Cue の最新版を確認して入力を引き継ぐ').click())
      expect(container.querySelector('.layout-reviewed-draft')).not.toBeNull()
      setValue(editKind, 'text')
      submit(form)
      complete(createLayoutFailureValue(status, request(2).requestId, 'layout.apply'))
      expect(container.querySelector('.layout-workspace, .layout-draft-guard, .layout-reviewed-draft')).toBeNull()
      expect(container.querySelector('.layout-evidence-badge')).toBeNull()
      expect(container.textContent).not.toContain('まだ送らない本文')
      expect(container.textContent).not.toContain(layoutCueId)
      expect(element('.layout-panel').getAttribute('data-layout-status')).toBe(status)
    },
  )

  it.each([layoutSecondProjectId, null, 'invalid'])(
    'does not expose recovery evidence or permit refresh for target %s',
    (projectId) => {
      loadAvailable()
      const workspace = element<HTMLElement>('.layout-workspace')
      const text = element<HTMLTextAreaElement>('[name="subtitle-text"]')
      setValue(text, '元案件だけの本文')
      setValue(element<HTMLInputElement>('[name="subtitle-reason"]'), '元案件だけの理由')
      submit(element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form'))
      complete(createLayoutFailureValue('conflict', request(1).requestId, 'layout.apply'))
      renderPanel({ context: { projectId } })
      flushAutoLoad()
      expect(element('.layout-workspace')).toBe(workspace)
      expect(workspace.hidden).toBe(true)
      expect(workspace.hasAttribute('inert')).toBe(true)
      expect(container.querySelector('.layout-heading .layout-evidence-badge')).toBeNull()
      expect(container.querySelector('.layout-draft-guard, .layout-reviewed-draft, .layout-failure')).toBeNull()
      expect(element('.layout-panel').getAttribute('data-layout-status')).toBe('idle')
      if (projectId === layoutSecondProjectId) {
        expect(button('最新版を再読み込み').disabled).toBe(true)
        act(() => button('最新版を再読み込み').click())
      }
      submit(element<HTMLFormElement>('.subtitle-editor-grid > section:first-child .cue-edit-form'))
      expect(client.sent).toHaveLength(2)
      renderPanel({ context: { projectId: layoutProjectId } })
      expect(element('[name="subtitle-text"]')).toBe(text)
      expect(text.value).toBe('元案件だけの本文')
      expect(element('.layout-draft-guard')).not.toBeNull()
    },
  )

  it('retains standalone reinitialization when a controlled snapshot is replaced', () => {
    const response = parseLayoutBridgeResponse(createLayoutResponseValue())
    if (response === null || response.result.status !== 'available') throw new Error('Expected snapshot.')
    const actions = {
      changeProjectId: vi.fn(), loadSnapshot: vi.fn(), refreshSnapshot: vi.fn(),
      applyOperation: vi.fn(), clearError: vi.fn(),
    }
    const renderSnapshot = (snapshot: LayoutSnapshot): void => {
      act(() => root.render(
        <PlayerNavigationProvider>
          <LayoutPanelView
            {...actions}
            state={{
              projectIdInput: snapshot.projectId, snapshot, status: 'available',
              pending: null, error: null, lastCompletedMethod: 'layout.read',
            }}
          />
        </PlayerNavigationProvider>,
      ))
    }
    renderSnapshot(response.result.snapshot)
    const workspace = element('.layout-workspace')
    setValue(element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea'), '旧入力')
    setValue(element<HTMLInputElement>('#keyframe-0-0-destination-x'), '1/4')
    renderSnapshot({ ...response.result.snapshot, revision: 4 })
    expect(element('.layout-workspace')).not.toBe(workspace)
    expect(element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea').value)
      .toBe(response.result.snapshot.subtitles[0]!.text)
    expect(element<HTMLInputElement>('#keyframe-0-0-destination-x').value).toBe('0/1')
    expect(actions.applyOperation).not.toHaveBeenCalled()
  })

  it.each(['unavailable', 'forbidden', 'notFound'] as const)(
    'closes previously loaded evidence when the native mutation is %s',
    (status) => {
      loadAvailable()
      submit(element<HTMLFormElement>('.keyframe-form'))
      complete(createLayoutFailureValue(status, request(1).requestId, 'layout.apply'))
      expect(element('.layout-panel').getAttribute('data-layout-status')).toBe(status)
      expect(container.querySelector('.layout-workspace')).toBeNull()
      expect(container.querySelector('svg, .layout-preview-controls, .subtitle-editor')).toBeNull()
      expect(container.querySelector('.layout-draft-guard, .layout-reviewed-draft')).toBeNull()
      renderPanel({ context: { projectId: layoutProjectId }, active: false })
      renderPanel({ context: { projectId: layoutProjectId }, active: true })
      flushAutoLoad()
      expect(client.sent).toHaveLength(2)
    },
  )

  it('does not invent a selected display profile when native variant evidence is absent', () => {
    renderPanel({ context: { projectId: layoutProjectId } })
    flushAutoLoad()
    const value = createLayoutResponseValue(request(0).requestId)
    complete({
      ...value,
      result: { ...value.result, snapshot: { ...value.result.snapshot, variantPair: null } },
    })
    const profiles = container.querySelectorAll<HTMLInputElement>('input[name="subtitle-display-profile"]')
    expect(profiles.length).toBe(value.result.snapshot.subtitleDisplays.length)
    expect(Array.from(profiles).every((profile) => !profile.checked)).toBe(true)
    expect(container.querySelector('[data-subtitle-cue]')).toBeNull()
    const frame = element('[data-safe-zone="output-frame-bounds"]')
    expect(['x', 'y', 'width', 'height'].map((attribute) => frame.getAttribute(attribute)))
      .toEqual(['0', '0', '1080', '1920'])
  })

  it('notifies dirty transitions through the latest callback without an inline-callback render loop', () => {
    const notifications: boolean[] = []
    function FeedbackPanel() {
      const [updates, setUpdates] = useState(0)
      return (
        <>
          <output data-feedback-updates={updates} />
          <button type="button" onClick={() => setUpdates((current) => current + 1)}>
            親を再描画
          </button>
          <LayoutPanel
            context={{ projectId: layoutProjectId }}
            onDirtyChange={(dirty) => {
              notifications.push(dirty)
              if (notifications.length > 5) throw new Error('Unexpected dirty callback loop.')
              setUpdates((current) => current + 1)
            }}
          />
        </>
      )
    }
    act(() => root.render(
      <PlayerNavigationProvider>
        <LayoutStateProvider createClient={createClient}>
          <FeedbackPanel />
        </LayoutStateProvider>
      </PlayerNavigationProvider>,
    ))
    flushAutoLoad()
    completeAvailable(0)
    expect(notifications).toEqual([false])
    setValue(element<HTMLTextAreaElement>('.subtitle-editor-grid > section:first-child textarea'), '変更')
    expect(notifications).toEqual([false, true])
    act(() => button('親を再描画').click())
    flushAutoLoad()
    expect(notifications).toEqual([false, true])
    expect(client.sent).toHaveLength(1)
    act(() => button('未適用の入力を取り消す').click())
    expect(notifications).toEqual([false, true, false])
    expect(element('[data-feedback-updates]').getAttribute('data-feedback-updates')).toBe('4')
  })

  it('uses a replacement dirty callback only on the next dirty transition', () => {
    const first = vi.fn<(dirty: boolean) => void>()
    const second = vi.fn<(dirty: boolean) => void>()
    loadAvailable({ onDirtyChange: first })
    expect(first.mock.calls).toEqual([[false]])
    renderPanel({ context: { projectId: layoutProjectId }, onDirtyChange: second })
    flushAutoLoad()
    expect(second).not.toHaveBeenCalled()

    const mode = element<HTMLButtonElement>('.manual-mode-grid [role="radio"]')
    act(() => mode.dispatchEvent(new KeyboardEvent('keydown', {
      key: 'End', bubbles: true, cancelable: true,
    })))
    expect(first.mock.calls).toEqual([[false]])
    expect(second.mock.calls).toEqual([[true]])
    act(() => button('未適用の入力を取り消す').click())
    expect(second.mock.calls).toEqual([[true], [false]])
    expect(client.sent).toHaveLength(1)
  })

  it('preserves native no-solution evidence and does not turn it into a resolved subtitle', () => {
    renderPanel({ context: { projectId: layoutProjectId } })
    flushAutoLoad()
    const value = createLayoutResponseValue(request(0).requestId)
    complete({
      ...value,
      result: {
        ...value.result,
        snapshot: {
          ...value.result.snapshot,
          subtitles: value.result.snapshot.subtitles.map((cue) => ({
            ...cue,
            layoutResolution: {
              ...cue.layoutResolution,
              status: 'manualAdjustmentRequired',
              selectedCandidateId: null,
              globalHardConstraints: ['noPlacementCandidate'],
              evaluations: cue.layoutResolution.evaluations.map((evaluation) => ({
                ...evaluation,
                hardConstraints: ['prohibitedRegionOverlap'],
              })),
            },
          })),
        },
      },
    })
    expect(container.textContent).toContain('自動配置の解がない字幕が 1 件あります。')
    expect(container.textContent).toContain('自動配置の解がありません')
    expect(container.textContent).toContain('禁止 region と重なり')
    expect(container.querySelector('[data-subtitle-cue]')).toBeNull()
    expect(container.querySelector('.manual-layout-form')).not.toBeNull()
    expect(container.querySelector('.cue-edit-form textarea')).not.toBeNull()
    const workspace = element('.layout-workspace')
    renderPanel({ context: { projectId: layoutProjectId }, active: false })
    renderPanel({ context: { projectId: layoutProjectId }, active: true })
    flushAutoLoad()
    expect(element('.layout-workspace')).toBe(workspace)
    expect(container.querySelector('[data-subtitle-cue]')).toBeNull()
    expect(client.sent).toHaveLength(1)
  })

  function renderPanel(props: LayoutPanelProps = {}, strict = false): void {
    const panel = (
      <PlayerNavigationProvider>
        <LayoutStateProvider createClient={createClient}>
          <LayoutPanel {...props} />
        </LayoutStateProvider>
      </PlayerNavigationProvider>
    )
    act(() => root.render(strict ? <StrictMode>{panel}</StrictMode> : panel))
  }

  function flushAutoLoad(): void {
    act(() => vi.runAllTicks())
    act(() => vi.runAllTicks())
  }

  function loadAvailable(props: LayoutPanelProps = {}): void {
    renderPanel({ ...props, context: { projectId: layoutProjectId } })
    flushAutoLoad()
    completeAvailable(0)
  }

  function completeAvailable(index: number, revision = 3): void {
    complete(availableValue(index, revision))
  }

  function availableValue(index: number, revision: number) {
    const sent = request(index)
    const value = createLayoutResponseValue(sent.requestId, sent.method)
    value.result.snapshot.projectId = sent.params.projectId
    value.result.snapshot.revision = revision
    return value
  }

  function complete(value: unknown): void {
    const response = parseLayoutBridgeResponse(value)
    if (response === null) throw new Error('Expected a valid native layout fixture.')
    act(() => client.emit(response))
  }

  function request(index: number): LayoutBridgeRequest {
    const sent = client.sent[index]
    if (sent === undefined) throw new Error(`Expected layout request ${index}.`)
    return sent
  }

  function element<T extends Element = HTMLElement>(selector: string): T {
    const found = container.querySelector<T>(selector)
    if (found === null) throw new Error(`Expected ${selector}.`)
    return found
  }

  function button(text: string): HTMLButtonElement {
    const found = Array.from(container.querySelectorAll('button'))
      .find((candidate) => candidate.textContent?.trim() === text)
    if (found === undefined) throw new Error(`Expected button ${text}.`)
    return found
  }
})

class FakeLayoutClient implements LayoutBridgeClient {
  readonly sent: LayoutBridgeRequest[] = []
  readonly abandoned: string[] = []
  subscribeCount = 0
  disposeCount = 0
  private listener: ((response: LayoutBridgeResponse) => void) | null = null

  sendLayout(request: LayoutBridgeRequest): void { this.sent.push(request) }
  abandonLayoutRequest(requestId: string): void { this.abandoned.push(requestId) }
  onLayoutResponse(listener: (response: LayoutBridgeResponse) => void): () => void {
    this.subscribeCount++
    this.listener = listener
    return () => { this.listener = null }
  }
  dispose(): void {
    this.disposeCount++
    this.listener = null
  }
  emit(response: LayoutBridgeResponse): void { this.listener?.(response) }
}

function setValue(element: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement, value: string): void {
  act(() => dispatchValue(element, value))
}

function dispatchValue(element: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement, value: string): void {
  const prototype = element instanceof HTMLTextAreaElement
    ? HTMLTextAreaElement.prototype
    : element instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype
  const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set
  if (setter === undefined) throw new Error('Expected native value setter.')
  setter.call(element, value)
  element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }))
}

function submit(form: HTMLFormElement): void {
  act(() => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
}

function setCueRevision(
  value: ReturnType<typeof createLayoutResponseValue>,
  kind: 'subtitle' | 'terop',
  text: string,
): void {
  const cue = kind === 'subtitle'
    ? value.result.snapshot.subtitles[0]! : value.result.snapshot.terops[0]!
  if (kind === 'subtitle') setLayoutFixtureSubtitleText(value, text)
  else cue.text = text
  cue.revisionId = kind === 'subtitle'
    ? '82112222-3333-4444-8555-666677778888' : 'b2112222-3333-4444-8555-666677778888'
  cue.revisionDigest = '4'.repeat(64)
  cue.revisionCount++
}

const secondSubtitleId = '72112222-3333-4444-8555-666677778888'

function withSecondSubtitle(value: ReturnType<typeof createLayoutResponseValue>, text = '二番目の字幕') {
  const snapshot = value.result.snapshot
  snapshot.subtitles.push({
    ...snapshot.subtitles[0]!, cueId: secondSubtitleId,
    revisionId: '83112222-3333-4444-8555-666677778888', text,
  })
  for (const display of snapshot.subtitleDisplays) {
    display.sourceCueCount = 2
    if (display.cueMode === 'full') {
      display.displayedCues.push({ cueId: secondSubtitleId, text, textChanged: false })
    }
  }
  return value
}

function withAppliedKeyframe(value: ReturnType<typeof createLayoutResponseValue>) {
  const snapshot = value.result.snapshot
  return {
    ...value,
    result: {
      ...value.result,
      snapshot: {
        ...snapshot,
        keyframeTracks: snapshot.keyframeTracks.map((track) => ({
          ...track,
          segments: track.segments.map((segment, segmentIndex) => ({
            ...segment,
            keyframes: segment.keyframes.map((keyframe, keyframeIndex) =>
              segmentIndex === 0 && keyframeIndex === 0 ? {
                ...keyframe,
                destination: { ...keyframe.destination, x: { numerator: '1', denominator: '4' } },
              } : keyframe),
          })),
          evaluation: {
            ...track.evaluation,
            destination: { ...track.evaluation.destination, x: { numerator: '3', denominator: '8' } },
            destinationPixels: { ...track.evaluation.destinationPixels, x: 405 },
          },
        })),
      },
    },
  }
}