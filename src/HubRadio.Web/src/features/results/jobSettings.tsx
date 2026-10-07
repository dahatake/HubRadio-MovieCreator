import settingEditorCatalog from '../settings/settingEditorCatalog.json'
import { settingHelpEntries } from '../settings/settingHelp'
import type { SettingValue } from '../settings/settingsProtocol'
import type { PipelineVersionEntry, PipelineVersionSetting } from '../workflow/pipelineProtocol'

const editableKeys = new Set(Object.keys((settingEditorCatalog as { readonly editors: Readonly<Record<string, unknown>> }).editors))

/** FR-055.3: only the settings the settings screen offers (FR-050 `editors`) are shown. */
export function editableJobSettings(entry: PipelineVersionEntry): readonly PipelineVersionSetting[] {
  return (entry.settings ?? []).filter(item => editableKeys.has(item.key))
}

/** FR-055.3: the keys whose value differs from the previous (older) Job; keys the previous Job lacks are not counted. */
export function changedJobSettingKeys(entry: PipelineVersionEntry, previous: PipelineVersionEntry | undefined): readonly string[] {
  if (previous === undefined) return []
  const before = new Map((previous.settings ?? []).map(item => [item.key, item] as const))
  return editableJobSettings(entry).filter(item => {
    const old = before.get(item.key)
    return old !== undefined && settingValueText(old) !== settingValueText(item)
  }).map(item => item.key)
}

/** FR-055.3: a Job's settings that differ between Jobs, marking what changed from the previous Job. */
export function VersionSettings({ entry, previous }: Readonly<{ entry: PipelineVersionEntry, previous: PipelineVersionEntry | undefined }>) {
  const settings = editableJobSettings(entry)
  if (entry.settings === undefined) return null
  if (settings.length === 0) {
    return <p className="result-gallery-note">ほかの動画と同じ設定で作りました。</p>
  }
  const before = new Map((previous?.settings ?? []).map(item => [item.key, item] as const))
  return (
    <dl className="version-video-settings" aria-label="この動画の設定（動画ごとに違う項目）">
      {settings.map(item => {
        const old = before.get(item.key)
        const changed = previous !== undefined && old !== undefined && settingValueText(old) !== settingValueText(item)
        return (
          <div key={item.key} data-setting-key={item.key} data-setting-changed={changed}>
            <dt>{settingLabel(item.key)}</dt>
            <dd>
              {settingValueText(item)}
              {changed && <span className="version-setting-change">（前の動画: {settingValueText(old)} から変更）</span>}
            </dd>
          </div>
        )
      })}
    </dl>
  )
}

export function settingLabel(key: string): string {
  return settingHelpEntries[key]?.[0] ?? '設定'
}

const catalogOptions = Object.entries((settingEditorCatalog as {
  readonly editors: Readonly<Record<string, { readonly options?: readonly { readonly value: string, readonly label: string }[] }>>
}).editors).flatMap(([key, editor]) => (editor.options ?? []).map(option => [key, option.value, option.label] as const))
const optionLabels = new Map<string, string>(catalogOptions.map(([, value, label]) => [value, label] as const))
const keyedOptionLabels = new Map<string, string>(catalogOptions.map(([key, value, label]) => [`${key}\u0000${value}`, label] as const))

/** Plain text of a setting value; rationals are shown as decimals (5/6 → 0.83). */
export function settingValueText(setting: PipelineVersionSetting): string {
  const value: SettingValue = setting.value
  switch (value.kind) {
    case 'boolean': return value.booleanValue ? 'オン' : 'オフ'
    case 'integer': return value.integerValue
    case 'rational': {
      const decimal = Number(value.numerator) / Number(value.denominator)
      return Number.isInteger(decimal) ? String(decimal) : decimal.toFixed(2).replace(/0$/, '')
    }
    case 'text': return keyedOptionLabels.get(`${setting.key}\u0000${value.textValue}`) ?? value.textValue
    case 'textList': return value.textListValue.length === 0 ? 'なし' : value.textListValue.map(item => optionLabels.get(item) ?? item).join('・')
  }
}
