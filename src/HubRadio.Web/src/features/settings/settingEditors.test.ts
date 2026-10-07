import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import { findSettingChoice, formatSettingChoice, getSettingEditor, isSettingEditable, maximumDerivedRangeChoices, settingEditorCatalog } from './settingEditors'
import type { SettingDefinition, SettingValue, SettingsGroup, SettingsUnit } from './settingsProtocol'

const here = dirname(fileURLToPath(import.meta.url))

interface ProfileValue { readonly kind: SettingValue['kind']; readonly boolean?: boolean; readonly integer?: number; readonly numerator?: number; readonly denominator?: number; readonly text?: string; readonly values?: readonly string[] }
interface ProfileSetting {
  readonly key: string; readonly group: SettingsGroup; readonly displayName: string; readonly description: string; readonly isAdvanced: boolean
  readonly unit: SettingsUnit; readonly defaultValue: ProfileValue; readonly minimum: ProfileValue | null; readonly maximum: ProfileValue | null; readonly allowedTextValues: readonly string[]
}

function toValue(value: ProfileValue): SettingValue {
  if (value.kind === 'boolean') return { kind: 'boolean', booleanValue: value.boolean! }
  if (value.kind === 'integer') return { kind: 'integer', integerValue: String(value.integer) }
  if (value.kind === 'rational') return { kind: 'rational', numerator: String(value.numerator), denominator: String(value.denominator) }
  if (value.kind === 'text') return { kind: 'text', textValue: value.text! }
  return { kind: 'textList', textListValue: value.values! }
}

function loadProfile(version: string): readonly SettingDefinition[] {
  const path = resolve(here, `../../../../HubRadio.App/DefaultSettings/Profiles/${version}.json`)
  return (JSON.parse(readFileSync(path, 'utf8')) as { settings: readonly ProfileSetting[] }).settings.map(item => ({
    key: item.key, group: item.group, displayName: item.displayName, description: item.description, isAdvanced: item.isAdvanced, unit: item.unit,
    defaultValue: toValue(item.defaultValue), minimum: item.minimum === null ? null : toValue(item.minimum), maximum: item.maximum === null ? null : toValue(item.maximum),
    allowedTextValues: item.allowedTextValues, evidence: [],
  }))
}

const changeable = [
  'project.verticalShortCandidateCount', 'output.endScreenDuration', 'output.standardFrameRateBitrate', 'output.highFrameRateBitrate', 'output.monoBitrate', 'output.stereoBitrate',
  'editing.shortHookMaximumStart',
  'thumbnails.candidateCount', 'subtitles.platformUiMaskWarningRequired', 'subtitles.horizontalLineLengthMaximum', 'subtitles.verticalLineLengthMaximum', 'subtitles.lineCountMaximum',
  'subtitles.readingSpeedMaximum', 'subtitles.cueMinimumDuration', 'subtitles.cueMaximumDuration', 'subtitles.cueGapMinimum', 'subtitles.landscapeLineHeight', 'subtitles.verticalLineHeight',
  'assets.musicEligibleSections', 'audio.internalOnlineTargetLoudness', 'audio.truePeakMaximum', 'audio.internalOnlineLoudnessTolerance',
]
// FR-057〜FR-060: settings that only profile 1.2.0 defines.
const addedIn120 = ['editing.pauseTightening', 'output.longFormOrientation', 'subtitles.emphasisTelopAmount', 'audio.soundEffectsEnabled']

function unknown(defaultValue: SettingValue, overrides: Partial<SettingDefinition> = {}): SettingDefinition {
  return { key: 'future.setting', group: 'editing', isAdvanced: false, displayName: 'Future', description: 'Future setting.', unit: 'none',
    defaultValue, minimum: null, maximum: null, allowedTextValues: [], evidence: [], ...overrides }
}
const integer = (integerValue: string): SettingValue => ({ kind: 'integer', integerValue })

describe('FR-050 setting editors', () => {
  // FR-050.1 AC-025.1
  it.each([['1.1.0', 123, changeable], ['1.2.0', 127, [...changeable, ...addedIn120]]] as const)('shows exactly the changeable settings of profile %s', (version, count, keys) => {
    const profile = loadProfile(version)
    expect(profile).toHaveLength(count)
    expect(profile.filter(isSettingEditable).map(item => item.key).sort()).toEqual([...keys].sort())
    expect(new Set(profile.filter(isSettingEditable).map(item => item.group))).toEqual(new Set(['projectAndOutput', 'editing', 'subtitlesAndGraphics', 'audioAndAssets']))
    for (const item of profile.filter(isSettingEditable)) {
      const editor = getSettingEditor(item)!
      expect(editor.kind).not.toBe('input')
      // Every offered value is valid for the definition and the default is always offered.
      if (editor.kind === 'choices') expect(findSettingChoice(editor.choices, item.defaultValue)).toBeDefined()
    }
  })

  // FR-050.1 AC-025.1, FR-048.5: profile 1.0.0 fixes the thumbnail count and has no hook range.
  it('hides the settings that profile 1.0.0 does not let change', () => {
    const profile = loadProfile('1.0.0')
    expect(profile.filter(isSettingEditable).map(item => item.key).sort())
      .toEqual(changeable.filter(key => key !== 'thumbnails.candidateCount' && key !== 'editing.shortHookMaximumStart').sort())
  })

  // FR-057 FR-058 FR-060: the new text settings are picked from labelled choices, never typed.
  it('offers the profile 1.2.0 text settings as labelled choices', () => {
    const profile = loadProfile('1.2.0')
    const labels = (key: string) => {
      const editor = getSettingEditor(profile.find(item => item.key === key)!)
      return editor?.kind === 'choices' ? editor.choices.map(choice => [choice.id, choice.label]) : null
    }
    expect(labels('editing.pauseTightening')).toEqual([['weak', '弱め（2秒以上の間を詰める）'], ['standard', '標準（0.7秒以上の間を詰める）'], ['strong', '強め（0.4秒以上の間を詰める）']])
    expect(labels('output.longFormOrientation')).toEqual([['followSource', '元動画の向きに合わせる'], ['landscape', '常に横長（16:9）']])
    expect(labels('subtitles.emphasisTelopAmount')).toEqual([['few', '少なめ'], ['standard', '標準'], ['many', '多め']])
    expect(labels('audio.soundEffectsEnabled')).toEqual([['true', '有効'], ['false', '無効']])
  })

  // FR-050.7
  it('classifies every shipped key exactly once', () => {
    const keys = loadProfile('1.2.0').map(item => item.key).sort()
    expect([...loadProfile('1.0.0').map(item => item.key), ...addedIn120].sort()).toEqual(keys)
    expect([...loadProfile('1.1.0').map(item => item.key), ...addedIn120].sort()).toEqual(keys)
    const fixed = [...settingEditorCatalog.fixed.keys()]
    const editors = [...settingEditorCatalog.editors.keys()]
    expect([...fixed, ...editors].sort()).toEqual(keys)
    expect(fixed.filter(key => settingEditorCatalog.editors.has(key))).toEqual([])
  })

  // FR-050.6 AC-025.1
  it('derives controls for keys the catalog does not know', () => {
    expect(getSettingEditor(unknown({ kind: 'text', textValue: 'a' }, { allowedTextValues: ['a'] }))).toBeNull()
    expect(getSettingEditor(unknown(integer('3'), { minimum: integer('3'), maximum: integer('3') }))).toBeNull()
    expect(getSettingEditor(unknown({ kind: 'boolean', booleanValue: false }))?.kind).toBe('choices')
    expect(getSettingEditor(unknown({ kind: 'text', textValue: 'a' }, { allowedTextValues: ['a', 'b'] }))?.kind).toBe('choices')
    const range = getSettingEditor(unknown(integer('0'), { minimum: integer('0'), maximum: integer('3') }))
    expect(range?.kind === 'choices' ? range.choices.map(choice => choice.label) : null).toEqual(['0', '1', '2', '3'])
    const widest = getSettingEditor(unknown(integer('0'), { minimum: integer('1'), maximum: integer(String(maximumDerivedRangeChoices)) }))
    expect(widest?.kind === 'choices' ? widest.choices : []).toHaveLength(31)
    expect(getSettingEditor(unknown(integer('0'), { minimum: integer('1'), maximum: integer('32') }))?.kind).toBe('input')
    expect(getSettingEditor(unknown(integer('0')))?.kind).toBe('input')
    expect(getSettingEditor(unknown({ kind: 'rational', numerator: '1', denominator: '2' }))?.kind).toBe('input')
    expect(getSettingEditor(unknown({ kind: 'text', textValue: 'free' }))?.kind).toBe('input')
    expect(getSettingEditor(unknown({ kind: 'textList', textListValue: [] }))?.kind).toBe('input')
  })

  // FR-050.4 table T2
  it.each([
    ['bitsPerSecond', integer('8000000'), '8 Mbps'],
    ['bitsPerSecond', integer('128000'), '128 kbps'],
    ['seconds', integer('5'), '5 秒'],
    ['frames', integer('2'), '2 フレーム'],
    ['fullWidthCharacters', integer('16'), '全角 16 文字'],
    ['charactersPerSecond', integer('7'), '1秒に 7 文字'],
    ['loudnessUnitsFullScale', integer('-16'), '-16 LUFS'],
    ['decibelsFullScale', integer('-1'), '-1 dBFS'],
    ['decibels', integer('1'), '1 dB'],
    ['count', integer('3'), '3'],
    ['seconds', { kind: 'rational', numerator: '5', denominator: '6' }, '約0.83 秒（5/6）'],
    ['seconds', { kind: 'rational', numerator: '1', denominator: '1' }, '1 秒'],
    ['percent', { kind: 'rational', numerator: '15', denominator: '2' }, '7.5 %（15/2）'],
  ] as const)('formats %s %j as %s', (unit, value, label) => {
    expect(formatSettingChoice(unit, value)).toBe(label)
  })

  it('matches equal fractions to the listed choice', () => {
    const definition = loadProfile('1.1.0').find(item => item.key === 'subtitles.cueMinimumDuration')!
    const editor = getSettingEditor(definition)!
    expect(editor.kind === 'choices' ? findSettingChoice(editor.choices, { kind: 'rational', numerator: '10', denominator: '12' })?.id : null).toBe('5/6')
    expect(editor.kind === 'choices' ? findSettingChoice(editor.choices, { kind: 'rational', numerator: '1', denominator: '0' }) : 'x').toBeUndefined()
  })
})
