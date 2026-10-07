import { useEffect, useId, useRef, useState } from 'react'
import type { SettingDefinition, SettingSelection, SettingValue, SettingsGroup } from './settingsProtocol'

export interface SettingsListProps {
  readonly definitions: readonly SettingDefinition[]
  readonly values: readonly SettingSelection[]
  readonly selectedKey: string | null
  readonly onSelect: (key: string) => void
  /** Keys with unsaved drafts, supplied by the parent rather than inferred from values. */
  readonly changedKeys?: readonly string[]
  /** Explicitly locks list controls; a pending save alone need not prevent selection. */
  readonly disabled?: boolean
}

const pageSize = 6
const groupNames: Readonly<Record<SettingsGroup, string>> = {
  projectAndOutput: 'Projectと出力',
  editing: '編集',
  subtitlesAndGraphics: '字幕とグラフィック',
  audioAndAssets: '音声と素材',
  privacyAndStorage: 'プライバシーと保存',
}
const groups = Object.keys(groupNames) as SettingsGroup[]

interface ListView {
  readonly searchQuery: string
  readonly activeGroup: SettingsGroup
  readonly advancedVisible: boolean
  readonly page: number
}

export function SettingsList({ definitions, values, selectedKey, onSelect, changedKeys = [], disabled = false }: SettingsListProps) {
  const id = useId()
  const selected = definitions.find(definition => definition.key === selectedKey)
  const [view, setView] = useState<ListView>(() => {
    const initial: ListView = { searchQuery: '', activeGroup: 'projectAndOutput', advancedVisible: false, page: 0 }
    return selected === undefined ? initial : revealDefinition(definitions, initial, selected)
  })
  const revealedKey = useRef(selected?.key ?? null)

  useEffect(() => {
    const key = selected?.key ?? null
    if (revealedKey.current === key) return
    revealedKey.current = key
    // Follow a new parent selection (including late definitions), not each filter or snapshot refresh.
    if (selected !== undefined) setView(current => revealDefinition(definitions, current, selected))
  }, [definitions, selected])

  const filtered = filterDefinitions(definitions, view)
  const pageCount = Math.ceil(filtered.length / pageSize)
  const page = Math.min(view.page, Math.max(0, pageCount - 1))
  const start = page * pageSize
  const rows = filtered.slice(start, start + pageSize)
  const currentValues = new Map(values.map(selection => [selection.key, selection.value]))
  const changed = new Set(changedKeys)
  const selectedVisible = rows.some(definition => definition.key === selectedKey)
  const itemsId = `${id}-items`

  return (
    <section className="settings-list" aria-labelledby={`${id}-title`}>
      <h3 id={`${id}-title`}>設定一覧</h3>
      <fieldset className="settings-list-groups">
        <legend>設定の分類</legend>
        <ul>
          {groups.map(group => (
            <li key={group}>
              <button type="button" aria-pressed={view.activeGroup === group} aria-controls={itemsId} disabled={disabled}
                onClick={() => setView(current => ({ ...current, activeGroup: group, page: 0 }))}>
                {groupNames[group]}
              </button>
            </li>
          ))}
        </ul>
      </fieldset>
      <div className="settings-list-controls">
        <label htmlFor={`${id}-search`}>設定を検索</label>
        <input id={`${id}-search`} type="search" value={view.searchQuery} disabled={disabled}
          autoComplete="off" aria-describedby={`${id}-search-hint`}
          onChange={event => {
            const searchQuery = event.currentTarget.value
            setView(current => ({ ...current, searchQuery, page: 0 }))
          }} />
        <p id={`${id}-search-hint`}>選択中の分類の名前・キー・説明を検索します。</p>
        <label htmlFor={`${id}-advanced`}>
          <input id={`${id}-advanced`} type="checkbox" checked={view.advancedVisible} disabled={disabled}
            onChange={event => {
              const advancedVisible = event.currentTarget.checked
              setView(current => ({ ...current, advancedVisible, page: 0 }))
            }} />
          詳細設定を表示
        </label>
      </div>
      <div className="settings-list-selection" role="group" aria-labelledby={`${id}-selection-title`}>
        <p id={`${id}-selection-title`}>選択中の設定</p>
        {selectedKey === null ? <p>未選択</p> : (
          <>
            {selected !== undefined && <p className="settings-list-selected-name">{selected.displayName}</p>}
            <code>{selectedKey}</code>
            {changed.has(selectedKey) && <span className="setting-badge">未保存</span>}
            {selected === undefined ? <p>この一覧に定義がありません。</p> : (
              <>
                {!selectedVisible && <p>現在の一覧には表示されていません。</p>}
                <button type="button" disabled={disabled} aria-controls={itemsId}
                  onClick={() => setView(current => revealDefinition(definitions, current, selected))}>
                  選択項目を表示
                </button>
              </>
            )}
          </>
        )}
      </div>
      <ul id={itemsId} className="settings-list-items" aria-label={`${groupNames[view.activeGroup]}の設定`}>
        {rows.map(definition => {
          const rowId = `${id}-${definition.key}`
          const value = currentValues.get(definition.key)
          const hasDraft = changed.has(definition.key)
          return (
            <li key={definition.key}>
              <button type="button" className="settings-list-row" disabled={disabled}
                aria-pressed={selectedKey === definition.key} aria-labelledby={`${rowId}-name`}
                aria-describedby={[
                  `${rowId}-key`, `${rowId}-value`,
                  definition.isAdvanced ? `${rowId}-advanced` : null,
                  hasDraft ? `${rowId}-draft` : null,
                ].filter(Boolean).join(' ')}
                onClick={() => onSelect(definition.key)}>
                <span id={`${rowId}-name`} className="settings-list-row-name">{definition.displayName}</span>
                <code id={`${rowId}-key`}>{definition.key}</code>
                <span id={`${rowId}-value`} className="settings-list-row-value">現在値：{value === undefined ? '未取得' : formatValue(value)}</span>
                {definition.isAdvanced && <span id={`${rowId}-advanced`} className="setting-badge">詳細</span>}
                {hasDraft && <span id={`${rowId}-draft`} className="setting-badge">未保存</span>}
              </button>
            </li>
          )
        })}
      </ul>
      {filtered.length === 0 && <p>該当する設定はありません。検索条件や詳細設定の表示を変更してください。</p>}
      <nav className="settings-list-pagination" aria-label="設定一覧のページ">
        <button type="button" disabled={disabled || page === 0} aria-controls={itemsId}
          onClick={() => setView(current => ({ ...current, page: Math.max(0, page - 1) }))}>前のページ</button>
        <p role="status" aria-live="polite" aria-atomic="true">
          {filtered.length === 0 ? '0件 · 0 / 0ページ' : `${start + 1}–${start + rows.length} / ${filtered.length}件 · ${page + 1} / ${pageCount}ページ`}
        </p>
        <button type="button" disabled={disabled || page + 1 >= pageCount} aria-controls={itemsId}
          onClick={() => setView(current => ({ ...current, page: Math.min(page + 1, pageCount - 1) }))}>次のページ</button>
      </nav>
    </section>
  )
}

function matchesQuery(definition: SettingDefinition, query: string): boolean {
  return [definition.displayName, definition.key, definition.description].some(text => text.toLowerCase().includes(query))
}

function filterDefinitions(definitions: readonly SettingDefinition[], view: ListView): readonly SettingDefinition[] {
  const query = view.searchQuery.trim().toLowerCase()
  return definitions.filter(definition => definition.group === view.activeGroup
    && (view.advancedVisible || !definition.isAdvanced) && matchesQuery(definition, query))
}

function revealDefinition(definitions: readonly SettingDefinition[], view: ListView, selected: SettingDefinition): ListView {
  const next: ListView = {
    ...view,
    activeGroup: selected.group,
    advancedVisible: view.advancedVisible || selected.isAdvanced,
    searchQuery: matchesQuery(selected, view.searchQuery.trim().toLowerCase()) ? view.searchQuery : '',
  }
  const index = filterDefinitions(definitions, next).findIndex(definition => definition.key === selected.key)
  return { ...next, page: Math.floor(Math.max(0, index) / pageSize) }
}

function formatValue(value: SettingValue): string {
  if (value.kind === 'boolean') return value.booleanValue ? '有効' : '無効'
  if (value.kind === 'integer') return value.integerValue
  if (value.kind === 'rational') return `${value.numerator}/${value.denominator}`
  if (value.kind === 'text') return value.textValue
  return value.textListValue.length === 0 ? 'なし' : value.textListValue.join('、')
}