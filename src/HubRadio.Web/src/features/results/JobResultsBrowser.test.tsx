// @vitest-environment jsdom
import userEvent from '@testing-library/user-event'
import { within } from '@testing-library/dom'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fixtureId } from '../workflow/pipelineTestFixture'
import type { PipelineVersionEntry } from '../workflow/pipelineProtocol'
import { compareJobSettings, JobResultsBrowser, jobOutputItems, type JobResultsBrowserProps } from './JobResultsBrowser'

const integer = (value: string) => ({ kind: 'integer', integerValue: value }) as const
const projectId = fixtureId(30)
const mediaBase = `https://player-media.appassets.example/source-only/${projectId}`

const current: PipelineVersionEntry = Object.freeze({
  versionId: fixtureId(11), versionDigest: 'a'.repeat(64), parentVersionId: fixtureId(12),
  executionId: fixtureId(13), phase: 'Completed', hasResult: true, isCurrent: true,
})
const past: PipelineVersionEntry = Object.freeze({
  versionId: fixtureId(12), versionDigest: 'b'.repeat(64), parentVersionId: null,
  executionId: fixtureId(14), phase: 'Completed', hasResult: true, isCurrent: false,
})
const older: PipelineVersionEntry = Object.freeze({
  ...past, createdAt: '2026-10-06T12:00:00.0000000+00:00',
  settings: [{ key: 'subtitles.readingSpeedMaximum', value: integer('7') }, { key: 'subtitles.foregroundColor', value: { kind: 'text', textValue: '#FFFFFF' } as const }],
  outputs: [
    { role: 'LandscapeBurnIn', artifactId: fixtureId(31), contentDigest: 'c'.repeat(64), mediaType: 'video/mp4' },
    { role: 'LandscapeThumbnail', artifactId: fixtureId(35), contentDigest: 'f'.repeat(64), mediaType: 'image/png' },
  ] as PipelineVersionEntry['outputs'],
})
const newer: PipelineVersionEntry = Object.freeze({
  ...current, createdAt: '2026-10-06T13:00:00.0000000+00:00',
  settings: [{ key: 'subtitles.readingSpeedMaximum', value: integer('9') }, { key: 'subtitles.foregroundColor', value: { kind: 'text', textValue: '#FFFFFF' } as const }],
  outputs: [
    { role: 'LandscapeThumbnail', artifactId: fixtureId(32), contentDigest: 'd'.repeat(64), mediaType: 'image/png' },
    { role: 'VerticalBurnIn', artifactId: fixtureId(33), contentDigest: 'e'.repeat(64), mediaType: 'video/mp4' },
    { role: 'VerticalBurnIn', artifactId: fixtureId(36), contentDigest: '1'.repeat(64), mediaType: 'video/mp4' },
    { role: 'LandscapeBurnIn', artifactId: fixtureId(34), contentDigest: '2'.repeat(64), mediaType: 'video/mp4' },
  ] as PipelineVersionEntry['outputs'],
})

describe('JobResultsBrowser', () => {
  let container: HTMLElement
  let root: Root

  beforeEach(() => {
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    vi.restoreAllMocks()
  })

  function render(props: Partial<JobResultsBrowserProps>): void {
    act(() => root.render(<JobResultsBrowser versions={null} projectId={projectId} currentResults={<p>いまの版の詳細</p>} {...props} />))
  }
  const rail = () => within(container).getByRole('list', { name: 'Jobの一覧' })
  const railButton = (name: RegExp) => within(rail()).getByRole('button', { name })

  // FR-063 AC-038.1
  it('lists the Jobs newest first and shows the current Job results until another Job is chosen', () => {
    render({ versions: [newer, older] })
    expect(within(container).getByRole('heading', { name: 'Jobごとの仕上がり（2件）' })).toBeTruthy()
    const items = rail().querySelectorAll('li')
    expect(items).toHaveLength(2)
    expect(items[0]!.getAttribute('data-version-current')).toBe('true')
    expect(items[0]!.textContent).toContain('Job 2（いまの版）')
    expect(items[0]!.textContent).toContain('設定の変更 1項目')
    expect(items[1]!.textContent).toContain('Job 1')
    expect(items[1]!.textContent).toContain('最初のJob')
    // The Job picture is the first thumbnail; there is no picture text when it is absent.
    expect(items[0]!.querySelector('img')!.getAttribute('src')).toBe(`${mediaBase}/${newer.executionId}/${fixtureId(32)}/${'d'.repeat(64)}`)
    expect(railButton(/^.*Job 2（いまの版）/).getAttribute('aria-pressed')).toBe('true')
    expect(container.textContent).toContain('Job 2（いまの版）の仕上がり')
    expect(container.textContent).toContain('いまの版の詳細')
    expect(container.textContent).not.toMatch(/[0-9a-f]{64}/)
    expect(container.textContent).not.toContain(older.versionId)
  })

  // FR-063 AC-038.2 (FR-055.2, FR-055.3 / AC-030.2)
  it('shows a past Job with its videos and pictures and only the settings the settings screen offers', async () => {
    render({ versions: [newer, older] })
    await act(async () => { railButton(/Job 1/).click() })
    expect(railButton(/Job 1/).getAttribute('aria-pressed')).toBe('true')
    expect(container.textContent).not.toContain('いまの版の詳細')
    expect(container.textContent).toContain('Job 1の仕上がり')
    expect(container.textContent).toContain('Job 2（いまの版）の仕上がりで確認できます')
    const outputs = within(container).getByRole('list', { name: 'Job 1の動画・画像' })
    const video = outputs.querySelector('video')!
    expect(video.getAttribute('preload')).toBe('none')
    expect(video.getAttribute('src')).toBe(`${mediaBase}/${older.executionId}/${fixtureId(31)}/${'c'.repeat(64)}`)
    expect(outputs.querySelector('img')!.getAttribute('alt')).toBe('Job 1・横長サムネイル')
    expect(older.settings).toBeDefined()
    // Keys the settings screen does not offer are not listed.
    expect(container.querySelector('[data-setting-key="subtitles.foregroundColor"]')).toBeNull()
    expect(container.querySelector('[data-setting-key="subtitles.readingSpeedMaximum"]')!.getAttribute('data-setting-changed')).toBe('false')

    await act(async () => { railButton(/Job 2/).click() })
    expect(container.textContent).toContain('いまの版の詳細')
  })

  // FR-063 AC-038.2 (FR-055.3 / AC-030.2): a middle Job marks the setting changed from the previous Job.
  it('marks a past Job setting that changed from the previous Job', async () => {
    const middle: PipelineVersionEntry = Object.freeze({
      ...older, versionId: fixtureId(40), executionId: fixtureId(41), createdAt: '2026-10-06T12:30:00.0000000+00:00',
      settings: [{ key: 'subtitles.readingSpeedMaximum', value: integer('9') }],
    })
    const oldest: PipelineVersionEntry = Object.freeze({ ...older, versionId: fixtureId(42), executionId: fixtureId(43) })
    render({ versions: [newer, middle, oldest] })
    expect(rail().querySelectorAll('li')[1]!.textContent).toContain('設定の変更 1項目')
    await act(async () => { railButton(/Job 2(?!（)/).click() })
    expect(container.textContent).toContain('Job 2の仕上がり')
    const changed = container.querySelector('[data-setting-key="subtitles.readingSpeedMaximum"]')!
    expect(changed.getAttribute('data-setting-changed')).toBe('true')
    expect(changed.textContent).toContain('9（前の動画: 7 から変更）')
  })

  it('numbers repeated short videos and orders outputs long, short, then pictures', () => {
    expect(jobOutputItems(newer).map(item => item.label)).toEqual([
      '長尺・字幕焼き込み版', '短尺・字幕焼き込み版 1', '短尺・字幕焼き込み版 2', '横長サムネイル',
    ])
    expect(jobOutputItems(current)).toEqual([])
  })

  it('separates a not-yet-read list, an empty read, an unavailable read and old records', async () => {
    render({ versions: null })
    expect(container.textContent).toContain('Jobの一覧を取得しています。')
    expect(container.textContent).toContain('いまの版の詳細')
    render({ versions: [] })
    expect(container.textContent).toContain('このProjectのJobの記録はまだありません。')
    render({ versions: null, unavailableReason: 'Jobの一覧を取得できませんでした。' })
    expect(container.textContent).toContain('Jobの一覧を取得できませんでした。')
    expect(container.textContent).not.toContain('取得しています')

    render({ versions: [current, past] })
    await act(async () => { railButton(/Job 1/).click() })
    expect(container.textContent).toContain('このJobの成果物の情報はありません（古い記録です）。')
    expect(container.textContent).toContain('このJobの設定の記録はありません。')
    expect(container.querySelector('video, dl')).toBeNull()
  })

  // FR-055.4 / AC-030.3
  it('restores a past Job only after confirmation, with the keyboard', async () => {
    const onRestore = vi.fn()
    render({ versions: [current, past], onRestore })
    const user = userEvent.setup({ document })
    await act(async () => { railButton(/Job 1/).click() })

    const start = within(container).getByRole('button', { name: 'この動画から作り直す' })
    start.focus()
    await act(async () => { await user.keyboard('{Enter}') })
    expect(onRestore).not.toHaveBeenCalled()
    expect(container.textContent).toContain('この動画の構成から新しい動画を作りますか')

    const cancel = within(container).getByRole('button', { name: '取り消す' })
    cancel.focus()
    await act(async () => { await user.keyboard('{Enter}') })
    expect(onRestore).not.toHaveBeenCalled()

    within(container).getByRole('button', { name: 'この動画から作り直す' }).focus()
    await act(async () => { await user.keyboard(' ') })
    within(container).getByRole('button', { name: '作り直す' }).focus()
    await act(async () => { await user.keyboard('{Enter}') })
    expect(onRestore).toHaveBeenCalledExactlyOnceWith(past.versionId, past.versionDigest)
    expect(container.textContent).toContain('新しいJobを作っても、前のJobと仕上がりは消えません。')
  })

  it('does not restore while an operation is pending or blocked, and shows why', async () => {
    const onRestore = vi.fn()
    render({ versions: [current, past], disabledReason: '未保存の編集があります。', onRestore })
    await act(async () => { railButton(/Job 1/).click() })
    expect(container.textContent).toContain('未保存の編集があります。')
    expect(within(container).getByRole('button', { name: 'この動画から作り直す' }).hasAttribute('disabled')).toBe(true)
    render({ versions: [current, past], pending: true, onRestore })
    expect(within(container).getByRole('button', { name: 'この動画から作り直す' }).hasAttribute('disabled')).toBe(true)
    expect(onRestore).not.toHaveBeenCalled()
  })

  it('returns to the current Job when the chosen Job leaves the list', async () => {
    render({ versions: [newer, older] })
    await act(async () => { railButton(/Job 1/).click() })
    render({ versions: [newer] })
    expect(container.textContent).toContain('いまの版の詳細')
  })

  // FR-063 AC-038.3
  it('compares two Jobs side by side with linked playback and a settings table', async () => {
    const onViewChange = vi.fn()
    render({ versions: [newer, older], onViewChange })
    await act(async () => { within(container).getByRole('button', { name: 'Jobを比較' }).click() })
    expect(onViewChange).toHaveBeenCalledWith('compare')
    expect(container.querySelector('[data-job-results-view]')!.getAttribute('data-job-results-view')).toBe('compare')
    expect(within(container).queryByRole('list', { name: 'Jobの一覧' })).toBeNull()

    const leftSelect = within(container).getByLabelText('1つ目に表示するJob') as HTMLSelectElement
    const rightSelect = within(container).getByLabelText('2つ目に表示するJob') as HTMLSelectElement
    expect(leftSelect.selectedOptions[0]!.textContent).toBe('Job 1')
    expect(rightSelect.selectedOptions[0]!.textContent).toBe('Job 2（いまの版）')
    // Two Jobs exist, so the count cannot change.
    expect((within(container).getByLabelText('比べるJobの数') as HTMLSelectElement).disabled).toBe(true)
    const outputSelect = within(container).getByLabelText('比べる成果物') as HTMLSelectElement
    // The default is the first output both Jobs have.
    expect(outputSelect.selectedOptions[0]!.textContent).toBe('長尺・字幕焼き込み版')
    const left = within(container).getByRole('region', { name: '1つ目: Job 1' })
    const right = within(container).getByRole('region', { name: '2つ目: Job 2（いまの版）' })
    const leftVideo = left.querySelector('video')!
    const rightVideo = right.querySelector('video')!
    expect(leftVideo.getAttribute('src')).toBe(`${mediaBase}/${older.executionId}/${fixtureId(31)}/${'c'.repeat(64)}`)
    expect(rightVideo.getAttribute('src')).toBe(`${mediaBase}/${newer.executionId}/${fixtureId(34)}/${'2'.repeat(64)}`)

    // Linked playback: playing, pausing and seeking one video moves the other.
    let leftPaused = true
    let rightPaused = true
    const times = new Map<HTMLVideoElement, number>([[leftVideo, 0], [rightVideo, 0]])
    for (const media of [leftVideo, rightVideo]) {
      Object.defineProperty(media, 'currentTime', { configurable: true, get: () => times.get(media), set: (value: number) => { times.set(media, value) } })
    }
    Object.defineProperty(leftVideo, 'paused', { configurable: true, get: () => leftPaused })
    Object.defineProperty(rightVideo, 'paused', { configurable: true, get: () => rightPaused })
    const leftPlay = vi.fn(() => { leftPaused = false; return Promise.resolve() })
    const rightPlay = vi.fn(() => { rightPaused = false; return Promise.resolve() })
    const rightPause = vi.fn(() => { rightPaused = true })
    leftVideo.play = leftPlay
    rightVideo.play = rightPlay
    leftVideo.pause = vi.fn(() => { leftPaused = true })
    rightVideo.pause = rightPause
    await act(async () => { within(container).getByRole('button', { name: '2つを同時に再生' }).click() })
    expect(leftPlay).toHaveBeenCalledTimes(1)
    expect(rightPlay).toHaveBeenCalledTimes(1)
    await act(async () => { leftVideo.dispatchEvent(new Event('pause')) })
    expect(rightPause).toHaveBeenCalledTimes(1)
    leftVideo.currentTime = 12
    await act(async () => { leftVideo.dispatchEvent(new Event('seeked')) })
    expect(rightVideo.currentTime).toBe(12)
    leftPaused = true
    rightPaused = true
    await act(async () => { leftVideo.dispatchEvent(new Event('play')) })
    expect(rightPlay).toHaveBeenCalledTimes(2)

    // Unlinked: the other video is left alone.
    await act(async () => { within(container).getByRole('checkbox', { name: /再生位置を2つで合わせる/ }).click() })
    leftVideo.currentTime = 30
    await act(async () => { leftVideo.dispatchEvent(new Event('seeked')) })
    expect(rightVideo.currentTime).toBe(12)

    // The settings table shows only differing settings until all are requested.
    const table = within(container).getByRole('table', { name: '設定の比較' })
    const rows = table.querySelectorAll('tbody tr')
    expect(rows).toHaveLength(1)
    expect(rows[0]!.getAttribute('data-setting-key')).toBe('subtitles.readingSpeedMaximum')
    expect(rows[0]!.textContent).toContain('（違い）')
    expect([...rows[0]!.querySelectorAll('td')].map(cell => cell.textContent)).toEqual(['7', '9'])
    expect([...table.querySelectorAll('thead th')].map(cell => cell.textContent)).toEqual(['項目', 'Job 1', 'Job 2（いまの版）'])
    expect(container.textContent).not.toMatch(/[0-9a-f]{64}/)
  })

  it('switches the compared output and reports Jobs that lack it', async () => {
    render({ versions: [newer, older], view: 'compare' })
    const outputSelect = within(container).getByLabelText('比べる成果物') as HTMLSelectElement
    expect([...outputSelect.options].map(option => option.textContent)).toEqual([
      '長尺・字幕焼き込み版', '短尺・字幕焼き込み版 1', '短尺・字幕焼き込み版 2', '横長サムネイル',
    ])
    const user = userEvent.setup({ document })
    await act(async () => { await user.selectOptions(outputSelect, 'VerticalBurnIn#2') })
    expect(within(container).getByRole('region', { name: '1つ目: Job 1' }).textContent).toContain('このJobにはこの成果物がありません。')
    expect(within(container).getByRole('region', { name: '2つ目: Job 2（いまの版）' }).querySelector('video')!.getAttribute('aria-label'))
      .toBe('Job 2（いまの版）・短尺・字幕焼き込み版 2')
    // The playback controls need two videos.
    expect(within(container).queryByRole('button', { name: '2つを同時に再生' })).toBeNull()

    await act(async () => { await user.selectOptions(within(container).getByLabelText('1つ目に表示するJob'), newer.versionId) })
    expect(container.textContent).toContain('同じJobが選ばれています。')
    expect(container.textContent).toContain('2つのJobで違う設定はありません。')
    await act(async () => { within(container).getByRole('checkbox', { name: '違う項目だけ表示' }).click() })
    expect(within(container).getByRole('table', { name: '設定の比較' }).querySelectorAll('tbody tr')).toHaveLength(1)
  })

  it('explains that a comparison needs two Jobs', () => {
    render({ versions: [newer], view: 'compare' })
    expect(container.textContent).toContain('比較するには、Jobが2つ以上必要です。')
    render({ versions: [current, past], view: 'compare' })
    expect(container.textContent).toContain('設定の記録が無いJobがあるため、設定を比べられません。')
  })

  it('marks settings recorded for only one Job', () => {
    const withExtra: PipelineVersionEntry = { ...newer, settings: [...newer.settings!, { key: 'editing.pauseTightening', value: { kind: 'text', textValue: 'standard' } }] }
    const rows = compareJobSettings([older, withExtra])
    const extra = rows.find(row => row.key === 'editing.pauseTightening')
    expect(extra?.values).toEqual(['（記録なし）', '標準（0.7秒以上の間を詰める）'])
    expect(extra?.different).toBe(true)
    expect(rows.find(row => row.key === 'subtitles.foregroundColor')).toBeUndefined()
  })

  // FR-063 AC-038.3 (Q-011 B): up to four Jobs in a 2×2 grid with one settings table column each.
  it('compares up to four Jobs at once with linked playback and a four-column settings table', async () => {
    const make = (n: number, speed: string, hour: string): PipelineVersionEntry => Object.freeze({
      versionId: fixtureId(60 + n), versionDigest: String(n).repeat(64), parentVersionId: null, executionId: fixtureId(70 + n),
      phase: 'Completed', hasResult: true, isCurrent: n === 5, createdAt: `2026-10-06T${hour}:00:00.0000000+00:00`,
      settings: [{ key: 'subtitles.readingSpeedMaximum', value: integer(speed) }],
      outputs: [{ role: 'LandscapeBurnIn', artifactId: fixtureId(80 + n), contentDigest: String(n).repeat(64), mediaType: 'video/mp4' }],
    }) as PipelineVersionEntry
    const jobs = [make(5, '9', '15'), make(4, '8', '14'), make(3, '7', '13'), make(2, '6', '12'), make(1, '5', '11')]
    render({ versions: jobs, view: 'compare' })
    const user = userEvent.setup({ document })
    const count = within(container).getByLabelText('比べるJobの数') as HTMLSelectElement
    expect([...count.options].map(option => option.textContent)).toEqual(['2つ', '3つ', '4つ'])
    await act(async () => { await user.selectOptions(count, '4') })

    // The newest four Jobs, the oldest of them first.
    const panes = [...container.querySelectorAll('.job-compare-pane')]
    expect(panes.map(pane => pane.querySelector('h3')!.textContent)).toEqual(['Job 2', 'Job 3', 'Job 4', 'Job 5（いまの版）'])
    expect(within(container).getByLabelText('4つ目に表示するJob')).toBeTruthy()
    const videos = panes.map(pane => pane.querySelector('video')!)
    expect(videos).toHaveLength(4)

    const paused = videos.map(() => true)
    const plays = videos.map((video, index) => {
      Object.defineProperty(video, 'paused', { configurable: true, get: () => paused[index] })
      const play = vi.fn(() => { paused[index] = false; return Promise.resolve() })
      video.play = play
      video.pause = vi.fn(() => { paused[index] = true })
      return play
    })
    await act(async () => { videos[2]!.dispatchEvent(new Event('play')) })
    expect(plays.map(play => play.mock.calls.length)).toEqual([1, 1, 0, 1])
    await act(async () => { within(container).getByRole('button', { name: '4つを一時停止' }).click() })
    expect(paused).toEqual([true, true, true, true])

    const table = within(container).getByRole('table', { name: '設定の比較' })
    expect([...table.querySelectorAll('thead th')].map(cell => cell.textContent)).toEqual(['項目', 'Job 2', 'Job 3', 'Job 4', 'Job 5（いまの版）'])
    expect([...table.querySelectorAll('tbody tr')[0]!.querySelectorAll('td')].map(cell => cell.textContent)).toEqual(['6', '7', '8', '9'])
    expect(within(container).getByRole('checkbox', { name: /再生位置を4つで合わせる/ })).toBeTruthy()
  })
})
