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

const activePollMilliseconds = 1_500

export function JobProgressPanel() {
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
  const snapshot = jobMonitor.snapshot
  const displayStatus = useMemo(
    () => (snapshot === null ? null : selectJobDisplayStatus(snapshot)),
    [snapshot],
  )

  useEffect(() => {
    const shouldPoll =
      jobMonitor.trackedJobId !== null &&
      (snapshot?.state === 'queued' ||
        snapshot?.state === 'running' ||
        jobMonitor.lastError === 'conflict')
    if (!shouldPoll) {
      return
    }

    const timer = window.setInterval(refreshJob, activePollMilliseconds)
    return () => window.clearInterval(timer)
  }, [jobMonitor.lastError, jobMonitor.trackedJobId, refreshJob, snapshot?.state])

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

  const isPending = jobMonitor.pending !== null
  const canCancel =
    snapshot !== null &&
    (snapshot.state === 'queued' || snapshot.state === 'running') &&
    !snapshot.cancellationRequested
  const retryIsEligible =
    snapshot !== null &&
    (snapshot.state === 'failed' || snapshot.state === 'interrupted') &&
    snapshot.retryability === 'retryable'

  return (
    <section className="job-panel" aria-labelledby="job-panel-title">
      <div className="section-heading">
        <div>
          <p className="eyebrow">バックグラウンド処理</p>
          <h2 id="job-panel-title">ジョブの進行状況</h2>
        </div>
        {jobMonitor.trackedJobId !== null && (
          <button className="button button-secondary" type="button" onClick={stopMonitoring}>
            表示を閉じる
          </button>
        )}
      </div>

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

      {jobMonitor.trackedJobId !== null && (
        <div className="job-status-region" aria-live="polite" aria-atomic="true">
          {snapshot === null ? (
            <p className="job-loading" role="status">
              {isPending ? 'ジョブの状態を取得しています…' : '状態を取得できませんでした。'}
            </p>
          ) : (
            <article className="job-card" data-job-status={displayStatus}>
              <header className="job-card-header">
                <div>
                  <p className="job-kind">{selectJobKindLabel(snapshot)}</p>
                  <h3>{displayStatus === null ? '' : jobStatusLabels[displayStatus]}</h3>
                </div>
                <span className="job-status-badge">
                  試行 {snapshot.currentAttemptNumber}
                </span>
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

              <dl className="job-metadata">
                <div>
                  <dt>更新日時</dt>
                  <dd>{new Date(snapshot.updatedAt).toLocaleString('ja-JP')}</dd>
                </div>
                <div>
                  <dt>リビジョン</dt>
                  <dd>{snapshot.revision}</dd>
                </div>
              </dl>

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
            </article>
          )}

          {jobMonitor.lastCommand !== null && (
            <p className="command-result" role="status">
              {jobMonitor.lastCommand.operation === 'cancel'
                ? cancellationOutcomeLabels[jobMonitor.lastCommand.outcome]
                : retryOutcomeLabels[jobMonitor.lastCommand.outcome]}
            </p>
          )}
          {jobMonitor.lastError !== null && (
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