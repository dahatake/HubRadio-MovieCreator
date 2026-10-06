// FR-050: which settings the settings screen may edit, and with which choices.
// The catalog (settingEditorCatalog.json) is shared with the .NET tests that check every
// offered choice against the generation code (SettingEditorCatalogTests).
import catalogJson from './settingEditorCatalog.json'
import type { SettingDefinition, SettingValue, SettingsUnit } from './settingsProtocol'

export type FixedSettingReason = 'singleValue' | 'runtimeFixed' | 'noEffect'

export interface SettingChoice {
  /** Stable radio value: `true`/`false`, the integer, `numerator/denominator`, or the text. */
  readonly id: string
  readonly value: SettingValue
  readonly label: string
}

export interface SettingMultipleOption {
  readonly value: string
  readonly label: string
}

export type SettingEditor =
  | Readonly<{ kind: 'choices'; choices: readonly SettingChoice[] }>
  | Readonly<{ kind: 'multiple'; options: readonly SettingMultipleOption[] }>
  | Readonly<{ kind: 'input' }>

/** Integer ranges up to this many values are offered as choices when the catalog does not list the key. */
export const maximumDerivedRangeChoices = 31

type CatalogEditor =
  | Readonly<{ control: 'range' }>
  | Readonly<{ control: 'boolean' }>
  | Readonly<{ control: 'choices'; values: readonly string[] }>
  | Readonly<{ control: 'multiple'; options: readonly SettingMultipleOption[] }>

interface SettingEditorCatalog {
  readonly fixed: ReadonlyMap<string, FixedSettingReason>
  readonly editors: ReadonlyMap<string, CatalogEditor>
}

export const settingEditorCatalog: SettingEditorCatalog = parseCatalog(catalogJson as unknown)

function parseCatalog(source: unknown): SettingEditorCatalog {
  const invalid = () => new Error('The setting editor catalog is invalid.')
  if (!isRecord(source) || source.schema !== 'hubradio.setting-editor-catalog.v1' || !isRecord(source.fixed) || !isRecord(source.editors)) throw invalid()
  const fixed = new Map<string, FixedSettingReason>()
  for (const [key, reason] of Object.entries(source.fixed)) {
    if (reason !== 'singleValue' && reason !== 'runtimeFixed' && reason !== 'noEffect') throw invalid()
    fixed.set(key, reason)
  }
  const editors = new Map<string, CatalogEditor>()
  for (const [key, entry] of Object.entries(source.editors)) {
    if (fixed.has(key) || !isRecord(entry)) throw invalid()
    if (entry.control === 'range' || entry.control === 'boolean') editors.set(key, { control: entry.control })
    else if (entry.control === 'choices' && Array.isArray(entry.values) && entry.values.every(item => typeof item === 'string')) editors.set(key, { control: 'choices', values: [...entry.values] as string[] })
    else if (entry.control === 'multiple' && Array.isArray(entry.options) && entry.options.every(item => isRecord(item) && typeof item.value === 'string' && typeof item.label === 'string')) {
      editors.set(key, { control: 'multiple', options: (entry.options as { value: string; label: string }[]).map(item => ({ value: item.value, label: item.label })) })
    } else throw invalid()
  }
  return { fixed, editors }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/**
 * Returns the editor for a setting the user can actually change, or null when the
 * settings screen must not show it (FR-050.1). Unknown keys are derived from their type (FR-050.6).
 */
export function getSettingEditor(definition: SettingDefinition): SettingEditor | null {
  if (settingEditorCatalog.fixed.has(definition.key)) return null
  const entry = settingEditorCatalog.editors.get(definition.key)
  const kind = definition.defaultValue.kind
  if (entry !== undefined) {
    if (entry.control === 'boolean' && kind === 'boolean') return booleanEditor()
    if (entry.control === 'range' && kind === 'integer') return atLeastTwo(rangeChoices(definition, Number.POSITIVE_INFINITY))
    if (entry.control === 'choices' && (kind === 'integer' || kind === 'rational')) return atLeastTwo(listedChoices(definition, entry.values))
    if (entry.control === 'multiple' && kind === 'textList') return { kind: 'multiple', options: entry.options }
  }
  return derivedEditor(definition)
}

/** Editor for any definition, including hidden ones, so the inspector can still render a fixed key. */
export function getAnySettingEditor(definition: SettingDefinition): SettingEditor {
  const editor = getSettingEditor(definition)
  if (editor !== null) return editor
  if (definition.defaultValue.kind === 'boolean') return booleanEditor()
  if (definition.defaultValue.kind === 'text' && definition.allowedTextValues.length > 0) return textEditor(definition)
  return { kind: 'input' }
}

export function isSettingEditable(definition: SettingDefinition): boolean {
  return getSettingEditor(definition) !== null
}

function derivedEditor(definition: SettingDefinition): SettingEditor | null {
  const value = definition.defaultValue
  if (value.kind === 'boolean') return booleanEditor()
  if (value.kind === 'text') {
    if (definition.allowedTextValues.length === 1) return null
    return definition.allowedTextValues.length > 1 ? textEditor(definition) : { kind: 'input' }
  }
  if (value.kind === 'integer' && definition.minimum?.kind === 'integer' && definition.maximum?.kind === 'integer') {
    const count = BigInt(definition.maximum.integerValue) - BigInt(definition.minimum.integerValue) + 1n
    if (count <= 1n) return null
    if (count <= BigInt(maximumDerivedRangeChoices)) return { kind: 'choices', choices: rangeChoices(definition, maximumDerivedRangeChoices) }
  }
  return { kind: 'input' }
}

function atLeastTwo(choices: readonly SettingChoice[]): SettingEditor | null {
  return choices.length < 2 ? null : { kind: 'choices', choices }
}

function booleanEditor(): SettingEditor {
  return { kind: 'choices', choices: [
    { id: 'true', value: { kind: 'boolean', booleanValue: true }, label: '有効' },
    { id: 'false', value: { kind: 'boolean', booleanValue: false }, label: '無効' },
  ] }
}

function textEditor(definition: SettingDefinition): SettingEditor {
  return { kind: 'choices', choices: definition.allowedTextValues.map(item => ({ id: item, value: { kind: 'text', textValue: item }, label: item })) }
}

function rangeChoices(definition: SettingDefinition, limit: number): readonly SettingChoice[] {
  if (definition.minimum?.kind !== 'integer' || definition.maximum?.kind !== 'integer') return []
  const choices: SettingChoice[] = []
  for (let item = BigInt(definition.minimum.integerValue); item <= BigInt(definition.maximum.integerValue) && choices.length < limit; item += 1n) {
    const value: SettingValue = { kind: 'integer', integerValue: item.toString() }
    choices.push({ id: settingChoiceId(value), value, label: formatSettingChoice(definition.unit, value) })
  }
  return choices
}

function listedChoices(definition: SettingDefinition, values: readonly string[]): readonly SettingChoice[] {
  return values.flatMap(text => {
    const value = parseListedValue(definition.defaultValue.kind, text)
    if (value === null || !withinRange(definition, value)) return []
    return [{ id: settingChoiceId(value), value, label: formatSettingChoice(definition.unit, value) }]
  })
}

function parseListedValue(kind: SettingValue['kind'], text: string): SettingValue | null {
  if (kind === 'integer' && /^-?(0|[1-9][0-9]*)$/u.test(text)) return { kind: 'integer', integerValue: text }
  const match = /^(-?(?:0|[1-9][0-9]*))\/([1-9][0-9]*)$/u.exec(text)
  if (kind === 'rational' && match !== null) return { kind: 'rational', numerator: match[1]!, denominator: match[2]! }
  return null
}

function withinRange(definition: SettingDefinition, value: SettingValue): boolean {
  const below = definition.minimum !== null && compare(value, definition.minimum) < 0n
  const above = definition.maximum !== null && compare(value, definition.maximum) > 0n
  return !below && !above
}

function compare(left: SettingValue, right: SettingValue): bigint {
  const [ln, ld] = fraction(left)
  const [rn, rd] = fraction(right)
  return ln * rd - rn * ld
}

function fraction(value: SettingValue): readonly [bigint, bigint] {
  if (value.kind === 'integer') return [BigInt(value.integerValue), 1n]
  if (value.kind === 'rational') return [BigInt(value.numerator), BigInt(value.denominator)]
  throw new Error('Only numeric settings values can be compared.')
}

export function settingChoiceId(value: SettingValue): string {
  if (value.kind === 'boolean') return String(value.booleanValue)
  if (value.kind === 'integer') return value.integerValue
  if (value.kind === 'rational') return `${value.numerator}/${value.denominator}`
  if (value.kind === 'text') return value.textValue
  return value.textListValue.join('\n')
}

/** The choice equal to the value (rationals compare by value, so 10/12 selects 5/6), if any. */
export function findSettingChoice(choices: readonly SettingChoice[], value: SettingValue): SettingChoice | undefined {
  return choices.find(choice => {
    if (choice.value.kind !== value.kind) return false
    if (value.kind === 'rational' && choice.value.kind === 'rational') {
      try { return value.denominator !== '0' && compare(choice.value, value) === 0n } catch { return false }
    }
    return settingChoiceId(choice.value) === settingChoiceId(value)
  })
}

const unitText: Readonly<Partial<Record<SettingsUnit, readonly [string, string]>>> = {
  seconds: ['', ' 秒'],
  milliseconds: ['', ' ミリ秒'],
  frames: ['', ' フレーム'],
  fullWidthCharacters: ['全角 ', ' 文字'],
  charactersPerSecond: ['1秒に ', ' 文字'],
  loudnessUnitsFullScale: ['', ' LUFS'],
  decibelsFullScale: ['', ' dBFS'],
  decibels: ['', ' dB'],
  percent: ['', ' %'],
  hertz: ['', ' Hz'],
}

/** Choice wording (FR-050 table T2). */
export function formatSettingChoice(unit: SettingsUnit, value: SettingValue): string {
  if (value.kind === 'boolean') return value.booleanValue ? '有効' : '無効'
  if (value.kind === 'text') return value.textValue
  if (value.kind === 'textList') return value.textListValue.join('、')
  const [prefix, suffix] = unitText[unit] ?? ['', '']
  if (value.kind === 'integer') {
    if (unit === 'bitsPerSecond') return formatBitrate(BigInt(value.integerValue))
    return `${prefix}${value.integerValue}${suffix}`
  }
  const numerator = BigInt(value.numerator)
  const denominator = BigInt(value.denominator)
  if (denominator === 1n) return `${prefix}${numerator}${suffix}`
  const exact = (numerator * 100n) % denominator === 0n
  const rounded = Math.round(Number(numerator) / Number(denominator) * 100) / 100
  return `${exact ? '' : '約'}${prefix}${rounded}${suffix}（${value.numerator}/${value.denominator}）`
}

function formatBitrate(value: bigint): string {
  if (value !== 0n && value % 1_000_000n === 0n) return `${value / 1_000_000n} Mbps`
  if (value % 1000n === 0n) return `${value / 1000n} kbps`
  return `${value} bps`
}
