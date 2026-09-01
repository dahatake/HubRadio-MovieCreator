import {
  useCallback,
  useEffect,
  useReducer,
  useRef,
  useState,
  type FormEvent,
} from 'react'

import { createHostBridgeClient } from '../../bridge'
import { usePlayerSourceNavigation } from '../player'
import { useProjectState } from '../projects/ProjectStateProvider'
import {
  createAnalysisAdoptRequest,
  createAnalysisReadRequest,
  createAnalysisRejectRequest,
  createAnalysisUpdateTopicRequest,
  type AnalysisCandidate,
  type AnalysisCandidatePayload,
  type AnalysisSpeaker,
  type AnalysisStagedChange,
  type AnalysisTimeRange,
  type AnalysisTopic,
  type AnalysisTopicUpdate,
} from './analysisProtocol'
import {
  startAnalysisBridgeSession,
  type AnalysisBridgeClient,
  type AnalysisBridgeSession,
  type AnalysisDispatch,
} from './analysisBridgeSession'
import {
  analysisStateReducer,
  initialAnalysisState,
  type AnalysisState,
  type AnalysisUiStatus,
} from './analysisState'

const statusMessages: Readonly<Record<AnalysisUiStatus, string>> = {
  idle: '確定済み案件を開くと解析結果を確認できます',
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
  unavailable: '同じ案件の代理動画を先に読み込んでください。',
  projectMismatch: 'プレイヤーには別の案件が読み込まれています。',
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
} as const)

export interface AnalysisPanelProps {
  readonly projectId?: string | null
  readonly createClient?: () => AnalysisBridgeClient
}

export function AnalysisPanel({
  projectId: explicitProjectId,
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
      key={selectedProjectId ?? 'no-project'}
      projectId={selectedProjectId}
      createClient={createClient}
    />
  )
}

export interface AnalysisPanelViewProps {
  readonly projectId: string | null
  readonly createClient?: () => AnalysisBridgeClient
}

export function AnalysisPanelView({
  projectId,
  createClient = createHostBridgeClient,
}: AnalysisPanelViewProps) {
  const [state, dispatch] = useReducer(analysisStateReducer, {
    ...initialAnalysisState,
    projectId,
  })
  const [reason, setReason] = useState('')
  const [inputMessage, setInputMessage] = useState<string | null>(null)
  const sessionRef = useRef<AnalysisBridgeSession | null>(null)
  const stateRef = useRef<AnalysisState>(state)
  const requestPlayerSourceSeek = usePlayerSourceNavigation()
  stateRef.current = state

  useEffect(() => {
    const session = startAnalysisBridgeSession(createClient, dispatch)
    sessionRef.current = session
    dispatch({ type: 'sessionStarted' })
    if (projectId !== null) {
      try {
        session.send(createAnalysisReadRequest(createRequestId(), projectId))
      } catch {
        dispatch({ type: 'requestInvalid' })
      }
    }
    return () => {
      if (sessionRef.current === session) sessionRef.current = null
      session.dispose()
    }
  }, [createClient, projectId])

  const sendRead = useCallback(() => {
    if (projectId === null || stateRef.current.pending !== null) return
    try {
      send(
        sessionRef.current,
        createAnalysisReadRequest(createRequestId(), projectId),
        dispatch,
      )
    } catch {
      setInputMessage('案件 ID を解析要求として検証できませんでした。')
    }
  }, [projectId])

  const snapshot = state.snapshot
  const pending = state.pending !== null
  const reasonIsValid = isRequiredText(reason)

  const decideCandidate = (
    candidate: AnalysisCandidate,
    decision: 'adopt' | 'reject',
  ): void => {
    const currentSnapshot = stateRef.current.snapshot
    if (currentSnapshot === null || stateRef.current.pending !== null) return
    if (!reasonIsValid) {
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
      reason,
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

  const updateTopic = (topic: AnalysisTopic, update: AnalysisTopicUpdate): void => {
    const currentSnapshot = stateRef.current.snapshot
    if (currentSnapshot === null || stateRef.current.pending !== null) return
    if (!reasonIsValid) {
      setInputMessage('トピック変更の理由を入力してください。')
      return
    }
    setInputMessage(null)
    try {
      send(
        sessionRef.current,
        createAnalysisUpdateTopicRequest(createRequestId(), {
          projectId: currentSnapshot.projectId,
          revisionId: currentSnapshot.revisionId,
          digest: currentSnapshot.revisionDigest,
          topicId: topic.topicId,
          expectedTopicRevisionId: topic.metadataRevisionId,
          update,
          reason,
        }),
        dispatch,
      )
    } catch {
      setInputMessage('トピック入力を安全な単一項目の変更として検証できませんでした。')
    }
  }

  const navigateToRange = (range: AnalysisTimeRange): void => {
    const currentSnapshot = stateRef.current.snapshot
    if (currentSnapshot === null) return
    const outcome = requestPlayerSourceSeek({
      projectId: currentSnapshot.projectId,
      sourceTime: range.start,
    })
    dispatch({ type: 'navigationReported', message: navigationMessages[outcome] })
  }

  return (
    <section className="analysis-panel" aria-labelledby="analysis-title">
      <div className="analysis-heading">
        <div>
          <p className="eyebrow">Revision-bound analysis</p>
          <h2 id="analysis-title">解析結果</h2>
        </div>
        <div className="analysis-heading-actions">
          <p className="analysis-status" role="status" data-analysis-status={state.status}>
            {statusMessages[state.status]}
          </p>
          <button
            type="button"
            className="quiet-button"
            disabled={projectId === null || pending}
            onClick={sendRead}
          >
            最新版を読み込む
          </button>
        </div>
      </div>

      {projectId === null ? (
        <div className="analysis-empty">
          <p>確定済み案件を案件一覧から開いてください。</p>
        </div>
      ) : null}

      {snapshot === null ? null : (
        <div className="analysis-workspace">
          <div className="analysis-persistence-notice" role="note">
            <strong>作業中データ</strong>
            <span>ここでの採用・編集は自動保存されません。保存操作とは分離されています。</span>
          </div>

          <label className="analysis-reason">
            <span>判断・変更理由</span>
            <input
              type="text"
              value={reason}
              disabled={pending}
              onChange={(event) => setReason(event.currentTarget.value)}
              aria-describedby="analysis-reason-help"
            />
          </label>
          <p id="analysis-reason-help" className="field-help">
            候補の採否またはトピック1項目の変更に記録する理由です。
          </p>
          <p className="analysis-confidence-note">
            確度は解析元の正規値または「未提供」をそのまま表示し、画面側で補完しません。
          </p>

          {inputMessage === null ? null : (
            <p className="analysis-input-error" role="alert">{inputMessage}</p>
          )}
          {state.operationMessage === null ? null : (
            <p className="analysis-operation-status" role="status">{state.operationMessage}</p>
          )}
          {state.navigationMessage === null ? null : (
            <p className="analysis-navigation-status" role="status">
              {state.navigationMessage}
            </p>
          )}

          <div className="analysis-evidence-summary">
            <span>解析版 {snapshot.revisionId}</span>
            <span>文字起こし版 {snapshot.sourceRevisionId}</span>
            <time dateTime={snapshot.createdAt}>{snapshot.createdAt}</time>
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
                    disabled={pending}
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
                    key={topic.metadataRevisionId}
                    topic={topic}
                    speakers={snapshot.speakers}
                    disabled={pending}
                    onNavigate={() => navigateToRange(topic.range)}
                    onUpdate={(update) => updateTopic(topic, update)}
                  />
                ))}
              </div>
            )}
          </section>

          <CandidateSection
            candidates={snapshot.candidates}
            disabled={pending}
            onNavigate={(candidate) => navigateToRange(candidate.source.range)}
            onDecide={decideCandidate}
          />

          <SensitivitySection
            sensitivity={snapshot.sensitivity}
            disabled={pending}
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
  speakers,
  disabled,
  onNavigate,
  onUpdate,
}: Readonly<{
  topic: AnalysisTopic
  speakers: readonly AnalysisSpeaker[]
  disabled: boolean
  onNavigate: () => void
  onUpdate: (update: AnalysisTopicUpdate) => void
}>) {
  const [title, setTitle] = useState(topic.title)
  const [summary, setSummary] = useState(topic.summary ?? '')
  const [mainSpeakerId, setMainSpeakerId] = useState(topic.mainSpeaker?.speakerId ?? '')
  const [keywords, setKeywords] = useState(topic.keywords.join('\n'))
  const [importantStatements, setImportantStatements] = useState(
    topic.importantStatementSourceSpanIds.join('\n'),
  )
  const [audience, setAudience] = useState(topic.audience)
  const [publicationCautions, setPublicationCautions] = useState(
    topic.publicationCautions.join('\n'),
  )

  return (
    <article className="analysis-topic-card">
      <header className="analysis-card-heading">
        <div>
          <h4>{topic.title}</h4>
          <p>{formatRange(topic.range)}</p>
        </div>
        <div className="analysis-topic-actions">
          <span className="analysis-chip">変更元 {fieldLabel(topic.changedField)}</span>
          <button type="button" disabled={disabled} onClick={onNavigate}>
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
          label="タイトル"
          value={title}
          disabled={disabled}
          origin={topic.titleOrigin}
          onChange={setTitle}
          onSave={() => onUpdate({ field: 'title', text: title })}
        />
        <TopicTextAreaField
          label="要約"
          value={summary}
          disabled={disabled}
          origin={topic.summaryOrigin}
          onChange={setSummary}
          onSave={() => onUpdate({ field: 'summary', text: summary === '' ? null : summary })}
        />
        <form
          className="analysis-topic-field"
          onSubmit={(event) => {
            event.preventDefault()
            onUpdate({ field: 'mainSpeaker', speakerId: mainSpeakerId || null })
          }}
        >
          <label>
            <span>主話者</span>
            <select
              value={mainSpeakerId}
              disabled={disabled}
              onChange={(event) => setMainSpeakerId(event.currentTarget.value)}
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
          <button type="submit" disabled={disabled}>主話者を保存</button>
        </form>
        <TopicListField
          label="キーワード"
          value={keywords}
          disabled={disabled}
          origin={topic.keywordsOrigin}
          onChange={setKeywords}
          onSave={() => onUpdate({ field: 'keywords', values: splitEntries(keywords) })}
        />
        <TopicListField
          label="重要発言の元区間 ID"
          value={importantStatements}
          disabled={disabled}
          origin={topic.importantStatementsOrigin}
          onChange={setImportantStatements}
          onSave={() => onUpdate({
            field: 'importantStatements',
            sourceSpanIds: splitEntries(importantStatements),
          })}
        />
        <TopicTextField
          label="対象視聴者"
          value={audience}
          disabled={disabled}
          origin={topic.audienceOrigin}
          onChange={setAudience}
          onSave={() => onUpdate({ field: 'audience', text: audience })}
        />
        <TopicListField
          label="公開時の注意"
          value={publicationCautions}
          disabled={disabled}
          origin={topic.publicationCautionsOrigin}
          onChange={setPublicationCautions}
          onSave={() => onUpdate({
            field: 'publicationCautions',
            values: splitEntries(publicationCautions),
          })}
        />
      </div>
    </article>
  )
}

function TopicTextField({
  label,
  value,
  disabled,
  origin,
  onChange,
  onSave,
}: TopicFieldProps) {
  return (
    <form className="analysis-topic-field" onSubmit={(event) => submit(event, onSave)}>
      <label>
        <span>{label}</span>
        <input
          type="text"
          value={value}
          disabled={disabled}
          onChange={(event) => onChange(event.currentTarget.value)}
        />
      </label>
      <p className="analysis-field-origin">由来: {originLabel(origin)}</p>
      <button type="submit" disabled={disabled}>{label}を保存</button>
    </form>
  )
}

function TopicTextAreaField(props: TopicFieldProps) {
  return (
    <form className="analysis-topic-field" onSubmit={(event) => submit(event, props.onSave)}>
      <label>
        <span>{props.label}</span>
        <textarea
          value={props.value}
          disabled={props.disabled}
          onChange={(event) => props.onChange(event.currentTarget.value)}
        />
      </label>
      <p className="analysis-field-origin">由来: {originLabel(props.origin)}</p>
      <button type="submit" disabled={props.disabled}>{props.label}を保存</button>
    </form>
  )
}

function TopicListField(props: TopicFieldProps) {
  return (
    <form className="analysis-topic-field" onSubmit={(event) => submit(event, props.onSave)}>
      <label>
        <span>{props.label}（1行1件）</span>
        <textarea
          value={props.value}
          disabled={props.disabled}
          onChange={(event) => props.onChange(event.currentTarget.value)}
        />
      </label>
      <p className="analysis-field-origin">由来: {originLabel(props.origin)}</p>
      <button type="submit" disabled={props.disabled}>{props.label}を保存</button>
    </form>
  )
}

interface TopicFieldProps {
  readonly label: string
  readonly value: string
  readonly disabled: boolean
  readonly origin: AnalysisTopic['titleOrigin']
  readonly onChange: (value: string) => void
  readonly onSave: () => void
}

function CandidateSection({
  candidates,
  disabled,
  onNavigate,
  onDecide,
}: Readonly<{
  candidates: readonly AnalysisCandidate[]
  disabled: boolean
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
                    <button type="button" disabled={disabled} onClick={() => onNavigate(candidate)}>
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
                <span>{finding.reasonCode}</span>
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
          <h3 id="analysis-staged-title">未保存の変更</h3>
          <p>採用と編集の結果です。いずれも persisted = false です。</p>
        </div>
        <span className="analysis-chip is-warning">未永続化</span>
      </div>
      {changes.length === 0 ? (
        <p>この解析セッションに未保存の変更はありません。</p>
      ) : (
        <ul className="analysis-staged-list">
          {changes.map((change, index) => (
            <li key={stagedChangeKey(change, index)}>
              <strong>{stagedChangeLabel(change)}</strong>
              <span>未保存</span>
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

function submit(event: FormEvent<HTMLFormElement>, onSave: () => void): void {
  event.preventDefault()
  onSave()
}

function groupCandidates(candidates: readonly AnalysisCandidate[]) {
  return (['termCorrection', 'deleteCandidate', 'topicBoundary', 'highlight', 'technicalWarning'] as const)
    .map((kind) => ({ kind, items: candidates.filter((candidate) => candidate.displayKind === kind) }))
}

function splitEntries(value: string): readonly string[] {
  return [...new Set(value.split(/\r?\n/u).map((entry) => entry.trim()).filter(Boolean))]
}

function formatConfidence(value: number | null): string {
  return value === null ? '未提供' : value.toString()
}

function formatRange(range: AnalysisTimeRange): string {
  return `開始 ${formatRational(range.start)}・長さ ${formatRational(range.duration)}`
}

function formatRational(value: AnalysisTimeRange['start']): string {
  return `${value.value}/${value.rate}`
}

function yesNo(value: boolean): string {
  return value ? 'はい' : 'いいえ'
}

function originLabel(origin: AnalysisTopic['titleOrigin']): string {
  switch (origin) {
    case 'unset': return '未設定'
    case 'deterministicPlaceholder': return '決定論的な仮値'
    case 'projectSnapshot': return '案件 snapshot'
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
    case 'transcriptRevision': return '文字起こし子revision'
    case 'editOperation': return change.operation.kind === 'delete' ? '削除操作' : '分割操作'
    case 'cue': return '強調graphic cue'
    case 'topicMetadataRevision': return `トピックmetadata（${fieldLabel(change.field)}）`
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