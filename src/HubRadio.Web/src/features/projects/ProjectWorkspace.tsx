import { useEffect, useRef } from 'react'

import { isProjectInputDirty } from './projectState'
import { LegacyProjectDraftPanel } from './LegacyProjectDraftPanel'
import { ProjectListCards } from './ProjectListCards'
import {
  useProjectState,
  type ProjectStateContextValue,
} from './ProjectStateProvider'
import { QuickStartPanel } from './QuickStartPanel'

export { getWizardStepAfterKey } from './LegacyProjectDraftPanel'

export interface ProjectWorkspaceProps {
  /** Visibility never closes the draft or restarts the provider's list request. */
  readonly active?: boolean
  /** Optional notification of the persisted identity; the provider remains its owner. */
  readonly onProjectChange?: (projectId: string | null) => void
  readonly onDirtyChange?: (dirty: boolean) => void
  /** Derived by the shell from other editors' unsaved changes, not persisted here. */
  readonly navigationBlocked?: boolean
}

export type ProjectWorkspaceViewProps = ProjectStateContextValue & ProjectWorkspaceProps

export function ProjectWorkspace(props: ProjectWorkspaceProps) {
  return <ProjectWorkspaceView {...useProjectState()} {...props} />
}

export function ProjectWorkspaceView({
  state,
  refresh,
  createDraft,
  loadDraft,
  deleteProject,
  updateInput,
  saveDraft,
  pickSource,
  finalizeDraft,
  closeDraft,
  clearError,
  active = true,
  onProjectChange,
  onDirtyChange,
  navigationBlocked = false,
}: ProjectWorkspaceViewProps) {
  const errorRef = useRef<HTMLDivElement>(null)
  const onProjectChangeRef = useRef(onProjectChange)
  const onDirtyChangeRef = useRef(onDirtyChange)
  const draftId = state.draft?.draftId ?? null
  const projectId = state.draft?.projectId ?? null
  const pending = state.pending !== null
  const dirty = isProjectInputDirty(state.input, state.draft)
  const navigationDisabled = !active || pending || navigationBlocked
  const openDisabled = navigationDisabled || dirty

  useEffect(() => {
    onProjectChangeRef.current = onProjectChange
    onDirtyChangeRef.current = onDirtyChange
  }, [onProjectChange, onDirtyChange])

  useEffect(() => {
    onProjectChangeRef.current?.(projectId)
  }, [projectId])

  useEffect(() => {
    onDirtyChangeRef.current?.(dirty)
  }, [dirty])

  useEffect(() => {
    if (active && state.error !== null) {
      errorRef.current?.focus()
    }
  }, [active, draftId, state.error])

  const openDraft = (id: string): void => {
    if (!openDisabled) loadDraft(id)
  }

  const requestDeleteProject = (id: string, name: string): void => {
    if (openDisabled || !state.canCreate) return
    if (
      !window.confirm(
        `案件「${name}」を削除しますか？\n削除した案件は一覧から消え、アプリから元に戻せません。\nこの案件の作業ファイルと、アプリの保存領域に書き出したファイルも削除されます。選んだ元の動画ファイルは削除されません。`,
      )
    ) {
      return
    }
    deleteProject(id)
  }

  const selectNewSource = (): void => {
    if (!openDisabled && state.canCreate && state.draft === null) {
      // The existing native create operation owns the picker and source-only defaults.
      createDraft()
    }
  }

  const requestCloseDraft = (): void => {
    if (navigationDisabled || state.draft === null) return
    if (dirty && !window.confirm('未保存の変更があります。変更を破棄して一覧へ戻りますか？')) {
      return
    }
    closeDraft()
  }

  const selectSource = (): void => {
    if (!navigationDisabled && state.draft?.isFinalized === false) pickSource()
  }

  const finalizeCurrentDraft = (): void => {
    if (!navigationDisabled && state.draft?.isFinalized === false) finalizeDraft()
  }

  return (
    <div className="project-workspace" hidden={!active} inert={!active}>
      {state.error !== null && (
        <div
          className="error-banner"
          ref={errorRef}
          role="alert"
          tabIndex={-1}
        >
          <div>
            <strong>操作を完了できませんでした</strong>
            <p>{state.error.message}</p>
          </div>
          <div className="error-actions">
            {state.error.code === 'project.revision_conflict' && draftId !== null && (
              <button type="button" disabled={openDisabled} onClick={() => openDraft(draftId)}>
                最新版を再読み込み
              </button>
            )}
            {state.error.code === 'project.runtime_unavailable' && (
              <button
                type="button"
                disabled={navigationDisabled || state.draft?.isFinalized !== false}
                onClick={finalizeCurrentDraft}
              >
                もう一度試す（自動で再確認）
              </button>
            )}
            {state.error.code === 'project.source_reselection_required' && (
              <button
                type="button"
                disabled={navigationDisabled || state.draft?.isFinalized !== false}
                onClick={selectSource}
              >
                元動画を再選択
              </button>
            )}
            <button
              className="quiet-button"
              type="button"
              disabled={!active}
              onClick={() => { if (active) clearError() }}
            >
              閉じる
            </button>
          </div>
        </div>
      )}

      {state.notice !== null && (
        <p className="permission-note" role="status">{state.notice}</p>
      )}

      {navigationBlocked && (
        <p className="permission-note" role="status">
          別の画面に未保存の変更があります。保存または破棄してから動画を切り替えてください。
        </p>
      )}

      {state.draft === null ? (
        <section className="project-panel project-list-panel" aria-labelledby="projects-title">
          <div className="section-heading-row">
            <div>
              <p className="section-kicker">Projects</p>
              <h2 id="projects-title">案件一覧</h2>
              <p>下書きと作成済み案件を、更新が新しい順に表示します。「新しい案件を作成」を押して動画ファイルを1本選ぶと、新しい案件が作成されます。</p>
            </div>
            <div className="section-actions">
              <button
                className="secondary-button"
                type="button"
                disabled={!active || pending}
                onClick={() => { if (active && !pending) refresh() }}
              >
                一覧を更新
              </button>
              <button
                className="primary-button"
                type="button"
                disabled={openDisabled || !state.canCreate}
                onClick={selectNewSource}
              >
                新しい案件を作成
              </button>
            </div>
          </div>

          {!state.canCreate && !pending && state.catalog.outputProfiles.length > 0 && (
            <p className="permission-note">
              案件を作成する権限がありません。一覧の閲覧権限は案件ごとに適用されます。
            </p>
          )}

          {state.projects.length === 0 ? (
            <div className="project-list-cards">
              <div className="empty-state project-list-cards-empty" role="status">
                <strong>案件はまだありません</strong>
                <p>作成権限がある場合は「新しい案件を作成」から始められます。</p>
              </div>
            </div>
          ) : (
            <ProjectListCards
              projects={state.projects}
              disabled={openDisabled}
              onOpen={openDraft}
              onDelete={state.canCreate ? requestDeleteProject : undefined}
            />
          )}
        </section>
      ) : (
        <>
          <div className="section-actions">
            <button
              className="secondary-button"
              type="button"
              disabled={navigationDisabled}
              onClick={requestCloseDraft}
            >
              別の動画を選ぶ
            </button>
          </div>
          {state.draft.sourceOnly !== null ? (
            <QuickStartPanel
              draft={state.draft}
              input={state.input}
              pending={state.pending}
              active={active}
              finalizeDraft={finalizeCurrentDraft}
              closeDraft={requestCloseDraft}
              pickSource={selectSource}
            />
          ) : (
            <LegacyProjectDraftPanel
              state={state}
              active={active}
              updateInput={(input) => { if (active && !pending) updateInput(input) }}
              saveDraft={() => { if (active && !pending) saveDraft() }}
              pickSource={selectSource}
              finalizeDraft={finalizeCurrentDraft}
              closeDraft={requestCloseDraft}
            />
          )}
        </>
      )}
    </div>
  )
}
