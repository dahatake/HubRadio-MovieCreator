import type { SettingDefinition } from './settingsProtocol'
import { audioAndEditingHelp, type SettingHelpEntry } from './settingHelpAudioEditing'
import { outputProjectStorageHelp } from './settingHelpOutputProject'
import { subtitlesAndThumbnailsHelp } from './settingHelpSubtitles'

export interface SettingHelp {
  readonly label: string
  readonly what: string
  readonly effect: string
  /** True when no written explanation exists for the key and generic guidance is shown. */
  readonly generic: boolean
}

export const settingHelpEntries: Readonly<Record<string, SettingHelpEntry>> = Object.freeze({
  ...audioAndEditingHelp,
  ...outputProjectStorageHelp,
  ...subtitlesAndThumbnailsHelp,
})

export const settingHelpAdvice =
  '迷ったときは、初期値のままにしておくのが安全です。値を変えても、「保存」を押すまでは反映されません。'

export function getSettingHelp(definition: Pick<SettingDefinition, 'key' | 'displayName'>): SettingHelp {
  const entry = Object.hasOwn(settingHelpEntries, definition.key) ? settingHelpEntries[definition.key] : undefined
  if (entry !== undefined) {
    return { label: entry[0], what: entry[1], effect: entry[2], generic: false }
  }

  return {
    label: definition.displayName,
    what: 'この項目の詳しい説明はまだ用意されていません。「初期値・用途」を開くと、用途（英語）と初期値を確認できます。',
    effect: '内容が分からないときは、値を変えずに初期値のままにしてください。',
    generic: true,
  }
}
