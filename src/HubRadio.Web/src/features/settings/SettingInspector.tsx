import { useId, type FormEvent } from 'react'
import { AppIcon } from '../../components/AppIcons'
import { parseValue, type SettingDefinition, type SettingValue } from './settingsProtocol'

export interface SettingInspectorProps {
  readonly definition: SettingDefinition
  readonly value: SettingValue
  readonly initialValue: SettingValue
  /** Owned by the parent, including while this inspector is unmounted. */
  readonly draft: SettingValue
  readonly scope: 'global' | 'project'
  /** May be omitted when the parent header displays snapshot.profileVersion. */
  readonly profileVersion?: string
  readonly disabled: boolean
  readonly onChange: (value: SettingValue) => void
  readonly onSave: () => void
  readonly onReset: () => void
}

export function SettingInspector({ definition, value, initialValue, draft, scope, profileVersion, disabled, onChange, onSave, onReset }: SettingInspectorProps) {
  const id = `setting-inspector-${useId()}`
  const unit = unitSuffix(definition)
  const changed = !sameValue(draft, value)
  const differsFromInitial = !sameValue(value, initialValue)
  const error = validationMessage(definition, draft)
  const saveDisabled = disabled || !changed || error !== null
  const restoreLabel = scope === 'global' ? '復元先（プロファイル初期値）' : '復元先（案件開始時の初期値）'
  const resetLabel = scope === 'global' ? 'プロファイル初期値へ戻す' : '案件開始時の値へ戻す'
  const describedBy = `${id}-description ${id}-status${error === null ? '' : ` ${id}-error`}`
  function submit(event: FormEvent) {
    event.preventDefault()
    if (!saveDisabled) onSave()
  }

  return (
    <form className="setting-inspector setting-card" aria-labelledby={`${id}-title`} aria-describedby={`${id}-description`} noValidate onSubmit={submit}>
      <div className="setting-copy">
        <h3 id={`${id}-title`}>{definition.displayName}{definition.isAdvanced && <span className="setting-badge">詳細</span>}</h3>
        <p>{scope === 'global' ? '新しい動画の既定値' : 'この動画'}</p>
      </div>
      <dl className="setting-value-summary setting-current-value" tabIndex={0} aria-label="保存済みの現在値"><div><dt>現在値</dt><dd>{formatValue(value)}{unit}</dd></div></dl>
      <div className="field-group">
        {draft.kind === 'textList'
          ? <span id={`${id}-label`}>{definition.displayName}{unit}：未保存の値</span>
          : <label htmlFor={id}>{definition.displayName}{unit}：未保存の値</label>}
        <ValueControl id={id} definition={definition} value={draft} disabled={disabled} describedBy={describedBy} invalid={error !== null} onChange={onChange} />
        <p id={`${id}-status`} role="status" aria-live="polite">{changed ? '未保存の変更があります。' : '未保存の変更はありません。'}</p>
        {error !== null && <p id={`${id}-error`} className="field-error" role="alert">{error}</p>}
      </div>
      <div className="setting-actions">
        <button type="submit" className="primary-button" disabled={saveDisabled}><AppIcon name="save" /> 保存</button>
        <button type="button" disabled={disabled || !differsFromInitial} onClick={() => onReset()}>{resetLabel}</button>
      </div>
      <details onKeyDown={(event) => {
        if (event.key === 'Escape' && event.currentTarget.open) {
          event.preventDefault()
          event.currentTarget.open = false
          event.currentTarget.querySelector('summary')?.focus()
        }
      }}>
        <summary tabIndex={0}>初期値・用途・根拠</summary>
        <dl className="setting-value-summary">
          <div><dt>{restoreLabel}</dt><dd>{formatValue(initialValue)}{unit}</dd></div>
          <div><dt>プロファイル既定値</dt><dd>{formatValue(definition.defaultValue)}{unit}</dd></div>
          {profileVersion !== undefined && <div><dt>プロファイル版</dt><dd>v{profileVersion}</dd></div>}
          {definition.minimum !== null && <div><dt>最小値</dt><dd>{formatValue(definition.minimum)}{unit}</dd></div>}
          {definition.maximum !== null && <div><dt>最大値</dt><dd>{formatValue(definition.maximum)}{unit}</dd></div>}
          <div><dt>用途</dt><dd id={`${id}-description`}>{definition.description}</dd></div>
          <div><dt>根拠</dt><dd><ul>{definition.evidence.map((item) => <li key={`${item.kind}:${item.sourceId}`}>{item.sourceId} · {item.kind}</li>)}</ul></dd></div>
          <div><dt>設定キー</dt><dd><code>{definition.key}</code></dd></div>
        </dl>
      </details>
    </form>
  )
}

function unitSuffix(definition: SettingDefinition): string { return definition.unit === 'none' ? '' : ` (${definition.unit})` }

function formatValue(value: SettingValue): string {
  if (value.kind === 'boolean') return value.booleanValue ? '有効' : '無効'
  if (value.kind === 'integer') return value.integerValue
  if (value.kind === 'rational') return `${value.numerator}/${value.denominator}`
  if (value.kind === 'text') return value.textValue
  return value.textListValue.length === 0 ? '（なし）' : value.textListValue.join('\n')
}

function sameValue(left: SettingValue, right: SettingValue): boolean {
  if (left.kind === 'boolean') return right.kind === 'boolean' && left.booleanValue === right.booleanValue
  if (left.kind === 'integer') return right.kind === 'integer' && left.integerValue === right.integerValue
  if (left.kind === 'rational') return right.kind === 'rational' && left.numerator === right.numerator && left.denominator === right.denominator
  if (left.kind === 'text') return right.kind === 'text' && left.textValue === right.textValue
  return right.kind === 'textList' && left.textListValue.length === right.textListValue.length && left.textListValue.every((item, index) => item === right.textListValue[index])
}

const invalidMessages: Readonly<Record<SettingValue['kind'], string>> = {
  boolean: '設定値を確認してください。',
  integer: '整数を入力してください（64 ビット範囲、先頭の 0 なし）。',
  rational: '分子は整数、分母は 1 以上の整数を入力してください（64 ビット範囲、先頭の 0 なし）。',
  text: '1〜4096 文字で入力し、前後の空白や不正な文字を除いてください。',
  textList: '各項目を 1〜4096 文字、重複なしの 64 項目以内で入力し、空の項目・前後の空白・不正な文字を除いてください。',
}

function validationMessage(definition: SettingDefinition, value: SettingValue): string | null {
  if (value.kind !== definition.defaultValue.kind) return '設定の型が一致しません。'
  if (parseValue(value) === null) return invalidMessages[value.kind]
  if (value.kind === 'text' && definition.allowedTextValues.length > 0 && !definition.allowedTextValues.includes(value.textValue)) return '候補から値を選択してください。'
  if (definition.minimum !== null && compareNumeric(value, definition.minimum) < 0n) return `最小値 ${formatValue(definition.minimum)}${unitSuffix(definition)} 以上で入力してください。`
  if (definition.maximum !== null && compareNumeric(value, definition.maximum) > 0n) return `最大値 ${formatValue(definition.maximum)}${unitSuffix(definition)} 以下で入力してください。`
  return null
}

function compareNumeric(left: SettingValue, right: SettingValue): bigint {
  if (left.kind === 'integer' && right.kind === 'integer') return BigInt(left.integerValue) - BigInt(right.integerValue)
  if (left.kind === 'rational' && right.kind === 'rational') return BigInt(left.numerator) * BigInt(right.denominator) - BigInt(right.numerator) * BigInt(left.denominator)
  throw new Error('Only settings values of the same numeric kind can be compared.')
}

// Minimal local adaptation of SettingsPanel's private control. UX-025 owns shared extraction.
function ValueControl({ id, definition, value, disabled, describedBy, invalid, onChange }: { id: string; definition: SettingDefinition; value: SettingValue; disabled: boolean; describedBy: string; invalid: boolean; onChange: (value: SettingValue) => void }) {
  const attributes = { disabled, 'aria-describedby': describedBy, 'aria-invalid': invalid }
  const unit = unitSuffix(definition)
  if (value.kind === 'boolean') return <select {...attributes} id={id} value={String(value.booleanValue)} onChange={(event) => onChange({ kind: 'boolean', booleanValue: event.currentTarget.value === 'true' })}><option value="true">有効</option><option value="false">無効</option></select>
  if (value.kind === 'integer') return <input {...attributes} id={id} type="text" inputMode="numeric" required value={value.integerValue} onChange={(event) => onChange({ kind: 'integer', integerValue: event.currentTarget.value })} />
  if (value.kind === 'rational') return <span className="rational-input"><input {...attributes} id={id} type="text" inputMode="numeric" required aria-label={`${definition.displayName} 分子${unit}：未保存の値`} value={value.numerator} onChange={(event) => onChange({ ...value, numerator: event.currentTarget.value })} /><span aria-hidden="true">/</span><input {...attributes} id={`${id}-denominator`} type="text" inputMode="numeric" required aria-label={`${definition.displayName} 分母：未保存の値`} value={value.denominator} onChange={(event) => onChange({ ...value, denominator: event.currentTarget.value })} /></span>
  if (value.kind === 'text' && definition.allowedTextValues.length > 0) return <select {...attributes} id={id} required value={value.textValue} onChange={(event) => onChange({ kind: 'text', textValue: event.currentTarget.value })}>{!definition.allowedTextValues.includes(value.textValue) && <option value={value.textValue} disabled>{value.textValue || '選択してください'}</option>}{definition.allowedTextValues.map((item) => <option key={item} value={item}>{item}</option>)}</select>
  if (value.kind === 'text') return <textarea {...attributes} id={id} rows={3} required value={value.textValue} onChange={(event) => onChange({ kind: 'text', textValue: event.currentTarget.value })} />
  // Newlines belong to entries; keep each draft string intact.
  return (
    <div id={id} className="setting-list-editor" role="group" aria-labelledby={`${id}-label`}>
      {value.textListValue.map((item, index) => (
        <div key={index} className="field-group">
          <label htmlFor={`${id}-entry-${index}`}>{definition.displayName} 項目 {index + 1}{unit}：未保存の値</label>
          <textarea {...attributes} id={`${id}-entry-${index}`} rows={3} required value={item} onChange={(event) => {
            const next = event.currentTarget.value
            onChange({ kind: 'textList', textListValue: value.textListValue.map((entry, entryIndex) => entryIndex === index ? next : entry) })
          }} />
          <button type="button" disabled={disabled} aria-label={`${definition.displayName} 項目 ${index + 1} を削除`} onClick={() => onChange({ kind: 'textList', textListValue: value.textListValue.filter((_, entryIndex) => entryIndex !== index) })}>削除</button>
        </div>
      ))}
      <button type="button" disabled={disabled || value.textListValue.length >= 64} onClick={() => onChange({ kind: 'textList', textListValue: [...value.textListValue, ''] })}>項目を追加</button>
    </div>
  )
}