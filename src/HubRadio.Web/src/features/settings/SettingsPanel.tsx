import { useEffect, useState, type FormEvent, type KeyboardEvent } from 'react'
import { useSettingsState } from './SettingsStateProvider'
import type { SettingDefinition, SettingValue, SettingsGroup } from './settingsProtocol'
import type { SettingsError } from './settingsState'

const groupNames: Readonly<Record<SettingsGroup, string>> = {
  projectAndOutput: '案件と出力', editing: '編集', subtitlesAndGraphics: '字幕とグラフィック', audioAndAssets: '音声と素材', privacyAndStorage: 'プライバシーと保存',
}
const errorMessages: Readonly<Record<SettingsError, string>> = {
  forbidden: 'この設定を表示または変更する権限がありません。', notFound: '案件が見つかりません。', unbound: 'この案件には初期設定がまだ関連付けられていません。', conflict: '設定が別の操作で更新されました。再読み込みしてください。', invalid: '設定値を確認してください。', unavailable: '設定を利用できません。',
}
const groups = Object.keys(groupNames) as SettingsGroup[]
const completionMessages = {
  'settings.read': '設定を読み込みました。',
  'settings.update': '現在値を保存しました。',
  'settings.reset': '案件開始時の値へ戻しました。',
  'settings.resetAll': 'すべての設定を案件開始時の値へ戻しました。',
} as const

export function SettingsPanel() {
  const { state, changeProjectId, load, refresh, update, reset, resetAll, setAdvancedVisible, clearError } = useSettingsState()
  const busy = state.pendingMethod !== null
  const [activeGroup, setActiveGroup] = useState<SettingsGroup>(groups[0])
  function moveGroup(event: KeyboardEvent<HTMLButtonElement>, group: SettingsGroup) {
    const current = groups.indexOf(group)
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? groups.length - 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? (current + groups.length - 1) % groups.length : event.key === 'ArrowRight' || event.key === 'ArrowDown' ? (current + 1) % groups.length : -1
    if (next < 0) return
    event.preventDefault()
    setActiveGroup(groups[next])
    document.getElementById(`settings-tab-${groups[next]}`)?.focus()
  }
  return (
    <section className="workspace-panel settings-panel" aria-labelledby="settings-title">
      <div className="panel-heading-row">
        <div><p className="eyebrow">Project defaults</p><h2 id="settings-title">案件設定</h2></div>
        {state.snapshot !== null && <p>{state.snapshot.definitionCount} 項目 · revision {state.snapshot.revision}</p>}
      </div>
      <form className="settings-project-form" onSubmit={(event) => { event.preventDefault(); load() }}>
        <label htmlFor="settings-project-id">案件 ID</label>
        <input id="settings-project-id" value={state.projectIdInput} onChange={(event) => changeProjectId(event.currentTarget.value)} autoComplete="off" spellCheck={false} />
        <button type="submit" disabled={busy}>設定を読み込む</button>
        <button type="button" disabled={busy || state.snapshot === null} onClick={refresh}>再読み込み</button>
      </form>
      <div aria-live="polite" role="status">
        {busy && '設定を処理しています。'}
        {state.needsRefresh && '変更を続けるには設定を再読み込みしてください。'}
        {!busy && !state.needsRefresh && state.lastCompletedMethod !== null && completionMessages[state.lastCompletedMethod]}
      </div>
      {state.error !== null && <div className="inline-error" role="alert"><p>{errorMessages[state.error]}</p><button type="button" onClick={clearError}>閉じる</button></div>}
      {state.snapshot !== null && (
        <>
          <div className="settings-toolbar">
            <button type="button" aria-expanded={state.advancedVisible} onClick={() => setAdvancedVisible(!state.advancedVisible)}>{state.advancedVisible ? '詳細設定を閉じる' : '詳細設定を表示'}</button>
            <button type="button" className="danger-button" disabled={busy || state.needsRefresh} onClick={resetAll}>案件開始時の値へすべて戻す</button>
          </div>
          <p className="settings-profile">{state.snapshot.profileName} v{state.snapshot.profileVersion}</p>
          <div className="settings-group-tabs" role="tablist" aria-label="設定グループ">
            {groups.map((group) => <button id={`settings-tab-${group}`} key={group} type="button" role="tab" aria-selected={activeGroup === group} aria-controls={`settings-panel-${group}`} tabIndex={activeGroup === group ? 0 : -1} onClick={() => setActiveGroup(group)} onKeyDown={(event) => moveGroup(event, group)}>{groupNames[group]}</button>)}
          </div>
          {groups.map((group) => {
            const definitions = state.snapshot!.definitions.filter((item) => item.group === group && (state.advancedVisible || !item.isAdvanced))
            return <fieldset id={`settings-panel-${group}`} role="tabpanel" aria-labelledby={`settings-tab-${group}`} hidden={activeGroup !== group} className="settings-group" key={group}><legend>{groupNames[group]}</legend>{definitions.map((definition) => {
              const selection = state.snapshot!.values.find((item) => item.key === definition.key)!
              const initial = state.snapshot!.initialValues.find((item) => item.key === definition.key)!
              return <SettingEditor key={`${definition.key}:${state.snapshot!.revision}`} definition={definition} value={selection.value} initialValue={initial.value} disabled={busy || state.needsRefresh} onSave={(value) => update(definition.key, value)} onReset={() => reset(definition.key)} />
            })}</fieldset>
          })}
        </>
      )}
    </section>
  )
}

function SettingEditor({ definition, value, initialValue, disabled, onSave, onReset }: { definition: SettingDefinition; value: SettingValue; initialValue: SettingValue; disabled: boolean; onSave: (value: SettingValue) => void; onReset: () => void }) {
  const [draft, setDraft] = useState(value)
  useEffect(() => setDraft(value), [value])
  const changed = JSON.stringify(draft) !== JSON.stringify(value)
  const differsFromInitial = JSON.stringify(value) !== JSON.stringify(initialValue)
  const id = `setting-${definition.key.replaceAll('.', '-')}`
  function submit(event: FormEvent) { event.preventDefault(); onSave(draft) }
  return (
    <form className="setting-card" onSubmit={submit} aria-describedby={`${id}-description ${id}-evidence`}>
      <div className="setting-copy"><label htmlFor={id}>{definition.displayName}{definition.isAdvanced && <span className="setting-badge">詳細</span>}</label><p id={`${id}-description`}>{definition.description}</p><code>{definition.key}</code></div>
      <dl className="setting-value-summary"><div><dt>現在値</dt><dd>{formatValue(value)}</dd></div><div><dt>案件開始時の初期値</dt><dd>{formatValue(initialValue)}</dd></div><div><dt>プロファイル既定値</dt><dd>{formatValue(definition.defaultValue)}</dd></div></dl>
      <ValueControl id={id} definition={definition} value={draft} disabled={disabled} onChange={setDraft} />
      <div className="setting-actions"><button type="submit" disabled={disabled || !changed}>保存</button><button type="button" disabled={disabled || !differsFromInitial} onClick={onReset}>案件開始時の値へ戻す</button></div>
      <details id={`${id}-evidence`} onKeyDown={(event) => { if (event.key === 'Escape' && event.currentTarget.open) { event.preventDefault(); event.currentTarget.open = false; event.currentTarget.querySelector('summary')?.focus() } }}><summary tabIndex={0}>根拠</summary><ul>{definition.evidence.map((item) => <li key={`${item.kind}:${item.sourceId}`}>{item.sourceId} · {item.kind}</li>)}</ul></details>
    </form>
  )
}

function formatValue(value: SettingValue): string {
  if (value.kind === 'boolean') return value.booleanValue ? '有効' : '無効'
  if (value.kind === 'integer') return value.integerValue
  if (value.kind === 'rational') return `${value.numerator}/${value.denominator}`
  if (value.kind === 'text') return value.textValue
  return value.textListValue.join('、')
}

function ValueControl({ id, definition, value, disabled, onChange }: { id: string; definition: SettingDefinition; value: SettingValue; disabled: boolean; onChange: (value: SettingValue) => void }) {
  const unit = definition.unit === 'none' ? '' : ` (${definition.unit})`
  if (value.kind === 'boolean') return <select id={id} disabled={disabled} value={String(value.booleanValue)} onChange={(event) => onChange({ kind: 'boolean', booleanValue: event.currentTarget.value === 'true' })}><option value="true">有効</option><option value="false">無効</option></select>
  if (value.kind === 'integer') return <input id={id} disabled={disabled} type="number" value={value.integerValue} min={definition.minimum?.kind === 'integer' ? definition.minimum.integerValue : undefined} max={definition.maximum?.kind === 'integer' ? definition.maximum.integerValue : undefined} aria-label={`${definition.displayName}${unit}`} onChange={(event) => onChange({ kind: 'integer', integerValue: event.currentTarget.value })} />
  if (value.kind === 'rational') return <span className="rational-input"><input id={id} disabled={disabled} inputMode="numeric" aria-label={`${definition.displayName} 分子${unit}`} value={value.numerator} onChange={(event) => onChange({ ...value, numerator: event.currentTarget.value })} /><span aria-hidden="true">/</span><input disabled={disabled} inputMode="numeric" aria-label={`${definition.displayName} 分母`} value={value.denominator} onChange={(event) => onChange({ ...value, denominator: event.currentTarget.value })} /></span>
  if (value.kind === 'text' && definition.allowedTextValues.length > 0) return <select id={id} disabled={disabled} value={value.textValue} onChange={(event) => onChange({ kind: 'text', textValue: event.currentTarget.value })}>{definition.allowedTextValues.map((item) => <option key={item}>{item}</option>)}</select>
  if (value.kind === 'text') return <input id={id} disabled={disabled} value={value.textValue} onChange={(event) => onChange({ kind: 'text', textValue: event.currentTarget.value })} />
  return <textarea id={id} disabled={disabled} rows={Math.max(2, value.textListValue.length)} value={value.textListValue.join('\n')} onChange={(event) => onChange({ kind: 'textList', textListValue: event.currentTarget.value.split('\n').filter((item) => item.length > 0) })} />
}
