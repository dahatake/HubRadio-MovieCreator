// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fixtureId } from '../workflow/pipelineTestFixture'
import type { PipelineVersionEntry, PipelineVersionOutput } from '../workflow/pipelineProtocol'
import { JobResultsBrowser } from '../results/JobResultsBrowser'
import { isJobSelectable, jobThumbnailLabel, jobThumbnailOutput, jobThumbnailStatus } from './JobThumbnail'
import { ProjectJobList } from './ProjectJobList'
import { buildProjectJobRows } from './projectJobs'

const thumb = (n: number, digest: string): PipelineVersionOutput =>
  ({ role: 'LandscapeThumbnail', artifactId: fixtureId(200 + n), contentDigest: digest.repeat(64), mediaType: 'image/png' })

const entry = (n: number, phase: PipelineVersionEntry['phase'], isCurrent: boolean, outputs: readonly PipelineVersionOutput[]): PipelineVersionEntry => ({
  versionId: fixtureId(n), versionDigest: 'c'.repeat(64), parentVersionId: null, executionId: fixtureId(100 + n),
  phase, hasResult: outputs.length > 0, isCurrent, createdAt: `2026-02-0${n}T00:00:00Z`, settings: [], outputs,
})

const projectId = fixtureId(900)

describe('Job thumbnails', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })
  const thumbnails = () => [...container.querySelectorAll<HTMLElement>('[data-job-thumbnail-phase]')]

  const versions = [entry(3, 'Rendering', false, []), entry(2, 'Completed', true, [thumb(2, 'b')]), entry(1, 'Failed', false, [])]

  // FR-131 AC-133
  it('shows a substitute for a Job without a thumbnail and keeps the other Jobs\' thumbnails', () => {
    act(() => root.render(<ProjectJobList versions={versions} status="available" projectId={projectId} />))
    const all = thumbnails()
    expect(all).toHaveLength(3)
    expect(all.map(t => t.querySelector('img') !== null)).toEqual([false, true, false])
    expect(all[0]!.textContent).toContain('サムネイルなし（生成中）')
    expect(all[2]!.textContent).toContain('サムネイルなし（失敗）')
    expect(all[1]!.querySelector('img')!.getAttribute('src')).toContain(fixtureId(202))
  })

  // FR-131 AC-133
  it('falls back to the substitute when the picture cannot be read, without touching other Jobs', () => {
    const two = [entry(2, 'Completed', true, [thumb(2, 'b')]), entry(1, 'Completed', false, [thumb(1, 'a')])]
    act(() => root.render(<ProjectJobList versions={two} status="available" projectId={projectId} />))
    const first = thumbnails()[0]!.querySelector('img')!
    act(() => { first.dispatchEvent(new Event('error')) })
    const after = thumbnails()
    expect(after[0]!.querySelector('img')).toBeNull()
    expect(after[0]!.textContent).toContain('サムネイルなし')
    expect(after[1]!.querySelector('img')).not.toBeNull()
  })

  // FR-131 AC-134
  it('shows the latest picture after a Job completes or is re-created, and never swaps Jobs', () => {
    const before = [entry(2, 'Rendering', false, []), entry(1, 'Completed', true, [thumb(1, 'a')])]
    act(() => root.render(<ProjectJobList versions={before} status="available" projectId={projectId} />))
    expect(thumbnails()[0]!.querySelector('img')).toBeNull()
    const second = entry(2, 'Completed', true, [thumb(2, 'b')])
    const after = [second, entry(1, 'Completed', false, [thumb(1, 'c')])]
    act(() => root.render(<ProjectJobList versions={after} status="available" projectId={projectId} />))
    const imgs = thumbnails().map(t => t.querySelector('img')!.getAttribute('src')!)
    expect(imgs[0]).toContain(fixtureId(202) + '/' + 'b'.repeat(64))
    expect(imgs[1]).toContain(fixtureId(201) + '/' + 'c'.repeat(64))
    expect(imgs[1]).not.toBe(imgs[0])
  })

  // FR-131 AC-134
  it('carries each version\'s outputs into the Job rows', () => {
    const rows = buildProjectJobRows({ draftId: fixtureId(1), projectId, projectName: 'p' }, versions)
    expect(rows.map(row => row.outputs?.length)).toEqual([0, 1, 0])
  })

  // FR-131 AC-135
  it('names every thumbnail with the Job, its time and its state in words, and makes it a tab stop', () => {
    act(() => root.render(<ProjectJobList versions={versions} status="available" projectId={projectId} />))
    const labels = thumbnails().map(t => t.getAttribute('aria-label')!)
    expect(labels[0]).toMatch(/^サムネイル: Job 3、作成日時 .+、生成中$/)
    expect(labels[1]).toContain('Job 2')
    expect(labels[1]).toContain('完了')
    expect(labels[2]).toContain('失敗')
    expect(new Set(labels).size).toBe(3)
    for (const t of thumbnails()) {
      expect(t.getAttribute('role')).toBe('img')
      expect(t.tabIndex).toBe(0)
    }
    expect(jobThumbnailStatus('Canceled')).toBe('取消')
    expect(jobThumbnailLabel({ jobName: 'Job 1', createdAt: null, phase: 'Canceled' })).toContain('不明')
  })

  // FR-131 AC-133 AC-135
  it('shows thumbnails in the results rail and lets the comparison choose only completed Jobs', () => {
    const list = [entry(3, 'Rendering', false, []), entry(2, 'Completed', true, [thumb(2, 'b')]), entry(1, 'Completed', false, [thumb(1, 'a')])]
    act(() => root.render(<JobResultsBrowser versions={list} projectId={projectId} currentResults={null} view="job" />))
    expect(thumbnails()).toHaveLength(3)
    act(() => root.render(<JobResultsBrowser versions={list} projectId={projectId} currentResults={null} view="compare" />))
    const picker = container.querySelector('ul.job-thumbnail-picker')!
    const buttons = [...picker.querySelectorAll('button')]
    const running = buttons.find(b => b.textContent!.includes('Job 3'))!
    expect(running.disabled).toBe(true)
    expect(running.textContent).toContain('選べません')
    expect(running.querySelector('[data-job-thumbnail-empty]')).not.toBeNull()
    expect(buttons.filter(b => !b.disabled).length).toBeGreaterThanOrEqual(2)
    const options = [...container.querySelectorAll('select option')].filter(o => o.textContent!.includes('Job 3'))
    expect(options.every(o => (o as HTMLOptionElement).disabled)).toBe(true)
    expect(isJobSelectable('Rendering')).toBe(false)
  })

  it('picks the landscape picture first and ignores non-image outputs', () => {
    const video: PipelineVersionOutput = { role: 'LandscapeBurnIn', artifactId: fixtureId(1), contentDigest: 'd'.repeat(64), mediaType: 'video/mp4' }
    expect(jobThumbnailOutput([video])).toBeNull()
    expect(jobThumbnailOutput(undefined)).toBeNull()
    expect(jobThumbnailOutput([video, thumb(1, 'a')])?.artifactId).toBe(fixtureId(201))
  })
})
