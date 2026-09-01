import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const styles = readFileSync(
  new URL('../../src/HubRadio.Web/src/styles.css', import.meta.url),
  'utf8',
)

const lightTheme = readCustomProperties(':root')
const darkTheme = readCustomProperties('html[data-theme="dark"]')

const normalTextPairs = [
  ['--cp-text', '--cp-surface'],
  ['--cp-text', '--cp-surface-soft'],
  ['--cp-text-muted', '--cp-surface'],
  ['--cp-text-muted', '--cp-surface-soft'],
  ['--cp-text-soft', '--cp-surface'],
  ['--cp-text-soft', '--cp-surface-soft'],
  ['--cp-accent', '--cp-surface'],
  ['--cp-accent', '--cp-surface-soft'],
  ['--cp-danger', '--cp-surface'],
  ['--cp-danger', '--cp-surface-soft'],
  ['--cp-link', '--cp-surface'],
  ['--cp-link', '--cp-surface-soft'],
]

const filledControlPairs = [
  ['--cp-accent-fg', '--cp-accent'],
  ['--cp-accent-fg', '--cp-accent-hover'],
  ['--cp-accent-fg', '--cp-danger'],
]

test('light and dark normal-text token pairs meet 4.5:1', () => {
  assertPairsMeetRatio('light', lightTheme, normalTextPairs, 4.5)
  assertPairsMeetRatio('dark', darkTheme, normalTextPairs, 4.5)
})

test('filled control text meets 4.5:1 in both themes', () => {
  assertPairsMeetRatio('light', lightTheme, filledControlPairs, 4.5)
  assertPairsMeetRatio('dark', darkTheme, filledControlPairs, 4.5)
})

test('the shared keyboard focus indicator is explicit and at least three pixels', () => {
  const rule = readRule(':focus-visible')
  assert.match(rule, /outline:\s*3px\s+solid\s+var\(--cp-accent\)\s*;/)
  assert.match(rule, /outline-offset:\s*3px\s*;/)
})

test('description text uses the theme-aware muted text token', () => {
  const rule = readRule('.description')
  assert.match(rule, /color:\s*var\(--cp-text-muted\)\s*;/)
  assert.doesNotMatch(rule, /color:\s*#[0-9a-f]{3,8}\s*;/i)
})

function assertPairsMeetRatio(themeName, theme, pairs, minimum) {
  for (const [foregroundName, backgroundName] of pairs) {
    const foreground = requiredColor(theme, foregroundName)
    const background = requiredColor(theme, backgroundName)
    const ratio = contrastRatio(foreground, background)
    assert.ok(
      ratio >= minimum,
      `${themeName} ${foregroundName} on ${backgroundName}: ` +
        `${ratio.toFixed(3)}:1 is below ${minimum}:1`,
    )
  }
}

function readCustomProperties(selector) {
  const rule = readRule(selector)
  return new Map(
    [...rule.matchAll(/(--[a-z0-9-]+):\s*(#[0-9a-f]{6})\s*;/gi)]
      .map((match) => [match[1], match[2].toLowerCase()]),
  )
}

function readRule(selector) {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const match = new RegExp(`${escaped}\\s*\\{([^}]*)\\}`, 'i').exec(styles)
  assert.notEqual(match, null, `Missing CSS rule for ${selector}.`)
  return match[1]
}

function requiredColor(theme, name) {
  const value = theme.get(name)
  assert.notEqual(value, undefined, `Missing color token ${name}.`)
  return value
}

function contrastRatio(first, second) {
  const firstLuminance = relativeLuminance(first)
  const secondLuminance = relativeLuminance(second)
  const lighter = Math.max(firstLuminance, secondLuminance)
  const darker = Math.min(firstLuminance, secondLuminance)
  return (lighter + 0.05) / (darker + 0.05)
}

function relativeLuminance(color) {
  const channels = color
    .slice(1)
    .match(/.{2}/g)
    .map((channel) => Number.parseInt(channel, 16) / 255)
    .map((channel) =>
      channel <= 0.04045
        ? channel / 12.92
        : ((channel + 0.055) / 1.055) ** 2.4,
    )
  return (
    0.2126 * channels[0] +
    0.7152 * channels[1] +
    0.0722 * channels[2]
  )
}
