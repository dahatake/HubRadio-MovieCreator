import { useId, useLayoutEffect, useRef, useState } from 'react'
import { useSettingsWorkspace, type SettingsWorkspaceContextValue } from './SettingsStateProvider'
import type { AnySettingsSnapshot, SettingValue } from './settingsProtocol'
import type { WorkspaceSettingsError } from './settingsState'

export interface SettingsFileActionsProps {
  /** Removes inactive controls without abandoning the provider's requests or drafts. */
  readonly active?: boolean
  /** UX-025 selects/reveals an unsaved input; this component never saves or discards it. */
  readonly onReturnToDraft?: () => void
}

const pageSize = 6
const errorMessages: Readonly<Record<WorkspaceSettingsError, string>> = {
  forbidden: '設定ファイルを読み書きする権限がありません。',
  notFound: '対象の動画が見つかりません。',
  unbound: 'この動画には設定がまだ関連付けられていません。',
  conflict: '設定またはファイルが別の操作で変更されました。設定を再読み込みし、ファイル操作をやり直してください。',
  invalid: '設定ファイルの形式・設定値・サイズを確認してください。部分適用は行いません。',
  unavailable: '設定ファイルの操作結果を確認できません。再保存する前に、設定とファイルを読み込んで確認してください。',
  timeout: '設定ファイルの操作が時間内に完了しませんでした。再保存する前に、設定とファイルを読み込んで確認してください。',
  fileNotFound: 'setting.txtが見つかりません。保存済みの設定は引き続き利用できます。',
  profileMismatch: 'ファイルの設定プロファイルが適用対象と一致しません。別のプロファイルの設定は適用できません。',
  snapshotMismatch: 'ファイルの設定内容とダイジェストが一致しません。内容の整合性を確認できないため適用できません。',
  overwriteRequired: 'setting.txtは既に存在します。上書きするには確認が必要です。',
}

export function SettingsFileActions({ active = true, onReturnToDraft }: SettingsFileActionsProps) {
  const workspace = useSettingsWorkspace()
  return active ? <FileActions workspace={workspace} onReturnToDraft={onReturnToDraft} /> : null
}

function FileActions({ workspace, onReturnToDraft }: { workspace: SettingsWorkspaceContextValue; onReturnToDraft?: () => void }) {
  const { state, isDirty } = workspace
  const { snapshot, importPreview, fileSha256 } = state
  // Only transient UI is reset on target/file changes. The workspace remains the
  // sole owner of saved values, drafts, correlation and captured apply drafts.
  const binding = [
    state.scope, state.projectId, snapshot?.profileId, snapshot?.profileVersion,
    snapshot?.profileDigest, snapshot?.snapshotDigest, snapshot?.revision,
    state.fileSha256, state.importPreview?.snapshotDigest, state.exportNeedsConfirmation,
  ].join(':')
  const id = useId()
  const [confirmationRequest, setConfirmation] = useState<{ action: 'savedOnly' | 'apply'; binding: string } | null>(null)
  const [pageRequest, setPage] = useState<{ page: number; binding: string } | null>(null)
  const confirmation = confirmationRequest?.binding === binding ? confirmationRequest.action : null
  const requestedPage = pageRequest?.binding === binding ? pageRequest.page : 0
  // Invalidate during render, then forget stale choices so an ABA identity change
  // cannot revive them. Do not replace the controls just to reset local state.
  useLayoutEffect(() => {
    setConfirmation(current => current?.binding === binding ? current : null)
    setPage(current => current?.binding === binding ? current : null)
  }, [binding])
  const exportButton = useRef<HTMLButtonElement>(null)
  const readButton = useRef<HTMLButtonElement>(null)
  const refreshButton = useRef<HTMLButtonElement>(null)
  const actionAfterRefresh = useRef<'export' | 'read'>('read')
  const confirmationCancel = useRef<HTMLButtonElement>(null)
  const focusAfterAction = useRef<{ source: HTMLButtonElement; action: 'export' | 'read' } | null>(null)
  const sending = useRef(false)
  // Also guard repeated activation before React commits a synchronous response.
  useLayoutEffect(() => { sending.current = false }, [state])
  const busy = state.pendingRequest !== null
  const blocked = busy || snapshot === null || state.needsRefresh
  const overwrite = state.exportNeedsConfirmation && fileSha256 !== null
  const scopeName = state.scope === 'global' ? '新しい動画の既定値' : 'この動画'
  const changes = snapshot !== null && importPreview !== null ? differences(snapshot, importPreview) : []
  const pageCount = Math.ceil(changes.length / pageSize)
  const page = Math.min(requestedPage, Math.max(0, pageCount - 1))
  const start = page * pageSize
  const rows = changes.slice(start, start + pageSize)

  useLayoutEffect(() => {
    const ownerDocument = readButton.current?.ownerDocument
    if (ownerDocument === undefined) return
    const forgetFocus = (event: FocusEvent) => {
      if (event.target !== ownerDocument.body && event.target !== focusAfterAction.current?.source) focusAfterAction.current = null
    }
    ownerDocument.addEventListener('focusin', forgetFocus)
    return () => ownerDocument.removeEventListener('focusin', forgetFocus)
  }, [])
  useLayoutEffect(() => {
    if (!state.needsRefresh) actionAfterRefresh.current = 'read'
    const request = focusAfterAction.current
    if (request === null) return
    const { activeElement, body } = request.source.ownerDocument
    if (activeElement !== request.source && activeElement !== body) {
      focusAfterAction.current = null
      return
    }
    const originalTarget = confirmationCancel.current ?? (request.action === 'export' ? exportButton.current : readButton.current)
    const target = state.needsRefresh && (originalTarget === null || originalTarget.matches(':disabled'))
      ? refreshButton.current : originalTarget
    if (target === null || target.matches(':disabled')) {
      if (!busy) focusAfterAction.current = null
      return
    }
    // Keep only the return action, not focus ownership, until refresh is activated.
    if (target === refreshButton.current) actionAfterRefresh.current = request.action
    focusAfterAction.current = null
    if (activeElement !== target) target.focus()
  })

  function rememberFocus(source: HTMLButtonElement, action: 'export' | 'read') {
    if (busy || sending.current) return
    focusAfterAction.current = source.ownerDocument.activeElement === source ? { source, action } : null
  }
  function send(action: () => boolean, enabled = !blocked): boolean {
    if (!enabled || busy || sending.current) return false
    sending.current = true
    const accepted = action()
    if (!accepted) sending.current = false
    return accepted
  }
  function cancelFile() {
    if (busy || sending.current) return
    workspace.clearFile()
    workspace.clearError()
    setConfirmation(null)
  }
  function returnToDraft(clearFile: boolean) {
    if (busy || sending.current || onReturnToDraft === undefined) return
    focusAfterAction.current = null
    if (clearFile) cancelFile()
    else setConfirmation(null)
    onReturnToDraft()
  }

  return (
    <section className="settings-file-actions" aria-labelledby={`${id}-title`} aria-busy={busy}>
      <h3 id={`${id}-title`}>設定ファイル</h3>
      <p>対象：{scopeName}</p>
      <p>setting.txtへの保存は保存済み設定だけを出力します。読み込みは差分の確認だけで、設定を自動適用しません。</p>
      {snapshot === null && <p>先に対象の設定を読み込んでください。</p>}
      <div className="setting-actions">
        <button ref={exportButton} type="button" disabled={blocked || confirmation !== null || overwrite} onClick={event => {
          if (blocked || sending.current) return
          rememberFocus(event.currentTarget, 'export')
          if (isDirty) setConfirmation({ action: 'savedOnly', binding })
          else send(() => workspace.exportFile(null))
        }}>setting.txtに保存</button>
        <button ref={readButton} type="button" disabled={blocked || confirmation !== null || overwrite}
          onClick={event => { rememberFocus(event.currentTarget, 'read'); send(workspace.previewFile) }}>setting.txtから読み込む</button>
      </div>
      <div role="status" aria-live="polite" aria-atomic="true">
        {busy && '設定ファイルの操作が完了するまでお待ちください。'}
        {!busy && !state.needsRefresh && state.error === null && state.lastCompletedMethod === 'settings.file.export'
          && '保存済み設定をsetting.txtに出力しました。'}
        {!busy && !state.needsRefresh && state.error === null && state.lastCompletedMethod === 'settings.file.apply'
          && `読み込んだ設定を「${scopeName}」に適用しました。既存の動画や成果物は更新しません。`}
      </div>
      {state.needsRefresh && (
        <div className="inline-error">
          <p>変更を続けるには設定を再読み込みしてください。未保存項目は保持します。</p>
          <button ref={refreshButton} type="button" disabled={busy} onClick={event => {
            rememberFocus(event.currentTarget, actionAfterRefresh.current)
            send(workspace.refresh, true)
          }}>設定を再読み込み</button>
        </div>
      )}
      {state.error !== null && !overwrite && (
        <div className="inline-error" role="alert">
          <p>{errorMessages[state.error]}</p>
          <button type="button" disabled={busy} onClick={workspace.clearError}>閉じる</button>
        </div>
      )}
      {confirmation === 'savedOnly' && (
        <fieldset className="settings-file-confirmation" disabled={busy}>
          <legend>未保存項目があります</legend>
          <p>「{scopeName}」の未保存の変更は出力されません。入力へ戻って個別に保存するか、保存済み値だけを出力してください。未保存項目は保持します。</p>
          <button type="button" disabled={onReturnToDraft === undefined} onClick={() => returnToDraft(false)}>未保存項目へ戻る</button>
          <button type="button" disabled={blocked} onClick={event => {
            rememberFocus(event.currentTarget, 'export')
            if (send(() => workspace.exportFile(null), !blocked && confirmation === 'savedOnly')) setConfirmation(null)
          }}>保存済み値だけを出力</button>
          <button ref={confirmationCancel} type="button" onClick={event => {
            rememberFocus(event.currentTarget, 'export'); setConfirmation(null)
          }}>取消</button>
          {onReturnToDraft === undefined && <p>未保存項目は設定一覧から選んで保存してください。</p>}
        </fieldset>
      )}
      {overwrite && snapshot !== null && (
        <fieldset className="settings-file-confirmation" disabled={busy}>
          <legend>setting.txtを上書きしますか？</legend>
          <p>既存のsetting.txtを「{scopeName}」の保存済み設定で上書きします。確認後にファイルが変わった場合は中止します。</p>
          {isDirty && <p>未保存項目があります。保存済み値だけを出力し、未保存の変更は保持します。</p>}
          <IdentityDetails snapshot={snapshot} fileSha256={fileSha256} />
          {isDirty && <button type="button" disabled={onReturnToDraft === undefined} onClick={() => returnToDraft(true)}>未保存項目へ戻る</button>}
          <button type="button" disabled={blocked} onClick={event => {
            rememberFocus(event.currentTarget, 'export'); send(() => workspace.exportFile(fileSha256))
          }}>上書きする</button>
          <button ref={confirmationCancel} type="button" onClick={event => {
            rememberFocus(event.currentTarget, 'export'); cancelFile()
          }}>取消</button>
          {isDirty && onReturnToDraft === undefined && <p>未保存項目は設定一覧から選んで保存してください。</p>}
        </fieldset>
      )}
      {snapshot !== null && importPreview !== null && fileSha256 !== null && !overwrite && (
        <section className="settings-file-preview" aria-labelledby={`${id}-preview-title`}>
          <h4 id={`${id}-preview-title`}>読み込んだ設定の差分</h4>
          <p>保存済み設定との比較です。未保存の入力値との比較ではありません。読み込んだだけでは設定や未保存項目を変更しません。</p>
          {changes.length === 0 ? <p>保存済み設定との差分はありません。</p> : (
            <table id={`${id}-diff`}>
              <caption>保存済み設定とsetting.txtの差分</caption>
              <thead><tr><th scope="col">設定</th><th scope="col">保存済み値</th><th scope="col">読み込んだ値</th></tr></thead>
              <tbody>{rows.map(({ definition, current, imported }) => (
                <tr key={definition.key} data-setting-key={definition.key}>
                  <th scope="row"><span>{definition.displayName}</span><br /><code>{definition.key}</code></th>
                  <td><pre>{formatValue(current)}</pre></td>
                  <td><pre>{formatValue(imported)}</pre></td>
                </tr>
              ))}</tbody>
            </table>
          )}
          {changes.length > 0 && (
            <nav className="settings-list-pagination" aria-label="設定ファイルの差分ページ">
              <button type="button" disabled={blocked || confirmation !== null || page === 0} aria-controls={`${id}-diff`}
                onClick={() => setPage({ binding, page: Math.max(0, page - 1) })}>前のページ</button>
              <p role="status" aria-live="polite" aria-atomic="true">{start + 1}–{start + rows.length} / {changes.length}件 · {page + 1} / {pageCount}ページ</p>
              <button type="button" disabled={blocked || confirmation !== null || page + 1 >= pageCount} aria-controls={`${id}-diff`}
                onClick={() => setPage({ binding, page: Math.min(page + 1, pageCount - 1) })}>次のページ</button>
            </nav>
          )}
          <fieldset className="settings-file-confirmation" disabled={busy}>
            <legend>{confirmation === 'apply' ? '読み込んだ設定を適用しますか？' : '読み込んだ設定の適用先'}</legend>
            <p>適用先：{scopeName}</p>
            <IdentityDetails snapshot={snapshot} fileSha256={fileSha256} />
            {confirmation === 'apply' ? (
              <>
                <p>保存済み設定を読み込んだ設定に置き換えます。既存の動画や成果物は更新しません。</p>
                {isDirty && <p>未保存の変更も破棄する操作です。適用成功までは保持し、適用処理中に行った新しい編集は残します。</p>}
                <button type="button" disabled={blocked || changes.length === 0} onClick={event => {
                  rememberFocus(event.currentTarget, 'read'); send(workspace.applyFile, !blocked && confirmation === 'apply')
                }}>
                  {isDirty ? '未保存の変更を破棄して適用する' : '適用する'}
                </button>
              </>
            ) : (
              <button type="button" disabled={blocked || confirmation !== null || changes.length === 0}
                onClick={event => { rememberFocus(event.currentTarget, 'read'); setConfirmation({ action: 'apply', binding }) }}>この設定を適用</button>
            )}
            <button ref={confirmation === 'apply' ? confirmationCancel : undefined} type="button" disabled={confirmation === 'savedOnly'}
              onClick={event => { rememberFocus(event.currentTarget, 'read'); cancelFile() }}>読込を取り消す</button>
          </fieldset>
        </section>
      )}
    </section>
  )
}

function IdentityDetails({ snapshot, fileSha256 }: { snapshot: AnySettingsSnapshot; fileSha256: string }) {
  return (
    <details className="settings-file-identity">
      <summary>確認対象の詳細</summary>
      <dl>
        <div><dt>ファイル SHA-256（元のバイト列）</dt><dd><code>{fileSha256}</code></dd></div>
        <div><dt>対象プロファイル</dt><dd><code>{snapshot.profileId}</code> v{snapshot.profileVersion}</dd></div>
        <div><dt>プロファイルダイジェスト</dt><dd><code>{snapshot.profileDigest}</code></dd></div>
        <div><dt>保存済みrevision</dt><dd>{snapshot.revision}</dd></div>
        <div><dt>保存済みスナップショットダイジェスト</dt><dd><code>{snapshot.snapshotDigest}</code></dd></div>
      </dl>
    </details>
  )
}

function differences(snapshot: AnySettingsSnapshot, preview: AnySettingsSnapshot) {
  const saved = new Map(snapshot.values.map(item => [item.key, item.value]))
  const importedValues = new Map(preview.values.map(item => [item.key, item.value]))
  return snapshot.definitions.flatMap(definition => {
    const current = saved.get(definition.key)
    const imported = importedValues.get(definition.key)
    if (current === undefined || imported === undefined || (current.kind === imported.kind && formatValue(current) === formatValue(imported))) return []
    return [{ definition, current, imported }]
  })
}

function formatValue(value: SettingValue): string {
  if (value.kind === 'boolean') return value.booleanValue ? '有効' : '無効'
  if (value.kind === 'integer') return value.integerValue
  if (value.kind === 'rational') return `${value.numerator}/${value.denominator}`
  if (value.kind === 'text') return value.textValue
  // Preserve list boundaries, order and escapes instead of joining ambiguous text.
  return JSON.stringify(value.textListValue)
}