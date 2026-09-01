import {
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
} from 'react'

import {
  isCanonicalReviewEntityId,
  isReviewOutputTime,
  isReviewTextInput,
  REVIEW_ADD_COMMENT_METHOD,
  REVIEW_GET_SNAPSHOT_METHOD,
  REVIEW_RECORD_APPROVAL_METHOD,
  REVIEW_REQUEST_CHANGES_METHOD,
  type ReviewActionKind,
  type ReviewActionStatus,
  type ReviewActorRole,
  type ReviewApproval,
  type ReviewApprovalActorRole,
  type ReviewAuthorActorRole,
  type ReviewCompositionChange,
  type ReviewRationalTime,
  type ReviewSnapshot,
  type ReviewState,
} from './reviewProtocol'
import {
  useReviewState,
  type ReviewStateContextValue,
} from './ReviewStateProvider'
import { selectReviewAction } from './reviewState'

const stateLabels: Readonly<Record<ReviewState, string>> = {
  editing: '編集中',
  editorReview: '編集レビュー',
  performerReview: '出演者レビュー',
  contentReview: '内容レビュー',
  brandReview: 'ブランドレビュー',
  changesRequested: '変更依頼済み',
  approved: '承認済み',
  publicationBlocked: '公開停止',
  finalRendering: '最終レンダー中',
  qualityControl: '品質確認',
  exported: 'エクスポート済み',
  published: '公開記録済み',
}

const roleLabels: Readonly<Record<ReviewActorRole, string>> = {
  editor: '編集担当',
  performerReviewer: '出演者レビュー担当',
  contentApprover: '内容承認担当',
  brandApprover: 'ブランド承認担当',
  rightsApprover: '権利承認担当',
  administrator: '管理担当',
}

const actionKinds: readonly ReviewActionKind[] = [
  'addComment',
  'requestChanges',
  'recordApproval',
]

const actionLabels: Readonly<Record<ReviewActionKind, string>> = {
  addComment: 'コメント',
  requestChanges: '変更依頼',
  recordApproval: '承認',
}

const actionStatusMessages: Readonly<Record<ReviewActionStatus, string>> = {
  authorized: 'この版に対して実行できます。',
  actorNotAuthorized: '現在の Windows ユーザーには、この操作の役割が割り当てられていません。',
  alreadyRecorded: '現在の Windows ユーザーが割り当てられた各役割では、この版の承認をすでに記録済みです。',
  stateNotEligible: '現在のレビュー状態では、この操作を実行できません。',
  targetNotCurrent: 'この版は現行の作業対象ではないため、書き込みできません。',
  reviewStateUnrecorded: 'レビュー状態が未記録のため、この操作を実行できません。',
  publicationBlocked: '公開停止は終端状態です。この版から復旧操作は実行できません。',
}

const operationLabels = {
  [REVIEW_GET_SNAPSHOT_METHOD]: 'レビューを読み込みました。',
  [REVIEW_ADD_COMMENT_METHOD]: 'タイムコードコメントを記録しました。',
  [REVIEW_REQUEST_CHANGES_METHOD]: '変更依頼を記録しました。',
  [REVIEW_RECORD_APPROVAL_METHOD]: '承認を記録しました。',
} as const

export function ReviewPanel() {
  return <ReviewPanelView {...useReviewState()} />
}

export function ReviewPanelView({
  state,
  changeVersionId,
  loadSnapshot,
  refreshSnapshot,
  addComment,
  requestChanges,
  recordApproval,
  clearError,
}: ReviewStateContextValue) {
  const pending = state.pending !== null
  const canLoad =
    !pending && isCanonicalReviewEntityId(state.versionIdInput)

  return (
    <section className="review-panel" aria-labelledby="review-title">
      <div className="review-heading">
        <div>
          <p className="eyebrow">Version-bound review</p>
          <h2 id="review-title">レビュー・コメント・承認</h2>
          <p>
            構成版と完全な内容 digest を固定し、追記専用のレビュー根拠を表示します。
          </p>
        </div>
        {state.snapshot !== null && (
          <span
            className={`review-target-badge ${
              state.snapshot.isCurrentTarget ? 'current' : 'stale'
            }`}
          >
            {state.snapshot.isCurrentTarget ? '現行版' : '非現行版'}
          </span>
        )}
      </div>

      <form
        className="review-load-form"
        onSubmit={(event) => {
          event.preventDefault()
          loadSnapshot()
        }}
      >
        <label htmlFor="review-version-id">構成版 ID</label>
        <div className="review-load-row">
          <input
            id="review-version-id"
            name="reviewVersionId"
            type="text"
            autoComplete="off"
            spellCheck={false}
            minLength={36}
            maxLength={36}
            pattern="[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
            value={state.versionIdInput}
            disabled={pending}
            aria-describedby="review-version-help"
            onChange={(event) => changeVersionId(event.currentTarget.value)}
          />
          <button type="submit" disabled={!canLoad}>
            レビューを開く
          </button>
          {state.snapshot !== null && (
            <button
              className="secondary-button"
              type="button"
              disabled={pending}
              onClick={refreshSnapshot}
            >
              再読み込み
            </button>
          )}
        </div>
        <p id="review-version-help" className="field-help">
          小文字 UUID の構成版 ID を指定します。ユーザー ID や役割はネイティブ側で判定されます。
        </p>
      </form>

      {state.error !== null && (
        <div className="review-error" role="alert">
          <div>
            <strong>レビュー操作を完了できませんでした</strong>
            <p>{state.error.message}</p>
          </div>
          <div className="review-error-actions">
            {(state.error.code === 'review.conflict' ||
              state.error.code === 'review.stale_version') &&
              state.snapshot !== null && (
                <button
                  className="secondary-button"
                  type="button"
                  disabled={pending}
                  onClick={refreshSnapshot}
                >
                  現在の状態を再読み込み
                </button>
              )}
            <button
              className="quiet-button"
              type="button"
              onClick={clearError}
            >
              閉じる
            </button>
          </div>
        </div>
      )}

      <p
        className="review-operation-status"
        role="status"
        aria-live="polite"
        data-review-pending={pending ? 'true' : 'false'}
      >
        {state.pending !== null
          ? 'ネイティブ側でレビュー根拠を確認しています。'
          : state.lastCompletedMethod === null
            ? '構成版を指定してレビューを読み込んでください。'
            : operationLabels[state.lastCompletedMethod]}
      </p>

      {state.snapshot === null ? (
        <div className="review-empty">
          <strong>レビュー対象は未選択です</strong>
          <p>構成版 ID を読み込むまで、レビュー根拠や操作は表示されません。</p>
        </div>
      ) : (
        <ReviewWorkspace
          snapshot={state.snapshot}
          pending={pending}
          addComment={addComment}
          requestChanges={requestChanges}
          recordApproval={recordApproval}
        />
      )}
    </section>
  )
}

export function getReviewActionAfterKey(
  currentIndex: number,
  key: string,
): number | null {
  if (
    !Number.isInteger(currentIndex) ||
    currentIndex < 0 ||
    currentIndex >= actionKinds.length
  ) {
    return null
  }

  switch (key) {
    case 'ArrowLeft':
    case 'ArrowUp':
      return currentIndex === 0 ? actionKinds.length - 1 : currentIndex - 1
    case 'ArrowRight':
    case 'ArrowDown':
      return currentIndex === actionKinds.length - 1 ? 0 : currentIndex + 1
    case 'Home':
      return 0
    case 'End':
      return actionKinds.length - 1
    default:
      return null
  }
}

function ReviewWorkspace({
  snapshot,
  pending,
  addComment,
  requestChanges,
  recordApproval,
}: Readonly<{
  snapshot: ReviewSnapshot
  pending: boolean
  addComment: ReviewStateContextValue['addComment']
  requestChanges: ReviewStateContextValue['requestChanges']
  recordApproval: ReviewStateContextValue['recordApproval']
}>) {
  return (
    <div className="review-workspace">
      <VersionSummary snapshot={snapshot} />
      {!snapshot.isCurrentTarget && (
        <div className="review-stale-warning" role="alert">
          <strong>この版は現行の作業対象ではありません</strong>
          <p>
            コメント、変更依頼、承認は記録できません。表示される承認の有効性は選択した版を基準にしています。
          </p>
        </div>
      )}
      {snapshot.reviewState === 'publicationBlocked' && (
        <div className="publication-blocked" role="alert">
          <strong>この構成版は公開停止です</strong>
          <p>{snapshot.reviewStateReason}</p>
          <p>公開停止はこの版の終端状態です。この画面は復旧操作を提供しません。</p>
        </div>
      )}
      {snapshot.reviewState === 'changesRequested' && (
        <div className="changes-requested" role="status">
          <strong>変更依頼が記録されています</strong>
          <p>{snapshot.reviewStateReason}</p>
          <p>修正は新しい子版として作成し、改めてレビューしてください。</p>
        </div>
      )}
      <ReviewActions
        snapshot={snapshot}
        pending={pending}
        addComment={addComment}
        requestChanges={requestChanges}
        recordApproval={recordApproval}
      />
      <CommentThread comments={snapshot.comments} />
      <ApprovalTable approvals={snapshot.approvals} />
      <DirectParentDiffView
        diff={snapshot.directParentDiff}
        parentVersionId={snapshot.parentVersionId}
      />
    </div>
  )
}

function VersionSummary({ snapshot }: Readonly<{ snapshot: ReviewSnapshot }>) {
  return (
    <section className="review-card version-summary" aria-labelledby="review-version-title">
      <div className="review-card-heading">
        <div>
          <p className="section-kicker">Pinned target</p>
          <h3 id="review-version-title">対象構成版</h3>
        </div>
        <span
          className={`review-state-badge state-${snapshot.reviewState ?? 'unrecorded'}`}
        >
          {snapshot.reviewState === null
            ? '状態未記録'
            : stateLabels[snapshot.reviewState]}
        </span>
      </div>
      <dl className="review-metadata">
        <div>
          <dt>構成版 ID</dt>
          <dd><code>{snapshot.versionId}</code></dd>
        </div>
        <div className="digest-row">
          <dt>内容 digest（SHA-256・完全値）</dt>
          <dd><code>{snapshot.versionDigest}</code></dd>
        </div>
        <div>
          <dt>作成日時</dt>
          <dd><time dateTime={snapshot.createdAt}>{formatUtc(snapshot.createdAt)}</time></dd>
        </div>
        <div>
          <dt>直接の親版</dt>
          <dd>{snapshot.parentVersionId === null ? 'なし' : <code>{snapshot.parentVersionId}</code>}</dd>
        </div>
      </dl>
      {snapshot.reviewState === null ? (
        <p className="review-unrecorded-note">
          レビュー状態は未記録です。`editing` として推測・補完していません。
        </p>
      ) : (
        <dl className="review-state-record">
          <div>
            <dt>記録した役割</dt>
            <dd>{roleLabels[snapshot.reviewStateActorRole!]}</dd>
          </div>
          <div>
            <dt>記録日時</dt>
            <dd>
              <time dateTime={snapshot.reviewStateRecordedAt!}>
                {formatUtc(snapshot.reviewStateRecordedAt!)}
              </time>
            </dd>
          </div>
          <div className="reason-row">
            <dt>理由</dt>
            <dd>{snapshot.reviewStateReason}</dd>
          </div>
        </dl>
      )}
    </section>
  )
}

function ReviewActions({
  snapshot,
  pending,
  addComment,
  requestChanges,
  recordApproval,
}: Readonly<{
  snapshot: ReviewSnapshot
  pending: boolean
  addComment: ReviewStateContextValue['addComment']
  requestChanges: ReviewStateContextValue['requestChanges']
  recordApproval: ReviewStateContextValue['recordApproval']
}>) {
  const [selectedIndex, setSelectedIndex] = useState(0)
  const tabs = useRef<Array<HTMLButtonElement | null>>([])

  const onTabKeyDown = (
    event: KeyboardEvent<HTMLButtonElement>,
    index: number,
  ): void => {
    const nextIndex = getReviewActionAfterKey(index, event.key)
    if (nextIndex === null) {
      return
    }
    event.preventDefault()
    setSelectedIndex(nextIndex)
    tabs.current[nextIndex]?.focus()
  }

  return (
    <section className="review-card review-actions" aria-labelledby="review-actions-title">
      <p className="section-kicker">Native-authorized actions</p>
      <h3 id="review-actions-title">レビュー操作</h3>
      <div className="review-action-tabs" role="tablist" aria-label="レビュー操作の選択">
        {actionKinds.map((kind, index) => (
          <button
            key={kind}
            id={`review-action-tab-${kind}`}
            role="tab"
            type="button"
            ref={(element) => {
              tabs.current[index] = element
            }}
            aria-selected={selectedIndex === index}
            aria-controls={`review-action-panel-${kind}`}
            tabIndex={selectedIndex === index ? 0 : -1}
            onClick={() => setSelectedIndex(index)}
            onKeyDown={(event) => onTabKeyDown(event, index)}
          >
            {actionLabels[kind]}
          </button>
        ))}
      </div>
      {actionKinds.map((kind, index) => {
        const action = selectReviewAction(snapshot, kind)
        const status = action.status
        return (
          <div
            key={kind}
            id={`review-action-panel-${kind}`}
            role="tabpanel"
            aria-labelledby={`review-action-tab-${kind}`}
            hidden={selectedIndex !== index}
          >
            <p className={`review-action-status status-${status}`}>
              {actionStatusMessages[status]}
            </p>
            {kind === 'addComment' ? (
              <TimecodedTextForm
                idPrefix="review-comment"
                legend="タイムコードコメントを追記"
                submitLabel="コメントを記録"
                textLabel="コメント"
                actionStatus={status}
                actorRoles={action.actorRoles}
                pending={pending}
                onSubmit={(outputTime, text, actorRole) => {
                  if (actorRole !== null) {
                    addComment(actorRole, outputTime, text)
                  }
                }}
              />
            ) : kind === 'requestChanges' ? (
              <TimecodedTextForm
                idPrefix="review-changes"
                legend="変更依頼を追記"
                submitLabel="変更を依頼"
                textLabel="変更内容"
                actionStatus={status}
                pending={pending}
                onSubmit={(outputTime, text) => requestChanges(outputTime, text)}
              />
            ) : (
              <ApprovalForm
                actionStatus={status}
                actorRoles={action.actorRoles.filter(isApprovalActorRole)}
                pending={pending}
                onSubmit={recordApproval}
              />
            )}
          </div>
        )
      })}
    </section>
  )
}

function TimecodedTextForm({
  idPrefix,
  legend,
  submitLabel,
  textLabel,
  actionStatus,
  actorRoles,
  pending,
  onSubmit,
}: Readonly<{
  idPrefix: string
  legend: string
  submitLabel: string
  textLabel: string
  actionStatus: ReviewActionStatus
  actorRoles?: readonly ReviewAuthorActorRole[]
  pending: boolean
  onSubmit: (
    outputTime: ReviewRationalTime,
    text: string,
    actorRole: ReviewAuthorActorRole | null,
  ) => void
}>) {
  const [value, setValue] = useState('0')
  const [rate, setRate] = useState('1')
  const [text, setText] = useState('')
  const [selectedActorRole, setSelectedActorRole] =
    useState<ReviewAuthorActorRole | ''>('')
  const outputTime = { value, rate }
  const actorRole = actorRoles?.includes(
    selectedActorRole as ReviewAuthorActorRole,
  )
    ? selectedActorRole as ReviewAuthorActorRole
    : null
  const timeIsValid = isReviewOutputTime(outputTime)
  const textIsValid = isReviewTextInput(text)
  const actorRoleIsValid = actorRoles === undefined || actorRole !== null
  const controlsDisabled = actionStatus !== 'authorized' || pending
  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    if (!controlsDisabled && actorRoleIsValid && timeIsValid && textIsValid) {
      onSubmit(outputTime, text, actorRole)
    }
  }

  return (
    <form className="review-action-form" onSubmit={submit}>
      <fieldset disabled={controlsDisabled}>
        <legend>{legend}</legend>
        {actorRoles !== undefined && (
          <ActorRoleSelect
            id={`${idPrefix}-actor-role`}
            actorRoles={actorRoles}
            value={actorRole}
            onChange={setSelectedActorRole}
          />
        )}
        <div className="rational-time-fields">
          <div className="field-group">
            <label htmlFor={`${idPrefix}-value`}>出力時刻 value</label>
            <input
              id={`${idPrefix}-value`}
              type="text"
              inputMode="numeric"
              pattern="(?:0|[1-9][0-9]{0,18})"
              value={value}
              aria-describedby={`${idPrefix}-time-help`}
              onChange={(event) => setValue(event.currentTarget.value)}
            />
          </div>
          <div className="field-group">
            <label htmlFor={`${idPrefix}-rate`}>出力時刻 rate</label>
            <input
              id={`${idPrefix}-rate`}
              type="text"
              inputMode="numeric"
              pattern="[1-9][0-9]{0,18}"
              value={rate}
              aria-describedby={`${idPrefix}-time-help`}
              onChange={(event) => setRate(event.currentTarget.value)}
            />
          </div>
        </div>
        <p id={`${idPrefix}-time-help`} className="field-help">
          0 以上の value と正の rate を、約分済み 64-bit 整数で入力します。小数へ丸めません。
        </p>
        <div className="field-group">
          <label htmlFor={`${idPrefix}-text`}>{textLabel}</label>
          <textarea
            id={`${idPrefix}-text`}
            rows={4}
            maxLength={16_384}
            value={text}
            aria-describedby={`${idPrefix}-text-help`}
            onChange={(event) => setText(event.currentTarget.value)}
          />
          <p id={`${idPrefix}-text-help`} className="field-help">
            前後に空白を付けず、UTF-8 で 16 KiB 以内にします。
          </p>
        </div>
        <button
          className="primary-button"
          type="submit"
          disabled={
            controlsDisabled || !actorRoleIsValid || !timeIsValid || !textIsValid
          }
        >
          {submitLabel}
        </button>
      </fieldset>
    </form>
  )
}

function ApprovalForm({
  actionStatus,
  actorRoles,
  pending,
  onSubmit,
}: Readonly<{
  actionStatus: ReviewActionStatus
  actorRoles: readonly ReviewApprovalActorRole[]
  pending: boolean
  onSubmit: (actorRole: ReviewApprovalActorRole, reason: string) => void
}>) {
  const [reason, setReason] = useState('')
  const [selectedActorRole, setSelectedActorRole] =
    useState<ReviewApprovalActorRole | ''>('')
  const actorRole = actorRoles.includes(
    selectedActorRole as ReviewApprovalActorRole,
  )
    ? selectedActorRole as ReviewApprovalActorRole
    : null
  const reasonIsValid = isReviewTextInput(reason)
  const controlsDisabled = actionStatus !== 'authorized' || pending
  return (
    <form
      className="review-action-form"
      onSubmit={(event) => {
        event.preventDefault()
        if (!controlsDisabled && reasonIsValid && actorRole !== null) {
          onSubmit(actorRole, reason)
        }
      }}
    >
      <fieldset disabled={controlsDisabled}>
        <legend>この構成版を承認</legend>
        <ActorRoleSelect
          id="review-approval-actor-role"
          actorRoles={actorRoles}
          value={actorRole}
          onChange={setSelectedActorRole}
        />
        <div className="field-group">
          <label htmlFor="review-approval-reason">承認理由</label>
          <textarea
            id="review-approval-reason"
            rows={4}
            maxLength={16_384}
            value={reason}
            aria-describedby="review-approval-help"
            onChange={(event) => setReason(event.currentTarget.value)}
          />
          <p id="review-approval-help" className="field-help">
            承認は上記の構成版 ID と完全 digest に固定され、同じ役割での二重承認は競合として拒否されます。
          </p>
        </div>
        <button
          className="primary-button"
          type="submit"
          disabled={controlsDisabled || actorRole === null || !reasonIsValid}
        >
          承認を記録
        </button>
      </fieldset>
    </form>
  )
}

function ActorRoleSelect<TRole extends ReviewAuthorActorRole>({
  id,
  actorRoles,
  value,
  onChange,
}: Readonly<{
  id: string
  actorRoles: readonly TRole[]
  value: TRole | null
  onChange: (role: TRole) => void
}>) {
  return (
    <div className="field-group">
      <label htmlFor={id}>この操作で使用する役割</label>
      <select
        id={id}
        value={value ?? ''}
        onChange={(event) => {
          const candidate = event.currentTarget.value as TRole
          if (actorRoles.includes(candidate)) {
            onChange(candidate)
          }
        }}
      >
        <option value="" disabled>役割を選択</option>
        {actorRoles.map((role) => (
          <option key={role} value={role}>{roleLabels[role]}</option>
        ))}
      </select>
      <p className="field-help">
        選択した役割はネイティブ側で現在の割り当てと再照合されます。
      </p>
    </div>
  )
}

function isApprovalActorRole(
  role: ReviewAuthorActorRole,
): role is ReviewApprovalActorRole {
  return role === 'contentApprover' ||
    role === 'brandApprover' ||
    role === 'rightsApprover'
}

function CommentThread({ comments }: Readonly<{ comments: ReviewSnapshot['comments'] }>) {
  return (
    <section className="review-card" aria-labelledby="review-comments-title">
      <div className="review-card-heading">
        <div>
          <p className="section-kicker">Append-only evidence</p>
          <h3 id="review-comments-title">タイムコードコメント</h3>
        </div>
        <span>{comments.length} 件</span>
      </div>
      {comments.length === 0 ? (
        <p className="review-card-empty">この版のコメントはありません。</p>
      ) : (
        <ol className="review-comment-list">
          {[...comments].reverse().map((comment) => (
            <li key={comment.id}>
              <div className="review-comment-meta">
                <strong>{roleLabels[comment.actorRole]}</strong>
                <span>出力 {formatRational(comment.outputTime)}</span>
                <time dateTime={comment.createdAt}>{formatUtc(comment.createdAt)}</time>
              </div>
              <p>{comment.text}</p>
              <dl>
                <div>
                  <dt>SourceSpan ID</dt>
                  <dd><code>{comment.sourceSpanId}</code></dd>
                </div>
              </dl>
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}

function ApprovalTable({ approvals }: Readonly<{ approvals: readonly ReviewApproval[] }>) {
  return (
    <section className="review-card" aria-labelledby="review-approvals-title">
      <div className="review-card-heading">
        <div>
          <p className="section-kicker">Digest-pinned evidence</p>
          <h3 id="review-approvals-title">承認根拠</h3>
        </div>
        <span>{approvals.length} 件</span>
      </div>
      {approvals.length === 0 ? (
        <p className="review-card-empty">表示できる承認根拠はありません。</p>
      ) : (
        <div className="review-table-scroll" tabIndex={0} aria-label="承認根拠（横スクロール可能）">
          <table className="review-approval-table">
            <caption className="visually-hidden">現在版と祖先版の承認根拠</caption>
            <thead>
              <tr>
                <th scope="col">有効性</th>
                <th scope="col">役割</th>
                <th scope="col">対象版</th>
                <th scope="col">対象 digest</th>
                <th scope="col">承認日時</th>
                <th scope="col">理由</th>
              </tr>
            </thead>
            <tbody>
              {approvals.map((approval) => (
                <tr key={approval.id} data-approval-validity={approval.validity}>
                  <td>
                    <span className={`approval-validity ${approval.validity}`}>
                      {approval.validity === 'valid' ? '有効' : '失効'}
                    </span>
                  </td>
                  <td>{roleLabels[approval.actorRole]}</td>
                  <td><code>{approval.versionId}</code></td>
                  <td><code>{approval.versionDigest}</code></td>
                  <td><time dateTime={approval.approvedAt}>{formatUtc(approval.approvedAt)}</time></td>
                  <td>{approval.reason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {approvals.some((approval) => approval.validity === 'stale') && (
        <p className="stale-approval-note" role="note">
          失効した承認は別の構成版に固定された履歴です。現行版の承認として再利用しません。
        </p>
      )}
    </section>
  )
}

function DirectParentDiffView({
  diff,
  parentVersionId,
}: Readonly<{
  diff: ReviewSnapshot['directParentDiff']
  parentVersionId: string | null
}>) {
  return (
    <section className="review-card" aria-labelledby="review-diff-title">
      <p className="section-kicker">Direct-parent comparison</p>
      <h3 id="review-diff-title">直前版との差分</h3>
      {diff === null ? (
        <p className="review-card-empty">
          {parentVersionId === null
            ? 'この構成版には直接の親版がありません。'
            : '直接の親版との差分を読み込めませんでした。'}
        </p>
      ) : (
        <details className="review-diff-details">
          <summary>直接の親版との差分 {diff.changes.length} 件を表示</summary>
          <dl className="diff-version-pair">
            <div><dt>親版</dt><dd><code>{diff.fromVersionId}</code></dd></div>
            <div><dt>対象版</dt><dd><code>{diff.toVersionId}</code></dd></div>
          </dl>
          {diff.changes.length === 0 ? (
            <p>安定内容に差分はありません。</p>
          ) : (
            <ol className="review-change-list">
              {diff.changes.map((change) => (
                <li key={change.elementId}>
                  <strong>{changeKindLabel(change.kind)}</strong>
                  <code>{change.elementId}</code>
                  <span>{formatChangeIndices(change)}</span>
                </li>
              ))}
            </ol>
          )}
        </details>
      )}
    </section>
  )
}

function changeKindLabel(kind: ReviewCompositionChange['kind']): string {
  switch (kind) {
    case 'added': return '追加'
    case 'removed': return '削除'
    case 'moved': return '移動'
    case 'modified': return '変更'
  }
}

function formatChangeIndices(change: ReviewCompositionChange): string {
  const previous = change.previousIndex === null ? '—' : String(change.previousIndex)
  const current = change.currentIndex === null ? '—' : String(change.currentIndex)
  return `位置 ${previous} → ${current}`
}

function formatRational(value: ReviewRationalTime): string {
  return `${value.value}/${value.rate}`
}

function formatUtc(value: string): string {
  const date = new Date(value)
  const year = date.getUTCFullYear().toString().padStart(4, '0')
  const month = (date.getUTCMonth() + 1).toString().padStart(2, '0')
  const day = date.getUTCDate().toString().padStart(2, '0')
  const hour = date.getUTCHours().toString().padStart(2, '0')
  const minute = date.getUTCMinutes().toString().padStart(2, '0')
  const second = date.getUTCSeconds().toString().padStart(2, '0')
  return `${year}/${month}/${day} ${hour}:${minute}:${second} UTC`
}
