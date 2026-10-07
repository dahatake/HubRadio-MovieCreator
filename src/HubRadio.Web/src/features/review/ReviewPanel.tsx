import {
  useEffect,
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
import { formatTimestamp } from '../results/humanReadable'

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

export interface ReviewPanelProps {
  /** Undefined preserves the standalone legacy forms; null/invalid IDs select nothing. */
  readonly context?: Readonly<{ versionId: string | null }>
  readonly active?: boolean
  /** Unsaved inputs or a pending mutation, never review approval or export eligibility. */
  readonly onDirtyChange?: (dirty: boolean) => void
}

export interface ReviewPanelViewProps extends ReviewStateContextValue, ReviewPanelProps {}

interface ReviewFormDraft {
  readonly value: string
  readonly rate: string
  readonly text: string
  readonly actorRole: ReviewAuthorActorRole | ''
}

type ReviewDrafts = Readonly<Record<ReviewActionKind, ReviewFormDraft>>

const emptyDraft: ReviewFormDraft = Object.freeze({
  value: '0', rate: '1', text: '', actorRole: '',
})
const emptyDrafts: ReviewDrafts = Object.freeze({
  addComment: emptyDraft,
  requestChanges: emptyDraft,
  recordApproval: emptyDraft,
})
const mutationMethods = {
  addComment: REVIEW_ADD_COMMENT_METHOD,
  requestChanges: REVIEW_REQUEST_CHANGES_METHOD,
  recordApproval: REVIEW_RECORD_APPROVAL_METHOD,
} as const
const retainedInputMessage =
  '未保存の入力・処理を保持しています。元の版に戻るか、処理後に入力を取り消してください。'

function sameDraft(left: ReviewFormDraft, right: ReviewFormDraft): boolean {
  return left.value === right.value && left.rate === right.rate &&
    left.text === right.text && left.actorRole === right.actorRole
}

export function ReviewPanel(props: ReviewPanelProps) {
  return <ReviewPanelView {...useReviewState()} {...props} />
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
  context,
  active = true,
  onDirtyChange,
}: ReviewPanelViewProps) {
  // Keep drafts above the target-dependent workspace so hiding an old target
  // cannot discard its inputs or display them as evidence for a new selection.
  const [drafts, setDrafts] = useState(emptyDrafts)
  const [savedDrafts, setSavedDrafts] = useState(emptyDrafts)
  const [draftTarget, setDraftTarget] = useState<Readonly<
    Pick<ReviewSnapshot, 'versionId' | 'versionDigest'>
  > | null>(null)
  const [submitted, setSubmitted] = useState<Readonly<{
    kind: ReviewActionKind
    draft: ReviewFormDraft
    snapshot: ReviewSnapshot
  }> | null>(null)
  const [inputMessage, setInputMessage] = useState<string | null>(null)
  const panelRef = useRef<HTMLElement | null>(null)
  const onDirtyChangeRef = useRef(onDirtyChange)
  const autoLoadVersionRef = useRef<string | null>(null)
  const workflow = context !== undefined
  const versionId = typeof context?.versionId === 'string' &&
    isCanonicalReviewEntityId(context.versionId) ? context.versionId : null
  const pending = state.pending !== null
  const hasDraftChanges = actionKinds.some((kind) => !sameDraft(drafts[kind], savedDrafts[kind]))
  const dirty = hasDraftChanges || submitted !== null ||
    (state.pending !== null && state.pending.method !== REVIEW_GET_SNAPSHOT_METHOD)
  const draftMatchesSnapshot = draftTarget === null ||
    (draftTarget.versionId === state.snapshot?.versionId &&
      draftTarget.versionDigest === state.snapshot.versionDigest)
  const draftTargetChanged = draftTarget !== null &&
    ((workflow && versionId !== draftTarget.versionId) ||
      state.versionIdInput !== draftTarget.versionId ||
      (state.snapshot !== null && !draftMatchesSnapshot))
  const targetSwitchBlocked =
    (workflow && state.versionIdInput !== (versionId ?? '') && (dirty || pending)) ||
    (dirty && draftTargetChanged)
  const matchesTarget = !workflow ||
    (versionId !== null && state.versionIdInput === versionId)
  const snapshot = !targetSwitchBlocked && matchesTarget &&
    (!workflow || state.snapshot?.versionId === versionId)
    ? state.snapshot : null
  const displayedDrafts = draftMatchesSnapshot ? drafts : emptyDrafts
  const canInteract = active && !pending && submitted === null && snapshot !== null
  const canLoad = active && !pending && submitted === null && !targetSwitchBlocked &&
    isCanonicalReviewEntityId(state.versionIdInput)

  useEffect(() => {
    onDirtyChangeRef.current = onDirtyChange
  }, [onDirtyChange])

  useEffect(() => {
    onDirtyChangeRef.current?.(dirty)
  }, [dirty])

  useEffect(() => () => onDirtyChangeRef.current?.(false), [])

  useEffect(() => {
    if (submitted === null || pending) return
    // A click, a read, or a previous success is not a saved draft. The real
    // provider supplies a new snapshot only for a correlated native response.
    if (
      state.error === null && state.lastCompletedMethod === mutationMethods[submitted.kind] &&
      state.snapshot !== null && state.snapshot !== submitted.snapshot &&
      state.snapshot.versionId === submitted.snapshot.versionId &&
      state.snapshot.versionDigest === submitted.snapshot.versionDigest
    ) {
      setSavedDrafts((previous) => ({ ...previous, [submitted.kind]: submitted.draft }))
    }
    setSubmitted(null)
  }, [pending, state.error, state.lastCompletedMethod, state.snapshot, submitted])

  useEffect(() => {
    if (!workflow) {
      autoLoadVersionRef.current = null
      return
    }
    if (!active || dirty || pending) return
    const input = versionId ?? ''
    if (state.versionIdInput !== input) {
      autoLoadVersionRef.current = null
      setDrafts(emptyDrafts)
      setSavedDrafts(emptyDrafts)
      setDraftTarget(null)
      setInputMessage(null)
      changeVersionId(input)
      return
    }
    if (versionId === null || autoLoadVersionRef.current === versionId) return

    // loadSnapshot closes over the provider's input, not the prop. Wait for
    // that input to match and for the parent provider to install its session.
    let cancelled = false
    queueMicrotask(() => {
      if (cancelled || autoLoadVersionRef.current === versionId) return
      autoLoadVersionRef.current = versionId
      loadSnapshot()
    })
    return () => { cancelled = true }
  }, [active, changeVersionId, dirty, loadSnapshot, pending, state.versionIdInput, versionId, workflow])

  useEffect(() => {
    if (active) return
    const panel = panelRef.current
    const focused = panel?.ownerDocument.activeElement
    if (focused instanceof HTMLElement && panel?.contains(focused)) focused.blur()
  }, [active])

  const reload = (): void => {
    if (!active || pending || submitted !== null || !matchesTarget || targetSwitchBlocked) return
    if (workflow) {
      if (versionId === null || state.versionIdInput !== versionId) return
      autoLoadVersionRef.current = versionId
      loadSnapshot()
    } else {
      refreshSnapshot()
    }
  }

  const updateDraft = (kind: ReviewActionKind, draft: ReviewFormDraft): void => {
    if (!canInteract || snapshot === null) return
    if (draftTarget === null || !draftMatchesSnapshot) {
      setDraftTarget({ versionId: snapshot.versionId, versionDigest: snapshot.versionDigest })
      setSavedDrafts(emptyDrafts)
      setDrafts({ ...emptyDrafts, [kind]: draft })
    } else {
      setDrafts((previous) => ({ ...previous, [kind]: draft }))
    }
  }

  const submitDraft = (kind: ReviewActionKind, send: () => void): void => {
    if (
      !canInteract || snapshot === null || (workflow && kind !== 'addComment') ||
      selectReviewAction(snapshot, kind).status !== 'authorized'
    ) return
    setSubmitted({ kind, draft: displayedDrafts[kind], snapshot })
    send()
  }

  const workflowStatus = versionId === null
    ? 'レビュー対象は未選択です。'
    : targetSwitchBlocked
      ? '元の版の入力・処理を保持しています。'
      : !matchesTarget
        ? '選択した版の履歴を待っています。'
        : state.error !== null
          ? '旧形式の履歴を確認できませんでした。'
          : pending
            ? '旧形式の履歴を確認しています。'
            : snapshot === null
              ? '旧形式の履歴は未取得です。'
              : state.lastCompletedMethod === REVIEW_ADD_COMMENT_METHOD
                ? '旧形式の履歴にコメントを記録しました。'
                : '旧形式の履歴を表示しています。'

  return (
    <section
      ref={panelRef}
      className="review-panel"
      aria-labelledby="review-title"
      hidden={!active}
      inert={!active}
      onFocusCapture={(event) => {
        if (!active) event.target.blur()
      }}
    >
      <div className="review-heading">
        <div>
          <p className="eyebrow">{workflow ? 'Legacy review history' : 'Version-bound review'}</p>
          <h2 id="review-title">{workflow ? '旧形式のレビュー履歴' : 'レビュー・コメント・承認'}</h2>
          {workflow ? (
            <p>
              自己レビューの完了記録は未接続です。コメントは任意で、書き出しの必須条件ではありません。
              この画面では、旧形式の履歴から自己レビュー完了・書き出し可否を判定しません。
            </p>
          ) : (
            <p>構成版と完全な内容 digest を固定し、追記専用のレビュー根拠を表示します。</p>
          )}
        </div>
        {snapshot !== null && (
          <span
            className={`review-target-badge ${
              snapshot.isCurrentTarget ? 'current' : 'stale'
            }`}
          >
            {snapshot.isCurrentTarget ? '現行版' : '非現行版'}
          </span>
        )}
      </div>

      {workflow ? (
        versionId !== null && (
          <button
            className="secondary-button"
            type="button"
            disabled={!active || pending || submitted !== null || !matchesTarget || targetSwitchBlocked}
            onClick={reload}
          >
            履歴を再読み込み
          </button>
        )
      ) : (
        <form
          className="review-load-form"
          onSubmit={(event) => {
            event.preventDefault()
            if (canLoad) loadSnapshot()
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
              disabled={!active || pending || submitted !== null}
              aria-describedby="review-version-help"
              onChange={(event) => {
                if (!active || pending || submitted !== null) return
                if (dirty) {
                  setInputMessage(retainedInputMessage)
                  return
                }
                setDrafts(emptyDrafts)
                setSavedDrafts(emptyDrafts)
                setDraftTarget(null)
                setInputMessage(null)
                changeVersionId(event.currentTarget.value)
              }}
            />
            <button type="submit" disabled={!canLoad}>
              レビューを開く
            </button>
            {state.snapshot !== null && (
              <button
                className="secondary-button"
                type="button"
                disabled={!active || pending || submitted !== null || targetSwitchBlocked}
                onClick={reload}
              >
                再読み込み
              </button>
            )}
          </div>
          <p id="review-version-help" className="field-help">
            小文字 UUID の構成版 ID を指定します。ユーザー ID や役割はネイティブ側で判定されます。
          </p>
        </form>
      )}

      {(targetSwitchBlocked || inputMessage !== null) && (
        <p className="review-input-warning" role="alert">
          {targetSwitchBlocked ? retainedInputMessage : inputMessage}
        </p>
      )}
      {hasDraftChanges && (
        <button
          className="quiet-button"
          type="button"
          disabled={!active || pending || submitted !== null}
          onClick={() => {
            if (!active || pending || submitted !== null) return
            setDrafts(savedDrafts)
            setInputMessage(null)
          }}
        >
          未保存の入力を取り消す
        </button>
      )}

      {state.error !== null && matchesTarget && !targetSwitchBlocked && (
        <div className="review-error" role="alert">
          <div>
            <strong>レビュー操作を完了できませんでした</strong>
            <p>{state.error.message}</p>
          </div>
          <div className="review-error-actions">
            {(state.error.code === 'review.conflict' ||
              state.error.code === 'review.stale_version') &&
              snapshot !== null && (
                <button
                  className="secondary-button"
                  type="button"
                  disabled={!active || pending || submitted !== null}
                  onClick={reload}
                >
                  現在の状態を再読み込み
                </button>
              )}
            <button
              className="quiet-button"
              type="button"
              disabled={!active}
              onClick={() => { if (active) clearError() }}
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
        {workflow ? workflowStatus : state.pending !== null
          ? 'ネイティブ側でレビュー根拠を確認しています。'
          : state.lastCompletedMethod === null
            ? '構成版を指定してレビューを読み込んでください。'
            : operationLabels[state.lastCompletedMethod]}
      </p>

      {snapshot === null ? (
        <div className="review-empty">
          <strong>{workflow && versionId !== null ? '旧形式の履歴は未取得です' : 'レビュー対象は未選択です'}</strong>
          {!workflow && <p>構成版 ID を読み込むまで、レビュー根拠や操作は表示されません。</p>}
        </div>
      ) : (
        <ReviewWorkspace
          snapshot={snapshot}
          pending={pending || submitted !== null}
          workflow={workflow}
          active={active}
          drafts={displayedDrafts}
          onDraftChange={updateDraft}
          addComment={(role, time, text) => submitDraft('addComment', () => addComment(role, time, text))}
          requestChanges={(time, text) => submitDraft('requestChanges', () => requestChanges(time, text))}
          recordApproval={(role, reason) => submitDraft('recordApproval', () => recordApproval(role, reason))}
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
  workflow,
  active,
  drafts,
  onDraftChange,
  addComment,
  requestChanges,
  recordApproval,
}: Readonly<{
  snapshot: ReviewSnapshot
  pending: boolean
  workflow: boolean
  active: boolean
  drafts: ReviewDrafts
  onDraftChange: (kind: ReviewActionKind, draft: ReviewFormDraft) => void
  addComment: ReviewStateContextValue['addComment']
  requestChanges: ReviewStateContextValue['requestChanges']
  recordApproval: ReviewStateContextValue['recordApproval']
}>) {
  return (
    <div className="review-workspace">
      <VersionSummary snapshot={snapshot} />
      {!workflow && !snapshot.isCurrentTarget && (
        <div className="review-stale-warning" role="alert">
          <strong>この版は現行の作業対象ではありません</strong>
          <p>
            コメント、変更依頼、承認は記録できません。表示される承認の有効性は選択した版を基準にしています。
          </p>
        </div>
      )}
      {!workflow && snapshot.reviewState === 'publicationBlocked' && (
        <div className="publication-blocked" role="alert">
          <strong>この構成版は公開停止です</strong>
          <p>{snapshot.reviewStateReason}</p>
          <p>公開停止はこの版の終端状態です。この画面は復旧操作を提供しません。</p>
        </div>
      )}
      {!workflow && snapshot.reviewState === 'changesRequested' && (
        <div className="changes-requested" role="status">
          <strong>変更依頼が記録されています</strong>
          <p>{snapshot.reviewStateReason}</p>
          <p>修正は新しい子版として作成し、改めてレビューしてください。</p>
        </div>
      )}
      <ReviewActions
        snapshot={snapshot}
        pending={pending}
        workflow={workflow}
        active={active}
        drafts={drafts}
        onDraftChange={onDraftChange}
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
          <dt>作成日時</dt>
          <dd><time dateTime={snapshot.createdAt}>{formatUtc(snapshot.createdAt)}</time></dd>
        </div>
        <div>
          <dt>直接の親版</dt>
          <dd>{snapshot.parentVersionId === null ? 'なし（最初の版）' : 'あり'}</dd>
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
  workflow,
  active,
  drafts,
  onDraftChange,
  addComment,
  requestChanges,
  recordApproval,
}: Readonly<{
  snapshot: ReviewSnapshot
  pending: boolean
  workflow: boolean
  active: boolean
  drafts: ReviewDrafts
  onDraftChange: (kind: ReviewActionKind, draft: ReviewFormDraft) => void
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
    if (!active) return
    const nextIndex = getReviewActionAfterKey(index, event.key)
    if (nextIndex === null) {
      return
    }
    event.preventDefault()
    setSelectedIndex(nextIndex)
    tabs.current[nextIndex]?.focus()
  }

  if (workflow) {
    const action = selectReviewAction(snapshot, 'addComment')
    return (
      <section className="review-card review-actions" aria-labelledby="review-actions-title">
        <h3 id="review-actions-title">任意コメント（旧形式）</h3>
        <p className={`review-action-status status-${action.status}`}>
          {actionStatusMessages[action.status]}
        </p>
        {snapshot.isCurrentTarget && action.status === 'authorized' && action.actorRoles.length > 0 && (
          <TimecodedTextForm
            idPrefix="review-comment"
            legend="旧形式の履歴にコメントを追記（任意）"
            submitLabel="コメントを記録"
            textLabel="コメント"
            actionStatus={action.status}
            actorRoles={action.actorRoles}
            pending={pending || !active}
            draft={drafts.addComment}
            onChange={(draft) => onDraftChange('addComment', draft)}
            onSubmit={(time, text, role) => {
              if (role !== null) addComment(role, time, text)
            }}
          />
        )}
      </section>
    )
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
            disabled={!active}
            onClick={() => { if (active) setSelectedIndex(index) }}
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
                pending={pending || !active}
                draft={drafts.addComment}
                onChange={(draft) => onDraftChange('addComment', draft)}
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
                pending={pending || !active}
                draft={drafts.requestChanges}
                onChange={(draft) => onDraftChange('requestChanges', draft)}
                onSubmit={(outputTime, text) => requestChanges(outputTime, text)}
              />
            ) : (
              <ApprovalForm
                actionStatus={status}
                actorRoles={action.actorRoles.filter(isApprovalActorRole)}
                pending={pending || !active}
                draft={drafts.recordApproval}
                onChange={(draft) => onDraftChange('recordApproval', draft)}
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
  draft,
  onChange,
  onSubmit,
}: Readonly<{
  idPrefix: string
  legend: string
  submitLabel: string
  textLabel: string
  actionStatus: ReviewActionStatus
  actorRoles?: readonly ReviewAuthorActorRole[]
  pending: boolean
  draft: ReviewFormDraft
  onChange: (draft: ReviewFormDraft) => void
  onSubmit: (
    outputTime: ReviewRationalTime,
    text: string,
    actorRole: ReviewAuthorActorRole | null,
  ) => void
}>) {
  const { value, rate, text, actorRole: selectedActorRole } = draft
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
  const update = (changes: Partial<ReviewFormDraft>): void => {
    if (!controlsDisabled) onChange({ ...draft, ...changes })
  }
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
            onChange={(role) => update({ actorRole: role })}
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
              onChange={(event) => update({ value: event.currentTarget.value })}
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
              onChange={(event) => update({ rate: event.currentTarget.value })}
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
            onChange={(event) => update({ text: event.currentTarget.value })}
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
  draft,
  onChange,
  onSubmit,
}: Readonly<{
  actionStatus: ReviewActionStatus
  actorRoles: readonly ReviewApprovalActorRole[]
  pending: boolean
  draft: ReviewFormDraft
  onChange: (draft: ReviewFormDraft) => void
  onSubmit: (actorRole: ReviewApprovalActorRole, reason: string) => void
}>) {
  const { text: reason, actorRole: selectedActorRole } = draft
  const actorRole = actorRoles.includes(
    selectedActorRole as ReviewApprovalActorRole,
  )
    ? selectedActorRole as ReviewApprovalActorRole
    : null
  const reasonIsValid = isReviewTextInput(reason)
  const controlsDisabled = actionStatus !== 'authorized' || pending
  const update = (changes: Partial<ReviewFormDraft>): void => {
    if (!controlsDisabled) onChange({ ...draft, ...changes })
  }
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
          onChange={(role) => update({ actorRole: role })}
        />
        <div className="field-group">
          <label htmlFor="review-approval-reason">承認理由</label>
          <textarea
            id="review-approval-reason"
            rows={4}
            maxLength={16_384}
            value={reason}
            aria-describedby="review-approval-help"
            onChange={(event) => update({ text: event.currentTarget.value })}
          />
          <p id="review-approval-help" className="field-help">
            承認はこの編集版に対して記録されます。同じ役割で二重に承認することはできません。
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
                <span>動画の {formatRational(comment.outputTime)} の位置</span>
                <time dateTime={comment.createdAt}>{formatUtc(comment.createdAt)}</time>
              </div>
              <p>{comment.text}</p>
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
          {diff.changes.length === 0 ? (
            <p>安定内容に差分はありません。</p>
          ) : (
            <ol className="review-change-list">
              {diff.changes.map((change) => (
                <li key={change.elementId}>
                  <strong>{changeKindLabel(change.kind)}</strong>
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
  return formatTimestamp(value.value, value.rate)
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
