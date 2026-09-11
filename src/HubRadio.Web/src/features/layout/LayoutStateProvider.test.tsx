// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import {
  PlayerNavigationProvider,
  usePlayerSourceSeekRegistration,
  type PlayerSourceSeekHandler,
  type PlayerSourceSeekTarget,
} from '../player'
import { LayoutPanel } from './LayoutPanel'
import {
  LayoutStateProvider,
} from './LayoutStateProvider'
import type { LayoutBridgeClient } from './layoutBridgeSession'
import {
  LAYOUT_APPLY_METHOD,
  parseLayoutBridgeResponse,
  type LayoutApplyRequest,
  type LayoutBridgeRequest,
  type LayoutBridgeResponse,
} from './layoutProtocol'
import {
  createLayoutFailureValue,
  createLayoutResponseValue,
  setLayoutFixtureSubtitleText,
  layoutCueDigest,
  layoutCueId,
  layoutCueRevisionId,
  layoutDisplayDigest,
  layoutProjectId,
  layoutSourceId,
  layoutTeropDigest,
  layoutTeropId,
  layoutTeropRevisionId,
} from './layoutTestFixture'

const hostileSubtitle = '<img src=x onerror="globalThis.__layoutAttack=true">字幕、です。'
const hostileSubtitleWithoutPunctuation =
  '<img src=x onerror="globalThis.__layoutAttack=true">字幕です'

interface MutableResolution {
  status: string
  selectedCandidateId: string | null
  globalHardConstraints: string[]
  evaluations: Array<{ hardConstraints: string[] }>
}

describe('LayoutStateProvider integration', () => {
  let container: HTMLDivElement
  let root: Root
  let mounted: boolean
  let client: FakeLayoutClient
  let sourceSeekTargets: PlayerSourceSeekTarget[]

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    mounted = true
    client = new FakeLayoutClient()
    sourceSeekTargets = []
    const seek: PlayerSourceSeekHandler = (target) => {
      sourceSeekTargets.push(target)
      return 'requested'
    }
    act(() => {
      root.render(
        <PlayerNavigationProvider>
          <SourceSeekRegistration handler={seek} />
          <LayoutStateProvider createClient={() => client}>
            <LayoutPanel />
          </LayoutStateProvider>
        </PlayerNavigationProvider>,
      )
    })
  })

  afterEach(() => {
    if (mounted) {
      act(() => root.unmount())
    }
    container.remove()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT
    delete (globalThis as { __layoutAttack?: boolean }).__layoutAttack
  })

  it('loads correlated native evidence and changes preview variants without inventing render operations', () => {
    loadSnapshot()
    const read = requiredSent(0)
    expect(read.method).toBe('layout.read')
    expect(read.params).toEqual({
      projectId: layoutProjectId,
      evaluationTime: null,
    })
    expect(JSON.stringify(read)).not.toMatch(/actor|path|provider|confidence|face/i)

    const value = createLayoutResponseValue(read.requestId)
    setLayoutFixtureSubtitleText(value, hostileSubtitle)
    complete(value)

    const preview = requiredElement<SVGElement>('.layout-preview svg')
    expect(preview.getAttribute('viewBox')).toBe('0 0 1080 1920')
    expect(preview.getAttribute('data-native-width')).toBe('1080')
    expect(preview.getAttribute('data-native-height')).toBe('1920')
    expect(preview.getAttribute('data-aspect')).toBe('9:16')
    const safeZone = requiredElement<SVGRectElement>(
      '[data-safe-zone="output-frame-bounds"]',
    )
    expect(rectAttributes(safeZone)).toEqual({
      x: '0',
      y: '0',
      width: '1080',
      height: '1920',
    })
    const keyframe = requiredElement<SVGRectElement>(
      '[data-keyframe-slot="singleSubject"]',
    )
    expect(rectAttributes(keyframe)).toEqual({
      x: '270',
      y: '480',
      width: '540',
      height: '960',
    })

    expect(container.querySelector('img')).toBeNull()
    expect(container.innerHTML).toContain('&lt;img')
    expect(
      (globalThis as { __layoutAttack?: boolean }).__layoutAttack,
    ).toBeUndefined()
    expect(container.querySelector('[data-subtitle-cue]')).not.toBeNull()
    expect(container.querySelector('[data-terop-cue]')).not.toBeNull()

    const displayInputs = Array.from(
      container.querySelectorAll<HTMLInputElement>(
        'input[name="subtitle-display-profile"]',
      ),
    )
    expect(displayInputs).toHaveLength(4)
    expect(displayInputs[0]!.checked).toBe(true)
    expect(container.textContent).toContain('全文・句読点あり')
    act(() => displayInputs[1]!.click())
    expect(preview.getAttribute('data-subtitle-profile')).toBe('1'.repeat(64))
    expect(
      requiredElement<SVGTextElement>('.layout-preview-subtitle-text')
        .textContent,
    ).toBe(hostileSubtitleWithoutPunctuation)
    act(() => displayInputs[2]!.click())
    expect(preview.getAttribute('data-subtitle-profile')).toBe('2'.repeat(64))
    expect(
      requiredElement<SVGTextElement>('.layout-preview-subtitle-text')
        .textContent,
    ).toBe(hostileSubtitle)
    expect(client.sent).toHaveLength(1)

    const variantInputs = Array.from(
      container.querySelectorAll<HTMLInputElement>(
        'input[name="subtitle-variant"]',
      ),
    )
    expect(variantInputs).toHaveLength(2)
    act(() => variantInputs[1]!.click())
    expect(preview.getAttribute('data-variant')).toBe('subtitleFree')
    expect(preview.getAttribute('data-subtitle-profile')).toBe(layoutDisplayDigest)
    expect(container.querySelector('[data-subtitle-cue]')).toBeNull()
    expect(container.querySelector('[data-terop-cue]')).not.toBeNull()
    expect(client.sent).toHaveLength(1)

    act(() => variantInputs[0]!.click())
    expect(preview.getAttribute('data-variant')).toBe('burnedIn')
    expect(container.querySelector('[data-subtitle-cue]')).not.toBeNull()
    expect(client.sent).toHaveLength(1)
    act(() => displayInputs[1]!.click())
    expect(preview.getAttribute('data-variant')).toBe('editor')
    expect(preview.getAttribute('data-subtitle-profile')).toBe('1'.repeat(64))
    expect(container.textContent).toContain(
      'preview 表示だけの session-local 状態',
    )
  })

  it('supports all seven manual modes by keyboard and submits exact no-face geometry', () => {
    loadAndComplete()
    const modes = Array.from(
      container.querySelectorAll<HTMLButtonElement>(
        '[aria-label="手動レイアウトモード"] [role="radio"]',
      ),
    )
    expect(modes).toHaveLength(7)
    expect(modes.map((mode) => mode.tabIndex)).toEqual([0, -1, -1, -1, -1, -1, -1])

    act(() => {
      modes[0]!.focus()
      modes[0]!.dispatchEvent(new KeyboardEvent('keydown', {
        key: 'End',
        bubbles: true,
        cancelable: true,
      }))
    })
    expect(document.activeElement).toBe(modes[6])
    expect(modes[6]!.getAttribute('aria-checked')).toBe('true')
    expect(container.textContent).toContain('既存 template evidence')

    act(() => {
      modes[6]!.dispatchEvent(new KeyboardEvent('keydown', {
        key: 'Home',
        bubbles: true,
        cancelable: true,
      }))
    })
    expect(document.activeElement).toBe(modes[0])
    expect(modes[0]!.getAttribute('aria-checked')).toBe('true')

    const decisions = container.querySelectorAll<HTMLInputElement>(
      'input[name="static-crop-decision"]',
    )
    act(() => decisions[0]!.click())
    submit(requiredElement<HTMLFormElement>('.manual-layout-form'))

    const request = requiredApply(1)
    expect(request.params.expectedRevision).toBe(3)
    expect(request.params.operation).toEqual({
      kind: 'setManualLayout',
      mode: 'singleSubjectCrop',
      staticCropDecision: 'singleStaticCropSufficient',
      sources: [
        {
          sourceAssetId: layoutSourceId,
          crop: {
            x: { numerator: '1', denominator: '3' },
            y: { numerator: '0', denominator: '1' },
            width: { numerator: '2', denominator: '3' },
            height: { numerator: '1', denominator: '1' },
          },
          subject: {
            x: { numerator: '1', denominator: '3' },
            y: { numerator: '1', denominator: '4' },
            width: { numerator: '1', denominator: '3' },
            height: { numerator: '1', denominator: '2' },
          },
          destination: {
            x: { numerator: '1', denominator: '3' },
            y: { numerator: '0', denominator: '1' },
            width: { numerator: '2', denominator: '3' },
            height: { numerator: '1', denominator: '1' },
          },
        },
      ],
      templateId: null,
    })
    expect(JSON.stringify(request)).not.toMatch(
      /face|candidate|confidence|provider|actor|path/i,
    )
  })

  it('does not invent a default display profile without variant evidence', () => {
    loadSnapshot()
    const read = requiredSent(0)
    const value = createLayoutResponseValue(read.requestId)
    ;(value.result.snapshot as unknown as { variantPair: unknown })
      .variantPair = null
    complete(value)

    const displayInputs = Array.from(
      container.querySelectorAll<HTMLInputElement>(
        'input[name="subtitle-display-profile"]',
      ),
    )
    expect(displayInputs).toHaveLength(4)
    expect(displayInputs.every((input) => !input.checked)).toBe(true)
    expect(container.querySelector('[data-subtitle-cue]')).toBeNull()
    expect(container.textContent).toContain('profile を明示的に選択')

    act(() => displayInputs[0]!.click())
    expect(container.querySelector('[data-subtitle-cue]')).not.toBeNull()
    expect(client.sent).toHaveLength(1)
  })

  it('edits only an existing native keyframe track with exact values', () => {
    loadAndComplete()
    setControlValue(
      requiredElement<HTMLInputElement>(
        '#keyframe-0-0-destination-x',
      ),
      '1/4',
    )
    submit(requiredElement<HTMLFormElement>('.keyframe-form'))

    const request = requiredApply(1)
    const operation = request.params.operation
    expect(operation.kind).toBe('setKeyframes')
    if (operation.kind !== 'setKeyframes') {
      throw new Error('Expected a keyframe operation.')
    }
    expect(operation.sourceAssetId).toBe(layoutSourceId)
    expect(operation.destinationSlot).toBe('singleSubject')
    expect(operation.evaluationTime).toEqual({ value: '1', rate: '1' })
    expect(operation.segments[0]!.keyframes[0]!.destination.x).toEqual({
      numerator: '1',
      denominator: '4',
    })
    expect(operation.segments[0]!.keyframes[1]!.interpolation).toBe('hold')
    expect(JSON.stringify(operation)).not.toMatch(/float|frameRate|easing/i)
  })

  it('shows overlap evidence, escapes Cue text, and navigates an unsolved subtitle to source time', () => {
    loadSnapshot()
    const read = requiredSent(0)
    const value = createLayoutResponseValue(read.requestId)
    const subtitle = value.result.snapshot.subtitles[0]!
    setLayoutFixtureSubtitleText(value, hostileSubtitle)
    const resolution = subtitle.layoutResolution as unknown as MutableResolution
    resolution.status = 'manualAdjustmentRequired'
    resolution.selectedCandidateId = null
    resolution.globalHardConstraints = ['noPlacementCandidate']
    resolution.evaluations[0]!.hardConstraints = ['prohibitedRegionOverlap']
    complete(value)

    expect(container.textContent).toContain('自動配置の解がありません')
    expect(container.textContent).toContain('配置候補なし')
    expect(container.textContent).toContain('禁止 region と重なり')
    expect(container.querySelector('img')).toBeNull()
    expect(container.innerHTML).toContain('&lt;img')
    expect(container.querySelector('[data-subtitle-cue]')).toBeNull()

    act(() => buttonByText('元時刻へ移動').click())
    expect(sourceSeekTargets).toEqual([{
      projectId: layoutProjectId,
      sourceTime: { value: '0', rate: '1' },
    }])
    expect(container.textContent).toContain(
      '字幕の元時刻への移動をプレイヤーへ要求しました。',
    )

    const subtitleEditor = requiredElement<HTMLElement>(
      '[data-layout-status="manualAdjustmentRequired"]',
    )
    setControlValue(
      requiredElementWithin<HTMLTextAreaElement>(subtitleEditor, 'textarea'),
      '修正版の字幕',
    )
    setControlValue(
      requiredElementWithin<HTMLInputElement>(subtitleEditor, 'input[type="text"]'),
      '重なりを手動で確認して本文を修正',
    )
    submit(requiredElementWithin<HTMLFormElement>(subtitleEditor, 'form'))

    const request = requiredApply(1)
    expect(request.params.operation).toEqual({
      kind: 'editSubtitle',
      cueId: layoutCueId,
      expectedRevisionId: layoutCueRevisionId,
      expectedRevisionDigest: layoutCueDigest,
      edit: { kind: 'text', text: '修正版の字幕' },
      reason: '重なりを手動で確認して本文を修正',
    })
  })

  it('edits a subtitle revision through keyboard focus order and Enter', async () => {
    loadAndComplete()
    const user = userEvent.setup({ document })
    const cueOption = requiredElement<HTMLButtonElement>(
      '[aria-label="字幕 Cue 一覧"] [role="option"]',
    )
    act(() => cueOption.focus())

    await act(async () => user.keyboard('[Tab]'))
    expect(document.activeElement).toBe(buttonByText('元時刻へ移動'))

    await act(async () => user.keyboard('[Tab]'))
    expect(document.activeElement).toBe(
      requiredElement<HTMLSelectElement>('select[name="subtitle-edit-kind"]'),
    )

    await act(async () => user.keyboard('[Tab]'))
    const text = requiredElement<HTMLTextAreaElement>(
      '.subtitle-editor-grid > section:first-child textarea',
    )
    expect(document.activeElement).toBe(text)
    await act(async () => user.clear(text))
    await act(async () => user.keyboard('キーボードで修正した字幕'))

    await act(async () => user.keyboard('[Tab]'))
    const reason = requiredElement<HTMLInputElement>(
      'input[name="subtitle-reason"]',
    )
    expect(document.activeElement).toBe(reason)
    await act(async () => user.keyboard('キーボード操作を確認'))

    await act(async () => user.keyboard('[Tab]'))
    const submitButton = buttonByText('字幕 revision を作成')
    expect(document.activeElement).toBe(submitButton)
    await act(async () => user.keyboard('[Enter]'))

    expect(document.activeElement).toBe(submitButton)
    expect(requiredApply(1).params.operation).toEqual({
      kind: 'editSubtitle',
      cueId: layoutCueId,
      expectedRevisionId: layoutCueRevisionId,
      expectedRevisionDigest: layoutCueDigest,
      edit: { kind: 'text', text: 'キーボードで修正した字幕' },
      reason: 'キーボード操作を確認',
    })
  })

  it('submits every existing non-text Cue edit without inventing style controls', () => {
    loadAndComplete()
    const subtitleForm = requiredElement<HTMLFormElement>(
      '.subtitle-editor-grid > section:first-child .cue-edit-form',
    )
    const subtitleKind = requiredElement<HTMLSelectElement>(
      'select[name="subtitle-edit-kind"]',
    )

    setControlValue(subtitleKind, 'timing')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="subtitle-startValue"]',
    ), '1')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="subtitle-startRate"]',
    ), '2')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="subtitle-durationValue"]',
    ), '3')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="subtitle-durationRate"]',
    ), '2')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="subtitle-reason"]',
    ), '字幕 timing を調整')
    submit(subtitleForm)
    expect(requiredApply(1).params.operation).toMatchObject({
      kind: 'editSubtitle',
      cueId: layoutCueId,
      expectedRevisionId: layoutCueRevisionId,
      expectedRevisionDigest: layoutCueDigest,
      edit: {
        kind: 'timing',
        outputRange: {
          start: { value: '1', rate: '2' },
          duration: { value: '3', rate: '2' },
        },
      },
    })
    completeNoChange(1)

    setControlValue(subtitleKind, 'style')
    setControlValue(requiredElement<HTMLSelectElement>(
      'select[name="subtitle-style"]',
    ), 'emphasis')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="subtitle-reason"]',
    ), '字幕 style を調整')
    submit(subtitleForm)
    expect(requiredApply(2).params.operation).toMatchObject({
      kind: 'editSubtitle',
      edit: { kind: 'style', style: 'emphasis' },
    })
    completeNoChange(2)

    setControlValue(subtitleKind, 'placement')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="subtitle-x"]',
    ), '1/10')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="subtitle-reason"]',
    ), '字幕 placement を調整')
    submit(subtitleForm)
    expect(requiredApply(3).params.operation).toMatchObject({
      kind: 'editSubtitle',
      edit: {
        kind: 'placement',
        regionName: 'subtitle',
        rect: {
          x: { numerator: '1', denominator: '10' },
          y: { numerator: '3', denominator: '4' },
          width: { numerator: '1', denominator: '1' },
          height: { numerator: '1', denominator: '4' },
        },
      },
    })
    completeNoChange(3)

    const teropForm = requiredElement<HTMLFormElement>(
      '.subtitle-editor-grid > section:last-child .cue-edit-form',
    )
    const teropKind = requiredElement<HTMLSelectElement>(
      'select[name="terop-edit-kind"]',
    )
    setControlValue(teropKind, 'timing')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="terop-startValue"]',
    ), '2')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="terop-durationValue"]',
    ), '5')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="terop-reason"]',
    ), 'テロップ timing を調整')
    submit(teropForm)
    expect(requiredApply(4).params.operation).toMatchObject({
      kind: 'editTerop',
      cueId: layoutTeropId,
      expectedRevisionId: layoutTeropRevisionId,
      expectedRevisionDigest: layoutTeropDigest,
      edit: {
        kind: 'timing',
        outputRange: {
          start: { value: '2', rate: '1' },
          duration: { value: '5', rate: '1' },
        },
      },
    })
    completeNoChange(4)

    setControlValue(teropKind, 'style')
    setControlValue(requiredElement<HTMLSelectElement>(
      'select[name="terop-style"]',
    ), 'quote')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="terop-reason"]',
    ), 'テロップ style を調整')
    submit(teropForm)
    expect(requiredApply(5).params.operation).toMatchObject({
      kind: 'editTerop',
      edit: { kind: 'style', style: 'quote' },
    })
    completeNoChange(5)

    setControlValue(teropKind, 'region')
    setControlValue(requiredElement<HTMLSelectElement>(
      'select[name="terop-region"]',
    ), 'graphic')
    setControlValue(requiredElement<HTMLInputElement>(
      'input[name="terop-reason"]',
    ), 'テロップ region を調整')
    submit(teropForm)
    expect(requiredApply(6).params.operation).toMatchObject({
      kind: 'editTerop',
      edit: { kind: 'region', regionName: 'graphic' },
    })
    expect(JSON.stringify(client.sent.slice(1))).not.toMatch(
      /fontSize|color|animation|provider|actor|path/i,
    )
  })

  it('retains evidence on noChange and keeps the standalone form through conflict and explicit refresh', () => {
    loadAndComplete()
    const workspace = requiredElement<HTMLElement>('.layout-workspace')
    const keyframeForm = requiredElement<HTMLFormElement>('.keyframe-form')
    const keyframe = requiredElement<HTMLInputElement>('#keyframe-0-0-destination-x')
    submit(requiredElement<HTMLFormElement>('.keyframe-form'))
    const firstApply = requiredApply(1)
    complete(createLayoutFailureValue(
      'noChange',
      firstApply.requestId,
      LAYOUT_APPLY_METHOD,
    ))

    expect(container.querySelector('.layout-workspace')).not.toBeNull()
    expect(container.textContent).toContain(
      '変更内容は現行 evidence と同一でした。現行 snapshot を維持します。',
    )
    expect(container.textContent).toContain('revision 3・未永続化')

    setControlValue(keyframe, '1/4')
    submit(requiredElement<HTMLFormElement>('.keyframe-form'))
    const secondApply = requiredApply(2)
    complete(createLayoutFailureValue(
      'conflict',
      secondApply.requestId,
      LAYOUT_APPLY_METHOD,
    ))

    expect(requiredElement('.layout-workspace')).toBe(workspace)
    expect(requiredElement('.keyframe-form')).toBe(keyframeForm)
    expect(requiredElement('#keyframe-0-0-destination-x')).toBe(keyframe)
    expect(keyframe.value).toBe('1/4')
    expect(container.textContent).toContain('旧根拠（再確認が必要）')
    expect(requiredElement('.layout-panel').getAttribute('data-layout-dirty')).toBe('true')
    const failure = requiredElement<HTMLElement>('.layout-failure[role="alert"]')
    expect(failure.textContent).toContain(
      'レイアウトまたは Cue の revision が更新されました。',
    )
    expect(failure.textContent).not.toMatch(/C:\\|exception|actor|path/i)
    submit(keyframeForm)
    expect(client.sent).toHaveLength(3)
    expect(buttonByText('最新版を再読み込み').disabled).toBe(false)
    act(() => buttonByText('最新版を再読み込み').click())
    const refresh = requiredSent(3)
    expect(refresh.method).toBe('layout.read')
    const latest = createLayoutResponseValue(refresh.requestId)
    latest.result.snapshot.revision = 4
    complete(latest)
    expect(requiredElement('.layout-workspace')).toBe(workspace)
    expect(requiredElement('.keyframe-form')).toBe(keyframeForm)
    expect(keyframe.value).toBe('1/4')
    expect(client.sent).toHaveLength(4)
    submit(keyframeForm)
    expect(requiredApply(4).params.expectedRevision).toBe(4)
    expect(requiredApply(4).params.operation).toEqual(secondApply.params.operation)
  })

  function loadSnapshot(): void {
    setControlValue(
      requiredElement<HTMLInputElement>('#layout-project-id'),
      layoutProjectId,
    )
    submit(requiredElement<HTMLFormElement>('.layout-load-form'))
  }

  function loadAndComplete(): void {
    loadSnapshot()
    const request = requiredSent(0)
    complete(createLayoutResponseValue(request.requestId))
  }

  function complete(value: unknown): void {
    const response = parseLayoutBridgeResponse(value)
    if (response === null) {
      throw new Error('Expected a valid layout response fixture.')
    }
    act(() => client.emit(response))
  }

  function completeNoChange(requestIndex: number): void {
    complete(createLayoutFailureValue(
      'noChange',
      requiredApply(requestIndex).requestId,
      LAYOUT_APPLY_METHOD,
    ))
  }

  function requiredSent(index: number): LayoutBridgeRequest {
    const request = client.sent[index]
    if (request === undefined) {
      throw new Error(`Expected sent layout request ${index}.`)
    }
    return request
  }

  function requiredApply(index: number): LayoutApplyRequest {
    const request = requiredSent(index)
    if (request.method !== LAYOUT_APPLY_METHOD) {
      throw new Error(`Expected layout apply request ${index}.`)
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

  function buttonByText(text: string): HTMLButtonElement {
    const button = Array.from(
      container.querySelectorAll<HTMLButtonElement>('button'),
    ).find((candidate) => candidate.textContent?.trim() === text)
    if (button === undefined) {
      throw new Error(`Expected button with text ${text}.`)
    }
    return button
  }
})

function SourceSeekRegistration({
  handler,
}: Readonly<{ handler: PlayerSourceSeekHandler }>) {
  usePlayerSourceSeekRegistration(handler)
  return null
}

class FakeLayoutClient implements LayoutBridgeClient {
  readonly sent: LayoutBridgeRequest[] = []
  readonly abandoned: string[] = []
  unsubscribeCount = 0
  disposeCount = 0
  private listener: ((response: LayoutBridgeResponse) => void) | null = null

  sendLayout(request: LayoutBridgeRequest): void {
    this.sent.push(request)
  }

  abandonLayoutRequest(requestId: string): void {
    this.abandoned.push(requestId)
  }

  onLayoutResponse(
    listener: (response: LayoutBridgeResponse) => void,
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

  emit(response: LayoutBridgeResponse): void {
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

function submit(form: HTMLFormElement): void {
  act(() => {
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  })
}

function requiredElementWithin<T extends Element>(
  parent: Element,
  selector: string,
): T {
  const element = parent.querySelector(selector)
  if (element === null) {
    throw new Error(`Expected child element matching ${selector}.`)
  }
  return element as T
}

function rectAttributes(rect: SVGRectElement) {
  return {
    x: rect.getAttribute('x'),
    y: rect.getAttribute('y'),
    width: rect.getAttribute('width'),
    height: rect.getAttribute('height'),
  }
}
