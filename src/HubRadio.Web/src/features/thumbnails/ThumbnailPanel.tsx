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
  ThumbnailTemplateSnapshot,
} from './thumbnailProtocol'
import {
  expectationMatchesCurrent,
  initialThumbnailState,
  selectionMatchesSnapshot,
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
  readonly createClient?: () => ThumbnailBridgeClient
  readonly createId?: () => string
}

export function ThumbnailPanel({
  selection,
  createClient = createHostBridgeClient,
  createId = createCanonicalId,
}: ThumbnailPanelProps) {
  const [state, dispatch] = useReducer(
    thumbnailStateReducer,
    initialThumbnailState,
  )
  const [previewFailed, setPreviewFailed] = useState(false)
  const clientRef = useRef<ThumbnailBridgeClient | null>(null)

  useEffect(() => {
    let client: ThumbnailBridgeClient
    try {
      client = createClient()
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
  }, [createClient])

  useEffect(() => {
    setPreviewFailed(false)
  }, [state.output?.contentDigest])

  const template = selectThumbnailTemplate(state)
  const bindingMatches = selectionMatchesSnapshot(selection, state.snapshot)
  const rightsApproved =
    state.snapshot?.rights.status === 'approved' &&
    state.snapshot.rights.externalPublicationAllowed
  const busy =
    state.pendingRead !== null || state.pendingGeneration !== null
  const canRead = selection !== null && !busy
  const canGenerate =
    selection !== null &&
    state.snapshot !== null &&
    template !== null &&
    bindingMatches &&
    rightsApproved &&
    state.pendingGeneration === null
  const canCancel =
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
    dispatch({ type: 'templateSelected', templateId: event.currentTarget.value })
  }

  return (
    <section className="thumbnail-card" aria-labelledby="thumbnail-title">
      <div className="thumbnail-heading">
        <div>
          <p className="eyebrow">Exact frame · approved template</p>
          <h2 id="thumbnail-title">手動サムネイル生成</h2>
        </div>
        <p
          className="thumbnail-status"
          role="status"
          data-thumbnail-status={state.status}
        >
          {statusMessages[state.status]}
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
              <dl className="thumbnail-evidence-list">
                <Evidence label="権利状態" value={state.snapshot.rights.status} />
                <Evidence
                  label="外部公開"
                  value={
                    state.snapshot.rights.externalPublicationAllowed
                      ? '許可'
                      : '未許可'
                  }
                />
                <Evidence label="権利 digest" value={state.snapshot.sourceRightsDigest} code />
                <Evidence label="ソース digest" value={state.snapshot.sourceContentDigest} code />
                <Evidence label="PTS index digest" value={state.snapshot.sourcePtsIndexDigest} code />
                <Evidence label="frame map digest" value={state.snapshot.proxyFrameMapDigest} code />
                <Evidence label="player evidence" value={state.snapshot.playerEvidenceDigest} code />
                <Evidence label="thumbnail evidence" value={state.snapshot.evidenceDigest} code />
              </dl>
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
                  disabled={state.pendingGeneration !== null}
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

              {state.output === null ? (
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
                        src={state.output.mediaReference}
                        width={state.output.width}
                        height={state.output.height}
                        alt={`${template?.name ?? '承認済みテンプレート'}、Source PTS ${formatRationalTime(state.output.selection.sourcePresentationTime)} の生成済みサムネイル`}
                        onError={() => setPreviewFailed(true)}
                      />
                    )}
                    <figcaption>
                      {state.output.width} × {state.output.height} PNG ·{' '}
                      {state.output.byteSize} bytes
                    </figcaption>
                  </figure>
                  <dl className="thumbnail-output-evidence">
                    <Evidence label="output SHA-256" value={state.output.contentDigest} code />
                    <Evidence label="binding digest" value={state.output.bindingDigest} code />
                    <Evidence label="artifact ID" value={state.output.artifactId} code />
                    <Evidence
                      label="exact source PTS"
                      value={formatRationalTime(state.output.selection.sourcePresentationTime)}
                      code
                    />
                    <Evidence
                      label="source frame"
                      value={String(state.output.selection.sourceFrameSequence)}
                    />
                    <Evidence
                      label="proxy frame"
                      value={String(state.output.selection.proxyFrameSequence)}
                    />
                    <Evidence label="template approval" value={state.output.template.approvalDigest} code />
                    <Evidence label="作成日時 (UTC)" value={state.output.createdAt} code />
                  </dl>
                </div>
              )}
            </section>
          </>
        )}
      </div>
    </section>
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
        <code>approval {template.approvalDigest}</code>
        <code>template {template.templateVersionDigest}</code>
        <code>profile {template.renderProfileDigest}</code>
        <code>layout {template.layoutSpecDigest}</code>
      </span>
    </label>
  )
}

function createCanonicalId(): string {
  return window.crypto.randomUUID().toLowerCase()
}
