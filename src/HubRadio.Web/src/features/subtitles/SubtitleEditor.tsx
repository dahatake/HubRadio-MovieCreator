import {
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
} from 'react'

import { usePlayerSourceNavigation } from '../player'
import {
  getRovingIndexAfterKey,
  layoutRectToText,
  layoutTimeRangeToText,
  parseExactLayoutRect,
  parseLayoutTimeRange,
  type LayoutRectText,
  type LayoutTimeRangeText,
} from '../layout/layoutForm'
import type {
  LayoutCueStyle,
  LayoutHardConstraint,
  LayoutOperation,
  LayoutSnapshot,
  LayoutSubtitleEdit,
  LayoutSubtitleCue,
  LayoutTeropEdit,
  LayoutTeropCue,
} from '../layout/layoutProtocol'

export interface SubtitleEditorProps {
  readonly snapshot: LayoutSnapshot
  readonly disabled: boolean
  readonly applyOperation: (operation: LayoutOperation) => void
  readonly graphicRegionNames: readonly string[]
}

const navigationMessages = Object.freeze({
  requested: '字幕の元時刻への移動をプレイヤーへ要求しました。',
  unavailable: '同じ案件の代理動画を先に読み込んでください。',
  projectMismatch: 'プレイヤーには別の案件が読み込まれています。',
  busy: 'プレイヤーの移動確認が完了するまでお待ちください。',
  outOfRange: 'この元時刻に対応する代理フレームはありません。',
} as const)

const hardConstraintLabels: Readonly<Record<LayoutHardConstraint, string>> = {
  noPlacementCandidate: '配置候補なし',
  sourceTimingAdjustmentRequired: '元時刻の調整が必要',
  requiredSafeZoneMissing: '必須 safe zone なし',
  outsideOutputFrame: 'output frame 外',
  unknownLayoutRegion: '未知の layout region',
  layoutRegionIsNotSubtitle: '字幕用でない region',
  outsideSubtitleRegion: '字幕 region 外',
  outsideRequiredSafeZone: '必須 safe zone 外',
  prohibitedRegionOverlap: '禁止 region と重なり',
}

const cueStyles: readonly LayoutCueStyle[] = Object.freeze([
  'normal',
  'emphasis',
  'caution',
  'quote',
])

export function SubtitleEditor({
  snapshot,
  disabled,
  applyOperation,
  graphicRegionNames,
}: SubtitleEditorProps) {
  const [subtitleIndex, setSubtitleIndex] = useState(0)
  const [teropIndex, setTeropIndex] = useState(0)
  const [navigationMessage, setNavigationMessage] = useState<string | null>(null)
  const subtitleButtons = useRef<Array<HTMLButtonElement | null>>([])
  const teropButtons = useRef<Array<HTMLButtonElement | null>>([])
  const requestPlayerSourceSeek = usePlayerSourceNavigation()
  const subtitle = snapshot.subtitles[subtitleIndex]
  const terop = snapshot.terops[teropIndex]
  const subtitleRegionNames = snapshot.layout.regions
    .filter((region) => region.role === 'subtitle')
    .map((region) => region.name)

  const navigateSubtitle = (cue: LayoutSubtitleCue): void => {
    const outcome = requestPlayerSourceSeek({
      projectId: snapshot.projectId,
      sourceTime: cue.sourceRange.start,
    })
    setNavigationMessage(navigationMessages[outcome])
  }

  return (
    <section className="layout-editor-card subtitle-editor" aria-labelledby="subtitle-editor-title">
      <div className="layout-card-heading">
        <div>
          <p className="section-kicker">Immutable cue revisions</p>
          <h3 id="subtitle-editor-title">字幕 Cue とテロップ</h3>
        </div>
        <span className="layout-evidence-badge">未永続化</span>
      </div>
      <p className="layout-card-note">
        字幕とテロップは別の revision evidence で編集します。文字列は React text node として表示し、HTML として解釈しません。
      </p>
      {navigationMessage !== null && (
        <p className="subtitle-navigation-status" role="status">
          {navigationMessage}
        </p>
      )}

      <div className="subtitle-editor-grid">
        <section aria-labelledby="subtitle-cue-list-title">
          <h4 id="subtitle-cue-list-title">字幕 Cue</h4>
          {snapshot.subtitles.length === 0 ? (
            <p className="layout-empty-list">字幕 Cue はありません。</p>
          ) : (
            <>
              <div className="cue-list" role="listbox" aria-label="字幕 Cue 一覧">
                {snapshot.subtitles.map((cue, index) => (
                  <button
                    key={cue.cueId}
                    ref={(element) => {
                      subtitleButtons.current[index] = element
                    }}
                    type="button"
                    role="option"
                    aria-selected={subtitleIndex === index}
                    tabIndex={subtitleIndex === index ? 0 : -1}
                    onClick={() => setSubtitleIndex(index)}
                    onKeyDown={(event) => handleListKey(
                      event,
                      index,
                      snapshot.subtitles.length,
                      setSubtitleIndex,
                      subtitleButtons.current,
                    )}
                  >
                    <strong>{cue.text}</strong>
                    <span>{formatRange(cue.outputRange)}</span>
                    <small>{cue.layoutResolution.status}</small>
                  </button>
                ))}
              </div>
              {subtitle !== undefined && (
                <SubtitleCueEditor
                  key={subtitle.revisionId}
                  cue={subtitle}
                  disabled={disabled}
                  applyOperation={applyOperation}
                  navigate={() => navigateSubtitle(subtitle)}
                  regionNames={subtitleRegionNames}
                />
              )}
            </>
          )}
        </section>

        <section aria-labelledby="terop-cue-list-title">
          <h4 id="terop-cue-list-title">テロップ</h4>
          {snapshot.terops.length === 0 ? (
            <p className="layout-empty-list">テロップはありません。</p>
          ) : (
            <>
              <div className="cue-list" role="listbox" aria-label="テロップ一覧">
                {snapshot.terops.map((cue, index) => (
                  <button
                    key={cue.cueId}
                    ref={(element) => {
                      teropButtons.current[index] = element
                    }}
                    type="button"
                    role="option"
                    aria-selected={teropIndex === index}
                    tabIndex={teropIndex === index ? 0 : -1}
                    onClick={() => setTeropIndex(index)}
                    onKeyDown={(event) => handleListKey(
                      event,
                      index,
                      snapshot.terops.length,
                      setTeropIndex,
                      teropButtons.current,
                    )}
                  >
                    <strong>{cue.text}</strong>
                    <span>{formatRange(cue.outputRange)}</span>
                    <small>{cue.sourceKind}</small>
                  </button>
                ))}
              </div>
              {terop !== undefined && (
                <TeropCueEditor
                  key={terop.revisionId}
                  cue={terop}
                  disabled={disabled}
                  applyOperation={applyOperation}
                  regionNames={graphicRegionNames}
                />
              )}
            </>
          )}
        </section>
      </div>
    </section>
  )
}

function SubtitleCueEditor({
  cue,
  disabled,
  applyOperation,
  navigate,
  regionNames,
}: Readonly<{
  cue: LayoutSubtitleCue
  disabled: boolean
  applyOperation: SubtitleEditorProps['applyOperation']
  navigate: () => void
  regionNames: readonly string[]
}>) {
  const initialPlacement = getInitialSubtitlePlacement(cue)
  const [editKind, setEditKind] = useState<LayoutSubtitleEdit['kind']>('text')
  const [text, setText] = useState(cue.text)
  const [timing, setTiming] = useState<LayoutTimeRangeText>(
    layoutTimeRangeToText(cue.outputRange),
  )
  const [style, setStyle] = useState<LayoutCueStyle>(cue.style)
  const [regionName, setRegionName] = useState(
    initialPlacement?.regionName ?? regionNames[0] ?? '',
  )
  const [rect, setRect] = useState<LayoutRectText>(
    initialPlacement === null ? emptyRectText() : layoutRectToText(initialPlacement.rect),
  )
  const [reason, setReason] = useState('')
  const [inputMessage, setInputMessage] = useState<string | null>(null)

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    if (!isRequiredText(reason)) {
      setInputMessage('前後空白のない変更理由を入力してください。')
      return
    }
    let edit: LayoutSubtitleEdit
    if (editKind === 'text') {
      if (!isRequiredText(text)) {
        setInputMessage('前後空白のない字幕本文を入力してください。')
        return
      }
      edit = { kind: 'text', text }
    } else if (editKind === 'timing') {
      const outputRange = parseLayoutTimeRange(timing)
      if (outputRange === null) {
        setInputMessage('時刻は約分済み signed Int64、duration は正数で入力してください。')
        return
      }
      edit = { kind: 'timing', outputRange }
    } else if (editKind === 'style') {
      edit = { kind: 'style', style }
    } else {
      const placement = parseExactLayoutRect(rect)
      if (!isRequiredText(regionName) || placement === null) {
        setInputMessage('既存 subtitle region と約分済み n/d の配置を指定してください。')
        return
      }
      edit = { kind: 'placement', regionName, rect: placement }
    }
    setInputMessage(null)
    applyOperation({
      kind: 'editSubtitle',
      cueId: cue.cueId,
      expectedRevisionId: cue.revisionId,
      expectedRevisionDigest: cue.revisionDigest,
      edit,
      reason,
    })
  }

  const constraints = [
    ...cue.layoutResolution.globalHardConstraints,
    ...cue.layoutResolution.evaluations.flatMap((item) => item.hardConstraints),
  ]
  return (
    <article
      className="cue-editor-card"
      data-layout-status={cue.layoutResolution.status}
    >
      <div className="cue-editor-heading">
        <div>
          <strong>Revision {cue.revisionCount}</strong>
          <span>{cue.style}</span>
        </div>
        <button type="button" disabled={disabled} onClick={navigate}>
          元時刻へ移動
        </button>
      </div>
      <p className="cue-text-preview">{cue.text}</p>
      {cue.layoutResolution.status === 'manualAdjustmentRequired' && (
        <div className="subtitle-no-solution" role="alert">
          <strong>自動配置の解がありません</strong>
          <p>fallback を適用せず、元時刻と constraint evidence を表示します。</p>
        </div>
      )}
      {constraints.length > 0 && (
        <ul className="subtitle-constraint-list">
          {[...new Set(constraints)].map((constraint) => (
            <li key={constraint}>{hardConstraintLabels[constraint]}</li>
          ))}
        </ul>
      )}
      <form className="cue-edit-form" onSubmit={submit}>
        <fieldset disabled={disabled}>
          <legend>字幕 Cue の typed child revision を作成する</legend>
          <label>
            <span>変更内容</span>
            <select
              name="subtitle-edit-kind"
              value={editKind}
              onChange={(event) => setEditKind(
                event.currentTarget.value as LayoutSubtitleEdit['kind'],
              )}
            >
              <option value="text">本文</option>
              <option value="timing">表示時刻</option>
              <option value="style">style</option>
              <option value="placement">配置</option>
            </select>
          </label>
          {editKind === 'text' && (
            <label>
              <span>字幕本文</span>
              <textarea
                value={text}
                maxLength={16_384}
                onChange={(event) => setText(event.currentTarget.value)}
              />
            </label>
          )}
          {editKind === 'timing' && (
            <TimeRangeFields
              prefix="subtitle"
              value={timing}
              onChange={setTiming}
            />
          )}
          {editKind === 'style' && (
            <CueStyleField
              name="subtitle-style"
              value={style}
              onChange={setStyle}
            />
          )}
          {editKind === 'placement' && (
            <>
              <RegionField
                name="subtitle-region"
                value={regionName}
                regionNames={regionNames}
                onChange={setRegionName}
              />
              <RectFields prefix="subtitle" value={rect} onChange={setRect} />
            </>
          )}
          <label>
            <span>変更理由</span>
            <input
              type="text"
              name="subtitle-reason"
              value={reason}
              maxLength={16_384}
              onChange={(event) => setReason(event.currentTarget.value)}
            />
          </label>
          {inputMessage !== null && <p role="alert">{inputMessage}</p>}
          <button type="submit">字幕 revision を作成</button>
        </fieldset>
      </form>
    </article>
  )
}

function TeropCueEditor({
  cue,
  disabled,
  applyOperation,
  regionNames,
}: Readonly<{
  cue: LayoutTeropCue
  disabled: boolean
  applyOperation: SubtitleEditorProps['applyOperation']
  regionNames: readonly string[]
}>) {
  const [editKind, setEditKind] = useState<LayoutTeropEdit['kind']>('text')
  const [text, setText] = useState(cue.text)
  const [timing, setTiming] = useState<LayoutTimeRangeText>(
    layoutTimeRangeToText(cue.outputRange),
  )
  const [style, setStyle] = useState<LayoutCueStyle>(cue.style)
  const [regionName, setRegionName] = useState(cue.regionName)
  const [reason, setReason] = useState('')
  const [inputMessage, setInputMessage] = useState<string | null>(null)

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    if (!isRequiredText(reason)) {
      setInputMessage('前後空白のない変更理由を入力してください。')
      return
    }
    let edit: LayoutTeropEdit
    if (editKind === 'text') {
      if (!isRequiredText(text)) {
        setInputMessage('前後空白のないテロップ本文を入力してください。')
        return
      }
      edit = { kind: 'text', text }
    } else if (editKind === 'timing') {
      const outputRange = parseLayoutTimeRange(timing)
      if (outputRange === null) {
        setInputMessage('時刻は約分済み signed Int64、duration は正数で入力してください。')
        return
      }
      edit = { kind: 'timing', outputRange }
    } else if (editKind === 'style') {
      edit = { kind: 'style', style }
    } else {
      if (!isRequiredText(regionName)) {
        setInputMessage('既存 layout region を選択してください。')
        return
      }
      edit = { kind: 'region', regionName }
    }
    setInputMessage(null)
    applyOperation({
      kind: 'editTerop',
      cueId: cue.cueId,
      expectedRevisionId: cue.revisionId,
      expectedRevisionDigest: cue.revisionDigest,
      edit,
      reason,
    })
  }

  return (
    <article className="cue-editor-card">
      <div className="cue-editor-heading">
        <div>
          <strong>Revision {cue.revisionCount}</strong>
          <span>{cue.regionName}</span>
        </div>
        <span className="layout-evidence-badge">{cue.sourceKind}</span>
      </div>
      <p className="cue-text-preview">{cue.text}</p>
      <form className="cue-edit-form" onSubmit={submit}>
        <fieldset disabled={disabled}>
          <legend>テロップ Cue の typed child revision を作成する</legend>
          <label>
            <span>変更内容</span>
            <select
              name="terop-edit-kind"
              value={editKind}
              onChange={(event) => setEditKind(
                event.currentTarget.value as LayoutTeropEdit['kind'],
              )}
            >
              <option value="text">本文</option>
              <option value="timing">表示時刻</option>
              <option value="style">style</option>
              <option value="region">region</option>
            </select>
          </label>
          {editKind === 'text' && (
            <label>
              <span>テロップ本文</span>
              <textarea
                value={text}
                maxLength={16_384}
                onChange={(event) => setText(event.currentTarget.value)}
              />
            </label>
          )}
          {editKind === 'timing' && (
            <TimeRangeFields
              prefix="terop"
              value={timing}
              onChange={setTiming}
            />
          )}
          {editKind === 'style' && (
            <CueStyleField
              name="terop-style"
              value={style}
              onChange={setStyle}
            />
          )}
          {editKind === 'region' && (
            <RegionField
              name="terop-region"
              value={regionName}
              regionNames={regionNames}
              onChange={setRegionName}
            />
          )}
          <label>
            <span>変更理由</span>
            <input
              type="text"
              name="terop-reason"
              value={reason}
              maxLength={16_384}
              onChange={(event) => setReason(event.currentTarget.value)}
            />
          </label>
          {inputMessage !== null && <p role="alert">{inputMessage}</p>}
          <button type="submit">テロップ revision を作成</button>
        </fieldset>
      </form>
    </article>
  )
}

function TimeRangeFields({
  prefix,
  value,
  onChange,
}: Readonly<{
  prefix: string
  value: LayoutTimeRangeText
  onChange: (value: LayoutTimeRangeText) => void
}>) {
  const field = (
    key: keyof LayoutTimeRangeText,
    label: string,
  ) => (
    <label>
      <span>{label}</span>
      <input
        type="text"
        name={`${prefix}-${key}`}
        value={value[key]}
        inputMode="numeric"
        onChange={(event) => onChange({
          ...value,
          [key]: event.currentTarget.value,
        })}
      />
    </label>
  )
  return (
    <div className="cue-exact-field-grid">
      {field('startValue', '開始 value')}
      {field('startRate', '開始 rate')}
      {field('durationValue', '長さ value')}
      {field('durationRate', '長さ rate')}
    </div>
  )
}

function CueStyleField({
  name,
  value,
  onChange,
}: Readonly<{
  name: string
  value: LayoutCueStyle
  onChange: (value: LayoutCueStyle) => void
}>) {
  return (
    <label>
      <span>Style</span>
      <select
        name={name}
        value={value}
        onChange={(event) => onChange(event.currentTarget.value as LayoutCueStyle)}
      >
        {cueStyles.map((option) => (
          <option key={option} value={option}>{option}</option>
        ))}
      </select>
    </label>
  )
}

function RegionField({
  name,
  value,
  regionNames,
  onChange,
}: Readonly<{
  name: string
  value: string
  regionNames: readonly string[]
  onChange: (value: string) => void
}>) {
  return (
    <label>
      <span>Layout region</span>
      <select
        name={name}
        value={value}
        onChange={(event) => onChange(event.currentTarget.value)}
      >
        {regionNames.length === 0 && <option value="">利用可能な region なし</option>}
        {regionNames.map((regionName) => (
          <option key={regionName} value={regionName}>{regionName}</option>
        ))}
      </select>
    </label>
  )
}

function RectFields({
  prefix,
  value,
  onChange,
}: Readonly<{
  prefix: string
  value: LayoutRectText
  onChange: (value: LayoutRectText) => void
}>) {
  return (
    <fieldset className="cue-exact-field-grid">
      <legend>Exact placement（n/d）</legend>
      {(['x', 'y', 'width', 'height'] as const).map((key) => (
        <label key={key}>
          <span>{key}</span>
          <input
            type="text"
            name={`${prefix}-${key}`}
            value={value[key]}
            inputMode="numeric"
            onChange={(event) => onChange({
              ...value,
              [key]: event.currentTarget.value,
            })}
          />
        </label>
      ))}
    </fieldset>
  )
}

function getInitialSubtitlePlacement(cue: LayoutSubtitleCue) {
  const selectedId = cue.layoutResolution.selectedCandidateId
  return cue.layoutResolution.evaluations.find(
    (evaluation) => evaluation.candidateId === selectedId,
  ) ?? cue.layoutResolution.evaluations[0] ?? null
}

function emptyRectText(): LayoutRectText {
  return Object.freeze({ x: '', y: '', width: '', height: '' })
}

function handleListKey(
  event: KeyboardEvent<HTMLButtonElement>,
  index: number,
  length: number,
  select: (index: number) => void,
  elements: Array<HTMLButtonElement | null>,
): void {
  const nextIndex = getRovingIndexAfterKey(index, event.key, length)
  if (nextIndex === null) {
    return
  }
  event.preventDefault()
  select(nextIndex)
  elements[nextIndex]?.focus()
}

function formatRange(range: LayoutSubtitleCue['outputRange']): string {
  return `${range.start.value}/${range.start.rate} + ${range.duration.value}/${range.duration.rate}`
}

function isRequiredText(value: string): boolean {
  return value.length > 0 && value === value.trim() && /\S/u.test(value)
}
