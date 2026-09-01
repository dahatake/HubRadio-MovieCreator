import { DELIVERY_READ_METHOD } from './deliveryProtocol'

export const deliveryRequestId = '00112233-4455-4677-8899-aabbccddeeff'
export const deliverySecondRequestId = '10112233-4455-4677-8899-aabbccddeeff'
export const deliveryManifestId = '11112222-3333-4444-8555-666677778888'
export const deliveryProjectId = '21112222-3333-4444-8555-666677778888'
export const deliveryVersionId = '31112222-3333-4444-8555-666677778888'
export const deliverySecondVersionId = '32112222-3333-4444-8555-666677778888'
export const deliveryRenderManifestId = '41112222-3333-4444-8555-666677778888'
export const deliveryProfileId = '51112222-3333-4444-8555-666677778888'
export const deliveryVideoArtifactId = '61112222-3333-4444-8555-666677778888'
export const deliverySubtitleArtifactId = '71112222-3333-4444-8555-666677778888'
export const deliveryReviewRecordId = '81112222-3333-4444-8555-666677778888'
export const deliveryApprovalId = '91112222-3333-4444-8555-666677778888'
export const deliveryAssetId = 'a1112222-3333-4444-8555-666677778888'
export const deliverySourceSpanId = 'b1112222-3333-4444-8555-666677778888'
export const deliveryVersionDigest = 'a'.repeat(64)
export const deliveryProfileDigest = 'b'.repeat(64)
export const deliveryEvidenceDigest = 'c'.repeat(64)
export const deliveryUsedSourceSpansDigest = 'd'.repeat(64)
export const deliveryApprovalEvidenceDigest = 'e'.repeat(64)
export const deliveryVideoDigest = 'f'.repeat(64)
export const deliverySubtitleDigest = '0'.repeat(64)
export const deliverySourceDigest = '1'.repeat(64)
export const deliveryProbeDigest = '2'.repeat(64)
export const deliveryVideoTimingDigest = '3'.repeat(64)
export const deliveryAudioTimingDigest = '4'.repeat(64)
export const deliverySubtitleTimingDigest = '5'.repeat(64)
export const deliveryLumaDigest = '6'.repeat(64)
export const deliveryAudioDigest = '7'.repeat(64)
export const deliveryFfprobeDigest = '8'.repeat(64)
export const deliveryFfmpegDigest = '9'.repeat(64)
export const deliveryTimestamp = '2036-07-08T09:10:11.0000000+00:00'

export function createDeliveryResponseValue(
  requestId = deliveryRequestId,
  includeTrace = true,
) {
  return {
    version: 1,
    requestId,
    method: DELIVERY_READ_METHOD,
    result: {
      status: 'available',
      snapshot: {
        deliveryManifestId,
        projectId: deliveryProjectId,
        versionId: deliveryVersionId,
        versionDigest: deliveryVersionDigest,
        renderManifestId: deliveryRenderManifestId,
        profileId: deliveryProfileId,
        profileDigest: deliveryProfileDigest,
        deliveryEvidenceDigest,
        usedSourceSpansDigest: deliveryUsedSourceSpansDigest,
        approvalEvidenceDigest: deliveryApprovalEvidenceDigest,
        publicationScope: 'internalOnly',
        artifacts: [
          {
            artifactId: deliveryVideoArtifactId,
            artifactType: 'master',
            mediaType: 'video/mp4',
            language: null,
            byteSize: '4096',
            sha256: deliveryVideoDigest,
          },
          {
            artifactId: deliverySubtitleArtifactId,
            artifactType: 'subtitle',
            mediaType: 'text/vtt',
            language: 'ja-JP',
            byteSize: '512',
            sha256: deliverySubtitleDigest,
          },
        ],
        review: {
          records: [
            {
              reviewRecordId: deliveryReviewRecordId,
              state: 'approved',
              actorRole: 'contentApprover',
              recordedAt: deliveryTimestamp,
            },
          ],
          approvals: [
            {
              approvalId: deliveryApprovalId,
              actorRole: 'rightsApprover',
              approvedAt: deliveryTimestamp,
            },
          ],
        },
        rights: [
          {
            assetId: deliveryAssetId,
            sourceDigest: deliverySourceDigest,
            status: 'approved',
            evidenceRecorded: true,
            allowedUse: '番組内および承認済みの公開範囲で使用',
            externalPublicationAllowed: true,
          },
        ],
        qualityControl: {
          artifactId: deliveryVideoArtifactId,
          sha256: deliveryVideoDigest,
          byteSize: '4096',
          machineChecksPassed: true,
          humanReviewRequired: true,
          approvedNumericThresholdsApplied: false,
          canProceedToDelivery: false,
          decodedSuccessfully: true,
          probeEvidenceDigest: deliveryProbeDigest,
          videoTiming: {
            streamIndex: 0,
            observedCount: 300,
            presentationStart: { value: '0', rate: '1' },
            presentationEnd: { value: '10', rate: '1' },
            observedDuration: { value: '10', rate: '1' },
            evidenceDigest: deliveryVideoTimingDigest,
          },
          audioTiming: {
            streamIndex: 1,
            observedCount: 480000,
            presentationStart: { value: '1', rate: '100' },
            presentationEnd: { value: '1001', rate: '100' },
            observedDuration: { value: '10', rate: '1' },
            evidenceDigest: deliveryAudioTimingDigest,
          },
          subtitleTiming: {
            streamIndex: 2,
            observedCueCount: 3,
            evidenceDigest: deliverySubtitleTimingDigest,
          },
          duration: {
            expected: { value: '10', rate: '1' },
            videoDelta: { value: '0', rate: '1' },
            audioDelta: { value: '0', rate: '1' },
            audioVideoStartOffset: { value: '1', rate: '100' },
            audioVideoEndOffset: { value: '1', rate: '100' },
          },
          luma: {
            measuredFrameCount: 300,
            minimumFrameAverage: '12.5',
            maximumFrameAverage: '240.25',
            meanFrameAverage: '100',
            evidenceDigest: deliveryLumaDigest,
          },
          audio: {
            integratedLoudnessLufs: { kind: 'finite', value: '-16.2' },
            truePeakDbfs: { kind: 'finite', value: '-1' },
            evidenceDigest: deliveryAudioDigest,
          },
          tools: [
            {
              name: 'ffprobe',
              version: '7.1',
              executableDigest: deliveryFfprobeDigest,
              executableByteSize: '1000000',
            },
            {
              name: 'ffmpeg',
              version: '7.1',
              executableDigest: deliveryFfmpegDigest,
              executableByteSize: '2000000',
            },
          ],
          requiredHumanReviewAreas: [
            'duration',
            'audioVideoSynchronization',
            'subtitlePresentation',
            'screenLuma',
            'programLoudness',
            'truePeak',
          ],
        },
      },
      trace: includeTrace
        ? {
            status: 'resolved',
            outputTime: { value: '1', rate: '2' },
            originKind: 'sourceSpan',
            originId: deliverySourceSpanId,
            sourceTime: { value: '3', rate: '2' },
            sourceSpan: {
              sourceSpanId: deliverySourceSpanId,
              assetId: deliveryAssetId,
              streamId: 'video:0',
              start: { value: '1', rate: '1' },
              end: { value: '2', rate: '1' },
              sourceDigest: deliverySourceDigest,
            },
          }
        : null,
    },
  }
}

export function createDeliveryFailureValue(
  status: 'forbidden' | 'unavailable' | 'incomplete' | 'mismatched' | 'qualityControlFailed',
  requestId = deliveryRequestId,
) {
  return {
    version: 1,
    requestId,
    method: DELIVERY_READ_METHOD,
    result: { status, snapshot: null, trace: null },
  }
}
