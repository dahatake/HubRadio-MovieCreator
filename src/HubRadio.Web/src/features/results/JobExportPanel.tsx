import { useEffect, useId, useRef, useState } from 'react'
import {
  createHostWorkspaceLocationService,
  type WorkspaceBrowseOutcome,
  type WorkspaceLocationService,
} from '../settings/workspaceLocationProtocol'
import {
  createHostJobExportService,
  JOB_EXPORT_MAX_JOB_NAME_LENGTH,
  JOB_EXPORT_MAX_PATH_LENGTH,
  type JobExportDestinationReason,
  type JobExportFailureReason,
  type JobExportKind,
  type JobExportOutcome,
  type JobExportService,
} from './jobExportProtocol'

/** FR-132: what each saved file is, in words (never a colour or an ID). */
export const jobExportKindLabels: Readonly<Record<JobExportKind, string>> = {
  longClean: '長尺（クリーン版）',
  longBurnIn: '長尺（字幕焼き込み版）',
  shortClean: '短尺（クリーン版）',
  shortBurnIn: '短尺（字幕焼き込み版）',
  thumbnailLandscape: 'サムネイル（横長）',
  thumbnailVertical: 'サムネイル（縦型）',
}

/** FR-132 AC-138: why a file could not be saved. No path, exception text or SID is ever shown. */
export const jobExportFailureLabels: Readonly<Record<JobExportFailureReason, string>> = {
  missing: '成果物がありません',
  notWritable: '書き込めません',
  diskFull: '空き容量がありません',
  unavailable: '保存できませんでした',
}

const destinationLabels: Readonly<Record<JobExportDestinationReason, string>> = {
  notFound: '書き出し先のフォルダーが見つかりません。場所を確かめてください。',
  notFolder: '書き出し先がフォルダーではありません。フォルダーを指定してください。',
  notWritable: '書き出し先のフォルダーに書き込めません。別のフォルダーを指定してください。',
}

export const jobExportMessages = {
  forbidden: 'この操作の権限がないため、保存しませんでした。',
  unavailable: '書き出しを利用できません。',
  unavailableEnvironment: 'この環境では利用できません。',
  invalid: '書き出しの指定を確認できません。画面を更新してもう一度お試しください。',
  notFound: 'この Job の成果物は現在の結果から確認できません。画面を更新してください。',
  busy: '別の書き出しを処理中です。完了後にもう一度お試しください。',
  timeout: '応答がありません。書き出し先のフォルダーを確認してください。',
  empty: '書き出し先のフォルダーを入力してください。',
  tooLong: `書き出し先の場所は ${JOB_EXPORT_MAX_PATH_LENGTH} 文字以内で入力してください。`,
  browseFailed: 'フォルダーを選べませんでした。場所を直接入力してください。',
  nothingToSave: '保存できる成果物がありませんでした。保存できたファイルは 0 件です。',
  notCompleted: '完了した Job を選ぶと、動画とサムネイルを書き出せます。',
} as const

export interface JobExportResultView {
  readonly summary: string
  readonly saved: readonly string[]
  readonly failed: readonly string[]
}

/** FR-132 AC-138: the saved count and each failed file with its reason, in Japanese. */
export function describeJobExport(outcome: JobExportOutcome): JobExportResultView | string {
  switch (outcome.status) {
    case 'completed': {
      const failed = outcome.failures.map(failure => `${jobExportKindLabels[failure.kind]}: ${jobExportFailureLabels[failure.reason]}`)
      if (outcome.savedCount === 0 && failed.length === 0) return jobExportMessages.nothingToSave
      return {
        summary: failed.length === 0
          ? `${outcome.savedCount}件のファイルを保存しました。`
          : `${outcome.savedCount}件のファイルを保存しました。保存できなかったファイルが${failed.length}件あります。`,
        saved: outcome.files.map(file => `${jobExportKindLabels[file.kind]}: ${file.fileName}`),
        failed,
      }
    }
    case 'destinationInvalid':
      return outcome.destinationReason === null ? jobExportMessages.invalid : `${destinationLabels[outcome.destinationReason]}ファイルは保存していません。`
    case 'disconnected': return jobExportMessages.unavailable
    case 'timeout': return jobExportMessages.timeout
    default: return jobExportMessages[outcome.status]
  }
}

export interface JobExportPanelProps {
  readonly projectId: string | null
  readonly executionId: string
  /** The Job's name such as "Job 2". Sent so the saved files carry it (the creation time is used when empty). */
  readonly jobName: string | null
  /** Why saving is not allowed right now (for example the Job has not completed); null when it is allowed. */
  readonly disabledReason?: string | null
  readonly isActive?: boolean
  /** undefined: use the WebView2 host bridge; null: unavailable. */
  readonly service?: JobExportService | null
  /** The FR-064 folder dialog. undefined: use the WebView2 host bridge; null: unavailable. */
  readonly browseService?: Pick<WorkspaceLocationService, 'browse'> | null
}

/**
 * FR-132: saves the shown Job's videos and thumbnails to a folder outside the app. The folder is typed or chosen
 * with the same OS dialog as FR-064; existing files are never overwritten (a numbered name is used).
 */
export function JobExportPanel({
  projectId, executionId, jobName, disabledReason = null, isActive = true,
  service: injected, browseService: injectedBrowse,
}: JobExportPanelProps) {
  const id = useId()
  const [service, setService] = useState<JobExportService | null>(injected ?? null)
  const [browser, setBrowser] = useState<Pick<WorkspaceLocationService, 'browse'> | null>(injectedBrowse ?? null)
  const [destination, setDestination] = useState('')
  const [pending, setPending] = useState(false)
  const [message, setMessage] = useState('')
  const [view, setView] = useState<JobExportResultView | null>(null)
  const mounted = useRef(true)

  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])

  useEffect(() => {
    if (injected !== undefined) { setService(injected); return undefined }
    const host = createHostJobExportService()
    setService(host)
    return () => { host?.dispose() }
  }, [injected])

  useEffect(() => {
    if (injectedBrowse !== undefined) { setBrowser(injectedBrowse); return undefined }
    const host = createHostWorkspaceLocationService()
    setBrowser(host)
    return () => { host?.dispose() }
  }, [injectedBrowse])

  // A result belongs to the Job it was made for.
  useEffect(() => { setMessage(''); setView(null) }, [executionId])

  /** FR-064 way: the dialog only fills the input; saving stays with the 書き出し button. */
  async function browse(): Promise<void> {
    if (browser === null || pending) return
    setPending(true)
    setMessage('')
    setView(null)
    let outcome: WorkspaceBrowseOutcome
    try { outcome = await browser.browse() } catch { outcome = { status: 'disconnected' } }
    if (!mounted.current) return
    if (outcome.status === 'selected') setDestination(outcome.path)
    else if (outcome.status === 'forbidden') setMessage(jobExportMessages.forbidden)
    else if (outcome.status !== 'cancelled') setMessage(jobExportMessages.browseFailed)
    setPending(false)
  }

  async function save(): Promise<void> {
    if (service === null || projectId === null || pending) return
    const path = destination.trim()
    if (path.length === 0) { setMessage(jobExportMessages.empty); setView(null); return }
    if (path.length > JOB_EXPORT_MAX_PATH_LENGTH) { setMessage(jobExportMessages.tooLong); setView(null); return }
    setPending(true)
    setMessage('')
    setView(null)
    let outcome: JobExportOutcome
    const name = jobName !== null && jobName.length > 0 ? jobName.slice(0, JOB_EXPORT_MAX_JOB_NAME_LENGTH) : null
    try { outcome = await service.export({ projectId, executionId, destination: path, jobName: name }) }
    catch { outcome = { status: 'disconnected' } }
    if (!mounted.current) return
    const described = describeJobExport(outcome)
    if (typeof described === 'string') setMessage(described)
    else setView(described)
    setPending(false)
  }

  const available = service !== null && projectId !== null
  const blocked = disabledReason !== null
  const disabled = !isActive || !available || blocked || pending
  const headingId = `${id}-title`
  const statusId = `${id}-status`

  return (
    <section className="job-export" aria-labelledby={headingId} aria-busy={pending} data-job-export="">
      <h3 id={headingId}>Jobの書き出し</h3>
      <p className="result-gallery-note">
        このJobの動画（長尺・短尺）とサムネイルを、選んだフォルダーへコピーして保存します。同じ名前のファイルがあるときは上書きせず、番号を付けて別のファイルにします。
        アプリの中の成果物は変わりません。
      </p>
      {!available && <p>{jobExportMessages.unavailableEnvironment}</p>}
      {blocked && <p>{disabledReason}</p>}
      <div className="job-export-field">
        <label htmlFor={`${id}-path`}>書き出し先</label>
        <div className="job-export-input">
          <input id={`${id}-path`} type="text" value={destination} spellCheck={false} autoComplete="off" disabled={disabled}
            aria-label="書き出し先のフォルダー" aria-describedby={statusId}
            onChange={event => setDestination(event.target.value)}
            onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); void save() } }} />
          <button type="button" disabled={disabled || browser === null} onClick={() => { void browse() }}>参照…</button>
          <button type="button" disabled={disabled} onClick={() => { void save() }}>書き出し</button>
        </div>
      </div>
      <div id={statusId} role="status" aria-live="polite" aria-atomic="true" data-job-export-message="">
        {pending && <p>処理しています…</p>}
        {message !== '' && <p>{message}</p>}
        {view !== null && (
          <>
            <p><strong>{view.summary}</strong></p>
            {view.saved.length > 0 && (
              <ul aria-label="保存したファイル">{view.saved.map((line, index) => <li key={`${index}:${line}`}>保存: {line}</li>)}</ul>
            )}
            {view.failed.length > 0 && (
              <ul aria-label="保存できなかったファイル">{view.failed.map((line, index) => <li key={`${index}:${line}`}>失敗: {line}</li>)}</ul>
            )}
          </>
        )}
      </div>
    </section>
  )
}
