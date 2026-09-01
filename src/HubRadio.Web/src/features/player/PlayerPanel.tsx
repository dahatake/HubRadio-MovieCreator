import {
  useCallback,
  useEffect,
  useReducer,
  useRef,
  useState,
  type ChangeEvent,
  type FormEvent,
  type KeyboardEvent,
} from 'react'

import { createHostBridgeClient, type BridgeClient } from '../../bridge'
import { GenericUiMask } from '../preview-modes/GenericUiMask'
import {
  initialPreviewModeState,
  type PreviewModeState,
} from '../preview-modes/previewModeState'
import {
  findProxyFrameAtDerivedMediaSeconds,
  findProxyFrameAtSourceTime,
  formatRationalTime,
  isCanonicalEntityId,
  positionMatchesFrame,
  proxyTimeToDerivedMediaSeconds,
  rationalToDerivedSeconds,
  type PlayerFrame,
  type PlayerSnapshot,
  type PlayerTimelinePosition,
} from './playerProtocol'
import {
  initialPlayerState,
  playerStateReducer,
  selectCurrentFrame,
  selectKeyboardSeekSequence,
  type PlayerState,
  type PlayerStatus,
} from './playerState'
import {
  usePlayerSourceSeekRegistration,
  type PlayerSourceSeekHandler,
} from './PlayerNavigationProvider'

const statusMessages: Readonly<Record<PlayerStatus, string>> = {
  idle: '取込ジョブを指定してください',
  loading: '正規のプレビュー根拠を確認しています',
  available: '代理動画とプレビュー根拠を読み込みました',
  forbidden: 'このプレビューを表示する権限がありません',
  unavailable: '利用できる完了済みプレビューがありません',
  stale: 'プレビュー根拠が更新または失効しました',
  mismatched: 'プレビュー根拠の対応関係を確認できません',
  invalidTime: '指定したフレーム時刻は利用できません',
  mediaUnavailable: '代理動画を読み込めません。根拠表示のみ利用できます',
  bridgeUnavailable: 'ネイティブホストへ接続できません',
  bridgeSendFailed: 'ネイティブホストへ要求を送信できません',
}

export type PlayerBridgeClient = Pick<
  BridgeClient,
  | 'sendPlayerRead'
  | 'onPlayerReadResponse'
  | 'sendPlayerSeek'
  | 'onPlayerSeekResponse'
  | 'dispose'
>

export interface PlayerExactFrameSelection {
  readonly jobId: string
  readonly projectId: string
  readonly sourceAssetId: string
  readonly playerEvidenceDigest: string
  readonly frame: PlayerFrame
}

export interface PlayerPanelProps {
  readonly createClient?: () => PlayerBridgeClient
  readonly initialJobId?: string
  readonly previewModes?: PreviewModeState
  readonly onPositionChange?: (position: PlayerTimelinePosition | null) => void
  readonly onSelectionChange?: (
    selection: PlayerExactFrameSelection | null,
  ) => void
}

export function PlayerPanel({
  createClient = createHostBridgeClient,
  initialJobId = '',
  previewModes = initialPreviewModeState,
  onPositionChange,
  onSelectionChange,
}: PlayerPanelProps) {
  const [state, dispatch] = useReducer(playerStateReducer, {
    ...initialPlayerState,
    jobIdInput: initialJobId,
  })
  const [isPlaying, setIsPlaying] = useState(false)
  const [isMuted, setIsMuted] = useState(false)
  const stateRef = useRef<PlayerState>(state)
  const selectionChangeRef = useRef(onSelectionChange)
  const clientRef = useRef<PlayerBridgeClient | null>(null)
  const videoRef = useRef<HTMLVideoElement | null>(null)
  stateRef.current = state
  selectionChangeRef.current = onSelectionChange

  useEffect(() => {
    let client: PlayerBridgeClient
    try {
      client = createClient()
    } catch {
      dispatch({ type: 'bridgeUnavailable' })
      return
    }

    let unsubscribeRead: (() => void) | null = null
    let unsubscribeSeek: (() => void) | null = null
    try {
      unsubscribeRead = client.onPlayerReadResponse((response) => {
        dispatch({ type: 'readReceived', response })
      })
      unsubscribeSeek = client.onPlayerSeekResponse((response) => {
        const current = stateRef.current
        const pending = current.pendingSeek
        const snapshot = current.snapshot
        if (
          pending !== null &&
          snapshot !== null &&
          pending.requestId === response.requestId &&
          response.result.status === 'available'
        ) {
          const expected = snapshot.frames[pending.proxyFrameSequence]
          if (
            expected !== undefined &&
            positionMatchesFrame(response.result.position, expected) &&
            videoRef.current !== null
          ) {
            videoRef.current.currentTime = proxyTimeToDerivedMediaSeconds(
              snapshot,
              response.result.position.proxyPresentationTime,
            )
          }
        }
        dispatch({ type: 'seekReceived', response })
      })
    } catch {
      unsubscribeRead?.()
      unsubscribeSeek?.()
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
        unsubscribeSeek?.()
      } finally {
        client.dispose()
      }
    }
  }, [createClient])

  useEffect(() => {
    setIsPlaying(false)
  }, [state.snapshot?.evidenceDigest])

  const requestSeek = useCallback((proxyFrameSequence: number): boolean => {
    const current = stateRef.current
    const snapshot = current.snapshot
    const jobId = current.jobId
    if (
      snapshot === null ||
      jobId === null ||
      current.pendingSeek !== null ||
      !Number.isInteger(proxyFrameSequence) ||
      proxyFrameSequence < 0 ||
      proxyFrameSequence >= snapshot.frames.length
    ) {
      return false
    }

    const requestId = createRequestId()
    dispatch({ type: 'seekRequested', requestId, proxyFrameSequence })
    const client = clientRef.current
    if (client === null) {
      dispatch({ type: 'bridgeUnavailable' })
      return false
    }
    try {
      client.sendPlayerSeek(
        requestId,
        jobId,
        snapshot.evidenceDigest,
        proxyFrameSequence,
      )
      return true
    } catch {
      dispatch({ type: 'bridgeSendFailed', requestId })
      return false
    }
  }, [])

  const requestSourceSeek = useCallback<PlayerSourceSeekHandler>(
    (target) => {
      const current = stateRef.current
      const snapshot = current.snapshot
      if (snapshot === null || current.jobId === null) {
        return 'unavailable'
      }
      if (target.projectId !== snapshot.projectId) {
        return 'projectMismatch'
      }
      if (current.pendingSeek !== null) {
        return 'busy'
      }
      const sequence = findProxyFrameAtSourceTime(snapshot, target.sourceTime)
      if (sequence === null) {
        return 'outOfRange'
      }
      return requestSeek(sequence) ? 'requested' : 'unavailable'
    },
    [requestSeek],
  )
  usePlayerSourceSeekRegistration(requestSourceSeek)

  const currentFrame = selectCurrentFrame(state)
  useEffect(() => {
    const snapshot = state.snapshot
    onPositionChange?.(
      snapshot === null || currentFrame === null
        ? null
        : Object.freeze({
            projectId: snapshot.projectId,
            sourcePresentationTime: currentFrame.sourcePresentationTime,
            outputPresentationTime: currentFrame.outputPresentationTime,
          }),
    )
  }, [currentFrame, onPositionChange, state.snapshot])

  useEffect(() => {
    const snapshot = state.snapshot
    const jobId = state.jobId
    onSelectionChange?.(
      snapshot === null || jobId === null || currentFrame === null
        ? null
        : Object.freeze({
            jobId,
            projectId: snapshot.projectId,
            sourceAssetId: snapshot.sourceAssetId,
            playerEvidenceDigest: snapshot.evidenceDigest,
            frame: currentFrame,
          }),
    )
  }, [currentFrame, onSelectionChange, state.jobId, state.snapshot])

  useEffect(() => () => selectionChangeRef.current?.(null), [])
  const canLoad =
    isCanonicalEntityId(state.jobIdInput) && state.status !== 'loading'
  const seekDisabled =
    state.snapshot === null || state.pendingSeek !== null || state.jobId === null

  const submitRead = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    if (!isCanonicalEntityId(state.jobIdInput)) {
      return
    }

    const requestId = createRequestId()
    dispatch({
      type: 'readRequested',
      requestId,
      jobId: state.jobIdInput,
    })
    const client = clientRef.current
    if (client === null) {
      dispatch({ type: 'bridgeUnavailable' })
      return
    }
    try {
      client.sendPlayerRead(requestId, state.jobIdInput)
    } catch {
      dispatch({ type: 'bridgeSendFailed', requestId })
    }
  }

  const onTimelineChange = (event: ChangeEvent<HTMLInputElement>): void => {
    const proxyFrameSequence = Number(event.currentTarget.value)
    requestSeek(proxyFrameSequence)
  }

  const togglePlayback = (): void => {
    const video = videoRef.current
    if (video === null) {
      return
    }
    if (video.paused) {
      void video.play().catch(() => {
        setIsPlaying(false)
        dispatch({ type: 'mediaUnavailable' })
      })
    } else {
      video.pause()
    }
  }

  const onPlayerKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === ' ') {
      event.preventDefault()
      togglePlayback()
      return
    }

    const sequence = selectKeyboardSeekSequence(state, event.key)
    if (sequence !== null) {
      event.preventDefault()
      requestSeek(sequence)
    }
  }

  const onTimeUpdate = (): void => {
    const video = videoRef.current
    const snapshot = stateRef.current.snapshot
    if (video === null || snapshot === null) {
      return
    }
    const sequence = findProxyFrameAtDerivedMediaSeconds(
      snapshot,
      video.currentTime,
    )
    if (sequence !== null) {
      dispatch({ type: 'playbackFrameObserved', proxyFrameSequence: sequence })
    }
  }

  return (
    <section className="player-card" aria-labelledby="player-title">
      <div className="player-heading">
        <div>
          <p className="eyebrow">Canonical preview</p>
          <h2 id="player-title">代理動画プレイヤー</h2>
        </div>
        <p className="player-status" role="status" data-player-status={state.status}>
          {statusMessages[state.status]}
        </p>
      </div>

      <form className="player-load-form" onSubmit={submitRead}>
        <label htmlFor="player-job-id">取込ジョブ ID</label>
        <div className="player-load-row">
          <input
            id="player-job-id"
            name="playerJobId"
            type="text"
            inputMode="text"
            autoComplete="off"
            spellCheck={false}
            minLength={36}
            maxLength={36}
            pattern="[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
            value={state.jobIdInput}
            aria-describedby="player-job-help"
            onChange={(event) => {
              dispatch({ type: 'jobIdChanged', value: event.currentTarget.value })
            }}
          />
          <button type="submit" disabled={!canLoad}>
            プレビューを開く
          </button>
        </div>
        <p id="player-job-help" className="field-help">
          完了した媒体取込ジョブの小文字 UUID を指定します。
        </p>
      </form>

      {state.snapshot === null ? (
        <div className="player-empty" aria-hidden={state.status === 'loading'}>
          <span>PROXY</span>
          <p>動画・波形・サムネイルは正規の根拠確認後に表示されます。</p>
        </div>
      ) : (
        <div
          className="player-workspace"
          data-preview-orientation={previewModes.orientation ?? 'source'}
          data-mobile-preview={String(previewModes.mobilePreview)}
          data-audio-only={String(previewModes.audioOnly)}
        >
          <div
            className="video-stage"
            data-preview-orientation={previewModes.orientation ?? 'source'}
            data-audio-only={String(previewModes.audioOnly)}
            tabIndex={0}
            onKeyDown={onPlayerKeyDown}
            aria-label={
              previewModes.audioOnly
                ? '音声のみプレビュー。左右矢印で1フレーム移動、HomeとEndで端へ移動、Spaceで再生と一時停止'
                : '代理動画。左右矢印で1フレーム移動、HomeとEndで端へ移動、Spaceで再生と一時停止'
            }
          >
            <video
              ref={videoRef}
              src={state.snapshot.proxy.mediaReference}
              playsInline
              muted={isMuted}
              aria-hidden={previewModes.audioOnly ? 'true' : undefined}
              preload="metadata"
              onCanPlay={() => dispatch({ type: 'mediaAvailable' })}
              onPlay={() => setIsPlaying(true)}
              onPause={() => setIsPlaying(false)}
              onEnded={() => setIsPlaying(false)}
              onError={() => {
                setIsPlaying(false)
                dispatch({ type: 'mediaUnavailable' })
              }}
              onTimeUpdate={onTimeUpdate}
            >
              代理動画を再生できません。
            </video>
            {previewModes.audioOnly ? (
              <div className="audio-only-placeholder" role="note">
                <strong>音声のみプレビュー</strong>
                <span>映像を隠しています。再生位置と波形は同じ player evidence を使用します。</span>
              </div>
            ) : null}
            <GenericUiMask
              visible={previewModes.genericUiMaskVisible}
              audioOnly={previewModes.audioOnly}
            />
          </div>

          <WaveformView
            snapshot={state.snapshot}
            currentFrame={currentFrame}
          />

          <div className="timeline-controls">
            <button
              type="button"
              className="playback-toggle"
              onClick={togglePlayback}
              aria-label={isPlaying ? '代理動画を一時停止' : '代理動画を再生'}
            >
              {isPlaying ? '一時停止' : '再生'}
            </button>
            <button
              type="button"
              className="mute-toggle"
              aria-pressed={isMuted}
              aria-label={isMuted ? '音声のミュートを解除' : '音声をミュート'}
              onClick={() => setIsMuted((current) => !current)}
            >
              {isMuted ? 'ミュート解除' : 'ミュート'}
            </button>
            <button
              type="button"
              disabled={
                seekDisabled || state.currentFrameSequence === 0
              }
              onClick={() => {
                const sequence = selectKeyboardSeekSequence(state, 'ArrowLeft')
                if (sequence !== null) requestSeek(sequence)
              }}
              aria-label="前の代理フレーム"
            >
              ←
            </button>
            <label htmlFor="proxy-frame-sequence">代理フレーム</label>
            <input
              id="proxy-frame-sequence"
              type="range"
              min={0}
              max={state.snapshot.frames.length - 1}
              step={1}
              value={state.currentFrameSequence ?? 0}
              disabled={seekDisabled}
              onChange={onTimelineChange}
            />
            <output htmlFor="proxy-frame-sequence">
              {(state.currentFrameSequence ?? 0) + 1} / {state.snapshot.frames.length}
            </output>
            <button
              type="button"
              disabled={
                seekDisabled ||
                state.currentFrameSequence === state.snapshot.frames.length - 1
              }
              onClick={() => {
                const sequence = selectKeyboardSeekSequence(state, 'ArrowRight')
                if (sequence !== null) requestSeek(sequence)
              }}
              aria-label="次の代理フレーム"
            >
              →
            </button>
          </div>

          <div className="evidence-grid">
            <TimeDomain label="Source" value={currentFrame?.sourcePresentationTime ?? null} />
            <TimeDomain label="Proxy" value={currentFrame?.proxyPresentationTime ?? null} />
            <TimeDomain label="Output" value={currentFrame?.outputPresentationTime ?? null} />
          </div>

          <SpriteView snapshot={state.snapshot} frame={currentFrame} />
        </div>
      )}
    </section>
  )
}

function WaveformView({
  snapshot,
  currentFrame,
}: Readonly<{ snapshot: PlayerSnapshot; currentFrame: PlayerFrame | null }>) {
  const geometry = createWaveformGeometry(snapshot, currentFrame)
  return (
    <figure className="waveform-panel">
      <figcaption>
        波形 <span>{snapshot.waveform.sampleRate} samples/s</span>
      </figcaption>
      <svg
        viewBox="0 0 1000 160"
        preserveAspectRatio="none"
        role="img"
        aria-label="代理フレームに同期した音声波形"
      >
        <line className="waveform-axis" x1="0" y1="80" x2="1000" y2="80" />
        {geometry.minimumPoints === '' ? null : (
          <>
            <polyline className="waveform-minimum" points={geometry.minimumPoints} />
            <polyline className="waveform-maximum" points={geometry.maximumPoints} />
          </>
        )}
        {geometry.markerX === null ? null : (
          <line
            className="waveform-marker"
            x1={geometry.markerX}
            y1="0"
            x2={geometry.markerX}
            y2="160"
          />
        )}
      </svg>
    </figure>
  )
}

function SpriteView({
  snapshot,
  frame,
}: Readonly<{ snapshot: PlayerSnapshot; frame: PlayerFrame | null }>) {
  if (frame?.spriteCell === null || frame === null) {
    return (
      <figure className="sprite-panel sprite-empty">
        <figcaption>キーフレームサムネイル</figcaption>
        <p>この代理フレームに対応する sprite cell はありません。</p>
      </figure>
    )
  }

  const cell = frame.spriteCell
  const x = cell.column * snapshot.sprite.cellWidth
  const y = cell.row * snapshot.sprite.cellHeight
  return (
    <figure className="sprite-panel">
      <figcaption>キーフレームサムネイル #{cell.ordinal + 1}</figcaption>
      <svg
        viewBox={`${x} ${y} ${snapshot.sprite.cellWidth} ${snapshot.sprite.cellHeight}`}
        role="img"
        aria-label={`代理フレーム ${frame.proxyFrameSequence} のサムネイル`}
      >
        <image
          href={snapshot.sprite.imageReference}
          x="0"
          y="0"
          width={snapshot.sprite.cellWidth * snapshot.sprite.columns}
          height={snapshot.sprite.cellHeight * snapshot.sprite.rows}
        />
      </svg>
    </figure>
  )
}

function TimeDomain({
  label,
  value,
}: Readonly<{
  label: 'Source' | 'Proxy' | 'Output'
  value: PlayerFrame['sourcePresentationTime'] | null
}>) {
  return (
    <div className="time-domain">
      <dt>{label}</dt>
      <dd>{formatRationalTime(value)}</dd>
    </div>
  )
}

function createWaveformGeometry(
  snapshot: PlayerSnapshot,
  currentFrame: PlayerFrame | null,
): Readonly<{
  minimumPoints: string
  maximumPoints: string
  markerX: string | null
}> {
  const start = rationalToDerivedSeconds(snapshot.proxy.presentationStart)
  const end = rationalToDerivedSeconds(snapshot.proxy.presentationEnd)
  const duration = end - start
  if (!(duration > 0)) {
    return { minimumPoints: '', maximumPoints: '', markerX: null }
  }

  const points = snapshot.frames.flatMap((frame) => {
    const channel = frame.waveformChannels[0]
    if (channel === undefined) {
      return []
    }
    const x =
      ((rationalToDerivedSeconds(frame.proxyPresentationTime) - start) /
        duration) *
      1000
    return [
      {
        x,
        minimumY: sampleToWaveformY(channel.minimumSample),
        maximumY: sampleToWaveformY(channel.maximumSample),
      },
    ]
  })
  const minimumPoints = points
    .map((point) => `${formatCoordinate(point.x)},${formatCoordinate(point.minimumY)}`)
    .join(' ')
  const maximumPoints = points
    .map((point) => `${formatCoordinate(point.x)},${formatCoordinate(point.maximumY)}`)
    .join(' ')
  const markerX =
    currentFrame === null
      ? null
      : formatCoordinate(
          ((rationalToDerivedSeconds(currentFrame.proxyPresentationTime) - start) /
            duration) *
            1000,
        )
  return { minimumPoints, maximumPoints, markerX }
}

function sampleToWaveformY(sample: number): number {
  return 80 - (sample / 32_768) * 68
}

function formatCoordinate(value: number): string {
  return value.toFixed(3)
}

function createRequestId(): string {
  return window.crypto.randomUUID().toLowerCase()
}
