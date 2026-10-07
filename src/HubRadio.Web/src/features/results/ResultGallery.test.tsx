// @vitest-environment jsdom

import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { within } from '@testing-library/dom'
import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { SourceOnlyProjectSummary } from '../../bridge/projectProtocol'
import { parseDeliveryReadResponse, type DeliveryArtifact } from '../delivery/deliveryProtocol'
import {
  createDeliveryResponseValue, deliveryProjectId, deliveryRequestId,
  deliverySubtitleArtifactId, deliveryVersionDigest, deliveryVersionId, deliveryVideoArtifactId,
} from '../delivery/deliveryTestFixture'
import type { WorkflowWarningTarget } from '../workflow/workflowNavigation'
import { verifiedPipelineResponse } from '../workflow/pipelineProtocol'
import { createCompletedPipeline, createPipelineArtifact, fixtureId, pipelineResponse } from '../workflow/pipelineTestFixture'
import { ResultGallery, type ResultGalleryProps, type ResultGalleryWarning } from './ResultGallery'
import { deriveResultPresentation, type ResultKind, type ResultPresentation } from './resultPresentation'

// Read source directly: Vitest returns an empty CSS import when CSS processing is disabled.
const resultsCss = readFileSync(resolve(process.cwd(), 'src/features/results/results.css'), 'utf8')

// Test-only records; the component itself receives no default files or warnings.
const sourceOnlySummary: SourceOnlyProjectSummary = {
  profileId: '8d75a1fe-d10e-4aa7-87c5-3ac2d06d1f9b', profileVersion: '1.0.0',
  profileDigest: 'd'.repeat(64), settingsSnapshotDigest: 'a'.repeat(64),
  artifactSet: ['landscapeLongForm', 'verticalShortCandidates', 'subtitleFiles', 'thumbnailCandidates'],
}
const longArtifact = artifact(1, 'landscapeLongForm', 'video/mp4')
const shortArtifact = artifact(2, 'verticalShortCandidates', 'video/mp4')
const thumbnailArtifact = artifact(3, 'thumbnail', 'image/jpeg')
const binding = { projectId: deliveryProjectId, versionId: deliveryVersionId, versionDigest: deliveryVersionDigest }
const destinations = [
  { target: { ...binding, kind: 'preview', timeBasis: 'output', time: { value: '1', rate: '2' } }, label: '該当時刻へ' },
  { target: { ...binding, kind: 'settings', key: 'subtitle.fontSize' }, label: '設定へ' },
  { target: { ...binding, kind: 'evidence' }, label: '根拠へ' },
  { target: { ...binding, kind: 'editor', tool: 'timeline' }, label: '編集へ' },
] as const satisfies readonly { target: WorkflowWarningTarget; label: string }[]

describe('ResultGallery', () => {
  let container: HTMLDivElement
  let root: Root

  beforeEach(() => {
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    vi.unstubAllGlobals()
  })

  it('shows four unknown group shells without inventing records, counts or actions', () => {
    for (const presentation of [deriveResultPresentation(), deriveResultPresentation({
      sourceOnlySummary: null, delivery: null, jobMonitor: null, currentSettingsSnapshotDigest: null,
    })]) {
      renderGallery(presentation)
      const list = within(container).getByRole('list', { name: '成果物の種類' })
      expect(list.children).toHaveLength(4)
      expect(within(list).getAllByRole('article').map(item => within(item).getByRole('heading').textContent))
        .toEqual(['長尺動画', '短尺動画', '字幕・テキスト', 'サムネイル'])
      for (const group of within(list).getAllByRole('article')) {
        expect(group.getAttribute('data-result-state')).toBe('unavailable')
        expect(group.hasAttribute('data-result-id')).toBe(false)
        expect(field(group, '予定')).toBe('未取得')
        expect(field(group, '実生成')).toBe('未取得')
        expect(field(group, 'ローカル保存')).toBe('未確認')
      }
      expect(container.querySelector('button, img, video, canvas, [data-warning-id], [data-job-status]')).toBeNull()
      expect(container.textContent).not.toContain('0件')
      expect(container.textContent).not.toContain('保存済み')
      // FR-052: the evaluation status is shown in plain language.
      expect(within(container).getByText('未評価')).toBeTruthy()
      expect(container.textContent).not.toContain('NOT EVALUATED')
    }
  })

  it('shows source-only planned labels without deriving file counts, previews or saved states', () => {
    const onSelectArtifact = vi.fn()
    renderGallery(deriveResultPresentation({ sourceOnlySummary }), { onSelectArtifact })
    for (const kind of ['videoLong', 'videoShort', 'text', 'thumbnail'] as const) {
      const group = groupFor(kind)
      expect(group.getAttribute('data-result-state')).toBe('planned')
      expect(group.getAttribute('data-save-state')).toBe('unavailable')
      expect(within(group).getByText('作成予定')).toBeTruthy()
      expect(field(group, '実生成')).toBe('未取得')
    }
    expect(field(groupFor('videoShort'), '予定')).toBe('短尺動画候補')
    expect(field(groupFor('text'), '予定')).toBe('字幕ファイル')
    expect(field(groupFor('thumbnail'), '予定')).toBe('サムネイル候補')
    expect(container.querySelector('button, img, image')).toBeNull()
    expect(container.textContent).not.toContain('保存済み')
    expect(onSelectArtifact).not.toHaveBeenCalled()
  })

  it.each(['unavailable', 'forbidden', 'incomplete', 'mismatched', 'qualityControlFailed', null] as const)(
    'does not expose retained files when delivery availability is %s', status => {
      renderGallery(deriveResultPresentation({ sourceOnlySummary, delivery: { status, snapshot: delivery().snapshot } }))
      expect(container.querySelector('[data-save-state="saved"], button, img')).toBeNull()
      expect(container.textContent).not.toContain('保存済み')
      expect(container.textContent).not.toContain('PASS')
      // FR-052: the raw status stays in data-evaluation; the visible label is plain language.
      expect(container.querySelector('[data-evaluation]')?.getAttribute('data-evaluation')).toBe('NOT EVALUATED')
      expect(container.querySelector('[data-evaluation]')?.textContent).toBe('未評価')
      expect(field(groupFor('videoLong'), '実生成')).toBe('未取得')
    },
  )

  it('shows generated and registered local files separately from plans and evaluation', () => {
    const presentation = fullPresentation()
    const before = structuredClone(presentation)
    renderGallery(presentation)
    for (const kind of ['videoLong', 'videoShort', 'text', 'thumbnail'] as const) {
      const group = groupFor(kind)
      expect(group.getAttribute('data-result-state')).toBe('generated')
      expect(group.getAttribute('data-save-state')).toBe('saved')
      expect(field(group, '実生成')).toBe('1件')
      expect(field(group, 'ローカル保存')).toBe('保存済み')
      expect(within(group).getByText('生成済み')).toBeTruthy()
    }
    expect(field(groupFor('videoShort'), '予定')).toBe('短尺動画候補')
    // FR-052: the raw status stays in data-evaluation; the visible label is plain language.
    expect(container.querySelector('[data-evaluation]')?.getAttribute('data-evaluation')).toBe(presentation.evaluation)
    expect(presentation.evaluation).toBe('NOT EVALUATED')
    expect(container.querySelector('[data-evaluation]')?.textContent).toBe('未評価')
    expect(container.textContent).not.toMatch(/PASS|WARNING|FAIL|承認済み/)
    expect(presentation).toEqual(before)
  })

  it('does not infer saved from generated, planned or job completion', () => {
    const actual = fullPresentation()
    renderGallery({ ...actual, cards: actual.cards.map(card => ({ ...card, saveState: 'unavailable' })) })
    expect(within(groupFor('videoLong')).getByText('生成済み')).toBeTruthy()
    expect(field(groupFor('videoLong'), 'ローカル保存')).toBe('未確認')
    expect(container.textContent).not.toContain('保存済み')

    const planned = deriveResultPresentation({ sourceOnlySummary })
    renderGallery({ ...planned, jobStatus: 'succeeded' })
    expect(container.querySelector('[data-job-status="succeeded"]')?.textContent).toBe('完了')
    expect(container.querySelector('[data-result-state="generated"], [data-save-state="saved"], button')).toBeNull()
    // Even an inconsistent caller-provided saved flag cannot promote a plan with no files.
    renderGallery({ ...planned, cards: planned.cards.map(card => ({ ...card, saveState: 'saved' })) })
    expect(container.textContent).not.toContain('保存済み')
  })

  it('distinguishes known zero matching outputs from missing data without inventing rejection reasons', () => {
    renderGallery(deriveResultPresentation({ sourceOnlySummary, delivery: delivery() }))
    for (const kind of ['videoLong', 'videoShort', 'thumbnail'] as const) {
      const group = groupFor(kind)
      expect(group.getAttribute('data-result-state')).toBe('notProduced')
      expect(field(group, '実生成')).toBe('0件')
      expect(field(group, 'ローカル保存')).toBe('未確認')
      expect(within(group).getByText('この保存記録に該当成果物はありません。')).toBeTruthy()
      expect(group.querySelector('button, img')).toBeNull()
    }
    expect(container.textContent).not.toMatch(/却下|候補不足|品質不良/)
  })

  it('keeps stale plans distinct from evidenced generated files', () => {
    renderGallery(deriveResultPresentation({
      sourceOnlySummary, currentSettingsSnapshotDigest: 'b'.repeat(64), delivery: delivery(),
    }))
    expect(container.querySelector('[data-plan-state]')?.textContent).toBe('設定変更あり')
    expect(within(groupFor('thumbnail')).getByText('変更後未生成')).toBeTruthy()
    expect(field(groupFor('thumbnail'), '実生成')).toBe('0件')
    expect(field(groupFor('text'), 'ローカル保存')).toBe('保存済み')
  })

  it.each([['processing', '生成中'], ['failed', '失敗'], ['unavailable', '未取得']] as const)(
    'renders the supplied %s state without adding files or save evidence', (state, label) => {
      const presentation = deriveResultPresentation({ sourceOnlySummary })
      renderGallery({ ...presentation, cards: presentation.cards.map(card => ({ ...card, state })) })
      expect(within(groupFor('videoLong')).getByText(label, { selector: '.result-card-state' })).toBeTruthy()
      expect(field(groupFor('videoLong'), '実生成')).toBe('未取得')
      expect(container.querySelector('button, [data-save-state="saved"]')).toBeNull()
    },
  )

  it('keeps master and unknown image references outside the four primary groups', () => {
    const onSelectArtifact = vi.fn()
    const image = artifact(9, '<img src=x onerror=alert(1)>', 'image/png')
    renderGallery(deriveResultPresentation({ sourceOnlySummary, delivery: delivery([image]) }), { onSelectArtifact })
    const primary = within(container).getByRole('list', { name: '成果物の種類' })
    expect(primary.children).toHaveLength(4)
    expect(primary.textContent).not.toContain(deliveryVideoArtifactId)
    expect(primary.textContent).not.toContain(image.artifactId)
    expect(field(groupFor('videoLong'), '実生成')).toBe('0件')
    expect(field(groupFor('thumbnail'), '実生成')).toBe('0件')
    const additional = container.querySelector<HTMLDetailsElement>('.result-gallery-other')!
    expect(additional.open).toBe(false)
    act(() => { additional.open = true })
    openArtifactList('other')
    // FR-052: groups show a plain media label instead of raw IDs/artifact types; IDs are only forwarded on selection.
    expect(groupFor('video').textContent).toContain('動画')
    expect(groupFor('video').textContent).not.toContain(deliveryVideoArtifactId)
    expect(groupFor('other').textContent).toContain('画像')
    expect(groupFor('other').textContent).not.toContain(image.artifactType)
    expect(groupFor('other').textContent).not.toContain(image.artifactId)
    act(() => within(groupFor('video')).getByRole('button').click())
    act(() => within(groupFor('other')).getByRole('button').click())
    expect(onSelectArtifact.mock.calls).toEqual([[deliveryVideoArtifactId], [image.artifactId]])
    expect(container.querySelector('img, image, video, canvas')).toBeNull()
  })

  it.each([
    ['videoLong', '長尺動画 1を選択', longArtifact.artifactId, 'videoShort'],
    ['videoShort', '短尺動画 1を選択', shortArtifact.artifactId, 'text'],
    ['text', '字幕・テキスト 1を選択', deliverySubtitleArtifactId, 'thumbnail'],
    ['thumbnail', 'サムネイル候補 1を選択', thumbnailArtifact.artifactId, null],
  ] as const)('selects only the real %s ID with keyboard-accessible native buttons', async (kind, label, id, nextKind) => {
    const onSelectArtifact = vi.fn()
    renderGallery(fullPresentation(), { onSelectArtifact })
    expect(onSelectArtifact).not.toHaveBeenCalled()
    const user = userEvent.setup({ document })
    const group = groupFor(kind)
    const details = group.querySelector<HTMLDetailsElement>('.result-gallery-artifacts')!
    const summary = details.querySelector('summary')!
    expect(summary.isConnected).toBe(true)
    expect(details.open).toBe(false)
    await act(async () => user.click(summary))
    expect(details.open).toBe(true)
    expect(onSelectArtifact).not.toHaveBeenCalled()
    const button = within(group).getByRole<HTMLButtonElement>('button', { name: label })
    expect(button.type).toBe('button')
    expect(button.disabled).toBe(false)
    act(() => summary.focus())
    expect(document.activeElement).toBe(summary)
    await act(async () => user.tab())
    expect(document.activeElement).toBe(button)
    await act(async () => user.keyboard('[Enter]'))
    expect(onSelectArtifact).toHaveBeenCalledExactlyOnceWith(id)
    await act(async () => user.keyboard('[Space]'))
    expect(onSelectArtifact.mock.calls).toEqual([[id], [id]])

    await act(async () => user.tab({ shift: true }))
    expect(document.activeElement).toBe(summary)
    await act(async () => user.tab())
    expect(document.activeElement).toBe(button)
    const nextSummary = nextKind === null
      ? container.querySelector('.result-gallery-other > summary')
      : groupFor(nextKind).querySelector('.result-gallery-artifacts > summary')
    expect(nextSummary).not.toBeNull()
    await act(async () => user.tab())
    expect(document.activeElement).toBe(nextSummary)
    expect(onSelectArtifact.mock.calls).toEqual([[id], [id]])
  })

  it('disables selection without its callback and removes old controls when files disappear', () => {
    renderGallery(fullPresentation())
    openArtifactList('thumbnail')
    expect(within(groupFor('thumbnail')).getByRole<HTMLButtonElement>('button').disabled).toBe(true)
    const onSelectArtifact = vi.fn()
    renderGallery(fullPresentation(), { onSelectArtifact })
    openArtifactList('thumbnail')
    const oldButton = within(groupFor('thumbnail')).getByRole<HTMLButtonElement>('button')
    renderGallery(deriveResultPresentation({ sourceOnlySummary }), { onSelectArtifact })
    expect(container.querySelector('button')).toBeNull()
    act(() => oldButton.click())
    expect(onSelectArtifact).not.toHaveBeenCalled()
  })

  it('preserves the maximum actual candidate list and selects its first and last exact IDs', () => {
    // The existing protocol permits 1024 total files: two base files + 1022 thumbnails.
    const thumbnails = Array.from({ length: 1022 }, (_, index) => artifact(index + 1, 'thumbnail', 'image/jpeg'))
    const onSelectArtifact = vi.fn()
    renderGallery(deriveResultPresentation({ sourceOnlySummary, delivery: delivery(thumbnails) }), { onSelectArtifact })
    openArtifactList('thumbnail')
    const group = groupFor('thumbnail')
    expect(field(group, '実生成')).toBe('1022件')
    const buttons = within(group).getAllByRole('button')
    expect(buttons).toHaveLength(1022)
    act(() => { buttons[0]!.click(); buttons[1021]!.click() })
    expect(onSelectArtifact.mock.calls).toEqual([[thumbnails[0]!.artifactId], [thumbnails[1021]!.artifactId]])
    expect(within(group).getByText('画像参照は未取得です。')).toBeTruthy()
    expect(container.querySelector('img, image, video, canvas')).toBeNull()
  })

  it('renders only supplied warnings and forwards exact validated targets and record IDs', async () => {
    const onFollowWarning = vi.fn()
    const warnings: readonly ResultGalleryWarning[] = destinations.map(({ target }) => ({
      id: `test-record-${target.kind}`, message: `確認対象：${target.kind}`, target, available: true,
    }))
    renderGallery(deriveResultPresentation(), { warnings, onFollowWarning })
    expect(onFollowWarning).not.toHaveBeenCalled()
    const user = userEvent.setup({ document })
    for (const [index, { label }] of destinations.entries()) {
      const button = within(container).getByRole<HTMLButtonElement>('button', { name: label })
      expect(button.disabled).toBe(false)
      expect(document.getElementById(button.getAttribute('aria-describedby')!)?.textContent).toBe(warnings[index]!.message)
      act(() => button.focus())
      await act(async () => user.keyboard('[Enter]'))
      expect(onFollowWarning).toHaveBeenLastCalledWith(warnings[index]!.target, warnings[index]!.id)
    }
    expect(onFollowWarning).toHaveBeenCalledTimes(4)
    expect(container.querySelectorAll('[data-warning-id]')).toHaveLength(4)
    expect(container.querySelector('a, img')).toBeNull()
    // FR-052: the raw status stays in data-evaluation; the visible label is plain language.
    expect(container.querySelector('[data-evaluation]')?.getAttribute('data-evaluation')).toBe('NOT EVALUATED')
    expect(container.querySelector('[data-evaluation]')?.textContent).toBe('未評価')
  })

  it.each([
    { name: 'unvalidated target', target: destinations[0].target, available: undefined, callback: true },
    { name: 'unavailable target', target: destinations[0].target, available: false, callback: true },
    { name: 'missing target', target: null, available: true, callback: true },
    { name: 'missing callback', target: destinations[0].target, available: true, callback: false },
  ])('disables warning navigation for $name', async ({ target, available, callback }) => {
    const onFollowWarning = vi.fn()
    const warning: ResultGalleryWarning = { id: 'test-warning', message: '対象を確認してください。', target, available }
    renderGallery(deriveResultPresentation(), { warnings: [warning], onFollowWarning: callback ? onFollowWarning : undefined })
    const button = within(container).getByRole<HTMLButtonElement>('button')
    expect(button.disabled).toBe(true)
    const descriptions = button.getAttribute('aria-describedby')!.split(' ').map(id => document.getElementById(id)?.textContent)
    expect(descriptions[0]).toBe(warning.message)
    expect(descriptions[1]).toBe(callback ? '移動先を確認できません。' : '移動操作は未接続です。')
    const user = userEvent.setup({ document })
    await act(async () => user.click(button))
    await act(async () => user.tab())
    expect(document.activeElement).not.toBe(button)
    expect(onFollowWarning).not.toHaveBeenCalled()
  })

  it('honors withdrawn parent validation without synthesizing another destination', () => {
    const onFollowWarning = vi.fn()
    const warning: ResultGalleryWarning = { id: 'test-current-warning', message: '対象版を確認', target: destinations[0].target, available: true }
    renderGallery(deriveResultPresentation(), { warnings: [warning], onFollowWarning })
    const button = within(container).getByRole<HTMLButtonElement>('button')
    renderGallery(deriveResultPresentation(), { warnings: [{ ...warning, available: false }], onFollowWarning })
    expect(button.disabled).toBe(true)
    act(() => button.click())
    expect(onFollowWarning).not.toHaveBeenCalled()
  })

  it('makes no media/bridge requests and does not create warnings from approved QC or completed jobs', () => {
    const fetch = vi.fn()
    const postMessage = vi.fn()
    vi.stubGlobal('fetch', fetch)
    vi.stubGlobal('chrome', { webview: { postMessage } })
    renderGallery({ ...fullPresentation(), jobStatus: 'succeeded' })
    expect(container.querySelector('[data-warning-id], img, image, video, audio, canvas')).toBeNull()
    expect(container.textContent).not.toMatch(/PASS|WARNING|FAIL/)
    expect(fetch).not.toHaveBeenCalled()
    expect(postMessage).not.toHaveBeenCalled()
  })

  // FR-063.1: the Job list lives in JobResultsBrowser; the gallery shows only the current Job's results.
  it('does not list other Jobs itself', () => {
    renderGallery(fullPresentation())
    expect(container.querySelector('[data-version-id]')).toBeNull()
    expect(within(container).queryByRole('heading', { name: /^作成した動画|^Jobごとの仕上がり/ })).toBeNull()
    for (const kind of ['videoLong', 'videoShort', 'text', 'thumbnail'] as const) {
      expect(groupFor(kind).getAttribute('data-result-state')).toBe('generated')
    }
  })

  it('shows delivered ancillary artifacts and keeps the rendered outputs visible when they are absent', () => {
    const snapshot = createCompletedPipeline()
    snapshot.result!.artifacts.push(createPipelineArtifact(snapshot.result!, 'LandscapeThumbnail', 20))
    snapshot.result!.ancillaryArtifacts = [
      { kind: 'Transcript', artifact: { artifactId: fixtureId(91), contentDigest: '1'.repeat(64), byteSize: 128, kind: 'Delivery' } },
      { kind: 'UsedSourceRanges', artifact: { artifactId: fixtureId(92), contentDigest: '2'.repeat(64), byteSize: 64, kind: 'Delivery' } },
    ]
    const pipeline = verifiedPipelineResponse(pipelineResponse(snapshot))!.result.snapshot!
    renderGallery(deriveResultPresentation({ pipeline }))
    const section = within(container).getByRole('region', { name: '付帯成果物' })
    // FR-052: ancillary files are listed by plain name and size, without file IDs or digests.
    expect(within(section).getByText('文字起こし（128 バイト）')).toBeTruthy()
    expect(within(section).getByText('元動画のどこを使ったかの一覧（64 バイト）')).toBeTruthy()
    expect(section.textContent).not.toContain(fixtureId(91))
    expect(section.textContent).not.toContain('1'.repeat(64))
    expect(section.textContent).not.toMatch(/transcript\.json|used-source-ranges\.json/)

    const omitted = createCompletedPipeline()
    omitted.result!.artifacts.push(createPipelineArtifact(omitted.result!, 'LandscapeThumbnail', 20))
    omitted.result!.omissions = [
      { role: 'Transcript', reason: 'AncillaryExportFailed', renditionId: null },
      { role: 'UsedSourceRanges', reason: 'AncillaryExportFailed', renditionId: null },
    ]
    const omittedPipeline = verifiedPipelineResponse(pipelineResponse(omitted))!.result.snapshot!
    renderGallery(deriveResultPresentation({ pipeline: omittedPipeline }))
    const omittedSection = within(container).getByRole('region', { name: '付帯成果物' })
    expect(omittedSection.textContent).toContain('文字起こし・使用区間一覧が含まれていません')
    expect(omittedSection.querySelector('code')).toBeNull()
    // Both rendered outputs in this fixture stay visible and marked as produced.
    expect(container.querySelectorAll('[data-artifact-delivered="true"]')).toHaveLength(2)
    // FR-052: each omission is recorded in plain language without internal role/reason codes.
    const omissionRecords = Array.from(container.querySelectorAll('.result-gallery-other li'))
      .filter(item => item.textContent?.endsWith('今回は作成しませんでした'))
    expect(omissionRecords).toHaveLength(2)
    expect(container.textContent).not.toContain('AncillaryExportFailed')
    expect(container.textContent).not.toMatch(/Transcript:|UsedSourceRanges:/)
  })

  it('renders only verified source-only video/image URLs with per-artifact evaluation and delivery', () => {
    const snapshot = createCompletedPipeline()
    snapshot.result!.artifacts.push(createPipelineArtifact(snapshot.result!, 'LandscapeThumbnail', 20))
    const pipeline = verifiedPipelineResponse(pipelineResponse(snapshot))!.result.snapshot!
    const onSelectArtifact = vi.fn()
    renderGallery(deriveResultPresentation({ pipeline }), { onSelectArtifact })
    const video = container.querySelector('video')!
    const image = container.querySelector('img')!
    for (const element of [video, image]) {
      expect(new URL(element.src).origin).toBe('https://player-media.appassets.example')
      expect(new URL(element.src).pathname).toContain(`/source-only/${snapshot.projectId}/${snapshot.executionId}/`)
    }
    expect(video.controls).toBe(true)
    expect(video.autoplay).toBe(false)
    expect(container.querySelectorAll('[data-artifact-evaluation="NOT EVALUATED"]')).toHaveLength(2)
    expect(container.querySelectorAll('[data-artifact-delivered="true"]')).toHaveLength(2)
    act(() => within(container).getByRole('button', { name: '横長サムネイルをプレビュー' }).click())
    expect(onSelectArtifact).toHaveBeenCalledWith(snapshot.result!.artifacts[1]!.artifact.artifactId)
    const html = readFileSync(resolve(process.cwd(), 'index.html'), 'utf8')
    expect(html).toContain("img-src 'self' data: https://player-media.appassets.example;")
    expect(html).toContain('media-src https://player-media.appassets.example;')
    expect(html).toContain("connect-src 'none';")
    expect(html).not.toMatch(/img-src[^;]*(?:https:\s|\*)|media-src[^;]*\*/)
  })

  it('explains unavailable warning destinations and retains the exact returned warning selection', () => {
    renderGallery(deriveResultPresentation(), { warnings: [{ id: 'record-1', message: '音声の確認', target: null,
      available: false, unavailableReason: '音声編集の対象は提供されていません。' }], selectedWarningId: 'record-1' })
    expect(container.textContent).toContain('音声編集の対象は提供されていません。')
    expect(container.querySelector('[data-warning-id]')?.getAttribute('aria-current')).toBe('true')
    expect(within(container).getByRole<HTMLButtonElement>('button').disabled).toBe(true)
  })

  // Source rules only; this is not a claim of measured browser geometry.
  it('uses scoped theme tokens, a two-column grid and single-column reflow below 800px', () => {
    expect(resultsCss).toMatch(/grid-template-columns:\s*repeat\(2,\s*minmax\(0,\s*1fr\)\)/)
    expect(resultsCss).toMatch(/@media\s*\(width\s*<\s*800px\)[\s\S]*grid-template-columns:\s*minmax\(0,\s*1fr\)/)
    expect(resultsCss).toMatch(/:focus-visible[\s\S]*var\(--cp-accent\)/)
    expect(resultsCss).not.toMatch(/#[\da-f]{3,8}\b|(?:rgba?|hsla?)\(|url\(|@import|overflow:\s*hidden/i)
  })

  function renderGallery(presentation: ResultPresentation, props: Omit<ResultGalleryProps, 'presentation'> = {}): void {
    act(() => root.render(<ResultGallery presentation={presentation} {...props} />))
  }

  function groupFor(kind: ResultKind): HTMLElement {
    const group = container.querySelector<HTMLElement>(`[data-result-kind="${kind}"]`)
    if (group === null) throw new Error(`Missing result group: ${kind}`)
    return group
  }

  function openArtifactList(kind: ResultKind): void {
    const details = groupFor(kind).querySelector('details')
    if (details === null) throw new Error(`Missing artifact list: ${kind}`)
    act(() => { details.open = true })
  }
})

function field(group: HTMLElement, label: string): string | null | undefined {
  return within(group).getByText(label, { selector: 'dt' }).nextElementSibling?.textContent
}

function artifact(index: number, artifactType: string, mediaType: string): DeliveryArtifact {
  return {
    artifactId: `b0000000-0000-4000-8000-${index.toString().padStart(12, '0')}`,
    artifactType, mediaType, language: null, byteSize: '123', sha256: 'c'.repeat(64),
  }
}

function delivery(artifacts: readonly DeliveryArtifact[] = []) {
  const value = createDeliveryResponseValue(deliveryRequestId, false)
  value.result.snapshot.artifacts.push(...artifacts)
  const response = parseDeliveryReadResponse(value)
  if (response?.result.status !== 'available') throw new Error('Expected a validated delivery fixture.')
  return response.result
}

function fullPresentation(): ResultPresentation {
  return deriveResultPresentation({ sourceOnlySummary, delivery: delivery([longArtifact, shortArtifact, thumbnailArtifact]) })
}