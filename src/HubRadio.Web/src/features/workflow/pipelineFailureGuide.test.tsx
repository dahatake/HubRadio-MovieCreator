// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { describePipelineFailure } from './pipelineFailureGuide'
import { PipelineFailureDetails } from './pipelinePresentation'
import type { PipelinePhase } from './pipelineProtocol'

// FR-047.3 AC-022.3: every failure code the host can report gets a non-empty title,
// cause and at least one remedy; known codes get their own explanation.
describe('describePipelineFailure', () => {
  const hostCodes = [
    'source_only.unsupported_source_timing', 'source_only.source_frame_rate_unavailable', 'source_only.source_geometry_unavailable',
    'source_only.audio_stream_absent', 'source_only.decode_failed', 'source_only.unsupported_source_type',
    'source_only.generation_failed', 'source_only.execution_failed', 'source_only.state_persistence_failed',
    'source_only.source_unavailable', 'source_only.source_changed', 'source_only.path_unavailable', 'source_only.locator_invalid',
    'source_only.runtime_verification_failed', 'source_only.model_directory_mismatch', 'source_only.required_setting_missing_or_invalid',
    'source_only.settings_mismatch', 'source_only.loudness_outside_frozen_tolerance', 'source_only.true_peak_above_frozen_limit',
    'source_only.subtitle_mapping_limit', 'source_only.subtitle_source_mapping_unsupported', 'source_only.analysis_checkpoint_invalid',
    'source_only.version_conflict', 'source_only.generated_version_mismatch', 'job.app_restarted', 'job.execution_timed_out',
    'task.timeout', 'task.invalid_result', 'asr.processing_failed', 'source_only.no_speech_detected', 'source_only.no_speech_recognized',
  ]
  const fallbackTitle = describePipelineFailure('source_only.not_a_real_code_xyz', 'Failed')!.title

  it.each(hostCodes)('explains %s with a cause and remedies', code => {
    const guide = describePipelineFailure(code, 'Failed')
    expect(guide).not.toBeNull()
    expect(guide!.title.length).toBeGreaterThan(0)
    expect(guide!.cause.length).toBeGreaterThan(0)
    expect(guide!.remedies.length).toBeGreaterThan(0)
    expect(guide!.remedies.every(item => item.length > 0)).toBe(true)
    expect(guide!.title).not.toBe(fallbackTitle)
  })

  it('names the timing reasons and the re-export remedy for unsupported source timing', () => {
    const guide = describePipelineFailure('source_only.unsupported_source_timing', 'Failed')!
    expect(guide.title).toBe('元動画の映像と音声の時間情報を扱えません')
    expect(guide.cause).toContain('可変フレームレート')
    expect(guide.cause).toContain('0.25秒')
    expect(guide.remedies.join('')).toContain('固定フレームレート')
    expect(guide.remedies.join('')).toContain('新しい案件を作成')
  })

  it('falls back to a generic explanation for an unknown code', () => {
    const guide = describePipelineFailure('source_only.not_a_real_code_xyz', 'Failed')!
    expect(guide.title).toBe('自動編集を完了できませんでした')
    expect(guide.remedies.join('')).toContain('自動編集を再試行')
  })

  it.each<[string | null, PipelinePhase, string | null]>([
    [null, 'Analyzing', null],
    [null, 'Completed', null],
    [null, 'Canceled', null],
    ['source_only.execution_failed', 'Canceled', null],
    [null, 'Failed', '自動編集を完了できませんでした'],
    [null, 'ReselectionRequired', '元動画の選び直しが必要です'],
    ['source_only.reconciliation_required', 'ReconciliationRequired', '保存状態の確認が必要です'],
    ['source_only.source_changed', 'ReselectionRequired', '元動画のファイルを読み込めません'],
  ])('code %s in phase %s is titled %s', (code, phase, title) => {
    expect(describePipelineFailure(code, phase)?.title ?? null).toBe(title)
  })
})

// FR-047.3 AC-022.4: the job panel shows the explanation, the remedies and the code.
describe('PipelineFailureDetails', () => {
  let root: Root
  let container: HTMLDivElement
  beforeEach(() => {
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    container = document.createElement('div'); document.body.append(container)
    root = createRoot(container)
  })
  afterEach(async () => {
    await act(async () => root.unmount())
    container.remove(); vi.unstubAllGlobals()
  })

  it('renders title, cause, ordered remedies and the stable code as an alert', async () => {
    await act(async () => root.render(<PipelineFailureDetails code="source_only.unsupported_source_timing" phase="Failed" />))
    const alert = container.querySelector('[role="alert"]')!
    expect(alert).not.toBeNull()
    expect(alert.querySelector('h3')!.textContent).toBe('エラーの内容：元動画の映像と音声の時間情報を扱えません')
    const terms = Array.from(alert.querySelectorAll('dt')).map(item => item.textContent)
    expect(terms).toEqual(['原因', '対応策', 'エラーコード'])
    expect(alert.querySelectorAll('ol > li').length).toBeGreaterThanOrEqual(2)
    expect(alert.querySelector('code')!.textContent).toBe('source_only.unsupported_source_timing')
  })

  it('renders nothing while the run has not failed', async () => {
    await act(async () => root.render(<PipelineFailureDetails code={null} phase="Rendering" />))
    expect(container.innerHTML).toBe('')
  })
})
