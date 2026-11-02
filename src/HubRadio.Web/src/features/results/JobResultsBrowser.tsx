import { Fragment, useId, useRef, useState, type ReactNode } from 'react'
import { projectThumbnailUrl } from '../../bridge'
import { formatJobCreatedAt } from '../jobs/projectJobs'
import { isJobSelectable, JobThumbnail, jobThumbnailStatus } from '../jobs/JobThumbnail'
import { pipelinePhaseLabels } from '../workflow/pipelinePresentation'
import type { PipelineVersionEntry, PipelineVersionOutput } from '../workflow/pipelineProtocol'
import { changedJobSettingKeys, editableJobSettings, settingLabel, settingValueText, VersionSettings } from './jobSettings'
import { pipelineArtifactLabels } from './ResultGallery'
import type { JobExportService } from './jobExportProtocol'
import { JobExportPanel, jobExportMessages, type JobExportPanelProps } from './JobExportPanel'
import type { DisclosureService } from './disclosureProtocol'
import { DisclosurePanel } from './DisclosurePanel'
import './results.css'

export type JobResultsView = 'job' | 'compare'

export interface JobResultsBrowserProps {
  /** Null until the list has been read; an empty array is a read that found none. Newest first. */
  readonly versions: readonly PipelineVersionEntry[] | null
  readonly unavailableReason?: string | null
  readonly projectId: string | null
  readonly pending?: boolean
  /** Why restoring is not allowed right now; null when it is allowed. */
  readonly disabledReason?: string | null
  /** Restoring creates a NEW Job from the chosen one; nothing is replaced or deleted. */
  readonly onRestore?: (versionId: string, versionDigest: string) => void
  /** Controlled view; the component keeps its own when omitted. */
  readonly view?: JobResultsView
  readonly onViewChange?: (view: JobResultsView) => void
  /** The current Job's detailed results (automatic check, saving with opening/closing). */
  readonly currentResults: ReactNode
  /** FR-132: injected services for the Job export panel. undefined uses the WebView2 host bridge; null is unavailable. */
  readonly exportService?: JobExportService | null
  readonly exportBrowseService?: JobExportPanelProps['browseService']
  /** NFR-BR-005: injected service for the AI disclosure panel. undefined uses the WebView2 host bridge; null is unavailable. */
  readonly disclosureService?: DisclosureService | null
}

/** FR-063.5: the most Jobs shown side by side (Q-011 B). */
const maxComparedJobs = 4

/** FR-063.4: the order media outputs are listed and the first one used as a Job's picture. */
const outputOrder: readonly PipelineVersionOutput['role'][] = ['LandscapeBurnIn', 'LandscapeClean', 'VerticalBurnIn', 'VerticalClean', 'LandscapeThumbnail', 'VerticalThumbnail']

interface JobOutputItem {
  readonly key: string
  readonly label: string
  readonly output: PipelineVersionOutput
}

/** FR-063.4: outputs in display order; repeated roles (short videos) are numbered 1, 2, … */
export function jobOutputItems(entry: PipelineVersionEntry): readonly JobOutputItem[] {
  const outputs = entry.outputs ?? []
  return outputOrder.flatMap(role => {
    const same = outputs.filter(output => output.role === role)
    return same.map((output, index) => ({
      key: `${role}#${index + 1}`,
      label: same.length > 1 ? `${pipelineArtifactLabels[role]} ${index + 1}` : pipelineArtifactLabels[role],
      output,
    }))
  })
}

function jobNumber(versions: readonly PipelineVersionEntry[], entry: PipelineVersionEntry): number {
  return versions.length - versions.indexOf(entry)
}

function jobTitle(versions: readonly PipelineVersionEntry[], entry: PipelineVersionEntry): string {
  return `Job ${jobNumber(versions, entry)}${entry.isCurrent ? '（いまの版）' : ''}`
}

function mediaUrl(projectId: string, entry: PipelineVersionEntry, output: PipelineVersionOutput): string {
  return projectThumbnailUrl(projectId, { executionId: entry.executionId, artifactId: output.artifactId, contentDigest: output.contentDigest })
}

/**
 * FR-063: the results screen organised by Job. A Job rail selects whose results are shown; the
 * current Job shows the detailed results, a past Job shows its videos, pictures and settings, and
 * the comparison places two Jobs' videos side by side with a settings table. No IDs or digests are
 * shown, and the component sends no bridge request of its own.
 */
export function JobResultsBrowser({
  versions, unavailableReason = null, projectId, pending = false, disabledReason = null, onRestore,
  view: controlledView, onViewChange, currentResults, exportService, exportBrowseService, disclosureService,
}: JobResultsBrowserProps) {
  const headingId = useId()
  const [ownView, setOwnView] = useState<JobResultsView>('job')
  const view = controlledView ?? ownView
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [compareSelection, setCompareSelection] = useState<CompareSelection>(initialCompareSelection)
  const timeRef = useRef(0)
  const list = versions ?? []
  const selected =  list.find(entry => entry.versionId === selectedId) ?? null
  const shownPast = selected !== null && !selected.isCurrent ? selected : null
  const current = list.find(entry => entry.isCurrent) ?? null

  function changeView(next: JobResultsView): void {
    if (controlledView === undefined) setOwnView(next)
    onViewChange?.(next)
  }

  return (
    <section className="result-gallery job-results" aria-labelledby={headingId} data-job-results-view={view}>
      <header className="result-gallery-header">
        <h2 id={headingId}>Jobごとの仕上がり{list.length > 0 ? `（${list.length}件）` : ''}</h2>
        <div className="job-results-views" role="group" aria-label="仕上がりの見方">
          <button type="button" aria-pressed={view === 'job'} onClick={() => changeView('job')}>Jobごとに見る</button>
          <button type="button" aria-pressed={view === 'compare'} onClick={() => changeView('compare')}>Jobを比較</button>
        </div>
      </header>
      <p className="result-gallery-note">
        このProjectの元動画から作ったJobの仕上がりです。Jobを選ぶと、そのJobで作った動画・画像と設定を表示します。
        「Jobを比較」では、2〜4つのJobの映像を並べて同時に再生し、設定の違いを表で見比べられます。
        新しいJobを作っても、前のJobと仕上がりは消えません。
      </p>
      {unavailableReason !== null && <p role="status">{unavailableReason}</p>}
      {unavailableReason === null && versions === null && <p role="status">Jobの一覧を取得しています。</p>}
      {unavailableReason === null && versions !== null && versions.length === 0 && <p role="status">このProjectのJobの記録はまだありません。</p>}
      {view === 'job' ? <>
        {list.length > 0 && (
          <ol className="job-rail" aria-label="Jobの一覧">
            {list.map((entry, index) => {
              const shown = shownPast === null ? entry.isCurrent : entry === shownPast
              const changed = changedJobSettingKeys(entry, list[index + 1]).length
              return (
                <li key={entry.versionId} data-version-id={entry.versionId} data-version-current={entry.isCurrent}>
                  <button type="button" aria-pressed={shown} onClick={() => setSelectedId(entry.versionId)}>
                    <JobThumbnail projectId={projectId} jobName={`Job ${jobNumber(list, entry)}`} executionId={entry.executionId}
                      createdAt={entry.createdAt ?? null} phase={entry.phase} outputs={entry.outputs} inControl showCaption={false} />
                    <strong>{jobTitle(list, entry)}</strong>
                    <span>{pipelinePhaseLabels[entry.phase]}</span>
                    <span>{formatJobCreatedAt(entry.createdAt ?? null)}</span>
                    <span>{index === list.length - 1 ? '最初のJob' : changed > 0 ? `設定の変更 ${changed}項目` : '設定の変更なし'}</span>
                  </button>
                </li>
              )
            })}
          </ol>
        )}
        {list.length > 0 && (
          <details className="job-rail-table">
            <summary>Jobの一覧を表で見る</summary>
            <table aria-label="Jobの一覧表">
              <thead><tr><th scope="col">Job</th><th scope="col">状態</th><th scope="col">作成日時</th><th scope="col">選択</th></tr></thead>
              <tbody>
                {list.map(entry => {
                  const shown = shownPast === null ? entry.isCurrent : entry === shownPast
                  return (
                    <tr key={entry.versionId} data-version-id={entry.versionId}>
                      <th scope="row">{jobTitle(list, entry)}</th>
                      <td>{pipelinePhaseLabels[entry.phase]}</td>
                      <td>{formatJobCreatedAt(entry.createdAt ?? null)}</td>
                      <td>{shown ? '表示中' : <button type="button" onClick={() => setSelectedId(entry.versionId)}>{jobTitle(list, entry)}を表示</button>}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </details>
        )}
        {list.length > 0 && (() => {
          const shown = shownPast ?? current
          return shown === null ? null : (
            <JobExportPanel key={shown.versionId} projectId={projectId} executionId={shown.executionId}
              jobName={`Job ${jobNumber(list, shown)}`}
              disabledReason={isJobSelectable(shown.phase) ? null : jobExportMessages.notCompleted}
              service={exportService} browseService={exportBrowseService} />
          )
        })()}
        {list.length > 0 && <DisclosurePanel projectId={projectId} service={disclosureService} />}
        {shownPast === null ? (
          <div className="job-results-current">
            {current !== null && <h3>{jobTitle(list, current)}の仕上がり</h3>}
            {currentResults}
          </div>
        ) : (
          <PastJobDetail key={shownPast.versionId} versions={list} entry={shownPast} current={current} projectId={projectId}
            pending={pending} disabledReason={disabledReason} onRestore={onRestore} />
        )}
      </> : <JobComparison versions={list} projectId={projectId} selection={compareSelection} timeRef={timeRef} seedId={selectedId}
        onSelectionChange={patch => setCompareSelection(previous => ({ ...previous, ...patch }))} />}
    </section>
  )
}

function PastJobDetail({ versions, entry, current, projectId, pending, disabledReason, onRestore }: Readonly<{
  versions: readonly PipelineVersionEntry[]
  entry: PipelineVersionEntry
  current: PipelineVersionEntry | null
  projectId: string | null
  pending: boolean
  disabledReason: string | null
  onRestore?: (versionId: string, versionDigest: string) => void
}>) {
  const headingId = useId()
  const [confirming, setConfirming] = useState(false)
  const blocked = pending || disabledReason !== null
  const title = jobTitle(versions, entry)
  const previous = versions[versions.indexOf(entry) + 1]
  return (
    <article className="job-detail" aria-labelledby={headingId} data-version-id={entry.versionId}>
      <h3 id={headingId}>{title}の仕上がり</h3>
      <p>状態: {pipelinePhaseLabels[entry.phase]} · 作成日時: {formatJobCreatedAt(entry.createdAt ?? null)}</p>
      <p className="result-gallery-note">
        自動チェックの結果、オープニング・クロージングを付けた保存、解析〜根拠の各タブは、
        {current === null ? 'いまの版' : jobTitle(versions, current)}の仕上がりで確認できます。
      </p>
      <JobOutputs projectId={projectId} entry={entry} title={title} />
      <h4>このJobの設定（Jobごとに違う項目）</h4>
      {entry.settings === undefined
        ? <p className="result-gallery-note">このJobの設定の記録はありません。</p>
        : <VersionSettings entry={entry} previous={previous} />}
      {disabledReason !== null && <p role="status">{disabledReason}</p>}
      {confirming ? (
        <p>
          <span>この動画の構成から新しい動画を作りますか。今までの動画は残ります。</span>
          <button type="button" disabled={blocked} onClick={() => { setConfirming(false); onRestore?.(entry.versionId, entry.versionDigest) }}>作り直す</button>
          <button type="button" onClick={() => setConfirming(false)}>取り消す</button>
        </p>
      ) : (
        <button type="button" disabled={blocked || onRestore === undefined} onClick={() => setConfirming(true)}>この動画から作り直す</button>
      )}
    </article>
  )
}

function JobOutputs({ projectId, entry, title }: Readonly<{ projectId: string | null, entry: PipelineVersionEntry, title: string }>) {
  if (entry.outputs === undefined) return <p className="result-gallery-note">このJobの成果物の情報はありません（古い記録です）。</p>
  const items = jobOutputItems(entry)
  if (items.length === 0) return <p className="result-gallery-note">このJobには、見られる動画・画像がありません。</p>
  if (projectId === null) return <p className="result-gallery-note">Projectを確認できないため、プレビューを表示できません。</p>
  return (
    <ul className="job-outputs" aria-label={`${title}の動画・画像`} role="list">
      {items.map(item => (
        <li key={item.key} data-output-role={item.output.role}>
          <figure>
            <figcaption>{item.label}</figcaption>
            <JobMedia url={mediaUrl(projectId, entry, item.output)} output={item.output} label={`${title}・${item.label}`} />
          </figure>
        </li>
      ))}
    </ul>
  )
}

function JobMedia({ url, output, label, mediaRef, onPlay, onPause, onSeeked }: Readonly<{
  url: string
  output: PipelineVersionOutput
  label: string
  mediaRef?: (element: HTMLVideoElement | null) => void
  onPlay?: () => void
  onPause?: () => void
  onSeeked?: () => void
}>) {
  return output.mediaType === 'video/mp4'
    ? <video ref={mediaRef} src={url} controls preload="none" playsInline aria-label={label} onPlay={onPlay} onPause={onPause} onSeeked={onSeeked} />
    : <img src={url} alt={label} loading="lazy" />
}

/** Above this difference (seconds) the other video follows a seek. */
const seekTolerance = 0.3

function startPlayback(media: HTMLVideoElement | null): void {
  if (media === null || !media.paused) return
  try {
    const started = media.play() as Promise<void> | undefined
    started?.catch(() => {})
  } catch {
    // A video that cannot start stays paused; the other one is unaffected.
  }
}

/** NFR-UX-003: the comparison's selection, kept by the browser so switching the view back and forth loses nothing. */
export interface CompareSelection {
  readonly count: number
  readonly slotIds: readonly (string | undefined)[]
  readonly outputKey: string | null
  readonly linked: boolean
  readonly differencesOnly: boolean
}
export const initialCompareSelection: CompareSelection = { count: 2, slotIds: [], outputKey: null, linked: true, differencesOnly: true }

function JobComparison({ versions, projectId, selection, onSelectionChange, timeRef, seedId }: Readonly<{
  versions: readonly PipelineVersionEntry[]
  projectId: string | null
  selection: CompareSelection
  onSelectionChange: (patch: Partial<CompareSelection>) => void
  /** The playback position (seconds) shared across views. */
  timeRef: { current: number }
  /** The Job selected in the per-Job view; it fills the first slot until the user chooses otherwise. */
  seedId: string | null
}>) {
  const { count, slotIds, outputKey, linked, differencesOnly } = selection
  const setCount = (value: number) => onSelectionChange({ count: value })
  const setSlotIds = (update: (previous: readonly (string | undefined)[]) => readonly (string | undefined)[]) => onSelectionChange({ slotIds: update(slotIds) })
  const setOutputKey = (value: string | null) => onSelectionChange({ outputKey: value })
  const setLinked = (value: boolean) => onSelectionChange({ linked: value })
  const setDifferencesOnly = (value: boolean) => onSelectionChange({ differencesOnly: value })
  const medias = useRef<(HTMLVideoElement | null)[]>([])
  const countSelectId = useId()
  const outputSelectId = useId()
  const slotSelectId = useId()

  if (versions.length < 2) {
    return <p role="status" className="job-compare-empty">比較するには、Jobが2つ以上必要です。「元動画」の「新しいJobを作成」で、設定を変えたJobを作れます。</p>
  }
  const maxCount = Math.min(maxComparedJobs, versions.length)
  const shownCount = Math.min(count, maxCount)
  // FR-063.5: by default the newest Jobs are shown, the oldest of them first (left/top).
  // FR-131: only completed Jobs are chosen by default; unfinished ones fill in only when too few have completed.
  const pool = versions.filter(entry => isJobSelectable(entry.phase)).length >= shownCount
    ? versions.filter(entry => isJobSelectable(entry.phase)) : versions
  const defaults = pool.slice(0, shownCount).reverse()
  const seed = seedId !== null && slotIds.every(id => id === undefined) ? versions.find(entry => entry.versionId === seedId) : undefined
  if (seed !== undefined && !defaults.includes(seed)) defaults[0] = seed
  const entries =  Array.from({ length: shownCount }, (_, index) =>
    versions.find(entry => entry.versionId === slotIds[index]) ?? defaults[index]!)
  const itemLists = entries.map(jobOutputItems)
  const keys = [...new Map(itemLists.flat().map(item => [item.key, item.label] as const))]
    .sort(([a], [b]) => keyRank(a) - keyRank(b))
  const inAll = keys.find(([key]) => itemLists.every(items => items.some(item => item.key === key)))
  const chosen = keys.find(([key]) => key === outputKey) ?? inAll ?? keys[0]
  const items = itemLists.map(list => chosen === undefined ? undefined : list.find(item => item.key === chosen[0]))
  const videoCount = items.filter(item => item?.output.mediaType === 'video/mp4').length
  const sameJob = new Set(entries).size < entries.length

  function follow(from: number, event: 'play' | 'pause' | 'seeked'): void {
    const source = medias.current[from]
    if (!linked || source === null || source === undefined) return
    medias.current.forEach((target, index) => {
      if (index === from || target === null || target === undefined) return
      if (event === 'play') startPlayback(target)
      else if (event === 'pause') { if (!target.paused) target.pause() }
      else if (Math.abs(target.currentTime - source.currentTime) > seekTolerance) target.currentTime = source.currentTime
    })
  }

  function eachVideo(action: (media: HTMLVideoElement) => void): void {
    for (const media of medias.current) if (media !== null && media !== undefined) action(media)
  }

  return (
    <div className="job-compare">
      <div className="job-compare-pickers">
        <label htmlFor={countSelectId}>比べるJobの数</label>
        <select id={countSelectId} value={shownCount} disabled={maxCount === 2} onChange={event => setCount(Number(event.currentTarget.value))}>
          {Array.from({ length: maxCount - 1 }, (_, index) => index + 2).map(value => <option key={value} value={value}>{value}つ</option>)}
        </select>
        {entries.map((entry, index) => (
          <Fragment key={index}>
            <label htmlFor={`${slotSelectId}-${index}`}>{index + 1}つ目に表示するJob</label>
            <select id={`${slotSelectId}-${index}`} value={entry.versionId}
              onChange={event => { const value = event.currentTarget.value; setSlotIds(previous => { const next = [...previous]; next[index] = value; return next }) }}>
              {versions.map(option => <option key={option.versionId} value={option.versionId}
                disabled={!isJobSelectable(option.phase) && option !== entry}>
                {jobTitle(versions, option)}{isJobSelectable(option.phase) ? '' : `（${jobThumbnailStatus(option.phase)}・選べません）`}
              </option>)}
            </select>
            <ul className="job-thumbnail-picker" aria-label={`${index + 1}つ目に表示するJobのサムネイル`}>
              {versions.map(option => {
                const selectable = isJobSelectable(option.phase)
                const chosenHere = option === entry
                return <li key={option.versionId}>
                  <button type="button" aria-pressed={chosenHere} disabled={!selectable && !chosenHere} data-version-id={option.versionId}
                    onClick={() => setSlotIds(previous => { const next = [...previous]; next[index] = option.versionId; return next })}>
                    <JobThumbnail projectId={projectId} jobName={jobTitle(versions, option)} executionId={option.executionId}
                      createdAt={option.createdAt ?? null} phase={option.phase} outputs={option.outputs} inControl />
                    {!selectable && <span>選べません（完了していません）</span>}
                  </button>
                </li>
              })}
            </ul>
          </Fragment>
        ))}
        <label htmlFor={outputSelectId}>比べる成果物</label>
        <select id={outputSelectId} value={chosen?.[0] ?? ''} disabled={keys.length === 0} onChange={event => setOutputKey(event.currentTarget.value)}>
          {keys.length === 0 && <option value="">見られる成果物がありません</option>}
          {keys.map(([key, label]) => <option key={key} value={key}>{label}</option>)}
        </select>
      </div>
      {sameJob && <p role="status">同じJobが選ばれています。違うJobを選ぶと、仕上がりと設定の違いを比べられます。</p>}
      {videoCount >= 2 && (
        <div className="job-compare-controls" role="group" aria-label={`${shownCount}つの動画の再生`}>
          <label><input type="checkbox" checked={linked} onChange={event => setLinked(event.currentTarget.checked)} /> 再生・一時停止・再生位置を{shownCount}つで合わせる</label>
          <button type="button" onClick={() => eachVideo(startPlayback)}>{shownCount}つを同時に再生</button>
          <button type="button" onClick={() => eachVideo(media => { if (!media.paused) media.pause() })}>{shownCount}つを一時停止</button>
          <button type="button" onClick={() => eachVideo(media => { media.currentTime = 0 })}>{shownCount}つを先頭へ戻す</button>
        </div>
      )}
      <div className="job-compare-panes">
        {entries.map((entry, index) => {
          const item = items[index]
          return (
            <section key={index} className="job-compare-pane" aria-label={`${index + 1}つ目: ${jobTitle(versions, entry)}`} data-version-id={entry.versionId}>
              <h3>{jobTitle(versions, entry)}</h3>
              <p>状態: {pipelinePhaseLabels[entry.phase]} · 作成日時: {formatJobCreatedAt(entry.createdAt ?? null)}</p>
              {item === undefined || projectId === null
                ? <p className="result-gallery-note">{projectId === null ? 'Projectを確認できないため、プレビューを表示できません。' : 'このJobにはこの成果物がありません。'}</p>
                : <JobMedia url={mediaUrl(projectId, entry, item.output)} output={item.output} label={`${jobTitle(versions, entry)}・${item.label}`}
                  mediaRef={element => {
                    medias.current[index] = element
                    if (element !== null && timeRef.current > 0 && element.currentTime === 0) element.currentTime = timeRef.current
                  }}
                  onPlay={() => follow(index, 'play')} onPause={() => { timeRef.current = medias.current[index]?.currentTime ?? timeRef.current; follow(index, 'pause') }}
                  onSeeked={() => { timeRef.current = medias.current[index]?.currentTime ?? timeRef.current; follow(index, 'seeked') }} />}
            </section>
          )
        })}
      </div>
      <JobSettingsComparison versions={versions} entries={entries} differencesOnly={differencesOnly} onDifferencesOnlyChange={setDifferencesOnly} />
    </div>
  )
}

function keyRank(key: string): number {
  const [role, index] = key.split('#') as [PipelineVersionOutput['role'], string]
  return outputOrder.indexOf(role) * 1000 + Number(index)
}

const missingSetting = '（記録なし）'

/** FR-063.7: compares the settings the settings screen offers across the Jobs; keys equal across all Jobs are not recorded (FR-055.1). */
export function compareJobSettings(entries: readonly PipelineVersionEntry[]) {
  const values = entries.map(entry => new Map(editableJobSettings(entry).map(item => [item.key, settingValueText(item)] as const)))
  const keys = [...new Set(values.flatMap(map => [...map.keys()]))]
  return keys.map(key => {
    const texts = values.map(map => map.get(key) ?? missingSetting)
    return { key, label: settingLabel(key), values: texts, different: new Set(texts).size > 1 }
  })
}

function JobSettingsComparison({ versions, entries, differencesOnly, onDifferencesOnlyChange }: Readonly<{
  versions: readonly PipelineVersionEntry[]
  entries: readonly PipelineVersionEntry[]
  differencesOnly: boolean
  onDifferencesOnlyChange: (value: boolean) => void
}>) {
  const headingId = useId()
  const rows = compareJobSettings(entries)
  const shown = differencesOnly ? rows.filter(row => row.different) : rows
  let body: ReactNode
  if (entries.some(entry => entry.settings === undefined)) {
    body = <p className="result-gallery-note">設定の記録が無いJobがあるため、設定を比べられません。</p>
  } else if (rows.length === 0) {
    body = <p className="result-gallery-note">{entries.length}つのJobは、設定画面で変えられる項目がすべて同じです。</p>
  } else if (shown.length === 0) {
    body = <p className="result-gallery-note">{entries.length}つのJobで違う設定はありません。</p>
  } else {
    body = (
      <table className="job-settings-table" aria-labelledby={headingId}>
        <thead>
          <tr><th scope="col">項目</th>{entries.map((entry, index) => <th key={index} scope="col">{jobTitle(versions, entry)}</th>)}</tr>
        </thead>
        <tbody>
          {shown.map(row => (
            <tr key={row.key} data-setting-key={row.key} data-setting-different={row.different}>
              <th scope="row">{row.label}{row.different && <span className="job-setting-different">（違い）</span>}</th>
              {row.values.map((value, index) => <td key={index}>{value}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
    )
  }
  return (
    <section className="job-settings-compare">
      <h3 id={headingId}>設定の比較</h3>
      <label><input type="checkbox" checked={differencesOnly} onChange={event => onDifferencesOnlyChange(event.currentTarget.checked)} /> 違う項目だけ表示</label>
      {body}
    </section>
  )
}
