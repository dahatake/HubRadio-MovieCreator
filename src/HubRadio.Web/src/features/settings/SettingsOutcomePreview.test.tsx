// @vitest-environment jsdom

import { readFileSync } from 'node:fs'
import { URL as NodeURL } from 'node:url'
import { within } from '@testing-library/dom'
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SourceOnlyProjectSummary } from '../../bridge/projectProtocol'
import { deriveResultPresentation } from '../results/resultPresentation'
import {
  SETTINGS_OUTCOME_CONTROL_KEYS,
  SettingsOutcomePreview,
  SettingsRegenerationAction,
  type SettingsOutcomePreviewProps,
} from './SettingsOutcomePreview'
import type { AnySettingsSnapshot, SettingDefinition, SettingSelection, SettingValue } from './settingsProtocol'

const sourceOnly: SourceOnlyProjectSummary = Object.freeze({
  profileId: '8d75a1fe-d10e-4aa7-87c5-3ac2d06d1f9b',
  profileVersion: '1.0.0', profileDigest: 'a'.repeat(64), settingsSnapshotDigest: 'b'.repeat(64),
  artifactSet: Object.freeze([
    'landscapeLongForm', 'verticalShortCandidates', 'subtitleBurnInVariant',
    'cleanVariant', 'subtitleFiles', 'thumbnailCandidates',
  ]),
})

const bundledSettings = (JSON.parse(readFileSync(new NodeURL(
  '../../../../HubRadio.App/DefaultSettings/Profiles/1.1.0.json', import.meta.url,
), 'utf8')) as { settings: { key: string; minimum: unknown; maximum: unknown; allowedTextValues: readonly string[] }[] }).settings

// Bundled keys/count bounds, with test-only resolution options to exercise changes.
const definitions: readonly SettingDefinition[] = [
  definition('output.landscapeResolution', text('1920x1080'), { allowedTextValues: ['1920x1080', '1280x720', '2560x1440'] }),
  definition('output.verticalResolution', text('1080x1920'), { allowedTextValues: ['1080x1920', '720x1280'] }),
  definition('subtitles.enabled', boolean(true)),
  definition('project.verticalShortCandidateCount', integer('3'), { minimum: integer('0'), maximum: integer('3') }),
  definition('output.shortTargetDuration', integer('30'), { minimum: integer('15'), maximum: integer('60') }),
  definition('thumbnails.candidateCount', integer('3'), { minimum: integer('0') }),
]
const snapshot: AnySettingsSnapshot = {
  projectId: '11112222-3333-4444-8555-666677778888',
  profileId: sourceOnly.profileId, profileName: 'outcome-test', profileVersion: '1.0.0',
  profileDigest: sourceOnly.profileDigest, initialSnapshotDigest: 'b'.repeat(64),
  snapshotDigest: 'c'.repeat(64), revision: 4, updatedAt: '2026-09-07T00:00:00.0000000+00:00',
  definitionCount: definitions.length, definitions,
  initialValues: definitions.map(item => selection(item.key, item.defaultValue)),
  values: [
    selection('output.landscapeResolution', text('1280x720')),
    selection('output.verticalResolution', text('720x1280')),
    selection('subtitles.enabled', boolean(false)),
    selection('project.verticalShortCandidateCount', integer('2')),
    selection('output.shortTargetDuration', integer('45')),
    selection('thumbnails.candidateCount', integer('0')),
  ],
}
const draftValues: readonly SettingSelection[] = Object.freeze([
  selection('output.landscapeResolution', text('2560x1440')),
  selection('output.verticalResolution', text('1080x1920')),
  selection('subtitles.enabled', boolean(true)),
  selection('project.verticalShortCandidateCount', integer('3')),
  selection('output.shortTargetDuration', integer('60')),
  selection('thumbnails.candidateCount', integer('4')),
])

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('SettingsOutcomePreview source-only summary', () => {
  it('uses the existing result presentation with no full snapshot or catalog dimensions/counts', () => {
    const view = renderPreview({ mode: 'sourceOnly', sourceOnly })
    const plans = deriveResultPresentation({ sourceOnlySummary: sourceOnly })
    expect(Array.from(view.querySelectorAll('[data-result-id]')).map(row => ({
      id: row.getAttribute('data-result-id'),
      title: row.querySelector('dt')?.textContent,
    }))).toEqual(plans.cards.map(card => ({ id: card.id, title: card.title })))
    for (const card of plans.cards) {
      for (const label of card.plannedLabels) expect(view.textContent).toContain(label)
    }
    expect(view.textContent).toContain('保存済み設定')
    expect(view.textContent).toContain('固定された設定')
    expect(view.textContent).toContain('解像度・候補数・字幕設定の詳細は未取得')
    expect(view.querySelector('[data-setting-key]')).toBeNull()
    expect(view.textContent).not.toMatch(/1920|1080|1280|720|\d+\s*[本枚]|px（幅/)
    expect(view.querySelector('video, img, canvas, button, input')).toBeNull()
    expect(view.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true')
    expectUnconfirmed(view)
  })

  it('does not parse dimensions or counts from unknown artifact names or replace an older summary', () => {
    const view = renderPreview({
      mode: 'sourceOnly',
      sourceOnly: {
        ...sourceOnly, profileVersion: '0.1.0', profileDigest: 'd'.repeat(64),
        artifactSet: ['long-form-1280x720', 'verticalShortCandidates3', '<img src=x onerror="alert(1)">'],
      },
    })
    expect(view.textContent).toContain('その他の予定成果物')
    expect(view.textContent).not.toMatch(/1280|720|1920|1080|3 本|短尺動画候補|onerror/)
    expect(view.querySelectorAll('[data-result-id]')).toHaveLength(1)
    expect(view.querySelector('[data-preview-state]')?.getAttribute('data-preview-state')).toBe('planned')
    expectUnconfirmed(view)
  })

  it('does not fill an empty summary with the standard artifact set', () => {
    const view = renderPreview({ mode: 'sourceOnly', sourceOnly: { ...sourceOnly, artifactSet: [] } })
    expect(view.querySelector('[data-preview-state]')?.getAttribute('data-preview-state')).toBe('unavailable')
    expect(view.querySelector('[data-result-id]')).toBeNull()
    expect(view.textContent).toContain('予定成果物は未取得')
    expectUnconfirmed(view)
  })
})

describe('SettingsOutcomePreview snapshot plans', () => {
  it('uses exact bundled keys and the real vertical candidate count bounds', () => {
    expect(SETTINGS_OUTCOME_CONTROL_KEYS).toEqual(definitions.map(item => item.key))
    for (const key of SETTINGS_OUTCOME_CONTROL_KEYS) {
      expect(bundledSettings.filter(item => item.key === key)).toHaveLength(1)
    }
    const count = bundledSettings.find(item => item.key === 'project.verticalShortCandidateCount')!
    expect(count.minimum).toEqual({ kind: 'integer', integer: 0 })
    expect(count.maximum).toEqual({ kind: 'integer', integer: 3 })
  })

  it('shows saved values rather than initial/catalog defaults and does not mistake revision for unsaved changes', () => {
    const view = renderPreview({ mode: 'snapshot', snapshot })
    expect(cells(view, 'output.landscapeResolution')).toEqual(['1280 × 720 px（幅 × 高さ）'])
    expect(cells(view, 'output.verticalResolution')).toEqual(['720 × 1280 px（幅 × 高さ）'])
    expect(cells(view, 'subtitles.enabled')).toEqual(['字幕なし'])
    expect(cells(view, 'project.verticalShortCandidateCount')).toEqual(['2 本（目標）'])
    expect(cells(view, 'output.shortTargetDuration')).toEqual(['45 秒（目標）'])
    expect(cells(view, 'thumbnails.candidateCount')).toEqual(['0 枚（目標）'])
    expect(view.textContent).not.toMatch(/1920|3 本|3 枚/)
    expect(view.querySelector('[role="status"]')?.getAttribute('data-changed')).toBe('false')
    expect(view.querySelectorAll('tr[data-changed="true"]')).toHaveLength(0)
    expectUnconfirmed(view)
  })

  it('compares saved and draft plans with visible numbers, subtitle changes and explicit changed flags', () => {
    const before = JSON.stringify({ snapshot, draftValues })
    const view = renderPreview({ mode: 'snapshot', snapshot, draftValues })
    const table = within(view).getByRole('table', { name: '設定に基づく次回生成の予定' })
    expect(within(table).getByRole('columnheader', { name: '保存済み設定' })).toBeTruthy()
    expect(within(table).getByRole('columnheader', { name: '未保存の変更' })).toBeTruthy()
    expect(cells(view, 'output.landscapeResolution')).toEqual(['1280 × 720 px（幅 × 高さ）', '2560 × 1440 px（幅 × 高さ）'])
    expect(cells(view, 'output.verticalResolution')).toEqual(['720 × 1280 px（幅 × 高さ）', '1080 × 1920 px（幅 × 高さ）'])
    expect(cells(view, 'subtitles.enabled')).toEqual(['字幕なし', '字幕あり'])
    expect(cells(view, 'project.verticalShortCandidateCount')).toEqual(['2 本（目標）', '3 本（目標）'])
    expect(cells(view, 'output.shortTargetDuration')).toEqual(['45 秒（目標）', '60 秒（目標）'])
    expect(cells(view, 'thumbnails.candidateCount')).toEqual(['0 枚（目標）', '4 枚（目標）'])
    expect(view.querySelector('[role="status"]')?.getAttribute('data-changed')).toBe('true')
    const changed = view.querySelectorAll('tr[data-changed="true"]')
    expect(changed).toHaveLength(6)
    for (const row of changed) expect(row.querySelector('th')?.textContent).toContain('変更あり')
    expect(JSON.stringify({ snapshot, draftValues })).toBe(before)
    expectUnconfirmed(view)
  })

  it('treats partial drafts as overrides and keeps omitted settings unchanged', () => {
    const view = renderPreview({ mode: 'snapshot', snapshot, draftValues: [selection('subtitles.enabled', boolean(true))] })
    expect(cells(view, 'subtitles.enabled')).toEqual(['字幕なし', '字幕あり'])
    expect(cells(view, 'project.verticalShortCandidateCount')).toEqual(['2 本（目標）', '変更なし'])
    expect(view.querySelectorAll('tr[data-changed="true"]')).toHaveLength(1)
  })

  it('does not call an identical, reordered or empty draft an unsaved change', () => {
    for (const values of [[], [...snapshot.values].reverse().map(item => ({
      ...item,
      value: item.value.kind === 'integer' ? { integerValue: item.value.integerValue, kind: 'integer' as const } : { ...item.value },
    }))]) {
      const view = renderPreview({ mode: 'snapshot', snapshot, draftValues: values })
      expect(view.querySelector('[role="status"]')?.getAttribute('data-changed')).toBe('false')
      expect(within(view).queryByRole('columnheader', { name: '未保存の変更' })).toBeNull()
    }
  })

  it('separates global defaults from this video and its children, and project settings from existing outputs', () => {
    const global = renderPreview({ mode: 'snapshot', snapshot: { ...snapshot, projectId: null }, draftValues })
    expect(global.textContent).toContain('全体の既定設定：新しく作成する動画向け')
    expect(global.textContent).toContain('現在の動画や既存の子版には適用しません')
    const project = renderPreview({ mode: 'snapshot', snapshot, draftValues })
    expect(project.textContent).toContain('この動画の設定：次回生成向け')
    expect(project.textContent).toContain('既存の成果物や子版は更新しません')
    expectUnconfirmed(global)
    expectUnconfirmed(project)
  })

  it('does not invent visual effects for unsupported or undeclared keys', () => {
    const unsupported = [
      definition('thumbnails.generatedFaceEnabled', boolean(false)),
      definition('evaluation.humanPassClaimEnabled', boolean(false)),
    ]
    const view = renderPreview({
      mode: 'snapshot',
      snapshot: {
        ...snapshot, definitions: [...definitions, ...unsupported], definitionCount: definitions.length + unsupported.length,
        values: [...snapshot.values, ...unsupported.map(item => selection(item.key, item.defaultValue))],
        initialValues: [...snapshot.initialValues, ...unsupported.map(item => selection(item.key, item.defaultValue))],
      },
      draftValues: [...unsupported.map(item => selection(item.key, boolean(true))), selection('output.longForm.width', integer('9999'))],
    })
    expect(view.querySelector('[role="status"]')?.getAttribute('data-changed')).toBe('true')
    expect(view.querySelectorAll('tr[data-changed="true"]')).toHaveLength(0)
    expect(view.textContent).toContain('プレビュー未対応の設定の影響は表示しません')
    expect(view.textContent).not.toMatch(/9999|顔検出|顔生成|PASS/)
    expect(view.querySelectorAll('[data-setting-key]')).toHaveLength(6)
    expectUnconfirmed(view)
  })

  it('requires definitions as well as values, without treating an undeclared draft as an output change', () => {
    const view = renderPreview({ mode: 'snapshot', snapshot: { ...snapshot, definitions: [], definitionCount: 0 }, draftValues })
    expect(view.querySelector('[data-setting-key]')).toBeNull()
    expect(view.querySelector('[role="status"]')?.getAttribute('data-changed')).toBe('false')
    expect(view.textContent).toContain('プレビュー対応の設定がありません')
    expect(view.textContent).not.toMatch(/1280|2560|3 本|字幕あり/)
    expectUnconfirmed(view)
  })

  it.each([
    ['missing value', undefined],
    ['wrong kind', integer('1920')],
    ['unrecognized resolution', text('long-form-1920x1080')],
    ['zero dimension', text('0x1080')],
  ] as const)('does not fill an unavailable dimension from its definition: %s', (_name, value) => {
    const values = snapshot.values.filter(item => item.key !== 'output.landscapeResolution')
    if (value !== undefined) values.push(selection('output.landscapeResolution', value))
    const view = renderPreview({ mode: 'snapshot', snapshot: { ...snapshot, values } })
    expect(cells(view, 'output.landscapeResolution')).toEqual(['未取得・未対応'])
    expectUnconfirmed(view)
  })

  it.each(['', 'NaN', '-1', '1.5'])('does not turn an in-progress invalid draft count into a planned count: %s', value => {
    const view = renderPreview({ mode: 'snapshot', snapshot, draftValues: [selection('project.verticalShortCandidateCount', integer(value))] })
    expect(cells(view, 'project.verticalShortCandidateCount')).toEqual(['2 本（目標）', '入力値を確認'])
    expectUnconfirmed(view)
  })

  it.each([
    ['0', '0 本（目標）'],
    ['3', '3 本（目標）'],
    ['4', '入力値を確認'],
    ['7', '入力値を確認'],
  ])('enforces the bundled inclusive 0..3 candidate count range for draft %s', (value, expected) => {
    const view = renderPreview({ mode: 'snapshot', snapshot, draftValues: [selection('project.verticalShortCandidateCount', integer(value))] })
    expect(cells(view, 'project.verticalShortCandidateCount')).toEqual(['2 本（目標）', expected])
    expect(view.querySelector('[role="status"]')?.getAttribute('data-changed')).toBe('true')
    const row = view.querySelector('[data-setting-key="project.verticalShortCandidateCount"]')!
    expect(row.getAttribute('data-changed')).toBe('true')
    expect(row.querySelector('th')?.textContent).toContain('変更あり')
    if (expected === '入力値を確認') expect(view.textContent).not.toContain(`${value} 本（目標）`)
    expectUnconfirmed(view)
  })

  it('rejects well-formed resolutions outside the bundled allowlist without replacing the saved value', () => {
    const key = 'output.landscapeResolution'
    const allowedTextValues = bundledSettings.find(item => item.key === key)!.allowedTextValues
    expect(allowedTextValues).toEqual(['1920x1080'])
    const constrained = withSetting(definition(key, text('1920x1080'), { allowedTextValues }), text('1920x1080'))
    const view = renderPreview({ mode: 'snapshot', snapshot: constrained, draftValues: [selection(key, text('3840x2160'))] })
    expect(cells(view, key)).toEqual(['1920 × 1080 px（幅 × 高さ）', '入力値を確認'])
    expect(view.querySelector(`[data-setting-key="${key}"]`)?.getAttribute('data-changed')).toBe('true')
    expect(view.textContent).not.toMatch(/3840|2160/)
    expectUnconfirmed(view)
  })

  it.each([
    ['minimum', '9007199254740993', '9007199254740992', '入力値を確認'],
    ['minimum', '9007199254740993', '9007199254740993', '9007199254740993 枚（目標）'],
    ['maximum', '9007199254740995', '9007199254740996', '入力値を確認'],
    ['maximum', '9007199254740995', '9007199254740995', '9007199254740995 枚（目標）'],
  ] as const)('compares integer %s %s exactly for draft %s', (boundName, bound, draft, expected) => {
    const key = 'thumbnails.candidateCount'
    const current = integer('9007199254740994')
    const constrained = withSetting(definition(key, current, { [boundName]: integer(bound) }), current)
    const view = renderPreview({ mode: 'snapshot', snapshot: constrained, draftValues: [selection(key, integer(draft))] })
    expect(cells(view, key)).toEqual(['9007199254740994 枚（目標）', expected])
    expectUnconfirmed(view)
  })

  it.each([
    ['below minimum', rational('1', '3'), '入力値を確認'],
    ['equal minimum with a different denominator', rational('2', '4'), '未取得・未対応'],
    ['equal maximum', rational('2', '1'), '未取得・未対応'],
    ['above maximum', rational('5', '2'), '入力値を確認'],
    ['zero denominator', rational('1', '0'), '入力値を確認'],
  ] as const)('distinguishes invalid rational drafts from unsupported valid formats: %s', (_name, draft, expected) => {
    const key = 'output.shortTargetDuration'
    const current = rational('3', '2')
    const constrained = withSetting(definition(key, current, { minimum: rational('1', '2'), maximum: rational('2', '1') }), current)
    const view = renderPreview({ mode: 'snapshot', snapshot: constrained, draftValues: [selection(key, draft)] })
    // Rational duration formatting is not supported; do not invent an integer seconds goal.
    expect(cells(view, key)).toEqual(['未取得・未対応', expected])
    expect(view.querySelector(`[data-setting-key="${key}"]`)?.getAttribute('data-changed')).toBe('true')
    expectUnconfirmed(view)
  })

  it.each([
    ['minimum', rational('9007199254740993', '9007199254740992'), rational('2', '1')],
    ['maximum', rational('9007199254740992', '9007199254740993'), rational('1', '2')],
  ] as const)('compares rational %s by exact cross multiplication, not rounded division', (boundName, bound, current) => {
    const key = 'output.shortTargetDuration'
    const constrained = withSetting(definition(key, current, { [boundName]: bound }), current)
    const view = renderPreview({ mode: 'snapshot', snapshot: constrained, draftValues: [selection(key, rational('1', '1'))] })
    expect(cells(view, key)).toEqual(['未取得・未対応', '入力値を確認'])
    expectUnconfirmed(view)
  })

  it.each([
    ['integer minimum kind', definition('project.verticalShortCandidateCount', integer('3'), { minimum: rational('0', '1') }), integer('2'), integer('3')],
    ['integer maximum kind', definition('project.verticalShortCandidateCount', integer('3'), { maximum: text('3') }), integer('2'), integer('3')],
    ['boolean bound', definition('subtitles.enabled', boolean(true), { minimum: boolean(false) }), boolean(false), boolean(true)],
    ['text bound', definition('output.landscapeResolution', text('1920x1080'), { maximum: text('3840x2160') }), text('1280x720'), text('1920x1080')],
    ['numeric allowlist', definition('project.verticalShortCandidateCount', integer('3'), { allowedTextValues: ['3'] }), integer('2'), integer('3')],
    ['boolean allowlist', definition('subtitles.enabled', boolean(true), { allowedTextValues: ['true'] }), boolean(false), boolean(true)],
    ['zero denominator bound', definition('output.shortTargetDuration', rational('3', '2'), { minimum: rational('1', '0') }), rational('3', '2'), rational('1', '1')],
  ] as const)('fails closed for malformed definition constraints without coercion or throwing: %s', (_name, setting, current, draft) => {
    const view = renderPreview({ mode: 'snapshot', snapshot: withSetting(setting, current), draftValues: [selection(setting.key, draft)] })
    expect(cells(view, setting.key)).toEqual(['未取得・未対応', '入力値を確認'])
    expect(view.querySelector(`[data-setting-key="${setting.key}"]`)?.getAttribute('data-changed')).toBe('true')
    expectUnconfirmed(view)
  })

  it('does not let an invalid draft make missing saved values available or fall back to defaults', () => {
    const view = renderPreview({
      mode: 'snapshot', snapshot: { ...snapshot, values: [] },
      draftValues: [selection('project.verticalShortCandidateCount', integer('7'))],
    })
    expect(view.querySelector('[data-preview-state]')?.getAttribute('data-preview-state')).toBe('unavailable')
    expect(cells(view, 'project.verticalShortCandidateCount')).toEqual(['未取得・未対応', '入力値を確認'])
    expect(view.querySelector('[role="status"]')?.getAttribute('data-changed')).toBe('true')
    expect(view.textContent).not.toMatch(/7 本|3 本/)
    expectUnconfirmed(view)
  })

  it('keeps an entirely missing value set unavailable even when definitions supply defaults', () => {
    const view = renderPreview({ mode: 'snapshot', snapshot: { ...snapshot, values: [] } })
    expect(view.querySelector('[data-preview-state]')?.getAttribute('data-preview-state')).toBe('unavailable')
    for (const key of SETTINGS_OUTCOME_CONTROL_KEYS) expect(cells(view, key)).toEqual(['未取得・未対応'])
    expect(view.textContent).not.toMatch(/1920|1080|3 本|3 枚|字幕あり/)
    expectUnconfirmed(view)
  })

  it('keeps missing snapshots and unsupported-only snapshots unavailable rather than successful', () => {
    const unknown = definition('evaluation.humanPassClaimEnabled', boolean(true))
    for (const value of [null, {
      ...snapshot, definitions: [unknown], definitionCount: 1,
      initialValues: [selection(unknown.key, unknown.defaultValue)], values: [selection(unknown.key, unknown.defaultValue)],
    }]) {
      const view = renderPreview({ mode: 'snapshot', snapshot: value, draftValues })
      expect(view.querySelector('[data-preview-state]')?.getAttribute('data-preview-state')).toBe('unavailable')
      expect(view.querySelector('[data-setting-key]')).toBeNull()
      expectUnconfirmed(view)
    }
  })

  it('updates only presentation on mounted prop changes and never calls HTTP or the native bridge', async () => {
    const fetch = vi.fn()
    const xhr = vi.fn()
    const nativePost = vi.fn()
    const nativeSubscribe = vi.fn()
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    vi.stubGlobal('fetch', fetch)
    vi.stubGlobal('XMLHttpRequest', xhr)
    vi.stubGlobal('chrome', { webview: { postMessage: nativePost, addEventListener: nativeSubscribe, removeEventListener: vi.fn() } })
    const post = vi.spyOn(window, 'postMessage').mockImplementation(() => {})
    const container = document.createElement('div')
    document.body.append(container)
    const root = createRoot(container)
    try {
      await act(async () => { root.render(<SettingsOutcomePreview mode="sourceOnly" sourceOnly={sourceOnly} />) })
      await act(async () => { root.render(<SettingsOutcomePreview mode="snapshot" snapshot={snapshot} draftValues={draftValues} />) })
      expect(cells(container, 'project.verticalShortCandidateCount')).toEqual(['2 本（目標）', '3 本（目標）'])
      const saved = { ...snapshot, values: draftValues, revision: 5, snapshotDigest: 'd'.repeat(64) }
      await act(async () => { root.render(<SettingsOutcomePreview mode="snapshot" snapshot={saved} draftValues={draftValues} />) })
      expect(cells(container, 'project.verticalShortCandidateCount')).toEqual(['3 本（目標）'])
      expect(container.querySelector('[role="status"]')?.getAttribute('data-changed')).toBe('false')
      expectUnconfirmed(container)
      await act(async () => { root.render(<SettingsOutcomePreview mode="snapshot" snapshot={{ ...saved, projectId: null }} />) })
      expect(container.textContent).toContain('全体の既定設定')
      await act(async () => { root.render(<SettingsOutcomePreview mode="sourceOnly" sourceOnly={sourceOnly} />) })
      expect(container.querySelector('[data-setting-key]')).toBeNull()
      expect(container.textContent).not.toMatch(/3 本|2560|1440/)
      expectUnconfirmed(container)
      await act(async () => { root.render(<SettingsOutcomePreview mode="snapshot" snapshot={null} />) })
      expect(container.querySelector('[data-setting-key]')).toBeNull()
      expectUnconfirmed(container)
    } finally {
      await act(async () => { root.unmount() })
      container.remove()
    }
    for (const call of [fetch, xhr, nativePost, nativeSubscribe, post]) expect(call).not.toHaveBeenCalled()
  })
})

describe('explicit settings regeneration confirmation', () => {
  it('never regenerates on saving/props changes, supports cancel and refuses changed identities or dirty tools', async () => {
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    const container = document.createElement('div'); document.body.append(container)
    const root = createRoot(container)
    const onConfirm = vi.fn(() => true)
    const params = { projectId: snapshot.projectId!, expectedVersionId: '80000000-0000-4000-8000-000000000005',
      expectedVersionDigest: 'a'.repeat(64), expectedSettingsRevision: snapshot.revision, expectedSettingsSnapshotDigest: snapshot.snapshotDigest }
    const render = async (options: { dirty?: boolean; revision?: number } = {}) => act(async () => root.render(
      <SettingsRegenerationAction params={{ ...params, expectedSettingsRevision: options.revision ?? params.expectedSettingsRevision }}
        pending={false} disabledReason={options.dirty ? '未保存の編集があります。' : null} onConfirm={onConfirm} />))
    const click = async (label: string) => act(async () => { within(container).getByRole<HTMLButtonElement>('button', { name: label }).click() })
    try {
      await render()
      await render({ revision: 5 })
      expect(onConfirm).not.toHaveBeenCalled()
      await click('保存済み設定で再生成'); await click('取消')
      expect(onConfirm).not.toHaveBeenCalled()
      expect(within(container).queryByRole('alertdialog')).toBeNull()
      await click('保存済み設定で再生成')
      await render({ revision: 6 })
      expect(within(container).getByRole<HTMLButtonElement>('button', { name: '確認して再生成' }).disabled).toBe(true)
      await click('取消')
      await render({ dirty: true })
      expect(within(container).getByRole<HTMLButtonElement>('button', { name: '保存済み設定で再生成' }).disabled).toBe(true)
      await render()
      await click('保存済み設定で再生成')
      const confirm = within(container).getByRole<HTMLButtonElement>('button', { name: '確認して再生成' })
      await act(async () => { confirm.click(); confirm.click() })
      expect(onConfirm).toHaveBeenCalledExactlyOnceWith(params)
    } finally { await act(async () => root.unmount()); container.remove() }
  })
})

function renderPreview(props: SettingsOutcomePreviewProps): HTMLDivElement {
  const container = document.createElement('div')
  container.innerHTML = renderToStaticMarkup(<SettingsOutcomePreview {...props} />)
  return container
}

function cells(container: HTMLElement, key: string): (string | null)[] {
  const row = container.querySelector(`[data-setting-key="${key}"]`)
  expect(row).not.toBeNull()
  return Array.from(row!.querySelectorAll('td')).map(cell => cell.textContent)
}

function expectUnconfirmed(container: HTMLElement) {
  expect(container.textContent).toContain('実際の出力は未更新')
  expect(container.textContent).toContain('生成・保存状況：未確認')
  expect(container.textContent).toContain('未評価（NOT EVALUATED）')
  expect(container.textContent).not.toMatch(/PASS|品質合格|生成済み|保存完了|顔検出/)
}

function definition(
  key: string,
  defaultValue: SettingValue,
  constraints: Partial<Pick<SettingDefinition, 'minimum' | 'maximum' | 'allowedTextValues'>> = {},
): SettingDefinition {
  return {
    key, defaultValue, group: key.startsWith('subtitles.') || key.startsWith('thumbnails.') ? 'subtitlesAndGraphics' : 'projectAndOutput',
    displayName: key, description: 'Test-only setting.', isAdvanced: false,
    unit: defaultValue.kind === 'integer' || defaultValue.kind === 'rational' ? key === 'output.shortTargetDuration' ? 'seconds' : 'count' : 'none',
    minimum: null, maximum: null, allowedTextValues: [], evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }],
    ...constraints,
  }
}

function withSetting(setting: SettingDefinition, current: SettingValue): AnySettingsSnapshot {
  return {
    ...snapshot,
    definitions: snapshot.definitions.map(item => item.key === setting.key ? setting : item),
    initialValues: snapshot.initialValues.map(item => item.key === setting.key ? selection(setting.key, setting.defaultValue) : item),
    values: snapshot.values.map(item => item.key === setting.key ? selection(setting.key, current) : item),
  }
}

function selection(key: string, value: SettingValue): SettingSelection { return { key, value } }
function text(textValue: string): SettingValue { return { kind: 'text', textValue } }
function integer(integerValue: string): SettingValue { return { kind: 'integer', integerValue } }
function boolean(booleanValue: boolean): SettingValue { return { kind: 'boolean', booleanValue } }
function rational(numerator: string, denominator: string): SettingValue { return { kind: 'rational', numerator, denominator } }