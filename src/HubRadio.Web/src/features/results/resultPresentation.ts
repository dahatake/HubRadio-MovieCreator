import type { SourceOnlyProjectSummary } from '../../bridge/projectProtocol'
import type { BridgeJobSnapshot } from '../../bridge/protocol'
import type { JobMonitorState } from '../../state/types'
import type { DeliveryArtifact } from '../delivery/deliveryProtocol'
import type { DeliveryUiState } from '../delivery/deliveryState'
import { selectJobDisplayStatus, type JobDisplayStatus } from '../jobs/jobPresentation'
import { aggregateEvaluation, isPipelineLive, type EvaluationStatus, type PipelineSnapshot, type SourceOnlyResultArtifact, type SourceOnlyResultDto } from '../workflow/pipelineProtocol'

export type ResultKind =
  | 'videoLong'
  | 'videoShort'
  | 'text'
  | 'thumbnail'
  | 'video'
  | 'other'

export type ResultPlanState = 'planned' | 'stale' | 'unavailable'
export type ResultState =
  | ResultPlanState
  | 'processing'
  | 'generated'
  | 'notProduced'
  | 'failed'

/** Display inputs only; all snapshots must already have passed their bridge parser. */
export interface ResultPresentationInput {
  readonly pipeline?: PipelineSnapshot | null
  readonly sourceOnlySummary?: SourceOnlyProjectSummary | null
  readonly delivery?: Pick<DeliveryUiState, 'status' | 'snapshot'> | null
  /** The same project's current settings digest, never a changed global default. */
  readonly currentSettingsSnapshotDigest?: string | null
  /** Supply only the actual job selected for this context; it has no artifact binding. */
  readonly jobMonitor?: Pick<JobMonitorState, 'trackedJobId' | 'snapshot'> | null
}

export interface ResultCard {
  readonly id: string
  readonly kind: ResultKind
  readonly title: string
  /** notProduced means no matching artifact in this delivery, not a candidate rejection. */
  readonly state: ResultState
  /** Classified files in the supplied available manifest, not planned/candidate counts. */
  readonly count?: number
  readonly plannedLabels: readonly string[]
  /** Exact existing references. Raw artifact metadata remains data, never HTML. */
  readonly artifacts: readonly DeliveryArtifact[]
  /** Actual v2 records, never disguised as a legacy delivery DTO. */
  readonly pipelineArtifacts?: readonly SourceOnlyResultArtifact[]
  /** Registered local files only; this does not assert V2 automatic promotion/approval. */
  readonly saveState: 'saved' | 'unavailable'
}

export interface ResultPresentation {
  readonly cards: readonly ResultCard[]
  readonly planState: ResultPlanState
  readonly deliveryStatus: DeliveryUiState['status']
  readonly jobStatus: JobDisplayStatus | null
  readonly evaluation: EvaluationStatus
  readonly pipelineResult?: SourceOnlyResultDto
  readonly resultSource?: 'current' | 'previous'
}

const groups: readonly Readonly<{ kind: ResultKind; title: string }>[] = [
  { kind: 'videoLong', title: '長尺動画' },
  { kind: 'videoShort', title: '短尺動画' },
  { kind: 'text', title: '字幕・テキスト' },
  { kind: 'thumbnail', title: 'サムネイル' },
  { kind: 'video', title: '動画（尺の区分未取得）' },
  { kind: 'other', title: 'その他の成果物' },
]

interface PlannedArtifactLabel {
  readonly kind: ResultKind
  readonly label: string
}

// Exact names from the bundled 1.0.0 quick-start set, existing summaries, and
// explicit Japanese labels. Never extract counts/dimensions from arbitrary names.
const plannedArtifactLabels = new Map<string, PlannedArtifactLabel>([
  ['landscapeLongForm', { kind: 'videoLong', label: '長尺動画' }],
  ['verticalShortCandidates', { kind: 'videoShort', label: '短尺動画候補' }],
  ['subtitleBurnInVariant', { kind: 'video', label: '字幕焼き込み版' }],
  ['cleanVariant', { kind: 'video', label: 'クリーン版' }],
  ['subtitleFiles', { kind: 'text', label: '字幕ファイル' }],
  ['thumbnailCandidates', { kind: 'thumbnail', label: 'サムネイル候補' }],
  ['landscapeVideo', { kind: 'video', label: '横長動画' }],
  ['verticalShorts', { kind: 'videoShort', label: '短尺動画' }],
  ['verticalShort', { kind: 'videoShort', label: '短尺動画' }],
  ['長尺動画', { kind: 'videoLong', label: '長尺動画' }],
  ['短尺動画', { kind: 'videoShort', label: '短尺動画' }],
  ['字幕ファイル', { kind: 'text', label: '字幕ファイル' }],
  ['文字起こし', { kind: 'text', label: '文字起こし' }],
  ['サムネイル候補', { kind: 'thumbnail', label: 'サムネイル候補' }],
])
const unknownPlan: PlannedArtifactLabel = { kind: 'other', label: 'その他の予定成果物' }

const artifactKinds = new Map<string, ResultKind>([
  ['master', 'video'],
  ['video', 'video'],
  ['subtitle', 'text'],
  ['transcript', 'text'],
  ['chapters', 'text'],
  ['publishing-text', 'text'],
  ['editing-instructions', 'text'],
  ['thumbnail', 'thumbnail'],
])

export function deriveResultPresentation(
  input: ResultPresentationInput = {},
): ResultPresentation {
  if (input.pipeline != null) return derivePipelinePresentation(input, input.pipeline)
  const summary = input.sourceOnlySummary ?? null
  const planState = selectPlanState(summary, input.currentSettingsSnapshotDigest)
  const snapshot = input.delivery?.status === 'available'
    ? input.delivery.snapshot
    : null
  const monitor = input.jobMonitor
  const job = monitor?.snapshot != null && monitor.snapshot.jobId === monitor.trackedJobId
    ? monitor.snapshot
    : null
  const renderState = job?.kind === 'finalRender' ? selectRenderState(job) : null
  const plans = new Map<ResultKind, Set<string>>()
  for (const value of summary?.artifactSet ?? []) {
    const { kind, label } = plannedArtifactLabels.get(value) ?? unknownPlan
    const labels = plans.get(kind) ?? new Set<string>()
    labels.add(label)
    plans.set(kind, labels)
  }

  const files = new Map<ResultKind, DeliveryArtifact[]>()
  for (const artifact of snapshot?.artifacts ?? []) {
    const kind = selectArtifactKind(artifact)
    const artifacts = files.get(kind) ?? []
    artifacts.push(artifact)
    files.set(kind, artifacts)
  }

  const cards: ResultCard[] = []
  for (const { kind, title } of groups) {
    // A final-render job does not identify a particular long/short/text/thumbnail
    // output. Show its state only on the unclassified video group, not every plan.
    const jobState = kind === 'video' ? renderState : null
    if (!plans.has(kind) && !files.has(kind) && jobState === null) continue

    const artifacts = Object.freeze([...(files.get(kind) ?? [])])
    const state: ResultState = artifacts.length > 0
      ? 'generated'
      : jobState ?? (
          plans.has(kind) && planState === 'stale'
            ? 'stale'
            : snapshot !== null
              ? 'notProduced'
              : plans.has(kind) ? 'planned' : 'unavailable'
        )
    cards.push(Object.freeze({
      id: `result-${kind}`,
      kind,
      title,
      state,
      ...(snapshot === null ? {} : { count: artifacts.length }),
      plannedLabels: Object.freeze([...(plans.get(kind) ?? [])]),
      artifacts,
      // Available native delivery evidence validates registered local artifacts.
      // Neither a render job nor QC/review approval alone is this evidence.
      saveState: artifacts.length > 0 ? 'saved' : 'unavailable',
    }))
  }

  return Object.freeze({
    cards: Object.freeze(cards),
    planState,
    deliveryStatus: input.delivery?.status ?? null,
    jobStatus: job === null ? null : selectJobDisplayStatus(job),
    // The current delivery QC/review records are not E-EVALUATION results.
    evaluation: 'NOT EVALUATED',
  })
}

function selectPlanState(
  summary: SourceOnlyProjectSummary | null,
  currentDigest: string | null | undefined,
  acceptedSettingsDigest = summary?.settingsSnapshotDigest,
): ResultPlanState {
  if (summary === null) return 'unavailable'
  if (
    currentDigest != null &&
    currentDigest.length === 64 &&
    /^[0-9a-f]+$/.test(currentDigest) &&
    currentDigest !== acceptedSettingsDigest
  ) {
    return 'stale'
  }
  // Profile age alone says nothing about freshness. Delivery's render-profile
  // digest is not a settings digest and cannot bind these files to this plan.
  return 'planned'
}

function selectArtifactKind(artifact: DeliveryArtifact): ResultKind {
  const known = artifactKinds.get(artifact.artifactType)
    ?? plannedArtifactLabels.get(artifact.artifactType)?.kind
  if (known !== undefined) return known
  if (artifact.mediaType.startsWith('video/')) return 'video'
  if (artifact.mediaType.startsWith('text/')) return 'text'
  // A generic image or JSON file is not necessarily a thumbnail or transcript.
  return 'other'
}

function selectRenderState(job: BridgeJobSnapshot): ResultState {
  switch (job.state) {
    case 'queued': return 'planned'
    case 'running': return 'processing'
    case 'failed': return 'failed'
    case 'cancelled':
    case 'interrupted':
    case 'succeeded':
      // Stopping, interruption and completion do not enumerate produced files.
      return 'unavailable'
  }
}

function derivePipelinePresentation(input: ResultPresentationInput, pipeline: PipelineSnapshot): ResultPresentation {
  const plan = deriveResultPresentation({ sourceOnlySummary: input.sourceOnlySummary, currentSettingsSnapshotDigest: input.currentSettingsSnapshotDigest })
  const planState = selectPlanState(input.sourceOnlySummary ?? null, input.currentSettingsSnapshotDigest, pipeline.settingsSnapshotDigest)
  const current = pipeline.resultStatus === 'valid' ? pipeline.result : null
  const result = current ?? pipeline.previousResult
  const kinds: Readonly<Record<SourceOnlyResultArtifact['role'], ResultKind>> = {
    LandscapeClean: 'videoLong', LandscapeBurnIn: 'videoLong', VerticalBurnIn: 'videoShort',
    SubRip: 'text', WebVtt: 'text', LandscapeThumbnail: 'thumbnail', VerticalThumbnail: 'thumbnail', VerticalClean: 'videoShort',
  }
  const cards = groups.flatMap(({ kind, title }): ResultCard[] => {
    const planned = plan.cards.find(card => card.kind === kind)
    const files = result?.artifacts.filter(a => kinds[a.role] === kind) ?? []
    if (planned === undefined && files.length === 0) return []
    const state: ResultState = files.length > 0 ? 'generated' : result !== null ? 'notProduced'
      : pipeline.phase === 'Failed' || pipeline.resultStatus === 'invalid' ? 'failed'
        : isPipelineLive(pipeline.phase) ? 'processing' : planned === undefined ? 'unavailable' : planState
    return [Object.freeze({
      id: `result-${kind}`, kind, title, state,
      ...(result === null ? {} : { count: files.length }),
      plannedLabels: planned?.plannedLabels ?? Object.freeze([]), artifacts: Object.freeze([]),
      pipelineArtifacts: Object.freeze(files),
      saveState: files.length > 0 && files.every(a => a.delivered) ? 'saved' : 'unavailable',
    })]
  })
  return Object.freeze({
    cards: Object.freeze(cards), planState,
    deliveryStatus: result === null ? null : 'available', jobStatus: null,
    evaluation: aggregateEvaluation(result?.artifacts.map(a => a.evaluation?.aggregateStatus ?? 'NOT EVALUATED') ?? []),
    ...(result === null ? {} : { pipelineResult: result, resultSource: current === null ? 'previous' as const : 'current' as const }),
  })
}