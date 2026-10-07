import {
  useEffect,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
} from 'react'

import {
  isCanonicalComparisonDigest,
  isCanonicalComparisonEntityId,
  type ComparisonAdoptionChange,
  type ComparisonElementChange,
  type ComparisonFeatureAvailability,
  type ComparisonRationalTime,
  type ComparisonSnapshot,
  type ComparisonVersionSnapshot,
  type UsedSourceRange,
} from './compareProtocol'
import {
  useComparisonState,
  type ComparisonStateContextValue,
} from './CompareStateProvider'
import { formatDuration, formatTimestamp } from '../results/humanReadable'

type ComparisonTab = 'summary' | 'ranges' | 'changes'

const comparisonTabs: readonly ComparisonTab[] = [
  'summary',
  'ranges',
  'changes',
]

const tabLabels: Readonly<Record<ComparisonTab, string>> = {
  summary: '概要・機能',
  ranges: '使用区間',
  changes: '変更・採用',
}

const featureLabels: Readonly<Record<
  keyof ComparisonFeatureAvailability,
  string
>> = {
  openingHook: '冒頭フック',
  subtitleStyle: '字幕スタイル',
  telopCount: 'テロップ数',
  transitions: 'トランジション',
  effects: '効果',
  callToAction: 'CTA',
  destination: '想定公開先',
  warnings: '要確認事項・警告',
}

const changeKindLabels: Readonly<Record<
  ComparisonElementChange['kind'],
  string
>> = {
  added: '追加',
  removed: '削除',
  moved: '移動',
  modified: '変更',
}

const adoptionKindLabels: Readonly<Record<
  ComparisonAdoptionChange['kind'],
  string
>> = {
  adopted: '採用',
  removed: '採用解除',
  replaced: '採用差し替え',
}

type ComparisonVersionTarget = Readonly<{
  versionId: string
  versionDigest: string
}>

export interface ComparePanelProps {
  readonly context?: Readonly<{
    left: ComparisonVersionTarget | null
    right: ComparisonVersionTarget | null
  }>
  readonly active?: boolean
}

export function ComparePanel(props: ComparePanelProps = {}) {
  return <ComparePanelView {...useComparisonState()} {...props} />
}

export function ComparePanelView({
  state,
  changeInput,
  compareVersions,
  clearError,
  context,
  active = true,
}: ComparisonStateContextValue & ComparePanelProps) {
  const workflow = context !== undefined
  const leftVersionId = context?.left?.versionId ?? ''
  const leftVersionDigest = context?.left?.versionDigest ?? ''
  const rightVersionId = context?.right?.versionId ?? ''
  const rightVersionDigest = context?.right?.versionDigest ?? ''
  const contextKey =
    isCanonicalComparisonEntityId(leftVersionId) &&
    isCanonicalComparisonDigest(leftVersionDigest) &&
    isCanonicalComparisonEntityId(rightVersionId) &&
    isCanonicalComparisonDigest(rightVersionDigest)
      ? `${leftVersionId}:${leftVersionDigest}:${rightVersionId}:${rightVersionDigest}`
      : null
  const inputsMatchContext =
    state.leftVersionIdInput === leftVersionId &&
    state.leftVersionDigestInput === leftVersionDigest &&
    state.rightVersionIdInput === rightVersionId &&
    state.rightVersionDigestInput === rightVersionDigest
  const pending = state.pending !== null
  const rejectedContext = workflow && inputsMatchContext && (
    state.error?.code === 'notFound' ||
    state.error?.code === 'staleVersion' ||
    state.error?.code === 'invalidRequest'
  )
  const canCompare = active && !pending && !rejectedContext &&
    (!workflow || (contextKey !== null && inputsMatchContext)) &&
    isCanonicalComparisonEntityId(state.leftVersionIdInput) &&
    isCanonicalComparisonDigest(state.leftVersionDigestInput) &&
    isCanonicalComparisonEntityId(state.rightVersionIdInput) &&
    isCanonicalComparisonDigest(state.rightVersionDigestInput)
  const comparison = !workflow || (
    contextKey !== null &&
    state.comparison?.left.versionId === leftVersionId &&
    state.comparison.left.versionDigest === leftVersionDigest &&
    state.comparison.right.versionId === rightVersionId &&
    state.comparison.right.versionDigest === rightVersionDigest
  ) ? state.comparison : null
  const error = !workflow || (contextKey !== null && inputsMatchContext)
    ? state.error
    : null
  const [selectedIndex, setSelectedIndex] = useState(0)
  const requestedContextKey = useRef<string | null>(null)
  const sectionRef = useRef<HTMLElement | null>(null)
  const resultTitle = useRef<HTMLHeadingElement | null>(null)
  const previousComparison = useRef<ComparisonSnapshot | null>(null)

  useEffect(() => {
    if (active) return
    const focusedElement = document.activeElement
    if (focusedElement instanceof HTMLElement && sectionRef.current?.contains(focusedElement)) {
      focusedElement.blur()
    }
  }, [active])

  useEffect(() => {
    if (!active || pending) return
    if (contextKey === null) {
      requestedContextKey.current = null
      return
    }
    if (!inputsMatchContext) {
      changeInput('leftVersionId', leftVersionId)
      changeInput('leftVersionDigest', leftVersionDigest)
      changeInput('rightVersionId', rightVersionId)
      changeInput('rightVersionDigest', rightVersionDigest)
      return
    }
    if (requestedContextKey.current === contextKey) return

    // Read only after input updates (including a previous response) have committed.
    // compareVersions closes over these exact inputs in the existing provider.
    requestedContextKey.current = contextKey
    compareVersions()
  }, [
    active,
    changeInput,
    compareVersions,
    contextKey,
    inputsMatchContext,
    leftVersionDigest,
    leftVersionId,
    pending,
    rightVersionDigest,
    rightVersionId,
  ])

  useEffect(() => {
    const changed = previousComparison.current !== state.comparison
    previousComparison.current = state.comparison
    if (active && changed && comparison !== null) {
      resultTitle.current?.focus()
    }
  }, [active, comparison, state.comparison])

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    if (canCompare) {
      compareVersions()
    }
  }

  return (
    <section
      ref={sectionRef}
      className="comparison-panel"
      aria-labelledby="comparison-title"
      hidden={!active}
      inert={!active}
    >
      <div className="comparison-heading">
        <div>
          <p className="eyebrow">編集版の比較</p>
          <h2 id="comparison-title">構成版の比較</h2>
          <p>
            同じProjectの2版を完全な内容 digest で固定し、永続化された根拠だけを比較します。
          </p>
        </div>
        <span className="comparison-safety-badge">推測なし</span>
      </div>

      {workflow ? (
        <div className="comparison-submit-row">
          <button
            type="button"
            disabled={!canCompare}
            onClick={() => {
              if (canCompare) compareVersions()
            }}
          >
            {pending ? '比較しています…' : '2版を比較'}
          </button>
        </div>
      ) : (
        <form className="comparison-form" onSubmit={submit}>
          <VersionTargetFields
            side="left"
            label="比較元"
            versionId={state.leftVersionIdInput}
            versionDigest={state.leftVersionDigestInput}
            disabled={pending || !active}
            onVersionIdChange={(value) => changeInput('leftVersionId', value)}
            onDigestChange={(value) => changeInput('leftVersionDigest', value)}
          />
          <VersionTargetFields
            side="right"
            label="比較先"
            versionId={state.rightVersionIdInput}
            versionDigest={state.rightVersionDigestInput}
            disabled={pending || !active}
            onVersionIdChange={(value) => changeInput('rightVersionId', value)}
            onDigestChange={(value) => changeInput('rightVersionDigest', value)}
          />
          <div className="comparison-submit-row">
            <p id="comparison-target-help" className="field-help">
              各版の小文字 UUID と、64文字の小文字 SHA-256 digest を指定します。
              Windows ユーザーと閲覧権限はネイティブ側で確認されます。
            </p>
            <button type="submit" disabled={!canCompare}>
              {pending ? '比較しています…' : '2版を比較'}
            </button>
          </div>
        </form>
      )}

      {error !== null && (
        <div className="comparison-error" role="alert">
          <div>
            <strong>構成版を比較できませんでした</strong>
            <p>
              {workflow && (
                error.code === 'notFound' ||
                error.code === 'staleVersion' ||
                error.code === 'invalidRequest'
              ) ? '選択した2版を確認できませんでした。' : error.message}
            </p>
          </div>
          <button
            className="quiet-button"
            type="button"
            disabled={!active}
            onClick={clearError}
          >
            閉じる
          </button>
        </div>
      )}

      <p
        className="comparison-status"
        role="status"
        aria-live="polite"
        data-comparison-pending={pending ? 'true' : 'false'}
      >
        {workflow && contextKey === null
          ? '比較する2つの編集版を選んでください。'
          : pending
            ? '比較の準備をしています。'
            : comparison === null
              ? workflow
                ? '選択した2版の比較結果はまだありません。'
                : '左右の exact version を指定してください。'
              : '比較結果を読み込みました。'}
      </p>

      {comparison === null ? (
        <div className="comparison-empty">
          <strong>比較結果はまだありません</strong>
          <p>
            {workflow
              ? '2版がそろうと比較できます。'
              : '2つの編集版を選ぶと、使った部分、長さ、場面の違い、採用した提案の違いを表示します。'}
          </p>
        </div>
      ) : (
        <ComparisonWorkspace
          comparison={comparison}
          resultTitleRef={resultTitle}
          active={active}
          selectedIndex={selectedIndex}
          onSelectTab={setSelectedIndex}
        />
      )}
    </section>
  )
}

export function getComparisonTabAfterKey(
  currentIndex: number,
  key: string,
): number | null {
  if (
    !Number.isInteger(currentIndex) ||
    currentIndex < 0 ||
    currentIndex >= comparisonTabs.length
  ) {
    return null
  }
  switch (key) {
    case 'ArrowLeft':
    case 'ArrowUp':
      return currentIndex === 0 ? comparisonTabs.length - 1 : currentIndex - 1
    case 'ArrowRight':
    case 'ArrowDown':
      return currentIndex === comparisonTabs.length - 1 ? 0 : currentIndex + 1
    case 'Home':
      return 0
    case 'End':
      return comparisonTabs.length - 1
    default:
      return null
  }
}

function VersionTargetFields({
  side,
  label,
  versionId,
  versionDigest,
  disabled,
  onVersionIdChange,
  onDigestChange,
}: Readonly<{
  side: 'left' | 'right'
  label: string
  versionId: string
  versionDigest: string
  disabled: boolean
  onVersionIdChange: (value: string) => void
  onDigestChange: (value: string) => void
}>) {
  return (
    <fieldset className="comparison-target" disabled={disabled}>
      <legend>{label}</legend>
      <label htmlFor={`comparison-${side}-version-id`}>構成版 ID</label>
      <input
        id={`comparison-${side}-version-id`}
        name={`${side}VersionId`}
        type="text"
        autoComplete="off"
        spellCheck={false}
        minLength={36}
        maxLength={36}
        pattern="[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
        value={versionId}
        aria-describedby="comparison-target-help"
        onChange={(event) => onVersionIdChange(event.currentTarget.value)}
      />
      <label htmlFor={`comparison-${side}-version-digest`}>
        内容 digest（SHA-256・完全値）
      </label>
      <input
        id={`comparison-${side}-version-digest`}
        name={`${side}VersionDigest`}
        type="text"
        autoComplete="off"
        spellCheck={false}
        minLength={64}
        maxLength={64}
        pattern="[0-9a-f]{64}"
        value={versionDigest}
        aria-describedby="comparison-target-help"
        onChange={(event) => onDigestChange(event.currentTarget.value)}
      />
    </fieldset>
  )
}

function ComparisonWorkspace({
  comparison,
  resultTitleRef,
  active,
  selectedIndex,
  onSelectTab,
}: Readonly<{
  comparison: ComparisonSnapshot
  resultTitleRef: React.RefObject<HTMLHeadingElement | null>
  active: boolean
  selectedIndex: number
  onSelectTab: (index: number) => void
}>) {
  const tabs = useRef<Array<HTMLButtonElement | null>>([])
  const sameVersion = comparison.left.versionId === comparison.right.versionId &&
    comparison.left.versionDigest === comparison.right.versionDigest

  const onTabKeyDown = (
    event: KeyboardEvent<HTMLButtonElement>,
    index: number,
  ): void => {
    if (!active) return
    const nextIndex = getComparisonTabAfterKey(index, event.key)
    if (nextIndex === null) return
    event.preventDefault()
    onSelectTab(nextIndex)
    tabs.current[nextIndex]?.focus()
  }

  return (
    <div className="comparison-workspace">
      <div className="comparison-result-heading">
        <div>
          <p className="section-kicker">比較結果</p>
          <h3 ref={resultTitleRef} tabIndex={-1} id="comparison-result-title">
            比較結果
          </h3>
        </div>
        {sameVersion && <span className="comparison-same-badge">同一版</span>}
      </div>

      <div className="comparison-tabs" role="tablist" aria-label="比較結果の分類">
        {comparisonTabs.map((tab, index) => (
          <button
            key={tab}
            id={`comparison-tab-${tab}`}
            role="tab"
            type="button"
            ref={(element) => {
              tabs.current[index] = element
            }}
            aria-selected={selectedIndex === index}
            aria-controls={`comparison-panel-${tab}`}
            disabled={!active}
            tabIndex={active && selectedIndex === index ? 0 : -1}
            onClick={() => {
              if (active) onSelectTab(index)
            }}
            onKeyDown={(event) => onTabKeyDown(event, index)}
          >
            {tabLabels[tab]}
          </button>
        ))}
      </div>

      <div
        id="comparison-panel-summary"
        role="tabpanel"
        aria-labelledby="comparison-tab-summary"
        hidden={selectedIndex !== 0}
      >
        <div className="comparison-side-grid">
          <VersionSummary side="left" label="比較元" version={comparison.left} />
          <VersionSummary side="right" label="比較先" version={comparison.right} />
        </div>
        <FeatureComparison comparison={comparison} />
      </div>

      <div
        id="comparison-panel-ranges"
        role="tabpanel"
        aria-labelledby="comparison-tab-ranges"
        hidden={selectedIndex !== 1}
      >
        <div className="comparison-side-grid">
          <SourceRangeList label="比較元" ranges={comparison.left.usedSourceRanges} />
          <SourceRangeList label="比較先" ranges={comparison.right.usedSourceRanges} />
        </div>
        <p className="comparison-evidence-note">
          元動画のどの部分を、完成した動画のどこで使っているかを示します。
        </p>
      </div>

      <div
        id="comparison-panel-changes"
        role="tabpanel"
        aria-labelledby="comparison-tab-changes"
        hidden={selectedIndex !== 2}
      >
        <ChangeList changes={comparison.elementChanges} />
        <AdoptionChangeList changes={comparison.adoptionChanges} />
      </div>
    </div>
  )
}

function VersionSummary({
  side,
  label,
  version,
}: Readonly<{
  side: 'left' | 'right'
  label: string
  version: ComparisonVersionSnapshot
}>) {
  return (
    <section className="comparison-card" aria-labelledby={`comparison-${side}-summary`}>
      <p className="section-kicker">{label}</p>
      <h4 id={`comparison-${side}-summary`}>編集版</h4>
      <dl className="comparison-version-data">
        <div>
          <dt>作成日時</dt>
          <dd><time dateTime={version.createdAt}>{formatUtc(version.createdAt)}</time></dd>
        </div>
        <div>
          <dt>再生時間</dt>
          <dd><strong>{formatRationalTime(version.playbackDuration)}</strong></dd>
        </div>
        <div className="comparison-reason-row">
          <dt>変更理由</dt>
          <dd>{version.changeReason}</dd>
        </div>
      </dl>
    </section>
  )
}

function FeatureComparison({
  comparison,
}: Readonly<{ comparison: ComparisonSnapshot }>) {
  const features = Object.keys(featureLabels) as Array<
    keyof ComparisonFeatureAvailability
  >
  return (
    <div className="comparison-table-scroll">
      <table className="comparison-feature-table">
        <caption>版別の機能・確認項目</caption>
        <thead>
          <tr>
            <th scope="col">項目</th>
            <th scope="col">比較元</th>
            <th scope="col">比較先</th>
          </tr>
        </thead>
        <tbody>
          {features.map((feature) => (
            <tr key={feature}>
              <th scope="row">{featureLabels[feature]}</th>
              <td>{formatAvailability(comparison.left.features[feature])}</td>
              <td>{formatAvailability(comparison.right.features[feature])}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="comparison-unavailable-note">
        「データ未提供」は、その編集版に記録がないため比べられない項目です。
      </p>
    </div>
  )
}

function SourceRangeList({
  label,
  ranges,
}: Readonly<{ label: string; ranges: readonly UsedSourceRange[] }>) {
  return (
    <section className="comparison-card source-range-card">
      <p className="section-kicker">{label}</p>
      <h4>元動画から使った部分</h4>
      {ranges.length === 0 ? (
        <p className="comparison-list-empty">元動画から使った部分はありません。</p>
      ) : (
        <ol className="comparison-range-list">
          {ranges.map((range) => (
            <li key={range.elementId}>
              <dl>
                <div>
                  <dt>元動画</dt>
                  <dd>{formatRange(range.sourceRange)}</dd>
                </div>
                <div>
                  <dt>完成した動画</dt>
                  <dd>{formatRange(range.outputRange)}</dd>
                </div>
              </dl>
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}

function ChangeList({
  changes,
}: Readonly<{ changes: readonly ComparisonElementChange[] }>) {
  return (
    <section className="comparison-card">
      <p className="section-kicker">場面の並び</p>
      <h4>場面の変更</h4>
      {changes.length === 0 ? (
        <p className="comparison-list-empty">場面の変更はありません。</p>
      ) : (
        <ol className="comparison-change-list">
          {changes.map((change) => (
            <li key={change.elementId}>
              <span className={`comparison-change-badge kind-${change.kind}`}>
                {changeKindLabels[change.kind]}
              </span>
              <span>{formatIndexChange(change)}</span>
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}

function AdoptionChangeList({
  changes,
}: Readonly<{ changes: readonly ComparisonAdoptionChange[] }>) {
  return (
    <section className="comparison-card">
      <p className="section-kicker">自動提案</p>
      <h4>採用した提案の変更</h4>
      {changes.length === 0 ? (
        <p className="comparison-list-empty">採用した提案の変更はありません。</p>
      ) : (
        <ol className="comparison-adoption-list">
          {changes.map((change) => (
            <li key={change.elementId}>
              <strong>{adoptionKindLabels[change.kind]}</strong>
              <dl>
                <div>
                  <dt>変更前</dt>
                  <dd>{change.previousSuggestionId === null ? '提案なし' : '提案を採用'}</dd>
                </div>
                <div>
                  <dt>変更後</dt>
                  <dd>{change.currentSuggestionId === null ? '提案なし' : '提案を採用'}</dd>
                </div>
              </dl>
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}

function formatIndexChange(change: ComparisonElementChange): string {
  const previous = change.previousIndex === null ? 'なし' : `${change.previousIndex + 1}番`
  const current = change.currentIndex === null ? 'なし' : `${change.currentIndex + 1}番`
  return `${previous} → ${current}`
}

function formatRange(range: UsedSourceRange['sourceRange']): string {
  return `${formatTimestamp(range.start.value, range.start.rate)} から ${formatDuration(range.duration.value, range.duration.rate)}`
}

function formatRationalTime(time: ComparisonRationalTime): string {
  return formatDuration(time.value, time.rate)
}

function formatAvailability(
  availability: ComparisonFeatureAvailability[keyof ComparisonFeatureAvailability],
): string {
  return availability === 'unavailable' ? 'データ未提供' : availability
}

function formatUtc(value: string): string {
  return new Intl.DateTimeFormat('ja-JP', {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value))
}