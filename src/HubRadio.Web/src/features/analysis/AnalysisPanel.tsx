import {
  useCallback,
  useEffect,
  useReducer,
  useRef,
  useState,
  type FormEvent,
} from 'react'

import { createHostBridgeClient } from '../../bridge'
import { isCanonicalBridgeId } from '../../bridge/protocol'
import { usePlayerSourceNavigation } from '../player'
import { useProjectState } from '../projects/ProjectStateProvider'
import {
  createAnalysisAdoptRequest,
  createAnalysisReadRequest,
  createAnalysisRejectRequest,
  createAnalysisUpdateTopicRequest,
  type AnalysisCandidate,
  type AnalysisCandidatePayload,
  type AnalysisSnapshot,
  type AnalysisSpeaker,
  type AnalysisStagedChange,
  type AnalysisTimeRange,
  type AnalysisTopic,
  type AnalysisTopicUpdate,
  type AnalysisUpdateTopicRequest,
} from './analysisProtocol'
import {
  startAnalysisBridgeSession,
  type AnalysisBridgeClient,
  type AnalysisBridgeSession,
  type AnalysisDispatch,
} from './analysisBridgeSession'
import {
  analysisStateReducer,
  analysisTopicInputIsCurrent,
  getAnalysisTopicValue,
  hasLocalAnalysisInputs,
  initialAnalysisState,
  isAnalysisDirty,
  type AnalysisState,
  type AnalysisTopicField,
  type AnalysisTopicInputs,
  type AnalysisUiStatus,
} from './analysisState'
import { formatDuration, formatTimestamp } from '../results/humanReadable'

const statusMessages: Readonly<Record<AnalysisUiStatus, string>> = {
  idle: '確定済みProjectを開くと解析結果を確認できます',
  loading: '正規の解析 snapshot を確認しています',
  available: '解析結果を読み込みました',
  forbidden: 'この解析結果を表示または判断する権限がありません',
  unavailable: '利用できる解析 snapshot がありません',
  staleBase: '解析結果が更新されました。最新版を読み込んでください',
  conflict: '別の変更と競合しました。最新版を読み込んでください',
  noChange: '内容は変更されませんでした',
  invalid: '解析要求または応答を検証できませんでした',
  bridgeUnavailable: 'ネイティブホストへ接続できません',
  sendFailed: 'ネイティブホストへ要求を送信できません',
}

const navigationMessages = Object.freeze({
  requested: '元時刻への移動をプレイヤーへ要求しました。',
  unavailable: '同じProjectの代理動画を先に読み込んでください。',
  projectMismatch: 'プレイヤーには別のProjectが読み込まれています。',
  busy: 'プレイヤーの移動確認が完了するまでお待ちください。',
  outOfRange: 'この元時刻に対応する代理フレームはありません。',
} as const)

const producerLabels: Readonly<Record<AnalysisCandidate['producer'], string>> = {
  termCorrection: '用語辞書',
  silence: '無音解析',
  editorialPhrase: '編集指示語句',
  repetition: '反復解析',
  topicBoundary: 'トピック境界解析',
  audioReview: '音声レビュー',
  contextDeviation: '文脈逸脱解析',
  emphasis: '強調解析',
}

const displayKindLabels: Readonly<Record<AnalysisCandidate['displayKind'], string>> = {
  termCorrection: '用語修正候補',
  deleteCandidate: '削除候補',
  topicBoundary: 'トピック境界候補',
  highlight: '見どころ候補（強調）',
  technicalWarning: '注意候補（技術的な注意）',
}

const freshnessLabels: Readonly<Record<AnalysisCandidate['freshness'], string>> = {
  current: '現在版',
  stale: '旧版・判断不可',
}

const decisionLabels = Object.freeze({
  proposed: '未判断',
  reviewed: '修正済み',
  rejected: '却下済み',
  adopted: '採用済み',
} as const)

const sensitivityLabels = Object.freeze({
  emailAddress: 'メールアドレスの可能性',
  internalNetworkLocation: '内部ネットワーク位置の可能性',
  credentialPattern: '認証情報形式の可能性',
  dictionaryTerm: '機微語辞書との一致',
  customerName: '顧客名・取引先名の可能性',
  unreleasedProduct: '未公開製品の可能性',
  personalInformation: '個人情報の可能性',
  financialInformation: '契約・価格・売上情報の可能性',
  confidentialMaterial: '社外秘の資料の可能性',
  futurePlan: '将来計画の可能性',
} as const)

/** analysis.read needs only the selected project's ID; revisions come from its snapshot. */
export interface AnalysisPanelContext {
  readonly projectId: string | null
  /** Meaningful native registration identity, never a per-poll progress revision. */
  readonly refreshKey?: string
}

export interface AnalysisPanelProps {
  readonly projectId?: string | null
  readonly context?: AnalysisPanelContext
  readonly active?: boolean
  readonly readOnly?: boolean
  readonly onDirtyChange?: (dirty: boolean) => void
  readonly createClient?: () => AnalysisBridgeClient
}

export function AnalysisPanel(props: AnalysisPanelProps) {
  // Explicit workflow context must not fall back to a provider's selected project.
  return props.context === undefined
    ? <ProjectAnalysisPanel {...props} />
    : <AnalysisPanelView {...props} />
}

function ProjectAnalysisPanel({
  projectId: explicitProjectId,
  active = true,
  readOnly = false,
  onDirtyChange,
  createClient = createHostBridgeClient,
}: AnalysisPanelProps) {
  const { state: projectState } = useProjectState()
  const selectedProjectId =
    explicitProjectId === undefined
      ? projectState.draft?.isFinalized === true
        ? projectState.draft.projectId
        : null
      : explicitProjectId

  return (
    <AnalysisPanelView
      projectId={selectedProjectId}
      active={active}
      readOnly={readOnly}
      onDirtyChange={onDirtyChange}
      createClient={createClient}
    />
  )
}

export interface AnalysisPanelViewProps {
  readonly projectId?: string | null
  readonly context?: AnalysisPanelContext
  readonly active?: boolean
  readonly readOnly?: boolean
  readonly onDirtyChange?: (dirty: boolean) => void
  readonly createClient?: () => AnalysisBridgeClient
}

export function AnalysisPanelView({
  projectId = null,
  context,
  active = true,
  readOnly = false,
  onDirtyChange,
  createClient = createHostBridgeClient,
}: AnalysisPanelViewProps) {
  const isContextual = context !== undefined
  const suppliedProjectId = isContextual ? context?.projectId : projectId
  const selectedProjectId = isContextual
    ? typeof suppliedProjectId === 'string' && isCanonicalBridgeId(suppliedProjectId)
      ? suppliedProjectId
      : null
    : projectId

  return (
    <AnalysisPanelSession
      key={selectedProjectId ?? 'no-project'}
      projectId={selectedProjectId}
      isContextual={isContextual}
      refreshKey={context?.refreshKey}
      active={active}
      readOnly={readOnly}
      onDirtyChange={onDirtyChange}
      createClient={createClient}
    />
  )
}

function AnalysisPanelSession({
  projectId,
  isContextual,
  refreshKey,
  active,
  readOnly,
  onDirtyChange,
  createClient,
}: Readonly<{
  projectId: string | null
  isContextual: boolean
  refreshKey?: string
  active: boolean
  readOnly: boolean
  onDirtyChange?: (dirty: boolean) => void
  createClient: () => AnalysisBridgeClient
}>) {
  const [state, reduce] = useReducer(analysisStateReducer, {
    ...initialAnalysisState,
    projectId,
  })
  const [inputMessage, setInputMessage] = useState<string | null>(null)
  const sessionRef = useRef<AnalysisBridgeSession | null>(null)
  const autoReadAttemptedRef = useRef(false)
  const readRefreshKeyRef = useRef(refreshKey)
  const [readRefreshKey, setReadRefreshKey] = useState(refreshKey)
  const createClientRef = useRef(createClient)
  const stateRef = useRef<AnalysisState>(state)
  const topicRequestRef = useRef<AnalysisUpdateTopicRequest | null>(null)
  const lastReportedDirtyRef = useRef<boolean | null>(null)
  const panelRef = useRef<HTMLElement | null>(null)
  const resultTitleRef = useRef<HTMLHeadingElement | null>(null)
  const hadResultsRef = useRef(false)
  const requestPlayerSourceSeek = usePlayerSourceNavigation()
  createClientRef.current = createClient
  stateRef.current = state

  const dispatch = useCallback<AnalysisDispatch>((action) => {
    const request = topicRequestRef.current
    const correlatedAction = action.type === 'requestStarted' &&
      request !== null && request.requestId === action.requestId &&
      request.method === action.method && request.params.projectId === action.projectId
      ? {
          ...action,
          topicUpdate: { topicId: request.params.topicId, field: request.params.update.field },
        }
      : action
    // Guard imperative events even before React commits a batched send/response.
    // Both reductions receive the same actions; input identities stay within each state.
    stateRef.current = analysisStateReducer(stateRef.current, correlatedAction)
    reduce(correlatedAction)
  }, [reduce])

  const dirty = isAnalysisDirty(state)
  useEffect(() => {
    if (onDirtyChange === undefined || lastReportedDirtyRef.current === dirty) return
    // An inline parent callback may change identity after every notification.
    lastReportedDirtyRef.current = dirty
    onDirtyChange(dirty)
  }, [dirty, onDirtyChange])

  useEffect(() => {
    if (isContextual && projectId === null) return
    const session = startAnalysisBridgeSession(createClientRef.current, dispatch)
    sessionRef.current = session
    autoReadAttemptedRef.current = false
    dispatch({ type: 'sessionStarted' })
    return () => {
      if (sessionRef.current === session) sessionRef.current = null
      session.dispose()
    }
  }, [dispatch, isContextual, projectId])

  useEffect(() => {
    if (projectId === null || (isContextual && !active) ||
      (autoReadAttemptedRef.current && readRefreshKeyRef.current === refreshKey) ||
      isAnalysisDirty(stateRef.current) || stateRef.current.pending !== null) return
    const session = sessionRef.current
    if (session === null) return
    let cancelled = false
    // Cancel StrictMode's discarded setup, without reconnecting when a tab is hidden.
    queueMicrotask(() => {
      if (cancelled || sessionRef.current !== session ||
        (autoReadAttemptedRef.current && readRefreshKeyRef.current === refreshKey) ||
        isAnalysisDirty(stateRef.current) || stateRef.current.pending !== null ||
        (refreshKey !== undefined && panelRef.current?.closest('[hidden], [inert], [aria-hidden="true"]') !== null)) return
      // Consume the attempt even on failure; recovery is an explicit reload, not an effect loop.
      autoReadAttemptedRef.current = true
      readRefreshKeyRef.current = refreshKey
      setReadRefreshKey(refreshKey)
      try {
        session.send(createAnalysisReadRequest(createRequestId(), projectId))
      } catch {
        dispatch({ type: 'requestInvalid' })
      }
    })
    return () => { cancelled = true }
  }, [active, dispatch, isContextual, projectId, refreshKey, dirty, state.pending])

  const sendRead = useCallback(() => {
    if (!active || panelRef.current?.closest('[hidden], [inert], [aria-hidden="true"]') !== null ||
      projectId === null || stateRef.current.pending !== null) return
    autoReadAttemptedRef.current = true
    readRefreshKeyRef.current = refreshKey
    setReadRefreshKey(refreshKey)
    setInputMessage(null)
    try {
      send(
        sessionRef.current,
        createAnalysisReadRequest(createRequestId(), projectId),
        dispatch,
      )
    } catch {
      setInputMessage('Project ID を解析要求として検証できませんでした。')
    }
  }, [active, dispatch, projectId, refreshKey])

  const snapshot = state.snapshot
  const hasResults = snapshot !== null
  const pending = state.pending !== null
  const navigationDisabled = pending || !active
  const mutationDisabled = readOnly || navigationDisabled
  const reason = state.reasonInput.value
  const hasLocalInputs = hasLocalAnalysisInputs(state)
  const mutationPending = state.pending !== null && state.pending.method !== 'analysis.read'

  const localControlsBlocked = (): boolean => readOnly || !active ||
    stateRef.current.pending !== null ||
    panelRef.current?.closest('[hidden], [inert], [aria-hidden="true"]') !== null

  const canUseEvidence = (): boolean => stateRef.current.snapshot?.projectId === projectId &&
    (stateRef.current.status === 'available' || stateRef.current.status === 'noChange')

  const discardTopicInput = (topicId: string, field: AnalysisTopicField): void => {
    if (localControlsBlocked()) return
    dispatch({ type: 'topicInputDiscarded', topicId, field })
    setInputMessage(null)
  }

  const discardLocalInputs = (): void => {
    if (localControlsBlocked()) return
    dispatch({ type: 'localInputsDiscarded' })
    setInputMessage(null)
  }

  const changeTopicInput = (topic: AnalysisTopic, field: AnalysisTopicField, value: string): void => {
    if (localControlsBlocked() || !canUseEvidence() ||
      !stateRef.current.snapshot?.topics.includes(topic)) return
    dispatch({ type: 'topicInputChanged', topicId: topic.topicId, field, value })
  }

  useEffect(() => {
    const justLoaded = hasResults && !hadResultsRef.current
    hadResultsRef.current ||= hasResults
    // A hidden load is still successful; switching tabs must not steal focus later.
    if (!justLoaded || !isContextual || !active) return
    const heading = resultTitleRef.current
    if (heading === null || heading.closest('[hidden], [inert], [aria-hidden="true"]') !== null) return
    heading.focus({ preventScroll: true })
  }, [active, hasResults, isContextual])

  const decideCandidate = (
    candidate: AnalysisCandidate,
    decision: 'adopt' | 'reject',
  ): void => {
    const currentSnapshot = stateRef.current.snapshot
    if (localControlsBlocked() || !canUseEvidence() || currentSnapshot === null ||
      !currentSnapshot.candidates.includes(candidate)) return
    const currentReason = stateRef.current.reasonInput.value
    if (!isRequiredText(currentReason)) {
      setInputMessage('採否の理由を入力してください。')
      return
    }
    if (
      (decision === 'adopt' && !candidate.canAdopt) ||
      (decision === 'reject' && !candidate.canReject)
    ) {
      setInputMessage('この候補は現在の状態では選択した判断を行えません。')
      return
    }

    const parameters = {
      projectId: currentSnapshot.projectId,
      revisionId: currentSnapshot.revisionId,
      digest: currentSnapshot.revisionDigest,
      candidateId: candidate.candidateId,
      reason: currentReason,
    }
    setInputMessage(null)
    try {
      send(
        sessionRef.current,
        decision === 'adopt'
          ? createAnalysisAdoptRequest(createRequestId(), parameters)
          : createAnalysisRejectRequest(createRequestId(), parameters),
        dispatch,
      )
    } catch {
      setInputMessage('候補の判断を安全な要求として検証できませんでした。')
    }
  }

  const updateTopic = (topic: AnalysisTopic, field: AnalysisTopicField): void => {
    const currentSnapshot = stateRef.current.snapshot
    if (localControlsBlocked() || !canUseEvidence() || currentSnapshot === null ||
      !currentSnapshot.topics.includes(topic)) return
    const input = stateRef.current.topicInputs[topic.topicId]?.[field]
    if (input !== undefined && !analysisTopicInputIsCurrent(currentSnapshot, topic, field, input)) return
    const currentReason = stateRef.current.reasonInput.value
    if (!isRequiredText(currentReason)) {
      setInputMessage('トピック変更の理由を入力してください。')
      return
    }
    setInputMessage(null)
    try {
      const request = createAnalysisUpdateTopicRequest(createRequestId(), {
        projectId: currentSnapshot.projectId,
        revisionId: currentSnapshot.revisionId,
        digest: currentSnapshot.revisionDigest,
        topicId: topic.topicId,
        expectedTopicRevisionId: input?.base.metadataRevisionId ?? topic.metadataRevisionId,
        update: topicUpdate(field, input?.value ?? getAnalysisTopicValue(topic, field)),
        reason: currentReason,
      })
      // The existing bridge emits requestStarted synchronously before transport/ACK.
      topicRequestRef.current = request
      send(sessionRef.current, request, dispatch)
    } catch {
      setInputMessage('トピック入力を安全な単一項目の変更として検証できませんでした。')
    } finally {
      topicRequestRef.current = null
    }
  }

  const navigateToRange = (range: AnalysisTimeRange): void => {
    const currentSnapshot = stateRef.current.snapshot
    if (!active || currentSnapshot === null || currentSnapshot.projectId !== projectId ||
      stateRef.current.pending !== null ||
      panelRef.current?.closest('[hidden], [inert], [aria-hidden="true"]') !== null) return
    const outcome = requestPlayerSourceSeek({
      projectId: currentSnapshot.projectId,
      sourceTime: range.start,
    })
    dispatch({ type: 'navigationReported', message: navigationMessages[outcome] })
  }

  return (
    <section ref={panelRef} className="analysis-panel" aria-labelledby="analysis-title" hidden={!active}>
      <div className="analysis-heading">
        <div>
          <p className="eyebrow">Revision-bound analysis</p>
          <h2 id="analysis-title" ref={resultTitleRef} tabIndex={isContextual ? -1 : undefined}>
            解析結果
          </h2>
        </div>
        <div className="analysis-heading-actions">
          <p className="analysis-status" role="status" data-analysis-status={state.status}>
            {isContextual && projectId === null
              ? '未選択 — 解析するProjectを選択してください。'
              : isContextual && (state.status === 'bridgeUnavailable' || state.status === 'unavailable')
                ? `未接続 — ${statusMessages[state.status]}`
                : statusMessages[state.status]}
          </p>
          {!isContextual || projectId !== null ? (
            <button
              type="button"
              className="quiet-button"
              disabled={projectId === null || navigationDisabled}
              onClick={sendRead}
            >
              最新版を読み込む
            </button>
          ) : null}
        </div>
      </div>

      {!isContextual && projectId === null ? (
        <div className="analysis-empty">
          <p>確定済みProjectをProject一覧から開いてください。</p>
        </div>
      ) : null}

      {dirty && autoReadAttemptedRef.current && readRefreshKey !== refreshKey ? (
        <p className="analysis-registration-notice" role={active ? 'status' : undefined}>
          解析データが更新されました。未送信の入力を保持しています。保存または取消後、表示中に最新版を読み込みます。
        </p>
      ) : null}

      {snapshot === null && !hasLocalInputs ? null : (
        <div className="analysis-local-inputs">
          <label className="analysis-reason">
            <span>判断・変更理由</span>
            <input
              type="text"
              value={reason}
              disabled={mutationDisabled}
              readOnly={snapshot === null}
              onChange={(event) => {
                if (!localControlsBlocked() && canUseEvidence()) {
                  dispatch({ type: 'reasonInputChanged', value: event.currentTarget.value })
                }
              }}
              aria-describedby="analysis-reason-help"
            />
          </label>
          <p id="analysis-reason-help" className="field-help">
            {snapshot === null
              ? '保持した未送信の理由です。選択してコピーできます。最新版の確認前は編集・送信できません。'
              : '候補の採否またはトピック1項目の変更に記録する理由です。'}
          </p>
          {hasLocalInputs ? (
            <div className="analysis-persistence-notice" role="note">
              <p>未送信の入力だけを取り消します。ホスト受付済みの未永続化の変更は取り消しません。</p>
              <button type="button" disabled={mutationDisabled} onClick={discardLocalInputs}>
                未送信の入力を取り消す
              </button>
            </div>
          ) : null}
        </div>
      )}
      {mutationPending ? (
        <p className="analysis-operation-status" role="status">変更要求を送信中です。</p>
      ) : null}
      {inputMessage === null ? null : (
        <p className="analysis-input-error" role="alert">{inputMessage}</p>
      )}

      <RetainedTopicInputs
        inputs={state.topicInputs}
        snapshot={snapshot}
        showRawIdFields={!isContextual}
        active={active}
        disabled={mutationDisabled}
        onDiscard={discardTopicInput}
      />

      {snapshot === null ? null : (
        <div className="analysis-workspace">
          <div className="analysis-persistence-notice" role="note">
            <strong>{readOnly ? '参照専用' : '作業中データ'}</strong>
            <span>{readOnly
              ? '最新版の読み込みと元時刻・文字起こしへの移動は利用できます。ここでは採用・編集できません。'
              : 'ここでの採用・編集は自動保存されません。ホストのメモリ内への反映であり、永続化や未送信の入力とは別です。'}</span>
          </div>

          <p className="analysis-confidence-note">
            確度は解析元の正規値または「未提供」をそのまま表示し、画面側で補完しません。
          </p>

          {state.operationMessage === null ? null : (
            <p className="analysis-operation-status" role="status">{state.operationMessage}</p>
          )}
          {state.navigationMessage === null ? null : (
            <p className="analysis-navigation-status" role="status">
              {state.navigationMessage}
            </p>
          )}

          <div className="analysis-evidence-summary">
            <span>解析日時</span>
            <time dateTime={snapshot.createdAt}>{new Date(snapshot.createdAt).toLocaleString('ja-JP')}</time>
          </div>

          <section className="analysis-section" aria-labelledby="analysis-speakers-title">
            <div className="analysis-section-heading">
              <div>
                <h3 id="analysis-speakers-title">話者</h3>
                <p>解析対象の発話位置と現在の話者表示です。</p>
              </div>
              <a className="analysis-edit-link" href="#transcript-title">
                文字起こし編集で話者を変更
              </a>
            </div>
            {snapshot.speakers.length === 0 ? (
              <p>話者情報はありません。</p>
            ) : (
              <div className="analysis-card-grid">
                {snapshot.speakers.map((speaker) => (
                  <SpeakerCard
                    key={speaker.speakerId}
                    speaker={speaker}
                    disabled={navigationDisabled}
                    onNavigate={() => navigateToRange(speaker.source.range)}
                  />
                ))}
              </div>
            )}
          </section>

          <section className="analysis-section" aria-labelledby="analysis-topics-title">
            <div className="analysis-section-heading">
              <div>
                <h3 id="analysis-topics-title">トピック</h3>
                <p>各保存ボタンは1項目だけを現在の metadata 版へ反映します。</p>
              </div>
            </div>
            {snapshot.topics.length === 0 ? (
              <p>トピック情報はありません。</p>
            ) : (
              <div className="analysis-topic-list">
                {snapshot.topics.map((topic) => (
                  <TopicCard
                    key={topic.topicId}
                    topic={topic}
                    snapshot={snapshot}
                    inputs={state.topicInputs[topic.topicId]}
                    speakers={snapshot.speakers}
                    disabled={mutationDisabled}
                    navigationDisabled={navigationDisabled}
                    showRawIdFields={!isContextual}
                    onNavigate={() => navigateToRange(topic.range)}
                    onChange={(field, value) => changeTopicInput(topic, field, value)}
                    onUpdate={(field) => updateTopic(topic, field)}
                    onDiscard={(field) => discardTopicInput(topic.topicId, field)}
                  />
                ))}
              </div>
            )}
          </section>

          <CandidateSection
            candidates={snapshot.candidates}
            disabled={mutationDisabled}
            navigationDisabled={navigationDisabled}
            onNavigate={(candidate) => navigateToRange(candidate.source.range)}
            onDecide={decideCandidate}
          />

          <SensitivitySection
            sensitivity={snapshot.sensitivity}
            disabled={navigationDisabled}
            onNavigate={navigateToRange}
          />

          <StagedChanges changes={snapshot.stagedChanges} />
        </div>
      )}
    </section>
  )
}

function SpeakerCard({
  speaker,
  disabled,
  onNavigate,
}: Readonly<{
  speaker: AnalysisSpeaker
  disabled: boolean
  onNavigate: () => void
}>) {
  return (
    <article className="analysis-card analysis-speaker-card">
      <div className="analysis-card-heading">
        <h4>{speaker.isVisible ? speaker.text : '話者名は非表示'}</h4>
        <span className={speaker.isAssigned ? 'analysis-chip is-current' : 'analysis-chip'}>
          {speaker.isAssigned ? '出演者割当済み' : '未割当'}
        </span>
      </div>
      <p>{formatRange(speaker.source.range)}</p>
      {speaker.titleOrAffiliationConfirmationRequired ? (
        <p className="analysis-warning">肩書・所属の確認が必要です。</p>
      ) : null}
      <button type="button" disabled={disabled} onClick={onNavigate}>
        話者発話の元時刻へ移動
      </button>
    </article>
  )
}

function TopicCard({
  topic,
  snapshot,
  inputs,
  speakers,
  disabled,
  navigationDisabled,
  showRawIdFields,
  onNavigate,
  onChange,
  onUpdate,
  onDiscard,
}: Readonly<{
  topic: AnalysisTopic
  snapshot: AnalysisSnapshot
  inputs: AnalysisTopicInputs | undefined
  speakers: readonly AnalysisSpeaker[]
  disabled: boolean
  navigationDisabled: boolean
  showRawIdFields: boolean
  onNavigate: () => void
  onChange: (field: AnalysisTopicField, value: string) => void
  onUpdate: (field: AnalysisTopicField) => void
  onDiscard: (field: AnalysisTopicField) => void
}>) {
  const fieldProps = (field: AnalysisTopicField) => {
    const input = inputs?.[field]
    return {
      value: input?.value ?? getAnalysisTopicValue(topic, field),
      disabled,
      hasInput: input !== undefined,
      stale: input !== undefined && !analysisTopicInputIsCurrent(snapshot, topic, field, input),
      helpId: `analysis-topic-${topic.topicId}-${field}-input`,
      onChange: (value: string) => onChange(field, value),
      onSave: () => onUpdate(field),
      onDiscard: () => onDiscard(field),
    }
  }
  const mainSpeakerProps: TopicFieldProps = {
    ...fieldProps('mainSpeaker'), label: '主話者', origin: topic.mainSpeakerOrigin,
  }

  return (
    <article className="analysis-topic-card" data-topic-id={topic.topicId}>
      <header className="analysis-card-heading">
        <div>
          <h4>{topic.title}</h4>
          <p>{formatRange(topic.range)}</p>
        </div>
        <div className="analysis-topic-actions">
          <span className="analysis-chip">変更元 {fieldLabel(topic.changedField)}</span>
          <button type="button" disabled={navigationDisabled} onClick={onNavigate}>
            トピックの元時刻へ移動
          </button>
        </div>
      </header>
      {topic.titleRequiresManualReview ? (
        <p className="analysis-warning">仮タイトルです。手動確認が必要です。</p>
      ) : null}
      {topic.mainSpeakerTieUnresolved ? (
        <p className="analysis-warning">主話者の重複時間が同率のため未確定です。</p>
      ) : null}

      <div className="analysis-topic-fields">
        <TopicTextField
          {...fieldProps('title')}
          label="タイトル"
          origin={topic.titleOrigin}
        />
        <TopicTextAreaField
          {...fieldProps('summary')}
          label="要約"
          origin={topic.summaryOrigin}
        />
        {mainSpeakerProps.stale ? <TopicTextField {...mainSpeakerProps} /> : (
        <form
          className="analysis-topic-field"
          onSubmit={(event) => {
            event.preventDefault()
            if (disabled) return
            onUpdate('mainSpeaker')
          }}
        >
          <label>
            <span>主話者</span>
            <select
              value={mainSpeakerProps.value}
              disabled={disabled}
              aria-describedby={mainSpeakerProps.hasInput ? mainSpeakerProps.helpId : undefined}
              onChange={(event) => {
                if (!disabled) mainSpeakerProps.onChange(event.currentTarget.value)
              }}
            >
              <option value="">未設定</option>
              {speakers.map((speaker) => (
                <option key={speaker.speakerId} value={speaker.speakerId}>
                  {speaker.isVisible ? speaker.text : '非表示の話者'}
                </option>
              ))}
            </select>
          </label>
          <p className="analysis-field-origin">由来: {originLabel(topic.mainSpeakerOrigin)}</p>
          <TopicInputNotice {...mainSpeakerProps} />
          <button type="submit" disabled={disabled}>主話者を保存</button>
        </form>
        )}
        <TopicListField
          {...fieldProps('keywords')}
          label="キーワード"
          origin={topic.keywordsOrigin}
        />
        {showRawIdFields ? (
          <TopicListField
            {...fieldProps('importantStatements')}
            label="重要発言の元区間 ID"
            origin={topic.importantStatementsOrigin}
          />
        ) : null}
        <TopicTextField
          {...fieldProps('audience')}
          label="対象視聴者"
          origin={topic.audienceOrigin}
        />
        <TopicListField
          {...fieldProps('publicationCautions')}
          label="公開時の注意"
          origin={topic.publicationCautionsOrigin}
        />
      </div>
    </article>
  )
}

function TopicTextField(props: TopicFieldProps) {
  return (
    <form className="analysis-topic-field" onSubmit={(event) => submit(event, props.onSave, props.disabled || props.stale)}>
      <label>
        <span>{props.label}</span>
        <input
          type="text"
          value={props.value}
          disabled={props.disabled}
          readOnly={props.stale}
          aria-describedby={props.hasInput ? props.helpId : undefined}
          onChange={(event) => {
            if (!props.disabled && !props.stale) props.onChange(event.currentTarget.value)
          }}
        />
      </label>
      <p className="analysis-field-origin">由来: {originLabel(props.origin)}</p>
      <TopicInputNotice {...props} />
      <button type="submit" disabled={props.disabled || props.stale}>{props.label}を保存</button>
    </form>
  )
}

function TopicTextAreaField(props: TopicFieldProps) {
  return (
    <form className="analysis-topic-field" onSubmit={(event) => submit(event, props.onSave, props.disabled || props.stale)}>
      <label>
        <span>{props.label}</span>
        <textarea
          value={props.value}
          disabled={props.disabled}
          readOnly={props.stale}
          aria-describedby={props.hasInput ? props.helpId : undefined}
          onChange={(event) => {
            if (!props.disabled && !props.stale) props.onChange(event.currentTarget.value)
          }}
        />
      </label>
      <p className="analysis-field-origin">由来: {originLabel(props.origin)}</p>
      <TopicInputNotice {...props} />
      <button type="submit" disabled={props.disabled || props.stale}>{props.label}を保存</button>
    </form>
  )
}

function TopicListField(props: TopicFieldProps) {
  return (
    <form className="analysis-topic-field" onSubmit={(event) => submit(event, props.onSave, props.disabled || props.stale)}>
      <label>
        <span>{props.label}（1行1件）</span>
        <textarea
          value={props.value}
          disabled={props.disabled}
          readOnly={props.stale}
          aria-describedby={props.hasInput ? props.helpId : undefined}
          onChange={(event) => {
            if (!props.disabled && !props.stale) props.onChange(event.currentTarget.value)
          }}
        />
      </label>
      <p className="analysis-field-origin">由来: {originLabel(props.origin)}</p>
      <TopicInputNotice {...props} />
      <button type="submit" disabled={props.disabled || props.stale}>{props.label}を保存</button>
    </form>
  )
}

interface TopicFieldProps {
  readonly label: string
  readonly value: string
  readonly disabled: boolean
  readonly hasInput: boolean
  readonly stale: boolean
  readonly helpId: string
  readonly origin: AnalysisTopic['titleOrigin']
  readonly onChange: (value: string) => void
  readonly onSave: () => void
  readonly onDiscard: () => void
}

function TopicInputNotice(props: TopicFieldProps) {
  if (!props.hasInput) return null
  return (
    <div data-input-status={props.stale ? 'stale' : 'unsent'}>
      <p id={props.helpId} className="field-help">
        {props.stale
          ? '入力の基準版が変わったため送信できません。原文は選択してコピーできます。最新版から編集するには、この項目の入力を取り消してください。'
          : 'この項目には未送信の入力があります。'}
      </p>
      <button type="button" disabled={props.disabled} onClick={props.onDiscard}>
        {props.stale ? 'この入力を取り消して最新版から編集' : 'この入力を取り消す'}
      </button>
    </div>
  )
}

function RetainedTopicInputs({
  inputs, snapshot, showRawIdFields, active, disabled, onDiscard,
}: Readonly<{
  inputs: AnalysisState['topicInputs']
  snapshot: AnalysisSnapshot | null
  showRawIdFields: boolean
  active: boolean
  disabled: boolean
  onDiscard: (topicId: string, field: AnalysisTopicField) => void
}>) {
  const retained = Object.entries(inputs).flatMap(([topicId, fields]) => {
    const topicVisible = snapshot?.topics.some((topic) => topic.topicId === topicId) === true
    return Object.entries(fields).flatMap(([field, input]) =>
      topicVisible && (showRawIdFields || field !== 'importantStatements')
        ? []
        : [{ topicId, field: field as AnalysisTopicField, input }],
    )
  })
  if (retained.length === 0) return null
  return (
    <section className="analysis-section analysis-retained-inputs" aria-labelledby="analysis-retained-title">
      <h3 id="analysis-retained-title">保持している未送信の入力</h3>
      <p>対応する解析結果を表示できないため送信できません。自分の入力だけを保持しています。原文は選択してコピーできます。</p>
      {retained.map(({ topicId, field, input }, index) => (
        <div className="analysis-retained-topic-input" key={`${topicId}-${field}`}>
          <label>
            <span>保持した入力 {index + 1}: {fieldLabel(field)}</span>
            <textarea value={input.value} readOnly disabled={!active} />
          </label>
          <button type="button" disabled={disabled} onClick={() => onDiscard(topicId, field)}>
            この入力を取り消す
          </button>
        </div>
      ))}
    </section>
  )
}

function CandidateSection({
  candidates,
  disabled,
  navigationDisabled,
  onNavigate,
  onDecide,
}: Readonly<{
  candidates: readonly AnalysisCandidate[]
  disabled: boolean
  navigationDisabled: boolean
  onNavigate: (candidate: AnalysisCandidate) => void
  onDecide: (candidate: AnalysisCandidate, decision: 'adopt' | 'reject') => void
}>) {
  const groups = groupCandidates(candidates)
  return (
    <section className="analysis-section" aria-labelledby="analysis-candidates-title">
      <div className="analysis-section-heading">
        <div>
          <h3 id="analysis-candidates-title">解析候補と技術的な注意</h3>
          <p>理由・確度・版の鮮度・採否を、producer固有の根拠とともに表示します。</p>
        </div>
      </div>
      {groups.map(({ kind, items }) => (
        <section className="analysis-candidate-group" key={kind}>
          <h4>{displayKindLabels[kind]}</h4>
          {items.length === 0 ? (
            <p>該当項目はありません。</p>
          ) : (
            <div className="analysis-card-grid">
              {items.map((candidate) => (
                <article className="analysis-card analysis-candidate-card" key={candidate.candidateId}>
                  <div className="analysis-card-heading">
                    <div>
                      <p className="analysis-producer">{producerLabels[candidate.producer]}</p>
                      <h5>{displayKindLabels[candidate.displayKind]}</h5>
                    </div>
                    <div className="analysis-chip-row">
                      <span className={`analysis-chip is-${candidate.freshness}`}>
                        {freshnessLabels[candidate.freshness]}
                      </span>
                      <span className={`analysis-chip is-${candidate.status}`}>
                        {decisionLabels[candidate.status]}
                      </span>
                    </div>
                  </div>
                  <dl className="analysis-facts">
                    <div><dt>理由</dt><dd>{candidate.reason}</dd></div>
                    <div><dt>確度</dt><dd>{formatConfidence(candidate.confidence)}</dd></div>
                    <div><dt>元区間</dt><dd>{formatRange(candidate.source.range)}</dd></div>
                  </dl>
                  {candidate.missingContext === null ? null : (
                    <p className="analysis-warning">不足情報: {candidate.missingContext}</p>
                  )}
                  {candidate.warning === null ? null : (
                    <p className="analysis-warning">注意: {candidate.warning}</p>
                  )}
                  <ProducerEvidence payload={candidate.payload} />
                  {candidate.decision === null ? null : (
                    <div className="analysis-decision">
                      <strong>判断: {decisionKindLabel(candidate.decision.decision)}</strong>
                      <span>{candidate.decision.reason}</span>
                      <time dateTime={candidate.decision.decidedAt}>
                        {candidate.decision.decidedAt}
                      </time>
                    </div>
                  )}
                  <div className="analysis-card-actions">
                    <button type="button" disabled={navigationDisabled} onClick={() => onNavigate(candidate)}>
                      元時刻へ移動
                    </button>
                    {candidate.displayKind === 'technicalWarning' ? null : (
                      <button
                        type="button"
                        disabled={disabled || !candidate.canAdopt}
                        onClick={() => onDecide(candidate, 'adopt')}
                      >
                        採用
                      </button>
                    )}
                    <button
                      type="button"
                      disabled={disabled || !candidate.canReject}
                      onClick={() => onDecide(candidate, 'reject')}
                    >
                      却下
                    </button>
                  </div>
                </article>
              ))}
            </div>
          )}
        </section>
      ))}
    </section>
  )
}

function ProducerEvidence({ payload }: Readonly<{ payload: AnalysisCandidatePayload }>) {
  switch (payload.producer) {
    case 'termCorrection':
      return <EvidenceList items={[
        `表記: ${payload.observedText} → ${payload.proposedText}`,
        `辞書読み: ${payload.dictionaryReading}`,
        `一致方式: ${payload.matchKind}`,
      ]} />
    case 'silence':
      return <EvidenceList items={[
        payload.detection.description,
        `ノイズレベル: ${payload.noiseDb} dB`,
        `最小時間: ${formatRational(payload.minimumDuration)}`,
        '本番閾値未承認・自動削除不可',
      ]} />
    case 'editorialPhrase':
      return <EvidenceList items={[
        payload.detection.description,
        `一致語句: ${payload.matchedPhrase}`,
        `辞書版: ${payload.dictionaryVersion}`,
        `発話時刻fallback: ${yesNo(payload.usesUtteranceTimingFallback)}`,
      ]} />
    case 'repetition':
      return <EvidenceList items={[
        `規則: ${payload.features.rule}`,
        `一致token数: ${payload.features.matchingTokenCount}`,
        `完全一致: ${yesNo(payload.features.exactNfkcMatch)}`,
        `残す側の選択が必要: ${yesNo(payload.features.retentionChoiceRequired)}`,
      ]} />
    case 'topicBoundary':
      return <EvidenceList items={[
        payload.detection.description,
        `推奨境界: ${formatRational(payload.suggestedBoundary)}`,
        `観測間隔: ${formatRational(payload.features.observedGap)}`,
        `話者変更: ${yesNo(payload.features.speakerChanged)}`,
      ]} />
    case 'audioReview':
      return <EvidenceList items={[
        payload.detection.description,
        `音声事象: ${payload.eventKind}`,
        `確認方法: ${payload.evidenceMethods.join(' / ') || '未提供'}`,
        `手動試聴済み: ${yesNo(payload.manualListeningConfirmed)}`,
        `自動削除可: ${yesNo(payload.automaticDeletionAllowed)}`,
      ]} />
    case 'contextDeviation':
      return <EvidenceList items={[
        payload.detection.description,
        `位置: ${payload.features.position}`,
        `最寄りトピックまで: ${formatRational(payload.features.distanceToNearestTopicRange)}`,
        `意味距離を計算: ${yesNo(payload.features.semanticDistanceComputed)}`,
      ]} />
    case 'emphasis':
      return <EvidenceList items={[
        `表示文: ${payload.text}`,
        `文字出典: ${payload.textSource}`,
        `強調理由: ${payload.reasons.join(' / ') || '未提供'}`,
        `自動採用可: ${yesNo(payload.automaticAdoptionAllowed)}`,
      ]} />
  }

  const exhaustivePayload: never = payload
  return exhaustivePayload
}

function EvidenceList({ items }: Readonly<{ items: readonly string[] }>) {
  return (
    <ul className="analysis-producer-evidence">
      {items.map((item) => <li key={item}>{item}</li>)}
    </ul>
  )
}

function SensitivitySection({
  sensitivity,
  disabled,
  onNavigate,
}: Readonly<{
  sensitivity: AnalysisState['snapshot'] extends infer _ ? NonNullable<AnalysisState['snapshot']>['sensitivity'] : never
  disabled: boolean
  onNavigate: (range: AnalysisTimeRange) => void
}>) {
  return (
    <section className="analysis-section analysis-sensitivity" aria-labelledby="analysis-sensitivity-title">
      <div className="analysis-section-heading">
        <div>
          <h3 id="analysis-sensitivity-title">公開前の機微情報確認</h3>
          <p>一致した実値は表示せず、分類・理由コード・元区間だけを示します。</p>
        </div>
        <span className={`analysis-chip ${sensitivity.requiresExternalOutputWarning ? 'is-warning' : 'is-current'}`}>
          {sensitivity.requiresExternalOutputWarning ? '外部出力前に確認' : '警告なし'}
        </span>
      </div>
      {!sensitivity.isComplete ? (
        <p className="analysis-warning">走査は未完了です。外部出力前に再確認してください。</p>
      ) : null}
      {sensitivity.findings.length === 0 ? (
        <p>安全に表示できる指摘はありません。</p>
      ) : (
        <ul className="analysis-sensitivity-list">
          {sensitivity.findings.map((finding, index) => (
            <li key={`${finding.utteranceId}-${finding.kind}-${index}`}>
              <div>
                <strong>{sensitivityLabels[finding.kind]}</strong>
                <span>{formatRange(finding.sourceRange)}</span>
              </div>
              <button type="button" disabled={disabled} onClick={() => onNavigate(finding.sourceRange)}>
                指摘の元時刻へ移動
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function StagedChanges({ changes }: Readonly<{ changes: readonly AnalysisStagedChange[] }>) {
  return (
    <section className="analysis-section analysis-staged" aria-labelledby="analysis-staged-title">
      <div className="analysis-section-heading">
        <div>
          <h3 id="analysis-staged-title">ホスト受付済みの変更（未永続化）</h3>
          <p>採用と編集をホストのメモリ内へ反映した結果です。いずれも persisted = false で、未送信の入力とは別です。</p>
        </div>
        <span className="analysis-chip is-warning">未永続化</span>
      </div>
      {changes.length === 0 ? (
        <p>この解析セッションにホスト受付済みの変更はありません。</p>
      ) : (
        <ul className="analysis-staged-list">
          {changes.map((change, index) => (
            <li key={stagedChangeKey(change, index)}>
              <strong>{stagedChangeLabel(change)}</strong>
              <span>未永続化</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function send(
  session: AnalysisBridgeSession | null,
  request: Parameters<AnalysisBridgeSession['send']>[0],
  dispatch: AnalysisDispatch,
): void {
  if (session === null) {
    dispatch({ type: 'bridgeUnavailable' })
    return
  }
  session.send(request)
}

function submit(event: FormEvent<HTMLFormElement>, onSave: () => void, disabled: boolean): void {
  event.preventDefault()
  if (!disabled) onSave()
}

function groupCandidates(candidates: readonly AnalysisCandidate[]) {
  return (['termCorrection', 'deleteCandidate', 'topicBoundary', 'highlight', 'technicalWarning'] as const)
    .map((kind) => ({ kind, items: candidates.filter((candidate) => candidate.displayKind === kind) }))
}

function splitEntries(value: string): readonly string[] {
  return [...new Set(value.split(/\r?\n/u).map((entry) => entry.trim()).filter(Boolean))]
}

function topicUpdate(field: AnalysisTopicField, value: string): AnalysisTopicUpdate {
  switch (field) {
    case 'title':
    case 'audience': return { field, text: value }
    case 'summary': return { field, text: value === '' ? null : value }
    case 'mainSpeaker': return { field, speakerId: value || null }
    case 'keywords':
    case 'publicationCautions': return { field, values: splitEntries(value) }
    case 'importantStatements': return { field, sourceSpanIds: splitEntries(value) }
  }
}

function formatConfidence(value: number | null): string {
  return value === null ? '未提供' : value.toString()
}

function formatRange(range: AnalysisTimeRange): string {
  return `${formatTimestamp(range.start.value, range.start.rate)} から ${formatDuration(range.duration.value, range.duration.rate)}`
}

function formatRational(value: AnalysisTimeRange['start']): string {
  return formatTimestamp(value.value, value.rate)
}

function yesNo(value: boolean): string {
  return value ? 'はい' : 'いいえ'
}

function originLabel(origin: AnalysisTopic['titleOrigin']): string {
  switch (origin) {
    case 'unset': return '未設定'
    case 'deterministicPlaceholder': return '決定論的な仮値'
    case 'projectSnapshot': return 'Project snapshot'
    case 'deterministicTranscriptOverlap': return '文字起こし重複時間'
    case 'manualEditorial': return '手動編集'
  }
}

function fieldLabel(field: AnalysisTopic['changedField']): string {
  switch (field) {
    case 'initial': return '初期値'
    case 'title': return 'タイトル'
    case 'summary': return '要約'
    case 'mainSpeaker': return '主話者'
    case 'keywords': return 'キーワード'
    case 'importantStatements': return '重要発言'
    case 'audience': return '対象視聴者'
    case 'publicationCautions': return '公開時の注意'
  }
}

function decisionKindLabel(decision: NonNullable<AnalysisCandidate['decision']>['decision']): string {
  switch (decision) {
    case 'rejected': return '却下'
    case 'modified': return '修正'
    case 'adopted': return '採用'
  }
}

function stagedChangeLabel(change: AnalysisStagedChange): string {
  switch (change.kind) {
    case 'transcriptRevision': return '文字起こしの修正'
    case 'editOperation': return change.operation.kind === 'delete' ? '削除操作' : '分割操作'
    case 'cue': return '強調テロップ'
    case 'topicMetadataRevision': return `トピック情報（${fieldLabel(change.field)}）`
  }
}

function stagedChangeKey(change: AnalysisStagedChange, index: number): string {
  switch (change.kind) {
    case 'transcriptRevision':
    case 'editOperation':
    case 'cue':
      return `${change.kind}-${change.candidateId}-${index}`
    case 'topicMetadataRevision':
      return `${change.kind}-${change.topicId}-${change.revisionId}`
  }
}

function isRequiredText(value: string): boolean {
  return value.length !== 0 && value === value.trim() && /\S/u.test(value)
}

function createRequestId(): string {
  return window.crypto.randomUUID().toLowerCase()
}