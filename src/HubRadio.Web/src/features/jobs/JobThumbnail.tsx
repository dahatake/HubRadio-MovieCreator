import { useState } from 'react'
import { projectThumbnailUrl } from '../../bridge'
import type { PipelinePhase, PipelineVersionOutput } from '../workflow/pipelineProtocol'
import { pipelinePhaseLabels } from '../workflow/pipelinePresentation'
import { formatJobCreatedAt } from './projectJobs'
import './jobs.css'

/** FR-131: the pictures used as a Job's thumbnail, the landscape one first (same order as FR-045/FR-053). */
const thumbnailRoles: readonly PipelineVersionOutput['role'][] = ['LandscapeThumbnail', 'VerticalThumbnail']

const liveStatusPhases: readonly PipelinePhase[] = ['Queued', 'Ingesting', 'Analyzing', 'Composing', 'Rendering', 'Evaluating', 'Delivering']

/** FR-131 AC-135: the Job's state in words, so it never depends on colour. */
export function jobThumbnailStatus(phase: PipelinePhase): string {
  if (phase === 'Completed') return '完了'
  if (phase === 'Failed') return '失敗'
  if (phase === 'Canceled') return '取消'
  if (liveStatusPhases.includes(phase)) return '生成中'
  return pipelinePhaseLabels[phase]
}

/** FR-131: a Job can be chosen (for example in the comparison) only when it has completed. */
export function isJobSelectable(phase: PipelinePhase): boolean {
  return phase === 'Completed'
}

/** FR-131: the Job's own generated thumbnail, or null when it has none (in progress, failed, canceled). */
export function jobThumbnailOutput(outputs: readonly PipelineVersionOutput[] | undefined): PipelineVersionOutput | null {
  for (const role of thumbnailRoles) {
    const found = outputs?.find(output => output.role === role && output.mediaType === 'image/png')
    if (found !== undefined) return found
  }
  return null
}

export interface JobThumbnailProps {
  readonly projectId: string | null
  /** The Job's name such as "Job 2". */
  readonly jobName: string
  readonly executionId: string
  readonly createdAt: string | null
  readonly phase: PipelinePhase
  readonly outputs?: readonly PipelineVersionOutput[]
  /** Inside a button or link the thumbnail is not a tab stop of its own; the control is. */
  readonly inControl?: boolean
  readonly className?: string
  /** The name, time and state are already written next to it (a Job row or button). */
  readonly showCaption?: boolean
}

/** FR-131 AC-135: "サムネイル: Job 2、作成日時 …、完了" — identifies the Job without the picture or colour. */
export function jobThumbnailLabel(props: Pick<JobThumbnailProps, 'jobName' | 'createdAt' | 'phase'>): string {
  return `サムネイル: ${props.jobName}、作成日時 ${formatJobCreatedAt(props.createdAt)}、${jobThumbnailStatus(props.phase)}`
}

/**
 * FR-131: one Job's thumbnail with its name, creation time and state. A Job without a thumbnail (or
 * whose picture cannot be read) shows a substitute box of the same size, so the layout never shifts
 * and the other Jobs' thumbnails are unaffected. The picture is keyed by its address, which carries
 * the content digest, so a re-created or newly completed Job shows its latest picture.
 */
export function JobThumbnail(props: Readonly<JobThumbnailProps>) {
  const { projectId, executionId, outputs, phase, inControl = false, className, showCaption = true } = props
  const output = jobThumbnailOutput(outputs)
  const src = projectId !== null && output !== null
    ? projectThumbnailUrl(projectId, { executionId, artifactId: output.artifactId, contentDigest: output.contentDigest })
    : null
  const [failedSrc, setFailedSrc] = useState<string | null>(null)
  const showImage = src !== null && failedSrc !== src
  const status = jobThumbnailStatus(phase)
  const label = jobThumbnailLabel(props)
  return <figure className={`job-thumbnail${className === undefined ? '' : ` ${className}`}`} data-job-thumbnail-phase={phase}
    role="img" aria-label={label} tabIndex={inControl ? undefined : 0}>
    {showImage
      ? <img key={src} className="job-thumbnail-image" src={src} alt="" loading="lazy" onError={() => setFailedSrc(src)} />
      : <span className="job-thumbnail-empty" data-job-thumbnail-empty="true">サムネイルなし（{status}）</span>}
    {showCaption && <figcaption className="job-thumbnail-caption">
      <strong>{props.jobName}</strong>
      <span>{formatJobCreatedAt(props.createdAt)}</span>
      <span className="job-thumbnail-status">{status}</span>
    </figcaption>}
  </figure>
}
