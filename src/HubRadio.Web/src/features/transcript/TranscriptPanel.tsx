import {
  useCallback,
  useEffect,
  useReducer,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
} from 'react'

import { createHostBridgeClient } from '../../bridge'
import { isCanonicalBridgeId } from '../../bridge/protocol'
import { usePlayerSourceNavigation } from '../player'
import { useProjectState } from '../projects/ProjectStateProvider'
import {
  createTranscriptAdoptTermRequest,
  createTranscriptEditTokenRequest,
  createTranscriptReadRequest,
  createTranscriptRejectTermRequest,
  createTranscriptRemoveSpeakerRequest,
  createTranscriptSetSpeakerRequest,
  type TranscriptDependencyNode,
  type TranscriptPerformer,
  type TranscriptReadRequest,
  type TranscriptSnapshot,
  type TranscriptSpeakerDisplayFormat,
  type TranscriptTermCandidate,
  type TranscriptToken,
  type TranscriptUtterance,
} from './transcriptProtocol'
import {
  startTranscriptBridgeSession,
  type TranscriptBridgeClient,
  type TranscriptDispatch,
  type TranscriptBridgeSession,
} from './transcriptBridgeSession'
import {
  findTranscriptToken,
  initialTranscriptState,
  transcriptStateReducer,
  type TranscriptEditDraft,
  type TranscriptState,
  type TranscriptUiStatus,
} from './transcriptState'

const statusMessages: Readonly<Record<TranscriptUiStatus, string>> = {
  idle: '確定済み案件を開くと文字起こしを確認できます',
  loading: '正規の文字起こし snapshot を確認しています',
  available: '文字起こしを読み込みました',
  forbidden: 'この文字起こしを表示する権限がありません',
  unavailable: '利用できる文字起こし snapshot がありません',
  staleBase: '文字起こしが更新されました。最新版を読み込んでください',
  conflict: '別の変更と競合しました。最新版を読み込んでください',
  noChange: '内容は変更されませんでした',
  invalid: '文字起こしの要求または応答を検証できませんでした',
  bridgeUnavailable: 'ネイティブホストへ接続できません',
  sendFailed: 'ネイティブホストへ要求を送信できません',
}

const timingReasonLabels = Object.freeze({
  workerReported: '解析 worker が手動確認を要求',
  missingRawTiming: '元の単語時刻が未提供',
  nonPositiveRawDuration: '元の単語時間が正ではない',
  outsideUtterance: '単語時刻が発話範囲外',
  outsideAnalysisAudio: '単語時刻が解析音声範囲外',
  nonPositiveRoundedDuration: 'sample 丸め後の時間が正ではない',
  overlapsPreviousTimedToken: '直前の時刻付き単語と重複',
} as const)

const dependencyLabels: Readonly<Record<TranscriptDependencyNode, string>> = {
  termCandidates: '用語候補',
  editorialPhraseCandidates: '編集指示語句候補',
  repetitionCandidates: '反復候補',
  topicBoundaryCandidates: 'トピック境界候補',
  topicRangeBinding: 'トピック範囲の結合',
  contextDeviationCandidates: '文脈逸脱候補',
  speakerAssignmentBinding: '話者割当の結合',
  topicMetadata: 'トピック metadata',
  emphasisCandidates: '強調候補',
}

const navigationMessages = Object.freeze({
  requested: '元時刻への移動をプレイヤーへ要求しました。',
  unavailable: '同じ案件の代理動画を先に読み込んでください。',
  projectMismatch: 'プレイヤーには別の案件が読み込まれています。',
  busy: 'プレイヤーの移動確認が完了するまでお待ちください。',
  outOfRange: 'この元時刻に対応する代理フレームはありません。',
} as const)

/** transcript.read resolves the current revision by project; it has no revision selector. */
export interface TranscriptPanelContext {
  readonly projectId?: TranscriptReadRequest['params']['projectId'] | null
  /** Meaningful native registration identity, not a timer/progress revision. */
  readonly refreshKey?: string
}

export interface TranscriptPanelProps {
  readonly projectId?: string | null
  /** Undefined preserves the legacy selection; null/missing IDs select nothing. */
  readonly context?: TranscriptPanelContext | null
  readonly active?: boolean
  /** The shell must guard project changes while inputs or a mutation are pending. */
  readonly onDirtyChange?: (dirty: boolean) => void
  readonly createClient?: () => TranscriptBridgeClient
}

export function TranscriptPanel({
  projectId: explicitProjectId,
  context,
  active = true,
  onDirtyChange,
  createClient = createHostBridgeClient,
}: TranscriptPanelProps) {
  const { state: projectState } = useProjectState()
  const selectedProjectId =
    context !== undefined
      ? context?.projectId ?? null
      : explicitProjectId === undefined
        ? projectState.draft?.isFinalized === true
          ? projectState.draft.projectId
          : null
        : explicitProjectId
  return (
    <TranscriptPanelView
      projectId={selectedProjectId}
      context={context}
      active={active}
      onDirtyChange={onDirtyChange}
      createClient={createClient}
    />
  )
}

export interface TranscriptPanelViewProps extends TranscriptPanelProps {
  readonly projectId: string | null
}

interface TranscriptSpeakerDraft {
  readonly speakerId: string
  readonly performerId: string
  readonly surname: string
  readonly displayFormat: TranscriptSpeakerDisplayFormat
  readonly base: Readonly<Pick<TranscriptSnapshot,
    'revisionId' | 'revisionDigest' | 'speakerRevisionId' | 'parentRevisionId' | 'changeSource' | 'createdAt'
  > & { utteranceId: string; sourceSpanId: string }>
  readonly stale?: boolean
}

export function TranscriptPanelView({
  projectId: explicitProjectId,
  context,
  active = true,
  onDirtyChange,
  createClient = createHostBridgeClient,
}: TranscriptPanelViewProps) {
  const suppliedProjectId = context === undefined ? explicitProjectId : context?.projectId
  const refreshKey = context?.refreshKey
  const projectId =
    typeof suppliedProjectId === 'string' && isCanonicalBridgeId(suppliedProjectId)
      ? suppliedProjectId
      : null
  const [state, dispatch] = useReducer(transcriptStateReducer, {
    ...initialTranscriptState,
    projectId,
  })
  const [reason, setReason] = useState('')
  const [savedReason, setSavedReason] = useState('')
  const [submittedReason, setSubmittedReason] = useState<string | null>(null)
  const [speakerDraft, setSpeakerDraft] = useState<TranscriptSpeakerDraft | null>(null)
  const [inputMessage, setInputMessage] = useState<string | null>(null)
  const panelRef = useRef<HTMLElement | null>(null)
  const sessionRef = useRef<TranscriptBridgeSession | null>(null)
  const initialReadSessionRef = useRef<TranscriptBridgeSession | null>(null)
  const readRefreshKeyRef = useRef(refreshKey)
  const [readRefreshKey, setReadRefreshKey] = useState(refreshKey)
  const dirtyRef = useRef(false)
  const createClientRef = useRef(createClient)
  const onDirtyChangeRef = useRef(onDirtyChange)
  const targetRef = useRef({ projectId, active })
  const submittedRequestRef = useRef<Readonly<{
    request: Parameters<TranscriptBridgeSession['send']>[0]
    speakerDraft: TranscriptSpeakerDraft | null
  }> | null>(null)
  const stateRef = useRef<TranscriptState>(state)
  const requestPlayerSourceSeek = usePlayerSourceNavigation()
  stateRef.current = state
  createClientRef.current = createClient
  onDirtyChangeRef.current = onDirtyChange
  targetRef.current = { projectId, active }

  const pending = state.pending !== null
  const dirty =
    reason !== savedReason || state.edit !== null || speakerDraft !== null ||
    submittedReason !== null ||
    (state.pending !== null && state.pending.method !== 'transcript.read')
  dirtyRef.current = dirty
  const hasSelectedProject = projectId !== null && state.projectId === projectId
  const targetSwitchBlocked = state.projectId !== projectId && dirty
  // A retained draft is never evidence for a different (or missing) selection.
  const snapshot = hasSelectedProject ? state.snapshot : null
  const displayStatus = hasSelectedProject ? state.status : 'idle'
  const canInteract = active && hasSelectedProject && !pending
  const hasFreshSnapshot = snapshot !== null && (state.status === 'available' || state.status === 'noChange')
  const canMutate = canInteract && hasFreshSnapshot
  const speakerDraftStale = speakerDraft !== null &&
    (speakerDraft.stale === true || !hasSameSpeakerBase(speakerDraft, state.snapshot))

  useEffect(() => {
    onDirtyChangeRef.current?.(dirty)
  }, [dirty])

  useEffect(() => () => onDirtyChangeRef.current?.(false), [])

  useEffect(() => {
    if (
      speakerDraft === null || speakerDraft.stale === true ||
      hasSameSpeakerBase(speakerDraft, state.snapshot)
    ) return
    setSpeakerDraft({ ...speakerDraft, stale: true })
  }, [speakerDraft, state.snapshot])

  useEffect(() => {
    if (state.projectId === projectId || dirty) return
    setReason('')
    setSavedReason('')
    setInputMessage(null)
    dispatch({ type: 'projectChanged', projectId })
  }, [dirty, projectId, state.projectId])

  useEffect(() => {
    if (state.projectId === null) return
    let disposed = false
    // A recreated factory or a visibility change is not a new project/session.
    const session = startTranscriptBridgeSession(createClientRef.current, (action) => {
      if (disposed) return
      const submitted = submittedRequestRef.current
      if (submitted !== null) {
        const request = submitted.request
        if (
          action.type === 'responseReceived' &&
          action.response.requestId === request.requestId && action.response.method === request.method &&
          stateRef.current.projectId === request.params.projectId
        ) {
          submittedRequestRef.current = null
          const result = action.response.result
          if (
            (result.status === 'available' && result.snapshot.projectId === request.params.projectId) ||
            (result.status === 'noChange' && request.method !== 'transcript.read')
          ) {
            // A refresh reconciles transport uncertainty, not the submitted mutation.
            if (request.method !== 'transcript.read') setSavedReason(request.params.reason)
            if (request.method === 'transcript.setSpeaker') {
              setSpeakerDraft((current) => current === submitted.speakerDraft ? null : current)
            }
            setSubmittedReason(null)
          }
        } else if (
          action.type === 'requestInvalid' || action.type === 'bridgeUnavailable' ||
          (action.type === 'sendFailed' && action.requestId === request.requestId)
        ) {
          submittedRequestRef.current = null
        }
      }
      dispatch(action)
    })
    sessionRef.current = session
    initialReadSessionRef.current = null
    dispatch({ type: 'sessionStarted' })
    return () => {
      disposed = true
      if (sessionRef.current === session) {
        sessionRef.current = null
        submittedRequestRef.current = null
      }
      session.dispose()
    }
  }, [state.projectId])

  useEffect(() => {
    const session = sessionRef.current
    if (
      !active || projectId === null || state.projectId !== projectId ||
      session === null || dirty || state.pending !== null || submittedRequestRef.current !== null ||
      (initialReadSessionRef.current === session && readRefreshKeyRef.current === refreshKey)
    ) return
    let cancelled = false
    queueMicrotask(() => {
      if (
        cancelled || sessionRef.current !== session || !targetRef.current.active ||
        targetRef.current.projectId !== projectId || stateRef.current.projectId !== projectId ||
        dirtyRef.current || stateRef.current.pending !== null || submittedRequestRef.current !== null ||
        (initialReadSessionRef.current === session && readRefreshKeyRef.current === refreshKey) ||
        (refreshKey !== undefined && panelRef.current?.closest('[hidden], [inert], [aria-hidden="true"]') !== null)
      ) return
      initialReadSessionRef.current = session
      readRefreshKeyRef.current = refreshKey
      setReadRefreshKey(refreshKey)
      try {
        session.send(createTranscriptReadRequest(createRequestId(), projectId))
      } catch {
        dispatch({ type: 'requestInvalid' })
      }
    })
    return () => { cancelled = true }
  }, [active, projectId, state.projectId, refreshKey, dirty, state.pending])

  useEffect(() => {
    if (active) return
    const panel = panelRef.current
    const focused = panel?.ownerDocument.activeElement
    if (focused instanceof HTMLElement && panel?.contains(focused)) focused.blur()
  }, [active])

  const sendRead = useCallback(() => {
    const current = stateRef.current
    if (
      !active || projectId === null || current.projectId !== projectId ||
      current.pending !== null || submittedRequestRef.current !== null
    ) return
    try {
      const request = createTranscriptReadRequest(createRequestId(), projectId)
      const session = sessionRef.current
      initialReadSessionRef.current = session
      readRefreshKeyRef.current = refreshKey
      setReadRefreshKey(refreshKey)
      submittedRequestRef.current = { request, speakerDraft: null }
      send(session, request, dispatch)
      if (session === null) submittedRequestRef.current = null
    } catch {
      setInputMessage('案件 ID を文字起こし要求として検証できませんでした。')
    }
  }, [active, projectId, refreshKey])

  const reasonIsValid = isRequiredText(reason)

  const discardChanges = (): void => {
    if (!active || stateRef.current.pending !== null) return
    setReason(savedReason)
    setSubmittedReason(null)
    submittedRequestRef.current = null
    setSpeakerDraft(null)
    setInputMessage(null)
    dispatch({ type: 'editCancelled' })
  }

  const sendChange = (
    request: Exclude<Parameters<TranscriptBridgeSession['send']>[0], TranscriptReadRequest>,
  ): void => {
    if (!canMutate || submittedRequestRef.current !== null) return
    const session = sessionRef.current
    submittedRequestRef.current = { request, speakerDraft }
    setSubmittedReason(reason)
    send(session, request, dispatch)
    if (session === null) submittedRequestRef.current = null
  }

  const beginEdit = (token: TranscriptToken): void => {
    if (!canMutate) return
    setInputMessage(null)
    dispatch({ type: 'editStarted', token })
  }

  const cancelEdit = (): void => {
    if (!canInteract) return
    setInputMessage(null)
    dispatch({ type: 'editCancelled' })
  }

  const submitEdit = (): void => {
    const current = stateRef.current
    const edit = current.edit
    const currentSnapshot = current.snapshot
    if (!canMutate || edit === null || edit.stale === true || currentSnapshot === null) return
    if (!isRequiredText(edit.revisedText)) {
      setInputMessage('修正後の文字は前後空白のない非空文字列にしてください。')
      return
    }
    if (!reasonIsValid) {
      setInputMessage('変更理由を入力してください。')
      return
    }
    setInputMessage(null)
    try {
      sendChange(
        createTranscriptEditTokenRequest(createRequestId(), {
          projectId: currentSnapshot.projectId,
          revisionId: currentSnapshot.revisionId,
          digest: currentSnapshot.revisionDigest,
          tokenId: edit.tokenId,
          expectedText: edit.expectedText,
          revisedText: edit.revisedText,
          reason,
        }),
      )
    } catch {
      setInputMessage('文字修正の入力を安全な要求として検証できませんでした。')
    }
  }

  const startSpeakerEdit = (utterance: TranscriptUtterance): void => {
    if (!canMutate || snapshot === null || speakerDraft !== null) return
    const firstPerformer = snapshot.performers[0]
    if (firstPerformer === undefined) {
      setInputMessage('この案件には割当可能な出演者がいません。')
      return
    }
    setInputMessage(null)
    setSpeakerDraft({
      speakerId: utterance.speaker.speakerId,
      performerId: utterance.speaker.performerId ?? firstPerformer.id,
      surname:
        utterance.speaker.format === 'surname' && utterance.speaker.text !== null
          ? utterance.speaker.text
          : '',
      displayFormat: utterance.speaker.format ?? 'fullName',
      base: {
        revisionId: snapshot.revisionId,
        revisionDigest: snapshot.revisionDigest,
        speakerRevisionId: snapshot.speakerRevisionId,
        parentRevisionId: snapshot.parentRevisionId,
        changeSource: snapshot.changeSource,
        createdAt: snapshot.createdAt,
        utteranceId: utterance.id,
        sourceSpanId: utterance.sourceSpanId,
      },
    })
  }

  const submitSpeaker = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    const currentSnapshot = stateRef.current.snapshot
    if (
      !canMutate || speakerDraft === null || currentSnapshot === null ||
      speakerDraft.stale === true || !hasSameSpeakerBase(speakerDraft, currentSnapshot)
    ) return
    if (!isRequiredText(speakerDraft.surname) || !reasonIsValid) {
      setInputMessage('確認した姓と変更理由を入力してください。')
      return
    }
    setInputMessage(null)
    try {
      sendChange(
        createTranscriptSetSpeakerRequest(createRequestId(), {
          projectId: currentSnapshot.projectId,
          revisionId: currentSnapshot.revisionId,
          digest: currentSnapshot.revisionDigest,
          expectedSpeakerRevisionId: currentSnapshot.speakerRevisionId,
          speakerId: speakerDraft.speakerId,
          performerId: speakerDraft.performerId,
          surname: speakerDraft.surname,
          displayFormat: speakerDraft.displayFormat,
          reason,
        }),
      )
    } catch {
      setInputMessage('話者割当の入力を安全な要求として検証できませんでした。')
    }
  }

  const removeSpeaker = (speakerId: string): void => {
    const currentSnapshot = stateRef.current.snapshot
    if (!canMutate || currentSnapshot === null) return
    if (!reasonIsValid) {
      setInputMessage('変更理由を入力してください。')
      return
    }
    setInputMessage(null)
    try {
      sendChange(
        createTranscriptRemoveSpeakerRequest(createRequestId(), {
          projectId: currentSnapshot.projectId,
          revisionId: currentSnapshot.revisionId,
          digest: currentSnapshot.revisionDigest,
          expectedSpeakerRevisionId: currentSnapshot.speakerRevisionId,
          speakerId,
          reason,
        }),
      )
    } catch {
      setInputMessage('話者割当解除の入力を安全な要求として検証できませんでした。')
    }
  }

  const decideTerm = (
    candidate: TranscriptTermCandidate,
    decision: 'adopt' | 'reject',
  ): void => {
    const currentSnapshot = stateRef.current.snapshot
    if (!canMutate || currentSnapshot === null) return
    if (!reasonIsValid) {
      setInputMessage('変更理由を入力してください。')
      return
    }
    const parameters = {
      projectId: currentSnapshot.projectId,
      revisionId: currentSnapshot.revisionId,
      digest: currentSnapshot.revisionDigest,
      candidateId: candidate.candidateId,
      termEntryId: candidate.termEntryId,
      termEntryVersion: candidate.termEntryVersion,
      reason,
    }
    setInputMessage(null)
    try {
      const request =
        decision === 'adopt'
          ? createTranscriptAdoptTermRequest(createRequestId(), parameters)
          : createTranscriptRejectTermRequest(createRequestId(), parameters)
      sendChange(request)
    } catch {
      setInputMessage('用語候補の判断を安全な要求として検証できませんでした。')
    }
  }

  const navigateToToken = (token: TranscriptToken): void => {
    if (!canMutate || snapshot === null) return
    const outcome = requestPlayerSourceSeek({
      projectId: snapshot.projectId,
      sourceTime: token.effectiveSeekSourceTime,
    })
    dispatch({
      type: 'navigationReported',
      message: navigationMessages[outcome],
    })
  }

  return (
    <section
      ref={panelRef}
      className="transcript-panel"
      aria-labelledby="transcript-title"
      hidden={!active}
      inert={!active}
      style={{ minHeight: 0, overflow: 'auto' }}
    >
      <div className="transcript-heading">
        <div>
          <p className="eyebrow">Revision-bound transcript</p>
          <h2 id="transcript-title">文字起こし編集</h2>
        </div>
        <div className="transcript-heading-actions">
          <p
            className="transcript-status"
            role="status"
            data-transcript-status={displayStatus}
          >
            {statusMessages[displayStatus]}
          </p>
          <button
            type="button"
            className="quiet-button"
            disabled={!canInteract}
            onClick={sendRead}
          >
            最新版を読み込む
          </button>
          {dirty ? (
            <button
              type="button"
              className="quiet-button"
              disabled={!active || pending}
              onClick={discardChanges}
            >
              未保存の入力を取り消す
            </button>
          ) : null}
        </div>
      </div>

      {projectId === null ? (
        <div className="transcript-empty">
          <p>確定済み案件を案件一覧から開いてください。</p>
        </div>
      ) : null}

      {targetSwitchBlocked ? (
        <p className="transcript-input-error" role="alert">
          未保存の入力または保存中の変更を保持しています。元の案件に戻り、保存または取消してから切り替えてください。
        </p>
      ) : null}

      {hasSelectedProject && dirty && initialReadSessionRef.current !== null && readRefreshKey !== refreshKey ? (
        <p className="transcript-registration-notice" role={active ? 'status' : undefined}>
          文字起こしデータが更新されました。未保存の入力を保持しています。保存または取消後、表示中に最新版を読み込みます。
        </p>
      ) : null}

      {!hasSelectedProject || (snapshot === null && !dirty) ? null : (
        <div
          className={snapshot === null ? 'transcript-drafts' : 'transcript-workspace'}
          data-transcript-stale={snapshot !== null && !hasFreshSnapshot ? true : undefined}
        >
          <label className="transcript-reason">
            <span>変更理由</span>
            <input
              type="text"
              value={reason}
              disabled={!canInteract}
              readOnly={!canMutate}
              onChange={(event) => { if (canMutate) setReason(event.currentTarget.value) }}
              aria-describedby="transcript-reason-help"
            />
          </label>
          <p id="transcript-reason-help" className="field-help">
            文字修正、話者割当、用語候補の判断に記録する理由です。
          </p>
          {snapshot !== null && !hasFreshSnapshot ? (
            <p className="transcript-input-error" role="status">
              前回取得した文字起こしは未確認です。下書きは保持しています。最新版の読み込みが成功するまで変更は送信できません。
            </p>
          ) : null}

          {inputMessage === null ? null : (
            <p className="transcript-input-error" role="alert">
              {inputMessage}
            </p>
          )}
          {state.edit?.stale === true ? (
            <div className="transcript-input-error" role="alert">
              <p>文字修正の基準が更新されました。本文をコピーし、文字修正を取消してから、最新版の単語を選び直して貼り付けてください。変更理由と話者の入力は保持されます。</p>
              <p>{state.edit.revisedText}</p>
            </div>
          ) : null}
          {speakerDraftStale ? (
            <p className="transcript-input-error" role="alert">
              話者設定の基準が更新されました。姓などの入力を控え、話者設定だけを取消してから、最新版の話者と出演者を選び直してください。
            </p>
          ) : null}
          {state.edit !== null && (snapshot === null || findTranscriptToken(snapshot, state.edit.tokenId) === null) ? (
            <div className="transcript-preserved-draft">
              <TokenEditor
                label="保持した文字修正"
                edit={state.edit}
                active={false}
                disabled={!canInteract}
                readOnly
                onEditChange={(value) => dispatch({ type: 'editChanged', value })}
                onSubmitEdit={submitEdit}
                onCancelEdit={cancelEdit}
              />
            </div>
          ) : null}
          {snapshot === null || state.navigationMessage === null ? null : (
            <p className="transcript-navigation-status" role="status">
              {state.navigationMessage}
            </p>
          )}

          {snapshot === null ? null : (
            <p className="transcript-confidence-note">
              低確度を判定する数値閾値は未承認です。認識確度の正規値または未提供をそのまま表示します。
            </p>
          )}

          {snapshot === null ? null : <div className="transcript-evidence-summary">
            <span>{!hasFreshSnapshot
              ? '前回取得した文字起こし（未確認）'
              : context === undefined ? `版 ${snapshot.revisionId}` : '現在の文字起こし'}</span>
            <span>{snapshot.changeSource === 'human' ? '人による修正' : '解析結果'}</span>
            <span>{snapshot.createdAt}</span>
          </div>}

          {snapshot === null ? null : <div className="utterance-list">
            {snapshot.utterances.map((utterance) => (
              <article className="utterance-card" key={utterance.id}>
                <header className="utterance-header">
                  <div>
                    <h3>
                      {utterance.speaker.isVisible
                        ? utterance.speaker.text
                        : '話者名は非表示'}
                    </h3>
                    <p>
                      発話範囲 {formatRange(utterance.range)}・認識確度{' '}
                      {formatConfidence(utterance.recognitionConfidence)}
                    </p>
                  </div>
                  <div className="utterance-actions">
                    <button
                      type="button"
                      disabled={!canMutate || speakerDraft !== null || snapshot.performers.length === 0}
                      onClick={() => startSpeakerEdit(utterance)}
                    >
                      話者を設定
                    </button>
                    <button
                      type="button"
                      disabled={!canMutate || !utterance.speaker.isAssigned}
                      onClick={() => removeSpeaker(utterance.speaker.speakerId)}
                    >
                      割当を解除
                    </button>
                  </div>
                </header>

                {utterance.speaker.titleOrAffiliationConfirmationRequired ? (
                  <p className="transcript-review-flag">
                    要確認: 出演者の肩書・所属が未確認です。
                  </p>
                ) : null}
                {utterance.manualTimingAdjustmentRequired ? (
                  <TimingReview reasons={utterance.timingReviewReasons} />
                ) : null}

                <div className="sentence-list">
                  {utterance.sentences.map((sentence) => (
                    <div className="sentence-card" key={sentence.id}>
                      <p className="sentence-label">文: {sentence.text}</p>
                      <div className="token-list" aria-label="単語一覧">
                        {sentence.tokens.map((token) => (
                          <TokenControl
                            key={token.id}
                            token={token}
                            state={state}
                            active={active}
                            disabled={!canMutate}
                            draftDisabled={!canInteract}
                            onBeginEdit={beginEdit}
                            onEditChange={(value) =>
                              dispatch({ type: 'editChanged', value })
                            }
                            onSubmitEdit={submitEdit}
                            onCancelEdit={cancelEdit}
                            onNavigate={navigateToToken}
                          />
                        ))}
                      </div>
                    </div>
                  ))}
                </div>
              </article>
            ))}
          </div>}

          {speakerDraft === null ? null : (
            <SpeakerEditor
              draft={speakerDraft}
              performers={snapshot?.performers ?? []}
              disabled={!canInteract}
              readOnly={!canMutate || speakerDraftStale}
              onChange={setSpeakerDraft}
              onSubmit={submitSpeaker}
              onCancel={() => { if (canInteract) setSpeakerDraft(null) }}
            />
          )}

          {snapshot === null ? null : <section className="term-candidate-section" aria-labelledby="term-candidates-title">
            <h3 id="term-candidates-title">用語辞書の修正候補</h3>
            {snapshot.termCandidates.length === 0 ? (
              <p>未判断の用語候補はありません。</p>
            ) : (
              <ul>
                {snapshot.termCandidates.map((candidate) => (
                  <li key={candidate.candidateId}>
                    <div>
                      <strong>{candidate.observedText}</strong>
                      <span aria-hidden="true"> → </span>
                      <strong>{candidate.proposedText}</strong>
                      <p>
                        辞書読み {candidate.dictionaryReading}・確度{' '}
                        {formatConfidence(candidate.confidence)}
                      </p>
                    </div>
                    <div className="candidate-actions">
                      <button
                        type="button"
                        disabled={!canMutate}
                        onClick={() => decideTerm(candidate, 'adopt')}
                      >
                        採用
                      </button>
                      <button
                        type="button"
                        disabled={!canMutate}
                        onClick={() => decideTerm(candidate, 'reject')}
                      >
                        却下
                      </button>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>}

          {snapshot === null ? null : <section className="term-decision-section" aria-labelledby="term-decisions-title">
            <h3 id="term-decisions-title">用語候補の判断履歴</h3>
            {snapshot.termDecisions.length === 0 ? (
              <p>この snapshot に判断履歴はありません。</p>
            ) : (
              <ul>
                {snapshot.termDecisions.map((decision) => (
                  <li key={decision.candidateId}>
                    <strong>{decision.decision === 'adopted' ? '採用' : '却下'}</strong>
                    <span>{decision.reason}</span>
                    <time dateTime={decision.decidedAt}>{decision.decidedAt}</time>
                  </li>
                ))}
              </ul>
            )}
          </section>}

          {snapshot === null ? null : <DiffView state={state} />}
          {snapshot === null ? null : <InvalidationView state={state} showRevisionDetails={context === undefined} />}
        </div>
      )}
    </section>
  )
}

function TokenControl({
  token,
  state,
  active,
  disabled,
  draftDisabled,
  onBeginEdit,
  onEditChange,
  onSubmitEdit,
  onCancelEdit,
  onNavigate,
}: Readonly<{
  token: TranscriptToken
  state: TranscriptState
  active: boolean
  disabled: boolean
  draftDisabled: boolean
  onBeginEdit: (token: TranscriptToken) => void
  onEditChange: (value: string) => void
  onSubmitEdit: () => void
  onCancelEdit: () => void
  onNavigate: (token: TranscriptToken) => void
}>) {
  const edit = state.edit?.tokenId === token.id ? state.edit : null
  if (edit !== null) {
    return (
      <TokenEditor
        label={`${token.text} の修正後文字`}
        edit={edit}
        active={active}
        disabled={draftDisabled}
        readOnly={disabled || edit.stale === true}
        onEditChange={onEditChange}
        onSubmitEdit={onSubmitEdit}
        onCancelEdit={onCancelEdit}
      />
    )
  }

  return (
    <div className="token-control">
      <button
        type="button"
        className="token-text-button"
        disabled={disabled || state.edit?.stale === true}
        onClick={() => onBeginEdit(token)}
        aria-label={`${token.text} を編集`}
      >
        {token.text}
      </button>
      <button
        type="button"
        className="token-time-button"
        disabled={disabled}
        onClick={() => onNavigate(token)}
        aria-label={`${token.text} の元時刻へ移動`}
      >
        {formatRange(token.range)}
      </button>
      <span>認識確度 {formatConfidence(token.recognitionConfidence)}</span>
      {token.manualTimingAdjustmentRequired ? (
        <TimingReview
          reasons={token.timingReviewReasons}
          usesUtteranceRange={token.seekUsesUtteranceRange}
        />
      ) : null}
    </div>
  )
}

function TokenEditor({
  label, edit, active, disabled, readOnly, onEditChange, onSubmitEdit, onCancelEdit,
}: Readonly<{
  label: string
  edit: TranscriptEditDraft
  active: boolean
  disabled: boolean
  readOnly: boolean
  onEditChange: (value: string) => void
  onSubmitEdit: () => void
  onCancelEdit: () => void
}>) {
  return (
    <div className="token-control token-editing">
      <label>
        <span className="visually-hidden">{label}</span>
        <input
          type="text"
          autoFocus={active}
          value={edit.revisedText}
          disabled={disabled}
          readOnly={readOnly}
          onChange={(event) => { if (!disabled && !readOnly) onEditChange(event.currentTarget.value) }}
          onKeyDown={(event) => { if (!disabled) onEditKeyDown(event, onSubmitEdit, onCancelEdit) }}
        />
      </label>
      <span className="token-key-help">{readOnly ? 'コピーできます / Escape: 取消' : 'Enter: 保存 / Escape: 取消'}</span>
      <div className="token-edit-actions">
        <button type="button" disabled={disabled || readOnly} onClick={onSubmitEdit}>保存</button>
        <button type="button" disabled={disabled} onClick={onCancelEdit}>取消</button>
      </div>
    </div>
  )
}

function SpeakerEditor({
  draft,
  performers,
  disabled,
  readOnly,
  onChange,
  onSubmit,
  onCancel,
}: Readonly<{
  draft: TranscriptSpeakerDraft
  performers: readonly TranscriptPerformer[]
  disabled: boolean
  readOnly: boolean
  onChange: (draft: TranscriptSpeakerDraft) => void
  onSubmit: (event: FormEvent<HTMLFormElement>) => void
  onCancel: () => void
}>) {
  return (
    <form className="speaker-editor" onSubmit={onSubmit}>
      <h3>話者表示を設定</h3>
      <label>
        <span>出演者</span>
        <select
          value={draft.performerId}
          disabled={disabled || readOnly}
          onChange={(event) => { if (!disabled && !readOnly) onChange({ ...draft, performerId: event.currentTarget.value }) }}
        >
          {performers.some((performer) => performer.id === draft.performerId) ? null : (
            <option value={draft.performerId}>下書きの選択（要確認）</option>
          )}
          {performers.map((performer) => (
            <option key={performer.id} value={performer.id}>
              {performer.displayName}（{performer.role}）
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>確認した姓</span>
        <input
          type="text"
          value={draft.surname}
          disabled={disabled}
          readOnly={readOnly}
          onChange={(event) => { if (!disabled && !readOnly) onChange({ ...draft, surname: event.currentTarget.value }) }}
        />
      </label>
      <label>
        <span>表示形式</span>
        <select
          className="speaker-format-select"
          value={draft.displayFormat}
          disabled={disabled || readOnly}
          onChange={(event) => {
            if (!disabled && !readOnly) onChange({
              ...draft,
              displayFormat: event.currentTarget.value as TranscriptSpeakerDisplayFormat,
            })
          }}
        >
          <option value="fullName">フルネーム</option>
          <option value="surname">姓のみ</option>
          <option value="role">役割名</option>
          <option value="hidden">非表示</option>
        </select>
      </label>
      <div className="speaker-editor-actions">
        <button type="submit" disabled={disabled || readOnly}>設定</button>
        <button type="button" disabled={disabled} onClick={onCancel}>取消</button>
      </div>
    </form>
  )
}

function hasSameSpeakerBase(draft: TranscriptSpeakerDraft, snapshot: TranscriptSnapshot | null): boolean {
  const base = draft.base
  return snapshot !== null &&
    base.revisionId === snapshot.revisionId && base.revisionDigest === snapshot.revisionDigest &&
    base.speakerRevisionId === snapshot.speakerRevisionId && base.parentRevisionId === snapshot.parentRevisionId &&
    base.changeSource === snapshot.changeSource && base.createdAt === snapshot.createdAt &&
    snapshot.utterances.some((utterance) =>
      utterance.id === base.utteranceId && utterance.sourceSpanId === base.sourceSpanId &&
      utterance.speaker.speakerId === draft.speakerId,
    ) && snapshot.performers.some((performer) => performer.id === draft.performerId)
}

function TimingReview({
  reasons,
  usesUtteranceRange = false,
}: Readonly<{
  reasons: TranscriptToken['timingReviewReasons']
  usesUtteranceRange?: boolean
}>) {
  return (
    <div className="transcript-review-flag">
      <strong>要確認: 手動の時刻調整</strong>
      {usesUtteranceRange ? <p>移動には発話の開始時刻を使用します。</p> : null}
      {reasons.length === 0 ? null : (
        <ul>
          {reasons.map((reason) => (
            <li key={reason}>{timingReasonLabels[reason]}</li>
          ))}
        </ul>
      )}
    </div>
  )
}

function DiffView({ state }: Readonly<{ state: TranscriptState }>) {
  const diff = state.snapshot?.diff ?? []
  return (
    <section className="transcript-diff" aria-labelledby="transcript-diff-title">
      <h3 id="transcript-diff-title">直前の文字差分</h3>
      {diff.length === 0 ? (
        <p>この snapshot に文字差分はありません。</p>
      ) : (
        <ul>
          {diff.map((change) => (
            <li key={`${change.kind}-${change.tokenId ?? change.sentenceId ?? change.utteranceId}`}>
              <span>{change.kind === 'utterance' ? '発話' : change.kind === 'sentence' ? '文' : '単語'}</span>
              <del>{change.previousText}</del>
              <ins>{change.currentText}</ins>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function InvalidationView({ state, showRevisionDetails }: Readonly<{
  state: TranscriptState
  showRevisionDetails: boolean
}>) {
  const invalidation = state.snapshot?.invalidation
  const staleRange = state.snapshot?.staleRange
  if (invalidation === undefined || staleRange === undefined) return null
  return (
    <section className="transcript-invalidation" aria-labelledby="transcript-invalidation-title">
      <h3 id="transcript-invalidation-title">再解析・再結合の範囲</h3>
      {staleRange === null ? (
        <p>この snapshot に失効を生じさせた文字修正はありません。</p>
      ) : showRevisionDetails ? (
        <dl className="transcript-stale-evidence">
          <div>
            <dt>失効した基準版</dt>
            <dd>{staleRange.sourceRevisionId}</dd>
          </div>
          <div>
            <dt>失効した基準 digest</dt>
            <dd>{staleRange.sourceRevisionDigest}</dd>
          </div>
          <div>
            <dt>現在版</dt>
            <dd>{staleRange.revisionId}</dd>
          </div>
          <div>
            <dt>現在 digest</dt>
            <dd>{staleRange.revisionDigest}</dd>
          </div>
        </dl>
      ) : null}
      <DependencyList label="失効" nodes={invalidation.staleNodes} />
      <DependencyList label="自動再計算" nodes={invalidation.recomputationNodes} />
      <DependencyList label="手動再結合" nodes={invalidation.manualRebindingNodes} />
    </section>
  )
}

function DependencyList({
  label,
  nodes,
}: Readonly<{ label: string; nodes: readonly TranscriptDependencyNode[] }>) {
  return (
    <div>
      <h4>{label}</h4>
      {nodes.length === 0 ? (
        <p>なし</p>
      ) : (
        <ul>
          {nodes.map((node) => <li key={node}>{dependencyLabels[node]}</li>)}
        </ul>
      )}
    </div>
  )
}

function onEditKeyDown(
  event: KeyboardEvent<HTMLInputElement>,
  submit: () => void,
  cancel: () => void,
): void {
  if (event.key === 'Enter') {
    event.preventDefault()
    submit()
  } else if (event.key === 'Escape') {
    event.preventDefault()
    cancel()
  }
}

function send(
  session: TranscriptBridgeSession | null,
  request: Parameters<TranscriptBridgeSession['send']>[0],
  dispatch: TranscriptDispatch,
): void {
  if (session === null) {
    dispatch({ type: 'bridgeUnavailable' })
    return
  }
  session.send(request)
}

function formatConfidence(value: number | null): string {
  return value === null ? '未提供' : value.toString()
}

function formatRange(range: TranscriptToken['range']): string {
  if (range === null) return '単語時刻なし'
  const end = addRationalTimes(range.start, range.duration)
  return `開始 ${range.start.value}/${range.start.rate}・終了 ${end.value}/${end.rate}`
}

function addRationalTimes(
  left: TranscriptToken['effectiveSeekSourceTime'],
  right: TranscriptToken['effectiveSeekSourceTime'],
): TranscriptToken['effectiveSeekSourceTime'] {
  const numerator =
    BigInt(left.value) * BigInt(right.rate) +
    BigInt(right.value) * BigInt(left.rate)
  const denominator = BigInt(left.rate) * BigInt(right.rate)
  const divisor = greatestCommonDivisor(numerator < 0n ? -numerator : numerator, denominator)
  return {
    value: (numerator / divisor).toString(),
    rate: (denominator / divisor).toString(),
  }
}

function greatestCommonDivisor(left: bigint, right: bigint): bigint {
  let current = left
  let remainder = right
  while (remainder !== 0n) {
    const next = current % remainder
    current = remainder
    remainder = next
  }
  return current
}

function isRequiredText(value: string): boolean {
  return value.length !== 0 && value === value.trim() && /\S/u.test(value)
}

function createRequestId(): string {
  return window.crypto.randomUUID().toLowerCase()
}

export function selectTokenForEdit(
  state: TranscriptState,
  tokenId: string,
): TranscriptToken | null {
  return state.snapshot === null ? null : findTranscriptToken(state.snapshot, tokenId)
}
