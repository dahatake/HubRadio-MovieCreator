import { describe, expect, it } from 'vitest'

import {
  BRIDGE_MAX_THUMBNAIL_REQUEST_UTF8_BYTES,
  BRIDGE_MAX_THUMBNAIL_RESPONSE_UTF8_BYTES,
} from '../../bridge/protocol'
import {
  createThumbnailCancelRequest,
  createThumbnailGenerateRequest,
  createThumbnailReadRequest,
  parseThumbnailCancelResponse,
  parseThumbnailGenerateResponse,
  parseThumbnailReadResponse,
} from './thumbnailProtocol'
import {
  createThumbnailCancelResponseValue,
  createThumbnailGenerateResponseValue,
  createThumbnailReadResponseValue,
  thumbnailEvidenceDigest,
  thumbnailGenerationId,
  thumbnailJobId,
  thumbnailPlayerEvidenceDigest,
  thumbnailRequestId,
  thumbnailTemplateId,
} from './thumbnailTestFixture'

describe('thumbnail bridge protocol', () => {
  it('creates only bounded evidence-bound read, generation, and cancel requests', () => {
    const read = createThumbnailReadRequest(thumbnailRequestId, thumbnailJobId)
    const generate = createThumbnailGenerateRequest(thumbnailRequestId, {
      jobId: thumbnailJobId,
      generationId: thumbnailGenerationId,
      digest: thumbnailEvidenceDigest,
      playerDigest: thumbnailPlayerEvidenceDigest,
      templateId: thumbnailTemplateId,
      proxyFrameSequence: 2_147_483_647,
    })
    const cancel = createThumbnailCancelRequest(
      thumbnailRequestId,
      thumbnailGenerationId,
    )

    expect(read).toEqual({
      version: 1,
      requestId: thumbnailRequestId,
      method: 'thumbnail.read',
      params: { jobId: thumbnailJobId },
    })
    expect(generate).toEqual({
      version: 1,
      requestId: thumbnailRequestId,
      method: 'thumbnail.generate',
      params: {
        jobId: thumbnailJobId,
        generationId: thumbnailGenerationId,
        digest: thumbnailEvidenceDigest,
        playerDigest: thumbnailPlayerEvidenceDigest,
        templateId: thumbnailTemplateId,
        proxyFrameSequence: 2_147_483_647,
      },
    })
    expect(cancel.params).toEqual({ generationId: thumbnailGenerationId })
    for (const request of [read, generate, cancel]) {
      expect(Object.isFrozen(request)).toBe(true)
      expect(Object.isFrozen(request.params)).toBe(true)
      expect(new TextEncoder().encode(JSON.stringify(request)).byteLength).toBeLessThanOrEqual(
        BRIDGE_MAX_THUMBNAIL_REQUEST_UTF8_BYTES,
      )
    }
  })

  it.each([
    [`${thumbnailRequestId.slice(0, -1)}A`, thumbnailJobId, thumbnailGenerationId, thumbnailEvidenceDigest, thumbnailPlayerEvidenceDigest, thumbnailTemplateId, 1],
    [thumbnailRequestId, '00000000-0000-0000-0000-000000000000', thumbnailGenerationId, thumbnailEvidenceDigest, thumbnailPlayerEvidenceDigest, thumbnailTemplateId, 1],
    [thumbnailRequestId, thumbnailJobId, `${thumbnailGenerationId.slice(0, -1)}A`, thumbnailEvidenceDigest, thumbnailPlayerEvidenceDigest, thumbnailTemplateId, 1],
    [thumbnailRequestId, thumbnailJobId, thumbnailGenerationId, `${thumbnailEvidenceDigest.slice(0, -1)}A`, thumbnailPlayerEvidenceDigest, thumbnailTemplateId, 1],
    [thumbnailRequestId, thumbnailJobId, thumbnailGenerationId, thumbnailEvidenceDigest, `${thumbnailPlayerEvidenceDigest}\n`, thumbnailTemplateId, 1],
    [thumbnailRequestId, thumbnailJobId, thumbnailGenerationId, thumbnailEvidenceDigest, thumbnailPlayerEvidenceDigest, thumbnailTemplateId, -1],
    [thumbnailRequestId, thumbnailJobId, thumbnailGenerationId, thumbnailEvidenceDigest, thumbnailPlayerEvidenceDigest, thumbnailTemplateId, 0.5],
    [thumbnailRequestId, thumbnailJobId, thumbnailGenerationId, thumbnailEvidenceDigest, thumbnailPlayerEvidenceDigest, thumbnailTemplateId, 2_147_483_648],
  ])(
    'rejects noncanonical generation input %#',
    (requestId, jobId, generationId, digest, playerDigest, templateId, sequence) => {
      expect(() =>
        createThumbnailGenerateRequest(requestId, {
          jobId,
          generationId,
          digest,
          playerDigest,
          templateId,
          proxyFrameSequence: sequence,
        }),
      ).toThrow()
    },
  )

  it('parses and freezes negative source PTS plus genuine approval bindings', () => {
    const parsed = parseThumbnailReadResponse(createThumbnailReadResponseValue())

    expect(parsed?.result.status).toBe('available')
    if (parsed?.result.status !== 'available') {
      throw new Error('Expected available thumbnail evidence.')
    }
    expect(parsed.result.snapshot.sourcePresentationStart).toEqual({
      value: '-1',
      rate: '10',
    })
    expect(parsed.result.snapshot.templates[0]).toMatchObject({
      approvalRole: 'brandApprover',
      approvalDigest: '7'.repeat(64),
      templateVersionDigest: '8'.repeat(64),
      renderProfileDigest: '9'.repeat(64),
      layoutSpecDigest: 'a'.repeat(64),
    })
    expect(Object.isFrozen(parsed)).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot)).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot.templates)).toBe(true)
    expect(Object.isFrozen(parsed.result.snapshot.templates[0])).toBe(true)
    expect(JSON.stringify(parsed)).not.toMatch(/face|quality|recommend|score/i)
  })

  it('parses only digest-bound thumbnail media and approved source evidence', () => {
    const parsed = parseThumbnailGenerateResponse(
      createThumbnailGenerateResponseValue(),
    )

    expect(parsed?.result.status).toBe('available')
    if (parsed?.result.status !== 'available') {
      throw new Error('Expected available thumbnail output.')
    }
    expect(parsed.result.output.selection.sourcePresentationTime).toEqual({
      value: '-1',
      rate: '20',
    })
    expect(parsed.result.output.mediaReference).toContain('/thumbnail/')
    expect(parsed.result.output.source).toMatchObject({
      rightsStatus: 'approved',
      externalPublicationAllowed: true,
    })
    expect(Object.isFrozen(parsed.result.output.selection)).toBe(true)
  })

  it('rejects unknown fields, private values, invalid time, and every opaque media cross-binding break', () => {
    const mutations: Array<(value: GenerateValue) => void> = [
      (value) => {
        value.privatePath = 'C:\\private\\source.mov'
      },
      (value) => {
        value.result.output.source.actorSid = 'S-1-5-21-private'
      },
      (value) => {
        value.result.output.mediaReference = 'file:///C:/private/output.png'
      },
      (value) => {
        value.result.output.projectId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'
      },
      (value) => {
        value.result.output.artifactId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'
      },
      (value) => {
        value.result.output.contentDigest = 'd'.repeat(64)
      },
      (value) => {
        value.result.output.origin.assetId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'
      },
      (value) => {
        value.result.output.source.rightsStatus = 'unconfirmed'
      },
      (value) => {
        value.result.output.source.externalPublicationAllowed = false
      },
      (value) => {
        value.result.output.selection.sourcePresentationTime = {
          value: '9223372036854775808',
          rate: '1',
        }
      },
      (value) => {
        value.result.output.selection.sourcePresentationTime = {
          value: '-2',
          rate: '20',
        }
      },
      (value) => {
        value.result.output.createdAt = '2041-02-03T05:15:06Z'
      },
      (value) => {
        value.result.output.createdAt = '2041-02-03T05:15:06.0000000Z'
      },
    ]

    for (const mutate of mutations) {
      const value = cloneGenerateValue()
      mutate(value)
      expect(parseThumbnailGenerateResponse(value)).toBeNull()
    }
  })

  it('requires status and nullable payloads to agree exactly', () => {
    for (const status of [
      'forbidden',
      'unavailable',
      'stale',
      'mismatched',
      'invalidTime',
      'rightsNotApproved',
      'templateUnavailable',
      'cancelled',
      'conflict',
      'failed',
    ]) {
      expect(
        parseThumbnailGenerateResponse({
          version: 1,
          requestId: thumbnailRequestId,
          method: 'thumbnail.generate',
          result: { status, output: null },
        }),
      ).not.toBeNull()
    }
    expect(
      parseThumbnailGenerateResponse({
        version: 1,
        requestId: thumbnailRequestId,
        method: 'thumbnail.generate',
        result: { status: 'available', output: null },
      }),
    ).toBeNull()
    expect(
      parseThumbnailReadResponse({
        version: 1,
        requestId: thumbnailRequestId,
        method: 'thumbnail.read',
        result: { status: 'privateFailure', snapshot: null },
      }),
    ).toBeNull()
    expect(
      parseThumbnailCancelResponse({
        ...createThumbnailCancelResponseValue(),
        result: { status: 'forbidden', outcome: null },
      }),
    ).not.toBeNull()
    expect(
      parseThumbnailCancelResponse({
        ...createThumbnailCancelResponseValue(),
        result: { status: 'forbidden', outcome: 'requested' },
      }),
    ).toBeNull()
  })

  it('accepts template catalogs without invented count or name limits', () => {
    const value = cloneReadValue()
    const template = value.result.snapshot.templates[0]!
    value.result.snapshot.templates = Array.from({ length: 33 }, (_, index) => ({
      ...template,
      approvalId: `10000000-0000-4000-8000-${String(index + 1).padStart(12, '0')}`,
      templateId: `20000000-0000-4000-8000-${String(index + 1).padStart(12, '0')}`,
      name: index === 0 ? 'x'.repeat(300) : `Approved template ${index + 1}`,
    }))

    const parsed = parseThumbnailReadResponse(value)

    expect(parsed?.result.status).toBe('available')
    if (parsed?.result.status !== 'available') {
      throw new Error('Expected available thumbnail evidence.')
    }
    expect(parsed.result.snapshot.templates).toHaveLength(33)
    expect(parsed.result.snapshot.templates[0]?.name).toHaveLength(300)
  })

  it('enforces canonical template ordering and the established response envelope', () => {
    const unsorted = cloneReadValue()
    unsorted.result.snapshot.templates.unshift({
      ...unsorted.result.snapshot.templates[0]!,
      templateId: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
    })
    expect(parseThumbnailReadResponse(unsorted)).toBeNull()

    const oversized = cloneReadValue()
    oversized.result.snapshot.templates[0]!.name = 'x'.repeat(
      BRIDGE_MAX_THUMBNAIL_RESPONSE_UTF8_BYTES,
    )
    expect(parseThumbnailReadResponse(oversized)).toBeNull()
  })
})

type GenerateValue = ReturnType<typeof cloneGenerateValue>

function cloneGenerateValue() {
  return JSON.parse(JSON.stringify(createThumbnailGenerateResponseValue())) as {
    version: number
    requestId: string
    method: string
    privatePath?: string
    result: {
      status: string
      output: {
        projectId: string
        artifactId: string
        contentDigest: string
        mediaReference: string
        createdAt: string
        source: {
          actorSid?: string
          assetId: string
          rightsStatus: string
          externalPublicationAllowed: boolean
        }
        selection: {
          sourcePresentationTime: { value: string; rate: string }
        }
        origin: { assetId: string }
      }
    }
  }
}

function cloneReadValue() {
  return JSON.parse(JSON.stringify(createThumbnailReadResponseValue())) as {
    version: number
    requestId: string
    method: string
    result: {
      status: string
      snapshot: {
        templates: Array<{
          templateId: string
          name: string
          [key: string]: unknown
        }>
      }
    }
  }
}
