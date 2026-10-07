import {
  createContext,
  useContext,
  useEffect,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
} from 'react'

import {
  isCanonicalDeliveryEntityId,
  isDeliveryOutputTime,
  type DeliveryApproval,
  type DeliveryPublicationScope,
  type DeliveryQcMeasuredValue,
  type DeliveryQcReviewArea,
  type DeliveryQcStream,
  type DeliveryQualityControl,
  type DeliveryRationalTime,
  type DeliveryReadStatus,
  type DeliveryReviewActorRole,
  type DeliveryReviewState,
  type DeliveryRightsStatus,
  type DeliverySnapshot,
  type DeliveryTrace,
} from './deliveryProtocol'
import {
  useDeliveryState,
  type DeliveryStateContextValue,
} from './DeliveryStateProvider'

const evidenceSectionKinds = [
  'identity',
  'artifacts',
  'review',
  'rights',
  'qualityControl',
  'trace',
] as const

type EvidenceSectionKind = (typeof evidenceSectionKinds)[number]

const evidenceSectionLabels: Readonly<Record<EvidenceSectionKind, string>> = {
  identity: '版と manifest',
  artifacts: '成果物 hash',
  review: 'レビュー・承認',
  rights: '権利・公開範囲',
  qualityControl: '品質確認',
  trace: '時刻トレース',
}

const publicationScopeLabels: Readonly<Record<DeliveryPublicationScope, string>> = {
  internalOnly: '内部限定',
  specificGroupOnly: '指定グループ限定',
  externalCandidate: '外部公開候補',
  externalPublicationApproved: '外部公開承認済み',
  publicationForbidden: '公開禁止',
  pendingDecision: '公開範囲の判断待ち',
}

const reviewStateLabels: Readonly<Record<DeliveryReviewState, string>> = {
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

const roleLabels: Readonly<Record<DeliveryReviewActorRole, string>> = {
  editor: '編集担当',
  performerReviewer: '出演者レビュー担当',
  contentApprover: '内容承認担当',
  brandApprover: 'ブランド承認担当',
  rightsApprover: '権利承認担当',
  administrator: '管理担当',
}

const rightsStatusLabels: Readonly<Record<DeliveryRightsStatus, string>> = {
  unconfirmed: '未確認',
  approved: '承認済み',
  rejected: '拒否',
}

const qcReviewAreaLabels: Readonly<Record<DeliveryQcReviewArea, string>> = {
  duration: '尺',
  audioVideoSynchronization: '音声・映像同期',
  subtitlePresentation: '字幕表示',
  screenLuma: '画面輝度',
  programLoudness: '番組ラウドネス',
  truePeak: 'トゥルーピーク',
}

const failureMessages: Readonly<
  Record<Exclude<DeliveryReadStatus, 'available'>, string>
> = {
  forbidden: '現在の Windows ユーザーには、この配信証跡を閲覧する権限がありません。',
  unavailable: '配信証跡の production data provider は利用できません。利用可能として補完していません。',
  incomplete: '必要な成果物または承認根拠が不足しています。部分的な証跡は表示しません。',
  mismatched: 'digest、manifest、版、権利、QC、またはトレースの結合が一致しません。',
  qualityControlFailed: '品質確認が失敗したため、配信証跡を利用可能として表示しません。',
}

export interface DeliveryPanelProps {
  readonly context?: Readonly<{ versionId: string | null }>
  readonly active?: boolean
}

export function DeliveryPanel(props: DeliveryPanelProps = {}) {
  return <DeliveryPanelView {...useDeliveryState()} {...props} />
}

export function DeliveryPanelView({
  state,
  changeVersionId,
  changeTraceValue,
  changeTraceRate,
  loadSnapshot,
  refreshSnapshot,
  lookupTrace,
  clearError,
  context,
  active = true,
}: DeliveryStateContextValue & DeliveryPanelProps) {
  const panelRef = useRef<HTMLElement>(null)
  const requestedVersionId = useRef<string | null>(null)
  const workflow = context !== undefined
  const contextVersionId =
    typeof context?.versionId === 'string' &&
    isCanonicalDeliveryEntityId(context.versionId)
      ? context.versionId
      : null
  const pending = state.pending !== null
  const canLoad = active && !pending && isCanonicalDeliveryEntityId(state.versionIdInput)
  // Both identities must match before even an old available snapshot is shown.
  const snapshot = workflow && (
    contextVersionId === null ||
    state.versionIdInput !== contextVersionId ||
    state.snapshot?.versionId !== contextVersionId ||
    state.status !== 'available' ||
    state.error !== null
  ) ? null : state.snapshot

  useEffect(() => {
    if (!active || contextVersionId === null || pending) {
      return
    }
    if (state.versionIdInput !== contextVersionId) {
      // Switching the provider input can discard its only cached snapshot, even
      // if this selection is superseded before its deferred read is sent.
      requestedVersionId.current = null
      changeVersionId(contextVersionId)
      // loadSnapshot closes over the provider's input. Wait for its next render.
      return
    }
    if (requestedVersionId.current === contextVersionId) {
      return
    }

    let superseded = false
    // The provider initializes its bridge after child effects on first mount.
    // Defer the read, and cancel it on a context/visibility change or StrictMode replay.
    void Promise.resolve().then(() => {
      if (superseded) {
        return
      }
      requestedVersionId.current = contextVersionId
      loadSnapshot()
    })
    return () => {
      superseded = true
    }
  }, [active, changeVersionId, contextVersionId, loadSnapshot, pending, state.versionIdInput])

  useEffect(() => {
    if (active) {
      return
    }
    const focused = panelRef.current?.ownerDocument.activeElement
    if (focused instanceof HTMLElement && panelRef.current?.contains(focused)) {
      focused.blur()
    }
  }, [active])

  const refresh = (): void => {
    if (!active || pending || (workflow && (
      contextVersionId === null || state.versionIdInput !== contextVersionId
    ))) {
      return
    }
    if (workflow && snapshot === null) {
      loadSnapshot()
    } else {
      refreshSnapshot()
    }
  }
  const workspace = snapshot === null ? null : (
    <TechnicalIdentifiersVisible.Provider value={!workflow}>
    <DeliveryWorkspace
      key={snapshot.versionId}
      snapshot={snapshot}
      trace={state.trace}
      traceValueInput={state.traceValueInput}
      traceRateInput={state.traceRateInput}
      pending={pending}
      active={active}
      changeTraceValue={changeTraceValue}
      changeTraceRate={changeTraceRate}
      lookupTrace={lookupTrace}
    />
    </TechnicalIdentifiersVisible.Provider>
  )

  return (
    <section
      ref={panelRef}
      className="delivery-panel"
      aria-labelledby="delivery-title"
      hidden={!active}
      inert={!active}
      onFocusCapture={(event) => {
        if (!active) {
          event.target.blur()
        }
      }}
    >
      <div className="delivery-heading">
        <div>
          <p className="eyebrow">{workflow ? '保存結果' : 'Validated delivery evidence'}</p>
          <h2 id="delivery-title">{workflow ? '成果物の保存' : '配信成果物と証跡'}</h2>
          {!workflow && (
            <p>
              構成版に固定された manifest、完全 digest、承認、権利、QC、時刻由来を読み取り専用で確認します。
            </p>
          )}
        </div>
        {snapshot !== null && (
          <span
            className={`delivery-scope-badge scope-${snapshot.publicationScope}`}
          >
            {publicationScopeLabels[snapshot.publicationScope]}
          </span>
        )}
      </div>

      {!workflow && (
        <form
          className="delivery-load-form"
          onSubmit={(event) => {
            event.preventDefault()
            if (canLoad) {
              loadSnapshot()
            }
          }}
        >
          <label htmlFor="delivery-version-id">構成版 ID</label>
          <div className="delivery-load-row">
            <input
              id="delivery-version-id"
              name="deliveryVersionId"
              type="text"
              autoComplete="off"
              spellCheck={false}
              minLength={36}
              maxLength={36}
              pattern="[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
              value={state.versionIdInput}
              disabled={pending || !active}
              aria-describedby="delivery-version-help"
              onChange={(event) => {
                if (active && !pending) {
                  changeVersionId(event.currentTarget.value)
                }
              }}
            />
            <button type="submit" disabled={!canLoad}>
              証跡を開く
            </button>
            {snapshot !== null && (
              <button
                className="secondary-button"
                type="button"
                disabled={pending || !active}
                onClick={refresh}
              >
                再読み込み
              </button>
            )}
          </div>
          <p id="delivery-version-help" className="field-help">
            小文字 UUID の構成版 ID を指定します。アクセス権と全 evidence binding はネイティブ側で再検証されます。
          </p>
        </form>
      )}

      {!workflow && state.error !== null && (
        <div className="delivery-error" role="alert">
          <div>
            <strong>配信証跡を確認できませんでした</strong>
            <p>{state.error.message}</p>
          </div>
          <button
            className="quiet-button"
            type="button"
            disabled={!active}
            onClick={() => {
              if (active) {
                clearError()
              }
            }}
          >
            閉じる
          </button>
        </div>
      )}

      <p
        className="delivery-operation-status"
        role="status"
        aria-live="polite"
        data-delivery-pending={pending && (!workflow || state.versionIdInput === contextVersionId) ? 'true' : 'false'}
      >
        {workflow
          ? workflowStatusMessage(contextVersionId, state, snapshot)
          : pending
            ? state.pending?.outputTime === null
              ? 'ネイティブ側で配信証跡を検証しています。'
              : '指定した出力時刻の由来を照合しています。'
            : statusMessage(state.status, state.lastCompletedOperation)}
      </p>

      {workflow && contextVersionId !== null && (
        <button
          className="secondary-button"
          type="button"
          disabled={pending || !active || state.versionIdInput !== contextVersionId}
          onClick={refresh}
        >
          再読み込み
        </button>
      )}

      {workflow ? snapshot !== null && (
        <>
          <ArtifactOverview snapshot={snapshot} />
          <p className="delivery-evidence-note" role="note">
            旧レビュー・承認・機械 QC は、新しい自動評価や人の確認の合格を示しません。V2 の自動昇格の完了とも別です。
          </p>
          <details
            key={`${snapshot.versionId}:${snapshot.deliveryManifestId}`}
            className="delivery-details"
          >
            <summary
              tabIndex={active ? undefined : -1}
              onClick={(event) => {
                if (!active) {
                  event.preventDefault()
                }
              }}
            >
              保存した動画の確認記録の詳細（承認・品質確認・元動画との対応）
            </summary>
            {workspace}
          </details>
        </>
      ) : state.status !== null && state.status !== 'available' ? (
        <div className="delivery-failure" role="alert" data-delivery-status={state.status}>
          <strong>証跡を利用可能として確認できません</strong>
          <p>{failureMessages[state.status]}</p>
        </div>
      ) : snapshot === null ? (
        <div className="delivery-empty">
          <strong>配信証跡は未選択です</strong>
          <p>構成版 ID を読み込むまで、成果物や evidence は表示されません。</p>
        </div>
      ) : (
        workspace
      )}
    </section>
  )
}

function workflowStatusMessage(
  versionId: string | null,
  state: DeliveryStateContextValue['state'],
  snapshot: DeliverySnapshot | null,
): string {
  if (versionId === null) {
    return '保存対象の版が選択されていません。'
  }
  if (state.versionIdInput !== versionId) {
    return state.snapshot === null
      ? '成果物の保存処理は未接続です'
      : '選択した版の保存状態は未取得です。'
  }
  if (state.pending !== null) {
    return state.pending.outputTime === null
      ? '保存証跡を確認しています。'
      : '指定した出力時刻の由来を照合しています。'
  }
  if (state.error !== null || state.status === null || state.status === 'unavailable') {
    return '成果物の保存処理は未接続です'
  }
  if (state.status !== 'available') {
    return failureMessages[state.status]
  }
  if (snapshot === null) {
    return state.snapshot === null
      ? '成果物の保存処理は未接続です'
      : '選択した版の保存証跡は確認できません。'
  }
  return '登録済みの保存証跡を確認しました。'
}

function ArtifactOverview({ snapshot }: Readonly<{ snapshot: DeliverySnapshot }>) {
  const counts = new Map<string, number>()
  for (const artifact of snapshot.artifacts) {
    counts.set(artifact.artifactType, (counts.get(artifact.artifactType) ?? 0) + 1)
  }
  return (
    <section className="delivery-card" aria-labelledby="delivery-overview-title">
      <h3 id="delivery-overview-title">登録された成果物（{snapshot.artifacts.length} 件）</h3>
      {snapshot.artifacts.length === 0 ? (
        <p>登録された成果物はありません。</p>
      ) : (
        <>
          <dl className="delivery-metadata">
            <PlainField label="生成状態" value="生成済み（登録成果物）" />
            <PlainField label="保存状態" value="ローカル保存済み（delivery 登録）" />
          </dl>
          <ul aria-label="保存済み成果物の種類と件数">
            {[...counts].map(([type, count]) => (
              <li key={type}>{type}: {count} 件</li>
            ))}
          </ul>
          <p className="delivery-evidence-note">
            レンダー成功の表示ではなく、delivery に登録されたローカル成果物の保存証跡です。
          </p>
        </>
      )}
    </section>
  )
}

export function getDeliveryEvidenceSectionAfterKey(
  currentIndex: number,
  key: string,
): number | null {
  if (
    !Number.isInteger(currentIndex) ||
    currentIndex < 0 ||
    currentIndex >= evidenceSectionKinds.length
  ) {
    return null
  }
  switch (key) {
    case 'ArrowLeft':
    case 'ArrowUp':
      return currentIndex === 0
        ? evidenceSectionKinds.length - 1
        : currentIndex - 1
    case 'ArrowRight':
    case 'ArrowDown':
      return currentIndex === evidenceSectionKinds.length - 1
        ? 0
        : currentIndex + 1
    case 'Home':
      return 0
    case 'End':
      return evidenceSectionKinds.length - 1
    default:
      return null
  }
}

function DeliveryWorkspace({
  snapshot,
  trace,
  traceValueInput,
  traceRateInput,
  pending,
  active,
  changeTraceValue,
  changeTraceRate,
  lookupTrace,
}: Readonly<{
  snapshot: DeliverySnapshot
  trace: DeliveryTrace | null
  traceValueInput: string
  traceRateInput: string
  pending: boolean
  active: boolean
  changeTraceValue: DeliveryStateContextValue['changeTraceValue']
  changeTraceRate: DeliveryStateContextValue['changeTraceRate']
  lookupTrace: DeliveryStateContextValue['lookupTrace']
}>) {
  const [selectedIndex, setSelectedIndex] = useState(0)
  const tabs = useRef<Array<HTMLButtonElement | null>>([])
  const onTabKeyDown = (
    event: KeyboardEvent<HTMLButtonElement>,
    index: number,
  ): void => {
    if (!active) {
      return
    }
    const nextIndex = getDeliveryEvidenceSectionAfterKey(index, event.key)
    if (nextIndex === null) {
      return
    }
    event.preventDefault()
    setSelectedIndex(nextIndex)
    tabs.current[nextIndex]?.focus()
  }

  return (
    <div className="delivery-workspace">
      <PublicationNotice snapshot={snapshot} />
      <div
        className="delivery-evidence-tabs"
        role="tablist"
        aria-label="配信証跡セクション"
      >
        {evidenceSectionKinds.map((kind, index) => (
          <button
            key={kind}
            id={`delivery-evidence-tab-${kind}`}
            role="tab"
            type="button"
            ref={(element) => {
              tabs.current[index] = element
            }}
            aria-selected={selectedIndex === index}
            aria-controls={`delivery-evidence-panel-${kind}`}
            disabled={!active}
            tabIndex={active && selectedIndex === index ? 0 : -1}
            onClick={() => {
              if (active) {
                setSelectedIndex(index)
              }
            }}
            onKeyDown={(event) => onTabKeyDown(event, index)}
          >
            {evidenceSectionLabels[kind]}
          </button>
        ))}
      </div>

      {evidenceSectionKinds.map((kind, index) => (
        <div
          key={kind}
          id={`delivery-evidence-panel-${kind}`}
          className="delivery-evidence-panel"
          role="tabpanel"
          aria-labelledby={`delivery-evidence-tab-${kind}`}
          hidden={selectedIndex !== index}
        >
          {kind === 'identity' ? (
            <IdentityEvidence snapshot={snapshot} />
          ) : kind === 'artifacts' ? (
            <ArtifactEvidence snapshot={snapshot} active={active} />
          ) : kind === 'review' ? (
            <ReviewEvidence snapshot={snapshot} />
          ) : kind === 'rights' ? (
            <RightsEvidence snapshot={snapshot} active={active} />
          ) : kind === 'qualityControl' ? (
            <QualityControlEvidence qualityControl={snapshot.qualityControl} />
          ) : (
            <TraceEvidence
              trace={trace}
              value={traceValueInput}
              rate={traceRateInput}
              pending={pending}
              active={active}
              changeValue={changeTraceValue}
              changeRate={changeTraceRate}
              lookup={lookupTrace}
            />
          )}
        </div>
      ))}
    </div>
  )
}

function PublicationNotice({ snapshot }: Readonly<{ snapshot: DeliverySnapshot }>) {
  const externallyApproved =
    snapshot.publicationScope === 'externalPublicationApproved'
  return (
    <div
      className={`delivery-publication-notice ${externallyApproved ? 'approved' : 'restricted'}`}
      role="note"
    >
      <strong>
        プロジェクト公開範囲: {publicationScopeLabels[snapshot.publicationScope]}
      </strong>
      <p>
        {externallyApproved
          ? 'この記録はプロジェクトの公開範囲を示します。各素材の権利根拠と人による最終確認は別に必要です。'
          : '外部公開の許可を示すものではありません。素材ごとの権利状態とも別の evidence です。'}
      </p>
      <p>
        機械 QC は承認済み数値閾値を適用しておらず、単独では配信可否を決定しません。
      </p>
    </div>
  )
}

function IdentityEvidence({ snapshot }: Readonly<{ snapshot: DeliverySnapshot }>) {
  if (!useContext(TechnicalIdentifiersVisible)) {
    return (
      <section className="delivery-card" aria-labelledby="delivery-identity-title">
        <h3 id="delivery-identity-title">保存した動画の記録</h3>
        <p>この動画は、作成したときの元動画・設定・確認の記録と照合済みです。</p>
      </section>
    )
  }
  return (
    <section className="delivery-card" aria-labelledby="delivery-identity-title">
      <p className="section-kicker">Pinned identity and digests</p>
      <h3 id="delivery-identity-title">版・manifest・evidence digest</h3>
      <dl className="delivery-metadata">
        <EvidenceField label="Delivery manifest ID" value={snapshot.deliveryManifestId} />
        <EvidenceField label="Project ID" value={snapshot.projectId} />
        <EvidenceField label="構成版 ID" value={snapshot.versionId} />
        <EvidenceField label="構成版 digest（SHA-256・完全値）" value={snapshot.versionDigest} wide />
        <EvidenceField label="Render manifest ID" value={snapshot.renderManifestId} />
        <EvidenceField label="Profile ID" value={snapshot.profileId} />
        <EvidenceField label="Profile digest（完全値）" value={snapshot.profileDigest} wide />
        <EvidenceField label="Delivery evidence digest（完全値）" value={snapshot.deliveryEvidenceDigest} wide />
        <EvidenceField label="Used SourceSpan digest（完全値）" value={snapshot.usedSourceSpansDigest} wide />
        <EvidenceField label="Approval evidence digest（完全値）" value={snapshot.approvalEvidenceDigest} wide />
      </dl>
    </section>
  )
}

function ArtifactEvidence({ snapshot, active }: Readonly<{ snapshot: DeliverySnapshot; active: boolean }>) {
  const technical = useContext(TechnicalIdentifiersVisible)
  return (
    <section className="delivery-card" aria-labelledby="delivery-artifacts-title">
      <div className="delivery-card-heading">
        <div>
          <p className="section-kicker">保存したファイル</p>
          <h3 id="delivery-artifacts-title">成果物 hash</h3>
        </div>
        <span>{snapshot.artifacts.length} 件</span>
      </div>
      <div className="delivery-table-scroll" tabIndex={active ? 0 : -1} aria-label="成果物証跡（横スクロール可能）">
        <table className="delivery-table">
          <caption className="visually-hidden">配信 manifest の成果物と完全 SHA-256</caption>
          <thead>
            <tr>
              {technical && <th scope="col">Artifact ID</th>}
              <th scope="col">種類</th>
              <th scope="col">Media type</th>
              <th scope="col">言語</th>
              <th scope="col">Bytes</th>
              {technical && <th scope="col">SHA-256（完全値）</th>}
            </tr>
          </thead>
          <tbody>
            {snapshot.artifacts.map((artifact) => (
              <tr key={artifact.artifactId}>
                {technical && <td><code>{artifact.artifactId}</code></td>}
                <td>{artifact.artifactType}</td>
                <td>{artifact.mediaType}</td>
                <td>{artifact.language ?? 'なし'}</td>
                <td>{formatInteger(artifact.byteSize)}</td>
                {technical && <td><code>{artifact.sha256}</code></td>}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function ReviewEvidence({ snapshot }: Readonly<{ snapshot: DeliverySnapshot }>) {
  return (
    <section className="delivery-card" aria-labelledby="delivery-review-title">
      <p className="section-kicker">確認の記録</p>
      <h3 id="delivery-review-title">レビュー・承認根拠</h3>
      <div className="delivery-review-grid">
        <div>
          <h4>レビュー状態記録（{snapshot.review.records.length} 件）</h4>
          {snapshot.review.records.length === 0 ? (
            <p className="delivery-card-empty">状態記録はありません。</p>
          ) : (
            <ol className="delivery-record-list">
              {snapshot.review.records.map((record) => (
                <li key={record.reviewRecordId}>
                  <strong>{reviewStateLabels[record.state]}</strong>
                  <span>{roleLabels[record.actorRole]}</span>
                  <time dateTime={record.recordedAt}>{formatUtc(record.recordedAt)}</time>
                  <TechnicalCode value={record.reviewRecordId} />
                </li>
              ))}
            </ol>
          )}
        </div>
        <div>
          <h4>承認記録（{snapshot.review.approvals.length} 件）</h4>
          <ol className="delivery-record-list">
            {snapshot.review.approvals.map((approval) => (
              <ApprovalRecord key={approval.approvalId} approval={approval} />
            ))}
          </ol>
        </div>
      </div>
      <p className="delivery-evidence-note">
        各記録は上部の構成版と approval evidence digest にネイティブ側で結合済みです。非公開の actor identity や理由は表示しません。
      </p>
    </section>
  )
}

function ApprovalRecord({ approval }: Readonly<{ approval: DeliveryApproval }>) {
  return (
    <li>
      <strong>{roleLabels[approval.actorRole]}</strong>
      <time dateTime={approval.approvedAt}>{formatUtc(approval.approvedAt)}</time>
      <TechnicalCode value={approval.approvalId} />
    </li>
  )
}

function RightsEvidence({ snapshot, active }: Readonly<{ snapshot: DeliverySnapshot; active: boolean }>) {
  const technical = useContext(TechnicalIdentifiersVisible)
  return (
    <section className="delivery-card" aria-labelledby="delivery-rights-title">
      <p className="section-kicker">素材の権利</p>
      <h3 id="delivery-rights-title">素材権利とプロジェクト公開範囲</h3>
      <p className="delivery-evidence-note">
        プロジェクト公開範囲は「{publicationScopeLabels[snapshot.publicationScope]}」です。これは各素材の外部公開許可とは別に評価します。
      </p>
      {snapshot.rights.length === 0 ? (
        <p className="delivery-card-empty">使用素材の権利証跡はありません。</p>
      ) : (
        <div className="delivery-table-scroll" tabIndex={active ? 0 : -1} aria-label="素材権利証跡（横スクロール可能）">
          <table className="delivery-table">
            <caption className="visually-hidden">使用素材ごとの権利証跡</caption>
            <thead>
              <tr>
                {technical && <th scope="col">Asset ID</th>}
                {technical && <th scope="col">Source digest</th>}
                <th scope="col">権利状態</th>
                <th scope="col">根拠記録</th>
                <th scope="col">許可用途</th>
                <th scope="col">外部公開</th>
              </tr>
            </thead>
            <tbody>
              {snapshot.rights.map((rights) => (
                <tr key={rights.assetId}>
                  {technical && <td><code>{rights.assetId}</code></td>}
                  {technical && <td><code>{rights.sourceDigest}</code></td>}
                  <td>{rightsStatusLabels[rights.status]}</td>
                  <td>{formatBoolean(rights.evidenceRecorded)}</td>
                  <td>{rights.allowedUse}</td>
                  <td>{formatBoolean(rights.externalPublicationAllowed)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

function QualityControlEvidence({
  qualityControl,
}: Readonly<{ qualityControl: DeliveryQualityControl }>) {
  return (
    <section className="delivery-card" aria-labelledby="delivery-qc-title">
      <p className="section-kicker">自動の品質確認</p>
      <h3 id="delivery-qc-title">品質確認 evidence と制約</h3>
      <div className="delivery-qc-warning" role="note">
        <strong>機械チェック通過は配信許可ではありません</strong>
        <p>
          人による確認が必要です。承認済み数値閾値は適用されておらず、この QC 単独では配信へ進めません。
        </p>
      </div>
      <dl className="delivery-metadata delivery-qc-summary">
        <EvidenceField label="QC artifact ID" value={qualityControl.artifactId} />
        <EvidenceField label="QC artifact SHA-256（完全値）" value={qualityControl.sha256} wide />
        <EvidenceField label="Artifact bytes" value={formatInteger(qualityControl.byteSize)} />
        <EvidenceField label="Probe evidence digest（完全値）" value={qualityControl.probeEvidenceDigest} wide />
        <BooleanField label="機械チェック" value={qualityControl.machineChecksPassed} />
        <BooleanField label="全 decode" value={qualityControl.decodedSuccessfully} />
        <BooleanField label="人による確認が必要" value={qualityControl.humanReviewRequired} />
        <BooleanField label="承認済み数値閾値を適用" value={qualityControl.approvedNumericThresholdsApplied} />
        <BooleanField label="QC 単独で配信へ進める" value={qualityControl.canProceedToDelivery} />
      </dl>

      <QcStream title="映像 timing" stream={qualityControl.videoTiming} />
      {qualityControl.audioTiming === null ? (
        <p className="delivery-card-empty">音声 timing evidence はありません。</p>
      ) : (
        <QcStream title="音声 timing" stream={qualityControl.audioTiming} />
      )}

      <div className="delivery-qc-grid">
        <section aria-labelledby="delivery-qc-subtitle-title">
          <h4 id="delivery-qc-subtitle-title">字幕 timing</h4>
          {qualityControl.subtitleTiming.streamIndex === null ? (
            <p>字幕 stream はありません。</p>
          ) : (
            <dl>
              <PlainField label="Stream index" value={String(qualityControl.subtitleTiming.streamIndex)} />
              <PlainField label="Cue count" value={String(qualityControl.subtitleTiming.observedCueCount)} />
              <EvidenceField label="Evidence digest" value={qualityControl.subtitleTiming.evidenceDigest!} />
            </dl>
          )}
        </section>
        <section aria-labelledby="delivery-qc-duration-title">
          <h4 id="delivery-qc-duration-title">尺・同期差分</h4>
          <dl>
            <PlainField label="Expected" value={formatRational(qualityControl.duration.expected)} />
            <PlainField label="Video delta" value={formatRational(qualityControl.duration.videoDelta)} />
            <PlainField label="Audio delta" value={formatNullableRational(qualityControl.duration.audioDelta)} />
            <PlainField label="A/V start offset" value={formatNullableRational(qualityControl.duration.audioVideoStartOffset)} />
            <PlainField label="A/V end offset" value={formatNullableRational(qualityControl.duration.audioVideoEndOffset)} />
          </dl>
        </section>
        <section aria-labelledby="delivery-qc-luma-title">
          <h4 id="delivery-qc-luma-title">画面輝度</h4>
          <dl>
            <PlainField label="Measured frames" value={String(qualityControl.luma.measuredFrameCount)} />
            <PlainField label="Minimum average" value={qualityControl.luma.minimumFrameAverage} />
            <PlainField label="Maximum average" value={qualityControl.luma.maximumFrameAverage} />
            <PlainField label="Mean average" value={qualityControl.luma.meanFrameAverage} />
            <EvidenceField label="Evidence digest" value={qualityControl.luma.evidenceDigest} />
          </dl>
        </section>
        <section aria-labelledby="delivery-qc-audio-title">
          <h4 id="delivery-qc-audio-title">音声測定</h4>
          {qualityControl.audio === null ? (
            <p>音声測定 evidence はありません。</p>
          ) : (
            <dl>
              <PlainField label="Integrated loudness LUFS" value={formatMeasuredValue(qualityControl.audio.integratedLoudnessLufs)} />
              <PlainField label="True peak dBFS" value={formatMeasuredValue(qualityControl.audio.truePeakDbfs)} />
              <EvidenceField label="Evidence digest" value={qualityControl.audio.evidenceDigest} />
            </dl>
          )}
        </section>
      </div>

      <section className="delivery-qc-tools" aria-labelledby="delivery-qc-tools-title">
        <h4 id="delivery-qc-tools-title">検証 tool</h4>
        <ol>
          {qualityControl.tools.map((tool) => (
            <li key={`${tool.name}-${tool.executableDigest}`}>
              <strong>{tool.name} {tool.version}</strong>
              <span>{formatInteger(tool.executableByteSize)} bytes</span>
              <TechnicalCode value={tool.executableDigest} />
            </li>
          ))}
        </ol>
      </section>
      <section className="delivery-human-review" aria-labelledby="delivery-human-review-title">
        <h4 id="delivery-human-review-title">必要な人による確認</h4>
        <ul>
          {qualityControl.requiredHumanReviewAreas.map((area) => (
            <li key={area}>{qcReviewAreaLabels[area]}</li>
          ))}
        </ul>
      </section>
    </section>
  )
}

function QcStream({ title, stream }: Readonly<{ title: string; stream: DeliveryQcStream }>) {
  return (
    <section className="delivery-qc-stream">
      <h4>{title}</h4>
      <dl className="delivery-metadata">
        <PlainField label="Stream index" value={String(stream.streamIndex)} />
        <PlainField label="Observed count" value={String(stream.observedCount)} />
        <PlainField label="Presentation start" value={formatRational(stream.presentationStart)} />
        <PlainField label="Presentation end" value={formatRational(stream.presentationEnd)} />
        <PlainField label="Observed duration" value={formatRational(stream.observedDuration)} />
        <EvidenceField label="Evidence digest（完全値）" value={stream.evidenceDigest} wide />
      </dl>
    </section>
  )
}

function TraceEvidence({
  trace,
  value,
  rate,
  pending,
  active,
  changeValue,
  changeRate,
  lookup,
}: Readonly<{
  trace: DeliveryTrace | null
  value: string
  rate: string
  pending: boolean
  active: boolean
  changeValue: (value: string) => void
  changeRate: (value: string) => void
  lookup: () => void
}>) {
  const outputTime = { value, rate }
  const valid = isDeliveryOutputTime(outputTime)
  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    if (active && !pending && valid) {
      lookup()
    }
  }
  return (
    <section className="delivery-card" aria-labelledby="delivery-trace-title">
      <p className="section-kicker">元動画との対応</p>
      <h3 id="delivery-trace-title">任意の出力時刻から由来を照合</h3>
      <form className="delivery-trace-form" onSubmit={submit}>
        <fieldset disabled={pending || !active}>
          <legend>約分済み rational time</legend>
          <div className="rational-time-fields">
            <div className="field-group">
              <label htmlFor="delivery-trace-value">出力時刻 value</label>
              <input
                id="delivery-trace-value"
                type="text"
                inputMode="numeric"
                pattern="(?:0|[1-9][0-9]{0,18})"
                value={value}
                aria-describedby="delivery-trace-help"
                onChange={(event) => {
                  if (active && !pending) {
                    changeValue(event.currentTarget.value)
                  }
                }}
              />
            </div>
            <div className="field-group">
              <label htmlFor="delivery-trace-rate">出力時刻 rate</label>
              <input
                id="delivery-trace-rate"
                type="text"
                inputMode="numeric"
                pattern="[1-9][0-9]{0,18}"
                value={rate}
                aria-describedby="delivery-trace-help"
                onChange={(event) => {
                  if (active && !pending) {
                    changeRate(event.currentTarget.value)
                  }
                }}
              />
            </div>
          </div>
          <p id="delivery-trace-help" className="field-help">
            0 以上の value と正の rate を、約分済み signed 64-bit 整数で指定します。小数へ丸めません。
          </p>
          <button className="primary-button" type="submit" disabled={!valid || pending || !active}>
            出力時刻を照合
          </button>
        </fieldset>
      </form>
      {trace === null ? (
        <p className="delivery-card-empty">時刻トレースはまだ実行されていません。</p>
      ) : trace.status === 'notFound' ? (
        <div className="delivery-trace-not-found" role="status">
          <strong>対応する由来はありません</strong>
          <p>
            出力時刻 {formatRational(trace.outputTime)} は timeline の半開区間に含まれません。由来を推測していません。
          </p>
        </div>
      ) : (
        <TraceResult trace={trace} />
      )}
    </section>
  )
}

function TraceResult({ trace }: Readonly<{ trace: Exclude<DeliveryTrace, { status: 'notFound' }> }>) {
  return (
    <div className="delivery-trace-result" role="status">
      <strong>由来を解決しました</strong>
      <dl className="delivery-metadata">
        <PlainField label="Output time" value={formatRational(trace.outputTime)} />
        <PlainField label="Origin kind" value={trace.originKind} />
        <EvidenceField label="Origin ID" value={trace.originId} />
        <PlainField label="Source time" value={formatNullableRational(trace.sourceTime)} />
        {trace.sourceSpan !== null && (
          <>
            <EvidenceField label="SourceSpan ID" value={trace.sourceSpan.sourceSpanId} />
            <EvidenceField label="Asset ID" value={trace.sourceSpan.assetId} />
            <PlainField label="Stream ID" value={trace.sourceSpan.streamId} />
            <PlainField label="Source range" value={`[${formatRational(trace.sourceSpan.start)}, ${formatRational(trace.sourceSpan.end)})`} />
            <EvidenceField label="Source digest（完全値）" value={trace.sourceSpan.sourceDigest} wide />
          </>
        )}
      </dl>
    </div>
  )
}

// FR-052: inside the workflow (仕上がり) IDs and digests are not shown; the standalone evidence view keeps them.
const TechnicalIdentifiersVisible = createContext(true)

function EvidenceField({
  label,
  value,
  wide = false,
}: Readonly<{ label: string; value: string; wide?: boolean }>) {
  if (!useContext(TechnicalIdentifiersVisible)) {
    return null
  }
  return (
    <div className={wide ? 'wide' : undefined}>
      <dt>{label}</dt>
      <dd><code>{value}</code></dd>
    </div>
  )
}

function TechnicalCode({ value }: Readonly<{ value: string }>) {
  return useContext(TechnicalIdentifiersVisible) ? <code>{value}</code> : null
}

function PlainField({ label, value }: Readonly<{ label: string; value: string }>) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  )
}

function BooleanField({ label, value }: Readonly<{ label: string; value: boolean }>) {
  return <PlainField label={label} value={formatBoolean(value)} />
}

function statusMessage(
  status: DeliveryReadStatus | null,
  operation: DeliveryStateContextValue['state']['lastCompletedOperation'],
): string {
  if (status === null) {
    return '構成版を指定して配信証跡を読み込んでください。'
  }
  if (status !== 'available') {
    return 'ネイティブ側の検証は閉じた失敗として完了しました。'
  }
  return operation === 'trace'
    ? '配信証跡と指定時刻の由来を検証しました。'
    : '配信証跡を検証しました。'
}

function formatRational(value: DeliveryRationalTime): string {
  return `${value.value}/${value.rate}`
}

function formatNullableRational(value: DeliveryRationalTime | null): string {
  return value === null ? 'なし' : formatRational(value)
}

function formatMeasuredValue(value: DeliveryQcMeasuredValue): string {
  if (value.kind === 'finite') {
    return value.value
  }
  return value.kind === 'negativeInfinity' ? '−∞' : '+∞'
}

function formatBoolean(value: boolean): string {
  return value ? 'はい' : 'いいえ'
}

function formatInteger(value: string): string {
  return BigInt(value).toLocaleString('ja-JP')
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
