import { useCallback, useEffect, useId, useRef, useState } from 'react'
import {
  createHostProxyService,
  type ProxyOutcome,
  type ProxyService,
} from './proxyProtocol'

export const proxyPreviewMessages = {
  title: '元動画のプロキシプレビュー',
  missing: '未生成',
  generating: '生成中',
  failed: '失敗',
  forbidden: 'この操作の権限がありません。',
  unavailable: 'プロキシ動画の状態を確認できません。',
  timeout: 'プロキシ動画の状態を確認できませんでした。時間をおいて「再生成」を押してください。',
  disconnected: 'この環境ではプロキシ動画を表示できません。',
  playFailed: '再生を開始できませんでした。',
} as const

const pollMilliseconds = 1500

export interface ProxyPreviewProps {
  readonly draftId: string
  readonly isActive?: boolean
  /** undefined: use the WebView2 host bridge; null: unavailable. */
  readonly service?: ProxyService | null
  readonly pollMilliseconds?: number
}

function formatSeconds(value: number): string {
  const whole = Math.max(0, Math.floor(Number.isFinite(value) ? value : 0))
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`
}

/**
 * FR-144: plays the low-resolution proxy of the Project's source video next to the settings, so the user can
 * check the video without leaving. Not ready: the state (未生成 / 生成中 / 失敗) and its reason take the player's place,
 * with 「再生成」. A failure here never blocks the settings.
 */
export function ProxyPreview({ draftId, isActive = true, service: injected, pollMilliseconds: poll = pollMilliseconds }: ProxyPreviewProps) {
  const id = useId()
  const [service, setService] = useState<ProxyService | null>(injected ?? null)
  const [outcome, setOutcome] = useState<ProxyOutcome | null>(null)
  const [playing, setPlaying] = useState(false)
  const [position, setPosition] = useState(0)
  const [duration, setDuration] = useState(0)
  const [playError, setPlayError] = useState(false)
  const video = useRef<HTMLVideoElement>(null)

  useEffect(() => {
    if (injected !== undefined) { setService(injected); return undefined }
    const created = createHostProxyService()
    setService(created)
    return () => created?.dispose()
  }, [injected])

  const refresh = useCallback(async () => {
    if (service === null) { setOutcome(Object.freeze({ status: 'disconnected' as const })); return }
    setOutcome(await service.get(draftId))
  }, [service, draftId])

  useEffect(() => { if (isActive) void refresh() }, [isActive, refresh])

  const status = outcome?.status ?? null
  useEffect(() => {
    if (!isActive || status !== 'generating') return undefined
    const timer = setInterval(() => { void refresh() }, poll)
    return () => clearInterval(timer)
  }, [isActive, status, refresh, poll])

  useEffect(() => {
    if (!isActive) video.current?.pause()
  }, [isActive])

  const regenerate = async (): Promise<void> => {
    if (service === null) return
    setPlaying(false)
    setPosition(0)
    setOutcome(await service.regenerate(draftId))
  }

  const play = async (): Promise<void> => {
    setPlayError(false)
    try { await video.current?.play() } catch { setPlayError(true) }
  }

  const ready = outcome !== null && 'uri' in outcome && outcome.status === 'ready' && outcome.uri !== null
  const uri = ready ? outcome.uri : null
  const generating = status === 'generating'
  const stateText = status === 'missing' ? proxyPreviewMessages.missing
    : status === 'generating' ? proxyPreviewMessages.generating
    : status === 'failed' ? proxyPreviewMessages.failed : null
  const reason = outcome !== null && 'reason' in outcome ? outcome.reason : null
  const note = status === 'forbidden' ? proxyPreviewMessages.forbidden
    : status === 'unavailable' ? proxyPreviewMessages.unavailable
    : status === 'timeout' ? proxyPreviewMessages.timeout
    : status === 'disconnected' ? proxyPreviewMessages.disconnected : null
  const max = Math.max(duration, 0.1)

  return (
    <section className="proxy-preview" role="region" aria-label={proxyPreviewMessages.title} data-proxy-status={status ?? 'loading'}>
      <h3 id={`${id}-title`}>{proxyPreviewMessages.title}</h3>
      {outcome === null && <p role="status">プロキシ動画を確認しています。</p>}
      {uri !== null && (
        <div className="proxy-preview-player">
          <video key={uri} ref={video} src={uri} preload="metadata" playsInline aria-label="元動画のプロキシ（確認用の低解像度）"
            onLoadedMetadata={event => setDuration(event.currentTarget.duration)}
            onDurationChange={event => setDuration(event.currentTarget.duration)}
            onTimeUpdate={event => setPosition(event.currentTarget.currentTime)}
            onSeeked={event => setPosition(event.currentTarget.currentTime)}
            onPlay={() => setPlaying(true)} onPause={() => setPlaying(false)} onEnded={() => setPlaying(false)} />
          <div className="proxy-preview-controls">
            {playing
              ? <button type="button" onClick={() => video.current?.pause()}>一時停止</button>
              : <button type="button" onClick={() => { void play() }}>再生</button>}
            <input type="range" aria-label="再生位置" min={0} max={max} step={0.1} value={Math.min(position, max)}
              onChange={event => {
                const next = Number(event.currentTarget.value)
                if (video.current !== null && Number.isFinite(next)) video.current.currentTime = next
                setPosition(next)
              }} />
            <span className="proxy-preview-time" aria-hidden="true">{formatSeconds(position)} / {formatSeconds(duration)}</span>
          </div>
          {playError && <p role="status">{proxyPreviewMessages.playFailed}</p>}
        </div>
      )}
      {stateText !== null && (
        <div className="proxy-preview-state" role="status">
          <p>状態：{stateText}</p>
          {reason !== null && <p>{status === 'failed' ? '原因' : '理由'}：{reason}</p>}
        </div>
      )}
      {note !== null && <p role="status">{note}</p>}
      {outcome !== null && status !== 'forbidden' && status !== 'disconnected' && (
        <div className="proxy-preview-actions">
          <button type="button" disabled={!isActive || generating} onClick={() => { void regenerate() }}>再生成</button>
          <span className="proxy-preview-hint">高さ360pxの確認用動画です。元動画は変更しません。</span>
        </div>
      )}
    </section>
  )
}
