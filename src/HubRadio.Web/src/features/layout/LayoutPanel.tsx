import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type FormEvent,
  type SyntheticEvent,
} from 'react'

import {
  SubtitleDisplaySelector,
  SubtitleEditor,
  SubtitleVariantSelector,
} from '../subtitles'
import {
  initialPreviewModeState,
  type PreviewModeState,
} from '../preview-modes/previewModeState'
import { KeyframeEditor } from './KeyframeEditor'
import { LayoutPreview, type LayoutVariantChoice } from './LayoutPreview'
import { ManualLayoutEditor } from './ManualLayoutEditor'
import {
  isCanonicalLayoutEntityId,
  isCanonicalLayoutVersionDigest,
  layoutSnapshotMatchesTarget,
  layoutSupportsManualEditing,
  type LayoutBridgeStatus,
  type LayoutOperation,
  type LayoutSnapshot,
  type LayoutSubtitleEdit,
  type LayoutTeropEdit,
  type LayoutVersionContext,
} from './layoutProtocol'
import { layoutCanAdoptSnapshotVersion, layoutHasPersistedSubtitles, type LayoutVersionChange } from './layoutState'
import {
  useLayoutState,
  type LayoutStateContextValue,
} from './LayoutStateProvider'

const failureMessages: Readonly<
  Record<Exclude<LayoutBridgeStatus, 'available' | 'noChange'>, string>
> = {
  forbidden: '現在の Windows ユーザーには、このレイアウトを閲覧または変更する権限がありません。',
  notFound: '指定した案件のレイアウト aggregate は見つかりませんでした。',
  conflict: 'レイアウトまたは Cue の revision が更新されました。最新版を再読み込みしてください。',
  invalid: '要求または native evidence を閉じた contract として検証できませんでした。',
  unavailable: 'production layout provider は利用できません。利用可能な状態を画面側で補完していません。',
}

export interface LayoutPanelContext {
  readonly projectId: string | null
  /** Both omitted means legacy project selection. A partial/null pair is not a fallback. */
  readonly versionId?: string | null
  readonly versionDigest?: string | null
  /** Generated artifact root, retained when the effective editor adopts a child. */
  readonly root?: LayoutVersionContext
  readonly readMode?: 'latestInBranch' | 'exact'
}

export interface LayoutPanelProps {
  readonly previewModes?: PreviewModeState
  readonly context?: LayoutPanelContext
  readonly active?: boolean
  /** Unapplied inputs or a pending mutation; this does not mean persisted or rendered. */
  readonly onDirtyChange?: (dirty: boolean) => void
}

type LayoutDraftKind = 'manual' | 'keyframes' | 'subtitle' | 'terop'
type CueDraftField = LayoutSubtitleEdit['kind'] | LayoutTeropEdit['kind'] | 'reason' | 'editKind'

interface LayoutDraft {
  readonly kind: LayoutDraftKind
  readonly cueId: string | null
  readonly fields: readonly CueDraftField[]
  /** Evidence used by the mounted form; never replaced by an ACK or refresh. */
  readonly snapshot: LayoutSnapshot
  /** Only an explicit Cue review may change the expected child revision. */
  readonly reviewedSnapshot: LayoutSnapshot | null
}

const draftLabels: Readonly<Record<LayoutDraftKind, string>> = {
  manual: '手動レイアウト',
  keyframes: 'Keyframe track',
  subtitle: '字幕',
  terop: 'テロップ',
}

export function LayoutPanel(props: LayoutPanelProps = {}) {
  return <LayoutPanelView {...useLayoutState()} {...props} />
}

export function LayoutPanelView({
  state,
  changeProjectId,
  loadSnapshot,
  refreshSnapshot,
  invalidatePendingContext,
  adoptSnapshotVersion,
  applyOperation,
  clearError,
  previewModes = initialPreviewModeState,
  context,
  active = true,
  onDirtyChange,
}: LayoutStateContextValue & LayoutPanelProps) {
  const workflow = context !== undefined
  const suppliedProjectId = context?.projectId
  const projectId = typeof suppliedProjectId === 'string' &&
    isCanonicalLayoutEntityId(suppliedProjectId) ? suppliedProjectId : null
  const versionId = context?.versionId
  const versionDigest = context?.versionDigest
  const versioned = context !== undefined && ('versionId' in context || 'versionDigest' in context)
  const target = useMemo<LayoutVersionContext | undefined>(() =>
    projectId !== null && versioned && isCanonicalLayoutEntityId(versionId) &&
      isCanonicalLayoutVersionDigest(versionDigest)
      ? { projectId, versionId, versionDigest } : undefined,
  [projectId, versioned, versionId, versionDigest])
  const rootTarget = context?.root ?? target
  const latestInBranch = versioned && context?.readMode !== 'exact'
  const validContext = projectId !== null && (!versioned || (target !== undefined && rootTarget !== undefined &&
    rootTarget.projectId === projectId && isCanonicalLayoutEntityId(rootTarget.versionId) &&
    rootTarget.versionDigest === target.versionDigest))
  const contextKey = JSON.stringify([String(suppliedProjectId), versioned, String(versionId), String(versionDigest),
    rootTarget?.versionId, rootTarget?.versionDigest, latestInBranch])
  const [dirtyDrafts, setDirtyDrafts] = useState<readonly LayoutDraft[]>([])
  const [draftReset, setDraftReset] = useState(0)
  const [observedSnapshot, setObservedSnapshot] = useState(state.snapshot)
  const [holdTargetSwitch, setHoldTargetSwitch] = useState(false)
  const panelRef = useRef<HTMLElement | null>(null)
  const requestedContextRef = useRef<string | null>(null)
  const submittedDraftRef = useRef<LayoutOperation | null>(null)
  const refreshingDraftsRef = useRef(false)
  const onDirtyChangeRef = useRef(onDirtyChange)
  onDirtyChangeRef.current = onDirtyChange

  const pending = state.pending !== null
  const dirty = dirtyDrafts.length > 0 || state.pending?.method === 'layout.apply'
  const currentSnapshot = state.snapshot
  const recoveringApplyBase = currentSnapshot !== null &&
    currentSnapshot.projectId === state.projectIdInput &&
    (state.status === 'conflict' || state.status === 'invalid')
  // Aggregate revision alone may advance safely; changed editor evidence must
  // never be silently rebased onto the provider's new expectedRevision.
  const needsReload = currentSnapshot !== null
    ? dirtyDrafts.filter((draft) => recoveringApplyBase ||
      draftContextKey(draft.kind, draft.reviewedSnapshot ?? draft.snapshot, draft.cueId) !==
        draftContextKey(draft.kind, currentSnapshot, draft.cueId))
    : []
  const canRefresh = !dirty || recoveringApplyBase || needsReload.length > 0
  const selectedVersionMatches = target !== undefined && state.target != null &&
    state.target.projectId === target.projectId && state.target.versionId === target.versionId &&
    state.target.versionDigest === target.versionDigest
  const adoptingChild = !selectedVersionMatches && target !== undefined &&
    layoutCanAdoptSnapshotVersion(state, target)
  const targetChanged = workflow && (
    !validContext || state.projectIdInput !== projectId ||
    (state.snapshot !== null && state.snapshot.projectId !== projectId) ||
    (state.pending !== null && state.pending.projectId !== projectId) ||
    (versioned && (state.latestInBranch === true) !== latestInBranch) ||
    (versioned ? target === undefined || (!selectedVersionMatches && !adoptingChild &&
      !(state.target == null && state.snapshot !== null && layoutSnapshotMatchesTarget(state.snapshot, target)))
      : state.target != null)
  )
  const matchesTarget = !workflow || (validContext && !targetChanged)
  const subtitlesPersisted = matchesTarget && layoutHasPersistedSubtitles(state)
  const targetSwitchBlocked = targetChanged && (pending || dirty || holdTargetSwitch)
  const failureStatus = matchesTarget && state.status !== null &&
    state.status !== 'available' &&
    state.status !== 'noChange'
    ? state.status
    : null

  useEffect(() => {
    onDirtyChangeRef.current?.(dirty)
  }, [dirty])

  useEffect(() => () => onDirtyChangeRef.current?.(false), [])

  useLayoutEffect(() => {
    if (adoptingChild && target !== undefined) adoptSnapshotVersion?.(target)
    if (targetChanged && pending) {
      requestedContextRef.current = null
      invalidatePendingContext?.()
    }
  }, [adoptSnapshotVersion, adoptingChild, invalidatePendingContext, pending, target, targetChanged])

  useEffect(() => {
    if (!pending) refreshingDraftsRef.current = false
  }, [pending])

  useEffect(() => {
    if (!targetChanged) setHoldTargetSwitch(false)
    else if (pending || dirty) setHoldTargetSwitch(true)
  }, [dirty, pending, targetChanged])

  useEffect(() => {
    const submitted = submittedDraftRef.current
    if (pending || submitted === null) return
    submittedDraftRef.current = null
    if (
      !targetChanged && state.error === null && state.lastCompletedMethod === 'layout.apply' &&
      (state.status === 'available' || state.status === 'noChange')
    ) {
      const kind = draftKindForOperation(submitted)
      const cueId = 'cueId' in submitted ? submitted.cueId : null
      setDirtyDrafts((current) => current.flatMap((draft) => {
        if (draft.kind !== kind || draft.cueId !== cueId) return [draft]
        if (submitted.kind === 'editSubtitle' || submitted.kind === 'editTerop') {
          const fields = draft.fields.filter((field) => field !== submitted.edit.kind)
          // Reason and edit-kind are shared by the bound Cue form. Retain both
          // while any other field still has unsent input, including on noChange.
          if (fields.some((field) => field !== 'reason' && field !== 'editKind')) {
            return [{ ...draft, fields }]
          }
        }
        return []
      }))
    }
  }, [pending, state, targetChanged])

  useEffect(() => {
    if (!workflow) {
      requestedContextRef.current = null
      return
    }
    if (
      !targetChanged && state.pending?.method === 'layout.read' && !state.pending.invalidated
    ) requestedContextRef.current = contextKey
    if (!active || pending || dirty || targetSwitchBlocked) return

    // The provider's loadSnapshot closes over projectIdInput. Change the input
    // first, then use the next committed render's function, never the old closure.
    // Deferring also lets provider setup / StrictMode replay finish before sending.
    let cancelled = false
    queueMicrotask(() => {
      if (cancelled) return
      if (!validContext) {
        requestedContextRef.current = null
        if (state.projectIdInput !== '') changeProjectId('')
        return
      }
      if (state.projectIdInput !== projectId) {
        requestedContextRef.current = null
        changeProjectId(projectId!)
        return
      }
      if (requestedContextRef.current === contextKey) return
      requestedContextRef.current = contextKey
      if (targetChanged || state.snapshot?.projectId !== projectId || (versioned && state.target == null)) {
        loadSnapshot(latestInBranch ? rootTarget : target, latestInBranch ? { latestInBranch: true } : undefined)
      }
    })
    return () => { cancelled = true }
  }, [
    active, changeProjectId, contextKey, dirty, latestInBranch, loadSnapshot, pending, projectId, rootTarget,
    state.pending, state.projectIdInput, state.snapshot, state.target, target, targetChanged,
    targetSwitchBlocked, validContext, versioned, workflow,
  ])

  useEffect(() => {
    const panel = panelRef.current
    const focused = panel?.ownerDocument.activeElement
    if (
      focused instanceof HTMLElement && panel?.contains(focused) &&
      (!active || (!matchesTarget && focused.closest('.layout-workspace') !== null))
    ) focused.blur()
  }, [active, matchesTarget])

  const markDraftChanged = useCallback((
    kind: LayoutDraftKind,
    cueId: string | null = null,
    field?: CueDraftField,
  ): void => {
    const snapshot = state.snapshot
    if (snapshot === null) return
    setDirtyDrafts((current) => {
      const existing = current.find((draft) => draft.kind === kind && draft.cueId === cueId)
      if (existing !== undefined) {
        if (field === undefined || existing.fields.includes(field)) return current
        return current.map((draft) => draft === existing
          ? { ...draft, fields: [...draft.fields, field] } : draft)
      }
      return [...current, {
        kind, cueId, snapshot, fields: field === undefined ? [] : [field], reviewedSnapshot: null,
      }]
    })
  }, [state.snapshot])

  const submitOperation: LayoutStateContextValue['applyOperation'] = (operation) => {
    if (
      !active || !matchesTarget || pending || recoveringApplyBase ||
      currentSnapshot === null || submittedDraftRef.current !== null
    ) return
    const kind = draftKindForOperation(operation)
    const cueId = 'cueId' in operation ? operation.cueId : null
    if (needsReload.some((draft) => draft.kind === kind && draft.cueId === cueId)) return
    const draft = dirtyDrafts.find((item) => item.kind === kind && item.cueId === cueId)
    let reviewedOperation = operation
    if (
      draft?.reviewedSnapshot != null &&
      (operation.kind === 'editSubtitle' || operation.kind === 'editTerop')
    ) {
      const cues = operation.kind === 'editSubtitle'
        ? draft.reviewedSnapshot.subtitles : draft.reviewedSnapshot.terops
      const cue = cues.find((item) => item.cueId === operation.cueId)
      if (cue === undefined) return
      // No field values are merged. Only the explicitly reviewed base changes.
      reviewedOperation = {
        ...operation, expectedRevisionId: cue.revisionId, expectedRevisionDigest: cue.revisionDigest,
      }
    }
    markDraftChanged(kind, cueId, 'edit' in operation ? operation.edit.kind : undefined)
    submittedDraftRef.current = reviewedOperation
    applyOperation(reviewedOperation)
  }

  const reviewCueDraft = (draft: LayoutDraft): void => {
    if (
      !active || !matchesTarget || pending || recoveringApplyBase ||
      submittedDraftRef.current !== null || currentSnapshot === null ||
      !canReviewCueDraft(draft, currentSnapshot, state.lastVersionChange)
    ) return
    setDirtyDrafts((current) => current.map((item) => item === draft
      ? { ...item, reviewedSnapshot: currentSnapshot } : item))
  }

  const discardInputs = (): void => {
    if (!active || pending || submittedDraftRef.current !== null) return
    submittedDraftRef.current = null
    setDirtyDrafts([])
    setDraftReset((current) => current + 1)
    setHoldTargetSwitch(false)
  }

  const blockUnavailableInteraction = (event: SyntheticEvent<HTMLElement>): void => {
    const inWorkspace = event.target instanceof Element &&
      event.target.closest('.layout-workspace') !== null
    const hiddenCue = event.target instanceof Element &&
      event.target.closest('.cue-editor-slot[hidden]') !== null
    const staleSubmit = inWorkspace && event.type === 'submit' &&
      (recoveringApplyBase || needsReload.some((draft) =>
        draft.kind === draftKindForControl(event.target) &&
        draft.cueId === cueIdForControl(event.target)))
    // The ref closes the interval before React commits pending/disabled, also
    // covering future controls without enumerating their individual fields.
    if (
      active && !staleSubmit && !hiddenCue &&
      (!inWorkspace || (matchesTarget && !pending && submittedDraftRef.current === null))
    ) return
    if (
      active && matchesTarget && !hiddenCue &&
      (event.type === 'focus' ||
        (event.type === 'keydown' && 'key' in event && event.key === 'Tab'))
    ) return
    event.preventDefault()
    event.stopPropagation()
    if (event.type === 'focus' && event.target instanceof HTMLElement) event.target.blur()
  }

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    if (active && !workflow && !pending && !dirty && submittedDraftRef.current === null) loadSnapshot()
  }

  const refresh = (): void => {
    if (active && matchesTarget && !pending && canRefresh && submittedDraftRef.current === null) {
      refreshingDraftsRef.current = dirty
      // Provider retains the original root for latest reads and the effective
      // exact version for warning reads, without replacing mounted draft bases.
      refreshSnapshot(workflow && state.target == null ? (latestInBranch ? rootTarget : target) : undefined,
        latestInBranch ? { latestInBranch: true } : undefined)
    }
  }

  // Controlled standalone reads intentionally replace inputs. Own apply ACKs
  // and explicit draft-preserving refreshes must not change the workspace key.
  if (observedSnapshot !== currentSnapshot) {
    setObservedSnapshot(currentSnapshot)
    if (
      !workflow && currentSnapshot !== null && state.lastCompletedMethod === 'layout.read' &&
      !refreshingDraftsRef.current
    ) {
      setDirtyDrafts([])
      setDraftReset((current) => current + 1)
    }
  }

  return (
    <section
      ref={panelRef}
      className="layout-panel"
      aria-labelledby="layout-title"
      hidden={!active}
      inert={!active}
      style={{ display: active ? undefined : 'none', minHeight: 0, overflow: 'auto' }}
      data-layout-status={matchesTarget ? state.status ?? 'idle' : 'idle'}
      data-layout-dirty={dirty ? 'true' : 'false'}
      onClickCapture={blockUnavailableInteraction}
      onKeyDownCapture={blockUnavailableInteraction}
      onChangeCapture={blockUnavailableInteraction}
      onInputCapture={blockUnavailableInteraction}
      onSubmitCapture={blockUnavailableInteraction}
      onFocusCapture={blockUnavailableInteraction}
    >
      <div className="layout-heading">
        <div>
          <p className="eyebrow">Exact native composition evidence</p>
          <h2 id="layout-title">レイアウト・字幕・テロップ</h2>
          <p>
            native の LayoutResolver、SubtitleLayoutEngine、Cue revision evidence を同じ aggregate revision で編集します。
          </p>
        </div>
        {matchesTarget && state.snapshot !== null && (
          <span className="layout-evidence-badge">
            revision {state.snapshot.revision}
            {recoveringApplyBase ? '・旧根拠（再確認が必要）' : ''}
            {subtitlesPersisted ? '・字幕変更は保存済み・その他は未永続化' : '・未永続化'}
          </span>
        )}
      </div>

      {!workflow ? (
        <form className="layout-load-form" onSubmit={submit}>
          <label htmlFor="layout-project-id">確定済みプロジェクト ID</label>
          <div className="layout-load-row">
            <input
              id="layout-project-id"
              type="text"
              autoComplete="off"
              spellCheck={false}
              minLength={36}
              maxLength={36}
              pattern="[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
              value={state.projectIdInput}
              disabled={!active || pending || dirty}
              aria-describedby="layout-project-help"
              onChange={(event) => {
                if (active && !pending && !dirty) changeProjectId(event.currentTarget.value)
              }}
            />
            <button
              className="primary-button"
              type="submit"
              disabled={!active || pending || dirty || !isCanonicalLayoutEntityId(state.projectIdInput)}
            >
              レイアウトを開く
            </button>
            {state.snapshot !== null && (
              <button
                className="secondary-button"
                type="button"
                disabled={!active || pending || !canRefresh}
                onClick={refresh}
              >
                最新版を再読み込み
              </button>
            )}
          </div>
          <p id="layout-project-help" className="field-help">
            小文字 UUID を指定します。authorization と project correlation は native 側でも再検証されます。
          </p>
        </form>
      ) : (
        <div className="layout-context">
          <p>
            {projectId === null
              ? suppliedProjectId == null
                ? '字幕・画角を編集する案件を選択してください。'
                : '選択した案件 ID を確認できません。'
              : !validContext
                ? '版 ID と digest の両方を確認できるまで読み込みません。'
                : versioned
                  ? '選択した案件・版 ID・digest に結び付いた native 根拠を使用します。'
                  : '選択した案件の native レイアウトを使用します。別の版は画面側で補完しません。'}
          </p>
          {projectId !== null && (
            <button
              className="secondary-button"
              type="button"
              disabled={!active || !matchesTarget || pending || !canRefresh || targetSwitchBlocked}
              onClick={refresh}
            >
              最新版を再読み込み
            </button>
          )}
        </div>
      )}

      {targetSwitchBlocked ? (
        <div className="layout-target-guard" role={active ? 'alert' : undefined}>
          <p>
            処理中または未適用の入力があるため、案件・版の切り替えを保留しています。
            元の案件・版に戻るか、処理の完了後に入力の破棄を確認して切り替えてください。
          </p>
          <button type="button" disabled={!active || pending} onClick={discardInputs}>
            入力を破棄して切り替える
          </button>
        </div>
      ) : dirty ? (
        <button
          className="quiet-button"
          type="button"
          disabled={!active || pending}
          onClick={discardInputs}
        >
          未適用の入力を取り消す
        </button>
      ) : null}

      {matchesTarget && needsReload.length > 0 && (
        <div
          className="layout-draft-guard"
          role={active ? 'alert' : undefined}
          data-layout-needs-reload={[...new Set(needsReload.map((draft) => draft.kind))].join(' ')}
        >
          <strong>{recoveringApplyBase
            ? '適用を確認できないため、旧根拠の入力を保持して再適用を停止しています'
            : '編集元が更新されたため、該当する入力は適用できません'}</strong>
          <ul>
            {needsReload.map((draft) => (
              <li key={`${draft.kind}-${draft.cueId ?? ''}`} data-layout-draft-cue={draft.cueId ?? undefined}>
                {draftLabels[draft.kind]}: 編集開始時 revision {draft.snapshot.revision}
                {recoveringApplyBase ? ' → 現在の revision は未確認' : ` → 現在 revision ${state.snapshot?.revision}`}
                {!recoveringApplyBase && currentSnapshot !== null && (
                  <details>
                    <summary>編集開始時と現在の根拠を比較</summary>
                    <p>編集開始時</p>
                    <pre>{draftContextKey(draft.kind, draft.snapshot, draft.cueId)}</pre>
                    <p>現在（入力値は自動で統合しません）</p>
                    <pre>{draftContextKey(draft.kind, currentSnapshot, draft.cueId)}</pre>
                    {canReviewCueDraft(draft, currentSnapshot, state.lastVersionChange) && (
                      <button
                        type="button"
                        disabled={!active || pending}
                        onClick={() => reviewCueDraft(draft)}
                      >
                        この Cue の最新版を確認して入力を引き継ぐ
                      </button>
                    )}
                  </details>
                )}
              </li>
            ))}
          </ul>
          <p>
            該当する一覧・入力欄は編集開始時の根拠と入力を保持しています。
            {recoveringApplyBase
              ? 'プレビューも旧根拠です。「最新版を再読み込み」は入力を保持して根拠だけを取得します。'
              : 'プレビューは最新版です。同じ構成・配置の既存 Cue は、根拠の比較後に明示的に入力を引き継げます。'}
            入力を控えたうえで「未適用の入力を取り消す」で最新版を確認し、編集し直してください。
            この取消操作はすべての未適用の入力を破棄します。
          </p>
        </div>
      )}

      {matchesTarget && currentSnapshot !== null &&
        (failureStatus === null || recoveringApplyBase) &&
        dirtyDrafts.filter((draft) => draft.reviewedSnapshot !== null).map((draft) => (
        <p className="layout-reviewed-draft" key={`${draft.kind}-${draft.cueId}`}>
          {draftLabels[draft.kind]}: 編集開始時 revision {draft.snapshot.revision} の入力を保持・
          明示確認済み revision {draft.reviewedSnapshot?.revision}
        </p>
      ))}

      {matchesTarget && state.error !== null && (
        <div className="layout-error" role={active ? 'alert' : undefined}>
          <div>
            <strong>レイアウト操作を開始できませんでした</strong>
            <p>{state.error.message}</p>
          </div>
          <button className="quiet-button" type="button" disabled={!active} onClick={clearError}>
            閉じる
          </button>
        </div>
      )}

      <p
        className="layout-operation-status"
        role={active ? 'status' : undefined}
        aria-live={active ? 'polite' : 'off'}
        data-layout-pending={pending ? 'true' : 'false'}
      >
        {pending
          ? state.pending?.method === 'layout.apply'
            ? '同じ aggregate revision に対する変更を native 側で検証しています。'
            : 'レイアウト aggregate を native 側で検証しています。'
          : workflow && (!matchesTarget || state.status === null)
            ? '選択した案件の根拠を確認するまで、レイアウトは表示しません。'
            : statusMessage(state.status, state.lastCompletedMethod)}
      </p>

      {failureStatus !== null && (
        <div className="layout-failure" role={active ? 'alert' : undefined}>
          <strong>{recoveringApplyBase ? '変更後のレイアウトを確認できません' : 'レイアウト aggregate を利用できません'}</strong>
          <p>{failureMessages[failureStatus]}</p>
        </div>
      )}
      {state.snapshot === null ? failureStatus === null && (
        <div className="layout-empty">
          <strong>レイアウトは未選択です</strong>
          <p>プロジェクトを読み込むまで、mode、座標、Cue、variant evidence は表示しません。</p>
        </div>
      ) : (failureStatus === null || recoveringApplyBase) && (
        <LayoutWorkspace
          key={`${state.snapshot.projectId}-${draftReset}`}
          snapshot={state.snapshot}
          subtitlesPersisted={subtitlesPersisted}
          drafts={dirtyDrafts}
          active={active && matchesTarget}
          disabled={!active || !matchesTarget || pending}
          applyOperation={submitOperation}
          previewModes={previewModes}
          onDraftChange={markDraftChanged}
        />
      )}
    </section>
  )
}

function LayoutWorkspace({
  snapshot,
  subtitlesPersisted,
  drafts,
  active,
  disabled,
  applyOperation,
  previewModes,
  onDraftChange,
}: Readonly<{
  snapshot: LayoutSnapshot
  subtitlesPersisted: boolean
  drafts: readonly LayoutDraft[]
  active: boolean
  disabled: boolean
  applyOperation: LayoutStateContextValue['applyOperation']
  previewModes: PreviewModeState
  onDraftChange: (kind: LayoutDraftKind, cueId?: string | null, field?: CueDraftField) => void
}>) {
  const [variantChoice, setVariantChoice] = useState<LayoutVariantChoice>(null)
  const [displayProfileDigest, setDisplayProfileDigest] = useState<string | null>(
    () => initialDisplayProfileDigest(snapshot),
  )
  const manualSnapshot = drafts.find((draft) => draft.kind === 'manual')?.snapshot ?? snapshot
  const keyframeSnapshot = drafts.find((draft) => draft.kind === 'keyframes')?.snapshot ?? snapshot
  const subtitleSnapshot = drafts.find((draft) => draft.kind === 'subtitle')?.snapshot ?? snapshot
  const teropSnapshot = drafts.find((draft) => draft.kind === 'terop')?.snapshot ?? snapshot
  const subtitles = [...snapshot.subtitles]
  const terops = [...snapshot.terops]
  for (const draft of drafts) {
    if (draft.kind === 'subtitle') {
      const cue = draft.snapshot.subtitles.find((item) => item.cueId === draft.cueId)
      if (cue === undefined) continue
      const index = subtitles.findIndex((item) => item.cueId === cue.cueId)
      if (index < 0) subtitles.push(cue)
      else subtitles[index] = cue
    } else if (draft.kind === 'terop') {
      const cue = draft.snapshot.terops.find((item) => item.cueId === draft.cueId)
      if (cue === undefined) continue
      const index = terops.findIndex((item) => item.cueId === cue.cueId)
      if (index < 0) terops.push(cue)
      else terops[index] = cue
    }
  }
  const activeDisplay = snapshot.subtitleDisplays.find(
    (display) => display.profileVersionDigest === displayProfileDigest,
  ) ?? null
  const manualEditable = layoutSupportsManualEditing(snapshot)
  const requiresKeyframes =
    snapshot.manualSelection?.status === 'manualKeyframesRequired'
  const unresolvedSubtitles = snapshot.subtitles.filter(
    (cue) => cue.layoutResolution.status === 'manualAdjustmentRequired',
  ).length
  const graphicRegionNames = teropSnapshot.layout.regions
    .filter((region) => region.role === 'graphic')
    .map((region) => region.name)

  const changeDisplayProfile = (digest: string): void => {
    setDisplayProfileDigest(digest)
    if (snapshot.variantPair?.displayProfileDigest !== digest) {
      setVariantChoice(null)
    }
  }

  const changeVariant = (
    choice: Exclude<LayoutVariantChoice, null>,
  ): void => {
    setVariantChoice(choice)
    if (snapshot.variantPair !== null) {
      setDisplayProfileDigest(snapshot.variantPair.displayProfileDigest)
    }
  }

  return (
    <div
      className="layout-workspace"
      hidden={!active}
      inert={disabled}
      style={{ display: active ? undefined : 'none' }}
      onChangeCapture={(event) => {
        const kind = draftKindForControl(event.target)
        if (!disabled && kind !== null) {
          onDraftChange(kind, cueIdForControl(event.target), cueFieldForControl(event.target, kind))
        }
      }}
      onClickCapture={(event) => {
        if (!disabled && isManualModeControl(event.target)) onDraftChange('manual')
      }}
      onKeyDownCapture={(event) => {
        if (
          !disabled && isManualModeControl(event.target) &&
          ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)
        ) onDraftChange('manual')
      }}
    >
      <div className="layout-persistence-notice" role="note">
        <strong>{subtitlesPersisted ? '字幕変更は保存済み／動画は変更後未生成' : '未永続化'}</strong>
        <span>
          {subtitlesPersisted
            ? '保存済みなのは検証済みの字幕変更だけです。未適用の入力は含みません。以前の成果物はそのまま保持されます。'
            : '字幕変更の保存証跡を確認できないため、保存済みとは表示しません。'}
          手動レイアウト・Keyframe・テロップの変更はメモリ内のみ（未永続化）です。
        </span>
      </div>
      {(requiresKeyframes || unresolvedSubtitles > 0) && (
        <div className="layout-manual-required" role="alert">
          <strong>手動調整が必要です</strong>
          <p>
            {requiresKeyframes ? 'manual keyframe が必要です。' : ''}
            {unresolvedSubtitles > 0
              ? ` 自動配置の解がない字幕が ${unresolvedSubtitles} 件あります。`
              : ''}
            自動 fallback は適用しません。
          </p>
        </div>
      )}
      <dl className="layout-identity">
        <div><dt>Composition version</dt><dd><code>{snapshot.compositionVersionId}</code></dd></div>
        <div><dt>Composition digest</dt><dd><code>{snapshot.compositionVersionDigest}</code></dd></div>
        <div><dt>Layout spec</dt><dd><code>{snapshot.layout.layoutSpecId}</code></dd></div>
        <div><dt>Output</dt><dd>{snapshot.output.width} × {snapshot.output.height}（{snapshot.output.aspectWidth}:{snapshot.output.aspectHeight}）</dd></div>
      </dl>
      {!manualEditable && (
        <p className="layout-subtitle-only-notice" role="note">
          字幕専用の native 根拠です。映像の画角・手動レイアウト・Keyframe は編集できません。
        </p>
      )}

      <div
        className="layout-preview-grid"
        data-mobile-preview={String(previewModes.mobilePreview)}
      >
        <LayoutPreview
          snapshot={snapshot}
          variantChoice={variantChoice}
          subtitleDisplay={activeDisplay}
          previewModes={previewModes}
        />
        <div className="layout-preview-controls">
          <SubtitleDisplaySelector
            displays={snapshot.subtitleDisplays}
            activeDigest={displayProfileDigest}
            disabled={disabled}
            onChange={changeDisplayProfile}
          />
          <SubtitleVariantSelector
            variantPair={snapshot.variantPair}
            choice={variantChoice}
            disabled={disabled}
            onChange={changeVariant}
          />
        </div>
      </div>

      {manualEditable && <ManualLayoutEditor
        // Sibling editors may share a revision, but must not share a React key.
        key={`manual-${manualSnapshot.compositionVersionId}-${manualSnapshot.revision}`}
        snapshot={manualSnapshot}
        disabled={disabled}
        applyOperation={applyOperation}
      />}
      {manualEditable && <KeyframeEditor
        key={`keyframes-${keyframeSnapshot.compositionVersionId}-${keyframeSnapshot.revision}`}
        snapshot={keyframeSnapshot}
        disabled={disabled}
        applyOperation={applyOperation}
      />}
      <SubtitleEditor
        // Form-only inputs: retain each dirty Cue independently so its
        // revision-keyed child cannot remount on another Cue/field's ACK.
        // Preview and native requests still use the actual current aggregate.
        snapshot={{
          ...snapshot,
          layout: subtitleSnapshot.layout,
          subtitles,
          terops,
        }}
        disabled={disabled}
        applyOperation={applyOperation}
        graphicRegionNames={graphicRegionNames}
      />
    </div>
  )
}

function draftContextKey(kind: LayoutDraftKind, snapshot: LayoutSnapshot, cueId: string | null): string {
  const context = [
    snapshot.projectId,
    snapshot.compositionVersionId,
    snapshot.compositionVersionDigest,
    snapshot.subtitleOnly,
    snapshot.output,
    snapshot.layout,
  ]
  const evidence = kind === 'manual'
    ? [snapshot.manualSelection, snapshot.sources, snapshot.templates]
    : kind === 'keyframes'
      ? [snapshot.keyframeTracks, snapshot.sources, snapshot.manualSelection]
      : kind === 'subtitle'
        ? snapshot.subtitles.find((cue) => cue.cueId === cueId)
        : snapshot.terops.find((cue) => cue.cueId === cueId)
  return JSON.stringify([context, evidence])
}

function canReviewCueDraft(
  draft: LayoutDraft,
  snapshot: LayoutSnapshot,
  versionChange?: LayoutVersionChange | null,
): boolean {
  if (draft.kind !== 'subtitle' && draft.kind !== 'terop') return false
  const original = draft.snapshot
  const reviewed = draft.reviewedSnapshot ?? original
  const sameVersion = reviewed.compositionVersionId === snapshot.compositionVersionId &&
    reviewed.compositionVersionDigest === snapshot.compositionVersionDigest
  const acknowledgedChild = versionChange != null &&
    layoutSnapshotMatchesTarget(reviewed, versionChange.source) &&
    layoutSnapshotMatchesTarget(snapshot, versionChange.created)
  const cues = draft.kind === 'subtitle' ? snapshot.subtitles : snapshot.terops
  const previousCue = draft.kind === 'subtitle'
    ? reviewed.subtitles.find((cue) => cue.cueId === draft.cueId) : undefined
  return original.projectId === snapshot.projectId &&
    (sameVersion || acknowledgedChild) && original.subtitleOnly === snapshot.subtitleOnly &&
    JSON.stringify([original.output, original.layout]) === JSON.stringify([snapshot.output, snapshot.layout]) &&
    cues.some((cue) => cue.cueId === draft.cueId &&
      (cue.compositionVersionId === snapshot.compositionVersionId ||
        (previousCue !== undefined && JSON.stringify(cue) === JSON.stringify(previousCue))))
}

function draftKindForOperation(operation: LayoutOperation): LayoutDraftKind {
  return operation.kind === 'setManualLayout' ? 'manual'
    : operation.kind === 'setKeyframes' ? 'keyframes'
      : operation.kind === 'editSubtitle' ? 'subtitle' : 'terop'
}

function cueIdForControl(target: EventTarget): string | null {
  return target instanceof Element
    ? target.closest('form')?.getAttribute('data-layout-cue-id') ?? null : null
}

function cueFieldForControl(target: EventTarget, kind: LayoutDraftKind): CueDraftField | undefined {
  if (!(target instanceof Element) || (kind !== 'subtitle' && kind !== 'terop')) return undefined
  const name = target.getAttribute('name')
  if (name === `${kind}-reason`) return 'reason'
  if (name === `${kind}-edit-kind`) return 'editKind'
  const editKind = target.closest('form')?.querySelector<HTMLSelectElement>(`[name="${kind}-edit-kind"]`)?.value
  return editKind === 'text' || editKind === 'timing' || editKind === 'style' ||
    editKind === 'placement' || editKind === 'region' ? editKind : undefined
}

function draftKindForControl(target: EventTarget): LayoutDraftKind | null {
  if (!(target instanceof Element)) return null
  const form = target.closest('form')
  if (form?.matches('.manual-layout-form')) return 'manual'
  if (form?.matches('.keyframe-form')) return 'keyframes'
  if (form?.matches('.cue-edit-form')) {
    return form.querySelector('[name="subtitle-edit-kind"]') !== null ? 'subtitle' : 'terop'
  }
  return null
}

function isManualModeControl(target: EventTarget): boolean {
  return target instanceof Element &&
    target.closest('.manual-layout-form [role="radio"]') !== null
}

function initialDisplayProfileDigest(snapshot: LayoutSnapshot): string | null {
  return snapshot.variantPair?.displayProfileDigest ?? null
}

function statusMessage(
  status: LayoutBridgeStatus | null,
  method: LayoutStateContextValue['state']['lastCompletedMethod'],
): string {
  if (status === null) {
    return 'プロジェクトを指定してレイアウト aggregate を読み込んでください。'
  }
  if (status === 'available') {
    return method === 'layout.apply'
      ? '変更後の immutable snapshot を native 側で検証しました。'
      : 'レイアウト aggregate を native 側で検証しました。'
  }
  if (status === 'noChange') {
    return '変更内容は現行 evidence と同一でした。現行 snapshot を維持します。'
  }
  return 'native 側の検証は閉じた失敗として完了しました。'
}
