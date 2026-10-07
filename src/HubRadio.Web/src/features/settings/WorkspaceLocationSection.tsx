import { useEffect, useId, useRef, useState } from 'react'
import {
  createHostWorkspaceLocationService,
  type WorkspaceLocationOutcome,
  type WorkspaceLocationResult,
  type WorkspaceLocationService,
} from './workspaceLocationProtocol'

export const workspaceLocationMessages = {
  saved: '保存しました。アプリを再起動すると新しい場所を使います。今ある案件のファイルは自動では移動されません。',
  savedSame: '保存しました。現在使っている場所と同じです。',
  invalid: 'この場所は使えません。ローカルのドライブ上の、空のフォルダー（または新しいフォルダー）を指定してください。',
  forbidden: 'この操作の権限がありません。',
  unavailable: 'Workspaceフォルダーの設定を利用できません。',
  unavailableEnvironment: 'この環境では利用できません。',
  empty: '場所を入力してください。',
} as const

type Available = Extract<WorkspaceLocationResult, { status: 'available' }>

export interface WorkspaceLocationSectionProps {
  readonly isActive?: boolean
  /** undefined: use the WebView2 host bridge; null: unavailable. */
  readonly service?: WorkspaceLocationService | null
}

function failureMessage(outcome: Exclude<WorkspaceLocationOutcome, Available>): string {
  switch (outcome.status) {
    case 'invalid': return workspaceLocationMessages.invalid
    case 'forbidden': return workspaceLocationMessages.forbidden
    default: return workspaceLocationMessages.unavailable
  }
}

/** FR-062: shows and edits where the Workspace folder is stored. A change applies from the next start. */
export function WorkspaceLocationSection({ isActive = true, service: injected }: WorkspaceLocationSectionProps) {
  const id = useId()
  const [service, setService] = useState<WorkspaceLocationService | null>(injected ?? null)
  const [info, setInfo] = useState<Available | null>(null)
  const [draft, setDraft] = useState('')
  const [pending, setPending] = useState(false)
  const [message, setMessage] = useState('')
  const mounted = useRef(true)
  const loaded = useRef(false)

  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])

  useEffect(() => {
    if (injected !== undefined) { setService(injected); return undefined }
    const host = createHostWorkspaceLocationService()
    setService(host)
    return () => { host?.dispose() }
  }, [injected])

  useEffect(() => {
    if (service === null || !isActive || loaded.current) return
    loaded.current = true
    setPending(true)
    void (async () => {
      let outcome: WorkspaceLocationOutcome
      try { outcome = await service.read() } catch { outcome = { status: 'disconnected' } }
      if (!mounted.current) return
      if (outcome.status === 'available') {
        setInfo(outcome)
        setDraft(outcome.savedPath ?? outcome.currentPath)
      } else {
        loaded.current = false
        setMessage(failureMessage(outcome))
      }
      setPending(false)
    })()
  }, [service, isActive])

  async function apply(path: string | null): Promise<void> {
    if (service === null || pending) return
    setPending(true)
    setMessage('')
    let outcome: WorkspaceLocationOutcome
    try { outcome = await service.update(path) } catch { outcome = { status: 'disconnected' } }
    if (!mounted.current) return
    if (outcome.status === 'available') {
      setInfo(outcome)
      setDraft(outcome.savedPath ?? outcome.defaultPath)
      setMessage(outcome.restartRequired ? workspaceLocationMessages.saved : workspaceLocationMessages.savedSame)
    } else {
      setMessage(failureMessage(outcome))
    }
    setPending(false)
  }

  function save(): void {
    const path = draft.trim()
    if (path.length === 0) { setMessage(workspaceLocationMessages.empty); return }
    void apply(path)
  }

  const available = service !== null
  const disabled = !isActive || !available || pending
  const headingId = `${id}-title`

  return (
    <section className="settings-workspace-location" aria-labelledby={headingId} aria-busy={pending}>
      <h3 id={headingId}>Workspaceフォルダー</h3>
      <p className="settings-workspace-location-note">
        案件やデータベースを置くフォルダーです。変更は次にアプリを起動したときから使います。
      </p>
      {!available && <p>{workspaceLocationMessages.unavailableEnvironment}</p>}
      {info !== null && (
        <dl className="settings-workspace-location-paths">
          <dt>現在使っている場所</dt>
          <dd data-workspace-path="current">{info.currentPath}</dd>
          {info.restartRequired && (
            <>
              <dt>保存済みで次回起動から使う場所</dt>
              <dd data-workspace-path="saved">{info.savedPath ?? info.defaultPath}</dd>
            </>
          )}
          <dt>初期値</dt>
          <dd data-workspace-path="default">{info.defaultPath}</dd>
        </dl>
      )}
      <label htmlFor={`${id}-path`}>次回起動から使う場所</label>
      <input id={`${id}-path`} type="text" value={draft} spellCheck={false} autoComplete="off" disabled={disabled}
        aria-describedby={`${id}-status`} onChange={event => setDraft(event.target.value)}
        onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); save() } }} />
      <div className="settings-workspace-location-actions">
        <button type="button" disabled={disabled} onClick={save}>保存</button>
        <button type="button" disabled={disabled} onClick={() => { void apply(null) }}>初期値に戻す</button>
      </div>
      <p id={`${id}-status`} role="status" aria-live="polite" aria-atomic="true" data-workspace-message="">{message}</p>
    </section>
  )
}
