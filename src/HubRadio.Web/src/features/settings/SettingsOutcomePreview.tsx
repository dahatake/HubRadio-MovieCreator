import type { SourceOnlyProjectSummary } from '../../bridge/projectProtocol'
import { useLayoutEffect, useRef, useState } from 'react'
import type { PipelineRegenerationParams } from '../workflow/pipelineProtocol'
import { AppIcon } from '../../components/AppIcons'
import { deriveResultPresentation } from '../results/resultPresentation'
import {
  parseValue,
  type AnySettingsSnapshot,
  type SettingDefinition,
  type SettingSelection,
  type SettingValue,
} from './settingsProtocol'

export interface ExplicitRegenerationProps {
  readonly active?: boolean
  readonly params: PipelineRegenerationParams | null
  readonly disabledReason: string | null
  readonly pending: boolean
  /** One explicit acceptance; the owner rechecks saved settings and dirty tools. */
  readonly onConfirm: (params: PipelineRegenerationParams) => boolean
}
export type SettingsOutcomePreviewProps = (
  | Readonly<{
      mode: 'sourceOnly'
      sourceOnly: SourceOnlyProjectSummary
      snapshot?: never
      draftValues?: never
    }>
  | Readonly<{
      mode: 'snapshot'
      /** Null represents unavailable settings, never a request for global defaults. */
      snapshot: AnySettingsSnapshot | null
      /** Partial, unsaved overrides of snapshot.values; omitted keys stay unchanged. */
      draftValues?: readonly SettingSelection[]
      sourceOnly?: never
    }>
  ) & Readonly<{ regeneration?: ExplicitRegenerationProps }>

// Exact bundled settings keys, not render-profile fields or output catalog names.
const controls = [
  { key: 'output.landscapeResolution', label: '長尺動画（横長）の予定サイズ', format: formatResolution },
  { key: 'output.verticalResolution', label: '短尺動画（縦型）の予定サイズ', format: formatResolution },
  { key: 'subtitles.enabled', label: '字幕の予定', format: formatSubtitles },
  { key: 'project.verticalShortCandidateCount', label: '短尺候補の目標本数', format: (value: SettingValue) => formatTarget(value, '本') },
  { key: 'output.shortTargetDuration', label: '短尺動画の目標時間', format: (value: SettingValue) => formatTarget(value, '秒') },
  { key: 'thumbnails.candidateCount', label: 'サムネイル候補の目標枚数', format: (value: SettingValue) => formatTarget(value, '枚') },
] as const

export const SETTINGS_OUTCOME_CONTROL_KEYS = Object.freeze(controls.map(control => control.key))

/** Display only: no settings context, catalog fallback, media generation or bridge calls. */
export function SettingsOutcomePreview(props: SettingsOutcomePreviewProps) {
  return (
    <section className="completion-summary settings-outcome-preview" aria-label="設定による予定成果物">
      <h3><AppIcon name="video" />予定成果物</h3>
      <p>設定に基づく予定の要約です。実際の映像や生成数を示すものではありません。</p>
      {props.mode === 'sourceOnly'
        ? <SourceOnlyOutcome sourceOnly={props.sourceOnly} />
        : <SnapshotOutcome {...props} />}
      <p>実際の出力は未更新です。この表示や設定の保存だけでは再生成しません。</p>
      <p>生成・保存状況：未確認。品質評価：未評価（NOT EVALUATED）。</p>
      {props.regeneration !== undefined && <SettingsRegenerationAction {...props.regeneration} />}
    </section>
  )
}

function SourceOnlyOutcome({ sourceOnly }: { readonly sourceOnly: SourceOnlyProjectSummary }) {
  const presentation = deriveResultPresentation({ sourceOnlySummary: sourceOnly })
  return (
    <div data-preview-state={presentation.cards.length === 0 ? 'unavailable' : presentation.planState}>
      <h4>保存済み設定</h4>
      <p>この動画に固定された設定の要約です。現在の全体設定では補完しません。</p>
      {presentation.cards.length === 0
        ? <p>予定成果物は未取得です。</p>
        : (
            <dl className="review-list">
              {presentation.cards.map(card => (
                <div key={card.id} data-result-id={card.id}>
                  <dt>{card.title}</dt>
                  <dd>{card.plannedLabels.join('、')} <span className="setting-badge">予定</span></dd>
                </div>
              ))}
            </dl>
          )}
      <p>解像度・候補数・字幕設定の詳細は未取得です。</p>
    </div>
  )
}

function SnapshotOutcome({ snapshot, draftValues = [] }: Extract<SettingsOutcomePreviewProps, { mode: 'snapshot' }>) {
  if (snapshot === null) {
    return <p data-preview-state="unavailable">設定を取得できません。予定成果物と適用範囲は未取得です。</p>
  }

  const saved = new Map(snapshot.values.map(selection => [selection.key, selection.value]))
  const drafts = new Map(draftValues.map(selection => [selection.key, selection.value]))
  const definitions = new Map(snapshot.definitions.map(definition => [definition.key, definition]))
  const changedKeys = new Set(snapshot.definitions
    .filter(definition => drafts.has(definition.key) && !sameValue(saved.get(definition.key), drafts.get(definition.key)))
    .map(definition => definition.key))
  const rows = controls.flatMap(control => {
    const definition = definitions.get(control.key)
    if (definition === undefined) return []
    const current = saved.get(control.key)
    const proposed = drafts.has(control.key) ? drafts.get(control.key) : current
    return [{
      ...control,
      current: presentValue(definition, current, control.format),
      proposed: presentValue(definition, proposed, control.format),
      changed: changedKeys.has(control.key),
    }]
  })
  const hasChanges = changedKeys.size > 0
  const hasUnsupportedChanges = [...drafts.keys()].some(key => !definitions.has(key))
    || [...changedKeys].some(key => !controls.some(control => control.key === key))

  return (
    <div data-preview-state={rows.some(row => row.current.label !== null || row.proposed.label !== null) ? 'planned' : 'unavailable'}>
      <p>{snapshot.projectId === null
        ? '全体の既定設定：新しく作成する動画向けです。現在の動画や既存の子版には適用しません。'
        : 'この動画の設定：次回生成向けです。未保存の変更は保存後に使う予定で、既存の成果物や子版は更新しません。'}</p>
      <p role="status" aria-live="polite" data-changed={hasChanges}>
        {hasChanges ? '未保存の変更があります。' : '保存済み設定を表示しています（表示対象の設定に未保存の変更なし）。'}
      </p>
      {rows.length === 0
        ? <p>このスナップショットにはプレビュー対応の設定がありません。</p>
        : (
            <table>
              <caption>設定に基づく次回生成の予定</caption>
              <thead>
                <tr><th scope="col">項目</th><th scope="col">保存済み設定</th>{hasChanges && <th scope="col">未保存の変更</th>}</tr>
              </thead>
              <tbody>
                {rows.map(row => (
                  <tr key={row.key} data-setting-key={row.key} data-changed={row.changed}>
                    <th scope="row">{row.label}{row.changed && <span className="setting-badge">変更あり</span>}</th>
                    <td>{row.current.label ?? '未取得・未対応'}</td>
                    {hasChanges && <td>{row.changed ? row.proposed.invalid ? '入力値を確認' : row.proposed.label ?? '未取得・未対応' : '変更なし'}</td>}
                  </tr>
                ))}
              </tbody>
            </table>
          )}
      {hasUnsupportedChanges && <p>プレビュー未対応の設定の影響は表示しません。</p>}
      <p>候補数や時間は設定上の目標です。実際の生成数・時間は未確認です。</p>
    </div>
  )
}

function presentValue(
  definition: SettingDefinition,
  value: SettingValue | undefined,
  format: (value: SettingValue) => string | null,
): Readonly<{ label: string | null; invalid: boolean }> {
  // The definition is a gate, never a substitute for a missing saved/draft value.
  if (value === undefined) return { label: null, invalid: false }
  if (!acceptsPreviewValue(definition, value)) return { label: null, invalid: true }
  // A valid value can still use a format this preview does not support.
  return { label: format(value), invalid: false }
}

/** Local display gate for the six controls above; Core still validates saved settings. */
function acceptsPreviewValue(definition: SettingDefinition, value: SettingValue): boolean {
  if (parseValue(value) === null || parseValue(definition.defaultValue) === null || value.kind !== definition.defaultValue.kind) return false
  if (definition.allowedTextValues.length > 0
    && (value.kind !== 'text' || !definition.allowedTextValues.includes(value.textValue))) return false

  for (const [bound, isMinimum] of [[definition.minimum, true], [definition.maximum, false]] as const) {
    if (bound === null) continue
    if (parseValue(bound) === null) return false
    const comparison = comparePreviewNumeric(value, bound)
    // Reject nonnumeric or mismatched constraint kinds rather than coercing them.
    if (comparison === null || (isMinimum ? comparison < 0n : comparison > 0n)) return false
  }
  return true
}

function comparePreviewNumeric(left: SettingValue, right: SettingValue): bigint | null {
  if (left.kind === 'integer' && right.kind === 'integer') {
    return BigInt(left.integerValue) - BigInt(right.integerValue)
  }
  if (left.kind === 'rational' && right.kind === 'rational') {
    // parseValue has already required positive, nonzero denominators.
    return BigInt(left.numerator) * BigInt(right.denominator) - BigInt(right.numerator) * BigInt(left.denominator)
  }
  return null
}

function formatResolution(value: SettingValue): string | null {
  if (value.kind !== 'text') return null
  const dimensions = /^([1-9][0-9]*)x([1-9][0-9]*)$/.exec(value.textValue)
  const maximumDimension = 9_007_199_254_740_991n
  if (dimensions === null || BigInt(dimensions[1]!) > maximumDimension || BigInt(dimensions[2]!) > maximumDimension) return null
  return `${dimensions[1]} × ${dimensions[2]} px（幅 × 高さ）`
}

function formatSubtitles(value: SettingValue): string | null {
  return value.kind === 'boolean' ? value.booleanValue ? '字幕あり' : '字幕なし' : null
}

function formatTarget(value: SettingValue, unit: string): string | null {
  return value.kind === 'integer' && /^(0|[1-9][0-9]*)$/.test(value.integerValue)
    ? `${value.integerValue} ${unit}（目標）`
    : null
}

function sameValue(left: SettingValue | undefined, right: SettingValue | undefined): boolean {
  if (left === undefined || right === undefined) return left === right
  switch (left.kind) {
    case 'boolean': return right.kind === 'boolean' && left.booleanValue === right.booleanValue
    case 'integer': return right.kind === 'integer' && left.integerValue === right.integerValue
    case 'text': return right.kind === 'text' && left.textValue === right.textValue
    case 'rational': return right.kind === 'rational' && left.numerator === right.numerator && left.denominator === right.denominator
    case 'textList': return right.kind === 'textList' && left.textListValue.length === right.textListValue.length
      && left.textListValue.every((value, index) => value === right.textListValue[index])
  }
}

/** Also usable in the workflow toolbar without mounting another settings editor. */
export function SettingsRegenerationAction({ params, disabledReason, pending, onConfirm, active = true }: ExplicitRegenerationProps) {
  const [confirmation, setConfirmation] = useState<PipelineRegenerationParams | null>(null)
  const [rejected, setRejected] = useState(false)
  const cancel = useRef<HTMLButtonElement>(null)
  const opener = useRef<HTMLButtonElement>(null)
  const sending = useRef(false)
  const restoreFocus = useRef(false)
  const available = active && params !== null && disabledReason === null && !pending
  const same = confirmation !== null && params !== null && JSON.stringify(confirmation) === JSON.stringify(params)
  useLayoutEffect(() => {
    if (!active || opener.current?.closest('[hidden], [inert]')) return
    if (confirmation !== null) cancel.current?.focus()
    else if (restoreFocus.current) { restoreFocus.current = false; opener.current?.focus() }
  }, [confirmation, active])
  function close() { restoreFocus.current = true; setConfirmation(null); setRejected(false); sending.current = false }
  return <div className="settings-regeneration">
    <button ref={opener} type="button" disabled={!available || confirmation !== null} onClick={() => {
      if (available) { sending.current = false; setRejected(false); setConfirmation(Object.freeze({ ...params })) }
    }}>保存済み設定で再生成</button>
    {disabledReason !== null && <p role="status">{disabledReason}</p>}
    {pending && <p role="status">再生成の受付を確認しています。</p>}
    {confirmation !== null && <section role="alertdialog" aria-label="再生成の確認" onKeyDown={event => {
      if (event.key === 'Escape') { event.preventDefault(); close() }
    }}>
      <p>この動画の保存済み設定と現在の親版を固定して、新しい子版を生成します。以前の成果物は削除しません。</p>
      <p>親版: <code>{confirmation.expectedVersionId}</code> · <code>{confirmation.expectedVersionDigest}</code></p>
      <p>保存済み設定 revision {confirmation.expectedSettingsRevision} · <code>{confirmation.expectedSettingsSnapshotDigest}</code></p>
      {(!same || !available || rejected) && <p role="status">確認中に対象または編集状態が変わりました。取り消して状態を確認してください。</p>}
      <button type="button" disabled={!available || !same || rejected} onClick={() => {
        if (!available || !same || sending.current) return
        sending.current = true
        if (onConfirm(confirmation)) setConfirmation(null)
        else { sending.current = false; setRejected(true) }
      }}>確認して再生成</button>
      <button ref={cancel} type="button" onClick={close}>取消</button>
    </section>}
  </div>
}