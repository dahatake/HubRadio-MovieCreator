// @vitest-environment jsdom

import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { PlayerNavigationProvider } from '../player'
import { LayoutPanel, type LayoutPanelContext } from './LayoutPanel'
import { LayoutStateProvider, useLayoutState, type LayoutStateContextValue } from './LayoutStateProvider'
import type { LayoutBridgeClient } from './layoutBridgeSession'
import {
  parseLayoutBridgeResponse,
  type LayoutBridgeRequest,
  type LayoutBridgeResponse,
  type LayoutOperation,
  type LayoutSnapshot,
  type LayoutSubtitleCue,
  type LayoutTeropCue,
  type LayoutVersionContext,
} from './layoutProtocol'
import {
  createLayoutResponseValue,
  createSubtitleOnlyLayoutResponseValue,
  layoutCompositionDigest,
  layoutCompositionId,
  layoutProjectId,
} from './layoutTestFixture'

const first: LayoutVersionContext = {
  projectId: layoutProjectId, versionId: layoutCompositionId, versionDigest: layoutCompositionDigest,
}
const second: LayoutVersionContext = {
  ...first, versionId: '32112222-3333-4444-8555-666677778888', versionDigest: '9'.repeat(64),
}
const child: LayoutVersionContext = {
  ...first, versionId: '33112222-3333-4444-8555-666677778888', versionDigest: first.versionDigest,
}

describe('LayoutPanel and provider exact version context', () => {
  let container: HTMLDivElement
  let root: Root
  let client: FakeClient
  let api: LayoutStateContextValue | undefined
  const createClient = (): LayoutBridgeClient => client
  function Probe() { api = useLayoutState(); return null }

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    vi.useFakeTimers({ toFake: ['queueMicrotask'] })
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    client = new FakeClient()
    api = undefined
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    vi.useRealTimers()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT
  })

  it('exposes optional exact load/refresh targets without relying on an old project-input closure', () => {
    render(undefined, false)
    act(() => requiredApi().loadSnapshot(first))
    expect(request(0).params).toEqual({
      projectId: first.projectId, evaluationTime: null,
      expectedVersionId: first.versionId, expectedVersionDigest: first.versionDigest,
    })
    complete(readValue(0, first))
    expect(requiredApi().state.target).toEqual(first)
    act(() => requiredApi().refreshSnapshot())
    expect(request(1).params).toEqual(request(0).params)
    complete(readValue(1, first))
    act(() => requiredApi().refreshSnapshot(second))
    expect(request(2).params).toMatchObject({
      expectedVersionId: second.versionId, expectedVersionDigest: second.versionDigest,
    })
    expect(requiredApi().state.snapshot).toBeNull()
    complete(readValue(2, second))
    expect(requiredApi().state.target).toEqual(second)
    expect(requiredApi().state.snapshot?.compositionVersionId).toBe(second.versionId)
  })

  it('loads clean same-project version/digest changes once and keeps equivalent context objects stable', () => {
    render(first)
    flush()
    complete(readValue(0, first))
    const workspace = element('.layout-workspace')
    render({ ...first })
    flush()
    expect(client.sent).toHaveLength(1)
    expect(element('.layout-workspace')).toBe(workspace)

    render(second)
    flush()
    expect(client.sent).toHaveLength(2)
    expect(request(1).params).toMatchObject({
      expectedVersionId: second.versionId, expectedVersionDigest: second.versionDigest,
    })
    complete(readValue(1, second))
    const changedDigest = { ...second, versionDigest: '6'.repeat(64) }
    render(changedDigest)
    flush()
    expect(request(2).params).toMatchObject({ expectedVersionDigest: changedDigest.versionDigest })
    complete(readValue(2, changedDigest))
    render({ ...changedDigest })
    flush()
    expect(client.sent).toHaveLength(3)
  })

  it.each([
    { projectId: layoutProjectId, versionId: first.versionId },
    { projectId: layoutProjectId, versionDigest: first.versionDigest },
    { ...first, versionDigest: null },
    { ...first, versionId: 'invalid' },
    { ...first, versionDigest: 'A'.repeat(64) },
  ])('does not fall back to the default aggregate for incomplete/invalid version context %#', context => {
    render(context)
    flush()
    expect(client.sent).toHaveLength(0)
    expect(container.querySelector('.layout-workspace, .layout-load-form')).toBeNull()
    expect(container.textContent).toContain('版 ID と digest の両方')
  })

  it.each([second, { ...first, versionDigest: '6'.repeat(64) }])(
    'retains dirty form instances and inputs across a version/digest switch until explicit discard %#', target => {
      render(first)
      flush()
      complete(readValue(0, first))
      const workspace = element<HTMLElement>('.layout-workspace')
      const text = element<HTMLTextAreaElement>('[name="subtitle-text"]')
      const keyframe = element<HTMLInputElement>('#keyframe-0-0-destination-x')
      setValue(text, '未送信の本文と undo のフォームを保持')
      setValue(keyframe, '1/4')
      render(target)
      flush()
      expect(workspace.hidden).toBe(true)
      expect(element('[name="subtitle-text"]')).toBe(text)
      expect(element('#keyframe-0-0-destination-x')).toBe(keyframe)
      expect(client.sent).toHaveLength(1)
      render(first)
      flush()
      expect(workspace.hidden).toBe(false)
      expect(text.value).toBe('未送信の本文と undo のフォームを保持')
      expect(keyframe.value).toBe('1/4')
      expect(element('[name="subtitle-text"]')).toBe(text)
      render(target)
      act(() => button('入力を破棄して切り替える').click())
      flush()
      expect(text.isConnected).toBe(false)
      expect(request(1).params).toMatchObject({
        expectedVersionId: target.versionId, expectedVersionDigest: target.versionDigest,
      })
    },
  )

  it('drains a superseded read without adopting its snapshot, then rejects its late duplicate', () => {
    render(first)
    flush()
    render(second)
    expect(requiredApi().state.pending?.invalidated).toBe(true)
    complete(readValue(0, first))
    flush()
    expect(requiredApi().state.snapshot).toBeNull()
    expect(requiredApi().state.lastCompletedMethod).toBeNull()
    expect(client.sent).toHaveLength(1)
    act(() => button('入力を破棄して切り替える').click())
    flush()
    complete(readValue(0, first))
    expect(requiredApi().state.pending?.requestId).toBe(request(1).requestId)
    complete(readValue(1, second))
    expect(requiredApi().state.snapshot?.compositionVersionId).toBe(second.versionId)
  })

  it('does not ACK or replace a dirty form when a superseded mutation returns a child', () => {
    render(first)
    flush()
    complete(readValue(0, first))
    const base = requiredApi().state.snapshot
    const text = element<HTMLTextAreaElement>('[name="subtitle-text"]')
    setValue(text, '切替前の未確認本文')
    setValue(element<HTMLInputElement>('[name="subtitle-reason"]'), '本文修正')
    submit(element<HTMLFormElement>('form[data-layout-cue-id]'))
    render(second)
    expect(button('入力を破棄して切り替える').disabled).toBe(true)
    complete(childValue(1))
    expect(requiredApi().state.snapshot).toBe(base)
    expect(requiredApi().state.lastCompletedMethod).toBeNull()
    expect(requiredApi().state.status).toBe('invalid')
    expect(element('[name="subtitle-text"]')).toBe(text)
    expect(text.value).toBe('切替前の未確認本文')
    expect(element('.layout-panel').getAttribute('data-layout-dirty')).toBe('true')
    expect(client.sent).toHaveLength(2)
    render(first)
    expect(element('[name="subtitle-text"]')).toBe(text)
    submit(element<HTMLFormElement>('form[data-layout-cue-id]'))
    expect(client.sent).toHaveLength(2)
  })

  it('accepts an operation-bound child, retains the selected context and refreshes the child exactly', () => {
    render({ ...first, readMode: 'exact' })
    flush()
    complete(readValue(0, first))
    const text = element<HTMLTextAreaElement>('[name="subtitle-text"]')
    setValue(text, '子版の本文')
    setValue(element<HTMLInputElement>('[name="subtitle-reason"]'), '本文修正')
    submit(element<HTMLFormElement>('form[data-layout-cue-id]'))
    expect(request(1).params).toMatchObject({
      expectedVersionId: first.versionId, expectedVersionDigest: first.versionDigest,
    })
    const value = childValue(1)
    complete(value)
    flush()
    expect(requiredApi().state.target).toEqual(first)
    expect(requiredApi().state.snapshot?.compositionVersionId).toBe(child.versionId)
    expect(requiredApi().state.lastVersionChange).toEqual({ source: first, created: child })
    expect(element<HTMLElement>('.layout-workspace').hidden).toBe(false)
    expect(container.querySelector('.layout-target-guard')).toBeNull()
    expect(client.sent).toHaveLength(2)
    act(() => button('最新版を再読み込み').click())
    expect(request(2).params).toMatchObject({
      expectedVersionId: child.versionId, expectedVersionDigest: child.versionDigest,
    })
    complete({ ...value, requestId: request(2).requestId, method: 'layout.read' })
    expect(requiredApi().state.target).toEqual(first)
    const cue = requiredApi().state.snapshot!.subtitles[0]!
    act(() => requiredApi().applyOperation({
      kind: 'editSubtitle', cueId: cue.cueId, expectedRevisionId: cue.revisionId,
      expectedRevisionDigest: cue.revisionDigest, edit: { kind: 'style', style: 'quote' }, reason: 'style 修正',
    }))
    expect(request(3).params).toMatchObject({ expectedVersionId: child.versionId, expectedVersionDigest: child.versionDigest })
  })

  it.each(['editSubtitle', 'editTerop'] as const)('edits A -> B -> A from exact effective native snapshots (%s)', kind => {
    render(undefined, false)
    act(() => requiredApi().loadSnapshot(first))
    const parsed = parseLayoutBridgeResponse(readValue(0, first))
    if (parsed?.result.status !== 'available') throw new Error('Invalid base fixture.')
    const initial: LayoutSnapshot = {
      ...parsed.result.snapshot,
      variantPair: null,
      subtitleDisplays: [],
      subtitles: [...parsed.result.snapshot.subtitles, {
        ...parsed.result.snapshot.subtitles[0]!, cueId: '72112222-3333-4444-8555-666677778888',
        revisionId: '82112222-3333-4444-8555-666677778888', revisionDigest: '5'.repeat(64), text: '兄弟字幕',
      }],
      terops: [...parsed.result.snapshot.terops, {
        ...parsed.result.snapshot.terops[0]!, cueId: 'a2112222-3333-4444-8555-666677778888',
        revisionId: 'b2112222-3333-4444-8555-666677778888', revisionDigest: '4'.repeat(64), text: '兄弟テロップ',
      }],
    }
    complete({ ...parsed, result: { status: 'available', snapshot: initial } })
    const steps = [
      { index: 0, versionId: child.versionId, revisionId: '85112222-3333-4444-8555-666677778888', digest: '7'.repeat(64), style: 'quote' },
      { index: 1, versionId: '34112222-3333-4444-8555-666677778888', revisionId: '86112222-3333-4444-8555-666677778888', digest: '6'.repeat(64), style: 'caution' },
      { index: 0, versionId: '35112222-3333-4444-8555-666677778888', revisionId: '87112222-3333-4444-8555-666677778888', digest: '3'.repeat(64), style: 'emphasis' },
    ] as const
    for (const [index, step] of steps.entries()) {
      const before = requiredApi().state.snapshot
      if (before === null) throw new Error('Missing effective snapshot.')
      const cue = (kind === 'editSubtitle' ? before.subtitles : before.terops)[step.index]!
      if (index > 0) expect(cue.compositionVersionId).not.toBe(before.compositionVersionId)
      const operation: Extract<LayoutOperation, { kind: 'editSubtitle' | 'editTerop' }> = {
        kind, cueId: cue.cueId, expectedRevisionId: cue.revisionId, expectedRevisionDigest: cue.revisionDigest,
        edit: { kind: 'style', style: step.style }, reason: '受信済み cue のみを編集',
      }
      const staleApply = requiredApi().applyOperation
      act(() => requiredApi().applyOperation(operation))
      const sent = request(index + 1)
      expect(sent.params).toEqual({
        projectId: before.projectId, expectedRevision: before.revision, operation,
        expectedVersionId: before.compositionVersionId, expectedVersionDigest: before.compositionVersionDigest,
      })
      const revise = <T extends LayoutSubtitleCue | LayoutTeropCue>(item: T): T => item.cueId !== cue.cueId ? item : {
        ...item, compositionVersionId: step.versionId, revisionId: step.revisionId,
        revisionDigest: step.digest, revisionCount: item.revisionCount + 1, style: step.style,
      }
      const next: LayoutSnapshot = {
        ...before, revision: before.revision + 1, compositionVersionId: step.versionId,
        subtitles: kind === 'editSubtitle' ? before.subtitles.map(revise) : before.subtitles,
        terops: kind === 'editTerop' ? before.terops.map(revise) : before.terops,
      }
      complete({ version: sent.version, requestId: sent.requestId, method: sent.method,
        result: { status: 'available', snapshot: next } })
      expect(requiredApi().state.status).toBe('available')
      expect(requiredApi().state.snapshot).toEqual(next)
      expect(requiredApi().state.target).toEqual(first)
      expect(requiredApi().state.error).toBeNull()
      const count = client.sent.length
      // A retained callback knows the old cue but cannot submit its old head.
      act(() => staleApply(operation))
      expect(client.sent).toHaveLength(count)
      expect(requiredApi().state.error?.code).toBe('layout.invalid_request')
      // Neither a previous revision nor an unknown cue can borrow this head.
      for (const stale of [operation, { ...operation, expectedRevisionId: step.revisionId },
        { ...operation, expectedRevisionDigest: step.digest },
        { ...operation, cueId: '73112222-3333-4444-8555-666677778888',
          expectedRevisionId: step.revisionId, expectedRevisionDigest: step.digest }]) {
        act(() => requiredApi().applyOperation(stale))
        expect(client.sent).toHaveLength(count)
        expect(requiredApi().state.error?.code).toBe('layout.invalid_request')
        expect(requiredApi().state.snapshot).toEqual(next)
      }
    }
  })

  it('explicitly carries an unchanged sibling draft after another cue commits a child', () => {
    render({ ...first, readMode: 'exact' })
    flush()
    const parsed = parseLayoutBridgeResponse(readValue(0, first))
    if (parsed?.result.status !== 'available') throw new Error('Invalid initial fixture.')
    const a = parsed.result.snapshot.subtitles[0]!
    const b: LayoutSubtitleCue = { ...a,
      cueId: '72112222-3333-4444-8555-666677778888',
      revisionId: '82112222-3333-4444-8555-666677778888',
      revisionDigest: '5'.repeat(64), text: '兄弟字幕',
    }
    const initial: LayoutSnapshot = { ...parsed.result.snapshot, variantPair: null,
      subtitles: [a, b], subtitleDisplays: [] }
    complete({ ...parsed, result: { status: 'available', snapshot: initial } })
    const options = () => container.querySelectorAll<HTMLButtonElement>('[aria-label="字幕 Cue 一覧"] [role="option"]')
    act(() => options()[1]!.click())
    const siblingForm = element<HTMLFormElement>(`form[data-layout-cue-id="${b.cueId}"]`)
    const siblingText = siblingForm.querySelector<HTMLTextAreaElement>('[name="subtitle-text"]')!
    setValue(siblingText, 'まだ適用していない兄弟の本文')
    setValue(siblingForm.querySelector<HTMLInputElement>('[name="subtitle-reason"]')!, '兄弟の変更理由')
    act(() => options()[0]!.click())
    const firstForm = element<HTMLFormElement>(`form[data-layout-cue-id="${a.cueId}"]`)
    setValue(firstForm.querySelector<HTMLInputElement>('[name="subtitle-reason"]')!, 'Aのstyleだけ保存')
    setValue(firstForm.querySelector<HTMLSelectElement>('[name="subtitle-edit-kind"]')!, 'style')
    setValue(firstForm.querySelector<HTMLSelectElement>('[name="subtitle-style"]')!, 'quote')
    submit(firstForm)
    const sent = request(1)
    const next: LayoutSnapshot = { ...initial, revision: initial.revision + 1,
      compositionVersionId: child.versionId,
      subtitles: [{ ...a, compositionVersionId: child.versionId,
        revisionId: '85112222-3333-4444-8555-666677778888', revisionDigest: '7'.repeat(64),
        revisionCount: a.revisionCount + 1, style: 'quote' }, b] }
    complete({ version: sent.version, requestId: sent.requestId, method: sent.method,
      result: { status: 'available', snapshot: next } })
    act(() => options()[1]!.click())
    expect(element(`form[data-layout-cue-id="${b.cueId}"]`)).toBe(siblingForm)
    expect(siblingText.value).toBe('まだ適用していない兄弟の本文')
    submit(siblingForm)
    expect(client.sent).toHaveLength(2)
    act(() => button('この Cue の最新版を確認して入力を引き継ぐ').click())
    submit(siblingForm)
    expect(request(2).params).toMatchObject({ expectedVersionId: child.versionId,
      expectedVersionDigest: child.versionDigest, operation: { cueId: b.cueId,
        expectedRevisionId: b.revisionId, expectedRevisionDigest: b.revisionDigest,
        edit: { kind: 'text', text: 'まだ適用していない兄弟の本文' } } })
  })

  it('loads the saved head from the original root and refreshes that root after adopting the child', () => {
    render(first)
    flush()
    expect(request(0).params).toMatchObject({ expectedVersionId: first.versionId,
      expectedVersionDigest: first.versionDigest, latestInBranch: true })
    const saved = durableValue(0, 1)
    complete(saved)
    render({ ...child, root: first })
    flush()
    expect(requiredApi().state.target).toEqual(child)
    expect(requiredApi().state.branch).toEqual({ root: first, sequence: 1 })
    expect(container.textContent).toContain('字幕変更は保存済み／動画は変更後未生成')
    expect(client.sent).toHaveLength(1)
    act(() => button('最新版を再読み込み').click())
    expect(request(1).params).toEqual(request(0).params)
    complete({ ...saved, requestId: request(1).requestId })
    flush()
    expect(requiredApi().state.snapshot?.compositionVersionId).toBe(child.versionId)
    expect(client.sent).toHaveLength(2)
  })

  it('marks only committed subtitles saved while preserving other fields and their original binding across a latest refresh', () => {
    render(first)
    flush()
    complete(durableValue(0, 0))
    const form = element<HTMLFormElement>('form[data-layout-cue-id]')
    const text = element<HTMLTextAreaElement>('[name="subtitle-text"]')
    const keyframe = element<HTMLInputElement>('#keyframe-0-0-destination-x')
    setValue(text, '保存された style に含まれない本文')
    setValue(keyframe, '1/4')
    setValue(element<HTMLInputElement>('[name="subtitle-reason"]'), 'style のみ保存')
    setValue(element<HTMLSelectElement>('[name="subtitle-edit-kind"]'), 'style')
    setValue(element<HTMLSelectElement>('[name="subtitle-style"]'), 'quote')
    submit(form)
    expect(container.textContent).not.toContain('字幕変更は保存済み／動画は変更後未生成')
    complete(durableValue(1, 1, 'layout.apply'))
    expect(element('form[data-layout-cue-id]')).toBe(form)
    expect(element('#keyframe-0-0-destination-x')).toBe(keyframe)
    expect(keyframe.value).toBe('1/4')
    expect(container.textContent).toContain('字幕変更は保存済み／動画は変更後未生成')
    expect(container.textContent).toContain('手動レイアウト・Keyframe・テロップの変更はメモリ内のみ')
    expect(element('.layout-panel').getAttribute('data-layout-dirty')).toBe('true')
    setValue(element<HTMLSelectElement>('[name="subtitle-edit-kind"]'), 'text')
    // Changing edit kind intentionally remounts its conditional field. The
    // form/state survives; now keep this visible field through the head read.
    const retainedText = element<HTMLTextAreaElement>('[name="subtitle-text"]')
    expect(retainedText.value).toBe('保存された style に含まれない本文')
    retainedText.setSelectionRange(1, 5)
    act(() => button('最新版を再読み込み').click())
    expect(request(2).params).toMatchObject({ expectedVersionId: first.versionId, latestInBranch: true })
    complete(durableValue(2, 2))
    expect(element('form[data-layout-cue-id]')).toBe(form)
    expect(element('[name="subtitle-text"]')).toBe(retainedText)
    expect(retainedText.value).toBe('保存された style に含まれない本文')
    expect(retainedText.selectionStart).toBe(1)
    expect(retainedText.selectionEnd).toBe(5)
    expect(keyframe.value).toBe('1/4')
    expect(requiredApi().state.branch).toEqual({ root: first, sequence: 2 })
    submit(form)
    expect(client.sent).toHaveLength(3)
    expect(container.textContent).toContain('編集開始時 revision 0')
  })

  it.each(['digest', 'siblingText', 'siblingStyle', 'siblingTime', 'siblingVersion', 'siblingRevision', 'siblingOrigin'] as const)(
    'rejects a child ACK with changed %s and closes further edits', mismatch => {
      render(undefined, false)
      act(() => requiredApi().loadSnapshot(first))
      complete(readValue(0, first))
      const before = requiredApi().state.snapshot
      if (before === null) throw new Error('Missing effective snapshot.')
      const cue = before.subtitles[0]!
      const operation: LayoutOperation = {
        kind: 'editSubtitle', cueId: cue.cueId, expectedRevisionId: cue.revisionId, expectedRevisionDigest: cue.revisionDigest,
        edit: { kind: 'style', style: 'quote' }, reason: 'ACK を検証',
      }
      act(() => requiredApi().applyOperation(operation))
      const value = childValue(1)
      const snapshot = value.result.snapshot
      const sibling = snapshot.terops[0]!
      switch (mismatch) {
        case 'digest': snapshot.compositionVersionDigest = '8'.repeat(64); break
        case 'siblingText': sibling.text = '操作していない本文'; break
        case 'siblingStyle': sibling.style = 'quote'; break
        case 'siblingTime': sibling.outputRange.start.value = '3'; break
        case 'siblingVersion': sibling.compositionVersionId = child.versionId; break
        case 'siblingRevision': sibling.revisionDigest = '8'.repeat(64); break
        case 'siblingOrigin': sibling.sourceKind = 'adoptedSuggestion'; break
      }
      complete(value)
      expect(requiredApi().state.snapshot).toBe(before)
      expect(requiredApi().state.status).toBe('invalid')
      expect(requiredApi().state.lastVersionChange).toBeNull()
      expect(requiredApi().state.error?.code).toBe('layout.operation_failed')
      act(() => requiredApi().applyOperation(operation))
      expect(client.sent).toHaveLength(2)
    },
  )

  it.each([true, false])('shows only subtitle controls for native subtitle-only evidence (landscape=%s)', landscape => {
    render(first)
    flush()
    complete(createSubtitleOnlyLayoutResponseValue(request(0).requestId, landscape))
    expect(container.querySelector('.manual-layout-form, .keyframe-form')).toBeNull()
    expect(container.querySelector('[name="subtitle-text"]')).not.toBeNull()
    // FR-052: the subtitle-only notice is shown in plain language.
    expect(container.textContent).toContain('映像の画角や配置は変更できません')
    const svg = element<SVGElement>('.layout-preview svg')
    expect(svg.getAttribute('data-aspect')).toBe(landscape ? '16:9' : '9:16')
    expect(svg.style.aspectRatio).toBe(landscape ? '1920 / 1080' : '1080 / 1920')
    expect(svg.getAttribute('viewBox')).toBe(landscape ? '0 0 1920 1080' : '0 0 1080 1920')
    const legacy = createLayoutResponseValue().result.snapshot
    act(() => requiredApi().applyOperation({
      kind: 'setKeyframes', sourceAssetId: legacy.sources[0]!.sourceAssetId, destinationSlot: 'singleSubject',
      segments: [{ ...legacy.keyframeTracks[0]!.segments[0]!, keyframes: [{
        time: { value: '0', rate: '1' }, interpolation: 'hold',
        sourceCrop: legacy.keyframeTracks[0]!.evaluation.sourceCrop,
        destination: legacy.keyframeTracks[0]!.evaluation.destination,
      }] }], evaluationTime: { value: '0', rate: '1' },
    }))
    expect(client.sent).toHaveLength(1)
    expect(requiredApi().state.error?.code).toBe('layout.invalid_request')
  })

  it('adopts only its verified child context while retaining partial-ACK text and form history', () => {
    render(first)
    flush()
    complete(readValue(0, first))
    const form = element<HTMLFormElement>('form[data-layout-cue-id]')
    setValue(element<HTMLTextAreaElement>('[name="subtitle-text"]'), 'style ACK 後も未送信の本文')
    setValue(element<HTMLInputElement>('[name="subtitle-reason"]'), '本文にも残す理由')
    setValue(element<HTMLSelectElement>('[name="subtitle-edit-kind"]'), 'style')
    setValue(element<HTMLSelectElement>('[name="subtitle-style"]'), 'quote')
    submit(form)
    complete(childValue(1))
    expect(requiredApi().state.target).toEqual(first)
    expect(element('form[data-layout-cue-id]')).toBe(form)
    render(child)
    flush()
    expect(requiredApi().state.target).toEqual(child)
    expect(element('form[data-layout-cue-id]')).toBe(form)
    expect(container.querySelector('.layout-target-guard')).toBeNull()
    expect(client.sent).toHaveLength(2)
    setValue(element<HTMLSelectElement>('[name="subtitle-edit-kind"]'), 'text')
    expect(element<HTMLTextAreaElement>('[name="subtitle-text"]').value).toBe('style ACK 後も未送信の本文')
    submit(form)
    expect(client.sent).toHaveLength(2)
    act(() => button('この Cue の最新版を確認して入力を引き継ぐ').click())
    submit(form)
    expect(request(2).params).toMatchObject({
      expectedVersionId: child.versionId, expectedVersionDigest: child.versionDigest,
      operation: { edit: { kind: 'text', text: 'style ACK 後も未送信の本文' } },
    })
    act(() => requiredApi().adoptSnapshotVersion?.(second))
    expect(requiredApi().state.target).toEqual(child)
  })

  function render(context?: LayoutPanelContext, showPanel = true): void {
    act(() => root.render(
      <PlayerNavigationProvider>
        <LayoutStateProvider createClient={createClient}>
          <Probe />
          {showPanel && <LayoutPanel context={context} />}
        </LayoutStateProvider>
      </PlayerNavigationProvider>,
    ))
  }
  function flush(): void { act(() => vi.runAllTicks()); act(() => vi.runAllTicks()) }
  function requiredApi(): LayoutStateContextValue {
    if (api === undefined) throw new Error('Missing provider probe.')
    return api
  }
  function request(index: number): LayoutBridgeRequest {
    const sent = client.sent[index]
    if (sent === undefined) throw new Error(`Missing request ${index}.`)
    return sent
  }
  function complete(value: unknown): void {
    const response = parseLayoutBridgeResponse(value)
    if (response === null) throw new Error('Invalid native-shaped fixture.')
    act(() => client.emit(response))
  }
  function readValue(index: number, target: LayoutVersionContext) {
    const value = createLayoutResponseValue(request(index).requestId)
    const snapshot = value.result.snapshot
    snapshot.compositionVersionId = target.versionId
    snapshot.compositionVersionDigest = target.versionDigest
    snapshot.variantPair.compositionVersionId = target.versionId
    snapshot.variantPair.compositionVersionDigest = target.versionDigest
    for (const cue of [...snapshot.subtitles, ...snapshot.terops]) cue.compositionVersionId = target.versionId
    return value
  }
  function childValue(index: number) {
    const sent = request(index)
    if (sent.method !== 'layout.apply' || sent.params.operation.kind !== 'editSubtitle') throw new Error('Expected subtitle edit.')
    const value = readValue(index, first)
    const snapshot = value.result.snapshot
    const cue = snapshot.subtitles[0]!
    cue.compositionVersionId = child.versionId
    cue.revisionId = '85112222-3333-4444-8555-666677778888'
    cue.revisionDigest = '7'.repeat(64)
    cue.revisionCount++
    if (sent.params.operation.edit.kind === 'text') {
      cue.text = sent.params.operation.edit.text
      for (const display of snapshot.subtitleDisplays) {
        display.displayedCues[0]!.text = cue.text
        display.displayedCues[0]!.textChanged = false
      }
    } else if (sent.params.operation.edit.kind === 'style') {
      cue.style = sent.params.operation.edit.style
    }
    return {
      ...value, method: 'layout.apply',
      result: { status: 'available', snapshot: {
        ...snapshot, revision: sent.params.expectedRevision + 1,
        compositionVersionId: child.versionId, compositionVersionDigest: child.versionDigest, variantPair: null,
      } },
    }
  }
  function durableValue(index: number, sequence: number, method: 'layout.read' | 'layout.apply' = 'layout.read') {
    const original = readValue(index, first)
    const versionId = sequence === 0 ? first.versionId : sequence === 1 ? child.versionId : '34112222-3333-4444-8555-666677778888'
    return { ...original, method, result: { status: 'available', snapshot: {
      ...original.result.snapshot, revision: sequence, compositionVersionId: versionId, variantPair: null,
      persistence: sequence > 0 ? 'persisted' : 'notPersisted',
      subtitles: original.result.snapshot.subtitles.map(cue => sequence === 0 ? cue : ({ ...cue,
        compositionVersionId: versionId, revisionId: sequence === 1 ? '85112222-3333-4444-8555-666677778888' : '86112222-3333-4444-8555-666677778888',
        revisionDigest: String(sequence).repeat(64), revisionCount: cue.revisionCount + sequence,
        style: sequence === 1 ? 'quote' : 'caution',
      })),
    }, durableSubtitles: { rootVersionId: first.versionId, headVersionId: versionId,
      headVersionDigest: first.versionDigest, sequence, journalDigest: sequence === 0 ? null : String(sequence).repeat(64) } } }
  }
  function element<T extends Element = HTMLElement>(selector: string): T {
    const found = container.querySelector<T>(selector)
    if (found === null) throw new Error(`Missing ${selector}.`)
    return found
  }
  function button(label: string): HTMLButtonElement {
    const found = [...container.querySelectorAll('button')].find(candidate => candidate.textContent?.trim() === label)
    if (found === undefined) throw new Error(`Missing ${label}.`)
    return found
  }
})

class FakeClient implements LayoutBridgeClient {
  readonly sent: LayoutBridgeRequest[] = []
  private listener: ((response: LayoutBridgeResponse) => void) | undefined
  sendLayout(request: LayoutBridgeRequest): void { this.sent.push(request) }
  abandonLayoutRequest(): void {}
  onLayoutResponse(listener: (response: LayoutBridgeResponse) => void): () => void {
    this.listener = listener
    return () => { this.listener = undefined }
  }
  dispose(): void { this.listener = undefined }
  emit(response: LayoutBridgeResponse): void { this.listener?.(response) }
}

function setValue(input: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement, value: string): void {
  const prototype = input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype
    : input instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype
  const setter = Object.getOwnPropertyDescriptor(prototype, 'value')!.set!
  act(() => { setter.call(input, value); input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true })) })
}

function submit(form: HTMLFormElement): void {
  act(() => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
}