export const thumbnailRequestId = '11111111-1111-4111-8111-111111111111'
export const thumbnailJobId = '22222222-2222-4222-8222-222222222222'
export const thumbnailGenerationId = '33333333-3333-4333-8333-333333333333'
export const thumbnailProjectId = '44444444-4444-4444-8444-444444444444'
export const thumbnailSourceArtifactId = '55555555-5555-4555-8555-555555555555'
export const thumbnailSourceAssetId = '66666666-6666-4666-8666-666666666666'
export const thumbnailApprovalId = '77777777-7777-4777-8777-777777777777'
export const thumbnailTemplateId = '88888888-8888-4888-8888-888888888888'
export const thumbnailProfileId = '99999999-9999-4999-8999-999999999999'
export const thumbnailLayoutId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
export const thumbnailOutputArtifactId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
export const thumbnailEvidenceDigest = '1'.repeat(64)
export const thumbnailPlayerEvidenceDigest = '2'.repeat(64)
export const thumbnailSourceDigest = '3'.repeat(64)
export const thumbnailRightsDigest = '4'.repeat(64)
export const thumbnailPtsDigest = '5'.repeat(64)
export const thumbnailFrameMapDigest = '6'.repeat(64)
export const thumbnailApprovalDigest = '7'.repeat(64)
export const thumbnailTemplateDigest = '8'.repeat(64)
export const thumbnailProfileDigest = '9'.repeat(64)
export const thumbnailLayoutDigest = 'a'.repeat(64)
export const thumbnailBindingDigest = 'b'.repeat(64)
export const thumbnailOutputDigest = 'c'.repeat(64)

export function createThumbnailReadResponseValue() {
  return {
    version: 1,
    requestId: thumbnailRequestId,
    method: 'thumbnail.read',
    result: {
      status: 'available',
      snapshot: {
        evidenceDigest: thumbnailEvidenceDigest,
        playerEvidenceDigest: thumbnailPlayerEvidenceDigest,
        projectId: thumbnailProjectId,
        sourceArtifactId: thumbnailSourceArtifactId,
        sourceAssetId: thumbnailSourceAssetId,
        sourceContentDigest: thumbnailSourceDigest,
        sourceRightsDigest: thumbnailRightsDigest,
        sourcePtsIndexDigest: thumbnailPtsDigest,
        proxyFrameMapDigest: thumbnailFrameMapDigest,
        sourcePresentationStart: { value: '-1', rate: '10' },
        sourcePresentationEnd: { value: '7', rate: '60' },
        rights: {
          status: 'approved',
          externalPublicationAllowed: true,
        },
        templates: [
          {
            approvalId: thumbnailApprovalId,
            approvalDigest: thumbnailApprovalDigest,
            approvalRole: 'brandApprover',
            approvedAt: '2041-02-03T04:15:06.0000000+00:00',
            templateId: thumbnailTemplateId,
            name: 'Approved manual thumbnail',
            templateVersionDigest: thumbnailTemplateDigest,
            renderProfileId: thumbnailProfileId,
            renderProfileDigest: thumbnailProfileDigest,
            layoutSpecId: thumbnailLayoutId,
            layoutSpecDigest: thumbnailLayoutDigest,
            width: 1280,
            height: 720,
          },
        ],
      },
    },
  }
}

export function createThumbnailGenerateResponseValue() {
  return {
    version: 1,
    requestId: thumbnailRequestId,
    method: 'thumbnail.generate',
    result: {
      status: 'available',
      output: {
        generationId: thumbnailGenerationId,
        artifactId: thumbnailOutputArtifactId,
        projectId: thumbnailProjectId,
        mediaReference:
          `https://player-media.appassets.example/media/v1/${thumbnailProjectId}` +
          `/thumbnail/${thumbnailOutputArtifactId}/${thumbnailOutputDigest}`,
        bindingDigest: thumbnailBindingDigest,
        contentDigest: thumbnailOutputDigest,
        byteSize: '2048',
        width: 1280,
        height: 720,
        createdAt: '2041-02-03T05:15:06.0000000+00:00',
        wasAlreadyRegistered: false,
        source: {
          artifactId: thumbnailSourceArtifactId,
          assetId: thumbnailSourceAssetId,
          contentDigest: thumbnailSourceDigest,
          ptsIndexDigest: thumbnailPtsDigest,
          frameMapDigest: thumbnailFrameMapDigest,
          rightsStatus: 'approved',
          externalPublicationAllowed: true,
        },
        selection: {
          proxyFrameSequence: 1,
          sourceFrameSequence: 7,
          sourcePresentationTime: { value: '-1', rate: '20' },
          selectedAt: '2041-02-03T05:10:06.0000000+00:00',
        },
        template: {
          approvalId: thumbnailApprovalId,
          approvalDigest: thumbnailApprovalDigest,
          approvalRole: 'brandApprover',
          approvedAt: '2041-02-03T04:15:06.0000000+00:00',
          templateId: thumbnailTemplateId,
          templateVersionDigest: thumbnailTemplateDigest,
          renderProfileId: thumbnailProfileId,
          renderProfileDigest: thumbnailProfileDigest,
          layoutSpecId: thumbnailLayoutId,
          layoutSpecDigest: thumbnailLayoutDigest,
        },
        origin: {
          kind: 'asset',
          assetId: thumbnailSourceAssetId,
        },
      },
    },
  }
}

export function createThumbnailCancelResponseValue(
  outcome: 'requested' | 'alreadyRequested' | 'notRunning' | 'notFound' =
    'requested',
) {
  return {
    version: 1,
    requestId: thumbnailRequestId,
    method: 'thumbnail.cancel',
    result: { status: 'available', outcome },
  }
}
