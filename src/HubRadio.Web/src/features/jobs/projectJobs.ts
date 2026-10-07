import type { PipelinePhase, PipelineVersionEntry } from '../workflow/pipelineProtocol'

/** FR-061: one Job (a source-only execution) of a Project, as shown in job lists. */
export interface ProjectJobRow {
  readonly draftId: string
  readonly projectId: string
  readonly projectName: string
  readonly jobNumber: number
  readonly executionId: string
  readonly phase: PipelinePhase
  readonly createdAt: string | null
  readonly isLatest: boolean
}

/** FR-061: versions are newest first, so the oldest entry is Job 1. */
export function buildProjectJobRows(
  project: Readonly<{ draftId: string, projectId: string, projectName: string }>,
  versions: readonly PipelineVersionEntry[],
): ProjectJobRow[] {
  return versions.map((entry, index) => ({
    draftId: project.draftId,
    projectId: project.projectId,
    projectName: project.projectName,
    jobNumber: versions.length - index,
    executionId: entry.executionId,
    phase: entry.phase,
    createdAt: entry.createdAt ?? null,
    isLatest: index === 0,
  }))
}

/** FR-061.10: the latest execution of a Project as read by pipeline.read, kept for cancel/retry (FR-061.11). */
export interface LatestJobSnapshot {
  readonly executionId: string
  readonly revision: number
  readonly phase: PipelinePhase
  readonly versionId: string | null
  readonly hasResult: boolean
}

/**
 * FR-061.10: folds the latest snapshot into one Project's version rows. A snapshot of a listed
 * execution updates that row's phase; an execution without a version becomes a new latest row.
 */
export function mergeLatestSnapshot(
  project: Readonly<{ draftId: string, projectId: string, projectName: string }>,
  rows: readonly ProjectJobRow[],
  latest: LatestJobSnapshot | null,
): ProjectJobRow[] {
  if (latest === null) return [...rows]
  if (rows.some(row => row.executionId === latest.executionId)) {
    return rows.map(row => row.executionId === latest.executionId ? { ...row, phase: latest.phase } : row)
  }
  return [{
    draftId: project.draftId,
    projectId: project.projectId,
    projectName: project.projectName,
    jobNumber: rows.length + 1,
    executionId: latest.executionId,
    phase: latest.phase,
    createdAt: null,
    isLatest: true,
  }, ...rows.map(row => ({ ...row, isLatest: false }))]
}

function timeOf(value: string | null): number | null {
  if (value === null) return null
  const time = new Date(value).getTime()
  return Number.isNaN(time) ? null : time
}

/**
 * FR-061 / FR-061.10: newest first across Projects. A latest row without a creation time (a Job
 * that has no version yet) is the newest of its Project and comes first; other unknown times last.
 * Does not mutate the input.
 */
export function sortAllJobRows(rows: readonly ProjectJobRow[]): ProjectJobRow[] {
  return [...rows].sort((a, b) => {
    const left = timeOf(a.createdAt)
    const right = timeOf(b.createdAt)
    const leftTop = left === null && a.isLatest
    const rightTop = right === null && b.isLatest
    if (leftTop !== rightTop) return leftTop ? -1 : 1
    if (left !== right) {
      if (left === null) return 1
      if (right === null) return -1
      return right - left
    }
    return a.projectName.localeCompare(b.projectName, 'ja') || b.jobNumber - a.jobNumber
  })
}

/** FR-061: localized creation time, or 不明 when absent or invalid. */
export function formatJobCreatedAt(createdAt: string | null): string {
  const time = timeOf(createdAt)
  return time === null ? '不明' : new Date(time).toLocaleString('ja-JP')
}
