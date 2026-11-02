// @vitest-environment jsdom

import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ShortTargetNote } from './SettingsPanel'
import type { AnySettingsSnapshot, SettingDefinition } from './settingsProtocol'

const key = 'output.shortTargetDuration'

function snapshot(seconds: string, withRange: boolean): AnySettingsSnapshot {
  const definition = {
    key, group: 'projectAndOutput', isAdvanced: false, displayName: '短尺動画の目標時間', description: '', unit: 'seconds',
    defaultValue: { kind: 'integer', integerValue: '30' },
    minimum: withRange ? { kind: 'integer', integerValue: '15' } : null,
    maximum: withRange ? { kind: 'integer', integerValue: '60' } : null,
    allowedTextValues: [], evidence: [],
  } as unknown as SettingDefinition
  return { definitions: [definition], values: [{ key, value: { kind: 'integer', integerValue: seconds } }] } as unknown as AnySettingsSnapshot
}

describe('FR-010 AC-002 short target duration is shown as a product decision', () => {
  it('shows the saved value with the search range and the product-decision note', () => {
    const text = renderToStaticMarkup(<ShortTargetNote snapshot={snapshot('30.0', true)} />)
    expect(text).toMatch(/30\s*秒/)
    expect(text).toContain('探索範囲 15〜60 秒')
    expect(text).toContain('製品判断')
  })

  it('reads the value from settings rather than hard-coding 30', () => {
    expect(renderToStaticMarkup(<ShortTargetNote snapshot={snapshot('45', false)} />)).toContain('45 秒')
    expect(renderToStaticMarkup(<ShortTargetNote snapshot={snapshot('45', false)} />)).not.toMatch(/30\s*秒/)
  })

  it('renders nothing when the setting is not available', () => {
    expect(renderToStaticMarkup(<ShortTargetNote snapshot={{ definitions: [], values: [] } as unknown as AnySettingsSnapshot} />)).toBe('')
  })
})
