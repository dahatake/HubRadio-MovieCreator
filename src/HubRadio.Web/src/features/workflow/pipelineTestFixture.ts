// SYNTHETIC TEST DATA ONLY. No production module imports this file. Field names
// and enum casing mirror SourceOnlyResultCodec.ToDto / AutomatedEvaluationJsonCodec.
import { EVALUATION_LIMITATIONS, EVALUATION_RULES, type PipelinePhase, type PipelineResponse, type PipelineSnapshot,
  type SourceOnlyResultArtifact, type SourceOnlyResultDto, type SourceOnlyResultTarget } from './pipelineProtocol'

export type Mutable<T> = T extends object ? { -readonly [K in keyof T]: Mutable<T[K]> } : T
export const fixtureId = (n: number): string => `80000000-0000-4000-8000-${n.toString().padStart(12, '0')}`
export const pipelineProjectId = fixtureId(1)
export const pipelineExecutionId = fixtureId(2)
export const pipelineRootJobId = fixtureId(3)
export const pipelineIngestJobId = fixtureId(4)
export const pipelineVersionId = fixtureId(5)
export const pipelineVersionDigest = 'a'.repeat(64)
export const pipelineSettingsDigest = 'b'.repeat(64)
const at = '2026-09-09T00:00:00.0000000+00:00'

export function createPipelineSnapshot(phase: PipelinePhase = 'Queued', projectId = pipelineProjectId): Mutable<PipelineSnapshot> {
  return { projectId, executionId: pipelineExecutionId, rootJobId: pipelineRootJobId, ingestJobId: pipelineIngestJobId,
    revision: 0, phase, settingsSnapshotDigest: pipelineSettingsDigest, versionId: null, versionDigest: null,
    parentVersionId: null, parentDigest: null, resultStatus: 'none', result: null, previousResult: null,
    failureCode: phase === 'Failed' ? 'source_only.execution_failed' : null }
}

export function createPipelineResult(projectId = pipelineProjectId): Mutable<SourceOnlyResultDto> {
  const result: Mutable<SourceOnlyResultDto> = {
    schemaVersion: 'source-only.result.v2',
    binding: { projectId, executionId: pipelineExecutionId, rootJobId: pipelineRootJobId,
      versionId: pipelineVersionId, versionDigest: pipelineVersionDigest, settingsSnapshotDigest: pipelineSettingsDigest },
    generationDisposition: 'Completed', artifacts: [], omissions: [], failures: [], reconciliationArtifacts: [],
    transcriptReferences: [], primaryWorkflow: null, stagingCleanupRequired: false, generationWarnings: [],
    ancillaryArtifacts: [],
  }
  result.artifacts.push(createPipelineArtifact(result, 'LandscapeClean', 10))
  return result
}

export function createCompletedPipeline(projectId = pipelineProjectId): Mutable<PipelineSnapshot> {
  const result = createPipelineResult(projectId)
  return { ...createPipelineSnapshot('Completed', projectId), revision: 8, resultStatus: 'valid', result,
    versionId: result.binding.versionId, versionDigest: result.binding.versionDigest }
}

export function createPipelineArtifact(result: SourceOnlyResultDto, role: SourceOnlyResultArtifact['role'], seed: number): Mutable<SourceOnlyResultArtifact> {
  const image = role === 'LandscapeThumbnail' || role === 'VerticalThumbnail'
  const video = role === 'LandscapeClean' || role === 'LandscapeBurnIn' || role === 'VerticalBurnIn'
  const artifactId = fixtureId(seed)
  const artifactDigest = 'c'.repeat(64)
  const versionId = role === 'VerticalBurnIn' || image ? fixtureId(seed + 100) : result.binding.versionId
  const versionDigest = role === 'VerticalBurnIn' || image ? 'd'.repeat(64) : result.binding.versionDigest
  const target: Mutable<SourceOnlyResultTarget> = { kind: 'Settings', projectId: result.binding.projectId, versionId, versionDigest,
    artifactId, artifactDigest, timeBasis: null, time: null, settingKey: 'project.title', editorKind: null }
  const rules: Mutable<NonNullable<SourceOnlyResultArtifact['evaluation']>['rules']> = EVALUATION_RULES.map((ruleId, index) => {
    const skipped = index >= 14 || (!video && ['fullDecode', 'duration', 'avSync', 'loudness', 'truePeak'].includes(ruleId))
    const warned = video && ruleId === 'loudness'
    return { ruleId, category: 'machine', scope: index >= 14 ? 'userInterface' : 'artifact',
      status: skipped ? 'NOT EVALUATED' : warned ? 'WARNING' : 'PASS', reasonCode: skipped ? 'source_only.test_unmeasured' : warned ? 'source_only.test_warning' : 'source_only.test_pass',
      evidenceDigest: skipped ? null : 'e'.repeat(64), target: warned ? { kind: 'settings', projectId: target.projectId,
        compositionVersionId: versionId, compositionVersionDigest: versionDigest, artifactId, artifactDigest,
        timeBasis: null, time: null, settingKey: target.settingKey, editorKind: null } : null }
  })
  const limitations: Mutable<NonNullable<SourceOnlyResultArtifact['evaluation']>['limitations']> = EVALUATION_LIMITATIONS.map(ruleId => ({ ruleId,
    category: 'humanLimitation', scope: 'humanReview', status: 'NOT EVALUATED', reasonCode: 'evaluation.human_review_required', evidenceDigest: null, target: null }))
  const counts = [...rules, ...limitations]
  return {
    role, renditionId: role === 'VerticalBurnIn' ? fixtureId(seed + 200) : null,
    artifact: { artifactId, contentDigest: artifactDigest, byteSize: 128, kind: 'Render' },
    mediaType: video ? 'video/mp4' : image ? 'image/png' : role === 'SubRip' ? 'application/x-subrip' : 'text/vtt',
    versionId, versionDigest, source: { artifactId: fixtureId(9), contentDigest: 'f'.repeat(64), byteSize: 256, kind: 'Original' },
    graphDigest: 'd'.repeat(64), manifestDigest: 'd'.repeat(64),
    evidence: (['Graph', 'Manifest', 'RenderingInputs', 'QualityControl'] as const).map((kind, i) => ({ kind,
      artifact: { artifactId: fixtureId(seed + 300 + i), contentDigest: 'e'.repeat(64), byteSize: 128, kind: 'Evidence' } })),
    evaluation: { schemaVersion: '1.0.0', evaluationId: fixtureId(seed + 400), jobId: result.binding.rootJobId,
      projectId: result.binding.projectId, compositionVersionId: versionId, compositionVersionDigest: versionDigest, artifactId, artifactDigest,
      settingsProfileId: fixtureId(8), settingsProfileVersion: '1.0.0', settingsProfileDigest: 'a'.repeat(64), settingsSnapshotDigest: result.binding.settingsSnapshotDigest,
      ruleSetVersion: '2.0.0', environmentVersion: 'source-only.output-evaluation.v2', environmentDigest: 'a'.repeat(64), toolVersionsDigest: 'a'.repeat(64),
      modelVersionsDigest: null, templateDigest: null, musicDigest: null, startedAt: at, completedAt: at, evaluatedAt: at,
      inputDigest: 'a'.repeat(64), outputDigest: artifactDigest, aggregateStatus: 'NOT EVALUATED',
      statusCounts: { pass: counts.filter(r => r.status === 'PASS').length, warning: counts.filter(r => r.status === 'WARNING').length,
        fail: 0, notEvaluated: counts.filter(r => r.status === 'NOT EVALUATED').length }, rules, limitations },
    evaluationDigest: 'a'.repeat(64),
    measurements: video ? { expectedDuration: { value: 10, rate: 1 }, videoStart: { value: 0, rate: 1 }, videoEnd: { value: 10, rate: 1 },
      audioStart: null, audioEnd: null, videoFrameCount: 300, audioFrameCount: null, probeDigest: 'a'.repeat(64), videoTimingDigest: 'a'.repeat(64),
      audioTimingDigest: null, subtitleTimingDigest: null, lumaDigest: 'a'.repeat(64), audioDigest: null, minimumLuma: 0, maximumLuma: 255, meanLuma: 120,
      loudness: null, truePeak: null, decodedSuccessfully: true, cues: [], videoTimeBase: { value: 1, rate: 30 }, audioTimeBase: null,
      videoTimingContinuous: true, videoFrameCadenceExact: true, outputDigitalSilence: false, audioVideoStartOffset: null, audioVideoEndOffset: null } : null,
    failureCode: null, qcFailureReason: null, qcFailureStage: null,
    warnings: video ? [{ id: `${artifactId}:loudness`, ruleId: 'loudness', status: 'Warning', reasonCode: 'source_only.test_warning', target }] : [],
    delivered: true, delivery: { artifact: { artifactId: fixtureId(seed + 500), contentDigest: artifactDigest, byteSize: 128, kind: 'Delivery' },
      receipt: { kind: 'DeliveryReceipt', artifact: { artifactId: fixtureId(seed + 600), contentDigest: 'a'.repeat(64), byteSize: 128, kind: 'Evidence' } } },
    thumbnailPlanId: image ? fixtureId(seed + 700) : null,
    frameEvidence: image ? { sourceAssetId: fixtureId(9), sourceSpanId: fixtureId(seed + 800), sourceDigest: 'f'.repeat(64), sourcePtsIndexDigest: 'a'.repeat(64),
      sourceFrameSequence: 1, sourcePresentationTime: { value: 1, rate: 30 }, sourceFrameDuration: { value: 1, rate: 30 }, sourceWidth: 1920, sourceHeight: 1080,
      width: 1280, height: 720, extractionRule: 'decode-original-select-source-sequence-contain-v1' } : null,
    qcApplicability: video ? 'VideoOutputQc' : image ? 'ImageFileOnly' : 'SubtitleFileOnly', profileId: fixtureId(seed + 900), profileDigest: 'a'.repeat(64),
  }
}

export function setPipelineWarningTarget(result: Mutable<SourceOnlyResultDto>, patch: Partial<Mutable<SourceOnlyResultTarget>>): void {
  const a = result.artifacts[0]!
  const target = { ...a.warnings[0]!.target, ...patch }
  a.warnings[0]!.target = target
  const rule = a.evaluation!.rules.find(r => r.ruleId === a.warnings[0]!.ruleId)!
  const editors = { Timeline: 'timeline', Subtitles: 'subtitles', Audio: 'audio', Layout: 'layout', Metadata: 'metadata' } as const
  rule.target = { kind: ({ Preview: 'preview', Settings: 'settings', Evidence: 'evidence', Editor: 'editor' } as const)[target.kind],
    projectId: target.projectId, compositionVersionId: target.versionId, compositionVersionDigest: target.versionDigest,
    artifactId: target.artifactId, artifactDigest: target.artifactDigest, time: target.time,
    timeBasis: target.timeBasis === null ? null : target.timeBasis === 'Source' ? 'source' : 'output',
    settingKey: target.settingKey, editorKind: target.editorKind === null ? null : editors[target.editorKind] }
}

export function pipelineResponse(snapshot: PipelineSnapshot | null, requestId = fixtureId(1000), method: PipelineResponse['method'] = 'pipeline.read'): Mutable<PipelineResponse> {
  return { version: 1, requestId, method, result: { status: snapshot === null ? 'notFound' : snapshot.resultStatus === 'invalid' ? 'invalid' : 'available', snapshot: structuredClone(snapshot) as Mutable<PipelineSnapshot> | null } }
}