import { describe, expect, it } from 'vitest'

import {
  ANALYSIS_ADOPT_METHOD,
  ANALYSIS_MAX_RESPONSE_UTF8_BYTES,
  ANALYSIS_READ_METHOD,
  ANALYSIS_UPDATE_TOPIC_METHOD,
  createAnalysisAdoptRequest,
  createAnalysisReadRequest,
  createAnalysisUpdateTopicRequest,
  parseAnalysisResponse,
  type AnalysisCandidate,
  type AnalysisDecisionParameters,
} from './analysisProtocol'
import {
  analysisDigest,
  analysisMetadataRevisionId,
  analysisProjectId,
  analysisRequestId,
  analysisRevisionId,
  analysisSourceSpanId,
  analysisTopicId,
  createAllProducerCandidates,
  createAnalysisCandidate,
  createAnalysisResponseValue,
} from './analysisTestFixture'

describe('analysisProtocol', () => {
  it('accepts and freezes the complete native projection for all producers', () => {
    const parsed = parseAnalysisResponse(
      createAnalysisResponseValue(ANALYSIS_READ_METHOD, createAllProducerCandidates()),
    )

    expect(parsed?.result.status).toBe('available')
    if (parsed?.result.status !== 'available') throw new Error('Expected an available snapshot.')
    expect(parsed.result.snapshot.candidates.map((candidate) => candidate.producer)).toEqual([
      'termCorrection',
      'silence',
      'editorialPhrase',
      'repetition',
      'topicBoundary',
      'audioReview',
      'contextDeviation',
      'emphasis',
    ])
    expect(parsed.result.snapshot.candidates[1]?.confidence).toBeNull()
    expect(Object.isFrozen(parsed)).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot.candidates[0]?.payload)).toBe(true)
  })

  it('accepts a closed failure result without a snapshot', () => {
    const value = createAnalysisResponseValue(ANALYSIS_ADOPT_METHOD)
    value.result = { status: 'staleBase', snapshot: null }

    expect(parseAnalysisResponse(value)).toEqual(value)
  })

  it.each([
    'envelope extra',
    'result actor SID',
    'snapshot extra',
    'candidate extra',
    'payload extra',
    'sensitivity matched value',
  ])('rejects %s', (mutation) => {
    const value = clone(createAnalysisResponseValue())
    const snapshot = requireSnapshot(value)
    switch (mutation) {
      case 'envelope extra':
        value.extra = true
        break
      case 'result actor SID':
        value.result.actorSid = 'S-1-5-21-private'
        break
      case 'snapshot extra':
        asRecord(snapshot).secret = 'private'
        break
      case 'candidate extra':
        asRecord(snapshot.candidates[0]).elementId = analysisTopicId
        break
      case 'payload extra':
        asRecord(snapshot.candidates[0]?.payload).cueId = analysisTopicId
        break
      case 'sensitivity matched value':
        asRecord(snapshot.sensitivity.findings[0]).matchedValue = 'do-not-expose'
        break
    }

    expect(parseAnalysisResponse(value)).toBeNull()
  })

  it('rejects inconsistent technical warnings and unsafe persisted staging', () => {
    const warning = clone(
      createAnalysisResponseValue(
        ANALYSIS_READ_METHOD,
        [createAllProducerCandidates()[5]!],
      ),
    )
    const warningSnapshot = requireSnapshot(warning)
    asRecord(warningSnapshot.candidates[0]).canAdopt = true
    expect(parseAnalysisResponse(warning)).toBeNull()

    const staged = clone(createAnalysisResponseValue())
    const stagedSnapshot = requireSnapshot(staged)
    asRecord(stagedSnapshot).stagedChanges = [
      {
        kind: 'transcriptRevision',
        candidateId: stagedSnapshot.candidates[0]!.candidateId,
        sourceRevisionId: stagedSnapshot.sourceRevisionId,
        resultingRevisionId: '20000000-0000-4000-8000-000000000001',
        persisted: true,
      },
    ]
    expect(parseAnalysisResponse(staged)).toBeNull()
  })

  it('rejects noncanonical rational values and invalid confidence', () => {
    const rational = clone(createAnalysisResponseValue())
    const rationalSnapshot = requireSnapshot(rational)
    asRecord(rationalSnapshot.speakers[0]?.source.range.start).value = '2'
    asRecord(rationalSnapshot.speakers[0]?.source.range.start).rate = '2'
    expect(parseAnalysisResponse(rational)).toBeNull()

    const confidence = clone(createAnalysisResponseValue())
    asRecord(requireSnapshot(confidence).candidates[0]).confidence = 1.01
    expect(parseAnalysisResponse(confidence)).toBeNull()
  })

  it.each([
    'speaker ID',
    'topic ID',
    'topic metadata ID',
    'candidate ID',
    'term-evidence ID',
  ])('rejects duplicate %s', (duplicate) => {
    const value = clone(
      createAnalysisResponseValue(ANALYSIS_READ_METHOD, createAllProducerCandidates()),
    )
    const snapshot = requireSnapshot(value)

    switch (duplicate) {
      case 'speaker ID':
        asRecord(snapshot).speakers = [
          snapshot.speakers[0],
          clone(snapshot.speakers[0]),
        ]
        break
      case 'topic ID':
        asRecord(snapshot).topics = [
          snapshot.topics[0],
          clone(snapshot.topics[0]),
        ]
        break
      case 'topic metadata ID': {
        const duplicateMetadata = clone(snapshot.topics[0])
        asRecord(duplicateMetadata).topicId =
          '20000000-0000-4000-8000-000000000001'
        asRecord(snapshot).topics = [snapshot.topics[0], duplicateMetadata]
        break
      }
      case 'candidate ID':
        asRecord(snapshot).candidates = [
          ...snapshot.candidates,
          clone(snapshot.candidates[0]),
        ]
        break
      case 'term-evidence ID': {
        const emphasis = snapshot.candidates.find(
          (candidate) => candidate.producer === 'emphasis',
        )
        if (emphasis?.payload.producer !== 'emphasis') {
          throw new Error('Expected an emphasis fixture.')
        }
        asRecord(emphasis.payload).termEvidence = [
          emphasis.payload.termEvidence[0],
          clone(emphasis.payload.termEvidence[0]),
        ]
        break
      }
    }

    expect(parseAnalysisResponse(value)).toBeNull()
  })

  it('accepts correlated native adoption decisions and nonpersistent stages', () => {
    const value = createCorrelatedAdoptionResponse()

    const parsed = parseAnalysisResponse(value)

    expect(parsed?.result.status).toBe('available')
    if (parsed?.result.status !== 'available') throw new Error('Expected an available snapshot.')
    expect(parsed.result.snapshot.stagedChanges.map((change) => change.kind)).toEqual([
      'transcriptRevision',
      'editOperation',
      'editOperation',
      'cue',
    ])
    expect(parsed.result.snapshot.stagedChanges.every((change) => !change.persisted))
      .toBe(true)
  })

  it.each([
    'unadopted candidate',
    'term result revision',
    'edit operation kind',
    'duplicate candidate stage',
  ])('rejects a stage with mismatched %s', (mismatch) => {
    const value = createCorrelatedAdoptionResponse()
    const snapshot = requireSnapshot(value)
    switch (mismatch) {
      case 'unadopted candidate': {
        const term = requireCandidate(snapshot, 'termCorrection')
        Object.assign(asRecord(term), {
          status: 'proposed',
          canAdopt: true,
          canReject: true,
          decision: null,
        })
        break
      }
      case 'term result revision': {
        const stage = snapshot.stagedChanges.find(
          (change) => change.kind === 'transcriptRevision',
        )
        asRecord(stage).resultingRevisionId = testEntityId(901)
        break
      }
      case 'edit operation kind': {
        const boundaryId = requireCandidate(snapshot, 'topicBoundary').candidateId
        const stage = snapshot.stagedChanges.find(
          (change) => change.kind === 'editOperation' &&
            change.candidateId === boundaryId,
        )
        if (stage?.kind !== 'editOperation') {
          throw new Error('Expected a staged boundary edit.')
        }
        Object.assign(asRecord(stage.operation), {
          kind: 'delete',
          splitOffset: null,
          rightElementId: null,
        })
        break
      }
      case 'duplicate candidate stage':
        asRecord(snapshot).stagedChanges = [
          ...snapshot.stagedChanges,
          clone(snapshot.stagedChanges[0]),
        ]
        break
    }

    expect(parseAnalysisResponse(value)).toBeNull()
  })

  it.each([
    'modified replacement ID',
    'adopted source span',
    'technical-warning adoption',
    'current action state',
    'duplicate decision ID',
  ])('rejects inconsistent candidate %s', (mismatch) => {
    const value = mismatch === 'adopted source span' ||
      mismatch === 'duplicate decision ID'
      ? createCorrelatedAdoptionResponse()
      : clone(createAnalysisResponseValue(
          ANALYSIS_READ_METHOD,
          createAllProducerCandidates(),
        ))
    const snapshot = requireSnapshot(value)
    switch (mismatch) {
      case 'modified replacement ID': {
        const candidate = requireCandidate(snapshot, 'termCorrection')
        markModified(candidate, 30, testEntityId(301))
        asRecord(candidate.decision).replacementCandidateId = testEntityId(302)
        break
      }
      case 'adopted source span': {
        const candidate = requireCandidate(snapshot, 'silence')
        if (candidate.decision?.adoptedReference === null ||
          candidate.decision === null) {
          throw new Error('Expected an adopted source reference.')
        }
        asRecord(candidate.decision.adoptedReference).sourceSpanId =
          testEntityId(303)
        break
      }
      case 'technical-warning adoption':
        markAdopted(
          requireCandidate(snapshot, 'audioReview'),
          31,
          null,
        )
        break
      case 'current action state':
        Object.assign(
          asRecord(requireCandidate(snapshot, 'termCorrection')),
          { canAdopt: false, canReject: false },
        )
        break
      case 'duplicate decision ID': {
        const term = requireCandidate(snapshot, 'termCorrection')
        const silence = requireCandidate(snapshot, 'silence')
        if (term.decision === null || silence.decision === null) {
          throw new Error('Expected adopted decisions.')
        }
        asRecord(silence.decision).decisionId = term.decision.decisionId
        break
      }
    }

    expect(parseAnalysisResponse(value)).toBeNull()
  })

  it('accepts visible replacement links only when both directions agree', () => {
    const value = clone(createAnalysisResponseValue(ANALYSIS_READ_METHOD, [
      createAnalysisCandidate('termCorrection', 20),
      createAnalysisCandidate('termCorrection', 21),
    ]))
    const snapshot = requireSnapshot(value)
    const parent = snapshot.candidates[0]!
    const replacement = snapshot.candidates[1]!
    markModified(parent, 32, replacement.candidateId)
    asRecord(replacement).parentCandidateId = parent.candidateId

    expect(parseAnalysisResponse(value)).not.toBeNull()

    asRecord(replacement).parentCandidateId = testEntityId(304)
    expect(parseAnalysisResponse(value)).toBeNull()
  })

  it('accepts an ordered topic stage chain with stale emphasis evidence', () => {
    const value = createTopicStageResponse()

    const parsed = parseAnalysisResponse(value)

    expect(parsed?.result.status).toBe('available')
    if (parsed?.result.status !== 'available') throw new Error('Expected an available snapshot.')
    const emphasis = parsed.result.snapshot.candidates.find(
      (candidate) => candidate.producer === 'emphasis',
    )
    expect(emphasis?.freshness).toBe('stale')
    if (emphasis?.payload.producer !== 'emphasis') {
      throw new Error('Expected stale emphasis evidence.')
    }
    expect(emphasis.payload.topicMetadataRevisionId).toBe(analysisMetadataRevisionId)
    expect(parsed.result.snapshot.topics[0]?.metadataRevisionId)
      .not.toBe(analysisMetadataRevisionId)
  })

  it.each([
    'broken parent chain',
    'mismatched current field',
    'cyclic revision reuse',
  ])('rejects topic stages with %s', (mismatch) => {
    const value = createTopicStageResponse()
    const snapshot = requireSnapshot(value)
    const topic = snapshot.topics[0]!
    const stages = snapshot.stagedChanges
    const first = stages[0]
    const second = stages[1]
    if (first?.kind !== 'topicMetadataRevision' ||
      second?.kind !== 'topicMetadataRevision') {
      throw new Error('Expected two topic metadata stages.')
    }

    switch (mismatch) {
      case 'broken parent chain':
        asRecord(second).parentRevisionId = testEntityId(401)
        break
      case 'mismatched current field':
        asRecord(topic).changedField = 'title'
        break
      case 'cyclic revision reuse':
        asRecord(snapshot).stagedChanges = [
          ...stages,
          {
            kind: 'topicMetadataRevision',
            topicId: analysisTopicId,
            revisionId: first.revisionId,
            parentRevisionId: second.revisionId,
            field: 'audience',
            persisted: false,
          },
        ]
        Object.assign(asRecord(topic), {
          metadataRevisionId: first.revisionId,
          parentMetadataRevisionId: second.revisionId,
          changedField: 'audience',
        })
        break
    }

    expect(parseAnalysisResponse(value)).toBeNull()
  })

  it('constructs only the five allowed decision parameters despite injected targets', () => {
    const parameters = Object.assign(decisionParameters(), {
      elementId: analysisTopicId,
      cueId: analysisTopicId,
      splitOffset: { value: '1', rate: '1' },
    }) as AnalysisDecisionParameters

    const request = createAnalysisAdoptRequest(analysisRequestId, parameters)

    expect(request.method).toBe(ANALYSIS_ADOPT_METHOD)
    expect(Object.keys(request.params)).toEqual([
      'projectId',
      'revisionId',
      'digest',
      'candidateId',
      'reason',
    ])
    expect(JSON.stringify(request)).not.toContain('elementId')
    expect(JSON.stringify(request)).not.toContain('cueId')
    expect(JSON.stringify(request)).not.toContain('splitOffset')
  })

  it('constructs one closed topic field update with both OCC revisions', () => {
    const request = createAnalysisUpdateTopicRequest(analysisRequestId, {
      projectId: analysisProjectId,
      revisionId: analysisRevisionId,
      digest: analysisDigest,
      topicId: analysisTopicId,
      expectedTopicRevisionId: analysisMetadataRevisionId,
      update: { field: 'importantStatements', sourceSpanIds: [analysisSourceSpanId] },
      reason: '重要発言を確認',
    })

    expect(request.method).toBe(ANALYSIS_UPDATE_TOPIC_METHOD)
    expect(request.params.update).toEqual({
      field: 'importantStatements',
      sourceSpanIds: [analysisSourceSpanId],
    })
    expect(Object.keys(request.params.update)).toHaveLength(2)
  })

  it('rejects invalid IDs, digests, blank reasons, and oversized responses', () => {
    expect(() => createAnalysisReadRequest(analysisRequestId.toUpperCase(), analysisProjectId))
      .toThrow(/lowercase UUID/u)
    expect(() => createAnalysisAdoptRequest(analysisRequestId, {
      ...decisionParameters(),
      digest: 'A'.repeat(64),
    })).toThrow(/SHA-256/u)
    expect(() => createAnalysisAdoptRequest(analysisRequestId, {
      ...decisionParameters(),
      reason: ' ',
    })).toThrow(/nonblank/u)

    const oversized = clone(createAnalysisResponseValue())
    asRecord(requireSnapshot(oversized).candidates[0]).reason =
      'x'.repeat(ANALYSIS_MAX_RESPONSE_UTF8_BYTES)
    expect(parseAnalysisResponse(oversized)).toBeNull()
  })
})

function createCorrelatedAdoptionResponse(): ReturnType<
  typeof createAnalysisResponseValue
> {
  const value = clone(createAnalysisResponseValue(
    ANALYSIS_READ_METHOD,
    createAllProducerCandidates(),
  ))
  const snapshot = requireSnapshot(value)
  const term = requireCandidate(snapshot, 'termCorrection')
  const silence = requireCandidate(snapshot, 'silence')
  const boundary = requireCandidate(snapshot, 'topicBoundary')
  const emphasis = requireCandidate(snapshot, 'emphasis')
  if (term.payload.producer !== 'termCorrection') {
    throw new Error('Expected a term-correction payload.')
  }

  const resultingRevisionId = testEntityId(101)
  markAdopted(term, 1, resultingRevisionId)
  markAdopted(silence, 2, null)
  markAdopted(boundary, 3, null)
  markAdopted(emphasis, 4, null)
  asRecord(snapshot).stagedChanges = [
    {
      kind: 'transcriptRevision',
      candidateId: term.candidateId,
      sourceRevisionId: term.payload.sourceRevisionId,
      resultingRevisionId,
      persisted: false,
    },
    {
      kind: 'editOperation',
      candidateId: silence.candidateId,
      operation: {
        kind: 'delete',
        elementId: testEntityId(102),
        splitOffset: null,
        rightElementId: null,
      },
      persisted: false,
    },
    {
      kind: 'editOperation',
      candidateId: boundary.candidateId,
      operation: {
        kind: 'split',
        elementId: testEntityId(103),
        splitOffset: { value: '1', rate: '2' },
        rightElementId: testEntityId(104),
      },
      persisted: false,
    },
    {
      kind: 'cue',
      candidateId: emphasis.candidateId,
      cue: {
        cueId: testEntityId(105),
        kind: 'graphic',
        outputRange: clone(emphasis.source.range),
        regionName: 'graphic-main',
      },
      persisted: false,
    },
  ]
  return value
}

function createTopicStageResponse(): ReturnType<
  typeof createAnalysisResponseValue
> {
  const value = clone(createAnalysisResponseValue(
    ANALYSIS_READ_METHOD,
    createAllProducerCandidates(),
  ))
  const snapshot = requireSnapshot(value)
  const firstRevisionId = testEntityId(201)
  const secondRevisionId = testEntityId(202)
  const topic = snapshot.topics[0]!
  Object.assign(asRecord(topic), {
    metadataRevisionId: secondRevisionId,
    parentMetadataRevisionId: firstRevisionId,
    changedField: 'summary',
  })
  Object.assign(asRecord(requireCandidate(snapshot, 'emphasis')), {
    freshness: 'stale',
    canAdopt: false,
    canReject: false,
  })
  asRecord(snapshot).stagedChanges = [
    {
      kind: 'topicMetadataRevision',
      topicId: analysisTopicId,
      revisionId: firstRevisionId,
      parentRevisionId: analysisMetadataRevisionId,
      field: 'title',
      persisted: false,
    },
    {
      kind: 'topicMetadataRevision',
      topicId: analysisTopicId,
      revisionId: secondRevisionId,
      parentRevisionId: firstRevisionId,
      field: 'summary',
      persisted: false,
    },
  ]
  return value
}

function markAdopted(
  candidate: AnalysisCandidate,
  decisionSequence: number,
  resultingRevisionId: string | null,
): void {
  const termCorrection = candidate.suggestionKind === 'termCorrection'
  if (termCorrection !== (resultingRevisionId !== null)) {
    throw new Error('The adopted fixture output does not match its candidate kind.')
  }
  Object.assign(asRecord(candidate), {
    status: 'adopted',
    canAdopt: false,
    canReject: false,
    decision: {
      decisionId: testEntityId(500 + decisionSequence),
      candidateId: candidate.candidateId,
      producer: candidate.producer,
      suggestionKind: candidate.suggestionKind,
      decision: 'adopted',
      statusAfterDecision: 'adopted',
      sourceDigest: candidate.sourceDigest,
      decidedAt: '2026-01-02T03:05:05.0000000+00:00',
      reason: '候補を採用',
      replacementCandidateId: null,
      resultingTranscriptRevisionId: resultingRevisionId,
      adoptedReference: termCorrection
        ? null
        : {
            candidateId: candidate.candidateId,
            sourceSpanId: candidate.source.sourceSpanId,
          },
    },
  })
}

function markModified(
  candidate: AnalysisCandidate,
  decisionSequence: number,
  replacementCandidateId: string,
): void {
  Object.assign(asRecord(candidate), {
    status: 'reviewed',
    canAdopt: false,
    canReject: false,
    replacementCandidateId,
    decision: {
      decisionId: testEntityId(600 + decisionSequence),
      candidateId: candidate.candidateId,
      producer: candidate.producer,
      suggestionKind: candidate.suggestionKind,
      decision: 'modified',
      statusAfterDecision: 'reviewed',
      sourceDigest: candidate.sourceDigest,
      decidedAt: '2026-01-02T03:05:05.0000000+00:00',
      reason: '候補を修正',
      replacementCandidateId,
      resultingTranscriptRevisionId: null,
      adoptedReference: null,
    },
  })
}

function requireCandidate(
  snapshot: AnalysisSnapshotMutable,
  producer: AnalysisCandidate['producer'],
): AnalysisCandidate {
  const candidate = snapshot.candidates.find(
    (item) => item.producer === producer,
  )
  if (candidate === undefined) {
    throw new Error(`Expected a ${producer} candidate.`)
  }
  return candidate
}

function testEntityId(sequence: number): string {
  return `e1000000-0000-4000-8000-${sequence.toString().padStart(12, '0')}`
}

function decisionParameters(): AnalysisDecisionParameters {
  return {
    projectId: analysisProjectId,
    revisionId: analysisRevisionId,
    digest: analysisDigest,
    candidateId: createAllProducerCandidates()[0]!.candidateId,
    reason: '候補を確認',
  }
}

function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

function requireSnapshot(value: ReturnType<typeof createAnalysisResponseValue>): AnalysisSnapshotMutable {
  const snapshot = value.result.snapshot
  if (snapshot === null) throw new Error('Expected a mutable analysis snapshot.')
  return snapshot as AnalysisSnapshotMutable
}

type AnalysisSnapshotMutable = NonNullable<
  ReturnType<typeof createAnalysisResponseValue>['result']['snapshot']
>

function asRecord(value: unknown): Record<string, unknown> {
  if (typeof value !== 'object' || value === null) {
    throw new Error('Expected a mutable record.')
  }
  return value as Record<string, unknown>
}