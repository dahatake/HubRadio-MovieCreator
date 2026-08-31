import type {
  BridgeJobSnapshot,
  JobCancellationOutcome,
  JobRetryOutcome,
  JobWarningCode,
} from '../../bridge/protocol'
import type { JobRequestError } from '../../state'

export type JobDisplayStatus =
  | 'queued'
  | 'running'
  | 'retryableFailed'
  | 'nonRetryableFailed'
  | 'cancelRequested'
  | 'cancelled'
  | 'interrupted'
  | 'requeued'
  | 'succeeded'

export const jobStatusLabels: Readonly<Record<JobDisplayStatus, string>> = {
  queued: '待機中',
  running: '実行中',
  retryableFailed: '失敗（再試行可能）',
  nonRetryableFailed: '失敗（再試行不可）',
  cancelRequested: 'キャンセル要求済み',
  cancelled: 'キャンセル済み',
  interrupted: '中断',
  requeued: '再キュー済み',
  succeeded: '完了',
}

export const warningLabels: Readonly<Record<JobWarningCode, string>> = {
  audioTrackAbsent: '音声トラックがありません。',
  durationFieldsDiffer: 'メディアの長さ情報に差異があります。',
  lowResolution: '映像の解像度が低い可能性があります。',
  multipleAudioStreams: '複数の音声ストリームが含まれています。',
  multipleVideoStreams: '複数の映像ストリームが含まれています。',
  signatureFormatMismatch: 'ファイル署名と形式情報が一致しません。',
  unrecognizedContainerSignature: 'コンテナー署名を認識できません。',
  variableRateFieldsDiffer: '可変フレームレート情報に差異があります。',
}

export const requestErrorLabels: Readonly<Record<JobRequestError, string>> = {
  bridge_unavailable: 'ホスト接続を利用できません。',
  bridge_send_failed: 'ホストへ要求を送信できませんでした。',
  bridge_timeout: 'ホストから応答がありません。もう一度状態を取得してください。',
  forbidden: 'このジョブを操作する権限がありません。',
  notFound: '指定されたジョブが見つかりません。',
  conflict: 'ジョブの状態が更新されています。最新状態を再取得します。',
  unavailable: 'ジョブサービスを現在利用できません。',
}

export const cancellationOutcomeLabels: Readonly<
  Record<JobCancellationOutcome, string>
> = {
  requested: 'キャンセルを要求しました。',
  alreadyRequested: 'キャンセルはすでに要求されています。',
  notRunning: 'ジョブはすでに停止しているため、キャンセルしませんでした。',
  notTracked: '実行中の処理が見つからないため、キャンセルしませんでした。',
}

export const retryOutcomeLabels: Readonly<Record<JobRetryOutcome, string>> = {
  queued: 'ジョブを再キューしました。',
  stateNotEligible: '現在の状態では再試行できません。',
  errorNotRetryable: 'この失敗は再試行できません。',
  coordinatorRunActive: '処理がまだ終了していないため再試行できません。',
}

export function selectJobDisplayStatus(
  snapshot: BridgeJobSnapshot,
): JobDisplayStatus {
  if (snapshot.state === 'queued') {
    return snapshot.currentAttemptNumber > 0 ? 'requeued' : 'queued'
  }

  if (snapshot.state === 'running') {
    return snapshot.cancellationRequested ? 'cancelRequested' : 'running'
  }

  if (snapshot.state === 'failed') {
    return snapshot.retryability === 'retryable'
      ? 'retryableFailed'
      : 'nonRetryableFailed'
  }

  return snapshot.state
}

export function selectJobKindLabel(snapshot: BridgeJobSnapshot): string {
  switch (snapshot.kind) {
    case 'mediaIngest':
      return 'メディア取り込み'
    case 'finalRender':
      return '最終レンダー'
    case 'unknown':
      return '不明なジョブ'
  }
}