// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  ACTIVITY_SLOW_AFTER_SECONDS,
  ProjectActivityNotice,
  describeProjectActivity,
} from './ProjectActivityNotice'

const actEnvironment = globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
let container: HTMLDivElement
let root: Root

beforeEach(() => {
  vi.useFakeTimers()
  actEnvironment.IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  vi.useRealTimers()
  delete actEnvironment.IS_REACT_ACT_ENVIRONMENT
})

describe('ProjectActivityNotice', () => {
  // FR-039.1 FR-039.2 AC-012.1
  it('shows what the app is doing, the user action and the next step while starting editing', () => {
    act(() => root.render(<ProjectActivityNotice method="project.finalizeDraft" />))

    const section = container.querySelector('section.operation-activity')!
    expect(section.getAttribute('aria-labelledby')).toBe('operation-activity-title')
    expect(section.querySelector('h3')!.textContent).toBe('いま行っていること：編集の受付')
    expect(section.textContent).toContain('既定設定の固定')
    expect(section.textContent).toContain('あなたの操作は不要です')
    // FR-056 AC-031.2: the processing step is shown as 「ジョブの状態の確認」.
    expect(section.textContent).toContain('「ジョブの状態の確認」画面で処理状況を確認できます')
    expect(section.textContent).toContain('経過時間：0 秒')
    expect(section.querySelector('button, input, a')).toBeNull()
  })

  // FR-039.3 AC-012.2
  it('counts elapsed seconds and adds a slow-wait notice only after the threshold', () => {
    act(() => root.render(<ProjectActivityNotice method="project.finalizeDraft" />))

    act(() => vi.advanceTimersByTime(5000))
    expect(container.textContent).toContain('経過時間：5 秒')
    expect(container.querySelector('.operation-activity-slow')).toBeNull()

    act(() => vi.advanceTimersByTime((ACTIVITY_SLOW_AFTER_SECONDS - 5) * 1000))
    expect(container.textContent).toContain(`経過時間：${ACTIVITY_SLOW_AFTER_SECONDS} 秒`)
    const slow = container.querySelector('.operation-activity-slow')!
    expect(slow.textContent).toContain('秒以上かかっています')
    expect(slow.textContent).toContain('troubleshooting.md')
  })

  // FR-039.4 AC-012.3
  it('describes the other pending operations and never claims a percentage', () => {
    for (const method of ['project.pickSource', 'project.saveDraft'] as const) {
      expect(describeProjectActivity(method)).not.toBeNull()
    }
    expect(describeProjectActivity('project.list')).toBeNull()

    act(() => root.render(<ProjectActivityNotice method="project.pickSource" />))
    expect(container.querySelector('h3')!.textContent).toBe('いま行っていること：元動画の選択')
    expect(container.querySelector('.operation-activity-slow')).toBeNull()
    act(() => vi.advanceTimersByTime(60_000))
    expect(container.querySelector('.operation-activity-slow')).toBeNull()

    act(() => root.render(<ProjectActivityNotice method="project.list" />))
    expect(container.querySelector('.operation-activity')).toBeNull()
    expect(container.textContent).not.toMatch(/\d+\s*%|生成中|生成済み/)
  })
})
