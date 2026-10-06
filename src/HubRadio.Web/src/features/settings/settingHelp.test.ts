import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import { getSettingHelp, settingHelpAdvice, settingHelpEntries } from './settingHelp'

interface ProfileSetting {
  readonly key: string
  readonly displayName: string
}

const profilePath = resolve(
  dirname(fileURLToPath(import.meta.url)),
  '../../../../HubRadio.App/DefaultSettings/Profiles/1.1.0.json',
)
const settings = (JSON.parse(readFileSync(profilePath, 'utf8')) as { settings: ProfileSetting[] }).settings

describe('setting help', () => {
  it('has a written explanation for every setting in the bundled profile and for nothing else', () => {
    expect(settings).toHaveLength(123)
    expect(Object.keys(settingHelpEntries).sort()).toEqual(settings.map((setting) => setting.key).sort())
  })

  it.each(settings.map((setting) => [setting.key, setting] as const))('explains %s in plain Japanese', (_key, setting) => {
    const help = getSettingHelp(setting)

    expect(help.generic).toBe(false)
    expect(help.label.trim().length).toBeGreaterThan(0)
    for (const text of [help.what, help.effect]) {
      expect(text.trim()).toBe(text)
      expect(text.length).toBeGreaterThanOrEqual(20)
      expect(text).toMatch(/[\u3040-\u30ff]/)
      expect(text).toMatch(/[。]$/)
    }
  })

  it('falls back to honest generic guidance for a key without an explanation', () => {
    const help = getSettingHelp({ key: 'future.unknownSetting', displayName: 'Future setting' })

    expect(help).toMatchObject({ label: 'Future setting', generic: true })
    expect(help.what).toContain('まだ用意されていません')
    expect(getSettingHelp({ key: 'constructor', displayName: 'Inherited name' }).generic).toBe(true)
    expect(settingHelpAdvice).toContain('初期値のままにしておくのが安全')
  })

  // FR-050.8 AC-025.6
  it('points generic guidance to the disclosure without evidence', () => {
    const help = getSettingHelp({ key: 'future.unknownSetting', displayName: 'Future setting' })
    expect(help.what).toContain('「初期値・用途」を開くと')
    expect(help.what).not.toContain('根拠')
  })

  // FR-050.9 AC-025.7
  it('never asks to type values for settings that are picked from choices', () => {
    const editable = Object.keys((JSON.parse(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), 'settingEditorCatalog.json'), 'utf8')) as { editors: Record<string, unknown> }).editors)
    expect(editable).toHaveLength(22)
    for (const key of editable) {
      const help = getSettingHelp({ key, displayName: key })
      expect(help.generic).toBe(false)
      for (const text of [help.what, help.effect]) {
        expect(text).not.toContain('分子と分母')
        expect(text).not.toContain('入力します')
      }
    }
  })
})
