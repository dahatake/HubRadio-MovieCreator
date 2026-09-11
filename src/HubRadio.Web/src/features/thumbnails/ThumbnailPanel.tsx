import {
  useEffect,
  useReducer,
  useRef,
  useState,
  type ChangeEvent,
} from 'react'

import { createHostBridgeClient, type BridgeClient } from '../../bridge'
import {
  formatRationalTime,
  type PlayerExactFrameSelection,
} from '../player'
import type {
  ThumbnailCancelResponse,
  ThumbnailGenerateParams,
  ThumbnailGenerateResponse,
  ThumbnailReadResponse,
  ThumbnailSnapshot,
  ThumbnailTemplateSnapshot,
} from './thumbnailProtocol'
import {
  expectationMatchesCurrent,
  initialThumbnailState,
  selectionMatchesSnapshot,
  selectThumbnailOutput,
  selectThumbnailTemplate,
  thumbnailStateReducer,
  type ThumbnailGenerationExpectation,
  type ThumbnailStatus,
} from './thumbnailState'

const statusMessages: Readonly<Record<ThumbnailStatus, string>> = {
  idle: 'プレイヤーで正確なフレームを選択してください',
  loading: 'サムネイル用の信頼済み根拠を確認しています',
  ready: 'ソース・権利・承認済みテンプレートの根拠を確認しました',
  generating: '選択したソースフレームから PNG を生成しています',
  cancelling: '生成のキャンセルを要求しています',
  generated: '検証済み PNG を生成しました',
  forbidden: 'このサムネイル操作を実行する権限がありません',
  unavailable: 'サムネイル生成に必要な信頼済み根拠を取得できません',
  stale: 'ソースまたは承認済みテンプレートの根拠が更新されました',
  mismatched: 'プレイヤーとサムネイルの根拠が一致しません',
  invalidTime: '選択したフレームの正確なソース PTS を確認できません',
  rightsNotApproved: 'ソースの外部公開権利が承認されていません',
  templateUnavailable: '選択した承認済みテンプレートは利用できません',
  cancelled: 'サムネイル生成をキャンセルしました',
  conflict: '同じ生成 ID が異なる選択へ使用されました',
  failed: 'サムネイル生成に失敗しました',
  bridgeUnavailable: 'ネイティブホストへ接続できません',
  bridgeSendFailed: 'ネイティブホストへ要求を送信できません。再送できます',
}

const cancelMessages = {
  requested: 'キャンセル要求を送信しました',
  alreadyRequested: 'キャンセルはすでに要求済みです',
  notRunning: '生成処理はすでに完了しています',
  notFound: '対象の生成処理は見つかりませんでした',
  forbidden: 'キャンセル権限がありません',
  unavailable: 'キャンセル要求を処理できません',
  sendFailed: 'キャンセル要求を送信できませんでした',
} as const

export type ThumbnailBridgeClient = Pick<
  BridgeClient,
  | 'sendThumbnailRead'
  | 'onThumbnailReadResponse'
  | 'sendThumbnailGenerate'
  | 'onThumbnailGenerateResponse'
  | 'sendThumbnailCancellation'
  | 'onThumbnailCancelResponse'
  | 'dispose'
>

export interface ThumbnailPanelProps {
  readonly selection: PlayerExactFrameSelection | null
  readonly active?: boolean
  readonly context?: Readonly<{ projectId: string | null }>
  readonly createClient?: () => ThumbnailBridgeClient
  readonly createId?: () => string
  readonly previewReduced?: boolean
}

export function ThumbnailPanel({
  selection: playerSelection,
  active = true,
  context,
  createClient = createHostBridgeClient,
  createId = createCanonicalId,
  previewReduced = false,
}: ThumbnailPanelProps) {
  const [state, dispatch] = useReducer(
    thumbnailStateReducer,
    initialThumbnailState,
  )
  const [previewFailed, setPreviewFailed] = useState(false)
  const clientRef = useRef<ThumbnailBridgeClient | null>(null)
  const createClientRef = useRef(createClient)
  createClientRef.current = createClient
  const selection =
    context === undefined || playerSelection?.projectId === context.projectId
      ? playerSelection
      : null

  useEffect(() => {
    let client: ThumbnailBridgeClient
    try {
      client = createClientRef.current()
    } catch {
      dispatch({ type: 'bridgeUnavailable' })
      return
    }

    let unsubscribeRead: (() => void) | null = null
    let unsubscribeGenerate: (() => void) | null = null
    let unsubscribeCancel: (() => void) | null = null
    try {
      unsubscribeRead = client.onThumbnailReadResponse(
        (response: ThumbnailReadResponse) => {
          dispatch({ type: 'readReceived', response })
        },
      )
      unsubscribeGenerate = client.onThumbnailGenerateResponse(
        (response: ThumbnailGenerateResponse) => {
          dispatch({ type: 'generationReceived', response })
        },
      )
      unsubscribeCancel = client.onThumbnailCancelResponse(
        (response: ThumbnailCancelResponse) => {
          dispatch({ type: 'cancelReceived', response })
        },
      )
    } catch {
      unsubscribeRead?.()
      unsubscribeGenerate?.()
      unsubscribeCancel?.()
      client.dispose()
      dispatch({ type: 'bridgeUnavailable' })
      return
    }

    clientRef.current = client
    return () => {
      if (clientRef.current === client) {
        clientRef.current = null
      }
      try {
        unsubscribeRead?.()
        unsubscribeGenerate?.()
        unsubscribeCancel?.()
      } finally {
        client.dispose()
      }
    }
  }, [])

  useEffect(() => {
    setPreviewFailed(false)
  }, [state.output?.contentDigest])

  const template = selectThumbnailTemplate(state)
  const bindingMatches = selectionMatchesSnapshot(selection, state.snapshot)
  const output = selectThumbnailOutput(state, selection)
  const displayStatus =
    state.snapshot !== null &&
    (!bindingMatches || (state.output !== null && output === null))
      ? selection === null ? 'idle' : 'mismatched'
      : state.status
  const rightsApproved =
    state.snapshot?.rights.status === 'approved' &&
    state.snapshot.rights.externalPublicationAllowed
  const busy =
    state.pendingRead !== null || state.pendingGeneration !== null
  const canRead = active && selection !== null && !busy
  // The legacy API has no authoritative internal-consumer scope policy.
  // Workflow may read its evidence, but must not use it for internal generation.
  const canGenerate =
    active &&
    context === undefined &&
    selection !== null &&
    state.snapshot !== null &&
    template !== null &&
    bindingMatches &&
    rightsApproved &&
    state.pendingGeneration === null
  const canCancel =
    active &&
    context === undefined &&
    state.pendingGeneration !== null && state.pendingCancel === null
  const retryingSameGeneration =
    selection !== null &&
    state.snapshot !== null &&
    template !== null &&
    expectationMatchesCurrent(
      state.retryGeneration,
      selection,
      state.snapshot,
      template,
    )

  const readCurrentEvidence = (): void => {
    if (selection === null || !canRead) {
      return
    }
    const requestId = createId()
    dispatch({
      type: 'readRequested',
      requestId,
      binding: Object.freeze({
        jobId: selection.jobId,
        projectId: selection.projectId,
        sourceAssetId: selection.sourceAssetId,
        playerEvidenceDigest: selection.playerEvidenceDigest,
      }),
    })
    const client = clientRef.current
    if (client === null) {
      dispatch({ type: 'bridgeUnavailable' })
      return
    }
    try {
      client.sendThumbnailRead(requestId, selection.jobId)
    } catch {
      dispatch({ type: 'readSendFailed', requestId })
    }
  }

  const generate = (): void => {
    if (
      !canGenerate ||
      selection === null ||
      state.snapshot === null ||
      template === null
    ) {
      return
    }

    const generationId = retryingSameGeneration
      ? state.retryGeneration!.generationId
      : createId()
    const requestId = createId()
    const expectation: ThumbnailGenerationExpectation = Object.freeze({
      jobId: selection.jobId,
      generationId,
      snapshot: state.snapshot,
      template,
      frame: selection.frame,
    })
    const params: ThumbnailGenerateParams = Object.freeze({
      jobId: selection.jobId,
      generationId,
      digest: state.snapshot.evidenceDigest,
      playerDigest: selection.playerEvidenceDigest,
      templateId: template.templateId,
      proxyFrameSequence: selection.frame.proxyFrameSequence,
    })
    dispatch({ type: 'generationRequested', requestId, expectation })
    const client = clientRef.current
    if (client === null) {
      dispatch({ type: 'bridgeUnavailable' })
      return
    }
    try {
      client.sendThumbnailGenerate(requestId, params)
    } catch {
      dispatch({ type: 'generationSendFailed', requestId })
    }
  }

  const cancel = (): void => {
    const generationId = state.pendingGeneration?.generationId
    if (generationId === undefined || !canCancel) {
      return
    }
    const requestId = createId()
    dispatch({ type: 'cancelRequested', requestId, generationId })
    const client = clientRef.current
    if (client === null) {
      dispatch({ type: 'cancelSendFailed', requestId })
      return
    }
    try {
      client.sendThumbnailCancellation(requestId, generationId)
    } catch {
      dispatch({ type: 'cancelSendFailed', requestId })
    }
  }

  const onTemplateChange = (event: ChangeEvent<HTMLInputElement>): void => {
    if (!active || context !== undefined) return
    dispatch({ type: 'templateSelected', templateId: event.currentTarget.value })
  }

  if (context !== undefined) {
    return (
      <section
        className="thumbnail-card"
        aria-labelledby="thumbnail-title"
        hidden={!active}
        inert={!active}
        data-thumbnail-preview={previewReduced ? 'reduced' : 'fit'}
      >
        <div className="thumbnail-heading">
          <h2 id="thumbnail-title">サムネイル</h2>
          <p
            className="thumbnail-status"
            role="status"
            data-thumbnail-status="unavailable"
          >
            {selection === null
              ? 'サムネイルを表示する元フレームは未取得です'
              : '社内向けサムネイル生成は未接続です'}
          </p>
        </div>
        {selection === null ? null : (
          <div className="thumbnail-body">
            <section className="thumbnail-step" aria-labelledby="thumbnail-frame-title">
              <div className="thumbnail-step-heading">
                <h3 id="thumbnail-frame-title">プレイヤーの選択フレーム</h3>
                <button
                  type="button"
                  className="secondary-button"
                  disabled={!canRead}
                  onClick={readCurrentEvidence}
                >
                  信頼済み根拠を読み込む
                </button>
              </div>
              <FrameEvidence selection={selection} />
              <p className="field-help">
                既存 API の根拠は参照専用です。社内向けの生成可否は含まれません。
              </p>
              {bindingMatches && state.snapshot !== null ? (
                <details>
                  <summary>既存の外部公開用 API の根拠（参照のみ）</summary>
                  <SnapshotEvidence snapshot={state.snapshot} />
                  <ul>
                    {state.snapshot.templates.map((candidate) => (
                      <li key={candidate.templateId}>
                        {candidate.name} · {candidate.width} × {candidate.height} PNG
                      </li>
                    ))}
                  </ul>
                </details>
              ) : (
                <p className="thumbnail-empty">選択フレームの根拠は未取得です。</p>
              )}
            </section>
          </div>
        )}
      </section>
    )
  }

  return (
    <section
      className="thumbnail-card"
      aria-labelledby="thumbnail-title"
      hidden={!active}
      inert={!active}
      data-thumbnail-preview={previewReduced ? 'reduced' : 'fit'}
    >
      <div className="thumbnail-heading">
        <div>
          <p className="eyebrow">Exact frame · approved template</p>
          <h2 id="thumbnail-title">手動サムネイル生成</h2>
        </div>
        <p
          className="thumbnail-status"
          role="status"
          data-thumbnail-status={displayStatus}
        >
          {statusMessages[displayStatus]}
        </p>
      </div>

      <div className="thumbnail-body">
        <section className="thumbnail-step" aria-labelledby="thumbnail-frame-title">
          <div className="thumbnail-step-heading">
            <div>
              <p className="step-number">1</p>
              <h3 id="thumbnail-frame-title">プレイヤーの選択フレーム</h3>
            </div>
            <button
              type="button"
              className="secondary-button"
              disabled={!canRead}
              onClick={readCurrentEvidence}
            >
              信頼済み根拠を読み込む
            </button>
          </div>

          {selection === null ? (
            <p className="thumbnail-empty">
              上の代理動画プレイヤーを開き、使用するフレームを選択します。
            </p>
          ) : (
            <>
              <FrameEvidence selection={selection} />
              <p className="field-help">
                時刻は整数の value/rate のまま保持され、生成は Source frame
                sequence を使用します。
              </p>
            </>
          )}

          {state.snapshot !== null && !bindingMatches ? (
            <p className="thumbnail-warning" role="alert">
              現在のプレイヤー選択は読み込み済み根拠と一致しません。根拠を再読込してください。
            </p>
          ) : null}
        </section>

        {state.snapshot === null ? null : (
          <>
            <section className="thumbnail-step" aria-labelledby="thumbnail-rights-title">
              <div className="thumbnail-step-heading">
                <div>
                  <p className="step-number">2</p>
                  <h3 id="thumbnail-rights-title">ソース権利と binding</h3>
                </div>
                <span
                  className="thumbnail-badge"
                  data-approved={rightsApproved ? 'true' : 'false'}
                >
                  {rightsApproved ? '外部公開可' : '生成不可'}
                </span>
              </div>
              <details>
                <summary>根拠の詳細</summary>
                <SnapshotEvidence snapshot={state.snapshot} />
              </details>
              {!rightsApproved ? (
                <p className="thumbnail-warning" role="alert">
                  権利状態が approved かつ外部公開許可の場合にのみ生成できます。
                </p>
              ) : null}
            </section>

            <section className="thumbnail-step" aria-labelledby="thumbnail-template-title">
              <div className="thumbnail-step-heading">
                <div>
                  <p className="step-number">3</p>
                  <h3 id="thumbnail-template-title">承認済みテンプレート</h3>
                </div>
              </div>
              {state.snapshot.templates.length === 0 ? (
                <p className="thumbnail-warning" role="alert">
                  native host から検証可能な BrandApprover 承認情報が提供されていません。
                </p>
              ) : (
                <fieldset
                  className="thumbnail-template-list"
                  disabled={!active || state.pendingGeneration !== null}
                >
                  <legend className="visually-hidden">承認済みテンプレートを選択</legend>
                  {state.snapshot.templates.map((candidate) => (
                    <TemplateOption
                      key={candidate.templateId}
                      template={candidate}
                      checked={state.selectedTemplateId === candidate.templateId}
                      onChange={onTemplateChange}
                    />
                  ))}
                </fieldset>
              )}
            </section>

            <section className="thumbnail-step" aria-labelledby="thumbnail-generate-title">
              <div className="thumbnail-step-heading">
                <div>
                  <p className="step-number">4</p>
                  <h3 id="thumbnail-generate-title">検証済み PNG</h3>
                </div>
                <div className="thumbnail-actions">
                  <button
                    type="button"
                    className="primary-button"
                    disabled={!canGenerate}
                    onClick={generate}
                  >
                    {retryingSameGeneration
                      ? '同じ生成 ID で再送'
                      : '選択フレームから生成'}
                  </button>
                  <button
                    type="button"
                    className="button-danger"
                    disabled={!canCancel}
                    onClick={cancel}
                  >
                    生成をキャンセル
                  </button>
                </div>
              </div>

              {state.cancelStatus === null ? null : (
                <p className="thumbnail-cancel-status" role="status">
                  {cancelMessages[state.cancelStatus]}
                </p>
              )}

              {output === null ? (
                <p className="thumbnail-empty">
                  出力は exact source PTS と承認済み template binding の検証後に表示されます。
                </p>
              ) : (
                <div className="thumbnail-output">
                  <figure className="thumbnail-preview">
                    {previewFailed ? (
                      <p role="alert">生成済み PNG を読み込めません。</p>
                    ) : (
                      <img
                        src={output.mediaReference}
                        width={output.width}
                        height={output.height}
                        alt={`${template?.name ?? '承認済みテンプレート'}、Source PTS ${formatRationalTime(output.selection.sourcePresentationTime)} の生成済みサムネイル`}
                        onError={() => setPreviewFailed(true)}
                      />
                    )}
                    <figcaption>
                      {output.width} × {output.height} PNG ·{' '}
                      {output.byteSize} bytes
                      {previewReduced ? '・縮小表示（拡大なし）' : ''}
                    </figcaption>
                  </figure>
                  <details>
                    <summary>生成結果の詳細</summary>
                    <dl className="thumbnail-output-evidence">
                      <Evidence label="output SHA-256" value={output.contentDigest} code />
                      <Evidence label="binding digest" value={output.bindingDigest} code />
                      <Evidence label="artifact ID" value={output.artifactId} code />
                      <Evidence
                        label="exact source PTS"
                        value={formatRationalTime(output.selection.sourcePresentationTime)}
                        code
                      />
                      <Evidence
                        label="source frame"
                        value={String(output.selection.sourceFrameSequence)}
                      />
                      <Evidence
                        label="proxy frame"
                        value={String(output.selection.proxyFrameSequence)}
                      />
                      <Evidence label="template approval" value={output.template.approvalDigest} code />
                      <Evidence label="作成日時 (UTC)" value={output.createdAt} code />
                    </dl>
                  </details>
                </div>
              )}
            </section>
          </>
        )}
      </div>
    </section>
  )
}

function FrameEvidence({
  selection,
}: Readonly<{ selection: PlayerExactFrameSelection }>) {
  return (
    <dl className="thumbnail-time-grid">
      <TimeEvidence
        label="Source PTS"
        value={formatRationalTime(selection.frame.sourcePresentationTime)}
        sequence={selection.frame.sourceFrameSequence}
      />
      <TimeEvidence
        label="Proxy PTS"
        value={formatRationalTime(selection.frame.proxyPresentationTime)}
        sequence={selection.frame.proxyFrameSequence}
      />
      <TimeEvidence
        label="Output PTS"
        value={formatRationalTime(selection.frame.outputPresentationTime)}
        sequence={null}
      />
    </dl>
  )
}

function SnapshotEvidence({
  snapshot,
}: Readonly<{ snapshot: ThumbnailSnapshot }>) {
  return (
    <dl className="thumbnail-evidence-list">
      <Evidence label="権利状態" value={snapshot.rights.status} />
      <Evidence
        label="外部公開"
        value={snapshot.rights.externalPublicationAllowed ? '許可' : '未許可'}
      />
      <Evidence label="権利 digest" value={snapshot.sourceRightsDigest} code />
      <Evidence label="ソース digest" value={snapshot.sourceContentDigest} code />
      <Evidence label="PTS index digest" value={snapshot.sourcePtsIndexDigest} code />
      <Evidence label="frame map digest" value={snapshot.proxyFrameMapDigest} code />
      <Evidence label="player evidence" value={snapshot.playerEvidenceDigest} code />
      <Evidence label="thumbnail evidence" value={snapshot.evidenceDigest} code />
    </dl>
  )
}

function TimeEvidence({
  label,
  value,
  sequence,
}: Readonly<{ label: string; value: string; sequence: number | null }>) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>
        <code>{value}</code>
        {sequence === null ? null : <span>frame {sequence}</span>}
      </dd>
    </div>
  )
}

function Evidence({
  label,
  value,
  code = false,
}: Readonly<{ label: string; value: string; code?: boolean }>) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{code ? <code>{value}</code> : value}</dd>
    </div>
  )
}

function TemplateOption({
  template,
  checked,
  onChange,
}: Readonly<{
  template: ThumbnailTemplateSnapshot
  checked: boolean
  onChange: (event: ChangeEvent<HTMLInputElement>) => void
}>) {
  return (
    <div>
      <label className="thumbnail-template-option">
        <input
          type="radio"
          name="thumbnail-template"
          value={template.templateId}
          checked={checked}
          onChange={onChange}
        />
        <span className="thumbnail-template-copy">
          <strong>{template.name}</strong>
          <span>{template.width} × {template.height} PNG</span>
          <span>BrandApprover · {template.approvedAt}</span>
        </span>
      </label>
      <details>
        <summary>テンプレートの詳細</summary>
        <div className="thumbnail-template-copy">
          <code>approval {template.approvalDigest}</code>
          <code>template {template.templateVersionDigest}</code>
          <code>profile {template.renderProfileDigest}</code>
          <code>layout {template.layoutSpecDigest}</code>
        </div>
      </details>
    </div>
  )
}

function createCanonicalId(): string {
  return window.crypto.randomUUID().toLowerCase()
}
