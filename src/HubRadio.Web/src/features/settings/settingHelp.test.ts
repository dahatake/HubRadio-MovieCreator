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
  '../../../../HubRadio.App/DefaultSettings/Profiles/1.0.0.json',
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
})
