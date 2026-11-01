import { useEffect, useRef } from 'react'

import type {
  ProjectDraft,
  ProjectDraftInput,
  SourceOnlyProjectSummary,
  SourceStatus,
} from '../../bridge'
import { SettingsOutcomePreview } from '../settings/SettingsOutcomePreview'
import { ProjectActivityNotice } from './ProjectActivityNotice'
import { ProjectNameEditor } from './ProjectNameEditor'
import { SourceMediaDetails } from './SourceMediaDetails'
import {
  getProjectDisplayName,
  getProjectInputSourceStatus,
  isProjectInputFinalizable,
  type PendingProjectRequest,
} from './projectState'
import './projects.css'

export interface QuickStartPanelProps {
  readonly draft: ProjectDraft
  readonly input: ProjectDraftInput
  readonly pending: PendingProjectRequest | null
  /** The owner controls visibility; inactive panels stay mounted without taking focus. */
  readonly active?: boolean
  /** Omitted when the actor cannot edit; the native host still enforces authority. */
  readonly renameProject?: (displayName: string) => void
  readonly finalizeDraft: () => void
  readonly pickSource: () => void
}

const sourceStatusLabels: Readonly<Record<SourceStatus, string>> = {
  notSelected: '未選択',
  available: '選択済み',
  reselectionRequired: '再選択が必要',
}

export function QuickStartPanel({
  draft,
  input,
  pending,
  active = true,
  renameProject,
  finalizeDraft,
  pickSource,
}: QuickStartPanelProps) {
  const headingRef = useRef<HTMLHeadingElement>(null)
  const sourceOnly = requireSourceOnlySummary(draft.sourceOnly)
  const sourceStatus = getProjectInputSourceStatus(input, draft)
  const isPending = pending !== null
  const canStart = !draft.isFinalized && isProjectInputFinalizable(input, draft)
  const warningCount = sourceStatus === 'available' ? 0 : 1
  const contentName = getProjectDisplayName(draft) ?? input.contentName ?? '名称未入力'

  useEffect(() => {
    if (active) {
      headingRef.current?.focus()
    }
  }, [active, draft.draftId])

  return (
    <section className="project-panel quick-start-panel quick-start-compact" aria-labelledby="quick-start-title">
      <div className="wizard-heading">
        <div>
          <p className="section-kicker">Quick start</p>
          <h2 id="quick-start-title" ref={headingRef} tabIndex={-1}>
            {draft.isFinalized ? 'Projectの内容' : 'かんたん編集'}
          </h2>
          {!draft.isFinalized && <p>一回の確認でProjectを作成します。</p>}
        </div>
        <span className={`state-badge ${draft.isFinalized ? 'finalized' : 'draft'}`}>
          {draft.isFinalized ? '作成済み' : '確認待ち'}
        </span>
      </div>

      <div className="quick-start-source">
        <div className="project-card-heading">
          <h3>元動画</h3>
          <p className="project-card-name quick-start-content-name" title={contentName}>
            {contentName}
          </p>
        </div>
        <div className="section-actions">
          <span className="state-badge">社内限定</span>
          <span className={`source-status ${sourceStatus}`}>
            {sourceStatusLabels[sourceStatus]}
          </span>
          {!draft.isFinalized && sourceStatus !== 'available' && (
            <button
              className="secondary-button"
              type="button"
              disabled={isPending}
              onClick={pickSource}
            >
              元動画を選び直す
            </button>
          )}
        </div>
      </div>

      {draft.sourceMedia != null && <SourceMediaDetails media={draft.sourceMedia} />}

      {renameProject !== undefined && (
        <ProjectNameEditor draft={draft} disabled={isPending} onRename={renameProject} />
      )}

      <p className="project-status-note">
        公開範囲は「社内限定」で固定されており、変更できません（外部公開は許可されません）。
        {draft.isFinalized
          ? 'Projectは作成済みです。作成済みのProjectを「確認待ち」に戻すことはできません。'
          : '「確認待ち」は、「編集を開始」を押すと「作成済み」に変わります。'}
      </p>

      {sourceStatus !== 'available' && !draft.isFinalized && (
        <p className="field-warning" role="status">
          編集を開始するには、元動画をもう一度選択してください。
        </p>
      )}

      <SettingsOutcomePreview mode="sourceOnly" sourceOnly={sourceOnly} />

      {draft.autoSettings != null && (
        <section className="quick-start-auto-settings" aria-labelledby="quick-start-auto-settings-title">
          <h3 id="quick-start-auto-settings-title">動画に合わせて自動で設定した項目</h3>
          {draft.autoSettings.length === 0 ? (
            <p>この動画では、標準の設定のままで作成します（変更した設定はありません）。</p>
          ) : (
            <ul>
              {draft.autoSettings.map(item => (
                <li key={item.key} data-setting-key={item.key}>
                  <strong>{item.label}: {item.valueText}</strong>
                  <span>{item.reason}</span>
                </li>
              ))}
            </ul>
          )}
          <p className="field-help">
            ショート動画で一般的に効果があるとされる作り方を、元動画の長さと縦横の向きに合わせて当てはめました。値は設定画面の「次に作成するJob」でいつでも変更できます。
          </p>
        </section>
      )}

      <details className="completion-summary">
        <summary id="quick-start-summary-title">適用する既定設定</summary>
        <dl className="project-card-metadata quick-start-provenance">
          <SummaryItem term="設定プロファイル版" value={sourceOnly.profileVersion} />
          <SummaryItem term="開始前の警告" value={`${warningCount} 件`} />
        </dl>
      </details>

      {draft.isFinalized ? null : (
        <form
          className="quick-start-actions"
          aria-describedby="quick-start-rights"
          onSubmit={(event) => {
            event.preventDefault()
            if (canStart && !isPending) {
              finalizeDraft()
            }
          }}
        >
          <button
            className="primary-button"
            type="submit"
            disabled={isPending || !canStart}
          >
            編集を開始
          </button>
          <p id="quick-start-rights">開始すると、選択した元動画を社内編集に使用する権限があることを確認したものとして記録します。外部公開は許可しません。</p>
        </form>
      )}

      <p className="operation-status" role="status" aria-live="polite">
        {pending === null
          ? draft.isFinalized
            ? 'Projectを作成しました。「ジョブの状態の確認」画面で処理状況を確認できます。'
            : canStart
              ? 'この内容でProjectを作成できます。'
              : sourceStatus !== 'available'
                ? '元動画の再選択を待っています。'
                : 'Projectの作成に必要な情報を確認してください。'
          : '操作を処理しています。'}
      </p>
      {pending !== null && (
        <ProjectActivityNotice key={pending.requestId} method={pending.method} />
      )}
    </section>
  )
}

function requireSourceOnlySummary(
  sourceOnly: SourceOnlyProjectSummary | null,
): SourceOnlyProjectSummary {
  if (sourceOnly === null) {
    throw new Error('Quick start requires a source-only project summary.')
  }

  return sourceOnly
}

function SummaryItem({
  term,
  value,
}: {
  readonly term: string
  readonly value: string
}) {
  return (
    <div>
      <dt>{term}</dt>
      <dd>{value}</dd>
    </div>
  )
}
