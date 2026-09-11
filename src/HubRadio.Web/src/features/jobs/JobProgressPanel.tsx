import { useEffect, useMemo, useState, type FormEvent } from 'react'

import { isCanonicalBridgeId } from '../../bridge/protocol'
import { useAppState } from '../../state'
import {
  cancellationOutcomeLabels,
  jobStatusLabels,
  requestErrorLabels,
  retryOutcomeLabels,
  selectJobDisplayStatus,
  selectJobKindLabel,
  warningLabels,
} from './jobPresentation'

import './jobs.css'

const activePollMilliseconds = 1_500

export interface JobProgressPanelProps {
  readonly jobId?: string | null
  readonly context?: Readonly<{ jobId: string | null }>
  readonly active?: boolean
}

export function JobProgressPanel({
  jobId,
  context,
  active = true,
}: JobProgressPanelProps = {}) {
  const {
    state,
    monitorJob,
    refreshJob,
    cancelJob,
    retryJob,
    stopMonitoringJob,
  } = useAppState()
  const { jobMonitor } = state
  const [jobIdInput, setJobIdInput] = useState('')
  const [showInputError, setShowInputError] = useState(false)
  const normalizedJobId = jobIdInput.trim().toLowerCase()
  const inputIsValid = isCanonicalBridgeId(normalizedJobId)
  // An explicit context (including null) takes precedence over the shorthand ID.
  const suppliedJobId = context === undefined ? jobId : context.jobId
  const isStandalone = suppliedJobId === undefined
  const contextJobId =
    typeof suppliedJobId === 'string' && isCanonicalBridgeId(suppliedJobId)
      ? suppliedJobId
      : null
  const hasTrackedJob =
    jobMonitor.trackedJobId !== null &&
    (isStandalone || jobMonitor.trackedJobId === contextJobId)
  const snapshot = hasTrackedJob ? jobMonitor.snapshot : null
  const displayStatus = useMemo(
    () => (snapshot === null ? null : selectJobDisplayStatus(snapshot)),
    [snapshot],
  )

  useEffect(() => {
    if (isStandalone || contextJobId === jobMonitor.trackedJobId) {
      return
    }

    let superseded = false
    // Child effects run before the provider initializes its bridge client.
    // Cleanup only discards this deferred switch; visibility never stops a job.
    void Promise.resolve().then(() => {
      if (superseded) {
        return
      }

      if (jobMonitor.trackedJobId !== null) {
        stopMonitoringJob()
      }
      if (contextJobId !== null) {
        monitorJob(contextJobId)
      }
    })

    return () => {
      superseded = true
    }
  }, [contextJobId, isStandalone, jobMonitor.trackedJobId, monitorJob, stopMonitoringJob])

  useEffect(() => {
    const shouldPoll =
      hasTrackedJob &&
      (snapshot?.state === 'queued' ||
        snapshot?.state === 'running' ||
        jobMonitor.lastError === 'conflict')
    if (!shouldPoll) {
      return
    }

    const timer = window.setInterval(refreshJob, activePollMilliseconds)
    return () => window.clearInterval(timer)
  }, [hasTrackedJob, jobMonitor.lastError, jobMonitor.trackedJobId, refreshJob, snapshot?.state])

  const submitJob = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!inputIsValid) {
      setShowInputError(true)
      return
    }

    setShowInputError(false)
    monitorJob(normalizedJobId)
  }

  const stopMonitoring = () => {
    stopMonitoringJob()
    setShowInputError(false)
  }

  const isPending = hasTrackedJob && jobMonitor.pending !== null
  const canCancel =
    snapshot !== null &&
    (snapshot.state === 'queued' || snapshot.state === 'running') &&
    !snapshot.cancellationRequested
  const retryIsEligible =
    snapshot !== null &&
    (snapshot.state === 'failed' || snapshot.state === 'interrupted') &&
    snapshot.retryability === 'retryable'

  return (
    <section
      className="job-panel job-panel-compact"
      aria-labelledby="job-panel-title"
      hidden={!active}
    >
      <div className="section-heading">
        <div>
          <p className="eyebrow">バックグラウンド処理</p>
          <h2 id="job-panel-title">ジョブの進行状況</h2>
        </div>
        {isStandalone && hasTrackedJob && (
          <button className="button button-secondary" type="button" onClick={stopMonitoring}>
            表示を閉じる
          </button>
        )}
      </div>

      {isStandalone && (
        <form className="job-lookup" onSubmit={submitJob} noValidate>
          <label htmlFor="job-id">ジョブ ID</label>
          <div className="job-lookup-row">
            <input
              id="job-id"
              name="jobId"
              type="text"
              maxLength={64}
              value={jobIdInput}
              onChange={(event) => {
                setJobIdInput(event.currentTarget.value)
                setShowInputError(false)
              }}
              aria-describedby="job-id-help job-id-error"
              aria-invalid={showInputError && !inputIsValid}
              autoComplete="off"
              spellCheck={false}
            />
            <button className="button button-primary" type="submit" disabled={isPending}>
              状態を表示
            </button>
          </div>
          <p id="job-id-help" className="field-help">
            小文字 UUID 形式のジョブ ID を指定します。
          </p>
          <p id="job-id-error" className="field-error" role="alert">
            {showInputError && !inputIsValid
              ? 'ジョブ ID を正しい UUID 形式で入力してください。'
              : ''}
          </p>
        </form>
      )}

      {!isStandalone && suppliedJobId === null && (
        <p className="job-unstarted" role="status">
          未開始 — 処理を開始すると進行状況が表示されます。
        </p>
      )}
      {!isStandalone && suppliedJobId !== null && contextJobId === null && (
        <p className="request-error" role="alert">
          処理の状態は未取得です。対象の処理を選び直してください。
        </p>
      )}

      {(hasTrackedJob || (!isStandalone && contextJobId !== null)) && (
        <div className="job-status-region" aria-live="polite" aria-atomic="true">
          {snapshot === null ? (
            <>
              <p className="job-loading" role="status">
                {!hasTrackedJob || isPending
                  ? 'ジョブの状態を取得しています…'
                  : '状態を取得できませんでした。'}
              </p>
              {hasTrackedJob && (
                <div className="job-actions" aria-label="ジョブ操作">
                  <button
                    className="button button-secondary"
                    type="button"
                    disabled={isPending}
                    onClick={refreshJob}
                  >
                    最新状態を取得
                  </button>
                </div>
              )}
            </>
          ) : (
            <article className="job-card" data-job-status={displayStatus}>
              <header className="job-card-header">
                <div>
                  <p className="job-kind">{selectJobKindLabel(snapshot)}</p>
                  <h3>{displayStatus === null ? '' : jobStatusLabels[displayStatus]}</h3>
                </div>
              </header>

              {snapshot.state === 'running' && (
                <div className="progress-group">
                  <label htmlFor="job-progress">進行状況</label>
                  <progress
                    id="job-progress"
                    max={100}
                    value={
                      snapshot.progressRatio === null
                        ? undefined
                        : Math.round(snapshot.progressRatio * 100)
                    }
                  />
                  <span>
                    {snapshot.progressRatio === null
                      ? '処理中'
                      : `${Math.round(snapshot.progressRatio * 100)}%`}
                  </span>
                </div>
              )}

              {snapshot.state === 'interrupted' && (
                <p className="notice notice-neutral">
                  処理は中断されました。
                  {snapshot.retryability === 'retryable'
                    ? ' 再試行できます。'
                    : ' 再試行できません。'}
                </p>
              )}

              {snapshot.failure !== null && (
                <p className="notice notice-neutral">
                  {failureLabel(snapshot.failure)}
                </p>
              )}

              {snapshot.kind === 'unknown' && (
                <p className="notice notice-neutral">
                  このバージョンではジョブ種別の詳細を表示できません。
                </p>
              )}

              {snapshot.reconciliation === 'required' && (
                <p className="notice notice-warning" role="alert">
                  解析結果が現在の素材リビジョンと一致しません。再解析してから続行してください。
                </p>
              )}
              {snapshot.reconciliation === 'unknown' && (
                <p className="notice notice-warning">
                  解析結果と現在の素材との整合性を確認できません。
                </p>
              )}

              {snapshot.warnings.length > 0 && (
                <div className="warning-list" aria-labelledby="job-warning-title">
                  <h4 id="job-warning-title">素材に関する注意</h4>
                  <ul>
                    {snapshot.warnings.map((warning) => (
                      <li key={warning}>{warningLabels[warning]}</li>
                    ))}
                  </ul>
                </div>
              )}

              <div className="job-actions" aria-label="ジョブ操作">
                <button
                  className="button button-secondary"
                  type="button"
                  disabled={isPending}
                  onClick={refreshJob}
                >
                  最新状態を取得
                </button>
                {canCancel && (
                  <button
                    className="button button-danger"
                    type="button"
                    disabled={isPending}
                    onClick={cancelJob}
                  >
                    キャンセル
                  </button>
                )}
                {(snapshot.state === 'failed' || snapshot.state === 'interrupted') && (
                  <button
                    className="button button-primary"
                    type="button"
                    disabled={isPending || !retryIsEligible}
                    aria-describedby={!retryIsEligible ? 'retry-disabled-reason' : undefined}
                    onClick={retryJob}
                  >
                    再試行
                  </button>
                )}
              </div>

              {!retryIsEligible &&
                (snapshot.state === 'failed' || snapshot.state === 'interrupted') && (
                  <p id="retry-disabled-reason" className="field-help">
                    このジョブは安全に再試行できません。
                  </p>
                )}

              <details className="job-details">
                <summary>処理の詳細</summary>
                <dl className="job-metadata">
                  <div>
                    <dt>試行</dt>
                    <dd>{snapshot.currentAttemptNumber}</dd>
                  </div>
                  <div>
                    <dt>更新日時</dt>
                    <dd>{new Date(snapshot.updatedAt).toLocaleString('ja-JP')}</dd>
                  </div>
                  <div>
                    <dt>リビジョン</dt>
                    <dd>{snapshot.revision}</dd>
                  </div>
                </dl>
              </details>
            </article>
          )}

          {hasTrackedJob && jobMonitor.lastCommand !== null && (
            <p className="command-result" role="status">
              {jobMonitor.lastCommand.operation === 'cancel'
                ? cancellationOutcomeLabels[jobMonitor.lastCommand.outcome]
                : retryOutcomeLabels[jobMonitor.lastCommand.outcome]}
            </p>
          )}
          {hasTrackedJob && jobMonitor.lastError !== null && (
            <p className="request-error" role="alert">
              {requestErrorLabels[jobMonitor.lastError]}
            </p>
          )}
        </div>
      )}
    </section>
  )
}

function failureLabel(failure: 'timedOut' | 'appRestarted' | 'other'): string {
  switch (failure) {
    case 'timedOut':
      return '処理がタイムアウトしました。'
    case 'appRestarted':
      return 'アプリの再起動により処理が中断されました。'
    case 'other':
      return '処理中にエラーが発生しました。'
  }
}