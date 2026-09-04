import { useEffect, useRef } from 'react'

import type {
  ProjectDraft,
  ProjectDraftInput,
  SourceOnlyProjectSummary,
  SourceStatus,
} from '../../bridge'
import {
  getProjectInputSourceStatus,
  isProjectInputFinalizable,
  type PendingProjectRequest,
} from './projectState'

export interface QuickStartPanelProps {
  readonly draft: ProjectDraft
  readonly input: ProjectDraftInput
  readonly pending: PendingProjectRequest | null
  readonly finalizeDraft: () => void
  readonly closeDraft: () => void
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
  finalizeDraft,
  closeDraft,
  pickSource,
}: QuickStartPanelProps) {
  const headingRef = useRef<HTMLHeadingElement>(null)
  const sourceOnly = requireSourceOnlySummary(draft.sourceOnly)
  const sourceStatus = getProjectInputSourceStatus(input, draft)
  const isPending = pending !== null
  const canStart = !draft.isFinalized && isProjectInputFinalizable(input, draft)
  const warningCount = sourceStatus === 'available' ? 0 : 1

  useEffect(() => {
    headingRef.current?.focus()
  }, [draft.draftId])

  return (
    <section className="project-panel quick-start-panel" aria-labelledby="quick-start-title">
      <div className="wizard-heading">
        <div>
          <p className="section-kicker">Quick start</p>
          <h2 id="quick-start-title" ref={headingRef} tabIndex={-1}>
            {draft.isFinalized ? '案件の内容' : 'かんたん編集'}
          </h2>
          <p>元動画と固定済みの既定設定を確認して、編集を開始します。</p>
        </div>
        <span className={`state-badge ${draft.isFinalized ? 'finalized' : 'draft'}`}>
          {draft.isFinalized ? '作成済み' : '確認待ち'}
        </span>
      </div>

      <div className="quick-start-source">
        <div>
          <h3>元動画</h3>
          <p>ファイルパスはこの画面へ表示しません。</p>
        </div>
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

      {sourceStatus !== 'available' && !draft.isFinalized && (
        <p className="field-warning" role="status">
          編集を開始するには、元動画をもう一度選択してください。
        </p>
      )}

      <section className="completion-summary" aria-labelledby="quick-start-summary-title">
        <h3 id="quick-start-summary-title">適用する既定設定</h3>
        <p>
          社内限定の版付き設定と標準成果物を、この一回の確認で案件へ固定します。
        </p>
        <dl className="review-list">
          <SummaryItem term="公開範囲" value="社内限定" />
          <SummaryItem term="設定プロファイル版" value={sourceOnly.profileVersion} />
          <SummaryItem term="設定プロファイルdigest" value={sourceOnly.profileDigest} />
          <SummaryItem term="設定snapshot digest" value={sourceOnly.settingsSnapshotDigest} />
          <SummaryItem term="開始前の警告" value={`${warningCount} 件`} />
        </dl>
        <h4>標準成果物</h4>
        <ul className="quick-start-artifacts">
          {sourceOnly.artifactSet.map((artifact) => (
            <li key={artifact}>{artifact}</li>
          ))}
        </ul>
      </section>

      {draft.isFinalized ? (
        <div className="quick-start-actions">
          <button className="quiet-button" type="button" onClick={closeDraft}>
            一覧へ戻る
          </button>
        </div>
      ) : (
        <form
          className="quick-start-actions"
          onSubmit={(event) => {
            event.preventDefault()
            if (canStart && !isPending) {
              finalizeDraft()
            }
          }}
        >
          <button
            className="quiet-button"
            type="button"
            disabled={isPending}
            onClick={closeDraft}
          >
            取消
          </button>
          <button
            className="primary-button"
            type="submit"
            disabled={isPending || !canStart}
          >
            編集を開始
          </button>
        </form>
      )}

      <p className="operation-status" role="status" aria-live="polite">
        {pending === null
          ? draft.isFinalized
            ? 'この案件は作成済みです。'
            : warningCount === 0
              ? '編集を開始できます。'
              : '元動画の再選択を待っています。'
          : '操作を処理しています。'}
      </p>
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
