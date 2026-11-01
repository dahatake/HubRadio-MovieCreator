import type { ProjectSourceMedia } from '../../bridge'

/** FR-001 AC-049: formats the registered source's metadata for display. Pure so it can be unit tested. */
export function formatSourceMedia(media: ProjectSourceMedia): readonly (readonly [string, string])[] {
  return [
    ['ファイル名', media.fileName],
    ['再生時間', formatDuration(media.durationSeconds)],
    ['解像度', media.width !== null && media.height !== null ? `${media.width}×${media.height}` : '不明'],
    ['縦横比', formatAspectRatio(media.width, media.height)],
    ['フレームレート', media.frameRate === null ? '不明' : `${trimNumber(media.frameRate)} fps`],
    ['音声', media.hasAudio === null ? '不明' : media.hasAudio ? 'あり' : 'なし'],
    ['容量', formatSize(media.sizeBytes)],
    ['作成日時', formatCreatedAt(media.createdAt)],
    ['登録者', media.registeredBy],
  ]
}

export function formatDuration(seconds: number | null): string {
  if (seconds === null) {
    return '不明'
  }

  const whole = Math.round(seconds)
  const minutes = Math.floor(whole / 60)
  const rest = whole % 60
  return minutes > 0 ? `${minutes}分${rest}秒` : `${trimNumber(seconds)}秒`
}

export function formatAspectRatio(width: number | null, height: number | null): string {
  if (width === null || height === null) {
    return '不明'
  }

  const divisor = gcd(width, height)
  return `${width / divisor}:${height / divisor}`
}

export function formatSize(bytes: number): string {
  if (bytes >= 1024 * 1024) {
    return `${trimNumber(bytes / (1024 * 1024))} MB`
  }

  return bytes >= 1024 ? `${trimNumber(bytes / 1024)} KB` : `${bytes} B`
}

function formatCreatedAt(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime())
    ? value
    : parsed.toLocaleString('ja-JP', { dateStyle: 'medium', timeStyle: 'short' })
}

function trimNumber(value: number): string {
  return String(Math.round(value * 100) / 100)
}

function gcd(a: number, b: number): number {
  return b === 0 ? a : gcd(b, a % b)
}

export function SourceMediaDetails({ media }: { readonly media: ProjectSourceMedia }) {
  return (
    <section className="source-media-details" aria-labelledby="source-media-details-title">
      <h4 id="source-media-details-title">元動画の情報</h4>
      <dl className="project-card-metadata source-media-metadata">
        {formatSourceMedia(media).map(([term, value]) => (
          <div key={term}>
            <dt>{term}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
      {media.warnings.length > 0 && (
        <div className="field-warning" role="status">
          <h4>警告</h4>
          <ul>
            {media.warnings.map(warning => (
              <li key={warning}>{warning}</li>
            ))}
          </ul>
        </div>
      )}
    </section>
  )
}
