import { describe, expect, it } from 'vitest'
import { fixtureId } from '../workflow/pipelineTestFixture'
import type { PipelineVersionEntry } from '../workflow/pipelineProtocol'
import { buildProjectJobRows, formatJobCreatedAt, mergeLatestSnapshot, sortAllJobRows, type LatestJobSnapshot, type ProjectJobRow } from './projectJobs'

const entry = (n: number, createdAt?: string): PipelineVersionEntry => ({
  versionId: fixtureId(n), versionDigest: 'a'.repeat(64), parentVersionId: null, executionId: fixtureId(100 + n),
  phase: 'Completed', hasResult: true, isCurrent: n === 1,
  ...(createdAt === undefined ? {} : { createdAt, settings: [], outputs: [] }),
})
const project = { draftId: 'draft-1', projectId: fixtureId(1), projectName: 'ラジオ' }
const row = (name: string, jobNumber: number, createdAt: string | null): ProjectJobRow => ({
  draftId: name, projectId: fixtureId(1), projectName: name, jobNumber, executionId: fixtureId(2), phase: 'Completed', createdAt, isLatest: false,
})

// FR-061 AC-036
describe('buildProjectJobRows', () => {
  it('numbers jobs from the oldest and marks only the newest as latest', () => {
    const rows = buildProjectJobRows(project, [entry(3, '2026-03-01T00:00:00Z'), entry(2), entry(1)])
    expect(rows.map(r => r.jobNumber)).toEqual([3, 2, 1])
    expect(rows.map(r => r.isLatest)).toEqual([true, false, false])
    expect(rows.map(r => r.createdAt)).toEqual(['2026-03-01T00:00:00Z', null, null])
    expect(rows[0]).toMatchObject({ draftId: 'draft-1', projectId: fixtureId(1), projectName: 'ラジオ', executionId: fixtureId(103), phase: 'Completed' })
  })

  it('returns nothing for no versions', () => {
    expect(buildProjectJobRows(project, [])).toEqual([])
  })
})

// FR-061 AC-036
describe('mergeLatestSnapshot (FR-061.10)', () => {
  const rows = buildProjectJobRows(project, [entry(2, '2026-02-01T00:00:00Z'), entry(1)])
  const snap = (executionId: string, phase: LatestJobSnapshot['phase']): LatestJobSnapshot => ({ executionId, revision: 1, phase, versionId: null, hasResult: false })

  it('returns the rows unchanged without a snapshot', () => {
    expect(mergeLatestSnapshot(project, rows, null)).toEqual(rows)
  })

  it('updates the phase of the row whose execution matches', () => {
    const merged = mergeLatestSnapshot(project, rows, snap(fixtureId(102), 'Failed'))
    expect(merged.map(r => r.phase)).toEqual(['Failed', 'Completed'])
    expect(merged.map(r => r.isLatest)).toEqual([true, false])
  })

  it('adds a version-less latest row with the next Job number and demotes the previous latest', () => {
    const merged = mergeLatestSnapshot(project, rows, snap(fixtureId(50), 'Composing'))
    expect(merged.map(r => [r.jobNumber, r.isLatest, r.createdAt, r.phase])).toEqual([[3, true, null, 'Composing'], [2, false, '2026-02-01T00:00:00Z', 'Completed'], [1, false, null, 'Completed']])
    expect(merged[0]).toMatchObject({ draftId: 'draft-1', projectId: fixtureId(1), projectName: 'ラジオ', executionId: fixtureId(50) })
    expect(mergeLatestSnapshot(project, [], snap(fixtureId(50), 'Queued')).map(r => r.jobNumber)).toEqual([1])
  })
})

// FR-061 AC-036
describe('sortAllJobRows', () => {
  it('puts undated latest rows first', () => {
    const latest = { ...row('Z', 3, null), isLatest: true }
    const sorted = sortAllJobRows([row('A', 1, '2026-02-01T00:00:00Z'), row('B', 1, null), latest])
    expect(sorted.map(r => `${r.projectName}${r.jobNumber}`)).toEqual(['Z3', 'A1', 'B1'])
  })
  it('sorts newest first, unknown dates last, then by name and job number', () => {
    const input = [
      row('B', 1, null), row('B', 1, '2026-01-01T00:00:00Z'), row('A', 1, '2026-02-01T00:00:00Z'),
      row('C', 1, '2026-02-01T00:00:00Z'), row('C', 2, '2026-02-01T00:00:00Z'), row('A', 2, null),
    ]
    const copy = [...input]
    const sorted = sortAllJobRows(input)
    expect(sorted.map(r => `${r.projectName}${r.jobNumber}`)).toEqual(['A1', 'C2', 'C1', 'B1', 'A2', 'B1'])
    expect(input).toEqual(copy)
  })
})

// FR-061 AC-036
describe('formatJobCreatedAt', () => {
  it('formats valid timestamps in ja-JP and falls back for null or invalid', () => {
    expect(formatJobCreatedAt('2026-02-01T00:00:00Z')).toBe(new Date('2026-02-01T00:00:00Z').toLocaleString('ja-JP'))
    expect(formatJobCreatedAt(null)).toBe('不明')
    expect(formatJobCreatedAt('not a date')).toBe('不明')
  })
})
