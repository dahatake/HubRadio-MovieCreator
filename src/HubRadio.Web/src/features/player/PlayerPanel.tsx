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
  type PlayerAction,
  type PlayerState,
  type PlayerStatus,
} from './playerState'
import {
  usePlayerSourceSeekRegistration,
  type PlayerSourceSeekHandler,
} from './PlayerNavigationProvider'
import { createPlayerBridgeSession, type PlayerBridgeSession } from './playerBridgeSession'

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
> & Readonly<{ abandonPlayerRequest?: (requestId: string) => void }>

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
  readonly context?: Readonly<{ jobId: string | null }>
  readonly active?: boolean
  readonly previewModes?: PreviewModeState
  readonly onPositionChange?: (position: PlayerTimelinePosition | null) => void
  readonly onSelectionChange?: (
    selection: PlayerExactFrameSelection | null,
  ) => void
}

export function PlayerPanel(props: PlayerPanelProps) {
  // Only a job/context change replaces the evidence session, never visibility,
  // display modes, or callback identities. Old media cannot cross this boundary.
  return (
    <PlayerSession
      key={props.context === undefined
        ? 'standalone'
        : props.context.jobId === null ? 'no-job' : `job:${props.context.jobId}`}
      {...props}
    />
  )
}

function PlayerSession({
  createClient = createHostBridgeClient,
  initialJobId = '',
  context,
  active = true,
  previewModes = initialPreviewModeState,
  onPositionChange,
  onSelectionChange,
}: PlayerPanelProps) {
  const contextJobId = context?.jobId
  const [state, reduce] = useReducer(playerStateReducer, {
    ...initialPlayerState,
    jobIdInput: context === undefined ? initialJobId : context.jobId ?? '',
  })
  const [isPlaying, setIsPlaying] = useState(false)
  const [isMuted, setIsMuted] = useState(false)
  const [seekNotice, setSeekNotice] = useState<string | null>(null)
  const stateRef = useRef<PlayerState>(state)
  const seekSessionRef = useRef<PlayerBridgeSession | null>(null)
  const dispatch = useCallback((action: PlayerAction): void => {
    const previousSeek = stateRef.current.pendingSeek
    stateRef.current = playerStateReducer(stateRef.current, action)
    if (previousSeek !== null && stateRef.current.pendingSeek?.requestId !== previousSeek.requestId) {
      seekSessionRef.current?.abandonSeek(previousSeek.requestId)
    }
    reduce(action)
  }, [])
  const createClientRef = useRef(createClient)
  const positionChangeRef = useRef(onPositionChange)
  const selectionChangeRef = useRef(onSelectionChange)
  const activeRef = useRef(active)
  const clientRef = useRef<PlayerBridgeClient | null>(null)
  const videoRef = useRef<HTMLVideoElement | null>(null)
  const playbackRequestRef = useRef<Readonly<{ video: HTMLVideoElement }> | null>(null)
  stateRef.current = state
  createClientRef.current = createClient
  positionChangeRef.current = onPositionChange
  selectionChangeRef.current = onSelectionChange
  activeRef.current = active

  useEffect(() => {
    if (
      contextJobId !== undefined &&
      (contextJobId === null || !isCanonicalEntityId(contextJobId))
    ) {
      return
    }

    let client: PlayerBridgeClient
    try {
      client = createClientRef.current()
    } catch {
      dispatch({ type: 'bridgeUnavailable' })
      return
    }

    let unsubscribeRead: (() => void) | null = null
    let unsubscribeSeek: (() => void) | null = null
    const seekSession = createPlayerBridgeSession(client, requestId => {
      if (stateRef.current.pendingSeek?.requestId !== requestId) return
      dispatch({ type: 'seekAbandoned', requestId })
      setSeekNotice('元動画の移動確認が時間切れになりました。時刻は確定していません。もう一度手動で移動できます。')
    })
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
          pending.evidenceDigest === snapshot.evidenceDigest &&
          response.result.status === 'available'
        ) {
          const expected = snapshot.frames[pending.proxyFrameSequence]
          if (
            expected !== undefined &&
            positionMatchesFrame(response.result.position, expected) &&
            videoRef.current !== null
          ) {
            try {
              videoRef.current.currentTime = proxyTimeToDerivedMediaSeconds(
                snapshot,
                response.result.position.proxyPresentationTime,
              )
            } catch {
              dispatch({ type: 'mediaUnavailable' })
              setSeekNotice('元動画の指定時刻へ移動できませんでした。移動要求を解除しました。')
              return
            }
          }
        }
        if (pending?.requestId === response.requestId) seekSession.releaseSeek(response.requestId)
        dispatch({ type: 'seekReceived', response })
      })
    } catch {
      seekSession.dispose()
      unsubscribeRead?.()
      unsubscribeSeek?.()
      client.dispose()
      dispatch({ type: 'bridgeUnavailable' })
      return
    }

    clientRef.current = client
    seekSessionRef.current = seekSession
    return () => {
      if (clientRef.current === client) {
        clientRef.current = null
      }
      if (seekSessionRef.current === seekSession) seekSessionRef.current = null
      seekSession.dispose()
      try {
        unsubscribeRead?.()
        unsubscribeSeek?.()
      } finally {
        client.dispose()
      }
    }
  }, [contextJobId, dispatch])

  const pausePlayback = useCallback((video: HTMLVideoElement | null): void => {
    const hadPlaybackRequest = playbackRequestRef.current !== null
    playbackRequestRef.current = null
    if (video !== null && (!video.paused || hadPlaybackRequest)) video.pause()
  }, [])

  useEffect(() => {
    setIsPlaying(false)
    const video = videoRef.current
    return () => pausePlayback(video)
  }, [pausePlayback, state.snapshot?.evidenceDigest])

  useEffect(() => {
    if (active) return
    pausePlayback(videoRef.current)
    setIsPlaying(false)
  }, [active, pausePlayback])

  const requestRead = useCallback((jobId: string): void => {
    if (!isCanonicalEntityId(jobId)) return

    const pendingSeek = stateRef.current.pendingSeek
    if (pendingSeek !== null) seekSessionRef.current?.abandonSeek(pendingSeek.requestId)
    setSeekNotice(null)
    const requestId = createRequestId()
    dispatch({ type: 'readRequested', requestId, jobId })
    const client = clientRef.current
    if (client === null) {
      dispatch({ type: 'bridgeUnavailable' })
      return
    }
    try {
      client.sendPlayerRead(requestId, jobId)
    } catch {
      dispatch({ type: 'bridgeSendFailed', requestId })
    }
  }, [dispatch])

  useEffect(() => {
    if (
      contextJobId === undefined ||
      contextJobId === null ||
      !isCanonicalEntityId(contextJobId)
    ) {
      return
    }

    // Defer past effect setup so StrictMode replay cancels, rather than sends,
    // the first read. A new context object for the same job does not reload.
    let cancelled = false
    queueMicrotask(() => {
      if (!cancelled) requestRead(contextJobId)
    })
    return () => {
      cancelled = true
    }
  }, [contextJobId, requestRead])

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
    const client = clientRef.current
    const session = seekSessionRef.current
    if (client === null || session === null) {
      dispatch({ type: 'bridgeUnavailable' })
      return false
    }
    if (!session.trackSeek(requestId)) return false
    dispatch({ type: 'seekRequested', requestId, proxyFrameSequence })
    setSeekNotice(null)
    try {
      client.sendPlayerSeek(
        requestId,
        jobId,
        snapshot.evidenceDigest,
        proxyFrameSequence,
      )
      return true
    } catch {
      session.abandonSeek(requestId)
      dispatch({ type: 'bridgeSendFailed', requestId })
      return false
    }
  }, [dispatch])

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
    positionChangeRef.current?.(
      snapshot === null || currentFrame === null
        ? null
        : Object.freeze({
            projectId: snapshot.projectId,
            sourcePresentationTime: currentFrame.sourcePresentationTime,
            outputPresentationTime: currentFrame.outputPresentationTime,
          }),
    )
  }, [currentFrame, state.snapshot])

  useEffect(() => {
    const snapshot = state.snapshot
    const jobId = state.jobId
    selectionChangeRef.current?.(
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
  }, [currentFrame, state.jobId, state.snapshot])

  useEffect(() => () => {
    positionChangeRef.current?.(null)
    selectionChangeRef.current?.(null)
  }, [])
  const canLoad =
    isCanonicalEntityId(state.jobIdInput) && state.status !== 'loading'
  const seekDisabled =
    state.snapshot === null || state.pendingSeek !== null || state.jobId === null
  const contextMessage = contextJobId === null
    ? 'プレビューする取込ジョブが選択されていません'
    : contextJobId !== undefined && !isCanonicalEntityId(contextJobId)
      ? '選択した取込ジョブを確認できません'
      : null

  const submitRead = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    if (activeRef.current && canLoad) requestRead(state.jobIdInput)
  }

  const onTimelineChange = (event: ChangeEvent<HTMLInputElement>): void => {
    const proxyFrameSequence = Number(event.currentTarget.value)
    requestSeek(proxyFrameSequence)
  }

  const togglePlayback = (): void => {
    const video = videoRef.current
    if (video === null || !activeRef.current) {
      return
    }
    if (video.paused && playbackRequestRef.current === null) {
      const playbackRequest = { video }
      playbackRequestRef.current = playbackRequest
      void video.play()
        .then(() => {
          if (
            videoRef.current !== video ||
            !activeRef.current ||
            playbackRequestRef.current === null
          ) {
            if (!video.paused) video.pause()
          }
        })
        .catch(() => {
          if (
            videoRef.current !== video ||
            !activeRef.current ||
            playbackRequestRef.current !== playbackRequest
          ) return
          playbackRequestRef.current = null
          setIsPlaying(false)
          dispatch({ type: 'mediaUnavailable' })
        })
    } else {
      pausePlayback(video)
      setIsPlaying(false)
    }
  }

  const onPlayerKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (!activeRef.current) return
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
    <section
      className={context === undefined ? 'player-card' : 'player-card player-workflow'}
      aria-labelledby="player-title"
      hidden={!active}
      inert={!active}
      data-preview-kind="proxy"
      data-output-verification="not-verified"
    >
      <div className="player-heading">
        <div>
          {context === undefined ? <p className="eyebrow">Canonical preview</p> : null}
          <h2 id="player-title">{context === undefined ? '代理動画プレイヤー' : 'プレビュー'}</h2>
        </div>
        <p className="player-status" role="status" data-player-status={state.status}>
          {contextMessage ?? statusMessages[state.status]}
        </p>
      </div>

      {seekNotice !== null && <p className="player-seek-notice" role={active ? 'status' : undefined}>{seekNotice}</p>}

      {context === undefined ? (
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
      ) : (
        <p className="player-preview-kind">
          代理動画プレビューです。最終出力の画質・コーデックを検証するものではありません。
        </p>
      )}

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
              onPlay={(event) => {
                if (
                  !activeRef.current ||
                  playbackRequestRef.current?.video !== event.currentTarget
                ) {
                  pausePlayback(event.currentTarget)
                  setIsPlaying(false)
                  return
                }
                setIsPlaying(true)
              }}
              onPause={() => {
                playbackRequestRef.current = null
                setIsPlaying(false)
              }}
              onEnded={() => {
                playbackRequestRef.current = null
                setIsPlaying(false)
              }}
              onError={() => {
                playbackRequestRef.current = null
                setIsPlaying(false)
                const pendingSeek = stateRef.current.pendingSeek
                if (pendingSeek !== null) seekSessionRef.current?.abandonSeek(pendingSeek.requestId)
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
