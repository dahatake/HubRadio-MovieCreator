import { useEffect, useId, useRef, useState } from 'react'
import type { SourceOnlyResultArtifact, SourceOnlyResultDto } from '../workflow/pipelineProtocol'
import type { BookendStitchService, StitchFailureCode, StitchOutcome, StitchPickClipResult, StitchRunResult } from './stitchProtocol'

/** FR-035.20 user-facing failure text. Codes are the stable FR-035 identifiers. */
export const stitchFailureMessages: Readonly<Record<StitchFailureCode, string>> = {
  'stitch.input_path_invalid': 'ネットワーク上の場所にある動画は使えません。',
  'stitch.input_not_found': '選んだ動画が見つかりません。',
  'stitch.input_unsupported': '選んだ動画を読み込めません（映像がない、または未対応の形式です）。',
  'stitch.input_invalid_duration': '動画の長さを確認できません。',
  'stitch.unsupported_geometry': '本編の画面サイズが対応範囲（16〜4096 px）の外です。',
  'stitch.unsupported_frame_rate': '本編のフレームレートが対応範囲（120 fps以下）の外です。',
  'stitch.output_path_invalid': 'ネットワーク上の場所や .mp4 以外の名前には保存できません。',
  'stitch.output_directory_missing': '保存先のフォルダーが見つかりません。',
  'stitch.output_exists': '同じ名前のファイル（または .stitch.json）が既にあります。別の名前で保存してください。',
  'stitch.tool_unverified': '同梱ツールを確認できません。',
  'stitch.encode_failed': '動画の書き出しに失敗しました。',
  'stitch.timeout': '時間内に終わりませんでした。',
  'stitch.output_verification_failed': '書き出した動画の確認に失敗したため、保存しませんでした。',
  'stitch.cancelled': '処理を中止しました。',
  'stitch.internal_error': '予期しないエラーが発生しました。',
}

export const stitchMessages = {
  unavailableEnvironment: 'この環境では利用できません。',
  unsupported: 'この形式は選べません（.mp4 .m4v .mov .mkv .webm）。',
  forbidden: 'この操作の権限がありません。',
  unavailable: '結合機能を利用できません。',
  cancelled: '保存を取り消しました。ファイルは作成していません。',
  notFound: 'この成果物は現在の結果から確認できません。画面を更新してください。',
  invalid: '選んだオープニング・クロージングを確認できません。選び直してください。',
  busy: '別の結合を処理中です。完了後にもう一度お試しください。',
  timeout: '応答がありません。保存先を確認してください。',
} as const

type Slot = 'opening' | 'closing'
type Selection = Readonly<{ clipId: string; fileName: string }>

export interface BookendStitchPanelProps {
  /** null/undefined: the WebView2 host bridge is absent. */
  readonly service: BookendStitchService | null | undefined
  readonly result: SourceOnlyResultDto
  readonly artifact: SourceOnlyResultArtifact
  readonly label: string
}

function formatSeconds(value: number): string {
  return Number(value.toFixed(6)).toString()
}

function runMessage(outcome: StitchOutcome<StitchRunResult>): string {
  switch (outcome.status) {
    case 'completed':
      return `保存しました: ${outcome.outputFileName}（${formatSeconds(outcome.durationSeconds)}秒）。同じ場所に ${outcome.manifestFileName} を作成しました。`
    case 'failed':
      return `${stitchFailureMessages[outcome.code]}（${outcome.code}）`
    case 'disconnected':
      return stitchMessages.unavailable
    default:
      return stitchMessages[outcome.status]
  }
}

function pickMessage(outcome: StitchOutcome<StitchPickClipResult>): string | null {
  switch (outcome.status) {
    case 'selected':
    case 'cancelled':
      return null
    case 'disconnected':
      return stitchMessages.unavailable
    default:
      return stitchMessages[outcome.status]
  }
}

/** FR-035.13〜FR-035.21: choose Opening/Closing and save a new stitched copy of one verified video output. */
export function BookendStitchPanel({ service, result, artifact, label }: BookendStitchPanelProps) {
  const headingId = useId()
  const [opening, setOpening] = useState<Selection | null>(null)
  const [closing, setClosing] = useState<Selection | null>(null)
  const [pending, setPending] = useState<'opening' | 'closing' | 'run' | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const mounted = useRef(true)
  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])

  const available = service !== null && service !== undefined
  const busy = pending !== null

  async function pick(slot: Slot): Promise<void> {
    if (!available || busy) return
    setPending(slot)
    setMessage(null)
    let outcome: StitchOutcome<StitchPickClipResult>
    try { outcome = await service.pickClip() } catch { outcome = { status: 'disconnected' } }
    if (!mounted.current) return
    if (outcome.status === 'selected') {
      const selection = { clipId: outcome.clipId, fileName: outcome.fileName }
      if (slot === 'opening') setOpening(selection); else setClosing(selection)
    }
    setMessage(pickMessage(outcome))
    setPending(null)
  }

  async function run(): Promise<void> {
    if (!available || busy || (opening === null && closing === null)) return
    setPending('run')
    setMessage(null)
    let outcome: StitchOutcome<StitchRunResult>
    try {
      outcome = await service.run({
        projectId: result.binding.projectId, executionId: result.binding.executionId,
        artifactId: artifact.artifact.artifactId, artifactDigest: artifact.artifact.contentDigest,
        openingClipId: opening?.clipId ?? null, closingClipId: closing?.clipId ?? null,
      })
    } catch { outcome = { status: 'disconnected' } }
    if (!mounted.current) return
    if (outcome.status === 'invalid') { setOpening(null); setClosing(null) }
    setMessage(runMessage(outcome))
    setPending(null)
  }

  function slotRow(slot: Slot, selection: Selection | null) {
    const title = slot === 'opening' ? 'オープニング' : 'クロージング'
    return (
      <div className="bookend-stitch-slot" data-stitch-slot={slot}>
        <span>{title}: </span>
        <span data-stitch-selection={slot}>{selection === null ? '未選択' : selection.fileName}</span>{' '}
        <button type="button" disabled={!available || busy} onClick={() => { void pick(slot) }}>
          {pending === slot ? '選択中…' : `${title}を選ぶ`}
        </button>{' '}
        {selection !== null && (
          <button type="button" disabled={busy} aria-label={`${title}の選択を外す`}
            onClick={() => { if (slot === 'opening') setOpening(null); else setClosing(null) }}>外す</button>
        )}
      </div>
    )
  }

  return (
    <details className="bookend-stitch" data-stitch-artifact={artifact.artifact.artifactId}>
      <summary id={headingId}>オープニング・クロージングを付けて保存</summary>
      <section aria-labelledby={headingId}>
        <p className="result-gallery-note">
          {label}の前後に選んだ動画を付けた新しいMP4を、保存先を選んで作成します。元の成果物は変更しません。
        </p>
        {!available && <p className="result-gallery-note">{stitchMessages.unavailableEnvironment}</p>}
        {slotRow('opening', opening)}
        {slotRow('closing', closing)}
        <button type="button" disabled={!available || busy || (opening === null && closing === null)} onClick={() => { void run() }}>
          {pending === 'run' ? '結合中…' : '結合して保存'}
        </button>
        <p role="status" data-stitch-message="">{message ?? ''}</p>
      </section>
    </details>
  )
}
