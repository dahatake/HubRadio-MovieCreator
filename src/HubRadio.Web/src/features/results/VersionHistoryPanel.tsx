import { useId, useState } from 'react'
import type { PipelineVersionEntry } from '../workflow/pipelineProtocol'
import './results.css'

export interface VersionHistoryPanelProps {
  /** Null until the list has been read; an empty array is a read that found none. */
  readonly versions: readonly PipelineVersionEntry[] | null
  readonly unavailableReason?: string | null
  readonly pending?: boolean
  /** Why restoring is not allowed right now; null when it is allowed. */
  readonly disabledReason?: string | null
  /**
   * Restoring creates a NEW child version and regenerates from it. It never
   * replaces or deletes the current version, a past version or an old artifact.
   */
  readonly onRestore?: (versionId: string, versionDigest: string) => void
}

/**
 * Past versions of this project and the confirmed restore intent. The panel
 * performs no bridge read, no navigation and no automatic selection.
 */
export function VersionHistoryPanel({
  versions, unavailableReason = null, pending = false, disabledReason = null, onRestore,
}: VersionHistoryPanelProps) {
  const headingId = useId()
  const [confirming, setConfirming] = useState<string | null>(null)
  // A list that no longer contains the pending selection must not keep a
  // confirmation open against a version the caller can no longer restore.
  const confirmingId = versions?.some(entry => entry.versionId === confirming) === true ? confirming : null
  const blocked = pending || disabledReason !== null

  return (
    <section className="result-gallery" aria-labelledby={headingId}>
      <header className="result-gallery-header">
        <h2 id={headingId}>過去版</h2>
      </header>
      <p role="status">
        「この版から作り直す」は、選んだ版の構成で<strong>新しい版を作って成果物を作り直します</strong>。
        今の版・過去の版・すでに保存した成果物は消えません。
      </p>
      {unavailableReason !== null && <p role="status">{unavailableReason}</p>}
      {disabledReason !== null && <p role="status">{disabledReason}</p>}
      {unavailableReason === null && versions === null && (
        <p role="status">過去版はまだ取得していません。</p>
      )}
      {unavailableReason === null && versions !== null && versions.length === 0 && (
        <p role="status">過去版の記録はありません。</p>
      )}
      {versions !== null && versions.length > 0 && (
        <ul className="result-gallery-other-list" aria-label="過去版の一覧" role="list">
          {versions.map(entry => (
            <li key={entry.versionId} data-version-id={entry.versionId} data-version-current={entry.isCurrent}>
              <p>
                版 <code>{entry.versionId}</code> · <code>{entry.versionDigest}</code>
              </p>
              <p>
                状態: {entry.phase} · 成果物の記録: {entry.hasResult ? 'あり' : 'なし'}
                {entry.isCurrent ? ' · 現在の版' : ''}
              </p>
              {confirmingId === entry.versionId ? (
                <p>
                  <span>この版から新しい版を作って作り直しますか。今の成果物は残ります。</span>
                  <button
                    type="button"
                    disabled={blocked}
                    onClick={() => {
                      setConfirming(null)
                      onRestore?.(entry.versionId, entry.versionDigest)
                    }}
                  >
                    作り直す
                  </button>
                  <button type="button" onClick={() => setConfirming(null)}>取り消す</button>
                </p>
              ) : (
                <button
                  type="button"
                  disabled={blocked || entry.isCurrent}
                  onClick={() => setConfirming(entry.versionId)}
                >
                  この版から作り直す
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
