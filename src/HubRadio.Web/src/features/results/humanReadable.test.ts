import { describe, expect, it } from 'vitest'
import {
  evaluationRuleLabel,
  evaluationStatusLabel,
  failureCodeLabel,
  formatByteSize,
  formatDuration,
  formatTimestamp,
  generationCodeLabel,
  generationRoleLabel,
  summarizeGenerationNotes,
} from './humanReadable'
import { EVALUATION_LIMITATIONS, EVALUATION_RULES } from '../workflow/pipelineProtocol'

const uuid = '61112222-3333-4444-8555-666677778888'
const looksTechnical = /[0-9a-f]{8}-[0-9a-f]{4}|[0-9a-f]{64}|\d+\/\d+|[A-Z][a-z]+[A-Z]|^[a-z]+(-[a-z]+)+$/

describe('humanReadable (FR-052)', () => {
  // FR-052 AC-027.1: rational seconds become minutes and seconds, never "2321/30".
  it('formats durations and playback positions for people', () => {
    expect(formatDuration(2321, 30)).toBe('1分17秒')
    expect(formatDuration(17, 2)).toBe('8.5秒')
    expect(formatDuration(3723, 1)).toBe('1時間2分3秒')
    expect(formatDuration('30', '1')).toBe('30秒')
    expect(formatDuration(1, 0)).toBe('不明')
    expect(formatTimestamp(2321, 30)).toBe('1:17.4')
    expect(formatTimestamp(85, 10)).toBe('0:08.5')
    expect(formatTimestamp(60, 1)).toBe('1:00')
    expect(formatTimestamp(-1, 10)).toBe('-0:00.1')
    expect(formatByteSize(512)).toBe('512 バイト')
    expect(formatByteSize(43_471_608)).toBe('43.5 MB')
  })

  // FR-052 AC-027.2: every evaluation status, rule and failure code has a Japanese label without codes.
  it('labels statuses, rules and failures in plain Japanese', () => {
    expect(evaluationStatusLabel('NOT EVALUATED')).toBe('未評価')
    expect(evaluationStatusLabel(undefined)).toBe('未評価')
    expect(evaluationStatusLabel('PASS')).toBe('問題なし')
    for (const rule of [...EVALUATION_RULES, ...EVALUATION_LIMITATIONS]) {
      expect(evaluationRuleLabel(rule)).not.toBe('その他の確認項目')
      expect(evaluationRuleLabel(rule)).not.toMatch(looksTechnical)
    }
    expect(failureCodeLabel('QualityControlFailed')).toBe('品質の確認で問題が見つかりました')
    expect(failureCodeLabel('UnknownCode')).not.toContain('UnknownCode')
  })

  // FR-052 AC-027.2: roles never reveal their embedded IDs; repeated notes are grouped with a count.
  it('hides IDs inside roles and groups repeated generation notes', () => {
    expect(generationRoleLabel(`vertical-burn-in-${uuid}`)).toBe('短尺・字幕焼き込み版')
    expect(generationRoleLabel(`thumbnail-landscape-${uuid}`)).toBe('横長サムネイル')
    expect(generationRoleLabel('Transcript')).toBe('文字起こし')
    expect(generationCodeLabel('shown-above-maximum-reading-speed')).toContain('早口')
    expect(generationCodeLabel('some-unknown-code')).not.toContain('some-unknown-code')
    const notes = summarizeGenerationNotes([
      { role: 'landscape-subtitles', code: 'missing-token-timing' },
      { role: 'landscape-subtitles', code: 'missing-token-timing' },
      { role: `vertical-subtitles-${uuid}`, code: 'NOT EVALUATED' },
    ])
    expect(notes).toHaveLength(2)
    expect(notes[0]!.count).toBe(2)
    expect(notes.map(note => note.text).join('')).not.toContain(uuid)
  })
})
