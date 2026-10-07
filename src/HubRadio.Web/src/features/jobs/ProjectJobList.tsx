import { useLayoutEffect, useRef, useState } from 'react'
import type { PipelineVersionEntry } from '../workflow/pipelineProtocol'
import { pipelinePhaseLabels } from '../workflow/pipelinePresentation'
import { editableJobSettings, settingLabel, settingValueText, VersionSettings } from '../results/jobSettings'
import { formatJobCreatedAt } from './projectJobs'

/** FR-061.13: copying one Job's settings into 「次に作成するJob」; the caller owns the saving. */
export interface ProjectJobSettingsCopy {
  /** Why copying is not possible now; null when it is. */
  readonly disabledReason: string | null
  /** A copy is being saved; every copy button is disabled. */
  readonly busy: boolean
  /** The outcome of the last copy, shown once under the heading. */
  readonly status: string | null
  readonly onCopy: (entry: PipelineVersionEntry, jobNumber: number) => void
}

/** FR-061: the Jobs of one Project, newest first, each with its own settings. */
export function ProjectJobList({ versions, status, heading = 'このProjectのジョブ', copy }: Readonly<{
  versions: readonly PipelineVersionEntry[] | null
  status: 'idle' | 'loading' | 'available' | 'notFound' | 'conflict' | 'forbidden' | 'unavailable' | 'invalid' | string
  heading?: string
  copy?: ProjectJobSettingsCopy
}>) {
  const [confirming, setConfirming] = useState<string | null>(null)
  const cancel = useRef<HTMLButtonElement>(null)
  const list = useRef<HTMLOListElement>(null)
  // The opener is replaced by the dialog, so focus returns to the re-rendered button.
  const restoreFocus = useRef<string | null>(null)
  // A list that no longer has the Job must not keep a confirmation for it.
  const confirmingId = versions?.some(entry => entry.versionId === confirming) === true ? confirming : null
  useLayoutEffect(() => {
    if (confirmingId !== null) cancel.current?.focus()
    else if (restoreFocus.current !== null) {
      const versionId = restoreFocus.current
      restoreFocus.current = null
      list.current?.querySelector<HTMLButtonElement>(`button[data-copy-version="${versionId}"]`)?.focus()
    }
  }, [confirmingId])
  function close() { restoreFocus.current = confirmingId; setConfirming(null) }

  const count = status === 'available' && versions !== null ? versions.length : 0
  let body
  if (status === 'idle' || status === 'loading' || versions === null && status === 'available') {
    body = <p>ジョブの一覧を取得しています。</p>
  } else if (status === 'notFound' || versions !== null && versions.length === 0 && status === 'available') {
    body = <p>このProjectのジョブはまだありません。</p>
  } else if (status !== 'available' || versions === null) {
    body = <p>ジョブの一覧を取得できませんでした。</p>
  } else {
    body = <ol ref={list} aria-label="ジョブの一覧">
      {versions.map((entry, index) => {
        const jobNumber = versions.length - index
        const items = editableJobSettings(entry)
        return <li key={entry.versionId} data-job-phase={entry.phase}>
          <h3>Job {jobNumber}{entry.isCurrent ? '（いまの版）' : ''}</h3>
          <p>状態: {pipelinePhaseLabels[entry.phase]} · 作成日時: {formatJobCreatedAt(entry.createdAt ?? null)}</p>
          <VersionSettings entry={entry} previous={versions[index + 1]} />
          {copy !== undefined && items.length > 0 && (confirmingId === entry.versionId
            ? <section role="alertdialog" aria-label="Jobの設定を写す確認" onKeyDown={event => {
              if (event.key === 'Escape') { event.preventDefault(); close() }
            }}>
              <p>Job {jobNumber}の設定のうち、Jobごとに違う次の{items.length}項目を「次に作成するJob」に保存します。ほかの項目は、いまの「次に作成するJob」の値のままです。</p>
              <ul>{items.map(item => <li key={item.key}>{settingLabel(item.key)}: {settingValueText(item)}</li>)}</ul>
              <button type="button" disabled={copy.busy || copy.disabledReason !== null} onClick={() => {
                close()
                copy.onCopy(entry, jobNumber)
              }}>保存する</button>
              <button ref={cancel} type="button" onClick={close}>やめる</button>
            </section>
            : <button type="button" aria-label={`Job ${jobNumber}の設定を次のJobに使う`} disabled={copy.busy || copy.disabledReason !== null || confirmingId !== null}
              data-copy-version={entry.versionId} onClick={() => setConfirming(entry.versionId)}>このJobの設定を次のJobに使う</button>)}
        </li>
      })}
    </ol>
  }
  return <section>
    <h2>{heading}{count > 0 ? `（${count}件）` : ''}</h2>
    {copy?.status != null && <p role="status">{copy.status}</p>}
    {copy !== undefined && copy.disabledReason !== null && count > 0 && <p>{copy.disabledReason}</p>}
    {body}
  </section>
}
