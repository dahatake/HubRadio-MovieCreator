export const timelineRequestId = '00112233-4455-6677-8899-aabbccddeeff'
export const timelineProjectId = '11111111-1111-4111-8111-111111111111'
export const timelineWorkingCopyId = '22222222-2222-4222-8222-222222222222'
export const timelineSourceElementId = '33333333-3333-4333-8333-333333333333'
export const timelineAssetElementId = '44444444-4444-4444-8444-444444444444'
export const timelineRightElementId = '55555555-5555-4555-8555-555555555555'
export const timelineSourceSpanId = '66666666-6666-4666-8666-666666666666'
export const timelineAssetId = '77777777-7777-4777-8777-777777777777'
export const timelineVersionId = '88888888-8888-4888-8888-888888888888'

export function createTimelineSnapshotValue() {
  return {
    workingCopyId: timelineWorkingCopyId,
    projectId: timelineProjectId,
    baseVersionId: null as string | null,
    revision: 3,
    createdAt: '2026-09-01T02:03:04.0000000+00:00',
    updatedAt: '2026-09-01T02:04:04.0000000+00:00',
    elements: [
      {
        id: timelineSourceElementId,
        outputRange: {
          start: { value: '0', rate: '1' },
          duration: { value: '1001', rate: '24000' },
        },
        origin: { kind: 'sourceSpan', sourceSpanId: timelineSourceSpanId },
        sourceRange: {
          start: { value: '-1', rate: '10' },
          duration: { value: '1001', rate: '24000' },
        },
      },
      {
        id: timelineAssetElementId,
        outputRange: {
          start: { value: '1001', rate: '24000' },
          duration: { value: '1', rate: '2' },
        },
        origin: { kind: 'asset', assetId: timelineAssetId },
        sourceRange: null,
      },
    ],
  }
}

export function createTimelineReadResponseValue() {
  return {
    version: 1,
    requestId: timelineRequestId,
    method: 'timeline.read',
    result: {
      status: 'available',
      snapshot: createTimelineSnapshotValue(),
    },
  }
}

export function createTimelineApplyResponseValue() {
  const value = createTimelineReadResponseValue()
  return {
    ...value,
    method: 'timeline.apply',
    result: {
      status: 'available',
      snapshot: {
        ...value.result.snapshot,
        baseVersionId: timelineVersionId as string | null,
        revision: value.result.snapshot.revision + 1,
      },
    },
  }
}