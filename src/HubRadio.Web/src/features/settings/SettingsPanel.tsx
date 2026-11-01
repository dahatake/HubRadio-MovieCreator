import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { SettingInspector } from './SettingInspector'
import { ArrangeablePanels } from '../arrange/ArrangeablePanels'
import { SettingsFileActions } from './SettingsFileActions'
import { SettingsList } from './SettingsList'
import { SettingsOutcomePreview } from './SettingsOutcomePreview'
import { WorkspaceLocationSection } from './WorkspaceLocationSection'
import { isSettingEditable } from './settingEditors'
import { useSettingsWorkspace } from './SettingsStateProvider'
import { createSettingsReadRequest, type AnySettingsSnapshot, type SettingsGroup, type SettingsWorkspaceMethod } from './settingsProtocol'
import type { WorkspaceSettingsDraftValues, WorkspaceSettingsError } from './settingsState'
import './settings.css'

export interface SettingsPanelProps {
  readonly isActive?: boolean
  readonly activeProjectId?: string | null
  /**
   * FR-061.6: whether a project is open. Defaults to "an active project ID was given".
   * Without an open project only 「全体の初期値」 is offered.
   */
  readonly projectOpen?: boolean
  readonly onClose?: () => void
  readonly initialSettingKey?: string | null
  readonly onDirtyChange?: (dirty: boolean) => void
}

const groupNames: Readonly<Record<SettingsGroup, string>> = {
  projectAndOutput: 'Projectと出力', editing: '編集', subtitlesAndGraphics: '字幕とグラフィック', audioAndAssets: '音声と素材', privacyAndStorage: 'プライバシーと保存',
}
const groups = Object.keys(groupNames) as SettingsGroup[]
const errorMessages: Readonly<Record<WorkspaceSettingsError, string>> = {
  forbidden: 'この設定を表示または変更する権限がありません。',
  notFound: '対象の動画が見つかりません。',
  unbound: 'この動画には初期設定がまだ関連付けられていません。',
  conflict: '設定が別の操作で更新されました。未保存項目は保持しています。再読み込みしてください。',
  invalid: '設定値を確認してください。',
  unavailable: '設定の操作結果を確認できません。再読み込みして状態を確認してください。',
  timeout: '設定の操作が時間内に完了しませんでした。再読み込みして状態を確認してください。',
  fileNotFound: 'setting.txtが見つかりません。',
  profileMismatch: '設定ファイルのプロファイルが対象と一致しません。',
  snapshotMismatch: '設定ファイルの内容とダイジェストが一致しません。',
  overwriteRequired: '設定ファイルの表示からsetting.txtの上書きを確認してください。',
}
const completionMessages: Readonly<Record<SettingsWorkspaceMethod, string>> = {
  'settings.read': '設定を読み込みました。',
  'settings.global.read': '全体の初期値を読み込みました。',
  'settings.update': '現在値を保存しました。',
  'settings.global.update': '既定値を保存しました。',
  'settings.reset': 'Project開始時の値へ戻しました。',
  'settings.global.reset': 'プロファイル初期値へ戻しました。',
  'settings.resetAll': 'すべての設定をProject開始時の値へ戻しました。',
  'settings.global.resetAll': 'すべての設定をプロファイル初期値へ戻しました。',
  'settings.file.export': '保存済み設定をsetting.txtに出力しました。',
  'settings.file.preview': '設定ファイルの差分を取得しました。設定は未変更です。',
  'settings.file.apply': '読み込んだ設定を保存しました。既存の動画や成果物は未更新です。',
}
type DetailView = 'setting' | 'outcome' | 'file'
type ConfirmationAction =
  | Readonly<{ kind: 'scope'; target: string | null }>
  | Readonly<{ kind: 'reset'; key: string }>
  | Readonly<{ kind: 'resetAll' }>
  | Readonly<{ kind: 'close' }>
interface Confirmation {
  readonly action: ConfirmationAction
  readonly projectId: string | null
  readonly snapshot: AnySettingsSnapshot | null
  readonly drafts: WorkspaceSettingsDraftValues
}

/** Keep one SettingsWorkspaceStateProvider mounted above this page; legacy consumers remain separate. */
export function SettingsPanel({ isActive = true, activeProjectId = null, projectOpen: projectOpenProp, onClose, initialSettingKey = null, onDirtyChange }: SettingsPanelProps) {
  const workspace = useSettingsWorkspace()
  const { state, isDirty, setScope, load } = workspace
  const { snapshot, draftValues, pendingRequest } = state
  const activeTarget = useMemo(() => canonicalProjectId(activeProjectId), [activeProjectId])
  const projectOpen = projectOpenProp ?? activeProjectId !== null
  // A stale project scope stays switchable back to the defaults until it is resolved.
  const showGlobalScope = !projectOpen || activeTarget === null || state.scope === 'global'
  const id = `settings-${useId()}`
  const panel = useRef<HTMLElement>(null)
  const title = useRef<HTMLHeadingElement>(null)
  const inspector = useRef<HTMLDivElement>(null)
  const detailPane = useRef<HTMLDivElement>(null)
  const opener = useRef<HTMLElement | null>(null)
  const confirmationOpener = useRef<HTMLElement | null>(null)
  const tabs = useRef<Partial<Record<SettingsGroup, HTMLButtonElement | null>>>({})
  const seenContext = useRef<string | null | undefined>(undefined)
  const requestedReveal = useRef<{ key: string; projectId: string | null } | null>(null)
  const revealed = useRef<{ key: string; projectId: string | null } | null>(null)
  const sending = useRef(false)
  const closing = useRef(false)
  const reportedDirty = useRef<boolean | undefined>(undefined)
  const focusTarget = useRef<HTMLElement | 'editor' | null>(null)
  const [focusRevision, setFocusRevision] = useState(0)
  const [revealRevision, setRevealRevision] = useState(0)
  const [activeGroupState, setActiveGroup] = useState<SettingsGroup>('projectAndOutput')
  const [focusedGroup, setFocusedGroup] = useState<SettingsGroup | null>(null)
  const [selectedKeys, setSelectedKeys] = useState<Partial<Record<SettingsGroup, string>>>({})
  const [missingKey, setMissingKey] = useState<string | null>(null)
  const [detailView, setDetailView] = useState<DetailView>('setting')
  // Keep confirmation intent here; values stay in provider state.
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null)
  // FR-050.1: only settings the user can actually change are listed; groups without any are not shown.
  const editableDefinitions = useMemo(() => snapshot?.definitions.filter(isSettingEditable) ?? [], [snapshot?.definitions])
  const visibleGroups = useMemo(() => snapshot === null ? groups : groups.filter(group => editableDefinitions.some(item => item.group === group)),
    [snapshot, editableDefinitions])
  const activeGroup = visibleGroups.includes(activeGroupState) ? activeGroupState : visibleGroups[0] ?? activeGroupState
  const definitions = useMemo(() => editableDefinitions.filter(item => item.group === activeGroup), [editableDefinitions, activeGroup])
  const selectedKey = missingKey ?? selectedKeys[activeGroup] ?? definitions.find(item => !item.isAdvanced)?.key ?? definitions[0]?.key ?? null
  const definition = editableDefinitions.find(item => item.key === selectedKey)
  const fixedTarget = missingKey === null ? undefined : snapshot?.definitions.find(item => item.key === missingKey)
  const current = snapshot?.values.find(item => item.key === selectedKey)?.value
  const initial = snapshot?.initialValues.find(item => item.key === selectedKey)?.value
  const changedKeys = Object.keys(draftValues)
  const busy = pendingRequest !== null
  const oldProject = state.projectId !== null && state.projectId !== activeTarget
  const scopeName = state.scope === 'global' ? '全体の初期値' : oldProject ? '切替前のProject' : '次に作成するJob'
  const restoreName = state.scope === 'global' ? 'プロファイル初期値' : 'Project開始時の初期値'
  const blocked = !isActive || busy || snapshot === null || state.needsRefresh || confirmation !== null
  const confirmationIsCurrent = confirmation !== null && confirmation.projectId === state.projectId
    && confirmation.snapshot === snapshot && confirmation.drafts === draftValues
    && (confirmation.action.kind !== 'scope' || confirmation.action.target === null || confirmation.action.target === activeTarget)

  useLayoutEffect(() => { sending.current = false }, [state, confirmation])
  useEffect(() => {
    if (onDirtyChange === undefined || reportedDirty.current === isDirty) return
    reportedDirty.current = isDirty
    onDirtyChange(isDirty)
  }, [isDirty, onDirtyChange])

  useLayoutEffect(() => {
    if (!isActive || panel.current?.closest('[hidden], [inert]') || detailPane.current === null) return
    detailPane.current.scrollTop = 0
    detailPane.current.scrollLeft = 0
  }, [isActive, selectedKey, detailView])
  const queueFocus = useCallback((target: HTMLElement | 'editor' | null) => {
    focusTarget.current = target
    setFocusRevision(value => value + 1)
  }, [])
  useLayoutEffect(() => {
    if (!isActive) { focusTarget.current = null; return }
    if (confirmation !== null || panel.current?.closest('[hidden], [inert]')) return
    const target = focusTarget.current
    focusTarget.current = null
    if (target === null) return
    const element = target === 'editor'
      ? inspector.current?.querySelector<HTMLElement>('input:not(:disabled), textarea:not(:disabled), select:not(:disabled)') ?? inspector.current
      : target
    // Reveal explicit focus targets even below long saved values.
    if (!focusVisible(element, false)) focusVisible(title.current)
  }, [isActive, confirmation, selectedKey, detailView, focusRevision])

  useLayoutEffect(() => {
    const element = panel.current
    if (!isActive || element === null || element.closest('[hidden], [inert]')) return
    closing.current = false
    const previous = document.activeElement
    if (previous instanceof HTMLElement && previous !== document.body && !element.contains(previous)) opener.current = previous
    if (element.querySelector('[role="alertdialog"]') === null) focusVisible(title.current)
    return () => {
      const active = document.activeElement
      // Do not take focus back from a destination already chosen by the shell.
      if (active === document.body || element.contains(active)) {
        if (!focusVisible(opener.current) && active instanceof HTMLElement) active.blur()
      }
    }
    // Visibility, not responses/errors or callback identity, owns entry focus.
  }, [isActive])

  const ask = useCallback((action: ConfirmationAction) => {
    confirmationOpener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    setConfirmation({ action, projectId: state.projectId, snapshot, drafts: draftValues })
  }, [state.projectId, snapshot, draftValues])
  const requestScope = useCallback((target: string | null, allowPendingRead = false) => {
    if (!isActive || (pendingRequest !== null && !(allowPendingRead && isRead(pendingRequest.method)))) return
    if (state.projectId === target) {
      if (snapshot === null && pendingRequest === null) load()
      return
    }
    if (!setScope(target)) { if (isDirty) ask({ kind: 'scope', target }); return }
    setConfirmation(null)
    setMissingKey(null)
    setDetailView('setting')
    load()
  }, [isActive, pendingRequest, state.projectId, snapshot, load, setScope, isDirty, ask])

  useEffect(() => {
    if (!isActive || initialSettingKey === null) requestedReveal.current = null
    const needsRevealScope = initialSettingKey !== null
      && (requestedReveal.current?.key !== initialSettingKey || requestedReveal.current?.projectId !== activeTarget)
    if (!isActive || (seenContext.current === activeTarget && !needsRevealScope)
      || (pendingRequest !== null && !isRead(pendingRequest.method))) return
    let cancelled = false
    // Defer beyond layout/session setup, including StrictMode's setup/cleanup replay.
    queueMicrotask(() => {
      if (cancelled || panel.current?.closest('[hidden], [inert]')) return
      seenContext.current = activeTarget
      // Consume warning intent before asking, so cancellation does not retry it.
      if (initialSettingKey !== null) requestedReveal.current = { key: initialSettingKey, projectId: activeTarget }
      requestScope(activeTarget, true)
    })
    return () => { cancelled = true }
  }, [isActive, activeTarget, initialSettingKey, pendingRequest, requestScope])

  const revealSetting = useCallback((key: string, focus: boolean) => {
    const target = editableDefinitions.find(item => item.key === key)
    setMissingKey(target === undefined ? key : null)
    if (target !== undefined) {
      setActiveGroup(target.group)
      setSelectedKeys(keys => ({ ...keys, [target.group]: key }))
      setRevealRevision(value => value + 1)
    }
    setDetailView('setting')
    if (focus) queueFocus('editor')
  }, [editableDefinitions, queueFocus])
  useEffect(() => {
    if (!isActive || initialSettingKey === null) { revealed.current = null; return }
    if (snapshot === null || snapshot.projectId !== activeTarget) return
    if (revealed.current?.key === initialSettingKey && revealed.current.projectId === snapshot.projectId) return
    revealed.current = { key: initialSettingKey, projectId: snapshot.projectId }
    // A late response may reveal a warning target, but must not interrupt typing.
    revealSetting(initialSettingKey, document.activeElement === title.current)
  }, [isActive, initialSettingKey, snapshot, activeTarget, revealSetting])

  function send(action: () => boolean): boolean {
    if (!isActive || busy || sending.current || confirmation !== null) return false
    sending.current = true
    const accepted = action()
    if (!accepted) sending.current = false
    return accepted
  }
  function close() {
    if (onClose === undefined || closing.current) return
    closing.current = true
    onClose()
    if (document.activeElement === document.body || panel.current?.contains(document.activeElement)) focusVisible(opener.current)
  }
  function requestClose() {
    if (!isActive || busy || confirmation !== null || onClose === undefined) return
    if (isDirty) ask({ kind: 'close' })
    else close()
  }
  function closeWithDrafts() {
    if (!isActive || busy || sending.current || confirmation?.action.kind !== 'close' || !confirmationIsCurrent) return
    setConfirmation(null)
    close()
  }
  function returnToDraft() {
    setConfirmation(null)
    const key = selectedKey !== null && Object.hasOwn(draftValues, selectedKey) ? selectedKey : changedKeys[0]
    if (key !== undefined) revealSetting(key, true)
  }
  function cancelConfirmation() {
    setConfirmation(null)
    queueFocus(confirmationOpener.current ?? title.current)
  }
  function confirm() {
    if (!isActive || busy || sending.current || confirmation === null || !confirmationIsCurrent) return
    sending.current = true
    const action = confirmation.action
    if (action.kind === 'close') {
      workspace.discardDrafts()
      if (onDirtyChange !== undefined && reportedDirty.current !== false) { reportedDirty.current = false; onDirtyChange(false) }
      setConfirmation(null)
      close()
      return
    }
    if (action.kind === 'scope') {
      workspace.discardDrafts()
      if (setScope(action.target)) { setMissingKey(null); setDetailView('setting'); load() }
    } else {
      // Preserve drafts until the reset succeeds.
      if (action.kind === 'reset') workspace.reset(action.key)
      else workspace.resetAll()
    }
    setConfirmation(null)
    queueFocus(confirmationOpener.current ?? title.current)
  }
  function moveGroup(event: KeyboardEvent<HTMLButtonElement>, group: SettingsGroup) {
    if (!isActive || confirmation !== null) return
    const index = visibleGroups.indexOf(group)
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? visibleGroups.length - 1
      : event.key === 'ArrowLeft' ? (index + visibleGroups.length - 1) % visibleGroups.length
      : event.key === 'ArrowRight' ? (index + 1) % visibleGroups.length : -1
    if (next < 0) return
    event.preventDefault()
    tabs.current[visibleGroups[next]!]?.focus()
    // Enter/Space use native button activation; Up/Down remain native scroll keys.
  }

  return (
    <section ref={panel} className="settings-panel" hidden={!isActive} inert={!isActive} aria-labelledby={`${id}-title`}>
      <div className="settings-page-content" inert={confirmation !== null} aria-hidden={confirmation !== null ? true : undefined}
        onKeyDown={event => {
          if (!isActive || event.key !== 'Escape' || event.defaultPrevented) return
          const disclosure = event.target instanceof Element ? event.target.closest('details[open]') : null
          if (disclosure instanceof HTMLDetailsElement) {
            event.preventDefault()
            disclosure.open = false
            focusVisible(disclosure.querySelector('summary'), false)
            return
          }
          if (event.target instanceof Element && event.target.closest('.settings-file-actions')) return
          if (onClose !== undefined && !busy) { event.preventDefault(); requestClose() }
        }}>
        <header className="settings-heading">
          <h2 id={`${id}-title`} ref={title} tabIndex={-1}>設定</h2>
          <div className="settings-scope" role="group" aria-label="設定の対象">
            <span>対象</span>
            {/* FR-061.6: the list edits only the overall defaults; a created project edits only its next Job. */}
            {showGlobalScope && <button type="button" aria-pressed={state.scope === 'global'} disabled={!isActive || busy} onClick={() => requestScope(null)}>全体の初期値</button>}
            {projectOpen && <button type="button" aria-pressed={state.scope === 'project' && !oldProject} disabled={!isActive || busy || activeTarget === null} onClick={() => requestScope(activeTarget)}>次に作成するJob</button>}
          </div>
          {onClose !== undefined && <button type="button" className="settings-close" disabled={!isActive || busy} onClick={requestClose}>設定を閉じて戻る</button>}
        </header>
        <p className="settings-scope-note">編集中：{scopeName}。{activeTarget === null && projectOpen && (activeProjectId === null ? '「次に作成するJob」は、「編集を開始」でProjectを作成すると選べます。作成前は全体の初期値を編集できます。' : 'このProjectの設定先を確認できません。')}{state.scope === 'project' && !oldProject && '保存した設定は、このProjectで次に作成するJobに使います。作成済みのJobの設定は変わりません。'}設定の保存だけでは動画・既存の成果物を更新しません。</p>
        {oldProject && <div className="settings-context-warning" role="status">切替前のProjectの設定を保持しています。個別に保存するか、対象を切り替えてください。</div>}
        <WorkspaceLocationSection isActive={isActive} />
        <div className="settings-toolbar">
          <div className="settings-view-switch" role="group" aria-label="設定の表示">
            {([['setting', '項目を編集'], ['outcome', '予定を確認'], ['file', '設定ファイル']] as const).map(([view, label]) => (
              <button key={view} type="button" aria-pressed={detailView === view} aria-controls={`${id}-detail-${view}`} disabled={!isActive || snapshot === null}
                onClick={() => setDetailView(view)}>{label}</button>
            ))}
          </div>
          <div className="settings-page-actions">
            {isDirty && <button type="button" disabled={!isActive} onClick={returnToDraft}>未保存 {changedKeys.length} 項目</button>}
            <button type="button" disabled={!isActive || busy} onClick={() => send(workspace.refresh)}>設定を再読み込み</button>
            <button type="button" disabled={blocked || oldProject} onClick={() => ask({ kind: 'resetAll' })}>初期値へすべて戻す</button>
          </div>
        </div>
        <p className="settings-operation-status" role="status" aria-live={isActive ? 'polite' : 'off'} aria-atomic="true">
          {busy ? '設定を処理しています。' : state.needsRefresh ? '変更を続けるには設定を再読み込みしてください。未保存項目は保持しています。'
            : state.error === null && state.lastCompletedMethod !== null ? completionMessages[state.lastCompletedMethod] : ''}
        </p>
        {state.error !== null && detailView !== 'file' && <div className="settings-error" role="alert"><p>{errorMessages[state.error]}</p><button type="button" disabled={!isActive || busy} onClick={workspace.clearError}>エラーを閉じる</button></div>}
        {snapshot === null ? <p className="settings-empty">{busy ? '設定を読み込んでいます。' : '設定は未取得です。再読み込みを利用できます。'}</p> : (
          <>
            <div className="settings-group-tabs" role="tablist" aria-label="設定グループ" aria-orientation="horizontal"
              onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget)) setFocusedGroup(null) }}>
              {visibleGroups.map(group => <button id={`${id}-tab-${group}`} key={group} ref={element => { tabs.current[group] = element }}
                type="button" role="tab" aria-selected={activeGroup === group} aria-controls={`${id}-panel-${group}`}
                tabIndex={(focusedGroup ?? activeGroup) === group ? 0 : -1} disabled={!isActive}
                onFocus={() => setFocusedGroup(group)} onClick={() => { setActiveGroup(group); setMissingKey(null); setDetailView('setting') }}
                onKeyDown={event => moveGroup(event, group)}>{groupNames[group]}</button>)}
            </div>
            <ArrangeablePanels screenId="settings" className="settings-workspace" panels={[
              { id: 'list', label: '設定の一覧', children: (
              <div className="settings-list-pane">
                {visibleGroups.length === 0 && <p>変更できる設定はありません。</p>}
                {visibleGroups.map(group => <div id={`${id}-panel-${group}`} key={group} role="tabpanel" aria-labelledby={`${id}-tab-${group}`} hidden={activeGroup !== group} tabIndex={0}>
                  {activeGroup === group && (definitions.length === 0 ? <p>この分類に設定はありません。</p> : (
                    <SettingsList key={`${state.projectId ?? 'global'}:${group}:${revealRevision}`} definitions={definitions} values={snapshot.values}
                      selectedKey={selectedKey} changedKeys={changedKeys} disabled={!isActive}
                      onSelect={key => { setSelectedKeys(keys => ({ ...keys, [group]: key })); setMissingKey(null); setDetailView('setting') }} />
                  ))}
                </div>)}
              </div>) },
              { id: 'detail', label: '設定の詳細', children: (
              <div ref={detailPane} className="settings-detail-pane">
                <div id={`${id}-detail-setting`} ref={inspector} hidden={detailView !== 'setting'} inert={!isActive || detailView !== 'setting'} tabIndex={-1} role="region" aria-label="選択した設定の詳細">
                  {definition !== undefined && current !== undefined && initial !== undefined ? (
                    <SettingInspector key={definition.key} definition={definition} value={current} initialValue={initial}
                      draft={Object.hasOwn(draftValues, definition.key) ? draftValues[definition.key] ?? current : current}
                      scope={state.scope} profileVersion={snapshot.profileVersion} disabled={blocked}
                      onChange={value => workspace.changeDraft(definition.key, value)} onSave={() => send(() => workspace.save(definition.key))}
                      onReset={() => { if (blocked) return; if (Object.hasOwn(draftValues, definition.key)) ask({ kind: 'reset', key: definition.key }); else send(() => workspace.reset(definition.key)) }} />
                  ) : fixedTarget !== undefined
                    ? <p>この設定（{fixedTarget.key}）は変更できない固定の値のため、設定画面には表示していません。</p>
                    : <p>指定された設定項目はこの一覧にありません。設定一覧から項目を選択してください。</p>}
                  <details className="settings-provenance">
                    <summary>{snapshot.profileName} v{snapshot.profileVersion} · {snapshot.definitionCount} 項目 · 保存状態の詳細</summary>
                    <dl>
                      <div><dt>対象</dt><dd>{scopeName}</dd></div>
                      <div><dt>保存済みrevision</dt><dd>{snapshot.revision}</dd></div>
                      <div><dt>プロファイル ID</dt><dd><code>{snapshot.profileId}</code></dd></div>
                      <div><dt>プロファイルダイジェスト</dt><dd><code>{snapshot.profileDigest}</code></dd></div>
                      <div><dt>初期値ダイジェスト</dt><dd><code>{snapshot.initialSnapshotDigest}</code></dd></div>
                      <div><dt>保存済み設定ダイジェスト</dt><dd><code>{snapshot.snapshotDigest}</code></dd></div>
                    </dl>
                  </details>
                </div>
                <div id={`${id}-detail-outcome`} hidden={detailView !== 'outcome'} inert={!isActive || detailView !== 'outcome'}>
                  <p className="settings-application-note">動画への再適用機能は未接続です。設定保存済みと生成済みを区別します。</p>
                  <SettingsOutcomePreview mode="snapshot" snapshot={snapshot} draftValues={Object.entries(draftValues).flatMap(([key, value]) => value === undefined ? [] : [{ key, value }])} />
                </div>
                <div id={`${id}-detail-file`} hidden={detailView !== 'file'} inert={!isActive || detailView !== 'file' || oldProject}>
                  {oldProject ? <p>対象を切り替えてから設定ファイルを操作してください。</p>
                    : <SettingsFileActions active={isActive && detailView === 'file'} onReturnToDraft={returnToDraft} />}
                </div>
              </div>) },
            ]} />
          </>
        )}
      </div>
      {isActive && confirmation !== null && <SettingsConfirmation confirmation={confirmation} scopeName={scopeName} restoreName={restoreName}
        blocked={busy || !confirmationIsCurrent} stale={!confirmationIsCurrent} onCancel={cancelConfirmation} onConfirm={confirm} onReturn={returnToDraft} onKeepDrafts={closeWithDrafts} />}
    </section>
  )
}

function SettingsConfirmation({ confirmation, scopeName, restoreName, blocked, stale, onCancel, onConfirm, onReturn, onKeepDrafts }: {
  confirmation: Confirmation; scopeName: string; restoreName: string; blocked: boolean; stale: boolean
  onCancel: () => void; onConfirm: () => void; onReturn: () => void; onKeepDrafts: () => void
}) {
  const id = useId()
  const cancel = useRef<HTMLButtonElement>(null)
  useLayoutEffect(() => { focusVisible(cancel.current) }, [])
  const { action } = confirmation
  const dirty = Object.keys(confirmation.drafts).length > 0
  const label = action.kind === 'scope' ? '設定の対象を切り替えますか？' : action.kind === 'close' ? '未保存の設定があります' : '初期値へ戻しますか？'
  const confirmLabel = action.kind === 'scope' ? '変更を破棄して切り替える' : action.kind === 'close' ? '変更を破棄して閉じる' : '初期値へ戻す'
  return (
    <div className="settings-confirmation-backdrop">
      <section className="settings-confirmation" role="alertdialog" aria-modal="true" aria-labelledby={`${id}-title`} aria-describedby={`${id}-message`}
        onKeyDown={event => {
          if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onCancel() }
          if (event.key !== 'Tab') return
          const controls = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>('button:not(:disabled)'))
          const first = controls[0]
          const last = controls.at(-1)
          if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
          else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
        }}>
        <h3 id={`${id}-title`}>{label}</h3>
        <div id={`${id}-message`}>
          <p>対象：{scopeName}</p>
          {action.kind === 'scope' ? <p>「{action.target === null ? '全体の初期値' : '次に作成するJob'}」へ切り替えます。未保存項目を個別に保存する場合は入力へ戻ってください。</p>
            : action.kind === 'close' ? <p>「未保存のまま戻る」では入力を保持して画面を閉じます。保存はしません。個別に保存する場合は「未保存項目へ戻る」を選んでください。</p>
              : <p>{action.kind === 'resetAll' ? 'すべての設定' : '選択した設定'}を{restoreName}へ戻します。既存の動画や成果物は更新しません。</p>}
          {dirty && <p>{action.kind === 'close' ? '「変更を破棄して閉じる」を選ぶと、未保存の変更を破棄します。' : '未保存の変更を破棄する操作です。'}{action.kind === 'reset' && '他の項目の未保存値は保持します。'}</p>}
          {stale && <p>確認中に設定状態が変わりました。取り消して操作をやり直してください。</p>}
        </div>
        <div className="settings-confirmation-actions">
          {dirty && <button type="button" onClick={onReturn}>未保存項目へ戻る</button>}
          {action.kind === 'close' && <button type="button" disabled={blocked} onClick={onKeepDrafts}>未保存のまま戻る</button>}
          <button type="button" disabled={blocked} onClick={onConfirm}>{confirmLabel}</button>
          <button ref={cancel} type="button" onClick={onCancel}>取消</button>
        </div>
      </section>
    </div>
  )
}

function canonicalProjectId(value: string | null): string | null {
  if (value === null) return null
  try { createSettingsReadRequest(value, value); return value } catch { return null }
}
function isRead(method: SettingsWorkspaceMethod): boolean { return method === 'settings.read' || method === 'settings.global.read' }
function focusVisible(element: HTMLElement | null | undefined, preventScroll = true): boolean {
  if (element === null || element === undefined || !element.isConnected || element.closest('[hidden], [inert]') || element.matches(':disabled')) return false
  element.focus({ preventScroll })
  return document.activeElement === element
}
